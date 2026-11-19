using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace Velivo
{
    // Velivo Audio Balance for Windows (całkowicie samodzielny program, bez przeglądarki Velivo): start, jedna kopia, dziennik błędów, folder danych
    public partial class App : Application
    {
        // 🧩 wspólny folder wszystkich programów Velivo: %LOCALAPPDATA%\Velivo (obok Dane i „Poważne błędy”); dawniej AppData\Roaming - przenoszone raz
        internal static readonly string DataDir = AbDane();
        static string AbDane()
        {
            var nowy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velivo", "Audio Balance for Windows");
            var stary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Velivo Audio Balance for Windows");
            try
            {
                if (!Directory.Exists(nowy) && Directory.Exists(stary)) { Directory.CreateDirectory(Path.GetDirectoryName(nowy)); Directory.Move(stary, nowy); }
                if (!Directory.Exists(stary) && Directory.Exists(nowy))   // łącze w starym miejscu - starsza wersja Audio Balance / Velivo trafia w te same dane
                    using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + stary + "\" \"" + nowy + "\"") { CreateNoWindow = true, UseShellExecute = false })) p?.WaitForExit(5000);
            }
            catch (Exception) { if (Directory.Exists(stary)) return stary; }   // nie udało się (plik otwarty) - zostajemy, próba przy następnym starcie
            return nowy;
        }

        static void Line(string s)
        {
            VelivoBledy.Write("Velivo Audio Balance for Windows", s);   // ⚠ wspólny katalog „Poważne błędy” wszystkich programów Velivo
        }
        internal static void LogError(Exception ex) => Line("✖ " + ex);
        internal static void LogInfo(string text) => Line("ℹ " + text);

        [STAThread]
        static void Main(string[] args)
        {
            try   // 🧩 spis programów Velivo (HKCU\Software\Velivo) - inne programy wiedzą, że tu jest
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Velivo\Programy\VelivoAudioBalance"))
                { k.SetValue("Nazwa", "Velivo Audio Balance for Windows"); k.SetValue("Exe", Environment.ProcessPath ?? ""); k.SetValue("Wersja", typeof(App).Assembly.GetName().Version?.ToString() ?? ""); k.SetValue("Dane", DataDir); }
            }
            catch (Exception) { }
            if (Array.Exists(args, a => a == "--aktualizacja")) { AbMain.RestoreSpeakers(true); return; }   // budowa/aktualizacja: głośniki na czas przerwy, nowa wersja kieruje z powrotem
            if (Array.Exists(args, a => a == "--przywroc")) { AbMain.RestoreSpeakers(); return; }   // odinstalowanie: Windows wraca do głośników
            using var one = new Mutex(true, "VelivoAudioBalance.Jeden", out bool first);
            if (!first) { AbMain.SignalShow(); return; }   // drugie uruchomienie - pokaż okno działającej kopii
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Velivo.SuwakRolka.Wlacz();   // kółko myszy na każdym suwaku (jak w Velivo)
            app.DispatcherUnhandledException += (s, e) => { LogError(e.Exception); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { if (e.ExceptionObject is Exception ex) LogError(ex); };
            var main = new AbMain(Array.Exists(args, a => a == "--zasobnik"));
            app.Run();
            main.Dispose();
        }
    }
}
