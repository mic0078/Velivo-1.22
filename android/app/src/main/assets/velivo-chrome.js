// Velivo na Androidzie: zastępstwo funkcji dodatków Chrome dla strony Szybkiego Dostępu
// (ta sama strona co w Velivo na Windows - src/QuickAccessExtension/kod).
// Dane zapisuje aplikacja w swoim pliku (VelivoSD - kanał dostępny tylko dla tej strony).
(function () {
  if (window.chrome && window.chrome.storage) return;
  var czeka = {}, licznik = 0, sluchacze = [];
  if (window.VelivoSD) {
    VelivoSD.onmessage = function (e) {
      var m = JSON.parse(e.data);
      var f = czeka[m.id];
      if (f) { delete czeka[m.id]; f(m.wynik); }
    };
  }
  function zapytaj(op, obszar, dane) {
    return new Promise(function (ok) {
      if (!window.VelivoSD) { ok({}); return; }
      var id = ++licznik;
      czeka[id] = ok;
      VelivoSD.postMessage(JSON.stringify({ id: id, op: op, obszar: obszar, dane: dane }));
    });
  }
  function powiadom(zmiany, obszar) {
    sluchacze.forEach(function (f) { try { f(zmiany, obszar); } catch (e) {} });
  }
  // Obszar zapisu z interfejsem chrome.storage (obietnice i funkcje zwrotne).
  function obszar(nazwa) {
    return {
      QUOTA_BYTES: 10 * 1024 * 1024,
      get: function (q, cb) {
        var klucze = null, domyslne = {};
        if (typeof q === 'string') klucze = [q];
        else if (Array.isArray(q)) klucze = q;
        else if (q && typeof q === 'object') { domyslne = q; klucze = Object.keys(q); }
        var p = zapytaj('get', nazwa, klucze).then(function (w) {
          w = w || {};
          Object.keys(domyslne).forEach(function (k) { if (!(k in w)) w[k] = domyslne[k]; });
          return w;
        });
        if (cb) p.then(cb);
        return p;
      },
      set: function (o, cb) {
        var p = zapytaj('set', nazwa, o).then(function () {
          var z = {};
          Object.keys(o).forEach(function (k) { z[k] = { newValue: o[k] }; });
          powiadom(z, nazwa);
        });
        if (cb) p.then(function () { cb(); });
        return p;
      },
      remove: function (k, cb) {
        var lista = Array.isArray(k) ? k : [k];
        var p = zapytaj('remove', nazwa, lista).then(function () {
          var z = {};
          lista.forEach(function (x) { z[x] = { newValue: undefined }; });
          powiadom(z, nazwa);
        });
        if (cb) p.then(function () { cb(); });
        return p;
      },
      clear: function (cb) {
        var p = zapytaj('clear', nazwa, null);
        if (cb) p.then(function () { cb(); });
        return p;
      },
      getBytesInUse: function (k, cb) {
        if (typeof k === 'function') cb = k;
        var p = zapytaj('size', nazwa, null).then(function (n) { return n || 0; });
        if (cb) p.then(cb);
        return p;
      }
    };
  }
  var nie = function () { return Promise.resolve(false); };
  window.chrome = window.chrome || {};
  window.chrome.storage = {
    local: obszar('local'),
    sync: obszar('sync'),
    session: obszar('session'),
    onChanged: { addListener: function (f) { sluchacze.push(f); }, removeListener: function () {} }
  };
  window.chrome.runtime = {
    id: 'velivo-android',
    lastError: undefined,
    // pliki dodatku leżą obok strony; brakujące (np. /_favicon/, dane z folderu Windows) dają 404 - strona ma zapas
    getURL: function (p) { return new URL(String(p).replace(/^\//, ''), location.href).toString(); },
    getManifest: function () { return { version: 'Android' }; },
    sendMessage: function () { return Promise.resolve(undefined); },
    connectNative: function () { throw new Error('brak mostu na Androidzie'); }
  };
  window.chrome.permissions = { contains: nie, request: nie, remove: nie };
  window.chrome.tabs = {
    getCurrent: function () { return Promise.resolve(null); },
    update: function (id, o) { if (o && o.url) location.href = o.url; return Promise.resolve(); },
    remove: function () { return Promise.resolve(); },
    create: function (o) { if (o && o.url) location.href = o.url; return Promise.resolve(); }
  };
})();
