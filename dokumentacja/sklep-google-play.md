# Velivo w Sklepie Google Play – lista kroków

## Trzy wersje (jeden kod)

| | **pelna** (APK, rodzina) | **sklep** (Google Play, ostrożna) | **sklepyt** (Google Play, jak Brave) |
| --- | --- | --- | --- |
| Identyfikator | `pl.velivo.browser` | `pl.velivo` | `pl.velivo` |
| Skrypt usuwający reklamy z odtwarzacza YouTube | tak | nie | tak |
| YouTube w tle i w okienku (PiP) | tak | nie (inne filmy i muzyka – tak) | tak |
| Napis na pasku | „▶ YouTube · bez reklam” | „▶ YouTube” | „▶ YouTube” |
| Pobudka | dokładna bez pytania | po zgodzie „Alarmy i przypomnienia” | po zgodzie |
| Blokada reklam, ochrona, czytnik, karteczki, synchronizacja | tak | tak | tak |

Do sklepu wysyłasz **jedną**: `sklep` (najmniejsze ryzyko odrzucenia) albo `sklepyt` (jak Brave – ryzyko większe, ale bez reklamowania YouTube w opisie i na zrzutach zwykle przechodzi). Obie mają ten sam identyfikator, więc można zacząć od jednej i później podmienić na drugą.

W Android Studio: **Build → Select Build Variant** → `pelnaRelease`, `sklepRelease` albo `sklepytRelease`.

## Budowa dla sklepu

1. Build Variant: **sklepRelease** albo **sklepytRelease**.
2. **Build → Generate Signed App Bundle / APK… → Android App Bundle** → ten sam klucz (alias `velivo`) → wybrany wariant.
3. Plik: `android/app/sklep/release/Velivo-sklep-release.aab` (albo `sklepyt/release/Velivo-sklepyt-release.aab`).

## Play Console

1. Konto dewelopera (25 USD, weryfikacja tożsamości).
2. Utwórz aplikację: nazwa **Velivo**, język domyślny polski, bezpłatna, aplikacja (nie gra).
3. **Test zamknięty:** co najmniej 12 testerów przez 14 dni (wymóg dla nowych kont prywatnych).
4. **Polityka prywatności:** adres strony `docs/polityka-prywatnosci.html` (opublikowanej na stronie Velivo).
5. **Bezpieczeństwo danych:** „Aplikacja nie zbiera ani nie udostępnia danych użytkowników”; dane szyfrowane; użytkownik może je usunąć (odinstalowanie / Wyczyść historię).
6. **Uprawnienia:** usługa na pierwszym planie „mediaPlayback” – odtwarzanie dźwięku i filmów po wyjściu z aplikacji; alarmy – pobudka.
7. **Klasyfikacja treści:** przeglądarka (dostęp do internetu bez ograniczeń).
8. Grafika: ikona 512×512, grafika 1024×500, co najmniej 2 zrzuty ekranu telefonu.

## Opis w sklepie

**Krótki (PL):** Prywatna przeglądarka z telewizją, filmami i odtwarzaczem – lekka, bez śledzenia i reklam.

**Pełny (PL):**
Velivo to lekka i prywatna przeglądarka – bez konta, bez chmury, bez śledzenia – z wbudowaną telewizją i filmami.
• Blokada reklam i elementów śledzących, bez pustych pól po reklamach
• Ochrona przed fałszywymi stronami banków i podejrzanymi aplikacjami
• Telewizja z Twojej listy IPTV (M3U): program TV, siatka programu, archiwum, zaplanowane włączenie audycji
• Odtwarzacz na bibliotece VLC – każdy film i dźwięk, także stare AVI; gesty jasności i głośności, okienko, wyłącznik czasowy
• Twoja biblioteka Jellyfin w sieci domowej – bez kluczy, logowanie nazwą i hasłem
• Filmy i muzyka z telefonu w jednym miejscu, Velivo jako domyślny odtwarzacz i przeglądarka (gdy chcesz)
• Czytnik i czytanie na głos, powiększanie dwoma palcami, ciemny i czarno-biały motyw – wygodne dla słabowidzących
• Karteczki z przypomnieniem, tryb bankowy, karty prywatne, hasła szyfrowane w telefonie
• Synchronizacja z Velivo na Windows w sieci domowej – bez serwerów
Velivo nie dostarcza kanałów ani filmów – odtwarza Twoje własne listy i Twój serwer.

**Short (EN):** A private browser with TV, films and a player – light, no tracking, no adverts.

**Full (EN):**
Velivo is a light, private browser – no account, no cloud, no tracking – with built-in TV and films.
• Blocks adverts and trackers, with no empty boxes left behind
• Protection against fake bank sites and suspicious apps
• TV from your own IPTV list (M3U): TV guide, guide grid, catch-up, scheduled programmes
• Player built on the VLC library – every film and sound, old AVI too; brightness and volume gestures, picture-in-picture, sleep timer
• Your Jellyfin library on the home network – no keys, sign in with name and password
• Videos and music on the phone in one place, Velivo as default player and browser (if you want)
• Reader view and read aloud, two-finger zoom, dark and black-and-white themes – comfortable for weaker eyesight
• Sticky notes with reminders, banking mode, private tabs, passwords encrypted on the phone
• Sync with Velivo for Windows on your home network – no servers
Velivo provides no channels or films – it plays your own lists and your own server.

W opisie i na zrzutach **nie** pisz o „YouTube bez reklam” ani o graniu YouTube w tle.
