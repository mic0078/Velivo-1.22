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
const url = (o, v) => pathToFileURL(player).href + '#a=' + o.a + '&r=' + o.r + '&l=' + o.l + '&s=sol123&v=' + encodeURIComponent(v);
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

// "Film na wierzchu" z odtwarzacza: w okienku jeden pasek (Velivo), bez drugiego paska odtwarzacza
await page.goto('about:blank');
await page.goto(url({ a: 1, r: 0, l: 0 }, filmUrl));
await page.waitForFunction(() => document.getElementById('v').readyState >= 1, null, { timeout: 10000 });
await page.evaluate(skrypt('FloatPageScript') + '(0)');
await page.waitForTimeout(1200);
st = await page.evaluate(() => ({ controls: document.getElementById('v').controls, bars: document.querySelectorAll('#velivo-seek').length }));
check(!st.controls && st.bars === 1, 'okienko Na wierzchu: tylko pasek Velivo (pasek odtwarzacza ukryty: ' + !st.controls + ')');

// okienko Na wierzchu: przewijanie -10/+10 s, pauza, glosnosc (suwak, wycisz, kolko, strzalki)
await page.mouse.move(200, 200); await page.waitForTimeout(100);
const has = await page.evaluate(() => ['back','fwd','play','mute'].every(a => document.querySelector('#velivo-seek [data-a=' + a + ']')) && !!document.getElementById('velivo-vol'));
check(has, 'okienko Na wierzchu: przyciski -10 s, +10 s, pauza, wycisz i suwak glosnosci');
if (has) {
  const tA = await page.evaluate(() => { const v = document.getElementById('v'); v.currentTime = 5; return v.currentTime; });
  await page.evaluate(() => document.querySelector('#velivo-seek [data-a=fwd]').click());
  const tB = await page.evaluate(() => document.getElementById('v').currentTime);
  await page.evaluate(() => document.querySelector('#velivo-seek [data-a=back]').click());
  const tC = await page.evaluate(() => document.getElementById('v').currentTime);
  check(Math.abs(tB - tA - 10) < 0.8 && Math.abs(tC - tA) < 0.8, 'przewijanie +10 s / -10 s: ' + tA.toFixed(0) + ' -> ' + tB.toFixed(0) + ' -> ' + tC.toFixed(0));
  await page.evaluate(() => { const r = document.getElementById('velivo-vol'); r.value = 0.3; r.dispatchEvent(new Event('input', { bubbles: true })); });
  check(Math.abs((await page.evaluate(() => document.getElementById('v').volume)) - 0.3) < 0.01, 'suwak glosnosci ustawia glosnosc');
  await page.evaluate(() => document.querySelector('#velivo-seek [data-a=mute]').click());
  check(await page.evaluate(() => document.getElementById('v').muted), 'przycisk wycisz');
  await page.evaluate(() => document.querySelector('#velivo-seek [data-a=mute]').click());
  await page.keyboard.press('ArrowUp');
  check(Math.abs((await page.evaluate(() => document.getElementById('v').volume)) - 0.35) < 0.01, 'strzalka w gore = glosniej');
  const k0 = await page.evaluate(() => document.getElementById('v').currentTime);
  await page.keyboard.press('ArrowRight');
  check(Math.abs((await page.evaluate(() => document.getElementById('v').currentTime)) - k0 - 10) < 0.6, 'strzalka w prawo w okienku = +10 s');
  const box = await page.evaluate(() => { const r = document.getElementById('velivo-vol').getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; });
  await page.mouse.move(box.x, box.y); await page.mouse.wheel(0, -100); await page.waitForTimeout(100);
  check(Math.abs((await page.evaluate(() => document.getElementById('v').volume)) - 0.4) < 0.01, 'kolko nad glosnoscia = glosniej o 5%');
  const p0 = await page.evaluate(() => document.getElementById('v').paused);
  await page.evaluate(() => document.querySelector('#velivo-seek [data-a=play]').click()); await page.waitForTimeout(400);
  const p1 = await page.evaluate(() => document.getElementById('v').paused);
  await page.evaluate(() => document.querySelector('#velivo-seek [data-a=play]').click()); await page.waitForTimeout(400);
  const p2s = await page.evaluate(() => document.getElementById('v').paused);
  check(p1 !== p0 && p2s === p0, 'przycisk odtwarzaj/pauza przelacza (' + p0 + ' -> ' + p1 + ' -> ' + p2s + ')');
}

