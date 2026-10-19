# VELIVO 1.22 — COMPLETE GUIDE AND FEATURE CATALOGUE

# How to read this handbook

The handbook describes Velivo 1.22 from the user's point of view: what you can do, where it is and how to use it. Every feature has its own number (e.g. **7.4**) and a fixed layout:

- **What for** – why the feature exists.
- **Where** – the button, menu or place in Settings.
- **How to use** – step-by-step actions.
- **What it does** – what exactly happens.
- **Limits** – what the feature doesn't do or what it depends on.

Features that were discussed but are not in version 1.22 are marked **NOT AVAILABLE IN 1.22**. At the end there is the "Full list of Velivo 1.22 features".

Velivo 1.22 runs on Windows 10 (version 1809 or newer) and Windows 11, 64-bit. Pages are displayed by the Microsoft Edge engine (WebView2), so they look and work as in Edge and Chrome. Velivo needs no account and no cloud.

# 1. First start

## Installation

### 1.1 The Velivo-Setup-1.22.exe installer
- **What for:** installs Velivo on the computer.
- **Where:** the `Velivo-Setup-1.22.exe` file (the `Instalator` folder in the repository).
- **How to use:** 1) run the file; 2) if Windows shows "Unknown publisher", click "More info" → "Run anyway"; 3) choose the installer language: Polski or English – Velivo will start in that language; 4) tick whether you want a desktop shortcut; 5) click "Install" and finally, optionally, "Launch Velivo".
- **What it does:** installs the program to `%LOCALAPPDATA%\Programs\Velivo` (no administrator rights), adds a Start menu shortcut and registers Velivo in Windows as a browser.
- **Limits:** the files have no paid digital signature, hence the "Unknown publisher" warning.

### 1.2 Checking system components
- **What for:** Velivo needs .NET 10 (built into the installer) and Microsoft Edge WebView2 Runtime.
- **How to use:** nothing to do – the installer checks both components itself.
- **What it does:** .NET 10 is included in the installer (about 60 MB), so you don't need to install it. If WebView2 is missing, the installer downloads it from Microsoft and installs it. WebView2 is in Windows 11 and current Windows 10.
- **Limits:** without WebView2 the browser cannot display pages.

### 1.3 Update or clean install
- **What for:** installing a new version over an older one.
- **How to use:** when the installer finds data from an earlier installation, choose **"Keep my settings and data (update)"** – recommended – or **"Clean install – start from scratch"**.
- **What it does:** with a clean install, settings, bookmarks, history, browser passwords and Quick Access are moved to a backup (a folder with a "kopia-…" suffix) and Velivo starts as new.

### 1.4 No-install version
- **What for:** running Velivo without installing it, e.g. from a USB stick.
- **How to use:** run `Velivo-1.22.exe` (a single file) or unpack `Velivo-1.22-portable.zip` and run `Velivo.exe`.
- **Limits:** to set it fully as the default browser, the installer is better.

### 1.5 Silent install and uninstall (advanced)
- **How to use:** `Velivo-Setup-1.22.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LANG=en /DIR="C:\folder"`; uninstall: `unins000.exe /VERYSILENT` in the program folder or Windows Settings → Apps.

### 1.6 Checking that the file is genuine
- **What for:** confirming that the downloaded installer hasn't been changed.
- **How to use:** in PowerShell `Get-FileHash .\Velivo-Setup-1.22.exe -Algorithm SHA256`.
- **What it does:** compare the result with the SHA-256 checksum published together with the release you downloaded.

## First steps

### 1.7 First opening
- **What it does:** Velivo opens a new tab with Quick Access, the built-in ad blocker (uBlock Origin Lite) and default privacy settings. The full ad-blocking lists download in the background.
- **Limits:** the first start may take a little longer (creating the engine profile).

### 1.8 Setting Velivo as the default browser
- **Where:** Settings → "Default browser" → "Set Velivo as the default browser…".
- **How to use:** click the button, choose Velivo in the Windows "Default apps" window and "Set default".
- **What it does:** links from other programs and `.html` files open in Velivo. The state is shown in Settings ("✓ Velivo is the default browser").
- **First start:** right after installation Velivo asks once whether to make it the default browser; "No" – the question won't come back.
- **PDF:** PDF files open in Velivo's built-in reader (zoom, search, print, save); the installer adds Velivo to "Open with" for `.pdf`, permanently – in "Default apps" under Velivo.
- **Limits:** Windows doesn't let a program set itself – the last click is yours.

### 1.9 Import from your previous browser
- **Where:** Settings → "Import passwords, logins and bookmarks".
- **How to use:** "From Chrome / Edge / Brave / Opera" – bookmarks are moved straight away; for passwords Velivo opens that browser's password page, where you export a CSV file and load it with the "Passwords and logins from a file (CSV…)" button.
- **What it does:** described in detail in chapter 23.

### 1.10 Program language
- **Where:** Settings → Look → "Interface language / Language".
- **How to use:** choose Automatic (language from the installer / Windows), Polski or English and restart Velivo.
- **What it does:** translates all windows, menus, tooltips and Quick Access.

### 1.11 All in one – no extra programs
- **What it means:** Velivo replaces several separate programs: a **video player** (chapter 28 – library, TV, subtitles, narrator; plus picture in picture and "Video on top"), a **PDF reader** (1.8) and **torrents** (8.19). You don't need to install or update them separately.
- **How to use:** double-click a file or right-click → "Open with" → Velivo; permanently – Settings → Video player → "Open videos and PDFs in Velivo (Windows)…".

# 2. Interface

## Main window

### 2.1 Tab bar
- **What for:** switching between open pages.
- **Where:** the top of the window.
- **How to use:** click – switch; ✕ or the middle mouse button – close (a pinned tab can't be closed with the middle button); right-click – tab menu (chapter 4); "+" – new tab. At the end of the bar is the green banking-mode button.
- **What it does:** private tabs are marked, banking tabs are green, pinned tabs are at the start without ✕, tabs playing sound show a speaker, and auto-refreshed ones a refresh badge.

### 2.2 Toolbar
- **Where:** below the tab bar.
- **Buttons (from the left):** new private tab, new tab, Back, Forward, Reload, home page, address bar, active profile, bookmark star, privacy shield, dark/night mode, read aloud (with speed and stop), Reader, screenshot, extensions, downloads, history, settings, "More tools".
- **What it does:** every button has a tooltip with its name and keyboard shortcut.

### 2.3 Compact and adaptive toolbar
- **What for:** a readable toolbar even in a narrow window.
- **Where:** works by itself; Settings → Look → "Always compact toolbar".
- **What it does:** in a narrow window labels shrink and less-used buttons move to the "…" menu. "Always compact" forces this layout in a wide window too.

### 2.4 Address bar
- **What for:** typing addresses and searching.
- **How to use:** type an address or words and press Enter; Ctrl+L moves the cursor to the address bar.
- **What it does:** text that isn't an address goes to the chosen search engine; search shortcuts are supported (5.8).

### 2.5 Active privacy rule indicator
- **Where:** next to the shield ("Active privacy rule for this domain"), the label "Privacy*".
- **What it does:** shows that your own rule is active for the open site (chapter 12).

### 2.6 The "➕ Add to Velivo" button in the extension store
- **Where:** appears on the toolbar when you are on an extension page in the Chrome Web Store.
- **What it does:** downloads the extension from the store and installs it (chapter 22).

### 2.7 Notification bubbles
- **What for:** short messages that don't cover the page.
- **Where:** at the bottom centre of the window, just above the Windows taskbar (never on it), also with a maximised window.
- **What it does:** tells you about saving, downloading, syncing, reminders etc. Some have buttons (e.g. "Open", "Show in folder").

### 2.8 Full screen
- **How to use:** F11 – on/off; Esc – leave full screen. A video on a page switched to full screen works as in other browsers.

### 2.9 One program window
- **What it does:** when Velivo is already running and you open a link from another program (e.g. from e-mail), the link opens as a new tab in the existing window instead of starting a second Velivo. The window is restored if it was minimised.

### 2.10 Error resilience
- **What it does:** an unexpected error doesn't close the browser. Velivo shows a message (at most one every 30 seconds for the same error) and writes the details to `bledy.log` in the data folder. A page that tries to close the window (`window.close`) closes at most its own tab, never the whole of Velivo.

# 3. Mouse and keyboard control

Velivo is designed so that it can be used entirely with the mouse, even from the sofa in front of the TV.

### 3.1 Mouse wheel on the zoom button
- **Where:** the zoom-percentage button on the toolbar.
- **How to use:** hover and turn the wheel.
- **What it does:** zooms the page in or out and remembers it for that site.

### 3.2 Mouse wheel on the night-mode button
- **How to use:** with night mode on, hover the moon button and turn the wheel.
- **What it does:** changes the warmth strength from 5% to 100%.

### 3.3 Mouse wheel over the video window
- **What it does:** changes the transparency of the "Video on top" window from 15% to 100% (chapter 7).

### 3.4 Clicking links
- **A normal click** – opens in the same tab (Back and Forward work).
- **The middle button or Ctrl+click** – always a new tab.

### 3.5 Right-click menu on a page
- **What for:** quick access to tools for the page, a selection, a video or a link.
- **What it contains (depending on where you click):** Search for the selected text; Translate the selection into Polish; Read the selection aloud; Read from here; Translate the page into Polish / English; Reader mode and summary; Where did I read that?; Video on top (small window); Picture in picture; Download video…; Block element (ad)…; Restore blocked elements on this site; Reject / Don't reject cookie banners on this site; Show the phone version of this site / Back to the desktop version; Screenshot (visible part / whole page); Add to Quick Access; Velivo tools (Privacy and anti-fingerprinting, Detect media to download, Download manager, LAN sync diagnostics, Extensions, History, Search tabs, profile switch).

### 3.6 Right-click menu on a tab
- **What it contains:** Reload, Duplicate tab, Pin/Unpin tab, Mute tab / Unmute tab, Auto-refresh, Add to group, Remove from group, Tab sets, Send to…, Open as private (incognito), Close tab, Close other tabs, Close tabs to the right, Restore closed tab.

### 3.7 Keyboard shortcuts
| Shortcut | Action |
|---|---|
| Ctrl+T | new tab |
| Ctrl+Shift+N | new private tab |
| Ctrl+W | close tab |
| Ctrl+Shift+T | restore closed tab |
| Ctrl+Tab | next tab |
| Ctrl+Shift+A | search tabs |
| Ctrl+L | go to the address bar |
| Ctrl+D | add / remove bookmark |
| Ctrl+H | history |
| Ctrl+J | downloads |
| Ctrl+Shift+F | "Where did I read that?" |
| Ctrl+O | open a file from disk – video (player) or PDF |
| Ctrl+Shift+U | read the page aloud / pause / resume |
| Alt+← / Alt+→ | back / forward |
| F5 | reload |
| F11, Esc | full screen / exit |
| Ctrl + / Ctrl − / Ctrl 0, Ctrl+wheel | zoom in / out / default |

# 4. Tabs and windows

### 4.1 New tab
- **How to use:** Ctrl+T, "+" on the tab bar or the mouse gesture up.
- **What it does:** opens Quick Access (or the page set as the new tab page).

### 4.2 Private tab (incognito)
- **How to use:** Ctrl+Shift+N, the private-tab button on the toolbar or "Open as private" from the tab menu.
- **What it does:** uses a separate, isolated store (InPrivate); saves no history, session or content for "Where did I read that?". Everything disappears when it is closed.

### 4.3 Duplicating a tab
- **Where:** tab menu → "Duplicate tab".

### 4.4 Closing tabs
- **How to use:** ✕, Ctrl+W, the gesture down, a middle click on the tab (not for pinned ones); tab menu: "Close other tabs", "Close tabs to the right".

### 4.5 Restoring a closed tab
- **How to use:** Ctrl+Shift+T or tab menu → "Restore closed tab". Repeat to restore more.

### 4.6 Pinned tabs
- **What for:** sites kept permanently open (e-mail, calendar).
- **How to use:** tab menu → "Pin tab"; unpinning – "Unpin tab".
- **What it does:** the tab moves to the start of the bar, has no ✕ and comes back on every start. With LAN sync pinned tabs also appear on the other computer.

### 4.7 Tab groups
- **What for:** order with many tabs (e.g. "Work", "Shopping").
- **How to use:** 1) tab menu → "Add to group" → "New group…" → name; 2) more tabs: "Add to group" → group name; 3) clicking the group label collapses or expands it; 4) right-click the label: "Rename group…", "Color" (8 colors), "Ungroup (tabs stay)", "Close all tabs in the group"; 5) "Remove from group" in the tab menu.
- **What it does:** groups with their color, name and collapsed state come back after a restart.

### 4.8 Tab sets
- **What for:** saving a whole set of tabs and opening it later with one click.
- **How to use:** tab menu → "Tab sets" → "Save open tabs as a set…" → name (e.g. Work). Later "Tab sets" → name → "Open all tabs" or "Replace current tabs"; "Delete set".

### 4.9 Auto-refreshing a tab
- **What for:** pages that don't update by themselves (scores, rates, an order queue) or sign you out after inactivity.
- **How to use:** tab menu → "Auto-refresh" → Off / 1 / 5 / 15 / 30 min.
- **What it does:** the tab reloads at the chosen interval; the tab shows a badge with the tooltip "Refreshing every N min".
- **Limits:** the setting is not remembered after a restart.

### 4.10 Searching tabs
- **How to use:** Ctrl+Shift+A (or Velivo tools → Search tabs) → type part of a title or address → arrows and Enter. Esc closes.

