'use strict';

// =====================================================================
//  Szybki Dostep - lokalny speed dial
// =====================================================================
//  Dane siedza w chrome.storage.local i zapisuja sie NATYCHMIAST po kazdej
//  zmianie - nie ma przycisku "zapisz" i nie da sie zgubic zmian.
//
//  Trzy drogi przeniesienia na inny komputer:
//    1. kopia.json obok rozszerzenia - wczytywana automatycznie przy
//       pierwszym uruchomieniu w nowym profilu,
//    2. reczny eksport/import pliku,
//    3. opcjonalna synchronizacja przez konto Google (domyslnie WYLACZONA).
//
//  Rozszerzenie nie ma uprawnien do zadnej strony. CSP dopuszcza polaczenia
//  wylacznie do wlasnych plikow ('self') - do internetu nie siega.
//  Ikony pochodza z lokalnego magazynu faviconow Chrome.
// =====================================================================

const KLUCZ = 'szybkiDostep';
const KLUCZ_USTAWIEN = 'ustawienia';

// =====================================================================
//  PROFILE - osobne zestawy zakladek dla roznych osob
// =====================================================================
//  Profil domyslny zostaje DOKLADNIE tam, gdzie byl: dane\kopia.json
//  i dane\ikony\. Dzieki temu nic nie trzeba przenosic ani przerabiac,
//  a starsza wersja programu nadal odczyta swoje dane.
//  Kazdy kolejny profil dostaje wlasny podfolder:
//
//     dane\
//        kopia.json          <- profil domyslny (bez zmian)
//        ikony\
//        miniatury\
//        profile.json        <- lista profili, wspolna dla komputerow
//        profil-Marek\
//           kopia.json
//           ikony\
//           miniatury\
//
//  To, KTORY profil jest aktywny, siedzi w pamieci przegladarki, a NIE
//  we wspolnym pliku. Inaczej przelaczenie profilu na jednym komputerze
//  przelaczaloby go drugiej osobie w srodku pracy.
const PROFIL_DOMYSLNY = 'Domyslny';
const KLUCZ_PROFIL = 'aktywnyProfil';
const PLIK_PROFILI = 'profile.json';

let profil = PROFIL_DOMYSLNY;
let profile = [PROFIL_DOMYSLNY];

// nazwa profilu trafia do nazwy folderu, wiec musi byc bezpieczna
function bezpiecznaNazwa(n) {
  const czysta = String(n || '').replace(/[^a-zA-Z0-9 _-]/g, '_').trim().slice(0, 40);
  return czysta || 'Profil';
}

// sciezka do danych aktywnego profilu, wzgledem folderu rozszerzenia
function folderProfilu() {
  return profil === PROFIL_DOMYSLNY ? PODF_DANE : PODF_DANE + '/profil-' + bezpiecznaNazwa(profil);
}

// klucz w pamieci przegladarki - domyslny profil zachowuje stary klucz,
// wiec dotychczasowe dane sa widoczne od razu, bez przenoszenia
function kluczDanych() {
  return profil === PROFIL_DOMYSLNY ? KLUCZ : KLUCZ + '::' + bezpiecznaNazwa(profil);
}
function kluczIkon() {
  return profil === PROFIL_DOMYSLNY ? KLUCZ_IKON : KLUCZ_IKON + '::' + bezpiecznaNazwa(profil);
}

let dane = { grupy: [{ nazwa: 'Start', skroty: [] }], aktywna: 0, zapisano: 0 };
// Synchronizacja z kontem Google jest WLACZONA domyslnie (decyzja z 6.08.2026).
// Dzieki temu po zalogowaniu sie do przegladarki na nowym komputerze skroty
// wracaja same, bez przenoszenia plikow. Kto tego nie chce, wylacza w menu -
// wybor zostaje zapamietany i ta wartosc domyslna go nie nadpisze.
//
// Co idzie na konto Google: skroty, grupy i wyglad. Ikony i miniatury NIE -
// nie miescilyby sie w limicie 100 KB i zostaja wylacznie na dysku.
let ustawienia = { sync: true, pytajOPlik: true, rozmiar: 148, tlo: 'auto' };
let ostrzezonoOKoncie = false;   // ostrzezenie o zapelnianiu konta - raz na sesje
let edytowany = null;   // indeks edytowanego skrotu albo null przy dodawaniu

const $ = (id) => document.getElementById(id);

// ---------------------------------------------------------------- dane
function poprawne(x) {
  return x && Array.isArray(x.grupy) && x.grupy.length > 0;
}

// Odczyt kopia.json lezacego w folderze rozszerzenia. To zasob wlasny,
// wiec nie potrzeba do niego zadnych uprawnien ani zgody uzytkownika.
// cache: 'no-store' - inaczej przegladarka moglaby podac stara zawartosc.
// =====================================================================
//  MOST - natywny posrednik do plikow (bez zadnych pytan o zgode)
// =====================================================================
//  Gdy na komputerze uruchomiono INSTALUJ.bat, rozszerzenie rozmawia
//  z malym programem, ktory czyta i zapisuje pliki w folderze programu.
//  Zero okien, zero uprawnien wygasajacych po zamknieciu przegladarki.
//  Gdy mostu nie ma - wszystko dziala po staremu, przez wybor folderu.
const MOST = 'pl.szybkidostep.most';
let mostDziala = null;          // null = jeszcze nie sprawdzone

let mostBlad = '';          // ostatni komunikat bledu - do diagnostyki

// JEDNO dlugo otwarte polaczenie zamiast osobnego procesu na kazde pytanie.
// Wczesniej kazde zapytanie uruchamialo cmd.exe + powershell.exe - przy
// wymianie co 5 sekund robilo sie z tego kilkadziesiat procesow na minute,
// co niepotrzebnie obciazalo komputer i ochrone antywirusowa.
let mostPolaczenie = null;
let mostLicznik = 0;
const mostOczekujace = new Map();

function mostGniazdo() {
  if (mostPolaczenie) return mostPolaczenie;
  try {
    mostPolaczenie = chrome.runtime.connectNative(MOST);
    mostPolaczenie.onMessage.addListener((m) => {
      if (!m || m.id === undefined) return;
      const oddaj = mostOczekujace.get(m.id);
      if (oddaj) { mostOczekujace.delete(m.id); oddaj(m); }
    });
    mostPolaczenie.onDisconnect.addListener(() => {
      mostBlad = (chrome.runtime.lastError && chrome.runtime.lastError.message) || 'most rozlaczony';
      mostPolaczenie = null;
      for (const [, oddaj] of mostOczekujace) oddaj(null);
      mostOczekujace.clear();
    });
  } catch (e) {
    mostBlad = 'wyjatek przy laczeniu: ' + (e.message || e);
    mostPolaczenie = null;
  }
  return mostPolaczenie;
}

function mostWyslij(zadanie) {
  return new Promise((gotowe) => {
    const g = mostGniazdo();
    if (!g) { gotowe(null); return; }

    const id = ++mostLicznik;
    zadanie.id = id;
    mostOczekujace.set(id, gotowe);

    // gdyby most zamilkl - nie wisimy w nieskonczonosc
    setTimeout(() => {
      if (mostOczekujace.has(id)) {
        mostOczekujace.delete(id);
        mostBlad = 'brak odpowiedzi mostu (limit czasu)';
        gotowe(null);
      }
    }, 8000);

    try { g.postMessage(zadanie); }
    catch (e) {
      mostOczekujace.delete(id);
      mostBlad = 'blad wysylki: ' + (e.message || e);
      mostPolaczenie = null;
      gotowe(null);
    }
  });
}

let mostBaza = '';     // folder, ktory faktycznie obsluguje most

async function sprawdzMost() {
  if (mostDziala !== null) return mostDziala;
  const o = await mostWyslij({ c: 'ping' });
  mostDziala = !!(o && o.ok);
  if (mostDziala && o.baza) mostBaza = o.baza;
  return mostDziala;
}

// Most jest kanonicznym miejscem wspolnych danych. Rozszerzenie moze byc
// zaladowane z innego folderu, innej kopii ZIP-a albo z innej przegladarki;
// Chrome nie udostepnia stronie rozszerzenia jej fizycznej sciezki, wiec
// porownywanie kopii bylo bledne i blokowalo prawidlowy zapis.
// Wazne jest tylko to, czy zarejestrowany host odpowiada. Jego folder jest
// tym samym folderem, ktory instalator wybral jako wspolny dla komputerow.
let zgodnoscFolderu = null;    // null = jeszcze nie sprawdzone

const WAZNOSC_ZGODNOSCI = 6 * 60 * 60 * 1000;   // 6 godzin

// Wynik pamietamy, bo kazda nowa karta to nowe uruchomienie tego skryptu,
// a sprawdzenie zapisuje plik. Bez tego przy folderze w synchronizacji
// (AllwaySync, OneDrive) lecialby zapis co karte i program bez przerwy
// mielilby dysk. Ponawiamy, gdy most zmienil folder albo minelo 6 godzin.
async function zapamietanaZgodnosc(baza) {
  try {
    const w = await chrome.storage.local.get('zgodnoscFolderu');
    const z = w && w.zgodnoscFolderu;
    if (!z || z.baza !== baza) return null;
    if (Date.now() - (z.kiedy || 0) > WAZNOSC_ZGODNOSCI) return null;
    return !!z.ok;
  } catch (e) { return null; }
}

async function zapiszZgodnosc(baza, ok) {
  try {
    await chrome.storage.local.set({ zgodnoscFolderu: { baza, ok, kiedy: Date.now() } });
  } catch (e) { /* brak pamieci lokalnej nie moze blokowac programu */ }
}

async function sprawdzZgodnoscFolderu() {
  if (zgodnoscFolderu !== null) return zgodnoscFolderu;
  if (!(await sprawdzMost())) { zgodnoscFolderu = false; return false; }
  zgodnoscFolderu = true;
  return zgodnoscFolderu;
}

// --- operacje na plikach przez most (sciezki wzgledne wobec folderu) ---
// =====================================================================
//  LUSTRO Z DRUGIEGO KOMPUTERA (przez Sejf)
// =====================================================================
//  Sejf przywozi kopia.json drugiego komputera i kladzie ja obok naszej jako
//  z-sieci.json (ikony i miniatury dokladajac do folderow). Scalamy ja ta
//  sama funkcja co wspolny plik - z nagrobkami, wiec usuniecie tam usuwa tu.
let zegarZSieci = null;
let ostatnioZSieci = '';

async function scalZSieci() {
  if (!(await sprawdzMost())) return false;
  const t = await mostCzytajTekst(folderProfilu() + '/z-sieci.json');
  if (!t || t === ostatnioZSieci) return false;
  let zdalne;
  try { zdalne = JSON.parse(t); } catch (e) { return false; }
  ostatnioZSieci = t;
  if (!poprawne(zdalne) || !poprawne(dane)) return false;
  const scalone = scalDane(dane, zdalne);
  if (!rozniSie(scalone, dane)) return false;
  const bylo = dane.grupy.reduce((s, g) => s + g.skroty.length, 0);
  dane = scalone;
  if (dane.aktywna >= dane.grupy.length) dane.aktywna = 0;
  await chrome.storage.local.set({ [kluczDanych()]: dane });
  rysuj();
  const jest = dane.grupy.reduce((s, g) => s + g.skroty.length, 0);
  if (jest !== bylo) pokazPasek('Z drugiego komputera: bylo ' + bylo + ' skrotow, jest ' + jest + '.');
  return true;
}

async function mostCzytajTekst(sciezka) {
  const o = await mostWyslij({ c: 'read', p: sciezka });
  if (!o || !o.ok || !o.b64) return null;
  try {
    const bajty = Uint8Array.from(atob(o.b64), (z) => z.charCodeAt(0));
    return new TextDecoder('utf-8').decode(bajty);
  } catch (e) { return null; }
}

async function mostZapiszTekst(sciezka, tekst) {
  const bajty = new TextEncoder().encode(tekst);
  let s = '';
  for (let i = 0; i < bajty.length; i++) s += String.fromCharCode(bajty[i]);
  const o = await mostWyslij({ c: 'write', p: sciezka, b64: btoa(s) });
  return !!(o && o.ok);
}

async function mostZapiszDataUrl(sciezka, dataUrl) {
  const b64 = dataUrl.split(',')[1];
  const o = await mostWyslij({ c: 'write', p: sciezka, b64 });
  return !!(o && o.ok);
}

async function mostLista(sciezka) {
  const o = await mostWyslij({ c: 'list', p: sciezka });
  return (o && o.ok && o.pliki) ? o.pliki : [];
}

async function mostUsun(sciezka) {
  const o = await mostWyslij({ c: 'del', p: sciezka });
  return !!(o && o.ok);
}

// ODCZYT DANYCH Z DYSKU - zawsze swiezy.
// Zasob rozszerzenia (chrome.runtime.getURL) bywa buforowany przez
// przegladarke, wiec zmiany zrobione w innej przegladarce moglyby byc
// niewidoczne az do przeladowania. Gdy mamy uchwyt do folderu, czytamy
// plik bezposrednio - to jedyna droga do prawdziwej synchronizacji na zywo.
async function wczytajZDysku() {
  // 0. MOST - najprostsza droga, ale tylko gdy obsluguje ten sam folder
  if (await sprawdzMost() && await sprawdzZgodnoscFolderu()) {
    const t = await mostCzytajTekst(folderProfilu() + '/kopia.json');
    if (t) {
      try { const o = JSON.parse(t); if (poprawne(o)) return o; } catch (e) { /* uszkodzony */ }
    }
  }
  try {
    const kat = await katalogDanych(null);
    if (kat) {
      const p = await kat.getFileHandle('kopia.json');
      const f = await p.getFile();
      if (f.size > 0) {
        const o = JSON.parse(await f.text());
        if (poprawne(o)) return o;
      }
    }
  } catch (e) { /* brak pliku, zgody albo folder odlaczony */ }
  return await wczytajZFolderu();     // zapas: zasob rozszerzenia
}

async function wczytajZFolderu() {
  // najpierw dane aktywnego profilu, potem - tylko dla profilu domyslnego -
  // stare miejsce w korzeniu folderu, dla zgodnosci z pierwszymi wersjami
  const sciezki = [folderProfilu() + '/kopia.json'];
  if (profil === PROFIL_DOMYSLNY) sciezki.push('kopia.json');
  for (const sciezka of sciezki) {
    try {
      const odp = await fetch(chrome.runtime.getURL(sciezka), { cache: 'no-store' });
      if (!odp.ok) continue;
      const o = await odp.json();
      if (poprawne(o)) return o;
    } catch (e) { /* szukamy dalej */ }
  }
  return null;
}

// ---------------------------------------------------------- profile
//  Lista profili lezy w dane\profile.json - wspolna dla wszystkich
//  komputerow, zeby po dodaniu profilu na jednym pojawil sie na drugim.
function poprawnaListaProfili(o) {
  return !!(o && Array.isArray(o.profile) && o.profile.length);
}

async function wczytajPlikProfili() {
  if (await sprawdzMost() && await sprawdzZgodnoscFolderu()) {
    const t = await mostCzytajTekst(PODF_DANE + '/' + PLIK_PROFILI);
    if (t) {
      try { const o = JSON.parse(t); if (poprawnaListaProfili(o)) return o; }
      catch (e) { /* uszkodzony - probujemy dalej */ }
    }
  }
  try {
    const kat = await katalogDaneKorzen();
    if (kat) {
      const p = await kat.getFileHandle(PLIK_PROFILI);
      const f = await p.getFile();
      if (f.size > 0) {
        const o = JSON.parse(await f.text());
        if (poprawnaListaProfili(o)) return o;
      }
    }
  } catch (e) { /* brak pliku albo zgody */ }
  try {
    const odp = await fetch(chrome.runtime.getURL(PODF_DANE + '/' + PLIK_PROFILI), { cache: 'no-store' });
    if (odp.ok) {
      const o = await odp.json();
      if (poprawnaListaProfili(o)) return o;
    }
  } catch (e) { /* brak pliku - normalne, zanim powstanie drugi profil */ }
  return null;
}

async function zapiszPlikProfili() {
  const tresc = JSON.stringify({ profile, piny, zapisano: Date.now() }, null, 2);
  if (await sprawdzMost() && await sprawdzZgodnoscFolderu()) {
    if (await mostZapiszTekst(PODF_DANE + '/' + PLIK_PROFILI, tresc)) return true;
  }
  try {
    const kat = await katalogDaneKorzen();
    if (!kat) return false;
    const plik = await kat.getFileHandle(PLIK_PROFILI, { create: true });
    const s = await plik.createWritable();
    await s.write(tresc);
    await s.close();
    return true;
  } catch (e) { return false; }
}

// Scalanie listy: profil dodany na drugim komputerze ma sie pojawic tutaj,
// a nasz swiezo dodany nie moze zniknac. Suma obu stron, bez kasowania.
async function odswiezListeProfili() {
  const o = await wczytajPlikProfili();
  const suma = [PROFIL_DOMYSLNY];
  for (const p of ((o && o.profile) || []).concat(profile)) {
    if (p && !suma.includes(p)) suma.push(p);
  }
  profile = suma;
  if (o && o.piny) piny = Object.assign({}, o.piny, piny);

  // Podajemy liste procesowi tla. On tez probuje odczytac profile.json sam,
  // ale jego droga bywa zawodna, a strona ma do dyspozycji jeszcze most
  // i uchwyt do folderu. Kto ma pewniejsze zrodlo, ten dostarcza dane.
  try { await chrome.storage.local.set({ listaProfiliCache: { profile, piny, kiedy: Date.now() } }); }
  catch (e) { /* brak miejsca - proces tla poradzi sobie sam */ }
}

// =====================================================================
//  PIN PRZY WEJSCIU W PROFIL
// =====================================================================
//  Zamek na uczciwosc: powstrzyma domownika przed zajrzeniem w cudze
//  zakladki. NIE jest szyfrowaniem - kto ma dostep do folderu, otworzy
//  kopia.json w notatniku. Dlatego samego PIN-u nie zapisujemy nigdzie:
//  w pliku leza tylko losowa sol i skrot SHA-256, z ktorych kodu nie da
//  sie odtworzyc.
let piny = {};        // { 'Marek': { sol: '...', skrot: '...' } }

function losowaSol() {
  const b = new Uint8Array(16);
  crypto.getRandomValues(b);
  return Array.from(b).map((x) => x.toString(16).padStart(2, '0')).join('');
}

async function skrotPin(pin, sol) {
  const bajty = new TextEncoder().encode(sol + '|' + String(pin));
  const wynik = await crypto.subtle.digest('SHA-256', bajty);
  return Array.from(new Uint8Array(wynik)).map((x) => x.toString(16).padStart(2, '0')).join('');
}

function maPin(nazwa) {
  const p = piny[nazwa];
  return !!(p && p.sol && p.skrot);
}

async function pinPasuje(nazwa, pin) {
  const p = piny[nazwa];
  if (!p) return true;
  return (await skrotPin(pin, p.sol)) === p.skrot;
}

// Okno PIN-u. Zwraca wpisany kod albo null przy anulowaniu.
function zapytajOPin(tytul, podpowiedz) {
  return new Promise((gotowe) => {
    const okno = $('oknoPin');
    const pole = $('polePin');
    $('pinTytul').textContent = tytul;
    $('pinPodpowiedz').textContent = podpowiedz || '';
    pole.value = '';
    let oddane = false;
    const zamknij = (wynik) => {
      if (oddane) return;
      oddane = true;
      okno.onclose = null;
      $('pinOk').onclick = null;
      $('pinAnuluj').onclick = null;
      pole.onkeydown = null;
      try { okno.close(); } catch (e) { /* juz zamkniete */ }
      gotowe(wynik);
    };
    $('pinOk').onclick = () => zamknij(pole.value);
    $('pinAnuluj').onclick = () => zamknij(null);
    pole.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); zamknij(pole.value); } };
    okno.onclose = () => zamknij(null);
    okno.showModal();
    setTimeout(() => pole.focus(), 30);
  });
}

