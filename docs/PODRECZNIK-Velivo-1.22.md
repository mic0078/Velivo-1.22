# VELIVO 1.22 — KOMPLETNA INSTRUKCJA I KATALOG FUNKCJI

# Jak czytać ten podręcznik

Podręcznik opisuje Velivo 1.22 z punktu widzenia użytkownika: co można zrobić, gdzie to jest i jak tego użyć. Każda funkcja ma swój numer (np. **7.4**) i stały układ:

- **Do czego** – po co jest funkcja.
- **Gdzie** – przycisk, menu lub miejsce w Ustawieniach.
- **Jak używać** – czynności krok po kroku.
- **Co robi** – co dokładnie się dzieje.
- **Ograniczenia** – czego funkcja nie robi albo od czego zależy.

Funkcje, o których rozmawialiśmy, ale których w wersji 1.22 nie ma, są oznaczone **NIE DOSTĘPNE W 1.22**. Na końcu jest „Pełna lista funkcji Velivo 1.22”.

Velivo 1.22 działa na Windows 10 (wersja 1809 lub nowsza) i Windows 11, 64-bit. Strony wyświetla silnik Microsoft Edge (WebView2), więc wyglądają i działają jak w Edge i Chrome. Velivo nie wymaga konta ani chmury.

# 1. Pierwsze uruchomienie

## Instalacja

### 1.1 Instalator Velivo-Setup-1.22.exe
- **Do czego:** instaluje Velivo na komputerze.
- **Gdzie:** plik `Velivo-Setup-1.22.exe` (folder `Instalator` w repozytorium).
- **Jak używać:** 1) uruchom plik; 2) jeśli Windows pokaże „Nieznany wydawca”, kliknij „Więcej informacji” → „Uruchom mimo to”; 3) wybierz język instalatora: Polski albo English – w tym języku uruchomi się Velivo; 4) zaznacz, czy chcesz skrót na pulpicie; 5) kliknij „Instaluj”, a na końcu opcjonalnie „Uruchom Velivo”.
- **Co robi:** instaluje program do `%LOCALAPPDATA%\Programs\Velivo` (bez uprawnień administratora), dodaje skrót w menu Start i rejestruje Velivo w Windows jako przeglądarkę.
- **Ograniczenia:** pliki nie mają płatnego podpisu cyfrowego, stąd ostrzeżenie „Nieznany wydawca”.

### 1.2 Sprawdzenie składników systemu
- **Do czego:** Velivo potrzebuje .NET 10 Desktop Runtime i Microsoft Edge WebView2 Runtime.
- **Jak używać:** nic nie trzeba robić – instalator sam sprawdza oba składniki.
- **Co robi:** gdy czegoś brakuje, instalator proponuje pobranie (ok. 55 MB dla .NET) albo otwarcie strony pobierania. WebView2 jest zwykle w Windows 11.
- **Ograniczenia:** bez WebView2 przeglądarka nie wyświetli stron.

### 1.3 Aktualizacja albo czysta instalacja
- **Do czego:** instalacja nowej wersji na starszą.
- **Jak używać:** gdy instalator wykryje dane z wcześniejszej instalacji, wybierz: **„Zachowaj moje ustawienia i dane (aktualizacja)”** – zalecane – albo **„Czysta instalacja – zacznij od zera”**.
- **Co robi:** przy czystej instalacji ustawienia, zakładki, historia, hasła przeglądarki i Szybki Dostęp są przenoszone do kopii zapasowej (folder z dopiskiem „kopia-…”), a Velivo startuje jak nowe.

### 1.4 Wersja bez instalacji
- **Do czego:** uruchomienie Velivo bez instalowania, np. z pendrive’a.
- **Jak używać:** uruchom `Velivo-1.22.exe` (jeden plik) albo rozpakuj `Velivo-1.22-portable.zip` i uruchom `Velivo.exe`.
- **Ograniczenia:** do pełnego ustawienia jako domyślnej przeglądarki lepiej użyć instalatora.

### 1.5 Ciche instalowanie i odinstalowanie (dla zaawansowanych)
- **Jak używać:** `Velivo-Setup-1.22.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LANG=pl /DIR="C:\folder"`; odinstalowanie: `unins000.exe /VERYSILENT` w folderze programu albo Ustawienia Windows → Aplikacje.

### 1.6 Sprawdzenie, czy plik jest oryginalny
- **Do czego:** potwierdzenie, że pobrany instalator nie został zmieniony.
- **Jak używać:** w PowerShellu `Get-FileHash .\Velivo-Setup-1.22.exe -Algorithm SHA256`.
- **Co robi:** wynik musi być `01919B13F638C7ABD4B7A44157DFE13278550D08887C3DF4A53614B7671C9975` (instalator 1.22), a dla `Velivo-1.22.exe`: `A65A74A8B68BC74F34ECF10C90484F092DF93B6D2DFEFE56831A5AAD3672D494`.

## Pierwsze kroki

### 1.7 Pierwsze otwarcie
- **Co robi:** Velivo otwiera nową kartę z Szybkim Dostępem, wbudowanym blokerem reklam (uBlock Origin Lite) i domyślnymi ustawieniami prywatności. W tle pobierają się pełne listy blokowania reklam.
- **Ograniczenia:** pierwsze uruchomienie może trwać chwilę dłużej (tworzenie profilu silnika).

### 1.8 Ustawienie Velivo jako domyślnej przeglądarki
- **Gdzie:** Ustawienia → „Domyślna przeglądarka” → „Ustaw Velivo jako domyślną przeglądarkę…”.
- **Jak używać:** kliknij przycisk, w oknie Windows „Aplikacje domyślne” wybierz Velivo i „Ustaw domyślne”.
- **Co robi:** linki z innych programów i pliki `.html` otwierają się w Velivo. Stan jest widoczny w Ustawieniach („✓ Velivo jest domyślną przeglądarką”).
- **Pierwsze uruchomienie:** zaraz po instalacji Velivo jednorazowo pyta, czy ustawić je jako domyślną przeglądarkę; „Nie” – pytanie się nie powtórzy.
- **PDF:** pliki PDF otwierają się we wbudowanym czytniku Velivo (powiększanie, wyszukiwanie, drukowanie, zapis); instalator dodaje Velivo do „Otwórz za pomocą” dla `.pdf`, na stałe – w „Aplikacjach domyślnych” przy Velivo.
- **Ograniczenia:** Windows nie pozwala programowi ustawić się samemu – ostatnie kliknięcie należy do Ciebie.

### 1.9 Import z poprzedniej przeglądarki
- **Gdzie:** Ustawienia → „Import haseł, loginów i zakładek”.
- **Jak używać:** „Z Chrome / Edge / Brave / Opery” – zakładki są przenoszone od razu; dla haseł Velivo otwiera stronę haseł tamtej przeglądarki, z której eksportujesz plik CSV i wczytujesz go przyciskiem „Hasła i loginy z pliku (CSV…)”.
- **Co robi:** opisane szczegółowo w rozdziale 23.

### 1.10 Język programu
- **Gdzie:** Ustawienia → Wygląd → „Język interfejsu / Language”.
- **Jak używać:** wybierz Automatycznie (język z instalatora / Windows), Polski albo English i uruchom Velivo ponownie.
- **Co robi:** tłumaczy wszystkie okna, menu, podpowiedzi oraz Szybki Dostęp.

### 1.11 Wszystko w jednym – bez dodatkowych programów
- **Co to znaczy:** Velivo zastępuje kilka osobnych programów: **odtwarzacz filmów z dysku** (7.16 – z obrazem w obrazie i „Filmem na wierzchu”), **czytnik PDF** (1.8) i **torrenty** (8.19). Nie trzeba ich instalować ani aktualizować osobno.
- **Jak używać:** dwuklik na pliku albo prawy przycisk → „Otwórz za pomocą” → Velivo; na stałe – Ustawienia → Odtwarzacz filmów → „Otwieraj filmy i PDF w Velivo (Windows)…”.

# 2. Interfejs

## Okno główne

### 2.1 Pasek kart
- **Do czego:** przełączanie między otwartymi stronami.
- **Gdzie:** górna część okna.
- **Jak używać:** klik – przełącz; krzyżyk albo środkowy przycisk myszy – zamknij (karty przypiętej nie zamkniesz środkowym przyciskiem); prawy klik – menu karty (rozdział 4); „+” – nowa karta. Przy końcu paska jest zielony przycisk trybu bankowego.
- **Co robi:** karty prywatne mają oznaczenie, karty bankowe są zielone, karty przypięte są na początku bez krzyżyka, karty grające dźwięk mają głośnik, a automatycznie odświeżane – znaczek odświeżania.

### 2.2 Pasek narzędzi
- **Gdzie:** pod paskiem kart.
- **Przyciski (od lewej):** nowa karta prywatna, nowa karta, Wstecz, Dalej, Odśwież, strona startowa, pasek adresu, aktywny profil, gwiazdka zakładki, tarcza prywatności, tryb ciemny/nocny, czytanie na głos (z prędkością i stopem), Czytnik, zrzut ekranu, dodatki, pobrane, historia, ustawienia, „Więcej narzędzi”.
- **Co robi:** każdy przycisk ma podpowiedź z nazwą i skrótem klawiszowym.

### 2.3 Kompaktowy i dopasowujący się pasek
- **Do czego:** czytelny pasek także w wąskim oknie.
- **Gdzie:** działa sam; Ustawienia → Wygląd → „Zawsze kompaktowy pasek narzędzi”.
- **Co robi:** przy wąskim oknie etykiety się zmniejszają, a rzadziej używane przyciski trafiają do menu „…”. Opcja „Zawsze kompaktowy” wymusza ten układ także na szerokim oknie.

### 2.4 Pasek adresu
- **Do czego:** wpisywanie adresów i wyszukiwanie.
- **Jak używać:** wpisz adres albo słowa i naciśnij Enter; Ctrl+L przenosi kursor do paska adresu.
- **Co robi:** tekst, który nie jest adresem, trafia do wybranej wyszukiwarki; obsługuje skróty wyszukiwania (5.8).

### 2.5 Wskaźnik aktywnej reguły prywatności
- **Gdzie:** obok tarczy („Aktywna reguła prywatności dla tej domeny”), napis „Prywatność*”.
- **Co robi:** pokazuje, że dla otwartej strony działa Twoja własna reguła (rozdział 12).

### 2.6 Przycisk „➕ Dodaj do Velivo” w sklepie dodatków
- **Gdzie:** pojawia się na pasku, gdy jesteś na stronie dodatku w Chrome Web Store.
- **Co robi:** pobiera dodatek ze sklepu i instaluje go (rozdział 22).

### 2.7 Dymki powiadomień
- **Do czego:** krótkie informacje bez zasłaniania strony.
- **Gdzie:** na środku dołu okna, tuż nad paskiem zadań Windows (nigdy na nim), także przy oknie zmaksymalizowanym.
- **Co robi:** informują o zapisaniu, pobraniu, synchronizacji, przypomnieniach itp. Niektóre mają przyciski (np. „Otwórz”, „Pokaż w folderze”).

### 2.8 Pełny ekran
- **Jak używać:** F11 – włącz/wyłącz; Esc – wyjście z pełnego ekranu. Film na stronie przełączony w pełny ekran działa jak w innych przeglądarkach.

### 2.9 Jedno okno programu
- **Co robi:** gdy Velivo już działa, a otwierasz link z innego programu (np. z poczty), link otwiera się jako nowa karta w istniejącym oknie, zamiast uruchamiać drugie Velivo. Okno jest przywracane, jeśli było zminimalizowane.

### 2.10 Odporność na błędy
- **Co robi:** nieprzewidziany błąd nie zamyka przeglądarki. Velivo pokazuje komunikat (najwyżej jeden na 30 sekund dla tego samego błędu) i zapisuje szczegóły w pliku `bledy.log` w folderze danych. Strona, która próbuje zamknąć okno (`window.close`), zamyka najwyżej swoją kartę, nigdy całe Velivo.

# 3. Sterowanie myszką i klawiaturą

Velivo jest projektowane tak, żeby dało się je w pełni obsłużyć samą myszką, także z kanapy przy telewizorze.

### 3.1 Kółko myszy na przycisku powiększenia
- **Gdzie:** przycisk z procentem powiększenia na pasku.
- **Jak używać:** najedź i kręć kółkiem.
- **Co robi:** powiększa lub pomniejsza stronę i zapamiętuje to dla tej strony.

### 3.2 Kółko myszy na przycisku trybu nocnego
- **Jak używać:** gdy włączony jest tryb nocny, najedź na przycisk z księżycem i kręć kółkiem.
- **Co robi:** zmienia natężenie ocieplenia kolorów od 5% do 100%.

### 3.3 Kółko myszy nad okienkiem filmu
- **Co robi:** zmienia przezroczystość okienka „Film na wierzchu” od 15% do 100% (rozdział 7).

### 3.4 Klik na linkach
- **Zwykły klik** – otwiera w tej samej karcie (działają Wstecz i Dalej).
- **Środkowy przycisk albo Ctrl+klik** – zawsze nowa karta.

### 3.5 Menu prawego przycisku na stronie
- **Do czego:** szybki dostęp do narzędzi dla strony, zaznaczenia, filmu lub linku.
- **Co zawiera (zależnie od miejsca kliknięcia):** Wyszukaj zaznaczony tekst; Przetłumacz zaznaczenie na polski; Czytaj zaznaczenie na głos; Czytaj od tego miejsca; Przetłumacz stronę na polski / na angielski; Tryb czytania i streszczenie; Gdzie ja to czytałem?; Film na wierzchu (małe okienko); Obraz w obrazie; Pobierz film…; Blokuj element (reklamę)…; Przywróć zablokowane elementy na tej stronie; Odrzucaj / Nie odrzucaj banerów ciasteczek na tej stronie; Pokaż wersję telefonu tej strony / Wróć do wersji komputerowej; Zrzut ekranu (widoczna część / cała strona); Dodaj do Szybkiego Dostępu; Narzędzia Velivo (Prywatność i antyfingerprinting, Wykryj media do pobrania, Menedżer pobrań, Diagnostyka LAN sync, Dodatki, Historia, Szukaj w kartach, przełączenie profilu).

### 3.6 Menu prawego przycisku na karcie
- **Co zawiera:** Odśwież, Duplikuj kartę, Przypnij/Odepnij kartę, Wycisz kartę / Włącz dźwięk karty, Odświeżaj automatycznie, Dodaj do grupy, Usuń z grupy, Zestawy kart, Wyślij do…, Otwórz jako prywatną (incognito), Zamknij kartę, Zamknij inne karty, Zamknij karty po prawej, Przywróć zamkniętą kartę.

### 3.7 Skróty klawiszowe
| Skrót | Działanie |
|---|---|
| Ctrl+T | nowa karta |
| Ctrl+Shift+N | nowa karta prywatna |
| Ctrl+W | zamknij kartę |
| Ctrl+Shift+T | przywróć zamkniętą kartę |
| Ctrl+Tab | następna karta |
| Ctrl+Shift+A | szukaj w kartach |
| Ctrl+L | przejdź do paska adresu |
| Ctrl+D | dodaj / usuń zakładkę |
| Ctrl+H | historia |
| Ctrl+J | pobrane pliki |
| Ctrl+Shift+F | „Gdzie ja to czytałem?” |
| Ctrl+O | otwórz film z dysku (odtwarzacz) |
| Ctrl+Shift+U | czytaj stronę na głos / pauza / wznów |
| Alt+← / Alt+→ | wstecz / dalej |
| F5 | odśwież |
| F11, Esc | pełny ekran / wyjście |
| Ctrl + / Ctrl − / Ctrl 0, Ctrl+kółko | powiększ / pomniejsz / domyślne |

# 4. Karty i okna

### 4.1 Nowa karta
- **Jak używać:** Ctrl+T, „+” na pasku kart albo gest myszy w górę.
- **Co robi:** otwiera Szybki Dostęp (albo stronę ustawioną jako stronę nowej karty).

### 4.2 Karta prywatna (incognito)
- **Jak używać:** Ctrl+Shift+N, przycisk karty prywatnej na pasku albo z menu karty „Otwórz jako prywatną”.
- **Co robi:** używa osobnego, izolowanego magazynu (InPrivate); nie zapisuje historii, sesji ani treści do „Gdzie ja to czytałem?”. Po zamknięciu znika wszystko.

