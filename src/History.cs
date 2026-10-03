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
    // Plik historia.txt: czas \t adres \t tytul \t sesja  (starsze wpisy bez sesji dzielimy po 30 min przerwy).
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
            public DateTime Start;    // pierwsza wizyta
            public string Url, Title, Session;
            public string Host
            {
                get { Uri u; return Uri.TryCreate(Url, UriKind.Absolute, out u) ? u.Host : Url; }
            }
        }

        sealed class HistSession { public string Key; public List<HistEntry> Entries = new List<HistEntry>(); }

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
                    list.Add(new HistEntry { Line = n, Time = t, Url = p[1], Title = p.Length > 2 ? p[2] : "", Session = p.Length > 3 ? p[3] : null });
                }
            }
            catch (IOException) { }
            return list;
        }

        // Sesje w obrebie dnia: wg zapisanego numeru sesji, a dla starych wpisow - przerwa > 30 minut.
        static List<HistSession> SplitSessions(IEnumerable<HistEntry> dayEntries)
        {
            var sessions = new List<HistSession>();
            HistSession cur = null; HistEntry prev = null;
            foreach (var e in dayEntries.OrderBy(x => x.Time).ThenBy(x => x.Line))
            {
                bool newSession = cur == null ||
                    (e.Session != null ? e.Session != cur.Key : (prev.Session != null || (e.Time - prev.Time).TotalMinutes > 30));
                if (newSession) { cur = new HistSession { Key = e.Session ?? ("~" + e.Time.Ticks) }; sessions.Add(cur); }
                // kolejne odswiezenia tej samej strony pokazujemy raz (najnowszy czas, wszystkie linie do usuwania)
                var shownPrev = cur.Entries.Count > 0 ? cur.Entries[cur.Entries.Count - 1] : null;
                if (!newSession && shownPrev != null && shownPrev.Url == e.Url)
                {
                    shownPrev.Lines.Add(e.Line); shownPrev.Time = e.Time; prev = e; continue;
                }
                e.Lines.Clear(); e.Lines.Add(e.Line); e.Start = e.Time;
                cur.Entries.Add(e); prev = e;
            }
            sessions.Reverse();
            foreach (var s in sessions) s.Entries.Reverse();
            return sessions;
        }

        static void DeleteHistoryLines(ICollection<int> lines)
        {
            if (lines.Count == 0) return;
            var keep = new HashSet<int>(lines);
            var all = File.ReadAllLines(HistoryFile);
            File.WriteAllLines(HistoryFile, all.Where((l, i) => !keep.Contains(i)));
        }

        static string DayLabel(DateTime d)
        {
            var today = DateTime.Today;
            string name = d.ToString("dddd, d MMMM yyyy", Pl);
            if (d == today) return "Dziś – " + name;
            if (d == today.AddDays(-1)) return "Wczoraj – " + name;
            return char.ToUpper(name[0]) + name.Substring(1);
        }

        static string Pages(int n)
        {
            if (n == 1) return "1 strona";
            int r10 = n % 10, r100 = n % 100;
            return n + (r10 >= 2 && r10 <= 4 && (r100 < 12 || r100 > 14) ? " strony" : " stron");
        }

        void History_Click(object sender, RoutedEventArgs e)
        {
            var win = new Window { Title = "Historia", Width = 820, Height = 620, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var search = new TextBox { Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(8, 8, 8, 4), FontSize = 13, ToolTip = "Szukaj w tytułach i adresach" };
            var hint = new TextBlock { Text = "🔍 Szukaj w historii…", Foreground = Brushes.Gray, Margin = new Thickness(16, 12, 0, 0), IsHitTestVisible = false };
            var tree = new TreeView { BorderThickness = new Thickness(0), Margin = new Thickness(4, 0, 4, 0) };
            var status = new TextBlock { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

            Action rebuild = null;
            Action<ICollection<int>> remove = lines =>
            {
                try { DeleteHistoryLines(lines); } catch (IOException ex) { MessageBox.Show(win, ex.Message, "Historia"); }
                rebuild();
            };

            Func<HistEntry, TreeViewItem> entryItem = h =>
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = h.Time.ToString("HH:mm"), Foreground = Brushes.Gray, Width = 46 });
                row.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(h.Title) ? h.Url : h.Title, MaxWidth = 470, TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(new TextBlock { Text = "  " + h.Host, Foreground = Brushes.Gray });
                var it = new TreeViewItem { Header = row, ToolTip = h.Url, Tag = h };
                var menu = new ContextMenu();
                var open = new MenuItem { Header = "Otwórz" }; open.Click += (a, b) => { if (_current != null) Navigate(_current, h.Url); };
                var openNew = new MenuItem { Header = "Otwórz w nowej karcie" }; openNew.Click += (a, b) => AddTab(h.Url);
                var del = new MenuItem { Header = "Usuń z historii" }; del.Click += (a, b) => remove(h.Lines.ToList());
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
                var all = LoadHistory();
                string q = search.Text.Trim();
                bool filtering = q.Length > 0;
                var shown = filtering
                    ? all.Where(h => (h.Title ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || h.Url.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
                    : all;
                int days = 0;
                foreach (var day in shown.GroupBy(h => h.Time.Date).OrderByDescending(g => g.Key))
                {
                    var sessions = SplitSessions(day);
                    int count = sessions.Sum(s => s.Entries.Count);
                    var dayItem = new TreeViewItem
                    {
                        Header = new TextBlock { Text = DayLabel(day.Key) + "   ·   " + Pages(count), FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 4, 0, 2) },
                        IsExpanded = filtering || days == 0
                    };
                    var dayLines = day.Select(h => h.Line).ToList();
                    var dayMenu = new ContextMenu();
                    var delDay = new MenuItem { Header = filtering ? "Usuń znalezione wpisy z tego dnia" : "Usuń cały dzień z historii" };
                    delDay.Click += (a, b) => { if (MessageBox.Show(win, "Usunąć historię z tego dnia?", "Historia", MessageBoxButton.YesNo) == MessageBoxResult.Yes) remove(dayLines); };
                    dayMenu.Items.Add(delDay); dayItem.ContextMenu = dayMenu;

                    int si = 0;
                    foreach (var s in sessions)
                    {
                        var first = s.Entries.Last(); var last = s.Entries.First();
                        var hosts = s.Entries.GroupBy(x => x.Host).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key);
                        bool now = s.Key == SessionId;
                        var header = new StackPanel { Orientation = Orientation.Horizontal };
                        header.Children.Add(new TextBlock { Text = (now ? "● Bieżąca sesja  " : "Sesja  ") + first.Start.ToString("HH:mm") + "–" + last.Time.ToString("HH:mm"), FontWeight = FontWeights.SemiBold, Foreground = now ? Brushes.SeaGreen : Brushes.Black });
                        header.Children.Add(new TextBlock { Text = "   " + Pages(s.Entries.Count) + "   ·   " + string.Join(", ", hosts), Foreground = Brushes.Gray, MaxWidth = 520, TextTrimming = TextTrimming.CharacterEllipsis });
                        var sItem = new TreeViewItem { Header = header, IsExpanded = filtering || (days == 0 && si == 0) };
                        var sMenu = new ContextMenu();
                        var openAll = new MenuItem { Header = "Otwórz wszystkie w nowych kartach" };
                        var urls = s.Entries.Select(x => x.Url).Distinct().Reverse().ToList();
                        openAll.Click += (a, b) =>
                        {
                            if (urls.Count > 15 && MessageBox.Show(win, "Otworzyć " + urls.Count + " kart?", "Historia", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                            foreach (var u in urls) AddTab(u);
                        };
                        var sLines = s.Entries.SelectMany(x => x.Lines).ToList();
                        var delS = new MenuItem { Header = "Usuń sesję z historii" };
                        delS.Click += (a, b) => { if (MessageBox.Show(win, "Usunąć tę sesję z historii?", "Historia", MessageBoxButton.YesNo) == MessageBoxResult.Yes) remove(sLines); };
                        sMenu.Items.Add(openAll); sMenu.Items.Add(new Separator()); sMenu.Items.Add(delS);
                        sItem.ContextMenu = sMenu;
                        foreach (var h in s.Entries) sItem.Items.Add(entryItem(h));
                        dayItem.Items.Add(sItem);
                        si++;
                    }
                    tree.Items.Add(dayItem);
                    days++;
                }
                status.Text = filtering ? "Znaleziono: " + Pages(shown.Count) : "Razem: " + Pages(all.Count);
                if (tree.Items.Count == 0)
                    tree.Items.Add(new TreeViewItem { Header = new TextBlock { Text = filtering ? "Nic nie znaleziono." : "Historia jest pusta.", Foreground = Brushes.Gray, Margin = new Thickness(6) } });
            };

            search.TextChanged += (a, b) => { hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; rebuild(); };
            win.PreviewKeyDown += (a, b) => { if (b.Key == Key.Escape) win.Close(); };

            var clearAll = SmallButton("Wyczyść całą historię", () =>
            {
                if (MessageBox.Show(win, "Usunąć całą historię?", "Historia", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                try { File.Delete(HistoryFile); } catch (IOException) { }
                rebuild();
            });
            var bottom = new DockPanel { Margin = new Thickness(6) };
            DockPanel.SetDock(clearAll, Dock.Right);
            bottom.Children.Add(clearAll); bottom.Children.Add(status);
            bottom.Children.Add(new TextBlock { Text = "Dwuklik otwiera stronę · prawy klik: więcej opcji", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right });

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
