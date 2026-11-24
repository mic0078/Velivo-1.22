# Velivo Audio Balance – opis działania do konsultacji (dla innych AI)

Proszę o ocenę i konkretne propozycje (liczby), jak ma działać profesjonalnie wyrównywanie głośności filmów i telewizji (LUFS) razem z kompresorem. Poniżej dokładny opis programu z kodu – wszystkie zmienne i wartości są prawdziwe (stan 2026-11-24).

## 1. Do czego służy

- Domowy odtwarzacz filmów i telewizji IPTV (VLC) oraz osobny program „Audio Balance for Windows”, który przepuszcza **cały dźwięk Windows**.
- Cel użytkownika: dialogi zawsze dobrze słyszalne i równe, wybuchy i reklamy nie wyskakują, cichy film nie jest za cichy. Bez „pompowania” (falowania głośności) między zdaniami i bez ściskania muzyki na płasko.
- Słuchanie: głośniki komputera albo telewizor, pokój dzienny, wieczorem.

## 2. Gdzie działa przetwarzanie

- **Odtwarzacz Velivo:** dźwięk z VLC przez wywołania zwrotne – 48 kHz, float, stereo, przetwarzany w miejscu (`AbProcessor.Process`). Opóźnienie przetwarzania ok. 5 ms (limiter z wyprzedzeniem), wyrównywane z obrazem.
- **Audio Balance for Windows:** przechwytuje dźwięk z wirtualnego kabla VB-CABLE (WASAPI, NAudio), ten sam `AbProcessor`, wynik na głośniki. Opóźnienie do wyboru: 0,1 / 0,2 / 0,35 s. Odtwarzacz Velivo przesuwa wtedy swój dźwięk o tyle wcześniej, żeby był zgrany z obrazem (obecnie 175 ms).

## 3. Kolejność przetwarzania (tor sygnału)

1. **Wtyczki VST3** użytkownika (opcjonalnie, np. iZotope Ozone 12 Spectral Shaper / Clarity) – przed wszystkim.
2. **Korektor 10-pasmowy** (31 Hz … 16 kHz, filtry Peak, Q 1,41) – opcjonalnie.
3. **Wyraźne dialogi** (opcjonalnie) – pasmo 250–4000 Hz ze środka stereo (L+R) podbijane równolegle o 0 / +4 / +8 dB, tylko gdy mowa jest przykryta: udział mowy < 0,8 (pełne podbicie przy 0,35), bez mowy (< 0,25) – nic. Atak 80 ms, powrót 400 ms.
4. **Regulator LUFS (wyrównywanie)** – trzy warstwy, opis w p. 4.
5. **Ściszanie filmu pod lektora** (gdy czyta syntezator mowy Velivo) – lektor 10 dB nad filmem w paśmie 1–4 kHz (12 dB przy dialogu w filmie), atak 0,15 s, powrót 0,6 s, przytrzymanie 0,8 s, najgłębiej −9 dB (suwak).
6. **Kompresor wstępny** (wzór: Ozone Dynamics) – mimo nazwy stoi **po** regulatorze LUFS. Opis w p. 5.
7. **Limiter True Peak** – sufit −1 dBTP, wyprzedzenie 5 ms (240 próbek), punkty między próbkami (oversampling), powrót 80 ms, twardy bezpiecznik na suficie.
8. Głośność odtwarzacza (wygładzana 80 ms).

## 4. Regulator LUFS

**Pomiar (ITU-R BS.1770, K-weighting dla 48 kHz):**
- Fast – blok 100 ms;
- Momentary – 400 ms;
- ShortTerm – 3 s;
- Gated10 – jak zintegrowana (bramki −70 LUFS i −10 LU), ale z ostatnich 10 s;
- Median – mediana Momentary z 10 s (bez ciszy poniżej bramki);
- Median15 – mediana z 1,5 s, tylko do wykrywania zmiany sceny;
- Integrated – z całego filmu (histogram bloków 400 ms).

**Cel:** wybór użytkownika, domyślnie **−18 LUFS**. Tryb „Auto” = −23 LUFS (EBU R128). Nauka może przesunąć cel dla danego źródła (±6 dB).

**Tryby i intensywność** (`AbProfile`; Auto = podgłaśnia i ścisza, Tylko szczyty = tylko ścisza):

| Parametr | Lekko | Normalnie | Mocno | Znaczenie |
|---|---|---|---|---|
| Amount | 0,6 | 0,85 | 1,0 | jaka część różnicy do celu jest korygowana |
| Dead (strefa komfortu) | ±4 dB | ±3 dB | ±2 dB | odchylenie od celu bez korekty |
| MaxBoost | 10 dB | 15 dB | 20 dB | najwyższe podgłośnienie (Tylko szczyty: 0) |
| MaxCut | 10 dB | 15 dB | 20 dB | najgłębsze ściszenie |
| UpDbS | 0,7 dB/s | 1,0 dB/s | 1,5 dB/s | szybkość podgłaśniania |
| DownDbS | 1,0 dB/s | 1,5 dB/s | 2,5 dB/s | szybkość ściszania |
| Gate | −25 dB | −30 dB | −35 dB | poniżej cel+Gate – regulator stoi (cisza) |
| FastHead | 12 dB | 10 dB | 8 dB | zapas nad celem dla szybkiego ściszania |
| FastDownDbS | 30 dB/s | 40 dB/s | 60 dB/s | szybkie ściszanie (huk, reklama) |
| ThrOff (wewn. kompresor) | +8 dB | +6 dB | +5 (Tylko szczyty: +4) dB | próg = cel + ThrOff |
| Ratio (wewn. kompresor) | 1,5:1 | 2:1 | 2,5:1 | |
| Attack / Release | 25 / 400 ms | 15 / 300 ms | 8 / 200 ms | |

