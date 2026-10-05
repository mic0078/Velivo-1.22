# Podpis cyfrowy Velivo – jak podpisać prawdziwym certyfikatem

## Stan obecny (wersja 1.22)

Pliki wydania **nie są podpisane**. W repozytorium, sekretach CI i środowisku budowania nie ma
prawdziwego certyfikatu Code Signing. Nie używamy certyfikatów self-signed ani testowych.

## 1. Zdobycie certyfikatu

Potrzebny jest certyfikat **Code Signing** wydany przez zaufany urząd certyfikacji
(np. DigiCert, Sectigo, GlobalSign, SSL.com) albo usługa **Azure Trusted Signing**:
- **OV (Organization/Individual Validation)** – tańszy. Reputacja w SmartScreen buduje się stopniowo.
- **EV (Extended Validation)** – reputacja w SmartScreen od razu. Klucz jest zwykle na tokenie
  sprzętowym lub w chmurze (HSM), więc plik `.pfx` nie wchodzi w grę. Wtedy krok podpisu trzeba
  dostosować do narzędzia dostawcy (np. `signtool` z KSP dostawcy albo Azure Trusted Signing).

Od czerwca 2023 nowe certyfikaty Code Signing muszą mieć klucz na sprzęcie (token lub HSM).
Plik `.pfx` wchodzi w grę tylko przy starszych certyfikatach albo przy dostawcach, którzy go wydają
(np. przez chmurowe HSM z eksportem). Najprostsza ścieżka dla CI to **Azure Trusted Signing**.

## 2. Ustawienie w GitHub (wariant z plikiem .pfx)

Settings → Secrets and variables → Actions → New repository secret:
- `CODESIGN_PFX_BASE64` – zawartość pliku `.pfx` w base64
  (PowerShell: `[Convert]::ToBase64String([IO.File]::ReadAllBytes('cert.pfx')) | Set-Clipboard`),
- `CODESIGN_PFX_PASSWORD` – hasło do pliku `.pfx`.

## 3. Podpisanie

Actions → **Podpisane wydanie (prawdziwy certyfikat Code Signing)** → Run workflow.

Workflow `.github/workflows/podpisane-wydanie.yml`:
1. **Odrzuca** certyfikat, gdy go brak, gdy jest self-signed, wygasł, nie ma przeznaczenia
   Code Signing (EKU 1.3.6.1.5.5.7.3.3) albo jego łańcuch nie jest zaufany w Windows.
2. Buduje Velivo i podpisuje `Velivo.exe` (oba warianty) **przed** spakowaniem do instalatora,
   z SHA-256 i znacznikiem czasu RFC 3161.
3. Buduje instalator i podpisuje `Velivo-Setup-1.22.exe`.
4. Weryfikuje `Get-AuthenticodeSignature` i `signtool verify /pa` dla obu plików. Instaluje też
   program po cichu i sprawdza podpis `Velivo.exe` **po instalacji**, czyli to, że pakowanie nie
   uszkodziło podpisu. Jeśli którykolwiek status jest inny niż `Valid`, workflow kończy się błędem.
5. Udostępnia podpisane pliki jako artefakt `Velivo-1.22-podpisane`.

## 4. Weryfikacja u siebie (Windows)

```powershell
Get-AuthenticodeSignature .\Velivo-Setup-1.22.exe | Format-List *
Get-FileHash .\Velivo-Setup-1.22.exe -Algorithm SHA256
```
Albo: prawy przycisk na pliku → Właściwości → zakładka **Podpisy cyfrowe**.

Workflow **Weryfikacja wydania** (`.github/workflows/weryfikacja-wydania.yml`) sprawdza gotowe pliki
z dowolnego commita: Authenticode, certyfikat, SHA-256, uruchomienie programu oraz cichą instalację
i deinstalację.
