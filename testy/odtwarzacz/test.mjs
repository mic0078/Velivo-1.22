// Test odtwarzacza filmow z dysku (PlayerHtml z src/VideoPlayer.cs) w Chromium: film z pliku gra, ustawienia z adresu
// (autoodtwarzanie, powtarzanie, wznawianie) dzialaja, adres spoza dysku jest odrzucany. Uruchom: node testy/odtwarzacz/test.mjs
import { createRequire } from 'node:module';
import { writeFileSync, mkdtempSync, copyFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { skrypt } from '../wspolne/skrypt-cs.mjs';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }
const dir = mkdtempSync(join(tmpdir(), 'velivo-odtw-'));
const player = join(dir, 'odtwarzacz.html');
writeFileSync(player, skrypt('PlayerHtml').replace('{ERR}', 'BLAD-FORMATU'));
const film = join(dir, 'film #1.webm');   // spacja i # w nazwie jak w prawdziwych plikach
copyFileSync(new URL('film.webm', import.meta.url), film);   // 20-sekundowy film testowy (ffmpeg testsrc)
const url = (o, v) => pathToFileURL(player).href + '#a=' + o.a + '&r=' + o.r + '&l=' + o.l + '&v=' + encodeURIComponent(v);
const filmUrl = pathToFileURL(film).href;   // jak w C#: "#" w nazwie zakodowane jako %23
const browser = await pw.chromium.launch({ args: ['--autoplay-policy=no-user-gesture-required'] });
let ok = true; const check = (c, m) => { console.log((c ? 'OK   ' : 'BLAD ') + m); if (!c) ok = false; };
const page = await browser.newPage();

await page.goto(url({ a: 1, r: 1, l: 1 }, filmUrl));
await page.waitForFunction(() => { const v = document.getElementById('v'); return v.readyState >= 2 && !v.paused; }, null, { timeout: 10000 }).catch(() => {});
let st = await page.evaluate(() => { const v = document.getElementById('v'); return { paused: v.paused, loop: v.loop, dur: v.duration, title: document.title, err: getComputedStyle(document.getElementById('err')).display }; });
check(!st.paused && st.err === 'none', 'film z dysku gra od razu (autoodtwarzanie wlaczone)');
check(st.loop === true, 'powtarzanie z ustawien');
check(st.title === 'film #1.webm', 'tytul karty = nazwa pliku: ' + st.title);
check(Math.round(st.dur) === 20, 'dlugosc filmu 20 s');

// wznawianie: przewin na 12 s, wyjdz, otworz ponownie - film startuje od 12 s
await page.evaluate(() => { const v = document.getElementById('v'); v.currentTime = 12; v.pause(); });
await page.waitForTimeout(300);
await page.goto('about:blank');
await page.goto(url({ a: 0, r: 1, l: 0 }, filmUrl));
await page.waitForFunction(() => document.getElementById('v').readyState >= 1, null, { timeout: 10000 });
await page.waitForTimeout(300);
st = await page.evaluate(() => { const v = document.getElementById('v'); return { t: v.currentTime, paused: v.paused, loop: v.loop }; });
check(Math.abs(st.t - 12) < 1, 'wznawia od miejsca, w ktorym skonczyles: ' + st.t.toFixed(1) + ' s');
check(st.paused, 'bez autoodtwarzania film czeka na klik');
check(st.loop === false, 'powtarzanie wylaczone');

// skroty: spacja gra, strzalka w prawo +5 s
await page.keyboard.press('Space'); await page.waitForTimeout(200);
check(!(await page.evaluate(() => document.getElementById('v').paused)), 'spacja wlacza odtwarzanie');
await page.keyboard.press('Space');
const t0 = await page.evaluate(() => document.getElementById('v').currentTime);
await page.keyboard.press('ArrowRight'); await page.waitForTimeout(100);
const t1 = await page.evaluate(() => document.getElementById('v').currentTime);
check(Math.abs(t1 - t0 - 5) < 0.6, 'strzalka w prawo = +5 s');

// wznawianie wylaczone - od poczatku
await page.goto('about:blank');
await page.goto(url({ a: 0, r: 0, l: 0 }, filmUrl));
await page.waitForFunction(() => document.getElementById('v').readyState >= 1, null, { timeout: 10000 });
check((await page.evaluate(() => document.getElementById('v').currentTime)) < 1, 'bez wznawiania film zaczyna od poczatku');

// bezpieczenstwo: odtwarzacz nie laduje niczego spoza dysku
await page.goto('about:blank');
await page.goto(url({ a: 1, r: 0, l: 0 }, 'https://example.com/x.mp4'));
st = await page.evaluate(() => ({ src: document.getElementById('v').getAttribute('src'), err: getComputedStyle(document.getElementById('err')).display }));
check(!st.src && st.err === 'block', 'adres z internetu odrzucony');

// zly format - komunikat
const zly = join(dir, 'zly.mp4'); writeFileSync(zly, 'to nie jest film');
await page.goto('about:blank');
await page.goto(url({ a: 1, r: 0, l: 0 }, pathToFileURL(zly).href));
await page.waitForFunction(() => getComputedStyle(document.getElementById('err')).display === 'block', null, { timeout: 10000 }).catch(() => {});
check(await page.evaluate(() => getComputedStyle(document.getElementById('err')).display === 'block'), 'nieobslugiwany plik - komunikat zamiast czarnego ekranu');

await browser.close();
console.log(ok ? 'WSZYSTKO OK' : 'SA BLEDY'); process.exit(ok ? 0 : 1);
