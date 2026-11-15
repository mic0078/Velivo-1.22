namespace Velivo
{
    // 🧩 jeden kod, kilka programów: Velivo (Premium, wszystko), Velivo Odtwarzacz, Velivo Przeglądarka (klawiatura zostaje w pełnym Velivo).
    // Wybór przy budowie: dotnet publish -p:Wariant=Odtwarzacz | Przegladarka (bez = pełne Velivo).
    // Każdy działa sam; gdy kilka działa na jednym komputerze - wspólne dane (Velivo\Dane), sieć domową prowadzi jeden.
    static class Wariant
    {
#if WARIANT_ODTWARZACZ
        internal const string Nazwa = "Velivo Odtwarzacz";
        internal const bool Odtwarzacz = true, Przegladarka = false, Pelne = false;
#elif WARIANT_PRZEGLADARKA
        internal const string Nazwa = "Velivo Przeglądarka";
        internal const bool Odtwarzacz = false, Przegladarka = true, Pelne = false;
#else
        internal const string Nazwa = "Velivo";
        internal const bool Odtwarzacz = false, Przegladarka = false, Pelne = true;
#endif
        // co ma ten program: przeglądarka (karty), odtwarzacz (filmy, TV, Kanapa, Pilot), komunikator
        internal const bool MaKarty = Pelne || Przegladarka;
        internal const bool MaFilmy = Pelne || Odtwarzacz;
        internal const bool MaKomunikator = Pelne;

        // 🤝 współpraca: Przeglądarka oddaje filmy Odtwarzaczowi (gdy zainstalowany); Odtwarzacz strony - przeglądarce domyślnej
        internal static string OdtwarzaczExe => System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Programs", "Velivo Odtwarzacz", "VelivoOdtwarzacz.exe");
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
