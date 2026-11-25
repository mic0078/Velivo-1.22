# Velivo Procesor – projekt silnika AMD (i Intel)

Projekt, bez zmian w kodzie. Podstawa: kod Velivo Procesor (`velivo-procesor/Silnik.cs`) i kod źródłowy RyzenAdj (FlyGoat/RyzenAdj, stan z 2026-11-17: `main.c`, `lib/api.c`, `lib/win32/osdep_win32.cpp`). Sprzęt referencyjny: Ryzen 7 5825U (Cezanne / Barcelo, 8C/16T) w NUCBOX.

Oznaczenia: **[KOD]** – sprawdzone w kodzie (Velivo albo RyzenAdj); **[TEST]** – wymaga pomiaru na sprzęcie; **[WIEDZA]** – wiedza ogólna o platformie, niepotwierdzona na NUCBOX.

---

## 1. Co RyzenAdj daje na Cezanne – z kodu

### 1.1 Zapis (polecenia do SMU – „mailbox”), rodzina `FAM_CEZANNE` [KOD]

| Parametr | Opcja | Komenda SMU | Uwagi |
|---|---|---|---|
| STAPM limit (moc długotrwała) | `--stapm-limit` | 0x14 | mW |
| PPT Fast (moc chwilowa) | `--fast-limit` | 0x15 | mW |
| PPT Slow (moc średnia) | `--slow-limit` | 0x16 | mW |
| Czas Slow PPT | `--slow-time` | 0x17 | s |
| Czas STAPM | `--stapm-time` | 0x18 | s |
| Granica temperatury Tctl | `--tctl-temp` | 0x19 | °C |
| TDC VDD / SoC (prąd ciągły) | `--vrm-current` / `--vrmsoc-current` | 0x1A / 0x1B | mA |
| EDC VDD / SoC (prąd szczytowy) | `--vrmmax-current` / `--vrmsocmax-current` | 0x1C / 0x1D | mA |
| PSI0 VDD / SoC | `--psi0-current` / `--psi0soc-current` | 0x1E / 0x1F | próg trybu fazy zasilacza |
| Prochot deassertion ramp | `-y` | 0x20 | |
| APU PPT (A+A z kartą AMD) | `--apu-slow-limit` | 0x21 | **nie dotyczy NUCBOX** (brak osobnej karty) |
| STT APU / dGPU (temperatura obudowy) | `--apu-skin-temp` / `--dgpu-skin-temp` | 0x38 / 0x39 | działa tylko z czujnikiem obudowy |
| Moc przy temp. obudowy | `--skin-temp-limit` | 0x53 | j.w. |
| Power Saving / Max Performance | `--power-saving` / `--max-performance` | ALIB 0x12 / 0x11 | „zależy od generacji, urządzenia i producenta” (opis w RyzenAdj) |
| Curve Optimizer (wszystkie / rdzeń / grafika) | `--set-coall` / `--set-coper` / `--set-cogfx` | 0x55 / 0x54 / 0x64 | obniżenie napięcia – ryzyko zawieszeń |
| OC (stały zegar / napięcie) | `--oc-clk`, `--oc-volt`, `--enable-oc` | 0x31–0x33 | wyłącza boost i oszczędzanie – **nie używać** |
| Zegary SoC / FCLK / VCN / LCLK / GFX min-max | – | **brak dla Cezanne** w kodzie | niedostępne |
| **STTv2** | – | **brak w RyzenAdj** | niedostępne przez RyzenAdj |

Wniosek: z 4 używanych dziś parametrów (STAPM, Fast, Slow, Tctl) możemy przejść do ~10 rozsądnych; reszta jest niedostępna albo niebezpieczna.

### 1.2 Odczyt – tabela pomiarów (PM table, wersje 0x400001–0x400005 = Cezanne) [KOD]

Jedno odświeżenie tabeli daje naraz:

