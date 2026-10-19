// Velivo: bez zachet "pobierz aplikacje" / "zainstaluj" / "dodaj do Chrome" (np. Startpage) - na Windows i telefonie.
// Ukrywa tylko mala ramke z linkiem do sklepu z aplikacjami i krotkim tekstem zachety; tresc strony zostaje.
(function () {
  if (window.__velivoBezApki) return; window.__velivoBezApki = true;
  var STORE = 'a[href*="play.google.com/store/apps"],a[href*="apps.apple.com"],a[href*="itunes.apple.com"],a[href*="chrome.google.com/webstore"],a[href*="chromewebstore.google.com"],a[href*="addons.mozilla.org"],a[href*="microsoftedge.microsoft.com/addons"],a[href*="appgallery.huawei.com"]';
  var PROMO = /(pobierz|zainstaluj|instaluj|aplikacj|dodaj do|download|install|get the app|our app|add to (chrome|firefox|edge|safari)|app store|google play|rozszerzeni|extension)/i;
  var CLS = /(app-?promo|app-?banner|smart-?banner|download-?app|get-?app|install-?app|app-?download|extension-?promo|promo-?app|mobile-?app-?(banner|promo))/i;
  function small(e) { var t = (e.innerText || '').trim(); return t.length < 320 && !e.querySelector('input[type=search],input[name=q],textarea,h1,article,main,form'); }
  function hide(e) { e.style.setProperty('display', 'none', 'important'); }
  function run() {
    var links = document.querySelectorAll(STORE);
    for (var i = 0; i < links.length; i++) {
      var box = links[i], best = null;
      for (var k = 0; k < 5 && box && box !== document.body; k++, box = box.parentElement) {
        if (!small(box)) break;
        if (PROMO.test(box.innerText || '') || PROMO.test(box.getAttribute('aria-label') || '')) best = box;
      }
      if (best) hide(best);
    }
    var els = document.querySelectorAll('div,section,aside,a,span');
    for (var j = 0; j < els.length; j++) {
      var e = els[j], n = (typeof e.className === 'string' ? e.className : '') + ' ' + e.id;
      if (CLS.test(n) && small(e) && PROMO.test(e.innerText || '')) hide(e);
    }
  }
  var t = 0;
  function soon() { clearTimeout(t); t = setTimeout(run, 400); }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', run); else run();
  new MutationObserver(soon).observe(document.documentElement, { childList: true, subtree: true });
})();
