# Velivo 1.22 – audyt funkcjonalny (na podstawie kodu)

Audyt na podstawie kodu `src/*.cs` (50 plików) i rozszerzenia `src/QuickAccessExtension` (Szybki Dostęp, JS),
stan `main` po `476bdbd`. Kod nie był zmieniany.
Kolumny: **Impl.** = zaimplementowane w kodzie · **UI** = dostępne z interfejsu · **Sys.** = działa samo w tle / wewnętrznie.
✔ tak · – nie · ⚠ częściowo.

## A. Tryb bankowy (`Bank.cs`)
| Funkcja | Opis | Kod | Impl. | UI | Sys. |
|---|---|---|:-:|:-:|:-:|
| Przycisk 🏦 na pasku kart | otwiera tryb, prawy klik = menu | Bank.cs ~212 | ✔ | ✔ | – |
| Odizolowany profil przeglądarki | osobny profil WebView2: cookies, logowania, bez dodatków i historii | Bank.cs 18 | ✔ | ✔ | ✔ |
| Zielone karty bankowe 🏦 | wyróżnienie kart trybu | Bank.cs | ✔ | ✔ | – |
| Czyszczenie po zamknięciu | cache i historia profilu bankowego kasowane, logowania zostają | Bank.cs 321 | ✔ | – | ✔ |
| Hasło trybu (min. 8 znaków) | PBKDF2-SHA256 600 000 iteracji | Bank.cs 209, 599 | ✔ | ✔ | – |
| Klucz sprzętowy FIDO2 | YubiKey, Titan, kilka kluczy; WebAuthn przez webauthn.dll | Bank.cs ~2490–2700 | ✔ | ✔ | – |
| Klucz sprzętowy szyfruje bazę (hmac-secret) | klucz z 🔐 sam otwiera i szyfruje bazę; HKDF | Bank.cs 120–125, 423 | ✔ | ✔ | ✔ |
| Otwieranie samym kluczem | klucz w porcie = dotknięcie zamiast hasła | Bank.cs 434 | ✔ | ✔ | – |
| Osobna zaszyfrowana baza | plik `bank*.json`, pola zaszyfrowane AES-GCM (`SealList`) | Bank.cs 127–140 | ✔ | – | ✔ |
| Automatyczna blokada po bezczynności (1–60 min) | zamyka okienka trybu | Bank.cs | ✔ | ✔ | ✔ |
| 🔒 Zablokuj teraz | natychmiastowe zamknięcie trybu | Bank.cs | ✔ | ✔ | – |
| Profile bankowe | osobny tryb dla innej osoby: własne hasło, klucz i dane | Bank.cs 27 | ✔ | ✔ | – |
| 📜 Dziennik otwarć | kiedy, na którym komputerze, czym otwarto (także złe hasła) | Bank.cs | ✔ | ✔ | ✔ |
| ⚙ Ustawienia trybu | hasło, klucze, czas blokady | Bank.cs | ✔ | ✔ | – |
| Zapomniałem hasła – wyczyść tryb | usuwa tryb i jego dane | Bank.cs | ✔ | ✔ | – |
| 💾 Kopia / 📂 Przywróć | zaszyfrowany plik `.vbank` ze wszystkimi profilami | Bank.cs | ✔ | ✔ | – |
| 🏦 Moje banki / 🛒 Moje sklepy | lista stron, otwieranie w trybie | Bank.cs | ✔ | ✔ | – |
| ➕ Dodaj tę stronę do banków / sklepów | z otwartej karty bankowej | Bank.cs | ✔ | ✔ | – |
| ✏ Dane logowania banku | login / nr klienta, passcode / PIN, hasło, memorable information | Bank.cs | ✔ | ✔ | – |
| 🔑 Wpisz login | wypełnia logowanie na stronie banku | Bank.cs | ✔ | ✔ | – |
| 🔢 Wpisz wybrane znaki | odczytuje z formularza numery znaków (2., 5., 9.) i wpisuje je, także w listach wyboru | Bank.cs | ✔ | ✔ | – |
| 🧪 Skopiuj opis formularza | opis pól bez danych, do zgłoszenia problemu | Bank.cs | ✔ | ✔ | – |
| 💳 Moje karty | numer, data, posiadacz, CVV opcjonalnie; 👁 pokaż; 📋 kopiuj | Bank.cs | ✔ | ✔ | – |
| 💳 Wypełnij kartę na tej stronie | formularz płatności | Bank.cs | ✔ | ✔ | – |
| Przypomnienie o wygasających kartach | przy otwarciu trybu | Bank.cs ~1799 | ✔ | ✔ | ✔ |
| 🔑 Moje loginy i hasła | dowolne usługi; wpis tylko na stronie z tą samą domeną; 🎲 generator | Bank.cs 1183, 1730 | ✔ | ✔ | – |
| 📄 Poufne dane | dowód, paszport, prawo jazdy, PESEL, NI number, ubezpieczenie; numer ukryty; ważność | Bank.cs 1186 | ✔ | ✔ | – |
| Przypomnienie o dokumentach (60 dni) | przy otwarciu trybu | Bank.cs ~1799 | ✔ | ✔ | ✔ |
| 🏦 Rachunki bankowe | właściciel, IBAN, sort code / BIC, bank, tytuł przelewu, 📋 | Bank.cs 1189 | ✔ | ✔ | – |
| 📝 Moje notatki | kategorie (Login, PIN, Przelewy, Kody odzyskiwania, Inne), 🎲, 🔢 znaki z numerami; okienko nie blokuje strony | Bank.cs 310 | ✔ | ✔ | – |
| 🔑 Wpisz login z notatki | linie `login:` i `hasło:` | Bank.cs | ✔ | ✔ | – |
| 🔍 Szukaj w mojej bazie | banki, sklepy, karty, notatki | Bank.cs ~2136 | ✔ | ✔ | – |
| 🧾 Rachunki do opłacenia | kwota, termin, powtarzanie, przypomnienie, numer klienta, strona płatności, notatka | Bank.cs 1192 | ✔ | ✔ | – |
| Powtarzanie rachunków | od tygodnia do roku, jednorazowo, własne („co 10 dni”) | Bank.cs NextDue | ✔ | ✔ | ✔ |
| Przypomnienia o terminach | codziennie aż do zapłaty, także po terminie | Bank.cs ~1799 | ✔ | ✔ | ✔ |
| ✔ Zapłacone (następny termin) | wpis do historii i przesunięcie terminu; jednorazowy znika | Bank.cs ~1756 | ✔ | ✔ | – |
| 🌐 Otwórz stronę płatności | w karcie bankowej | Bank.cs | ✔ | ✔ | – |
| Nazwy rachunków z arkusza | lista wyboru w polu „Za co” | Bank.cs ~1668 | ✔ | ✔ | – |
| ➕ Dodaj rachunki z arkusza | tworzy brakujące rachunki z kolumn | Bank.cs ~1785 | ✔ | ✔ | – |
| 📊 Arkusz rachunków (w karcie) | miesiące × rachunki, sumy wierszy, kolumn i całości | Bank.cs OpenBillSheet | ✔ | ✔ | – |
| Arkusz: kolejny miesiąc z kopią kwot | ➕ Wiersz | Bank.cs (JS) | ✔ | ✔ | – |
| Arkusz: kolumny i nazwy | dodawanie, zmiana nazwy, usuwanie wierszy i kolumn | Bank.cs (JS) | ✔ | ✔ | – |
| Arkusz: import z Excela (CSV) | `;` `,` TAB, pomija „Razem”, scala miesiące, sortuje | Bank.cs (JS) | ✔ | ✔ | – |
| Arkusz: waluta | £, zł, €, $, CHF, kr, Kč, Ft, lei, ₴, ¥ – tylko symbol | Bank.cs | ✔ | ✔ | – |
| Arkusz: zapis zaszyfrowany | jednorazowy znacznik strony, adres strony sprawdzany | Bank.cs | ✔ | ✔ | ✔ |
| 📊 Zestawienie płatności (okno) | tabela, ręczne dopisywanie i usuwanie, import i eksport CSV, waluta | Bank.cs ShowBillHistory | ✔ | ✔ | – |
| Ostrzeżenie o stronie podobnej do Twojego banku | przy otwartym trybie | Bank.cs / Ochrona.cs | ✔ | ✔ | ✔ |
| Propozycja trybu bankowego | gdy bank/sklep z listy otworzysz w zwykłej karcie (po domenie głównej) | Bank.cs | ✔ | ✔ | ✔ |
| ❓ Instrukcja trybu bankowego | okno pomocy PL/EN | Bank.cs BankHelp | ✔ | ✔ | – |
| Synchronizacja trybu bankowego | zaszyfrowany pakiet, tylko między sparowanymi | LanSync.cs 703 | ✔ | – | ✔ |

