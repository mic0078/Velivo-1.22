using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace Przegladarka
{
    // Tryb czytania + lokalne streszczenie (bez wysyłania treści do chmury).
    public partial class MainWindow
    {
        const string ReaderExtractScript = @"(() => {
  const mainRoot = () => {
    const cand = [...document.querySelectorAll('article, main, [role=main], #content, .content, .article, .post')];
    let best = null, bestLen = 0;
    const score = el => [...el.querySelectorAll('p')].reduce((n, p) => n + p.innerText.length, 0);
    for (const c of cand) { const s = score(c); if (s > bestLen) { best = c; bestLen = s; } }
    if (!best || bestLen < 500) {
      for (const d of document.querySelectorAll('div, section')) { const s = score(d); if (s > bestLen) { best = d; bestLen = s; } }
    }
    return best || document.body;
  };
  // smieci stron: menu, reklamy, 'czytaj tez', polecane, komentarze, newslettery, podpisy zdjec, paski udostepniania
  const JUNK_SEL = 'nav, footer, aside, header nav, form, button, script, style, noscript, figure figcaption, [aria-hidden=true], [hidden], ' +
    '.ad, .ads, .advert, .share, .social, .related, .comments, [class*=related i], [class*=recommend i], [class*=polecam i], [class*=promo i], ' +
    '[class*=newsletter i], [class*=cookie i], [class*=consent i], [class*=comment i], [id*=comment i], [class*=sponsor i], [class*=advert i], ' +
    '[class*=reklam i], [class*=breadcrumb i], [class*=share i], [class*=social i], [class*=paywall i], [class*=subscribe i], [class*=author-box i], ' +
    '[class*=tags i], [class*=see-also i], [class*=read-more i], [class*=readmore i], [id*=taboola i], [class*=taboola i], [class*=outbrain i]';
  const JUNK_TXT = /^(czytaj (też|także|również|więcej|dalej)|zobacz (też|także|również|wideo|więcej)|polecamy|polecane|reklama|artykuł sponsorowany|materiał (sponsorowany|partnera)|advertisement|sponsored|tagi:|tags:|źródło:|fot\.|foto:|zdjęcie:|autor zdjęcia|udostępnij|share|subskrybuj|zapisz się|newsletter|komentarze|dodaj komentarz|dołącz do|pobierz aplikację|obserwuj nas|kup teraz|więcej na ten temat|read more|related|see also|follow us)/i;
  const junk = (el, root) => {
    for (let e = el; e && e !== root; e = e.parentElement) if (e.matches && e.matches(JUNK_SEL)) return true;
    const t = (el.innerText || '').trim();
    if (JUNK_TXT.test(t)) return true;
    // blok zlozony glownie z linkow = nawigacja albo lista 'polecane'
    let links = 0; for (const a of el.querySelectorAll('a')) links += (a.innerText || '').length;
    if (t.length > 0 && t.length < 300 && links / t.length > 0.6) return true;
    if (t.length < 60 && t === t.toUpperCase() && /[A-ZĄĆĘŁŃÓŚŹŻ]{4}/.test(t)) return true;   // krzyczace etykiety (ZOBACZ, REKLAMA)
    return false;
  };
  const visible = el => { const r = el.getBoundingClientRect(); const cs = getComputedStyle(el); return r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none'; };
  const root = mainRoot();
  const seen = new Set();
  const blocks = [...root.querySelectorAll('h1,h2,h3,p,li,blockquote')]
    .filter(x => !x.querySelector('p,li,h1,h2,h3,blockquote') && visible(x) && !junk(x, root))
    .map(x => x.innerText.replace(/\s+/g, ' ').trim())
    .filter(x => x.length > 40 && !seen.has(x) && seen.add(x));
  const text = blocks.join('\n\n');
  return JSON.stringify({ title: document.title || '', url: location.href, text });
})();";

        void ReaderMode_Click(object sender, RoutedEventArgs e)
        {
            OpenReaderMode();
        }

        async void OpenReaderMode()
        {
            if (Core == null) return;
            try
            {
                var raw = await Core.ExecuteScriptAsync(ReaderExtractScript);
                var json = JsonSerializer.Deserialize<string>(raw);
                if (string.IsNullOrWhiteSpace(json)) return;

                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    var title = root.TryGetProperty("title", out var t) ? t.GetString() : L.T("Tryb czytania");
                    var url = root.TryGetProperty("url", out var u) ? u.GetString() : "";
                    var text = root.TryGetProperty("text", out var x) ? x.GetString() : "";
                    if (string.IsNullOrWhiteSpace(text) || text.Length < 120)
                    {
                        ShowToast(L.T("📰 Za mało treści do trybu czytania na tej stronie."), null);
                        return;
                    }

                    var summary = LocalSummary(text, 5, title);

                    var win = new Window
                    {
                        Title = L.T("Tryb czytania – ") + (title ?? ""),
                        Width = 900,
                        Height = 700,
                        Owner = this,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner
                    };
                    // ostatni rozmiar okna ustawiony przez uzytkownika
                    try
                    {
                        var sz = (_settings.ReaderSize ?? "").Split(';');
                        double rw, rh;
                        if (sz.Length == 2 && double.TryParse(sz[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rw) && double.TryParse(sz[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rh))
                        {
                            win.Width = Math.Max(420, Math.Min(SystemParameters.VirtualScreenWidth, rw));
                            win.Height = Math.Max(320, Math.Min(SystemParameters.VirtualScreenHeight, rh));
                        }
                    }
                    catch (Exception) { }
                    win.Closing += (a0, b0) =>
                    {
                        if (win.WindowState != WindowState.Normal) return;
                        _settings.ReaderSize = win.ActualWidth.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + ";" + win.ActualHeight.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                        try { _settings.Save(DataDir); } catch (Exception) { }
                    };

                    var view = new WebView2();
                    var readSummaryBtn = SmallButton(L.T("Czytaj podsumowanie"), null);
                    var readAllBtn = SmallButton(L.T("Czytaj całość"), null);
                    var stopBtn = SmallButton(L.T("Zatrzymaj"), null);
                    var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
                    // suwak glosnosci czytania (zapamietywany w ustawieniach)
                    var volLabel = new TextBlock { Text = "🔊", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), FontSize = 14 };
                    var volume = new Slider { Minimum = 0, Maximum = 100, Value = Math.Round(_settings.ReadVolume * 100), Width = 140, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), IsMoveToPointEnabled = true, ToolTip = L.T("Głośność czytania") };
                    var volValue = new TextBlock { Text = (int)volume.Value + "%", Width = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                    var volTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    volTimer.Tick += async (s4, e4) =>
                    {
                        volTimer.Stop();
                        _settings.ReadVolume = volume.Value / 100.0;
                        SetPiperVolume(_settings.ReadVolume);
                        try { _settings.Save(DataDir); } catch (Exception) { }
                        if (view.CoreWebView2 != null) await view.CoreWebView2.ExecuteScriptAsync("window.__velivoRead && window.__velivoRead.volume(" + Num(_settings.ReadVolume) + ")");
                    };
                    volume.ValueChanged += (s4, e4) => { volValue.Text = (int)volume.Value + "%"; volTimer.Stop(); volTimer.Start(); };
                    // tryb czytnika: jasny / ciemny / nocny + natezenie trybu nocnego (jak w samym Velivo)
                    var modeBox = new ComboBox { Width = 120, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = L.T("Wygląd czytnika") };
                    modeBox.Items.Add(new ComboBoxItem { Content = L.T("☀ Jasny"), Tag = "light" });
                    modeBox.Items.Add(new ComboBoxItem { Content = L.T("🌙 Ciemny"), Tag = "dark" });
                    modeBox.Items.Add(new ComboBoxItem { Content = L.T("🌅 Nocny"), Tag = "night" });
                    modeBox.SelectedItem = modeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.ReaderTheme) ?? modeBox.Items[0];
                    var nightLabel = new TextBlock { Text = "🌅", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), FontSize = 14, ToolTip = L.T("Natężenie trybu nocnego") };
                    var night = new Slider { Minimum = 5, Maximum = 100, Value = _settings.ReaderNight, Width = 110, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), IsMoveToPointEnabled = true, ToolTip = L.T("Natężenie trybu nocnego") };
                    var nightValue = new TextBlock { Text = _settings.ReaderNight + "%", Width = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                    Action applyLook = () =>
                    {
                        var mode = (string)((ComboBoxItem)modeBox.SelectedItem).Tag;
                        bool isNight = mode == "night";
                        nightLabel.Visibility = night.Visibility = nightValue.Visibility = isNight ? Visibility.Visible : Visibility.Collapsed;
                        if (view.CoreWebView2 != null) _ = view.CoreWebView2.ExecuteScriptAsync("window.__velivoLook && window.__velivoLook('" + mode + "'," + (int)night.Value + ")");
                    };
                    var lookTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    lookTimer.Tick += (s5, e5) => { lookTimer.Stop(); try { _settings.Save(DataDir); } catch (Exception) { } };
                    modeBox.SelectionChanged += (s5, e5) => { _settings.ReaderTheme = (string)((ComboBoxItem)modeBox.SelectedItem).Tag; applyLook(); lookTimer.Stop(); lookTimer.Start(); };
                    night.ValueChanged += (s5, e5) => { nightValue.Text = (int)night.Value + "%"; _settings.ReaderNight = (int)night.Value; applyLook(); lookTimer.Stop(); lookTimer.Start(); };
                    bar.Children.Add(modeBox);
                    bar.Children.Add(nightLabel);
                    bar.Children.Add(night);
                    bar.Children.Add(nightValue);
                    bar.Children.Add(volLabel);
                    bar.Children.Add(volume);
                    bar.Children.Add(volValue);
                    bar.Children.Add(readSummaryBtn);
                    bar.Children.Add(readAllBtn);
                    bar.Children.Add(stopBtn);
                    win.Content = Docked(bar, view);

                    win.Closed += (a, b) =>
                    {
                        try { if (view.CoreWebView2 != null) _ = view.CoreWebView2.ExecuteScriptAsync("window.__velivoRead && window.__velivoRead.stop()"); } catch (Exception) { }
                        view.Dispose();
                    };

                    win.Loaded += async (a, b) =>
                    {
                        try
                        {
                            await view.EnsureCoreWebView2Async(_env);
                            string html = BuildReaderHtml(title ?? L.T("Tryb czytania"), url ?? "", summary, text);
                            view.NavigateToString(html);
                            view.CoreWebView2.NavigationCompleted += async (s2, e2) =>
                            {
                                if (!e2.IsSuccess) return;
                                await view.CoreWebView2.ExecuteScriptAsync(ReaderScript);
                                applyLook();
                                await view.CoreWebView2.ExecuteScriptAsync("try{ window.__velivoRead.volume(" + Num(_settings.ReadVolume) + "); }catch(e){}");
                                await PreparePiperReading(view.CoreWebView2);
                                // klikniecie w tekst = czytaj od tego miejsca (przeciaganie/zaznaczanie i linki dzialaja jak zwykle)
                                await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
  if (window.__velivoClickRead) return; window.__velivoClickRead = true;
  const st = document.createElement('style'); st.textContent = 'p,li,blockquote,h1,h2,h3,h4{cursor:pointer}'; document.head.appendChild(st);
  document.addEventListener('click', e => {
    if (e.button !== 0 || (getSelection() && getSelection().toString().trim())) return;
    if (e.target.closest('a,button,input,textarea,select')) return;
    window.__velivoRead && window.__velivoRead.startAt(e.clientX, e.clientY, " + Num(_settings.ReadRate) + ", " + JsonSerializer.Serialize(_settings.ReadVoice ?? "") + @");
  });
})();");
                                await ReadSummaryInReader(view);
                            };
                            readSummaryBtn.Click += async (s3, e3) => await ReadSummaryInReader(view);
                            readAllBtn.Click += async (s3, e3) =>
                            {
                                await view.CoreWebView2.ExecuteScriptAsync("window.getSelection().removeAllRanges();window.__velivoRead && window.__velivoRead.start(false," + Num(_settings.ReadRate) + "," + JsonSerializer.Serialize(_settings.ReadVoice ?? "") + ")");
                            };
                            stopBtn.Click += async (s3, e3) => await view.CoreWebView2.ExecuteScriptAsync("window.__velivoRead && window.__velivoRead.stop()");
                        }
                        catch (Exception ex)
                        {
                            App.LogError(ex);
                            MessageBox.Show(win, L.T("Nie udało się uruchomić czytnika:\n") + ex.Message, L.T("Tryb czytania"));
                            win.Close();
                        }
                    };
                    win.Show();
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show(this, L.T("Nie udało się uruchomić trybu czytania:\n") + ex.Message, L.T("Tryb czytania"));
            }
        }

        async System.Threading.Tasks.Task ReadSummaryInReader(WebView2 view)
        {
            if (view == null || view.CoreWebView2 == null) return;
            string js = "(function(){var el=document.getElementById('velivo-summary');if(!el||!window.__velivoRead)return 0;var r=document.createRange();r.selectNodeContents(el);var s=window.getSelection();s.removeAllRanges();s.addRange(r);return window.__velivoRead.start(true," + Num(_settings.ReadRate) + "," + JsonSerializer.Serialize(_settings.ReadVoice ?? "") + ");})()";
            await view.CoreWebView2.ExecuteScriptAsync(js);
        }

        static string BuildReaderHtml(string title, string url, string summary, string text)
        {
            string T(string s) { return WebUtility.HtmlEncode(s ?? ""); }
            var body = T(text).Replace("\n\n", "</p><p>").Replace("\n", "<br/>");
            var sum = T(summary).Replace("\n", "<br/>");
            return "<!doctype html><html lang='pl'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'/>" +
                   "<title>Tryb czytania</title><style>body{font-family:Georgia,serif;line-height:1.72;max-width:900px;margin:0 auto;padding:24px;background:#f8fafc;color:#111827}h1{font-size:34px;margin:0 0 8px}small{color:#6b7280}#velivo-summary{background:#ecfeff;border:1px solid #bae6fd;border-left:5px solid #0891b2;padding:14px;border-radius:10px;margin:14px 0 20px;font-size:18px}article{background:#fff;border:1px solid #e5e7eb;border-radius:12px;padding:18px}p{margin:0 0 14px;font-size:21px}" +
                   "body,article,#velivo-summary{transition:background-color .25s,color .25s,border-color .25s}" +
                   "body.dark{background:#111827;color:#e5e7eb}body.dark small{color:#9ca3af}body.dark article{background:#1f2937;border-color:#374151}body.dark #velivo-summary{background:#0f2a33;border-color:#155e75;color:#e5e7eb}body.dark a{color:#93c5fd}" +
                   "#velivo-night{position:fixed;inset:0;pointer-events:none;background:#ff8a00;mix-blend-mode:multiply;opacity:0;transition:opacity .25s;z-index:2147483647}</style>" +
                   "<script>window.__velivoLook=function(m,s){var b=document.body;if(!b)return;b.classList.toggle('dark',m==='dark');var n=document.getElementById('velivo-night');if(!n){n=document.createElement('div');n.id='velivo-night';document.documentElement.appendChild(n);}n.style.opacity=m==='night'?(Math.max(5,Math.min(100,s))/100*0.45).toFixed(3):'0';};</script></head><body>" +
                   "<h1>" + T(title) + "</h1><small>" + T(url) + "</small>" +
                   "<div id='velivo-summary'><strong>" + T(L.T("Najważniejsze zdania (streszczenie lokalne, bez AI):")) + "</strong><br/>" + sum + "</div>" +
                   "<article><p>" + body + "</p></article></body></html>";
        }

        // Streszczenie LOKALNE (bez chmury i bez AI): wybiera najwazniejsze zdania artykulu.
        // Waga zdania: czeste slowa kluczowe tekstu + slowa z tytulu + premia za poczatek artykulu (tam zwykle jest sedno).
        static string LocalSummary(string text, int maxSentences, string title = null)
        {
            var sentences = Regex.Split(text, @"(?<=[\.!\?…])\s+|\n+")
                .Select(s => s.Trim())
                .Where(s => s.Length > 50 && s.Length < 450 && !s.EndsWith("?"))
                .Take(200)
                .ToList();
            if (sentences.Count == 0) return L.T("Brak danych do streszczenia.");

            var stop = new HashSet<string>(("i oraz a w z na do o że to ten ta te tego tej tym się jest są był była było były być jak dla po od przez nie tak lub ale " +
                "czy też także jeszcze już tylko może można który która które którzy których jego jej ich nim nią tu tam gdy kiedy gdzie co kto " +
                "jako przy pod nad bez przed między albo więc jednak bardzo który mają ma mieć będzie będą został została zostały około roku lat " +
                "the and of to in is are was were for on with that this it as by at from be or an have has not but they their which will can").Split(' '),
                StringComparer.OrdinalIgnoreCase);
            Func<string, IEnumerable<string>> words = s => Regex.Matches(s.ToLowerInvariant(), "[a-ząćęłńóśźż0-9]{4,}").Cast<Match>()
                .Select(m => m.Value.Length > 6 ? m.Value.Substring(0, 6) : m.Value)   // prosty rdzen: "rządu", "rządzie" -> "rządu"/"rządz"
                .Where(w => !stop.Contains(w));

            var freq = new Dictionary<string, int>();
            foreach (var s in sentences) foreach (var w in words(s).Distinct()) { int n; freq[w] = freq.TryGetValue(w, out n) ? n + 1 : 1; }
            var titleWords = new HashSet<string>(words(title ?? ""));

            var scored = new List<(int Index, string Text, double Score)>();
            for (int i = 0; i < sentences.Count; i++)
            {
                var ws = words(sentences[i]).ToList();
                if (ws.Count == 0) continue;
                double sc = ws.Distinct().Sum(w => { int n; return freq.TryGetValue(w, out n) && n > 1 ? Math.Log(1 + n) : 0; });
                sc += ws.Distinct().Count(w => titleWords.Contains(w)) * 2.0;
                sc /= Math.Sqrt(ws.Count);
                if (i < 3) sc *= 1.35;   // lead artykulu
                scored.Add((i, sentences[i], sc));
            }

            var best = new List<(int Index, string Text, double Score)>();
            foreach (var c in scored.OrderByDescending(x => x.Score))
            {
                if (best.Count >= maxSentences) break;
                // bez powtorzen: pomijamy zdanie bardzo podobne do juz wybranego
                var cw = new HashSet<string>(words(c.Text));
                if (best.Any(b => { var bw = new HashSet<string>(words(b.Text)); return cw.Count > 0 && cw.Count(w => bw.Contains(w)) > cw.Count * 0.6; })) continue;
                best.Add(c);
            }
            return string.Join("\n", best.OrderBy(x => x.Index).Select(x => "• " + x.Text));
        }
    }
}
