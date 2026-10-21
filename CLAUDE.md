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
* 2026-10-21: autokorekta zmieniała dobre słowa spoza słownika (naprawiaj → naprawiać): formy rozkazujące uznane za poprawne, dwie pomyłki tylko przy zgodnym początku, cofnięta poprawka (⌫) zapamiętana na stałe.

## Zrobione (pamięć)

* Komunikator: w domu i poza domem przez domowy komputer (pośrednik, UPnP, bez ustawiania); 🏠/🌍; pliki i zrzuty w obie strony; zaproszenia (WhatsApp, SMS); rozmowy wideo (WebRTC, poza domem prywatny TURN na porcie pośrednika).
* Klawiatura (Windows i telefon, te same reguły): do 2 pomyłek sama, większe – propozycje; nie zmienia dobrych słów (formy -aj, -uje, -ąc); sklejone słowa (mi chodziło, o tym); na prawdę → naprawdę; przecinek przed „że”.
* Klawiatura zapamiętuje na stałe: Mój słownik, cofnięte poprawki (⌫), częste słowa i pary – zaszyfrowane na urządzeniu.
* Budowa: hasło klucza sprawdzane od razu; instalator ponawia przy antywirusie.

## Na potem (pomysły właściciela)

* Sieć pośredników znajomych: komputery z Velivo wymieniają listę pośredników i przekazują sobie wiadomości, gdy mój komputer padnie (treść dalej szyfrowana; telefony nie są pośrednikami).
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
* Gotowe pliki: `build\` (instalator), `build\android\` (`Velivo-pelna.apk`, `Velivo-klawiatura.apk`).