async function ustawPinProfilu() {
  const pin = await zapytajOPin('PIN dla profilu "' + profil + '"',
    'Zostaw puste, zeby zdjac PIN. Kod bedzie potrzebny przy kazdym wejsciu w ten profil.');
  if (pin === null) return;
  if (!String(pin).trim()) {
    delete piny[profil];
    await zapiszPlikProfili();
    odswiezMenu();
    pokazPasek('PIN zdjety z profilu "' + profil + '".');
    return;
  }
  const sol = losowaSol();
  piny[profil] = { sol, skrot: await skrotPin(String(pin).trim(), sol) };
  await zapiszPlikProfili();
  odswiezMenu();
  pokazPasek('Profil "' + profil + '" jest teraz na PIN. Zapamietaj kod - nie da sie go odzyskac.');
}

// =====================================================================
//  PRZELACZANIE I ZARZADZANIE PROFILAMI
// =====================================================================
async function przelaczProfil(nazwa) {
  if (!nazwa || nazwa === profil) return false;
  if (!profile.includes(nazwa)) return false;

  if (maPin(nazwa)) {
    const pin = await zapytajOPin('Profil "' + nazwa + '" jest na PIN', 'Podaj kod, zeby wejsc.');
    if (pin === null) { odswiezWyborProfilu(); return false; }
    if (!(await pinPasuje(nazwa, String(pin).trim()))) {
      odswiezWyborProfilu();
      pokazPasek('Zly PIN - profil nie zostal otwarty.');
      return false;
    }
  }

  // domykamy biezacy profil: zapis juz sie odbyl po kazdej zmianie,
  // wiec wystarczy przelaczyc sie i wczytac dane nowego
  profil = nazwa;
  await chrome.storage.local.set({ [KLUCZ_PROFIL]: profil });
  // menu kontekstowe ma od razu pokazywac grupy TEGO profilu
  try { chrome.runtime.sendMessage({ typ: 'profilZmieniony' }).catch(() => {}); }
  catch (e) { /* proces tla spi - obudzi go zdarzenie magazynu */ }
  bazaZapisu = 0;
  ikonyLokalne = {};
  miniaturyLokalne = {};
  dane = { grupy: [{ nazwa: 'Start', skroty: [] }], aktywna: 0, zapisano: 0 };

  const zapis = await chrome.storage.local.get([kluczDanych(), kluczIkon()]);
  if (zapis[kluczIkon()]) ikonyLokalne = zapis[kluczIkon()];
  if (poprawne(zapis[kluczDanych()])) dane = zapis[kluczDanych()];

  // dociagamy z dysku - na drugim komputerze profil moze byc bogatszy
  const zPliku = await wczytajZDysku();
  if (zPliku) {
    const scalone = scalDane(dane, zPliku);
    dane = scalone;
    await chrome.storage.local.set({ [kluczDanych()]: dane });
    bazaZapisu = dane.zapisano || 0;
  }
  if (dane.aktywna >= dane.grupy.length) dane.aktywna = 0;
  if (!dane.wyglad) dane.wyglad = { tlo: 'auto', rozmiar: 148 };
  wygladWuzyciu = { tlo: dane.wyglad.tlo, rozmiar: dane.wyglad.rozmiar };

  odswiezWyborProfilu();
  odswiezMenu();
  zastosujTlo();
  zastosujRozmiar();
  rysuj();
  pokazPasek('Profil: ' + profil + (maPin(profil) ? ' (na PIN)' : ''));
  return true;
}

async function nowyProfil() {
  const nazwa = prompt('Nazwa nowego profilu:', '');
  if (nazwa === null) { odswiezWyborProfilu(); return; }
  const czysta = bezpiecznaNazwa(nazwa);
  if (!czysta || czysta === 'Profil' && !String(nazwa).trim()) { odswiezWyborProfilu(); return; }
  if (profile.includes(czysta)) {
    pokazPasek('Profil "' + czysta + '" juz istnieje.');
    odswiezWyborProfilu();
    return;
  }
  profile.push(czysta);
  await zapiszPlikProfili();
  odswiezWyborProfilu();
  await przelaczProfil(czysta);
  pokazPasek('Nowy profil "' + czysta + '" - zaczyna od pustej tablicy. Dane leza w dane\\profil-' + czysta + '\\.');
}

async function zmienNazweProfilu() {
  if (profil === PROFIL_DOMYSLNY) {
    pokazPasek('Profilu domyslnego nie da sie przemianowac - jego dane leza wprost w dane\\.');
    return;
  }
  const nazwa = prompt('Nowa nazwa profilu "' + profil + '":', profil);
  if (nazwa === null) return;
  const czysta = bezpiecznaNazwa(nazwa);
  if (!czysta || czysta === profil) return;
  if (profile.includes(czysta)) { pokazPasek('Taki profil juz istnieje.'); return; }
  pokazPasek('Zmiana nazwy wymaga przeniesienia folderu dane\\profil-' + profil +
             ' na dane\\profil-' + czysta + '. Zrob to w Eksploratorze, potem wroc tutaj.');
}

async function usunProfil() {
  if (profil === PROFIL_DOMYSLNY) {
    pokazPasek('Profilu domyslnego nie da sie usunac.');
    return;
  }
  const nazwa = profil;
  if (!confirm('Usunac profil "' + nazwa + '" z listy?\n\n' +
               'Jego zakladki i ikony ZOSTAJA na dysku w folderze dane\\profil-' + nazwa + '.\n' +
               'Nic nie jest kasowane - profil po prostu znika z przelacznika.')) return;
  profile = profile.filter((p) => p !== nazwa);
  delete piny[nazwa];
  await zapiszPlikProfili();
  await chrome.storage.local.remove([kluczDanych(), kluczIkon()]);
  await przelaczProfil(PROFIL_DOMYSLNY);
  pokazPasek('Profil "' + nazwa + '" zdjety z listy. Pliki zostaly w dane\\profil-' + nazwa + '\\.');
}

function odswiezWyborProfilu() {
  const wybor = $('profil');
  if (!wybor) return;
  wybor.innerHTML = '';
  for (const p of profile) {
    const o = document.createElement('option');
    o.value = p;
    o.textContent = (maPin(p) && p !== profil ? '* ' : '') + p;
    if (p === profil) o.selected = true;
    wybor.appendChild(o);
  }
  const nowy = document.createElement('option');
  nowy.value = '::nowy::';
  nowy.textContent = '+ Nowy profil...';
  wybor.appendChild(nowy);
  wybor.title = 'Profil: ' + profil + (maPin(profil) ? ' (na PIN)' : '');
}

async function wczytaj() {
  // Uchwyty do folderu MUSZA byc pierwsze - lista profili moze lezec na
  // dysku, a bez uchwytu nie da sie jej stamtad odczytac.
  uchwytFolderu = await pobierzFolder();
  uchwytPliku = await pobierzUchwyt();

  // Profil jest drugi w kolejnosci, bo to on decyduje, z ktorego klucza
  // w pamieci przegladarki i z ktorego podfolderu czytamy dane.
  const wybor = await chrome.storage.local.get([KLUCZ_PROFIL]);
  if (wybor[KLUCZ_PROFIL]) profil = wybor[KLUCZ_PROFIL];
  await odswiezListeProfili();
  if (!profile.includes(profil)) profil = PROFIL_DOMYSLNY;   // profil skasowany gdzie indziej

  const zapis = await chrome.storage.local.get([kluczDanych(), KLUCZ_USTAWIEN, kluczIkon()]);
  if (zapis[KLUCZ_USTAWIEN]) ustawienia = Object.assign(ustawienia, zapis[KLUCZ_USTAWIEN]);
  if (zapis[kluczIkon()]) ikonyLokalne = zapis[kluczIkon()];
  if (poprawne(zapis[kluczDanych()])) dane = zapis[kluczDanych()];
  else dane = { grupy: [{ nazwa: 'Start', skroty: [] }], aktywna: 0, zapisano: 0 };

  // 1. Synchronizacja: jesli wlaczona i wersja z konta jest nowsza - bierzemy ja.
  // 1a. Wspolny plik na dysku - ma pierwszenstwo, bo laczy rozne przegladarki.
  if (uchwytZgody()) {
    try {
      if (await maPrawo(uchwytZgody(), false)) {
        await synchronizujZPlikiem(true);
      } else if (ustawienia.pytajOPlik === false) {
        // uzytkownik wybral tylko reczna synchronizacje - nie zawracamy glowy
      } else if (await zgodaWSesji()) {
        // w tej sesji juz raz zezwoliles - bierzemy zgode po cichu,
        // przy pierwszym klikniecu, bez pokazywania paska
        zgodaPrzyPierwszymKliknieciu();
      } else {
        poprosOZgode();
      }
    } catch (e) { /* plik niedostepny - dzialamy lokalnie */ }
  }

  if (syncWlaczony(await kontoPrzegladarki())) {
    try {
      const zdalne = await wczytajZSync();
      if (zdalne && (zdalne.zapisano || 0) > (dane.zapisano || 0)) {
        dane = zdalne;
        bazaZapisu = dane.zapisano || 0;
        await chrome.storage.local.set({ [kluczDanych()]: dane });
        pokazPasek('Wczytano nowsza wersje z konta Google.');
      }
    } catch (e) { /* brak logowania w Chrome - dzialamy lokalnie */ }
  }

  // 2. kopia.json w folderze rozszerzenia. ODCZYT NIE WYMAGA ZADNEJ ZGODY,
  //    bo to zwykly zasob rozszerzenia - dziala od razu, takze zaraz po
  //    starcie przegladarki. Zgoda jest potrzebna tylko do ZAPISU.
  const zFolderu = await wczytajZFolderu();
  if (zFolderu) {
    // SCALANIE zamiast podmiany - zmiany zrobione tu bez dostepu do folderu
    // musza przetrwac spotkanie z wersja z innego laptopa.
    const scalone = scalDane(dane, zFolderu);
    if (rozniSie(scalone, dane)) {
      const bylo = dane.grupy.reduce((t, g) => t + g.skroty.length, 0);
      dane = scalone;
      const jest = dane.grupy.reduce((t, g) => t + g.skroty.length, 0);
      await chrome.storage.local.set({ [kluczDanych()]: dane });
      if (jest !== bylo) {
        pokazPasek('Scalono z wspolnym plikiem: bylo ' + bylo + ' skrotow, jest ' + jest + '.');
      }
    }
    bazaZapisu = dane.zapisano || 0;
  }

  // 3. Lustro z drugiego komputera (przywozi je Sejf jako z-sieci.json). Scalamy od razu
  //    i potem co 5 s, zeby kafelek dodany albo usuniety tam pojawil sie/zniknal tutaj.
  await scalZSieci().catch(() => false);
  if (!zegarZSieci) zegarZSieci = setInterval(() => { scalZSieci().catch(() => false); }, 5000);

  // =====================================================================
  //  PUSTA TABLICA A KOPIA NA KONCIE GOOGLE
  // =====================================================================
  //  Swiezo zainstalowany program na nowym komputerze nie ma zadnych
  //  skrotow. Jesli jednak kiedys wlaczyles synchronizacje, kopia lezy
  //  na Twoim koncie Google - szkoda, zeby przepadla tylko dlatego,
  //  ze nikt o nia nie zapytal.
  //
  //  Sprawdzamy to TYLKO gdy tablica jest pusta. Nie wlaczamy przy okazji
  //  synchronizacji - to osobna decyzja. Tu chodzi wylacznie o jednorazowe
  //  odzyskanie tego, co juz tam lezy.
  const pustaTablica = !dane.grupy.some((g) => g.skroty && g.skroty.length);
  if (pustaTablica && !ustawienia.sync) {
    try {
      const zKonta = await wczytajZSync();
      const ileZKonta = zKonta ? zKonta.grupy.reduce((t, g) => t + ((g.skroty && g.skroty.length) || 0), 0) : 0;
      if (ileZKonta > 0) {
        pokazPasek('Tablica jest pusta, ale na Twoim koncie Google lezy kopia: ' +
                   ileZKonta + ' skrotow w ' + zKonta.grupy.length + ' grupach. Przywrocic?', [
          {
            napis: 'Przywroc',
            akcja: async () => {
              ukryjPasek();
              dane = scalDane(dane, zKonta);
              if (dane.aktywna >= dane.grupy.length) dane.aktywna = 0;
              await chrome.storage.local.set({ [kluczDanych()]: dane });
              await zapisz();
              rysuj();
              const ile = dane.grupy.reduce((t, g) => t + g.skroty.length, 0);
              pokazPasek('Przywrocono ' + ile + ' skrotow z konta Google. Ikony pobiora sie same - ' +
                         'obrazki nie miescilyby sie w limicie konta i nie sa tam trzymane.');
            }
          },
          { napis: 'Nie teraz', drugi: true, akcja: () => ukryjPasek() }
        ]);
      }
    } catch (e) { /* brak logowania w Chrome albo pusty magazyn - trudno */ }
  }

  if (dane.aktywna >= dane.grupy.length) dane.aktywna = 0;

  // Przeniesienie wygladu ze starych ustawien lokalnych do danych.
  // Od 3.3 motyw i rozmiar sa czescia danych, wiec sie synchronizuja.
  if (!dane.wyglad) {
    dane.wyglad = { tlo: ustawienia.tlo || 'auto', rozmiar: ustawienia.rozmiar || 148 };
  }
  wygladWuzyciu = { tlo: dane.wyglad.tlo, rozmiar: dane.wyglad.rozmiar };
}

// ---------------------------------------------------------- synchronizacja
//  chrome.storage.sync ma twardy limit 8 KB na POJEDYNCZY element, wiec
//  calosci nie da sie zapisac pod jednym kluczem. Tniemy tekst na kawalki
//  po 4000 znakow (najgorszy przypadek w UTF-8 to 8000 bajtow) i zapisujemy
//  jako sd_0, sd_1, ... plus licznik. Laczny limit konta to 100 KB / 512 pozycji.
const KAWALEK = 4000;

// =====================================================================
//  JAK ZMIESCIC SIE W 100 KB KONTA GOOGLE
// =====================================================================
//  Google daje 100 KB na cale rozszerzenie i 8 KB na pojedynczy wpis.
//  Robimy dwie rzeczy, ktore razem odsuwaja ten limit kilkunastokrotnie:
//
//  1. NIE WYSYLAMY SMIECI. Kosz i nagrobki po skasowanych skrotach to
//     sprawy lokalne - potrzebne, zeby usuniety skrot nie wrocil z drugiego
//     komputera, ale nie ma powodu trzymac ich na koncie. Same potrafia
//     zajmowac wiecej miejsca niz skroty.
//
//  2. PAKUJEMY gzipem. Przegladarka ma to wbudowane (CompressionStream),
//     a JSON kurczy sie 5-8 razy. 100 KB miejsca zamienia sie w praktyce
//     w okolo 600 KB danych, czyli grubo ponad tysiac skrotow.
//
//  Zapis starym sposobem (bez pakowania) nadal daje sie odczytac -
//  rozpoznajemy go po braku znacznika sd_zip.
async function spakuj(tekst) {
  const strumien = new Blob([tekst]).stream().pipeThrough(new CompressionStream('gzip'));
  const bajty = new Uint8Array(await new Response(strumien).arrayBuffer());
  let s = '';
  for (let i = 0; i < bajty.length; i += 8192) {
    s += String.fromCharCode.apply(null, bajty.subarray(i, i + 8192));
  }
  return btoa(s);
}

async function rozpakuj(b64) {
  const surowe = atob(b64);
  const bajty = new Uint8Array(surowe.length);
  for (let i = 0; i < surowe.length; i++) bajty[i] = surowe.charCodeAt(i);
  const strumien = new Blob([bajty]).stream().pipeThrough(new DecompressionStream('gzip'));
  return await new Response(strumien).text();
}

// Wersja danych do wyslania: bez kosza i ze skroconymi nagrobkami.
function odchudzDoSync(obiekt) {
  const granica = Date.now() - 30 * 24 * 3600 * 1000;   // nagrobki tylko z 30 dni
  return {
    grupy: obiekt.grupy,
    aktywna: obiekt.aktywna || 0,
    wyglad: obiekt.wyglad,
    usuniete: (obiekt.usuniete || []).filter((n) => (n.kiedy || 0) > granica),
    zapisano: obiekt.zapisano || Date.now()
  };
}

async function zapiszDoSync(obiekt) {
  const surowy = JSON.stringify(odchudzDoSync(obiekt));
  let tresc, spakowane;
  try {
    tresc = await spakuj(surowy);
    spakowane = true;
  } catch (e) {
    tresc = surowy;          // starsza przegladarka bez CompressionStream
    spakowane = false;
  }

  const czesci = [];
  for (let i = 0; i < tresc.length; i += KAWALEK) czesci.push(tresc.slice(i, i + KAWALEK));

  const paczka = {
    sd_ile: czesci.length,
    sd_czas: obiekt.zapisano || Date.now(),
    sd_zip: spakowane
  };
  czesci.forEach((c, i) => { paczka['sd_' + i] = c; });

  // sprzataj kawalki po wiekszej, poprzedniej wersji
  const stare = await chrome.storage.sync.get(null);
  const zbedne = Object.keys(stare).filter(
    (k) => (/^sd_\d+$/.test(k) && parseInt(k.slice(3), 10) >= czesci.length) || k === KLUCZ);
  if (zbedne.length) await chrome.storage.sync.remove(zbedne);

  await chrome.storage.sync.set(paczka);
  return tresc.length;
}

async function wczytajZSync() {
  const w = await chrome.storage.sync.get(null);
  if (!w.sd_ile) {
    // stary zapis sprzed podzialu na kawalki
    return poprawne(w[KLUCZ]) ? w[KLUCZ] : null;
  }
  let tresc = '';
  for (let i = 0; i < w.sd_ile; i++) {
    if (typeof w['sd_' + i] !== 'string') return null;   // niekompletna paczka
    tresc += w['sd_' + i];
  }
  try {
    // sd_zip mowi, czy paczka jest spakowana. Starsze zapisy go nie maja
    // i czytamy je wprost - dzieki temu nic nie ginie po aktualizacji.
    const tekst = w.sd_zip ? await rozpakuj(tresc) : tresc;
    const o = JSON.parse(tekst);
    return poprawne(o) ? o : null;
  } catch (e) { return null; }
}

// =====================================================================
//  KTORE KONTO, I CZY W OGOLE JAKIES
// =====================================================================
//  chrome.storage.sync istnieje ZAWSZE - takze w Brave i w Chrome bez
//  zalogowania. Wtedy zachowuje sie jak zwykla pamiec lokalna i nic nigdzie
//  nie wysyla. Program pokazywal przez to "synchronizacja WLACZONA" tam,
//  gdzie zadnej synchronizacji nie bylo. Teraz pytamy przegladarke wprost,
//  na jakim koncie pracuje.
//
//  Uprawnienie 'identity' jest OPCJONALNE - prosimy o nie dopiero przy
//  wlaczaniu synchronizacji. Bez niego program dziala, tylko uczciwie
//  przyznaje, ze konta nie zna.
let kontoPamiec = undefined;   // undefined = jeszcze nie pytalismy

async function maUprawnienieDoKonta() {
  try { return await chrome.permissions.contains({ permissions: ['identity', 'identity.email'] }); }
  catch (e) { return false; }
}

async function poprosOKonto() {
  try { return await chrome.permissions.request({ permissions: ['identity', 'identity.email'] }); }
  catch (e) { return false; }
}

// Zwraca: { znane:false }                    - nie wiemy (brak uprawnienia lub brak API)
//         { znane:true, zalogowany:false }   - przegladarka bez konta Google (np. Brave)
//         { znane:true, zalogowany:true, id, email }
async function kontoPrzegladarki(odswiez) {
  if (kontoPamiec !== undefined && !odswiez) return kontoPamiec;

  if (!chrome.identity || !chrome.identity.getProfileUserInfo || !(await maUprawnienieDoKonta())) {
    kontoPamiec = { znane: false };
    return kontoPamiec;
  }
  try {
    const i = await new Promise((ok) => {
      chrome.identity.getProfileUserInfo({ accountStatus: 'ANY' }, (w) => ok(w || {}));
    });
    // Brave i Chrome bez logowania oddaja puste pola zamiast bledu
    kontoPamiec = (i && i.id)
      ? { znane: true, zalogowany: true, id: i.id, email: i.email || '' }
      : { znane: true, zalogowany: false };
  } catch (e) {
    kontoPamiec = { znane: false };
  }
  return kontoPamiec;
}