## B. Hasła, Sejf, autouzupełnianie (`PasswordVault.cs`, `SejfBridge.cs`, `Autofill.cs`, `BrowserImport.cs`, rozszerzenie)
| Funkcja | Opis | Kod | Impl. | UI | Sys. |
|---|---|---|:-:|:-:|:-:|
| Menedżer haseł Velivo | lokalna baza DPAPI; lista, szukanie (domena, login, nazwa), źródło | PasswordVault.cs 16 | ✔ | ✔ | – |
| Dodaj / edytuj / usuń wpis, usuń wszystkie | | PasswordVault.cs | ✔ | ✔ | – |
| Kopiuj login / hasło, pokaż/ukryj, wklej ze schowka | | PasswordVault.cs | ✔ | ✔ | – |
| Generator mocnych haseł | kryptograficzny | PasswordVault.cs 1025 | ✔ | ✔ | – |
| Propozycja zapisu i aktualizacji hasła | po wykryciu logowania | PasswordVault.cs | ✔ | ✔ | ✔ |
| Wpisz zapisany login (wybór konta) | tylko dla prawdziwej domeny, bez dopasowania po nazwie | PasswordVault.cs 839 | ✔ | ✔ | ✔ |
| Import CSV | Chrome, Edge, KeePass, KeePassXC (pomija kosz), wieloliniowe pola | PasswordVault.cs 716, 895 | ✔ | ✔ | – |
| Eksport CSV | | PasswordVault.cs | ✔ | ✔ | – |
| Import z Chrome / Edge / Brave / Opery / Vivaldi | zakładki z plików; hasła przez stronę haseł przeglądarki | BrowserImport.cs 13 | ✔ | ✔ | – |
| Sejf: kluczyk na stronach logowania | Velivo pyta zewnętrzny Sejf (SejfMost) o loginy dla domeny | SejfBridge.cs 18 | ✔ | ✔ | ✔ |
| Sejf: sprawdzenie, że karta nadal jest na tej stronie | przed wypełnieniem | SejfBridge.cs 21 | ✔ | – | ✔ |
| Sejf: zapis loginu w zaszyfrowanym sejfie | okienko rozszerzenia | QuickAccessExtension/kod/hasla.js | ✔ | ✔ | – |
| Wypełnianie formularza z Sejfu | | formularze.js | ✔ | ✔ | – |
| Autouzupełnianie adresów i kart | DPAPI; klik w puste pole wypełnia formularz; CVC nie jest zapisywany | Autofill.cs 15, 87 | ✔ | ✔ | ✔ |
| Autouzupełnianie konta bankowego | IBAN, numer konta, sort code | Autofill.cs | ✔ | ✔ | ✔ |
| Pytanie przed wpisaniem karty i konta | potwierdzenie adresu strony | Autofill.cs | ✔ | ✔ | ✔ |
| Tylko pola widoczne dla człowieka | ukryte pola nie są wypełniane | Autofill.cs 128 | ✔ | – | ✔ |
| Pokaż zapisane dane / usuń karty / usuń adresy | | Settings.cs | ✔ | ✔ | – |
| Synchronizacja haseł | tylko w zaszyfrowanym pakiecie po sparowaniu | LanSync.cs | ✔ | – | ✔ |

