# Velivo for phones – manual

Velivo for Android is a light, private browser in Velivo colours, linked to Velivo for Windows. It needs Android 10 or newer. The computer is the "main" device: anything coming from the phone is always confirmed on the computer.

## 1. Screen and menu

- **Address bar** – type an address or search words (searches go through Startpage).
- **🕶** – new private tab. **The button with a number** – the tab list with thumbnails (tap = switch, ✕ or a sideways swipe = close).
- **Velivo logo** – the menu. Tiles are in four groups, three to a row: **Tabs**, **This page** (find, reader view, read aloud, remove element, add bookmark, fill in password), **Mine**, **Settings** (with a **🌐 Language** tile). The blocked-ads counter is at the top.
- **Bottom bar** – ◀ back, ▶ forward, ⟳ reload, ⌂ start page, ＋ new tab (Quick Access).
- **The red "▶ YouTube" bar at the very bottom** – YouTube without adverts; press and hold: sleep timer and wake-up (chapter 8).
- **Press and hold on an advert** or an empty "ADVERT" box – Velivo asks whether to remove it.

## 1a. Zoom (for weaker eyesight)

- **Spread two fingers** in the menu or the lists of tabs, bookmarks, history, passwords and notes – everything gets bigger; pinch – smaller (80–220%). The top bar and the bottom buttons grow too, and at large sizes the menu shows tiles two to a row.
- **On a page** two fingers change that page's text size (50–300%) – **every site remembers its own size** (the start page / Quick Access always stays at 100%), and Velivo remembers its own separately. Set it once.
- In **reader view** two fingers change the text size (up to 48 pt), like A− / A+.

## 2. Tabs and This page

**Tabs:**

| Tile | What it does |
| --- | --- |
| ＋ New tab | Opens Quick Access – the same tiles and groups as on the computer |
| 🕶 New private tab | A separate profile (cookies, sign-ins), no history; erased when the last private tab closes |
| ⭐ Quick Access | Back to Quick Access in the current tab |
| 🏦 Banking mode | Fingerprint / PIN entry, a separate permanent profile, no history or screenshots, locks after 10 minutes idle |
| 📤 Send to computer | The current page opens on the paired computer (with the video position); private and banking tabs aren't sent |
| 🖥 Desktop site | The desktop version of the page (for this tab); tap again for the mobile site |

**This page:**

| Tile | What it does |
| --- | --- |
| 🔎 Find in page | A bar with a "3/12" counter, ▲ ▼ – previous / next match, ✕ closes |
| 📖 Reader view | Just the article text, without adverts or menus |
| 🎯 Remove element | Removes an advert or box with a tap of your finger |
| 🔊 Read aloud | Reads the article; while reading the tile turns into ⏹ Stop reading |
| ➕ Add bookmark | Adds the current page to bookmarks |
| ✍ Fill in password | Fills in a saved password when the form appeared later |

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
2. Tap the advert – Velivo picks the whole advert box (violet outline) and asks **"Remove?"**: **Remove**, **Select a larger area** or **Cancel**. It also works on adverts inside frames (iframes).
3. On the bar: **⬆** – larger area, **📋** – copy the element's code (to report an advert Velivo doesn't hide).
4. **🗑** – remove. The element disappears and takes up no space; it stays hidden on this site on later visits too.
5. **↺** – restore everything removed on this site. **✕** – finish.

Quicker: **press and hold** on an advert or an empty "ADVERT" box – Velivo asks whether to remove it (on text and links a long press works as usual). Velivo checks every layer under your finger, so it also finds adverts hidden beneath a transparent layer of the page.

The rules are kept on the phone only (they do not sync with the computer).

## 3. Mine

| Tile | What it does |
| --- | --- |
| 🔖 Bookmarks, 🕘 History | A list with search, sorting (newest / A–Z) and ✕ |
| 🔑 Passwords | The password list after a fingerprint / PIN; **Copy password** marks it as sensitive in the clipboard |
| 🗒 Sticky notes, 📝 New note | Notes as on Windows – see below |
| 📥 Downloads | Downloaded files |

Passwords are encrypted with a key in the Android Keystore (the key never leaves the phone). Velivo fills them in only on https pages and on the home network (router, NAS).

### Sticky notes

1. Menu → **📝 New note** → choose a kind: 📝 Note, ✅ To do, 🛒 Shopping, 📞 Ring, ❗ Important, 💡 Idea, ⭐ For today.
2. Type. **The deadline** is a date in the text (e.g. `24.10`, `24.10.2026`, `2026-10-24`) – or tap **📅** and pick a day from the calendar (it is inserted at the cursor).
3. **🔁 Repeat deadline:** one-off, every week, every 2 weeks, every month, every year or **Custom interval…** (every N days, weeks, months, years). Velivo counts the next dates from the one in the text, e.g. "4.10 bins" every week → 11.10, 18.10…
4. **🎨** changes the colour, **🗑** deletes, **✓** (or Back) saves.

**Reminder:** a notification **the day before** ("⏰ Due tomorrow") and **on the day** ("⏰ Due today") – daily at about 9:00, also after the phone is switched back on. Tapping it opens the note. The **🗒 Sticky notes** list is sorted by the nearest date; the date and repeat show under the text.

## 4. Settings

