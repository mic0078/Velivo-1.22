using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Przegladarka
{
    // Czytanie na glos (glosy Windows przez speechSynthesis silnika): glowna tresc strony albo zaznaczenie.
    // Czytany akapit jest podswietlany i przewijany na srodek - latwo sledzic wzrokiem.
    public partial class MainWindow
    {
        static readonly double[] ReadRates = { 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 };
        DispatcherTimer _readTimer;
        BrowserTab _readTab;

        // Czytnik wstrzykiwany do strony. Sterowanie: start(tylkoZaznaczenie, tempo, glos), pause, resume, stop, state.
        const string ReaderScript = @"(() => {
  if (window.__velivoRead) return;
  const S = speechSynthesis;
  const st = { items: [], i: 0, rate: 1, volume: 1, external: false, pending: null, seq: 0, voice: null, active: false, paused: false, mark: null, lang: '' };
  const HL = 'velivo-czyta';
  const style = document.createElement('style');
  style.textContent = '.' + HL + '{background:rgba(255,213,0,.45)!important;outline:3px solid #f59e0b!important;border-radius:4px;transition:background .2s}';
  (document.head || document.documentElement).appendChild(style);

  // glowna tresc: article/main albo blok z najwieksza iloscia tekstu w akapitach
  const mainRoot = () => {
    const cand = [...document.querySelectorAll('article, main, [role=main], #content, .content, .article, .post')];
    let best = null, bestLen = 0;
    const score = el => [...el.querySelectorAll('p')].reduce((n, p) => n + p.innerText.length, 0);
    for (const c of cand) { const s = score(c); if (s > bestLen) { best = c; bestLen = s; } }
    if (!best || bestLen < 400) {
      for (const d of document.querySelectorAll('div, section')) { const s = score(d); if (s > bestLen * 1.2 || (!best && s > 0)) { if (s > bestLen) { best = d; bestLen = s; } } }
    }
    return best || document.body;
  };
  const visible = el => { const r = el.getBoundingClientRect(); const cs = getComputedStyle(el); return r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none'; };
  const SKIP = 'nav, footer, aside, header nav, form, button, script, style, noscript, figure figcaption, [aria-hidden=true], .ad, .ads, .advert, .share, .social, .related, .comments';
  const collect = root => {
    const out = [];
    for (const el of root.querySelectorAll('h1, h2, h3, h4, p, li, blockquote, dd, figcaption, td')) {
      if (el.closest(SKIP) && !root.matches(SKIP)) continue;
      if (el.querySelector('p, li, h1, h2, h3, h4, blockquote')) continue; // tylko najglebsze bloki
      const t = el.innerText.replace(/\s+/g, ' ').trim();
      if (t.length < 2 || !visible(el)) continue;
      out.push({ el, text: t });
    }
    if (out.length === 0) { const t = root.innerText.replace(/\s+/g, ' ').trim(); if (t) out.push({ el: root, text: t }); }
    // tytul strony na poczatek, jesli nie ma go w tresci
    const h1 = document.querySelector('h1');
    if (h1 && !root.contains(h1) && visible(h1)) out.unshift({ el: h1, text: h1.innerText.trim() });
    return out;
  };
  // dluzsze bloki dzielimy na zdania (krotsze wypowiedzi = plynniej, pauza dziala od razu)
  const splitSentences = t => (t.match(/[^.!?…]+[.!?…]+[""')\]]*\s*|[^.!?…]+$/g) || [t]).map(s => s.trim()).filter(Boolean);
  // dlugie zdania dzielimy na przecinkach (do ~120 znakow) - zmiana glosnosci/predkosci szybciej wchodzi w zycie
  const split = t => splitSentences(t).flatMap(z => {
    if (z.length <= 140) return [z];
    const out = []; let cur = '';
    for (const part of z.split(/(?<=[,;:–—])\s+/)) {
      if (cur && (cur + ' ' + part).length > 120) { out.push(cur); cur = part; } else cur = cur ? cur + ' ' + part : part;
    }
    if (cur) out.push(cur);
    return out;
  });
  // Glos: wybrany przez uzytkownika, a przy 'Automatycznie' - w jezyku strony (polski/angielski),
  // najchetniej naturalny glos online (Microsoft ... Online (Natural)), gdy silnik go udostepnia.
  const pickVoice = (voices, name) => {
    if (name) { const v = voices.find(x => x.name === name); if (v) return v; }
    const pageLang = (document.documentElement.lang || '').toLowerCase().slice(0, 2) === 'en' ? 'en' : 'pl';
    const inLang = voices.filter(x => (x.lang || '').toLowerCase().startsWith(pageLang));
    return inLang.find(x => /natural/i.test(x.name)) || inLang.find(x => /paulina|aria|jenny/i.test(x.name)) || inLang[0] ||
           voices.find(x => /^pl/i.test(x.lang)) || voices[0] || null;
  };
  const unmark = () => { if (st.mark) st.mark.classList.remove(HL); st.mark = null; };
  const speakNext = () => {
    if (!st.active) return;
    if (st.i >= st.items.length) { st.active = false; unmark(); return; }
    const it = st.items[st.i];
    if (it.el && st.mark !== it.el) {
      unmark(); st.mark = it.el; it.el.classList.add(HL);
      const r = it.el.getBoundingClientRect();
      if (r.top < 60 || r.bottom > innerHeight - 60) it.el.scrollIntoView({ block: 'center', behavior: 'smooth' });
    }
    // glos zewnetrzny (Piper w programie Velivo): zdanie odbiera program, ktory po odtworzeniu wola done()
    if (st.external) {
      const nx = st.items[st.i + 1];
      st.pending = { seq: ++st.seq, i: st.i, text: it.text, next: nx ? nx.text : '' };
      return;
    }
    const u = new SpeechSynthesisUtterance(it.text);
    u.rate = st.rate; u.volume = st.volume; if (st.voice) u.voice = st.voice; u.lang = st.voice ? st.voice.lang : 'pl-PL';
    u.onend = () => { if (!st.active || st.paused) return; st.i++; speakNext(); };
    u.onerror = e => { if (e.error === 'interrupted' || e.error === 'canceled') return; st.i++; speakNext(); };
    S.speak(u);
  };
  window.__velivoRead = {
    start(onlySelection, rate, voiceName) {
      S.cancel(); unmark();
      const voices = S.getVoices();
      st.lang = (document.documentElement.lang || '').toLowerCase();
      st.voice = pickVoice(voices, voiceName);
      st.rate = rate; st.i = 0; st.paused = false;
      const sel = getSelection(); const selText = sel ? sel.toString().replace(/\s+/g, ' ').trim() : '';
      let blocks;
      if (onlySelection && selText.length > 0) {
        const anchor = sel && sel.rangeCount ? sel.getRangeAt(0).commonAncestorContainer : null;
        const el = anchor ? (anchor.nodeType === 1 ? anchor : anchor.parentElement) : null;
        blocks = [{ el, text: selText }];
      } else blocks = collect(mainRoot());
      st.items = [];
      for (const b of blocks) for (const s of split(b.text)) st.items.push({ el: b.el, text: s });
      st.active = st.items.length > 0;
      speakNext();
      return st.items.length;
    },
    // czytanie od miejsca klikniecia: akapit pod mysza, od zdania, w ktore kliknieto
    startAt(x, y, rate, voiceName) {
      let node = null, off = 0;
      if (document.caretRangeFromPoint) { const r = document.caretRangeFromPoint(x, y); if (r) { node = r.startContainer; off = r.startOffset; } }
      if (!node) return 0;
      S.cancel(); unmark();
      const voices = S.getVoices();
      st.voice = pickVoice(voices, voiceName);
      st.rate = rate; st.paused = false;
      const blocks = collect(mainRoot());
      st.items = [];
      let startIdx = -1;
      for (const b of blocks) {
        const parts = split(b.text);
        if (startIdx < 0 && b.el && b.el.contains(node)) {
          let clicked = 0;
          try { const r = document.createRange(); r.setStart(b.el, 0); r.setEnd(node, off); clicked = r.toString().replace(/\s+/g, ' ').length; } catch (e) {}
          let pos = 0, k = 0;
          for (; k < parts.length - 1; k++) { pos += parts[k].length + 1; if (pos > clicked) break; }
          startIdx = st.items.length + k;
        }
        for (const s of parts) st.items.push({ el: b.el, text: s });
      }
      if (startIdx < 0) return 0;
      st.i = startIdx;
      st.active = st.items.length > 0;
      speakNext();
      return st.items.length - startIdx;
    },
    // glosy Windows w silniku nie obsluguja speechSynthesis.pause() - pauza = zatrzymanie i zapamietanie
    // miejsca; wznowienie czyta przerwane zdanie od poczatku
    pause() { if (st.active && !st.paused) { st.paused = true; S.cancel(); } },
    resume() { if (st.active && st.paused) { st.paused = false; speakNext(); } },
    stop() { st.active = false; st.paused = false; S.cancel(); unmark(); },
    volume(v) { st.volume = Math.max(0, Math.min(1, v)); }, // glosnosc 0-1: bez przerywania, dziala od nastepnego fragmentu (glos systemowy nie zmienia glosnosci w trakcie zdania)
    rate(r) { st.rate = r; if (st.active && !st.paused) { S.cancel(); speakNext(); } }, // od biezacego zdania
    state() { return JSON.stringify({ active: st.active, paused: st.paused, i: st.i, n: st.items.length, lang: st.lang, seq: st.seq }); },
    setExternal(b) { st.external = !!b; },
    take() { const p = st.pending; st.pending = null; return p ? JSON.stringify(p) : ''; },
    done(seq) { if (st.active && !st.paused && seq === st.seq) { st.i++; speakNext(); } }
  };
  addEventListener('pagehide', () => { try { window.__velivoRead.stop(); } catch (e) {} });
})();";

        // Polskie glosy dostepne w systemie (do wyboru w ustawieniach).
        System.Collections.Generic.List<string> _voiceNames = new System.Collections.Generic.List<string>();

        async void LoadVoiceNames(Microsoft.Web.WebView2.Core.CoreWebView2 core)
        {
            if (_voiceNames.Count > 1 || core == null) return;   // glosy online potrafia dojsc pozniej - probujemy, dopoki lista jest uboga
            try
            {
                const string js = "new Promise(r => { const go = () => r(JSON.stringify(speechSynthesis.getVoices().filter(v => /^(pl|en)/i.test(v.lang)).sort((a, b) => (/^pl/i.test(b.lang) - /^pl/i.test(a.lang)) || (/natural/i.test(b.name) - /natural/i.test(a.name))).map(v => v.name + '|' + v.lang + '|' + (v.localService ? 1 : 0)))); " +
                                  "if (speechSynthesis.getVoices().length) go(); else { speechSynthesis.onvoiceschanged = go; setTimeout(go, 3000); } })";
                var res = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = js, awaitPromise = true, returnByValue = true }));
                using (var d = JsonDocument.Parse(res))
                {
                    var val = d.RootElement.GetProperty("result").GetProperty("value").GetString();
                    _voiceNames = JsonSerializer.Deserialize<System.Collections.Generic.List<string>>(val) ?? _voiceNames;
                }
            }
            catch (Exception) { }
        }

        async Task<bool> EnsureReader(BrowserTab tab)
        {
            var core = tab.View.CoreWebView2;
            if (core == null) return false;
            await core.ExecuteScriptAsync(ReaderScript);
            await core.ExecuteScriptAsync("try{ window.__velivoRead.volume(" + Num(_settings.ReadVolume) + "); }catch(e){}");
            await PreparePiperReading(core);   // glos Piper (offline) - dzwiek z programu, nie z silnika
            return true;
        }

        string Num(double d) { return d.ToString(CultureInfo.InvariantCulture); }

        // onlySelection: true = zaznaczenie, false = cala strona
        async void StartReading(bool onlySelection)
        {
            var tab = _current;
            if (tab == null || !await EnsureReader(tab)) return;
            if (_readTab != null && _readTab != tab) StopReading();
            var n = await tab.View.CoreWebView2.ExecuteScriptAsync("window.__velivoRead.start(" + (onlySelection ? "true" : "false") + "," +
                Num(_settings.ReadRate) + "," + JsonSerializer.Serialize(_settings.ReadVoice ?? "") + ")");
            if (n == "0" || n == "null") { ShowToast("🔊 Nie znalazłem tekstu do przeczytania na tej stronie.", null); return; }
            _readTab = tab;
            ShowReadControls(true, false);
            if (_readTimer == null)
            {
                _readTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                _readTimer.Tick += async (s, e) => await PollReading();
            }
            _readTimer.Start();
        }

        async Task PollReading()
        {
            var tab = _readTab;
            // najpierw: czy karta jeszcze istnieje (zamknieta karta ma zamkniety silnik - nie wolno go dotykac)
            if (tab == null || !_tabs.Contains(tab)) { StopReading(); return; }
            try
            {
                if (tab.View.CoreWebView2 == null) { StopReading(); return; }
                var raw = await tab.View.CoreWebView2.ExecuteScriptAsync("window.__velivoRead ? window.__velivoRead.state() : null");
                if (raw == "null") { StopReading(); return; } // strona sie zmienila
                using (var d = JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw)))
                {
                    var r = d.RootElement;
                    if (!r.GetProperty("active").GetBoolean()) { StopReading(); return; }
                    ShowReadControls(true, r.GetProperty("paused").GetBoolean());
                    int i = r.GetProperty("i").GetInt32(), n = r.GetProperty("n").GetInt32();
                    ReadBtn.ToolTip = "Czytanie: zdanie " + Math.Min(i + 1, n) + " z " + n + "\nKliknij: pauza / wznów (Ctrl+Shift+U)";
                }
            }
            catch (Exception) { StopReading(); }
        }

        void ShowReadControls(bool reading, bool paused)
        {
            ReadStopBtn.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
            ReadRateBtn.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
            ReadRateBtn.Content = _settings.ReadRate.ToString("0.##", CultureInfo.GetCultureInfo("pl-PL")) + "×";
            ReadBtn.Content = !reading ? "" : (paused ? "" : ""); // glosnik / odtworz / pauza
            if (!reading) ReadBtn.ToolTip = "Czytaj stronę na głos (Ctrl+Shift+U)\nZaznacz tekst, aby przeczytać tylko fragment";
        }

        async void ReadBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_readTab == null) { StartReading(false); return; }
            try
            {
                var raw = await _readTab.View.CoreWebView2.ExecuteScriptAsync("window.__velivoRead.state()");
                bool paused = raw.Contains("\\\"paused\\\":true");
                await _readTab.View.CoreWebView2.ExecuteScriptAsync(paused ? "window.__velivoRead.resume()" : "window.__velivoRead.pause()");
                ShowReadControls(true, !paused);
            }
            catch (Exception) { StopReading(); }
        }

        void ReadStopBtn_Click(object sender, RoutedEventArgs e) { StopReading(); }

        void ReadRateBtn_Click(object sender, RoutedEventArgs e)
        {
            int i = Array.FindIndex(ReadRates, r => Math.Abs(r - _settings.ReadRate) < 0.01);
            _settings.ReadRate = ReadRates[(i + 1) % ReadRates.Length];
            try { _settings.Save(DataDir); } catch (Exception) { }
            ShowReadControls(true, false);
            if (_readTab != null && _readTab.View.CoreWebView2 != null)
                _ = _readTab.View.CoreWebView2.ExecuteScriptAsync("window.__velivoRead && window.__velivoRead.rate(" + Num(_settings.ReadRate) + ")");
        }

        void StopReading()
        {
            if (_readTimer != null) _readTimer.Stop();
            var tab = _readTab; _readTab = null;
            if (tab != null && _tabs.Contains(tab))
                try { var c = tab.View.CoreWebView2; if (c != null) _ = c.ExecuteScriptAsync("window.__velivoRead && window.__velivoRead.stop()"); } catch (Exception) { }
            ShowReadControls(false, false);
        }
    }
}
