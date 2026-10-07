// Test efektu wejscia "wyostrzenie" (PageScriptBody z src/Extras.cs): mgielka w kolorze trybu TEJ strony - ciemna, gdy strona
// ma zapamietany tryb ciemny (lub ogolny ciemny), jasna w trybie jasnym - choc strona deklaruje jasne tlo (tryb ciemny Velivo
// przyciemnia ja dopiero przy rysowaniu). Uruchom: node testy/ciemny-tryb/test-wejscie.mjs
import { createRequire } from 'node:module';
import { skrypt } from '../wspolne/skrypt-cs.mjs';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }
const body = skrypt('PageScriptBody');
const browser = await pw.chromium.launch(); let ok = true;
async function mgla(cfg) {
  const page = await browser.newPage();
  await page.route('https://sport.example/**', r => r.fulfill({ contentType: 'text/html; charset=utf-8', body: '<!doctype html><html><body style="background:#fff"><p>Treść</p></body></html>' }));
  await page.addInitScript('(function(C){' + body + '})(' + JSON.stringify({ token: 'T', fade: 0, entrance: 'blur', entMs: 5000, ...cfg }) + ')');
  await page.goto('https://sport.example/');
  const bg = await page.evaluate(() => { const d = [...document.querySelectorAll('div[aria-hidden=true]')].find(x => x.style.zIndex === '2147483646'); return d ? d.style.background : '(brak)'; });
  await page.close(); return bg;
}
const przypadki = [
  ['strona z trybem ciemnym (zapamietanym dla strony)', { darkHosts: ['sport.example'] }, /rgba\(0, 0, 0/],
  ['tryb ciemny ogolny', { darkPages: true }, /rgba\(0, 0, 0/],
  ['tryb ciemny ogolny, ale ta strona ma jasny', { darkPages: true, lightHosts: ['sport.example'] }, /rgba\(255, 255, 255/],
  ['tryb jasny', {}, /rgba\(255, 255, 255/],
];
for (const [n, cfg, want] of przypadki) {
  const bg = await mgla(cfg); const pass = want.test(bg); ok &&= pass;
  console.log(`${pass ? 'PASS' : 'FAIL'} ${n}: ${bg}`);
}
await browser.close(); process.exit(ok ? 0 : 1);
