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

namespace Przegladarka
{
    // Odtwarzacz filmow jak VLC (dodatek wybierany w instalatorze): AVI, MKV, WMV, FLV, MPG, TS i inne kodeki przez biblioteke VLC
    // (libvlc w folderze programu vlc\ - pobierana przez instalator z oficjalnej paczki VideoLAN). Bez dodatku Velivo dziala jak dotad
    // (filmy MP4/WebM w karcie). Przycisk 🎬 obok 🏦: klik = biblioteka filmow, prawy przycisk = otworz plik, dodaj do biblioteki,
    // odinstaluj dodatek. Sterowanie jak w VLC: pasek czasu, pauza, stop, poprzedni/nastepny, ±10 s, predkosc, napisy, sciezka
    // dzwieku, proporcje obrazu, zrzut klatki, glosnosc do 125%, pelny ekran. Biblioteka i miejsca wznowienia - lokalnie (filmy.json).
    public partial class MainWindow
    {
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
        Button _filmBtn;

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
            // ustawienia tego filmu (filmy maja rozna glosnosc): glosnosc filmu i lektor; null = jak ostatnio
            public int? Vol { get; set; }
            public double? LVol { get; set; }
            public double? LRate { get; set; }
            public int? LDuck { get; set; }
            public string LVoice { get; set; }
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

        List<LibraryVideo> LoadLibrary()
        {
            try { return File.Exists(LibraryFile) ? ParseLibrary(File.ReadAllText(LibraryFile)) : new List<LibraryVideo>(); }
            catch (IOException) { return new List<LibraryVideo>(); }
        }

