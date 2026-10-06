// Test trybu ciemnego "na zywo" (LiveDarkCss z src/DarkMode.cs) w Chromium.
// Strona jest odwracana (invert), a zdjecia odwracane drugi raz, zeby mialy prawdziwe kolory.
// Zdjecie ma prawdziwe kolory tylko wtedy, gdy liczba odwrocen na drodze od <html> do zdjecia jest PARZYSTA.
// Uruchom: node testy/ciemny-tryb/test.mjs  (wymaga playwright i Chromium)
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }

const src = readFileSync(new URL('../../src/DarkMode.cs', import.meta.url), 'utf8');
const m = src.match(/const string LiveDarkCss = ((?:"(?:[^"\\]|\\.)*"\s*\+?\s*)+);/);
if (!m) { console.log('FAIL nie znaleziono LiveDarkCss'); process.exit(1); }
const css = [...m[1].matchAll(/"((?:[^"\\]|\\.)*)"/g)].map(x => JSON.parse('"' + x[1] + '"')).join('');

const html = `<!doctype html><html><body>
<img id="a" src="data:image/gif;base64,R0lGODlhAQABAAAAACw=">
<picture><source srcset="x.webp"><img id="b" src="data:image/gif;base64,R0lGODlhAQABAAAAACw="></picture>
<div style="background-image:url(x.png)"><img id="c" src="data:image/gif;base64,R0lGODlhAQABAAAAACw="></div>
<div id="d" style="background-image:url(x.png);width:10px;height:10px"></div>
<video id="e"></video>
<p id="t">tekst</p>
</body></html>`;

const browser = await pw.chromium.launch(process.env.CHROMIUM ? { executablePath: process.env.CHROMIUM } : {});
const page = await browser.newPage();
await page.setContent(html);
await page.addStyleTag({ content: css });
const res = await page.evaluate(() => {
  const n = el => { let c = 0; for (let e = el; e && e.nodeType === 1; e = e.parentElement) if (/invert\(1\)/.test(getComputedStyle(e).filter)) c++; return c; };
  return Object.fromEntries(['a', 'b', 'c', 'd', 'e', 't'].map(id => [id, n(document.getElementById(id))]));
});
await browser.close();
const opis = { a: 'zwykle <img>', b: '<img> w <picture>', c: '<img> w elemencie z tlem-obrazkiem', d: 'element z tlem-obrazkiem', e: '<video>' };
let ok = true;
for (const [id, name] of Object.entries(opis)) { const p = res[id] % 2 === 0; ok &&= p; console.log(`${p ? 'PASS' : 'FAIL'} ${name}: odwrocen ${res[id]} (${p ? 'prawdziwe kolory' : 'NEGATYW'})`); }
const t = res.t % 2 === 1; ok &&= t; console.log(`${t ? 'PASS' : 'FAIL'} tekst strony przyciemniony: odwrocen ${res.t}`);
process.exit(ok ? 0 : 1);
