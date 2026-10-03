using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Przegladarka
{
    // Jezyk interfejsu: polski (oryginal w kodzie) albo angielski (slownik ponizej).
    // L.T("polski tekst") zwraca tlumaczenie, gdy wybrano angielski, a gdy brak wpisu - tekst polski.
    public static class L
    {
        public static bool En { get; private set; }

        // ustawienie: "auto" (jezyk Windows), "pl", "en"
        public static void Init(string setting)
        {
            setting = (setting ?? "auto").Trim().ToLowerInvariant();
            En = setting == "en" || (setting != "pl" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "pl");
        }

        public static string T(string pl)
        {
            string en;
            return En && pl != null && D.TryGetValue(pl, out en) ? en : pl;
        }

        // Tlumaczy napisy okna zbudowanego w XAML: tresc przyciskow, dymki, teksty, pozycje menu.
        public static void TranslateTree(DependencyObject root)
        {
            if (!En || root == null) return;
            if (root is FrameworkElement fe && fe.ToolTip is string tip) fe.ToolTip = T(tip);
            if (root is HeaderedItemsControl hi && hi.Header is string h) hi.Header = T(h);
            if (root is ContentControl cc && cc.Content is string c) cc.Content = T(c);
            if (root is TextBlock tb && !string.IsNullOrEmpty(tb.Text)) tb.Text = T(tb.Text);
            foreach (var child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject d) TranslateTree(d);
        }

        static readonly Dictionary<string, string> D = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ---- okno glowne (XAML) ----
            { "Czytnik", "Reader" },
            { "Prywatność", "Privacy" },
            { "Prywatność*", "Privacy*" },
            { "➕ Dodaj do Velivo", "➕ Add to Velivo" },
            { "Aktywny profil", "Active profile" },
            { "Czytaj stronę na głos (Ctrl+Shift+U)", "Read page aloud (Ctrl+Shift+U)" },
            { "Dalej (Alt+→)", "Forward (Alt+→)" },
            { "Wstecz (Alt+←)", "Back (Alt+←)" },
            { "Odśwież (F5)", "Reload (F5)" },
            { "Strona startowa", "Home page" },
            { "Dodaj/usuń zakładkę (Ctrl+D)", "Add/remove bookmark (Ctrl+D)" },
            { "Dodatki (tryb dewelopera)", "Extensions (developer mode)" },
            { "Historia (Ctrl+H)", "History (Ctrl+H)" },
            { "Nowa karta (Ctrl+T)", "New tab (Ctrl+T)" },
            { "Nowa karta prywatna (Ctrl+Shift+N) – nic nie zapisuje", "New private tab (Ctrl+Shift+N) – saves nothing" },
            { "Panel prywatności i antyfingerprinting", "Privacy and anti-fingerprinting panel" },
            { "Aktywna reguła prywatności dla tej domeny", "Active privacy rule for this domain" },
            { "Pobiera ten dodatek ze sklepu i instaluje go", "Downloads this extension from the store and installs it" },
            { "Pobrane pliki (Ctrl+J)", "Downloads (Ctrl+J)" },
            { "Prędkość czytania (kliknij, aby zmienić)", "Reading speed (click to change)" },
            { "Tryb ciemny stron", "Dark mode for pages" },
            { "Tryb czytania + lokalne streszczenie", "Reader mode + local summary" },
            { "Ustawienia", "Settings" },
            { "Więcej narzędzi", "More tools" },
            { "Wszystkie zakładki", "All bookmarks" },
            { "Włącz/wyłącz AdBlock", "Turn AdBlock on/off" },
            { "Zatrzymaj czytanie", "Stop reading" },
            { "Zrzut ekranu strony (widoczna część lub cała strona)", "Page screenshot (visible part or full page)" },
            { "Nowa karta", "New tab" },
            { "🕶 Prywatna", "🕶 Private" },
            { "wyłączony", "off" },

            // ---- pasek: menu "wiecej" ----
            { "Tryb czytania", "Reader mode" },
            { "Zrzut ekranu", "Screenshot" },
            { "Czytaj na głos", "Read aloud" },
            { "Tryb ciemny", "Dark mode" },
            { "Pobrane pliki", "Downloads" },
            { "Dodatki", "Extensions" },
            { "Historia", "History" },
            { "Zakładki", "Bookmarks" },

            // ---- menu karty ----
            { "Odśwież", "Reload" },
            { "Duplikuj kartę", "Duplicate tab" },
            { "Zamknij kartę (Ctrl+W)", "Close tab (Ctrl+W)" },
            { "Zamknij inne karty", "Close other tabs" },
            { "Zamknij karty po prawej", "Close tabs to the right" },
            { "Przywróć zamkniętą kartę (Ctrl+Shift+T)", "Reopen closed tab (Ctrl+Shift+T)" },

            // ---- powiekszenie ----
            { "Powiększ (Ctrl +)", "Zoom in (Ctrl +)" },
            { "Pomniejsz (Ctrl −)", "Zoom out (Ctrl −)" },
            { "Domyślne (Ctrl 0)", "Default (Ctrl 0)" },

            // ---- menu pod prawym przyciskiem ----
            { "Przetłumacz zaznaczenie na polski", "Translate selection to Polish" },
            { "Czytaj zaznaczenie na głos", "Read selection aloud" },
            { "Przetłumacz stronę na polski", "Translate page to Polish" },
            { "Przetłumacz stronę na angielski", "Translate page to English" },
            { "🚫 Blokuj element (reklamę)…", "🚫 Block element (ad)…" },
            { "Przywróć zablokowane elementy na tej stronie", "Restore blocked elements on this page" },
            { "Tryb czytania i streszczenie", "Reader mode and summary" },
            { "Czytaj stronę na głos (Ctrl+Shift+U) ", "Read page aloud (Ctrl+Shift+U) " },
            { "Widoczna część strony", "Visible part of the page" },
            { "Cała strona (z przewijaniem)", "Full page (scrolling)" },
            { "Narzędzia Velivo", "Velivo tools" },
            { "Prywatność i antyfingerprinting", "Privacy and anti-fingerprinting" },
            { "Wykryj media do pobrania", "Detect downloadable media" },
            { "Menedżer pobrań", "Download manager" },
            { "Diagnostyka LAN sync", "LAN sync diagnostics" },
            { "Przełącz profil: praca", "Switch profile: work" },
            { "Przełącz użytkownika/profil…", "Switch user/profile…" },

            // ---- tryb ciemny / nocny ----
            { "Tryb ciemny stron: WŁĄCZONY\nKliknij, aby wyłączyć", "Dark mode for pages: ON\nClick to turn off" },
            { "Tryb ciemny stron: wyłączony\nKliknij, aby przyciemnić strony (zdjęcia zostają w prawdziwych kolorach)", "Dark mode for pages: off\nClick to darken pages (photos keep their real colors)" },
            { "Tryb nocny: WŁĄCZONY (cieplejsze kolory)\nKliknij, aby wrócić do trybu jasnego", "Night mode: ON (warmer colors)\nClick to return to light mode" },
            { "🌙 Tryb ciemny", "🌙 Dark mode" },
            { "🌅 Tryb nocny – cieplejsze kolory", "🌅 Night mode – warmer colors" },
            { "☀ Tryb jasny", "☀ Light mode" },

            // ---- ustawienia: jezyk ----
            { "Język interfejsu / Language:", "Language / Język interfejsu:" },
            { "Automatycznie (język Windows)", "Automatic (Windows language)" },
            { "Zmiana języka zadziała po ponownym uruchomieniu Velivo.", "The language change takes effect after restarting Velivo." },
        };
    }
}