        void SaveLibrary(List<LibraryVideo> lib)
        {
            try { Directory.CreateDirectory(DataDir); File.WriteAllText(LibraryFile, JsonSerializer.Serialize(lib, new JsonSerializerOptions { WriteIndented = true })); }
            catch (IOException ex) { App.LogError(ex); }
        }

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
        internal static int PickTrack(IEnumerable<(int Id, string Name)> tracks, string wanted)
        {
            if (string.IsNullOrEmpty(wanted)) return int.MinValue;
            if (wanted == "-") return -1;
            var list = tracks.Where(t => t.Id >= 0).ToList();
            var exact = list.FirstOrDefault(t => t.Name == wanted);
            if (exact.Name != null) return exact.Id;
            Func<string, string> langOf = n => { var m = System.Text.RegularExpressions.Regex.Match(n ?? "", @"\[([^\]]+)\]"); return (m.Success ? m.Groups[1].Value : n ?? "").Trim().ToLowerInvariant(); };
            var lang = langOf(wanted);
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
            Action<string, string, Action> item = (glyph, text, act) =>
            {
                var mi = new MenuItem { Header = text, Icon = MenuGlyph(glyph) };
                mi.Click += (s, e) => act();
                menu.Items.Add(mi);
            };
            item("\uE8B3", L.T("Biblioteka filmów"), ShowVideoLibrary);
            if (IptvInstalled) item("\uE7F4", L.T("📺 Nagrane z TV"), () => { _libTvView = true; _libSharedView = false; if (_libWin != null && _libWin.IsLoaded) _libWin.Close(); ShowVideoLibrary(); });
            item("\uE8E5", L.T("Otwórz film z dysku…"), () => { var f = PickVideoFiles(false); if (f != null) PlayVideos(f, 0); });
            menu.Items.Add(new Separator());
            item("\uE710", L.T("Dodaj filmy do biblioteki…"), () => { var f = PickVideoFiles(true); if (f != null) AddPathsToLibrary(f); });
            item("\uE8F4", L.T("Dodaj folder z filmami do biblioteki…"), () => { var d = PickFolder(); if (d != null) AddPathsToLibrary(new[] { d }); });
            menu.Items.Add(new Separator());
            item("\uE74D", L.T("Odinstaluj odtwarzacz filmów…"), UninstallVlc);
            _filmBtn.ContextMenu = menu;
            AutoSyncJellyfin();   // informacje o filmach z Jellyfin - uzupelnienie lokalnej bazy w tle (gdy ustawione)
            int bank = TabBarPanel.Children.IndexOf(_bankBtn);
            TabBarPanel.Children.Insert(bank >= 0 ? bank + 1 : 1, _filmBtn);   // dock z prawej: nastepny w kolejnosci stoi po lewej od 🏦
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

        void UninstallVlc()
        {
            if (MessageBox.Show(this, L.T("Odinstalować odtwarzacz filmów (bibliotekę VLC)?\n\nFilmy MP4 i WebM dalej otworzysz w karcie Velivo. Twoja lista filmów zostaje – wróci po ponownej instalacji dodatku."),
                L.T("Odtwarzacz filmów"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
        bool _libSharedView;   // otwarta biblioteka: wspolna albo tego komputera
        bool _libTvView;       // otwarta biblioteka "Nagrane z TV" (pliki z folderow nagran)
        bool? _playShared;     // z ktorej biblioteki wlaczono film (jej licznik obejrzenia); null = obojetnie

        sealed class LibRow
        {
            public string Title { get; set; } public string Length { get; set; } public string Seen { get; set; } public string LastPlayed { get; set; } public string Folder { get; set; }
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
                case "title": by = v => (mi(v)?.Name ?? v.Title).ToLowerInvariant(); break;
                case "year": by = v => mi(v)?.Year ?? 0; break;
                case "rating": by = v => mi(v)?.Rating ?? 0; break;
                case "genre": by = v => (mi(v)?.Genres.FirstOrDefault() ?? "").ToLowerInvariant(); break;
                case "len": by = v => v.Len; break;
                case "seen": by = v => SeenFraction(v); break;
                case "folder": by = v => (System.IO.Path.GetDirectoryName(v.Path) ?? "").ToLowerInvariant(); break;
                default: by = v => v.Last; break;
            }
            var sorted = desc ? list.OrderByDescending(by) : list.OrderBy(by);
            return sorted.ThenBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase);
        }

        // owner: okno, nad ktorym ma byc biblioteka (z odtwarzacza - nad filmem, takze gdy odtwarzacz jest zawsze na wierzchu)
        void ShowVideoLibrary() { ShowVideoLibrary(this); }

        void ShowVideoLibrary(Window owner)
        {
            if (_libWin != null && _libWin.IsLoaded) { if (_libWin.Owner != owner) _libWin.Owner = owner; _libWin.Topmost = owner.Topmost; _libWin.Activate(); return; }
            var win = new Window { Title = L.T("🎬 Biblioteka filmów"), Width = 860, Height = 540, Owner = owner, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
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
                new[] { "last", "Ostatnio", "LastPlayed", "100" }, new[] { "folder", "Folder", "Folder", "260" } };
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
            RememberColumns(win, "biblioteka-v2", grid);   // v2: kolumny Rok / Ocena / Gatunek
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
                    var mine = local.Where(v => tv ? UnderAny(v.Path, recDirs) : !UnderAny(v.Path, recDirs) && IsShared(v) == shared).ToList();   // nagrania TV - tylko w swojej bibliotece
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
                    var r = SortLibrary(all.Where(v => q.Length == 0 || v.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || v.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        mf(v) is MovieInfo m1 && ((m1.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (m1.Original ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)), key, desc, mf)
                    .Select(v => new LibRow
                    {
                        Item = v, Missing = !of.ContainsKey(v) && (v.Peer != null ? !online.Contains(v.Peer) : !exists(v.Path)), Title = MetaTitle(mf(v)) ?? CleanTitle(v.Title),
                        Year = mf(v)?.Year > 0 ? mf(v).Year.ToString() : "", Rating = mf(v)?.Rating > 0 ? "★ " + mf(v).Rating.ToString("0.0") : "", Genre = string.Join(", ", mf(v)?.Genres ?? new List<string>()),
                        Length = v.Len > 0 ? FormatTime(v.Len) : "",
                        Seen = SeenText(v),
                        LastPlayed = LastPlayedText(v.Last, now),
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
                fill();
            };
            _libRefresh = fill;
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
                Func<LibraryVideo, bool> mine = v => tv ? UnderAny(v.Path, recDirs) : !UnderAny(v.Path, recDirs) && IsShared(v) == shared;
                int here = LoadLibrary().Count(mine), fromPeers = shared && !tv ? remoteOf.Count : 0;
                var name = tv ? L.T("Nagrane z TV") : shared ? sharedName() : localName();
                // filmy z innych komputerow sa zapisane tam - tu mozna usunac tylko wpisy z tego komputera
                var peersNote = fromPeers > 0 ? string.Format(L.T("\n\n{0} pozycji przychodzi z innych komputerów – są zapisane tam i usuwa się je na tamtym komputerze."), fromPeers) : "";
                if (here == 0) { MessageBox.Show(win, string.Format(L.T("W bibliotece „{0}” na tym komputerze nie ma żadnych pozycji do usunięcia."), name) + peersNote, L.T("Biblioteka filmów"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
                if (MessageBox.Show(win, string.Format(L.T("Usunąć z biblioteki „{0}” {1} pozycji zapisanych na tym komputerze?\n\nPliki filmów zostają na dysku."), name, here) + peersNote,
                    L.T("Biblioteka filmów"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
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
                if (remoteOf.TryGetValue(r.Item, out var rem)) { WantRemoteFilm(rem.Key, rem.Value, false); return; }   // film z drugiego komputera - ogladasz tutaj
                if (r.Item.Peer != null)   // skopiowany z innego komputera - przez siec
                {
                    var pr = PeerById(r.Item.Peer);
                    if (pr == null) { MessageBox.Show(win, L.T("Komputer, na którym jest ten film, jest teraz wyłączony albo niedostępny."), L.T("Biblioteka filmów")); return; }
                    WantRemoteFilm(pr, new LanFilmItem { i = r.Item.RemoteId, t = r.Item.Title, l = r.Item.Len }, false); return;
                }
                if (r.Missing) { MessageBox.Show(win, L.T("Nie ma już tego pliku na dysku (przeniesiony albo usunięty)."), L.T("Biblioteka filmów")); return; }
                var playable = rows.Where(x => !x.Missing && !remoteOf.ContainsKey(x.Item) && x.Item.Peer == null).Select(x => x.Item.Path).ToList();   // lista odtwarzania = widoczna lista
                _playShared = _libSharedView;
                PlayVideos(playable.ToArray(), playable.IndexOf(r.Item.Path));
            };
            Action remove = () =>
            {
                var rs = sel().Where(r => !remoteOf.ContainsKey(r.Item)).ToList(); if (rs.Count == 0) return;
                var l = LoadLibrary(); bool sh = _libSharedView, tv = _libTvView; var recDirs = TvRecDirs(); l.RemoveAll(v => (tv ? UnderAny(v.Path, recDirs) : IsShared(v) == sh) && rs.Any(r => string.Equals(r.Item.Path, v.Path, StringComparison.OrdinalIgnoreCase)));
                SaveLibrary(l); fill();
            };
            // usun z biblioteki i plik z dysku (do Kosza; dysk sieciowy nie ma Kosza - wtedy na stale)
            Action removeFiles = () =>
            {
                var rs = sel().Where(r => !remoteOf.ContainsKey(r.Item) && !r.Missing).ToList(); if (rs.Count == 0) return;
                var msg = rs.Count == 1 ? string.Format(L.T("Usunąć film „{0}” z biblioteki i z dysku?"), rs[0].Item.Title) : string.Format(L.T("Usunąć {0} filmów z biblioteki i z dysku?"), rs.Count);
                if (MessageBox.Show(win, msg + L.T("\n\nPlik trafi do Kosza (na dysku sieciowym – usunięty na stałe)."), L.T("Biblioteka filmów"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                var errs = DeleteVideoFiles(rs.Select(r => r.Item.Path));
                fill();
                if (errs.Count > 0) MessageBox.Show(win, L.T("Nie udało się usunąć (plik w użyciu – np. właśnie odtwarzany):\n") + string.Join("\n", errs.Take(10)), L.T("Biblioteka filmów"));
            };
            list.MouseDoubleClick += (s, a) => play();
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
                if (peers.Count == 0) root.Items.Add(new MenuItem { Header = L.T("Brak innych Velivo w sieci (muszą być włączone i sparowane)"), IsEnabled = false });
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
                menu.Items.Add(peersMenu(L.T("📺 Odtwórz na…"), false));
                menu.Items.Add(peersMenu(L.T("👥 Oglądaj razem z…"), true));
                menu.Items.Add(new Separator());
                addItem(L.T("Pokaż w folderze"), () => { var r = list.SelectedItem as LibRow; if (r != null && !r.Missing) ShowInFolder(r.Item.Path); });
                addItem(L.T("💬 Pobierz napisy…"), () => { var r = list.SelectedItem as LibRow; if (r != null && !r.Missing) ShowSubtitleSearch(r.Item.Path, win); });
                addItem(L.T("Obejrzyj od początku"), () => { foreach (var r in sel()) r.Item.Pos = 0; var l = LoadLibrary(); foreach (var r in sel()) { var v = FindEntry(l, r.Item.Path, _libSharedView); if (v != null) v.Pos = 0; } SaveLibrary(l); play(); });
                if (_libTvView) { addItem(L.T("Usuń z biblioteki (plik zostaje na dysku)"), remove); addItem(L.T("🗑 Usuń z biblioteki i z dysku…"), removeFiles); return; }   // nagrania TV - bez przenoszenia
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
                addItem(L.T("Usuń z biblioteki (plik zostaje na dysku)"), remove);
                addItem(L.T("🗑 Usuń z biblioteki i z dysku…"), removeFiles);
            };
            list.ContextMenu = menu;
            var bar = new DockPanel { Margin = new Thickness(6), LastChildFill = false };
            DockPanel.SetDock(count, Dock.Left); bar.Children.Add(count);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right); bar.Children.Add(buttons);
            buttons.Children.Add(SmallButton(L.T("▶ Odtwórz"), play));
            buttons.Children.Add(SmallButton(L.T("➕ Dodaj filmy…"), () => { var f = PickVideoFiles(true, win); if (f != null) AddPathsToLibrary(f, _libSharedView && !_libTvView); }));
            buttons.Children.Add(SmallButton(L.T("📁 Dodaj folder…"), () => { var d = PickFolder(win); if (d != null) AddPathsToLibrary(new[] { d }, _libSharedView && !_libTvView); }));
            buttons.Children.Add(SmallButton(L.T("Usuń z biblioteki"), remove));
            // odswiez: wspolna biblioteka od nowa z innych komputerow + (gdy ustawiony) nowe dane z Jellyfin w tle
            buttons.Children.Add(SmallButton(L.T("🔄 Odśwież"), async () =>
            {
                _sharedCache = null;
                if (_libSharedView && !_libTvView) loadPeers(); else fill();
                var jc = LoadJellyfinConf();
                if (jc != null && !string.IsNullOrEmpty(jc.Url))
                    try { await System.Threading.Tasks.Task.Run(() => SyncJellyfin(jc, _ => { })); fill(); } catch (Exception) { }   // serwer wylaczony - zostaje lokalna baza
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
                if (m.Poster != null) { var pf = System.IO.Path.Combine(PosterDir, m.Poster); if (File.Exists(pf)) dPoster.Source = LoadLogo(pf); }
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

        bool EnsureLibVlc()
        {
            if (_libVlc != null) return true;
            if (!VlcInstalled) { ShowToast(L.T("🎬 Odtwarzacz filmów nie jest zainstalowany"), null); return false; }
            try
            {
                LibVLCSharp.Shared.Core.Initialize(VlcDir);
                // bez tytulu filmu na obrazie; sprzetowe dekodowanie (lzejsze dla procesora); bez sieciowych uslug VLC
                _libVlc = new LibVLC("--no-video-title-show", "--avcodec-hw=any", "--no-lua", "--no-stats", "--no-osd");
                return true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show(this, L.T("Nie udało się uruchomić odtwarzacza filmów:\n") + ex.Message, L.T("Odtwarzacz filmów"));
                return false;
            }
        }

        void PlayVideos(string[] files, int index)
        {
            if (files == null || files.Length == 0 || !EnsureLibVlc()) return;
            _playlist = files.ToList();
            _playIndex = Math.Max(0, Math.Min(index, files.Length - 1));
            // film otwarty z dysku trafia do biblioteki (jak "ostatnio odtwarzane" w VLC)
            var lib = LoadLibrary();
            if (AddToLibrary(lib, files.Where(f => !_remoteFilms.ContainsKey(f) && !_iptvPlaying.ContainsKey(f))) > 0) { SaveLibrary(lib); _libRefresh?.Invoke(); }   // filmy z innego komputera zostaja w jego bibliotece
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
            { "mini", "M19,11H11V17H19V11M23,19V5C23,3.88 22.1,3 21,3H3A2,2 0 0,0 1,5V19A2,2 0 0,0 3,21H21A2,2 0 0,0 23,19M21,19H3V4.97H21V19Z" },
            { "exitmini", "M21,3H3A2,2 0 0,0 1,5V19A2,2 0 0,0 3,21H21A2,2 0 0,0 23,19V5A2,2 0 0,0 21,3M21,19H3V5H21V19M13,9H5V7H15V17H13V9Z" },
            { "close", "M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z" },
            { "lector", "M9,5A4,4 0 0,1 13,9A4,4 0 0,1 9,13A4,4 0 0,1 5,9A4,4 0 0,1 9,5M9,15C11.67,15 17,16.34 17,19V21H1V19C1,16.34 6.33,15 9,15M16.76,5.36C18.78,7.56 18.78,10.61 16.76,12.63L15.08,10.94C15.92,9.76 15.92,8.23 15.08,7.05L16.76,5.36M20.07,2C24,6.05 23.97,12.11 20.07,16L18.44,14.37C21.21,11.19 21.21,6.65 18.44,3.63L20.07,2Z" },
            { "pinned", "M16,12V4H17V2H7V4H8V12L6,14V16H11.2V22H12.8V16H18V14L16,12Z" },
        };

        internal static System.Windows.Shapes.Path PlayerIcon(string name)
        {
            return new System.Windows.Shapes.Path { Data = Geometry.Parse(PlayerIcons[name]), Fill = Brushes.White, Width = 20, Height = 20, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
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

        void BuildVlcWindow()
        {
            LibraryVideo current = null;   // film, ktory teraz gra (jego wpis w bibliotece)
            var mp = new LibVLCSharp.Shared.MediaPlayer(_libVlc) { EnableKeyInput = false, EnableMouseInput = false, Volume = 100 };
            var view = new VideoView { MediaPlayer = mp, Background = Brushes.Black };
            var win = new Window { Title = L.T("Odtwarzacz filmów"), Width = 960, Height = 600, Background = Brushes.Black, Icon = Icon, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            // warstwa na obrazie (nad okienkiem VLC): dwuklik = pelny ekran, klik = pauza, ruch myszy = pokaz pasek
            var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
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
            var timeLen = new TextBlock { Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0), Text = "0:00", MinWidth = 52 };
            var seek = new Slider { Minimum = 0, Maximum = 1000, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            var seekRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(timeNow, Dock.Left); DockPanel.SetDock(timeLen, Dock.Right);
            seekRow.Children.Add(timeNow); seekRow.Children.Add(timeLen); seekRow.Children.Add(seek);

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
            var top = glyphBtn("pin", L.T("Zawsze na wierzchu"));
            var mini = glyphBtn("mini", L.T("Tryb mini – małe okienko na wierzchu (jak dla YouTube)"));
            var vol = new Slider { Minimum = 0, Maximum = 125, Value = 100, Width = 110, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, ToolTip = L.T("Głośność (↑ ↓, kółko myszy)") };
            var volText = new TextBlock { Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Width = 44, Margin = new Thickness(6, 0, 8, 0), Text = "100%" };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var b in new UIElement[] { prev, play, stop, next, back10, fwd10, speed }) left.Children.Add(b);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var b in new UIElement[] { subs, lector, audio, aspect, snap, lib, top, mini, full, mute, vol, volText }) right.Children.Add(b);
            var btnRow = new DockPanel { Margin = new Thickness(4, 2, 4, 4) };
            DockPanel.SetDock(left, Dock.Left); DockPanel.SetDock(right, Dock.Right);
            btnRow.Children.Add(left); btnRow.Children.Add(right);
            // pasek nakladany na obraz (jak w "Na wierzchu"): chowa sie po chwili bez ruchu myszy, film nie zmienia rozmiaru
            var controls = new StackPanel { Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x1C, 0x1C, 0x1C)), VerticalAlignment = VerticalAlignment.Bottom };
            controls.Children.Add(seekRow); controls.Children.Add(btnRow);
            overlay.Children.Add(controls);
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
                foreach (var b in new UIElement[] { subs, lector, audio, aspect, snap, lib, top }) b.Visibility = w >= 820 ? Visibility.Visible : Visibility.Collapsed;
                foreach (var b in new UIElement[] { speed, stop }) b.Visibility = w >= 600 ? Visibility.Visible : Visibility.Collapsed;
                foreach (var b in new UIElement[] { back10, fwd10, volText }) b.Visibility = w >= 480 ? Visibility.Visible : Visibility.Collapsed;
                vol.Width = w >= 480 ? 110 : 70;
            };

            bool seeking = false, full0 = false;
            WindowState prevState = WindowState.Normal;
            float[] speeds = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 3f };
            int speedIdx = 3;
            var osdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
            osdTimer.Tick += (s, e) => { osdTimer.Stop(); osd.Visibility = Visibility.Collapsed; };
            Action<string> showOsd = t => { osd.Text = t; osd.Visibility = Visibility.Visible; osdTimer.Stop(); osdTimer.Start(); };
            var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            // chowanie paska: tylko gdy film gra, mysz nie jest nad paskiem i zadne menu (napisy, dzwiek) nie jest otwarte
            hideTimer.Tick += (s, e) =>
            {
                hideTimer.Stop();
                if (!mp.IsPlaying || controls.IsMouseOver || controls.ContextMenu != null && controls.ContextMenu.IsOpen || Mouse.Captured != null) { hideTimer.Start(); return; }
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
                    var l = LoadLibrary();
                    var v = FindEntry(l, current.Path, IsShared(current));
                    if (v == null) return;
                    RecordProgress(v, t, len, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    SaveLibrary(l);
                    _libRefresh?.Invoke();
                }
                catch (Exception ex) { App.LogError(ex); }
            };
            var saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            saveTimer.Tick += (s, e) => { if (mp.IsPlaying) savePos(); };
            saveTimer.Start();

            // lektor: napisy z pliku obok filmu albo wczytane z menu Napisy
            var lc = new Lector();
            bool muted = false;
            Action<string, string> loadSubsText = (text, name) =>
            {
                lc.Cues = new List<SubCue>(); lc.File = null; lc.LastSpoken = -1;
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
            Action saveLector = () => { try { _settings.Save(DataDir); } catch (Exception) { } };
            var lectorOn = new CheckBox { Content = L.T("Lektor włączony"), Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8), IsChecked = _settings.VlcLector };
            // zapis ustawien tego filmu (glosnosc + lektor) chwile po ostatniej zmianie
            var filmPrefsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            filmPrefsTimer.Tick += (s, e) =>
            {
                filmPrefsTimer.Stop(); saveLector();
                if (current == null) return;
                current.Vol = _settings.VlcVolume; current.LVol = _settings.VlcLectorVolume; current.LRate = _settings.VlcLectorRate; current.LDuck = _settings.VlcLectorDuck; current.LVoice = _settings.VlcLectorVoice;
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
            // napisy na obrazie mozna ukryc - lektor czyta je dalej (z pliku napisow)
            var showSubs = new CheckBox { Content = L.T("Pokazuj napisy na ekranie"), Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8), IsChecked = _settings.VlcShowSubs };
            lectorPanel.Children.Add(showSubs);
            // glos lektora: automatycznie (jezyk napisow), glosy Piper (niepobrany - pobierze), glosy Windows
            lectorPanel.Children.Add(new TextBlock { Text = L.T("Głos lektora:"), Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 2) });
            var voiceBox = new ComboBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
            voiceBox.Items.Add(new ComboBoxItem { Content = L.T("Automatycznie (język napisów)"), Tag = "" });
            foreach (var pv in PiperVoices) voiceBox.Items.Add(new ComboBoxItem { Content = "Piper: " + L.T(pv.Label) + (PiperVoiceInstalled(pv) ? "" : L.T(" – pobierz")), Tag = "piper:" + pv.Id });
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
                    it.Content = "Piper: " + L.T(pv.Label);
                }
                _settings.VlcLectorVoice = tag; LectorStopVoice(lc, mp); filmPrefsChanged();
            };
            lectorPanel.Children.Add(voiceBox);
            var lvRow = sliderRow(L.T("Głośność lektora"), 10, 100, v => (int)v + "%", v => { _settings.VlcLectorVolume = v / 100; if (lc.Voice != null) lc.Voice.Volume = _settings.VlcLectorVolume; });
            var lrRow = sliderRow(L.T("Prędkość czytania"), 0.8, 2.0, v => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "×", v => _settings.VlcLectorRate = Math.Round(v * 4) / 4);
            var ldRow = sliderRow(L.T("Film podczas czytania"), 0, 100, v => v >= 100 ? L.T("bez ściszania") : (int)v + "%", v => _settings.VlcLectorDuck = (int)v);
            foreach (var r in new[] { lvRow, lrRow, ldRow }) lectorPanel.Children.Add(r.Item1);
            // ustawienia filmu do panelu i odtwarzacza (przy wczytaniu filmu): zapisane dla filmu, inaczej ostatnio uzywane
            Action applyFilmPrefs = () =>
            {
                lvRow.Item2.Value = (current?.LVol ?? _settings.VlcLectorVolume) * 100;
                lrRow.Item2.Value = current?.LRate ?? _settings.VlcLectorRate;
                ldRow.Item2.Value = current?.LDuck ?? _settings.VlcLectorDuck;
                settingVoice = true; _settings.VlcLectorVoice = current?.LVoice ?? _settings.VlcLectorVoice; selectVoice(_settings.VlcLectorVoice); settingVoice = false;
                vol.Value = current?.Vol ?? _settings.VlcVolume;
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
                try { _settings.Save(DataDir); } catch (Exception) { }
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
                    var spu = PickTrack(mp.SpuDescription.Select(t => (t.Id, t.Name)), current?.Spu ?? _settings.VlcSpu);
                    if (!_settings.VlcShowSubs) mp.SetSpu(-1);   // napisy ukryte (lektor czyta dalej)
                    else if (spu != int.MinValue) mp.SetSpu(spu);
                    else if (mp.Spu < 0) { var first = mp.SpuDescription.FirstOrDefault(t => t.Id >= 0); if (first.Name != null) mp.SetSpu(first.Id); }
                    var au = PickTrack(mp.AudioTrackDescription.Select(t => (t.Id, t.Name)), current?.Audio ?? _settings.VlcAudio);
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
            bool userStopped = false; long lastTvTime = -1; DateTime lastTvProgress = DateTime.Now; int tvRetries = 0;
            var tvWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            tvWatch.Tick += (s, e) =>
            {
                var pth = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                // opcja: ogladany kanal zaczal sie nagrywac - przejscie na plik nagrania (zostaje jedno polaczenie)
                if (pth != null && _iptvPlaying.ContainsKey(pth) && IptvSt.WatchFromRec && curRecFile == null && !userStopped && mp.State == VLCState.Playing && RecFileFor(pth) != null)
                { recResume = -1; _vlcLoad(_playIndex); return; }
                // opcja: dluga pauza na zywo - polaczenie z dostawca zwolnione (▶ laczy ponownie)
                if (pth != null && _iptvPlaying.ContainsKey(pth) && mp.State == VLCState.Paused && curRecFile == null && IptvSt.PauseRelease && (DateTime.Now - pausedAt).TotalMinutes >= 5)
                { userStopped = true; mp.Stop(); showOsd(L.T("⏸ Połączenie z dostawcą zwolnione po 5 min pauzy – ▶ wznawia")); return; }
                if (pth == null || !_iptvPlaying.ContainsKey(pth) || userStopped || mp.State == VLCState.Paused) { lastTvProgress = DateTime.Now; return; }
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
            Action<bool> showInfoRef = null;
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
                        if (fm.Poster != null) { var pf = System.IO.Path.Combine(PosterDir, fm.Poster); if (File.Exists(pf)) { infoLogo.Source = LoadLogo(pf); infoLogo.Visibility = Visibility.Visible; infoLogo.Height = 90 * infoK; infoLogo.Width = 64 * infoK; } }
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
                    infoBox.Margin = new Thickness(16, 0, 16, controls.Visibility == Visibility.Visible ? controls.ActualHeight + 10 : 16);
                    infoBox.Visibility = Visibility.Visible; infoShownFor = null;
                    infoTimer.Stop(); infoTimer.Interval = TimeSpan.FromSeconds(IptvSt.InfoSec == 0 ? 8 : IptvSt.InfoSec); infoTimer.Start();
                    return;
                }
                infoLogo.Visibility = Visibility.Visible; infoLogo.Width = 72 * infoK; infoLogo.Height = 44 * infoK;
                var l = EpgFor(_iptvEpg, ch); var now = DateTime.Now; var p0 = EpgAt(l, now); var nx = l?.FirstOrDefault(x => x.Start >= (p0?.Stop ?? now));
                infoBox.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Max(20, Math.Min(100, IptvSt.InfoAlpha)) / 100), 0x14, 0x16, 0x1A));
                infoText.Children.Clear();
                Func<string, double, FontWeight, Brush, TextBlock> tb = (t, size, w, b) => new TextBlock { Text = t, FontSize = size * infoK, FontWeight = w, Foreground = b, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                infoText.Children.Add(tb((IsRecording(pth) ? "⏺ REC   " : "") + ch.Name, 15, FontWeights.SemiBold, Brushes.LightGray));
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
                infoBox.Margin = new Thickness(16, 0, 16, controls.Visibility == Visibility.Visible ? controls.ActualHeight + 10 : 16);   // nad paskiem sterowania
                infoBox.Visibility = Visibility.Visible;
                infoShownFor = p0 != null ? pth + p0.Start.Ticks : pth;
                infoTimer.Stop();
                var sec = IptvSt.InfoSec == 0 ? 8 : IptvSt.InfoSec;
                infoTimer.Interval = TimeSpan.FromSeconds(sec); infoTimer.Start();
            };
            showInfoRef = showInfo;

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
            var chDock = new DockPanel(); DockPanel.SetDock(chGroup, Dock.Top); chDock.Children.Add(chGroup); chDock.Children.Add(chList);
            var chPanel = new Border { Child = chDock, Width = 340, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 0), Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(0, 8, 8, 0) };
            overlay.Children.Add(chPanel);
            var chHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            chHide.Tick += (s, e) => { chHide.Stop(); if (!chPanel.IsMouseOver) chPanel.Visibility = Visibility.Collapsed; };
            chPanel.MouseLeave += (s, e) => { chHide.Stop(); chHide.Start(); };
            chPanel.MouseEnter += (s, e) => chHide.Stop();
            chPanel.MouseLeftButtonDown += (s, e) => e.Handled = true;   // klik na liscie to nie pauza
            bool? chTvMode = null;   // lista kanalow albo lista filmow (zmiana - nowe grupy)
            Action<bool> showChannels = null;
            // lista filmow nad filmem: biezaca lista / ostatnio ogladane / wszystkie filmy - plakat, tytul, rok, ocena, ile obejrzane
            Action showFilms = () =>
            {
                const string fCur = "\u0001cur", fRec = "\u0001rec", fAll = "\u0001all";
                if (chGroup.Items.Count == 0)
                {
                    chGroup.Items.Add(new ComboBoxItem { Content = L.T("▶ Bieżąca lista"), Tag = fCur });
                    chGroup.Items.Add(new ComboBoxItem { Content = L.T("🕘 Ostatnio oglądane"), Tag = fRec });
                    chGroup.Items.Add(new ComboBoxItem { Content = L.T("🎬 Wszystkie filmy"), Tag = fAll });
                }
                if (chPanel.Visibility != Visibility.Visible) chGroup.SelectedIndex = 0;
                var fk = (chGroup.SelectedItem as ComboBoxItem)?.Tag as string ?? fCur;
                var libAll = LoadLibrary();
                List<LibraryVideo> films = fk == fCur ? _playlist.Select(u => FindEntry(libAll, u, null) ?? new LibraryVideo { Path = u, Title = System.IO.Path.GetFileNameWithoutExtension(u) }).ToList()
                    : fk == fRec ? libAll.Where(v => v.Last > 0 && v.Peer == null).OrderByDescending(v => v.Last).Take(60).ToList()
                    : libAll.Where(v => v.Peer == null).OrderBy(v => MetaTitle(MetaFor(v.Path)) ?? CleanTitle(v.Title), StringComparer.CurrentCultureIgnoreCase).ToList();
                var curP = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                chList.Items.Clear(); ListBoxItem cur = null;
                for (int k = 0; k < films.Count; k++)
                {
                    var f = films[k]; int idx = k; var m = MetaFor(f.Path);
                    var img = new Image { Width = 34, Height = 50, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                    if (m?.Poster != null) { var pf = System.IO.Path.Combine(PosterDir, m.Poster); if (File.Exists(pf)) img.Source = LoadLogo(pf); }
                    var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    txt.Children.Add(new TextBlock { Text = MetaTitle(m) ?? CleanTitle(f.Title), FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
                    var sub = string.Join("  ·  ", new[] { m?.Rating > 0 ? "★ " + m.Rating.ToString("0.0") : null, m?.Genres.Count > 0 ? m.Genres[0] : null, f.Len > 0 ? FormatTime(f.Len) : null, SeenText(f) is string st && st.Length > 0 ? L.T("obejrzane ") + st : null }.Where(x => x != null));
                    if (sub.Length > 0) txt.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Brushes.Silver, TextTrimming = TextTrimming.CharacterEllipsis });
                    var row = new DockPanel(); DockPanel.SetDock(img, Dock.Left); row.Children.Add(img); row.Children.Add(txt);
                    var it = new ListBoxItem { Content = row, Padding = new Thickness(8, 4, 8, 4), Cursor = Cursors.Hand };
                    if (string.Equals(f.Path, curP, StringComparison.OrdinalIgnoreCase)) { it.Background = new SolidColorBrush(Color.FromArgb(0x66, 0x3B, 0x82, 0xF6)); cur = it; }
                    it.PreviewMouseLeftButtonUp += (q, w2) =>
                    {
                        if (fk == fCur) _vlcLoad(idx);
                        else { var paths = films.Select(x => x.Path).ToList(); PlayVideos(paths.ToArray(), idx); }   // ta lista staje sie lista odtwarzania
                        showChannelsRefresh();
                    };
                    chList.Items.Add(it);
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
                if (chTvMode != tvMode) { chGroup.Items.Clear(); chTvMode = tvMode; }
                if (!tvMode) { showFilms(); return; }
                // grupy (raz na otwarcie): biezaca + z listy kanalow
                const string curKey = "\u0001cur", favKey = "\u0001fav", recKey = "\u0001rec";
                if (chGroup.Items.Count == 0)
                {
                    chGroup.Items.Add(new ComboBoxItem { Content = L.T("▶ Bieżąca lista"), Tag = curKey });
                    chGroup.Items.Add(new ComboBoxItem { Content = L.T("⭐ Ulubione"), Tag = favKey });
                    chGroup.Items.Add(new ComboBoxItem { Content = L.T("🕘 Ostatnio oglądane"), Tag = recKey });
                    if (_iptvPl != null) foreach (var g in _iptvPl.Channels.Select(c => c.Group).Distinct().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)) chGroup.Items.Add(new ComboBoxItem { Content = g.Length == 0 ? L.T("(bez grupy)") : g, Tag = g });
                }
                if (chPanel.Visibility != Visibility.Visible) chGroup.SelectedIndex = 0;   // kazde otwarcie: biezaca lista i biezacy kanal
                var gk = (chGroup.SelectedItem as ComboBoxItem)?.Tag as string ?? curKey;
                List<IptvChannel> chans;
                if (gk == curKey) chans = _playlist.Where(u => _iptvPlaying.ContainsKey(u)).Select(u => _iptvPlaying[u]).ToList();
                else
                {
                    var all = _iptvPl?.Channels ?? new List<IptvChannel>();
                    chans = gk == favKey ? IptvSt.Fav.Select(u => all.FirstOrDefault(c => c.Url == u)).Where(c => c != null).ToList()
                        : gk == recKey ? IptvSt.Recent.Select(u => all.FirstOrDefault(c => c.Url == u)).Where(c => c != null).ToList()
                        : all.Where(c => c.Group == gk).ToList();
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
                    txt.Children.Add(new TextBlock { Text = (IptvSt.Fav.Contains(ch.Url) ? "⭐ " : "") + ch.Name, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
                    if (nowP != null) txt.Children.Add(new TextBlock { Text = nowP.Start.ToString("HH:mm") + "  " + nowP.Title, FontSize = 11.5, Foreground = Brushes.Silver, TextTrimming = TextTrimming.CharacterEllipsis });
                    var row = new DockPanel(); DockPanel.SetDock(img, Dock.Left); row.Children.Add(img); row.Children.Add(txt);
                    var it = new ListBoxItem { Content = row, Padding = new Thickness(8, 4, 8, 4), Cursor = Cursors.Hand };
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
                if (!(chGroup.SelectedItem is ComboBoxItem gi) || !(gi.Tag is string g)) return;
                if (chPanel.Visibility == Visibility.Visible) showChannels(true);
            };
            chGroup.DropDownOpened += (s, e) => chHide.Stop();   // wybieranie grupy - lista nie chowa sie
            // pasek informacji pokazuje sie razem z paskiem sterowania (ruch mysza), a znika po swoim czasie (ustawienie "Znikanie po")
            controls.IsVisibleChanged += (s, e) =>
            {
                if (controls.IsVisible && infoBox.Visibility != Visibility.Visible) showInfo(true);
                else if (infoBox.Visibility == Visibility.Visible) infoBox.Margin = new Thickness(16, 0, 16, controls.IsVisible ? controls.ActualHeight + 10 : 16);
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
            _vlcLoad = i =>
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
                if (tv != null) current = null;
                else if (rf == null) current = FindEntry(LoadLibrary(), path, _playShared);
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
                using (var media = curRecFile != null ? new Media(_libVlc, curRecFile, FromType.FromPath) : stream ? new Media(_libVlc, new Uri(path)) : new Media(_libVlc, path, FromType.FromPath))
                {
                    // napisy o tej samej nazwie obok filmu (film.srt, film.pl.srt) dolaczane same - jak w VLC; przy strumieniu - z komputera wlasciciela
                    if (stream && rf?.Sub != null) media.AddSlave(MediaSlaveType.Subtitle, 4, rf.Sub);
                    if (tv != null && curRecFile == null) foreach (var o in tv.Opts) media.AddOption(o);   // naglowki wymagane przez dostawce (z listy)
                    if (tv != null && curRecFile == null) media.AddOption(":network-caching=" + Math.Max(1, IptvSt.Buffer) * 1000);   // zapas bufora (ustawienie)
                    mp.Play(media);
                }
                long resume = current != null ? ResumeAt(current.Pos, current.Len) : 0;
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
                    var once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    once.Tick += (s, e) => { once.Stop(); if (mp.Length > 0) { mp.Time = r; showOsd(L.T("Wznowiono od ") + FormatTime(r)); } };
                    once.Start();
                }
                // sciezki filmu znane chwile po starcie - wtedy ustawiamy zapamietane napisy i dzwiek
                var tracksTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                tracksTimer.Tick += (s, e) => { tracksTimer.Stop(); applyTracks(); mp.Volume = muted ? 0 : (int)vol.Value; };   // VLC przyjmuje glosnosc pewnie dopiero po starcie filmu
                tracksTimer.Start();
                if (tv != null && _recording.Count > 0) WarnOneConnection();   // trwa nagrywanie - drugie polaczenie
                if (tv != null) { IptvSt.Watched[path] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var rc = IptvSt.Recent; rc.Remove(path); rc.Insert(0, path); if (rc.Count > 30) rc.RemoveRange(30, rc.Count - 30); SaveIptv(IptvSt); _iptvJobsChanged?.Invoke(); }   // lista kanalow odswieza "Ostatnio ogladane"   // ostatnio ogladane - takze po zmianie kanalu w odtwarzaczu
                if (tv != null) showOsd("📺 " + (i + 1) + ". " + tv.Name);
                var infoOnce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(tv != null ? 700 : 1500) };   // film - gdy znana jest dlugosc
                infoOnce.Tick += (s, e) => { infoOnce.Stop(); if (_playlist.Count > _playIndex && _playlist[_playIndex] == path) showInfo(false); };
                infoOnce.Start();
                speedIdx = 3; speed.Content = "1×";
                prev.IsEnabled = i > 0; next.IsEnabled = i < _playlist.Count - 1;
            };

            mp.LengthChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() => timeLen.Text = FormatTime(e.Length)));
            mp.TimeChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                timeNow.Text = FormatTime(e.Time);
                if (!seeking && mp.Length > 0) seek.Value = 1000.0 * e.Time / mp.Length;
            }));
            var see = overlay.Background;   // prawie przezroczysta - widac film, a klik trafia w okno
            mp.Playing += (s, e) => Dispatcher.BeginInvoke(new Action(() => { play.Content = PlayerIcon("pause"); overlay.Background = see; }));
            mp.Paused += (s, e) => Dispatcher.BeginInvoke(new Action(() => play.Content = PlayerIcon("play")));
            // po Stop / koncu pod obrazem jest jasne okienko VLC - zaslaniamy je na czarno
            mp.Stopped += (s, e) => Dispatcher.BeginInvoke(new Action(() => { play.Content = PlayerIcon("play"); seek.Value = 0; timeNow.Text = "0:00"; overlay.Background = Brushes.Black; }));
            mp.EndReached += (s, e) => Dispatcher.BeginInvoke(new Action(() => overlay.Background = Brushes.Black));
            mp.EndReached += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (current != null && _remoteFilms.TryGetValue(current.Path, out var rfe)) { SendRemoteProgress(rfe, 0, mp.Length, true); current = null; }
                if (current != null) { var l = LoadLibrary(); var v = FindEntry(l, current.Path, IsShared(current)); if (v != null) { v.Last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); v.Done = true; v.Pos = 0; SaveLibrary(l); _libRefresh?.Invoke(); } current = null; }
                // z nagrania: dojechalismy do konca zapisanego pliku - ponownie otworz plik (urosl) od tego miejsca
                if (curRecFile != null && _playlist.Count > _playIndex) { recResume = Math.Max(0, mp.Length - 1000); _vlcLoad(_playIndex); return; }
                if (_playlist.Count > _playIndex && _iptvPlaying.ContainsKey(_playlist[_playIndex])) { showOsd(L.T("📺 Brak sygnału – łączę ponownie…")); return; }   // telewizja: bez przeskoku na nastepny kanal; strażnik polaczy ponownie
                if (_playIndex < _playlist.Count - 1) _vlcLoad(_playIndex + 1);   // nastepny z listy (VLC nie moze startowac w swoim watku zdarzen)
            }));
            mp.EncounteredError += (s, e) => Dispatcher.BeginInvoke(new Action(() => showOsd(L.T("Nie da się odtworzyć tego pliku"))));

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
            vol.ValueChanged += (s, e) => { _settings.VlcVolume = (int)vol.Value; filmPrefsChanged(); if (muted && vol.Value > 0) muted = false; mp.Volume = muted ? 0 : (int)vol.Value; volText.Text = (int)vol.Value + "%"; mute.Content = PlayerIcon(muted || vol.Value == 0 ? "mute" : "volume"); };
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
                if (full0) { prevState = win.WindowState; win.WindowStyle = WindowStyle.None; win.WindowState = WindowState.Normal; win.WindowState = WindowState.Maximized; win.Topmost = true; }
                else { win.Topmost = _settings.VlcTopmost; win.WindowStyle = WindowStyle.SingleBorderWindow; win.WindowState = prevState; }
                showBar();
                full.Content = PlayerIcon(full0 ? "exitfull" : "full");
            };
            exitFull = () => { if (full0) toggleFull(); };