### 4.3 Duplikowanie karty
- **Gdzie:** menu karty → „Duplikuj kartę”.

### 4.4 Zamykanie kart
- **Jak używać:** krzyżyk, Ctrl+W, gest w dół, środkowy klik na karcie (nie dotyczy przypiętych); menu karty: „Zamknij inne karty”, „Zamknij karty po prawej”.

### 4.5 Przywracanie zamkniętej karty
- **Jak używać:** Ctrl+Shift+T albo menu karty → „Przywróć zamkniętą kartę”. Można powtarzać, by przywracać kolejne.

### 4.6 Przypięte karty
- **Do czego:** stale otwarte strony (poczta, kalendarz).
- **Jak używać:** menu karty → „Przypnij kartę”; odpięcie – „Odepnij kartę”.
- **Co robi:** karta przechodzi na początek paska, nie ma krzyżyka i wraca po każdym uruchomieniu. Przy synchronizacji LAN przypięte karty pojawiają się też na drugim komputerze.

### 4.7 Grupy kart
- **Do czego:** porządek przy wielu kartach (np. „Praca”, „Zakupy”).
- **Jak używać:** 1) menu karty → „Dodaj do grupy” → „Nowa grupa…” → nazwa; 2) kolejne karty: „Dodaj do grupy” → nazwa grupy; 3) klik w etykietę grupy zwija lub rozwija grupę; 4) prawy klik na etykiecie: „Zmień nazwę grupy…”, „Kolor” (8 kolorów), „Rozgrupuj (karty zostają)”, „Zamknij wszystkie karty grupy”; 5) „Usuń z grupy” w menu karty.
- **Co robi:** grupy z kolorem, nazwą i stanem zwinięcia wracają po ponownym uruchomieniu.

### 4.8 Zestawy kart
- **Do czego:** zapisanie kompletu kart i otwarcie go później jednym kliknięciem.
- **Jak używać:** menu karty → „Zestawy kart” → „Zapisz otwarte karty jako zestaw…” → nazwa (np. Praca). Później „Zestawy kart” → nazwa → „Otwórz wszystkie karty” albo „Zastąp obecnymi kartami”; „Usuń zestaw”.

### 4.9 Automatyczne odświeżanie karty
- **Do czego:** strony, które same się nie aktualizują (wyniki, kursy, kolejka zamówień) albo wylogowują po bezczynności.
- **Jak używać:** menu karty → „Odświeżaj automatycznie” → Wyłączone / 1 / 5 / 15 / 30 min.
- **Co robi:** karta odświeża się co wybrany czas; na karcie widać znaczek z podpowiedzią „Odświeżanie co N min”.
- **Ograniczenia:** ustawienie nie jest zapamiętywane po ponownym uruchomieniu.

### 4.10 Szukanie w kartach
- **Jak używać:** Ctrl+Shift+A (albo Narzędzia Velivo → Szukaj w kartach) → wpisz fragment tytułu lub adresu → strzałki i Enter. Esc zamyka.

### 4.11 Wyciszanie karty
- **Jak używać:** menu karty → „Wycisz kartę” / „Włącz dźwięk karty”.

### 4.12 Linki w tej samej karcie
- **Gdzie:** Ustawienia → Karty → „Otwieraj linki w tej samej karcie”.
- **Co robi:** linki, które strona chce otworzyć w nowej karcie, otwierają się w bieżącej – działają Wstecz i Dalej. Ctrl+klik nadal otwiera nową kartę. Wyłączone: jak w innych przeglądarkach.

### 4.13 Przywracanie sesji
- **Gdzie:** Ustawienia → Karty → „Po uruchomieniu przywracaj karty z poprzedniej sesji”.
- **Co robi:** po starcie wracają karty, aktywna karta i grupy. Karty prywatne nigdy nie są zapisywane; przy „Czyść dane przy zamknięciu” sesja też nie jest zapisywana.

### 4.14 Wyślij kartę na inny komputer
- **Gdzie:** menu karty → „Wyślij do…”. Opis w rozdziale 17.

### 4.15 Powiększenie strony
- **Jak używać:** Ctrl+kółko, Ctrl +/−/0, kółko na przycisku procentów; domyślne powiększenie wszystkich stron – Ustawienia → Wygląd.
- **Co robi:** powiększenie jest zapamiętywane osobno dla każdej strony i przenoszone na inne karty z tą samą stroną.

### 4.16 Wersja telefonu strony
- **Do czego:** strony, które na komputerze są ciężkie albo pełne reklam, a w wersji mobilnej prostsze.
- **Jak używać:** prawy klik → „Pokaż wersję telefonu tej strony”; powrót – „Wróć do wersji komputerowej tej strony”.
- **Co robi:** karta przedstawia się stronie jako telefon z Androidem; wybór jest zapamiętany dla strony.

### 4.17 Zrzut ekranu
- **Jak używać:** przycisk aparatu albo prawy klik → „Zrzut ekranu” → „Widoczna część strony” albo „Cała strona (z przewijaniem)”.
- **Co robi:** zapisuje PNG w `Obrazy\Zrzuty Velivo`, kopiuje go do schowka i pokazuje dymek z „Otwórz” i „Pokaż w folderze”. Folder zrzutów otwiera „Otwórz folder ze zrzutami”.
- **Ograniczenia:** na stronach banków i płatności zrzut jest zablokowany (13.5).

### 4.18 Zamykanie i minimalizowanie Velivo
- **Zamknięcie okna:** zamyka Velivo i zapisuje sesję. Gdy trwają pobierania, Velivo pyta, czy zamknąć.
- **Gdy gra „Film na wierzchu”:** zamknięcie głównego okna nie zatrzymuje filmu – okienko filmu gra dalej samodzielnie (7.10).
- **Zostań w zasobniku:** opcja chowa Velivo do ikony przy zegarze zamiast zamykać (25.17).
- **Minimalizowanie:** zwykłe; Velivo wraca z paska zadań, a otwarcie linku z innego programu albo kliknięcie w powiadomienie przywraca okno.

# 5. Gesty myszy i wyszukiwanie

### 5.1 Gesty myszy
- **Do czego:** nawigacja bez szukania przycisków.
- **Gdzie:** włączone domyślnie; Ustawienia → Wyszukiwanie i start → „Gesty myszy (prawy przycisk + ruch)”.
- **Jak używać:** przytrzymaj **prawy przycisk**, przesuń myszkę o ok. 3 cm i puść.

| Ruch | Działanie |
|---|---|
| ← w lewo | Wstecz |
| → w prawo | Dalej |
| ↑ w górę | Nowa karta |
| ↓ w dół | Zamknij kartę |
| ↓ potem → | Odśwież |

- **Co robi:** zwykły prawy klik bez ruchu otwiera menu jak zawsze. Strona nie może podrobić gestu – komunikat do Velivo ma tajny znacznik uruchomienia.
- **Przypięta karta:** gest ↓ jej nie zamyka (tak samo jak krzyżyk). Skręt w geście ↓ potem → liczy się już od ok. 15 px, więc „Odśwież” nie zamienia się przypadkiem w zamknięcie karty.

### 5.2 Wyszukiwarka w pasku adresu
- **Gdzie:** Ustawienia → Wyszukiwanie i start → „Wyszukiwarka w pasku adresu”.
- **Jak używać:** wybierz z listy, np. Startpage (domyślna, prywatna z wynikami Google), DuckDuckGo, Brave Search.

### 5.3 Skróty wyszukiwania
- **Do czego:** szukanie od razu w konkretnym serwisie.
- **Gdzie:** Ustawienia → Wyszukiwanie i start → „Skróty wyszukiwania (np. „yt koty”)…”.
- **Jak używać:** w pasku adresu wpisz skrót, spację i zapytanie, np. `yt koty` – wyniki YouTube. W okienku skrótów dodasz własne (skrót + adres z miejscem na zapytanie).

### 5.4 Wyszukiwanie zaznaczonego tekstu
- **Jak używać:** zaznacz tekst → prawy klik → „Wyszukaj …”. Wyniki otwierają się w nowej karcie.

### 5.5 Strona startowa
- **Gdzie:** Ustawienia → Wyszukiwanie i start → „Strona startowa”; przycisk domku na pasku.

### 5.6 Szybki Dostęp jako strona nowej karty
- **Gdzie:** Ustawienia → Wyszukiwanie i start → „Szybki Dostęp jako strona nowej karty”. Opis Szybkiego Dostępu – rozdział 21.

# 6. Animacje i wygląd

### 6.1 Efekt wejścia treści
- **Do czego:** płynne, przyjemne pojawianie się nowych stron.
- **Gdzie:** Ustawienia → Wygląd i czytelność → „Efekt wejścia treści”.
- **Jak używać:** wybierz jeden z efektów:
  - **Wyostrzenie** (domyślny) – treść wyłania się z lekkiego rozmycia;
  - **Z ciemności (kinowe)** – strona rozjaśnia się z ciemnego ekranu, jak w kinie;
  - **Delikatne przyciemnienie** – krótkie, subtelne przyciemnienie i powrót;
  - **Brak** – strona pojawia się od razu.
- **Co robi:** po kliknięciu linku strona lekko przygasa, a nowa treść pojawia się wybranym efektem. Efekt obejmuje całe okno strony, także tło i przypięte menu.

### 6.2 Szybkość efektu wejścia
- **Gdzie:** Ustawienia → Wygląd i czytelność → „Szybkość efektu wejścia”.
- **Jak używać:** suwak od 0 do 5 s (co 0,1 s); 0 = automatycznie. Przykłady: szybkie 0,15 s, delikatne 0,3 s, wolne 0,5 s, spokojne 1 s, bardzo spokojne 2 s, senne 3 s, najwolniejsze 4 s.

### 6.3 Styl wyglądu: Nowoczesny i Kolorowy
- **Gdzie:** Ustawienia → Wygląd → „Styl wyglądu”.
- **Co robi:** **Nowoczesny** (domyślny, jak Windows 11) – spokojne przyciski bez kolorowych teł, jednokolorowe ikony Windows 11, jeden niebieski akcent, kolor tylko przy najechaniu albo gdy coś jest włączone; aktywna karta wyróżniona pogrubieniem; ikony także w menu prawego przycisku. **Kolorowy** – kolorowe przyciski i emoji.

### 6.4 Motywy przeglądarki
- **Gdzie:** Ustawienia → Wygląd → „Motyw przeglądarki (kolory pasków i kart)”.
- **Co robi:** 10 motywów: Jasny, Grafit, Granat, Nocny fiolet, Las, Ocean, Zachód słońca, Czerń (OLED), Papier, Mgła. Zmieniają kolory paska kart, paska narzędzi i zakładek.

### 6.5 Tryb ciemny stron
- **Do czego:** wygodne czytanie wieczorem.
- **Gdzie:** przycisk trybu stron (ikona pokazuje obecny tryb: ☀ jasny, 🌙 ciemny, 🌅 nocny); Ustawienia → Wygląd → „Tryb ciemny stron”.
- **Jak używać:** klik w przycisk (jasny → ciemny → nocny).
- **Co robi:** strony, które mają własny ciemny wygląd, przełączają się na niego; pozostałe jasne strony są przyciemniane, a **zdjęcia zostają w prawdziwych kolorach**. Tryb jest zapamiętywany **osobno dla każdej strony**.
- **Każda strona ma swój tryb:** jedna strona może być jasna, druga ciemna, trzecia nocna – Velivo pamięta to dla każdej strony osobno, także po ponownym uruchomieniu.
- **Wejście strony w trybie ciemnym:** tło karty jest ciemne, zanim strona się narysuje, a efekt wejścia (6.1) przechodzi przez ciemną mgłę – bez białego błysku.
- **Ograniczenia:** gdy silnik wystartował w innym trybie, Velivo poprawia stronę albo proponuje ponowne uruchomienie (karty wracają).

### 6.6 Tryb nocny (cieplejsze kolory)
- **Gdzie:** ten sam przycisk – trzecie kliknięcie; Ustawienia → „Tryb nocny – cieplejsze kolory stron (jak Światło nocne w Windows)”.
- **Jak używać:** kółkiem myszy na przycisku ustawiasz natężenie 5–100%.
- **Co robi:** ciepła, półprzezroczysta warstwa nad stroną – mniej niebieskiego światła. Nie przeszkadza w klikaniu.

### 6.7 Domyślne powiększenie stron
- **Gdzie:** Ustawienia → Wygląd i czytelność → „Domyślne powiększenie stron”. Każdą stronę możesz też powiększyć osobno (4.15).

# 7. Wideo i „Film na wierzchu”

Po najechaniu myszką na film w jego prawym górnym rogu pojawiają się trzy przyciski: **⬇ Pobierz · ▣ Na wierzchu · ⧉ Obraz w obrazie**. Działa to także na YouTube.

### 7.1 Film na wierzchu – otwarcie
- **Do czego:** oglądanie filmu w małym, osobnym okienku, które może być nad innymi programami.
- **Gdzie:** przycisk **▣ Na wierzchu** nad filmem albo prawy klik → „Film na wierzchu (małe okienko)”.
- **Jak używać:** kliknij ▣. Film w karcie się zatrzyma, a w okienku ruszy od tego samego miejsca.
- **Co robi:** tworzy **osobne okno Windows** (z własną pozycją na pasku zadań), w którym jest tylko film.

### 7.2 Czysty widok filmu
- **Co robi:** okienko pokazuje sam film na całą swoją powierzchnię – bez menu, komentarzy, podpowiedzi, okienek i banerów. Na YouTube wygląda jak osobny odtwarzacz. Blokada reklam i trackerów działa jak w karcie, a reklamy wideo YouTube są pomijane.

### 7.3 Przesuwanie okienka
- **Jak używać:** chwyć ciemny pasek u góry okienka i przeciągnij.

### 7.4 Zmiana rozmiaru okienka
- **Jak używać:** chwyć krawędź albo róg i ciągnij.
- **Ograniczenia:** najmniejszy rozmiar to 160×90 pikseli obrazu.

### 7.5 Przezroczystość okienka
- **Do czego:** oglądanie i jednoczesna praca – widać tekst pod filmem.
- **Jak używać:** najedź na okienko i kręć **kółkiem myszy**; na pasku widać np. „Widoczność 60%”.
- **Co robi:** zmienia przezroczystość całego okienka razem z filmem od 15% do 100%.
- **Ograniczenia:** wymaga Windows 10 w wersji 1809 lub nowszej.

### 7.6 Zawsze na wierzchu (przypinka)
- **Jak używać:** kliknij przypinkę 📌 na pasku okienka.
- **Co robi:** **niebieska** – okienko zawsze nad innymi programami; **biała** – zwykłe okno, które chowa się pod inne, gdy klikniesz gdzie indziej.

### 7.7 Pauza i wznowienie
- **Jak używać:** kliknij w film.

### 7.8 Kompaktowy odtwarzacz w okienku (przewijanie, głośność, pełny ekran)
- **Co robi:** po najechaniu myszką na dole okienka pojawia się pasek: ▶/❚❚ pauza, ◀◀ 10 s wstecz, ▶▶ 10 s do przodu, pasek czasu (klik / przeciąganie, kółko nad nim ±5 s), 🔊 wycisz, suwak głośności (kółko nad nim ±5%) i ⛶ pełny ekran. Działa tak samo dla YouTube, innych stron i filmów z dysku.
- **Klawiatura** (po kliknięciu w okienko): spacja – pauza, ← → – 10 s, ↑ ↓ – głośność, M – wycisz, F – pełny ekran, Esc – wyjście z pełnego ekranu. Dwuklik na filmie – pełny ekran.
- **Małe okienko:** pasek sam się upraszcza – najpierw chowa suwak głośności i czas, w najmniejszym także ±10 s.

### 7.9 Powrót do strony (↩)
- **Jak używać:** kliknij ↩ na pasku okienka.
- **Co robi:** wraca do strony w Velivo w tym samym miejscu filmu – **nawet gdy przeglądarka była zamknięta** (Velivo uruchomi się na tej stronie). Na YouTube miejsce w filmie jest przekazywane w adresie.

