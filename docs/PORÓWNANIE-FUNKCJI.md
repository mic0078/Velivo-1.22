# Velivo 1.22 – pełna lista funkcji i porównanie

Lista funkcji Velivo pochodzi z **kodu źródłowego** (`src/`, wersja na `main` po commicie `8f3717b`).
Funkcje innych przeglądarek dotyczą wersji standardowych, bez dodatków, według stanu na październik 2026.
Legenda: 🟢 jest · ⚠️ częściowo, inaczej, zależnie od ustawień albo przez chmurę · 🔴 brak wbudowanego odpowiednika.

## 1. Bezpieczeństwo i prywatność
| Funkcja | Velivo | Brave | Firefox | Vivaldi | Opera |
|---|:-:|:-:|:-:|:-:|:-:|
| Blokowanie reklam (własny bloker + wbudowany uBlock Origin Lite) | 🟢 | 🟢 | ⚠️ tylko trackery, reklamy przez dodatek | 🟢 | 🟢 |
| EasyList | 🟢 | 🟢 | 🔴 (dodatek) | 🟢 | 🟢 |
| EasyPrivacy | 🟢 | 🟢 | ⚠️ własne listy (Disconnect) | 🟢 | 🟢 |
| Polska lista filtrów | 🟢 | 🟢 lista regionalna | 🔴 (dodatek) | ⚠️ do dodania ręcznie | ⚠️ do dodania ręcznie |
| Blokowanie trackerów (zrównoważone / ścisłe) | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Reguły prywatności dla domen (blokuj JS, bez cookies, wymuś blokadę, czyść dane po wejściu) | 🟢 | ⚠️ Shields per strona | ⚠️ uprawnienia per strona | ⚠️ uprawnienia per strona | ⚠️ uprawnienia per strona |
| Fingerprinting | ⚠️ **wykrywanie i raport** (canvas, WebGL, audio); ukrycie marki WebView2; trackery z list blokowane. Nie fałszuje odczytów. | 🟢 farbling | 🟢 ochrona przed fingerprintingiem | ⚠️ | ⚠️ |
| Paragon prywatności (firmy, kraje, brokerzy danych, próby rozpoznania komputera) | 🟢 | ⚠️ licznik Shields | ⚠️ panel ochrony | ⚠️ licznik | ⚠️ licznik |
| „Nie śledź” (DNT + Global Privacy Control) | 🟢 | 🟢 GPC | 🟢 | ⚠️ DNT | ⚠️ DNT |
| Automatyczne odrzucanie banerów cookies (nigdy „Akceptuj”) | 🟢 | 🟢 | 🟢 | ⚠️ ukrywanie banerów | 🟢 |
| Wykrywacz sztuczek presji w sklepach (fałszywe liczniki, odznaczanie dodatków) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Wykrywanie podejrzanych domen **bez internetu** | 🟢 | ⚠️ Safe Browsing (online) | ⚠️ Safe Browsing (online) | ⚠️ Safe Browsing (online) | ⚠️ ochrona online |
| Homoglify / litery z innych alfabetów | 🟢 | ⚠️ punycode w pasku | ⚠️ punycode w pasku | ⚠️ punycode w pasku | ⚠️ punycode w pasku |
| Domeny `xn--` | 🟢 ostrzeżenie | ⚠️ wyświetlanie punycode | ⚠️ wyświetlanie punycode | ⚠️ wyświetlanie punycode | ⚠️ wyświetlanie punycode |
| Podszywanie się pod marki (`paypal-secure-login.com`, literówki) | 🟢 | ⚠️ Safe Browsing | ⚠️ Safe Browsing | ⚠️ Safe Browsing | ⚠️ |
| Podróbki stron, do których masz zapisane hasła | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Hasło tylko dla właściwej domeny | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Ochrona danych płatniczych (CVC nigdy nie zapisywany, pytanie przed wpisaniem karty i konta) | 🟢 | ⚠️ | ⚠️ | ⚠️ | ⚠️ |
| Okno niewidoczne dla nagrywania ekranu na stronach banków i płatności | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Ostrzeżenie HTTPS / SmartScreen | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Blokada wyskakujących okien bez kliknięcia | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Ręczne ukrywanie elementów strony | 🟢 | 🟢 | 🔴 | ⚠️ | ⚠️ |
| Znacznik „plik z internetu” na pobranych plikach | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Weryfikacja SHA-256 pobieranych narzędzi | 🟢 | n/d | n/d | n/d | n/d |
| Karty prywatne (InPrivate) | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Szyfrowany eksport/import (paczka E2E: hasło + AES-GCM) | 🟢 | ⚠️ szyfrowany sync | ⚠️ szyfrowany sync | ⚠️ szyfrowany sync | ⚠️ szyfrowany sync |
| Kopia zapasowa i odtwarzanie (plik `.vbank`, plik odzyskiwania parowania) | 🟢 | ⚠️ przez sync | ⚠️ przez sync / profil | ⚠️ przez sync | ⚠️ przez sync |

