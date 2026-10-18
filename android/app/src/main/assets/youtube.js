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

  // 2. Reklamy na liście, w wynikach i pod filmem - ukryte.
  var css = 'ytd-ad-slot-renderer,ytd-in-feed-ad-layout-renderer,ytd-promoted-sparkles-web-renderer,' +
    'ytd-display-ad-renderer,ytd-companion-slot-renderer,ytd-action-companion-ad-renderer,' +
    'ytd-banner-promo-renderer,ytd-statement-banner-renderer,ytd-player-legacy-desktop-watch-ads-renderer,' +
    '#player-ads,#masthead-ad,.ytp-ad-overlay-container,.ytp-ad-image-overlay,' +
    'ytm-promoted-sparkles-web-renderer,ytm-promoted-video-renderer,ytm-companion-ad-renderer,' +
    'ad-slot-renderer,ytm-ad-slot-renderer,.ytm-promoted-sparkles-text-search-root' +
    '{display:none!important}';
  function addCss() {
    if (document.getElementById('velivo-yt')) return;
    var s = document.createElement('style');
    s.id = 'velivo-yt';
    s.textContent = css;
    (document.head || document.documentElement).appendChild(s);
  }

  // 3. Gdy reklama jednak się przedrze: przewinięcie na koniec i "Pomiń".
  function skip() {
    var p = document.querySelector('.ad-showing,.ad-interrupting');
    if (!p) return;
    var v = p.querySelector('video');
    if (v && isFinite(v.duration) && v.duration > 0) { v.muted = true; v.currentTime = v.duration; }
    var b = document.querySelector('.ytp-ad-skip-button,.ytp-ad-skip-button-modern,.ytp-skip-ad-button,.ytm-skip-ad-button,button[class*="skip-ad"]');
    if (b) b.click();
  }

  function start() {
    addCss();
    new MutationObserver(skip).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
    setInterval(skip, 500);
  }
  if (document.documentElement) start(); else document.addEventListener('DOMContentLoaded', start);
})();
