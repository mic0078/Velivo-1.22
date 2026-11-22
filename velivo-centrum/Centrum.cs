using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VelivoCentrum
{
    // 🧩 Velivo Centrum: jedna ikona V przy zegarze - po kliknięciu lista zainstalowanych programów Velivo (stan „działa / wyłączony”, krótka informacja na żywo).
    // Dopóki Centrum działa, inne programy Velivo chowają swoje ikony przy zegarze (znak: muteks Local\Velivo.Centrum); po jego zamknięciu pokazują je z powrotem.
    static class Program
    {
        [STAThread]
        static void Main()
        {
            using (var one = new System.Threading.Mutex(true, @"Local\Velivo.Centrum", out bool first))
            {
                if (!first) { try { using (var ev = System.Threading.EventWaitHandle.OpenExisting(@"Local\Velivo.Centrum.Pokaz")) ev.Set(); } catch (Exception) { } return; }   // drugie uruchomienie (skrót) - pokaż listę w działającym
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var c = new Centrum();
                app.Startup += (s, e) => c.Start();
                app.Run();
            }
        }
    }

    sealed class Prog
    {
        public string Pid, Name, Desc, Proc, DefDir, Exe, Args, Same;   // Args/Same: moduł pełnego Velivo (ten sam program, inny start); Same = osobny program o tej roli - gdy jest, modułu nie pokazujemy
        public Prog(string pid, string name, string desc, string proc, string dir, string args = null, string same = null) { Pid = pid; Name = name; Desc = desc; Proc = proc; DefDir = dir; Args = args; Same = same; }
        public bool Modul => Args != null;
    }

    sealed partial class Centrum
    {
        static readonly Prog[] All =
        {
            new Prog("VelivoOdtwarzacz", "Velivo Odtwarzacz", "filmy, TV, Kanapa", "VelivoOdtwarzacz", "Velivo Odtwarzacz"),
            new Prog("Velivo", "Velivo", "przeglądarka, filmy, komunikator", "Velivo", "Velivo"),
            // 🧩 moduły pełnego Velivo - każdy widoczny osobno jak dawniej osobne programy (start prosto w moduł)
            new Prog("Velivo", "Velivo Odtwarzacz", "filmy i TV – w Velivo", "Velivo", "Velivo", "--odtwarzacz", "VelivoOdtwarzacz"),
            new Prog("Velivo", "Komunikator Velivo", "wiadomości i rozmowy – w Velivo", "Velivo", "Velivo", "--komunikator", "VelivoKomunikator"),
            new Prog("Velivo", "Velivo Klawiatura", "klawiatura ekranowa – w Velivo", "Velivo", "Velivo", "--klawiatura", "VelivoKlawiatura"),
            new Prog("VelivoPrzegladarka", "Velivo Przeglądarka", "strony internetowe", "VelivoPrzegladarka", "Velivo Przeglądarka"),
            new Prog("VelivoSerwer", "Velivo Serwer filmów", "biblioteka filmów dla Twoich komputerów", "VelivoSerwer", "Velivo Serwer filmów"),
            new Prog("VelivoKomunikator", "Velivo Komunikator", "wiadomości i rozmowy", "VelivoKomunikator", "Velivo Komunikator"),
            new Prog("VelivoAudioBalance", "Audio Balance", "wyrównywanie dźwięku Windows", "VelivoAudioBalance", "Velivo Audio Balance for Windows"),
            new Prog("VelivoMonitor", "Velivo Monitor", "sieć i procesor", "VelivoMonitor", "Velivo Monitor"),
            new Prog("VelivoKlawiatura", "Velivo Klawiatura", "klawiatura ekranowa i poprawianie pisania", "VelivoKlawiatura", "Velivo Klawiatura"),
            new Prog("VelivoKursory", "Velivo Kursory", "kursory myszy w stylu Velivo", "VelivoKursory", "Velivo Kursory"),
        };
        static readonly string ExePath = System.Reflection.Assembly.GetEntryAssembly().Location;

        // gdzie jest program: spis programów Velivo (instalator), zapasowo folder domyślny
        static string FindExe(Prog p)
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Velivo\Programy\" + p.Pid)) if (k?.GetValue("Exe") is string e && File.Exists(e)) return e; } catch (Exception) { }
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", p.DefDir, p.Proc + ".exe");
            return File.Exists(d) ? d : null;
        }
        // 🖱 Velivo Kursory nie działa w tle - „działa” = kursory Velivo włączone w Windows, wielkość z jego ustawień
        static bool KursoryOn() { try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors")) return (k?.GetValue("Arrow") as string ?? "").Contains(@"\Velivo\Kursory\"); } catch (Exception) { return false; } }
        static string KursoryPx() { try { return File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velivo", "Kursory", "rozmiar.txt")).Trim(); } catch (Exception) { return "32"; } }
        static void KursoryRun(Prog p, string args) { try { Process.Start(new ProcessStartInfo(p.Exe, args) { UseShellExecute = false, CreateNoWindow = true }); } catch (Exception) { } }
        static bool Running(Prog p) { if (p.Pid == "VelivoKursory") return KursoryOn(); var ps = Process.GetProcessesByName(p.Proc); foreach (var x in ps) x.Dispose(); return ps.Length > 0; }

        // na żywo: poziom z Audio Balance (pamięć współdzielona), procesor dla Monitora
        static string Live(Prog p, bool on)
        {
            if (!on) return File.Exists(p.Exe ?? "") ? "kliknij, aby uruchomić" : p.Desc;
            if (p.Pid == "VelivoAudioBalance")
                try { using (var m = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(@"Local\Velivo.AudioBalance.Wynik")) using (var v = m.CreateViewAccessor()) { if ((long)GetTickCount64() - v.ReadInt64(8) < 3000) { float l = v.ReadSingle(0); if (l > -70 && l < 0) return l.ToString("0.0") + " LUFS · przetwarza"; } } } catch (Exception) { }
            if (p.Pid == "VelivoMonitor" && cpu >= 0) return "procesor " + cpu.ToString("0") + " %";
            if (p.Pid == "VelivoKursory") return "włączone · " + KursoryPx() + " px";
            return p.Desc;
        }
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        static double cpu = -1; static long li, lk, lu;
        static void CpuTick() { if (GetSystemTimes(out var i, out var k, out var u)) { long dt = (k - lk) + (u - lu); if (lk != 0 && dt > 0) cpu = 100.0 * (dt - (i - li)) / dt; li = i; lk = k; lu = u; } }

        // ---------- okienko listy (nad zegarem) ----------
        Window win; StackPanel list; TextBlock count;
        static readonly Brush Bg = new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x18, 0x21)), Line = new SolidColorBrush(Color.FromRgb(0x2A, 0x32, 0x40)), Fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEB, 0xF0)),
            Dim = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)), Hover = new SolidColorBrush(Color.FromRgb(0x23, 0x2A, 0x37)), OnBg = new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)), OnFg = new SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC));

        // logo Velivo jak na telefonie: zaokrąglony kwadrat fiolet → turkus, jasne V i trzy linie ruchu
        static UIElement Logo()
        {
            var cv = new Canvas { Width = 24, Height = 24 };
            var bg = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Background = new LinearGradientBrush(Color.FromRgb(0x6A, 0x3B, 0xE0), Color.FromRgb(0x2B, 0xA6, 0xD8), new Point(0, 0), new Point(1, 1)) };
            cv.Children.Add(bg);
            var ink = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEA));
            var v = new System.Windows.Shapes.Polyline { Points = new PointCollection { new Point(11, 6.5), new Point(15, 17.5), new Point(19, 6.5) }, Stroke = ink, StrokeThickness = 3.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            cv.Children.Add(v);
            void L(double x1, double x2, double y) => cv.Children.Add(new System.Windows.Shapes.Line { X1 = x1, X2 = x2, Y1 = y, Y2 = y, Stroke = ink, StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0.85 });
            L(4.5, 9, 9.5); L(5.5, 9.8, 12); L(6.5, 10.6, 14.5);
            return new Border { Child = cv, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        }
        void BuildWin()
        {
            var head = new DockPanel { Margin = new Thickness(4, 2, 4, 8) };
            var logo = Logo();
            count = new TextBlock { Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(logo, Dock.Left); DockPanel.SetDock(count, Dock.Right);
            head.Children.Add(logo); head.Children.Add(count); var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center }; name.Inlines.Add(new System.Windows.Documents.Run("Velivo") { Foreground = Fg, FontSize = 14, FontWeight = FontWeights.SemiBold }); name.Inlines.Add(new System.Windows.Documents.Run("  by MichAel") { Foreground = Dim, FontSize = 11 }); head.Children.Add(name);
            list = new StackPanel();
            Button B(string t, Action a) { var b = new Button { Content = t, Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(6), Foreground = Fg, Background = Hover, BorderThickness = new Thickness(0), Cursor = Cursors.Hand }; b.Click += (s, e) => a(); return b; }
            var foot = new UniformGridLite(B("Uruchom wszystkie", StartAll), B("Zamknij…", () => { win.Hide(); ZamknijWybor(); }));
            var konB = B("🧹 Porządki i kopia…", () => { win.Hide(); Konserwacja(); }); konB.Margin = new Thickness(3, 6, 3, 0);
            var root = new StackPanel(); root.Children.Add(head); root.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 0, 0, 6) }); root.Children.Add(list);
            root.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 6, 0, 8) }); root.Children.Add(foot); root.Children.Add(konB);
            win = new Window { Title = "Velivo Centrum", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true, Width = 320, SizeToContent = SizeToContent.Height,
                Content = new Border { Background = Bg, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(10), Child = root } };
            win.Deactivated += (s, e) => win.Hide();   // klik obok - lista się chowa
            win.KeyDown += (s, e) => { if (e.Key == Key.Escape) win.Hide(); };
        }

        static readonly Dictionary<string, ImageSource> icons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static ImageSource IconOf(string exe)
        {
            if (icons.TryGetValue(exe, out var c)) return c;
            return icons[exe] = IconLoad(exe);
        }
        // ikona modułu Velivo: jego plik .ico obok Velivo (te same co skróty na pulpicie)
        static string ModIco(Prog p)
        {
            if (!p.Modul) return null;
            var f = Path.Combine(Path.GetDirectoryName(p.Exe) ?? "", p.Args == "--odtwarzacz" ? "odtwarzacz.ico" : p.Args == "--komunikator" ? "komunikator.ico" : "klawiatura.ico");
            return File.Exists(f) ? f : null;
        }
        static ImageSource IconLoad(string exe)
        {
            try { using (var ic = System.Drawing.Icon.ExtractAssociatedIcon(exe)) return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); } catch (Exception) { return null; }
        }

        void Fill()
        {
            list.Children.Clear(); int on = 0, n = 0;
            foreach (var p in All)
            {
                p.Exe = FindExe(p); if (p.Exe == null) continue;   // tylko zainstalowane
                if (p.Modul && All.Any(o => o.Pid == p.Same && FindExe(o) != null)) continue;   // jest osobny program tej roli - bez podwójnego wpisu
                bool run = Running(p); if (!p.Modul) { n++; if (run) on++; }
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                var bd = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = row, Background = Brushes.Transparent,
                    ToolTip = run ? "Kliknij – otwórz · prawy przycisk – menu" : "Kliknij – uruchom" };
                bd.MouseEnter += (s, e) => bd.Background = Hover; bd.MouseLeave += (s, e) => bd.Background = Brushes.Transparent;
                var img = new Image { Width = 30, Height = 30, Source = IconOf(ModIco(p) ?? p.Exe), Margin = new Thickness(0, 0, 10, 0) };
                var st = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 2, 7, 2), VerticalAlignment = VerticalAlignment.Center, Background = run ? OnBg : Line,
                    Child = new TextBlock { Text = run ? "działa" : "wyłączony", FontSize = 11, Foreground = run ? OnFg : Dim } };
                var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                txt.Children.Add(new TextBlock { Text = p.Name, Foreground = Fg, FontSize = 13 });
                txt.Children.Add(new TextBlock { Text = Live(p, run), Foreground = Dim, FontSize = 11 });
                DockPanel.SetDock(img, Dock.Left); DockPanel.SetDock(st, Dock.Right);
                row.Children.Add(img); row.Children.Add(st); row.Children.Add(txt);
                var pp = p;
                bd.MouseLeftButtonUp += (s, e) => { Open(pp); win.Hide(); };
                var cm = new ContextMenu();
                void Mi(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (s, e) => a(); cm.Items.Add(mi); }
                Mi(run ? "▶ Otwórz " + p.Name : "▶ Uruchom " + p.Name, () => { Open(pp); win.Hide(); });
                if (p.Pid == "VelivoKursory")
                {   // 🖱 wielkość kursorów prosto z Centrum, przywrócenie kursorów Windows
                    var wl = new MenuItem { Header = "Wielkość kursorów" }; cm.Items.Add(wl);
                    foreach (var (px, t) in new[] { ("24", "24 px – mała"), ("28", "28 px – trochę mniejsza"), ("32", "32 px – normalna (jak Windows)"), ("40", "40 px – trochę większa"), ("48", "48 px – duża"), ("64", "64 px – bardzo duża"), ("96", "96 px – największa") })
                    { var mi = new MenuItem { Header = t, IsCheckable = true, IsChecked = run && KursoryPx() == px }; mi.Click += (s, e) => { KursoryRun(pp, "--zastosuj " + px); Fill(); }; wl.Items.Add(mi); }
                    if (!run) Mi("✔ Włącz kursory Velivo", () => { KursoryRun(pp, "--zastosuj"); Fill(); });
                    else Mi("↺ Przywróć kursory Windows", () => { KursoryRun(pp, "--przywroc"); Fill(); });
                }
                else if (run) Mi("✕ Zamknij " + (p.Modul ? "Velivo" : p.Name), () => Close(pp));
                // prawy przycisk: oryginalne menu programu (to samo co przy jego ikonie przy zegarze); starsza wersja programu bez tej możliwości - menu powyżej
                bd.MouseRightButtonUp += (s, e) => { if (run && !pp.Modul && pp.Pid != "VelivoKursory" && ProgMenu(pp)) { e.Handled = true; win.Hide(); } else { cm.PlacementTarget = bd; cm.IsOpen = true; e.Handled = true; } };
                // 📌 pokazywać też przy zegarze (obok Centrum) - zapamiętane dla programu
                bool pin = Pinned(pp);
                var pinB = new TextBlock { Text = pin ? "\uE718" : "\uE77A", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = pin ? new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)) : Dim, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Background = Brushes.Transparent,   // pinezka: żółta = też przy zegarze, szara przekreślona = tylko w Centrum
                    ToolTip = pin ? "Pokazywany też przy zegarze – kliknij, aby tylko w Centrum" : "Tylko w Centrum – kliknij, aby pokazywać też przy zegarze" };
                pinB.MouseLeftButtonUp += (s, e) => { SetPinned(pp, !Pinned(pp)); e.Handled = true; Fill(); };
                if (!p.Modul) { DockPanel.SetDock(pinB, Dock.Right); row.Children.Insert(2, pinB); }   // moduł ma ikonę przy zegarze razem z Velivo
                bd.Tag = cm;
                list.Children.Add(bd);
            }
            if (n == 0) list.Children.Add(new TextBlock { Text = "Nie znaleziono zainstalowanych programów Velivo.", Foreground = Dim, Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap });
            count.Text = on + " z " + n + " działa";
        }

        // 📌 „też przy zegarze” - program czyta to sam (HKCU\Software\Velivo\Centrum\<proces>)
        static bool Pinned(Prog p) { try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Velivo\Centrum")) return (k?.GetValue(p.Proc) as int?) == 1; } catch (Exception) { return false; } }
        static void SetPinned(Prog p, bool on) { try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Velivo\Centrum")) k.SetValue(p.Proc, on ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord); } catch (Exception) { } }
        // menu programu: znak dla programu, który pokazuje swoje menu przy kursorze; false = program go nie zna (starsza wersja)
        static bool ProgMenu(Prog p) { try { using (var ev = System.Threading.EventWaitHandle.OpenExisting(@"Local\Velivo.Menu." + p.Proc)) return ev.Set(); } catch (Exception) { return false; } }

        // uruchomienie działającego programu = jego okno (każdy program Velivo oddaje drugie uruchomienie pierwszemu)
        static void Open(Prog p) { try { Process.Start(new ProcessStartInfo(p.Exe, p.Args ?? "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(p.Exe) }); } catch (Exception) { } }
        static void Close(Prog p)
        {
            if (MessageBox.Show("Zamknąć " + p.Name + "?", "Velivo Centrum", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Shut(p);
        }
        static void Shut(Prog p)
        {
            try
            {
                if (p.Pid == "VelivoAudioBalance") using (var r = Process.Start(new ProcessStartInfo(p.Exe, "--przywroc") { UseShellExecute = false, CreateNoWindow = true })) r?.WaitForExit(5000);   // dźwięk Windows wraca na głośniki
                foreach (var x in Process.GetProcessesByName(p.Proc)) using (x) { try { if (!x.CloseMainWindow() || !x.WaitForExit(5000)) x.Kill(); } catch (Exception) { } }
            }
            catch (Exception) { }
        }
        void StartAll() { foreach (var p in All) if (p.Exe != null && !p.Modul && p.Pid != "VelivoKursory" && !Running(p)) Uruchom(p); win.Hide(); }   // Kursory nie działa w tle - nie otwieramy jego okna
        static void Uruchom(Prog p) { try { Process.Start(new ProcessStartInfo(p.Exe, p.Pid == "Velivo" || p.Pid == "VelivoOdtwarzacz" || p.Pid == "VelivoPrzegladarka" || p.Pid == "VelivoKlawiatura" || p.Pid == "VelivoSerwer" ? "--autostart 0" : "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(p.Exe) }); } catch (Exception) { } }

        void ShowList()
        {
            Fill();
            var wa = SystemParameters.WorkArea; win.Show(); win.UpdateLayout();
            win.Left = wa.Right - win.ActualWidth - 10; win.Top = wa.Bottom - win.ActualHeight - 10;   // nad zegarem
            win.Activate();
        }

        System.Windows.Forms.NotifyIcon tray;
        public void Start()
        {
            BuildWin();
            tray = new System.Windows.Forms.NotifyIcon { Text = "Velivo – wszystkie programy", Visible = true };
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(ExePath); } catch (Exception) { }
            var tm = new System.Windows.Forms.ContextMenuStrip();
            tm.Items.Add("Pokaż programy Velivo", null, (s, e) => ShowList());
            var auto = new System.Windows.Forms.ToolStripMenuItem("Uruchamiaj z Windows") { CheckOnClick = true };
            tm.Opening += (s, e) => auto.Checked = AutoOn(); auto.Click += (s, e) => SetAuto(auto.Checked); tm.Items.Add(auto);
            tm.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            tm.Items.Add("Zakończ Velivo Centrum (ikony programów wrócą)", null, (s, e) => { tray.Visible = false; tray.Dispose(); Application.Current.Shutdown(); });
            tray.ContextMenuStrip = tm;
            tray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) { if (win.IsVisible) win.Hide(); else ShowList(); } };
            var ev = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\Velivo.Centrum.Pokaz");
            new System.Threading.Thread(() => { while (ev.WaitOne()) Application.Current.Dispatcher.BeginInvoke(new Action(ShowList)); }) { IsBackground = true }.Start();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (s, e) => { CpuTick(); if (win.IsVisible && !list.IsMouseOver && !list.Children.OfType<Border>().Any(b => b.Tag is ContextMenu bm && bm.IsOpen)) Fill(); };   // stan na żywo, gdy lista otwarta
            t.Start();
        }
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static bool AutoOn() { try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue("VelivoCentrum") != null; } catch (Exception) { return false; } }
        static void SetAuto(bool on) { try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey)) { if (on) k.SetValue("VelivoCentrum", "\"" + ExePath + "\""); else k.DeleteValue("VelivoCentrum", false); } } catch (Exception) { } }
    }

    // dwa przyciski po równo (bez UniformGrid z zależnościami)
    sealed class UniformGridLite : Grid
    {
        public UniformGridLite(params UIElement[] els)
        {
            for (int i = 0; i < els.Length; i++) { ColumnDefinitions.Add(new ColumnDefinition()); SetColumn(els[i], i); Children.Add(els[i]); }
        }
    }
}
