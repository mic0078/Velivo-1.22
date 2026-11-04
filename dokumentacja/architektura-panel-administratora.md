# Velivo – Panel administratora: architektura (centrum zarządzania wszystkimi serwerami)

Stan: 2026-11-04. Oznaczenia: ✓ jest w kodzie · ◐ częściowo · ✗ do zrobienia. Nic nie jest „gotowe” bez testu na NUCBOX i Ryzenie.

## 0. Zasada

- **Jeden panel ≠ jeden serwer ani jedna baza.** Serwerów może być 2, 5 albo 20. Każdy działa samodzielnie i trzyma swoje dane, a panel na **dowolnym** z nich widzi i steruje **wszystkimi**.
- Panel to okno Velivo na serwerze (dziś: 🌍 → Panel administratora). Ten sam panel otwarty na NUCBOX i na Ryzenie pokazuje to samo: stan całej infrastruktury.
- Administrator = Ty. Panel jest dostępny tylko na Twoich serwerach (członkach infrastruktury) albo na Twoim komputerze, który dołączysz jako konsolę administracyjną (rola „konsola”, punkt 2).
- Szyfrowanie end-to-end zostaje. Serwery i panel widzą **metadane**: kto, do kogo, kiedy, rozmiar, stan i skrót SHA-256. Nie widzą treści wiadomości. Treść czytasz na swoich urządzeniach (historia na serwerach – projekt B w `architektura-serwery-i-historia.md`).

## 1. Jak panel rozmawia z serwerami

| Warstwa | Co robi | Stan |
|---|---|---|
| Tożsamość | każdy serwer: klucz ECDSA + certyfikat infrastruktury; sprawdzany przy każdym połączeniu (`p2p-srvid`) | ✓ |
| Drogi | sieć domowa (UDP, porty 41918/41919) + łącze serwerów przez internet (`KomPeerLinks`, TCP przez pośrednika, 443 / porty 41920–41929) | ✓ |
| Stan na żywo | co minutę każdy serwer wysyła wszystkim `p2p-srvinfo` (wersja, port, adres, router, połączeni, skrzynka, odciski skrzynki i historii) | ✓ |
| **Zapytania panelu** | `p2p-adm-q` → `p2p-adm-r`: panel pyta wybrany albo każdy serwer o listę (skrzynka, osoby, dziennik, błędy, zasoby), odpowiedź paczkami ≤ 8 KB, z numerem zapytania | ✗ |
| **Polecenia panelu** | `p2p-adm-cmd`: {id polecenia, serwer docelowy / wszystkie, czynność, parametry, czas, podpis kluczem serwera-nadawcy}; odbiorca sprawdza certyfikat i podpis, wykonuje raz (id), odsyła wynik `p2p-adm-ack` | ✗ |
| **Konfiguracja** | ustawienia wspólne jako wersjonowany dokument (`cfg`, numer wersji + podpis); serwer przyjmuje wyższą wersję, starsza nie nadpisuje; ustawienia lokalne (port, adres w domu) osobno dla serwera | ◐ (dziś osobne mechanizmy: `boxDays`, konta, usunięci, infrastruktura) |
| **Dziennik operacji administratora** | każde polecenie i zmiana konfiguracji → zdarzenie `adm` w dzienniku zdarzeń serwerów (`KomEventLog` – replikowany, bez duplikatów, uzupełniany po przerwie) | ✗ (zdarzenia już są dla przyjęć/doręczeń) |
| Odporność | serwer wyłączony dostaje zaległe polecenia i konfigurację po powrocie (dziennik zdarzeń „mam do N”) | ◐ (mechanizm jest, polecenia jeszcze nie) |

Polecenie zawsze idzie przez dziennik zdarzeń. Dzięki temu dochodzi także do serwera, który był wyłączony, i każdy serwer wykonuje je dokładnie raz.

## 2. Moduły panelu

