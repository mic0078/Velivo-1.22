// Tryb strony w silniku karty (TabColorCommands z src/DarkMode.cs): przy ciemnym motywie Windows strona z wlasnym
// ciemnym wygladem (jak Google Wiadomosci) ma byc JASNA w trybie jasnym i CIEMNA (bez odwracania) w ciemnym;
// zwykla jasna strona w trybie ciemnym przyciemniona przez silnik. Uruchom: node testy/ciemny-tryb/test-karta.mjs
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }
// polecenia dokladnie z kodu Velivo (C#): new[] { "Metoda", "parametry" }
const src = readFileSync(new URL('../../src/DarkMode.cs', import.meta.url), 'utf8');
const body = src.slice(src.indexOf('TabColorCommands(bool dark)'), src.indexOf('ApplyTabColorMode(BrowserTab'));
const cmds = dark => [...body.matchAll(/new\[\] \{ "([\w.]+)", (.+?) \},/g)].map(m => {
  const expr = m[2].replace(/\(dark \? ("[^"]*") : ("[^"]*")\)/g, (_, a, b) => dark ? a : b);
  return [m[1], JSON.parse(eval(expr))]; });
const google = '<!doctype html><meta name=color-scheme content="light dark"><style>body{background:#fff;color:#202124;margin:0;height:100vh}@media (prefers-color-scheme:dark){body{background:#202124;color:#e8eaed}}</style><body><h1>Google Wiadomosci</h1></body>';
const zwykla = '<!doctype html><style>body{background:#fff;color:#000;margin:0;height:100vh}</style><body><h1>Zwykla strona</h1></body>';
const browser = await pw.chromium.launch();
const ctx = await browser.newContext({ colorScheme: 'dark' });   // ciemny motyw Windows / profil
let ok = true; const check = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) ok = false; };
async function jasnosc(html, dark) {
  const page = await ctx.newPage(); const c = await ctx.newCDPSession(page);
  for (const [m, p] of cmds(dark)) await c.send(m, p);
  await page.goto('data:text/html,' + encodeURIComponent(html));   // tryb ustawiony przed wczytaniem - jak przy nawigacji w Velivo
  const png = await page.screenshot({ clip: { x: 300, y: 300, width: 1, height: 1 } });
  const px = await page.evaluate(async b64 => { const i = new Image(); i.src = 'data:image/png;base64,' + b64; await i.decode(); const cv = document.createElement('canvas'); cv.width = cv.height = 1; const x = cv.getContext('2d'); x.drawImage(i, 0, 0); const d = x.getImageData(0, 0, 1, 1).data; return (0.299 * d[0] + 0.587 * d[1] + 0.114 * d[2]) / 255; }, png.toString('base64'));
  await page.close(); return px;
}
check(cmds(true).length === 2 && cmds(false)[0][1].features[0].value === 'light', 'polecenia z kodu Velivo: ' + JSON.stringify(cmds(false)));
check(await jasnosc(google, false) > 0.8, 'Google przy ciemnym Windows, tryb JASNY - strona jasna');
check(await jasnosc(google, true) < 0.3, 'Google, tryb CIEMNY - wlasny ciemny wyglad (nie jasnoszary)');
check(await jasnosc(zwykla, true) < 0.3, 'zwykla jasna strona, tryb CIEMNY - przyciemniona przez silnik');
check(await jasnosc(zwykla, false) > 0.8, 'zwykla strona, tryb JASNY - jasna');
await browser.close();
console.log(ok ? 'WSZYSTKO OK' : 'SA BLEDY'); process.exit(ok ? 0 : 1);
