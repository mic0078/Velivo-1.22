'use strict';

// =====================================================================
//  Szybki Dostep - przechwytywanie formularzy logowania
// =====================================================================
//  Do tej pory login trafial do sejfu WYLACZNIE przez recznie wypelnione
//  pola w okienku rozszerzenia. Nikt tego nie robil, wiec baza zostawala
//  pusta, a menedzer hasel bez hasel jest tylko ikonka. Teraz wpisany
//  login i haslo sa wylapywane w chwili wysylania formularza, a pasek na
//  dole strony pyta, czy zapisac je w sejfie.
//
//  NIC NIE WYCHODZI ZE STRONY BEZ ZGODY. Skrypt nie wysyla niczego sam:
//  dane czekaja w pamieci karty, a do procesu tla ida dopiero po
//  kliknieciu "Zapisz". Odmowa dla danej domeny jest zapamietywana.
// =====================================================================

const SD_ODMOWY = 'sdOdmowyZapisu';
const SD_PASEK_ID = 'sd-pasek-zapisu-hasla';
// Pytanie o zapis musi PRZETRWAC przeladowanie strony. Logowanie prawie zawsze
// konczy sie przejsciem na inna strone, a wtedy pasek ginal razem ze skryptem -
// czlowiek widzial tylko mignieciem okienko, ktore "samo znikalo".
// Dane czekaja w sessionStorage TEJ karty (nie wychodza poza strone, tak jak dotad,
// i znikaja przy zamknieciu karty), najwyzej przez SD_WAZNE_MS.
const SD_CZEKA = 'sdCzekaNaZapis';
const SD_WAZNE_MS = 180000;

function sdOdloz(domena, login, haslo) {
  try {
    sessionStorage.setItem(SD_CZEKA, JSON.stringify({ domena, login, haslo, kiedy: Date.now() }));
  } catch (e) { /* bez sessionStorage pasek zadziala tylko do przeladowania */ }
}

function sdZapomnijOdlozone() {
  try { sessionStorage.removeItem(SD_CZEKA); } catch (e) { /* nic */ }
}

function sdOdlozone() {
  try {
    const t = sessionStorage.getItem(SD_CZEKA);
    if (!t) return null;
    const d = JSON.parse(t);
    if (!d || !d.haslo || !d.login) { sdZapomnijOdlozone(); return null; }
    if (Date.now() - (d.kiedy || 0) > SD_WAZNE_MS) { sdZapomnijOdlozone(); return null; }
    return d;
  } catch (e) { return null; }
}

function sdPolaFormularza(korzen) {
  const pola = [...korzen.querySelectorAll('input:not([type="hidden"])')];
  const haslo = pola.find((p) => p.type === 'password' && p.value);
  if (!haslo) return null;
  // Login to zwykle pole tekstowe stojace PRZED haslem. Kolejnosc w DOM jest
  // pewniejsza niz nazwa pola - strony nazywaja je jak chca.
  const przed = pola.slice(0, pola.indexOf(haslo));
  const login =
    przed.reverse().find((p) => /^(text|email|tel)$/.test(p.type) && p.value) ||
    pola.find((p) => p !== haslo && /user|login|email|mail|nazwa/i.test(`${p.name} ${p.id} ${p.autocomplete}`) && p.value);
  return { login: login ? login.value.trim() : '', haslo: haslo.value };
}

let sdOstatnie = null;

function sdZapamietaj(korzen) {
  const dane = sdPolaFormularza(korzen || document);
  if (!dane || !dane.haslo || !dane.login) return;
  sdOstatnie = dane;
  // Odkladamy NATYCHMIAST, jeszcze w trakcie wysylania formularza. Strona potrafi
  // przeladowac sie szybciej, niz minie chwila do pokazania paska - wtedy pytanie
  // przepadalo i wygladalo to tak, jakby okienko "samo znikalo".
  const domena = location.hostname.toLowerCase();
  if (domena) sdOdloz(domena, dane.login, dane.haslo);
}

// Formularz wyslany - dopiero teraz wiadomo, ze to byla proba logowania,
// a nie przypadkowo wypelnione pole.
document.addEventListener('submit', (e) => {
  sdZapamietaj(e.target);
  if (sdOstatnie) setTimeout(sdSprobujZapytac, 400);
}, true);