### 4.10c Menu of the empty tab bar and the "+" button
- **How to use:** right-click "+" or an empty spot on the tab bar.
- **What it does:** new tab, new private tab, restore closed tab, Tab sets (open a saved one, save the current ones), Tab groups (expand / collapse), search tabs. A normal click on "+" opens a new tab straight away.

### 4.10a Split screen (two tabs side by side)
- **How to use:** right-click a tab → "◫ Show side by side (split screen)"; drag the middle bar with the mouse, clicking the page next to it makes it active. Exit: "Close split screen" or close one of the tabs.

### 4.10b Sleeping unused tabs
- **Where:** Settings → Tabs → "Put unused tabs to sleep" (off by default; 15 / 30 min, 1 / 2 h).
- **What it does:** a tab you haven't looked at for a long time goes to sleep (less memory and CPU) and wakes up when clicked. The tab you are viewing, pinned tabs and tabs playing something (music, video, read aloud) don't sleep.

### 4.11 Muting a tab
- **How to use:** tab menu → "Mute tab" / "Unmute tab".

### 4.12 Links in the same tab
- **Where:** Settings → Tabs → "Open links in the same tab".
- **What it does:** links a page wants to open in a new tab open in the current one – Back and Forward work. Ctrl+click still opens a new tab. Off: as in other browsers.

### 4.13 Restoring the session
- **Where:** Settings → Tabs → "Restore tabs from the previous session on start".
- **What it does:** after start the tabs, the active tab and groups come back. Private tabs are never saved; with "Clear data on close" the session isn't saved either.

### 4.14 Send a tab to another computer
- **Where:** tab menu → "Send to…". Described in chapter 17.

### 4.15 Page zoom
- **How to use:** Ctrl+wheel, Ctrl +/−/0, the wheel on the percent button; the default zoom for all sites – Settings → Look.
- **What it does:** zoom is remembered separately for each site and carried over to other tabs with the same site.

### 4.16 Phone version of a site
- **What for:** sites that are heavy or full of ads on a computer and simpler in their mobile version.
- **How to use:** right-click → "Show the phone version of this site"; back – "Back to the desktop version of this site".
- **What it does:** the tab presents itself to the site as an Android phone; the choice is remembered for the site.

### 4.17 Screenshot
- **How to use:** the camera button or right-click → "Screenshot" → "Visible part of the page" or "Whole page (with scrolling)".
- **What it does:** saves a PNG in `Pictures\Zrzuty Velivo`, copies it to the clipboard and shows a bubble with "Open" and "Show in folder". "Open the screenshots folder" opens the folder.
- **Limits:** on bank and payment sites screenshots are blocked (13.5).

### 4.18 Closing and minimising Velivo
- **Closing the window:** closes Velivo and saves the session. If downloads are running, Velivo asks whether to close.
- **When "Video on top" is playing:** closing the main window doesn't stop the video – the video window keeps playing on its own (7.10).
- **Stay in the tray:** this option hides Velivo to the icon by the clock instead of closing (25.17).
- **Minimising:** as usual; Velivo comes back from the taskbar, and opening a link from another program or clicking a notification restores the window.

# 5. Mouse gestures and search

### 5.1 Mouse gestures
- **What for:** navigation without looking for buttons.
- **Where:** on by default; Settings → Search and start → "Mouse gestures (right button + movement)".
- **How to use:** hold the **right button**, move the mouse about 3 cm and release.

| Movement | Action |
|---|---|
| ← left | Back |
| → right | Forward |
| ↑ up | New tab |
| ↓ down | Close tab |
| ↓ then → | Reload |

- **What it does:** a normal right-click without moving opens the menu as always. A page can't fake a gesture – the message to Velivo carries a secret launch token.
- **Pinned tab:** the ↓ gesture doesn't close it (just like ✕). The turn in the ↓ then → gesture counts from about 15 px, so "Reload" doesn't accidentally become closing the tab.

### 5.2 Search engine in the address bar
- **Where:** Settings → Search and start → "Search engine in the address bar".
- **How to use:** choose from the list, e.g. Startpage (default, private with Google results), DuckDuckGo, Brave Search.

### 5.3 Search shortcuts
- **What for:** searching a specific service straight away.
- **Where:** Settings → Search and start → "Search shortcuts (e.g. "yt cats")…".
- **How to use:** in the address bar type the shortcut, a space and the query, e.g. `yt cats` – YouTube results. In the shortcuts dialog you can add your own (shortcut + address with a place for the query).

### 5.4 Searching the selected text
- **How to use:** select text → right-click → "Search …". The results open in a new tab.

### 5.5 Home page
- **Where:** Settings → Search and start → "Home page"; the house button on the toolbar.

### 5.6 Quick Access as the new tab page
- **Where:** Settings → Search and start → "Quick Access as the new tab page". Quick Access is described in chapter 21.

# 6. Animations and look

### 6.1 Content entrance effect
- **What for:** new pages appear smoothly and pleasantly.
- **Where:** Settings → Look and readability → "Content entrance effect".
- **How to use:** choose one of the effects:
  - **Focus-in** (default) – content emerges from a slight blur;
  - **From darkness (cinematic)** – the page brightens from a dark screen, as in a cinema;
  - **Gentle dimming** – a short, subtle dimming and back;
  - **None** – the page appears at once.
- **What it does:** after clicking a link the page dims slightly and the new content appears with the chosen effect. The effect covers the whole page window, including the background and pinned menus.

### 6.2 Entrance effect speed
- **Where:** Settings → Look and readability → "Entrance effect speed".
- **How to use:** a slider from 0 to 5 s (in 0.1 s steps); 0 = automatic. Examples: fast 0.15 s, gentle 0.3 s, slow 0.5 s, calm 1 s, very calm 2 s, sleepy 3 s, slowest 4 s.

### 6.3 Look style: Modern and Colorful
- **Where:** Settings → Look → "Look style".
- **What it does:** **Modern** (default, like Windows 11) – calm buttons without colored backgrounds, single-color Windows 11 icons, one blue accent, color only on hover or when something is on; the active tab is in bold; icons in the right-click menus too. **Colorful** – colored buttons and emoji.

### 6.4 Browser themes
- **Where:** Settings → Look → "Browser theme (colors of bars and tabs)".
- **What it does:** 10 themes: Light, Graphite, Navy, Night violet, Forest, Ocean, Sunset, Black (OLED), Paper, Mist. They change the colors of the tab bar, toolbar and bookmarks.

### 6.5 Dark mode for pages
- **What for:** comfortable reading in the evening.
- **Where:** the page-mode button (the icon shows the current mode: ☀ light, 🌙 dark, 🌅 night); Settings → Look → "Dark mode for pages".
- **How to use:** click the button (light → dark → night).
- **What it does:** sites with their own dark look switch to it; other light sites are darkened and **photos keep their real colors**. The mode is remembered **separately for each site**.
- **Each site has its own mode:** one site can be light, another dark, a third night – Velivo remembers it per site, also after a restart.
- **Opening a page in dark mode:** the tab background is dark before the page is drawn, and the entrance effect (6.1) goes through a dark mist – no white flash.
- **Limits:** if the engine started in another mode, Velivo fixes the page or offers a restart (tabs come back).

### 6.6 Night mode (warmer colors)
- **Where:** the same button – the third click; Settings → "Night mode – warmer page colors (like Night light in Windows)".
- **How to use:** the mouse wheel on the button sets the strength 5–100%.
- **What it does:** a warm, semi-transparent layer over the page – less blue light. It doesn't get in the way of clicking.

### 6.7 Default page zoom
- **Where:** Settings → Look and readability → "Default page zoom". You can also zoom each site separately (4.15).

# 7. Video and "Video on top"

When you hover over a video, three buttons appear in its top right corner: **⬇ Download · ▣ On top · ⧉ Picture in picture**. This works on YouTube too.

### 7.1 Video on top – opening
- **What for:** watching a video in a small, separate window that can stay above other programs.
- **Where:** the **▣ On top** button over the video or right-click → "Video on top (small window)".
- **How to use:** click ▣. The video in the tab pauses and continues in the window from the same moment.
- **What it does:** creates a **separate Windows window** (with its own place on the taskbar) that contains only the video.

### 7.2 Clean video view
- **What it does:** the window shows just the video over its whole area – no menus, comments, tooltips, pop-ups or banners. On YouTube it looks like a separate player. Ad and tracker blocking works as in the tab, and YouTube video ads are skipped.

### 7.3 Moving the window
- **How to use:** grab the dark bar at the top of the window and drag.

### 7.4 Resizing the window
- **How to use:** grab an edge or a corner and drag.
- **Limits:** the smallest size is 160×90 pixels of picture.

### 7.5 Window transparency
- **What for:** watching and working at the same time – you can see the text under the video.
- **How to use:** hover over the window and turn the **mouse wheel**; the bar shows e.g. "Visibility 60%".
- **What it does:** changes the transparency of the whole window together with the video from 15% to 100%.
- **Limits:** needs Windows 10 version 1809 or newer.

### 7.6 Always on top (pin)
- **How to use:** click the 📌 pin on the window bar.
- **What it does:** **blue** – the window is always above other programs; **white** – a normal window that goes under others when you click elsewhere.

### 7.7 Pause and resume
- **How to use:** click the video.

### 7.8 Compact player in the window (seeking, volume, full screen)
- **What it does:** when you hover at the bottom of the window a bar appears: ▶/❚❚ pause, ◀◀ 10 s back, ▶▶ 10 s forward, a time bar (click / drag, the wheel over it ±5 s), 🔊 mute, a volume slider (the wheel over it ±5%) and ⛶ full screen. It works the same for YouTube, other sites and videos from disk.
- **Keyboard** (after clicking the window): Space – pause, ← → – 10 s, ↑ ↓ – volume, M – mute, F – full screen, Esc – leave full screen. Double-click the video – full screen.
- **Small window:** the bar simplifies itself – first it hides the volume slider and time, in the smallest also ±10 s.

### 7.9 Back to the page (↩)
- **How to use:** click ↩ on the window bar.
- **What it does:** goes back to the page in Velivo at the same moment of the video – **even if the browser was closed** (Velivo starts on that page). On YouTube the position is passed in the address.

### 7.10 Watching with the browser closed
- **What for:** music, a podcast or a match without keeping the whole browser open.
- **How to use:** open Video on top, then close the tab – or even the whole main Velivo window.
- **What it does:** the video window is a separate window and **keeps playing after the tab and the main Velivo window are closed**. The program then runs only for the video window, so it uses less memory (LAN sync and the Sejf connection are stopped then). Closing the window with ✕ ends the program. **The "Stay in the tray" option is not needed.**
- **Limits:** when you start Velivo again (with the icon or a link) while only the video window is playing, Velivo closes the window and starts the full browser window.

### 7.11 Remembering the window settings
- **What it does:** position, size, transparency and pin are remembered and used the next time it opens. If the remembered position doesn't fit the current screen (e.g. a disconnected monitor), the window appears by the right edge of the screen.

### 7.12 Closing the window
- **How to use:** ✕ on the window bar. Closing the window stops the video.

### 7.13 Examples of use
- music or a podcast in a small, semi-transparent window in a screen corner while working;
- a match or a live stream over a document or a spreadsheet – set visibility to 40–60% and the text underneath stays readable;
- a video tutorial next to the program in which you follow the steps;
- the video plays even after Velivo is closed, so the browser uses no memory.

### 7.14 Picture in picture (PiP)
- **Where:** the **⧉** button over the video (Settings → "Picture in picture button over videos") or right-click → "Picture in picture".
- **What it does:** the Edge engine's standard picture-in-picture window, always on top. From the menu the largest video on the page is chosen. The window stays even after the tab is closed.
- **Limits:** unlike "Video on top" there is no transparency control and no remembered position. Videos in frames (e.g. embedded YouTube) get the ⧉ button on hover.

### 7.15 Muting a video in a tab
- Tab menu → "Mute tab" (4.11).