## 2. Tryb bankowy
| Funkcja | Velivo | Brave | Firefox | Vivaldi | Opera |
|---|:-:|:-:|:-:|:-:|:-:|
| Odizolowany profil na banki (własne cookies, bez dodatków i historii) | 🟢 | ⚠️ osobny profil | ⚠️ kontenery / profil | ⚠️ osobny profil | ⚠️ osobny profil |
| Osobna zaszyfrowana baza | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Szyfrowanie AES-GCM, PBKDF2 600 tys. iteracji | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Własne hasło trybu | 🟢 | 🔴 | ⚠️ hasło główne do haseł | ⚠️ hasło do sync | 🔴 |
| Automatyczna blokada po bezczynności (1–60 min) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Klucz sprzętowy FIDO2 (YubiKey, Titan) z szyfrowaniem bazy (hmac-secret) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Kilka profili bankowych (dla domowników) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Dziennik otwarć (także złe hasła) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Moje banki (otwieranie jednym kliknięciem) | 🟢 | ⚠️ zakładki | ⚠️ zakładki | ⚠️ zakładki | ⚠️ zakładki |
| Moje sklepy online | 🟢 | ⚠️ zakładki | ⚠️ zakładki | ⚠️ zakładki | ⚠️ zakładki |
| Dane logowania do banku (login, PIN, hasło, memorable information) | 🟢 | ⚠️ menedżer haseł | ⚠️ menedżer haseł | ⚠️ menedżer haseł | ⚠️ menedżer haseł |
| Wpisywanie wybranych znaków hasła (2., 5., 9.) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Rachunki bankowe (IBAN, sort code, BIC, odbiorcy) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Karty płatnicze (szyfrowane, CVV opcjonalnie) z przypomnieniem o wygasaniu | 🟢 | ⚠️ karty w autouzupełnianiu | ⚠️ karty w autouzupełnianiu | ⚠️ karty w autouzupełnianiu | ⚠️ karty w autouzupełnianiu |
| Poufne dokumenty (dowód, paszport, NI) z przypomnieniem 60 dni przed końcem ważności | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Notatki z kategoriami | 🟢 | 🔴 | 🔴 | 🟢 Notatki (nieszyfrowane) | ⚠️ Pinboards |
| Arkusz rachunków (miesiące × rachunki, sumy) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Rachunki cykliczne (tydzień … rok, własne „co 10 dni”) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Terminy płatności z codziennym przypomnieniem | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| „Zapłacone” → wpis w arkuszu i następny termin | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Historia miesięczna | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Import z Excela (CSV) i eksport CSV | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Wybór waluty (bez przeliczania) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Ostrzeżenie o stronie podobnej do Twojego banku | 🟢 | ⚠️ | ⚠️ | ⚠️ | ⚠️ |
| Propozycja trybu bankowego, gdy bank otwierasz w zwykłej karcie | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Dane trybu bankowego oddzielone od zwykłego profilu | 🟢 | ⚠️ | ⚠️ | ⚠️ | ⚠️ |

## 3. Hasła i Sejf
| Funkcja | Velivo | Brave | Firefox | Vivaldi | Opera |
|---|:-:|:-:|:-:|:-:|:-:|
| Menedżer haseł (lokalna szyfrowana baza, DPAPI) | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Integracja z zewnętrznym programem Sejf (kluczyk na stronach logowania) | 🟢 | ⚠️ dodatki menedżerów | ⚠️ | ⚠️ | ⚠️ |
| Zapis loginów i haseł (z pytaniem) | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Import z Chrome, Edge, Brave i Opery | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Import i eksport CSV (KeePassXC, Chrome…) | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Generator mocnych haseł | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Autouzupełnianie adresów i kart (lokalna szyfrowana baza) | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Ochrona formularzy (tylko prawdziwe kliknięcia, ukryte pola nie są wypełniane) | 🟢 | ⚠️ | ⚠️ | ⚠️ | ⚠️ |

