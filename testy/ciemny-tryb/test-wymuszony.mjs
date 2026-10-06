// Test skryptu "ciemny-wymuszony" z src/DarkMode.cs (tryb ciemny silnika wlaczony przy starcie).
// Silnik (WebContentsForceDark) NIE przyciemnia strony, gdy jej uzywany schemat kolorow to "dark".
// Skrypt Velivo ma dociemniac tylko takie strony. Gdy strona ma uzywany schemat "light" (np. meta
// "light dark", ale CSS html{color-scheme:light}), silnik juz ja przyciemnil - drugie odwrocenie = jasna strona.
// Uruchom: node testy/ciemny-tryb/test-wymuszony.mjs
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }

const src = readFileSync(new URL('../../src/DarkMode.cs', import.meta.url), 'utf8');
const blok = src.match(/var check = ([\s\S]*?\}\)\(\);");/);
if (!blok) { console.log('FAIL nie znaleziono skryptu check'); process.exit(1); }
const lit = s => [...s.replace(/^\s*\/\/.*$/gm, "").matchAll(/"((?:[^"\\]|\\.)*)"/g)].map(x => JSON.parse('"' + x[1] + '"')).join('');
const [a, b] = blok[1].split(/"\s*\+\s*fix\s*\+\s*"/);
const script = lit(a + '"') + JSON.stringify('html{filter:invert(1)}') + lit('"' + b);

const przypadki = [
  { n: 'meta "light dark" + CSS color-scheme:light (jak Interia) - silnik juz przyciemnil', h: '<meta name=color-scheme content="light dark"><style>html{color-scheme:light}body{background:#fff}</style>', ma: false },
  { n: 'meta "light dark", jasne tlo (silnik pomija) - trzeba dociemnic', h: '<meta name=color-scheme content="light dark"><style>body{background:#fff}</style>', ma: true },
  { n: 'CSS color-scheme:light dark, jasne tlo - trzeba dociemnic', h: '<style>html{color-scheme:light dark}body{background:#fff}</style>', ma: true },
  { n: 'bez deklaracji (silnik przyciemnia sam)', h: '<style>body{background:#fff}</style>', ma: false },
  { n: 'meta "light dark", juz ciemne tlo', h: '<meta name=color-scheme content="light dark"><style>body{background:#111}</style>', ma: false },
];
const browser = await pw.chromium.launch();
const ctx = await browser.newContext({ colorScheme: 'dark' });
let ok = true;
for (const p of przypadki) {
  const page = await ctx.newPage();
  await page.setContent('<!doctype html><html><head>' + p.h + '</head><body>x</body></html>');
  await page.evaluate(script);
  const jest = await page.evaluate(() => !!document.getElementById('velivo-ciemny-wymuszony'));
  const pass = jest === p.ma; ok &&= pass;
  console.log(`${pass ? 'PASS' : 'FAIL'} ${p.n}: dociemnione=${jest}, oczekiwane=${p.ma}`);
}
await browser.close();
process.exit(ok ? 0 : 1);
