// Velivo: reklamy na YouTube (wstrzykiwane przed załadowaniem strony, tylko na domenach YouTube).
(function () {
  if (window.__velivoYt) return;
  window.__velivoYt = 1;
  var AD_KEYS = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams'];

  // 1. Usuwa informacje o reklamach z danych odtwarzacza - reklama się nie zaczyna.
  function prune(o) {
    if (!o || typeof o !== 'object') return o;
    AD_KEYS.forEach(function (k) { if (k in o) try { delete o[k]; } catch (e) {} });
    if (o.playerResponse) prune(o.playerResponse);
    return o;
  }
  var parse = JSON.parse;
  JSON.parse = function () { return prune(parse.apply(this, arguments)); };
  var rj = Response.prototype.json;
  Response.prototype.json = function () { return rj.apply(this, arguments).then(prune); };
  ['ytInitialPlayerResponse', 'ytInitialData'].forEach(function (name) {
    var v;
    try {
      Object.defineProperty(window, name, {
        configurable: true,
        get: function () { return v; },
        set: function (x) { v = prune(x); }
      });
    } catch (e) {}
  });

  // 2. Reklama, która się przedrze: "Pomiń" i wyciszenie na jej czas.
  // Bez przewijania na koniec - YouTube wkleja reklamy w strumień filmu i przeskakiwało cały film
  // (jak w Velivo na Windows). Bez ukrywania miejsc na reklamy - YouTube wykrywał to jako bloker
  // i zamiast filmu pokazywał "Zezwól na reklamy".
  function skip() {
    var p = document.querySelector('.html5-video-player,#movie_player');
    var b = document.querySelector('.ytp-ad-skip-button,.ytp-ad-skip-button-modern,.ytp-skip-ad-button,.ytm-skip-ad-button');
    if (b) b.click();
    var v = p && p.querySelector('video');
    if (!v) return;
    if (p.classList.contains('ad-showing')) { if (!v.muted) { v.muted = true; v.__velivoAdMuted = true; } }
    else if (v.__velivoAdMuted) { v.muted = false; v.__velivoAdMuted = false; }
  }

  setInterval(function () { try { skip(); } catch (e) {} }, 300);
})();

// Odtwarzanie w tle: YouTube pauzuje film, gdy strona jest niewidoczna - strona zawsze widzi się jako widoczna.
(function () {
  try {
    Object.defineProperty(document, 'hidden', { get: function () { return false; } });
    Object.defineProperty(document, 'visibilityState', { get: function () { return 'visible'; } });
    Object.defineProperty(document, 'webkitVisibilityState', { get: function () { return 'visible'; } });
    ['visibilitychange', 'webkitvisibilitychange'].forEach(function (t) {
      document.addEventListener(t, function (e) { e.stopImmediatePropagation(); }, true);
    });
  } catch (e) {}
})();