**Warstwy regulatora:**
- **a) wolny poziom (levDb).**
  - Podstawa pomiaru: Gated10 (po 6 s; na starcie ShortTerm), ale nie wyżej niż Median + 3 dB, żeby krótki huk nie ściągał w dół.
  - Zmiana sceny: Median15 różni się od podstawy o ponad 5 dB dłużej niż 2 s – podstawą staje się Median15, a ruch jest 6× szybszy.
  - Film (FilmLong) po 30 s: podstawą jest Integrated z całego filmu, sceny są pomijane.
  - Telewizja po 10 s: poziom kanału z ok. minuty (bramka −10 LU). Cicha scena nie podgłaśnia, głośniejszy program (ponad 5 dB przez 5 s) – ścisza.
  - Korekta: want = clamp(Amount × (cel − podstawa poza strefą Dead), −MaxCut … MaxBoost), wygładzana 2 s (przy zmianie sceny 0,5 s), ograniczona szybkością Up/DownDbS (pierwsze 6 s i zmiana sceny 6× szybciej).
  - Film w trybie Normalnie: Amount = 1, strefa ±1 dB.
  - Tło: fragment ponad 22 dB cichszy niż średnia filmu (albo poniżej cel + Gate) nie jest podbijany – regulator stoi.
- **b) szybkie ściszanie (fastDb):** gdy max(Fast, Momentary) + levDb przekracza cel + FastHead, regulator ścisza z szybkością FastDownDbS (nauka może to przyspieszyć). Wraca 5× wolniej. Wolny poziom zostaje, więc po wybuchu dialog od razu jest znów normalny.
- **c) wewnętrzny kompresor regulatora:** detektor mocy K 10 ms po wzmocnieniu, próg cel + ThrOff, Ratio, miękkie kolano ±3 dB. Powyżej progu + 8 dB działa prawie jak twarda granica.
- **Wzmocnienie** = levDb + fastDb + grDb. W trybie Tylko szczyty nigdy powyżej 0 dB.

**„Bardzo ciche nagrania”** (opcja, u użytkownika włączona): gdy całe nagranie jest poniżej −25 LUFS, dodatkowe podbicie ponad MaxBoost, najwyżej +24 dB (tyle, ile brakuje do celu).

**Nauka (dla każdego kanału TV / filmu, do 400 źródeł):**
- zapamiętany poziom startowy (SeedLevDb);
- mnożnik szybkiego ściszania przy kanałach z reklamami (FastMul);
- przesunięcie celu z poprawek użytkownika (TargetAdj, połowa jego zmiany, najwyżej ±6 dB).

Usunięte wcześniej, bo samo powodowało falowanie (±3 dB między zdaniami): korekta wyniku w pętli zamkniętej na wyjściu.

## 5. Kompresor wstępny (DynParams, VelivoDynamicsProcessor)

**Parametry i zakresy:**
- Próg: −60 … 0 dB (domyślnie −32);
- Stopień: 1 … 20:1 (4);
- Atak: 0,5 … 200 ms (25);
- Powrót: 10 … 2000 ms (250);
- Miękkie kolano: 0 … 12 dB (10);
- Podbicie: −12 … +24 dB (6);
- Automatyczne podbicie: tak / nie – oddaje średnią redukcję z ok. 3 s;
- Mix (równoległa): 0 … 100 %;
- Wykrywanie: Peak albo RMS (RMS z ok. 20 ms, L+P razem – ta sama redukcja na obu kanałach);
- Ekspander (próg ciszy): −80 … −30 dB (−52), stopień 1 … 4:1 (1,5), najwyżej −12 dB;
- Próg automatyczny: tak / nie, „Nad średnią filmu” 0 … 20 dB (6).

**Charakterystyka:**
- miękkie kolano: g = (1/R − 1)·(d + w/2)²/(2w) dla |d| ≤ w/2, d = x − próg;
- powyżej kolana: g = (1/R − 1)·d;
- ekspander: g += max(−12, (x − prógCiszy)·(R_exp − 1)).