## C. Sieć domowa LAN (`LanSync.cs`, `LanPairing.cs`, `LanHistorySync.cs`, `QuickAccessLanSync.cs`, `Innovations.cs §3`, `Tray.cs`)
| Funkcja | Opis | Kod | Impl. | UI | Sys. |
|---|---|---|:-:|:-:|:-:|
| Synchronizacja bez chmury | UDP 41919 w sieci lokalnej | LanSync.cs 20, 82 | ✔ | ✔ | ✔ |
| Przed sparowaniem tylko `announce` | identyfikator, nazwa komputera, profil – bez danych | LanSync.cs 647 | ✔ | – | ✔ |
| Propozycja sparowania | gdy w sieci jest drugi Velivo (pyta jeden z dwóch) | LanSync.cs OfferLanPairing | ✔ | ✔ | ✔ |
| Parowanie z kodem do porównania | uzgodnienie klucza + HMAC, krótki kod na obu ekranach, potwierdzenie | LanPairing.cs 201–310 | ✔ | ✔ | – |
| Wygasanie prób parowania, odrzucanie obcych profili | | LanPairing.cs | ✔ | – | ✔ |
| Wybór, czyje ustawienia zostają przy 1. połączeniu | zakładki, hasła, SD łączone | LanPairing.cs 321 | ✔ | ✔ | – |
| Plik odzyskiwania parowania | hasło ≥12, PBKDF2 600 tys., AES-GCM, limit 1 MB | LanPairing.cs 447 | ✔ | ✔ | – |
| Odtwórz parowanie z pliku | | LanPairing.cs 503 | ✔ | ✔ | – |
| Szyfrowanie i uwierzytelnianie pakietów | AES-GCM + HMAC-SHA256; klucze z PBKDF2 250 tys. | LanSync.cs 230–310 | ✔ | – | ✔ |
| Odrzucanie starych i powtórzonych pakietów, kontrola zegara (±5 min) | | LanSync.cs 448 | ✔ | – | ✔ |
| Kompresja GZip i limit po rozpakowaniu (8 MB) | | LanSync.cs 279, 328 | ✔ | – | ✔ |
| Izolacja profili | tylko ten sam profil; inny profil = pytanie o przełączenie | LanSync.cs 540, 614 | ✔ | ✔ | ✔ |
| Synchronizowane: ustawienia (bez lokalnych: głośniki, foldery, język…) | | LanSync.cs 68 | ✔ | – | ✔ |
| Synchronizowane: zakładki (łączone, usunięcia przez listę 90 dni) | | LanSync.cs 854 | ✔ | – | ✔ |
| Synchronizowane: hasła (łączone) | | LanSync.cs | ✔ | – | ✔ |
| Synchronizowane: reguły prywatności, profile, lista dodatków (+ automatyczna instalacja) | | LanSync.cs, Extensions.cs | ✔ | – | ✔ |
| Synchronizowane: przypięte karty | | Session.cs 39 | ✔ | – | ✔ |
| Synchronizowane: historia | łączona, usunięcia i „wyczyść wszystko” przenoszone | LanHistorySync.cs | ✔ | – | ✔ |
| Synchronizowane: tryb bankowy (wszystkie profile) | zaszyfrowany | LanSync.cs 703 | ✔ | – | ✔ |
| Synchronizowane: Szybki Dostęp z grafikami | TCP, szyfrowane, tylko od sparowanych, ochrona ścieżek | QuickAccessLanSync.cs | ✔ | – | ✔ |
| Nowsza zmiana wygrywa (ustawienia), znacznik czasu zmian | | LanSync.cs 765 | ✔ | – | ✔ |
| 📺 Wyślij do… (karta na inny komputer) | zaszyfrowane, z miejscem w filmie YouTube | Innovations.cs 404 | ✔ | ✔ | – |
| Ikona w zasobniku pulsuje przy synchronizacji | | Tray.cs, LanSync.cs TrayPulse | ✔ | ✔ | ✔ |
| Dymki „Zsynchronizowano…” | | LanSync.cs | ✔ | ✔ | ✔ |
| Tryb cichy LAN | bez dymków | Settings.cs | ✔ | ✔ | – |
| Panel diagnostyczny LAN | urządzenia, status, liczniki, log (250 wpisów), odśwież, wyczyść | LanSync.cs | ✔ | ✔ | – |
| Ostrzeżenia: różnica zegarów, za duży pakiet, błędy odbioru i nadawania | | LanSync.cs 898, 722 | ✔ | ✔ | ✔ |
| Zostań w zasobniku | zamknięcie okna = ikonka przy zegarze; menu: Otwórz, Synchronizuj teraz, Zamknij | Tray.cs | ✔ | ✔ | ✔ |
| Synchronizacja E2E plikiem | eksport i import paczki: hasło + AES-GCM, PBKDF2 600 tys. | SyncE2E.cs | ✔ | ✔ | – |

