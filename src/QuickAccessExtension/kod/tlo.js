'use strict';
importScripts('jezyk-wybor.js', 'jezyk.js');

// =====================================================================
//  Szybki Dostep - proces tla
// =====================================================================
//  Obsluguje menu kontekstowe: prawy przycisk na dowolnej stronie ->
//  "Dodaj do Szybkiego Dostepu" -> wybor grupy.
//
//  UPRAWNIENIA: contextMenus + activeTab. To drugie daje dostep do adresu
//  i tytulu WYLACZNIE tej karty i WYLACZNIE w chwili, gdy sam klikniesz
//  pozycje w menu. Nie ma tu zadnego staleg dostepu do przegladanych stron.
// =====================================================================

const KLUCZ = 'szybkiDostep';
const KORZEN = 'sd-dodaj';

// --- profile ---------------------------------------------------------
// Proces tla musi trafic do danych TEGO profilu, ktory jest teraz otwarty,
// inaczej menu kontekstowe pokazywaloby cudze grupy. Aktywny profil siedzi
// w pamieci przegladarki - nie w pliku, bo jest sprawa tego komputera.
const PROFIL_DOMYSLNY = 'Domyslny';
const KLUCZ_PROFIL = 'aktywnyProfil';
const PODF_DANE = 'dane';
const PLIK_PROFILI = 'profile.json';

function bezpiecznaNazwa(n) {
  const czysta = String(n || '').replace(/[^a-zA-Z0-9 _-]/g, '_').trim().slice(0, 40);
  return czysta || 'Profil';
}

async function aktywnyProfil() {
  try {
    const z = await chrome.storage.local.get(KLUCZ_PROFIL);
    return z[KLUCZ_PROFIL] || PROFIL_DOMYSLNY;
  } catch (e) { return PROFIL_DOMYSLNY; }
}

function kluczDanych(p) {
  return p === PROFIL_DOMYSLNY ? KLUCZ : KLUCZ + '::' + bezpiecznaNazwa(p);
}

function folderProfilu(p) {
  return p === PROFIL_DOMYSLNY ? PODF_DANE : PODF_DANE + '/profil-' + bezpiecznaNazwa(p);
}

async function dane() {
  const k = kluczDanych(await aktywnyProfil());
  const z = await chrome.storage.local.get(k);
  const d = z[k];
  if (d && Array.isArray(d.grupy) && d.grupy.length) return d;
  return { grupy: [{ nazwa: 'Start', skroty: [] }], aktywna: 0, zapisano: 0 };
}

function nazwaZUrl(url) {
  try {
    const h = new URL(url).hostname.replace(/^www\./, '');
    return h.split('.')[0].replace(/^./, (c) => c.toUpperCase());
  } catch (e) { return url; }
}

// --------------------------------------------------------- budowa menu
// Lista profili - najpierw z pliku (jest zasobem rozszerzenia, wiec odczyt
// nie wymaga zadnych uprawnien), a gdyby go nie bylo, z kluczy w magazynie.
async function listaProfili() {
  // 1. Lista podana przez strone nowej karty - najpewniejsze zrodlo.
  //    Strona ma most i uchwyt do folderu, wiec odczyta profile.json
  //    nawet tam, gdzie samemu procesowi tla sie to nie udaje.
  try {
    const c = await chrome.storage.local.get('listaProfiliCache');
    const w = c.listaProfiliCache;
    if (w && Array.isArray(w.profile) && w.profile.length) return w.profile;
  } catch (e) { /* probujemy dalej */ }

  // 2. Wlasny odczyt pliku jako zasobu rozszerzenia.
  try {
    const odp = await fetch(chrome.runtime.getURL(PODF_DANE + '/' + PLIK_PROFILI), { cache: 'no-store' });
    if (odp.ok) {
      const o = await odp.json();
      if (o && Array.isArray(o.profile) && o.profile.length) return o.profile;
    }
  } catch (e) { /* brak pliku - jest tylko profil domyslny */ }
  try {
    const w = await chrome.storage.local.get(null);
    const lista = [PROFIL_DOMYSLNY];
    const przedrostek = KLUCZ + '::';
    for (const k of Object.keys(w)) {
      if (k.indexOf(przedrostek) === 0) {
        const n = k.slice(przedrostek.length);
        if (n && !lista.includes(n)) lista.push(n);
      }
    }
    return lista;
  } catch (e) { return [PROFIL_DOMYSLNY]; }
}