// Ustawienie synchronizacji trzymamy OSOBNO DLA KAZDEGO KONTA. Konto A moze
// chciec synchronizacji, konto B nie - i jedno nie moze decydowac za drugie.
// ustawienia.sync zostaje jako wartosc dla przypadku, gdy konta nie znamy.
function syncWlaczony(konto) {
  if (konto && konto.znane && konto.zalogowany) {
    const wg = ustawienia.syncKonta || {};
    if (Object.prototype.hasOwnProperty.call(wg, konto.id)) return !!wg[konto.id];
  }
  return !!ustawienia.sync;
}

function ustawSyncDlaKonta(konto, wlaczony) {
  ustawienia.sync = wlaczony;
  if (konto && konto.znane && konto.zalogowany) {
    if (!ustawienia.syncKonta) ustawienia.syncKonta = {};
    ustawienia.syncKonta[konto.id] = wlaczony;
  }
}

// Jednym zdaniem: co naprawde dzieje sie z danymi.
async function opisSynchronizacji() {
  const k = await kontoPrzegladarki();
  if (k.znane && !k.zalogowany) {
    return { stan: 'brak-konta', tekst: 'Synchronizacja: brak konta w przegladarce - dane zostaja lokalnie' };
  }
  const wl = syncWlaczony(k);
  if (!wl) return { stan: 'wylaczona', tekst: 'Synchronizacja z kontem Google: wylaczona' };
  if (k.znane && k.zalogowany) {
    return { stan: 'wlaczona', konto: k, tekst: 'Synchronizacja: WLACZONA dla ' + (k.email || 'konta w tej przegladarce') };
  }
  return { stan: 'wlaczona-nieznane-konto', tekst: 'Synchronizacja: WLACZONA (nie wiem, na jakim koncie)' };
}

// Ile miejsca zajmujemy na koncie Google i ile zostalo.
async function stanKontaGoogle() {
  try {
    const uzyte = await chrome.storage.sync.getBytesInUse(null);
    const limit = chrome.storage.sync.QUOTA_BYTES || 102400;
    const skroty = dane.grupy.reduce((t, g) => t + ((g.skroty && g.skroty.length) || 0), 0);
    return {
      uzyte, limit,
      procent: Math.round(uzyte * 100 / limit),
      skroty,
      // ile skrotow zmiescimy przy obecnym stopniu upakowania
      zapas: (uzyte > 0 && skroty > 0) ? Math.floor((limit - uzyte) / (uzyte / skroty)) : null
    };
  } catch (e) { return null; }
}

async function zapisz() {
  dane.zapisano = Date.now();
  await chrome.storage.local.set({ [kluczDanych()]: dane });

  // wspolny plik na dysku - drugi profil zobaczy zmiane przy nastepnej karcie
  try { await zapiszDoPliku(); } catch (e) { /* plik chwilowo niedostepny */ }

  // Przegladarka bez konta ma chrome.storage.sync, ale on tylko udaje
  // synchronizacje. Nie ma po co tam pisac ani ostrzegac o limicie konta,
  // ktorego nie ma - zajmowaloby to miejsce i mylilo uzytkownika.
  const kontoTeraz = await kontoPrzegladarki();
  if (kontoTeraz.znane && !kontoTeraz.zalogowany) return;

  if (syncWlaczony(kontoTeraz)) {
    try {
      await zapiszDoSync(dane);

      // Ostrzegamy ZAWCZASU, a nie dopiero gdy zapis padnie. Raz na sesje,
      // zeby nie meczyc - od 80 procent zajetosci konta.
      if (!ostrzezonoOKoncie) {
        const s = await stanKontaGoogle();
        if (s && s.procent >= 80) {
          ostrzezonoOKoncie = true;
          pokazPasek('Konto Google zapelnia sie: zajete ' + s.procent + '% ze 100 KB (' +
                     s.skroty + ' skrotow). Zmiesci sie jeszcze okolo ' + s.zapas + '. ' +
                     'Gdy zabraknie miejsca, skroty i tak zostana na dysku - przestana tylko ' +
                     'wedrowac miedzy komputerami.', {
            napis: 'Rozumiem',
            akcja: () => ukryjPasek()
          });
        }
      }
    } catch (e) {
      const tekst = String(e && e.message ? e.message : e);
      if (/QUOTA_BYTES|kQuotaBytes/i.test(tekst)) {
        pokazPasek('Konto Google jest pelne (limit 100 KB, ustala go Google i nie da sie go podniesc). ' +
                   'Twoje skroty SA BEZPIECZNE - zapis na dysku sie udal, nie wedruja tylko na inne komputery. ' +
                   'Pomoze oproznienie kosza albo usuniecie nieuzywanych skrotow.');
      } else {
        pokazPasek('Nie udalo sie zapisac do konta Google: ' + tekst + ' (zapis lokalny sie udal)');
      }
    }
  }
}

async function zapiszUstawienia() {
  await chrome.storage.local.set({ [KLUCZ_USTAWIEN]: ustawienia });
}

// =====================================================================
//  WSPOLNY PLIK NA DYSKU
// =====================================================================
//  Rozwiazuje przypadek dwoch profili Chrome (lub dwoch kont) na tym samym
//  komputerze: kazdy ma wlasny chrome.storage, wiec same z siebie sie nie
//  widza. Oba wskazuja ten sam plik kopia.json i wymieniaja sie przez niego.
//  Uchwyt do pliku trzymamy w IndexedDB - to jedyny sposob, zeby przetrwal
//  restart przegladarki. Zadnej chmury, zadnego posrednika.
let uchwytPliku = null;

// Do pytania o zgode wystarczy JEDEN uchwyt - folder ma pierwszenstwo,
// bo obejmuje i kopie, i ikony. Nie mieszamy tych dwoch w jednej zmiennej:
// katalog nie ma metody createWritable i zapis by po cichu padal.
function uchwytZgody() { return uchwytFolderu || uchwytPliku; }

function idb() {
  return new Promise((ok, blad) => {
    const z = indexedDB.open('szybkiDostep', 1);
    z.onupgradeneeded = () => z.result.createObjectStore('uchwyty');
    z.onsuccess = () => ok(z.result);
    z.onerror = () => blad(z.error);
  });
}

async function zapamietajUchwyt(uchwyt) {
  const db = await idb();
  return new Promise((ok, blad) => {
    const t = db.transaction('uchwyty', 'readwrite');
    t.objectStore('uchwyty').put(uchwyt, 'plik');
    t.oncomplete = () => ok(true);
    t.onerror = () => blad(t.error);
  });
}

async function pobierzUchwyt() {
  try {
    const db = await idb();
    return await new Promise((ok) => {
      const t = db.transaction('uchwyty', 'readonly');
      const z = t.objectStore('uchwyty').get('plik');
      z.onsuccess = () => ok(z.result || null);
      z.onerror = () => ok(null);
    });
  } catch (e) { return null; }
}

// --- uchwyt do CALEGO folderu rozszerzenia (potrzebny do zapisu ikon) ---
let uchwytFolderu = null;

async function zapamietajFolder(uchwyt) {
  const db = await idb();
  return new Promise((ok, blad) => {
    const t = db.transaction('uchwyty', 'readwrite');
    t.objectStore('uchwyty').put(uchwyt, 'folder');
    t.oncomplete = () => ok(true);
    t.onerror = () => blad(t.error);
  });
}

async function pobierzFolder() {
  try {
    const db = await idb();
    return await new Promise((ok) => {
      const t = db.transaction('uchwyty', 'readonly');
      const z = t.objectStore('uchwyty').get('folder');
      z.onsuccess = () => ok(z.result || null);
      z.onerror = () => ok(null);
    });
  } catch (e) { return null; }
}

async function zapomnijUchwyt() {
  const db = await idb();
  return new Promise((ok) => {
    const t = db.transaction('uchwyty', 'readwrite');
    t.objectStore('uchwyty').delete('plik');
    t.oncomplete = () => ok(true);
    t.onerror = () => ok(false);
  });
}

async function maPrawo(uchwyt, pytac) {
  if (!uchwyt) return false;
  const opcje = { mode: 'readwrite' };
  if ((await uchwyt.queryPermission(opcje)) === 'granted') return true;
  if (!pytac) return false;
  return (await uchwyt.requestPermission(opcje)) === 'granted';
}

// Znacznik wersji, na ktorej opieraja sie nasze zmiany. Sluzy do wykrycia,
// czy w miedzyczasie ktos inny nie zapisal do wspolnego pliku - istotne,
// gdy folder lezy na dysku sieciowym i korzysta z niego kilka komputerow.
let bazaZapisu = 0;

async function zapiszDoPliku() {
  // 0. MOST - zapis bez pytan. Kolizje rozwiazujemy tak samo: scalamy.
  //    Piszemy TYLKO gdy most obsluguje ten sam folder, z ktorego dziala
  //    rozszerzenie - inaczej dane poszlyby do poprzedniej kopii programu.
  if (await sprawdzMost() && await sprawdzZgodnoscFolderu()) {
    try {
      const t = await mostCzytajTekst(folderProfilu() + '/kopia.json');
      if (t) {
        const cudze = JSON.parse(t);
        const czasPliku = cudze && cudze.zapisano ? cudze.zapisano : 0;
        if (poprawne(cudze) && czasPliku > bazaZapisu && czasPliku !== dane.zapisano) {
          const scalone = scalDane(dane, cudze);
          scalone.zapisano = Date.now();
          dane = scalone;
          if (dane.aktywna >= dane.grupy.length) dane.aktywna = 0;
          await chrome.storage.local.set({ [kluczDanych()]: dane });
          rysuj();
        }
      }
    } catch (e) { /* plik pusty lub uszkodzony */ }

    if (await mostZapiszTekst(folderProfilu() + '/kopia.json', JSON.stringify(dane, null, 2))) {
      bazaZapisu = dane.zapisano || 0;
      return true;
    }
  }

  // gdy mamy uchwyt do folderu - zapisujemy do <folder>\dane\kopia.json
  const kat = await katalogDanych(null);
  if (kat) {
    const plik = await kat.getFileHandle('kopia.json', { create: true });

    // KOLIZJA ZAPISU - rozwiazywana SAMA, bez pytania.
    // Gdy inny komputer zapisal w miedzyczasie, scalamy jego wersje z nasza
    // i zapisujemy wynik. Scalanie nie gubi niczego z zadnej strony, wiec
    // nie ma o co pytac.
    try {
      const obecny = await plik.getFile();
      if (obecny.size > 0) {
        const cudze = JSON.parse(await obecny.text());
        const czasPliku = cudze && cudze.zapisano ? cudze.zapisano : 0;
        if (czasPliku > bazaZapisu && czasPliku !== dane.zapisano && poprawne(cudze)) {
          const scalone = scalDane(dane, cudze);
          scalone.zapisano = Date.now();
          dane = scalone;
          if (dane.aktywna >= dane.grupy.length) dane.aktywna = 0;
          await chrome.storage.local.set({ [kluczDanych()]: dane });
          rysuj();
        }
      }
    } catch (e) { /* plik pusty lub uszkodzony - zapisujemy normalnie */ }

    const s = await plik.createWritable();
    await s.write(JSON.stringify(dane, null, 2));
    await s.close();
    bazaZapisu = dane.zapisano || 0;
    return true;
  }
  // tylko prawdziwy uchwyt PLIKU - katalog nie ma createWritable
  if (!uchwytPliku || uchwytPliku.kind !== 'file') return false;
  if (!(await maPrawo(uchwytPliku, false))) return false;
  const strumien = await uchwytPliku.createWritable();
  await strumien.write(JSON.stringify(dane, null, 2));
  await strumien.close();
  return true;
}

// =====================================================================
//  IKONY JAKO PLIKI PNG W PODFOLDERZE "ikony"
// =====================================================================
//  W danych trzymamy tylko NAZWE pliku, a nie sam obrazek. Dzieki temu
//  kopia.json zostaje mala (miesci sie w limicie konta Google), a ikony
//  leza na dysku obok rozszerzenia i wedruja razem z folderem.
//
//  Odczyt idzie przez chrome.runtime.getURL - to zasob rozszerzenia,
//  wiec nie wymaga zadnej zgody. Zapis wymaga uchwytu do folderu.
const KLUCZ_IKON = 'ikonyLokalne';   // awaryjny podglad, NIE trafia do kopii
let ikonyLokalne = {};

// Uklad folderu:
//   <folder rozszerzenia>\        - kod (manifest, newtab.*, tlo.js)
//     dane\                       - wszystko, co tworzy program
//       kopia.json                - skroty i grupy
//       ikony\*.png               - ikony, po jednym pliku na skrot
const PODF_DANE  = 'dane';
const PODF_IKONY = 'ikony';

// Pilnuje, zeby folder byl podlaczony ZANIM zrobimy cokolwiek z ikonami.
// Gdy go nie ma - pyta i od razu otwiera wybor folderu. Zwraca true/false.
async function zapewnijFolder() {
  // Most zalatwia wszystko - ale tylko wtedy, gdy obsluguje TEN folder.
  // Po skopiowaniu programu gdzie indziej wracamy do wyboru folderu.
  if (await sprawdzMost() && await sprawdzZgodnoscFolderu()) return true;
  if (uchwytFolderu) {
    if (await maPrawo(uchwytFolderu, false)) return true;
    if (await maPrawo(uchwytFolderu, true)) { await zapamietajZgodeSesji(); return true; }
    return false;
  }
  if (!window.showDirectoryPicker) {
    // Brave i kilka innych przegladarek wylacza dostep do plikow.
    // Nie milczymy - mowimy wprost, co jest grane i co dziala mimo to.
    pokazPasek('Ta przegladarka nie udostepnia wyboru folderu, wiec nie moze ZAPISYWAC do wspolnego pliku. ' +
               'Skroty i ikony nadal sie wczytuja przy starcie. W Brave sprobuj: brave://flags > "File System Access API" > Enabled.', {
      napis: 'Rozumiem',
      akcja: () => ukryjPasek()
    });
    return false;
  }
  // Bez pytania "czy chcesz wskazac folder" - samo okno wyboru JEST pytaniem.
  try {
    const u = await window.showDirectoryPicker({ mode: 'readwrite' });
    if (!(await maPrawo(u, true))) { pokazPasek('Brak zgody na zapis w folderze.'); return false; }
    uchwytFolderu = u;
    await zapamietajFolder(u);
    await zapamietajZgodeSesji();
    odswiezMenu();
    pokazPasek('Podlaczono folder "' + u.name + '".');
    return true;
  } catch (e) {
    if (e && e.name !== 'AbortError') pokazPasek('Nie udalo sie podlaczyc folderu: ' + (e.message || e));
    return false;
  }
}

// Uchwyt do samego <folder>\dane - wspolny korzen, niezalezny od profilu.
// Tu leza dane profilu domyslnego i lista profili.
async function katalogDaneKorzen() {
  if (!uchwytFolderu) return null;
  if (!(await maPrawo(uchwytFolderu, false))) return null;
  return await uchwytFolderu.getDirectoryHandle(PODF_DANE, { create: true });
}

// Uchwyt do danych AKTYWNEGO profilu, tworzac brakujace ogniwa.
// Domyslny siedzi wprost w dane\, kazdy inny w dane\profil-<nazwa>\.
async function katalogDanych(podfolder) {
  let kat = await katalogDaneKorzen();
  if (!kat) return null;
  if (profil !== PROFIL_DOMYSLNY) {
    kat = await kat.getDirectoryHandle('profil-' + bezpiecznaNazwa(profil), { create: true });
  }
  if (!podfolder) return kat;
  return await kat.getDirectoryHandle(podfolder, { create: true });
}

// Poczatek nazwy pliku ikony wynika WPROST z adresu strony - dzieki temu
// da sie potem sprawdzic, czy ikona nalezy do tego skrotu.
function prefiksIkony(url) {
  try { return new URL(url).hostname.replace(/^www\./, '').replace(/[^a-z0-9.-]/gi, '_'); }
  catch (e) { return 'ikona'; }
}

function nazwaPlikuIkony(url) {
  // krotki skrot adresu, zeby dwie strony z tej samej domeny sie nie nadpisaly
  let h = 0;
  for (let i = 0; i < url.length; i++) { h = ((h << 5) - h + url.charCodeAt(i)) | 0; }
  return prefiksIkony(url) + '_' + Math.abs(h).toString(36) + '.png';
}

// =====================================================================
//  MINIATURY STRON (zrzuty ekranu)
// =====================================================================
//  Zrzut robi proces tla w chwili dodawania strony z menu kontekstowego
//  i zostawia go w chrome.storage.local. Tutaj skalujemy go i zapisujemy
//  jako plik JPEG w dane\miniatury - dzieki temu wedruje z folderem
//  i pojawia sie na kazdym komputerze.
const PODF_MINIATURY = 'miniatury';
const MINI_SZER = 480;
const MINI_WYS = 300;

// podglad zrzutow zapisanych w tej sesji - zanim Chrome zobaczy nowe pliki
let miniaturyLokalne = {};

function nazwaPlikuMiniatury(url) {
  let h = 0;
  for (let i = 0; i < url.length; i++) { h = ((h << 5) - h + url.charCodeAt(i)) | 0; }
  return prefiksIkony(url) + '_' + Math.abs(h).toString(36) + '.jpg';
}

function adresMiniatury(nazwa) {
  return chrome.runtime.getURL(folderProfilu() + '/' + PODF_MINIATURY + '/' + nazwa);
}

// Przycina zrzut do proporcji kafelka (od gory, bo tam jest tresc strony).
function przytnijZrzut(dataUrl) {
  return new Promise((gotowe, blad) => {
    const img = new Image();
    img.onload = () => {
      try {
        const c = document.createElement('canvas');
        c.width = MINI_SZER; c.height = MINI_WYS;
        const g = c.getContext('2d');
        const skala = Math.max(MINI_SZER / img.width, MINI_WYS / img.height);
        const w = img.width * skala, h = img.height * skala;
        g.drawImage(img, (MINI_SZER - w) / 2, 0, w, h);     // rownamy do gory
        gotowe(c.toDataURL('image/jpeg', 0.72));
      } catch (e) { blad(e); }
    };
    img.onerror = () => blad(new Error('zly zrzut'));
    img.src = dataUrl;
  });
}

async function zapiszMiniatureNaDysk(url, dataUrl) {
  if (await sprawdzMost()) {
    const nazwa = nazwaPlikuMiniatury(url);
    if (await mostZapiszDataUrl(folderProfilu() + '/' + PODF_MINIATURY + '/' + nazwa, dataUrl)) {
      miniaturyLokalne[nazwa] = dataUrl;
      return nazwa;
    }
  }
  try {
    const kat = await katalogDanych(PODF_MINIATURY);
    if (!kat) return null;
    const nazwa = nazwaPlikuMiniatury(url);
    const plik = await kat.getFileHandle(nazwa, { create: true });
    const s = await plik.createWritable();
    await s.write(dataUrlNaBajty(dataUrl));
    await s.close();
    miniaturyLokalne[nazwa] = dataUrl;    // podglad, zanim Chrome zobaczy plik
    return nazwa;
  } catch (e) { return null; }
}