### 7.10 Oglądanie przy zamkniętej przeglądarce
- **Do czego:** muzyka, podcast albo mecz bez trzymania otwartej całej przeglądarki.
- **Jak używać:** otwórz Film na wierzchu, potem zamknij kartę – a nawet całe główne okno Velivo.
- **Co robi:** okienko filmu jest osobnym oknem i **gra dalej po zamknięciu karty i po zamknięciu głównego okna Velivo**. Program działa wtedy tylko dla okienka filmu, więc zajmuje mniej pamięci (synchronizacja LAN i połączenie z Sejfem są wtedy zatrzymane). Zamknięcie okienka ✕ kończy program. **Nie wymaga opcji „Zostań w zasobniku”.**
- **Ograniczenia:** gdy ponownie uruchomisz Velivo (ikoną lub linkiem), a gra już tylko okienko filmu, Velivo zamknie okienko i uruchomi pełne okno przeglądarki.

### 7.11 Zapamiętywanie ustawień okienka
- **Co robi:** miejsce, wielkość, przezroczystość i przypinka są zapamiętywane i używane przy następnym otwarciu. Gdy zapamiętane miejsce nie mieści się na obecnym ekranie (np. odłączony monitor), okienko pojawia się przy prawej krawędzi ekranu.

### 7.12 Zamknięcie okienka
- **Jak używać:** ✕ na pasku okienka. Zamknięcie okienka zatrzymuje film.

### 7.13 Przykłady użycia
- muzyka lub podcast w małym, półprzezroczystym okienku w rogu ekranu podczas pracy;
- mecz albo transmisja na żywo nad dokumentem lub arkuszem – ustaw widoczność 40–60%, a tekst pod spodem jest czytelny;
- poradnik wideo obok programu, w którym wykonujesz kroki;
- film gra nawet po zamknięciu Velivo, więc przeglądarka nie zajmuje pamięci.

### 7.14 Obraz w obrazie (PiP)
- **Gdzie:** przycisk **⧉** nad filmem (Ustawienia → „Przycisk Obraz w obrazie nad filmami”) albo prawy klik → „Obraz w obrazie”.
- **Co robi:** standardowe okienko obrazu w obrazie silnika Edge, zawsze na wierzchu. Z menu wybierany jest największy film na stronie. Okienko zostaje nawet po zamknięciu karty.
- **Ograniczenia:** w odróżnieniu od „Filmu na wierzchu” nie ma regulacji przezroczystości ani zapamiętywania miejsca. Filmy w ramkach (np. osadzony YouTube) mają przycisk ⧉ po najechaniu.

### 7.15 Wyciszenie filmu w karcie
- Menu karty → „Wycisz kartę” (4.11).

### 7.16 Odtwarzacz filmów z dysku (offline)
- **Gdzie:** dwuklik na pliku wideo (MP4, WebM, MKV, MOV, M4V, OGV – instalator dodaje Velivo do „Otwórz za pomocą”), Ctrl+O, prawy klik → Narzędzia Velivo → „🎬 Otwórz film z dysku”.
- **Co robi:** film w karcie na całe okno, bez internetu; działają ⧉ Obraz w obrazie i ▣ Film na wierzchu.
- **Sterowanie:** pasek odtwarzacza; spacja / klik – pauza, ← → – 5 s, ↑ ↓ – głośność, F – pełny ekran, M – wycisz.
- **Ustawienia:** sekcja „Odtwarzacz filmów” (25.26) – otwieranie w Velivo, odtwarzanie od razu, wznawianie od miejsca (tylko ten komputer), powtarzanie.
- **Ograniczenia:** najpewniej MP4 (H.264) i WebM; nieobsługiwany kodek (np. HEVC/H.265) – komunikat.

# 8. Pobieranie plików, filmów i muzyki

## Menedżer pobierania

### 8.1 Menedżer pobierania Velivo
- **Do czego:** szybkie i pewne pobieranie plików, jak w Internet Download Manager.
- **Gdzie:** przycisk pobranych na pasku, Ctrl+J.
- **Co robi:** Velivo samo pobiera plik i pokazuje listę z paskami postępu, prędkością i stanem. Wbudowane okienko pobierania silnika Edge jest ukryte.

### 8.2 Pobieranie wieloma połączeniami
- **Gdzie:** Ustawienia → Bezpieczeństwo i pobieranie → „Połączeń na jeden pobierany plik”.
- **Jak używać:** wybierz od 1 (bez dzielenia) do 16. Więcej połączeń zwykle znaczy szybciej.
- **Ograniczenia:** dzielenie działa dla plików od 2 MB, gdy serwer obsługuje pobieranie w częściach.

### 8.3 Wstrzymaj i wznów
- **Jak używać:** przyciski „Wstrzymaj” i „Wznów” przy pliku.
- **Co robi:** wznawia od miejsca przerwania. Gdy serwer nie pozwala wznowić, Velivo informuje, że plik zacznie się od nowa.

### 8.4 Automatyczne ponawianie
- **Co robi:** po zerwaniu połączenia Velivo samo próbuje ponownie do 5 razy, z coraz dłuższą przerwą, od miejsca przerwania. Gdy serwer nie przysyła danych przez 60 s, pobieranie jest przerywane z komunikatem i przyciskiem „Wznów”.

### 8.5 Pobieranie plików po zalogowaniu
- **Co robi:** Velivo przekazuje do pobierania ciasteczka strony i samo obsługuje przekierowania, więc działa pobieranie z kont, poczty i chmur, na których jesteś zalogowany.

### 8.6 Pobieranie awaryjne przez silnik
- **Co robi:** plik tworzony przez skrypt strony (którego nie da się przejąć) pobiera silnik Edge; pojawia się na tej samej liście z dopiskiem „(pobiera silnik przeglądarki)”.

### 8.7 Pytanie o miejsce zapisu
- **Gdzie:** Ustawienia → „Pytaj, gdzie zapisać każdy pobierany plik”. Bez tej opcji pliki trafiają do folderu Pobrane.

### 8.8 Lista pobranych plików
- **Jak używać:** „Otwórz” – uruchamia plik; „Pokaż w folderze”; „Anuluj” – pyta i usuwa pobraną część; „Otwórz folder Pobrane”.
- **Co robi:** lista jest zapamiętywana po ponownym uruchomieniu.

### 8.9 Ostrzeżenie przy zamykaniu
- **Co robi:** gdy zamykasz Velivo w trakcie pobierania, pojawia się pytanie, czy zamknąć (pliki pobierane przez silnik zostaną przerwane).

### 8.10 Znacznik „plik z internetu”
- **Co robi:** każdy pobrany plik dostaje znacznik Windows „pobrano z internetu”. Dzięki temu SmartScreen ostrzega przy uruchamianiu programu, a Word i Excel otwierają dokument w widoku chronionym (makra zablokowane). Adres strony nie jest zapisywany w pliku.
- **Ograniczenia:** na pendrive’ach z systemem FAT32/exFAT znacznik nie może zostać zapisany.

## Filmy i audio

### 8.11 Przycisk ⬇ Pobierz nad filmem
- **Gdzie:** po najechaniu na film; Ustawienia → „Przycisk Pobierz nad filmami”; prawy klik → „Pobierz film…”.
- **Co robi:** zwykły plik wideo trafia do menedżera Velivo (do 16 połączeń). YouTube, strumienie i inne serwisy obsługuje darmowe narzędzie **yt-dlp**.

### 8.12 Wybór jakości i formatu
- **Jak używać:** po kliknięciu ⬇ wybierz:
  - **Najlepsza jakość** – obraz do 1080p w MP4 z dźwiękiem (wymaga FFmpeg);
  - **Prosta** – jeden plik bez łączenia (bez FFmpeg);
  - **Tylko dźwięk M4A** – bez dodatków;
  - **Tylko dźwięk MP3** – najwyższa jakość (wymaga FFmpeg).
- Wybierz folder (zapamiętywany) i potwierdź. Postęp jest widoczny na liście pobranych z możliwością anulowania.

### 8.13 yt-dlp – pobierany raz
- **Co robi:** przy pierwszym pobieraniu z YouTube Velivo pyta o pobranie yt-dlp (ok. 18 MB z GitHub). Narzędzie aktualizuje się samo co 2 tygodnie, bo serwisy często się zmieniają.

### 8.14 FFmpeg – pobierany raz
- **Co robi:** dla najlepszej jakości i MP3 Velivo pyta o pobranie FFmpeg (ok. 140 MB do pobrania, ok. 130 MB po rozpakowaniu). Bez niego Velivo pobiera w trybie prostym.

### 8.15 Sprawdzanie pobranych narzędzi
- **Co robi:** przed użyciem yt-dlp i FFmpeg Velivo sprawdza ich sumę kontrolną SHA-256 z oficjalnego wydania. Plik, który się nie zgadza, jest usuwany i nie jest uruchamiany.

### 8.16 Wykryj media do pobrania
- **Gdzie:** Narzędzia Velivo (prawy klik albo „Więcej narzędzi”) → „Wykryj media do pobrania”.
- **Co robi:** pokazuje listę źródeł audio i wideo na stronie z przyciskami „Pobierz”, „Pobierz jako audio” i „Pokaż”.
- **Ograniczenia:** źródła typu `blob:` (dane tymczasowe w pamięci strony) nie są plikami; użyj wtedy przycisku ⬇ nad filmem.

### 8.17 Pobieranie napisów – **NIE DOSTĘPNE W 1.22**
- Velivo 1.22 nie pobiera napisów do filmów.

### 8.18 Pobieranie z YouTube w trybie „Wykryj media” – ograniczenie
- Na YouTube Velivo nie udostępnia pobierania przez „Wykryj media” – służy do tego przycisk ⬇ Pobierz (yt-dlp).

### 8.19 Torrenty (magnet i .torrent) – włączane w Ustawieniach
- **Gdzie:** Ustawienia → sekcja **Torrenty** (domyślnie wyłączone). Postęp – w oknie Pobrane (Ctrl+J).
- **Co robi:** link magnet (kliknięty albo wklejony w pasek adresu) i pobrany plik `.torrent` pobiera darmowy program **aria2** – Velivo dociąga go przy pierwszym użyciu, za zgodą (ok. 2,5 MB), i uruchamia tylko przy zgodnej sumie SHA-256.
- **Skojarzenie z Windows:** instalator kojarzy pliki `.torrent` i linki `magnet:` z Velivo – dwuklik na pliku albo link z innego programu od razu rozpoczyna pobieranie. Gdy torrenty są wyłączone, Velivo podpowie, gdzie je włączyć. Jeśli `.torrent` otwiera inny program, wybierz Velivo w „Otwórz za pomocą” albo w „Aplikacjach domyślnych”.
- **Osobna strefa:** domyślny folder `Pobrane\Velivo-Torrenty` (do zmiany w Ustawieniach). Przy włączonym „Pytaj, gdzie zapisać plik” folder wybierasz przy każdym torrencie. Pobrane pliki mają znacznik „plik z internetu” (8.10), Velivo niczego samo nie otwiera.
- **Ustawienia:** prędkość pobierania i wysyłania, udostępnianie po pobraniu (do jakiego współczynnika i jak długo – 0 = bez udostępniania), liczba uczestników.
- **Przyciski w oknie Pobrane:** w trakcie – „Zatrzymaj”; po pobraniu, gdy trwa udostępnianie – „Otwórz folder” i „Zakończ udostępnianie”; na końcu – „Pokaż w folderze” (zaznacza pobrany plik), „Usuń plik” (kasuje pobrany plik albo folder torrenta z dysku, po potwierdzeniu, nigdy cały folder pobierania) i „Usuń z listy” (plik zostaje na dysku).
- **Przerwane pobieranie:** po zatrzymaniu albo błędzie Velivo pyta, czy usunąć z dysku częściowo pobrane pliki (z listą nazw). Usuwa tylko niedokończone pliki tego torrenta.
- **Po zamknięciu okna:** gdy Velivo działa w zasobniku, torrenty pobierają się dalej; zatrzymuje je dopiero „Zamknij całkowicie” (prawy klik na ikonce przy zegarze). Bez zasobnika zamknięcie Velivo zatrzymuje torrenty.
- **Ograniczenia:** w trybie bankowym torrenty nie działają. Twój adres IP widzą inni uczestnicy wymiany – dla anonimowości użyj VPN. Udostępniaj tylko materiały, do których masz prawo.

# 9. Czytnik i czytanie stron

### 9.1 Tryb czytania (Czytnik)
- **Do czego:** czytanie artykułu bez reklam, menu i rozpraszaczy.
- **Gdzie:** przycisk „Czytnik” na pasku; prawy klik → „Tryb czytania i streszczenie”.
- **Co robi:** otwiera sam tekst artykułu w osobnym, wygodnym oknie. Rozmiar okna jest zapamiętywany.

### 9.2 Wygląd czytnika
- **Jak używać:** lista „Wygląd czytnika”: ☀ Jasny, 🌙 Ciemny, 🌅 Nocny; w trybie nocnym suwak „Natężenie trybu nocnego”.

### 9.3 Lokalne streszczenie
- **Do czego:** szybkie poznanie sedna długiego artykułu.
- **Co robi:** Velivo wybiera najważniejsze zdania artykułu **na Twoim komputerze – bez sztucznej inteligencji i bez wysyłania treści do internetu** (liczą się słowa kluczowe, słowa z tytułu i początek artykułu).

### 9.4 Czytanie strony na głos
- **Gdzie:** przycisk czytania na pasku; Ctrl+Shift+U.
- **Co robi:** czyta główną treść strony; czytany akapit jest podświetlany i przewijany na środek ekranu, łatwo śledzić wzrokiem.
- **Treść jak w Czytniku:** czyta i Czytnik korzystają z tego samego wykrywania artykułu – czytany jest sam artykuł, bez bocznych kolumn, polecanych i dodatków.
- **Głos wg języka akapitu:** w tekście mieszanym (np. polski artykuł z angielskim cytatem) każdy akapit czytany jest głosem w swoim języku.
- **Reaguje na zmiany:** zmiana głosu lub prędkości w Ustawieniach działa od razu, bez ponownego włączania; gdy strona przejdzie do innego artykułu (np. kolejny tekst bez przeładowania), czytanie się zatrzymuje, zamiast czytać nieaktualną treść.

### 9.5 Czytanie zaznaczenia
- **Jak używać:** zaznacz tekst → przycisk czytania albo prawy klik → „Czytaj zaznaczenie na głos”.

### 9.6 Czytanie od wskazanego miejsca
- **Jak używać:** prawy klik w miejscu tekstu → „Czytaj od tego miejsca” – bez zaznaczania.

### 9.7 Pauza, wznowienie, stop
- **Jak używać:** klik w przycisk czytania (albo Ctrl+Shift+U) – pauza / wznów; „Zatrzymaj czytanie” – stop.

### 9.8 Prędkość czytania
- **Jak używać:** przycisk prędkości obok przycisku czytania – każde kliknięcie zmienia tempo.

### 9.9 Wybór głosu
- **Gdzie:** Ustawienia → „Czytanie na głos – głos i prędkość”.
- **Co robi:** „Automatycznie (język strony: polski/angielski)” dobiera głos do języka strony; do wyboru są też głosy Windows, naturalne głosy online oraz **naturalne głosy offline Piper** (rozdział 10).

### 9.10 „Gdzie ja to czytałem?”
- **Do czego:** znalezienie artykułu, który czytałeś, po słowach zapamiętanych z jego treści – a nie tylko po tytule.
- **Gdzie:** Ctrl+Shift+F; prawy klik → „Gdzie ja to czytałem?”; włączanie – Ustawienia → Prywatność → „Zapamiętuj treść przeczytanych stron”.
- **Jak używać:** wpisz kilka zapamiętanych słów → wybierz wynik strzałkami → Enter otwiera stronę.
- **Co robi:** Velivo zapisuje tekst przeczytanych stron **tylko na tym komputerze** i przeszukuje go lokalnie.
- **Ograniczenia:** pomijane są karty prywatne, banki, płatności, poczta i każda strona z polem hasła.
- **Co zapisuje:** tylko główną treść strony (bez menu, reklam i list linków); strony wyników wyszukiwarek (Google, Bing, DuckDuckGo…) są pomijane, żeby wyniki nie zaśmiecały pamięci.

### 9.11 Tłumaczenie stron i zaznaczenia
- **Gdzie:** prawy klik.
- **Co robi:** „Przetłumacz stronę na polski” (strona obca) albo „na angielski” (strona polska) oraz „Przetłumacz zaznaczenie na polski” – przez Tłumacza Google.
- **Ograniczenia:** tłumaczenie wymaga internetu i wysyła treść do Google.