// Dane WSKAZANEGO profilu - z magazynu, a gdy go tam nie ma (np. profil
// zalozony na drugim laptopie), wprost z pliku tego profilu.
async function daneProfilu(p) {
  const k = kluczDanych(p);
  const z = await chrome.storage.local.get(k);
  const d = z[k];
  if (d && Array.isArray(d.grupy) && d.grupy.length) return d;
  try {
    const odp = await fetch(chrome.runtime.getURL(folderProfilu(p) + '/kopia.json'), { cache: 'no-store' });
    if (odp.ok) {
      const o = await odp.json();
      if (o && Array.isArray(o.grupy) && o.grupy.length) return o;
    }
  } catch (e) { /* nowy profil, jeszcze pusty */ }
  return { grupy: [{ nazwa: 'Start', skroty: [] }], aktywna: 0, zapisano: 0 };
}

// --------------------------------------------------------- budowa menu
//  W menu pokazujemy WSZYSTKIE profile naraz, kazdy ze swoimi grupami.
//  Dzieki temu menu nie musi zgadywac, ktory profil jest teraz otwarty -
//  a przy okazji mozna wrzucic strone do cudzej tablicy bez przelaczania sie.
//
//  DECYZJA (swiadoma, nie przeoczenie): nazwy grup profilu na PIN sa w menu
//  widoczne, zanim ktokolwiek poda kod. Inaczej nie dalo by sie wskazac celu.
//  Wlasciciel wie o tym i sie zgadza - zamkiem jest PIN przy DODAWANIU
//  i przy wejsciu w profil, a nie ukrywanie nazw grup. Nie "naprawiac".
const KONTEKSTY = ['page', 'link', 'image', 'selection'];

// chrome.contextMenus.create nie zwraca obietnicy - opakowujemy je, zeby
// dalo sie CZEKAC na utworzenie kazdej pozycji. Bez tego kolejna przebudowa
// zaczynala sie w polowie poprzedniej i sypala bledem "duplicate id".
function utworzPozycje(opcje) {
  return new Promise((gotowe) => {
    chrome.contextMenus.create(opcje, () => {
      void chrome.runtime.lastError;    // odczytanie wycisza ostrzezenie
      gotowe();
    });
  });
}

async function zbudujMenuTeraz() {
  await chrome.contextMenus.removeAll();
  const profile = await listaProfili();

  await utworzPozycje({ id: KORZEN, title: SD_T('Dodaj do Szybkiego Dostepu'), contexts: KONTEKSTY });

  // przy jednym profilu nie robimy zbednego poziomu w menu
  const jeden = profile.length <= 1;

  for (const p of profile) {
    const d = await daneProfilu(p);
    let rodzic = KORZEN;
    if (!jeden) {
      rodzic = KORZEN + '|p|' + p;
      await utworzPozycje({ id: rodzic, parentId: KORZEN, title: p, contexts: KONTEKSTY });
    }
    for (let i = 0; i < d.grupy.length; i++) {
      const g = d.grupy[i];
      await utworzPozycje({
        // nazwa profilu siedzi w identyfikatorze - proces tla moze zostac
        // uspiony miedzy zbudowaniem menu a klknieciem i stracic pamiec
        id: KORZEN + '|g|' + p + '|' + i,
        parentId: rodzic,
        title: g.nazwa + '   (' + g.skroty.length + ')',
        contexts: KONTEKSTY
      });
    }
  }
}

// Powodow do przebudowy menu jest kilka (start, zmiana w pamieci, meldunek
// z nowej karty) i potrafia wypasc w tej samej chwili. Wpuszczamy wiec tylko
// JEDNA budowe naraz, a zgloszenia z czasu jej trwania zaliczamy jako jedno
// powtorzenie na koniec - dzieki temu menu zawsze konczy na aktualnych danych.
let budowaTrwa = null;
let budowaPonownie = false;

function zbudujMenu() {
  if (budowaTrwa) { budowaPonownie = true; return budowaTrwa; }
  budowaTrwa = (async () => {
    try {
      do {
        budowaPonownie = false;
        await zbudujMenuTeraz();
      } while (budowaPonownie);
    } catch (e) {
      // menu to nie powod, zeby wywalac caly proces tla
    } finally {
      budowaTrwa = null;
    }
  })();
  return budowaTrwa;
}