## D. Wideo (`FloatingVideo.cs`, `Extras.cs`, `DefaultBrowser.cs`)
| Funkcja | Opis | Kod | Impl. | UI | Sys. |
|---|---|---|:-:|:-:|:-:|
| ▣ Film na wierzchu | osobne okno z widokiem filmu, reszta strony ukryta | FloatingVideo.cs | ✔ | ✔ | – |
| Działa niezależnie od karty | zamknięcie karty nie zatrzymuje filmu | FloatingVideo.cs 19 | ✔ | ✔ | ✔ |
| Gra po zamknięciu głównego okna | okienko jest osobnym oknem – proces żyje, dopóki ono jest otwarte (także bez zasobnika); okno główne zamknięte, połączenia LAN i Sejfu zatrzymane | DefaultBrowser.cs 92, MainWindow.xaml.cs 113 | ✔ | ✔ | ✔ |
| Ponowne uruchomienie Velivo przy grającym tylko okienku | zamyka okienko i startuje pełne Velivo | DefaultBrowser.cs 96 | ✔ | – | ✔ |
| Zmiana rozmiaru, przeciąganie za pasek | | FloatingVideo.cs | ✔ | ✔ | – |
| Przypinka „zawsze na wierzchu” | przełącznik, zapamiętywany | FloatingVideo.cs 141 | ✔ | ✔ | – |
| Przezroczystość 15–100% | warstwa okna Windows; suwak i kółko; zapamiętywana | FloatingVideo.cs 188 | ✔ | ✔ | – |
| Zapamiętywanie pozycji i rozmiaru | z kontrolą, czy mieści się na ekranie | FloatingVideo.cs 153 | ✔ | – | ✔ |
| Pasek przewijania filmu w okienku | | FloatingVideo.cs 100 | ✔ | ✔ | – |
| YouTube: wznowienie przez odtwarzacz YT, czas w adresie | | FloatingVideo.cs 81, 219 | ✔ | – | ✔ |
| Powrót do karty | przycisk w okienku | FloatingVideo.cs | ✔ | ✔ | – |
| ⧉ Obraz w obrazie | przycisk nad filmem i w menu (największy film) | Extras.cs 373, 489 | ✔ | ✔ | – |
| Okienko PiP zachowane po zamknięciu karty | | Extras.cs 462 | ✔ | – | ✔ |
| Wyciszanie karty | | TabExtras.cs | ✔ | ✔ | – |

## E. Pobieranie (`Downloads.cs`, `VideoDownloader.cs`, `MediaDownloader.cs`, `Integrity.cs`)
| Funkcja | Opis | Kod | Impl. | UI | Sys. |
|---|---|---|:-:|:-:|:-:|
| Menedżer pobierania Velivo (jak IDM) | własne okno, lista, postęp | Downloads.cs 20 | ✔ | ✔ | – |
| Wiele połączeń (1–16) | segmenty dla plików ≥2 MB z obsługą zakresów | Downloads.cs | ✔ | ✔ | ✔ |
| Wstrzymaj / wznów | wznawianie od miejsca przerwania, gdy serwer pozwala | Downloads.cs | ✔ | ✔ | – |
| Ponawianie (5 prób, rosnące opóźnienie) | | Downloads.cs 378 | ✔ | – | ✔ |
| Cookies i przekierowania strony | pobiera zalogowany plik, ręczne przekierowania | Downloads.cs 174, 196 | ✔ | – | ✔ |
| Awaryjnie przez silnik | gdy pliku nie da się przejąć | Downloads.cs 22 | ✔ | – | ✔ |
| Pytaj, gdzie zapisać | | Settings.cs | ✔ | ✔ | – |
| Otwórz, pokaż w folderze, anuluj (usuwa część), lista zapamiętana | | Downloads.cs | ✔ | ✔ | – |
| Wbudowane okienko pobierania Edge ukryte | | MainWindow.xaml.cs 531 | ✔ | – | ✔ |
| ⬇ Pobierz nad filmem i w menu | | Extras.cs, VideoDownloader.cs | ✔ | ✔ | – |
| Wykryj media do pobrania | lista źródeł audio i wideo, pobranie jako audio | MediaDownloader.cs | ✔ | ✔ | – |
| yt-dlp | YouTube, m3u8/mpd i inne serwisy; pobierany raz, aktualizacja co 14 dni | VideoDownloader.cs | ✔ | ✔ | ✔ |
| FFmpeg | najlepsza jakość ≤1080p (MP4) i MP3 | VideoDownloader.cs | ✔ | ✔ | – |
| Tryby: najlepsza, prosta (1 plik), M4A, MP3 | | VideoDownloader.cs 206 | ✔ | ✔ | – |
| Wybór folderu filmów (zapamiętany) | | VideoDownloader.cs 97 | ✔ | ✔ | – |
| Napisy | **brak** | – | – | – | – |
| Weryfikacja SHA-256 narzędzi | yt-dlp, FFmpeg, Piper, uBOL | Integrity.cs | ✔ | – | ✔ |
| Znacznik „plik z internetu” (MOTW) | | Integrity.cs 30 | ✔ | – | ✔ |
| Separator `--` dla yt-dlp | | VideoDownloader.cs | ✔ | – | ✔ |

