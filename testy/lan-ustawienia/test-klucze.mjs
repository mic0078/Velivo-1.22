// Test: ustawienia zalezne od konkretnego komputera NIE moga przychodzic z drugiego komputera przez LAN.
// Glos czytania (readVoice) to nazwa glosu zainstalowanego w Windows - na innym komputerze moze go nie byc,
// a synchronizacja nadpisywala wybor uzytkownika. Lista blokad pochodzi z src/LanSync.cs.
// Uruchom: node testy/lan-ustawienia/test-klucze.mjs
import { readFileSync } from 'node:fs';
const src = readFileSync(new URL('../../src/LanSync.cs', import.meta.url), 'utf8');
const m = src.match(/LanSettingsBlockedKeys = new HashSet<string>\([^)]*\)\s*\{([\s\S]*?)\};/);
if (!m) { console.log('FAIL nie znaleziono LanSettingsBlockedKeys'); process.exit(1); }
const blocked = new Set([...m[1].matchAll(/"([^"]+)"/g)].map(x => x[1].toLowerCase()));
let ok = true;
for (const [k, why] of [['readVoice', 'glos czytania (glosy Windows sa rozne na kazdym komputerze)'], ['audioOut', 'glosniki'], ['language', 'jezyk interfejsu']]) {
  const p = blocked.has(k.toLowerCase()); ok &&= p;
  console.log(`${p ? 'PASS' : 'FAIL'} ${k} – ${why}: ${p ? 'zostaje na tym komputerze' : 'NADPISYWANY przez drugi komputer'}`);
}
process.exit(ok ? 0 : 1);
