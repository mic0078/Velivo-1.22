# Velivo na telefon – instrukcja

Velivo na Androida to lekka, prywatna przeglądarka w kolorach Velivo, połączona z Velivo na Windows. Wymaga Androida 10 lub nowszego. Komputer jest „główny”: to, co przychodzi z telefonu, komputer zawsze potwierdza.

## 1. Ekran i menu

- **Pasek adresu** – wpisz adres albo szukane słowa (wyszukiwanie przez Startpage).
- **🕶** – nowa karta prywatna. **Przycisk z liczbą** – lista kart z miniaturami (stuknij = przełącz, ✕ lub przesunięcie w bok = zamknij).
- **Logo Velivo** – menu. Kafelki są w czterech grupach, po 3 w rzędzie: **Karty**, **Ta strona** (szukaj, czytnik, czytanie na głos, usuń element, dodaj zakładkę, wpisz hasło), **Moje**, **Ustawienia** (z kafelkiem **🌐 Język**). Na górze licznik zablokowanych reklam.
- **Dolny pasek** – ◀ wstecz, ▶ dalej, ⟳ odśwież, ⌂ strona startowa, ＋ nowa karta (Szybki Dostęp).

## 2. Karty

| Kafelek | Co robi |
| --- | --- |
| ＋ Nowa karta | Otwiera Szybki Dostęp – te same kafelki i grupy co na komputerze |
| 🕶 Nowa karta prywatna | Osobny profil (ciasteczka, logowania), bez historii; kasowany po zamknięciu ostatniej prywatnej karty |
| ⭐ Szybki Dostęp | Wraca do Szybkiego Dostępu w bieżącej karcie |
| 🏦 Tryb bankowy | Wejście odciskiem / PIN-em, osobny stały profil, bez historii i zrzutów ekranu, blokada po 10 min bezczynności |
| 🔎 Szukaj na stronie | Pasek z licznikiem „3/12”, ▲ ▼ – poprzedni / następny wynik, ✕ zamyka |
| 🖥 Tryb pulpitu | Strona w wersji na komputer (dla tej karty); ponownie – wersja na telefon |
| 📖 Czytnik | Sam tekst artykułu, bez reklam i menu |
| 🎯 Usuń element | Usuwa reklamę lub ramkę stuknięciem palca |
| 🔊 Czytaj na głos | Czyta artykuł; w trakcie kafelek zmienia się na ⏹ Zatrzymaj czytanie |

### Czytnik

- **A− / A+** – wielkość tekstu (zapamiętywana).
- **🔊** – czytanie na głos z czytnika, **✕** lub Wstecz – zamknięcie (czytanie też się zatrzymuje).
- Velivo **pamięta, gdzie skończyłeś czytać** daną stronę i przy następnym otwarciu czytnika wraca w to miejsce. Zapis zostaje tylko w telefonie.

### Czytanie na głos

- Działa także przy **zgaszonym ekranie** i w innej aplikacji – w powiadomieniu jest „🔊 tytuł strony” i przycisk **Zatrzymaj**. Po przeczytaniu całości powiadomienie znika.
- Cofnięcie strony (◀ lub Wstecz) zatrzymuje czytanie.
- Brak syntezatora mowy lub głosu w języku telefonu – Velivo zaproponuje **pobranie Usług mowy Google**.
- Na Xiaomi ustaw dla Velivo: Ustawienia → Aplikacje → Velivo → Oszczędzanie baterii → **Brak ograniczeń**, inaczej system może przerwać czytanie.

### Usuwanie elementów palcem

1. Menu → **🎯 Usuń element**. Strona dostaje lekko fioletową nakładkę, na górze pasek.
2. Stuknij reklamę – Velivo sam dobiera całe pole reklamy (fioletowa ramka) i pyta **„Usunąć?”**: **Usuń**, **Zaznacz większy obszar** albo **Anuluj**. Działa też na reklamach w ramkach (iframe).
3. Na pasku: **⬆** – większy obszar, **📋** – kopiuj kod elementu (do zgłoszenia reklamy, której Velivo nie ukrywa).
4. **🗑** – usuń. Element znika i nie zajmuje miejsca; na tej stronie zostanie ukryty także przy następnych wizytach.
5. **↺** – przywróć wszystko, co usunięto na tej stronie. **✕** – koniec.

Szybciej: **przytrzymaj palec** na reklamie albo pustym polu „REKLAMA” – Velivo samo zapyta, czy je usunąć (na tekście i linkach przytrzymanie działa jak zwykle). Velivo sprawdza wszystkie warstwy pod palcem, więc znajdzie też reklamę schowaną pod przezroczystą warstwą strony.

Reguły są zapisane tylko w telefonie (nie synchronizują się z komputerem).

## 3. Moje

| Kafelek | Co robi |
| --- | --- |
| Zakładki, Historia | Lista z wyszukiwaniem, sortowaniem (najnowsze / A–Z) i ✕ |
| Dodaj zakładkę | Dodaje bieżącą stronę |
| Hasła | Lista haseł po odcisku palca / PIN-ie; **Kopiuj hasło** oznacza je w schowku jako poufne |
| Wpisz hasło | Wpisuje zapisane hasło, gdy formularz pojawił się później |

Hasła są szyfrowane kluczem w Android Keystore (klucz nie opuszcza telefonu). Velivo wpisuje je tylko na stronach https i w sieci domowej (router, NAS).

## 4. Ustawienia

| Kafelek | Co robi |
| --- | --- |
| Blokuj reklamy | Te same listy co na komputerze (EasyList, EasyPrivacy, KAD) – dołączone do aplikacji, odświeżane co 4 dni. Po reklamie nie zostaje puste pole ani błąd w ramce (reguły ukrywania jak w uBlock Origin); na YouTube bez reklam |
| Odtwarzanie w tle | Film / muzyka grają po wyjściu z aplikacji (powiadomienie z „Zatrzymaj”) |
| Motyw | Jak w telefonie / jasny / ciemny – w kolorach Velivo |
| Synchronizacja z komputerem | Parowanie, plik odzyskiwania, nazwa telefonu w sieci |
| Wyczyść historię | Usuwa historię przeglądania |

Język aplikacji (polski / angielski) zmienisz w Ustawieniach Androida → Aplikacje → Velivo → Język.

## 5. Połączenie z komputerem

1. Telefon i komputer w tej samej sieci Wi-Fi, Velivo otwarte na obu.
2. Telefon: menu → **Synchronizacja z komputerem → Sparuj z Velivo na Windows**.
3. Komputer: Ustawienia → Synchronizacja (LAN) → **Sparuj urządzenie w sieci…**. Porównaj 6-cyfrowy kod na obu ekranach i potwierdź.

Przy kilku komputerach użyj **pliku odzyskiwania** (komputer: Zapisz plik odzyskiwania…, telefon: Wczytaj plik odzyskiwania).

| Co | Jak |
| --- | --- |
| Zakładki, hasła, Szybki Dostęp | Z komputera na telefon – same, gdy Velivo jest otwarte |
| Nowe zakładki i hasła z telefonu | Na komputer – komputer pyta, czy dodać |
| Plik z telefonu | **Udostępnij → Velivo**; komputer pyta o zgodę |
| Plik z komputera | Na komputerze 📎 Wyślij plik do: telefon; zapis w Pobranych |

## 6. Prywatność

- Telefon nigdy nie wysyła całego profilu – tylko nowe pozycje, które komputer potwierdza.
- Połączenie jest szyfrowane (AES-GCM) kluczem z parowania; inne telefony w sieci nic nie odczytają.
- Pozycja w czytniku, ukryte elementy i ustawienia zostają w telefonie.