### 7.16 Videos from disk
- **Where:** double-click a video file (Velivo is added to "Open with"), Ctrl+O, right-click → Velivo tools → "📂 Open a file from disk".
- **What it does:** with the optional VLC-like player installed, the file opens in the Velivo player (chapter 28 – library, TV, subtitles, narrator). Without it MP4 and WebM play in a browser tab, offline, with ⧉ Picture in picture and ▣ Video on top.
- **Controls in a tab:** the player bar; Space / click – pause, ← → – 5 s, ↑ ↓ – volume, F – full screen, M – mute.
- **Settings:** the "Video player" section (25.26) – opening in Velivo, autoplay, resuming where you left off (this computer only), looping.
- **Privacy:** the position in the video is saved under an encrypted key (SHA-256 with Velivo's random salt) – other HTML files from the disk can't read which videos you watch.
- **Limits:** in a tab MP4 (H.264) and WebM work best; an unsupported codec (e.g. HEVC/H.265) shows a message – the VLC player plays it.

# 8. Downloading files, videos and music

## Download manager

### 8.1 Velivo download manager
- **What for:** fast and reliable file downloads, as in Internet Download Manager.
- **Where:** the downloads button on the toolbar, Ctrl+J.
- **What it does:** Velivo downloads the file itself and shows a list with progress bars, speed and state. The Edge engine's built-in download bubble is hidden.

### 8.2 Downloading with multiple connections
- **Where:** Settings → Security and downloads → "Connections per downloaded file".
- **How to use:** choose from 1 (no splitting) to 16. More connections usually means faster.
- **Limits:** splitting works for files from 2 MB when the server supports downloading in parts.

### 8.3 Pause and resume
- **How to use:** the "Pause" and "Resume" buttons next to the file.
- **What it does:** resumes from where it stopped. If the server doesn't allow resuming, Velivo says the file will start over.

### 8.4 Automatic retrying
- **What it does:** after a dropped connection Velivo retries by itself up to 5 times, with longer and longer breaks, from where it stopped. If the server sends no data for 60 s, the download stops with a message and a "Resume" button.

### 8.5 Downloading files while signed in
- **What it does:** Velivo passes the page's cookies to the download and handles redirects itself, so downloading from accounts, e-mail and clouds you are signed in to works.

### 8.6 Fallback download through the engine
- **What it does:** a file created by a page script (which can't be taken over) is downloaded by the Edge engine; it appears in the same list marked "(downloaded by the browser engine)".

### 8.7 Asking where to save
- **Where:** Settings → "Ask where to save each downloaded file". Without this option files go to the Downloads folder.

### 8.8 Download list
- **How to use:** "Open" – runs the file; "Show in folder"; "Cancel" – asks and deletes the downloaded part; "Open the Downloads folder".
- **What it does:** the list is remembered after a restart.

### 8.9 Warning when closing
- **What it does:** when you close Velivo during a download, it asks whether to close (files downloaded by the engine will be interrupted).

### 8.10 The "file from the internet" mark
- **What it does:** every downloaded file gets the Windows "downloaded from the internet" mark. Thanks to it SmartScreen warns when a program is run, and Word and Excel open the document in Protected View (macros blocked). The page address is not stored in the file.
- **Limits:** on FAT32/exFAT USB sticks the mark can't be written.

## Videos and audio

### 8.11 The ⬇ Download button over a video
- **Where:** when hovering over a video; Settings → "Download button over videos"; right-click → "Download video…".
- **What it does:** a plain video file goes to the Velivo manager (up to 16 connections). YouTube, streams and other services are handled by the free **yt-dlp** tool.

### 8.12 Choosing quality and format
- **How to use:** after clicking ⬇ choose:
  - **Best quality** – picture up to 1080p in MP4 with sound (needs FFmpeg);
  - **Simple** – one file without merging (no FFmpeg);
  - **Audio only M4A** – no extras;
  - **Audio only MP3** – highest quality (needs FFmpeg).
- Choose the folder (remembered) and confirm. Progress is shown in the download list, with cancelling.

### 8.13 yt-dlp – downloaded once
- **What it does:** on the first YouTube download Velivo asks to download yt-dlp (about 18 MB from GitHub). The tool updates itself every 2 weeks, because services change often.

### 8.14 FFmpeg – downloaded once
- **What it does:** for best quality and MP3 Velivo asks to download FFmpeg (about 140 MB to download, about 130 MB unpacked). Without it Velivo downloads in simple mode.

### 8.15 Checking the downloaded tools
- **What it does:** before using yt-dlp and FFmpeg Velivo checks their SHA-256 checksum from the official release. A file that doesn't match is deleted and never run.

### 8.16 Detect media to download
- **Where:** Velivo tools (right-click or "More tools") → "Detect media to download".
- **What it does:** shows a list of audio and video sources on the page with "Download", "Download as audio" and "Show" buttons.
- **Limits:** `blob:` sources (temporary data in the page's memory) are not files; use the ⬇ button over the video instead.

### 8.17 Downloading subtitles for films
- **Where:** the video player and library – right-click → "💬 Download subtitles…" (described in 28.4).
- **Limits:** subtitles are not downloaded with YouTube videos (⬇ Download).

### 8.18 Downloading from YouTube in "Detect media" mode – a limit
- On YouTube Velivo doesn't offer downloading via "Detect media" – use the ⬇ Download button (yt-dlp).

### 8.19 Torrents (magnet and .torrent) – turned on in Settings
- **Where:** Settings → the **Torrents** section (off by default). Progress – in the Downloads window (Ctrl+J).
- **What it does:** a magnet link (clicked or pasted into the address bar) and a downloaded `.torrent` file are downloaded by the free **aria2** program – Velivo fetches it on first use, with consent (about 2.5 MB), and runs it only if the SHA-256 checksum matches.
- **Windows association:** the installer associates `.torrent` files and `magnet:` links with Velivo – double-clicking a file or a link from another program starts the download straight away. If torrents are off, Velivo tells you where to turn them on. If another program opens `.torrent`, choose Velivo in "Open with" or in "Default apps".
- **Separate zone:** the default folder `Downloads\Velivo-Torrenty` (changeable in Settings). With "Ask where to save the file" on, you choose the folder for each torrent. Downloaded files get the "file from the internet" mark (8.10); Velivo opens nothing by itself.
- **Settings:** download and upload speed, seeding after download (up to what ratio and for how long – 0 = no seeding), number of peers.
- **Buttons in the Downloads window:** during – "Stop"; after downloading, while seeding – "Open folder" and "Stop seeding"; at the end – "Show in folder" (selects the downloaded file), "Delete file" (deletes the downloaded file or torrent folder from disk, after confirmation, never the whole download folder) and "Remove from list" (the file stays on disk).
- **Interrupted download:** after stopping or an error Velivo asks whether to delete the partly downloaded files from disk (with a list of names). It deletes only unfinished files of that torrent.
- **Security:** a page can't start a torrent by itself (a magnet without a click, a slipped-in `.torrent` file – a question first); torrents never start in banking mode; aria2 doesn't read foreign settings files and keeps its network data (DHT) only in the Velivo folder.
- **After closing the window:** when Velivo runs in the tray, torrents keep downloading; only "Close completely" (right-click the icon by the clock) stops them. Without the tray, closing Velivo stops torrents.
- **Limits:** torrents don't work in banking mode. Other peers see your IP address – use a VPN for anonymity. Share only material you have the right to.

# 9. Reader and reading pages

### 9.1 Reader mode (Reader)
- **What for:** reading an article without ads, menus and distractions.
- **Where:** the "Reader" button on the toolbar; right-click → "Reader mode and summary".
- **What it does:** opens just the article text in a separate, comfortable window. The window size is remembered.

### 9.2 Reader look
- **How to use:** the "Reader look" list: ☀ Light, 🌙 Dark, 🌅 Night; in night mode the "Night mode strength" slider. **A− / A+** – text size (remembered).
- **What it does:** Velivo colours (paper background, violet); the reader remembers where you left off in an article and takes you back there (stored on the computer only, without page addresses); the title is read once and summary boxes on the page are skipped.

### 9.3 Local summary
- **What for:** quickly getting the gist of a long article.
- **What it does:** Velivo picks the article's most important sentences **on your computer – without artificial intelligence and without sending content to the internet** (keywords, title words and the start of the article count).
- **Quality:** the "In short" box contains only full, self-contained sentences – no subheadings, torn-out quotes or sentences that depend on the previous one ("As he said…"). If there are none, there is no box.
- **Finding the article:** works also when the page hides content in an unusual block (posts from X, videos) – no "not enough content" message.

### 9.4 Reading a page aloud
- **Where:** the read-aloud button on the toolbar; Ctrl+Shift+U.
- **What it does:** reads the page's main content; the paragraph being read is highlighted and scrolled to the middle of the screen, easy to follow.
- **Content as in Reader:** reading aloud and Reader use the same article detection – only the article is read, without side columns, recommendations and extras.
- **Voice by paragraph language:** in mixed text (e.g. a Polish article with an English quote) each paragraph is read with a voice in its own language.
- **Reacts to changes:** changing the voice or speed in Settings works straight away, without restarting reading; when the page moves to another article (e.g. the next text without reloading), reading stops instead of reading outdated content.

### 9.5 Reading a selection
- **How to use:** select text → the read button or right-click → "Read the selection aloud".

### 9.6 Reading from a chosen place
- **How to use:** right-click in the text → "Read from here" – no selecting needed.

### 9.7 Pause, resume, stop
- **How to use:** click the read button (or Ctrl+Shift+U) – pause / resume; "Stop reading" – stop.

### 9.8 Reading speed
- **How to use:** the speed button next to the read button – each click changes the pace.

### 9.9 Choosing a voice
- **Where:** Settings → "Read aloud – voice and speed".
- **What it does:** "Automatic (page language: Polish/English)" matches the voice to the page language; you can also choose Windows voices, natural online voices and **natural offline Piper voices** (chapter 10).

### 9.10 "Where did I read that?"
- **What for:** finding an article you read by words remembered from its content – not just the title.
- **Where:** Ctrl+Shift+F; right-click → "Where did I read that?"; turning on – Settings → Privacy → "Remember the content of pages you read".
- **How to use:** type a few remembered words → choose a result with the arrows → Enter opens the page.
- **What it does:** Velivo stores the text of pages you read **only on this computer** and searches it locally.
- **Limits:** private tabs, banks, payments, e-mail and every page with a password field are skipped.
- **What it stores:** only the page's main content (no menus, ads and link lists); search result pages (Google, Bing, DuckDuckGo…) are skipped so the results don't clutter the memory.

### 9.11 Translating pages and selections
- **Where:** right-click.
- **What it does:** "Translate the page into Polish" (a foreign page) or "into English" (a Polish page) and "Translate the selection into Polish" – via Google Translate.
- **Limits:** translation needs the internet and sends the content to Google.

# 10. Offline reading – Piper voices

### 10.1 Natural offline voices
- **What for:** reading pages aloud with a natural voice **without the internet**.
- **Where:** Settings → Read aloud → choose a voice marked "natural offline".
- **What it does:** on first choice Velivo downloads the Piper program and the chosen voice once (about 60 MB per voice), with progress in a bubble. After that it reads completely offline: the text is split into sentences and the sentence being read is highlighted.

### 10.2 Piper voices available in 1.22
| Voice | Language |
|---|---|
| Gosia | Polish |
| Darkman | Polish |
| MC Speech | Polish |
| Lessac | English (US) |
| Ryan | English (US) |
| Alba | English (UK) |
- **Limits:** version 1.22 has **6 offline voices (3 Polish and 3 English)**. A variant with 8 voices – **NOT AVAILABLE IN 1.22**.

### 10.3 Security of the downloaded Piper program
- **What it does:** the downloaded Piper program is checked with a SHA-256 checksum stored in Velivo; a mismatching file is rejected.

### 10.4 Where the voices are
- `%LOCALAPPDATA%\Programs\Velivo\Dane\Piper` – you can delete this folder to free space; the voice downloads again the next time it is used.

# 11. Privacy

### 11.1 Privacy shield
- **Where:** the shield button on the toolbar.
- **What it does:** shows the number of blocked items on the page and the "Blocked on this page" list, and after a click the privacy receipt (11.8).

### 11.2 Velivo ad blocking
- **Where:** the "Turn AdBlock on/off" button; Settings → Ad blocking.
- **What it does:** blocks ads and trackers at the level of whole domains before they load. The built-in rule set works from the first start.

### 11.3 Full filter lists
- **Where:** Settings → Ad blocking → "Full filter lists (EasyList, EasyPrivacy, Polish list) – about 97k rules".
- **What it does:** the **EasyList** (ads), **EasyPrivacy** (tracking) and **Polish** lists download in the background and refresh every 4 days. "Update lists now" downloads them at once. Settings show the number of lists and rules in use.
- **Limits:** if the download fails, Velivo uses the previous lists.

### 11.4 Your own blocking rules (advanced)
- **How to use:** add domains or EasyList-format rules to `filters.txt` in the data folder and restart Velivo.

### 11.5 uBlock Origin Lite (built in)
- **Where:** Settings → Security → "uBlock Origin Lite – built-in ad blocker (recommended)", "uBlock Origin Lite settings…".
- **What it does:** the popular open-source blocker (GPL-3.0) works together with Velivo's blocking; its results show on the shield. Once a week Velivo checks for a new release and installs it only if the SHA-256 checksum given by GitHub matches.
- **Limits:** it can be turned off; if it was installed earlier from the store, that version stays (no duplicates).

### 11.6 Blocking page elements
- **What for:** floating ads, banners and pop-ups the filters missed.
- **How to use:** 1) right-click → "Block element (ad)…"; 2) hover – the element is highlighted; 3) the mouse wheel or arrows change the scope (a bigger / smaller part); 4) a click or Enter blocks; Esc cancels.
- **What it does:** the element disappears on every visit to that site. A bubble tells you how to undo it.
- **Undo:** right-click → "Restore blocked elements on this site". The number of hidden elements is shown on the shield.

### 11.7 "Do Not Track" and Global Privacy Control
- **Where:** Settings → Privacy → "Send "Do Not Track" signals (DNT and Global Privacy Control)".
- **What it does:** sites get a request not to sell or track your data.
- **Limits:** sites don't have to respect this request.

### 11.8 Privacy receipt
- **What for:** to see what the page does "behind the scenes".
- **Where:** click the shield; Settings → "Privacy receipt on the shield".
- **What it does:** shows how many outside companies and how many countries the page connected to, whether data brokers were among them, the most frequent companies and **attempts to identify the computer (fingerprinting)**.

### 11.9 Fingerprinting – detection and report
- **What it does:** Velivo **detects and reports** attempts to identify the computer: reading an invisible image (canvas), reading the graphics card model (WebGL) and an audio fingerprint. The results show in the privacy receipt.
- **Limits:** Velivo **doesn't change the values read** – it doesn't add noise or normalise the browser fingerprint. Protection comes from: tracker blocking via the lists (fingerprinting sites are often on them), domain rules and hiding that Velivo is an "embedded browser".

