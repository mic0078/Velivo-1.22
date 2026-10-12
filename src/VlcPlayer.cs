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
            public string Title { get; set; } public string Length { get; set; } public string Seen { get; set; } public string Folder { get; set; }
            public LibraryVideo Item;
            public bool Missing;
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
            grid.Columns.Add(new GridViewColumn { Header = L.T("Tytuł"), Width = 360, DisplayMemberBinding = new System.Windows.Data.Binding("Title") });
            grid.Columns.Add(new GridViewColumn { Header = L.T("Długość"), Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding("Length") });
            grid.Columns.Add(new GridViewColumn { Header = L.T("Obejrzane"), Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("Seen") });
            grid.Columns.Add(new GridViewColumn { Header = L.T("Folder"), Width = 300, DisplayMemberBinding = new System.Windows.Data.Binding("Folder") });
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
                var all = lib;
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
                    return all.Where(v => q.Length == 0 || v.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || v.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(v => v.Last).ThenBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase)
                    .Select(v => new LibRow
                    {
                        Item = v, Missing = !exists(v.Path), Title = v.Title,
                        Length = v.Len > 0 ? FormatTime(v.Len) : "",
                        Seen = v.Last == 0 ? "" : v.Len > 0 && v.Pos == 0 ? "✓" : v.Len > 0 ? (int)(100.0 * v.Pos / v.Len) + "%" : "…",
                        Folder = System.IO.Path.GetDirectoryName(v.Path),
                    }).Select(r => { if (r.Missing) r.Title = "⚠ " + r.Title; return r; }).ToList();
                });
                if (ver != fillVer) return;   // w miedzyczasie nowsze szukanie
                rows = newRows;
                list.ItemsSource = rows;
                count.Text = rows.Count + " / " + lib.Count + L.T(" filmów");
            };
            _libRefresh = fill;
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

        void BuildVlcWindow()
        {
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
            Func<string, string, Button> glyphBtn = (g, tip) => new Button
            {
                Content = new TextBlock { Text = g, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16 }, ToolTip = tip,
                Width = 38, Height = 34, Margin = new Thickness(1, 0, 1, 0), Foreground = fg, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            };
            Func<string, string, Button> textBtn = (t, tip) => new Button
            {
                Content = t, ToolTip = tip, MinWidth = 44, Height = 34, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(1, 0, 1, 0), Foreground = fg,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 13,
            };
            var timeNow = new TextBlock { Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), Text = "0:00", MinWidth = 52, TextAlignment = TextAlignment.Right };
            var timeLen = new TextBlock { Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0), Text = "0:00", MinWidth = 52 };
            var seek = new Slider { Minimum = 0, Maximum = 1000, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            var seekRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(timeNow, Dock.Left); DockPanel.SetDock(timeLen, Dock.Right);
            seekRow.Children.Add(timeNow); seekRow.Children.Add(timeLen); seekRow.Children.Add(seek);

            var prev = glyphBtn("\uE892", L.T("Poprzedni (P)"));
            var play = glyphBtn("\uE769", L.T("Pauza / odtwarzaj (spacja)"));
            var stop = glyphBtn("\uE71A", L.T("Stop"));
            var next = glyphBtn("\uE893", L.T("Następny (N)"));
            var back10 = textBtn("−10 s", L.T("10 s wstecz (←)"));
            var fwd10 = textBtn("+10 s", L.T("10 s do przodu (→)"));
            var speed = textBtn("1×", L.T("Prędkość odtwarzania ([ i ])"));
            var subs = glyphBtn("\uE7F0", L.T("Napisy"));
            var audio = glyphBtn("\uE8D6", L.T("Ścieżka dźwięku (język)"));
            var aspect = glyphBtn("\uE799", L.T("Proporcje obrazu"));
            var snap = glyphBtn("\uE722", L.T("Zrzut klatki (zapis do Obrazów)"));
            var lib = glyphBtn("\uE8B3", L.T("Biblioteka filmów"));
            var full = glyphBtn("\uE740", L.T("Pełny ekran (F, dwuklik)"));
            var mute = glyphBtn("\uE767", L.T("Wycisz (M)"));
            var vol = new Slider { Minimum = 0, Maximum = 125, Value = 100, Width = 110, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, ToolTip = L.T("Głośność (↑ ↓, kółko myszy)") };
            var volText = new TextBlock { Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Width = 44, Margin = new Thickness(6, 0, 8, 0), Text = "100%" };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var b in new UIElement[] { prev, play, stop, next, back10, fwd10, speed }) left.Children.Add(b);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var b in new UIElement[] { subs, audio, aspect, snap, lib, full, mute, vol, volText }) right.Children.Add(b);
            var btnRow = new DockPanel { Margin = new Thickness(4, 2, 4, 4) };
            DockPanel.SetDock(left, Dock.Left); DockPanel.SetDock(right, Dock.Right);
            btnRow.Children.Add(left); btnRow.Children.Add(right);
            var controls = new StackPanel { Background = dim };
            controls.Children.Add(seekRow); controls.Children.Add(btnRow);
            var root = new DockPanel();
            DockPanel.SetDock(controls, Dock.Bottom);
            root.Children.Add(controls); root.Children.Add(view);
            win.Content = root;

            LibraryVideo current = null;
            bool seeking = false, full0 = false;
            WindowState prevState = WindowState.Normal;
            float[] speeds = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 3f };
            int speedIdx = 3;
            var osdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
            osdTimer.Tick += (s, e) => { osdTimer.Stop(); osd.Visibility = Visibility.Collapsed; };
            Action<string> showOsd = t => { osd.Text = t; osd.Visibility = Visibility.Visible; osdTimer.Stop(); osdTimer.Start(); };
            var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            hideTimer.Tick += (s, e) => { hideTimer.Stop(); if (full0 && !controls.IsMouseOver) { controls.Visibility = Visibility.Collapsed; overlay.Cursor = Cursors.None; } };

            // zapis miejsca wznowienia co kilka sekund i przy zmianie filmu / zamknieciu
            Action savePos = () =>
            {
                if (current == null) return;
                try
                {
                    long t = mp.Time, len = mp.Length;
                    var l = LoadLibrary();
                    var v = l.FirstOrDefault(x => string.Equals(x.Path, current.Path, StringComparison.OrdinalIgnoreCase));
                    if (v == null) return;
                    v.Last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (len > 0) v.Len = len;
                    v.Pos = ResumeAt(t, v.Len);
                    SaveLibrary(l);
                    _libRefresh?.Invoke();
                }
                catch (Exception ex) { App.LogError(ex); }
            };
            var saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            saveTimer.Tick += (s, e) => { if (mp.IsPlaying) savePos(); };
            saveTimer.Start();

            _vlcLoad = i =>
            {
                savePos();
                if (i < 0 || i >= _playlist.Count) return;
                _playIndex = i;
                var path = _playlist[i];
                current = LoadLibrary().FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                win.Title = System.IO.Path.GetFileName(path) + " – " + L.T("Odtwarzacz filmów");
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
                speedIdx = 3; speed.Content = "1×";
                prev.IsEnabled = i > 0; next.IsEnabled = i < _playlist.Count - 1;
            };

            mp.LengthChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() => timeLen.Text = FormatTime(e.Length)));
            mp.TimeChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                timeNow.Text = FormatTime(e.Time);
                if (!seeking && mp.Length > 0) seek.Value = 1000.0 * e.Time / mp.Length;
            }));
            mp.Playing += (s, e) => Dispatcher.BeginInvoke(new Action(() => ((TextBlock)play.Content).Text = "\uE769"));
            mp.Paused += (s, e) => Dispatcher.BeginInvoke(new Action(() => ((TextBlock)play.Content).Text = "\uE768"));
            mp.Stopped += (s, e) => Dispatcher.BeginInvoke(new Action(() => { ((TextBlock)play.Content).Text = "\uE768"; seek.Value = 0; timeNow.Text = "0:00"; }));
            mp.EndReached += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (current != null) { var l = LoadLibrary(); var v = l.FirstOrDefault(x => x.Path == current.Path); if (v != null) { v.Pos = 0; v.Last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); SaveLibrary(l); _libRefresh?.Invoke(); } current = null; }
                if (_playIndex < _playlist.Count - 1) _vlcLoad(_playIndex + 1);   // nastepny z listy (VLC nie moze startowac w swoim watku zdarzen)
            }));
            mp.EncounteredError += (s, e) => Dispatcher.BeginInvoke(new Action(() => showOsd(L.T("Nie da się odtworzyć tego pliku"))));

            Action togglePlay = () => { if (mp.IsPlaying) mp.SetPause(true); else if (mp.State == VLCState.Stopped || mp.State == VLCState.Ended || mp.State == VLCState.NothingSpecial) _vlcLoad(_playIndex); else mp.SetPause(false); };
            Action<long> jump = d => { if (mp.Length <= 0) return; mp.Time = Math.Max(0, Math.Min(mp.Length - 500, mp.Time + d)); showOsd((d > 0 ? "+" : "−") + Math.Abs(d / 1000) + " s"); };
            Action<double> setVol = v => { vol.Value = Math.Max(0, Math.Min(125, v)); };
            vol.ValueChanged += (s, e) => { mp.Volume = (int)vol.Value; volText.Text = (int)vol.Value + "%"; if (mp.Mute && vol.Value > 0) mp.Mute = false; ((TextBlock)mute.Content).Text = mp.Mute || vol.Value == 0 ? "\uE74F" : "\uE767"; };
            Action toggleMute = () => { mp.Mute = !mp.Mute; ((TextBlock)mute.Content).Text = mp.Mute ? "\uE74F" : "\uE767"; showOsd(mp.Mute ? L.T("Wyciszono") : (int)vol.Value + "%"); };
            Action<int> setSpeed = d => { speedIdx = Math.Max(0, Math.Min(speeds.Length - 1, speedIdx + d)); mp.SetRate(speeds[speedIdx]); speed.Content = speeds[speedIdx].ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×"; showOsd(L.T("Prędkość ") + speed.Content); };
            Action toggleFull = () =>
            {
                full0 = !full0;
                if (full0) { prevState = win.WindowState; win.WindowStyle = WindowStyle.None; win.WindowState = WindowState.Normal; win.WindowState = WindowState.Maximized; win.Topmost = true; hideTimer.Start(); }
                else { win.Topmost = false; win.WindowStyle = WindowStyle.SingleBorderWindow; win.WindowState = prevState; controls.Visibility = Visibility.Visible; overlay.Cursor = null; }
                ((TextBlock)full.Content).Text = full0 ? "\uE73F" : "\uE740";
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
                    mi.Click += (a, b) => mp.SetSpu(id);
                    cm.Items.Add(mi);
                }
                if (cm.Items.Count > 0) cm.Items.Add(new Separator());
                var load = new MenuItem { Header = L.T("Wczytaj napisy z pliku…") };
                load.Click += (a, b) =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("Napisy"), Filter = L.T("Napisy") + "|" + string.Join(";", SubtitleExts.Select(x => "*" + x)) };
                    if (dlg.ShowDialog(win) == true) mp.AddSlave(MediaSlaveType.Subtitle, new Uri(dlg.FileName).AbsoluteUri, true);
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
                    mi.Click += (a, b) => mp.SetAudioTrack(id);
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
                if (e.ClickCount == 2) { togglePlay(); toggleFull(); }   // pierwszy klik dwukliku juz zatrzymal - wznawiamy
                else togglePlay();
            };
            overlay.MouseWheel += (s, e) => { setVol(vol.Value + (e.Delta > 0 ? 5 : -5)); showOsd(L.T("Głośność ") + (int)vol.Value + "%"); };
            overlay.MouseMove += (s, e) => { if (!full0) return; controls.Visibility = Visibility.Visible; overlay.Cursor = null; hideTimer.Stop(); hideTimer.Start(); };
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
                    case Key.Escape: if (full0) toggleFull(); else h = false; break;
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
                saveTimer.Stop(); hideTimer.Stop(); osdTimer.Stop();
                _vlcWin = null; _vlcLoad = null;
                view.MediaPlayer = null;
                System.Threading.Tasks.Task.Run(() => { try { mp.Stop(); mp.Dispose(); } catch (Exception) { } });   // Stop VLC moze chwile trwac - nie w watku okna
            };
            _vlcWin = win;
            win.Show();
        }
    }
}
