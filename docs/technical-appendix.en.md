# Velivo 1.22 – Detailed guide (technical appendix)

This document describes every Velivo 1.22 feature point by point: what it is for, where to find it, how to use it step by step, how it works technically and what its limits are. It is based on the source code (`src/`). Where a feature works only partly or something is missing, it says so plainly.

Conventions: **Where** – the place in the interface. **Steps** – the order of actions. **Technically** – mechanism, files, parameters. **Notes** – limits and dependencies. Data file names refer to the profile data folder: `%LOCALAPPDATA%\Przegladarka` (default profile) or `%LOCALAPPDATA%\Przegladarka\Profiles\<name>`.

# 1. Architecture and data files

## 1.1 Engine and shell
- Velivo is a WPF program (.NET 10, Windows 10 1809+ x64) with the **Microsoft Edge WebView2** page engine. Pages are rendered by the same engine as Edge; the window, tabs, menus, privacy, downloads and sync are Velivo's own code.
- Engine profile: `Profil\` in the data folder. Banking mode has a separate engine profile.
- One program window: a second launch passes addresses to the running window through a named pipe (name depends on the data folder) and exits. Links from other programs open as new tabs.
- Velivo presents itself to sites as Microsoft Edge: the "Microsoft Edge WebView2" brand is removed from Client Hints headers and `navigator.userAgentData` (Google blocks sign-in in "embedded browsers"). Extension pages (`chrome-extension://`) see the real brand.