# 10. Czytanie offline – głosy Piper

### 10.1 Naturalne głosy offline
- **Do czego:** czytanie stron na głos naturalnym głosem **bez internetu**.
- **Gdzie:** Ustawienia → Czytanie na głos → wybór głosu oznaczonego „naturalny offline”.
- **Co robi:** przy pierwszym wyborze Velivo jednorazowo pobiera program Piper i wybrany głos (ok. 60 MB na głos), z postępem w dymku. Potem czyta całkowicie offline: tekst dzielony jest na zdania, czytane zdanie jest podświetlane.

### 10.2 Dostępne głosy Piper w wersji 1.22
| Głos | Język |
|---|---|
| Gosia | polski |
| Darkman | polski |
| MC Speech | polski |
| Lessac | angielski (USA) |
| Ryan | angielski (USA) |
| Alba | angielski (Wielka Brytania) |
- **Ograniczenia:** w wersji 1.22 jest **6 głosów offline (3 polskie i 3 angielskie)**. Wariant z 8 głosami – **NIE DOSTĘPNE W 1.22**.

### 10.3 Bezpieczeństwo pobieranego programu Piper
- **Co robi:** pobrany program Piper jest sprawdzany sumą kontrolną SHA-256 zapisaną w Velivo; niezgodny plik jest odrzucany.

### 10.4 Gdzie leżą głosy
- `%LOCALAPPDATA%\Przegladarka\Piper` – można usunąć ten folder, by zwolnić miejsce; głos pobierze się ponownie przy kolejnym użyciu.

# 11. Prywatność

### 11.1 Tarcza prywatności
- **Gdzie:** przycisk tarczy na pasku.
- **Co robi:** pokazuje liczbę zablokowanych elementów na stronie i listę „Zablokowane na tej stronie”, a po kliknięciu paragon prywatności (11.8).

### 11.2 Blokowanie reklam Velivo
- **Gdzie:** przycisk „Włącz/wyłącz AdBlock”; Ustawienia → Blokowanie reklam.
- **Co robi:** blokuje reklamy i trackery na poziomie całych domen, zanim się wczytają. Wbudowany zestaw reguł działa od pierwszego uruchomienia.

### 11.3 Pełne listy filtrów
- **Gdzie:** Ustawienia → Blokowanie reklam → „Pełne listy filtrów (EasyList, EasyPrivacy, polska lista) – ok. 97 tys. reguł”.
- **Co robi:** listy **EasyList** (reklamy), **EasyPrivacy** (śledzenie) i **polska lista** pobierają się w tle i odświeżają co 4 dni. „Aktualizuj listy teraz” pobiera je od razu. Ustawienia pokazują liczbę list i reguł w użyciu.
- **Ograniczenia:** gdy pobranie się nie uda, Velivo używa poprzednich list.

### 11.4 Własne reguły blokowania (dla zaawansowanych)
- **Jak używać:** dopisz domeny lub reguły w formacie EasyList do pliku `filters.txt` w folderze danych i uruchom Velivo ponownie.

### 11.5 uBlock Origin Lite (wbudowany)
- **Gdzie:** Ustawienia → Bezpieczeństwo → „uBlock Origin Lite – wbudowany bloker reklam (zalecany)”, „Ustawienia uBlock Origin Lite…”.
- **Co robi:** popularny bloker open source (GPL-3.0) działa razem z blokadą Velivo; jego wyniki widać na tarczy. Raz w tygodniu Velivo sprawdza nowe wydanie i instaluje je tylko po zgodności sumy SHA-256 podanej przez GitHub.
- **Ograniczenia:** można go wyłączyć; jeśli zainstalowano go wcześniej ze sklepu, zostaje tamta wersja (bez dubli).

### 11.6 Blokowanie elementów strony
- **Do czego:** pływające reklamy, banery i okienka, które filtry przeoczyły.
- **Jak używać:** 1) prawy klik → „Blokuj element (reklamę)…”; 2) najedź – element podświetla się; 3) kółko myszy lub strzałki zmieniają zakres (większy / mniejszy fragment); 4) klik lub Enter blokuje; Esc anuluje.
- **Co robi:** element znika przy każdym wejściu na tę stronę. Dymek informuje, jak to cofnąć.
- **Cofnięcie:** prawy klik → „Przywróć zablokowane elementy na tej stronie”. Liczba ukrytych elementów jest widoczna na tarczy.

### 11.7 „Nie śledź” i Global Privacy Control
- **Gdzie:** Ustawienia → Prywatność → „Wysyłaj sygnały „Nie śledź” (DNT i Global Privacy Control)”.
- **Co robi:** strony dostają prośbę o niesprzedawanie i nieśledzenie Twoich danych.
- **Ograniczenia:** strony nie muszą respektować tej prośby.

### 11.8 Paragon prywatności
- **Do czego:** zobaczyć, co strona robi „za kulisami”.
- **Gdzie:** klik w tarczę; Ustawienia → „Paragon prywatności na tarczy”.
- **Co robi:** pokazuje, z iloma zewnętrznymi firmami i w ilu krajach łączyła się strona, czy byli wśród nich brokerzy danych, najczęstsze firmy oraz **próby rozpoznania komputera (fingerprintingu)**.

### 11.9 Fingerprinting – wykrywanie i raport
- **Co robi:** Velivo **wykrywa i raportuje** próby rozpoznania komputera: odczyt niewidocznego obrazu (canvas), odczyt modelu karty graficznej (WebGL) i odcisk dźwięku (audio). Wyniki widać w paragonie prywatności.
- **Ograniczenia:** Velivo **nie zmienia odczytywanych wartości** – nie „zaszumia” i nie ujednolica odcisku palca przeglądarki. Ochronę dają: blokowanie trackerów z list (strony fingerprintujące są często na listach), reguły domen i ukrycie, że Velivo jest „przeglądarką wbudowaną”.

### 11.10 Automatyczne odrzucanie banerów ciasteczek
- **Gdzie:** Ustawienia → Prywatność → „Automatycznie odrzucaj banery z ciasteczkami (RODO)”.
- **Co robi:** Velivo samo klika „Odrzuć” albo „Tylko niezbędne”. Gdy baner nie ma takiego przycisku, nic nie jest klikane – **Velivo nigdy nie klika „Akceptuj”**. Odrzucenie widać w dzienniku tarczy.
- **Wyjątek dla strony:** prawy klik → „Nie odrzucaj banerów ciasteczek na tej stronie” (powrót – „Odrzucaj banery…”).

### 11.11 Blokada wyskakujących okien
- **Gdzie:** Ustawienia → „Blokuj wyskakujące okna otwierane bez kliknięcia”.

### 11.12 Historia – zapisywanie i czyszczenie
- **Gdzie:** Ustawienia → Prywatność → „Zapisuj historię przeglądania” oraz „Czyść dane przy zamknięciu (historia i pamięć podręczna)”.
- **Co robi:** czyszczenie przy zamknięciu nie wylogowuje kont i nie usuwa zapisanych logowań.

### 11.13 Wyczyść dane przeglądania
- **Gdzie:** Ustawienia → Dane → „Wyczyść dane przeglądania teraz…”.
- **Jak używać:** zaznacz: Historia przeglądania, Pamięć podręczna (cache), Historia pobrań, Cookies i aktywne sesje (wyloguje konta), Dane formularzy i kart zapisane przez silnik, Zapisane hasła w silniku → „Wyczyść”.

### 11.14 Dziennik „Co zostało zablokowane i dlaczego”
- **Gdzie:** tarcza → „Panel prywatności i antyfingerprinting”.
- **Co robi:** lista blokad: godzina, domena, powód (np. „Tracker zablokowany (AdBlock)”, „Tracker zablokowany (reguła domeny)”, „Cookies usunięte dla domeny (reguła)”). Zaznaczenie wpisu pokazuje pełny adres i szczegóły. „Wyczyść panel blokad”.

### 11.15 Ukrywanie „przeglądarki wbudowanej”
- **Co robi:** Velivo przedstawia się stronom jak zwykły Microsoft Edge (ten sam silnik). Dzięki temu np. logowanie Google działa, a strony nie wiedzą, że to „przeglądarka wbudowana”.

### 11.16 Wykrywacz sztuczek presji w sklepach
- **Gdzie:** Ustawienia → Prywatność → „Ostrzegaj przed sztuczkami presji w sklepach”.
- **Co robi:** rozpoznaje fałszywe liczniki czasu, „ostatnie sztuki”, „X osób ogląda”, zaznaczone z góry dodatki (Velivo je **odznacza**) i ukryte opłaty, i pokazuje ostrzeżenie.

# 12. Poziomy ochrony i reguły dla stron

### 12.1 Ochrona przed śledzeniem – zrównoważona
- **Gdzie:** Ustawienia → Prywatność → „Ochrona przed śledzeniem” → „Ochrona zrównoważona (zalecana)”.
- **Co robi:** blokuje znane trackery, a osadzone treści (np. wpisy z X, filmy) działają.

### 12.2 Ochrona przed śledzeniem – ścisła
- **Co robi:** blokuje także osadzone treści serwisów społecznościowych.
- **Ograniczenia:** na zaufanych domenach ścisła działa jak zrównoważona, chyba że dla domeny zaznaczysz „Wymuś blokowanie trackerów”.

### 12.3 Panel prywatności i reguły domen
- **Gdzie:** tarcza → „Panel prywatności i antyfingerprinting”; Ustawienia → Dane → „Prywatność per-strona”.
- **Jak używać:** 1) wpisz domenę (np. `example.com` – można wkleić cały adres, zostaje sama domena); 2) zaznacz wybrane opcje (12.4–12.7); 3) „Zapisz regułę”. „Usuń regułę” kasuje ją.
- **Co robi:** reguła działa przy każdym wejściu na domenę i jest synchronizowana z Twoimi komputerami. Gdy strona ma regułę, na pasku widać „Prywatność*”.

### 12.4 Reguła: Blokuj JavaScript dla domeny
- **Co robi:** strona działa bez skryptów – szybciej i bez śledzenia skryptami.
- **Ograniczenia:** wiele nowoczesnych stron bez JavaScriptu nie działa poprawnie.

### 12.5 Reguła: Nie wysyłaj cookies dla domeny
- **Co robi:** strona nie dostaje Twoich ciasteczek.
- **Ograniczenia:** na tej stronie nie będziesz zalogowany.

### 12.6 Reguła: Wymuś blokowanie trackerów dla domeny
- **Co robi:** ścisła blokada trackerów także na domenie uznanej za zaufaną.

### 12.7 Reguła: Automatycznie czyść dane po wejściu na domenę
- **Co robi:** po wejściu na stronę jej dane (ciasteczka itp.) są usuwane – strona widzi Cię zawsze jak nowego gościa.

### 12.8 Zaufane domeny
- **Jak używać:** w panelu zaznacz wpis w dzienniku blokad → „Zaznaczoną domenę do zaufanych”, albo dodaj domenę do listy „Zaufane domeny”.
- **Co robi:** gdy strona przez blokadę działa źle, zaufanie przywraca jej działanie.

### 12.9 Poziomy bezpieczeństwa – podsumowanie
| Poziom | Co włączyć |
|---|---|
| Zwykły | ustawienia domyślne: zrównoważona ochrona, AdBlock, uBOL, odrzucanie banerów, wykrywanie podróbek, SmartScreen, HTTPS |
| Wyższy | ochrona ścisła, „Nie śledź”, czyszczenie danych przy zamknięciu, paragon prywatności |
| Najwyższy dla jednej strony | reguła domeny: blokuj JavaScript, bez cookies, wymuś trackery, czyść dane po wejściu |
| Banki i płatności | tryb bankowy (rozdział 14) + bezpieczne płatności |
| Bez śladu | karta prywatna |

# 13. Bezpieczeństwo

### 13.1 Wykrywanie fałszywych stron (bez internetu)
- **Gdzie:** Ustawienia → Bezpieczeństwo → „Wykrywaj fałszywe strony banków, sklepów i portali (działa bez internetu)”.
- **Co robi:** zanim strona się otworzy, Velivo sprawdza adres na Twoim komputerze i rozpoznaje:
  - **markę na obcej domenie** – np. `paypal-secure-login.com`, `ebay.co.uk.konto.top` (PayPal, eBay, Amazon, Facebook, Instagram, WhatsApp, Google, Gmail, Microsoft, Outlook, Apple, iCloud, X/Twitter, Netflix, Spotify, banki i inne);
  - **literówki i podmienione znaki** – `paypa1.com`, `rnicrosoft.com`, `amaz0n.com`;
  - **litery z innych alfabetów** – adresy `xn--` (punycode), które wyglądają jak prawdziwe;
  - **podróbki stron, do których masz zapisane hasła**.
- **Jak używać:** pojawia się ostrzeżenie „Uwaga – to może być fałszywa strona!” z wyjaśnieniem, do jakiej firmy adres się podszywa. Domyślna odpowiedź to **Nie**. Jeśli to prawdziwa strona, wybierz „Tak” – Velivo ją zapamięta.

### 13.2 SmartScreen
- **Gdzie:** Ustawienia → „Ostrzegaj przed niebezpiecznymi stronami i plikami (SmartScreen)”.
- **Co robi:** ochrona Microsoft przed wyłudzeniami i złośliwymi plikami.
- **Ograniczenia:** sprawdzanie wysyła adresy do Microsoft.

### 13.3 Zawsze szyfrowane połączenie (HTTPS)
- **Gdzie:** Ustawienia → „Zawsze szyfrowane połączenie (HTTPS) – ostrzegaj przed stronami bez szyfrowania”.
- **Co robi:** strony bez szyfrowania otwierają się dopiero po ostrzeżeniu.

### 13.4 Hasła tylko dla prawdziwej domeny
- **Co robi:** zapisane hasła są podawane wyłącznie stronie o tej samej domenie, dla której je zapisano – nigdy na podstawie podobnej nazwy. Podróbka strony nie dostanie hasła.

### 13.5 Bezpieczne płatności – blokada nagrywania ekranu
- **Gdzie:** Ustawienia → „Bezpieczne płatności – na stronach banków i płatności ukrywaj okno przed programami nagrywającymi ekran”.
- **Co robi:** na stronach banków i płatności (PayPal, Monzo, Revolut, Barclays, HSBC, Lloyds, NatWest, Santander, PKO, mBank, Stripe i inne) okno Velivo jest **niewidoczne dla programów nagrywających ekran** i zrzutów ekranu; pojawia się komunikat 🛡.
- **Ograniczenia:** na tych stronach Ty także nie zrobisz zrzutu ekranu.

### 13.6 Ochrona danych płatniczych
- **Co robi:** kod CVC karty **nigdy nie jest zapisywany** w autouzupełnianiu; przed wpisaniem karty i konta bankowego Velivo pyta i pokazuje adres strony.

### 13.7 Formularze odporne na ataki
- **Co robi:** Velivo wypełnia tylko pola naprawdę widoczne dla człowieka (strona nie wyciągnie danych ukrytym polem), działają tylko prawdziwe kliknięcia myszą, a pomocnika Velivo strona nie może podmienić.

### 13.8 Zamknięty kanał między stroną a Velivo
- **Co robi:** strony nie mają dostępu do wewnętrznego kanału Velivo. Polecenia (np. gesty, zapis hasła) przyjmowane są tylko z tajnym znacznikiem uruchomienia, którego strona nie zna.

### 13.9 Znacznik „plik z internetu” i sprawdzanie narzędzi
- Opisane w 8.10 i 8.15.

### 13.10 Ochrona przed zamknięciem okna przez stronę
- Opisane w 2.10.

# 14. Tryb bankowy (Banking Mode)

### 14.1 Czym jest tryb bankowy
- **Do czego:** bezpieczne bankowanie i zakupy w osobnym, zamkniętym środowisku.
- **Co robi:** to osobny, odizolowany profil przeglądarki z **własną zaszyfrowaną bazą**: własne logowania i ciasteczka, **bez dodatków i bez historii**. Zwykłe karty nic z niego nie widzą i odwrotnie. Karty bankowe są **zielone** i mają ikonę 🏦.

### 14.2 Przycisk 🏦 i menu trybu
- **Gdzie:** zielony przycisk 🏦 na pasku kart. Klik – otwiera tryb; **prawy klik – menu** ze wszystkimi funkcjami opisanymi niżej.

