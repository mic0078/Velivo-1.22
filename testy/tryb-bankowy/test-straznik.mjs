// Test straznika przelewu (TransferGuardScript z src/Bank.cs): do Velivo trafia tylko numer z pola rachunku -
// nigdy z pola hasla ani karty. Uruchom: node testy/tryb-bankowy/test-straznik.mjs
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }

const src = readFileSync(new URL('../../src/Bank.cs', import.meta.url), 'utf8');
const m = src.match(/const string TransferGuardScript = @"([\s\S]*?)(?<!")";\s*\n/);
if (!m) { console.log('FAIL nie znaleziono TransferGuardScript'); process.exit(1); }
const script = m[1].replace(/""/g, '"').replace('__VT__', 'TOKEN');

const browser = await pw.chromium.launch();
const page = await browser.newPage();
await page.setContent(`<!doctype html><html><body>
<input id="iban" name="beneficiaryIban"><input id="acc" name="accountNumber"><input id="sort" name="sortCode">
<input id="pw" type="password" name="pin"><input id="card" name="cardnumber"><input id="q" type="search" name="q">
<input id="ref" name="reference"></body></html>`);
await page.evaluate(() => { window.__m = []; window.chrome = { webview: { postMessage: x => window.__m.push(x) } }; });
await page.evaluate(script);

async function wpisz(id, value, how) {
  await page.evaluate(([id, value, how]) => { const el = document.getElementById(id); el.value = value; el.dispatchEvent(new Event(how, { bubbles: true })); }, [id, value, how]);
  await page.waitForTimeout(20);
  return page.evaluate(() => window.__m.splice(0));
}
const przypadki = [
  ['wklejony IBAN w polu odbiorcy', 'iban', 'PL61 1090 1014 0000 0712 1981 2874', 'paste', ['velivo:TOKEN:acct:{"v":"PL61109010140000071219812874","hint":true}']],
  ['UK: numer konta (8 cyfr) po wpisaniu', 'acc', '1234 5678', 'change', ['velivo:TOKEN:acct:{"v":"12345678","hint":true}']],
  ['pole hasla / PIN - nigdy', 'pw', '12345678901234', 'change', []],
  ['pole karty - nigdy', 'card', '4111 1111 1111 1111', 'paste', []],
  ['sort code - nie numer rachunku', 'sort', '12-34-56', 'change', []],
  ['tekst w wyszukiwarce - nic', 'q', 'hello world', 'change', []],
  ['tytul przelewu bez cyfr - nic', 'ref', 'czynsz pazdziernik', 'change', []],
];
let ok = true;
for (const [n, id, v, how, want] of przypadki) {
  const got = await wpisz(id, v, how);
  const pass = JSON.stringify(got) === JSON.stringify(want); ok &&= pass;
  console.log(`${pass ? 'PASS' : 'FAIL'} ${n}: ${JSON.stringify(got)}${pass ? '' : ' oczekiwane ' + JSON.stringify(want)}`);
}
await browser.close();
process.exit(ok ? 0 : 1);
