namespace Velivo
{
    // 🧩 jeden kod, kilka programów: Velivo (Premium, wszystko), Velivo Odtwarzacz, Velivo Przeglądarka (klawiatura zostaje w pełnym Velivo).
    // Wybór przy budowie: dotnet publish -p:Wariant=Odtwarzacz | Przegladarka (bez = pełne Velivo).
    // Każdy działa sam; gdy kilka działa na jednym komputerze - wspólne dane (Velivo\Dane), sieć domową prowadzi jeden.
    static class Wariant
    {
#if WARIANT_ODTWARZACZ
        internal const string Nazwa = "Velivo Odtwarzacz", Pid = "VelivoOdtwarzacz";
        internal const bool Odtwarzacz = true, Przegladarka = false, Pelne = false;
#elif WARIANT_PRZEGLADARKA
        internal const string Nazwa = "Velivo Przeglądarka", Pid = "VelivoPrzegladarka";
        internal const bool Odtwarzacz = false, Przegladarka = true, Pelne = false;
#else
        internal const string Nazwa = "Velivo", Pid = "Velivo";   // Pid: przedrostek wpisów Windows (VelivoHTML, VelivoVideo…) - każdy program własne
        internal const bool Odtwarzacz = false, Przegladarka = false, Pelne = true;
#endif
        // co ma ten program: przeglądarka (karty), odtwarzacz (filmy, TV, Kanapa, Pilot), komunikator
        internal const bool MaKarty = Pelne || Przegladarka;
        internal const bool MaFilmy = Pelne || Odtwarzacz;
        internal const bool MaKomunikator = Pelne;

        // 🤝 współpraca: Przeglądarka oddaje filmy Odtwarzaczowi (gdy zainstalowany); Odtwarzacz strony - przeglądarce domyślnej
        internal static string OdtwarzaczExe => ProgramExe("VelivoOdtwarzacz", "{6E4C2B7A-3F1D-4C8E-9A51-ODTWARZACZ01}", "Velivo Odtwarzacz", "VelivoOdtwarzacz.exe");

        // 🧩 gdzie jest zainstalowany inny program Velivo: miejsce zapisane przez instalator (Windows → Aplikacje), zapasowo folder domyślny
        internal static string ProgramExe(string pid, string appId, string defDir, string exe)
        {
            var zeSpisu = VelivoData.ProgramZeSpisu(pid);   // spis programów Velivo - najpierw
            if (zeSpisu != null) return zeSpisu;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + appId + "_is1"))
                {
                    var dir = k?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(dir) && System.IO.File.Exists(System.IO.Path.Combine(dir, exe))) return System.IO.Path.Combine(dir, exe);
                }
            }
            catch (System.Exception) { }
            return System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Programs", defDir, exe);
        }
        internal static bool Odtworz(params string[] args)
        {
            if (MaFilmy || !System.IO.File.Exists(OdtwarzaczExe)) return false;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(OdtwarzaczExe) { UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch (System.Exception ex) { App.LogError(ex); return false; }
        }
    }
}
