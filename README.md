# Velivo

Lekka, prywatna przeglądarka dla Windows, która trzyma Twoje dane u Ciebie: bez konta, bez chmury, bez wysyłania historii czy haseł na zewnętrzne serwery. Działa na silniku Microsoft Edge (WebView2), więc strony wyświetlają się tak samo jak w Edge i Chrome.

Dane między Twoimi komputerami przenosi synchronizacja w sieci lokalnej (LAN), szyfrowana po sparowaniu urządzeń. W zestawie jest dodatek **Szybki Dostęp** (strona nowej karty ze skrótami) oraz współpraca z programem **Sejf**, który przechowuje hasła zaszyfrowane offline.

**[⬇ Pobierz instalator Velivo-Setup-1.22.exe](Instalator/Velivo-Setup-1.22.exe)**

## Funkcje

| Obszar | Co oferuje |
| --- | --- |
| Prywatność | Blokowanie reklam i trackerów (EasyList, EasyPrivacy, polska lista – ok. 95 tys. reguł), sygnały „Nie śledź”, ścisła ochrona przed śledzeniem, SmartScreen, karty prywatne |
| Blokowanie elementów | Prawy przycisk → „Blokuj element (reklamę)”: wskazujesz baner albo pływającą reklamę, a Velivo ukrywa ją przy każdym wejściu |
| Reguły dla stron | Panel prywatności: blokada JavaScriptu, ciasteczek, czyszczenie danych dla wybranej domeny oraz zaufane domeny (bez blokowania) |
| Szybki Dostęp | Strona nowej karty: skróty w grupach, ikony, miniatury stron robione w tle, profile z PIN-em, motywy tła |
| Hasła | Logowanie z Sejfu (kluczyk przy polach logowania), własna zaszyfrowana baza Velivo, generator haseł, import i eksport CSV (np. z KeePassXC) |
| Synchronizacja LAN | Ustawienia, zakładki, hasła i Szybki Dostęp między komputerami w domowej sieci, szyfrowane po sparowaniu kodem |
| Czytanie na głos | Głosy polskie i angielskie Windows oraz naturalne głosy offline Piper (Gosia, Darkman, MC Speech, Lessac, Ryan, Alba) |
| Tryb czytania | Czysty tekst artykułu ze streszczeniem, suwak głośności, kliknięcie w tekst czyta od tego miejsca |
| Tłumaczenie | Prawy przycisk → „Przetłumacz stronę na polski/angielski” albo tłumaczenie zaznaczonego tekstu |
| Wygląd | 10 motywów przeglądarki, tryb ciemny i nocny stron (jasny → ciemny → nocny jednym przyciskiem), zapamiętywane powiększenie |
| Narzędzia | Menedżer pobrań z wieloma połączeniami, wykrywanie mediów do pobrania, zrzuty ekranu (cała strona), historia, zakładki, dodatki Chrome |
| Profile | Osobne profile (np. praca, prywatny) z własnymi ustawieniami, historią i zakładkami |

## Instalacja

Velivo instaluje się jednym plikiem `Velivo-Setup-1.22.exe`, bez uprawnień administratora, do folderu `%LOCALAPPDATA%\Programs\Velivo`.

**Wymagania:** Windows 10 lub 11 (64-bit), .NET 10 Desktop Runtime (x64) oraz Microsoft Edge WebView2 Runtime (zwykle jest już w Windows 11). Gdy czegoś brakuje, instalator sam to wykryje i otworzy stronę pobierania.

1. Pobierz `Velivo-Setup-1.22.exe` z folderu [`Instalator`](Instalator).
2. Uruchom instalator. Gdy Windows pokaże „Nieznany wydawca”, kliknij „Więcej informacji” → „Uruchom mimo to” (instalator nie ma podpisu cyfrowego).
3. Jeśli Velivo było już zainstalowane, wybierz:
    - **Zachowaj moje ustawienia i dane** – zwykła aktualizacja (zalecane),
    - **Czysta instalacja** – start od zera; stare dane trafiają do kopii zapasowej z dopiskiem `.kopia-<data>`.
4. Opcjonalnie zaznacz skrót na pulpicie i zakończ instalację.

Antywirus może jednorazowo zapytać o nowe pliki. Najwygodniej dodać do zaufanych folder programu oraz `%LOCALAPPDATA%\Przegladarka\Piper` (głosy offline).

## Jak używać

Najważniejsze rzeczy są pod trzema miejscami: paskiem narzędzi, prawym przyciskiem myszy na stronie i oknem Ustawień (ikona zębatki).

**Pasek narzędzi**

| Przycisk | Działanie |
| --- | --- |
| Tarcza z licznikiem | Włącza i wyłącza blokowanie reklam; pokazuje liczbę zablokowanych elementów |
| Prywatność | Panel reguł dla domen, zaufane domeny i lista tego, co zablokowano |
| Księżyc | Jasny → ciemny → nocny tryb stron |
| Czytnik | Tryb czytania ze streszczeniem i czytaniem na głos |
| Głośnik | Czyta stronę na głos (Ctrl+Shift+U); zaznacz tekst, by przeczytać tylko fragment |
| Aparat | Zrzut ekranu |
| Gwiazdka | Dodaje zakładkę (Ctrl+D) |
| Strzałka w dół | Menedżer pobrań |