### 2.1 Serwery (infrastruktura)
| Funkcja | Stan |
|---|---|
| lista wszystkich serwerów: stan, wersja, port, adres, router, połączeni, działa od | ✓ Przegląd / Infrastruktura |
| dodawanie: „Czy to Twój serwer?” po obu stronach, „Dołącz zaznaczony” | ✓ |
| usuwanie: odłączenie z nowym kluczem infrastruktury | ✓ |
| wykrywanie awarii: brak wieści 3 min, brak łącza → „Wymaga uwagi” | ✓ |
| zastępstwo portów wyłączonego serwera (UPnP) | ✓ |
| przywracanie synchronizacji po awarii: dosyłanie brakujących zdarzeń, skrzynki, plików | ✓ (do potwierdzenia testami) |
| sterowanie **zdalnym** serwerem: włącz / wyłącz, uruchom od nowa, sprawdź porty, otwórz zaporę – z panelu na innym serwerze | ✗ (dziś tylko ten komputer) – polecenia `p2p-adm-cmd` |
| zasoby: procesor, pamięć, dysk (folder danych, skrzynka), czas pracy, liczba połączeń – w `srvinfo` + wykres | ✗ |
| role i priorytety: główny serwer dla 443, kolejność zastępstwa, „tylko zapas”, konsola administracyjna | ✗ |

### 2.2 Dane (rozproszone, bez jednej bazy)
| Dane | Gdzie są | Panel | Stan |
|---|---|---|---|
| wiadomości czekające (skrzynka) | każdy serwer, kopie u członków | lista, usuwanie na wszystkich, odcisk SKRZYNKA | ✓ |
| pliki / głosówki / filmy czekające | jw. + SHA-256 | lista, los pliku, usuwanie | ✓ |
| historia doręczeń | dziennik zdarzeń (replikowany) | odcisk HISTORIA, diagnostyka | ✓ |
| historia rozmów (treść) | urządzenia (E2E); na serwerach – projekt B | tylko metadane | ✗ (B) |
| połączenia głosowe / wideo | dziś tylko urządzenia | rejestr połączeń (kto, kiedy, długość, przez który serwer TURN) | ✗ |
| kontakty | każdy serwer, wymiana między serwerami | Osoby i urządzenia, usuwanie na wszystkich | ✓ |
| konta (numer + PIN) | każdy serwer, wymiana | Konta: lista, usuwanie | ✓ |
| backup i odtwarzanie | kopia ☁ urządzeń na serwerze; kopia Velivo (folder) | lista kopii na wszystkich serwerach, „zrób kopię serwera teraz”, odtworzenie na nowym serwerze | ✗ (panel) |
| spójność | odciski SKRZYNKA/HISTORIA, wpisy RÓŻNICA | ✓ diagnostyka; ✗ przycisk „wyrównaj teraz” (dosłanie brakującej pozycji) |
| konflikty | scalanie po identyfikatorze (bez nadpisywania); konta – nowsze wygrywa, numer – pierwszy | ✗ lista konfliktów w panelu |

### 2.3 Użytkownicy
| Funkcja | Stan |
|---|---|
| konta: lista, usuwanie | ✓ |
| tworzenie konta przez administratora (imię, numer, jednorazowy PIN) | ✗ |
| blokowanie / odblokowanie (serwery odrzucają połączenie i wiadomości od zablokowanego; replikowane) | ✗ |
| urządzenia osoby (telefony, komputery), ostatnio widziane, przez który serwer | ◐ (osoby tak, urządzenia osoby – nie) |
| uprawnienia: zwykły / moje urządzenie / serwer / konsola; nowe konta z internetu: za zgodą / otwarte / wyłączone | ◐ |
| sesje i aktywne połączenia na każdym serwerze (rozłącz) | ◐ (widać połączonych, bez „rozłącz”) |
| historia aktywności osoby (połączenia, przyjęcia, doręczenia) | ◐ (dziennik skrzynki + diagnostyka, bez widoku osoby) |

### 2.4 Komunikacja
| Funkcja | Stan |
|---|---|
| stan każdej wiadomości i pliku: przyjęta, kopia, do odbiorcy, potwierdzona | ✓ |
| kolejki (skrzynka) na każdym serwerze; „wyślij ponownie teraz”, „przenieś na serwer, z którym odbiorca jest połączony” | ◐ (usuwanie tak, ponowienie ręczne – nie) |
| diagnostyka błędów przesyłania (odmowy z powodem, przerwania) | ✓ |
| monitor LAN / internet: kto przez sieć domową, kto przez internet, przez który serwer | ◐ |
| test połączenia z wybranym urządzeniem (ping przez każdą drogę) | ✗ |

