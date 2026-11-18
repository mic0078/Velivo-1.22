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
        static HttpClient WikiHttp()
        {
            var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
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
                    Meta[key] = m; win.Close(); done?.Invoke();
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
            var c = LoadJellyfinConf();
            if (c != null && !string.IsNullOrEmpty(c.Url) && !string.IsNullOrEmpty(c.Key))
                try
                {
                    var url = await JellyfinReach(c) ?? c.Url;
                    using (var http = JellyfinClient(new JellyfinConf { Url = url, Key = c.Key }))
                    {
                        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["SearchInfo"] = y > 0 ? (object)new { Name = q, Year = y } : new { Name = q } });
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
            if (y > 0) r = r.OrderByDescending(f => f.Year == y || f.Year == 0 && (f.Desc ?? "").Contains(rok)).ToList();
            return r;
        }

        // wybrany wynik -> informacje: z TMDb tytuł, rok, opis, plakat; obsada, reżyseria, gatunek z Wikidanych (po numerze IMDb / TMDb)
        async Task<MovieInfo> PobierzFilm(FilmWynik w, string path, MovieInfo cur)
        {
            if (w.WdId != null) return await WikiFilm(w.WdId, path, cur);
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
            if (w.Img != null)
                try { using (var http = WikiHttp()) { var b = await http.GetByteArrayAsync(w.Img); Directory.CreateDirectory(PosterDir); var f = "reczny-" + Math.Abs(MetaKey(path).GetHashCode()) + ".jpg"; File.WriteAllBytes(Path.Combine(PosterDir, f), b); m.Poster = f; } }
                catch (Exception ex) { App.LogInfo("Plakat (TMDb): " + ex.Message); }
            return m;
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
