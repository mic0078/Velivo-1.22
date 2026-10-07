// Test czytania na glos (ReaderScript z src/ReadAloud.cs + wspolny wykrywacz artykulu): czyta sam artykul (bez menu,
// polecanych, podpisow zdjec, "czytaj tez"), akapit angielski glosem angielskim, reaguje na zmiane glosu w Ustawieniach
// i konczy, gdy strona przejdzie na inny artykul. Uruchom: node testy/czytanie/test-czytnik.mjs
import { createRequire } from 'node:module';
import { skrypt } from '../wspolne/skrypt-cs.mjs';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }
const reader = skrypt('ReaderScript');

const fake = `(() => {
  const all = [{ name: 'Wybrany', lang: 'pl-PL' }, { name: 'Inny polski', lang: 'pl-PL' }, { name: 'English Natural', lang: 'en-GB' }];
  const spoken = []; window.__spoken = spoken;
  const S = { onvoiceschanged: null, getVoices() { return all.slice(); }, cancel() { S.gen++; }, gen: 0,
    speak(u) { spoken.push({ t: u.text, v: u.voice ? u.voice.name : '-' }); const g = S.gen; setTimeout(() => { if (g === S.gen && u.onend) u.onend(); }, 15); } };
  Object.defineProperty(window, 'speechSynthesis', { value: S, configurable: true });
  window.SpeechSynthesisUtterance = function (t) { this.text = t; };
})();`;

const pl1 = 'Iga Świątek pokonała rywalkę w dwóch setach i awansowała do półfinału turnieju w Pekinie, gdzie zagra w sobotę.';
const pl2 = 'Polka nie straciła w tym meczu ani jednego gema przy swoim serwisie, co jest jej najlepszym wynikiem w sezonie.';
const en1 = 'This is one of the best performances of the season and the crowd in Beijing was on its feet for the whole match.';
const html = `<!doctype html><html lang="pl"><body>
<div class="top-menu"><a href=#>Pogoda</a> <a href=#>Sport</a> <a href=#>Biznes</a></div>
<div class="layout"><div class="td-post-content">
  <h1>Świątek w półfinale China Open</h1>
  <p>${pl1}</p>
  <figure><img src="data:," width=10 height=10><figcaption>Fot. Agencja Foto / Iga Świątek na korcie centralnym</figcaption></figure>
  <div class="read-more-box"><p>Czytaj też: inna historia o czymś, co nie ma związku z tym meczem</p></div>
  <p>${pl2}</p>
  <blockquote><p>${en1}</p></blockquote>
  <ul class="related-links"><li><a href=#>Polecany artykuł numer jeden o czymś innym</a></li><li><a href=#>Polecany artykuł numer dwa</a></li></ul>
</div><div class="sidebar"><p>Najpopularniejsze: lista artykułów, które nie mają nic wspólnego z tym tekstem, kliknij i czytaj.</p></div></div>
<footer><p>Copyright 2026 Portal sportowy, wszystkie prawa zastrzeżone, regulamin serwisu i polityka prywatności.</p></footer></body></html>`;

const browser = await pw.chromium.launch(); const page = await browser.newPage(); let ok = true;
const sprawdz = (n, pass, opis) => { ok &&= pass; console.log(`${pass ? 'PASS' : 'FAIL'} ${n}${opis ? ': ' + opis : ''}`); };
async function czytaj(pre) {
  await page.route('https://portal.example/**', r => r.fulfill({ contentType: 'text/html; charset=utf-8', body: html }));
  await page.goto('https://portal.example/sport/swiatek');
  await page.evaluate(fake); await page.evaluate(reader);
  if (pre) await pre();
}

// 1-2: co i jakim glosem
await czytaj();
await page.evaluate(() => window.__velivoRead.start(false, 1, 'Wybrany'));
await page.waitForTimeout(1200);
let s = await page.evaluate(() => window.__spoken);
const tekst = s.map(x => x.t).join(' ');
sprawdz('czyta tytul i artykul', tekst.includes('Świątek w półfinale') && tekst.includes('Iga Świątek pokonała') && tekst.includes('ani jednego gema'));
sprawdz('bez menu, podpisu zdjecia, "czytaj tez", polecanych, bocznej kolumny i stopki',
  !/Pogoda|Fot\.|Czytaj też|Polecany|Najpopularniejsze|Copyright/.test(tekst), tekst.slice(0, 400));
const enGlos = s.filter(x => /performances|crowd/.test(x.t)).map(x => x.v), plGlos = s.filter(x => /Świątek|gema/.test(x.t)).map(x => x.v);
sprawdz('akapit angielski - glos angielski, polski - wybrany', enGlos.length > 0 && enGlos.every(v => v === 'English Natural') && plGlos.every(v => v === 'Wybrany'), JSON.stringify({ enGlos, plGlos }));

// 3: zmiana glosu w Ustawieniach w trakcie czytania
await czytaj();
await page.evaluate(() => window.__velivoRead.start(false, 0.01, 'Wybrany'));   // wolno - zdanie "trwa" do zmiany
await page.evaluate(() => { const S = speechSynthesis; S.speak = function (u) { window.__spoken.push({ t: u.text, v: u.voice ? u.voice.name : '-' }); }; });
await page.evaluate(() => window.__velivoRead.config(1, 'Inny polski', 0.5));
s = await page.evaluate(() => window.__spoken);
sprawdz('zmiana glosu w Ustawieniach dziala od biezacego zdania', s.length >= 1 && s[s.length - 1].v === 'Inny polski', JSON.stringify(s.slice(-2)));

// 4: strona przeszla na inny artykul (bez przeladowania) - koniec czytania
await czytaj();
await page.evaluate(() => window.__velivoRead.start(false, 1, 'Wybrany'));
await page.evaluate(() => history.pushState({}, '', '/sport/inny-artykul'));
await page.waitForTimeout(400);
const st = JSON.parse(await page.evaluate(() => window.__velivoRead.state()));
s = await page.evaluate(() => window.__spoken);
sprawdz('inny artykul na stronie - czytanie konczy sie', st.active === false && s.length <= 2, 'przeczytano ' + s.length + ' fragm.');

await browser.close(); process.exit(ok ? 0 : 1);
