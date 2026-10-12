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
        internal static int AddToLibrary(List<LibraryVideo> lib, IEnumerable<string> paths)
        {
            int added = 0;
            var known = new HashSet<string>(lib.Select(v => v.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                IEnumerable<string> files;
                try { files = Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(IsVlcVideoFile) : new[] { p }; }
                catch (Exception) { continue; }
                foreach (var f in files)
                {
                    if (!IsVlcVideoFile(f)) continue;
                    var full = System.IO.Path.GetFullPath(f);
                    if (!known.Add(full)) continue;
                    lib.Add(new LibraryVideo { Path = full, Title = System.IO.Path.GetFileNameWithoutExtension(full), Added = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
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
            item("\uE8E5", L.T("Otwórz film z dysku…"), () => { var f = PickVideoFiles(false); if (f != null) PlayVideos(f, 0); });
            menu.Items.Add(new Separator());
            item("\uE710", L.T("Dodaj filmy do biblioteki…"), () => { var f = PickVideoFiles(true); if (f != null) AddPathsToLibrary(f); });
            item("\uE8F4", L.T("Dodaj folder z filmami do biblioteki…"), () => { var d = PickFolder(); if (d != null) AddPathsToLibrary(new[] { d }); });
            menu.Items.Add(new Separator());
            item("\uE74D", L.T("Odinstaluj odtwarzacz filmów…"), UninstallVlc);
            _filmBtn.ContextMenu = menu;
            int bank = TabBarPanel.Children.IndexOf(_bankBtn);
            TabBarPanel.Children.Insert(bank >= 0 ? bank + 1 : 1, _filmBtn);   // dock z prawej: nastepny w kolejnosci stoi po lewej od 🏦
        }

        string[] PickVideoFiles(bool multi)
        {
            var exts = string.Join(";", VideoExts.Concat(VlcExtraExts).Select(x => "*" + x));
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("Wybierz film"), Multiselect = multi, Filter = L.T("Filmy") + "|" + exts + "|" + L.T("Wszystkie pliki") + "|*.*" };
            return dlg.ShowDialog(this) == true ? dlg.FileNames : null;
        }

        string PickFolder()
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Wybierz folder z filmami") };
            return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        }

        // skanowanie w tle - folder na dysku sieciowym (NAS, \\serwer\Filmy, Z:) moze sie czytac dlugo
        async void AddPathsToLibrary(IEnumerable<string> paths)
        {
            var list = paths.ToList();
            ShowToast(L.T("🎬 Szukam filmów…"), null);
            var found = await System.Threading.Tasks.Task.Run(() => { var tmp = new List<LibraryVideo>(); AddToLibrary(tmp, list); return tmp; });
            var lib = LoadLibrary();
            int n = AddToLibrary(lib, found.Select(v => v.Path));
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

        // ---------- okno biblioteki ----------

        Window _libWin;
        Action _libRefresh;

        sealed class LibRow
        {
            public string Title { get; set; } public string Length { get; set; } public string Seen { get; set; } public string LastPlayed { get; set; } public string Folder { get; set; }
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
        internal static IEnumerable<LibraryVideo> SortLibrary(IEnumerable<LibraryVideo> list, string key, bool desc)
        {
            Func<LibraryVideo, IComparable> by;
            switch (key)
            {
                case "title": by = v => v.Title.ToLowerInvariant(); break;
                case "len": by = v => v.Len; break;
                case "seen": by = v => SeenFraction(v); break;
                case "folder": by = v => (System.IO.Path.GetDirectoryName(v.Path) ?? "").ToLowerInvariant(); break;
                default: by = v => v.Last; break;
            }
            var sorted = desc ? list.OrderByDescending(by) : list.OrderBy(by);
            return sorted.ThenBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase);
        }

        void ShowVideoLibrary()
        {
            if (_libWin != null && _libWin.IsLoaded) { _libWin.Activate(); return; }
            var win = new Window { Title = L.T("🎬 Biblioteka filmów"), Width = 860, Height = 540, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var search = new TextBox { Margin = new Thickness(8, 8, 8, 4), Padding = new Thickness(4) };
            var hint = new TextBlock { Text = L.T("🔍 Szukaj filmu…"), Foreground = Brushes.Gray, Margin = new Thickness(14, 13, 0, 0), IsHitTestVisible = false };
            search.TextChanged += (s, a) => hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var top = new Grid(); top.Children.Add(search); top.Children.Add(hint);
            var grid = new GridView();
            // kolumny: naglowek klik = sortuj, drugi klik = odwrotnie
            var cols = new[] { new[] { "title", "Tytuł", "Title", "340" }, new[] { "len", "Długość", "Length", "80" }, new[] { "seen", "Obejrzane", "Seen", "86" },
                new[] { "last", "Ostatnio", "LastPlayed", "110" }, new[] { "folder", "Folder", "Folder", "280" } };
            string sortKey = "last"; bool sortDesc = true;
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
            var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, Margin = new Thickness(10, 0, 0, 0) };
            List<LibraryVideo> lib = LoadLibrary();
            List<LibRow> rows = new List<LibRow>();
            int fillVer = 0;
            Action fill = async () =>
            {
                int ver = ++fillVer;
                lib = LoadLibrary();
                var q = search.Text.Trim();
                var all = lib; var key = sortKey; var desc = sortDesc;
                // czy plik jest na dysku - w tle: wylaczony NAS / odlaczony dysk sieciowy odpowiada dopiero po kilku sekundach
                var newRows = await System.Threading.Tasks.Task.Run(() =>
                {
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
                    return SortLibrary(all.Where(v => q.Length == 0 || v.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || v.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0), key, desc)
                    .Select(v => new LibRow
                    {
                        Item = v, Missing = !exists(v.Path), Title = v.Title,
                        Length = v.Len > 0 ? FormatTime(v.Len) : "",
                        Seen = SeenText(v),
                        LastPlayed = LastPlayedText(v.Last, now),
                        Folder = System.IO.Path.GetDirectoryName(v.Path),
                    }).Select(r => { if (r.Missing) r.Title = "⚠ " + r.Title; return r; }).ToList();
                });
                if (ver != fillVer) return;   // w miedzyczasie nowsze szukanie
                rows = newRows;
                list.ItemsSource = rows;
                count.Text = rows.Count + " / " + lib.Count + L.T(" filmów");
            };
            _libRefresh = fill;
            list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((s, a) =>
            {
                if (!(a.OriginalSource is GridViewColumnHeader h) || !(h.Tag is string k)) return;
                if (k == sortKey) sortDesc = !sortDesc;
                else { sortKey = k; sortDesc = k == "last" || k == "len" || k == "seen"; }   // daty i liczby: najpierw najwieksze
                markSort(); fill();
            }));
            search.TextChanged += (s, a) => fill();
            fill();
            Func<List<LibRow>> sel = () => list.SelectedItems.Cast<LibRow>().ToList();
            Action play = () =>
            {
                var r = list.SelectedItem as LibRow; if (r == null) return;
                if (r.Missing) { MessageBox.Show(win, L.T("Nie ma już tego pliku na dysku (przeniesiony albo usunięty)."), L.T("Biblioteka filmów")); return; }
                var playable = rows.Where(x => !x.Missing).Select(x => x.Item.Path).ToList();   // lista odtwarzania = widoczna lista
                PlayVideos(playable.ToArray(), playable.IndexOf(r.Item.Path));
            };
            Action remove = () =>
            {
                var rs = sel(); if (rs.Count == 0) return;
                var l = LoadLibrary(); l.RemoveAll(v => rs.Any(r => string.Equals(r.Item.Path, v.Path, StringComparison.OrdinalIgnoreCase)));
                SaveLibrary(l); fill();
            };
            list.MouseDoubleClick += (s, a) => play();
            list.PreviewKeyDown += (s, a) =>
            {
                if (a.Key == Key.Enter) { play(); a.Handled = true; }
                else if (a.Key == Key.Delete) { remove(); a.Handled = true; }
            };
            var menu = new ContextMenu();
            foreach (var (text, act) in new (string, Action)[] {
                (L.T("▶ Odtwórz"), play), (L.T("Pokaż w folderze"), () => { var r = list.SelectedItem as LibRow; if (r != null && !r.Missing) ShowInFolder(r.Item.Path); }),
                (L.T("Obejrzyj od początku"), () => { foreach (var r in sel()) r.Item.Pos = 0; var l = LoadLibrary(); foreach (var v in l) if (sel().Any(r => r.Item.Path == v.Path)) v.Pos = 0; SaveLibrary(l); play(); }),
                (L.T("Usuń z biblioteki (plik zostaje na dysku)"), remove) })
            {
                var mi = new MenuItem { Header = text }; var act0 = act; mi.Click += (s, a) => act0(); menu.Items.Add(mi);
            }
            list.ContextMenu = menu;
            var bar = new DockPanel { Margin = new Thickness(6), LastChildFill = false };
            DockPanel.SetDock(count, Dock.Left); bar.Children.Add(count);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right); bar.Children.Add(buttons);
            buttons.Children.Add(SmallButton(L.T("▶ Odtwórz"), play));
            buttons.Children.Add(SmallButton(L.T("➕ Dodaj filmy…"), () => { var f = PickVideoFiles(true); if (f != null) AddPathsToLibrary(f); }));
            buttons.Children.Add(SmallButton(L.T("📁 Dodaj folder…"), () => { var d = PickFolder(); if (d != null) AddPathsToLibrary(new[] { d }); }));
            buttons.Children.Add(SmallButton(L.T("Usuń z biblioteki"), remove));
            var body = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); body.Children.Add(top);
            DockPanel.SetDock(bar, Dock.Bottom); body.Children.Add(bar);
            if (lib.Count == 0)
            {
                var empty = new TextBlock { Text = L.T("Biblioteka jest pusta. Dodaj filmy albo cały folder (np. Wideo) – przyciski na dole."), Margin = new Thickness(12, 4, 12, 4), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
                DockPanel.SetDock(empty, Dock.Top); body.Children.Add(empty);
            }
            body.Children.Add(list);
            win.Content = body;
            // przeciagnij pliki / foldery z Eksploratora na okno = dodaj do biblioteki
            win.AllowDrop = true;
            win.Drop += (s, a) => { if (a.Data.GetData(DataFormats.FileDrop) is string[] f) AddPathsToLibrary(f); };
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
            if (AddToLibrary(lib, files) > 0) { SaveLibrary(lib); _libRefresh?.Invoke(); }
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
                    var l = LoadLibrary();
                    var v = l.FirstOrDefault(x => string.Equals(x.Path, current.Path, StringComparison.OrdinalIgnoreCase));
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
            Action<string> loadSubs = path =>
            {
                lc.Cues = new List<SubCue>(); lc.File = null; lc.LastSpoken = -1;
                if (path == null) return;
                try { lc.Cues = ParseSubtitles(ReadSubtitleFile(path)); lc.Lang = SubtitleLanguage(lc.Cues); lc.File = path; }
                catch (Exception ex) { App.LogError(ex); }
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
                var l = LoadLibrary(); var v = l.FirstOrDefault(x => string.Equals(x.Path, current.Path, StringComparison.OrdinalIgnoreCase));
                if (v == null) return;
                v.Vol = _settings.VlcVolume; v.LVol = _settings.VlcLectorVolume; v.LRate = _settings.VlcLectorRate; v.LDuck = _settings.VlcLectorDuck; v.LVoice = _settings.VlcLectorVoice;
                current.Vol = v.Vol; current.LVol = v.LVol; current.LRate = v.LRate; current.LDuck = v.LDuck; current.LVoice = v.LVoice;
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
                mp.Volume = (int)vol.Value;   // takze gdy suwak juz stal na tej wartosci (bez zdarzenia zmiany)
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
            lectorTimer.Tick += (s, e) => { if (_settings.VlcLector) LectorTick(lc, mp, () => (int)vol.Value); };
            lectorTimer.Start();
            // pamietanie napisow i sciezki dzwieku: dla tego filmu (biblioteka) i jako ostatni wybor dla kolejnych filmow
            Action<bool, string> rememberTrack = (spu, name) =>
            {
                if (spu) _settings.VlcSpu = name; else _settings.VlcAudio = name;
                try { _settings.Save(DataDir); } catch (Exception) { }
                if (current == null) return;
                var l = LoadLibrary(); var v = l.FirstOrDefault(x => string.Equals(x.Path, current.Path, StringComparison.OrdinalIgnoreCase));
                if (v == null) return;
                if (spu) v.Spu = name; else v.Audio = name;
                SaveLibrary(l);
            };
            Action applyTracks = () =>
            {
                try
                {
                    var spu = PickTrack(mp.SpuDescription.Select(t => (t.Id, t.Name)), current?.Spu ?? _settings.VlcSpu);
                    if (spu != int.MinValue) mp.SetSpu(spu);
                    var au = PickTrack(mp.AudioTrackDescription.Select(t => (t.Id, t.Name)), current?.Audio ?? _settings.VlcAudio);
                    if (au != int.MinValue && au >= 0) mp.SetAudioTrack(au);
                }
                catch (Exception ex) { App.LogError(ex); }
            };
            _vlcLoad = i =>
            {
                savePos();
                if (i < 0 || i >= _playlist.Count) return;
                _playIndex = i;
                var path = _playlist[i];
                current = LoadLibrary().FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                win.Title = System.IO.Path.GetFileName(path) + " – " + L.T("Odtwarzacz filmów");
                LectorStopVoice(lc, mp); loadSubs(FindSubtitleFor(path));
                applyFilmPrefs();
                miniTitle.Text = System.IO.Path.GetFileNameWithoutExtension(path);
                using (var media = new Media(_libVlc, path, FromType.FromPath))
                {
                    // napisy o tej samej nazwie obok filmu (film.srt, film.pl.srt) dolaczane same - jak w VLC
                    mp.Play(media);
                }
                long resume = current != null ? ResumeAt(current.Pos, current.Len) : 0;
                if (resume > 0)
                {
                    long r = resume;
                    var once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    once.Tick += (s, e) => { once.Stop(); if (mp.Length > 0) { mp.Time = r; showOsd(L.T("Wznowiono od ") + FormatTime(r)); } };
                    once.Start();
                }
                // sciezki filmu znane chwile po starcie - wtedy ustawiamy zapamietane napisy i dzwiek
                var tracksTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                tracksTimer.Tick += (s, e) => { tracksTimer.Stop(); applyTracks(); mp.Volume = (int)vol.Value; };   // VLC przyjmuje glosnosc pewnie dopiero po starcie filmu
                tracksTimer.Start();
                speedIdx = 3; speed.Content = "1×";
                prev.IsEnabled = i > 0; next.IsEnabled = i < _playlist.Count - 1;
            };

            mp.LengthChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() => timeLen.Text = FormatTime(e.Length)));
            mp.TimeChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                timeNow.Text = FormatTime(e.Time);
                if (!seeking && mp.Length > 0) seek.Value = 1000.0 * e.Time / mp.Length;
            }));
            mp.Playing += (s, e) => Dispatcher.BeginInvoke(new Action(() => play.Content = PlayerIcon("pause")));
            mp.Paused += (s, e) => Dispatcher.BeginInvoke(new Action(() => play.Content = PlayerIcon("play")));
            mp.Stopped += (s, e) => Dispatcher.BeginInvoke(new Action(() => { play.Content = PlayerIcon("play"); seek.Value = 0; timeNow.Text = "0:00"; }));
            mp.EndReached += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (current != null) { var l = LoadLibrary(); var v = l.FirstOrDefault(x => x.Path == current.Path); if (v != null) { v.Last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); v.Done = true; v.Pos = 0; SaveLibrary(l); _libRefresh?.Invoke(); } current = null; }
                if (_playIndex < _playlist.Count - 1) _vlcLoad(_playIndex + 1);   // nastepny z listy (VLC nie moze startowac w swoim watku zdarzen)
            }));
            mp.EncounteredError += (s, e) => Dispatcher.BeginInvoke(new Action(() => showOsd(L.T("Nie da się odtworzyć tego pliku"))));

            Action togglePlay = () => { if (mp.IsPlaying) mp.SetPause(true); else if (mp.State == VLCState.Stopped || mp.State == VLCState.Ended || mp.State == VLCState.NothingSpecial) _vlcLoad(_playIndex); else mp.SetPause(false); };
            Action<long> jump = d => { if (mp.Length <= 0) return; mp.Time = Math.Max(0, Math.Min(mp.Length - 500, mp.Time + d)); showOsd((d > 0 ? "+" : "−") + Math.Abs(d / 1000) + " s"); };
            Action<double> setVol = v => { vol.Value = Math.Max(0, Math.Min(125, v)); };
            vol.ValueChanged += (s, e) => { _settings.VlcVolume = (int)vol.Value; filmPrefsChanged(); mp.Volume = (int)vol.Value; volText.Text = (int)vol.Value + "%"; if (mp.Mute && vol.Value > 0) mp.Mute = false; mute.Content = PlayerIcon(mp.Mute || vol.Value == 0 ? "mute" : "volume"); };
            Action toggleMute = () => { mp.Mute = !mp.Mute; mute.Content = PlayerIcon(mp.Mute ? "mute" : "volume"); showOsd(mp.Mute ? L.T("Wyciszono") : (int)vol.Value + "%"); };
            Action<int> setSpeed = d => { speedIdx = Math.Max(0, Math.Min(speeds.Length - 1, speedIdx + d)); mp.SetRate(speeds[speedIdx]); speed.Content = speeds[speedIdx].ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×"; showOsd(L.T("Prędkość ") + speed.Content); };
            // tryb mini: male okienko bez ramki w prawym dolnym rogu, zawsze na wierzchu; powrot - poprzedni rozmiar i miejsce
            Rect normalBounds = Rect.Empty;
            Action toggleMini = () =>
            {
                if (full0) return;
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

            play.Click += (s, e) => togglePlay();
            stop.Click += (s, e) => { savePos(); mp.Stop(); };
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
            lib.Click += (s, e) => ShowVideoLibrary();
            snap.Click += (s, e) =>
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                var file = System.IO.Path.Combine(dir, "Velivo-klatka-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                if (mp.TakeSnapshot(0, file, 0, 0)) ShowToast(L.T("📷 Zapisano klatkę: ") + System.IO.Path.GetFileName(file), file);
            };
            seek.PreviewMouseDown += (s, e) => seeking = true;
            seek.PreviewMouseUp += (s, e) => { if (mp.Length > 0) mp.Time = (long)(seek.Value / 1000 * mp.Length); seeking = false; };
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
                if (e.ClickCount == 2) { togglePlay(); toggleFull(); }   // pierwszy klik dwukliku juz zatrzymal - wznawiamy
                else togglePlay();
            };
            overlay.MouseWheel += (s, e) => { if (controls.IsMouseOver && !vol.IsMouseOver) return; showBar(); setVol(vol.Value + (e.Delta > 0 ? 5 : -5)); showOsd(L.T("Głośność ") + (int)vol.Value + "%"); };
            overlay.MouseMove += (s, e) => showBar();
            mp.Paused += (s, e) => Dispatcher.BeginInvoke(showBar);   // pauza - pasek zostaje widoczny
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
                    case Key.Escape: if (full0) toggleFull(); else if (mini0) toggleMini(); else h = false; break;
                    case Key.M: toggleMute(); break;
                    case Key.N: _vlcLoad(_playIndex + 1); break;
                    case Key.P: _vlcLoad(_playIndex - 1); break;
                    case Key.OemOpenBrackets: setSpeed(-1); break;
                    case Key.OemCloseBrackets: setSpeed(1); break;
                    case Key.S: mp.Stop(); break;
                    default: h = false; break;
                }
                if (h) e.Handled = true;
            };
            win.Closing += (s, e) => savePos();
            win.Closed += (s, e) =>
            {
                saveTimer.Stop(); hideTimer.Stop(); osdTimer.Stop(); lectorTimer.Stop(); LectorStopVoice(lc, mp);
                _vlcWin = null; _vlcLoad = null;
                view.MediaPlayer = null;
                System.Threading.Tasks.Task.Run(() => { try { mp.Stop(); mp.Dispose(); } catch (Exception) { } });   // Stop VLC moze chwile trwac - nie w watku okna
            };
            _vlcWin = win;
            win.Show();
        }
    }
}
