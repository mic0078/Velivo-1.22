using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Dodatki do przegladania: odrzucanie banerow z ciasteczkami, gesty myszy, przycisk "obraz w obrazie",
    // skroty wyszukiwania w pasku adresu i szukanie w otwartych kartach.
    public partial class MainWindow
    {
        // Losowy znacznik tego uruchomienia - strona nie zna go, wiec nie podrobi wiadomosci (gest, ciasteczka).
        static readonly string PageToken = Guid.NewGuid().ToString("N");

        // ================= wspolny skrypt stron =================

        static string CookieExceptionsFile { get { return Path.Combine(DataDir, "ciasteczka-wyjatki.txt"); } }
        HashSet<string> _cookieExceptions;

        HashSet<string> CookieExceptions
        {
            get
            {
                if (_cookieExceptions == null)
                {
                    _cookieExceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try { if (File.Exists(CookieExceptionsFile)) foreach (var l in File.ReadAllLines(CookieExceptionsFile)) if (l.Trim().Length > 0) _cookieExceptions.Add(l.Trim()); }
                    catch (IOException) { }
                }
                return _cookieExceptions;
            }
        }

        bool CookieRejectOn(string url)
        {
            var host = ElementRuleHost(url);
            return host != null && !CookieExceptions.Any(d => host == d || host.EndsWith("." + d));
        }

        void SetCookieException(string url, bool except)
        {
            var host = ElementRuleHost(url);
            if (host == null) return;
            if (except) CookieExceptions.Add(host);
            else CookieExceptions.RemoveWhere(d => host == d || host.EndsWith("." + d));
            try { Directory.CreateDirectory(DataDir); File.WriteAllLines(CookieExceptionsFile, CookieExceptions.OrderBy(x => x)); } catch (IOException) { }
            RefreshPageScripts();
        }

        string BuildPageScript()
        {
            var cfg = JsonSerializer.Serialize(new
            {
                token = PageToken,
                cookies = _settings.AutoRejectCookies,
                except = CookieExceptions.ToArray(),
                gestures = _settings.MouseGestures,
                pip = _settings.PipButton,
                pipLabel = L.T("Obraz w obrazie"),
                dlBtn = _settings.VideoDownloadButton,
                dlLabel = L.T("Pobierz"),
                dark = _settings.DarkPatterns,
                receipt = _settings.PrivacyReceipt,
            });
            return "(function(C){" + PageScriptBody + DarkPatternScript + FingerprintScript + "})(" + cfg + ");";
        }

        const string PageScriptBody = @"
try {
  if (location.protocol === 'chrome-extension:') return;
  var pm = (window.chrome && chrome.webview && chrome.webview.postMessage) ? chrome.webview.postMessage.bind(chrome.webview) : null;
  function send(m) { try { if (pm) pm('velivo:' + C.token + ':' + m); } catch (x) {} }
  var top = window === window.top;

  // ---------- gesty myszy (prawy przycisk + ruch) ----------
  if (C.gestures && top) {
    var G = null, eat = false;
    addEventListener('mousedown', function (e) { if (e.button === 2) G = { x: e.clientX, y: e.clientY, p: [] }; }, true);
    addEventListener('mousemove', function (e) {
      if (!G) return;
      if (!(e.buttons & 2)) { G = null; return; }
      var dx = e.clientX - G.x, dy = e.clientY - G.y;
      if (Math.abs(dx) < 30 && Math.abs(dy) < 30) return;
      var d = Math.abs(dx) > Math.abs(dy) ? (dx > 0 ? 'R' : 'L') : (dy > 0 ? 'D' : 'U');
      if (G.p[G.p.length - 1] !== d) G.p.push(d);
      G.x = e.clientX; G.y = e.clientY;
    }, true);
    addEventListener('mouseup', function (e) {
      if (e.button !== 2 || !G) return;
      var g = G.p.join(''); G = null;
      if (g) { eat = true; setTimeout(function () { eat = false; }, 600); send('gest:' + g); }
    }, true);
    addEventListener('contextmenu', function (e) {
      if (eat || (G && G.p.length)) { e.preventDefault(); e.stopImmediatePropagation(); eat = false; }
    }, true);
  }

  // ---------- odrzucanie banerow z ciasteczkami ----------
  var host = location.hostname.replace(/^www\./, '');
  if (C.cookies && !C.except.some(function (d) { return host === d || host.slice(-(d.length + 1)) === '.' + d; })) {
    var SEL = ['#onetrust-reject-all-handler', '#CybotCookiebotDialogBodyButtonDecline', '#didomi-notice-disagree-button',
      '.didomi-continue-without-agreeing', 'button.sp_choice_type_REJECT_ALL', '[data-testid=""uc-deny-all-button""]',
      '#cookiescript_reject', '.cky-btn-reject', '.cc-deny', '.osano-cm-denyAll', 'button[data-cookiefirst-action=""reject""]',
      '#tarteaucitronAllDenied2', '.fc-cta-do-not-consent', '.qc-cmp2-summary-buttons button[mode=""secondary""]',
      '#truste-consent-required', '.cmplz-deny', '#CookieBoxSaveButton[data-reject]', '.iubenda-cs-reject-btn'];
    var TXT = ['odrzuć wszystkie', 'odrzuć wszystko', 'odrzucam wszystkie', 'odrzuć', 'odrzucam', 'nie zgadzam się',
      'nie wyrażam zgody', 'nie akceptuję', 'tylko niezbędne', 'tylko niezbędne pliki cookie', 'tylko niezbędne cookies',
      'akceptuj tylko niezbędne', 'zaakceptuj tylko niezbędne', 'zezwól tylko na niezbędne', 'użyj tylko niezbędnych plików cookie',
      'kontynuuj bez akceptacji', 'kontynuuj bez zgody', 'reject all', 'reject', 'decline', 'decline all', 'deny', 'deny all',
      'refuse all', 'only necessary', 'necessary only', 'accept only necessary', 'only essential cookies',
      'use necessary cookies only', 'continue without accepting', 'do not consent', 'disagree'];
    var CTX = /cookie|ciasteczk|rodo|gdpr|prywatno|privacy|consent|zgod|cmp/i;
    var done = false, started = Date.now();
    function txt(el) { return ((el.innerText || el.value || el.getAttribute('aria-label') || '') + '').replace(/\s+/g, ' ').trim().toLowerCase(); }
    function vis(el) { var r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden'; }
    function consent(el) {
      for (var e = el, i = 0; e && i < 12; e = e.parentElement || (e.getRootNode && e.getRootNode().host), i++) {
        var k = (e.id || '') + ' ' + (typeof e.className === 'string' ? e.className : '');
        if (CTX.test(k)) return true;
        var t = e.innerText || '';
        if (i > 0 && t.length > 40 && t.length < 6000 && CTX.test(t)) return true;
      }
      return false;
    }
    function scan(root) {
      for (var s = 0; s < SEL.length; s++) { var b = root.querySelector(SEL[s]); if (b && vis(b)) return b; }
      var c = root.querySelectorAll('button,[role=button],a,input[type=button],input[type=submit]');
      for (var i = 0; i < c.length; i++) {
        var t = txt(c[i]);
        if (!t || t.length > 50 || TXT.indexOf(t) < 0) continue;
        if (vis(c[i]) && consent(c[i])) return c[i];
      }
      return null;
    }
    function attempt() {
      if (done || !document.body) return;
      if (Date.now() - started > 20000) { if (mo) mo.disconnect(); return; }
      var b = scan(document);
      if (!b) { var kids = document.body.children; for (var i = 0; i < kids.length && !b; i++) if (kids[i].shadowRoot) b = scan(kids[i].shadowRoot); }
      if (!b) return;
      done = true; if (mo) mo.disconnect();
      try { b.click(); } catch (x) {}
      send('cookie');
    }
    var mo = null, timer = 0;
    function later() { if (!timer) timer = setTimeout(function () { timer = 0; attempt(); }, 400); }
    function go() { attempt(); if (!done && window.MutationObserver) { mo = new MutationObserver(later); mo.observe(document.documentElement, { childList: true, subtree: true }); } }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', go); else go();
  }

  // obraz w obrazie: program musi wiedziec, zeby zamkniecie karty nie zamykalo okienka z filmem
  if (top) {
    document.addEventListener('enterpictureinpicture', function () { send('pip:1'); }, true);
    document.addEventListener('leavepictureinpicture', function () { send('pip:0'); }, true);
  }
  // ---------- przyciski nad filmem: ""obraz w obrazie"" i ""pobierz"" ----------
  if (C.pip || C.dlBtn) {
    var btn = null, cur = null, hideT = 0, last = 0;
    var BST = 'all:initial;cursor:pointer;background:rgba(17,24,39,.82);color:#fff;font:600 13px Segoe UI,sans-serif;padding:6px 10px;border-radius:8px;box-shadow:0 2px 8px rgba(0,0,0,.4);margin-left:6px';
    function mk() {
      var h = document.createElement('div');
      h.style.cssText = 'position:fixed;z-index:2147483647;display:none;';
      var r = h.attachShadow({ mode: 'closed' });
      // bez innerHTML - YouTube (Trusted Types) blokuje wstawianie HTML z tekstu
      function addB(a, label) { var b = document.createElement('button'); b.setAttribute('data-a', a); b.title = label; b.textContent = (a === 'dl' ? '⬇ ' : '⧉ ') + label; b.style.cssText = BST; r.appendChild(b); }
      if (C.dlBtn && pm) addB('dl', C.dlLabel);
      if (C.pip && document.pictureInPictureEnabled !== false) addB('pip', C.pipLabel);
      r.querySelectorAll('button').forEach(function (b) {
        b.addEventListener('click', function (e) {
          e.preventDefault(); e.stopPropagation();
          if (!cur) return;
          if (b.getAttribute('data-a') === 'dl') { send('dl:' + JSON.stringify({ page: location.href, src: cur.currentSrc || cur.src || '', title: document.title || '' })); return; }
          if (document.pictureInPictureElement === cur) document.exitPictureInPicture().catch(function () {});
          else { try { cur.disablePictureInPicture = false; } catch (x) {} cur.requestPictureInPicture().catch(function () {}); }
        }, true);
      });
      (document.documentElement || document.body).appendChild(h);
      return h;
    }
    addEventListener('mousemove', function (e) {
      var now = Date.now(); if (now - last < 150) return; last = now;
      var vids = document.getElementsByTagName('video'), hit = null;
      for (var i = 0; i < vids.length; i++) {
        var r = vids[i].getBoundingClientRect();
        if (r.width >= 240 && r.height >= 135 && e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom) { hit = vids[i]; break; }
      }
      if (!hit) { if (btn && !hideT) hideT = setTimeout(function () { btn.style.display = 'none'; hideT = 0; }, 1500); return; }
      if (hideT) { clearTimeout(hideT); hideT = 0; }
      cur = hit;
      if (!btn) btn = mk();
      var rr = hit.getBoundingClientRect();
      btn.style.display = 'block';
      btn.style.left = Math.max(0, rr.right - btn.getBoundingClientRect().width - 12) + 'px'; btn.style.top = Math.max(0, rr.top + 10) + 'px';
    }, true);
  }
} catch (x) {}";

        // Skrypt jest wpisywany przy tworzeniu karty; po zmianie ustawien podmieniamy go we wszystkich kartach.
        async Task InstallPageScript(BrowserTab tab, CoreWebView2 core)
        {
            try
            {
                if (tab.PageScriptId != null) { core.RemoveScriptToExecuteOnDocumentCreated(tab.PageScriptId); tab.PageScriptId = null; }
                tab.PageScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildPageScript());
                // skrypt stron musi dzialac PRZED ukryciem chrome.webview (inaczej przyciski Pobierz/gesty nie maja kanalu do programu)
                if (tab.HideScriptId != null)
                {
                    core.RemoveScriptToExecuteOnDocumentCreated(tab.HideScriptId);
                    tab.HideScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(HideWebViewBrandScript);
                }
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void RefreshPageScripts()
        {
            foreach (var t in _tabs.ToList())
                if (t.View.CoreWebView2 != null) _ = InstallPageScript(t, t.View.CoreWebView2);
        }

        // Wiadomosci od skryptu strony (tylko ze znacznikiem tego uruchomienia).
        void HandlePageMessage(BrowserTab tab, string msg)
        {
            var prefix = "velivo:" + PageToken + ":";
            if (msg == null || !msg.StartsWith(prefix, StringComparison.Ordinal)) return;
            msg = msg.Substring(prefix.Length);
            var core = tab.View.CoreWebView2;
            if (msg.StartsWith("dark:", StringComparison.Ordinal)) { if (_settings.DarkPatterns) HandleDarkPatterns(tab, msg.Substring(5)); return; }
            if (msg.StartsWith("dl:", StringComparison.Ordinal)) { HandleVideoDownloadRequest(tab, msg.Substring(3)); return; }
            if (msg.StartsWith("fp:", StringComparison.Ordinal)) { HandleFingerprintReport(tab, msg.Substring(3)); return; }
            if (msg == "pip:1" || msg == "pip:0")
            {
                tab.InPip = msg == "pip:1";
                if (tab.InPip) _pipViews.Add(tab.View); else _pipViews.Remove(tab.View);
                // karta byla juz zamknieta, a uzytkownik zamknal okienko - dopiero teraz zwalniamy film
                if (!tab.InPip && !_tabs.Contains(tab)) ReleaseParkedViews();
                return;
            }
            if (msg == "cookie")
            {
                NoteBlocked(tab, L.T("Ciasteczka"), (core != null ? core.Source : "") + L.T("  (baner zgody odrzucony)"));
                return;
            }
            if (msg.StartsWith("gest:", StringComparison.Ordinal) && _settings.MouseGestures && core != null)
            {
                switch (msg.Substring(5))
                {
                    case "L": if (core.CanGoBack) core.GoBack(); break;
                    case "R": if (core.CanGoForward) core.GoForward(); break;
                    case "U": AddTab(NewTabUrl); break;
                    case "D": CloseTab(tab); break;
                    case "DR": core.Reload(); break;
                    case "UD": core.Reload(); break;
                }
            }
        }

        // Obraz w obrazie z menu: najwiekszy film na stronie.
        async Task StartPictureInPicture(BrowserTab tab)
        {
            var core = tab != null ? tab.View.CoreWebView2 : null;
            if (core == null) return;
            try
            {
                var r = await core.ExecuteScriptAsync(@"(function(){var v=Array.prototype.slice.call(document.querySelectorAll('video')).filter(function(x){return x.readyState>0;}).sort(function(a,b){return b.clientWidth*b.clientHeight-a.clientWidth*a.clientHeight;})[0];
if(!v)return 'brak';if(document.pictureInPictureElement===v){document.exitPictureInPicture();return 'ok';}try{v.disablePictureInPicture=false;}catch(e){}v.requestPictureInPicture().catch(function(){});return 'ok';})()");
                if (r.Contains("brak"))
                    ShowToast(L.T("Na tej stronie nie ma filmu. Filmy w ramkach (np. osadzony YouTube) mają przycisk ⧉ po najechaniu myszką."), null);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // ================= skroty wyszukiwania w pasku adresu =================

        static string SearchKeywordsFile { get { return Path.Combine(DataDir, "skroty-wyszukiwania.txt"); } }

        const string DefaultSearchKeywords =
            "yt=https://www.youtube.com/results?search_query=%s\n" +
            "g=https://www.google.com/search?q=%s\n" +
            "ddg=https://duckduckgo.com/?q=%s\n" +
            "wiki=https://pl.wikipedia.org/w/index.php?search=%s\n" +
            "allegro=https://allegro.pl/listing?string=%s\n" +
            "olx=https://www.olx.pl/oferty/q-%s/\n" +
            "ceneo=https://www.ceneo.pl/;szukaj-%s\n" +
            "mapy=https://www.google.com/maps/search/%s\n" +
            "filmweb=https://www.filmweb.pl/search?q=%s\n" +
            "tlumacz=https://translate.google.com/?sl=auto&tl=pl&text=%s\n";

        static Dictionary<string, string> LoadSearchKeywords()
        {
            string text;
            try { text = File.Exists(SearchKeywordsFile) ? File.ReadAllText(SearchKeywordsFile) : DefaultSearchKeywords; }
            catch (IOException) { text = DefaultSearchKeywords; }
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.StartsWith("#") || eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var url = line.Substring(eq + 1).Trim();
                if (key.Length > 0 && key.IndexOf(' ') < 0 && url.Contains("%s") && (url.StartsWith("http://") || url.StartsWith("https://"))) d[key] = url;
            }
            return d;
        }

        // "yt koty" -> wyszukiwanie na YouTube. null = to nie jest skrot.
        static string ExpandSearchKeyword(string text)
        {
            var m = Regex.Match(text ?? "", @"^\s*(\S+)\s+(.+?)\s*$");
            if (!m.Success) return null;
            string url;
            if (!LoadSearchKeywords().TryGetValue(m.Groups[1].Value, out url)) return null;
            return url.Replace("%s", Uri.EscapeDataString(m.Groups[2].Value));
        }

        void EditSearchKeywords(Window owner)
        {
            string text;
            try { text = File.Exists(SearchKeywordsFile) ? File.ReadAllText(SearchKeywordsFile) : DefaultSearchKeywords; }
            catch (IOException) { text = DefaultSearchKeywords; }
            var box = new TextBox { Text = text, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(10, 4, 10, 4) };
            var help = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 10, 10, 0),
                Text = L.T("Jeden skrót w linii: skrót=adres, gdzie %s to szukany tekst. Przykład: wpisz w pasku adresu „yt koty”, a otworzy się wyszukiwanie „koty” na YouTube.")
            };
            var ok = new Button { Content = L.T("Zapisz"), Width = 90, Height = 28, Margin = new Thickness(6, 8, 10, 10), IsDefault = true };
            var reset = new Button { Content = L.T("Przywróć domyślne"), Padding = new Thickness(10, 0, 10, 0), Height = 28, Margin = new Thickness(10, 8, 6, 10) };
            var bottom = new DockPanel();
            DockPanel.SetDock(ok, Dock.Right);
            DockPanel.SetDock(reset, Dock.Left);
            bottom.Children.Add(ok); bottom.Children.Add(reset); bottom.Children.Add(new Border());
            var dock = new DockPanel();
            DockPanel.SetDock(help, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(help); dock.Children.Add(bottom); dock.Children.Add(box);
            var w = new Window { Title = L.T("Skróty wyszukiwania"), Width = 640, Height = 460, Owner = owner ?? this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = dock };
            reset.Click += (s, e) => box.Text = DefaultSearchKeywords;
            ok.Click += (s, e) =>
            {
                try { Directory.CreateDirectory(DataDir); File.WriteAllText(SearchKeywordsFile, box.Text.Replace("\r\n", "\n")); w.Close(); }
                catch (IOException ex) { MessageBox.Show(w, ex.Message, "Velivo"); }
            };
            w.ShowDialog();
        }

        // ================= szukanie w otwartych kartach (Ctrl+Shift+A) =================

        void ShowTabSearch()
        {
            var search = new TextBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(8), FontSize = 14 };
            var list = new ListBox { Margin = new Thickness(8, 0, 8, 8), BorderThickness = new Thickness(0) };
            var dock = new DockPanel();
            DockPanel.SetDock(search, Dock.Top);
            dock.Children.Add(search); dock.Children.Add(list);
            var w = new Window { Title = L.T("Szukaj w kartach") + " (" + _tabs.Count + ")", Width = 640, Height = 480, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = dock };
            Action fill = () =>
            {
                list.Items.Clear();
                var q = search.Text.Trim();
                foreach (var t in _tabs)
                {
                    var url = t.View.CoreWebView2 != null ? t.View.CoreWebView2.Source : t.StartUrl ?? "";
                    var title = t.Title.Text ?? "";
                    if (q.Length > 0 && title.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0 && url.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var row = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
                    row.Children.Add(new TextBlock { Text = (t.Pinned ? "📌 " : "") + (t == _current ? "● " : "") + title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    row.Children.Add(new TextBlock { Text = url, Foreground = Brushes.Gray, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
                    list.Items.Add(new ListBoxItem { Content = row, Tag = t });
                }
                if (list.Items.Count > 0) list.SelectedIndex = 0;
            };
            Action go = () =>
            {
                var it = list.SelectedItem as ListBoxItem;
                if (it == null) return;
                var t = (BrowserTab)it.Tag;
                w.Close();
                if (_tabs.Contains(t)) { SelectTab(t); t.Header.BringIntoView(); }
            };
            search.TextChanged += (s, e) => fill();
            list.MouseDoubleClick += (s, e) => go();
            w.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { w.Close(); e.Handled = true; }
                else if (e.Key == Key.Enter) { go(); e.Handled = true; }
                else if (e.Key == Key.Down && list.Items.Count > 0) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; }
                else if (e.Key == Key.Up && list.Items.Count > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; }
            };
            fill();
            w.Loaded += (s, e) => search.Focus();
            w.ShowDialog();
        }
    }
}