| Grupa | Pola (`--info`) | Wartość dla silnika |
|---|---|---|
| Moc | STAPM LIMIT / VALUE, PPT FAST LIMIT / VALUE, PPT SLOW LIMIT / VALUE, socket power, moc SoC, moc każdego rdzenia | prawdziwy pobór i wykorzystanie limitów |
| Prąd | TDC VDD / SoC LIMIT i VALUE, EDC VDD / SoC LIMIT i VALUE | czy hamuje zasilanie płyty |
| Temperatura | THM LIMIT / VALUE CORE (Tctl), STT LIMIT / VALUE APU, temperatura rdzeni, L3, grafiki | ogranicznik cieplny, czujnik obudowy |
| Zegar | CCLK Boost SETPOINT, CCLK BUSY VALUE, zegar każdego rdzenia, L3, FCLK, zegar pamięci, grafika | czy zegar spada przy limicie |
| Czasy | STAPM TIME, SLOW TIME | odczyt fabrycznych okien |

`--dump-table` wypisuje całą tabelę (wszystkie przesunięcia) – tylko do kalibracji i diagnozy, nie do pracy ciągłej.

### 1.3 Jak RyzenAdj dostaje się do procesora [KOD]

Windows: sterownik **WinRing0** (`OlsApi`, dostęp do PCI – skrzynka SMU) i **inpoutx64** (`MapPhysToLin` – mapowanie tabeli pomiarów). Wydanie zawiera też `libryzenadj.dll` (te same funkcje bez osobnego procesu).

⚠ **Bezpieczeństwo [WIEDZA, TEST]:** WinRing0 to znany sterownik z luką (CVE-2020-14979) – w 2025 r. Microsoft Defender zaczął go oznaczać jako niebezpieczny i może go blokować / usuwać (lista zablokowanych sterowników Windows). Skutek dla Velivo: RyzenAdj może przestać działać po aktualizacji Windows, a sam załadowany sterownik to furtka podniesienia uprawnień dla innych programów. Trzeba to uwzględnić w projekcie (pkt 4.8).

---

## 2. Odpowiedzi na 10 punktów

**1. Pełny odczyt.** `--info` daje wszystko potrzebne do sterowania (1.2); `--dump-table` – tylko diagnoza. Dziś Velivo czyta z tego wyłącznie temperaturę, co 10 s, nowym procesem [KOD]. Projekt: `libryzenadj.dll` w procesie (`init_ryzenadj` raz, `refresh_table` + odczyty co 1 s) – bez uruchamiania procesu. Koszt [TEST], oczekiwany < 1 ms.

**2. STAPM / Fast / Slow.** Zależności (zachowanie SMU) [WIEDZA]:
- **Fast** – limit chwilowy (dziesiątki ms): decyduje o szczytach – szybkość otwierania programów, ale też „szpilki” wentylatora.
- **Slow** – średnia z `slow-time` (kilka s): decyduje o krótkich zadaniach.
- **STAPM** – średnia z `stapm-time` (dziesiątki–setki s), dodatkowo zależna od temperatury obudowy na laptopach: moc długotrwała.
- Reguła poprawności: **Fast ≥ Slow ≥ STAPM**. Okna czasowe są ważniejsze niż same waty: długie okno STAPM = krótkie zadania na pełnej mocy, hamowanie tylko długich.
- Dziś [KOD]: okien nie ustawiamy, a Cichy ma Fast 28 W przy Slow 15 W – szpilki mocy w „cichym” trybie.

**3. TDC / EDC.** Limity prądu chronią sekcję zasilania płyty (VRM) – ich wartości ustala producent pod konkretną płytę. Podnoszenie = ryzyko dla płyty, której parametrów nie znamy. Obniżanie dławi wydajność jedno- i wielowątkową szybciej niż moc. **Projekt: tylko odczyt** (czy hamują). Zapis – nie.

**4. APU PPT i STTv2.** APU PPT – tylko platformy z osobną kartą AMD (nie NUCBOX). STTv2 – **brak w RyzenAdj** [KOD]. STT (v1) – polecenie i odczyt są dla Cezanne [KOD], ale działa tylko z czujnikiem obudowy podłączonym przez producenta; w mini-PC częsty brak [TEST: STT VALUE zmienia się z obciążeniem, czy stoi / 0].

**5. Monitorowanie.** Co 1 s z tabeli: socket power, STAPM/Slow/Fast VALUE ÷ LIMIT, TDC/EDC VALUE ÷ LIMIT, Tctl, STT, zegar (setpoint / busy, rdzenie). Do okna: „pobór X W, wykorzystanie: długotrwała 92 %, chwilowa 40 %, temperatura 78/95 °C, prąd 60 %”.

