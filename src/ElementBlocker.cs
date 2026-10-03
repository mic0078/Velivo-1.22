using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Reczne blokowanie elementow strony (plywajace reklamy, banery, okienka), gdy filtry cos przeocza.
    // Regula = domena + selektor CSS; element jest ukrywany (display:none) przy kazdym wejsciu na strone.
    public partial class MainWindow
    {
        static string ElementRulesFile { get { return Path.Combine(DataDir, "elementy.txt"); } }
        readonly List<KeyValuePair<string, string>> _elementRules = new List<KeyValuePair<string, string>>();
        bool _elementRulesLoaded;

        void LoadElementRules()
        {
            _elementRulesLoaded = true;
            _elementRules.Clear();
            try
            {
                if (!File.Exists(ElementRulesFile)) return;
                foreach (var line in File.ReadAllLines(ElementRulesFile))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0 || tab == line.Length - 1) continue;
                    _elementRules.Add(new KeyValuePair<string, string>(line.Substring(0, tab).Trim().ToLowerInvariant(), line.Substring(tab + 1).Trim()));
                }
            }
            catch (IOException) { }
        }

        void SaveElementRules()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllLines(ElementRulesFile, _elementRules.Select(r => r.Key + "\t" + r.Value));
            }
            catch (IOException) { }
        }

        static string ElementRuleHost(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return null;
            var host = u.Host.ToLowerInvariant();
            return host.StartsWith("www.") ? host.Substring(4) : host;
        }

        List<string> ElementSelectorsFor(string url)
        {
            if (!_elementRulesLoaded) LoadElementRules();
            var host = ElementRuleHost(url);
            if (host == null) return new List<string>();
            // regula dla example.com dziala tez na poddomenach (m.example.com)
            return _elementRules.Where(r => host == r.Key || host.EndsWith("." + r.Key)).Select(r => r.Value).Distinct().ToList();
        }

        async void ApplyElementRules(CoreWebView2 core)
        {
            try
            {
                if (core == null) return;
                var selectors = ElementSelectorsFor(core.Source);
                if (selectors.Count == 0) return;
                var css = string.Join("\n", selectors.Select(s => s + " { display: none !important; visibility: hidden !important; }"));
                await core.ExecuteScriptAsync("(function(){try{var id='velivo-blokowane-elementy';var st=document.getElementById(id);if(!st){st=document.createElement('style');st.id=id;(document.head||document.documentElement).appendChild(st);}st.textContent=" + JsonSerializer.Serialize(css) + ";}catch(e){}})();");
            }
            catch (Exception) { }
        }

        // Tryb wybierania: podswietla element pod myszka, klik/Enter blokuje, kolko/strzalki zmieniaja zakres, Esc anuluje.
        const string ElementPickerScript = @"(function(startX, startY){
  if (window.__velivoPicker) return;
  window.__velivoPickResult = null;
  var box = document.createElement('div');
  box.style.cssText = 'position:fixed;z-index:2147483647;pointer-events:none;border:2px solid #e11d48;background:rgba(225,29,72,.18);border-radius:3px;transition:all .05s';
  var tip = document.createElement('div');
  tip.style.cssText = 'position:fixed;z-index:2147483647;left:50%;top:12px;transform:translateX(-50%);background:#111827;color:#fff;font:13px Segoe UI,sans-serif;padding:8px 14px;border-radius:8px;box-shadow:0 4px 16px rgba(0,0,0,.4)';
  tip.textContent = 'Blokowanie elementu: kliknij element (albo Enter). Kółko myszy / strzałki ↑↓ = większy/mniejszy obszar. Esc = anuluj.';
  // przezroczysta warstwa lapie mysz - inaczej klik w reklame w ramce (iframe) trafialby do ramki, nie do nas
  var shield = document.createElement('div');
  shield.style.cssText = 'position:fixed;inset:0;z-index:2147483646;cursor:crosshair;background:transparent';
  document.documentElement.appendChild(shield); document.documentElement.appendChild(box); document.documentElement.appendChild(tip);
  var cur = null, stack = [];
  function floating(el){ for (var e = el; e && e !== document.body; e = e.parentElement){ var p = getComputedStyle(e).position; if (p === 'fixed' || p === 'sticky') return e; } return null; }
  function pickAt(x, y){ shield.style.pointerEvents = 'none'; var el = document.elementFromPoint(x, y); shield.style.pointerEvents = 'auto'; if (!el || el === box || el === tip || el === shield) return; var f = floating(el); set(f && f.offsetWidth * f.offsetHeight < innerWidth * innerHeight * 0.9 ? f : el); stack = []; }
  function set(el){ if (!el || el === document.documentElement || el === document.body) return; cur = el; var r = el.getBoundingClientRect(); box.style.left = r.left + 'px'; box.style.top = r.top + 'px'; box.style.width = r.width + 'px'; box.style.height = r.height + 'px'; }
  function bigger(){ if (cur && cur.parentElement && cur.parentElement !== document.body) { stack.push(cur); set(cur.parentElement); } }
  function smaller(){ if (stack.length) set(stack.pop()); }
  function esc(s){ return window.CSS && CSS.escape ? CSS.escape(s) : s.replace(/([^\w-])/g, '\\$1'); }
  function randomish(s){ return /\d{3,}|[a-f0-9]{8,}|^[a-z]{1,2}\d|__/i.test(s); }
  function selector(el){
    if (el.id && !randomish(el.id) && document.querySelectorAll('#' + esc(el.id)).length === 1) return '#' + esc(el.id);
    if (el.tagName === 'IFRAME' && el.src) { try { var u = new URL(el.src, location.href); return 'iframe[src*=""' + u.hostname + '""]'; } catch (e) {} }
    var parts = [];
    for (var e = el; e && e.nodeType === 1 && e !== document.body && e !== document.documentElement; e = e.parentElement) {
      if (e.id && !randomish(e.id)) { parts.unshift('#' + esc(e.id)); break; }
      var part = e.tagName.toLowerCase();
      var cls = Array.prototype.filter.call(e.classList, function(c){ return !randomish(c); }).slice(0, 3);
      if (cls.length) part += '.' + cls.map(esc).join('.');
      var p = e.parentElement;
      if (p) { var same = Array.prototype.filter.call(p.children, function(c){ return c.tagName === e.tagName; }); if (same.length > 1) part += ':nth-of-type(' + (same.indexOf(e) + 1) + ')'; }
      parts.unshift(part);
      var sel = parts.join(' > ');
      try { if (document.querySelectorAll(sel).length === 1 && parts.length >= 2) return sel; } catch (x) {}
    }
    return parts.join(' > ');
  }
  function finish(result){
    document.removeEventListener('keydown', key, true);
    shield.remove(); box.remove(); tip.remove(); window.__velivoPicker = false; window.__velivoPickResult = result;
  }
  function move(ev){ pickAt(ev.clientX, ev.clientY); }
  function click(ev){ ev.preventDefault(); ev.stopPropagation(); if (!cur) return; var s = selector(cur); cur.style.setProperty('display', 'none', 'important'); finish({ ok: true, selector: s }); }
  function key(ev){ if (ev.key === 'Escape') { ev.preventDefault(); finish({ ok: false }); } else if (ev.key === 'Enter') click(ev); else if (ev.key === 'ArrowUp') { ev.preventDefault(); bigger(); } else if (ev.key === 'ArrowDown') { ev.preventDefault(); smaller(); } }
  function wheel(ev){ ev.preventDefault(); if (ev.deltaY < 0) bigger(); else smaller(); }
  shield.addEventListener('mousemove', move); shield.addEventListener('click', click);
  shield.addEventListener('contextmenu', function(ev){ ev.preventDefault(); finish({ ok: false }); });
  shield.addEventListener('wheel', wheel, { passive: false });
  document.addEventListener('keydown', key, true);
  window.__velivoPicker = true;
  pickAt(startX, startY);
})";

        async Task StartElementPicker(BrowserTab tab, double x, double y)
        {
            var core = tab != null ? tab.View.CoreWebView2 : null;
            if (core == null) return;
            var host = ElementRuleHost(core.Source);
            if (host == null) return;
            try
            {
                double zoom = tab.View.ZoomFactor > 0 ? tab.View.ZoomFactor : 1;
                await core.ExecuteScriptAsync(ElementPickerScript + "(" + (x / zoom).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                    (y / zoom).ToString(System.Globalization.CultureInfo.InvariantCulture) + ");");
                // wynik odbieramy odpytywaniem - strona nie ma kanalu wiadomosci do programu
                for (int i = 0; i < 600; i++)
                {
                    await Task.Delay(250);
                    if (tab.View.CoreWebView2 == null || ElementRuleHost(core.Source) != host) return;
                    var json = await core.ExecuteScriptAsync("JSON.stringify(window.__velivoPickResult || null)");
                    var raw = JsonSerializer.Deserialize<string>(json);
                    if (string.IsNullOrEmpty(raw) || raw == "null") continue;
                    using (var doc = JsonDocument.Parse(raw))
                    {
                        if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return;
                        var selector = doc.RootElement.GetProperty("selector").GetString();
                        if (string.IsNullOrWhiteSpace(selector) || selector.Length > 1000 || selector.IndexOf('\n') >= 0 || selector.IndexOf('{') >= 0 || selector.IndexOf('}') >= 0) return;
                        if (!_elementRulesLoaded) LoadElementRules();
                        if (!_elementRules.Any(r => r.Key == host && r.Value == selector))
                        {
                            _elementRules.Add(new KeyValuePair<string, string>(host, selector));
                            SaveElementRules();
                        }
                        ApplyElementRules(core);
                        ShowToast("🚫 Element zablokowany na " + host + ". Cofniesz to: prawy przycisk → Przywróć zablokowane elementy.", null);
                    }
                    return;
                }
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void ClearElementRules(BrowserTab tab)
        {
            var core = tab != null ? tab.View.CoreWebView2 : null;
            var host = core != null ? ElementRuleHost(core.Source) : null;
            if (host == null) return;
            if (!_elementRulesLoaded) LoadElementRules();
            int removed = _elementRules.RemoveAll(r => host == r.Key || host.EndsWith("." + r.Key));
            if (removed == 0) return;
            SaveElementRules();
            core.Reload();
            ShowToast("Przywrócono " + removed + " zablokowanych elementów na " + host + ".", null);
        }
    }
}
