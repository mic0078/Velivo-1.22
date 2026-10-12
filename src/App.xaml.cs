using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Przegladarka
{
    public partial class App : Application
    {
        static bool _errorShowing;
        static string _lastError;
        static DateTime _lastErrorShown;

        static string DefaultRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Przegladarka"); }
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
            // okna o tym samym rozmiarze otwierane w tym samym miejscu - kaskadowo jak w Windows (najnowsze na wierzchu, przesuniete w dol)
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, a) =>
            {
                var w = s as Window;
                if (w != null) Przegladarka.MainWindow.RememberWindow(w);   // ostatnie miejsce i rozmiar okna
                if (w == null || w.WindowStyle == WindowStyle.None || w.WindowState != WindowState.Normal || w == MainWindow) return;
                var others = Windows.Cast<Window>().Where(o => o != w && o.IsVisible && o.WindowState == WindowState.Normal).Select(o => new Rect(o.Left, o.Top, o.ActualWidth, o.ActualHeight));
                var p = CascadeSpot(new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight), others, SystemParameters.WorkArea);
                w.Left = p.X; w.Top = p.Y;
            }));
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
            int autoDelay = Przegladarka.MainWindow.TakeAutostartArg(args);
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

            if (Environment.GetEnvironmentVariable("PRZEGLADARKA_DANE") == null)
            {
                string dir = requestedProfile == "domyslny" ? DefaultRoot : Path.Combine(DefaultRoot, "Profiles", requestedProfile);
                Environment.SetEnvironmentVariable("PRZEGLADARKA_DANE", dir);
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
            var urls = args.Where(a => a.Length > 0).Select(Przegladarka.MainWindow.ArgToUrl).ToArray();
            // Velivo juz dziala: oddaj mu adresy (otworzy je w nowych kartach) i zakoncz.
            // Gdyby przekazanie sie nie udalo, otwieramy normalnie drugie okno.
            if (!Przegladarka.MainWindow.TryBecomePrimary() && Przegladarka.MainWindow.SendToPrimary(urls))
            {
                Shutdown();
                return;
            }
            new Przegladarka.MainWindow(urls).Show();
        }

        internal static void LogError(Exception ex)
        {
            if (ex == null) return;
            try
            {
                File.AppendAllText(Path.Combine(Przegladarka.MainWindow.DataFolder, "bledy.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n" + ex + "\n\n");
            }
            catch (Exception) { }
        }
    }
}