## 4. Sieć domowa (LAN) – bez chmury
| Funkcja | Velivo | Brave | Firefox | Vivaldi | Opera |
|---|:-:|:-:|:-:|:-:|:-:|
| Synchronizacja bez chmury i bez konta | 🟢 | 🔴 (serwery Brave) | 🔴 (konto Mozilla) | 🔴 (konto Vivaldi) | 🔴 (konto Opera) |
| Szyfrowanie end-to-end | 🟢 AES-GCM + HMAC | 🟢 | 🟢 | 🟢 | ⚠️ |
| Parowanie z porównaniem krótkiego kodu na obu ekranach | 🟢 | ⚠️ kod łańcucha sync | ⚠️ logowanie na konto | ⚠️ logowanie na konto | ⚠️ logowanie na konto |
| Synchronizacja dopiero po sparowaniu; przed nim żadnych danych | 🟢 | n/d | n/d | n/d | n/d |
| Ochrona przed powtórzeniem pakietu i kontrola zegara | 🟢 | n/d | n/d | n/d | n/d |
| Profile rozdzielone (komputery synchronizują tylko ten sam profil) | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Wysyłanie karty na inny komputer („📺 Wyślij do…”) | 🟢 lokalnie | ⚠️ przez sync | 🟢 przez chmurę | ⚠️ | ⚠️ My Flow |
| Synchronizowane: ustawienia, zakładki, hasła, reguły prywatności, Szybki Dostęp, historia, przypięte karty, dodatki, tryb bankowy | 🟢 | ⚠️ przez chmurę | ⚠️ przez chmurę | ⚠️ przez chmurę | ⚠️ przez chmurę |
| Plik odzyskiwania parowania (hasło min. 12 znaków) | 🟢 | ⚠️ kod sync | 🔴 | 🔴 | 🔴 |
| Ikona w zasobniku pulsuje przy synchronizacji | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Dymki „Zsynchronizowano…” i tryb cichy | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Panel diagnostyczny LAN (urządzenia, status, log) | 🟢 | ⚠️ brave://sync-internals | ⚠️ about:sync | ⚠️ | ⚠️ |
| Praca w zasobniku (synchronizacja w tle) | 🟢 | ⚠️ działanie w tle | 🔴 | 🔴 | ⚠️ |

## 5. Wideo
| Funkcja | Velivo | Brave | Firefox | Vivaldi | Opera |
|---|:-:|:-:|:-:|:-:|:-:|
| „Film na wierzchu” – osobne okno filmu | 🟢 | ⚠️ PiP | ⚠️ PiP | ⚠️ PiP | 🟢 Video pop-out |
| Zmiana rozmiaru | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Przezroczystość regulowana suwakiem / kółkiem (15–100%) | 🟢 | 🔴 | 🔴 | 🔴 | ⚠️ tylko wł./wył. (GX: suwak) |
| Przypinanie „zawsze na wierzchu” (wł./wył.) | 🟢 | ⚠️ zawsze na wierzchu | ⚠️ zawsze na wierzchu | ⚠️ zawsze na wierzchu | 🟢 |
| Zapamiętywanie pozycji, rozmiaru i przezroczystości | 🟢 | ⚠️ | ⚠️ | ⚠️ | ⚠️ |
| Pasek przewijania filmu w okienku | 🟢 | 🔴 | ⚠️ | 🔴 | ⚠️ |
| Film gra dalej po zamknięciu głównego okna | ⚠️ gdy włączone „Zostań w zasobniku” | 🔴 | 🔴 | 🔴 | 🔴 |
| Obraz w obrazie (PiP) z przyciskiem ⧉ nad filmem | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |

## 6. Pobieranie
| Funkcja | Velivo | Brave | Firefox | Vivaldi | Opera |
|---|:-:|:-:|:-:|:-:|:-:|
| Własny menedżer pobierania | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Pobieranie wielowątkowe, do 16 połączeń | 🟢 | ⚠️ ukryta flaga Chromium | 🔴 | ⚠️ ukryta flaga | ⚠️ ukryta flaga |
| Pauza i wznowienie | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Ponawianie po zerwaniu (do 5 prób, od miejsca przerwania) | 🟢 | ⚠️ ręcznie | ⚠️ ręcznie | ⚠️ ręcznie | ⚠️ ręcznie |
| Historia pobrań | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Przycisk ⬇ Pobierz nad filmem | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| yt-dlp (YouTube i wiele serwisów) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| FFmpeg – najlepsza jakość do 1080p (MP4) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Tylko dźwięk MP3 / M4A | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Wykrywanie mediów na stronie | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Napisy do filmów | 🔴 **nie zaimplementowane** | 🔴 | 🔴 | 🔴 | 🔴 |
| Sprawdzanie integralności narzędzi (SHA-256) | 🟢 | n/d | n/d | n/d | n/d |

