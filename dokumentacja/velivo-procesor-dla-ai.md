# Velivo Procesor – pełny opis działania (do diagnozy przez inną AI)

Opis zrobiony z kodu źródłowego (`velivo-procesor/`: `Silnik.cs`, `Uczenie.cs`, `ProBalance.cs`, `Konflikty.cs`, `App.cs`, `Dioda.cs`) – stan na 2026-11-25. Wszystkie liczby poniżej są w kodzie; nic nie jest dopisane z pamięci. Na końcu – pytania do diagnozy.

## 1. Czym jest

Samodzielny program Windows (C#, .NET Framework 4.8, WPF + ikona w zasobniku). Zastępuje programy typu CPU Manager / Process Lasso / Armoury Crate w części „sterowanie procesorem”. Co sekundę mierzy komputer, decyduje o jednym z trzech trybów i ustawia go w **planie zasilania Windows** (Intel i AMD), a na AMD dodatkowo **limity mocy przez RyzenAdj**. Obok działa **ProBalance** (obniżanie priorytetu programów w tle, jak w Process Lasso).

Tryby: **Cichy**, **Zrównoważony**, **Turbo**. Wybór użytkownika: `auto` (program decyduje), ręcznie jeden z trzech, `pauza` (tylko mierzy, nic nie zmienia – gdy steruje inny program), `windows` (ustawienia domyślne Windows, program tylko pokazuje).

Uprawnienia: instalator bez administratora (`PrivilegesRequired=lowest`), start z Windows przez `HKCU\...\Run`. Dla RyzenAdj (AMD) potrzebny administrator – program tworzy zadanie Harmonogramu „Velivo Procesor” (`/sc onlogon /rl highest`, jedno „Tak” w UAC), które wtedy zastępuje wpis Run.

## 2. Pętla (co 1 s, osobny wątek, priorytet AboveNormal)

```
Zmierz() → ProgCpu z ProBalance → Decyduj() → (co 2 s) ProBalance.Krok() → blokada uśpienia dla Studio → stan do Velivo Centrum (pamięć współdzielona) → zapis „co zmieniło Velivo” (najwyżej co minutę) → odświeżenie okna
```
Osobno co 60 ms (wątek okna): ikona w zasobniku – kolor obciążenia (zielona < 30 %, żółta < 70 %, czerwona) i mrugająca „dioda dysku” (licznik PDH Disk Bytes/sec).

## 3. Pomiar (`Zmierz`)

| Wielkość | Skąd |
|---|---|
| Obciążenie procesora % | `GetSystemTimes` (różnica idle / kernel+user między pomiarami) |
| Zegar GHz | PDH `\Processor Information(_Total)\% Processor Performance` × `~MHz` z rejestru (`HARDWARE\DESCRIPTION\System\CentralProcessor\0`) |
| Dysk | PDH `PhysicalDisk(_Total)`: `% Idle Time`, `Disk Bytes/sec`, `Current Disk Queue Length` (zajętość % na NVMe bywa fałszywa, dlatego decyzja używa MB/s i kolejki) |
| Karta graficzna % | PDH `\GPU Engine(*engtype_3D)\Utilization Percentage` – suma wszystkich wystąpień, max 100 |
| Temperatura | co 3 s (AMD przez PawnIO co 10 s); źródła po kolei, wybrane raz: 1) tabela pomiarów SMU przez PawnIO (Tctl), 2) WMI LibreHardwareMonitor / OpenHardwareMonitor (CPU Package / Tctl, inaczej najwyższy rdzeń; bez „Distance to TjMax”), 3) WMI `MSAcpi_ThermalZoneTemperature`, 4) PDH Thermal Zone. Czujnik płyty (ACPI), który nie zmienia się o 0,5 °C przez 10 min, uznany za „nie mierzy procesora” → temperatura nieznana (-1). Bez źródła – szukanie co minutę. |
| Program na wierzchu | `GetForegroundWindow` → nazwa procesu (aplikacje ze Sklepu: proces wewnątrz `ApplicationFrameHost`) |
| Obciążenie samego programu | z ProBalance (czas procesu), w % jednego rdzenia |
| Bateria | `GetSystemPowerStatus` (AC == 0) |
| Ekran wyłączony | `RegisterPowerSettingNotification` GUID_CONSOLE_DISPLAY_STATE (0 = wył.) |

Okna „do patrzenia” (sam Velivo Procesor, Menedżer zadań, HWiNFO, Monitor…) i powłoka (pulpit, menu Start, ekran blokady, dwm) nie liczą się jako program – decyzja dotyczy programu pod spodem.