**Prawy przycisk myszy na stronie**

- „Blokuj element (reklamę)” – kliknij element; kółko myszy powiększa zaznaczenie, Esc anuluje.
- „Przywróć zablokowane elementy na tej stronie” – cofa blokady.
- „Przetłumacz stronę na polski / angielski” oraz wyszukiwanie i tłumaczenie zaznaczonego tekstu.
- „Dodaj do Szybkiego Dostępu” – skrót do wybranej grupy.
- „Narzędzia Velivo” – prywatność, media do pobrania, historia, dodatki, diagnostyka LAN, profile.

**Ustawienia warte zajrzenia**

- Wygląd: motyw przeglądarki, tryb ciemny i nocny, głos czytania (★ = naturalne głosy offline – pobierają się raz, ok. 60 MB).
- Prywatność: proponowanie zapisu haseł, autouzupełnianie, import haseł CSV.
- Profile użytkownika: osobny profil na osobę lub cel, nazwy bez polskich liter (np. `michal-praca`).
- Synchronizacja: włączenie LAN i parowanie urządzeń.

## Synchronizacja dwóch komputerów

Komputery z tym samym profilem w tej samej sieci domowej łączą się raz kodem, a potem synchronizują się same, szyfrowane, bez internetu.

1. Zainstaluj tę samą wersję Velivo na obu komputerach.
2. Ustaw na obu ten sam profil (Ustawienia → Profile użytkownika).
3. Upewnij się, że w Ustawieniach zaznaczone jest „Włącz synchronizację między uruchomionymi Velivo”, a zapora Windows przepuszcza Velivo (port 41919).
4. Velivo zapyta: „Połączyć oba komputery i synchronizować wszystko?” – wybierz Tak. Możesz też kliknąć „Sparuj urządzenie w sieci…”.
5. Porównaj krótki kod na obu ekranach i potwierdź.
6. Wybierz, czyje ustawienia zachować. Zakładki, hasła i Szybki Dostęp i tak połączą się z obu komputerów.

| Dane | Jak się synchronizują |
| --- | --- |
| Zakładki | Suma z obu; usunięcie działa na obu |
| Hasła Velivo | Suma; przy tym samym koncie wygrywa nowsze |
| Szybki Dostęp | Skróty i grupy łączone; ikony i miniatury każdy komputer robi sam |
| Ustawienia, prywatność, profile, dodatki | Wygrywa ostatnia zmiana; świeża instalacja nie nadpisuje danych |
| Otwarte karty | Nie są synchronizowane |

Stan połączenia pokazuje Ustawienia → „Panel diagnostyczny LAN…”. Hasła z programu Sejf nie są przenoszone – Velivo tylko o nie pyta.

## Problemy i miejsce danych

| Problem | Co zrobić |
| --- | --- |
| Komputery się nie widzą | Ten sam profil na obu, zapora przepuszcza Velivo, automatyczny czas Windows na obu (różnica ponad 5 min blokuje wymianę) |
| Strona coś blokuje, a nie powinna | Panel prywatności → „Dodaj do zaufanych” albo prawy przycisk → „Przywróć zablokowane elementy” |
| Brak ikon lub miniatur w Szybkim Dostępie | Otwórz nową kartę i odczekaj kilka minut; menu „⋮” → „Uzupełnij brakujące ikony” |
| Głos Piper nie czyta | Usuń folder `Piper` (poniżej) – pobierze się ponownie przy następnym czytaniu |
| Antywirus blokuje instalator | Plik nie ma podpisu cyfrowego – dodaj wyjątek albo zgłoś fałszywy alarm producentowi |

**Gdzie leżą dane**

- Program: `%LOCALAPPDATA%\Programs\Velivo`
- Ustawienia, zakładki, hasła, historia: `%LOCALAPPDATA%\Przegladarka` (profil inny niż domyślny: podfolder `Profiles\<nazwa>`)
- Głosy offline: `%LOCALAPPDATA%\Przegladarka\Piper`
- Dziennik błędów: plik `bledy.log` w folderze danych profilu – przydaje się przy zgłaszaniu problemów.

## Dla programistów

| Folder | Zawartość |
| --- | --- |
| `src/` | Kod źródłowy programu (C#, WPF, WebView2) |
| `src/QuickAccessExtension/` | Dodatek Szybki Dostęp |
| `src/installer.iss` | Skrypt instalatora (Inno Setup) |
| `Instalator/` | Gotowy instalator |
| `.github/workflows/instalator.yml` | Automatyczna budowa instalatora |

Każda zmiana w `src/` wysłana na GitHub automatycznie buduje nowy instalator (GitHub Actions, Windows) i nadpisuje `Instalator/Velivo-Setup-1.22.exe`.

Ręcznie (Windows, .NET 10 SDK, Inno Setup 6):

```
dotnet publish src\Przegladarka.csproj -c Release -r win-x64 --self-contained false -o build\velivo
"%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" src\installer.iss
```