**Próg automatyczny:**
- **Typowy poziom filmu (progDb):** co 64 próbki; pierwsze 3 s szybko; potem średnia dB z ok. 10 s (do 30 s), następnie z ok. 60 s. Cisza (20 dB pod poziomem) jest pomijana, głośne fragmenty liczą się najwyżej +3 dB.
- **Próg** = progDb + „Nad średnią filmu”. Próg ciszy = min(ustawiony, progDb − 30).
- **Wykrywanie zmiany programu/kanału:**
  - 6 dB głośniej przez 1,5 s albo 12 dB przez 0,2 s – przejście na nowy poziom w ok. 0,05–0,7 s;
  - 8 dB ciszej przez 3 s – przejście w ok. 0,1 s.
  - (Zmiana z dziś: dawniej ciszej po 0,3 s przy mowie – dialogi pompowały).
- **Podbicie przy progu automatycznym:** równe średniej redukcji (−avgGr). Podbijanie „do celu LUFS” jest obecnie wyłączone – kompresor stoi za regulatorem LUFS i miałby dublować jego pracę.
- **Parametry, gdyby było włączone** (dziś zmienione): podgłaśnianie 6 dB/s (było 20), bezpiecznik „wynik z 0,1 s nie więcej niż 12 dB nad celem” (było 8).

**Gotowe ustawienia:**
| Preset | Próg | Stopień | Atak / Powrót | Kolano | Podbicie | Mix | Inne |
|---|---|---|---|---|---|---|---|
| Film / kino | auto (+6) | 3,5:1 | 30 / 300 ms | 10 | 4 | 100 % | RMS |
| Noc / ciche nagrania | −42 | 6:1 | 20 / 250 ms | 10 | 12 | 100 % | ekspander −50 |
| Bardzo ciche filmy | −52 | 4:1 | 15 / 300 ms | 12 | 22 | 100 % | ekspander −68 |
| Delikatnie (równoległa) | −20 | 4:1 | 10 / 150 ms | 6 | 2 | 60 % | Peak |

## 6. Obecne ustawienia użytkownika (z ekranu)

- Wyrównywanie: **Auto**, siła **Lekko** (zmienione dziś z Normalnie), wyraźne dialogi **wył.**, opóźnienie 0,2 s, cel **−18 LUFS**, korektor wł., bardzo ciche nagrania wł. (do +24 dB, poniżej −25 LUFS).
- Kompresor: wł., próg automatyczny wł., **„Nad średnią filmu” +3 dB**, stopień **2,5:1**, atak 30 ms, powrót **700 ms**, kolano 10 dB, podbicie +4 dB (przy progu automatycznym nieużywane), mix 100 %, RMS, ekspander −52 dB / 1,5:1.
- Wtyczki VST3: wyłączone.

## 7. Problemy zgłoszone przez użytkownika

1. Przy progu automatycznym z „+6 dB” dialogi raz ciche, raz głośne – „zgłaśniają się i ściszają”. Po dzisiejszych zmianach jest trochę lepiej.
2. W filmach z polskim lektorem (głos lektora nałożony na oryginalny dialog) lektor „faluje”.
3. Wcześniej, z innymi wersjami: dźwięk „jak robot” przy agresywnym ekspanderze; falowanie ±3 dB przy korekcie w pętli zamkniętej.

Użytkownik nie chce samoczynnych „naprawiaczy”, które ciągle przełączają ustawienia. Chce przewidywalnego, profesjonalnego działania jak w procesorach broadcastowych albo w nocnym trybie dobrych amplitunerów.

## 8. Pytania

1. Czy kolejność toru jest prawidłowa: VST → korektor → dialogi → regulator LUFS (wolny + szybki + wewnętrzny kompresor) → kompresor → limiter TP? Czy kompresor powinien stać przed regulatorem LUFS, czy po nim?
2. Czy dwa kompresory (wewnętrzny w regulatorze i osobny „wstępny”) to dobry pomysł, czy jeden powinien zniknąć? Jakie wartości dla jednego wspólnego?
3. Jakie wartości regulatora LUFS dla filmów i dla TV: okno pomiaru, strefa komfortu, szybkości dB/s w górę i w dół, bramka ciszy, wykrywanie zmiany sceny/programu?
4. Jaki cel LUFS dla domowego oglądania wieczorem: −18, −20, −23, −24?
5. Kompresor dla dialogów i filmów z lektorem: próg względem średniej filmu, stopień, atak, powrót, kolano, detektor RMS czy Peak, okno RMS? Jak uniknąć pompowania lektora, gdy pod nim gra oryginał?
6. Czy próg automatyczny (średnia filmu + X dB) to dobre podejście, czy lepiej stały próg po wyrównaniu LUFS (np. cel + 4 dB)?
7. Czy wykrywanie zmiany programu (głośniej / ciszej przez X s) w filmie ma sens, czy tylko w TV?
8. Czy ekspander w ogóle jest potrzebny w filmach (szum tła), a jeśli tak – jaki próg i stopień, żeby nie robił „robota” z cichego głosu?
9. Czy warto dodać kompresję wielopasmową (np. 3 pasma) albo osobne prowadzenie pasma mowy, zamiast podbicia dialogów?
10. Proszę o gotowy, spójny zestaw liczb dla trzech trybów: Lekko, Normalnie, Mocno (Noc).
