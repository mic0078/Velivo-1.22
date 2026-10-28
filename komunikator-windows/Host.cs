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
    // dziennik, autostart z Windows. Dane te same co w Velivo (folder Velivo\Dane) - gdy oba są zainstalowane, rozmowy prowadzi Komunikator.
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
            try { File.AppendAllText(Path.Combine(Velivo.MainWindow.DataFolder, "bledy.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [Komunikator]\n" + text + "\n\n"); }
            catch (Exception) { }
        }
    }

    // niewidoczne okno-gospodarz: komunikator (wspólny kod) jest częścią tej klasy, tak jak w Velivo częścią okna przeglądarki
    public partial class MainWindow : Window
    {
        const bool KomSamodzielny = true;
        static bool KomOddany => false;
        static bool KomOldPortFree => KomVelivoExe == null;   // 41919 to synchronizacja Velivo - gdy Velivo jest na tym komputerze, Komunikator go nie zajmuje (inaczej synchronizacja między komputerami nie startowała)   // to jest Komunikator - prowadzi rozmowy sam
        static void KomAppRun(bool silent) { }
        // 🌍 synchronizacja Velivo przez internet: stan od moich komputerów - dla Velivo na tym komputerze (folder danych); stan od Velivo - wysłany przez serwer
        void KomSyncIn(byte[] state)
        {
            if (KomVelivoExe == null) return;   // bez Velivo nie ma czego synchronizować
            try { Directory.CreateDirectory(KomSyncInDir); var f = Path.Combine(KomSyncInDir, DateTime.UtcNow.Ticks + ".tmp"); File.WriteAllBytes(f, state); File.Move(f, Path.ChangeExtension(f, ".json")); } catch (Exception ex) { App.LogError(ex); }
        }
        FileSystemWatcher _komSyncOutWatch;
        void KomSyncOutWatch()
        {
            void Send() { try { if (!File.Exists(KomSyncOutFile)) return; var j = File.ReadAllText(KomSyncOutFile); File.Delete(KomSyncOutFile); if (KomSyncOut(j) is string r) App.LogInfo(r); } catch (Exception) { } }
            _komSyncOutWatch = new FileSystemWatcher(DataDir, Path.GetFileName(KomSyncOutFile)) { EnableRaisingEvents = true };
            _komSyncOutWatch.Created += (s, e) => Dispatcher.BeginInvoke(new Action(async () => { await System.Threading.Tasks.Task.Delay(500); Send(); }));
            _komSyncOutWatch.Changed += (s, e) => Dispatcher.BeginInvoke(new Action(async () => { await System.Threading.Tasks.Task.Delay(500); Send(); }));
            Send();
        }
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
                var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Velivo", "Velivo.exe");
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
            Directory.CreateDirectory(DataDir);
            new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();   // uchwyt okna bez pokazywania (klucz sprzętowy, okienka Windows Hello)
            Autostart();
            KomLoad();
            KomStartOwn();   // sieć domowa (41918 i 41919), serwer, łączenie przez internet
            KomEnsureTray();
            try { KomSyncOutWatch(); } catch (Exception ex) { App.LogError(ex); }   // zmiany z Velivo na tym komputerze - przez internet do moich komputerów
            _ = InitWebView();
            if (!silent) ShowMessenger();
        }

        async System.Threading.Tasks.Task InitWebView()
        {
            try { _env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "Komunikator-WebView")); }
            catch (Exception ex) { App.LogError(ex); }   // bez WebView2 - wiadomości i pliki działają, rozmowy wideo nie
        }

        // ▶ start z Windows (serwer ma działać zawsze): wpis użytkownika, bez uprawnień administratora, po cichu do zasobnika
        static void Autostart()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                var cmd = "\"" + Environment.ProcessPath + "\" --autostart";
                if ((k.GetValue("Velivo Komunikator") as string) != cmd) k.SetValue("Velivo Komunikator", cmd);
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

        // link z rozmowy: w Velivo, gdy jest, inaczej w domyślnej przeglądarce
        void AddTab(string url)
        {
            try { if (KomVelivoExe is string v) Process.Start(v, url); else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { App.LogError(ex); }
        }
        // 🎬 film z rozmowy: odtwarzacz Velivo (wszystkie kodeki), gdy jest; inaczej program przypisany w Windows
        void PlayVideos(string[] files, int index, bool library = true)
        {
            var f = files[Math.Max(0, Math.Min(index, files.Length - 1))];
            try { if (KomVelivoExe is string v) Process.Start(v, "\"" + f + "\""); else Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); }
            catch (Exception ex) { App.LogError(ex); }
        }

        // 🎬 Filmy i TV do kontaktu - ustawienia odtwarzacza są w Velivo; w samym Komunikatorze ich nie ma
        sealed class JellyfinConf { public string Url { get; set; } public string Key { get; set; } }
        JellyfinConf LoadJellyfinConf() => null;
        List<Dictionary<string, string>> MediaTvLists() => new List<Dictionary<string, string>>();

        void ShowFromTray() { try { if (KomVelivoExe is string v) Process.Start(v); } catch (Exception ex) { App.LogError(ex); } }
        void ExitFromTray()
        {
            _komExit = true;
            try { KomBye(); KomRelayStop(); } catch (Exception) { }
            KomTrayRemove();
            try { _komWindow?.Close(); } catch (Exception) { }
            Application.Current.Shutdown();
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    }
}
