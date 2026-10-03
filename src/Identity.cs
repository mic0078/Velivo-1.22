using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Velivo przedstawia sie stronom jak zwykly Microsoft Edge (ten sam silnik).
    // WebView2 dopisuje do Client Hints marke "Microsoft Edge WebView2", po ktorej m.in. Google
    // rozpoznaje przegladarki wbudowane w aplikacje i potrafi zablokowac w nich logowanie.
    public partial class MainWindow
    {
        static readonly Regex WebViewBrand = new Regex(@",?\s*""Microsoft Edge WebView2"";v=""[^""]*""", RegexOptions.Compiled);
        static readonly string[] BrandHeaders = { "Sec-CH-UA", "Sec-CH-UA-Full-Version-List" };

        static void StripWebViewBrand(CoreWebView2HttpRequestHeaders headers)
        {
            foreach (var name in BrandHeaders)
            {
                if (!headers.Contains(name)) continue;
                var value = headers.GetHeader(name);
                var clean = WebViewBrand.Replace(value, "").TrimStart(',', ' ');
                if (clean != value) headers.SetHeader(name, clean);
            }
        }

        // To samo dla skryptow strony: navigator.userAgentData.brands i getHighEntropyValues().
        // Strony dodatkow (chrome-extension://, np. Szybki Dostep) zostawiamy w spokoju - one musza wiedziec,
        // ze dzialaja w Velivo, i potrzebuja kanalu chrome.webview do programu.
        const string HideWebViewBrandScript = @"(() => {
  if (location.protocol === 'chrome-extension:') return;
  try { if (window.chrome && 'webview' in window.chrome) delete window.chrome.webview; } catch (e) {}
  try {
    const P = window.NavigatorUAData && NavigatorUAData.prototype;
    if (!P) return;
    const strip = a => Array.isArray(a) ? a.filter(b => !/WebView2/i.test(b.brand)) : a;
    const d = Object.getOwnPropertyDescriptor(P, 'brands');
    if (d && d.get) Object.defineProperty(P, 'brands', { get() { return strip(d.get.call(this)); }, configurable: true, enumerable: d.enumerable });
    const h = P.getHighEntropyValues;
    if (h) P.getHighEntropyValues = function (hints) {
      return h.call(this, hints).then(v => { if (v.brands) v.brands = strip(v.brands); if (v.fullVersionList) v.fullVersionList = strip(v.fullVersionList); return v; });
    };
    const j = P.toJSON;
    if (j) P.toJSON = function () { const v = j.call(this); if (v && v.brands) v.brands = strip(v.brands); return v; };
  } catch (e) {}
})();";
    }
}
