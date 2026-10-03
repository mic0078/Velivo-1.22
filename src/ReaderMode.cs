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
  const root = mainRoot();
  const blocks = [...root.querySelectorAll('h1,h2,h3,p,li,blockquote')]
    .map(x => x.innerText.replace(/\s+/g, ' ').trim())
    .filter(x => x.length > 40);
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
                    var title = root.TryGetProperty("title", out var t) ? t.GetString() : "Tryb czytania";
                    var url = root.TryGetProperty("url", out var u) ? u.GetString() : "";
                    var text = root.TryGetProperty("text", out var x) ? x.GetString() : "";
                    if (string.IsNullOrWhiteSpace(text) || text.Length < 120)
                    {
                        ShowToast("📰 Za mało treści do trybu czytania na tej stronie.", null);
                        return;
                    }

                    var summary = LocalSummary(text, 5);

                    var win = new Window
                    {
                        Title = "Tryb czytania – " + (title ?? ""),
                        Width = 900,
                        Height = 700,
                        Owner = this,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner
                    };

                    var view = new WebView2();
                    var readSummaryBtn = SmallButton("Czytaj podsumowanie", null);
                    var readAllBtn = SmallButton("Czytaj całość", null);
                    var stopBtn = SmallButton("Zatrzymaj", null);
                    var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
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
                            string html = BuildReaderHtml(title ?? "Tryb czytania", url ?? "", summary, text);
                            view.NavigateToString(html);
                            view.CoreWebView2.NavigationCompleted += async (s2, e2) =>
                            {
                                if (!e2.IsSuccess) return;
                                await view.CoreWebView2.ExecuteScriptAsync(ReaderScript);
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
                            MessageBox.Show(win, "Nie udało się uruchomić czytnika:\n" + ex.Message, "Tryb czytania");
                            win.Close();
                        }
                    };
                    win.Show();
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show(this, "Nie udało się uruchomić trybu czytania:\n" + ex.Message, "Tryb czytania");
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
                   "<title>Tryb czytania</title><style>body{font-family:Georgia,serif;line-height:1.72;max-width:900px;margin:0 auto;padding:24px;background:#f8fafc;color:#111827}h1{font-size:34px;margin:0 0 8px}small{color:#6b7280}#velivo-summary{background:#ecfeff;border:1px solid #bae6fd;border-left:5px solid #0891b2;padding:14px;border-radius:10px;margin:14px 0 20px;font-size:18px}article{background:#fff;border:1px solid #e5e7eb;border-radius:12px;padding:18px}p{margin:0 0 14px;font-size:21px}</style></head><body>" +
                   "<h1>" + T(title) + "</h1><small>" + T(url) + "</small>" +
                   "<div id='velivo-summary'><strong>Podsumowanie:</strong><br/>" + sum + "</div>" +
                   "<article><p>" + body + "</p></article></body></html>";
        }

        static string LocalSummary(string text, int maxSentences)
        {
            var sentences = Regex.Split(text, @"(?<=[\.!\?…])\s+")
                .Select(s => s.Trim())
                .Where(s => s.Length > 40)
                .Take(120)
                .ToList();
            if (sentences.Count == 0) return "Brak danych do streszczenia.";

            var stop = new HashSet<string>(new[]
            {
                "i","oraz","a","w","z","na","do","o","że","to","ten","ta","to","się","jest","są","jak","dla","po","od","przez","nie","tak","lub","ale"
            }, StringComparer.OrdinalIgnoreCase);

            var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sentences)
            {
                foreach (Match m in Regex.Matches(s.ToLowerInvariant(), "[a-ząćęłńóśźż0-9]{3,}"))
                {
                    var w = m.Value;
                    if (stop.Contains(w)) continue;
                    int n;
                    freq[w] = freq.TryGetValue(w, out n) ? n + 1 : 1;
                }
            }

            var scored = new List<(int Index, string Text, double Score)>();
            for (int i = 0; i < sentences.Count; i++)
            {
                double sc = 0;
                foreach (Match m in Regex.Matches(sentences[i].ToLowerInvariant(), "[a-ząćęłńóśźż0-9]{3,}"))
                {
                    int n;
                    if (freq.TryGetValue(m.Value, out n)) sc += n;
                }
                scored.Add((i, sentences[i], sc / Math.Max(1, sentences[i].Length / 80.0)));
            }

            var best = scored.OrderByDescending(x => x.Score).Take(Math.Min(maxSentences, scored.Count)).OrderBy(x => x.Index).ToList();
            return string.Join("\n", best.Select(x => "• " + x.Text));
        }
    }
}