// Odbiera zrzut zostawiony przez proces tla i przypisuje go do skrotu.
async function odbierzMiniature() {
  let paczka = null;
  try {
    const s = await chrome.storage.local.get('miniaturaDoZapisu');
    paczka = s.miniaturaDoZapisu;
  } catch (e) { return; }
  if (!paczka || !paczka.dataUrl || !paczka.url) return;
  // Zrzut nalezy do konkretnego profilu - gdy dotyczy innego, zostawiamy go
  // w spokoju, zeby zapisal go ten profil, do ktorego trafil skrot.
  if (paczka.profil && paczka.profil !== profil) return;
  try { await chrome.storage.local.remove('miniaturaDoZapisu'); } catch (e) { /* nic */ }

  // szukamy skrotu o tym adresie
  let cel = null;
  for (const g of dane.grupy) for (const s of g.skroty) if (s.url === paczka.url) cel = s;
  if (!cel) return;

  try {
    const maly = await przytnijZrzut(paczka.dataUrl);
    const nazwa = await zapiszMiniatureNaDysk(paczka.url, maly);
    if (nazwa) {
      cel.miniatura = nazwa;
      await zapisz();
      rysuj();
    }
  } catch (e) { /* nieudany zrzut - kafelek zostaje z ikona */ }
}

// SAMONAPRAWA: wykrywa ikony, ktore nie pasuja do adresu skrotu. Powstaja,
// gdy adres zmieniono starsza wersja programu albo gdy dane przyszly
// z laptopa z nieaktualnym kodem. Wlasnych obrazkow nie ruszamy - one maja
// pasowac do wpisu, a nie do domeny.
async function naprawNiepasujaceIkony() {
  let poprawionych = 0;
  for (const g of dane.grupy) {
    for (const s of g.skroty) {
      if (!s.ikonaPlik || s.ikonaWlasna) continue;
      if (s.ikonaPlik.startsWith(prefiksIkony(s.url) + '_')) continue;
      delete s.ikonaPlik;          // wygeneruje sie na nowo, juz dla wlasciwej strony
      bezIkony.delete(s.url);
      poprawionych++;
    }
  }
  if (poprawionych) {
    await zapisz();
    rysuj();
    pokazPasek('Naprawiono ' + poprawionych + ' ikon nienalezacych do swoich stron - pobiora sie na nowo.');
  }
  return poprawionych;
}

function adresIkonyPliku(nazwa) {
  return chrome.runtime.getURL(folderProfilu() + '/' + PODF_IKONY + '/' + nazwa);
}

function dataUrlNaBajty(dataUrl) {
  const czesc = dataUrl.split(',')[1];
  const surowe = atob(czesc);
  const bajty = new Uint8Array(surowe.length);
  for (let i = 0; i < surowe.length; i++) bajty[i] = surowe.charCodeAt(i);
  return bajty;
}

// Zapisuje PNG do podfolderu "ikony". Zwraca nazwe pliku albo null.
async function zapiszIkoneNaDysk(url, dataUrl) {
  // przez most, gdy jest - bez uprawnien i bez uchwytu do folderu
  if (await sprawdzMost()) {
    const nazwa = nazwaPlikuIkony(url);
    if (await mostZapiszDataUrl(folderProfilu() + '/' + PODF_IKONY + '/' + nazwa, dataUrl)) {
      ikonyLokalne[nazwa] = dataUrl;
      await chrome.storage.local.set({ [kluczIkon()]: ikonyLokalne });
      return nazwa;
    }
  }
  try {
    const katalog = await katalogDanych(PODF_IKONY);
    if (!katalog) return null;
    const nazwa = nazwaPlikuIkony(url);
    const plik = await katalog.getFileHandle(nazwa, { create: true });
    const s = await plik.createWritable();
    await s.write(dataUrlNaBajty(dataUrl));
    await s.close();

    // podglad na teraz - Chrome moze nie widziec swiezo dodanego pliku,
    // dopoki rozszerzenie nie zostanie przeladowane
    ikonyLokalne[nazwa] = dataUrl;
    await chrome.storage.local.set({ [kluczIkon()]: ikonyLokalne });
    return nazwa;
  } catch (e) { return null; }
}

async function wczytajZPliku() {
  if (!uchwytPliku || uchwytPliku.kind !== 'file') return null;
  if (!(await maPrawo(uchwytPliku, false))) return null;
  try {
    const plik = await uchwytPliku.getFile();
    const o = JSON.parse(await plik.text());
    return poprawne(o) ? o : null;
  } catch (e) { return null; }
}

// =====================================================================
//  SCALANIE - serce "domowej chmury"
// =====================================================================
//  Kilka laptopow pracuje na przemian, czasem bez dostepu do wspolnego
//  folderu. Zasada "nowszy plik wygrywa" gubilaby wtedy zmiany zrobione
//  offline na drugiej maszynie, dlatego zamiast podmieniac - scalamy.
//
//  Zasady:
//    - skrot rozpoznajemy po ADRESIE w obrebie grupy,
//    - grupy laczymy po nazwie, kolejnosc bierzemy z wersji lokalnej,
//    - usuniecia zapamietujemy jako "nagrobki" (url + kiedy), inaczej
//      skasowany skrot wracalby z drugiego laptopa przy kazdej wymianie,
//    - nagrobek starszy niz dodanie skrotu przegrywa - czyli ponowne
//      dodanie tej samej strony po skasowaniu dziala poprawnie,
//    - wyglad bierzemy z wersji z nowszym znacznikiem czasu.
const DNI_NAGROBKA = 60;
const DNI_KOSZA = 30;      // ile dni trzymamy skasowane skroty w koszu
const MAX_KOSZ = 200;      // gorny limit pozycji w koszu

function kluczNagrobka(grupa, url) { return grupa + '|' + url; }

function zbierzNagrobki(...zrodla) {
  const mapa = new Map();
  for (const z of zrodla) {
    for (const n of (z && z.usuniete ? z.usuniete : [])) {
      if (!n || !n.url) continue;
      const k = kluczNagrobka(n.grupa || '', n.url);
      const stary = mapa.get(k);
      if (!stary || (n.kiedy || 0) > (stary.kiedy || 0)) mapa.set(k, n);
    }
  }
  // wyrzucamy przeterminowane, zeby lista nie rosla w nieskonczonosc
  const granica = Date.now() - DNI_NAGROBKA * 24 * 3600 * 1000;
  return Array.from(mapa.values()).filter((n) => (n.kiedy || 0) > granica);
}

function scalDane(lokalne, zdalne) {
  if (!poprawne(zdalne)) return lokalne;
  if (!poprawne(lokalne)) return zdalne;

  // nagrobki sortujemy - inaczej ta sama tresc dawalaby rozna kolejnosc
  // po obu stronach i porownanie 'rozniSie' wiecznie widzialoby zmiane
  const nagrobki = zbierzNagrobki(lokalne, zdalne)
    .sort((a, b) => (a.url + a.grupa).localeCompare(b.url + b.grupa));
  const mapaNagrobkow = new Map(nagrobki.map((n) => [kluczNagrobka(n.grupa || '', n.url), n.kiedy || 0]));

  // KOLEJNOSC MUSI BYC TAKA SAMA PO OBU STRONACH, inaczej kazdy komputer
  // w kolko przepisywalby plik po swojemu i powstawala petla zapisow.
  // Dlatego porzadek dyktuje wersja z NOWSZYM znacznikiem czasu.
  const pierwszy = (zdalne.zapisano || 0) > (lokalne.zapisano || 0) ? zdalne : lokalne;
  const drugi = (pierwszy === zdalne) ? lokalne : zdalne;

  const nazwy = [];
  for (const g of pierwszy.grupy) if (!nazwy.includes(g.nazwa)) nazwy.push(g.nazwa);
  for (const g of drugi.grupy) if (!nazwy.includes(g.nazwa)) nazwy.push(g.nazwa);

  const grupy = [];
  for (const nazwa of nazwy) {
    const gl = pierwszy.grupy.find((g) => g.nazwa === nazwa);
    const gz = drugi.grupy.find((g) => g.nazwa === nazwa);
    const mapa = new Map();

    for (const zrodlo of [gl, gz]) {
      for (const s of (zrodlo && zrodlo.skroty ? zrodlo.skroty : [])) {
        if (!s || !s.url) continue;
        const nagrobek = mapaNagrobkow.get(kluczNagrobka(nazwa, s.url)) || 0;
        if (nagrobek > (s.dodano || 0)) continue;      // skasowany i nie dodany ponownie
        const juz = mapa.get(s.url);
        // przy duplikacie wygrywa wpis nowszy (moze miec swiezsza ikone/nazwe)
        if (!juz || (s.dodano || 0) > (juz.dodano || 0)) mapa.set(s.url, s);
      }
    }
    // grupa pusta i nieobecna po zadnej stronie - nie tworzymy jej na sile
    if (mapa.size === 0 && !gl && !gz) continue;
    grupy.push({ nazwa, skroty: Array.from(mapa.values()) });
  }

  // Kosz tez scalamy - inaczej wymiana z innym komputerem by go czyscila.
  const koszMapa = new Map();
  for (const k of (lokalne.kosz || []).concat(zdalne.kosz || [])) {
    if (!k || !k.url) continue;
    const klucz = (k.grupa || '') + '|' + k.url + '|' + (k.usunieto || 0);
    if (!koszMapa.has(klucz)) koszMapa.set(klucz, k);
  }
  const granicaKosza = Date.now() - DNI_KOSZA * 24 * 3600 * 1000;
  const kosz = Array.from(koszMapa.values())
    .filter((k) => (k.usunieto || 0) > granicaKosza)
    .sort((a, b) => (b.usunieto || 0) - (a.usunieto || 0))
    .slice(0, MAX_KOSZ);

  const nowszy = (zdalne.zapisano || 0) > (lokalne.zapisano || 0) ? zdalne : lokalne;
  return {
    grupy,
    aktywna: Math.min(lokalne.aktywna || 0, Math.max(0, grupy.length - 1)),
    wyglad: nowszy.wyglad || lokalne.wyglad || { tlo: 'auto', rozmiar: 148 },
    usuniete: nagrobki,
    kosz,
    zapisano: Math.max(lokalne.zapisano || 0, zdalne.zapisano || 0)
  };
}

// Czy scalenie faktycznie cos zmienilo wobec wersji lokalnej?
function rozniSie(a, b) {
  const bezCzasu = (x) => JSON.stringify({
    g: x.grupy, w: x.wyglad,
    u: (x.usuniete || []).length,
    k: (x.kosz || []).length
  });
  return bezCzasu(a) !== bezCzasu(b);
}

function dodajNagrobek(nazwaGrupy, url) {
  if (!dane.usuniete) dane.usuniete = [];
  dane.usuniete.push({ grupa: nazwaGrupy, url, kiedy: Date.now() });
}

// =====================================================================
//  KOSZ
// =====================================================================
//  Skasowane skroty trafiaja do kosza zamiast znikac. Kosz jest czescia
//  danych, wiec widac go z kazdego komputera. Wpisy starsze niz DNI_KOSZA
//  wypadaja same. Stale sa wyzej, przy DNI_NAGROBKA.

function doKosza(nazwaGrupy, skrot) {
  if (!dane.kosz) dane.kosz = [];
  dane.kosz.unshift(Object.assign({}, skrot, { grupa: nazwaGrupy, usunieto: Date.now() }));
  const granica = Date.now() - DNI_KOSZA * 24 * 3600 * 1000;
  dane.kosz = dane.kosz.filter((k) => (k.usunieto || 0) > granica).slice(0, MAX_KOSZ);
}

// Przywraca wpis z kosza. Nowy znacznik 'dodano' sprawia, ze nagrobek
// (starszy) przestaje go blokowac przy scalaniu z innymi komputerami.
async function przywrocZKosza(indeks) {
  if (!dane.kosz || !dane.kosz[indeks]) return false;
  const wpis = dane.kosz.splice(indeks, 1)[0];
  const nazwaGrupy = wpis.grupa || dane.grupy[0].nazwa;
  delete wpis.grupa;
  delete wpis.usunieto;
  wpis.dodano = Date.now();

  let g = dane.grupy.find((x) => x.nazwa === nazwaGrupy);
  if (!g) { g = { nazwa: nazwaGrupy, skroty: [] }; dane.grupy.push(g); }
  if (!g.skroty.some((s) => s.url === wpis.url)) g.skroty.push(wpis);

  await zapisz();
  rysuj();
  pokazPasek('Przywrocono "' + wpis.nazwa + '" do grupy ' + nazwaGrupy + '.');
  return true;
}

// =====================================================================
//  KOPIE ZAPASOWE PER URZADZENIE
// =====================================================================
//  Kazdy laptop zostawia wlasna kopie w dane\kopie pod swoja nazwa.
//  Dzieki temu po nieudanym scaleniu albo pomylkowym skasowaniu mozna
//  wrocic do stanu z konkretnej maszyny i konkretnej godziny.
//  Przegladarka NIE zna nazwy komputera - pytamy o nia raz i zapamietujemy
//  lokalnie (nazwa nie synchronizuje sie, bo dotyczy tej jednej maszyny).
const PODF_KOPIE = 'kopie';
const ILE_KOPII = 10;

// Nazwa maszyny do kopii zapasowych. Przegladarka nie zna nazwy komputera,
// wiec NIE PYTAMY - nadajemy sensowna sama i pozwalamy zmienic w menu.
function nazwaUrzadzenia() {
  if (ustawienia.urzadzenie && ustawienia.urzadzenie.trim()) return ustawienia.urzadzenie.trim();
  return null;
}