## F. Prywatność i bezpieczeństwo (`AdBlocker.cs`, `FilterLists.cs`, `Ubol.cs`, `PrivacyAndProfiles.cs`, `Ochrona.cs`, `Extras.cs`, `Innovations.cs`, `Identity.cs`, `ElementBlocker.cs`)
| Funkcja | Opis | Kod | Impl. | UI | Sys. |
|---|---|---|:-:|:-:|:-:|
| AdBlock Velivo | domeny w HashSet + fragmenty URL; reguły `||domena^` z EasyList | AdBlocker.cs 8 | ✔ | ✔ | ✔ |
| Pełne listy: EasyList, EasyPrivacy, polska (~97 tys. reguł) | pobierane w tle, co 4 dni, „Aktualizuj teraz” | FilterLists.cs | ✔ | ✔ | ✔ |
| Własne reguły użytkownika (`filters.txt`) | | MainWindow.xaml.cs 125 | ✔ | – | ✔ |
| uBlock Origin Lite wbudowany | + cotygodniowa aktualizacja z GitHub z SHA-256 | Ubol.cs | ✔ | ✔ | ✔ |
| Tarcza: licznik i lista zablokowanych | Velivo + uBOL bez dubli | Ubol.cs 100 | ✔ | ✔ | – |
| Włącz / wyłącz AdBlock | | Lang/okno | ✔ | ✔ | – |
| Ochrona przed śledzeniem: zrównoważona / ścisła | poziom silnika Edge wg aktywnej strony | PrivacyAndProfiles.cs 257 | ✔ | ✔ | ✔ |
| Reguły dla domen | blokuj JavaScript, bez cookies, wymuś trackery, czyść dane po wejściu | PrivacyAndProfiles.cs | ✔ | ✔ | ✔ |
| Zaufane domeny | | PrivacyAndProfiles.cs | ✔ | ✔ | – |
| Dziennik „Co zablokowano i dlaczego” | godzina, domena, powód, pełny adres | PrivacyAndProfiles.cs | ✔ | ✔ | – |
| Paragon prywatności | firmy, kraje, brokerzy danych, próby fingerprintingu (canvas, WebGL, audio) | Innovations.cs 531 | ✔ | ✔ | ✔ |
| Fingerprinting | ⚠ **wykrywanie i raport**, bez zmiany odczytów | Innovations.cs 535 | ⚠ | ✔ | ✔ |
| Ukrycie marki „WebView2” | Client Hints i `userAgentData` | Identity.cs | ✔ | – | ✔ |
| DNT + Global Privacy Control | | Settings.cs | ✔ | ✔ | ✔ |
| Odrzucanie banerów cookies | „Odrzuć” / „Tylko niezbędne”, nigdy „Akceptuj”; wyjątek per strona | Extras.cs 315 | ✔ | ✔ | ✔ |
| Blokada wyskakujących okien | | Settings.cs | ✔ | ✔ | ✔ |
| 🚫 Blokuj element | wybór myszką, kółko zmienia zakres, reguła domena+CSS, przywracanie | ElementBlocker.cs | ✔ | ✔ | – |
| Wykrywanie podróbek offline | marka na obcej domenie (lista marek), literówki, podmienione znaki | Ochrona.cs 13 | ✔ | ✔ | ✔ |
| Domeny `xn--` / litery z innych alfabetów | ostrzeżenie | Ochrona.cs | ✔ | ✔ | ✔ |
| Podróbki stron z zapisanymi hasłami | | Ochrona.cs | ✔ | ✔ | ✔ |
| Ostrzeżenie z domyślnym „Nie” + zapamiętanie prawdziwej strony | | Ochrona.cs | ✔ | ✔ | – |
| Najpierw HTTPS | strony bez szyfrowania po ostrzeżeniu | Ochrona.cs | ✔ | ✔ | ✔ |
| SmartScreen | | Settings.cs | ✔ | ✔ | ✔ |
| Bezpieczne płatności | `SetWindowDisplayAffinity` – okno niewidoczne dla nagrywania na stronach banków i płatności | Ochrona.cs 297 | ✔ | ✔ | ✔ |
| Kanał strona ↔ program ukryty, losowy znacznik uruchomienia | | Extras.cs 20, Identity.cs | ✔ | – | ✔ |
| Karty prywatne (InPrivate) | nic nie zapisują | MainWindow.xaml.cs | ✔ | ✔ | – |
| Czyść dane przy zamknięciu / wyczyść dane przeglądania | historia, cache, pobrania, cookies, formularze, hasła silnika | Settings.cs | ✔ | ✔ | – |
| Wykrywacz sztuczek presji w sklepach | liczniki, „ostatnie sztuki”, „X osób ogląda”, odznaczanie dodatków, ukryte opłaty | Innovations.cs 254 | ✔ | ✔ | ✔ |
| Siatka bezpieczeństwa błędów | błąd nie wyłącza programu, `bledy.log`, maks. 1 komunikat / 30 s | App.xaml.cs 33 | ✔ | – | ✔ |
| Ochrona przed zamknięciem okna przez silnik | | WindowClose.cs | ✔ | – | ✔ |

## G. Karty i okna (`TabExtras.cs`, `Session.cs`, `MainWindow.xaml.cs`, `Extras.cs`, `MobileView.cs`, `Zoom.cs`, `Screenshot.cs`, `ContextMenu.cs`, `History.cs`, `Features.cs`)
| Funkcja | Opis | Kod | Impl. | UI | Sys. |
|---|---|---|:-:|:-:|:-:|
| Grupy kart | nazwa, kolor, zwijanie, dodaj/usuń, rozgrupuj, zamknij grupę; zapisywane z sesją | TabExtras.cs 164 | ✔ | ✔ | – |
| Zestawy kart | zapisz otwarte, otwórz, zastąp, usuń | TabExtras.cs 94 | ✔ | ✔ | – |
| Przypięte karty | na początku paska, bez krzyżyka, po restarcie | Session.cs 61 | ✔ | ✔ | – |
| Automatyczne odświeżanie | interwały z menu karty | TabExtras.cs 57 | ✔ | ✔ | – |
| Szukaj w kartach (Ctrl+Shift+A) | | Session.cs | ✔ | ✔ | – |
| Duplikuj, zamknij inne, zamknij po prawej, przywróć zamkniętą | | Session.cs 163 | ✔ | ✔ | – |
| Przywracanie sesji | | Session.cs 10 | ✔ | ✔ | ✔ |
| Linki w tej samej karcie | Ctrl+klik = nowa karta | MainWindow.xaml.cs 401 | ✔ | ✔ | – |
| Gesty myszy | ← → ↑ ↓ ↓→ | Extras.cs 292 | ✔ | ✔ | – |
| Kółko na przyciskach | np. natężenie trybu nocnego, przezroczystość | DarkMode.cs, FloatingVideo.cs | ✔ | ✔ | – |
| Efekty wejścia stron + szybkość 0–5 s | wyostrzenie, kinowe, przyciemnienie, brak | Extras.cs 132–154 | ✔ | ✔ | – |
| Szybsze otwieranie (wczytywanie po najechaniu) | | Extras.cs 87 | ✔ | ✔ | ✔ |
| Wersja telefonu dla strony | UA Androida + Client Hints, zapamiętana | MobileView.cs | ✔ | ✔ | – |
| Powiększenie: domyślne + dla strony | przenoszone na karty z tą samą stroną | Zoom.cs | ✔ | ✔ | ✔ |
| Zrzut ekranu (widoczna część / cała strona) | `Obrazy\Zrzuty Velivo`, dymek Otwórz / Pokaż | Screenshot.cs | ✔ | ✔ | – |
| Menu prawego przycisku | wyszukaj zaznaczenie, tłumacz zaznaczenie i stronę PL↔EN, czytaj, blokuj element | ContextMenu.cs | ✔ | ✔ | – |
| Zakładki (Ctrl+D), wszystkie zakładki | | Features.cs 18 | ✔ | ✔ | – |
| Historia (Ctrl+H) | dzień → sesja → strony; szukanie; usuń wpis, dzień, sesję, całość; otwórz sesję | History.cs | ✔ | ✔ | – |
| Jedno okno programu | linki z innych programów jako nowe karty (nazwany potok) | DefaultBrowser.cs 25 | ✔ | – | ✔ |
| Domyślna przeglądarka | rejestracja i otwarcie ustawień Windows | DefaultBrowser.cs 127 | ✔ | ✔ | – |
| Skróty klawiszowe | Ctrl+T, Ctrl+Shift+N, Ctrl+W, Ctrl+Shift+T, Ctrl+H, Ctrl+J, Ctrl+D, Ctrl+Shift+A, Ctrl+Shift+F, Ctrl+Shift+U, Alt+←/→, F5, Ctrl ±/0 | Lang.cs | ✔ | ✔ | – |

