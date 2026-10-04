# Velivo – a new level of browsing

## Complete guide and user manual

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
16. [Home network sync (no cloud)](#16-home-network-sync-no-cloud)
17. [History and downloads](#17-history-and-downloads)
18. [Extensions, profiles and tools](#18-extensions-profiles-and-tools)
19. [Keyboard shortcuts](#19-keyboard-shortcuts)
20. [Troubleshooting and where data is stored](#20-troubleshooting-and-where-data-is-stored)

---

## 1. What makes Velivo different

Chrome, Edge, Firefox and Opera **do not have these features built in**. Some need separate extensions, some can't be done at all. In Velivo they work right away, with no add-ons and no account.

| Feature | What it does |
| --- | --- |
| **▣ Video on top** | Velivo's own video window you can shrink almost to an icon (from 160×90 px), with **transparency set by the mouse wheel** and an "always on top" pin. **It keeps playing after you close the tab – and even the whole browser.** |
| **🧠 "Where did I read that?"** | Finds an article you read by words from its **content**, not just its title. Everything stays on your computer. |
| **⚠ Shop-trick detector** | Warns about fake countdowns, "only 2 left" pressure and hidden fees, and unticks pre-selected add-ons in the cart. |
| **🧾 Privacy receipt** | Shows how many companies and countries the page connected to, which of them are data brokers and whether it tried to fingerprint your computer. |
| **📺 Send tabs at home** | The page opens on another computer running Velivo, and videos continue at the same moment. No cloud, no account. |
| **Sync without the cloud** | Bookmarks, passwords, history, pinned tabs, Quick Access and settings between home computers, encrypted. |
| **⬇ IDM-style video downloads** | A "Download" button over every video, YouTube included, with quality, MP3/M4A and folder choice. |
| **🔊 "Read from here"** | Right-click a paragraph and Velivo reads from that sentence. |
| **🌅 Night mode with strength** | Like Windows "Night light", adjusted with the mouse wheel. |
| **🖱 Gestures and mouse-only use** | Back, forward, new tab, close, reload and zoom without a keyboard. |

---

## 2. How small it is and why

| Item | Size |
| --- | --- |
| **Installer** `Velivo-Setup-1.22.exe` | **about 6.7 MB** |
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
| **🌙** | Page mode: light → dark → night. In night mode the mouse wheel sets the strength |
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

### Mouse wheel on buttons

| Where | Action |
| --- | --- |
| Zoom button (e.g. 125%) | Zooms the page in and out, remembered per site |
| Mode button 🌙 in night mode | Warmth strength 5–100% |
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

Transparency needs Windows 10 version 1809 or newer, because the window uses a Windows component to draw the picture.

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
| **Private tabs** | Separate, isolated data that disappears when closed |
| **Clearing data** | On exit or by hand in settings |

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
- **Page modes** (🌙 button):
  - light;
  - **dark** – pages darkened while photos keep their true colors;
  - **night** – warm colors with less blue light, **strength set with the mouse wheel**.
- **Zoom** remembered per site, changed with the wheel on the percent button or with Ctrl+wheel.
- **Phone version** for a chosen site (right-click), remembered.
- **Language:** Polish or English (Settings → Look → Language).

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
- **Autofill** for addresses and cards in a local, encrypted store.

---

## 16. Home network sync (no cloud)

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

## 17. History and downloads

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

---

## 18. Extensions, profiles and tools

- **Chrome extensions:** install from the Chrome Web Store, from a link or from an unpacked folder.
  - the 🧩 button lists extensions with pins: pinned ones have an icon on the toolbar, unpinned ones keep working without the icon;
  - right-click an extension icon → Unpin from toolbar.
- **User profiles:** separate data per person or purpose, for example work or personal.
- **Screenshots:** the visible part or the whole page with scrolling.
- **Translation:** the whole page or a selection (Google Translate).
- **Detect media to download** on a page (Velivo tools).

### Cache on a RAM disk

If you have a RAM disk (for example ImDisk, SoftPerfect RAM Disk or your own tool), Velivo can keep its page cache there.

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

## 19. Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| Ctrl+T | New tab |
| Ctrl+Shift+N | New private tab |
| Ctrl+W | Close tab |
| Ctrl+Shift+T | Reopen closed tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+Shift+A | Search tabs |
| Ctrl+Shift+F | 🧠 Where did I read that? |
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

## 20. Troubleshooting and where data is stored

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
