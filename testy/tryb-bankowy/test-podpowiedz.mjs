// Test propozycji pod polem w trybie bankowym (BankFieldHintScript z src/BankHints.cs): rodzaj pola rozpoznany,
// kod SMS i zwykle pola - bez propozycji, do Velivo nigdy nie trafia wartosc pola. Uruchom: node testy/tryb-bankowy/test-podpowiedz.mjs
import { createRequire } from 'node:module';
import { skrypt } from '../wspolne/skrypt-cs.mjs';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }
const s = skrypt('BankFieldHintScript').replace('__VT__', 'T');
const browser = await pw.chromium.launch(); const page = await browser.newPage(); let ok = true;
await page.setContent(`<!doctype html><html><body>
<input id=u name=username value="tajny-login"><input id=p type=password value="tajne-haslo"><input id=z maxlength=1 type=password>
<input id=k autocomplete=cc-number><label for=a>Rachunek odbiorcy</label><input id=a><input id=o autocomplete=one-time-code maxlength=1>
<input id=q type=search name=q><input id=n name=payeeName></body></html>`);
await page.evaluate(() => { window.__m = []; window.chrome = { webview: { postMessage: x => window.__m.push(x) } }; });
await page.evaluate(s);
const przypadki = [['u', 'login', true], ['p', 'login', true], ['z', 'partial', false], ['k', 'card', false], ['a', 'transfer', false], ['o', null], ['q', null], ['n', null]];
for (const [id, want, filled] of przypadki) {
  const m = await page.evaluate(id => { window.__m.length = 0; document.getElementById(id).focus(); const r = window.__m.slice(); document.activeElement.blur(); return r; }, id);
  const f = m.find(x => x.startsWith('velivo:T:bfocus:'));
  const got = f ? JSON.parse(f.slice(16)) : null;
  const pass = want === null ? !got : !!got && got.k === want && got.filled === filled && !/tajn/.test(f);
  ok &&= pass; console.log(`${pass ? 'PASS' : 'FAIL'} pole ${id}: ${f || '(brak propozycji)'}`);
}
await browser.close(); process.exit(ok ? 0 : 1);
