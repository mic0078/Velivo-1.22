// Test "Gdzie ja to czytalem?" (MemoryTextScript z src/Innovations.cs): zapamietywana jest wlasciwa tresc strony,
// bez menu, naglowkow, stopek, reklam i blokow linkow. Uruchom: node testy/pamiec-stron/test-tresc.mjs
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }
const src = readFileSync(new URL('../../src/Innovations.cs', import.meta.url), 'utf8');
const m = src.match(/const string MemoryTextScript = @"([\s\S]*?)(?<!")";\s*\n/);
if (!m) { console.log('FAIL nie znaleziono MemoryTextScript'); process.exit(1); }
const script = m[1].replace(/""/g, '"');
const art = 'Iga Świątek pokonała rywalkę w dwóch setach i awansowała do półfinału turnieju China Open w Pekinie.';
const strony = [
  { n: 'portal: menu, artykul, polecane, stopka', h: `<header><nav><a href=#>Pogoda Poznań</a> <a href=#>Pogoda Wrocław</a> <a href=#>Sport</a></nav></header>
<div class="cookie-banner">Ta strona używa ciasteczek, zgódź się na wszystko, żeby kontynuować czytanie</div>
<article><h1>Świątek w półfinale</h1><p>${art}</p><p>Polka zagra teraz z liderką rankingu, mecz zaplanowano na sobotę o godzinie czternastej.</p>
<div class="related"><p>Polecane: inny artykuł o czymś zupełnie innym niż tenis, kliknij tutaj teraz</p></div></article>
<footer><p>Copyright 2026 Portal, wszystkie prawa zastrzeżone, regulamin, polityka prywatności</p></footer>`,
    ma: [art, 'Świątek w półfinale', 'liderką rankingu'], nie: ['Pogoda Poznań', 'ciasteczek', 'Polecane', 'Copyright'] },
  { n: 'strona bez <article>: lista linkow w tresci pomijana', h: `<div id="main-menu"><ul><li><a href=#>Piłka nożna Tenis Siatkówka Skoki narciarskie</a></li></ul></div>
<div class="content"><p>${art}</p><ul><li><a href=#>Link jeden do innego tekstu o czymś</a> <a href=#>Link dwa do jeszcze innego tekstu</a></li></ul></div>`,
    ma: [art], nie: ['Piłka nożna', 'Link jeden'] },
  { n: 'strona logowania (pole hasla) - nic', h: `<p>${art}</p><input type=password>`, pusto: true },
];
const browser = await pw.chromium.launch(); const page = await browser.newPage(); let ok = true;
for (const s of strony) {
  await page.setContent('<!doctype html><html><body>' + s.h + '</body></html>');
  const t = await page.evaluate(script);
  let pass = s.pusto ? t === '' : s.ma.every(x => t.includes(x)) && s.nie.every(x => !t.includes(x));
  ok &&= pass; console.log(`${pass ? 'PASS' : 'FAIL'} ${s.n}${pass ? '' : ': ' + JSON.stringify(t)}`);
}
await browser.close(); process.exit(ok ? 0 : 1);
