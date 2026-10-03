# Velivo

[Polski](README.md) | **English**

A lightweight, private browser for Windows that keeps your data with you: no account, no cloud, no history or passwords sent to outside servers. It runs on the Microsoft Edge engine (WebView2), so pages look the same as in Edge and Chrome.

Your computers share data through local network (LAN) sync, encrypted after the devices are paired. Velivo ships with the **Quick Access** add-on (a new tab page with shortcuts) and works with **Sejf**, a program that keeps passwords encrypted offline.

The interface is available in **Polish and English**. The installer asks for the language, and you can change it later in Settings → Appearance → Language.

**[⬇ Download the installer Velivo-Setup-1.22.exe](Instalator/Velivo-Setup-1.22.exe)**

## Features

| Area | What it offers |
| --- | --- |
| Privacy | Ad and tracker blocking (EasyList, EasyPrivacy, Polish list, about 95k rules), "Do Not Track", strict tracking prevention, SmartScreen, private tabs |
| Element blocking | Right-click → "Block element (ad)": point at a banner or floating ad and Velivo hides it on every visit |
| Site rules | Privacy panel: block JavaScript or cookies, clear data for a domain, trusted domains (no blocking) |
| Quick Access | New tab page: shortcuts in groups, icons, page thumbnails made in the background, PIN-protected profiles, background themes |
| Passwords | Sign-in from Sejf (key icon next to login fields), Velivo's own encrypted store, password generator, CSV import and export (e.g. from KeePassXC) |
| LAN sync | Settings, bookmarks, passwords and Quick Access between computers on your home network, encrypted after pairing with a code |
| Read aloud | Windows Polish and English voices plus natural offline Piper voices |
| Reader mode | Clean article text with a summary, volume slider, click the text to read from that point |
| Translation | Right-click → "Translate page" or translate the selected text |
| Appearance | 10 browser themes, dark and night mode for pages, remembered zoom |
| Tools | Multi-connection download manager, media detection, full-page screenshots, history, bookmarks, Chrome extensions |
| Profiles | Separate profiles (e.g. work, personal) with their own settings, history and bookmarks |

## Installation

Velivo installs from a single `Velivo-Setup-1.22.exe`, without administrator rights, into `%LOCALAPPDATA%\Programs\Velivo`.

**Requirements:** Windows 10 or 11 (64-bit), .NET 10 Desktop Runtime (x64) and Microsoft Edge WebView2 Runtime (usually already in Windows 11). If something is missing, the installer detects it and opens the download page.

1. Download `Velivo-Setup-1.22.exe` from the [`Instalator`](Instalator) folder.
2. Run it and pick the language (Polski / English). Velivo will start in that language. If Windows shows "Unknown publisher", click "More info" → "Run anyway" (the installer is not digitally signed).
3. If Velivo was already installed, choose **Keep my settings and data** (normal update, recommended) or **Clean install** (start from scratch; old data is backed up with a `.kopia-<date>` suffix).
4. Optionally create a desktop shortcut and finish.