// ------------------------------------------------------------ dodanie
async function dodajSkrot(profilDocelowy, indeksGrupy, url, tytul) {
  const d = await daneProfilu(profilDocelowy);
  const g = d.grupy[indeksGrupy];
  if (!g) return { ok: false, powod: 'Grupa juz nie istnieje' };

  if (g.skroty.some((s) => s.url === url)) {
    return { ok: false, powod: 'Ta strona juz jest w grupie ' + g.nazwa };
  }

  g.skroty.push({
    url,
    nazwa: (tytul && tytul.trim()) ? tytul.trim() : nazwaZUrl(url),
    dodano: Date.now()          // znacznik potrzebny przy scalaniu miedzy laptopami
  });
  d.zapisano = Date.now();          // dzieki temu wymiana przez plik to podchwyci
  await chrome.storage.local.set({ [kluczDanych(profilDocelowy)]: d });
  return { ok: true, grupa: g.nazwa, profil: profilDocelowy };
}

function pokazWynik(tekst, dobry) {
  // Przegladarki na WebView2 (Velivo) nie maja ikonki dodatku na pasku - bez tego wyjatek przerywal dodawanie skrotu
  if (!chrome.action || !chrome.action.setBadgeText) return;
  try {
  chrome.action.setBadgeText({ text: dobry ? 'OK' : '!' });
  chrome.action.setBadgeBackgroundColor({ color: dobry ? '#16a34a' : '#b45309' });
  chrome.action.setTitle({ title: tekst });
  setTimeout(() => {
    chrome.action.setBadgeText({ text: '' });
    chrome.action.setTitle({ title: SD_T('Szybki Dostep') });
  }, 3000);
  } catch (e) { /* brak paska dodatkow */ }
}

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const id = String(info.menuItemId || '');
  if (id.indexOf(KORZEN + '|g|') !== 0) return;

  // KORZEN|g|<nazwa profilu>|<numer grupy> - nazwa profilu moze zawierac
  // spacje i myslniki, ale nigdy pionowej kreski (pilnuje tego bezpiecznaNazwa)
  const reszta = id.slice((KORZEN + '|g|').length);
  const kreska = reszta.lastIndexOf('|');
  if (kreska < 1) return;
  const profilDocelowy = reszta.slice(0, kreska);
  const indeks = parseInt(reszta.slice(kreska + 1), 10);
  if (Number.isNaN(indeks)) return;

  // Klikniecie w link dodaje ten link, w pozostalych przypadkach - biezaca strone.
  const url = info.linkUrl || info.pageUrl || (tab && tab.url);
  if (!url) { pokazWynik('Nie udalo sie odczytac adresu', false); return; }

  // Tytul karty mamy dzieki activeTab; przy linku wolimy jego tekst.
  let tytul = '';
  if (info.linkUrl) tytul = (info.selectionText || '').trim();
  if (!tytul && tab && tab.title) tytul = tab.title;

  // ZRZUT STRONY - robimy go ZANIM otworzymy tablice, bo pozniej widoczna
  // bylaby juz ona, a nie strona uzytkownika. captureVisibleTab dziala dzieki
  // uprawnieniu activeTab, przyznanemu w chwili klikniecia w menu.
  let zrzut = null;
  if (!info.linkUrl && tab && tab.windowId !== undefined) {
    try {
      zrzut = await chrome.tabs.captureVisibleTab(tab.windowId, { format: 'jpeg', quality: 70 });
    } catch (e) { /* strona chroniona - trudno, zostanie sama ikona */ }
  }

  // PIN: profil chroniony wymaga kodu, chyba ze wlasnie w nim siedzisz -
  // wtedy zamek zostal juz otwarty przy wejsciu i nie ma po co pytac drugi raz.
  const aktywnyTeraz = await aktywnyProfil();
  if (profilDocelowy !== aktywnyTeraz && await profilMaPin(profilDocelowy)) {
    await chrome.storage.session.set({
      oczekujaceDodanie: { profil: profilDocelowy, indeks, url, tytul, zrzut, czas: Date.now() }
    });
    try {
      await chrome.windows.create({
        url: chrome.runtime.getURL('kod/pin.html'),
        type: 'popup', width: 460, height: 300
      });
    } catch (e) { pokazWynik('Nie udalo sie otworzyc okna PIN', false); }
    return;
  }

  const w = await dodajSkrot(profilDocelowy, indeks, url, tytul);
  const gdzie = (profilDocelowy === PROFIL_DOMYSLNY)
    ? ('Dodano do grupy ' + w.grupa)
    : ('Dodano do grupy ' + w.grupa + ' (profil ' + profilDocelowy + ')');
  pokazWynik(w.ok ? gdzie : w.powod, w.ok);
  if (!w.ok) return;

  // Zrzut oddajemy stronie nowej karty - tylko ona ma uchwyt do folderu
  // i moze zapisac plik. Service worker nie moze prosic o zgode.
  // Miniatura zapisuje sie do folderu profilu, wiec musi wiedziec, czyja jest.
  if (zrzut) {
    try {
      await chrome.storage.local.set({
        miniaturaDoZapisu: { url, dataUrl: zrzut, kiedy: Date.now(), profil: profilDocelowy }
      });
    } catch (e) { /* brak miejsca - pomijamy */ }
  }

  zbudujMenu();          // odswiez liczniki przy nazwach grup

  // Tablice otwieramy TYLKO wtedy, gdy skrot poszedl do profilu, ktory jest
  // teraz otwarty. Inaczej pokazalaby sie cudza tablica bez nowego skrotu
  // i wygladaloby to na blad.
  const aktywny = await aktywnyProfil();
  if (profilDocelowy !== aktywny) return;

  // Informacje dla strony zapisujemy PRZED utworzeniem karty - inaczej
  // strona moglaby zdazyc sie wczytac, zanim tam trafia.
  try {
    await chrome.storage.session.set({
      ostatnioDodane: {
        nazwa: (tytul && tytul.trim()) ? tytul.trim() : nazwaZUrl(url),
        grupa: w.grupa,
        indeksGrupy: indeks,
        zrodloTab: (tab && tab.id) ? tab.id : null,
        czas: Date.now()
      }
    });
    await chrome.tabs.create({ active: true });
  } catch (e) { /* gdy sie nie uda, skrot i tak jest dodany */ }
});