## 1.2 Files in the data folder
| File / folder | Contents |
|---|---|
| `ustawienia.txt` | settings (key=value) |
| `lan-sync-key.dpapi` | LAN key encrypted with DPAPI (CurrentUser) |
| `zakladki.txt`, `zakladki-usuniete.txt` | bookmarks (address TAB title), list of deleted ones (90 days) |
| `historia.txt`, `historia-usuniete.txt`, `historia-wyczyszczona.txt` | history (time, address, title, session), deletions for sync |
| `sesja.txt`, `sesja.txt.aktywna`, `karty-przypiete.txt` | open tabs, active tab, pinned tabs |
| `zestawy-kart.json` | saved tab sets |
| `hasla.vault` | local passwords (DPAPI) |
| `autouzupelnianie.vault` | addresses, cards, accounts (DPAPI) |
| `bank*.json` | banking mode (fields encrypted with AES-GCM) |
| `prywatnosc.txt`, `prywatnosc-log.txt` | domain rules, block log |
| `ochrona-dozwolone.txt` | sites accepted as genuine after a fake-site warning |
| `ciasteczka-wyjatki.txt` | sites without cookie-banner rejection |
| `elementy.txt` | manually blocked elements (domain + CSS selector) |
| `powiekszenie.txt`, `tryb-stron.txt`, `widok-telefonu.txt` | zoom, dark/night mode, phone version – per site |
| `pamiec-stron.jsonl` | local "Where did I read that?" index |
| `pobrane.json` | download list |
| `skroty-wyszukiwania.txt` | search shortcuts |
| `Filtry\` | downloaded filter lists |
| `Dodatki\` | extensions, uBOL, sync list |
| `Miniatury stron\` | Quick Access thumbnails |
| `narzedzia\` | yt-dlp, FFmpeg |
| `sync-device-id.txt`, `sync-peers.json`, `lan-zmiana.txt` | sync |
| `bledy.log`, `debug.log` | error log, diagnostic log (`VELIVO_DEBUG=1`) |

## 1.3 Launch parameters and variables
- `--profil <name>` – start with the chosen user profile.
- `--klawiatura` – open only the on-screen keyboard (the "Velivo Klawiatura" shortcut).
- `PRZEGLADARKA_DANE=<folder>` – a separate data folder (e.g. for tests); also a separate single window.
- `VELIVO_DEBUG=1` – logs window-closing events to `debug.log`.
- Safety net: an unhandled exception does not close the program; it goes to `bledy.log`, and a message appears at most once every 30 s for the same error.

# 2. Window, toolbar and navigation

## 2.1 Toolbar
**Where:** the top bar. Buttons (from the left): new private tab, new tab, Back, Forward, Reload, home page, address bar, profile, bookmark (star), privacy shield, dark mode, read aloud with speed, reader mode, screenshot, extensions, downloads, history, settings, "More tools".
- **Compact bar:** in a narrow window labels shrink and some buttons move to the "…" menu. The "Always compact toolbar" setting forces this layout.
- **Mouse wheel on buttons** changes values without clicking: on the night-mode button – strength, on the zoom button – percent, in the "Video on top" window – transparency.

## 2.2 Keyboard shortcuts
| Shortcut | Action |
|---|---|
| Ctrl+T | new tab |
| Ctrl+Shift+N | new private tab |
| Ctrl+W | close tab |
| Ctrl+Shift+T | restore closed tab |
| Ctrl+Shift+A | search tabs |
| Ctrl+D | add/remove bookmark |
| Ctrl+H | history |
| Ctrl+J | downloads |
| Ctrl+O | open a file from disk (video or PDF) |
| Ctrl+Shift+F | "Where did I read that?" |
| Ctrl+Shift+U | read the page aloud / pause |
| Alt+← / Alt+→ | back / forward |
| F5 | reload |
| Ctrl + / Ctrl − / Ctrl 0, Ctrl+wheel | zoom |

## 2.3 Mouse gestures
**Where:** Settings → Search and start → "Mouse gestures".
**Steps:** hold the right button and move: ← back, → forward, ↑ new tab, ↓ close tab, ↓→ reload. A plain right-click opens the menu.
**Technically:** a page script recognises the movement and sends a message to the program only with a random launch token; a page cannot fake a gesture.

## 2.4 Right-click menu on a page
Items (depending on context): search for the selected text, translate the selection into Polish, translate the page into Polish/English (Google Translate), read the selection aloud, "Read from here", reader mode and summary, "Where did I read that?", Video on top, Picture in picture, Download video, Block element, Restore blocked elements, Reject / Don't reject cookie banners on this site, Show the phone version / Back to the desktop version, Screenshot (visible part / whole page), Velivo tools (Privacy and anti-fingerprinting, Detect media, Download manager, LAN diagnostics, Extensions, History, Search tabs, profile switch).

# 3. Tabs and windows

## 3.1 Tabs – basics
**Where:** the tab bar; right-click a tab.
- Tab menu: Reload, Duplicate tab, Pin/Unpin, Mute/Unmute, Auto-refresh, Add to group, Tab sets, Send to…, Open as private, Close, Close others, Close tabs to the right, Restore closed (Ctrl+Shift+T).
- **Links in the same tab** (Settings → Tabs): links with `target=_blank` open in the current tab, so Back works. Ctrl+click and middle-click still open a new tab.

## 3.2 Pinned tabs
**Steps:** right-click a tab → "Pin tab". The tab moves to the start of the bar, loses its ✕ and comes back after a restart.
**Technically:** `karty-przypiete.txt`; synced over LAN (missing pinned tabs are opened and pinned on the other computer).

## 3.3 Tab groups
**Steps:**
1. Right-click a tab → "Add to group" → "New group…" → type a name.
2. More tabs: "Add to group" → pick an existing one.
3. Clicking the group label collapses/expands it. Right-click the label: "Rename group…", "Color" (8 colors: blue, green, orange, purple, red, teal, pink, grey), "Ungroup (tabs stay)", "Close all tabs in the group".
4. "Remove from group" in the tab menu.
**Technically:** groups are saved with the session (tab position, name, color, collapsed state) and come back after a restart.

## 3.4 Tab sets
**Steps:** tab menu → "Tab sets" → "Save open tabs as a set…" → name (e.g. Work). Later: "Tab sets" → name → "Open all tabs" or "Replace current tabs"; "Delete set".
**Technically:** `zestawy-kart.json`.

## 3.5 Auto-refresh
**Steps:** right-click a tab → "Auto-refresh" → Off / 1 / 5 / 15 / 30 min. The tab shows a badge with the tooltip "Refreshing every N min".
**Notes:** the timer works per tab and is not saved across restarts.

## 3.6 Searching tabs
**Steps:** Ctrl+Shift+A → type part of a title or address → choose the tab.

## 3.7 Restoring the session and closed tabs
- Settings → Tabs → "Restore tabs from the previous session on start". Private tabs are never saved. With "Clear data on close" the session is not saved either.
- Ctrl+Shift+T restores recently closed tabs (an in-memory stack).

## 3.8 Private tabs
**Steps:** Ctrl+Shift+N or the private-tab button; from the tab menu "Open as private (incognito)".
**Technically:** a separate, isolated WebView2 store (InPrivate): no history, session or content memory; skipped by "Where did I read that?".

## 3.9 Muting a tab
Right-click a tab → "Mute tab" / "Unmute tab". A tab playing sound shows a speaker badge.

## 3.10 Zoom
- Default for all sites: Settings → Look. For a single site: Ctrl+wheel, Ctrl ±, Ctrl 0 or the wheel on the percent button.
- **Technically:** `powiekszenie.txt` (host → percent); a change carries over to other tabs with the same site.

## 3.11 Phone version of a site
**Steps:** right-click the page → "Show the phone version of this site". Back: "Back to the desktop version of this site".
**Technically:** the tab presents itself as an Android phone (User-Agent and Client Hints, which Google among others checks) on every navigation; the choice is remembered per site in `widok-telefonu.txt`.

## 3.12 Screenshot
**Steps:** the camera button or right-click → "Screenshot" → "Visible part of the page" or "Whole page (with scrolling)".
**Technically:** PNG saved to `Pictures\Zrzuty Velivo` and copied to the clipboard; a bubble with "Open" and "Show in folder" buttons. On bank and payment sites a screenshot is impossible (see 7.8).

## 3.13 Page entrance effects
**Where:** Settings → Look and readability → "Content entrance effect" and "Entrance effect speed".
- Effects: **Focus-in** (default – content emerges from a blur), **From darkness (cinematic)**, **Gentle dimming**, **None**.
- Speed: slider 0–5000 ms in 100 ms steps; 0 = automatic.
**Technically:** the effect applies to the whole page window (including the background and pinned menus); after clicking a link the page dims slightly and the new content appears with the effect.

## 3.14 Faster page opening
**Where:** Settings → Page loading speed.
**Technically:** hovering a link on the same site starts loading it in the background; Velivo connects in advance to the servers of visible links. Off in private and banking tabs. Better switched off on a data cap.

# 4. Bookmarks and history

## 4.1 Bookmarks
- Ctrl+D or the star – add/remove a bookmark for the current page. The "All bookmarks" button – the list; right-click a bookmark: rename, delete.
- **Technically:** `zakladki.txt`; deletions go to `zakladki-usuniete.txt` (90 days) so LAN sync removes them on the other computer too. Adding again clears the entry from the deleted list.

## 4.2 History (Ctrl+H)
**Layout:** day → session (one browser launch, "● Current session") → pages.
**Steps:** the "Search titles and addresses" field; double-click opens a page; right-click: "Remove from history", "Remove found entries from this day", "Remove the whole day from history", "Open all in new tabs", "Remove the session from history"; the "Clear all history" button.
**Technically:** `historia.txt` (time TAB address TAB title TAB session); old entries without a session number are split at 30-minute gaps. Saving can be turned off: Settings → Privacy → "Save browsing history".

# 5. Look and personalisation

## 5.1 Style and themes
- Settings → Look → "Look style": **Modern** (single-color Windows 11 Segoe Fluent icons, one blue accent, color only on hover or when on) or **Colorful** (colored buttons and emoji).
- "Browser theme": Light, Graphite, Navy, Night violet, Forest, Ocean, Sunset, Black (OLED), Paper, Mist.

## 5.2 Dark and night mode for pages
**Where:** the moon button; Settings → Look.
**Steps:** a click switches light → dark → night. In night mode the mouse wheel on the button sets the strength 5–100%.
**Technically:** dark mode uses the engine's dark theme: sites with their own dark look switch to it, others are darkened, and photos keep their colors. Night mode is a warm, semi-transparent layer over the page that does not catch clicks. The mode is remembered per site (`tryb-stron.txt`). If the engine started in another mode, Velivo fixes the page with CSS or offers a restart (tabs come back).

## 5.3 Language
Settings → Look → "Interface language": Automatic (from the installer / Windows), Polish, English. The change takes effect after a restart. All windows are translated, including those built in XAML, and Quick Access.

## 5.4 Search engine and search shortcuts
- Settings → Search and start → "Search engine in the address bar" (DuckDuckGo, Startpage – default, Brave Search…).
- "Search shortcuts…": word + space + query, e.g. `yt cats` searches YouTube. Add your own in the dialog; saved in `skroty-wyszukiwania.txt`.

## 5.5 Home page and new tab
Settings → Search and start → "Home page" and "Quick Access as the new tab page".

## 5.6 Velivo user profiles
**Where:** the profile button on the toolbar; Settings → Profiles → "Manage users/profiles…".
**Steps:** "Add user/profile…" → name (letters, digits, `-`, `_`); "Profile icon" → "Save icon"; "Switch to selected" (Velivo restarts in that profile); "Delete selected profile" (the active or default profile cannot be deleted).
**Technically:** each profile has its own data folder `Profiles\<name>`: sign-ins, history, passwords, settings and Quick Access. The active profile is stored in `active-profile.txt`.

## 5.7 Sound – Velivo speakers
**Where:** Settings → Security and downloads → "Velivo speakers".
- Pick a specific output instead of "Default Windows output".
- **"Don't lose sound":** every 2 s Velivo checks whether the chosen output is free. When a music program (Ableton, Cubase – ASIO/WASAPI exclusive) takes the card, Velivo's sound moves to another active output and returns when it is released.
- "🔊 Windows volume mixer…" opens Windows settings, where you can pin speakers permanently for "Velivo" / "Microsoft Edge WebView2".
**Technically:** the MMDevice API and the undocumented `IAudioPolicyConfigFactory` interface (the same one EarTrumpet uses) to assign the app's output.

## 5.8 Default browser
Settings → "Set Velivo as the default browser…". Velivo registers with Windows (class `VelivoHTML`, `StartMenuInternet\Velivo`) and opens "Default apps". Windows does not let a program set itself – you have to click "Set default".

## 5.9 Working from the tray
Settings → "Stay in the tray when the window is closed". Closing the window hides Velivo to the icon by the clock. Background pages are paused (a video in the "Video on top" window keeps playing). LAN sync keeps working. Right-click the icon: Open Velivo, Sync now, Close Velivo completely. The icon pulses while syncing.

# 6. Quick Access (new tab page)

Quick Access is a built-in extension (`chrome-extension://…/kod/newtab.html`).
## 6.1 Shortcuts and groups
**Steps:** "+ Shortcut" → address, name, optionally an image ("Choose image…" from a file, paste an image, "Remove custom") → Save. "+ Group" – a new group (e.g. Start, Finance, Music). A shortcut can be edited and deleted with the icons on its tile. From any page: right-click → "Add to Quick Access".
## 6.2 Page thumbnails
Menu ⋮ → "Page thumbnails". An extension in WebView2 cannot capture a tab, so Velivo does it: an invisible off-screen window opens the pages one by one without history, sound, pop-ups or downloads and saves thumbnails in `Miniatury stron\`.
## 6.3 Quick Access profiles and PIN
Profile list → "+ New profile...". Menu ⋮ → "PIN for this profile" – the PIN is protected with a salt and a hash. Entering a profile with a PIN requires the code, and so does adding a page to it. "Delete this profile".
## 6.4 Bin
Deleted shortcuts go to the bin: menu ⋮ → "Bin…" → "Restore" or empty it.
## 6.5 Look
Background themes and tile size.
## 6.6 Backup, folder and import
- Backup to disk ("Quick Access backup") and a data folder handled through the bridge (SejfMost) – "Extension folder".
- Import bookmarks from a JSON or HTML file. Backup limit: about 100 KB, i.e. about a thousand shortcuts.
- "Name of this computer" – to tell backups apart.
- In Chrome the same extension can sync with a Google account ("Sync with Google account", "Sync OK" state); in Velivo it is synced over LAN (see 10.8).

# 7. Privacy and security

## 7.1 Velivo ad blocking
**Where:** the shield on the toolbar; the "Turn AdBlock on/off" button; Settings → Ad blocking.
**Technically:** a domain filter in a `HashSet` (checked by successive host labels) + a list of address fragments. From EasyList-format lists the rules that block whole domains (`||domain^`) are used. Your own rules: `filters.txt` in the data folder. A blocked request gets a 403 response.

## 7.2 Full filter lists
EasyList, EasyPrivacy and the Polish list (about 97k rules). Downloaded in the background and refreshed every 4 days; "Update lists now" downloads immediately. The file is replaced only after a successful download. The filter is built in the background (a few hundred ms) and swapped as a whole. On error: "Could not download N list(s) – using the previous ones".

## 7.3 uBlock Origin Lite
Built in (GPL-3.0), on by default, can be turned off; "uBlock Origin Lite settings…". The installer includes the latest release. Once a week Velivo checks for a new release on GitHub (`uBlockOrigin/uBOL-home`) and installs it **only if the SHA-256 matches the digest given by the GitHub API**. uBOL blocks (`net::ERR_BLOCKED_BY_CLIENT`) are counted on the shield without double counting Velivo's blocks.

## 7.4 Shield and block log
- The shield shows the number of blocked items on the page and the "Blocked on this page" list.
- The "Privacy and anti-fingerprinting" panel → "What was blocked and why": time, domain, reason (tracker – domain rule / AdBlock, cookies removed by a rule…), the full address after selecting an entry; "Clear the block panel". Saved in `prywatnosc-log.txt`.

## 7.5 Tracking prevention
Settings → Privacy → "Tracking prevention":
- **Balanced** (recommended): known trackers are blocked, embedded content (posts from X, videos) works.
- **Strict:** also blocks embedded social-media content. On trusted domains it works like balanced unless you tick "Force tracker blocking" for the domain.
**Technically:** the engine's protection level is shared by all tabs, so Velivo sets it according to the active page.

## 7.6 Privacy rules per domain
**Where:** shield → "Privacy and anti-fingerprinting panel"; Settings → per-site privacy.
**Steps:** type a domain (you can paste a full address – the domain is kept) → tick: "Block JavaScript for the domain", "Don't send cookies for the domain", "Force tracker blocking for the domain", "Automatically clear data after visiting the domain" → "Save rule". "Delete rule". The "Trusted domains" list: "✔ Selected domain to trusted" (from the block log).
**Technically:** `prywatnosc.txt`; rules apply only to web pages (not extensions). Synced over LAN.

## 7.7 Privacy receipt and fingerprinting
**Where:** click the shield (Settings → "Privacy receipt on the shield").
- Shows: how many outside companies and how many countries the page connected to, data brokers, the most frequent companies and **attempts to identify the computer**: canvas (reading an invisible canvas < 250,000 px), WebGL (reading the graphics card vendor/model – parameters 37445/37446), audio (OfflineAudioContext).
- **Notes:** Velivo **detects and reports** fingerprinting but **does not change the values read** (no "farbling" like Brave and no normalisation like Firefox). Protection comes from tracker blocking via the lists and hiding the WebView2 brand.

## 7.8 Safe payments (screen-recording block)
Settings → Security → "Safe payments". On bank and payment sites (PayPal, Monzo, Revolut, Barclays, HSBC, Lloyds, NatWest, Santander, PKO, mBank, Stripe and others) the Velivo window gets `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` – recording programs and screenshots see an empty area. A 🛡 message appears. You cannot take a screenshot on these sites.

## 7.9 Fake-site detection (offline)
Settings → "Detect fake bank, shop and portal sites". Before opening a page Velivo checks:
1. **A brand on a foreign domain** – a list of protected brands with their real domains (PayPal, eBay, Amazon, Facebook, Instagram, WhatsApp, Google/Gmail, Microsoft/Outlook, Apple/iCloud, Twitter/X, Netflix, Spotify, banks and others); e.g. `paypal-secure-login.com`, `ebay.co.uk.konto.top`.
2. **Typos and swapped characters:** `paypa1.com`, `rnicrosoft.com`, `amaz0n.com`.
3. **`xn--` (punycode) addresses** with letters from other alphabets (homoglyphs).
4. **Fakes of sites you have saved passwords for.**
A warning "Careful – this may be a fake site!" appears with **No** as the default answer. "Yes" remembers the site as genuine (`ochrona-dozwolone.txt`).

## 7.10 HTTPS, SmartScreen, pop-ups
- "Always use an encrypted connection (HTTPS)" – unencrypted sites open only after a warning.
- "Warn about dangerous sites and files (SmartScreen)" – addresses are checked with Microsoft.
- "Block pop-ups opened without a click".

## 7.11 "Do Not Track" and cookie banners
- "Send Do Not Track signals" – DNT and Global Privacy Control headers.
- "Automatically reject cookie banners (GDPR)" – Velivo clicks "Reject" / "Only necessary"; if there is no such button, it clicks nothing (never "Accept"). Exception: right-click → "Don't reject cookie banners on this site" (`ciasteczka-wyjatki.txt`).

## 7.12 Blocking page elements
**Steps:** right-click → "🚫 Block element (ad)…" → hover – the element is highlighted; the wheel or arrows change the scope (parent/child element); a click or Enter blocks; Esc cancels. Undo: right-click → "Restore blocked elements on this site".
**Technically:** a rule = domain + CSS selector in `elementy.txt`; the element gets `display:none` on every visit.

## 7.13 Shop pressure-trick detector
Settings → "Warn about pressure tricks in shops". Detects fake countdown timers, "last items", "X people are watching", pre-ticked extras (Velivo **unticks** them) and hidden fees; a message appears above the page.

## 7.14 Page ↔ program channel
Pages have no access to `chrome.webview` (hidden when the document is created). Velivo scripts keep the channel before hiding it and sign messages with a random token of this launch; a message without the token is ignored. Quick Access accepts commands only from its own address.

## 7.15 Downloaded files – "file from the internet" mark
Every file downloaded by the Velivo manager, yt-dlp or the engine gets a `Zone.Identifier` stream with `ZoneId=3` (Internet) and `HostUrl=about:internet` (no page address – privacy). SmartScreen warns on launch, and Office opens the document in Protected View. An existing mark is not overwritten; on FAT32/exFAT drives the mark cannot be written.

## 7.16 Clearing data
- "Clear data on close (history and cache)" – does not sign out of accounts.
- "Clear browsing data now…": Browsing history, Cache, Download history, Cookies and active sessions (signs out of accounts), Form and card data saved by the engine, Passwords saved in the engine.

## 7.17 Program resilience
- An error in the program does not close the browser (see 1.3).
- Protection against the whole window being closed by the WebView2 control's internal handling (`window.close` on a page closes only the tab).

# 8. Passwords, Sejf and autofill

## 8.1 Velivo password manager
**Where:** Settings → Privacy → "Local password manager…".
**Steps:** "Search by domain, login or name"; "Add" (domain, user, password, name, notes; "Generate a strong password", "Paste from clipboard", "Show/hide password"); "Edit"; "Copy login", "Copy password"; "Delete selected"; "Delete all saved passwords". Columns: Name, Domain, User, Source, Changed.
**Technically:** `hasla.vault` encrypted with DPAPI (CurrentUser) with its own entropy. Generator: `RandomNumberGenerator`, at least one lowercase, uppercase, digit and symbol each, then a Fisher–Yates shuffle.

## 8.2 Saving and filling passwords
- "Offer to save passwords": after signing in, the question "A password was detected on … Add it to the Velivo database?" or "A new password was detected for … Update the entry?".
- Filling: "Fill in the saved login and password from Velivo" / "Choose an account to fill in" / "Another account from the Velivo database…".
- **Security:** an entry is matched **only to the real domain** – no matching by name, so a fake site won't get the password.

## 8.3 Import and export
- "Import passwords CSV…": Chrome, Edge, Firefox, KeePass, KeePassXC (entries from the bin are skipped); quoted fields with new lines are supported.
- "Export passwords CSV…" – **a plain-text file**, store it carefully.
- "🌐 From Chrome / Edge / Brave / Opera" (Settings → Import): bookmarks are read directly from those browsers' files (Vivaldi too); passwords – Velivo opens the browser's password page for a CSV export.

## 8.4 Sejf (external manager)
Settings → "Logins from Sejf: key on the toolbar on sign-in pages". When a page has a password field, Velivo asks Sejf (the `SejfMost.exe` program, a registered native host) for logins for **the domain from the tab's real address**. Clicking the key fills the form; before filling Velivo checks that the tab is still on that site. Sejf may ask to be unlocked (Sejf window, 1 minute limit). From the extension popup you can also save a login to the encrypted vault. Without Sejf: "(Sejf not found)".

## 8.5 Form autofill
Settings → "Form autofill (addresses and cards, local encrypted database)".
- Data: **address** (first name, last name, street, postcode, city, phone, e-mail), **card** (no CVC), **bank account** (IBAN, account number, sort code).
- Clicking an empty field fills the whole form on any https site. Velivo fills in the card and account only after you confirm the site address.
- Saving always happens only after asking ("A filled-in address form / payment card details were detected").
- **Security:** only fields really visible to a person are filled; hidden fields never are (a site can't extract data with a hidden field); only real clicks count.
- "Show saved data…", "Delete saved cards", "Delete saved addresses". File `autouzupelnianie.vault` (DPAPI).

# 9. Banking mode

## 9.1 What it is
A separate, isolated browser profile for banks, payments and shopping, with its own encrypted database. Its own cookies and sign-ins, no extensions and no history. Normal tabs see nothing of it and vice versa. Banking tabs are green and show a 🏦 icon.

## 9.2 First start
1. Click the green 🏦 button on the tab bar.
2. Set a password (min. 8 characters) and repeat it.
3. Optionally: tick "Also require a hardware key (YubiKey, Google Titan…)", insert the key, click "➕ Add key (insert it and touch it)". Windows asks for a touch **twice** (registration + reading the secret). Add all your keys (a spare!) in the same dialog. Windows finds NFC/Bluetooth keys itself after clicking "Add".
4. Choose the idle lock time and click "Save".

## 9.3 Opening and locking
- Key in the port → Velivo asks for a touch right away; no password needed. No key → password.
- Automatic lock after inactivity (default 10 min, choice 1–60 min) closes all the mode's windows. "🔒 Lock now" – immediately.
- After the mode is closed, the banking profile's cache and history are cleared (sign-ins and "remember me" stay).

## 9.4 Encryption (technical)
- Password → PBKDF2-HMAC-SHA256, **600,000 iterations**, 16 B salt → 32 B key.
- Data (cards, notes, sites, documents, accounts, bills, logins, payment history) encrypted with **AES-256-GCM** (random 12 B nonce, 16 B tag) by the `SealList` function.
- Hardware key with the **FIDO2 hmac-secret** extension: the secret from the key goes through HKDF-SHA256 ("velivo-bank-kk") and becomes the second part of the key (`SHA256(Kp + Kk)`). Without the physical key the password alone is then not enough. Key signature: ES256; WebAuthn via Windows `webauthn.dll`.
- File `bank.json` (profiles: `bank-<name>.json`), read into RAM only when the file changes.

## 9.5 Banking-mode menu (right-click 🏦)
🔍 Search my database · 🔑 My logins and passwords · 📄 Confidential data (documents) · 🏦 Bank accounts · 🧾 Bills to pay · 📊 Bills sheet (in a tab) · 🏦 My banks · 🛒 My online shops · ➕ Add this site to My banks / shops · 💳 My cards · 💳 Fill in the card on this page · 📝 My notes · 🔑 Fill in the login from a note · 🔢 Fill in selected characters · 🧪 Copy the login form description · ⚙ Banking mode settings · 📜 Opening log · 💾 Database backup · 📂 Restore database from backup · 👤 Banking profiles / ➕ New banking profile · 🔒 Lock now · ❓ Banking mode guide · I forgot the password – clear banking mode.

## 9.6 My banks and My online shops
- A list of your sites; a click opens the site in banking mode.
- Adding: on an open banking tab "➕ Add this site to My banks/shops" or on the list "➕ Add a bank / shop (name and address)…".
- **Protection:** when you open a bank or shop from the list in a normal tab, "Switch to banking mode" / "Stay here" appears above the taskbar (recognised by the main domain, including other addresses of the same company). When, with the mode open, you visit a site resembling your bank, a big warning appears.

## 9.7 Bank login details and selected characters
- "✏ Login details" (in My banks): login / customer number, passcode / PIN, password, memorable information – separately for each bank. Enter the **full** password and passcode.
- "🔑 Fill in login" – fills the login and password on the bank's site.
- "🔢 Fill in selected characters" – when a bank asks e.g. for the 2nd, 5th and 9th character (RBS, NatWest, Bank of Scotland, TSB, Lloyds, Halifax), Velivo reads the numbers from the form itself and enters the right characters, including in drop-down lists.
- "🧪 Copy the login form description (without your data)" – if filling doesn't work, it copies only the field description for a report.

## 9.8 My cards
"💳 My cards" → click a card to view and change its details. Fields: name (e.g. "Visa PKO"), number (validated), valid until (MM/YY), cardholder name, CVV (optional, 3–4 digits, encrypted). "👁 Show number and CVV". "📋 Copy number / date / CVV" – the clipboard clears after 30 s. "💳 Fill in the card on this page". On opening the mode – a reminder about cards expiring soon.

## 9.9 My logins and passwords (in the mode)
Name, login, password (hidden, "👁 Show hidden data"), site address, note. "🌐 Open site". "🔑 Fill in on the open page" – **only when the open tab has the same domain** (a message otherwise). "🎲 Generate password" – 20 characters, copied, clipboard cleared after 30 s.

## 9.10 Confidential data (documents)
Type (ID card, Passport, Driving licence, PESEL, NI number, Health insurance, Other document), full name, number (hidden), valid until (DD.MM.YYYY), note. A reminder about documents expiring within 60 days.

## 9.11 Bank accounts
Name (e.g. "My account", "Rent – landlord"), owner, number / IBAN (hidden), sort code / BIC (SWIFT), bank, transfer reference / note. 📋 buttons by each field – error-free copying into a transfer.

## 9.12 My notes
Categories: Login, PIN, Transfers, Recovery codes, Other. Title + content → "Save". "🎲 Generate password", "🔢 Numbered characters" (the password character by character with numbers – helps with "enter the 3rd and 7th character"), 📋 copying. Note and search windows don't block the page (you can copy into a form). "🔑 Fill in the login from a note" – the note must contain `login: …` and `hasło: …` lines.

## 9.13 Bills to pay
**Fields:** What for (a list of names from the sheet), Amount, Due date (DD.MM.YYYY), Repeat (Weekly, Every 2 weeks, Every 4 weeks, Monthly, Every 2 months, Quarterly, Every half year, Yearly, One-off or your own, e.g. "co 10 dni", "every 3 weeks"), Remind days before, Customer number / reference, Payment page, Note.
**Steps:**
1. Fill in the fields → "➕ Add as new" (or "✔ Change selected", "Delete selected", "Clear fields").
2. After paying: select the bill → "✔ Paid (next due date)". The payment goes to the history (month = the due month), the due date moves on according to the repeat; a one-off bill disappears.
3. "🌐 Open payment page".
4. "➕ Add bills from the sheet" – for each sheet column without a bill: the amount from the last month, monthly, due on the 1st of the next month. Correct the due day.
5. Finally "Save" – only then do changes and payments go into the encrypted database.
**Reminders:** on opening the mode – bills due within a few days and overdue ones, every day until paid.
**Notes:** Velivo does not connect to the bank; you mark payments yourself. Two "Paid" in the same month add up.

## 9.14 Bills sheet
**Where:** "📊 Bills sheet (in a tab)" in the 🏦 menu or in "Bills to pay".
- Row = month, column = bill; the "Total" column = the month's sum, the "Total" row = the bill's sum, bottom right = the grand total. Calculated live.
- Enter the month in words or numbers: "październik 2026", "October 2026", "10.2026", "2026-10".
- "➕ Row (month)" – the month after the latest in the table, copying amounts from the last row.
- "➕ Column (bill)" – name in the header; ✕ under the name deletes the column, ✕ at the end of a row deletes the month.
- "📤 Load from Excel (CSV)" – Excel: File → Save as → CSV. Layout: 1st column = month, 1st row = bills. The `;`, `,` or TAB separator is detected automatically. "Razem/Suma/Total/Sum" columns and rows are skipped. Existing months are filled in (empty cells don't erase amounts), new bills are added as columns, rows are sorted chronologically.
- "Currency you enter amounts in": £, zł, €, $, CHF, kr, Kč, Ft, lei, ₴, ¥ – **only a symbol, no conversion**; by default from the Windows region.
- "💾 Save and close" – encrypted in the database and the tab closes; rows without amounts and without a valid month are skipped (with a notice). "Cancel" – no saving.
- **Technically:** the sheet page is loaded locally (no internet) with a one-time random token; Velivo accepts data only from that page and only if the tab's address hasn't changed.

## 9.15 Payment summary
The "📊 Payment summary" window: a table of months and bills, adding a payment manually (bill, month, amount), "Delete this bill's payments for the month", "📤 Load from Excel (CSV)…", "📥 Save to Excel (CSV)" (export `Velivo-rachunki.csv`, UTF-8 with BOM, `;` separator), currency choice.

## 9.16 Banking profiles, log, backups
- "👤 Banking profiles" / "➕ New banking profile (another user)…" – a separate mode for another person: their own password or key, separate sign-ins and data.
- "📜 Opening log" – when, on which computer and how the mode was opened (wrong passwords too).
- "💾 Database backup…" / "📂 Restore database from backup…" – a `.vbank` file with all profiles, still encrypted (e.g. on a USB stick).
- "I forgot the password – clear banking mode…" – without the password and key the data cannot be read; this option deletes the mode and its data on this computer.
- Sync: all banking profiles go to paired computers in an encrypted package; bank sign-ins (cookies) stay separately on each computer.

# 10. Home network sync (LAN)

## 10.1 Principle
No cloud and no account. Computers with the same Velivo profile on one network exchange data directly (UDP, port 41919; Quick Access images – TCP on the same port). The Windows firewall must allow Velivo. On by default (Settings → Sync).

## 10.2 Before pairing
Every 10 s Velivo sends **only** an `announce` packet (identifier, computer name, profile) and **neither sends nor accepts any data**. The log says: "LAN sync waiting for pairing – no data is sent". When another Velivo with the same profile is on the network, one of the computers (the smaller identifier) proposes a connection.

## 10.3 Pairing step by step
1. Turn sync on and save the settings on both computers.
2. On one: "Pair a device on the network…" (or accept the proposal).
3. The same **6-digit code** appears on both screens. Compare it and confirm on both.
4. On the first connection choose whose **settings** stay (bookmarks, passwords and Quick Access are merged anyway).
**Technically:** **ECDH P-256** key exchange; transport key = HMAC-SHA256(agreed secret, "Velivo LAN pairing v1" + pairing id); code = HMAC(key, …) mod 1,000,000; HMAC confirmation; the sync key (32 random bytes) is sent encrypted with AES-GCM. Requests from another profile are rejected, pairing attempts expire.

## 10.4 Encryption after pairing
Key → PBKDF2-SHA256 (250,000 iterations) → 32 B encryption + 32 B authentication. Packets: AES-256-GCM with associated data (type, sender, device, profile, time, hash), "hello" packets signed with HMAC-SHA256 (constant-time comparison). GZip compression before encryption, an 8 MB limit after decompression, 60,000 B per UDP packet. Packets older or newer than ±5 min from the receiver's clock are rejected (replay protection) – set automatic Windows time on both computers.

## 10.5 What is synced
| Data | How |
|---|---|
| Settings | the newer change wins; local ones are not transferred: speakers, folders (downloads, videos), language, reader window and video window size, LAN key, quiet mode |
| Bookmarks | merged from both computers; deletions via the deleted list (90 days) |
| Passwords | merged (only in an encrypted package) |
| Privacy rules, profiles | the newer change wins |
| Extension list | missing store extensions are installed automatically |
| Pinned tabs | opened and pinned |
| History | merged, separate packages; deletions and "clear all" are transferred |
| Quick Access with images | archive ≤24 MB over TCP, only from a paired device, with path protection inside the archive |
| Banking mode (all profiles) | encrypted package; bank cookies stay local |
| Open tabs | **no** – each computer has its own (use "Send to…" to move one) |

## 10.6 Profile isolation
Only computers with the same profile sync. Packets from another profile are skipped; when the other computer switches to a different profile, Velivo asks whether to switch too, to keep syncing.

## 10.7 Send a tab to another computer
Right-click a tab → "📺 Send to…" → choose the computer. The tab opens on the other computer; for a YouTube video also from the same moment. The package is encrypted.

## 10.8 Indicators, notifications and diagnostics
- The tray icon pulses while sending and receiving changes.
- Bubbles "🌐 Synced with Velivo on the local network" / "Synced with … (settings accepted)". "LAN quiet mode" turns the bubbles off (the log keeps working).
- "LAN diagnostic panel…" (also Velivo tools → LAN sync diagnostics): status ("off", "active but no connected devices", "connected to N devices"), detected devices, packet and error counters, last receive/send time, log (250 entries), "Refresh now", "Clear log".
- Warnings: clock difference ("Rejected packets from …: clock differs by N min"), packet too big, start/receive/send errors, incomplete state.

## 10.9 Recovery file and file-based sync
- "Save recovery file…" – password min. 12 characters (twice); file ≤1 MB, PBKDF2 600,000 + AES-GCM. "Restore pairing…" after a reinstall restores the key without pairing again.
- **E2E sync by file:** "Export package…" / "Import package…" – settings, bookmarks, passwords and other data in a password-encrypted file (PBKDF2 600,000 + AES-GCM) to move e.g. on a USB stick.

# 11. Video

## 11.1 Video on top
**Where:** the ▣ button when hovering over a video; right-click → "▣ Video on top (small window)".
**Steps and window elements:**
1. The window shows just the video (the rest of the page is hidden); on first use it appears by the right edge of the screen.
2. Drag it by the top bar; resize with the frame (min. 160×114).
3. Pin: "always on top" on/off (blue = pinned).
4. Transparency 15–100%: slider and mouse wheel – e.g. 40–60% over a document.
5. A video seek bar at the bottom of the window.
6. "Back to tab" – the video returns to the tab.
**Technically:** a separate WebView2 view; transparency via a Windows window layer. Position, size, pin and transparency are remembered (with a check that the window fits on screen). YouTube: resumed through its player and the time in the address (works after a Velivo restart too). Closing the tab doesn't stop the video; closing the window does.
**Watching with the browser closed:** the video window is a separate window. You can close the tab and even the whole main Velivo window – the video keeps playing and the program runs only for the window (LAN and Sejf connections are then stopped). When you start Velivo again while only the video window is playing, Velivo closes it and starts the full window. The "Back to tab" button starts the browser on the same page and at the same moment in the video.

## 11.2 Picture in picture (PiP)
The ⧉ button when hovering over a video (Settings → "Picture in picture button over videos") or right-click → "⧉ Picture in picture" (the largest video). The PiP window stays even after the tab is closed. Videos in frames (e.g. embedded YouTube) get the ⧉ button on hover.

# 12. Downloads

## 12.1 Download manager (Ctrl+J)
Velivo downloads files itself (like Internet Download Manager):
- **Multiple connections:** Settings → "Connections per downloaded file" 1–16. Segments are used for files ≥2 MB when the server supports ranges (Range).
- **Pause / Resume** – from where it stopped; if the server doesn't allow it, a notice "will start over".
- **Retrying:** up to 5 attempts with growing delay after a dropped connection; no data for 60 s = error; "Too many redirects".
- **Signed-in files:** Velivo passes the page's cookies and follows redirects itself (cookies counted per host).
- **Fallback:** a file created by a page script cannot be taken over – the engine downloads it (`pobieranie-silnik.log`).
- "Ask where to save each downloaded file"; "Open", "Show in folder", "Cancel" (deletes the partial file), "Open the Downloads folder". The list is in `pobrane.json`.
- Edge's built-in download bubble is hidden.
- Closing Velivo with active downloads – a question.
- Every downloaded file gets the "file from the internet" mark (7.15).
- **Torrents** (Settings → Torrents, off by default): magnet and `.torrent` via aria2 (downloaded once, with consent, SHA-256 checked), `.torrent`/`magnet:` association in the installer, a separate zone `Downloads\Velivo-Torrenty` or a chosen folder, speed and seeding limits; buttons "Open folder", "Stop seeding", "Show in folder", "Delete file", "Remove from list"; after an interruption a question about deleting the partial files. From the tray they keep downloading after the window is closed; disabled in banking mode.

## 12.2 Video and audio downloads
**Where:** the ⬇ Download button when hovering over a video (Settings → "Download button over videos"), right-click → "⬇ Download video…", Tools → "Detect media to download".
**Modes:** **Best quality** (video ≤1080p MP4 + M4A audio, merged with FFmpeg), **Simple** (one file without FFmpeg), **Audio only M4A**, **Audio only MP3** (FFmpeg, highest quality). The video folder is remembered.
**Technically:** a plain video file → the Velivo manager; YouTube, streams (m3u8/mpd/blob) and other services → **yt-dlp** (downloaded once, about 18 MB, from GitHub; `-U` update every 14 days). **FFmpeg** (about 140 MB) is downloaded once when first needed. Both tools are checked with **SHA-256** from the same release's checksum file; a mismatching file is deleted and never run. The page address is passed after the `--` separator.
**Notes:** subtitles are not downloaded with YouTube videos – you download subtitles for films in the player (18.3). In "Detect media" mode a `blob:` source is not a file and cannot be downloaded directly.

## 12.3 Detect media to download
A list of audio/video sources on the page with "Download", "Download as audio", "Show" buttons. The message "No audio/video sources detected" when there are none.

# 13. Reading

## 13.1 Reader mode ("Reader" button)
The article without ads and menus. Look: ☀ Light, 🌙 Dark, 🌅 Night with a strength slider. The reader window size is remembered.
**Local summary:** no AI and no cloud – Velivo picks the most important sentences (weighted by frequent keywords of the text, title words and a bonus for the start of the article).

## 13.2 Read aloud (Ctrl+Shift+U)
- The whole page or a selection; right-click → "🔊 Read from here".
- The paragraph being read is highlighted and scrolled to the middle.
- The speed button (a click changes the pace), pause/resume, "Stop reading".
- Voice: Settings → Reading: "Automatic (page language: Polish/English)", Windows voices, natural online voices.

## 13.3 Natural offline voices (Piper)
Settings → Reading → the "Piper" voice. On first use Velivo downloads the Piper program (version 2023.11.14-2, SHA-256 checked) and the chosen voice (about 60 MB) to `%LOCALAPPDATA%\Przegladarka\Piper`; after that it works offline. The Piper process runs in the background; the page splits the text into sentences and highlights them.

## 13.4 "Where did I read that?" (Ctrl+Shift+F)
Settings → "Remember the content of pages you read". Velivo stores page text **only on this computer** (`pamiec-stron.jsonl`) and finds an article by words remembered from its content, not just the title. Skipped: private tabs, banks, payments, e-mail, pages with a password field.

## 13.5 Translation
Right-click: "Translate the selection into Polish", "Translate the page into Polish / English" (foreign → Polish, Polish → English) via Google Translate.

# 14. Extensions

## 14.1 Chromium extensions
**Where:** the Extensions button (developer mode).
- An extension icon bar with popups, "Open the extension popup", "Open the popup in a tab", "Extension settings page", pinning to the toolbar (`dodatki-odpiete.txt`), "Reload", removal.
- Loading an unpacked extension manually: "Extension folder (with manifest.json)".
- On start Velivo reloads extensions from a folder whose code changed (WebView2 keeps the old background script in its cache).

## 14.2 Chrome Web Store
On an extension's store page there is a "➕ Add to Velivo" button (or "Install from link/ID…"). WebView2 has no store API, so Velivo intercepts the `.crx` download (CRX2 and CRX3), unpacks and installs it. The extension list syncs over LAN.

# 15. Performance and cache

## 15.1 Browser junk and a RAM disk
Settings → "Browser junk (cache)":
- "Junk folder" – e.g. a RAM disk (`R:\`). Velivo creates a `Velivo-smieci` subfolder there and clears **only its contents**. No links or external tools are needed. The change takes effect after a restart.
- "Default" – back to the profile.
- "Delete junk on every start" – pages load a little slower the first time.
- "Currently taking: …", "Clear junk now".
**Technically:** the page cache and compiled JavaScript go to the chosen folder (engine arguments). The graphics cache (`GPUCache`) always stays in the profile – it is cleared on the next start. Junk is cleaned only when no other Velivo window is running.
**RAM disk benefits:** faster loading from cache, fewer writes to the SSD, the cache disappears when the computer is turned off.

## 15.2 Other mechanisms
- "Cache size (after restart)": Automatic or a chosen size.
- Request interception skips images and fonts (each intercepted request goes through the window thread); image ads are blocked by uBOL inside the engine at no cost to the window.
- The ad filter is built in the background and swapped as a whole.
- The banking profile is read from disk only when the file changes.
- Faster page opening (3.14).

# 16. Installing, updating, uninstalling

- The `Velivo-Setup-1.22.exe` installer (Inno Setup): language choice (Polski / English), installs to `%LOCALAPPDATA%\Programs\Velivo` without administrator rights, a Start menu shortcut and optionally a desktop one, registration as a browser, the "Velivo Klawiatura" shortcut.
- If it finds data from an earlier installation: "Keep my settings and data (update)" or "Clean install" (old data moved to a backup with a "kopia-…" suffix).
- .NET 10 is included in the installer (about 60 MB). If WebView2 Runtime is missing, the installer downloads it from Microsoft and installs it silently.
- The "Video player" option downloads the VLC libraries (about 145 MB, official VideoLAN package, SHA-256 checked) – only if you tick it.
- Silent install: `Velivo-Setup-1.22.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LANG=en /DIR=<folder>`; uninstall: `unins000.exe /VERYSILENT`.
- No-install version: `Velivo-1.22.exe` (a single file) or `Velivo-1.22-portable.zip`.
- **The files are not digitally signed** – Windows may show "Unknown publisher" → "More info" → "Run anyway". The release SHA-256 checksums are in `docs/VELIVO-SECURITY-AUDIT-CERTIFICATE.md`.

# 17. What Velivo doesn't have (as of 1.22)

- Faking fingerprinting readings (there is detection and a report).
- A "shared environment panel" for many computers (there is a LAN diagnostic panel).
- Syncing open tabs (there is "Send to…").
- A connection to the bank – payments are marked manually.
- A digital signature on the program files.

# 18. Video player and TV

## 18.1 Player
- A player based on libVLC (`VlcPlayer.cs`), installed optionally to `{app}\vlc`. Double-clicking a video file (associated with Velivo) opens just the player – the browser window stays minimised.
- A video library with resuming, custom titles and videos from another computer on the home network (`LanVideo.cs`).

## 18.2 TV
- Channels from the user's own list (`Iptv.cs`), TV guide, favourites, history of the last 20 channels, catch-up, recording, scheduling and a sleep timer. Access tokens are never written to logs.

## 18.3 Subtitles
- OpenSubtitles.com (`OpenSubtitles.cs`): search by file hash and by name, saved next to the film as `film.pl.srt`; the user's own account, password encrypted with DPAPI (`napisy-konto.bin`).
- Subtitle shift G / H remembered per film; subtitles can be hidden on the picture.

## 18.4 Narrator
- `SubtitleLector.cs`: reads subtitles with a Piper voice (offline) or a Windows voice, lowers the film's volume while reading; settings remembered per film.

# 19. On-screen keyboard and sticky notes

## 19.1 Keyboard
- `ScreenKeyboard.cs`: a non-activating window (doesn't take the cursor), characters injected with `SendInput`; keyboard listening (`WH_KEYBOARD_LL`) only while the keyboard is open or hidden, on its own thread; corrections run synchronously at the moment Space is pressed.
- Opening: the wheel, holding the wheel, the tray icon, the `Velivo.exe --klawiatura` shortcut ("Velivo Klawiatura" in the Start menu, its own AppUserModelID), optionally a click in a text field (UI Automation).
- Password fields are detected every 0.3 s (UI Automation) – then no corrections, learning or suggestions.

## 19.2 Dictionary, autocorrect and learning
- `SwipeTyping.cs`: Polish dictionary of about 90k words (only forms correct according to SJP), English; corrections: Polish letters, typos (edit distance 1), „nie”, fixed phrases, „ze/że” by context, polite forms of address, your own corrections.
- Learning (words, pairs and triples of words, sentences, corrections, My dictionary) stored in one DPAPI-encrypted file.

## 19.3 Gamepad
- XInput (`xinput1_4.dll`), read every 16 ms; right stick – cursor (`SetCursorPos`), clicks via `SendInput`; settings in preferences (`klawiatura-pad-*`).

## 19.4 Sticky notes
- `StickyNotes.cs`: saved in `karteczki.json`; due-date reminder – a date in the text (dd.mm[.yyyy], yyyy-mm-dd, dd/mm/yyyy), checked every 10 min, the "seen" state saved per day.