## 4. Decyzja (`Decyduj`) – kolejność reguł (pierwsza pasująca wygrywa)

0. `pauza` / `windows` – nic nie zmienia. Ręczny tryb – ustawiony; przez 2 min po kliknięciu uczy się, że ten program chce tego trybu.
1. **Temperatura**: `Temp ≥ limit` (domyślnie 90 °C, suwak 70–98) albo przewidywana za 10 s (`Temp + trend×10 ≥ limit + 2`, trend = °C/s z ostatnich 10 próbek) → **Cichy**.
2. **„Zawsze ten tryb” dla programu** (lista użytkownika): program na wierzchu – jego tryb; w tle – tylko tryby wyższe niż Cichy (najwyższy z działających), żeby np. program z „zawsze Cichy” przy zegarze nie trzymał całego komputera w Cichym.
3. **Wczytywanie**: dysk > 15 MB/s albo kolejka ≥ 2 przez ≥ 3 s (trzymane 5 s po końcu) **i** procesor > 30 % → **Turbo**.
4. **Gra na karcie**: karta > 75 % i procesor < 50 % (wyjście, gdy karta ≤ 60 % albo procesor > 50 % przez 3 s) → **Zrównoważony** (procesor nie dostaje więcej mocy, karta ma więcej zapasu cieplnego).
5. **Duże obciążenie**: procesor > `ProgWysoki` (start 70 %, samodostrajany 50–85) choć przez 1 s → **Turbo** od razu.
6. **Głosowanie AI** (sekcja 5) – gdy co najmniej 2 głosy mają zdanie.
7. **Zapamiętany program** (≥ 30 s nauki) z wyuczonym trybem innym niż Cichy.
8. **Spoczynek**: procesor < `ProgNiski` (20 %) przez 8 s → **Cichy**, chyba że o tej godzinie zwykle jest ciężka praca (średnia godziny > 40 % z > 120 próbek) – wtedy Zrównoważony.
9. W pozostałych przypadkach → **Zrównoważony**.

Poprawki po wyborze (nie dotyczą przegrzania i „zawsze ten tryb”):
- Cichy zamieniany na Zrównoważony, gdy: kopiowanie (chyba że program zwykle chce Cichego, np. film), działa instalator (`setup`/`install`/`unins*`, albo ≥ 2 procesy msiexec), przez 6 s po przełączeniu / uruchomieniu programu, albo program obciąża swój rdzeń > 80 %.
- Na baterii bez Turbo (Zrównoważony), chyba że temperatura nieznana i obciążenie wysokie > 3 s.
- **Histereza czasowa**: w górę od razu; w dół Turbo → niżej po 3 s (15 s, jeśli Turbo wróciło w ciągu 30 s od wyjścia), Zrównoważony → Cichy po 8 s; po wyjściu z Cichego powrót do Cichego najwcześniej po 20 s.
- **Samodostrajanie progu**: w Cichym procesor > 95 % przez 20 s → `ProgWysoki` −2 (min 50); w Turbo procesor < 30 % (bez gry) przez 60 s → `ProgWysoki` +2 (max 85). `ProgNiski` się nie zmienia.
- **Studio** (program z listy Studio działa, np. DAW): nigdy Cichy (niski zegar = trzaski dźwięku), komputer i ekran nie zasypiają.
- **Ekran wyłączony**: tryb się nie zmienia aż do włączenia ekranu (poza przegrzaniem), bo `PowerSetActiveScheme` budzi monitor.

## 5. Nauka i „AI” (wszystko liczby w pamięci, zapis na dysk co 5 min)

**Profil programu** (`nauka.txt`, P): dla programu na wierzchu średnie obciążenie (jego rdzenia, albo całego procesora, gdy nieznane) i karty z ostatnich ~600 próbek (~10 min) oraz „szczyt” (w górę 20 % różnicy na próbkę, w dół ~5 min). Instalatory się nie uczą. Wyuczony tryb: karta > 70 % i procesor < 50 % → Zrównoważony; procesor > 55 % albo szczyt > 85 % przy procesorze > 30 % → Turbo; procesor < 12 % i szczyt < 40 % → Cichy; inaczej Zrównoważony.

**Ocena skutków** (lekki Q-learning, A): dla programu trzy liczby Q (Cichy/Zrównoważony/Turbo). Co sekundę nagroda dla bieżącego trybu: przycięcie (`lag` = procesor > 85 % albo rdzeń programu > 90 %) – Cichy −2, Zrównoważony −0,5, Turbo +1,5; bez przycięcia – Cichy +1, Zrównoważony +0,5 (−0,3 przy < 15 %), Turbo +0,3 (−1,5 przy < 30 %); temperatura ≥ limit−5 – Turbo −1, Zrównoważony −0,3. Ręczny wybór = nagroda +2 (pozostałe tryby ciągnięte do −1). Uczenie 0,02 (ręcznie 0,05). Decyzja po ≥ 60 próbkach; wygrywa najwyższe Q, przy różnicy ≤ 0,2 cichszy. Bez losowania.

