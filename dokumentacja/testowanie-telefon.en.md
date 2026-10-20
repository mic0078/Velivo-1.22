# Velivo for phone — tester guide

Thanks for helping test! This is a test build, so bugs may happen.

## 1. Installing
1. Open the received **Velivo-pelna.apk** (e.g. in WhatsApp or Downloads).
2. Allow installs from that source: **Settings → Allow from this source**, go back and tap **Install**.
3. If Play Protect warns ("unknown" or "harmful app"), it is because the app is not from Google Play. Choose **More details → Install anyway**.
4. If you get "App not installed" / package conflict, uninstall the older Velivo and retry.

## 2. What to test
- **Pages**: opening, tabs, pinch zoom (the menu zooms too), ad blocking.
- **YouTube**: playback, picture-in-picture after going Home, playing with the screen off, sleep timer 🌙.
- **🎵 My songs**: menu → ⭐ Add this song, then 🎵 My songs — sorting, own playlists, queue.
- **📺 TV** (your own M3U list – address or file):
  - channels A–Z, ⭐ favourites, search, groups (☰);
  - 📅: what's on now / tonight / tomorrow, **🗓 guide grid**, **📋 what's on when**, **⏰ scheduled**;
  - a programme in the guide: **⏰ Turn on at …** (starts by itself, also on the lock screen; can stop when it ends), **⏪ from the archive** (if the list has catch-up);
  - player: ⏮ ⏭ channels, 🗨 audio and subtitles, 🌙 sleep timer (also "after this programme"), ⏪ from the start / ⏩ live;
  - gestures: left side up/down – brightness, right side – volume.
- **🎬 Library (Jellyfin)**: the home server is found automatically – pick a user and type the password (no API keys). Every film plays, old AVI too.
- **Reader mode** and read-aloud, **sticky notes** with reminders, **alarm**.

## Updates
You get a new version the same way as the first one — the `Velivo-pelna.apk` file. Open it and tap **Update**: it installs over the current version, settings, channel lists, favourites and passwords stay. Do not uninstall the old version (you would lose your data).

## 3. Reporting bugs
- After a crash, the app shows a report on next start — tap **Copy** and send it to me.
- Briefly describe what you did, what happened, phone model and Android version. A screenshot helps a lot.

## 4. Privacy
Passwords and TV list addresses are encrypted on the phone. The app sends nothing to the author and never connects to someone's computer without your pairing.
