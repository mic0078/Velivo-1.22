using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Velivo;

namespace VelivoKopia
{
    // 💾 Velivo Kopia (makieta: dokumentacja/makieta-kopia-velivo.html): kopia i przywracanie danych i ustawień wszystkich programów Velivo,
    // kopia sama wg harmonogramu (w tle przy zegarze, także gdy inne programy Velivo są zamknięte).
    // Kopię robi i przywraca silnik Velivo (ten sam co 🧹 w przeglądarce i Velivo Centrum: przyrosty, hasło przeniesienia, zamiana ścieżek konta) -
    // ten program wybiera, planuje i pokazuje. Każdy folder programu Velivo trafia do kopii sam (KopiaSkladniki.FolderyModulow) - nowy moduł bez zmian tutaj.
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            using (var one = new Mutex(true, @"Local\Velivo.Kopia", out bool first))
            {
                if (!first) { try { using (var ev = EventWaitHandle.OpenExisting(@"Local\Velivo.Kopia.Pokaz")) ev.Set(); } catch (Exception) { } return; }   // drugie uruchomienie (skrót) - pokaż okno działającego
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var o = new Okno(args.Contains("--zasobnik"));
                app.Startup += (s, e) => o.Start();
                app.Run();
            }
        }
    }

    sealed class Okno
    {
        static readonly Brush Bg = B(0x0B, 0x0E, 0x14), Win = B(0x11, 0x14, 0x1C), Card = B(0x14, 0x18, 0x21), Line = B(0x2A, 0x32, 0x40), Fg = B(0xE8, 0xEB, 0xF0), Dim = B(0x8A, 0x93, 0xA3),
            Ok = B(0x4A, 0xDE, 0x80), Warn = B(0xF5, 0xB5, 0x44), Hot = B(0xF8, 0x71, 0x71), InfoBg = B(0x0E, 0x21, 0x40), InfoLine = B(0x1E, 0x3A, 0x66), InfoFg = B(0xCF, 0xE3, 0xFF), Field = B(0x0B, 0x0E, 0x14);
        static readonly Brush Grad = new LinearGradientBrush(Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x22, 0xB8, 0xD8), 45), TabOn = new LinearGradientBrush(Color.FromRgb(0x2E, 0x1A, 0x4D), Color.FromRgb(0x0B, 0x3C, 0x48), 45);
        static SolidColorBrush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

        readonly bool ukryty; Window win; System.Windows.Forms.NotifyIcon tray;
        public Okno(bool zasobnik) { ukryty = zasobnik; }

        // ---------- dane i ustawienia (wspólne z Velivo i Velivo Centrum: rejestr HKCU\Software\Velivo\Kopia) ----------
        static string Dane { get { try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Velivo")) if (k?.GetValue("Dane") is string d && Directory.Exists(d)) return d; } catch (Exception) { } return Path.Combine(KopiaSkladniki.Lokal, "Dane"); } }
        static string KopieDir { get { var d = KopiaSkladniki.Get("kopia-folder"); return string.IsNullOrWhiteSpace(d) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Velivo - kopie") : d; } }
        const string Prefix = "Velivo-kopia-";
        static bool Przyrost(string f) => Path.GetFileNameWithoutExtension(f).EndsWith("-przyrost", StringComparison.OrdinalIgnoreCase);
        static string Mb(long b) => b >= 1L << 30 ? (b / 1073741824.0).ToString("0.0") + " GB" : b < 100L << 10 ? "< 0,1 MB" : (b / 1048576.0).ToString(b < 10L << 20 ? "0.0" : "0") + " MB";
        static int Dni => int.TryParse(KopiaSkladniki.Get("kopia-auto"), out var d) ? d : 7;   // jak w Velivo: domyślnie raz w tygodniu
        static int Godzina => int.TryParse(KopiaSkladniki.Get("kopia-godzina"), out var h) && h >= 0 && h < 24 ? h : -1;   // -1 = o dowolnej porze
        static List<FileInfo> Kopie() { try { return Directory.Exists(KopieDir) ? new DirectoryInfo(KopieDir).GetFiles(Prefix + "*.zip").OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList() : new List<FileInfo>(); } catch (Exception) { return new List<FileInfo>(); } }
        static DateTime Ostatnia()
        {
            DateTime last = DateTime.TryParse(KopiaSkladniki.Get("kopia-ostatnia"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var l) ? l : DateTime.MinValue;
            foreach (var z in Kopie()) if (z.LastWriteTime > last) last = z.LastWriteTime;   // data z samych kopii - wspólna dla Velivo, Centrum i tego programu
            return last;
        }
        static DateTime? Nastepna()
        {
            if (Dni <= 0) return null;
            var t = Ostatnia() == DateTime.MinValue ? DateTime.Now : Ostatnia().AddDays(Dni);
            if (Godzina >= 0) { var g = t.Date.AddHours(Godzina); t = g < t ? g.AddDays(1) : g; }
            return t;
        }

        // programy Velivo (spis instalatorów HKCU\Software\Velivo\Programy, zapasowo folder domyślny)
        static string Exe(string pid, string dir)
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Velivo\Programy\" + pid)) if (k?.GetValue("Exe") is string e && File.Exists(e)) return e; } catch (Exception) { }
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", dir, pid + ".exe");
            return File.Exists(d) ? d : null;
        }
        // silnik kopii: program Velivo, który ją robi (najpierw działający) - pełne Velivo, Przeglądarka, Odtwarzacz, Klawiatura
        static string Silnik()
        {
            var l = new[] { ("Velivo", "Velivo"), ("VelivoPrzegladarka", "Velivo Przeglądarka"), ("VelivoOdtwarzacz", "Velivo Odtwarzacz"), ("VelivoKlawiatura", "Velivo Klawiatura") }
                .Select(x => (Pid: x.Item1, Exe: Exe(x.Item1, x.Item2))).Where(x => x.Exe != null).ToList();
            return l.OrderByDescending(x => Process.GetProcessesByName(x.Pid).Length > 0).Select(x => x.Exe).FirstOrDefault();
        }
        static bool Uruchom(string args, Window owner)
        {
            var exe = Silnik();
            if (exe == null) { if (owner != null) MessageBox.Show(owner, "Kopię robi i przywraca Velivo, a nie znalazłem go na tym komputerze (pełne Velivo, Przeglądarka, Odtwarzacz albo Klawiatura). Zainstaluj najpierw pakiet Velivo.", "Velivo Kopia"); return false; }
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) }); return true; }
            catch (Exception ex) { if (owner != null) MessageBox.Show(owner, "Nie udało się uruchomić " + Path.GetFileName(exe) + ": " + ex.Message, "Velivo Kopia"); return false; }
        }

        // ---------- moduły w kopii ----------
        sealed class Modul { public string Id, Nazwa, Opis, Stan; public bool Jest; public Func<long> Rozmiar; }
        static readonly Dictionary<string, (string Nazwa, string Opis, string Pid, string Dir)> Foldery = new Dictionary<string, (string, string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Procesor"] = ("Velivo Procesor", "tryb, suwaki, nauczone programy (AI), Studio, priorytety – bez planu zasilania i limitów BIOS tego komputera", "VelivoProcesor", "Velivo Procesor"),
            ["Kursory"] = ("Velivo Kursory", "wybrany rozmiar i styl", "VelivoKursory", "Velivo Kursory"),
        };
        static readonly Dictionary<string, string> Dir = new Dictionary<string, string> { ["Velivo"] = "Velivo", ["VelivoPrzegladarka"] = "Velivo Przeglądarka", ["VelivoOdtwarzacz"] = "Velivo Odtwarzacz", ["VelivoKomunikator"] = "Velivo Komunikator",
            ["VelivoKlawiatura"] = "Velivo Klawiatura", ["VelivoAudioBalance"] = "Velivo Audio Balance for Windows", ["VelivoMonitor"] = "Velivo Monitor" };
        static long Suma(string root, Func<string, bool> pasuje)
        {
            long n = 0; if (!Directory.Exists(root)) return 0;
            try { foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { var r = f.Substring(root.Length).TrimStart('\\'); if (pasuje(r)) try { n += new FileInfo(f).Length; } catch (Exception) { } } } catch (Exception) { }
            return n;
        }
        static List<Modul> Moduly()
        {
            var l = new List<Modul>(); var dane = Dane;
            // folder danych przeglądany raz dla wszystkich programów, które w nim mają pliki (było: osobno dla każdego)
            var wDanych = new Lazy<Dictionary<string, long>>(() =>
            {
                var r = new Dictionary<string, long>();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dane, "*", SearchOption.AllDirectories))
                    {
                        var id = KopiaSkladniki.Wlasciciel(f.Substring(dane.Length).TrimStart('\\'))?.Id ?? "wspolne"; long n = 0;
                        try { n = new FileInfo(f).Length; } catch (Exception) { }
                        r[id] = (r.TryGetValue(id, out var v) ? v : 0) + n;
                    }
                }
                catch (Exception) { }
                return r;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            foreach (var s in KopiaSkladniki.Wszystkie)
            {
                bool jest = s.Proc == null || Dir.TryGetValue(s.Proc, out var dd) && Exe(s.Proc, dd) != null || s.Uzywaja.Any(u => Dir.TryGetValue(u, out var d2) && Exe(u, d2) != null);
                var sk = s;
                Func<long> roz = s.Inny != null ? (Func<long>)(() => Suma(KopiaSkladniki.FolderInny(sk), r => KopiaSkladniki.PlikModulu(sk.Inny, r)))
                    : () => wDanych.Value.TryGetValue(sk.Id, out var v) ? v : 0;
                l.Add(new Modul { Id = s.Id, Nazwa = s.Nazwa, Opis = s.Opis, Jest = jest, Stan = jest ? "zainstalowany" : "nie zainstalowany", Rozmiar = roz });
            }
            // pozostałe foldery programów Velivo (Procesor, Kursory, każdy nowy moduł) - także wyłączone z kopii, żeby dało się je włączyć
            string[] dirs; try { dirs = Directory.Exists(KopiaSkladniki.Lokal) ? Directory.GetDirectories(KopiaSkladniki.Lokal) : new string[0]; } catch (Exception) { dirs = new string[0]; }
            foreach (var d in dirs)
            {
                var n = Path.GetFileName(d); var dd = d;
                if (string.Equals(n, "Dane", StringComparison.OrdinalIgnoreCase) || n == "Poważne błędy" || n == "Instalacja" || KopiaSkladniki.Wszystkie.Any(x => string.Equals(x.Inny, n, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(Path.GetFullPath(d).TrimEnd('\\'), Path.GetFullPath(dane).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                bool znany = Foldery.TryGetValue(n, out var f);
                bool jest = !znany || Exe(f.Pid, f.Dir) != null;
                l.Add(new Modul { Id = "f:" + n, Nazwa = znany ? f.Nazwa : n, Opis = znany ? f.Opis : "dane programu Velivo (folder „" + n + "”)", Jest = jest, Stan = jest ? (znany ? "zainstalowany" : "dane są") : "dane są (program usunięty)", Rozmiar = () => Suma(dd, r => KopiaSkladniki.PlikModulu(n, r)) });
            }
            l.Add(new Modul { Id = null, Nazwa = "Ustawienia systemu", Opis = "które programy Velivo startują z Windows, przypięte w Centrum, ustawienia kopii", Jest = true, Stan = "—", Rozmiar = () => 0 });
            return l;
        }

        // ---------- start: zasobnik, harmonogram ----------
        public void Start()
        {
            if (KopiaSkladniki.Get("kopia-start-ustawiony") == null) { StartZWindows.Ustaw("VelivoKopia", "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\" --zasobnik"); KopiaSkladniki.Set("kopia-start-ustawiony", "1"); }   // kopia sama wymaga programu przy zegarze
            Zbuduj();
            tray = new System.Windows.Forms.NotifyIcon { Text = "Velivo Kopia", Visible = true };
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetEntryAssembly().Location); } catch (Exception) { tray.Icon = System.Drawing.SystemIcons.Application; }
            tray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Pokaz(); };
            var m = new System.Windows.Forms.ContextMenuStrip();
            m.Items.Add("💾 Otwórz Velivo Kopia", null, (s, e) => Pokaz());
            m.Items.Add("＋ Zrób kopię teraz", null, (s, e) => Zrob("auto", false));
            m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            m.Items.Add("✕ Zamknij Velivo Kopia", null, (s, e) => { tray.Visible = false; tray.Dispose(); Application.Current.Shutdown(); });
            tray.ContextMenuStrip = m;
            CentrumZasobnik.Podlacz(tray);
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Velivo.Kopia.Pokaz");
            new Thread(() => { while (ev.WaitOne()) win.Dispatcher.BeginInvoke(new Action(Pokaz)); }) { IsBackground = true }.Start();
            // ⏰ kopia sama: co minutę - termin minął (także po włączeniu komputera, gdy był wyłączony) i nadeszła wybrana godzina
            var zegar = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) }; DateTime proba = DateTime.MinValue;
            zegar.Tick += (s, e) =>
            {
                var n = Nastepna(); if (n == null || DateTime.Now < n.Value || trwa || (DateTime.Now - proba).TotalHours < 1) return;
                proba = DateTime.Now; Zrob("auto", true);
            };
            zegar.Start();
            if (!ukryty) Pokaz();
        }
        void Pokaz() { if (!win.IsVisible) { OdswiezKopie(); OdswiezModuly(); } win.Show(); if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal; win.Activate(); }

        // ---------- okno ----------
        Border tabKopia, tabPrzywroc; FrameworkElement panelKopia, panelPrzywroc; StackPanel modulyRows, kopieRows; TextBlock stan, planTxt, folderTxt, hasloTxt; ProgressBar bar; Button bAuto, bPelna;

        static TextBlock T(string t, double size = 13, Brush fg = null, FontWeight? w = null) => new TextBlock { Text = t, FontSize = size, Foreground = fg ?? Fg, FontWeight = w ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        static Border Sekcja(string tytul, params UIElement[] els)
        {
            var sp = new StackPanel(); sp.Children.Add(T(tytul, 13, Fg, FontWeights.SemiBold)); foreach (var e in els) sp.Children.Add(e);
            return new Border { Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 12, 0, 0), Child = sp };
        }
        static Border Info(string t) => new Border { Background = InfoBg, BorderBrush = InfoLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10), Child = T(t, 13, InfoFg) };
        // przycisk Velivo: własny szablon (bez białego tła Windows po najechaniu / wyłączeniu)
        static Button Btn(string t, Action a, bool glowny = false)
        {
            var bd = new FrameworkElementFactory(typeof(Border)); bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); bd.SetValue(Border.PaddingProperty, new Thickness(14, 6, 14, 6));
            bd.SetValue(Border.BackgroundProperty, glowny ? Grad : B(0x1B, 0x20, 0x30)); bd.SetValue(Border.BorderBrushProperty, glowny ? Brushes.Transparent : Line); bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center); bd.AppendChild(cp);
            var tpl = new ControlTemplate(typeof(Button)) { VisualTree = bd };
            var tr = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; tr.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85)); tpl.Triggers.Add(tr);
            var tr2 = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; tr2.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45)); tpl.Triggers.Add(tr2);
            var b = new Button { Content = new TextBlock { Text = t, Foreground = Fg, FontWeight = glowny ? FontWeights.SemiBold : FontWeights.Normal }, Template = tpl, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(8, 0, 0, 0) };
            b.Click += (s, e) => a(); return b;
        }
        static Border Pole(TextBlock t) => new Border { Background = Field, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 5), Child = t };
        // lista wyboru w stylu Velivo (zwykły ComboBox Windows jest biały na ciemnym oknie): przycisk z bieżącym wyborem, kliknięcie - menu z listą
        static Button Lista(string[] items, int sel, Action<int> zmiana)
        {
            sel = Math.Max(0, Math.Min(items.Length - 1, sel));
            var txt = new TextBlock { Text = items[sel] + "  ▾", Foreground = Fg };
            Button b = null; b = Btn("", () => { b.ContextMenu.PlacementTarget = b; b.ContextMenu.IsOpen = true; });
            b.Content = txt; b.Margin = new Thickness(0, 0, 8, 0); b.MinWidth = 150;
            var cm = new ContextMenu { Background = Card, Foreground = Fg, BorderBrush = Line };
            for (int i = 0; i < items.Length; i++)
            {
                int k = i; var mi = new MenuItem { Header = items[i], Background = Card, Foreground = Fg };
                mi.Click += (s, e) => { txt.Text = items[k] + "  ▾"; zmiana(k); };
                cm.Items.Add(mi);
            }
            b.ContextMenu = cm; return b;
        }

        void Zbuduj()
        {
            win = new Window { Title = "Velivo Kopia", Width = 820, Height = Math.Min(900, SystemParameters.WorkArea.Height - 40), MinWidth = 560, Background = Win, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            try { win.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kopia.ico"))); } catch (Exception) { }
            win.Closing += (s, e) => { e.Cancel = true; win.Hide(); };   // zamknięcie okna = zostaje przy zegarze (kopia sama); całkiem - menu ikony
            var dock = new DockPanel();
            // nagłówek: logo, nazwa, zakładki
            var head = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
            var logo = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = Grad, Child = new TextBlock { Text = "V", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            head.Children.Add(logo);
            var title = T("Velivo Kopia", 17, Fg, FontWeights.SemiBold); title.Margin = new Thickness(10, 0, 0, 0); title.VerticalAlignment = VerticalAlignment.Center;
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            tabKopia = Zakladka("💾 Zrób kopię", () => Przelacz(true)); tabPrzywroc = Zakladka("♻ Przywróć", () => Przelacz(false));
            tabs.Children.Add(tabKopia); tabs.Children.Add(tabPrzywroc); DockPanel.SetDock(tabs, Dock.Right); head.Children.Add(tabs); head.Children.Add(title);
            var headB = new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = head }; DockPanel.SetDock(headB, Dock.Top); dock.Children.Add(headB);
            // stopka: postęp i przyciski
            var foot = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
            bAuto = Btn("💾 Zrób kopię", () => Zrob("auto", false), true); bPelna = Btn("⬛ Pełna kopia", () => Zrob("pelna", false));
            bAuto.ToolTip = "Przyrostowa (same zmiany), gdy włączona i jest świeża pełna kopia – inaczej pełna"; bPelna.ToolTip = "Pełna kopia teraz – wszystkie dane od nowa";
            var fb = new StackPanel { Orientation = Orientation.Horizontal }; fb.Children.Add(bPelna); fb.Children.Add(bAuto); DockPanel.SetDock(fb, Dock.Right); foot.Children.Add(fb);
            bar = new ProgressBar { Height = 8, Width = 180, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 10, 0), Foreground = Grad, Background = B(0x22, 0x2A, 0x38), BorderThickness = new Thickness(0) };
            DockPanel.SetDock(bar, Dock.Left); foot.Children.Add(bar);
            stan = T("", 12.5, Dim); stan.VerticalAlignment = VerticalAlignment.Center; foot.Children.Add(stan);
            var footB = new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = foot }; DockPanel.SetDock(footB, Dock.Bottom); dock.Children.Add(footB);
            panelKopia = PanelKopia(); panelPrzywroc = PanelPrzywroc();
            var body = new Grid(); body.Children.Add(panelKopia); body.Children.Add(panelPrzywroc); dock.Children.Add(body);
            win.Content = dock;
            Przelacz(true);
        }
        Border Zakladka(string t, Action a)
        {
            var b = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand, Child = T(t, 13) };
            b.MouseLeftButtonUp += (s, e) => a(); return b;
        }
        void Przelacz(bool kopia)
        {
            panelKopia.Visibility = kopia ? Visibility.Visible : Visibility.Collapsed; panelPrzywroc.Visibility = kopia ? Visibility.Collapsed : Visibility.Visible;
            foreach (var (t, on) in new[] { (tabKopia, kopia), (tabPrzywroc, !kopia) }) { t.Background = on ? TabOn : Brushes.Transparent; t.BorderBrush = on ? B(0x22, 0xB8, 0xD8) : Line; ((TextBlock)t.Child).Foreground = on ? Fg : Dim; }
            bAuto.Visibility = bPelna.Visibility = kopia ? Visibility.Visible : Visibility.Collapsed;
            if (win.IsVisible) { if (kopia) OdswiezModuly(); else OdswiezKopie(); }   // ukryte przy starcie z Windows - bez przeglądania dysku
        }

        // ---------- 💾 zrób kopię ----------
        FrameworkElement PanelKopia()
        {
            var sp = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            sp.Children.Add(Info("Zaznacz, co ma być w kopii. Pamięć podręczna, dzienniki i rzeczy, które pobiorą się same (lektor, ikony kanałów, narzędzia), są pomijane – kopia jest mała. Każdy nowy program Velivo trafia do kopii sam."));
            var hdr = Wiersz(T("Moduł", 12, Dim), T("Co trafia do kopii", 12, Dim), T("Stan", 12, Dim), T("Na dysku", 12, Dim));
            sp.Children.Add(hdr); modulyRows = new StackPanel(); sp.Children.Add(modulyRows);
            // 🔐 hasło przeniesienia (silnik Velivo: hasło albo klucz sprzętowy)
            hasloTxt = T("", 12.5, Dim);
            var hRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) }; var hb = Btn("🔐 Hasło i klucze przeniesienia…", () => { if (Uruchom("--kopia-zabezpiecz", win)) Opoznij(4, OdswiezHaslo); }); DockPanel.SetDock(hb, Dock.Right); hRow.Children.Add(hb); hRow.Children.Add(hasloTxt);
            sp.Children.Add(Sekcja("🔐 Hasło kopii", hRow, T("Hasła stron, klucze komunikatora i Serwera Windows chroni tylko na tym komputerze – w kopii zamyka je hasło przeniesienia (albo klucz sprzętowy), żeby dało się je otworzyć na nowym. Bez niego reszta wraca, a te dane nie.", 12, Dim)));
            // ⏰ kopia sama
            var co = Lista(new[] { "codziennie", "raz w tygodniu", "wyłączona" }, Dni == 1 ? 0 : Dni <= 0 ? 2 : 1, i => { KopiaSkladniki.Set("kopia-auto", i == 0 ? "1" : i == 1 ? "7" : "0"); OdswiezPlan(); });
            var godz = Lista(new[] { "o dowolnej porze" }.Concat(Enumerable.Range(0, 24).Select(h => "o " + h + ":00")).ToArray(), Godzina + 1, i => { KopiaSkladniki.Set("kopia-godzina", i == 0 ? "" : (i - 1).ToString()); OdswiezPlan(); });
            var r1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) }; r1.Children.Add(co); r1.Children.Add(godz); var rt = T("gdy komputer był wtedy wyłączony – zaraz po jego włączeniu", 12, Dim); rt.VerticalAlignment = VerticalAlignment.Center; r1.Children.Add(rt);
            var inc = new CheckBox { Content = T("Przyrostowa – pełna co 7 dni, pozostałe tylko ze zmianami (zwykle kilka MB)"), IsChecked = KopiaSkladniki.Get("kopia-przyrost") != "0", Margin = new Thickness(0, 8, 0, 0) };
            inc.Click += (s, e) => KopiaSkladniki.Set("kopia-przyrost", inc.IsChecked == true ? "1" : "0");
            var auto = new CheckBox { Content = T("Velivo Kopia startuje z Windows (potrzebne, żeby kopia robiła się sama)"), IsChecked = StartZWindows.Wlaczony("VelivoKopia"), Margin = new Thickness(0, 6, 0, 0) };
            auto.Click += (s, e) => StartZWindows.Ustaw("VelivoKopia", auto.IsChecked == true ? "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\" --zasobnik" : null);
            planTxt = T("", 12.5, Dim); planTxt.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(Sekcja("⏰ Kopia sama", r1, inc, auto, T("Przechowywane: 5 ostatnich pełnych kopii z ich przyrostowymi – starsze usuwane same.", 12, Dim), planTxt));
            // 📁 gdzie
            folderTxt = new TextBlock { Foreground = Fg, TextTrimming = TextTrimming.CharacterEllipsis };
            var fRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var fz = Btn("Zmień…", () =>
            {
                using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = "Folder kopii Velivo", SelectedPath = KopieDir })
                    if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) { KopiaSkladniki.Set("kopia-folder", d.SelectedPath); OdswiezFolder(); }
            });
            var fo = Btn("📂", () => { try { Directory.CreateDirectory(KopieDir); Process.Start("explorer.exe", "\"" + KopieDir + "\""); } catch (Exception) { } }); fo.ToolTip = "Otwórz folder kopii";
            DockPanel.SetDock(fo, Dock.Right); DockPanel.SetDock(fz, Dock.Right); fRow.Children.Add(fo); fRow.Children.Add(fz); fRow.Children.Add(Pole(folderTxt));
            sp.Children.Add(Sekcja("📁 Gdzie zapisać", fRow, T("Najlepiej dysk zewnętrzny albo folder w sieci (np. NUCBOX) – nie ten sam dysk, co system.", 12, Dim)));
            OdswiezHaslo(); OdswiezPlan(); OdswiezFolder();
            return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        static Grid Wiersz(UIElement a, UIElement b, UIElement c, UIElement d)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            foreach (var w in new[] { new GridLength(2.2, GridUnitType.Star), new GridLength(3.6, GridUnitType.Star), new GridLength(1.4, GridUnitType.Star), new GridLength(78) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            int i = 0; foreach (var e in new[] { a, b, c, d }) { if (e is FrameworkElement fe) fe.Margin = new Thickness(4, 6, 4, 6); if (i == 3 && e is TextBlock tb) tb.TextAlignment = TextAlignment.Right; Grid.SetColumn(e, i++); g.Children.Add(e); }
            return g;
        }
        static Border Linia(UIElement e) => new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = e };
        int odswN;
        void OdswiezModuly()
        {
            if (modulyRows == null) return;
            modulyRows.Children.Clear(); int nr = ++odswN;
            foreach (var m in Moduly())
            {
                UIElement nazwa;
                if (m.Id == null) nazwa = T(m.Nazwa);
                else
                {
                    var id = m.Id; var cb = new CheckBox { Content = T(m.Nazwa), IsChecked = KopiaSkladniki.WKopii(id), VerticalAlignment = VerticalAlignment.Top };
                    cb.Click += (s, e) => KopiaSkladniki.UstawWKopii(id, cb.IsChecked == true); nazwa = cb;
                }
                var roz = T("…", 12.5, Dim);
                var st = T(m.Stan, 12.5, m.Jest ? Ok : Warn);
                modulyRows.Children.Add(Linia(Wiersz(nazwa, T(m.Opis, 12.5), st, roz)));
                var mm = m; Task.Run(() => mm.Rozmiar()).ContinueWith(t => win.Dispatcher.BeginInvoke(new Action(() => { if (nr == odswN) roz.Text = mm.Id == null ? "< 0,1 MB" : Mb(t.Result); })));   // rozmiar w tle - okno od razu
            }
        }
        void OdswiezHaslo() { if (hasloTxt == null) return; bool jest = !string.IsNullOrEmpty(KopiaSkladniki.Get("zamki")); hasloTxt.Text = jest ? "✓ ustawione – hasła i klucze przeniosą się na nowy komputer" : "nie ustawione – hasła stron, rozmowy i konta zostaną tylko na tym komputerze"; hasloTxt.Foreground = jest ? Ok : Warn; hasloTxt.VerticalAlignment = VerticalAlignment.Center; }
        void OdswiezFolder() { if (folderTxt != null) { folderTxt.Text = KopieDir; folderTxt.ToolTip = KopieDir; } OdswiezKopie(); }
        void OdswiezPlan()
        {
            if (planTxt == null) return;
            var k = Kopie(); var last = k.FirstOrDefault(); var n = Nastepna();
            planTxt.Text = (last != null ? "Ostatnia: " + last.LastWriteTime.ToString("dd.MM.yyyy HH:mm") + " · " + (Przyrost(last.Name) ? "przyrostowa" : "pełna") + " · " + Mb(last.Length) : "Jeszcze nie ma żadnej kopii")
                + " · " + (n == null ? "kopia sama wyłączona" : "następna: " + (n.Value <= DateTime.Now ? "zaraz" : n.Value.ToString("dd.MM.yyyy HH:mm")));
        }
        static void Opoznij(int s, Action a) { var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(s) }; t.Tick += (x, y) => { t.Stop(); a(); }; t.Start(); }

        // kopia przez silnik Velivo: postęp w kopia-stan.txt (trwa|N, gotowe|ścieżka|N, blad|opis)
        bool trwa;
        void Zrob(string rodzaj, bool cicho)
        {
            if (trwa) return;
            var plik = Path.Combine(Dane, KopiaSkladniki.StanPlik); try { File.Delete(plik); } catch (Exception) { }
            if (!Uruchom("--kopia " + rodzaj, cicho ? null : win)) { if (cicho) stan.Text = "⚠ Kopia sama: nie znalazłem Velivo, które ją robi"; return; }
            trwa = true; bar.Visibility = Visibility.Visible; bAuto.IsEnabled = bPelna.IsEnabled = false; stan.Foreground = Dim; stan.Text = (rodzaj == "pelna" ? "Pełna kopia" : "Kopia") + " – zaczynam…";
            var start = DateTime.Now; var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            poll.Tick += (s, e) =>
            {
                string t = null; try { if (File.Exists(plik)) t = File.ReadAllText(plik); } catch (Exception) { }
                var p = (t ?? "").Split('|');
                if (p[0] == "trwa") { stan.Text = "Tworzę kopię… " + p.ElementAtOrDefault(1) + " plików"; return; }
                if (p[0] != "gotowe" && p[0] != "blad") { if (DateTime.Now - start < TimeSpan.FromSeconds(90)) return; p = new[] { "blad", "Velivo nie odpowiada – zaktualizuj je do najnowszej wersji (starsza nie zna kopii z tego programu)." }; }
                poll.Stop(); trwa = false; bar.Visibility = Visibility.Collapsed; bAuto.IsEnabled = bPelna.IsEnabled = true;
                bool ok = p[0] == "gotowe"; long len = 0; try { len = new FileInfo(p.ElementAtOrDefault(1) ?? "").Length; } catch (Exception) { }
                stan.Text = ok ? "✅ Kopia gotowa: " + Path.GetFileName(p[1]) + " · " + p.ElementAtOrDefault(2) + " plików · " + Mb(len) : "❌ Nie udało się: " + p.ElementAtOrDefault(1);
                stan.Foreground = ok ? Ok : Hot;
                if (cicho && !ok) try { tray.ShowBalloonTip(8000, "Velivo Kopia", "Kopia sama nie udała się: " + p.ElementAtOrDefault(1), System.Windows.Forms.ToolTipIcon.Warning); } catch (Exception) { }
                OdswiezPlan(); OdswiezKopie();
            };
            poll.Start();
        }

        // ---------- ♻ przywróć ----------
        FrameworkElement PanelPrzywroc()
        {
            var sp = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            sp.Children.Add(Info("Wybierz dzień – program sam składa pełną kopię i przyrostowe do tego dnia. Przed przywróceniem programy Velivo zostaną zamknięte; Velivo zapyta jeszcze o potwierdzenie i – dla kopii z innego komputera – o hasło albo klucz przeniesienia."));
            var hdr = Wiersz(T("Kopia", 12, Dim), T("Rodzaj", 12, Dim), T("", 12, Dim), T("Rozmiar", 12, Dim)); sp.Children.Add(hdr);
            kopieRows = new StackPanel(); sp.Children.Add(kopieRows);
            var inny = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var it = T("Kopia z innego miejsca (np. dysk zewnętrzny):", 12.5, Dim); it.VerticalAlignment = VerticalAlignment.Center; inny.Children.Add(it);
            inny.Children.Add(Btn("Wybierz plik…", () =>
            {
                var d = new Microsoft.Win32.OpenFileDialog { Title = "Kopia Velivo do przywrócenia", Filter = "Kopia Velivo|" + Prefix + "*.zip;*.zip" };
                if (Directory.Exists(KopieDir)) d.InitialDirectory = KopieDir;
                if (d.ShowDialog(win) == true) Przywroc(d.FileName);
            }));
            sp.Children.Add(inny);
            sp.Children.Add(Sekcja("⚙ Co dzieje się przy przywracaniu",
                T("• dane i ustawienia zaznaczonych programów wracają z kopii; ścieżki starego konta (inna nazwa użytkownika) zamieniane same", 12.5),
                T("• start z Windows – te same programy Velivo co na starym komputerze (tylko zainstalowane tutaj)", 12.5),
                T("• przypięte w Centrum i ustawienia kopii – jak były", 12.5),
                T("• hasła stron, rozmowy, słownik, konta – gdy podasz hasło albo klucz przeniesienia", 12.5),
                T("Nie przenoszę: planu zasilania i limitów BIOS starego komputera, numeru urządzenia w sieci domowej, pamięci podręcznej. Sterowanie mocą w Velivo Procesor (zadanie z uprawnieniami, sterownik PawnIO na AMD) włączasz jednym „Tak” w Velivo Procesor – jak po każdej instalacji. Po przywróceniu najlepiej uruchomić komputer ponownie.", 12, Dim)));
            return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        void OdswiezKopie()
        {
            if (kopieRows == null) return;
            kopieRows.Children.Clear(); var l = Kopie();
            if (l.Count == 0) kopieRows.Children.Add(T("W folderze " + KopieDir + " nie ma jeszcze kopii.", 12.5, Dim));
            foreach (var f in l.Take(60))
            {
                var ff = f.FullName;
                var bt = Btn(f == l[0] ? "♻ Przywróć ten stan" : "♻ Przywróć", () => Przywroc(ff), f == l[0]); bt.HorizontalAlignment = HorizontalAlignment.Right;
                var g = Wiersz(T(f.LastWriteTime.ToString("dd.MM.yyyy  HH:mm")), T(Przyrost(f.Name) ? "przyrostowa" : "PEŁNA", 12.5, Przyrost(f.Name) ? Dim : Fg, Przyrost(f.Name) ? FontWeights.Normal : FontWeights.SemiBold), bt, T(Mb(f.Length), 12.5, Dim));
                kopieRows.Children.Add(Linia(g));
            }
            OdswiezPlan();
        }
        // zamknięcie wszystkich programów Velivo (Procesor z uprawnieniami - przez jego zadanie), potem przywracanie przez silnik Velivo
        static readonly string[] Procesy = { "Velivo", "VelivoOdtwarzacz", "VelivoPrzegladarka", "VelivoKlawiatura", "VelivoSerwer", "VelivoKomunikator", "VelivoAudioBalance", "VelivoMonitor", "VelivoKursory", "VelivoProcesor", "VelivoCentrum" };
        void Przywroc(string zip)
        {
            if (Silnik() == null) { Uruchom("", win); return; }
            if (MessageBox.Show(win, "Zamknąć wszystkie programy Velivo i przywrócić kopię\n" + Path.GetFileName(zip) + "?\n\nVelivo zapyta jeszcze o potwierdzenie i – dla kopii z innego komputera – o hasło albo klucz przeniesienia. Po przywróceniu najlepiej uruchomić komputer ponownie.", "Velivo Kopia", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            stan.Foreground = Dim; stan.Text = "Zamykam programy Velivo…";
            Task.Run(() =>
            {
                try { using (var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/end /tn \"Velivo Procesor\"") { UseShellExecute = false, CreateNoWindow = true })) p.WaitForExit(5000); } catch (Exception) { }
                foreach (var n in Procesy)
                    foreach (var p in Process.GetProcessesByName(n))
                        try { if (!p.CloseMainWindow() || !p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); } } catch (Exception) { } finally { p.Dispose(); }
            }).ContinueWith(t => win.Dispatcher.BeginInvoke(new Action(() =>
            {
                var zostaly = Procesy.Where(n => { var ps = Process.GetProcessesByName(n); foreach (var x in ps) x.Dispose(); return ps.Length > 0; }).ToList();
                stan.Text = zostaly.Count > 0 ? "⚠ Nie zamknęły się: " + string.Join(", ", zostaly) + " – ich ustawienia mogą się nie przywrócić" : "Przywraca Velivo – odpowiedz na jego pytania";
                stan.Foreground = zostaly.Count > 0 ? Warn : Dim;
                Uruchom("--przywroc \"" + zip + "\"", win);
            })));
        }
    }
}
