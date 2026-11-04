# Velivo – API serwerów

Stan: 2026-11-04. Kod: `src/KomunikatorApi.cs`. Na serwerach jeszcze nie sprawdzone (tylko kompilacja).

## 1. API HTTP (dla programów na tym komputerze)

- Adres: `http://127.0.0.1:41930/api` – tylko ten komputer (z sieci i internetu niedostępne).
- Startuje razem z Velivo (każdym; konta i polecenia innym serwerom – gdy ten komputer jest serwerem).
- Klucz API: plik `api-klucz.txt` w folderze danych Velivo. Każde zapytanie: nagłówek `Authorization: Bearer <klucz>` albo `?klucz=<klucz>`.
- Zamiast klucza API można zalogować się **kluczem sprzętowym administratora** (te same klucze co w panelu) – punkt 1.2.
- Odpowiedzi: JSON (UTF-8). Błąd: `{"blad": "…"}` z kodem 400 / 401 / 404.

### 1.1 Zapytania

| Zapytanie | Co robi |
|---|---|
| `GET /api` | lista zapytań |
| `GET /api/stan` | stan tego serwera (wersja, port, adres, router, połączeni, skrzynka, odciski) |
| `GET /api/serwery` | wszystkie moje serwery: id, ostatnia wieść, `dziala` (wieść < 3 min), stan |
| `GET /api/polecenia` | czynności administratora (`op` + opis) |
| `GET /api/polecenie?serwer=<id\|*>&op=<op>[&arg=…]` | polecenie dla jednego albo wszystkich serwerów – ta sama droga co panel (podpisane kluczem serwera, `p2p-admcmd`, dziennik `adm.log`); wynik: `/api/dziennik` |
| `GET /api/dziennik` | ostatnie 200 wpisów dziennika operacji administratora |
| `GET /api/konta` | konta (id, imię, numer, założone, w sieci) – bez PIN-u |
| `GET /api/konto/usun?id=<id>` | usuwa konto, skrzynkę i osobę na wszystkich moich serwerach (jak panel) – zapis w dzienniku |
| `GET /api/klucze` | klucze sprzętowe administratora (nazwa, id) |

Czynności (`op`): restart, on, off, ports, firewall, flush, sync, syncoff, syncon, diagclr, logout, repair.

### 1.2 Logowanie kluczem sprzętowym (FIDO2 / WebAuthn)

1. `GET /api/wyzwanie` → `challenge`, `rpId` (`velivo.local`), `allowCredentials` (id kluczy, base64url). Ważne 2 min, jednorazowe.
2. Klient FIDO2 (np. python-fido2, libfido2) robi „get assertion” dla `rpId` = `velivo.local` z tym wyzwaniem – dotknięcie klucza.
3. `GET /api/zaloguj?id=<credentialId>&authData=<…>&clientData=<clientDataJSON>&sig=<podpis DER>` (wszystko base64url) → `token`.
4. Token jak klucz API (`Authorization: Bearer <token>`), wygasa po 15 min bez zapytań.

Serwer sprawdza: znany klucz, wyzwanie (raz), typ `webauthn.get`, skrót rpId, dotknięcie (flaga UP), podpis ECDSA P-256. Każde logowanie (udane i odrzucone) – w `adm.log`.
Uwaga: przeglądarka nie pozwoli strony z innego adresu użyć rpId `velivo.local` – potrzebny klient FIDO2 (program), nie strona WWW.

Przykład (PowerShell):
```
$k = (Get-Content "$env:LOCALAPPDATA\Programs\Velivo\Dane\api-klucz.txt").Trim()   # starsze instalacje: $env:LOCALAPPDATA\Przegladarka
Invoke-RestMethod http://127.0.0.1:41930/api/serwery -Headers @{Authorization="Bearer $k"}
```

## 2. Protokół między serwerami i urządzeniami (pakiety `p2p-…`)

Porty: 41918/41919 UDP (sieć domowa), 41920–41929 i 443 TCP (przez internet, ramki). Wszystko szyfrowane/podpisane – bez kluczy pary i certyfikatu infrastruktury nic się nie przyjmie.

| Pakiet | Plik | Kto → kto | Co |
|---|---|---|---|
| `p2p-here`, `p2p-who`, `p2p-bye` | Komunikator.cs | urządzenia | obecność |
| `p2p-msg` / `p2p-ack` | Komunikator.cs | urządzenie → urządzenie (przez serwer) | wiadomość (AES-GCM kluczem pary) / potwierdzenie (HMAC); serwer może pisać w imieniu innego mojego serwera (`alias`) |
| `p2p-file`, `p2p-box` | Komunikator.cs | urządzenie → urządzenie / skrzynka | plik (tunel), plik do skrzynki serwera |
| `p2p-boxdone` | KomunikatorPosrednik.cs | serwer → moje serwery | doręczone – kasuj kopie |
| `p2p-srv` | KomunikatorPosrednik.cs | serwer ↔ serwer (internet) | koperta łącza serwerów (te same pakiety co w domu) |
| `p2p-srvid` | KomunikatorKlaster.cs | serwer ↔ serwer | dowód członkostwa (certyfikat + podpis) |
| `p2p-srvinfo`, `p2p-srvbye` | KomunikatorPosrednik.cs | serwer → moje serwery | stan co minutę / wyłączam się |
| `p2p-evs`, `p2p-evhave` | KomunikatorZdarzenia.cs | serwer ↔ serwer | dziennik zdarzeń (przyjęte / doręczone), dosyłanie braków |
| `p2p-fpreq`, `p2p-fprep` | KomunikatorPosrednik.cs | serwer ↔ serwer | porównanie skrzynek (diagnostyka) |
| `p2p-admcmd` / `p2p-admack` | KomunikatorKlaster.cs | panel → serwer / wynik | polecenia administratora |
| karty kontaktów (`rk`/`rv`) | Komunikator.cs, KomunikatorPosrednik.cs | serwer → moje serwery | kontakty i klucze z nimi – od razu przy zmianie (`KomInfraSync`) |
