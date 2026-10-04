using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // 1. "Gdzie ja to czytalem?" - lokalna pamiec tresci stron z wyszukiwaniem po tresci.
    // 2. Wykrywacz sztuczek presji w sklepach (liczniki, "ostatnie sztuki", zaznaczone dodatki, ukryte oplaty).
    // 3. Wyslij karte na inny komputer z Velivo w sieci domowej (szyfrowane, bez chmury).
    // 4. Paragon prywatnosci strony (firmy, kraje, brokerzy danych, proby rozpoznania komputera).
    public partial class MainWindow
    {
        // ======================================================================
        //  1. Pamiec tresci stron
        // ======================================================================

        sealed class MemPage
        {
            public string Url { get; set; }
            public string Title { get; set; }
            public DateTime Time { get; set; }
            public string Text { get; set; }
            [JsonIgnore] public string Folded;
        }

        static string MemoryFile { get { return Path.Combine(DataDir, "pamiec-stron.jsonl"); } }
        static List<MemPage> _memory;
        static readonly object MemoryLock = new object();

        // strony, ktorych tresci nigdy nie zapamietujemy (bankowosc, platnosci, poczta, konta)
        static readonly string[] MemoryExcluded =
        {
            "bank", "pko", "ipko", "mbank", "ing.pl", "santander", "millennium", "pekao", "alior", "credit-agricole", "citi", "bnpparibas",
            "velobank", "nestbank", "revolut", "paypal", "przelewy24", "payu", "blik", "tpay", "dotpay", "epuap", "gov.pl", "zus.pl",
            "podatki", "mail.", "poczta", "outlook.", "accounts.", "login.", "signin", "auth.", "konto.", "account.",
        };

        static bool MemoryAllowed(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) return false;
            var h = u.Host.ToLowerInvariant();
            return !MemoryExcluded.Any(x => h.Contains(x));
        }

        static List<MemPage> MemoryPages()
        {
            lock (MemoryLock)
            {
                if (_memory != null) return _memory;
                _memory = new List<MemPage>();
                try
                {
                    if (File.Exists(MemoryFile))
                    {
                        var byUrl = new Dictionary<string, MemPage>();
                        foreach (var line in File.ReadLines(MemoryFile))
                        {
                            try { var p = JsonSerializer.Deserialize<MemPage>(line); if (p != null && p.Url != null) byUrl[p.Url] = p; }
                            catch (JsonException) { }
                        }
                        _memory = byUrl.Values.OrderBy(p => p.Time).ToList();
                    }
                }
                catch (IOException) { }
                return _memory;
            }
        }

        static void ForgetAllPageMemory()
        {
            lock (MemoryLock)
            {
                _memory = new List<MemPage>();
                try { if (File.Exists(MemoryFile)) File.Delete(MemoryFile); } catch (IOException) { }
            }
        }

        static void RewriteMemoryFile()
        {
            lock (MemoryLock)
            {
                try
                {
                    Directory.CreateDirectory(DataDir);
                    var tmp = MemoryFile + ".tmp";
                    File.WriteAllLines(tmp, _memory.Select(p => JsonSerializer.Serialize(p)));
                    File.Move(tmp, MemoryFile, true);
                }
                catch (IOException) { }
            }
        }

        async Task RememberPageTextAsync(BrowserTab tab, CoreWebView2 core)
        {
            try
            {
                if (tab.Private || !_settings.PageMemory || !_settings.SaveHistory || _settings.ClearOnExit) return;
                var url = core.Source;
                if (!MemoryAllowed(url)) return;
                await Task.Delay(2500);   // tresc czesto dochodzi skryptami po zaladowaniu
                if (!_tabs.Contains(tab) || tab.View.CoreWebView2 == null || tab.View.CoreWebView2.Source != url) return;
                var json = await core.ExecuteScriptAsync("(function(){try{if(document.querySelector('input[type=password]'))return '';var t=(document.body&&document.body.innerText)||'';return t.replace(/[ \\t]+/g,' ').replace(/\\n\\s*\\n+/g,'\\n').slice(0,12000);}catch(e){return '';}})()");
                var text = JsonSerializer.Deserialize<string>(json) ?? "";
                if (text.Length < 200) return;
                var page = new MemPage { Url = url, Title = core.DocumentTitle ?? "", Time = DateTime.Now, Text = text };
                await Task.Run(() =>
                {
                    lock (MemoryLock)
                    {
                        var list = MemoryPages();
                        list.RemoveAll(p => p.Url == url);
                        list.Add(page);
                        if (list.Count > 4000) { list.RemoveRange(0, list.Count - 3500); RewriteMemoryFile(); }
                        else { try { Directory.CreateDirectory(DataDir); File.AppendAllText(MemoryFile, JsonSerializer.Serialize(page) + "\n"); } catch (IOException) { } }
                    }
                });
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // male litery bez polskich znakow, ta sama dlugosc co oryginal (do wycinkow)
        static string Fold(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                char c = char.ToLowerInvariant(ch);
                switch (c)
                {
                    case 'ą': c = 'a'; break; case 'ć': c = 'c'; break; case 'ę': c = 'e'; break; case 'ł': c = 'l'; break;
                    case 'ń': c = 'n'; break; case 'ó': c = 'o'; break; case 'ś': c = 's'; break; case 'ź': case 'ż': c = 'z'; break;
                    default:
                        if (c > 127)
                        {
                            var d = c.ToString().Normalize(NormalizationForm.FormD);
                            if (d.Length > 0 && d[0] < 128) c = d[0];
                        }
                        break;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        sealed class MemHit { public MemPage Page; public int Score; public int Pos; }

        static List<MemHit> SearchPageMemory(string query)
        {
            var words = Fold(query ?? "").Split(new[] { ' ', ',', '.', ';', ':', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2).Distinct().ToList();
            var hits = new List<MemHit>();
            if (words.Count == 0) return hits;
            List<MemPage> pages;
            lock (MemoryLock) pages = MemoryPages().ToList();
            foreach (var p in pages)
            {
                if (p.Folded == null) p.Folded = Fold((p.Title ?? "") + "\n" + (p.Text ?? ""));
                int score = 0, first = -1, matched = 0;
                foreach (var w in words)
                {
                    int i = p.Folded.IndexOf(w, StringComparison.Ordinal), n = 0;
                    if (i >= 0) { matched++; if (first < 0 || i < first) first = i; }
                    while (i >= 0 && n < 5) { n++; i = p.Folded.IndexOf(w, i + w.Length, StringComparison.Ordinal); }
                    score += n;
                }
                if (matched == 0) continue;
                score += matched * 10;
                if (matched == words.Count) score += 100;   // wszystkie slowa - na gore
                hits.Add(new MemHit { Page = p, Score = score, Pos = first });
            }
            return hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.Page.Time).Take(60).ToList();
        }

        void ShowPageMemorySearch()
        {
            var search = new TextBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(8, 8, 8, 4), FontSize = 14 };
            var info = new TextBlock { Margin = new Thickness(10, 0, 10, 6), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
            var list = new ListBox { Margin = new Thickness(8, 0, 8, 8), BorderThickness = new Thickness(0) };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            var clear = new Button { Content = L.T("Wyczyść pamięć stron"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 8, 8), HorizontalAlignment = HorizontalAlignment.Right };
            var dock = new DockPanel();
            DockPanel.SetDock(search, Dock.Top); DockPanel.SetDock(info, Dock.Top); DockPanel.SetDock(clear, Dock.Bottom);
            dock.Children.Add(search); dock.Children.Add(info); dock.Children.Add(clear); dock.Children.Add(list);
            var w = new Window { Title = L.T("🧠 Gdzie ja to czytałem?"), Width = 820, Height = 600, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = dock };
            int total; lock (MemoryLock) total = MemoryPages().Count;
            Action fill = () =>
            {
                list.Items.Clear();
                var q = search.Text.Trim();
                if (q.Length < 2)
                {
                    info.Text = (L.En ? "Type words you remember from the text (e.g. \"laptop battery 6 hours\"). Remembered pages: " : "Wpisz słowa, które pamiętasz z treści (np. „bateria laptop 6 godzin”). Zapamiętanych stron: ") + total +
                                (_settings.PageMemory ? "" : L.T("  ·  zapamiętywanie jest wyłączone w ustawieniach"));
                    return;
                }
                var hits = SearchPageMemory(q);
                info.Text = (L.En ? "Found: " : "Znaleziono: ") + hits.Count;
                foreach (var h in hits)
                {
                    var p = h.Page;
                    var row = new StackPanel { Margin = new Thickness(2, 4, 2, 6) };
                    row.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(p.Title) ? p.Url : p.Title, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
                    Uri u; Uri.TryCreate(p.Url, UriKind.Absolute, out u);
                    row.Children.Add(new TextBlock { Text = (u != null ? u.Host.Replace("www.", "") : p.Url) + "  ·  " + p.Time.ToString("d MMM yyyy, HH:mm", L.En ? new CultureInfo("en-GB") : new CultureInfo("pl-PL")), Foreground = Brushes.SeaGreen, FontSize = 11 });
                    string snippet = "";
                    var full = (p.Title ?? "") + "\n" + (p.Text ?? "");
                    if (h.Pos >= 0 && h.Pos < full.Length)
                    {
                        int a = Math.Max(0, h.Pos - 90), b = Math.Min(full.Length, h.Pos + 180);
                        snippet = (a > 0 ? "…" : "") + full.Substring(a, b - a).Replace('\n', ' ') + (b < full.Length ? "…" : "");
                    }
                    row.Children.Add(new TextBlock { Text = snippet, Foreground = Brushes.DimGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 52 });
                    var item = new ListBoxItem { Content = row, Tag = p, ToolTip = p.Url };
                    var menu = new ContextMenu();
                    var open = new MenuItem { Header = L.T("Otwórz w nowej karcie") }; open.Click += (s, e) => AddTab(p.Url);
                    var forget = new MenuItem { Header = L.T("Usuń z pamięci") };
                    forget.Click += (s, e) => { lock (MemoryLock) { MemoryPages().Remove(p); RewriteMemoryFile(); } list.Items.Remove(item); };
                    menu.Items.Add(open); menu.Items.Add(forget);
                    item.ContextMenu = menu;
                    list.Items.Add(item);
                }
            };
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (s, e) => { timer.Stop(); fill(); };
            search.TextChanged += (s, e) => { timer.Stop(); timer.Start(); };
            list.MouseDoubleClick += (s, e) => { var it = list.SelectedItem as ListBoxItem; if (it != null) { AddTab(((MemPage)it.Tag).Url); w.Close(); } };
            w.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) w.Close();
                else if (e.Key == Key.Enter) { var it = (list.SelectedItem ?? (list.Items.Count > 0 ? list.Items[0] : null)) as ListBoxItem; if (it != null) { AddTab(((MemPage)it.Tag).Url); w.Close(); } }
            };
            clear.Click += (s, e) =>
            {
                if (MessageBox.Show(w, L.T("Usunąć całą zapamiętaną treść stron?"), "Velivo", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                ForgetAllPageMemory(); total = 0; fill();
            };
            fill();
            w.Loaded += (s, e) => search.Focus();
            w.Show();
        }

        // ======================================================================
        //  2. Sztuczki presji w sklepach
        // ======================================================================

        const string DarkPatternScript = @"
try { if (C.dark && window === window.top && /^https?:$/.test(location.protocol)) {
  var found = [], seenP = {};
  function addP(k, t, s) { t = (t || '').replace(/\s+/g, ' ').trim().slice(0, 140); var key = k + '|' + (k === 'timer' ? '' : t); if (seenP[key]) return; seenP[key] = 1; found.push({ k: k, t: t, s: s || 0 }); }
  var checkout = /(koszyk|cart|checkout|zamow|zamaw|order|basket|kasa|platnos|płatnoś|payment|rezerwac|booking)/i.test(location.href);
  var PRESS = /(promocj|oferta|kończy|konczy|zostało|zostalo|ends|left|offer|deal|sale|rabat|zniżk|znizk|okazj|tylko dziś|only today|hurry|spiesz|limited)/i;
  var TIME = /\b(\d{1,2}):(\d{2})(?::(\d{2}))?\b/;
  function secs(m) { return m[3] !== undefined ? (+m[1]) * 3600 + (+m[2]) * 60 + (+m[3]) : (+m[1]) * 60 + (+m[2]); }
  function timers() {
    var out = [], all = document.body ? document.body.getElementsByTagName('*') : [];
    for (var i = 0; i < all.length && out.length < 25; i++) {
      var el = all[i]; if (el.children.length > 3) continue;
      var t = el.textContent || ''; if (t.length > 40) continue;
      var m = TIME.exec(t); if (m) out.push({ el: el, v: secs(m) });
    }
    return out;
  }
  function scanDark() {
    var body = document.body; if (!body) return;
    var text = (body.innerText || '').slice(0, 60000), m, c;
    var re1 = /(tylko|zostały?|zostało|zostala|ostatnie|jeszcze)\s+\d{1,3}\s*(szt\.?|sztuk[ia]?|produkt\w*|miejsc\w*|pokoi|pokoje|bilet\w*)/gi; c = 0;
    while ((m = re1.exec(text)) && c++ < 3) addP('scarcity', m[0]);
    var re2 = /only\s+\d{1,3}\s+left|\d{1,3}\s+left in stock/gi; c = 0;
    while ((m = re2.exec(text)) && c++ < 2) addP('scarcity', m[0]);
    var re3 = /\d{1,4}\s+(os[oó]b|osoby|people|users|klient\w*)\s+(teraz\s+|w tej chwili\s+)?(ogląda|oglądają|przegląda|przeglądają|is viewing|are viewing|are looking|watching|kupił\w*|bought|zarezerwował\w*)/gi; c = 0;
    while ((m = re3.exec(text)) && c++ < 3) addP('social', m[0]);
    if (checkout) {
      var re4 = /(opłat\w* (serwisow|manipulacyjn|rezerwacyjn|administracyjn|za obsług)\w*|service fee|handling fee|booking fee|convenience fee)/gi; c = 0;
      while ((m = re4.exec(text)) && c++ < 3) addP('fee', m[0]);
      var boxes = document.querySelectorAll('input[type=checkbox]');
      for (var i = 0; i < boxes.length; i++) {
        var b = boxes[i]; if (!b.checked || b.disabled) continue;
        var lab = (b.labels && b.labels[0] ? b.labels[0].innerText : '') || ((b.closest('label') || b.parentElement || {}).innerText || '');
        if (/(ubezpiecz|gwarancj|ochron|newsletter|insurance|protection|warranty|subscribe|marketing)/i.test(lab) && !/(regulamin|terms|wymagan|required|akceptuj\w* regul)/i.test(lab)) {
          try { b.click(); if (b.checked) { b.checked = false; b.dispatchEvent(new Event('change', { bubbles: true })); } } catch (x) {}
          addP('preselect', lab);
        }
      }
    }
    var t1 = timers();
    setTimeout(function () {
      for (var i = 0; i < t1.length; i++) {
        var el = t1[i].el, m2 = TIME.exec(el.textContent || ''); if (!m2) continue;
        var v = secs(m2);
        if (v < t1[i].v && t1[i].v - v <= 3) {
          var box = el.parentElement && el.parentElement.parentElement ? el.parentElement.parentElement : el;
          var ctx = box.innerText || '';
          if (ctx.length < 500 && PRESS.test(ctx)) { addP('timer', ctx, v); break; }
        }
      }
      if (found.length) send('dark:' + JSON.stringify(found));
    }, 1600);
  }
  setTimeout(scanDark, 3000);
} } catch (x) {}";

        readonly Dictionary<string, DateTime> _timerEnds = new Dictionary<string, DateTime>();
        Button _pressureBtn;

        sealed class DarkFinding { public string k { get; set; } public string t { get; set; } public int s { get; set; } }

        void HandleDarkPatterns(BrowserTab tab, string json)
        {
            List<DarkFinding> list;
            try { list = JsonSerializer.Deserialize<List<DarkFinding>>(json); } catch (JsonException) { return; }
            if (list == null) return;
            var url = tab.View.CoreWebView2 != null ? tab.View.CoreWebView2.Source : "";
            var key = url.Split('?', '#')[0];
            foreach (var f in list.Take(10))
            {
                string text = (f.t ?? "").Trim();
                if (text.Length > 140) text = text.Substring(0, 140) + "…";
                string line;
                switch (f.k)
                {
                    case "timer":
                        var end = DateTime.Now.AddSeconds(f.s);
                        DateTime prev;
                        bool fake = _timerEnds.TryGetValue(key, out prev) && end > prev.AddSeconds(90);
                        if (!_timerEnds.ContainsKey(key)) _timerEnds[key] = end;
                        line = fake
                            ? L.T("⏱ Fałszywy licznik – po odświeżeniu strony odlicza od nowa: ") + text
                            : L.T("⏱ Licznik odliczający presję czasu (odśwież stronę – jeśli zacznie od nowa, jest fałszywy): ") + text;
                        break;
                    case "scarcity": line = L.T("📦 Presja „ostatnich sztuk”: ") + text; break;
                    case "social": line = L.T("👥 Presja „inni właśnie kupują / oglądają”: ") + text; break;
                    case "preselect": line = L.T("☑ Zaznaczony z góry dodatek – Velivo go odznaczyło: ") + text; break;
                    case "fee": line = L.T("💸 Dodatkowa opłata pojawiająca się przy zamówieniu: ") + text; break;
                    default: continue;
                }
                tab.Pressure.RemoveAll(x => f.k == "timer" && x.StartsWith("⏱"));
                if (!tab.Pressure.Contains(line)) tab.Pressure.Add(line);
            }
            if (tab == _current) UpdatePressureButton();
        }

        void InitInnovationsUi()
        {
            try
            {
                var parent = AdToggle.Parent as Panel;
                if (parent == null) return;
                _pressureBtn = new Button
                {
                    Visibility = Visibility.Collapsed, Margin = new Thickness(4, 7, 4, 7), Padding = new Thickness(10, 0, 10, 0), Width = double.NaN, Height = double.NaN,
                    Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7)), Foreground = new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E)),
                    FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0)
                };
                DockPanel.SetDock(_pressureBtn, Dock.Right);
                parent.Children.Insert(parent.Children.IndexOf(AdToggle) + 1, _pressureBtn);
                _pressureBtn.Click += (s, e) => ShowPressureDetails();
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void UpdatePressureButton()
        {
            if (_pressureBtn == null) return;
            int n = _current != null ? _current.Pressure.Count : 0;
            _pressureBtn.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            _pressureBtn.Content = "⚠ " + n;
            _pressureBtn.ToolTip = L.En ? "This shop uses " + n + " pressure tricks – click for details" : "Ten sklep używa sztuczek presji: " + n + " – kliknij, aby zobaczyć";
        }

        void ShowPressureDetails()
        {
            var tab = _current;
            if (tab == null || tab.Pressure.Count == 0) return;
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                Text = (L.En ? "⚠ This shop uses " : "⚠ Ten sklep używa sztuczek presji: ") + tab.Pressure.Count + (L.En ? " pressure tricks" : ""),
                FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8)
            });
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Takie elementy mają skłonić do szybkiego zakupu bez zastanowienia. Nie musisz się spieszyć – sprawdź cenę gdzie indziej i wróć, kiedy chcesz."),
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10)
            });
            foreach (var p in tab.Pressure)
                panel.Children.Add(new TextBlock { Text = "• " + p, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) });
            var w = new Window { Title = L.T("Sztuczki presji w sklepie"), Width = 620, SizeToContent = SizeToContent.Height, MaxHeight = 600, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            w.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.ShowDialog();
        }

        // ======================================================================
        //  3. Wyslij karte do innego Velivo w sieci
        // ======================================================================

        sealed class LanTabPayload
        {
            public string url { get; set; }
            public string title { get; set; }
            public double time { get; set; }
            public string to { get; set; }
        }

        List<LanPeerInfo> SendTargets()
        {
            return _lanPeers.Values
                .Where(p => p.Id != _lanId && DateTime.UtcNow - p.LastSeenUtc < TimeSpan.FromMinutes(3) &&
                            string.Equals(p.Profile ?? "", SelectedProfileName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(p => p.Id).Select(g => g.OrderByDescending(x => x.LastSeenUtc).First())
                .OrderBy(p => p.Device).ToList();
        }

        MenuItem BuildSendTabMenu(BrowserTab tab)
        {
            var root = new MenuItem { Header = L.T("📺 Wyślij do…") };
            root.Items.Add(new MenuItem());
            root.SubmenuOpened += (s, e) =>
            {
                if (e.OriginalSource != root) return;
                root.Items.Clear();
                var targets = SendTargets();
                if (_lanEncryptionKey == null || _lanLegacyNoKeyMode || !_settings.LanSync)
                    root.Items.Add(new MenuItem { Header = L.T("Włącz i sparuj synchronizację LAN w ustawieniach"), IsEnabled = false });
                else if (targets.Count == 0)
                    root.Items.Add(new MenuItem { Header = L.T("Brak innych Velivo w sieci (muszą być włączone)"), IsEnabled = false });
                foreach (var p0 in targets)
                {
                    var p = p0;
                    var it = new MenuItem { Header = "💻 " + (string.IsNullOrWhiteSpace(p.Device) ? p.Id.Substring(0, 8) : p.Device) };
                    it.Click += async (a, b) => await SendTabToPeer(tab, p);
                    root.Items.Add(it);
                }
            };
            return root;
        }

        async Task SendTabToPeer(BrowserTab tab, LanPeerInfo peer)
        {
            var core = tab.View.CoreWebView2;
            if (core == null || _lanTx == null || _lanEncryptionKey == null) return;
            var url = core.Source;
            if (!(url.StartsWith("http://") || url.StartsWith("https://"))) { ShowToast(L.T("Tej karty nie da się wysłać (to nie jest zwykła strona)."), null); return; }
            double time = 0;
            try
            {
                var r = await core.ExecuteScriptAsync("(function(){var v=Array.prototype.slice.call(document.querySelectorAll('video')).filter(function(x){return x.currentTime>0;}).sort(function(a,b){return b.clientWidth*b.clientHeight-a.clientWidth*a.clientHeight;})[0];return v?v.currentTime:0;})()");
                double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out time);
            }
            catch (Exception) { }
            var payload = new LanTabPayload { url = url, title = core.DocumentTitle, time = time, to = peer.Id };
            var pkt = new LanStatePacket { t = "tab", id = _lanId, device = _lanDeviceName, profile = SelectedProfileName, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hash = "" };
            EncryptLanBlob(pkt, JsonSerializer.SerializeToUtf8Bytes(payload));
            LanSend(pkt);
            ShowToast((L.En ? "📺 Sent to " : "📺 Wysłano do: ") + peer.Device + (time > 5 ? (L.En ? " (video from " : " (film od ") + TimeSpan.FromSeconds(time).ToString(time >= 3600 ? @"h\:mm\:ss" : @"m\:ss") + ")" : ""), null);
        }

        void EncryptLanBlob(LanStatePacket pkt, byte[] json)
        {
            var plain = CompressLanPayload(json);
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(12);
                var cipher = new byte[plain.Length];
                var tag = new byte[16];
                using (var gcm = new AesGcm(_lanEncryptionKey, 16))
                    gcm.Encrypt(nonce, plain, cipher, tag, LanAssociatedData(pkt));
                pkt.nonce = Convert.ToBase64String(nonce);
                pkt.tag = Convert.ToBase64String(tag);
                pkt.data = Convert.ToBase64String(cipher);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        T DecryptLanBlob<T>(LanStatePacket pkt) where T : class
        {
            if (_lanEncryptionKey == null || string.IsNullOrWhiteSpace(pkt.nonce) || string.IsNullOrWhiteSpace(pkt.tag) || string.IsNullOrWhiteSpace(pkt.data)) return null;
            var nonce = Convert.FromBase64String(pkt.nonce);
            var tag = Convert.FromBase64String(pkt.tag);
            var cipher = Convert.FromBase64String(pkt.data);
            if (nonce.Length != 12 || tag.Length != 16 || cipher.Length == 0 || cipher.Length > 60000) return null;
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(_lanEncryptionKey, 16))
                gcm.Decrypt(nonce, cipher, tag, plain, LanAssociatedData(pkt));
            return JsonSerializer.Deserialize<T>(DecompressLanPayload(plain));
        }

        void ReceiveLanTab(LanStatePacket pkt, LanTabPayload p)
        {
            if (p == null || p.to != _lanId || string.IsNullOrEmpty(p.url)) return;
            if (!(p.url.StartsWith("http://") || p.url.StartsWith("https://")) || p.url.Length > 4000) return;
            var url = p.url;
            // YouTube: czas w adresie dziala najpewniej
            if (p.time > 5 && (url.Contains("youtube.com/watch") || url.Contains("youtu.be/")))
            {
                url = System.Text.RegularExpressions.Regex.Replace(url, @"([?&])t=[^&]*&?", "$1").TrimEnd('&', '?');
                url += (url.Contains("?") ? "&" : "?") + "t=" + (int)p.time + "s";
            }
            AddTab(url);
            var tab = _tabs[_tabs.Count - 1];
            if (p.time > 5) tab.PendingVideoTime = p.time;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
            Activate();
            ShowToast((L.En ? "📺 Tab from " : "📺 Karta z komputera ") + (pkt.device ?? "?") + ": " + (string.IsNullOrWhiteSpace(p.title) ? url : p.title), null);
        }

        async Task ApplyPendingVideoTime(BrowserTab tab, CoreWebView2 core)
        {
            if (tab.PendingVideoTime <= 0) return;
            var t = tab.PendingVideoTime;
            tab.PendingVideoTime = 0;
            try
            {
                await Task.Delay(1500);
                await core.ExecuteScriptAsync("(function(t){var v=Array.prototype.slice.call(document.querySelectorAll('video')).sort(function(a,b){return b.clientWidth*b.clientHeight-a.clientWidth*a.clientHeight;})[0];if(v&&Math.abs(v.currentTime-t)>5){v.currentTime=t;}})(" + t.ToString(CultureInfo.InvariantCulture) + ")");
            }
            catch (Exception) { }
        }

        // ======================================================================
        //  4. Paragon prywatnosci
        // ======================================================================

        const string FingerprintScript = @"
try { if (C.receipt) {
  var fp = {}, fpT = 0;
  var noteFp = function (k) { fp[k] = (fp[k] || 0) + 1; if (!fpT) fpT = setTimeout(function () { fpT = 0; send('fp:' + JSON.stringify(fp)); fp = {}; }, 2000); };
  var cp = HTMLCanvasElement.prototype, td = cp.toDataURL, tb = cp.toBlob;
  cp.toDataURL = function () { if (this.width * this.height < 250000 && !this.isConnected) noteFp('canvas'); return td.apply(this, arguments); };
  if (tb) cp.toBlob = function () { if (this.width * this.height < 250000 && !this.isConnected) noteFp('canvas'); return tb.apply(this, arguments); };
  [window.WebGLRenderingContext, window.WebGL2RenderingContext].forEach(function (W) {
    if (!W) return; var gp = W.prototype.getParameter;
    W.prototype.getParameter = function (p) { if (p === 37445 || p === 37446) noteFp('webgl'); return gp.apply(this, arguments); };
  });
  ['OfflineAudioContext', 'webkitOfflineAudioContext'].forEach(function (n) {
    var A = window[n]; if (!A) return;
    window[n] = new Proxy(A, { construct: function (t, a, nt) { noteFp('audio'); return Reflect.construct(t, a, nt); } });
  });
} } catch (x) {}";

        static string RegistrableDomain(string host)
        {
            if (string.IsNullOrEmpty(host)) return host;
            var p = host.ToLowerInvariant().TrimEnd('.').Split('.');
            if (p.Length <= 2) return string.Join(".", p);
            var second = p[p.Length - 2];
            bool cc = p[p.Length - 1].Length == 2 && (second == "co" || second == "com" || second == "org" || second == "net" || second == "gov" || second == "edu" || second == "ac");
            return string.Join(".", p.Skip(p.Length - (cc ? 3 : 2)));
        }

        void RecordThirdParty(BrowserTab tab, Uri request)
        {
            if (tab.PageSite == null) return;
            var site = RegistrableDomain(request.Host);
            if (site == tab.PageSite || site.Length == 0) return;
            int n;
            tab.ThirdParties.TryGetValue(site, out n);
            if (n == 0 && tab.ThirdParties.Count > 400) return;
            tab.ThirdParties[site] = n + 1;
        }

        void HandleFingerprintReport(BrowserTab tab, string json)
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                if (d == null) return;
                foreach (var kv in d)
                {
                    if (kv.Key != "canvas" && kv.Key != "webgl" && kv.Key != "audio") continue;
                    int n; tab.Fingerprint.TryGetValue(kv.Key, out n);
                    tab.Fingerprint[kv.Key] = Math.Min(999, n + Math.Max(0, kv.Value));
                }
            }
            catch (JsonException) { }
        }

        // domena -> firma, kraj, czy to broker danych
        static readonly (string Domain, string Company, string Country, bool Broker)[] KnownCompanies =
        {
            ("google.com", "Google", "USA", false), ("googleapis.com", "Google", "USA", false), ("gstatic.com", "Google", "USA", false),
            ("googlesyndication.com", "Google", "USA", false), ("doubleclick.net", "Google", "USA", false), ("googletagmanager.com", "Google", "USA", false),
            ("google-analytics.com", "Google", "USA", false), ("googleadservices.com", "Google", "USA", false), ("googletagservices.com", "Google", "USA", false),
            ("youtube.com", "Google", "USA", false), ("ytimg.com", "Google", "USA", false), ("google.pl", "Google", "USA", false),
            ("facebook.com", "Meta", "USA", false), ("facebook.net", "Meta", "USA", false), ("fbcdn.net", "Meta", "USA", false), ("instagram.com", "Meta", "USA", false),
            ("amazon-adsystem.com", "Amazon", "USA", false), ("amazonaws.com", "Amazon", "USA", false), ("cloudfront.net", "Amazon", "USA", false),
            ("microsoft.com", "Microsoft", "USA", false), ("bing.com", "Microsoft", "USA", false), ("clarity.ms", "Microsoft", "USA", false), ("msn.com", "Microsoft", "USA", false),
            ("linkedin.com", "Microsoft (LinkedIn)", "USA", false), ("licdn.com", "Microsoft (LinkedIn)", "USA", false),
            ("twitter.com", "X (Twitter)", "USA", false), ("x.com", "X (Twitter)", "USA", false), ("twimg.com", "X (Twitter)", "USA", false), ("ads-twitter.com", "X (Twitter)", "USA", false),
            ("tiktok.com", "TikTok (ByteDance)", "Chiny", false), ("tiktokcdn.com", "TikTok (ByteDance)", "Chiny", false), ("byteoversea.com", "TikTok (ByteDance)", "Chiny", false),
            ("pinterest.com", "Pinterest", "USA", false), ("snapchat.com", "Snap", "USA", false), ("sc-static.net", "Snap", "USA", false),
            ("criteo.com", "Criteo", "Francja", false), ("criteo.net", "Criteo", "Francja", false), ("smartadserver.com", "Equativ", "Francja", false),
            ("adform.net", "Adform", "Dania", false), ("rtbhouse.com", "RTB House", "Polska", false), ("creativecdn.com", "RTB House", "Polska", false),
            ("gemius.pl", "Gemius", "Polska", false), ("gemius.com", "Gemius", "Polska", false), ("onaudience.com", "OnAudience", "Polska", true),
            ("wp.pl", "Wirtualna Polska", "Polska", false), ("wpimg.pl", "Wirtualna Polska", "Polska", false), ("onet.pl", "Ringier Axel Springer (Onet)", "Polska", false),
            ("ocdn.eu", "Ringier Axel Springer (Onet)", "Polska", false), ("interia.pl", "Interia", "Polska", false), ("dotmetrics.net", "Dotmetrics", "Chorwacja", false),
            ("yandex.ru", "Yandex", "Rosja", false), ("yandex.net", "Yandex", "Rosja", false), ("yastatic.net", "Yandex", "Rosja", false), ("mail.ru", "VK (Mail.ru)", "Rosja", false),
            ("taboola.com", "Taboola", "Izrael", false), ("outbrain.com", "Outbrain", "USA", false), ("hotjar.com", "Hotjar", "Malta", false),
            ("cloudflare.com", "Cloudflare", "USA", false), ("cloudflareinsights.com", "Cloudflare", "USA", false),
            ("adsrvr.org", "The Trade Desk", "USA", true), ("bluekai.com", "Oracle", "USA", true), ("addthis.com", "Oracle", "USA", true),
            ("liveramp.com", "LiveRamp", "USA", true), ("rlcdn.com", "LiveRamp", "USA", true), ("acxiom.com", "Acxiom", "USA", true),
            ("lotame.com", "Lotame", "USA", true), ("crwdcntrl.net", "Lotame", "USA", true), ("quantserve.com", "Quantcast", "USA", true),
            ("quantcount.com", "Quantcast", "USA", true), ("tapad.com", "Tapad", "USA", true), ("exelator.com", "Nielsen (eXelate)", "USA", true),
            ("scorecardresearch.com", "Comscore", "USA", true), ("id5-sync.com", "ID5", "Wielka Brytania", true), ("bidswitch.net", "BidSwitch", "USA", true),
            ("pubmatic.com", "PubMatic", "USA", false), ("rubiconproject.com", "Magnite", "USA", false), ("openx.net", "OpenX", "USA", false),
            ("onetrust.com", "OneTrust", "USA", false), ("cookielaw.org", "OneTrust", "USA", false), ("jsdelivr.net", "jsDelivr (CDN)", null, false),
        };

        static readonly Dictionary<string, string> CountryByTld = new Dictionary<string, string>
        {
            { "pl", "Polska" }, { "de", "Niemcy" }, { "fr", "Francja" }, { "uk", "Wielka Brytania" }, { "nl", "Holandia" }, { "ru", "Rosja" },
            { "cn", "Chiny" }, { "cz", "Czechy" }, { "sk", "Słowacja" }, { "it", "Włochy" }, { "es", "Hiszpania" }, { "ie", "Irlandia" },
            { "se", "Szwecja" }, { "ua", "Ukraina" }, { "us", "USA" }, { "jp", "Japonia" }, { "il", "Izrael" }, { "at", "Austria" },
            { "ch", "Szwajcaria" }, { "be", "Belgia" }, { "dk", "Dania" }, { "no", "Norwegia" }, { "fi", "Finlandia" }, { "lt", "Litwa" },
            { "hr", "Chorwacja" }, { "ro", "Rumunia" }, { "hu", "Węgry" }, { "by", "Białoruś" }, { "in", "Indie" }, { "br", "Brazylia" },
        };

        static readonly Dictionary<string, string> CountryEn = new Dictionary<string, string>
        {
            { "Polska", "Poland" }, { "Niemcy", "Germany" }, { "Francja", "France" }, { "Wielka Brytania", "United Kingdom" }, { "Holandia", "Netherlands" },
            { "Rosja", "Russia" }, { "Chiny", "China" }, { "Czechy", "Czechia" }, { "Słowacja", "Slovakia" }, { "Włochy", "Italy" }, { "Hiszpania", "Spain" },
            { "Irlandia", "Ireland" }, { "Szwecja", "Sweden" }, { "Ukraina", "Ukraine" }, { "Japonia", "Japan" }, { "Izrael", "Israel" },
            { "Szwajcaria", "Switzerland" }, { "Belgia", "Belgium" }, { "Dania", "Denmark" }, { "Norwegia", "Norway" }, { "Finlandia", "Finland" },
            { "Litwa", "Lithuania" }, { "Chorwacja", "Croatia" }, { "Rumunia", "Romania" }, { "Węgry", "Hungary" }, { "Białoruś", "Belarus" },
            { "Indie", "India" }, { "Brazylia", "Brazil" }, { "Malta", "Malta" }, { "Austria", "Austria" }, { "USA", "USA" },
        };

        static string CountryName(string pl) { string en; return L.En && pl != null && CountryEn.TryGetValue(pl, out en) ? en : pl; }

        static (string Company, string Country, bool Broker) Identify(string site)
        {
            foreach (var k in KnownCompanies)
                if (site == k.Domain || site.EndsWith("." + k.Domain)) return (k.Company, k.Country, k.Broker);
            string country = null;
            var tld = site.Substring(site.LastIndexOf('.') + 1);
            CountryByTld.TryGetValue(tld, out country);
            return (site, country, false);
        }

        UIElement BuildPrivacyReceipt(BrowserTab tab)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var border = new Border
            {
                Child = panel, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)), BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)), BorderThickness = new Thickness(1)
            };
            panel.Children.Add(new TextBlock { Text = L.T("🧾 Paragon prywatności tej strony"), FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 4) });
            if (tab == null) return border;
            var companies = tab.ThirdParties
                .Select(kv => new { Site = kv.Key, Count = kv.Value, Info = Identify(kv.Key) })
                .GroupBy(x => x.Info.Company)
                .Select(g => new { Company = g.Key, Count = g.Sum(x => x.Count), Country = g.Select(x => x.Info.Country).FirstOrDefault(c => c != null), Broker = g.Any(x => x.Info.Broker) })
                .OrderByDescending(x => x.Count).ToList();
            var countries = companies.Where(c => c.Country != null).Select(c => c.Country).Distinct().ToList();
            Action<string> line = t => panel.Children.Add(new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
            if (companies.Count == 0) line(L.T("Strona nie łączyła się z żadną zewnętrzną firmą."));
            else
                line((L.En ? "This page tried to connect to " + companies.Count + " outside companies" : "Ta strona próbowała połączyć się z " + companies.Count + " zewnętrznymi firmami") +
                     (countries.Count > 0 ? (L.En ? " in at least " + countries.Count + " countries: " : " w co najmniej " + countries.Count + " krajach: ") + string.Join(", ", countries.Select(CountryName)) : "") + ".");
            line((L.En ? "Blocked by Velivo: " : "Zablokowało Velivo: ") + tab.Blocked);
            int fpTotal = tab.Fingerprint.Values.Sum();
            if (fpTotal > 0)
            {
                var parts = new List<string>();
                int n;
                if (tab.Fingerprint.TryGetValue("canvas", out n)) parts.Add((L.En ? "canvas image " : "obraz canvas ") + n);
                if (tab.Fingerprint.TryGetValue("webgl", out n)) parts.Add((L.En ? "graphics card " : "karta graficzna ") + n);
                if (tab.Fingerprint.TryGetValue("audio", out n)) parts.Add((L.En ? "audio " : "dźwięk ") + n);
                line((L.En ? "⚠ Attempts to identify your computer (fingerprinting): " : "⚠ Próby rozpoznania Twojego komputera (fingerprinting): ") + fpTotal + " (" + string.Join(", ", parts) + ")");
            }
            else line(L.T("Nie wykryto prób rozpoznania komputera (fingerprinting)."));
            var brokers = companies.Where(c => c.Broker).Select(c => c.Company).ToList();
            line(brokers.Count > 0
                ? (L.En ? "⚠ Data brokers: " : "⚠ Brokerzy danych (handlują profilami ludzi): ") + brokers.Count + " – " + string.Join(", ", brokers)
                : L.T("Nie wykryto znanych brokerów danych."));
            if (companies.Count > 0)
                line((L.En ? "Most contacted: " : "Najczęściej: ") + string.Join(", ", companies.Take(8).Select(c => c.Company + (c.Country != null ? " (" + CountryName(c.Country) + ")" : "") + " ×" + c.Count)));
            return border;
        }
    }
}
