# Velivo 1.22 – Instrukcja szczegółowa (dodatek techniczny)

Dokument opisuje każdą funkcję Velivo 1.22 punkt po punkcie: do czego służy, gdzie ją znaleźć, jak jej użyć krok po kroku, jak działa technicznie i jakie ma ograniczenia. Treść powstała na podstawie kodu źródłowego (`src/`, stan `main` 9778aac, program z instalatora 1f08f31). Tam, gdzie funkcja działa tylko częściowo albo czegoś nie ma, jest to napisane wprost.

Konwencje: **Gdzie** – miejsce w interfejsie. **Kroki** – kolejność czynności. **Technicznie** – mechanizm, pliki, parametry. **Uwagi** – ograniczenia i zależności. Nazwy plików danych odnoszą się do folderu danych profilu: `%LOCALAPPDATA%\Przegladarka` (profil domyślny) albo `%LOCALAPPDATA%\Przegladarka\Profiles\<nazwa>`.

# 1. Architektura i pliki danych

## 1.1 Silnik i powłoka
- Velivo to program WPF (.NET 10, Windows 10 1809+ x64) z silnikiem stron **Microsoft Edge WebView2**. Strony renderuje ten sam silnik co Edge; okno, karty, menu, prywatność, pobieranie i synchronizacja to kod Velivo.
- Profil silnika: `Profil\` w folderze danych. Tryb bankowy ma osobny profil silnika.
- Jedno okno programu: drugie uruchomienie przekazuje adresy do działającego okna przez nazwany potok (nazwa zależna od folderu danych) i kończy się. Linki z innych programów otwierają się jako nowe karty.
- Velivo przedstawia się stronom jak Microsoft Edge: z nagłówków Client Hints i z `navigator.userAgentData` usuwana jest marka „Microsoft Edge WebView2” (Google blokuje logowanie w „przeglądarkach wbudowanych”). Strony dodatków (`chrome-extension://`) widzą prawdziwą markę.

