// Test asystenta wypelniania w trybie bankowym: skrypt rozpoznawania strony (BankPageDetectScript z src/Bank.cs)
// na typowych stronach. Asystent ma sie pojawic tylko tam, gdzie jest co wypelnic - i nigdzie indziej.
// Uruchom: node testy/tryb-bankowy/test-asystent.mjs
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }

const src = readFileSync(new URL('../../src/Bank.cs', import.meta.url), 'utf8');
const m = src.match(/const string BankPageDetectScript = @"([\s\S]*?)(?<!")";\s*\n/);
if (!m) { console.log('FAIL nie znaleziono BankPageDetectScript'); process.exit(1); }
const script = m[1].replace(/""/g, '"');

const strony = [
  { n: 'logowanie (login + haslo)', h: '<form><input name="username"><input type="password" name="pass"></form>', ma: { login: true, partial: false, card: false } },
  { n: 'logowanie - samo pole numeru klienta', h: '<form><input name="customerNumber" aria-label="Customer number"><button>Next</button></form>', ma: { login: true, partial: false, card: false } },
  { n: 'wybrane znaki (RBS/NatWest)', h: '<p>Enter the 2nd, 4th and 6th digits of your PIN</p><input maxlength="1"><input maxlength="1"><input maxlength="1">', ma: { login: false, partial: true, card: false } },
  { n: 'wybrane znaki z list (Lloyds/Halifax)', h: '<p>Please enter characters 1, 3 and 7 from your memorable information</p><select><option>a</option></select><select><option>b</option></select><select><option>c</option></select>', ma: { login: false, partial: true, card: false } },
  { n: 'wybrane cyfry PIN w polach hasla (RBS)', h: '<p>Enter the 1st, 3rd and 4th digit from your PIN</p><input type="password" maxlength="1"><input type="password" maxlength="1"><input type="password" maxlength="1">', ma: { login: false, partial: true, card: false } },
  { n: 'platnosc karta', h: '<form><input autocomplete="cc-number"><input autocomplete="cc-exp"><input autocomplete="cc-csc" maxlength="4"></form>', ma: { login: false, partial: false, card: true } },
  { n: 'platnosc karta z MM/RR i CVV jako haslo (nie znaki, nie login)', h: '<p>Enter the 3 digits on the back of your card</p><input name="cardnumber"><input name="exp-month" maxlength="2"><input name="exp-year" maxlength="2"><input type="password" name="cvv" maxlength="4">', ma: { login: false, partial: false, card: true } },
  { n: 'polski bank: 3., 5. i 8. znak hasla', h: '<p>Podaj 3., 5. i 8. znak hasła</p><input type="password" maxlength="1"><input type="password" maxlength="1"><input type="password" maxlength="1">', ma: { login: false, partial: true, card: false } },
  { n: 'strona z tekstem o 16-cyfrowym numerze i datami (bez znakow hasla)', h: '<p>Your 16-digit card number is on the front</p><select><option>1</option></select><select><option>2</option></select>', ma: { login: false, partial: false, card: false } },
  { n: 'polski bank: wpisz cyfre nr 3 kodu PIN', h: '<p>Wpisz cyfrę nr 3 i 5 kodu PIN</p><input maxlength="1"><input maxlength="1">', ma: { login: false, partial: true, card: false } },
  { n: 'polski bank: znaki hasla nr: 2, 5, 8', h: '<p>Wpisz znaki hasła nr: 2, 5, 8</p><input type="password" maxlength="1"><input type="password" maxlength="1"><input type="password" maxlength="1">', ma: { login: false, partial: true, card: false } },
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
  await page.setContent('<!doctype html><html><body>' + s.h + '</body></html>');
  const r = JSON.parse(await page.evaluate(script));
  const pass = r.login === s.ma.login && r.partial === s.ma.partial && r.card === s.ma.card; ok &&= pass;
  console.log(`${pass ? 'PASS' : 'FAIL'} ${s.n}: ${JSON.stringify(r)}${pass ? '' : ' oczekiwane ' + JSON.stringify(s.ma)}`);
}
await browser.close();
process.exit(ok ? 0 : 1);