            play.Click += (s, e) => togglePlay();
            stop.Click += (s, e) => { userStopped = true; savePos(); mp.Stop(); LeaveTogether(); };   // Stop konczy wspolne ogladanie tylko tutaj
            prev.Click += (s, e) => _vlcLoad(_playIndex - 1);
            next.Click += (s, e) => _vlcLoad(_playIndex + 1);
            back10.Click += (s, e) => jump(-10000);
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
                try { _settings.Save(DataDir); } catch (Exception) { }
            };
            win.Topmost = _settings.VlcTopmost; showTop();
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
            seek.PreviewMouseUp += (s, e) => { if (mp.Length > 0) { mp.Time = (long)(seek.Value / 1000 * mp.Length); TogetherSend("seek", mp.Time); } seeking = false; };
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
                var load = new MenuItem { Header = L.T("Wczytaj napisy z pliku…") };
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
                            if (MessageBox.Show(win, string.Format(L.T("Usunąć plik napisów „{0}” (do Kosza)?"), System.IO.Path.GetFileName(f)), L.T("Napisy"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
                var delay = new MenuItem { Header = L.T("Opóźnienie napisów") };
                foreach (var ms in new[] { -1000, -250, 0, 250, 1000 })
                {
                    var d = ms;
                    var mi = new MenuItem { Header = d == 0 ? L.T("Bez przesunięcia") : (d > 0 ? "+" : "−") + Math.Abs(d) + " ms" };
                    mi.Click += (a, b) => { mp.SetSpuDelay(d == 0 ? 0 : mp.SpuDelay + d * 1000L); showOsd(L.T("Napisy: ") + (mp.SpuDelay / 1000) + " ms"); };
                    delay.Items.Add(mi);
                }
                cm.Items.Add(delay);
            });
            popup(audio, cm =>
            {
                int cur = mp.AudioTrack;
                foreach (var t in mp.AudioTrackDescription)
                {
                    var id = t.Id;
                    var mi = new MenuItem { Header = id < 0 ? L.T("Bez dźwięku") : t.Name, IsCheckable = true, IsChecked = id == cur };
                    var nm = t.Name;
                    mi.Click += (a, b) => { if (id >= 0) { mp.SetAudioTrack(id); rememberTrack(false, nm); } else mp.SetAudioTrack(id); };
                    cm.Items.Add(mi);
                }
                if (cm.Items.Count == 0) cm.Items.Add(new MenuItem { Header = L.T("(brak ścieżek)"), IsEnabled = false });
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

            // mysz na obrazie: klik = pauza, dwuklik = pelny ekran, kolko = glosnosc, ruch = pokaz pasek (pelny ekran)
            overlay.MouseLeftButtonDown += (s, e) =>
            {
                if (controls.IsMouseOver) return;   // klik na pasku to nie klik w obraz
                var curPath = _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                if (chPanel.Visibility == Visibility.Visible) { showChannels(false); e.Handled = true; return; }   // klik obok listy - schowaj (bez pauzy)
                if (e.ClickCount == 2) { togglePlay(); toggleFull(); }   // pierwszy klik dwukliku juz zatrzymal - wznawiamy
                else togglePlay();
            };
            // prawy przycisk na obrazie: najczestsze rzeczy pod reka
            overlay.MouseRightButtonUp += (s, e) =>
            {
                if (controls.IsMouseOver) return;
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
                var infoM = new MenuItem { Header = L.T("ℹ Pasek informacji") };
                var hideM = new MenuItem { Header = L.T("Znikanie po") };
                foreach (var sec0 in new[] { 3, 5, 8, 10, 15, 0 })
                {
                    var sec = sec0; var mi = new MenuItem { Header = sec == 0 ? L.T("nie pokazuj sam (tylko klawisz I)") : sec + " s", IsCheckable = true, IsChecked = IptvSt.InfoSec == sec };
                    mi.Click += (q, w2) => { IptvSt.InfoSec = sec; SaveIptv(IptvSt); showInfo(true); };
                    hideM.Items.Add(mi);
                }
                var alphaM = new MenuItem { Header = L.T("Przezroczystość tła") };
                foreach (var a0 in new[] { 100, 85, 70, 55, 40 })
                {
                    var al = a0; var mi = new MenuItem { Header = (100 - al) + " %" + (al == 100 ? L.T(" (pełne tło)") : ""), IsCheckable = true, IsChecked = IptvSt.InfoAlpha == al };
                    mi.Click += (q, w2) => { IptvSt.InfoAlpha = al; SaveIptv(IptvSt); showInfo(true); };
                    alphaM.Items.Add(mi);
                }
                var sizeM = new MenuItem { Header = L.T("Wielkość tekstu (względem okna)") };
                foreach (var z0 in new[] { new object[] { 85, "Mały" }, new object[] { 100, "Normalny" }, new object[] { 125, "Duży" }, new object[] { 150, "Bardzo duży" } })
                {
                    var z = (int)z0[0]; var mi = new MenuItem { Header = L.T((string)z0[1]), IsCheckable = true, IsChecked = IptvSt.InfoScale == z };
                    mi.Click += (q, w2) => { IptvSt.InfoScale = z; SaveIptv(IptvSt); showInfo(true); };
                    sizeM.Items.Add(mi);
                }
                infoM.Items.Add(hideM); infoM.Items.Add(alphaM); infoM.Items.Add(sizeM);
                cm.Items.Add(infoM);
                add(L.T("🎬 Biblioteka filmów"), click(lib), true);
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
                    if (nowP != null) add(L.T("▶ Teraz: ") + nowP.Title + L.T(" (do ") + nowP.Stop.ToString("HH:mm") + ")", () => ShowEpgGuide(Window.GetWindow(overlay) ?? win, path), true);
                    if (nextP != null) add(L.T("⏭ Następnie: ") + nextP.Start.ToString("HH:mm") + " " + nextP.Title, () => ShowEpgGuide(Window.GetWindow(overlay) ?? win, path), true);
                    bool isFav = IptvSt.Fav.Contains(path);
                    add(isFav ? L.T("⭐ Usuń z ulubionych") : L.T("⭐ Dodaj do ulubionych"), () => { if (!IptvSt.Fav.Remove(path)) IptvSt.Fav.Add(path); SaveIptv(IptvSt); _iptvJobsChanged?.Invoke(); }, true);
                    // ostatnio ogladane kanaly - szybkie przelaczenie
                    var recent = IptvSt.Recent.Where(u => u != path).Take(10).Select(u => _iptvPlaying.TryGetValue(u, out var c1) ? c1 : _iptvPl?.Channels.FirstOrDefault(c => c.Url == u)).Where(c => c != null).ToList();
                    if (recent.Count > 0)
                    {
                        var rm = new MenuItem { Header = L.T("🕘 Ostatnio oglądane kanały") };
                        foreach (var c0 in recent) { var c = c0; var mi = new MenuItem { Header = c.Name }; mi.Click += (q, w2) => { _iptvPlaying[c.Url] = c; PlayVideos(new[] { c.Url }, 0); }; rm.Items.Add(mi); }
                        cm.Items.Add(rm);
                    }
                    add(L.T("⏮ Poprzedni kanał (PgUp)"), () => _vlcLoad(_playIndex - 1), _playIndex > 0);
                    add(L.T("⏭ Następny kanał (PgDn)"), () => _vlcLoad(_playIndex + 1), _playIndex < _playlist.Count - 1);
                    add(L.T("🔄 Odśwież kanał (F5)"), () => { showOsd(L.T("🔄 Odświeżam kanał…")); _vlcLoad(_playIndex); }, true);
                    if (curRecFile != null) add(L.T("⏩ Na żywo (koniec nagrania)"), () => { recResume = -1; _vlcLoad(_playIndex); }, true);
                    else if (IptvSt.WatchFromRec && RecFileFor(path) != null) add(L.T("⏺ Oglądaj z nagrania (jedno połączenie, pauza i cofanie)"), () => { recResume = -1; _vlcLoad(_playIndex); }, true);
                    var optM = new MenuItem { Header = L.T("⚙ Opcje połączenia") };
                    var o1 = new MenuItem { Header = L.T("Nagrywany kanał oglądaj z nagrania (jedno połączenie, pauza i cofanie)"), IsCheckable = true, IsChecked = IptvSt.WatchFromRec };
                    o1.Click += (q, w2) => { IptvSt.WatchFromRec = !IptvSt.WatchFromRec; SaveIptv(IptvSt); };
                    var o2 = new MenuItem { Header = L.T("Po 5 min pauzy zwolnij połączenie z dostawcą"), IsCheckable = true, IsChecked = IptvSt.PauseRelease };
                    o2.Click += (q, w2) => { IptvSt.PauseRelease = !IptvSt.PauseRelease; SaveIptv(IptvSt); };
                    optM.Items.Add(o1); optM.Items.Add(o2); cm.Items.Add(optM);
                    // zapas bufora: wiecej = mniej przyciec, ale troche wieksze opoznienie i wolniejsze przelaczanie
                    var bufM = new MenuItem { Header = L.T("⏱ Bufor kanałów: ") + IptvSt.Buffer + " s" };
                    foreach (var sec0 in new[] { 1, 3, 5, 10 })
                    {
                        var sec = sec0; var bi = new MenuItem { Header = sec + " s" + (sec == 1 ? L.T(" (najmniejsze opóźnienie)") : sec == 3 ? L.T(" (zalecane przy przycięciach)") : ""), IsCheckable = true, IsChecked = IptvSt.Buffer == sec };
                        bi.Click += (q, w2) => { IptvSt.Buffer = sec; SaveIptv(IptvSt); showOsd(L.T("Bufor: ") + sec + " s"); _vlcLoad(_playIndex); };
                        bufM.Items.Add(bi);
                    }
                    cm.Items.Add(bufM);
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
                        if (peers.Count == 0) root.Items.Add(new MenuItem { Header = L.T("Brak innych Velivo w sieci (muszą być włączone i sparowane)"), IsEnabled = false });
                        foreach (var p0 in peers)
                        {
                            var p = p0; var mi = new MenuItem { Header = "🖥 " + p.Device };
                            mi.Click += (a, b) =>
                            {
                                savePos();
                                var v = current != null ? current : new LibraryVideo { Path = path, Title = System.IO.Path.GetFileNameWithoutExtension(path) };
                                v.Pos = mp.Time; v.Done = false;
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
                add(L.T("Napisy na ekranie: ") + (_settings.VlcShowSubs ? L.T("ukryj") : L.T("pokaż")), () => { showSubs.IsChecked = !_settings.VlcShowSubs; showSubs.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }, true);
                add(_settings.VlcLector ? L.T("🗣 Wyłącz lektora") : L.T("🗣 Włącz lektora"), () => { lectorOn.IsChecked = !_settings.VlcLector; lectorOn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }, true);
                add(L.T("📌 Zawsze na wierzchu"), click(top), true);
                add(L.T("Tryb mini"), toggleMini, true);
                add(L.T("Pełny ekran"), toggleFull, true);
                add(L.T("📷 Zrzut klatki"), click(snap), true);
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
            mp.Paused += (s, e) => Dispatcher.BeginInvoke(showBar);
            mp.Paused += (s, e) => Dispatcher.BeginInvoke(new Action(() => pausedAt = DateTime.Now));   // pauza - pasek zostaje widoczny
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
                    case Key.N: case Key.PageDown: _vlcLoad(_playIndex + 1); break;
                    case Key.PageUp: _vlcLoad(_playIndex - 1); break;
                    case Key.P: _vlcLoad(_playIndex - 1); break;
                    case Key.OemOpenBrackets: setSpeed(-1); break;
                    case Key.OemCloseBrackets: setSpeed(1); break;
                    case Key.S: userStopped = true; mp.Stop(); break;
                    default: h = false; break;
                }
                if (h) e.Handled = true;
            };
            win.Closing += (s, e) => savePos();
            win.Closed += (s, e) =>
            {
                saveTimer.Stop(); hideTimer.Stop(); osdTimer.Stop(); lectorTimer.Stop(); LectorStopVoice(lc, mp);
                _vlcWin = null; _vlcLoad = null; _vlcRemoteCtl = null; _vlcAsk = null; _playShared = null;
                LeaveTogether();   // zamkniecie odtwarzacza: wychodzisz ze wspolnego ogladania, druga osoba oglada dalej
                view.MediaPlayer = null;
                System.Threading.Tasks.Task.Run(() => { try { mp.Stop(); mp.Dispose(); } catch (Exception) { } });   // Stop VLC moze chwile trwac - nie w watku okna
            };
            _vlcWin = win;
            win.Show();
        }
    }
}