// pelny ekran okienka: przycisk, F, dwuklik -> prosba do Velivo; Esc -> wyjscie
await page.evaluate(() => { window.__msgs = []; window.chrome = { webview: { postMessage(m) { window.__msgs.push(m); } } }; });
await page.evaluate(() => document.querySelector('#velivo-seek [data-a=full]').click());
await page.keyboard.press('f'); await page.keyboard.press('Escape');
await page.mouse.dblclick(200, 100);
const msgs = await page.evaluate(() => window.__msgs.filter(m => m.startsWith('velivo-float-full')));
check(JSON.stringify(msgs) === JSON.stringify(['velivo-float-full', 'velivo-float-full', 'velivo-float-full:0', 'velivo-float-full']), 'pelny ekran okienka: przycisk, F, Esc, dwuklik ' + JSON.stringify(msgs));

// powrot z okienka do karty: miejsce filmu w adresie (&t=) ma pierwszenstwo - takze przy wylaczonym wznawianiu
await page.goto('about:blank');
await page.goto(url({ a: 0, r: 0, l: 0 }, filmUrl).replace('&v=', '&t=14&v='));
await page.waitForFunction(() => document.getElementById('v').readyState >= 1, null, { timeout: 10000 });
await page.waitForTimeout(300);
check(Math.abs((await page.evaluate(() => document.getElementById('v').currentTime)) - 14) < 1, 'powrot z okienka Na wierzchu - film od tego samego miejsca');

// przyciski nad filmem z dysku: bez "Pobierz" (plik juz jest na dysku), "Na wierzchu" i "Obraz w obrazie" sa
const p2 = await browser.newPage({ viewport: { width: 900, height: 600 } });
await p2.addInitScript(() => { const o = Element.prototype.attachShadow; Element.prototype.attachShadow = function (i) { return o.call(this, { ...i, mode: 'open' }); }; window.chrome = { webview: { postMessage() {} } }; });
await p2.addInitScript('(function(C){' + skrypt('PageScriptBody') + '})(' + JSON.stringify({ token: 'T', pip: true, dlBtn: true, dlLabel: 'Pobierz', floatLabel: 'Na wierzchu', pipLabel: 'Obraz w obrazie' }) + ')');
await p2.goto(url({ a: 0, r: 0, l: 0 }, filmUrl));
await p2.waitForFunction(() => document.getElementById('v').readyState >= 1, null, { timeout: 10000 });
await p2.mouse.move(450, 300); await p2.waitForTimeout(300); await p2.mouse.move(460, 310); await p2.waitForTimeout(300);
const btns = await p2.evaluate(() => { const h = [...document.documentElement.children].find(x => x.shadowRoot); return h ? [...h.shadowRoot.querySelectorAll('button')].map(b => b.getAttribute('data-a')) : []; });
check(btns.includes('float') && !btns.includes('dl'), 'film z dysku: przyciski ' + JSON.stringify(btns) + ' - bez Pobierz');
await p2.close();

// okienko Na wierzchu z filmem ze strony WWW (YouTube): ten sam kompaktowy pasek, pauza przez odtwarzacz YouTube
const p3 = await browser.newPage({ viewport: { width: 480, height: 270 } });
const filmData = (await import('node:fs')).readFileSync(new URL('film.webm', import.meta.url));
await p3.route('https://www.youtube.com/**', r => r.request().url().endsWith('.webm') ? (() => { const m = /bytes=(\d+)-(\d*)/.exec(r.request().headers()['range'] || ''); const st = m ? +m[1] : 0, en = m && m[2] ? +m[2] : filmData.length - 1;
    return r.fulfill({ status: m ? 206 : 200, headers: { 'Content-Type': 'video/webm', 'Accept-Ranges': 'bytes', 'Content-Length': String(en - st + 1), ...(m ? { 'Content-Range': 'bytes ' + st + '-' + en + '/' + filmData.length } : {}) }, body: filmData.subarray(st, en + 1) }); })()
  : r.fulfill({ contentType: 'text/html; charset=utf-8', body: '<!doctype html><html><body><div id=masthead>YouTube</div><div id=movie_player><video src=/f.webm muted></video></div><div id=comments>komentarze</div><script>var mp=document.getElementById("movie_player"),vv=mp.querySelector("video");window.ytCalls=[];mp.playVideo=function(){ytCalls.push("play");vv.play();};mp.pauseVideo=function(){ytCalls.push("pause");vv.pause();};</script></body></html>' }));
