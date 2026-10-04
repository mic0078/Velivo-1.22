# Velivo – a new level of browsing

[Polski](README.md) | **English**

A lightweight, private browser for Windows that keeps your data with you: no account, no cloud, no history or passwords sent to outside servers. It runs on the Microsoft Edge engine (WebView2), so pages look the same as in Edge and Chrome.

Your computers share data through local network (LAN) sync, encrypted after the devices are paired. Velivo ships with the **Quick Access** add-on (a new tab page with shortcuts) and works with **Sejf**, a program that keeps passwords encrypted offline.

The interface is available in **Polish and English**. The installer asks for the language, and you can change it later in Settings → Appearance → Language.

**[⬇ Download the installer Velivo-Setup-1.22.exe](Instalator/Velivo-Setup-1.22.exe)**

## What no other browser has

Popular browsers (Chrome, Edge, Firefox, Opera) don't have these features built in – in Velivo they work right away, with no add-ons and no account:

- **▣ Video on top** – Velivo's own video window you can shrink almost to an icon (from 160×90), make **transparent with the mouse wheel** (15–100%), pin **always on top** or let it go behind other windows. It keeps playing after you close the tab **and even the whole browser**; the ↩ button brings the page back into Velivo at the same moment.
- **🧠 "Where did I read that?"** – Velivo remembers the text of pages you read (only on your computer) and finds an article by words you remember from the text, not just by its title. Banks, payments, mail, private tabs and pages with a password field are skipped.
- **⚠ Shop trick detector** – warns about fake "sale ends in 09:59" countdowns, "only 2 left" and "15 people are watching" pressure, hidden checkout fees, and **unticks** pre-selected insurance and newsletters for you.
- **🧾 Privacy receipt** – click the shield to see how many companies and countries the page connected to, which of them are data brokers and how many times it tried to fingerprint your computer.
- **📺 Send a tab to another computer** at home – over the encrypted local network, no cloud and no account; videos continue at the same moment.
- **Sync without the cloud** – bookmarks, passwords, history, pinned tabs, Quick Access and settings between computers on your home network, encrypted after pairing with a code.
- **⬇ Video downloads like Internet Download Manager** – a "Download" button over every video, YouTube included: quality choice (up to 1080p), MP3/M4A, folder choice; regular files with up to 16 connections and resume after restart.
- **🔊 "Read from here"** – right-click a paragraph and Velivo reads from that sentence; natural Polish and English offline voices.
- **🌅 Night mode with strength** like Windows "Night light" – adjusted with the mouse wheel on the button.
- **🖱 Mouse gestures and mouse-only use** – great on the sofa in front of the TV: ← back, → forward, ↑ new tab, ↓ close, ↓→ reload; page zoom with the wheel on the percent button.

## Features

| Area | What it offers |
| --- | --- |
| Privacy | Ad and tracker blocking (EasyList, EasyPrivacy, Polish list, about 95k rules), automatic rejection of cookie banners (GDPR), balanced or strict tracking prevention (trusted sites behave like balanced), "Do Not Track", SmartScreen, private tabs |
| Shield | Counter and list of everything blocked on the page with a privacy receipt; AdBlock on/off in one click |
| Element blocking | Right-click → "Block element (ad)": point at a banner or floating ad and Velivo hides it on every visit |
| Site rules | Block JavaScript or cookies, force tracker blocking, clear data for a domain, trusted domains |
| Tabs | Pinned tabs (frozen to their address), colored collapsible tab groups, saved tab sets, mute tab 🔊, auto refresh every 1–30 min, tab search (Ctrl+Shift+A), links in the same tab (Ctrl+click and middle click open a new tab) |
| Videos | ⬇ Download, ▣ Video on top, ⧉ Picture in picture (keeps playing after closing the tab) – buttons over every video |
| Quick Access | New tab page: shortcuts in groups, icons, page thumbnails made in the background, PIN-protected profiles, background themes |
| Passwords | Sign-in from Sejf (key icon next to login fields), Velivo's own encrypted store, password generator, CSV import and export |
| LAN sync | Settings, bookmarks, passwords, history, pinned tabs and Quick Access; send tabs to another computer |
| Reading | Read aloud (Windows voices and natural Piper offline voices), "Read from here", reader mode with a local summary (no AI, no cloud), skips ads and "Read also" blocks |
| History & downloads | History by day → site → pages, "Where did I read that?" (Ctrl+Shift+F), download history with date and source |
| Search | Address bar shortcuts: `yt cats`, `allegro bike`, `wiki Kraków`, `mapy Gdańsk` (add your own), "Paste and go" |
| Look | Modern (calm, like Windows 11) or Colorful, 10 themes, light → dark → night mode with strength, zoom remembered per site, phone version for a chosen site |
| Tools | Download manager (up to 16 connections), full-page screenshots, page and selection translation, pin/unpin Chrome extensions, user profiles |
| Languages | Polish and English – chosen in the installer and in settings |

## Installation

Velivo installs from a single `Velivo-Setup-1.22.exe`, without administrator rights, into `%LOCALAPPDATA%\Programs\Velivo`.

**Requirements:** Windows 10 (version 1809 or newer) or 11, 64-bit, .NET 10 Desktop Runtime (x64) and Microsoft Edge WebView2 Runtime (usually already in Windows 11). If something is missing, the installer detects it and opens the download page.

1. Download `Velivo-Setup-1.22.exe` from the [`Instalator`](Instalator) folder.
2. Run it and pick the language (Polski / English). Velivo will start in that language. If Windows shows "Unknown publisher", click "More info" → "Run anyway" (the installer is not digitally signed).
3. If Velivo was already installed, choose **Keep my settings and data** (normal update, recommended) or **Clean install** (start from scratch; old data is backed up with a `.kopia-<date>` suffix).
4. Optionally create a desktop shortcut and finish.