### 14.3 Pierwsze uruchomienie trybu
- **Jak używać:** 1) kliknij 🏦; 2) ustaw **hasło** (min. 8 znaków) i powtórz je; 3) opcjonalnie dodaj klucz sprzętowy (14.4); 4) wybierz czas automatycznej blokady; 5) „Zapisz”.

### 14.4 Klucz sprzętowy FIDO2 (YubiKey, Google Titan)
- **Do czego:** silniejsza ochrona – do otwarcia potrzebny fizyczny klucz.
- **Jak używać:** zaznacz „Dodatkowo wymagaj klucza sprzętowego”, włóż klucz, kliknij „➕ Dodaj klucz (włóż go i dotknij)” – Windows poprosi o dotknięcie **dwa razy**. Dodaj w tym samym okienku wszystkie swoje klucze (warto mieć zapasowy), potem „Zapisz”. Klucze NFC/Bluetooth Windows znajdzie po kliknięciu „Dodaj”.
- **Co robi:** klucz oznaczony 🔐 **sam otwiera tryb i szyfruje bazę** – bez niego danych nie da się odczytać nawet po skopiowaniu plików.
- **Ograniczenia:** klucz musi obsługiwać FIDO2 z podpisem ES256 i rozszerzeniem hmac-secret.

### 14.5 Otwieranie trybu
- **Co robi:** klucz w porcie → Velivo od razu prosi o dotknięcie, hasło niepotrzebne. Bez klucza → wpisujesz hasło.

### 14.6 Automatyczna blokada
- **Co robi:** po bezczynności (do wyboru 1–60 min, domyślnie 10 min) tryb blokuje się sam i zamyka wszystkie swoje okienka.

### 14.7 Zablokuj teraz
- **Gdzie:** menu 🏦 → „Zablokuj teraz”.

### 14.8 Czyszczenie po zamknięciu trybu
- **Co robi:** po zamknięciu trybu znika pamięć podręczna i historia profilu bankowego. Logowania i „zapamiętaj mnie” zostają.

### 14.9 Szyfrowanie bazy
- **Co robi:** dane trybu (banki, sklepy, karty, notatki, dokumenty, konta, rachunki, loginy, historia płatności) są szyfrowane silnym szyfrem AES-256 z kluczem wyprowadzonym z hasła (600 000 powtórzeń funkcji PBKDF2) i opcjonalnie z klucza sprzętowego.

### 14.10 Moje banki
- **Gdzie:** menu 🏦 → „Moje banki”.
- **Jak używać:** klik na nazwie otwiera stronę banku w trybie bankowym. Dodawanie: na otwartej karcie bankowej „Dodaj tę stronę do Moich banków” albo na liście „Dodaj bank (nazwa i adres)…”.

### 14.11 Moje sklepy online
- Tak samo jak 14.10, menu „Moje sklepy online” i „Dodaj tę stronę do Moich sklepów”.

### 14.12 Dane logowania banku
- **Gdzie:** w „Moich bankach” – „Dane logowania”.
- **Co robi:** przechowuje osobno dla każdego banku: login / numer klienta, passcode / PIN, hasło i memorable information. Wpisuj zawsze **pełne** hasło i passcode.

### 14.13 Wpisz login
- **Co robi:** wypełnia login i hasło na stronie banku z danych tego banku.

### 14.14 Wpisz wybrane znaki
- **Do czego:** banki, które proszą np. o 2., 5. i 9. znak hasła (RBS, NatWest, Bank of Scotland, TSB, Lloyds, Halifax).
- **Gdzie:** menu 🏦 → „Wpisz wybrane znaki (passcode / hasło)”.
- **Co robi:** Velivo samo odczytuje z formularza, o które znaki prosi bank, i wpisuje właściwe – także w listach wyboru.

### 14.15 Skopiuj opis formularza
- **Gdzie:** menu 🏦 → „Skopiuj opis formularza logowania (bez Twoich danych)”.
- **Do czego:** gdy wpisywanie na jakiejś stronie się nie uda – kopiuje sam opis pól (bez danych) do zgłoszenia problemu.

### 14.16 Moje karty
- **Gdzie:** menu 🏦 → „Moje karty”.
- **Jak używać:** „Dodaj kartę”: nazwa (np. „Visa PKO”), numer (sprawdzany), ważna do (MM/RR), imię i nazwisko na karcie, CVV (opcjonalne, 3–4 cyfry). Klik na karcie pokazuje i pozwala zmienić dane; „Pokaż numer i CVV” odsłania ukryte pola; „Kopiuj numer / datę / CVV” – schowek czyści się sam po 30 s.
- **Co robi:** karty są zaszyfrowane. Przy otwarciu trybu Velivo przypomina o kartach, które niedługo wygasają.

### 14.17 Wypełnij kartę na tej stronie
- **Gdzie:** menu 🏦 → „Wypełnij kartę na tej stronie”.
- **Co robi:** wpisuje dane karty w formularzu płatności.

### 14.18 Moje loginy i hasła (w trybie)
- **Gdzie:** menu 🏦 → „Moje loginy i hasła”.
- **Co robi:** dla dowolnych usług (poczta, Netflix, urzędy): nazwa, login, hasło, adres strony, notatka. „Otwórz stronę”; „Wpisz na otwartej stronie” – **tylko na stronie o tym samym adresie**, więc hasło nie trafi na podróbkę; „Wygeneruj hasło” – mocne, 20-znakowe, kopiowane (schowek czyszczony po 30 s).

### 14.19 Poufne dane (dokumenty)
- **Gdzie:** menu 🏦 → „Poufne dane (dokumenty)”.
- **Co robi:** rodzaj (Dowód osobisty, Paszport, Prawo jazdy, PESEL, NI number, Ubezpieczenie zdrowotne, Inny), imię i nazwisko, numer (ukryty), ważny do, notatka. Velivo **przypomina o dokumentach wygasających w ciągu 60 dni**.

### 14.20 Rachunki bankowe
- **Gdzie:** menu 🏦 → „Rachunki bankowe”.
- **Co robi:** Twoje konta i konta odbiorców (np. właściciel mieszkania): nazwa, właściciel, numer / IBAN (ukryty), sort code / BIC (SWIFT), bank, tytuł przelewu. Przy każdym polu 📋 – bezbłędne przepisywanie do przelewu.

### 14.21 Moje notatki
- **Gdzie:** menu 🏦 → „Moje notatki (loginy, hasła, numery klienta)”.
- **Jak używać:** wybierz kategorię (Login, PIN, Przelewy, Kody odzyskiwania, Inne), wpisz tytuł i treść, „Zapisz”. „Wygeneruj hasło”, „Znaki z numerami” (hasło znak po znaku z numerami), 📋 kopiowanie.
- **Co robi:** okienko notatek nie blokuje strony – możesz przepisywać do formularza banku.

### 14.22 Wpisz login i hasło (dane skojarzone ze stroną)
- **Gdzie:** menu 🏦 → „Wpisz login i hasło na tej stronie”; na stronie z Twojej bazy – samo.
- **Co robi:** bierze konta z Moich loginów i haseł oraz z Moich banków dla tej strony. Jedno konto – login, hasło i wybrane znaki wpisują się same (formularza Velivo nie wysyła); kilka kont – wybór jednym kliknięciem. Gdy kont brak – lista notatek (linie `login: …` i `hasło: …`).
- **Ustawienia:** „Wpisuj dane z bazy samo…” w Ustawieniach trybu bankowego (synchronizowane).
- **Izolacja:** tylko baza trybu bankowego – zwykłe hasła przeglądarki nie są tu używane; dane trafiają tylko do kart bankowych i tylko na stronę, dla której je wybrano.
- **🛡 Strażnik przelewu:** wklejony / wpisany numer rachunku – suma kontrolna IBAN / NRB i porównanie z Rachunkami bankowymi (odbiorca z bazy / ostrzeżenie / błędny numer).
- **💸 Wypełnij przelew:** menu 🏦 albo propozycja na formularzu przelewu – odbiorca, numer (NRB, IBAN, sort code + 8 cyfr), tytuł; kwota i numer klienta z Rachunku do opłacenia o tej samej nazwie. Zawsze po kliknięciu.
- **💬 Propozycja pod polem** – klikasz w login, hasło, wybrane znaki, numer karty albo numer konta odbiorcy: gdy baza ma pasujące dane dla tej strony, tuż pod polem pojawia się wybór (jedno kliknięcie wypełnia); nic nie pasuje – nic się nie pokazuje. Strona nie widzi nazw Twoich kont.
- **Menu tylko z pasującymi danymi** – pozycje wypełniania w menu 🏦 pojawiają się tylko wtedy, gdy baza ma dane dla otwartej strony.
- **Bezpieczne wypełnianie** – pełne hasło nigdy nie trafia do pól po jednym znaku (tam idą tylko wybrane znaki), a dane karty wpisywane są wyłącznie w widoczne pola – ukryte pola strony nic nie dostaną.

### 14.23 Szukaj w mojej bazie
- **Gdzie:** menu 🏦 → „Szukaj w mojej bazie…”.
- **Co robi:** jedno pole dla banków, sklepów, kart i notatek; okienko nie blokuje strony.

### 14.24 Rachunki do opłacenia
- **Do czego:** pilnowanie stałych i jednorazowych opłat.
- **Gdzie:** menu 🏦 → „Rachunki do opłacenia”.
- **Pola:** Za co (lista nazw z arkusza), Kwota, Termin płatności, Powtarzanie, Przypominaj dni wcześniej, Numer klienta / referencja, Strona do płatności, Notatka.
- **Jak używać:** wypełnij pola → „Dodaj jako nową”; „Zmień zaznaczoną”, „Usuń zaznaczoną”, „Wyczyść pola”; na końcu **„Zapisz”** (dopiero wtedy zmiany trafiają do zaszyfrowanej bazy).

### 14.25 Rachunki cykliczne
- **Co robi:** powtarzanie: Co tydzień, Co 2 tygodnie, Co 4 tygodnie, Co miesiąc, Co 2 miesiące, Co kwartał, Co pół roku, Co rok, Jednorazowo – albo wpisz własne, np. „co 10 dni”, „co 3 tygodnie”.

### 14.26 Terminy i przypomnienia
- **Co robi:** przy otwarciu trybu Velivo pokazuje rachunki z terminem w ciągu kilku dni i po terminie – **codziennie, aż oznaczysz je jako zapłacone**.

### 14.27 Oznaczanie jako zapłacone
- **Jak używać:** zaznacz rachunek → „Zapłacone (następny termin)”.
- **Co robi:** płatność trafia do historii (do miesiąca terminu) i do arkusza, termin przesuwa się zgodnie z powtarzaniem, a rachunek jednorazowy znika z listy.
- **Ograniczenia:** Velivo nie łączy się z bankiem – nie wie samo, że zapłaciłeś. Dwa kliknięcia w tym samym miesiącu sumują kwotę (popraw w arkuszu).

### 14.28 Otwórz stronę płatności
- **Co robi:** otwiera adres z pola „Strona do płatności” w karcie bankowej.

### 14.29 Nazwy rachunków z arkusza
- **Co robi:** pole „Za co” podpowiada nazwy kolumn z arkusza – wybieraj z listy, a „Zapłacone” trafi do właściwej kolumny. Inna nazwa (nawet literówka) utworzy nową kolumnę.

### 14.30 Dodaj rachunki z arkusza
- **Co robi:** tworzy brakujące rachunki z kolumn arkusza: kwota z ostatniego miesiąca, powtarzanie co miesiąc, termin 1. dnia następnego miesiąca. **Popraw potem dzień terminu** i kliknij „Zapisz”.

### 14.31 Arkusz rachunków
- **Do czego:** zestawienie wydatków jak w Excelu.
- **Gdzie:** menu 🏦 albo „Rachunki do opłacenia” → „Arkusz rachunków (w karcie)”.
- **Co robi:** wiersz = miesiąc, kolumna = rachunek; kolumna „Razem” = suma miesiąca, wiersz „Razem” = suma rachunku, w rogu suma całości – liczone na bieżąco. Arkusz działa lokalnie, bez internetu.

### 14.32 Arkusz – wpisywanie kwot i miesięcy
- **Jak używać:** klik w komórkę i wpisz kwotę. Miesiąc wpisujesz słownie lub liczbowo: „październik 2026”, „October 2026”, „10.2026”, „2026-10”.

### 14.33 Arkusz – nowy miesiąc
- **Jak używać:** „Wiersz (miesiąc)” – dodaje miesiąc po najpóźniejszym w tabeli (po wrześniu jest październik) i kopiuje kwoty z ostatniego wiersza (stałych opłat nie trzeba wpisywać od nowa).

### 14.34 Arkusz – rachunki (kolumny)
- **Jak używać:** „Kolumna (rachunek)” – nowa kolumna; nazwę zmieniasz klikając w nagłówek; ✕ pod nazwą usuwa kolumnę; ✕ na końcu wiersza usuwa miesiąc.

### 14.35 Arkusz – import z Excela (CSV)
- **Jak używać:** w Excelu: Plik → Zapisz jako → CSV; w arkuszu „Wczytaj z Excela (CSV)”. Układ pliku: **pierwsza kolumna = miesiąc, pierwszy wiersz = nazwy rachunków**.
- **Co robi:** rozpoznaje separator (średnik, przecinek, tabulator); pomija kolumny i wiersze „Razem/Suma/Total”; uzupełnia istniejące miesiące (puste komórki nie kasują Twoich kwot); dodaje nowe rachunki jako kolumny; układa miesiące po kolei; pokazuje liczbę wczytanych wierszy.

### 14.36 Arkusz – waluta
- **Jak używać:** lista „Waluta, w której wpisujesz kwoty”: £, zł, €, $, CHF, kr, Kč, Ft, lei, ₴, ¥.
- **Co robi:** zmienia **tylko symbol** – kwoty **nie są przeliczane** po kursie. Domyślnie waluta z ustawień regionalnych Windows; wybór zapamiętuje się przy zapisie.

### 14.37 Arkusz – zapis
- **Jak używać:** „Zapisz i zamknij” – dane szyfrują się w bazie, karta się zamyka; wiersze bez kwot i bez poprawnego miesiąca są pomijane (z informacją). „Anuluj” – bez zapisu.
- **Co robi:** arkusz przyjmuje dane tylko od siebie – gdy w jego karcie otworzysz inną stronę, Velivo kończy bez zapisu.

### 14.38 Zestawienie płatności
- **Gdzie:** „Rachunki do opłacenia” → „Zestawienie płatności”.
- **Co robi:** okno z tabelą miesięcy i rachunków: ręczne dopisanie płatności (rachunek, miesiąc, kwota), „Usuń płatności tego rachunku z miesiąca”, „Wczytaj z Excela (CSV)…”, „Zapisz do Excela (CSV)” (plik `Velivo-rachunki.csv`), wybór waluty.

### 14.39 Ostrzeżenie o stronie podobnej do Twojego banku
- **Co robi:** gdy przy otwartym trybie wejdziesz na stronę podobną do Twojego banku (np. inny adres z nazwą banku), pojawi się duże ostrzeżenie.

### 14.40 Propozycja trybu bankowego
- **Co robi:** gdy bank, sklep lub stronę płatności z listy otworzysz w **zwykłej** karcie, nad paskiem zadań pojawi się pytanie „Przełącz na tryb bankowy” / „Zostań tutaj”. Rozpoznawanie działa po domenie głównej, także dla innych adresów tej samej firmy.

### 14.41 Dziennik otwarć
- **Gdzie:** menu 🏦 → „Dziennik otwarć…”.
- **Co robi:** kiedy, na którym komputerze i czym (hasło / klucz) otwarto tryb – także **nieudane próby ze złym hasłem**.

### 14.42 Profile bankowe
- **Gdzie:** menu 🏦 → „Profile bankowe” → „Nowy profil bankowy (inny użytkownik)…”.
- **Co robi:** osobny tryb bankowy dla innej osoby (np. domownika): własne hasło lub klucz, osobne logowania i dane.

### 14.43 Ustawienia trybu bankowego
- **Gdzie:** menu 🏦 → „Ustawienia trybu bankowego…”: nowe hasło (puste = bez zmiany), dodawanie i usuwanie kluczy („Usuń zaznaczoną”, „Usuń wszystkie klucze”), czas blokady.
- **Wpisuj dane z bazy samo** – na stronie z bazy (jedno pasujące konto) login, hasło i wybrane znaki wpisują się same; wyłączone = wszystko po kliknięciu. Karta płatnicza – zawsze po kliknięciu.

