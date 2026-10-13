using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Przegladarka
{
    // Napisy z OpenSubtitles.com (jak w Jellyfin): wyszukiwanie po skrocie pliku (dokladne dopasowanie) i po nazwie,
    // zapis obok filmu jako "film.pl.srt" - odtwarzacz i lektor wczytuja je same. Klucz aplikacji wbudowany, uzytkownik - swoje konto;
    // haslo zaszyfrowane dla tego konta Windows (DPAPI), tylko na tym komputerze.
    public partial class MainWindow
    {
        static string SubsAccountFile { get { return Path.Combine(DataDir, "napisy-konto.bin"); } }
        static readonly byte[] SubsEntropy = Encoding.UTF8.GetBytes("Velivo.OpenSubtitles");
        const string OsApi = "https://api.opensubtitles.com/api/v1";
        // klucz aplikacji Velivo (OpenSubtitles.com - API Consumers, jak w Jellyfin); uzytkownik loguje sie swoim kontem
        const string OsAppKey = "tL1Bgs1TAlhoBU5x2eZNb4qU6p8wawyK";

        sealed class SubsAccount { public string Key { get; set; } public string User { get; set; } public string Pass { get; set; } public string Lang { get; set; } = "pl"; }

        SubsAccount LoadSubsAccount()
        {
            try
            {
                if (!File.Exists(SubsAccountFile)) return null;
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(SubsAccountFile), SubsEntropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<SubsAccount>(plain);
            }
            catch (Exception ex) { App.LogError(ex); return null; }
        }

        void SaveSubsAccount(SubsAccount a)
        {
            try { Directory.CreateDirectory(DataDir); File.WriteAllBytes(SubsAccountFile, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(a), SubsEntropy, DataProtectionScope.CurrentUser)); }
            catch (Exception ex) { App.LogError(ex); }
        }

        // skrot pliku OpenSubtitles: rozmiar + suma 64-bitowych slow z pierwszych i ostatnich 64 KB
        internal static string OsHash(string file)
        {
            using (var fs = File.OpenRead(file))
            {
                long len = fs.Length; ulong h = (ulong)len;
                if (len < 131072) return null;
                var buf = new byte[65536];
                foreach (var off in new[] { 0L, len - 65536 })
                {
                    fs.Position = off;
                    int n = 0; while (n < buf.Length) { int r = fs.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; }
                    for (int i = 0; i + 8 <= n; i += 8) h = unchecked(h + BitConverter.ToUInt64(buf, i));
                }
                return h.ToString("x16");
            }
        }

        // nazwa do wyszukania: bez jakosci, kodekow i grup ("Alien.1979.PL.1080p.BDRip.x264-MR" -> "alien 1979")
        internal static string OsQuery(string title)
        {
            var t = Regex.Replace(title ?? "", @"[._\[\]\(\)]", " ");
            var m = Regex.Match(t, @"^(.+?\b(19|20)\d{2})\b");   // tytul do roku ("2001 A Space Odyssey 1968" - rok z tytulu nie ucina)
            if (m.Success) t = m.Groups[1].Value;
            else t = Regex.Replace(t, @"\b(pl|eng?|lektor|dubbing|napisy|\d{3,4}p|4k|bdrip|brrip|web-?dl|webrip|hdtv|dvdrip|x26[45]|h ?26[45]|hevc|xvid|ac3|dd\d?.*|aac.*)\b.*$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(t, @"\s+", " ").Trim(' ', '-', '_', ',').ToLowerInvariant();   // "28 years later-720p" -> "28 years later"
        }

        static HttpClient OsClient(SubsAccount a, string token)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("Api-Key", string.IsNullOrEmpty(a.Key) ? OsAppKey : a.Key);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Velivo v1.22");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            if (token != null) http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + token);
            return http;
        }

        static async Task<JsonElement> OsJson(HttpResponseMessage resp)
        {
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                string msg = null;
                try { var e = JsonDocument.Parse(text).RootElement; if (e.TryGetProperty("message", out var m)) msg = m.GetString(); } catch (Exception) { }
                throw new InvalidOperationException(msg ?? ((int)resp.StatusCode + " " + resp.ReasonPhrase));
            }
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        sealed class OsResult { public string Name { get; set; } public string Source { get; set; } public string Lang { get; set; } public int Downloads { get; set; } public string Match { get; set; } public long FileId; public string Text; }   // Text - napisy z Napiprojektu (juz pobrane)

        // ---------- Napiprojekt (polskie napisy, po skrocie pliku, bez konta) ----------

        // skrot: MD5 pierwszych 10 MB pliku
        internal static string NapiHash(string file)
        {
            using (var fs = File.OpenRead(file))
            using (var md5 = MD5.Create())
            {
                var buf = new byte[10485760]; int n = 0, r;
                while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
                return Convert.ToHexString(md5.ComputeHash(buf, 0, n)).ToLowerInvariant();
            }
        }

        // kontrolny ciag "t" wymagany przez Napiprojekt (ten sam algorytm co w innych programach, np. subliminal)
        internal static string NapiToken(string h)
        {
            int[] idx = { 0xe, 0x3, 0x6, 0x8, 0x2 }, mul = { 2, 2, 5, 4, 3 }, add = { 0, 0xd, 0x10, 0xb, 0x5 };
            var sb = new StringBuilder();
            for (int i = 0; i < 5; i++)
            {
                int t = add[i] + Convert.ToInt32(h[idx[i]].ToString(), 16);
                int v = Convert.ToInt32(h.Substring(t, 2), 16);
                var x = (v * mul[i]).ToString("x");
                sb.Append(x[x.Length - 1]);
            }
            return sb.ToString();
        }

        // MPL2 ("[12][34]tekst", dziesiate czesci sekundy) -> SRT; inne formaty bez zmian
        internal static string MplToSrt(string text)
        {
            var lines = (text ?? "").Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count == 0 || !lines.All(l => Regex.IsMatch(l, @"^\[\d+\]\[\d*\]"))) return text;
            var sb = new StringBuilder(); int n = 1;
            Func<int, string> ts = d => { var t = TimeSpan.FromMilliseconds(d * 100L); return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") + "," + t.Milliseconds.ToString("000"); };
            foreach (var l in lines)
            {
                var m = Regex.Match(l, @"^\[(\d+)\]\[(\d*)\](.*)$");
                int a = int.Parse(m.Groups[1].Value), b = m.Groups[2].Value.Length > 0 ? int.Parse(m.Groups[2].Value) : a + 30;
                var body = string.Join("\n", m.Groups[3].Value.Split('|').Select(x => x.TrimStart('/').Trim()));
                sb.Append(n++).Append('\n').Append(ts(a)).Append(" --> ").Append(ts(b)).Append('\n').Append(body).Append("\n\n");
            }
            return sb.ToString();
        }

        static async Task<string> NapiFetch(string video)
        {
            var h = await Task.Run(() => NapiHash(video));
            var url = "http://napiprojekt.pl/unit_napisy/dl.php?v=dreambox&kolejka=false&nick=&pass=&napios=Linux&l=PL&f=" + h + "&t=" + NapiToken(h);
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            {
                var data = await http.GetByteArrayAsync(url);
                if (data.Length < 10 || Encoding.ASCII.GetString(data, 0, Math.Min(4, data.Length)) == "NPc0") return null;   // brak napisow dla tego pliku
                return MplToSrt(SubtitleText(data));
            }
        }

        // konto: klucz API (z opensubtitles.com - Konsumenci API), login, haslo, jezyk napisow
        bool SubsAccountDialog(Window owner, SubsAccount a)
        {
            var d = new Window { Title = L.T("💬 Konto OpenSubtitles"), Width = 480, SizeToContent = SizeToContent.Height, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var p = new StackPanel { Margin = new Thickness(12) };
            p.Children.Add(new TextBlock { Text = L.T("Napisy z OpenSubtitles.com – zaloguj się swoim kontem (darmowe konto: opensubtitles.com)."), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
            Func<string, string, TextBox> field = (label, value) => { p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 2) }); var t = new TextBox { Text = value ?? "", Padding = new Thickness(4) }; p.Children.Add(t); return t; };
            var user = field(L.T("Nazwa użytkownika:"), a.User);
            p.Children.Add(new TextBlock { Text = L.T("Hasło:"), Margin = new Thickness(0, 8, 0, 2) });
            var pass = new PasswordBox { Password = a.Pass ?? "", Padding = new Thickness(4) }; p.Children.Add(pass);
            var lang = field(L.T("Języki napisów (np. pl albo pl,en):"), a.Lang);
            var ok = new Button { Content = "OK", Width = 90, Height = 28, IsDefault = true, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            var info = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            p.Children.Add(info);
            // OK = proba logowania od razu: potwierdzenie albo dokladny powod z OpenSubtitles
            ok.Click += async (s, e) =>
            {
                var test = new SubsAccount { Key = a.Key, User = user.Text.Trim(), Pass = pass.Password };
                if (test.User.Length == 0) { info.Foreground = Brushes.IndianRed; info.Text = L.T("Podaj nazwę użytkownika."); return; }
                ok.IsEnabled = false; info.Foreground = Brushes.Gray; info.Text = L.T("Loguję…");
                try
                {
                    var r = await OsLogin(test);
                    info.Foreground = Brushes.SeaGreen; info.Text = L.T("✅ Zalogowano: ") + test.User + (r.Item2 > 0 ? L.T(" · pobrań dziennie: ") + r.Item2 : "");
                    await System.Threading.Tasks.Task.Delay(1200);
                    d.DialogResult = true;
                }
                catch (Exception ex) { info.Foreground = Brushes.IndianRed; info.Text = L.T("⚠ Nie udało się zalogować: ") + ex.Message; ok.IsEnabled = true; }
            };
            p.Children.Add(ok);
            p.Children.Remove(info); p.Children.Insert(p.Children.IndexOf(ok), info);
            d.Content = p;
            if (d.ShowDialog() != true) return false;
            a.User = user.Text.Trim(); a.Pass = pass.Password; a.Lang = Regex.Replace(lang.Text.ToLowerInvariant(), @"[^a-z,-]", "");
            if (a.Lang.Length == 0) a.Lang = "pl";
            SaveSubsAccount(a);
            return a.User.Length > 0;
        }

        // logowanie: token i dzienny limit pobran
        static async Task<Tuple<string, int>> OsLogin(SubsAccount a)
        {
            using (var http = OsClient(a, null))
            {
                var body = new StringContent(JsonSerializer.Serialize(new { username = a.User, password = a.Pass }), Encoding.UTF8, "application/json");
                var j = await OsJson(await http.PostAsync(OsApi + "/login", body));
                int allowed = j.TryGetProperty("user", out var u) && u.TryGetProperty("allowed_downloads", out var ad) && ad.ValueKind == JsonValueKind.Number ? ad.GetInt32() : 0;
                return Tuple.Create(j.GetProperty("token").GetString(), allowed);
            }
        }

        // okno: wyniki dla filmu, "Pobierz" = zapis obok filmu (film.<jezyk>.srt)
        void ShowSubtitleSearch(string video, Window owner, Action<string> loaded = null)
        {
            if (!File.Exists(video)) { MessageBox.Show(owner, L.T("Napisy można pobrać do filmu z dysku tego komputera albo z dysku sieciowego."), L.T("Napisy")); return; }
            var acc = LoadSubsAccount() ?? new SubsAccount();
            if (string.IsNullOrEmpty(acc.User) && !SubsAccountDialog(owner, acc)) return;
            var w = new Window { Title = L.T("💬 Napisy – ") + Path.GetFileNameWithoutExtension(video), Width = 760, Height = 440, Owner = owner, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            Independent(w);   // zwykle okno Windows - nie przykrywa innych na stale
            var query = new TextBox { Text = OsQuery(Path.GetFileNameWithoutExtension(video)), Margin = new Thickness(8, 8, 0, 4), Padding = new Thickness(4) };
            var searchBtn = SmallButton(L.T("🔍 Szukaj"), null); searchBtn.Margin = new Thickness(6, 8, 0, 4);
            var accBtn = SmallButton(L.T("👤 Konto"), null); accBtn.Margin = new Thickness(6, 8, 8, 4);
            var top = new DockPanel();
            DockPanel.SetDock(accBtn, Dock.Right); top.Children.Add(accBtn);
            // zrodla napisow (zapamietane)
            var useOs = new CheckBox { Content = "OpenSubtitles", IsChecked = GetPref("napisy-os") != "0", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 8, 0, 4) };
            var useNapi = new CheckBox { Content = L.T("Napiprojekt (PL)"), IsChecked = GetPref("napisy-napi") != "0", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 8, 0, 4), ToolTip = L.T("Polskie napisy dopasowane do tego pliku, bez konta") };
            useOs.Click += (s, a) => SetPref("napisy-os", useOs.IsChecked == true ? "1" : "0");
            useNapi.Click += (s, a) => SetPref("napisy-napi", useNapi.IsChecked == true ? "1" : "0");
            DockPanel.SetDock(useNapi, Dock.Right); top.Children.Add(useNapi);
            DockPanel.SetDock(useOs, Dock.Right); top.Children.Add(useOs);
            DockPanel.SetDock(searchBtn, Dock.Right); top.Children.Add(searchBtn);
            top.Children.Add(query);
            var grid = new GridView();
            foreach (var c in new[] { new[] { "Name", "Napisy (wydanie)", "360" }, new[] { "Source", "Źródło", "100" }, new[] { "Lang", "Język", "60" }, new[] { "Match", "Dopasowanie", "110" }, new[] { "Downloads", "Pobrań", "70" } })
                grid.Columns.Add(new GridViewColumn { Header = L.T(c[1]), Width = double.Parse(c[2]), DisplayMemberBinding = new System.Windows.Data.Binding(c[0]) });
            var list = new ListView { View = grid, Margin = new Thickness(8, 0, 8, 0) };
            RememberColumns(w, "napisy", grid);
            var sortSubs = SortableList(list, "napisy");
            var status = new TextBlock { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            string token = null;
            Func<Task<bool>> login = async () =>
            {
                if (token != null || string.IsNullOrEmpty(acc.User)) return true;
                token = (await OsLogin(acc)).Item1;
                return true;
            };
            Func<Task> search = async () =>
            {
                status.Text = L.T("Szukam napisów…"); list.ItemsSource = null;
                var res = new List<OsResult>(); string osInfo = null, napiInfo = null;
                bool os = useOs.IsChecked == true, np = useNapi.IsChecked == true;
                // Napiprojekt rownolegle (polskie napisy dokladnie do tego pliku); wynik albo powod
                var napi = np ? Task.Run(async () => { try { return Tuple.Create(await NapiFetch(video), (string)null); } catch (Exception ex) { return Tuple.Create((string)null, ex.Message); } }) : Task.FromResult(Tuple.Create((string)null, (string)null));
                if (os) try
                {
                    var hash = await Task.Run(() => { try { return OsHash(video); } catch (Exception) { return null; } });
                    var args = new SortedDictionary<string, string>(StringComparer.Ordinal) { { "languages", acc.Lang } };   // parametry alfabetycznie (wymaganie API)
                    if (hash != null) args["moviehash"] = hash;
                    if (query.Text.Trim().Length > 0) args["query"] = query.Text.Trim().ToLowerInvariant();
                    var url = OsApi + "/subtitles?" + string.Join("&", args.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
                    JsonElement j;
                    using (var http = OsClient(acc, null)) j = await OsJson(await http.GetAsync(url));
                    foreach (var it in j.GetProperty("data").EnumerateArray())
                    {
                        var at = it.GetProperty("attributes");
                        var files = at.GetProperty("files"); if (files.GetArrayLength() == 0) continue;
                        bool hashMatch = at.TryGetProperty("moviehash_match", out var mm) && mm.ValueKind == JsonValueKind.True;
                        res.Add(new OsResult
                        {
                            Name = (at.TryGetProperty("release", out var rl) ? rl.GetString() : null) ?? files[0].GetProperty("file_name").GetString(),
                            Lang = at.TryGetProperty("language", out var lg) ? lg.GetString() : "",
                            Downloads = at.TryGetProperty("download_count", out var dc) && dc.ValueKind == JsonValueKind.Number ? dc.GetInt32() : 0,
                            Match = hashMatch ? L.T("✔ ten plik") : L.T("po nazwie"), Source = "OpenSubtitles",
                            FileId = files[0].GetProperty("file_id").GetInt64(),
                        });
                    }
                }
                catch (Exception ex) { osInfo = "⚠ " + ex.Message; }
                if (os && osInfo == null) osInfo = res.Count + L.T(" wyników");
                var nt = await napi;
                if (nt.Item1 != null) { res.Insert(0, new OsResult { Name = L.T("Napisy do tego pliku"), Source = "Napiprojekt", Lang = "pl", Match = L.T("✔ ten plik"), Text = nt.Item1 }); napiInfo = L.T("znaleziono"); }
                else if (np) napiInfo = nt.Item2 != null ? "⚠ " + nt.Item2 : L.T("brak napisów do tego pliku");
                list.ItemsSource = res.OrderBy(r => r.Match == L.T("✔ ten plik") ? 0 : 1).ThenByDescending(r => r.Downloads).ToList();
                sortSubs?.Invoke();
                // co odpowiedzialo kazde zrodlo
                status.Text = (!os && !np ? L.T("Zaznacz co najmniej jedno źródło napisów.") :
                    string.Join("  ·  ", new[] { os ? "OpenSubtitles: " + osInfo : null, np ? "Napiprojekt: " + napiInfo : null }.Where(x => x != null))) + (res.Count > 0 ? L.T("  ·  dwuklik = pobierz") : "");
            };
            Func<Task> download = async () =>
            {
                var r = list.SelectedItem as OsResult; if (r == null) return;
                status.Text = L.T("Pobieram…");
                if (r.Text != null)   // Napiprojekt - tekst juz jest; MicroDVD ({klatki}) jako .sub, reszta jako .srt
                {
                    try
                    {
                        var ext = Regex.IsMatch(r.Text.TrimStart(), @"^\{\d+\}") ? ".sub" : ".srt";
                        var t2 = Path.Combine(Path.GetDirectoryName(video), Path.GetFileNameWithoutExtension(video) + ".pl" + ext);
                        File.WriteAllText(t2, r.Text, new UTF8Encoding(true));
                        status.Text = L.T("✅ Zapisano: ") + Path.GetFileName(t2);
                        loaded?.Invoke(t2);
                    }
                    catch (Exception ex) { status.Text = L.T("⚠ ") + ex.Message; }
                    return;
                }
                try
                {
                    await login();
                    JsonElement j;
                    using (var http = OsClient(acc, token))
                        j = await OsJson(await http.PostAsync(OsApi + "/download", new StringContent(JsonSerializer.Serialize(new { file_id = r.FileId }), Encoding.UTF8, "application/json")));
                    var link = j.GetProperty("link").GetString();
                    byte[] data;
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) }) data = await http.GetByteArrayAsync(link);
                    var lang = string.IsNullOrEmpty(r.Lang) ? "pl" : r.Lang;
                    var target = Path.Combine(Path.GetDirectoryName(video), Path.GetFileNameWithoutExtension(video) + "." + lang + ".srt");
                    File.WriteAllBytes(target, data);
                    var left = j.TryGetProperty("remaining", out var rem) && rem.ValueKind == JsonValueKind.Number ? L.T("  · pozostało pobrań dziś: ") + rem.GetInt32() : "";
                    status.Text = L.T("✅ Zapisano: ") + Path.GetFileName(target) + left;
                    loaded?.Invoke(target);
                }
                catch (UnauthorizedAccessException) { status.Text = L.T("⚠ Brak zapisu do folderu filmu"); }
                catch (Exception ex) { status.Text = L.T("⚠ ") + ex.Message; }
            };
            searchBtn.Click += (s, a) => _ = search();
            query.KeyDown += (s, a) => { if (a.Key == System.Windows.Input.Key.Enter) _ = search(); };
            accBtn.Click += (s, a) => { if (SubsAccountDialog(w, acc)) { token = null; _ = search(); } };
            list.MouseDoubleClick += (s, a) => _ = download();
            var bar = new DockPanel { Margin = new Thickness(6), LastChildFill = false };
            DockPanel.SetDock(status, Dock.Left); bar.Children.Add(status);
            var dl = SmallButton(L.T("⬇ Pobierz do folderu filmu"), () => _ = download());
            DockPanel.SetDock(dl, Dock.Right); bar.Children.Add(dl);
            var body = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); body.Children.Add(top);
            DockPanel.SetDock(bar, Dock.Bottom); body.Children.Add(bar);
            body.Children.Add(list);
            w.Content = body;
            w.Show();
            _ = search();
        }
    }
}
