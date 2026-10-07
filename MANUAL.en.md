# Velivo – a new level of browsing

## Complete guide and user manual

*Made with passion. Author: **Michael** ([github.com/mic0078](https://github.com/mic0078)).*

[Polski](INSTRUKCJA.md) | **English** · [Project home](README.en.md)

Velivo is a private browser for Windows that keeps your data with you. There is no account and no cloud, and your history and passwords are never sent to outside servers. Pages are displayed by the Microsoft Edge engine (WebView2), so they look and work exactly as in Edge and Chrome. Everything around the pages – the window, tabs, menus, privacy, downloads and sync – is Velivo's own code.

Velivo is designed to be fully usable with the mouse alone, even from the sofa in front of the TV. It works in Polish and English.

---

## Contents

1. [What makes Velivo different](#1-what-makes-velivo-different)
2. [How small it is and why](#2-how-small-it-is-and-why)
3. [Installing and updating](#3-installing-and-updating)
4. [Getting started – the toolbar](#4-getting-started--the-toolbar)
5. [Right-click menus](#5-right-click-menus)
6. [Mouse-only use: gestures and the wheel](#6-mouse-only-use-gestures-and-the-wheel)
7. [Tabs](#7-tabs)
8. [Videos: Video on top, picture in picture, downloads](#8-videos-video-on-top-picture-in-picture-downloads)
9. [Privacy and security](#9-privacy-and-security)
10. [Shopping: pressure-trick detector](#10-shopping-pressure-trick-detector)
11. [Reading: read aloud, reader mode, "Where did I read that?"](#11-reading-read-aloud-reader-mode-where-did-i-read-that)
12. [Search and the address bar](#12-search-and-the-address-bar)
13. [Look: styles, themes, dark and night mode, zoom](#13-look-styles-themes-dark-and-night-mode-zoom)
14. [Quick Access – the new tab page](#14-quick-access--the-new-tab-page)
15. [Passwords and Sejf](#15-passwords-and-sejf)
16. [Banking mode](#16-banking-mode)
17. [Home network sync (no cloud)](#17-home-network-sync-no-cloud)
18. [History and downloads](#18-history-and-downloads)
19. [Extensions, profiles and tools](#19-extensions-profiles-and-tools)
20. [Keyboard shortcuts](#20-keyboard-shortcuts)
21. [Settings – every option explained](#21-settings--every-option-explained)
22. [Troubleshooting and where data is stored](#22-troubleshooting-and-where-data-is-stored)

---

## 1. What makes Velivo different

**Velivo goes beyond the limits of today's browsers.** Chrome, Edge, Firefox and Opera **do not have these features built in**. Some need separate extensions, some can't be done at all. In Velivo they work right away, with no add-ons and no account.

| Feature | What it does |
| --- | --- |
| **🎞 All in one** | Video player for files on disk (with picture in picture and "Video on top"), PDF viewer and torrents – **no separate programs to install**. Open files by double-click or "Open with". |
| **▣ Video on top** | Velivo's own video window you can shrink almost to an icon (from 160×90 px), with **transparency set by the mouse wheel** and an "always on top" pin. **It keeps playing after you close the tab – and even the whole browser.** |
| **🧠 "Where did I read that?"** | Finds an article you read by words from its **content**, not just its title. Everything stays on your computer. |
| **⚠ Shop-trick detector** | Warns about fake countdowns, "only 2 left" pressure and hidden fees, and unticks pre-selected add-ons in the cart. |
| **🧾 Privacy receipt** | Shows how many companies and countries the page connected to, which of them are data brokers and whether it tried to fingerprint your computer. |
| **📺 Send tabs at home** | The page opens on another computer running Velivo, and videos continue at the same moment. No cloud, no account. |
| **Sync without the cloud** | Bookmarks, passwords, history, pinned tabs, Quick Access and settings between home computers, encrypted. |
| **⬇ IDM-style video downloads** | A "Download" button over every video, YouTube included, with quality, MP3/M4A and folder choice. |
| **🔊 "Read from here"** | Right-click a paragraph and Velivo reads from that sentence. |
| **🌅 Night mode with strength** | Like Windows "Night light", adjusted with the mouse wheel. |
| **🛡 Offline fraud protection** | Recognises fake bank, shop and portal sites (`paypa1.com`, `ebay-verify.top`, letters from other alphabets) and fakes of sites you have saved passwords for – before the page opens. |
| **💳 Safe payments** | On bank and payment sites the Velivo window becomes invisible to screen-recording programs. |
| **🔐 Fake-proof passwords** | A password goes only to the real domain. A page cannot swap the helper, "click" for you or steal data with a hidden field. |
| **📥 Moving from another browser** | Bookmarks read directly from Chrome, Edge, Brave, Opera and Vivaldi; passwords from KeePassXC and others via CSV. |
| **🖱 Gestures and mouse-only use** | Back, forward, new tab, close, reload and zoom without a keyboard. |

---

## 2. How small it is and why

| Item | Size |
| --- | --- |
| **Installer** `Velivo-Setup-1.22.exe` | **about 13 MB** (about 6 MB of it are the rule lists of the built-in uBlock Origin Lite) |
| **Installed program** | about 30–40 MB (including Windows components and the Quick Access add-on) |
| **RAM used by Velivo itself** (`Velivo.exe`) | typically **50–80 MB** |
| For comparison: Chrome or Firefox installer | about 100–130 MB |

### Why the installer is so small

1. **Velivo doesn't ship its own browser engine.** Chrome, Opera and Brave each include the whole Chromium engine, over 100 MB. Velivo uses the Microsoft Edge engine (WebView2), which is already part of Windows 10 and 11 and is updated by Windows Update. Velivo only adds its own parts: the window, tabs, privacy, downloads and sync.
2. **The interface is a native Windows program (WPF, .NET),** not a web page pretending to be an app as in Electron. Native code like this is very compact.
3. **Large extras download only when you need them, and only with your consent:**
   - full ad-block lists download in the background after the first start;
   - natural offline voices are about 60 MB each;
   - the yt-dlp video tool is about 18 MB;
   - the FFmpeg add-on for best quality and MP3 is about 140 MB.

### Why Velivo uses so little memory

1. **The window, tabs, menus and toolbars are native.** They are not built from web pages, so the shell itself takes 50–80 MB instead of several hundred.
2. **Ads and trackers are blocked before they download.** Pages carry fewer scripts, images and frames, so they use less memory and load faster.
3. **Background tabs give memory back.** Velivo tells the engine a tab is in the background and the engine frees part of its RAM. Music and chats in those tabs keep working.
4. **There are no background services:** no account, cloud, telemetry, suggestions, shopping or news feed.
5. **The engine is shared with Windows.** Part of its libraries are kept in memory by the system anyway.

**To be fair:** the 50–80 MB is the Velivo program itself. Page content is rendered by the Edge engine in separate `msedgewebview2.exe` processes, which also use memory, as in every browser. How much depends on how many tabs are open and how heavy the pages are. Thanks to ad blocking and background-tab sleep it is usually less than Chrome uses for the same pages.

---

## 3. Installing and updating

**Requirements:**
- Windows 10 version 1809 or newer, or Windows 11, 64-bit;
- .NET 10 Desktop Runtime (x64);
- Microsoft Edge WebView2 Runtime (always present in Windows 11).

If something is missing, the installer detects it and opens the download page.

**Step by step:**
1. Download `Velivo-Setup-1.22.exe` from the [`Instalator`](Instalator) folder.
2. Run it. If Windows shows "Unknown publisher", click **More info → Run anyway**. The installer has no paid code signature.
3. **Choose the installer language: Polski or English.** Velivo will start in that language. You can change it later in Settings → Look → Language.
4. If Velivo is already installed, choose:
   - **Keep my settings and data** – a normal update (recommended);
   - **Clean install** – start from scratch. Old data is backed up with a `.kopia-<date>` suffix.
5. Optionally tick the desktop shortcut and finish.

No administrator rights are needed. The program goes to `%LOCALAPPDATA%\Programs\Velivo`. The installer closes Velivo processes running in the background and works around files locked by antivirus software.

**Make it the default browser:** Settings → "Set as default". Links from other programs will then open in Velivo.

---

## 4. Getting started – the toolbar

![The Velivo window: tabs, toolbar, shield with counter, and the Video on top window in the corner](docs/zrzuty/velivo-okno.png)

Velivo starts maximized. From the left:

| Item | What it does |
| --- | --- |
| **← →** | Back and forward |
| **⟳** | Reload (F5) |
| **⌂** | Home page |
| **Address bar** | Type an address or search words. Right-click gives Cut, Copy, Paste and **Paste and go** |
| **125%** | Page zoom. The **mouse wheel over the button** changes it and a click restores the default. Zoom is remembered per site |
| **🔑** | Appears when Sejf has logins for the site; a click fills them in |
| **☆** | Add or remove a bookmark (Ctrl+D). A gold star means the page is bookmarked |
| **Privacy** | Rules panel for the current domain |
| **Extension icons** | Chrome extensions. They can be pinned and unpinned |
| **🛡 + number** | Shield: how much was blocked on the page. A click shows the list and the privacy receipt. A red shield means AdBlock is off |
| **⚠ + number** (yellow) | Appears in a shop that uses pressure tricks |
| **📚** | All bookmarks |
| **☀ / 🌙 / 🌅** | Page mode – the icon shows the current one: ☀ light → 🌙 dark → 🌅 night. In night mode the mouse wheel sets the strength |
| **📷** | Screenshot: visible part or the whole page |
| **Reader** | Reader mode with a summary |
| **🔊** | Read the page aloud. While reading, pause, stop and speed buttons appear |
| **⬇** | Downloads (Ctrl+J) with the number of active downloads |
| **🧩** | Extensions: list with pins and "Manage extensions…" |
| **🕘** | History (Ctrl+H) |
| **⚙** | Settings |
| **Profile** | Active profile; a click switches |

The tab bar also has **+** (new tab, Ctrl+T) and **🕶** (private tab, Ctrl+Shift+N).

On a narrow screen some buttons move into the **"…"** menu.

---

## 5. Right-click menus

### On a page

Right-click anywhere on a page.

| Item | When it appears | What it does |
| --- | --- | --- |
| Search "…" / Go to … | Text selected | Searches the selection or opens the selected address |
| Translate selection / Read selection aloud | Text selected | Translates or reads the selection |
| Translate page to Polish / English | Page in another language | Google Translate for the whole page |
| 🚫 Block element (ad)… | Always | Point at an element that should disappear on every visit |
| Restore blocked elements | When something was blocked by hand | Undoes the blocks on this page |
| 🍪 (Don't) reject cookie banners | Always | Exception from automatic rejection for this site |
| 📱 / 🖥 Phone / desktop version | Always | Switches and remembers for this site |
| ▣ Video on top · ⬇ Download video · ⧉ Picture in picture | On a video or on YouTube | See [section 8](#8-videos-video-on-top-picture-in-picture-downloads) |
| Reader mode and summary | Always | Clean article text |
| Read page aloud (Ctrl+Shift+U) | Always | Reads the main content |
| 🔊 Read from here | Nothing selected | Reads from the sentence you clicked |
| 🧠 Where did I read that? | Always | Search in the content of pages you read |
| Screenshot | Always | Visible part or the whole page |
| Velivo tools | Always | Privacy, media, downloads, history, extensions, network diagnostics, profiles, picture in picture, tab search |
| Add to Quick Access | Always | Shortcut to a chosen group |

### On a tab

Right-click a tab.

- Reload, Duplicate tab
- Pin or unpin tab
- Mute tab or unmute
- Auto refresh: every 1, 5, 15 or 30 minutes
- 📺 Send to… (another computer running Velivo at home)
- Add to group, Tab sets, Search tabs
- Close tab, other tabs or tabs to the right (pinned tabs are skipped)
- Reopen closed tab (Ctrl+Shift+T)

In the Modern look menus show Windows 11 icons. In the Colorful look they show emoji.

---

## 6. Mouse-only use: gestures and the wheel

### Mouse gestures

Hold the **right button**, move the mouse about 3 cm and release:

| Move | Action |
| --- | --- |
| ← left | Back |
| → right | Forward |
| ↑ up | New tab |
| ↓ down | Close tab |
| ↓ then → | Reload |

A normal right-click without moving opens the menu as always. You can turn gestures off in Settings → Search and start.

A **pinned tab** is not closed by the ↓ gesture (just like it has no close button). The turn in ↓ then → counts from about 15 px, so "Reload" doesn't accidentally become "close tab".

### Mouse wheel on buttons

| Where | Action |
| --- | --- |
| Zoom button (e.g. 125%) | Zooms the page in and out, remembered per site |
| Page mode button (🌅) in night mode | Warmth strength 5–100% |
| "Video on top" window | Transparency 15–100% |

### Clicking links

- **A normal click** opens in the same tab (Back and Forward work). You can change this in settings.
- **The middle button** or **Ctrl+click** always opens a new tab.

---

## 7. Tabs

- **Pinning:** right-click a tab → Pin tab.
  - the tab moves to the front of the bar, with a pin and no close button;
  - it is "frozen" to its address: a link to another site opens in a new tab;
  - it comes back on every start and syncs with your other computers.
- **Tab groups:** right-click → Add to group → New group….
  - tabs in a group have a colored dot, with a colored label in front of them;
  - a click on the label collapses the group into one label with a tab count;
  - right-click the label: name, color, ungroup, close the whole group.
- **Tab sets:** right-click → Tab sets → Save open tabs as a set…. From the same menu you later open the whole set, replace it or delete it.
- **Mute:** a tab that plays sound shows 🔊. A click mutes it (🔇).
- **Auto refresh:** right-click → Auto refresh. The tab then shows ⟳.
- **Tab search:** Ctrl+Shift+A, type a few letters, Enter switches.
- **Private tabs (🕶):** save nothing, don't sync and don't go to history.
- **After a restart** your tabs, groups and pinned tabs come back. You can turn this off in settings.

---

## 8. Videos: Video on top, picture in picture, downloads

Hover over a video and three buttons appear in its top right corner: **⬇ Download · ▣ On top · ⧉ Picture in picture**. This works on YouTube too.

### ▣ Video on top (Velivo only)

![Buttons over a video: Download, On top, Picture in picture](docs/zrzuty/przyciski-filmu.png)

| The video window | Shrunk in a screen corner | Almost transparent over the desktop |
| --- | --- | --- |
| ![Video on top window](docs/zrzuty/film-na-wierzchu.png) | ![Small window by the clock](docs/zrzuty/film-maly-w-rogu.png) | ![Transparent window – desktop icons show through](docs/zrzuty/film-przezroczysty.png) |

1. Click **▣ On top**. The video in the tab pauses and continues in a small window from the same moment.
2. Using the window:
   - **move** it by the dark bar at the top;
   - **resize** it by an edge or corner, from 160×90 px;
   - **transparency:** mouse wheel over the window, 15–100%; the bar briefly shows "Visibility 60%";
   - **📌 pin:** blue means always on top, white means a normal window that can go behind others;
   - **click the video** to pause or resume;
   - **↩** goes back to the page in Velivo from the same moment, even if the browser was closed;
   - **✕** closes the window.
3. The window **keeps playing after you close the tab, and even the whole of Velivo.** Clicking the Velivo icon then starts the browser again.
4. Position, size, transparency and pin are remembered.

**Clean video view.** The window shows **only the video**, filling the whole window – none of the rest of the page: no menus, comments, suggestions, pop-ups or banners. On YouTube it feels like a separate player. Ad and tracker blocking works as in a tab, and YouTube video ads are skipped (clicking "Skip" or fast-forwarding the muted ad).

**What it's great for:**
- music or a podcast in a small, see-through window in a corner while you work in other programs;
- a match or live stream over a document or spreadsheet – set visibility to 40–60% and the text underneath stays readable;
- a video tutorial next to the program you're following it in;
- the video keeps playing even after Velivo is closed, so the browser uses no memory.

**Mouse-only summary:**

| What | How |
| --- | --- |
| Pause / resume | click the video, ▶/❚❚ on the bar or Space |
| Seeking | ◀◀ / ▶▶ (10 s), time bar, ← → keys, wheel over the time bar (5 s) |
| Volume | 🔊 mute, slider, ↑ ↓ keys, wheel over the slider, M |
| Full screen | ⛶ on the bar, F or double-click the video; Esc to leave |
| Transparency 15–100% | mouse wheel over the window |
| Move | dark bar at the top |
| Size (from 160×90) | edge or corner |
| Always on top | 📌 (blue = on) |
| Back to the page at the same moment | ↩ |
| Close | ✕ |

Transparency needs Windows 10 version 1809 or newer, because the window uses a Windows component to draw the picture.

### 🎬 Offline video player

- **Opening:** double-click a video file in Windows (the installer adds Velivo to "Open with" for MP4, WebM, MKV, MOV, M4V, OGV; make it permanent in Default apps → Velivo), **Ctrl+O** or right-click → Velivo tools → **📂 Open a file**.
- The video opens in a tab, filling the window, with no internet needed. **⧉ Picture in picture** and **▣ Video on top** work on it.
- **Controls:** the player bar (play/pause, seek, volume, full screen) and keys: Space or click – pause, ← → – 5 s, ↑ ↓ – volume, **F** – full screen, **M** – mute.
- **If Velivo isn't in "Open with":** start Velivo once (it registers itself with Windows) or use **Settings → Video player → "Open videos and PDFs in Velivo (Windows)…"**.
- **Settings → Video player:** open videos in Velivo, play right away, resume where you left off (this computer only), loop.
- **Formats:** MP4 (H.264) and WebM work best. If the codec isn't supported (e.g. HEVC/H.265), a message appears.

### ⧉ Picture in picture

The standard window of the browser engine. It keeps playing after you close the tab, until you close the window. Its minimum and maximum size are set by the Chromium engine, not by Velivo.

### ⬇ Video downloads

1. Click **⬇ Download** or right-click a video → **⬇ Download video…**.
2. Choose the quality:
   - **best video quality** (up to 1080p, MP4);
   - **quick** – video in one file, usually 360p–720p;
   - **audio only M4A**;
   - **audio only MP3**.
3. Choose the folder (**Change folder…**). Velivo remembers the last one.
4. Progress, speed and time left are shown in the Downloads window.

**What does the downloading:**
- **Regular video files:** Velivo's manager, up to 16 connections at once.
- **YouTube, streams and over 1,000 sites:** the free **yt-dlp** tool:
  - downloaded once with your consent (about 18 MB);
  - updates itself every 2 weeks;
  - downloads 8 parts at once.
- **Best quality and MP3** need the free **FFmpeg** (one-time, about 140 MB, with consent).

**Limits:**
- Netflix, Disney+ and other DRM-protected services are not supported.
- Only download content you have the right to, for example for personal use.

---

## 9. Privacy and security

| Feature | Description |
| --- | --- |
| **Ad and tracker blocking** | EasyList, EasyPrivacy and a Polish list, about 95k rules. Works before download, so pages are lighter |
| **Manual element blocking** | Right-click → 🚫 Block element. The mouse wheel widens the area, a click blocks, Esc cancels |
| **🍪 Cookie banners (GDPR)** | Velivo clicks "Reject" or "Only necessary" for you. **It never clicks "Accept".** If a banner has no reject button, nothing is clicked |
| **Tracking prevention** | Balanced (default) or strict. On trusted sites strict behaves like balanced, unless you tick "Force tracker blocking" |
| **Site rules** (Privacy button) | Block JavaScript or cookies, force tracker blocking, auto-clear data, trusted domain |
| **🛡 Shield** | Counter and list of everything blocked: AdBlock, rules, JavaScript, hidden elements, cookie banners |
| **🧾 Privacy receipt** | At the top of the shield window: how many outside companies, in how many countries, data brokers, fingerprinting attempts (canvas, graphics card, audio) and the most contacted companies |
| **"Do Not Track"** | Sends DNT and Global Privacy Control signals |
| **SmartScreen** | Microsoft protection against dangerous sites and files, skipped on trusted domains |
| **🛡 Fake-site detection** (offline) | Before a page opens, checks whether the address imitates a bank, shop or portal: a brand on a foreign domain (`paypal-secure-login.com`, `ebay.co.uk.account.top`), typos and swapped characters (`paypa1.com`, `rnicrosoft.com`, `amaz0n.com`), `xn--` addresses with letters from other alphabets, and fakes of sites you have saved passwords for. Shows a warning with "No" as the default. If it's the real site, choose "Yes" – Velivo remembers it |
| **🔒 Always HTTPS** | Every connection is tried encrypted first. An unencrypted site opens only after a warning, so you don't type passwords or card details there |
| **💳 Safe payments** | On bank and payment sites (PayPal, Monzo, Revolut, Barclays, HSBC, Lloyds, NatWest, Santander, PKO, mBank, Stripe and more) the Velivo window is invisible to screen-recording programs. A 🛡 message appears. You can't take screenshots on those sites |
| **🔐 Attack-proof passwords and forms** | Passwords only for the real domain; the Velivo helper can't be swapped by a page; only real mouse clicks work; hidden fields are never filled; Velivo asks before filling a card or bank account; the CVC is never saved |
| **Private tabs** | Separate, isolated data that disappears when closed. Passwords and forms work; saving only with your consent |
| **Clearing data** | On exit or by hand in settings |

All switches are in Settings → "Security and downloads". Attack tests are in the repository under `testy/bezpieczenstwo/`.

**Honestly:** Velivo protects you in the browser. Viruses and spyware on the system itself are caught by an antivirus (e.g. Windows Defender or Bitdefender) – keep it on.

---

## 10. Shopping: pressure-trick detector

When a shop tries to rush you, a yellow **⚠ number** button lights up next to the shield. A click shows the list:

- **⏱ a countdown.** If it restarts after you reload the page, Velivo marks it as **fake**;
- **📦 "only 2 left", "3 seats remaining";**
- **👥 "15 people are viewing now", "someone just bought";**
- **☑ pre-ticked insurance, warranty or newsletter in the cart.** Velivo **unticks** them;
- **💸 a service, handling or booking fee** that appears only at checkout.

The detector reads the page's texts and behavior, so it won't catch every shop and can occasionally be wrong.

---

## 11. Reading: read aloud, reader mode, "Where did I read that?"

### Read aloud

- **🔊 on the toolbar** or Ctrl+Shift+U reads the main content. The paragraph being read is highlighted and scrolled to the middle of the screen.
- **Right-click → 🔊 Read from here** reads from the clicked sentence. A click elsewhere jumps there.
- **Select text → Read selection aloud** reads just that part.
- **Same content as Reader:** read aloud and Reader share one article detector – only the article is read, without side columns and extras.
- **Voice per paragraph language:** in mixed text each paragraph is read with a voice in its own language (Polish / English).
- **Reacts to changes:** a new voice or speed in Settings applies at once; when the page switches to another article, reading stops.
- While reading, the toolbar shows pause, stop and speed (0.75×–2×); volume is in settings.
- **Voices:**
  - Windows Polish and English voices;
  - natural Piper offline voices: Gosia, Darkman, MC Speech, Lessac, Ryan, Alba. Each downloads once, about 60 MB.
- **Skipped while reading:**
  - menus, footers and ads;
  - "Read also" and "Recommended" blocks and link lists;
  - comments, newsletters and photo captions.

### Reader mode ("Reader" button)

- Clean article text in a readable font, without ads or distractions.
- **"Key sentences"** – an automatic summary computed locally, **no AI and no cloud**. Velivo picks the sentences with the article's and title's most important words, with a bonus for the start of the text.
- "Read summary" and "Read all" buttons. Clicking the text reads from that point.

### 🧠 "Where did I read that?" (Ctrl+Shift+F)

- Velivo remembers the text of pages you read, **only on this computer**.
- Type words you remember from the content, for example "laptop battery 6 hours". Letter case and Polish characters don't matter.
- Results show the title, site, date and a snippet with the words. A double-click opens the page.
- **Skipped:** private tabs, banks, payments, mail, logins, gov.pl, ZUS and any page with a password field.
- Only the page's **main content** is stored (no menus or ads); **search result pages** are not stored.
- **Where to open it:** right-click on a page, the button in the History window or Velivo tools.
- Clearing history clears this memory too. You can turn the feature off in settings.

---

## 12. Search and the address bar

- **Type words** instead of an address and Velivo searches them in your chosen search engine (Settings → Search and start).
- **Search shortcuts:** type a shortcut, a space and the search text:

| Type | Searches |
| --- | --- |
| `yt cats` | YouTube |
| `g recipe` | Google |
| `ddg weather` | DuckDuckGo |
| `wiki Kraków` | Wikipedia (PL) |
| `allegro bike` | Allegro |
| `olx sofa` | OLX |
| `ceneo tv` | Ceneo |
| `mapy Gdańsk` | Google Maps |
| `filmweb Matrix` | Filmweb |
| `tlumacz hello` | Google Translate |

  Add your own in Settings → Search and start → **Search shortcuts…**, for example `shortcut=address with %s`.
- **Right-click the address bar → Paste and go** pastes and opens at once.

---

## 13. Look: styles, themes, dark and night mode, zoom

- **Style** (Settings → Look):
  - **Modern** (default): calm buttons without colored backgrounds, Windows 11 icons, one blue accent, and color only on hover or when something is on;
  - **Colorful**: colored buttons and emoji.
- **10 themes:** Light, Graphite, Navy, Night violet, Forest, Ocean, Sunset, Black (OLED), Paper, Mist.
- **Page modes** (mode button – the icon shows the current mode: ☀ light, 🌙 dark, 🌅 night):
  - light;
  - **dark** – pages darkened while photos keep their true colors;
  - **night** – warm colors with less blue light, **strength set with the mouse wheel**.
- **Each site has its own mode** – one light, another dark, another night; Velivo remembers it per site. In dark mode a page enters without a white flash (dark background before painting, dark mist in the entrance effect).
- **Zoom** remembered per site, changed with the wheel on the percent button or with Ctrl+wheel.
- **Phone version** for a chosen site (right-click), remembered.
- **Language:** Polish or English (Settings → Look → Language).
- **Content entrance effect:** a new page appears from a focus-in blur, from darkness (cinematic), with a gentle dim or instantly; speed 0 to 5 s (Settings → Look). Details in chapter 21.
- **Reader mode** has its own look: ☀ light, 🌙 dark or 🌅 night with a strength slider.

---

## 14. Quick Access – the new tab page

- Shortcuts arranged in groups, for example Start, Finance, Music, Games.
- Site icons and **page thumbnails** made in the background. Icons don't flicker when you switch groups.
- **Quick Access profiles**, optionally with a PIN, and **background themes**.
- Adding: the **+ Shortcut** button, right-click on a page → "Add to Quick Access", or import bookmarks.
- Syncs with other home computers: shortcuts and groups are merged.

---

## 15. Passwords and Sejf

- **Sejf** (a separate program by the same author) keeps passwords encrypted offline. When a page has a login field, **🔑** appears on the toolbar and a click fills in the form.
- **Saving to Sejf:** after you log in, Velivo asks whether to save or update the password.
- **Velivo's own encrypted store** (Local password manager) with a **strong password generator**.
- **CSV import and export**, for example from KeePassXC or Chrome.
- **Import from Chrome/Edge/Brave/Opera/Vivaldi** (Settings → “Import from Chrome/Edge/Brave…”): Velivo reads bookmarks by itself, from every profile. Passwords move via a CSV file: a button opens the password export page in that browser, then “Load CSV file…”, and finally delete the CSV file.
- **Important:** import **before** you uninstall the old browser. Once it is removed, its bookmarks can't be read and its passwords can't be exported. Passwords from a Google account can also be downloaded later from passwords.google.com. Site logins don't transfer – sign in once in Velivo.
- **Buttons by the login field:** a **🔑 Fill in · ⚡ Generate** badge appears to the right of the field:
  - **🔑 Fill in** lists this site's accounts (name and e-mail); a click fills in login and password. At the end of the list, **🔎 Another account from the Velivo vault…** opens a window that searches the whole vault (e.g. signing in with a PreSonus account on Fender's site). The window shows the site address and warns about fakes;
  - **⚡ Generate** types a strong random password.
- **Save prompt:** after you click "Sign in", press Enter or submit a form, Velivo asks whether to save or update the password. Works in private tabs too.
- **Password manager:** search, copy login and password, edit, export. Entries from KeePassXC and phones without an address get a domain from the title or app link (`android://…`), and a double-click opens the site and fills the form.
- **Autofill** (local, encrypted store): **address** (first and last name, street, postcode, city, phone, e-mail), **card** (no CVC) and **bank account** (IBAN, account number, sort code). Clicking an empty field fills the whole form – on any https site. Card and bank details are filled only after you confirm the site address.

---

## 16. Banking mode

Banking mode is a separate, isolated browser profile for banking, payments and shopping, with its own **encrypted vault**. Normal tabs can't see anything from it and vice versa. It has no extensions and no history, and the cache is cleared when it closes (logins and "remember me" stay).

**Opening and settings**

- The green **🏦** button on the tab bar. The first time you set a **password** (min. 8 characters) and, optionally, a **hardware key** (YubiKey, Google Titan and other FIDO2 keys): tick "Also require a hardware security key", insert the key and click **"➕ Add key"** – Windows asks you to touch it twice. Add all your keys in the same window, then **"Save"**.
- **Key plugged in** – Velivo asks you to touch it right away, no password needed. **No key** – enter the password. A key marked **🔐** opens the mode on its own and encrypts the vault.
- Banking tabs are green and show 🏦. The mode **locks itself** after inactivity (1–60 min, your choice) and closes all its windows.
- Everything is under **right-click on 🏦**, including **❓ Banking mode guide**.

**My banks and My online shops**

- **🏦 My banks / 🛒 My online shops** – your sites; a click opens the site in banking mode.
- Adding: **"➕ Add this site to My banks / shops"** on an open banking tab, or **"➕ Add a bank / shop (name and address)…"** in the list.
- **✏ Login details** (in My banks): login / customer number, passcode / PIN, password and memorable information – separately for each bank. Always enter the **full** password and passcode.
- **🔑 Fill in a login** – fills the login and password on the bank's page.
- **🔢 Fill in selected characters** – when the bank asks e.g. for the 2nd, 5th and 9th character (RBS, NatWest, Bank of Scotland, TSB, Lloyds, Halifax), Velivo reads the numbers and types the right characters, drop-down lists included. If a page doesn't work, **🧪 Copy a description of the login form** copies only the field description (without your data) for a report.
- **🔗 Data linked to the page** – on a page from your vault the login, password and selected characters fill in by themselves (when one account matches; Velivo never submits the form). Several accounts (e.g. personal and business) – one click to choose; payment card – always after a click. Can be turned off in Bank mode settings.
- **🔒 Full isolation** – bank mode uses only its own encrypted vault: the browser's regular passwords and autofill are neither suggested nor saved there, and vault data goes only into bank tabs (no extensions).
- **Extensions** get no access to bank or private tabs – even when opened from the toolbar while such a tab is active.
- **🛡 Transfer guard** – a pasted or typed account number is checked (IBAN / NRB checksum) and compared with your Bank accounts: a typo, a number swapped in the clipboard by malware or an unknown payee shows up at once.
- **💸 Transfer from the vault** – on a transfer form Velivo fills in the payee, account number (NRB / IBAN / sort code + 8 digits) and reference, and the amount and customer number from the Bill with the same name. You always confirm it yourself.
- **💬 Suggestion under the field** – click the login, password, selected characters, card number or payee account field: if the vault has matching data for this page, a choice appears right under the field (one click fills it); nothing matches – nothing appears. The page can't see your account names.
- **Menu only with matching data** – fill items in the 🏦 menu appear only when the vault has data for the open page.
- **Safe filling** – the full password never goes into one-character boxes (only the selected characters do), and card data goes only into visible fields – hidden fields on the page get nothing.

**Cards, notes and search**

- **💳 My cards** – click a card to view and edit it; **"👁 Show number and CVV"** reveals the hidden fields. The CVV is optional. **📋 Copy number / expiry / CVV** – the clipboard clears itself after 30 s. **💳 Fill in a card on this page** fills the payment form. When the mode opens, Velivo reminds you of cards that expire soon.
- **📝 My notes** – logins, passwords, customer numbers, with categories (Login, PIN, Transfers, Recovery codes, Other). **🎲 Generate password** inserts a strong password, **🔢 Numbered characters** shows the password character by character with numbers. The notes and search windows don't block the page.
- **🔍 Search my vault** – one field for banks, shops, cards and notes.

**🔑 My logins and passwords**

- For any non-bank service (email, shops, Netflix, government sites…): *Name*, *Login*, *Password* (hidden – "👁 Show hidden data" reveals it), *Website address*, *Note*. **📋** next to each field copies it (clipboard cleared after 30 s).
- **🌐 Open site** – opens the address in a tab. **🔑 Fill in on the open page** – types login and password, but **only on a page with the same address (domain)** – a password never reaches a fake site. **🎲 Generate password** – inserts and copies a strong 20-character password.

**📄 Confidential data (documents)**

- *Document type* (ID card, passport, driving licence, PESEL, NI number, health insurance, other), *Full name*, *Number* (hidden), *Valid until* (DD.MM.YYYY), *Note*.
- When banking mode opens, Velivo **reminds you about documents expiring within 60 days**.

**🏦 Bank accounts**

- Your accounts and payees (e.g. your landlord): *Name*, *Account holder*, *Account number / IBAN* (hidden), *Sort code / BIC (SWIFT)*, *Bank*, *Payment reference / note*. **📋** buttons let you copy details into a transfer without mistakes.

**🧾 Bills to pay**

A list of regular and one-off payments (rent, electricity, heating, council tax, internet, instalments…), encrypted with the rest of the vault. Open it from **🏦 → 🧾 Bills to pay**.

- **Bill fields:** *What for* (name), *Amount*, *Due date* (DD.MM.YYYY), *Repeat* (weekly, every 2 weeks, every 4 weeks, monthly, every 2 months, quarterly, every half year, yearly, one-off – or type your own, e.g. "every 10 days"), *Remind days before*, *Customer number / reference*, *Payment website*, *Note*. Each field has **📋** to copy (clipboard cleared after 30 s).
- **Name from the sheet:** the *What for* field has a drop-down with the column names from the bills sheet (e.g. "Housing Rata 1", "Council Rata 2"). Pick the name from the list so paid bills land in the right sheet column. A different name (even a typo) = a new column in the sheet.
- **➕ Add as new / ✔ Change selected / Delete selected / Clear fields** – normal list editing. Finally click **Save** – only then are changes (including payments marked as paid) written to the encrypted vault.
- **✔ Paid (next due date)** – select a bill and click: the payment is recorded in the history (month = due-date month, amount from the bill) and the due date moves on by the repeat interval. A one-off bill disappears from the list once paid. Clicking twice in the same month adds the amount twice – fix mistakes in the sheet.
- **➕ Add bills from the sheet** – adds every sheet column that is not on the list yet: amount from the latest month, repeat *monthly*, due on the 1st of the following month. **Then correct the due day** and click **Save**.
- **🌐 Open payment site** – opens the *Payment website* address in a banking tab.
- **Reminders:** when banking mode opens, Velivo shows bills due within a few days and overdue ones – every day until you mark them as paid.
- Velivo **does not connect to your bank** – it cannot know you paid. You mark payments with *Paid*.

**📊 Bills sheet**

An Excel-like overview of all paid bills, opened in a banking tab (local, offline). Button **📊 Bills sheet (in a tab)** in the *Bills to pay* window.

- **How to read it:** each **row** is a month, each **column** is a bill, a cell holds the amount paid that month. The **Total** column on the right = month total, the **Total** row at the bottom = total of each bill across all months, bottom-right = grand total.
- **Typing:** click a cell and type the amount – totals update at once. Months can be written as words or numbers: "October 2026", "październik 2026", "10.2026", "2026-10".
- **➕ Row (month)** – adds the month after the latest one in the table (after September 2026 → October 2026) and copies amounts from the last row (fixed bills need not be retyped).
- **➕ Column (bill)** – a new bill; click the header to rename it. **✕** under the name deletes the column, **✕** at the end of a row deletes the month.
- **📤 Load from Excel (CSV)** – in Excel: *File → Save as → CSV (comma/semicolon delimited)*. Layout: **first column = month**, **first row = bill names**, amounts inside. The separator (`;`, `,` or tab) is detected automatically. Columns and rows named *Total / Sum / Razem / Suma* are skipped (so totals are not counted twice). Months already in the sheet are filled in (empty cells in the file do not erase your amounts), new bills become new columns and rows are sorted chronologically. The number of loaded rows is shown afterwards.
- **Currency you type amounts in** – £, zł, €, $, CHF, kr, Kč, Ft, lei, ₴, ¥. It is only a **symbol** – amounts are **not converted** by exchange rate. Default comes from Windows regional settings; the choice is remembered on *Save and close*.
- **💾 Save and close** – the data is encrypted in the vault and the tab closes. Rows without amounts are skipped, as are rows without a valid month (Velivo tells you how many). **Cancel** closes without saving.
- **Link with Bills to pay:** the sheet and the *Paid* button share the same history. Every *Paid* shows up in the sheet in the due-date month, in the column with the same name.
- The older **📊 Payment summary** window shows the same data as a table, with manual adding/removing of payments and **📥 Save to Excel (CSV)** (export).

**Protection**

- **Fake sites:** while banking mode is open, a site that looks like your bank (e.g. another address with the bank's name) shows a big warning.
- **Bank in a normal tab:** when you open a listed bank, shop or payment site in a normal tab, a prompt above the taskbar asks **"Switch to banking mode"** / **"Stay here"**. Recognition works by the main domain, including other addresses of the same company.
- **📜 Access log** – when, on which computer and how the mode was opened (wrong passwords included).

**Profiles, backup and sync**

- **👤 Banking profiles** – a separate mode for another person: own password or key, separate logins and data.
- **💾 Back up** and **📂 Restore** – a `.vbank` file with all profiles, still encrypted (e.g. on a USB stick).
- **Sync** – banks, shops, cards, notes and mode settings go to paired computers in an encrypted package. Bank logins (cookies) stay on each computer.
- **Forgotten password:** without the password and the key the data can't be read. **"Forgot password – clear banking mode"** removes the mode and its data on this computer – so keep a second key and a backup.

---

## 17. Home network sync (no cloud)

### Pairing (once)

1. Install the same Velivo version on both computers.
2. Use **the same profile** on both (Settings → User profiles).
3. Tick "Enable sync between running Velivo". The Windows firewall must allow Velivo (port 41919).
4. Velivo asks: "Connect both computers…?" Choose **Yes**, or click "Pair a device on the network…".
5. Compare the short code on both screens and confirm.
6. Choose whose settings to keep.

From then on everything goes **encrypted, over your network, without the internet**.

### What syncs

| Data | How |
| --- | --- |
| Bookmarks | Merged from both; deleting works on both |
| Velivo passwords | Merged; for the same account the newer one wins |
| History (last 60 days) | Merged; deleting an entry, day or everything works on both |
| Pinned tabs | The latest change wins |
| Quick Access | Shortcuts and groups merged |
| Settings, privacy, profiles, extensions | The latest change wins; a fresh install never overwrites data |
| Language, folders, video window | **No** – each computer keeps its own |
| Other open tabs | **No** – instead right-click a tab → 📺 Send to… |

### 📺 Sending tabs

Right-click a tab → **📺 Send to… → computer name**. On the other computer the page opens in a new tab and the window comes to the front. Videos continue at the same moment.

The connection status is in Settings → **LAN diagnostics panel…**.

---

## 18. History and downloads

### History (Ctrl+H)

- Arranged **day → site → pages**, for example "Yesterday · 180 pages · 12 sites" and inside it "polsatnews.pl · 45 pages".
- Repeated visits are shown once with a ×3 note. Quick Access entries are hidden.
- **Right-click:** open, open in a new tab, open all pages of a site, delete an entry, site or day.
- Search by titles and addresses, plus the **🧠 Search page contents…** button.

### Downloads (Ctrl+J)

- **Velivo's manager:** up to 16 connections, pause and **resume after restart**, retry after a dropped connection.
- **Download history survives restarts.** Each file shows its size, date and time, and the site it came from.
- A deleted file is marked in gray: "File deleted or moved".
- Buttons: Open, 📁 Show in folder, Remove from list, Clear finished, Media on page.

### Torrents (optional)

Turn them on in **Settings → Torrents** (off by default). Then:
- **magnet links** (clicked or pasted into the address bar) and **downloaded .torrent files** are downloaded by the free **aria2** – fetched on first use with your consent (about 2.5 MB) and run only if its SHA-256 checksum matches;
- **Windows association:** the installer associates `.torrent` files and `magnet:` links with Velivo – double-clicking a file or a magnet link from another program starts the download (if torrents are off, Velivo tells you where to turn them on);
- **separate zone:** files go to their own folder (default `Downloads\Velivo-Torrenty`); with "Ask where to save" on you pick a folder for each torrent; files are marked as from the internet and Velivo opens nothing by itself;
- settings: zone folder, download and upload speed, seeding after download (ratio and time), number of peers;
- buttons in Downloads: Stop; while seeding – Open folder and Stop sharing; at the end – Show in folder (selects the file), Delete file (removes the downloaded file or torrent folder from disk after confirmation) and Remove from list;
- after a stop or an error Velivo asks whether to delete the partly downloaded files;
- **a page can't start a torrent by itself:** a magnet link without your click or a pushed `.torrent` file – Velivo asks first; torrents never start from Banking Mode; aria2 reads no foreign config and keeps its data only in Velivo's folder;
- when Velivo stays in the tray, torrents keep downloading after the window is closed; "Exit completely" stops them; torrents don't work in Banking Mode.

**Note:** in torrents other peers can see your IP address – use a VPN for anonymity. Share only material you have the rights to.

---

## 19. Extensions, profiles and tools

- **Chrome extensions:** install from the Chrome Web Store, from a link or from an unpacked folder.
  - the 🧩 button lists extensions with pins: pinned ones have an icon on the toolbar, unpinned ones keep working without the icon;
  - right-click an extension icon → Unpin from toolbar.
- **User profiles:** separate data per person or purpose, for example work or personal.
- **Screenshots:** the visible part or the whole page with scrolling.
- **Translation:** the whole page or a selection (Google Translate).
- **Detect media to download** on a page (Velivo tools).

### 🛡 uBlock Origin Lite (built in)

- Included in the installer and **enabled automatically** – no need to add it.
- Settings → "Security and downloads": a switch and a **"uBlock Origin Lite settings…"** button (filtering mode, lists, site exceptions).
- What it blocks adds to the **shield counter** and shows on its list. The shield tooltip shows "of which uBlock Origin Lite: N".
- **Updates itself** weekly from the developers' official GitHub. When the version is replaced, uBOL settings return to defaults.
- GPL-3.0 licence, details in `THIRD-PARTY.md`. If uBOL is already added from the store, Velivo keeps it (no duplicates).
- Add-on pop-ups fit their content, like in Chrome.

### Cache on a RAM disk

If you have a RAM disk (for example ImDisk, SoftPerfect RAM Disk or your own tool), Velivo can keep its page cache there. **You don't need any external tools**, symlinks or moved folders, as you would with Chrome. Just pick the folder in Velivo's settings.

1. Settings → **Junk folder (empty = in the browser profile)** → **Choose…** and pick a folder on the RAM disk, for example `R:\`.
2. Save the settings and restart Velivo.
3. Velivo creates a `Velivo-smieci` subfolder there and when cleaning up deletes **only its contents**, never anything else on the RAM disk.
4. The "Delete junk on every browser start" option clears it on every start.

**What you gain:**
- cached pages load from RAM, which is faster than from disk;
- your SSD gets fewer writes;
- the cache disappears by itself when you turn the computer off, which is a bonus for privacy.

**What it doesn't cover:** the graphics cache (`GPUCache`) is always kept by the Edge engine in the browser profile. This is an engine requirement, not Velivo's choice. The **Default** button in settings restores the normal location.

---

## 20. Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| Ctrl+T | New tab |
| Ctrl+Shift+N | New private tab |
| Ctrl+W | Close tab |
| Ctrl+Shift+T | Reopen closed tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+Shift+A | Search tabs |
| Ctrl+Shift+F | 🧠 Where did I read that? |
| Ctrl+O | 📂 Open a file – video (player) or PDF |
| Ctrl+L | Address bar |
| Ctrl+D | Bookmark |
| Ctrl+H | History |
| Ctrl+J | Downloads |
| Ctrl+Shift+U | Read page aloud / pause |
| F5 | Reload |
| F11 | Full screen |
| Alt+← / Alt+→ | Back / forward |
| Ctrl + mouse wheel | Zoom |

You can also do each of these with the mouse alone: a button, a gesture or the right-click menu.

---

### The Settings window

![Settings, part 1: import, search, privacy, look, uBlock Origin Lite and security](docs/zrzuty/ustawienia-1.png)

![Settings, part 2: junk on a RAM disk, data, profiles and sync](docs/zrzuty/ustawienia-2.png)

---

## 21. Settings – every option explained

Open the settings with the **⚙** button on the toolbar. Below is **every option in order**, as in the window.

### Default browser
- Status: "✓ Velivo is the default browser" or "Velivo is not the default browser right now".
- **Set Velivo as the default browser…** – registers Velivo with Windows and opens the default-apps window.
- **On first start** (also right after installing) Velivo asks once whether to become the default browser; "No" – it won't ask again.
- **PDF:** Velivo opens PDF files in its built-in viewer (zoom, search, print, save). The installer adds Velivo to "Open with" for `.pdf`; make it permanent in Default apps → Velivo.

### 📥 Import passwords, logins and bookmarks
- **🔑 Passwords and logins from a file** (CSV from KeePassXC, Chrome, Edge and other programs).
- **🌐 From Chrome / Edge / Brave / Opera** – bookmarks and passwords straight from an installed browser.

### Look and readability
- **Default page zoom** – for all pages; each page can also be zoomed separately (Ctrl + wheel) and Velivo remembers it.
- **Dark page mode** – pages with their own dark look switch to it, other pages are dimmed (photos keep their real colours).
- **Night mode** – warmer colours and less blue light (like Windows Night light). The page mode button (☀ / 🌙 / 🌅) cycles light → dark → night; change the strength with the mouse wheel on the button.
- **Content entrance effect** – how a new page appears on screen:
  - **Focus in** (default) – content emerges from a slight blur;
  - **From darkness (cinematic)** – the page brightens from a dark screen, like in a cinema;
  - **Gentle dim** – a short, subtle dim and back;
  - **None** – the page appears instantly.
- **Entrance effect speed** – slider from 0 to 5 s in 0.1 s steps (0 = automatic). Example values: quick 0.15 s, gentle 0.3 s, slow 0.5 s, calm 1 s, very calm 2 s, dreamy 3 s, slowest 4 s.
- **Browser theme** – colours of bars and tabs: Light, Graphite, Navy, Night violet, Forest, Ocean, Sunset, Black (OLED), Paper, Mist.
- **Style** – **Modern** (calm, like Windows 11: Windows icons, one blue accent) or **Colourful** (coloured buttons and emoji).
- **Interface language** – automatic (installer / Windows), Polish or English. Takes effect after a restart.
- **Always compact toolbar** – smaller labels, some buttons move into the "…" menu even on a wide window.

### Read aloud
- **Voice and speed** – automatic (by page language: Polish/English), Windows voices, natural online voices or **natural offline voices** (Piper – downloads about 60 MB, then works without internet).

### Tabs
- **Restore tabs from the previous session on start** – private tabs are never saved. Ctrl+Shift+T also restores a closed tab.
- **Open links in the same tab** – links a page wants to open in a new tab open in the current one (Back and Forward work). Ctrl+click still opens a new tab.

### Search and start
- **Search shortcuts** – e.g. "yt cats" searches YouTube; add your own in the window.
- **Search engine in the address bar**.
- **Mouse gestures** – hold the right button and move: ← back, → forward, ↑ new tab, ↓ close tab, ↓→ reload. A normal right-click still opens the menu.
- **"Picture in picture" button over videos** – hovering a video shows ⧉; the video moves to a small always-on-top window.
- **"Download" button over videos** – like Internet Download Manager: ⬇ Download over the video. Normal files go to the Velivo download manager (up to 16 connections), YouTube and streams to the free yt-dlp tool.
- **Start page** and **Quick Access as the new tab page** (separate Velivo data; Sejf's Quick Access in other browsers stays separate).
- **Extension folder for manual installation in other browsers** – a path to paste into "Load unpacked".

### Privacy
- **Send "Do Not Track" signals** (DNT and Global Privacy Control).
- **Tracking protection** – **balanced** (recommended: blocks known trackers, embedded content like X posts or videos works) or **strict** (also blocks embedded social content; on trusted domains it acts as balanced unless you tick "Force tracker blocking" for the domain) or **off – no tracking control** (the engine blocks no trackers; uBlock Origin Lite and site rules still work if enabled).
- **Save browsing history**.
- **Clear data on close** – history and cache; accounts stay logged in.
- **Logins from Sejf: key on the toolbar on login pages** – clicking the key fills the form with a Sejf login.
- **Offer to save passwords**.
- **Form autofill** – addresses and cards in a local, encrypted offline vault. **Show saved data…**, **Delete saved cards**, **Delete saved addresses**.
- **Local password manager…**, **Import passwords CSV…**, **Export passwords CSV…**.
- **Block pop-ups opened without a click**.
- **Automatically reject cookie banners (GDPR)** – Velivo clicks "Reject" / "Necessary only"; it never clicks "Accept". Exception for a site: right-click on the page.
- **Remember the content of pages you read** – the "Where did I read that?" search (Ctrl+Shift+F), only on this computer.
- **Warn about pressure tricks in shops** – fake timers, "last items", "X people are viewing", pre-ticked extras (Velivo unticks them), hidden fees.
- **Privacy receipt on the shield** – how many companies and countries the page contacted, data brokers, attempts to fingerprint the computer.

### Ad blocking
- **Full filter lists** (EasyList, EasyPrivacy, Polish list – about 97k rules). Downloaded in the background and refreshed every 4 days; **Update lists now** fetches them at once.

### Security and downloads
- **Warn about dangerous sites and files (SmartScreen)** – addresses are checked with Microsoft.
- **Ask where to save each download**.
- **Detect fake bank, shop and portal sites** – works offline (paypa1.com, ebay-verification.top, fakes of sites you have saved passwords for).
- **Always encrypted connection (HTTPS)** – warns about unencrypted sites.
- **Safe payments** – on bank and payment sites the window is invisible to screen-recording programs (no screenshots there either).
- **uBlock Origin Lite** – built-in ad blocker (recommended, can be turned off), **uBlock Origin Lite settings…**.
- **Velivo speakers** – choose a specific sound output instead of the Windows default. **Don't lose sound** – when a music program (Ableton, Cubase) takes the speakers, Velivo plays on another active output and returns when they are free. **🔊 Windows volume mixer…** – pin Velivo to your speakers permanently.
- **Stay in the system tray when the window is closed** – background sync and instant start; the icon by the clock pulses while syncing, right-click: Open, Sync now, Exit completely.
- **Connections per download** – 1 (no splitting) to 16; more is usually faster.

### Video player
- **Open video files in the Velivo player**, **Play right after opening**, **Resume where you left off** (this computer only), **Loop the video**. Details in chapter 8.

### Torrents
- **Download torrents (magnet and .torrent) in Velivo** – off by default; **Torrent zone folder** (Choose… / Default), **Download** and **upload speed**, **Share after download** (ratio, 0 = not at all) and **for how long**, **Most peers**. Details in chapter 8.

### Browser junk (cache)
- Page cache, compiled scripts and graphics cache – can be deleted without losing logins. **Junk folder** (e.g. on a RAM disk), **Default**, **Delete junk on every start**, **Currently uses: …**, **Clear junk now**.

### Page loading speed
- **Faster page opening** – Velivo starts loading a page when you hover a link and pre-connects to servers of visible links (off in private and banking tabs; better off on a data cap).
- **Cache size** – automatic or a chosen size (after a restart).

### Data
- **Clear browsing data now…** – tick: browsing history, cache, download history, cookies and sessions (logs you out), engine form and card data, passwords saved in the engine.
- **Per-site privacy → Privacy and anti-fingerprinting panel…** – domain rules: block JavaScript, send no cookies, force tracker blocking, clear data automatically on visit; a **trusted domains** list; a "What was blocked and why" log with full address and reason.

### User profiles
- Active profile (e.g. Work, Private) and **Manage users/profiles…** – add, switch, delete, custom profile icon. Each profile has its own logins, history and settings.

### Sync
- **E2E sync** – **Export / Import package…**: an encrypted file (password + AES-GCM) to move to another device.
- **Real-time sync (LAN)** – settings, bookmarks, passwords, privacy rules and Quick Access between home computers. **Pair a device on the network…** (compare a short code), **Save recovery file…** / **Restore pairing…** (password of at least 12 characters), **Quiet LAN mode** (no pop-ups), **LAN diagnostic panel…** (devices, status, log).

---

## 22. Troubleshooting and where data is stored

| Problem | What to do |
| --- | --- |
| A page blocks something it shouldn't | Privacy button → **Add to trusted**, or right-click → Restore blocked elements |
| A cookie banner is still shown | That banner has no "Reject" button. Velivo never clicks "Accept" |
| Computers don't see each other | Same profile on both, the firewall allows Velivo, automatic Windows time on both (a difference over 5 minutes blocks the exchange) |
| YouTube download fails | Wait a moment and try again. yt-dlp updates itself, and YouTube sometimes changes things |
| No icons in Quick Access | Open a new tab and wait a few minutes; "⋮" menu → Fill in missing icons |
| An offline voice doesn't read | Delete the `Piper` folder (below). It downloads again |
| Antivirus blocks the installer | The file has no paid code signature – add an exception |
| "An unexpected error occurred" | Velivo keeps running. Details are in `bledy.log` – attach it when reporting |

### Where data is stored

| What | Where |
| --- | --- |
| Program | `%LOCALAPPDATA%\Programs\Velivo` |
| Settings, bookmarks, passwords, history, page memory | `%LOCALAPPDATA%\Przegladarka` (other profile: `Profiles\<name>`) |
| Offline voices | `%LOCALAPPDATA%\Przegladarka\Piper` |
| yt-dlp and FFmpeg | `%LOCALAPPDATA%\Przegladarka\narzedzia` |
| Error log | `bledy.log` in the profile data folder |

All data stays on your computer. Velivo has no server and no account, and sends nothing about you.
