using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Wbudowany uBlock Origin Lite (GPL-3.0, https://github.com/uBlockOrigin/uBOL-home) - instalator zawiera najnowsze
    // wydanie, Velivo wlacza je samo. Jesli uBOL jest juz zainstalowany ze sklepu, zostaje ten (bez dubli).
    public partial class MainWindow
    {
        static string BundledUbolDir { get { return Path.Combine(AppContext.BaseDirectory, "Dodatki", "uBlock Origin Lite"); } }
        static string UpdatedUbolRoot { get { return Path.Combine(DataDir, "Dodatki", "uBOL"); } }
        static string UbolVersionFile { get { return Path.Combine(DataDir, "Dodatki", "ubol.version"); } }

        static Version ManifestVersion(string dir)
        {
            try
            {
                using (var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))))
                    return Version.TryParse(d.RootElement.GetProperty("version").GetString(), out var v) ? v : new Version(0, 0);
            }
            catch (Exception) { return new Version(0, 0); }
        }

        // najnowsza dostepna kopia: z instalatora albo pobrana przez automatyczna aktualizacje
        static string BestUbolDir()
        {
            var dirs = new List<string>();
            if (File.Exists(Path.Combine(BundledUbolDir, "manifest.json"))) dirs.Add(BundledUbolDir);
            try { if (Directory.Exists(UpdatedUbolRoot)) dirs.AddRange(Directory.GetDirectories(UpdatedUbolRoot).Where(d => File.Exists(Path.Combine(d, "manifest.json")))); } catch (Exception) { }
            return dirs.OrderByDescending(ManifestVersion).FirstOrDefault();
        }

        static readonly HttpClient UbolHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        // raz w tygodniu: nowe wydanie z oficjalnego GitHuba uBOL
        async Task UpdateUbolAsync()
        {
            try
            {
                if (_settings != null && !_settings.UbolLite) return;
                var stamp = Path.Combine(DataDir, "Dodatki", "ubol.checked");
                if (File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < TimeSpan.FromDays(7)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(stamp));
                File.WriteAllText(stamp, DateTime.UtcNow.ToString("o"));
                var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/uBlockOrigin/uBOL-home/releases/latest");
                req.Headers.UserAgent.ParseAdd("Velivo");
                string json;
                using (var resp = await UbolHttp.SendAsync(req)) { if (!resp.IsSuccessStatusCode) return; json = await resp.Content.ReadAsStringAsync(); }
                string zipUrl = null, tag;
                using (var d = JsonDocument.Parse(json))
                {
                    tag = d.RootElement.GetProperty("tag_name").GetString() ?? "";
                    foreach (var a in d.RootElement.GetProperty("assets").EnumerateArray())
                    {
                        var n = a.GetProperty("name").GetString() ?? "";
                        if (n.IndexOf("chromium", StringComparison.OrdinalIgnoreCase) >= 0 && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { zipUrl = a.GetProperty("browser_download_url").GetString(); break; }
                    }
                }
                if (zipUrl == null || !zipUrl.StartsWith("https://github.com/uBlockOrigin/", StringComparison.OrdinalIgnoreCase)) return;
                var cur = BestUbolDir();
                Version newV;
                if (!Version.TryParse(new string(tag.Where(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.'), out newV)) return;
                if (cur != null && ManifestVersion(cur) >= newV) return;
                var tmp = Path.Combine(Path.GetTempPath(), "velivo-ubol-" + Guid.NewGuid().ToString("N") + ".zip");
                using (var resp = await UbolHttp.GetAsync(zipUrl)) { if (!resp.IsSuccessStatusCode) return; using (var f = File.Create(tmp)) await resp.Content.CopyToAsync(f); }
                var unpack = Path.Combine(UpdatedUbolRoot, "tmp-" + Guid.NewGuid().ToString("N"));
                ZipFile.ExtractToDirectory(tmp, unpack);
                File.Delete(tmp);
                var manifest = Directory.GetFiles(unpack, "manifest.json", SearchOption.AllDirectories).FirstOrDefault();
                if (manifest == null) { Directory.Delete(unpack, true); return; }
                var target = Path.Combine(UpdatedUbolRoot, newV.ToString());
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(Path.GetDirectoryName(manifest), target);
                try { Directory.Delete(unpack, true); } catch (Exception) { }
                await EnsureBundledUbolAsync();
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        static bool IsUbol(CoreWebView2BrowserExtension e)
        {
            return e != null && (e.Id == "ddkjiahejlhfcafbddmgiahcphecmpfh" || (e.Name ?? "").IndexOf("uBlock Origin Lite", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Tarcza: zadania zablokowane przez uBOL silnik zglasza jako net::ERR_BLOCKED_BY_CLIENT (blokada Velivo daje 403, wiec bez dubli)
        async Task HookUbolShield(BrowserTab tab, CoreWebView2 core)
        {
            if (tab == null || core == null || (_settings != null && !_settings.UbolLite)) return;
            try
            {
                var urls = new Dictionary<string, string>();
                core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent").DevToolsProtocolEventReceived += (s, e) =>
                {
                    try
                    {
                        // szybko, bez parsowania calego zdarzenia (naglowki, stos wywolan) - strony z setkami zadan nie zwalniaja
                        var j = e.ParameterObjectAsJson;
                        var id = JsonField(j, "requestId");
                        var url = JsonField(j, "url");
                        if (id == null || url == null) return;
                        if (urls.Count > 3000) urls.Clear();
                        urls[id] = url.Length > 300 ? url.Substring(0, 300) : url;
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
                            // tylko prawdziwe blokady: zasob zewnetrznej firmy (nie strona, na ktorej jestes),
                            // nie plik samego dodatku i nie glowny dokument - silnik oznacza tak tez przerwane zadania
                            if (r.TryGetProperty("type", out var ty) && ty.GetString() == "Document") return;
                            if (!Uri.TryCreate(url, UriKind.Absolute, out var ru) || (ru.Scheme != Uri.UriSchemeHttp && ru.Scheme != Uri.UriSchemeHttps)) return;
                            Uri.TryCreate(core.Source ?? "", UriKind.Absolute, out var pu);
                            if (pu != null && string.Equals(ProtRegDomain(ru.Host), ProtRegDomain(pu.Host), StringComparison.OrdinalIgnoreCase)) return;
                            tab.UbolBlocked++;
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

        // panel uBOL (tryby filtrowania, listy, wyjatki) w nowej karcie
        async Task OpenUbolSettingsAsync()
        {
            try
            {
                if (Core == null) return;
                var ext = (await Core.Profile.GetBrowserExtensionsAsync()).FirstOrDefault(IsUbol);
                if (ext == null) { System.Windows.MessageBox.Show(this, L.T("uBlock Origin Lite nie jest włączony."), "Velivo"); return; }
                AddTab("chrome-extension://" + ext.Id + "/dashboard.html");
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // pierwsze wystapienie "pole":"wartosc" (w zdarzeniu requestWillBeSent pierwszy "url" to adres zadania)
        static string JsonField(string json, string name)
        {
            var key = "\"" + name + "\":\"";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            var sb = new System.Text.StringBuilder();
            for (; i < json.Length && json[i] != '"'; i++)
            {
                if (json[i] == '\\' && i + 1 < json.Length) { i++; sb.Append(json[i] == 'u' ? '?' : json[i]); if (json[i] == 'u') i += 4; }
                else sb.Append(json[i]);
            }
            return sb.ToString();
        }

        async Task EnsureBundledUbolAsync()
        {
            var dir = BestUbolDir();
            if (Core == null || dir == null) return;
            try
            {
                var dirVer = ManifestVersion(dir).ToString();
                string have = null;
                try { if (File.Exists(UbolVersionFile)) have = File.ReadAllText(UbolVersionFile).Trim(); } catch (Exception) { }
                var exts = await Core.Profile.GetBrowserExtensionsAsync();
                var installed = exts.FirstOrDefault(IsUbol);
                bool want = _settings == null || _settings.UbolLite;   // wbudowany, domyslnie wlaczony - uzytkownik moze wylaczyc
                // nowsza wersja niz zainstalowana przez Velivo - podmiana dodatku
                // (dodatek ze sklepu, bez pliku wersji Velivo, zostawiamy w spokoju)
                if (installed != null && have != null && have != dirVer && want)
                {
                    await installed.RemoveAsync();
                    installed = null;
                }
                if (installed != null)
                {
                    if (installed.IsEnabled != want) await installed.EnableAsync(want);
                    return;
                }
                if (!want) return;
                var added = await Core.Profile.AddBrowserExtensionAsync(dir);
                if (!added.IsEnabled) await added.EnableAsync(true);
                SaveExtPath(added.Id, dir);
                Directory.CreateDirectory(Path.GetDirectoryName(UbolVersionFile));
                File.WriteAllText(UbolVersionFile, dirVer);
                // stare pobrane wersje
                try { foreach (var d in Directory.GetDirectories(UpdatedUbolRoot)) if (!string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)) Directory.Delete(d, true); } catch (Exception) { }
            }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