## 7. Czytanie
| Funkcja | Velivo | Brave | Firefox | Vivaldi | Opera |
|---|:-:|:-:|:-:|:-:|:-:|
| Tryb czytania (jasny, ciemny, nocny + suwak) | 🟢 | 🟢 Speedreader | 🟢 | 🟢 | 🟢 |
| Lokalne streszczenie w trybie czytania | 🟢 | ⚠️ Leo (AI online) | ⚠️ AI (opcjonalnie) | 🔴 | ⚠️ Aria (online) |
| „Gdzie ja to czytałem?” – szukanie po treści przeczytanych stron (Ctrl+Shift+F), tylko lokalnie | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Czytanie na głos | 🟢 | 🟢 w Speedreaderze | 🟢 Narrator | 🔴 | 🔴 |
| Naturalne głosy offline (Piper, ok. 60 MB) | 🟢 | 🔴 | ⚠️ głosy systemu | 🔴 | 🔴 |
| Tłumaczenie strony lub zaznaczenia | 🟢 (Google) | 🟢 | 🟢 lokalne | 🟢 | 🟢 |

## 8. Karty i okna
| Funkcja | Velivo | Brave | Firefox | Vivaldi | Opera |
|---|:-:|:-:|:-:|:-:|:-:|
| Grupy kart (nazwa, kolor, zwijanie) | 🟢 | 🟢 | 🟢 | 🟢 stosy | 🟢 wyspy kart |
| Zestawy kart (zapisz i otwórz) | 🟢 | 🔴 | 🔴 | 🟢 sesje | ⚠️ obszary robocze |
| Przypięte karty | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Automatyczne odświeżanie karty | 🟢 | 🔴 | 🔴 | 🟢 | 🔴 |
| Szukanie w kartach (Ctrl+Shift+A) | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Przywróć zamkniętą kartę, duplikuj, wycisz | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Przywracanie sesji po uruchomieniu | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Linki w tej samej karcie (opcja) | 🟢 | 🔴 | ⚠️ about:config | ⚠️ | 🔴 |
| Efekty wejścia stron (wyostrzenie, kinowe, przyciemnienie, 0–5 s) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Gesty myszy | 🟢 | 🔴 | 🔴 | 🟢 | 🟢 |
| Obsługa samą myszką (kółko na przyciskach, sterowanie z kanapy) | 🟢 | 🔴 | 🔴 | ⚠️ | ⚠️ |
| Wersja telefonu dla wybranej strony | 🟢 | ⚠️ narzędzia dewelopera | ⚠️ narzędzia dewelopera | ⚠️ | ⚠️ |
| Zrzut ekranu (widoczna część lub cała strona) | 🟢 | ⚠️ | 🟢 | 🟢 | 🟢 |

## 9. Personalizacja
| Funkcja | Velivo | Brave | Firefox | Vivaldi | Opera |
|---|:-:|:-:|:-:|:-:|:-:|
| Polski i angielski | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| 10 motywów + styl Nowoczesny / Kolorowy | 🟢 | ⚠️ | 🟢 | 🟢 | 🟢 |
| Tryb ciemny stron i tryb nocny z natężeniem | 🟢 | ⚠️ | ⚠️ | ⚠️ | 🟢 |
| Powiększenie zapamiętywane dla strony | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Profile użytkowników z ikoną | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Szybki Dostęp (grupy, miniatury, profile z PIN-em, motywy tła) | 🟢 | ⚠️ | ⚠️ | 🟢 Speed Dial | 🟢 Speed Dial |
| Wyszukiwarka i skróty wyszukiwania („yt koty”) | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Strona startowa | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Wybór głośników dla Velivo i „Nie gub dźwięku” | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Dodatki z Chrome Web Store | 🟢 | 🟢 | ⚠️ własny sklep | 🟢 | 🟢 |

## 10. Wydajność
| Funkcja | Velivo | Brave | Firefox | Vivaldi | Opera |
|---|:-:|:-:|:-:|:-:|:-:|
| Pamięć podręczna na RAM dysku (wybór folderu w ustawieniach) | 🟢 | ⚠️ parametr startowy | ⚠️ about:config | ⚠️ parametr startowy | ⚠️ parametr startowy |
| Rozmiar pamięci podręcznej i czyszczenie przy starcie | 🟢 | ⚠️ | ⚠️ | ⚠️ | ⚠️ |
| Wczytywanie strony po najechaniu na link | 🟢 | ⚠️ preload | ⚠️ preload | ⚠️ preload | ⚠️ preload |
| Lekka powłoka na silniku WebView2 (Edge) | 🟢 | n/d (Chromium) | n/d (Gecko) | n/d (Chromium) | n/d (Chromium) |
| Działanie bez konta | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Podstawowe funkcje bez chmury | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |

## 11. Powiadomienia
| Funkcja | Velivo | Brave | Firefox | Vivaldi | Opera |
|---|:-:|:-:|:-:|:-:|:-:|
| Dymki powiadomień (pobrania, zapis, synchronizacja, przypomnienia) | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Powiadomienia o połączeniu i synchronizacji LAN | 🟢 | n/d | n/d | n/d | n/d |
| Pulsująca ikona w zasobniku przy synchronizacji | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Status urządzeń w sieci (panel LAN) | 🟢 | ⚠️ | ⚠️ | ⚠️ | ⚠️ |
| Ostrzeżenia o błędach (np. różnica zegarów, limit pakietu) i plik `bledy.log` | 🟢 | ⚠️ | ⚠️ | ⚠️ | ⚠️ |
| Przypomnienia o rachunkach, kartach i dokumentach | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |

## 12. Import, eksport, migracja
| Funkcja | Velivo | Brave | Firefox | Vivaldi | Opera |
|---|:-:|:-:|:-:|:-:|:-:|
| Import z innych przeglądarek | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Import i eksport CSV haseł | 🟢 | 🟢 | 🟢 | 🟢 | 🟢 |
| Import CSV arkusza rachunków / eksport CSV | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Szyfrowany eksport i import (paczka E2E) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Kopia trybu bankowego (`.vbank`, zaszyfrowana) | 🟢 | 🔴 | 🔴 | 🔴 | 🔴 |
| Odtworzenie parowania z pliku | 🟢 | ⚠️ | 🔴 | 🔴 | 🔴 |

## Czego Velivo nie ma (z listy wymagań)
- **Pobieranie napisów** do filmów – w kodzie nie ma takiej opcji (yt-dlp nie jest wywoływany z napisami).
- **„Wspólny panel środowiska”** dla wielu komputerów – nie istnieje jako osobna funkcja. Jest panel diagnostyczny LAN (urządzenia, status, log).
- **Fałszowanie odczytów fingerprintingu** – Velivo wykrywa i raportuje próby (paragon prywatności), ale nie zmienia odczytów jak Brave czy Firefox.
- **Film po zamknięciu głównego okna** – działa tylko z opcją „Zostań w zasobniku”. Bez niej zamknięcie okna kończy program.
- **Podpis cyfrowy programu** – brak (patrz `PODPIS-CYFROWY.md`).

## Co wyróżnia Velivo?
Velivo łączy w jednym, lokalnym programie funkcje, które w innych przeglądarkach są rozproszone, wymagają dodatków albo chmury albo nie występują wcale. Są to:
- bezpieczeństwo i prywatność, w tym wykrywanie podróbek bez internetu oraz ukrywanie okna przy płatnościach;
- tryb bankowy z zaszyfrowanym sejfem, kluczem sprzętowym i arkuszem rachunków;
- synchronizacja w sieci domowej bez konta i chmury;
- „Film na wierzchu” z przezroczystością;
- pobieranie wideo i audio;
- organizacja treści, w tym „Gdzie ja to czytałem?”;
- zarządzanie danymi z szyfrowanymi kopiami.

Poszczególne funkcje mają swoje odpowiedniki u konkurencji. Często są one mocniejsze, np. ochrona przed fingerprintingiem w Brave i Firefoksie albo zarządzanie kartami w Vivaldi.

Źródła dla innych przeglądarek: [Opera – Video pop-out](https://forums.opera.com/topic/61285/video-pop-out-transparency-value), [Vivaldi – Periodic Reload](https://vivaldi.com/blog/periodic-tab-reload-vivaldi-browser-snapshot-2056-19/), [Firefox – Tab Groups](https://blog.mozilla.org/en/firefox/tab-groups-community/), [Brave – filtry regionalne](https://github.com/brave/brave-browser/issues/20825), [Opera – gesty myszy](https://blogs.opera.com/news/2015/06/mouse-gestures-in-opera/), [Vivaldi – brak czytania na głos](https://forum.vivaldi.net/topic/68495/read-aloud-feature), [Brave – Speedreader TTS](https://x.com/brave/status/1749962766960472199), [Opera – tryb czytania](https://winaero.com/how-to-enable-reader-mode-in-the-opera-browser/), [Brave i Firefox – fingerprinting](https://www.chrislockard.net/posts/fingerprinting-protection-brave-firefox-safari/).