**6. Throttling i przyczyna.** Ogranicznik = parametr z wykorzystaniem ≥ 97 % przez ≥ 2 s **i** zegar poniżej możliwego przy tym obciążeniu (CCLK setpoint spada / zegar rdzeni < boost). Kolejność rozstrzygania remisu: temperatura → STT → prąd (EDC, TDC) → Fast → Slow → STAPM. Wynik słowami: „hamuje: moc długotrwała (STAPM)” / „nic – procesor ma zapas”. Bez spadku zegaru limit na 100 % nie jest throttlingiem (procesor po prostu pracuje).

**7. Dynamiczne sterowanie.** Procesor ma własne płynne pętle (STAPM z oknem, Tctl, STT) działające co ms. Silnik Velivo **nie** przestawia watów co sekundę – ustawia cele (temperatura, moc długotrwała, okna) i koryguje je powoli (minuty) na podstawie telemetrii (pkt 4.5).

**8. Nadpisywanie.** Co 10 s porównanie LIMIT z tabeli z ostatnio ustawionym. Różnica = BIOS / EC / inny program (typowo po wybudzeniu, zmianie zasilacza – [TEST] na NUCBOX). Reakcja: po wybudzeniu – ponowne ustawienie od razu (dziś tego nie ma [KOD]); w trakcie pracy – ponowienie najwyżej 3 razy w 10 min, potem ostrzeżenie i przyjęcie cudzego stanu (bez walki, jak dziś przy planie zasilania).

**9. Wartości fabryczne.** Odczyt przy pierwszym uruchomieniu **przed jakąkolwiek zmianą**, po świeżym starcie (jeśli Velivo zmieniało coś wcześniej – kalibracja po restarcie): STAPM / Fast / Slow LIMIT, STAPM TIME, SLOW TIME, Tctl LIMIT, STT LIMIT. Zapis transakcyjny jak dla planu Windows: przy zamknięciu, wylogowaniu, deinstalacji i awarii wraca każdy parametr, który Velivo zmieniło, do zapisanej wartości fabrycznej. Dziś [KOD] wraca profil Turbo (25/35/30 W, 88 °C) – błąd. Ograniczenie [WIEDZA]: RyzenAdj nie ma polecenia „przywróć fabryczne” – tylko zapis odczytanej wartości; restart komputera przywraca zawsze wszystko (SMU startuje z BIOS).

**10. CPPC a Windows.** Na Cezanne zegar wybiera CPPC (procesor + Windows) co ms; plan zasilania ustawia EPP, minimum/maksimum, boost [KOD Velivo]. Tryb autonomiczny CPPC (`Autonom`) Velivo przywraca do wartości sprzed programu, a komentarz w kodzie mówi co innego [KOD – niezgodność]. Zasada projektu: **Windows / CPPC decyduje o zegarze (ms), RyzenAdj o budżecie mocy i temperatury (s–min), Velivo tylko o skłonności (EPP) i budżecie.** Bez stałego minimum zegaru (dziś 1,4 GHz we wszystkich trybach [KOD]) – minimum jak w Windows.

---

## 3. Cztery poziomy pracy (AMD)

Wartości **względne do fabrycznych z kalibracji** (F = fabryczny limit danego komputera) – nie sztywne waty. Liczby w W poniżej tylko jako przykład dla F_STAPM = 15 W.

| Parametr | Oszczędny | Zrównoważony | Wydajny | Uzasadnienie |
|---|---|---|---|---|
| EPP (plan Windows) | 80 | 33 (jak Windows) | 10 | skłonność CPPC; 0 nie daje zauważalnie więcej niż 10, a podnosi pobór w spoczynku [TEST] |
| Boost | włączony, efektywny | efektywny | agresywny | wyłączenie boostu zabija wydajność jednego rdzenia (otwieranie programów) – Oszczędny ogranicza moc, nie zegar |
| Max stan procesora | 100 % | 100 % | 100 % | ciszę daje budżet mocy, nie obcięty zegar |
| Min stan procesora | jak Windows | jak Windows | jak Windows | stałe minimum = pobór przy lekkiej pracy |
| STAPM | F × 0,8 | F | min(F × 1,3; granica z kalibracji cieplnej) | moc długotrwała = ciepło i wentylator |
| Slow | = STAPM | F_slow | F_slow (≥ STAPM) | |
| Fast | = Slow (bez szpilek) | F_fast | F_fast | Fast powyżej Slow = szpilki wentylatora; w Wydajnym szpilki dają reakcję |
| STAPM time | fabryczny | fabryczny | dłuższy (do zbadania) | krótkie zadania na pełnej mocy |
| Tctl | F_tctl − 15 °C | F_tctl − 5 °C | F_tctl | procesor sam łagodnie zwalnia przy cieple; nigdy powyżej fabrycznego |
| STT | jeśli czujnik: niższy | fabryczny | fabryczny | tylko gdy czujnik działa |
| TDC / EDC | fabryczne | fabryczne | fabryczne | ochrona płyty – bez zmian |