## H. Czytanie (`ReaderMode.cs`, `ReadAloud.cs`, `Piper.cs`, `Innovations.cs §1`)
| Funkcja | Opis | Kod | Impl. | UI | Sys. |
|---|---|---|:-:|:-:|:-:|
| Tryb czytania | jasny / ciemny / nocny + suwak, rozmiar zapamiętany | ReaderMode.cs | ✔ | ✔ | – |
| Lokalne streszczenie | bez AI i chmury: ważenie zdań | ReaderMode.cs 241 | ✔ | ✔ | – |
| Czytanie na głos (Ctrl+Shift+U) | strona albo zaznaczenie; od miejsca prawego kliku; podświetlanie akapitu i przewijanie | ReadAloud.cs | ✔ | ✔ | – |
| Tempo, pauza / wznów, wybór głosu | automatycznie wg języka strony | ReadAloud.cs 84 | ✔ | ✔ | – |
| Piper – naturalne głosy offline | pobierane raz (~60 MB, suma SHA-256), proces w tle, podświetlanie zdań | Piper.cs | ✔ | ✔ | ✔ |
| 🧠 Gdzie ja to czytałem? (Ctrl+Shift+F) | lokalny indeks treści; pomija prywatne, banki, płatności, pocztę, strony z hasłem | Innovations.cs 26 | ✔ | ✔ | ✔ |
| Tłumaczenie (Google) | strona i zaznaczenie | ContextMenu.cs 24 | ✔ | ✔ | – |

## I. Wygląd, personalizacja, dodatki (`ModernLook.cs`, `Themes.cs`, `DarkMode.cs`, `Lang.cs`, `Settings.cs`, `QuickAccess.cs`, `Extensions.cs`, `WebStore.cs`, `Dzwiek.cs`)
| Funkcja | Opis | Kod | Impl. | UI | Sys. |
|---|---|---|:-:|:-:|:-:|
| Styl Nowoczesny / Kolorowy | ikony Windows 11, akcent | ModernLook.cs | ✔ | ✔ | – |
| 10 motywów | | Themes.cs | ✔ | ✔ | – |
| Tryb ciemny i nocny stron | zapamiętane osobno dla każdej strony; propozycja restartu silnika | DarkMode.cs 50, 177 | ✔ | ✔ | ✔ |
| Kompaktowy pasek, adaptacyjny układ, menu „…” | | MainWindow.xaml.cs | ✔ | ✔ | ✔ |
| Język PL/EN | tłumaczenie interfejsu i okien XAML | Lang.cs | ✔ | ✔ | – |
| Wyszukiwarka i skróty wyszukiwania | | Settings.cs, Extras.cs 504 | ✔ | ✔ | – |
| Strona startowa | | Settings.cs | ✔ | ✔ | – |
| Profile użytkowników Velivo | dodaj, przełącz, usuń, ikona, `--profil` | PrivacyAndProfiles.cs, App.xaml.cs | ✔ | ✔ | – |
| Szybki Dostęp: skróty i grupy | | newtab.js | ✔ | ✔ | – |
| Szybki Dostęp: miniatury stron | robione przez Velivo w niewidocznym oknie | QuickAccess.cs 249 | ✔ | ✔ | ✔ |
| Szybki Dostęp: profile z PIN-em | | newtab.js, pin.js | ✔ | ✔ | – |
| Szybki Dostęp: kosz z przywracaniem | | newtab.js | ✔ | ✔ | – |
| Szybki Dostęp: własne ikony | obrazek z pliku, wklejony, usuwanie | newtab.js | ✔ | ✔ | – |
| Szybki Dostęp: tło i rozmiar kafelków | | newtab.js | ✔ | ✔ | – |
| Szybki Dostęp: import zakładek (JSON/HTML) | | newtab.js zbierzZJson/Html | ✔ | ✔ | – |
| Szybki Dostęp: kopia na dysk, folder, nazwa komputera, sync konta Google (w Chrome) | | newtab.js, tlo.js | ✔ | ✔ | ✔ |
| Dodatki Chromium | rozpakowane, przeładowanie po zmianie kodu, okienka popup, pasek ikon, przypinanie, ustawienia dodatku | Extensions.cs, Features.cs 172 | ✔ | ✔ | ✔ |
| Instalacja z Chrome Web Store | przycisk „➕ Dodaj do Velivo”, przechwycenie `.crx` (CRX2/CRX3) | WebStore.cs | ✔ | ✔ | – |
| Głośniki Velivo | wybór wyjścia, „Nie gub dźwięku” (sprawdzanie co 2 s), mikser | Dzwiek.cs | ✔ | ✔ | ✔ |
| Dymki Velivo nad paskiem zadań | | Screenshot.cs 89 | ✔ | ✔ | ✔ |

