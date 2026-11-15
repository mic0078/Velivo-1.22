# Przekazanie pracy – Velivo (dla Claude Fable)

**Zadanie:** rozwiąż problem do końca – po podziale Velivo na osobne programy (Premium, Odtwarzacz, Przeglądarka) wszystko ma działać jak przed podziałem, także gdy kilka programów Velivo działa naraz i po restarcie Windows. Najpierw znajdź wszystkie przyczyny w kodzie (punkt 1 i 2 „Do zrobienia”), napraw, skompiluj trzy warianty, wyślij na `audio-balance` i podaj właścicielowi jedno polecenie budowy. Nie pytaj go o testy ani nie dawaj rad do wykonania ręcznie.

Czytaj najpierw `CLAUDE.md` na gałęzi `rozwoj` – to zasady właściciela (Michał) i wnioski z błędów. Odpowiadaj po polsku, krótko, bez pouczania. Gdy właściciel mówi „nie działa” – nie działa: nie dyskutuj, nie każ mu testować ponownie, szukaj błędu w ostatnich zmianach. Program ma sam naprawiać sytuacje (zamykać, przenosić, otwierać) – nie dawaj rad „zamknij X”.

## Gałąź i budowa

* Bieżąca praca: gałąź `audio-balance` (wnioski do CLAUDE.md idą na `rozwoj`).
* Kompilacja w kontenerze: `dotnet build src/Velivo.csproj -p:EnableWindowsTargeting=true` – trzy warianty: bez `Wariant`, `-p:Wariant=Odtwarzacz`, `-p:Wariant=Przegladarka`. Kompiluj wszystkie trzy przed wysłaniem.
* Nigdy kodu za `//` w tej samej linii.
* Polecenie dla właściciela (PowerShell, bez `Stop-Process`):
```
cd "C:\Users\andro\Desktop\Velivo 1.22 z github"
git fetch origin
git reset --hard origin/audio-balance
.\zbuduj-wszystko.ps1 -TylkoWindows
powershell -NoProfile -ExecutionPolicy Bypass -File .\zbuduj-audiobalance.ps1   # tylko gdy zmiana w audio-balance-windows
```

## Co zostało zrobione (podział na programy)

Z jednego kodu powstają osobne programy, każdy z instalatorem i własną ikoną w zasobniku:

* **Velivo Premium (pełne)** – wszystko, instalator pyta o składniki.
* **Velivo Odtwarzacz** (`WARIANT_ODTWARZACZ`, `VelivoOdtwarzacz.exe`) – Kanapa, filmy, Jellyfin, TV, lektor, Pilot; bez kart i komunikatora.
* **Velivo Przeglądarka** (`WARIANT_PRZEGLADARKA`) – tylko strony; filmy przekazuje do Odtwarzacza.
* Osobno już istniały: **Velivo Komunikator** i **Audio Balance for Windows**. Klawiatura zostaje w pełnym Velivo.

Klucz: `src/Wariant.cs` (Nazwa, Pid, MaKarty, MaFilmy, MaKomunikator, Pelne, ProgramExe). Wspólne dane: `%LOCALAPPDATA%\Velivo\Dane` (`VelivoData.Migrate`, junction w starym miejscu), spis programów `HKCU\Software\Velivo\Programy\<Pid>`. Zapis równoczesny: `VelivoData.MergeWrite` (ustawienia, okna), `MergeLibrary` (filmy.json). Sieć domowa – jeden prowadzi (mutex `Local\Velivo.SiecDomowa`).

## Stan teraz – właściciel: „wszystko się popieprzyło po podziale”

Ostatni błąd: po restarcie pełne Velivo „Nie udało się otworzyć karty… 0x800700AA” – Odtwarzacz z autostartu używał tego samego profilu WebView2. Poprawione:
1. `WebViewProfil` – każdy program własny profil (pełne dalej `Profil`).
2. `ZamknijInneVelivo()` w `InitView` – przy 0x800700AA program sam zamyka inne programy Velivo i ponawia.
Skompilowane, niesprawdzone na Windows.

## Do zrobienia (w tej kolejności)

1. **Audyt zasobów dzielonych między programami** (zasób, którego może używać tylko jeden proces): porty (41916–41930, zwłaszcza 41919 – `KomStartOwn`), pliki z blokadą, mutexy, autostart, rejestracje typów plików, VLC, Supertonic. Napraw bez pytania.
2. Znalezione w przeglądzie, niepoprawione: SaveLibrary bez wersji wyjściowej (base); kolejność zapisu `teraz-gra.txt`; okienko Odtwarzacza; opóźnienie `EnsureLink` (cmd); MergeWrite wskrzesza usunięte klucze; nieużywane `Programy()`.
3. **Audio Balance for Windows** (samodzielny – zmiany NIE dotykają Velivo): przywrócony do stanu z commita acdd6cb („brzmi czysto”). Właściciel to muzyk – ma być „jak w studio”, automatycznie, bez AI, nic nie omija AB. Znane: zapisane „Bardzo ciche nagrania” +24 dB (właściciel ma wyłączyć), automatyczny próg kompresora rośnie powoli (60 s) i ścisza – nie ruszać bez prośby. Niepotwierdzone: czy film z Odtwarzacza omija kabel CABLE. Zrywanie dźwięku – przyczyna nieznana (dzienniki czyste).

## Czego nie robić

* Nie zmieniać Velivo przy poprawkach samodzielnego AB (i odwrotnie).
* Nie robić „ulepszeń” dźwięku bez prośby i bez możliwości odsłuchu.
* Nie zmieniać UI bez makiety.
* Nie tworzyć PR bez polecenia; `main` tylko na „scal”.