**Inteligentny** – nie czwarty zestaw liczb, tylko wybór i korekta powyższych:
- spoczynek / film / przeglądanie → Oszczędny (ale boost działa – programy otwierają się szybko);
- program na wierzchu z pełnym rdzeniem albo trwałe obciążenie wielu rdzeni → Wydajny;
- reszta → Zrównoważony;
- korekta budżetu co kilka minut z telemetrii (4.5); przejścia z histerezą jak dziś (w dół wolno, w górę szybko);
- Studio (DAW) → co najmniej Zrównoważony, bez zmian budżetu w trakcie nagrywania.

---

## 4. Architektura silnika

1. **Warstwa dostępu (`AmdSmu`)** – `libryzenadj.dll` w procesie: `init_ryzenadj` raz, `refresh_table` co 1 s, gettery, settery. Zapasowo dzisiejszy `ryzenadj.exe`. Gdy brak administratora / sterownik zablokowany – silnik AMD wyłączony słowami („Windows zablokował sterownik dostępu do procesora”), reszta Velivo działa (plan Windows).
2. **Kalibracja (`AmdProfil`, raz + na żądanie)** – fabryczne limity, okna, Tctl, STT, TDC/EDC (tylko zapis do pliku); test czujnika obudowy; zapis `amd-fabryczne.txt` z wersją tabeli i nazwą procesora (inny procesor / BIOS = kalibracja od nowa).
3. **Telemetria (`AmdStan`)** – co 1 s: moc, wykorzystanie limitów, temperatury, zegar; średnie 10 s / 60 s / 5 min.
4. **Diagnoza ogranicznika** – pkt 2.6; słowami w oknie i w dzienniku.
5. **Regulator budżetu (powolny, co 2–5 min)** – tylko w Wydajnym / Inteligentnym:
   - temperatura średnia < cel − 8 °C i hamuje STAPM → STAPM +1 W (do granicy z kalibracji);
   - temperatura ≥ cel − 2 °C albo wentylator głośny (jeśli odczyt obrotów jest dostępny [TEST]) → STAPM −1 W (do F × 0,8);
   - nic nie hamuje → bez zmian (brak zysku z większego limitu);
   - granica górna = wynik kalibracji cieplnej (pkt 5), nigdy ponad nią.
6. **Strażnik** – nadpisania (2.8), wybudzenie (`PBT_APMRESUMEAUTOMATIC` → ponowne ustawienie), zmiana zasilacza.
7. **Przywracanie** – transakcyjne: tylko parametry zmienione przez Velivo, do wartości z kalibracji; przy zamknięciu, wylogowaniu, deinstalacji (`--przywroc`), a po awarii – przy następnym starcie przed czymkolwiek.
8. **Bezpieczeństwo sterownika** – sprawdzenie przy starcie, czy sterownik się załadował; brak = jasny komunikat, bez prób obejścia. Do rozważenia długoterminowo: alternatywa bez WinRing0 (np. sterownik podpisany przez inny projekt) – [TEST/decyzja właściciela].

Narzut: odczyt tabeli co 1 s (pamięć), zapis limitów rzadko – mniej niż dziś (proces `ryzenadj.exe` co 10 s znika).

---

## 5. Intel (dopisane na prośbę właściciela)

