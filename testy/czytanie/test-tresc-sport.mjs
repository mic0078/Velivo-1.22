// Wykrywacz artykulu (ArticleCoreScript z src/ReaderMode.cs) na stronie jak serwis sportowy: caly artykul w bloku, ktorego
// nazwa zawiera "share" / "video" (wyglada jak widget), w srodku wpis z X. Wczesniej: "Za malo tresci do trybu czytania".
// Uruchom: node testy/czytanie/test-tresc-sport.mjs
import { createRequire } from 'node:module';
import { skrypt } from '../wspolne/skrypt-cs.mjs';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }
const core = skrypt('ArticleCoreScript');
const p1 = 'Nikola Bartunkova z wielkim impetem weszła w czwartkowe spotkanie. Kompletnie zdominowała rywalkę i po zaledwie kilkunastu minutach prowadziła już 5:0.';
const p2 = 'Muchova próbowała jeszcze odrobić straty, lecz Bartunkova dopięła swego i zwyciężyła w pierwszym secie 6:3, pokazując bardzo dobry tenis.';
const p3 = 'Wydawało się, że Muchova dorównała poziomem swojej rywalce na początku drugiej partii, jednak Bartunkova błyskawicznie przełamała rodaczkę i wygrała 6:3, 6:0.';
const strona = (klasa) => `<!doctype html><html lang="pl"><head><meta charset="utf-8"></head><body>
<nav><a href=#>Piłka nożna</a> <a href=#>Tenis</a> <a href=#>Siatkówka</a></nav>
<div class="${klasa}"><h1>Bartunkova w półfinale w Pekinie</h1>
 <div class="google-news">Czytaj nas częściej w Google <button>DODAJ W GOOGLE</button></div>
 <p>${p1}</p><p>${p2}</p><p>${p3}</p>
 <blockquote class="twitter-tweet"><p>BIGGEST WIN OF HER CAREER ⭐ Nikola Bartunkova defeats Muchova and advances to the semifinals in Beijing!</p>— wta (@WTA)</blockquote>
 <div class="share-buttons"><a href=#>Facebook</a> <a href=#>X</a></div>
</div>
<aside class="related"><p>Inny artykuł: Świątek trenuje przed turniejem w Wuhan i szykuje się na kolejny mecz w Azji.</p></aside>
</body></html>`;
const browser = await pw.chromium.launch(); let ok = true;
const check = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) ok = false; };
for (const klasa of ['article-content social-share-wrapper', 'video-article__content', 'news-body']) {
  const page = await browser.newPage();
  await page.setContent(strona(klasa));
  const r = await page.evaluate(core + '; (() => { const a = velivoArticle(); return a.blocks.map(b => b.text).join("\\n"); })()');
  check(r.includes('Kompletnie zdominowała') && r.includes('przełamała rodaczkę') && !r.includes('Świątek trenuje') && !r.includes('Piłka nożna'),
    'artykul w bloku "' + klasa + '": cala tresc, bez menu i polecanych (' + r.length + ' znakow)');
  await page.close();
}
await browser.close(); process.exit(ok ? 0 : 1);
