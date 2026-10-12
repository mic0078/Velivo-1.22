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

namespace Przegladarka
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

        sealed class JellyfinConf { public string Url { get; set; } public string Key { get; set; } public long Synced { get; set; } }

        static string MetaDir { get { return Path.Combine(DataDir, "filmy-info"); } }
        static string MetaFile { get { return Path.Combine(MetaDir, "baza.json"); } }
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
                return _meta ?? (_meta = new Dictionary<string, MovieInfo>(StringComparer.OrdinalIgnoreCase));
            }
        }

        // klucz dopasowania: nazwa pliku bez rozszerzenia (male litery)
        internal static string MetaKey(string path)
        {
            var p = (path ?? "").Replace('/', '\\');
            int i = p.LastIndexOf('\\');
            var name = i >= 0 ? p.Substring(i + 1) : p;
            int dot = name.LastIndexOf('.');
            return (dot > 0 ? name.Substring(0, dot) : name).ToLowerInvariant();
        }

        internal MovieInfo MetaFor(string path) { return path != null && Meta.TryGetValue(MetaKey(path), out var m) ? m : null; }

        // tytul z Jellyfin tylko dla rozpoznanego filmu (nierozpoznany ma za nazwe nazwe pliku - bez opisu i oceny)
        internal static string MetaTitle(MovieInfo m) { return m == null || string.IsNullOrEmpty(m.Overview) && m.Rating <= 0 ? null : m.Name + (m.Year > 0 ? " (" + m.Year + ")" : ""); }

        // czytelny tytul z nazwy pliku: bez [dopiskow], jakosci i kodekow, kropki jako spacje, rok w nawiasie
        internal static string CleanTitle(string raw)
        {
            var t = System.Text.RegularExpressions.Regex.Replace(raw ?? "", @"\[[^\]]*\]", " ");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"[._]", " ");
            string title, year = null;
            var m = System.Text.RegularExpressions.Regex.Match(t, @"^(.+?)[\s(\-]*\b((19|20)\d{2})\b");
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
            var http = new HttpClient { BaseAddress = new Uri(c.Url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };
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
                var uid = users.EnumerateArray().Select(u => u.GetProperty("Id").GetString()).FirstOrDefault();
                if (uid == null) throw new InvalidOperationException(L.T("brak użytkowników na serwerze"));
                progress(L.T("Pobieram listę filmów…"));
                var json = await (await Ok(http.GetAsync("Users/" + uid + "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path,Overview,Genres,People,OriginalTitle,OfficialRating"))).Content.ReadAsStringAsync();
                var items = JsonDocument.Parse(json).RootElement.GetProperty("Items");
                var meta = new Dictionary<string, MovieInfo>(Meta, StringComparer.OrdinalIgnoreCase);
                int n = 0, total = items.GetArrayLength(), posters = 0;
                Directory.CreateDirectory(PosterDir);
                foreach (var it in items.EnumerateArray())
                {
                    n++;
                    var path = it.TryGetProperty("Path", out var pth) ? pth.GetString() : null; if (path == null) continue;
                    var m = ParseJellyfinItem(it);
                    var key = MetaKey(path);
                    meta.TryGetValue(key, out var old);
                    m.Poster = old?.Poster;
                    if (m.ImageTag != null && (old == null || old.ImageTag != m.ImageTag || old.Poster == null || !File.Exists(Path.Combine(PosterDir, old.Poster))))
                    {
                        try
                        {
                            var img = await http.GetByteArrayAsync("Items/" + m.Id + "/Images/Primary?maxHeight=360&quality=85");
                            var file = m.Id + ".jpg";
                            File.WriteAllBytes(Path.Combine(PosterDir, file), img);
                            m.Poster = file; posters++;
                        }
                        catch (Exception) { }
                    }
                    meta[key] = m;
                    if (n % 25 == 0) progress(L.T("Filmy: ") + n + " / " + total + L.T(" · nowe plakaty: ") + posters);
                }
                Directory.CreateDirectory(MetaDir);
                File.WriteAllText(MetaFile, JsonSerializer.Serialize(meta));
                _meta = meta;
                c.Synced = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); SaveJellyfinConf(c);
                return L.T("✅ Filmów z Jellyfin: ") + total + L.T(" · nowe plakaty: ") + posters;
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
        void ShowJellyfinDialog(Window owner)
        {
            var c = LoadJellyfinConf() ?? new JellyfinConf { Url = "http://" };
            var d = new Window { Title = L.T("🎞 Informacje o filmach z Jellyfin"), Width = 520, SizeToContent = SizeToContent.Height, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var p = new StackPanel { Margin = new Thickness(12) };
            p.Children.Add(new TextBlock { Text = L.T("Velivo pobierze z Twojego serwera Jellyfin tytuły, opisy, oceny, obsadę i plakaty do własnej bazy na tym komputerze (działa też, gdy serwer jest wyłączony) i będzie ją uzupełniać."), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
            Func<string, string, TextBox> field = (label, value) => { p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 2) }); var t = new TextBox { Text = value ?? "", Padding = new Thickness(4) }; p.Children.Add(t); return t; };
            var url = field(L.T("Adres serwera (jak w przeglądarce, np. http://192.168.1.20:8096):"), c.Url);
            var key = field(L.T("Klucz API (Jellyfin: Panel sterowania → Klucze API):"), c.Key);
            var info = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Text = c.Synced > 0 ? L.T("Ostatnio pobrano: ") + DateTimeOffset.FromUnixTimeMilliseconds(c.Synced).LocalDateTime.ToString("dd.MM.yyyy HH:mm") + L.T(" · filmów w bazie: ") + Meta.Count : "" };
            p.Children.Add(info);
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
                    _libRefresh?.Invoke();
                }
                catch (Exception ex) { info.Foreground = Brushes.IndianRed; info.Text = L.T("⚠ Nie udało się: ") + ex.Message; }
                ok.IsEnabled = true;
            };
            p.Children.Add(ok);
            d.Content = p;
            d.ShowDialog();
        }
    }
}
