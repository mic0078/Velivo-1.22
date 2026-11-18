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

        static string Rate(double b) => (b < 1000 * 1024 ? (b / 1024).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " KB/s" : (b / 1048576).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " MB/s").PadLeft(11);   // stała liczba znaków (czcionka o stałej szerokości na pasku)
        string GhzText => Ghz > 0 ? Ghz.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GHz" : "– GHz";

        // ---------- pasek zadań ----------
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        static readonly IntPtr Top = new IntPtr(-1);

        Window bar; TextBlock barTxt, bUp, bDown, bGhz, bCpu, lUp, lDown; System.Windows.Threading.DispatcherTimer ledTm; readonly Random ledRnd = new Random(); bool ledLight;
        void BuildBar()
        {
            bool light = false;
            try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); light = (k?.GetValue("SystemUsesLightTheme") as int?) == 1; } catch (Exception) { }
            barTxt = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, Foreground = light ? Brushes.Black : Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
            bar = new Window { Title = "Velivo Monitor – pasek", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), ShowInTaskbar = false, Topmost = true, Width = BarW(), Height = 40, Content = BarContent(light),   /* stała szerokość - napis nie skacze */
                ToolTip = "Velivo Monitor – przeciągnij w lewo/prawo, aby przesunąć; prawy przycisk – menu" };
            bar.ContextMenu = Menu();
            int dragX = 0; double dragOff = 0; bool drag = false;   // przesuwanie wzdłuż paska: zapamiętany odstęp od zegara
            bar.MouseLeftButtonDown += (s, e) => { drag = true; dragX = System.Windows.Forms.Cursor.Position.X; dragOff = GetD("pasek-odstep", 0); bar.CaptureMouse(); };
            bar.MouseMove += (s, e) => { if (!drag) return; set["pasek-odstep"] = Math.Max(0, dragOff + (dragX - System.Windows.Forms.Cursor.Position.X)).ToString(System.Globalization.CultureInfo.InvariantCulture); PlaceBar(); };
            bar.MouseLeftButtonUp += (s, e) => { if (!drag) return; drag = false; bar.ReleaseMouseCapture(); Put("pasek-odstep", GetD("pasek-odstep", 0)); };
            bar.Show(); PlaceBar();
        }
        // 🧩 układ C (wybór właściciela): kropka stanu internetu, ↑ ↓ z jednostkami (czcionka Windows, cyfry równej szerokości), GHz i %
        UIElement BarContent(bool light)
        {
            var fg = light ? Brushes.Black : Brushes.White; var blue = new SolidColorBrush(light ? Color.FromRgb(0x3B, 0x5B, 0xDB) : Color.FromRgb(0x7C, 0x9C, 0xFF));
            TextBlock T(Brush c) { var t = new TextBlock { Foreground = c, FontSize = 12, FontFamily = new FontFamily("Segoe UI") }; System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular); return t; }
            bUp = T(fg); bDown = T(fg); bGhz = T(blue); bCpu = T(blue);
            TextBlock Led(string a) { var t = new TextBlock { Text = a, FontSize = 12, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 4, 0), Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 7, Opacity = 0 } }; return t; }
            lUp = Led("↑"); lDown = Led("↓"); ledLight = light;
            var r1 = new StackPanel { Orientation = Orientation.Horizontal }; r1.Children.Add(lUp); r1.Children.Add(bUp);
            var r2 = new StackPanel { Orientation = Orientation.Horizontal }; r2.Children.Add(lDown); r2.Children.Add(bDown);
            ledTm?.Stop(); ledTm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) }; ledTm.Tick += (s, e) => LedTick(); ledTm.Start();
            var c1 = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 74 }; c1.Children.Add(r1); c1.Children.Add(r2);
            var c2 = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 56 }; c2.Children.Add(bGhz); c2.Children.Add(bCpu);
            if (!Pok("up")) r1.Visibility = Visibility.Collapsed; if (!Pok("down")) r2.Visibility = Visibility.Collapsed;
            if (!Pok("ghz")) bGhz.Visibility = Visibility.Collapsed; if (!Pok("cpu")) bCpu.Visibility = Visibility.Collapsed;
            if (!Pok("up") && !Pok("down")) c1.Visibility = Visibility.Collapsed; if (!Pok("ghz") && !Pok("cpu")) c2.Visibility = Visibility.Collapsed;
            bAb = T(new SolidColorBrush(light ? Color.FromRgb(0x6D, 0x28, 0xD9) : Color.FromRgb(0xA7, 0x8B, 0xFA))); bAb.FontWeight = FontWeights.SemiBold; bAbSub = T(light ? Brushes.DimGray : new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA3)));
            bAbCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 58, Visibility = Visibility.Collapsed }; bAbCol.Children.Add(bAb); bAbCol.Children.Add(bAbSub);
            var all = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            all.Children.Add(c1); all.Children.Add(c2); all.Children.Add(bAbCol);
            barTxt = bUp; return all;
        }
        // 🧩 co pokazuje pasek (wybór w menu): wysyłanie, pobieranie, GHz, użycie procesora; szerokość tylko na wybrane kolumny
        bool Pok(string k) => Get("pasek-" + k, "1") == "1";
        TextBlock bAb, bAbSub; StackPanel bAbCol;
        bool BarAb => abOk && Pok("ab");   // Audio Balance na pasku - tylko gdy działa
        double BarW() => 8 + (Pok("up") || Pok("down") ? 74 : 0) + (Pok("ghz") || Pok("cpu") ? 56 : 0) + (BarAb ? 58 : 0);
        void BarPokaz(string k)
        {
            Put("pasek-" + k, Pok(k) ? "0" : "1");
            if (!new[] { "up", "down", "ghz", "cpu", "ab" }.Any(Pok)) Put("pasek-" + k, "1");   // choć jedna wartość zostaje
            if (bar != null) { bar.Content = BarContent(ledLight); bar.Width = BarW(); BarText(); bar.UpdateLayout(); PlaceBar(); }
            if (wid != null) WidgetText();
        }
        // napis na pasku: równe kolumny w obu wierszach (czcionka o stałej szerokości), strzałki świecą w LedTick
        void BarText()
        {
            bool off = Internet.Wylaczony();
            bUp.Text = Rate(Up).Trim(); bDown.Text = Rate(Down).Trim();
            bGhz.Text = GhzText; bCpu.Text = Cpu.ToString("0") + " %";
            var abV = BarAb ? Visibility.Visible : Visibility.Collapsed;
            if (bAbCol.Visibility != abV) { bAbCol.Visibility = abV; bar.Width = BarW(); }   // Audio Balance włączony / wyłączony - pasek dopasowuje szerokość
            if (BarAb) { bAb.Text = abOut == "-" ? "– dB" : abOut + " dB"; bAbSub.Text = abSub; }
            bar.ToolTip = (off ? "Internet WYŁĄCZONY (sieć domowa działa)\n" : "Internet włączony\n") +  "Przeciągnij w lewo/prawo, aby przesunąć; prawy przycisk – menu";
        }
        // 💡 strzałki świecą jak diody LED: błyskają, gdy płyną dane (im więcej, tym częściej); internet - ↑ żółta, ↓ zielona; internet wyłączony (sama sieć domowa) - fioletowe
        void LedTick()
        {
            if (bar == null) { ledTm?.Stop(); return; }
            bool off = Internet.Wylaczony();
            void One(TextBlock t, double v, Color c)
            {
                if (off) c = Color.FromRgb(0xB0, 0x6C, 0xFF);
                bool on = v > 300 && ledRnd.NextDouble() < Math.Min(0.85, 0.25 + Math.Log10(v / 300 + 1) / 4);
                t.Foreground = new SolidColorBrush(on ? c : ledLight ? Color.FromRgb(0xA0, 0xA0, 0xA0) : Color.FromRgb(0x55, 0x55, 0x55));
                var ef = (System.Windows.Media.Effects.DropShadowEffect)t.Effect; ef.Color = c; ef.Opacity = on ? 0.9 : 0;
            }
            One(lUp, Up, Color.FromRgb(0xF5, 0xC5, 0x18)); One(lDown, Down, Color.FromRgb(0x4A, 0xDE, 0x80));
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
        // 🧩 karta A (wybór właściciela 2026-11-18): 200 × 58, wiersz Monitora i wiersz Audio Balance, każdy z wykresem z ostatniej minuty w tle;
        // bez działającego Audio Balance - sam górny wiersz
        Window wid; TextBlock wUp, wDown, wCpu, wAb, wAbSub; FrameworkElement wAbRow, wSep, wNetRow; Polygon pUp, pDown; Polyline pGhz; Polygon pAb, pGr; double maxDown, maxUp;
        const double GW = 184, GH = 22, BH = 11;   // lustro: od środka pobieranie w górę, wysyłanie w dół - każde ze swoją skalą
        void BuildWidget()
        {
            TextBlock T(Brush c, double size = 11.5) { var tb = new TextBlock { Foreground = c, FontSize = size, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 4, Opacity = 1 } }; System.Windows.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular); return tb; }   // cyfry równej szerokości - liczby nie skaczą
            wUp = T(new SolidColorBrush(Color.FromRgb(0xF5, 0xC5, 0x18))); wDown = T(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))); wCpu = T(new SolidColorBrush(Color.FromRgb(0x7C, 0x9C, 0xFF)));
            wDown.Margin = new Thickness(7, 0, 0, 0);
            Canvas Cv(double op) => new Canvas { Width = GW, Height = GH, ClipToBounds = true, Opacity = op };
            var cv = Cv(0.28);   // wykres przygaszony - liczby na nim czytelne
            Polygon A(Color c) { var p = new Polygon { Fill = new SolidColorBrush(c) }; cv.Children.Add(p); return p; }
            pDown = A(Color.FromRgb(0x4A, 0xDE, 0x80)); pUp = A(Color.FromRgb(0xF5, 0xC5, 0x18));
            pGhz = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(0x7C, 0x9C, 0xFF)), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } }; cv.Children.Add(pGhz);
            var t1 = new DockPanel { Height = GH, LastChildFill = false }; t1.Children.Add(wUp); t1.Children.Add(wDown); DockPanel.SetDock(wCpu, Dock.Right); t1.Children.Add(wCpu);
            var row1 = new Grid(); row1.Children.Add(cv); row1.Children.Add(t1); wNetRow = row1;
            var cvA = Cv(0.4); cvA.Children.Add(new Line { X1 = 0, X2 = GW, Y1 = GH / 2, Y2 = GH / 2, Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)) });
            pGr = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(0x66, 0xFA, 0xCC, 0x15)), Stroke = new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)), StrokeThickness = 1 }; cvA.Children.Add(pGr);
            pAb = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x2E, 0xD3, 0xC6)), Stroke = new SolidColorBrush(Color.FromRgb(0x2E, 0xD3, 0xC6)), StrokeThickness = 1 }; cvA.Children.Add(pAb);   // kolory z okna Audio Balance
            wAb = T(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)), 12); wAb.FontWeight = FontWeights.SemiBold; wAbSub = T(new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA3)));
            var t2 = new DockPanel { Height = GH, LastChildFill = false }; var ikA = T(wAbSub.Foreground); ikA.Text = "🔊"; ikA.Margin = new Thickness(0, 0, 6, 0); t2.Children.Add(ikA); t2.Children.Add(wAb); DockPanel.SetDock(wAbSub, Dock.Right); t2.Children.Add(wAbSub);
            var row2 = new Grid(); row2.Children.Add(cvA); row2.Children.Add(t2); wAbRow = row2;
            wSep = new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x32, 0x40)), Margin = new Thickness(0, 3, 0, 3) };
            var st = new StackPanel(); st.Children.Add(row1); st.Children.Add(wSep); st.Children.Add(row2);
            wid = new Window { Title = "Velivo Monitor – widżet", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, SizeToContent = SizeToContent.Height, Width = GW + 16,
                Topmost = Get("widzet-wierzch", "1") == "1", WindowStartupLocation = WindowStartupLocation.Manual, Opacity = Math.Round(Math.Max(0.1, Math.Min(1, GetD("widzet-alfa", 0.9))), 1),
                Content = new Border { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x21)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x32, 0x40)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 4, 7, 4), Child = st },
                ToolTip = "Górny wiersz: wysyłanie (żółte), pobieranie (zielone), procesor – wykres z ostatniej minuty.\nDolny wiersz: Velivo Audio Balance – głośność po wyrównaniu (LUFS) i zmiana; widać go, gdy Audio Balance działa.\nPrzeciągnij – przesuń · kółko myszy – przezroczystość · prawy przycisk – menu" };
            double x = GetD("widzet-x", SystemParameters.WorkArea.Right - 220), y = GetD("widzet-y", SystemParameters.WorkArea.Top + 20);
            if (x < SystemParameters.VirtualScreenLeft || x > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 || y < SystemParameters.VirtualScreenTop || y > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 30)
            { x = SystemParameters.WorkArea.Right - 220; y = SystemParameters.WorkArea.Top + 20; }   // odłączony drugi monitor - widżet wraca na główny ekran
            wid.Left = x; wid.Top = y;
            wid.MouseLeftButtonDown += (s, e) => { try { wid.DragMove(); Put("widzet-x", wid.Left); Put("widzet-y", wid.Top); } catch (Exception) { } };
            wid.MouseWheel += (s, e) => { wid.Opacity = Math.Round(Math.Max(0.1, Math.Min(1, wid.Opacity + (e.Delta > 0 ? 0.1 : -0.1))), 1); Put("widzet-alfa", wid.Opacity); };
            wid.ContextMenu = Menu();
            wid.Show();
        }
        // 🧩 Velivo Audio Balance: stan z pliku (zapisuje go co sekundę); nieświeży = Audio Balance nie działa
        readonly double[] hAb = new double[N], hGr = new double[N]; string abOut, abSub; string[] abP; bool abOk;
        void ReadAb()
        {
            abOk = false;
            try
            {
                var f = System.IO.Path.Combine(Dir, "ab-stan.txt");
                if (File.Exists(f) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalSeconds < 4) { var p = abP = File.ReadAllText(f).Split('\t'); abOut = p[0]; abSub = p.Length > 1 ? p[1] : ""; abOk = true; }
            }
            catch (Exception) { }
            { var p = abOk ? abP : new string[0]; int at = (hPos + N - 1) % N;   // wykres jak „praca” w oknie Audio Balance: podgłaśnia/ścisza (turkus), kompresor (żółty)
              double D(int k) => p.Length > k && double.TryParse(p[k], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
              hGr[at] = D(2); hAb[at] = D(3); }
            bool pokazuje = abOk && Get("ab-osobno", "0") != "1" && (wid != null && Get("wykres", "oba") != "net" || bar != null && Pok("ab"));   // „osobno” - Audio Balance pokazuje swój widżet   // widżet zawsze pokazuje wykres Audio Balance
            try { if (pokazuje) File.WriteAllText(System.IO.Path.Combine(Dir, "ab-w-monitorze.txt"), ""); } catch (Exception) { }   // Audio Balance chowa wtedy swój widżet
        }
        void WidgetText()
        {
            DrawGraph(); Val(wUp, "↑", Up); Val(wDown, "↓", Down);
            bool off = Internet.Wylaczony(); if (off) { wUp.Foreground = wDown.Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x6C, 0xFF)); }
            else { wUp.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xC5, 0x18)); wDown.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)); }
            wCpu.Text = (Pok("ghz") ? GhzText : "") + (Pok("ghz") && Pok("cpu") ? " " : "") + (Pok("cpu") ? Cpu.ToString("0") + "%" : "");   // wybór „Pokazuj” - te same liczby co na pasku; wykresy zawsze
            wUp.Visibility = Pok("up") ? Visibility.Visible : Visibility.Hidden; wDown.Visibility = Pok("down") ? Visibility.Visible : Visibility.Hidden;
            bool abW = abOk && Get("ab-osobno", "0") != "1" && Get("wykres", "oba") != "net", netW = !abW || Get("wykres", "oba") != "ab";   // który wykres: internet i procesor / Audio Balance / oba
            wNetRow.Visibility = netW ? Visibility.Visible : Visibility.Collapsed; wAbRow.Visibility = abW ? Visibility.Visible : Visibility.Collapsed; wSep.Visibility = netW && abW ? Visibility.Visible : Visibility.Collapsed;
            if (abOk) { wAb.Text = abOut == "-" ? "– dB" : abOut + " dB"; wAbSub.Text = abSub; wAb.Visibility = wAbSub.Visibility = Pok("ab") ? Visibility.Visible : Visibility.Hidden; }   // liczby wg wyboru, wykres pracy zawsze
            wid.ToolTip = (off ? "Internet WYŁĄCZONY – działa tylko sieć domowa (fioletowe strzałki).\n" : "") + "Wysyłanie ↑ / pobieranie ↓ – teraz; największe z ostatniej minuty: ↑ " + Short(maxUp) + "B/s, ↓ " + Short(maxDown) + "B/s.\n"
                + (abOk ? "Dolny wiersz: Velivo Audio Balance – wynik (LUFS) i zmiana; wykres jak w oknie Audio Balance: turkus – podgłaśnia ↑ / ścisza ↓, żółty – kompresor ścisza głośne fragmenty (±24 dB od linii 0).\n" : "") + "Przeciągnij – przesuń · kółko myszy – przezroczystość · prawy przycisk – menu";
        }
        // bieżąca wartość + szare maksimum z ostatniej minuty („↑ 0.84 KB / 10.8M”) - widżet bez zmiany rozmiaru
        static string Short(double b) => b < 1000 * 1024 ? (b / 1024).ToString(b < 100 * 1024 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + "K" : (b / 1048576).ToString(b < 100 * 1048576 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + "M";
        static void Val(TextBlock t, string arrow, double v) => t.Text = arrow + Short(v);
        void DrawGraph()
        {   // każdy pasek ze swoją skalą - wysyłanie 1 MB/s widać obok pobierania 70 MB/s
            double md = Math.Max(1024, hDown.Max()), mu = Math.Max(1024, hUp.Max()), gmx = Math.Max(0.1, hGhz.Max() * 1.15);
            PointCollection Area(double[] h, double top, int dir)   // dir -1 = w górę od środka (pobieranie), +1 = w dół (wysyłanie)
            {
                var pts = new PointCollection(N + 2) { new Point(0, BH) };
                for (int i = 0; i < N; i++) { double v = h[(hPos + i) % N]; pts.Add(new Point(i * GW / (N - 1), BH + dir * (BH - 1) * Math.Min(1, v / top))); }
                pts.Add(new Point(GW, BH)); return pts;
            }
            pDown.Points = Area(hDown, md, -1); pUp.Points = Area(hUp, mu, 1);
            var g = new PointCollection(N);
            for (int i = 0; i < N; i++) { double v = hGhz[(hPos + i) % N]; g.Add(new Point(i * GW / (N - 1), GH - 1 - (GH - 2) * Math.Min(1, v / gmx))); }
            pGhz.Points = g;
            PointCollection W(double[] h)   // ±24 dB od linii 0 jak w oknie Audio Balance
            {
                var q = new PointCollection(N + 2) { new Point(0, GH / 2) };
                for (int i = 0; i < N; i++) q.Add(new Point(i * GW / (N - 1), GH / 2 - Math.Max(-24, Math.Min(24, h[(hPos + i) % N])) / 24 * (GH / 2 - 1)));
                q.Add(new Point(GW, GH / 2)); return q;
            }
            pAb.Points = W(hAb); pGr.Points = W(hGr);
            maxDown = hDown.Max(); maxUp = hUp.Max();
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
            var ab = It("🎚 Otwórz Audio Balance", OtworzAb); cm.Opened += (s, e) => ab.Visibility = AbDziala() ? Visibility.Visible : Visibility.Collapsed;   // tylko gdy Audio Balance działa
            It("📊 Menedżer zadań Windows", () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch (Exception) { } });
            cm.Items.Add(new Separator());
            It("Widżet na pulpicie", ToggleWidget, () => wid != null);
            It("Widżet zawsze na wierzchu", ToggleTop, () => Get("widzet-wierzch", "1") == "1");
            It("Na pasku zadań", ToggleBar, () => bar != null);
            var pok = new MenuItem { Header = "Pokazuj liczby (pasek i widżet)", ToolTip = "Na pasku zadań i w widżecie – wykresy w widżecie widać zawsze" }; cm.Items.Add(pok);
            foreach (var (k, h) in new[] { ("up", "↑ Wysyłanie"), ("down", "↓ Pobieranie"), ("ghz", "Taktowanie procesora (GHz)"), ("cpu", "Użycie procesora (%)"), ("ab", "Audio Balance (głośność, gdy działa)") })
            { var mi = new MenuItem { Header = h, IsCheckable = true }; mi.Click += (s, e) => BarPokaz(k); cm.Opened += (s, e) => mi.IsChecked = Pok(k); pok.Items.Add(mi); }
            var wyk = new MenuItem { Header = "Wykres w widżecie" }; cm.Items.Add(wyk);
            foreach (var (k, h) in new[] { ("net", "Internet i procesor"), ("ab", "Audio Balance"), ("oba", "Oba") })
            { var mi = new MenuItem { Header = h, IsCheckable = true }; mi.Click += (s, e) => { Put("wykres", k); if (wid != null) WidgetText(); }; cm.Opened += (s, e) => mi.IsChecked = Get("wykres", "oba") == k; wyk.Items.Add(mi); }
            It("Osobny widżet Audio Balance", () => { Put("ab-osobno", Get("ab-osobno", "0") == "1" ? "0" : "1"); if (wid != null) WidgetText(); }, () => Get("ab-osobno", "0") == "1").ToolTip = "Audio Balance pokazuje swój własny widżet (włączony w Audio Balance → 📌 Widżet), a Monitor tylko internet i procesor";
            It("🌐 Internet wyłączony (sieć domowa działa)", ToggleNet, Internet.Wylaczony);
            It("Uruchamiaj z Windows", ToggleAuto, AutoOn);
            cm.Items.Add(new Separator());
            It("Zakończ Velivo Monitor", Quit);
            return cm;
        }
        // 🎚 okno Velivo Audio Balance - sygnał „pokaż” do działającego programu
        static bool AbDziala() { if (!System.Threading.EventWaitHandle.TryOpenExisting("VelivoAudioBalance.Pokaz", out var ev)) return false; ev.Dispose(); return true; }
        static void OtworzAb() { if (System.Threading.EventWaitHandle.TryOpenExisting("VelivoAudioBalance.Pokaz", out var ev)) using (ev) ev.Set(); }
        void ToggleWidget() { if (wid != null) { wid.Close(); wid = null; Put("widzet", "0"); } else { BuildWidget(); Put("widzet", "1"); } }
        void ToggleTop() { bool on = Get("widzet-wierzch", "1") != "1"; Put("widzet-wierzch", on ? "1" : "0"); if (wid != null) wid.Topmost = on; }
        void ToggleBar() { if (bar != null) { bar.Close(); bar = null; ledTm?.Stop(); Put("pasek", "0"); } else { BuildBar(); Put("pasek", "1"); } }
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
                MessageBox.Show((off ? "Nie udało się wyłączyć internetu." : "Nie udało się włączyć internetu.") + "\nWynik zmiany zapory: " + (res ?? "brak (program z prawami administratora się nie uruchomił albo zablokował go antywirus)"), "Velivo Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                if (bar != null) { BarText(); PlaceBar(); }   // co sekundę z powrotem nad paskiem (kliknięcie w pasek go przykrywa)
                ReadAb();
                if (wid != null) { WidgetText(); if (wid.Topmost) { var wh = new WindowInteropHelper(wid).Handle; if (wh != IntPtr.Zero) SetWindowPos(wh, Top, 0, 0, 0, 0, 0x0013); } }   // „zawsze na wierzchu” co sekundę od nowa - także nad pełnym ekranem (filmy, przeglądarka F11); SWP_NOSIZE|NOMOVE|NOACTIVATE
            };
            t.Start();
        }
    }

    // 🌐 internet wyłączony, sieć domowa działa - BEZ zapory (Bitdefender, COMODO itp. zastępują zaporę Windows i jej reguły nic nie dają):
    // na każdej karcie z bramą dodajemy trasy 0.0.0.0/1, 128.0.0.0/1 (IPv4) i 2000::/3 (IPv6 internet) „bez bramy” - są dokładniejsze niż trasa domyślna,
    // więc ruch do internetu nie trafia do routera i ginie; sieć domowa (np. 192.168.8.0/24) ma jeszcze dokładniejszą trasę i działa dalej.
    // Trasy tylko do ponownego uruchomienia komputera (store=active) - restart zawsze przywraca internet.
    static class Internet
    {
        internal static readonly string Wynik = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velivo", "Monitor", "internet-wynik.txt");
        static readonly string Znak = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velivo", "Monitor", "internet-wylaczony.txt");
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        static DateTime Boot => DateTime.UtcNow.AddMilliseconds(-(double)GetTickCount64());
        static DateTime _at; static bool _st;
        internal static bool Wylaczony() => Wylaczony(false);
        internal static bool Wylaczony(bool teraz)
        {
            if (!teraz && (DateTime.UtcNow - _at).TotalSeconds < 2) return _st;
            _at = DateTime.UtcNow; _st = false;
            try
            {   // znak zapisany w tym uruchomieniu Windows (po restarcie trasy znikają - internet jest)
                if (File.Exists(Znak) && long.TryParse(File.ReadAllLines(Znak).FirstOrDefault(), out var b)) _st = Math.Abs((Boot - new DateTime(b, DateTimeKind.Utc)).TotalMinutes) < 2;
            }
            catch (Exception) { }
            return _st;
        }
        static string Netsh(string a)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "netsh.exe"), a) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = System.Diagnostics.Process.Start(psi)) { var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(10000); return p.ExitCode == 0 ? null : a + ": " + o.Trim(); }
        }
        internal static void Ustaw(bool off)
        {
            var errs = new List<string>();
            try
            {
                var ifs = new List<int>();
                if (File.Exists(Znak)) foreach (var l in File.ReadAllLines(Znak).Skip(1)) if (int.TryParse(l, out var i)) ifs.Add(i);   // karty, na których były trasy
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var ip = ni.GetIPProperties(); if (ip.GatewayAddresses.Count == 0) continue;
                    try { int idx = ip.GetIPv4Properties().Index; if (!ifs.Contains(idx)) ifs.Add(idx); } catch (Exception) { }
                }
                foreach (var idx in ifs)
                {   // najpierw zawsze precz (bez błędu, gdy ich nie ma)
                    Netsh("interface ipv4 delete route 0.0.0.0/1 interface=" + idx + " store=active");
                    Netsh("interface ipv4 delete route 128.0.0.0/1 interface=" + idx + " store=active");
                    Netsh("interface ipv6 delete route 2000::/3 interface=" + idx + " store=active");
                }
                if (off)
                {
                    foreach (var idx in ifs)
                    {
                        foreach (var a in new[] { "interface ipv4 add route 0.0.0.0/1 interface=" + idx + " nexthop=0.0.0.0 metric=1 store=active", "interface ipv4 add route 128.0.0.0/1 interface=" + idx + " nexthop=0.0.0.0 metric=1 store=active" })
                        { var e = Netsh(a); if (e != null) errs.Add(e); }
                        Netsh("interface ipv6 add route 2000::/3 interface=" + idx + " metric=1 store=active");   // karta bez IPv6 - bez znaczenia
                    }
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Znak));
                    File.WriteAllLines(Znak, new[] { Boot.Ticks.ToString() }.Concat(ifs.Select(i => i.ToString())));
                    if (ifs.Count == 0) errs.Add("nie znaleziono karty sieciowej z bramą (routerem)");
                }
                else try { File.Delete(Znak); } catch (Exception) { }
                // dawna próba przez zaporę Windows - sprzątanie reguł, jeśli zostały
                try { var pol = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")); var r = pol.GetType().InvokeMember("Rules", System.Reflection.BindingFlags.GetProperty, null, pol, null); for (int i = 0; i < 4; i++) try { r.GetType().InvokeMember("Remove", System.Reflection.BindingFlags.InvokeMethod, null, r, new object[] { "Velivo Monitor – bez internetu" }); } catch (Exception) { } } catch (Exception) { }
            }
            catch (Exception ex) { errs.Add((ex.InnerException ?? ex).Message); }
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Wynik)); File.WriteAllText(Wynik, errs.Count == 0 ? "OK" : "BŁĄD: " + string.Join("\n", errs)); } catch (Exception) { }
        }
    }
}