| Tile | What it does |
| --- | --- |
| Block ads | The same lists as on the computer (EasyList, EasyPrivacy, KAD) – bundled with the app, refreshed every 4 days. No empty box or error frame is left where an advert was (hiding rules as in uBlock Origin); no adverts on YouTube |
| Play in background | Video / music keep playing after you leave the app and with the screen off (notification with "Stop" and "⏾ Stop in 30 min"); a video moves to a picture-in-picture window |
| Theme | Like the phone / light / dark – in Velivo colours |
| Sync with computer | Pairing, recovery file, phone name on the network |
| 🌐 Language | Polish or English – for Velivo only (the system app-language picker) |
| Clear history | Deletes the browsing history |

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
| A tab from the phone | Menu → 📤 Send to computer |
| A tab from the computer | On the computer: right-click a tab → 📺 Send to… → phone; the phone opens it straight away (YouTube from the same point) |

## 6. Privacy

- **Privacy panel:** menu → tap the green "🛡 Blocked ads" counter at the top. It shows whether the connection is encrypted (🔒 https), how many adverts and trackers were blocked on this page and from which domains. **Turn off blocking here** – for a site that misbehaves with blocking (remembered for that site; **Turn on blocking here** undoes it).
- **Scam protection** (works on its own, offline, as in Velivo for Windows):
  - **fake sites** of banks and well-known companies (e.g. `mbank-logowanie.com`, `rnbank.pl`, `paypa1.com`, `olx.pl.dostawa-pay.xyz`, addresses with characters disguised as letters) – a red warning, **Go back (safe)** or **I know this site – continue**; fakes of sites you have saved passwords for are caught too;
  - **app (APK) downloads from outside Google Play** – a warning, **Don't download** by default;
  - **HTTPS first** – pages open encrypted; if that fails, Velivo asks whether to open the page unencrypted (home network – router, NAS – without asking). A bad certificate is always blocked.
- The phone never sends its whole profile – only new items, which the computer confirms.
- The connection is encrypted (AES-GCM) with the key from pairing; other phones on the network can read nothing.
- Your reading position, removed elements and settings stay on the phone.

## 7. Velivo for people with weaker eyesight – step-by-step setup

Best set up once, together with the person who will use the phone:

1. **Size:** open the menu (the V logo) and spread two fingers in it until the text is comfortable (up to 220%). All of Velivo grows – the bar, buttons, menu, lists and page text. Velivo remembers it.
2. **Theme:** menu → 🌓 Theme → light or dark – whichever is easier to see.
3. **Reading:** on a long article, menu → 📖 **Reader view** (just the text, two fingers = larger text) or 🔊 **Read aloud** (works with the screen off too; Back stops it).
4. **Safety:** warnings about fake bank sites and about installing apps from outside Google Play work on their own – it is worth saying: "if Velivo shows **⚠ Warning**, tap **Go back (safe)**".
5. **Reminders:** 🗒 Sticky notes with a date (e.g. doctor 24.10) – a notification the day before and on the day; 🔁 for medicines every week or month.
6. **Adverts:** blocked straight away; for a leftover "ADVERT" box press and hold on it and tap **Remove**.

## 8. YouTube without adverts, picture-in-picture and music for sleep

- **The red "▶ YouTube" bar at the very bottom** – opens YouTube (or returns to the open YouTube tab). Adverts are removed.
- **Plays in the background and with the screen off:** start a video or music, leave Velivo or turn the screen off – it keeps playing; **Stop** is in the notification.
- **Picture-in-picture (PiP):** when a video is playing and you go to the home screen, it moves to a small window above other apps (you can move it; tap to return to Velivo).
- **⏾ Music for sleep (sleep timer):** press and hold the red YouTube bar and choose **When this track ends** (YouTube won't start the next one) or 15, 30, 45, 60, 90 minutes – then the music stops by itself. With the screen off the notification has **⏾ Stop in 30 min** (each tap adds 30 minutes).
- **⏰ Wake-up / scheduled playback:** press and hold the red YouTube bar → **⏰ Wake-up with a track…** → choose the track: **from the current tab** (e.g. an open YouTube video), **from bookmarks** or **from history** → the time → when: **every day, Mon–Fri, weekend, chosen days** or **once**. At that time Velivo opens even on the lock screen and plays the track with the volume rising gently; **Turn off** or **Snooze 10 min**. In the same place: **▶ Try the wake-up now** and **Turn off wake-up**. The alarm icon in the phone's status bar shows a wake-up is set.
- The sleep timer also has **Custom time (minutes)…** – any number of minutes.

## 9. When something goes wrong

- If Velivo closes because of an error, next time it opens it shows **"Velivo closed because of an error"** with a **Copy** button – send that description to the Velivo developer (no addresses or page content – just how the error happened).
- An advert Velivo doesn't hide: 🎯 Remove element → tap it → **📋** copies the element's code for a report.
- A site misbehaves because of ad blocking: the privacy panel (the 🛡 counter in the menu) → **Turn off blocking here**.
- Reading or music stops after the screen turns off (Xiaomi): Settings → Apps → Velivo → Battery saver → **No restrictions**.
- The wake-up didn't ring: check the phone isn't muted and that Velivo has **Alarms & reminders** permission in Android Settings.
