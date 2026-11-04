# Velivo – moje serwery i historia rozmów (architektura)

Stan: 2026-11-04. Część A zrobiona w kodzie (kompiluje się, testy klasy przechodzą; na prawdziwych serwerach niesprawdzona). Części B i C – projekt do wdrożenia.

## A. Prywatna infrastruktura serwerów (zrobione)

Pliki: `src/KomunikatorKlaster.cs` (klasa `KomCluster` + obsługa w `MainWindow`), `src/KomunikatorPosrednik.cs` (łącze serwerów), testy `testy/Velivo.Tests/KlasterTests.cs`.

| Element | Jak |
|---|---|
| Tożsamość serwera | własny klucz ECDSA P-256 (`srvKey`), identyfikator = skrót klucza publicznego; osobno od klucza komunikatora i od „moje urządzenie” (Dom) |
| Identyfikator infrastruktury | losowy, z kluczem podpisu infrastruktury (ECDSA P-256), znany tylko członkom |
| Certyfikat członkostwa | podpis infrastruktury na {infrastruktura, serwer, klucz serwera, identyfikator w komunikatorze, czas} |
| Weryfikacja przy połączeniu | po uwierzytelnieniu kluczem pary obie strony wysyłają `p2p-srvid`: certyfikat + podpis kluczem serwera na (od, do, infrastruktura); dane serwerów (`p2p-srv`, kopie plików) dopiero po sprawdzeniu |
| Dołączenie | jednorazowe „Czy to Twój serwer?” na **każdym** z dwóch serwerów (osobne pytanie – nie zwykłe zaproszenie). Obie zgody → serwer z mniejszym identyfikatorem infrastruktury wystawia certyfikat i przekazuje infrastrukturę; dotychczasowi członkowie drugiej strony przechodzą razem (nowe certyfikaty) |
| Odwołanie | menu kontaktu serwera → „⛔ Odłącz od moich serwerów”: podpisane odwołanie + **nowy klucz infrastruktury** dla pozostałych (odwołany zna stary klucz, nie wystawi już certyfikatu); odwołany nie wraca przy scalaniu |
| Synchronizacja stanu | co 10 min i po zmianie: suma certyfikatów i odwołań (bez nadpisywania) |
| Migracja | serwery, które już dzieliły wspólny sekret (NUCBOX, Ryzen), dołączają same, bez pytań |
| Serwer znajomego | bez „Tak” po mojej stronie nie dostaje certyfikatu → żadnych danych przez łącze serwerów |

Ograniczenie, które zostaje świadomie: pakiety serwerów w **tej samej sieci domowej** (UDP) są nadal przyjmowane na dotychczasowych zasadach (serwer domowy = moje urządzenie). Zmienię to na certyfikat dopiero, gdy wszystkie serwery będą na nowej wersji – inaczej obecne NUCBOX/Ryzen przestałyby się wymieniać w trakcie aktualizacji.

## B. Historia rozmów na serwerach z szyfrowaniem od końca do końca (projekt)

Hierarchia kluczy (serwery nigdy nie dostają żadnego z nich):

1. **Klucz konta (UK)** – losowy 32 B, jeden dla wszystkich moich urządzeń. Wersjonowany (`uk1`, `uk2`…).
2. **Klucz rozmowy (CK)** – losowy 32 B na rozmowę i wersję UK, zapisany zaszyfrowany UK (AES-256-GCM).
3. **Rekord historii** – każda wiadomość (treść, czas, stan, odnośnik do pliku) szyfrowana CK; identyfikator = `mid` (już istnieje) → idempotencja.
4. **Pliki** – szyfrowane własnym kluczem pliku, klucz pliku w rekordzie wiadomości.

Przechowywanie: rekordy jako zdarzenia `hist` w istniejącym dzienniku zdarzeń serwerów (`KomEventLog`: trwałe numery, „mam do N”, dosyłanie brakujących, bez duplikatów, w domu i przez internet). Serwer widzi tylko: identyfikator konta (skrót), identyfikator rozmowy (skrót z solą), rozmiar, czas. Każdy serwer przechowuje wszystko – brak serwera „głównego”.

Urządzenia:
- **Nowe urządzenie** dostaje UK od dowolnego mojego działającego urządzenia przez istniejący kanał pary (ECDH, już szyfrowany) – jedno potwierdzenie na starym urządzeniu (jak dziś dodawanie urządzenia).
- **Odzyskiwanie po utracie wszystkich urządzeń**: UK zamknięty (a) PIN-em konta (Argon2id/PBKDF2 + sól, ograniczona liczba prób przez serwery), (b) opcjonalnie każdym kluczem sprzętowym (część C). Zamknięte kopie UK leżą na serwerach.
- **Unieważnienie urządzenia**: nowy UK (`uk+1`), nowe CK dla nowych wiadomości; stare rekordy zostają pod starymi kluczami (dostępne dla pozostałych urządzeń, które znają stare UK); unieważnione urządzenie nie dostaje nowych.
- **Rotacja**: ten sam mechanizm co unieważnienie, okresowo albo na żądanie.

Migracja (bez utraty danych):
1. Obecna historia zostaje w dotychczasowych plikach (nic nie jest kasowane).
2. Urządzenie wysyła ją na serwery jako rekordy `hist` (paczki, wznawiane).
3. Weryfikacja: suma kontrolna listy `mid` na urządzeniu = suma z serwerów (po odszyfrowaniu u siebie); porównanie między moimi urządzeniami.
4. Dopiero po zgodności historia z serwerów jest używana do odtworzenia na nowym urządzeniu; stare pliki zostają.

## C. Klucze sprzętowe (projekt, opcjonalne)

Stan w kodzie: Windows już używa FIDO2 **hmac-secret** (kopia komunikatora, Bank) – klucz bez hmac-secret jest odrzucany z komunikatem („Ten klucz nie umie szyfrować”).

| Standard | Użycie w Velivo | Uwagi |
|---|---|---|
| FIDO2 hmac-secret / WebAuthn PRF | zamknięcie UK (odzyskiwanie historii), zatwierdzenie nowego urządzenia | nie każdy klucz FIDO2 ma hmac-secret – sprawdzane przy dodaniu; Windows: webauthn.dll; Android: Credential Manager z PRF (Android 14+, nie każdy klucz/NFC) |
| PIV (np. YubiKey 5) | zamknięcie UK kluczem ECDH P-256 na karcie | wymaga PIN-u karty; Windows przez minidriver/CNG; Android – dopiero przez USB/NFC z biblioteką |
| OpenPGP (YubiKey, Nitrokey) | jak PIV (rozszyfrowanie klucza) | najmniej wygodne – na końcu |
| FIDO U2F bez hmac-secret | tylko potwierdzenie (podpis), bez szyfrowania | nie nadaje się do odzyskiwania |

Zasady: klucz sprzętowy nigdy nie jest wymagany na co dzień; używany tylko przy odzyskiwaniu historii i (opcjonalnie) przy dodawaniu urządzenia; każdy klucz osobno zamyka UK (zgubienie jednego nie blokuje).

## Testy

- `KlasterTests` (4) i `SerweryDziennikTests` (5) – przechodzą (uruchomione na Linuksie z samą klasą).
- Do wykonania na serwerach: dwa serwery (LAN i internet), „Tak” na obu, „Nie”, restart, zerwanie łącza, odłączenie serwera, serwer znajomego bez dostępu.
