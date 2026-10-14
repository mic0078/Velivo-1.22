# Zasady pracy w repozytorium Velivo

## Gałęzie
- `main` – stabilna, publiczna wersja. Push do `main` automatycznie buduje i podmienia publiczny instalator (`Instalator/`).
- `rozwoj` – JEDYNA gałąź robocza. Wszystkie zmiany (poprawki, testy, dokumentacja) idą tylko tutaj.
- Nie twórz nowych gałęzi roboczych (`fix/...`, `claude/...`, `test/...`). Pracuj zawsze na `rozwoj`.
- Do `main` scalaj dopiero po teście właściciela na instalatorze testowym i jego wyraźnym „scal”.

## Instalator testowy
- Każdy push na `rozwoj` ze zmianą w `src/` uruchamia workflow `instalator-rozwoj.yml` – dwa równoległe zadania: `instalator` (sam instalator testowy, ok. 3 min, tylko jako artefakt w GitHub Actions, bez zapisu do repozytorium) i `testy` (wszystkie testy, w tym bezpieczeństwa, ok. 7 min).
- Na gałęzi roboczej budujemy tylko to, co właściciel testuje (instalator) – bez wersji przenośnej i samodzielnego exe.
- Link do pobrania podawaj ZAWSZE i bez pytania, gdy tylko instalator jest gotowy (pełny adres `https://github.com/mic0078/Velivo-1.22/actions/runs/<run>/artifacts/<id>`).
- Po każdej zmianie SAM poczekaj na zadanie `instalator` i od razu podaj właścicielowi link do artefaktu (właściciel nie powinien sprawdzać, czy już gotowe); potem SAM sprawdź zadanie `testy` i zgłoś wynik – czerwone testy naprawiasz od razu.
- Przy niewielkich poprawkach: od razu link do instalatora, testy sprawdzaj w tle (nie blokuj rozmowy czekaniem) i potem podaj krótkie podsumowanie wyniku.
- Scalenie do `main` tylko przy zielonych testach na ostatniej zmianie.

## Procedura poprawki
1. Najpierw test odtwarzający problem (wynik „przed poprawką”).
2. Minimalna poprawka – bez zmian „na wszelki wypadek”. Poprawiaj istniejący kod w miejscu (nadpisuj), nie dopisuj nowej wersji obok starej – żadnego martwego kodu.
3. Test „po poprawce” + istniejące testy bezpieczeństwa.
4. Uczciwy raport: co sprawdzone, a czego nie.

## Rozmiar kodu
- Nie dodawaj – poprawiaj. Zanim dopiszesz nową funkcję lub plik, sprawdź, czy da się rozszerzyć albo poprawić istniejący kod; kod nie może rosnąć w nieskończoność.
- Przy każdej zmianie usuń to, co staje się zbędne (stare warianty, nieużywane metody, powtórzenia) – bilans linii ma być jak najmniejszy.

## Interfejs
- Każdy przycisk, ikona i pozycja menu ma podpowiedź po krótkim najechaniu myszą (PL i EN) – także w klawiaturze ekranowej i odtwarzaczu.
- Każda lista w Velivo ma sortowanie (klik w nagłówek albo wybór) i zapamiętuje je – razem z miejscem i rozmiarem okna oraz szerokością kolumn.

## Dokumentacja
- Każdą nową funkcję lub zmianę zachowania w filmach / TV dopisuj od razu do `dokumentacja/instrukcja-filmy-tv-notatki.md` i do wersji roboczej `dokumentacja/instrukcja-filmy-tv.md` (podstawa przyszłej instrukcji wbudowanej w Velivo).

## Inne
- Odpowiadaj po polsku.
- Nie przepisuj opublikowanej historii `main` (bez force-push / rebase / amend na `main`).