### 11.10 Automatic cookie-banner rejection
- **Where:** Settings → Privacy → "Automatically reject cookie banners (GDPR)".
- **What it does:** Velivo clicks "Reject" or "Only necessary" itself. If the banner has no such button, nothing is clicked – **Velivo never clicks "Accept"**. The rejection shows in the shield log.
- **Exception for a site:** right-click → "Don't reject cookie banners on this site" (back – "Reject banners…").

### 11.11 Pop-up blocking
- **Where:** Settings → "Block pop-ups opened without a click".

### 11.12 History – saving and clearing
- **Where:** Settings → Privacy → "Save browsing history" and "Clear data on close (history and cache)".
- **What it does:** clearing on close doesn't sign you out of accounts and doesn't delete saved logins.

### 11.13 Clear browsing data
- **Where:** Settings → Data → "Clear browsing data now…".
- **How to use:** tick: Browsing history, Cache, Download history, Cookies and active sessions (signs out of accounts), Form and card data saved by the engine, Passwords saved in the engine → "Clear".

### 11.14 The "What was blocked and why" log
- **Where:** shield → "Privacy and anti-fingerprinting panel".
- **What it does:** a list of blocks: time, domain, reason (e.g. "Tracker blocked (AdBlock)", "Tracker blocked (domain rule)", "Cookies removed for the domain (rule)"). Selecting an entry shows the full address and details. "Clear the block panel".

### 11.15 Hiding the "embedded browser"
- **What it does:** Velivo presents itself to sites as a normal Microsoft Edge (the same engine). Thanks to this, e.g. Google sign-in works and sites don't know it is an "embedded browser".

### 11.16 Shop pressure-trick detector
- **Where:** Settings → Privacy → "Warn about pressure tricks in shops".
- **What it does:** recognises fake countdown timers, "last items", "X people are watching", pre-ticked extras (Velivo **unticks** them) and hidden fees, and shows a warning.

# 12. Protection levels and site rules

### 12.1 Tracking prevention – balanced
- **Where:** Settings → Privacy → "Tracking prevention" → "Balanced protection (recommended)".
- **What it does:** blocks known trackers, while embedded content (e.g. posts from X, videos) works.

### 12.2 Tracking prevention – strict
- **What it does:** also blocks embedded content from social networks.
- **Limits:** on trusted domains strict works like balanced unless you tick "Force tracker blocking" for the domain.

### 12.2a Tracking prevention – off (no control)
- **Where:** Settings → Privacy → "Tracking prevention" → "Off – no control".
- **What it does:** the engine doesn't block trackers – for those who prefer sites without any interference. uBlock Origin Lite and site rules keep working if they are on. Balanced protection remains the default.

### 12.3 Privacy panel and domain rules
- **Where:** shield → "Privacy and anti-fingerprinting panel"; Settings → Data → "Per-site privacy".
- **How to use:** 1) type a domain (e.g. `example.com` – you can paste a whole address, only the domain is kept); 2) tick the chosen options (12.4–12.7); 3) "Save rule". "Delete rule" removes it.
- **What it does:** the rule applies on every visit to the domain and is synced with your computers. When a site has a rule, the toolbar shows "Privacy*".

### 12.4 Rule: Block JavaScript for the domain
- **What it does:** the site works without scripts – faster and without script-based tracking.
- **Limits:** many modern sites don't work properly without JavaScript.

### 12.5 Rule: Don't send cookies for the domain
- **What it does:** the site doesn't get your cookies.
- **Limits:** you won't be signed in on that site.

### 12.6 Rule: Force tracker blocking for the domain
- **What it does:** strict tracker blocking even on a domain marked as trusted.

### 12.7 Rule: Automatically clear data after visiting the domain
- **What it does:** after you visit the site its data (cookies etc.) is deleted – the site always sees you as a new visitor.

### 12.8 Trusted domains
- **How to use:** in the panel select an entry in the block log → "Selected domain to trusted", or add a domain to the "Trusted domains" list.
- **What it does:** when blocking makes a site work badly, trusting it restores it.

### 12.9 Security levels – summary
| Level | What to turn on |
|---|---|
| Normal | default settings: balanced protection, AdBlock, uBOL, banner rejection, fake-site detection, SmartScreen, HTTPS |
| Higher | strict protection, "Do Not Track", clearing data on close, privacy receipt |
| Highest for one site | domain rule: block JavaScript, no cookies, force trackers, clear data after visiting |
| Banks and payments | banking mode (chapter 14) + safe payments |
| No trace | private tab |

# 13. Security

### 13.1 Fake-site detection (offline)
- **Where:** Settings → Security → "Detect fake bank, shop and portal sites (works offline)".
- **What it does:** before a page opens, Velivo checks the address on your computer and recognises:
  - **a brand on a foreign domain** – e.g. `paypal-secure-login.com`, `ebay.co.uk.konto.top` (PayPal, eBay, Amazon, Facebook, Instagram, WhatsApp, Google, Gmail, Microsoft, Outlook, Apple, iCloud, X/Twitter, Netflix, Spotify, banks and others);
  - **typos and swapped characters** – `paypa1.com`, `rnicrosoft.com`, `amaz0n.com`;
  - **letters from other alphabets** – `xn--` (punycode) addresses that look like real ones;
  - **fakes of sites you have saved passwords for**.
- **How to use:** a warning "Careful – this may be a fake site!" appears with an explanation of which company the address imitates. The default answer is **No**. If it is the real site, choose "Yes" – Velivo will remember it.

### 13.2 SmartScreen
- **Where:** Settings → "Warn about dangerous sites and files (SmartScreen)".
- **What it does:** Microsoft's protection against phishing and malicious files.
- **Limits:** checking sends addresses to Microsoft.

### 13.3 Always encrypted connection (HTTPS)
- **Where:** Settings → "Always use an encrypted connection (HTTPS) – warn about unencrypted sites".
- **What it does:** unencrypted sites open only after a warning.

### 13.4 Passwords only for the real domain
- **What it does:** saved passwords are given only to a site with the same domain they were saved for – never because of a similar name. A fake site won't get the password.

### 13.5 Safe payments – screen-recording block
- **Where:** Settings → "Safe payments – hide the window from screen-recording programs on bank and payment sites".
- **What it does:** on bank and payment sites (PayPal, Monzo, Revolut, Barclays, HSBC, Lloyds, NatWest, Santander, PKO, mBank, Stripe and others) the Velivo window is **invisible to screen-recording programs** and screenshots; a 🛡 message appears.
- **Limits:** you can't take a screenshot on these sites either.

### 13.6 Payment data protection
- **What it does:** the card's CVC is **never saved** in autofill; before filling in a card and a bank account Velivo asks and shows the site address.

### 13.7 Attack-resistant forms
- **What it does:** Velivo fills in only fields really visible to a person (a site can't extract data with a hidden field), only real mouse clicks count, and a site can't swap Velivo's helper.

### 13.8 A closed channel between the page and Velivo
- **What it does:** pages have no access to Velivo's internal channel. Commands (e.g. gestures, saving a password) are accepted only with a secret launch token the page doesn't know.

### 13.9 The "file from the internet" mark and tool checking
- Described in 8.10 and 8.15.

### 13.10 Protection against a page closing the window
- Described in 2.10.

# 14. Banking mode

### 14.1 What banking mode is
- **What for:** safe banking and shopping in a separate, closed environment.
- **What it does:** a separate, isolated browser profile with **its own encrypted database**: its own sign-ins and cookies, **no extensions and no history**. Normal tabs see nothing of it and vice versa. Banking tabs are **green** and show a 🏦 icon.

### 14.2 The 🏦 button and the mode menu
- **Where:** the green 🏦 button on the tab bar. Click – opens the mode; **right-click – the menu** with all the features described below.

### 14.3 First start of the mode
- **How to use:** 1) click 🏦; 2) set a **password** (min. 8 characters) and repeat it; 3) optionally add a hardware key (14.4); 4) choose the automatic lock time; 5) "Save".

### 14.4 FIDO2 hardware key (YubiKey, Google Titan)
- **What for:** stronger protection – a physical key is needed to open it.
- **How to use:** tick "Also require a hardware key", insert the key, click "➕ Add key (insert it and touch it)" – Windows asks for a touch **twice**. Add all your keys in the same dialog (a spare is worth having), then "Save". Windows finds NFC/Bluetooth keys after clicking "Add".
- **What it does:** a key marked 🔐 **opens the mode by itself and encrypts the database** – without it the data can't be read even if the files are copied.
- **Limits:** the key must support FIDO2 with an ES256 signature and the hmac-secret extension.

### 14.5 Opening the mode
- **What it does:** key in the port → Velivo asks for a touch right away, no password needed. No key → you type the password.

### 14.6 Automatic lock
- **What it does:** after inactivity (1–60 min to choose, default 10 min) the mode locks itself and closes all its windows.

### 14.7 Lock now
- **Where:** 🏦 menu → "Lock now".

### 14.8 Clean-up after closing the mode
- **What it does:** after the mode closes, the banking profile's cache and history disappear. Sign-ins and "remember me" stay.

### 14.9 Database encryption
- **What it does:** the mode's data (banks, shops, cards, notes, documents, accounts, bills, logins, payment history) is encrypted with strong AES-256 with a key derived from the password (600,000 rounds of PBKDF2) and optionally from the hardware key.

### 14.10 My banks
- **Where:** 🏦 menu → "My banks".
- **How to use:** clicking a name opens the bank's site in banking mode. Adding: on an open banking tab "Add this site to My banks" or on the list "Add a bank (name and address)…".

### 14.11 My online shops
- The same as 14.10, menu "My online shops" and "Add this site to My shops".

### 14.12 Bank login details
- **Where:** in "My banks" – "Login details".
- **What it does:** stores separately for each bank: login / customer number, passcode / PIN, password and memorable information. Always enter the **full** password and passcode.

### 14.13 Fill in login
- **What it does:** fills in the login and password on the bank's site from that bank's data.

### 14.14 Fill in selected characters
- **What for:** banks that ask e.g. for the 2nd, 5th and 9th character of the password (RBS, NatWest, Bank of Scotland, TSB, Lloyds, Halifax).
- **Where:** 🏦 menu → "Fill in selected characters (passcode / password)".
- **What it does:** Velivo reads from the form which characters the bank asks for and enters the right ones – also in drop-down lists.

### 14.15 Copy the form description
- **Where:** 🏦 menu → "Copy the login form description (without your data)".
- **What for:** when filling in fails on some site – copies only the field description (no data) for a problem report.

### 14.16 My cards
- **Where:** 🏦 menu → "My cards".
- **How to use:** "Add card": name (e.g. "Visa PKO"), number (checked), valid until (MM/YY), name on the card, CVV (optional, 3–4 digits). Clicking a card shows its details and lets you change them; "Show number and CVV" reveals the hidden fields; "Copy number / date / CVV" – the clipboard clears itself after 30 s.
- **What it does:** cards are encrypted. When the mode opens Velivo reminds you about cards expiring soon.

### 14.17 Fill in the card on this page
- **Where:** 🏦 menu → "Fill in the card on this page".
- **What it does:** enters the card details in the payment form.

### 14.18 My logins and passwords (in the mode)
- **Where:** 🏦 menu → "My logins and passwords".
- **What it does:** for any services (e-mail, Netflix, government sites): name, login, password, site address, note. "Open site"; "Fill in on the open page" – **only on a site with the same address**, so the password won't go to a fake; "Generate password" – strong, 20 characters, copied (clipboard cleared after 30 s).

### 14.19 Confidential data (documents)
- **Where:** 🏦 menu → "Confidential data (documents)".
- **What it does:** type (ID card, Passport, Driving licence, PESEL, NI number, Health insurance, Other), full name, number (hidden), valid until, note. Velivo **reminds you about documents expiring within 60 days**.

### 14.20 Bank accounts
- **Where:** 🏦 menu → "Bank accounts".
- **What it does:** your accounts and payees' accounts (e.g. a landlord): name, owner, number / IBAN (hidden), sort code / BIC (SWIFT), bank, transfer reference. A 📋 by each field – error-free copying into a transfer.

### 14.21 My notes
- **Where:** 🏦 menu → "My notes (logins, passwords, customer numbers)".
- **How to use:** choose a category (Login, PIN, Transfers, Recovery codes, Other), type a title and content, "Save". "Generate password", "Numbered characters" (the password character by character with numbers), 📋 copying.
- **What it does:** the notes window doesn't block the page – you can copy into the bank's form.

