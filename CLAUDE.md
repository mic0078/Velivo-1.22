# Zasady pracy w repozytorium Velivo

Velivo ma być lekkie, szybkie i dyskretne. Odpowiadaj po polsku, krótko. Mów, co sprawdziłeś, a czego nie.

## Gałęzie

* Pracuj tylko na `rozwoj`. Nie twórz innych gałęzi.
* `main` to wersja publiczna (push publikuje instalator). Scalaj tylko na polecenie „scal” i po zielonych testach.
* Na `main` bez force-push, rebase i amend.

## Przed każdym wysłaniem

1. Najmniejsza poprawka tylko zgłoszonej sprawy, bez martwego kodu.
2. Kompilacja Windows i Androida (przeglądarka i klawiatura) bez błędów.
3. Każdy napis `L.T("…")` ma tłumaczenie w `Lang.cs` (test `SlownikTests`); napisy Androida w PL i EN.
4. Sprawdź `git diff`. Wysyłaj dopiero po pomyślnej kompilacji.

## Interfejs

* Przyciski, ikony i menu mają podpowiedzi PL i EN.
* Wygląd Velivo (bez szarych okien Androida), czytelny w jasnym i ciemnym motywie.
* Zmiany Filmów i TV opisz w `dokumentacja/instrukcja-filmy-tv.md` i `instrukcja-filmy-tv-notatki.md`; telefonu – w `instrukcja-telefon.md` (PL i EN).

## Budowa u właściciela (Windows, PowerShell, nie jako administrator)

Na końcu odpowiedzi napisz, co zbudować (Windows, Android, oba). Polecenia zawsze dla **PowerShell**:

```
cd "C:\Users\andro\Desktop\Velivo 1.22 z github"
git checkout rozwoj
git pull origin rozwoj
.\zbuduj-wszystko.ps1 -Wszystko
```

* Tylko Windows: `.\zbuduj-wszystko.ps1`. Tylko Android: `-TylkoAndroid`. Do Google Play: `-TylkoAndroid -Sklep`.
* Gotowe pliki: `build\` (instalator), `build\android\` (`Velivo-pelna.apk`, `Velivo-klawiatura.apk`).
