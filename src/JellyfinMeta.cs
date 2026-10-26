using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // Informacje o filmach z serwera Jellyfin (w sieci domowej): tytul, rok, opis, ocena, gatunki, obsada, plakat.
    // Kopia we wlasnej bazie Velivo (filmy-info\) - dziala tez przy wylaczonym serwerze; uzupelniana przy kazdym polaczeniu.
    // Dopasowanie do biblioteki po nazwie pliku (sciezki na obu komputerach moga byc rozne). Klucz API zaszyfrowany (DPAPI).
    public partial class MainWindow
    {
        internal sealed class MovieInfo
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Original { get; set; }
            public int Year { get; set; }
            public string Overview { get; set; }
            public double Rating { get; set; }
            public string Age { get; set; }
            public List<string> Genres { get; set; } = new List<string>();
            public List<string> People { get; set; } = new List<string>();
            public string Director { get; set; }
            public string ImageTag { get; set; }
            public string Poster { get; set; }   // plik plakatu w bazie (nazwa)
        }

        sealed class JellyfinConf
        {
            public string Url { get; set; }
            public string Key { get; set; }
            public long Synced { get; set; }
            public int AutoHours { get; set; }   // samo odswiezanie biblioteki z Jellyfin co tyle godzin (0 = wylaczone)
        }

        static string MetaDir { get { return Path.Combine(DataDir, "filmy-info"); } }
        static string MetaFile { get { return Path.Combine(MetaDir, "baza.json"); } }
        static string JellyfinFilesFile { get { return Path.Combine(MetaDir, "pliki.txt"); } }
        // filmy, ktore zniknely z Jellyfin od ostatniego pobrania - czekaja na decyzje uzytkownika (usunac z biblioteki Velivo?)
        static string JellyfinRemovedFile { get { return Path.Combine(MetaDir, "usuniete-z-jellyfin.txt"); } }

        // nowe filmy z Jellyfin, ktorych plik jest dostepny na tym komputerze, a nie ma ich w bibliotece (ten sam plik z dwoch bibliotek Jellyfin - raz)
        // 🎞 filmy usunięte w Velivo „tylko z biblioteki” - nie wracają przy 🔄 Odśwież (pliki dalej na serwerze Jellyfin)
        static string JellyfinSkipFile { get { return Path.Combine(MetaDir, "pominiete.txt"); } }
        static IEnumerable<string> JellyfinSkipped() { try { return File.Exists(JellyfinSkipFile) ? File.ReadAllLines(JellyfinSkipFile) : Array.Empty<string>(); } catch (Exception) { return Array.Empty<string>(); } }
        static void JellyfinSkipAdd(IEnumerable<string> paths)
        {
            try { Directory.CreateDirectory(MetaDir); File.WriteAllLines(JellyfinSkipFile, JellyfinSkipped().Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase)); } catch (Exception ex) { App.LogError(ex); }
        }
        // usunięcie filmów na serwerze Jellyfin (DELETE /Items/{id}; klucz API administratora) - wynik do dymka
        static async Task<string> JellyfinDeleteAsync(JellyfinConf c, List<string> ids)
        {
            int ok = 0; string err = null;
            try
            {
                using var http = JellyfinClient(c);
                foreach (var id in ids)
                {
                    var r = await http.DeleteAsync("Items/" + Uri.EscapeDataString(id));
                    if (r.IsSuccessStatusCode) ok++; else err ??= (int)r.StatusCode + " " + r.ReasonPhrase;
                }
            }
            catch (Exception ex) { err ??= ex.Message; }
            return "🎞 " + L.T("Usunięto z Jellyfin: ") + ok + "/" + ids.Count + (err != null ? " – " + L.T("serwer odpowiedział: ") + err + (err.StartsWith("401") || err.StartsWith("403") ? L.T(" (klucz API bez prawa usuwania)") : "") : "");
        }

        internal static List<string> NewJellyfinFiles(List<string> jellyfinPaths, IEnumerable<string> libraryPaths, Func<string, bool> exists)
        {
            var have = new HashSet<string>(libraryPaths.Select(MetaKey));
            var res = new List<string>();
            foreach (var p in jellyfinPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var k = MetaKey(p);
                if (have.Contains(k) || !IsVlcVideoFile(p) || !exists(p)) continue;
                have.Add(k); res.Add(p);
            }
            return res;
        }

        // informacje o filmach wpisane recznie (gdy Jellyfin ich nie zna) - osobny plik, pobieranie z Jellyfin ich nie nadpisuje
        static string ManualFile { get { return Path.Combine(MetaDir, "reczne.json"); } }
        static Dictionary<string, MovieInfo> LoadManual()
        {
            try { if (File.Exists(ManualFile)) return JsonSerializer.Deserialize<Dictionary<string, MovieInfo>>(File.ReadAllText(ManualFile)) ?? new Dictionary<string, MovieInfo>(); } catch (Exception ex) { App.LogError(ex); }
            return new Dictionary<string, MovieInfo>();
        }

        // okno: tytul, rok, ocena, gatunek, rezyser, obsada, opis, plakat (z pliku) - dla filmu bez danych albo z blednymi
        void ShowMetaEditor(Window owner, string path, string fileTitle, Action done)
        {
            var key = MetaKey(path);
            var cur = LookupMeta(Meta, path);
            var win = new Window { Title = L.T("📝 Informacje o filmie"), Width = 640, Height = 600, Owner = owner, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            Independent(win);   // zwykle okno Windows - nie przykrywa innych na stale
            var grid = new Grid { Margin = new Thickness(12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var poster = new Image { Width = 170, Height = 250, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
            string posterFile = cur?.Poster, posterNew = null;
            Action showPoster = () => { var f = posterNew ?? (posterFile != null ? Path.Combine(PosterDir, posterFile) : null); poster.Source = f != null && File.Exists(f) ? LoadLogo(f, 500) : null; };
            showPoster();
            var left = new StackPanel(); left.Children.Add(poster);
            left.Children.Add(SmallButton(L.T("🖼 Wybierz plakat…"), () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Obrazy") + "|*.jpg;*.jpeg;*.png;*.webp;*.bmp" };
                if (dlg.ShowDialog(win) == true) { posterNew = dlg.FileName; showPoster(); }
            }));
            Grid.SetColumn(left, 0); grid.Children.Add(left);
            var form = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            Func<string, string, bool, TextBox> field = (label, val, multi) =>
            {
                form.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
                var tb = new TextBox { Text = val ?? "", Padding = new Thickness(4), AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, Height = multi ? 170 : double.NaN, VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
                form.Children.Add(tb); return tb;
            };
            var tTitle = field(L.T("Tytuł"), cur?.Name ?? fileTitle, false);
            var tOrig = field(L.T("Tytuł oryginalny"), cur?.Original, false);
            var tYear = field(L.T("Rok"), cur?.Year > 0 ? cur.Year.ToString() : "", false);
            var tRating = field(L.T("Ocena (0–10)"), cur?.Rating > 0 ? cur.Rating.ToString("0.0") : "", false);
            var tGenre = field(L.T("Gatunek (po przecinku)"), cur != null ? string.Join(", ", cur.Genres ?? new List<string>()) : "", false);
            var tDir = field(L.T("Reżyseria"), cur?.Director, false);
            var tPeople = field(L.T("Obsada (po przecinku)"), cur != null ? string.Join(", ", cur.People ?? new List<string>()) : "", false);
            var tOver = field(L.T("Opis"), cur?.Overview, true);
            var sv = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(sv, 1); grid.Children.Add(sv);
            var ok = SmallButton(L.T("💾 Zapisz"), () =>
            {
                Func<string, List<string>> list = t => t.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                var m = new MovieInfo { Id = cur?.Id, ImageTag = cur?.ImageTag, Age = cur?.Age, Name = tTitle.Text.Trim(), Original = tOrig.Text.Trim(), Overview = tOver.Text.Trim(), Director = tDir.Text.Trim(), Genres = list(tGenre.Text), People = list(tPeople.Text), Poster = posterFile };
                int.TryParse(tYear.Text.Trim(), out var y); m.Year = y;
                double.TryParse(tRating.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r); m.Rating = Math.Max(0, Math.Min(10, r));
                if (string.IsNullOrEmpty(m.Overview) && m.Rating <= 0) m.Overview = " ";   // film uznany za opisany (tytul z tego okna)
                if (posterNew != null)
                    try { Directory.CreateDirectory(PosterDir); var f = "reczny-" + Math.Abs(key.GetHashCode()) + Path.GetExtension(posterNew).ToLowerInvariant(); File.Copy(posterNew, Path.Combine(PosterDir, f), true); m.Poster = f; }
                    catch (Exception ex) { InfoBox(win, L.T("Nie udało się skopiować plakatu: ") + ex.Message); return; }
                var man = LoadManual(); man[key] = m;
                Directory.CreateDirectory(MetaDir); File.WriteAllText(ManualFile, JsonSerializer.Serialize(man));
                Meta[key] = m; win.Close(); done?.Invoke();
            });
            var del = SmallButton(L.T("↺ Przywróć dane z Jellyfin"), () =>
            {
                var man = LoadManual(); if (!man.Remove(key)) { win.Close(); return; }
                File.WriteAllText(ManualFile, JsonSerializer.Serialize(man)); _meta = null; win.Close(); done?.Invoke();   // baza wczytana od nowa - bez recznych danych tego filmu
            });
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12) };
            bar.Children.Add(del); bar.Children.Add(ok);
            var dock = new DockPanel(); DockPanel.SetDock(bar, Dock.Bottom); dock.Children.Add(bar); dock.Children.Add(grid);
            win.Content = dock;
            win.Show();
        }

        // wyszukanie filmu w sieci przez Jellyfin (jego dostawcy: TMDb, OMDb) i przypisanie wybranego wyniku - poprawia tez Jellyfin
        void ShowMetaSearch(Window owner, string path, string fileTitle, Action done)
        {
            var c = LoadJellyfinConf();
            var cur = LookupMeta(Meta, path);
            if (c == null || string.IsNullOrEmpty(c.Url) || cur?.Id == null)
            {
                // film nieznany Jellyfin - wyszukiwarka w nowej karcie (Filmweb) i reczne uzupelnienie
                ShowWebWindow(owner, "https://www.filmweb.pl/search#/?query=" + Uri.EscapeDataString(fileTitle));
                InfoBox(owner, L.T("Tego filmu nie ma w Jellyfin (albo Jellyfin nie jest ustawiony), więc otworzyłem wyszukiwanie w Filmweb.\n\nInformacje możesz wpisać w „📝 Uzupełnij informacje…”."));
                return;
            }
            var win = new Window { Title = L.T("🔎 Wyszukaj informacje o filmie"), Width = 720, Height = 560, Owner = owner, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            Independent(win);   // zwykle okno Windows - nie przykrywa innych na stale
            var ym = System.Text.RegularExpressions.Regex.Match(fileTitle ?? "", @"^(.+) \((\d{4})\)$");
            var qName = new TextBox { Text = ym.Success ? ym.Groups[1].Value : fileTitle, Padding = new Thickness(4), Width = 380 };
            var qYear = new TextBox { Text = ym.Success ? ym.Groups[2].Value : "", Padding = new Thickness(4), Width = 70, Margin = new Thickness(6, 0, 6, 0) };
            var status = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(12, 4, 12, 4) };
            var list = new ListBox { Margin = new Thickness(12, 0, 12, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            var results = new List<JsonElement>();
            Func<System.Threading.Tasks.Task> search = async () =>
            {
                status.Text = L.T("Szukam…"); list.Items.Clear(); results.Clear();
                try
                {
                    using (var http = JellyfinClient(c))
                    {
                        int.TryParse(qYear.Text.Trim(), out var y);
                        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["SearchInfo"] = y > 0 ? (object)new { Name = qName.Text.Trim(), Year = y } : new { Name = qName.Text.Trim() }, ["ItemId"] = cur.Id });
                        var resp = await Ok(http.PostAsync("Items/RemoteSearch/Movie", new StringContent(body, Encoding.UTF8, "application/json")));
                        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                        foreach (var r in doc.RootElement.EnumerateArray()) results.Add(r.Clone());
                    }
                }
                catch (Exception ex) { status.Text = L.T("⚠ Błąd wyszukiwania: ") + ex.Message; return; }
                foreach (var r in results)
                {
                    string S(string n) { return r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null; }
                    var year = r.TryGetProperty("ProductionYear", out var py) && py.ValueKind == JsonValueKind.Number ? " (" + py.GetInt32() + ")" : "";
                    var img = new Image { Width = 60, Height = 90, Stretch = Stretch.Uniform, Margin = new Thickness(0, 2, 10, 2) };
                    var url = S("ImageUrl");
                    if (url != null) try { img.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(url)); } catch (Exception) { }
                    var txt = new StackPanel();
                    txt.Children.Add(new TextBlock { Text = S("Name") + year, FontWeight = FontWeights.SemiBold, FontSize = 14 });
                    var ov = S("Overview"); if (ov != null) txt.Children.Add(new TextBlock { Text = ov.Length > 220 ? ov.Substring(0, 220) + "…" : ov, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
                    var row = new DockPanel(); DockPanel.SetDock(img, Dock.Left); row.Children.Add(img); row.Children.Add(txt);
                    list.Items.Add(new ListBoxItem { Content = row });
                }
                status.Text = results.Count == 0 ? L.T("Nic nie znaleziono – zmień tytuł albo rok i szukaj ponownie.") : L.T("Wybierz właściwy film i kliknij „Zastosuj”.");
            };
            var apply = SmallButton(L.T("✅ Zastosuj"), async () =>
            {
                int i = list.SelectedIndex; if (i < 0 || i >= results.Count) return;
                status.Text = L.T("Przypisuję w Jellyfin i pobieram dane…");
                try
                {
                    using (var http = JellyfinClient(c))
                    {
                        var before = cur.ImageTag + "|" + cur.Overview + "|" + cur.Year;
                        await Ok(http.PostAsync("Items/RemoteSearch/Apply/" + cur.Id + "?ReplaceAllImages=true", new StringContent(results[i].GetRawText(), Encoding.UTF8, "application/json")));
                        // Jellyfin aktualizuje film w tle - pobieramy tylko ten film, az dane sie zmienia (najwyzej ok. 45 s)
                        MovieInfo m = null;
                        for (int t = 0; t < 15; t++)
                        {
                            await System.Threading.Tasks.Task.Delay(3000);
                            var js = await (await Ok(http.GetAsync("Items?Ids=" + cur.Id + "&Fields=Path,Overview,Genres,People,OriginalTitle,OfficialRating"))).Content.ReadAsStringAsync();
                            var items = JsonDocument.Parse(js).RootElement.GetProperty("Items");
                            if (items.GetArrayLength() == 0) break;
                            m = ParseJellyfinItem(items[0]);
                            if (m.ImageTag + "|" + m.Overview + "|" + m.Year != before && !string.IsNullOrEmpty(m.Overview)) break;
                        }
                        if (m == null) { status.Text = L.T("⚠ Jellyfin nie zwrócił filmu – spróbuj „🔄 Odśwież” w bibliotece za chwilę."); return; }
                        if (m.ImageTag != null)
                            try { var img = await http.GetByteArrayAsync("Items/" + m.Id + "/Images/Primary?maxHeight=720&quality=90"); Directory.CreateDirectory(PosterDir); var f = m.Id + "-720.jpg"; File.WriteAllBytes(Path.Combine(PosterDir, f), img); m.Poster = f; }
                            catch (Exception) { m.Poster = cur.Poster; }
                        var key = MetaKey(path);
                        var meta = new Dictionary<string, MovieInfo>(Meta, StringComparer.OrdinalIgnoreCase);
                        foreach (var k in meta.Where(kv => kv.Value.Id == cur.Id || kv.Value == cur).Select(kv => kv.Key).ToList()) meta[k] = m;   // wszystkie klucze tego filmu
                        meta[key] = m;
                        Directory.CreateDirectory(MetaDir);
                        File.WriteAllText(MetaFile, JsonSerializer.Serialize(meta.Where(kv => !kv.Key.StartsWith("ty:") && !kv.Key.StartsWith("t:")).ToDictionary(kv => kv.Key, kv => kv.Value)));
                        _meta = null;   // wczytana od nowa (z kluczami tytulow i recznymi danymi)
                    }
                    done?.Invoke(); win.Close();
                }
                catch (Exception ex) { status.Text = L.T("⚠ Nie udało się: ") + ex.Message; }
            });
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 12, 12, 4) };
            top.Children.Add(qName); top.Children.Add(qYear); top.Children.Add(SmallButton(L.T("🔎 Szukaj"), () => { _ = search(); }));
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
            bar.Children.Add(SmallButton(L.T("🌐 Szukaj w Filmweb"), () => ShowWebWindow(win, "https://www.filmweb.pl/search#/?query=" + Uri.EscapeDataString(qName.Text.Trim()))));
            bar.Children.Add(apply);
            var dock = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(status, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom);
            dock.Children.Add(top); dock.Children.Add(status); dock.Children.Add(bar); dock.Children.Add(list);
            win.Content = dock;
            list.MouseDoubleClick += (s, a) => apply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            win.Loaded += (s, a) => { _ = search(); };
            win.Show();
        }

        // strona w osobnym okienku na wierzchu (nad biblioteka i oknem wyszukiwania - karta w glownym oknie bylaby schowana)
        void ShowWebWindow(Window owner, string url)
        {
            var w = new Window { Title = "Filmweb", Width = 1000, Height = 760, Owner = owner, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var view = new Microsoft.Web.WebView2.Wpf.WebView2();
            w.Content = view;
            w.Loaded += async (s, a) => { try { await view.EnsureCoreWebView2Async(_env); view.CoreWebView2.Navigate(url); } catch (Exception ex) { App.LogError(ex); } };
            w.Closed += (s, a) => view.Dispose();
            w.Show();
        }

        static string PosterDir { get { return Path.Combine(MetaDir, "plakaty"); } }
        static string JellyfinConfFile { get { return Path.Combine(DataDir, "jellyfin.bin"); } }
        static readonly byte[] JellyfinEntropy = Encoding.UTF8.GetBytes("Velivo.Jellyfin");

        Dictionary<string, MovieInfo> _meta;
        internal Dictionary<string, MovieInfo> Meta
        {
            get
            {
                if (_meta != null) return _meta;
                try { _meta = File.Exists(MetaFile) ? JsonSerializer.Deserialize<Dictionary<string, MovieInfo>>(File.ReadAllText(MetaFile)) : null; } catch (Exception ex) { App.LogError(ex); }
                _meta = new Dictionary<string, MovieInfo>(_meta ?? new Dictionary<string, MovieInfo>(), StringComparer.OrdinalIgnoreCase);
                foreach (var kv in LoadManual()) _meta[kv.Key] = kv.Value;   // informacje wpisane recznie maja pierwszenstwo
                foreach (var m in _meta.Where(kv => !kv.Key.StartsWith("ty:") && !kv.Key.StartsWith("t:")).Select(kv => kv.Value).Distinct().ToList()) AddTitleKeys(_meta, m);   // tytul + rok od razu, bez ponownej synchronizacji
                return _meta;
            }
        }

        // klucz dopasowania: nazwa pliku bez rozszerzenia (male litery)
        internal static string MetaKey(string path)
        {
            var p = (path ?? "").Replace('/', '\\');
            int i = p.LastIndexOf('\\');
            var name = i >= 0 ? p.Substring(i + 1) : p;
            int dot = name.LastIndexOf('.');
            return (dot > 0 ? name.Substring(0, dot) : name).Normalize(NormalizationForm.FormC).ToLowerInvariant();   // polskie znaki zapisane na dwa sposoby (NAS) - jednakowo
        }

        // klucz folderu filmu - gdy Jellyfin podaje sciezke folderu zamiast pliku
        internal static string MetaDirKey(string path)
        {
            var p = (path ?? "").Replace('/', '\\').TrimEnd('\\');
            int i = p.LastIndexOf('\\');
            var dir = i > 0 ? p.Substring(0, i) : "";
            int j = dir.LastIndexOf('\\');
            return "dir:" + (j >= 0 ? dir.Substring(j + 1) : dir).Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }

        internal static MovieInfo LookupMeta(Dictionary<string, MovieInfo> meta, string path)
        {
            if (path == null) return null;
            if (meta.TryGetValue(MetaKey(path), out var m) || meta.TryGetValue(MetaDirKey(path), out m)) return m;
            var ct = CleanTitle(MetaKey(path));
            var c = System.Text.RegularExpressions.Regex.Match(ct, @"^(.+) \((\d{4})\)$");   // "alien (1979)"
            var k = c.Success ? TitleYearKey(c.Groups[1].Value, int.Parse(c.Groups[2].Value)) : TitleYearKey(ct, 0);   // bez roku w nazwie - sam tytul, gdy jednoznaczny
            return k != null && meta.TryGetValue(k, out m) && m != Ambiguous ? m : null;
        }

        internal MovieInfo MetaFor(string path) { return LookupMeta(Meta, path); }

        // sortowanie listy filmow: az / za / ocena / rok (rok nierozpoznanego filmu z nazwy pliku)
        internal static List<T> SortFilms<T>(IEnumerable<T> src, Func<T, string> raw, Func<T, MovieInfo> meta, string how, Func<T, string> custom = null)
        {
            var rows = src.Select(x => { var m = meta(x); var c = custom?.Invoke(x); var t = !string.IsNullOrWhiteSpace(c) ? c : MetaTitle(m) ?? CleanTitle(raw(x)); var y = System.Text.RegularExpressions.Regex.Match(t, @"\((\d{4})\)$");
                return (x, t, r: m?.Rating ?? 0, y: m?.Year > 0 ? m.Year : y.Success ? int.Parse(y.Groups[1].Value) : 0); }).ToList();
            var cmp = StringComparer.CurrentCultureIgnoreCase;
            var o = how == "za" ? rows.OrderByDescending(r => r.t, cmp)
                : how == "ocena" ? rows.OrderByDescending(r => r.r).ThenBy(r => r.t, cmp)
                : how == "rok" ? rows.OrderByDescending(r => r.y).ThenBy(r => r.t, cmp)
                : rows.OrderBy(r => r.t, cmp);
            return o.Select(r => r.x).ToList();
        }

        // sasiad na liscie (nastepny / poprzedni film); null - koniec listy albo nie ma na liscie
        internal static string Neighbor(List<string> ids, string cur, int d) { var i = ids.IndexOf(cur); return i < 0 || i + d < 0 || i + d >= ids.Count ? null : ids[i + d]; }

        // tytul z Jellyfin tylko dla rozpoznanego filmu (nierozpoznany ma za nazwe nazwe pliku - bez opisu i oceny)
        internal static string MetaTitle(MovieInfo m)
        {
            if (m == null || string.IsNullOrEmpty(m.Overview) && m.Rating <= 0) return null;
            // nazwa w Jellyfin jak nazwa pliku ([dopiski], jakosc, kodeki) - czytelnie, jak nierozpoznany plik
            if (System.Text.RegularExpressions.Regex.IsMatch(m.Name ?? "", @"\[|\b(\d{3,4}p|brrip|bdrip|web-?dl|webrip|x26[45]|h ?26[45]|ac3)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return CleanTitle(m.Name);
            return m.Name + (m.Year > 0 ? " (" + m.Year + ")" : "");
        }

        static int Richness(MovieInfo m) { return (string.IsNullOrEmpty(m.Overview) ? 0 : 4) + (m.Rating > 0 ? 2 : 0) + (m.Genres?.Count > 0 ? 1 : 0); }

        // klucz "tytul + rok": plik o innej nazwie niz w Jellyfin (np. inna wersja, kolekcja w jednym folderze)
        // bez polskich znakow (pasazer = pasażer) i znakow innych niz litery i cyfry; rok 0 = klucz samego tytulu
        static string TitleYearKey(string title, int year)
        {
            var d = (title ?? "").Replace('ł', 'l').Replace('Ł', 'L').Normalize(NormalizationForm.FormD).ToLowerInvariant();
            var t = new string(d.Where(ch => char.IsLetterOrDigit(ch) && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
            return t.Length == 0 ? null : year > 0 ? "ty:" + t + year : "t:" + t;
        }

        static readonly MovieInfo Ambiguous = new MovieInfo();   // ten sam tytul bez roku u kilku filmow - nie dopasowujemy

        internal static void AddTitleKeys(Dictionary<string, MovieInfo> meta, MovieInfo m)
        {
            if (m.Year <= 0 || string.IsNullOrEmpty(m.Overview) && m.Rating <= 0) return;   // tylko rozpoznane filmy
            foreach (var t in new[] { m.Name, m.Original })
            {
                var k = TitleYearKey(t, m.Year); if (k != null && !meta.ContainsKey(k)) meta[k] = m;
                var k0 = TitleYearKey(t, 0); if (k0 != null) meta[k0] = meta.TryGetValue(k0, out var o) && o != m ? Ambiguous : m;
            }
        }

        // czytelny tytul z nazwy pliku: bez [dopiskow], jakosci i kodekow, kropki jako spacje, rok w nawiasie
        internal static string CleanTitle(string raw)
        {
            var t = System.Text.RegularExpressions.Regex.Replace(raw ?? "", @"\[[^\]]*\]", " ");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"[._]", " ");
            string title, year = null;
            var m = System.Text.RegularExpressions.Regex.Match(t, @"^(.+?)[\s(\-]*(?<!\d)((19|20)\d{2})(?!\d)");
            if (m.Success) { title = m.Groups[1].Value; year = m.Groups[2].Value; }
            else title = System.Text.RegularExpressions.Regex.Replace(t, @"[\s\-]*\b(pl|lektor|dubbing|napisy|\d{3,4}p|4k|bdrip|brrip|bluray|web-?dl|webrip|hdtv|dvdrip|x26[45]|h ?26[45]|hevc|xvid|divx|ac3|dd\d.*|aac.*|lp|dp|r d11)\b.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+", " ").Trim(' ', '-', '–', '(', ',');
            if (title.Length == 0) return raw;
            return year != null ? title + " (" + year + ")" : title;
        }

        JellyfinConf LoadJellyfinConf()
        {
            try { return File.Exists(JellyfinConfFile) ? JsonSerializer.Deserialize<JellyfinConf>(ProtectedData.Unprotect(File.ReadAllBytes(JellyfinConfFile), JellyfinEntropy, DataProtectionScope.CurrentUser)) : null; }
            catch (Exception ex) { App.LogError(ex); return null; }
        }

        void SaveJellyfinConf(JellyfinConf c)
        {
            try { Directory.CreateDirectory(DataDir); File.WriteAllBytes(JellyfinConfFile, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(c), JellyfinEntropy, DataProtectionScope.CurrentUser)); }
            catch (Exception ex) { App.LogError(ex); }
        }

        static HttpClient JellyfinClient(JellyfinConf c)
        {
            var http = new HttpClient { BaseAddress = new Uri(c.Url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };   // duza baza (ponad 1000 filmow z obsada) - serwer odpowiada dlugo
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Token", c.Key);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            return http;
        }

        // dane jednego filmu z odpowiedzi Jellyfin (/Items)
        internal static MovieInfo ParseJellyfinItem(JsonElement it)
        {
            string S(string n) { return it.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null; }
            var m = new MovieInfo { Id = S("Id"), Name = S("Name"), Original = S("OriginalTitle"), Overview = S("Overview"), Age = S("OfficialRating") };
            if (it.TryGetProperty("ProductionYear", out var y) && y.ValueKind == JsonValueKind.Number) m.Year = y.GetInt32();
            if (it.TryGetProperty("CommunityRating", out var r) && r.ValueKind == JsonValueKind.Number) m.Rating = Math.Round(r.GetDouble(), 1);
            if (it.TryGetProperty("Genres", out var g) && g.ValueKind == JsonValueKind.Array) m.Genres = g.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).Take(4).ToList();
            if (it.TryGetProperty("People", out var pp) && pp.ValueKind == JsonValueKind.Array)
                foreach (var p in pp.EnumerateArray())
                {
                    var name = p.TryGetProperty("Name", out var pn) ? pn.GetString() : null; var type = p.TryGetProperty("Type", out var pt) ? pt.GetString() : null;
                    if (name == null) continue;
                    if (type == "Director" && m.Director == null) m.Director = name;
                    else if (type == "Actor" && m.People.Count < 8) m.People.Add(name);
                }
            if (it.TryGetProperty("ImageTags", out var tags) && tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("Primary", out var pr)) m.ImageTag = pr.GetString();
            return m;
        }

        // pobranie / uzupelnienie bazy: dane wszystkich filmow, plakaty tylko nowe albo zmienione
        async Task<string> SyncJellyfin(JellyfinConf c, Action<string> progress)
        {
            using (var http = JellyfinClient(c))
            {
                var users = JsonDocument.Parse(await (await Ok(http.GetAsync("Users"))).Content.ReadAsStringAsync()).RootElement;
                // administrator widzi wszystkie biblioteki (pierwszy uzytkownik moze nie miec dostepu do czesci z nich)
                var ul = users.EnumerateArray().ToList();
                var admin = ul.FirstOrDefault(u => u.TryGetProperty("Policy", out var pol) && pol.TryGetProperty("IsAdministrator", out var ad) && ad.ValueKind == JsonValueKind.True);
                var uid = (admin.ValueKind == JsonValueKind.Object ? admin : ul.FirstOrDefault()).GetProperty("Id").GetString();
                if (uid == null) throw new InvalidOperationException(L.T("brak użytkowników na serwerze"));
                progress(L.T("Pobieram listę filmów…"));
                // wszystkie filmy serwera (jak /Items z kluczem API) - widok uzytkownika pomija biblioteki bez dostepu i filmy ponad jego limit wieku (PL-16, PL-18)
                const string q = "Items?Recursive=true&IncludeItemTypes=Movie,Video&Fields=Path,MediaSources,Overview,Genres,People,OriginalTitle,OfficialRating";
                var resp = await http.GetAsync(q);
                if (!resp.IsSuccessStatusCode) resp = await Ok(http.GetAsync("Users/" + uid + "/" + q));   // starszy serwer - widok uzytkownika
                var json = await resp.Content.ReadAsStringAsync();
                var items = JsonDocument.Parse(json).RootElement.GetProperty("Items");
                // foldery ustawione teraz w bibliotekach Jellyfin - stare wpisy z usunietych folderow (np. dawne litery dyskow) pomijamy
                var roots = new List<string>();
                try
                {
                    var vf = JsonDocument.Parse(await (await Ok(http.GetAsync("Library/VirtualFolders"))).Content.ReadAsStringAsync()).RootElement;
                    foreach (var f in vf.EnumerateArray()) if (f.TryGetProperty("Locations", out var locs)) foreach (var l in locs.EnumerateArray()) { var s = l.GetString(); if (!string.IsNullOrEmpty(s)) roots.Add(s.Replace('/', '\\').TrimEnd('\\') + "\\"); }
                }
                catch (Exception) { roots.Clear(); }   // starszy serwer - bez filtra
                var meta = new Dictionary<string, MovieInfo>(Meta, StringComparer.OrdinalIgnoreCase);
                int n = 0, total = items.GetArrayLength(), posters = 0;
                Directory.CreateDirectory(PosterDir);
                var jfAll = new List<(string Name, string Path)>();   // do raportu niedopasowanych
                foreach (var it in items.EnumerateArray())
                {
                    n++;
                    var path = it.TryGetProperty("Path", out var pth) ? pth.GetString() : null; if (path == null) continue;
                    if (roots.Count > 0 && !roots.Any(r => path.Replace('/', '\\').StartsWith(r, StringComparison.OrdinalIgnoreCase))) continue;   // poza bibliotekami Jellyfin
                    var m = ParseJellyfinItem(it);
                    var key = MetaKey(path);
                    if (it.TryGetProperty("Type", out var ty) && ty.GetString() == "Movie") jfAll.Add((m.Name + (m.Year > 0 ? " (" + m.Year + ")" : "") + (string.IsNullOrEmpty(m.Overview) && m.Rating <= 0 ? "  [nierozpoznany w Jellyfin]" : ""), path));
                    meta.TryGetValue(key, out var old);
                    if (old != null && old.Id != m.Id && Richness(old) > Richness(ParseJellyfinItem(it))) continue;   // ten sam plik w drugiej bibliotece Jellyfin, tam nierozpoznany - zostaja lepsze dane
                    m.Poster = old?.Poster;
                    if (m.ImageTag != null && (old == null || old.ImageTag != m.ImageTag || old.Poster != m.Id + "-720.jpg" || !File.Exists(Path.Combine(PosterDir, old.Poster))))
                    {
                        try
                        {
                            var img = await http.GetByteArrayAsync("Items/" + m.Id + "/Images/Primary?maxHeight=720&quality=90");
                            var file = m.Id + "-720.jpg";   // wyzsza jakosc - stare (360 px) pobierane od nowa
                            File.WriteAllBytes(Path.Combine(PosterDir, file), img);
                            if (old?.Poster != null && old.Poster != file) try { File.Delete(Path.Combine(PosterDir, old.Poster)); } catch (Exception) { }   // stary plakat (360 px)
                            m.Poster = file; posters++;
                        }
                        catch (Exception) { }
                    }
                    meta[key] = m;
                    AddTitleKeys(meta, m);
                    if (it.TryGetProperty("MediaSources", out var ms) && ms.ValueKind == JsonValueKind.Array)   // kilka wersji jednego filmu (Jellyfin laczy pliki) - kazdy plik
                        foreach (var s in ms.EnumerateArray()) if (s.TryGetProperty("Path", out var sp) && sp.GetString() is string sps && !meta.ContainsKey(MetaKey(sps))) meta[MetaKey(sps)] = m;
                    if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"\.(mkv|mp4|avi|m4v|ts|m2ts|wmv|mov|mpg|mpeg|iso|webm|flv)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        meta[MetaDirKey(path + "\\x")] = m;   // Jellyfin podal folder filmu - dopasowanie po nazwie folderu
                    if (n % 25 == 0) progress(L.T("Filmy: ") + n + " / " + total + L.T(" · nowe plakaty: ") + posters);
                }
                Directory.CreateDirectory(MetaDir);
                foreach (var k in meta.Keys.Where(k => k.StartsWith("ty:") || k.StartsWith("t:")).ToList()) meta.Remove(k);   // klucze tytulow liczone przy wczytaniu
                File.WriteAllText(MetaFile, JsonSerializer.Serialize(meta));
                foreach (var m in meta.Values.Distinct().ToList()) AddTitleKeys(meta, m);
                foreach (var kv in LoadManual()) meta[kv.Key] = kv.Value;   // reczne informacje zostaja po pobraniu z Jellyfin
                _meta = meta;
                c.Synced = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); SaveJellyfinConf(c);
                var lib = LoadLibrary(); int matched = lib.Count(v => LookupMeta(meta, v.Path) != null);
                // raport: filmy biblioteki bez danych i filmy Jellyfin bez pliku w bibliotece - widac, czemu sie nie lacza
                try
                {
                    var libKeys = new HashSet<string>(lib.Select(v => MetaKey(v.Path)));
                    var rep = new StringBuilder("Biblioteka Velivo bez danych z Jellyfin:\n");
                    foreach (var v in lib.Where(v => LookupMeta(meta, v.Path) == null).OrderBy(v => v.Path)) rep.Append("  " + CleanTitle(v.Title) + "   <-   " + v.Path + "\n");
                    rep.Append("\nFilmy w Jellyfin bez pliku o tej nazwie w bibliotece Velivo:\n");
                    foreach (var j in jfAll.Where(j => !libKeys.Contains(MetaKey(j.Path))).OrderBy(j => j.Path)) rep.Append("  " + j.Name + "   <-   " + j.Path + "\n");
                    File.WriteAllText(Path.Combine(DataDir, "jellyfin-raport.txt"), rep.ToString());
                    // co zniknelo z Jellyfin od poprzedniego pobrania; gdy serwer zwrocil wyraznie mniej (blad, odlaczony dysk) - nic nie oznaczamy
                    var oldFiles = File.Exists(JellyfinFilesFile) ? File.ReadAllLines(JellyfinFilesFile).ToList() : new List<string>();
                    var nowKeys = new HashSet<string>(jfAll.Select(j => MetaKey(j.Path)));
                    if (oldFiles.Count > 0 && jfAll.Count >= oldFiles.Count / 2)
                    {
                        var pending = File.Exists(JellyfinRemovedFile) ? File.ReadAllLines(JellyfinRemovedFile).ToList() : new List<string>();
                        pending = pending.Concat(oldFiles.Where(o => !nowKeys.Contains(MetaKey(o))))
                            .Where(o => !nowKeys.Contains(MetaKey(o))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        if (pending.Count > 0) File.WriteAllLines(JellyfinRemovedFile, pending); else File.Delete(JellyfinRemovedFile);
                    }
                    File.WriteAllLines(JellyfinFilesFile, jfAll.Select(j => j.Path));   // pliki filmow Jellyfin - Odswiez w bibliotece dopisuje nowe
                }
                catch (Exception ex) { App.LogError(ex); }
                return L.T("✅ Filmów z Jellyfin: ") + total + L.T(" · dopasowanych w bibliotece: ") + matched + " / " + lib.Count + L.T(" · nowe plakaty: ") + posters;
            }
        }

        static async Task<HttpResponseMessage> Ok(Task<HttpResponseMessage> t)
        {
            var r = await t;
            if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException(L.T("zły klucz API"));
            r.EnsureSuccessStatusCode();
            return r;
        }

        // przy starcie (w tle) - uzupelnienie bazy, gdy serwer wlaczony; najwyzej raz na 6 godzin
        async void AutoSyncJellyfin()
        {
            var c = LoadJellyfinConf();
            if (c == null || string.IsNullOrEmpty(c.Url) || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - c.Synced < 6 * 3600 * 1000L) return;
            try { await SyncJellyfin(c, _ => { }); _libRefresh?.Invoke(); } catch (Exception) { }   // serwer wylaczony - zostaje lokalna baza
        }

        // okno: adres serwera, klucz API, pobierz / uzupelnij teraz
        // Samo odswiezanie z Jellyfin (ustawienie w oknie "🎞 Jellyfin…"): sprawdzane co 10 min, odswiezenie, gdy minal interwal.
        System.Windows.Threading.DispatcherTimer _jellyfinTimer;
        bool _jellyfinSyncing;
        void StartJellyfinAutoSync()
        {
            if (_jellyfinTimer != null) return;
            _jellyfinTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            _jellyfinTimer.Tick += async (s, e) =>
            {
                _jellyfinTimer.Interval = TimeSpan.FromMinutes(10);
                ShowJellyfinRemoved();   // decyzja o usuniętych z Jellyfin - dymek aż do odpowiedzi (także po ponownym uruchomieniu)
                var c = LoadJellyfinConf();
                if (_jellyfinSyncing || c == null || c.AutoHours <= 0 || string.IsNullOrEmpty(c.Url) || string.IsNullOrEmpty(c.Key)) return;
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - c.Synced < c.AutoHours * 3600000L) return;
                _jellyfinSyncing = true;
                try
                {
                    var msg = await JellyfinRefreshAsync(c, false, false, null);
                    _libRefresh?.Invoke();
                    if (PendingRemovedFromJellyfin().Count > 0) ShowJellyfinRemoved();
                    else if (msg.StartsWith("🎬")) ShowToast(msg, null);
                }
                catch (Exception ex) { App.LogError(ex); }   // serwer wylaczony - sprobujemy przy nastepnym sprawdzeniu
                finally { _jellyfinSyncing = false; }
            };
            _jellyfinTimer.Interval = TimeSpan.FromMinutes(2);   // pierwsze sprawdzenie chwile po starcie, gdy Velivo juz sie wczytalo
            _jellyfinTimer.Start();
        }

        // filmy z biblioteki Velivo, ktore zniknely z Jellyfin
        List<LibraryVideo> PendingRemovedFromJellyfin()
        {
            if (!File.Exists(JellyfinRemovedFile)) return new List<LibraryVideo>();
            var keys = new HashSet<string>(File.ReadAllLines(JellyfinRemovedFile).Select(MetaKey));
            return LoadLibrary().Where(v => keys.Contains(MetaKey(v.Path))).ToList();
        }

        // Pyta, czy usunac z biblioteki Velivo filmy usuniete w Jellyfin. Pliki na dysku zostaja; decyzja jest zapamietana.
        void AskRemovedFromJellyfin(Window owner)
        {
            var hits = PendingRemovedFromJellyfin();
            if (hits.Count == 0) { try { File.Delete(JellyfinRemovedFile); } catch (Exception) { } return; }
            var names = hits.Take(15).Select(v => "• " + (MetaTitle(MetaFor(v.Path)) ?? CleanTitle(v.Title))).ToList();
            var text = L.T("Te filmy usunięto z Jellyfin: ") + hits.Count + "\n\n" + string.Join("\n", names) +
                (hits.Count > names.Count ? "\n… " + (hits.Count - names.Count) : "") +
                L.T("\n\nUsunąć je także z biblioteki Velivo? Pliki na dysku zostają.");
            JellyfinRemovedDecide(MessageBox.Show(owner ?? this, text, "Velivo – Jellyfin", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        }
        // decyzja: usunąć z biblioteki Velivo (pliki na dysku zostają) albo zostawić - zapamiętana (lista wyczyszczona)
        void JellyfinRemovedDecide(bool remove)
        {
            if (remove)
            {
                var keys = new HashSet<string>(PendingRemovedFromJellyfin().Select(v => MetaKey(v.Path)));
                var lib = LoadLibrary();
                lib.RemoveAll(v => keys.Contains(MetaKey(v.Path)));
                SaveLibrary(lib); _libRefresh?.Invoke();
            }
            try { File.Delete(JellyfinRemovedFile); } catch (Exception) { }
            try { _jfRemovedToast?.Close(); } catch (Exception) { }
        }
        // 🎬 dymek „usunięte z Jellyfin” - zostaje na ekranie, aż wybierzesz: usuń z Velivo / zostaw
        Window _jfRemovedToast;
        void ShowJellyfinRemoved()
        {
            var hits = PendingRemovedFromJellyfin();
            if (hits.Count == 0 || _jfRemovedToast != null) return;
            var names = hits.Take(8).Select(v => "• " + (MetaTitle(MetaFor(v.Path)) ?? CleanTitle(v.Title))).ToList();
            var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            panel.Children.Add(new TextBlock { Text = L.T("🎬 Usunięte z Jellyfin: ") + hits.Count, Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = string.Join("\n", names) + (hits.Count > names.Count ? "\n… " + (hits.Count - names.Count) : ""), Foreground = Brushes.Gainsboro, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(new TextBlock { Text = L.T("Usunąć je także z biblioteki Velivo? Pliki na dysku zostają."), Foreground = Brushes.White, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new Thickness(0, 8, 0, 0) });
            var toast = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Owner = IsLoaded ? this : null, Topmost = true, ShowActivated = false,
                SizeToContent = SizeToContent.WidthAndHeight, Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)), Content = panel };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var yes = SmallButton("🗑 " + L.T("Usuń z Velivo"), () => JellyfinRemovedDecide(true));
            var no = SmallButton(L.T("Zostaw"), () => JellyfinRemovedDecide(false));
            foreach (var b in new[] { yes, no }) { b.Background = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)); b.Foreground = Brushes.White; b.BorderBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)); row.Children.Add(b); }
            panel.Children.Add(row);
            toast.Loaded += (s, e) => PlaceToast(toast);
            toast.Closed += (s, e) => _jfRemovedToast = null;
            _jfRemovedToast = toast;
            toast.Show();
        }

        void ShowJellyfinDialog(Window owner)
        {
            var c = LoadJellyfinConf() ?? new JellyfinConf { Url = "http://" };
            var d = new Window { Title = L.T("🎞 Informacje o filmach z Jellyfin"), Width = 520, SizeToContent = SizeToContent.Height, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            Independent(d);   // zwykle okno Windows - nie przykrywa innych na stale
            var p = new StackPanel { Margin = new Thickness(12) };
            p.Children.Add(new TextBlock { Text = L.T("Velivo pobierze z Twojego serwera Jellyfin tytuły, opisy, oceny, obsadę i plakaty do własnej bazy na tym komputerze (działa też, gdy serwer jest wyłączony) i będzie ją uzupełniać."), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
            Func<string, string, TextBox> field = (label, value) => { p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 2) }); var t = new TextBox { Text = value ?? "", Padding = new Thickness(4) }; p.Children.Add(t); return t; };
            var url = field(L.T("Adres serwera (jak w przeglądarce, np. http://192.168.1.20:8096):"), c.Url);
            var key = field(L.T("Klucz API (Jellyfin: Panel sterowania → Klucze API):"), c.Key);
            p.Children.Add(new TextBlock { Text = L.T("Odświeżaj bibliotekę filmów z Jellyfin samo:"), Margin = new Thickness(0, 8, 0, 2) });
            var hours = new[] { 0, 1, 6, 24 };
            var auto = new ComboBox { ItemsSource = new[] { L.T("wyłączone (tylko przyciskiem 🔄 Odśwież)"), L.T("co godzinę"), L.T("co 6 godzin"), L.T("raz dziennie") },
                SelectedIndex = Math.Max(0, Array.IndexOf(hours, c.AutoHours)), ToolTip = L.T("Nowe filmy z Jellyfin trafiają do biblioteki same; o filmach usuniętych z Jellyfin Velivo zapyta przy 🔄 Odśwież") };
            auto.SelectionChanged += (s, e) => { var cur = LoadJellyfinConf() ?? c; cur.AutoHours = hours[Math.Max(0, auto.SelectedIndex)]; c.AutoHours = cur.AutoHours; SaveJellyfinConf(cur); };
            p.Children.Add(auto);
            var info = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Text = c.Synced > 0 ? L.T("Ostatnio pobrano: ") + DateTimeOffset.FromUnixTimeMilliseconds(c.Synced).LocalDateTime.ToString("dd.MM.yyyy HH:mm") + L.T(" · filmów w bazie: ") + Meta.Count : "" };
            p.Children.Add(info);
            // 📂 klucz (i adres serwera) z pliku - bez przepisywania i bez pokazywania klucza na ekranie przy wklejaniu
            var fromFile = SmallButton(L.T("📂 Wczytaj klucz z pliku…"), () =>
            {
                var f = new Microsoft.Win32.OpenFileDialog { Title = L.T("Plik z kluczem API Jellyfin"), Filter = L.T("Pliki tekstowe") + "|*.txt", FileName = "api.txt" };
                if (f.ShowDialog(d) != true) return;
                string text; try { text = new FileInfo(f.FileName).Length > 65536 ? "" : File.ReadAllText(f.FileName); } catch (Exception ex) { info.Foreground = Brushes.IndianRed; info.Text = L.T("⚠ Nie udało się: ") + ex.Message; return; }
                var k = System.Text.RegularExpressions.Regex.Match(text, @"\b[0-9a-fA-F]{32}\b");   // klucz Jellyfin: 32 znaki szesnastkowe
                if (!k.Success) { info.Foreground = Brushes.IndianRed; info.Text = L.T("W tym pliku nie ma klucza API Jellyfin (32 znaki 0–9, a–f)."); return; }
                key.Text = k.Value.ToLowerInvariant();
                // adres serwera w tym samym pliku - też wpisany; najpierw domowy (192.168…, 10.…) - komputer jest w domu, internetowy jest dla telefonu poza domem
                var us = System.Text.RegularExpressions.Regex.Matches(text, @"https?://[^\s""'<>]+").Select(m => m.Value.TrimEnd('/', ',', ';')).ToList();
                var u = us.FirstOrDefault(x => System.Text.RegularExpressions.Regex.IsMatch(x, @"^https?://(192\.168\.|10\.|172\.(1[6-9]|2\d|3[01])\.|[^/:]+\.local\b|localhost\b)")) ?? us.FirstOrDefault();
                if (u != null && (url.Text.Trim().Length == 0 || url.Text.Trim() == "http://")) url.Text = u;
                info.Foreground = Brushes.SeaGreen; info.Text = L.T("🔑 Klucz wczytany z pliku – kliknij „Pobierz / uzupełnij teraz”.");
            });
            fromFile.HorizontalAlignment = HorizontalAlignment.Left; fromFile.Margin = new Thickness(0, 4, 0, 0);
            p.Children.Insert(p.Children.IndexOf(key) + 1, fromFile);   // pod polem klucza
            var ok = new Button { Content = L.T("⬇ Pobierz / uzupełnij teraz"), Height = 30, Padding = new Thickness(12, 0, 12, 0), IsDefault = true, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            ok.Click += async (s, e) =>
            {
                c.Url = url.Text.Trim(); c.Key = key.Text.Trim();
                if (!Uri.TryCreate(c.Url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https") || c.Key.Length == 0) { info.Foreground = Brushes.IndianRed; info.Text = L.T("Podaj adres serwera i klucz API."); return; }
                SaveJellyfinConf(c);
                ok.IsEnabled = false; info.Foreground = Brushes.Gray; info.Text = L.T("Łączę z serwerem…");
                try
                {
                    var r = await Task.Run(() => SyncJellyfin(c, t => Dispatcher.BeginInvoke(new Action(() => info.Text = t))));
                    info.Foreground = Brushes.SeaGreen; info.Text = r;
                    // od razu jak 🔄 Odśwież: nowe filmy z Jellyfin do biblioteki (wcześniej tylko opisy i plakaty - biblioteka zostawała pusta)
                    var add = await JellyfinAddNewAsync(_libSharedView);
                    info.Text = r + "\n" + add;
                    _libRefresh?.Invoke();
                }
                catch (Exception ex) { info.Foreground = Brushes.IndianRed; info.Text = L.T("⚠ Nie udało się: ") + ex.Message; }
                ok.IsEnabled = true;
            };
            p.Children.Add(ok);
            // zmieniony adres / klucz zapisany także przy zamknięciu okna (wcześniej tylko przyciskiem „Pobierz” - zostawał stary adres)
            d.Closed += (s, e) =>
            {
                var nu = url.Text.Trim(); var nk = key.Text.Trim();
                if (Uri.TryCreate(nu, UriKind.Absolute, out var u2) && (u2.Scheme == "http" || u2.Scheme == "https") && nk.Length > 0 && (nu != c.Url || nk != c.Key))
                { var cur = LoadJellyfinConf() ?? c; cur.Url = nu; cur.Key = nk; SaveJellyfinConf(cur); }
            };
            d.Content = p;
            d.Show();
        }
    }
}
