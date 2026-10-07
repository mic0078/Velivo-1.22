// Wyciaga stala skrypt z kodu C# Velivo: const string Nazwa = @"..." + InnaStala + @"..."; - sklada czesci tak jak kompilator.
import { readFileSync, readdirSync } from 'node:fs';
const dir = new URL('../../src/', import.meta.url);
const zrodla = readdirSync(dir).filter(f => f.endsWith('.cs')).map(f => readFileSync(new URL(f, dir), 'utf8')).join('\n');
export function skrypt(nazwa) {
  const start = zrodla.indexOf('const string ' + nazwa + ' = ');
  if (start < 0) throw new Error('nie znaleziono ' + nazwa);
  let i = start + ('const string ' + nazwa + ' = ').length, out = '';
  for (;;) {
    while (/\s/.test(zrodla[i])) i++;
    if (zrodla.startsWith('@"', i)) {
      i += 2; let s = '';
      for (;;) { if (zrodla[i] === '"') { if (zrodla[i + 1] === '"') { s += '"'; i += 2; continue; } i++; break; } s += zrodla[i++]; }
      out += s;
    } else {
      const m = /^[A-Za-z_]\w*/.exec(zrodla.slice(i)); if (!m) throw new Error('nieznany fragment w ' + nazwa);
      out += skrypt(m[0]); i += m[0].length;
    }
    while (/\s/.test(zrodla[i])) i++;
    if (zrodla[i] === '+') { i++; continue; }
    if (zrodla[i] === ';') return out;
    throw new Error('nieoczekiwany znak w ' + nazwa + ': ' + zrodla[i]);
  }
}
