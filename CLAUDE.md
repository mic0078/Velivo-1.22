# Zasady pracy w repozytorium Velivo

## Gałęzie
- `main` – stabilna, publiczna wersja. Push do `main` automatycznie buduje i podmienia publiczny instalator (`Instalator/`).
- `rozwoj` – JEDYNA gałąź robocza. Wszystkie zmiany (poprawki, testy, dokumentacja) idą tylko tutaj.
- Nie twórz nowych gałęzi roboczych (`fix/...`, `claude/...`, `test/...`). Pracuj zawsze na `rozwoj`.
- Do `main` scalaj dopiero po teście właściciela na instalatorze testowym i jego wyraźnym „scal”.

## Instalator testowy
- Każdy push na `rozwoj` ze zmianą w `src/` buduje instalator testowy (workflow `instalator-rozwoj.yml`) – tylko jako artefakt w GitHub Actions, bez zapisu do repozytorium.
- Link do artefaktu podawaj właścicielowi do testów.

## Procedura poprawki
1. Najpierw test odtwarzający problem (wynik „przed poprawką”).
2. Minimalna poprawka – bez zmian „na wszelki wypadek”.
3. Test „po poprawce” + istniejące testy bezpieczeństwa.
4. Uczciwy raport: co sprawdzone, a czego nie.

## Inne
- Odpowiadaj po polsku.
- Nie przepisuj opublikowanej historii `main` (bez force-push / rebase / amend na `main`).