**Co realnie da się zrobić bez sterownika jądra [WIEDZA]:**
- plan zasilania: EPP, boost, max % i limit MHz, osobno dla rdzeni P i E (hybrydowe) – **już jest** [KOD];
- odczyt przyczyny ograniczenia przez liczniki Windows: `\Processor Information(*)\% Performance Limit` i `Performance Limit Flags` (powód obniżenia zegaru przez firmware: temperatura / moc) – [TEST, które komputery je wypełniają]; działa także na AMD – tani zapas, gdy RyzenAdj niedostępny;
- pobór mocy: liczniki `Energy Meter` (EMI, RAPL wystawiony przez Windows) na części laptopów [TEST];
- EcoQoS / priorytety (ProBalance) – już jest.

**Czego nie robić:** PL1/PL2/Tau i napięcia przez MSR/MMIO wymagają sterownika (ten sam problem co WinRing0) i często są zablokowane przez producenta (BIOS lock) – poza zakresem; od tego jest Intel XTU / ThrottleStop.

**Poziomy Intel:** jak w tabeli AMD, ale tylko kolumny planu Windows (EPP, boost, max %) + diagnoza z `% Performance Limit`. Oszczędny = EPP 80, boost efektywny (nie wyłączony), bez stałego minimum.

---

## A. Czego dziś nie wykorzystujemy
Okna czasowe STAPM / Slow; telemetria mocy i wykorzystania limitów (czytamy tylko temperaturę); diagnoza ogranicznika; wykrywanie nadpisania i ponowne ustawienie po wybudzeniu; odczyt i przywracanie wartości fabrycznych (dziś przywracamy Turbo); biblioteka w procesie zamiast procesu co 10 s; czujnik obudowy (STT); liczniki Windows `% Performance Limit` (AMD i Intel).

## B. Dostępne i potwierdzone w kodzie RyzenAdj dla Cezanne
STAPM, Fast, Slow, slow-time, stapm-time, Tctl, TDC/EDC VDD i SoC (zapis i odczyt), PSI0, prochot ramp, STT APU (zapis i odczyt), power-saving / max-performance, Curve Optimizer, OC; tabela: moc całkowita i rdzeni, SoC, temperatury, zegary rdzeni / L3 / FCLK / pamięci / grafiki, CCLK setpoint i busy. **Niedostępne:** zegary SoC/FCLK/VCN/LCLK/GFX, STTv2, APU PPT (nie dotyczy NUCBOX).

## C. Wymaga testów na NUCBOX
Fabryczne limity i okna; czy limity trzymają się po wybudzeniu; czy STT działa; power-saving / max-performance (pomiar mocy); krzywa moc ↔ temperatura ↔ wentylator (kalibracja cieplna); długość okna STAPM a wydajność krótkich zadań; czy WinRing0 nie jest blokowany przez Defender; liczniki `% Performance Limit`; EPP 0 vs 10 (pobór w spoczynku); tryb autonomiczny CPPC (`powercfg /qh`).

## D. Architektura
Sekcja 4: dostęp w procesie → kalibracja → telemetria → diagnoza → powolny regulator budżetu → strażnik → przywracanie transakcyjne. Windows/CPPC = zegar (ms), RyzenAdj = budżet (s–min), Velivo = skłonność i cele.

## E. Testy i pomiar korzyści
Narzędzia: telemetria Velivo do pliku CSV (1 s), HWiNFO (wentylator, porównanie), Cinebench R23 jeden i wiele rdzeni (10 min), test krótkich zadań (otwarcie 10 programów, eksport 30 s), LatencyMon przy DAW, miernik z gniazdka (jeśli jest) dla poboru całego komputera.
Scenariusze dla każdego poziomu i **dla Windows bez Velivo** (punkt odniesienia): spoczynek 10 min, przeglądanie 10 min, film 30 min, Cinebench 1T i nT, krótkie zadania, DAW 15 min.
Miary: wynik i czas zadania, średnia moc i Wh na zadanie, średnia i szczytowa temperatura, obroty wentylatora / głośność, liczba zmian ogranicznika, trzaski. Zmiana wchodzi tylko, gdy: wydajność ≥ odniesienia (± 2 %), energia na zadanie ≤, temperatura i wentylator ≤, zero trzasków.

