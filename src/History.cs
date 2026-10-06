using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Przegladarka
{
    // Historia pogrupowana jak w Chrome: dzien -> sesja (jedno uruchomienie przegladarki) -> strony.
    // Plik historia.txt: czas \t adres \t tytul \t sesja (znacznik uruchomienia - zostaje w formacie pliku: synchronizacja LAN ze starszymi wersjami).
    public partial class MainWindow
    {
        static readonly string SessionId = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        static readonly CultureInfo Pl = new CultureInfo("pl-PL");

        static void AppendHistory(string url, string title)
        {
            if (string.IsNullOrEmpty(url) || url.StartsWith("about:")) return;
            try
            {
                File.AppendAllText(HistoryFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\t" + url + "\t" +
                    (title ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') + "\t" + SessionId + Environment.NewLine);
            }
            catch (IOException) { }
        }

        sealed class HistEntry
        {
            public int Line;          // numer linii w pliku (do usuwania)
            public List<int> Lines = new List<int>(); // ta linia + zwiniete odswiezenia tej samej strony
            public DateTime Time;     // ostatnia wizyta (po zwinieciu odswiezen)
            public string Url, Title;
            public string Host
            {
                get { Uri u; return Uri.TryCreate(Url, UriKind.Absolute, out u) ? u.Host : Url; }
            }
        }

        static List<HistEntry> LoadHistory()
        {
            var list = new List<HistEntry>();
            try
            {
                if (!File.Exists(HistoryFile)) return list;
                int n = -1;
                foreach (var line in File.ReadLines(HistoryFile))
                {
                    n++;
                    var p = line.Split('\t');
                    DateTime t;
                    if (p.Length < 2 || !DateTime.TryParseExact(p[0], new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" },
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                    list.Add(new HistEntry { Line = n, Time = t, Url = p[1], Title = p.Length > 2 ? p[2] : "" });
                }
            }
            catch (IOException) { }
            return list;
        }

        static void DeleteHistoryLines(ICollection<int> lines)
        {
            if (lines.Count == 0) return;
            var keep = new HashSet<int>(lines);
            var all = File.ReadAllLines(HistoryFile);
            RememberDeletedHistory(all.Where((l, i) => keep.Contains(i)));   // drugi komputer w sieci tez je usunie
            File.WriteAllLines(HistoryFile, all.Where((l, i) => !keep.Contains(i)));
        }

        static string DayLabel(DateTime d)
        {
            var today = DateTime.Today;
            string name = d.ToString("dddd, d MMMM yyyy", Pl);
            if (d == today) return L.T("Dziś – ") + name;
            if (d == today.AddDays(-1)) return L.T("Wczoraj – ") + name;
            return char.ToUpper(name[0]) + name.Substring(1);
        }

        static string SiteName(string host) { return host != null && host.StartsWith("www.") ? host.Substring(4) : host; }

        static string Sites(int n)
        {
            if (L.En) return n + (n == 1 ? " site" : " sites");
            int r10 = n % 10, r100 = n % 100;
            return n + (n == 1 ? " witryna" : r10 >= 2 && r10 <= 4 && (r100 < 12 || r100 > 14) ? " witryny" : " witryn");
        }

        static string Pages(int n)
        {
            if (L.En) return n + (n == 1 ? " page" : " pages");
            if (n == 1) return "1 strona";
            int r10 = n % 10, r100 = n % 100;
            return n + (r10 >= 2 && r10 <= 4 && (r100 < 12 || r100 > 14) ? L.T(" strony") : " stron");
        }

        void History_Click(object sender, RoutedEventArgs e)
        {
            var win = new Window { Title = L.T("Historia"), Width = 820, Height = 620, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var search = new TextBox { Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(8, 8, 8, 4), FontSize = 13, ToolTip = L.T("Szukaj w tytułach i adresach") };
            var hint = new TextBlock { Text = L.T("🔍 Szukaj w historii…"), Foreground = Brushes.Gray, Margin = new Thickness(16, 12, 0, 0), IsHitTestVisible = false };
            var tree = new TreeView { BorderThickness = new Thickness(0), Margin = new Thickness(4, 0, 4, 0) };
            var status = new TextBlock { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

            Action rebuild = null;
            Action<ICollection<int>> remove = lines =>
            {
                try { DeleteHistoryLines(lines); } catch (IOException ex) { MessageBox.Show(win, ex.Message, L.T("Historia")); }
                rebuild();
            };

            Func<HistEntry, TreeViewItem> entryItem = h =>
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = h.Time.ToString("HH:mm"), Foreground = Brushes.Gray, Width = 46 });
                row.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(h.Title) ? h.Url : h.Title, MaxWidth = 470, TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(new TextBlock { Text = "  " + SiteName(h.Host) + (h.Lines.Count > 1 ? "  ·  ×" + h.Lines.Count : ""), Foreground = Brushes.Gray });
                var it = new TreeViewItem { Header = row, ToolTip = h.Url, Tag = h };
                var menu = new ContextMenu();
                var open = new MenuItem { Header = L.T("Otwórz") }; open.Click += (a, b) => { if (_current != null) Navigate(_current, h.Url); };
                var openNew = new MenuItem { Header = L.T("Otwórz w nowej karcie") }; openNew.Click += (a, b) => AddTab(h.Url);
                var del = new MenuItem { Header = L.T("Usuń z historii") }; del.Click += (a, b) => remove(h.Lines.ToList());
                menu.Items.Add(open); menu.Items.Add(openNew); menu.Items.Add(new Separator()); menu.Items.Add(del);
                it.ContextMenu = menu;
                it.MouseDoubleClick += (a, b) =>
                {
                    if (!it.IsSelected) return;
                    b.Handled = true;
                    if (_current != null) Navigate(_current, h.Url);
                };
                return it;
            };

            rebuild = () =>
            {
                tree.Items.Clear();
                // strona nowej karty (Szybki Dostep) i strony wewnetrzne to szum - nie pokazujemy ich w historii
                var all = LoadHistory().Where(h => h.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
                string q = search.Text.Trim();
                bool filtering = q.Length > 0;
                var shown = filtering
                    ? all.Where(h => (h.Title ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || h.Url.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
                    : all;
                int days = 0;
                // dzien -> strona (domena) -> podstrony; ta sama podstrona odwiedzona kilka razy pokazana raz
                foreach (var day in shown.GroupBy(h => h.Time.Date).OrderByDescending(g => g.Key))
                {
                    var pages = day.GroupBy(h => h.Url).Select(g =>
                    {
                        var newest = g.OrderByDescending(x => x.Time).First();
                        return new HistEntry { Url = g.Key, Title = g.Select(x => x.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "", Time = newest.Time, Lines = g.Select(x => x.Line).ToList(), Line = g.Count() };
                    }).ToList();
                    var sites = pages.GroupBy(h => SiteName(h.Host)).OrderByDescending(g => g.Max(x => x.Time)).ToList();
                    var dayItem = new TreeViewItem
                    {
                        Header = new TextBlock { Text = DayLabel(day.Key) + "   ·   " + Pages(pages.Count) + "   ·   " + Sites(sites.Count), FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 4, 0, 2) },
                        IsExpanded = filtering || days == 0
                    };
                    var dayLines = day.Select(h => h.Line).ToList();
                    var dayMenu = new ContextMenu();
                    var delDay = new MenuItem { Header = filtering ? L.T("Usuń znalezione wpisy z tego dnia") : L.T("Usuń cały dzień z historii") };
                    delDay.Click += (a, b) => { if (MessageBox.Show(win, L.T("Usunąć historię z tego dnia?"), L.T("Historia"), MessageBoxButton.YesNo) == MessageBoxResult.Yes) remove(dayLines); };
                    dayMenu.Items.Add(delDay); dayItem.ContextMenu = dayMenu;

                    foreach (var site in sites)
                    {
                        var list = site.OrderByDescending(x => x.Time).ToList();
                        if (list.Count == 1) { dayItem.Items.Add(entryItem(list[0])); continue; }   // pojedyncza strona - bez dodatkowego poziomu
                        var header = new StackPanel { Orientation = Orientation.Horizontal };
                        header.Children.Add(new TextBlock { Text = list[0].Time.ToString("HH:mm"), Foreground = Brushes.Gray, Width = 46 });
                        header.Children.Add(new TextBlock { Text = site.Key, FontWeight = FontWeights.SemiBold });
                        header.Children.Add(new TextBlock { Text = "   " + Pages(list.Count), Foreground = Brushes.Gray });
                        var sItem = new TreeViewItem { Header = header, IsExpanded = filtering };
                        var sMenu = new ContextMenu();
                        var openAll = new MenuItem { Header = L.T("Otwórz wszystkie w nowych kartach") };
                        var urls = list.Select(x => x.Url).ToList();
                        openAll.Click += (a, b) =>
                        {
                            if (urls.Count > 15 && MessageBox.Show(win, L.T("Otworzyć ") + urls.Count + L.T(" kart?"), L.T("Historia"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                            foreach (var u in urls) AddTab(u);
                        };
                        var sLines = list.SelectMany(x => x.Lines).ToList();
                        var delS = new MenuItem { Header = L.T("Usuń tę stronę z historii dnia") };
                        delS.Click += (a, b) => remove(sLines);
                        sMenu.Items.Add(openAll); sMenu.Items.Add(new Separator()); sMenu.Items.Add(delS);
                        sItem.ContextMenu = sMenu;
                        foreach (var h in list) sItem.Items.Add(entryItem(h));
                        dayItem.Items.Add(sItem);
                    }
                    tree.Items.Add(dayItem);
                    days++;
                }
                status.Text = filtering ? L.T("Znaleziono: ") + Pages(shown.Count) : L.T("Razem: ") + Pages(all.Count);
                if (tree.Items.Count == 0)
                    tree.Items.Add(new TreeViewItem { Header = new TextBlock { Text = filtering ? L.T("Nic nie znaleziono.") : L.T("Historia jest pusta."), Foreground = Brushes.Gray, Margin = new Thickness(6) } });
            };

            search.TextChanged += (a, b) => { hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; rebuild(); };
            win.PreviewKeyDown += (a, b) => { if (b.Key == Key.Escape) win.Close(); };

            var clearAll = SmallButton(L.T("Wyczyść całą historię"), () =>
            {
                if (MessageBox.Show(win, L.T("Usunąć całą historię?"), L.T("Historia"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                try { File.Delete(HistoryFile); } catch (IOException) { }
                RememberHistoryCleared();
                ForgetAllPageMemory();
                rebuild();
            });
            var bottom = new DockPanel { Margin = new Thickness(6) };
            DockPanel.SetDock(clearAll, Dock.Right);
            var memBtn = SmallButton(L.T("🧠 Szukaj w treści stron…"), ShowPageMemorySearch);
            memBtn.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(memBtn, Dock.Right);
            bottom.Children.Add(clearAll); bottom.Children.Add(memBtn); bottom.Children.Add(status);
            bottom.Children.Add(new TextBlock { Text = L.T("Dwuklik otwiera stronę · prawy klik: więcej opcji"), Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right });

            var top = new Grid();
            top.Children.Add(search); top.Children.Add(hint);
            var dock = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(top); dock.Children.Add(bottom); dock.Children.Add(tree);
            win.Content = dock;
            rebuild();
            win.Show();
            search.Focus();
        }
    }
}
