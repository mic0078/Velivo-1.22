using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VelivoMonitor
{
    // 📈 Velivo Monitor: sieć ↑↓, taktowanie i obciążenie procesora - napis na pasku zadań (obok zegara) i mały widżet na pulpicie z wykresem z ostatniej minuty.
    // Odczyt co 1 s, bez dodatkowych bibliotek: sieć z kart sieciowych, procesor z Windows (GetSystemTimes, licznik „% Processor Performance”).
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 1 && (args[0] == "--internet-wyl" || args[0] == "--internet-wl")) { Internet.Ustaw(args[0] == "--internet-wyl"); return; }   // uruchomione jako administrator tylko po to
            using var one = new System.Threading.Mutex(true, @"Local\VelivoMonitor", out bool first);
            if (!first) return;   // jedna kopia
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var m = new Monitor();
            app.Startup += (s, e) => m.Start();
            app.Run();
        }
    }

    sealed class Monitor
    {
        static readonly string ExePath = System.Reflection.Assembly.GetEntryAssembly().Location;
        // ---------- ustawienia (plik tekstowy klucz=wartość) ----------
        static readonly string Dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velivo", "Monitor");
        static readonly string SetFile = System.IO.Path.Combine(Dir, "ustawienia.txt");
        readonly Dictionary<string, string> set = new Dictionary<string, string>();
        string Get(string k, string d = null) => set.TryGetValue(k, out var v) ? v : d;
        double GetD(string k, double d) => double.TryParse(Get(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
        void Put(string k, object v)
        {
            set[k] = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            try { Directory.CreateDirectory(Dir); File.WriteAllLines(SetFile + ".tmp", set.Select(x => x.Key + "=" + x.Value)); if (File.Exists(SetFile)) File.Replace(SetFile + ".tmp", SetFile, null); else File.Move(SetFile + ".tmp", SetFile); } catch (Exception) { }
        }

        // ---------- pomiar ----------
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr inBuf, int inLen, [Out] byte[] outBuf, int outLen);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string src, IntPtr user, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
        [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr c, uint fmt, IntPtr type, out PdhValue v);
        [StructLayout(LayoutKind.Sequential)] struct PdhValue { public uint Status; public uint Pad; public double Value; }

        long lastIdle, lastKer, lastUsr, lastSent = -1, lastRecv = -1; DateTime lastAt;
        IntPtr pdhQ, pdhC; double maxMhz;
        public double Up, Down, Cpu, Ghz;   // B/s, B/s, %, GHz
        const int N = 60; readonly double[] hUp = new double[N], hDown = new double[N], hGhz = new double[N]; int hPos;

        void InitCpu()
        {
            try
            {   // najwyższe taktowanie bazowe (MHz) z Windows - PROCESSOR_POWER_INFORMATION: 6 × uint, MaxMhz na pozycji 2
                int n = Environment.ProcessorCount; var b = new byte[24 * n];
                if (CallNtPowerInformation(11, IntPtr.Zero, 0, b, b.Length) == 0) maxMhz = BitConverter.ToUInt32(b, 8);
            }
            catch (Exception) { }
            try { if (PdhOpenQuery(null, IntPtr.Zero, out pdhQ) == 0 && PdhAddEnglishCounter(pdhQ, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out pdhC) == 0) PdhCollectQueryData(pdhQ); else pdhQ = IntPtr.Zero; } catch (Exception) { pdhQ = IntPtr.Zero; }
        }

        void Sample()
        {
            var now = DateTime.UtcNow; double sec = lastAt == default ? 1 : Math.Max(0.2, (now - lastAt).TotalSeconds); lastAt = now;
            long sent = 0, recv = 0;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var st = ni.GetIPStatistics(); sent += st.BytesSent; recv += st.BytesReceived;
                }
            }
            catch (Exception) { }
            if (lastSent >= 0) { Up = Math.Max(0, (sent - lastSent) / sec); Down = Math.Max(0, (recv - lastRecv) / sec); }   // zmiana kart (Wi-Fi ↔ kabel) - bez ujemnych wartości
            lastSent = sent; lastRecv = recv;
            if (GetSystemTimes(out var idle, out var ker, out var usr))
            {
                long di = idle - lastIdle, dt = (ker - lastKer) + (usr - lastUsr);   // czas jądra zawiera czas bezczynności
                if (lastKer != 0 && dt > 0) Cpu = Math.Max(0, Math.Min(100, 100.0 * (dt - di) / dt));
                lastIdle = idle; lastKer = ker; lastUsr = usr;
            }
            if (pdhQ != IntPtr.Zero && maxMhz > 0 && PdhCollectQueryData(pdhQ) == 0 && PdhGetFormattedCounterValue(pdhC, 0x200, IntPtr.Zero, out var v) == 0)   // PDH_FMT_DOUBLE
                Ghz = maxMhz * v.Value / 100 / 1000;   // „% Processor Performance” ponad 100 = turbo
            else if (maxMhz > 0) Ghz = maxMhz / 1000;
            hUp[hPos] = Up; hDown[hPos] = Down; hGhz[hPos] = Ghz; hPos = (hPos + 1) % N;
        }

        static string Rate(double b) => b < 1000 * 1024 ? (b / 1024).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " KB/s" : (b / 1048576).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " MB/s";
        string GhzText => Ghz > 0 ? Ghz.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GHz" : "– GHz";

        // ---------- pasek zadań ----------
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        static readonly IntPtr Top = new IntPtr(-1);

        Window bar; TextBlock barTxt;
        void BuildBar()
        {
            bool light = false;
            try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); light = (k?.GetValue("SystemUsesLightTheme") as int?) == 1; } catch (Exception) { }
            barTxt = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, Foreground = light ? Brushes.Black : Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
            bar = new Window { Title = "Velivo Monitor – pasek", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), ShowInTaskbar = false, Topmost = true, SizeToContent = SizeToContent.Width, Height = 40, Content = barTxt,
                ToolTip = "Velivo Monitor – przeciągnij w lewo/prawo, aby przesunąć; prawy przycisk – menu" };
            bar.ContextMenu = Menu();
            int dragX = 0; double dragOff = 0; bool drag = false;   // przesuwanie wzdłuż paska: zapamiętany odstęp od zegara
            bar.MouseLeftButtonDown += (s, e) => { drag = true; dragX = System.Windows.Forms.Cursor.Position.X; dragOff = GetD("pasek-odstep", 0); bar.CaptureMouse(); };
            bar.MouseMove += (s, e) => { if (!drag) return; set["pasek-odstep"] = Math.Max(0, dragOff + (dragX - System.Windows.Forms.Cursor.Position.X)).ToString(System.Globalization.CultureInfo.InvariantCulture); PlaceBar(); };
            bar.MouseLeftButtonUp += (s, e) => { if (!drag) return; drag = false; bar.ReleaseMouseCapture(); Put("pasek-odstep", GetD("pasek-odstep", 0)); };
            bar.Show(); PlaceBar();
        }
        void PlaceBar()
        {
            if (bar == null) return;
            var tray = FindWindow("Shell_TrayWnd", null); if (tray == IntPtr.Zero || !GetWindowRect(tray, out var tr)) return;
            var hwnd = new WindowInteropHelper(bar).Handle; if (hwnd == IntPtr.Zero) return;
            var nt = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            int right = nt != IntPtr.Zero && GetWindowRect(nt, out var nr) ? nr.L : tr.R - 200;   // tuż przed ikonami przy zegarze
            GetWindowRect(hwnd, out var me); int w = me.R - me.L, h = tr.B - tr.T;
            int x = right - w - (int)GetD("pasek-odstep", 0);
            SetWindowPos(hwnd, Top, x, tr.T, w, h, 0x0010);   // SWP_NOACTIVATE - zawsze nad paskiem zadań
        }

        // ---------- widżet na pulpicie ----------
        Window wid; TextBlock wUp, wDown, wCpu; Polyline pUp, pDown, pGhz;
        const double GW = 150, GH = 32;
        void BuildWidget()
        {
            var dim = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2));
            TextBlock T(Brush c) => new TextBlock { Foreground = c, FontSize = 11, FontFamily = new FontFamily("Segoe UI") };
            wUp = T(new SolidColorBrush(Color.FromRgb(0xF5, 0xA5, 0x24))); wDown = T(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))); wCpu = T(new SolidColorBrush(Color.FromRgb(0x7C, 0x9C, 0xFF)));
            var row1 = new DockPanel(); DockPanel.SetDock(wUp, Dock.Left); row1.Children.Add(wUp); wDown.HorizontalAlignment = HorizontalAlignment.Right; row1.Children.Add(wDown);
            var cv = new Canvas { Width = GW, Height = GH, Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x24)), Margin = new Thickness(0, 3, 0, 0), ClipToBounds = true };
            Polyline P(Color c, bool dash) { var p = new Polyline { Stroke = new SolidColorBrush(c), StrokeThickness = 1.2 }; if (dash) p.StrokeDashArray = new DoubleCollection { 3, 2 }; cv.Children.Add(p); return p; }
            pGhz = P(Color.FromRgb(0x7C, 0x9C, 0xFF), true); pDown = P(Color.FromRgb(0x4A, 0xDE, 0x80), false); pUp = P(Color.FromRgb(0xF5, 0xA5, 0x24), false);
            var st = new StackPanel(); st.Children.Add(row1); st.Children.Add(wCpu); st.Children.Add(cv);
            wid = new Window { Title = "Velivo Monitor – widżet", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight,
                Topmost = Get("widzet-wierzch", "1") == "1", WindowStartupLocation = WindowStartupLocation.Manual, Opacity = Math.Max(0.3, Math.Min(1, GetD("widzet-alfa", 0.9))),
                Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0F, 0x12, 0x17)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x32, 0x40)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 5, 7, 6), Child = st },
                ToolTip = "Wykres z ostatniej minuty: wysyłanie (pomarańczowy), pobieranie (zielony), taktowanie procesora (niebieski, przerywany).\nPrzeciągnij – przesuń · kółko myszy – przezroczystość · prawy przycisk – menu" };
            double x = GetD("widzet-x", SystemParameters.WorkArea.Right - 190), y = GetD("widzet-y", SystemParameters.WorkArea.Top + 20);
            if (x < SystemParameters.VirtualScreenLeft || x > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 || y < SystemParameters.VirtualScreenTop || y > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 30)
            { x = SystemParameters.WorkArea.Right - 190; y = SystemParameters.WorkArea.Top + 20; }   // odłączony drugi monitor - widżet wraca na główny ekran
            wid.Left = x; wid.Top = y;
            wid.MouseLeftButtonDown += (s, e) => { try { wid.DragMove(); Put("widzet-x", wid.Left); Put("widzet-y", wid.Top); } catch (Exception) { } };
            wid.MouseWheel += (s, e) => { wid.Opacity = Math.Max(0.3, Math.Min(1, wid.Opacity + (e.Delta > 0 ? 0.1 : -0.1))); Put("widzet-alfa", wid.Opacity); };
            wid.ContextMenu = Menu();
            wid.Show();
        }
        void DrawGraph()
        {
            double mx = Math.Max(1024, Math.Max(hUp.Max(), hDown.Max())), gmx = Math.Max(0.1, hGhz.Max() * 1.15);
            void Line(Polyline p, double[] h, double top)
            {
                var pts = new PointCollection(N);
                for (int i = 0; i < N; i++) { double v = h[(hPos + i) % N]; pts.Add(new Point(i * GW / (N - 1), GH - 1 - (GH - 2) * Math.Min(1, v / top))); }
                p.Points = pts;
            }
            Line(pUp, hUp, mx); Line(pDown, hDown, mx); Line(pGhz, hGhz, gmx);
        }

        // ---------- 🎞 ikona przy zegarze: słupki = obciążenie procesora z ostatnich 5 s (wygląd Velivo: fiolet → turkus) ----------
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        readonly double[] cpuH = new double[5]; IntPtr iconH;
        void TrayIcon()
        {
            Array.Copy(cpuH, 1, cpuH, 0, cpuH.Length - 1); cpuH[cpuH.Length - 1] = Cpu;
            const int S = 32;
            using (var bmp = new System.Drawing.Bitmap(S, S))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Point(0, 0), new System.Drawing.Point(S, S), System.Drawing.Color.FromArgb(0x6D, 0x28, 0xD9), System.Drawing.Color.FromArgb(0x22, 0xB8, 0xD8)))
                {
                    int r = 7; path.AddArc(0, 0, r * 2, r * 2, 180, 90); path.AddArc(S - 1 - r * 2, 0, r * 2, r * 2, 270, 90); path.AddArc(S - 1 - r * 2, S - 1 - r * 2, r * 2, r * 2, 0, 90); path.AddArc(0, S - 1 - r * 2, r * 2, r * 2, 90, 90); path.CloseFigure();
                    g.FillPath(br, path);
                }
                for (int i = 0; i < cpuH.Length; i++)
                {   // kolor wg obciążenia: do 50% biały, do 80% żółty, wyżej czerwony
                    double c = cpuH[i]; var col = c < 50 ? System.Drawing.Color.FromArgb(240, 255, 255, 255) : c < 80 ? System.Drawing.Color.FromArgb(255, 0xFA, 0xCC, 0x15) : System.Drawing.Color.FromArgb(255, 0xF8, 0x5A, 0x5A);
                    float h = 3 + 21 * (float)Math.Min(1, c / 100);
                    using (var wb = new System.Drawing.SolidBrush(col)) g.FillRectangle(wb, 5 + i * 4.6f, 27 - h, 3.2f, h);
                }
                var nh = bmp.GetHicon(); var ico = System.Drawing.Icon.FromHandle(nh);
                tray.Icon = ico;
                if (iconH != IntPtr.Zero) DestroyIcon(iconH);   // stara ikona - bez wycieku uchwytów (co sekundę nowa)
                iconH = nh;
            }
            tray.Text = "Velivo Monitor – procesor " + Cpu.ToString("0") + " %, " + GhzText;
        }

        // ---------- menu (zasobnik, pasek, widżet) ----------
        System.Windows.Forms.NotifyIcon tray;
        ContextMenu Menu()
        {
            var cm = new ContextMenu();
            MenuItem It(string h, Action a, Func<bool> chk = null) { var mi = new MenuItem { Header = h, IsCheckable = chk != null }; mi.Click += (s, e) => a(); cm.Items.Add(mi); if (chk != null) cm.Opened += (s, e) => mi.IsChecked = chk(); return mi; }
            It("Widżet na pulpicie", ToggleWidget, () => wid != null);
            It("Widżet zawsze na wierzchu", ToggleTop, () => Get("widzet-wierzch", "1") == "1");
            It("Na pasku zadań", ToggleBar, () => bar != null);
            It("🌐 Internet wyłączony (sieć domowa działa)", ToggleNet, Internet.Wylaczony);
            It("Uruchamiaj z Windows", ToggleAuto, AutoOn);
            cm.Items.Add(new Separator());
            It("Zakończ Velivo Monitor", Quit);
            return cm;
        }
        void ToggleWidget() { if (wid != null) { wid.Close(); wid = null; Put("widzet", "0"); } else { BuildWidget(); Put("widzet", "1"); } }
        void ToggleTop() { bool on = Get("widzet-wierzch", "1") != "1"; Put("widzet-wierzch", on ? "1" : "0"); if (wid != null) wid.Topmost = on; }
        void ToggleBar() { if (bar != null) { bar.Close(); bar = null; Put("pasek", "0"); } else { BuildBar(); Put("pasek", "1"); } }
        // 🌐 internet wył./wł.: reguły zapory Windows (zmiana wymaga jednego „Tak” administratora - ten program uruchomiony z prawami tylko na tę chwilę)
        void ToggleNet()
        {
            bool off = !Internet.Wylaczony(true);
            try { File.Delete(Internet.Wynik); } catch (Exception) { }
            try { using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExePath, off ? "--internet-wyl" : "--internet-wl") { UseShellExecute = true, Verb = "runas" })) p?.WaitForExit(15000); }
            catch (Exception ex) { MessageBox.Show("Zmiana nie została wykonana – brak zgody administratora (okienko Windows „Tak / Nie”).\n" + ex.Message, "Velivo Monitor", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            string res = null; try { res = File.ReadAllText(Internet.Wynik); } catch (Exception) { }
            bool now = Internet.Wylaczony(true);
            if (res == null || res.StartsWith("BŁĄD") || now != off)
                MessageBox.Show((off ? "Nie udało się wyłączyć internetu." : "Nie udało się włączyć internetu.") + "\nWynik zmiany zapory: " + (res ?? "brak (program z prawami administratora się nie uruchomił albo zablokował go antywirus)") + "\nReguła w zaporze: " + (now ? "jest" : "nie ma") + "\nZapora Windows: " + (Internet.ZaporaWindows() ? "włączona" : "WYŁĄCZONA"), "Velivo Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (off && Internet.Wylaczony(true) && !Internet.ZaporaWindows())
                MessageBox.Show("Reguła jest zapisana, ale zapora Windows jest wyłączona (np. zastąpiona zaporą antywirusa, jak Bitdefender) – internet nadal działa.\nWłącz zaporę Windows albo zablokuj internet w zaporze antywirusa.", "Velivo Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        bool AutoOn() { try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("VelivoMonitor") != null; } catch (Exception) { return false; } }
        void ToggleAuto() { try { using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey); if (AutoOn()) k.DeleteValue("VelivoMonitor", false); else k.SetValue("VelivoMonitor", "\"" + ExePath + "\""); } catch (Exception) { } }
        void Quit() { tray.Visible = false; tray.Dispose(); Application.Current.Shutdown(); }

        public void Start()
        {
            try { if (File.Exists(SetFile)) foreach (var l in File.ReadAllLines(SetFile)) { int k = l.IndexOf('='); if (k > 0) set[l.Substring(0, k)] = l.Substring(k + 1); } } catch (Exception) { }
            InitCpu(); Sample();
            tray = new System.Windows.Forms.NotifyIcon { Text = "Velivo Monitor", Visible = true };
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(ExePath); } catch (Exception) { }
            var tm = new System.Windows.Forms.ContextMenuStrip();
            void Add(string h, Action a, Func<bool> chk = null) { var it = new System.Windows.Forms.ToolStripMenuItem(h); it.Click += (s, e) => a(); if (chk != null) tm.Opening += (s, e) => it.Checked = chk(); tm.Items.Add(it); }
            Add("Widżet na pulpicie", ToggleWidget, () => wid != null);
            Add("Widżet zawsze na wierzchu", ToggleTop, () => Get("widzet-wierzch", "1") == "1");
            Add("Na pasku zadań", ToggleBar, () => bar != null);
            Add("🌐 Internet wyłączony (sieć domowa działa)", ToggleNet, Internet.Wylaczony);
            Add("Uruchamiaj z Windows", ToggleAuto, AutoOn);
            tm.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            Add("Zakończ Velivo Monitor", Quit);
            tray.ContextMenuStrip = tm;
            // 🧩 Velivo Centrum: ikona chowa się przy działającym Centrum (chyba że wybrano „też przy zegarze”), prawy przycisk w Centrum = to menu
            Velivo.CentrumZasobnik.Podlacz(tray);
            tray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ToggleWidget(); };
            if (Get("pasek", "1") == "1") BuildBar();
            if (Get("widzet", "1") == "1") BuildWidget();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (s, e) =>
            {
                Sample();
                TrayIcon();
                if (bar != null) { barTxt.Text = (Internet.Wylaczony() ? "🌐✕ " : "") + "↑ " + Rate(Up) + "  CPU: " + GhzText + "\n↓ " + Rate(Down) + "  CPU: " + Cpu.ToString("0") + " %"; PlaceBar(); }   // co sekundę z powrotem nad paskiem (kliknięcie w pasek go przykrywa)
                if (wid != null) { wUp.Text = "↑ " + Rate(Up); wDown.Text = "↓ " + Rate(Down); wCpu.Text = (Internet.Wylaczony() ? "🌐 bez internetu · " : "") + "CPU " + GhzText + "  ·  " + Cpu.ToString("0") + " %"; DrawGraph(); }
            };
            t.Start();
        }
    }

    // 🌐 internet wyłączony, sieć domowa działa: dwie reguły zapory Windows blokują ruch do/z adresów publicznych;
    // adresy domowe (192.168.x, 10.x, 172.16–31.x), lokalne IPv6, rozgłoszenia i multicast - bez zmian
    static class Internet
    {
        const string Nazwa = "Velivo Monitor – bez internetu";
        const string Publiczne = "1.0.0.0-9.255.255.255,11.0.0.0-100.63.255.255,100.128.0.0-126.255.255.255,128.0.0.0-169.253.255.255,169.255.0.0-172.15.255.255,172.32.0.0-192.167.255.255,192.169.0.0-223.255.255.255,2000::/3";
        static object Policy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
        static object Rules(object pol) => pol.GetType().InvokeMember("Rules", System.Reflection.BindingFlags.GetProperty, null, pol, null);
        static DateTime _at; static bool _st;
        internal static bool Wylaczony() => Wylaczony(false);
        internal static bool Wylaczony(bool teraz)
        {
            if (!teraz && (DateTime.UtcNow - _at).TotalSeconds < 2) return _st;   // odczyt zapory najwyżej co 2 s
            _at = DateTime.UtcNow;
            try { var r = Rules(Policy()); r.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, r, new object[] { Nazwa }); _st = true; } catch (Exception) { _st = false; }
            return _st;
        }
        // czy zapora Windows działa w obecnej sieci (bez niej reguła nic nie blokuje)
        internal static bool ZaporaWindows()
        {
            try
            {
                var pol = Policy(); var t = pol.GetType(); int prof = (int)t.InvokeMember("CurrentProfileTypes", System.Reflection.BindingFlags.GetProperty, null, pol, null);
                foreach (int pf in new[] { 1, 2, 4 }) if ((prof & pf) != 0 && !(bool)t.InvokeMember("FirewallEnabled", System.Reflection.BindingFlags.GetProperty, null, pol, new object[] { pf })) return false;
                return true;
            }
            catch (Exception) { return true; }
        }
        internal static readonly string Wynik = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velivo", "Monitor", "internet-wynik.txt");
        internal static void Ustaw(bool off)
        {
            string res = "OK";
            try
            {
                var r = Rules(Policy()); var t = r.GetType();
                for (int i = 0; i < 4; i++) try { t.InvokeMember("Remove", System.Reflection.BindingFlags.InvokeMethod, null, r, new object[] { Nazwa }); } catch (Exception) { }   // stare reguły precz
                if (off) foreach (int dir in new[] { 1, 2 })   // 1 = przychodzące, 2 = wychodzące
                {
                    var rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")); var rt = rule.GetType();
                    void P(string n, object v) => rt.InvokeMember(n, System.Reflection.BindingFlags.SetProperty, null, rule, new[] { v });
                    P("Name", Nazwa); P("Description", "Velivo Monitor: internet wyłączony, sieć domowa działa. Usuń regułę albo włącz internet w Velivo Monitor."); P("Protocol", 256); P("RemoteAddresses", Publiczne);
                    P("Direction", dir); P("Action", 0); P("Profiles", 0x7FFFFFFF); P("Enabled", true);
                    t.InvokeMember("Add", System.Reflection.BindingFlags.InvokeMethod, null, r, new[] { rule });
                }
            }
            catch (Exception ex) { res = "BŁĄD: " + (ex.InnerException ?? ex).Message; }
            try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Wynik)); System.IO.File.WriteAllText(Wynik, res); } catch (Exception) { }
        }
    }
}