### 14.44 Zapomniane hasło
- **Gdzie:** okno logowania trybu → „Zapomniałem hasła – wyczyść tryb bankowy…”.
- **Co robi:** bez hasła i bez klucza danych nie da się odczytać – ta opcja usuwa tryb i jego dane na tym komputerze (logowania, ciasteczka), a Ty ustawiasz go od nowa. Dlatego warto mieć kopię zapasową i drugi klucz.

### 14.45 Instrukcja trybu w programie
- **Gdzie:** menu 🏦 → „Instrukcja trybu bankowego” – pełny opis po polsku albo angielsku.

# 15. Sejf (zewnętrzny menedżer haseł)

### 15.1 Loginy z Sejfu na stronach logowania
- **Do czego:** korzystanie z haseł zapisanych w programie Sejf bez otwierania jego okna.
- **Gdzie:** Ustawienia → Prywatność → „Loginy z Sejfu: kluczyk na pasku na stronach logowania”.
- **Co robi:** gdy strona ma pole hasła, Velivo pyta Sejf o loginy **dla prawdziwej domeny karty**. Na pasku pojawia się kluczyk; kliknięcie wypełnia formularz. Przed wypełnieniem Velivo sprawdza, że karta nadal jest na tej stronie.
- **Ograniczenia:** wymaga zainstalowanego programu Sejf (w Ustawieniach widać „(nie znaleziono Sejfu)”, gdy go brak). Sejf może poprosić o odblokowanie – Velivo czeka do minuty.

### 15.2 Okienko Sejfu w Szybkim Dostępie
- **Co robi:** okienko z listą wpisów Sejfu: „Wybierz wpis i wypełnij formularz”, zapis nowego loginu w zaszyfrowanym sejfie („Zapisuję wpis w zaszyfrowanym sejfie…”, „Login zapisany w sejfie”).

### 15.3 Wypełnianie formularzy z Sejfu
- **Co robi:** ta sama logika wypełniania co w Velivo: tylko widoczne pola, tylko właściwa domena („Formularz wypełniony. Sprawdź dane i zaloguj się.”).

# 16. Hasła i dane

### 16.1 Menedżer haseł Velivo
- **Gdzie:** Ustawienia → Prywatność → „Menedżer haseł lokalnych…”.
- **Co robi:** lokalna baza haseł zaszyfrowana kluczem Twojego konta Windows. Kolumny: Nazwa, Domena, Użytkownik, Źródło, Zmieniono; licznik wpisów.

### 16.2 Szukanie haseł
- **Jak używać:** pole „Szukaj po domenie, loginie lub nazwie”.

### 16.3 Dodawanie i edycja wpisu
- **Jak używać:** „Dodaj” / „Edytuj”: domena, użytkownik, hasło, nazwa, notatki; „Pokaż/ukryj hasło”, „Wklej ze schowka”, „Generuj mocne hasło”.

### 16.4 Generator mocnych haseł
- **Co robi:** losowe hasło z małymi i wielkimi literami, cyframi i symbolami (losowość kryptograficzna).

### 16.5 Kopiowanie i usuwanie
- **Jak używać:** „Kopiuj login”, „Kopiuj hasło”, „Usuń zaznaczone”, „Usuń wszystkie zapisane hasła” (z potwierdzeniem).

### 16.6 Propozycja zapisu hasła
- **Gdzie:** Ustawienia → „Proponuj zapisywanie haseł”.
- **Co robi:** po zalogowaniu Velivo pyta „Wykryto hasło na stronie … Dodać do bazy Velivo?” albo przy zmianie hasła „Zaktualizować wpis?”.

### 16.7 Wpisywanie zapisanego loginu
- **Co robi:** na stronie logowania „Wpisz zapisany login i hasło z Velivo”; przy kilku kontach „Wybierz konto do wpisania” albo „Inne konto z bazy Velivo…” z wyszukiwaniem.
- **Bezpieczeństwo:** tylko dla tej samej domeny (13.4).

### 16.8 Autouzupełnianie formularzy
- **Gdzie:** Ustawienia → „Autouzupełnianie formularzy (adresy i karty, lokalna szyfrowana baza)”.
- **Co robi:** klik w puste pole wypełnia cały formularz: **adres** (imię, nazwisko, ulica, kod, miasto, telefon, e-mail), **karta** (bez CVC), **konto bankowe** (IBAN, numer konta, sort code). Działa na każdej stronie https, także w kartach prywatnych. Kartę i konto wpisuje dopiero po potwierdzeniu adresu strony.

### 16.9 Zapisywanie danych formularza
- **Co robi:** po wysłaniu formularza Velivo pyta „Wykryto wypełniony formularz adresowy” / „Wykryto dane karty płatniczej” – zapis zawsze dopiero po Twojej zgodzie.

### 16.10 Zarządzanie danymi autouzupełniania
- **Gdzie:** Ustawienia → „Pokaż zapisane dane…”, „Usuń zapisane karty”, „Usuń zapisane adresy”.

# 17. LAN Sync – synchronizacja w sieci domowej

### 17.1 Synchronizacja bez chmury
- **Do czego:** te same zakładki, hasła i ustawienia na kilku komputerach w domu – bez konta i chmury.
- **Gdzie:** Ustawienia → Synchronizacja → „Włącz synchronizację między uruchomionymi Velivo w tej samej sieci lokalnej”.
- **Co robi:** komputery z tym samym profilem Velivo w jednej sieci wymieniają dane bezpośrednio. Otwarte karty zostają na każdym komputerze osobno.
- **Ograniczenia:** zapora Windows musi przepuszczać Velivo; czas Windows ustawiony automatycznie na obu komputerach.

### 17.2 Przed sparowaniem – żadnych danych
- **Co robi:** dopóki komputery nie są sparowane, Velivo tylko informuje sieć, że jest dostępne do sparowania (identyfikator, nazwa komputera, profil). **Nie wysyła i nie przyjmuje żadnych Twoich danych.** W panelu diagnostycznym widać „LAN sync czeka na sparowanie – dane nie są wysyłane”.

### 17.3 Propozycja połączenia
- **Co robi:** gdy w sieci pojawi się drugi Velivo z tym samym profilem, jeden z komputerów pyta: „W sieci jest drugi Velivo… Połączyć oba komputery i synchronizować wszystko?”.

### 17.4 Parowanie urządzeń
- **Jak używać:** 1) włącz i zapisz synchronizację na obu komputerach; 2) na jednym kliknij „Sparuj urządzenie w sieci…” (albo zaakceptuj propozycję); 3) na **obu ekranach** pojawia się ten sam **6-cyfrowy kod** – porównaj i potwierdź na obu; 4) gotowe.
- **Co robi:** komputery uzgadniają tajny klucz bez przesyłania go jawnie; kod chroni przed podszyciem się innego urządzenia. Klucz zapisuje się lokalnie i jest chroniony kluczem konta Windows.
- **Ograniczenia:** prośby z innego profilu są odrzucane, niedokończone parowanie wygasa.

### 17.5 Pierwsze połączenie – czyje ustawienia
- **Co robi:** przy pierwszym połączeniu wybierasz, czyje **ustawienia** zostają. Zakładki, hasła i Szybki Dostęp i tak są łączone z obu.

### 17.6 Szyfrowanie synchronizacji
- **Co robi:** każdy pakiet jest szyfrowany (AES-256) i podpisany; komputer bez klucza nie odczyta ani nie podrobi danych. Pakiety stare lub z innym czasem (różnica ponad 5 minut) są odrzucane – ochrona przed powtórzeniem.

### 17.7 Co się synchronizuje
| Dane | Jak |
|---|---|
| Ustawienia | wygrywa nowsza zmiana; lokalne zostają: głośniki, foldery, język, rozmiary okien |
| Zakładki | łączone z obu; usunięcia przenoszone (przez 90 dni) |
| Hasła | łączone |
| Reguły prywatności, profile | wygrywa nowsza zmiana |
| Dodatki | brakujące instalowane automatycznie ze sklepu |
| Przypięte karty | otwierane i przypinane |
| Historia | łączona; usunięcia i „wyczyść wszystko” przenoszone |
| Szybki Dostęp z grafikami | łączony, przesyłany szyfrowanym połączeniem |
| Tryb bankowy (wszystkie profile) | zaszyfrowany; logowania do banków zostają lokalnie |
| Otwarte karty | **nie** – służy do tego „Wyślij do…” |

### 17.8 Izolacja profili przy synchronizacji
- **Co robi:** synchronizują się tylko komputery z **tym samym profilem**. Dane profilu A nigdy nie trafiają do profilu B. Gdy drugi komputer przełączy się na inny profil, Velivo pyta, czy też się przełączyć.

### 17.9 Wyślij kartę na inny komputer
- **Gdzie:** menu karty → „Wyślij do…” → wybierz komputer.
- **Co robi:** karta otwiera się na drugim komputerze; film YouTube rusza od tego samego miejsca. Przesyłanie jest zaszyfrowane.

### 17.10 Wskaźnik w zasobniku
- **Co robi:** ikona Velivo przy zegarze **pulsuje** przy wysyłaniu i odbieraniu zmian.

### 17.11 Powiadomienia o synchronizacji
- **Co robi:** dymki „Zsynchronizowano z Velivo w sieci lokalnej” oraz szczegóły (ustawienia przyjęte / tu nowsze, zakładki połączone).

### 17.12 Tryb cichy
- **Gdzie:** Ustawienia → Synchronizacja → „Tryb cichy LAN (bez dymków…)”. Log i panel działają dalej.

### 17.13 Panel diagnostyczny LAN
- **Gdzie:** Ustawienia → „Panel diagnostyczny LAN…”; Narzędzia Velivo → „Diagnostyka LAN sync”.
- **Co robi:** status (wyłączona / aktywna, ale brak połączonych urządzeń / połączono z N urządzeniami), wykryte urządzenia, liczniki pakietów i błędów, czasy ostatniej wymiany, log zdarzeń; „Odśwież teraz”, „Wyczyść log”.

### 17.14 Ostrzeżenia i błędy
- **Co robi:** Velivo informuje o różnicy zegarów („zegar różni się o N min – ustaw automatyczny czas”), za dużym pakiecie danych, niepełnym stanie i błędach połączenia.

### 17.15 Plik odzyskiwania parowania
- **Gdzie:** Ustawienia → Synchronizacja → „Zapisz plik odzyskiwania…” / „Odtwórz parowanie…”.
- **Co robi:** zapisuje zaszyfrowany hasłem (min. 12 znaków) plik z kluczem parowania. Po reinstalacji Windows odtwarzasz parowanie bez ponownego parowania.

### 17.16 Zostań w zasobniku – synchronizacja w tle
- Opis w 25.17.

# 18. Profile i izolacja użytkowników

### 18.1 Profile użytkowników Velivo
- **Do czego:** osobne środowisko dla każdej osoby lub zastosowania (np. „Praca”, „Prywatny”, drugie konto Google).
- **Gdzie:** przycisk aktywnego profilu na pasku; Ustawienia → Profile → „Zarządzaj użytkownikami/profilami…”.
- **Co robi:** każdy profil ma **osobny folder danych**: własne logowania, ciasteczka, historię, hasła, zakładki, ustawienia i Szybki Dostęp.

### 18.2 Dodanie profilu
- **Jak używać:** „Dodaj użytkownika/profil…” → nazwa (np. `google-konto2`; litery, cyfry, `-`, `_`).

### 18.3 Przełączanie profilu
- **Jak używać:** przycisk profilu → wybór; w oknie profili „Przełącz na zaznaczony”; z menu Narzędzia Velivo → „Przełącz użytkownika/profil…” albo szybkie „Przełącz profil: praca”.
- **Co robi:** Velivo uruchamia się ponownie na wybranym profilu.

### 18.4 Ikona profilu
- **Jak używać:** w oknie profili „Ikona profilu” → wybór → „Zapisz ikonkę”. Ikona jest widoczna na przycisku profilu.

### 18.5 Usunięcie profilu
- **Jak używać:** „Usuń zaznaczony profil” – pyta i usuwa profil razem z jego lokalnymi danymi.
- **Ograniczenia:** nie można usunąć aktywnego ani domyślnego profilu.

### 18.6 Izolacja danych
- **Co robi:** dane profili są rozdzielone na dysku i podczas synchronizacji LAN (17.8). Tryb bankowy jest dodatkowo odizolowany od zwykłych kart (14.1), a karty prywatne mają własny, ulotny magazyn (4.2).

### 18.7 Profile bankowe
- Osobne tryby bankowe dla innych osób – 14.42.

### 18.8 Profile Szybkiego Dostępu
- Osobne zestawy skrótów z PIN-em – 21.6.

### 18.9 Uruchamianie z wybranym profilem (dla zaawansowanych)
- **Jak używać:** skrót do Velivo z parametrem `--profil nazwa`.

# 19. Pamięć podręczna i relokacja danych tymczasowych

### 19.1 Śmieci przeglądarki – czym są
- **Co robi:** pamięć podręczna stron, skompilowane skrypty i pamięć grafiki. Można je usuwać **bez utraty logowań i danych dodatków** – to nie są Twoje dane.

### 19.2 Folder na śmieci (relokacja cache)
- **Do czego:** przeniesienie pamięci podręcznej na inny dysk, np. RAM dysk albo szybszy/większy dysk.
- **Gdzie:** Ustawienia → Śmieci przeglądarki → „Folder na śmieci (puste = w profilu przeglądarki)” → „Wybierz…”.
- **Co robi:** Velivo tworzy w wybranym folderze własny podfolder i **czyści tylko jego zawartość**, nigdy nic innego z tego dysku. Nowy folder działa po ponownym uruchomieniu.
- **Ograniczenia:** pamięć grafiki silnik zawsze trzyma w profilu przeglądarki (wymóg silnika). „Domyślny” przywraca zwykłą lokalizację.

### 19.3 Usuwaj śmieci przy każdym uruchomieniu
- **Gdzie:** Ustawienia → „Usuwaj śmieci przy każdym uruchomieniu przeglądarki”.
- **Ograniczenia:** strony za pierwszym razem wczytają się odrobinę wolniej.

### 19.4 Wyczyść śmieci teraz
- **Jak używać:** Ustawienia → „Wyczyść śmieci teraz”. Velivo pokazuje, ile zwolniono i ile teraz zajmują. Pamięć grafiki zniknie przy następnym uruchomieniu.

### 19.5 Rozmiar pamięci podręcznej
- **Gdzie:** Ustawienia → Szybkość wczytywania stron → „Rozmiar pamięci podręcznej (po ponownym uruchomieniu)”: Automatycznie albo wybrany rozmiar.

### 19.6 Bezpieczne sprzątanie
- **Co robi:** śmieci są usuwane tylko wtedy, gdy nie działa inne okno Velivo (silnik trzyma te pliki).

### 19.7 Osobny folder danych (dla zaawansowanych)
- **Co robi:** zmienna środowiskowa `PRZEGLADARKA_DANE` uruchamia Velivo na całkiem osobnym folderze danych – np. do testów.

# 20. RAM dysk