async function zapewnijNazweUrzadzenia() {
  if (nazwaUrzadzenia()) return;
  let system = 'komputer';
  try {
    const p = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || '';
    if (/win/i.test(p)) system = 'Windows';
    else if (/mac/i.test(p)) system = 'Mac';
    else if (/linux/i.test(p)) system = 'Linux';
  } catch (e) { /* nic */ }

  let przegladarka = 'Chrome';
  const ua = navigator.userAgent || '';
  if (/Edg\//.test(ua)) przegladarka = 'Edge';
  else if (/OPR\//.test(ua)) przegladarka = 'Opera';
  else if (navigator.brave) przegladarka = 'Brave';
  else if (/Vivaldi/.test(ua)) przegladarka = 'Vivaldi';

  // krotki losowy przyrostek, zeby dwa takie same zestawy sie nie zlaly
  const sufiks = Math.random().toString(36).slice(2, 6);
  ustawienia.urzadzenie = system + '-' + przegladarka + '-' + sufiks;
  await zapiszUstawienia();
}

function bezpiecznaNazwa(t) {
  return String(t).replace(/[^A-Za-z0-9_-]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 32) || 'komputer';
}

async function zapiszKopieUrzadzenia() {
  const nazwa = nazwaUrzadzenia();
  if (!nazwa) return false;

  const stempel0 = new Date().toISOString().slice(0, 16).replace('T', '_').replace(':', '-');
  const nazwaPliku0 = bezpiecznaNazwa(nazwa) + '_' + stempel0 + '.json';

  // przez most - bez uprawnien
  if (await sprawdzMost()) {
    if (!(await mostZapiszTekst(folderProfilu() + '/' + PODF_KOPIE + '/' + nazwaPliku0, JSON.stringify(dane, null, 2)))) return false;
    // zostawiamy tylko ILE_KOPII najnowszych kopii tego urzadzenia
    try {
      const prefiks = bezpiecznaNazwa(nazwa) + '_';
      const moje = (await mostLista(folderProfilu() + '/' + PODF_KOPIE))
        .map((f) => f.nazwa)
        .filter((n) => n.startsWith(prefiks) && n.endsWith('.json'))
        .sort();
      while (moje.length > ILE_KOPII) {
        await mostUsun(folderProfilu() + '/' + PODF_KOPIE + '/' + moje.shift());
      }
    } catch (e) { /* sprzatanie nieobowiazkowe */ }
    return true;
  }

  const kat = await katalogDanych(PODF_KOPIE);
  if (!kat) return false;

  const stempel = stempel0;
  const plikNazwa = nazwaPliku0;
  try {
    const p = await kat.getFileHandle(plikNazwa, { create: true });
    const s = await p.createWritable();
    await s.write(JSON.stringify(dane, null, 2));
    await s.close();
  } catch (e) { return false; }

  // zostawiamy tylko ILE_KOPII najnowszych kopii TEGO urzadzenia
  try {
    const prefiks = bezpiecznaNazwa(nazwa) + '_';
    const moje = [];
    for await (const [n, u] of kat.entries()) {
      if (u.kind === 'file' && n.startsWith(prefiks) && n.endsWith('.json')) moje.push(n);
    }
    moje.sort();                              // nazwa zawiera date, wiec sortuje sie chronologicznie
    while (moje.length > ILE_KOPII) {
      const doUsuniecia = moje.shift();
      try { await kat.removeEntry(doUsuniecia); } catch (e) { /* nic */ }
    }
  } catch (e) { /* sprzatanie nieobowiazkowe */ }
  return true;
}

// Jedna wymiana z plikiem - w obie strony, bez pytan.
let ostatniZnacznikPliku = 0;
async function synchronizujZPlikiem(cicho) {
  if (!uchwytZgody()) return 'brak';
  if (!(await maPrawo(uchwytZgody(), false))) return 'brakZgody';

  // przy podlaczonym folderze czytamy zasob rozszerzenia (bez zgody),
  // przy samym uchwycie pliku - z niego
  const zPliku = uchwytFolderu ? await wczytajZDysku() : await wczytajZPliku();
  if (!zPliku) {
    // pliku jeszcze nie ma - zakladamy go naszymi danymi
    if (await zapiszDoPliku()) {
      if (!cicho) pokazPasek('Utworzono wspolny plik z biezaca zawartoscia.');
      return 'wyslano';
    }
    return 'brak';
  }

  // SCALANIE w obie strony: nasze zmiany + ich zmiany = wspolny wynik
  const scalone = scalDane(dane, zPliku);
  const zmianaUNas   = rozniSie(scalone, dane);
  const zmianaUNich  = rozniSie(scalone, zPliku);

  if (zmianaUNas) {
    dane = scalone;
    if (dane.aktywna >= dane.grupy.length) dane.aktywna = 0;
    await chrome.storage.local.set({ [kluczDanych()]: dane });
    rysuj();
    if (!cicho) pokazPasek('Scalono zmiany ze wspolnego pliku.');
    await sprawdzWyglad();
  }

  if (zmianaUNich) {
    dane = scalone;
    dane.zapisano = Date.now();          // wynik scalenia jest nowsza wersja
    bazaZapisu = dane.zapisano;
    await chrome.storage.local.set({ [kluczDanych()]: dane });
    await zapiszDoPliku();
    ostatniZnacznikPliku = dane.zapisano;
    if (!cicho) pokazPasek('Wyslano scalona wersje do wspolnego pliku.');
  }

  if (!zmianaUNas && !zmianaUNich) {
    bazaZapisu = Math.max(bazaZapisu, zPliku.zapisano || 0);
    return 'bezZmian';
  }
  return zmianaUNas ? 'pobrano' : 'wyslano';
}

// ---------------------------------------------------------------------
//  Zgoda na plik: przegladarka kasuje ja, gdy zamkna sie wszystkie karty
//  rozszerzenia - a strona nowej karty otwiera sie i zamyka bez przerwy.
//  Nie da sie tego zapamietac z poziomu kodu, ale mozna ograniczyc pytania
//  do JEDNEGO na uruchomienie przegladarki: znacznik trzymamy w
//  chrome.storage.session, ktory znika przy zamknieciu przegladarki.
//  Gdy zgoda juz w tej sesji padla, kolejne karty prosza o nia po cichu,
//  przy pierwszym Twoim klikniecu gdziekolwiek - bez zadnego paska.
// ---------------------------------------------------------------------
async function zgodaWSesji() {
  try {
    const s = await chrome.storage.session.get('zgodaPlik');
    return !!s.zgodaPlik;
  } catch (e) { return false; }
}

async function zapamietajZgodeSesji() {
  try { await chrome.storage.session.set({ zgodaPlik: true }); } catch (e) { /* nic */ }
}

// Po cichu, przy pierwszym gescie uzytkownika na stronie.
function zgodaPrzyPierwszymKliknieciu() {
  const sprobuj = async () => {
    document.removeEventListener('pointerdown', sprobuj, true);
    document.removeEventListener('keydown', sprobuj, true);
    try {
      if (await maPrawo(uchwytZgody(), true)) {
        await zapamietajZgodeSesji();
        await synchronizujZPlikiem(true);
      }
    } catch (e) { /* nic */ }
  };
  document.addEventListener('pointerdown', sprobuj, true);
  document.addEventListener('keydown', sprobuj, true);
}

// Prosba widoczna - tylko raz na uruchomienie przegladarki.
function poprosOZgode() {
  pokazPasek('Wspolny plik czeka na zgode. Przegladarka kasuje ja po zamknieciu, ' +
             'wiec pytam raz na jej uruchomienie. Jesli w oknie zgody bedzie opcja ' +
             '"Zezwalaj przy kazdej wizycie" - zaznacz ja.', {
    napis: 'Zezwol i zsynchronizuj',
    akcja: async () => {
      if (await maPrawo(uchwytZgody(), true)) {
        ukryjPasek();
        await zapamietajZgodeSesji();
        const co = await synchronizujZPlikiem(false);
        if (co === 'bezZmian') pokazPasek('Polaczono - obie wersje sa identyczne.');
      } else {
        pokazPasek('Bez zgody wymiana przez plik nie zadziala.');
      }
    }
  });
}

// Komunikat na dole ekranu. Opcjonalnie z przyciskiem - potrzebny tam,
// gdzie przegladarka wymaga klikniecia uzytkownika (np. zgoda na plik).
let czasomierzPaska = null;
// Pasek z przyciskami czeka na decyzje uzytkownika i NIE MOZE zostac
// nadpisany przez zwykly komunikat (np. o dogranych ikonach) - wczesniej
// znikal, zanim ktokolwiek zdazyl kliknac.
let pasekZablokowany = false;

function ukryjPasek() {
  pasekZablokowany = false;
  clearTimeout(czasomierzPaska);
  const p = $('pasek');
  if (p) p.hidden = true;
}

function pokazPasek(tekst, przyciski) {
  const p = $('pasek');
  if (!p) return;

  const lista = !przyciski ? [] : (Array.isArray(przyciski) ? przyciski : [przyciski]);
  if (!lista.length && pasekZablokowany) return;   // nie przykrywamy pytania

  p.textContent = '';
  p.appendChild(document.createTextNode(tekst));
  clearTimeout(czasomierzPaska);

  for (const przycisk of lista) {
    const b = document.createElement('button');
    b.textContent = przycisk.napis;
    b.className = 'pasekPrzycisk' + (przycisk.drugi ? ' drugi' : '');
    b.addEventListener('click', async (ev) => {
      pasekZablokowany = false;      // decyzja zapadla - pasek znow wolny
      await przycisk.akcja(ev);
    });
    p.appendChild(b);
  }

  pasekZablokowany = lista.length > 0;
  p.hidden = false;
  if (!lista.length) czasomierzPaska = setTimeout(() => { p.hidden = true; }, 5000);
}

function grupa() { return dane.grupy[dane.aktywna]; }

// ------------------------------------------------------------ pomocnicze
function pelnyUrl(wpisane) {
  const t = wpisane.trim();
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(t)) return t;
  return 'https://' + t;
}

function nazwaZUrl(url) {
  try {
    const h = new URL(url).hostname.replace(/^www\./, '');
    return h.split('.')[0].replace(/^./, (c) => c.toUpperCase());
  } catch (e) {
    return url;
  }
}

// Lokalny magazyn faviconow Chrome - zadnego ruchu do internetu.
// Dozwolone rozmiary to 16/24/32/48; przy innych API zwraca domyslny globus.
function adresIkony(url) {
  const u = new URL(chrome.runtime.getURL('/_favicon/'));
  u.searchParams.set('pageUrl', url);
  u.searchParams.set('size', '32');
  return u.toString();
}

function hostZUrl(url) {
  try { return new URL(url).host; } catch (e) { return null; }
}

// Probuje zaladowac obrazek pod danym adresem. Zwraca true/false.
function sprobujObrazek(adres) {
  return new Promise((gotowe) => {
    const img = new Image();
    let rozstrzygniete = false;
    const koniec = (wynik) => { if (!rozstrzygniete) { rozstrzygniete = true; gotowe(wynik); } };
    img.onload  = () => koniec(img.naturalWidth > 0);
    img.onerror = () => koniec(false);
    setTimeout(() => koniec(false), 6000);
    img.src = adres;
  });
}

function blobNaDataUrl(blob) {
  return new Promise((ok, blad) => {
    const r = new FileReader();
    r.onload = () => ok(r.result);
    r.onerror = () => blad(r.error);
    r.readAsDataURL(blob);
  });
}

// Pobiera ikone z witryny i od razu ZAPISUJE JA JAKO PLIK w dane\ikony.
// Wymaga jednorazowej zgody na dostep do witryn - o nia prosimy tylko tutaj.
async function pobierzIkoneJakoPlik(url) {
  const host = hostZUrl(url);
  if (!host) return null;
  const kandydaci = [
    'https://' + host + '/favicon.ico',
    'https://' + host + '/apple-touch-icon.png',
    'https://' + host + '/favicon.png'
  ];
  for (const k of kandydaci) {
    try {
      const odp = await fetch(k, { cache: 'no-store' });
      if (!odp.ok) continue;
      const blob = await odp.blob();
      if (!blob.size || !/^image\//i.test(blob.type || 'image/x')) continue;
      const dataUrl = await skalujDoDataUrl(await blobNaDataUrl(blob));
      return { dataUrl, zrodlo: k };
    } catch (e) { /* nastepny kandydat */ }
  }
  return null;
}

// Ikony z witryn: prosimy raz o zgode, potem kazda pobrana ikona ląduje
// od razu jako plik PNG skojarzony z adresem strony.
async function odswiezIkony(skroty, etykieta) {
  if (!(await zapewnijFolder())) {
    pokazPasek('Przerwane - bez podlaczonego folderu nie zapisze ikon jako plikow.');
    return;
  }
  let maZgode = false;
  try {
    maZgode = await chrome.permissions.contains({ origins: ['https://*/*'] });
    if (!maZgode) {
      maZgode = await chrome.permissions.request({ origins: ['https://*/*'] });
    }
  } catch (e) { maZgode = false; }

  const kat = await katalogDanych(PODF_IKONY);
  let doPlikow = 0, doAdresu = 0, brak = 0;

  for (let i = 0; i < skroty.length; i++) {
    const s = skroty[i];
    pokazPasek('Pobieram ikony (' + etykieta + '): ' + (i + 1) + ' z ' + skroty.length + '...');
    const host = hostZUrl(s.url);
    if (!host || /^(chrome|edge|brave|about|file)/i.test(s.url)) continue;

    if (maZgode) {
      const wynik = await pobierzIkoneJakoPlik(s.url);
      if (wynik) {
        const nazwa = kat ? await zapiszIkoneNaDysk(s.url, wynik.dataUrl) : null;
        if (nazwa) { s.ikonaPlik = nazwa; delete s.ikona; doPlikow++; }
        else { s.ikona = wynik.dataUrl; doPlikow++; }   // folder niepodlaczony
        continue;
      }
      brak++;
      continue;
    }

    // bez zgody na dostep do witryn zostaje stary sposob: sam adres obrazka
    const kandydaci = [
      'https://' + host + '/favicon.ico',
      'https://' + host + '/apple-touch-icon.png',
      'https://' + host + '/favicon.png'
    ];
    let znaleziona = null;
    for (const k of kandydaci) { if (await sprobujObrazek(k)) { znaleziona = k; break; } }
    if (znaleziona) { s.ikona = znaleziona; doAdresu++; } else { brak++; }
  }

  await zapisz();
  rysuj();

  if (maZgode) {
    pokazPasek('Gotowe: ' + doPlikow + ' ikon zapisano jako pliki w dane\\ikony, ' +
               brak + ' stron nie udostepnia ikony pod standardowym adresem.');
  } else {
    pokazPasek('Bez zgody na dostep do witryn zapisalem tylko adresy ' + doAdresu +
               ' ikon (nie stana sie plikami). Brak ikony: ' + brak + '.');
  }
}

// --------------------------------------------------------------- widok
function rysujGrupy() {
  const cel = $('grupy');
  cel.textContent = '';
  dane.grupy.forEach((g, i) => {
    const b = document.createElement('button');
    b.textContent = g.nazwa;
    if (i === dane.aktywna) b.className = 'aktywna';
    b.addEventListener('click', async () => {
      dane.aktywna = i;
      await zapisz();
      rysuj();
    });

    // Zakladka grupy jako cel przeciagania - upuszczenie kafelka
    // przenosi skrot z biezacej grupy do tej.
    b.addEventListener('dragover', (e) => {
      if (i === dane.aktywna) return;      // do siebie samej nie ma sensu
      e.preventDefault();
      b.classList.add('celGrupy');
    });
    b.addEventListener('dragleave', () => b.classList.remove('celGrupy'));
    b.addEventListener('drop', async (e) => {
      e.preventDefault();
      b.classList.remove('celGrupy');
      if (i === dane.aktywna) return;
      const skad = parseInt(e.dataTransfer.getData('text/plain'), 10);
      if (Number.isNaN(skad)) return;
      const [przenoszony] = grupa().skroty.splice(skad, 1);
      if (!przenoszony) return;
      dane.grupy[i].skroty.push(przenoszony);
      await zapisz();
      rysuj();
      pokazPasek('Przeniesiono "' + przenoszony.nazwa + '" do grupy ' + dane.grupy[i].nazwa + '.');
    });

    cel.appendChild(b);
  });
}

function rysujSkroty() {
  const cel = $('siatka');
  const filtr = $('szukaj').value.trim().toLowerCase();
  cel.textContent = '';

  const lista = grupa().skroty
    .map((s, i) => ({ s, i }))
    .filter(({ s }) => !filtr ||
      s.nazwa.toLowerCase().includes(filtr) ||
      s.url.toLowerCase().includes(filtr));

  $('pusto').hidden = grupa().skroty.length !== 0;

  for (const { s, i } of lista) {
    const a = document.createElement('a');
    a.className = 'kafel';
    a.href = s.url;
    a.draggable = true;
    a.dataset.indeks = String(i);

    // Chrome nie pozwala otwierac adresow chrome:// czy edge:// klikiem w link.
    // Zamiast martwego kafelka kopiujemy adres do schowka.
    if (/^(chrome|edge|brave|opera|vivaldi|about|chrome-extension):/i.test(s.url)) {
      a.addEventListener('click', async (e) => {
        e.preventDefault();
        try {
          await navigator.clipboard.writeText(s.url);
          pokazPasek('Chrome blokuje otwieranie "' + s.url + '" z linku. Adres skopiowano - wklej go w pasek adresu.');
        } catch (err) {
          pokazPasek('Ten adres trzeba wpisac recznie w pasku adresu: ' + s.url);
        }
      });
    }

    // Kolejnosc szukania ikony:
    //   1. plik PNG w podfolderze "ikony" (przenosny, w danych tylko nazwa)
    //   2. podglad lokalny, gdy plik jest swiezy i Chrome go jeszcze nie widzi
    //   3. adres pobrany z witryny  4. magazyn faviconow Chrome  5. litera
    const zrodla = [];
    if (s.ikonaPlik) {
      zrodla.push(adresIkonyPliku(s.ikonaPlik));
      if (ikonyLokalne[s.ikonaPlik]) zrodla.push(ikonyLokalne[s.ikonaPlik]);
    }
    if (s.ikona) zrodla.push(s.ikona);
    zrodla.push(adresIkony(s.url));

    // MINIATURA - zrzut strony jako tlo kafelka, ikona schodzi do stopki
    const pokazMiniature = (wyglad().miniatury !== false) && s.miniatura;
    if (pokazMiniature) {
      a.classList.add('zMiniatura');
      const mini = document.createElement('img');
      mini.className = 'miniatura';
      mini.alt = '';
      mini.src = miniaturyLokalne[s.miniatura] || adresMiniatury(s.miniatura);
      mini.addEventListener('error', () => {
        // pliku nie ma (np. przyszedl z innego komputera) - wracamy do ikony
        a.classList.remove('zMiniatura');
        mini.remove();
      });
      a.appendChild(mini);
    }

    const ikona = document.createElement('img');
    ikona.alt = '';
    let nrZrodla = 0;
    ikona.src = zrodla[0];
    ikona.addEventListener('error', () => {
      nrZrodla++;
      if (nrZrodla < zrodla.length) { ikona.src = zrodla[nrZrodla]; return; }
      const z = document.createElement('div');
      z.className = 'literka';
      z.textContent = (s.nazwa[0] || '?').toUpperCase();
      ikona.replaceWith(z);
    });
    a.appendChild(ikona);

    const n = document.createElement('div');
    n.className = 'nazwa';
    n.textContent = s.nazwa;
    a.appendChild(n);

    const ad = document.createElement('div');
    ad.className = 'adres';
    try { ad.textContent = new URL(s.url).hostname.replace(/^www\./, ''); } catch (e) { ad.textContent = s.url; }
    a.appendChild(ad);

    const akcje = document.createElement('div');
    akcje.className = 'akcje';

    const edytuj = document.createElement('button');
    edytuj.textContent = '✎';
    edytuj.title = 'Edytuj';
    edytuj.addEventListener('click', (e) => { e.preventDefault(); e.stopPropagation(); otworzOkno(i); });
    akcje.appendChild(edytuj);

    const usun = document.createElement('button');
    usun.textContent = '×';
    usun.title = 'Usun';
    usun.addEventListener('click', async (e) => {
      e.preventDefault(); e.stopPropagation();
      doKosza(grupa().nazwa, s);             // najpierw kosz, potem nagrobek
      dodajNagrobek(grupa().nazwa, s.url);   // zeby nie wrocil z drugiego laptopa
      grupa().skroty.splice(i, 1);
      await zapisz();
      rysuj();
      pokazPasek('Usunieto "' + s.nazwa + '". Mozna cofnac.', {
        napis: 'Cofnij',
        akcja: async () => { ukryjPasek(); await przywrocZKosza(0); }
      });
    });
    akcje.appendChild(usun);
    a.appendChild(akcje);

    // przeciaganie w obrebie grupy
    a.addEventListener('dragstart', (e) => {
      e.dataTransfer.setData('text/plain', String(i));
      a.classList.add('przeciagany');
    });
    a.addEventListener('dragend', () => a.classList.remove('przeciagany'));
    a.addEventListener('dragover', (e) => { e.preventDefault(); a.classList.add('cel'); });
    a.addEventListener('dragleave', () => a.classList.remove('cel'));
    a.addEventListener('drop', async (e) => {
      e.preventDefault();
      a.classList.remove('cel');
      const skad = parseInt(e.dataTransfer.getData('text/plain'), 10);
      const dokad = i;
      if (Number.isNaN(skad) || skad === dokad) return;
      // ZAMIANA MIEJSCAMI - oba kafelki wymieniaja sie pozycjami,
      // reszta ukladu zostaje nietknieta.
      const lista = grupa().skroty;
      if (!lista[skad] || !lista[dokad]) return;
      const pom = lista[skad];
      lista[skad] = lista[dokad];
      lista[dokad] = pom;
      await zapisz();
      rysuj();
    });

    cel.appendChild(a);
  }
}

// Rozmiar kafelka steruje wszystkim: ikona, czcionki i odstepy licza sie
// z niego w CSS, wiec wieksze kafelki maja tez wieksze miniaturki.
function zastosujRozmiar() {
  const k = parseInt(wyglad().rozmiar, 10) || 148;
  const korzen = document.documentElement.style;
  korzen.setProperty('--kafel', k + 'px');
  korzen.setProperty('--ikona', Math.round(k * 0.30) + 'px');
  korzen.setProperty('--odstep', Math.round(8 + k * 0.055) + 'px');
  const pole = $('rozmiar');
  if (pole && String(pole.value) !== String(k)) pole.value = String(k);
}

// =====================================================================
//  WYGLAD - czesc DANYCH, nie ustawien przegladarki
// =====================================================================
//  Motyw i rozmiar kafelkow siedza w dane.wyglad, dzieki czemu wedruja
//  tak samo jak skroty: przez wspolny plik i przez konto Google.
//  Zmiana w jednej przegladarce pojawia sie w drugiej - z pytaniem,
//  zeby nie podmieniac komus wygladu bez uprzedzenia.
function wyglad() {
  if (!dane.wyglad) dane.wyglad = { tlo: 'auto', rozmiar: 148 };
  return dane.wyglad;
}

// ostatnio zastosowany wyglad - do wykrywania zmian przychodzacych z zewnatrz
let wygladWuzyciu = { tlo: 'auto', rozmiar: 148 };

function zastosujTlo() {
  const t = wyglad().tlo || 'auto';
  if (t === 'auto') document.documentElement.removeAttribute('data-tlo');
  else document.documentElement.setAttribute('data-tlo', t);
  const pole = $('tlo');
  if (pole && pole.value !== t) pole.value = t;
}

const NAZWY_TLA = {
  auto: 'systemowe', grafit: 'Grafit', granat: 'Granat', fiolet: 'Nocny fiolet',
  las: 'Las', ocean: 'Ocean', zachod: 'Zachod slonca', czern: 'Czern',
  papier: 'Papier', mgla: 'Mgla'
};

// Wywolywane po kazdym przyjeciu danych z zewnatrz. Gdy druga przegladarka
// zmienila wyglad, pytamy - a odmowa odsyla NASZ wyglad z powrotem, wiec
// lustro dziala w obie strony.
// Wyglad zmieniony na innym komputerze stosujemy OD RAZU, bez pytania.
// To ma byc lustro: co ustawisz na jednej maszynie, widzisz na kazdej.
async function sprawdzWyglad() {
  const nowy = wyglad();
  const inny = (nowy.tlo !== wygladWuzyciu.tlo) || (String(nowy.rozmiar) !== String(wygladWuzyciu.rozmiar));
  if (!inny) return;

  zastosujTlo();
  zastosujRozmiar();
  wygladWuzyciu = { tlo: nowy.tlo, rozmiar: nowy.rozmiar };
}

// =====================================================================
//  WSKAZNIK SYNCHRONIZACJI
// =====================================================================
//  Zawsze widoczny w pasku. Gdy folder nie jest podlaczony albo zgoda
//  wygasla, przegladarka NIE wymienia sie danymi - i musi to byc widac
//  od razu, bez zagladania w menu.
async function odswiezStanSync() {
  const p = $('stanSync');
  if (!p) return;
  let przezMost = false;
  try { przezMost = await sprawdzMost(); } catch (e) { przezMost = false; }

  let dziala = przezMost;
  if (!dziala) {
    try { dziala = !!(uchwytFolderu && await maPrawo(uchwytFolderu, false)); } catch (e) { dziala = false; }
  }

  const brakApi = !window.showDirectoryPicker;

  if (przezMost) {
    p.classList.add('dziala');
    p.classList.remove('nie-dziala', 'tylko-odczyt');
    // pokazujemy WPROST, ktory folder obsluguje most - po skopiowaniu
    // programu na inny dysk od razu widac, czy trzeba uruchomic INSTALUJ.bat
    const krotka = mostBaza ? mostBaza.replace(/^.*[\\/]/, '') : '?';
    p.textContent = 'Sync OK: ' + krotka;
    p.title = 'Most obsluguje folder:\n' + (mostBaza || '?') +
              '\n\nJesli to NIE jest folder tego rozszerzenia, uruchom w nim INSTALUJ.bat.';
    return;
  }

  p.classList.toggle('dziala', dziala);
  p.classList.toggle('nie-dziala', !dziala && !brakApi);
  p.classList.toggle('tylko-odczyt', !dziala && brakApi);

  if (dziala) {
    p.textContent = 'Sync OK';
    p.title = 'Wymiana danych z folderem dziala. Zmiany z innych komputerow przychodza co 5 sekund.';
  } else if (brakApi) {
    p.textContent = 'Tylko odczyt';
    p.title = 'Ta przegladarka nie udostepnia wyboru folderu (np. Brave z wylaczonym File System Access API). ' +
              'Skroty i ikony wczytuja sie przy starcie, ale zmiany zrobione tutaj nie trafia do pozostalych komputerow.';
  } else if (uchwytFolderu) {
    p.textContent = 'Sync: kliknij, aby wznowic';
    p.title = 'Zgoda na zapis wygasla po zamknieciu przegladarki. Jedno klikniecie ja przywroci.';
  } else {
    p.textContent = 'BRAK SYNCHRONIZACJI - kliknij';
    p.title = 'Folder nie jest podlaczony. Ta przegladarka nie wymienia sie danymi z pozostalymi.';
  }
}

function rysuj() { rysujGrupy(); rysujSkroty(); }

// =====================================================================
//  IKONY WLASNE I OSADZANIE
// =====================================================================
//  Ikona zapisana jako data: URL wedruje razem z danymi, wiec pojawia sie
//  w kazdej przegladarce. Favicon z magazynu Chrome jest lokalny - dlatego
//  osobna funkcja przerysowuje go na canvas i osadza w danych.
// 128 px zamiast 64 - przy duzych kafelkach ikona 64 px bylaby rozmyta.
// Plik rosnie z okolo 3 KB do 6-10 KB, co przy kilkudziesieciu skrotach
// nadal jest niczym, a dane w kopia.json sie nie zmieniaja (tam jest nazwa).
const ROZMIAR_IKONY = 128;

function skalujDoDataUrl(zrodlo) {
  return new Promise((gotowe, blad) => {
    const img = new Image();
    img.onload = () => {
      try {
        const c = document.createElement('canvas');
        c.width = ROZMIAR_IKONY;
        c.height = ROZMIAR_IKONY;
        const g = c.getContext('2d');
        g.imageSmoothingQuality = 'high';
        // wpisujemy obrazek w kwadrat, zachowujac proporcje
        const skala = Math.min(ROZMIAR_IKONY / img.width, ROZMIAR_IKONY / img.height);
        const w = img.width * skala, h = img.height * skala;
        g.drawImage(img, (ROZMIAR_IKONY - w) / 2, (ROZMIAR_IKONY - h) / 2, w, h);
        gotowe(c.toDataURL('image/png'));
      } catch (e) { blad(e); }
    };
    img.onerror = () => blad(new Error('Nie udalo sie wczytac obrazka'));
    img.src = zrodlo;
  });
}

function plikNaDataUrl(plik) {
  return new Promise((ok, blad) => {
    const r = new FileReader();
    r.onload = () => ok(r.result);
    r.onerror = () => blad(r.error);
    r.readAsDataURL(plik);
  });
}

// Wzorzec domyslnego globusa - Chrome zwraca go, gdy nie zna ikony strony.
// Sluzy do rozpoznania, czego nie warto osadzac.
let wzorzecGlobusa = null;
async function pobierzWzorzecGlobusa() {
  if (wzorzecGlobusa !== null) return wzorzecGlobusa;
  try {
    wzorzecGlobusa = await skalujDoDataUrl(adresIkony('https://nieistniejaca-domena-testowa.invalid'));
  } catch (e) { wzorzecGlobusa = ''; }
  return wzorzecGlobusa;
}

// Ustawia ikone skrotu: probuje zapisac plik PNG obok rozszerzenia,
// a gdy nie ma uchwytu do folderu - wpisuje obrazek prosto do danych.
async function ustawIkone(s, dataUrl) {
  const nazwa = await zapiszIkoneNaDysk(s.url, dataUrl);
  if (nazwa) {
    s.ikonaPlik = nazwa;
    delete s.ikona;         // nie duplikujemy obrazka w danych
    return 'plik';
  }
  s.ikona = dataUrl;
  return 'dane';
}

// Przenosi ikony wpisane do danych (data:) na dysk jako pliki PNG,
// zaklada strukture podfolderow i odchudza kopia.json.
async function uporzadkujFolder() {
  if (!(await zapewnijFolder())) {
    pokazPasek('Przerwane - folder rozszerzenia nie zostal podlaczony.');
    return;
  }
  // Gdy dziala most, ikony zapisuje ON - uchwyt do folderu nie jest wtedy
  // potrzebny i nie ma po co go wymagac. Sprawdzamy katalog tylko na drugiej
  // drodze, przez uchwyt.
  if (!(await sprawdzMost())) {
    const kat = await katalogDanych(PODF_IKONY);
    if (!kat) { pokazPasek('Nie udalo sie utworzyc podfolderu dane\\ikony.'); return; }
  }

  const wszystkie = dane.grupy.reduce((t, g) => t.concat(g.skroty), []);
  let przeniesionych = 0, bledow = 0;

  for (let i = 0; i < wszystkie.length; i++) {
    const s = wszystkie[i];
    if (!s.ikona || !s.ikona.startsWith('data:')) continue;
    pokazPasek('Porzadkuje: ' + (i + 1) + ' z ' + wszystkie.length + '...');
    const nazwa = await zapiszIkoneNaDysk(s.url, s.ikona);
    if (nazwa) { s.ikonaPlik = nazwa; delete s.ikona; przeniesionych++; }
    else bledow++;
  }

  await zapisz();   // zapisze tez dane\kopia.json
  rysuj();

  const osierocone = await usunOsieroconeIkony();
  const rozmiar = Math.round(JSON.stringify(dane).length / 1024);
  pokazPasek('Uporzadkowane. Przeniesiono ' + przeniesionych + ' ikon do plikow' +
             (bledow ? (', ' + bledow + ' sie nie udalo') : '') +
             (osierocone ? (', usunieto ' + osierocone + ' osieroconych') : '') +
             '. kopia.json ma teraz okolo ' + rozmiar + ' KB.');
}

// Usuwa z dane\ikony pliki, do ktorych nie odwoluje sie juz zaden skrot.
// Wolane po kazdym usunieciu skrotu, grupy albo zmianie adresu - inaczej
// osierocone obrazki zostawaly na dysku na zawsze.
async function usunOsieroconeIkony() {
  const kat = await katalogDanych(PODF_IKONY);
  if (!kat) return 0;                       // brak folderu lub zgody - cicho

  const uzywane = new Set();
  for (const g of dane.grupy) {
    for (const s of g.skroty) if (s.ikonaPlik) uzywane.add(s.ikonaPlik);
  }

  const doUsuniecia = [];
  try {
    for await (const [nazwa, uchwyt] of kat.entries()) {
      if (uchwyt.kind === 'file' && /\.png$/i.test(nazwa) && !uzywane.has(nazwa)) {
        doUsuniecia.push(nazwa);
      }
    }
  } catch (e) { return 0; }

  let usuniete = 0;
  for (const n of doUsuniecia) {
    try {
      await kat.removeEntry(n);
      delete ikonyLokalne[n];
      stanPlikowIkon.delete(n);
      usuniete++;
    } catch (e) { /* plik zajety - zostanie na nastepny raz */ }
  }
  if (usuniete) await chrome.storage.local.set({ [kluczIkon()]: ikonyLokalne });
  return usuniete;
}

// =====================================================================
//  DOGRYWANIE IKON W TLE
// =====================================================================
//  Chrome zapisuje favicon do swojego magazynu dopiero PO odwiedzeniu
//  strony - dlatego swiezo dodany skrot ma globus, a ikona pojawia sie
//  po pierwszym klikniecu. Ta funkcja chodzi po cichu w tle i gdy tylko
//  Chrome pozna ikone, zapisuje ja jako plik PNG. Bez pytan i komunikatow.
const bezIkony = new Set();     // adresy sprawdzone bez skutku w tej sesji
let dogrywanieTrwa = false;
const stanPlikowIkon = new Map();   // nazwa pliku -> czy istnieje

// Czy plik ikony faktycznie lezy w dane\ikony? Wazne po przeniesieniu
// przez konto Google: dane przychodza, ale PLIKI ikon juz nie - wtedy
// trzeba je odtworzyc lokalnie, zamiast zostawic kafelek z litera.
async function plikIkonyIstnieje(nazwa) {
  if (stanPlikowIkon.has(nazwa)) return stanPlikowIkon.get(nazwa);
  let jest = false;
  try {
    const r = await fetch(adresIkonyPliku(nazwa), { cache: 'no-store' });
    jest = r.ok;
  } catch (e) { jest = false; }
  stanPlikowIkon.set(nazwa, jest);
  return jest;
}

async function dogrywajIkony() {
  if (dogrywanieTrwa || !uchwytFolderu) return;
  if (!(await maPrawo(uchwytFolderu, false))) return;   // bez pytania

  dogrywanieTrwa = true;
  try {
    const globus = await pobierzWzorzecGlobusa();
    const wszystkie = dane.grupy.reduce((t, g) => t.concat(g.skroty), []);
    let zmiany = 0;

    for (const s of wszystkie) {
      if (s.ikona) continue;                      // wlasny obrazek lub adres
      if (bezIkony.has(s.url)) continue;
      if (/^(chrome|edge|brave|about|file)/i.test(s.url)) { bezIkony.add(s.url); continue; }

      // Ikona przypisana, ale czy plik tu jest? Po synchronizacji przez
      // konto Google dane przychodza bez plikow - wtedy odtwarzamy lokalnie.
      if (s.ikonaPlik) {
        if (ikonyLokalne[s.ikonaPlik]) continue;
        if (await plikIkonyIstnieje(s.ikonaPlik)) continue;
      }
      try {
        const dataUrl = await skalujDoDataUrl(adresIkony(s.url));
        if (globus && dataUrl === globus) { bezIkony.add(s.url); continue; }
        const nazwa = await zapiszIkoneNaDysk(s.url, dataUrl);
        if (nazwa) { s.ikonaPlik = nazwa; zmiany++; }
      } catch (e) { bezIkony.add(s.url); }
    }

    if (zmiany) {
      await zapisz();
      rysuj();
      pokazPasek('Dograno ' + zmiany + ' ikon do dane\\ikony (Chrome poznal je po odwiedzeniu stron).');
    }
  } finally {
    dogrywanieTrwa = false;
  }
}

// =====================================================================
//  UZUPELNIANIE IKON - jedna droga zamiast trzech pozycji w menu
// =====================================================================
//  Kolejnosc: najpierw magazyn Chrome (za darmo, bez sieci), potem sama
//  witryna. Gdy cicho=true, nie zawracamy glowy zadnym pytaniem - jesli
//  brak zgody na dostep do witryn, po prostu robimy tyle, ile sie da.
// cicho          - nie prosimy o zgode i nie meldujemy niczego
// bezNarzekania  - prosimy o zgode, ale milczymy gdy nic nie znaleziono
async function uzupelnijIkony(skroty, cicho, bezNarzekania) {
  if (!(await zapewnijFolder())) {
    if (!cicho && !bezNarzekania) pokazPasek('Bez podlaczonego folderu nie zapisze ikon jako plikow.');
    return 0;
  }

  const globus = await pobierzWzorzecGlobusa();
  let zPamieci = 0;
  const nadalBrak = [];

  // 1) magazyn faviconow Chrome
  for (const s of skroty) {
    if (s.ikona || s.ikonaPlik) continue;
    if (/^(chrome|edge|brave|about|file)/i.test(s.url)) continue;
    try {
      const d = await skalujDoDataUrl(adresIkony(s.url));
      if (globus && d === globus) { nadalBrak.push(s); continue; }
      const nazwa = await zapiszIkoneNaDysk(s.url, d);
      if (nazwa) { s.ikonaPlik = nazwa; zPamieci++; } else { nadalBrak.push(s); }
    } catch (e) { nadalBrak.push(s); }
  }

  // 2) reszta - prosto z witryn, o ile mamy (albo dostaniemy) zgode
  let zSieci = 0;
  if (nadalBrak.length) {
    let maZgode = false;
    try { maZgode = await chrome.permissions.contains({ origins: ['https://*/*'] }); } catch (e) { }

    if (!maZgode && !cicho) {
      try { maZgode = await chrome.permissions.request({ origins: ['https://*/*'] }); } catch (e) { }
      if (!maZgode) ustawienia.niePytajOIkony = true;
      await zapiszUstawienia();
    }

    if (maZgode) {
      for (let i = 0; i < nadalBrak.length; i++) {
        const s = nadalBrak[i];
        pokazPasek('Pobieram brakujace ikony: ' + (i + 1) + ' z ' + nadalBrak.length + '...');
        const w = await pobierzIkoneJakoPlik(s.url);
        if (!w) { bezIkony.add(s.url); continue; }
        const nazwa = await zapiszIkoneNaDysk(s.url, w.dataUrl);
        if (nazwa) { s.ikonaPlik = nazwa; zSieci++; } else { s.ikona = w.dataUrl; zSieci++; }
      }
    }
  }

  if (zPamieci || zSieci) {
    await zapisz();
    rysuj();
    pokazPasek('Uzupelniono ikony: ' + zPamieci + ' z pamieci przegladarki, ' +
               zSieci + ' pobranych z witryn.');
  } else if (!cicho && !bezNarzekania) {
    pokazPasek('Nie udalo sie znalezc ikon dla tych stron - mozesz wstawic wlasny obrazek przy edycji kafelka.');
  }
  return zPamieci + zSieci;
}

async function osadzIkony(skroty, etykieta) {
  if (!(await zapewnijFolder())) {
    pokazPasek('Przerwane - bez podlaczonego folderu ikony nie moga stac sie plikami.');
    return;
  }
  const globus = await pobierzWzorzecGlobusa();
  let doPlikow = 0, doDanych = 0, pominietych = 0;
  for (let i = 0; i < skroty.length; i++) {
    const s = skroty[i];
    pokazPasek('Osadzam ikony (' + etykieta + '): ' + (i + 1) + ' z ' + skroty.length + '...');
    if (s.ikonaPlik || s.ikona) continue;   // juz ma wlasna
    try {
      const dataUrl = await skalujDoDataUrl(adresIkony(s.url));
      if (globus && dataUrl === globus) { pominietych++; continue; }
      const gdzie = await ustawIkone(s, dataUrl);
      if (gdzie === 'plik') doPlikow++; else doDanych++;
    } catch (e) { pominietych++; }
  }
  await zapisz();
  rysuj();
  pokazPasek('Ikony: ' + doPlikow + ' zapisano jako pliki w podfolderze "ikony", ' +
             doDanych + ' wpisano do danych, pominieto ' + pominietych +
             ' (Chrome nie zna ich ikon).');
}

// ---------------------------------------------------------- okno skrotu
// undefined = bez zmian, null = usun wlasna, string = nowa ikona
let tymczasowaIkona;

function pokazPodgladIkony(zrodlo) {
  const p = $('podgladIkony');
  if (p) p.src = zrodlo || adresIkony('https://przyklad.invalid');
}

function otworzOkno(indeks) {
  edytowany = (typeof indeks === 'number') ? indeks : null;
  const s = (edytowany !== null) ? grupa().skroty[edytowany] : null;
  $('oknoTytul').textContent = s ? 'Edytuj skrot' : 'Nowy skrot';
  $('poleUrl').value = s ? s.url : '';
  $('poleNazwa').value = s ? s.nazwa : '';
  tymczasowaIkona = undefined;
  pokazPodgladIkony(s ? (s.ikona || adresIkony(s.url)) : null);
  $('okno').showModal();
  $('poleUrl').focus();
}

// Anulowanie zamyka okno bez walidacji i bez zapisu.
$('oknoAnuluj').addEventListener('click', () => $('okno').close('anuluj'));
$('koszZamknij').addEventListener('click', () => $('oknoKosza').close());

// --- wlasny obrazek: z pliku albo ze schowka (np. z narzedzia wycinania) ---
$('btnObrazek').addEventListener('click', () => $('plikObrazek').click());

$('plikObrazek').addEventListener('change', async (e) => {
  const plik = e.target.files[0];
  if (!plik) return;
  try {
    tymczasowaIkona = await skalujDoDataUrl(await plikNaDataUrl(plik));
    pokazPodgladIkony(tymczasowaIkona);
  } catch (err) {
    alert('Nie udalo sie wczytac obrazka: ' + err.message);
  }
  e.target.value = '';
});

$('btnBezIkony').addEventListener('click', () => {
  tymczasowaIkona = null;
  pokazPodgladIkony($('poleUrl').value ? adresIkony(pelnyUrl($('poleUrl').value)) : null);
});

// Ctrl+V w otwartym oknie - wklejenie zrzutu z narzedzia wycinania
$('okno').addEventListener('paste', async (e) => {
  const rzeczy = (e.clipboardData || window.clipboardData).items;
  for (const r of rzeczy) {
    if (r.type && r.type.startsWith('image/')) {
      e.preventDefault();
      try {
        tymczasowaIkona = await skalujDoDataUrl(await plikNaDataUrl(r.getAsFile()));
        pokazPodgladIkony(tymczasowaIkona);
        pokazPasek('Wklejono obrazek jako ikone.');
      } catch (err) { alert('Nie udalo sie wkleic obrazka: ' + err.message); }
      return;
    }
  }
});

$('okno').addEventListener('close', async () => {
  if ($('okno').returnValue !== 'zapisz') return;
  const surowy = $('poleUrl').value.trim();
  if (!surowy) return;
  const url = pelnyUrl(surowy);
  const nazwa = $('poleNazwa').value.trim() || nazwaZUrl(url);

  const stary = (edytowany !== null) ? grupa().skroty[edytowany] : null;
  const wpis = { url, nazwa };
  const adresZmieniony = !!(stary && stary.url !== url);

  // undefined = zostaw jak bylo, null = usun wlasna, string = nowa
  if (tymczasowaIkona === undefined && stary) {
    // Ikona automatyczna jest zwiazana z ADRESEM (z niego bierze sie nazwa
    // pliku), wiec po zmianie adresu musi zniknac i wygenerowac sie na nowo.
    // Wlasny obrazek uzytkownika zostaje - to jego swiadomy wybor.
    if (!adresZmieniony || stary.ikonaWlasna) {
      if (stary.ikonaPlik) wpis.ikonaPlik = stary.ikonaPlik;
      if (stary.ikona) wpis.ikona = stary.ikona;
      if (stary.ikonaWlasna) wpis.ikonaWlasna = true;
    }
  }

  wpis.dodano = (stary && stary.dodano) ? stary.dodano : Date.now();

  if (edytowany !== null) grupa().skroty[edytowany] = wpis;
  else grupa().skroty.push(wpis);

  if (typeof tymczasowaIkona === 'string') {
    const gdzie = await ustawIkone(wpis, tymczasowaIkona);
    wpis.ikonaWlasna = true;          // obrazek wskazany przez uzytkownika
    pokazPasek(gdzie === 'plik'
      ? 'Ikone zapisano jako plik w dane\\ikony - powedruje z folderem.'
      : 'Ikone zapisano w danych. Podlacz folder rozszerzenia, zeby trafiala do pliku.');
  } else if (tymczasowaIkona === null) {
    delete wpis.ikona;
    delete wpis.ikonaPlik;
    delete wpis.ikonaWlasna;
  }

  await zapisz();
  rysuj();

  // Po zmianie adresu od razu probujemy zdobyc ikone dla nowej strony,
  // zeby kafelek nie zostawal z litera do najblizszej wizyty.
  if (adresZmieniony && !wpis.ikonaPlik && !wpis.ikona) {
    bezIkony.delete(url);
    await dogrywajIkony();
  }

  // zmiana adresu albo usuniecie wlasnej ikony moze osierocic stary plik
  if (stary && (adresZmieniony || tymczasowaIkona !== undefined)) {
    const ile = await usunOsieroconeIkony();
    if (ile) pokazPasek('Uporzadkowano: usunieto ' + ile + ' niepotrzebny plik ikony.');
  }
});

// ------------------------------------------------------------- przyciski
$('btnDodaj').addEventListener('click', () => otworzOkno(null));

$('btnGrupa').addEventListener('click', async () => {
  const nazwa = prompt('Nazwa nowej grupy:');
  if (!nazwa || !nazwa.trim()) return;
  dane.grupy.push({ nazwa: nazwa.trim(), skroty: [] });
  dane.aktywna = dane.grupy.length - 1;
  await zapisz();
  rysuj();
});

$('szukaj').addEventListener('input', rysujSkroty);

$('rozmiar').addEventListener('change', async () => {
  wyglad().rozmiar = parseInt($('rozmiar').value, 10) || 148;
  zastosujRozmiar();
  wygladWuzyciu = { tlo: wyglad().tlo, rozmiar: wyglad().rozmiar };
  await zapisz();          // wyglad jest czescia danych, wiec idzie dalej
});

// Klikniecie we wskaznik: podlacza folder albo odnawia zgode i od razu
// przeprowadza pelna wymiane. Jedno klikniecie, bez zadnych pytan po drodze.
$('stanSync').addEventListener('click', async () => {
  if (!(await zapewnijFolder())) { await odswiezStanSync(); return; }
  await zapewnijNazweUrzadzenia();
  await synchronizujZPlikiem(true);
  await odswiezStanSync();
  odswiezMenu();
  rysuj();
  pokazPasek('Synchronizacja wznowiona.');
});

$('tlo').addEventListener('change', async () => {
  wyglad().tlo = $('tlo').value;
  zastosujTlo();
  wygladWuzyciu = { tlo: wyglad().tlo, rozmiar: wyglad().rozmiar };
  await zapisz();
});

function schowajMenu() { $('menu').hidden = true; $('menuZaaw').hidden = true; }

$('btnMenu').addEventListener('click', (e) => {
  e.stopPropagation();
  const bylo = $('menu').hidden;
  schowajMenu();
  $('menu').hidden = !bylo;
});
document.addEventListener('click', schowajMenu);
$('menu').addEventListener('click', (e) => e.stopPropagation());
$('menuZaaw').addEventListener('click', (e) => e.stopPropagation());

// --------------------------------------------------------------- menu
const obsluzMenu = async (e) => {
  const akcja = e.target.dataset ? e.target.dataset.akcja : null;
  if (!akcja) return;

  // "Zaawansowane..." tylko przelacza drugie menu, nie zamyka wszystkiego
  if (akcja === 'zaawansowane') {
    $('menu').hidden = true;
    $('menuZaaw').hidden = false;
    return;
  }
  schowajMenu();

  if (akcja === 'pinProfilu') { await ustawPinProfilu(); return; }
  if (akcja === 'usunProfil') { await usunProfil(); return; }

  if (akcja === 'eksport') {
    // 1. Folder podlaczony - piszemy tam, gdzie leza wszystkie dane programu.
    if (uchwytFolderu && (await maPrawo(uchwytFolderu, true))) {
      await zapiszDoPliku();
      pokazPasek('Zapisano w ' + uchwytFolderu.name + '\\dane\\kopia.json - obok programu, nie w pobranych.');
      return;
    }
    // 2. Bez folderu - pytamy, gdzie zapisac.
    if (window.showSaveFilePicker) {
      try {
        const u = await window.showSaveFilePicker({
          suggestedName: 'kopia.json',
          types: [{ description: 'Kopia Szybkiego Dostepu', accept: { 'application/json': ['.json'] } }]
        });
        const s = await u.createWritable();
        await s.write(JSON.stringify(dane, null, 2));
        await s.close();
        pokazPasek('Zapisano do wskazanego pliku.');
        return;
      } catch (e) {
        if (e && e.name === 'AbortError') return;
      }
    }
    // 3. Ostatecznosc - zwykle pobranie.
    const blob = new Blob([JSON.stringify(dane, null, 2)], { type: 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'kopia.json';
    a.click();
    URL.revokeObjectURL(a.href);
    pokazPasek('Zapisano do folderu pobierania. Podlacz folder rozszerzenia, zeby kopia trafiala od razu obok programu.');
  }

  if (akcja === 'import') $('plikImport').click();

  if (akcja === 'uporzadkuj') {
    await uporzadkujFolder();
    return;
  }

  if (akcja === 'nazwaKomputera') {
    const teraz = nazwaUrzadzenia() || '';
    const nowa = prompt('Nazwa tego komputera (uzywana w nazwach kopii zapasowych):', teraz || 'Laptop-1');
    if (nowa === null) return;
    ustawienia.urzadzenie = nowa.trim();
    await zapiszUstawienia();
    odswiezMenu();
    if (ustawienia.urzadzenie) {
      const ok = await zapiszKopieUrzadzenia();
      pokazPasek(ok
        ? ('Kopia zapisana jako ' + bezpiecznaNazwa(ustawienia.urzadzenie) + '_...json w dane\\kopie')
        : 'Nazwa zapisana. Kopia powstanie, gdy folder bedzie podlaczony.');
    }
    return;
  }

  if (akcja === 'kopiaTeraz') {
    if (!nazwaUrzadzenia()) { pokazPasek('Najpierw nadaj nazwe temu komputerowi.'); return; }
    const ok = await zapiszKopieUrzadzenia();
    pokazPasek(ok ? 'Kopia zapasowa zapisana w dane\\kopie.' : 'Nie udalo sie zapisac kopii - sprawdz folder.');
    return;
  }

  if (akcja === 'cofnijDostep') {
    try {
      const ma = await chrome.permissions.contains({ origins: ['https://*/*'] });
      if (!ma) { pokazPasek('Rozszerzenie nie ma teraz dostepu do witryn.'); return; }
      await chrome.permissions.remove({ origins: ['https://*/*'] });
      pokazPasek('Cofnieto dostep do witryn. Ikony juz zapisane jako pliki zostaja.');
    } catch (e) { pokazPasek('Nie udalo sie cofnac: ' + (e.message || e)); }
    return;
  }

  if (akcja === 'ikonyKasuj') {
    let ile = 0;
    for (const g of dane.grupy) for (const s of g.skroty) {
      if (s.ikona) { delete s.ikona; ile++; }
      if (s.ikonaPlik) { delete s.ikonaPlik; ile++; }
    }
    await zapisz();
    rysuj();
    const plikow = await usunOsieroconeIkony();
    pokazPasek('Usunieto ' + ile + ' przypisan i ' + plikow + ' plikow PNG z dane\\ikony.');
  }

  if (akcja === 'sync') {
    let konto = await kontoPrzegladarki();
    if (!syncWlaczony(konto)) {
      // Najpierw sprawdzamy, CZY jest co synchronizowac. Bez konta w
      // przegladarce (Brave, Chrome bez logowania) chrome.storage.sync tylko
      // udaje synchronizacje - zapisuje lokalnie i nic nie wysyla.
      if (!konto.znane) {
        const chce = confirm(
          'Zeby uczciwie pokazac, co dzieje sie z Twoimi danymi, program musi\n' +
          'wiedziec, na jakim koncie pracuje ta przegladarka.\n\n' +
          'Za chwile przegladarka zapyta o dostep do nazwy konta. Nic poza\n' +
          'adresem e-mail konta nie jest odczytywane i nic nigdzie nie wychodzi.\n\n' +
          'Bez tej zgody synchronizacje mozna wlaczyc, ale program nie bedzie\n' +
          'umial powiedziec, czy naprawde dziala.');
        if (chce) {
          await poprosOKonto();
          konto = await kontoPrzegladarki(true);
        }
      }
      if (konto.znane && !konto.zalogowany) {
        alert('Ta przegladarka nie ma konta Google.\n\n' +
              'Synchronizacja przez konto Google nie ma tu jak dzialac -\n' +
              'dane zostaja na tym komputerze. Do wymiany miedzy komputerami\n' +
              'uzyj wspolnego folderu (dane\\kopia.json).');
        return;
      }
      const zgoda = confirm(
        'Wlaczyc synchronizacje przez konto Google?\n\n' +
        (konto.email ? ('Konto: ' + konto.email + '\n\n') : '') +
        'Skroty beda przesylane na serwery Google i pojawia sie automatycznie\n' +
        'w kazdym Chrome, w ktorym jestes zalogowany tym samym kontem.\n\n' +
        'Ustawienie dotyczy TYLKO tego konta - inne konta maja wlasne.\n' +
        'Limit: ok. 100 KB, czyli okolo tysiaca skrotow.');
      if (!zgoda) return;
      ustawSyncDlaKonta(konto, true);
      await zapiszUstawienia();
      try {
        const rozmiar = await zapiszDoSync(dane);
        pokazPasek('Synchronizacja wlaczona' + (konto.email ? (' dla ' + konto.email) : '') +
                   ' - wyslano ' + Math.round(rozmiar / 1024) + ' KB na konto.');
      } catch (e) {
        ustawSyncDlaKonta(konto, false);
        await zapiszUstawienia();
        pokazPasek('Nie udalo sie wlaczyc synchronizacji: ' + (e.message || e));
      }
    } else {
      ustawSyncDlaKonta(konto, false);
      await zapiszUstawienia();
      try {
        const w = await chrome.storage.sync.get(null);
        const nasze = Object.keys(w).filter((k) => k === KLUCZ || /^sd_/.test(k));
        if (nasze.length) await chrome.storage.sync.remove(nasze);
      } catch (e) { /* nic */ }
      pokazPasek('Synchronizacja wylaczona, dane usuniete z konta Google.');
    }
    odswiezMenu();
  }

  if (akcja === 'pytanieOPlik') {
    ustawienia.pytajOPlik = !(ustawienia.pytajOPlik !== false);
    await zapiszUstawienia();
    odswiezMenu();
    pokazPasek(ustawienia.pytajOPlik
      ? 'Bede pytal o zgode raz na uruchomienie przegladarki.'
      : 'Nie bede pytal - synchronizacja tylko przez "Synchronizuj z plikiem TERAZ".');
    return;
  }

  if (akcja === 'folderRozszerzenia') {
    if (uchwytFolderu) {
      if (!confirm('Odlaczyc folder rozszerzenia?\n\nIkony przestana sie zapisywac jako pliki PNG.')) return;
      const db = await idb();
      await new Promise((ok) => {
        const t = db.transaction('uchwyty', 'readwrite');
        t.objectStore('uchwyty').delete('folder');
        t.oncomplete = () => ok(true);
        t.onerror = () => ok(false);
      });
      uchwytFolderu = null;
      odswiezMenu();
      pokazPasek('Folder odlaczony.');
      return;
    }
    if (!window.showDirectoryPicker) { alert('Ta przegladarka nie obsluguje wyboru folderu.'); return; }
    try {
      const uchwyt = await window.showDirectoryPicker({ mode: 'readwrite' });
      if (!(await maPrawo(uchwyt, true))) { pokazPasek('Brak zgody na zapis w folderze.'); return; }
      uchwytFolderu = uchwyt;
      await zapamietajFolder(uchwyt);
      await zapamietajZgodeSesji();
      odswiezMenu();
      pokazPasek('Podlaczono folder "' + uchwyt.name + '" - porzadkuje strukture...');
      await uporzadkujFolder();      // tworzy dane\ i dane\ikony\, przenosi ikony
    } catch (e) {
      if (e && e.name !== 'AbortError') pokazPasek('Nie udalo sie podlaczyc folderu: ' + (e.message || e));
    }
    return;
  }

  if (akcja === 'synchronizuj') {
    if (!uchwytZgody()) { pokazPasek('Najpierw podlacz folder rozszerzenia albo wspolny plik.'); return; }
    if (!(await maPrawo(uchwytZgody(), true))) { pokazPasek('Brak zgody na dostep.'); return; }
    const co = await synchronizujZPlikiem(false);
    if (co === 'bezZmian') pokazPasek('Obie wersje sa identyczne - nie bylo czego wymieniac.');
    return;
  }

  if (akcja === 'plikWspolny') {
    if (uchwytPliku) {
      const wybor = confirm(
        'Wspolny plik: ' + (uchwytPliku.name || 'kopia.json') + '\n\n' +
        'OK     = ODLACZ (skroty zostaja, wymiana sie konczy)\n' +
        'Anuluj = zostaw polaczenie');
      if (!wybor) return;
      await zapomnijUchwyt();
      uchwytPliku = null;
      odswiezMenu();
      pokazPasek('Wspolny plik odlaczony.');
      return;
    }
    if (!window.showSaveFilePicker) {
      alert('Ta wersja przegladarki nie obsluguje zapisu do wskazanego pliku.');
      return;
    }
    try {
      const uchwyt = await window.showSaveFilePicker({
        suggestedName: 'kopia.json',
        types: [{ description: 'Kopia Szybkiego Dostepu', accept: { 'application/json': ['.json'] } }]
      });
      if (!(await maPrawo(uchwyt, true))) { pokazPasek('Brak zgody na zapis do pliku.'); return; }

      // jesli plik juz istnieje i jest nowszy - bierzemy jego zawartosc
      uchwytPliku = uchwyt;
      const zPliku = await wczytajZPliku();
      if (zPliku && (zPliku.zapisano || 0) > (dane.zapisano || 0)) {
        if (confirm('Wskazany plik zawiera NOWSZA wersje skrotow.\n\nOK = wczytaj z pliku\nAnuluj = nadpisz plik tym, co masz tutaj')) {
          dane = zPliku;
          await chrome.storage.local.set({ [kluczDanych()]: dane });
          rysuj();
        }
      }
      await zapamietajUchwyt(uchwyt);
      await zapiszDoPliku();
      odswiezMenu();
      pokazPasek('Polaczono ze wspolnym plikiem. Wskaz ten sam plik w drugim profilu.');
    } catch (e) {
      if (e && e.name !== 'AbortError') pokazPasek('Nie udalo sie polaczyc z plikiem: ' + (e.message || e));
    }
  }

  if (akcja === 'kosz') {
    const lista = dane.kosz || [];
    if (!lista.length) { pokazPasek('Kosz jest pusty.'); return; }

    const cel = $('listaKosza');
    cel.textContent = '';
    lista.forEach((k, i) => {
      const w = document.createElement('div');
      w.className = 'wierszKosza';

      const opis = document.createElement('div');
      opis.className = 'opisKosza';
      const n = document.createElement('div');
      n.className = 'nazwa';
      n.textContent = k.nazwa || k.url;
      const m = document.createElement('div');
      m.className = 'adres';
      m.textContent = (k.grupa || '?') + '  |  ' + new Date(k.usunieto || 0).toLocaleString();
      opis.appendChild(n); opis.appendChild(m);
      w.appendChild(opis);

      const b = document.createElement('button');
      b.textContent = 'Przywroc';
      b.addEventListener('click', async () => {
        await przywrocZKosza(i);
        $('oknoKosza').close();
      });
      w.appendChild(b);
      cel.appendChild(w);
    });
    $('oknoKosza').showModal();
    return;
  }

  if (akcja === 'oproznijKosz') {
    if (!dane.kosz || !dane.kosz.length) { pokazPasek('Kosz jest juz pusty.'); return; }
    if (!confirm('Oproznic kosz? ' + dane.kosz.length + ' pozycji zniknie na dobre.')) return;
    dane.kosz = [];
    await zapisz();
    pokazPasek('Kosz oprozniony.');
    return;
  }

  if (akcja === 'miniatury') {
    wyglad().miniatury = (wyglad().miniatury === false);
    wygladWuzyciu = { tlo: wyglad().tlo, rozmiar: wyglad().rozmiar };
    await zapisz();
    rysuj();
    odswiezMenu();
    return;
  }

  if (akcja === 'ikonyUzupelnij') {
    const brakujace = dane.grupy
      .reduce((t, g) => t.concat(g.skroty), [])
      .filter((s) => !s.ikona && !s.ikonaPlik);
    if (!brakujace.length) { pokazPasek('Wszystkie skroty maja juz ikony.'); return; }
    await uzupelnijIkony(brakujace, false);
    return;
  }

  if (akcja === 'pomoc') {
    let zgoda = '?';
    try { zgoda = uchwytFolderu ? await uchwytFolderu.queryPermission({ mode: 'readwrite' }) : 'brak uchwytu'; }
    catch (e) { zgoda = 'blad: ' + e.message; }

    mostDziala = null;                 // wymuszamy swiezy test
    const mostOk = await sprawdzMost();

    alert(
      'DIAGNOSTYKA TEJ PRZEGLADARKI\n' +
      '  identyfikator       : ' + chrome.runtime.id + '\n' +
      '  most (posrednik)    : ' + (mostOk ? 'DZIALA' : 'NIE DZIALA') + '\n' +
      '  blad mostu          : ' + (mostBlad || '(brak)') + '\n' +
      '  showDirectoryPicker : ' + (window.showDirectoryPicker ? 'DOSTEPNY' : 'BRAK (przegladarka nie pozwala wybrac folderu)') + '\n' +
      '  showSaveFilePicker  : ' + (window.showSaveFilePicker ? 'dostepny' : 'brak') + '\n' +
      '  uchwyt folderu      : ' + (uchwytFolderu ? ('jest (' + uchwytFolderu.name + ')') : 'brak') + '\n' +
      '  zgoda na zapis      : ' + zgoda + '\n' +
      '  nazwa tego komputera: ' + (nazwaUrzadzenia() || '(nie ustawiono)') + '\n\n' +
      'SZYBKI DOSTEP ' + chrome.runtime.getManifest().version + '\n\n' +
      'CO DZIEJE SIE SAMO\n' +
      '  - zapis po kazdej zmianie (bez przycisku "zapisz")\n' +
      '  - ikony: z magazynu Chrome po odwiedzeniu strony, z sieci gdy brak\n' +
      '  - usuwanie plikow ikon, do ktorych nic juz nie prowadzi\n' +
      '  - wymiana danych przez plik w folderze, co 8 sekund i przy starcie\n\n' +
      'CO WYMAGA KLIKNIECIA (wymog przegladarki, nie moj wybor)\n' +
      '  - zgoda na zapis w folderze: raz na uruchomienie przegladarki\n' +
      '  - zgoda na dostep do witryn: raz, tylko do pobierania ikon\n\n' +
      'PRZENIESIENIE NA INNY KOMPUTER\n' +
      '  Skopiuj caly folder rozszerzenia. Tam: chrome://extensions >\n' +
      '  Tryb dewelopera > Zaladuj rozpakowane. Skroty i ikony beda od razu.\n\n' +
      'DWIE PRZEGLADARKI NA JEDNYM KOMPUTERZE\n' +
      '  W obu wskaz ten sam folder - wymieniaja sie przez dane\\kopia.json.\n' +
      '  Konto Google laczy tylko Chrome z Chrome (Edge ma wlasne konto).\n\n' +
      'UPRAWNIENIA\n' +
      '  storage, favicon, contextMenus, activeTab.\n' +
      '  Dostep do witryn jest OPCJONALNY i tylko na czas pobierania ikon.\n' +
      '  Rozszerzenie nie czyta zadnej strony i nie wysyla nigdzie danych.\n\n' +
      'Synchronizacja z kontem Google: ' + (ustawienia.sync ? 'WLACZONA' : 'wylaczona'));
    return;
  }

  if (akcja === 'zmienGrupe') {
    const nowa = prompt('Nowa nazwa grupy:', grupa().nazwa);
    if (nowa && nowa.trim()) {
      grupa().nazwa = nowa.trim();
      await zapisz();
      rysuj();
    }
  }

  if (akcja === 'usunGrupe') {
    if (dane.grupy.length === 1) { alert('Nie mozna usunac jedynej grupy.'); return; }
    if (!confirm('Usunac grupe "' + grupa().nazwa + '" razem ze skrotami?')) return;
    // wszystko do kosza + nagrobki, inaczej wrocilyby przy scalaniu
    for (const s of grupa().skroty) { doKosza(grupa().nazwa, s); dodajNagrobek(grupa().nazwa, s.url); }
    dane.grupy.splice(dane.aktywna, 1);
    dane.aktywna = 0;
    await zapisz();
    rysuj();
    const ile = await usunOsieroconeIkony();
    if (ile) pokazPasek('Usunieto grupe oraz ' + ile + ' niepotrzebnych plikow ikon.');
  }

};

$('menu').addEventListener('click', obsluzMenu);
$('menuZaaw').addEventListener('click', obsluzMenu);

function odswiezMenu() {
  const pin = $('pozycjaPin');
  if (pin) pin.textContent = 'PIN profilu "' + profil + '": ' + (maPin(profil) ? 'USTAWIONY' : 'brak');
  const up = $('pozycjaUsunProfil');
  if (up) {
    up.hidden = (profil === PROFIL_DOMYSLNY);
    up.textContent = 'Usun profil "' + profil + '"';
  }

  const p = $('pozycjaSync');
  if (p) {
    // Napis bierzemy z opisSynchronizacji, bo tylko ono wie, czy przegladarka
    // ma konto. Samo ustawienia.sync klamalo w Brave i w Chrome bez logowania.
    // Zajetosc konta dopisujemy WYLACZNIE gdy synchronizacja naprawde dziala -
    // inaczej pod napisem "brak konta" pojawialby sie procent ze 100 KB.
    opisSynchronizacji().then(async (o) => {
      p.textContent = o.tekst;
      p.title = '';
      if (o.stan !== 'wlaczona' && o.stan !== 'wlaczona-nieznane-konto') return;
      const s = await stanKontaGoogle();
      if (!s) return;
      p.textContent = o.tekst + ' (' + s.procent + '% ze 100 KB)';
      p.title = 'Zajete ' + (Math.round(s.uzyte / 102.4) / 10) + ' KB ze 100 KB.\n' +
                'Skrotow: ' + s.skroty +
                (s.zapas ? (', zmiesci sie jeszcze okolo ' + s.zapas) : '') + '.\n\n' +
                'Dane sa pakowane przed wyslaniem, a kosz i stare nagrobki nie jada wcale.\n' +
                'Dzieki temu miejsca starcza na ponad dwa razy wiecej skrotow.';
    }).catch(() => { /* brak dostepu do konta - zostaje sam napis */ });
  }
  const f = $('pozycjaPlik');
  if (f) f.textContent = 'Wspolny plik na dysku: ' + (uchwytPliku ? 'POLACZONY' : 'niepolaczony');
  // Gdy dziala most, folderu NIE trzeba podlaczac recznie - most zapisuje
  // pliki sam. Pokazywanie wtedy "niepodlaczony" zachecalo do klikania
  // w cos, czego nie trzeba robic.
  const fo = $('pozycjaFolder');
  if (fo) {
    if (mostDziala) {
      fo.textContent = 'Folder rozszerzenia: obsluguje most (nic nie trzeba robic)';
    } else {
      fo.textContent = 'Folder rozszerzenia: ' + (uchwytFolderu ? 'PODLACZONY' : 'niepodlaczony');
    }
  }

  // "Cofnij dostep do witryn" pokazujemy tylko wtedy, gdy jest co cofac
  const dost = $('pozycjaDostep');
  if (dost) {
    chrome.permissions.contains({ origins: ['https://*/*'] })
      .then((ma) => { dost.hidden = !ma; })
      .catch(() => { dost.hidden = true; });
  }

  const kz = $('pozycjaKosz');
  if (kz) kz.textContent = 'Kosz... (' + ((dane.kosz && dane.kosz.length) || 0) + ')';
  const mi = $('pozycjaMiniatury');
  if (mi) mi.textContent = 'Miniatury stron: ' + (wyglad().miniatury === false ? 'wylaczone' : 'WLACZONE');

  const u = $('pozycjaUrzadzenie');
  if (u) u.textContent = 'Nazwa tego komputera: ' + (nazwaUrzadzenia() || '(nie ustawiono)');

  const q = $('pozycjaPytanie');
  if (q) q.textContent = 'Pytanie o zgode do pliku: ' +
    (ustawienia.pytajOPlik === false ? 'WYLACZONE (tylko recznie)' : 'raz na uruchomienie');
}

// =====================================================================
//  IMPORT - rozumie wlasny format, obce formaty JSON (np. Group Speed
//  Dial) oraz zakladki wyeksportowane do HTML.
// =====================================================================
const POLA_URL   = ['url', 'href', 'link', 'loc', 'address', 'uri', 'site'];
const POLA_NAZWY = ['title', 'name', 'label', 'text', 'caption', 'displayName'];
const POLA_DZIECI = ['children', 'items', 'dials', 'tiles', 'links', 'bookmarks',
                     'groups', 'folders', 'entries', 'list', 'nodes', 'data'];

function wygladaNaUrl(v) {
  // Przepuszczamy kazdy schemat z '//' - takze chrome://, edge://, file://
  // Odpadaja przez to data: i javascript:, ktore '//' nie maja.
  return typeof v === 'string' && /^([a-z][a-z0-9+.-]*:\/\/|www\.)/i.test(v.trim());
}

// Przechodzi dowolna strukture JSON i zbiera pary adres + nazwa,
// zapamietujac nazwe folderu/grupy, w ktorej element sie znajduje.
function zbierzZJson(wezel, grupaNazwa, wynik) {
  if (!wezel || typeof wezel !== 'object') return;

  if (Array.isArray(wezel)) {
    for (const el of wezel) zbierzZJson(el, grupaNazwa, wynik);
    return;
  }

  // czy ten obiekt sam jest skrotem?
  let url = null;
  for (const p of POLA_URL) {
    if (wygladaNaUrl(wezel[p])) { url = wezel[p].trim(); break; }
  }
  if (url) {
    let nazwa = '';
    for (const p of POLA_NAZWY) {
      if (typeof wezel[p] === 'string' && wezel[p].trim()) { nazwa = wezel[p].trim(); break; }
    }
    if (!url.startsWith('http')) url = 'https://' + url.replace(/^www\./, 'www.');
    wynik.push({ grupa: grupaNazwa, url, nazwa: nazwa || nazwaZUrl(url) });
  }

  // czy ma dzieci? jesli tak, jego nazwa staje sie nazwa grupy
  let wlasnaNazwa = grupaNazwa;
  if (!url) {
    for (const p of POLA_NAZWY) {
      if (typeof wezel[p] === 'string' && wezel[p].trim()) { wlasnaNazwa = wezel[p].trim(); break; }
    }
  }
  for (const p of POLA_DZIECI) {
    if (wezel[p] && typeof wezel[p] === 'object') zbierzZJson(wezel[p], wlasnaNazwa, wynik);
  }
  // przejdz tez po nieznanych kluczach obiektowych
  for (const [k, v] of Object.entries(wezel)) {
    if (POLA_DZIECI.includes(k) || POLA_URL.includes(k) || POLA_NAZWY.includes(k)) continue;
    if (v && typeof v === 'object') zbierzZJson(v, wlasnaNazwa, wynik);
  }
}

// Zakladki wyeksportowane do HTML (format Netscape - uzywa go Chrome i Firefox)
function zbierzZHtml(tekst) {
  const wynik = [];
  const dok = new DOMParser().parseFromString(tekst, 'text/html');
  for (const a of dok.querySelectorAll('a[href]')) {
    const href = a.getAttribute('href');
    if (!wygladaNaUrl(href)) continue;
    // nazwa folderu = najblizszy nagolwek H3 powyzej
    let folder = 'Zaimportowane';
    let el = a.closest('dl') ? a.closest('dl').previousElementSibling : null;
    if (el && el.tagName === 'H3') folder = el.textContent.trim();
    wynik.push({ grupa: folder, url: href.trim(), nazwa: (a.textContent || '').trim() || nazwaZUrl(href) });
  }
  return wynik;
}

function zbudujGrupy(plaskie) {
  const mapa = new Map();
  for (const s of plaskie) {
    const g = s.grupa && s.grupa.trim() ? s.grupa.trim() : 'Zaimportowane';
    if (!mapa.has(g)) mapa.set(g, []);
    // pomijamy duplikaty w obrebie grupy
    if (!mapa.get(g).some((x) => x.url === s.url)) mapa.get(g).push({ url: s.url, nazwa: s.nazwa });
  }
  return Array.from(mapa, ([nazwa, skroty]) => ({ nazwa, skroty }));
}

$('plikImport').addEventListener('change', async (e) => {
  const plik = e.target.files[0];
  if (!plik) return;
  try {
    const tekst = await plik.text();
    let noweGrupy = null;
    let zrodlo = '';

    // 1. wlasny format
    try {
      const j = JSON.parse(tekst);
      if (poprawne(j) && j.grupy[0] && Array.isArray(j.grupy[0].skroty)) {
        noweGrupy = j.grupy;
        zrodlo = 'kopia Szybkiego Dostepu';
      } else if (Array.isArray(j.groups) && j.groups.some((g) => Array.isArray(g.dials))) {
        // Format Group Speed Dial: groups[].name + groups[].dials[].{name,url}
        // Bierzemy go osobno, zeby nie zaciagac smieci z options/metadata/___thumbnails.
        noweGrupy = j.groups
          .filter((g) => Array.isArray(g.dials) && !g.archived)
          .map((g, nr) => ({
            nazwa: (g.name && String(g.name).trim()) || ('Grupa ' + (nr + 1)),
            skroty: g.dials
              .filter((d) => d && wygladaNaUrl(d.url))
              .map((d) => ({
                url: d.url.trim(),
                nazwa: (d.name && String(d.name).trim()) || nazwaZUrl(d.url)
              }))
          }))
          .filter((g) => g.skroty.length > 0);
        zrodlo = 'Group Speed Dial';
      } else {
        // 2. obcy JSON
        const plaskie = [];
        zbierzZJson(j, 'Zaimportowane', plaskie);
        if (plaskie.length) { noweGrupy = zbudujGrupy(plaskie); zrodlo = 'obcy plik JSON'; }
      }
    } catch (bladJson) {
      // 3. HTML z zakladkami
      const plaskie = zbierzZHtml(tekst);
      if (plaskie.length) { noweGrupy = zbudujGrupy(plaskie); zrodlo = 'zakladki HTML'; }
    }

    if (!noweGrupy || !noweGrupy.length) {
      alert('Nie znalazlem w tym pliku zadnych adresow.\n\n' +
            'Obslugiwane sa: kopie Szybkiego Dostepu, pliki JSON z innych\n' +
            'speed dialow oraz zakladki wyeksportowane do HTML.');
      e.target.value = '';
      return;
    }

    const ile = noweGrupy.reduce((s, g) => s + g.skroty.length, 0);
    const doklej = confirm(
      'Rozpoznano: ' + zrodlo + '\n' +
      'Znaleziono ' + ile + ' skrotow w ' + noweGrupy.length + ' grupach.\n\n' +
      'OK  = DODAJ do obecnych skrotow\n' +
      'Anuluj = ZASTAP wszystko zawartoscia pliku');

    if (doklej) {
      for (const g of noweGrupy) {
        const istnieje = dane.grupy.find((x) => x.nazwa === g.nazwa);
        if (istnieje) {
          for (const s of g.skroty) {
            if (!istnieje.skroty.some((x) => x.url === s.url)) istnieje.skroty.push(s);
          }
        } else {
          dane.grupy.push(g);
        }
      }
    } else {
      if (!confirm('Na pewno ZASTAPIC wszystkie obecne skroty?')) { e.target.value = ''; return; }
      dane = { grupy: noweGrupy, aktywna: 0, zapisano: 0 };
    }

    if (dane.aktywna >= dane.grupy.length) dane.aktywna = 0;
    await zapisz();
    rysuj();
    const osierocone = await usunOsieroconeIkony();
    pokazPasek('Zaimportowano ' + ile + ' skrotow (' + zrodlo + ')' +
               (osierocone ? (', usunieto ' + osierocone + ' nieuzywanych ikon') : '') + '.');
  } catch (err) {
    alert('Nie udalo sie wczytac pliku: ' + err.message);
  }
  e.target.value = '';
});

// --------------------------------------------------------------- start
// przelacznik profili w pasku narzedzi
$('profil').addEventListener('change', async (e) => {
  const wybor = e.target.value;
  if (wybor === '::nowy::') { await nowyProfil(); return; }
  await przelaczProfil(wybor);
});

(async function start() {
  await wczytaj();
  odswiezWyborProfilu();

  // Kazde otwarcie nowej karty przypomina procesowi tla, ktory profil jest
  // aktywny. Proces tla bywa uspiony i potrafi przegapic zmiane w magazynie,
  // a menu kontekstowe z cudzymi grupami to najgorszy rodzaj pomylki.
  try { chrome.runtime.sendMessage({ typ: 'profilZmieniony' }).catch(() => {}); }
  catch (e) { /* proces tla wstanie sam */ }

  zastosujTlo();
  zastosujRozmiar();
  odswiezMenu();
  await odswiezStanSync();
  rysuj();
  $('szukaj').focus();

  // Karta otwarta zaraz po dodaniu strony z menu kontekstowego: pokazujemy
  // potwierdzenie i pytamy, czy zostac tutaj, czy wrocic na strone.
  try {
    const s = await chrome.storage.session.get('ostatnioDodane');
    const inf = s.ostatnioDodane;
    if (inf && (Date.now() - inf.czas) < 20000) {
      await chrome.storage.session.remove('ostatnioDodane');

      // przelaczamy sie na grupe, do ktorej skrot trafil
      if (typeof inf.indeksGrupy === 'number' && inf.indeksGrupy < dane.grupy.length) {
        dane.aktywna = inf.indeksGrupy;
        rysuj();
      }

      const przyciski = [{
        napis: 'Zostan tutaj',
        drugi: true,
        akcja: () => { ukryjPasek(); }
      }];

      if (inf.zrodloTab) {
        przyciski.unshift({
          napis: 'Wroc na strone',
          akcja: async () => {
            try {
              await chrome.tabs.update(inf.zrodloTab, { active: true });
              const ta = await chrome.tabs.getCurrent();
              if (ta) await chrome.tabs.remove(ta.id);
            } catch (e) {
              pokazPasek('Nie moge wrocic - tamta karta zostala zamknieta.');
            }
          }
        });
      }

      pokazPasek('Dodano "' + inf.nazwa + '" do grupy ' + inf.grupa + '.', przyciski);
    }
  } catch (e) { /* brak pamieci sesji - pomijamy */ }

  // Dogrywanie ikon: chwile po otwarciu karty (Chrome mogl w miedzyczasie
  // poznac ikony stron, ktore odwiedziles) oraz po kazdym powrocie do karty.
  // Gdy mamy juz zgode na dostep do witryn, brakujace ikony dociagamy z sieci
  // TEZ automatycznie - bez pytania, bo zgoda zostala wczesniej udzielona.
  // Kopia zapasowa tej maszyny - raz na uruchomienie przegladarki.
  // Znacznik trzyma sie w pamieci sesji, wiec nie robi sie przy kazdej karcie.
  setTimeout(async () => {
    try {
      if (!uchwytFolderu) return;
      await zapewnijNazweUrzadzenia();
      const s = await chrome.storage.session.get('kopiaZrobiona');
      if (s.kopiaZrobiona) return;
      if (await zapiszKopieUrzadzenia()) {
        await chrome.storage.session.set({ kopiaZrobiona: true });
      }
    } catch (e) { /* nic */ }
  }, 3000);

  await odbierzMiniature();           // zrzut zostawiony przez proces tla

  setTimeout(async () => {
    await naprawNiepasujaceIkony();   // najpierw czyscimy ikony nie od tej strony
    await dogrywajIkony();
    try {
      if (!uchwytFolderu) return;
      if (!(await chrome.permissions.contains({ origins: ['https://*/*'] }))) return;
      const brak = dane.grupy.reduce((t, g) => t.concat(g.skroty), [])
                             .filter((s) => !s.ikona && !s.ikonaPlik && !bezIkony.has(s.url));
      if (brak.length) await uzupelnijIkony(brak, true);
    } catch (e) { /* nic */ }
  }, 1500);

  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) setTimeout(dogrywajIkony, 1200);
  });

  // Klikniecie kafelka oznacza, ze zaraz odwiedzisz ta strone - kasujemy
  // ja z listy "sprawdzone bez skutku", zeby przy powrocie sprobowac znowu.
  document.addEventListener('click', (e) => {
    const kafel = e.target.closest ? e.target.closest('.kafel') : null;
    if (kafel && kafel.href) bezIkony.delete(kafel.href);
  }, true);

  // JEDNO pytanie na cala instalacje. Przegladarka nie pozwala rozszerzeniu
  // pisac po dysku bez gestu uzytkownika, wiec tego klikniecia nie da sie
  // uniknac - ale wystarczy raz i zalatwia wszystko naraz: folder, strukture,
  // przeniesienie ikon do plikow i uzupelnienie brakujacych.
  //
  // GDY DZIALA MOST - nie pytamy o NIC. Most zapisuje pliki sam, uchwyt do
  // folderu jest wtedy zbedny. Wczesniej ten pasek wyskakiwal po kazdej
  // czystej instalacji (uchwyt ginie razem z pamiecia przegladarki) i przy
  // okazji NADPISYWAL pytanie "Wrocic na strone?" - jeden pasek, ostatni wygrywa.
  const mostGotowy = await sprawdzMost();
  odswiezMenu();     // teraz juz wiadomo, czy most dziala - poprawiamy napis w menu
  if (!mostGotowy && !uchwytFolderu && window.showDirectoryPicker && !ustawienia.niePytajOFolder) {
    setTimeout(() => {
      pokazPasek('Jednorazowa konfiguracja: wskaz folder rozszerzenia, a reszta zrobi sie sama.', [
        {
          napis: 'Skonfiguruj',
          akcja: async () => {
            ukryjPasek();
            if (!(await zapewnijFolder())) return;
            await zapewnijNazweUrzadzenia();     // bez pytania - nadajemy sama
            await uporzadkujFolder();
            await zapiszKopieUrzadzenia();
            const brak = dane.grupy.reduce((t, g) => t.concat(g.skroty), [])
                                   .filter((s) => !s.ikona && !s.ikonaPlik);
            if (brak.length) await uzupelnijIkony(brak, false, true);
            odswiezMenu();
          }
        },
        {
          napis: 'Nie pytaj',
          drugi: true,
          akcja: async () => {
            ukryjPasek();
            ustawienia.niePytajOFolder = true;
            await zapiszUstawienia();
          }
        }
      ]);
    }, 800);
  }

  // Dodanie strony z menu kontekstowego zmienia magazyn - otwarta karta
  // ma to pokazac od razu, bez odswiezania.
  chrome.storage.onChanged.addListener(async (zmiany, obszar) => {
    if (obszar !== 'local' || !zmiany[kluczDanych()]) return;
    const nowe = zmiany[kluczDanych()].newValue;
    if (!poprawne(nowe)) return;
    if ((nowe.zapisano || 0) <= (dane.zapisano || 0)) return;   // to byla nasza wlasna zmiana
    dane = nowe;
    bazaZapisu = dane.zapisano || 0;
    if (dane.aktywna >= dane.grupy.length) dane.aktywna = 0;
    rysuj();
    pokazPasek('Dodano nowa strone z menu kontekstowego.');
    await sprawdzWyglad();
  });

  // Dopoki karta jest otwarta i widoczna, co 5 sekund zagladamy do pliku
  // i SCALAMY w obie strony. Bez pytan - lustro ma dzialac samo.
  setInterval(async () => {
    if (document.hidden) return;
    try {
      const o = await wczytajZDysku();
      if (!o) return;

      const scalone = scalDane(dane, o);
      const uNas  = rozniSie(scalone, dane);
      const uNich = rozniSie(scalone, o);
      if (!uNas && !uNich) return;

      dane = scalone;
      if (uNich) dane.zapisano = Date.now();
      bazaZapisu = dane.zapisano || 0;
      if (dane.aktywna >= dane.grupy.length) dane.aktywna = 0;
      await chrome.storage.local.set({ [kluczDanych()]: dane });
      rysuj();
      await sprawdzWyglad();
      if (uNich) await zapiszDoPliku();
    } catch (e) { /* plik chwilowo zajety - sprobujemy za chwile */ }
    finally { await odswiezStanSync(); }
  }, 5000);

  // Zamkniecie karty lub przegladarki: jesli sa niezapisane zmiany i mamy
  // zgode, dopychamy je do pliku. Zapis i tak idzie po kazdej zmianie,
  // to jest zabezpieczenie na wypadek, gdy zgoda pojawila sie pozniej.
  const dopchnij = async () => {
    if (!uchwytZgody()) return;
    try {
      if (!(await maPrawo(uchwytZgody(), false))) return;
      const o = await wczytajZDysku();
      if (!o || (dane.zapisano || 0) > (o.zapisano || 0)) await zapiszDoPliku();
    } catch (e) { /* nic */ }
  };
  document.addEventListener('visibilitychange', () => { if (document.hidden) dopchnij(); });
  window.addEventListener('pagehide', dopchnij);
})();
