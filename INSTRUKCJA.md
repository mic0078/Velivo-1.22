# Velivo – nowy poziom przeglądarki

## Pełny opis i instrukcja obsługi

**Polski** | [English](MANUAL.en.md) · [Strona główna projektu](README.md)

Velivo to prywatna przeglądarka dla Windows. Twoje dane zostają u Ciebie: nie ma konta ani chmury, a historia i hasła nie trafiają na zewnętrzne serwery. Strony wyświetla silnik Microsoft Edge (WebView2), więc wyglądają i działają tak samo jak w Edge i Chrome. Wszystko wokół stron – okno, karty, menu, prywatność, pobieranie, synchronizacja – to własny kod Velivo.

Przeglądarka jest projektowana tak, żeby dało się ją w pełni obsłużyć samą myszką, także z kanapy przy telewizorze. Działa po polsku i po angielsku.

---

## Spis treści

1. [Czym Velivo różni się od innych przeglądarek](#1-czym-velivo-różni-się-od-innych-przeglądarek)
2. [Ile zajmuje i dlaczego tak mało](#2-ile-zajmuje-i-dlaczego-tak-mało)
3. [Instalacja i aktualizacja](#3-instalacja-i-aktualizacja)
4. [Pierwsze kroki – pasek narzędzi](#4-pierwsze-kroki--pasek-narzędzi)
5. [Menu prawego przycisku myszy](#5-menu-prawego-przycisku-myszy)
6. [Obsługa samą myszką: gesty i kółko](#6-obsługa-samą-myszką-gesty-i-kółko)
7. [Karty](#7-karty)
8. [Filmy: Film na wierzchu, obraz w obrazie, pobieranie](#8-filmy-film-na-wierzchu-obraz-w-obrazie-pobieranie)
9. [Prywatność i bezpieczeństwo](#9-prywatność-i-bezpieczeństwo)
10. [Zakupy: wykrywacz sztuczek presji](#10-zakupy-wykrywacz-sztuczek-presji)
11. [Czytanie: na głos, tryb czytania, „Gdzie ja to czytałem?”](#11-czytanie-na-głos-tryb-czytania-gdzie-ja-to-czytałem)
12. [Wyszukiwanie i pasek adresu](#12-wyszukiwanie-i-pasek-adresu)
13. [Wygląd: style, motywy, tryb ciemny i nocny, powiększenie](#13-wygląd-style-motywy-tryb-ciemny-i-nocny-powiększenie)
14. [Szybki Dostęp – strona nowej karty](#14-szybki-dostęp--strona-nowej-karty)
15. [Hasła i Sejf](#15-hasła-i-sejf)
16. [Synchronizacja w sieci domowej (bez chmury)](#16-synchronizacja-w-sieci-domowej-bez-chmury)
17. [Historia i pobrane pliki](#17-historia-i-pobrane-pliki)
18. [Dodatki, profile i narzędzia](#18-dodatki-profile-i-narzędzia)
19. [Skróty klawiszowe](#19-skróty-klawiszowe)
20. [Ustawienia – co gdzie jest](#20-ustawienia--co-gdzie-jest)
21. [Rozwiązywanie problemów i miejsce danych](#21-rozwiązywanie-problemów-i-miejsce-danych)

---

## 1. Czym Velivo różni się od innych przeglądarek

**Velivo wykracza poza granice obecnych przeglądarek.** Chrome, Edge, Firefox i Opera **nie mają wbudowanych** poniższych funkcji. Do części z nich trzeba szukać osobnych dodatków, części nie da się zrobić wcale. W Velivo działają od razu, bez dodatków i bez konta.

| Funkcja | Co robi |
| --- | --- |
| **▣ Film na wierzchu** | Własne okienko z filmem, które zmniejszysz prawie do znaczka (od 160×90 pikseli), z **przezroczystością regulowaną kółkiem myszy** i przypinką „zawsze na wierzchu”. **Gra dalej po zamknięciu karty, a nawet całej przeglądarki.** |
| **🧠 „Gdzie ja to czytałem?”** | Znajduje przeczytany artykuł po słowach z jego **treści**, a nie tylko po tytule. Wszystko zostaje na Twoim komputerze. |
| **⚠ Wykrywacz sztuczek w sklepach** | Ostrzega przed fałszywymi licznikami, presją „ostatnie sztuki” i ukrytymi opłatami. Zaznaczone z góry dodatki w koszyku sam odznacza. |
| **🧾 Paragon prywatności** | Pokazuje, z iloma firmami i krajami łączyła się strona, którzy z nich to brokerzy danych i czy strona próbowała rozpoznać Twój komputer. |
| **📺 Wysyłanie kart w domu** | Strona, a film w tym samym miejscu, otwiera się na drugim komputerze z Velivo. Bez chmury i bez konta. |
| **Synchronizacja bez chmury** | Zakładki, hasła, historia, karty przypięte, Szybki Dostęp i ustawienia między komputerami w domowej sieci, zaszyfrowane. |
| **⬇ Pobieranie filmów jak w IDM** | Przycisk „Pobierz” nad każdym filmem, także na YouTube. Do wyboru jakość, MP3 albo M4A i folder. |
| **🔊 „Czytaj od tego miejsca”** | Prawy przycisk na akapicie i Velivo czyta od tego zdania. |
| **🌅 Tryb nocny z natężeniem** | Jak „Światło nocne” w Windows, regulowany kółkiem myszy. |
| **🛡 Ochrona przed oszustwami bez internetu** | Rozpoznaje fałszywe strony banków, sklepów i portali (`paypa1.com`, `ebay-weryfikacja.top`, litery z innych alfabetów) oraz podróbki stron, do których masz zapisane hasła – zanim strona się otworzy. |
| **💳 Bezpieczne płatności** | Na stronach banków i płatności okno Velivo staje się niewidoczne dla programów nagrywających ekran. |
| **🔐 Hasła odporne na podróbki** | Hasło trafia wyłącznie do prawdziwej domeny. Strona nie może podmienić pomocnika, „kliknąć” za Ciebie ani wyłudzić danych ukrytym polem. |
| **📥 Przeprowadzka z innej przeglądarki** | Zakładki z Chrome, Edge, Brave, Opery i Vivaldi odczytywane wprost; hasła z KeePassXC i innych przez CSV. |
| **🖱 Gesty myszy i obsługa samą myszką** | Wstecz, dalej, nowa karta, zamknięcie, odświeżanie i powiększenie – bez klawiatury. |

---

## 2. Ile zajmuje i dlaczego tak mało

| Co | Ile |
| --- | --- |
| **Instalator** `Velivo-Setup-1.22.exe` | **ok. 6,7 MB** |
| **Program po instalacji** | ok. 30–40 MB (wraz ze składnikami Windows i dodatkiem Szybki Dostęp) |
| **Pamięć RAM samego programu Velivo** (proces `Velivo.exe`) | zwykle **50–80 MB** |
| Dla porównania: instalator Chrome lub Firefoksa | ok. 100–130 MB |

### Dlaczego instalator jest taki mały

1. **Velivo nie dźwiga własnej kopii silnika przeglądarki.** Chrome, Opera czy Brave mają w instalacji cały silnik Chromium, czyli ponad 100 MB. Velivo korzysta z silnika Microsoft Edge (WebView2), który jest już w Windows 10 i 11 i aktualizuje go Windows Update. Velivo dokłada tylko to, co własne: okno, karty, prywatność, pobieranie i synchronizację.
2. **Obudowa jest natywnym programem Windows (WPF, .NET),** a nie stroną internetową udającą program jak w Electronie. Taki kod jest bardzo zwarty.
3. **Duże dodatki pobierają się dopiero wtedy, gdy ich potrzebujesz, i tylko za Twoją zgodą:**
   - pełne listy blokowania reklam pobierają się w tle po pierwszym uruchomieniu;
   - naturalne głosy offline do czytania to ok. 60 MB na głos;
   - narzędzie yt-dlp do pobierania filmów to ok. 18 MB;
   - dodatek FFmpeg do najlepszej jakości i MP3 to ok. 140 MB do pobrania.

### Dlaczego Velivo zużywa tak mało pamięci

1. **Okno, karty, menu i paski są natywne.** Nie są zrobione ze stron internetowych, więc sama obudowa zajmuje 50–80 MB zamiast kilkuset.
2. **Reklamy i trackery są blokowane, zanim się pobiorą.** Strona ma mniej skryptów, obrazków i ramek, więc zajmuje mniej pamięci i wczytuje się szybciej.
3. **Karty w tle oddają pamięć.** Velivo informuje silnik, że karta jest w tle, a ten zwalnia część RAM-u. Muzyka i czaty w tych kartach działają dalej.
4. **Nie ma usług w tle:** brak konta, chmury, telemetrii, sugestii, zakupów i wiadomości na stronie startowej.
5. **Silnik jest współdzielony z Windows.** Część jego bibliotek system i tak trzyma w pamięci.

**Uczciwie:** te 50–80 MB to sam program Velivo. Treść otwartych stron renderuje silnik Edge w osobnych procesach `msedgewebview2.exe`, które też zajmują pamięć, jak w każdej przeglądarce. Ich zużycie zależy od tego, ile kart masz otwartych i jak ciężkie są strony. Dzięki blokowaniu reklam i usypianiu kart w tle zwykle wychodzi mniej niż w Chrome przy tych samych stronach.

---

## 3. Instalacja i aktualizacja

**Wymagania:**
- Windows 10 w wersji 1809 lub nowszej albo Windows 11, 64-bit;
- .NET 10 Desktop Runtime (x64);
- Microsoft Edge WebView2 Runtime (w Windows 11 jest zawsze).

Gdy czegoś brakuje, instalator sam to wykryje i otworzy stronę pobierania.

**Instalacja krok po kroku:**
1. Pobierz `Velivo-Setup-1.22.exe` z folderu [`Instalator`](Instalator).
2. Uruchom instalator. Gdy Windows pokaże „Nieznany wydawca”, kliknij **Więcej informacji → Uruchom mimo to**. Instalator nie ma płatnego podpisu cyfrowego.
3. **Wybierz język instalatora: Polski albo English.** Velivo uruchomi się w tym samym języku. Później zmienisz go w Ustawienia → Wygląd → Język.
4. Jeśli Velivo było już zainstalowane, wybierz:
   - **Zachowaj moje ustawienia i dane** – zwykła aktualizacja (zalecane);
   - **Czysta instalacja** – start od zera. Stare dane trafią do kopii zapasowej z dopiskiem `.kopia-<data>`.
5. Opcjonalnie zaznacz skrót na pulpicie i zakończ.

Instalacja nie wymaga uprawnień administratora. Program trafia do `%LOCALAPPDATA%\Programs\Velivo`. Instalator sam zamyka działające w tle Velivo i omija pliki zablokowane przez antywirusa.

**Ustaw jako domyślną przeglądarkę:** Ustawienia → „Ustaw jako domyślną”. Wtedy linki z innych programów otwierają się w Velivo.

---

## 4. Pierwsze kroki – pasek narzędzi

Velivo startuje na pełnym ekranie. Od lewej:

| Element | Działanie |
| --- | --- |
| **← →** | Wstecz i dalej |
| **⟳** | Odśwież (F5) |
| **⌂** | Strona startowa |
| **Pasek adresu** | Wpisz adres albo szukane słowa. Prawy przycisk daje Wytnij, Kopiuj, Wklej i **Wklej i przejdź** |
| **125%** | Powiększenie strony. **Kółko myszy nad przyciskiem** zmienia, kliknięcie przywraca domyślne. Powiększenie jest zapamiętywane osobno dla każdej strony |
| **🔑** | Pojawia się, gdy Sejf ma loginy dla tej strony; kliknięcie wypełnia logowanie |
| **☆** | Dodaj albo usuń zakładkę (Ctrl+D). Złota gwiazdka oznacza, że strona jest w zakładkach |
| **Prywatność** | Panel reguł dla bieżącej domeny |
| **Ikonki dodatków** | Dodatki Chrome. Można je przypinać i odpinać |
| **🛡 + liczba** | Tarcza: ile zablokowano na stronie. Kliknięcie pokazuje listę i paragon prywatności. Czerwona tarcza oznacza wyłączony AdBlock |
| **⚠ + liczba** (żółty) | Pojawia się w sklepie, który używa sztuczek presji |
| **📚** | Wszystkie zakładki |
| **🌙** | Tryb stron: jasny → ciemny → nocny. W trybie nocnym kółko myszy zmienia natężenie |
| **📷** | Zrzut ekranu: widoczna część albo cała strona |
| **Czytnik** | Tryb czytania ze streszczeniem |
| **🔊** | Czytaj stronę na głos. Podczas czytania pojawiają się pauza, stop i prędkość |
| **⬇** | Pobrane pliki (Ctrl+J) z liczbą trwających pobrań |
| **🧩** | Dodatki: lista z przypinaniem i „Zarządzaj dodatkami…” |
| **🕘** | Historia (Ctrl+H) |
| **⚙** | Ustawienia |
| **Profil** | Aktywny profil; kliknięcie przełącza |

Na pasku kart jest jeszcze **+** (nowa karta, Ctrl+T) i **🕶** (karta prywatna, Ctrl+Shift+N).

Na wąskim ekranie część przycisków przechodzi do menu **„…”**.

---

## 5. Menu prawego przycisku myszy

### Na stronie

Kliknij prawym przyciskiem w dowolnym miejscu strony.

| Pozycja | Kiedy jest widoczna | Działanie |
| --- | --- | --- |
| Wyszukaj „…” / Przejdź do … | Zaznaczony tekst | Wyszukuje zaznaczenie albo otwiera zaznaczony adres |
| Przetłumacz zaznaczenie / Czytaj zaznaczenie na głos | Zaznaczony tekst | Tłumaczy albo czyta zaznaczenie |
| Przetłumacz stronę na polski / angielski | Strona w innym języku | Tłumaczenie Google całej strony |
| 🚫 Blokuj element (reklamę)… | Zawsze | Wskazujesz element, który ma znikać przy każdym wejściu |
| Przywróć zablokowane elementy | Gdy coś zablokowano ręcznie | Cofa blokady na tej stronie |
| 🍪 (Nie) odrzucaj banerów ciasteczek | Zawsze | Wyjątek od automatycznego odrzucania dla tej strony |
| 📱 / 🖥 Wersja telefonu / komputerowa | Zawsze | Przełącza i zapamiętuje dla tej strony |
| ▣ Film na wierzchu · ⬇ Pobierz film · ⧉ Obraz w obrazie | Na filmie albo na YouTube | Patrz [rozdział 8](#8-filmy-film-na-wierzchu-obraz-w-obrazie-pobieranie) |
| Tryb czytania i streszczenie | Zawsze | Czysty tekst artykułu |
| Czytaj stronę na głos (Ctrl+Shift+U) | Zawsze | Czyta główną treść |
| 🔊 Czytaj od tego miejsca | Gdy nic nie jest zaznaczone | Czyta od zdania, które kliknąłeś |
| 🧠 Gdzie ja to czytałem? | Zawsze | Szukanie w treści przeczytanych stron |
| Zrzut ekranu | Zawsze | Widoczna część albo cała strona |
| Narzędzia Velivo | Zawsze | Prywatność, media, pobrane, historia, dodatki, diagnostyka sieci, profile, obraz w obrazie, szukanie w kartach |
| Dodaj do Szybkiego Dostępu | Zawsze | Skrót do wybranej grupy |

### Na karcie

Kliknij prawym przyciskiem na karcie.

- Odśwież, Duplikuj kartę
- Przypnij albo odepnij kartę
- Wycisz kartę albo włącz dźwięk
- Odświeżaj automatycznie: co 1, 5, 15 albo 30 minut
- 📺 Wyślij do… (inny komputer z Velivo w domu)
- Dodaj do grupy, Zestawy kart, Szukaj w kartach
- Zamknij kartę, inne karty albo karty po prawej (przypięte są pomijane)
- Przywróć zamkniętą kartę (Ctrl+Shift+T)

W nowoczesnym wyglądzie menu mają ikony Windows 11. W kolorowym wyglądzie mają emoji.

---

## 6. Obsługa samą myszką: gesty i kółko

### Gesty myszy

Przytrzymaj **prawy przycisk**, przesuń myszkę o ok. 3 cm i puść:

| Ruch | Działanie |
| --- | --- |
| ← w lewo | Wstecz |
| → w prawo | Dalej |
| ↑ w górę | Nowa karta |
| ↓ w dół | Zamknij kartę |
| ↓ potem → | Odśwież |

Zwykły prawy klik bez ruchu otwiera menu jak zawsze. Gesty wyłączysz w Ustawienia → Wyszukiwanie i start.

### Kółko myszy na przyciskach

| Gdzie | Działanie |
| --- | --- |
| Przycisk powiększenia (np. 125%) | Powiększa i pomniejsza stronę, zapamiętuje dla strony |
| Przycisk trybu 🌙, gdy włączony jest tryb nocny | Natężenie ocieplenia 5–100% |
| Okienko „Film na wierzchu” | Przezroczystość 15–100% |

### Klik myszką na linkach

- **Zwykły klik** otwiera w tej samej karcie (działają Wstecz i Dalej). Można to zmienić w ustawieniach.
- **Środkowy przycisk** albo **Ctrl+klik** zawsze otwiera nową kartę.

---

## 7. Karty

- **Przypinanie:** prawy przycisk na karcie → Przypnij kartę.
  - karta przechodzi na początek paska, z pinezką, bez krzyżyka;
  - jest „zamrożona” na swoim adresie: link do innej strony otwiera się w nowej karcie;
  - wraca po każdym uruchomieniu i synchronizuje się z innymi komputerami.
- **Grupy kart:** prawy przycisk → Dodaj do grupy → Nowa grupa….
  - karty grupy mają kolorową kropkę, a przed nimi stoi kolorowa etykieta;
  - kliknięcie etykiety zwija grupę do jednej etykiety z liczbą kart;
  - prawy przycisk na etykiecie: nazwa, kolor, rozgrupowanie, zamknięcie całej grupy.
- **Zestawy kart:** prawy przycisk → Zestawy kart → Zapisz otwarte karty jako zestaw…. Potem z tego samego menu otwierasz cały zestaw, zastępujesz go albo usuwasz.
- **Wyciszanie:** na karcie, która gra, pojawia się 🔊. Kliknięcie wycisza (🔇).
- **Automatyczne odświeżanie:** prawy przycisk → Odświeżaj automatycznie. Karta ma wtedy znaczek ⟳.
- **Szukanie w kartach:** Ctrl+Shift+A, wpisz kilka liter, Enter przełącza.
- **Karty prywatne (🕶):** nic nie zapisują, nie synchronizują się i nie trafiają do historii.
- **Po ponownym uruchomieniu** karty, grupy i przypięte wracają. Wyłączysz to w ustawieniach.

---

## 8. Filmy: Film na wierzchu, obraz w obrazie, pobieranie

Po najechaniu myszką na film, w jego prawym górnym rogu, pojawiają się trzy przyciski: **⬇ Pobierz · ▣ Na wierzchu · ⧉ Obraz w obrazie**. Działa to także na YouTube.

### ▣ Film na wierzchu (wyłącznie Velivo)

1. Kliknij **▣ Na wierzchu**. Film w karcie się zatrzyma, a w małym okienku ruszy od tego samego miejsca.
2. Okienko obsługujesz tak:
   - **przesuwasz** za ciemny pasek u góry;
   - **zmieniasz wielkość** za krawędź albo róg, od 160×90 pikseli;
   - **przezroczystość** zmieniasz kółkiem myszy nad okienkiem, od 15% do 100%, a na pasku widać „Widoczność 60%”;
   - **📌 przypinka:** niebieska oznacza zawsze na wierzchu, biała – zwykłe okno, które chowa się pod inne;
   - **kliknięcie na film** to pauza albo wznowienie;
   - **↩** wraca do strony w Velivo od tego samego miejsca, nawet gdy przeglądarka była zamknięta;
   - **✕** zamyka okienko.
3. Okienko **gra dalej po zamknięciu karty, a nawet całego Velivo.** Kliknięcie ikony Velivo uruchamia przeglądarkę od nowa.
4. Miejsce, wielkość, przezroczystość i przypinka są zapamiętywane.

**Czysty widok filmu.** Okienko pokazuje **sam film** na całą swoją powierzchnię – bez reszty strony: bez menu, komentarzy, podpowiedzi, okienek i banerów. Na YouTube wygląda to jak osobny odtwarzacz. Blokada reklam Velivo działa jak w karcie.

**Do czego to się przydaje:**
- muzyka lub podcast w małym, półprzezroczystym okienku w rogu ekranu, podczas pracy w innych programach;
- mecz albo transmisja na żywo nad dokumentem lub arkuszem – ustaw widoczność 40–60%, a tekst pod spodem zostaje czytelny;
- poradnik wideo obok programu, w którym wykonujesz kroki;
- film gra nawet po zamknięciu Velivo, więc przeglądarka nie zajmuje pamięci.

**Obsługa samą myszką (podsumowanie):**

| Co | Jak |
| --- | --- |
| Pauza / wznowienie | klik na film |
| Przezroczystość 15–100% | kółko myszy nad okienkiem |
| Przesuwanie | ciemny pasek u góry |
| Wielkość (od 160×90) | krawędź lub róg |
| Zawsze na wierzchu | 📌 (niebieska = włączone) |
| Powrót do strony w tym samym miejscu | ↩ |
| Zamknięcie | ✕ |

Przezroczystość wymaga Windows 10 w wersji 1809 lub nowszej, bo okienko używa składnika Windows do rysowania obrazu.

### ⧉ Obraz w obrazie

Standardowe okienko silnika przeglądarki. Gra dalej po zamknięciu karty, aż zamkniesz okienko. Jego minimalną i maksymalną wielkość narzuca silnik Chromium, a nie Velivo.

### ⬇ Pobieranie filmów

1. Kliknij **⬇ Pobierz** albo prawy przycisk na filmie → **⬇ Pobierz film…**.
2. Wybierz jakość:
   - **najlepsza jakość wideo** (do 1080p, MP4);
   - **szybko** – wideo w jednym pliku, zwykle 360p–720p;
   - **tylko dźwięk M4A**;
   - **tylko dźwięk MP3**.
3. Wybierz folder (**Zmień folder…**). Velivo zapamięta ostatni.
4. Postęp, prędkość i pozostały czas widać w oknie Pobrane.

**Czym pobiera:**
- **Zwykłe pliki wideo:** menedżer Velivo, do 16 połączeń naraz.
- **YouTube, strumienie i ponad 1000 serwisów:** darmowe narzędzie **yt-dlp**:
  - pobiera się raz, za Twoją zgodą (ok. 18 MB);
  - samo aktualizuje się co 2 tygodnie;
  - pobiera po 8 kawałków naraz.
- **Najlepsza jakość i MP3** wymagają darmowego **FFmpeg** (jednorazowo ok. 140 MB, za zgodą).

**Ograniczenia:**
- Netflix, Disney+ i inne serwisy z zabezpieczeniem DRM nie są obsługiwane.
- Pobieraj tylko materiały, do których masz prawo, na przykład na własny użytek.

---

## 9. Prywatność i bezpieczeństwo

| Funkcja | Opis |
| --- | --- |
| **Blokowanie reklam i trackerów** | EasyList, EasyPrivacy i lista polska, ok. 95 tys. reguł. Działa przed pobraniem, więc strony są lżejsze |
| **Ręczne blokowanie elementów** | Prawy przycisk → 🚫 Blokuj element. Kółko myszy powiększa obszar, klik blokuje, Esc anuluje |
| **🍪 Banery ciasteczek (RODO)** | Velivo samo klika „Odrzuć” albo „Tylko niezbędne”. **Nigdy nie klika „Akceptuj”.** Gdy baner nie ma przycisku odrzucenia, nic nie jest klikane |
| **Ochrona przed śledzeniem** | Zrównoważona (domyślna) albo ścisła. Na zaufanych stronach ścisła działa jak zrównoważona, chyba że zaznaczysz „Wymuś blokowanie trackerów” |
| **Reguły dla domen** (przycisk Prywatność) | Blokada JavaScriptu, ciasteczek, wymuszone blokowanie trackerów, automatyczne czyszczenie danych, zaufana domena |
| **🛡 Tarcza** | Licznik i lista wszystkiego, co zablokowano: AdBlock, reguły, JavaScript, ręcznie ukryte elementy, banery ciasteczek |
| **🧾 Paragon prywatności** | Na górze okna tarczy: ile zewnętrznych firm, w ilu krajach, brokerzy danych, próby rozpoznania komputera (canvas, karta graficzna, dźwięk) i najczęstsze firmy |
| **„Nie śledź”** | Wysyła sygnały DNT i Global Privacy Control |
| **SmartScreen** | Ochrona Microsoft przed groźnymi stronami i plikami, pomijana na zaufanych domenach |
| **🛡 Wykrywanie podróbek** (bez internetu) | Przed otwarciem strony sprawdza, czy adres nie udaje banku, sklepu lub portalu: marka na obcej domenie (`paypal-secure-login.com`, `ebay.co.uk.konto.top`), literówki i podmienione znaki (`paypa1.com`, `rnicrosoft.com`, `amaz0n.com`), adresy `xn--` z literami z innych alfabetów i podróbki stron, do których masz zapisane hasła. Pokazuje ostrzeżenie z domyślną odpowiedzią „Nie”. Jeśli to prawdziwa strona, wybierz „Tak” – Velivo zapamięta ją |
| **🔒 Zawsze HTTPS** | Każde połączenie najpierw szyfrowane. Strona bez szyfrowania otwiera się dopiero po ostrzeżeniu, żeby nie wpisywać tam haseł ani karty |
| **💳 Bezpieczne płatności** | Na stronach banków i płatności (PayPal, Monzo, Revolut, Barclays, HSBC, Lloyds, NatWest, Santander, PKO, mBank, Stripe i inne) okno Velivo jest niewidoczne dla programów nagrywających ekran. Pojawia się komunikat 🛡. Na tych stronach nie zrobisz zrzutu ekranu |
| **🔐 Hasła i formularze odporne na ataki** | Hasło tylko dla prawdziwej domeny; pomocnik Velivo niepodmienialny przez stronę; działają tylko prawdziwe kliknięcia myszą; ukryte pola nie są wypełniane; przed wpisaniem karty lub konta bankowego Velivo pyta; kod CVC nigdy nie jest zapisywany |
| **Karty prywatne** | Osobne, izolowane dane, które znikają po zamknięciu. Hasła i formularze działają, a zapis – tylko po Twojej zgodzie |
| **Czyszczenie danych** | Przy zamknięciu albo ręcznie w ustawieniach |

Wszystkie przełączniki są w Ustawieniach → „Bezpieczeństwo i pobieranie”. Testy ataków są w repozytorium w `testy/bezpieczenstwo/`.

**Uczciwie:** Velivo chroni w przeglądarce. Wirusy i programy szpiegujące w samym systemie wykrywa antywirus (np. Windows Defender lub Bitdefender) – zostaw go włączonego.

---

## 10. Zakupy: wykrywacz sztuczek presji

Gdy sklep próbuje Cię popędzić, obok tarczy zapala się żółty przycisk **⚠ liczba**. Po kliknięciu widzisz listę:

- **⏱ licznik czasu.** Jeśli po odświeżeniu strony liczy od nowa, Velivo oznacza go jako **fałszywy**;
- **📦 „tylko 2 sztuki”, „zostało 3 miejsca”;**
- **👥 „15 osób ogląda teraz”, „ktoś właśnie kupił”;**
- **☑ zaznaczone z góry ubezpieczenie, gwarancja albo newsletter w koszyku.** Velivo je **odznacza**;
- **💸 opłata serwisowa, manipulacyjna albo rezerwacyjna,** która pojawia się dopiero przy zamówieniu.

Wykrywacz czyta teksty i zachowanie strony, więc nie wyłapie każdego sklepu i rzadko może się pomylić.

---

## 11. Czytanie: na głos, tryb czytania, „Gdzie ja to czytałem?”

### Czytanie na głos

- **🔊 na pasku** albo Ctrl+Shift+U czyta główną treść strony. Czytany akapit jest podświetlony i przewijany na środek ekranu.
- **Prawy przycisk → 🔊 Czytaj od tego miejsca** czyta od klikniętego zdania. Klik w inne miejsce przeskakuje tam.
- **Zaznacz tekst → Czytaj zaznaczenie na głos** czyta tylko fragment.
- Podczas czytania na pasku są pauza, stop i prędkość (0,75×–2×), a w ustawieniach jest głośność.
- **Głosy:**
  - polskie i angielskie głosy Windows;
  - naturalne głosy offline Piper: Gosia, Darkman, MC Speech, Lessac, Ryan, Alba. Każdy pobiera się raz, ok. 60 MB.
- **Pomijane przy czytaniu:**
  - menu, stopki i reklamy;
  - bloki „Czytaj także”, „Polecamy” i listy linków;
  - komentarze, newslettery i podpisy zdjęć.

### Tryb czytania (przycisk „Czytnik”)

- Czysty tekst artykułu w czytelnej czcionce, bez reklam i rozpraszaczy.
- **„Najważniejsze zdania”** – automatyczne streszczenie liczone lokalnie, **bez AI i bez chmury**. Velivo wybiera zdania z najważniejszymi słowami artykułu i tytułu, z premią za początek tekstu.
- Przyciski „Czytaj podsumowanie” i „Czytaj całość”. Kliknięcie w tekst czyta od tego miejsca.

### 🧠 „Gdzie ja to czytałem?” (Ctrl+Shift+F)

- Velivo zapamiętuje tekst przeczytanych stron, **tylko na tym komputerze**.
- Wpisujesz słowa, które pamiętasz z treści, na przykład „bateria laptop 6 godzin”. Wielkość liter i polskie znaki nie mają znaczenia.
- Wyniki pokazują tytuł, stronę, datę i fragment z szukanymi słowami. Dwuklik otwiera stronę.
- **Pomijane:** karty prywatne, banki, płatności, poczta, logowania, gov.pl, ZUS i każda strona z polem hasła.
- **Gdzie to otworzyć:** prawy przycisk na stronie, przycisk w oknie Historii albo Narzędzia Velivo.
- Wyczyszczenie historii czyści także tę pamięć. Funkcję wyłączysz w ustawieniach.

---

## 12. Wyszukiwanie i pasek adresu

- **Wpisz słowa** zamiast adresu, a Velivo wyszuka je w wybranej wyszukiwarce (Ustawienia → Wyszukiwanie i start).
- **Skróty wyszukiwania:** wpisz skrót, spację i hasło:

| Wpisz | Szuka w |
| --- | --- |
| `yt koty` | YouTube |
| `g przepis` | Google |
| `ddg pogoda` | DuckDuckGo |
| `wiki Kraków` | Wikipedia (PL) |
| `allegro rower` | Allegro |
| `olx kanapa` | OLX |
| `ceneo telewizor` | Ceneo |
| `mapy Gdańsk` | Mapy Google |
| `filmweb Matrix` | Filmweb |
| `tlumacz hello` | Tłumacz Google |

  Własne skróty dodasz w Ustawienia → Wyszukiwanie i start → **Skróty wyszukiwania…**, na przykład `skrót=adres z %s`.
- **Prawy przycisk na pasku adresu → Wklej i przejdź** wkleja i od razu otwiera.

---

## 13. Wygląd: style, motywy, tryb ciemny i nocny, powiększenie

- **Styl** (Ustawienia → Wygląd):
  - **Nowoczesny** (domyślny): spokojne przyciski bez kolorowych teł, ikony Windows 11, jeden niebieski akcent, a kolor tylko przy najechaniu albo gdy coś jest włączone;
  - **Kolorowy**: kolorowe przyciski i emoji.
- **10 motywów:** Jasny, Grafit, Granat, Nocny fiolet, Las, Ocean, Zachód słońca, Czerń (OLED), Papier, Mgła.
- **Tryby stron** (przycisk 🌙):
  - jasny;
  - **ciemny** – strony przyciemnione, a zdjęcia zostają w prawdziwych kolorach;
  - **nocny** – ciepłe kolory i mniej niebieskiego światła, **natężenie kółkiem myszy**.
- **Powiększenie** zapamiętywane dla każdej strony, zmieniane kółkiem na przycisku procentów albo Ctrl+kółkiem.
- **Wersja telefonu** dla wybranej strony (prawy przycisk), zapamiętywana.
- **Język:** polski albo angielski (Ustawienia → Wygląd → Język).

---

## 14. Szybki Dostęp – strona nowej karty

- Skróty ułożone w grupach, na przykład Start, Finanse, Muzyka, Gry.
- Ikony stron i **miniatury stron** robione w tle. Ikony nie migają przy przełączaniu grup.
- **Profile Szybkiego Dostępu**, opcjonalnie z PIN-em, oraz **motywy tła**.
- Dodawanie: przycisk **+ Skrót**, prawy przycisk na stronie → „Dodaj do Szybkiego Dostępu” albo import zakładek.
- Synchronizacja z innymi komputerami w domu: skróty i grupy są łączone.

---

## 15. Hasła i Sejf

- **Sejf** (osobny program autora) przechowuje hasła zaszyfrowane offline. Gdy strona ma pole logowania, na pasku pojawia się **🔑**, a kliknięcie wypełnia formularz.
- **Zapis do Sejfu:** po zalogowaniu Velivo pyta, czy zapisać albo zaktualizować hasło.
- **Własna zaszyfrowana baza Velivo** (Menedżer haseł lokalnych) z **generatorem silnych haseł**.
- **Import i eksport CSV**, na przykład z KeePassXC albo Chrome.
- **Import z Chrome/Edge/Brave/Opery/Vivaldi** (Ustawienia → „Importuj z Chrome/Edge/Brave…”): zakładki Velivo odczytuje samo, z każdego profilu. Hasła przenosisz przez plik CSV: przycisk otwiera stronę eksportu haseł w tamtej przeglądarce, potem „Wczytaj plik CSV…”, a na koniec usuń plik CSV.
- **Ważne:** zrób import, **zanim** odinstalujesz starą przeglądarkę. Po jej usunięciu zakładek nie da się odczytać, a haseł wyeksportować. Hasła z konta Google można też pobrać później ze strony passwords.google.com. Logowania do stron się nie przenoszą – zaloguj się raz w Velivo.
- **Przyciski przy polu logowania:** obok pola, po prawej, pojawia się plakietka **🔑 Wpisz · ⚡ Generuj**:
  - **🔑 Wpisz** pokazuje listę kont tej strony (nazwa i e-mail); klik wpisuje login i hasło. Na końcu listy jest **🔎 Inne konto z bazy Velivo…** – okno z wyszukiwarką całej bazy (np. logowanie kontem PreSonus na stronie Fendera). Okno pokazuje adres strony i ostrzega przed podróbkami;
  - **⚡ Generuj** wpisuje silne, losowe hasło.
- **Pytanie o zapis:** po kliknięciu „Zaloguj”, Enterze lub wysłaniu formularza Velivo pyta, czy zapisać lub zaktualizować hasło. Działa też w kartach prywatnych.
- **Menedżer haseł:** wyszukiwarka, kopiowanie loginu i hasła, edycja, eksport. Wpisy z KeePassXC i telefonu bez adresu dostają domenę z tytułu lub linku aplikacji (`android://…`), a dwuklik otwiera stronę i wypełnia formularz.
- **Autouzupełnianie** (lokalna, szyfrowana baza): **adres** (imię, nazwisko, ulica, kod, miasto, telefon, e-mail), **karta** (bez CVC) i **konto bankowe** (IBAN, numer konta, sort code). Klik w puste pole wypełnia cały formularz – na każdej stronie https. Kartę i konto Velivo wpisuje dopiero po potwierdzeniu adresu strony.

---

## 16. Synchronizacja w sieci domowej (bez chmury)

### Parowanie (raz)

1. Zainstaluj tę samą wersję Velivo na obu komputerach.
2. Ustaw na obu **ten sam profil** (Ustawienia → Profile użytkownika).
3. Zaznacz „Włącz synchronizację między uruchomionymi Velivo”. Zapora Windows musi przepuszczać Velivo (port 41919).
4. Velivo zapyta: „Połączyć oba komputery…?” Wybierz **Tak** albo kliknij „Sparuj urządzenie w sieci…”.
5. Porównaj krótki kod na obu ekranach i potwierdź.
6. Wybierz, czyje ustawienia zachować.

Od tej chwili wszystko idzie **zaszyfrowane, przez Twoją sieć, bez internetu**.

### Co się synchronizuje

| Dane | Jak |
| --- | --- |
| Zakładki | Suma z obu; usunięcie działa na obu |
| Hasła Velivo | Suma; przy tym samym koncie wygrywa nowsze |
| Historia (ostatnie 60 dni) | Suma; usunięcie wpisu, dnia albo całości działa na obu |
| Karty przypięte | Wygrywa ostatnia zmiana |
| Szybki Dostęp | Skróty i grupy łączone |
| Ustawienia, prywatność, profile, dodatki | Wygrywa ostatnia zmiana; świeża instalacja nie nadpisuje danych |
| Język, foldery, okienko filmu | **Nie** – każdy komputer ma swoje |
| Pozostałe otwarte karty | **Nie** – zamiast tego prawy przycisk na karcie → 📺 Wyślij do… |

### 📺 Wysyłanie kart

Prawy przycisk na karcie → **📺 Wyślij do… → nazwa komputera**. Na drugim komputerze strona otwiera się w nowej karcie, a okno wychodzi na wierzch. Film rusza od tego samego miejsca.

Stan połączenia pokazuje Ustawienia → **Panel diagnostyczny LAN…**.

---

## 17. Historia i pobrane pliki

### Historia (Ctrl+H)

- Ułożona **dzień → witryna → podstrony**, na przykład „Wczoraj · 180 stron · 12 witryn”, a w nim „polsatnews.pl · 45 stron”.
- Powtórzone wizyty są pokazane raz, z dopiskiem ×3. Wpisy Szybkiego Dostępu są ukryte.
- **Prawy przycisk:** otwórz, otwórz w nowej karcie, otwórz wszystkie strony witryny, usuń wpis, witrynę albo dzień.
- Wyszukiwanie po tytułach i adresach oraz przycisk **🧠 Szukaj w treści stron…**.

### Pobrane (Ctrl+J)

- **Menedżer Velivo:** do 16 połączeń, pauza i **wznawianie po ponownym uruchomieniu**, ponawianie po zerwaniu połączenia.
- **Historia pobranych zostaje po ponownym uruchomieniu.** Przy każdym pliku widać rozmiar, datę i godzinę oraz stronę, z której pochodzi.
- Usunięty plik ma szary napis „Plik usunięty lub przeniesiony”.
- Przyciski: Otwórz, 📁 Pokaż w folderze, Usuń z listy, Wyczyść zakończone, Media na stronie.

---

## 18. Dodatki, profile i narzędzia

- **Dodatki Chrome:** instalacja z Chrome Web Store, z linku albo rozpakowanego folderu.
  - przycisk 🧩 pokazuje listę dodatków z pinezką: przypięte mają ikonkę na pasku, odpięte działają dalej, tylko bez ikonki;
  - prawy przycisk na ikonce dodatku → Odepnij z paska.
- **Profile użytkowników:** osobne dane dla osoby albo celu, na przykład praca czy prywatny.
- **Zrzuty ekranu:** widoczna część albo cała strona z przewijaniem.
- **Tłumaczenie:** całej strony albo zaznaczenia (Tłumacz Google).
- **Wykrywanie mediów do pobrania** na stronie (Narzędzia Velivo).

### Pamięć podręczna na RAM dysku

Jeśli masz RAM dysk (na przykład ImDisk, SoftPerfect RAM Disk albo własne narzędzie), Velivo może trzymać na nim pamięć podręczną stron, czyli cache. **Nie potrzebujesz do tego żadnych zewnętrznych narzędzi**, dowiązań ani przenoszenia folderów, jak przy Chrome. Wystarczy wskazać folder w ustawieniach Velivo.

1. Ustawienia → **Folder na śmieci (puste = w profilu przeglądarki)** → **Wybierz…** i wskaż folder na RAM dysku, na przykład `R:\`.
2. Zapisz ustawienia i uruchom Velivo ponownie.
3. Velivo utworzy tam podfolder `Velivo-smieci` i przy porządkach czyści **tylko jego zawartość**, nigdy nic innego z RAM dysku.
4. Opcja „Usuwaj śmieci przy każdym uruchomieniu przeglądarki” czyści go przy każdym starcie.

**Co to daje:**
- strony z pamięci podręcznej wczytują się z pamięci RAM, czyli szybciej niż z dysku;
- dysk SSD dostaje mniej zapisów;
- po wyłączeniu komputera cache znika sam, co jest dodatkowym plusem dla prywatności.

**Czego nie obejmuje:** pamięć podręczną grafiki (`GPUCache`) silnik Edge zawsze trzyma w profilu przeglądarki. To wymóg silnika, nie wybór Velivo. Przycisk **Domyślny** w ustawieniach przywraca zwykłą lokalizację.

---

## 19. Skróty klawiszowe

| Skrót | Działanie |
| --- | --- |
| Ctrl+T | Nowa karta |
| Ctrl+Shift+N | Nowa karta prywatna |
| Ctrl+W | Zamknij kartę |
| Ctrl+Shift+T | Przywróć zamkniętą kartę |
| Ctrl+Tab / Ctrl+Shift+Tab | Następna / poprzednia karta |
| Ctrl+Shift+A | Szukaj w kartach |
| Ctrl+Shift+F | 🧠 Gdzie ja to czytałem? |
| Ctrl+L | Pasek adresu |
| Ctrl+D | Zakładka |
| Ctrl+H | Historia |
| Ctrl+J | Pobrane |
| Ctrl+Shift+U | Czytaj stronę na głos / pauza |
| F5 | Odśwież |
| F11 | Pełny ekran |
| Alt+← / Alt+→ | Wstecz / dalej |
| Ctrl + kółko myszy | Powiększenie |

Każdą z tych rzeczy zrobisz też samą myszką: przyciskiem, gestem albo prawym przyciskiem.

---

## 20. Ustawienia – co gdzie jest

| Sekcja | Najważniejsze opcje |
| --- | --- |
| **Wygląd** | Styl (Nowoczesny / Kolorowy), motyw, język, tryb ciemny i nocny, domyślne powiększenie, kompaktowy pasek |
| **Karty** | Przywracanie kart po uruchomieniu, linki w tej samej karcie |
| **Wyszukiwanie i start** | Wyszukiwarka, strona startowa, skróty wyszukiwania, gesty myszy, przyciski „Obraz w obrazie” i „Pobierz” nad filmami, Szybki Dostęp jako nowa karta |
| **Prywatność** | „Nie śledź”, ochrona przed śledzeniem, historia, czyszczenie przy zamknięciu, Sejf, hasła, autouzupełnianie, wyskakujące okna, banery ciasteczek, pamięć treści stron, wykrywacz sztuczek, paragon prywatności |
| **Czytanie** | Głos, prędkość, głośność, naturalne głosy offline |
| **Pobieranie** | Pytanie o miejsce zapisu, liczba połączeń (1–16) |
| **Synchronizacja** | Włączenie LAN, parowanie, panel diagnostyczny |
| **Profile** | Profile użytkowników |
| **Dane** | Czyszczenie danych, folder na śmieci, czyli cache (także na RAM dysku – patrz rozdział 18) |

---

## 21. Rozwiązywanie problemów i miejsce danych

| Problem | Co zrobić |
| --- | --- |
| Strona coś blokuje, a nie powinna | Przycisk Prywatność → **Dodaj do zaufanych** albo prawy przycisk → Przywróć zablokowane elementy |
| Baner ciasteczek dalej jest widoczny | Ten baner nie ma przycisku „Odrzuć”. Velivo nigdy nie klika „Akceptuj” |
| Komputery się nie widzą w sieci | Ten sam profil na obu, zapora przepuszcza Velivo, automatyczny czas Windows na obu (różnica ponad 5 minut blokuje wymianę) |
| Pobieranie z YouTube nie działa | Odczekaj chwilę i spróbuj ponownie. yt-dlp samo się aktualizuje, a YouTube czasem coś zmienia |
| Brak ikon w Szybkim Dostępie | Otwórz nową kartę i odczekaj kilka minut; menu „⋮” → Uzupełnij brakujące ikony |
| Głos offline nie czyta | Usuń folder `Piper` (poniżej). Pobierze się ponownie |
| Antywirus blokuje instalator | Plik nie ma płatnego podpisu cyfrowego – dodaj wyjątek |
| „Wystąpił nieoczekiwany błąd” | Velivo działa dalej. Szczegóły są w pliku `bledy.log` – przyślij go przy zgłoszeniu |

### Gdzie leżą dane

| Co | Gdzie |
| --- | --- |
| Program | `%LOCALAPPDATA%\Programs\Velivo` |
| Ustawienia, zakładki, hasła, historia, pamięć stron | `%LOCALAPPDATA%\Przegladarka` (inny profil: `Profiles\<nazwa>`) |
| Głosy offline | `%LOCALAPPDATA%\Przegladarka\Piper` |
| yt-dlp i FFmpeg | `%LOCALAPPDATA%\Przegladarka\narzedzia` |
| Dziennik błędów | `bledy.log` w folderze danych profilu |

Wszystkie dane są tylko na Twoim komputerze. Velivo nie ma serwera ani konta i niczego o Tobie nie wysyła.
