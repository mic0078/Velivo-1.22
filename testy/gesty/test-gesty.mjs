// Test gestow myszy (PageScriptBody z src/Extras.cs): rozpoznanie ruchow, zwykly prawy klik bez gestu otwiera menu,
// krotki skret 'w dol, potem w prawo' = odswiezenie (nie zamkniecie karty). Uruchom: node testy/gesty/test-gesty.mjs
import { createRequire } from 'node:module';
import { skrypt } from '../wspolne/skrypt-cs.mjs';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }
const body = skrypt('PageScriptBody');
const browser = await pw.chromium.launch(); const page = await browser.newPage(); let ok = true;
await page.setContent('<!doctype html><html><body style="height:2000px"><p>tekst</p></body></html>');
await page.evaluate(() => { window.__m = []; window.chrome = { webview: { postMessage: x => window.__m.push(x) } }; });
await page.evaluate('(function(C){' + body + '})(' + JSON.stringify({ token: 'T', gestures: true }) + ')');
async function gest(punkty) {
  return page.evaluate(p => {
    window.__m.length = 0; const ev = (t, x, y, b, bs) => document.dispatchEvent(new MouseEvent(t, { clientX: x, clientY: y, button: b, buttons: bs, bubbles: true, cancelable: true }));
    ev('mousedown', p[0][0], p[0][1], 2, 2);
    for (const [x, y] of p.slice(1)) ev('mousemove', x, y, 0, 2);
    const last = p[p.length - 1]; ev('mouseup', last[0], last[1], 2, 0);
    const cm = new MouseEvent('contextmenu', { bubbles: true, cancelable: true }); document.dispatchEvent(cm);
    return { m: window.__m.filter(x => x.startsWith('velivo:T:gest:')).map(x => x.slice(14)), menuZablokowane: cm.defaultPrevented };
  }, punkty);
}
const przypadki = [
  ['w lewo = wstecz', [[400, 300], [360, 302], [320, 305], [280, 303]], ['L'], true],
  ['w prawo = dalej', [[300, 300], [340, 300], [380, 298]], ['R'], true],
  ['w gore = nowa karta', [[300, 400], [302, 360], [298, 320]], ['U'], true],
  ['w dol = zamknij karte', [[300, 300], [301, 340], [303, 380]], ['D'], true],
  ['w dol, potem w prawo = odswiez', [[300, 300], [300, 340], [300, 380], [340, 380]], ['DR'], true],
  ['w dol i KROTKO w prawo (20 px) = odswiez, nie zamknij', [[300, 300], [300, 340], [300, 380], [320, 381]], ['DR'], true],
  ['zwykly prawy klik bez ruchu - menu otwiera sie', [[300, 300], [305, 303]], [], false],
];
for (const [n, p, want, blok] of przypadki) {
  const r = await gest(p);
  const pass = JSON.stringify(r.m) === JSON.stringify(want) && r.menuZablokowane === blok; ok &&= pass;
  console.log(`${pass ? 'PASS' : 'FAIL'} ${n}: ${JSON.stringify(r)}`);
}
await browser.close(); process.exit(ok ? 0 : 1);
