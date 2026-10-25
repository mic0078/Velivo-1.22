# Zasady pracy w repozytorium Velivo

Velivo ma być lekkie, szybkie i dyskretne. Odpowiadaj po polsku, krótko. Mów, co sprawdziłeś, a czego nie.

## Postawa

* Jesteś inżynierem informatykiem: skuteczność, działanie, a odpowiedzialność przede wszystkim. Kochasz i szanujesz użytkownika, myślisz jego tokiem rozumowania i proponujesz rozwiązania.
* Wszystko ma działać: kod czysty, solidny, bez błędów, także w kompilacjach. Nie psujesz kodu, nie robisz zbędnych symulacji.
* Podział pracy: Claude buduje kod, użytkownik testuje (ufa, więc testy robi sam). Jak coś jest źle, użytkownik mówi, a Claude poprawia i zapisuje błąd w „Wnioskach”.
* Sam szukasz przyczyny błędu i kompilujesz tylko poprawioną część. Pytasz (w pytaniach) tylko, gdy nie wiesz, czego chce użytkownik.
* Poprawiasz istniejący kod, nie dopisujesz nowego i nie zwiększasz plików bez potrzeby – aplikacje mają być lekkie i nie rosnąć w nieskończoność.
* Klawiatura bez zbędnego słownika: nie dodawaj dużych list słów – poprawiaj reguły i istniejący słownik.
* Nie powtarzasz błędów: wyciągasz wnioski, pamiętasz je i uczysz się na innych aplikacjach dostępnych w sieci.
* Użytkownik zawsze ma rację: gdy mówi, że coś nie działa, najpierw szukasz błędu w swoim kodzie i ostatnich zmianach – nie odsyłasz go do sprawdzania. Wyciągasz wnioski.
* Nie można ludzi oszukiwać: bez bzdur i zgadywanek, zawsze piszesz, co sprawdziłeś, a czego nie. CLAUDE.md ma być wiarygodny i trzymasz się go.

## Wnioski (pamięć 3–5 dni)

Claude zapamiętuje do 3 dni wstecz, a nawet 5 dni, jeśli użytkownik nie był aktywny: po każdym błędzie dopisz tu jedną linijkę z datą, by je naprawiać, jeśli wystąpią ponownie; starsze wpisy usuń.