### 2.5 Monitoring i diagnostyka
| Funkcja | Stan |
|---|---|
| centralne dzienniki wszystkich serwerów (skrzynka – ✓; błędy Velivo i diagnostyka – dziś każdy serwer osobno) | ◐ – zapytania `p2p-adm-q` |
| błędy i ostrzeżenia, „Wymaga uwagi” | ✓ (ten serwer + stan innych) |
| historia awarii (serwer zniknął / wrócił, z czasem) | ✗ (zdarzenie `srv-down/up`) |
| historia synchronizacji (uzupełnienia po starcie, porównania) | ✓ diagnostyka |
| statystyki zasobów i ruchu | ✗ |
| historia operacji administratora | ✗ (zdarzenia `adm`) |

### 2.6 Konfiguracja całego systemu
Jeden dokument konfiguracji, wersjonowany i podpisany, rozsyłany dziennikiem zdarzeń do wszystkich serwerów. Każdy serwer stosuje go u siebie:
- przechowywanie skrzynki (dni) ✓, nowe konta z internetu ◐ (dziś na każdym serwerze osobno), wspólny schowek, limity (bez limitów – decyzja właściciela), adres domu (nazwa), porty zapasowe, zastępstwo portów wł./wył., okna aktualizacji, kopie zapasowe (kiedy, gdzie, ile trzymać), progi ostrzeżeń.
- Ustawienia tylko dla jednego serwera (port, adres w sieci domowej) – w panelu przy tym serwerze, zmiana poleceniem.

### 2.7 Automatyzacja (wszystko domyślnie włączone, z możliwością wyłączenia i ręcznego uruchomienia)
synchronizacja i replikacja ✓ (bez przełącznika) · uzupełnianie po awarii ✓ · zastępstwo portów ✓ · kontrola spójności co minutę ✓ · backup serwera ✗ · porządki skrzynki wg „Przechowuj” ✓ · ponawianie kolejek co minutę ✓. W panelu: stan każdego mechanizmu (ostatnie uruchomienie, wynik), „uruchom teraz”, „wstrzymaj”.

### 2.8 Klucze sprzętowe
lista ✓, dodaj / sprawdź / usuń ✓, blokada komunikatora ✓; dalej (B/C): zatwierdzanie nowego urządzenia i odzyskiwanie historii kluczem.

## 3. Wygląd i obsługa (to, co już jest w panelu)
- Na górze kafelki, które prowadzą do szczegółów, i ramka „Wymaga uwagi” z przyciskami czynności.
- Zakładki: Przegląd, Serwery, Skrzynka, Osoby, Dziennik skrzynki, Błędy, Informacje, Infrastruktura, Pliki, Diagnostyka, Konta, Klucze sprzętowe, Ustawienia.
- Menu kontekstowe (prawy przycisk) na każdej tabeli: czynności dla zaznaczonych wierszy, kopiuj, odśwież.

## 4. Kolejność wdrażania (proponowana)
1. Protokół zapytań i poleceń `p2p-adm-q` / `p2p-adm-cmd` z dziennikiem operacji. Bez tego panel steruje tylko „swoim” serwerem.
2. Zdalne sterowanie serwerami i centralne dzienniki (błędy, diagnostyka ze wszystkich serwerów w jednej tabeli).
3. Zasoby, historia awarii, role i priorytety.
4. Użytkownicy: blokowanie, tworzenie konta, urządzenia osoby, rozłączanie sesji.
5. Jeden dokument konfiguracji (migracja dzisiejszych osobnych ustawień bez utraty wartości).
6. Backup / odtwarzanie serwera z panelu.
7. Historia rozmów i połączeń (projekt B) oraz klucze sprzętowe (C).

Każdy krok: kompilacja, testy klas, potem test na NUCBOX i Ryzenie, zanim przejdziemy do następnego.
