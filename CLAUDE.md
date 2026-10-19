# Zasady pracy w repozytorium Velivo

Velivo ma być lekkie, szybkie i dyskretne — każdą zmianę oceniaj pod kątem płynności, rozmiaru i prywatności.

## Gałęzie i scalanie

* `rozwoj` to jedyna gałąź robocza. Nie twórz innych gałęzi.
* `main` jest wersją publiczną; push uruchamia publikację instalatora.
* Do `main` scalaj tylko po zielonych testach na ostatniej zmianie i wyraźnym poleceniu właściciela: „scal”.
* Nie zmieniaj opublikowanej historii `main` przez force-push, rebase ani amend.

## Zmiany i testy

1. Sprawdź stan repozytorium i dostępne testy.
2. Odtwórz problem przed poprawką. Jeśli nie da się tego zautomatyzować, opisz sposób reprodukcji i ograniczenie.
3. Wprowadź najmniejszą czytelną poprawkę. Nie dodawaj martwego kodu ani nie zmieniaj niezwiązanych zachowań.
4. Uruchom właściwe testy, w tym testy bezpieczeństwa, jeśli są dostępne.
5. Przed pushem sprawdź `git diff` i potwierdź, że zmiany dotyczą tylko zgłoszonej sprawy.

Nowe funkcje dodawaj tak, by nie zmieniały istniejącego działania bez potrzeby. Gdy coś się zepsuje, najpierw przywróć działanie, potem szukaj przyczyny.

## Instalator testowy

* Zmiany w `src/` na `rozwoj` uruchamiają workflow `instalator-rozwoj.yml`.
* Na `rozwoj` buduj tylko instalator testowy.
* Gdy artefakt będzie dostępny, podaj jego rzeczywisty link z GitHub Actions. Nie zgaduj adresu.
* Sprawdź wynik testów i zgłoś go. Napraw czerwone testy związane ze zmianą.
* Nie czekaj bez potrzeby na długie zadania; podaj dostępny wynik i później uzupełnij raport.
* Nie scalaj do `main`, jeśli testy ostatniej zmiany nie są zielone.

## Budowa u właściciela (Windows, PowerShell)

Na końcu odpowiedzi podawaj polecenia i napisz, co trzeba zbudować (Windows, Android, oba, nic):

```
cd "C:\Users\andro\Desktop\Velivo 1.22 z github"
git checkout rozwoj
git pull origin rozwoj
.\zbuduj-wszystko.ps1 -TylkoAndroid -Sklep
```

* Android, 3 wersje (APK dla znajomych + 2× AAB do Google Play): `.\zbuduj-wszystko.ps1 -TylkoAndroid -Sklep`.
* Tylko Windows: `.\zbuduj-wszystko.ps1`. Szybkie próby Androida: ▶ w Android Studio (wariant pelnaDebug).

## Interfejs i dokumentacja

* Przyciski, ikony i pozycje menu mają podpowiedzi PL i EN, także w klawiaturze ekranowej i odtwarzaczu.
* Listy mają sortowanie i zapamiętują ustawienia okna oraz szerokości kolumn.
* Zmiany funkcji filmów lub TV opisz w `dokumentacja/instrukcja-filmy-tv-notatki.md` i `dokumentacja/instrukcja-filmy-tv.md`.

Odpowiadaj po polsku. Raportuj, co sprawdziłeś i czego nie udało się zweryfikować.
