using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Karty: wyciszanie, automatyczne odswiezanie, zapisane zestawy kart i kolorowe grupy kart.
    public partial class MainWindow
    {
        // ================= dzwiek karty =================

        void HookTabSound(BrowserTab tab, CoreWebView2 core)
        {
            try
            {
                core.IsDocumentPlayingAudioChanged += (s, e) => UpdateTabSound(tab);
                core.IsMutedChanged += (s, e) => UpdateTabSound(tab);
            }
            catch (Exception) { }
        }

        void UpdateTabSound(BrowserTab tab)
        {
            var core = tab.View.CoreWebView2;
            if (core == null || tab.SoundBtn == null) return;
            bool muted, playing;
            try { muted = core.IsMuted; playing = core.IsDocumentPlayingAudio; } catch (Exception) { return; }
            tab.SoundBtn.Visibility = muted || playing ? Visibility.Visible : Visibility.Collapsed;
            tab.SoundBtn.Content = muted ? "🔇" : "🔊";
            tab.SoundBtn.ToolTip = muted ? L.T("Włącz dźwięk karty") : L.T("Wycisz kartę");
        }

        // Windows: urzadzenie wyjsciowe dla kazdej aplikacji osobno (Mikser glosnosci). Przegladarka nie moze sama
        // wybrac glosnikow - tu ustawiasz Velivo / Microsoft Edge WebView2 na stale na wybrane glosniki.
        void OpenAppAudioSettings()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true }); }
            catch (Exception) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sndvol.exe") { UseShellExecute = true }); } catch (Exception ex) { App.LogError(ex); } }
            ShowToast(L.T("Przy „Velivo” lub „Microsoft Edge WebView2” wybierz swoje głośniki zamiast „Domyślne” – Windows zapamięta to na stałe."), null);
        }

        void ToggleTabMute(BrowserTab tab)
        {
            var core = tab.View.CoreWebView2;
            if (core == null) return;
            try { core.IsMuted = !core.IsMuted; } catch (Exception) { }
            UpdateTabSound(tab);
        }

        // ================= automatyczne odswiezanie =================

        void SetAutoRefresh(BrowserTab tab, int minutes)
        {
            if (tab.RefreshTimer != null) { tab.RefreshTimer.Stop(); tab.RefreshTimer = null; }
            tab.RefreshMinutes = minutes;
            if (minutes > 0)
            {
                tab.RefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
                tab.RefreshTimer.Tick += (s, e) =>
                {
                    if (!_tabs.Contains(tab)) { tab.RefreshTimer.Stop(); return; }
                    try { if (tab.View.CoreWebView2 != null) tab.View.CoreWebView2.Reload(); } catch (Exception) { }
                };
                tab.RefreshTimer.Start();
            }
            if (tab.RefreshMark != null)
            {
                tab.RefreshMark.Visibility = minutes > 0 ? Visibility.Visible : Visibility.Collapsed;
                tab.RefreshMark.ToolTip = minutes > 0 ? (L.En ? "Auto refresh every " + minutes + " min" : "Odświeżanie co " + minutes + " min") : null;
            }
        }

        MenuItem BuildAutoRefreshMenu(BrowserTab tab)
        {
            var root = new MenuItem { Header = L.T("Odświeżaj automatycznie") };
            foreach (var m in new[] { 0, 1, 5, 15, 30 })
            {
                int min = m;
                var it = new MenuItem { Header = min == 0 ? L.T("Wyłączone") : (L.En ? "Every " + min + " min" : "Co " + min + " min"), IsCheckable = true };
                it.Click += (s, e) => SetAutoRefresh(tab, min);
                root.Items.Add(it);
            }
            root.SubmenuOpened += (s, e) => { foreach (MenuItem it in root.Items) it.IsChecked = root.Items.IndexOf(it) == Array.IndexOf(new[] { 0, 1, 5, 15, 30 }, tab.RefreshMinutes); };
            return root;
        }

        // ================= zapisane zestawy kart =================

        static string TabSetsFile { get { return Path.Combine(DataDir, "zestawy-kart.json"); } }

        static Dictionary<string, List<string>> LoadTabSets()
        {
            try
            {
                if (File.Exists(TabSetsFile))
                    return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(TabSetsFile)) ?? new Dictionary<string, List<string>>();
            }
            catch (Exception) { }
            return new Dictionary<string, List<string>>();
        }

        static void SaveTabSets(Dictionary<string, List<string>> sets)
        {
            try { Directory.CreateDirectory(DataDir); File.WriteAllText(TabSetsFile, JsonSerializer.Serialize(sets, new JsonSerializerOptions { WriteIndented = true })); }
            catch (IOException) { }
        }

        MenuItem BuildTabSetsMenu()
        {
            var root = new MenuItem { Header = L.T("Zestawy kart") };
            root.Items.Add(new MenuItem());   // wypelniane przy otwarciu
            root.SubmenuOpened += (s, e) =>
            {
                if (e.OriginalSource != root) return;
                var stare = root.Items.Cast<object>().ToList();   // stare pozycje usuwamy na koncu - pusta lista zamyka podmenu (WPF)
                var save = new MenuItem { Header = L.T("Zapisz otwarte karty jako zestaw…") };
                save.Click += (a, b) =>
                {
                    var urls = _tabs.Where(t => !t.Private).Select(t => t.View.CoreWebView2 != null ? t.View.CoreWebView2.Source : t.StartUrl).Where(Restorable).Distinct().ToList();
                    if (urls.Count == 0) return;
                    var name = Prompt(L.T("Nazwa zestawu (np. Praca, Zakupy):"), "");
                    if (string.IsNullOrWhiteSpace(name)) return;
                    var sets = LoadTabSets();
                    sets[name.Trim()] = urls;
                    SaveTabSets(sets);
                    ShowToast((L.En ? "Saved set „" : "Zapisano zestaw „") + name.Trim() + "” (" + urls.Count + ")", null);
                };
                root.Items.Add(save);
                var all = LoadTabSets();
                if (all.Count > 0) root.Items.Add(new Separator());
                foreach (var kv in all.OrderBy(k => k.Key))
                {
                    var name = kv.Key; var urls = kv.Value ?? new List<string>();
                    var set = new MenuItem { Header = name + "  (" + urls.Count + ")" };
                    var open = new MenuItem { Header = L.T("Otwórz wszystkie karty") };
                    open.Click += (a, b) => { foreach (var u in urls.Where(Restorable)) AddTab(u); };
                    var update = new MenuItem { Header = L.T("Zastąp obecnymi kartami") };
                    update.Click += (a, b) =>
                    {
                        var sets = LoadTabSets();
                        sets[name] = _tabs.Where(t => !t.Private).Select(t => t.View.CoreWebView2 != null ? t.View.CoreWebView2.Source : t.StartUrl).Where(Restorable).Distinct().ToList();
                        SaveTabSets(sets);
                    };
                    var del = new MenuItem { Header = L.T("Usuń zestaw") };
                    del.Click += (a, b) =>
                    {
                        if (MessageBox.Show(this, (L.En ? "Delete set „" : "Usunąć zestaw „") + name + "”?", "Velivo", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                        var sets = LoadTabSets(); sets.Remove(name); SaveTabSets(sets);
                    };
                    set.Items.Add(open); set.Items.Add(update); set.Items.Add(new Separator()); set.Items.Add(del);
                    root.Items.Add(set);
                }
                foreach (var o in stare) root.Items.Remove(o);
            };
            return root;
        }

        // ================= grupy kart =================

        sealed class TabGroup
        {
            public string Name;
            public Color Color;
            public bool Collapsed;
            public Button Chip;
        }

        static readonly Color[] GroupColors =
        {
            Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x16, 0xA3, 0x4A), Color.FromRgb(0xEA, 0x58, 0x0C), Color.FromRgb(0x93, 0x33, 0xEA),
            Color.FromRgb(0xDC, 0x26, 0x26), Color.FromRgb(0x0D, 0x94, 0x88), Color.FromRgb(0xDB, 0x27, 0x77), Color.FromRgb(0x64, 0x74, 0x8B),
        };

        static readonly string[] GroupColorNames = { "niebieski", "zielony", "pomarańczowy", "fioletowy", "czerwony", "morski", "różowy", "szary" };

        readonly List<TabGroup> _groups = new List<TabGroup>();

        // Przenosi karte na podana pozycje (lista kart i pasek kart).
        void MoveTabTo(BrowserTab tab, int index)
        {
            _tabs.Remove(tab);
            index = Math.Max(0, Math.Min(index, _tabs.Count));
            _tabs.Insert(index, tab);
            TabStrip.Children.Remove(tab.Header);
            var before = index + 1 < _tabs.Count ? (UIElement)_tabs[index + 1].Header : NewTabBtn;
            TabStrip.Children.Insert(TabStrip.Children.IndexOf(before), tab.Header);
        }

        void AddTabToGroup(BrowserTab tab, TabGroup g)
        {
            if (tab.Pinned) return;
            if (!_groups.Contains(g)) _groups.Add(g);
            tab.Group = g;
            // karty grupy stoja obok siebie - nowa idzie za ostatnia karta grupy
            var members = _tabs.Where(t => t.Group == g && t != tab).ToList();
            if (members.Count > 0) MoveTabTo(tab, _tabs.IndexOf(members.Last()) + (_tabs.IndexOf(tab) > _tabs.IndexOf(members.Last()) ? 1 : 0));
            RefreshGroupsUi();
            SaveSessionSoon();
        }

        void RemoveTabFromGroup(BrowserTab tab)
        {
            var g = tab.Group;
            if (g == null) return;
            tab.Group = null;
            // karta wychodzi za grupe, zeby grupa zostala w jednym kawalku
            var members = _tabs.Where(t => t.Group == g).ToList();
            if (members.Count > 0 && _tabs.IndexOf(tab) < _tabs.IndexOf(members.Last())) MoveTabTo(tab, _tabs.IndexOf(members.Last()));
            RefreshGroupsUi();
            SaveSessionSoon();
        }

        void RefreshGroupsUi()
        {
            foreach (var g in _groups.ToList())
            {
                if (g.Chip != null) TabStrip.Children.Remove(g.Chip);
                if (!_tabs.Any(t => t.Group == g)) _groups.Remove(g);
            }
            foreach (var t in _tabs)
            {
                if (t.GroupDot == null) continue;
                t.GroupDot.Visibility = t.Group != null ? Visibility.Visible : Visibility.Collapsed;
                if (t.Group != null) t.GroupDot.Foreground = new SolidColorBrush(t.Group.Color);
                t.Header.Visibility = t.Group != null && t.Group.Collapsed && t != _current ? Visibility.Collapsed : Visibility.Visible;
            }
            foreach (var g in _groups)
            {
                var first = _tabs.First(t => t.Group == g);
                int count = _tabs.Count(t => t.Group == g);
                if (g.Chip == null) g.Chip = BuildGroupChip(g);
                g.Chip.Background = new SolidColorBrush(g.Color);
                g.Chip.Content = (g.Collapsed ? "▸ " : "▾ ") + g.Name + (g.Collapsed ? "  (" + count + ")" : "");
                TabStrip.Children.Insert(TabStrip.Children.IndexOf(first.Header), g.Chip);
            }
        }

        Button BuildGroupChip(TabGroup g)
        {
            var chip = new Button
            {
                Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 0, 10, 0),
                Height = 26, Margin = new Thickness(4, 7, 0, 0), BorderThickness = new Thickness(0), Width = double.NaN,
                ToolTip = L.T("Kliknij, aby zwinąć lub rozwinąć grupę · prawy przycisk: więcej opcji")
            };
            chip.Click += (s, e) => { g.Collapsed = !g.Collapsed; RefreshGroupsUi(); SaveSessionSoon(); };
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = L.T("Zmień nazwę grupy…") };
            rename.Click += (s, e) => { var n = Prompt(L.T("Nazwa grupy:"), g.Name); if (!string.IsNullOrWhiteSpace(n)) { g.Name = n.Trim(); RefreshGroupsUi(); SaveSessionSoon(); } };
            var color = new MenuItem { Header = L.T("Kolor") };
            for (int i = 0; i < GroupColors.Length; i++)
            {
                var c = GroupColors[i];
                var it = new MenuItem { Header = new TextBlock { Text = "●  " + L.T(GroupColorNames[i]), Foreground = new SolidColorBrush(c) } };
                it.Click += (s, e) => { g.Color = c; RefreshGroupsUi(); SaveSessionSoon(); };
                color.Items.Add(it);
            }
            var ungroup = new MenuItem { Header = L.T("Rozgrupuj (karty zostają)") };
            ungroup.Click += (s, e) => { foreach (var t in _tabs.Where(t => t.Group == g)) t.Group = null; RefreshGroupsUi(); SaveSessionSoon(); };
            var closeAll = new MenuItem { Header = L.T("Zamknij wszystkie karty grupy") };
            closeAll.Click += (s, e) =>
            {
                var members = _tabs.Where(t => t.Group == g).ToList();
                if (MessageBox.Show(this, (L.En ? "Close " : "Zamknąć ") + members.Count + (L.En ? " tabs?" : " kart?"), "Velivo", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                foreach (var t in members) CloseTab(t);
            };
            foreach (var m in new object[] { rename, color, new Separator(), ungroup, closeAll }) menu.Items.Add(m);
            chip.ContextMenu = menu;
            return chip;
        }

        MenuItem BuildGroupMenu(BrowserTab tab)
        {
            var root = new MenuItem { Header = L.T("Dodaj do grupy") };
            root.Items.Add(new MenuItem());
            root.SubmenuOpened += (s, e) =>
            {
                if (e.OriginalSource != root) return;
                var stare = root.Items.Cast<object>().ToList();   // stare pozycje usuwamy na koncu - pusta lista zamyka podmenu (WPF)
                var fresh = new MenuItem { Header = L.T("Nowa grupa…") };
                fresh.Click += (a, b) =>
                {
                    var name = Prompt(L.T("Nazwa grupy:"), "");
                    if (string.IsNullOrWhiteSpace(name)) return;
                    var g = new TabGroup { Name = name.Trim(), Color = GroupColors[_groups.Count % GroupColors.Length] };
                    AddTabToGroup(tab, g);
                };
                root.Items.Add(fresh);
                if (_groups.Count > 0) root.Items.Add(new Separator());
                foreach (var g0 in _groups)
                {
                    var g = g0;
                    var it = new MenuItem { Header = new TextBlock { Text = "●  " + g.Name, Foreground = new SolidColorBrush(g.Color) }, IsEnabled = tab.Group != g };
                    it.Click += (a, b) => AddTabToGroup(tab, g);
                    root.Items.Add(it);
                }
                if (tab.Group != null)
                {
                    root.Items.Add(new Separator());
                    var leave = new MenuItem { Header = L.T("Usuń z grupy") };
                    leave.Click += (a, b) => RemoveTabFromGroup(tab);
                    root.Items.Add(leave);
                }
                foreach (var o in stare) root.Items.Remove(o);
            };
            return root;
        }

        // Zapis grup razem z sesja: numer karty w pliku sesji, nazwa, kolor, zwinieta.
        static string GroupsFile { get { return SessionFile + ".grupy"; } }

        void SaveTabGroups(List<BrowserTab> savedTabs)
        {
            try
            {
                var lines = new List<string>();
                for (int i = 0; i < savedTabs.Count; i++)
                {
                    var g = savedTabs[i].Group;
                    if (g == null) continue;
                    lines.Add(i + "\t" + g.Name.Replace('\t', ' ') + "\t" + g.Color.R.ToString("X2") + g.Color.G.ToString("X2") + g.Color.B.ToString("X2") + "\t" + (g.Collapsed ? "1" : "0"));
                }
                if (lines.Count == 0) { if (File.Exists(GroupsFile)) File.Delete(GroupsFile); }
                else File.WriteAllLines(GroupsFile, lines);
            }
            catch (IOException) { }
        }

        void RestoreTabGroups(List<BrowserTab> sessionTabs)
        {
            try
            {
                if (!File.Exists(GroupsFile)) return;
                var byKey = new Dictionary<string, TabGroup>();
                foreach (var line in File.ReadAllLines(GroupsFile))
                {
                    var p = line.Split('\t');
                    int i;
                    if (p.Length < 4 || !int.TryParse(p[0], out i) || i < 0 || i >= sessionTabs.Count) continue;
                    var key = p[1] + "\t" + p[2];
                    TabGroup g;
                    if (!byKey.TryGetValue(key, out g))
                    {
                        var c = GroupColors[0];
                        try { c = (Color)ColorConverter.ConvertFromString("#" + p[2]); } catch (Exception) { }
                        g = new TabGroup { Name = p[1], Color = c, Collapsed = p[3] == "1" };
                        byKey[key] = g; _groups.Add(g);
                    }
                    sessionTabs[i].Group = g;
                }
                RefreshGroupsUi();
            }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