### 14.22 Fill in login and password (data linked to the site)
- **Where:** 🏦 menu → "Fill in login and password on this page"; on a site from your database – by itself.
- **What it does:** takes the accounts from My logins and passwords and from My banks for this site. One account – the login, password and selected characters fill in by themselves (Velivo doesn't submit the form); several accounts – a choice with one click. With no accounts – a list of notes (`login: …` and `hasło: …` lines).
- **Settings:** "Fill in data from the database automatically…" in Banking mode settings (synced).
- **Isolation:** only the banking-mode database – normal browser passwords aren't used here; data goes only to banking tabs and only to the site it was chosen for.
- **🛡 Transfer guard:** a pasted / typed account number – the IBAN / NRB checksum and a comparison with Bank accounts (payee from the database / warning / wrong number).
- **💸 Fill in a transfer:** the 🏦 menu or a suggestion on the transfer form – payee, number (NRB, IBAN, sort code + 8 digits), reference; amount and customer number from the Bill to pay with the same name. Always after a click.
- **💬 Suggestion under the field** – you click the login, password, selected characters, card number or payee account number: when the database has matching data for this site, a choice appears right under the field (one click fills it); nothing matches – nothing appears. The site doesn't see your account names.
- **Menu with matching data only** – the fill-in items in the 🏦 menu appear only when the database has data for the open site.
- **Safe filling** – the full password never goes into one-character fields (only the selected characters go there), and card details are entered only into visible fields – hidden page fields get nothing.

### 14.23 Search my database
- **Where:** 🏦 menu → "Search my database…".
- **What it does:** one field for banks, shops, cards and notes; the window doesn't block the page.

### 14.24 Bills to pay
- **What for:** keeping track of regular and one-off payments.
- **Where:** 🏦 menu → "Bills to pay".
- **Fields:** What for (a list of names from the sheet), Amount, Due date, Repeat, Remind days before, Customer number / reference, Payment page, Note.
- **How to use:** fill in the fields → "Add as new"; "Change selected", "Delete selected", "Clear fields"; finally **"Save"** (only then do the changes go into the encrypted database).

### 14.25 Recurring bills
- **What it does:** repeat: Weekly, Every 2 weeks, Every 4 weeks, Monthly, Every 2 months, Quarterly, Every half year, Yearly, One-off – or type your own, e.g. "every 10 days", "every 3 weeks".

### 14.26 Due dates and reminders
- **What it does:** when the mode opens Velivo shows bills due within a few days and overdue ones – **every day until you mark them as paid**.

### 14.27 Marking as paid
- **How to use:** select the bill → "Paid (next due date)".
- **What it does:** the payment goes to the history (the due month) and to the sheet, the due date moves according to the repeat, and a one-off bill disappears from the list.
- **Limits:** Velivo doesn't connect to the bank – it doesn't know by itself that you paid. Two clicks in the same month add up the amount (correct it in the sheet).

### 14.28 Open the payment page
- **What it does:** opens the address from the "Payment page" field in a banking tab.

### 14.29 Bill names from the sheet
- **What it does:** the "What for" field suggests column names from the sheet – choose from the list and "Paid" goes to the right column. A different name (even a typo) creates a new column.

### 14.30 Add bills from the sheet
- **What it does:** creates the missing bills from the sheet's columns: the amount from the last month, monthly repeat, due on the 1st of the next month. **Then correct the due day** and click "Save".

### 14.31 Bills sheet
- **What for:** an Excel-like summary of expenses.
- **Where:** the 🏦 menu or "Bills to pay" → "Bills sheet (in a tab)".
- **What it does:** row = month, column = bill; the "Total" column = the month's sum, the "Total" row = the bill's sum, the grand total in the corner – calculated live. The sheet works locally, offline.

### 14.32 Sheet – entering amounts and months
- **How to use:** click a cell and type the amount. Enter the month in words or numbers: "październik 2026", "October 2026", "10.2026", "2026-10".

### 14.33 Sheet – a new month
- **How to use:** "Row (month)" – adds the month after the latest in the table (October after September) and copies the amounts from the last row (no need to retype regular payments).

### 14.34 Sheet – bills (columns)
- **How to use:** "Column (bill)" – a new column; rename it by clicking the header; ✕ under the name deletes the column; ✕ at the end of a row deletes the month.

### 14.35 Sheet – import from Excel (CSV)
- **How to use:** in Excel: File → Save as → CSV; in the sheet "Load from Excel (CSV)". File layout: **first column = month, first row = bill names**.
- **What it does:** detects the separator (semicolon, comma, tab); skips "Razem/Suma/Total" columns and rows; fills in existing months (empty cells don't erase your amounts); adds new bills as columns; sorts the months; shows the number of rows loaded.

### 14.36 Sheet – currency
- **How to use:** the "Currency you enter amounts in" list: £, zł, €, $, CHF, kr, Kč, Ft, lei, ₴, ¥.
- **What it does:** changes **only the symbol** – amounts **are not converted** at any rate. By default the currency from the Windows regional settings; the choice is remembered on saving.
- **Limits:** currency conversion in the sheet – **NOT AVAILABLE IN 1.22**.

### 14.37 Sheet – saving
- **How to use:** "Save and close" – the data is encrypted in the database and the tab closes; rows without amounts or without a valid month are skipped (with a notice). "Cancel" – no saving.
- **What it does:** the sheet accepts data only from itself – if you open another page in its tab, Velivo finishes without saving.

### 14.38 Payment summary
- **Where:** "Bills to pay" → "Payment summary".
- **What it does:** a window with a table of months and bills: adding a payment manually (bill, month, amount), "Delete this bill's payments for the month", "Load from Excel (CSV)…", "Save to Excel (CSV)" (file `Velivo-rachunki.csv`), currency choice.

### 14.39 Warning about a site resembling your bank
- **What it does:** when, with the mode open, you visit a site resembling your bank (e.g. another address with the bank's name), a big warning appears.

### 14.40 Banking-mode suggestion
- **What it does:** when you open a bank, shop or payment site from the list in a **normal** tab, the question "Switch to banking mode" / "Stay here" appears above the taskbar. Recognition works by the main domain, also for other addresses of the same company.

### 14.41 Opening log
- **Where:** 🏦 menu → "Opening log…".
- **What it does:** when, on which computer and how (password / key) the mode was opened – including **failed attempts with a wrong password**.

### 14.42 Banking profiles
- **Where:** 🏦 menu → "Banking profiles" → "New banking profile (another user)…".
- **What it does:** a separate banking mode for another person (e.g. a household member): their own password or key, separate sign-ins and data.

### 14.43 Banking mode settings
- **Where:** 🏦 menu → "Banking mode settings…": a new password (empty = no change), adding and removing keys ("Delete selected", "Delete all keys"), lock time.
- **Fill in data from the database automatically** – on a site from the database (one matching account) the login, password and selected characters fill in by themselves; off = everything after a click. A payment card – always after a click.

### 14.44 Forgotten password
- **Where:** the mode's sign-in window → "I forgot the password – clear banking mode…".
- **What it does:** without the password and the key the data can't be read – this option deletes the mode and its data on this computer (sign-ins, cookies), and you set it up again. That's why a backup and a second key are worth having.

### 14.45 The mode's guide in the program
- **Where:** 🏦 menu → "Banking mode guide" – a full description in Polish or English.

# 15. Sejf (external password manager)

### 15.1 Logins from Sejf on sign-in pages
- **What for:** using passwords saved in the Sejf program without opening its window.
- **Where:** Settings → Privacy → "Logins from Sejf: key on the toolbar on sign-in pages".
- **What it does:** when a page has a password field, Velivo asks Sejf for logins **for the tab's real domain**. A key appears on the toolbar; clicking it fills the form. Before filling, Velivo checks that the tab is still on that site.
- **Limits:** needs the Sejf program installed (Settings show "(Sejf not found)" when it is missing). Sejf may ask to be unlocked – Velivo waits up to a minute.

### 15.2 The Sejf popup in Quick Access
- **What it does:** a popup with a list of Sejf entries: "Choose an entry and fill in the form", saving a new login in the encrypted vault ("Saving the entry in the encrypted vault…", "Login saved in the vault").

### 15.3 Filling forms from Sejf
- **What it does:** the same filling logic as in Velivo: only visible fields, only the right domain ("Form filled in. Check the data and sign in.").

# 16. Passwords and data

### 16.1 Velivo password manager
- **Where:** Settings → Privacy → "Local password manager…".
- **What it does:** a local password database encrypted with your Windows account key. Columns: Name, Domain, User, Source, Changed; an entry counter.

### 16.2 Searching passwords
- **How to use:** the "Search by domain, login or name" field.

### 16.3 Adding and editing an entry
- **How to use:** "Add" / "Edit": domain, user, password, name, notes; "Show/hide password", "Paste from clipboard", "Generate a strong password".

### 16.4 Strong password generator
- **What it does:** a random password with lowercase and uppercase letters, digits and symbols (cryptographic randomness).

### 16.5 Copying and deleting
- **How to use:** "Copy login", "Copy password", "Delete selected", "Delete all saved passwords" (with confirmation).

### 16.6 Offer to save a password
- **Where:** Settings → "Offer to save passwords".
- **What it does:** after signing in Velivo asks "A password was detected on … Add it to the Velivo database?" or, when the password changes, "Update the entry?".

### 16.7 Filling in a saved login
- **What it does:** on a sign-in page "Fill in the saved login and password from Velivo"; with several accounts "Choose an account to fill in" or "Another account from the Velivo database…" with search.
- **Security:** only for the same domain (13.4).

### 16.8 Form autofill
- **Where:** Settings → "Form autofill (addresses and cards, local encrypted database)".
- **What it does:** clicking an empty field fills the whole form: **address** (first name, last name, street, postcode, city, phone, e-mail), **card** (no CVC), **bank account** (IBAN, account number, sort code). Works on any https site, also in private tabs. Card and account are filled in only after you confirm the site address.

### 16.9 Saving form data
- **What it does:** after a form is sent Velivo asks "A filled-in address form was detected" / "Payment card details were detected" – saving always only with your consent.

### 16.10 Managing autofill data
- **Where:** Settings → "Show saved data…", "Delete saved cards", "Delete saved addresses".

# 17. LAN Sync – home network sync

### 17.1 Sync without the cloud
- **What for:** the same bookmarks, passwords and settings on several computers at home – without an account or cloud.
- **Where:** Settings → Sync → "Turn on sync between running Velivo instances on the same local network".
- **What it does:** computers with the same Velivo profile on one network exchange data directly. Open tabs stay separately on each computer.
- **Limits:** the Windows firewall must allow Velivo; Windows time set automatically on both computers.

### 17.2 Before pairing – no data
- **What it does:** until the computers are paired, Velivo only tells the network it is available for pairing (identifier, computer name, profile). **It neither sends nor accepts any of your data.** The diagnostic panel shows "LAN sync waiting for pairing – no data is sent".

### 17.3 Connection proposal
- **What it does:** when a second Velivo with the same profile appears on the network, one of the computers asks: "There is another Velivo on the network… Connect both computers and sync everything?".

### 17.4 Pairing devices
- **How to use:** 1) turn on and save sync on both computers; 2) on one click "Pair a device on the network…" (or accept the proposal); 3) the same **6-digit code** appears on **both screens** – compare and confirm on both; 4) done.
- **What it does:** the computers agree on a secret key without sending it in the clear; the code protects against another device impersonating one. The key is stored locally and protected with the Windows account key.
- **Limits:** requests from another profile are rejected, unfinished pairing expires.

### 17.5 First connection – whose settings
- **What it does:** on the first connection you choose whose **settings** stay. Bookmarks, passwords and Quick Access are merged from both anyway.

### 17.6 Sync encryption
- **What it does:** every packet is encrypted (AES-256) and signed; a computer without the key can neither read nor fake the data. Old packets or ones with a different time (a difference of more than 5 minutes) are rejected – replay protection.

### 17.7 What syncs
| Data | How |
|---|---|
| Settings | the newer change wins; local ones stay: speakers, folders, language, window sizes |
| Bookmarks | merged from both; deletions carried over (for 90 days) |
| Passwords | merged |
| Privacy rules, profiles | the newer change wins |
| Extensions | missing ones installed automatically from the store |
| Pinned tabs | opened and pinned |
| History | merged; deletions and "clear all" carried over |
| Quick Access with images | merged, sent over an encrypted connection |
| Banking mode (all profiles) | encrypted; bank sign-ins stay local |
| Open tabs | **no** – use "Send to…" for that |

### 17.8 Profile isolation in sync
- **What it does:** only computers with **the same profile** sync. Profile A's data never goes to profile B. When the other computer switches to another profile, Velivo asks whether to switch too.

### 17.9 Send a tab to another computer
- **Where:** tab menu → "Send to…" → choose the computer.
- **What it does:** the tab opens on the other computer; a YouTube video starts from the same moment. The transfer is encrypted.

### 17.10 Tray indicator
- **What it does:** the Velivo icon by the clock **pulses** while sending and receiving changes.

### 17.11 Sync notifications
- **What it does:** bubbles "Synced with Velivo on the local network" and details (settings accepted / newer here, bookmarks merged).

### 17.12 Quiet mode
- **Where:** Settings → Sync → "LAN quiet mode (no bubbles…)". The log and the panel keep working.

### 17.13 LAN diagnostic panel
- **Where:** Settings → "LAN diagnostic panel…"; Velivo tools → "LAN sync diagnostics".
- **What it does:** status (off / active but no connected devices / connected to N devices), detected devices, packet and error counters, times of the last exchange, an event log; "Refresh now", "Clear log".

### 17.14 Warnings and errors
- **What it does:** Velivo tells you about a clock difference ("the clock differs by N min – set automatic time"), a data packet that is too big, an incomplete state and connection errors.

### 17.15 Pairing recovery file
- **Where:** Settings → Sync → "Save recovery file…" / "Restore pairing…".
- **What it does:** saves a file with the pairing key encrypted with a password (min. 12 characters). After reinstalling Windows you restore the pairing without pairing again.

### 17.16 Stay in the tray – syncing in the background
- Described in 25.17.

# 18. Profiles and user isolation

### 18.1 Velivo user profiles
- **What for:** a separate environment for each person or use (e.g. "Work", "Private", a second Google account).
- **Where:** the active-profile button on the toolbar; Settings → Profiles → "Manage users/profiles…".
- **What it does:** each profile has **its own data folder**: its own sign-ins, cookies, history, passwords, bookmarks, settings and Quick Access.

### 18.2 Adding a profile
- **How to use:** "Add user/profile…" → name (e.g. `google-account2`; letters, digits, `-`, `_`).

