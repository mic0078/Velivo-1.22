// Test asystenta wypelniania w trybie bankowym na typowych stronach: rozpoznawanie strony (BankPageDetectScript
// + PartialFillScript w trybie liczenia - jak DetectAndOfferBankFill w src/Bank.cs) i samo wpisywanie wybranych znakow.
// Asystent ma sie pojawic tylko tam, gdzie jest co wypelnic - i wpisac wlasciwe znaki we wlasciwe pola.
// Uruchom: node testy/tryb-bankowy/test-asystent.mjs
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }

const src = readFileSync(new URL('../../src/Bank.cs', import.meta.url), 'utf8');
function wyciagnij(nazwa) {
  const m = src.match(new RegExp('const string ' + nazwa + ' = @"([\\s\\S]*?)(?<!")";\\s*\\n'));
  if (!m) { console.log('FAIL nie znaleziono ' + nazwa); process.exit(1); }
  return m[1].replace(/""/g, '"');
}
const detect = wyciagnij('BankPageDetectScript');
const partial = wyciagnij('PartialFillScript');
const login = wyciagnij('LoginFillScript');

const litery = 'abcdefghijklmnopqrstuvwxyz';
const sel = '<select><option value="">-</option>' + [...litery].map(c => `<option>${c}</option>`).join('') + '</select>';
const pole = (a = '') => `<input maxlength="1" ${a}>`;

