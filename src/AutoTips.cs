using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Przegladarka
{
    // Podpowiedzi w jednym miejscu: przycisk / pozycja menu bez wlasnej podpowiedzi dostaje ja stad wg napisu
    // (dokladnie albo po poczatku napisu). Istniejace podpowiedzi zostaja - ten slownik tylko uzupelnia braki.
    internal static class AutoTips
    {
        // napis (jak w kodzie, przed tlumaczeniem) -> podpowiedz
        static readonly (string Text, string Tip)[] Exact =
        {
            // 🧹 porzadki i kopia
            ("🧹 Wyczyść zaznaczone", "Usuwa zaznaczone śmieci. Hasła, zakładki i logowania na stronach zostają."),
            ("Zmień folder…", "Wybierz inny folder (najlepiej na innym dysku lub pendrive)."),
            ("♻ Przywróć z kopii…", "Wybierz kopię – Velivo uruchomi się ponownie i przywróci z niej dane (kopia przyrostowa sama składa się z pełną)."),
            ("📁 Otwórz folder kopii", "Pokazuje folder z kopiami w Eksploratorze."),
            // 🏦 tryb bankowy
            ("⚙ Ustawienia trybu bankowego…", "Hasło trybu, klucze sprzętowe, czas automatycznej blokady."),
            ("💳 Moje karty", "Zaszyfrowane karty płatnicze – numer i CVV widoczne dopiero po kliknięciu 👁."),
            ("💳 Wypełnij kartę na tej stronie", "Wpisuje dane wybranej karty w formularz płatności na otwartej stronie."),
            ("🔑 Moje loginy i hasła", "Loginy i hasła trybu bankowego (osobne od zwykłej bazy haseł Velivo)."),
            ("📄 Poufne dane (dokumenty)", "Numery dokumentów i inne poufne dane – zaszyfrowane w bazie trybu."),
            ("🏦 Rachunki bankowe", "Twoje rachunki i odbiorcy przelewów – do szybkiego wypełniania przelewów."),
            ("🧾 Rachunki do opłacenia", "Rachunki z terminami – Velivo przypomina o nich przy otwarciu trybu."),
            ("📊 Arkusz rachunków (w karcie)", "Zestawienie rachunków w formie arkusza w osobnej karcie."),
            ("📝 Moje notatki (loginy, hasła, numery klienta)", "Zaszyfrowane notatki – z linii „login:” i „hasło:” Velivo umie wpisać logowanie."),
            ("🏦 Moje banki", "Lista Twoich banków – klik otwiera stronę w trybie bankowym."),
            ("🛒 Moje sklepy online", "Lista sklepów – klik otwiera stronę w trybie bankowym."),
            ("➕ Dodaj tę stronę do Moich banków", "Zapamiętuje otwartą stronę na liście Moich banków."),
            ("➕ Dodaj tę stronę do Moich sklepów", "Zapamiętuje otwartą stronę na liście Moich sklepów."),
            ("👤 Profile bankowe", "Osobny tryb bankowy dla innej osoby – własne hasło, klucz i dane."),
            ("❓ Instrukcja trybu bankowego", "Opis wszystkich funkcji trybu bankowego."),
            ("🔍 Szukaj w mojej bazie…", "Szuka we wszystkich zapisanych danych trybu (karty, loginy, notatki, rachunki)."),
            ("🔑 Wpisz login i hasło na tej stronie", "Wypełnia logowanie na otwartej stronie danymi z bazy trybu."),
            ("🔢 Wpisz wybrane znaki (passcode / hasło, np. RBS, NatWest)", "Dla banków, które pytają o wybrane znaki hasła (np. 2., 5. i 8.) – Velivo wpisze właściwe."),
            ("💸 Wypełnij przelew na tej stronie (odbiorca z Rachunków bankowych)", "Wpisuje odbiorcę, numer rachunku i tytuł w formularz przelewu."),
            ("🧪 Skopiuj opis formularza logowania (bez Twoich danych)", "Kopiuje do schowka budowę formularza – przydaje się, gdy wpisywanie na jakiejś stronie nie działa."),
            ("💾 Kopia zapasowa bazy…", "Zaszyfrowany plik .vbank ze wszystkimi profilami trybu bankowego."),
            ("📂 Przywróć bazę z kopii…", "Wczytuje bazę trybu z pliku .vbank."),
            ("📜 Dziennik otwarć…", "Kiedy, na którym komputerze i czym otwierano tryb (także nieudane próby)."),
            ("🔒 Zablokuj teraz", "Natychmiast zamyka tryb bankowy i jego okna."),
            ("Zapomniałem hasła – wyczyść tryb bankowy…", "Usuwa tryb bankowy razem z jego danymi – potem można założyć nowy."),
            ("➕ Nowy profil bankowy (inny użytkownik)…", "Zakłada osobny tryb bankowy dla innej osoby."),
            ("🔑 Otwórz kluczem sprzętowym", "Włóż klucz (YubiKey, Titan…) i dotknij go zamiast wpisywać hasło."),
            ("Dodatkowo wymagaj klucza sprzętowego (YubiKey, Google Titan…)", "Tryb otworzy się dopiero po haśle i dotknięciu klucza."),
            ("➕ Dodaj klucz (włóż go i dotknij)", "Rejestruje nowy klucz sprzętowy do tego trybu."),
            ("Usuń wszystkie klucze", "Tryb będzie otwierany samym hasłem."),
            ("👁 Pokaż numer i CVV", "Pokazuje pełny numer karty i CVV (chwilowo)."),
            ("👁 Pokaż ukryte dane", "Pokazuje zamaskowane pola (chwilowo)."),
            ("🌐 Otwórz stronę", "Otwiera stronę w trybie bankowym."),
            ("🔑 Wpisz na otwartej stronie", "Wpisuje login i hasło – tylko na stronie o tym samym adresie."),
            ("🎲 Wygeneruj hasło", "Tworzy silne, losowe hasło."),
            ("✔ Zapłacone (następny termin)", "Oznacza rachunek jako zapłacony i ustawia kolejny termin."),
            ("🌐 Otwórz stronę płatności", "Otwiera stronę, na której płacisz ten rachunek."),
            ("📊 Zestawienie płatności", "Wszystkie płatności rachunku w kolejnych miesiącach."),
            ("📤 Wczytaj z Excela (CSV)…", "Dopisuje rachunki z pliku CSV (np. z Excela)."),
            ("📥 Zapisz do Excela (CSV)", "Zapisuje rachunki do pliku CSV do otwarcia w Excelu."),
            ("📋 Kopiuj treść", "Kopiuje treść do schowka."),
            ("🏦 Przełącz na tryb bankowy", "Otwiera tę stronę w odizolowanym trybie bankowym."),
            // przegladarka
            ("Importuj zaznaczone zakładki", "Dopisuje zaznaczone zakładki do Velivo – istniejące zostają."),
            ("Wczytaj plik CSV…", "Hasła i loginy z pliku CSV (KeePassXC, Chrome, Edge, Firefox)."),
            ("Zarządzaj dodatkami…", "Włączanie, wyłączanie i usuwanie dodatków."),
            ("Otwórz okienko dodatku", "Otwiera menu dodatku jak w Chrome."),
            ("Otwórz okienko w karcie", "Otwiera okienko dodatku w zwykłej karcie (większe)."),
            ("Opcje", "Ustawienia dodatku."),
            ("Odepnij z paska (dodatek dalej działa)", "Chowa ikonę dodatku z paska – dodatek działa dalej."),
            ("Przywróć domyślne", "Wraca do ustawień fabrycznych tej części."),
            ("Wszystkie zakładki…", "Okno ze wszystkimi zakładkami – szukanie, edycja, foldery."),
            ("Otwórz wszystkie w nowych kartach", "Otwiera wszystkie zaznaczone strony naraz."),
            ("Usuń tę stronę z historii dnia", "Usuwa tę stronę z historii z wybranego dnia."),
            ("Wyczyść pamięć stron", "Usuwa zapamiętaną treść stron używaną przez „Gdzie ja to czytałem?”."),
            ("📺 Wyślij do…", "Wysyła stronę lub film na inny komputer z Velivo w sieci domowej."),
            ("Blokuj JavaScript dla domeny", "Strona nie uruchomi skryptów (niektóre strony mogą wtedy nie działać)."),
            ("Nie wysyłaj cookies dla domeny", "Strona nie dostanie ciasteczek – nie zapamięta logowania."),
            ("Wymuś blokowanie trackerów dla domeny", "Zawsze blokuje śledzenie na tej stronie, także przy łagodniejszych ustawieniach."),
            ("Automatycznie czyść dane po wejściu na domenę", "Po każdym wejściu strona dostaje czyste dane (ciasteczka, pamięć)."),
            ("Zaufana domena (bez blokowania i SmartScreen)", "Velivo niczego tu nie blokuje – tylko dla stron, którym ufasz."),
            ("Widoczna część strony", "Zrzut tego, co widać w oknie."),
            ("Cała strona (z przewijaniem)", "Zrzut całej strony, także części poniżej ekranu."),
            ("Otwórz folder ze zrzutami", "Pokazuje folder ze zrzutami ekranu."),
            ("Szukaj w kartach (Ctrl+Shift+A)", "Szuka po tytułach i adresach otwartych kart."),
            ("Grupy kart", "Łączenie kart w nazwane, kolorowe grupy."),
            ("🕶 Otwórz jako prywatną (incognito)", "Otwiera tę stronę w karcie prywatnej – bez historii i ciasteczek."),
            ("Duplikuj kartę", "Otwiera drugą kartę z tą samą stroną."),
            ("Historia przeglądania", "Lista odwiedzonych stron."),
            ("Pamięć podręczna (cache)", "Zapisane kopie stron i obrazków – wczytają się na nowo."),
            ("Historia pobrań", "Lista pobranych plików (same pliki zostają na dysku)."),
            ("Cookies i aktywne sesje (wyloguje konta)", "Usuwa ciasteczka – wylogujesz się ze stron."),
            ("Dane formularzy i kart zapisane przez silnik", "Autouzupełnianie silnika Edge (baza Velivo zostaje)."),
            ("Zapisane hasła w silniku", "Hasła zapisane przez silnik Edge – baza haseł Velivo zostaje."),
            ("Odświeżaj automatycznie", "Karta sama odświeża stronę co wybrany czas."),
            ("Zestawy kart", "Zapisane zestawy kart do otwarcia jednym kliknięciem."),
            ("Zapisz otwarte karty jako zestaw…", "Zapamiętuje wszystkie otwarte karty pod jedną nazwą."),
            ("Zastąp obecnymi kartami", "Nadpisuje zestaw kartami, które są teraz otwarte."),
            ("Rozgrupuj (karty zostają)", "Usuwa grupę – karty zostają otwarte."),
            ("Zapisz grupę jako zestaw kart", "Zapamiętuje karty tej grupy jako zestaw."),
            ("⬇ Pobierz / uzupełnij teraz", "Pobiera brakujące okładki i opisy filmów."),
            ("🔊 Wybierz głośniki dla Velivo…", "Velivo może grać na innych głośnikach niż reszta Windows."),
            ("Wklej i przejdź", "Wkleja adres ze schowka i od razu go otwiera."),
            ("🗒 Karteczki na pulpit", "Kolorowe karteczki, które zostają na pulpicie."),
            ("↺ Ikona z listy", "Wraca do ikony kanału z listy dostawcy."),
            ("Przywróć oryginał", "Wraca do nazwy i ikony z listy dostawcy."),
            ("📂 Z pliku…", "Wybierz obrazek z dysku."),
            ("📁 Zmień folder nagrań…", "Gdzie zapisywać nagrania telewizji."),
            ("Przed początkiem audycji", "Ile minut wcześniej zacząć nagrywanie (audycje często startują przed czasem)."),
            ("Po końcu audycji", "Ile minut dłużej nagrywać (audycje często się przedłużają)."),
            ("✏ Wpisz własny zapas…", "Dowolna liczba minut przed i po audycji."),
            // filmy i TV
            ("🎞 Playlisty i moja kolejka", "Twoje playlisty i kolejka filmów do obejrzenia."),
            ("Zapamiętaj wybór i nie pytaj (zmiana: Ustawienia → Odtwarzacz filmów)", "Velivo zapamięta odpowiedź i nie będzie pytać przy kolejnych filmach."),
            ("⏮ Od początku", "Odtwarza film od początku."),
            ("Tylko nieobejrzane", "Pokazuje tylko filmy, których jeszcze nie obejrzałeś."),
            ("➕ Dodaj zaznaczone", "Dopisuje zaznaczone filmy do playlisty."),
            ("🗑 Usuń playlistę", "Usuwa playlistę (filmy zostają na dysku)."),
            ("Nagrywany kanał oglądaj z nagrania (jedno połączenie, pauza i cofanie)", "Gdy kanał się nagrywa, oglądasz go z nagrania – jedno połączenie z dostawcą, można pauzować i cofać."),
            ("Po 5 min pauzy zwolnij połączenie z dostawcą", "Długa pauza zwalnia połączenie – nie blokuje limitu urządzeń u dostawcy."),
            ("Lektor włączony", "Czyta napisy na głos."),
            ("Pokazuj napisy na ekranie", "Pokazuje napisy na obrazie (lektor może czytać także przy ukrytych)."),
            ("Pokazuj zegar", "Zegar na obrazie filmu lub kanału."),
            ("⧉ Pokaż w PiP (podgląd)", "Otwiera ten kanał lub film w małym okienku obok obecnego."),
            ("↕ Sortuj według", "Kolejność listy – zapamiętana."),
            ("🎬 Zostań przy filmie", "Nie przełącza – film gra dalej."),
            ("🎬 Po końcu tego filmu", "Wyłącznik zadziała, gdy skończy się obecny film."),
            ("💻 Potem wyłącz komputer", "Po wyłączniku czasowym wyłącza też komputer."),
            ("✖ Wyłącz wyłącznik czasowy", "Odwołuje zaplanowane zatrzymanie."),
            ("Pokazuj przy ruchu myszy", "Pasek informacji pojawia się, gdy ruszysz myszą."),
            ("⏱ Znikanie po", "Po ilu sekundach pasek informacji znika."),
            ("🌓 Przezroczystość tła", "Jak bardzo prześwituje tło paska informacji."),
            ("🔠 Wielkość tekstu (względem okna)", "Wielkość napisów paska informacji."),
            ("📂 Wczytaj napisy z pliku…", "Napisy z pliku .srt / .txt z dysku."),
            ("🗑 Usuń plik napisów", "Usuwa pobrany plik napisów tego filmu."),
            ("⏱ Przesunięcie napisów (na żywo)…", "Gdy napisy spieszą się albo spóźniają – przesuwasz je w trakcie oglądania."),
            ("Dźwięk 5.1 bez miksowania do stereo (zestaw kina domowego)", "Dla amplitunera / kina domowego – dźwięk wielokanałowy idzie bez zmian."),
            ("📋 Przezroczystość listy (kanały i filmy)", "Jak bardzo prześwituje lista kanałów i filmów."),
            ("🕘 Ostatnio oglądane Filmy", "Ostatnie filmy – klik wznawia od miejsca, w którym skończyłeś."),
            ("🕘 Ostatnio oglądane TV", "Ostatnio oglądane kanały."),
            ("⏾ Wyłącznik czasowy", "Zatrzymuje odtwarzanie po wybranym czasie albo po filmie."),
            ("⧉ Obraz w obrazie (PiP)", "Małe okienko z drugim kanałem lub filmem."),
            ("📺 Telewizja", "Kanały telewizji."),
            ("🎬 Filmy", "Twoja biblioteka filmów."),
            ("📂 Wszystkie grupy", "Wszystkie grupy kanałów z listy."),
            ("⏹ Zatrzymaj nagrywanie", "Kończy nagrywanie – nagranie zostaje na dysku."),
            ("Powiększ (Ctrl +)", "Powiększa stronę – zapamiętane dla tej strony."),
            ("Pomniejsz (Ctrl −)", "Pomniejsza stronę – zapamiętane dla tej strony."),
            ("Domyślne (Ctrl 0)", "Wraca do domyślnej wielkości strony."),
        };

        // napisy zmienne (z liczba, nazwa itp.) - po poczatku napisu
        static readonly (string Prefix, string Tip)[] Prefixes =
        {
            ("▶ Wznów od ", "Wznawia film od miejsca, w którym skończyłeś."),
            ("➕ Przedłuż o ", "Nagrywa dłużej o wybrany czas."),
            ("⏱ Zapas nagrywania: ", "Ile minut przed i po audycji nagrywać – audycje często zaczynają się wcześniej i kończą później."),
        };

        // podpowiedzi po angielsku (gdy Velivo jest po angielsku)
        static readonly Dictionary<string, string> En = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Usuwa zaznaczone śmieci. Hasła, zakładki i logowania na stronach zostają.", "Removes the selected junk. Passwords, bookmarks and site logins stay." },
            { "Wybierz inny folder (najlepiej na innym dysku lub pendrive).", "Choose another folder (ideally on another drive or a USB stick)." },
            { "Wybierz kopię – Velivo uruchomi się ponownie i przywróci z niej dane (kopia przyrostowa sama składa się z pełną).", "Choose a backup – Velivo restarts and restores the data from it (an incremental backup is combined with its full one automatically)." },
            { "Pokazuje folder z kopiami w Eksploratorze.", "Shows the backup folder in Explorer." },
            { "Hasło trybu, klucze sprzętowe, czas automatycznej blokady.", "Mode password, hardware keys, automatic lock time." },
            { "Zaszyfrowane karty płatnicze – numer i CVV widoczne dopiero po kliknięciu 👁.", "Encrypted payment cards – number and CVV shown only after clicking 👁." },
            { "Wpisuje dane wybranej karty w formularz płatności na otwartej stronie.", "Fills the selected card into the payment form on the open page." },
            { "Loginy i hasła trybu bankowego (osobne od zwykłej bazy haseł Velivo).", "Bank mode logins and passwords (separate from the regular Velivo password vault)." },
            { "Numery dokumentów i inne poufne dane – zaszyfrowane w bazie trybu.", "Document numbers and other confidential data – encrypted in the mode's database." },
            { "Twoje rachunki i odbiorcy przelewów – do szybkiego wypełniania przelewów.", "Your bank accounts and payees – for filling in transfers quickly." },
            { "Rachunki z terminami – Velivo przypomina o nich przy otwarciu trybu.", "Bills with due dates – Velivo reminds you about them when the mode opens." },
            { "Zestawienie rachunków w formie arkusza w osobnej karcie.", "Bills summary as a spreadsheet in a separate tab." },
            { "Zaszyfrowane notatki – z linii „login:” i „hasło:” Velivo umie wpisać logowanie.", "Encrypted notes – Velivo can fill in a login from lines “login:” and “password:”." },
            { "Lista Twoich banków – klik otwiera stronę w trybie bankowym.", "List of your banks – a click opens the site in bank mode." },
            { "Lista sklepów – klik otwiera stronę w trybie bankowym.", "List of shops – a click opens the site in bank mode." },
            { "Zapamiętuje otwartą stronę na liście Moich banków.", "Remembers the open page in My banks." },
            { "Zapamiętuje otwartą stronę na liście Moich sklepów.", "Remembers the open page in My shops." },
            { "Osobny tryb bankowy dla innej osoby – własne hasło, klucz i dane.", "A separate bank mode for another person – own password, key and data." },
            { "Opis wszystkich funkcji trybu bankowego.", "Description of all bank mode features." },
            { "Szuka we wszystkich zapisanych danych trybu (karty, loginy, notatki, rachunki).", "Searches all data saved in the mode (cards, logins, notes, bills)." },
            { "Wypełnia logowanie na otwartej stronie danymi z bazy trybu.", "Fills in the login on the open page with data from the mode's database." },
            { "Dla banków, które pytają o wybrane znaki hasła (np. 2., 5. i 8.) – Velivo wpisze właściwe.", "For banks that ask for selected password characters (e.g. 2nd, 5th and 8th) – Velivo types the right ones." },
            { "Wpisuje odbiorcę, numer rachunku i tytuł w formularz przelewu.", "Fills in the payee, account number and reference in the transfer form." },
            { "Kopiuje do schowka budowę formularza – przydaje się, gdy wpisywanie na jakiejś stronie nie działa.", "Copies the form structure to the clipboard – useful when filling in does not work on some site." },
            { "Zaszyfrowany plik .vbank ze wszystkimi profilami trybu bankowego.", "Encrypted .vbank file with all bank mode profiles." },
            { "Wczytuje bazę trybu z pliku .vbank.", "Loads the mode's database from a .vbank file." },
            { "Kiedy, na którym komputerze i czym otwierano tryb (także nieudane próby).", "When, on which computer and with what the mode was opened (failed attempts too)." },
            { "Natychmiast zamyka tryb bankowy i jego okna.", "Closes bank mode and its windows immediately." },
            { "Usuwa tryb bankowy razem z jego danymi – potem można założyć nowy.", "Removes bank mode together with its data – you can set up a new one afterwards." },
            { "Zakłada osobny tryb bankowy dla innej osoby.", "Sets up a separate bank mode for another person." },
            { "Włóż klucz (YubiKey, Titan…) i dotknij go zamiast wpisywać hasło.", "Insert the key (YubiKey, Titan…) and touch it instead of typing the password." },
            { "Tryb otworzy się dopiero po haśle i dotknięciu klucza.", "The mode opens only after the password and touching the key." },
            { "Rejestruje nowy klucz sprzętowy do tego trybu.", "Registers a new hardware key for this mode." },
            { "Tryb będzie otwierany samym hasłem.", "The mode will be opened with the password alone." },
            { "Pokazuje pełny numer karty i CVV (chwilowo).", "Shows the full card number and CVV (briefly)." },
            { "Pokazuje zamaskowane pola (chwilowo).", "Shows masked fields (briefly)." },
            { "Otwiera stronę w trybie bankowym.", "Opens the site in bank mode." },
            { "Wpisuje login i hasło – tylko na stronie o tym samym adresie.", "Fills in the login and password – only on a page with the same address." },
            { "Tworzy silne, losowe hasło.", "Creates a strong random password." },
            { "Oznacza rachunek jako zapłacony i ustawia kolejny termin.", "Marks the bill as paid and sets the next due date." },
            { "Otwiera stronę, na której płacisz ten rachunek.", "Opens the page where you pay this bill." },
            { "Wszystkie płatności rachunku w kolejnych miesiącach.", "All payments of the bill in the following months." },
            { "Dopisuje rachunki z pliku CSV (np. z Excela).", "Adds bills from a CSV file (e.g. from Excel)." },
            { "Zapisuje rachunki do pliku CSV do otwarcia w Excelu.", "Saves bills to a CSV file to open in Excel." },
            { "Kopiuje treść do schowka.", "Copies the content to the clipboard." },
            { "Otwiera tę stronę w odizolowanym trybie bankowym.", "Opens this page in isolated bank mode." },
            { "Dopisuje zaznaczone zakładki do Velivo – istniejące zostają.", "Adds the selected bookmarks to Velivo – existing ones stay." },
            { "Hasła i loginy z pliku CSV (KeePassXC, Chrome, Edge, Firefox).", "Passwords and logins from a CSV file (KeePassXC, Chrome, Edge, Firefox)." },
            { "Włączanie, wyłączanie i usuwanie dodatków.", "Enable, disable and remove add-ons." },
            { "Otwiera menu dodatku jak w Chrome.", "Opens the add-on menu like in Chrome." },
            { "Otwiera okienko dodatku w zwykłej karcie (większe).", "Opens the add-on popup in a regular tab (larger)." },
            { "Ustawienia dodatku.", "Add-on settings." },
            { "Chowa ikonę dodatku z paska – dodatek działa dalej.", "Hides the add-on icon from the toolbar – the add-on keeps working." },
            { "Wraca do ustawień fabrycznych tej części.", "Returns this part to factory settings." },
            { "Okno ze wszystkimi zakładkami – szukanie, edycja, foldery.", "Window with all bookmarks – search, editing, folders." },
            { "Otwiera wszystkie zaznaczone strony naraz.", "Opens all selected pages at once." },
            { "Usuwa tę stronę z historii z wybranego dnia.", "Removes this page from the history of the selected day." },
            { "Usuwa zapamiętaną treść stron używaną przez „Gdzie ja to czytałem?”.", "Removes the remembered page contents used by “Where did I read that?”." },
            { "Wysyła stronę lub film na inny komputer z Velivo w sieci domowej.", "Sends the page or video to another computer with Velivo on the home network." },
            { "Strona nie uruchomi skryptów (niektóre strony mogą wtedy nie działać).", "The site will not run scripts (some sites may then not work)." },
            { "Strona nie dostanie ciasteczek – nie zapamięta logowania.", "The site gets no cookies – it will not remember your login." },
            { "Zawsze blokuje śledzenie na tej stronie, także przy łagodniejszych ustawieniach.", "Always blocks tracking on this site, even with milder settings." },
            { "Po każdym wejściu strona dostaje czyste dane (ciasteczka, pamięć).", "On every visit the site gets clean data (cookies, storage)." },
            { "Velivo niczego tu nie blokuje – tylko dla stron, którym ufasz.", "Velivo blocks nothing here – only for sites you trust." },
            { "Zrzut tego, co widać w oknie.", "Screenshot of what is visible in the window." },
            { "Zrzut całej strony, także części poniżej ekranu.", "Screenshot of the whole page, including the part below the screen." },
            { "Pokazuje folder ze zrzutami ekranu.", "Shows the screenshots folder." },
            { "Szuka po tytułach i adresach otwartych kart.", "Searches titles and addresses of open tabs." },
            { "Łączenie kart w nazwane, kolorowe grupy.", "Combine tabs into named, coloured groups." },
            { "Otwiera tę stronę w karcie prywatnej – bez historii i ciasteczek.", "Opens this page in a private tab – no history and no cookies." },
            { "Otwiera drugą kartę z tą samą stroną.", "Opens a second tab with the same page." },
            { "Lista odwiedzonych stron.", "List of visited pages." },
            { "Zapisane kopie stron i obrazków – wczytają się na nowo.", "Saved copies of pages and images – they will load again." },
            { "Lista pobranych plików (same pliki zostają na dysku).", "List of downloaded files (the files themselves stay on disk)." },
            { "Usuwa ciasteczka – wylogujesz się ze stron.", "Removes cookies – you will be signed out of sites." },
            { "Autouzupełnianie silnika Edge (baza Velivo zostaje).", "Edge engine autofill (the Velivo database stays)." },
            { "Hasła zapisane przez silnik Edge – baza haseł Velivo zostaje.", "Passwords saved by the Edge engine – the Velivo password vault stays." },
            { "Karta sama odświeża stronę co wybrany czas.", "The tab reloads the page by itself at the chosen interval." },
            { "Zapisane zestawy kart do otwarcia jednym kliknięciem.", "Saved tab sets to open with one click." },
            { "Zapamiętuje wszystkie otwarte karty pod jedną nazwą.", "Remembers all open tabs under one name." },
            { "Nadpisuje zestaw kartami, które są teraz otwarte.", "Overwrites the set with the tabs open now." },
            { "Usuwa grupę – karty zostają otwarte.", "Removes the group – the tabs stay open." },
            { "Zapamiętuje karty tej grupy jako zestaw.", "Remembers the tabs of this group as a set." },
            { "Pobiera brakujące okładki i opisy filmów.", "Downloads missing covers and movie descriptions." },
            { "Velivo może grać na innych głośnikach niż reszta Windows.", "Velivo can play on different speakers than the rest of Windows." },
            { "Wkleja adres ze schowka i od razu go otwiera.", "Pastes the address from the clipboard and opens it right away." },
            { "Kolorowe karteczki, które zostają na pulpicie.", "Coloured sticky notes that stay on the desktop." },
            { "Wraca do ikony kanału z listy dostawcy.", "Returns to the channel icon from the provider's list." },
            { "Wraca do nazwy i ikony z listy dostawcy.", "Returns to the name and icon from the provider's list." },
            { "Wybierz obrazek z dysku.", "Choose a picture from disk." },
            { "Gdzie zapisywać nagrania telewizji.", "Where to save TV recordings." },
            { "Ile minut wcześniej zacząć nagrywanie (audycje często startują przed czasem).", "How many minutes earlier to start recording (shows often start early)." },
            { "Ile minut dłużej nagrywać (audycje często się przedłużają).", "How many minutes longer to record (shows often run late)." },
            { "Dowolna liczba minut przed i po audycji.", "Any number of minutes before and after the show." },
            { "Twoje playlisty i kolejka filmów do obejrzenia.", "Your playlists and the queue of movies to watch." },
            { "Velivo zapamięta odpowiedź i nie będzie pytać przy kolejnych filmach.", "Velivo will remember the answer and not ask again for the next movies." },
            { "Odtwarza film od początku.", "Plays the movie from the beginning." },
            { "Pokazuje tylko filmy, których jeszcze nie obejrzałeś.", "Shows only movies you have not watched yet." },
            { "Dopisuje zaznaczone filmy do playlisty.", "Adds the selected movies to the playlist." },
            { "Usuwa playlistę (filmy zostają na dysku).", "Deletes the playlist (the movies stay on disk)." },
            { "Gdy kanał się nagrywa, oglądasz go z nagrania – jedno połączenie z dostawcą, można pauzować i cofać.", "While a channel is recording you watch it from the recording – one provider connection, pause and rewind work." },
            { "Długa pauza zwalnia połączenie – nie blokuje limitu urządzeń u dostawcy.", "A long pause releases the connection – it does not block the provider's device limit." },
            { "Czyta napisy na głos.", "Reads subtitles aloud." },
            { "Pokazuje napisy na obrazie (lektor może czytać także przy ukrytych).", "Shows subtitles on the picture (the narrator can read them even when hidden)." },
            { "Zegar na obrazie filmu lub kanału.", "Clock on the movie or channel picture." },
            { "Otwiera ten kanał lub film w małym okienku obok obecnego.", "Opens this channel or movie in a small window next to the current one." },
            { "Kolejność listy – zapamiętana.", "List order – remembered." },
            { "Nie przełącza – film gra dalej.", "Does not switch – the movie keeps playing." },
            { "Wyłącznik zadziała, gdy skończy się obecny film.", "The timer triggers when the current movie ends." },
            { "Po wyłączniku czasowym wyłącza też komputer.", "After the sleep timer also shuts down the computer." },
            { "Odwołuje zaplanowane zatrzymanie.", "Cancels the scheduled stop." },
            { "Pasek informacji pojawia się, gdy ruszysz myszą.", "The info bar appears when you move the mouse." },
            { "Po ilu sekundach pasek informacji znika.", "After how many seconds the info bar disappears." },
            { "Jak bardzo prześwituje tło paska informacji.", "How transparent the info bar background is." },
            { "Wielkość napisów paska informacji.", "Size of the info bar text." },
            { "Napisy z pliku .srt / .txt z dysku.", "Subtitles from an .srt / .txt file on disk." },
            { "Usuwa pobrany plik napisów tego filmu.", "Deletes the downloaded subtitle file of this movie." },
            { "Gdy napisy spieszą się albo spóźniają – przesuwasz je w trakcie oglądania.", "When subtitles are early or late – shift them while watching." },
            { "Dla amplitunera / kina domowego – dźwięk wielokanałowy idzie bez zmian.", "For an AV receiver / home cinema – multichannel sound passes through unchanged." },
            { "Jak bardzo prześwituje lista kanałów i filmów.", "How transparent the channel and movie list is." },
            { "Ostatnie filmy – klik wznawia od miejsca, w którym skończyłeś.", "Recent movies – a click resumes where you left off." },
            { "Ostatnio oglądane kanały.", "Recently watched channels." },
            { "Zatrzymuje odtwarzanie po wybranym czasie albo po filmie.", "Stops playback after the chosen time or after the movie." },
            { "Małe okienko z drugim kanałem lub filmem.", "A small window with a second channel or movie." },
            { "Kanały telewizji.", "TV channels." },
            { "Twoja biblioteka filmów.", "Your movie library." },
            { "Wszystkie grupy kanałów z listy.", "All channel groups from the list." },
            { "Kończy nagrywanie – nagranie zostaje na dysku.", "Ends the recording – the recording stays on disk." },
            { "Powiększa stronę – zapamiętane dla tej strony.", "Zooms in the page – remembered for this site." },
            { "Pomniejsza stronę – zapamiętane dla tej strony.", "Zooms out the page – remembered for this site." },
            { "Wraca do domyślnej wielkości strony.", "Returns to the default page size." },
            { "Wznawia film od miejsca, w którym skończyłeś.", "Resumes the movie where you left off." },
            { "Nagrywa dłużej o wybrany czas.", "Records longer by the chosen time." },
            { "Ile minut przed i po audycji nagrywać – audycje często zaczynają się wcześniej i kończą później.", "How many minutes before and after the show to record – shows often start earlier and end later." },
        };

        static string Tr(string tip) { return L.En && En.TryGetValue(tip, out var en) ? en : tip; }

        static Dictionary<string, string> _exact;
        static List<(string Prefix, string Tip)> _prefixes;

        static void Build()
        {
            if (_exact != null) return;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (t, tip) in Exact) { d[t] = Tr(tip); d[L.T(t)] = Tr(tip); }
            _exact = d;
            _prefixes = Prefixes.SelectMany(p => new[] { (p.Prefix, Tr(p.Tip)), (L.T(p.Prefix), Tr(p.Tip)) }).ToList();
        }

        internal static string For(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            Build();
            if (_exact.TryGetValue(text, out var tip)) return tip;
            foreach (var (p, t) in _prefixes) if (text.StartsWith(p, StringComparison.Ordinal)) return t;
            return null;
        }

        // wpinane raz przy starcie aplikacji: przy pokazaniu kontrolki bez podpowiedzi - podpowiedz ze slownika (jesli jest)
        internal static void Register()
        {
            EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
            EventManager.RegisterClassHandler(typeof(MenuItem), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        }

        static void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var fe = sender as FrameworkElement;
                if (fe == null || fe.ToolTip != null) return;
                var text = (fe as MenuItem)?.Header as string ?? (fe as ContentControl)?.Content as string;
                var tip = For(text);
                if (tip != null) fe.ToolTip = tip;
            }
            catch (Exception) { }
        }
    }
}