### 18.3 Switching profiles
- **How to use:** the profile button → choice; in the profiles window "Switch to selected"; from the Velivo tools menu → "Switch user/profile…" or the quick "Switch profile: work".
- **What it does:** Velivo restarts in the chosen profile.

### 18.4 Profile icon
- **How to use:** in the profiles window "Profile icon" → choice → "Save icon". The icon is shown on the profile button.

### 18.5 Deleting a profile
- **How to use:** "Delete selected profile" – asks and deletes the profile together with its local data.
- **Limits:** the active or default profile can't be deleted.

### 18.6 Data isolation
- **What it does:** profile data is kept apart on disk and during LAN sync (17.8). Banking mode is additionally isolated from normal tabs (14.1), and private tabs have their own volatile store (4.2).

### 18.7 Banking profiles
- Separate banking modes for other people – 14.42.

### 18.8 Quick Access profiles
- Separate shortcut sets with a PIN – 21.6.

### 18.9 Starting with a chosen profile (advanced)
- **How to use:** a Velivo shortcut with the `--profil name` parameter.

# 19. Cache and relocating temporary data

### 19.1 Browser junk – what it is
- **What it does:** the page cache, compiled scripts and the graphics cache. They can be deleted **without losing sign-ins or extension data** – they are not your data.

### 19.2 Junk folder (cache relocation)
- **What for:** moving the cache to another drive, e.g. a RAM disk or a faster/bigger drive.
- **Where:** Settings → Browser junk → "Junk folder (empty = in the browser profile)" → "Choose…".
- **What it does:** Velivo creates its own subfolder in the chosen folder and **clears only its contents**, never anything else on that drive. The new folder takes effect after a restart.
- **Limits:** the engine always keeps the graphics cache in the browser profile (an engine requirement). "Default" restores the normal location.

### 19.3 Delete junk on every start
- **Where:** Settings → "Delete junk on every browser start".
- **Limits:** pages load a little slower the first time.

### 19.4 Clear junk now
- **How to use:** Settings → "Clear junk now". Velivo shows how much was freed and how much it takes now. The graphics cache disappears on the next start.

### 19.5 Cache size
- **Where:** Settings → Page loading speed → "Cache size (after restart)": Automatic or a chosen size.

### 19.6 Safe clean-up
- **What it does:** junk is deleted only when no other Velivo window is running (the engine holds these files).

### 19.7 Separate data folder (advanced)
- **What it does:** the `VELIVO_DANE` environment variable starts Velivo with a completely separate data folder – e.g. for tests.

# 20. RAM disk