// Klikniecie ikony na pasku otwiera nowa karte, czyli nasza tablice.
// Bez chrome.action (WebView2) wyjatek w tym miejscu zatrzymywal reszte procesu tla:
// menu kontekstowe, PIN i hasla z Sejfu przestawaly dzialac.
try { chrome.action.onClicked.addListener(() => chrome.tabs.create({})); } catch (e) { /* brak paska dodatkow */ }

// ---------------------------------------------------------------------
//  Aktualizacja z kopia.json przy starcie przegladarki
// ---------------------------------------------------------------------
//  kopia.json lezy w folderze rozszerzenia, wiec jest jego zasobem -
//  odczyt nie wymaga ZADNEJ zgody i dziala od razu po starcie, nawet gdy
//  uzytkownik nie otworzyl jeszcze nowej karty. Dzieki temu menu
//  kontekstowe od pierwszej chwili zna aktualne grupy.
async function zsynchronizujZPliku() {
  // Sciezki sa WZGLEDNE wobec folderu rozszerzenia - dziala z kazdej
  // lokalizacji i dysku. Czytamy dane AKTYWNEGO profilu; dla domyslnego
  // dochodzi jeszcze stare miejsce w korzeniu, dla zgodnosci wstecz.
  const p = await aktywnyProfil();
  const sciezki = [folderProfilu(p) + '/kopia.json'];
  if (p === PROFIL_DOMYSLNY) sciezki.push('kopia.json');

  for (const sciezka of sciezki) {
    try {
      const odp = await fetch(chrome.runtime.getURL(sciezka), { cache: 'no-store' });
      if (!odp.ok) continue;
      const o = await odp.json();
      if (!o || !Array.isArray(o.grupy) || !o.grupy.length) continue;

      const biezace = await dane();
      if ((o.zapisano || 0) <= (biezace.zapisano || 0)) return false;

      await chrome.storage.local.set({ [kluczDanych(p)]: o });
      return true;
    } catch (e) { /* szukamy dalej */ }
  }
  return false;
}

