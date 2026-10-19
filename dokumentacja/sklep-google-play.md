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

**Krótki (PL):** Lekka, prywatna przeglądarka: bez śledzenia, z blokadą reklam, czytnikiem i dużymi napisami.

**Pełny (PL):**
Velivo to lekka i prywatna przeglądarka – bez konta, bez chmury, bez śledzenia.
• Blokada reklam i elementów śledzących, bez pustych pól po reklamach
• Ochrona przed fałszywymi stronami banków i podejrzanymi aplikacjami
• Czytnik i czytanie artykułów na głos, także przy zgaszonym ekranie
• Powiększanie dwoma palcami – wygodne dla osób słabiej widzących
• Karteczki z przypomnieniem dzień wcześniej, pobudka z ulubionym utworem
• Tryb bankowy i karty prywatne, hasła szyfrowane w telefonie
• Synchronizacja z Velivo na Windows w sieci domowej – bez serwerów

**Short (EN):** A light, private browser: no tracking, ad blocking, reader view and large text.

**Full (EN):**
Velivo is a light, private browser – no account, no cloud, no tracking.
• Blocks adverts and trackers, with no empty boxes left behind
• Protection against fake bank sites and suspicious apps
• Reader view and read aloud, even with the screen off
• Two-finger zoom – comfortable for people with weaker eyesight
• Sticky notes with a reminder the day before, wake-up with your favourite track
• Banking mode and private tabs, passwords encrypted on the phone
• Sync with Velivo for Windows on your home network – no servers

W opisie i na zrzutach **nie** pisz o „YouTube bez reklam” ani o graniu YouTube w tle.