## J. Wydajność (`Junk.cs`, `MainWindow.xaml.cs`, `Extras.cs`, `Bank.cs`)
| Funkcja | Opis | Kod | Impl. | UI | Sys. |
|---|---|---|:-:|:-:|:-:|
| Folder cache / RAM dysk | własny podfolder, czyszczona tylko jego zawartość | Junk.cs 14 | ✔ | ✔ | ✔ |
| Czyść śmieci teraz / przy starcie, rozmiar | | Junk.cs 82 | ✔ | ✔ | ✔ |
| Rozmiar pamięci podręcznej | | Settings.cs | ✔ | ✔ | ✔ |
| Filtr reklam budowany w tle i podmieniany w całości | | FilterLists.cs 23 | ✔ | – | ✔ |
| Pomijanie zdjęć i czcionek w przechwytywaniu żądań | | MainWindow.xaml.cs 425 | ✔ | – | ✔ |
| Pamięć podręczna profilu bankowego w RAM | odczyt pliku tylko po zmianie | Bank.cs 54 | ✔ | – | ✔ |
| WebView2 (silnik Edge) | wspólny z systemem | MainWindow.xaml.cs | ✔ | – | ✔ |
| Bez konta i bez chmury | | całość | ✔ | – | ✔ |
| Osobny folder danych (`PRZEGLADARKA_DANE`), log diagnostyczny (`VELIVO_DEBUG`) | | MainWindow.xaml.cs 20, 122 | ✔ | – | ✔ |

