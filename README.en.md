# Velivo – a new level of browsing

[Polski](README.md) | **English**

*Made with passion. Author: **Michael** ([github.com/mic0078](https://github.com/mic0078)).*

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-2563eb?logo=windows" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/installer-64%20MB%20(.NET%20included)-16a34a" alt="installer 64 MB with .NET">
  <img src="https://img.shields.io/badge/account%20%26%20cloud-not%20needed-0f172a" alt="no account, no cloud">
  <img src="https://img.shields.io/badge/language-PL%20%7C%20EN-b45309" alt="PL | EN">
</p>

<h3 align="center">A browser that goes beyond the limits of today's browsers.</h3>

<p align="center">
  ⚡ <b>Lightning fast</b> and light – the program itself uses about 50–80 MB of memory, ad-free pages load faster<br>
  🎨 <b>Beautiful, modern look</b> like Windows 11 – 10 themes, dark and night mode<br>
  🧩 <b>Light, built-in add-ons</b> – no store hunting and no slowdown<br>
  🎬 Video in a see-through window above everything – keeps playing even after the browser is closed<br>
  🏦 <b>Banking mode</b> – a separate, encrypted vault for banks, shops, cards and passwords, opened with a <b>hardware security key</b> (YubiKey, Google Titan) or a password<br>
  ✨ Page entrance effects (cinematic from darkness, focus-in) and <b>pages that start loading when you hover a link</b><br>
  🛡 Built-in uBlock Origin Lite and offline detection of fake bank sites<br>
  🔐 Passwords go only to the real sites · 💳 safe payments<br>
  🖱 Use all of it with the mouse alone · 🔒 no account, no cloud, no tracking
</p>

<p align="center"><b><a href="Instalator/Velivo-Setup-1.22.exe">⬇ Download Velivo for free</a></b> · <a href="MANUAL.en.md">📖 Manual</a> · ⭐ Star it if you like it</p>


A lightweight, private browser for Windows that keeps your data with you: no account, no cloud, no history or passwords sent to outside servers. It runs on the Microsoft Edge engine (WebView2), so pages look the same as in Edge and Chrome.

Your computers share data through local network (LAN) sync, encrypted after the devices are paired. Velivo ships with the **Quick Access** add-on (a new tab page with shortcuts) and works with **Sejf**, a program that keeps passwords encrypted offline.

The interface is available in **Polish and English**. The installer asks for the language, and you can change it later in Settings → Appearance → Language.

**[⬇ Download the installer Velivo-Setup-1.22.exe](Instalator/Velivo-Setup-1.22.exe)** (about 64 MB – .NET and uBlock Origin Lite included, nothing else to install) · **[📖 Complete user manual](MANUAL.en.md)**

![Velivo – the browser window, with the Video on top window playing in the bottom-right corner](docs/zrzuty/velivo-okno.png)

## What no other browser has

| ![](docs/zrzuty/film-na-wierzchu.png) | ![](docs/zrzuty/film-maly-w-rogu.png) | ![](docs/zrzuty/film-przezroczysty.png) |
| --- | --- | --- |
| ▣ Video on top | shrunk in a corner | transparent over the desktop |

**Velivo goes beyond the limits of today's browsers** – in protection too: it detects fake bank and shop sites offline, hides the window from screen recording during payments, and gives passwords only to the real sites.

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

## 🏦 Banking mode

A separate, isolated browser profile for banking, payments and shopping – with its own **encrypted vault**:

- **Opened with a hardware key or a password** – YubiKey, Google Titan and other FIDO2 keys. Key plugged in = one touch and you're in; the password is a backup when the key isn't with you. The key can also **encrypt the whole vault** (hmac-secret) – without it the data can't be read even if the files are copied.
- **🏦 My banks and 🛒 My online shops** – your sites, opened in banking mode with one click.
- **✏ Login details for each bank** – login / customer number, passcode / PIN, password and memorable information. Velivo **fills in the selected characters** your bank asks for (e.g. the 2nd, 5th and 9th – RBS, NatWest, Bank of Scotland, TSB, Lloyds, Halifax), drop-down lists included.
- **💳 My cards** – view, edit, CVV hidden until revealed, copying with a self-clearing clipboard, payment-form filling and expiry reminders.
- **📝 Notes** with categories (logins, PINs, transfers, recovery codes), **🎲 strong password generator**, **🔢 numbered password characters** and 🔍 vault search.
- **Protection**: warnings about **fake sites impersonating your bank**, a "Switch to banking mode" prompt when a listed bank or shop is opened in a normal tab, no extensions and no history, traces cleared on close, auto-lock after inactivity (1–60 min), access log.
- **Several banking profiles** (e.g. for another family member), **vault backup** to a `.vbank` file and **sync with your computers** on the home network – all encrypted.

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
| Banking mode | Separate profile for banking and shopping, hardware key (YubiKey, Titan) or password, encrypted vault: banks, shops, cards, notes, login details with selected-character filling, fake-site warnings, backup, LAN sync |
| Speed | Pages load when you hover a link, background page preparation, early server connections, selectable cache size; images don't burden the window (smooth scrolling e.g. on eBay) |
| Passwords | Sign-in from Sejf (key icon next to login fields), Velivo's own encrypted store, password generator, CSV import and export |
| LAN sync | Settings, bookmarks, passwords, history, pinned tabs and Quick Access; send tabs to another computer |
| Reading | Read aloud (Windows voices and natural Piper offline voices), "Read from here", reader mode with a local summary (no AI, no cloud), skips ads and "Read also" blocks |
| History & downloads | History by day → site → pages, "Where did I read that?" (Ctrl+Shift+F), download history with date and source |
| Search | Address bar shortcuts: `yt cats`, `allegro bike`, `wiki Kraków`, `mapy Gdańsk` (add your own), "Paste and go" |
| Look | Modern (calm, like Windows 11) or Colorful, 10 themes, light → dark → night mode with strength – **remembered per site** (like zoom), page entrance effects (from darkness, focus-in, dim) with a speed slider, phone version for a chosen site |
| Tools | Page cache on a RAM disk – just pick a folder, no external tools, download manager (up to 16 connections), full-page screenshots, page and selection translation, pin/unpin Chrome extensions, user profiles |
| Languages | Polish and English – chosen in the installer and in settings |

## Installation

Velivo installs from a single `Velivo-Setup-1.22.exe`, without administrator rights, into `%LOCALAPPDATA%\Programs\Velivo`.

**Requirements:** Windows 10 (version 1809 or newer) or 11, 64-bit. **.NET is built into the installer** – nothing else to install. Microsoft Edge WebView2 Runtime is usually already in Windows; if it is missing, the installer downloads it.

1. Download `Velivo-Setup-1.22.exe` from the [`Instalator`](Instalator) folder.
2. Run it and pick the language (Polski / English). Velivo will start in that language. If Windows shows "Unknown publisher", click "More info" → "Run anyway" (the installer is not digitally signed).
3. If Velivo was already installed, choose **Keep my settings and data** (normal update, recommended) or **Clean install** (start from scratch; old data is backed up with a `.kopia-<date>` suffix).
4. Optionally create a desktop shortcut and finish.

## License and name

Velivo™ – © 2026 andro ([github.com/mic0078](https://github.com/mic0078)). All rights reserved. You may download and use the program free of charge; copying the code, modifying it and using the Velivo name require the author's permission. Details: [LICENSE](LICENSE) and [TRADEMARKS.md](TRADEMARKS.md).
