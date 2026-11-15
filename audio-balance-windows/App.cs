using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace Velivo
{
    // Velivo Audio Balance for Windows (całkowicie samodzielny program, bez przeglądarki Velivo): start, jedna kopia, dziennik błędów, folder danych
    public partial class App : Application
    {
        internal static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Velivo Audio Balance for Windows");

        static void Line(string s)
        {
            VelivoBledy.Write("Velivo Audio Balance for Windows", s);   // ⚠ wspólny katalog „Poważne błędy” wszystkich programów Velivo
        }
        internal static void LogError(Exception ex) => Line("✖ " + ex);
        internal static void LogInfo(string text) => Line("ℹ " + text);

        [STAThread]
        static void Main(string[] args)
        {
            if (Array.Exists(args, a => a == "--przywroc")) { AbMain.RestoreSpeakers(); return; }   // odinstalowanie: Windows wraca do głośników
            using var one = new Mutex(true, "VelivoAudioBalance.Jeden", out bool first);
            if (!first) { AbMain.SignalShow(); return; }   // drugie uruchomienie - pokaż okno działającej kopii
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) => { LogError(e.Exception); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { if (e.ExceptionObject is Exception ex) LogError(ex); };
            var main = new AbMain(Array.Exists(args, a => a == "--zasobnik"));
            app.Run();
            main.Dispose();
        }
    }
}