### 20.1 Cache na RAM dysku
- **Do czego:** szybsze wczytywanie stron i oszczędzanie dysku SSD.
- **Jak używać:** 1) utwórz RAM dysk dowolnym programem (np. ImDisk, SoftPerfect RAM Disk); 2) Ustawienia → „Folder na śmieci” → „Wybierz…” → np. `R:\`; 3) zapisz i uruchom Velivo ponownie; 4) opcjonalnie „Usuwaj śmieci przy każdym uruchomieniu”.
- **Co robi:** strony z pamięci podręcznej wczytują się z RAM; SSD dostaje mniej zapisów; po wyłączeniu komputera cache znika sam – plus dla prywatności.
- **Ograniczenia:** nie potrzebujesz żadnych dowiązań ani przenoszenia folderów jak w Chrome. Pamięć grafiki zostaje w profilu.

# 21. Szybki Dostęp (Quick Access)

### 21.1 Strona nowej karty
- **Co robi:** skróty do ulubionych stron ułożone w grupach, z ikonami i miniaturami.

### 21.2 Dodawanie skrótu
- **Jak używać:** „+ Skrót” → adres, nazwa → „Zapisz”; albo na dowolnej stronie prawy klik → „Dodaj do Szybkiego Dostępu” → wybór grupy.

### 21.3 Grupy skrótów
- **Jak używać:** „+ Grupa” – np. Start, Finanse, Muzyka, Gry. Ikony nie migają przy przełączaniu grup.

### 21.4 Edycja i usuwanie skrótów
- **Jak używać:** ikony „Edytuj” i „Usuń” na kafelku.

### 21.5 Własne ikony
- **Jak używać:** w oknie skrótu „Wybierz obrazek…” (z pliku) albo wklej obrazek ze schowka; „Usuń własną” wraca do ikony strony.

### 21.6 Profile Szybkiego Dostępu
- **Jak używać:** lista profili → „+ Nowy profil…”; przełączanie z listy; zmiana nazwy; „Usuń ten profil”.

### 21.7 PIN profilu
- **Jak używać:** menu ⋮ → „PIN tego profilu” – ustaw lub zdejmij PIN.
- **Co robi:** wejście do profilu z PIN-em wymaga kodu („Podaj kod, żeby wejść”); dodanie strony do takiego profilu też.

### 21.8 Kosz
- **Jak używać:** menu ⋮ → „Kosz…” – usunięte skróty można „Przywróć”; opróżnianie kosza.

### 21.9 Miniatury stron
- **Gdzie:** menu ⋮ → „Miniatury stron”.
- **Co robi:** Velivo robi miniatury stron w tle, w niewidocznym oknie – bez historii, dźwięku, okien i pobierań.

### 21.10 Motywy tła i rozmiar
- **Co robi:** wybór tła strony nowej karty i wielkości kafelków.

### 21.11 Import zakładek
- **Co robi:** import skrótów z pliku zakładek (HTML) lub kopii (JSON).

### 21.12 Kopia i folder danych
- **Co robi:** kopia Szybkiego Dostępu na dysku; „Folder rozszerzenia” obsługiwany przez most Sejfu (nic nie trzeba robić); „Nazwa tego komputera”.

### 21.13 Synchronizacja
- **Co robi:** w Velivo Szybki Dostęp synchronizuje się przez LAN razem z grafikami. Ten sam dodatek w Chrome może synchronizować się z kontem Google („Synchronizacja z kontem Google”, stan „Sync OK”).

### 21.14 Uprawnienia do witryn
- **Gdzie:** menu ⋮ → „Cofnij dostęp do witryn”, „Pytanie o zgodę”.

### 21.15 Szybki Dostęp w innych przeglądarkach
- **Gdzie:** Ustawienia → „Folder rozszerzenia do ręcznej instalacji w innych przeglądarkach”.
- **Co robi:** podaje ścieżkę do skopiowania w oknie „Załaduj rozpakowane rozszerzenie” Chrome/Edge. Dane Szybkiego Dostępu w Velivo i w innych przeglądarkach są osobne.

# 22. Dodatki (rozszerzenia)

### 22.1 Okno dodatków
- **Gdzie:** przycisk „Dodatki” na pasku (tryb dewelopera).
- **Co robi:** lista zainstalowanych dodatków: włącz/wyłącz, „Strona ustawień dodatku”, „Przeładuj”, usuń.

### 22.2 Instalacja z Chrome Web Store
- **Jak używać:** otwórz stronę dodatku w Chrome Web Store („Otwórz Chrome Web Store”) → kliknij „➕ Dodaj do Velivo” na pasku albo przycisk sklepu; albo „Zainstaluj z linku/ID…”.
- **Co robi:** Velivo pobiera dodatek i instaluje go samo.

### 22.3 Wczytanie dodatku z folderu
- **Jak używać:** „Folder dodatku (z plikiem manifest.json)”.

### 22.4 Pasek ikon dodatków
- **Co robi:** ikony dodatków na pasku; klik otwiera okienko dodatku; „Otwórz okienko w karcie”; przypinanie i odpinanie ikon.
- **Ograniczenia:** niektóre dodatki nie mają okienka („działa w tle i na stronach”).

### 22.5 Automatyczne przeładowanie zmienionych dodatków
- **Co robi:** przy starcie Velivo przeładowuje dodatki, których pliki się zmieniły.

### 22.6 Synchronizacja dodatków
- **Co robi:** lista dodatków przechodzi na sparowane komputery, a brakujące są tam instalowane.

### 22.7 Wbudowane dodatki
- **uBlock Origin Lite** (11.5) i **Szybki Dostęp** (rozdział 21).

# 23. Import i eksport

### 23.1 Import z innych przeglądarek
- **Gdzie:** Ustawienia → „Z Chrome / Edge / Brave / Opery”.
- **Co robi:** zakładki są czytane wprost z plików tamtej przeglądarki (także Vivaldi); dla haseł Velivo otwiera stronę haseł tamtej przeglądarki do eksportu CSV.

### 23.2 Import haseł CSV
- **Gdzie:** Ustawienia → „Hasła i loginy z pliku (CSV: KeePassXC, Chrome, Edge…)” albo „Import haseł CSV…”.
- **Co robi:** wczytuje hasła z KeePass/KeePassXC (pomija wpisy z kosza), Chrome, Edge, Firefoksa i innych; obsługuje pola z wieloma liniami. Pokazuje liczbę zaimportowanych wpisów.

### 23.3 Eksport haseł CSV
- **Gdzie:** Ustawienia → „Eksport haseł CSV…”.
- **Ograniczenia:** plik CSV jest **jawny** (niezaszyfrowany) – przechowuj go ostrożnie i usuń po użyciu.

### 23.4 Import i eksport arkusza rachunków
- Import z Excela (CSV) – 14.35; eksport „Zapisz do Excela (CSV)” – 14.38.

### 23.5 Szyfrowana paczka eksportu (synchronizacja E2E plikiem)
- **Gdzie:** Ustawienia → Synchronizacja E2E → „Eksportuj paczkę…” / „Importuj paczkę…”.
- **Co robi:** zapisuje Twoje dane (ustawienia, zakładki, hasła i inne) w pliku **zaszyfrowanym hasłem**; plik możesz przenieść na inne urządzenie (np. pendrive) i tam zaimportować.

### 23.6 Import zakładek do Szybkiego Dostępu
- 21.11.

# 24. Kopia zapasowa i odzyskiwanie

### 24.1 Kopia trybu bankowego
- **Gdzie:** menu 🏦 → „Kopia zapasowa bazy…”.
- **Co robi:** plik `.vbank` ze wszystkimi profilami bankowymi, **nadal zaszyfrowany** – np. na pendrive.

### 24.2 Przywrócenie trybu bankowego
- **Gdzie:** menu 🏦 → „Przywróć bazę z kopii…”. Do otwarcia potrzebne jest hasło lub klucz z chwili wykonania kopii.

### 24.3 Plik odzyskiwania parowania LAN
- 17.15.

### 24.4 Szyfrowana paczka danych
- 23.5.

### 24.5 Kopia Szybkiego Dostępu
- 21.12.

### 24.6 Kopia przy czystej instalacji
- 1.3 – stare dane trafiają do folderu z dopiskiem „kopia-…”.

### 24.7 Zapomniane hasło trybu bankowego
- 14.44 – bez hasła i klucza danych nie da się odzyskać; dlatego kopia i drugi klucz.

# 25. Ustawienia – każda opcja

Ustawienia otwierasz przyciskiem ⚙. Poniżej każda opcja w kolejności okna.

### 25.1 Domyślna przeglądarka
- Stan i przycisk „Ustaw Velivo jako domyślną przeglądarkę…” (1.8).

### 25.2 Import haseł, loginów i zakładek
- „Hasła i loginy z pliku (CSV)”, „Z Chrome / Edge / Brave / Opery” (rozdział 23).

### 25.3 Domyślne powiększenie stron
- 6.7.

### 25.4 Tryb ciemny i tryb nocny
- 6.5, 6.6.

### 25.5 Efekt wejścia treści i jego szybkość
- 6.1, 6.2.

### 25.6 Motyw i styl wyglądu
- 6.3, 6.4.

### 25.7 Język interfejsu
- 1.10.

### 25.8 Zawsze kompaktowy pasek narzędzi
- 2.3.

### 25.9 Czytanie na głos – głos i prędkość
- 9.9, rozdział 10.

### 25.10 Karty: przywracanie sesji i linki w tej samej karcie
- 4.12, 4.13.

### 25.11 Wyszukiwanie i start
- Skróty wyszukiwania, wyszukiwarka, gesty myszy, przyciski „Obraz w obrazie” i „Pobierz” nad filmami, strona startowa, Szybki Dostęp jako nowa karta, folder rozszerzenia (rozdziały 5, 7, 8, 21).

### 25.12 Prywatność
- „Nie śledź”, ochrona przed śledzeniem (zrównoważona/ścisła), zapisywanie historii, czyszczenie przy zamknięciu, loginy z Sejfu, proponowanie zapisu haseł, autouzupełnianie, wyskakujące okna, odrzucanie banerów, pamięć treści stron, sztuczki presji, paragon prywatności, zarządzanie danymi formularzy i hasłami (rozdziały 11, 15, 16).

### 25.13 Blokowanie reklam
- Pełne listy filtrów i „Aktualizuj listy teraz” (11.3).

### 25.14 Bezpieczeństwo i pobieranie
- SmartScreen, pytanie o miejsce zapisu, wykrywanie fałszywych stron, HTTPS, bezpieczne płatności, uBlock Origin Lite (rozdziały 8, 11, 13).

### 25.15 Głośniki Velivo
- **Do czego:** dźwięk Velivo na wybranych głośnikach.
- **Jak używać:** lista „Głośniki Velivo” – wybierz wyjście zamiast „Domyślne wyjście Windows”.

### 25.16 Nie gub dźwięku
- **Do czego:** dla muzyków – gdy program muzyczny (Ableton, Cubase) zajmie głośniki na wyłączność.
- **Co robi:** Velivo co 2 sekundy sprawdza, czy wybrane wyjście jest wolne; zajęte → dźwięk Velivo idzie na inne aktywne wyjście i wraca, gdy głośniki się zwolnią. „Mikser głośności Windows…” pozwala przypiąć Velivo do głośników na stałe.

### 25.17 Zostań w zasobniku
- **Gdzie:** „Po zamknięciu okna zostań w zasobniku (synchronizacja w tle, natychmiastowy start)”.
- **Co robi:** zamknięcie okna chowa Velivo do ikony przy zegarze: strony w tle są pauzowane (okienko „Film na wierzchu” gra dalej), synchronizacja LAN działa, a kolejne otwarcie jest natychmiastowe. Przy pierwszym schowaniu pojawia się dymek z wyjaśnieniem. Prawy klik na ikonie: **Otwórz Velivo**, **Synchronizuj teraz**, **Zamknij Velivo całkowicie**. Ikona pulsuje przy synchronizacji.

### 25.18 Połączeń na jeden pobierany plik
- 8.2.

### 25.19 Śmieci przeglądarki
- Rozdział 19.

### 25.20 Szybkość wczytywania stron
- 26.1, 19.5.

### 25.21 Dane
- „Wyczyść dane przeglądania teraz…”, „Prywatność per-strona” (11.13, rozdział 12).

### 25.22 Profile użytkownika
- Rozdział 18.

### 25.23 Synchronizacja
- Synchronizacja E2E (paczka), LAN, parowanie, plik odzyskiwania, tryb cichy, panel diagnostyczny (rozdziały 17, 23).

### 25.24 Zapis ustawień
- **Co robi:** „Zapisz” zapisuje i od razu stosuje ustawienia we wszystkich kartach. Zmiana języka lub folderu śmieci działa po ponownym uruchomieniu (Velivo o tym informuje).

### 25.25 Torrenty
- **Gdzie:** Ustawienia → sekcja „Torrenty”: włączanie (domyślnie wyłączone), folder strefy, prędkość pobierania i wysyłania, udostępnianie po pobraniu (współczynnik i czas), liczba uczestników. Opis – 8.19.

### 25.26 Odtwarzacz filmów
- **Gdzie:** Ustawienia → sekcja „Odtwarzacz filmów”: otwieranie filmów z dysku w Velivo, odtwarzanie od razu, wznawianie od miejsca, powtarzanie. Opis – 7.16.

# 26. Wydajność

### 26.1 Szybsze otwieranie stron
- **Gdzie:** Ustawienia → Szybkość wczytywania stron → „Szybsze otwieranie stron (wczytywanie przy najechaniu na link)”.
- **Co robi:** Velivo zaczyna pobierać stronę, gdy najedziesz na link, i z wyprzedzeniem łączy się z serwerami widocznych linków.
- **Ograniczenia:** wyłączone w kartach prywatnych i bankowych; przy limicie danych lepiej wyłączyć.

### 26.2 Mały instalator i lekki program
- **Co robi:** Velivo używa silnika WebView2 wbudowanego w Windows, więc nie zawiera własnej kopii silnika – instalator jest mały, a pamięć jest współdzielona z systemem.

### 26.3 Szybki bloker reklam
- **Co robi:** filtr ok. 100 tys. reguł jest budowany w tle i podmieniany w całości, bez przycinania przeglądania.

### 26.4 Mniej obciążenia okna
- **Co robi:** zdjęcia i czcionki nie przechodzą przez sprawdzanie w oknie programu (reklamy-obrazki blokuje uBlock wewnątrz silnika) – przewijanie i ładowanie są płynniejsze.

### 26.5 Lekki tryb „tylko film”
- **Co robi:** po zamknięciu głównego okna z grającym okienkiem filmu Velivo działa tylko dla filmu (7.10).

### 26.6 Cache na RAM dysku
- Rozdział 20.

# 27. Wszystkie pozostałe funkcje

### 27.1 Zakładki
- **Gdzie:** gwiazdka na pasku (Ctrl+D); przycisk „Wszystkie zakładki”.
- **Co robi:** dodaj/usuń zakładkę bieżącej strony; lista zakładek; prawy klik: „Zmień nazwę”, usuń. Usunięcie przenosi się na sparowane komputery.

### 27.2 Historia (Ctrl+H)
- **Co robi:** historia pogrupowana jak w Chrome: **dzień → sesja** (jedno uruchomienie przeglądarki, „Bieżąca sesja”) → strony.
- **Jak używać:** „Szukaj w historii…” (w tytułach i adresach); dwuklik otwiera stronę; prawy klik: „Usuń z historii”, „Usuń znalezione wpisy z tego dnia”, „Usuń cały dzień z historii”, „Otwórz wszystkie w nowych kartach”, „Usuń sesję z historii”; „Wyczyść całą historię”.

### 27.3 Panel pobranych
- Rozdział 8.

### 27.4 Wyciszanie i dźwięk kart
- 4.11, 25.15–25.16.

### 27.5 Zrzuty ekranu
- 4.17.

### 27.6 Wersja telefonu
- 4.16.

### 27.7 Tłumaczenie
- 9.11.

### 27.8 Jedno okno i linki z innych programów
- 2.9.

### 27.9 Odporność na błędy i dziennik błędów
- 2.10. Plik `bledy.log` w folderze danych przydaje się przy zgłaszaniu problemów.

### 27.10 Gdzie leżą dane
- **Co robi:** wszystkie dane profilu domyślnego są w `%LOCALAPPDATA%\Przegladarka` (inne profile: `…\Przegladarka\Profiles\nazwa`). Najważniejsze pliki: `ustawienia.txt`, `zakladki.txt`, `historia.txt`, `sesja.txt`, `hasla.vault` (zaszyfrowane), `autouzupelnianie.vault` (zaszyfrowane), `bank.json` (zaszyfrowane pola), `bledy.log`.

### 27.11 Rozwiązywanie problemów
| Problem | Co zrobić |
|---|---|
| Strona blokuje coś, czego nie powinna | tarcza → panel → „do zaufanych” albo prawy klik → „Przywróć zablokowane elementy” |
| Baner cookies nadal widoczny | baner nie ma przycisku „Odrzuć” – Velivo nigdy nie klika „Akceptuj” |
| Komputery się nie widzą | ten sam profil na obu, zapora przepuszcza Velivo, automatyczny czas Windows (różnica ponad 5 minut blokuje wymianę) |
| Pobieranie z YouTube się nie udaje | spróbuj za chwilę – yt-dlp aktualizuje się samo, a serwisy czasem się zmieniają |
| Antywirus blokuje instalator | plik nie ma płatnego podpisu cyfrowego – dodaj wyjątek albo sprawdź sumę SHA-256 (1.6) |
| Program się nie uruchamia | sprawdź WebView2 Runtime i .NET 10 (1.2); zajrzyj do `bledy.log` |

# Funkcje NIE DOSTĘPNE W 1.22

Poniższe rzeczy były omawiane, ale **w wersji 1.22 ich nie ma**:
- **Pobieranie napisów do filmów** – NIE DOSTĘPNE W 1.22.
- **Zmiana (fałszowanie) odcisku palca przeglądarki** – NIE DOSTĘPNE W 1.22; Velivo wykrywa i raportuje fingerprinting (11.9).
- **8 głosów offline Piper** – NIE DOSTĘPNE W 1.22; dostępnych jest 6 (3 polskie, 3 angielskie).
- **Wspólny panel środowiska dla wielu komputerów** – NIE DOSTĘPNE W 1.22; jest panel diagnostyczny LAN (17.13).
- **Synchronizacja otwartych kart** – NIE DOSTĘPNE W 1.22; jest „Wyślij do…” (17.9).
- **Połączenie z bankiem / automatyczne wykrywanie zapłaty** – NIE DOSTĘPNE W 1.22; płatność oznaczasz ręcznie (14.27).
- **Przeliczanie walut w arkuszu** – NIE DOSTĘPNE W 1.22; waluta to tylko symbol (14.36).
- **Podpis cyfrowy plików programu** – NIE DOSTĘPNE W 1.22.

# PEŁNA LISTA FUNKCJI VELIVO 1.22

Opisanych funkcji: **268**. Numer przy funkcji wskazuje jej opis w podręczniku. Pozycje, które w podręczniku są tylko odsyłaczami do innego miejsca (40), nie są tu liczone drugi raz.


## 1. Pierwsze uruchomienie

- **1.1** Instalator Velivo-Setup-1.22.exe
- **1.2** Sprawdzenie składników systemu
- **1.3** Aktualizacja albo czysta instalacja
- **1.4** Wersja bez instalacji
- **1.5** Ciche instalowanie i odinstalowanie (dla zaawansowanych)
- **1.6** Sprawdzenie, czy plik jest oryginalny
- **1.7** Pierwsze otwarcie
- **1.8** Ustawienie Velivo jako domyślnej przeglądarki
- **1.9** Import z poprzedniej przeglądarki
- **1.10** Język programu
- **1.11** Wszystko w jednym – bez dodatkowych programów

## 2. Interfejs

- **2.1** Pasek kart
- **2.2** Pasek narzędzi
- **2.3** Kompaktowy i dopasowujący się pasek
- **2.4** Pasek adresu
- **2.5** Wskaźnik aktywnej reguły prywatności
- **2.6** Przycisk „➕ Dodaj do Velivo” w sklepie dodatków
- **2.7** Dymki powiadomień
- **2.8** Pełny ekran
- **2.9** Jedno okno programu
- **2.10** Odporność na błędy

## 3. Sterowanie myszką i klawiaturą

- **3.1** Kółko myszy na przycisku powiększenia
- **3.2** Kółko myszy na przycisku trybu nocnego
- **3.3** Kółko myszy nad okienkiem filmu
- **3.4** Klik na linkach
- **3.5** Menu prawego przycisku na stronie
- **3.6** Menu prawego przycisku na karcie
- **3.7** Skróty klawiszowe

## 4. Karty i okna

- **4.1** Nowa karta
- **4.2** Karta prywatna (incognito)
- **4.3** Duplikowanie karty
- **4.4** Zamykanie kart
- **4.5** Przywracanie zamkniętej karty
- **4.6** Przypięte karty
- **4.7** Grupy kart
- **4.8** Zestawy kart
- **4.9** Automatyczne odświeżanie karty
- **4.10** Szukanie w kartach
- **4.11** Wyciszanie karty
- **4.12** Linki w tej samej karcie
- **4.13** Przywracanie sesji
- **4.14** Wyślij kartę na inny komputer
- **4.15** Powiększenie strony
- **4.16** Wersja telefonu strony
- **4.17** Zrzut ekranu
- **4.18** Zamykanie i minimalizowanie Velivo

## 5. Gesty myszy i wyszukiwanie

- **5.1** Gesty myszy
- **5.2** Wyszukiwarka w pasku adresu
- **5.3** Skróty wyszukiwania
- **5.4** Wyszukiwanie zaznaczonego tekstu
- **5.5** Strona startowa
- **5.6** Szybki Dostęp jako strona nowej karty

## 6. Animacje i wygląd

- **6.1** Efekt wejścia treści
- **6.2** Szybkość efektu wejścia
- **6.3** Styl wyglądu: Nowoczesny i Kolorowy
- **6.4** Motywy przeglądarki
- **6.5** Tryb ciemny stron
- **6.6** Tryb nocny (cieplejsze kolory)
- **6.7** Domyślne powiększenie stron

## 7. Wideo i „Film na wierzchu”

- **7.1** Film na wierzchu – otwarcie
- **7.2** Czysty widok filmu
- **7.3** Przesuwanie okienka
- **7.4** Zmiana rozmiaru okienka
- **7.5** Przezroczystość okienka
- **7.6** Zawsze na wierzchu (przypinka)
- **7.7** Pauza i wznowienie
- **7.8** Przewijanie filmu w okienku
- **7.9** Powrót do strony (↩)
- **7.10** Oglądanie przy zamkniętej przeglądarce
- **7.11** Zapamiętywanie ustawień okienka
- **7.12** Zamknięcie okienka
- **7.13** Przykłady użycia
- **7.14** Obraz w obrazie (PiP)
- **7.15** Wyciszenie filmu w karcie
- **7.16** Odtwarzacz filmów z dysku (offline)

## 8. Pobieranie plików, filmów i muzyki

- **8.1** Menedżer pobierania Velivo
- **8.2** Pobieranie wieloma połączeniami
- **8.3** Wstrzymaj i wznów
- **8.4** Automatyczne ponawianie
- **8.5** Pobieranie plików po zalogowaniu
- **8.6** Pobieranie awaryjne przez silnik
- **8.7** Pytanie o miejsce zapisu
- **8.8** Lista pobranych plików
- **8.9** Ostrzeżenie przy zamykaniu
- **8.10** Znacznik „plik z internetu”
- **8.11** Przycisk ⬇ Pobierz nad filmem
- **8.12** Wybór jakości i formatu
- **8.13** yt-dlp – pobierany raz
- **8.14** FFmpeg – pobierany raz
- **8.15** Sprawdzanie pobranych narzędzi
- **8.16** Wykryj media do pobrania
- **8.17** Pobieranie napisów – **NIE DOSTĘPNE W 1.22**
- **8.18** Pobieranie z YouTube w trybie „Wykryj media” – ograniczenie
- **8.19** Torrenty (magnet i .torrent) – włączane w Ustawieniach

## 9. Czytnik i czytanie stron

- **9.1** Tryb czytania (Czytnik)
- **9.2** Wygląd czytnika
- **9.3** Lokalne streszczenie
- **9.4** Czytanie strony na głos
- **9.5** Czytanie zaznaczenia
- **9.6** Czytanie od wskazanego miejsca
- **9.7** Pauza, wznowienie, stop
- **9.8** Prędkość czytania
- **9.9** Wybór głosu
- **9.10** „Gdzie ja to czytałem?”
- **9.11** Tłumaczenie stron i zaznaczenia

## 10. Czytanie offline – głosy Piper

- **10.1** Naturalne głosy offline
- **10.2** Dostępne głosy Piper w wersji 1.22
- **10.3** Bezpieczeństwo pobieranego programu Piper
- **10.4** Gdzie leżą głosy

## 11. Prywatność

- **11.1** Tarcza prywatności
- **11.2** Blokowanie reklam Velivo
- **11.3** Pełne listy filtrów
- **11.4** Własne reguły blokowania (dla zaawansowanych)
- **11.5** uBlock Origin Lite (wbudowany)
- **11.6** Blokowanie elementów strony
- **11.7** „Nie śledź” i Global Privacy Control
- **11.8** Paragon prywatności
- **11.9** Fingerprinting – wykrywanie i raport
- **11.10** Automatyczne odrzucanie banerów ciasteczek
- **11.11** Blokada wyskakujących okien
- **11.12** Historia – zapisywanie i czyszczenie
- **11.13** Wyczyść dane przeglądania
- **11.14** Dziennik „Co zostało zablokowane i dlaczego”
- **11.15** Ukrywanie „przeglądarki wbudowanej”
- **11.16** Wykrywacz sztuczek presji w sklepach

## 12. Poziomy ochrony i reguły dla stron

- **12.1** Ochrona przed śledzeniem – zrównoważona
- **12.2** Ochrona przed śledzeniem – ścisła
- **12.3** Panel prywatności i reguły domen
- **12.4** Reguła: Blokuj JavaScript dla domeny
- **12.5** Reguła: Nie wysyłaj cookies dla domeny
- **12.6** Reguła: Wymuś blokowanie trackerów dla domeny
- **12.7** Reguła: Automatycznie czyść dane po wejściu na domenę
- **12.8** Zaufane domeny
- **12.9** Poziomy bezpieczeństwa – podsumowanie

## 13. Bezpieczeństwo

- **13.1** Wykrywanie fałszywych stron (bez internetu)
- **13.2** SmartScreen
- **13.3** Zawsze szyfrowane połączenie (HTTPS)
- **13.4** Hasła tylko dla prawdziwej domeny
- **13.5** Bezpieczne płatności – blokada nagrywania ekranu
- **13.6** Ochrona danych płatniczych
- **13.7** Formularze odporne na ataki
- **13.8** Zamknięty kanał między stroną a Velivo

## 14. Tryb bankowy (Banking Mode)

- **14.1** Czym jest tryb bankowy
- **14.2** Przycisk 🏦 i menu trybu
- **14.3** Pierwsze uruchomienie trybu
- **14.4** Klucz sprzętowy FIDO2 (YubiKey, Google Titan)
- **14.5** Otwieranie trybu
- **14.6** Automatyczna blokada
- **14.7** Zablokuj teraz
- **14.8** Czyszczenie po zamknięciu trybu
- **14.9** Szyfrowanie bazy
- **14.10** Moje banki
- **14.12** Dane logowania banku
- **14.13** Wpisz login
- **14.14** Wpisz wybrane znaki
- **14.15** Skopiuj opis formularza
- **14.16** Moje karty
- **14.17** Wypełnij kartę na tej stronie
- **14.18** Moje loginy i hasła (w trybie)
- **14.19** Poufne dane (dokumenty)
- **14.20** Rachunki bankowe
- **14.21** Moje notatki
- **14.22** Wpisz login i hasło (dane skojarzone ze stroną)
- **14.23** Szukaj w mojej bazie
- **14.24** Rachunki do opłacenia
- **14.25** Rachunki cykliczne
- **14.26** Terminy i przypomnienia
- **14.27** Oznaczanie jako zapłacone
- **14.28** Otwórz stronę płatności
- **14.29** Nazwy rachunków z arkusza
- **14.30** Dodaj rachunki z arkusza
- **14.31** Arkusz rachunków
- **14.32** Arkusz – wpisywanie kwot i miesięcy
- **14.33** Arkusz – nowy miesiąc
- **14.34** Arkusz – rachunki (kolumny)
- **14.35** Arkusz – import z Excela (CSV)
- **14.36** Arkusz – waluta
- **14.37** Arkusz – zapis
- **14.38** Zestawienie płatności
- **14.39** Ostrzeżenie o stronie podobnej do Twojego banku
- **14.40** Propozycja trybu bankowego
- **14.41** Dziennik otwarć
- **14.42** Profile bankowe
- **14.43** Ustawienia trybu bankowego
- **14.44** Zapomniane hasło
- **14.45** Instrukcja trybu w programie

## 15. Sejf (zewnętrzny menedżer haseł)

- **15.1** Loginy z Sejfu na stronach logowania
- **15.2** Okienko Sejfu w Szybkim Dostępie
- **15.3** Wypełnianie formularzy z Sejfu

## 16. Hasła i dane

- **16.1** Menedżer haseł Velivo
- **16.2** Szukanie haseł
- **16.3** Dodawanie i edycja wpisu
- **16.4** Generator mocnych haseł
- **16.5** Kopiowanie i usuwanie
- **16.6** Propozycja zapisu hasła
- **16.7** Wpisywanie zapisanego loginu
- **16.8** Autouzupełnianie formularzy
- **16.9** Zapisywanie danych formularza
- **16.10** Zarządzanie danymi autouzupełniania

## 17. LAN Sync – synchronizacja w sieci domowej

- **17.1** Synchronizacja bez chmury
- **17.2** Przed sparowaniem – żadnych danych
- **17.3** Propozycja połączenia
- **17.4** Parowanie urządzeń
- **17.5** Pierwsze połączenie – czyje ustawienia
- **17.6** Szyfrowanie synchronizacji
- **17.7** Co się synchronizuje
- **17.8** Izolacja profili przy synchronizacji
- **17.9** Wyślij kartę na inny komputer
- **17.10** Wskaźnik w zasobniku
- **17.11** Powiadomienia o synchronizacji
- **17.12** Tryb cichy
- **17.13** Panel diagnostyczny LAN
- **17.14** Ostrzeżenia i błędy
- **17.15** Plik odzyskiwania parowania

## 18. Profile i izolacja użytkowników

- **18.1** Profile użytkowników Velivo
- **18.2** Dodanie profilu
- **18.3** Przełączanie profilu
- **18.4** Ikona profilu
- **18.5** Usunięcie profilu
- **18.6** Izolacja danych
- **18.9** Uruchamianie z wybranym profilem (dla zaawansowanych)

## 19. Pamięć podręczna i relokacja danych tymczasowych

- **19.1** Śmieci przeglądarki – czym są
- **19.2** Folder na śmieci (relokacja cache)
- **19.3** Usuwaj śmieci przy każdym uruchomieniu
- **19.4** Wyczyść śmieci teraz
- **19.5** Rozmiar pamięci podręcznej
- **19.6** Bezpieczne sprzątanie
- **19.7** Osobny folder danych (dla zaawansowanych)

## 20. RAM dysk

- **20.1** Cache na RAM dysku

## 21. Szybki Dostęp (Quick Access)

- **21.1** Strona nowej karty
- **21.2** Dodawanie skrótu
- **21.3** Grupy skrótów
- **21.4** Edycja i usuwanie skrótów
- **21.5** Własne ikony
- **21.6** Profile Szybkiego Dostępu
- **21.7** PIN profilu
- **21.8** Kosz
- **21.9** Miniatury stron
- **21.10** Motywy tła i rozmiar
- **21.11** Import zakładek
- **21.12** Kopia i folder danych
- **21.13** Synchronizacja
- **21.14** Uprawnienia do witryn
- **21.15** Szybki Dostęp w innych przeglądarkach

## 22. Dodatki (rozszerzenia)

- **22.1** Okno dodatków
- **22.2** Instalacja z Chrome Web Store
- **22.3** Wczytanie dodatku z folderu
- **22.4** Pasek ikon dodatków
- **22.5** Automatyczne przeładowanie zmienionych dodatków
- **22.6** Synchronizacja dodatków
- **22.7** Wbudowane dodatki

## 23. Import i eksport

- **23.1** Import z innych przeglądarek
- **23.2** Import haseł CSV
- **23.3** Eksport haseł CSV
- **23.4** Import i eksport arkusza rachunków
- **23.5** Szyfrowana paczka eksportu (synchronizacja E2E plikiem)

## 24. Kopia zapasowa i odzyskiwanie

- **24.1** Kopia trybu bankowego
- **24.2** Przywrócenie trybu bankowego

## 25. Ustawienia – każda opcja

- **25.15** Głośniki Velivo
- **25.16** Nie gub dźwięku
- **25.17** Zostań w zasobniku
- **25.24** Zapis ustawień
- **25.25** Torrenty
- **25.26** Odtwarzacz filmów

## 26. Wydajność

- **26.1** Szybsze otwieranie stron
- **26.2** Mały instalator i lekki program
- **26.3** Szybki bloker reklam
- **26.4** Mniej obciążenia okna
- **26.5** Lekki tryb „tylko film”

## 27. Wszystkie pozostałe funkcje

- **27.1** Zakładki
- **27.2** Historia (Ctrl+H)
- **27.10** Gdzie leżą dane
- **27.11** Rozwiązywanie problemów