## BRAKUJE W TABELI
Funkcje znalezione w kodzie, których nie było w poprzedniej tabeli porównawczej:
1. Przycisk 🏦 z menu prawego przycisku i zielone karty bankowe
2. Czyszczenie cache i historii profilu bankowego po zamknięciu trybu
3. Otwieranie trybu samym kluczem sprzętowym (bez hasła)
4. Kilka kluczy sprzętowych w jednym trybie
5. „Zapomniałem hasła – wyczyść tryb bankowy”
6. ⚙ Ustawienia trybu bankowego (hasło, klucze, czas blokady)
7. 🔒 Zablokuj teraz
8. ➕ Dodaj tę stronę do Moich banków / sklepów
9. 🔑 Wpisz login na stronie banku
10. 🧪 Skopiuj opis formularza logowania (bez danych)
11. 💳 Wypełnij kartę na tej stronie (z trybu bankowego)
12. 👁 Pokaż numer i CVV, 📋 kopiowanie z czyszczeniem schowka po 30 s
13. 🔑 Moje loginy i hasła w trybie bankowym, wpis tylko na tej samej domenie
14. 🎲 Generator haseł w trybie bankowym
15. 🔢 Znaki hasła z numerami w notatkach
16. Kategorie notatek (Login, PIN, Przelewy, Kody odzyskiwania, Inne)
17. 🔑 Wpisz login z notatki
18. Notatki i wyszukiwarka nie blokują strony
19. 🔍 Szukaj w mojej bazie
20. Rodzaje dokumentów (dowód, paszport, prawo jazdy, PESEL, NI number, ubezpieczenie)
21. Rachunki: numer klienta / referencja i strona płatności, 🌐 Otwórz stronę płatności
22. Jednorazowy rachunek znika po opłaceniu
23. Nazwy rachunków z arkusza do wyboru w „Za co”
24. ➕ Dodaj rachunki z arkusza
25. Arkusz: ➕ Wiersz (kolejny miesiąc) z kopią kwot
26. Arkusz: dodawanie, zmiana nazwy i usuwanie kolumn i wierszy
27. Arkusz: sortowanie miesięcy i scalanie przy imporcie, pomijanie kolumn „Razem”
28. Arkusz: zabezpieczenie jednorazowym znacznikiem
29. 📊 Zestawienie płatności (osobne okno: ręczne dopisywanie i usuwanie płatności)
30. ❓ Instrukcja trybu bankowego w aplikacji (PL/EN)
31. Synchronizacja trybu bankowego (wszystkich profili) między sparowanymi komputerami
32. Menedżer haseł: szukanie po domenie, loginie i nazwie; kolumna „Źródło”
33. Menedżer haseł: edycja, usuń zaznaczone, usuń wszystkie
34. Kopiuj login / hasło, pokaż/ukryj hasło, wklej ze schowka
35. Propozycja aktualizacji zmienionego hasła
36. Wybór konta do wpisania („Inne konto z bazy Velivo…”)
37. Import KeePass / KeePassXC z pomijaniem kosza
38. Sejf: sprawdzenie przed wypełnieniem, że karta nadal jest na tej stronie
39. Sejf: zapis loginu w zaszyfrowanym sejfie z okienka rozszerzenia
40. Autouzupełnianie konta bankowego (IBAN, numer konta, sort code)
41. Autouzupełnianie: ukryte pola nigdy nie są wypełniane
42. Autouzupełnianie: tylko prawdziwe kliknięcia myszą
43. Pokaż zapisane dane autouzupełniania, usuń karty, usuń adresy
44. Import zakładek z Vivaldi
45. Propozycja sparowania, gdy w sieci jest drugi Velivo
46. Wybór, czyje ustawienia zostają przy pierwszym połączeniu
47. Wygasanie prób parowania i odrzucanie próśb z innego profilu
48. Odrzucanie powtórzonych pakietów i kontrola zegara (±5 min)
49. Kompresja GZip pakietów i limit po rozpakowaniu
50. Pytanie o przełączenie profilu, gdy drugi komputer pracuje na innym profilu
51. Ustawienia lokalne nie synchronizowane (głośniki, foldery, język, rozmiar okien)
52. Synchronizacja historii (łączona, z przenoszeniem usunięć)
53. Synchronizacja przypiętych kart
54. Synchronizacja listy dodatków z automatyczną instalacją na drugim komputerze
55. Synchronizacja Szybkiego Dostępu z grafikami (TCP, tylko od sparowanych)
56. Lista usuniętych zakładek przechowywana 90 dni
57. „Nowsza zmiana wygrywa” przy ustawieniach
58. „Wyślij do…” przenosi też miejsce w filmie YouTube
59. Panel diagnostyczny LAN: liczniki, log 250 wpisów, „Odśwież”, „Wyczyść log”
60. Ostrzeżenie o różnicy zegarów i o za dużym pakiecie
61. Menu ikony w zasobniku: Otwórz, Synchronizuj teraz, Zamknij całkowicie
62. Synchronizacja E2E plikiem (eksport i import paczki)
63. Ponowne uruchomienie Velivo, gdy gra już tylko okienko filmu
64. Pasek przewijania filmu w okienku „Film na wierzchu”
65. Przycisk powrotu do karty w okienku filmu
66. YouTube w okienku: wznawianie przez odtwarzacz i czas w adresie
67. Okienko PiP zostaje po zamknięciu karty
68. Kontrola, czy zapamiętana pozycja okienka mieści się na ekranie
69. Wyciszanie karty
70. Pobieranie z cookies i przekierowaniami strony (pliki po zalogowaniu)
71. Pobieranie awaryjne przez silnik
72. Pytaj, gdzie zapisać każdy plik
73. Anulowanie z usunięciem pobranej części; lista pobrań zapamiętana
74. Wbudowane okienko pobierania Edge ukryte
75. Wykryj media do pobrania (lista źródeł, pobranie jako audio)
76. Tryb „prosty” (jeden plik bez FFmpeg)
77. Zapamiętany folder filmów
78. Automatyczna aktualizacja yt-dlp co 14 dni
79. Separator `--` w yt-dlp
80. Własne reguły blokowania (`filters.txt`)
81. Cotygodniowa aktualizacja uBlock Origin Lite z weryfikacją SHA-256
82. Tarcza: lista zablokowanych na stronie, bez dubli Velivo/uBOL
83. Włącz/wyłącz AdBlock jednym przyciskiem
84. Poziom ochrony silnika ustawiany wg aktywnej strony
85. Reguła: blokuj JavaScript dla domeny
86. Reguła: nie wysyłaj cookies dla domeny
87. Reguła: automatyczne czyszczenie danych po wejściu na domenę
88. Zaufane domeny
89. Dziennik „Co zostało zablokowane i dlaczego”
90. Ukrycie marki „WebView2” przed stronami
91. Wyjątek od odrzucania banerów dla strony
92. 🚫 Blokuj element (wybór myszką, zmiana zakresu kółkiem, przywracanie)
93. Ostrzeżenie o podróbce z domyślnym „Nie” i zapamiętaniem prawdziwej strony
94. Najpierw HTTPS (ostrzeżenie przed stroną bez szyfrowania)
95. Ukryty kanał strona ↔ program z losowym znacznikiem uruchomienia
96. Wyczyść dane przeglądania (wybór kategorii)
97. Siatka bezpieczeństwa błędów i `bledy.log`
98. Ochrona przed zamknięciem okna przez silnik
99. Duplikuj kartę, zamknij inne, zamknij po prawej
100. Linki w tej samej karcie (Ctrl+klik = nowa)
101. Kółko myszy na przyciskach
102. Szybsze otwieranie stron (wczytywanie po najechaniu)
103. Wersja telefonu dla wybranej strony
104. Powiększenie zapamiętane dla strony i przenoszone na karty
105. Zrzut ekranu (widoczna część / cała strona) z dymkiem Otwórz / Pokaż
106. Menu prawego przycisku: wyszukaj zaznaczenie, tłumacz zaznaczenie i stronę PL↔EN
107. Zakładki i „Wszystkie zakładki”
108. Historia pogrupowana dzień → sesja, usuwanie dnia i sesji, otwieranie całej sesji
109. Jedno okno programu – linki z innych programów jako karty
110. Domyślna przeglądarka (rejestracja w Windows)
111. Skróty klawiszowe (pełna lista w sekcji G)
112. Tryb czytania: rozmiar zapamiętany
113. Lokalne streszczenie w trybie czytania
114. Czytanie od miejsca prawego kliknięcia, podświetlanie akapitu
115. Tempo czytania, pauza / wznów, wybór polskiego głosu
116. „Gdzie ja to czytałem?” pomija banki, płatności, pocztę i strony z hasłem
117. Tłumaczenie strony i zaznaczenia
118. Styl Nowoczesny / Kolorowy
119. Tryb ciemny i nocny zapamiętany osobno dla każdej strony
120. Propozycja restartu silnika po zmianie trybu
121. Kompaktowy, adaptacyjny pasek z menu „…”
122. Profile Velivo z ikoną i parametr `--profil`
123. Szybki Dostęp: miniatury stron robione w tle
124. Szybki Dostęp: profile z PIN-em
125. Szybki Dostęp: kosz z przywracaniem
126. Szybki Dostęp: własne ikony (plik, wklejenie)
127. Szybki Dostęp: tło i rozmiar kafelków
128. Szybki Dostęp: import zakładek JSON/HTML
129. Szybki Dostęp: kopia na dysk, nazwa komputera, synchronizacja z kontem Google w Chrome
130. Dodatki Chromium: okienka popup, pasek ikon, przypinanie, strona ustawień
131. Przeładowanie dodatków po zmianie ich kodu
132. Instalacja z Chrome Web Store (przechwycenie `.crx`)
133. Wybór głośników Velivo i „Nie gub dźwięku”
134. Mikser głośności Windows z menu
135. Dymki Velivo nad paskiem zadań
136. Folder cache z czyszczeniem tylko własnego podfolderu
137. Filtr reklam budowany w tle
138. Pomijanie zdjęć i czcionek w przechwytywaniu żądań (szybkość)
139. Osobny folder danych `PRZEGLADARKA_DANE` i log `VELIVO_DEBUG`
