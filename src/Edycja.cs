namespace Velivo
{
    // 💎 Edycja Velivo: Premium (płatna) albo Podstawowa (bezpłatna) - ten sam kod, wybór przy budowie: .\zbuduj-wszystko.ps1 -Podstawowa
    // Premium: Audio Balance (normalizacja, Audio Monitor, widżety, nauka, korektor, dialogi), VST3, głosy Supertonic i XTTS,
    //          Pilot (telefon steruje odtwarzaczem), Kanapa, nagrywanie TV, Oglądaj razem. Reszta Velivo - bezpłatna.
    internal static class Edycja
    {
#if PODSTAWOWA
        public static readonly bool Premium = false;
#else
        public static readonly bool Premium = true;   // readonly zamiast const - bez ostrzeżeń „nieosiągalny kod” w obu edycjach
#endif
    }

    public partial class MainWindow
    {
        // funkcja Premium w Velivo bezpłatnym: krótka informacja zamiast cichego „nic” (false = nie wykonuj)
        internal bool PremiumOk(string co)
        {
            if (Edycja.Premium) return true;
            ShowToast("💎 " + co + L.T(" – w Velivo Premium"), null); return false;
        }
    }
}
