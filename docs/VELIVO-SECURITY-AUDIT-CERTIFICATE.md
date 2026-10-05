# VELIVO — SECURITY AUDIT CERTIFICATE

| | |
|---|---|
| **Certificate No.** | VELIVO-SEC-2026-001 |
| **Product** | Velivo browser for Windows |
| **Version** | 1.22 |
| **Audit date** | 5 October 2026 |
| **Security commit** | `8f3717b` (merge of PR #88 into `main`) |
| **Final installer commit** | `1f08f31` (`Instalator/` built by CI run "Instalator Velivo" #271 from `8f3717b`) |
| **Type** | **Author-issued / internal security audit certificate** |

> **This is NOT an independent, third-party certification.** It was issued by the author of Velivo
> on the basis of an internal source-code review and automated tests run in the project's own CI.
> It does not certify the absence of all vulnerabilities and is not endorsed by any external
> security company, certification body or Microsoft.

---

## 1. Scope of the audit

Manual review of the areas of the source code that handle the network, passwords, files and the
launching of programs: cryptography (Banking Mode, E2E sync, LAN sync, recovery file), LAN sync
and pairing, the TCP transfer of Quick Access data, the page ↔ program message channel
(`chrome.webview`), downloads, tool downloads (yt-dlp, FFmpeg, Piper, uBlock Origin Lite),
archive extraction, process launching and the bills sheet in Banking Mode.
Not every line of the ~22,000-line code base was reviewed.

## 2. Findings and fixes (security commit `8f3717b`)

| # | Severity | Finding | Fix |
|---|---|---|---|
| 1 | Critical | LAN sync is on by default. Before pairing, Velivo broadcast settings, bookmarks, open-tab addresses, privacy rules, profiles and the extension list **in plain text**, and **accepted the same data from any device on the network without authentication** (including the extension list, which triggers installation from the Chrome Web Store). | Before pairing, Velivo only sends an `announce` packet (ID, computer name, profile). It neither sends nor accepts data. Applying synchronised data requires an encrypted and authenticated packet from a paired device. Paired sync is unchanged. |
| 2 | High | Files downloaded by Velivo's own download manager and by yt-dlp did not get the Windows Mark-of-the-Web. | `Zone.Identifier` with `ZoneId=3` (Internet) on completed downloads, without storing the page address. |
| 3 | Medium | yt-dlp, FFmpeg and Piper were downloaded and run without any integrity check. uBlock Origin Lite updates were installed without a hash check. | SHA-256 check before use: Piper against a hash pinned in the code; yt-dlp and FFmpeg against the checksum file of the same release; uBlock Origin Lite against the SHA-256 digest from the GitHub API. A mismatching file is deleted and never run. |
| 4 | Low | The bills sheet accepted data from whatever page was loaded in its tab. | Data is accepted only from the sheet page: the address is checked and a random one-time token is generated for each opening. |
| 5 | Low | The page address was passed to yt-dlp without an argument separator. | `--` before the address. |
| 6 | Info | Passwords and autofill data are protected by Windows DPAPI, so any program running under the same user can read them (same model as Chrome). Banking Mode is protected more strongly. | No change; documented. |

**Unchanged:** the Banking Mode encryption (AES-GCM, PBKDF2-SHA256 600,000 iterations, optional
FIDO2 hmac-secret key), the E2E and recovery-file encryption, LAN pairing, and the protections of
the page ↔ program channel.

**Residual limitations:** for yt-dlp, FFmpeg and uBlock Origin Lite the expected hash comes from the
same publisher on GitHub as the file. This protects against corruption and tampering in transit,
but not against a compromise of the publisher's account. yt-dlp's own self-update (`-U`) relies on
yt-dlp's built-in verification.

## 3. Tests actually run

The tests ran on GitHub Actions `windows-latest` against the real `Velivo.exe`. The pre-fix build
`285d73b` was used as a control.

| Test | Result | Source |
|---|---|---|
| Foreign device sends a forged bookmark (unpaired) | **Rejected** (pre-fix: accepted) | "Testy bezpieczeństwa" run #3, commit `bf77096` |
| Foreign device changes the search engine (unpaired) | **Unchanged** (pre-fix: changed) | run #3 |
| Data sent by Velivo before pairing | Only `announce` packets, 0 packets carrying data | run #3 |
| Paired device still syncs bookmarks and settings | **Pass** | run #3 |
| Velivo keeps running for the whole test; `bledy.log` stays empty | **Pass** | run #3 |
| Mark-of-the-Web written (`ZoneId=3`); an existing mark is not overwritten | **Pass** (xUnit, real `Integrity` code) | run #3 |
| Tampered tool file is rejected; a missing checksum is rejected | **Pass** (xUnit) | run #3 |
| Real Piper download matches the pinned hash; one changed byte is detected | **Pass** (xUnit) | run #3 |
| Real yt-dlp passes the check against `SHA2-256SUMS` | **Pass** (xUnit) | run #3 |
| uBlock Origin Lite: GitHub provides a digest and it matches the file | **Pass** | run #3 |
| yt-dlp: `--version` placed after `--` is not executed as an option | **Pass** (without `--` it is executed) | run #3 |
| Bills sheet token and page guard (`testy/bezpieczenstwo/arkusz.js`) | **5/5** (pre-fix code: 4 of 5 checks fail) | local Node.js run |
| Final installer build from `main` | **Pass**; installer committed as `1f08f31` | "Instalator Velivo" run #271 |

**Partially verified:** the bills-sheet guard was tested at the level of the page script and the
code, not through the Banking Mode UI. The Mark-of-the-Web was tested on the function itself, not on
a full download through the Velivo window. In the pre-fix build, the plain-text broadcast is
established from the code; the sniffer did not capture it because the old version sends it only at
start-up and when data changes. The acceptance of foreign data, however, was demonstrated in
practice.

## 4. Release files (`Instalator/` at commit `1f08f31`)

| File | SHA-256 |
|---|---|
| `Velivo-1.22.exe` (single file, no installation) | `a65a74a8b68bc74f34ecf10c90484f092df93b6d2dfefe56831a5aad3672d494` |
| `Velivo-Setup-1.22.exe` | `01919b13f638c7abd4b7a44157dfe13278550d08887c3df4a53614b7671c9975` |
| `Velivo-1.22-portable.zip` | `3b51230b79ccedc57260a8b65e27830c024498b73fae61a5ba45d07e2bc023a2` |

## 5. Code signing

**The release files are NOT digitally signed.** No genuine Code Signing certificate is available in
the repository, the CI secrets or the build environment. The Authenticode certificate table in the
PE header of both files is empty. No self-signed or test certificate was used. Windows SmartScreen
may therefore show "Unknown publisher".

Verified on Windows (workflow "Weryfikacja wydania" run #1, `windows-latest`, files from `1f08f31`):

| Check | `Velivo-1.22.exe` | `Velivo-Setup-1.22.exe` |
|---|---|---|
| `Get-AuthenticodeSignature` | `NotSigned` | `NotSigned` |
| `signtool verify /pa /v` (SDK 10.0.26100.0) | `No signature found` | `No signature found` |
| SHA-256 on Windows | `A65A74A8…3672D494` (matches section 4) | `01919B13…7671C9975` (matches section 4) |
| Code Signing certificates found (repository, `CODESIGN_PFX_BASE64` secret, `CurrentUser\My`, `LocalMachine\My`) | 0 | 0 |
| Start of the program (45 s) | runs, window "New tab – Velivo 1.22", 6 WebView2 processes, `bledy.log` empty | silent install exit code 0, 1,426 files; installed `Velivo.exe` (SHA-256 `C5E9A18A…56D8417F`, `NotSigned`) runs, window "Nowa karta – Velivo 1.22", `bledy.log` empty; uninstall exit code 0 |

Signing with a genuine certificate is prepared in `.github/workflows/podpisane-wydanie.yml`; see
`docs/PODPIS-CYFROWY.md`.

---

*Issued by the author of Velivo (internal audit). Certificate No. VELIVO-SEC-2026-001, 5 October 2026.*
