// Ekstraktor tekstu z PDF (PdfExtractHtml z src/PdfReader.cs) na prawdziwym PDF z polskim tekstem: tytul z metadanych,
// akapity rozdzielone, slowo przeniesione myslnikiem sklejone, tekst z 2 stron po kolei. PDF generuje reportlab.
// Uruchom: node testy/pdf/test-tekst-pdf.mjs   (wymaga python3 + reportlab)
import { createRequire } from 'node:module';
import { mkdtempSync, writeFileSync, copyFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { createServer } from 'node:http';
import { skrypt } from '../wspolne/skrypt-cs.mjs';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }

const dir = mkdtempSync(join(tmpdir(), 'velivo-pdf-'));
const src = new URL('../../src/PdfJs/', import.meta.url);
for (const f of ['pdf.min.mjs', 'pdf.worker.min.mjs']) copyFileSync(new URL(f, src), join(dir, f));
writeFileSync(join(dir, 'czytaj.html'), skrypt('PdfExtractHtml'));
execFileSync('python3', ['-I', new URL('./generuj-pdf.py', import.meta.url).pathname, join(dir, 'dokument.pdf')]);

const typy = { '.html': 'text/html', '.mjs': 'text/javascript', '.pdf': 'application/pdf' };
const srv = createServer((req, res) => {
  const name = decodeURIComponent(req.url.split('?')[0]).replace(/^\/+/, '');
  try { const b = readFileSync(join(dir, name)); res.writeHead(200, { 'content-type': typy[name.slice(name.lastIndexOf('.'))] || 'application/octet-stream' }); res.end(b); }
  catch { res.writeHead(404); res.end(); }
});
await new Promise(r => srv.listen(0, '127.0.0.1', r));
const base = `http://127.0.0.1:${srv.address().port}/`;

const browser = await pw.chromium.launch();
const page = await browser.newPage();
await page.addInitScript(() => { window.chrome = { webview: { postMessage: m => { window.__wynik = m; } } }; });
await page.goto(base + 'czytaj.html');
await page.waitForFunction(() => window.__wynik, null, { timeout: 30000 });
const w = JSON.parse(await page.evaluate(() => window.__wynik));
await browser.close(); srv.close(); rmSync(dir, { recursive: true, force: true });

let bledy = 0;
const spr = (ok, co) => { console.log((ok ? 'OK  ' : 'BŁĄD') + ' ' + co); if (!ok) bledy++; };
spr(!w.error, 'bez błędu (' + (w.error || '') + ')');
spr(w.title === 'Sprawozdanie roczne Velivo', 'tytuł z metadanych: ' + w.title);
spr(w.pages === 2, 'dwie strony: ' + w.pages);
const akapity = (w.text || '').split('\n\n');
spr(akapity.length >= 3, 'akapity rozdzielone (' + akapity.length + ')');
spr(/Przeglądarka Velivo działa szybko i chroni prywatność użytkownika/.test(w.text), 'polskie znaki w tekście');
spr(/bezpieczeństwo/.test(w.text) && !/bezpie-\s*czeństwo/.test(w.text), 'słowo przeniesione myślnikiem sklejone');
spr(w.text.indexOf('Pierwszy akapit') < w.text.indexOf('Druga strona'), 'strony po kolei');
spr(!/\n\n[^\n]{0,20}\n\n/.test('\n\n' + w.text), 'brak pustych/śmieciowych akapitów');
console.log(bledy ? `\n${bledy} błędów` : '\nWszystko OK');
process.exit(bledy ? 1 : 0);