## F. Rekomendacje dla Ryzen 7 5825U (NUCBOX)
1. **Najpierw kalibracja**: `ryzenadj --info` po świeżym starcie bez Velivo – bez tego żadnych liczb. Wg AMD 5825U: 15 W (konfigurowalne 10–25 W) [WIEDZA]; mini-PC często mają więcej – sprawdzić.
2. **Naprawić przywracanie** (fabryczne zamiast Turbo) i **ponowne ustawienie po wybudzeniu** – to błędy, nie optymalizacja.
3. **Fast = Slow w Oszczędnym** – koniec szpilek wentylatora; boost zostaje.
4. **Tctl jako główny regulator ciszy** zamiast obcinania zegaru 1,4 GHz.
5. **Bez stałego minimum zegaru** we wszystkich trybach.
6. **Wydajny: budżet długotrwały podnosić tylko, gdy telemetria pokazuje, że hamuje STAPM, a temperatura ma zapas** – i tylko do granicy z kalibracji cieplnej NUCBOX.
7. **TDC/EDC, OC, Curve Optimizer – nie automatycznie.** Curve Optimizer ewentualnie jako osobny, ręczny test właściciela z testem stabilności.
8. **Sprawdzić stan sterownika WinRing0** na NUCBOX (Defender) przed budową silnika na RyzenAdj.

---

## 6. Dwa wyspecjalizowane silniki + wspólna inteligencja (wizja właściciela)

```
            Wspólna inteligencja (co jest potrzebne: spoczynek / lekka praca / jeden rdzeń / wiele rdzeni / gra / Studio / temperatura)
                     │  wynik: POZIOM (Oszczędny / Zrównoważony / Wydajny) + powód
        ┌────────────┴────────────┐
   AMD Engine                Intel Engine          ← każdy sam tłumaczy poziom na swoje mechanizmy
        │                         │
 Wspólne bezpieczeństwo: przywracanie transakcyjne, ochrona Studio, brak walki z innymi programami, wycofanie po 3 błędach, ekran wyłączony
```

- Wspólna część nie zna watów, EPP ani rejestrów – mówi tylko „potrzebny poziom X, bo …”.
- Silnik ma własną kalibrację, telemetrię, diagnozę ogranicznika i przywracanie swoich parametrów.
- Mechanizmy bezpieczeństwa, przywracania i Studio **bez zmian** – przenoszone 1:1, nie przepisywane.
- Wybór silnika przy starcie z rozpoznania procesora (producent, rodzina, model z rejestru / CPUID, rodzaje rdzeni z `GetSystemCpuSetInformation`).

## 7. AMD – każdy rodzaj [KOD RyzenAdj `cpuid.c`]

RyzenAdj obsługuje **tylko procesory mobilne / APU** (i mobilne HX):

| Rodziny (RyzenAdj) | Przykłady | AMD Engine |
|---|---|---|
| Raven, Picasso, Dali (Zen / Zen+) | Ryzen 2000U / 3000U, Athlon 3000 | pełny (moc, okna, Tctl, telemetria – tabela starszego typu) |
| Renoir, Lucienne (Zen 2) | 4000U/H, 5300U/5500U/5700U | pełny |
| **Cezanne / Barcelo (Zen 3)** | **5600U/5800U/5825U**, 5600H | pełny – sprzęt referencyjny |
| Rembrandt (Zen 3+) | 6000U/H, 7035 | pełny |
| Van Gogh | Steam Deck | pełny (inne pola tabeli) |
| Mendocino (Zen 2) | 7020 | pełny |
| Phoenix, Hawk Point (Zen 4) | 7040, 8040 | pełny |
| Strix Point, Krackan Point, Strix Halo (Zen 5) | AI 300, AI Max | pełny (część pól „untested” w RyzenAdj) |
| Dragon Range, Fire Range | 7045HX, 9055HX | ograniczony (desktopowy krzem w laptopie – część poleceń brak) |
| **Desktop (Ryzen 1000–9000 bez „G”, Threadripper)** | 5800X, 7800X3D, 9950X | **RyzenAdj nie obsługuje** → tylko CPPC / plan Windows (EPP, boost, max %), telemetria z liczników Windows; PBO / Curve Optimizer – tylko BIOS (Ryzen Master – bez publicznego API) |
| Desktop APU („G”) | 5600G, 8700G | zależnie od rodziny (Cezanne / Phoenix) – [TEST] |
| Starsze (FX, Athlon przed Zen) | | tylko plan Windows |

Zasada: silnik czyta wersję tabeli i rodzinę; funkcja, której RyzenAdj nie obsługuje dla danej rodziny (zwraca `ADJ_ERR_FAM_UNSUPPORTED`), jest w oknie oznaczona „niedostępne na tym procesorze” – nigdy udawana.

