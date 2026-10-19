// Wykrywacz artykulu (ArticleCoreScript z src/ReaderMode.cs): tytul czytany raz, mimo ze strona powtarza go w zajawce
// (inna wielkosc liter / kropka), i bez ramki ze skrotem artykulu ("Skróć artykuł", podsumowanie).
// Uruchom: node testy/czytanie/test-tytul-raz.mjs
import { createRequire } from 'node:module';
import { skrypt } from '../wspolne/skrypt-cs.mjs';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }
const core = skrypt('ArticleCoreScript');
const tytul = 'Maja Chwalińska przemówiła po tytule w Wuhan';
const p1 = 'Polka pokonała w finale rywalkę w dwóch setach i po raz pierwszy w karierze wygrała turniej rangi WTA 1000, co jest jej największym sukcesem.';
const p2 = 'Po meczu podziękowała trenerowi i rodzinie, a w rozmowie z dziennikarzami zdradziła, jaki cel postawiła sobie na kolejny sezon na korcie.';
const html = `<!doctype html><html lang="pl"><head><meta charset="utf-8"></head><body>
<h1>${tytul}</h1>
<article><p class="lead">${tytul}.</p>
 <div class="ArticleSummary_box"><ul><li>Chwalińska wygrała turniej w Wuhan i to jest jej największy sukces w karierze tenisowej.</li></ul></div>
 <p>${p1}</p><p>${p2}</p></article></body></html>`;
const browser = await pw.chromium.launch(); let ok = true;
const check = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) ok = false; };
const page = await browser.newPage();
await page.setContent(html);
const b = await page.evaluate(core + '; velivoArticle().blocks.map(b => b.text)');
const razy = b.filter(t => t.toLowerCase().replace(/[^\p{L}]+/gu, '').includes(tytul.toLowerCase().replace(/[^\p{L}]+/gu, ''))).length;
check(razy === 1, 'tytul czytany raz (' + razy + 'x): ' + JSON.stringify(b.slice(0, 2)));
check(!b.some(t => t.includes('największy sukces w karierze tenisowej')), 'bez ramki ze skrotem artykulu');
check(b.some(t => t.includes('WTA 1000')) && b.some(t => t.includes('kolejny sezon')), 'cala tresc artykulu');
await browser.close(); process.exit(ok ? 0 : 1);
