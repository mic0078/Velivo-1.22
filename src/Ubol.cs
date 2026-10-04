using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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

        // Tarcza: zadania zablokowane przez uBOL silnik zglasza jako net::ERR_BLOCKED_BY_CLIENT (blokada Velivo daje 403, wiec bez dubli)
        async Task HookUbolShield(BrowserTab tab, CoreWebView2 core)
        {
            if (tab == null || core == null) return;
            try
            {
                var urls = new Dictionary<string, string>();
                core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent").DevToolsProtocolEventReceived += (s, e) =>
                {
                    try
                    {
                        using (var d = JsonDocument.Parse(e.ParameterObjectAsJson))
                        {
                            var id = d.RootElement.GetProperty("requestId").GetString();
                            var url = d.RootElement.GetProperty("request").GetProperty("url").GetString();
                            if (urls.Count > 3000) urls.Clear();
                            urls[id] = url;
                        }
                    }
                    catch (Exception) { }
                };
                core.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += (s, e) =>
                {
                    try
                    {
                        using (var d = JsonDocument.Parse(e.ParameterObjectAsJson))
                        {
                            var r = d.RootElement;
                            var err = r.TryGetProperty("errorText", out var et) ? et.GetString() ?? "" : "";
                            if (err.IndexOf("ERR_BLOCKED_BY_CLIENT", StringComparison.OrdinalIgnoreCase) < 0) return;
                            var id = r.GetProperty("requestId").GetString();
                            string url;
                            if (!urls.TryGetValue(id, out url)) url = "";
                            urls.Remove(id);
                            NoteBlocked(tab, "uBlock Origin Lite", url);
                        }
                    }
                    catch (Exception) { }
                };
                core.NavigationStarting += (s, e) => urls.Clear();
                await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        async Task EnsureBundledUbolAsync()
        {
            if (Core == null || !File.Exists(Path.Combine(BundledUbolDir, "manifest.json"))) return;
            try
            {
                var exts = await Core.Profile.GetBrowserExtensionsAsync();
                var installed = exts.FirstOrDefault(IsUbol);
                const bool want = true;   // wbudowany na stale
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
