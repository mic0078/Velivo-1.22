using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // ⬇ pobieranie z adresu (jak „Otwórz strumień sieciowy” w VLC, ale na dysk): adres tego, co gra, albo ze schowka;
    // login i hasło z linku (http://login:haslo@serwer/…) albo z pól; plik wprost (wznawiany od miejsca przerwania), strumień i strona - yt-dlp.
    // Adres z hasłem nigdy w dzienniku (VlcLogSafe).
    public partial class MainWindow
    {
        static readonly string[] PlikiFilmow = { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".webm", ".wmv", ".flv", ".mpg", ".mpeg", ".m2ts", ".mp3", ".m4a", ".flac", ".aac", ".ogg" };
        Window _dlWin;

        internal void ShowUrlDownload(string current, Window owner, string folderNa = null, Action<string> poPobraniu = null, string nazwa = null)   // nazwa - tytuł pozycji z listy (adres od dostawcy ma w nazwie sam numer)
        {
            if (_dlWin != null) { _dlWin.Activate(); return; }
            string clip = null; try { if (Clipboard.ContainsText()) clip = Clipboard.GetText().Trim(); } catch (Exception) { }
            bool Http(string u) => u != null && (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            var start = Http(current) ? current : Http(clip) ? clip : "";
            // tytuł: kanał / film z biblioteki / nazwa z adresu
            string Tytul(string u)
            {
                if (string.IsNullOrEmpty(u)) return "";
                if (!string.IsNullOrWhiteSpace(nazwa) && u == current) return nazwa;
                if (_iptvPlaying.TryGetValue(u, out var ch) && !string.IsNullOrWhiteSpace(ch.Name)) return ch.Name;
                try { var v = LoadLibrary().FirstOrDefault(x => x.Path == u); if (v != null) return ShownTitle(v, MetaFor(v.Path)); } catch (Exception) { }
                try { return Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(u).AbsolutePath)); } catch (Exception) { return ""; }
            }
            string Ext(string u) { try { var e = Path.GetExtension(new Uri(u).AbsolutePath).ToLowerInvariant(); return PlikiFilmow.Contains(e) ? e : ""; } catch (Exception) { return ""; } }
            string Czysta(string n) { foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, ' '); return n.Trim(); }

            var sp = new StackPanel { Margin = new Thickness(16) };
            TextBlock Lab(string t) { var x = new TextBlock { Text = t, Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 8, 0, 2) }; sp.Children.Add(x); return x; }
            sp.Children.Add(new TextBlock { Text = L.T("Adres pliku na serwerze (film od dostawcy, nagranie, plik z chmury). Adres tego, co oglądasz, wpisuje się sam – możesz też wkleić inny. Login i hasło zapisane w linku Velivo odczyta samo."), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 });
            Lab(L.T("Adres (URL)"));
            var urlRow = new DockPanel();
            var url = new TextBox { Text = start, Padding = new Thickness(4) };
            var paste = new Button { Content = L.T("📋 Wklej"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = L.T("Wklej adres ze schowka") };
            var now = new Button { Content = L.T("🎬 To, co gra"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), IsEnabled = Http(current), ToolTip = L.T("Adres filmu albo kanału, który teraz grasz") };
            DockPanel.SetDock(now, Dock.Right); DockPanel.SetDock(paste, Dock.Right); urlRow.Children.Add(now); urlRow.Children.Add(paste); urlRow.Children.Add(url);
            sp.Children.Add(urlRow);
            var credRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            credRow.ColumnDefinitions.Add(new ColumnDefinition()); credRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); credRow.ColumnDefinitions.Add(new ColumnDefinition());
            var login = new TextBox { Padding = new Thickness(4), ToolTip = L.T("Login – tylko gdy serwer o niego pyta (z linku odczytany sam)") };
            var pass = new PasswordBox { Padding = new Thickness(4), ToolTip = L.T("Hasło – tylko gdy serwer o nie pyta (z linku odczytane samo)") };
            var lp = new StackPanel(); lp.Children.Add(new TextBlock { Text = L.T("Login (gdy serwer pyta)"), Foreground = Brushes.Gray, FontSize = 12 }); lp.Children.Add(login);
            var pp = new StackPanel(); pp.Children.Add(new TextBlock { Text = L.T("Hasło"), Foreground = Brushes.Gray, FontSize = 12 }); pp.Children.Add(pass);
            Grid.SetColumn(pp, 2); credRow.Children.Add(lp); credRow.Children.Add(pp); sp.Children.Add(credRow);
            Lab(L.T("Nazwa pliku"));
            var name = new TextBox { Padding = new Thickness(4) }; sp.Children.Add(name);
            Action fill = () => { var u = url.Text.Trim(); var t = Tytul(u); name.Text = t.Length == 0 ? "" : Czysta(t) + (Ext(u).Length > 0 ? Ext(u) : ""); };
            fill();
            paste.Click += (s, e) => { try { if (Clipboard.ContainsText()) { url.Text = Clipboard.GetText().Trim(); fill(); } } catch (Exception) { } };
            now.Click += (s, e) => { url.Text = current; fill(); };
            url.LostFocus += (s, e) => { if (name.Text.Length == 0) fill(); };
            string folder = folderNa ?? (!string.IsNullOrWhiteSpace(_settings.VideoDir) && Directory.Exists(_settings.VideoDir) ? _settings.VideoDir : DefaultVideoDir());
            var where = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var folderText = new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 8, 0), ToolTip = folder };
            var change = new Button { Content = L.T("Zmień folder…"), Padding = new Thickness(10, 3, 10, 3) };
            change.Click += (s, e) => { var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = folder, Title = L.T("Gdzie zapisać film?") }; if (dlg.ShowDialog(_dlWin) == true) { folder = dlg.FolderName; folderText.Text = folder; folderText.ToolTip = folder; } };
            var wl = new TextBlock { Text = L.T("Zapisz w:"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(wl, Dock.Left); DockPanel.SetDock(change, Dock.Right); where.Children.Add(wl); where.Children.Add(change); where.Children.Add(folderText);
            sp.Children.Add(where);
            var toLib = new CheckBox { Content = L.T("Dodaj do biblioteki filmów po pobraniu"), IsChecked = true, Margin = new Thickness(0, 8, 0, 0) }; if (poPobraniu == null) sp.Children.Add(toLib);
            var bar = new ProgressBar { Height = 8, Margin = new Thickness(0, 12, 0, 4), Visibility = Visibility.Collapsed };
            var stan = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
            sp.Children.Add(bar); sp.Children.Add(stan);
            var play = new Button { Content = L.T("▶ Odtwórz bez pobierania"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
            var ok = new Button { Content = L.T("⬇ Pobierz"), IsDefault = true, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = L.T("Anuluj"), IsCancel = true, Padding = new Thickness(14, 5, 14, 5) };
            var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            bs.Children.Add(play); bs.Children.Add(ok); bs.Children.Add(cancel); sp.Children.Add(bs);
            var w = _dlWin = new Window { Title = L.T("⬇ Pobierz z adresu"), Width = 620, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, Content = sp };
            if (owner != null) { w.Owner = owner; w.Topmost = owner.Topmost; }
            CancellationTokenSource cts = null;
            w.Closed += (s, e) => { cts?.Cancel(); _dlWin = null; };
            cancel.Click += (s, e) => w.Close();
            play.Click += (s, e) => { var u = url.Text.Trim(); if (!Http(u)) { stan.Text = L.T("Wpisz albo wklej adres zaczynający się od http:// lub https://"); return; } w.Close(); PlayVideos(new[] { u }, 0, false); };
            ok.Click += async (s, e) =>
            {
                var u = url.Text.Trim();
                if (!Http(u)) { stan.Text = L.T("Wpisz albo wklej adres zaczynający się od http:// lub https://"); return; }
                if (name.Text.Trim().Length == 0) fill();
                var plik = Path.Combine(folder, Czysta(name.Text.Trim().Length > 0 ? name.Text.Trim() : "film-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm")));
                if (folderNa == null && folder != _settings.VideoDir) { _settings.VideoDir = folder; try { _settings.Save(DataDir); } catch (Exception) { } }
                ok.IsEnabled = play.IsEnabled = url.IsEnabled = false; bar.Visibility = Visibility.Visible; bar.IsIndeterminate = true; stan.Text = L.T("Łączę z serwerem…");
                cts = new CancellationTokenSource();
                string wynik; bool lib = toLib.IsChecked == true;
                try { wynik = await PobierzZAdresu(u, login.Text.Trim(), pass.Password, plik, cts.Token, (got, total, speed) => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        bar.IsIndeterminate = total <= 0; if (total > 0) bar.Value = 100.0 * got / total;
                        stan.Text = Mb(got) + (total > 0 ? L.T(" z ") + Mb(total) : "") + " · " + Mb((long)speed) + "/s" + (total > 0 && speed > 0 ? L.T(" · zostało ok. ") + Math.Max(1, (int)Math.Ceiling((total - got) / speed / 60)) + L.T(" min") : "") + L.T(" · przerwane pobieranie wznowi się od miejsca, w którym stanęło");
                    }))); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { App.LogInfo("Pobieranie z adresu " + VlcLogSafe(u) + ": " + ex.Message); wynik = null; stan.Text = "❌ " + ex.Message; }
                if (wynik == null) { ok.IsEnabled = play.IsEnabled = url.IsEnabled = true; bar.Visibility = Visibility.Collapsed; return; }
                if (wynik == "yt") { w.Close(); return; }   // strumień / strona - pobieranie przez yt-dlp (własny postęp i komunikat)
                if (poPobraniu != null) { w.Close(); poPobraniu(wynik); return; }
                if (lib) try { var l = LoadLibrary(); if (AddToLibrary(l, new[] { wynik }, false) > 0) { SaveLibrary(l); _libRefresh?.Invoke(); } } catch (Exception ex) { App.LogError(ex); }
                ShowToast(L.T("⬇ Pobrano: ") + Path.GetFileName(wynik), wynik);
                w.Close();
            };
            w.Show(); w.Activate();
        }

        // plik wprost (HTTP, z wznawianiem) albo - gdy serwer daje stronę / listę strumienia - yt-dlp; wynik: ścieżka pliku, "yt" albo wyjątek
        async Task<string> PobierzZAdresu(string u, string login, string haslo, string plik, CancellationToken ct, Action<long, long, double> postep)
        {
            var uri = new Uri(u);
            if (uri.UserInfo.Length > 0 && login.Length == 0)   // login:hasło w linku
            {
                var ui = uri.UserInfo.Split(new[] { ':' }, 2); login = Uri.UnescapeDataString(ui[0]); if (ui.Length > 1 && haslo.Length == 0) haslo = Uri.UnescapeDataString(ui[1]);
                uri = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
            }
            using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = System.Net.DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("VLC/3.0.21 LibVLC/3.0.21");   // serwery dostawców wpuszczają odtwarzacze
                if (login.Length > 0) http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(login + ":" + haslo)));
                var part = plik + ".part"; long have = File.Exists(part) && !File.Exists(plik + ".part.stan") ? new FileInfo(part).Length : 0;   // wieloczęściowe - stan w .part.stan, nie długość pliku
                var req = new HttpRequestMessage(HttpMethod.Get, uri); if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using (var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized || res.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        throw new Exception(L.T("Serwer nie wpuszcza – sprawdź login i hasło (albo czy link jest jeszcze ważny)."));
                    if (res.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && have > 0) { File.Move(part, Gotowy(plik, null)); return Gotowy(plik, null); }   // już całe
                    res.EnsureSuccessStatusCode();
                    var typ = res.Content.Headers.ContentType?.MediaType ?? "";
                    bool lista = typ.Contains("mpegurl") || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase), strona = typ.StartsWith("text/html");
                    if (lista || strona)
                    {   // strumień HLS albo strona z filmem - yt-dlp (umie łączyć kawałki i wyciągać film ze strony)
                        if (!await EnsureYtDlpAsync()) throw new Exception(L.T("Do tego adresu potrzebne jest narzędzie yt-dlp."));
                        var a = u; if (login.Length > 0 && uri.UserInfo.Length == 0) a = new UriBuilder(uri) { UserName = Uri.EscapeDataString(login), Password = Uri.EscapeDataString(haslo) }.Uri.ToString();
                        _ = StartYtDlpAsync(a, "best", Path.GetFileNameWithoutExtension(plik), Path.GetDirectoryName(plik));
                        return "yt";
                    }
                    long caly = res.Content.Headers.ContentLength ?? -1;
                    if (have == 0 && res.StatusCode == System.Net.HttpStatusCode.OK && caly > 32L << 20 && res.Headers.AcceptRanges.Contains("bytes") || File.Exists(plik + ".part.stan"))
                    {   // ⚡ kilka połączeń naraz (serwer pozwala wznawiać od dowolnego miejsca)
                        var ext0 = Path.GetExtension(plik).Length > 0 ? null : RozszerzenieZTypu(typ, res.Content.Headers.ContentDisposition?.FileName);
                        res.Dispose();
                        if (caly <= 0) { var h = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri), ct); caly = h.Content.Headers.ContentLength ?? -1; }
                        if (caly > 0) { await Wieloczesciowo(http, uri, part, caly, ct, postep); var g = Gotowy(plik, ext0); File.Move(part, g); try { File.Delete(plik + ".part.stan"); } catch (Exception) { } return g; }
                    }
                    bool wzn = res.StatusCode == System.Net.HttpStatusCode.PartialContent;
                    if (!wzn) have = 0;
                    long total = res.Content.Headers.ContentLength is long cl ? cl + have : -1;
                    var ext = Path.GetExtension(plik).Length > 0 ? null : RozszerzenieZTypu(typ, res.Content.Headers.ContentDisposition?.FileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(plik));
                    using (var src = await res.Content.ReadAsStreamAsync(ct))
                    using (var dst = new FileStream(part, wzn ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
                    {
                        var buf = new byte[1 << 20]; long got = have, mark = got; var sw = System.Diagnostics.Stopwatch.StartNew(); double speed = 0;
                        for (int n; (n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0;)
                        {
                            await dst.WriteAsync(buf, 0, n, ct); got += n;
                            if (sw.ElapsedMilliseconds >= 500) { speed = (got - mark) * 1000.0 / sw.ElapsedMilliseconds; mark = got; sw.Restart(); postep(got, total, speed); }
                        }
                        if (total > 0 && got < total) throw new Exception(L.T("Połączenie przerwane – kliknij „Pobierz” jeszcze raz, pobieranie wznowi się."));
                    }
                    var gotowy = Gotowy(plik, ext); File.Move(part, gotowy); return gotowy;
                }
            }
        }
        // kawałki po 16 MB pobierane przez do 6 połączeń; serwer odmawia kolejnych (limit połączeń konta) - mniej połączeń, nie błąd;
        // stan kawałków w <plik>.part.stan - przerwane pobieranie wraca od miejsca, w którym stanęło
        static async Task Wieloczesciowo(HttpClient http, Uri uri, string part, long total, CancellationToken ct, Action<long, long, double> postep)
        {
            const long Kawalek = 16L << 20; int n = (int)((total + Kawalek - 1) / Kawalek);
            var done = new long[n]; var stanPlik = part.Substring(0, part.Length - 5) + ".part.stan";
            try { var t = File.ReadAllText(stanPlik).Split(';'); if (t.Length == n + 1 && long.Parse(t[0]) == total && File.Exists(part)) for (int i = 0; i < n; i++) done[i] = long.Parse(t[i + 1]); } catch (Exception) { }
            using (var f = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) if (f.Length != total) f.SetLength(total);
            void Zapisz() { lock (done) try { File.WriteAllText(stanPlik, total + ";" + string.Join(";", done)); } catch (Exception) { } }
            Zapisz();
            var kolejka = new System.Collections.Concurrent.ConcurrentQueue<int>(Enumerable.Range(0, n).Where(i => done[i] < Math.Min(Kawalek, total - i * Kawalek)));
            int aktywne = 0; Exception blad = null;
            async Task Pracownik(bool pierwszy)
            {
                Interlocked.Increment(ref aktywne);
                try
                {
                    using (var f = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 20))
                        while (kolejka.TryDequeue(out var i))
                        {
                            long od = i * Kawalek, dl = Math.Min(Kawalek, total - od); int proby = 0;
                            while (done[i] < dl)
                            {
                                ct.ThrowIfCancellationRequested();
                                try
                                {
                                    var rq = new HttpRequestMessage(HttpMethod.Get, uri); rq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(od + done[i], od + dl - 1);
                                    using (var r = await http.SendAsync(rq, HttpCompletionOption.ResponseHeadersRead, ct))
                                    {
                                        if (r.StatusCode != System.Net.HttpStatusCode.PartialContent)
                                        {   // limit połączeń - ten kawałek wraca do kolejki, połączenie znika (ostatnie zostaje i próbuje dalej)
                                            if (!pierwszy && Volatile.Read(ref aktywne) > 1) { kolejka.Enqueue(i); return; }
                                            throw new Exception(L.T("Serwer odmówił: ") + (int)r.StatusCode);
                                        }
                                        using (var src = await r.Content.ReadAsStreamAsync(ct))
                                        {
                                            var buf = new byte[1 << 18];
                                            for (int k; (k = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0;) { f.Position = od + done[i]; await f.WriteAsync(buf, 0, k, ct); lock (done) done[i] += k; }
                                        }
                                    }
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception) when (++proby < 5) { await Task.Delay(1000 * proby, ct); }
                            }
                        }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { blad = ex; }
                finally { Interlocked.Decrement(ref aktywne); }
            }
            var prac = new System.Collections.Generic.List<Task> { Pracownik(true) };
            for (int k = 1; k < Math.Min(6, n); k++) { await Task.Delay(300, ct); prac.Add(Pracownik(false)); }   // kolejne połączenia po chwili - serwer ma czas odmówić
            var wszystkie = Task.WhenAll(prac); long mark = done.Sum(); var sw = System.Diagnostics.Stopwatch.StartNew(); double speed = 0;
            while (!wszystkie.IsCompleted)
            {
                await Task.WhenAny(wszystkie, Task.Delay(500));
                long got; lock (done) got = done.Sum();
                if (sw.ElapsedMilliseconds >= 500) { speed = speed * 0.6 + (got - mark) * 1000.0 / sw.ElapsedMilliseconds * 0.4; mark = got; sw.Restart(); }
                postep(got, total, speed); Zapisz();
            }
            Zapisz();
            ct.ThrowIfCancellationRequested();
            long razem; lock (done) razem = done.Sum();
            if (razem < total) throw blad ?? new Exception(L.T("Połączenie przerwane – kliknij „Pobierz” jeszcze raz, pobieranie wznowi się."));
        }

        // wolna nazwa (bez nadpisywania) + rozszerzenie z odpowiedzi serwera, gdy nazwa go nie ma
        static string Gotowy(string plik, string ext)
        {
            var p = ext == null ? plik : plik + ext;
            for (int i = 2; File.Exists(p); i++) p = Path.Combine(Path.GetDirectoryName(plik), Path.GetFileNameWithoutExtension(plik) + " (" + i + ")" + (ext ?? Path.GetExtension(plik)));
            return p;
        }
        static string RozszerzenieZTypu(string typ, string nazwa)
        {
            var e = string.IsNullOrEmpty(nazwa) ? "" : Path.GetExtension(nazwa.Trim('"')).ToLowerInvariant();
            if (PlikiFilmow.Contains(e)) return e;
            return typ.Contains("matroska") ? ".mkv" : typ.Contains("mp4") ? ".mp4" : typ.Contains("mp2t") ? ".ts" : typ.Contains("webm") ? ".webm" : typ.Contains("avi") ? ".avi" : typ.Contains("mpeg") && typ.StartsWith("audio") ? ".mp3" : ".mp4";
        }
    }
}
