using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Wbudowany uBlock Origin Lite (GPL-3.0, https://github.com/uBlockOrigin/uBOL-home) - instalator zawiera najnowsze
    // wydanie, Velivo wlacza je samo. Jesli uBOL jest juz zainstalowany ze sklepu, zostaje ten (bez dubli).
    public partial class MainWindow
    {
        static string BundledUbolDir { get { return Path.Combine(AppContext.BaseDirectory, "Dodatki", "uBlock Origin Lite"); } }

        static bool IsUbol(CoreWebView2BrowserExtension e)
        {
            return e != null && (e.Id == "ddkjiahejlhfcafbddmgiahcphecmpfh" || (e.Name ?? "").IndexOf("uBlock Origin Lite", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        async Task EnsureBundledUbolAsync()
        {
            if (Core == null || !File.Exists(Path.Combine(BundledUbolDir, "manifest.json"))) return;
            try
            {
                var exts = await Core.Profile.GetBrowserExtensionsAsync();
                var installed = exts.FirstOrDefault(IsUbol);
                bool want = _settings == null || _settings.UbolLite;
                if (installed != null)
                {
                    if (installed.IsEnabled != want) await installed.EnableAsync(want);
                    return;
                }
                if (!want) return;
                var added = await Core.Profile.AddBrowserExtensionAsync(BundledUbolDir);
                if (!added.IsEnabled) await added.EnableAsync(true);
                SaveExtPath(added.Id, BundledUbolDir);
            }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