async function startowo() {
  await zsynchronizujZPliku();
  await zbudujMenu();
}

// =====================================================================
//  PIN PROFILU PO STRONIE PROCESU TLA
// =====================================================================
//  Proces tla nie ma okna i nie moze o nic zapytac, wiec przy profilu na
//  PIN otwiera male okienko (pin.html). Ono odsyla tu wpisany kod, a
//  sprawdzenie i samo dodanie zostaje TUTAJ - okienko nigdy nie dostaje
//  danych profilu i nie moze niczego ominac.
async function wczytajPiny() {
  // to samo pewniejsze zrodlo, co przy liscie profili
  try {
    const c = await chrome.storage.local.get('listaProfiliCache');
    const w = c.listaProfiliCache;
    if (w && w.piny) return w.piny;
  } catch (e) { /* probujemy dalej */ }
  try {
    const odp = await fetch(chrome.runtime.getURL(PODF_DANE + '/' + PLIK_PROFILI), { cache: 'no-store' });
    if (odp.ok) {
      const o = await odp.json();
      if (o && o.piny) return o.piny;
    }
  } catch (e) { /* brak pliku - nie ma zadnych PIN-ow */ }
  return {};
}

async function profilMaPin(p) {
  const piny = await wczytajPiny();
  const w = piny[p];
  return !!(w && w.sol && w.skrot);
}

async function skrotPin(pin, sol) {
  const bajty = new TextEncoder().encode(sol + '|' + String(pin));
  const wynik = await crypto.subtle.digest('SHA-256', bajty);
  return Array.from(new Uint8Array(wynik)).map((x) => x.toString(16).padStart(2, '0')).join('');
}

// Strona nowej karty melduje przelaczenie profilu wprost tutaj. Zdarzenie
// magazynu zwykle wystarcza, ale wiadomosc jest pewniejsza: budzi uspiony
// proces tla natychmiast, a nie dopiero przy nastepnej okazji.
chrome.runtime.onMessage.addListener((wiadomosc, nadawca, odpowiedz) => {
  if (!wiadomosc) return;

  if (wiadomosc.typ === 'profilZmieniony') { zbudujMenu(); return; }

  if (wiadomosc.typ === 'oczekujaceDodanie') {
    chrome.storage.session.get('oczekujaceDodanie').then((s) => {
      const o = s.oczekujaceDodanie;
      // po 5 minutach uznajemy, ze to juz nieaktualne
      if (!o || (Date.now() - (o.czas || 0)) > 300000) { odpowiedz({ ok: false }); return; }
      odpowiedz({ ok: true, profil: o.profil });
    });
    return true;
  }

  if (wiadomosc.typ === 'dodajZPinem') {
    (async () => {
      const s = await chrome.storage.session.get('oczekujaceDodanie');
      const o = s.oczekujaceDodanie;
      if (!o || (Date.now() - (o.czas || 0)) > 300000) {
        odpowiedz({ ok: false, powod: 'Zgloszenie wygaslo - sprobuj jeszcze raz.' });
        return;
      }
      const piny = await wczytajPiny();
      const wpis = piny[o.profil];
      if (!wpis || (await skrotPin(String(wiadomosc.pin || '').trim(), wpis.sol)) !== wpis.skrot) {
        odpowiedz({ ok: false, powod: 'Zly PIN.' });
        return;
      }
      await chrome.storage.session.remove('oczekujaceDodanie');

      const w = await dodajSkrot(o.profil, o.indeks, o.url, o.tytul);
      if (!w.ok) { odpowiedz({ ok: false, powod: w.powod }); return; }

      if (o.zrzut) {
        try {
          await chrome.storage.local.set({
            miniaturaDoZapisu: { url: o.url, dataUrl: o.zrzut, kiedy: Date.now(), profil: o.profil }
          });
        } catch (e) { /* brak miejsca - pomijamy */ }
      }
      zbudujMenu();
      pokazWynik('Dodano do grupy ' + w.grupa + ' (profil ' + o.profil + ')', true);
      odpowiedz({ ok: true, grupa: w.grupa, profil: o.profil });
    })();
    return true;
  }
});

