using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Velivo
{
    // Velivo Komunikator na Windows - samodzielny program: rozmowy, pliki, rozmowy wideo i serwer (pośrednik) bez przeglądarki Velivo.
    // Kod komunikatora wspólny z Velivo (..\src\Komunikator*.cs); tutaj tylko to, co w Velivo daje przeglądarka: start, zasobnik,
    // dziennik, autostart z Windows. Dane te same co w Velivo (folder Velivo\Dane) - gdy oba są zainstalowane, rozmowy i serwer prowadzi Velivo.
    public class App : Application
    {
        const string MutexName = "VelivoKomunikator-jeden", ShowEvent = "VelivoKomunikator-pokaz";

        [STAThread]
        static void Main(string[] args)
        {
            bool silent = args.Contains("--autostart");
            using var one = new Mutex(true, MutexName, out bool first);
            if (!first)
            {   // już działa - pokazuje swoje okno
                try { using var ev = EventWaitHandle.OpenExisting(ShowEvent); ev.Set(); } catch (Exception) { }
                return;
            }
            VelivoData.Migrate();   // 🧩 dane wspólne poza folderami programów (jak w Velivo) - zanim cokolwiek je otworzy
            VelivoData.ZapiszSie("VelivoKomunikator", "Velivo Komunikator", Environment.ProcessPath, typeof(App).Assembly.GetName().Version?.ToString());   // 🧩 spis programów Velivo
            L.Init("auto");
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) => { LogError(e.Exception); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError(e.ExceptionObject as Exception);
            var host = new Velivo.MainWindow();
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
            new Thread(() => { while (show.WaitOne()) host.Dispatcher.BeginInvoke(new Action(() => host.ShowMessenger())); }) { IsBackground = true }.Start();
            host.Dispatcher.BeginInvoke(new Action(() => host.Start(silent)));
            app.Run();
        }

        internal static void LogError(Exception ex) { if (ex != null) LogLine(ex.ToString()); }
        internal static void LogInfo(string text) => LogLine("ℹ " + text);
        static void LogLine(string text)
        {
            VelivoBledy.Write("Velivo Komunikator", text);   // ⚠ wspólny katalog „Poważne błędy”
        }
    }

    // niewidoczne okno-gospodarz: komunikator (wspólny kod) jest częścią tej klasy, tak jak w Velivo częścią okna przeglądarki
    public partial class MainWindow : Window
    {
        const bool KomSamodzielny = true;
        // Velivo na tym komputerze: rozmowy i serwer prowadzi Velivo (jak przed Komunikatorem) - Komunikator tylko je uruchamia / otwiera okno rozmów i kończy się
        // 🧩 programy równe (właściciel): Komunikator działa sam i NIGDY nie uruchamia Velivo; oddaje rozmowy tylko pełnemu Velivo, które JUŻ działa (nowe - znak KomVelivoMarkFile)
        static bool KomOddany => KomVelivoExe != null && File.Exists(KomVelivoMarkFile) && VelivoDziala();
        static bool VelivoDziala() { var ps = Process.GetProcessesByName("Velivo"); foreach (var p in ps) p.Dispose(); return ps.Length > 0; }
        static void KomAppRun(bool silent)
        {
            try
            {
                if (silent || !VelivoDziala()) return;   // start z Windows / Velivo nie działa - nic nie uruchamiamy
                Process.Start(KomVelivoExe, "--komunikator");   // działające Velivo pokazuje swoje okno rozmów (nowego Velivo nie otwiera)
            }
            catch (Exception ex) { App.LogError(ex); }
        }
        void KomSyncIn(byte[] state) { }
        void KomShowBrowser() { }   // link otwiera Velivo albo przeglądarka systemu - same się pokazują   // synchronizacja dotyczy tylko Velivo
        static readonly string DataDir = Environment.GetEnvironmentVariable("VELIVO_DANE") ?? VelivoData.Root;
        internal static string DataFolder => DataDir;
        const int LanPort = 41919;   // port sieci domowej starszych Velivo - komunikator słucha też na nim
        System.Net.Sockets.UdpClient _lanTx = null, _lanRx = null;   // synchronizacja przeglądarki - tu jej nie ma
        readonly string _lanDeviceName = Environment.MachineName;
        Microsoft.Web.WebView2.Core.CoreWebView2Environment _env;   // rozmowy wideo (strona Rozmowa\rozmowa.html)

        // pełne Velivo na tym komputerze (linki, filmy w odtwarzaczu Velivo), gdy jest
        static string KomVelivoExe
        {
            get
            {
                // miejsce z wpisu instalatora Velivo (Windows → Aplikacje), zapasowo folder domyślny
                string p = VelivoData.ProgramZeSpisu("Velivo");   // spis programów Velivo - najpierw
                if (p == null) try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E4C2B7A-3F1D-4C8E-9A51-PRZEGLADARKA01}_is1")) { var d = k?.GetValue("InstallLocation") as string; if (!string.IsNullOrEmpty(d)) p = Path.Combine(d, "Velivo.exe"); } } catch (Exception) { }
                if (p == null || !File.Exists(p)) p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Velivo", "Velivo.exe");
                return File.Exists(p) ? p : null;
            }
        }

        public MainWindow()
        {
            ShowInTaskbar = false; WindowStyle = WindowStyle.None; Width = Height = 0; Visibility = Visibility.Hidden;
            _winTheme = "auto";   // okienka jak motyw Windows
        }

        internal void Start(bool silent)
        {
            if (KomOddany) { KomAppRun(silent); Application.Current.Shutdown(); return; }
            Directory.CreateDirectory(DataDir);
            new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();   // uchwyt okna bez pokazywania (klucz sprzętowy, okienka Windows Hello)
            Autostart();
            KomLoad();
            KomStartOwn();   // sieć domowa (41918 i 41919), serwer, łączenie przez internet
            KomEnsureTray();
            _ = InitWebView();
            if (!silent) ShowMessenger();
        }

        async System.Threading.Tasks.Task InitWebView()
        {
            try { _env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "Komunikator-WebView")); }
            catch (Exception ex) { App.LogError(ex); }   // bez WebView2 - wiadomości i pliki działają, rozmowy wideo nie
        }

        // ▶ start z Windows domyślnie włączony - raz, przy pierwszym uruchomieniu; wyłączony w menu (zasobnik / Centrum) zostaje wyłączony.
        // Ścieżka programu po aktualizacji (inny folder) - poprawiana, gdy wpis jest.
        static void Autostart()
        {
            try
            {
                using var f = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Velivo\Komunikator");
                if (f.GetValue("AutostartInit") == null) { KomAutoStart = true; f.SetValue("AutostartInit", 1); return; }
                if (KomAutoStart) KomAutoStart = true;   // aktualna ścieżka programu
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // komunikat w zasobniku (w Velivo - okienko przeglądarki)
        Window ShowToast(string text, string file)
        {
            try { if (_komTray != null) _komTray.ShowBalloonTip(5000, "Velivo Komunikator", text.Length > 250 ? text.Substring(0, 250) : text, System.Windows.Forms.ToolTipIcon.None); } catch (Exception) { }
            return null;
        }
        Window PairDialogOwner() => _komWindow;

        // 🧩 inne programy Velivo (spis HKCU\Software\Velivo\Programy): strony - Velivo Przeglądarka albo pełne Velivo; filmy - Velivo Odtwarzacz albo pełne Velivo.
        // Najpierw ten, który już działa (bez otwierania drugiego programu), potem zainstalowany.
        static string KomProgram(params string[] pids)
        {
            string any = null;
            foreach (var pid in pids)
            {
                var exe = pid == "Velivo" ? KomVelivoExe : VelivoData.ProgramZeSpisu(pid);
                if (exe == null || !File.Exists(exe)) continue;
                var ps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)); bool on = ps.Length > 0; foreach (var p in ps) p.Dispose();
                if (on) return exe; any ??= exe;
            }
            return any;
        }
        static string KomPrzegladarka => KomProgram("VelivoPrzegladarka", "Velivo");
        static string KomOdtwarzacz => KomProgram("VelivoOdtwarzacz", "Velivo");
        // link z rozmowy: w Velivo Przeglądarce / Velivo, gdy jest, inaczej w domyślnej przeglądarce
        void AddTab(string url)
        {
            try { if (KomPrzegladarka is string v) Process.Start(v, url); else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { App.LogError(ex); }
        }
        // 🎬 film z rozmowy: odtwarzacz Velivo (wszystkie kodeki), gdy jest; inaczej program przypisany w Windows
        void PlayVideos(string[] files, int index, bool library = true)
        {
            var f = files[Math.Max(0, Math.Min(index, files.Length - 1))];
            try { if (KomOdtwarzacz is string v) Process.Start(v, "\"" + f + "\""); else Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); }
            catch (Exception ex) { App.LogError(ex); }
        }

        // 🎬 Filmy i TV do kontaktu - ustawienia odtwarzacza są w Velivo; w samym Komunikatorze ich nie ma
        sealed class JellyfinConf { public string Url { get; set; } public string Key { get; set; } }
        JellyfinConf LoadJellyfinConf() => null;
        List<Dictionary<string, string>> MediaTvLists() => new List<Dictionary<string, string>>();

        void ShowFromTray() { try { if (KomPrzegladarka is string v) Process.Start(v); } catch (Exception ex) { App.LogError(ex); } }
        void ExitFromTray()
        {
            _komExit = true;
            try { KomBye(); KomRelayStop(true); } catch (Exception) { }
            KomTrayRemove();
            try { _komWindow?.Close(); } catch (Exception) { }
            Application.Current.Shutdown();
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    }
}