**Głosowanie** (`glosy.txt`): 4 głosy – (1) ocena skutków, (2) profil programu, (3) przewidywanie (średnia z 10 s + nachylenie z 30 s × 5 s, przez progi z histerezą 4 punktów), (4) pora dnia (średnia dla dnia tygodnia i godziny, po > 600 próbkach). Każdy głos ma „trafność” (start 0,6 / 0,6 / 0,5 / 0,4), uczona co sekundę z tego, co faktycznie było potrzebne (przycięcie = Turbo, procesor < `ProgNiski` = Cichy, inaczej Zrównoważony), krok 0,01. Wynik = suma trafności na tryb; Cichy wygrywa przy remisie (≥ 0,9 × Turbo), Turbo wymaga co najmniej 2 głosów za Turbo. Za mało głosów (< 2) → zwykłe reguły.

## 6. Sterowanie

**Plan zasilania Windows** (aktywny plan, wartości AC i DC jednakowe, potem `PowerSetActiveScheme`):

| Ustawienie | Cichy | Zrównoważony | Turbo | Studio |
|---|---|---|---|---|
| Max stan procesora % | `cichy-ghz` / bazowy MHz (5–99) | 100 | 100 | jak tryb |
| Min stan % | = max | max(5, podłoga) | max(30, podłoga) | Turbo+Studio: 100 |
| Boost (tryb podbijania) | 0 wyłączony | 3 (efektywny) | 2 (agresywny) | Turbo+Studio: 2 |
| EPP % (0 wydajność … 100 oszczędność) | 80 | 33 | 0 | 25 (Turbo+Studio: 0) |
| Limit częstotliwości MHz (Intel) | `cichy-ghz`×1000 | 0 (brak) | 0 | jak tryb |
| Próg podnoszenia / obniżania % | 80 / 50 | 50 / 25 | 30 / 15 | 10 / 5 |
| Czas podnoszenia / obniżania (okresy) | 4 / 1 | 2 / 2 | 1 / 4 | 1 / 10 |
| Sposób podnoszenia | 1 (o krok) | 0 (stopniowo) | 2 (od razu) | 2 |
| Parkowanie (min rdzeni %) | jak Windows | jak Windows | jak Windows | 100 |

„Podłoga” = ten sam procent co Cichy (`cichy-ghz`, domyślnie 1,4 GHz) – żaden tryb nie schodzi poniżej. Procesory hybrydowe (Intel 12+ / Core Ultra, wykrywane przez `GetSystemCpuSetInformation` EfficiencyClass): te same max/min/EPP/limit także dla drugiej klasy rdzeni. Tryb autonomiczny AMD CPPC i „co ile ms Windows sprawdza obciążenie” (PerfCheck) – zawsze jak przed programem (u właściciela PerfCheck = 5000 ms, zostawione). Zapis pomijany, gdy wartość już taka. Suwak „Tryb zasilania” Windows 11 (nakładka) na czas sterowania ustawiany na Zrównoważony, potem wraca.