const strony = [
  { n: 'logowanie (login + haslo)', h: '<form><input name="username"><input type="password" name="pass"></form>', ma: { login: true, partial: false, card: false } },
  { n: 'logowanie - samo pole numeru klienta', h: '<form><input name="customerNumber" aria-label="Customer number"><button>Next</button></form>', ma: { login: true, partial: false, card: false } },
  { n: 'wybrane znaki (RBS/NatWest)', h: '<p>Enter the 2nd, 4th and 6th digits of your PIN</p>' + pole() + pole() + pole(), ma: { login: false, partial: true, card: false },
    wpis: { d: { pin: '123456' }, ma: ['2', '4', '6'] } },
  { n: 'wybrane znaki z list (Lloyds/Halifax)', h: '<p>Please enter characters 1, 3 and 7 from your memorable information</p>' + sel + sel + sel, ma: { login: false, partial: true, card: false },
    wpis: { d: { mem: 'abcdefg' }, ma: ['a', 'c', 'g'] } },
  { n: 'wybrane cyfry PIN w polach hasla (RBS)', h: '<p>Enter the 1st, 3rd and 4th digit from your PIN</p>' + pole('type="password"').repeat(3), ma: { login: false, partial: true, card: false },
    wpis: { d: { pin: '9876' }, ma: ['9', '7', '6'] } },
  { n: 'osobne podpisy 1st / 3rd / 4th przy polach', h: '<p>Enter digits from your PIN</p><label>1st ' + pole() + '</label><label>3rd ' + pole() + '</label><label>4th ' + pole() + '</label>', ma: { login: false, partial: true, card: false },
    wpis: { d: { pin: '9876' }, ma: ['9', '7', '6'] } },
  { n: 'polski bank: 3., 5. i 8. znak hasla', h: '<p>Podaj 3., 5. i 8. znak hasła</p>' + pole('type="password"').repeat(3), ma: { login: false, partial: true, card: false },
    wpis: { d: { pwd: 'abcdefgh' }, ma: ['c', 'e', 'h'] } },
  { n: 'polski bank: wpisz cyfre nr 3 i 5 kodu PIN', h: '<p>Wpisz cyfrę nr 3 i 5 kodu PIN</p>' + pole() + pole(), ma: { login: false, partial: true, card: false },
    wpis: { d: { pin: '123456' }, ma: ['3', '5'] } },
  { n: 'polski bank: znaki hasla nr: 2, 5, 8', h: '<p>Wpisz znaki hasła nr: 2, 5, 8</p>' + pole('type="password"').repeat(3), ma: { login: false, partial: true, card: false },
    wpis: { d: { pwd: 'abcdefgh' }, ma: ['b', 'e', 'h'] } },
  { n: 'PIN i haslo w dwoch sekcjach na jednej stronie (NatWest)', h: '<div><p>PIN: enter the 1st, 2nd and 4th digits</p>' + pole().repeat(3) + '</div><div><p>Password: enter the 3rd, 7th and 10th characters</p>' + pole('type="password"').repeat(3) + '</div>', ma: { login: false, partial: true, card: false },
    wpis: { d: { pin: '9876', pwd: 'abcdefghij' }, ma: ['9', '8', '6', 'c', 'g', 'j'] } },
  { n: 'slowa: first, third and fifth characters', h: '<p>Please enter the first, third and fifth characters of your password</p>' + pole('type="password"').repeat(3), ma: { login: false, partial: true, card: false },
    wpis: { d: { pwd: 'abcdefgh' }, ma: ['a', 'c', 'e'] } },
  { n: 'kazde pole we wlasnym bloku z podpisem "Character N"', h: '<p>Enter characters from your password</p>' + [2, 5, 9].map(i => `<div><span>Character ${i}</span>` + pole('type="password"') + '</div>').join(''), ma: { login: false, partial: true, card: false },
    wpis: { d: { pwd: 'abcdefghij' }, ma: ['b', 'e', 'i'] } },
  { n: 'zdanie tuz przed pierwszym polem, pozostale bez podpisu', h: '<div><p>Please enter the 2nd, 4th and 6th digits of your PIN below</p>' + pole().repeat(3) + '<p>We will never ask for your full PIN by phone, text message or e-mail. If unsure, contact us.</p></div>', ma: { login: false, partial: true, card: false },
    wpis: { d: { pin: '123456' }, ma: ['2', '4', '6'] } },
  { n: 'PIN: cyfry 1-3, haslo: znaki 4-6 (osobno - to nadal wybrane znaki)', h: '<div><p>PIN: enter the 1st, 2nd and 3rd digits</p>' + pole().repeat(3) + '</div><div><p>Password: enter the 4th, 5th and 6th characters</p>' + pole('type="password"').repeat(3) + '</div>', ma: { login: false, partial: true, card: false },
    wpis: { d: { pin: '9876', pwd: 'abcdefgh' }, ma: ['9', '8', '7', 'd', 'e', 'f'] } },
  { n: 'caly kod w 4 polach "Digit 1..4" (nie wybrane znaki - bez propozycji)', h: '<p>Enter your passcode</p>' + [1, 2, 3, 4].map(i => pole(`aria-label="Digit ${i}"`)).join(''), ma: { login: false, partial: false, card: false } },
  { n: 'platnosc karta', h: '<form><input autocomplete="cc-number"><input autocomplete="cc-exp"><input autocomplete="cc-csc" maxlength="4"></form>', ma: { login: false, partial: false, card: true } },
  { n: 'platnosc karta z MM/RR i CVV jako haslo (nie znaki, nie login)', h: '<p>Enter the 3 digits on the back of your card</p><input name="cardnumber"><input name="exp-month" maxlength="2"><input name="exp-year" maxlength="2"><input type="password" name="cvv" maxlength="4">', ma: { login: false, partial: false, card: true } },
  { n: 'kod SMS w 6 polach "Digit 1..6" (to nie wybrane znaki)', h: '<p>Enter the 6-digit code we sent to your phone</p>' + [1, 2, 3, 4, 5, 6].map(i => pole(`aria-label="Digit ${i}"`)).join(''), ma: { login: false, partial: false, card: false } },
  { n: 'kod jednorazowy (autocomplete one-time-code)', h: '<p>Security check</p>' + [1, 2, 3].map(i => pole(`autocomplete="one-time-code" aria-label="Character ${i * 2}"`)).join(''), ma: { login: false, partial: false, card: false } },
  { n: 'rejestracja: "co najmniej 8 znakow, w tym 1 cyfra" + data urodzenia z list', h: '<p>Password must be at least 8 characters, including 1 digit</p><input type="password" name="newpass"><label>Day <select><option>1</option><option>2</option></select></label><label>Month <select><option>1</option></select></label><label>Year <select><option>1990</option></select></label>', ma: { login: true, partial: false, card: false } },
  { n: 'strona z tekstem o 16-cyfrowym numerze i listami (bez znakow hasla)', h: '<p>Your 16-digit card number is on the front</p><select><option>1</option></select><select><option>2</option></select>', ma: { login: false, partial: false, card: false } },
  { n: 'listy wyboru i data "1st July" (bez znakow hasla)', h: '<p>Oferta wazna do 1st July</p><select><option>1</option></select><select><option>2</option></select>', ma: { login: false, partial: false, card: false } },
  { n: 'zwykla strona z wyszukiwarka', h: '<input type="search" name="q" placeholder="Szukaj"><p>Aktualnosci</p>', ma: { login: false, partial: false, card: false } },
  { n: 'newsletter (sam e-mail)', h: '<input type="email" name="newsletter_email" placeholder="Twoj e-mail">', ma: { login: false, partial: false, card: false } },
  { n: 'formularz z listami wyboru, bez znakow hasla', h: '<select><option>PL</option></select><select><option>EN</option></select><p>Wybierz kraj</p>', ma: { login: false, partial: false, card: false } },
  { n: 'ukryte pole hasla (niewidoczne)', h: '<input type="password" style="display:none">', ma: { login: false, partial: false, card: false } },
];

