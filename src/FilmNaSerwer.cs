using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // ➕ film z innego źródła (dysk, Eksplorator, torrent, pobrany) - zamiast dodawać sam do biblioteki: pytanie w rogu odtwarzacza
    // (🔒 moja / 📚 wspólna / 🗄 na serwer / nie), makieta dokumentacja/makieta-dodaj-do-biblioteki.html. Ustawienie „film-obcy”: pytaj / sam / nie.
    // 🗄 na serwer: wybór serwera (ten komputer albo komputer w sieci domowej z Velivo Serwer filmów) i jego folderu; serwer sam POBIERA plik
    // z tego komputera (ten sam bezpieczny strumień co „Odtwórz na”, z wznawianiem po przerwie), po pobraniu przegląda foldery (opis, plakat, obsada).
    public partial class MainWindow
    {
        static string FilmObcyTryb => GetPref("film-obcy") ?? "pytaj";
        static List<string> FilmObcyNiePytaj => (GetPref("film-obcy-nie") ?? "").Split('|').Where(x => x.Length > 0).ToList();

        // wołane z PlayVideos: pliki spoza biblioteki (nie z serwera, TV, innego komputera, komunikatora)
        void FilmObcy(string[] files)
        {
            var lib = LoadLibrary();
            var nowe = files.Where(f => !string.IsNullOrEmpty(f) && File.Exists(f) && !IsAudioFile(f) && IsVlcVideoFile(f) && !_remoteFilms.ContainsKey(f) && !_iptvPlaying.ContainsKey(f) && !WFolderze(f) && FindEntry(lib, f, null) == null).ToList();
            if (nowe.Count == 0) return;
            var tryb = FilmObcyTryb;
            if (tryb == "sam") { if (AddToLibrary(lib, nowe) > 0) { SaveLibrary(lib); _libRefresh?.Invoke(); } return; }   // jak dawniej
            if (tryb == "nie") return;
            var dir = Path.GetDirectoryName(nowe[0]) ?? "";
            if (FilmObcyNiePytaj.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase))) return;
            Dispatcher.BeginInvoke(new Action(() => FilmObcyPytanie(nowe)), System.Windows.Threading.DispatcherPriority.Background);
        }

        Window _obcyWin;
        void FilmObcyPytanie(List<string> nowe)
        {
            try { _obcyWin?.Close(); } catch (Exception) { }
            var owner = _vlcWin; if (owner == null || !owner.IsLoaded) return;
            var st = new StackPanel { Margin = new Thickness(12, 10, 12, 10), MaxWidth = 360 };
            var tytul = nowe.Count == 1 ? Path.GetFileNameWithoutExtension(nowe[0]) : nowe.Count + L.T(" filmów");
            st.Children.Add(new TextBlock { Text = L.T("➕ Dodać do biblioteki: ") + tytul + "?", Foreground = SFg, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            st.Children.Add(new TextBlock { Text = L.T("Film z: ") + Path.GetDirectoryName(nowe[0]), Foreground = SDim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });
            var w = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x14, 0x18, 0x21)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x3F, 0x6B)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = st } };
            _obcyWin = w;
            void Dodaj(bool wspolna) { var l = LoadLibrary(); int n = AddToLibrary(l, nowe, wspolna); if (n > 0) { SaveLibrary(l); _libRefresh?.Invoke(); } ShowToast((wspolna ? L.T("📚 Dodano do wspólnej biblioteki: ") : L.T("🔒 Dodano do mojej biblioteki: ")) + n, null); w.Close(); }
            var r1 = new WrapPanel(); r1.Children.Add(SBtnMake(L.T("🔒 Moja (ten komputer)"), () => Dodaj(false))); r1.Children.Add(SBtnMake(L.T("📚 Wspólna"), () => Dodaj(true)));
            r1.Children.Add(SBtnMake(L.T("🗄 Na serwer…"), () => { w.Close(); FilmNaSerwerOkno(owner, nowe); }, new LinearGradientBrush(Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x06, 0xB6, 0xD4), 0)));
            r1.Children.Add(SBtnMake(L.T("Nie"), () => w.Close()));
            st.Children.Add(r1);
            var nie = new CheckBox { Content = L.T("Nie pytaj o filmy z tego folderu"), Foreground = SDim, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
            nie.Click += (s, e) => { var l = FilmObcyNiePytaj; var d = Path.GetDirectoryName(nowe[0]) ?? ""; l.RemoveAll(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase)); if (nie.IsChecked == true) l.Add(d); SetPref("film-obcy-nie", string.Join("|", l)); };
            st.Children.Add(nie);
            st.Children.Add(new TextBlock { Text = L.T("Bez wyboru – po 20 s znika, nic nie dodane. Zmiana: ⚙ → Biblioteka → Nowy film."), Foreground = SDim, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            tm.Tick += (s, e) => { if (w.IsMouseOver) return; tm.Stop(); w.Close(); };
            w.Closed += (s, e) => { tm.Stop(); if (_obcyWin == w) _obcyWin = null; };
            w.Loaded += (s, e) =>
            {   // prawy dolny róg odtwarzacza (nad paskiem); zmaksymalizowany / pełny ekran - róg ekranu
                var a = SystemParameters.WorkArea;
                bool pelny = owner.WindowState == WindowState.Maximized;
                double r = pelny ? (owner.WindowStyle == WindowStyle.None ? SystemParameters.PrimaryScreenWidth : a.Right) : owner.Left + owner.ActualWidth, b = pelny ? (owner.WindowStyle == WindowStyle.None ? SystemParameters.PrimaryScreenHeight : a.Bottom) : owner.Top + owner.ActualHeight;
                w.Left = r - w.ActualWidth - 24; w.Top = b - w.ActualHeight - 120;
            };
            w.Show(); tm.Start();
        }

        // ---------- 🗄 wybór serwera i folderu ----------
        readonly ConcurrentDictionary<string, List<(LanPeerInfo Peer, List<LanFilmItem> Foldery)>> _folderOdp = new ConcurrentDictionary<string, List<(LanPeerInfo, List<LanFilmItem>)>>();

        static long WolneMiejsce(string folder) { try { return new DriveInfo(Path.GetPathRoot(folder)).AvailableFreeSpace; } catch (Exception) { return -1; } }
        static string Rozmiar(long b) => b < 0 ? "?" : b >= 1L << 40 ? (b / (double)(1L << 40)).ToString("0.0") + " TB" : b >= 1 << 30 ? (b / (double)(1 << 30)).ToString("0.0") + " GB" : (b / (double)(1 << 20)).ToString("0") + " MB";

        async void FilmNaSerwerOkno(Window owner, List<string> pliki)
        {
            var w = new Window { Title = L.T("🗄 Wyślij film na serwer"), Width = 560, SizeToContent = SizeToContent.Height, Owner = owner, Topmost = owner?.Topmost ?? false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = SBg, ResizeMode = ResizeMode.NoResize };
            var st = new StackPanel { Margin = new Thickness(14) }; w.Content = st;
            long suma = pliki.Sum(p => { try { return PlikiFilmu(p).Pliki.Sum(x => new FileInfo(x.Pelna).Length); } catch (Exception) { return 0L; } });   // z napisami / całym folderem filmu
            st.Children.Add(new TextBlock { Text = (pliki.Count == 1 ? Path.GetFileName(pliki[0]) : pliki.Count + L.T(" filmów")) + " · " + Rozmiar(suma), Foreground = SDim, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
            ListBox Lista(double h) => new ListBox { Height = h, Background = SCard, Foreground = SFg, BorderBrush = SLine, Margin = new Thickness(0, 4, 0, 10) };
            st.Children.Add(new TextBlock { Text = L.T("Serwer"), Foreground = SFg, FontWeight = FontWeights.SemiBold });
            var serwery = Lista(80); st.Children.Add(serwery);
            st.Children.Add(new TextBlock { Text = L.T("Folder na serwerze"), Foreground = SFg, FontWeight = FontWeights.SemiBold });
            var foldery = Lista(110); st.Children.Add(foldery);
            var kopiuj = new RadioButton { Content = L.T("Kopiuj (oryginał zostaje)"), Foreground = SFg, IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
            var przenies = new RadioButton { Content = L.T("Przenieś (oryginał do Kosza po sprawdzeniu kopii na serwerze)"), Foreground = SFg };
            var tryb = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) }; tryb.Children.Add(kopiuj); tryb.Children.Add(przenies); st.Children.Add(tryb);
            var info = new TextBlock { Foreground = SDim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Text = L.T("Szukam serwerów w sieci domowej…") }; st.Children.Add(info);
            var btns = new WrapPanel(); st.Children.Add(btns);
            var wyslij = SBtnMake(L.T("Wyślij"), () => { }, new LinearGradientBrush(Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x06, 0xB6, 0xD4), 0)); wyslij.IsEnabled = false;
            btns.Children.Add(wyslij); btns.Children.Add(SBtnMake(L.T("Anuluj"), () => w.Close()));
            var lista = new List<(LanPeerInfo Peer, string Nazwa, List<(string Folder, long Wolne)> F)>();
            void Pokaz()
            {
                serwery.Items.Clear(); foreach (var s in lista) serwery.Items.Add("● " + s.Nazwa);
                if (serwery.Items.Count > 0 && serwery.SelectedIndex < 0) serwery.SelectedIndex = 0;
            }
            serwery.SelectionChanged += (s, e) =>
            {
                foldery.Items.Clear(); int i = serwery.SelectedIndex; if (i < 0 || i >= lista.Count) return;
                foreach (var f in lista[i].F) foldery.Items.Add(f.Folder + "   ·   " + L.T("wolne: ") + Rozmiar(f.Wolne));
                if (foldery.Items.Count > 0) foldery.SelectedIndex = 0;
            };
            foldery.SelectionChanged += (s, e) => wyslij.IsEnabled = foldery.SelectedIndex >= 0;
            wyslij.Click += (s, e) =>
            {
                int i = serwery.SelectedIndex, j = foldery.SelectedIndex; if (i < 0 || j < 0) return;
                var cel = lista[i]; var folder = cel.F[j].Folder;
                if (cel.F[j].Wolne >= 0 && cel.F[j].Wolne < suma + (200L << 20)) { info.Text = L.T("⚠ Za mało miejsca w tym folderze – wybierz inny."); return; }
                bool move = przenies.IsChecked == true; w.Close();
                foreach (var p in pliki) { if (cel.Peer == null) KopiujLokalnie(owner, p, folder, move); else WyslijNaSerwer(owner, cel.Peer, p, folder, move); }
            };
            w.Show();
            // ten komputer - gdy działa na nim Velivo Serwer filmów
            var lok = SerwerFolderyTeraz().Where(Directory.Exists).ToList();
            if (lok.Count > 0) lista.Add((null, L.T("Ten komputer (") + Environment.MachineName + ")", lok.Select(f => (f, WolneMiejsce(f))).ToList()));
            Pokaz();
            // komputery w sieci domowej: zapytanie o foldery serwera (odpowiadają tylko te z Velivo Serwer filmów)
            var req = Guid.NewGuid().ToString("N"); _folderOdp[req] = new List<(LanPeerInfo, List<LanFilmItem>)>();
            foreach (var p in SendTargets().Where(p => !(p.Device ?? "").StartsWith("📱", StringComparison.Ordinal) && NetIdx(p.Id) == null)) SendFilmMsg(p, new LanFilmMsg { kind = "folders", req = req });
            for (int k = 0; k < 8 && w.IsVisible; k++)
            {
                await Task.Delay(500);
                List<(LanPeerInfo Peer, List<LanFilmItem> Foldery)> odp; lock (_folderOdp[req]) odp = _folderOdp[req].ToList();
                foreach (var o in odp) if (!lista.Any(x => x.Peer != null && x.Peer.Id == o.Peer.Id)) { lista.Add((o.Peer, o.Peer.Device, o.Foldery.Select(f => (f.t, f.l)).ToList())); Pokaz(); }
            }
            _folderOdp.TryRemove(req, out _);
            if (w.IsVisible) info.Text = lista.Count == 0 ? L.T("Nie znaleziono serwera filmów – włącz Velivo Serwer filmów na komputerze w sieci domowej i dodaj w nim foldery.") : L.T("Wybierz serwer i folder. Po wysłaniu serwer sam pobierze opis, plakat i obsadę.");
        }

        // ---------- 📁 co wysłać: film w swoim folderze (cały folder - napisy, opis, plakat) albo sam plik → nowy folder o nazwie filmu ----------
        // folder „filmu”: dokładnie jeden duży film (>100 MB) w środku, do 200 plików, reszta razem < 1 GB, nie Pulpit / Pobrane / Wideo / Dokumenty / katalog dysku
        static bool FolderFilmu(string dir, string plik)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || Path.GetPathRoot(dir)?.TrimEnd('\\') == dir.TrimEnd('\\')) return false;
                var prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var spec = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), prof, Path.Combine(prof, "Downloads") };
                if (spec.Any(x => string.Equals(x?.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return false;
                if (SerwerFolderyTeraz().Any(f => string.Equals(f.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return false;
                var all = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(201).ToList();
                if (all.Count > 200) return false;
                if (all.Count(f => IsVlcVideoFile(f) && !IsAudioFile(f) && new FileInfo(f).Length > 100L << 20) != 1) return false;
                return all.Where(f => !string.Equals(f, plik, StringComparison.OrdinalIgnoreCase)).Sum(f => new FileInfo(f).Length) < 1L << 30;
            }
            catch (Exception) { return false; }
        }
        // (pełna ścieżka, ścieżka w folderze serwera „Folder/plik”) + folder źródłowy (do usunięcia przy „Przenieś”, gdy cały pusty)
        static (List<(string Pelna, string Wzgl)> Pliki, string Zrodlo) PlikiFilmu(string plik)
        {
            var dir = Path.GetDirectoryName(plik) ?? ""; var nazwa = Path.GetFileNameWithoutExtension(plik);
            var l = new List<(string, string)>();
            if (FolderFilmu(dir, plik))
            {
                var cel = new DirectoryInfo(dir).Name;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".velivo-part", StringComparison.OrdinalIgnoreCase)))
                    l.Add((f, cel + "/" + Path.GetRelativePath(dir, f).Replace('\\', '/')));
                return (l, dir);
            }
            l.Add((plik, nazwa + "/" + Path.GetFileName(plik)));   // sam plik: nowy folder o nazwie filmu, obok napisy / opis / plakat o tej samej nazwie
            try { foreach (var f in Directory.EnumerateFiles(dir, nazwa + "*").Where(f => !string.Equals(f, plik, StringComparison.OrdinalIgnoreCase) && (SubtitleExts.Contains(Path.GetExtension(f).ToLowerInvariant()) || f.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(f).EndsWith("-poster", StringComparison.OrdinalIgnoreCase)))) l.Add((f, nazwa + "/" + Path.GetFileName(f))); } catch (Exception) { }
            return (l, null);
        }
        // ścieżka docelowa na serwerze: tylko wewnątrz folderu serwera, nazwy bez znaków niedozwolonych; null = taki plik (ta sama wielkość) już jest
        static string CelNaSerwerze(string folder, string wzgl, long len, out string istnieje)
        {
            istnieje = null;
            var seg = (wzgl ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(x => string.Concat(x.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim()).ToList();
            if (seg.Count == 0 || seg.Any(x => x.Length == 0 || x == "." || x == "..")) throw new IOException("zła nazwa pliku");
            var dir = Path.GetFullPath(Path.Combine(new[] { folder }.Concat(seg.Take(seg.Count - 1)).ToArray()));
            if (!(dir + "\\").StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("zła ścieżka");
            Directory.CreateDirectory(dir);
            var cel = WolnaNazwa(dir, seg.Last(), len);
            if (cel == null) istnieje = Path.Combine(dir, seg.Last());
            return cel;
        }

        // 📁 porządek w folderach serwera: film z katalogu głównego folderu → „Folder/Nazwa filmu/” (napisy, .nfo, plakat o tej samej nazwie też);
        //    biblioteka dostaje nowe ścieżki (postęp, ulubione zostają; informacje o filmie są po nazwie pliku - też zostają)
        // gotowy - który plik wolno już przenieść (porządek sam: skopiowany do końca); null - wszystkie (przycisk „📁 Uporządkuj w foldery”)
        async Task<(int Ok, int Pominiete)> SerwerUporzadkuj(Func<string, bool> gotowy = null)
        {
            int ok = 0, pom = 0; var zmiany = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); string wznowCel = null; long wznow = 0;
            foreach (var folder in SerwerFoldery().Where(Directory.Exists))
                foreach (var film in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).Where(f => IsVlcVideoFile(f) && !IsAudioFile(f)).ToList())
                {
                    if (gotowy != null && !gotowy(film)) continue;
                    var nazwa = Path.GetFileNameWithoutExtension(film); var odc = Odcinek(film);
                    var dir = odc != null ? Path.Combine(folder, odc.Value.Serial, "Sezon " + odc.Value.Sezon.ToString("00")) : Path.Combine(folder, nazwa.Trim().TrimEnd('.'));   // 📺 odcinek serialu: Serial\\Sezon 02
                    var razem = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).Where(f => !string.Equals(f, film, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f).StartsWith(nazwa, StringComparison.OrdinalIgnoreCase)
                        && (SubtitleExts.Contains(Path.GetExtension(f).ToLowerInvariant()) || f.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(f).EndsWith("-poster", StringComparison.OrdinalIgnoreCase))).ToList();
                    try
                    {
                        Directory.CreateDirectory(dir);
                        var cel = Path.Combine(dir, Path.GetFileName(film)); if (File.Exists(cel)) { pom++; continue; }
                        if (_vlcWin != null && _vlcPilotState != null && _playlist.Count > _playIndex && string.Equals(_playlist[_playIndex], film, StringComparison.OrdinalIgnoreCase))
                        {   // ▶ ten film gra tutaj: miejsce zapamiętane, zatrzymanie, przeniesienie, potem dalej z nowego miejsca
                            try { wznow = Convert.ToInt64(((Dictionary<string, object>)_vlcPilotState())["pos"]); } catch (Exception) { wznow = 0; }
                            ShowToast(L.T("🗄 Trwa przenoszenie – film zostanie wznowiony z nowej lokalizacji (z serwera), od tego samego miejsca."), null);
                            _vlcPilotCmd?.Invoke("stop", null, 0); await Task.Delay(1500); wznowCel = cel;
                        }
                        File.Move(film, cel); zmiany[film] = cel; ok++;
                        foreach (var sh in _filmShares.Values.Where(x => string.Equals(x.Path, film, StringComparison.OrdinalIgnoreCase))) sh.Path = cel;   // film oglądany teraz na innym komputerze - przewijanie dalej działa (nowa ścieżka)
                        foreach (var f in razem) try { var c = Path.Combine(dir, Path.GetFileName(f)); if (!File.Exists(c)) File.Move(f, c); } catch (Exception) { }
                    }
                    catch (Exception ex) { pom++; App.LogInfo("Serwer filmów – porządek: " + film + " – " + ex.Message); }   // film otwarty (gra) - zostaje
                }
            if (zmiany.Count > 0) { var l = LoadLibrary(); foreach (var v in l) if (zmiany.TryGetValue(v.Path, out var nowa)) v.Path = nowa; SaveLibrary(l); _sharedCache = null; _libRefresh?.Invoke(); }
            if (wznowCel != null && File.Exists(wznowCel)) { PlayVideos(new[] { wznowCel }, 0, false); SkokPo(wznow); }
            return (ok, pom);
        }

        // ---------- postęp wysyłania (małe okienko, można dalej oglądać) ----------
        sealed class Wysylka
        {
            public Window W; public TextBlock Txt; public ProgressBar Pas; public bool Move; public DateTime Start = DateTime.UtcNow; public string Zrodlo; public long Suma; public int Zostalo;
            public readonly ConcurrentDictionary<string, long> Poz = new ConcurrentDictionary<string, long>();
            public readonly ConcurrentDictionary<string, string> Sumy = new ConcurrentDictionary<string, string>();   // plik tutaj → suma pliku na serwerze
            public string Blad; public LanPeerInfo Peer;
            public readonly ConcurrentDictionary<string, string> Cele = new ConcurrentDictionary<string, string>();   // plik tutaj → jego ścieżka na serwerze
        }
        readonly ConcurrentDictionary<string, (Wysylka G, string Plik)> _wysylki = new ConcurrentDictionary<string, (Wysylka, string)>();

        Wysylka OknoPostepu(Window owner, string tytul, bool move, string zrodlo, long suma, int n)
        {
            var st = new StackPanel { Margin = new Thickness(14) };
            var txt = new TextBlock { Foreground = SDim, TextWrapping = TextWrapping.Wrap, Text = L.T("Łączę z serwerem…") };
            var pas = new ProgressBar { Height = 8, Maximum = 1000, Margin = new Thickness(0, 8, 0, 8), Foreground = new LinearGradientBrush(Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x06, 0xB6, 0xD4), 0), Background = SCard, BorderThickness = new Thickness(0) };
            st.Children.Add(new TextBlock { Text = tytul, Foreground = SFg, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }); st.Children.Add(pas); st.Children.Add(txt);
            var w = new Window { Title = L.T("🗄 Wysyłanie na serwer"), Width = 440, SizeToContent = SizeToContent.Height, Background = SBg, Content = st, Owner = owner != null && owner.IsLoaded ? owner : null, Topmost = owner?.Topmost ?? false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            w.Show();
            return new Wysylka { W = w, Txt = txt, Pas = pas, Move = move, Zrodlo = zrodlo, Suma = Math.Max(1, suma), Zostalo = n };
        }

        void Postep(Wysylka u)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                long pos = Math.Min(u.Suma, u.Poz.Values.Sum()), len = u.Suma; u.Pas.Value = 1000.0 * pos / len;
                double s = (DateTime.UtcNow - u.Start).TotalSeconds, v = s > 1 ? pos / s : 0;
                u.Txt.Text = Rozmiar(pos) + L.T(" z ") + Rozmiar(len) + (v > 0 ? " · " + (v / (1 << 20)).ToString("0") + " MB/s · " + L.T("zostało ok. ") + TimeSpan.FromSeconds(Math.Max(0, (len - pos) / v)).ToString(@"m\:ss") : "") + L.T(" · można dalej oglądać");
            }));
        }

        // jeden plik gotowy (albo nie); po ostatnim - wynik i przy „Przenieś” oryginały do Kosza (każdy dopiero po zgodnej sumie)
        void PlikGotowy(Wysylka u, string plik, bool ok, string sumaNaSerwerze, string blad, string koniecTxt, string celNaSerwerze = null)
        {
            if (ok) { u.Sumy[plik] = sumaNaSerwerze ?? ""; if (celNaSerwerze != null) u.Cele[plik] = celNaSerwerze; } else u.Blad ??= blad;
            if (Interlocked.Decrement(ref u.Zostalo) > 0) return;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                bool dobrze = u.Blad == null, showWznow = false; var tekst = dobrze ? koniecTxt : L.T("✖ Nie udało się: ") + u.Blad;
                u.Pas.Value = dobrze ? 1000 : u.Pas.Value; u.Txt.Text = tekst; u.Txt.Foreground = dobrze ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)) : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                if (dobrze && u.Move)
                {
                    u.Txt.Text = tekst + "\n" + L.T("Sprawdzam kopię na serwerze…");
                    int zostaly = 0; long wznow = -1; string wznowCel = null;
                    foreach (var kv in u.Sumy)
                    {
                        var moj = await Task.Run(() => SumaPliku(kv.Key));
                        if (moj == null || !string.Equals(moj, kv.Value, StringComparison.OrdinalIgnoreCase)) { zostaly++; continue; }
                        // ▶ ten film właśnie gra: zapamiętane miejsce, zatrzymanie (odtwarzacz zwalnia plik), potem dalej już z serwera
                        if (_vlcWin != null && _vlcPilotState != null && _playlist.Count > _playIndex && string.Equals(_playlist[_playIndex], kv.Key, StringComparison.OrdinalIgnoreCase) && u.Cele.TryGetValue(kv.Key, out var c0))
                        { try { wznow = Convert.ToInt64(((Dictionary<string, object>)_vlcPilotState())["pos"]); } catch (Exception) { wznow = 0; } wznowCel = c0; ShowToast(L.T("🗄 Trwa przenoszenie – film zostanie wznowiony z nowej lokalizacji (z serwera), od tego samego miejsca."), null); _vlcPilotCmd?.Invoke("stop", null, 0); await Task.Delay(1500); }
                        bool usun = false;
                        for (int k = 0; k < 5 && !usun; k++) { try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(kv.Key, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); usun = true; } catch (Exception) { await Task.Delay(1000); } }
                        if (!usun) zostaly++;
                    }
                    if (wznowCel != null)
                    {   // dalej od tego samego miejsca: ten komputer - wprost z folderu serwera; inny komputer - serwer udostępnia film jak „Odtwórz na”
                        if (u.Peer == null) { PlayVideos(new[] { wznowCel }, 0, false); SkokPo(wznow); }
                        else { _skokPo = (FilmId(wznowCel), wznow); SendFilmMsg(u.Peer, new LanFilmMsg { kind = "want", id = FilmId(wznowCel) }); }
                        showWznow = true;
                    }
                    try { if (u.Zrodlo != null && Directory.Exists(u.Zrodlo) && !Directory.EnumerateFiles(u.Zrodlo, "*", SearchOption.AllDirectories).Any()) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(u.Zrodlo, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); } catch (Exception) { }
                    u.Txt.Text = tekst + "\n" + (zostaly == 0 ? L.T("🗑 Oryginał przeniesiony do Kosza.") : L.T("⚠ Kopia na serwerze różni się od oryginału – oryginał zostaje.")) + (showWznow ? "\n" + L.T("▶ Oglądasz dalej – już z serwera, od tego samego miejsca.") : "");
                }
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(dobrze ? 15 : 60) }; t.Tick += (s, e) => { t.Stop(); try { u.W.Close(); } catch (Exception) { } }; t.Start();
            }));
        }

        static string SumaPliku(string p)
        {
            try { using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan)) using (var sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(fs)); }
            catch (Exception) { return null; }
        }

        // ten komputer: kopia do folderu serwera z postępem, potem przegląd folderów przez Serwer filmów
        void KopiujLokalnie(Window owner, string plik, string folder, bool move)
        {
            var (pliki, zrodlo) = PlikiFilmu(plik);
            long suma = pliki.Sum(x => new FileInfo(x.Pelna).Length);
            var u = OknoPostepu(owner, pliki[0].Wzgl.Split('/')[0] + " → " + folder, move, zrodlo, suma, pliki.Count);
            Task.Run(() =>
            {
                foreach (var (pelna, wzgl) in pliki)
                {
                    try
                    {
                        long len = new FileInfo(pelna).Length;
                        var cel = CelNaSerwerze(folder, wzgl, len, out var ist);
                        if (cel == null) { u.Poz[pelna] = len; Postep(u); PlikGotowy(u, pelna, true, u.Move ? SumaPliku(ist) : null, null, L.T("✔ Gotowe – Serwer filmów pobiera opis, plakat i obsadę."), ist); continue; }
                        var part = cel + ".velivo-part"; long pos = 0; var buf = new byte[1 << 20]; var last = DateTime.MinValue;
                        using (var a = new FileStream(pelna, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20)) using (var b = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                        { int n; while ((n = a.Read(buf, 0, buf.Length)) > 0) { b.Write(buf, 0, n); pos += n; u.Poz[pelna] = pos; if ((DateTime.UtcNow - last).TotalMilliseconds > 500) { last = DateTime.UtcNow; Postep(u); } } }
                        File.Move(part, cel);
                        PlikGotowy(u, pelna, true, u.Move ? SumaPliku(cel) : null, null, L.T("✔ Gotowe – Serwer filmów pobiera opis, plakat i obsadę."), cel);
                    }
                    catch (Exception ex) { PlikGotowy(u, pelna, false, null, Path.GetFileName(pelna) + ": " + ex.Message, null); }
                }
                SerwerPrzegladnij();
            });
        }

        // nazwa w folderze: ten sam plik (ta sama wielkość) = już jest (null); inny o tej samej nazwie - „(2)”
        static string WolnaNazwa(string folder, string nazwa, long len)
        {
            nazwa = string.Concat(Path.GetFileName(nazwa ?? "film").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var cel = Path.Combine(folder, nazwa);
            for (int i = 2; File.Exists(cel); i++)
            {
                if (new FileInfo(cel).Length == len) return null;
                cel = Path.Combine(folder, Path.GetFileNameWithoutExtension(nazwa) + " (" + i + ")" + Path.GetExtension(nazwa));
            }
            return cel;
        }

        // komputer w sieci domowej: serwer pobiera każdy plik z tego komputera (udostępniony tylko jemu, 12 h, z wznawianiem)
        void WyslijNaSerwer(Window owner, LanPeerInfo peer, string plik, string folder, bool move)
        {
            var (pliki, zrodlo) = PlikiFilmu(plik);
            var wpisy = pliki.Select(x => (x.Pelna, x.Wzgl, Url: ShareFilm(x.Pelna, peer))).ToList();
            if (wpisy.Any(x => x.Url == null)) { ShowToast(L.T("🗄 Nie udało się udostępnić filmu serwerowi (sieć / port)"), null); return; }
            var u = OknoPostepu(owner, pliki[0].Wzgl.Split('/')[0] + " → " + peer.Device + " · " + folder, move, zrodlo, pliki.Sum(x => new FileInfo(x.Pelna).Length), pliki.Count); u.Peer = peer;
            var ids = new List<string>();
            foreach (var x in wpisy)
            {
                var id = Guid.NewGuid().ToString("N"); ids.Add(id); _wysylki[id] = (u, x.Pelna);
                SendFilmMsg(peer, new LanFilmMsg { kind = "upload", req = id, url = x.Url, act = folder, title = x.Wzgl, len = new FileInfo(x.Pelna).Length });
            }
            u.W.Closed += (s, e) => { foreach (var id in ids) _wysylki.TryRemove(id, out _); };
            Task.Delay(20000).ContinueWith(_ => { if (u.Poz.IsEmpty && u.Zostalo > 0) Dispatcher.BeginInvoke(new Action(() => { u.Txt.Text = L.T("✖ Serwer nie odpowiada – sprawdź, czy ma najnowsze Velivo i działa Serwer filmów."); u.Txt.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); })); });
        }

        // ▶ po przeniesieniu: przewinięcie do zapamiętanego miejsca, gdy film z serwera już gra
        (string Id, long Pos) _skokPo;
        internal void SkokPo(long pos) { if (pos <= 0) return; Task.Delay(3000).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => _vlcPilotCmd?.Invoke("seek", null, pos)))); }

        // ---------- odbiór (LanVideo): foldery serwera, pobieranie pliku, postęp i wynik ----------
        void FilmSerwerMsg(LanFilmMsg m, LanPeerInfo peer)
        {
            switch (m.kind)
            {
                case "folders":
                    if (m.items != null) { if (m.req != null && _folderOdp.TryGetValue(m.req, out var lst)) lock (lst) lst.Add((peer, m.items)); return; }   // odpowiedź serwera
                    var fol = SerwerFolderyTeraz().Where(Directory.Exists).ToList();
                    if (fol.Count > 0) SendFilmMsg(peer, new LanFilmMsg { kind = "folders", req = m.req, items = fol.Select(f => new LanFilmItem { t = f, l = WolneMiejsce(f) }).ToList() });
                    return;
                case "upload":
                    _ = Task.Run(() => PobierzOdKlienta(peer, m));
                    return;
                case "upst":
                    if (m.req != null && _wysylki.TryGetValue(m.req, out var u)) { u.G.Poz[u.Plik] = m.pos; Postep(u.G); }
                    return;
                case "upok":
                    if (m.req != null && _wysylki.TryRemove(m.req, out var u2))   // raz (pakiet przychodzi kilkoma drogami)
                    { if (m.done) { u2.G.Poz[u2.Plik] = new FileInfo(u2.Plik).Length; Postep(u2.G); } PlikGotowy(u2.G, u2.Plik, m.done, m.done ? m.title : null, Path.GetFileName(u2.Plik) + ": " + m.title, L.T("✔ Gotowe – ") + peer.Device + L.T(" pobiera opis, plakat i obsadę."), m.done ? m.act : null); }
                    return;
            }
        }

        static readonly System.Net.Http.HttpClient _pobHttp = new System.Net.Http.HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        async Task PobierzOdKlienta(LanPeerInfo peer, LanFilmMsg m)
        {
            void Odp(bool ok, string tekst, string sciezka = null) => SendFilmMsg(peer, new LanFilmMsg { kind = "upok", req = m.req, done = ok, title = tekst, act = sciezka });
            try
            {
                var folder = SerwerFolderyTeraz().FirstOrDefault(f => string.Equals(f.TrimEnd('\\'), (m.act ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                if (folder == null || !Directory.Exists(folder)) { Odp(false, L.T("to nie jest folder tego serwera")); return; }
                if (m.url == null || !m.url.StartsWith("http://")) { Odp(false, L.T("zły adres")); return; }
                var cel = CelNaSerwerze(folder, m.title, m.len, out var ist);   // „Folder filmu/plik” - zawsze wewnątrz folderu serwera
                if (cel == null) { Odp(true, SumaPliku(ist) ?? "", ist); return; }   // już jest
                var part = cel + ".velivo-part";
                App.LogInfo("Serwer filmów: pobieram od " + peer.Device + " – " + Path.GetFileName(cel) + " (" + Rozmiar(m.len) + ")");
                var last = DateTime.MinValue; var buf = new byte[1 << 20];
                for (int proba = 0; proba < 40; proba++)   // przerwa w sieci - wznowienie od miejsca przerwania (do ok. 10 min)
                {
                    long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                    if (m.len > 0 && have >= m.len) break;
                    try
                    {
                        using (var cts = new CancellationTokenSource(60000))
                        using (var rq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, m.url))
                        {
                            if (have > 0) rq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                            using (var rs = await _pobHttp.SendAsync(rq, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token))
                            {
                                if (!rs.IsSuccessStatusCode) throw new IOException("HTTP " + (int)rs.StatusCode);
                                if (have > 0 && rs.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0;   // bez wznawiania - od nowa
                                using (var src = await rs.Content.ReadAsStreamAsync(cts.Token))
                                using (var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
                                {
                                    int n; long pos = have;
                                    while ((n = await src.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                                    {
                                        await dst.WriteAsync(buf, 0, n); pos += n; cts.CancelAfter(60000);   // 60 s bez danych = przerwa
                                        if ((DateTime.UtcNow - last).TotalSeconds >= 1) { last = DateTime.UtcNow; SendFilmMsg(peer, new LanFilmMsg { kind = "upst", req = m.req, pos = pos, len = m.len }); }
                                    }
                                }
                            }
                        }
                        if (m.len <= 0 || new FileInfo(part).Length >= m.len) break;
                    }
                    catch (Exception ex) { App.LogInfo("Serwer filmów: przerwa przy pobieraniu " + Path.GetFileName(cel) + " – " + ex.Message + " (ponawiam)"); await Task.Delay(15000); }
                }
                if (!File.Exists(part) || m.len > 0 && new FileInfo(part).Length != m.len) { Odp(false, L.T("przerwane – plik niepełny (spróbuj ponownie)")); return; }
                File.Move(part, cel);
                var hash = SumaPliku(cel);
                App.LogInfo("Serwer filmów: pobrano od " + peer.Device + " – " + cel);
                if (IsVlcVideoFile(cel) && !IsAudioFile(cel)) Dispatcher.Invoke(() => { var l = LoadLibrary(); if (AddToLibrary(l, new[] { cel }, true) > 0) { SaveLibrary(l); _sharedCache = null; _libRefresh?.Invoke(); } });   // od razu w bibliotece serwera - nadawca może oglądać dalej z serwera
                Odp(true, hash ?? "", cel);
                SerwerPrzegladnij();
            }
            catch (Exception ex) { App.LogError(ex); Odp(false, ex.Message); }
        }

        // przegląd folderów serwera (nowy plik → biblioteka, opis, plakat): ten program jest Serwerem - od razu; inaczej sygnał do Velivo Serwer filmów
        void SerwerPrzegladnij()
        {
            if (Wariant.Serwer) { Dispatcher.BeginInvoke(new Action(() => SerwerSkanuj(false))); return; }
            try { if (EventWaitHandle.TryOpenExisting("Velivo.SerwerSkanuj", out var ev)) using (ev) ev.Set(); } catch (Exception) { }
        }
        // Velivo Serwer filmów czeka na sygnał „przejrzyj foldery” od innych programów Velivo na tym komputerze
        void SerwerSygnalStart()
        {
            if (!Wariant.Serwer) return;
            try
            {
                var ev = new EventWaitHandle(false, EventResetMode.AutoReset, "Velivo.SerwerSkanuj");
                new Thread(() => { while (true) { ev.WaitOne(); Dispatcher.BeginInvoke(new Action(() => SerwerSkanuj(false))); } }) { IsBackground = true }.Start();
            }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
