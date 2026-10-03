using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Zakladki, pobieranie plikow i dodatki (rozpakowane rozszerzenia Chromium).
    public partial class MainWindow
    {
        static string BookmarksFile { get { return Path.Combine(DataDir, "zakladki.txt"); } }

        // ================= ZAKLADKI =================

        sealed class Bookmark { public string Url, Title; }
        readonly List<Bookmark> _bookmarks = new List<Bookmark>();

        void LoadBookmarks()
        {
            _bookmarks.Clear();
            try
            {
                if (File.Exists(BookmarksFile))
                    foreach (var line in File.ReadAllLines(BookmarksFile))
                    {
                        var p = line.Split('\t');
                        if (p.Length >= 1 && p[0].Length > 0)
                            _bookmarks.Add(new Bookmark { Url = p[0], Title = p.Length > 1 && p[1].Length > 0 ? p[1] : p[0] });
                    }
            }
            catch (IOException) { }
            RenderBookmarkBar();
        }

        void SaveBookmarks()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                RecordBookmarkTombstones();
                File.WriteAllLines(BookmarksFile, _bookmarks.Select(b => b.Url + "\t" + b.Title.Replace('\t', ' ').Replace('\n', ' ')));
            }
            catch (IOException ex) { MessageBox.Show(this, L.T("Nie zapisano zakładek:\n") + ex.Message, L.T("Zakładki")); }
            RenderBookmarkBar();
            UpdateStar();
            NotifyLanStateChanged();
        }

        // Usuniete zakladki zapisujemy z czasem - synchronizacja LAN usunie je tez na drugim komputerze.
        // Ponowne dodanie zakladki kasuje jej wpis z listy usunietych.
        void RecordBookmarkTombstones()
        {
            try
            {
                var before = File.Exists(BookmarksFile)
                    ? new HashSet<string>(File.ReadAllLines(BookmarksFile).Select(l => l.Split('\t')[0]).Where(u => u.Length > 0), StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                var now = new HashSet<string>(_bookmarks.Select(b => b.Url), StringComparer.Ordinal);
                var tomb = ParseTombstones(File.Exists(BookmarkTombstonesFile) ? File.ReadAllText(BookmarkTombstonesFile) : "");
                long t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                bool changed = false;
                foreach (var u in before) if (!now.Contains(u)) { tomb[u] = t; changed = true; }
                foreach (var u in now) if (tomb.Remove(u)) changed = true;
                if (changed) File.WriteAllLines(BookmarkTombstonesFile, tomb.Select(kv => kv.Key + "\t" + kv.Value));
            }
            catch (Exception) { }
        }

        void RenderBookmarkBar()
        {
            BookmarkBar.Children.Clear();
            foreach (var b in _bookmarks)
            {
                var bm = b;
                var btn = new Button
                {
                    Content = new TextBlock { Text = bm.Title, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis },
                    Width = double.NaN, Height = 24, FontSize = 12, Padding = new Thickness(6, 0, 6, 0), ToolTip = bm.Url
                };
                btn.Click += (s, e) => { if (_current != null) Navigate(_current, bm.Url); };
                btn.MouseUp += (s, e) => { if (e.ChangedButton == System.Windows.Input.MouseButton.Middle) AddTab(bm.Url); };
                var menu = new ContextMenu();
                var openNew = new MenuItem { Header = L.T("Otwórz w nowej karcie") };
                openNew.Click += (s, e) => AddTab(bm.Url);
                var rename = new MenuItem { Header = L.T("Zmień nazwę") };
                rename.Click += (s, e) =>
                {
                    var name = Prompt(L.T("Nazwa zakładki:"), bm.Title);
                    if (string.IsNullOrWhiteSpace(name)) return;
                    bm.Title = name.Trim();
                    SaveBookmarks();
                };
                var del = new MenuItem { Header = L.T("Usuń") };
                del.Click += (s, e) => { _bookmarks.Remove(bm); SaveBookmarks(); };
                menu.Items.Add(openNew); menu.Items.Add(rename); menu.Items.Add(del);
                btn.ContextMenu = menu;
                BookmarkBar.Children.Add(btn);
            }
            BookmarkBar.Visibility = _bookmarks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        string CurrentUrl { get { return Core != null ? Core.Source : null; } }

        void UpdateStar()
        {
            bool marked = CurrentUrl != null && _bookmarks.Any(b => b.Url == CurrentUrl);
            StarBtn.Content = marked ? "\uE735" : "\uE734"; // pelna / pusta gwiazdka (Segoe Fluent Icons)
            StarBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
        }

        void Star_Click(object sender, RoutedEventArgs e)
        {
            var url = CurrentUrl;
            if (string.IsNullOrEmpty(url) || url.StartsWith("about:")) return;
            var existing = _bookmarks.FirstOrDefault(b => b.Url == url);
            if (existing != null) _bookmarks.Remove(existing);
            else _bookmarks.Add(new Bookmark { Url = url, Title = string.IsNullOrEmpty(Core.DocumentTitle) ? url : Core.DocumentTitle });
            SaveBookmarks();
        }

        void Bookmarks_Click(object sender, RoutedEventArgs e)
        {
            var list = new ListBox { BorderThickness = new Thickness(0) };
            Action fill = () =>
            {
                list.Items.Clear();
                foreach (var b in _bookmarks) list.Items.Add(new ListBoxItem { Content = b.Title + "   —   " + b.Url, Tag = b });
            };
            fill();
            var win = new Window { Title = L.T("Zakładki (dwuklik otwiera)"), Width = 700, Height = 500, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            list.MouseDoubleClick += (s, a) =>
            {
                var it = list.SelectedItem as ListBoxItem;
                if (it == null || _current == null) return;
                Navigate(_current, ((Bookmark)it.Tag).Url);
                win.Close();
            };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6) };
            bar.Children.Add(SmallButton(L.T("▲ W górę"), () => MoveBookmark(list, -1, fill)));
            bar.Children.Add(SmallButton(L.T("▼ W dół"), () => MoveBookmark(list, 1, fill)));
            bar.Children.Add(SmallButton(L.T("Usuń"), () =>
            {
                var it = list.SelectedItem as ListBoxItem;
                if (it == null) return;
                _bookmarks.Remove((Bookmark)it.Tag); SaveBookmarks(); fill();
            }));
            win.Content = Docked(bar, list);
            win.Show();
        }

        void MoveBookmark(ListBox list, int dir, Action fill)
        {
            int i = list.SelectedIndex, j = i + dir;
            if (i < 0 || j < 0 || j >= _bookmarks.Count) return;
            var b = _bookmarks[i]; _bookmarks.RemoveAt(i); _bookmarks.Insert(j, b);
            SaveBookmarks(); fill(); list.SelectedIndex = j;
        }

        // ================= DODATKI =================

        static readonly HashSet<string> BuiltInExtensions = new HashSet<string>
        {
            "dgiklkfkllikcanfonkcabmbdfmgleag", // Microsoft Clipboard Extension
            "mhjfbmdgcfjbbpaeojofohoefgiehjai", // Microsoft Edge PDF Viewer
        };

        async void Extensions_Click(object sender, RoutedEventArgs e)
        {
            if (Core == null) return;
            var profile = Core.Profile;
            var list = new StackPanel();
            var win = new Window { Title = L.T("Dodatki – tryb dewelopera"), Width = 620, Height = 440, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };

            Func<System.Threading.Tasks.Task> refresh = null;
            refresh = async () =>
            {
                await RefreshExtensions();
                list.Children.Clear();
                IReadOnlyList<CoreWebView2BrowserExtension> exts;
                try { exts = await profile.GetBrowserExtensionsAsync(); }
                catch (Exception ex) { list.Children.Add(new TextBlock { Text = L.T("Błąd: ") + ex.Message, Margin = new Thickness(10) }); return; }
                if (exts.Count(x => !BuiltInExtensions.Contains(x.Id)) == 0)
                    list.Children.Add(new TextBlock { Text = L.T("Brak dodatków. Kliknij „Wczytaj rozpakowany…” i wskaż folder z manifest.json."), Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
                foreach (var x in exts)
                {
                    if (BuiltInExtensions.Contains(x.Id)) continue; // skladniki silnika Edge - nie pokazujemy
                    var ext = x;
                    var on = new CheckBox { IsChecked = ext.IsEnabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = L.T("Włączony") };
                    on.Click += async (s, a) =>
                    {
                        try
                        {
                            await ext.EnableAsync(on.IsChecked == true);
                            if (IsQuickAccessExtensionId(ext.Id) && _settings != null)
                            {
                                _settings.QuickAccessNewTab = on.IsChecked == true;
                                _settings.Save(DataDir);
                            }
                            await SaveExtensionsSyncListAsync();
                            NotifyLanStateChanged();
                        }
                        catch (Exception ex) { MessageBox.Show(win, ex.Message, L.T("Dodatki")); }
                        await refresh();
                    };
                    var remove = SmallButton(L.T("Usuń"), null);
                    remove.Click += async (s, a) =>
                    {
                        if (MessageBox.Show(win, L.T("Usunąć dodatek „") + ext.Name + "”?", L.T("Dodatki"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                        try
                        {
                            await ext.RemoveAsync();
                            if (IsQuickAccessExtensionId(ext.Id) && _settings != null)
                            {
                                _settings.QuickAccessNewTab = false;
                                _settings.Save(DataDir);
                            }
                            SaveExtPath(ext.Id, null);
                            if (Directory.Exists(ExtensionsDir))
                                foreach (var idf in Directory.GetFiles(ExtensionsDir, "*.id"))
                                    if (string.Equals(File.ReadAllText(idf).Trim(), ext.Id, StringComparison.OrdinalIgnoreCase))
                                    {
                                        var dir = Path.Combine(ExtensionsDir, Path.GetFileNameWithoutExtension(idf));
                                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                                        File.Delete(idf);
                                    }
                            await SaveExtensionsSyncListAsync();
                            NotifyLanStateChanged();
                        }
                        catch (Exception ex) { MessageBox.Show(win, ex.Message, L.T("Dodatki")); }
                        await refresh();
                    };
                    var paths = LoadExtPaths(); string folder; paths.TryGetValue(ext.Id, out folder);
                    var info = ReadManifest(ext, folder);
                    var opts = SmallButton(info.Options != null ? L.T("Opcje") : (info.Popup != null ? L.T("Okienko") : L.T("Opcje")), null);
                    opts.IsEnabled = info.Options != null || info.Popup != null;
                    opts.ToolTip = info.Options != null ? L.T("Strona ustawień dodatku") : info.Popup != null ? L.T("Otwiera okienko dodatku w karcie") : L.T("Dodatek nie ma strony ustawień");
                    opts.Click += (s, a) => AddTab(ExtUrl(info, info.Options ?? info.Popup));
                    var desc = new StackPanel();
                    desc.Children.Add(new TextBlock { Text = ext.Name, FontWeight = FontWeights.SemiBold });
                    desc.Children.Add(new TextBlock { Text = "ID: " + ext.Id + (folder != null ? "   ·   " + folder : ""), FontSize = 11, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder });
                    var reload = SmallButton(L.T("Przeładuj"), null);
                    reload.ToolTip = L.T("Wczytuje dodatek od nowa z folderu (po zmianie jego plików). Dane dodatku zostają.");
                    reload.IsEnabled = ext.IsEnabled;
                    reload.Click += async (s, a) =>
                    {
                        try
                        {
                            await ReloadExtension(ext);
                            if (folder != null) { var st = LoadStamps(); var stamp = CodeStamp(folder); if (stamp != null) { st[ext.Id] = stamp; SaveStamps(st); } }
                        }
                        catch (Exception ex) { MessageBox.Show(win, ex.Message, L.T("Dodatki")); }
                        await refresh();
                    };
                    var row = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
                    DockPanel.SetDock(on, Dock.Left); DockPanel.SetDock(remove, Dock.Right); DockPanel.SetDock(opts, Dock.Right); DockPanel.SetDock(reload, Dock.Right);
                    row.Children.Add(on); row.Children.Add(remove); row.Children.Add(opts); row.Children.Add(reload); row.Children.Add(desc);
                    list.Children.Add(row);
                }
            };

            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6) };
            var add = SmallButton(L.T("Wczytaj rozpakowany…"), null);
            add.Click += async (s, a) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Folder dodatku (z plikiem manifest.json)") };
                if (dlg.ShowDialog(win) != true) return;
                if (!File.Exists(Path.Combine(dlg.FolderName, "manifest.json")))
                {
                    MessageBox.Show(win, L.T("W tym folderze nie ma pliku manifest.json.\nJeśli masz plik .crx lub .zip – rozpakuj go najpierw."), L.T("Dodatki"));
                    return;
                }
                try { var added = await profile.AddBrowserExtensionAsync(dlg.FolderName); SaveExtPath(added.Id, dlg.FolderName); }
                catch (Exception ex) { MessageBox.Show(win, L.T("Nie udało się wczytać dodatku:\n") + ex.Message, L.T("Dodatki")); }
                await SaveExtensionsSyncListAsync();
                NotifyLanStateChanged();
                await refresh();
            };
            var openStore = SmallButton(L.T("Otwórz Chrome Web Store"), () => AddTab("https://chromewebstore.google.com/"));
            var fromStore = SmallButton(L.T("Zainstaluj z linku/ID…"), null);
            fromStore.Click += async (s, a) =>
            {
                var text = Prompt(L.T("Wklej link do dodatku z Chrome Web Store albo jego ID (32 litery):"), "");
                if (text == null) return;
                var id = ParseStoreId(text);
                if (id == null) { MessageBox.Show(win, L.T("To nie wygląda na link do dodatku ani na jego ID."), L.T("Dodatki")); return; }
                await InstallFromStore(id, win, false);
                await refresh();
            };
            bar.Children.Add(openStore);
            bar.Children.Add(fromStore);
            bar.Children.Add(add);
            var note = new TextBlock
            {
                Text = L.T("Dodatki działają w tle i na stronach (skrypty treści, blokowanie, zmiana wyglądu). Silnik WebView2 nie pokazuje ikonek dodatków ani ich okienek popup – ustawienia otwierasz przyciskiem „Opcje”. Po dodaniu odśwież stronę."),
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(10, 8, 10, 0)
            };
            var top = new DockPanel();
            DockPanel.SetDock(note, Dock.Top);
            top.Children.Add(note);
            top.Children.Add(Docked(bar, new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));
            win.Content = top;
            win.Show();
            await refresh();
        }

        // ================= pomocnicze =================

        static Button SmallButton(string text, Action onClick)
        {
            var b = new Button { Content = text, Width = double.NaN, Height = 28, FontSize = 12, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(4, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)) };
            if (onClick != null) b.Click += (s, e) => onClick();
            return b;
        }

        static DockPanel Docked(UIElement bottom, UIElement fill)
        {
            var d = new DockPanel();
            DockPanel.SetDock(bottom, Dock.Bottom);
            d.Children.Add(bottom);
            d.Children.Add(fill);
            return d;
        }

        string Prompt(string label, string value)
        {
            var box = new TextBox { Text = value, Margin = new Thickness(10, 4, 10, 4), Padding = new Thickness(4) };
            var ok = new Button { Content = "OK", Width = 80, Height = 28, IsDefault = true, Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Right };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(10, 10, 10, 0) });
            panel.Children.Add(box);
            panel.Children.Add(ok);
            var w = new Window { Title = "Velivo", Width = 400, SizeToContent = SizeToContent.Height, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Content = panel };
            ok.Click += (s, e) => w.DialogResult = true;
            w.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            return w.ShowDialog() == true ? box.Text : null;
        }
    }
}