* 2026-10-20: przed wysłaniem kompiluj poprawioną część i sprawdź tłumaczenia.
* 2026-10-20: nie zapisuj pliku przez `open(f,'w').write(open(f).read())` – czyści plik.
* 2026-10-20: pośrednik słyszy własne przekazane pakiety – urządzenie z `_komRemote` nie jest w domu.
* 2026-10-20: polecenia dla PowerShell, nie cmd – także gdy właściciel pisze „cmd” (pracuje w PowerShell; `cd /d` tam nie działa).
* 2026-10-21: przycisk ➤ omijał klawiaturę – ostatnie słowo bez poprawki; teraz ➤ prosi klawiaturę o poprawkę (`pl.velivo.slowo`), Enter = Wyślij.
* 2026-10-21: pakiety przez pośrednika mają adres 127.0.0.1 – nie zapisuj go jako adresu kontaktu (zrzut trafiał do samego telefonu).
* 2026-10-21: pośrednik wysyłał pliki do urządzenia poza domem przez 127.0.0.1 – po poprawce adresu przestało działać; teraz KomOpenAsync: odbiorca w `_komRemote` → tunel.
* 2026-10-21: zanim zmienisz zachowanie (adres, ścieżkę, warunek), znajdź wszystkie miejsca, które z niego korzystają – poprawka jednego nie może psuć drugiego.
* 2026-10-21: gdy użytkownik zgłasza „nie działa”, najpierw przejrzyj własne ostatnie zmiany, zamiast prosić go o sprawdzanie.
* 2026-10-21: zrzuty ekranu mają działać w obie strony: Windows → telefon działa, telefon → Windows (Udostępnij → Velivo) jeszcze nie – do naprawy.
* 2026-10-21: dzwonek rozmowy nie odblokowuje telefonu (bez prośby o PIN) – tylko okno nad ekranem blokady.
* 2026-10-21: „BUILD FAILED” Androida po ~25 s = złe hasło klucza, nie kod; skrypt sprawdza hasło od razu (keytool).
* 2026-10-21: sieć domowa zawsze działa: tryb „poza domem” wysyła przez pośrednika I w sieci domowej.
* 2026-10-21: próba połączenia z pośrednikiem po adresie domowym dodawała 1,5 s do każdego połączenia poza domem (zacinanie) – cofnięta; przed zmianą połączeń policz, ile razy się wykonuje.
* 2026-10-22: pośrednik (NUCBOX) przechodził w „poza domem”, gdy nikogo nie słyszał w Wi-Fi (po naprawie echa) – przestawał przekazywać; działający pośrednik jest zawsze „w domu”.
* 2026-10-22: na danych komórkowych połączenie z pośrednikiem „umiera po cichu” – telefon bez odpowiedzi (relay-pong) przez 50 s łączy się od nowa.
* 2026-10-22: ręczne przekierowanie 41920 w routerze → UPnP 718 → pośrednik sam przenosił się na 41921 (telefon tracił połączenie); teraz 718 na ten komputer = działa, powrót na 41920.
* 2026-10-22: dom dostał nowy adres IP – telefon poza domem znał tylko stary i nie mógł wrócić; teraz próbuje nazwy domu (No-IP z adresu Jellyfin poza domem).
* 2026-10-22: usługa komunikatora zatrzymana (powrót do aplikacji) zanim pokazała powiadomienie → awaria Androida 14; teraz zatrzymuje się sama po starcie.
* 2026-10-22: po onPause (stopLan) sieć domowa nie wracała w onResume – szybki dostęp i komunikator w domu się gubiły; onResume woła startLan(). Przed zmianą cyklu życia sprawdź, co włącza i wyłącza sieć.
* 2026-10-22: Windows się zawieszał – możliwe przeskakiwanie pośrednika 41920 ↔ 41921 w kółko (restart + ogłoszenia); zmiana portu najwyżej raz na godzinę, bez nakładania się sprawdzeń.
* 2026-10-22: komunikator Windows zamulał – każdy pakiet (obecność, potwierdzenie) przebudowywał całą rozmowę (300 dymków); odświeżanie najwyżej co 0,4 s. Zawieszony pośrednik = telefon traci internet.
* 2026-10-22: „urządzenia poza domem: 0” mimo przekierowania w routerze = zapora Windows (np. po przywracaniu systemu); reguła wejściowa TCP 41920 (PowerShell jako administrator) – zadziałało.
* 2026-10-22: zrywanie połączenia przy każdej „zmianie sieci” (activeNetwork bywa null w tle) rozłączało co kilka sekund – usunięte; nowe połączenie tylko po 50 s ciszy albo po powrocie do Velivo (25 s ciszy).
* 2026-10-22: przez internet po ~95 s „poza siecią”, choć połączenie żyło – sygnał relay-ping/pong nie odświeżał obecności; teraz odświeża po obu stronach.
* 2026-10-22: dziennik pośrednika: telefon sam zamykał połączenie (EndOfStream po 6 s i 248 s) – wyjątek przy jednym pakiecie (wiadomość, obecność) kończył czytanie i zrywał połączenie; teraz pakiet z błędem jest pomijany. Najpierw dziennik, potem poprawka.
* 2026-10-22: hotspot z drugim telefonem (kontakt) = „w domu” → telefon zamykał połączenie z pośrednikiem (to samo EndOfStream po 6 s); teraz „w domu” tylko, gdy widać pośrednika.
* 2026-10-22: każdy komputer z kontaktami sam włącza pośrednika, a „pośrednik zawsze w domu” → GPD na hotspocie nigdy nie łączył się z NUCBOX; teraz zawsze w domu tylko pośrednik z wpisanym adresem albo z podłączonymi urządzeniami.
* 2026-10-22: zamknięcie z powodem (zapis do martwego połączenia) mogło zawisnąć i zatrzymać ponowne łączenie; zmiana jednego internetu na inny nie zrywała starego połączenia – teraz martwe tylko zamykane, a połączenie z adresu, którego już nie ma, od razu od nowa.
* 2026-10-22: synchronizacja z komputerem pokazywała komunikat przy każdej wymianie (nad klawiaturą), a „📶 Velivo w tej sieci” budziło ekran – teraz po cichu.
* 2026-10-22: Notification.Builder nie ma setSilent (jest tylko w NotificationCompat) – moja kontrolna kompilacja tego nie wyłapała, budowa padła; nowe metody Androida sprawdzaj w dokumentacji SDK.
* 2026-10-23: Velivo zamykało się po zamknięciu filmu – zegar synchronizacji pytał zwolniony odtwarzacz VLC (PlayerBusy → IsPlaying na zwolnionym, 0xc0000005); po zamknięciu odtwarzacza _playerMp = null. Przyczynę dał dziennik Windows (Get-WinEvent Id 1000,1026).
* 2026-10-23: telefon w domu co ~3 min przeskakiwał dom ↔ internet (pośrednik milczał w Wi-Fi ponad 100 s); teraz po 40 s ciszy telefon pyta pośrednika (p2p-who) w Wi-Fi.
* 2026-10-23: plik od kontaktu przyjęty „z pytaniem” szedł do Pobranych bez ścieżki w rozmowie – stuknięcie nic nie robiło; teraz zawsze w rozmowie. Linki: najpierw aplikacja przypisana (FLAG_ACTIVITY_REQUIRE_NON_BROWSER), potem przeglądarka.
* 2026-10-23: mama nie widziała dzwonka ani wiadomości – Android 14 blokuje rozmowy na cały ekran bez zgody (canUseFullScreenIntent), a powiadomienia mogą być wyłączone; Velivo sprawdza i prosi o włączenie, wiadomości widoczne na zablokowanym ekranie.
* 2026-10-23: wiadomość doręczona ponad 10 min po napisaniu była po cichu odrzucana (sprawdzanie świeżości ts dla wszystkich pakietów) – teraz stare wiadomości przyjmowane (powtórki po mid w rozmowie); pośrednik przechowuje wiadomości do niedostępnych i doręcza, gdy się pojawią.
* 2026-10-23: wiadomości nie wyskakiwały na górze ekranu (trzeba było przeciągać w dół) – kanału powiadomień nie da się podnieść po utworzeniu; nowy kanał „wiadomosci2” (HIGH, wibracja, ekran blokady), stary usuwany z zachowaniem dźwięku.
* 2026-10-24: telefon w domu co ~5 min łączył się przez internet i wracał („zamykam: w domu” setki razy) – „w domu” trzymane 10 min (wyjście wykrywa zmiana adresu sieci); plik do nieobecnego pokazywał „błąd”, choć szedł do skrzynki; link z komunikatora otwierał się w Velivo pod oknem komunikatora; fałszywe „oszustwo” dla googleadservices.com z karty w tle.
* 2026-10-24: Windows: po dodaniu grup pole wiadomości było wyłączone także w zwykłych rozmowach? – nie, ale w grupach tak (IsEnabled pola zależało od !IsGroup) – poprawione: tekst w grupie działa.
* 2026-10-25: poprawka komunikatora telefonu nie trafiła do kopii w klawiaturze – teraz jeden plik `android/komunikator/.../Komunikator.kt` (pakiet `pl.velivo.komunikator`) dla przeglądarki i klawiatury; po zmianie kompiluj obie.
* 2026-10-25: NUCBOX ma pośrednika ręcznie na 41921, a Velivo co godzinę mogło samo wrócić na 41920 (UPnP) – teraz port zatwierdzony w oknie 🌍 (inny niż 41920) zostaje.
* 2026-10-25: zdjęcie profilowe szło do kontaktu dopiero, gdy ten napisał – nowy kontakt (telefon po reinstalacji) go nie dostawał; teraz przy pojawieniu się kontaktu; w nagłówku rozmowy go nie było.
* 2026-10-21: autokorekta zmieniała dobre słowa spoza słownika (naprawiaj → naprawiać): formy rozkazujące uznane za poprawne, dwie pomyłki tylko przy zgodnym początku, cofnięta poprawka (⌫) zapamiętana na stałe.

