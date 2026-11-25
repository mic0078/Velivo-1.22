using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // 🔎 informacje o filmie z własnej biblioteki - z Wikidanych i Wikipedii (bez konta i klucza): tytuł, rok, reżyseria, obsada, gatunek,
    // opis (polska Wikipedia, zapasowo angielska) i plakat (obrazek artykułu); zapis w Velivo jak „📝 Uzupełnij informacje”
    public partial class MainWindow
    {
        // 🚦 wszystkie zapytania do Wikimedia (Wikipedia, Wikidane, Commons) przez jeden lejek: po jednym, co najmniej 0,3 s przerwy;
        //    429 (za dużo zapytań) - czekanie tyle, ile każe serwer (najwyżej 2 min), i jedno ponowienie. Było: zdjęcia 12 aktorów naraz → Wikimedia blokowało Velivo
        //    i przestawało działać wyszukiwanie i plakaty filmów (2026-11-25).
        sealed class WikiLejek : DelegatingHandler
        {
            static readonly System.Threading.SemaphoreSlim jeden = new System.Threading.SemaphoreSlim(1, 1); static DateTime nast = DateTime.MinValue;
            public WikiLejek() : base(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, System.Threading.CancellationToken ct)
            {
                await jeden.WaitAsync(ct);
                try
                {
                    for (int proba = 0; ; proba++)
                    {
                        var w = nast - DateTime.UtcNow; if (w > TimeSpan.Zero) await Task.Delay(w, ct);
                        var r = await base.SendAsync(proba == 0 ? req : Kopia(req), ct);
                        nast = DateTime.UtcNow.AddMilliseconds(300);
                        if ((int)r.StatusCode != 429 || proba > 0) return r;
                        var ra = r.Headers.RetryAfter?.Delta ?? (r.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(20);
                        nast = DateTime.UtcNow + (ra < TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : ra > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : ra); r.Dispose();
                    }
                }
                finally { jeden.Release(); }
            }
            static HttpRequestMessage Kopia(HttpRequestMessage r) { var k = new HttpRequestMessage(r.Method, r.RequestUri); foreach (var h in r.Headers) k.Headers.TryAddWithoutValidation(h.Key, h.Value); return k; }
        }
        static HttpClient WikiHttp()
        {
            var h = new HttpClient(new WikiLejek()) { Timeout = TimeSpan.FromMinutes(3) };   // czas z czekaniem w lejku
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Velivo/1.22 (film library)");   // Wikimedia wymaga nazwy programu
            return h;
        }
        static string WdStr(JsonElement e, params string[] path) { foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null; } return e.ValueKind == JsonValueKind.String ? e.GetString() : null; }
        static List<string> WdIds(JsonElement claims, string prop) =>
            claims.TryGetProperty(prop, out var a) ? a.EnumerateArray().Select(c => WdStr(c, "mainsnak", "datavalue", "value", "id")).Where(x => x != null).ToList() : new List<string>();

        void ShowWikiSearch(Window owner, string path, string fileTitle, Action done)
        {
            var win = new Window { Title = L.T("🔎 Wyszukaj informacje o filmie"), Width = 720, Height = 560, Owner = owner, Topmost = owner?.Topmost == true, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            Independent(win);
            var ym = System.Text.RegularExpressions.Regex.Match(fileTitle ?? "", @"^(.+?)[ .(]+((?:19|20)\d{2})\)?");
            var qName = new TextBox { Text = ym.Success ? ym.Groups[1].Value.Replace('.', ' ').Trim() : fileTitle, Padding = new Thickness(4), Width = 380 };
            var qYear = new TextBox { Text = ym.Success ? ym.Groups[2].Value : "", Padding = new Thickness(4), Width = 70, Margin = new Thickness(6, 0, 6, 0), ToolTip = L.T("Rok (pomaga wybrać właściwy film)") };
            var status = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(12, 4, 12, 4), TextWrapping = TextWrapping.Wrap };
            var list = new ListBox { Margin = new Thickness(12, 0, 12, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            var found = new List<FilmWynik>();
            async Task Szukaj()
            {
                status.Text = L.T("Szukam…"); list.Items.Clear(); found.Clear();
                try { found = await SzukajFilmu(qName.Text.Trim(), qYear.Text.Trim()); }
                catch (Exception ex) { status.Text = L.T("⚠ Błąd wyszukiwania: ") + ex.Message; return; }
                foreach (var f in found)
                {
                    var row = new DockPanel { Margin = new Thickness(2, 4, 2, 4) };
                    var img = new Image { Width = 46, Height = 68, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 10, 0) };
                    if (f.Img != null) try { img.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(f.Img)); } catch (Exception) { }
                    DockPanel.SetDock(img, Dock.Left); row.Children.Add(img);
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = f.Label + (f.Year > 0 ? " (" + f.Year + ")" : ""), FontWeight = FontWeights.SemiBold, FontSize = 14 });
                    sp.Children.Add(new TextBlock { Text = (f.Desc ?? "").Length > 240 ? f.Desc.Substring(0, 240) + "…" : f.Desc ?? "", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
                    row.Children.Add(sp);
                    list.Items.Add(new ListBoxItem { Content = row });
                }
                if (found.Count > 0) list.SelectedIndex = 0;
                status.Text = found.Count == 0 ? L.T("Nic nie znaleziono – zmień tytuł (np. oryginalny, angielski) i szukaj ponownie.") : L.T("Wybierz właściwy film i kliknij „Zastosuj”.");
            }
            var go = SmallButton(L.T("🔎 Szukaj"), async () => await Szukaj());
            var apply = SmallButton(L.T("✅ Zastosuj"), async () =>
            {
                int i = list.SelectedIndex; if (i < 0 || i >= found.Count) return;
                status.Text = L.T("Pobieram opis, obsadę i plakat…");
                try
                {
                    var m = await PobierzFilm(found[i], path, LookupMeta(Meta, path));
                    var key = MetaKey(path); var man = LoadManual(); man[key] = m;
                    Directory.CreateDirectory(MetaDir); File.WriteAllText(ManualFile, JsonSerializer.Serialize(man));
                    Meta[key] = m; MetaObokFilmu(path, true); win.Close(); done?.Invoke();   // 📁 też obok filmu
                }
                catch (Exception ex) { status.Text = L.T("⚠ Nie udało się pobrać: ") + ex.Message; }
            });
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12) };
            top.Children.Add(new TextBlock { Text = L.T("Tytuł:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }); top.Children.Add(qName);
            top.Children.Add(new TextBlock { Text = L.T("Rok:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }); top.Children.Add(qYear); top.Children.Add(go);
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
            bottom.Children.Add(SmallButton(L.T("🌐 Szukaj w Filmweb"), () => ShowWebWindow(win, "https://www.filmweb.pl/search#/?query=" + Uri.EscapeDataString(qName.Text.Trim()))));   // film spoza Wikipedii
            bottom.Children.Add(SmallButton(L.T("📝 Wpisz ręcznie"), () => { win.Close(); ShowMetaEditor(owner, path, qName.Text.Trim(), done); }));
            bottom.Children.Add(apply);
            var dock = new DockPanel(); DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(status, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(top); dock.Children.Add(status); dock.Children.Add(bottom); dock.Children.Add(list);
            list.MouseDoubleClick += (s, e) => apply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            qName.KeyDown += async (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) await Szukaj(); };
            win.Content = dock; win.Show();
            _ = Szukaj();
        }

        // wynik wyszukiwania: TMDb (przez Jellyfin - ta sama baza co w Odtwarzaczu) albo Wikidane
        internal sealed class FilmWynik { public string WdId, Label, Desc, Img, Imdb, Tmdb; public int Year; public string Raw; }

        // najpierw TMDb (opis, plakat, rok - jak Odtwarzacz), potem Wikidane (polskie i angielskie); pasujący rok wyżej
        async Task<List<FilmWynik>> SzukajFilmu(string q, string rok)
        {
            var r = new List<FilmWynik>(); int.TryParse(rok, out var y);
            var c = Wariant.Serwer && !SerwerJfInfo ? null : LoadJellyfinConf();   // 🎞 Velivo Serwer filmów - samodzielny (Wikidane, Wikipedia); Jellyfin tylko za zgodą
            if (c != null && !string.IsNullOrEmpty(c.Url) && !string.IsNullOrEmpty(c.Key))
                try
                {
                    var url = await JellyfinReach(c) ?? c.Url;
                    using (var http = JellyfinClient(new JellyfinConf { Url = url, Key = c.Key }))
                    {
                        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["SearchInfo"] = y > 0 ? (object)new { Name = q, Year = y, MetadataLanguage = "pl", MetadataCountryCode = "PL" } : new { Name = q, MetadataLanguage = "pl", MetadataCountryCode = "PL" } });   // opisy po polsku
                        var resp = await http.PostAsync("Items/RemoteSearch/Movie", new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                        if (resp.IsSuccessStatusCode)
                            foreach (var x in JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.EnumerateArray())
                            {
                                string S(string n) => x.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                                var pid = x.TryGetProperty("ProviderIds", out var p) ? p : default;
                                r.Add(new FilmWynik { Label = S("Name"), Desc = S("Overview"), Img = S("ImageUrl"), Year = x.TryGetProperty("ProductionYear", out var py) && py.ValueKind == JsonValueKind.Number ? py.GetInt32() : 0,
                                    Imdb = pid.ValueKind == JsonValueKind.Object ? WdStr(pid, "Imdb") : null, Tmdb = pid.ValueKind == JsonValueKind.Object ? WdStr(pid, "Tmdb") : null, Raw = x.GetRawText() });
                            }
                        else App.LogInfo("Wyszukiwanie filmu (TMDb przez Jellyfin): " + (int)resp.StatusCode);
                    }
                }
                catch (Exception ex) { App.LogInfo("Wyszukiwanie filmu (TMDb przez Jellyfin): " + ex.Message); }
            using (var http = WikiHttp())
                foreach (var lang in new[] { "pl", "en" })
                {
                    var js = await http.GetStringAsync("https://www.wikidata.org/w/api.php?action=wbsearchentities&format=json&type=item&limit=20&language=" + lang + "&uselang=pl&search=" + Uri.EscapeDataString(q));
                    int przed = r.Count;
                    foreach (var x in JsonDocument.Parse(js).RootElement.GetProperty("search").EnumerateArray())
                    {
                        var d = WdStr(x, "description") ?? ""; var id = WdStr(x, "id");
                        if (id == null || r.Any(f => f.WdId == id) || d.IndexOf("film", StringComparison.OrdinalIgnoreCase) < 0 && d.IndexOf("serial", StringComparison.OrdinalIgnoreCase) < 0 && d.IndexOf("series", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        r.Add(new FilmWynik { WdId = id, Label = WdStr(x, "label") ?? id, Desc = d });
                    }
                    if (r.Count > przed) break;
                }
            if (Wariant.Serwer) r = r.Where(f => f.WdId != null).Concat(r.Where(f => f.WdId == null)).ToList();   // 🎞 serwer: własne źródła (Wikidane) przed Jellyfin
            if (y > 0) r = r.OrderByDescending(f => f.Year == y || f.Year == 0 && (f.Desc ?? "").Contains(rok)).ToList();
            return r;
        }

        // wybrany wynik -> informacje: z TMDb tytuł, rok, opis, plakat; obsada, reżyseria, gatunek z Wikidanych (po numerze IMDb / TMDb)
        async Task<MovieInfo> PobierzFilm(FilmWynik w, string path, MovieInfo cur)
        {
            if (w.WdId != null) { var mw = await WikiFilm(w.WdId, path, cur); if (mw.People == null || mw.People.Count == 0) mw.People = await ObsadaWiki(w.WdId, mw.Name ?? w.Label, mw.Year); return mw; }
            string wd = null;
            try
            {
                using (var http = WikiHttp())
                    foreach (var (prop, val) in new[] { ("P345", w.Imdb), ("P4947", w.Tmdb) })
                    {
                        if (string.IsNullOrEmpty(val)) continue;
                        var js = await http.GetStringAsync("https://www.wikidata.org/w/api.php?action=query&format=json&list=search&srlimit=1&srsearch=" + Uri.EscapeDataString("haswbstatement:" + prop + "=" + val));
                        var hits = JsonDocument.Parse(js).RootElement.GetProperty("query").GetProperty("search");
                        if (hits.GetArrayLength() > 0) { wd = WdStr(hits[0], "title"); break; }
                    }
            }
            catch (Exception ex) { App.LogInfo("Wikidane po numerze filmu: " + ex.Message); }
            MovieInfo m = null;
            if (wd != null) try { m = await WikiFilm(wd, path, cur); } catch (Exception ex) { App.LogInfo("Wikidane – „" + w.Label + "”: " + ex.Message); }
            m ??= new MovieInfo { Rating = cur?.Rating ?? 0, Genres = cur?.Genres ?? new List<string>(), People = cur?.People ?? new List<string>(), Director = cur?.Director };
            m.Name = w.Label ?? m.Name; if (w.Year > 0) m.Year = w.Year;
            if (!string.IsNullOrWhiteSpace(w.Desc)) m.Overview = w.Desc;   // opis z TMDb (po polsku, gdy Jellyfin ma język polski)
            if (string.IsNullOrWhiteSpace(m.Overview)) m.Overview = " ";
            if ((m.People == null || m.People.Count == 0) && Wariant.Serwer) m.People = await ObsadaWiki(wd, m.Original ?? w.Label, m.Year);   // 🎞 serwer: najpierw własne źródło (Wikipedia), Jellyfin dopiero potem
            if ((m.People == null || m.People.Count == 0) && (!Wariant.Serwer || SerwerJfInfo))
            {   // 🎭 obsada z TMDb przez Jellyfin (film o tym samym numerze TMDb / IMDb w jego bibliotece) - na serwerze tylko za zgodą
                var jf = await JellyfinFilm(w.Tmdb, w.Imdb, w.Label, w.Year);
                if (jf != null) { if (jf.People.Count > 0) m.People = jf.People; if (string.IsNullOrEmpty(m.Director)) m.Director = jf.Director; if (m.Genres == null || m.Genres.Count == 0) m.Genres = jf.Genres; if (m.Rating == 0) m.Rating = jf.Rating; }
            }
            if (m.People == null || m.People.Count == 0) m.People = await ObsadaWiki(wd, m.Original ?? w.Label, m.Year);   // 🎭 Wikidane bez obsady (nowe filmy) - z tabelki artykułu Wikipedii
            if (w.Img != null)
                try { using (var http = WikiHttp()) { var b = await http.GetByteArrayAsync(w.Img); Directory.CreateDirectory(PosterDir); var f = "reczny-" + Math.Abs(MetaKey(path).GetHashCode()) + ".jpg"; File.WriteAllBytes(Path.Combine(PosterDir, f), b); m.Poster = f; } }
                catch (Exception ex) { App.LogInfo("Plakat (TMDb): " + ex.Message); }
            return m;
        }

        internal static bool SerwerJfInfo => GetPref("serwer-jf-info") == "1";
        // 🎭 film z biblioteki Jellyfin: po numerze TMDb / IMDb (pewne), a bez numeru - po tytule i roku; obsada, reżyseria, gatunek, ocena
        internal async Task<MovieInfo> JellyfinFilm(string tmdb, string imdb, string nazwa, int rok)
        {
            var c = LoadJellyfinConf(); if (c == null || string.IsNullOrEmpty(c.Url) || string.IsNullOrEmpty(c.Key)) return null;
            string N(string x) => new string((x ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            try
            {
                var url = await JellyfinReach(c) ?? c.Url;
                using (var http = JellyfinClient(new JellyfinConf { Url = url, Key = c.Key }))
                {
                    const string f = "Fields=People,Genres,ProviderIds,OriginalTitle&IncludeItemTypes=Movie&Recursive=true&Limit=20";
                    var zap = new List<string>();
                    if (tmdb != null) zap.Add("Items?" + f + "&AnyProviderIdEquals=" + Uri.EscapeDataString("Tmdb." + tmdb));
                    if (imdb != null) zap.Add("Items?" + f + "&AnyProviderIdEquals=" + Uri.EscapeDataString("Imdb." + imdb));
                    if (!string.IsNullOrWhiteSpace(nazwa)) zap.Add("Items?" + f + "&SearchTerm=" + Uri.EscapeDataString(nazwa) + (rok > 0 ? "&Years=" + rok : ""));
                    foreach (var q in zap)
                    {
                        var resp = await http.GetAsync(q); if (!resp.IsSuccessStatusCode) continue;
                        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
                        if (!doc.TryGetProperty("Items", out var items)) continue;
                        foreach (var it in items.EnumerateArray())
                        {
                            var pid = it.TryGetProperty("ProviderIds", out var p) ? p : default; string P(string k) => pid.ValueKind == JsonValueKind.Object ? WdStr(pid, k) : null;
                            var m = ParseJellyfinItem(it);
                            bool zgodny = tmdb != null && P("Tmdb") == tmdb || imdb != null && P("Imdb") == imdb
                                || tmdb == null && imdb == null && (rok == 0 || m.Year == rok) && (N(m.Name) == N(nazwa) || N(m.Original) == N(nazwa));
                            if (zgodny) return m;
                        }
                    }
                }
            }
            catch (Exception ex) { App.LogInfo("Obsada z Jellyfin – „" + nazwa + "”: " + ex.Message); }
            return null;
        }

        // 🎭 zdjęcie aktora: Wikidane (osoba z zawodem aktor) → obrazek z Wikimedia Commons (240 px), zapisany raz w folderze informacji („aktorzy”),
        //    brak zdjęcia - znacznik .brak, ponowna próba po 30 dniach. Bez Jellyfin i bez klucza.
        static readonly System.Threading.SemaphoreSlim _aktSem = new System.Threading.SemaphoreSlim(2);
        static readonly string[] AktorZawody = { "Q33999", "Q10800557", "Q2405480", "Q10798782", "Q2259451", "Q4610556", "Q11481802" };   // aktor, filmowy, głosowy, telewizyjny, teatralny, model, dubbingowy
        internal static async Task<string> AktorZdjecie(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var dir = Path.Combine(MetaDir, "aktorzy");
            string h; using (var sha = System.Security.Cryptography.SHA1.Create()) h = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(name.Trim().ToLowerInvariant()))).Substring(0, 16);
            var f = Path.Combine(dir, h + ".jpg"); var brak = Path.Combine(dir, h + ".brak");
            if (File.Exists(f)) return f;
            if (File.Exists(brak) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(brak)).TotalDays < 30) return null;
            await _aktSem.WaitAsync();
            try
            {
                if (File.Exists(f)) return f;
                Directory.CreateDirectory(dir);
                using (var http = WikiHttp())
                {
                    var js = await http.GetStringAsync("https://www.wikidata.org/w/api.php?action=wbsearchentities&format=json&type=item&limit=7&language=en&search=" + Uri.EscapeDataString(name.Trim()));
                    var ids = JsonDocument.Parse(js).RootElement.GetProperty("search").EnumerateArray().Select(x => WdStr(x, "id")).Where(x => x != null).ToList();
                    string plik = null;
                    if (ids.Count > 0)
                    {
                        var ent = JsonDocument.Parse(await http.GetStringAsync("https://www.wikidata.org/w/api.php?action=wbgetentities&format=json&props=claims&ids=" + string.Join("|", ids))).RootElement.GetProperty("entities");
                        foreach (var id in ids)
                        {
                            if (!ent.TryGetProperty(id, out var e) || !e.TryGetProperty("claims", out var cl) || !WdIds(cl, "P31").Contains("Q5")) continue;   // tylko człowiek
                            var img = cl.TryGetProperty("P18", out var p18) && p18.GetArrayLength() > 0 ? WdStr(p18[0], "mainsnak", "datavalue", "value") : null;
                            if (img == null) continue;
                            bool aktor = WdIds(cl, "P106").Any(o => AktorZawody.Contains(o));
                            if (aktor) { plik = img; break; } plik ??= img;
                        }
                    }
                    if (plik != null)
                    {
                        var b = await http.GetByteArrayAsync("https://commons.wikimedia.org/wiki/Special:FilePath/" + Uri.EscapeDataString(plik) + "?width=240");
                        if (b.Length > 500) { File.WriteAllBytes(f, b); return f; }
                    }
                }
                File.WriteAllText(brak, "");
            }
            catch (Exception ex) { App.LogInfo("Zdjęcie aktora „" + name + "”: " + ex.Message); }
            finally { _aktSem.Release(); }
            return null;
        }

        // 🎭 obsada z tabelki (infobox) artykułu Wikipedii: „starring” (angielska), „obsada” / „występują” (polska) - nowe filmy często mają ją tam, a w Wikidanych jeszcze nie
        internal static async Task<List<string>> ObsadaWiki(string wd, string tytul, int rok)
        {
            var wynik = new List<string>();
            try
            {
                using (var http = WikiHttp())
                {
                    var art = new List<(string Lang, string Title)>();
                    if (wd != null)
                    {
                        var e = JsonDocument.Parse(await http.GetStringAsync("https://www.wikidata.org/w/api.php?action=wbgetentities&format=json&props=sitelinks&ids=" + wd)).RootElement.GetProperty("entities").GetProperty(wd);
                        foreach (var lang in new[] { "en", "pl" }) { var t = WdStr(e, "sitelinks", lang + "wiki", "title"); if (t != null) art.Add((lang, t)); }
                    }
                    if (art.Count == 0 && !string.IsNullOrWhiteSpace(tytul))
                        foreach (var lang in new[] { "en", "pl" })
                        {
                            var js = await http.GetStringAsync("https://" + lang + ".wikipedia.org/w/api.php?action=query&format=json&list=search&srlimit=1&srsearch=" + Uri.EscapeDataString(tytul + (rok > 0 ? " " + rok : "") + " film"));
                            var hits = JsonDocument.Parse(js).RootElement.GetProperty("query").GetProperty("search");
                            if (hits.GetArrayLength() > 0) art.Add((lang, WdStr(hits[0], "title")));
                        }
                    foreach (var (lang, title) in art)
                    {
                        var js = await http.GetStringAsync("https://" + lang + ".wikipedia.org/w/api.php?action=parse&format=json&prop=wikitext&section=0&redirects=1&page=" + Uri.EscapeDataString(title));
                        var txt = WdStr(JsonDocument.Parse(js).RootElement, "parse", "wikitext", "*") ?? "";
                        var pole = System.Text.RegularExpressions.Regex.Match(txt, @"\|\s*(?:starring|obsada|występują|główne role)\s*=(.*?)(?=\n\s*\|\s*[\w ]+=|\n\}\})", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (!pole.Success) continue;
                        var v = System.Text.RegularExpressions.Regex.Replace(pole.Groups[1].Value, @"<ref[^>]*/>|<ref.*?</ref>|<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);
                        v = System.Text.RegularExpressions.Regex.Replace(v, @"\[\[(?:[^|\]]*\|)?([^\]]+)\]\]", "$1");   // [[Osoba|Nazwa]] → Nazwa
                        v = System.Text.RegularExpressions.Regex.Replace(v, @"\{\{(?:plainlist|plain list|ubl|unbulleted list|flatlist)\s*\|?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Replace("}}", "").Replace("{{", "");
                        foreach (var cz in System.Text.RegularExpressions.Regex.Split(v, @"<br\s*/?>|\n|\||\*|,"))
                        {
                            var n = cz.Trim().Trim('\'', ' ');
                            if (n.Length > 2 && n.Length < 60 && !n.Contains("=") && char.IsLetter(n[0]) && !wynik.Contains(n)) wynik.Add(n);
                        }
                        if (wynik.Count > 0) break;
                    }
                }
            }
            catch (Exception ex) { App.LogInfo("Obsada z Wikipedii – „" + tytul + "”: " + ex.Message); }
            return wynik.Take(12).ToList();
        }

        async Task<MovieInfo> WikiFilm(string id, string path, MovieInfo cur)
        {
            using (var http = WikiHttp())
            {
                var e = JsonDocument.Parse(await http.GetStringAsync("https://www.wikidata.org/w/api.php?action=wbgetentities&format=json&props=labels|descriptions|claims|sitelinks&languages=pl|en&ids=" + id)).RootElement.GetProperty("entities").GetProperty(id);
                var opisWd = WdStr(e, "descriptions", "pl", "value") ?? WdStr(e, "descriptions", "en", "value");
                var claims = e.GetProperty("claims");
                string Label(JsonElement x) => WdStr(x, "labels", "pl", "value") ?? WdStr(x, "labels", "en", "value");
                var dirs = WdIds(claims, "P57"); var cast = WdIds(claims, "P161").Take(12).ToList(); var genres = WdIds(claims, "P136").Take(4).ToList();
                var names = new Dictionary<string, string>();
                var ids = dirs.Concat(cast).Concat(genres).Distinct().ToList();
                for (int k = 0; k < ids.Count; k += 50)
                {
                    var js = await http.GetStringAsync("https://www.wikidata.org/w/api.php?action=wbgetentities&format=json&props=labels&languages=pl|en&ids=" + string.Join("|", ids.Skip(k).Take(50)));
                    foreach (var p in JsonDocument.Parse(js).RootElement.GetProperty("entities").EnumerateObject()) names[p.Name] = Label(p.Value);
                }
                List<string> N(List<string> l) => l.Select(x => names.TryGetValue(x, out var n) ? n : null).Where(x => !string.IsNullOrEmpty(x)).ToList();
                int year = 0;
                if (claims.TryGetProperty("P577", out var pd)) foreach (var c in pd.EnumerateArray()) { var t = WdStr(c, "mainsnak", "datavalue", "value", "time"); if (t != null && t.Length > 5 && int.TryParse(t.Substring(1, 4), out var y) && (year == 0 || y < year)) year = y; }
                var orig = claims.TryGetProperty("P1476", out var ot) ? WdStr(ot[0], "mainsnak", "datavalue", "value", "text") : null;
                var m = new MovieInfo { Name = Label(e) ?? cur?.Name, Original = orig, Year = year, Director = string.Join(", ", N(dirs)), People = N(cast), Genres = N(genres), Rating = cur?.Rating ?? 0 };
                // opis i plakat z artykułu: polska Wikipedia, zapasowo angielska (obrazek artykułu o filmie to zwykle plakat); podstawowe API Wikipedii
                string poster = null;
                foreach (var (site, host) in new[] { ("plwiki", "pl"), ("enwiki", "en") })
                {
                    var title = WdStr(e, "sitelinks", site, "title"); if (title == null) continue;
                    try
                    {
                        var q = JsonDocument.Parse(await http.GetStringAsync("https://" + host + ".wikipedia.org/w/api.php?action=query&format=json&prop=extracts|pageimages&exintro=1&explaintext=1&piprop=original&redirects=1&titles=" + Uri.EscapeDataString(title))).RootElement;
                        foreach (var pg in q.GetProperty("query").GetProperty("pages").EnumerateObject())
                        {
                            var ex = WdStr(pg.Value, "extract");
                            if (string.IsNullOrWhiteSpace(m.Overview) && !string.IsNullOrWhiteSpace(ex)) m.Overview = ex.Trim();
                            poster ??= WdStr(pg.Value, "original", "source");
                        }
                    }
                    catch (Exception ex) { App.LogInfo("Wikipedia (" + host + ") – „" + title + "”: " + ex.Message); }
                    if (!string.IsNullOrWhiteSpace(m.Overview) && poster != null) break;
                }
                if (string.IsNullOrWhiteSpace(m.Overview)) m.Overview = opisWd;   // bez artykułu - krótki opis z Wikidanych
                if (string.IsNullOrWhiteSpace(m.Overview)) m.Overview = " ";   // film uznany za opisany
                m.Poster = cur?.Poster;
                if (poster != null)
                    try { var b = await http.GetByteArrayAsync(poster); Directory.CreateDirectory(PosterDir); var f = "reczny-" + Math.Abs(MetaKey(path).GetHashCode()) + ".jpg"; File.WriteAllBytes(Path.Combine(PosterDir, f), b); m.Poster = f; }
                    catch (Exception) { }
                return m;
            }
        }
    }
}