**AMD – PawnIO** (2026-11-25: RyzenAdj/WinRing0 zastąpiony – Defender blokuje WinRing0 jako VulnerableDriver). Folder `PawnIO\` (PawnIO_setup.exe 2.2.0 + moduł RyzenSMU.bin 0.2.12, sumy SHA-256 w `zbuduj-procesor.ps1`); program jako administrator sam instaluje sterownik, gdy go brak, i usuwa stary WinRing0 z folderu Velivo. Odczyt: tabela pomiarów SMU (wersje 0x37…0x65); zapis: skrzynka MP1 0x14/0x15/0x16/0x19 (+ PSMU 0x31–0x34 na Renoir/Cezanne), jak G-Helper. Limity od fabrycznych BIOS (`AmdSilnik.cs`). 3 nieudane zapisy z rzędu → moc AMD wyłączona do restartu programu.

**Bezpieczeństwo i porządek:**
- Przed pierwszą zmianą zapis wartości Windows (`windows-przed.txt`); przy zamknięciu, wylogowaniu i deinstalacji (`--przywroc`) przywracane – **transakcyjnie**: wraca tylko to, co Velivo zapisało i czego nikt potem nie zmienił (`velivo-zapisy.txt`).
- Zmiana planu zasilania przez użytkownika – stary plan wraca do swoich wartości, nowy zapamiętany od zera.
- Windows 3 razy z rzędu odrzuca ustawienia → program przestaje sterować (tryb `windows`) i przywraca swoje zmiany.
- Konflikty: lista znanych programów sterujących (Armoury Crate, Lenovo Vantage, MSI Center, Omen, ThrottleStop, Intel XTU, Process Lasso, CPU Manager, Ryzen Controller, UXTU) – tylko ostrzeżenie. Co 5 s sprawdza, czy ktoś zmienił Max/Min/Boost/EPP – liczy obce zmiany na minutę, **nie walczy** (przyjmuje cudzy stan).

## 7. ProBalance (co 2 s)

Gdy cały procesor > 60 % – program **w tle** (nie na wierzchu) z priorytetem Normalny, zużywający > 10 % całego procesora przez 2 kolejne pomiary (~4 s) → priorytet BelowNormal + EcoQoS Windows 11 (`SetProcessInformation` ProcessPowerThrottling). Przywracany, gdy: trafi na wierzch, przez 3 pomiary (~6 s) zużywa < 3 %, cały procesor < 35 % przez 3 pomiary, ProBalance wyłączony albo program zamknięty. Pomijane: procesy systemowe, dźwięk (audiodg), Defender, vlc, OBS, programy Velivo, programy z „nie uspokajaj”. Priorytet z profilu (⬆ Wysoki / ⬇ Niski) ustawiany na stałe i cofany po usunięciu profilu. W `pauza` – wszystko cofnięte.

## 8. Pliki (`%LOCALAPPDATA%\Velivo\Procesor\`)

`ustawienia.txt` (klucz=wartość: tryb, cichy-ghz, temp-limit, probalance, amd-0/1/2, suwak-w11…), `nauka.txt` (P profil, A ocena Q, H godziny, R zawsze-tryb, Q priorytet, S Studio, B bez ProBalance, T progi), `glosy.txt` (W trafność, D dzień×godzina), `windows-przed.txt`, `velivo-zapisy.txt`.

## 9. Co wiemy z praktyki (właściciel)

- NUCBOX (2026-11-24): przy wyłączonym ekranie zmiana trybu budziła monitor (`PowerSetActiveScheme`) – poprawione: przy zgaszonym ekranie tryb stoi (oprócz przegrzania); usunięta reguła „ekran wyłączony = Cichy”.
- PerfCheck 5000 ms (z CPU Manager v40) daje u właściciela ciszę; ustawienie 30 ms dawało skoki zegara i wentylator – dlatego program go nie rusza.
- Reszta działania na sprzęcie – nie mierzona przez Claude (brak dostępu do komputerów).

## 10. Pytania do diagnozy

1. Czy kolejność reguł (temperatura → zawsze → wczytywanie → karta → obciążenie → AI → profil → spoczynek) jest dobra? Czy „Turbo od 1 s ponad 70 %” nie jest za nerwowe (krótkie skoki, np. otwarcie karty przeglądarki)?
2. Wartości planu zasilania (EPP 80/33/0, boost 0/3/2, progi i czasy podnoszenia) – czy są sensowne dla Intel Speed Shift i AMD CPPC? Czy limit częstotliwości (Freq) i Max% razem nie kolidują?
3. Cichy = Min% = Max% (stały zegar ok. 1,4 GHz). Czy to dobre dla ciszy, czy lepiej pozwolić schodzić niżej?
4. „Podłoga” w Zrównoważonym i Turbo (Min% = procent 1,4 GHz, w Turbo co najmniej 30 %) – czy nie zwiększa ciepła w spoczynku?
5. Q-learning: nagrody i tempo (0,02) – czy ocena nie będzie przekrzywiona w stronę Cichego (+1 za każdą spokojną sekundę) i czy kara za przycięcie (−2) wystarczy?
6. Głosowanie: czy trafność „co było potrzebne teraz” nie faworyzuje głosu, który po prostu powtarza bieżący stan?
7. Temperatura z ACPI bywa fałszywa; przewidywanie +10 s z trendu z 10 próbek – czy nie da fałszywych alarmów przy krótkim skoku?
8. Profile RyzenAdj (W) – czy sensowne dla laptopów AMD 15–28 W i dla mini-PC (NUCBOX)?
9. ProBalance: progi 60 % / 10 % / 4 s – czy nie obniża za często procesów, które powinny działać (np. kompilacja w tle, eksport wideo)?
10. Co jeszcze warto mierzyć, żeby decyzje były trafniejsze (np. opóźnienia DPC, przerwy dźwięku, czas klatki), bez dużego kosztu?
