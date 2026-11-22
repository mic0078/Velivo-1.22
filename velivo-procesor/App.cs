using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VelivoProcesor
{
    // ⚙ Velivo Procesor - CPU Manager w rodzinie Velivo (dokumentacja/makieta-velivo-procesor.html): w zasobniku, co 1 s pomiar i decyzja,
    // okno ze stanem, trybami, „dlaczego”, wykresem, zapamiętanymi programami i ustawieniami. Zamknięcie = ustawienia Windows wracają.
    static class Program
    {
        static readonly string ExePath = System.Reflection.Assembly.GetEntryAssembly().Location;
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Contains("--przywroc")) { new Silnik().PrzywrocWindows(); return; }   // deinstalator
            if (args.Contains("--zadanie")) { Zadanie(); return; }   // uruchomione jako administrator tylko po to
            using (var one = new Mutex(true, @"Local\VelivoProcesor", out bool first))
            {
                if (!first) { try { using (var ev = EventWaitHandle.OpenExisting(@"Local\VelivoProcesor.Pokaz")) ev.Set(); } catch (Exception) { } return; }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var o = new Okno(args.Contains("--zasobnik"));
                app.Startup += (s, e) => o.Start();
                app.Run();
            }
        }

        // 🔑 sterowanie mocą AMD (RyzenAdj) wymaga administratora: zadanie Windows „Velivo Procesor” uruchamia program przy logowaniu z najwyższymi uprawnieniami (jedno „Tak”)
        static void Zadanie()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/create /tn \"Velivo Procesor\" /tr \"\\\"" + ExePath + "\\\" --zasobnik\" /sc onlogon /rl highest /f") { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) p.WaitForExit(10000);
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) k?.DeleteValue("VelivoProcesor", false);   // start z Windows przejmuje zadanie
            }
            catch (Exception) { }
        }
        internal static bool ZadanieJest()
        {
            try { using (var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/query /tn \"Velivo Procesor\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })) { p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); return p.ExitCode == 0; } }
            catch (Exception) { return false; }
        }
        internal static void ZadanieUtworz()
        {
            try { Process.Start(new ProcessStartInfo(ExePath, "--zadanie") { UseShellExecute = true, Verb = "runas" })?.WaitForExit(15000); } catch (Exception) { return; }   // „Nie” w oknie Windows - nic
            if (!ZadanieJest()) return;
            // ta kopia się kończy, a za 2 s zadanie uruchamia program z uprawnieniami (wcześniej druga kopia zobaczyłaby tę i od razu się zamknęła)
            try { Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & schtasks /run /tn \"Velivo Procesor\"") { UseShellExecute = false, CreateNoWindow = true }); } catch (Exception) { }
            Zakoncz?.Invoke();
        }
        internal static Action Zakoncz;
        internal static bool Autostart
        {
            get { try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return k?.GetValue("VelivoProcesor") != null || ZadanieJest(); } catch (Exception) { return false; } }
            set { try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) { if (value) k.SetValue("VelivoProcesor", "\"" + ExePath + "\" --zasobnik"); else k.DeleteValue("VelivoProcesor", false); } } catch (Exception) { } }
        }
    }

    sealed class Okno
    {
        static readonly Brush Bg = B(0x11, 0x14, 0x1C), Card = B(0x14, 0x18, 0x21), Line = B(0x2A, 0x32, 0x40), Fg = B(0xE8, 0xEB, 0xF0), Dim = B(0x8A, 0x93, 0xA3),
            Tq = B(0x22, 0xB8, 0xD8), Vi = B(0xA7, 0x8B, 0xFA), Ok = B(0x4A, 0xDE, 0x80), Warn = B(0xF5, 0xB5, 0x44), Hot = B(0xF8, 0x71, 0x71), On = B(0x2E, 0x1A, 0x4D);
        static SolidColorBrush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

        readonly bool ukryty; readonly Silnik s = new Silnik();
        Window win; System.Windows.Forms.NotifyIcon tray; Pomiar ost = new Pomiar();
        TextBlock tCpu, tGpu, tTemp, tGhz, tStan, tWhy, tInfo; Border[] kafle = new Border[5]; Canvas wykres; StackPanel lista;
        readonly double[] hCpu = new double[120], hTemp = new double[120]; readonly int[] hTryb = new int[120]; int hPos;

        public Okno(bool zasobnik) { ukryty = zasobnik; }

        public void Start()
        {
            s.SprawdzRyzen();
            Zbuduj();
            tray = new System.Windows.Forms.NotifyIcon { Icon = Ikona(), Visible = true, Text = "Velivo Procesor" };
            tray.MouseClick += (a, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Pokaz(); };
            tray.ContextMenuStrip = MenuZasobnik();
            Velivo.CentrumZasobnik.Podlacz(tray);
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\VelivoProcesor.Pokaz");
            new Thread(() => { while (ev.WaitOne()) win.Dispatcher.BeginInvoke(new Action(Pokaz)); }) { IsBackground = true }.Start();
            Microsoft.Win32.SystemEvents.SessionEnding += (a, e) => Koniec(false);
            Program.Zakoncz = () => Koniec(true);
            new Thread(Petla) { IsBackground = true, Name = "Velivo Procesor", Priority = ThreadPriority.AboveNormal }.Start();
            if (!ukryty) Pokaz();
        }

        // co 1 s: pomiar, decyzja, okno (bez pracy w wątku okna - przełączenie trybu i RyzenAdj w tle)
        void Petla()
        {
            while (true)
            {
                try
                {
                    var p = s.Zmierz(); var t = s.Decyduj(p); ost = p;
                    hCpu[hPos] = p.Cpu; hTemp[hPos] = p.Temp; hTryb[hPos] = (int)t; hPos = (hPos + 1) % hCpu.Length;
                    win.Dispatcher.BeginInvoke(new Action(Odswiez));
                }
                catch (Exception) { }
                Thread.Sleep(1000);
            }
        }

        System.Drawing.Icon Ikona()
        {
            try { var f = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "procesor.ico"); if (File.Exists(f)) return new System.Drawing.Icon(f, 16, 16); } catch (Exception) { }
            return System.Drawing.SystemIcons.Application;
        }

        System.Windows.Forms.ContextMenuStrip MenuZasobnik()
        {
            var m = new System.Windows.Forms.ContextMenuStrip();
            var stan = new System.Windows.Forms.ToolStripMenuItem("Velivo Procesor") { Enabled = false }; m.Items.Add(stan);
            m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            foreach (var (k, n) in new[] { ("auto", "🧠 Auto – sam wybiera"), ("cichy", "🌙 Cichy"), ("zrownowazony", "⚖ Zrównoważony"), ("turbo", "🚀 Turbo") })
            { var it = new System.Windows.Forms.ToolStripMenuItem(n); it.Click += (a, e) => Wybierz(k); it.Tag = k; m.Items.Add(it); }
            m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            m.Items.Add("🪟 Otwórz okno", null, (a, e) => Pokaz());
            m.Items.Add("↺ Przywróć ustawienia domyślne Windows", null, (a, e) => Domyslne());
            m.Items.Add("⏻ Zakończ (ustawienia Windows wracają)", null, (a, e) => Koniec(true));
            m.Opening += (a, e) =>
            {
                stan.Text = "Velivo Procesor · " + (s.Get("tryb") == "windows" ? "ustawienia Windows" : (s.Auto ? "Auto · " : "") + Silnik.Nazwa(s.Obecny)) + (ost.Temp > 0 ? " · " + ost.Temp.ToString("0") + " °C" : "");
                foreach (var it in m.Items.OfType<System.Windows.Forms.ToolStripMenuItem>()) if (it.Tag is string k) it.Checked = s.Get("tryb", "auto") == k;
            };
            return m;
        }

        void Wybierz(string k) { s.Put("tryb", k); Odswiez(); }
        void Domyslne()
        {
            if (MessageBox.Show(win.IsVisible ? win : null, "Przywrócić plan zasilania Windows do ustawień domyślnych?\n\nVelivo Procesor przestanie sterować procesorem, dopóki nie wybierzesz trybu.", "Velivo Procesor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var r = s.DomyslneWindows(); tInfo.Text = r; Odswiez();
        }
        void Koniec(bool zamknij)
        {
            try { s.PrzywrocWindows(); s.ZapiszNauke(); } catch (Exception) { }
            if (!zamknij) return;
            try { tray.Visible = false; tray.Dispose(); } catch (Exception) { }
            Application.Current.Shutdown();
        }
        void Pokaz() { win.Show(); if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal; win.Activate(); }

        // ---------- okno ----------
        static TextBlock T(string t, double size = 13, Brush c = null, FontWeight? w = null) => new TextBlock { Text = t, FontSize = size, Foreground = c ?? Fg, FontWeight = w ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        static Border Box(UIElement c, Thickness? m = null) => new Border { Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 8, 10, 8), Margin = m ?? new Thickness(4), Child = c };
        static Button Btn(string t, Action a, string tip = null)
        {
            var b = new Button { Content = t, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 4, 6, 0), Foreground = Fg, Background = Card, BorderBrush = Line, Cursor = Cursors.Hand, ToolTip = tip };
            b.Template = Szablon(); b.Click += (s, e) => a(); return b;
        }
        static ControlTemplate Szablon()
        {   // własny wygląd: zwykły przycisk Windows robi się biały po najechaniu (tekst niewidoczny)
            var t = new ControlTemplate(typeof(Button)); var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(7)); bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent }); bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center); bd.AppendChild(cp); t.VisualTree = bd;
            var hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hov.Setters.Add(new Setter(Control.BorderBrushProperty, Tq)); t.Triggers.Add(hov);
            return t;
        }

        void Zbuduj()
        {
            win = new Window { Title = "Velivo Procesor", Width = 800, Height = 720, MinWidth = 560, Background = Bg, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            try { win.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "procesor.ico"))); } catch (Exception) { }
            win.Closing += (a, e) => { e.Cancel = true; win.Hide(); };   // ✕ chowa do zasobnika - program steruje dalej
            var root = new StackPanel { Margin = new Thickness(10) };
            var head = new DockPanel { Margin = new Thickness(4, 0, 4, 6) };
            tStan = T("", 13, Ok); DockPanel.SetDock(tStan, Dock.Right); head.Children.Add(tStan);
            head.Children.Add(T("⚙ Velivo Procesor", 18, Fg, FontWeights.SemiBold));
            root.Children.Add(head);
            root.Children.Add(T("  " + s.ProcesorNazwa + (s.BazaMhz > 0 ? " · zegar bazowy " + (s.BazaMhz / 1000).ToString("0.0") + " GHz" : "") + (s.Amd ? (s.RyzenDziala ? " · moc AMD: RyzenAdj ✓" : s.Ryzen == null ? " · moc AMD: brak RyzenAdj" : " · moc AMD: wymaga zgody administratora") : ""), 11, Dim));
            // kafelki pomiarów
            var k = new UniformGrid4(); root.Children.Add(k.Panel);
            tCpu = k.Add("PROCESOR"); tGpu = k.Add("KARTA GRAFICZNA"); tTemp = k.Add("TEMPERATURA"); tGhz = k.Add("ZEGAR");
            // tryby
            var tr = new Grid { Margin = new Thickness(0, 4, 0, 0) }; for (int i = 0; i < 5; i++) tr.ColumnDefinitions.Add(new ColumnDefinition());
            var opisy = new[] { ("auto", "🧠", "Auto", "sam wybiera"), ("cichy", "🌙", "Cichy", "do " + s.CichyGhz.ToString("0.0") + " GHz"), ("zrownowazony", "⚖", "Zrównoważony", "zwykła praca"), ("turbo", "🚀", "Turbo", "pełna moc"), ("windows", "↺", "Windows", "ustawienia domyślne") };
            for (int i = 0; i < 5; i++)
            {
                var (key, ic, n, o) = opisy[i]; var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                sp.Children.Add(new TextBlock { Text = ic, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Fg });
                sp.Children.Add(new TextBlock { Text = n, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Fg, FontWeight = FontWeights.SemiBold });
                sp.Children.Add(new TextBlock { Text = o, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Dim });
                var b = Box(sp); b.Cursor = Cursors.Hand; b.Padding = new Thickness(6, 10, 6, 10); kafle[i] = b;
                b.ToolTip = key == "auto" ? "Program sam wybiera tryb: temperatura, wczytywanie, obciążenie, gry na karcie graficznej, zapamiętane programy, spoczynek" : key == "windows" ? "Przywraca plan zasilania Windows do ustawień domyślnych; program przestaje sterować" : "Zawsze ten tryb";
                b.MouseLeftButtonUp += (a, e) => { if (key == "windows") Domyslne(); else Wybierz(key); };
                Grid.SetColumn(b, i); tr.Children.Add(b);
            }
            root.Children.Add(tr);
            var why = new Border { Background = B(0x0E, 0x21, 0x40), BorderBrush = B(0x1E, 0x3A, 0x66), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(4) };
            tWhy = T("", 13, B(0xCF, 0xE3, 0xFF)); why.Child = tWhy; root.Children.Add(why);
            // wykres i programy
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition());
            var gw = new StackPanel(); gw.Children.Add(T("📈 Ostatnie 2 minuty", 13, Fg, FontWeights.SemiBold)); wykres = new Canvas { Height = 120, ClipToBounds = true, Margin = new Thickness(0, 6, 0, 4) }; gw.Children.Add(wykres);
            gw.Children.Add(T("turkus – procesor · fiolet – tryb · żółty – temperatura", 11, Dim)); var bw = Box(gw); Grid.SetColumn(bw, 0); g.Children.Add(bw);
            var gp = new StackPanel(); gp.Children.Add(T("🎮 Programy (zapamiętane)", 13, Fg, FontWeights.SemiBold)); lista = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            gp.Children.Add(new ScrollViewer { Content = lista, Height = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            gp.Children.Add(T("prawy przycisk: zawsze ten tryb / ucz się sam", 11, Dim)); var bp = Box(gp); Grid.SetColumn(bp, 1); g.Children.Add(bp);
            root.Children.Add(g);
            // ustawienia
            var us = new StackPanel(); us.Children.Add(T("⚙ Ustawienia", 13, Fg, FontWeights.SemiBold));
            var auto = new CheckBox { Content = "Start z Windows (w zasobniku)", Foreground = Fg, IsChecked = Program.Autostart, Margin = new Thickness(0, 6, 0, 0) };
            auto.Click += (a, e) => Program.Autostart = auto.IsChecked == true; us.Children.Add(auto);
            us.Children.Add(Suwak("🌙 Najniższy zegar w trybie Cichym", "cichy-ghz", 0.6, Math.Max(1.0, s.BazaMhz / 1000), 1.4, 0.1, v => v.ToString("0.0") + " GHz", "Także poniżej zegara bazowego – mniej ciepła i hałasu w spoczynku"));
            us.Children.Add(Suwak("🌡 Temperatura graniczna (wtedy Cichy)", "temp-limit", 70, 98, 90, 1, v => v.ToString("0") + " °C", "Program zwalnia też wcześniej, gdy temperatura szybko rośnie"));
            if (s.Amd && s.Ryzen != null && !s.RyzenDziala)
                us.Children.Add(Btn("🔑 Włącz sterowanie mocą AMD (jedno „Tak” administratora)", Program.ZadanieUtworz, "RyzenAdj zmienia moc procesora (W) – wymaga uprawnień administratora. Program będzie startował z Windows przez zadanie z najwyższymi uprawnieniami."));
            tInfo = T("", 12, Warn); us.Children.Add(tInfo);
            root.Children.Add(Box(us));
            win.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Odswiez();
        }

        UIElement Suwak(string n, string key, double min, double max, double def, double step, Func<double, string> fmt, string tip)
        {
            var d = new DockPanel { Margin = new Thickness(0, 6, 0, 0), ToolTip = tip };
            var val = T(fmt(s.GetD(key, def)), 12, Fg); val.Width = 70; DockPanel.SetDock(val, Dock.Right); d.Children.Add(val);
            var lab = T(n, 12, Fg); lab.Width = 260; DockPanel.SetDock(lab, Dock.Left); d.Children.Add(lab);
            var sl = new Slider { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, s.GetD(key, def))), TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            sl.ValueChanged += (a, e) => { val.Text = fmt(sl.Value); s.Put(key, sl.Value); };
            d.Children.Add(sl); return d;
        }

        string listaSig = "";
        void Odswiez()
        {
            var p = ost; string tryb = s.Get("tryb", "auto");
            tCpu.Text = p.Cpu.ToString("0") + " %"; tGpu.Text = p.Gpu >= 0 ? p.Gpu.ToString("0") + " %" : "–";
            tTemp.Text = p.Temp > 0 ? p.Temp.ToString("0") + " °C" : "–"; tTemp.Foreground = p.Temp >= s.TempLimit ? Hot : p.Temp >= s.TempLimit - 15 ? Warn : Fg;
            tTemp.ToolTip = p.Temp > 0 ? null : "Temperatura nieznana – na Intelu uruchom LibreHardwareMonitor (odczyt przez WMI); na AMD włącz sterowanie mocą";
            tGhz.Text = p.Ghz > 0 ? p.Ghz.ToString("0.00") + " GHz" : "–";
            tStan.Text = tryb == "windows" ? "● ustawienia Windows" : "● " + (tryb == "auto" ? "Auto · " : "") + Silnik.Nazwa(s.Obecny) + (p.Bateria ? " · bateria" : "");
            tWhy.Text = "💡 " + s.Dlaczego + (s.Blad.Length > 0 ? "  ⚠ " + s.Blad : "");
            var keys = new[] { "auto", "cichy", "zrownowazony", "turbo", "windows" };
            for (int i = 0; i < 5; i++)
            {
                bool wyb = keys[i] == tryb, teraz = tryb == "auto" && i - 1 == (int)s.Obecny;
                kafle[i].Background = wyb ? On : Card; kafle[i].BorderBrush = wyb ? Vi : teraz ? Tq : Line; kafle[i].BorderThickness = new Thickness(wyb || teraz ? 2 : 1);
            }
            if (tray != null) tray.Text = ("Velivo Procesor · " + (tryb == "windows" ? "Windows" : Silnik.Nazwa(s.Obecny)) + (p.Temp > 0 ? " · " + p.Temp.ToString("0") + " °C" : "") + " · " + p.Cpu.ToString("0") + " %");
            if (!win.IsVisible) return;   // schowane okno nie rysuje (bez pracy w tle)
            RysujWykres();
            var pr = s.ProgramyOpis(); var sig = string.Join("|", pr.Select(x => x.Prog + x.Opis));
            if (sig != listaSig)
            {
                listaSig = sig; lista.Children.Clear();
                if (pr.Count == 0) lista.Children.Add(T("Program uczy się – po kilku minutach pracy pojawią się tu Twoje programy.", 12, Dim));
                foreach (var (prog, opis) in pr)
                {
                    var d = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent }; var o = T(opis, 12, Dim); DockPanel.SetDock(o, Dock.Right); d.Children.Add(o); d.Children.Add(T(prog, 12));
                    var cm = new ContextMenu();
                    foreach (Tryb t in Enum.GetValues(typeof(Tryb))) { var tt = t; var mi = new MenuItem { Header = "Zawsze: " + Silnik.Nazwa(t) }; mi.Click += (a, e) => { s.Reczne[prog] = tt; s.ZapiszNauke(); listaSig = ""; Odswiez(); }; cm.Items.Add(mi); }
                    var ml = new MenuItem { Header = "Ucz się sam" }; ml.Click += (a, e) => { s.Reczne.Remove(prog); s.ZapiszNauke(); listaSig = ""; Odswiez(); }; cm.Items.Add(ml);
                    d.ContextMenu = cm; lista.Children.Add(d);
                }
            }
        }

        void RysujWykres()
        {
            wykres.Children.Clear(); double w = Math.Max(10, wykres.ActualWidth), h = wykres.Height; int n = hCpu.Length;
            Polyline L(Brush c, double th) { var pl = new Polyline { Stroke = c, StrokeThickness = th }; wykres.Children.Add(pl); return pl; }
            var lc = L(Tq, 1.5); var lt = L(Warn, 1); var lm = L(Vi, 1.5); lt.StrokeDashArray = new DoubleCollection { 3, 2 };
            for (int i = 0; i < n; i++)
            {
                int j = (hPos + i) % n; double x = w * i / (n - 1);
                lc.Points.Add(new Point(x, h - hCpu[j] / 100 * h));
                if (hTemp[j] > 0) lt.Points.Add(new Point(x, h - Math.Min(100, hTemp[j]) / 100 * h));
                lm.Points.Add(new Point(x, h - 6 - hTryb[j] * (h - 12) / 2));
            }
        }

        // cztery kafelki pomiarów
        sealed class UniformGrid4
        {
            public readonly Grid Panel = new Grid(); int i;
            public UniformGrid4() { for (int k = 0; k < 4; k++) Panel.ColumnDefinitions.Add(new ColumnDefinition()); }
            public TextBlock Add(string n)
            {
                var sp = new StackPanel(); sp.Children.Add(T(n, 10, Dim)); var v = T("–", 20, Fg, FontWeights.SemiBold); sp.Children.Add(v);
                var b = Box(sp); Grid.SetColumn(b, i++); Panel.Children.Add(b); return v;
            }
        }
    }
}