## 8. Intel – każdy rodzaj [WIEDZA; rozpoznanie: rodzina / model + rodzaje rdzeni]

| Generacja | Mechanizm | Intel Engine |
|---|---|---|
| Do 5. gen. (przed Skylake) | brak Speed Shift (HWP) – zegar wybiera Windows | max %, boost, progi i czasy reakcji Windows (to, co Velivo ma dziś) |
| 6.–11. gen. (Skylake … Tiger / Rocket Lake) | Speed Shift (HWP) + EPP | EPP jako główne narzędzie, boost, max %; progi Windows mało ważne (sprzęt decyduje) |
| 12.–14. gen. (Alder / Raptor Lake, hybrydowe P + E) | HWP + Thread Director | osobne EPP / max dla P i E (klasa 1 – jest [KOD]), polityka rdzeni heterogenicznych Windows; Oszczędny: praca kierowana na E, Wydajny: P |
| Core Ultra 1–2 (Meteor / Lunar / Arrow Lake, rdzenie LP-E) | jak wyżej + rdzenie niskiej mocy | jak wyżej; Oszczędny: Windows sam trzyma lekką pracę na LP-E – nie przeszkadzać (bez stałego minimum) |
| Atom / Celeron / Pentium N (N100 itd.) | HWP, tylko E | EPP, boost; mało zapasu – Oszczędny prawie = Zrównoważony |

Telemetria Intel bez sterownika: `% Performance Limit` + `Performance Limit Flags` (powód hamowania), `Energy Meter` (pobór, gdzie dostępny) – [TEST na każdym komputerze]. PL1 / PL2 / napięcia – **nie** (sterownik jądra, blokady BIOS – od tego są XTU / ThrottleStop).

## 9. Co realnie osiągalne na 5825U (do potwierdzenia pomiarem)

- **Pewne (błędy do naprawy):** przywracanie fabrycznych limitów zamiast Turbo; ponowne ustawienie po wybudzeniu; koniec szpilek 28 W w Oszczędnym; brak stałego minimum zegaru.
- **Prawdopodobne:** mniejszy pobór w lekkiej pracy (bez minimum 1,4 GHz), cichszy wentylator przy podobnej wydajności (Fast = Slow w Oszczędnym, Tctl jako regulator).
- **Zależne od NUCBOX:** ile wydajności wielordzeniowej da wyższy budżet długotrwały – tylko jeśli dziś hamuje STAPM, a chłodzenie ma zapas. Jednordzeniowej wyższy limit mocy praktycznie nie zwiększy (jeden rdzeń mieści się w 15 W) [WIEDZA].

## 10. Pomiary NUCBOX (2026-11-25, BIOS „Performance”, plan Windows „Maksymalna wydajność”, bez Velivo)

Fabryczne (po restarcie): STAPM 15 W / okno 100 s, PPT Slow 20 W / okno 5 s, PPT Fast 22 W, TDC VDD 51 A, EDC VDD 105 A, Tctl 80 °C, STT brak czujnika (0). Defender – brak wykryć.

| Test (16 wątków) | Ogranicznik | Pobór | Temperatura | Zegar (Menedżer zadań) | Wentylator |
|---|---|---|---|---|---|
| fabrycznie, 30 s | PPT Slow 20 W | 20 W | 67 °C | – | – |
| fabrycznie, 60–120 s | **STAPM 15 W** | 15 W | 63 °C | ok. 2,68 GHz | – |
| STAPM 20 W, 60–180 s | PPT Slow 20 W | 20 W | 70,4 → 71,6 °C (wolno rośnie) | ok. 3,0 GHz | stabilny |

Wniosek: przy długiej pracy wielu rdzeni hamuje STAPM, temperatura ma zapas; STAPM 20 W (= fabryczny Slow) daje ok. +12 % zegaru wielu rdzeni przy ok. 8 °C zapasu do Tctl i stabilnym wentylatorze. Do potwierdzenia: test 10 min (czy temperatura się stabilizuje). Wyżej niż 20 W wymagałoby podniesienia PPT Slow i zostawia mniej zapasu temperatury – nieuzasadnione bez dalszego pomiaru.
