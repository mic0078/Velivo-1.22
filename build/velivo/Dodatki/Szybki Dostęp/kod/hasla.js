const HOST = 'pl.szybkidostep.most';
const domenaElement = document.getElementById('domena');
const statusElement = document.getElementById('status');
const wpisyElement = document.getElementById('wpisy');
const wypelnijButton = document.getElementById('wypelnij');
const dodajLoginButton = document.getElementById('dodajLogin');
const nowyLoginElement = document.getElementById('nowyLogin');
const noweHasloElement = document.getElementById('noweHaslo');
let karta = null;
let domena = '';
let wpisy = [];

function blad(tekst) {
  statusElement.textContent = tekst;
  statusElement.className = 'blad';
}

function szukaj(domena) {
  return chrome.runtime.sendMessage({ typ: 'sejfZapytanie', dane: { c: 'sejf-szukaj', domena } });
}

async function start() {
  const karty = await chrome.tabs.query({ active: true, currentWindow: true });
  karta = karty[0];
  let adres;
  try { adres = new URL(karta.url); } catch (_) { throw new Error('Ta strona nie obsluguje logowania.'); }
  domena = adres.hostname.toLowerCase();
  domenaElement.textContent = domena;
  const odpowiedz = await szukaj(domena);
  if (!odpowiedz || !odpowiedz.ok) throw new Error(odpowiedz?.blad || 'Nie udalo sie otworzyc sejfu.');
  wpisy = odpowiedz.wpisy || [];
  if (!wpisy.length) {
    statusElement.textContent = 'Brak wpisu typu Login dla tej domeny. W bazie wpisz domene w polu Lokalizacja.';
    return;
  }
  statusElement.textContent = 'Wybierz wpis i wypelnij formularz.';
  wpisy.forEach((wpis, indeks) => {
    const opcja = document.createElement('option');
    opcja.value = indeks;
    opcja.textContent = wpis.Nazwa || wpis.Login || 'Bez nazwy';
    wpisyElement.appendChild(opcja);
  });
  wpisyElement.hidden = false;
  wypelnijButton.disabled = false;
}

function dodajLogin(domena, login, haslo) {
  return chrome.runtime.sendMessage({ typ: 'sejfZapytanie', dane: { c: 'sejf-dodaj', domena, login, haslo } });
}

function wypelnijFormularz(login, haslo) {
  const pola = [...document.querySelectorAll('input:not([type="hidden"]), textarea')];
  const poleHasla = pola.find((pole) => pole.type === 'password' || /current-password/i.test(pole.autocomplete || ''));
  const poleLogin = pola.find((pole) => pole !== poleHasla && /username|email|user|login/i.test(`${pole.name} ${pole.id} ${pole.autocomplete} ${pole.type}`)) ||
    pola.find((pole) => pole !== poleHasla && pole.type === 'text') || pola[0];

  function ustaw(pole, wartosc) {
    if (!pole) return false;
    const prototyp = pole instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(prototyp, 'value')?.set;
    if (setter) setter.call(pole, wartosc);
    else pole.value = wartosc;
    pole.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
    pole.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
    return true;
  }

  const loginUstawiony = ustaw(poleLogin, login);
  const hasloUstawione = ustaw(poleHasla, haslo);
  if (poleLogin) poleLogin.dispatchEvent(new Event('blur', { bubbles: true }));
  if (poleHasla) poleHasla.dispatchEvent(new Event('blur', { bubbles: true }));
  return { loginUstawiony, hasloUstawione };
}

wypelnijButton.addEventListener('click', async () => {
  const wpis = wpisy[Number(wpisyElement.value)];
  if (!wpis) return;
  try {
    const wynik = await chrome.scripting.executeScript({
      target: { tabId: karta.id },
      func: wypelnijFormularz,
      args: [wpis.Login, wpis.Haslo]
    });
    const stan = wynik?.[0]?.result;
    if (!stan?.loginUstawiony || !stan?.hasloUstawione) {
      throw new Error('Nie znaleziono pola loginu albo hasla na tej stronie.');
    }
    statusElement.textContent = 'Formularz wypelniony. Sprawdz dane i zaloguj sie.';
    setTimeout(() => window.close(), 350);
  } catch (error) {
    blad(`Nie udalo sie wypelnic formularza: ${error.message}`);
  }
});

dodajLoginButton.addEventListener('click', async () => {
  const login = nowyLoginElement.value.trim();
  const haslo = noweHasloElement.value;
  if (!login || !haslo) { blad('Wpisz login i haslo.'); return; }
  dodajLoginButton.disabled = true;
  statusElement.className = '';
  statusElement.textContent = 'Zapisuje wpis w zaszyfrowanym sejfie...';
  try {
    const odpowiedz = await dodajLogin(domena, login, haslo);
    if (!odpowiedz || !odpowiedz.ok) throw new Error(odpowiedz?.blad || 'Nie zapisano wpisu.');
    statusElement.textContent = 'Login zapisany w sejfie.';
    nowyLoginElement.value = '';
    noweHasloElement.value = '';
  } catch (e) {
    blad(e.message);
  } finally { dodajLoginButton.disabled = false; }
});

start().catch((e) => blad(e.message));
