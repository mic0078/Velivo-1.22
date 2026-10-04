using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Przegladarka
{
    // Obsluga tego, czego WebView2 nie daje dodatkom: pasek ikon z okienkami (popup),
    // strona nowej karty z dodatku (chrome_url_overrides.newtab) i strona opcji.
    // Manifest czytamy z folderu dodatku; sciezke znamy z naszego zapisu albo z Preferences profilu.
    public partial class MainWindow
    {
        sealed class ExtInfo
        {
            public string Id, Name, Folder, Popup, Options, NewTab, Icon;
            public bool Enabled;
        }

        sealed class SyncedExtensionItem
        {
            public string StoreId { get; set; }
            public bool Enabled { get; set; }
        }

        List<ExtInfo> _extInfos = new List<ExtInfo>();
        const string QuickAccessExtensionId = "acniffmanfmekaehjjbbkiogoaehogpf";
        // ID, pod ktorym Szybki Dostep jest zainstalowany u uzytkownika (chrome-extension://nkblpgfbidmbnemeecboimbnpkgmajfn/kod/newtab.html).
        // Zawsze rozpoznawany i zaufany - niezaleznie od mapy ID, ktora moze byc pusta albo nieaktualna.
        const string QuickAccessTrustedId = "nkblpgfbidmbnemeecboimbnpkgmajfn";
        static string ExtPathsFile { get { return Path.Combine(DataDir, "Dodatki", "sciezki.txt"); } }
        static string ExtensionsSyncListFile { get { return Path.Combine(DataDir, "Dodatki", "lista-sync.txt"); } }

        static string QuickAccessInstalledId
        {
            get
            {
                try
                {
                    string id;
                    var map = LoadStoreToInstalledMap();
                    if (map.TryGetValue(QuickAccessExtensionId, out id) && !string.IsNullOrWhiteSpace(id))
                        return id.Trim();
                }
                catch (Exception) { }
                return QuickAccessExtensionId;
            }
        }

        static IEnumerable<string> QuickAccessKnownIds()
        {
            yield return QuickAccessExtensionId;
            yield return QuickAccessTrustedId;
            var installed = QuickAccessInstalledId;
            if (!string.Equals(installed, QuickAccessExtensionId, StringComparison.OrdinalIgnoreCase))
                yield return installed;
        }

        static bool IsQuickAccessExtensionId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            return QuickAccessKnownIds().Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        }

        // ---------- sciezki folderow dodatkow ----------

        static Dictionary<string, string> LoadExtPaths()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // 1) co zapisal Chromium w profilu (dodatki wczytane wczesniej)
            foreach (var name in new[] { "Secure Preferences", "Preferences" })
            {
                var f = Path.Combine(DataDir, "Profil", "EBWebView", "Default", name);
                try
                {
                    if (!File.Exists(f)) continue;
                    using (var doc = JsonDocument.Parse(File.ReadAllText(f)))
                    {
                        JsonElement ext, settings;
                        if (!doc.RootElement.TryGetProperty("extensions", out ext) || !ext.TryGetProperty("settings", out settings)) continue;
                        foreach (var p in settings.EnumerateObject())
                        {
                            JsonElement path;
                            if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("path", out path) && path.ValueKind == JsonValueKind.String
                                && Path.IsPathRooted(path.GetString()))
                                map[p.Name] = path.GetString();
                        }
                    }
                }
                catch (Exception) { }
            }
            // 2) nasz wlasny zapis (ma pierwszenstwo - aktualny od razu po dodaniu)
            try
            {
                if (File.Exists(ExtPathsFile))
                    foreach (var line in File.ReadAllLines(ExtPathsFile))
                    {
                        int i = line.IndexOf('\t');
                        if (i > 0) map[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch (IOException) { }
            return map;
        }

        static void SaveExtPath(string id, string folder)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ExtPathsFile));
                var lines = File.Exists(ExtPathsFile) ? File.ReadAllLines(ExtPathsFile).Where(l => !l.StartsWith(id + "\t", StringComparison.OrdinalIgnoreCase)).ToList() : new List<string>();
                if (folder != null) lines.Add(id + "\t" + folder);
                File.WriteAllLines(ExtPathsFile, lines);
            }
            catch (IOException) { }
        }

        static Dictionary<string, string> LoadStoreToInstalledMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!Directory.Exists(ExtensionsDir)) return map;
                foreach (var idf in Directory.GetFiles(ExtensionsDir, "*.id"))
                {
                    var storeId = Path.GetFileNameWithoutExtension(idf)?.ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(storeId)) continue;
                    var installedId = File.ReadAllText(idf).Trim();
                    if (installedId.Length > 0) map[storeId] = installedId;
                }
            }
            catch (Exception) { }
            return map;
        }

        static Dictionary<string, string> LoadInstalledToStoreMap()
        {
            var rev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in LoadStoreToInstalledMap())
                rev[kv.Value] = kv.Key;
            return rev;
        }

        static Dictionary<string, bool> LoadSyncedExtensionsList()
        {
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(ExtensionsSyncListFile)) return map;
                var items = JsonSerializer.Deserialize<List<SyncedExtensionItem>>(File.ReadAllText(ExtensionsSyncListFile));
                if (items == null) return map;
                foreach (var it in items)
                {
                    var id = (it.StoreId ?? "").Trim().ToLowerInvariant();
                    if (id.Length == 32 && id.All(ch => ch >= 'a' && ch <= 'p')) map[id] = it.Enabled;
                }
            }
            catch (Exception) { }
            return map;
        }

        async Task SaveExtensionsSyncListAsync()
        {
            if (Core == null) return;
            try
            {
                var byInstalled = LoadInstalledToStoreMap();
                var exts = await Core.Profile.GetBrowserExtensionsAsync();
                var items = exts.Where(x => !BuiltInExtensions.Contains(x.Id))
                    .Where(x => !IsQuickAccessExtensionId(x.Id))
                    .Select(x =>
                    {
                        string storeId;
                        return byInstalled.TryGetValue(x.Id, out storeId)
                            ? new SyncedExtensionItem { StoreId = storeId, Enabled = x.IsEnabled }
                            : null;
                    })
                    .Where(x => x != null)
                    .OrderBy(x => x.StoreId)
                    .ToList();

                Directory.CreateDirectory(Path.GetDirectoryName(ExtensionsSyncListFile));
                File.WriteAllText(ExtensionsSyncListFile, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        async Task ApplyExtensionsSyncListAsync()
        {
            if (Core == null) return;
            var desired = LoadSyncedExtensionsList();
            if (desired.Count == 0) return;

            try
            {
                var exts = await Core.Profile.GetBrowserExtensionsAsync();
                var byId = exts.Where(x => !BuiltInExtensions.Contains(x.Id)).ToDictionary(x => x.Id, x => x, StringComparer.OrdinalIgnoreCase);
                var storeToInstalled = LoadStoreToInstalledMap();

                foreach (var kv in desired)
                {
                    if (string.Equals(kv.Key, QuickAccessExtensionId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string installedId;
                    CoreWebView2BrowserExtension ext;
                    bool have = storeToInstalled.TryGetValue(kv.Key, out installedId) && byId.TryGetValue(installedId, out ext);
                    if (!have)
                    {
                        await InstallFromStore(kv.Key, this, true);
                        exts = await Core.Profile.GetBrowserExtensionsAsync();
                        byId = exts.Where(x => !BuiltInExtensions.Contains(x.Id)).ToDictionary(x => x.Id, x => x, StringComparer.OrdinalIgnoreCase);
                        storeToInstalled = LoadStoreToInstalledMap();
                    }

                    if (storeToInstalled.TryGetValue(kv.Key, out installedId) && byId.TryGetValue(installedId, out ext))
                        if (ext.IsEnabled != kv.Value) await ext.EnableAsync(kv.Value);
                }

                await RefreshExtensions();
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // ---------- manifest ----------

        static string Str(JsonElement e, params string[] path)
        {
            foreach (var p in path)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
            }
            return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }

        // Najwieksza ikona z obiektu {"16": "...", "128": "..."} albo pojedynczy napis.
        static string BestIcon(JsonElement root, params string[][] candidates)
        {
            foreach (var path in candidates)
            {
                JsonElement e = root; bool ok = true;
                foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) { ok = false; break; } }
                if (!ok) continue;
                if (e.ValueKind == JsonValueKind.String) return e.GetString();
                if (e.ValueKind == JsonValueKind.Object)
                {
                    string best = null; int bestSize = -1;
                    foreach (var p in e.EnumerateObject())
                    {
                        int size; int.TryParse(p.Name, out size);
                        if (p.Value.ValueKind == JsonValueKind.String && (size >= 32 && (bestSize < 32 || size < bestSize) || bestSize < 0))
                        { best = p.Value.GetString(); bestSize = size; }
                    }
                    if (best != null) return best;
                }
            }
            return null;
        }

        static ExtInfo ReadManifest(CoreWebView2BrowserExtension ext, string folder)
        {
            var info = new ExtInfo { Id = ext.Id, Name = ext.Name, Enabled = ext.IsEnabled, Folder = folder };
            if (folder == null) return info;
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
                {
                    var r = doc.RootElement;
                    info.Popup = Str(r, "action", "default_popup") ?? Str(r, "browser_action", "default_popup") ?? Str(r, "page_action", "default_popup");
                    info.Options = Str(r, "options_ui", "page") ?? Str(r, "options_page");
                    info.NewTab = Str(r, "chrome_url_overrides", "newtab");
                    var icon = BestIcon(r, new[] { "action", "default_icon" }, new[] { "browser_action", "default_icon" }, new[] { "icons" });
                    if (icon != null) info.Icon = Path.Combine(folder, icon.TrimStart('/').Replace('/', '\\'));
                }
            }
            catch (Exception) { }
            return info;
        }

        static string ExtUrl(ExtInfo x, string page) { return "chrome-extension://" + x.Id + "/" + page.TrimStart('/'); }

        // ---------- przeladowanie dodatkow z folderu ----------
        // WebView2 trzyma skrypt tla dodatku w pamieci podrecznej i nie czyta zmienionych plikow z folderu
        // (Chrome robi to przy starcie). Wylaczenie i wlaczenie wczytuje dodatek od nowa, a jego dane zostaja.

        static string ExtStampsFile { get { return Path.Combine(DataDir, "Dodatki", "znaczniki.txt"); } }

        // "Odcisk" kodu dodatku: liczba i daty plikow kodu (bez danych, ktore dodatek moze sam zmieniac).
        static string CodeStamp(string folder)
        {
            try
            {
                var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".js", ".mjs", ".html", ".css", ".wasm" };
                var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(f => exts.Contains(Path.GetExtension(f)) || string.Equals(Path.GetFileName(f), "manifest.json", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                long newest = files.Count == 0 ? 0 : files.Max(f => File.GetLastWriteTimeUtc(f).Ticks);
                return files.Count + ":" + newest;
            }
            catch (Exception) { return null; }
        }

        static Dictionary<string, string> LoadStamps()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(ExtStampsFile))
                    foreach (var line in File.ReadAllLines(ExtStampsFile))
                    {
                        int i = line.IndexOf('\t');
                        if (i > 0) map[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch (IOException) { }
            return map;
        }

        static void SaveStamps(Dictionary<string, string> map)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ExtStampsFile));
                File.WriteAllLines(ExtStampsFile, map.Select(kv => kv.Key + "\t" + kv.Value));
            }
            catch (IOException) { }
        }

        static async Task ReloadExtension(CoreWebView2BrowserExtension ext)
        {
            await ext.EnableAsync(false);
            await ext.EnableAsync(true);
        }

        // Przy starcie: przeladuj wlaczone dodatki z folderu, ktorych kod zmienil sie od ostatniego razu.
        async Task ReloadChangedExtensions(IReadOnlyList<CoreWebView2BrowserExtension> exts, Dictionary<string, string> paths)
        {
            var stamps = LoadStamps();
            bool changed = false;
            foreach (var ext in exts)
            {
                string folder;
                if (!ext.IsEnabled || BuiltInExtensions.Contains(ext.Id) || !paths.TryGetValue(ext.Id, out folder) || !Directory.Exists(folder)) continue;
                var stamp = CodeStamp(folder);
                string old;
                if (stamp == null || (stamps.TryGetValue(ext.Id, out old) && old == stamp)) continue;
                try { await ReloadExtension(ext); stamps[ext.Id] = stamp; changed = true; }
                catch (Exception) { }
            }
            if (changed) SaveStamps(stamps);
        }

        // ---------- pasek ikon ----------

        bool _extensionsReloadChecked;

        async Task RefreshExtensions()
        {
            if (Core == null) return;
            IReadOnlyList<CoreWebView2BrowserExtension> exts;
            try { exts = await Core.Profile.GetBrowserExtensionsAsync(); }
            catch (Exception) { return; }
            var paths = LoadExtPaths();
            if (!_extensionsReloadChecked)
            {
                _extensionsReloadChecked = true;
                await ReloadChangedExtensions(exts, paths);
            }
            _extInfos = exts.Where(x => !BuiltInExtensions.Contains(x.Id))
                            .Select(x => { string f; paths.TryGetValue(x.Id, out f); return ReadManifest(x, f); })
                            .ToList();

            RebuildExtBar();
        }

        // ---------- przypinanie dodatkow do paska ----------
        // Odpiety dodatek dziala normalnie, tylko jego ikonka nie zajmuje miejsca na pasku - jest w menu pod przyciskiem Dodatki.

        static string UnpinnedExtFile { get { return Path.Combine(DataDir, "dodatki-odpiete.txt"); } }
        HashSet<string> _unpinnedExt;

        HashSet<string> UnpinnedExt
        {
            get
            {
                if (_unpinnedExt == null)
                {
                    _unpinnedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try { if (File.Exists(UnpinnedExtFile)) foreach (var l in File.ReadAllLines(UnpinnedExtFile)) if (l.Trim().Length > 0) _unpinnedExt.Add(l.Trim()); }
                    catch (IOException) { }
                }
                return _unpinnedExt;
            }
        }

        void SetExtPinned(ExtInfo info, bool pinned)
        {
            if (pinned) UnpinnedExt.Remove(info.Id); else UnpinnedExt.Add(info.Id);
            try { Directory.CreateDirectory(DataDir); File.WriteAllLines(UnpinnedExtFile, UnpinnedExt.OrderBy(x => x)); }
            catch (IOException) { }
            RebuildExtBar();
        }

        object ExtIconContent(ExtInfo info, double size)
        {
            BitmapImage img = null;
            try
            {
                if (info.Icon != null && File.Exists(info.Icon))
                {
                    img = new BitmapImage();
                    img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.UriSource = new Uri(info.Icon); img.DecodePixelWidth = 32; img.EndInit();
                }
            }
            catch (Exception) { img = null; }
            return img != null
                ? (object)new Image { Source = img, Width = size, Height = size }
                : new TextBlock { Text = string.IsNullOrEmpty(info.Name) ? "?" : info.Name.Substring(0, 1).ToUpperInvariant(), FontWeight = FontWeights.Bold };
        }

        // Przycisk Dodatki: lista wszystkich wlaczonych dodatkow z pinezka (jak w Chrome) i wejscie do zarzadzania.
        void ExtensionsMenu_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = ExtensionsBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var x in _extInfos.Where(i => i.Enabled))
            {
                var info = x;
                bool pinned = !UnpinnedExt.Contains(info.Id);
                var row = new DockPanel { MinWidth = 240, LastChildFill = true };
                var pin = new Button
                {
                    Content = pinned ? "📌" : "📍", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(12, 0, 0, 0),
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), Opacity = pinned ? 1 : 0.45,
                    ToolTip = pinned ? L.T("Odepnij z paska (dodatek dalej działa)") : L.T("Przypnij do paska")
                };
                DockPanel.SetDock(pin, Dock.Right);
                pin.Click += (s, a) => { a.Handled = true; menu.IsOpen = false; SetExtPinned(info, !pinned); };
                row.Children.Add(pin);
                row.Children.Add(new TextBlock { Text = info.Name, VerticalAlignment = VerticalAlignment.Center });
                var item = new MenuItem { Header = row, Icon = ExtIconContent(info, 16) };
                item.Click += (s, a) => OpenExtensionPopup(info, ExtensionsBtn);
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = L.T("Brak włączonych dodatków"), IsEnabled = false });
            menu.Items.Add(new Separator());
            var manage = new MenuItem { Header = L.T("Zarządzaj dodatkami…") };
            manage.Click += (s, a) => Extensions_Click(null, null);
            menu.Items.Add(manage);
            menu.IsOpen = true;
        }

        void RebuildExtBar()
        {
            ExtBar.Children.Clear();
            foreach (var x in _extInfos.Where(i => i.Enabled && !UnpinnedExt.Contains(i.Id)))
            {
                var info = x;
                var btn = new Button { ToolTip = info.Name, Padding = new Thickness(0) };
                btn.Content = ExtIconContent(info, 18);
                btn.Click += (s, e) => OpenExtensionPopup(info, btn);
                var menu = new ContextMenu();
                if (info.Popup != null) { var m = new MenuItem { Header = L.T("Otwórz okienko dodatku") }; m.Click += (s, e) => OpenExtensionPopup(info, btn); menu.Items.Add(m); }
                if (info.Popup != null) { var m = new MenuItem { Header = L.T("Otwórz okienko w karcie") }; m.Click += (s, e) => AddTab(ExtUrl(info, info.Popup)); menu.Items.Add(m); }
                if (info.Options != null) { var m = new MenuItem { Header = L.T("Opcje") }; m.Click += (s, e) => AddTab(ExtUrl(info, info.Options)); menu.Items.Add(m); }
                if (info.NewTab != null) { var m = new MenuItem { Header = L.T("Strona nowej karty") }; m.Click += (s, e) => AddTab(ExtUrl(info, info.NewTab)); menu.Items.Add(m); }
                var unpin = new MenuItem { Header = L.T("Odepnij z paska (dodatek dalej działa)") }; unpin.Click += (s, e) => SetExtPinned(info, false); menu.Items.Add(unpin);
                var manage = new MenuItem { Header = L.T("Zarządzaj dodatkami…") }; manage.Click += (s, e) => Extensions_Click(null, null); menu.Items.Add(manage);
                btn.ContextMenu = menu;
                ExtBar.Children.Add(btn);
            }
        }

        // Strona nowej karty: z wlaczonego dodatku, ktory ja podmienia, inaczej strona startowa.
        string NewTabUrl
        {
            get
            {
                var x = _extInfos.FirstOrDefault(i => i.Enabled && i.NewTab != null &&
                    (!IsQuickAccessExtensionId(i.Id) || _settings == null || _settings.QuickAccessNewTab));
                return x != null ? ExtUrl(x, x.NewTab) : HomeUrl;
            }
        }

        const int BridgeTabId = -7770;

        // W Chrome klikniecie ikonki daje dodatkowi dostep do aktywnej karty (activeTab). WebView2 tego nie robi,
        // a kazda karta jest dla dodatku osobnym oknem - wiec w okienku dodatku podmieniamy:
        //  - chrome.tabs.query({active: true}) -> karta ze strona, na ktorej jest uzytkownik,
        //  - chrome.scripting.executeScript({target: {tabId: ta karta}, func}) -> wykonuje program w tej karcie.
        async Task InstallActiveTabBridge(CoreWebView2 popup, BrowserTab pageTab)
        {
            var page = pageTab.View.CoreWebView2;
            string tabJson = JsonSerializer.Serialize(new
            {
                id = BridgeTabId, windowId = BridgeTabId, index = 0, active = true, highlighted = true,
                pinned = false, incognito = pageTab.Private, status = "complete", url = page.Source, title = page.DocumentTitle
            });
            string shim = @"(() => {
  if (!window.chrome || !chrome.tabs) return;
  const AKT = " + tabJson + @";
  const wynik = (p, cb) => { if (typeof cb === 'function') { p.then(cb, () => cb()); return; } return p; };
  const q = chrome.tabs.query.bind(chrome.tabs);
  chrome.tabs.query = (info, cb) => wynik(info && info.active ? Promise.resolve([Object.assign({}, AKT)]) : q(info || {}), cb);
  if (chrome.tabs.get) { const g = chrome.tabs.get.bind(chrome.tabs); chrome.tabs.get = (id, cb) => wynik(id === AKT.id ? Promise.resolve(Object.assign({}, AKT)) : g(id), cb); }
  if (!chrome.scripting || !window.chrome.webview) return;
  const ex = chrome.scripting.executeScript.bind(chrome.scripting);
  const czeka = {}; let n = 0;
  window.__przegladarkaWynik = (k, ok, v) => { const c = czeka[k]; delete czeka[k]; if (!c) return; if (ok) c.res([{ frameId: 0, result: v }]); else c.rej(new Error(v)); };
  chrome.scripting.executeScript = (inj, cb) => {
    if (!inj || !inj.target || inj.target.tabId !== AKT.id) return ex(inj, cb);
    const k = ++n;
    const p = new Promise((res, rej) => { czeka[k] = { res, rej }; });
    if (typeof inj.func !== 'function') czeka[k].rej(new Error('Velivo obsluguje tu tylko executeScript z func'));
    else window.chrome.webview.postMessage(JSON.stringify({ typ: 'wykonaj', k, func: inj.func.toString(), args: inj.args || [] }));
    return wynik(p, cb);
  };
})();";
            await popup.AddScriptToExecuteOnDocumentCreatedAsync(shim);
            popup.WebMessageReceived += async (s, e) =>
            {
                // polecenia wykonania kodu przyjmujemy tylko od strony dodatku, nigdy od zwyklej strony WWW
                if (!(e.Source ?? "").StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) return;
                int k = 0;
                try
                {
                    using (var doc = JsonDocument.Parse(e.TryGetWebMessageAsString()))
                    {
                        var r = doc.RootElement;
                        if (Str(r, "typ") != "wykonaj") return;
                        k = r.GetProperty("k").GetInt32();
                        string code = "(() => { try { return { ok: true, v: (" + r.GetProperty("func").GetString() + ").apply(null, " +
                                      r.GetProperty("args").GetRawText() + ") }; } catch (e) { return { ok: false, v: String(e && e.message || e) }; } })()";
                        var target = pageTab.View.CoreWebView2;
                        if (target == null) throw new InvalidOperationException(L.T("Karta została zamknięta."));
                        string res = await target.ExecuteScriptAsync(code);
                        using (var rd = JsonDocument.Parse(res))
                        {
                            var root = rd.RootElement;
                            JsonElement okEl, vEl;
                            bool ok = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out okEl) && okEl.ValueKind == JsonValueKind.True;
                            string v = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("v", out vEl) ? vEl.GetRawText() : "null";
                            await popup.ExecuteScriptAsync("window.__przegladarkaWynik(" + k + ", " + (ok ? "true" : "false") + ", " + v + ")");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (k > 0)
                        try { await popup.ExecuteScriptAsync("window.__przegladarkaWynik(" + k + ", false, " + JsonSerializer.Serialize(ex.Message) + ")"); }
                        catch (Exception) { }
                }
            };
        }

        // Okienko dodatku pod jego ikonka, zamykane po kliknieciu gdzie indziej (jak w Chrome).
        void OpenExtensionPopup(ExtInfo info, FrameworkElement anchor)
        {
            if (info.Popup == null)
            {
                if (info.Options != null) AddTab(ExtUrl(info, info.Options));
                else MessageBox.Show(this, L.T("Dodatek „") + info.Name + L.T("” nie ma własnego okienka.\nDziała w tle i na stronach."), L.T("Dodatki"));
                return;
            }
            var pageTab = _current; // strona, dla ktorej otwieramy okienko (w Chrome: aktywna karta)
            var view = new WebView2();
            var win = new Window
            {
                Title = info.Name, Width = 400, Height = 560, Owner = this, ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow, Content = view, WindowStartupLocation = WindowStartupLocation.Manual
            };
            var pt = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
            var src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null) pt = src.CompositionTarget.TransformFromDevice.Transform(pt);
            win.Left = Math.Max(0, pt.X - win.Width); win.Top = pt.Y + 2;
            bool closing = false;
            var openedAt = DateTime.UtcNow;
            win.Deactivated += (s, e) =>
            {
                // zamknij po kliknieciu poza okienkiem, ale nie gdy okienko otworzylo wlasne okno dialogowe
                // ani tuz po otwarciu (glowne okno potrafi wtedy na chwile przejac fokus)
                if (closing || win.OwnedWindows.Count > 0 || (DateTime.UtcNow - openedAt).TotalMilliseconds < 1500) return;
                closing = true;
                win.Dispatcher.BeginInvoke(new Action(() => { try { win.Close(); } catch (InvalidOperationException) { } }));
            };
            win.Closed += (s, e) => view.Dispose();
            win.Loaded += async (s, e) =>
            {
                try
                {
                    await view.EnsureCoreWebView2Async(_env);
                    var core = view.CoreWebView2;
                    ApplyViewSettings(core);
                    core.NavigationStarting += (a, b) =>
                    {
                        core.Settings.IsWebMessageEnabled = IsQuickAccessUrl(b.Uri);
                        core.Settings.IsReputationCheckingRequired = _settings.SmartScreen && ShouldUseReputationCheck(b.Uri) && !IsTrustedUrl(b.Uri);
                    };
                    core.WindowCloseRequested += (a, b) => { closing = true; win.Close(); };
                    core.NewWindowRequested += (a, b) => OnNewWindowRequested(b, false);
                    if (pageTab != null && pageTab.View.CoreWebView2 != null)
                        await InstallActiveTabBridge(core, pageTab);
                    core.DocumentTitleChanged += (a, b) => { if (!string.IsNullOrEmpty(core.DocumentTitle)) win.Title = core.DocumentTitle; };
                    // jak w Chrome: okienko dopasowane do zawartosci dodatku (bez pustego miejsca i bez paska przewijania)
                    core.NavigationCompleted += async (a, b) =>
                    {
                        foreach (var delay in new[] { 0, 250, 800 })
                        {
                            try
                            {
                                if (delay > 0) await System.Threading.Tasks.Task.Delay(delay);
                                if (view.CoreWebView2 == null) return;
                                var r = await core.ExecuteScriptAsync("(function(){var e=document.documentElement,b=document.body;if(!b)return '';e.style.width='max-content';e.style.overflowX='hidden';b.style.margin=getComputedStyle(b).margin;var w=Math.ceil(Math.max(e.scrollWidth,b.scrollWidth,e.getBoundingClientRect().width));var h=Math.ceil(Math.max(e.scrollHeight,b.scrollHeight));return w+'x'+h;})()");
                                var t = System.Text.Json.JsonSerializer.Deserialize<string>(r) ?? "";
                                var parts = t.Split('x');
                                double w, h;
                                if (parts.Length != 2 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out w) || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out h)) continue;
                                var z = view.ZoomFactor;
                                w = Math.Max(160, Math.Min(800, w)) * z;
                                h = Math.Max(60, Math.Min(600, h)) * z;
                                view.Width = w; view.Height = h;
                                win.SizeToContent = SizeToContent.WidthAndHeight;
                                win.Left = Math.Max(0, pt.X - win.ActualWidth);
                            }
                            catch (Exception) { }
                        }
                    };
                    core.Navigate(ExtUrl(info, info.Popup));
                }
                catch (Exception ex)
                {
                    closing = true; win.Close();
                    MessageBox.Show(this, L.T("Nie udało się otworzyć okienka dodatku:\n") + ex.Message, L.T("Dodatki"));
                }
            };
            win.Show();
        }
    }
}