## Zrobione (pamięć)

* **Cel komunikatora (właściciel):** działa jak WhatsApp (wiadomości, pliki, głosówki, wideo, rozmowy, grupy, powiadomienia, otwieranie stuknięciem), ale prywatnie – bez cudzych serwerów, tylko przez domowy pośrednik (NUCBOX) i szyfrowanie od nadawcy do odbiorcy. Każda zmiana komunikatora ma to porównanie z WhatsApp.

* Komunikator: w domu i poza domem przez domowy komputer (pośrednik, UPnP, bez ustawiania); 🏠/🌍; pliki i zrzuty w obie strony; zaproszenia (WhatsApp, SMS); rozmowy wideo (WebRTC, poza domem prywatny TURN na porcie pośrednika).
* 📬 Skrzynka pośrednika: wiadomości (`komunikator-skrzynka.json`) i pliki (`skrzynka\<odbiorca>`, typ `p2p-box`, klucz z klucza kontaktu) dla nieobecnych – oddawane, gdy odbiorca się pojawi.
* 🔗 „Mój adres Velivo”: link dla każdego (sekret u pośrednika), prośba → Dodaj/Odrzuć u właściciela; obcy dostaje tylko komunikator. Dom (sieć domowa, filmy, synchronizacja) ≠ komunikator przez internet.
* Klawiatura (Windows i telefon, te same reguły): do 2 pomyłek sama, większe – propozycje; nie zmienia dobrych słów (formy -aj, -uje, -ąc); sklejone słowa (mi chodziło, o tym); na prawdę → naprawdę; przecinek przed „że”.
* Klawiatura zapamiętuje na stałe: Mój słownik, cofnięte poprawki (⌫), częste słowa i pary – zaszyfrowane na urządzeniu.
* Budowa: hasło klucza sprawdzane od razu; instalator ponawia przy antywirusie.
* 2026-10-25: Komunikator Velivo – osobna aplikacja (`android/komunikator`, `pl.velivo.komunikator`, `Velivo-komunikator.apk`), działa sama. W telefonie jeden komunikator: Komunikator → przeglądarka → klawiatura (`Komunikator.owner`); przejmuje kontakty i rozmowy (dostawcy `…komunikator` „oddaj”). Porty: 41918 komunikator w domu (wysyłka też na 41919 dla starszego Velivo), 41919 dom/synchronizacja, 41917 filmy w sieci (było 41921 – kolizja z pośrednikiem), 41920–41929 pośrednik.

