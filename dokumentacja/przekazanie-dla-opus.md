# Przekazanie dla Claude Opus – co naprawił Fable (2026-11-15)

Gałąź: `audio-balance` (wnioski w `CLAUDE.md` na `rozwoj`). Wszystko skompilowane (Velivo ×3 warianty, Audio Balance for Windows), **nic nie było uruchamiane na Windows ani odsłuchiwane** – potwierdza właściciel.

## Zasady, które właściciel powtórzył (trzymaj się ich)

* Programy Velivo są **równe** – „Velivo główne to nie bóg”. Żaden nie zamyka drugiego i nie ma pierwszeństwa. Działają niezależnie, synchronizują się przez wspólny folder `%LOCALAPPDATA%\Velivo`.
* Audio Balance for Windows: LUFS i kompresor mają **trzymać głośność na podobnym poziomie** – ciche w górę, głośne w dół, oba stopnie współpracują. „Jak w studiu”, bez AI.
* Wyłączone przetwarzanie ≠ wyłączony pomiar: wskaźniki zawsze pokazują sygnał.
* Zmiany samodzielnego Audio Balance nie dotykają Velivo (`audio-balance-windows/` ma własną kopię silnika w `Silnik/`).
* Nie dawaj rad „zamknij / sprawdź” – program ma to zrobić sam. Nie dyskutuj, gdy mówi „nie działa”.

## Velivo – kilka programów naraz (commity 01eef25, 63249a2)

| Problem | Poprawka | Gdzie |
|---|---|---|
| Po starcie Windows Velivo i Odtwarzacz robiły to samo: wznawiały ten sam kanał TV (dwa strumienie), zaplanowane nagrania, Jellyfin, kopię, karteczki, widżet AB | `VelivoData.Jeden(nazwa)` – nazwany muteks; czynność wykonuje jeden program, drugi przejmuje po jego zamknięciu. Wołać z wątku okna | `src/VelivoData.cs`, `IptvRecord.cs` (`"tv"`), `JellyfinMeta.cs`, `Backup.cs`, `StickyNotes.cs` (`_notesObce`), `MainWindow.xaml.cs` (`"ab-pulpit"`) |
| Klawiatura (hak) mogła działać w dwóch programach | `StartGlobalKeyboardHook` tylko `Wariant.Pelne` | `src/ScreenKeyboard.cs` |
| Sieć domowa | Prowadzi program uruchomiony pierwszy (muteks `Local\Velivo.SiecDomowa`), drugi przejmuje do 15 s po zamknięciu. Wyjątek z możliwości: Przeglądarka (bez filmów i Pilota) oddaje sieć programowi z filmami i zwalnia porty 41916/41917 (`LanOddajPorty`) | `src/LanSync.cs` |
| Błąd kart 0x800700AA | Każdy program własny profil WebView2 (`WebViewProfil`). Zamykanie innych programów **usunięte** (wbrew zasadzie równości) | `src/MainWindow.xaml.cs` |

## Audio Balance for Windows (commity 722565e, 0c74624 i następny)

| Problem | Poprawka | Gdzie |
|---|---|---|
| „Cicho, kompresor nie działa” – suwak głośności Windows na „CABLE Input” ściszał dźwięk PRZED programem (wejście np. −56 dB) | Poziom wyrównany przed przetwarzaniem, głośność Windows stosowana po nim (`VolRead`, `preT`/`postT`, razem zawsze 1:1). Dziennik przy starcie: „Głośność Windows: …”. Przyczyna u właściciela **niepotwierdzona** | `AbEngine.cs` |
| Zmiana wyjścia w Windows omijała program | Strażnik `Pilnuj`: wybrane urządzenie staje się wyjściem programu, Windows wraca na kabel. `aktualizacja.flag` wstrzymuje strażnika na czas aktualizacji / odinstalowania | `AbMain.cs` |
| „Próg automatyczny zamiast wyciągać, ścisza” | Przy progu automatycznym kompresor doprowadza typową MOC dźwięku (`progP`) do celu LUFS (`AutoTargetDb` ustawia `AbProcessor`), zakres −20…+30 dB, w górę 6 dB/s, w dół 20 dB/s; suwak „Podbicie” wtedy nieużywany; w oknie „do celu: ±dB”. Symulacja: wejście −50 / −25 / −8 dB → wyjście −24,2 dB (cel −23 LUFS) | `Silnik/AudioBalanceDynamics.cs`, `Silnik/AudioBalanceDsp.cs`, `Silnik/AudioBalanceDynamicsUi.cs` |
| Podgląd pusty przy wyłączonym kierowaniu | Podsłuch wyjścia Windows (loopback) tylko do wskaźników: `AbEngine.Monitor(id)`, osobny `MonProc` (Bypass), `Shown` wybiera, co pokazać | `AbEngine.cs`, `AbLive.cs`, `AbWidget.cs`, `AbMain.cs` |

## Otwarte (niezrobione)

1. Velivo / Odtwarzacz: film włączony, zanim Audio Balance for Windows zaczął przetwarzać, gra dalej na urządzeniu z `ab-wyjscie` (może omijać kabel) – przy zmianie `_abWinOn` odłączyć własne wyjście (`AbDetach`) i wrócić do zwykłego wyjścia VLC.
2. Gdy sieć domową prowadzi Odtwarzacz, pakiety komunikatora na 41919 (`KomUdp` → `KomOddany`) przepadają – powinien je dostać program z komunikatorem.
3. Zmiany na żywo między programami (ulubione, ustawienia) – niesprawdzone, czy drugi program widzi je bez ponownego uruchomienia.
4. Zrywanie dźwięku ze źródła – przyczyna nieznana (dzienniki czyste). Możliwe, że to były dwa strumienie TV naraz (naprawione wyżej) – niepotwierdzone.
5. Z przeglądu kodu: `SaveLibrary` bez wersji wyjściowej, kolejność zapisu `teraz-gra.txt`, `MergeWrite` wskrzesza usunięte klucze, nieużywane `Programy()`.

## Budowa i powrót

```
cd "C:\Users\andro\Desktop\Velivo 1.22 z github"
git fetch origin
git reset --hard origin/audio-balance
.\zbuduj-wszystko.ps1 -TylkoWindows
powershell -NoProfile -ExecutionPolicy Bypass -File .\zbuduj-audiobalance.ps1
```

Stan sprzed tej sesji: `git reset --hard 2a87723`. Sam Audio Balance sprzed sesji: `git checkout 2a87723 -- audio-balance-windows`.
