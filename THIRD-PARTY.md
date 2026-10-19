# Składniki zewnętrzne / Third-party components

## uBlock Origin Lite
- Autor / Author: Raymond Hill i współtwórcy / and contributors
- Licencja / License: GNU General Public License v3.0
- Kod źródłowy / Source code: https://github.com/uBlockOrigin/uBOL-home
- Dołączony do instalatora Velivo jako osobny, niezmieniony dodatek przeglądarki (pobierany z oficjalnych wydań przy budowie).
  Bundled with the Velivo installer as a separate, unmodified browser add-on (taken from official releases at build time).

## Słowniki do pisania przeciągnięciem (polski i angielski) / Swipe-typing word lists (Polish and English)
- Źródło / Source: FrequencyWords (Hermit Dave), pełna lista słów polskich i 50 000 najczęstszych angielskich z napisów OpenSubtitles (2018)
- Licencja / License: Creative Commons Attribution-ShareAlike 4.0 (CC BY-SA 4.0)
- Kod źródłowy / Source: https://github.com/hermitdave/FrequencyWords
- Zmiany / Changes: tylko słowa z polskich liter, bez powtórzeń bez polskich znaków, słownik polski 100 000 słów (pełna lista), angielski 40 000; bez liczb częstości (`src/slowa-pl.txt`, `src/slowa-en.txt`).
  Only Polish-letter words, duplicates without diacritics removed, 40,000 words, frequencies dropped.
- Uzupełnienie / Supplement: `src/slowa-pl-dodatek.txt` – własna lista Velivo (częste słowa brakujące w napisach filmowych: włączanie, przeglądarka, zakładka…). / Velivo's own list of common words missing from the subtitle corpus.

## Listy filtrów reklam w Velivo na Androida / Ad filter lists in Velivo for Android
- EasyList, EasyPrivacy – The EasyList authors, licencja / licence: GNU GPL v3 lub / or CC BY-SA 3.0 (https://easylist.to/pages/licence.html)
- Oficjalne Polskie Filtry (MajkiIT) – licencja / licence: CC BY-NC-SA 4.0 (https://github.com/MajkiIT/polish-ads-filter)
- Dołączone bez zmian do aplikacji (`android/app/src/main/assets/filtry-*.txt`), aby blokowanie działało od pierwszego uruchomienia; co 4 dni aplikacja pobiera świeższe wersje.
  Bundled unmodified so blocking works from the first launch; the app downloads fresher versions every 4 days.
