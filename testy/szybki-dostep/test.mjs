// Szybki Dostep (strona nowej karty, src/QuickAccessExtension/kod) w Chromium z atrapa Velivo i przegladarki:
// dodawanie, edycja, usuwanie z cofnieciem, grupy, filtrowanie, zapis po ponownym otwarciu, brak bledow JavaScript.
// Uruchom: node testy/szybki-dostep/test.mjs
import { createRequire } from 'node:module'; import { readFileSync } from 'node:fs';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright'); } catch { pw = require(process.env.PLAYWRIGHT_PATH || '/opt/node-tools/node_modules/playwright'); }
const browser = await pw.chromium.launch(); const p = await browser.newPage({ viewport: { width: 1280, height: 800 } });
const bledy = []; p.on('pageerror', (e) => bledy.push('PAGEERROR ' + e.message));
p.on('console', (m) => { if (m.type() === 'error' && !/Failed to load resource|ERR_FILE_NOT_FOUND|net::/.test(m.text())) bledy.push('CONSOLE ' + m.text()); });
const odp = []; p.on('dialog', (d) => d.accept(odp.shift() || ''));
await p.addInitScript(readFileSync(new URL('atrapa.js', import.meta.url), 'utf8'));
// strona dodatku podawana jak przez przegladarke (serwer lokalny), bez internetu: ikony stron nie moga wstrzymac strony
const { createServer } = await import('node:http'); const { extname, join } = await import('node:path');
const kod = new URL('../../src/QuickAccessExtension/kod/', import.meta.url).pathname;
const typy = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.json': 'application/json' };
const serwer = createServer((q, r) => { let dane = null, f = ''; try { f = join(kod, decodeURIComponent(q.url.split('?')[0])); dane = readFileSync(f); } catch { } if (!dane) { r.writeHead(404); r.end(); return; } r.writeHead(200, { 'Content-Type': typy[extname(f)] || 'application/octet-stream' }); r.end(dane); });
await new Promise((ok) => serwer.listen(0, '127.0.0.1', ok));
const baza = 'http://127.0.0.1:' + serwer.address().port + '/';
await p.route((u) => !u.href.startsWith(baza), (r) => r.abort());
const strona = baza + 'newtab.html';
let ok = true; const check = (c, m) => { console.log((c ? 'OK   ' : 'BLAD ') + m); if (!c) ok = false; };
const kafle = () => p.$$eval('#siatka .kafel .nazwa', (a) => a.map((x) => x.textContent.trim()));
const dodaj = async (url, nazwa) => {
  await p.click('#btnDodaj'); await p.fill('#poleUrl', url); await p.fill('#poleNazwa', nazwa); await p.click('#oknoZapisz'); await p.waitForTimeout(400);
};

await p.goto(strona); await p.waitForTimeout(1200);
check(!(await p.isHidden('#pusto')), 'pusta strona: podpowiedz "brak skrotow"');
await dodaj('example.com', 'Przyklad'); await dodaj('https://wikipedia.org', 'Wiki');
check(JSON.stringify(await kafle()) === JSON.stringify(['Przyklad', 'Wiki']), 'dodawanie skrotow: ' + JSON.stringify(await kafle()));
check((await p.$eval('#siatka .kafel', (a) => a.href)).startsWith('https://example.com'), 'adres bez https dostaje https://');

await p.reload(); await p.waitForTimeout(1200);
check(JSON.stringify(await kafle()) === JSON.stringify(['Przyklad', 'Wiki']), 'skroty zostaja po ponownym otwarciu: ' + JSON.stringify(await kafle()));

await p.fill('#szukaj', 'wik'); await p.waitForTimeout(200);
check(JSON.stringify(await kafle()) === JSON.stringify(['Wiki']), 'filtrowanie');
await p.fill('#szukaj', ''); await p.waitForTimeout(200);

await p.hover('#siatka .kafel'); await p.click('#siatka .kafel .akcje button[title=Edytuj]');
await p.fill('#poleNazwa', 'Przyklad 2'); await p.click('#oknoZapisz'); await p.waitForTimeout(400);
check((await kafle())[0] === 'Przyklad 2', 'edycja nazwy');

await p.hover('#siatka .kafel'); await p.click('#siatka .kafel .akcje button[title=Usun]'); await p.waitForTimeout(400);
check(JSON.stringify(await kafle()) === JSON.stringify(['Wiki']), 'usuwanie');
const cofnij = await p.$('#pasek button'); if (cofnij) { await cofnij.click(); await p.waitForTimeout(500); }
check((await kafle()).includes('Przyklad 2'), 'cofniecie usuniecia (kosz)');

odp.push('Praca'); await p.click('#btnGrupa'); await p.waitForTimeout(400);
check((await p.innerText('#grupy')).includes('Praca') && (await kafle()).length === 0, 'nowa grupa (pusta, aktywna)');
await dodaj('github.com', 'GitHub');
await p.reload(); await p.waitForTimeout(1200);
const grupy = await p.innerText('#grupy');
check(grupy.includes('Start') && grupy.includes('Praca'), 'grupy zostaja po ponownym otwarciu: ' + grupy.replace(/\n/g, ' | '));

check(bledy.length === 0, 'brak bledow JavaScript' + (bledy.length ? ':\n  ' + bledy.join('\n  ') : ''));
await browser.close(); serwer.close();
console.log(ok ? 'WSZYSTKO OK' : 'SA BLEDY'); process.exit(ok ? 0 : 1);
