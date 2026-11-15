using System.Collections.Generic;
using System.Globalization;

namespace Velivo
{
    // teksty programu Velivo Audio Balance for Windows: polski (w kodzie) i angielski (Windows w innym języku) - własne, niezależne od Velivo
    static class L
    {
        static readonly bool En = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "pl";
        public static string T(string pl) => En && Ang.TryGetValue(pl, out var en) ? en : pl;
        static readonly Dictionary<string, string> Ang = new Dictionary<string, string>
        {
            { "Cały dźwięk Windows (przeglądarka, serwisy filmowe, gry) wyrównany jak w Velivo: kompresor, poziom LUFS i limiter.", "All Windows sound (browser, streaming services, games) levelled like in Velivo: compressor, LUFS level and limiter." },
            { "Potrzebny jest darmowy VB-Audio Virtual Cable (wirtualna karta dźwiękowa). Windows gra na nią, a Audio Balance przetwarza dźwięk i wysyła go na Twoje głośniki.", "The free VB-Audio Virtual Cable (a virtual sound card) is needed. Windows plays to it, and Audio Balance processes the sound and sends it to your speakers." },
            { "Kliknij „Zainstaluj VB-Cable automatycznie”. Albo ręcznie: pobierz ze strony producenta, uruchom VBCABLE_Setup_x64.exe jako administrator → Install Driver, uruchom komputer ponownie i kliknij „Sprawdź ponownie”.", "Click “Install VB-Cable automatically”. Or manually: download it from the maker’s website, run VBCABLE_Setup_x64.exe as administrator → Install Driver, restart the computer and click “Check again”." },
            { "Pobierz VB-Cable (strona producenta)", "Download VB-Cable (maker’s website)" },
            { "Sprawdź ponownie", "Check again" },
            { "Głośniki / słuchawki (tu trafia poprawiony dźwięk):", "Speakers / headphones (where the corrected sound goes):" },
            { "ŹRÓDŁO (LUFS)", "SOURCE (LUFS)" },
            { "ZMIANA (dB)", "CHANGE (dB)" },
            { "WYNIK (LUFS)", "RESULT (LUFS)" },
            { "KOMPRESOR (dB)", "COMPRESSOR (dB)" },
            { "Wyrównywanie:", "Levelling:" },
            { "Wył.", "Off" },
            { "Wyrównywanie odczuwalnej głośności.", "Levelling of perceived loudness." },
            { "Tylko ograniczanie głośnych szczytów.", "Only limiting loud peaks." },
            { "Intensywność:", "Intensity:" },
            { "Wyraźne dialogi:", "Clear dialogue:" },
            { "Średnio", "Medium" },
            { "Mocno", "Strong" },
            { "Kompresor:", "Compressor:" },
            { "Film / kino", "Film / cinema" },
            { "Próg automatyczny z poziomu dźwięku.", "Automatic threshold from the sound level." },
            { "Noc", "Night" },
            { "Mocne wyrównanie cichych nagrań.", "Strong levelling of quiet recordings." },
            { "Delikatnie", "Gentle" },
            { "Tylko szczyty, równoległa.", "Peaks only, parallel." },
            { "Poziom docelowy – do jakiej głośności wyrównywać.", "Target level – the loudness to level to." },
            { "Auto – inteligentnie (odniesienie -23 LUFS, zalecane)", "Auto – smart (reference -23 LUFS, recommended)" },
            { "Bez przetwarzania (dźwięk bez zmian, do porównania)", "No processing (sound unchanged, for comparison)" },
            { "Uruchamiaj razem z Windows (w zasobniku)", "Start with Windows (in the tray)" },
            { "Do zasobnika", "To tray" },
            { "Zakończ", "Exit" },
            { "Zamyka program; jeśli CABLE jest domyślny, Windows wraca do Twoich głośników.", "Closes the program; if CABLE is the default, Windows goes back to your speakers." },
            { "● Brak VB-Audio Virtual Cable – zainstaluj go (niżej)", "● VB-Audio Virtual Cable missing – install it (below)" },
            { "● Działa: dźwięk Windows → CABLE → Audio Balance → ", "● Running: Windows sound → CABLE → Audio Balance → " },
            { "● Zatrzymany", "● Stopped" },
            { " – nie znaleziono głośników", " – no speakers found" },
            { "działa", "working" },
            { "zatrzymany", "stopped" },
            { "✔ Windows gra przez Audio Balance – przywróć głośniki jako domyślne", "✔ Windows plays through Audio Balance – restore speakers as default" },
            { "▶ Kieruj dźwięk Windows przez Audio Balance (VB-Cable jako urządzenie domyślne)", "▶ Route Windows sound through Audio Balance (VB-Cable as the default device)" },
            { "● Działa, ale Windows gra prosto na ", "● Running, but Windows plays straight to " },
            { " – kliknij przycisk niżej", " – click the button below" },
            { "Pokaż", "Show" },
            { "⬇ Zainstaluj VB-Cable automatycznie", "⬇ Install VB-Cable automatically" },
            { "Pobiera VB-CABLE z serwera VB-Audio i instaluje go. Windows zapyta raz o zgodę (Tak) i o sterownik (Zainstaluj).", "Downloads VB-CABLE from the VB-Audio server and installs it. Windows asks once for permission (Yes) and for the driver (Install)." },
            { "VB-CABLE to darmowy program firmy VB-Audio (donationware) – vb-audio.com.", "VB-CABLE is free software by VB-Audio (donationware) – vb-audio.com." },
            { "Pobieram VB-CABLE z serwera VB-Audio…", "Downloading VB-CABLE from the VB-Audio server…" },
            { "Nie udało się pobrać VB-CABLE (internet, zapora albo antywirus blokuje download.vb-audio.com):", "Could not download VB-CABLE (internet, firewall or antivirus blocks download.vb-audio.com):" },
            { "Rozpakowuję…", "Unpacking…" },
            { "W pobranym pakiecie nie ma instalatora VBCABLE_Setup_x64.exe.", "The downloaded package has no VBCABLE_Setup_x64.exe installer." },
            { "Instaluję – kliknij „Tak” i „Zainstaluj” w oknach Windows…", "Installing – click “Yes” and “Install” in the Windows dialogs…" },
            { "Instalacja nie ruszyła (odmowa w oknie Kontrola konta użytkownika?): ", "Installation did not start (refused in User Account Control?): " },
            { "✔ VB-CABLE zainstalowany.", "✔ VB-CABLE installed." },
            { "✔ Instalacja zakończona – uruchom komputer ponownie, potem kliknij „Sprawdź ponownie”.", "✔ Installation finished – restart the computer, then click “Check again”." },
            { "odbiór z CABLE Output przerwany", "capture from CABLE Output stopped" },
            { "odtwarzanie na głośnikach przerwane", "playback on the speakers stopped" },
            { " – ponawiam za chwilę", " – retrying shortly" },
            { " (szczegóły: bledy.log w folderze ustawień)", " (details: bledy.log in the settings folder)" },
        };
    }
}
