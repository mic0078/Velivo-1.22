using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Tekst z otwartego PDF (do trybu czytania i czytania na glos). Wbudowany czytnik PDF silnika nie daje dostepu do tekstu,
    // wiec Velivo czyta plik sam - lokalnie, biblioteka pdf.js (Mozilla) z katalogu programu, w ukrytym silniku bez dostepu
    // do sieci i dysku (wirtualny adres wskazuje tylko katalog roboczy). PDF z sieci pobiera z ciasteczkami karty.
    public partial class MainWindow
    {
        const long PdfMaxBytes = 100L * 1024 * 1024;
        static readonly HttpClient PdfHttp = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(90) };

        // adres karty to PDF (plik z dysku albo z sieci; bez parametrow i kotwic)
        internal static bool IsPdfUrl(string url)
        {
            Uri u;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out u) && (u.IsFile || u.Scheme == "http" || u.Scheme == "https")
                && u.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        }

        static string PdfWorkDir { get { return Path.Combine(DataDir, "pdf-czytnik"); } }

        // strona-ekstraktor: pdf.js czyta dokument.pdf strona po stronie i sklada akapity (po odstepach w pionie i koncach wierszy),
        // laczy slowa przeniesione myslnikiem; wynik {title, text} wysyla do Velivo
        const string PdfExtractHtml = @"<!doctype html><html><head><meta charset=utf-8><title>PDF</title></head><body><script type=module>
const send = m => { try { chrome.webview.postMessage(JSON.stringify(m)); } catch (e) {} };
try {
  const pdfjs = await import('./pdf.min.mjs');
  pdfjs.GlobalWorkerOptions.workerSrc = './pdf.worker.min.mjs';
  const doc = await pdfjs.getDocument({ url: './dokument.pdf', isEvalSupported: false, disableAutoFetch: false }).promise;
  let title = '';
  try { const md = await doc.getMetadata(); title = (md.info && md.info.Title || '').trim(); } catch (e) {}
  const paras = [];
  for (let p = 1; p <= doc.numPages; p++) {
    const page = await doc.getPage(p);
    const tc = await page.getTextContent();
    let para = '', line = '', lastY = null, lastH = 0, eol = false;
    const endLine = (gap) => {
      const t = line.trim(); line = '';
      if (!t) return;
      if (/[A-Za-zÀ-ž]-$/.test(para)) para = para.slice(0, -1) + t;   // slowo przeniesione myslnikiem
      else para = para ? para + ' ' + t : t;
      // gap = odstep od tego wiersza do nastepnego; duzy = koniec akapitu
      if (gap !== null && lastH && gap > lastH * 1.6) { paras.push(para); para = ''; }
    };
    for (const it of tc.items) {
      if (!('str' in it)) continue;
      const y = it.transform[5], h = Math.abs(it.transform[3]) || Math.hypot(it.transform[2], it.transform[3]) || lastH;
      if (eol || (lastY !== null && Math.abs(y - lastY) > h * 0.5)) { endLine(lastY !== null ? Math.abs(y - lastY) : null); eol = false; }
      lastY = y; if (h) lastH = h;
      line += it.str;
      if (it.hasEOL) eol = true;
    }
    endLine(null);
    if (para) paras.push(para);
    page.cleanup();
  }
  const text = paras.map(s => s.replace(/\s+/g, ' ').trim()).filter(s => s.length > 1).join('\n\n');
  send({ title, text, pages: doc.numPages });
} catch (e) { send({ error: String(e && e.message || e) }); }
</script></body></html>";

        // Tekst PDF z karty: null = nie udalo sie (powod w wyjatku), "" = PDF bez tekstu (np. skan).
        async Task<(string Title, string Text)> ExtractPdfTextAsync(string url, CoreWebView2 tabCore)
        {
            var u = new Uri(url);
            byte[] bytes;
            if (u.IsFile)
            {
                var fi = new FileInfo(u.LocalPath);
                if (fi.Length > PdfMaxBytes) throw new InvalidOperationException(L.T("Plik jest za duży (ponad 100 MB)."));
                bytes = await File.ReadAllBytesAsync(fi.FullName);
            }
            else
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, u))
                {
                    try
                    {
                        var cookies = await tabCore.CookieManager.GetCookiesAsync(url);
                        var header = string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value));
                        if (header.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", header);
                    }
                    catch (Exception) { }
                    req.Headers.TryAddWithoutValidation("User-Agent", tabCore.Settings.UserAgent);
                    using (var resp = await PdfHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                    {
                        resp.EnsureSuccessStatusCode();
                        if ((resp.Content.Headers.ContentLength ?? 0) > PdfMaxBytes) throw new InvalidOperationException(L.T("Plik jest za duży (ponad 100 MB)."));
                        bytes = await resp.Content.ReadAsByteArrayAsync();
                        if (bytes.LongLength > PdfMaxBytes) throw new InvalidOperationException(L.T("Plik jest za duży (ponad 100 MB)."));
                    }
                }
            }

            Directory.CreateDirectory(PdfWorkDir);
            foreach (var name in new[] { "pdf.min.mjs", "pdf.worker.min.mjs" })
            {
                var src = Path.Combine(AppContext.BaseDirectory, "pdfjs", name);
                var dst = Path.Combine(PdfWorkDir, name);
                if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length) File.Copy(src, dst, true);
            }
            var page = Path.Combine(PdfWorkDir, "czytaj.html");
            if (!File.Exists(page) || File.ReadAllText(page) != PdfExtractHtml) File.WriteAllText(page, PdfExtractHtml);
            await File.WriteAllBytesAsync(Path.Combine(PdfWorkDir, "dokument.pdf"), bytes);

            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var ctl = await _env.CreateCoreWebView2ControllerAsync(hwnd);
            try
            {
                ctl.IsVisible = false;
                var core = ctl.CoreWebView2;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.Settings.IsWebMessageEnabled = true;
                core.SetVirtualHostNameToFolderMapping("velivo-pdf", PdfWorkDir, CoreWebView2HostResourceAccessKind.Deny);
                core.WebMessageReceived += (s, e) => { try { done.TrySetResult(e.TryGetWebMessageAsString()); } catch (Exception) { } };
                // ukryty silnik ma czytac tylko katalog roboczy - zadnych innych adresow
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (s, e) =>
                {
                    if (!e.Request.Uri.StartsWith("https://velivo-pdf/", StringComparison.OrdinalIgnoreCase))
                        e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                };
                core.Navigate("https://velivo-pdf/czytaj.html");
                var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(120)));
                if (finished != done.Task) throw new TimeoutException(L.T("Odczyt PDF trwał za długo."));
                using (var doc = JsonDocument.Parse(done.Task.Result))
                {
                    var root = doc.RootElement;
                    JsonElement err;
                    if (root.TryGetProperty("error", out err)) throw new InvalidOperationException(err.GetString());
                    var title = root.TryGetProperty("title", out var t) ? t.GetString() : "";
                    if (string.IsNullOrWhiteSpace(title)) title = Uri.UnescapeDataString(u.Segments.Last());
                    return (title, root.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "");
                }
            }
            finally
            {
                try { ctl.Close(); } catch (Exception) { }
                try { File.Delete(Path.Combine(PdfWorkDir, "dokument.pdf")); } catch (Exception) { }   // tresc PDF nie zostaje na dysku
            }
        }
    }
}
