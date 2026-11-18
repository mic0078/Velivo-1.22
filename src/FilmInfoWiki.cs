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
            var found = new List<(string Id, string Label, string Desc)>();
            async Task Szukaj()
            {
                status.Text = L.T("Szukam…"); list.Items.Clear(); found.Clear();
                try
                {
                    using (var http = WikiHttp())
                        foreach (var lang in new[] { "pl", "en" })
                        {
                            var js = await http.GetStringAsync("https://www.wikidata.org/w/api.php?action=wbsearchentities&format=json&type=item&limit=20&language=" + lang + "&uselang=pl&search=" + Uri.EscapeDataString(qName.Text.Trim()));
                            foreach (var r in JsonDocument.Parse(js).RootElement.GetProperty("search").EnumerateArray())
                            {
                                var d = WdStr(r, "description") ?? ""; var id = WdStr(r, "id");
                                if (id == null || found.Any(f => f.Id == id) || d.IndexOf("film", StringComparison.OrdinalIgnoreCase) < 0 && d.IndexOf("serial", StringComparison.OrdinalIgnoreCase) < 0 && d.IndexOf("series", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                found.Add((id, WdStr(r, "label") ?? id, d));
                            }
                            if (found.Count > 0) break;
                        }
                }
                catch (Exception ex) { status.Text = L.T("⚠ Błąd wyszukiwania: ") + ex.Message; return; }
                var y = qYear.Text.Trim();
                if (y.Length == 4) found = found.OrderByDescending(f => f.Desc.Contains(y)).ToList();   // rok w opisie („film z 2022 roku”) - wyżej
                foreach (var f in found)
                {
                    var sp = new StackPanel { Margin = new Thickness(2, 4, 2, 4) };
                    sp.Children.Add(new TextBlock { Text = f.Label, FontWeight = FontWeights.SemiBold, FontSize = 14 });
                    sp.Children.Add(new TextBlock { Text = f.Desc, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
                    list.Items.Add(new ListBoxItem { Content = sp });
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
                    var m = await WikiFilm(found[i].Id, path, LookupMeta(Meta, path));
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