await p3.goto('https://www.youtube.com/watch?v=x');
await p3.waitForFunction(() => document.querySelector('video').readyState >= 1, null, { timeout: 10000 });
await p3.evaluate(skrypt('FloatPageScript') + '(0)');
await p3.waitForTimeout(1500);
await p3.mouse.move(240, 120); await p3.waitForTimeout(100);
const yt = await p3.evaluate(() => { const v = document.querySelector('video'); const bar = document.getElementById('velivo-seek');
  const vis = getComputedStyle(document.getElementById('comments')).visibility;
  return { bars: document.querySelectorAll('#velivo-seek').length, on: bar.classList.contains('on'), btns: ['back','fwd','play','mute'].filter(a => bar.querySelector('[data-a=' + a + ']')).length, vol: !!document.getElementById('velivo-vol'), comments: vis }; });
check(yt.bars === 1 && yt.on && yt.btns === 4 && yt.comments === 'hidden', 'YouTube w okienku: kompaktowy pasek z przyciskami, reszta strony ukryta ' + JSON.stringify(yt));
await p3.evaluate(() => { document.querySelector('video').currentTime = 3; });
await p3.evaluate(() => document.querySelector('#velivo-seek [data-a=fwd]').click());
const ytT = await p3.evaluate(() => [document.querySelector('video').currentTime, document.querySelector('video').duration, document.querySelector('video').seekable.length]);
check(Math.abs(ytT[0] - 13) < 0.8, 'YouTube w okienku: +10 s ' + JSON.stringify(ytT));
const before = await p3.evaluate(() => document.querySelector('video').paused);
await p3.evaluate(() => document.querySelector('#velivo-seek [data-a=play]').click()); await p3.waitForTimeout(300);
const calls = await p3.evaluate(() => window.ytCalls.slice(-1)[0]);
check(calls === (before ? 'play' : 'pause'), 'YouTube w okienku: pauza/odtwarzanie przez odtwarzacz YouTube (' + calls + ')');
await p3.evaluate(() => { const r = document.getElementById('velivo-vol'); r.value = 0.6; r.dispatchEvent(new Event('input', { bubbles: true })); });
check(Math.abs((await p3.evaluate(() => document.querySelector('video').volume)) - 0.6) < 0.01, 'YouTube w okienku: glosnosc');
await p3.close();

// prywatnosc: inny lokalny plik HTML nie moze odczytac, jakie filmy ogladales (sciezki w pamieci strony)
await page.goto('about:blank');
await page.goto(url({ a: 0, r: 1, l: 0 }, filmUrl));
await page.waitForFunction(() => document.getElementById('v').readyState >= 1, null, { timeout: 10000 });
await page.evaluate(() => { const v = document.getElementById('v'); v.currentTime = 9; v.pause(); });
await page.waitForTimeout(400);
const obcy = join(dir, 'obcy.html'); writeFileSync(obcy, '<!doctype html><script>window.wyciek=JSON.stringify(Object.keys(localStorage).concat(Object.values(localStorage)))</script>');
await page.goto(pathToFileURL(obcy).href);
const wyciek = await page.evaluate(() => window.wyciek);
check(!/film|webm|odtw|velivo-odtw/i.test(wyciek), 'obcy plik HTML nie widzi nazw ani sciezek filmow: ' + wyciek.slice(0, 120));
await page.goto(url({ a: 0, r: 1, l: 0 }, filmUrl));
await page.waitForFunction(() => document.getElementById('v').readyState >= 1, null, { timeout: 10000 }); await page.waitForTimeout(400);
check(Math.abs((await page.evaluate(() => document.getElementById('v').currentTime)) - 9) < 1, 'wznawianie dalej dziala (zaszyfrowany klucz)');

await browser.close();
console.log(ok ? 'WSZYSTKO OK' : 'SA BLEDY'); process.exit(ok ? 0 : 1);