// Coraz wiecej stron loguje bez formularza - zwyklym przyciskiem i fetchem.
document.addEventListener('click', (e) => {
  const cel = e.target instanceof Element ? e.target.closest('button, input[type="submit"], [role="button"]') : null;
  if (!cel) return;
  sdZapamietaj(document);
  if (sdOstatnie) setTimeout(sdSprobujZapytac, 900);
}, true);

async function sdOdmowione(domena) {
  try {
    const z = await chrome.storage.local.get(SD_ODMOWY);
    return Array.isArray(z[SD_ODMOWY]) && z[SD_ODMOWY].includes(domena);
  } catch (e) { return false; }
}

async function sdOdmow(domena) {
  try {
    const z = await chrome.storage.local.get(SD_ODMOWY);
    const lista = Array.isArray(z[SD_ODMOWY]) ? z[SD_ODMOWY] : [];
    if (!lista.includes(domena)) lista.push(domena);
    await chrome.storage.local.set({ [SD_ODMOWY]: lista });
  } catch (e) { /* zapamietanie odmowy to wygoda, nie warunek */ }
}

async function sdSprobujZapytac() {
  if (!sdOstatnie || document.getElementById(SD_PASEK_ID)) return;
  const domena = location.hostname.toLowerCase();
  if (!domena || await sdOdmowione(domena)) return;
  const dane = sdOstatnie;
  sdOstatnie = null;
  // Najpierw odkladamy, potem pokazujemy: gdy strona przeladuje sie w trakcie,
  // pytanie wroci na nowej stronie zamiast przepasc.
  sdOdloz(domena, dane.login, dane.haslo);
  sdPokazPasek(domena, dane.login, dane.haslo);
}

