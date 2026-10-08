// Atrapa przegladarki i Velivo dla strony Szybkiego Dostepu (newtab.html): chrome.storage, chrome.runtime itd.
// oraz kanal chrome.webview z poleceniami Velivo (read/write/list/del/ping) na pamieci trwalej (localStorage).
(() => {
  const LS = window.localStorage;
  const wczytaj = (k) => { try { return JSON.parse(LS.getItem(k) || '{}'); } catch (e) { return {}; } };
  const obszar = (nazwa) => {
    const k = 'atrapa-' + nazwa;
    const get = (klucze) => { const all = wczytaj(k); let wyn = {};
      if (klucze == null) wyn = all; else if (typeof klucze === 'string') { if (klucze in all) wyn[klucze] = all[klucze]; }
      else if (Array.isArray(klucze)) klucze.forEach((x) => { if (x in all) wyn[x] = all[x]; });
      else Object.keys(klucze).forEach((x) => { wyn[x] = x in all ? all[x] : klucze[x]; });
      return JSON.parse(JSON.stringify(wyn)); };
    const api = {
      get: (klucze, cb) => { const w = get(klucze); if (cb) setTimeout(() => cb(w), 0); return Promise.resolve(w); },
      set: (obj, cb) => { const all = wczytaj(k); Object.assign(all, JSON.parse(JSON.stringify(obj))); LS.setItem(k, JSON.stringify(all)); if (cb) setTimeout(cb, 0); return Promise.resolve(); },
      remove: (klucze, cb) => { const all = wczytaj(k); [].concat(klucze).forEach((x) => delete all[x]); LS.setItem(k, JSON.stringify(all)); if (cb) setTimeout(cb, 0); return Promise.resolve(); },
      getBytesInUse: (x, cb) => { const n = JSON.stringify(wczytaj(k)).length; if (cb) cb(n); return Promise.resolve(n); },
      QUOTA_BYTES: 102400,
    };
    return api;
  };
  const sluchacze = [];
  const fs = () => wczytaj('atrapa-dysk');
  window.__bledyVelivo = [];
  window.chrome = {
    storage: { local: obszar('local'), sync: obszar('sync'), session: obszar('session'), onChanged: { addListener: () => {} } },
    runtime: { id: 'velivotest', getURL: (p) => new URL(p, location.href).href, getManifest: () => ({ version: '1.0', name: 'Szybki Dostep' }),
      sendMessage: (m, cb) => { if (cb) setTimeout(() => cb(null), 0); return Promise.resolve(null); }, lastError: null,
      connectNative: () => { throw new Error('brak mostu'); } },
    permissions: { contains: (p, cb) => { if (cb) cb(false); return Promise.resolve(false); }, request: (p, cb) => { if (cb) cb(false); return Promise.resolve(false); } },
    identity: { getProfileUserInfo: (o, cb) => { const w = { email: '', id: '' }; if (typeof o === 'function') o(w); else if (cb) cb(w); return Promise.resolve(w); } },
    tabs: { getCurrent: (cb) => { const t = { id: 1 }; if (cb) cb(t); return Promise.resolve(t); }, update: () => Promise.resolve(), remove: () => Promise.resolve() },
    webview: {
      addEventListener: (typ, f) => { if (typ === 'message') sluchacze.push(f); },
      postMessage: (m) => {
        if (!m || m.typ !== 'velivo-quick-access') return;
        const z = m.zadanie || {}, dysk = fs(); let o = { ok: false, id: m.id };
        if (z.c === 'ping') o = { ok: true, baza: 'C:\\Velivo\\Szybki', velivo: true, id: m.id };
        else if (z.c === 'read') o = z.p in dysk ? { ok: true, b64: dysk[z.p], id: m.id } : o;
        else if (z.c === 'write') { dysk[z.p] = z.b64; LS.setItem('atrapa-dysk', JSON.stringify(dysk)); o = { ok: true, id: m.id }; }
        else if (z.c === 'list') o = { ok: true, pliki: Object.keys(dysk).filter((x) => x.startsWith(z.p + '/')).map((x) => x.slice(z.p.length + 1)).filter((x) => !x.includes('/')), id: m.id };
        else if (z.c === 'del') { delete dysk[z.p]; LS.setItem('atrapa-dysk', JSON.stringify(dysk)); o = { ok: true, id: m.id }; }
        else if (z.c === 'thumbsQueue' || z.c === 'backToSource') o = { ok: true, id: m.id };
        setTimeout(() => sluchacze.forEach((f) => f({ data: { typ: 'velivo-quick-access-odpowiedz', id: m.id, odpowiedz: o } })), 5);
      },
    },
  };
})();
