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
        TextBlock tCichy, tCpu, tGpu, tTemp, tGhz, tStan, tWhy, tInfo, tPb, tSub, tAll; Button bAdmin; Border[] kafle = new Border[5]; Canvas wykres; StackPanel lista;
        readonly double[] hCpu = new double[120], hTemp = new double[120]; readonly int[] hTryb = new int[120], hPb = new int[120]; int hPos;

        public Okno(bool zasobnik) { ukryty = zasobnik; }

        public void Start()
        {
            if (s.Get("autostart-ustawiony") == null) { if (!Program.Autostart) Program.Autostart = true; s.Put("autostart-ustawiony", 1); }   // domyślnie start z Windows (wyłączenie w oknie zostaje)
            s.SprawdzRyzen();
            Zbuduj();
            tray = new System.Windows.Forms.NotifyIcon { Icon = Ikona(), Visible = true, Text = "Velivo Procesor" };
            tray.MouseClick += (a, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Pokaz(); };
            tray.ContextMenuStrip = MenuZasobnik();
            Velivo.CentrumZasobnik.Podlacz(tray, false);   // własna ikona przy zegarze zawsze (tryb i temperatura widać od razu), menu także z Centrum
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
                    if (++petlaN % 2 == 0) pb.Krok(s.Get("probalance", "1") == "1", p.Cpu, p.Pid, s.PriorytetDla, s.BezPbDla);
                    // 🎚 Studio: komputer i ekran nie zasną, dopóki działa program w trybie Studio (wołane z tego samego, stałego wątku)
                    bool st = s.StudioProg != null && s.Get("tryb") != "windows";
                    if (st != uspienieBlok) { uspienieBlok = st; SetThreadExecutionState(st ? 0x80000003u : 0x80000000u); }
                    Centrum(p);
                    hCpu[hPos] = p.Cpu; hTemp[hPos] = p.Temp; hTryb[hPos] = (int)t; hPb[hPos] = pb.Liczba; hPos = (hPos + 1) % hCpu.Length;
                    win.Dispatcher.BeginInvoke(new Action(Odswiez));
                }
                catch (Exception) { }
                Thread.Sleep(1000);
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint f);
        bool uspienieBlok;
        int petlaN; readonly ProBalance pb = new ProBalance();
        System.IO.MemoryMappedFiles.MemoryMappedFile mmf;
        // 🧩 stan dla Velivo Centrum (jak poziom Audio Balance): czas + tekst „Auto · Cichy · 54 °C”
        void Centrum(Pomiar p)
        {
            try
            {
                if (mmf == null) mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen(@"Local\Velivo.Procesor.Stan", 512);
                string tryb = s.Get("tryb", "auto");
                string t = (tryb == "windows" ? "ustawienia Windows" : (tryb == "auto" ? "Auto · " : "") + Silnik.Nazwa(s.Obecny)) + (p.Temp > 0 ? " · " + p.Temp.ToString("0") + " °C" : "") + " · " + p.Cpu.ToString("0") + " %";
                var b = System.Text.Encoding.UTF8.GetBytes(t); if (b.Length > 400) Array.Resize(ref b, 400);
                using (var v = mmf.CreateViewAccessor()) { v.Write(0, b.Length); v.WriteArray(16, b, 0, b.Length); v.Write(8, (long)GetTickCount64()); }
            }
            catch (Exception) { }
        }

        System.Drawing.Icon Ikona()
        {
            try { var f = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "procesor.ico"); if (File.Exists(f)) return new System.Drawing.Icon(f, System.Windows.Forms.SystemInformation.SmallIconSize); } catch (Exception) { }   // ten sam obraz co na pulpicie, w rozmiarze zasobnika (DPI)
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
            var pbIt = new System.Windows.Forms.ToolStripMenuItem("⚖ ProBalance – programy w tle nie przycinają"); pbIt.Click += (a, e) => PbUstaw(s.Get("probalance", "1") != "1"); m.Items.Add(pbIt);
            m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            m.Items.Add("🪟 Otwórz okno", null, (a, e) => Pokaz());
            m.Items.Add("↺ Przywróć ustawienia domyślne Windows", null, (a, e) => Domyslne());
            m.Items.Add("⏻ Zakończ (ustawienia Windows wracają)", null, (a, e) => Koniec(true));
            m.Opening += (a, e) =>
            {
                stan.Text = "Velivo Procesor · " + (s.Get("tryb") == "windows" ? "ustawienia Windows" : (s.Auto ? "Auto · " : "") + Silnik.Nazwa(s.Obecny)) + (ost.Temp > 0 ? " · " + ost.Temp.ToString("0") + " °C" : "");
                foreach (var it in m.Items.OfType<System.Windows.Forms.ToolStripMenuItem>()) if (it.Tag is string k) it.Checked = s.Get("tryb", "auto") == k;
                pbIt.Checked = s.Get("probalance", "1") == "1";
            };
            return m;
        }

        void Wybierz(string k) { s.Put("tryb", k); tInfo.Text = ""; Odswiez(); }   // komunikat po ↺ Windows już nieaktualny
        CheckBox pbBox;
        void PbUstaw(bool on) { s.Put("probalance", on ? "1" : "0"); if (pbBox != null) pbBox.IsChecked = on; Odswiez(); }
        void Domyslne()
        {
            if (MessageBox.Show(win.IsVisible ? win : null, "Przywrócić plan zasilania Windows do ustawień domyślnych?\n\nVelivo Procesor przestanie sterować procesorem, dopóki nie wybierzesz trybu.", "Velivo Procesor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var r = s.DomyslneWindows(); tInfo.Text = r; Odswiez();
        }
        void Koniec(bool zamknij)
        {
            try { pb.PrzywrocWszystkie(); s.PrzywrocWindows(); s.ZapiszNauke(); } catch (Exception) { }
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
            tSub = T("", 11, Dim); root.Children.Add(tSub);
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
                var to = new TextBlock { Text = o, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Dim }; sp.Children.Add(to); if (key == "cichy") tCichy = to;
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
            gw.Children.Add(T("turkus – procesor · fiolet – tryb · żółty – temperatura · zielona linia – ProBalance uspokoił program w tle", 11, Dim)); var bw = Box(gw); Grid.SetColumn(bw, 0); g.Children.Add(bw);
            var gp = new StackPanel(); var gh = new DockPanel();
            tAll = T("⤢ Wszystkie", 12, Tq); tAll.Cursor = Cursors.Hand; tAll.ToolTip = "Pełna lista programów – sortowanie i szukanie"; tAll.MouseLeftButtonUp += (a, e) => Wszystkie();
            DockPanel.SetDock(tAll, Dock.Right); gh.Children.Add(tAll); gh.Children.Add(T("🎮 Programy (zapamiętane)", 13, Fg, FontWeights.SemiBold)); gp.Children.Add(gh);
            lista = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            var sv = new ScrollViewer { Content = new Border { BorderBrush = Sep, BorderThickness = new Thickness(0, 1, 0, 0), Child = lista }, Height = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            sv.MouseDoubleClick += (a, e) => Wszystkie(); gp.Children.Add(sv);
            gp.Children.Add(T("śr. / szczyt procesora · 🎚 Studio · ⬆⬇ priorytet · ⚖̸ bez ProBalance · prawy przycisk – ustawienia", 11, Dim)); var bp = Box(gp); Grid.SetColumn(bp, 1); g.Children.Add(bp);
            root.Children.Add(g);
            // ustawienia
            var us = new StackPanel(); us.Children.Add(T("⚙ Ustawienia", 13, Fg, FontWeights.SemiBold));
            var auto = new CheckBox { Content = "Start z Windows (w zasobniku)", Foreground = Fg, IsChecked = Program.Autostart, Margin = new Thickness(0, 6, 0, 0) };
            auto.Click += (a, e) => Program.Autostart = auto.IsChecked == true; us.Children.Add(auto);
            pbBox = new CheckBox { Content = "⚖ ProBalance – programy w tle nie przycinają tego, z czym pracujesz", Foreground = Fg, IsChecked = s.Get("probalance", "1") == "1", Margin = new Thickness(0, 6, 0, 0),
                ToolTip = "Gdy procesor jest mocno zajęty, program w tle, który zjada dużo procesora, dostaje na chwilę niższy priorytet. Wraca sam, gdy się uspokoi albo przejdziesz do niego. System, dźwięk i programy Velivo – nigdy." };
            pbBox.Click += (a, e) => PbUstaw(pbBox.IsChecked == true); us.Children.Add(pbBox);
            tPb = T("", 11, Dim); tPb.Margin = new Thickness(22, 0, 0, 0); us.Children.Add(tPb);
            us.Children.Add(Suwak("🌙 Najniższy zegar w trybie Cichym", "cichy-ghz", 0.6, Math.Max(1.0, s.BazaMhz / 1000), 1.4, 0.1, v => v.ToString("0.0") + " GHz", "Także poniżej zegara bazowego – mniej ciepła i hałasu w spoczynku"));
            us.Children.Add(Suwak("🌡 Temperatura graniczna (wtedy Cichy)", "temp-limit", 70, 98, 90, 1, v => v.ToString("0") + " °C", "Program zwalnia też wcześniej, gdy temperatura szybko rośnie"));
            bAdmin = Btn(s.Amd && s.Ryzen != null ? "🔑 Włącz sterowanie mocą AMD i odczyt temperatury (jedno „Tak” administratora)" : "🔑 Włącz odczyt temperatury (jedno „Tak” administratora)", Program.ZadanieUtworz,
                "Odczyt czujników (i moc AMD przez RyzenAdj) wymaga uprawnień administratora. Program będzie startował z Windows przez zadanie z najwyższymi uprawnieniami – bez pytania przy każdym starcie.");
            bAdmin.HorizontalAlignment = HorizontalAlignment.Left; bAdmin.Visibility = Visibility.Collapsed; us.Children.Add(bAdmin);
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
            sl.ValueChanged += (a, e) => { val.Text = fmt(sl.Value); s.Put(key, sl.Value); s.Ponow(); if (key == "cichy-ghz" && tCichy != null) tCichy.Text = "do " + sl.Value.ToString("0.0") + " GHz"; };   // nowa wartość od razu (pętla stosuje w ciągu sekundy)
            d.Children.Add(sl); return d;
        }

        string listaSig = "";
        void Odswiez()
        {
            var p = ost; string tryb = s.Get("tryb", "auto");
            tCpu.Text = p.Cpu.ToString("0") + " %"; tGpu.Text = p.Gpu >= 0 ? p.Gpu.ToString("0") + " %" : "–";
            tTemp.Text = p.Temp > 0 ? p.Temp.ToString("0") + " °C" : "–"; tTemp.Foreground = p.Temp >= s.TempLimit ? Hot : p.Temp >= s.TempLimit - 15 ? Warn : Fg;
            tTemp.ToolTip = p.Temp > 0 ? "Źródło: " + s.TempZrodlo : "Temperatura nieznana – w Ustawieniach „🔑 Włącz odczyt temperatury” (czujniki wymagają uprawnień administratora) albo uruchom LibreHardwareMonitor";
            tGhz.Text = p.Ghz > 0 ? p.Ghz.ToString("0.00") + " GHz" : "–";
            bool studio = s.StudioProg != null && tryb != "windows";
            tStan.Text = tryb == "windows" ? "● ustawienia Windows" : "● " + (tryb == "auto" ? "Auto · " : "") + Silnik.Nazwa(s.Obecny) + (studio ? " · 🎚 Studio" : "") + (p.Bateria ? " · bateria" : "");
            tWhy.Text = "💡 " + s.Dlaczego + (studio ? " 🎚 Studio (" + s.StudioProg + ", " + Silnik.Nazwa(s.Obecny) + "): wszystkie rdzenie czuwają, zegar od razu w górę" + (s.Obecny == Tryb.Turbo ? ", stale pełny zegar" : " (bez stałego pełnego zegara – chłodniej i ciszej)") + ", priorytet Wysoki, komputer nie zaśnie. Po zamknięciu programu wszystko wraca." : "") + (s.Blad.Length > 0 ? "  ⚠ " + s.Blad : "");
            tSub.Text = "  " + s.ProcesorNazwa + (s.BazaMhz > 0 ? " · zegar bazowy " + (s.BazaMhz / 1000).ToString("0.0") + " GHz" : "") + (s.Amd ? (s.RyzenDziala ? " · moc AMD: RyzenAdj ✓" : s.Ryzen == null ? " · moc AMD: brak RyzenAdj" : " · moc AMD: wymaga zgody administratora") : "")
                + " · temperatura: " + (p.Temp > 0 ? s.TempZrodlo + " ✓" : "brak");
            bAdmin.Visibility = !Silnik.Admin() && petlaN > 5 && (p.Temp <= 0 || s.Amd && s.Ryzen != null && !s.RyzenDziala) ? Visibility.Visible : Visibility.Collapsed;
            var keys = new[] { "auto", "cichy", "zrownowazony", "turbo", "windows" };
            for (int i = 0; i < 5; i++)
            {
                bool wyb = keys[i] == tryb, teraz = tryb == "auto" && i - 1 == (int)s.Obecny;
                kafle[i].Background = wyb ? On : Card; kafle[i].BorderBrush = wyb ? Vi : teraz ? Tq : Line; kafle[i].BorderThickness = new Thickness(wyb || teraz ? 2 : 1);
            }
            tPb.Text = s.Get("probalance", "1") == "1" ? "ProBalance: " + (pb.Opis.Length > 0 ? pb.Opis : "czuwa") : "ProBalance wyłączony";
            if (tray != null) tray.Text = ("Velivo Procesor · " + (tryb == "windows" ? "Windows" : Silnik.Nazwa(s.Obecny)) + (p.Temp > 0 ? " · " + p.Temp.ToString("0") + " °C" : "") + " · " + p.Cpu.ToString("0") + " %");
            if (!win.IsVisible) return;   // schowane okno nie rysuje (bez pracy w tle)
            RysujWykres();
            var pr = s.ProgramyDane().OrderByDescending(x => x.Zawsze || x.StudioOn || x.Prio != 0).ThenByDescending(x => x.N).ToList();
            tAll.Text = "⤢ Wszystkie (" + pr.Count + ")";
            var sig = string.Join("|", pr.Take(30).Select(x => x.Prog + x.TrybOpis + x.Sr.ToString("0") + x.Szczyt.ToString("0") + x.Prio + x.StudioOn + x.BezPbOn));
            if (sig != listaSig && !menuOtwarte)
            {
                listaSig = sig; lista.Children.Clear();
                if (pr.Count == 0) lista.Children.Add(T("Program uczy się – po kilku minutach pracy pojawią się tu Twoje programy.", 12, Dim));
                foreach (var x in pr.Take(30)) lista.Children.Add(Wiersz(x));
            }
            if (wszWin != null && wszWin.IsVisible && !wszWin.IsMouseOver && !menuOtwarte && petlaN % 5 == 0) WszystkieOdswiez();
        }

        // ---------- programy: wiersz listy, menu, pełna lista ----------
        static readonly Brush Sep = B(0x22, 0x2A, 0x36), Hov = B(0x1E, 0x25, 0x32), Sel = B(0x2A, 0x1F, 0x44);
        string wybrany; bool menuOtwarte;   // program z otwartym menu (podświetlony); lista nie przebudowuje się pod otwartym menu
        UIElement Wiersz(Silnik.ProgInfo x)
        {
            var d = new DockPanel { Background = x.Prog == wybrany ? Sel : Brushes.Transparent, LastChildFill = true };
            var pas = new Border { Width = 3, Background = x.Prog == wybrany ? Vi : Brushes.Transparent }; DockPanel.SetDock(pas, Dock.Left); d.Children.Add(pas);
            var opis = x.Zawsze ? x.TrybOpis.Replace(" (zawsze)", "") + " · zawsze" : x.TrybOpis + (x.N > 0 ? " · " + x.Sr.ToString("0") + " % / " + x.Szczyt.ToString("0") + " %" : "");
            var o = new TextBlock { Text = opis, FontSize = 12, Foreground = Dim, Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(o, Dock.Right); d.Children.Add(o);
            var nz = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
            nz.Children.Add(new TextBlock { Text = x.Prog, FontSize = 12, Foreground = Fg, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170, VerticalAlignment = VerticalAlignment.Center });
            void Ik(string t, string tip) => nz.Children.Add(new TextBlock { Text = t, FontSize = 12, Foreground = Fg, Margin = new Thickness(5, 0, 0, 0), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center });
            if (x.StudioOn) Ik("🎚", "Tryb Studio – muzyka i nagrywanie bez trzasków");
            if (x.Prio > 0) Ik("⬆", "Priorytet Wysoki"); else if (x.Prio < 0) Ik("⬇", "Priorytet Niski");
            if (x.BezPbOn) Ik("⚖̸", "ProBalance nie uspokaja tego programu");
            d.Children.Add(nz);
            var b = new Border { Child = d, BorderBrush = Sep, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 4, 0, 4), CornerRadius = new CornerRadius(4) };
            b.MouseEnter += (a, e) => { if (x.Prog != wybrany) d.Background = Hov; };
            b.MouseLeave += (a, e) => { if (x.Prog != wybrany) d.Background = Brushes.Transparent; };
            b.ContextMenu = MenuProgramu(x.Prog);
            b.ContextMenuOpening += (a, e) => { wybrany = x.Prog; d.Background = Sel; pas.Background = Vi; };   // podświetlony, póki menu otwarte
            return b;
        }

        ContextMenu MenuProgramu(string prog)
        {
            var cm = new ContextMenu();
            cm.Opened += (a, e) => menuOtwarte = true;
            cm.Closed += (a, e) => { menuOtwarte = false; wybrany = null; listaSig = ""; Odswiez(); };
            void Zmien(Action a) { a(); s.ZapiszNauke(); listaSig = ""; Odswiez(); WszystkieOdswiez(); }
            cm.Items.Add(new MenuItem { Header = prog, IsEnabled = false });
            cm.Items.Add(new Separator());
            bool z = s.Reczne.TryGetValue(prog, out var rt);
            var mt = new MenuItem { Header = "Tryb" };
            bool stp; lock (s.Studio) stp = s.Studio.Contains(prog);
            foreach (Tryb t in Enum.GetValues(typeof(Tryb)))
            {
                var tt = t; var mi = new MenuItem { Header = "Zawsze: " + Silnik.Nazwa(t) + (stp && t == Tryb.Turbo ? " – miks, dużo wtyczek" : stp && t == Tryb.Zrownowazony ? " – nagrywanie, ciszej" : ""), IsChecked = z && rt == t };
                if (stp && t == Tryb.Cichy) { mi.IsEnabled = false; mi.Header = "Zawsze: Cichy – niedostępny w trybie Studio (trzaski)"; }
                mi.Click += (a, e) => Zmien(() => { lock (s.Studio) s.Reczne[prog] = tt; }); mt.Items.Add(mi);
            }
            var ml = new MenuItem { Header = "Ucz się sam", IsChecked = !z, IsEnabled = !stp, ToolTip = stp ? "W trybie Studio tryb jest stały – najpierw wyłącz Studio" : null }; ml.Click += (a, e) => Zmien(() => { lock (s.Studio) s.Reczne.Remove(prog); }); mt.Items.Add(ml);
            cm.Items.Add(mt);
            int pr; lock (s.Studio) pr = s.Priorytet.TryGetValue(prog, out var q) ? q : 0;
            var mp = new MenuItem { Header = "Priorytet" };
            foreach (var (v, n) in new[] { (1, "⬆ Wysoki"), (0, "Normalny (bez zmian)"), (-1, "⬇ Niski") })
            { var mi = new MenuItem { Header = n, IsChecked = pr == v }; mi.Click += (a, e) => Zmien(() => { lock (s.Studio) { if (v == 0) s.Priorytet.Remove(prog); else s.Priorytet[prog] = v; } }); mp.Items.Add(mi); }
            cm.Items.Add(mp);
            cm.Items.Add(new Separator());
            bool st; lock (s.Studio) st = s.Studio.Contains(prog);
            var ms = new MenuItem { Header = "🎚 Tryb Studio (muzyka, nagrywanie, bez trzasków)", IsChecked = st, ToolTip = "Gdy program działa: Turbo albo Zrównoważony (menu Tryb), wszystkie rdzenie czuwają, zegar od razu w górę, priorytet Wysoki, komputer nie zaśnie. Po zamknięciu programu wszystko wraca." };
            ms.Click += (a, e) => Zmien(() => { lock (s.Studio) { if (st) s.Studio.Remove(prog); else { s.Studio.Add(prog); if (!(s.Reczne.TryGetValue(prog, out var r0) && r0 == Tryb.Zrownowazony)) s.Reczne[prog] = Tryb.Turbo; } } });   // Studio: domyślnie Turbo, można zmienić na Zrównoważony
            cm.Items.Add(ms);
            bool bp = s.BezPbDla(prog);
            var mb = new MenuItem { Header = "⚖ Nie uspokajaj w ProBalance", IsChecked = bp }; mb.Click += (a, e) => Zmien(() => { lock (s.Studio) { if (bp) s.BezPb.Remove(prog); else s.BezPb.Add(prog); } });
            cm.Items.Add(mb);
            cm.Items.Add(new Separator());
            var mz = new MenuItem { Header = "🗑 Zapomnij program", ToolTip = "Usuwa to, czego program się nauczył, i wszystkie ustawienia tego programu" };
            mz.Click += (a, e) => { s.Zapomnij(prog); listaSig = ""; Odswiez(); WszystkieOdswiez(); };
            cm.Items.Add(mz);
            return cm;
        }

        // ⤢ pełna lista programów: kolumny z sortowaniem (klik w nagłówek, zapamiętane), szukanie, poszerzanie kolumn, to samo menu
        Window wszWin; ListView wszLv; TextBox wszSzukaj;
        void Wszystkie()
        {
            if (wszWin != null) { wszWin.Show(); wszWin.Activate(); WszystkieOdswiez(); return; }
            wszWin = new Window { Title = "Velivo Procesor – programy", Width = 900, Height = 560, Background = Bg, Owner = win, WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = win.Icon };
            wszWin.Closing += (a, e) => { e.Cancel = true; wszWin.Hide(); };
            var root = new DockPanel { Margin = new Thickness(10) };
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var h = T("klik w nagłówek – sortowanie · prawy przycisk – ustawienia programu", 11, Dim); h.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(h, Dock.Right); top.Children.Add(h);
            wszSzukaj = new TextBox { Width = 260, Background = Card, Foreground = Fg, BorderBrush = Line, CaretBrush = Fg, Padding = new Thickness(6, 3, 6, 3), ToolTip = "🔍 Szukaj programu" };
            wszSzukaj.TextChanged += (a, e) => WszystkieOdswiez(); top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { T("🔍 ", 13, Fg), wszSzukaj } });
            DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
            var gv = new GridView { ColumnHeaderContainerStyle = NaglowekStyl() };
            foreach (var (n, prop, sort, wid) in new[] { ("Program", "Prog", "Prog", 180.0), ("Tryb", "TrybOpis", "TrybOpis", 150), ("Średnio", "SrT", "Sr", 70), ("Szczyt", "SzczytT", "Szczyt", 70), ("Karta", "KartaT", "Karta", 60), ("Czas pracy", "CzasT", "N", 85), ("Priorytet", "PrioT", "Prio", 85), ("Studio", "StudioT", "StudioOn", 65), ("ProBalance", "PbT", "BezPbOn", 85) })
            {
                var hd = new GridViewColumnHeader { Content = n, Tag = sort }; hd.Click += (a, e) => Sortuj((string)hd.Tag);
                gv.Columns.Add(new GridViewColumn { Header = hd, DisplayMemberBinding = new System.Windows.Data.Binding(prop), Width = wid });
            }
            wszLv = new ListView { View = gv, Background = Card, Foreground = Fg, BorderBrush = Line, ItemContainerStyle = WierszStyl() };
            wszLv.PreviewMouseRightButtonDown += (a, e) =>
            {
                var it = ItemsControl.ContainerFromElement(wszLv, e.OriginalSource as DependencyObject) as ListViewItem;
                if (it?.DataContext is Silnik.ProgInfo x) { it.IsSelected = true; wszLv.ContextMenu = MenuProgramu(x.Prog); } else wszLv.ContextMenu = null;
            };
            root.Children.Add(wszLv);
            wszWin.Content = root; WszystkieOdswiez(); wszWin.Show();
        }
        void Sortuj(string kol)
        {
            var (k, malej) = SortTeraz(); malej = k == kol ? !malej : kol != "Prog" && kol != "TrybOpis";   // liczby od największej, nazwy od A
            s.Put("lista-sort", kol + (malej ? ":d" : ":a")); WszystkieOdswiez();
        }
        (string, bool) SortTeraz() { var a = (s.Get("lista-sort") ?? "N:d").Split(':'); return (a[0], a.Length > 1 && a[1] == "d"); }
        void WszystkieOdswiez()
        {
            if (wszLv == null) return;
            var q = wszSzukaj.Text.Trim(); var (k, malej) = SortTeraz();
            var dane = s.ProgramyDane().Where(x => q.Length == 0 || x.Prog.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            var pi = typeof(Silnik.ProgInfo).GetProperty(k) ?? typeof(Silnik.ProgInfo).GetProperty("N");
            dane = malej ? dane.OrderByDescending(x => pi.GetValue(x)) : dane.OrderBy(x => pi.GetValue(x));
            string zazn = (wszLv.SelectedItem as Silnik.ProgInfo)?.Prog;
            var l = dane.ToList(); wszLv.ItemsSource = l; wszLv.SelectedItem = l.FirstOrDefault(x => x.Prog == zazn);
            wszWin.Title = "Velivo Procesor – programy (" + l.Count + ")";
            foreach (var c in ((GridView)wszLv.View).Columns) { var hd = (GridViewColumnHeader)c.Header; var n = ((string)hd.Content).TrimEnd(' ', '▲', '▼'); hd.Content = n + ((string)hd.Tag == k ? (malej ? " ▼" : " ▲") : ""); }
        }
        // wiersz tabeli: własny wygląd (zwykły Windows - jasnoniebieskie zaznaczenie z białym tekstem), zaznaczony fioletem, pod myszą jaśniej, cienka linia
        static Style WierszStyl()
        {
            var t = new ControlTemplate(typeof(ListViewItem)); var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.BorderBrushProperty, Sep); bd.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1)); bd.SetValue(Border.PaddingProperty, new Thickness(0, 4, 0, 4)); bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.AppendChild(new FrameworkElementFactory(typeof(GridViewRowPresenter))); t.VisualTree = bd;
            var hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hov.Setters.Add(new Setter(Border.BackgroundProperty, Hov, "bd")); t.Triggers.Add(hov);
            var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; sel.Setters.Add(new Setter(Border.BackgroundProperty, Sel, "bd")); t.Triggers.Add(sel);
            var st = new Style(typeof(ListViewItem)); st.Setters.Add(new Setter(Control.TemplateProperty, t)); st.Setters.Add(new Setter(Control.ForegroundProperty, Fg)); return st;
        }
        // nagłówek kolumny: ciemny, z uchwytem PART_HeaderGripper (bez niego kolumn nie da się poszerzać)
        static Style NaglowekStyl()
        {
            var t = new ControlTemplate(typeof(GridViewColumnHeader)); var g = new FrameworkElementFactory(typeof(Grid));
            var bd = new FrameworkElementFactory(typeof(Border)); bd.SetValue(Border.BackgroundProperty, Bg); bd.SetValue(Border.BorderBrushProperty, Line); bd.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 1, 1)); bd.SetValue(Border.PaddingProperty, new Thickness(8, 5, 8, 5));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter)); bd.AppendChild(cp); g.AppendChild(bd);
            var th = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.Thumb), "PART_HeaderGripper"); th.SetValue(FrameworkElement.WidthProperty, 6.0); th.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            th.SetValue(FrameworkElement.CursorProperty, Cursors.SizeWE); th.SetValue(UIElement.OpacityProperty, 0.0); g.AppendChild(th); t.VisualTree = g;
            var st = new Style(typeof(GridViewColumnHeader)); st.Setters.Add(new Setter(Control.TemplateProperty, t)); st.Setters.Add(new Setter(Control.ForegroundProperty, Dim)); st.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left)); st.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand)); return st;
        }

        void RysujWykres()
        {
            wykres.Children.Clear(); double w = Math.Max(10, wykres.ActualWidth), h = wykres.Height; int n = hCpu.Length;
            Polyline L(Brush c, double th) { var pl = new Polyline { Stroke = c, StrokeThickness = th }; wykres.Children.Add(pl); return pl; }
            // ⚖ ProBalance: pionowa linia w chwili, gdy uspokoił program w tle (jak Process Lasso)
            for (int i = 1; i < n; i++)
            {
                int c = hPb[(hPos + i) % n], c0 = hPb[(hPos + i - 1) % n]; if (c <= c0) continue;
                double x = w * i / (n - 1);
                wykres.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = h, Stroke = Ok, StrokeThickness = 1, Opacity = 0.8, ToolTip = "ProBalance uspokoił program w tle (" + c + ")" });
            }
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