## Na potem (pomysły właściciela)

* Do zrobienia (2026-10-24, właściciel): Filmy z Jellyfin – gdy serwer niedostępny, pokazywać zapamiętaną bibliotekę i odświeżać w tle; ładniejszy wygląd komunikatora na telefonie; wersja Windows: „Mój adres”.

* Sieć pośredników znajomych: telefon poza domem łączy się już ze wszystkimi pośrednikami z kontaktów naraz (2026-10-22); dalej na potem: pośrednicy przekazujący między sobą (gdy rozmówcy mają różnych pośredników) i pliki/wideo przez dodatkowych pośredników.
* Prywatny serwer właściciela zamiast domowego komputera (właściciel zakłada go sam; Claude daje wymagania i pośrednika dla serwera).

## Gałęzie

* Pracuj tylko na `rozwoj`. Nie twórz innych gałęzi.
* `main` to wersja publiczna (push publikuje instalator). Scalaj tylko na polecenie „scal” i po zielonych testach.
* Na `main` bez force-push, rebase i amend.

## Przed każdym wysłaniem

1. Najmniejsza poprawka tylko zgłoszonej sprawy, bez martwego kodu.
2. Kompiluj tę część (Windows, przeglądarka, klawiatura), w której poprawiłeś błąd – bez błędów.
3. Każdy napis `L.T("…")` ma tłumaczenie w `Lang.cs` (test `SlownikTests`); napisy Androida w PL i EN.
4. Sprawdź `git diff`. Wysyłaj dopiero po pomyślnej kompilacji.

## Interfejs

* Przyciski, ikony i menu mają podpowiedzi PL i EN.
* Wygląd Velivo (bez szarych okien Androida), czytelny w jasnym i ciemnym motywie.
* Zmiany Filmów i TV opisz w `dokumentacja/instrukcja-filmy-tv.md` i `instrukcja-filmy-tv-notatki.md`; telefonu – w `instrukcja-telefon.md` (PL i EN).

## Budowa u właściciela (Windows, PowerShell, nie jako administrator)

Claude zawsze podaje kod kompilacji do PowerShell (cały blok poniżej). Na końcu odpowiedzi napisz, co zbudować (Windows, Android, oba):

```
cd "C:\Users\andro\Desktop\Velivo 1.22 z github"
git checkout rozwoj
git pull origin rozwoj
.\zbuduj-wszystko.ps1 -Wszystko
```

* Tylko Windows: `.\zbuduj-wszystko.ps1`. Tylko Android: `-TylkoAndroid`. Do Google Play: `-TylkoAndroid -Sklep`.
* Gotowe pliki: `build\` (instalator), `build\android\` (`Velivo-pelna.apk`, `Velivo-klawiatura.apk`, `Velivo-komunikator.apk`).
