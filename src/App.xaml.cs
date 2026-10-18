using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Velivo
{
    public partial class App : Application
    {
        static bool _errorShowing;
        static string _lastError;
        static DateTime _lastErrorShown;

        static string DefaultRoot
        {
            get { return VelivoData.Root; }
        }

        static string ActiveProfileFile { get { return Path.Combine(DefaultRoot, "active-profile.txt"); } }

        static string NormalizeProfileName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "domyslny";
            var src = raw.Trim().ToLowerInvariant();
            var chars = src.Where(ch => (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_').ToArray();
            return chars.Length == 0 ? "domyslny" : new string(chars);
        }

        internal static Point CascadeSpot(Rect me, System.Collections.Generic.IEnumerable<Rect> others, Rect work)
        {
            const double step = 30;
            var list = others.Where(o => Math.Abs(o.Width - me.Width) < 3 && Math.Abs(o.Height - me.Height) < 3).ToList();
            double x = me.X, y = me.Y;
            for (int i = 0; i < 50 && list.Any(o => Math.Abs(o.X - x) < 10 && Math.Abs(o.Y - y) < 10); i++)
            {
                x += step; y += step;
                if (y + me.Height > work.Bottom || x + me.Width > work.Right) { x = work.Left + (x - me.X) % (step * 5); y = work.Top; }   // dol ekranu - od gory
            }
            return new Point(x, y);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Exit += (s, a) => Velivo.MainWindow.EnsureProcessEnds();   // nic nie zostaje w tle po zamknieciu
            VelivoData.Migrate();   // %LOCALAPPDATA%\Przegladarka -> %LOCALAPPDATA%\Programs\Velivo\Dane, zanim cokolwiek siegnie po dane
            // okna o tym samym rozmiarze otwierane w tym samym miejscu - kaskadowo jak w Windows (najnowsze na wierzchu, przesuniete w dol)
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, a) =>
            {
                var w = s as Window;
                if (w != null) { Velivo.MainWindow.RememberWindow(w); Velivo.MainWindow.RestoreAlpha(w); Velivo.MainWindow.ApplyWindowTheme(w); }   // ostatnie miejsce, rozmiar i przezroczystosc okna
                if (w == null || w.WindowStyle == WindowStyle.None || w.WindowState != WindowState.Normal || w == MainWindow) return;
                var others = Windows.Cast<Window>().Where(o => o != w && o.IsVisible && o.WindowState == WindowState.Normal).Select(o => new Rect(o.Left, o.Top, o.ActualWidth, o.ActualHeight));
                var p = CascadeSpot(new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight), others, SystemParameters.WorkArea);
                w.Left = p.X; w.Top = p.Y;
            }));
            // Alt + kolko myszy = przezroczystosc okna (kazdego okna Velivo)
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent, new System.Windows.Input.MouseWheelEventHandler(Velivo.MainWindow.AlphaWheel));
            // klik rolka w pole do pisania = klawiatura ekranowa (kazde okno Velivo; tylko gdy wlaczona w ustawieniach)
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new System.Windows.Input.MouseButtonEventHandler(Velivo.MainWindow.KeyboardOnMiddleClick));
            // Siatka bezpieczenstwa: nieprzewidziany blad nie wylacza przegladarki - zapis do pliku i komunikat.
            // Komunikat najwyzej jeden naraz i nie czesciej niz raz na 30 s dla tego samego bledu
            // (powtarzajacy sie blad nie moze zasypac ekranu okienkami) - kazdy blad i tak trafia do bledy.log.
            DispatcherUnhandledException += (s, a) =>
            {
                LogError(a.Exception);
                a.Handled = true;
                string key = a.Exception.GetType().Name + a.Exception.Message;
                if (_errorShowing || (key == _lastError && DateTime.Now - _lastErrorShown < TimeSpan.FromSeconds(30))) return;
                _errorShowing = true; _lastError = key; _lastErrorShown = DateTime.Now;
                try
                {
                    MessageBox.Show(L.T("Wystąpił nieoczekiwany błąd, ale Velivo działa dalej.\n\n") + a.Exception.Message +
                                    L.T("\n\nSzczegóły zapisano w pliku bledy.log w folderze danych."), "Velivo");
                }
                finally { _errorShowing = false; _lastErrorShown = DateTime.Now; }
            };
            TaskScheduler.UnobservedTaskException += (s, a) => { LogError(a.Exception); a.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += (s, a) => LogError(a.ExceptionObject as Exception);

            var args = e.Args.ToList();
            // start z Windows (autostart): poczekaj ustawiony czas, zanim cokolwiek sie wczyta
            int autoDelay = Velivo.MainWindow.TakeAutostartArg(args);
            bool kbdOnly = args.Remove("--klawiatura");   // skrot "Velivo Klawiatura" (do przypiecia na pasku zadan): sama klawiatura ekranowa
            if (autoDelay > 0) System.Threading.Thread.Sleep(autoDelay * 1000);
            int p = args.IndexOf("--profil");
            string requestedProfile = null;
            if (p >= 0)
            {
                if (p + 1 < args.Count) requestedProfile = NormalizeProfileName(args[p + 1]);
                args.RemoveRange(p, Math.Min(2, args.Count - p));
            }
            if (requestedProfile == null)
            {
                try { if (File.Exists(ActiveProfileFile)) requestedProfile = NormalizeProfileName(File.ReadAllText(ActiveProfileFile)); }
                catch (Exception) { }
            }
            if (requestedProfile == null) requestedProfile = "domyslny";

            if (Environment.GetEnvironmentVariable("VELIVO_DANE") == null)
            {
                string dir = requestedProfile == "domyslny" ? DefaultRoot : Path.Combine(DefaultRoot, "Profiles", requestedProfile);
                Environment.SetEnvironmentVariable("VELIVO_DANE", dir);
            }
            Environment.SetEnvironmentVariable("VELIVO_PROFILE", requestedProfile);
            try
            {
                Directory.CreateDirectory(DefaultRoot);
                File.WriteAllText(ActiveProfileFile, requestedProfile);
            }
            catch (Exception) { }

            // ponowne uruchomienie przez samo Velivo: poczekaj, az poprzednie okno sie zamknie
            int w = args.IndexOf("--czekaj-na");
            if (w >= 0)
            {
                int pid;
                if (w + 1 < args.Count && int.TryParse(args[w + 1], out pid))
                    try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15000); } catch (Exception) { }
                args.RemoveRange(w, Math.Min(2, args.Count - w));
            }
            Velivo.MainWindow.ApplyPendingRestore();   // przywracanie kopii danych - zanim cokolwiek otworzy pliki
            var urls = args.Where(a => a.Length > 0).Select(Velivo.MainWindow.ArgToUrl).ToArray();
            // Velivo juz dziala: oddaj mu adresy (otworzy je w nowych kartach) i zakoncz.
            // Gdyby przekazanie sie nie udalo, otwieramy normalnie drugie okno.
            if (!Velivo.MainWindow.TryBecomePrimary() && Velivo.MainWindow.SendToPrimary(kbdOnly ? new[] { "--klawiatura" } : urls))
            {
                Shutdown();
                return;
            }
            WatchUncleanExit();
            var main = new Velivo.MainWindow(urls);
            if (Velivo.MainWindow.OnlyVlcVideos(urls)) main.WindowState = WindowState.Minimized;   // dwuklik w film: od razu odtwarzacz, przegladarka czeka na pasku zadan
            else if ((autoDelay >= 0 || kbdOnly) && urls.Length == 0) { main.SilentStart = true; main.WindowState = WindowState.Minimized; main.ShowInTaskbar = false; main.ShowActivated = false; }   // autostart z Windows: po cichu do zasobnika
            main.Show();
            if (kbdOnly) main.Dispatcher.BeginInvoke(new Action(Velivo.MainWindow.ShowScreenKeyboard), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // nagle zamkniecie (blad w VLC / WebView2 / systemie - bez wpisu w bledy.log): znacznik dziala.txt zostaje,
        // przy nastepnym starcie dopisujemy do bledy.log raport awarii Windows (ktory modul sie wysypal)
        void WatchUncleanExit()
        {
            try
            {
                var marker = Path.Combine(Velivo.MainWindow.DataFolder, "dziala.txt");
                if (File.Exists(marker))
                {
                    var since = File.GetLastWriteTime(marker).AddMinutes(-1);
                    var log = new System.Text.StringBuilder("Velivo zamknęło się nieoczekiwanie (poprzednie uruchomienie: " + File.ReadAllText(marker).Trim() + ")\n");
                    foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
                        foreach (var sub in new[] { "ReportArchive", "ReportQueue" })
                        {
                            var dir = Path.Combine(root, "Microsoft", "Windows", "WER", sub);
                            if (!Directory.Exists(dir)) continue;
                            foreach (var d in Directory.GetDirectories(dir, "*Velivo*").Where(d => Directory.GetLastWriteTime(d) >= since))
                            {
                                var wer = Path.Combine(d, "Report.wer");
                                if (File.Exists(wer)) log.Append(Path.GetFileName(d) + "\n" + string.Join("\n", File.ReadAllLines(wer).Where(l => l.StartsWith("Sig[") || l.StartsWith("EventType") || l.StartsWith("AppPath"))) + "\n");
                            }
                        }
                    LogError(new Exception(log.ToString()));
                }
                File.WriteAllText(marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Exit += (s, a) => { try { File.Delete(marker); } catch (Exception) { } };
                // zamkniecie przez instalator nowej wersji (Menedzer ponownego uruchamiania) albo wylaczenie Windows - to nie awaria
                SessionEnding += (s, a) => { try { File.Delete(marker); } catch (Exception) { } };
            }
            catch (Exception ex) { LogError(ex); }
        }

        internal static void LogError(Exception ex)
        {
            if (ex == null) return;
            try
            {
                File.AppendAllText(Path.Combine(Velivo.MainWindow.DataFolder, "bledy.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n" + ex + "\n\n");
            }
            catch (Exception) { }
        }
    }
}
