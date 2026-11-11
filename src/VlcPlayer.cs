using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;

namespace Velivo
{
    // Odtwarzacz filmow jak VLC (dodatek wybierany w instalatorze): AVI, MKV, WMV, FLV, MPG, TS i inne kodeki przez biblioteke VLC
    // (libvlc w folderze programu vlc\ - pobierana przez instalator z oficjalnej paczki VideoLAN). Bez dodatku Velivo dziala jak dotad
    // (filmy MP4/WebM w karcie). Przycisk 🎬 obok 🏦: klik = biblioteka filmow, prawy przycisk = otworz plik, dodaj do biblioteki,
    // odinstaluj dodatek. Sterowanie jak w VLC: pasek czasu, pauza, stop, poprzedni/nastepny, ±10 s, predkosc, napisy, sciezka
    // dzwieku, proporcje obrazu, zrzut klatki, glosnosc do 125%, pelny ekran. Biblioteka i miejsca wznowienia - lokalnie (filmy.json).
    public partial class MainWindow
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
        // 🌙 monitor: false = zgas (SC_MONITORPOWER 2, PostMessage - nie czeka na zawieszone okna), true = obudz (drobny ruch myszy)
        static void ScreenPower(bool on)
        {
            try
            {
                if (on) { mouse_event(1, 1, 0, 0, UIntPtr.Zero); mouse_event(1, -1, 0, 0, UIntPtr.Zero); }
                else PostMessage((IntPtr)0xFFFF, 0x0112, (IntPtr)0xF170, (IntPtr)2);
            }
            catch (Exception ex) { App.LogError(ex); }
        }
        // formaty, ktore dochodza z dodatkiem (MP4/WebM/MKV/MOV/OGV obsluguje tez zwykly odtwarzacz w karcie)
        internal static readonly string[] VlcExtraExts = { ".avi", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp", ".vob", ".divx", ".asf", ".rm", ".rmvb", ".f4v", ".m2v" };
        static readonly string[] SubtitleExts = { ".srt", ".ass", ".ssa", ".sub", ".vtt", ".smi" };

        static string VlcDir { get { return Path.Combine(AppContext.BaseDirectory, "vlc"); } }
        static string VlcRemoveFlag { get { return Path.Combine(DataDir, "odtwarzacz-usun.flag"); } }
        static string LibraryFile { get { return Path.Combine(DataDir, "filmy.json"); } }

        // dodatek zainstalowany (i nie czeka na usuniecie)
        internal static bool VlcInstalled { get { return File.Exists(Path.Combine(VlcDir, "libvlc.dll")) && !File.Exists(VlcRemoveFlag); } }

        internal static bool IsVlcVideoFile(string path)
        {
            var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return VideoExts.Contains(ext) || VlcExtraExts.Contains(ext);
        }

        LibVLC _libVlc;
        Window _vlcWin;
        Button _filmBtn, _playerBtn;
        bool _hiddenForPlayer;
        Func<IptvChannel, bool> _pipShow;
        Func<string, bool> _pipShowFilm;   // film w podgladzie PiP otwartego odtwarzacza (z biblioteki)   // podglad PiP w otwartym odtwarzaczu (gdy gra TV) - takze z okna telewizji   // przegladarka schowana do zasobnika przez przycisk odtwarzacza - wraca po zamknieciu odtwarzacza

        // sam odtwarzacz (jak typowy program do filmow): czarny ekran, przegladarka do zasobnika
        void OpenEmptyPlayer()
        {
            if (!EnsureLibVlc()) return;
            if (_vlcWin == null) { _playlist = new List<string>(); _playIndex = 0; BuildVlcWindow(); }
            else { if (_vlcWin.WindowState == WindowState.Minimized) _vlcWin.WindowState = WindowState.Normal; _vlcWin.Activate(); }
            if (IsVisible) { EnsureTray(); Hide(); _inTray = true; _hiddenForPlayer = true; }
        }

        // kanal wlaczony spoza okna TV (np. "Ostatnio ogladane"): gra w swojej grupie - lista kanalow i PgUp/PgDn po tej grupie, nie jako kolejka
        void PlayChannelInGroup(IptvChannel ch, IptvPlaylist pl)
        {
            var grp = pl?.Channels.Where(c => c.Group == ch.Group).ToList() ?? new List<IptvChannel>();
            var how = GetPref("sort-lista-kanalow") ?? "az";   // ta sama kolejnosc, co na liscie kanalow w odtwarzaczu
            if (how == "az") grp = grp.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            else if (how == "za") grp = grp.OrderByDescending(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            int at = grp.FindIndex(c => c.Url == ch.Url);
            if (at < 0) { grp = new List<IptvChannel> { ch }; at = 0; }
            foreach (var c in grp) _iptvPlaying[c.Url] = c;
            PlayVideos(grp.Select(c => c.Url).ToArray(), at);
        }

        // archiwum TV: audycja z przeszlosci (albo trwajaca - od poczatku) jak film: pauza, przewijanie; kanal zostaje kanalem (logo, program)
        internal static IptvChannel LiveOf(IptvChannel c)
        {
            if (c?.LiveUrl == null) return c;
            var l = new IptvChannel { Name = c.Name, Url = c.LiveUrl, Group = c.Group, Logo = c.Logo, TvgId = c.TvgId, Catchup = c.Catchup, CatchupSource = c.CatchupSource, CatchupDays = c.CatchupDays };
            l.Opts.AddRange(c.Opts); return l;
        }

        // serwer kanalu - dzialajaca odmiana adresu archiwum zapamietana dla niego
        static string ArchiveHost(string url) { try { return new Uri(url).Authority.ToLowerInvariant(); } catch (Exception) { return ""; } }

        internal void PlayArchive(IptvChannel ch, EpgItem p, int variant = -1)
        {
            var live = LiveOf(ch);
            if (variant < 0) variant = IptvSt.CatchupVariant != null && IptvSt.CatchupVariant.TryGetValue(ArchiveHost(live.Url), out var kv) ? kv : 0;
            var url = CatchupUrl(live, p.Start, p.Stop, DateTime.Now, variant);
            if (url == null && variant > 0) { variant = 0; url = CatchupUrl(live, p.Start, p.Stop, DateTime.Now, 0); }
            if (url == null) return;
            var a = new IptvChannel { Name = live.Name, Url = url, Group = live.Group, Logo = live.Logo, TvgId = live.TvgId, Catchup = live.Catchup, CatchupSource = live.CatchupSource, CatchupDays = live.CatchupDays, LiveUrl = live.Url, ArchiveStart = p.Start, ArchiveStop = p.Stop, ArchiveVariant = variant,
                OrigName = live.OrigName, OrigLogo = live.OrigLogo };
            a.Opts.AddRange(live.Opts);
            _iptvPlaying[url] = a;
            PlayVideos(new[] { url }, 0);
        }

        // 📺 na gornym pasku: od razu ostatni kanal w odtwarzaczu (w swojej grupie), przegladarka do zasobnika - jak ▶;
        // okno listy kanalow tylko przy pierwszym uruchomieniu (brak listy / nic jeszcze nie ogladane)
        void StartTv()
        {
            var pl = KnownPlaylist();
            var last = pl == null ? null : IptvSt.Recent.Select(u => pl.Channels.FirstOrDefault(c => c.Url == u)).FirstOrDefault(c => c != null);
            if (last == null || !EnsureLibVlc()) { ShowIptv(this); return; }
            var cur = _vlcWin != null && _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
            if (cur == null || !_iptvPlaying.ContainsKey(cur)) PlayChannelInGroup(last, pl);   // juz leci TV - tylko na wierzch
            else { if (_vlcWin.WindowState == WindowState.Minimized) _vlcWin.WindowState = WindowState.Normal; _vlcWin.Activate(); }
            if (IsVisible) { EnsureTray(); Hide(); _inTray = true; _hiddenForPlayer = true; }
        }

        // nazwa kodeka z kodu VLC (fourcc) - czytelna, jak w MediaInfo
        internal static string CodecName(uint fourcc)
        {
            var c = new string(BitConverter.GetBytes(fourcc).Select(b => (char)b).ToArray()).Trim('\0', ' ').ToLowerInvariant();
            switch (c)
            {
                case "h264": case "avc1": return "H.264"; case "hevc": case "h265": case "hvc1": return "H.265";
                case "av01": return "AV1"; case "vp90": case "vp09": return "VP9"; case "vp80": return "VP8"; case "mpgv": case "mp2v": return "MPEG-2"; case "mp4v": case "xvid": case "divx": case "dx50": return "MPEG-4";
                case "a52": return "AC3"; case "eac3": return "E-AC3"; case "mp4a": return "AAC"; case "mpga": case "mp3": return "MP3"; case "dts": return "DTS";
                case "opus": return "Opus"; case "vorb": return "Vorbis"; case "flac": return "FLAC"; case "truehd": case "mlp": return "TrueHD"; case "araw": case "s16l": return "PCM";
                default: return c.ToUpperInvariant();
            }
        }
        internal static string ChannelsName(uint ch) { return ch == 1 ? "mono" : ch == 2 ? "stereo" : ch == 6 ? "5.1" : ch == 8 ? "7.1" : ch + "ch"; }

        // ---------- biblioteka ----------

        internal sealed class LibraryVideo
        {
            public string Path { get; set; }
            public string Title { get; set; }
            public long Added { get; set; }
            public long Last { get; set; }   // ostatnie odtwarzanie (unix ms), 0 = nieogladany
            public long Pos { get; set; }    // miejsce wznowienia (ms)
            public long Len { get; set; }    // dlugosc (ms), znana po pierwszym odtworzeniu
            public bool Done { get; set; }   // obejrzany do konca
            public string Spu { get; set; }    // wybrane napisy (nazwa sciezki, "-" = wylaczone)
            public string Audio { get; set; }  // wybrana sciezka dzwieku (nazwa)
            public string MyTitle { get; set; }
            public long? SubDelay { get; set; }  // przesuniecie napisow tego filmu (ms; + = napisy pozniej)  // wlasny tytul (zmiana nazwy w Velivo; plik i dane z Jellyfin bez zmian)
            // ustawienia tego filmu (filmy maja rozna glosnosc): glosnosc filmu i lektor; null = jak ostatnio
            public int? Vol { get; set; }
            public double? LVol { get; set; }
            public double? LRate { get; set; }
            public int? LDuck { get; set; }
            public string LVoice { get; set; }
            public int? LSteps { get; set; }   // jakosc glosu Supertonic lektora tego filmu
            public string Peer { get; set; }       // kopia filmu z innego komputera (dysk tamtego komputera): jego id - gra przez siec
            public string RemoteId { get; set; }   // id filmu w bibliotece tamtego komputera
            public bool? Shared { get; set; }  // we wspolnej bibliotece; null = film z dysku sieciowego (NAS) wspolny, z dysku tego komputera prywatny
        }

        internal static bool IsShared(LibraryVideo v) { return v.Shared ?? NetworkPath(v.Path) != null; }

        // wpis filmu w danej bibliotece (ten sam plik moze byc we wspolnej i prywatnej - kazda z wlasnym licznikiem); shared null = dowolna
        internal static LibraryVideo FindEntry(List<LibraryVideo> lib, string path, bool? shared)
        {
            var all = lib.Where(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
            return all.FirstOrDefault(x => shared == null || IsShared(x) == shared) ?? all.FirstOrDefault();
        }

        internal static List<LibraryVideo> ParseLibrary(string json)
        {
            try { return (JsonSerializer.Deserialize<List<LibraryVideo>>(json ?? "") ?? new List<LibraryVideo>()).Where(v => v != null && !string.IsNullOrEmpty(v.Path)).ToList(); }
            catch (Exception) { return new List<LibraryVideo>(); }
        }

        // odczyt i zapis biblioteki po kolei (wątek okna i zapis w tle): odczyt w trakcie zapisu widział pół pliku - pusta lista zapisana z powrotem skasowałaby bibliotekę
        static readonly object LibraryLock = new object();
        List<LibraryVideo> LoadLibrary()
        {
            // plik zapisywany wlasnie przez inny program (np. synchronizacja) - ponow odczyt
            lock (LibraryLock)
                for (int i = 0; ; i++)
                {
                    try { return File.Exists(LibraryFile) ? ParseLibrary(File.ReadAllText(LibraryFile)) : new List<LibraryVideo>(); }
                    catch (IOException) when (i < 40) { System.Threading.Thread.Sleep(25); }
                }
        }

        void SaveLibrary(List<LibraryVideo> lib)
        {
            lock (LibraryLock)
                try { Directory.CreateDirectory(DataDir); VelivoData.SafeWrite(LibraryFile, JsonSerializer.Serialize(lib, new JsonSerializerOptions { WriteIndented = true })); }
                catch (IOException ex) { App.LogError(ex); }
        }

        // ⏱ miejsce w filmie zapisywane w tle, po kolei (kolejność zapisów zachowana - starsze miejsce nie nadpisze nowszego); odtwarzacz nie stoi przy zapisie
        readonly System.Collections.Concurrent.BlockingCollection<Action> _libQ = new System.Collections.Concurrent.BlockingCollection<Action>();
        System.Threading.Thread _libThread;
        void LibWrite(Action job, bool wait = false)
        {
            if (_libThread == null)
            {
                _libThread = new System.Threading.Thread(() => { foreach (var a in _libQ.GetConsumingEnumerable()) try { a(); } catch (Exception ex) { App.LogError(ex); } }) { IsBackground = true, Name = "Velivo biblioteka" };
                _libThread.Start();
            }
            if (!wait) { _libQ.Add(job); return; }
            using var done = new System.Threading.ManualResetEventSlim();   // zamykanie Velivo - czekamy (do 5 s), żeby postęp nie zginął razem z procesem
            _libQ.Add(() => { try { job(); } finally { done.Set(); } });
            done.Wait(5000);
        }
        void SaveProgress(string path, bool shared, long t, long len, bool wait = false) => LibWrite(() =>
        {
            lock (LibraryLock)
            {
                var l = LoadLibrary(); var v = FindEntry(l, path, shared); if (v == null) return;
                RecordProgress(v, t, len, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                SaveLibrary(l);
            }
        }, wait);

        // dodaje pliki (i filmy z podanych folderow, z podfolderami); zwraca liczbe nowych
        internal static int AddToLibrary(List<LibraryVideo> lib, IEnumerable<string> paths, bool? shared = null)
        {
            int added = 0;
            var known = new HashSet<string>(lib.Select(v => (IsShared(v) ? "S|" : "P|") + v.Path), StringComparer.OrdinalIgnoreCase);   // ten sam plik - raz w kazdej bibliotece
            foreach (var p in paths)
            {
                IEnumerable<string> files;
                try { files = Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(IsVlcVideoFile) : new[] { p }; }
                catch (Exception) { continue; }
                foreach (var f in files)
                {
                    if (!IsVlcVideoFile(f)) continue;
                    var full = System.IO.Path.GetFullPath(f);
                    if (!known.Add(((shared ?? NetworkPath(full) != null) ? "S|" : "P|") + full)) continue;
                    lib.Add(new LibraryVideo { Path = full, Title = System.IO.Path.GetFileNameWithoutExtension(full), Added = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Shared = shared });
                    added++;
                }
            }
            return added;
        }

        // wybor sciezki napisow / dzwieku wg zapamietanej nazwy: dokladnie ta sama, inaczej ten sam jezyk ([Polski], [English]);
        // "-" = wylaczone (-1); int.MinValue = brak pasujacej (zostaje domyslna filmu)
        // sameFilm = false: wybor z innego filmu - tylko jezyk ([Polski]); nazwy bez jezyka ("Track 2") w innym filmie to inna sciezka
        internal static int PickTrack(IEnumerable<(int Id, string Name)> tracks, string wanted, bool sameFilm = true)
        {
            if (string.IsNullOrEmpty(wanted)) return int.MinValue;
            if (wanted == "-") return -1;
            var list = tracks.Where(t => t.Id >= 0).ToList();
            var exact = list.FirstOrDefault(t => t.Name == wanted);
            if (sameFilm && exact.Name != null) return exact.Id;
            Func<string, string> langOf = n => { var m = System.Text.RegularExpressions.Regex.Match(n ?? "", @"\[([^\]]+)\]"); return (m.Success ? m.Groups[1].Value : sameFilm ? n ?? "" : "").Trim().ToLowerInvariant(); };
            var lang = langOf(wanted);
            if (lang.Length == 0) return int.MinValue;
            var same = list.FirstOrDefault(t => langOf(t.Name) == lang);
            return same.Name != null ? same.Id : int.MinValue;
        }

        // zapis postepu ogladania: koncowka filmu (ostatnie 15 s) = obejrzany; inaczej miejsce, w ktorym przerwano
        internal static void RecordProgress(LibraryVideo v, long time, long len, long nowMs)
        {
            v.Last = nowMs;
            if (len > 0) v.Len = len;
            if (v.Len > 0 && time >= v.Len - 15000) { v.Done = true; v.Pos = 0; }
            else if (time >= 10000) { v.Done = false; v.Pos = time; }
            else if (!v.Done) v.Pos = time;   // zaledwie zaczety (ponowne ogladanie obejrzanego nie kasuje znacznika)
        }

        // kolumna "Obejrzane": ✓ / procent / pusto
        internal static double SeenFraction(LibraryVideo v) { return v.Done ? 1.0 : v.Len > 0 ? Math.Min(1.0, (double)v.Pos / v.Len) : 0.0; }
        internal static string SeenText(LibraryVideo v)
        {
            if (v.Done) return "✓";
            int pct = (int)(100 * SeenFraction(v));
            return pct >= 1 ? pct + "%" : "";
        }

        // miejsce wznowienia: od poczatku, gdy obejrzane prawie do konca albo zaledwie zaczete
        // ten sam film w obu bibliotekach: miejsce wznowienia z wpisu ogladanego najpozniej (np. z kolejki), licznik zostaje w swojej bibliotece
        internal static void TakeNewestProgress(List<LibraryVideo> lib, LibraryVideo cur)
        {
            if (cur == null) return;
            var n = lib.Where(x => x != cur && string.Equals(x.Path, cur.Path, StringComparison.OrdinalIgnoreCase) && x.Last > cur.Last).OrderByDescending(x => x.Last).FirstOrDefault();
            if (n == null) return;
            cur.Pos = n.Pos; cur.Done = n.Done; if (n.Len > 0) cur.Len = n.Len;
        }

        internal static long ResumeAt(long pos, long len)
        {
            if (pos < 10000) return 0;
            if (len > 0 && pos > len - 15000) return 0;
            return pos;
        }

        internal static string FormatTime(long ms)
        {
            if (ms < 0) ms = 0;
            var t = TimeSpan.FromMilliseconds(ms);
            return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") : t.Minutes + ":" + t.Seconds.ToString("00");
        }

        // ---------- przycisk 🎬 i menu ----------

        void InitVlcFeature()
        {
            // odinstalowanie zlecone, gdy biblioteka byla w uzyciu - teraz (przed wczytaniem) mozna usunac
            if (File.Exists(VlcRemoveFlag))
            {
                try { if (Directory.Exists(VlcDir)) Directory.Delete(VlcDir, true); File.Delete(VlcRemoveFlag); }
                catch (Exception ex) { App.LogError(ex); }
            }
            if (!VlcInstalled) return;
            _filmBtn = new Button
            {
                Content = "🎬", FontSize = 16, Width = 40, Height = 36, Margin = new Thickness(2, 3, 2, 0),
                ToolTip = L.T("Filmy – biblioteka i odtwarzacz (jak VLC)\nPrawy klik: otwórz plik, dodaj do biblioteki")
            };
            DockPanel.SetDock(_filmBtn, Dock.Right);
            _filmBtn.Click += (s, e) => ShowVideoLibrary();
            var menu = new ContextMenu();
            // na gorze: kontynuuj ostatnio ogladany film (od miejsca przerwania)
            var cont = new MenuItem { Icon = MenuGlyph("\uE768"), FontWeight = FontWeights.SemiBold };
            cont.Click += (s, e) => ContinueLastFilm();
            menu.Items.Add(cont);
            // szybki dostep: moja kolejka i zapisane playlisty - jednym klikiem, bez otwierania biblioteki
            var plQuick = new MenuItem { Header = L.T("🎞 Playlisty i moja kolejka"), Icon = MenuGlyph("\uE8FD") };
            menu.Items.Add(plQuick);
            menu.Opened += (s, e) =>
            {
                plQuick.Items.Clear(); var pls = LoadPlaylists();
                Action<string, List<string>> one = (t, paths) => { var mi = new MenuItem { Header = t, IsEnabled = paths.Count > 0 }; mi.Click += (q, w) => PlayVideos(paths.ToArray(), 0); plQuick.Items.Add(mi); };
                if (pls.Mine.Count > 0) one(L.T("📋 Moja kolejka (") + pls.Mine.Count + ")", pls.Mine);
                foreach (var n in pls.Named.Keys.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)) one("🎞 " + n + " (" + pls.Named[n].Count + ")", pls.Named[n]);
                plQuick.Visibility = plQuick.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            menu.Opened += (s, e) => { var v = LastFilm(); cont.Visibility = v != null ? Visibility.Visible : Visibility.Collapsed; if (v != null) cont.Header = L.T("▶ Kontynuuj: ") + (MetaTitle(MetaFor(v.Path)) ?? CleanTitle(v.Title)); };
            Action<string, string, Action> item = (glyph, text, act) =>
            {
                var mi = new MenuItem { Header = text, Icon = MenuGlyph(glyph) };
                mi.Click += (s, e) => act();
                menu.Items.Add(mi);
            };
            item("\uE8B3", L.T("Biblioteka filmów"), ShowVideoLibrary);
            item("\uE823", L.T("⏰ Zaplanuj włączenie filmu / kanału…"), () => ShowScheduleStart(this));
            if (IptvInstalled) item("\uE7F4", L.T("📺 Nagrane z TV"), () => { _libTvView = true; _libSharedView = false; if (_libWin != null && _libWin.IsLoaded) _libWin.Close(); ShowVideoLibrary(); });
            item("\uE8E5", L.T("Otwórz film z dysku…"), () => { var f = PickVideoFiles(false); if (f != null) PlayVideos(f, 0); });
            menu.Items.Add(new Separator());
            item("\uE710", L.T("Dodaj filmy do biblioteki…"), () => { var f = PickVideoFiles(true); if (f != null) AddPathsToLibrary(f); });
            item("\uE8F4", L.T("Dodaj folder z filmami do biblioteki…"), () => { var d = PickFolder(); if (d != null) AddPathsToLibrary(new[] { d }); });
            menu.Items.Add(new Separator());
            item("\uE74D", L.T("Odinstaluj odtwarzacz filmów…"), () => UninstallVlc());
            menu.Items.Add(PhoneMediaMenu());
            _filmBtn.ContextMenu = menu;
            StartIptvScheduler();   // zaplanowane wlaczenie filmu dziala takze bez modulu TV
            AutoSyncJellyfin();   // informacje o filmach z Jellyfin - uzupelnienie lokalnej bazy w tle (gdy ustawione)
            int bank = TabBarPanel.Children.IndexOf(_bankBtn);
            TabBarPanel.Children.Insert(bank >= 0 ? bank + 1 : 1, _filmBtn);   // dock z prawej: nastepny w kolejnosci stoi po lewej od 🏦
            // ▶ sam odtwarzacz - pusty, czarny ekran; przegladarka chowa sie do zasobnika
            _playerBtn = new Button { Content = "▶", FontSize = 16, Width = 40, Height = 36, Margin = new Thickness(2, 3, 2, 0), ToolTip = L.T("Odtwarzacz – otwórz pusty odtwarzacz (przeglądarka schowa się do zasobnika i wróci po zamknięciu odtwarzacza)") };
            DockPanel.SetDock(_playerBtn, Dock.Right);
            _playerBtn.Click += (s, e) => OpenEmptyPlayer();
            TabBarPanel.Children.Insert(TabBarPanel.Children.IndexOf(_filmBtn) + 1, _playerBtn);
        }

        // okno wyboru zawsze na samej gorze: na ten czas okna "zawsze na wierzchu" (odtwarzacz, biblioteka) nie zaslaniaja go
        static bool? ShowAboveAll(Func<bool?> show)
        {
            var top = Application.Current.Windows.Cast<Window>().Where(w => w.Topmost).ToList();
            foreach (var w in top) w.Topmost = false;
            try { return show(); }
            finally { foreach (var w in top) w.Topmost = true; }
        }

        string _lastVideoDir;   // ostatnio wybrany folder - kolejne okno wyboru otwiera sie przy nim, nie w jego srodku

        // owner: okno, nad ktorym ma byc okno wyboru (z biblioteki - nad biblioteka)
        string[] PickVideoFiles(bool multi, Window owner = null)
        {
            var exts = string.Join(";", VideoExts.Concat(VlcExtraExts).Select(x => "*" + x));
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("Wybierz film"), Multiselect = multi, Filter = L.T("Filmy") + "|" + exts + "|" + L.T("Wszystkie pliki") + "|*.*" };
            if (_lastVideoDir != null) dlg.InitialDirectory = _lastVideoDir;
            if (ShowAboveAll(() => dlg.ShowDialog(owner ?? this)) != true) return null;
            _lastVideoDir = System.IO.Path.GetDirectoryName(dlg.FileName);
            return dlg.FileNames;
        }

        string PickFolder(Window owner = null)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Wybierz folder z filmami") };
            var up = _lastVideoDir != null ? System.IO.Path.GetDirectoryName(_lastVideoDir) : null;
            if (up != null) dlg.InitialDirectory = up;   // widac ostatni folder (np. D:\Filmy na D:\), a nie jego podfoldery
            if (ShowAboveAll(() => dlg.ShowDialog(owner ?? this)) != true) return null;
            _lastVideoDir = dlg.FolderName;
            return dlg.FolderName;
        }

        // skanowanie w tle - folder na dysku sieciowym (NAS, \\serwer\Filmy, Z:) moze sie czytac dlugo
        // shared: dodane w oknie wspolnej / prywatnej biblioteki; null = wg dysku (NAS - wspolna)
        async void AddPathsToLibrary(IEnumerable<string> paths, bool? shared = null)
        {
            var list = paths.ToList();
            ShowToast(L.T("🎬 Szukam filmów…"), null);
            var found = await System.Threading.Tasks.Task.Run(() => { var tmp = new List<LibraryVideo>(); AddToLibrary(tmp, list); return tmp; });
            var lib = LoadLibrary();
            int n = AddToLibrary(lib, found.Select(v => v.Path), shared);
            SaveLibrary(lib);
            ShowToast("🎬 " + (L.En ? "Added to library: " : "Dodano do biblioteki: ") + n, null);
            if (_libWin != null && _libWin.IsLoaded) _libRefresh?.Invoke();
        }

        void UninstallVlc(bool ask = true)
        {
            if (ask && !AskYes(this, L.T("Odinstalować odtwarzacz filmów (bibliotekę VLC)?\n\nFilmy MP4 i WebM dalej otworzysz w karcie Velivo. Twoja lista filmów zostaje – wróci po ponownej instalacji dodatku."))) return;
            if (_vlcWin != null) _vlcWin.Close();
            if (_libWin != null) _libWin.Close();
            bool loaded = _libVlc != null;
            if (_libVlc != null) { _libVlc.Dispose(); _libVlc = null; }
            try
            {
                if (loaded) throw new IOException("in use");   // biblioteka zaladowana w procesie - Windows nie pozwoli usunac plikow
                Directory.Delete(VlcDir, true);
                ShowToast(L.T("🎬 Odtwarzacz filmów odinstalowany"), null);
            }
            catch (Exception)
            {
                try { Directory.CreateDirectory(DataDir); File.WriteAllText(VlcRemoveFlag, "1"); } catch (Exception) { }
                ShowToast(L.T("🎬 Odtwarzacz filmów zostanie usunięty przy następnym uruchomieniu Velivo"), null);
            }
            if (_filmBtn != null) { TabBarPanel.Children.Remove(_filmBtn); _filmBtn = null; }
            EnsureFileTypes();   // AVI, WMV i inne znikaja z "Otworz za pomoca"
        }

        // pliki filmow do Kosza i z obu bibliotek; zwraca nazwy plikow, ktorych nie udalo sie usunac
        List<string> DeleteVideoFiles(IEnumerable<string> paths)
        {
            var errs = new List<string>(); var gone = new List<string>();
            foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(p)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    gone.Add(p);
                }
                catch (Exception ex) { App.LogError(ex); errs.Add(System.IO.Path.GetFileName(p)); }
            }
            var l = LoadLibrary();
            if (l.RemoveAll(v => gone.Contains(v.Path, StringComparer.OrdinalIgnoreCase)) > 0) SaveLibrary(l);
            _libRefresh?.Invoke();
            return errs;
        }

        // ---------- okno biblioteki ----------

        Window _libWin;
        Action _libRefresh;

        // Odswiezenie z Jellyfin (przycisk 🔄 i samo co interwal): dane filmow, nowe filmy do biblioteki, raport niedostepnych;
        // askOwner != null - od razu pytanie o filmy usuniete z Jellyfin. Zwraca tekst dymka.
        async System.Threading.Tasks.Task<string> JellyfinRefreshAsync(JellyfinConf jc, bool shared, bool tvView, Window askOwner)
        {
            var syncMsg = await System.Threading.Tasks.Task.Run(() => SyncJellyfin(jc, _ => { }));
            if (tvView || !File.Exists(JellyfinFilesFile)) return syncMsg;
            var msg = await JellyfinAddToChosenAsync(jc, askOwner);   // do bibliotek zaznaczonych w 🎞 Jellyfin…
            if (askOwner != null) AskRemovedFromJellyfin(askOwner);
            return msg;
        }

        // nowe filmy z Jellyfin (plik dostępny na tym komputerze) do biblioteki; niedostępne - do raportu
        async System.Threading.Tasks.Task<string> JellyfinAddNewAsync(bool shared, Window askOwner = null)
        {
            if (!File.Exists(JellyfinFilesFile)) return "";
            var jfFiles = File.ReadAllLines(JellyfinFilesFile).ToList();
            // porównanie tylko z otwartą biblioteką (wspólna / tego komputera) - film obecny w drugiej nie blokował dodania, a otwarta zostawała pusta („Nowe: 0”)
            // 🎞 Jellyfin jest serwerem: film, którego pliku nie ma na tym komputerze, gra wprost z Jellyfin (strumień) - bez Velivo na NUCBOX i bez „wspólnej”
            var jc0 = LoadJellyfinConf(); bool canJf = jc0 != null && !string.IsNullOrEmpty(jc0.Url) && !string.IsNullOrEmpty(jc0.Key);
            var fromJf = new HashSet<string>(canJf ? jfFiles.Where(p => !string.IsNullOrEmpty(MetaFor(p)?.Id)) : Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            Func<string, bool> playable = p => File.Exists(p) || fromJf.Contains(p);
            // filmy usunięte wcześniej „tylko z Velivo” nie wracają same - ręczne „Pobierz / uzupełnij” mówi, ile ich jest, i pyta o przywrócenie
            // (biblioteka pusta, a „Nowe: 0” - nie było widać dlaczego)
            var skipped = await System.Threading.Tasks.Task.Run(() => NewJellyfinFiles(jfFiles, LoadLibrary().Where(v => IsShared(v) == shared).Select(v => v.Path), playable)
                .Where(p => JellyfinSkipped(shared).Contains(p, StringComparer.OrdinalIgnoreCase)).ToList());
            if (askOwner != null && skipped.Count > 0 && MessageBox.Show(askOwner, string.Format(L.T("{0} filmów z Jellyfin jest na tym komputerze, ale wcześniej usunięto je tylko z biblioteki Velivo, więc nie wracają same.\n\nPrzywrócić je do biblioteki?"), skipped.Count),
                    "🎞 Jellyfin", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var back = new HashSet<string>(skipped, StringComparer.OrdinalIgnoreCase);
                JellyfinSkipRestore(back, shared);
                skipped.Clear();
            }
            var add = await System.Threading.Tasks.Task.Run(() => NewJellyfinFiles(jfFiles, LoadLibrary().Where(v => IsShared(v) == shared).Select(v => v.Path).Concat(JellyfinSkipped(shared)), playable));   // usunięte „tylko z Velivo” nie wracają
            int n = 0;
            if (add.Count > 0) { var l = LoadLibrary(); n = AddToLibrary(l, add, shared); SaveLibrary(l); }
            // brakujace pliki (Jellyfin zna, tu niedostepne) - do raportu, zeby bylo widac dlaczego
            var missing = await System.Threading.Tasks.Task.Run(() => NewJellyfinFiles(jfFiles, LoadLibrary().Where(v => IsShared(v) == shared).Select(v => v.Path), p => !playable(p)));
            try { File.AppendAllLines(System.IO.Path.Combine(DataDir, "jellyfin-raport.txt"), new[] { "", "Pliki z Jellyfin niedostępne na tym komputerze (" + missing.Count + "):" }.Concat(missing.Select(m => "  " + m))); } catch (Exception) { }
            _libRefresh?.Invoke();
            return L.T("🎬 Nowe filmy z Jellyfin: ") + n + (skipped.Count > 0 ? L.T(" · usuniętych wcześniej z Velivo (nie wracają): ") + skipped.Count : "") + (missing.Count > 0 ? L.T(" · niedostępnych tutaj: ") + missing.Count + L.T(" (lista w jellyfin-raport.txt)") : "")
                + (n == 0 && missing.Count > 0 && LoadLibrary().Count == 0 ? "\n" + L.T("Pliki filmów są na dysku innego komputera (serwera Jellyfin), nie na tym. Tutaj oglądasz je w bibliotece 🌐 Wspólna (oba komputery włączone, Velivo uruchomione, sparowane w sieci domowej).") : "");
        }
        bool _libSharedView;   // otwarta biblioteka: wspolna albo tego komputera
        bool _libTvView;
        List<KeyValuePair<LanPeerInfo, LanFilmItem>> _peerFilms = new List<KeyValuePair<LanPeerInfo, LanFilmItem>>();   // wspolna biblioteka innych komputerow (ostatnio wczytana)       // otwarta biblioteka "Nagrane z TV" (pliki z folderow nagran)
        bool? _playShared;     // z ktorej biblioteki wlaczono film (jej licznik obejrzenia); null = obojetnie

        sealed class LibRow
        {
            public string Title { get; set; } public string Length { get; set; } public string Seen { get; set; } public string LastPlayed { get; set; } public string AddedText { get; set; } public string Folder { get; set; }
            public string Year { get; set; } public string Rating { get; set; } public string Genre { get; set; }   // z Jellyfin (filmy-info)
            public LibraryVideo Item;
            public bool Missing;
        }

        // "kiedy ostatnio": dzis 21:15 / wczoraj 18:02 / 12.10.2026
        internal static string LastPlayedText(long unixMs, DateTime now)
        {
            if (unixMs <= 0) return "";
            var d = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
            if (d.Date == now.Date) return L.T("dziś ") + d.ToString("HH:mm");
            if (d.Date == now.Date.AddDays(-1)) return L.T("wczoraj ") + d.ToString("HH:mm");
            return d.ToString("dd.MM.yyyy");
        }

        // sortowanie biblioteki: kolumna + kierunek (domyslnie: ostatnio odtwarzane na gorze)
        internal static IEnumerable<LibraryVideo> SortLibrary(IEnumerable<LibraryVideo> list, string key, bool desc, Func<LibraryVideo, MovieInfo> meta = null)
        {
            Func<LibraryVideo, IComparable> by;
            Func<LibraryVideo, MovieInfo> mi = v => meta?.Invoke(v);
            switch (key)
            {
                case "title": by = v => ShownTitle(v, mi(v)).ToLowerInvariant(); break;   // tak, jak widac w kolumnie
                case "year": by = v => mi(v)?.Year ?? 0; break;
                case "rating": by = v => mi(v)?.Rating ?? 0; break;
                case "genre": by = v => (mi(v)?.Genres.FirstOrDefault() ?? "").ToLowerInvariant(); break;
                case "len": by = v => v.Len; break;
                case "added": by = v => v.Added; break;
                case "seen": by = v => SeenFraction(v); break;
                case "folder": by = v => (System.IO.Path.GetDirectoryName(v.Path) ?? "").ToLowerInvariant(); break;
                default: by = v => v.Last; break;
            }
            var sorted = desc ? list.OrderByDescending(by) : list.OrderBy(by);
            return sorted.ThenBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase);
        }

        // owner: okno, nad ktorym ma byc biblioteka (z odtwarzacza - nad filmem, takze gdy odtwarzacz jest zawsze na wierzchu)
        void ShowVideoLibrary() { ShowVideoLibrary(this); }

        // ⏱ prawdziwa różnica dźwięku względem zegara obrazu - VLC podaje ją w dzienniku wyjścia dźwięku („playback too late (µs)”, „not synchronized (µs)”)
        static long _vlcDriftUs; static DateTime _vlcDriftAt;
        static readonly System.Text.RegularExpressions.Regex VlcDriftRx = new System.Text.RegularExpressions.Regex(@"(?:playback (?:way )?too (?:late|early)|not synchronized) \((-?\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);
        static volatile bool _aoutFailed;   // VLC nie otworzyl wyjscia dzwieku (biezacy film)

        // plik AVI po zawartosci (bywa z inna koncowka, np. .ts)
        internal static bool IsAviFile(string path)
        {
            try { using (var f = File.OpenRead(path)) { var b = new byte[12]; return f.Read(b, 0, 12) == 12 && System.Text.Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(b, 8, 4) == "AVI "; } }
            catch (Exception) { return false; }
        }

        // otwarte menu (kontekstowe, z paska) - klik w obraz, ktory je zamyka, nie moze zatrzymac filmu ani TV
        static int _menusOpen; static DateTime _menuClosedAt;
        static bool _menuWatch;
        static void WatchMenus()
        {
            if (_menuWatch) return; _menuWatch = true;
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, e) => _menusOpen++));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((s, e) => { _menusOpen = Math.Max(0, _menusOpen - 1); _menuClosedAt = DateTime.UtcNow; }));
        }

        static readonly string VlcLogFile = System.IO.Path.Combine(DataDir, "vlc.log");
        // dziennik VLC przez kolejkę: VLC woła Log w wątku obrazu i dźwięku, a setki „picture is too late” na sekundę zapisywane
        // od razu na dysk mogły opóźniać te wątki (więcej spóźnionych klatek, rozjechany dźwięk)
        static readonly System.Collections.Concurrent.ConcurrentQueue<string> _vlcLogQ = new System.Collections.Concurrent.ConcurrentQueue<string>();
        static int _vlcLogBusy;
        static void VlcLogQueue(string line)
        {
            if (_vlcLogQ.Count > 2000) return;
            _vlcLogQ.Enqueue(line); VlcLogKick();
        }
        static void VlcLogKick()
        {
            if (System.Threading.Interlocked.Exchange(ref _vlcLogBusy, 1) == 1) return;
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(500);
                var sb = new System.Text.StringBuilder(); while (_vlcLogQ.TryDequeue(out var l)) sb.Append(l);
                System.Threading.Interlocked.Exchange(ref _vlcLogBusy, 0);
                try { lock (VlcLogFile) { var f = new FileInfo(VlcLogFile); if (!f.Exists || f.Length < 512 * 1024) File.AppendAllText(VlcLogFile, sb.ToString()); } } catch (Exception) { }
                if (!_vlcLogQ.IsEmpty) VlcLogKick();
            });
        }
        // 🔒 do dziennika tylko serwer - bez ścieżki i zapytania (token IPTV, login i hasło w adresie listy kanałów nigdy nie trafiają do pliku)
        static string VlcLogSafe(string s) => s == null ? "" : System.Text.RegularExpressions.Regex.Replace(s, @"((?:https?|rtsp|rtmp|udp|mms)://)(?:[^@/\s]*@)?([^/\s?#]+)[^\s]*", "$1$2/…", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // wznowic od zapisanego miejsca? true = wznow, false = od poczatku; "zapamietaj" - ustawienie na stale (bez pytania)
        bool? AskResume(Window owner, long at)
        {
            bool? res = null;
            var dlg = new Window { Title = L.T("Wznowić oglądanie?"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var p = new StackPanel { Margin = new Thickness(18) };
            p.Children.Add(new TextBlock { Text = L.T("Ten film był oglądany do ") + FormatTime(at) + ".", FontSize = 14, Margin = new Thickness(0, 0, 0, 12) });
            var remember = new CheckBox { Content = L.T("Zapamiętaj wybór i nie pytaj (zmiana: Ustawienia → Odtwarzacz filmów)"), Margin = new Thickness(0, 0, 0, 12) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            var resume = new Button { Content = L.T("▶ Wznów od ") + FormatTime(at), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var start = new Button { Content = L.T("⏮ Od początku"), Padding = new Thickness(14, 6, 14, 6) };
            resume.Click += (s, e) => { res = true; dlg.Close(); };
            start.Click += (s, e) => { res = false; dlg.Close(); };
            btns.Children.Add(resume); btns.Children.Add(start);
            p.Children.Add(remember); p.Children.Add(btns);
            dlg.Content = p;
            dlg.Loaded += (q, w) => dlg.Activate(); ShowAboveAll(() => dlg.ShowDialog());   // pytanie zawsze na samej górze i aktywne - Audio Monitor ani inne okna „na wierzchu” go nie zasłaniają
            if (res != null && remember.IsChecked == true) SetPref("wznawianie", res.Value ? "wznow" : "poczatek");
            return res;
        }

        string _libVlcKey;
        bool _forceResume;   // odtwarzacz uruchomiony ponownie (zmiana dzwieku) - wznow bez pytania   // opcje, z ktorymi utworzono VLC
        // tytul na liscie: wlasny (zmieniony w Velivo), z Jellyfin, z nazwy pliku
        internal static string ShownTitle(LibraryVideo v, MovieInfo m) { return !string.IsNullOrWhiteSpace(v.MyTitle) ? v.MyTitle : MetaTitle(m) ?? CleanTitle(v.Title); }

        // moja kolejka (zapamietana) i zapisane playlisty - sciezki filmow w kolejnosci
        internal sealed class Playlists { public List<string> Mine { get; set; } = new List<string>(); public Dictionary<string, List<string>> Named { get; set; } = new Dictionary<string, List<string>>(); }
        static string PlaylistsFile => System.IO.Path.Combine(DataDir, "playlisty.json");
        static Playlists LoadPlaylists()
        {
            try { if (File.Exists(PlaylistsFile)) return JsonSerializer.Deserialize<Playlists>(File.ReadAllText(PlaylistsFile)) ?? new Playlists(); } catch (Exception ex) { App.LogError(ex); }
            return new Playlists();
        }
        static void SavePlaylists(Playlists p) { try { Directory.CreateDirectory(DataDir); File.WriteAllText(PlaylistsFile, JsonSerializer.Serialize(p)); } catch (Exception ex) { App.LogError(ex); } }

        // losowanie filmow: gatunek / jak zaznaczony film, minimalna ocena, nieobejrzane, ile - potem kolejka albo playlista
        void ShowRandomPicker(Window owner, List<LibraryVideo> pool, LibraryVideo seed, Action<List<string>> play)
        {
            var win = new Window { Title = L.T("🎲 Wylosuj filmy"), Width = 760, Height = 720, Owner = owner, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            Independent(win);   // zwykle okno Windows - nie przykrywa innych na stale
            var genres = pool.SelectMany(v => MetaFor(v.Path)?.Genres ?? new List<string>()).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
            var seedGenres = seed != null ? MetaFor(seed.Path)?.Genres ?? new List<string>() : new List<string>();
            var genre = new ComboBox { Width = 240 };
            genre.Items.Add(L.T("Dowolny gatunek"));
            if (seedGenres.Count > 0) genre.Items.Add(L.T("Jak zaznaczony: ") + string.Join(", ", seedGenres.Take(3)));
            foreach (var g in genres) genre.Items.Add(g);
            genre.SelectedIndex = 0;
            var minRating = new ComboBox { Width = 120 };
            foreach (var r in new[] { "dowolna", "5+", "6+", "7+", "8+" }) minRating.Items.Add(L.T(r));
            minRating.SelectedIndex = 0;
            var count = new ComboBox { Width = 80 };
            foreach (var n in new[] { 3, 5, 10, 15, 20 }) count.Items.Add(n);
            count.SelectedIndex = 1;
            var unseen = new CheckBox { Content = L.T("Tylko nieobejrzane"), IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
            var form = new StackPanel { Margin = new Thickness(12) };
            Action<string, FrameworkElement> row = (t, c) => { var d = new DockPanel { Margin = new Thickness(0, 4, 0, 0) }; var tb = new TextBlock { Text = t, Width = 140, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(tb, Dock.Left); c.HorizontalAlignment = HorizontalAlignment.Left; d.Children.Add(tb); d.Children.Add(c); form.Children.Add(d); };
            row(L.T("Gatunek:"), genre); row(L.T("Ocena co najmniej:"), minRating); row(L.T("Ile filmów:"), count);
            form.Children.Add(unseen);
            var list = new ListBox { Margin = new Thickness(12, 0, 12, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            var info = new TextBlock { Margin = new Thickness(12, 6, 12, 6), Foreground = Brushes.Gray };
            var picked = new List<LibraryVideo>();
            var rnd = new Random();
            Action roll = () =>
            {
                double min = minRating.SelectedIndex <= 0 ? 0 : 4 + minRating.SelectedIndex;   // 5, 6, 7, 8
                List<string> want = genre.SelectedIndex <= 0 ? null : seedGenres.Count > 0 && genre.SelectedIndex == 1 ? seedGenres : new List<string> { (string)genre.SelectedItem };
                var cand = pool.Where(v =>
                {
                    var m = MetaFor(v.Path);
                    if (unseen.IsChecked == true && (v.Done || SeenFraction(v) > 0.9)) return false;
                    if (min > 0 && (m?.Rating ?? 0) < min) return false;
                    if (want != null && !(m?.Genres ?? new List<string>()).Any(g => want.Contains(g, StringComparer.CurrentCultureIgnoreCase))) return false;
                    return seed == null || !string.Equals(v.Path, seed.Path, StringComparison.OrdinalIgnoreCase) || want == null;
                }).ToList();
                picked = cand.OrderBy(_ => rnd.Next()).Take((int)count.SelectedItem).ToList();
                list.Items.Clear();
                foreach (var v in picked)   // plakat, tytul, ocena i gatunek, krotki opis
                {
                    var m = MetaFor(v.Path);
                    var img = new Image { Width = 60, Height = 90, Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 12, 4), VerticalAlignment = VerticalAlignment.Top };
                    if (m?.Poster != null) { var pf = System.IO.Path.Combine(PosterDir, m.Poster); if (File.Exists(pf)) img.Source = LoadLogo(pf, 180); }
                    var txt = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
                    txt.Children.Add(new TextBlock { Text = ShownTitle(v, m), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                    var sub = string.Join("  ·  ", new[] { m?.Rating > 0 ? "★ " + m.Rating.ToString("0.0") : null, m?.Genres.Count > 0 ? string.Join(", ", m.Genres.Take(3)) : null, v.Len > 0 ? FormatTime(v.Len) : null }.Where(x => x != null));
                    if (sub.Length > 0) txt.Children.Add(new TextBlock { Text = sub, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 2) });
                    var ov = (m?.Overview ?? "").Trim();
                    if (ov.Length > 0) txt.Children.Add(new TextBlock { Text = ov.Length > 260 ? ov.Substring(0, 260).TrimEnd() + "…" : ov, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                    var dp = new DockPanel(); DockPanel.SetDock(img, Dock.Left); dp.Children.Add(img); dp.Children.Add(txt);
                    list.Items.Add(new ListBoxItem { Content = dp, Padding = new Thickness(4) });
                }
                info.Text = picked.Count == 0 ? L.T("Nic nie pasuje – zmień warunki.") : string.Format(L.T("Wylosowano {0} z {1} pasujących."), picked.Count, cand.Count);
            };
            var bar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
            bar.Children.Add(SmallButton(L.T("🎲 Losuj ponownie"), roll));
            bar.Children.Add(SmallButton(L.T("▶ Odtwórz jako kolejkę"), () => { if (picked.Count == 0) return; var ps = picked.Select(v => v.Path).ToList(); var p0 = LoadPlaylists(); p0.Mine = ps; SavePlaylists(p0); win.Close(); play(ps); }));
            bar.Children.Add(SmallButton(L.T("➕ Dodaj do kolejki"), () =>
            {
                if (picked.Count == 0) return;
                var ps = picked.Select(v => v.Path).ToList();
                if (_vlcWin == null) { win.Close(); play(ps); return; }
                foreach (var p1 in ps) if (!_playlist.Contains(p1, StringComparer.OrdinalIgnoreCase)) _playlist.Add(p1);
                ShowToast(L.T("➕ Dodano do kolejki: ") + ps.Count, null);
            }));
            bar.Children.Add(SmallButton(L.T("💾 Zapisz jako playlistę…"), () =>
            {
                if (picked.Count == 0) return;
                var def = L.T("Losowe – ") + (genre.SelectedIndex <= 0 ? L.T("różne") : genre.SelectedItem.ToString().Replace(L.T("Jak zaznaczony: "), "")) + (minRating.SelectedIndex > 0 ? " " + minRating.SelectedItem : "");
                var n = Prompt(L.T("Nazwa playlisty:"), def); if (string.IsNullOrWhiteSpace(n)) return;
                var p0 = LoadPlaylists(); p0.Named[n.Trim()] = picked.Select(v => v.Path).ToList(); SavePlaylists(p0);
                ShowToast(L.T("🎞 Zapisano playlistę: ") + n.Trim() + " (" + picked.Count + ")", null);
            }));
            var dock = new DockPanel();
            DockPanel.SetDock(form, Dock.Top); DockPanel.SetDock(info, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom);
            dock.Children.Add(form); dock.Children.Add(info); dock.Children.Add(bar); dock.Children.Add(list);
            win.Content = dock;
            foreach (var c in new[] { genre, minRating, count }) c.SelectionChanged += (s, e) => { if (win.IsLoaded) roll(); };
            unseen.Click += (s, e) => roll();
            win.Loaded += (s, e) => roll();
            win.Show();
        }

        static bool Surround51 => GetPref("dzwiek-51") == "1";   // dzwiek przestrzenny bez miksowania do stereo (wybor w menu dzwieku)

        // ostatnio ogladany, nieskonczony film z tego komputera
        LibraryVideo LastFilm()
        {
            var tv = TvRecDirs();
            return LoadLibrary().Where(v => v.Peer == null && v.Last > 0 && !v.Done && v.Pos > 0 && !IsTvRec(v.Path, tv) && FilmPlayable(v.Path)).OrderByDescending(v => v.Last).FirstOrDefault();
        }

        void ContinueLastFilm()
        {
            var v = LastFilm(); if (v == null) return;
            _playShared = IsShared(v);
            PlayVideos(new[] { v.Path }, 0);   // od zapisanego miejsca
        }

        // film gra - biblioteka schowana (nie zamknięta); po zamknięciu odtwarzacza wraca z tą samą listą, od razu do wyboru następnego
        void LibAway(Window w) { if (w == _libWin) w.Hide(); else w.Close(); }
        void LibBack() { if (_libWin != null && _libWin.IsLoaded && !_libWin.IsVisible && !_mainClosing) { _libWin.Show(); _libWin.Activate(); } }

        void ShowVideoLibrary(Window owner)
        {
            if (_libWin != null && _libWin.IsLoaded) { if (!_libWin.IsVisible) _libWin.Show(); if (_libWin.WindowState == WindowState.Minimized) _libWin.WindowState = WindowState.Normal; _libWin.Topmost = owner.Topmost; _libWin.Activate(); return; }
            // właściciel - okno Velivo, nie odtwarzacz: okna podrzędne zamykają się razem z właścicielem (biblioteka znikała po zamknięciu filmu)
            var win = new Window { Title = L.T("🎬 Biblioteka filmów"), Width = 860, Height = 540, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            win.Topmost = owner.Topmost; win.Deactivated += (s0, e0) => win.Topmost = false;   // bez właściciela: przeglądarka może je zasłonić; nad odtwarzaczem na wierzchu tylko do kliknięcia gdzie indziej
            Independent(win);   // zwykle okno Windows - nie przykrywa innych na stale
            var search = new TextBox { Margin = new Thickness(8, 8, 8, 4), Padding = new Thickness(4) };
            var hint = new TextBlock { Text = L.T("🔍 Szukaj filmu…"), Foreground = Brushes.Gray, Margin = new Thickness(14, 13, 0, 0), IsHitTestVisible = false };
            search.TextChanged += (s, a) => hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var searchBox = new Grid(); searchBox.Children.Add(search); searchBox.Children.Add(hint);
            // dwie biblioteki: wspolna (ta sama nazwa na sparowanych komputerach, filmy wszystkich wlaczonych Velivo) i prywatna tego komputera
            var source = new ComboBox { Width = 230, Margin = new Thickness(8, 8, 0, 4), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = L.T("Wspólna biblioteka (widzą ją sparowane komputery) albo biblioteka tylko tego komputera") };
            Func<string> sharedName = () => string.IsNullOrWhiteSpace(_settings.VlcSharedName) ? L.T("Wspólna biblioteka") : _settings.VlcSharedName;
            Func<string> localName = () => string.IsNullOrWhiteSpace(_settings.VlcLocalName) ? Environment.MachineName : _settings.VlcLocalName;
            var sharedItem = new ComboBoxItem(); var localItem = new ComboBoxItem(); var tvItem = new ComboBoxItem { Content = L.T("📺 Nagrane z TV") };
            Action names = () => { sharedItem.Content = "📚 " + sharedName(); localItem.Content = "🔒 " + localName(); };
            names();
            source.Items.Add(sharedItem); source.Items.Add(localItem);
            if (IptvInstalled) source.Items.Add(tvItem); else _libTvView = false;
            source.SelectedItem = _libTvView ? tvItem : _libSharedView ? sharedItem : localItem;
            var renameBtn = SmallButton(L.T("✏ Zmień nazwę"), null);
            var deleteBtn = SmallButton(L.T("🗑 Usuń bibliotekę"), null);
            renameBtn.Margin = deleteBtn.Margin = new Thickness(6, 8, 0, 4);
            var top = new DockPanel();
            foreach (var c in new FrameworkElement[] { source, renameBtn, deleteBtn }) { DockPanel.SetDock(c, Dock.Left); top.Children.Add(c); }
            top.Children.Add(searchBox);
            // filmy wspolnej biblioteki z innych Velivo (wlaczonych) - wczytywane przy wejsciu do wspolnej
            var peerItems = new List<KeyValuePair<LanPeerInfo, LanFilmItem>>();
            var remoteOf = new Dictionary<LibraryVideo, KeyValuePair<LanPeerInfo, LanFilmItem>>();
            int peersOff = 0;
            var grid = new GridView();
            // kolumny: naglowek klik = sortuj, drugi klik = odwrotnie
            var cols = new[] { new[] { "title", "Tytuł", "Title", "320" }, new[] { "year", "Rok", "Year", "52" }, new[] { "rating", "Ocena", "Rating", "56" }, new[] { "genre", "Gatunek", "Genre", "110" },
                new[] { "len", "Długość", "Length", "70" }, new[] { "seen", "Obejrzane", "Seen", "80" },
                new[] { "last", "Ostatnio oglądano", "LastPlayed", "110" }, new[] { "added", "Dodano", "AddedText", "100" }, new[] { "folder", "Folder", "Folder", "260" } };
            var sortSaved = (GetPref("sort-biblioteka") ?? "last,desc").Split(',');   // ostatnie sortowanie
            string sortKey = cols.Any(c => c[0] == sortSaved[0]) ? sortSaved[0] : "last"; bool sortDesc = sortSaved.Length > 1 && sortSaved[1] == "desc";
            var headers = new Dictionary<string, GridViewColumnHeader>();
            foreach (var c in cols)
            {
                var h = new GridViewColumnHeader { Content = L.T(c[1]), Tag = c[0], Cursor = Cursors.Hand };
                headers[c[0]] = h;
                grid.Columns.Add(new GridViewColumn { Header = h, Width = double.Parse(c[3], System.Globalization.CultureInfo.InvariantCulture), DisplayMemberBinding = new System.Windows.Data.Binding(c[2]) });
            }
            Action markSort = () => { foreach (var c in cols) headers[c[0]].Content = L.T(c[1]) + (c[0] == sortKey ? (sortDesc ? "  ▼" : "  ▲") : ""); };
            markSort();
            var list = new ListView { View = grid, Margin = new Thickness(8, 0, 8, 0), SelectionMode = SelectionMode.Extended };
            RememberColumns(win, "biblioteka-v3", grid);   // v2: kolumny Rok / Ocena / Gatunek
            var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, Margin = new Thickness(10, 0, 0, 0) };
            List<LibraryVideo> lib = LoadLibrary();
            List<LibRow> rows = new List<LibRow>();
            int fillVer = 0;
            Action fill = async () =>
            {
                int ver = ++fillVer;
                lib = LoadLibrary();
                var q = search.Text.Trim();
                var key = sortKey; var desc = sortDesc; var shared = _libSharedView; var meta = Meta; var tv = _libTvView; var recDirs = TvRecDirs();
                var peersNow = SendTargets(); var online = new HashSet<string>(peersNow.Select(p => p.Id));   // kopie z innych komputerow - dostepne, gdy tamten wlaczony
                var fromPeers = shared && !tv ? peerItems.ToList() : new List<KeyValuePair<LanPeerInfo, LanFilmItem>>();
                var local = lib;
                // czy plik jest na dysku - w tle: wylaczony NAS / odlaczony dysk sieciowy odpowiada dopiero po kilku sekundach
                var res = await System.Threading.Tasks.Task.Run(() =>
                {
                    var mine = local.Where(v => tv ? IsTvRec(v.Path, recDirs) : !IsTvRec(v.Path, recDirs) && IsShared(v) == shared).ToList();   // nagrania TV - tylko w swojej bibliotece
                    var all = new List<LibraryVideo>(mine);
                    var of = new Dictionary<LibraryVideo, KeyValuePair<LanPeerInfo, LanFilmItem>>();
                    // ten sam film z NAS na kilku komputerach = jedna pozycja
                    var seen = new HashSet<string>(mine.Select(v => NetworkPath(v.Path)).Where(n => n != null), StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in fromPeers)
                    {
                        if (!seen.Add(kv.Value.n ?? kv.Key.Id + "|" + kv.Value.i)) continue;
                        var x = kv.Value;
                        var v = new LibraryVideo { Path = x.n ?? ("velivo:" + x.i + "\\" + (x.fn ?? x.i)), Title = x.t, Len = x.l, Pos = x.p, Done = x.d, Last = x.w };
                        of[v] = kv; all.Add(v);
                    }
                    // niedostepny udzial / dysk sprawdzany raz (nie po kilka sekund dla kazdego filmu)
                    var roots = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    Func<string, bool> exists = path =>
                    {
                        var root = System.IO.Path.GetPathRoot(path) ?? "";
                        bool up;
                        if (!roots.TryGetValue(root, out up)) roots[root] = up = Directory.Exists(root);
                        return up && File.Exists(path);
                    };
                    var now = DateTime.Now;
                    Func<LibraryVideo, MovieInfo> mf = v => LookupMeta(meta, v.Path);   // dane z Jellyfin (lokalna baza)
                    var jfc = LoadJellyfinConf(); bool jfOn = jfc != null && !string.IsNullOrEmpty(jfc.Url) && !string.IsNullOrEmpty(jfc.Key);   // brak pliku tutaj - film z serwera Jellyfin
                    var r = SortLibrary(all.Where(v => q.Length == 0 || v.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (v.MyTitle ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || v.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        mf(v) is MovieInfo m1 && ((m1.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (m1.Original ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)), key, desc, mf)
                    .Select(v => new LibRow
                    {
                        Item = v, Missing = !of.ContainsKey(v) && (v.Peer != null ? !online.Contains(v.Peer) : !exists(v.Path) && !(jfOn && !string.IsNullOrEmpty(mf(v)?.Id))), Title = ShownTitle(v, mf(v)),
                        Year = mf(v)?.Year > 0 ? mf(v).Year.ToString() : "", Rating = mf(v)?.Rating > 0 ? "★ " + mf(v).Rating.ToString("0.0") : "", Genre = string.Join(", ", mf(v)?.Genres ?? new List<string>()),
                        Length = v.Len > 0 ? FormatTime(v.Len) : "",
                        Seen = SeenText(v),
                        LastPlayed = LastPlayedText(v.Last, now),
                        AddedText = LastPlayedText(v.Added, now),   // kiedy dodany do biblioteki
                        Folder = v.Peer != null ? "🖥 " + (peersNow.FirstOrDefault(p => p.Id == v.Peer)?.Device ?? L.T("inny komputer (wyłączony)")) + "  ·  " + MetaKey(v.Path) : of.ContainsKey(v) ? (of[v].Value.n != null ? System.IO.Path.GetDirectoryName(of[v].Value.n) : "🖥 " + of[v].Key.Device) : System.IO.Path.GetDirectoryName(v.Path),
                    }).Select(row => { if (row.Missing) row.Title = "⚠ " + row.Title; return row; }).ToList();
                    return Tuple.Create(r, all.Count, of);
                });
                if (ver != fillVer) return;   // w miedzyczasie nowsze szukanie
                // odswiezenie (np. co 10 s zapis miejsca filmu) nie gubi zaznaczenia - inaczej znika opis z prawej
                var keepSel = new HashSet<string>(list.SelectedItems.Cast<LibRow>().Select(x => x.Item.Path), StringComparer.OrdinalIgnoreCase);
                rows = res.Item1; remoteOf.Clear(); foreach (var kv in res.Item3) remoteOf[kv.Key] = kv.Value;
                list.ItemsSource = rows;
                foreach (var r0 in rows.Where(x => keepSel.Contains(x.Item.Path))) list.SelectedItems.Add(r0);
                count.Text = rows.Count + " / " + res.Item2 + L.T(" filmów") + (shared && peersOff > 0 ? "  ·  " + L.T("niedostępne teraz (komputer wyłączony): ") + peersOff : "");
            };
            // filmy wspolnej biblioteki ze wszystkich wlaczonych Velivo naraz
            Action loadPeers = async () =>
            {
                var peers = SendTargets();
                if (peers.Count == 0) { peerItems.Clear(); peersOff = 0; fill(); return; }
                count.Text = L.T("Wczytuję wspólną bibliotekę…");
                var got = await System.Threading.Tasks.Task.WhenAll(peers.Select(p => RequestLibrary(p)));
                peerItems.Clear(); peersOff = 0;
                for (int i = 0; i < peers.Count; i++)
                {
                    if (got[i] == null) { peersOff++; continue; }
                    foreach (var x in got[i]) peerItems.Add(new KeyValuePair<LanPeerInfo, LanFilmItem>(peers[i], x));
                }
                _peerFilms = peerItems.ToList(); fill();   // lista filmow w odtwarzaczu (film z innego komputera)
            };
            // schowana biblioteka (film gra) - bez przebudowy listy w wątku okna przy każdym zapisie miejsca (przycięcia obrazu); raz po pokazaniu
            bool libStale = false;
            _libRefresh = () => { if (win.IsVisible) fill(); else libStale = true; };
            win.IsVisibleChanged += (s, a) => { if (win.IsVisible && libStale) { libStale = false; fill(); } };
            list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((s, a) =>
            {
                if (!(a.OriginalSource is GridViewColumnHeader h) || !(h.Tag is string k)) return;
                if (k == sortKey) sortDesc = !sortDesc;
                else { sortKey = k; sortDesc = k == "last" || k == "len" || k == "seen" || k == "year" || k == "rating"; }   // daty i liczby: najpierw najwieksze
                SetPref("sort-biblioteka", sortKey + (sortDesc ? ",desc" : ""));
                markSort(); fill();
            }));
            search.TextChanged += (s, a) => fill();
            source.SelectionChanged += (s, a) =>
            {
                _libSharedView = source.SelectedItem == sharedItem; _libTvView = source.SelectedItem == tvItem;
                renameBtn.IsEnabled = !_libTvView;
                fill();
                if (_libSharedView) loadPeers();
            };
            renameBtn.Click += (s, a) =>
            {
                bool shared = _libSharedView;
                win.Topmost = false;
                var name = Prompt(shared ? L.T("Nazwa wspólnej biblioteki (taka sama na wszystkich sparowanych komputerach):") : L.T("Nazwa biblioteki tego komputera:"), shared ? sharedName() : localName());
                win.Topmost = owner.Topmost;
                if (name == null) return;
                if (shared) _settings.VlcSharedName = name.Trim(); else _settings.VlcLocalName = name.Trim();
                try { _settings.Save(DataDir); } catch (Exception) { }
                names();
            };
            deleteBtn.Click += (s, a) =>
            {
                bool shared = _libSharedView, tv = _libTvView; var recDirs = TvRecDirs();
                Func<LibraryVideo, bool> mine = v => tv ? IsTvRec(v.Path, recDirs) : !IsTvRec(v.Path, recDirs) && IsShared(v) == shared;
                int here = LoadLibrary().Count(mine), fromPeers = shared && !tv ? remoteOf.Count : 0;
                var name = tv ? L.T("Nagrane z TV") : shared ? sharedName() : localName();
                // filmy z innych komputerow sa zapisane tam - tu mozna usunac tylko wpisy z tego komputera
                var peersNote = fromPeers > 0 ? string.Format(L.T("\n\n{0} pozycji przychodzi z innych komputerów – są zapisane tam i usuwa się je na tamtym komputerze."), fromPeers) : "";
                if (here == 0) { InfoBox(win, string.Format(L.T("W bibliotece „{0}” na tym komputerze nie ma żadnych pozycji do usunięcia."), name) + peersNote); return; }
                if (!AskYes(win, string.Format(L.T("Usunąć z biblioteki „{0}” {1} pozycji zapisanych na tym komputerze?\n\nPliki filmów zostają na dysku."), name, here) + peersNote)) return;
                var l = LoadLibrary(); l.RemoveAll(v => mine(v));
                SaveLibrary(l); fill();
            };
            renameBtn.IsEnabled = !_libTvView;
            if (_libSharedView) loadPeers();
            fill();
            Func<List<LibRow>> sel = () => list.SelectedItems.Cast<LibRow>().ToList();
            Action play = () =>
            {
                var r = list.SelectedItem as LibRow; if (r == null) return;
                if (remoteOf.TryGetValue(r.Item, out var rem)) { WantRemoteFilm(rem.Key, rem.Value, false); LibAway(win); return; }   // film z drugiego komputera - ogladasz tutaj; biblioteka schowana, wraca po zamknięciu odtwarzacza
                if (r.Item.Peer != null)   // skopiowany z innego komputera - przez siec
                {
                    var pr = PeerById(r.Item.Peer);
                    if (pr == null) { InfoBox(win, L.T("Komputer, na którym jest ten film, jest teraz wyłączony albo niedostępny.")); return; }
                    WantRemoteFilm(pr, new LanFilmItem { i = r.Item.RemoteId, t = r.Item.Title, l = r.Item.Len }, false); LibAway(win); return;
                }
                if (r.Missing) { InfoBox(win, L.T("Nie ma już tego pliku na dysku (przeniesiony albo usunięty).")); return; }
                var playable = rows.Where(x => !x.Missing && !remoteOf.ContainsKey(x.Item) && x.Item.Peer == null).Select(x => x.Item.Path).ToList();   // lista odtwarzania = widoczna lista
                _playShared = _libSharedView;
                PlayVideos(playable.ToArray(), playable.IndexOf(r.Item.Path));
                LibAway(win);   // film gra - biblioteka schowana, wraca po zamknięciu odtwarzacza
            };
            Action remove = async () =>
            {
                var rs = sel().Where(r => !remoteOf.ContainsKey(r.Item)).ToList(); if (rs.Count == 0) return;
                // 🎞 filmy z Jellyfin: usunięcie także na serwerze (Jellyfin kasuje plik z dysku serwera) albo tylko w Velivo (nie wraca przy 🔄 Odśwież)
                var jc = _libTvView ? null : LoadJellyfinConf();
                var jf = jc == null || string.IsNullOrEmpty(jc.Key) ? new List<(string Path, string Id)>() : rs.Select(r => (r.Item.Path, MetaFor(r.Item.Path)?.Id)).Where(x => !string.IsNullOrEmpty(x.Id)).ToList();
                bool jfDelete = false;
                if (jf.Count > 0)
                {
                    var ans = MessageBox.Show(win, string.Format(L.T("{0} z zaznaczonych filmów jest na serwerze Jellyfin.\n\nTak – usuń też z Jellyfin (serwer skasuje plik filmu ze swojego dysku, na stałe).\nNie – usuń tylko z biblioteki Velivo (nie wróci przy 🔄 Odśwież).\nAnuluj – nic nie usuwaj."), jf.Count), "🎞 Jellyfin", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
                    if (ans == MessageBoxResult.Cancel || ans == MessageBoxResult.None) return;
                    jfDelete = ans == MessageBoxResult.Yes;
                    if (!jfDelete) JellyfinSkipAdd(jf.Select(x => x.Path), _libSharedView);   // nie wraca tylko w tej bibliotece
                }
                var l = LoadLibrary(); bool sh = _libSharedView, tv = _libTvView; var recDirs = TvRecDirs(); l.RemoveAll(v => (tv ? IsTvRec(v.Path, recDirs) : IsShared(v) == sh) && rs.Any(r => string.Equals(r.Item.Path, v.Path, StringComparison.OrdinalIgnoreCase)));
                SaveLibrary(l); fill();
                if (jfDelete) foreach (var g in jf.GroupBy(x => MetaFor(x.Path)?.Srv)) ShowToast(await JellyfinDeleteAsync(JfServers(jc).FirstOrDefault(sv => JfSame(sv.Url, g.Key)) ?? jc, g.Select(x => x.Id).Distinct().ToList()), null);   // każdy film na swoim serwerze
            };
            // usun z biblioteki i plik z dysku (do Kosza; dysk sieciowy nie ma Kosza - wtedy na stale)
            Action removeFiles = () =>
            {
                var rs = sel().Where(r => !remoteOf.ContainsKey(r.Item) && !r.Missing).ToList(); if (rs.Count == 0) return;
                var msg = rs.Count == 1 ? string.Format(L.T("Usunąć film „{0}” z biblioteki i z dysku?"), rs[0].Item.Title) : string.Format(L.T("Usunąć {0} filmów z biblioteki i z dysku?"), rs.Count);
                if (!AskYes(win, msg + L.T("\n\nPlik trafi do Kosza (na dysku sieciowym – usunięty na stałe)."))) return;
                var errs = DeleteVideoFiles(rs.Select(r => r.Item.Path));
                fill();
                if (errs.Count > 0) InfoBox(win, L.T("Nie udało się usunąć (plik w użyciu – np. właśnie odtwarzany):\n") + string.Join("\n", errs.Take(10)));
            };
            list.MouseDoubleClick += (s, a) => play();
            // zaznaczanie kilku filmow przeciaganiem z wcisnietym lewym przyciskiem (jak w Eksploratorze); Ctrl/Shift - jak zwykle
            int dragAnchor = -1;
            Func<object, int> rowAt = src => ItemsControl.ContainerFromElement(list, src as DependencyObject) is ListViewItem c ? list.ItemContainerGenerator.IndexFromContainer(c) : -1;
            list.PreviewMouseLeftButtonDown += (s, a) => dragAnchor = Keyboard.Modifiers == ModifierKeys.None ? rowAt(a.OriginalSource) : -1;
            list.PreviewMouseLeftButtonUp += (s, a) => dragAnchor = -1;
            list.PreviewMouseMove += (s, a) =>
            {
                if (a.LeftButton != MouseButtonState.Pressed || dragAnchor < 0) return;
                int i = rowAt(a.OriginalSource); if (i < 0 || i == dragAnchor && list.SelectedItems.Count <= 1) return;
                list.SelectedItems.Clear();
                for (int k = Math.Min(i, dragAnchor); k <= Math.Max(i, dragAnchor); k++) list.SelectedItems.Add(list.Items[k]);
            };
            // zaznaczone filmy tego komputera (w kolejnosci listy) - do odtworzenia albo kolejki
            Func<string[]> selPaths = () => list.SelectedItems.Cast<LibRow>().OrderBy(r => rows.IndexOf(r)).Where(r => !r.Missing && !remoteOf.ContainsKey(r.Item) && r.Item.Peer == null).Select(r => r.Item.Path).ToArray();
            list.PreviewKeyDown += (s, a) =>
            {
                if (a.Key == Key.Enter) { play(); a.Handled = true; }
                else if (a.Key == Key.Delete) { remove(); a.Handled = true; }
            };
            // menu pod prawym: filmy tego komputera - takze "Odtworz na" / "Ogladaj razem z" innym Velivo; filmy drugiego komputera - tutaj / razem
            var menu = new ContextMenu();
            Action<string, Action> addItem = (text, act) => { var mi = new MenuItem { Header = text }; mi.Click += (s, a) => act(); menu.Items.Add(mi); };
            Func<string, bool, MenuItem> peersMenu = (text, together) =>
            {
                var root = new MenuItem { Header = text };
                var r = list.SelectedItem as LibRow;
                var peers = SendTargets();
                if (peers.Count == 0) root.Items.Add(new MenuItem { Header = NoPeersWhy(), IsEnabled = false });
                foreach (var p0 in peers)
                {
                    var p = p0;
                    var mi = new MenuItem { Header = "🖥 " + p.Device };
                    mi.Click += (s, a) => { if (r != null && !r.Missing) PlayFilmOn(r.Item, p, together); };
                    root.Items.Add(mi);
                }
                return root;
            };
            menu.Opened += (s, a) =>
            {
                menu.Items.Clear();
                var r0 = list.SelectedItem as LibRow;
                if (r0 != null && remoteOf.TryGetValue(r0.Item, out var rem))
                {
                    addItem(L.T("▶ Odtwórz tutaj"), play);
                    addItem(L.T("👥 Oglądaj razem z: ") + rem.Key.Device, () => WantRemoteFilm(rem.Key, rem.Value, true));
                    // kopia na ten komputer (wlasny licznik, wlasne zarzadzanie): NAS - wprost, dysk tamtego komputera - przez siec
                    Action<IEnumerable<KeyValuePair<LanPeerInfo, LanFilmItem>>> copyHere = items =>
                    {
                        var l = LoadLibrary(); int n = 0;
                        foreach (var kv in items)
                        {
                            var x = kv.Value;
                            if (x.n != null) { n += AddToLibrary(l, new[] { x.n }, false); continue; }
                            var p = "velivo:" + x.i + "\\" + (x.fn ?? x.i);
                            if (l.Any(v => v.Peer == kv.Key.Id && v.RemoteId == x.i)) continue;
                            l.Add(new LibraryVideo { Path = p, Title = x.t, Len = x.l, Added = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Shared = false, Peer = kv.Key.Id, RemoteId = x.i }); n++;
                        }
                        SaveLibrary(l); ShowToast(L.T("🔒 Skopiowano do: ") + localName() + " – " + n, null);
                    };
                    addItem(L.T("📋 Kopiuj zaznaczone do: ") + localName(), () => copyHere(sel().Where(r => remoteOf.ContainsKey(r.Item)).Select(r => remoteOf[r.Item]).ToList()));
                    addItem(L.T("📋 Kopiuj całą listę z innych komputerów do: ") + localName(), () => copyHere(remoteOf.Values.ToList()));
                    return;
                }
                addItem(L.T("▶ Odtwórz"), play);
                if (_pipShowFilm != null) addItem(L.T("⧉ Pokaż w PiP (podgląd w odtwarzaczu)"), () => { var ps = selPaths(); if (ps.Length > 0) _pipShowFilm(ps[0]); });
                int nSel = list.SelectedItems.Count;
                Action<List<string>> rememberMine = q => { var p0 = LoadPlaylists(); p0.Mine = q.ToList(); SavePlaylists(p0); };   // moja kolejka zostaje (takze po wlaczeniu innego filmu i po zamknieciu)
                if (nSel > 1) addItem(L.T("▶ Odtwórz zaznaczone jako kolejkę (") + nSel + ")", () => { var ps = selPaths(); if (ps.Length == 0) return; rememberMine(ps.ToList()); _playShared = _libSharedView; PlayVideos(ps, 0); LibAway(win); });
                addItem(L.T("➕ Dodaj do kolejki") + (nSel > 1 ? " (" + nSel + ")" : ""), () =>
                {
                    var ps = selPaths(); if (ps.Length == 0) return;
                    if (_vlcWin == null) { rememberMine(ps.ToList()); _playShared = _libSharedView; PlayVideos(ps, 0); LibAway(win); return; }   // nic nie gra - kolejka od tych filmow
                    int added = 0; foreach (var p0 in ps) if (!_playlist.Contains(p0, StringComparer.OrdinalIgnoreCase)) { _playlist.Add(p0); added++; }
                    rememberMine(_playlist.Where(u => !_iptvPlaying.ContainsKey(u) && !_remoteFilms.ContainsKey(u)).ToList());
                    ShowToast(L.T("➕ Dodano do kolejki: ") + added, null);
                });
                // playlisty: zapis zaznaczonych / mojej kolejki pod nazwa, odtwarzanie, usuwanie
                var plsM = LoadPlaylists();
                var plRoot = new MenuItem { Header = L.T("🎞 Playlisty") };
                Action<string, Action> plAdd = (t, act) => { var mi = new MenuItem { Header = t }; mi.Click += (q, w2) => act(); plRoot.Items.Add(mi); };
                Action<List<string>> saveAs = paths =>
                {
                    if (paths.Count == 0) return;
                    win.Topmost = false; var n = Prompt(L.T("Nazwa playlisty (np. Obcy – wszystkie części):"), ""); win.Topmost = owner.Topmost;
                    if (string.IsNullOrWhiteSpace(n)) return;
                    var p0 = LoadPlaylists(); p0.Named[n.Trim()] = paths; SavePlaylists(p0); ShowToast(L.T("🎞 Zapisano playlistę: ") + n.Trim() + " (" + paths.Count + ")", null);
                };
                plAdd(L.T("💾 Zapisz zaznaczone jako playlistę…"), () => saveAs(selPaths().ToList()));
                if (plsM.Mine.Count > 0)
                {
                    plAdd(L.T("▶ Odtwórz moją kolejkę (") + plsM.Mine.Count + ")", () => { _playShared = _libSharedView; PlayVideos(plsM.Mine.ToArray(), 0); LibAway(win); });
                    plAdd(L.T("💾 Zapisz moją kolejkę jako playlistę…"), () => saveAs(plsM.Mine.ToList()));
                    plAdd(L.T("🗑 Wyczyść moją kolejkę"), () => { var p0 = LoadPlaylists(); p0.Mine.Clear(); SavePlaylists(p0); });
                }
                if (plsM.Named.Count > 0) plRoot.Items.Add(new Separator());
                foreach (var name0 in plsM.Named.Keys.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
                {
                    var name = name0; var one = new MenuItem { Header = "🎞 " + name + " (" + plsM.Named[name].Count + ")" };
                    var pPlay = new MenuItem { Header = L.T("▶ Odtwórz") }; pPlay.Click += (q, w2) => { _playShared = _libSharedView; PlayVideos(plsM.Named[name].ToArray(), 0); LibAway(win); };
                    var pAdd = new MenuItem { Header = L.T("➕ Dodaj zaznaczone") }; pAdd.Click += (q, w2) => { var p0 = LoadPlaylists(); if (!p0.Named.TryGetValue(name, out var l0)) return; foreach (var x in selPaths()) if (!l0.Contains(x, StringComparer.OrdinalIgnoreCase)) l0.Add(x); SavePlaylists(p0); };
                    var pDel = new MenuItem { Header = L.T("🗑 Usuń playlistę") }; pDel.Click += (q, w2) => { if (!AskYes(win, string.Format(L.T("Usunąć playlistę „{0}”? Filmy zostają w bibliotece."), name))) return; var p0 = LoadPlaylists(); p0.Named.Remove(name); SavePlaylists(p0); };
                    one.Items.Add(pPlay); one.Items.Add(pAdd); one.Items.Add(pDel); plRoot.Items.Add(one);
                }
                menu.Items.Add(plRoot);
                addItem(L.T("🎲 Wylosuj filmy…"), () =>
                {
                    var pool = rows.Where(x => !x.Missing && !remoteOf.ContainsKey(x.Item) && x.Item.Peer == null).Select(x => x.Item).ToList();
                    win.Topmost = false;
                    ShowRandomPicker(win, pool, (list.SelectedItem as LibRow)?.Item, ps => { _playShared = _libSharedView; PlayVideos(ps.ToArray(), 0); LibAway(win); });
                    win.Topmost = owner.Topmost;
                });
                menu.Items.Add(peersMenu(L.T("📺 Odtwórz na…"), false));
                menu.Items.Add(peersMenu(L.T("👥 Oglądaj razem z…"), true));
                menu.Items.Add(new Separator());
                // wlasny tytul: tylko w Velivo (plik i dopasowanie do Jellyfin bez zmian - plakat i opis zostaja); pusty = tytul z Jellyfin
                addItem(L.T("✏ Zmień tytuł…"), () =>
                {
                    var r = list.SelectedItem as LibRow; if (r == null || remoteOf.ContainsKey(r.Item)) return;
                    win.Topmost = false;
                    var t = Prompt(L.T("Tytuł filmu w Velivo (plik na dysku i Jellyfin bez zmian; puste = tytuł z Jellyfin):"), r.Title.TrimStart('⚠', ' '));
                    win.Topmost = owner.Topmost;
                    if (t == null) return;
                    var l = LoadLibrary(); var v = FindEntry(l, r.Item.Path, IsShared(r.Item)); if (v == null) return;
                    t = t.Trim(); v.MyTitle = t.Length == 0 || t == (MetaTitle(MetaFor(v.Path)) ?? CleanTitle(v.Title)) ? null : t;
                    SaveLibrary(l); fill();
                });
                addItem(L.T("🔎 Wyszukaj informacje w sieci…"), () =>
                {
                    var r = list.SelectedItem as LibRow; if (r == null || remoteOf.ContainsKey(r.Item)) return;
                    win.Topmost = false;
                    ShowMetaSearch(win, r.Item.Path, MetaTitle(MetaFor(r.Item.Path)) ?? CleanTitle(r.Item.Title), fill);
                    win.Topmost = owner.Topmost;
                });
                addItem(L.T("📝 Uzupełnij informacje ręcznie (opis, plakat, ocena)…"), () =>
                {
                    var r = list.SelectedItem as LibRow; if (r == null || remoteOf.ContainsKey(r.Item)) return;
                    win.Topmost = false;
                    ShowMetaEditor(win, r.Item.Path, CleanTitle(r.Item.Title), fill);
                    win.Topmost = owner.Topmost;
                });
                addItem(L.T("📂 Pokaż w folderze"), () => { var r = list.SelectedItem as LibRow; if (r != null && !r.Missing) ShowInFolder(r.Item.Path); });
                addItem(L.T("💬 Pobierz napisy…"), () => { var r = list.SelectedItem as LibRow; if (r != null && !r.Missing) ShowSubtitleSearch(r.Item.Path, win); });
                addItem(L.T("⏮ Obejrzyj od początku"), () => { foreach (var r in sel()) r.Item.Pos = 0; var l = LoadLibrary(); foreach (var r in sel()) { var v = FindEntry(l, r.Item.Path, _libSharedView); if (v != null) v.Pos = 0; } SaveLibrary(l); play(); });
                if (_libTvView) { addItem(L.T("➖ Usuń z biblioteki (plik zostaje na dysku)"), remove); addItem(L.T("🗑 Usuń z biblioteki i z dysku…"), removeFiles); return; }   // nagrania TV - bez przenoszenia
                bool toShared = !_libSharedView;
                string target = toShared ? sharedName() : localName();
                // przenies: ten sam licznik obejrzenia; kopiuj: druga pozycja z wlasnym licznikiem (od zera)
                Action<bool> moveOrCopy = copy =>
                {
                    var l = LoadLibrary();
                    foreach (var r in sel().Where(r => !remoteOf.ContainsKey(r.Item)))
                    {
                        var v = FindEntry(l, r.Item.Path, !toShared); if (v == null || IsShared(v) == toShared) continue;
                        bool there = l.Any(x => x != v && string.Equals(x.Path, v.Path, StringComparison.OrdinalIgnoreCase) && IsShared(x) == toShared);
                        if (copy) { if (!there) l.Add(new LibraryVideo { Path = v.Path, Title = v.Title, Len = v.Len, Added = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Shared = toShared }); }
                        else if (there) l.Remove(v);
                        else v.Shared = toShared;
                    }
                    SaveLibrary(l); fill();
                };
                addItem((toShared ? "📚 " : "🔒 ") + L.T("Przenieś do: ") + target, () => moveOrCopy(false));
                addItem(L.T("📋 Kopiuj do: ") + target + L.T(" (osobny licznik)"), () => moveOrCopy(true));
                addItem(L.T("➖ Usuń z biblioteki (plik zostaje na dysku)"), remove);
                addItem(L.T("🗑 Usuń z biblioteki i z dysku…"), removeFiles);
            };
            list.ContextMenu = menu;
            var bar = new DockPanel { Margin = new Thickness(6), LastChildFill = false };
            DockPanel.SetDock(count, Dock.Left); bar.Children.Add(count);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right); bar.Children.Add(buttons);
            var contBtn = SmallButton(L.T("⏯ Kontynuuj"), ContinueLastFilm);   // ostatnio ogladany film od miejsca przerwania
            contBtn.ToolTip = LastFilm() is LibraryVideo lv ? L.T("Kontynuuj: ") + (MetaTitle(MetaFor(lv.Path)) ?? CleanTitle(lv.Title)) : null;
            contBtn.IsEnabled = contBtn.ToolTip != null;
            buttons.Children.Add(contBtn);
            buttons.Children.Add(SmallButton(L.T("▶ Odtwórz"), play));
            buttons.Children.Add(SmallButton(L.T("➕ Dodaj filmy…"), () => { var f = PickVideoFiles(true, win); if (f != null) AddPathsToLibrary(f, _libSharedView && !_libTvView); }));
            buttons.Children.Add(SmallButton(L.T("📁 Dodaj folder…"), () => { var d = PickFolder(win); if (d != null) AddPathsToLibrary(new[] { d }, _libSharedView && !_libTvView); }));
            buttons.Children.Add(SmallButton(L.T("➖ Usuń z biblioteki"), remove));
            // odswiez: wspolna biblioteka od nowa z innych komputerow + (gdy ustawiony) nowe dane z Jellyfin w tle
            buttons.Children.Add(SmallButton(L.T("🔄 Odśwież"), async () =>
            {
                _sharedCache = null;
                if (_libSharedView && !_libTvView) loadPeers(); else fill();
                var jc = LoadJellyfinConf();
                if (jc == null || string.IsNullOrEmpty(jc.Url)) return;
                ShowToast(L.T("🔄 Pobieram dane z Jellyfin…"), null);
                try { ShowToast(await JellyfinRefreshAsync(jc, _libSharedView, _libTvView, win), null); fill(); }
                catch (Exception ex) { App.LogError(ex); ShowToast(L.T("⚠ Jellyfin: ") + ex.Message, null); }   // serwer wylaczony - zostaje lokalna baza
            }));
            buttons.Children.Add(SmallButton(L.T("🎞 Jellyfin…"), () => ShowJellyfinDialog(win)));
            // opis zaznaczonego filmu (z Jellyfin): plakat, tytul, rok, ocena, gatunek, rezyser, obsada, opis
            var dPoster = new Image { Width = 180, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
            var dText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var dStack = new StackPanel { Margin = new Thickness(10, 4, 10, 4) }; dStack.Children.Add(dPoster); dStack.Children.Add(dText);
            var details = new ScrollViewer { Content = dStack, Width = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
            list.SelectionChanged += (s, a) =>
            {
                var r = list.SelectedItem as LibRow; var m = r != null ? MetaFor(r.Item.Path) : null;
                details.Visibility = m == null ? Visibility.Collapsed : Visibility.Visible;
                if (m == null) return;
                dPoster.Source = null;
                if (m.Poster != null) { var pf = System.IO.Path.Combine(PosterDir, m.Poster); if (File.Exists(pf)) dPoster.Source = LoadLogo(pf, 720); }
                dText.Inlines.Clear();
                dText.Inlines.Add(new System.Windows.Documents.Run(m.Name ?? "") { FontSize = 16, FontWeight = FontWeights.Bold });
                if (!string.IsNullOrEmpty(m.Original) && m.Original != m.Name) dText.Inlines.Add(new System.Windows.Documents.Run("\n" + m.Original) { FontStyle = FontStyles.Italic });
                var meta = string.Join("  ·  ", new[] { m.Year > 0 ? m.Year.ToString() : null, m.Rating > 0 ? "★ " + m.Rating.ToString("0.0") : null, m.Age, m.Genres.Count > 0 ? string.Join(", ", m.Genres) : null }.Where(x => !string.IsNullOrEmpty(x)));
                dText.Inlines.Add(new System.Windows.Documents.Run("\n" + meta) { Foreground = Brushes.Gray });
                if (m.Director != null) dText.Inlines.Add(new System.Windows.Documents.Run("\n" + L.T("Reżyseria: ") + m.Director) { Foreground = Brushes.Gray });
                if (m.People.Count > 0) dText.Inlines.Add(new System.Windows.Documents.Run("\n" + L.T("Obsada: ") + string.Join(", ", m.People)) { Foreground = Brushes.Gray });
                if (!string.IsNullOrEmpty(m.Overview)) dText.Inlines.Add(new System.Windows.Documents.Run("\n\n" + m.Overview));
            };
            var body = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); body.Children.Add(top);
            DockPanel.SetDock(bar, Dock.Bottom); body.Children.Add(bar);
            if (lib.Count == 0)
            {
                var empty = new TextBlock { Text = L.T("Biblioteka jest pusta. Dodaj filmy albo cały folder (np. Wideo) – przyciski na dole."), Margin = new Thickness(12, 4, 12, 4), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
                DockPanel.SetDock(empty, Dock.Top); body.Children.Add(empty);
            }
            DockPanel.SetDock(details, Dock.Right); body.Children.Add(details);
            body.Children.Add(list);
            win.Content = body;
            // przeciagnij pliki / foldery z Eksploratora na okno = dodaj do biblioteki
            win.AllowDrop = true;
            win.Drop += (s, a) => { if (a.Data.GetData(DataFormats.FileDrop) is string[] f) AddPathsToLibrary(f, _libSharedView && !_libTvView); };
            win.Closed += (s, a) => { _libWin = null; _libRefresh = null; };
            _libWin = win;
            win.Show();
        }

        // ---------- odtwarzacz ----------

        List<string> _playlist = new List<string>();
        int _playIndex;
        Action<int> _vlcLoad;
        Action<bool> _vlcQuiet;   // 🛋 okienko w Kanapie: bez paska i bez reakcji na mysz

        bool EnsureLibVlc()
        {
            var args = new List<string> { "--no-video-title-show", "--avcodec-hw=any", "--no-lua", "--no-osd", "--mmdevice-passthrough=0", "--no-spdif" };
            if (!Surround51) args.Add("--stereo-mode=1");
            var key = string.Join(" ", args);
            if (_libVlc != null && (key == _libVlcKey || _vlcWin != null)) return true;   // zmiana ustawien dzwieku - od nastepnego otwarcia odtwarzacza
            if (_libVlc != null) { var old = _libVlc; _libVlc = null; System.Threading.Tasks.Task.Run(async () => { await System.Threading.Tasks.Task.Delay(15000); try { old.Dispose(); } catch (Exception) { } }); }
            if (!VlcInstalled) { ShowToast(L.T("🎬 Odtwarzacz filmów nie jest zainstalowany"), null); return false; }
            try
            {
                LibVLCSharp.Shared.Core.Initialize(VlcDir);
                // bez tytulu filmu na obrazie; sprzetowe dekodowanie (lzejsze dla procesora); bez sieciowych uslug VLC
                // dzwiek 5.1 domyslnie miksowany do stereo: urzadzenie zglaszajace 5.1 (HDMI, soundbar), ktore gra tylko stereo, dawalo cisze
                // AC3/DTS dekodowane tutaj (bez passthrough) - urzadzenie, ktore go nie przyjmuje, dawalo "failed to create audio output" i cisze
                _libVlc = new LibVLC(args.ToArray()); _libVlcKey = key;
                // ostrzezenia i bledy VLC biezacego filmu w vlc.log (folder danych) - np. dlaczego nie ma dzwieku
                _libVlc.Log += (s, e) =>
                {
                    if (e.Message != null && e.Message.Contains("failed to create audio output")) _aoutFailed = true;
                    if (e.Message != null && VlcDriftRx.Match(e.Message) is var dm && dm.Success && long.TryParse(dm.Groups[1].Value, out var dus)) { _vlcDriftUs = dus; _vlcDriftAt = DateTime.Now; }
                    bool audio = e.Module != null && (e.Module.Contains("mmdevice") || e.Module.Contains("wasapi") || e.Module.Contains("directsound") || e.Module.Contains("a52") || e.Module == "main" && (e.Message ?? "").Contains("output"));
                    if (e.Level < LogLevel.Warning && !audio) return;   // szczegoly wyjscia dzwieku zawsze
                    VlcLogQueue(DateTime.Now.ToString("HH:mm:ss") + " " + e.Level + " " + e.Module + ": " + VlcLogSafe(e.Message) + "\n");   // zapis w tle - wątek obrazu/dźwięku VLC nie czeka na dysk
                };
                return true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                InfoBox(this, L.T("Nie udało się uruchomić odtwarzacza filmów:\n") + ex.Message);
                return false;
            }
        }

        void PlayVideos(string[] files, int index, bool library = true)
        {
            if (files == null || files.Length == 0 || !EnsureLibVlc()) return;
            _playlist = files.ToList();
            _playIndex = Math.Max(0, Math.Min(index, files.Length - 1));
            // film otwarty z dysku trafia do biblioteki (jak "ostatnio odtwarzane" w VLC); film z rozmowy komunikatora - nie
            var lib = LoadLibrary();
            if (library && AddToLibrary(lib, files.Where(f => !_remoteFilms.ContainsKey(f) && !_iptvPlaying.ContainsKey(f))) > 0) { SaveLibrary(lib); _libRefresh?.Invoke(); }   // filmy z innego komputera zostaja w jego bibliotece
            if (_vlcWin == null) BuildVlcWindow();
            _vlcLoad(_playIndex);
            if (_vlcWin.WindowState == WindowState.Minimized) _vlcWin.WindowState = WindowState.Normal;
            _vlcWin.Activate();
        }

        // ikony odtwarzacza rysowane wektorowo (ostre, jednakowe, bez kolorowych obwodek wygladzania czcionki na ciemnym tle)
        internal static readonly Dictionary<string, string> PlayerIcons = new Dictionary<string, string>
        {
            { "prev", "M6,6H8V18H6M9.5,12L18,6V18Z" },
            { "next", "M16,18H18V6H16M6,18L14.5,12L6,6Z" },
            { "play", "M8,5.14V19.14L19,12.14Z" },
            { "pause", "M14,19H18V5H14M6,19H10V5H6Z" },
            { "stop", "M6,6H18V18H6Z" },
            { "chback", "M12.5,8C9.85,8 7.45,9 5.6,10.6L2,7V16H11L7.38,12.38C8.77,11.22 10.54,10.5 12.5,10.5C16.04,10.5 19.05,12.81 20.1,16L22.47,15.22C21.08,11.03 17.15,8 12.5,8Z" },
            { "chfirst", "M18.41,16.59L13.82,12L18.41,7.41L17,6L11,12L17,18L18.41,16.59M6,6H8V18H6V6Z" },
            { "chhist", "M13.5,8H12V13L16.28,15.54L17,14.33L13.5,12.25V8M13,3A9,9 0 0,0 4,12H1L4.96,16.03L9,12H6A7,7 0 0,1 13,5A7,7 0 0,1 20,12A7,7 0 0,1 13,19C11.07,19 9.32,18.21 8.06,16.94L6.64,18.36C8.27,20 10.5,21 13,21A9,9 0 0,0 22,12A9,9 0 0,0 13,3Z" },
            { "subs", "M18,11H16.5V10.5H14.5V13.5H16.5V13H18V14A1,1 0 0,1 17,15H14A1,1 0 0,1 13,14V10A1,1 0 0,1 14,9H17A1,1 0 0,1 18,10M11,11H9.5V10.5H7.5V13.5H9.5V13H11V14A1,1 0 0,1 10,15H7A1,1 0 0,1 6,14V10A1,1 0 0,1 7,9H10A1,1 0 0,1 11,10M19,4H5C3.89,4 3,4.89 3,6V18A2,2 0 0,0 5,20H19A2,2 0 0,0 21,18V6C21,4.89 20.1,4 19,4Z" },
            { "audio", "M12,3V13.55C11.41,13.21 10.73,13 10,13A4,4 0 0,0 6,17A4,4 0 0,0 10,21A4,4 0 0,0 14,17V7H18V3H12Z" },
            { "aspect", "M19,12H17V15H14V17H19V12M7,9H10V7H5V12H7V9M21,3H3A2,2 0 0,0 1,5V19A2,2 0 0,0 3,21H21A2,2 0 0,0 23,19V5A2,2 0 0,0 21,3M21,19H3V5H21V19Z" },
            { "snap", "M4,4H7L9,2H15L17,4H20A2,2 0 0,1 22,6V18A2,2 0 0,1 20,20H4A2,2 0 0,1 2,18V6A2,2 0 0,1 4,4M12,7A5,5 0 0,0 7,12A5,5 0 0,0 12,17A5,5 0 0,0 17,12A5,5 0 0,0 12,7M12,9A3,3 0 0,1 15,12A3,3 0 0,1 12,15A3,3 0 0,1 9,12A3,3 0 0,1 12,9Z" },
            { "library", "M3,10H14V12H3V10M3,6H14V8H3V6M3,14H10V16H3V14M16,13V21L22,17L16,13Z" },
            { "full", "M5,5H10V7H7V10H5V5M14,5H19V10H17V7H14V5M17,14H19V19H14V17H17V14M10,17V19H5V14H7V17H10Z" },
            { "exitfull", "M14,14H19V16H16V19H14V14M5,14H10V19H8V16H5V14M8,5H10V10H5V8H8V5M19,8V10H14V5H16V8H19Z" },
            { "volume", "M14,3.23V5.29C16.89,6.15 19,8.83 19,12C19,15.17 16.89,17.84 14,18.7V20.77C18,19.86 21,16.28 21,12C21,7.72 18,4.14 14,3.23M16.5,12C16.5,10.23 15.5,8.71 14,7.97V16C15.5,15.29 16.5,13.76 16.5,12M3,9V15H7L12,20V4L7,9H3Z" },
            { "mute", "M12,4L9.91,6.09L12,8.18M4.27,3L3,4.27L7.73,9H3V15H7L12,20V13.27L16.25,17.53C15.58,18.04 14.83,18.46 14,18.7V20.77C15.38,20.45 16.63,19.82 17.68,18.96L19.73,21L21,19.73L12,10.73M19,12C19,12.94 18.8,13.82 18.46,14.64L19.97,16.15C20.62,14.91 21,13.5 21,12C21,7.72 18,4.14 14,3.23V5.29C16.89,6.15 19,8.83 19,12M16.5,12C16.5,10.23 15.5,8.71 14,7.97V10.18L16.45,12.63C16.5,12.43 16.5,12.21 16.5,12Z" },
            { "pin", "M16,12V4H17V2H7V4H8V12L6,14V16H11.2V22H12.8V16H18V14L16,12M8.8,14L10,12.8V4H14V12.8L15.2,14H8.8Z" },
            { "history", "M13.5,8H12V13L16.28,15.54L17,14.33L13.5,12.25V8M13,3A9,9 0 0,0 4,12H1L4.96,16.03L9,12H6A7,7 0 0,1 13,5A7,7 0 0,1 20,12A7,7 0 0,1 13,19C11.07,19 9.32,18.21 8.06,16.94L6.64,18.36C8.27,20 10.5,21 13,21A9,9 0 0,0 22,12A9,9 0 0,0 13,3Z" },
            { "mini", "M19,11H11V17H19V11M23,19V5C23,3.88 22.1,3 21,3H3A2,2 0 0,0 1,5V19A2,2 0 0,0 3,21H21A2,2 0 0,0 23,19M21,19H3V4.97H21V19Z" },
            { "exitmini", "M21,3H3A2,2 0 0,0 1,5V19A2,2 0 0,0 3,21H21A2,2 0 0,0 23,19V5A2,2 0 0,0 21,3M21,19H3V5H21V19M13,9H5V7H15V17H13V9Z" },
            { "close", "M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z" },
            { "lector", "M9,5A4,4 0 0,1 13,9A4,4 0 0,1 9,13A4,4 0 0,1 5,9A4,4 0 0,1 9,5M9,15C11.67,15 17,16.34 17,19V21H1V19C1,16.34 6.33,15 9,15M16.76,5.36C18.78,7.56 18.78,10.61 16.76,12.63L15.08,10.94C15.92,9.76 15.92,8.23 15.08,7.05L16.76,5.36M20.07,2C24,6.05 23.97,12.11 20.07,16L18.44,14.37C21.21,11.19 21.21,6.65 18.44,3.63L20.07,2Z" },
            { "pinned", "M16,12V4H17V2H7V4H8V12L6,14V16H11.2V22H12.8V16H18V14L16,12Z" },
            // ulubione, wylacznik, PiP i szybkie ikony - ten sam rysowany styl
            { "star", "M12,15.39L8.24,17.66L9.23,13.38L5.91,10.5L10.29,10.13L12,6.09L13.71,10.13L18.09,10.5L14.77,13.38L15.76,17.66M22,9.24L14.81,8.63L12,2L9.19,8.63L2,9.24L7.45,13.97L5.82,21L12,17.27L18.18,21L16.54,13.97L22,9.24Z" },
            { "starfill", "M12,17.27L18.18,21L16.54,13.97L22,9.24L14.81,8.62L12,2L9.19,8.62L2,9.24L7.45,13.97L5.82,21L12,17.27Z" },
            { "moon", "M2,12A10,10 0 0,0 12,22C16.5,22 20.3,19 21.5,14.9C20.4,15.6 19,16 17.5,16A8.5,8.5 0 0,1 9,7.5C9,6 9.4,4.6 10.1,3.5C6,4.7 2,7.5 2,12M4,12C4,9.3 5.4,7 7.4,5.7C7.1,6.3 7,6.9 7,7.5A10.5,10.5 0 0,0 17.5,18C18,18 18.5,18 19,17.9C17.5,19.2 14.9,20 12,20A8,8 0 0,1 4,12Z" },
            { "pip", "M4,4H16V6H6V16H4V4M8,8H20V20H8V8M10,10V18H18V10H10Z" },   // dwa nakladajace sie okna - inna niz tryb mini
            { "rec", "M12,20A8,8 0 0,1 4,12A8,8 0 0,1 12,4A8,8 0 0,1 20,12A8,8 0 0,1 12,20M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12,7A5,5 0 0,0 7,12A5,5 0 0,0 12,17A5,5 0 0,0 17,12A5,5 0 0,0 12,7Z" },
            { "recstop", "M12,20A8,8 0 0,1 4,12A8,8 0 0,1 12,4A8,8 0 0,1 20,12A8,8 0 0,1 12,20M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M9,9V15H15V9Z" },
            { "timer", "M12,20A7,7 0 0,1 5,13A7,7 0 0,1 12,6A7,7 0 0,1 19,13A7,7 0 0,1 12,20M19.03,7.39L20.45,5.97C20,5.46 19.55,5 19.04,4.56L17.62,6C16.07,4.74 14.12,4 12,4A9,9 0 0,0 3,13A9,9 0 0,0 12,22C17,22 21,17.97 21,13C21,10.88 20.26,8.93 19.03,7.39M11,14H13V8H11M15,1H9V3H15V1Z" },
            { "folder", "M20,18H4V8H20M20,6H12L10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6Z" },
            { "tv", "M21,17H3V5H21M21,3H3A2,2 0 0,0 1,5V17A2,2 0 0,0 3,19H8V21H16V19H21A2,2 0 0,0 23,17V5A2,2 0 0,0 21,3Z" },
            { "guide", "M19,19H5V8H19M16,1V3H8V1H6V3H5C3.89,3 3,3.89 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5C21,3.89 20.1,3 19,3H18V1M17,12H12V17H17V12Z" },
            { "info", "M11,9H13V7H11M12,20C7.59,20 4,16.41 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,16.41 16.41,20 12,20M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M11,17H13V11H11V17Z" },
            { "refresh", "M17.65,6.35C16.2,4.9 14.21,4 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20C15.73,20 18.84,17.45 19.73,14H17.65C16.83,16.33 14.61,18 12,18A6,6 0 0,1 6,12A6,6 0 0,1 12,6C13.66,6 15.14,6.69 16.22,7.78L13,11H20V4L17.65,6.35Z" },
            { "playlists", "M19,9H2V11H19V9M19,5H2V7H19V5M2,15H15V13H2V15M17,13V19L22,16L17,13Z" },
            { "tune", "M3,17V19H9V17H3M3,5V7H13V5H3M13,21V19H21V17H13V15H11V21H13M7,9V11H3V13H7V15H9V9H7M21,13V11H11V13H21M15,9H17V7H21V5H17V3H15V9Z" },
            { "eye", "M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9M12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5Z" },
            { "film", "M18,4L20,8H17L15,4H13L15,8H12L10,4H8L10,8H7L5,4H4A2,2 0 0,0 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V4H18Z" },
        };

        internal static System.Windows.Shapes.Path PlayerIcon(string name)
        {
            return new System.Windows.Shapes.Path { Data = Geometry.Parse(PlayerIcons[name]), Fill = Brushes.White, Width = _barIcon, Height = _barIcon, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        }
        const double BarIcon0 = 24;   // ikony paska przy 100 % (20 px było za małe w przyciskach 36 px)
        static double _barIcon = BarIcon0;   // wielkość ikon paska odtwarzacza (wysokość paska z ustawień 👁)

        // nowoczesny suwak (pasek postepu, glosnosc, PiP): cienka linia, niebieska czesc do uchwytu, okragly bialy uchwyt
        internal static Style ModernSliderStyle(string fill = "#3B82F6")   // fill: kolor wypełnienia (głośność - zielona)
        {
            return (Style)System.Windows.Markup.XamlReader.Parse((@"<Style TargetType='Slider' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Slider'>
  <Grid Height='16' Background='Transparent'><Border Height='4' CornerRadius='2' Background='#55FFFFFF' VerticalAlignment='Center'/>
    <Track x:Name='PART_Track' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
      <Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge'><RepeatButton.Template><ControlTemplate><Border Height='4' CornerRadius='2' Background='"+fill+@"' VerticalAlignment='Center'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
      <Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge'><RepeatButton.Template><ControlTemplate><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
      <Track.Thumb><Thumb><Thumb.Template><ControlTemplate><Ellipse Width='12' Height='12' Fill='White'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
    </Track></Grid></ControlTemplate></Setter.Value></Setter></Style>"));
        }

        // przyciski odtwarzacza: bez ramki, zaokraglone podswietlenie pod mysza, wylaczony = przygaszony
        internal static Style PlayerButtonStyle()
        {
            return (Style)System.Windows.Markup.XamlReader.Parse(@"<Style TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Setter Property='Foreground' Value='White'/><Setter Property='Background' Value='Transparent'/><Setter Property='Cursor' Value='Hand'/><Setter Property='Focusable' Value='False'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
    <Border x:Name='b' Background='{TemplateBinding Background}' CornerRadius='5' Padding='{TemplateBinding Padding}'>
      <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
    <ControlTemplate.Triggers>
      <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#33FFFFFF'/></Trigger>
      <Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Background' Value='#55FFFFFF'/></Trigger>
      <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.35'/></Trigger>
    </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        }

        // odtwarzacz, ktory teraz gra - synchronizacja LAN czeka, zeby nie przycinac obrazu (TV, filmy)
        static LibVLCSharp.Shared.MediaPlayer _playerMp;
        static bool PlayerBusy { get { try { return _playerMp != null && _playerMp.IsPlaying; } catch (Exception) { return false; } } }

        // 🕘 historia: 20 ostatnio oglądanych filmów alfabetycznie (ten sam film pod kilkoma ścieżkami - jedna pozycja); plakat, kiedy, ile obejrzano;
        // klik - odtwórz (z pytaniem o wznowienie); prawy przycisk - od początku, folder, usuń z historii, usuń z biblioteki
        static int _histSort;   // 🕘 historia: 0 - A–Z, 1 - ostatnio oglądany na górze, 2 - najstarszy na górze
        void ShowHistory(FrameworkElement anchor)
        {
            var pop = new System.Windows.Controls.Primitives.Popup { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, StaysOpen = false, AllowsTransparency = true };
            var lib = LoadLibrary();
            var groups = lib.Where(v => v.Last > 0 && v.Peer == null).GroupBy(v => ShownTitle(v, MetaFor(v.Path)), StringComparer.CurrentCultureIgnoreCase)
                .Select(g => (Title: g.Key, Main: g.OrderByDescending(v => v.Last).First(), All: g.ToList())).OrderByDescending(x => x.Main.Last).Take(20).ToList();
            groups = _histSort == 1 ? groups : _histSort == 2 ? groups.AsEnumerable().Reverse().ToList() : groups.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            void SortItems(ItemsControl menu)   // prawy przycisk: sortowanie listy
            {
                foreach (var (n, h) in new[] { (0, L.T("Sortuj: A–Z")), (1, L.T("Sortuj: ostatnio oglądany na górze")), (2, L.T("Sortuj: najstarszy na górze")) })
                {
                    var mi = new MenuItem { Header = h, IsCheckable = true, IsChecked = _histSort == n };
                    mi.Click += (q, w2) => { _histSort = n; pop.IsOpen = false; ShowHistory(anchor); };
                    menu.Items.Add(mi);
                }
            }
            var rows = new StackPanel();
            var head = new TextBlock { Text = "🕘 " + L.T("Ostatnio oglądane") + (groups.Count > 0 ? "  (" + groups.Count + ")" : ""), Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 10, 12, 6) };
            var tip = new TextBlock { Text = L.T("Klik – odtwórz · prawy przycisk – więcej"), Foreground = Brushes.Gray, FontSize = 11.5, Margin = new Thickness(12, 0, 12, 6) };
            pop.Child = new Border { Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x18, 0x1B, 0x24)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Width = 430,
                Child = new DockPanel { Children = { head, tip, new ScrollViewer { Content = rows, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(4, 0, 4, 6) } } } };
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(tip, Dock.Top);
            var hm = new ContextMenu(); SortItems(hm); ((Border)pop.Child).ContextMenu = hm;
            if (groups.Count == 0) rows.Children.Add(new TextBlock { Text = L.T("Historia jest pusta – obejrzyj jakiś film"), Foreground = Brushes.Silver, Margin = new Thickness(12) });
            var now = DateTime.Now;
            foreach (var g in groups)
            {
                var v = g.Main; var m = MetaFor(v.Path); var path = v.Path;
                var img = new Image { Width = 34, Height = 50, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                if (m?.Poster != null) { var pf = System.IO.Path.Combine(PosterDir, m.Poster); if (File.Exists(pf)) img.Source = LoadLogo(pf, 150); }
                var seen = v.Done ? L.T("✓ obejrzany") : v.Len > 0 && v.Pos > 0 ? L.T("obejrzano ") + Math.Min(99, v.Pos * 100 / v.Len) + "%  (" + FormatTime(v.Pos) + " / " + FormatTime(v.Len) + ")" : "";
                var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                txt.Children.Add(new TextBlock { Text = g.Title, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                txt.Children.Add(new TextBlock { Text = "🕘 " + LastPlayedText(v.Last, now) + (seen.Length > 0 ? "   ·   " + seen : ""), Foreground = Brushes.Silver, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                var dp = new DockPanel(); DockPanel.SetDock(img, Dock.Left); dp.Children.Add(img); dp.Children.Add(txt);
                var row = new Border { Child = dp, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = path };
                row.MouseEnter += (q, w2) => row.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x3B, 0x82, 0xF6));
                row.MouseLeave += (q, w2) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (q, w2) => { pop.IsOpen = false; PlayVideos(new[] { path }, 0); };
                var keys = new HashSet<string>(g.All.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
                void Change(Action<List<LibraryVideo>> act) { var l2 = LoadLibrary(); act(l2); SaveLibrary(l2); _libRefresh?.Invoke(); pop.IsOpen = false; ShowHistory(anchor); }
                var cm = new ContextMenu();
                void Item(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (q, w2) => a(); cm.Items.Add(mi); }
                Item("▶ " + L.T("Odtwórz"), () => { pop.IsOpen = false; PlayVideos(new[] { path }, 0); });
                Item("⏮ " + L.T("Odtwórz od początku"), () => { var l2 = LoadLibrary(); foreach (var x in l2.Where(x => keys.Contains(x.Path))) x.Pos = 0; SaveLibrary(l2); pop.IsOpen = false; PlayVideos(new[] { path }, 0); });
                Item("📂 " + L.T("Pokaż w folderze"), () => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch (Exception) { } });
                cm.Items.Add(new Separator());
                Item("🕘 " + L.T("Usuń z historii oglądania"), () => Change(l2 => { foreach (var x in l2.Where(x => keys.Contains(x.Path))) { x.Last = 0; x.Pos = 0; x.Done = false; } }));
                Item("🗑 " + L.T("Usuń z biblioteki (plik na dysku zostaje)"), () =>
                {
                    if (MessageBox.Show(Window.GetWindow(anchor) ?? this, L.T("Usunąć z biblioteki Velivo: ") + g.Title + "?\n\n" + L.T("Plik na dysku zostaje."), "Velivo", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    Change(l2 => l2.RemoveAll(x => keys.Contains(x.Path)));
                });
                cm.Items.Add(new Separator()); SortItems(cm);
                row.ContextMenu = cm;
                rows.Children.Add(row);
            }
            pop.IsOpen = true;
        }

        // ⭐ ulubione (kanały i filmy) w wyglądzie 🕘 historii: wiersz z obrazkiem, tytułem i opisem; klik - odtwórz, prawy przycisk - menu wiersza + sortowanie
        sealed class FavRow { public string Title, Sub, Tip; public ImageSource Img; public Func<System.Threading.Tasks.Task<ImageSource>> ImgAsync; public Action Play; public Action<ContextMenu> Menu; }
        System.Windows.Controls.Primitives.Popup FavPopup(FrameworkElement anchor, string title, string empty, List<FavRow> list, Action<ItemsControl> sortItems, double imgW, double imgH)
        {
            var pop = new System.Windows.Controls.Primitives.Popup { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, StaysOpen = false, AllowsTransparency = true };
            var rows = new StackPanel();
            var head = new TextBlock { Text = title + (list.Count > 0 ? "  (" + list.Count + ")" : ""), Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 10, 12, 6) };
            var tip = new TextBlock { Text = L.T("Klik – odtwórz · prawy przycisk – więcej"), Foreground = Brushes.Gray, FontSize = 11.5, Margin = new Thickness(12, 0, 12, 6) };
            pop.Child = new Border { Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x18, 0x1B, 0x24)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Width = 430,
                Child = new DockPanel { Children = { head, tip, new ScrollViewer { Content = rows, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(4, 0, 4, 6) } } } };
            DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(tip, Dock.Top);
            var hm = new ContextMenu(); sortItems(hm); ((Border)pop.Child).ContextMenu = hm;
            if (list.Count == 0) rows.Children.Add(new TextBlock { Text = empty, Foreground = Brushes.Silver, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap });
            foreach (var r in list)
            {
                var img = new Image { Width = imgW, Height = imgH, Stretch = imgW == imgH ? Stretch.Uniform : Stretch.UniformToFill, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Source = r.Img };
                if (r.Img == null && r.ImgAsync != null) { var ia = r.ImgAsync; Dispatcher.BeginInvoke(new Action(async () => { try { img.Source = await ia(); } catch (Exception) { } })); }
                var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                txt.Children.Add(new TextBlock { Text = r.Title, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                if (!string.IsNullOrEmpty(r.Sub)) txt.Children.Add(new TextBlock { Text = r.Sub, Foreground = Brushes.Silver, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                var dp = new DockPanel(); DockPanel.SetDock(img, Dock.Left); dp.Children.Add(img); dp.Children.Add(txt);
                var row = new Border { Child = dp, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = r.Tip };
                row.MouseEnter += (q, w2) => row.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x3B, 0x82, 0xF6));
                row.MouseLeave += (q, w2) => row.Background = Brushes.Transparent;
                var play = r.Play; row.MouseLeftButtonUp += (q, w2) => { pop.IsOpen = false; play(); };
                var cm = new ContextMenu(); r.Menu(cm); cm.Items.Add(new Separator()); sortItems(cm); row.ContextMenu = cm;
                rows.Children.Add(row);
            }
            pop.IsOpen = true;
            return pop;
        }
        static MenuItem FavItem(ItemsControl m, string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (q, w2) => a(); m.Items.Add(mi); return mi; }
        // sortowanie zapamiętane: 0 - A–Z, 1 - moja kolejność (przesuwanie w górę / w dół), 2 - ostatnio oglądany (tylko filmy)
        static int FavSort(string k) => int.TryParse(GetPref("sort-" + k), out var n) ? n : 0;
        void FavSortItems(ItemsControl menu, string k, bool films, System.Windows.Controls.Primitives.Popup[] pop, Action reopen)
        {
            var opts = new List<(int, string)> { (0, L.T("Sortuj: A–Z")), (1, L.T("Sortuj: moja kolejność")) };
            if (films) opts.Add((2, L.T("Sortuj: ostatnio oglądany na górze")));
            foreach (var (n, h) in opts) { var mi = FavItem(menu, h, () => { SetPref("sort-" + k, n.ToString()); if (pop[0] != null) pop[0].IsOpen = false; reopen(); }); mi.IsCheckable = true; mi.IsChecked = FavSort(k) == n; }
        }
        // ⭐ ulubione kanały TV
        void ShowFavChannels(FrameworkElement anchor)
        {
            var all = KnownPlaylist()?.Channels ?? new List<IptvChannel>();
            var favs = IptvSt.Fav.Select(u => _iptvPlaying.TryGetValue(u, out var c1) ? c1 : all.FirstOrDefault(c => c.Url == u)).Where(c => c != null).ToList();
            bool own = FavSort("ulubione-tv") == 1;
            if (!own) favs = favs.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var pop = new System.Windows.Controls.Primitives.Popup[1];
            void Reopen() => ShowFavChannels(anchor);
            void Move(string url, int d) { var l = IptvSt.Fav; int i = l.IndexOf(url), j2 = i + d; if (i < 0 || j2 < 0 || j2 >= l.Count) return; (l[i], l[j2]) = (l[j2], l[i]); SaveIptv(IptvSt); pop[0].IsOpen = false; Reopen(); }
            var rows = favs.Select((c, i) => { var url = c.Url; int i0 = i; var logo = c.Logo; return new FavRow
            {
                Title = c.Name, Sub = string.IsNullOrEmpty(c.Group) ? "" : "📺 " + c.Group, Tip = c.Name, ImgAsync = () => GetLogo(logo),
                Play = () => { foreach (var x in favs) _iptvPlaying[x.Url] = x; PlayVideos(favs.Select(x => x.Url).ToArray(), i0); },
                Menu = cm =>
                {
                    FavItem(cm, "▶ " + L.T("Odtwórz"), () => { pop[0].IsOpen = false; foreach (var x in favs) _iptvPlaying[x.Url] = x; PlayVideos(favs.Select(x => x.Url).ToArray(), i0); });
                    if (own) { FavItem(cm, "⬆ " + L.T("Przesuń wyżej"), () => Move(url, -1)); FavItem(cm, "⬇ " + L.T("Przesuń niżej"), () => Move(url, 1)); }
                    cm.Items.Add(new Separator());
                    FavItem(cm, "☆ " + L.T("Usuń z ulubionych"), () => { IptvSt.Fav.Remove(url); SaveIptv(IptvSt); pop[0].IsOpen = false; Reopen(); });
                }
            }; }).ToList();
            pop[0] = FavPopup(anchor, "⭐ " + L.T("Ulubione kanały"), L.T("Brak ulubionych kanałów – na kanale kliknij ⭐ albo prawy przycisk → Dodaj do ulubionych"), rows, m => FavSortItems(m, "ulubione-tv", false, pop, Reopen), 44, 32);
        }
        // ⭐ ulubione filmy (playlista „⭐ Ulubione”)
        void ShowFavFilms(FrameworkElement anchor)
        {
            const string favList = "⭐ Ulubione";
            var lib = LoadLibrary();
            var pls = LoadPlaylists(); var list = pls.Named.TryGetValue(favList, out var fl) ? fl : new List<string>();
            var favs = list.Where(File.Exists).ToList();
            Func<string, LibraryVideo> entry = f => FindEntry(lib, f, null);
            Func<string, string> title = f => { var v = entry(f); return v != null ? ShownTitle(v, MetaFor(v.Path)) : System.IO.Path.GetFileNameWithoutExtension(f); };
            int sort = FavSort("ulubione-filmy");
            if (sort == 0) favs = favs.OrderBy(title, StringComparer.CurrentCultureIgnoreCase).ToList();
            else if (sort == 2) favs = favs.OrderByDescending(f => entry(f)?.Last ?? 0L).ToList();
            var pop = new System.Windows.Controls.Primitives.Popup[1];
            void Reopen() => ShowFavFilms(anchor);
            void Save(Action<List<string>> act) { var p = LoadPlaylists(); if (!p.Named.TryGetValue(favList, out var l)) p.Named[favList] = l = new List<string>(); act(l); SavePlaylists(p); pop[0].IsOpen = false; Reopen(); }
            var now = DateTime.Now;
            var rows = favs.Select((f, i) => { int i0 = i; var v = entry(f); var m = MetaFor(f); ImageSource img = null;
                if (m?.Poster != null) { var pf = System.IO.Path.Combine(PosterDir, m.Poster); if (File.Exists(pf)) img = LoadLogo(pf, 150); }
                var seen = v == null ? "" : v.Done ? L.T("✓ obejrzany") : v.Len > 0 && v.Pos > 0 ? L.T("obejrzano ") + Math.Min(99, v.Pos * 100 / v.Len) + "%" : L.T("nieobejrzany");
                var sub = (m?.Year > 0 ? m.Year + "   ·   " : "") + seen + (v?.Last > 0 ? "   ·   🕘 " + LastPlayedText(v.Last, now) : "");
                return new FavRow
            {
                Title = title(f), Sub = sub, Tip = f, Img = img,
                Play = () => PlayVideos(favs.ToArray(), i0),
                Menu = cm =>
                {
                    FavItem(cm, "▶ " + L.T("Odtwórz"), () => { pop[0].IsOpen = false; PlayVideos(favs.ToArray(), i0); });
                    FavItem(cm, "⏮ " + L.T("Odtwórz od początku"), () => { var l2 = LoadLibrary(); var x = FindEntry(l2, f, null); if (x != null) { x.Pos = 0; SaveLibrary(l2); } pop[0].IsOpen = false; PlayVideos(favs.ToArray(), i0); });
                    FavItem(cm, "📂 " + L.T("Pokaż w folderze"), () => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + f + "\""); } catch (Exception) { } });
                    if (sort == 1) { FavItem(cm, "⬆ " + L.T("Przesuń wyżej"), () => Save(l => { int a = l.IndexOf(f), b = a - 1; if (a > 0) (l[a], l[b]) = (l[b], l[a]); })); FavItem(cm, "⬇ " + L.T("Przesuń niżej"), () => Save(l => { int a = l.IndexOf(f), b = a + 1; if (a >= 0 && b < l.Count) (l[a], l[b]) = (l[b], l[a]); })); }
                    cm.Items.Add(new Separator());
                    FavItem(cm, "☆ " + L.T("Usuń z ulubionych"), () => Save(l => l.RemoveAll(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))));
                }
            }; }).ToList();
            pop[0] = FavPopup(anchor, "⭐ " + L.T("Ulubione filmy"), L.T("Brak ulubionych filmów – podczas filmu kliknij ⭐"), rows, mn => FavSortItems(mn, "ulubione-filmy", true, pop, Reopen), 34, 50);
        }

        void BuildVlcWindow()
        {
            _barIcon = BarIcon0;   // ikony nowego okna od zwykłej wielkości - wysokość paska nakładana po zbudowaniu
            LibraryVideo current = null;   // film, ktory teraz gra (jego wpis w bibliotece)
            var mp = new LibVLCSharp.Shared.MediaPlayer(_libVlc) { EnableKeyInput = false, EnableMouseInput = false, Volume = 100 };
            _playerMp = mp;
            AbAttach(mp);   // 🎚 Audio Balance / korektor: dzwiek przez procesor Velivo (przed pierwszym Play)
            var view = new VideoView { MediaPlayer = mp, Background = Brushes.Black };
            var win = new Window { Title = L.T("Odtwarzacz filmów"), Width = 960, Height = 600, Background = Brushes.Black, Icon = Icon, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            // warstwa na obrazie (nad okienkiem VLC): dwuklik = pelny ekran, klik = pauza, ruch myszy = pokaz pasek
            var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
            var see = overlay.Background;   // prawie przezroczysta - widac film, a klik trafia w okno
            overlay.Background = Brushes.Black;   // bez filmu czarny ekran z logo Velivo (nie bialy)
            // logo rysowane wektorowo (ostre w kazdym rozmiarze): kwadrat z gradientem fiolet-blekit, biale "V" z liniami ruchu, napis Velivo
            var logoArt = new Canvas { Width = 150, Height = 150 };
            logoArt.Children.Add(new Border { Width = 150, Height = 150, CornerRadius = new CornerRadius(34),
                Background = new LinearGradientBrush(Color.FromRgb(0x7B, 0x2F, 0xF7), Color.FromRgb(0x38, 0xBD, 0xF8), 45) });
            logoArt.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M 62,38 L 88,108 L 114,38"), Stroke = Brushes.White, StrokeThickness = 20,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
            foreach (var (y, x0, op) in new[] { (58.0, 22.0, 1.0), (74.0, 30.0, 0.8), (90.0, 38.0, 0.6) })
                logoArt.Children.Add(new System.Windows.Shapes.Line { X1 = x0, X2 = x0 + 26, Y1 = y, Y2 = y, Stroke = Brushes.White, StrokeThickness = 6, Opacity = op,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            var bigLogo = new StackPanel { Opacity = 0.85, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            bigLogo.Children.Add(logoArt);
            bigLogo.Children.Add(new TextBlock { Text = "Velivo", FontSize = 52, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0),
                Foreground = new LinearGradientBrush(Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x63, 0x66, 0xF1), 0) });
            overlay.Children.Add(bigLogo);
            var osd = new TextBlock { Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(18), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 1, BlurRadius = 4 }, Visibility = Visibility.Collapsed };
            overlay.Children.Add(osd);
            view.Content = overlay;

            Brush fg = Brushes.White, dim = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C));
            var btnStyle = PlayerButtonStyle();
            Func<string, string, Button> glyphBtn = (icon, tip) => new Button
            {
                Content = PlayerIcon(icon), ToolTip = tip, Width = 40, Height = 36, Margin = new Thickness(1, 0, 1, 0), Style = btnStyle,
            };
            Func<string, string, Button> textBtn = (t, tip) => new Button
            {
                Content = t, ToolTip = tip, MinWidth = 46, Height = 36, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(1, 0, 1, 0), Style = btnStyle,
                FontSize = 14, FontWeight = FontWeights.SemiBold,
            };
            var timeNow = new TextBlock { Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), Text = "0:00", MinWidth = 52, TextAlignment = TextAlignment.Right };
            var timeLen = new TextBlock { Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), Text = "0:00", MinWidth = 52, TextAlignment = TextAlignment.Right };   // koniec rowno z "100%" ponizej
            var seek = new Slider { Minimum = 0, Maximum = 1000, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Style = ModernSliderStyle(), Cursor = Cursors.Hand };
            var seekRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(timeNow, Dock.Left); DockPanel.SetDock(timeLen, Dock.Right);
            // TV na zywo: zamiast paska postepu przesuwajacy sie napis - co leci i krotki opis
            var ticker = new Canvas { ClipToBounds = true, Height = 21, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var tickerText = new TextBlock { Foreground = fg, FontSize = 14.5, RenderTransform = new TranslateTransform(), CacheMode = new BitmapCache() };   // przesuwany jako gotowy obrazek - plynniej
            // przed napisem: nazwa stacji pogrubiona (stoi w miejscu) i cienki separator
            var tickerName = new TextBlock { Foreground = fg, FontSize = 14.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var tickerSep = new Border { Width = 1, Height = 16, Background = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            DockPanel.SetDock(tickerName, Dock.Left); DockPanel.SetDock(tickerSep, Dock.Left);
            ticker.Children.Add(tickerText);
            Action tickerRun = () =>
            {
                var tt = (TranslateTransform)tickerText.RenderTransform;
                tickerText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double w = tickerText.DesiredSize.Width, cw = Math.Max(50, ticker.ActualWidth);
                var anim = new System.Windows.Media.Animation.DoubleAnimation(cw, -w, TimeSpan.FromSeconds((cw + w) / 60.0)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };   // ok. 60 px/s - spokojnie, czytelnie
                tt.BeginAnimation(TranslateTransform.XProperty, anim);
            };
            ticker.SizeChanged += (s, e) => { if (ticker.Visibility == Visibility.Visible) tickerRun(); };
            seekRow.Children.Add(timeNow); seekRow.Children.Add(timeLen); seekRow.Children.Add(tickerName); seekRow.Children.Add(tickerSep);
            var seekArea = new Grid(); seekArea.Children.Add(seek); seekArea.Children.Add(ticker); seekRow.Children.Add(seekArea);   // wspolne pole wypelnia szerokosc: pasek postepu (film) albo napis (TV)

            var prev = glyphBtn("prev", L.T("Poprzedni (P)"));
            var play = glyphBtn("pause", L.T("Pauza / odtwarzaj (spacja)"));
            var stop = glyphBtn("stop", L.T("Stop"));
            var next = glyphBtn("next", L.T("Następny (N)"));
            var back10 = textBtn("−10 s", L.T("10 s wstecz (←)"));
            var fwd10 = textBtn("+10 s", L.T("10 s do przodu (→)"));
            var speed = textBtn("1×", L.T("Prędkość odtwarzania ([ i ])"));
            var subs = glyphBtn("subs", L.T("Napisy"));
            var lector = glyphBtn("lector", L.T("Lektor – czyta napisy na głos (Piper / głos Windows)"));
            var audio = glyphBtn("audio", L.T("Ścieżka dźwięku (język)"));
            var aspect = glyphBtn("aspect", L.T("Proporcje obrazu"));
            var snap = glyphBtn("snap", L.T("Zrzut klatki (zapis do Obrazów)"));
            var lib = glyphBtn("library", L.T("Biblioteka filmów"));
            var full = glyphBtn("full", L.T("Pełny ekran (F, dwuklik)"));
            var mute = glyphBtn("volume", L.T("Wycisz (M)"));
            var top = glyphBtn("pin", L.T("Zawsze na wierzchu"));   // tylko w menu (prawy przycisk) - na pasku 🕘 historia
            var hist = glyphBtn("history", L.T("Historia – ostatnio oglądane filmy (A–Z)"));
            var mini = glyphBtn("mini", L.T("Tryb mini – małe okienko na wierzchu (jak dla YouTube)"));
            var fav = glyphBtn("star", L.T("Ulubione – kanał do ⭐ Ulubionych, film do playlisty ⭐ Ulubione"));
            var sleep = glyphBtn("moon", L.T("Ekran i wyłącznik czasowy – zgaś ekran (film gra dalej) albo zatrzymaj po czasie")); sleep.Width = double.NaN; sleep.MinWidth = 40; sleep.Padding = new Thickness(8, 0, 8, 0);
            var pipBtn = glyphBtn("pip", L.T("Obraz w obrazie (PiP) – podgląd drugiego kanału TV"));
            var vol = new Slider { Minimum = 0, Maximum = 125, Value = 100, Width = 110, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, Style = ModernSliderStyle("#22C55E"), Cursor = Cursors.Hand, ToolTip = L.T("Głośność (↑ ↓, kółko myszy)") };   // zielona - inna niż pasek postępu
            var volText = new TextBlock { Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Width = 44, Margin = new Thickness(6, 0, 8, 0), Text = "100%" };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            // telewizja na zywo: zamiast -10 s / +10 s / predkosci - powrot po ostatnio przelaczanych kanalach (pamiec 20)
            var chBack = glyphBtn("chback", L.T("Poprzedni kanał – cofa po kolei po kanałach, które przełączałeś"));
            var chFirst = glyphBtn("chfirst", L.T("Wróć do pierwszego kanału – tego, od którego zacząłeś przełączać"));
            var chHistBtn = glyphBtn("chhist", L.T("Historia kanałów – ostatnie 20 (alfabetycznie)"));
            var filmNav = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var b in new UIElement[] { back10, fwd10, speed }) filmNav.Children.Add(b);
            var tvNav = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed }; foreach (var b in new UIElement[] { chBack, chFirst, chHistBtn }) tvNav.Children.Add(b);
            // grupy przycisków z podpisem pod spodem (Odtwarzanie, Telewizja, Film, …) - grupa bez widocznych przycisków znika razem z podpisem
            var visDp = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(UIElement));
            Func<string, UIElement[], FrameworkElement> grp = (cap, items) =>
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var it in items) row.Children.Add(it);
                var g = new StackPanel { Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Bottom };
                g.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(10), Padding = new Thickness(3, 2, 3, 2), Child = row });
                g.Children.Add(new TextBlock { Text = cap, FontSize = 11, Foreground = Brushes.Silver, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) });
                EventHandler upd = (s, e) => g.Visibility = items.Any(x => x.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
                foreach (var it in items) visDp.AddValueChanged(it, upd);
                upd(null, null); return g;
            };
            // podpis pod elementem (jak pod grupami) i ramka o wysokości grup - prawa strona paska w tym samym porządku co lewa
            Func<UIElement, string, FrameworkElement> capt = (el, t) => { var p = new StackPanel { Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Bottom }; p.Children.Add(el); p.Children.Add(new TextBlock { Text = t, FontSize = 11, Foreground = Brushes.Silver, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) }); return p; };
            Func<UIElement, Border> card = el => new Border { Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 0, 10, 0), Height = 40, Child = el };
            left.Children.Add(grp(L.T("Odtwarzanie"), new UIElement[] { prev, play, stop, next, filmNav, tvNav, hist, sleep }));
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            bool narrow = false; Action updMode = null;
            // ⚙ Obraz i dźwięk: napisy, lektor, ścieżka, proporcje, zrzut - w drugim rzędzie z podpisami (jedna ikona na pasku zamiast pięciu)
            Action<Button, string> withLabel = (b, t) =>
            {
                var ic = b.Content as UIElement; b.Content = null;   // ikona musi najpierw odejść od przycisku (inaczej wyjątek „już jest podrzędnym elementem”)
                var sp = new StackPanel { Orientation = Orientation.Horizontal }; sp.Children.Add(ic);
                sp.Children.Add(new TextBlock { Text = t, FontSize = 12, Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) });
                b.Content = sp; b.Width = double.NaN; b.Padding = new Thickness(8, 0, 8, 0);
            };
            withLabel(subs, L.T("Napisy")); withLabel(lector, L.T("Lektor")); withLabel(audio, L.T("Dźwięk")); withLabel(aspect, L.T("Proporcje")); withLabel(snap, L.T("Zrzut"));
            var toolsRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left,   // bez zawijania - WrapPanel + zmniejszanie paska (fitBar) przeliczały się w kółko
                Margin = new Thickness(4, 2, 4, 0), Visibility = GetPref("pasek-narzedzia") == "1" ? Visibility.Visible : Visibility.Collapsed };
            // ⚙ cztery zwijane grupy: Obraz, Dźwięk, Napisy, Kontroler (stan zapamiętany)
            var padBtn = textBtn("🎮 " + L.T("Przypisania"), L.T("Kontroler: co robi każdy przycisk, własne układy, przywróć domyślne")); padBtn.FontSize = 12;
            padBtn.Click += (s, e) => ShowPadMapDialog(win);
            var couchBtn = textBtn("🛋 " + L.T("Kanapa"), L.T("Tryb Kanapa – duże kafelki do obsługi kontrolerem (przycisk Menu ≡ na kontrolerze)")); couchBtn.FontSize = 12;
            couchBtn.Click += (s, e) => ShowKanapa();
            foreach (var cat in new (string Key, string Name, string Tip, UIElement[] Items)[] {
                ("obraz", L.T("Obraz"), L.T("Obraz: proporcje i skalowanie, zrzut klatki"), new UIElement[] { aspect, snap }),
                ("dzwiek", L.T("Dźwięk"), L.T("Dźwięk: ścieżka dźwięku i urządzenie"), new UIElement[] { audio }),
                ("napisy", L.T("Napisy"), L.T("Napisy: wybór, przesunięcie, wygląd, położenie i lektor"), new UIElement[] { subs, lector }),
                ("kontroler", L.T("Kontroler"), L.T("Kontroler: przypisania przycisków i tryb Kanapa"), new UIElement[] { padBtn, couchBtn }) })
            {
                var inner = new StackPanel { Orientation = Orientation.Horizontal, Visibility = GetPref("pasek-kat-" + cat.Key) == "0" ? Visibility.Collapsed : Visibility.Visible };
                foreach (var it in cat.Items) inner.Children.Add(it);
                var head = textBtn((inner.Visibility == Visibility.Visible ? "▾ " : "▸ ") + cat.Name, cat.Tip + "\n" + L.T("Kliknij, aby zwinąć / rozwinąć")); head.FontSize = 12; head.FontWeight = FontWeights.SemiBold;
                var key = cat.Key; var name = cat.Name;
                head.Click += (s, e) => { bool on = inner.Visibility != Visibility.Visible; inner.Visibility = on ? Visibility.Visible : Visibility.Collapsed; head.Content = (on ? "▾ " : "▸ ") + name; SetPref("pasek-kat-" + key, on ? "1" : "0"); };
                var box = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
                box.Children.Add(head); box.Children.Add(inner);
                toolsRow.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 6, 0), Child = box });
            }
            var tools = glyphBtn("tune", L.T("Ustawienia odtwarzacza – Obraz, Dźwięk, Napisy, Kontroler (pokaż / schowaj rząd)"));
            tools.Click += (s, e) => { bool on = toolsRow.Visibility != Visibility.Visible; toolsRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed; SetPref("pasek-narzedzia", on ? "1" : "0"); };
            // przełącznik TV ↔ Filmy: przy telewizji „🎬 Filmy” (biblioteka), przy filmie „📺 TV” (kanały) - zawsze w tym samym miejscu
            var swap = textBtn("🎬 " + L.T("Filmy"), L.T("Przełącz na filmy – biblioteka filmów\nPrawy przycisk: ostatnio oglądane filmy")); swap.FontSize = 13; swap.Margin = new Thickness(6, 0, 6, 0);
            bool swapTv = false;   // true = przycisk prowadzi do telewizji
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 4) };
            btnRow.Children.Add(left); btnRow.Children.Add(right);
            // pasek nakladany na obraz (jak w "Na wierzchu"): chowa sie po chwili bez ruchu myszy, film nie zmienia rozmiaru
            // tlo paska sterowania: przezroczystosc z ustawienia (ikona 👁), domyslnie 15%
            Func<int, Brush> barBg = pct => new SolidColorBrush(Color.FromArgb((byte)(255 * (100 - Math.Max(0, Math.Min(80, pct))) / 100), 0x1C, 0x1C, 0x1C));
            var controls = new StackPanel { Background = barBg(int.TryParse(GetPref("pasek-alfa"), out var ba) ? ba : 15), VerticalAlignment = VerticalAlignment.Bottom };
            // pasek postepu tak szeroki jak przyciski (konczy sie za "100%"); miejsce z prawej - na szybkie ikony
            var mainCol = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };   // do dolu - rzad przyciskow w jednej linii z szybkimi ikonami
            mainCol.Children.Add(toolsRow); mainCol.Children.Add(btnRow);   // pasek postępu - osobno, nad całym paskiem (ctlRow)
            // szybkie ikony z prawej (te same znaki co w menu pod prawym przyciskiem, rozmiar jak przyciski obok)
            // szybkie ikony z prawej - ten sam rysowany styl i wysokosc co przyciski obok
            Func<string, string, Button> qBtn = glyphBtn;
            Func<string, string, Button> qFavBtn = (badge, tip) =>
            {   // gwiazdka z malym znakiem (TV / film) w rogu
                var b = glyphBtn("star", tip); var g = new Grid { Width = 22, Height = 20 };
                var st = PlayerIcon("star"); st.Width = st.Height = 18; st.HorizontalAlignment = HorizontalAlignment.Left; st.VerticalAlignment = VerticalAlignment.Top;
                var bd = PlayerIcon(badge); bd.Width = bd.Height = 10; bd.HorizontalAlignment = HorizontalAlignment.Right; bd.VerticalAlignment = VerticalAlignment.Bottom;
                g.Children.Add(st); g.Children.Add(bd); b.Content = g; return b;
            };
            var qRec = qBtn("rec", L.T("Nagrywaj ten kanał TV")); var qRecStop = qBtn("recstop", L.T("Zatrzymaj nagrywanie")); var qRecSet = qBtn("timer", L.T("Zapas nagrywania (minuty przed i po audycji)"));
            var qRecDir = qBtn("folder", L.T("Folder z nagraniami")); var qTv = qBtn("tv", L.T("Kanały telewizji")); var qGuide = qBtn("guide", L.T("Pełny program TV"));
            var qInfo = qBtn("info", L.T("Pokaż pasek informacji (I)")); var qRefresh = qBtn("refresh", L.T("Odśwież kanał (F5)")); var qLists = qBtn("playlists", L.T("Playlisty i moja kolejka"));
            var qFavTv = qFavBtn("tv", L.T("Moje ulubione kanały TV")); var qFavFilm = qFavBtn("film", L.T("Moje ulubione filmy"));
            withLabel(qFavTv, L.T("Kanały")); withLabel(qFavFilm, L.T("Filmy"));
            qFavTv.ToolTip = L.T("Moje ulubione kanały TV") + "\n" + L.T("Prawy przycisk: wszystkie kanały tej grupy"); qFavFilm.ToolTip = L.T("Moje ulubione filmy") + "\n" + L.T("Prawy przycisk: wszystkie filmy");   // dwie gwiazdki z podpisem - od razu widać, która jest która
            var quick = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 2, 4, 4) };   // ta sama wysokosc co rzad przyciskow (btnRow: 2 u gory, 4 u dolu)
            var qBarTime = qBtn("eye", L.T("Pasek sterowania: jak długo zostaje widoczny i jak bardzo przezroczysty"));
            var qInfoSet = qBtn("tune", L.T("Pasek informacji: kiedy się pokazuje, znikanie, przezroczystość, wielkość tekstu"));
            right.Children.Add(grp(L.T("Telewizja"), new UIElement[] { qTv, qGuide, qRefresh }));
            right.Children.Add(grp(L.T("Nagrywanie"), new UIElement[] { qRec, qRecStop, qRecSet, qRecDir }));
            right.Children.Add(grp(L.T("Film"), new UIElement[] { lib, qLists }));
            // 🔊 przesunięcie dźwięku względem obrazu (jak J / K w VLC) - soundbar, Bluetooth, HDMI; prawy przycisk: wyrównaj teraz
            var aEarly = textBtn("🔊−", ""); var aLate = textBtn("🔊+", ""); aEarly.FontSize = aLate.FontSize = 13;
            right.Children.Add(grp(L.T("Obraz i dźwięk"), new UIElement[] { tools, pipBtn, mini, full, mute, vol, volText, aEarly, aLate }));
            var favGrp = grp(L.T("Ulubione"), new UIElement[] { fav, qFavTv, qFavFilm }); right.Children.Add(favGrp);
            right.Children.Add(grp(L.T("Widok"), new UIElement[] { qInfo, qBarTime, qInfoSet }));
            // przełącznik TV ↔ Filmy - niebieski, osobno na końcu (zawsze widać, jak przejść na drugą stronę)
            swap.Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)); swap.Margin = new Thickness(5, 0, 2, 0); swap.Height = 40;
            right.Children.Add(capt(swap, L.T("Przełącz")));
            var sofa = textBtn("🛋", L.T("Kanapa – duże kafelki na telewizor, sterowanie strzałkami, Enter i Esc (Home)")); sofa.Height = 40; sofa.Margin = new Thickness(2, 0, 2, 0); sofa.FontSize = 18;
            sofa.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xB5, 0x44)); sofa.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x04));   // złota jak w Kanapie - wyróżnia się obok niebieskiego „Przełącz”
            sofa.Click += (s, e) => ShowKanapa();
            right.Children.Add(capt(sofa, L.T("Kanapa")));
            // na koncu: separator, maly wykres pobierania z sieci (30 s) z predkoscia oraz godzina i data
            quick.Children.Add(new Border { Width = 1, Height = 36, Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(4, 0, 8, 18), VerticalAlignment = VerticalAlignment.Bottom });
            var netLine = new System.Windows.Shapes.Polyline { Stroke = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            var netGraph = new Canvas { Width = 120, Height = 28, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)) };
            var netUpLine = new System.Windows.Shapes.Polyline { Stroke = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };   // wysylanie (np. film dla drugiego komputera w LAN)
            netGraph.Children.Add(netLine); netGraph.Children.Add(netUpLine);
            var netText = new TextBlock { Foreground = Brushes.Silver, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 72, LineHeight = 14 };
            var nowTime = new TextBlock { Foreground = Brushes.White, FontSize = 26, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            var nowDate = new TextBlock { Foreground = Brushes.Silver, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
            var clockBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }; clockBox.Children.Add(nowTime); clockBox.Children.Add(nowDate);
            var netBox = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = L.T("Sieć – internet i LAN (ostatnie 30 s)\nKliknij: opcje połączenia i bufor kanałów\n↓ niebieska: pobieranie (TV, filmy z innego komputera)\n↑ zielona: wysyłanie (film dla drugiego komputera przy wspólnym oglądaniu)") };
            var netDown = new System.Windows.Documents.Run { Foreground = netLine.Stroke };
            var netUp = new System.Windows.Documents.Run { Foreground = netUpLine.Stroke };
            netText.Inlines.Add(netDown); netText.Inlines.Add(new System.Windows.Documents.LineBreak()); netText.Inlines.Add(netUp);
            netBox.Children.Add(netGraph); netBox.Children.Add(netText);
            // ⏱ koniec filmu / audycji TV i ● stan odbioru (z utraconych klatek i przerw strumienia) - między wykresem a zegarem
            var endRun = new System.Windows.Documents.Run { Foreground = Brushes.White }; var leftRun = new System.Windows.Documents.Run { Foreground = Brushes.Silver };
            var dotRun = new System.Windows.Documents.Run("●") { Foreground = Brushes.Gray }; var stateRun = new System.Windows.Documents.Run { Foreground = Brushes.Silver };
            var endText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,   // stała szerokość - zmiana tekstu co sekundę nie przesuwa i nie skaluje paska
               
                ToolTip = L.T("⏱ O której skończy się film (albo audycja TV według programu) i ile zostało.\n● Odbiór: Dobry – płynnie; Słaby – gubi klatki (przycina komputer albo łącze); Przerywa – strumień staje albo się buforuje.") };
            endText.Inlines.Add(endRun); endText.Inlines.Add(leftRun);   // 1. linijka: ⏱ do 16:20 · zostało 12 min
            var stateText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };   // 2. linijka: ● Dobry odbiór · jakość strumienia
            stateText.Inlines.Add(dotRun); stateText.Inlines.Add(new System.Windows.Documents.Run(" ")); stateText.Inlines.Add(stateRun);
            var lostHist = new Queue<int>();   // utracone klatki z ostatnich 10 s
            // za zegarem: film – plakat, ocena i gatunek (z Jellyfin); telewizja – logo stacji
            var badgeImg = new Image { Height = 66, MaxWidth = 120, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(badgeImg, BitmapScalingMode.HighQuality);
            var badgeRate = new TextBlock { Foreground = Brushes.Gold, FontSize = 16, FontWeight = FontWeights.SemiBold };
            var badgeGenre = new TextBlock { Foreground = Brushes.Silver, FontSize = 13, MaxWidth = 190, TextTrimming = TextTrimming.CharacterEllipsis };
            var badgeTxt = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; badgeTxt.Children.Add(badgeRate); badgeTxt.Children.Add(badgeGenre);
            var badge = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0), Visibility = Visibility.Collapsed };
            badge.Children.Add(badgeImg); badge.Children.Add(badgeTxt);
            int badgeSeq = 0; string badgeP = null; IptvChannel badgeCh = null;
            Func<string, IptvChannel, System.Threading.Tasks.Task> showBadge = async (p, ch) =>
            {
                int my = ++badgeSeq; badgeP = p; badgeCh = ch; ImageSource img = null; string rate = "", gen = "";
                try
                {
                    if (ch != null) img = string.IsNullOrEmpty(ch.Logo) ? null : await GetLogo(ch.Logo);
                    else
                    {
                        var m = MetaFor(p);
                        if (m != null)
                        {
                            if (m.Poster != null) { var pf = System.IO.Path.Combine(PosterDir, m.Poster); if (File.Exists(pf)) img = LoadLogo(pf, 120); }
                            rate = m.Rating > 0 ? "★ " + m.Rating.ToString("0.0") : ""; gen = m.Genres.Count > 0 ? string.Join(", ", m.Genres.Take(3)) : "";
                        }
                    }
                }
                catch (Exception ex) { App.LogError(ex); }
                if (my != badgeSeq) return;   // w międzyczasie inny film/kanał
                badgeImg.Source = img; badgeImg.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
                badgeRate.Text = rate; badgeGenre.Text = gen; badgeTxt.Visibility = rate.Length + gen.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                badge.ToolTip = (ch != null ? ch.Name : string.Join("\n", new[] { rate, gen }.Where(x => x.Length > 0))) + "\n" + (ch != null ? L.T("Kliknij: pełny opis programu") : L.T("Kliknij: opis filmu"));
                badge.Visibility = img != null || rate.Length + gen.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            // klik w plakat / logo: ładne okienko - film: opis z Jellyfin; TV: pełny opis programu, który leci, i co dalej
            badge.Cursor = Cursors.Hand; badge.Background = Brushes.Transparent;
            // okienko z opisem: film (Jellyfin) albo program TV, który leci - z plakatu za zegarem i z PiP
            Func<FrameworkElement, string, IptvChannel, ImageSource, System.Threading.Tasks.Task> showAbout = null;
            showAbout = async (anchor, infoP, infoCh, infoImg) =>
            {
                if (infoCh != null && infoImg == null && !string.IsNullOrEmpty(infoCh.Logo)) try { infoImg = await GetLogo(infoCh.Logo); } catch (Exception) { }
                var pop = new System.Windows.Controls.Primitives.Popup { PlacementTarget = anchor, Placement = anchor != null ? PlacementMode.Top : PlacementMode.MousePoint, StaysOpen = false, AllowsTransparency = true };
                var txt = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
                Action<string, double, Brush, FontWeight> line = (t, size, br, fw) => { if (!string.IsNullOrWhiteSpace(t)) txt.Children.Add(new TextBlock { Text = t.Trim(), FontSize = size, Foreground = br, FontWeight = fw, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) }); };
                var big = new Image { Source = infoImg, Width = infoCh != null ? 120 : 180, MaxHeight = 270, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
                if (infoCh != null)
                {
                    var l = EpgFor(_iptvEpg, infoCh); var now = DateTime.Now; var pr = EpgAt(l, now); var nx = pr == null ? null : l?.FirstOrDefault(x => x.Start >= pr.Stop);
                    line(infoCh.Name, 13, Brushes.Silver, FontWeights.Normal);
                    if (pr == null) line(L.T("Brak programu TV dla tego kanału."), 15, Brushes.White, FontWeights.Normal);
                    else
                    {
                        line(pr.Title, 20, Brushes.White, FontWeights.SemiBold);
                        line(string.Join("   ·   ", new[] { pr.Start.ToString("HH:mm") + "–" + pr.Stop.ToString("HH:mm"), pr.Sub, pr.Year, pr.Cats.Count > 0 ? string.Join(", ", pr.Cats.Take(3)) : null }.Where(x => !string.IsNullOrWhiteSpace(x))), 13, Brushes.Gold, FontWeights.Normal);
                        line(string.IsNullOrWhiteSpace(pr.Desc) ? L.T("Brak opisu tego programu.") : pr.Desc, 14, Brushes.Gainsboro, FontWeights.Normal);
                        if (nx != null) line(L.T("Potem: ") + nx.Start.ToString("HH:mm") + "  " + nx.Title, 13, Brushes.Silver, FontWeights.Normal);
                    }
                }
                else
                {
                    var m = MetaFor(infoP ?? "");
                    if (m == null) return;
                    if (m.Poster != null) { var pf = System.IO.Path.Combine(PosterDir, m.Poster); if (File.Exists(pf)) big.Source = LoadLogo(pf, 540); }
                    line(m.Name, 20, Brushes.White, FontWeights.SemiBold);
                    if (!string.IsNullOrEmpty(m.Original) && m.Original != m.Name) line(m.Original, 13, Brushes.Silver, FontWeights.Normal);
                    line(string.Join("   ·   ", new[] { m.Year > 0 ? m.Year.ToString() : null, m.Rating > 0 ? "★ " + m.Rating.ToString("0.0") : null, m.Age, m.Genres.Count > 0 ? string.Join(", ", m.Genres) : null }.Where(x => !string.IsNullOrWhiteSpace(x))), 13, Brushes.Gold, FontWeights.Normal);
                    line(string.IsNullOrWhiteSpace(m.Overview) ? L.T("Brak opisu tego filmu.") : m.Overview, 14, Brushes.Gainsboro, FontWeights.Normal);
                    if (!string.IsNullOrEmpty(m.Director)) line(L.T("Reżyseria: ") + m.Director, 13, Brushes.Silver, FontWeights.Normal);
                    if (m.People.Count > 0) line(L.T("Obsada: ") + string.Join(", ", m.People.Take(8)), 13, Brushes.Silver, FontWeights.Normal);
                }
                var dp = new DockPanel(); DockPanel.SetDock(big, Dock.Left); if (big.Source != null) dp.Children.Add(big);
                dp.Children.Add(new ScrollViewer { Content = txt, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                pop.Child = new Border { Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x18, 0x1B, 0x24)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Width = 640, Padding = new Thickness(14), Child = dp };
                pop.IsOpen = true;
            };
            badge.MouseLeftButtonUp += (s, e) => { e.Handled = true; _ = showAbout(badge, badgeP, badgeCh, badgeImg.Source); };
            var netGrp = capt(card(netBox), L.T("Sieć")); quick.Children.Add(netGrp);
            netBox.Cursor = Cursors.Hand; netBox.Background = Brushes.Transparent;
            long netLast = -1, netUpLast = -1; var netHist = new List<double>(); var netUpHist = new List<double>();
            // z prawej, nad wykresem sieci i zegarem: jakosc strumienia (rozdzielczosc, przeplywnosc, klatki, utracone klatki)
            var streamInfo = new TextBlock { Foreground = Brushes.Silver, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = L.T("Jakość strumienia: rozdzielczość, przepływność obrazu i dźwięku, klatki na sekundę.\nUtracone klatki (pomarańczowe) – przycina komputer; spadek Mb/s – przycina strumień od dostawcy.") };
            var line2 = new StackPanel { Orientation = Orientation.Horizontal }; line2.Children.Add(stateText);
            line2.Children.Add(new TextBlock { Text = "  ·  ", FontSize = 12, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center }); line2.Children.Add(streamInfo);
            var endCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 230 }; endCol.Children.Add(endText); endCol.Children.Add(line2);
            var endBox = capt(card(endCol), L.T("Czas i odbiór"));   // właściciel 2026-11-03: „wyrzuć” - ramka nie jest już dodawana do paska
            EventHandler endVis = (s, e) => endBox.Visibility = endText.Visibility; visDp.AddValueChanged(endText, endVis); endVis(null, null);   // bez filmu / kanału - cała ramka znika
            string techKey = null, techShort = "", techFull = "";   // dane techniczne biezacego filmu (raz na film)
            var netTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            bool netBusy = false;
            netTimer.Tick += async (s, e) =>
            {
                // zmiany tylko gdy naprawde inne - kazda zmiana przerysowuje cala warstwe paska (przesuwany napis by sie zacinal)
                var n = DateTime.Now; var tt0 = n.ToString("HH:mm"); if (nowTime.Text != tt0) { nowTime.Text = tt0; nowDate.Text = n.ToString("ddd d.MM"); }
                if (netBusy) return;
                long total = 0, sent = 0;
                // liczniki karty sieciowej w tle: pytanie Windows o wszystkie karty (VPN, Hyper-V, Bluetooth) trwa nawet
                // kilkadziesiat ms - w watku okna co sekunde przycinalo obraz TV i filmow
                netBusy = true;
                try
                {
                    var r = await System.Threading.Tasks.Task.Run(() =>
                    {
                        long t1 = 0, s1 = 0;
                        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                            if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                            { var st0 = ni.GetIPStatistics(); t1 += st0.BytesReceived; s1 += st0.BytesSent; }
                        return (t1, s1);
                    });
                    total = r.t1; sent = r.s1;
                }
                catch (Exception) { return; }
                finally { netBusy = false; }
                if (netLast >= 0 && controls.Visibility != Visibility.Visible) { netLast = total; netUpLast = sent; return; }   // pasek schowany - bez rysowania
                try
                {   // jakosc biezacego strumienia (statystyki VLC)
                    string info = "";
                    var md = mp.Media;
                    var pNow = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                    bool isFilm = pNow != null && !_iptvPlaying.ContainsKey(pNow);
                    if (md != null && isFilm && techKey != pNow && md.Tracks.Length > 0)
                    {   // film: dane techniczne raz na film - linia (krotko), podpowiedz (wszystko) i vlc.log
                        techKey = pNow;
                        var tr = md.Tracks; var vt = tr.FirstOrDefault(x => x.TrackType == TrackType.Video); var at = tr.Where(x => x.TrackType == TrackType.Audio).ToList();
                        long size = 0; try { if (File.Exists(pNow)) size = new FileInfo(pNow).Length; } catch (Exception) { }
                        var vS = vt.TrackType == TrackType.Video ? CodecName(vt.Codec) : "";
                        techShort = vS;   // w linii tylko kodek obrazu (miesci sie nad wykresem); dzwiek w podpowiedzi
                        var full = new System.Text.StringBuilder();
                        full.AppendLine(L.T("Plik: ") + System.IO.Path.GetFileName(pNow) + (size > 0 ? "  (" + (size / 1073741824.0 >= 1 ? (size / 1073741824.0).ToString("0.00") + " GB" : (size / 1048576.0).ToString("0") + " MB") + ")" : ""));
                        full.AppendLine(L.T("Kontener: ") + System.IO.Path.GetExtension(pNow).TrimStart('.').ToUpperInvariant() + L.T("   Długość: ") + FormatTime(mp.Length));
                        if (vt.TrackType == TrackType.Video) full.AppendLine(L.T("Obraz: ") + vS + "  " + vt.Data.Video.Width + "×" + vt.Data.Video.Height + (vt.Data.Video.FrameRateDen > 0 ? "  " + (vt.Data.Video.FrameRateNum / (double)vt.Data.Video.FrameRateDen).ToString("0.###") + L.T(" kl/s") : "") + (vt.Bitrate > 0 ? "  " + (vt.Bitrate / 1000000.0).ToString("0.0") + " Mb/s" : ""));
                        foreach (var a in at) full.AppendLine(L.T("Dźwięk: ") + CodecName(a.Codec) + "  " + ChannelsName(a.Data.Audio.Channels) + "  " + (a.Data.Audio.Rate / 1000.0).ToString("0.#") + " kHz" + (string.IsNullOrEmpty(a.Language) ? "" : "  [" + a.Language + "]") + (string.IsNullOrEmpty(a.Description) ? "" : "  " + a.Description));
                        int subsN = tr.Count(x => x.TrackType == TrackType.Text); if (subsN > 0) full.AppendLine(L.T("Napisy w pliku: ") + subsN);
                        techFull = full.ToString().TrimEnd();
                        try { lock (VlcLogFile) File.AppendAllText(VlcLogFile, "--- dane filmu ---\n" + VlcLogSafe(techFull.Replace("\r", "")) + "\n------------------\n"); } catch (Exception) { }   // przy zgloszeniu problemu od razu widac, co to za plik
                    }
                    if (md != null && (mp.IsPlaying || mp.State == VLCState.Paused))
                    {
                        uint vw = 0, vh = 0; mp.Size(0, ref vw, ref vh);
                        var st = md.Statistics;
                        double kbps = Math.Max(0, st.DemuxBitrate) * 8000;   // VLC: bajty na ms -> kb/s
                        var q = vh >= 2000 ? "4K" : vh >= 1000 ? "FHD" : vh >= 700 ? "HD" : vh > 0 ? "SD" : "";
                        int lost = st.LostPictures;
                        info = string.Join(" · ", new[] { q, kbps > 0 ? (kbps >= 1000 ? (kbps / 1000).ToString("0.0") + " Mb/s" : kbps.ToString("0") + " kb/s") : "" }.Where(x => x.Length > 0)) + (lost > 0 ? L.T("  ⚠ utracone: ") + lost : "");
                        var col = lost > 0 ? Brushes.Orange : Brushes.Silver; if (streamInfo.Foreground != col) streamInfo.Foreground = col;
                    }
                    if (streamInfo.Text != info) streamInfo.Text = info;
                    // ⏱ koniec i ● odbiór
                    string endS = null, leftS = "";
                    if (md != null && pNow != null && (mp.IsPlaying || mp.State == VLCState.Paused || mp.State == VLCState.Buffering))
                    {
                        Func<TimeSpan, string> dur = t => t.TotalMinutes >= 60 ? (int)t.TotalHours + L.T(" g ") + t.Minutes + L.T(" min") : Math.Max(1, (int)Math.Ceiling(t.TotalMinutes)) + L.T(" min");
                        if (isFilm && mp.Length > 0)
                        {
                            var left = TimeSpan.FromMilliseconds(Math.Max(0, mp.Length - mp.Time) / Math.Max(0.25, (double)mp.Rate));
                            endS = L.T("⏱ koniec ") + n.Add(left).ToString("HH:mm"); leftS = "  ·  " + L.T("zostało ") + dur(left);
                        }
                        else if (!isFilm && _iptvPlaying.TryGetValue(pNow, out var tch))
                        {
                            var at0 = tch.ArchiveStart.HasValue ? tch.ArchiveStart.Value.AddMilliseconds(Math.Max(0, mp.Time)) : n;
                            var pr = EpgAt(EpgFor(_iptvEpg, tch), at0);
                            if (pr != null) { var left = pr.Stop - at0; endS = L.T("⏱ do ") + n.Add(left).ToString("HH:mm"); leftS = "  ·  " + L.T("zostało ") + dur(left); }
                        }
                        int lostNow = md.Statistics.LostPictures; lostHist.Enqueue(lostNow); while (lostHist.Count > 10) lostHist.Dequeue();
                        int lostD = lostNow - lostHist.Peek(); double kb = Math.Max(0, md.Statistics.DemuxBitrate);
                        var (sTxt, sCol) = mp.State == VLCState.Buffering || (mp.IsPlaying && !isFilm && kb <= 0) ? (L.T("Przerywa"), Brushes.OrangeRed)
                            : lostD > 5 ? (L.T("Słaby odbiór"), Brushes.Orange) : (L.T("Dobry odbiór"), Brushes.LightGreen);
                        if (stateRun.Text != sTxt) { stateRun.Text = sTxt; dotRun.Foreground = sCol; }
                        if (endS == null) { endS = ""; }
                    }
                    else lostHist.Clear();
                    var vis = endS != null ? Visibility.Visible : Visibility.Collapsed;
                    if (endText.Visibility != vis) endText.Visibility = vis;
                    var endShow = endS == null ? null : endS.Length > 0 ? endS : L.T("⏱ na żywo");   // TV bez programu - sam stan odbioru
                    if (endShow != null && endRun.Text != endShow) endRun.Text = endShow;
                    if (leftRun.Text != leftS) leftRun.Text = leftS;
                    var tip = isFilm && techKey == pNow ? techFull + L.T("\n\nUtracone klatki (pomarańczowe) – przycina komputer.") : L.T("Jakość strumienia: rozdzielczość, przepływność obrazu i dźwięku, klatki na sekundę.\nUtracone klatki (pomarańczowe) – przycina komputer; spadek Mb/s – przycina strumień od dostawcy.");
                    if ((string)streamInfo.ToolTip != tip) streamInfo.ToolTip = tip;
                }
                catch (Exception) { }
                if (netLast >= 0)
                {
                    double bps = Math.Max(0, total - netLast), ups = Math.Max(0, sent - netUpLast);
                    netHist.Add(bps); if (netHist.Count > 30) netHist.RemoveAt(0);
                    netUpHist.Add(ups); if (netUpHist.Count > 30) netUpHist.RemoveAt(0);
                    double max = Math.Max(1, Math.Max(netHist.Max(), netUpHist.Max()));   // wspolna skala obu linii
                    Func<List<double>, PointCollection> line = h => { var pts = new PointCollection(); for (int k = 0; k < h.Count; k++) pts.Add(new Point(120 - (h.Count - 1 - k) * 4.0, 27 - 24 * h[k] / max)); return pts; };
                    netLine.Points = line(netHist); netUpLine.Points = line(netUpHist);
                    Func<double, string> sp = v => v >= 1048576 ? (v / 1048576).ToString("0.0") + " MB/s" : (v / 1024).ToString("0") + " KB/s";
                    netDown.Text = "↓ " + sp(bps); netUp.Text = "↑ " + sp(ups);
                }
                netLast = total; netUpLast = sent;
            };
            netTimer.Start(); win.Closed += (s, e) => netTimer.Stop();
            // nad szybkimi ikonami: znacznik nagrywania (czerwona pigulka, tylko gdy cos sie nagrywa) i licznik polaczen z dostawca
            var recPill = new Button { Cursor = Cursors.Hand, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(0), Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)), ToolTip = L.T("Trwające nagrania – kliknij: zatrzymaj albo przedłuż"),
                Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border Background='{TemplateBinding Background}' CornerRadius='10' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border></ControlTemplate>") };
            recPill.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.55, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });   // delikatne pulsowanie jak w dekoderze
            var connText = new Button { Cursor = Cursors.Hand, Padding = new Thickness(6, 0, 6, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.Silver, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
                Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border Background='Transparent' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border></ControlTemplate>") };
            var statusRow = new DockPanel { LastChildFill = false, Margin = new Thickness(12, 4, 4, 0), MinHeight = 21, VerticalAlignment = VerticalAlignment.Top };   // MinHeight: przycisk REC i połączenia (emoji) są wyższe niż 21 px - przy stałej wysokości wystawały i były ucięte od góry   // ta sama wysokosc i linia co rzad z czasem / NA ZYWO
            // separator nad dolnym separatorem (przed wykresem sieci)
            DockPanel.SetDock(recPill, Dock.Left); DockPanel.SetDock(connText, Dock.Left);
            statusRow.Children.Add(recPill); statusRow.Children.Add(connText);   // jakość strumienia - w ramce „Czas i odbiór”
            // układ: u góry pasek postępu (przez całą szerokość, do danych strumienia) i dane strumienia; pod spodem przyciski i szybkie ikony
            quick.VerticalAlignment = VerticalAlignment.Bottom;
            // dane strumienia rowno od lewej z wykresem sieci (zaraz za separatorem)
            SizeChangedEventHandler alignInfo = (s, e) =>
            {
                try
                {
                    var x = netBox.TranslatePoint(new Point(0, 0), statusRow).X;
                    // pasek postępu sięga do danych strumienia; przy nagrywaniu / połączeniach (napisy po lewej) - tylko nad przyciskami
                    bool free = recPill.Visibility != Visibility.Visible && connText.Visibility != Visibility.Visible;
                    double r = free && x > 0 ? statusRow.ActualWidth - x + 16 : statusRow.ActualWidth + statusRow.Margin.Left;
                    r = Math.Max(0, Math.Round(r));
                    if (Math.Abs(seekRow.Margin.Right - r) > 1) seekRow.Margin = new Thickness(0, 4, r, 0);
                }
                catch (InvalidOperationException) { }
            };
            statusRow.SizeChanged += alignInfo; quick.SizeChanged += alignInfo;
            recPill.IsVisibleChanged += (s, e) => alignInfo(s, null); connText.IsVisibleChanged += (s, e) => alignInfo(s, null);
            var ctlRow = new Grid();
            ctlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); ctlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ctlRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); ctlRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumnSpan(seekRow, 2); Grid.SetColumn(statusRow, 1); Grid.SetRow(mainCol, 1); Grid.SetRow(quick, 1); Grid.SetColumn(quick, 1);
            ctlRow.Children.Add(seekRow); ctlRow.Children.Add(statusRow); ctlRow.Children.Add(mainCol); ctlRow.Children.Add(quick);
            // zegar i plakat / logo stacji z prawej na wysokość obu wierszy - większe, czytelne z kanapy
            ctlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var rightBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 4, 10, 12) };
            var badgeSep = new Border { Width = 1, Height = 40, Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            EventHandler sepVis = (s, e) => badgeSep.Visibility = badge.Visibility; visDp.AddValueChanged(badge, sepVis); sepVis(null, null);
            rightBox.Children.Add(clockBox); rightBox.Children.Add(badgeSep); rightBox.Children.Add(badge);
            Grid.SetColumn(rightBox, 2); Grid.SetRowSpan(rightBox, 2); ctlRow.Children.Add(rightBox);
            // na obrazie (u gory na srodku, takze przy schowanym pasku): mala czerwona kropka i co sie nagrywa
            var recDot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            recDot.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(1)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            var recOnText = new TextBlock { Foreground = Brushes.White, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            var recOn = new Border { Background = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 10, 3), Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { recDot, recOnText } } };
            overlay.Children.Add(recOn);
            controls.Children.Add(ctlRow);
            overlay.Children.Add(controls);
            // przyciski szersze niż ekran (TV, niska rozdzielczość, duże skalowanie - CHUWI) - cały rząd zmniejszany, żeby nic nie było obcięte z prawej
            // największa potrzebna szerokość dla tej szerokości okna - zmienne napisy (prędkość sieci, Mb/s, opis filmu) nie zmieniają skali co sekundę (pasek „pływał”)
            double barNeed = 0, barHave = 0, barF = 1;
            // ↕ powiększony pasek za szeroki - po kolei: węższy wykres sieci, bez Sieci, bez opisu obok plakatu (Ulubione zawsze widać); po zmniejszeniu wracają
            Action<FrameworkElement, bool> hideEl = (el, on) => { el.MaxWidth = on ? 0 : double.PositiveInfinity; el.IsHitTestVisible = !on; };
            var stepW = new Func<double>[] { () => netGraph.ActualWidth * 0.5, () => netGrp.ActualWidth + netGrp.Margin.Left + netGrp.Margin.Right, () => badgeTxt.ActualWidth + badgeTxt.Margin.Left + badgeTxt.Margin.Right };
            var stepSet = new Action<bool>[] { on => netGraph.LayoutTransform = on ? new ScaleTransform(0.5, 1) : Transform.Identity, on => hideEl(netGrp, on), on => hideEl(badgeTxt, on) };
            var natW = new double[3]; var hidden = new bool[3];
            SizeChangedEventHandler fitBar = (s, e) =>
            {
                double have = overlay.ActualWidth;
                if (Math.Abs(have - barHave) > 1) { barHave = have; barNeed = 0; }
                double one = mainCol.ActualWidth + quick.ActualWidth + quick.Margin.Left + quick.Margin.Right + rightBox.ActualWidth + 14;
                double full = one;
                for (int i = 0; i < 3; i++) { if (!hidden[i]) { var w0 = stepW[i](); if (w0 > 0) natW[i] = w0; } else full += natW[i]; }
                var want = new bool[3]; double rest = full;
                if (barF > 1.001) for (int i = 0; i < 3; i++) { if (rest <= (hidden[i] ? have - 30 : have - 4)) break; if (natW[i] > 0) { want[i] = true; rest -= natW[i]; } }
                bool chg = false;
                for (int i = 0; i < 3; i++) if (want[i] != hidden[i]) { hidden[i] = want[i]; stepSet[i](want[i]); chg = true; }
                if (chg) { barNeed = 0; return; }   // po zmianie układu przyjdzie nowe SizeChanged z prawdziwymi szerokościami
                double need = barNeed = Math.Max(barNeed, one);
                double k = need > 0 && have > 0 && need > have ? Math.Floor(have / need * 20) / 20 : 1;   // skok co 5% - bez przeliczania paska w kółko (przycinało obraz)
                var cur = ctlRow.LayoutTransform as ScaleTransform;
                if (Math.Abs((cur?.ScaleX ?? 1) - k) > 0.001) ctlRow.LayoutTransform = k < 1 ? new ScaleTransform(k, k) : Transform.Identity;
            };
            overlay.SizeChanged += fitBar; mainCol.SizeChanged += fitBar; quick.SizeChanged += fitBar; rightBox.SizeChanged += fitBar;
            // ↕ wysokość paska (👁 → płynnie 100–160 %): wyższe przyciski, większe ikony, napisy i zegar - szerokość prawie bez zmian (ikony rosną w miejscu)
            var barBase = new Dictionary<object, double>();
            Action<double> applyBarHeight = null;
            applyBarHeight = f =>
            {
                double prevF = barF; barF = f; _barIcon = Math.Round(BarIcon0 * f); barNeed = 0;   // nowa wysokość - dopasowanie do okna od nowa (w obie strony)
                void walk(object o)
                {
                    switch (o)
                    {
                        case Button b: if (!barBase.ContainsKey(b)) barBase[b] = double.IsNaN(b.Height) ? 36 : b.Height; b.Height = Math.Round(barBase[b] * f);
                            if (!barBase.ContainsKey((b, "fs"))) barBase[(b, "fs")] = b.FontSize; b.FontSize = barBase[(b, "fs")] * f; walk(b.Content); return;
                        case System.Windows.Shapes.Path p: if (!barBase.ContainsKey(p)) barBase[p] = double.IsNaN(p.Width) ? BarIcon0 : p.Width / prevF; p.Width = p.Height = Math.Round(barBase[p] * f); return;   // ikona podmieniona później (⏸/▶) ma już bieżącą wielkość - podstawa = szerokość / poprzednia skala
                        case TextBlock t: if (!barBase.ContainsKey(t)) barBase[t] = t.FontSize; t.FontSize = barBase[t] * f; return;
                        case Image im: if (!barBase.ContainsKey(im)) barBase[im] = double.IsNaN(im.Height) ? 0 : im.Height; if (barBase[im] > 0) im.Height = Math.Round(barBase[im] * f); return;
                        case Slider: return;   // suwak głośności bez zmiany szerokości - wyższy pasek nie poszerza rzędu (inaczej cały rząd się zmniejszał i „pływał”)
                        case Panel pn: foreach (var c in pn.Children) walk(c); return;
                        case Decorator d: walk(d.Child); return;
                        case ContentControl cc: walk(cc.Content); return;
                    }
                }
                walk(ctlRow);
            };
            double barH0 = double.TryParse(GetPref("pasek-wysokosc"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bh0) ? Math.Max(1, Math.Min(1.6, bh0)) : 1;
            if (barH0 > 1.001) win.Loaded += (s, e) => applyBarHeight(barH0);
            // tryb mini: waski ciemny pasek u gory (tytul, powrot, zamknij), za niego przeciaga sie okienko
            var miniTitle = new TextBlock { Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var miniBack = glyphBtn("exitmini", L.T("Wróć do zwykłego okna"));
            var miniClose = glyphBtn("close", L.T("Zamknij"));
            foreach (var b in new[] { miniBack, miniClose }) { b.Width = 28; b.Height = 22; ((System.Windows.Shapes.Path)b.Content).Width = 14; ((System.Windows.Shapes.Path)b.Content).Height = 14; }
            var miniBtns = new StackPanel { Orientation = Orientation.Horizontal };
            miniBtns.Children.Add(miniBack); miniBtns.Children.Add(miniClose);
            var miniBar = new DockPanel { Height = 24, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x10, 0x10, 0x10)), Visibility = Visibility.Collapsed, Cursor = Cursors.SizeAll };
            DockPanel.SetDock(miniBtns, Dock.Right); miniBar.Children.Add(miniBtns); miniBar.Children.Add(miniTitle);
            overlay.Children.Add(miniBar);
            win.Content = view;
            // waskie okno: tylko najwazniejsze przyciski (reszta wraca po poszerzeniu)
            win.SizeChanged += (s, e) =>
            {
                double w = win.ActualWidth;
                narrow = w < 820; updMode?.Invoke();
                foreach (var b in new UIElement[] { speed, stop }) b.Visibility = w >= 600 ? Visibility.Visible : Visibility.Collapsed;
                foreach (var b in new UIElement[] { back10, fwd10, volText }) b.Visibility = w >= 480 ? Visibility.Visible : Visibility.Collapsed;
                vol.Width = w >= 480 ? 110 : 70;
            };

            bool seeking = false, full0 = false;
            long seekTo = -1; DateTime seekAt = DateTime.MinValue;   // po kliknięciu: VLC jeszcze chwilę podaje stary czas - suwak nie może wracać (wyglądało, jakby klik nie zadziałał)
            Func<long, bool> seekHold = t => seekTo >= 0 && (DateTime.Now - seekAt).TotalSeconds < 4 && Math.Abs(t - seekTo) > 3000;
            WindowState prevState = WindowState.Normal;
            float[] speeds = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 3f };
            int speedIdx = 3;
            var osdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };   // komunikaty (dzwiek, glosnosc...) - czas na przeczytanie
            osdTimer.Tick += (s, e) => { osdTimer.Stop(); osd.Visibility = Visibility.Collapsed; };
            Action<string> showOsd = t => { osd.Text = t; osd.Visibility = Visibility.Visible; osdTimer.Stop(); osdTimer.Start(); };
            netBox.MouseLeftButtonUp += (s, e) =>
            {   // opcje polaczenia z dostawca i bufor kanalow (zamiast w menu kontekstowym)
                var cm = new ContextMenu { PlacementTarget = netBox, Placement = PlacementMode.Top };
                cm.Items.Add(new MenuItem { Header = L.T("Opcje połączenia:"), IsEnabled = false });
                var o1 = new MenuItem { Header = L.T("Nagrywany kanał oglądaj z nagrania (jedno połączenie, pauza i cofanie)"), IsCheckable = true, IsChecked = IptvSt.WatchFromRec };
                o1.Click += (q, w2) => { IptvSt.WatchFromRec = !IptvSt.WatchFromRec; SaveIptv(IptvSt); };
                var o2 = new MenuItem { Header = L.T("Po 5 min pauzy zwolnij połączenie z dostawcą"), IsCheckable = true, IsChecked = IptvSt.PauseRelease };
                o2.Click += (q, w2) => { IptvSt.PauseRelease = !IptvSt.PauseRelease; SaveIptv(IptvSt); };
                cm.Items.Add(o1); cm.Items.Add(o2);
                cm.Items.Add(new Separator());
                // zapas bufora: wiecej = mniej przyciec, ale troche wieksze opoznienie i wolniejsze przelaczanie
                cm.Items.Add(new MenuItem { Header = L.T("Bufor kanałów:"), IsEnabled = false });
                foreach (var sec0 in new[] { 1, 3, 5, 10 })
                {
                    var sec = sec0; var bi = new MenuItem { Header = sec + " s" + (sec == 1 ? L.T(" (najmniejsze opóźnienie)") : sec == 3 ? L.T(" (zalecane przy przycięciach)") : ""), IsCheckable = true, IsChecked = IptvSt.Buffer == sec };
                    bi.Click += (q, w2) =>
                    {
                        IptvSt.Buffer = sec; SaveIptv(IptvSt); showOsd(L.T("Bufor: ") + sec + " s");
                        var pb = _playIndex >= 0 && _playIndex < _playlist.Count ? _playlist[_playIndex] : null;
                        if (pb != null && _iptvPlaying.ContainsKey(pb)) _vlcLoad(_playIndex);   // nowy bufor od razu tylko dla telewizji
                    };
                    cm.Items.Add(bi);
                }
                cm.IsOpen = true; e.Handled = true;
            };
            // czas widocznosci paska (ustawienie ikona na koncu paska); 0 = zawsze widoczny
            Func<double> barSec = () => double.TryParse(GetPref("pasek-czas"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bs) ? bs : 2.5;
            var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(barSec() > 0 ? barSec() : 2.5) };
            // chowanie paska: tylko gdy film gra, mysz nie jest nad paskiem i zadne menu (napisy, dzwiek) nie jest otwarte
            hideTimer.Tick += (s, e) =>
            {
                hideTimer.Stop();
                if (barSec() <= 0 || !mp.IsPlaying || controls.IsMouseOver || controls.ContextMenu != null && controls.ContextMenu.IsOpen || Mouse.Captured != null) { hideTimer.Start(); return; }
                if (miniBar.IsMouseOver) { hideTimer.Start(); return; }
                controls.Visibility = Visibility.Collapsed; miniBar.Visibility = Visibility.Collapsed; overlay.Cursor = Cursors.None;
            };
            bool mini0 = false;
            Action showBar = () => { controls.Visibility = Visibility.Visible; if (mini0) miniBar.Visibility = Visibility.Visible; overlay.Cursor = null; hideTimer.Stop(); hideTimer.Start(); };


            // zapis miejsca wznowienia co kilka sekund i przy zmianie filmu / zamknieciu
            Action savePos = () =>
            {
                if (current == null) return;
                // po Stop / koncu odtwarzacz podaje czas 0 - nie wolno nim nadpisac miejsca, w ktorym przerwano
                var st = mp.State;
                if (st != VLCState.Playing && st != VLCState.Paused && st != VLCState.Buffering) return;
                try
                {
                    long t = mp.Time, len = mp.Length;
                    if (_remoteFilms.TryGetValue(current.Path, out var rfs))   // film z innego komputera - postep do jego biblioteki
                    {
                        RecordProgress(current, t, len, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        SendRemoteProgress(rfs, t, len, false);
                        return;
                    }
                    // w tle, po kolei (pauza, zmiana filmu, zamknięcie nie przycinają obrazu); zamykanie Velivo - czekamy na zapis
                    SaveProgress(current.Path, IsShared(current), t, len, _mainClosing);
                    LibWrite(() => Dispatcher.BeginInvoke(new Action(() => _libRefresh?.Invoke())));   // lista biblioteki - po zapisie (schowana: raz po pokazaniu)
                }
                catch (Exception ex) { App.LogError(ex); }
            };
            var saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            // co 10 s miejsce w filmie zapisywane w tle (wczytanie i zapis calej biblioteki w watku okna przycinaly obraz);
            // przy pauzie, zmianie filmu i zamknieciu - jak dotad, od razu
            saveTimer.Tick += (s, e) =>
            {
                if (!mp.IsPlaying || current == null) return;
                if (_remoteFilms.ContainsKey(current.Path)) { savePos(); return; }   // film z innego komputera - postęp do jego biblioteki
                SaveProgress(current.Path, IsShared(current), mp.Time, mp.Length);   // w tle, bez przebudowy listy co 10 s
            };
            saveTimer.Start();

            // lektor: napisy z pliku obok filmu albo wczytane z menu Napisy
            var lc = new Lector();
            lc.Osd = t => win.Dispatcher.BeginInvoke(new Action(() => showOsd(t)));
            bool muted = false;
            Action<string, string> loadSubsText = (text, name) =>
            {
                lc.Cues = new List<SubCue>(); lc.File = null; lc.LastSpoken = -1; LectorStopRender(lc);
                if (text == null) return;
                try { lc.Cues = ParseSubtitles(text); lc.Lang = SubtitleLanguage(lc.Cues); lc.File = name; }
                catch (Exception ex) { App.LogError(ex); }
            };
            Action<string> loadSubs = path =>
            {
                string text = null;
                try { if (path != null) text = ReadSubtitleFile(path); } catch (Exception ex) { App.LogError(ex); }
                loadSubsText(text, path);
            };
            Action showLector = () => lector.Background = _settings.VlcLector ? new SolidColorBrush(Color.FromArgb(0x55, 0x3B, 0x82, 0xF6)) : Brushes.Transparent;
            // klik 🗣 = panel lektora: wlacz/wylacz, glosnosc, predkosc, sciszanie filmu (zapamietywane)
            // domyslne ustawienia (film bez wlasnych): te z ustawien Velivo - zmiany w filmie ich nie nadpisuja
            int defVol = _settings.VlcVolume, defDuck = _settings.VlcLectorDuck; double defLVol = _settings.VlcLectorVolume, defRate = _settings.VlcLectorRate; string defVoice = _settings.VlcLectorVoice; int defSteps = _settings.VlcLectorSteps;
            Action saveLector = () =>
            {
                try
                {
                    if (current == null) { defVol = _settings.VlcVolume; defDuck = _settings.VlcLectorDuck; defLVol = _settings.VlcLectorVolume; defRate = _settings.VlcLectorRate; defVoice = _settings.VlcLectorVoice; defSteps = _settings.VlcLectorSteps; _settings.Save(DataDir); return; }
                    // zapis pozostalych ustawien (lektor wl., napisy) z domyslnymi wartosciami filmu - ustawienia tego filmu ida do biblioteki
                    var keep = (_settings.VlcVolume, _settings.VlcLectorDuck, _settings.VlcLectorVolume, _settings.VlcLectorRate, _settings.VlcLectorVoice, _settings.VlcLectorSteps);
                    _settings.VlcVolume = defVol; _settings.VlcLectorDuck = defDuck; _settings.VlcLectorVolume = defLVol; _settings.VlcLectorRate = defRate; _settings.VlcLectorVoice = defVoice; _settings.VlcLectorSteps = defSteps;
                    try { _settings.Save(DataDir); } finally { (_settings.VlcVolume, _settings.VlcLectorDuck, _settings.VlcLectorVolume, _settings.VlcLectorRate, _settings.VlcLectorVoice, _settings.VlcLectorSteps) = keep; }
                }
                catch (Exception) { }
            };
            var lectorOn = new CheckBox { Content = L.T("Lektor włączony"), Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8), IsChecked = _settings.VlcLector };
            // zapis ustawien tego filmu (glosnosc + lektor) chwile po ostatniej zmianie
            var filmPrefsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            filmPrefsTimer.Tick += (s, e) =>
            {
                filmPrefsTimer.Stop(); saveLector();
                if (current == null) return;
                current.Vol = _settings.VlcVolume; current.LVol = _settings.VlcLectorVolume; current.LRate = _settings.VlcLectorRate; current.LDuck = _settings.VlcLectorDuck; current.LVoice = _settings.VlcLectorVoice; current.LSteps = _settings.VlcLectorSteps;
                if (_remoteFilms.TryGetValue(current.Path, out var rfp)) { SendRemoteProgress(rfp, mp.Time, mp.Length, false); return; }   // film z innego komputera - ustawienia do jego biblioteki
                var l = LoadLibrary(); var v = FindEntry(l, current.Path, IsShared(current));
                if (v == null) return;
                ApplyPrefs(v, PrefsOf(current));
                SaveLibrary(l);
            };
            Action filmPrefsChanged = () => { filmPrefsTimer.Stop(); filmPrefsTimer.Start(); };
            Func<string, double, double, Func<double, string>, Action<double>, Tuple<StackPanel, Slider>> sliderRow = (label, min, max, fmt, set) =>
            {
                var t = new TextBlock { Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0) };
                var sl = new Slider { Minimum = min, Maximum = max, Width = 240, IsMoveToPointEnabled = true };
                Action upd = () => t.Text = label + ": " + fmt(sl.Value);
                sl.ValueChanged += (a2, b2) => { set(sl.Value); upd(); filmPrefsChanged(); };
                upd();
                var row = new StackPanel(); row.Children.Add(t); row.Children.Add(sl); return Tuple.Create(row, sl);
            };
            var lectorPanel = new StackPanel { Margin = new Thickness(12) };
            lectorPanel.Children.Add(lectorOn);
            // 📜 kolejka: lektor czyta wszystkie kwestie po kolei, nie pomija ani nie urywa (może być w tyle za filmem)
            var lectorQueue = new CheckBox { Content = L.T("Czytaj wszystko po kolei (bez pomijania)"), Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8), IsChecked = _settings.VlcLectorQueue,
                ToolTip = L.T("Włączone: lektor czyta każdą kwestię do końca i w kolejności – gdy napisy są szybsze, czyta z opóźnieniem. Wyłączone: czyta tylko kwestię widoczną teraz na ekranie (zaległe pomija).") };
            lectorQueue.Click += (s, e) => { _settings.VlcLectorQueue = lectorQueue.IsChecked == true; _settings.Save(DataDir); };
            lectorPanel.Children.Add(lectorQueue);
            // napisy na obrazie mozna ukryc - lektor czyta je dalej (z pliku napisow)
            var showSubs = new CheckBox { Content = L.T("Pokazuj napisy na ekranie"), Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8), IsChecked = _settings.VlcShowSubs };
            lectorPanel.Children.Add(showSubs);
            // glos lektora: automatycznie (jezyk napisow), glosy Piper (niepobrany - pobierze), glosy Windows
            lectorPanel.Children.Add(new TextBlock { Text = L.T("Głos lektora:"), Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 2) });
            var voiceBox = new ComboBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
            voiceBox.Items.Add(new ComboBoxItem { Content = L.T("Automatycznie (język napisów)"), Tag = "" });
            foreach (var pv in PiperVoices) voiceBox.Items.Add(new ComboBoxItem { Content = (pv.St != null || pv.Xt != null ? "" : "Piper: ") + L.T(pv.Label) + (PiperVoiceInstalled(pv) ? "" : L.T(" – pobierz")), Tag = "piper:" + pv.Id });
            try { foreach (var wv in Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices) voiceBox.Items.Add(new ComboBoxItem { Content = "Windows: " + wv.DisplayName + " (" + wv.Language + ")", Tag = "win:" + wv.DisplayName }); } catch (Exception) { }
            Action<string> selectVoice = tag => voiceBox.SelectedItem = voiceBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (tag ?? "")) ?? voiceBox.Items[0];
            bool settingVoice = false;
            voiceBox.SelectionChanged += async (s, e) =>
            {
                if (settingVoice || !(voiceBox.SelectedItem is ComboBoxItem it)) return;
                var tag = (string)it.Tag;
                var pv = tag.StartsWith("piper:") ? PiperVoices.FirstOrDefault(x => "piper:" + x.Id == tag) : null;
                if (pv != null && !PiperVoiceInstalled(pv))
                {
                    showOsd(L.T("Pobieram głos ") + L.T(pv.Label) + "…");
                    if (!await EnsurePiperAsync(pv)) { settingVoice = true; selectVoice(_settings.VlcLectorVoice); settingVoice = false; return; }
                    it.Content = (pv.St != null || pv.Xt != null ? "" : "Piper: ") + L.T(pv.Label);
                }
                _settings.VlcLectorVoice = tag; LectorStopVoice(lc, mp); if (lc.CurIdx >= 0) lc.LastSpoken = Math.Min(lc.LastSpoken, lc.CurIdx - 1); lc.Gen++; LectorDropPre(lc); filmPrefsChanged(); AbLektorVoiceChanged(tag);   // 🗣 doradca: Twoje zwykłe ustawienia dla tego głosu - propozycja
            };
            lectorPanel.Children.Add(voiceBox);
            bool lekLoading = false;   // suwaki ustawiane przez program (wczytanie filmu, „Zastosuj”) - nie są Twoją korektą
            var lvRow = sliderRow(L.T("Głośność lektora"), 10, 99, v => (int)v + "%", v => { _settings.VlcLectorVolume = v / 100; if (lc.Voice != null) lc.Voice.Volume = WpfVol(_settings.VlcLectorVolume); if (!lekLoading) AbLektorUserSet(_settings.VlcLectorVoice, _settings.VlcLectorVolume, _settings.VlcLectorDuck); });
            var lrRow = sliderRow(L.T("Prędkość czytania"), 0.8, 2.0, v => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "×", v => _settings.VlcLectorRate = Math.Round(v * 10) / 10);
            var ldRow = sliderRow(L.T("Film podczas czytania"), 10, 100, v => v >= 100 ? L.T("bez ściszania") : (int)v + "%", v => { _settings.VlcLectorDuck = (int)v; if (!lekLoading) AbLektorUserSet(_settings.VlcLectorVoice, _settings.VlcLectorVolume, _settings.VlcLectorDuck); });
            foreach (var r in new[] { lvRow, lrRow, ldRow }) lectorPanel.Children.Add(r.Item1);
            var lekSmart = new CheckBox { Content = L.T("Inteligentne ściszanie filmu (na bieżąco)"), IsChecked = LectorSmart, Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0),
                ToolTip = L.T("Wł.: podczas czytania film ściszany płynnie tylko o tyle, ile trzeba, by lektor był wyraźny (cicha scena – wcale, głośna – mocniej, najwyżej do „Film podczas czytania”). Działa, gdy dźwięk idzie przez Audio Balance. Wył.: dawne stałe ściszenie na czas kwestii.") };
            lekSmart.Click += (s0, e0) => LectorSmart = lekSmart.IsChecked == true;
            lectorPanel.Children.Add(lekSmart);
            ldRow.Item1.Visibility = Visibility.Collapsed; lekSmart.Visibility = Visibility.Collapsed;   // film nie jest już ściszany pod lektora - bez martwych przełączników
            // 🗣 jakość głosu Supertonic (kroki): więcej = naturalniej, dłużej liczy; 0 = samo
            var stTxt = new TextBlock { Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0), ToolTip = L.T("Ile razy Supertonic dopracowuje głos: więcej – naturalniej, ale dłużej liczy. Samo: karta graficzna 8, procesor 5.") };
            var stSl = new Slider { Minimum = 0, Maximum = 16, Width = 240, IsMoveToPointEnabled = true, IsSnapToTickEnabled = true, TickFrequency = 1, Value = _settings.VlcLectorSteps };
            Action stUpd = () => stTxt.Text = L.T("Jakość głosu Supertonic") + ": " + (stSl.Value < 2 ? L.T("samo") : (int)stSl.Value + " " + L.T("kroków"));
            stSl.ValueChanged += (a2, b2) => { _settings.VlcLectorSteps = stSl.Value < 2 ? 0 : (int)stSl.Value; stUpd(); LectorDropPre(lc); filmPrefsChanged(); };   // osobno dla filmu
            stUpd(); lectorPanel.Children.Add(stTxt); lectorPanel.Children.Add(stSl);
            // 🗣 lektor dla calego filmu: sam w tle albo dopiero po ustawieniu glosu, tempa i jakosci - przyciskiem
            var autoBuild = new CheckBox { Content = L.T("Buduj lektora sam (cały film w tle)"), Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 6), IsChecked = _settings.VlcLectorAutoBuild,
                ToolTip = L.T("Włączone: po włączeniu filmu Velivo samo przygotowuje głos dla całego filmu. Wyłączone: najpierw ustaw głos, prędkość i jakość, potem kliknij „Zbuduj lektora teraz”.") };
            autoBuild.Click += (s, e) => { _settings.VlcLectorAutoBuild = autoBuild.IsChecked == true; _settings.Save(DataDir); LectorStopRender(lc); };   // od razu wg nowego wyboru (gotowe nagrania zostają na dysku)
            lectorPanel.Children.Add(autoBuild);
            var buildNow = new Button { Content = L.T("🗣 Zbuduj lektora teraz"), Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = L.T("Przygotowuje głos dla całego filmu z obecnymi ustawieniami (głos, prędkość, jakość). Wcześniej przygotowane nagrania w innych ustawieniach są pomijane.") };
            buildNow.Click += (s, e) => { LectorStopRender(lc); lc.BuildNow = true; };
            lectorPanel.Children.Add(buildNow);
            // 📁 folder nagran lektora - np. ramdysk, zeby nie mielic dysku
            Func<string> dirText = () => L.T("Folder nagrań: ") + (string.IsNullOrWhiteSpace(_settings.VlcLectorDir) ? L.T("domyślny (Dane\\lektor)") : _settings.VlcLectorDir);
            var dirLbl = new TextBlock { Text = dirText(), Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4) };
            lectorPanel.Children.Add(dirLbl);
            var dirRow = new StackPanel { Orientation = Orientation.Horizontal };
            var dirPick = new Button { Content = L.T("📁 Wybierz folder…"), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 0),
                ToolTip = L.T("Gdzie zapisywać nagrania lektora i pliki robocze głosu – np. ramdysk, żeby nie obciążać dysku. Nagrania liczą się od nowa w nowym miejscu.") };
            dirPick.Click += (s, e) => { var f = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(_settings.VlcLectorDir) ? _settings.VlcLectorDir : null }; if (f.ShowDialog(win) == true) { _settings.VlcLectorDir = f.FolderName; _settings.Save(DataDir); dirLbl.Text = dirText(); LectorStopRender(lc); } };
            var dirDef = new Button { Content = L.T("Domyślny"), Padding = new Thickness(8, 4, 8, 4), ToolTip = L.T("Wraca do folderu Dane\\lektor") };
            dirDef.Click += (s, e) => { _settings.VlcLectorDir = ""; _settings.Save(DataDir); dirLbl.Text = dirText(); LectorStopRender(lc); };
            dirRow.Children.Add(dirPick); dirRow.Children.Add(dirDef);
            lectorPanel.Children.Add(dirRow);
            // ustawienia filmu do panelu i odtwarzacza (przy wczytaniu filmu): zapisane dla filmu, inaczej ostatnio uzywane
            Action applyFilmPrefs = () =>
            {
                // film bez wlasnych ustawien - domyslne (nie te z poprzedniego filmu)
                lekLoading = true; lvRow.Item2.Value = (current?.LVol ?? AbLekLearnedVol(current?.LVoice ?? defVoice) ?? defLVol) * 100; lekLoading = false;   // 🧠 nowy film: głośność, której nauczyłeś Velivo dla tego głosu
                lrRow.Item2.Value = current?.LRate ?? defRate;
                lekLoading = true; ldRow.Item2.Value = current?.LDuck ?? defDuck; lekLoading = false;
                stSl.Value = current?.LSteps ?? defSteps;
                settingVoice = true; _settings.VlcLectorVoice = current?.LVoice ?? defVoice; selectVoice(_settings.VlcLectorVoice); settingVoice = false;
                vol.Value = current?.Vol ?? defVol;
                mp.Volume = muted ? 0 : (int)vol.Value;   // takze gdy suwak juz stal na tej wartosci (bez zdarzenia zmiany)
                filmPrefsTimer.Stop();
            };
            applyFilmPrefs();
            var lectorPop = new System.Windows.Controls.Primitives.Popup
            {
                Child = new Border { Child = lectorPanel, Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1C)), CornerRadius = new CornerRadius(6), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
                PlacementTarget = lector, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, StaysOpen = false, AllowsTransparency = true,
            };
            lectorOn.Click += (s, e) =>
            {
                _settings.VlcLector = lectorOn.IsChecked == true;
                if (!_settings.VlcLector) LectorStopVoice(lc, mp);
                showLector(); saveLector();
                showOsd(!_settings.VlcLector ? L.T("Lektor: wyłączony")
                    : lc.Cues.Count > 0 ? L.T("Lektor: czyta napisy ") + System.IO.Path.GetFileName(lc.File)
                    : L.T("Lektor: brak napisów obok filmu – wczytaj je w menu Napisy"));
            };
            lector.Click += (s, e) => { lectorOn.IsChecked = _settings.VlcLector; lectorPop.IsOpen = true; };
            // przesuniecie napisow na zywo: suwak i przyciski, okienko zostaje otwarte - widac od razu, czy napisy pasuja do mowy
            var sdText = new TextBlock { Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) };
            var sdSlider = new Slider { Minimum = -10, Maximum = 10, Width = 320, TickFrequency = 0.05, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true };
            bool sdSetting = false;
            var sdSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            sdSave.Tick += (s, e) =>
            {
                sdSave.Stop(); if (current == null || _remoteFilms.ContainsKey(current.Path)) return;
                current.SubDelay = mp.SpuDelay / 1000; var l = LoadLibrary(); var v = FindEntry(l, current.Path, IsShared(current)); if (v == null) return;
                v.SubDelay = current.SubDelay == 0 ? null : current.SubDelay; SaveLibrary(l);   // zapamietane dla tego filmu
            };
            Action<double> setSubDelay = sec =>
            {
                sec = Math.Round(Math.Max(-10, Math.Min(10, sec)) * 20) / 20;
                mp.SetSpuDelay((long)(sec * 1000000));
                sdText.Text = L.T("Przesunięcie napisów: ") + (sec > 0 ? "+" : "") + sec.ToString("0.00") + " s" + (sec > 0 ? L.T("  (później)") : sec < 0 ? L.T("  (wcześniej)") : "");
                sdSetting = true; sdSlider.Value = sec; sdSetting = false;
                sdSave.Stop(); sdSave.Start();
            };
            sdSlider.ValueChanged += (s, e) => { if (!sdSetting) setSubDelay(sdSlider.Value); };
            var sdPanel = new StackPanel { Margin = new Thickness(12) };
            var sdHead = new DockPanel();
            var sdClose = new Button { Content = "✕", Padding = new Thickness(6, 0, 6, 0), Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            DockPanel.SetDock(sdClose, Dock.Right); sdHead.Children.Add(sdClose); sdHead.Children.Add(sdText);
            sdPanel.Children.Add(sdHead); sdPanel.Children.Add(sdSlider);
            var sdBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var st0 in new[] { -1.0, -0.1, 0.0, 0.1, 1.0 })
            {
                var st = st0; var b = new Button { Content = st == 0 ? L.T("Zeruj") : (st > 0 ? "+" : "−") + Math.Abs(st).ToString("0.0") + " s", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
                b.Click += (s, e) => setSubDelay(st == 0 ? 0 : mp.SpuDelay / 1000000.0 + st);
                sdBtns.Children.Add(b);
            }
            sdPanel.Children.Add(sdBtns);
            sdPanel.Children.Add(new TextBlock { Text = L.T("Napisy za wcześnie → w prawo (+). Za późno → w lewo (−). Klawisze: G / H po 0,05 s."), Foreground = Brushes.Silver, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
            var subDelayPop = new System.Windows.Controls.Primitives.Popup
            {
                Child = new Border { Child = sdPanel, Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1C)), CornerRadius = new CornerRadius(6), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
                PlacementTarget = subs, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, StaysOpen = true, AllowsTransparency = true,
            };
            sdClose.Click += (s, e) => subDelayPop.IsOpen = false;
            Action openSubDelay = () => { setSubDelay(mp.SpuDelay / 1000000.0); subDelayPop.IsOpen = true; };
            win.Deactivated += (s, e) => subDelayPop.IsOpen = false;
            showLector();
            var lectorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            lectorTimer.Tick += (s, e) =>
            {
                if (muted && mp.Volume != 0) mp.Volume = 0;   // nowy film, sciszanie pod lektora itp. ustawiaja glosnosc - przy wyciszeniu pilnujemy zera
                if (_settings.VlcLector && !muted) LectorTick(lc, mp, () => (int)vol.Value);
            };
            lectorTimer.Start();
            // pamietanie napisow i sciezki dzwieku: dla tego filmu (biblioteka) i jako ostatni wybor dla kolejnych filmow
            Action<bool, string> rememberTrack = (spu, name) =>
            {
                if (spu) _settings.VlcSpu = name; else _settings.VlcAudio = name;
                saveLector();   // bez nadpisania domyslnych ustawien lektora ustawieniami tego filmu
                if (current == null) return;
                if (spu) current.Spu = name; else current.Audio = name;
                if (_remoteFilms.TryGetValue(current.Path, out var rft)) { SendRemoteProgress(rft, mp.Time, mp.Length, false); return; }
                var l = LoadLibrary(); var v = FindEntry(l, current.Path, IsShared(current));
                if (v == null) return;
                if (spu) v.Spu = name; else v.Audio = name;
                SaveLibrary(l);
            };
            Action applyTracks = () =>
            {
                try
                {
                    var spu = (current?.Spu != null ? PickTrack(mp.SpuDescription.Select(t => (t.Id, t.Name)), current.Spu) : PickTrack(mp.SpuDescription.Select(t => (t.Id, t.Name)), _settings.VlcSpu, false));
                    if (!_settings.VlcShowSubs) mp.SetSpu(-1);   // napisy ukryte (lektor czyta dalej)
                    else if (spu != int.MinValue) mp.SetSpu(spu);
                    else if (mp.Spu < 0) { var first = mp.SpuDescription.FirstOrDefault(t => t.Id >= 0); if (first.Name != null) mp.SetSpu(first.Id); }
                    mp.SetSpuDelay((current?.SubDelay ?? 0) * 1000);   // zapamietane przesuniecie napisow tego filmu
                    var au = current?.Audio != null ? PickTrack(mp.AudioTrackDescription.Select(t => (t.Id, t.Name)), current.Audio) : PickTrack(mp.AudioTrackDescription.Select(t => (t.Id, t.Name)), _settings.VlcAudio, false);   // inny film - tylko jezyk
                    if (au != int.MinValue && au >= 0) mp.SetAudioTrack(au);
                }
                catch (Exception ex) { App.LogError(ex); }
            };
            showSubs.Click += (s, e) =>
            {
                _settings.VlcShowSubs = showSubs.IsChecked == true; saveLector();
                applyTracks();
                showOsd(_settings.VlcShowSubs ? L.T("Napisy: widoczne") : L.T("Napisy: ukryte (lektor czyta)"));
            };
            // straznik kanalu na zywo: obraz stoi (czas sie nie przesuwa) albo strumien sie urwal - ponowne polaczenie
            string curRecFile = null; long recResume = -1; DateTime pausedAt = DateTime.Now;
            // zatrzymanie VLC poza wątkiem okna, po kolei: zatrzymanie zawieszonego strumienia TV trwało nawet kilka sekund (okno stało przy zmianie kanału i ⏹)
            var vlcTail = System.Threading.Tasks.Task.CompletedTask; int loadSeq = 0;
            Func<Action, System.Threading.Tasks.Task> vlcRun = a => vlcTail = vlcTail.ContinueWith(_ => { try { a(); } catch (Exception ex) { App.LogError(ex); } }, System.Threading.Tasks.TaskScheduler.Default);
            bool userStopped = false; long lastTvTime = -1; DateTime lastTvProgress = DateTime.Now; int tvRetries = 0;
            DateTime dFixed = DateTime.MinValue;   // ⏱ zapis zmierzonej różnicy dźwięku do vlc.log
            int aDelayMs = 0;   // zawsze od 0 - ręczne przesunięcie J/K tylko do zamknięcia odtwarzacza (zapamiętane dawało stałe opóźnienie na każdym kanale)
            Func<string> aDelayText = () => L.T("Dźwięk: ") + (aDelayMs > 0 ? "+" : "") + aDelayMs + " ms" + (aDelayMs > 0 ? L.T("  (później)") : aDelayMs < 0 ? L.T("  (wcześniej)") : "");
            Action aDelayTips = () =>
            {
                var now = "\n" + aDelayText() + "\n" + L.T("Prawy przycisk: wyrównaj teraz (R) i wróć do 0 ms");
                aEarly.ToolTip = L.T("Dźwięk wcześniej o 50 ms (J) – gdy głos jest spóźniony względem ust") + now;
                aLate.ToolTip = L.T("Dźwięk później o 50 ms (K) – gdy głos wyprzedza obraz") + now;
            };
            Action<int> setADelay = ms =>
            {
                aDelayMs = Math.Max(-5000, Math.Min(5000, ms));
                try { mp.SetAudioDelay(aDelayMs * 1000L); } catch (Exception) { }
                aDelayTips(); showOsd(aDelayText());
            };
            // ⇆ wyrównanie obrazu z dźwiękiem: film - skok w to samo miejsce (VLC opróżnia bufory i zaczyna synchronizację od nowa), TV - odświeżenie kanału
            Action resync = () =>
            {
                var pth = _playlist.Count > _playIndex ? _playlist[_playIndex] : null; if (pth == null) return;
                if (_iptvPlaying.ContainsKey(pth)) { showOsd(L.T("🔄 Wyrównuję obraz z dźwiękiem…")); _vlcLoad(_playIndex); return; }
                if (mp.Length <= 0 || !mp.IsSeekable) return;
                mp.Time = Math.Max(0, mp.Time - 200); showOsd(L.T("🔄 Wyrównuję obraz z dźwiękiem…"));
            };
            aEarly.Click += (s, e) => setADelay(aDelayMs - 50); aLate.Click += (s, e) => setADelay(aDelayMs + 50);
            MouseButtonEventHandler aReset = (s, e) => { e.Handled = true; setADelay(0); resync(); };
            aEarly.MouseRightButtonUp += aReset; aLate.MouseRightButtonUp += aReset; aDelayTips();
            mp.Playing += (s, e) => Dispatcher.BeginInvoke(new Action(() => { try { if (aDelayMs != 0) { mp.SetAudioDelay(aDelayMs * 1000L); showOsd(aDelayText()); } } catch (Exception) { } }));   // zapamiętane przesunięcie widoczne przy starcie - inaczej wygląda jak stałe opóźnienie   // każdy film / kanał z tym samym przesunięciem
            var tvWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            tvWatch.Tick += (s, e) =>
            {
                var pth = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                // opcja: ogladany kanal zaczal sie nagrywac - przejscie na plik nagrania (zostaje jedno polaczenie)
                if (pth != null && _iptvPlaying.ContainsKey(pth) && IptvSt.WatchFromRec && curRecFile == null && !userStopped && mp.State == VLCState.Playing && RecFileFor(pth) != null)
                { recResume = -1; _vlcLoad(_playIndex); return; }
                // opcja: dluga pauza na zywo - polaczenie z dostawca zwolnione (▶ laczy ponownie)
                if (pth != null && _iptvPlaying.ContainsKey(pth) && mp.State == VLCState.Paused && curRecFile == null && IptvSt.PauseRelease && (DateTime.Now - pausedAt).TotalMinutes >= 5)
                { userStopped = true; vlcRun(() => mp.Stop()); showOsd(L.T("⏸ Połączenie z dostawcą zwolnione po 5 min pauzy – ▶ wznawia")); return; }
                if (pth == null || !_iptvPlaying.ContainsKey(pth) || userStopped || mp.State == VLCState.Paused) { lastTvProgress = DateTime.Now; return; }
                // bez samoczynnych „naprawiaczy” dźwięku (przełączanie ścieżki, kanał od nowa) - same rozjeżdżały dźwięk; zostaje tylko zapis różnicy do vlc.log (co 30 s)
                if (mp.State == VLCState.Playing && (DateTime.Now - _vlcDriftAt).TotalSeconds < 3 && (DateTime.Now - dFixed).TotalSeconds > 30) { dFixed = DateTime.Now; try { lock (VlcLogFile) File.AppendAllText(VlcLogFile, DateTime.Now.ToString("HH:mm:ss") + " roznica dzwieku (pomiar VLC): " + (_vlcDriftUs / 1000) + " ms\n"); } catch (Exception) { } }
                if (mp.State == VLCState.Playing && mp.Time != lastTvTime) { lastTvTime = mp.Time; lastTvProgress = DateTime.Now; tvRetries = 0; return; }
                var wait = Math.Max(8, IptvSt.Buffer + 6) * (tvRetries >= 3 ? 2 : 1);   // po kilku probach rzadziej
                if ((DateTime.Now - lastTvProgress).TotalSeconds < wait) return;
                tvRetries++; lastTvProgress = DateTime.Now; lastTvTime = -1;
                showOsd(L.T("🔄 Obraz stanął – odświeżam kanał…") + (tvRetries > 1 ? " (" + tvRetries + ")" : ""));
                _vlcLoad(_playIndex);
            };
            tvWatch.Start();
            win.Closed += (s, e) => tvWatch.Stop();
            string curTitle = "";
            // pasek informacji przy kanale TV (jak w dekoderze): logo, kanal, program z godzinami, postep, opis, nastepny; znika sam
            var infoLogo = new Image { Width = 72, Height = 44, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
            var infoText = new StackPanel();
            var infoRow = new DockPanel(); DockPanel.SetDock(infoLogo, Dock.Left); infoRow.Children.Add(infoLogo); infoRow.Children.Add(infoText);
            var infoBox = new Border { Child = infoRow, CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(16, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed, IsHitTestVisible = false };   // pelna szerokosc okna
            // wielkosc tekstu: wg szerokosci okna (male okno - mniejszy, pelny ekran - wiekszy) razy ustawienie uzytkownika
            // wielkosc czcionek liczona przy budowaniu paska (bez skalowania warstwy - w oknie nakladanym na film potrafilo znikac)
            double infoK = 1;
            Action showChannelsRefresh = null;
            Action infoScale = () => infoK = Math.Max(0.65, Math.Min(1.7, win.ActualWidth / 1280.0)) * Math.Max(50, Math.Min(200, IptvSt.InfoScale)) / 100.0;
            Action<bool> showInfoRef = null; Func<bool> tvShownRef = null;
            var infoResize = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            infoResize.Tick += (s, e) => { infoResize.Stop(); if (infoBox.Visibility == Visibility.Visible) showInfoRef?.Invoke(true); };   // po zmianie rozmiaru - zbudowany od nowa
            win.SizeChanged += (s, e) => { infoResize.Stop(); infoResize.Start(); };
            overlay.Children.Add(infoBox);
            var infoTimer = new DispatcherTimer();
            infoTimer.Tick += (s, e) => { infoTimer.Stop(); infoBox.Visibility = Visibility.Collapsed; };
            string infoShownFor = null;
            Action<bool> showInfo = byUser =>
            {
                var pth = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                if (pth == null || mini0) { infoBox.Visibility = Visibility.Collapsed; return; }
                infoScale();
                bool roomy = win.ActualHeight >= 420;   // niskie okno - bez opisu, zeby pasek sie miescil
                if (!byUser && IptvSt.InfoSec == 0) return;   // tylko na zadanie
                if (!_iptvPlaying.TryGetValue(pth, out var ch))
                {
                    // zwykly film: tytul, miejsce / dlugosc, postep, ile zostalo i o ktorej koniec, nastepny z listy
                    infoBox.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Max(20, Math.Min(100, IptvSt.InfoAlpha)) / 100), 0x14, 0x16, 0x1A));
                    infoText.Children.Clear(); infoLogo.Source = null; infoLogo.Visibility = Visibility.Collapsed;
                    Func<string, double, FontWeight, Brush, TextBlock> ft = (t, size, w, b) => new TextBlock { Text = t, FontSize = size * infoK, FontWeight = w, Foreground = b, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                    var fm = MetaFor(pth);   // dane z Jellyfin (lokalna baza)
                    infoText.Children.Add(ft("🎬 " + (MetaTitle(fm) ?? (curTitle.Length > 0 ? curTitle : System.IO.Path.GetFileNameWithoutExtension(pth))), 20, FontWeights.Bold, Brushes.White));
                    if (fm != null)
                    {
                        var fmeta = string.Join("  ·  ", new[] { fm.Rating > 0 ? "★ " + fm.Rating.ToString("0.0") : null, fm.Genres.Count > 0 ? string.Join(", ", fm.Genres) : null, fm.Director != null ? L.T("reż. ") + fm.Director : null }.Where(x => x != null));
                        if (fmeta.Length > 0) infoText.Children.Add(ft(fmeta, 12.5, FontWeights.Normal, Brushes.Silver));
                        if (fm.Poster != null) { var pf = System.IO.Path.Combine(PosterDir, fm.Poster); if (File.Exists(pf)) { infoLogo.Source = LoadLogo(pf, 300); infoLogo.Visibility = Visibility.Visible; infoLogo.Height = 90 * infoK; infoLogo.Width = 64 * infoK; } }
                    }
                    long len = mp.Length, pos = Math.Max(0, mp.Time);
                    if (len > 0)
                    {
                        infoText.Children.Add(new ProgressBar { Height = 5, Maximum = len, Value = pos, Margin = new Thickness(0, 4, 0, 2), Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0) });
                        var left = TimeSpan.FromMilliseconds((len - pos) / Math.Max(0.1, mp.Rate));
                        infoText.Children.Add(ft(FormatTime(pos) + " / " + FormatTime(len) + L.T("  ·  zostało ") + FormatTime((long)left.TotalMilliseconds) + L.T("  ·  koniec o ") + DateTime.Now.Add(left).ToString("HH:mm"), 13.5, FontWeights.Normal, Brushes.Silver));
                    }
                    var extra = new List<string>();
                    if (lc.Cues.Count > 0 && lc.File != null) extra.Add(L.T("💬 napisy: ") + System.IO.Path.GetFileName(lc.File));
                    if (_settings.VlcLector && lc.Cues.Count > 0) extra.Add(L.T("🗣 lektor włączony"));
                    if (extra.Count > 0) infoText.Children.Add(ft(string.Join("   ·   ", extra), 12.5, FontWeights.Normal, Brushes.Silver));
                    if (roomy && fm != null && !string.IsNullOrEmpty(fm.Overview)) { var od = ft(fm.Overview, 13.5, FontWeights.Normal, Brushes.Gainsboro); od.MaxHeight = 58 * infoK; od.TextTrimming = TextTrimming.WordEllipsis; infoText.Children.Add(od); }
                    if (_playIndex < _playlist.Count - 1) infoText.Children.Add(ft("⏭ " + L.T("Następny: ") + System.IO.Path.GetFileNameWithoutExtension(_playlist[_playIndex + 1]), 13.5, FontWeights.SemiBold, Brushes.LightSkyBlue));
                    infoBox.Margin = new Thickness(16, 0, 16, (controls.ActualHeight > 0 ? controls.ActualHeight : 70) + 10);   // stale miejsce nad paskiem - nie opada, gdy pasek znika
                    infoBox.Visibility = Visibility.Visible; infoShownFor = null;
                    infoTimer.Stop(); infoTimer.Interval = TimeSpan.FromSeconds(IptvSt.InfoSec == 0 ? 8 : IptvSt.InfoSec); infoTimer.Start();
                    return;
                }
                infoLogo.Visibility = Visibility.Visible; infoLogo.Width = 72 * infoK; infoLogo.Height = 44 * infoK;
                // archiwum: "teraz" = miejsce w odtwarzanej audycji
                var l = EpgFor(_iptvEpg, ch); var now = ch.ArchiveStart.HasValue ? ch.ArchiveStart.Value.AddMilliseconds(Math.Max(0, mp.Time)) : DateTime.Now; var p0 = EpgAt(l, now); var nx = l?.FirstOrDefault(x => x.Start >= (p0?.Stop ?? now));
                infoBox.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Max(20, Math.Min(100, IptvSt.InfoAlpha)) / 100), 0x14, 0x16, 0x1A));
                infoText.Children.Clear();
                Func<string, double, FontWeight, Brush, TextBlock> tb = (t, size, w, b) => new TextBlock { Text = t, FontSize = size * infoK, FontWeight = w, Foreground = b, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                infoText.Children.Add(tb((IsRecording(pth) ? "⏺ REC   " : "") + (ch.ArchiveStart.HasValue ? L.T("⏪ Archiwum · ") + ch.ArchiveStart.Value.ToString("ddd d.MM") + " · " : HasCatchup(ch) ? "● " : "") + ch.Name, 15, FontWeights.SemiBold, ch.ArchiveStart.HasValue ? Brushes.LightGreen : Brushes.LightGray));
                if (p0 == null) infoText.Children.Add(tb(_iptvEpg == null ? L.T("Brak programu TV") : L.T("Brak programu dla tego kanału"), 16, FontWeights.Normal, Brushes.White));
                else
                {
                    infoText.Children.Add(tb("▶ " + p0.Start.ToString("HH:mm") + "–" + p0.Stop.ToString("HH:mm") + "   " + p0.Title + (string.IsNullOrEmpty(p0.Sub) ? "" : " – " + p0.Sub), 20, FontWeights.Bold, Brushes.White));
                    double total = (p0.Stop - p0.Start).TotalMinutes, done = (now - p0.Start).TotalMinutes;
                    var bar = new ProgressBar { Height = 5, Maximum = Math.Max(1, total), Value = Math.Max(0, done), Margin = new Thickness(0, 4, 0, 2), Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0) };
                    infoText.Children.Add(bar);
                    var meta = (int)done + L.T(" min z ") + (int)total + L.T(" · zostało ") + Math.Max(0, (int)Math.Ceiling(total - done)) + L.T(" min") + (p0.Cats.Count > 0 ? "  ·  " + string.Join(", ", p0.Cats) : "") + (!string.IsNullOrEmpty(p0.Year) ? "  ·  " + (p0.Year.Length > 4 ? p0.Year.Substring(0, 4) : p0.Year) : "");
                    infoText.Children.Add(tb(meta, 12.5, FontWeights.Normal, Brushes.Silver));
                    if (roomy && !string.IsNullOrEmpty(p0.Desc)) { var d = tb(p0.Desc, 13.5, FontWeights.Normal, Brushes.Gainsboro); d.MaxHeight = 58 * infoK; d.TextTrimming = TextTrimming.WordEllipsis; infoText.Children.Add(d); }
                }
                if (nx != null) infoText.Children.Add(tb("⏭ " + L.T("Następnie: ") + nx.Start.ToString("HH:mm") + "  " + nx.Title, 13.5, FontWeights.SemiBold, Brushes.LightSkyBlue));
                infoLogo.Source = null;
                var logo = !string.IsNullOrEmpty(ch.Logo) ? ch.Logo : ByChannel(_iptvIcons, ch);
                if (logo != null) GetLogo(logo).ContinueWith(t => { if (t.Result != null) infoLogo.Source = t.Result; }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                infoBox.Margin = new Thickness(16, 0, 16, (controls.ActualHeight > 0 ? controls.ActualHeight : 70) + 10);   // stale miejsce nad paskiem - nie opada, gdy pasek znika   // nad paskiem sterowania
                infoBox.Visibility = Visibility.Visible;
                infoShownFor = p0 != null ? pth + p0.Start.Ticks : pth;
                infoTimer.Stop();
                var sec = IptvSt.InfoSec == 0 ? 8 : IptvSt.InfoSec;
                infoTimer.Interval = TimeSpan.FromSeconds(sec); infoTimer.Start();
            };
            showInfoRef = showInfo;
            // program TV wczytal sie (okno TV / w tle) - od razu odswiez pasek informacji i napis, bez czekania na kolejny kanal
            Action onEpg = () => Dispatcher.BeginInvoke(new Action(() => { if (_vlcWin != win) return; if (infoBox.Visibility == Visibility.Visible) showInfo(false); tvShownRef?.Invoke(); }));
            _iptvLoaded += onEpg; win.Closed += (s, e) => _iptvLoaded -= onEpg;

            // lista kanalow nad filmem (z lewej): kanaly biezacej grupy z logo i tym, co leci; klik = przelacz, kolko = przewijanie
            var chList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            // wiersz bez jasnego podswietlenia Windows (bialy tekst byl nieczytelny) - pod mysza lekko jasniejsze tlo
            var chItemTpl = new ControlTemplate(typeof(ListBoxItem));
            var chBd = new FrameworkElementFactory(typeof(Border), "bd");
            chBd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            chBd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            chBd.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            chItemTpl.VisualTree = chBd;
            var chHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            chHover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), "bd"));
            chItemTpl.Triggers.Add(chHover);
            var chItemStyle = new Style(typeof(ListBoxItem)); chItemStyle.Setters.Add(new Setter(Control.TemplateProperty, chItemTpl));
            chList.ItemContainerStyle = chItemStyle;
            ScrollViewer.SetHorizontalScrollBarVisibility(chList, ScrollBarVisibility.Disabled);
            // wybor grupy na gorze listy (zapamietany): biezaca lista, ulubione, ostatnio ogladane, grupy z listy kanalow
            var chGroup = new ComboBox { Margin = new Thickness(6, 6, 6, 4) };
            var chSort = new ComboBox { Margin = new Thickness(6, 0, 6, 4) };   // sortowanie listy (zapamietane osobno dla filmow i kanalow)
            var chDock = new DockPanel(); DockPanel.SetDock(chGroup, Dock.Top); DockPanel.SetDock(chSort, Dock.Top); chDock.Children.Add(chGroup); chDock.Children.Add(chSort); chDock.Children.Add(chList);
            var chPanel = new Border { Child = chDock, Width = 340, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 0), Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(0, 8, 8, 0) };
            overlay.Children.Add(chPanel);
            // zegar na obrazie (filmy i TV): pozycja (rogi / srodek, gora / dol), wielkosc wzgledem okna, przezroczystosc - ustawienia zapamietane
            var clockText = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false };
            clockText.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.9, Color = Colors.Black };
            var clock = new Border { Child = clockText, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 2, 10, 4), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            overlay.Children.Add(clock);
            Action layoutClock = () =>
            {
                clock.Visibility = GetPref("zegar") == "1" ? Visibility.Visible : Visibility.Collapsed;
                if (clock.Visibility != Visibility.Visible) return;
                var pos = GetPref("zegar-poz") ?? "pg";   // [l|s|p][g|d]: lewy / srodek / prawy, gora / dol
                var zw = GetPref("zegar-wielk") ?? "2"; double k = zw == "0" ? 0.022 : zw == "1" ? 0.035 : zw == "3" ? 0.085 : 0.055;
                clockText.FontSize = Math.Max(10, win.ActualHeight * k);
                int alpha = int.TryParse(GetPref("zegar-alfa"), out var a) ? a : 85;
                clock.Opacity = Math.Max(0.08, Math.Min(1, alpha / 100.0));
                clock.Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));
                clock.HorizontalAlignment = pos[0] == 'l' ? HorizontalAlignment.Left : pos[0] == 's' ? HorizontalAlignment.Center : HorizontalAlignment.Right;
                clock.VerticalAlignment = pos.EndsWith("g") ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                double bottom = controls.Visibility == Visibility.Visible ? controls.ActualHeight + 12 : 16;   // nad paskiem sterowania
                clock.Margin = new Thickness(18, pos.EndsWith("g") ? 16 : 0, 18, pos.EndsWith("d") ? bottom : 0);
            };
            clockBox.Cursor = Cursors.Hand; clockBox.Background = Brushes.Transparent;
            clockBox.ToolTip = L.T("Kliknij: zegar na obrazie (pokazuj, pozycja, wielkość, przezroczystość)");
            clockBox.MouseLeftButtonUp += (s, e) =>
            {   // zegar na obrazie: wlacz, pozycja, wielkosc, przezroczystosc (zamiast w menu kontekstowym)
                var clM = new ContextMenu { PlacementTarget = clockBox, Placement = PlacementMode.Top };
                var on = new MenuItem { Header = L.T("Pokazuj zegar"), IsCheckable = true, IsChecked = GetPref("zegar") == "1" };
                on.Click += (q, w2) => { SetPref("zegar", on.IsChecked ? "1" : "0"); layoutClock(); };
                clM.Items.Add(on);
                Action<string, string, (string, string)[]> opts = (title, pref, items) =>
                {
                    var sub = new MenuItem { Header = title };
                    foreach (var it0 in items)
                    {
                        var it = it0; var mi = new MenuItem { Header = it.Item2, IsCheckable = true, IsChecked = (GetPref(pref) ?? (pref == "zegar-poz" ? "pg" : pref == "zegar-wielk" ? "2" : "85")) == it.Item1 };
                        mi.Click += (q, w2) => { SetPref(pref, it.Item1); SetPref("zegar", "1"); layoutClock(); };
                        sub.Items.Add(mi);
                    }
                    clM.Items.Add(sub);
                };
                opts(L.T("📍 Pozycja"), "zegar-poz", new[] { ("lg", L.T("↖ Lewy górny róg")), ("sg", L.T("⬆ Góra – środek")), ("pg", L.T("↗ Prawy górny róg")), ("ld", L.T("↙ Lewy dolny róg")), ("sd", L.T("⬇ Dół – środek")), ("pd", L.T("↘ Prawy dolny róg")) });
                opts(L.T("🔠 Wielkość"), "zegar-wielk", new[] { ("0", L.T("Bardzo mały")), ("1", L.T("Mały")), ("2", L.T("Średni")), ("3", L.T("Duży")) });
                opts(L.T("🌓 Przezroczystość"), "zegar-alfa", new[] { ("100", "0 %"), ("85", "15 %"), ("65", "35 %"), ("45", "55 %"), ("30", "70 %"), ("20", "80 %"), ("10", "90 %") });
                clM.IsOpen = true; e.Handled = true;
            };
            var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clockTimer.Tick += (s, e) => { if (clock.Visibility == Visibility.Visible) clockText.Text = DateTime.Now.ToString("HH:mm"); };
            clockTimer.Start(); clockText.Text = DateTime.Now.ToString("HH:mm");
            win.Closed += (s, e) => clockTimer.Stop();
            win.SizeChanged += (s, e) => layoutClock();
            controls.IsVisibleChanged += (s, e) => layoutClock();
            layoutClock();
            Action<IptvChannel> pipShow = null;   // PiP (ustawiane nizej) - z listy kanalow "Pokaz w PiP"
            var chHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            chHide.Tick += (s, e) => { chHide.Stop(); if (!chPanel.IsMouseOver && mp.State != VLCState.Stopped) chPanel.Visibility = Visibility.Collapsed; };   // po Stop lista zostaje
            chPanel.MouseLeave += (s, e) => { chHide.Stop(); chHide.Start(); };
            chPanel.MouseEnter += (s, e) => chHide.Stop();
            chPanel.MouseLeftButtonDown += (s, e) => e.Handled = true;   // klik na liscie to nie pauza
            bool? chTvMode = null;   // lista kanalow albo lista filmow (zmiana - nowe grupy)
            Action<bool> showChannels = null;
            // lista filmow nad filmem: biezaca lista / ostatnio ogladane / wszystkie filmy - plakat, tytul, rok, ocena, ile obejrzane
            // sortowanie listy nad filmem: wypelniane przy zmianie trybu (filmy / kanaly), wybor zapamietany
            Action<string, string[], string[]> fillSort = (pref, keys, names) =>
            {
                if (chSort.Items.Count > 0) return;
                for (int k = 0; k < keys.Length; k++) chSort.Items.Add(new ComboBoxItem { Content = L.T(names[k]), Tag = keys[k] });
                chSort.SelectedIndex = Math.Max(0, Array.IndexOf(keys, GetPref(pref) ?? keys[0]));
                chSort.Tag = pref;
            };
            chSort.SelectionChanged += (s, e) => { if (chSort.Tag is string pref && chSort.SelectedItem is ComboBoxItem si) { SetPref(pref, (string)si.Tag); showChannelsRefresh(); } };
            Action showFilms = () =>
            {
                const string fCur = "\u0001cur", fRec = "\u0001rec", fAll = "\u0001all", fShr = "\u0001shr";
                // grupy: kolejka, ostatnio ogladane, wszystkie, moja kolejka i zapisane playlisty (odswiezane, gdy sie zmienia)
                var pls = LoadPlaylists();
                // kolejka = cala biblioteka (film wlaczony z pelnej listy) - nie pokazujemy jej osobno, to "Wszystkie filmy"
                var libLocal = LoadLibrary().Count(v => v.Peer == null);
                bool realQueue = _playlist.Count > 1 && _playlist.Count < libLocal || _playlist.Count >= 1 && _remoteFilms.ContainsKey(_playlist[0]);
                var want = new List<(string Text, string Tag)> { (L.T("🕘 Ostatnio oglądane"), fRec), (L.T("🎬 Filmy na tym komputerze"), fAll), (L.T("🌐 Wspólna biblioteka"), fShr) };
                want.Insert(0, (L.T("▶ Kolejka") + (!realQueue ? L.T(" (pusta)") : _remoteFilms.ContainsKey(_playlist[0]) ? "" : " (" + _playlist.Count + ")"), fCur));
                if (pls.Mine.Count > 0) want.Add((L.T("📋 Moja kolejka") + " (" + pls.Mine.Count + ")", "\u0001mine"));
                foreach (var n in pls.Named.Keys.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)) want.Add(("🎞 " + n + " (" + pls.Named[n].Count + ")", "\u0001pl:" + n));
                if (!chGroup.Items.Cast<ComboBoxItem>().Select(i => (string)i.Content + "|" + i.Tag).SequenceEqual(want.Select(w => w.Text + "|" + w.Tag)))
                {
                    var keep = (chGroup.SelectedItem as ComboBoxItem)?.Tag as string;
                    chGroup.Tag = "busy";   // przebudowa grup - bez ponownego wypelniania listy
                    chGroup.Items.Clear();
                    foreach (var w in want) chGroup.Items.Add(new ComboBoxItem { Content = w.Text, Tag = w.Tag });
                    chGroup.SelectedItem = chGroup.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == keep);
                    chGroup.Tag = null;
                }
                fillSort("sort-lista-filmow", new[] { "az", "za", "ocena", "rok" }, new[] { "Sortuj: nazwa A–Z", "Sortuj: nazwa Z–A", "Sortuj: ocena", "Sortuj: rok (najnowsze)" });
                // otwarcie listy: biezaca lista, a gdy to tylko jeden film (start z "Kontynuuj", z dysku innego komputera) - wszystkie filmy
                if (chPanel.Visibility != Visibility.Visible)   // kazde otwarcie: ostatnio wybrana grupa (zapamietana), inaczej kolejka / wszystkie
                {
                    var keepF = GetPref("grupa-filmow"); var items = chGroup.Items.Cast<ComboBoxItem>();
                    chGroup.SelectedItem = items.FirstOrDefault(i => !string.IsNullOrEmpty(keepF) && (string)i.Tag == keepF && (keepF != fCur || realQueue)) ?? items.FirstOrDefault(i => (string)i.Tag == (realQueue ? fCur : fAll));
                }
                var fk = (chGroup.SelectedItem as ComboBoxItem)?.Tag as string ?? (realQueue ? fCur : fAll);
                var how = GetPref("sort-lista-filmow") ?? "az";
                var libAll = LoadLibrary(); var recDirs = TvRecDirs();   // nagrania TV - tylko w "Nagrane z TV"
                var curP = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                _remoteFilms.TryGetValue(curP ?? "", out var rfCur);
                bool remoteCur = fk == fCur && rfCur != null;
                if (fk == fCur && !realQueue)   // kolejka pusta - film wlaczony z calej biblioteki
                {
                    chSort.Visibility = Visibility.Collapsed; chList.Items.Clear();
                    chList.Items.Add(new ListBoxItem { Content = new TextBlock { Text = L.T("Kolejka jest pusta.\n\nW bibliotece zaznacz filmy (przeciągnij myszą albo Ctrl+klik) i wybierz pod prawym przyciskiem „Odtwórz zaznaczone jako kolejkę” albo „Dodaj do kolejki”."), Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) }, IsEnabled = false });
                    chPanel.Visibility = Visibility.Visible; chHide.Stop(); if (!chPanel.IsMouseOver) chHide.Start();
                    return;
                }   // film z innego komputera: biezaca lista = wspolna biblioteka tamtych komputerow
                List<LibraryVideo> films = fk == fCur ? (remoteCur ? new List<LibraryVideo>() : _playlist.Select(u => FindEntry(libAll, u, null) ?? new LibraryVideo { Path = u, Title = System.IO.Path.GetFileNameWithoutExtension(u) }).ToList())   // kolejnosc odtwarzania
                    : fk == fRec ? libAll.Where(v => v.Last > 0 && v.Peer == null).ToList()
                    : fk.StartsWith("\u0001mine") || fk.StartsWith("\u0001pl:") ? (fk.StartsWith("\u0001mine") ? pls.Mine : pls.Named.TryGetValue(fk.Substring(4), out var pl0) ? pl0 : new List<string>())
                        .Select(u => FindEntry(libAll, u, null) ?? new LibraryVideo { Path = u, Title = System.IO.Path.GetFileNameWithoutExtension(u) }).ToList()   // kolejnosc playlisty
                    : SortFilms(libAll.Where(v => v.Peer == null && IsShared(v) == (fk == fShr) && !IsTvRec(v.Path, recDirs))   // ten komputer albo wspólna biblioteka - osobno (były razem, każdy film dwa razy)
                        .GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()), v => v.Title, v => MetaFor(v.Path), how, v => v.MyTitle);
                // sortowanie nie dotyczy kolejnosci odtwarzania ani ostatnio ogladanych (zawsze od najnowszego)
                chSort.Visibility = fk == fRec || fk == fCur && !remoteCur || fk.StartsWith("\u0001mine") || fk.StartsWith("\u0001pl:") ? Visibility.Collapsed : Visibility.Visible;
                chList.Items.Clear(); ListBoxItem cur = null;
                Func<MovieInfo, string, string, ListBoxItem> mkRow = (m, raw, sub) =>
                {
                    var img = new Image { Width = 34, Height = 50, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                    if (m?.Poster != null) { var pf = System.IO.Path.Combine(PosterDir, m.Poster); if (File.Exists(pf)) img.Source = LoadLogo(pf, 150); }
                    var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    txt.Children.Add(new TextBlock { Text = raw, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
                    if (sub.Length > 0) txt.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Brushes.Silver, TextTrimming = TextTrimming.CharacterEllipsis });
                    var dp = new DockPanel(); DockPanel.SetDock(img, Dock.Left); dp.Children.Add(img); dp.Children.Add(txt);
                    return new ListBoxItem { Content = dp, Padding = new Thickness(8, 4, 8, 4), Cursor = Cursors.Hand };
                };
                Action<KeyValuePair<LanPeerInfo, LanFilmItem>> addPeer = kv =>
                {
                    var x = kv.Value;
                    var m = LookupMeta(Meta, PeerFilmKey(x));
                    var it = mkRow(m, MetaTitle(m) ?? CleanTitle(x.t), "🖥 " + kv.Key.Device + (m?.Rating > 0 ? "  ·  ★ " + m.Rating.ToString("0.0") : "") + (x.l > 0 ? "  ·  " + FormatTime(x.l) : ""));
                    if (rfCur != null && x.i == rfCur.Id) { it.Background = new SolidColorBrush(Color.FromArgb(0x66, 0x3B, 0x82, 0xF6)); cur = it; }
                    it.PreviewMouseLeftButtonUp += (q, w2) => { WantRemoteFilm(kv.Key, kv.Value, false); showChannels(false); };
                    chList.Items.Add(it);
                };
                Action<int> addLocal = k =>
                {
                    var f = films[k]; var m = MetaFor(f.Path);
                    var sub = string.Join("  ·  ", new[] { m?.Rating > 0 ? "★ " + m.Rating.ToString("0.0") : null, m?.Genres.Count > 0 ? m.Genres[0] : null, f.Len > 0 ? FormatTime(f.Len) : null, SeenText(f) is string st && st.Length > 0 ? L.T("obejrzane ") + st : null }.Where(x => x != null));
                    var it = mkRow(m, ShownTitle(f, m), sub);
                    if (string.Equals(f.Path, curP, StringComparison.OrdinalIgnoreCase)) { it.Background = new SolidColorBrush(Color.FromArgb(0x66, 0x3B, 0x82, 0xF6)); cur = it; }
                    it.PreviewMouseLeftButtonUp += (q, w2) =>
                    {
                        if (fk == fCur) _vlcLoad(k);
                        else { var paths = films.Select(x => x.Path).ToList(); PlayVideos(paths.ToArray(), k); }   // ta lista staje sie lista odtwarzania
                        showChannelsRefresh();
                    };
                    {   // prawy przycisk na filmie: otworz, podglad PiP
                        var fcm = new ContextMenu(); var fp = f.Path; int fi = k;
                        var o1 = new MenuItem { Header = L.T("▶ Otwórz") }; o1.Click += (q, w2) => { if (fk == fCur) _vlcLoad(fi); else PlayVideos(films.Select(x => x.Path).ToArray(), fi); showChannelsRefresh(); }; fcm.Items.Add(o1);
                        var o2 = new MenuItem { Header = L.T("⧉ Pokaż w PiP (podgląd)"), IsEnabled = !string.Equals(fp, curP, StringComparison.OrdinalIgnoreCase) }; o2.Click += (q, w2) => _pipShowFilm?.Invoke(fp); fcm.Items.Add(o2);
                        fcm.Opened += (q, w2) => chHide.Stop();
                        it.ContextMenu = fcm;
                    }
                    chList.Items.Add(it);
                };
                if (remoteCur)
                {
                    foreach (var kv in PeerQueue(how)) addPeer(kv);
                    if (chList.Items.Count == 0) chList.Items.Add(new ListBoxItem { Content = new TextBlock { Text = L.T("Otwórz wspólną bibliotekę, aby wczytać listę filmów z innych komputerów."), Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap }, IsEnabled = false });
                }
                else if (fk == fRec)   // tutaj i na innych komputerach - od najnowszego
                {
                    var order = films.Select((f, k) => (w: f.Last, k, p: (KeyValuePair<LanPeerInfo, LanFilmItem>?)null))
                        .Concat(PeerQueue(how).Where(p => p.Value.w > 0).Select(p => (w: p.Value.w, k: -1, p: (KeyValuePair<LanPeerInfo, LanFilmItem>?)p)))
                        .OrderByDescending(e => e.w).Take(60).ToList();
                    foreach (var e in order) { if (e.p.HasValue) addPeer(e.p.Value); else addLocal(e.k); }
                }
                else
                {
                    for (int k = 0; k < films.Count; k++) addLocal(k);
                    if (fk == fShr)   // wspólna biblioteka: dyski sieciowe i filmy z innych komputerów Velivo
                    {
                        foreach (var kv in PeerQueue(how)) addPeer(kv);
                        if (chList.Items.Count == 0) chList.Items.Add(new ListBoxItem { Content = new TextBlock { Text = L.T("Otwórz wspólną bibliotekę, aby wczytać listę filmów z innych komputerów."), Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap }, IsEnabled = false });
                    }
                }
                chPanel.Visibility = Visibility.Visible;
                if (cur != null) chList.ScrollIntoView(cur);
                chHide.Stop(); if (!chPanel.IsMouseOver) chHide.Start();
            };
            showChannels = show =>
            {
                if (!show) { chPanel.Visibility = Visibility.Collapsed; return; }
                if (mini0) return;
                chPanel.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Max(20, Math.Min(100, IptvSt.ListAlpha)) / 100), 0x14, 0x16, 0x1A));
                chPanel.Margin = new Thickness(0, 0, 0, controls.Visibility == Visibility.Visible ? controls.ActualHeight : 0);
                var nowPath = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                bool tvMode = nowPath != null && _iptvPlaying.ContainsKey(nowPath);
                if (chTvMode != tvMode) { chGroup.Items.Clear(); chSort.Items.Clear(); chSort.Tag = null; chTvMode = tvMode; }
                if (!tvMode) { showFilms(); return; }
                // grupy (raz na otwarcie): biezaca + z listy kanalow
                const string curKey = "\u0001cur", favKey = "\u0001fav", recKey = "\u0001rec";
                var plAll = _iptvPl ?? KnownPlaylist();   // okno TV niewlaczone - lista z pamieci podrecznej (wszystkie grupy)
                if (chGroup.Items.Count <= 3 && plAll != null && plAll.Channels.Count > 0) { chGroup.Tag = "busy"; chGroup.Items.Clear(); chGroup.Tag = null; }
                if (chGroup.Items.Count == 0)
                {
                    chGroup.Items.Add(new ComboBoxItem { Content = L.T("▶ Kolejka"), Tag = curKey });
                    chGroup.Items.Add(new ComboBoxItem { Content = L.T("⭐ Ulubione"), Tag = favKey });
                    chGroup.Items.Add(new ComboBoxItem { Content = L.T("🕘 Ostatnio oglądane"), Tag = recKey });
                    if (plAll != null) foreach (var g in plAll.Channels.Select(c => c.Group).Distinct().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)) chGroup.Items.Add(new ComboBoxItem { Content = g.Length == 0 ? L.T("(bez grupy)") : g, Tag = g });
                }
                fillSort("sort-lista-kanalow", new[] { "az", "za", "nr" }, new[] { "Sortuj: nazwa A–Z", "Sortuj: nazwa Z–A", "Sortuj: jak na liście" });
                if (chPanel.Visibility != Visibility.Visible)   // kazde otwarcie: biezaca lista i biezacy kanal; gdy to jeden kanal (program TV, wznowienie) - jego grupa
                {
                    // ostatnio wybrana grupa (zapamietana), jesli jest w niej obecny kanal; inaczej grupa obecnego kanalu - nie "Kolejka"
                    var nowCh = _playlist.Count > _playIndex && _iptvPlaying.TryGetValue(_playlist[_playIndex], out var nc) ? nc : null;
                    var items = chGroup.Items.Cast<ComboBoxItem>().ToList();
                    var keepG = GetPref("grupa-kanalow");
                    Func<string, bool> hasNow = g => nowCh == null || (g == favKey ? IptvSt.Fav.Contains(nowCh.Url) : g == recKey ? IptvSt.Recent.Contains(nowCh.Url) : g == curKey || (plAll?.Channels.Any(c => c.Url == nowCh.Url && c.Group == g) ?? false));
                    var grp = nowCh == null ? null : items.FirstOrDefault(i => (i.Tag as string) == (plAll?.Channels.FirstOrDefault(c => c.Url == nowCh.Url)?.Group ?? nowCh.Group));
                    chGroup.SelectedItem = items.FirstOrDefault(i => !string.IsNullOrEmpty(keepG) && (i.Tag as string) == keepG && hasNow(keepG)) ?? grp ?? items.FirstOrDefault();
                }
                var gk = (chGroup.SelectedItem as ComboBoxItem)?.Tag as string ?? curKey;
                chSort.Visibility = gk == curKey || gk == recKey ? Visibility.Collapsed : Visibility.Visible;   // kolejnosc przelaczania / od najnowszego
                List<IptvChannel> chans;
                if (gk == curKey) chans = _playlist.Where(u => _iptvPlaying.ContainsKey(u)).Select(u => _iptvPlaying[u]).ToList();
                else
                {
                    var all = plAll?.Channels ?? new List<IptvChannel>();
                    chans = gk == favKey ? IptvSt.Fav.Select(u => all.FirstOrDefault(c => c.Url == u)).Where(c => c != null).ToList()
                        : gk == recKey ? IptvSt.Recent.Select(u => all.FirstOrDefault(c => c.Url == u)).Where(c => c != null).ToList()
                        : all.Where(c => c.Group == gk).ToList();
                    if (gk != recKey)
                    {
                        var chHow = GetPref("sort-lista-kanalow") ?? "az";
                        if (chHow == "az") chans = chans.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                        else if (chHow == "za") chans = chans.OrderByDescending(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                    }
                }
                var curUrl = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                chList.Items.Clear();
                var now = DateTime.Now; ListBoxItem cur = null;
                for (int k = 0; k < chans.Count; k++)
                {
                    var ch = chans[k];
                    int idx = k;
                    var img = new Image { Width = 40, Height = 24, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                    var nowP = EpgAt(EpgFor(_iptvEpg, ch), now);
                    var txt = new StackPanel();
                    txt.Children.Add(new TextBlock { Text = (IsRecording(ch.Url) ? "⏺ " : "") + ch.Name, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
                    if (nowP != null)
                    {
                        txt.Children.Add(new TextBlock { Text = nowP.Start.ToString("HH:mm") + "–" + nowP.Stop.ToString("HH:mm") + "  " + nowP.Title, FontSize = 11.5, Foreground = Brushes.Silver, TextTrimming = TextTrimming.CharacterEllipsis });
                        // waski pasek: ile programu juz minelo (jak w dekoderach / programach IPTV)
                        double span = (nowP.Stop - nowP.Start).TotalMinutes, gone = (now - nowP.Start).TotalMinutes;
                        txt.Children.Add(new ProgressBar { Height = 3, Maximum = Math.Max(1, span), Value = Math.Max(0, Math.Min(span, gone)), Margin = new Thickness(0, 3, 0, 1), BorderThickness = new Thickness(0),
                            Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                            ToolTip = L.T("zostało ") + Math.Max(0, (int)Math.Ceiling(span - gone)) + " min" });
                    }
                    var star = new TextBlock { Text = IptvSt.Fav.Contains(ch.Url) ? "★" : "", Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xC5, 0x18)), FontSize = 14, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };   // ulubiony - gwiazdka z prawej
                    var arch = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)), Margin = new Thickness(6, 6, 0, 0), VerticalAlignment = VerticalAlignment.Top,
                        Visibility = HasCatchup(ch) ? Visibility.Visible : Visibility.Collapsed, ToolTip = L.T("Archiwum: ") + ch.CatchupDays + L.T(" dni wstecz") };   // kanal z archiwum
                    var row = new DockPanel(); DockPanel.SetDock(img, Dock.Left); DockPanel.SetDock(star, Dock.Right); DockPanel.SetDock(arch, Dock.Right); row.Children.Add(img); row.Children.Add(star); row.Children.Add(arch); row.Children.Add(txt);
                    var it = new ListBoxItem { Content = row, Padding = new Thickness(8, 4, 8, 4), Cursor = Cursors.Hand };
                    {   // prawy przycisk na kanale: otworz, ulubione, nagrywanie, program, sortowanie listy
                        var rcm = new ContextMenu();
                        Action<string, Action, bool> radd = (h, a, en) => { var mi = new MenuItem { Header = h, IsEnabled = en }; mi.Click += (q, w2) => a(); rcm.Items.Add(mi); };
                        radd(L.T("▶ Otwórz"), () => { if (gk == curKey) _vlcLoad(_playlist.IndexOf(ch.Url)); else { foreach (var c in chans) _iptvPlaying[c.Url] = c; PlayVideos(chans.Select(c => c.Url).ToArray(), idx); } showChannelsRefresh(); }, ch.Url != curUrl);
                        rcm.Items.Add(new Separator());
                        bool isF = IptvSt.Fav.Contains(ch.Url);
                        radd(isF ? L.T("⭐ Usuń z ulubionych") : L.T("⭐ Dodaj do ulubionych"), () =>
                        {
                            if (ch.Url == curUrl) { fav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); showChannelsRefresh(); return; }   // biezacy - jak przycisk na pasku
                            if (isF) IptvSt.Fav.Remove(ch.Url); else IptvSt.Fav.Add(ch.Url);
                            SaveIptv(IptvSt); _iptvJobsChanged?.Invoke(); showChannelsRefresh();
                        }, true);
                        if (IsRecording(ch.Url)) radd(L.T("⏹ Zatrzymaj nagrywanie"), () => { StopRecording(ch.Url); showChannelsRefresh(); }, true);
                        else radd(L.T("⏺ Nagrywaj w tle"), () => RecordNow(ch, win), true);
                        radd(L.T("⧉ Pokaż w PiP (podgląd)"), () => pipShow?.Invoke(ch), ch.Url != curUrl);
                        radd(L.T("✏ Edytuj kanał (nazwa, ikona)…"), () => EditChannel(ch, Window.GetWindow(overlay) ?? win, () => { showChannelsRefresh(); showInfo(false); }), true);
                        radd(L.T("📅 Program TV kanału"), () => ShowEpgGuide(Window.GetWindow(overlay) ?? win, ch.Url), true);
                        if (gk != curKey && gk != recKey)
                        {
                            rcm.Items.Add(new Separator());
                            var so = new MenuItem { Header = L.T("↕ Sortuj według") }; var nowHow = GetPref("sort-lista-kanalow") ?? "az";
                            foreach (var (k1, n1) in new[] { ("az", "nazwa A–Z"), ("za", "nazwa Z–A"), ("nr", "jak na liście") })
                            { var key = k1; var mi = new MenuItem { Header = L.T(n1), IsCheckable = true, IsChecked = nowHow == key }; mi.Click += (q, w2) => { SetPref("sort-lista-kanalow", key); chSort.Tag = null; chSort.SelectedItem = chSort.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == key); chSort.Tag = "sort-lista-kanalow"; showChannelsRefresh(); }; so.Items.Add(mi); }
                            rcm.Items.Add(so);
                        }
                        rcm.Opened += (q, w2) => chHide.Stop();   // menu otwarte - lista nie chowa sie
                        it.ContextMenu = rcm;
                    }
                    if (ch.Url == curUrl) { it.Background = new SolidColorBrush(Color.FromArgb(0x66, 0x3B, 0x82, 0xF6)); cur = it; }
                    // klik: z biezacej listy - przelacz; z innej grupy - ta grupa staje sie lista (PgUp/PgDn po niej)
                    it.PreviewMouseLeftButtonUp += (q, w2) =>
                    {
                        if (gk == curKey) _vlcLoad(_playlist.IndexOf(ch.Url));
                        else { foreach (var c in chans) _iptvPlaying[c.Url] = c; PlayVideos(chans.Select(c => c.Url).ToArray(), idx); }
                        showChannelsRefresh();
                    };
                    chList.Items.Add(it);
                    var logo = !string.IsNullOrEmpty(ch.Logo) ? ch.Logo : ByChannel(_iptvIcons, ch);
                    if (logo != null) GetLogo(logo).ContinueWith(t => { if (t.Result != null) img.Source = t.Result; }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                }
                chPanel.Visibility = Visibility.Visible;
                if (cur != null) { chList.SelectedItem = null; chList.ScrollIntoView(cur); }
                chHide.Stop(); if (!chPanel.IsMouseOver) chHide.Start();
            };
            showChannelsRefresh = () => { if (chPanel.Visibility == Visibility.Visible) showChannels(true); };
            chGroup.SelectionChanged += (s, e) =>
            {
                if (chGroup.Tag != null || !(chGroup.SelectedItem is ComboBoxItem gi) || !(gi.Tag is string g)) return;
                if (chTvMode == true) SetPref("grupa-kanalow", g); else if (chTvMode == false) SetPref("grupa-filmow", g);   // zapamietana grupa (takze po ponownym uruchomieniu)
                if (chPanel.Visibility == Visibility.Visible) showChannels(true);
            };
            chGroup.DropDownOpened += (s, e) => chHide.Stop();   // wybieranie grupy - lista nie chowa sie
            // pasek informacji pokazuje sie razem z paskiem sterowania (ruch mysza), a znika po swoim czasie (ustawienie "Znikanie po")
            controls.IsVisibleChanged += (s, e) =>
            {
                if (controls.IsVisible && infoBox.Visibility != Visibility.Visible) { if (IptvSt.InfoOnMove) showInfo(true); }
            };
            win.Closed += (s, e) => infoTimer.Stop();
            // tytul okna: nazwa (+ przy kanale TV: co teraz leci); gdy sie nie miesci - przesuwa sie (koncowka " – Odtwarzacz filmow" stala)
            int marqueePos = 0;
            Action recTitle = () =>
            {
                var pth = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                if (pth == null || curTitle.Length == 0) return;
                var text = (IsRecording(pth) ? "⏺ REC  " : "") + curTitle;
                if (_iptvPlaying.TryGetValue(pth, out var tvc))
                {
                    var nowP = EpgAt(EpgFor(_iptvEpg, tvc), DateTime.Now);
                    if (nowP != null) text += "  ·  ▶ " + nowP.Title + L.T(" (do ") + nowP.Stop.ToString("HH:mm") + ")";
                }
                int fit = Math.Max(12, (int)((win.ActualWidth - 300) / 7.5));
                if (text.Length <= fit) marqueePos = 0;
                else { var loop = text + "     •     "; marqueePos = (marqueePos + 1) % loop.Length; text = (loop + loop).Substring(marqueePos, fit); }
                win.Title = text + " – " + L.T("Odtwarzacz filmów");
            };
            var marquee = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            marquee.Tick += (s, e) =>
            {
                if (win.WindowStyle != WindowStyle.None) recTitle();
                // nowy program na ogladanym kanale - pasek informacji sam
                var pth = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                if (pth != null && _iptvPlaying.TryGetValue(pth, out var c2) && infoShownFor != null && infoShownFor.StartsWith(pth))
                {
                    var p1 = EpgAt(EpgFor(_iptvEpg, c2), DateTime.Now);
                    if (p1 != null && infoShownFor != pth + p1.Start.Ticks) showInfo(false);
                }
            };
            marquee.Start();
            win.Closed += (s, e) => marquee.Stop();
            _iptvJobsChanged += recTitle;   // nagrywanie ogladanego kanalu - widac w tytule
            win.Closed += (s, e) => _iptvJobsChanged -= recTitle;
            // zdarzenia VLC czekajace w kolejce po zamknieciu okna nie dotykaja juz odtwarzacza (zwolniony VLC = awaria calego Velivo)
            bool vlcClosed = false, aoutDs = false;
            DateTime? sleepAt = null; bool sleepEnd = false, sleepPc = false; Action updSleep = () => { }, pcOff = () => { };   // wylacznik czasowy
            Func<Action, Action> guard = a => () => { if (!vlcClosed) a(); };
            // historia przelaczania (zapisana - zostaje po zamknieciu TV), chPos = gdzie jestesmy; powrot nie kasuje historii
            var chHist = IptvSt.Hist; int chPos = chHist.Count - 1; bool chNav = false;
            Func<string, IptvChannel> chOf = u => _iptvPlaying.TryGetValue(u, out var c1) ? c1 : KnownPlaylist()?.Channels.FirstOrDefault(c => c.Url == u);
            Action<IptvChannel> chGo = c =>
            {
                int idx = _playlist.IndexOf(c.Url);
                if (idx >= 0) _vlcLoad(idx); else { _iptvPlaying[c.Url] = c; PlayVideos(new[] { c.Url }, 0); }
            };
            Func<int, bool> chJump = to => { var c = chOf(chHist[to]); if (c == null) { showOsd(L.T("Tego kanału nie ma już na liście")); return false; } chPos = to; chNav = true; chGo(c); return true; };
            _vlcLoad = async i =>
            {
                userStopped = false; lastTvProgress = DateTime.Now;
                savePos();
                if (i < 0 || i >= _playlist.Count) return;
                _playIndex = i;
                var path = _playlist[i];
                // film z innego komputera (strumien albo dysk sieciowy): miejsce i ustawienia z jego biblioteki
                _remoteFilms.TryGetValue(path, out var rf);
                if (rf != null)
                {
                    current = new LibraryVideo { Path = path, Title = rf.Title, Pos = rf.Pos, Len = rf.Len };
                    ApplyPrefs(current, rf.Prefs); rf.Prefs = current;
                }
                _iptvPlaying.TryGetValue(path, out var tv);   // kanal telewizji (IPTV) - bez biblioteki, napisow i wznawiania
                bool liveTv = tv != null && tv.ArchiveStart == null;
                _ = showBadge(path, tv);
                filmNav.Visibility = liveTv ? Visibility.Collapsed : Visibility.Visible; tvNav.Visibility = liveTv ? Visibility.Visible : Visibility.Collapsed;
                if (liveTv && !chNav && (chHist.Count == 0 || chHist[chHist.Count - 1] != tv.Url)) { chHist.Add(tv.Url); if (chHist.Count > 20) chHist.RemoveAt(0); SaveIptv(IptvSt); }
                if (liveTv && !chNav) chPos = chHist.Count - 1;   // zwykle przelaczenie - jestesmy na koncu historii
                chNav = false;
                if (tv != null) current = null;
                else if (rf == null) { var lib0 = LoadLibrary(); current = FindEntry(lib0, path, _playShared); TakeNewestProgress(lib0, current); }
                SaveTvResume(tv != null && tv.ArchiveStart == null ? _playIndex : -1);   // ostatni program wraca po uruchomieniu Velivo
                bool stream = path.Contains("://");
                var name = rf != null ? rf.Title : tv != null ? tv.Name : System.IO.Path.GetFileNameWithoutExtension(path);
                curTitle = name; marqueePos = 0;
                win.Title = (IsRecording(path) ? "⏺ REC  " : "") + name + " – " + L.T("Odtwarzacz filmów");
                LectorStopVoice(lc, mp);
                if (stream && rf?.Sub != null)
                {
                    loadSubsText(null, null);
                    var subUrl = rf.Sub;
                    _ = System.Threading.Tasks.Task.Run(async () => { try { using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) }) return await http.GetByteArrayAsync(subUrl); } catch (Exception) { return null; } })
                        .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { if (t.Result != null && _playlist.Count > _playIndex && _playlist[_playIndex] == path) loadSubsText(SubtitleText(t.Result), name); })));
                }
                else loadSubs(stream ? null : FindSubtitleFor(path));
                applyFilmPrefs();
                miniTitle.Text = name;
                // nagrywany kanal (opcja): obraz z pliku nagrania - jedno polaczenie z dostawca, pauza i cofanie
                curRecFile = tv != null && IptvSt.WatchFromRec ? RecFileFor(path) : null;
                if (curRecFile != null) stream = false;
                var src = !stream && curRecFile == null && tv == null && !File.Exists(path) ? JellyfinStreamUrl(path) : null;   // 🎞 pliku nie ma na tym komputerze - z serwera Jellyfin
                if (!stream && curRecFile == null && tv == null && !File.Exists(path))
                {   // pliku nie ma (np. odłączony dysk) - słowami zamiast stania na 0:00; po 15 s, gdy nic nie gra, powód
                    string drv = System.IO.Path.GetPathRoot(path) ?? "", srv = src != null ? new Uri(src).Authority : null;
                    showOsd(L.T("🎞 Pliku nie ma na dysku ") + drv + (srv != null ? L.T(" – próbuję z serwera Jellyfin ") + srv : L.T(" – dysk odłączony?")));
                    int seqNow = loadSeq + 1; var pathNow = path;
                    _ = System.Threading.Tasks.Task.Delay(15000).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        long len = 0; try { len = mp.Length; } catch (Exception) { }
                        if (seqNow != loadSeq || len > 0) return;
                        showOsd(L.T("🎞 Nie można otworzyć filmu: dysk ") + drv + L.T(" jest niedostępny") + (srv != null ? L.T(" i serwer Jellyfin ") + srv + L.T(" nie odpowiada") : "") + L.T(". Podłącz dysk albo włącz serwer."));
                        App.LogInfo("Film nie startuje: brak pliku " + pathNow + (srv != null ? ", Jellyfin " + srv + " nie odpowiada" : ""));
                    })));
                }
                using (var media = curRecFile != null ? new Media(_libVlc, curRecFile, FromType.FromPath) : src != null ? new Media(_libVlc, new Uri(src)) : stream ? new Media(_libVlc, new Uri(path)) : new Media(_libVlc, path, FromType.FromPath))
                {
                    // napisy o tej samej nazwie obok filmu (film.srt, film.pl.srt) dolaczane same - jak w VLC; przy strumieniu - z komputera wlasciciela
                    if (stream && rf?.Sub != null) media.AddSlave(MediaSlaveType.Subtitle, 4, rf.Sub);
                    if (tv != null && curRecFile == null) foreach (var o in tv.Opts) media.AddOption(o);   // naglowki wymagane przez dostawce (z listy)
                    if (tv != null && curRecFile == null) media.AddOption(":network-caching=" + Math.Max(1, IptvSt.Buffer) * 1000);   // zapas bufora (ustawienie)
                    // AVI: naglowek kontenera bywa sprzeczny ze sciezka AC3 (2 kanaly zamiast 5.1) - czytnik VLC daje cisze albo dziwne dzwieki; czytnik ffmpeg czyta naglowek AC3
                    if (tv == null && !stream && IsAviFile(path)) media.AddOption(":demux=avformat,any");
                    _aoutFailed = false;
                    // poprzedni strumień zatrzymany w tle; w międzyczasie kolejny kanał albo zamknięte okno - gra tylko ostatnio wybrany
                    // zawsze przez tę samą kolejkę: wcześniejsze ⏹ (np. tuż po końcu filmu) nie zatrzyma nowego materiału
                    int seq = ++loadSeq;
                    await vlcRun(() => { var ps = mp.State; if (ps == VLCState.Playing || ps == VLCState.Paused || ps == VLCState.Buffering || ps == VLCState.Opening) mp.Stop(); });
                    if (seq != loadSeq || vlcClosed) return;
                    try { lock (VlcLogFile) File.WriteAllText(VlcLogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + VlcLogSafe(path) + "\nVLC: " + _libVlcKey + "\nprzesuniecie dzwieku (J/K): " + aDelayMs + " ms, bufor: " + IptvSt.Buffer + " s\n"); _vlcDriftAt = DateTime.MinValue; } catch (Exception) { }   // dziennik od nowa dla tego filmu
                    AbLearnUserVolume(vol.Value); AbNewSource(tv != null ? tv.Name : System.IO.Path.GetFileNameWithoutExtension(path), tv != null);   // 🎚 nowy pomiar zintegrowany dla nowego filmu/kanalu
                    mp.Play(media);
                }
                long resume = current != null ? ResumeAt(current.Pos, current.Len) : 0;
                bool forced = _forceResume; _forceResume = false;   // "wznow bez pytania" tylko dla filmu wlaczonego zaraz po restarcie odtwarzacza
                if (curRecFile != null)
                {
                    long at = recResume; recResume = -1;   // -1 = na zywo (koniec pliku), inaczej miejsce, w ktorym bylismy
                    var once2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    once2.Tick += (s, e) => { once2.Stop(); if (mp.Length > 0) mp.Time = at >= 0 ? Math.Min(at, mp.Length - 1500) : Math.Max(0, mp.Length - 3000); };
                    once2.Start();
                    if (at < 0) showOsd(L.T("⏺ Oglądasz z nagrania – pauza i cofanie działają"));
                }
                if (resume > 0)
                {
                    long r = resume;
                    // wznowienie sprawdzane: VLC potrafi zignorować przewinięcie (w pauzie przy pytaniu, film z sieci, jeszcze się buforuje) - wtedy film szedł od początku
                    Action seekResume = () =>
                    {
                        mp.Time = r; int tries = 0;
                        var chk = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                        chk.Tick += (q, w) => { if (vlcClosed || ++tries > 4 || mp.Time >= r - 10000) { chk.Stop(); return; } mp.Time = r; };
                        chk.Start();
                    };
                    var once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    once.Tick += (s, e) =>
                    {
                        once.Stop(); if (mp.Length <= 0) return;
                        var how = forced ? "wznow" : _pilotResume ?? GetPref("wznawianie") ?? "pytaj"; _pilotResume = null;   // po restarcie odtwarzacza - bez pytania   // pytaj / wznow / poczatek (Ustawienia -> Odtwarzacz filmow)
                        if (how == "poczatek" || _together != null) return;
                        if (how == "wznow") { seekResume(); showOsd(L.T("Wznowiono od ") + FormatTime(r)); return; }
                        mp.SetPause(true);
                        var ans = AskResume(win, r);
                        if (ans == null) { mp.SetPause(false); return; }
                        if (ans.Value) { seekResume(); showOsd(L.T("Wznowiono od ") + FormatTime(r)); }
                        mp.SetPause(false);
                    };
                    once.Start();
                }
                // sciezki filmu znane chwile po starcie - wtedy ustawiamy zapamietane napisy i dzwiek
                var tracksTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                tracksTimer.Tick += (s, e) =>
                {
                    tracksTimer.Stop(); applyTracks(); mp.Volume = muted ? 0 : (int)vol.Value;
                    // wyjscie dzwieku Windows (WASAPI) nie przyjelo formatu filmu - DirectSound i ten sam film od tego miejsca
                    if (_aoutFailed && !aoutDs && _abOut == null)   // przy Audio Balance dzwiek idzie przez Velivo - zmiana wyjscia VLC odlaczylaby procesor
                    {
                        aoutDs = true; _aoutFailed = false;
                        long t = mp.Time; vlcRun(() => { mp.SetAudioOutput("directsound"); mp.Stop(); mp.Play(); });   // w tej samej kolejce co inne zatrzymania
                        var back = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                        back.Tick += (q, w) => { back.Stop(); if (!vlcClosed && t > 0) mp.Time = t; };
                        back.Start();
                        showOsd(L.T("🔊 Inne wyjście dźwięku (DirectSound)"));
                    }
                };   // VLC przyjmuje glosnosc pewnie dopiero po starcie filmu
                tracksTimer.Start();
                if (tv != null && _recording.Count > 0) WarnOneConnection();   // trwa nagrywanie - drugie polaczenie
                if (tv != null) { var rk = tv.LiveUrl ?? path; IptvSt.Watched[rk] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var rc = IptvSt.Recent; rc.Remove(rk); rc.Insert(0, rk); if (rc.Count > 30) rc.RemoveRange(30, rc.Count - 30); SaveIptv(IptvSt); _iptvJobsChanged?.Invoke(); LoadIptvInBackground(() => { if (_vlcWin == win) showInfo(false); }); }   // lista kanalow odswieza "Ostatnio ogladane"   // ostatnio ogladane - takze po zmianie kanalu w odtwarzaczu
                if (tv != null) showOsd("📺 " + (i + 1) + ". " + tv.Name);
                var infoOnce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(tv != null ? 700 : 1500) };   // film - gdy znana jest dlugosc
                infoOnce.Tick += (s, e) => { infoOnce.Stop(); if (_playlist.Count > _playIndex && _playlist[_playIndex] == path) showInfo(false); };
                infoOnce.Start();
                speedIdx = 3; speed.Content = "1×";
                prev.IsEnabled = i > 0; next.IsEnabled = i < _playlist.Count - 1;
            };

            // telewizja: na zywo - postep audycji z programu TV i "NA ZYWO"; archiwum - godzina w audycji
            Func<IptvChannel> liveCh = () => { var p1 = _playlist.Count > _playIndex ? _playlist[_playIndex] : null; return p1 != null && _iptvPlaying.TryGetValue(p1, out var c1) && curRecFile == null ? c1 : null; };
            Func<bool> tvShown = null;
            tvShown = () =>
            {
                var c = liveCh();
                if (c == null || c.ArchiveStart.HasValue) { if (ticker.Visibility == Visibility.Visible) { ticker.Visibility = tickerName.Visibility = tickerSep.Visibility = Visibility.Collapsed; ((TranslateTransform)tickerText.RenderTransform).BeginAnimation(TranslateTransform.XProperty, null); } seek.Visibility = Visibility.Visible; }   // film / archiwum - zwykly pasek
                if (c == null) { seek.IsEnabled = true; return false; }
                if (c.ArchiveStart.HasValue)
                {
                    seek.IsEnabled = mp.Length > 0;
                    timeNow.Text = c.ArchiveStart.Value.AddMilliseconds(Math.Max(0, mp.Time)).ToString("HH:mm:ss"); timeLen.Text = L.T("⏪ ARCHIWUM");
                    if (mp.Length > 0 && !seeking && !seekHold(mp.Time)) seek.Value = 1000.0 * mp.Time / mp.Length;
                    return true;
                }
                seek.IsEnabled = false;   // na zywo nie przewijamy
                var p = EpgAt(EpgFor(_iptvEpg, c), DateTime.Now);
                var tn = p != null ? p.Start.ToString("HH:mm") : ""; var tl = p != null ? p.Stop.ToString("HH:mm") + L.T("  ● NA ŻYWO") : L.T("● NA ŻYWO");
                if (timeNow.Text != tn) timeNow.Text = tn; if (timeLen.Text != tl) timeLen.Text = tl;
                if (tickerName.Text != c.Name) tickerName.Text = c.Name;
                // napis: tytul, odcinek i poczatek opisu (zmienia sie z audycja)
                tickerName.Visibility = tickerSep.Visibility = Visibility.Visible;
                var desc = p == null ? L.T("brak programu TV") : "▶ " + p.Title + (string.IsNullOrEmpty(p.Sub) ? "" : " – " + p.Sub) + (string.IsNullOrEmpty(p.Desc) ? "" : "   ·   " + (p.Desc.Length > 160 ? p.Desc.Substring(0, 160).TrimEnd() + "…" : p.Desc));
                seek.Visibility = Visibility.Collapsed;
                if (ticker.Visibility != Visibility.Visible || tickerText.Text != desc) { ticker.Visibility = Visibility.Visible; tickerText.Text = desc; Dispatcher.BeginInvoke(tickerRun, DispatcherPriority.Loaded); }
                return true;
            };
            tvShownRef = () => tvShown();
            mp.LengthChanged += (s, e) => Dispatcher.BeginInvoke(guard(() => { if (!tvShown()) timeLen.Text = FormatTime(e.Length); }));
            DateTime tvLastUpd = DateTime.MinValue;
            mp.TimeChanged += (s, e) => Dispatcher.BeginInvoke(guard(() =>
            {
                var lc = liveCh();
                if (lc != null && !lc.ArchiveStart.HasValue) { if ((DateTime.Now - tvLastUpd).TotalSeconds < 5) return; tvLastUpd = DateTime.Now; }   // na zywo: co 5 s wystarczy (godziny i napis zmieniaja sie z audycja)
                if (tvShown()) return;
                if (seekHold(e.Time)) return;
                timeNow.Text = FormatTime(e.Time);
                if (!seeking && mp.Length > 0 && !seekHold(e.Time)) seek.Value = 1000.0 * e.Time / mp.Length;
            }));
            mp.Playing += (s, e) => Dispatcher.BeginInvoke(guard(() => { play.Content = PlayerIcon("pause"); overlay.Background = see; bigLogo.Visibility = Visibility.Collapsed; if (!Surround51) mp.SetChannel(AudioOutputChannel.Stereo); if (chPanel.Visibility == Visibility.Visible && !chPanel.IsMouseOver) { chHide.Stop(); chHide.Start(); } }));   // lista po Stop chowa sie, gdy film znowu gra
            mp.Paused += (s, e) => Dispatcher.BeginInvoke(guard(() => play.Content = PlayerIcon("play")));
            // po Stop / koncu pod obrazem jest jasne okienko VLC - zaslaniamy je na czarno
            mp.Stopped += (s, e) => Dispatcher.BeginInvoke(guard(() => { play.Content = PlayerIcon("play"); seek.Value = 0; timeNow.Text = "0:00"; overlay.Background = Brushes.Black; bigLogo.Visibility = Visibility.Visible; }));
            mp.EndReached += (s, e) => Dispatcher.BeginInvoke(guard(() => { overlay.Background = Brushes.Black; bigLogo.Visibility = Visibility.Visible; }));
            // nastepny / poprzedni: film z innego komputera - po liscie tamtej biblioteki (jak na liscie z lewej), inaczej po liscie odtwarzania
            Action<int> step = d =>
            {
                var p = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                if (p == null || !_remoteFilms.TryGetValue(p, out var rf)) { _vlcLoad(_playIndex + d); return; }
                var q = PeerQueue(GetPref("sort-lista-filmow") ?? "az");
                var id = Neighbor(q.Select(x => x.Value.i).ToList(), rf.Id, d);
                if (id != null) { var nx = q.First(x => x.Value.i == id); WantRemoteFilm(nx.Key, nx.Value, false); }
            };
            mp.EndReached += (s, e) => Dispatcher.BeginInvoke(guard(() =>
            {
                bool stopHere = sleepEnd; if (stopHere) { sleepEnd = false; sleepAt = null; showOsd(L.T("⏾ Wyłącznik czasowy – koniec filmu")); updSleep(); Dispatcher.BeginInvoke(new Action(() => pcOff())); }   // bez nastepnego filmu
                if (current != null && _remoteFilms.TryGetValue(current.Path, out var rfe)) { SendRemoteProgress(rfe, 0, mp.Length, true); current = null; if (_together == null && !stopHere) step(1); return; }   // nastepny film z tamtej biblioteki
                if (current != null) { var l = LoadLibrary(); var v = FindEntry(l, current.Path, IsShared(current)); if (v != null) { v.Last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); v.Done = true; v.Pos = 0; SaveLibrary(l); _libRefresh?.Invoke(); } current = null; }
                // z nagrania: dojechalismy do konca zapisanego pliku - ponownie otworz plik (urosl) od tego miejsca
                if (curRecFile != null && _playlist.Count > _playIndex) { recResume = Math.Max(0, mp.Length - 1000); _vlcLoad(_playIndex); return; }
                if (_playlist.Count > _playIndex && _iptvPlaying.TryGetValue(_playlist[_playIndex], out var tvEnd) && tvEnd.ArchiveStart.HasValue)
                {   // koniec audycji z archiwum: nastepna z archiwum, a gdy juz trwa - na zywo
                    var el = EpgFor(_iptvEpg, tvEnd); var cur0 = EpgAt(el, tvEnd.ArchiveStart.Value); var nx0 = el?.FirstOrDefault(x => x.Start >= (cur0?.Stop ?? tvEnd.ArchiveStart.Value.AddMinutes(1)));
                    if (!stopHere && nx0 != null && nx0.Stop <= DateTime.Now) { PlayArchive(tvEnd, nx0); return; }
                    if (!stopHere) PlayChannelInGroup(LiveOf(tvEnd), KnownPlaylist());
                    return;
                }
                if (_playlist.Count > _playIndex && _iptvPlaying.ContainsKey(_playlist[_playIndex])) { showOsd(L.T("📺 Brak sygnału – łączę ponownie…")); return; }   // telewizja: bez przeskoku na nastepny kanal; strażnik polaczy ponownie
                if (!stopHere && _playIndex < _playlist.Count - 1) _vlcLoad(_playIndex + 1);   // nastepny z listy (VLC nie moze startowac w swoim watku zdarzen)
            }));
            mp.EncounteredError += (s, e) => Dispatcher.BeginInvoke(guard(() =>
            {
                // archiwum: serwer nie przyjal tej odmiany adresu - kolejna (jak w odtwarzaczach IPTV), az ktoras zadziala
                var pe = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                if (pe != null && _iptvPlaying.TryGetValue(pe, out var ta) && ta.ArchiveStart.HasValue && ta.ArchiveStop.HasValue)
                {
                    var live = LiveOf(ta); int nv = ta.ArchiveVariant + 1;
                    if (nv < 10 && CatchupUrl(live, ta.ArchiveStart.Value, ta.ArchiveStop.Value, DateTime.Now, nv) != null)
                    { showOsd(L.T("⏪ Próbuję inny format archiwum…")); PlayArchive(ta, new EpgItem { Start = ta.ArchiveStart.Value, Stop = ta.ArchiveStop.Value }, nv); return; }
                    showOsd(L.T("⏪ Serwer nie udostępnia tej audycji z archiwum")); return;
                }
                showOsd(L.T("Nie da się odtworzyć tego pliku"));
            }));
            // archiwum ruszylo - ta odmiana adresu dziala u tego dostawcy (nastepnym razem od razu)
            mp.Playing += (s, e) => Dispatcher.BeginInvoke(guard(() =>
            {
                var pp = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                if (pp == null || !_iptvPlaying.TryGetValue(pp, out var ta) || !ta.ArchiveStart.HasValue) return;
                if (IptvSt.CatchupVariant == null) IptvSt.CatchupVariant = new Dictionary<string, int>();
                var host = ArchiveHost(ta.LiveUrl ?? "");
                if (!IptvSt.CatchupVariant.TryGetValue(host, out var old) || old != ta.ArchiveVariant) { IptvSt.CatchupVariant[host] = ta.ArchiveVariant; SaveIptv(IptvSt); }
            }));

            Action togglePlay = () =>
            {
                if (mp.IsPlaying) { mp.SetPause(true); TogetherSend("pause", mp.Time); }
                else if (mp.State == VLCState.Stopped || mp.State == VLCState.Ended || mp.State == VLCState.NothingSpecial) _vlcLoad(_playIndex);
                else { mp.SetPause(false); TogetherSend("play", mp.Time); }
            };
            Action<long> jump = d => { if (mp.Length <= 0) return; mp.Time = Math.Max(0, Math.Min(mp.Length - 500, mp.Time + d)); TogetherSend("seek", mp.Time); showOsd((d > 0 ? "+" : "−") + Math.Abs(d / 1000) + " s"); };
            // ogladanie razem: polecenie z drugiego komputera (bez odsylania z powrotem)
            // pytanie na srodku obrazu (np. "za minute program - przelaczyc?"); zwraca: zamknij pytanie
            _vlcAsk = (text, yes, no) =>
            {
                var tb = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 18, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
                var by = new Button { Content = L.T("📺 Przełącz"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(6, 0, 6, 0), FontSize = 15, IsDefault = true };
                var bn = new Button { Content = L.T("🎬 Zostań przy filmie"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(6, 0, 6, 0), FontSize = 15 };
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                row.Children.Add(by); row.Children.Add(bn);
                var sp = new StackPanel(); sp.Children.Add(tb); sp.Children.Add(row);
                var box = new Border { Child = sp, Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1C, 0x1C, 0x1C)), CornerRadius = new CornerRadius(10), Padding = new Thickness(24, 18, 24, 18),
                    BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
                Action close = () => overlay.Children.Remove(box);
                by.Click += (s, e) => { close(); yes(); };
                bn.Click += (s, e) => { close(); no(); };
                box.MouseLeftButtonDown += (s, e) => e.Handled = true;   // klik w okienko to nie pauza
                overlay.Children.Add(box);
                if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
                return close;
            };
            _vlcRemoteCtl = (act, pos) =>
            {
                if (act == "pause") { mp.SetPause(true); if (pos > 0) mp.Time = pos; showOsd(L.T("⏸ Pauza na drugim komputerze")); }
                else if (act == "play") { if (pos > 0 && Math.Abs(mp.Time - pos) > 1500) mp.Time = pos; mp.SetPause(false); }
                else if (act == "seek") { if (mp.Length > 0) mp.Time = pos; }
                else if (act == "leave") showOsd(L.T("👋 Druga osoba przestała oglądać – oglądasz dalej sam"));   // tylko informacja - film gra dalej
                else if (act == "join")   // druga osoba wrocila - dostaje to miejsce filmu
                {
                    showOsd(L.T("👥 Druga osoba dołączyła ponownie"));
                    TogetherSend("seek", mp.Time); TogetherSend(mp.IsPlaying ? "play" : "pause", mp.Time);
                }
            };
            Action<double> setVol = v => { vol.Value = Math.Max(0, Math.Min(125, v)); };
            // wyciszenie pamietane tutaj: stan Mute w VLC zglasza sie z opoznieniem i kasuje przy zmianie filmu / glosnosci
            vol.ValueChanged += (s, e) => { AbLearnUserVolume(vol.Value); _settings.VlcVolume = (int)vol.Value; filmPrefsChanged(); if (muted && vol.Value > 0) muted = false; mp.Volume = muted ? 0 : (int)vol.Value; volText.Text = (int)vol.Value + "%"; mute.Content = PlayerIcon(muted || vol.Value == 0 ? "mute" : "volume"); };
            Action toggleMute = () =>
            {
                muted = !muted; mp.Mute = false; mp.Volume = muted ? 0 : (int)vol.Value;   // wyciszenie = glosnosc 0 (przelacznik Mute w VLC bywa zawodny)
                if (muted) LectorStopVoice(lc, mp);   // lektor tez cicho
                mute.Content = PlayerIcon(muted ? "mute" : "volume"); showOsd(muted ? L.T("Wyciszono") : (int)vol.Value + "%");
            };
            Action<int> setSpeed = d => { speedIdx = Math.Max(0, Math.Min(speeds.Length - 1, speedIdx + d)); mp.SetRate(speeds[speedIdx]); speed.Content = speeds[speedIdx].ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×"; showOsd(L.T("Prędkość ") + speed.Content); };
            // tryb mini: male okienko bez ramki w prawym dolnym rogu, zawsze na wierzchu; powrot - poprzedni rozmiar i miejsce
            Rect normalBounds = Rect.Empty;
            Action exitFull = null;   // ustawiane nizej (pelny ekran zdefiniowany po trybie mini)
            Action toggleMini = () =>
            {
                if (full0) exitFull?.Invoke();   // z pelnego ekranu - najpierw wyjscie, potem mini
                mini0 = !mini0;
                if (mini0)
                {
                    normalBounds = win.WindowState == WindowState.Normal ? new Rect(win.Left, win.Top, win.Width, win.Height) : win.RestoreBounds;
                    win.WindowState = WindowState.Normal; win.WindowStyle = WindowStyle.None; win.ResizeMode = ResizeMode.CanResizeWithGrip;
                    var wa = SystemParameters.WorkArea;
                    win.Width = 480; win.Height = 270; win.Left = wa.Right - 480 - 16; win.Top = wa.Bottom - 270 - 16;
                    win.Topmost = true;
                    miniTitle.Text = System.IO.Path.GetFileNameWithoutExtension(_playlist.Count > _playIndex ? _playlist[_playIndex] : "");
                }
                else
                {
                    win.WindowStyle = WindowStyle.SingleBorderWindow; win.ResizeMode = ResizeMode.CanResize; win.Topmost = _settings.VlcTopmost;
                    if (!normalBounds.IsEmpty) { win.Left = normalBounds.Left; win.Top = normalBounds.Top; win.Width = normalBounds.Width; win.Height = normalBounds.Height; }
                    miniBar.Visibility = Visibility.Collapsed;
                }
                showBar();
            };
            miniBar.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) toggleMini(); else try { win.DragMove(); } catch (InvalidOperationException) { } e.Handled = true; };
            miniBack.Click += (s, e) => toggleMini();
            miniClose.Click += (s, e) => win.Close();
            mini.Click += (s, e) => toggleMini();
            Action toggleFull = () =>
            {
                if (mini0) toggleMini();
                full0 = !full0;
                // pełny ekran bez „zawsze na wierzchu” - biblioteka, lista kanałów, program TV i inne okna Velivo pokazują się nad filmem
                // bez ramki do zmiany rozmiaru (NoResize): okno bez ramki z ramką zmiany rozmiaru po maksymalizacji wychodzi poza ekran o grubość ramki - pasek na dole obcięty (CHUWI, TV, skalowanie)
                if (full0) { prevState = win.WindowState; win.WindowStyle = WindowStyle.None; win.ResizeMode = ResizeMode.NoResize; win.WindowState = WindowState.Normal; win.WindowState = WindowState.Maximized; win.Topmost = false; }
                else { win.Topmost = _settings.VlcTopmost; win.WindowStyle = WindowStyle.SingleBorderWindow; win.ResizeMode = ResizeMode.CanResize; win.WindowState = prevState; }
                showBar();
                full.Content = PlayerIcon(full0 ? "exitfull" : "full");
            };
            exitFull = () => { if (full0) toggleFull(); };

            play.Click += (s, e) => togglePlay();
            // ponowne uruchomienie odtwarzacza w tym samym miejscu (zmiana filtrow dzwieku VLC dziala tylko od startu)
            Action restartPlayer = () =>
            {
                var pl = _playlist.ToArray(); int idx = _playIndex; if (pl.Length == 0) return;
                savePos(); _forceResume = true;
                win.Close();
                Dispatcher.BeginInvoke(new Action(() => PlayVideos(pl, idx)), DispatcherPriority.Background);
            };
            // ulubione: kanal TV - Ulubione kanalow, film - playlista "⭐ Ulubione"
            const string favList = "⭐ Ulubione";
            Func<string> favPath = () => _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
            Func<bool> isFav = () =>
            {
                var p0 = favPath(); if (p0 == null) return false;
                if (_iptvPlaying.ContainsKey(p0)) return IptvSt.Fav.Contains(p0);
                return LoadPlaylists().Named.TryGetValue(favList, out var l0) && l0.Contains(p0, StringComparer.OrdinalIgnoreCase);
            };
            Action updFav = () => { var on = isFav(); var ic = PlayerIcon(on ? "starfill" : "star"); if (on) ic.Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xC5, 0x18)); fav.Content = ic; };
            fav.Click += (s, e) =>
            {
                var p0 = favPath(); if (p0 == null) return;
                bool now;
                if (_iptvPlaying.ContainsKey(p0)) { now = !IptvSt.Fav.Remove(p0); if (now) IptvSt.Fav.Add(p0); SaveIptv(IptvSt); }
                else
                {
                    var pl = LoadPlaylists(); if (!pl.Named.TryGetValue(favList, out var l0)) pl.Named[favList] = l0 = new List<string>();
                    now = l0.RemoveAll(x => string.Equals(x, p0, StringComparison.OrdinalIgnoreCase)) == 0; if (now) l0.Add(p0);
                    if (l0.Count == 0) pl.Named.Remove(favList);
                    SavePlaylists(pl);
                }
                updFav(); showOsd(now ? L.T("⭐ Dodano do ulubionych") : L.T("☆ Usunięto z ulubionych"));
            };
            mp.Playing += (s, e) => Dispatcher.BeginInvoke(guard(updFav));
            // wylacznik czasowy: po czasie (ostatnie 30 s cichnie) albo po koncu filmu
            var sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            updSleep = () =>
            {
                var left = sleepAt.HasValue ? sleepAt.Value - DateTime.Now : TimeSpan.Zero;
                var sTxt = (sleepEnd ? L.T("koniec") : sleepAt.HasValue ? Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)) + L.T(" min") : "") + (sleepPc && (sleepEnd || sleepAt.HasValue) ? " 💻" : "");
                var sp0 = new StackPanel { Orientation = Orientation.Horizontal }; sp0.Children.Add(PlayerIcon("moon"));
                if (sTxt.Length > 0) sp0.Children.Add(new TextBlock { Text = sTxt, Foreground = Brushes.White, FontSize = 12.5, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                sleep.Content = sp0;
                if (sleepAt.HasValue) sleepTimer.Start(); else sleepTimer.Stop();
            };
            sleepTimer.Tick += (s, e) =>
            {
                if (!sleepAt.HasValue) { sleepTimer.Stop(); return; }
                var left = sleepAt.Value - DateTime.Now;
                if (left.TotalSeconds <= 30 && left.TotalSeconds > 0) mp.Volume = (int)(vol.Value * left.TotalSeconds / 30);   // wyciszanie
                if (left.TotalSeconds <= 0)
                {
                    var pz = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                    sleepAt = null; savePos();
                    if (pz != null && _iptvPlaying.ContainsKey(pz)) vlcRun(() => mp.Stop()); else mp.SetPause(true);   // TV na zywo - pauza nie ma sensu, zatrzymaj
                    mp.Volume = muted ? 0 : (int)vol.Value;
                    showOsd(L.T("⏾ Wyłącznik czasowy – zatrzymano")); pcOff();
                }
                updSleep();
            };
            win.Closed += (s, e) => sleepTimer.Stop();
            // po zadzialaniu wylacznika - opcjonalnie wylaczenie komputera (60 s na anulowanie)
            pcOff = () =>
            {
                if (!sleepPc) return; sleepPc = false;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", "/s /t 60") { CreateNoWindow = true, UseShellExecute = false }); } catch (Exception ex) { App.LogError(ex); return; }
                if (AskYes(win, L.T("💻 Komputer wyłączy się za 60 sekund."), L.T("Anuluj wyłączenie"), L.T("OK")))
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", "/a") { CreateNoWindow = true, UseShellExecute = false }); showOsd(L.T("💻 Wyłączenie komputera anulowane")); } catch (Exception ex) { App.LogError(ex); }
            };
            Action<ItemsControl> sleepItems = cm =>
            {
                foreach (var m0 in new[] { 15, 30, 45, 60, 90, 120 })
                {
                    var m = m0; var mi = new MenuItem { Header = m + " min" };
                    mi.Click += (a, b) => { sleepEnd = false; sleepAt = DateTime.Now.AddMinutes(m); updSleep(); showOsd(L.T("⏾ Zatrzymanie za ") + m + " min"); };
                    cm.Items.Add(mi);
                }
                var pNow = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                if (pNow != null && _iptvPlaying.TryGetValue(pNow, out var tvc))
                {   // telewizja: po koncu programu, ktory teraz leci (z programu TV)
                    var prog = EpgAt(EpgFor(_iptvEpg, tvc), DateTime.Now);
                    var endP = new MenuItem { Header = prog != null ? L.T("📺 Po końcu programu: ") + prog.Title + " (" + prog.Stop.ToString("HH:mm") + ")" : L.T("📺 Po końcu programu (brak programu TV)"), IsEnabled = prog != null };
                    if (prog != null) endP.Click += (a, b) => { sleepEnd = false; sleepAt = prog.Stop; updSleep(); showOsd(L.T("⏾ Zatrzymanie o ") + prog.Stop.ToString("HH:mm") + L.T(" – po końcu programu")); };
                    cm.Items.Add(endP);
                }
                else
                {
                    var endMi = new MenuItem { Header = L.T("🎬 Po końcu tego filmu") };
                    endMi.Click += (a, b) => { sleepAt = null; sleepEnd = true; updSleep(); showOsd(L.T("⏾ Zatrzymanie po końcu filmu")); };
                    cm.Items.Add(endMi);
                }
                cm.Items.Add(new Separator());
                var pcMi = new MenuItem { Header = L.T("💻 Potem wyłącz komputer"), IsCheckable = true, IsChecked = sleepPc };
                pcMi.Click += (a, b) => { sleepPc = pcMi.IsChecked; updSleep(); showOsd(sleepPc ? L.T("💻 Po zatrzymaniu komputer się wyłączy") : L.T("💻 Komputer zostanie włączony")); };
                cm.Items.Add(pcMi);
                if (sleepAt.HasValue || sleepEnd)
                {
                    var off = new MenuItem { Header = L.T("✖ Wyłącz wyłącznik czasowy") };
                    off.Click += (a, b) => { sleepAt = null; sleepEnd = false; sleepPc = false; mp.Volume = muted ? 0 : (int)vol.Value; updSleep(); showOsd(L.T("⏾ Wyłącznik czasowy wyłączony")); };
                    cm.Items.Add(off);
                }
            };
            // 🌙 wylaczenie ekranu (monitor gasnie, film gra dalej) - teraz albo po czasie; wybudzenie: mysz, klawisz albo Pilot
            DateTime? scrAt = null;
            var scrTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            Action scrOffNow = () => { scrAt = null; scrTimer.Stop(); showOsd(L.T("🌙 Ekran gaśnie – film gra dalej")); Dispatcher.BeginInvoke(new Action(() => ScreenPower(false)), DispatcherPriority.Background); };
            scrTimer.Tick += (s, e) => { if (scrAt.HasValue && DateTime.Now >= scrAt.Value) scrOffNow(); };
            win.Closed += (s, e) => scrTimer.Stop();
            Action<double> scrSet = m =>
            {
                if (m == 0) { scrOffNow(); return; }
                if (m < 0) { scrAt = null; scrTimer.Stop(); showOsd(L.T("🌙 Wyłączenie ekranu anulowane")); return; }
                scrAt = DateTime.Now.AddMinutes(m); scrTimer.Start(); showOsd(L.T("🌙 Ekran zgaśnie za ") + m + L.T(" min"));
            };
            Action<ItemsControl> screenItems = cm =>
            {
                var now = new MenuItem { Header = L.T("🌙 Wyłącz ekran teraz (film gra dalej)"), ToolTip = L.T("Monitor gaśnie, dźwięk gra. Ruch myszy, klawisz albo Pilot – ekran wraca.") };
                now.Click += (a, b) => scrSet(0); cm.Items.Add(now);
                var later = new MenuItem { Header = L.T("🌙 Wyłącz ekran za…") + (scrAt.HasValue ? " (" + Math.Max(1, (int)Math.Ceiling((scrAt.Value - DateTime.Now).TotalMinutes)) + L.T(" min") + ")" : "") };
                foreach (var m0 in new[] { 5, 15, 30, 60 }) { var m = m0; var mi = new MenuItem { Header = m + L.T(" min") }; mi.Click += (a, b) => scrSet(m); later.Items.Add(mi); }
                cm.Items.Add(later);
                if (scrAt.HasValue) { var c = new MenuItem { Header = L.T("✖ Nie wyłączaj ekranu") }; c.Click += (a, b) => scrSet(-1); cm.Items.Add(c); }
            };
            sleep.Click += (s, e) =>
            {
                var cm = new ContextMenu { PlacementTarget = sleep, Placement = PlacementMode.Top };
                screenItems(cm); cm.Items.Add(new Separator());
                var sl = new MenuItem { Header = L.T("⏾ Zatrzymaj odtwarzanie…") }; sleepItems(sl); cm.Items.Add(sl);
                cm.IsOpen = true;
            };

            // PiP (obraz w obrazie): drugi kanal TV w malym okienku nad obrazem, wyciszony; przeciaganie mysza = pozycja, krawedzie = wielkosc,
            // klik = zamiana z glownym; pozycja i wielkosc zapamietane wzgledem okna odtwarzacza (domyslnie prawy dolny rog)
            LibVLCSharp.Shared.MediaPlayer pipMp = null; Window pipWin = null; IptvChannel pipCh = null; TextBlock pipName = null; bool pipPlacing = false;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            double? pipX = null, pipY = null, pipW = null;   // miejsce i wielkosc ustawione mysza - zapisywane same (chwile po ostatniej zmianie)
            var pipKeep = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            pipKeep.Tick += (s, e) => { pipKeep.Stop(); if (pipX == null) return; SetPref("pip-x", pipX.Value.ToString(inv)); SetPref("pip-y", pipY.Value.ToString(inv)); SetPref("pip-w", pipW.Value.ToString(inv)); };
            Func<string, double, double> prefD = (k, d) => double.TryParse(GetPref(k), System.Globalization.NumberStyles.Float, inv, out var x) ? x : d;
            Func<Rect> pipHost = () =>
            {
                var src = PresentationSource.FromVisual(view); if (src?.CompositionTarget == null) return Rect.Empty;
                var tl = src.CompositionTarget.TransformFromDevice.Transform(view.PointToScreen(new Point(0, 0)));
                return new Rect(tl.X, tl.Y, view.ActualWidth, view.ActualHeight);
            };
            // wlasciciel podgladu: warstwa obrazu (klik nie trafia w glowny), a gdy jeszcze niewidoczna (minimalizacja, start) - okno odtwarzacza;
            // Windows nie pozwala przypiac okna do niepokazanego okna
            Func<Window> pipOwner = () => { var ow = Window.GetWindow(overlay); return ow != null && ow != win && ow.IsVisible ? ow : win; };
            Action pipPlace = () =>
            {
                if (pipWin == null) return; var r = pipHost(); if (r.IsEmpty || r.Width < 50) return;
                double w = r.Width * Math.Max(0.1, Math.Min(0.7, pipW ?? prefD("pip-w", 0.28))), h = w * 9 / 16;
                double fx = pipX ?? prefD("pip-x", -1), fy = pipY ?? prefD("pip-y", -1);
                double x = fx < 0 ? r.Width - w : fx * r.Width, y = fy < 0 ? r.Height - h - (controls.Visibility == Visibility.Visible ? controls.ActualHeight : 0) : fy * r.Height;
                x = Math.Max(0, Math.Min(r.Width - w, x)); y = Math.Max(0, Math.Min(r.Height - h, y));
                var ow = pipOwner(); if (pipWin.Owner != ow) pipWin.Owner = ow;   // warstwa obrazu bywa tworzona na nowo (zmiana rozmiaru) - podglad zawsze nad nia
                pipPlacing = true; pipWin.Width = w; pipWin.Height = h; pipWin.Left = r.X + x; pipWin.Top = r.Y + y; pipPlacing = false;
            };
            Action pipSave = () =>
            {
                if (pipWin == null) return; var r = pipHost(); if (r.IsEmpty || r.Width < 50) return;
                pipX = (pipWin.Left - r.X) / r.Width; pipY = (pipWin.Top - r.Y) / r.Height; pipW = pipWin.Width / r.Width;
                pipKeep.Stop(); pipKeep.Start();
            };
            // dzwiek z podgladu albo z glownego: wylaczana sama sciezka dzwieku (nie glosnosc - ta jest wspolna dla calego programu)
            bool pipSound = false; int mainTrack = -1;
            Action<bool> pipAudio = on =>
            {
                if (pipMp == null) return;
                if (on && !pipSound) { mainTrack = mp.AudioTrack; mp.SetAudioTrack(-1); var t = pipMp.AudioTrackDescription.FirstOrDefault(x => x.Id >= 0); if (t.Name != null) pipMp.SetAudioTrack(t.Id); }
                else if (!on && pipSound) { pipMp.SetAudioTrack(-1); var t = mp.AudioTrackDescription.FirstOrDefault(x => x.Id == mainTrack); mp.SetAudioTrack(t.Name != null ? mainTrack : mp.AudioTrackDescription.FirstOrDefault(x => x.Id >= 0).Id); }
                pipSound = on;
            };
            Action pipClose = () =>
            {
                if (pipWin == null) return;
                pipAudio(false);   // dzwiek wraca do glownego
                var m = pipMp; var w0 = pipWin; pipMp = null; pipWin = null; pipCh = null;
                try { m.Stop(); } catch (Exception) { }
                w0.Close();
                var d = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };   // jak przy glownym - zwolnienie VLC z opoznieniem (bez awarii)
                d.Tick += (q, w2) => { d.Stop(); try { m.Dispose(); } catch (Exception) { } };
                d.Start();
            };
            Action pipSwap = null;
            const string pipFilmGrp = "\u0001film";   // film w podgladzie (jak kanal, ale plik z biblioteki)
            long pipStart = -1;   // od ktorego miejsca film w podgladzie (-1 = jak w bibliotece)
            Func<string, IptvChannel> pipFilm = path0 => { var lv = FindEntry(LoadLibrary(), path0, null); return new IptvChannel { Name = "🎬 " + (lv != null ? ShownTitle(lv, MetaFor(path0)) : System.IO.Path.GetFileNameWithoutExtension(path0)), Url = path0, Group = pipFilmGrp }; };
            // kursor nad podgladem PiP - klik tam nie jest klikiem w glowny obraz (bez pauzy glownego)
            Func<MouseEventArgs, bool> overPip = e0 =>
            {
                if (pipWin == null || !pipWin.IsVisible) return false;
                var src = PresentationSource.FromVisual(overlay); if (src?.CompositionTarget == null) return false;
                var pt = src.CompositionTarget.TransformFromDevice.Transform(overlay.PointToScreen(e0.GetPosition(overlay)));
                return new Rect(pipWin.Left, pipWin.Top, pipWin.ActualWidth, pipWin.ActualHeight).Contains(pt);
            };
            Action<ItemsControl> pipItems = null;
            pipShow = ch =>
            {
                if (_libVlc == null || ch == null) return;
                if (pipWin == null)
                {
                    pipMp = new LibVLCSharp.Shared.MediaPlayer(_libVlc);
                    var pv = new VideoView { MediaPlayer = pipMp, Background = Brushes.Black };
                    var pl = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Cursor = Cursors.SizeAll, ToolTip = L.T("Klik – zamień z głównym • przeciągnij – przesuń • krawędź – wielkość • prawy – opcje") };
                    pipName = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 4, 30, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                        TextTrimming = TextTrimming.CharacterEllipsis, Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 1, BlurRadius = 3 } };
                    // przyciski podgladu: te same wektorowe ikony co na pasku (znaki tekstowe nie wyswietlaly sie w okienku obrazu)
                    var pStyle = PlayerButtonStyle();
                    Func<string, string, Button> pBtn = (icon, tip) => { var ic = PlayerIcon(icon); ic.Width = ic.Height = 14; return new Button { Content = ic, Style = pStyle, Width = 28, Height = 24, Padding = new Thickness(4), ToolTip = tip }; };
                    var px = pBtn("close", L.T("Zamknij PiP")); px.HorizontalAlignment = HorizontalAlignment.Right; px.VerticalAlignment = VerticalAlignment.Top; px.Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)); px.Margin = new Thickness(0, 3, 3, 0);
                    px.Click += (q, w2) => pipClose();
                    // sterowanie filmem w podgladzie: pauza / odtwarzaj, stop (od poczatku), pasek postepu do przewijania
                    var pPlay = pBtn("pause", L.T("Pauza / odtwarzaj"));
                    var pStop = pBtn("stop", L.T("Stop"));
                    // cienki pasek postepu: niebieska czesc obejrzana, okragly uchwyt
                    var pSeek = new Slider { Minimum = 0, Maximum = 1000, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0), Cursor = Cursors.Hand,
                        Style = ModernSliderStyle() };
                    var pBar = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom, Height = 30, Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), Visibility = Visibility.Collapsed, Cursor = Cursors.Arrow };
                    // TV w podgladzie: poprzedni / nastepny kanal z jego grupy
                    var pPrev = pBtn("prev", L.T("Poprzedni kanał"));
                    var pNext = pBtn("next", L.T("Następny kanał"));
                    Action<int> pStep = d =>
                    {
                        var c = pipCh; if (c == null) return;
                        var grp = (KnownPlaylist()?.Channels ?? new List<IptvChannel>()).Where(x => x.Group == c.Group).ToList();
                        var how = GetPref("sort-lista-kanalow") ?? "az";   // kolejnosc jak na liscie kanalow
                        if (how == "az") grp = grp.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                        else if (how == "za") grp = grp.OrderByDescending(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                        int at = grp.FindIndex(x => x.Url == c.Url); if (at < 0 || grp.Count < 2) return;
                        var p1 = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                        for (int n = 1; n < grp.Count; n++) { var nx = grp[((at + d * n) % grp.Count + grp.Count) % grp.Count]; if (nx.Url != p1) { pipShow(nx); return; } }   // z pominieciem kanalu z glownego
                    };
                    pPrev.Click += (q, w2) => pStep(-1); pNext.Click += (q, w2) => pStep(1);
                    foreach (var b0 in new[] { pPrev, pNext, pPlay, pStop }) DockPanel.SetDock(b0, Dock.Left);
                    var pSnd = pBtn("mute", L.T("Dźwięk z podglądu / z głównego okna")); DockPanel.SetDock(pSnd, Dock.Right);
                    pSnd.Click += (q, w2) => { pipAudio(!pipSound); var ic = PlayerIcon(pipSound ? "volume" : "mute"); ic.Width = ic.Height = 14; pSnd.Content = ic; showOsd(pipSound ? L.T("🔊 Dźwięk z podglądu") : L.T("🔊 Dźwięk z głównego okna")); };
                    // TV w podgladzie: separator po ⏭, przewijany napis (co leci), godzina konca przed dzwiekiem
                    var pSep = new Border { Width = 1, Height = 16, Background = new SolidColorBrush(Color.FromArgb(0x77, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(pSep, Dock.Left);
                    var pEnd = new TextBlock { Foreground = Brushes.Silver, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) }; DockPanel.SetDock(pEnd, Dock.Right);
                    var pTicker = new Canvas { ClipToBounds = true, Height = 18, VerticalAlignment = VerticalAlignment.Center };
                    var pTickText = new TextBlock { Foreground = Brushes.White, FontSize = 12.5, RenderTransform = new TranslateTransform(), CacheMode = new BitmapCache() }; pTicker.Children.Add(pTickText);
                    Action pTickRun = () =>
                    {
                        pTickText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        double w = pTickText.DesiredSize.Width, cw = Math.Max(40, pTicker.ActualWidth);
                        ((TranslateTransform)pTickText.RenderTransform).BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(cw, -w, TimeSpan.FromSeconds((cw + w) / 50.0)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
                    };
                    pTicker.SizeChanged += (q, w2) => { if (pTicker.Visibility == Visibility.Visible) pTickRun(); };
                    pBar.Children.Add(pSnd); pBar.Children.Add(pEnd);
                    pBar.Children.Add(pPrev); pBar.Children.Add(pNext); pBar.Children.Add(pSep); pBar.Children.Add(pPlay); pBar.Children.Add(pStop); var pArea = new Grid(); pArea.Children.Add(pSeek); pArea.Children.Add(pTicker); pBar.Children.Add(pArea);   // pasek postepu (film) albo napis (TV) - zawsze cala szerokosc
                    // glowny zmienil kanal / film przy dzwieku z podgladu - jego sciezka znow wylaczona (po ustawieniu sciezek)
                    mp.Playing += (q, w2) => Dispatcher.BeginInvoke(new Action(() => { if (!pipSound) return; var t1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) }; t1.Tick += (a1, b1) => { t1.Stop(); if (pipSound) { mainTrack = mp.AudioTrack >= 0 ? mp.AudioTrack : mainTrack; mp.SetAudioTrack(-1); } }; t1.Start(); }));
                    // nowy kanal / film w podgladzie - bez dzwieku, dopoki nie przelaczysz; sciezka dzwieku pojawia sie chwile po starcie, wiec ponawiamy do skutku
                    pipMp.Playing += (q, w2) => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        int tries = 0; var mt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                        mt.Tick += (a1, b1) =>
                        {
                            if (pipMp == null || pipSound || ++tries > 40) { mt.Stop(); return; }
                            if (pipMp.AudioTrackCount > 0 && pipMp.AudioTrack != -1) pipMp.SetAudioTrack(-1);
                            else if (pipMp.AudioTrackCount > 0 && tries > 8) mt.Stop();   // wylaczona i tak zostaje
                        };
                        mt.Start();
                    }));
                    bool pSeeking = false;
                    pPlay.Click += (q, w2) => { if (pipMp.IsPlaying) pipMp.SetPause(true); else if (pipMp.State == VLCState.Stopped || pipMp.State == VLCState.Ended) { var c = pipCh; pipStart = 0; pipShow(c); } else pipMp.SetPause(false); };
                    pStop.Click += (q, w2) => { pipMp.Stop(); pSeek.Value = 0; };
                    pSeek.PreviewMouseLeftButtonDown += (q, w2) => pSeeking = true;
                    pSeek.PreviewMouseLeftButtonUp += (q, w2) => { pSeeking = false; if (pipMp.Length > 0) pipMp.Position = (float)(pSeek.Value / 1000); };
                    var pTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    pTick.Tick += (q, w2) =>
                    {
                        if (pipMp == null) { pTick.Stop(); return; }
                        bool film = pipCh?.Group == pipFilmGrp;
                        pBar.Visibility = pipCh != null ? Visibility.Visible : Visibility.Collapsed;
                        var fv = film ? Visibility.Visible : Visibility.Collapsed; var tv0 = film ? Visibility.Collapsed : Visibility.Visible;
                        pPlay.Visibility = pStop.Visibility = pSeek.Visibility = fv; pPrev.Visibility = pNext.Visibility = pSep.Visibility = pTicker.Visibility = pEnd.Visibility = tv0;
                        if (!film && pipCh != null)
                        {   // co leci na kanale z podgladu i do ktorej
                            var pp = EpgAt(EpgFor(_iptvEpg, pipCh), DateTime.Now);
                            var pe = pp != null ? L.T("do ") + pp.Stop.ToString("HH:mm") : ""; if (pEnd.Text != pe) pEnd.Text = pe;   // tylko przy zmianie - bez przerysowania co chwile
                            var txt = pp == null ? L.T("brak programu TV") : "▶ " + pp.Title + (string.IsNullOrEmpty(pp.Sub) ? "" : " – " + pp.Sub) + (string.IsNullOrEmpty(pp.Desc) ? "" : "   ·   " + (pp.Desc.Length > 120 ? pp.Desc.Substring(0, 120).TrimEnd() + "…" : pp.Desc));
                            if (pTickText.Text != txt) { pTickText.Text = txt; Dispatcher.BeginInvoke(pTickRun, DispatcherPriority.Loaded); }
                        }
                        var sw = pipSound ? "volume" : "mute";
                        if ((string)pSnd.Tag != sw) { var si = PlayerIcon(sw); si.Width = si.Height = 14; pSnd.Content = si; pSnd.Tag = sw; }
                        if (!film) return;
                        var want = pipMp.IsPlaying ? "pause" : "play";
                        if ((string)pPlay.Tag != want) { var ic = PlayerIcon(want); ic.Width = ic.Height = 14; pPlay.Content = ic; pPlay.Tag = want; }
                        if (!pSeeking && pipMp.Length > 0) { pSeek.Value = pipMp.Position * 1000; pSeek.ToolTip = FormatTime(pipMp.Time) + " / " + FormatTime(pipMp.Length); }
                    };
                    pTick.Start();
                    pl.Children.Add(pipName); pl.Children.Add(px); pl.Children.Add(pBar); pv.Content = pl;
                    var pw = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false, ShowActivated = false, Owner = pipOwner(),   // nad warstwa obrazu glownego (inaczej klik trafial w glowny = pauza)
                         Topmost = win.Topmost, Background = Brushes.Black, Width = 320, Height = 180,
                        // ramka do lapania mysza: obraz VLC zakrywa krawedzie okna, wiec wielkosc zmienia sie za ten pasek
                        Content = new Border { Child = pv, BorderThickness = new Thickness(5), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x44)) } };
                    System.Windows.Shell.WindowChrome.SetWindowChrome(pw, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0) });
                    pl.MouseLeftButtonDown += (q, e2) =>
                    {
                        if (pBar.IsMouseOver) return;   // klik na pasku sterowania filmem - nie zamiana
                        var before = new Point(pw.Left, pw.Top);
                        try { pw.DragMove(); } catch (InvalidOperationException) { }
                        if (Math.Abs(pw.Left - before.X) < 3 && Math.Abs(pw.Top - before.Y) < 3) pipSwap?.Invoke();   // klik bez przesuwania = zamiana kanalow
                        else pipSave();
                        e2.Handled = true;
                    };
                    pl.MouseRightButtonUp += (q, e2) => { var cm = new ContextMenu(); pipItems(cm); cm.IsOpen = true; e2.Handled = true; };
                    pw.SizeChanged += (q, e2) => { if (pipPlacing) return; pipPlacing = true; pw.Height = pw.Width * 9 / 16; pipPlacing = false; pipSave(); };   // proporcje obrazu 16:9
                    pw.WindowStartupLocation = WindowStartupLocation.Manual;
                    pipWin = pw; pipPlace();   // miejsce przed pokazaniem - inaczej startowal w lewym gornym rogu i to zapamietywal
                    pipPlacing = true; pw.Show(); pipPlacing = false; pipPlace();
                }
                pipCh = ch; pipName.Text = "⧉ " + ch.Name;
                try
                {
                    bool film = ch.Group == pipFilmGrp;
                    using (var media = film ? new Media(_libVlc, ch.Url, FromType.FromPath) : new Media(_libVlc, new Uri(ch.Url)))
                    {
                        foreach (var o in ch.Opts) media.AddOption(o);
                        if (!film) media.AddOption(":network-caching=" + Math.Max(1, IptvSt.Buffer) * 1000);
                        else
                        {   // film od miejsca, w ktorym go zostawiono
                            long st0 = pipStart >= 0 ? pipStart : ResumeAt(FindEntry(LoadLibrary(), ch.Url, null)?.Pos ?? 0, FindEntry(LoadLibrary(), ch.Url, null)?.Len ?? 0);
                            if (st0 > 0) media.AddOption(":start-time=" + (st0 / 1000).ToString(inv));
                        }
                        pipStart = -1;
                        // podglad startuje bez dzwieku (sciezka wylaczana po starcie), dzwiek przelacza ikona 🔊 na pasku podgladu
                        pipMp.Play(media);
                    }
                }
                catch (Exception ex) { App.LogError(ex); showOsd(L.T("⚠ PiP: nie udało się włączyć kanału")); }
            };
            pipSwap = () =>
            {
                pipAudio(false);   // po zamianie dzwiek z glownego
                var a = pipCh; var p0 = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                if (a == null) return;
                // glowny idzie do podgladu: kanal albo film z tego komputera (od biezacego miejsca); film strumieniowany z innego komputera - podglad sie zamyka
                IptvChannel mainItem = p0 == null ? null : _iptvPlaying.TryGetValue(p0, out var mc) ? mc : !p0.Contains("://") && File.Exists(p0) ? pipFilm(p0) : null;
                if (mainItem != null && mainItem.Group == pipFilmGrp) { savePos(); pipStart = mp.Time; }
                if (mainItem != null) pipShow(mainItem); else pipClose();
                // podglad idzie na glowny: film (pyta o wznowienie) albo kanal w swojej grupie
                if (a.Group == pipFilmGrp) PlayVideos(new[] { a.Url }, 0);
                else { int at = _playlist.IndexOf(a.Url); if (at >= 0) _vlcLoad(at); else PlayChannelInGroup(a, KnownPlaylist()); }
            };
            pipItems = cm =>
            {
                var p0 = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                bool tvNow = p0 != null && _iptvPlaying.ContainsKey(p0);
                Action<ItemsControl, string, Action, bool> pa = (to, h, a, en) => { var mi = new MenuItem { Header = h, IsEnabled = en }; mi.Click += (q, w2) => a(); to.Items.Add(mi); };
                if (pipWin != null)
                {
                    { var pc0 = pipCh; bool pf0 = pc0?.Group == pipFilmGrp;   // co leci w PiP: opis filmu albo programu TV
                      if (pc0 != null) pa(cm, pf0 ? L.T("ℹ O filmie w podglądzie") : L.T("ℹ Co teraz leci w podglądzie"), () => _ = showAbout(null, pf0 ? pc0.Url : null, pf0 ? null : pc0, null), true); }
                    pa(cm, L.T("⇄ Zamień z głównym (klik w podgląd)"), () => pipSwap(), true);
                    pa(cm, L.T("↘ Przywróć domyślne miejsce i wielkość"), () => { pipKeep.Stop(); pipX = pipY = pipW = null; SetPref("pip-x", null); SetPref("pip-y", null); SetPref("pip-w", null); pipPlace(); }, true);
                    pa(cm, L.T("✖ Zamknij PiP"), pipClose, true);
                    cm.Items.Add(new Separator());
                }
                // co w podgladzie - drzewo: Telewizja (ostatnio, ulubione, ta grupa, wszystkie grupy) i Filmy (ostatnio, ulubione, wszystkie, plik); dlugie listy przewijane
                var plx = KnownPlaylist(); var allc = plx?.Channels ?? new List<IptvChannel>();
                Func<IptvChannel, bool> chOk = c => c != null && c.Url != p0;
                Action<MenuItem, string, IEnumerable<IptvChannel>> chSub = (to, h, list) =>
                {
                    var sm = new MenuItem { Header = h };
                    foreach (var c0 in list.Where(chOk)) { var c = c0; pa(sm, (pipCh?.Url == c.Url ? "● " : "") + c.Name, () => pipShow(c), true); }
                    if (sm.Items.Count > 0) to.Items.Add(sm);
                };
                Func<string, IptvChannel> byUrl = u => _iptvPlaying.TryGetValue(u, out var c1) ? c1 : allc.FirstOrDefault(c => c.Url == u);
                var tvM = new MenuItem { Header = L.T("📺 Telewizja") };
                chSub(tvM, L.T("🕘 Ostatnio oglądane"), IptvSt.Recent.Select(byUrl).Take(20));
                chSub(tvM, L.T("⭐ Ulubione"), IptvSt.Fav.Select(byUrl).Where(c => c != null).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));
                if (tvNow) chSub(tvM, L.T("📺 Kanały tej grupy"), _playlist.Where(u => _iptvPlaying.ContainsKey(u)).Select(u => _iptvPlaying[u]).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));
                var grM = new MenuItem { Header = L.T("📂 Wszystkie grupy") };
                foreach (var g0 in allc.Select(c => c.Group).Distinct().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
                { var g = g0; chSub(grM, g.Length == 0 ? L.T("(bez grupy)") : g, allc.Where(c => c.Group == g).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)); }
                if (grM.Items.Count > 0) tvM.Items.Add(grM);
                if (tvM.Items.Count > 0) cm.Items.Add(tvM);
                var fm = new MenuItem { Header = L.T("🎬 Filmy") };
                var libF = LoadLibrary().Where(v => v.Peer == null && v.Path != p0 && FilmPlayable(v.Path)).ToList();
                Action<string, IEnumerable<LibraryVideo>> fSub = (h, list) =>
                {
                    var sm = new MenuItem { Header = h };
                    foreach (var v0 in list) { var v = v0; pa(sm, (pipCh?.Url == v.Path ? "● " : "") + ShownTitle(v, MetaFor(v.Path)), () => pipShow(pipFilm(v.Path)), true); }
                    if (sm.Items.Count > 0) fm.Items.Add(sm);
                };
                fSub(L.T("🕘 Ostatnio oglądane"), libF.Where(v => v.Last > 0).OrderByDescending(v => v.Last).Take(20));
                var favF = LoadPlaylists().Named.TryGetValue("⭐ Ulubione", out var fl) ? fl : new List<string>();
                fSub(L.T("⭐ Ulubione"), favF.Select(u => libF.FirstOrDefault(v => string.Equals(v.Path, u, StringComparison.OrdinalIgnoreCase))).Where(v => v != null).OrderBy(v => ShownTitle(v, MetaFor(v.Path)), StringComparer.CurrentCultureIgnoreCase));
                var recD = TvRecDirs(); var allF = libF.Where(v => !IsShared(v) && !IsTvRec(v.Path, recD)).GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();   // tylko ten komputer, bez powtórek (wspólna - osobno)   // nagrania TV - tylko w "Nagrane z TV"
                fSub(L.T("📚 Wszystkie filmy (") + allF.Count + ")", allF.OrderBy(v => ShownTitle(v, MetaFor(v.Path)), StringComparer.CurrentCultureIgnoreCase));
                if (fm.Items.Count > 0) fm.Items.Add(new Separator());
                pa(fm, L.T("📂 Otwórz plik…"), () => { var fs = PickVideoFiles(false, Window.GetWindow(overlay) ?? win); if (fs != null && fs.Length > 0) pipShow(pipFilm(fs[0])); }, true);
                cm.Items.Add(fm);
            };
            _pipShow = ch => { if (_vlcWin != win) return false; var p1 = _playlist.Count > _playIndex ? _playlist[_playIndex] : null; if (p1 == ch.Url) return false; pipShow(ch); return true; };   // z okna TV
            _pipShowFilm = path1 => { if (_vlcWin != win || !File.Exists(path1)) return false; var p1 = _playlist.Count > _playIndex ? _playlist[_playIndex] : null; if (string.Equals(p1, path1, StringComparison.OrdinalIgnoreCase)) return false; pipShow(pipFilm(path1)); return true; };
            win.Closed += (s, e) => { _pipShow = null; _pipShowFilm = null; };
            // szybkie ikony: dzialanie i stan (nagrywanie na czerwono, rzeczy z TV tylko przy telewizji)
            Func<IptvChannel> qTvCh = () => { var p1 = _playlist.Count > _playIndex ? _playlist[_playIndex] : null; return p1 != null && _iptvPlaying.TryGetValue(p1, out var c1) ? c1 : null; };
            Action updQuick = () =>
            {
                var c = qTvCh(); bool rec = c != null && IsRecording(c.Url);
                var recAll = IptvSt.Jobs.Where(x => x.Kind == "rec" && _recording.ContainsKey(x.Id)).ToList();
                qRec.IsEnabled = c != null && !rec && !c.ArchiveStart.HasValue; qRecStop.IsEnabled = recAll.Count > 0;   // zatrzymanie - kazdego trwajacego nagrania, takze innego kanalu
                if (qRec.Content is System.Windows.Shapes.Path rp) rp.Fill = rec ? Brushes.Red : Brushes.White;
                qRec.ToolTip = rec ? L.T("⏺ Trwa nagrywanie tego kanału") : recAll.Count > 0 ? L.T("Nagrywaj także ten kanał (") + string.Join(", ", recAll.Select(x => x.Channel)) + L.T(" nagrywa się dalej)") : L.T("Nagrywaj ten kanał TV");
                qRecStop.ToolTip = recAll.Count == 1 ? L.T("Zatrzymaj nagrywanie: ") + recAll[0].Channel : recAll.Count > 1 ? L.T("Zatrzymaj nagrywanie – wybierz kanał") : L.T("Zatrzymaj nagrywanie");
                qRefresh.IsEnabled = c != null;
                // znacznik nagrywania i licznik polaczen
                var recs = IptvSt.Jobs.Where(x => x.Kind == "rec" && _recording.ContainsKey(x.Id)).ToList();
                recPill.Visibility = recs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                recOn.Visibility = recs.Count > 0 && !mini0 ? Visibility.Visible : Visibility.Collapsed;
                recOnText.Text = recs.Count == 0 ? "" : recs.Count > 1 ? L.T("Trwa nagrywanie: ") + string.Join(", ", recs.Select(x => x.Channel))
                    : c != null && (c.LiveUrl ?? c.Url) == recs[0].Url ? L.T("Trwa nagrywanie tego kanału") : L.T("Trwa nagrywanie: ") + recs[0].Channel;
                if (recs.Count == 1) recPill.Content = (c != null && (c.LiveUrl ?? c.Url) == recs[0].Url ? "⏺ REC" : "⏺ REC  " + recs[0].Channel) + L.T(" · do ") + LocalOf(recs[0].Stop).ToString("HH:mm");   // krótko; pełny opis w podpowiedzi
                else if (recs.Count > 1) recPill.Content = L.T("⏺ REC  ") + recs.Count + L.T(" kanały");
                int used = (c != null && curRecFile == null ? 1 : 0) + recs.Count + (pipCh != null && pipCh.Group != pipFilmGrp ? 1 : 0);
                connText.Visibility = IptvInstalled && (used > 1 || IptvSt.MaxConn > 0 && used >= IptvSt.MaxConn) ? Visibility.Visible : Visibility.Collapsed;   // samo oglądanie (1 połączenie) - bez napisu
                connText.Content = "🔌 " + used + (IptvSt.MaxConn > 0 ? " / " + IptvSt.MaxConn : "");
                connText.Foreground = IptvSt.MaxConn > 0 && used >= IptvSt.MaxConn ? Brushes.Orange : Brushes.Silver;
                connText.ToolTip = L.T("Połączenia z dostawcą IPTV teraz: ") + used + L.T(" (oglądanie, nagrania, podgląd PiP)") + (IptvSt.MaxConn > 0 ? L.T("\nLimit dostawcy: ") + IptvSt.MaxConn : L.T("\nKliknij, aby podać limit dostawcy"));
                bool tvOn = IptvInstalled; qTv.IsEnabled = qGuide.IsEnabled = qFavTv.IsEnabled = qRecDir.IsEnabled = qRecSet.IsEnabled = tvOn;
                // tryb: telewizja albo film - przyciski drugiego trybu i nieczynne schowane (zamiast szarych)
                Func<bool, Visibility> vis = on => on ? Visibility.Visible : Visibility.Collapsed;
                bool isTv = c != null;
                qRec.Visibility = vis(qRec.IsEnabled); qRecStop.Visibility = vis(recAll.Count > 0); qRefresh.Visibility = vis(isTv);
                qGuide.Visibility = qRecSet.Visibility = vis(isTv && tvOn); qRecDir.Visibility = vis(tvOn && (isTv || recAll.Count > 0));
                qFavTv.Visibility = pipBtn.Visibility = vis(tvOn); qTv.Visibility = vis(tvOn && isTv);   // przy filmie kanały otwiera „📺 TV” po lewej
                qLists.Visibility = vis(!isTv);   // ⭐ ulubione filmy i kanały w obu trybach - szybkie przejście na drugą stronę
                lib.Visibility = hist.Visibility = vis(!isTv && !narrow);
                swapTv = !isTv; swap.Visibility = vis(!isTv ? tvOn : true);
                var swC = isTv ? "🎬 " + L.T("Filmy") : "📺 " + L.T("TV");
                if ((swap.Content as string) != swC) swap.ToolTip = isTv ? L.T("Przełącz na filmy – biblioteka filmów\nPrawy przycisk: ostatnio oglądane filmy") : L.T("Przełącz na telewizję – lista kanałów\nPrawy przycisk: ostatnio oglądane kanały");
                if ((swap.Content as string) != swC) swap.Content = swC;   // tylko przy zmianie - bez przerysowania paska co 2 s
                tools.Visibility = vis(!narrow); toolsRow.Visibility = vis(!narrow && GetPref("pasek-narzedzia") == "1");
            };
            updMode = updQuick;
            var qTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            qTimer.Tick += (s, e) => updQuick(); qTimer.Start(); win.Closed += (s, e) => qTimer.Stop();
            mp.Playing += (s, e) => Dispatcher.BeginInvoke(guard(updQuick));
            qRec.Click += (s, e) => { var c = qTvCh(); if (c != null) { RecordNow(c, win); updQuick(); } };
            qRecStop.Click += (s, e) =>
            {   // biezacy kanal, jedyne nagranie albo wybor z listy (gdy nagrywa sie kilka)
                var c = qTvCh(); var recAll = IptvSt.Jobs.Where(x => x.Kind == "rec" && _recording.ContainsKey(x.Id)).ToList();
                if (c != null && IsRecording(c.Url)) StopRecording(c.Url);
                else if (recAll.Count == 1) { if (_recording.TryGetValue(recAll[0].Id, out var cts)) cts.Cancel(); }
                else if (recAll.Count > 1) { recPill.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); return; }
                updQuick();
            };
            qRecDir.Click += (s, e) => OpenRecDir();
            // ustawienia paska informacji - ikona obok 👁 (z menu ⚙ przeniesione tutaj)
            Action<ItemsControl> infoSettings = to =>
            {
                    var moveM = new MenuItem { Header = L.T("Pokazuj przy ruchu myszy"), IsCheckable = true, IsChecked = IptvSt.InfoOnMove };
                    moveM.Click += (q, w2) => { IptvSt.InfoOnMove = moveM.IsChecked; SaveIptv(IptvSt); showOsd(IptvSt.InfoOnMove ? L.T("Pasek informacji: przy ruchu myszy") : L.T("Pasek informacji: tylko klawisz I")); };
                    to.Items.Add(moveM);
                    var hideM = new MenuItem { Header = L.T("⏱ Znikanie po") };
                    foreach (var sec0 in new[] { 3, 5, 8, 10, 15, 0 })
                    {
                        var sec = sec0; var mi = new MenuItem { Header = sec == 0 ? L.T("nie pokazuj sam (tylko klawisz I)") : sec + " s", IsCheckable = true, IsChecked = IptvSt.InfoSec == sec };
                        mi.Click += (q, w2) => { IptvSt.InfoSec = sec; SaveIptv(IptvSt); showInfo(true); };
                        hideM.Items.Add(mi);
                    }
                    var alphaM = new MenuItem { Header = L.T("🌓 Przezroczystość tła") };
                    foreach (var a0 in new[] { 100, 85, 70, 55, 40 })
                    {
                        var al = a0; var mi = new MenuItem { Header = (100 - al) + " %" + (al == 100 ? L.T(" (pełne tło)") : ""), IsCheckable = true, IsChecked = IptvSt.InfoAlpha == al };
                        mi.Click += (q, w2) => { IptvSt.InfoAlpha = al; SaveIptv(IptvSt); showInfo(true); };
                        alphaM.Items.Add(mi);
                    }
                    var sizeM = new MenuItem { Header = L.T("🔠 Wielkość tekstu (względem okna)") };
                    foreach (var z0 in new[] { new object[] { 85, "Mały" }, new object[] { 100, "Normalny" }, new object[] { 125, "Duży" }, new object[] { 150, "Bardzo duży" } })
                    {
                        var z = (int)z0[0]; var mi = new MenuItem { Header = L.T((string)z0[1]), IsCheckable = true, IsChecked = IptvSt.InfoScale == z };
                        mi.Click += (q, w2) => { IptvSt.InfoScale = z; SaveIptv(IptvSt); showInfo(true); };
                        sizeM.Items.Add(mi);
                    }
                    to.Items.Add(hideM); to.Items.Add(alphaM); to.Items.Add(sizeM);
            };
            qInfoSet.Click += (s, e) => { var cm = new ContextMenu { PlacementTarget = qInfoSet, Placement = PlacementMode.Top }; infoSettings(cm); cm.IsOpen = true; };
            qBarTime.Click += (s, e) =>
            {   // czas wyswietlania paska
                var cm = new ContextMenu { PlacementTarget = qBarTime, Placement = PlacementMode.Top };
                cm.Items.Add(new MenuItem { Header = L.T("Pasek znika po:"), IsEnabled = false });
                foreach (var v0 in new[] { 2.5, 5.0, 10.0, 20.0, 0.0 })
                {
                    var v = v0; var mi = new MenuItem { Header = v == 0 ? L.T("nie znika (zawsze widoczny)") : v.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " s", IsCheckable = true, IsChecked = Math.Abs(barSec() - v) < 0.01 };
                    mi.Click += (q, w2) => { SetPref("pasek-czas", v.ToString(System.Globalization.CultureInfo.InvariantCulture)); if (v > 0) hideTimer.Interval = TimeSpan.FromSeconds(v); showOsd(v == 0 ? L.T("Pasek: zawsze widoczny") : L.T("Pasek znika po ") + v + " s"); };
                    cm.Items.Add(mi);
                }
                cm.Items.Add(new Separator());
                {   // ↕ płynna wysokość paska
                    double cur0 = double.TryParse(GetPref("pasek-wysokosc"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hv) ? hv : 1;
                    var hl = new TextBlock { Text = L.T("↕ Wysokość paska: ") + Math.Round(cur0 * 100) + " %", Margin = new Thickness(0, 0, 0, 4) };
                    var hs = new Slider { Minimum = 1, Maximum = 1.6, Value = Math.Max(1, Math.Min(1.6, cur0)), Width = 220, IsMoveToPointEnabled = true, SmallChange = 0.05, LargeChange = 0.1, TickFrequency = 0.05, IsSnapToTickEnabled = true };
                    hs.ValueChanged += (q, w2) => { hl.Text = L.T("↕ Wysokość paska: ") + Math.Round(hs.Value * 100) + " %"; applyBarHeight(hs.Value); SetPref("pasek-wysokosc", hs.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)); };
                    var hp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) }; hp.Children.Add(hl); hp.Children.Add(hs);
                    cm.Items.Add(new MenuItem { Header = hp, StaysOpenOnClick = true });
                    cm.Items.Add(new Separator());
                }
                cm.Items.Add(new MenuItem { Header = L.T("Przezroczystość paska:"), IsEnabled = false });
                int curA = int.TryParse(GetPref("pasek-alfa"), out var ca) ? ca : 15;
                foreach (var a0 in new[] { 0, 15, 30, 45, 60 })
                {
                    var a = a0; var mi = new MenuItem { Header = a + " %" + (a == 0 ? L.T(" (pełne tło)") : ""), IsCheckable = true, IsChecked = curA == a };
                    mi.Click += (q, w2) => { SetPref("pasek-alfa", a.ToString()); controls.Background = barBg(a); };
                    cm.Items.Add(mi);
                }
                cm.IsOpen = true;
            };
            recPill.Click += (s, e) =>
            {   // trwajace nagrania: zatrzymaj / przedluz
                var cm = new ContextMenu { PlacementTarget = recPill, Placement = PlacementMode.Top };
                foreach (var j0 in IptvSt.Jobs.Where(x => x.Kind == "rec" && _recording.ContainsKey(x.Id)).ToList())
                {
                    var j = j0; var m = new MenuItem { Header = "⏺ " + j.Channel + (string.IsNullOrEmpty(j.Title) ? "" : " – " + j.Title) + L.T(" (do ") + LocalOf(j.Stop).ToString("HH:mm") + ")" };
                    var st0 = new MenuItem { Header = L.T("⏹ Zatrzymaj nagrywanie") }; st0.Click += (q, w2) => { if (_recording.TryGetValue(j.Id, out var cts)) cts.Cancel(); updQuick(); }; m.Items.Add(st0);
                    foreach (var min0 in new[] { 15, 30, 60 }) { var min = min0; var ex = new MenuItem { Header = L.T("➕ Przedłuż o ") + min + " min" }; ex.Click += (q, w2) => { j.Stop += min * 60000L; SaveIptv(IptvSt); _iptvJobsChanged?.Invoke(); showOsd(L.T("⏺ Nagranie do ") + LocalOf(j.Stop).ToString("HH:mm")); updQuick(); }; m.Items.Add(ex); }
                    cm.Items.Add(m);
                }
                if (cm.Items.Count > 0) cm.IsOpen = true;
            };
            connText.Click += (s, e) =>
            {   // limit polaczen dostawcy - ostrzezenie tylko po jego przekroczeniu
                var cm = new ContextMenu { PlacementTarget = connText, Placement = PlacementMode.Top };
                cm.Items.Add(new MenuItem { Header = L.T("Mój dostawca IPTV pozwala na:"), IsEnabled = false });
                foreach (var n0 in new[] { 1, 2, 3, 4, 5 }) { var n = n0; var mi = new MenuItem { Header = n + (n == 1 ? L.T(" połączenie") : n < 5 ? L.T(" połączenia") : L.T(" połączeń")), IsCheckable = true, IsChecked = IptvSt.MaxConn == n }; mi.Click += (q, w2) => { IptvSt.MaxConn = n; SaveIptv(IptvSt); updQuick(); }; cm.Items.Add(mi); }
                cm.IsOpen = true;
            };
            qRecSet.Click += (s, e) => RecMarginDialog(Window.GetWindow(overlay) ?? win);
            qTv.Click += (s, e) => ShowIptv(Window.GetWindow(overlay) ?? win);
            swap.Click += (s, e) => { if (swapTv) ShowIptv(Window.GetWindow(overlay) ?? win); else ShowVideoLibrary(Window.GetWindow(overlay) ?? win); };
            qGuide.Click += (s, e) => ShowEpgGuide(Window.GetWindow(overlay) ?? win, qTvCh()?.LiveUrl ?? qTvCh()?.Url);
            qInfo.Click += (s, e) => showInfo(true);
            qRefresh.Click += (s, e) => { if (qTvCh() != null) { showOsd(L.T("🔄 Odświeżam kanał…")); _vlcLoad(_playIndex); } };
            Action<Button, Action<ContextMenu>> qMenu = (b, fill) => b.Click += (s, e) => { var cm = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Top }; fill(cm); if (cm.Items.Count == 0) cm.Items.Add(new MenuItem { Header = L.T("(pusto)"), IsEnabled = false }); cm.IsOpen = true; };
            qMenu(qLists, cm =>
            {
                var pls = LoadPlaylists();
                Action<string, List<string>> one = (t, paths) => { var mi = new MenuItem { Header = t }; mi.Click += (q, w2) => PlayVideos(paths.ToArray(), 0); cm.Items.Add(mi); };
                if (pls.Mine.Count > 0) one(L.T("📋 Moja kolejka (") + pls.Mine.Count + ")", pls.Mine);
                foreach (var n in pls.Named.Keys.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)) one("🎞 " + n + " (" + pls.Named[n].Count + ")", pls.Named[n]);
            });
            qFavTv.Click += (s, e) => ShowFavChannels(qFavTv);   // ⭐ ulubione kanały - jak 🕘 historia: logo, klik gra, prawy przycisk: sortowanie, kolejność, usuwanie
            qFavFilm.Click += (s, e) => ShowFavFilms(qFavFilm);   // ⭐ ulubione filmy - plakat, obejrzano, prawy przycisk: sortowanie, kolejność, usuwanie
            // prawy przycisk na gwiazdkach: przewijana lista wszystkich filmów / kanałów grupy, która leci (albo ostatnio oglądanej)
            // sortowanie jak w ulubionych (prawy przycisk na liście): A–Z / kolejność biblioteki albo listy dostawcy / ostatnio oglądany
            var allPop = new System.Windows.Controls.Primitives.Popup[1];
            Action showAllFilms = null; showAllFilms = () =>
            {
                var recDirs = TvRecDirs(); int sort = FavSort("wszystkie-filmy");
                var films = LoadLibrary().Where(v => v.Peer == null && !IsShared(v) && !IsTvRec(v.Path, recDirs) && FilmPlayable(v.Path))   // tylko filmy tego komputera, bez wspólnej grupy
                    .GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())   // bez powtórek
                    .Select(v => (v, m: MetaFor(v.Path))).Select(x => (x.v, x.m, t: ShownTitle(x.v, x.m))).ToList();
                if (sort == 0) films = films.OrderBy(x => x.t, StringComparer.CurrentCultureIgnoreCase).ToList();
                else if (sort == 2) films = films.OrderByDescending(x => x.v.Last).ToList();
                var paths = films.Select(x => x.v.Path).ToArray();
                var rows = films.Select((x, i) => { int i0 = i; var pf = x.m?.Poster != null ? System.IO.Path.Combine(PosterDir, x.m.Poster) : null; return new FavRow
                {
                    Title = x.t, Tip = x.v.Path,
                    Sub = string.Join("   ·   ", new[] { x.m?.Year > 0 ? x.m.Year.ToString() : null, x.m?.Rating > 0 ? "★ " + x.m.Rating.ToString("0.0") : null, x.m?.Genres.Count > 0 ? string.Join(", ", x.m.Genres.Take(2)) : null }.Where(t => t != null)),
                    ImgAsync = pf == null ? null : () => System.Threading.Tasks.Task.Run(() => File.Exists(pf) ? LoadLogo(pf, 150) : null),   // plakaty w tle - lista otwiera się od razu
                    Play = () => PlayVideos(paths, i0),
                    Menu = cm => FavItem(cm, "▶ " + L.T("Odtwórz"), () => PlayVideos(paths, i0))
                }; }).ToList();
                allPop[0] = FavPopup(qFavFilm, "🎬 " + L.T("Wszystkie filmy"), L.T("Biblioteka filmów jest pusta"), rows, m => FavSortItems(m, "wszystkie-filmy", true, allPop, showAllFilms), 34, 50);
            };
            Action showGroupCh = null; showGroupCh = () =>
            {
                var pl = KnownPlaylist(); var cur = qTvCh() ?? (chHist.Count > 0 ? chOf(chHist[chHist.Count - 1]) : null);
                var grp = cur?.Group ?? "";
                var chans = pl?.Channels.Where(c => (c.Group ?? "") == grp).GroupBy(c => c.Url).Select(g => g.First()).ToList() ?? new List<IptvChannel>();
                if (FavSort("grupa-tv") == 0) chans = chans.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();   // 1 = kolejność z listy dostawcy
                var rows = chans.Select(c => { var c0 = c; var logo = c.Logo; return new FavRow
                {
                    Title = c.Name + (cur != null && c.Url == cur.Url ? "  ◀" : ""), Sub = "", Tip = c.Name, ImgAsync = () => GetLogo(logo),
                    Play = () => PlayChannelInGroup(c0, pl),
                    Menu = cm => FavItem(cm, "▶ " + L.T("Odtwórz"), () => PlayChannelInGroup(c0, pl))
                }; }).ToList();
                allPop[0] = FavPopup(qFavTv, "📺 " + (grp.Length > 0 ? grp : L.T("Kanały")), L.T("Brak kanałów – włącz najpierw jakiś kanał TV"), rows, m => FavSortItems(m, "grupa-tv", false, allPop, showGroupCh), 44, 32);
            };
            qFavFilm.MouseRightButtonUp += (s, e) => { e.Handled = true; showAllFilms(); };
            qFavTv.MouseRightButtonUp += (s, e) => { e.Handled = true; showGroupCh(); };
            updQuick();
            pipBtn.Click += (s, e) => { var cm = new ContextMenu { PlacementTarget = pipBtn, Placement = PlacementMode.Top }; pipItems(cm); cm.IsOpen = true; };
            win.LocationChanged += (s, e) => pipPlace();
            win.SizeChanged += (s, e) => pipPlace();
            // zminimalizowany odtwarzacz: PiP zostaje z obrazem na pulpicie (bez właściciela, zawsze na wierzchu) - wcześniej chowany, a jego przyciski zostawały na czarnym tle
            win.StateChanged += (s, e) => { if (pipWin != null) { if (win.WindowState == WindowState.Minimized) { pipWin.Owner = null; pipWin.Topmost = true; pipWin.Show(); }
                else { pipWin.Topmost = win.Topmost; pipWin.Show(); Dispatcher.BeginInvoke(pipPlace, DispatcherPriority.Background); } } };
            win.Closed += (s, e) => pipClose();
            // po Stop zamiast czarnego ekranu: film - biblioteka filmow, telewizja - lista kanalow
            Action afterStop = () => { var p0 = _playlist.Count > _playIndex ? _playlist[_playIndex] : null; if (p0 != null && _iptvPlaying.ContainsKey(p0)) showChannels(true); else ShowVideoLibrary(win); };
            stop.Click += (s, e) => { userStopped = true; savePos(); vlcRun(() => mp.Stop()); LeaveTogether(); afterStop(); };   // Stop konczy wspolne ogladanie tylko tutaj
            prev.Click += (s, e) => step(-1);
            next.Click += (s, e) => step(1);
            back10.Click += (s, e) => jump(-10000);
            chBack.Click += (s, e) => { if (chPos <= 0 || chHist.Count < 2) { showOsd(L.T("Brak poprzedniego kanału")); return; } chJump(Math.Min(chPos, chHist.Count) - 1); };
            chFirst.Click += (s, e) => { if (chHist.Count < 2 || chPos == 0) { showOsd(L.T("To jest pierwszy kanał")); return; } chJump(0); };
            // 🕘 historia kanałów - ten sam wygląd co ⭐ ulubione: logo, grupa, klik - przełącz; prawy przycisk: ulubione, usuń z historii, sortowanie
            Action showChHist = null; FrameworkElement chAnchor = chHistBtn;   // 🕘 przy kanałach albo prawy przycisk na „📺 TV”
            showChHist = () =>
            {
                var cur = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                bool recent = FavSort("historia-tv") == 1;
                var order = Enumerable.Reverse(chHist).Distinct().Where(u => u != cur).Select(chOf).Where(c => c != null).Take(20).ToList();   // 20 ostatnio oglądanych (jak 🕘 historia filmów)
                if (!recent) order = order.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                var pop = new System.Windows.Controls.Primitives.Popup[1];
                void Sort(ItemsControl m)
                {
                    foreach (var (n0, h) in new[] { (0, L.T("Sortuj: A–Z")), (1, L.T("Sortuj: ostatnio oglądany na górze")) })
                    { var n1 = n0; var mi = FavItem(m, h, () => { SetPref("sort-historia-tv", n1.ToString()); pop[0].IsOpen = false; showChHist(); }); mi.IsCheckable = true; mi.IsChecked = (recent ? 1 : 0) == n1; }
                }
                var rows = order.Select(c => { var ch = c; return new FavRow
                {
                    Title = ch.Name, Sub = (IptvSt.Fav.Contains(ch.Url) ? "⭐ " : "") + (string.IsNullOrEmpty(ch.Group) ? "" : "📺 " + ch.Group), Tip = ch.Name, ImgAsync = () => GetLogo(ch.Logo),
                    Play = () => chGo(ch),
                    Menu = cm =>
                    {
                        FavItem(cm, "▶ " + L.T("Odtwórz"), () => { pop[0].IsOpen = false; chGo(ch); });
                        bool isF = IptvSt.Fav.Contains(ch.Url);
                        FavItem(cm, isF ? "☆ " + L.T("Usuń z ulubionych") : "⭐ " + L.T("Dodaj do ulubionych"), () => { if (isF) IptvSt.Fav.Remove(ch.Url); else IptvSt.Fav.Add(ch.Url); SaveIptv(IptvSt); updQuick(); pop[0].IsOpen = false; showChHist(); });
                        cm.Items.Add(new Separator());
                        FavItem(cm, "🕘 " + L.T("Usuń z historii oglądania"), () => { chHist.RemoveAll(u => u == ch.Url); chPos = Math.Min(chPos, chHist.Count - 1); SaveIptv(IptvSt); pop[0].IsOpen = false; showChHist(); });
                    }
                }; }).ToList();
                pop[0] = FavPopup(chAnchor, "🕘 " + L.T("Ostatnio oglądane kanały"), L.T("Historia jest pusta – przełącz kanał"), rows, Sort, 44, 32);
            };
            chHistBtn.Click += (s, e) => { chAnchor = chHistBtn; showChHist(); };
            // prawy przycisk na przełączniku: 20 ostatnich z drugiej strony – przy filmie kanały, przy TV filmy (szybki powrót)
            swap.MouseRightButtonUp += (s, e) => { e.Handled = true; if (swapTv) { chAnchor = swap; showChHist(); } else ShowHistory(swap); };
            fwd10.Click += (s, e) => jump(10000);
            speed.Click += (s, e) => setSpeed(speedIdx == speeds.Length - 1 ? -speedIdx : 1);
            speed.MouseRightButtonUp += (s, e) => { speedIdx = 3; setSpeed(0); e.Handled = true; };
            full.Click += (s, e) => toggleFull();
            // zawsze na wierzchu (zapamietywane); aktywny przycisk podswietlony
            Action showTop = () => { top.Content = PlayerIcon(_settings.VlcTopmost ? "pinned" : "pin"); top.Background = _settings.VlcTopmost ? new SolidColorBrush(Color.FromArgb(0x55, 0x3B, 0x82, 0xF6)) : Brushes.Transparent; };
            top.Click += (s, e) =>
            {
                _settings.VlcTopmost = !_settings.VlcTopmost;
                if (!full0) win.Topmost = _settings.VlcTopmost;
                showTop(); showOsd(_settings.VlcTopmost ? L.T("Zawsze na wierzchu") : L.T("Na wierzchu: wyłączone"));
                saveLector();
            };
            win.Topmost = _settings.VlcTopmost; showTop();
            hist.Click += (s, e) => ShowHistory(hist);
            mute.Click += (s, e) => toggleMute();
            // nad filmem: wlascicielem jest przezroczysta warstwa na obrazie (osobne okno nad odtwarzaczem),
            // inaczej warstwa przykrywa biblioteke i jej X / przyciski nad filmem nie reaguja na klik
            lib.Click += (s, e) => ShowVideoLibrary(Window.GetWindow(overlay) ?? win);
            snap.Click += (s, e) =>
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                var file = System.IO.Path.Combine(dir, "Velivo-klatka-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                if (mp.TakeSnapshot(0, file, 0, 0)) ShowToast(L.T("📷 Zapisano klatkę: ") + System.IO.Path.GetFileName(file), file);
            };
            seek.PreviewMouseDown += (s, e) => seeking = true;
            seek.PreviewMouseUp += (s, e) => { if (mp.Length > 0) { seekTo = (long)(seek.Value / 1000 * mp.Length); seekAt = DateTime.Now; mp.Time = seekTo; TogetherSend("seek", seekTo); } seeking = false; };
            seek.ValueChanged += (s, e) => { if (seeking && mp.Length > 0) timeNow.Text = FormatTime((long)(seek.Value / 1000 * mp.Length)); };

            // menu napisow / dzwieku / proporcji budowane przy otwarciu (sciezki znane dopiero po starcie filmu)
            Action<Button, Action<ContextMenu>> popup = (btn, build) => btn.Click += (s, e) =>
            {
                var cm = new ContextMenu { PlacementTarget = btn, Placement = PlacementMode.Top };
                build(cm);
                cm.IsOpen = true;
            };
            popup(subs, cm =>
            {
                int curSpu = mp.Spu;
                foreach (var t in mp.SpuDescription)
                {
                    var id = t.Id;
                    var mi = new MenuItem { Header = id < 0 ? L.T("Wyłącz napisy") : t.Name, IsCheckable = true, IsChecked = id == curSpu };
                    var nm = id < 0 ? "-" : t.Name;
                    mi.Click += (a, b) => { mp.SetSpu(id); rememberTrack(true, nm); };
                    cm.Items.Add(mi);
                }
                if (cm.Items.Count > 0) cm.Items.Add(new Separator());
                var load = new MenuItem { Header = L.T("📂 Wczytaj napisy z pliku…") };
                load.Click += (a, b) =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("Napisy"), Filter = L.T("Napisy") + "|" + string.Join(";", SubtitleExts.Select(x => "*" + x)) };
                    if (dlg.ShowDialog(win) == true) { mp.AddSlave(MediaSlaveType.Subtitle, new Uri(dlg.FileName).AbsoluteUri, true); loadSubs(dlg.FileName); }
                };
                cm.Items.Add(load);
                // pliki napisow obok filmu (np. pobrane) - do Kosza
                var cur = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                var subFiles = cur != null && !cur.Contains("://") ? SubtitleFilesFor(cur) : new List<string>();
                if (subFiles.Count > 0)
                {
                    var del = new MenuItem { Header = L.T("🗑 Usuń plik napisów") };
                    foreach (var f0 in subFiles)
                    {
                        var f = f0; var mi = new MenuItem { Header = System.IO.Path.GetFileName(f) };
                        mi.Click += (a, b) =>
                        {
                            if (!AskYes(win, string.Format(L.T("Usunąć plik napisów „{0}” (do Kosza)?"), System.IO.Path.GetFileName(f)))) return;
                            try
                            {
                                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                                if (lc.File == f) { mp.SetSpu(-1); loadSubs(null); }   // usuniete wlasnie uzywane - wylaczone (lektor tez)
                                showOsd(L.T("Napisy usunięte"));
                            }
                            catch (Exception ex) { App.LogError(ex); showOsd(L.T("Nie udało się usunąć napisów")); }
                        };
                        del.Items.Add(mi);
                    }
                    cm.Items.Add(del);
                }
                var delay = new MenuItem { Header = L.T("⏱ Przesunięcie napisów (na żywo)…") };
                delay.Click += (a, b) => openSubDelay();
                cm.Items.Add(delay);
            });
            popup(audio, cm =>
            {
                int cur = mp.AudioTrack;
                var info = new Dictionary<int, string>();   // kodek i kanaly sciezki - widac, ktora moze nie grac
                try { foreach (var mt in mp.Media?.Tracks ?? new MediaTrack[0]) if (mt.TrackType == TrackType.Audio) info[mt.Id] = new string(BitConverter.GetBytes(mt.Codec).Select(b => (char)b).ToArray()).Trim('\0', ' ').ToUpperInvariant() + (mt.Data.Audio.Channels > 0 ? " " + mt.Data.Audio.Channels + "ch" : ""); } catch (Exception) { }
                foreach (var t in mp.AudioTrackDescription)
                {
                    var id = t.Id;
                    var mi = new MenuItem { Header = id < 0 ? L.T("Bez dźwięku") : t.Name + (info.TryGetValue(id, out var ci) ? "   ·  " + ci : ""), IsCheckable = true, IsChecked = id == cur };
                    var nm = t.Name;
                    mi.Click += (a, b) => { if (id >= 0) { mp.SetAudioTrack(id); rememberTrack(false, nm); } else mp.SetAudioTrack(id); };
                    cm.Items.Add(mi);
                }
                if (cm.Items.Count == 0) cm.Items.Add(new MenuItem { Header = L.T("(brak ścieżek)"), IsEnabled = false });
                cm.Items.Add(new Separator());
                var sur = new MenuItem { Header = L.T("Dźwięk 5.1 bez miksowania do stereo (zestaw kina domowego)"), IsCheckable = true, IsChecked = Surround51 };
                sur.Click += (a, b) => { SetPref("dzwiek-51", sur.IsChecked ? "1" : "0"); restartPlayer(); };   // zmiana wyjscia dzwieku - odtwarzacz od nowa w tym samym miejscu
                cm.Items.Add(sur);
                if (Edycja.Premium)   // 💎 normalizacja i Audio Monitor - Premium
                {
                    // stan od razu widac (ptaszek) i jednym kliknieciem wl./wyl. - ten sam przelacznik co ⏻ w widzecie
                    // 🎚 INTELIGENTNA NORMALIZACJA: włącz/wyłącz, tryb, intensywność i stan na żywo (wszystko działa od razu na dźwięk)
                    cm.Items.Add(new MenuItem { Header = L.T("INTELIGENTNA NORMALIZACJA"), IsEnabled = false, FontWeight = FontWeights.Bold });
                    {
                        bool live = AbProc.Active && !AbBypass && AbWanted;
                        string F(double v) => double.IsNaN(v) || double.IsInfinity(v) || v < -70 ? "–" : v.ToString("0.0");
                        cm.Items.Add(new MenuItem { Header = live ? L.T("Teraz: ") + F(AbProc.Out.ShortTerm) + L.T(" LUFS · zmiana ") + (AbProc.GainDb >= 0 ? "+" : "") + F(AbProc.GainDb) + L.T(" dB · procesor: pracuje") : L.T("Procesor: nie pracuje (wyłączony albo nic nie gra)"), IsEnabled = false });
                    }
                    var abOn = new MenuItem { Header = L.T("Włączona"), IsCheckable = true, IsChecked = AbWanted && AbProfileName != "off" };
                    abOn.Click += (a, b) => { if (!abOn.IsChecked) AbSetProfile("off"); else { if (AbBypass) AbSetBypass(false); if (AbProfileName == "off") AbSetProfile("auto"); } };
                    cm.Items.Add(abOn);
                    foreach (var pm in new[] { "auto", "limit" })
                    {
                        var mi = new MenuItem { Header = L.T("Tryb: ") + AbProfileLabel(pm), IsCheckable = true, IsChecked = !AbBypass && AbProfileName == pm, ToolTip = AbProfileTip(pm) };
                        mi.Click += (a, b) => { if (AbBypass) AbSetBypass(false); AbSetProfile(pm); };
                        cm.Items.Add(mi);
                    }
                    foreach (var sl in AbSilas)
                    {
                        var mi = new MenuItem { Header = L.T("Intensywność: ") + AbSilaLabel(sl), IsCheckable = true, IsChecked = AbSilaName == sl, ToolTip = AbSilaTip(sl), IsEnabled = AbProfileName != "off" };   // przy Wył. nieaktywna
                        mi.Click += (a, b) => AbSetSila(sl);
                        cm.Items.Add(mi);
                    }
                    var abm = new MenuItem { Header = L.T("🎚 Audio Monitor i korektor…") };
                    abm.Click += (a, b) => ShowAudioMonitor();
                    cm.Items.Add(abm);
                    // 🎛 wtyczki VST3 (efekty) - rozwijana lista: całość wł./wył., włączone (każda osobno + okno producenta), dostępne do dodania
                    VstLoad();
                    var vm = new MenuItem { Header = L.T("🎛 Wtyczki VST3") };
                    var vOn = new MenuItem { Header = L.T("VST3 aktywne"), IsCheckable = true, IsChecked = Vst3.Active, IsEnabled = Vst3.Chain.Length > 0, ToolTip = L.T("Twoje wtyczki przejmują przetwarzanie dźwięku filmu (korektor i wyrównywanie Velivo są wtedy pominięte)") };
                    vOn.Click += (a, b) => { Vst3.Active = vOn.IsChecked; VstSave(); if (Vst3.Active) AbNeedsRestartHint(); };
                    vm.Items.Add(vOn); vm.Items.Add(new Separator());
                    vm.Items.Add(new MenuItem { Header = L.T("Włączone w łańcuchu") + " (" + Vst3.Chain.Length + ")", IsEnabled = false, FontWeight = FontWeights.Bold });
                    foreach (var vp in Vst3.Chain)
                    {
                        var p = vp; var it = new MenuItem { Header = p.Name, ToolTip = VstTip(p.Path) };
                        var onIt = new MenuItem { Header = L.T("Włączona"), IsCheckable = true, IsChecked = p.On, ToolTip = L.T("Włącz / wyłącz tę wtyczkę") };
                        onIt.Click += (a, b) => { p.On = onIt.IsChecked; VstSave(); };
                        var ed = new MenuItem { Header = L.T("Ustaw…"), ToolTip = L.T("Otwiera okno wtyczki (ustawienia producenta)") };
                        ed.Click += (a, b) => { try { p.ShowEditor(p.Name); } catch (Exception ex) { ShowToast("🎛 " + ex.Message, null); } };
                        var rm = new MenuItem { Header = L.T("Usuń"), ToolTip = L.T("Usuwa wtyczkę z łańcucha") };
                        rm.Click += (a, b) => VstSet(Vst3.Chain.Where(x => x != p).ToArray(), p);
                        it.Items.Add(onIt); it.Items.Add(ed); it.Items.Add(rm); vm.Items.Add(it);
                    }
                    var av = new MenuItem { Header = L.T("Dostępne") };
                    var found = Vst3.Scan().GroupBy(x => System.IO.Path.GetFileNameWithoutExtension(x), StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                        .Where(x => !Vst3.Chain.Any(c => string.Equals(System.IO.Path.GetFileNameWithoutExtension(c.Path), System.IO.Path.GetFileNameWithoutExtension(x), StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(x => System.IO.Path.GetFileNameWithoutExtension(x), StringComparer.CurrentCultureIgnoreCase).ToList();
                    if (found.Count == 0) av.Items.Add(new MenuItem { Header = L.T("Nie znaleziono wtyczek VST3 w folderze Common Files\\VST3"), IsEnabled = false });
                    foreach (var f0 in found)
                    {
                        var ff = f0; var mi = new MenuItem { Header = System.IO.Path.GetFileNameWithoutExtension(ff), ToolTip = VstTip(ff) };
                        mi.Click += (a, b) =>
                        {
                            try { var p = Vst3.Plugin.Load(ff); VstSet(Vst3.Chain.Concat(new[] { p }).ToArray()); if (!Vst3.Active) { Vst3.Active = true; VstSave(); AbNeedsRestartHint(); } ShowToast("🎛 " + p.Name + L.T(" – dodana i włączona"), null); }
                            catch (Exception ex) { App.LogError(new Exception("VST3: " + ff, ex)); ShowToast(L.T("🎛 Nie udało się wczytać wtyczki: ") + ex.Message, null); }
                        };
                        av.Items.Add(mi);
                    }
                    av.Header = L.T("Dostępne") + " (" + found.Count + ")";
                    vm.Items.Add(new Separator()); vm.Items.Add(av);
                    var fr = new MenuItem { Header = L.T("Darmowe wtyczki…"), ToolTip = L.T("Darmowe efekty do poprawiania dźwięku – opis i strona do pobrania") };
                    fr.Click += (a, b) => VstFreeWindow(Window.GetWindow(overlay) ?? win);
                    vm.Items.Add(fr);
                    cm.Items.Add(vm);
                }
            });
            popup(aspect, cm =>
            {
                foreach (var a0 in new[] { "", "16:9", "4:3", "21:9", "1:1", "5:4", "2.35:1" })
                {
                    var ar = a0;
                    var mi = new MenuItem { Header = ar == "" ? L.T("Automatycznie") : ar, IsCheckable = true, IsChecked = (mp.AspectRatio ?? "") == ar };
                    mi.Click += (a, b) => { mp.AspectRatio = ar == "" ? null : ar; showOsd(L.T("Proporcje: ") + (ar == "" ? L.T("auto") : ar)); };
                    cm.Items.Add(mi);
                }
            });

            WatchMenus();
            // mysz na obrazie: klik = pauza, dwuklik = pelny ekran, kolko = glosnosc, ruch = pokaz pasek (pelny ekran)
            overlay.MouseLeftButtonDown += (s, e) =>
            {
                if (controls.IsMouseOver) return;   // klik na pasku to nie klik w obraz
                if (_menusOpen > 0 || (DateTime.UtcNow - _menuClosedAt).TotalMilliseconds < 400) { e.Handled = true; return; }   // klik tylko zamyka otwarte menu - bez pauzy
                if (overPip(e)) { pipSwap?.Invoke(); e.Handled = true; return; }   // klik na podgladzie PiP - zamiana kanalow, bez pauzy glownego
                var curPath = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                if (chPanel.Visibility == Visibility.Visible) { showChannels(false); e.Handled = true; return; }   // klik obok listy - schowaj (bez pauzy)
                if (e.ClickCount == 2) { togglePlay(); toggleFull(); }   // pierwszy klik dwukliku juz zatrzymal - wznawiamy
                else togglePlay();
            };
            // prawy przycisk na obrazie: najczestsze rzeczy pod reka
            overlay.MouseRightButtonUp += (s, e) =>
            {
                if (overPip(e)) { var pc = new ContextMenu(); pipItems(pc); pc.IsOpen = true; e.Handled = true; return; }   // prawy na podgladzie - jego opcje
                if (controls.IsMouseOver || chPanel.IsMouseOver) return;   // na liscie kanalow - jej wlasne menu (ulubione, PiP, sortowanie)
                e.Handled = true;
                var cm = new ContextMenu();
                Action<string, Action, bool> add = (text, act, on) => { var mi = new MenuItem { Header = text, IsEnabled = on }; mi.Click += (a, b) => act(); cm.Items.Add(mi); };
                Func<Button, Action> click = b => () => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                add(mp.IsPlaying ? L.T("⏸ Pauza") : L.T("▶ Odtwarzaj"), togglePlay, true);
                add(L.T("⏹ Stop"), click(stop), true);
                cm.Items.Add(new Separator());
                var path = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                bool remote = path != null && _remoteFilms.ContainsKey(path);
                bool file = path != null && !path.Contains("://");   // plik albo NAS - mozna dodac do biblioteki
                Action<bool> addTo = sh => { var l = LoadLibrary(); int n = AddToLibrary(l, new[] { path }, sh); SaveLibrary(l); _libRefresh?.Invoke(); showOsd(n > 0 ? L.T("Dodano do biblioteki") : L.T("Już jest w tej bibliotece")); };
                string shName = string.IsNullOrWhiteSpace(_settings.VlcSharedName) ? L.T("Wspólna biblioteka") : _settings.VlcSharedName;
                string myName = string.IsNullOrWhiteSpace(_settings.VlcLocalName) ? Environment.MachineName : _settings.VlcLocalName;
                add(L.T("➕ Dodaj do: 📚 ") + shName, () => addTo(true), file);
                add(L.T("➕ Dodaj do: 🔒 ") + myName, () => addTo(false), file);
                if (!file && path != null && !_iptvPlaying.ContainsKey(path)) add(L.T("(film strumieniowany z innego komputera – jest w jego bibliotece)"), () => { }, false);
                add(L.T("ℹ Pokaż informacje (I)"), () => showInfo(true), true);
                if (path != null && _iptvPlaying.ContainsKey(path))
                {
                    add(L.T("📋 Lista kanałów (L / kursor przy lewej krawędzi)"), () => showChannels(true), true);
                    var laM = new MenuItem { Header = L.T("📋 Przezroczystość listy (kanały i filmy)") };
                    foreach (var a0 in new[] { 100, 85, 70, 55, 40 })
                    {
                        var al = a0; var mi = new MenuItem { Header = (100 - al) + " %", IsCheckable = true, IsChecked = IptvSt.ListAlpha == al };
                        mi.Click += (q, w2) => { IptvSt.ListAlpha = al; SaveIptv(IptvSt); showChannels(true); };
                        laM.Items.Add(mi);
                    }
                    cm.Items.Add(laM);
                }
                else if (path != null)
                {
                    add(L.T("📋 Lista filmów (L / kursor przy lewej krawędzi)"), () => showChannels(true), true);
                    var flM = new MenuItem { Header = L.T("📋 Przezroczystość listy (kanały i filmy)") };
                    foreach (var a0 in new[] { 100, 85, 70, 55, 40 })
                    {
                        var al = a0; var mi = new MenuItem { Header = (100 - al) + " %", IsCheckable = true, IsChecked = IptvSt.ListAlpha == al };
                        mi.Click += (q, w2) => { IptvSt.ListAlpha = al; SaveIptv(IptvSt); showChannels(true); };
                        flM.Items.Add(mi);
                    }
                    cm.Items.Add(flM);
                }

                add(L.T("📂 Otwórz plik…"), () => { var fs = PickVideoFiles(true, Window.GetWindow(overlay) ?? win); if (fs != null) PlayVideos(fs, 0); }, true);
                {   // ostatnio ogladane - osobno TV i filmy, po 20 ostatnich, najnowsze na gorze
                    var rf = new MenuItem { Header = L.T("🕘 Ostatnio oglądane Filmy") };
                    foreach (var v0 in LoadLibrary().Where(v => v.Peer == null && v.Last > 0 && v.Path != path && FilmPlayable(v.Path)).OrderByDescending(v => v.Last).Take(20))
                    { var v = v0; var mi = new MenuItem { Header = ShownTitle(v, MetaFor(v.Path)) }; mi.Click += (q, w2) => PlayVideos(new[] { v.Path }, 0); rf.Items.Add(mi); }
                    var rt = new MenuItem { Header = L.T("🕘 Ostatnio oglądane TV") };
                    var kpl = IptvInstalled && IptvSt.Recent.Any(u => u != path && !_iptvPlaying.ContainsKey(u)) ? KnownPlaylist() : _iptvPl;   // TV jeszcze nie otwierana - kanaly z pamieci podrecznej
                    foreach (var c0 in IptvSt.Recent.Where(u => u != path).Select(u => _iptvPlaying.TryGetValue(u, out var c1) ? c1 : kpl?.Channels.FirstOrDefault(x => x.Url == u)).Where(c => c != null).Take(20))
                    { var c = c0; var mi = new MenuItem { Header = c.Name }; mi.Click += (q, w2) => PlayChannelInGroup(c, kpl); rt.Items.Add(mi); }
                    if (rt.Items.Count > 0) cm.Items.Add(rt);
                    if (rf.Items.Count > 0) cm.Items.Add(rf);
                }
                add(L.T("🎬 Biblioteka filmów"), click(lib), true);
                add(isFav() ? L.T("⭐ Usuń z ulubionych") : L.T("⭐ Dodaj do ulubionych"), () => fav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)), true);
                var scM = new MenuItem { Header = L.T("🌙 Ekran i wyłącznik czasowy") };
                screenItems(scM); scM.Items.Add(new Separator());
                var slM = new MenuItem { Header = L.T("⏾ Zatrzymaj odtwarzanie…") + (sleepEnd ? L.T(" (po końcu filmu)") : sleepAt.HasValue ? " (" + Math.Max(1, (int)Math.Ceiling((sleepAt.Value - DateTime.Now).TotalMinutes)) + " min)" : "") };
                sleepItems(slM); scM.Items.Add(slM); cm.Items.Add(scM);
                { var pipM = new MenuItem { Header = L.T("⧉ Obraz w obrazie (PiP)") }; pipItems(pipM); cm.Items.Add(pipM); }
                add(L.T("⏰ Zaplanuj włączenie filmu / kanału…"), () => ShowScheduleStart(Window.GetWindow(overlay) ?? win), true);
                {   // playlisty i moja kolejka - wlaczenie jednym klikiem
                    var pls = LoadPlaylists(); var plM = new MenuItem { Header = L.T("🎞 Playlisty i moja kolejka") };
                    Action<string, List<string>> one = (t, paths) => { var mi = new MenuItem { Header = t }; mi.Click += (q, w2) => PlayVideos(paths.ToArray(), 0); plM.Items.Add(mi); };
                    if (pls.Mine.Count > 0) one(L.T("📋 Moja kolejka (") + pls.Mine.Count + ")", pls.Mine);
                    foreach (var n in pls.Named.Keys.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)) one("🎞 " + n + " (" + pls.Named[n].Count + ")", pls.Named[n]);
                    if (plM.Items.Count > 0) cm.Items.Add(plM);
                }
                if (file) add(L.T("💬 Pobierz napisy…"), () => ShowSubtitleSearch(path, Window.GetWindow(overlay) ?? win, f =>
                {
                    if (_playlist.Count <= _playIndex || _playlist[_playIndex] != path) return;   // w miedzyczasie inny film
                    mp.AddSlave(MediaSlaveType.Subtitle, new Uri(f).AbsoluteUri, true); loadSubs(f);
                    showOsd(L.T("💬 Napisy wczytane"));
                }), true);
                if (IptvInstalled) add(L.T("📺 Kanały telewizji"), () => ShowIptv(Window.GetWindow(overlay) ?? win), true);
                if (IptvInstalled && IptvSt.Jobs.Count > 0) add("📅 " + JobsSummary() + "…", () => ShowJobs(Window.GetWindow(overlay) ?? win), true);
                if (IptvInstalled) add(L.T("📅 Pełny program TV"), () => ShowEpgGuide(Window.GetWindow(overlay) ?? win, path != null && _iptvPlaying.ContainsKey(path) ? path : null), true);
                if (path != null && _iptvPlaying.ContainsKey(path))
                {
                    var tvNow = _iptvPlaying[path]; var epgL = EpgFor(_iptvEpg, tvNow); var nowP = EpgAt(epgL, DateTime.Now);
                    var nextP = epgL?.FirstOrDefault(x => x.Start >= (nowP?.Stop ?? DateTime.Now));
                    if (tvNow.ArchiveStart.HasValue) add(L.T("⏩ Na żywo (wróć z archiwum)"), () => PlayChannelInGroup(LiveOf(tvNow), KnownPlaylist()), true);
                    else if (nowP != null && HasCatchup(tvNow)) add(L.T("⏮ Od początku programu (archiwum)"), () => PlayArchive(tvNow, nowP), true);
                    if (nowP != null) add(L.T("▶ Teraz: ") + nowP.Title + L.T(" (do ") + nowP.Stop.ToString("HH:mm") + ")", () => ShowEpgGuide(Window.GetWindow(overlay) ?? win, path), true);
                    if (nextP != null) add(L.T("⏭ Następnie: ") + nextP.Start.ToString("HH:mm") + " " + nextP.Title, () => ShowEpgGuide(Window.GetWindow(overlay) ?? win, path), true);
                    bool isFav = IptvSt.Fav.Contains(path);
                    add(L.T("⏮ Poprzedni kanał (PgUp)"), () => _vlcLoad(_playIndex - 1), _playIndex > 0);
                    add(L.T("⏭ Następny kanał (PgDn)"), () => _vlcLoad(_playIndex + 1), _playIndex < _playlist.Count - 1);
                    add(L.T("🔄 Odśwież kanał (F5)"), () => { showOsd(L.T("🔄 Odświeżam kanał…")); _vlcLoad(_playIndex); }, true);
                    if (curRecFile != null) add(L.T("⏩ Na żywo (koniec nagrania)"), () => { recResume = -1; _vlcLoad(_playIndex); }, true);
                    else if (IptvSt.WatchFromRec && RecFileFor(path) != null) add(L.T("⏺ Oglądaj z nagrania (jedno połączenie, pauza i cofanie)"), () => { recResume = -1; _vlcLoad(_playIndex); }, true);
                    cm.Items.Add(RecMarginMenu());
                    var tvCh = _iptvPlaying[path];
                    if (IsRecording(path)) add(L.T("⏹ Zatrzymaj nagrywanie"), () => StopRecording(path), true);
                    else add(L.T("⏺ Nagrywaj ten kanał"), () => RecordNow(tvCh, win), true);
                }
                if (path != null && !remote && file)
                {
                    foreach (var together0 in new[] { false, true })
                    {
                        bool together = together0;
                        var root = new MenuItem { Header = together ? L.T("👥 Oglądaj razem z…") : L.T("📺 Odtwórz na…") };
                        var peers = SendTargets();
                        if (peers.Count == 0) root.Items.Add(new MenuItem { Header = NoPeersWhy(), IsEnabled = false });
                        foreach (var p0 in peers)
                        {
                            var p = p0; var mi = new MenuItem { Header = "🖥 " + p.Device };
                            mi.Click += (a, b) =>
                            {
                                savePos();
                                var v = current != null ? current : new LibraryVideo { Path = path, Title = System.IO.Path.GetFileNameWithoutExtension(path) };
                                v.Pos = mp.Time; v.Done = false;
                                if (together && !PremiumOk(L.T("👥 Oglądaj razem"))) return;
                                if (together) mp.Stop();   // oba komputery startuja razem z tego miejsca
                                PlayFilmOn(v, p, together);
                            };
                            root.Items.Add(mi);
                        }
                        cm.Items.Add(root);
                    }
                }
                if (_together != null) add(L.T("👋 Przestań oglądać razem (druga osoba ogląda dalej)"), LeaveTogether, true);
                else if (_lastTogether != null && PeerById(_lastTogether.PeerId) != null)
                    add(L.T("👥 Dołącz ponownie do: ") + PeerById(_lastTogether.PeerId).Device, () =>
                    {
                        if (mp.State != VLCState.Playing && mp.State != VLCState.Paused) _vlcLoad(_playIndex);
                        var t = _lastTogether;
                        StartAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1500, () => { _together = t; TogetherSend("join", 0); showOsd(L.T("👥 Dołączono ponownie")); });   // po wczytaniu filmu
                    }, true);
                cm.Items.Add(new Separator());
                add(L.T("💬 Napisy na ekranie: ") + (_settings.VlcShowSubs ? L.T("ukryj") : L.T("pokaż")), () => { showSubs.IsChecked = !_settings.VlcShowSubs; showSubs.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }, true);
                add(_settings.VlcLector ? L.T("🗣 Wyłącz lektora") : L.T("🗣 Włącz lektora"), () => { lectorOn.IsChecked = !_settings.VlcLector; lectorOn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }, true);
                add(L.T("📌 Zawsze na wierzchu"), click(top), true);
                add(L.T("🔲 Tryb mini"), toggleMini, true);
                add(L.T("🖥 Pełny ekran"), toggleFull, true);
                add(L.T("📷 Zrzut klatki"), click(snap), true);
                // porzadek: odtwarzanie, telewizja / film, listy, udostepnianie, widok, a ustawienia w jednym podmenu
                string H(object o) => (o as MenuItem)?.Header as string ?? "";
                Func<string, string[], bool> starts = (h, ps) => ps.Any(p0 => h.StartsWith(L.T(p0)));
                var groups = new List<(string Title, string[] Prefixes)>
                {
                    (null, new[] { "⏸ Pauza", "▶ Odtwarzaj", "⏹ Stop" }),
                    (null, new[] { "⏩ Na żywo (wróć", "⏮ Od początku programu", "▶ Teraz: ", "⏭ Następnie: ", "⏮ Poprzedni kanał", "⏭ Następny kanał", "🔄 Odśwież kanał", "⏩ Na żywo", "⏺ Oglądaj z nagrania", "⏺ Nagrywaj", "⏹ Zatrzymaj nagrywanie" }),
                    (null, new[] { "📋 Lista ", "ℹ Pokaż informacje", "📂 Otwórz plik", "🕘 Ostatnio oglądane", "🎬 Biblioteka filmów", "📺 Kanały telewizji", "📅", "🎞 Playlisty", "⭐ ", "⏾ ", "⧉ ", "⏰ Zaplanuj" }),
                    (null, new[] { "💬 Pobierz napisy", "💬 Napisy na ekranie", "🗣 " }),
                    (null, new[] { "📺 Odtwórz na", "👥 ", "👋 " }),
                    (null, new[] { "📌 Zawsze na wierzchu", "🔲 Tryb mini", "🖥 Pełny ekran", "📷 Zrzut klatki" }),
                    (L.T("⚙ Ustawienia"), new[] { "📋 Przezroczystość listy", "⏱ Zapas nagrywania", "➕ Dodaj do: ", "(film strumieniowany" }),
                };
                // co jest ikona na pasku - nie powtarzamy w menu (krotsze menu)
                var onBar = new[] { "⏸ Pauza", "▶ Odtwarzaj", "⏹ Stop", "⏮ Poprzedni kanał", "⏭ Następny kanał", "ℹ Pokaż informacje", "🔄 Odśwież kanał", "⏺ Nagrywaj ten kanał", "⏹ Zatrzymaj nagrywanie",
                    "🎬 Biblioteka filmów", "📺 Kanały telewizji", "📅 Pełny program TV", "⭐ ", "⏾ Wyłącznik czasowy", "⧉ Obraz w obrazie", "🎞 Playlisty i moja kolejka", "🔲 Tryb mini", "🖥 Pełny ekran", "📷 Zrzut klatki" };
                var all = cm.Items.Cast<object>().Where(o => o is MenuItem).Cast<MenuItem>().Where(m => !starts(H(m), onBar)).ToList();
                cm.Items.Clear();
                var used = new HashSet<MenuItem>();
                foreach (var g in groups)
                {
                    var its = all.Where(m => !used.Contains(m) && starts(H(m), g.Prefixes)).ToList();
                    if (its.Count == 0) continue;
                    foreach (var m in its) used.Add(m);
                    if (cm.Items.Count > 0) cm.Items.Add(new Separator());
                    if (g.Title == null) foreach (var m in its) cm.Items.Add(m);
                    else { var sub = new MenuItem { Header = g.Title }; foreach (var m in its) sub.Items.Add(m); cm.Items.Add(sub); }
                }
                var rest = all.Where(m => !used.Contains(m)).ToList();   // nic nie ginie
                if (rest.Count > 0) { cm.Items.Add(new Separator()); foreach (var m in rest) cm.Items.Add(m); }
                cm.IsOpen = true;
            };
            overlay.MouseWheel += (s, e) => { if (controls.IsMouseOver && !vol.IsMouseOver) return; showBar(); setVol(vol.Value + (e.Delta > 0 ? 5 : -5)); showOsd(L.T("Głośność ") + (int)vol.Value + "%"); };
            overlay.MouseMove += (s, e) =>
            {
                showBar();
                // kursor przy lewej krawedzi obrazu (kanal TV) - lista kanalow, bez klikania
                var cp = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                if (cp != null && e.GetPosition(overlay).X < 14 && chPanel.Visibility != Visibility.Visible) showChannels(true);   // kanaly albo filmy
            };
            _vlcQuiet = q => { overlay.IsHitTestVisible = !q; if (q) { hideTimer.Stop(); controls.Visibility = Visibility.Collapsed; miniBar.Visibility = Visibility.Collapsed; if (chPanel.Visibility == Visibility.Visible) showChannels(false); } else showBar(); };
            mp.Paused += (s, e) => Dispatcher.BeginInvoke(guard(() => { if (overlay.IsHitTestVisible) showBar(); }));
            mp.Paused += (s, e) => Dispatcher.BeginInvoke(guard(() => pausedAt = DateTime.Now));   // pauza - pasek zostaje widoczny
            hideTimer.Start();
            win.PreviewKeyDown += (s, e) =>
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
                bool h = true;
                switch (e.Key)
                {
                    case Key.Space: togglePlay(); break;
                    case Key.Left: jump(-10000); break;
                    case Key.Right: jump(10000); break;
                    case Key.Up: setVol(vol.Value + 5); showOsd(L.T("Głośność ") + (int)vol.Value + "%"); break;
                    case Key.Down: setVol(vol.Value - 5); showOsd(L.T("Głośność ") + (int)vol.Value + "%"); break;
                    case Key.F: case Key.F11: toggleFull(); break;
                    case Key.I: showInfo(true); break;
                    case Key.L: showChannels(chPanel.Visibility != Visibility.Visible); break;
                    case Key.F5: if (_playlist.Count > _playIndex && _iptvPlaying.ContainsKey(_playlist[_playIndex])) { showOsd(L.T("🔄 Odświeżam kanał…")); _vlcLoad(_playIndex); } break;
                    case Key.Escape: if (chPanel.Visibility == Visibility.Visible) showChannels(false); else if (full0) toggleFull(); else if (mini0) toggleMini(); else h = false; break;
                    case Key.M: toggleMute(); break;
                    case Key.Home: ShowKanapa(); break;   // 🛋 Kanapa
                    case Key.J: setADelay(aDelayMs - 50); break;   // dźwięk wcześniej (jak w VLC)
                    case Key.K: setADelay(aDelayMs + 50); break;   // dźwięk później
                    case Key.R: resync(); break;   // wyrównaj obraz z dźwiękiem
                    case Key.G: setSubDelay(mp.SpuDelay / 1000000.0 - 0.05); showOsd(sdText.Text); break;   // napisy wczesniej (jak w VLC)
                    case Key.H: setSubDelay(mp.SpuDelay / 1000000.0 + 0.05); showOsd(sdText.Text); break;   // napisy pozniej
                    case Key.N: case Key.PageDown: step(1); break;
                    case Key.PageUp: step(-1); break;
                    case Key.P: step(-1); break;
                    case Key.OemOpenBrackets: setSpeed(-1); break;
                    case Key.OemCloseBrackets: setSpeed(1); break;
                    case Key.S: userStopped = true; vlcRun(() => mp.Stop()); afterStop(); break;
                    default: h = false; break;
                }
                if (h) e.Handled = true;
            };
            win.Closing += (s, e) => savePos();
            // 🎮 kontroler (XInput) w odtwarzaczu: przypisania z PadMap (okno Kontroler w ⚙); w otwartym menu / liście kanałów krzyżak = strzałki, A = zatwierdź, B = zamknij
            var padTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            int padPrev = 0, padHeld = 0, padErr = 0; DateTime padNext = DateTime.MinValue; var padMap = PadMap(); string padMapSrc = GetPref("pad-mapa");
            Action<Key> padKey = k =>
            {
                var tgt = Keyboard.FocusedElement as UIElement ?? win;
                var src = (tgt is Visual tv ? PresentationSource.FromVisual(tv) : null) ?? PresentationSource.FromVisual(win); if (src == null) return;
                var ka = new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent }; tgt.RaiseEvent(ka);
                if (!ka.Handled) tgt.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.KeyDownEvent });
            };
            Action<Button> padClick = b => { if (b != null && b.IsVisible && b.IsEnabled) b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            Func<bool> padMenu = () => Keyboard.FocusedElement is MenuItem || chPanel.Visibility == Visibility.Visible && Keyboard.FocusedElement is DependencyObject fd && (fd == chPanel || chPanel.IsAncestorOf(fd));
            Action<string> padDo = act =>
            {
                showBar();
                switch (act)
                {
                    case "play": togglePlay(); break;
                    case "back": padKey(Key.Escape); break;
                    case "seek-10": jump(-10000); break;
                    case "seek+10": jump(10000); break;
                    case "seek-60": jump(-60000); break;
                    case "seek+60": jump(60000); break;
                    case "vol+": setVol(vol.Value + 5); showOsd(L.T("Głośność ") + (int)vol.Value + "%"); break;
                    case "vol-": setVol(vol.Value - 5); showOsd(L.T("Głośność ") + (int)vol.Value + "%"); break;
                    case "mute": toggleMute(); break;
                    case "prev": step(-1); break;
                    case "next": step(1); break;
                    case "channels": showChannels(chPanel.Visibility != Visibility.Visible); break;
                    case "info": showInfo(true); break;
                    case "subs": padClick(subs); break;
                    case "audio": padClick(audio); break;
                    case "settings": padClick(tools); break;
                    case "full": toggleFull(); break;
                    case "kanapa": ShowKanapa(); break;
                }
            };
            padTimer.Tick += (s, e) =>
            {
                if (!win.IsActive || padErr > 3) return;
                XInputState st; int r;
                try { r = KnPadGet(0, out st); } catch (Exception) { padErr = 99; return; }   // brak XInput - bez kontrolera
                if (r != 0) { padPrev = 0; padHeld = 0; return; }
                if (GetPref("pad-mapa") != padMapSrc) { padMapSrc = GetPref("pad-mapa"); padMap = PadMap(); }   // zmienione w oknie przypisań
                const int dead = 16000;
                int b = st.Buttons | (st.LT > 128 ? 0x10000 : 0) | (st.RT > 128 ? 0x20000 : 0)
                    | (st.LY > dead ? 0x0001 : 0) | (st.LY < -dead ? 0x0002 : 0) | (st.LX < -dead ? 0x0004 : 0) | (st.LX > dead ? 0x0008 : 0);   // lewa gałka = krzyżak
                int down = b & ~padPrev; padPrev = b;
                const int rep = 0x0001 | 0x0002 | 0x0004 | 0x0008 | 0x10000 | 0x20000;   // przytrzymane: powtarzanie (przewijanie, głośność)
                int held = b & rep;
                if (held == 0 || (down & rep) != 0) { padHeld = held; padNext = DateTime.UtcNow.AddMilliseconds(400); }
                else if (DateTime.UtcNow >= padNext) { padNext = DateTime.UtcNow.AddMilliseconds(150); down |= held; }
                if (down == 0) return;
                bool menu = padMenu();
                foreach (var pb in PadButtons)
                {
                    if ((down & pb.Mask) == 0) continue;
                    if (menu)
                    {   // nawigacja w menu / liście: krzyżak, A, B stałe (jak na konsoli)
                        Key k = pb.Id == "Up" ? Key.Up : pb.Id == "Down" ? Key.Down : pb.Id == "Left" ? Key.Left : pb.Id == "Right" ? Key.Right : pb.Id == "A" ? Key.Enter : pb.Id == "B" ? Key.Escape : Key.None;
                        if (k == Key.Enter && Keyboard.FocusedElement is Button fb) { fb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); continue; }
                        if (k != Key.None) { padKey(k); continue; }
                    }
                    if (padMap.TryGetValue(pb.Id, out var act) && act != "none") padDo(act);
                }
            };
            padTimer.Start();
            win.Closed += (s, e) => padTimer.Stop();
            // 🎮 Velivo Pilot (telefon): te same czynności co przyciski i klawisze odtwarzacza (Pilot.cs)
            _vlcPilotCmd = (op, v, n) =>
            {
                switch (op)
                {
                    case "play": togglePlay(); break;
                    case "stop": userStopped = true; savePos(); vlcRun(() => mp.Stop()); LeaveTogether(); afterStop(); break;
                    case "next": step(1); break;
                    case "prev": step(-1); break;
                    case "jump": jump((long)n); break;
                    case "seek": if (mp.Length > 0) { mp.Time = Math.Max(0, Math.Min(mp.Length - 500, (long)n)); TogetherSend("seek", mp.Time); } break;
                    case "vol": setVol(n); showOsd(L.T("Głośność ") + (int)vol.Value + "%"); break;
                    case "mute": toggleMute(); break;
                    case "full": toggleFull(); break;
                    case "aspect": mp.AspectRatio = string.IsNullOrEmpty(v) ? null : v; showOsd(L.T("Proporcje: ") + (string.IsNullOrEmpty(v) ? L.T("auto") : v)); break;
                    case "zoom": mp.Scale = (float)Math.Max(0, Math.Min(4, n)); break;
                    case "sub": { int id = (int)n; mp.SetSpu(id); var t = mp.SpuDescription.FirstOrDefault(x => x.Id == id); rememberTrack(true, id < 0 ? "-" : t.Name ?? ""); } break;
                    case "audio": { int id = (int)n; mp.SetAudioTrack(id); var t = mp.AudioTrackDescription.FirstOrDefault(x => x.Id == id); if (id >= 0) rememberTrack(false, t.Name ?? ""); } break;
                    case "kanapa": ShowKanapa(); break;
                    case "screenoff": scrSet(n); break;
                    case "screenon": scrAt = null; scrTimer.Stop(); ScreenPower(true); showOsd(L.T("🌙 Ekran włączony")); break;
                    case "sleep": if (n > 0) { sleepEnd = false; sleepAt = DateTime.Now.AddMinutes(n); showOsd(L.T("⏾ Zatrzymanie za ") + (int)n + " min"); } else if (n < 0) { sleepAt = null; sleepEnd = true; showOsd(L.T("⏾ Zatrzymanie po końcu filmu")); } else { sleepAt = null; sleepEnd = false; mp.Volume = muted ? 0 : (int)vol.Value; showOsd(L.T("⏾ Wyłącznik czasowy wyłączony")); } updSleep(); break;
                }
            };
            _vlcPilotState = () => new Dictionary<string, object>
            {
                ["st"] = mp.IsPlaying ? "play" : mp.State == LibVLCSharp.Shared.VLCState.Paused ? "pause" : "stop",
                ["title"] = win.Title ?? "", ["pos"] = mp.Time, ["len"] = mp.Length, ["vol"] = (int)vol.Value, ["mute"] = muted, ["full"] = full0,
                ["sub"] = mp.Spu, ["subs"] = mp.SpuDescription.Select(t => new object[] { t.Id, t.Id < 0 ? "" : t.Name }).ToList(),
                ["aud"] = mp.AudioTrack, ["auds"] = mp.AudioTrackDescription.Where(t => t.Id >= 0).Select(t => new object[] { t.Id, t.Name }).ToList(),
                ["asp"] = mp.AspectRatio ?? "", ["zoom"] = mp.Scale,
                ["now"] = PilotNowOnTv(),   // 📺 co teraz leci na włączonym kanale
                ["sleep"] = sleepEnd ? -1 : sleepAt.HasValue ? Math.Max(1, (int)Math.Ceiling((sleepAt.Value - DateTime.Now).TotalMinutes)) : 0,
                ["scr"] = scrAt.HasValue ? Math.Max(1, (int)Math.Ceiling((scrAt.Value - DateTime.Now).TotalMinutes)) : 0,
                ["kind"] = PilotNowName().Kind, ["name"] = PilotNowName().Name,   // tv / film i czysty tytuł
            };
            AbLektorUiRefresh = () => { lekLoading = true; lvRow.Item2.Value = _settings.VlcLectorVolume * 100; ldRow.Item2.Value = _settings.VlcLectorDuck; lekLoading = false; if (lc.Voice != null) lc.Voice.Volume = WpfVol(_settings.VlcLectorVolume); filmPrefsChanged(); };
            win.Closed += (s, e) => { _vlcPilotCmd = null; _vlcPilotState = null; AbLektorUiRefresh = null; };
            win.Closed += (s, e) =>
            {
                saveTimer.Stop(); hideTimer.Stop(); osdTimer.Stop(); lectorTimer.Stop(); LectorStopVoice(lc, mp); LectorStopRender(lc);
                _vlcWin = null; _vlcLoad = null; _vlcQuiet = null; _vlcRemoteCtl = null; _vlcAsk = null; _playShared = null;
                LeaveTogether();   // zamkniecie odtwarzacza: wychodzisz ze wspolnego ogladania, druga osoba oglada dalej
                if (!_mainClosing) SaveTvResume(-1);
                if (_hiddenForPlayer && !_mainClosing) { _hiddenForPlayer = false; ShowFromTray(); }
                LibBack();   // 🎬 biblioteka, z której włączono film, wraca   // przegladarka wraca z zasobnika   // odtwarzacz zamkniety recznie - bez wznawiania; przy zamykaniu Velivo zostaje
                view.MediaPlayer = null;
                if (_playerMp == mp) _playerMp = null;   // zwolniony za 10 s - zegar synchronizacji nie może już pytać „czy gra” (awaria Velivo)
                vlcClosed = true; AbDetach();
                // Stop VLC moze chwile trwac - nie w watku okna; zwolnienie pozniej, gdy kolejka okna i jednorazowe zegary (do 1,5 s) juz minely
                vlcRun(() => mp.Stop()).ContinueWith(async _ => { try { await System.Threading.Tasks.Task.Delay(10000); mp.Dispose(); } catch (Exception) { } });   // po kolejnych zatrzymaniach (nie w trakcie)
            };
            _vlcWin = win;
            win.Show();
        }
    }
}