// Po wejsciu na strone sprawdzamy, czy nie zostalo pytanie sprzed przeladowania.
async function sdWrocDoPytania() {
  if (document.getElementById(SD_PASEK_ID)) return;
  const d = sdOdlozone();
  if (!d) return;
  if (await sdOdmowione(d.domena)) { sdZapomnijOdlozone(); return; }
  sdPokazPasek(d.domena, d.login, d.haslo);
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', sdWrocDoPytania);
else sdWrocDoPytania();

function sdPokazPasek(domena, login, haslo) {
  const pasek = document.createElement('div');
  pasek.id = SD_PASEK_ID;
  pasek.style.cssText = [
    'position:fixed', 'left:16px', 'bottom:16px', 'z-index:2147483647',
    'background:#1e2028', 'color:#f5f7fb', 'padding:14px 16px', 'border-radius:6px',
    'box-shadow:0 6px 24px rgba(0,0,0,.45)', 'font:14px Segoe UI,system-ui,sans-serif',
    'max-width:360px', 'line-height:1.4'
  ].join(';');

  const tekst = document.createElement('div');
  tekst.textContent = `Zapisac login ${login} dla ${domena} w sejfie?`;
  tekst.style.marginBottom = '10px';
  pasek.appendChild(tekst);

  const stan = document.createElement('div');
  stan.style.cssText = 'margin-bottom:10px;color:#9da6b8;display:none';
  pasek.appendChild(stan);

  const rzad = document.createElement('div');
  rzad.style.cssText = 'display:flex;gap:8px';
  pasek.appendChild(rzad);

  const przycisk = (napis, tlo) => {
    const b = document.createElement('button');
    b.textContent = napis;
    b.style.cssText = `flex:1;padding:8px;border:0;border-radius:3px;cursor:pointer;color:#fff;background:${tlo};font:14px Segoe UI,system-ui,sans-serif`;
    rzad.appendChild(b);
    return b;
  };

  const zapisz = przycisk('Zapisz', '#0878c9');
  const nigdy = przycisk('Nie dla tej strony', '#4b5363');
  const teraz = przycisk('Nie teraz', '#4b5363');

  let zapisywanie = false, poWyniku = false;
  const zamknij = () => { sdZapomnijOdlozone(); pasek.remove(); };
  teraz.addEventListener('click', zamknij);
  nigdy.addEventListener('click', () => { sdOdmow(domena); zamknij(); });

  zapisz.addEventListener('click', async () => {
    zapisywanie = true;
    zapisz.disabled = true;
    nigdy.disabled = true;
    teraz.disabled = true;
    stan.style.display = 'block';
    stan.style.color = '#9da6b8';
    stan.textContent = 'Zapisuje w zaszyfrowanym sejfie... czekam na potwierdzenie.';
    try {
      const o = await chrome.runtime.sendMessage({
        typ: 'sejfZapytanie',
        dane: { c: 'sejf-zapisz', domena, login, haslo }
      });
      if (!o || !o.ok) throw new Error((o && o.blad) || 'Sejf nie odpowiedzial.');
      // Zapis potwierdzony przez sejf - dopiero teraz kasujemy odlozone dane.
      sdZapomnijOdlozone();
      zapisywanie = false;
      poWyniku = true;
      stan.style.color = '#8fe1a5';
      stan.textContent = o.zmieniono ? 'Haslo zaktualizowane w sejfie.' : 'Login zapisany w sejfie.';
      // Potwierdzenie ZOSTAJE na ekranie, dopoki czlowiek go nie zamknie. Wczesniej
      // znikalo po 1,8 s i nie dalo sie go przeczytac.
      rzad.textContent = '';
      const ok = przycisk('OK, zamknij', '#0878c9');
      ok.addEventListener('click', zamknij);
      ok.focus();
    } catch (e) {
      zapisywanie = false;
      poWyniku = true;
      stan.style.color = '#ff9a8f';
      stan.textContent = 'Nie zapisalem: ' + e.message;
      zapisz.disabled = false;
      nigdy.disabled = false;
      teraz.disabled = false;
      zapisz.textContent = 'Sprobuj ponownie';
    }
  });

  document.documentElement.appendChild(pasek);
  // Samo znikniecie tylko wtedy, gdy nikt nie odpowiedzial: ani w trakcie zapisu,
  // ani po wyniku pasek nie zamyka sie sam.
  setTimeout(() => {
    if (document.getElementById(SD_PASEK_ID) !== pasek) return;
    if (zapisywanie || poWyniku) return;
    pasek.remove();   // odlozone dane ZOSTAJA - pytanie wroci po przeladowaniu
  }, 45000);
}

// =====================================================================
//  Ikonka Sejfu w polach logowania: wypelnij, wygeneruj, zapisz
// =====================================================================
//  Jak w LastPass/Bitwarden: przy polu loginu i hasla pojawia sie mala
//  ikonka Sejfu. Klikniecie pokazuje konta zapisane dla TEJ strony
//  (tylko ta domena - strona "podobna do banku" nic nie dostanie),
//  a przy polu nowego hasla - generator mocnego hasla.
//
//  ZASADY: nic nie jest wpisywane ani wysylane samo. Hasla przychodza z
//  Sejfu dopiero po kliknieciu ikonki. Wygenerowane haslo trafia do
//  Sejfu od razu tylko jako NOWY wpis - istniejacego hasla nie nadpisuje;
//  zmiane potwierdza sie paskiem po wyslaniu formularza (strona mogla
//  zmiane odrzucic, a wtedy stare haslo musi zostac w Sejfie).
// =====================================================================

const SD_IKONA_SVG = '<svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true"><rect x="3" y="10" width="18" height="12" rx="2" fill="#2b6cb0"/><path d="M7 10V7a5 5 0 0 1 10 0v3" fill="none" stroke="#2b6cb0" stroke-width="2.4"/><circle cx="12" cy="16" r="2" fill="#fff"/></svg>';
const SD_ZNAKI = ['abcdefghijkmnopqrstuvwxyz', 'ABCDEFGHJKLMNPQRSTUVWXYZ', '23456789', '!@#$%^&*-_=+?'];
const SD_DLUGOSC_HASLA = 20;

// Kryptograficzny generator (crypto.getRandomValues) bez przesuniecia rozkladu:
// losowanie z odrzuceniem, co najmniej jeden znak z kazdej grupy, potem tasowanie.
function sdLosowy(n) {
  const granica = Math.floor(0x100000000 / n) * n;
  const b = new Uint32Array(1);
  for (;;) { crypto.getRandomValues(b); if (b[0] < granica) return b[0] % n; }
}
function sdGenerujHaslo(dlugosc) {
  const wszystkie = SD_ZNAKI.join('');
  const znaki = SD_ZNAKI.map((g) => g[sdLosowy(g.length)]);
  while (znaki.length < dlugosc) znaki.push(wszystkie[sdLosowy(wszystkie.length)]);
  for (let i = znaki.length - 1; i > 0; i--) { const j = sdLosowy(i + 1); [znaki[i], znaki[j]] = [znaki[j], znaki[i]]; }
  return znaki.join('');
}

function sdWidoczne(p) {
  if (!p || !p.isConnected || p.disabled || p.readOnly) return false;
  const r = p.getBoundingClientRect();
  if (r.width < 60 || r.height < 16) return false;
  const s = getComputedStyle(p);
  return s.visibility !== 'hidden' && s.display !== 'none' && Number(s.opacity) > 0.05;
}

function sdKorzen(pole) { return pole.form || pole.closest('form') || document; }

function sdPolaHasla(korzen) {
  return [...korzen.querySelectorAll('input[type="password"]')].filter(sdWidoczne);
}

// Pole loginu: widoczne pole tekstowe PRZED pierwszym haslem (najpierw po nazwie).
function sdPoleLoginu(korzen) {
  const pola = [...korzen.querySelectorAll('input:not([type="hidden"])')].filter(sdWidoczne);
  const pierwszeHaslo = pola.findIndex((p) => p.type === 'password');
  const kandydaci = (pierwszeHaslo >= 0 ? pola.slice(0, pierwszeHaslo) : pola)
    .filter((p) => /^(text|email|tel)$/.test(p.type) && !/search|szukaj|captcha|otp|code|kod/i.test(`${p.name} ${p.id} ${p.autocomplete}`));
  const poNazwie = kandydaci.find((p) => /user|login|email|mail|konto|nazwa|identyf/i.test(`${p.name} ${p.id} ${p.autocomplete} ${p.placeholder}`));
  return poNazwie || kandydaci[kandydaci.length - 1] || null;
}

// Czy to pole na NOWE haslo (rejestracja, zmiana hasla)?
function sdNoweHaslo(pole) {
  const ac = (pole.autocomplete || '').toLowerCase();
  if (ac === 'new-password') return true;
  if (ac === 'current-password') return false;
  const opis = `${pole.name} ${pole.id} ${pole.placeholder} ${pole.getAttribute('aria-label') || ''}`;
  if (/new|nowe|confirm|powt|repeat|retype|again|regist|signup|rejestr/i.test(opis)) return true;
  if (/old|stare|current|obecne/i.test(opis)) return false;
  const hasla = sdPolaHasla(sdKorzen(pole));
  // Dwa pola hasla = rejestracja (haslo + powtorz); trzy = zmiana (stare + nowe + powtorz).
  if (hasla.length === 2) return true;
  if (hasla.length >= 3) return hasla.indexOf(pole) > 0;
  return false;
}

// Wpisanie wartosci tak, zeby strona (React, Angular, zwykly formularz) to zauwazyla.
function sdUstaw(pole, wartosc) {
  if (!pole) return false;
  pole.focus();
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
  if (setter) setter.call(pole, wartosc); else pole.value = wartosc;
  pole.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
  pole.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
  pole.dispatchEvent(new Event('blur', { bubbles: true }));
  return true;
}

function sdSejf(dane) {
  return chrome.runtime.sendMessage({ typ: 'sejfZapytanie', dane }).then((o) => {
    if (!o || !o.ok) throw new Error((o && o.blad) || 'Sejf nie odpowiedzial.');
    return o;
  });
}

// ---------------------------------------------------------- warstwa ikonek
// Wszystko w zamknietym shadow DOM: style strony nie psuja ikonek, ikonki nie psuja
// strony, a skrypty strony nie siegna do menu z loginami.
let sdWarstwa = null, sdCien = null, sdMenu = null, sdMenuPole = null;
const sdIkonki = new Map();   // pole -> przycisk

function sdPrzygotujWarstwe() {
  if (sdWarstwa && sdWarstwa.isConnected) return;
  sdIkonki.forEach((ik) => ik.remove());
  sdIkonki.clear();
  sdWarstwa = document.createElement('div');
  sdWarstwa.id = 'sd-sejf-warstwa';
  sdWarstwa.style.cssText = 'position:absolute;left:0;top:0;width:0;height:0;z-index:2147483646;';
  sdCien = sdWarstwa.attachShadow({ mode: 'closed' });
  const styl = document.createElement('style');
  styl.textContent = [
    '.ik{position:absolute;width:22px;height:22px;padding:3px;border:0;border-radius:4px;background:transparent;cursor:pointer;opacity:.85;display:flex;align-items:center;justify-content:center;box-sizing:border-box}',
    '.ik:hover{background:#e0eef2;opacity:1}',
    '.menu{position:absolute;box-sizing:border-box;width:320px;background:#e0eef2;color:#20303a;border:1px solid #7ea4b2;border-radius:6px;box-shadow:0 6px 22px rgba(0,0,0,.28);font:14px "Segoe UI",system-ui,sans-serif;padding:6px;line-height:1.35;text-align:left}',
    '.nag{font-weight:600;padding:4px 6px 6px;border-bottom:1px solid #b0c4ce;margin-bottom:4px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
    '.poz{display:block;box-sizing:border-box;width:100%;text-align:left;border:1px solid #adbac2;background:#fdfdfd;color:#20303a;border-radius:4px;padding:7px 9px;margin:4px 0;cursor:pointer;font:14px "Segoe UI",system-ui,sans-serif}',
    '.poz:hover{border-color:#2b6cb0;background:#fff}',
    '.poz:disabled{opacity:.6;cursor:default}',
    '.poz small{display:block;color:#445c68;font-size:12px}',
    '.inf{padding:6px;color:#2c424c;font-size:13px}',
    '.blad{padding:6px;color:#962020;font-size:13px}',
    '.ok{padding:6px;color:#005c34;font-size:13px}',
    '.gen{font:15px Consolas,monospace;background:#fff;border:1px solid #adbac2;border-radius:4px;padding:6px 8px;margin:4px 0;word-break:break-all;user-select:all}'
  ].join('\n');
  sdCien.appendChild(styl);
  document.documentElement.appendChild(sdWarstwa);
}

function sdUstawPozycje(pole, ik) {
  const r = pole.getBoundingClientRect();
  ik.style.left = `${r.right + scrollX - 26}px`;
  ik.style.top = `${r.top + scrollY + (r.height - 22) / 2}px`;
}

function sdOdswiezPozycje() {
  for (const [pole, ik] of sdIkonki) {
    if (!sdWidoczne(pole)) { ik.style.display = 'none'; continue; }
    ik.style.display = 'flex';
    sdUstawPozycje(pole, ik);
  }
}

function sdDodajIkonke(pole) {
  if (sdIkonki.has(pole)) return;
  const ik = document.createElement('button');
  ik.className = 'ik';
  ik.type = 'button';
  ik.tabIndex = -1;
  ik.title = 'Sejf - wypelnij, wygeneruj haslo, zapisz';
  ik.innerHTML = SD_IKONA_SVG;
  ik.addEventListener('mousedown', (e) => e.preventDefault());   // pole nie traci fokusu
  ik.addEventListener('click', (e) => { e.preventDefault(); e.stopPropagation(); sdPrzelaczMenu(pole); });
  sdCien.appendChild(ik);
  sdIkonki.set(pole, ik);
  sdUstawPozycje(pole, ik);
}

function sdSkanuj() {
  const hasla = sdPolaHasla(document).slice(0, 12);
  if (!hasla.length && !sdIkonki.size) return;   // strona bez logowania - nic nie dokladamy
  sdPrzygotujWarstwe();
  // Pola, ktore zniknely ze strony, sprzatamy razem z ikonkami.
  for (const [pole, ik] of sdIkonki) if (!pole.isConnected) { ik.remove(); sdIkonki.delete(pole); }
  for (const h of hasla) {
    sdDodajIkonke(h);
    const login = sdPoleLoginu(sdKorzen(h));
    if (login) sdDodajIkonke(login);
  }
  sdOdswiezPozycje();
}

function sdZamknijMenu() { if (sdMenu) { sdMenu.remove(); sdMenu = null; sdMenuPole = null; } }

function sdElement(tag, klasa, tekst) {
  const e = document.createElement(tag);
  if (klasa) e.className = klasa;
  if (tekst != null) e.textContent = tekst;
  return e;
}

function sdPrzelaczMenu(pole) {
  if (sdMenu && sdMenuPole === pole) { sdZamknijMenu(); return; }
  sdZamknijMenu();
  const korzen = sdKorzen(pole);
  const hasla = sdPolaHasla(korzen);
  const noweHasla = hasla.filter(sdNoweHaslo);
  const polLogin = sdPoleLoginu(korzen);
  const domena = location.hostname.toLowerCase();

  const m = sdElement('div', 'menu');
  sdMenu = m; sdMenuPole = pole;
  const r = pole.getBoundingClientRect();
  const lewo = Math.min(Math.max(4, r.left), Math.max(4, document.documentElement.clientWidth - 324));
  m.style.left = `${lewo + scrollX}px`;
  m.style.top = `${r.bottom + scrollY + 4}px`;
  m.appendChild(sdElement('div', 'nag', `Sejf - ${domena}`));
  const tresc = sdElement('div');
  m.appendChild(tresc);
  sdCien.appendChild(m);

  // Formularz z polem nowego hasla: generator na gorze, bo po to czlowiek kliknal.
  if (noweHasla.length) sdMenuGeneratora(tresc, noweHasla, polLogin, domena);
  sdMenuKont(tresc, hasla, noweHasla, polLogin, domena);
}

function sdMenuKont(tresc, hasla, noweHasla, polLogin, domena) {
  const blok = sdElement('div');
  blok.appendChild(sdElement('div', 'inf', 'Szukam kont w Sejfie...'));
  tresc.appendChild(blok);
  sdSejf({ c: 'sejf-szukaj', domena }).then((o) => {
    blok.textContent = '';
    const wpisy = o.wpisy || [];
    // Do logowania wypelniamy pierwsze pole hasla, ktore NIE jest polem nowego hasla.
    const poleHasla = hasla.find((h) => !noweHasla.includes(h)) || null;
    // Rejestracja (same pola nowego hasla): zapisane konta tylko by przeszkadzaly.
    const doLogowania = poleHasla ? wpisy : [];
    if (poleHasla && !wpisy.length) blok.appendChild(sdElement('div', 'inf', 'Brak zapisanych kont dla tej strony.'));
    for (const w of doLogowania) {
      const b = sdElement('button', 'poz', w.Login || '(bez loginu)');
      if (w.Nazwa && w.Nazwa !== w.Login) b.appendChild(sdElement('small', null, w.Nazwa));
      b.addEventListener('click', () => {
        if (polLogin && w.Login) sdUstaw(polLogin, w.Login);
        if (poleHasla && w.Haslo) sdUstaw(poleHasla, w.Haslo);
        sdZamknijMenu();
      });
      blok.appendChild(b);
    }
    // Wpisane recznie dane mozna zapisac od razu, bez czekania na wyslanie formularza.
    const haslo = (hasla.find((h) => h.value) || {}).value;
    const login = polLogin ? polLogin.value.trim() : '';
    if (haslo && login) {
      const z = sdElement('button', 'poz', 'Zapisz wpisane dane w Sejfie');
      z.appendChild(sdElement('small', null, `login: ${login}`));
      z.addEventListener('click', () => {
        z.disabled = true;
        sdSejf({ c: 'sejf-dodaj', domena, login, haslo })
          .then(() => { z.remove(); blok.appendChild(sdElement('div', 'ok', 'Zapisane w Sejfie (Hasla do stron).')); })
          .catch((e) => {
            z.remove();
            // Taki login juz jest - nie nadpisujemy bez pytania: pasek po wyslaniu formularza zapyta.
            if (/istnieje/.test(e.message)) sdOdloz(domena, login, haslo);
            blok.appendChild(sdElement('div', 'blad', /istnieje/.test(e.message)
              ? 'Ten login juz jest w Sejfie. Po zalogowaniu Sejf zapyta, czy zaktualizowac haslo.'
              : 'Nie zapisalem: ' + e.message));
          });
      });
      blok.appendChild(z);
    }
  }).catch((e) => {
    blok.textContent = '';
    blok.appendChild(sdElement('div', 'blad', 'Sejf niedostepny: ' + e.message));
    blok.appendChild(sdElement('div', 'inf', 'Sprawdz, czy Sejf jest zainstalowany z mostem do przegladarek.'));
  });
}

function sdMenuGeneratora(tresc, noweHasla, polLogin, domena) {
  const blok = sdElement('div');
  tresc.appendChild(blok);
  let haslo = sdGenerujHaslo(SD_DLUGOSC_HASLA);
  const podglad = sdElement('div', 'gen', haslo);
  const uzyj = sdElement('button', 'poz', 'Uzyj tego hasla i zapisz w Sejfie');
  uzyj.appendChild(sdElement('small', null, `${SD_DLUGOSC_HASLA} znakow: male, duze litery, cyfry, znaki specjalne`));
  const inne = sdElement('button', 'poz', 'Losuj inne');
  blok.appendChild(sdElement('div', 'inf', 'Proponowane mocne haslo:'));
  blok.appendChild(podglad);
  blok.appendChild(uzyj);
  blok.appendChild(inne);
  inne.addEventListener('click', () => { haslo = sdGenerujHaslo(SD_DLUGOSC_HASLA); podglad.textContent = haslo; });
  uzyj.addEventListener('click', () => {
    for (const h of noweHasla) sdUstaw(h, haslo);   // "haslo" i "powtorz haslo" naraz
    inne.remove(); uzyj.remove();
    const login = polLogin ? polLogin.value.trim() : '';
    if (!login) {
      blok.appendChild(sdElement('div', 'inf', 'Haslo wpisane. Wpisz login i wyslij formularz - Sejf zapyta o zapis.'));
      return;
    }
    // Na wypadek przeladowania strony w trakcie - pasek po wyslaniu i tak zapyta.
    sdOdloz(domena, login, haslo);
    const stan = sdElement('div', 'inf', 'Zapisuje w Sejfie...');
    blok.appendChild(stan);
    sdSejf({ c: 'sejf-dodaj', domena, login, haslo })
      .then(() => { sdZapomnijOdlozone(); stan.className = 'ok'; stan.textContent = 'Haslo wpisane i zapisane w Sejfie (Hasla do stron).'; })
      .catch((e) => {
        stan.className = 'inf';
        stan.textContent = /istnieje/.test(e.message)
          ? 'Haslo wpisane. Ten login juz jest w Sejfie - stare haslo zostaje, dopoki po wyslaniu formularza nie potwierdzisz zmiany.'
          : 'Haslo wpisane, ale Sejf go nie zapisal (' + e.message + '). Po wyslaniu formularza Sejf zapyta jeszcze raz.';
      });
  });
}

// Klikniecie poza menu i Escape zamykaja menu.
document.addEventListener('mousedown', (e) => { if (sdMenu && !e.composedPath().includes(sdWarstwa)) sdZamknijMenu(); }, true);
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') sdZamknijMenu(); }, true);

// Strony dokladaja formularze w trakcie (logowanie w okienku, SPA) - skanujemy
// po zmianach DOM, ale najwyzej co 400 ms, zeby nie obciazac strony.
let sdSkanCzeka = false;
function sdZaplanujSkan() {
  if (sdSkanCzeka) return;
  sdSkanCzeka = true;
  setTimeout(() => { sdSkanCzeka = false; try { sdSkanuj(); } catch (e) { /* strona nie moze przez nas przestac dzialac */ } }, 400);
}
new MutationObserver((zmiany) => {
  // Zmiany w naszej wlasnej warstwie nie sa powodem do skanowania.
  if (zmiany.every((z) => z.target === sdWarstwa)) return;
  sdZaplanujSkan();
}).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['type', 'style', 'class', 'hidden'] });
addEventListener('scroll', () => { if (sdIkonki.size) sdOdswiezPozycje(); }, true);
addEventListener('resize', () => { if (sdIkonki.size) sdOdswiezPozycje(); });
setInterval(() => { if (sdIkonki.size) sdOdswiezPozycje(); }, 1500);
sdZaplanujSkan();
