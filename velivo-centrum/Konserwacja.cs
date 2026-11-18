using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Velivo;

namespace VelivoCentrum
{
    // 🧹 centrum konserwacji: porządki osobno dla każdego programu Velivo, kopia bezpieczeństwa (robi ją Velivo - ten sam kod co w 🧹 przeglądarki),
    // przywracanie, zamykanie programów i procesów pomocniczych. Okno poziome - mieści się na ekranie bez przewijania całości.
    sealed partial class Centrum
    {
        static string Dane
        {
            get
            {
                try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Velivo")) if (k?.GetValue("Dane") is string d && Directory.Exists(d)) return d; } catch (Exception) { }
                return Path.Combine(KopiaSkladniki.Lokal, "Dane");
            }
        }
        static string KopieDir { get { var d = KopiaSkladniki.Get("kopia-folder"); return string.IsNullOrWhiteSpace(d) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Velivo - kopie") : d; } }
        const string Prefix = "Velivo-kopia-";
        static bool Przyrost(string f) => Path.GetFileNameWithoutExtension(f).EndsWith("-przyrost", StringComparison.OrdinalIgnoreCase);
        static string Mb(long b) => b >= 1L << 30 ? (b / 1073741824.0).ToString("0.0") + " GB" : (b / 1048576.0).ToString(b < 10L << 20 ? "0.0" : "0") + " MB";
        static readonly Brush Card = new SolidColorBrush(Color.FromRgb(0x1B, 0x21, 0x30)), Field = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x1B)),
            Accent = new SolidColorBrush(Color.FromRgb(0x6A, 0x3B, 0xE0)), Red = new SolidColorBrush(Color.FromRgb(0x99, 0x1B, 0x1B)), Warn = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5));

        static TextBlock Txt(string t, double size = 13, Brush c = null, bool bold = false) => new TextBlock { Text = t, FontSize = size, Foreground = c ?? Fg, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        static Button Btn(string t, Action a, Brush bg = null)
        {
            var b = new Button { Content = t, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 6), Foreground = Fg, Background = bg ?? Hover, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            b.Click += (s, e) => a(); return b;
        }
        static CheckBox Box(bool on, object content) => new CheckBox { IsChecked = on, Content = content, Foreground = Fg, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };

        Window kon;
        void Konserwacja()
        {
            if (kon != null) { if (kon.WindowState == WindowState.Minimized) kon.WindowState = WindowState.Normal; kon.Activate(); return; }
            var wa = SystemParameters.WorkArea;
            kon = new Window { Title = "Velivo Centrum – porządki i kopia", Width = Math.Min(1240, wa.Width - 40), Height = Math.Min(760, wa.Height - 40), WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Bg, Foreground = Fg, Icon = IconOf(ExePath) };
            kon.Closed += (s, e) => kon = null;
            var grid = new Grid { Margin = new Thickness(16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            var left = Porzadki(); var right = Kopia();
            Grid.SetColumn(left, 0); Grid.SetColumn(right, 2);
            grid.Children.Add(left); grid.Children.Add(new Border { Width = 1, Background = Line, HorizontalAlignment = HorizontalAlignment.Center }); Grid.SetColumn(grid.Children[1], 1);
            grid.Children.Add(right);
            kon.Content = grid; kon.Show(); kon.Activate();
        }

        // ---------- 🧹 porządki ----------
        sealed class Pozycja { public KopiaSkladniki.Skladnik S; public KopiaSkladniki.Czysc C; public CheckBox Box; public long Size; }
        FrameworkElement Porzadki()
        {
            var dock = new DockPanel();
            var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top);
            head.Children.Add(Txt("🧹 Porządki – każdy program osobno", 17, null, true));
            head.Children.Add(Txt("Ile zajmuje każdy program, co da się bezpiecznie wyczyścić (pobierze się albo zbuduje samo) i czy program trafia do kopii. Hasła, zakładki, rozmowy, biblioteka i ustawienia nigdy nie są tu usuwane.", 11.5, Dim));
            dock.Children.Add(head);
            var foot = new DockPanel { Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(foot, Dock.Bottom);
            var sum = Txt("", 12, Dim); var clean = Btn("🧹 Wyczyść zaznaczone", () => { }, Accent);
            DockPanel.SetDock(clean, Dock.Right); foot.Children.Add(clean); foot.Children.Add(sum);
            dock.Children.Add(foot);
            var cards = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, VerticalAlignment = VerticalAlignment.Top };
            dock.Children.Add(new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0) });
            var poz = new List<Pozycja>();
            Action przelicz = () => { long z = poz.Where(p => p.Box.IsChecked == true).Sum(p => p.Size); sum.Text = "Zaznaczone do wyczyszczenia: " + Mb(z); clean.IsEnabled = z > 0; };
            Action wczytaj = null;
            wczytaj = () =>
            {
                cards.Children.Clear(); poz.Clear(); sum.Text = "Liczę, ile zajmują programy…"; clean.IsEnabled = false;
                Task.Run(() => Skanuj()).ContinueWith(t => kon?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (t.IsFaulted) { sum.Text = "Nie udało się policzyć: " + t.Exception.GetBaseException().Message; return; }
                    var (rozm, czysc) = t.Result;
                    foreach (var s in KopiaSkladniki.Wszystkie)
                    {
                        bool inst = KopiaSkladniki.Procesy(s).Any(pr => All.Any(a => a.Proc == pr && FindExe(a) != null));
                        rozm.TryGetValue(s.Id, out var size);
                        if (!inst && size == 0) continue;   // nie ma programu ani jego danych
                        var card = new StackPanel();
                        var top = new DockPanel();
                        var run = KopiaSkladniki.Procesy(s).Where(pr => Process.GetProcessesByName(pr).Length > 0).ToList();
                        var right = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(right, Dock.Right);
                        right.Children.Add(Txt(Mb(size), 12, Dim));
                        if (s.Id != "wspolne")
                        {
                            var wk = Box(KopiaSkladniki.WKopii(s.Id), "w kopii"); wk.Margin = new Thickness(12, 0, 0, 0);
                            wk.ToolTip = "Zaznaczone – dane tego programu trafiają do kopii bezpieczeństwa (pełnej i przyrostowej).";
                            var sid = s.Id; wk.Click += (o, e) => KopiaSkladniki.UstawWKopii(sid, wk.IsChecked == true);
                            right.Children.Add(wk);
                        }
                        else right.Children.Add(Txt("   zawsze w kopii", 12, Dim));
                        top.Children.Add(right);
                        var nm = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                        nm.Inlines.Add(new System.Windows.Documents.Run(s.Nazwa) { FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Fg });
                        if (run.Count > 0) nm.Inlines.Add(new System.Windows.Documents.Run("  ● działa") { Foreground = OnFg, FontSize = 11 });
                        top.Children.Add(nm);
                        card.Children.Add(top);
                        card.Children.Add(Txt(s.Opis, 11.5, Dim));
                        if (s.Sprzatanie.Length == 0) card.Children.Add(Txt("Nic do czyszczenia – same ustawienia.", 11.5, Dim));
                        foreach (var c in s.Sprzatanie)
                        {
                            czysc.TryGetValue(s.Id + "|" + c.Nazwa, out var cs);
                            var row = new DockPanel();
                            var sz = Txt(Mb(cs), 12, Dim); DockPanel.SetDock(sz, Dock.Right); row.Children.Add(sz);
                            var lab = new TextBlock { TextWrapping = TextWrapping.Wrap };
                            lab.Inlines.Add(new System.Windows.Documents.Run(c.Nazwa) { Foreground = Fg }); lab.Inlines.Add(new System.Windows.Documents.Run("  – " + c.Opis) { Foreground = Dim, FontSize = 11 });
                            var cb = Box(c.Domyslnie && cs > 0, lab); cb.IsEnabled = cs > 0; cb.Click += (o, e) => przelicz();
                            row.Children.Add(cb); card.Children.Add(row);
                            poz.Add(new Pozycja { S = s, C = c, Box = cb, Size = cs });
                        }
                        cards.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 8, 8), Child = card });
                    }
                    przelicz();
                })));
            };
            clean.Click += async (o, e) =>
            {
                var wyb = poz.Where(p => p.Box.IsChecked == true).ToList(); if (wyb.Count == 0) return;
                // programy, które trzymają te pliki - zamykane na czas sprzątania i uruchamiane ponownie (inaczej część plików zostaje)
                var procs = wyb.SelectMany(p => KopiaSkladniki.Procesy(p.S)).Distinct().Where(pr => Process.GetProcessesByName(pr).Length > 0).ToList();
                var zamk = new List<Prog>();
                if (procs.Count > 0)
                {
                    var names = procs.Select(pr => All.FirstOrDefault(a => a.Proc == pr)?.Name ?? pr).ToList();
                    var r = MessageBox.Show(kon, "Tych plików używają działające programy:\n• " + string.Join("\n• ", names) + "\n\nZamknąć je na czas sprzątania i uruchomić ponownie? (Tak – wszystko się wyczyści; Nie – wyczyszczę tylko to, czego nie używają)", "Velivo Centrum", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (r == MessageBoxResult.Cancel) return;
                    if (r == MessageBoxResult.Yes) foreach (var pr in procs) { var p = All.FirstOrDefault(a => a.Proc == pr); if (p != null) { p.Exe = FindExe(p); Shut(p); zamk.Add(p); } }
                }
                clean.IsEnabled = false; sum.Text = "Czyszczę…";
                var (wolne, zostalo) = await Task.Run(() => Czysc(wyb.Select(p => (p.S, p.C)).ToList()));
                foreach (var p in zamk) if (p.Exe != null) Uruchom(p);
                MessageBox.Show(kon, "Zwolniono " + Mb(wolne) + "." + (zostalo > 0 ? "\n\n" + zostalo + " plików nie dało się usunąć (używa ich działający program albo Windows). Spróbuj ponownie po zamknięciu programów." : ""), "Velivo Centrum", MessageBoxButton.OK, MessageBoxImage.Information);
                wczytaj();
            };
            wczytaj();
            return dock;
        }

        // pliki: (ścieżka, ścieżka względna małymi literami, składnik) - dane Velivo i foldery Audio Balance / Monitora
        static IEnumerable<(string Full, string Rel, KopiaSkladniki.Skladnik S)> Pliki()
        {
            var wsp = KopiaSkladniki.Wszystkie.First(x => x.Id == "wspolne");
            IEnumerable<string> Enum(string d) { try { return Directory.Exists(d) ? Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>(); } catch (Exception) { return Enumerable.Empty<string>(); } }
            string Rel(string root, string f) => f.Substring(root.TrimEnd('\\').Length + 1).Replace('\\', '/').ToLowerInvariant();
            var dane = Dane;
            var kop = Path.GetFullPath(KopieDir).TrimEnd('\\') + "\\";
            foreach (var f in Enum(dane)) { if (f.StartsWith(kop, StringComparison.OrdinalIgnoreCase)) continue; var r = Rel(dane, f); yield return (f, r, KopiaSkladniki.Wlasciciel(r) ?? wsp); }
            foreach (var s in KopiaSkladniki.Wszystkie.Where(x => x.Inny != null)) { var d = KopiaSkladniki.FolderInny(s); foreach (var f in Enum(d)) yield return (f, Rel(d, f), s); }
        }
        static long Len(string f) { try { return new FileInfo(f).Length; } catch (Exception) { return 0; } }
        static (Dictionary<string, long>, Dictionary<string, long>) Skanuj()
        {
            var rozm = new Dictionary<string, long>(); var czysc = new Dictionary<string, long>();
            foreach (var (f, r, s) in Pliki())
            {
                long n = Len(f); rozm[s.Id] = (rozm.TryGetValue(s.Id, out var a) ? a : 0) + n;
                var c = s.Sprzatanie.FirstOrDefault(x => x.Pasuje(r)); if (c == null) continue;
                var k = s.Id + "|" + c.Nazwa; czysc[k] = (czysc.TryGetValue(k, out var b) ? b : 0) + n;
            }
            return (rozm, czysc);
        }
        static (long, int) Czysc(List<(KopiaSkladniki.Skladnik S, KopiaSkladniki.Czysc C)> wyb)
        {
            long wolne = 0; int zostalo = 0;
            foreach (var (f, r, s) in Pliki().ToList())
            {
                var c = s.Sprzatanie.FirstOrDefault(x => x.Pasuje(r));
                if (c == null || !wyb.Any(w => w.S == s && w.C == c)) continue;
                long n = Len(f);
                try { File.Delete(f); wolne += n; } catch (Exception) { zostalo++; }
            }
            return (wolne, zostalo);
        }

        // ---------- 💾 kopia ----------
        FrameworkElement Kopia()
        {
            var sp = new DockPanel();
            var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); sp.Children.Add(top);
            top.Children.Add(Txt("💾 Kopia bezpieczeństwa", 17, null, true));
            top.Children.Add(Txt("Kopię robi Velivo (pełne, Przeglądarka, Odtwarzacz albo Klawiatura) – ten sam sposób co w 🧹 przeglądarki. Hasła w kopii zostają zaszyfrowane.", 11.5, Dim));
            var dirRow = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
            var dirBox = new TextBox { Text = KopieDir, IsReadOnly = true, Background = Field, Foreground = Fg, BorderBrush = Line, Padding = new Thickness(5), VerticalContentAlignment = VerticalAlignment.Center };
            var pick = Btn("Zmień folder…", () =>
            {
                using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = "Folder na kopie Velivo (najlepiej inny dysk albo pendrive)", SelectedPath = KopieDir })
                    if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) { KopiaSkladniki.Set("kopia-folder", d.SelectedPath); dirBox.Text = KopieDir; }
            });
            pick.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(pick, Dock.Right); dirRow.Children.Add(pick); dirRow.Children.Add(dirBox);
            top.Children.Add(dirRow);
            var autoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            autoRow.Children.Add(Txt("Automatycznie: ", 12.5));
            var keys = new[] { "1", "7", "0" }; var auto = new ComboBox { Width = 170, Margin = new Thickness(6, 0, 6, 0) };
            foreach (var t in new[] { "codziennie", "raz w tygodniu", "wyłączona" }) auto.Items.Add(t);
            auto.SelectedIndex = Math.Max(0, Array.IndexOf(keys, KopiaSkladniki.Get("kopia-auto") ?? "7"));
            auto.SelectionChanged += (s, e) => KopiaSkladniki.Set("kopia-auto", keys[Math.Max(0, auto.SelectedIndex)]);
            autoRow.Children.Add(auto); autoRow.Children.Add(Txt("(zostaje 5 ostatnich pełnych)", 11.5, Dim));
            top.Children.Add(autoRow);
            var full = new RadioButton { Foreground = Fg, Margin = new Thickness(0, 2, 0, 2), Content = Txt("Pełna – zaznaczone programy w całości (także głosy lektora, narzędzia, program TV)", 12.5) };
            var data = new RadioButton { Foreground = Fg, Margin = new Thickness(0, 2, 0, 2), Content = Txt("Podstawowa – same dane: hasła, zakładki, ustawienia, rozmowy, biblioteka (resztę Velivo pobierze)", 12.5) };
            full.IsChecked = KopiaSkladniki.Get("kopia-pelna") != "0"; data.IsChecked = !full.IsChecked;
            full.Checked += (s, e) => KopiaSkladniki.Set("kopia-pelna", "1"); data.Checked += (s, e) => KopiaSkladniki.Set("kopia-pelna", "0");
            top.Children.Add(full); top.Children.Add(data);
            var inc = Box(KopiaSkladniki.Get("kopia-przyrost") != "0", Txt("Kopie przyrostowe – pełna co 7 dni, pozostałe tylko ze zmianami (zwykle kilka MB)", 12.5));
            inc.Click += (s, e) => KopiaSkladniki.Set("kopia-przyrost", inc.IsChecked == true ? "1" : "0");
            top.Children.Add(inc);
            var hRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var hStan = Txt("", 12);
            Action hPokaz = () =>
            {
                var z = KopiaSkladniki.Get("zamki") ?? ""; bool on = z.Length > 0 && !string.IsNullOrEmpty(KopiaSkladniki.Get("klucz"));
                int keys = on ? (z.Length - z.Replace("\"c\":", "").Length) / 4 : 0; bool pw = z.Contains("\"p\":");
                hStan.Text = on ? "🔑 Przeniesienie włączone – hasło: " + (pw ? "ustawione" : "brak") + ", klucze sprzętowe: " + keys + ". Kopię przywrócisz na innym komputerze i koncie Windows razem z hasłami, rozmowami i słownikiem."
                    : "⚠ Przeniesienie wyłączone – zapisane hasła stron, rozmowy, słownik i konta (Jellyfin, synchronizacja) z kopii wrócą tylko na tym koncie Windows. Sejf trybu bankowego przenosi się zawsze (Twoje hasło albo klucz sprzętowy).";
                hStan.Foreground = on ? OnFg : Warn;
            };
            var hBtn = Btn("🔑 Hasło i klucze przeniesienia…", () => { if (Velivo("--kopia-zabezpiecz") == null) return; var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; int n = 0; t.Tick += (o, e) => { hPokaz(); if (++n > 300) t.Stop(); }; t.Start(); });
            hBtn.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(hBtn, Dock.Right); hRow.Children.Add(hBtn); hRow.Children.Add(hStan); hPokaz();
            top.Children.Add(hRow);

            var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); sp.Children.Add(bottom);
            var bar = new ProgressBar { Height = 6, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4), Foreground = Accent, Background = Field, BorderThickness = new Thickness(0) };
            var stan = Txt("", 12, Dim);
            var btns = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            bottom.Children.Add(bar); bottom.Children.Add(stan); bottom.Children.Add(btns);

            var lista = new ListBox { Background = Field, Foreground = Fg, BorderBrush = Line, SelectionMode = SelectionMode.Extended, Margin = new Thickness(0, 10, 0, 0) };
            var mid = new DockPanel(); var lh = Txt("Kopie w folderze (zaznacz, aby przywrócić albo usunąć):", 12, Dim); DockPanel.SetDock(lh, Dock.Top); lh.Margin = new Thickness(0, 10, 0, 0);
            mid.Children.Add(lh); mid.Children.Add(lista); sp.Children.Add(mid);

            Action odswiez = () =>
            {
                lista.Items.Clear();
                var fs = Directory.Exists(KopieDir) ? new DirectoryInfo(KopieDir).GetFiles(Prefix + "*.zip").OrderByDescending(x => x.Name).ToList() : new List<FileInfo>();
                foreach (var f in fs)
                {
                    var row = new DockPanel { Tag = f.FullName, Margin = new Thickness(2) };
                    var sz = Txt(Mb(f.Length), 12, Dim); DockPanel.SetDock(sz, Dock.Right); row.Children.Add(sz);
                    row.Children.Add(Txt(f.LastWriteTime.ToString("dd.MM.yyyy  HH:mm") + "   " + (Przyrost(f.Name) ? "przyrostowa" : "PEŁNA"), 12.5, Przyrost(f.Name) ? Fg : OnFg));
                    lista.Items.Add(row);
                }
                if (fs.Count == 0) stan.Text = "Jeszcze nie ma żadnej kopii w tym folderze.";
                else if (bar.Visibility != Visibility.Visible) stan.Text = "Kopii: " + fs.Count + " (" + fs.Count(x => !Przyrost(x.Name)) + " pełnych) · razem " + Mb(fs.Sum(x => x.Length)) + " · ostatnia " + fs[0].LastWriteTime.ToString("dd.MM.yyyy HH:mm");
            };
            List<string> Wybrane() => lista.SelectedItems.Cast<DockPanel>().Select(x => (string)x.Tag).ToList();

            DispatcherTimer poll = null; DateTime start = DateTime.MinValue;
            Action<string> zrob = rodzaj =>
            {
                var plik = Path.Combine(Dane, KopiaSkladniki.StanPlik); try { File.Delete(plik); } catch (Exception) { }
                var exe = Velivo("--kopia " + rodzaj); if (exe == null) return;
                bar.Visibility = Visibility.Visible; foreach (Button b in btns.Children) b.IsEnabled = false;
                stan.Text = (rodzaj == "pelna" ? "Pełna kopia" : "Kopia przyrostowa") + " – robi ją " + exe.Name + "…"; start = DateTime.Now;
                poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                poll.Tick += (s, e) =>
                {
                    string t = null; try { if (File.Exists(plik)) t = File.ReadAllText(plik); } catch (Exception) { }
                    var p = (t ?? "").Split('|');
                    if (p[0] == "trwa") { stan.Text = "Tworzę kopię… " + p.ElementAtOrDefault(1) + " plików"; return; }
                    if (p[0] != "gotowe" && p[0] != "blad")
                    {
                        if (DateTime.Now - start < TimeSpan.FromSeconds(90)) return;
                        p = new[] { "blad", exe.Name + " nie odpowiada – zaktualizuj go do najnowszej wersji (starsza nie zna kopii z Centrum)." };
                    }
                    poll.Stop(); bar.Visibility = Visibility.Collapsed; foreach (Button b in btns.Children) b.IsEnabled = true;
                    odswiez();
                    stan.Text = p[0] == "gotowe" ? "✅ Kopia gotowa: " + Path.GetFileName(p[1]) + " · " + p.ElementAtOrDefault(2) + " plików · " + Mb(Len(p[1])) : "❌ Nie udało się: " + p.ElementAtOrDefault(1);
                    stan.Foreground = p[0] == "gotowe" ? OnFg : Warn;
                };
                poll.Start();
            };
            btns.Children.Add(Btn("＋ Kopia przyrostowa teraz", () => zrob("przyrost")));
            btns.Children.Add(Btn("⬛ Pełna kopia teraz", () => zrob("pelna")));
            btns.Children.Add(Btn("♻ Przywróć zaznaczoną…", () => Przywroc(Wybrane())));
            btns.Children.Add(Btn("🗑 Usuń zaznaczone", () => { if (UsunKopie(Wybrane())) odswiez(); }, Red));
            btns.Children.Add(Btn("📂 Folder kopii", () => { try { Directory.CreateDirectory(KopieDir); Process.Start("explorer.exe", "\"" + KopieDir + "\""); } catch (Exception) { } }));
            odswiez();
            return sp;
        }

        // usuwanie: pełna kopia razem z przyrostami, które z niej wychodzą (bez niej nie dałyby się przywrócić)
        bool UsunKopie(List<string> wyb)
        {
            if (wyb.Count == 0) { MessageBox.Show(kon, "Zaznacz kopię na liście.", "Velivo Centrum"); return false; }
            var all = Directory.GetFiles(KopieDir, Prefix + "*.zip").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
            var del = new HashSet<string>(wyb, StringComparer.OrdinalIgnoreCase);
            foreach (var f in wyb.Where(x => !Przyrost(x)))
                foreach (var g in all.SkipWhile(x => !string.Equals(x, f, StringComparison.OrdinalIgnoreCase)).Skip(1).TakeWhile(Przyrost)) del.Add(g);
            long sz = del.Sum(Len);
            if (MessageBox.Show(kon, "Usunąć " + del.Count + " kopii (" + Mb(sz) + ")?" + (del.Count > wyb.Count ? "\n\nRazem z przyrostowymi, które wychodzą z usuwanej pełnej – bez niej nie dałyby się przywrócić." : ""), "Velivo Centrum", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            int bl = 0; foreach (var f in del) try { File.Delete(f); } catch (Exception) { bl++; }
            if (bl > 0) MessageBox.Show(kon, bl + " kopii nie dało się usunąć (otwarte w innym programie?).", "Velivo Centrum");
            return true;
        }

        // przywracanie: pełna + przyrosty do wybranej; Velivo przywraca przy starcie (zanim otworzy pliki) - wszystkie programy Velivo zamknięte
        void Przywroc(List<string> wyb)
        {
            if (wyb.Count != 1) { MessageBox.Show(kon, "Zaznacz jedną kopię do przywrócenia.", "Velivo Centrum"); return; }
            var chosen = wyb[0];
            if (VelivoExe() == null) { MessageBox.Show(kon, "Przywraca Velivo, a nie znalazłem żadnego programu Velivo – zainstaluj najpierw Velivo (instalator jest w kopii, folder _Instalator).", "Velivo Centrum"); return; }
            if (MessageBox.Show(kon, "Zamknąć wszystkie programy Velivo i przywrócić kopię " + Path.GetFileName(chosen) + "?\n\nVelivo zapyta jeszcze o potwierdzenie i – dla kopii z innego komputera – o hasło albo klucz sprzętowy przeniesienia. Pozostałe programy uruchomisz potem przyciskiem „Uruchom wszystkie”.", "Velivo Centrum", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            foreach (var p in All) { p.Exe = FindExe(p); if (p.Exe != null && Running(p)) Shut(p); }
            Velivo("--przywroc \"" + chosen + "\"");
        }

        // program Velivo, który robi kopie i przywraca (działający najpierw)
        static Prog VelivoExe() => new[] { "Velivo", "VelivoPrzegladarka", "VelivoOdtwarzacz", "VelivoKlawiatura" }.Select(pid => All.First(a => a.Pid == pid)).Select(p => { p.Exe = FindExe(p); return p; }).Where(p => p.Exe != null).OrderByDescending(Running).FirstOrDefault();
        Prog Velivo(string args)
        {
            var p = VelivoExe();
            if (p == null) { MessageBox.Show(kon, "Nie znalazłem żadnego programu Velivo (pełne, Przeglądarka, Odtwarzacz, Klawiatura).", "Velivo Centrum"); return null; }
            try { Process.Start(new ProcessStartInfo(p.Exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(p.Exe) }); return p; }
            catch (Exception ex) { MessageBox.Show(kon, "Nie udało się uruchomić " + p.Name + ": " + ex.Message, "Velivo Centrum"); return null; }
        }

        // ---------- ✕ zamykanie: wybrane programy i procesy pomocnicze ----------
        static List<Process> Pomocnicze(string grupa)
        {
            var r = new List<Process>(); var lok = KopiaSkladniki.Lokal;
            string[] nazwy = grupa == "lektor" ? new[] { "piper", "python" } : grupa == "pobieranie" ? new[] { "ffmpeg", "yt-dlp" } : new string[0];
            foreach (var n in nazwy) foreach (var p in Process.GetProcessesByName(n)) { string f = null; try { f = p.MainModule.FileName; } catch (Exception) { } if (f != null && f.StartsWith(lok, StringComparison.OrdinalIgnoreCase)) r.Add(p); else p.Dispose(); }
            if (grupa == "strony")
                try
                {   // silnik stron (WebView2) z folderem danych Velivo - nie ruszamy Edge ani innych programów
                    using (var q = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'"))
                        foreach (System.Management.ManagementObject o in q.Get())
                            if (((o["CommandLine"] as string) ?? "").IndexOf(lok, StringComparison.OrdinalIgnoreCase) >= 0) try { r.Add(Process.GetProcessById(Convert.ToInt32(o["ProcessId"]))); } catch (Exception) { }
                }
                catch (Exception) { }
            return r;
        }
        void ZamknijWybor()
        {
            var w = new Window { Title = "Zamknij programy Velivo", Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Bg, Foreground = Fg, Topmost = true, Icon = IconOf(ExePath) };
            var sp = new StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(Txt("Zaznacz, co zamknąć. Działające są zaznaczone.", 12, Dim));
            sp.Children.Add(Txt("PROGRAMY", 11, Dim)); ((TextBlock)sp.Children[1]).Margin = new Thickness(0, 8, 0, 2);
            var progs = new List<(CheckBox, Prog)>();
            foreach (var p in All)
            {
                p.Exe = FindExe(p); if (p.Exe == null) continue;
                bool run = Running(p); long mem = 0;
                foreach (var x in Process.GetProcessesByName(p.Proc)) using (x) try { mem += x.WorkingSet64; } catch (Exception) { }
                var cb = Box(run, p.Name + (run ? "   (" + Mb(mem) + ")" : "   – wyłączony")); cb.IsEnabled = run;
                progs.Add((cb, p)); sp.Children.Add(cb);
            }
            var ph = Txt("PROCESY POMOCNICZE (zostają, gdy program zamknie się nieczysto)", 11, Dim); ph.Margin = new Thickness(0, 10, 0, 2); sp.Children.Add(ph);
            var helpers = new List<(CheckBox, string)>();
            foreach (var (g, n) in new[] { ("strony", "Silnik stron (WebView2 Velivo)"), ("lektor", "Lektor (Piper, Python XTTS)"), ("pobieranie", "Pobieranie (ffmpeg, yt-dlp)") })
            {
                var ps = Pomocnicze(g); int c = ps.Count; foreach (var x in ps) x.Dispose();
                var cb = Box(c > 0, n + (c > 0 ? "   (" + c + (c == 1 ? " proces)" : " procesy)") : "   – brak")); cb.IsEnabled = c > 0;
                helpers.Add((cb, g)); sp.Children.Add(cb);
            }
            sp.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 8, 0, 6) });
            var self = Box(false, "Velivo Centrum (ta lista przy zegarze)"); sp.Children.Add(self);
            var row = new UniformGridLite(Btn("Zaznacz wszystko", () => { foreach (var (cb, _) in progs) if (cb.IsEnabled) cb.IsChecked = true; foreach (var (cb, _) in helpers) if (cb.IsEnabled) cb.IsChecked = true; }),
                Btn("Zamknij zaznaczone", () =>
                {
                    foreach (var (cb, p) in progs) if (cb.IsChecked == true) Shut(p);
                    foreach (var (cb, g) in helpers) if (cb.IsChecked == true) foreach (var x in Pomocnicze(g)) using (x) try { x.Kill(); } catch (Exception) { }
                    w.Close();
                    if (self.IsChecked == true) { tray.Visible = false; tray.Dispose(); Application.Current.Shutdown(); }
                }, Red));
            row.Margin = new Thickness(0, 10, 0, 0); sp.Children.Add(row);
            w.Content = sp; w.Show(); w.Activate();
        }
    }
}