const browser = await pw.chromium.launch();
const page = await browser.newPage();
let ok = true;
for (const s of strony) {
  const html = '<!doctype html><html><body>' + s.h + '</body></html>';
  await page.setContent(html);
  const r = JSON.parse(await page.evaluate(detect));
  // jak DetectAndOfferBankFill: wybrane znaki = co najmniej 2 rozne numery znakow i nie formularz karty
  const pozycje = parseInt(await page.evaluate(partial.replace('__D__', "{pin:'',pwd:'',mem:'',count:true}")), 10);
  r.partial = !r.card && pozycje >= 2;
  let pass = r.login === s.ma.login && r.partial === s.ma.partial && r.card === s.ma.card, opis = JSON.stringify(r);
  if (pass && s.wpis) {
    await page.setContent(html);
    const d = JSON.stringify({ pin: '', pwd: '', mem: '', ...s.wpis.d });
    const n = parseInt(await page.evaluate(partial.replace('__D__', d)), 10);
    const wart = await page.evaluate(() => [...document.querySelectorAll('input,select')].map(e => e.tagName === 'SELECT' ? (e.selectedIndex > 0 ? e.options[e.selectedIndex].text : '') : e.value));
    pass = n === s.wpis.ma.length && JSON.stringify(wart) === JSON.stringify(s.wpis.ma);
    opis += ' wpisano ' + JSON.stringify(wart) + (pass ? '' : ' oczekiwane ' + JSON.stringify(s.wpis.ma));
  } else if (!pass) opis += ' oczekiwane ' + JSON.stringify(s.ma);
  ok &&= pass;
  console.log(`${pass ? 'PASS' : 'FAIL'} ${s.n}: ${opis}`);
}
// dane wpisujemy tylko na stronie, dla ktorej powstaly (h) - strona, ktora w tej chwili przeszla gdzie indziej, nic nie dostaje
await page.route('https://bank.example/**', r => r.fulfill({ contentType: 'text/html', body: '<!doctype html><html><body><p>Enter the 2nd and 4th digits</p>' + pole().repeat(2) + '<form><input name="username"><input type="password" name="pass"></form></body></html>' }));
await page.goto('https://bank.example/login');
const wartosci = () => page.evaluate(() => [...document.querySelectorAll('input')].map(e => e.value).join('|'));
const host = await page.evaluate(() => location.hostname);
await page.evaluate(partial.replace('__D__', JSON.stringify({ pin: '1234', pwd: '', mem: '', h: 'inny-bank.example' })));
await page.evaluate(login.replace('__D__', JSON.stringify({ u: 'jan', p: 'tajne', h: 'inny-bank.example' })));
let p1 = (await wartosci()) === '|||';
await page.evaluate(partial.replace('__D__', JSON.stringify({ pin: '1234', pwd: '', mem: '', h: host })));
await page.evaluate(login.replace('__D__', JSON.stringify({ u: 'jan', p: 'tajne', h: host })));
let p2 = (await wartosci()) === '2|4|jan|tajne';
console.log(`${p1 ? 'PASS' : 'FAIL'} inna strona (h) - nic nie wpisano`);
console.log(`${p2 ? 'PASS' : 'FAIL'} ta sama strona (h=${host}) - wpisano: ${await wartosci()}`);
ok &&= p1 && p2;
await browser.close();
process.exit(ok ? 0 : 1);