## 1.2 Pliki w folderze danych
| Plik / folder | Zawartość |
|---|---|
| `ustawienia.txt` | ustawienia (klucz=wartość) |
| `lan-sync-key.dpapi` | klucz LAN zaszyfrowany DPAPI (CurrentUser) |
| `zakladki.txt`, `zakladki-usuniete.txt` | zakładki (adres TAB tytuł), lista usuniętych (90 dni) |
| `historia.txt`, `historia-usuniete.txt`, `historia-wyczyszczona.txt` | historia (czas, adres, tytuł, sesja), usunięcia do synchronizacji |
| `sesja.txt`, `sesja.txt.aktywna`, `karty-przypiete.txt` | otwarte karty, aktywna karta, przypięte |
| `zestawy-kart.json` | zapisane zestawy kart |
| `hasla.vault` | lokalne hasła (DPAPI) |
| `autouzupelnianie.vault` | adresy, karty, konta (DPAPI) |
| `bank*.json` | tryb bankowy (pola zaszyfrowane AES-GCM) |
| `prywatnosc.txt`, `prywatnosc-log.txt` | reguły domen, dziennik blokad |
| `ochrona-dozwolone.txt` | strony uznane za prawdziwe po ostrzeżeniu o podróbce |
| `ciasteczka-wyjatki.txt` | strony bez odrzucania banerów |
| `elementy.txt` | ręcznie zablokowane elementy (domena + selektor CSS) |
| `powiekszenie.txt`, `tryb-stron.txt`, `widok-telefonu.txt` | powiększenie, tryb ciemny/nocny, wersja telefonu – per strona |
| `pamiec-stron.jsonl` | lokalny indeks „Gdzie ja to czytałem?” |
| `pobrane.json` | lista pobrań |
| `skroty-wyszukiwania.txt` | skróty wyszukiwania |
| `Filtry\` | pobrane listy filtrów |
| `Dodatki\` | dodatki, uBOL, lista do synchronizacji |
| `Miniatury stron\` | miniatury Szybkiego Dostępu |
| `narzedzia\` | yt-dlp, FFmpeg |
| `sync-device-id.txt`, `sync-peers.json`, `lan-zmiana.txt` | synchronizacja |
| `bledy.log`, `debug.log` | dziennik błędów, dziennik diagnostyczny (`VELIVO_DEBUG=1`) |

## 1.3 Parametry uruchomienia i zmienne
- `--profil <nazwa>` – start z wybranym profilem użytkownika.
- `PRZEGLADARKA_DANE=<folder>` – osobny folder danych (np. do testów); wtedy także osobne jedno-okno.
- `VELIVO_DEBUG=1` – zapis zdarzeń zamykania okna do `debug.log`.
- Siatka bezpieczeństwa: nieobsłużony wyjątek nie zamyka programu; trafia do `bledy.log`, a komunikat pojawia się najwyżej raz na 30 s dla tego samego błędu.

# 2. Okno, pasek narzędzi i nawigacja

## 2.1 Pasek narzędzi
**Gdzie:** górny pasek. Przyciski (od lewej): nowa karta prywatna, nowa karta, Wstecz, Dalej, Odśwież, strona startowa, pasek adresu, profil, zakładka (gwiazdka), tarcza prywatności, tryb ciemny, czytanie na głos z prędkością, tryb czytania, zrzut ekranu, dodatki, pobrane, historia, ustawienia, „Więcej narzędzi”.
- **Kompaktowy pasek:** przy wąskim oknie etykiety się zmniejszają, a część przycisków trafia do menu „…”. Ustawienie „Zawsze kompaktowy pasek” wymusza ten układ.
- **Kółko myszy na przyciskach** zmienia wartości bez klikania: na przycisku trybu nocnego – natężenie, na przycisku powiększenia – procent, w okienku „Film na wierzchu” – przezroczystość.

## 2.2 Skróty klawiszowe
| Skrót | Działanie |
|---|---|
| Ctrl+T | nowa karta |
| Ctrl+Shift+N | nowa karta prywatna |
| Ctrl+W | zamknij kartę |
| Ctrl+Shift+T | przywróć zamkniętą kartę |
| Ctrl+Shift+A | szukaj w kartach |
| Ctrl+D | dodaj/usuń zakładkę |
| Ctrl+H | historia |
| Ctrl+J | pobrane pliki |
| Ctrl+Shift+F | „Gdzie ja to czytałem?” |
| Ctrl+Shift+U | czytaj stronę na głos / pauza |
| Alt+← / Alt+→ | wstecz / dalej |
| F5 | odśwież |
| Ctrl + / Ctrl − / Ctrl 0, Ctrl+kółko | powiększenie |

## 2.3 Gesty myszy
**Gdzie:** Ustawienia → Wyszukiwanie i start → „Gesty myszy”.
**Kroki:** przytrzymaj prawy przycisk i przesuń: ← wstecz, → dalej, ↑ nowa karta, ↓ zamknij kartę, ↓→ odśwież. Zwykły prawy klik otwiera menu.
**Technicznie:** skrypt strony rozpoznaje ruch i wysyła komunikat do programu tylko z losowym znacznikiem uruchomienia; strona nie może podrobić gestu.

## 2.4 Menu prawego przycisku na stronie
Pozycje (zależnie od kontekstu): wyszukaj zaznaczony tekst, przetłumacz zaznaczenie na polski, przetłumacz stronę na polski/angielski (Tłumacz Google), czytaj zaznaczenie na głos, „Czytaj od tego miejsca”, tryb czytania i streszczenie, „Gdzie ja to czytałem?”, Film na wierzchu, Obraz w obrazie, Pobierz film, Blokuj element, Przywróć zablokowane elementy, Odrzucaj / Nie odrzucaj banerów ciasteczek na tej stronie, Pokaż wersję telefonu / Wróć do wersji komputerowej, Zrzut ekranu (widoczna część / cała strona), Narzędzia Velivo (Prywatność i antyfingerprinting, Wykryj media, Menedżer pobrań, Diagnostyka LAN, Dodatki, Historia, Szukaj w kartach, przełączenie profilu).

# 3. Karty i okna

## 3.1 Karty – podstawy
**Gdzie:** pasek kart; prawy klik na karcie.
- Menu karty: Odśwież, Duplikuj kartę, Przypnij/Odepnij, Wycisz/Włącz dźwięk, Odświeżaj automatycznie, Dodaj do grupy, Zestawy kart, Wyślij do…, Otwórz jako prywatną, Zamknij, Zamknij inne, Zamknij karty po prawej, Przywróć zamkniętą (Ctrl+Shift+T).
- **Linki w tej samej karcie** (Ustawienia → Karty): linki z `target=_blank` otwierają się w bieżącej karcie, więc działa Wstecz. Ctrl+klik i środkowy klik nadal otwierają nową kartę.

## 3.2 Przypięte karty
**Kroki:** prawy klik na karcie → „Przypnij kartę”. Karta przesuwa się na początek paska, traci krzyżyk i wraca po ponownym uruchomieniu.
**Technicznie:** `karty-przypiete.txt`; synchronizowane przez LAN (brakujące przypięte karty są otwierane i przypinane na drugim komputerze).

## 3.3 Grupy kart
**Kroki:**
1. Prawy klik na karcie → „Dodaj do grupy” → „Nowa grupa…” → wpisz nazwę.
2. Kolejne karty: „Dodaj do grupy” → wybierz istniejącą.
3. Klik w etykietę grupy zwija/rozwija grupę. Prawy klik na etykiecie: „Zmień nazwę grupy…”, „Kolor” (8 kolorów: niebieski, zielony, pomarańczowy, fioletowy, czerwony, morski, różowy, szary), „Rozgrupuj (karty zostają)”, „Zamknij wszystkie karty grupy”.
4. „Usuń z grupy” w menu karty.
**Technicznie:** grupy zapisują się razem z sesją (pozycja karty, nazwa, kolor, stan zwinięcia) i wracają po restarcie.

## 3.4 Zestawy kart
**Kroki:** menu karty → „Zestawy kart” → „Zapisz otwarte karty jako zestaw…” → nazwa (np. Praca). Później: „Zestawy kart” → nazwa → „Otwórz wszystkie karty” albo „Zastąp obecnymi kartami”; „Usuń zestaw”.
**Technicznie:** `zestawy-kart.json`.

## 3.5 Automatyczne odświeżanie
**Kroki:** prawy klik na karcie → „Odświeżaj automatycznie” → Wyłączone / 1 / 5 / 15 / 30 min. Na karcie pojawia się znaczek z dymkiem „Odświeżanie co N min”.
**Uwagi:** licznik działa dla każdej karty osobno i nie jest zapisywany po restarcie.

## 3.6 Szukanie w kartach
**Kroki:** Ctrl+Shift+A → wpisz fragment tytułu lub adresu → wybierz kartę.

## 3.7 Przywracanie sesji i zamkniętych kart
- Ustawienia → Karty → „Po uruchomieniu przywracaj karty z poprzedniej sesji”. Karty prywatne nie są nigdy zapisywane. Przy „Czyść dane przy zamknięciu” sesja też nie jest zapisywana.
- Ctrl+Shift+T przywraca ostatnio zamknięte karty (stos w pamięci).

## 3.8 Karty prywatne
**Kroki:** Ctrl+Shift+N albo przycisk karty prywatnej; z menu karty „Otwórz jako prywatną (incognito)”.
**Technicznie:** osobny, izolowany magazyn WebView2 (InPrivate): bez historii, sesji, pamięci treści; pomijane przez „Gdzie ja to czytałem?”.

## 3.9 Wyciszanie karty
Prawy klik na karcie → „Wycisz kartę” / „Włącz dźwięk karty”. Karta z dźwiękiem ma znaczek głośnika.

## 3.10 Powiększenie
- Domyślne dla wszystkich stron: Ustawienia → Wygląd. Dla pojedynczej strony: Ctrl+kółko, Ctrl ±, Ctrl 0 albo kółko na przycisku procentów.
- **Technicznie:** `powiekszenie.txt` (host → procent); zmiana przenosi się na inne karty z tą samą stroną.

## 3.11 Wersja telefonu strony
**Kroki:** prawy klik na stronie → „Pokaż wersję telefonu tej strony”. Powrót: „Wróć do wersji komputerowej tej strony”.
**Technicznie:** karta przedstawia się jako telefon z Androidem (User-Agent i Client Hints, które sprawdza m.in. Google) przy każdej nawigacji; wybór zapamiętany per strona w `widok-telefonu.txt`.

## 3.12 Zrzut ekranu
**Kroki:** przycisk aparatu albo prawy klik → „Zrzut ekranu” → „Widoczna część strony” lub „Cała strona (z przewijaniem)”.
**Technicznie:** zapis PNG w `Obrazy\Zrzuty Velivo` i kopia do schowka; dymek z przyciskami „Otwórz” i „Pokaż w folderze”. Na stronach banków i płatności zrzut jest niemożliwy (patrz 7.8).

## 3.13 Efekty wejścia stron
**Gdzie:** Ustawienia → Wygląd i czytelność → „Efekt wejścia treści” i „Szybkość efektu wejścia”.
- Efekty: **Wyostrzenie** (domyślne – treść wyłania się z rozmycia), **Z ciemności (kinowe)**, **Delikatne przyciemnienie**, **Brak**.
- Szybkość: suwak 0–5000 ms co 100 ms; 0 = automatycznie.
**Technicznie:** efekt działa na całe okno strony (także tło i przypięte menu); po kliknięciu linku strona lekko się przyciemnia, a nowa treść pojawia się z efektem.

## 3.14 Szybsze otwieranie stron
**Gdzie:** Ustawienia → Szybkość wczytywania stron.
**Technicznie:** najechanie na link tej samej strony rozpoczyna pobieranie w tle; Velivo łączy się z wyprzedzeniem z serwerami widocznych linków. Wyłączone w kartach prywatnych i bankowych. Przy limicie danych lepiej wyłączyć.

# 4. Zakładki i historia

## 4.1 Zakładki
- Ctrl+D albo gwiazdka – dodaj/usuń zakładkę bieżącej strony. Przycisk „Wszystkie zakładki” – lista; prawy klik na zakładce: zmień nazwę, usuń.
- **Technicznie:** `zakladki.txt`; usunięcia trafiają z czasem do `zakladki-usuniete.txt` (90 dni), żeby synchronizacja LAN usunęła je też na drugim komputerze. Ponowne dodanie kasuje wpis z listy usuniętych.

## 4.2 Historia (Ctrl+H)
**Układ:** dzień → sesja (jedno uruchomienie przeglądarki, „● Bieżąca sesja”) → strony.
**Kroki:** pole „Szukaj w tytułach i adresach”; dwuklik otwiera stronę; prawy klik: „Usuń z historii”, „Usuń znalezione wpisy z tego dnia”, „Usuń cały dzień z historii”, „Otwórz wszystkie w nowych kartach”, „Usuń sesję z historii”; przycisk „Wyczyść całą historię”.
**Technicznie:** `historia.txt` (czas TAB adres TAB tytuł TAB sesja); stare wpisy bez numeru sesji są dzielone po 30 min przerwy. Zapisywanie można wyłączyć: Ustawienia → Prywatność → „Zapisuj historię przeglądania”.

# 5. Wygląd i personalizacja

## 5.1 Styl i motywy
- Ustawienia → Wygląd → „Styl wyglądu”: **Nowoczesny** (jednokolorowe ikony Windows 11 Segoe Fluent, jeden niebieski akcent, kolor tylko przy najechaniu lub włączeniu) albo **Kolorowy** (kolorowe przyciski i emoji).
- „Motyw przeglądarki”: Jasny, Grafit, Granat, Nocny fiolet, Las, Ocean, Zachód słońca, Czerń (OLED), Papier, Mgła.

## 5.2 Tryb ciemny i nocny stron
**Gdzie:** przycisk z księżycem; Ustawienia → Wygląd.
**Kroki:** klik przełącza jasny → ciemny → nocny. W trybie nocnym kółko myszy na przycisku ustawia natężenie 5–100%.
**Technicznie:** tryb ciemny używa ciemnego motywu silnika: strony z własnym ciemnym wyglądem przełączają się na niego, pozostałe są przyciemniane, a zdjęcia zachowują kolory. Tryb nocny to ciepła, półprzezroczysta warstwa nad stroną, która nie przechwytuje kliknięć. Tryb jest zapamiętywany osobno dla każdej strony (`tryb-stron.txt`). Gdy silnik wystartował w innym trybie, Velivo poprawia stronę CSS-em albo proponuje ponowne uruchomienie (karty wracają).

## 5.3 Język
Ustawienia → Wygląd → „Język interfejsu”: Automatycznie (z instalatora / Windows), polski, angielski. Zmiana działa po ponownym uruchomieniu. Tłumaczone są wszystkie okna, także zbudowane w XAML, oraz Szybki Dostęp.

## 5.4 Wyszukiwarka i skróty wyszukiwania
- Ustawienia → Wyszukiwanie i start → „Wyszukiwarka w pasku adresu” (m.in. DuckDuckGo, Startpage – domyślna, Brave Search…).
- „Skróty wyszukiwania…”: słowo + spacja + zapytanie, np. `yt koty` szuka na YouTube. Własne skróty dodajesz w okienku; zapis w `skroty-wyszukiwania.txt`.

## 5.5 Strona startowa i nowa karta
Ustawienia → Wyszukiwanie i start → „Strona startowa” oraz „Szybki Dostęp jako strona nowej karty”.

## 5.6 Profile użytkowników Velivo
**Gdzie:** przycisk profilu na pasku; Ustawienia → Profile → „Zarządzaj użytkownikami/profilami…”.
**Kroki:** „Dodaj użytkownika/profil…” → nazwa (litery, cyfry, `-`, `_`); „Ikona profilu” → „Zapisz ikonkę”; „Przełącz na zaznaczony” (Velivo uruchamia się ponownie na tym profilu); „Usuń zaznaczony profil” (nie można usunąć aktywnego ani domyślnego).
**Technicznie:** każdy profil ma osobny folder danych `Profiles\<nazwa>`: logowania, historię, hasła, ustawienia i Szybki Dostęp. Aktywny profil zapisany w `active-profile.txt`.

## 5.7 Dźwięk – głośniki Velivo
**Gdzie:** Ustawienia → Bezpieczeństwo i pobieranie → „Głośniki Velivo”.
- Wybór konkretnego wyjścia zamiast „Domyślne wyjście Windows”.
- **„Nie gub dźwięku”:** co 2 s Velivo sprawdza, czy wybrane wyjście jest wolne. Gdy program muzyczny (Ableton, Cubase – ASIO/WASAPI exclusive) zajmie kartę, dźwięk Velivo idzie na inne aktywne wyjście i wraca po jej zwolnieniu.
- „🔊 Mikser głośności Windows…” otwiera ustawienia Windows, gdzie przy „Velivo” / „Microsoft Edge WebView2” można przypiąć głośniki na stałe.
**Technicznie:** MMDevice API oraz nieudokumentowany interfejs `IAudioPolicyConfigFactory` (ten sam, którego używa EarTrumpet) do przypisania wyjścia aplikacji.

## 5.8 Domyślna przeglądarka
Ustawienia → „Ustaw Velivo jako domyślną przeglądarkę…”. Velivo rejestruje się w Windows (klasa `VelivoHTML`, `StartMenuInternet\Velivo`) i otwiera „Aplikacje domyślne”. Windows nie pozwala programowi ustawić się samemu – trzeba kliknąć „Ustaw domyślne”.

## 5.9 Praca w zasobniku
Ustawienia → „Po zamknięciu okna zostań w zasobniku”. Zamknięcie okna chowa Velivo do ikony przy zegarze. Strony w tle są pauzowane (film w okienku „Film na wierzchu” gra dalej). Synchronizacja LAN działa nadal. Prawy klik na ikonie: Otwórz Velivo, Synchronizuj teraz, Zamknij Velivo całkowicie. Ikona pulsuje przy synchronizacji.

# 6. Szybki Dostęp (strona nowej karty)

Szybki Dostęp to wbudowane rozszerzenie (`chrome-extension://…/kod/newtab.html`).
## 6.1 Skróty i grupy
**Kroki:** „+ Skrót” → adres, nazwa, opcjonalnie obrazek („Wybierz obrazek…” z pliku, wklejenie obrazka, „Usuń własną”) → Zapisz. „+ Grupa” – nowa grupa (np. Start, Finanse, Muzyka). Skrót można edytować i usuwać ikonami na kafelku. Z każdej strony: prawy klik → „Dodaj do Szybkiego Dostępu”.
## 6.2 Miniatury stron
Menu ⋮ → „Miniatury stron”. Rozszerzenie w WebView2 nie może zrobić zrzutu karty, więc robi to Velivo: niewidoczne okno poza ekranem otwiera po kolei strony bez historii, dźwięku, okien i pobierań i zapisuje miniatury w `Miniatury stron\`.
## 6.3 Profile Szybkiego Dostępu i PIN
Lista profili → „+ Nowy profil...”. Menu ⋮ → „PIN tego profilu” – PIN chroniony solą i skrótem. Wejście do profilu z PIN-em wymaga kodu, dodanie strony do takiego profilu także. „Usuń ten profil”.
## 6.4 Kosz
Usunięte skróty trafiają do kosza: menu ⋮ → „Kosz…” → „Przywróć” albo opróżnij.
## 6.5 Wygląd
Motywy tła i rozmiar kafelków.
## 6.6 Kopia, folder i import
- Kopia na dysk („Kopia Szybkiego Dostępu”) i folder danych obsługiwany przez most (SejfMost) – „Folder rozszerzenia”.
- Import zakładek z pliku JSON lub HTML. Limit kopii: ok. 100 KB, czyli około tysiąca skrótów.
- „Nazwa tego komputera” – do rozróżnienia kopii.
- W Chrome to samo rozszerzenie może synchronizować się z kontem Google („Synchronizacja z kontem Google”, stan „Sync OK”); w Velivo synchronizuje go LAN (patrz 10.8).

# 7. Prywatność i bezpieczeństwo

## 7.1 Blokowanie reklam Velivo
**Gdzie:** tarcza na pasku; przycisk „Włącz/wyłącz AdBlock”; Ustawienia → Blokowanie reklam.
**Technicznie:** filtr domen w `HashSet` (sprawdzanie po kolejnych członach hosta) + lista fragmentów adresu. Z list w formacie EasyList brane są reguły blokujące całe domeny (`||domena^`). Własne reguły: `filters.txt` w folderze danych. Zablokowane żądanie dostaje odpowiedź 403.

## 7.2 Pełne listy filtrów
EasyList, EasyPrivacy i polska lista (ok. 97 tys. reguł). Pobierane w tle i odświeżane co 4 dni; „Aktualizuj listy teraz” pobiera od razu. Plik jest podmieniany dopiero po udanym pobraniu. Filtr budowany w tle (kilkaset ms) i podmieniany w całości. Przy błędzie: „Nie udało się pobrać N list(y) – używam poprzednich”.

## 7.3 uBlock Origin Lite
Wbudowany (GPL-3.0), włączony domyślnie, można wyłączyć; „Ustawienia uBlock Origin Lite…”. Instalator zawiera najnowsze wydanie. Raz w tygodniu Velivo sprawdza nowe wydanie na GitHubie (`uBlockOrigin/uBOL-home`) i instaluje je **tylko gdy suma SHA-256 zgadza się z digestem podanym przez API GitHuba**. Blokady uBOL (`net::ERR_BLOCKED_BY_CLIENT`) są liczone na tarczy bez podwójnego liczenia z blokadą Velivo.

## 7.4 Tarcza i dziennik blokad
- Tarcza pokazuje liczbę zablokowanych elementów na stronie i listę „Zablokowane na tej stronie”.
- Panel „Prywatność i antyfingerprinting” → „Co zostało zablokowane i dlaczego”: godzina, domena, powód (tracker – reguła domeny / AdBlock, cookies usunięte regułą…), pełny adres po zaznaczeniu wpisu; „Wyczyść panel blokad”. Zapis w `prywatnosc-log.txt`.

## 7.5 Ochrona przed śledzeniem
Ustawienia → Prywatność → „Ochrona przed śledzeniem”:
- **Zrównoważona** (zalecana): znane trackery blokowane, osadzone treści (wpisy z X, filmy) działają.
- **Ścisła:** blokuje też osadzone treści serwisów społecznościowych. Na zaufanych domenach działa jak zrównoważona, chyba że dla domeny zaznaczysz „Wymuś blokowanie trackerów”.
**Technicznie:** poziom ochrony silnika jest wspólny dla wszystkich kart, więc Velivo ustawia go według aktywnej strony.

## 7.6 Reguły prywatności dla domen
**Gdzie:** tarcza → „Panel prywatności i antyfingerprinting”; Ustawienia → Prywatność per-strona.
**Kroki:** wpisz domenę (można wkleić cały adres – zostaje domena) → zaznacz: „Blokuj JavaScript dla domeny”, „Nie wysyłaj cookies dla domeny”, „Wymuś blokowanie trackerów dla domeny”, „Automatycznie czyść dane po wejściu na domenę” → „Zapisz regułę”. „Usuń regułę”. Lista „Zaufane domeny”: „✔ Zaznaczoną domenę do zaufanych” (z dziennika blokad).
**Technicznie:** `prywatnosc.txt`; reguły dotyczą tylko stron www (nie dodatków). Synchronizowane przez LAN.

## 7.7 Paragon prywatności i fingerprinting
**Gdzie:** klik w tarczę (Ustawienia → „Paragon prywatności na tarczy”).
- Pokazuje: z iloma zewnętrznymi firmami i w ilu krajach łączyła się strona, brokerów danych, najczęstsze firmy i **próby rozpoznania komputera**: canvas (odczyt niewidocznego płótna < 250 000 px), WebGL (odczyt producenta/modelu karty graficznej – parametry 37445/37446), audio (OfflineAudioContext).
- **Uwagi:** Velivo **wykrywa i raportuje** fingerprinting, ale **nie zmienia odczytywanych wartości** (nie „farbluje” jak Brave i nie ujednolica jak Firefox). Ochronę dają blokada trackerów z list i ukrycie marki WebView2.

## 7.8 Bezpieczne płatności (blokada nagrywania ekranu)
Ustawienia → Bezpieczeństwo → „Bezpieczne płatności”. Na stronach banków i płatności (PayPal, Monzo, Revolut, Barclays, HSBC, Lloyds, NatWest, Santander, PKO, mBank, Stripe i inne) okno Velivo dostaje `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` – programy nagrywające i zrzuty ekranu widzą puste pole. Pojawia się komunikat 🛡. Na tych stronach nie zrobisz zrzutu ekranu.

## 7.9 Wykrywanie podróbek (bez internetu)
Ustawienia → „Wykrywaj fałszywe strony banków, sklepów i portali”. Przed otwarciem strony Velivo sprawdza:
1. **Markę na obcej domenie** – lista chronionych marek z ich prawdziwymi domenami (PayPal, eBay, Amazon, Facebook, Instagram, WhatsApp, Google/Gmail, Microsoft/Outlook, Apple/iCloud, Twitter/X, Netflix, Spotify, banki i inne); np. `paypal-secure-login.com`, `ebay.co.uk.konto.top`.
2. **Literówki i podmienione znaki:** `paypa1.com`, `rnicrosoft.com`, `amaz0n.com`.
3. **Adresy `xn--` (punycode)** z literami z innych alfabetów (homoglify).
4. **Podróbki stron, do których masz zapisane hasła.**
Pojawia się ostrzeżenie „Uwaga – to może być fałszywa strona!” z domyślną odpowiedzią **Nie**. „Tak” zapamiętuje stronę jako prawdziwą (`ochrona-dozwolone.txt`).

## 7.10 HTTPS, SmartScreen, wyskakujące okna
- „Zawsze szyfrowane połączenie (HTTPS)” – strony bez szyfrowania otwierają się dopiero po ostrzeżeniu.
- „Ostrzegaj przed niebezpiecznymi stronami i plikami (SmartScreen)” – adresy sprawdzane w Microsoft.
- „Blokuj wyskakujące okna otwierane bez kliknięcia”.

## 7.11 „Nie śledź” i banery cookies
- „Wysyłaj sygnały Nie śledź” – nagłówki DNT i Global Privacy Control.
- „Automatycznie odrzucaj banery z ciasteczkami (RODO)” – Velivo klika „Odrzuć” / „Tylko niezbędne”; gdy takiego przycisku nie ma, nic nie klika (nigdy „Akceptuj”). Wyjątek: prawy klik → „Nie odrzucaj banerów ciasteczek na tej stronie” (`ciasteczka-wyjatki.txt`).

## 7.12 Blokowanie elementów strony
**Kroki:** prawy klik → „🚫 Blokuj element (reklamę)…” → najedź – element jest podświetlany; kółko lub strzałki zmieniają zakres (element nadrzędny/podrzędny); klik lub Enter blokuje; Esc anuluje. Cofnięcie: prawy klik → „Przywróć zablokowane elementy na tej stronie”.
**Technicznie:** reguła = domena + selektor CSS w `elementy.txt`; element dostaje `display:none` przy każdym wejściu na stronę.

## 7.13 Wykrywacz sztuczek presji w sklepach
Ustawienia → „Ostrzegaj przed sztuczkami presji w sklepach”. Wykrywa fałszywe liczniki czasu, „ostatnie sztuki”, „X osób ogląda”, zaznaczone z góry dodatki (Velivo je **odznacza**) i ukryte opłaty; komunikat pojawia się nad stroną.

## 7.14 Kanał strona ↔ program
Strony nie mają dostępu do `chrome.webview` (ukrywany przy tworzeniu dokumentu). Skrypty Velivo zapamiętują kanał przed ukryciem i podpisują wiadomości losowym znacznikiem tego uruchomienia; wiadomość bez znacznika jest ignorowana. Szybki Dostęp przyjmuje polecenia tylko ze swojego adresu.

## 7.15 Pobrane pliki – znacznik „plik z internetu”
Każdy plik pobrany przez menedżer Velivo, yt-dlp lub silnik dostaje strumień `Zone.Identifier` z `ZoneId=3` (Internet) i `HostUrl=about:internet` (bez adresu strony – prywatność). SmartScreen ostrzega przy uruchomieniu, a Office otwiera dokument w widoku chronionym. Istniejący znacznik nie jest nadpisywany; na dyskach FAT32/exFAT znacznika nie da się zapisać.

## 7.16 Czyszczenie danych
- „Czyść dane przy zamknięciu (historia i pamięć podręczna)” – nie wylogowuje kont.
- „Wyczyść dane przeglądania teraz…”: Historia przeglądania, Pamięć podręczna, Historia pobrań, Cookies i aktywne sesje (wyloguje konta), Dane formularzy i kart zapisane przez silnik, Zapisane hasła w silniku.

## 7.17 Odporność programu
- Błąd w programie nie zamyka przeglądarki (patrz 1.3).
- Ochrona przed zamknięciem całego okna przez wewnętrzną obsługę kontrolki WebView2 (`window.close` w stronie zamyka tylko kartę).

# 8. Hasła, Sejf i autouzupełnianie

## 8.1 Menedżer haseł Velivo
**Gdzie:** Ustawienia → Prywatność → „Menedżer haseł lokalnych…”.
**Kroki:** „Szukaj po domenie, loginie lub nazwie”; „Dodaj” (domena, użytkownik, hasło, nazwa, notatki; „Generuj mocne hasło”, „Wklej ze schowka”, „Pokaż/ukryj hasło”); „Edytuj”; „Kopiuj login”, „Kopiuj hasło”; „Usuń zaznaczone”; „Usuń wszystkie zapisane hasła”. Kolumny: Nazwa, Domena, Użytkownik, Źródło, Zmieniono.
**Technicznie:** `hasla.vault` zaszyfrowany DPAPI (CurrentUser) z własną entropią. Generator: `RandomNumberGenerator`, co najmniej po jednej małej, wielkiej literze, cyfrze i symbolu, potem tasowanie Fisher–Yates.

## 8.2 Zapisywanie i wpisywanie haseł
- „Proponuj zapisywanie haseł”: po zalogowaniu pytanie „Wykryto hasło na stronie … Dodać do bazy Velivo?” albo „Wykryto nowe hasło dla … Zaktualizować wpis?”.
- Wpisywanie: „Wpisz zapisany login i hasło z Velivo” / „Wybierz konto do wpisania” / „Inne konto z bazy Velivo…”.
- **Bezpieczeństwo:** wpis dopasowywany **tylko do prawdziwej domeny** – bez dopasowania po nazwie, więc strona-podróbka nie dostanie hasła.

## 8.3 Import i eksport
- „Import haseł CSV…”: Chrome, Edge, Firefox, KeePass, KeePassXC (wpisy z kosza są pomijane); obsługa pól w cudzysłowie z nowymi liniami.
- „Eksport haseł CSV…” – **plik jawny**, przechowuj ostrożnie.
- „🌐 Z Chrome / Edge / Brave / Opery” (Ustawienia → Import): zakładki czytane wprost z plików tych przeglądarek (także Vivaldi); hasła – Velivo otwiera stronę haseł danej przeglądarki do eksportu CSV.

## 8.4 Sejf (zewnętrzny menedżer)
Ustawienia → „Loginy z Sejfu: kluczyk na pasku na stronach logowania”. Gdy strona ma pole hasła, Velivo pyta Sejf (program `SejfMost.exe`, zarejestrowany host natywny) o loginy dla **domeny z prawdziwego adresu karty**. Kliknięcie kluczyka wypełnia formularz; przed wypełnieniem Velivo sprawdza, że karta nadal jest na tej stronie. Sejf może poprosić o odblokowanie (okno Sejfu, limit 1 minuta). Z okienka rozszerzenia można też zapisać login w zaszyfrowanym sejfie. Gdy Sejfu nie ma: „(nie znaleziono Sejfu)”.

## 8.5 Autouzupełnianie formularzy
Ustawienia → „Autouzupełnianie formularzy (adresy i karty, lokalna szyfrowana baza)”.
- Dane: **adres** (imię, nazwisko, ulica, kod, miasto, telefon, e-mail), **karta** (bez CVC), **konto bankowe** (IBAN, numer konta, sort code).
- Klik w puste pole wypełnia cały formularz na każdej stronie https. Kartę i konto Velivo wpisuje dopiero po potwierdzeniu adresu strony.
- Zapis zawsze dopiero po pytaniu („Wykryto wypełniony formularz adresowy / dane karty płatniczej”).
- **Bezpieczeństwo:** wypełniane są tylko pola naprawdę widoczne dla człowieka; ukryte pola nigdy (strona nie wyciągnie danych ukrytym polem); działają tylko prawdziwe kliknięcia.
- „Pokaż zapisane dane…”, „Usuń zapisane karty”, „Usuń zapisane adresy”. Plik `autouzupelnianie.vault` (DPAPI).

# 9. Tryb bankowy

## 9.1 Czym jest
Osobny, odizolowany profil przeglądarki na banki, płatności i zakupy, z własną zaszyfrowaną bazą. Własne cookies i logowania, brak dodatków i historii. Zwykłe karty nic z niego nie widzą i odwrotnie. Karty bankowe są zielone i mają ikonę 🏦.

## 9.2 Pierwsze uruchomienie
1. Kliknij zielony przycisk 🏦 na pasku kart.
2. Ustaw hasło (min. 8 znaków) i powtórz je.
3. Opcjonalnie: zaznacz „Dodatkowo wymagaj klucza sprzętowego (YubiKey, Google Titan…)”, włóż klucz, kliknij „➕ Dodaj klucz (włóż go i dotknij)”. Windows poprosi o dotknięcie **dwa razy** (rejestracja + odczyt sekretu). Dodaj wszystkie klucze (zapasowy!) w tym samym okienku. Klucze NFC/Bluetooth Windows znajdzie sam po kliknięciu „Dodaj”.
4. Wybierz czas blokady po bezczynności i kliknij „Zapisz”.

## 9.3 Otwieranie i blokada
- Klucz w porcie → Velivo od razu prosi o dotknięcie; hasło niepotrzebne. Bez klucza → hasło.
- Automatyczna blokada po bezczynności (domyślnie 10 min, wybór 1–60 min) zamyka wszystkie okienka trybu. „🔒 Zablokuj teraz” – natychmiast.
- Po zamknięciu trybu cache i historia profilu bankowego są czyszczone (logowania i „zapamiętaj mnie” zostają).

## 9.4 Szyfrowanie (technicznie)
- Hasło → PBKDF2-HMAC-SHA256, **600 000 iteracji**, sól 16 B → klucz 32 B.
- Dane (karty, notatki, strony, dokumenty, konta, rachunki, loginy, historia płatności) szyfrowane **AES-256-GCM** (losowy nonce 12 B, tag 16 B) przez funkcję `SealList`.
- Klucz sprzętowy z rozszerzeniem **FIDO2 hmac-secret**: sekret z klucza przechodzi przez HKDF-SHA256 („velivo-bank-kk”) i staje się drugą częścią klucza (`SHA256(Kp + Kk)`). Bez fizycznego klucza samo hasło wtedy nie wystarcza. Podpis klucza: ES256; WebAuthn przez `webauthn.dll` Windows.
- Plik `bank.json` (profile: `bank-<nazwa>.json`), w pamięci RAM odczyt tylko po zmianie pliku.

## 9.5 Menu trybu bankowego (prawy klik na 🏦)
🔍 Szukaj w mojej bazie · 🔑 Moje loginy i hasła · 📄 Poufne dane (dokumenty) · 🏦 Rachunki bankowe · 🧾 Rachunki do opłacenia · 📊 Arkusz rachunków (w karcie) · 🏦 Moje banki · 🛒 Moje sklepy online · ➕ Dodaj tę stronę do Moich banków / sklepów · 💳 Moje karty · 💳 Wypełnij kartę na tej stronie · 📝 Moje notatki · 🔑 Wpisz login z notatki · 🔢 Wpisz wybrane znaki · 🧪 Skopiuj opis formularza logowania · ⚙ Ustawienia trybu bankowego · 📜 Dziennik otwarć · 💾 Kopia zapasowa bazy · 📂 Przywróć bazę z kopii · 👤 Profile bankowe / ➕ Nowy profil bankowy · 🔒 Zablokuj teraz · ❓ Instrukcja trybu bankowego · Zapomniałem hasła – wyczyść tryb bankowy.

## 9.6 Moje banki i Moje sklepy online
- Lista Twoich stron; klik otwiera stronę w trybie bankowym.
- Dodawanie: na otwartej karcie bankowej „➕ Dodaj tę stronę do Moich banków/sklepów” albo na liście „➕ Dodaj bank / sklep (nazwa i adres)…”.
- **Ochrona:** gdy otworzysz bank lub sklep z listy w zwykłej karcie, nad paskiem zadań pojawi się „Przełącz na tryb bankowy” / „Zostań tutaj” (rozpoznawanie po domenie głównej, także inne adresy tej samej firmy). Gdy przy otwartym trybie wejdziesz na stronę podobną do Twojego banku, pojawi się duże ostrzeżenie.

## 9.7 Dane logowania banku i wpisywanie wybranych znaków
- „✏ Dane logowania” (w Moich bankach): login / numer klienta, passcode / PIN, hasło, memorable information – osobno dla każdego banku. Wpisuj **pełne** hasło i passcode.
- „🔑 Wpisz login” – wypełnia login i hasło na stronie banku.
- „🔢 Wpisz wybrane znaki” – gdy bank prosi np. o 2., 5. i 9. znak (RBS, NatWest, Bank of Scotland, TSB, Lloyds, Halifax), Velivo samo odczytuje numery z formularza i wpisuje właściwe znaki, także w listach wyboru.
- „🧪 Skopiuj opis formularza logowania (bez Twoich danych)” – gdy wpisywanie nie zadziała, kopiuje sam opis pól do zgłoszenia.

## 9.8 Moje karty
„💳 Moje karty” → kliknij kartę, by zobaczyć i zmienić dane. Pola: nazwa (np. „Visa PKO”), numer (walidacja), ważna do (MM/RR), imię i nazwisko, CVV (opcjonalne, 3–4 cyfry, zaszyfrowane). „👁 Pokaż numer i CVV”. „📋 Kopiuj numer / datę / CVV” – schowek czyści się po 30 s. „💳 Wypełnij kartę na tej stronie”. Przy otwarciu trybu – przypomnienie o kartach, które niedługo wygasają.

## 9.9 Moje loginy i hasła (w trybie)
Nazwa, login, hasło (ukryte, „👁 Pokaż ukryte dane”), adres strony, notatka. „🌐 Otwórz stronę”. „🔑 Wpisz na otwartej stronie” – **tylko gdy otwarta karta ma tę samą domenę** (komunikat w innym razie). „🎲 Wygeneruj hasło” – 20 znaków, kopiowane, schowek czyszczony po 30 s.

## 9.10 Poufne dane (dokumenty)
Rodzaj (Dowód osobisty, Paszport, Prawo jazdy, PESEL, NI number, Ubezpieczenie zdrowotne, Inny dokument), imię i nazwisko, numer (ukryty), ważny do (DD.MM.RRRR), notatka. Przypomnienie o dokumentach wygasających w ciągu 60 dni.

## 9.11 Rachunki bankowe
Nazwa (np. „Moje konto”, „Czynsz – właściciel”), właściciel, numer / IBAN (ukryty), sort code / BIC (SWIFT), bank, tytuł przelewu / notatka. Przyciski 📋 przy każdym polu – bezbłędne przepisywanie do przelewu.

## 9.12 Moje notatki
Kategorie: Login, PIN, Przelewy, Kody odzyskiwania, Inne. Tytuł + treść → „Zapisz”. „🎲 Wygeneruj hasło”, „🔢 Znaki z numerami” (hasło znak po znaku z numerami – pomaga przy „podaj 3. i 7. znak”), 📋 kopiowanie. Okienka notatek i wyszukiwarki nie blokują strony (można przepisywać do formularza). „🔑 Wpisz login z notatki” – notatka musi mieć linie `login: …` i `hasło: …`.

## 9.13 Rachunki do opłacenia
**Pola:** Za co (lista nazw z arkusza), Kwota, Termin (DD.MM.RRRR), Powtarzanie (Co tydzień, Co 2 tygodnie, Co 4 tygodnie, Co miesiąc, Co 2 miesiące, Co kwartał, Co pół roku, Co rok, Jednorazowo albo własne, np. „co 10 dni”, „every 3 weeks”), Przypominaj dni wcześniej, Numer klienta / referencja, Strona do płatności, Notatka.
**Kroki:**
1. Wypełnij pola → „➕ Dodaj jako nową” (albo „✔ Zmień zaznaczoną”, „Usuń zaznaczoną”, „Wyczyść pola”).
2. Po zapłacie: zaznacz rachunek → „✔ Zapłacone (następny termin)”. Płatność trafia do historii (miesiąc = miesiąc terminu), termin przesuwa się wg powtarzania; jednorazowy znika.
3. „🌐 Otwórz stronę płatności”.
4. „➕ Dodaj rachunki z arkusza” – dla każdej kolumny arkusza bez rachunku: kwota z ostatniego miesiąca, co miesiąc, termin 1. dnia następnego miesiąca. Popraw dzień terminu.
5. Na końcu „Zapisz” – dopiero wtedy zmiany i płatności trafiają do zaszyfrowanej bazy.
**Przypomnienia:** przy otwarciu trybu – rachunki z terminem w ciągu kilku dni i po terminie, codziennie aż do zapłaty.
**Uwagi:** Velivo nie łączy się z bankiem; płatność oznaczasz sam. Dwa „Zapłacone” w tym samym miesiącu sumują się.

## 9.14 Arkusz rachunków
**Gdzie:** „📊 Arkusz rachunków (w karcie)” w menu 🏦 albo w „Rachunkach do opłacenia”.
- Wiersz = miesiąc, kolumna = rachunek; kolumna „Razem” = suma miesiąca, wiersz „Razem” = suma rachunku, prawy dolny róg = suma całości. Liczone na bieżąco.
- Miesiąc wpisujesz słownie lub liczbowo: „październik 2026”, „October 2026”, „10.2026”, „2026-10”.
- „➕ Wiersz (miesiąc)” – miesiąc po najpóźniejszym w tabeli, z kopią kwot z ostatniego wiersza.
- „➕ Kolumna (rachunek)” – nazwa w nagłówku; ✕ pod nazwą usuwa kolumnę, ✕ na końcu wiersza usuwa miesiąc.
- „📤 Wczytaj z Excela (CSV)” – Excel: Plik → Zapisz jako → CSV. Układ: 1. kolumna = miesiąc, 1. wiersz = rachunki. Separator `;`, `,` lub TAB wykrywany sam. Kolumny i wiersze „Razem/Suma/Total/Sum” pomijane. Istniejące miesiące uzupełniane (puste komórki nie kasują kwot), nowe rachunki dochodzą jako kolumny, wiersze sortowane chronologicznie.
- „Waluta, w której wpisujesz kwoty”: £, zł, €, $, CHF, kr, Kč, Ft, lei, ₴, ¥ – **tylko symbol, bez przeliczania**; domyślnie z regionu Windows.
- „💾 Zapisz i zamknij” – szyfrowanie w bazie i zamknięcie karty; wiersze bez kwot i bez poprawnego miesiąca pomijane (z informacją). „Anuluj” – bez zapisu.
- **Technicznie:** strona arkusza jest wczytywana lokalnie (bez internetu) z jednorazowym losowym znacznikiem; Velivo przyjmuje dane tylko od tej strony i tylko gdy adres karty się nie zmienił.

## 9.15 Zestawienie płatności
Okno „📊 Zestawienie płatności”: tabela miesięcy i rachunków, ręczne dopisanie płatności (rachunek, miesiąc, kwota), „Usuń płatności tego rachunku z miesiąca”, „📤 Wczytaj z Excela (CSV)…”, „📥 Zapisz do Excela (CSV)” (eksport `Velivo-rachunki.csv`, UTF-8 z BOM, separator `;`), wybór waluty.

## 9.16 Profile bankowe, dziennik, kopie
- „👤 Profile bankowe” / „➕ Nowy profil bankowy (inny użytkownik)…” – osobny tryb dla innej osoby: własne hasło lub klucz, osobne logowania i dane.
- „📜 Dziennik otwarć” – kiedy, na którym komputerze i czym otwarto tryb (także złe hasła).
- „💾 Kopia zapasowa bazy…” / „📂 Przywróć bazę z kopii…” – plik `.vbank` ze wszystkimi profilami, nadal zaszyfrowany (np. na pendrive).
- „Zapomniałem hasła – wyczyść tryb bankowy…” – bez hasła i klucza danych nie da się odczytać; ta opcja usuwa tryb i jego dane na tym komputerze.
- Synchronizacja: wszystkie profile bankowe przechodzą na sparowane komputery w zaszyfrowanym pakiecie; logowania do banków (cookies) zostają na każdym komputerze osobno.

# 10. Synchronizacja w sieci domowej (LAN)

## 10.1 Zasada
Bez chmury i bez konta. Komputery z tym samym profilem Velivo w jednej sieci wymieniają dane bezpośrednio (UDP, port 41919; grafiki Szybkiego Dostępu – TCP na tym samym porcie). Zapora Windows musi przepuszczać Velivo. Domyślnie włączona (Ustawienia → Synchronizacja).

## 10.2 Przed sparowaniem
Velivo wysyła co 10 s **tylko** pakiet `announce` (identyfikator, nazwa komputera, profil) i **nie wysyła ani nie przyjmuje żadnych danych**. W logu: „LAN sync czeka na sparowanie – dane nie są wysyłane”. Gdy w sieci jest drugi Velivo z tym samym profilem, jeden z komputerów (mniejszy identyfikator) proponuje połączenie.

## 10.3 Parowanie krok po kroku
1. Włącz synchronizację i zapisz ustawienia na obu komputerach.
2. Na jednym: „Sparuj urządzenie w sieci…” (albo zaakceptuj propozycję).
3. Na obu ekranach pojawia się ten sam **6-cyfrowy kod**. Porównaj go i potwierdź na obu.
4. Przy pierwszym połączeniu wybierz, czyje **ustawienia** zostają (zakładki, hasła i Szybki Dostęp i tak są łączone).
**Technicznie:** wymiana kluczy **ECDH P-256**; klucz transportowy = HMAC-SHA256(uzgodniony sekret, „Velivo LAN pairing v1” + id parowania); kod = HMAC(klucz, …) mod 1 000 000; potwierdzenie HMAC; klucz synchronizacji (32 B losowe) przesyłany zaszyfrowany AES-GCM. Prośby z innego profilu są odrzucane, próby parowania wygasają.

## 10.4 Szyfrowanie po sparowaniu
Klucz → PBKDF2-SHA256 (250 000 iteracji) → 32 B szyfrowanie + 32 B uwierzytelnianie. Pakiety: AES-256-GCM z danymi skojarzonymi (typ, nadawca, urządzenie, profil, czas, skrót), pakiety „hello” podpisane HMAC-SHA256 (porównanie w stałym czasie). Kompresja GZip przed szyfrowaniem, limit 8 MB po rozpakowaniu, 60 000 B na pakiet UDP. Pakiety starsze lub nowsze niż ±5 min od zegara odbiorcy są odrzucane (ochrona przed powtórzeniem) – ustaw automatyczny czas Windows na obu komputerach.

## 10.5 Co jest synchronizowane
| Dane | Sposób |
|---|---|
| Ustawienia | wygrywa nowsza zmiana; lokalne nie są przenoszone: głośniki, foldery (pobierania, filmy), język, rozmiar okna czytnika i okienka filmu, klucz LAN, cichy tryb |
| Zakładki | łączone z obu komputerów; usunięcia przez listę usuniętych (90 dni) |
| Hasła | łączone (tylko w zaszyfrowanym pakiecie) |
| Reguły prywatności, profile | wygrywa nowsza zmiana |
| Lista dodatków | brakujące dodatki ze sklepu instalowane automatycznie |
| Przypięte karty | otwierane i przypinane |
| Historia | łączona, osobne pakiety; usunięcia i „wyczyść wszystko” przenoszone |
| Szybki Dostęp z grafikami | archiwum ≤24 MB przez TCP, tylko od sparowanego, z ochroną ścieżek w archiwum |
| Tryb bankowy (wszystkie profile) | zaszyfrowany pakiet; cookies banków zostają lokalnie |
| Otwarte karty | **nie** – każdy komputer ma swoje (do przeniesienia służy „Wyślij do…”) |

## 10.6 Izolacja profili
Synchronizują się tylko komputery z tym samym profilem. Pakiety innego profilu są pomijane; gdy drugi komputer przełączy się na inny profil, Velivo pyta, czy też się przełączyć, żeby zachować synchronizację.

## 10.7 Wyślij kartę na inny komputer
Prawy klik na karcie → „📺 Wyślij do…” → wybierz komputer. Karta otwiera się na drugim komputerze; w filmie YouTube także od tego samego miejsca. Pakiet zaszyfrowany.

## 10.8 Wskaźniki, powiadomienia i diagnostyka
- Ikona w zasobniku pulsuje przy wysyłaniu i odbieraniu zmian.
- Dymki „🌐 Zsynchronizowano z Velivo w sieci lokalnej” / „Zsynchronizowano z … (ustawienia przyjęte)”. „Tryb cichy LAN” wyłącza dymki (log działa).
- „Panel diagnostyczny LAN…” (też Narzędzia Velivo → Diagnostyka LAN sync): status („wyłączona”, „aktywna, ale brak połączonych urządzeń”, „połączono z N urządzeniami”), wykryte urządzenia, liczniki pakietów i błędów, czas ostatniego odbioru/wysyłki, log (250 wpisów), „Odśwież teraz”, „Wyczyść log”.
- Ostrzeżenia: różnica zegarów („Odrzucono pakiety z …: zegar różni się o N min”), pakiet za duży, błędy startu/odbioru/nadawania, niepełny stan.

## 10.9 Plik odzyskiwania i synchronizacja plikiem
- „Zapisz plik odzyskiwania…” – hasło min. 12 znaków (dwukrotnie); plik ≤1 MB, PBKDF2 600 000 + AES-GCM. „Odtwórz parowanie…” po reinstalacji przywraca klucz bez ponownego parowania.
- **Synchronizacja E2E plikiem:** „Eksportuj paczkę…” / „Importuj paczkę…” – ustawienia, zakładki, hasła i inne dane w pliku zaszyfrowanym hasłem (PBKDF2 600 000 + AES-GCM) do przeniesienia np. na pendrive.

# 11. Wideo

## 11.1 Film na wierzchu
**Gdzie:** przycisk ▣ po najechaniu na film; prawy klik → „▣ Film na wierzchu (małe okienko)”.
**Kroki i elementy okienka:**
1. Okienko pokazuje sam film (reszta strony ukryta); przy pierwszym użyciu pojawia się przy prawej krawędzi ekranu.
2. Przeciągaj za górny pasek; zmieniaj rozmiar ramką (min. 160×114).
3. Przypinka: „zawsze na wierzchu” włącz/wyłącz (niebieska = przypięte).
4. Przezroczystość 15–100%: suwak i kółko myszy – np. 40–60% nad dokumentem.
5. Pasek przewijania filmu na dole okienka.
6. „Wróć do karty” – film wraca do karty.
**Technicznie:** osobny widok WebView2; przezroczystość przez warstwę okna Windows. Pozycja, rozmiar, przypięcie i przezroczystość są zapamiętywane (z kontrolą, czy okno mieści się na ekranie). YouTube: wznawianie przez jego odtwarzacz i czas w adresie (działa też po ponownym uruchomieniu Velivo). Zamknięcie karty nie zatrzymuje filmu; zamknięcie okienka – tak.
**Oglądanie przy zamkniętej przeglądarce:** okienko filmu jest osobnym oknem. Możesz zamknąć kartę, a nawet całe główne okno Velivo – film gra dalej, a program działa tylko dla okienka (połączenia LAN i Sejfu są wtedy zatrzymane). Gdy ponownie uruchomisz Velivo, a gra już tylko okienko filmu, Velivo zamyka je i startuje pełne okno. Przycisk „Wróć do karty” uruchamia przeglądarkę na tej samej stronie i w tym samym miejscu filmu.

## 11.2 Obraz w obrazie (PiP)
Przycisk ⧉ po najechaniu na film (Ustawienia → „Przycisk Obraz w obrazie nad filmami”) albo prawy klik → „⧉ Obraz w obrazie” (największy film). Okienko PiP zostaje nawet po zamknięciu karty. Filmy w ramkach (np. osadzony YouTube) mają przycisk ⧉ po najechaniu.

# 12. Pobieranie

## 12.1 Menedżer pobierania (Ctrl+J)
Velivo samo pobiera pliki (jak Internet Download Manager):
- **Wiele połączeń:** Ustawienia → „Połączeń na jeden pobierany plik” 1–16. Segmenty używane dla plików ≥2 MB, gdy serwer obsługuje zakresy (Range).
- **Wstrzymaj / Wznów** – od miejsca przerwania; gdy serwer nie pozwala, informacja „zacznie od nowa”.
- **Ponawianie:** do 5 prób z rosnącym opóźnieniem po zerwaniu połączenia; brak danych przez 60 s = błąd; „Za dużo przekierowań”.
- **Zalogowane pliki:** Velivo przekazuje cookies strony i sam śledzi przekierowania (cookies liczone dla każdego hosta).
- **Awaryjnie:** pliku tworzonego przez skrypt strony nie da się przejąć – pobiera go silnik (`pobieranie-silnik.log`).
- „Pytaj, gdzie zapisać każdy pobierany plik”; „Otwórz”, „Pokaż w folderze”, „Anuluj” (usuwa część), „Otwórz folder Pobrane”. Lista w `pobrane.json`.
- Wbudowane okienko pobierania Edge jest ukryte.
- Przy zamykaniu Velivo z aktywnymi pobraniami – pytanie.
- Każdy pobrany plik dostaje znacznik „plik z internetu” (7.15).

## 12.2 Pobieranie filmów i audio
**Gdzie:** przycisk ⬇ Pobierz po najechaniu na film (Ustawienia → „Przycisk Pobierz nad filmami”), prawy klik → „⬇ Pobierz film…”, Narzędzia → „Wykryj media do pobrania”.
**Tryby:** **Najlepsza jakość** (wideo ≤1080p MP4 + dźwięk M4A, łączone FFmpeg), **Prosta** (jeden plik bez FFmpeg), **Tylko dźwięk M4A**, **Tylko dźwięk MP3** (FFmpeg, najwyższa jakość). Folder filmów zapamiętany.
**Technicznie:** zwykły plik wideo → menedżer Velivo; YouTube, strumienie (m3u8/mpd/blob) i inne serwisy → **yt-dlp** (pobierany raz, ok. 18 MB, z GitHub; aktualizacja `-U` co 14 dni). **FFmpeg** (ok. 140 MB) pobierany raz przy pierwszej potrzebie. Oba narzędzia są sprawdzane sumą **SHA-256** z pliku sum tego samego wydania; niezgodny plik jest usuwany i nie jest uruchamiany. Adres strony przekazywany po separatorze `--`.
**Uwagi:** **napisów nie ma** – Velivo nie pobiera napisów. W trybie „Wykryj media” źródło `blob:` nie jest plikiem i nie da się go pobrać bezpośrednio.

## 12.3 Wykryj media do pobrania
Lista źródeł audio/wideo na stronie z przyciskami „Pobierz”, „Pobierz jako audio”, „Pokaż”. Komunikat „Nie wykryto źródeł audio/wideo” gdy brak.

# 13. Czytanie

## 13.1 Tryb czytania (przycisk „Czytnik”)
Artykuł bez reklam i menu. Wygląd: ☀ Jasny, 🌙 Ciemny, 🌅 Nocny z suwakiem natężenia. Rozmiar okna czytnika zapamiętany.
**Lokalne streszczenie:** bez AI i chmury – Velivo wybiera najważniejsze zdania (waga: częste słowa kluczowe tekstu, słowa z tytułu, premia za początek artykułu).

## 13.2 Czytanie na głos (Ctrl+Shift+U)
- Cała strona albo zaznaczenie; prawy klik → „🔊 Czytaj od tego miejsca”.
- Czytany akapit jest podświetlany i przewijany na środek.
- Przycisk prędkości (klik zmienia tempo), pauza/wznów, „Zatrzymaj czytanie”.
- Głos: Ustawienia → Czytanie: „Automatycznie (język strony: polski/angielski)”, głosy Windows, naturalne głosy online.

## 13.3 Naturalne głosy offline (Piper)
Ustawienia → Czytanie → głos „Piper”. Przy pierwszym użyciu Velivo pobiera program Piper (wersja 2023.11.14-2, suma SHA-256 sprawdzana) i wybrany głos (ok. 60 MB) do `%LOCALAPPDATA%\Przegladarka\Piper`; potem działa bez internetu. Proces Piper działa w tle, strona dzieli tekst na zdania i podświetla je.

## 13.4 „Gdzie ja to czytałem?” (Ctrl+Shift+F)
Ustawienia → „Zapamiętuj treść przeczytanych stron”. Velivo zapisuje tekst stron **tylko na tym komputerze** (`pamiec-stron.jsonl`) i znajduje artykuł po słowach zapamiętanych z treści, nie tylko z tytułu. Pomijane: karty prywatne, banki, płatności, poczta, strony z polem hasła.

## 13.5 Tłumaczenie
Prawy klik: „Przetłumacz zaznaczenie na polski”, „Przetłumacz stronę na polski / angielski” (obca → polski, polska → angielski) przez Tłumacza Google.

# 14. Dodatki

## 14.1 Dodatki Chromium
**Gdzie:** przycisk Dodatki (tryb dewelopera).
- Pasek ikon dodatków z okienkami (popup), „Otwórz okienko dodatku”, „Otwórz okienko w karcie”, „Strona ustawień dodatku”, przypinanie do paska (`dodatki-odpiete.txt`), „Przeładuj”, usuwanie.
- Ręczne wczytanie rozpakowanego dodatku: „Folder dodatku (z plikiem manifest.json)”.
- Przy starcie Velivo przeładowuje dodatki z folderu, których kod się zmienił (WebView2 trzyma stary skrypt tła w pamięci podręcznej).

## 14.2 Chrome Web Store
Na stronie dodatku w sklepie przycisk „➕ Dodaj do Velivo” (albo „Zainstaluj z linku/ID…”). WebView2 nie ma API sklepu, więc Velivo przechwytuje pobranie pliku `.crx` (CRX2 i CRX3), rozpakowuje go i instaluje. Lista dodatków synchronizuje się przez LAN.

# 15. Wydajność i pamięć podręczna

## 15.1 Śmieci przeglądarki i RAM dysk
Ustawienia → „Śmieci przeglądarki (pamięć podręczna)”:
- „Folder na śmieci” – np. RAM dysk (`R:\`). Velivo tworzy w nim podfolder `Velivo-smieci` i czyści **tylko jego zawartość**. Nie potrzeba dowiązań ani zewnętrznych narzędzi. Zmiana działa po ponownym uruchomieniu.
- „Domyślny” – powrót do profilu.
- „Usuwaj śmieci przy każdym uruchomieniu” – strony za pierwszym razem wczytają się odrobinę wolniej.
- „Teraz zajmują: …”, „Wyczyść śmieci teraz”.
**Technicznie:** cache stron i skompilowany JavaScript idą do wskazanego folderu (argumenty silnika). Cache grafiki (`GPUCache`) silnik zawsze trzyma w profilu – jest czyszczony przy następnym starcie. Śmieci są sprzątane tylko, gdy nie działa inne okno Velivo.
**Korzyści RAM dysku:** szybsze wczytywanie z cache, mniej zapisów na SSD, cache znika po wyłączeniu komputera.

## 15.2 Pozostałe mechanizmy
- „Rozmiar pamięci podręcznej (po ponownym uruchomieniu)”: Automatycznie albo wybrany rozmiar.
- Przechwytywanie żądań pomija zdjęcia i czcionki (każde przechwycone żądanie przechodzi przez wątek okna); reklamy-obrazki blokuje uBOL wewnątrz silnika bez kosztu dla okna.
- Filtr reklam budowany w tle i podmieniany w całości.
- Profil bankowy czytany z dysku tylko po zmianie pliku.
- Szybsze otwieranie stron (3.14).

# 16. Instalacja, aktualizacja, odinstalowanie

- Instalator `Velivo-Setup-1.22.exe` (Inno Setup): wybór języka (polski / English), instalacja do `%LOCALAPPDATA%\Programs\Velivo` bez uprawnień administratora, skrót w menu Start i opcjonalnie na pulpicie, rejestracja jako przeglądarka.
- Gdy wykryje dane z wcześniejszej instalacji: „Zachowaj moje ustawienia i dane (aktualizacja)” albo „Czysta instalacja” (stare dane przenoszone do kopii zapasowej z dopiskiem „kopia-…”).
- Brak .NET 10 Desktop Runtime lub WebView2 Runtime – instalator proponuje pobranie.
- Ciche instalowanie: `Velivo-Setup-1.22.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LANG=pl /DIR=<folder>`; odinstalowanie: `unins000.exe /VERYSILENT`.
- Wersja bez instalacji: `Velivo-1.22.exe` (jeden plik) albo `Velivo-1.22-portable.zip`.
- **Pliki nie są podpisane cyfrowo** – Windows może pokazać „Nieznany wydawca” → „Więcej informacji” → „Uruchom mimo to”. Sumy SHA-256 wydania są w `docs/VELIVO-SECURITY-AUDIT-CERTIFICATE.md`.

# 17. Czego Velivo nie ma (stan 1.22)

- Pobierania napisów do filmów.
- Fałszowania odczytów fingerprintingu (jest wykrywanie i raport).
- „Wspólnego panelu środowiska” dla wielu komputerów (jest panel diagnostyczny LAN).
- Synchronizacji otwartych kart (jest „Wyślij do…”).
- Połączenia z bankiem – płatności oznacza się ręcznie.
- Podpisu cyfrowego plików programu.
