// Test: czytanie na glos uzywa glosu wybranego w ustawieniach, nawet gdy silnik podaje liste glosow
// z opoznieniem (pierwsze speechSynthesis.getVoices() zwraca pusta liste - tak bywa w Chromium/Edge).
// Skrypt czytnika (ReaderScript) jest brany z src/ReadAloud.cs. Uruchom: node testy/czytanie/test-glos.mjs
import { skrypt } from '../wspolne/skrypt-cs.mjs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }

const reader = skrypt('ReaderScript');

// udawany syntezator: lista glosow pojawia sie dopiero po 300 ms (jak w prawdziwej przegladarce)
const fake = `(() => {
  const all = [{ name: 'Domyslny', lang: 'pl-PL', localService: true }, { name: 'Wybrany', lang: 'pl-PL', localService: false }];
  let ready = false; const spoken = [];
  const S = { onvoiceschanged: null, speaking: false, paused: false, pending: false,
    getVoices() { return ready ? all.slice() : []; },
    speak(u) { spoken.push(u.voice ? u.voice.name : '(brak - domyslny systemu)'); },
    cancel() {}, pause() {}, resume() {} };
  Object.defineProperty(window, 'speechSynthesis', { value: S, configurable: true });
  window.SpeechSynthesisUtterance = function (t) { this.text = t; };
  window.__spoken = spoken;
  setTimeout(() => { ready = true; if (S.onvoiceschanged) S.onvoiceschanged(); }, 300);
})();`;

const browser = await pw.chromium.launch();
const page = await browser.newPage();
await page.setContent('<!doctype html><html lang="pl"><body><article><p>Pierwsze zdanie testowe. Drugie zdanie testowe.</p></article></body></html>');
await page.evaluate(fake);
await page.evaluate(reader);
await page.evaluate(() => window.__velivoRead.start(false, 1, 'Wybrany'));
await page.waitForTimeout(2500);
const spoken = await page.evaluate(() => window.__spoken);
await browser.close();
const ok = spoken.length > 0 && spoken[0] === 'Wybrany';
console.log(`${ok ? 'PASS' : 'FAIL'} pierwsze zdanie czytane glosem: ${spoken[0] ?? '(nic nie przeczytano)'} (wybrany w ustawieniach: Wybrany)`);
process.exit(ok ? 0 : 1);
