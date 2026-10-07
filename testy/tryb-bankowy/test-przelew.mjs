// Test wypelniania przelewu z bazy trybu bankowego (TransferFillScript z src/Bank.cs): rozpoznanie formularza przelewu
// (nie logowania numerem konta) i wpisanie odbiorcy, numeru, sort code, kwoty i tytulu w wlasciwe pola.
// Uruchom: node testy/tryb-bankowy/test-przelew.mjs
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }

const src = readFileSync(new URL('../../src/Bank.cs', import.meta.url), 'utf8');
const m = src.match(/const string TransferFillScript = @"([\s\S]*?)(?<!")";\s*\n/);
if (!m) { console.log('FAIL nie znaleziono TransferFillScript'); process.exit(1); }
const script = m[1].replace(/""/g, '"');
const run = d => script.replace('__D__', JSON.stringify(d));

const pl = '<label for=a>Rachunek odbiorcy</label><input id=a maxlength=32><label for=n>Nazwa odbiorcy</label><input id=n>' +
  '<label for=ad>Adres odbiorcy</label><input id=ad><label for=k>Kwota</label><input id=k><label for=t>Tytuł przelewu</label><input id=t>';
const plIban = '<label for=a>IBAN odbiorcy</label><input id=a><label for=n>Nazwa odbiorcy</label><input id=n><label for=k>Kwota</label><input id=k>';
const uk = '<label for=p>Payee name</label><input id=p><input aria-label="Sort code first two digits" maxlength=2><input aria-label="Sort code middle two digits" maxlength=2>' +
  '<input aria-label="Sort code last two digits" maxlength=2><label for=an>Account number</label><input id=an maxlength=8>' +
  '<label for=r>Reference</label><input id=r maxlength=18><label for=am>Amount</label><input id=am>';
const ukLogin = '<label for=s>Sort code</label><input id=s><label for=an>Account number</label><input id=an><input type=password name=pass>';
const card = '<input autocomplete=cc-number><input autocomplete=cc-exp><input autocomplete=cc-csc>';

const plData = { number: 'PL61 1090 1014 0000 0712 1981 2874', sort: '', name: 'Jan Kowalski', amount: '120,50', ref: 'Czynsz 10/2026' };
const ukData = { number: 'GB82WEST12345698765432', sort: '123456', name: 'Acme Ltd', amount: '45.00', ref: 'INV-77' };
const przypadki = [
  { n: 'PL: przelew krajowy (NRB bez PL, adres nietkniety)', h: pl, d: plData, licz: 1, ma: ['61109010140000071219812874', 'Jan Kowalski', '', '120,50', 'Czynsz 10/2026'] },
  { n: 'PL: pole IBAN - pelny numer', h: plIban, d: plData, licz: 1, ma: ['PL61 1090 1014 0000 0712 1981 2874', 'Jan Kowalski', '120,50'] },
  { n: 'UK: sort code w 3 polach, 8 cyfr konta z IBAN GB', h: uk, d: ukData, licz: 1, ma: ['Acme Ltd', '12', '34', '56', '98765432', 'INV-77', '45.00'] },
  { n: 'UK: logowanie numerem konta - to nie przelew', h: ukLogin, d: ukData, licz: 0 },
  { n: 'platnosc karta - to nie przelew', h: card, d: ukData, licz: 0 },
];

const browser = await pw.chromium.launch();
const page = await browser.newPage();
let ok = true;
for (const s of przypadki) {
  await page.setContent('<!doctype html><html><body>' + s.h + '</body></html>');
  const licz = parseInt(await page.evaluate(run({ count: true })), 10);
  let pass = licz === s.licz, opis = 'pola rachunku: ' + licz;
  if (pass && s.ma) {
    await page.evaluate(run(s.d));
    const wart = await page.evaluate(() => [...document.querySelectorAll('input')].filter(e => e.type !== 'password').map(e => e.value));
    pass = JSON.stringify(wart) === JSON.stringify(s.ma);
    opis += ' wpisano ' + JSON.stringify(wart) + (pass ? '' : ' oczekiwane ' + JSON.stringify(s.ma));
  }
  ok &&= pass;
  console.log(`${pass ? 'PASS' : 'FAIL'} ${s.n}: ${opis}`);
}
// dane tylko na stronie, dla ktorej je wybrano
await page.setContent('<!doctype html><html><body>' + pl + '</body></html>');
const r = await page.evaluate(run({ ...plData, h: 'inny-bank.example' }));
const pusto = await page.evaluate(() => [...document.querySelectorAll('input')].every(e => e.value === ''));
const p2 = r === '0' && pusto; ok &&= p2;
console.log(`${p2 ? 'PASS' : 'FAIL'} inna strona (h) - nic nie wpisano`);
await browser.close();
process.exit(ok ? 0 : 1);