// Menu odswiezamy przy starcie i po kazdej zmianie grup.
chrome.runtime.onInstalled.addListener(startowo);
chrome.runtime.onStartup.addListener(startowo);
chrome.storage.onChanged.addListener((zmiany, obszar) => {
  // przebudowa menu takze po przelaczeniu profilu i po zmianie danych
  // dowolnego profilu - klucze innych profili maja przyrostek "::nazwa"
  if (obszar !== 'local') return;
  const dotyczy = Object.keys(zmiany).some(
    (k) => k === KLUCZ || k.indexOf(KLUCZ + '::') === 0 ||
           k === KLUCZ_PROFIL || k === 'listaProfiliCache');
  if (dotyczy) zbudujMenu();
});

// Jedno polaczenie do sejfu dla panelu hasel. Dzieki temu most nie jest
// uruchamiany od nowa przy kazdym otwarciu popupu, a koszt PBKDF2 placimy
// tylko przy pierwszym zapytaniu w sesji przegladarki.
let sejfPort = null;
let sejfNumer = 0;
const sejfOczekujace = new Map();

function zapytajSejf(wiadomosc) {
  return new Promise((resolve, reject) => {
    try {
      if (!sejfPort) {
        sejfPort = chrome.runtime.connectNative('pl.szybkidostep.most');
        sejfPort.onMessage.addListener((odpowiedz) => {
          const oddaj = sejfOczekujace.get(odpowiedz && odpowiedz.id);
          if (oddaj) { sejfOczekujace.delete(odpowiedz.id); oddaj(odpowiedz); }
        });
        sejfPort.onDisconnect.addListener(() => {
          for (const [, oddaj] of sejfOczekujace) oddaj(null);
          sejfOczekujace.clear();
          sejfPort = null;
        });
      }
      const id = ++sejfNumer;
      wiadomosc.id = id;
      sejfOczekujace.set(id, resolve);
      sejfPort.postMessage(wiadomosc);
      setTimeout(() => {
        if (sejfOczekujace.has(id)) { sejfOczekujace.delete(id); reject(new Error('Brak odpowiedzi sejfu.')); }
      }, 20000);
    } catch (e) { reject(e); }
  });
}

// Zapytania ze stron (ikonka w polu, pasek zapisu) dostaja TYLKO to, co dotyczy
// strony, na ktorej naprawde sa: domene bierzemy z adresu karty, nie z wiadomosci,
// tylko z glownej ramki i tylko trzy polecenia. Okienko dodatku (bez karty) - bez zmian.
const SEJF_ZE_STRONY = ['sejf-szukaj', 'sejf-dodaj', 'sejf-zapisz'];

function sejfZapytanieZeStrony(dane, nadawca) {
  if (!nadawca.tab) return dane;
  // Wlasne strony dodatku (okienko hasel) traktujemy jak okienko bez karty. W Chrome okienko nie ma
  // karty, ale w przegladarkach na WebView2 kazde okienko jest osobna karta z adresem chrome-extension://.
  if (nadawca.id === chrome.runtime.id && (nadawca.url || '').startsWith(chrome.runtime.getURL(''))) return dane;
  if (nadawca.frameId !== 0) throw new Error('Sejf nie odpowiada ramkom wewnatrz strony.');
  if (!dane || !SEJF_ZE_STRONY.includes(dane.c)) throw new Error('Tego polecenia strona nie moze wyslac.');
  let adres;
  try { adres = new URL(nadawca.url || nadawca.tab.url); } catch (e) { throw new Error('Nieznany adres strony.'); }
  if (adres.protocol !== 'https:' && adres.protocol !== 'http:') throw new Error('To nie jest zwykla strona WWW.');
  return Object.assign({}, dane, { domena: adres.hostname.toLowerCase() });
}

chrome.runtime.onMessage.addListener((wiadomosc, nadawca, odpowiedz) => {
  if (!wiadomosc || wiadomosc.typ !== 'sejfZapytanie') return;
  let dane;
  try { dane = sejfZapytanieZeStrony(wiadomosc.dane, nadawca); }
  catch (blad) { odpowiedz({ ok: false, blad: blad.message }); return; }
  zapytajSejf(dane)
    .then((wynik) => odpowiedz(wynik || { ok: false, blad: 'Brak odpowiedzi sejfu.' }))
    .catch((blad) => odpowiedz({ ok: false, blad: blad.message }));
  return true;
});
