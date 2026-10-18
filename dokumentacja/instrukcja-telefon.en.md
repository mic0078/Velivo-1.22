# Velivo for phones – manual

Velivo for Android is a light, private browser in Velivo colours, linked to Velivo for Windows. It needs Android 10 or newer. The computer is the "main" device: anything coming from the phone is always confirmed on the computer.

## 1. Screen and menu

- **Address bar** – type an address or search words (searches go through Startpage).
- **🕶** – new private tab. **The button with a number** – the tab list with thumbnails (tap = switch, ✕ or a sideways swipe = close).
- **Velivo logo** – the menu. Tiles are in three groups: **Tabs**, **Mine**, **Settings**. The blocked-ads counter is at the top.
- **Bottom bar** – ◀ back, ▶ forward, ⟳ reload, ⌂ start page, ＋ new tab (Quick Access).

## 2. Tabs

| Tile | What it does |
| --- | --- |
| ＋ New tab | Opens Quick Access – the same tiles and groups as on the computer |
| 🕶 New private tab | A separate profile (cookies, sign-ins), no history; erased when the last private tab closes |
| ⭐ Quick Access | Back to Quick Access in the current tab |
| 🏦 Banking mode | Fingerprint / PIN entry, a separate permanent profile, no history or screenshots, locks after 10 minutes idle |
| 🔎 Find in page | A bar with a "3/12" counter, ▲ ▼ – previous / next match, ✕ closes |
| 🖥 Desktop site | The desktop version of the page (for this tab); tap again for the mobile site |
| 📖 Reader view | Just the article text, without adverts or menus |
| 🎯 Remove element | Removes an advert or box with a tap of your finger |
| 🔊 Read aloud | Reads the article; while reading the tile turns into ⏹ Stop reading |

### Reader view

- **A− / A+** – text size (remembered).
- **🔊** – read aloud from the reader; **✕** or Back closes it (and stops reading).
- Velivo **remembers where you left off** on each page and takes you back there next time. This stays on the phone only.

### Read aloud

- Carries on with the **screen off** and in other apps – the notification shows "🔊 page title" with a **Stop** button. It disappears once the article is finished.
- Going back (◀ or Back) stops reading.
- No speech engine or voice for the phone's language – Velivo offers to **download Google Speech Services**.
- On Xiaomi phones set: Settings → Apps → Velivo → Battery saver → **No restrictions**, otherwise the system may cut reading short.

### Removing elements with your finger

1. Menu → **🎯 Remove element**. The page gets a light violet overlay and a bar appears at the top.
2. Tap the advert – it gets a violet outline. It also works on adverts inside frames (iframes).
3. **⬆** – select a larger area (when an empty grey box is left behind).
4. **🗑** – remove. The element disappears and takes up no space; it stays hidden on this site on later visits too.
5. **↺** – restore everything removed on this site. **✕** – finish.

The rules are kept on the phone only (they do not sync with the computer).

## 3. Mine

| Tile | What it does |
| --- | --- |
| Bookmarks, History | A list with search, sorting (newest / A–Z) and ✕ |
| Add bookmark | Adds the current page |
| Passwords | The password list after a fingerprint / PIN; **Copy password** marks it as sensitive in the clipboard |
| Fill in password | Fills in a saved password when the form appeared later |

Passwords are encrypted with a key in the Android Keystore (the key never leaves the phone). Velivo fills them in only on https pages and on the home network (router, NAS).

## 4. Settings

| Tile | What it does |
| --- | --- |
| Block ads | The same lists as on the computer (EasyList, EasyPrivacy, KAD), refreshed every 4 days; no adverts on YouTube |
| Play in background | Video / music keep playing after you leave the app (notification with "Stop") |
| Theme | Like the phone / light / dark – in Velivo colours |
| Sync with computer | Pairing, recovery file, phone name on the network |
| Clear history | Deletes the browsing history |

Change the app language (Polish / English) in Android Settings → Apps → Velivo → Language.

## 5. Connecting to the computer

1. Phone and computer on the same Wi-Fi, Velivo open on both.
2. Phone: menu → **Sync with computer → Pair with Velivo on Windows**.
3. Computer: Settings → Sync (LAN) → **Pair a device on the network…**. Compare the 6-digit code on both screens and confirm.

With several computers use the **recovery file** (computer: Save recovery file…, phone: Load pairing recovery file).

| What | How |
| --- | --- |
| Bookmarks, passwords, Quick Access | From the computer to the phone – automatically while Velivo is open |
| New bookmarks and passwords from the phone | To the computer – the computer asks whether to add them |
| A file from the phone | **Share → Velivo**; the computer asks for consent |
| A file from the computer | On the computer 📎 Send a file to: phone; saved to Downloads |

## 6. Privacy

- The phone never sends its whole profile – only new items, which the computer confirms.
- The connection is encrypted (AES-GCM) with the key from pairing; other phones on the network can read nothing.
- Your reading position, removed elements and settings stay on the phone.