### 20.1 Cache on a RAM disk
- **What for:** faster page loading and saving the SSD.
- **How to use:** 1) create a RAM disk with any program (e.g. ImDisk, SoftPerfect RAM Disk); 2) Settings → "Junk folder" → "Choose…" → e.g. `R:\`; 3) save and restart Velivo; 4) optionally "Delete junk on every start".
- **What it does:** cached pages load from RAM; the SSD gets fewer writes; after the computer is turned off the cache disappears by itself – a plus for privacy.
- **Limits:** you don't need any links or folder moving as in Chrome. The graphics cache stays in the profile.

# 21. Quick Access

### 21.1 New tab page
- **What it does:** shortcuts to your favourite sites arranged in groups, with icons and thumbnails.

### 21.2 Adding a shortcut
- **How to use:** "+ Shortcut" → address, name → "Save"; or on any page right-click → "Add to Quick Access" → choose the group.

### 21.3 Shortcut groups
- **How to use:** "+ Group" – e.g. Start, Finance, Music, Games. Icons don't flicker when switching groups.

### 21.4 Editing and deleting shortcuts
- **How to use:** the "Edit" and "Delete" icons on the tile.

### 21.5 Custom icons
- **How to use:** in the shortcut dialog "Choose image…" (from a file) or paste an image from the clipboard; "Remove custom" goes back to the site icon.

### 21.6 Quick Access profiles
- **How to use:** profile list → "+ New profile…"; switching from the list; renaming; "Delete this profile".

### 21.7 Profile PIN
- **How to use:** menu ⋮ → "PIN for this profile" – set or remove a PIN.
- **What it does:** entering a profile with a PIN requires the code ("Enter the code to enter"); so does adding a page to such a profile.

### 21.8 Bin
- **How to use:** menu ⋮ → "Bin…" – deleted shortcuts can be restored with "Restore"; emptying the bin.

### 21.9 Page thumbnails
- **Where:** menu ⋮ → "Page thumbnails".
- **What it does:** Velivo makes page thumbnails in the background, in an invisible window – without history, sound, pop-ups or downloads.

### 21.10 Background themes and size
- **What it does:** choosing the new tab page background and tile size.

### 21.11 Importing bookmarks
- **What it does:** imports shortcuts from a bookmarks file (HTML) or a backup (JSON).

### 21.12 Backup and data folder
- **What it does:** a Quick Access backup on disk; the "Extension folder" handled by the Sejf bridge (nothing to do); "Name of this computer".

### 21.13 Sync
- **What it does:** in Velivo Quick Access syncs over LAN together with its images. The same extension in Chrome can sync with a Google account ("Sync with Google account", "Sync OK" state).

### 21.14 Site permissions
- **Where:** menu ⋮ → "Revoke site access", "Permission prompt".

### 21.15 Quick Access in other browsers
- **Where:** Settings → "Extension folder for manual installation in other browsers".
- **What it does:** gives the path to copy into Chrome/Edge's "Load unpacked extension" dialog. Quick Access data in Velivo and in other browsers is separate.

# 22. Extensions

### 22.1 Extensions window
- **Where:** the "Extensions" button on the toolbar (developer mode).
- **What it does:** a list of installed extensions: on/off, "Extension settings page", "Reload", remove.

### 22.2 Installing from the Chrome Web Store
- **How to use:** open the extension's page in the Chrome Web Store ("Open Chrome Web Store") → click "➕ Add to Velivo" on the toolbar or the store's button; or "Install from link/ID…".
- **What it does:** Velivo downloads the extension and installs it itself.

### 22.3 Loading an extension from a folder
- **How to use:** "Extension folder (with manifest.json)".

### 22.4 Extension icon bar
- **What it does:** extension icons on the toolbar; a click opens the extension popup; "Open the popup in a tab"; pinning and unpinning icons.
- **Limits:** some extensions have no popup ("works in the background and on pages").

### 22.5 Automatic reload of changed extensions
- **What it does:** on start Velivo reloads extensions whose files have changed.

### 22.6 Extension sync
- **What it does:** the extension list goes to paired computers, and missing ones are installed there.

### 22.7 Built-in extensions
- **uBlock Origin Lite** (11.5) and **Quick Access** (chapter 21).

# 23. Import and export

### 23.1 Import from other browsers
- **Where:** Settings → "From Chrome / Edge / Brave / Opera".
- **What it does:** bookmarks are read directly from that browser's files (Vivaldi too); for passwords Velivo opens that browser's password page for a CSV export.

### 23.2 Importing passwords from CSV
- **Where:** Settings → "Passwords and logins from a file (CSV: KeePassXC, Chrome, Edge…)" or "Import passwords CSV…".
- **What it does:** loads passwords from KeePass/KeePassXC (skips entries in the bin), Chrome, Edge, Firefox and others; supports multi-line fields. Shows the number of imported entries.

### 23.3 Exporting passwords to CSV
- **Where:** Settings → "Export passwords CSV…".
- **Limits:** the CSV file is **plain text** (not encrypted) – store it carefully and delete it after use.

### 23.4 Bills sheet import and export
- Import from Excel (CSV) – 14.35; export "Save to Excel (CSV)" – 14.38.

### 23.5 Encrypted export package (E2E sync by file)
- **Where:** Settings → E2E sync → "Export package…" / "Import package…".
- **What it does:** saves your data (settings, bookmarks, passwords and more) in a **password-encrypted** file; you can move the file to another device (e.g. on a USB stick) and import it there.

### 23.6 Importing bookmarks into Quick Access
- 21.11.

# 24. Backup and recovery

### 24.1 Banking mode backup
- **Where:** 🏦 menu → "Database backup…".
- **What it does:** a `.vbank` file with all banking profiles, **still encrypted** – e.g. on a USB stick.

### 24.2 Restoring banking mode
- **Where:** 🏦 menu → "Restore database from backup…". Opening it needs the password or key from the time the backup was made.

### 24.3 LAN pairing recovery file
- 17.15.

### 24.4 Encrypted data package
- 23.5.

### 24.5 Quick Access backup
- 21.12.

### 24.6 Backup on a clean install
- 1.3 – old data goes to a folder with a "kopia-…" suffix.

### 24.7 Forgotten banking-mode password
- 14.44 – without the password and key the data can't be recovered; hence a backup and a second key.

# 25. Settings – every option

You open Settings with the ⚙ button. Below is every option in the window's order.

### 25.1 Default browser
- The state and the "Set Velivo as the default browser…" button (1.8).

### 25.2 Import passwords, logins and bookmarks
- "Passwords and logins from a file (CSV)", "From Chrome / Edge / Brave / Opera" (chapter 23).

### 25.3 Default page zoom
- 6.7.

### 25.4 Dark mode and night mode
- 6.5, 6.6.

### 25.5 Content entrance effect and its speed
- 6.1, 6.2.

### 25.6 Theme and look style
- 6.3, 6.4.

### 25.7 Interface language
- 1.10.

### 25.8 Always compact toolbar
- 2.3.

### 25.9 Read aloud – voice and speed
- 9.9, chapter 10.

### 25.10 Tabs: session restore and links in the same tab
- 4.12, 4.13.

### 25.11 Search and start
- Search shortcuts, search engine, mouse gestures, the "Picture in picture" and "Download" buttons over videos, home page, Quick Access as the new tab, extension folder (chapters 5, 7, 8, 21).

### 25.12 Privacy
- "Do Not Track", tracking prevention (balanced/strict), saving history, clearing on close, logins from Sejf, offering to save passwords, autofill, pop-ups, banner rejection, page content memory, pressure tricks, privacy receipt, managing form data and passwords (chapters 11, 15, 16).

### 25.13 Ad blocking
- Full filter lists and "Update lists now" (11.3).

### 25.14 Security and downloads
- SmartScreen, asking where to save, fake-site detection, HTTPS, safe payments, uBlock Origin Lite (chapters 8, 11, 13).

### 25.15 Velivo speakers
- **What for:** Velivo's sound on chosen speakers.
- **How to use:** the "Velivo speakers" list – choose an output instead of "Default Windows output".

### 25.16 Don't lose sound
- **What for:** for musicians – when a music program (Ableton, Cubase) takes the speakers exclusively.
- **What it does:** every 2 seconds Velivo checks whether the chosen output is free; taken → Velivo's sound goes to another active output and comes back when the speakers are free. "Windows volume mixer…" lets you pin Velivo to the speakers permanently.

### 25.17 Stay in the tray
- **Where:** "Stay in the tray when the window is closed (background sync, instant start)".
- **What it does:** closing the window hides Velivo to the icon by the clock: background pages are paused (the "Video on top" window keeps playing), LAN sync works, and the next opening is instant. The first time it hides a bubble explains this. Right-click the icon: **🌐 Open the browser**, **🎬 Video library**, **▶ Open the video player**, **📺 Open TV**, **🧹 Clean-up and backup**, **🕶 Open an incognito tab**, **🏦 Open in banking mode**, **Sync now**, **Close Velivo completely** (nothing is left running in the background). The icon pulses while syncing.
- **Silent autostart:** with autostart with Windows Velivo starts silently in the tray and prepares the channel list, TV guide and player in the background – after opening everything is ready.

### 25.18 Connections per downloaded file
- 8.2.

### 25.19 Browser junk
- Chapter 19.

### 25.20 Page loading speed
- 26.1, 19.5.

### 25.21 Data
- "Clear browsing data now…", "Per-site privacy" (11.13, chapter 12).

### 25.22 User profiles
- Chapter 18.

### 25.23 Sync
- E2E sync (package), LAN, pairing, recovery file, quiet mode, diagnostic panel (chapters 17, 23).

### 25.24 Saving settings
- **What it does:** "Save" saves and immediately applies the settings in all tabs. A change of language or junk folder takes effect after a restart (Velivo tells you).

### 25.25 Torrents
- **Where:** Settings → the "Torrents" section: turning on (off by default), zone folder, download and upload speed, seeding after download (ratio and time), number of peers. Described in 8.19.

### 25.26 Video player
- **Where:** Settings → the "Video player" section: opening videos from disk in Velivo, autoplay, resuming where you left off, looping. Described in 7.16 and chapter 28.

### 25.27 On-screen keyboard
- **Where:** Settings → "⌨ Velivo on-screen keyboard" (on by default). Described in chapter 29.

# 26. Performance

### 26.1 Faster page opening
- **Where:** Settings → Page loading speed → "Faster page opening (loading on link hover)".
- **What it does:** Velivo starts loading the page when you hover over a link and connects in advance to the servers of visible links.
- **Limits:** off in private and banking tabs; better switched off on a data cap.

### 26.2 A light program
- **What it does:** Velivo uses the WebView2 engine built into Windows, so it doesn't carry its own copy of the engine – memory is shared with the system. .NET is included in the installer, so nothing else needs to be installed.

### 26.3 Fast ad blocker
- **What it does:** the filter of about 100k rules is built in the background and swapped as a whole, without slowing browsing.

### 26.4 Less load on the window
- **What it does:** images and fonts don't go through checking in the program window (image ads are blocked by uBlock inside the engine) – scrolling and loading are smoother.

### 26.5 Light "video only" mode
- **What it does:** after the main window is closed with the video window playing, Velivo runs only for the video (7.10).

### 26.6 Cache on a RAM disk
- Chapter 20.

# 27. All other features

### 27.1 Bookmarks
- **Where:** the star on the toolbar (Ctrl+D); the "All bookmarks" button.
- **What it does:** add/remove a bookmark for the current page; the bookmark list; right-click: "Rename", delete. A deletion carries over to paired computers.

### 27.2 History (Ctrl+H)
- **What it does:** history grouped as in Chrome: **day → session** (one browser launch, "Current session") → pages.
- **How to use:** "Search history…" (titles and addresses); double-click opens a page; right-click: "Remove from history", "Remove found entries from this day", "Remove the whole day from history", "Open all in new tabs", "Remove the session from history"; "Clear all history".

### 27.3 Downloads panel
- Chapter 8.

### 27.4 Muting and tab sound
- 4.11, 25.15–25.16.

### 27.5 Screenshots
- 4.17.

### 27.6 Phone version
- 4.16.

### 27.7 Translation
- 9.11.

### 27.8 One window and links from other programs
- 2.9.

### 27.9 Error resilience and the error log
- 2.10. The `bledy.log` file in the data folder is useful when reporting problems.

### 27.10 Where the data is
- **What it does:** all data of the default profile is in `%LOCALAPPDATA%\Programs\Velivo\Dane` (other profiles: `…\Dane\Profiles\name`). The most important files: `ustawienia.txt`, `zakladki.txt`, `historia.txt`, `sesja.txt`, `hasla.vault` (encrypted), `autouzupelnianie.vault` (encrypted), `bank.json` (encrypted fields), `bledy.log`.

### 27.11 Troubleshooting
| Problem | What to do |
|---|---|
| A site blocks something it shouldn't | shield → panel → "to trusted" or right-click → "Restore blocked elements" |
| A cookie banner is still visible | the banner has no "Reject" button – Velivo never clicks "Accept" |
| Computers don't see each other | the same profile on both, the firewall allows Velivo, automatic Windows time (a difference of more than 5 minutes blocks the exchange) |
| Downloading from YouTube fails | try again in a moment – yt-dlp updates itself, and services sometimes change |
| An antivirus blocks the installer | the file has no paid digital signature – add an exception or check the SHA-256 checksum (1.6) |
| The program doesn't start | check WebView2 Runtime (1.2); look into `bledy.log` |

# 28. Video player and TV

### 28.1 VLC-like video player
- **What for:** films from disk in every popular format (AVI, MKV, WMV, MP4…).
- **Where:** the installer ("Video player" option) or later in Velivo; double-click a video file in Windows.
- **What it does:** opens just the player, without the browser window. About 145 MB from the official VideoLAN package, verified with a checksum.
- **Limits:** without this add-on MP4 and WebM play in a browser tab.

### 28.2 Video library
- **Where:** player → library.
- **What it does:** a list of films with sorting, resuming where you left off, custom titles, films from another computer on the home network.

### 28.3 TV, catch-up and recording
- **Where:** player → 📺 TV.
- **What it does:** channels with a TV guide, favourites, history of recent channels, watching what already aired (catch-up), recording, scheduling and a sleep timer.
- **Limits:** you need your own channel list; Velivo doesn't write access tokens to logs.

### 28.4 Subtitles from OpenSubtitles.com
- **Where:** right-click the film or in the library → "💬 Download subtitles…".
- **How to use:** sign in with your OpenSubtitles.com account (the password is stored encrypted on this computer), choose subtitles from the list.
- **What it does:** searches by file hash (exact match) and by name, saves the subtitles next to the film as "film.pl.srt"; the player and the narrator load them automatically.

### 28.5 Subtitle shift
- **How to use:** **G** / **H** or the slider in the subtitles menu.
- **What it does:** shifts subtitles when they are late or early; Velivo remembers the shift for the film.

### 28.6 Narrator
- **Where:** the narrator button on the player bar.
- **What it does:** reads subtitles aloud (a natural offline Piper voice or a Windows voice), lowers the film's volume while reading; settings remembered per film.

### 28.7 Full description of the player
- **Where:** Part III of the user manual PDF (`Velivo-1.22-Manual-EN.pdf`) and the `dokumentacja/instrukcja-filmy-tv.en.md` file – all buttons, shortcuts and menus.

# 29. Velivo on-screen keyboard

### 29.1 Opening the keyboard
- **How to use:** middle-click a field, hold the wheel for 1 s, the keyboard icon by the clock, the "Velivo Klawiatura" shortcut in the Start menu (can be pinned to the taskbar) or the "Open automatically when you click a text field" option (tray icon menu).
- **What it does:** the keyboard doesn't take the cursor – letters go to the field you are typing in, in any program.

### 29.2 Autocorrect
- **What it does:** after Space it fixes Polish letters (zolw → żółw), typos, „nie” with verbs (niewiem → nie wiem), fixed phrases (napewno → na pewno), „ze/że”, polite forms of address (ty → Ty). ⌫ right after a correction undoes it.
- **Limits:** never in password fields.

### 29.3 Punctuation
- **What it does:** a comma before że, który, bo, ale, a…; periods and commas stick to the word; a space after a comma; a capital letter after a period.

### 29.4 Word and sentence suggestions
- **What it does:** the strip above the keys shows completions (Space inserts the first one), the next word and whole sentences you type often (click or Ctrl+Space).

### 29.5 Learning
- **What it does:** remembers your words, phrases, sentences and corrections (a word deleted and retyped). The data is encrypted with your Windows account, on this computer only.

### 29.6 📖 My dictionary
- **Where:** the 📖 button in the keyboard header.
- **What it does:** a window with a list of your words: adding many at once and from a .txt file, search, sorting, 🧹 clean-up (removes ordinary words and typos). You can also add a new word with ➕ or Ctrl+Space.

### 29.7 Swipe typing
- **What it does:** you drag the mouse over the letters, Velivo types the word and learns your movements.

### 29.8 Numeric keypad and shortcuts
- **What it does:** digits, Select all, copy, paste, undo, redo, Alt+Tab, Notes, sticky notes, e-mail, volume, calculator, screen snipping, quick search, shutting down the computer.

### 29.9 Gamepad
- **How to use:** the 🎮 button; right-click – settings.
- **What it does:** the D-pad picks a key, A presses it; the right stick moves the mouse cursor (RT faster, L3 slower, sensitivity in the settings); with the keyboard hidden A/X/Y – click, right-click, double-click; R3 – window switcher; Back – hide / show.

### 29.10 Working in the background
- **What it does:** the hidden keyboard (the "—" button, Back on the gamepad or the tray icon) keeps correcting what you type on a physical keyboard.

# 30. Sticky notes

### 30.1 A new note
- **Where:** the 🗒 button on the keyboard.
- **What it does:** a plain note, to-do, shopping, call, important, idea or today.

### 30.2 Notes above the taskbar
- **What it does:** docked side by side; clicking the bar expands upward, 📎 undocks to the desktop, 🎨 color, 📌 always on top, 📅 inserts a date; double-clicking a line ticks it on the list.

### 30.3 Due-date reminder
- **What it does:** when a note contains a date (19.10.2026, 19.10, 2026-10-19), its icon blinks and its bar pulses the day before and on the day until you open the note.

### 30.4 Repeating the date
- **Where:** the 🔁 button next to the note icon.
- **How to use:** choose: one-off, weekly, every 2 weeks, monthly, yearly or "Custom interval…" (every N days, weeks, months or years).
- **What it does:** counts the next dates from the date in the note – e.g. "17.10.2026 insulin" weekly reminds you on 24.10, 31.10 and so on. With repeating on, the 🔁 icon is bright and its tooltip shows the interval.

# 31. Velivo for phones (Android)

Velivo for Android (10 or newer) is a light, private browser in Velivo colours, linked to Velivo for Windows. The computer is the main device – it always confirms anything coming from the phone. Full guide: `dokumentacja/instrukcja-telefon.en.md`.

### 31.1 Menu
- **Where:** the Velivo logo next to the address bar.
- **What it does:** tiles three to a row in the groups Tabs, This page, Mine and Settings; the blocked-ads counter at the top (tap – privacy panel).

### 31.2 Tabs, private tab, banking mode
- **What it does:** tabs with thumbnails; 🕶 private tab (a separate profile erased on close); 🏦 banking mode (fingerprint / PIN, no history or screenshots, locks after 10 minutes).

### 31.3 Find in page and desktop site
- **What it does:** 🔎 a bar with a counter and ▲ ▼; 🖥 the desktop version of the page for the current tab.

### 31.4 Reader view and read aloud
- **What it does:** 📖 just the article text, A− / A+, remembers where you left off; 🔊 reading carries on with the screen off (notification with "Stop"), going back stops it; the title is read once, summary boxes are skipped.
- **Limits:** no speech engine – Velivo offers to download Google Speech Services.

### 31.5 Removing adverts with your finger
- **How to use:** press and hold on an advert or an empty "ADVERT" box (or menu → 🎯 Remove element and tap) – Velivo selects the whole box and asks "Remove?"; ⬆ larger area, ↺ restore, 📋 copy the element's code to report it.
- **What it does:** the removed element stays hidden on that site for good (stored on the phone only).

### 31.6 Ad blocking
- **What it does:** EasyList, EasyPrivacy and the Polish filters bundled with the app (working from the first launch), refreshed every 4 days; hiding rules as in uBlock Origin – no empty box or error frame left where an advert was; no adverts on YouTube.

### 31.7 Privacy panel
- **Where:** menu → the green "🛡 Blocked ads" counter.
- **What it does:** shows whether the connection is encrypted, how many items were blocked on the page and their domains; **Turn off blocking here** for a site that misbehaves.

### 31.8 Scam protection
- **What it does:** offline, as on Windows – warns about fake bank and brand sites (a brand on someone else's domain, typos, characters disguised as letters, fakes of sites with saved passwords) and about APK downloads from outside Google Play; HTTPS first (home network without asking); a bad certificate is always blocked.

### 31.9 Passwords
- **What it does:** encrypted with a key in the Android Keystore; filled in only on https and on the home network; the list opens after a fingerprint / PIN.

### 31.10 Sticky notes
- **Where:** menu → Mine → 🗒 Sticky notes / 📝 New note.
- **What it does:** the same kinds as on Windows (note, to do, shopping, ring, important, idea, for today), 6 colours; 📅 inserts a date from the calendar; 🔁 repeat (every week, 2 weeks, month, year, custom interval); the list is sorted by the nearest date.
- **Reminder:** a notification **the day before** and **on the day** (daily at 9:00, also after the phone restarts); tapping it opens the note.

### 31.11 Sync with the computer
- **What it does:** pairing with a 6-digit code; bookmarks, passwords and Quick Access from the computer; the computer confirms new items from the phone; files both ways (Share → Velivo, 📎 Send a file to…); 📤 a tab to the computer and 📺 from the computer to the phone (with the video position).

### 31.12 Look and language
- **What it does:** theme like the phone / light / dark in Velivo colours, dialogs included; 🌐 Language – Polish or English for Velivo only.

### 31.13 Two-finger zoom
- **What for:** for weaker eyesight – larger text and buttons throughout Velivo.
- **How to use:** spread two fingers in the menu or the lists of tabs, bookmarks, history, passwords or notes; pinch to make it smaller.
- **What it does:** 80–220%; the top bar and the bottom buttons grow too; at large sizes the menu tiles are two to a row; on a page two fingers change that page's text (50–300%) – every site remembers its own size, and Velivo its own. In reader view two fingers change the text size (up to 48 pt).

### 31.14 Velivo for people with weaker eyesight
- **What for:** comfortable browsing for older people and the partially sighted.
- **How to set up (once):** spread two fingers in the menu to a comfortable size (up to 220% – all of Velivo and the page text grow); pick a light or dark theme; show 📖 Reader view and 🔊 Read aloud; agree on "⚠ Warning → Go back (safe)"; notes with a date remind about the doctor and medicines the day before.
- **What it does:** everything larger and remembered, long texts to listen to, protection against fake bank sites and suspicious apps with no setup at all.

# Features NOT AVAILABLE IN 1.22

These things were discussed, but **version 1.22 doesn't have them**:
- **Changing (faking) the browser fingerprint** – NOT AVAILABLE IN 1.22; Velivo detects and reports fingerprinting (11.9).
- **8 Piper offline voices** – NOT AVAILABLE IN 1.22; 6 are available (3 Polish, 3 English).
- **A shared environment panel for many computers** – NOT AVAILABLE IN 1.22; there is a LAN diagnostic panel (17.13).
- **Syncing open tabs** – NOT AVAILABLE IN 1.22; there is "Send to…" (17.9).
- **A connection to the bank / automatic payment detection** – NOT AVAILABLE IN 1.22; you mark payments manually (14.27).
- **Currency conversion in the sheet** – NOT AVAILABLE IN 1.22; the currency is only a symbol (14.36).
- **A digital signature on the program files** – NOT AVAILABLE IN 1.22.

# FULL LIST OF VELIVO 1.22 FEATURES

Features described: **339**. The number next to each feature points to its description in the handbook.

## 1. First start

- **1.1** The Velivo-Setup-1.22.exe installer
- **1.2** Checking system components
- **1.3** Update or clean install
- **1.4** No-install version
- **1.5** Silent install and uninstall (advanced)
- **1.6** Checking that the file is genuine
- **1.7** First opening
- **1.8** Setting Velivo as the default browser
- **1.9** Import from your previous browser
- **1.10** Program language
- **1.11** All in one – no extra programs

## 2. Interface

- **2.1** Tab bar
- **2.2** Toolbar
- **2.3** Compact and adaptive toolbar
- **2.4** Address bar
- **2.5** Active privacy rule indicator
- **2.6** The "➕ Add to Velivo" button in the extension store
- **2.7** Notification bubbles
- **2.8** Full screen
- **2.9** One program window
- **2.10** Error resilience

## 3. Mouse and keyboard control

- **3.1** Mouse wheel on the zoom button
- **3.2** Mouse wheel on the night-mode button
- **3.3** Mouse wheel over the video window
- **3.4** Clicking links
- **3.5** Right-click menu on a page
- **3.6** Right-click menu on a tab
- **3.7** Keyboard shortcuts

## 4. Tabs and windows

- **4.1** New tab
- **4.2** Private tab (incognito)
- **4.3** Duplicating a tab
- **4.4** Closing tabs
- **4.5** Restoring a closed tab
- **4.6** Pinned tabs
- **4.7** Tab groups
- **4.8** Tab sets
- **4.9** Auto-refreshing a tab
- **4.10** Searching tabs
- **4.10c** Menu of the empty tab bar and the "+" button
- **4.10a** Split screen (two tabs side by side)
- **4.10b** Sleeping unused tabs
- **4.11** Muting a tab
- **4.12** Links in the same tab
- **4.13** Restoring the session
- **4.14** Send a tab to another computer
- **4.15** Page zoom
- **4.16** Phone version of a site
- **4.17** Screenshot
- **4.18** Closing and minimising Velivo

## 5. Mouse gestures and search

- **5.1** Mouse gestures
- **5.2** Search engine in the address bar
- **5.3** Search shortcuts
- **5.4** Searching the selected text
- **5.5** Home page
- **5.6** Quick Access as the new tab page

## 6. Animations and look

- **6.1** Content entrance effect
- **6.2** Entrance effect speed
- **6.3** Look style: Modern and Colorful
- **6.4** Browser themes
- **6.5** Dark mode for pages
- **6.6** Night mode (warmer colors)
- **6.7** Default page zoom

## 7. Video and "Video on top"

- **7.1** Video on top – opening
- **7.2** Clean video view
- **7.3** Moving the window
- **7.4** Resizing the window
- **7.5** Window transparency
- **7.6** Always on top (pin)
- **7.7** Pause and resume
- **7.8** Compact player in the window (seeking, volume, full screen)
- **7.9** Back to the page (↩)
- **7.10** Watching with the browser closed
- **7.11** Remembering the window settings
- **7.12** Closing the window
- **7.13** Examples of use
- **7.14** Picture in picture (PiP)
- **7.15** Muting a video in a tab
- **7.16** Videos from disk

## 8. Downloading files, videos and music

- **8.1** Velivo download manager
- **8.2** Downloading with multiple connections
- **8.3** Pause and resume
- **8.4** Automatic retrying
- **8.5** Downloading files while signed in
- **8.6** Fallback download through the engine
- **8.7** Asking where to save
- **8.8** Download list
- **8.9** Warning when closing
- **8.10** The "file from the internet" mark
- **8.11** The ⬇ Download button over a video
- **8.12** Choosing quality and format
- **8.13** yt-dlp – downloaded once
- **8.14** FFmpeg – downloaded once
- **8.15** Checking the downloaded tools
- **8.16** Detect media to download
- **8.17** Downloading subtitles for films
- **8.18** Downloading from YouTube in "Detect media" mode – a limit
- **8.19** Torrents (magnet and .torrent) – turned on in Settings

## 9. Reader and reading pages

- **9.1** Reader mode (Reader)
- **9.2** Reader look
- **9.3** Local summary
- **9.4** Reading a page aloud
- **9.5** Reading a selection
- **9.6** Reading from a chosen place
- **9.7** Pause, resume, stop
- **9.8** Reading speed
- **9.9** Choosing a voice
- **9.10** "Where did I read that?"
- **9.11** Translating pages and selections

## 10. Offline reading – Piper voices

- **10.1** Natural offline voices
- **10.2** Piper voices available in 1.22
- **10.3** Security of the downloaded Piper program
- **10.4** Where the voices are

## 11. Privacy

- **11.1** Privacy shield
- **11.2** Velivo ad blocking
- **11.3** Full filter lists
- **11.4** Your own blocking rules (advanced)
- **11.5** uBlock Origin Lite (built in)
- **11.6** Blocking page elements
- **11.7** "Do Not Track" and Global Privacy Control
- **11.8** Privacy receipt
- **11.9** Fingerprinting – detection and report
- **11.10** Automatic cookie-banner rejection
- **11.11** Pop-up blocking
- **11.12** History – saving and clearing
- **11.13** Clear browsing data
- **11.14** The "What was blocked and why" log
- **11.15** Hiding the "embedded browser"
- **11.16** Shop pressure-trick detector

## 12. Protection levels and site rules

- **12.1** Tracking prevention – balanced
- **12.2** Tracking prevention – strict
- **12.2a** Tracking prevention – off (no control)
- **12.3** Privacy panel and domain rules
- **12.4** Rule: Block JavaScript for the domain
- **12.5** Rule: Don't send cookies for the domain
- **12.6** Rule: Force tracker blocking for the domain
- **12.7** Rule: Automatically clear data after visiting the domain
- **12.8** Trusted domains
- **12.9** Security levels – summary

## 13. Security

- **13.1** Fake-site detection (offline)
- **13.2** SmartScreen
- **13.3** Always encrypted connection (HTTPS)
- **13.4** Passwords only for the real domain
- **13.5** Safe payments – screen-recording block
- **13.6** Payment data protection
- **13.7** Attack-resistant forms
- **13.8** A closed channel between the page and Velivo
- **13.9** The "file from the internet" mark and tool checking
- **13.10** Protection against a page closing the window

## 14. Banking mode

- **14.1** What banking mode is
- **14.2** The 🏦 button and the mode menu
- **14.3** First start of the mode
- **14.4** FIDO2 hardware key (YubiKey, Google Titan)
- **14.5** Opening the mode
- **14.6** Automatic lock
- **14.7** Lock now
- **14.8** Clean-up after closing the mode
- **14.9** Database encryption
- **14.10** My banks
- **14.11** My online shops
- **14.12** Bank login details
- **14.13** Fill in login
- **14.14** Fill in selected characters
- **14.15** Copy the form description
- **14.16** My cards
- **14.17** Fill in the card on this page
- **14.18** My logins and passwords (in the mode)
- **14.19** Confidential data (documents)
- **14.20** Bank accounts
- **14.21** My notes
- **14.22** Fill in login and password (data linked to the site)
- **14.23** Search my database
- **14.24** Bills to pay
- **14.25** Recurring bills
- **14.26** Due dates and reminders
- **14.27** Marking as paid
- **14.28** Open the payment page
- **14.29** Bill names from the sheet
- **14.30** Add bills from the sheet
- **14.31** Bills sheet
- **14.32** Sheet – entering amounts and months
- **14.33** Sheet – a new month
- **14.34** Sheet – bills (columns)
- **14.35** Sheet – import from Excel (CSV)
- **14.36** Sheet – currency
- **14.37** Sheet – saving
- **14.38** Payment summary
- **14.39** Warning about a site resembling your bank
- **14.40** Banking-mode suggestion
- **14.41** Opening log
- **14.42** Banking profiles
- **14.43** Banking mode settings
- **14.44** Forgotten password
- **14.45** The mode's guide in the program

## 15. Sejf (external password manager)

- **15.1** Logins from Sejf on sign-in pages
- **15.2** The Sejf popup in Quick Access
- **15.3** Filling forms from Sejf

## 16. Passwords and data

- **16.1** Velivo password manager
- **16.2** Searching passwords
- **16.3** Adding and editing an entry
- **16.4** Strong password generator
- **16.5** Copying and deleting
- **16.6** Offer to save a password
- **16.7** Filling in a saved login
- **16.8** Form autofill
- **16.9** Saving form data
- **16.10** Managing autofill data

## 17. LAN Sync – home network sync

- **17.1** Sync without the cloud
- **17.2** Before pairing – no data
- **17.3** Connection proposal
- **17.4** Pairing devices
- **17.5** First connection – whose settings
- **17.6** Sync encryption
- **17.7** What syncs
- **17.8** Profile isolation in sync
- **17.9** Send a tab to another computer
- **17.10** Tray indicator
- **17.11** Sync notifications
- **17.12** Quiet mode
- **17.13** LAN diagnostic panel
- **17.14** Warnings and errors
- **17.15** Pairing recovery file
- **17.16** Stay in the tray – syncing in the background

## 18. Profiles and user isolation

- **18.1** Velivo user profiles
- **18.2** Adding a profile
- **18.3** Switching profiles
- **18.4** Profile icon
- **18.5** Deleting a profile
- **18.6** Data isolation
- **18.7** Banking profiles
- **18.8** Quick Access profiles
- **18.9** Starting with a chosen profile (advanced)

## 19. Cache and relocating temporary data

- **19.1** Browser junk – what it is
- **19.2** Junk folder (cache relocation)
- **19.3** Delete junk on every start
- **19.4** Clear junk now
- **19.5** Cache size
- **19.6** Safe clean-up
- **19.7** Separate data folder (advanced)

## 20. RAM disk

- **20.1** Cache on a RAM disk

## 21. Quick Access

- **21.1** New tab page
- **21.2** Adding a shortcut
- **21.3** Shortcut groups
- **21.4** Editing and deleting shortcuts
- **21.5** Custom icons
- **21.6** Quick Access profiles
- **21.7** Profile PIN
- **21.8** Bin
- **21.9** Page thumbnails
- **21.10** Background themes and size
- **21.11** Importing bookmarks
- **21.12** Backup and data folder
- **21.13** Sync
- **21.14** Site permissions
- **21.15** Quick Access in other browsers

## 22. Extensions

- **22.1** Extensions window
- **22.2** Installing from the Chrome Web Store
- **22.3** Loading an extension from a folder
- **22.4** Extension icon bar
- **22.5** Automatic reload of changed extensions
- **22.6** Extension sync
- **22.7** Built-in extensions

## 23. Import and export

- **23.1** Import from other browsers
- **23.2** Importing passwords from CSV
- **23.3** Exporting passwords to CSV
- **23.4** Bills sheet import and export
- **23.5** Encrypted export package (E2E sync by file)
- **23.6** Importing bookmarks into Quick Access

## 24. Backup and recovery

- **24.1** Banking mode backup
- **24.2** Restoring banking mode
- **24.3** LAN pairing recovery file
- **24.4** Encrypted data package
- **24.5** Quick Access backup
- **24.6** Backup on a clean install
- **24.7** Forgotten banking-mode password

## 25. Settings – every option

- **25.1** Default browser
- **25.2** Import passwords, logins and bookmarks
- **25.3** Default page zoom
- **25.4** Dark mode and night mode
- **25.5** Content entrance effect and its speed
- **25.6** Theme and look style
- **25.7** Interface language
- **25.8** Always compact toolbar
- **25.9** Read aloud – voice and speed
- **25.10** Tabs: session restore and links in the same tab
- **25.11** Search and start
- **25.12** Privacy
- **25.13** Ad blocking
- **25.14** Security and downloads
- **25.15** Velivo speakers
- **25.16** Don't lose sound
- **25.17** Stay in the tray
- **25.18** Connections per downloaded file
- **25.19** Browser junk
- **25.20** Page loading speed
- **25.21** Data
- **25.22** User profiles
- **25.23** Sync
- **25.24** Saving settings
- **25.25** Torrents
- **25.26** Video player
- **25.27** On-screen keyboard

## 26. Performance

- **26.1** Faster page opening
- **26.2** A light program
- **26.3** Fast ad blocker
- **26.4** Less load on the window
- **26.5** Light "video only" mode
- **26.6** Cache on a RAM disk

## 27. All other features

- **27.1** Bookmarks
- **27.2** History (Ctrl+H)
- **27.3** Downloads panel
- **27.4** Muting and tab sound
- **27.5** Screenshots
- **27.6** Phone version
- **27.7** Translation
- **27.8** One window and links from other programs
- **27.9** Error resilience and the error log
- **27.10** Where the data is
- **27.11** Troubleshooting

## 28. Video player and TV

- **28.1** VLC-like video player
- **28.2** Video library
- **28.3** TV, catch-up and recording
- **28.4** Subtitles from OpenSubtitles.com
- **28.5** Subtitle shift
- **28.6** Narrator
- **28.7** Full description of the player

## 29. Velivo on-screen keyboard

- **29.1** Opening the keyboard
- **29.2** Autocorrect
- **29.3** Punctuation
- **29.4** Word and sentence suggestions
- **29.5** Learning
- **29.6** 📖 My dictionary
- **29.7** Swipe typing
- **29.8** Numeric keypad and shortcuts
- **29.9** Gamepad
- **29.10** Working in the background

## 30. Sticky notes

- **30.1** A new note
- **30.2** Notes above the taskbar
- **30.3** Due-date reminder
- **30.4** Repeating the date

## 31. Velivo for phones (Android)

- **31.1** Menu
- **31.2** Tabs, private tab, banking mode
- **31.3** Find in page and desktop site
- **31.4** Reader view and read aloud
- **31.5** Removing adverts with your finger
- **31.6** Ad blocking
- **31.7** Privacy panel
- **31.8** Scam protection
- **31.9** Passwords
- **31.10** Sticky notes
- **31.11** Sync with the computer
- **31.12** Look and language
- **31.13** Two-finger zoom
- **31.14** Velivo for people with weaker eyesight
