using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // 🎛 VST3 w Audio Monitorze: lista wtyczek (kolejność, wł./wył., okno producenta, usuwanie) i zapis łańcucha ze stanem każdej wtyczki (vst3.json)
    public partial class MainWindow
    {
        sealed class VstItem { public string Path { get; set; } public string Cid { get; set; } public bool On { get; set; } = true; public string State { get; set; } }
        sealed class VstConf { public bool Active { get; set; } public List<VstItem> List { get; set; } = new List<VstItem>(); }
        static string VstFile => System.IO.Path.Combine(DataDir, "vst3.json");
        static bool _vstLoaded; static readonly List<string> _vstErrors = new List<string>();

        // przy pierwszym uruchomieniu odtwarzacza z Audio Balance - wczytanie łańcucha (wtyczki z błędem pomijane, powód w Błędach Velivo)
        // onlyIfActive: start odtwarzacza - wtyczki wczytywane tylko, gdy łańcuch jest aktywny (wyłączony VST3 nie ładuje obcych bibliotek)
        static void VstLoad(bool onlyIfActive = false)
        {
            if (_vstLoaded || !Edycja.Premium) return;   // 💎 VST3 - Premium (bezpłatne Velivo nie wczytuje wtyczek)
            VstConf c = null;
            try { if (System.IO.File.Exists(VstFile)) c = JsonSerializer.Deserialize<VstConf>(System.IO.File.ReadAllText(VstFile)); } catch (Exception ex) { App.LogError(ex); }
            if (c == null) { _vstLoaded = true; return; }
            if (onlyIfActive && !c.Active) return;
            _vstLoaded = true;
            // bezpiecznik: wtyczka, przy której Velivo padło w trakcie wczytywania, nie jest wczytywana ponownie (inaczej padałoby przy każdym starcie)
            var mark = System.IO.Path.Combine(DataDir, "vst3-wczytuje.txt"); string crashed = null;
            try { if (System.IO.File.Exists(mark)) { crashed = System.IO.File.ReadAllText(mark).Trim(); System.IO.File.Delete(mark); } } catch (Exception ex) { App.LogError(ex); }
            var list = new List<Vst3.Plugin>(); bool dropped = false;
            foreach (var it in c.List)
            {
                if (crashed != null && string.Equals(it.Path, crashed, StringComparison.OrdinalIgnoreCase))
                {
                    _vstErrors.Add(System.IO.Path.GetFileName(it.Path) + ": " + L.T("zamknęła Velivo przy wczytywaniu – usunięta z łańcucha"));
                    App.LogError(new Exception("VST3: wtyczka zamknęła Velivo przy wczytywaniu - usunięta: " + it.Path)); dropped = true; continue;
                }
                try
                {
                    try { System.IO.File.WriteAllText(mark, it.Path); } catch (Exception) { }
                    var p = Vst3.Plugin.Load(it.Path, it.Cid); p.On = it.On;
                    if (!string.IsNullOrEmpty(it.State)) p.SetState(Convert.FromBase64String(it.State));
                    list.Add(p);
                }
                catch (Exception ex) { _vstErrors.Add(System.IO.Path.GetFileName(it.Path) + ": " + ex.Message); App.LogError(new Exception("VST3: " + it.Path, ex)); }
                finally { try { System.IO.File.Delete(mark); } catch (Exception) { } }
            }
            Vst3.Chain = list.ToArray(); Vst3.Active = c.Active && list.Count > 0;
            if (dropped) VstSave();
        }

        // zapis łańcucha i stanu każdej wtyczki (ustawienia gałek, presety)
        static void VstSave()
        {
            if (!_vstLoaded) return;
            try
            {
                var c = new VstConf { Active = Vst3.Active };
                foreach (var p in Vst3.Chain) { byte[] st = null; try { st = p.GetState(); } catch (Exception ex) { App.LogError(ex); } c.List.Add(new VstItem { Path = p.Path, Cid = p.Cid, On = p.On, State = st == null ? null : Convert.ToBase64String(st) }); }
                AbWriteLatest(VstFile, JsonSerializer.Serialize(c));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // zmiana łańcucha w oknie - pod blokadą wątku dźwięku (blok dźwięku nie trafi na wtyczkę w trakcie usuwania)
        static void VstSet(Vst3.Plugin[] chain, Vst3.Plugin removed = null)
        {
            lock (Vst3.Lock) { Vst3.Chain = chain; if (chain.Length == 0) Vst3.Active = false; }
            removed?.Dispose(); VstSave();
        }

        // 🎁 darmowe efekty do pobrania (strony projektów) - Velivo ich nie dołącza (licencje), po instalacji same pojawiają się w „Dostępne”
        static readonly (string Name, string Desc, string Lic, string Url)[] VstFree =
        {
            ("ZL Equalizer", "Korektor dynamiczny z widmem – podbija lub ścisza wybrane pasma, np. dialogi, bas, syczenie. Najbliższy Ozone EQ.", "AGPL-3 (otwarty kod)", "https://zl-audio.github.io/plugins/zlequalizer/"),
            ("Airwindows Consolidated", "Setki prostych efektów w jednej wtyczce: korektory, kompresory, „ciepło” i przestrzeń dźwięku.", "MIT / GPL-3 (otwarty kod)", "https://www.airwindows.com/consolidated/"),
            ("LSP Plugins", "Pełny zestaw studyjny: korektory, kompresory, limitery, bramki szumów. Wersja na Windows po rejestracji w sklepie LSP.", "LGPL-3 (otwarty kod)", "https://lsp-plug.in/"),
        };

        void VstFreeWindow(Window owner)
        {
            var fg = Brushes.White; var dim = new SolidColorBrush(Color.FromRgb(0xA8, 0xAE, 0xC4)); dim.Freeze();
            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock { Text = L.T("Darmowe efekty do poprawiania dźwięku. Kliknij „Pobierz”, zainstaluj wtyczkę, a Velivo samo ją wykryje (Wtyczki VST3 → Dostępne). Twoje własne wtyczki (np. Ozone) działają tak jak dotąd."), Foreground = dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            foreach (var p in VstFree)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14), LastChildFill = true };
                var url = p.Url; var b = new Button { Content = L.T("Pobierz"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = url };
                b.Click += (s, e) => { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); AddTab(url, false); };
                DockPanel.SetDock(b, Dock.Right); row.Children.Add(b);
                var t = new StackPanel();
                t.Children.Add(new TextBlock { Text = p.Name, Foreground = fg, FontSize = 14, FontWeight = FontWeights.SemiBold });
                t.Children.Add(new TextBlock { Text = L.T(p.Desc), Foreground = fg, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
                t.Children.Add(new TextBlock { Text = L.T("Licencja: ") + p.Lic, Foreground = dim, FontSize = 11 });
                row.Children.Add(t); sp.Children.Add(row);
            }
            new Window { Title = L.T("Darmowe wtyczki VST3"), Uid = "own-theme", Owner = owner, Background = AbDark, Foreground = fg, Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = sp }.Show();   // own-theme: ciemne niezależnie od motywu okien
        }

        FrameworkElement VstPanel(Window owner, Brush fg, Brush dim, Func<string, Action, Button> mk)
        {
            VstLoad(); Vst3.EditorClosed = VstSave;   // ustawienia z okna wtyczki zapisane od razu po jego zamknięciu, nie dopiero przy zamknięciu odtwarzacza
            var root = new StackPanel();
            TextBlock T(string s, double size, Brush c) => new TextBlock { Text = s, FontSize = size, Foreground = c, TextWrapping = TextWrapping.Wrap };
            var on = new CheckBox { Content = L.T("VST3 aktywne – Twoje wtyczki przejmują przetwarzanie dźwięku filmu"), Foreground = fg, IsChecked = Vst3.Active, Margin = new Thickness(0, 0, 0, 4) };
            root.Children.Add(on);
            var note = T(L.T("Gdy VST3 jest aktywne: oryginał → Twoje wtyczki po kolei → głośność odtwarzacza. Korektor, dialogi i wyrównywanie Velivo są wtedy pominięte (bez podwójnego przetwarzania). Wtyczka, która się wysypie, może zamknąć Velivo."), 11, dim);
            note.Margin = new Thickness(0, 0, 0, 8); root.Children.Add(note);
            var errs = T("", 11, AbRed); root.Children.Add(errs);
            var list = new StackPanel { Margin = new Thickness(0, 0, 0, 8) }; root.Children.Add(list);
            void Refresh()
            {
                errs.Text = _vstErrors.Count == 0 ? "" : L.T("Nie wczytano: ") + string.Join("; ", _vstErrors); errs.Visibility = _vstErrors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                list.Children.Clear(); var ch = Vst3.Chain;
                if (ch.Length == 0) list.Children.Add(T(L.T("Brak wtyczek. Dodaj pierwszą przyciskiem poniżej."), 12, dim));
                for (int i = 0; i < ch.Length; i++)
                {
                    var p = ch[i]; int idx = i;
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                    var cb = new CheckBox { IsChecked = p.On, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Włącz / wyłącz tę wtyczkę") };
                    cb.Click += (s, e) => { p.On = cb.IsChecked == true; VstSave(); };
                    var btns = new StackPanel { Orientation = Orientation.Horizontal };
                    Button B(string t, string tip, Action a) { var b = mk(t, a); b.Margin = new Thickness(4, 0, 0, 0); b.Padding = new Thickness(8, 2, 8, 2); b.ToolTip = tip; btns.Children.Add(b); return b; }
                    B("▲", L.T("Wyżej w łańcuchu"), () => { if (idx == 0) return; var l = Vst3.Chain.ToList(); (l[idx - 1], l[idx]) = (l[idx], l[idx - 1]); VstSet(l.ToArray()); Refresh(); }).IsEnabled = idx > 0;
                    B("▼", L.T("Niżej w łańcuchu"), () => { var l = Vst3.Chain.ToList(); if (idx >= l.Count - 1) return; (l[idx + 1], l[idx]) = (l[idx], l[idx + 1]); VstSet(l.ToArray()); Refresh(); }).IsEnabled = idx < ch.Length - 1;
                    B(L.T("Okno"), L.T("Otwiera okno wtyczki (ustawienia producenta)"), () => { try { p.ShowEditor(p.Name); } catch (Exception ex) { ShowToast("🎛 " + ex.Message, null); } });
                    B(L.T("Usuń"), L.T("Usuwa wtyczkę z łańcucha"), () => { VstSet(Vst3.Chain.Where(x => x != p).ToArray(), p); Refresh(); on.IsChecked = Vst3.Active; });
                    DockPanel.SetDock(cb, Dock.Left); DockPanel.SetDock(btns, Dock.Right); row.Children.Add(cb); row.Children.Add(btns);
                    row.Children.Add(new TextBlock { Text = (idx + 1) + ". " + p.Name + (p.Latency > 0 ? "  (" + Math.Round(p.Latency * 1000.0 / AbProcessor.Fs) + " ms)" : ""), Foreground = fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = p.Path });
                    list.Children.Add(row);
                }
            }
            void Add(string path)
            {
                try
                {
                    var p = Vst3.Plugin.Load(path); VstSet(Vst3.Chain.Concat(new[] { p }).ToArray());
                    if (!Vst3.Active) { Vst3.Active = true; on.IsChecked = true; VstSave(); AbNeedsRestartHint(); }
                    Refresh();
                }
                catch (Exception ex) { App.LogError(new Exception("VST3: " + path, ex)); ShowToast(L.T("🎛 Nie udało się wczytać wtyczki: ") + ex.Message, null); }
            }
            on.Click += (s, e) =>
            {
                if (on.IsChecked == true && Vst3.Chain.Length == 0) { on.IsChecked = false; ShowToast(L.T("🎛 Najpierw dodaj wtyczkę"), null); return; }
                Vst3.Active = on.IsChecked == true; VstSave(); if (Vst3.Active) AbNeedsRestartHint();
            };
            var brow = new WrapPanel();
            var addScan = mk(L.T("Dodaj z folderu VST3…"), () => { });
            addScan.ToolTip = L.T("Wtyczki zainstalowane w Windows (Common Files\\VST3)");
            addScan.Click += (s, e) =>
            {
                bool In(string ff) => Vst3.Chain.Any(x => string.Equals(x.Path, ff, StringComparison.OrdinalIgnoreCase));
                // wykryte wtyczki w porządku: najpierw włączone w łańcuchu, potem dostępne - w każdej grupie alfabetycznie, kolejne okno nie otwiera się drugi raz
                var found = Vst3.Scan().GroupBy(x => System.IO.Path.GetFileNameWithoutExtension(x), StringComparer.OrdinalIgnoreCase).Select(g => g.FirstOrDefault(In) ?? g.First()).ToList(); var sp = new StackPanel { Margin = new Thickness(14) };   // ta sama wtyczka w dwóch folderach (Common Files i folder użytkownika) - raz
                void Build()
                {
                    sp.Children.Clear();
                    sp.Children.Add(T(L.T("Wykryte wtyczki VST3 na tym komputerze – zaznacz, aby włączyć w łańcuchu, odznacz, aby wyłączyć"), 11, dim));
                    if (found.Count == 0) { sp.Children.Add(T(L.T("Nie znaleziono wtyczek VST3 w folderze Common Files\\VST3"), 12, dim)); return; }
                    var sorted = found.OrderBy(x => System.IO.Path.GetFileNameWithoutExtension(x), StringComparer.CurrentCultureIgnoreCase).ToList();
                    foreach (var (head, group) in new[] { (L.T("Włączone w łańcuchu"), sorted.Where(In).ToList()), (L.T("Dostępne"), sorted.Where(x => !In(x)).ToList()) })
                    {
                        if (group.Count == 0) continue;
                        var h = T(head + " (" + group.Count + ")", 12, fg); h.FontWeight = FontWeights.SemiBold; h.Margin = new Thickness(0, 10, 0, 4); sp.Children.Add(h);
                        foreach (var f in group)
                        {
                            var ff = f; bool on0 = In(ff);
                            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
                            var cb = new CheckBox { IsChecked = on0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = L.T("Włącz / wyłącz tę wtyczkę") };
                            cb.Click += (a2, b2) =>
                            {
                                var p = Vst3.Chain.FirstOrDefault(x => string.Equals(x.Path, ff, StringComparison.OrdinalIgnoreCase));
                                if (cb.IsChecked == true) { if (p == null) Add(ff); } else if (p != null) { VstSet(Vst3.Chain.Where(x => x != p).ToArray(), p); Refresh(); on.IsChecked = Vst3.Active; }
                                Build();
                            };
                            var ed = mk(L.T("Ustaw…"), () => { var p = Vst3.Chain.FirstOrDefault(x => string.Equals(x.Path, ff, StringComparison.OrdinalIgnoreCase)); if (p == null) return; try { p.ShowEditor(p.Name); } catch (Exception ex) { ShowToast("🎛 " + ex.Message, null); } });
                            ed.Padding = new Thickness(10, 2, 10, 2); ed.Margin = new Thickness(8, 0, 0, 0); ed.ToolTip = L.T("Otwiera okno wtyczki (ustawienia producenta)"); ed.Visibility = on0 ? Visibility.Visible : Visibility.Hidden;
                            var name = new TextBlock { Text = System.IO.Path.GetFileNameWithoutExtension(f), Foreground = on0 ? fg : dim, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = f };
                            DockPanel.SetDock(cb, Dock.Left); DockPanel.SetDock(ed, Dock.Right);
                            row.Children.Add(cb); row.Children.Add(ed); row.Children.Add(name);
                            sp.Children.Add(row);
                        }
                    }
                }
                Build();
                new Window { Title = L.T("Wtyczki VST3"), Uid = "own-theme", Owner = Window.GetWindow(addScan), Background = AbDark, Foreground = fg, Width = 460, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.Show();   // own-theme: wspólny jasny motyw okien kasował ciemne tło (jasne napisy na białym)
            };
            var addFile = mk(L.T("Dodaj z pliku…"), () =>
            {
                var d = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Wtyczki VST3") + " (*.vst3)|*.vst3", Title = L.T("Wybierz wtyczkę VST3"), Multiselect = true, InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3") };
                if (d.ShowDialog(owner) == true) foreach (var f in d.FileNames) Add(f);
            });
            var addFree = mk(L.T("Darmowe wtyczki…"), () => VstFreeWindow(owner)); addFree.ToolTip = L.T("Darmowe efekty do poprawiania dźwięku – opis i strona do pobrania");
            brow.Children.Add(addScan); brow.Children.Add(addFile); brow.Children.Add(addFree); root.Children.Add(brow);
            Refresh();
            return root;
        }
    }
}
