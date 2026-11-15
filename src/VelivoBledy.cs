using System;
using System.IO;
using System.Threading;

namespace Velivo
{
    // ⚠ jeden katalog błędów dla WSZYSTKICH programów Velivo (właściciel 2026-11-15): Velivo, Komunikator, Audio Balance for Windows…
    //    %LOCALAPPDATA%\Velivo\Poważne błędy\bledy.log - nie zależy od instalacji Velivo (program samodzielny pisze tu bez Velivo).
    //    Wpis: czas + [nazwa programu], treść, pusta linia. Kilka programów naraz - wspólna blokada (mutex), plik ponad 5 MB → bledy-stare.log.
    static class VelivoBledy
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velivo", "Poważne błędy");
        public static string File => Path.Combine(Dir, "bledy.log");
        static readonly Mutex Mx = Create();
        static Mutex Create() { try { return new Mutex(false, @"Local\Velivo.PowazneBledy"); } catch (Exception) { return null; } }

        public static void Write(string program, string text)
        {
            bool got = false;
            try
            {
                try { got = Mx?.WaitOne(2000) ?? false; } catch (AbandonedMutexException) { got = true; }
                Directory.CreateDirectory(Dir);
                var f = File;
                try { var fi = new FileInfo(f); if (fi.Exists && fi.Length > 5_000_000) { var old = Path.Combine(Dir, "bledy-stare.log"); System.IO.File.Delete(old); System.IO.File.Move(f, old); } } catch (Exception) { }
                System.IO.File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + program + "]\n" + text + "\n\n");
            }
            catch (Exception) { }
            finally { if (got) try { Mx.ReleaseMutex(); } catch (Exception) { } }
        }
    }
}
