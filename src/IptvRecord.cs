using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Przegladarka
{
    // Telewizja IPTV: nagrywanie (teraz albo zaplanowane, do wybranego folderu) i automatyczne przelaczanie na program o czasie.
    // Nagranie = zapis strumienia kanalu wprost do pliku .ts (MPEG-TS albo HLS .m3u8) - bez przekodowania; Velivo musi byc wtedy wlaczone.
    public partial class MainWindow
    {
        internal sealed class IptvJob
        {
            public string Id { get; set; }
            public string Kind { get; set; }      // "rec" = nagrywanie, "switch" = przelacz na kanal
            public string Url { get; set; }
            public string Channel { get; set; }
            public List<string> Opts { get; set; } = new List<string>();
            public string Title { get; set; }     // program z EPG albo wlasny opis
            public long Start { get; set; }       // unix ms
            public long Stop { get; set; }        // unix ms (nagrywanie)
            public string Folder { get; set; }
            public bool Film { get; set; }        // "switch" filmu z biblioteki (Url = sciezka pliku)
        }

        IptvState _iptvState;
        IptvState IptvSt { get { return _iptvState ?? (_iptvState = LoadIptv()); } }

        static string DefaultRecDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Velivo TV"); } }

        readonly Dictionary<string, CancellationTokenSource> _recording = new Dictionary<string, CancellationTokenSource>();   // id zadania -> nagrywanie w toku
        DispatcherTimer _iptvTimer;
        Action _iptvJobsChanged;

        // foldery nagran (domyslny, wybrany i z zaplanowanych) - ich filmy tworza biblioteke "Nagrane z TV"
        List<string> TvRecDirs()
        {
            var st = IptvSt;
            var dirs = new List<string> { string.IsNullOrWhiteSpace(st.RecDir) ? DefaultRecDir : st.RecDir };
            dirs.AddRange(st.Jobs.Where(j => !string.IsNullOrWhiteSpace(j.Folder)).Select(j => j.Folder));
            return dirs.Select(d => { try { return Path.GetFullPath(d).TrimEnd('\\') + "\\"; } catch (Exception) { return null; } }).Where(d => d != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static bool UnderAny(string path, List<string> dirs) { return dirs.Any(d => (path ?? "").StartsWith(d, StringComparison.OrdinalIgnoreCase)); }

        static long UnixMs(DateTime local) { return new DateTimeOffset(local).ToUnixTimeMilliseconds(); }
        static DateTime LocalOf(long ms) { return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime; }

        internal static string RecordingName(string channel, string title, DateTime start)
        {
            var name = (channel ?? "TV") + (string.IsNullOrWhiteSpace(title) ? "" : " – " + title) + " " + start.ToString("yyyy-MM-dd HH.mm");
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return (name.Length > 150 ? name.Substring(0, 150) : name) + ".ts";
        }

        // ---------- harmonogram ----------

        void StartIptvScheduler()
        {
            if (_iptvTimer != null) return;
            _iptvTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _iptvTimer.Tick += (s, e) => IptvTick();
            _iptvTimer.Start();
            IptvTick();
        }

        void IptvTick()
        {
            var st = IptvSt; var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool changed = false;
            foreach (var j in st.Jobs.ToList())
            {
                if (j.Kind == "switch")
                {
                    // minute wczesniej - pytanie na srodku filmu (gdy cos leci w odtwarzaczu); bez odpowiedzi - przelacza o czasie
                    var playingNow = _vlcWin != null && _playlist.Count > _playIndex ? _playlist[_playIndex] : null;
                    if (now >= j.Start - 60000 && now < j.Start && playingNow != null && playingNow != j.Url && _vlcAsk != null && !_asked.ContainsKey(j.Id))
                    {
                        var job = j;
                        _asked[j.Id] = _vlcAsk(j.Film ? string.Format(L.T("O {0} zaplanowany film:\n🎬 {1}\n\nWłączyć teraz?"), LocalOf(j.Start).ToString("HH:mm"), j.Channel)
                            : string.Format(L.T("O {0} zaczyna się:\n📺 {1}{2}\n\nPrzełączyć na ten kanał?"), LocalOf(j.Start).ToString("HH:mm"), j.Channel, string.IsNullOrEmpty(j.Title) ? "" : " – " + j.Title),
                            () => { _asked.Remove(job.Id); IptvSt.Jobs.Remove(job); SaveIptv(IptvSt); _iptvJobsChanged?.Invoke(); SwitchNow(job); },
                            () => { _asked.Remove(job.Id); IptvSt.Jobs.Remove(job); SaveIptv(IptvSt); _iptvJobsChanged?.Invoke(); ShowToast(L.T("📺 Zostajesz przy filmie"), null); });
                    }
                    if (now < j.Start) continue;
                    st.Jobs.Remove(j); changed = true;
                    if (_asked.TryGetValue(j.Id, out var closeAsk)) { closeAsk(); _asked.Remove(j.Id); }
                    if (now - j.Start > 10 * 60000) continue;   // Velivo bylo wylaczone - po czasie nie przelaczamy
                    SwitchNow(j);
                }
                else if (j.Kind == "rec")
                {
                    if (now >= j.Stop) { if (!_recording.ContainsKey(j.Id)) { st.Jobs.Remove(j); changed = true; } continue; }
                    // minute wczesniej - powiadomienie (i ostrzezenie, gdy wlasnie leci inny kanal TV)
                    if (now >= j.Start - 75000 && now < j.Start - 30000 && _recNotified.Add(j.Id))
                    {
                        ShowToast(L.T("⏺ Za minutę nagrywanie: ") + j.Channel + (string.IsNullOrEmpty(j.Title) ? "" : " – " + j.Title), null);
                        WarnOneConnection();
                    }
                    if (now >= j.Start - 30000 && !_recording.ContainsKey(j.Id)) StartRecording(j);
                }
            }
            if (changed) { SaveIptv(st); _iptvJobsChanged?.Invoke(); }
        }

        readonly Dictionary<string, Action> _asked = new Dictionary<string, Action>();   // pytanie o przelaczenie na ekranie (id zadania -> zamknij)
        Func<string, Action, Action, Action> _vlcAsk;   // odtwarzacz: pytanie na srodku obrazu (tekst, tak, nie) -> zamknij

        sealed class TvResume { public int Idx { get; set; } public List<TvResumeCh> Chans { get; set; } = new List<TvResumeCh>(); }
        sealed class TvResumeCh { public string Name { get; set; } public string Url { get; set; } public string Group { get; set; } public string Logo { get; set; } public string TvgId { get; set; } public List<string> Opts { get; set; } }
        string TvResumeFile => Path.Combine(IptvDataDir, "wznow.json");

        // lista kanalow, z ktorej ogladano, i biezacy kanal (-1 = bez wznawiania)
        void SaveTvResume(int idx)
        {
            try
            {
                if (idx < 0) { if (File.Exists(TvResumeFile)) File.Delete(TvResumeFile); return; }
                var r = new TvResume { Idx = idx };
                foreach (var u in _playlist) if (_iptvPlaying.TryGetValue(u, out var c)) r.Chans.Add(new TvResumeCh { Name = c.Name, Url = c.Url, Group = c.Group, Logo = c.Logo, TvgId = c.TvgId, Opts = c.Opts });
                Directory.CreateDirectory(IptvDataDir); File.WriteAllText(TvResumeFile, JsonSerializer.Serialize(r));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void ResumeTv()
        {
            try
            {
                if (!IptvInstalled || _vlcWin != null || !File.Exists(TvResumeFile)) return;
                var r = JsonSerializer.Deserialize<TvResume>(File.ReadAllText(TvResumeFile));
                if (r?.Chans == null || r.Chans.Count == 0) return;
                _iptvPlaying.Clear();
                foreach (var c in r.Chans) _iptvPlaying[c.Url] = new IptvChannel { Name = c.Name, Url = c.Url, Group = c.Group ?? "", Logo = c.Logo, TvgId = c.TvgId, Opts = c.Opts ?? new List<string>() };
                PlayVideos(r.Chans.Select(c => c.Url).ToArray(), r.Idx);
                ShowToast(L.T("📺 Wznawiam: ") + r.Chans[Math.Max(0, Math.Min(r.Idx, r.Chans.Count - 1))].Name, null);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // lista kanalow bez otwierania telewizji: wczytana albo z pamieci podrecznej ostatniego zrodla
        IptvPlaylist _knownPl;
        IptvPlaylist KnownPlaylist()
        {
            if (_iptvPl != null) return _iptvPl;
            if (_knownPl != null) return _knownPl;   // raz na uruchomienie - duza lista nie spowalnia menu
            var src = IptvSt.Sources.FirstOrDefault(x => x.Id == IptvSt.Last) ?? IptvSt.Sources.FirstOrDefault(); var f = src != null ? IptvCache(src, ".m3u") : null;
            if (f != null && File.Exists(f)) try { _knownPl = ParseM3u(File.ReadAllText(f)); ApplyEpgMap(_knownPl); return _knownPl; } catch (Exception) { }
            return null;
        }

        // kanal wlaczony bez okna TV (z menu odtwarzacza, harmonogramu): lista i program TV z pamieci podrecznej w tle, bez pobierania z sieci
        bool _iptvBgLoading;
        async void LoadIptvInBackground(Action done)
        {
            if (_iptvEpg != null || _iptvBgLoading || !IptvInstalled) return;
            var src = IptvSt.Sources.FirstOrDefault(x => x.Id == IptvSt.Last) ?? IptvSt.Sources.FirstOrDefault(); if (src == null) return;
            var m3u = IptvCache(src, ".m3u"); var epgFile = IptvCache(src, ".epg");
            _iptvBgLoading = true;
            try
            {
                if (_iptvPl == null && File.Exists(m3u)) { var t = _knownPl ?? await Task.Run(() => ParseM3u(File.ReadAllText(m3u))); ApplyEpgMap(t); if (_iptvPl == null) _iptvPl = t; }
                if (_iptvEpg == null && File.Exists(epgFile))
                {
                    var ic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var e = await Task.Run(() => { using (var fs = File.OpenRead(epgFile)) return ParseXmltv(fs, EpgFrom(_iptvPl, DateTime.Now), DateTime.Now.AddHours(30), ic); });
                    if (_iptvEpg == null) { _iptvEpg = e; _iptvIcons = ic; }   // okno TV moglo w miedzyczasie wczytac swoje
                }
                _iptvLoaded?.Invoke(); done?.Invoke();
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { _iptvBgLoading = false; }
        }

        // zaplanowane wlaczenie o godzinie: film z biblioteki albo kanal TV (zwykle okno, lista z wyszukiwaniem, data i godzina)
        void ShowScheduleStart(Window owner)
        {
            var w = new Window { Title = L.T("⏰ Zaplanuj włączenie"), Width = 560, Height = 640, Owner = owner, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            Independent(w);
            var films = LoadLibrary().Where(v => v.Peer == null && File.Exists(v.Path)).Select(v => (Name: ShownTitle(v, MetaFor(v.Path)), Url: v.Path, Opts: (List<string>)null))
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var chans = new List<(string Name, string Url, List<string> Opts)>();
            if (IptvInstalled)
            {
                var pl = KnownPlaylist();
                if (pl != null) chans = pl.Channels.Select(c => (c.Name, c.Url, c.Opts)).ToList();
            }
            var rbFilm = new RadioButton { Content = L.T("🎬 Film"), IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
            var rbTv = new RadioButton { Content = L.T("📺 Kanał TV"), IsEnabled = chans.Count > 0 };
            var kind = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 12, 12, 6) }; kind.Children.Add(rbFilm); kind.Children.Add(rbTv);
            var search = new TextBox { Margin = new Thickness(12, 0, 12, 6), Padding = new Thickness(4) };
            var list = new ListBox { Margin = new Thickness(12, 0, 12, 6) };
            Action fill = () =>
            {
                var q = search.Text.Trim(); list.Items.Clear();
                foreach (var x in (rbFilm.IsChecked == true ? films : chans).Where(x => q.Length == 0 || x.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0).Take(500))
                    list.Items.Add(new ListBoxItem { Content = x.Name, Tag = x });
            };
            search.TextChanged += (s, e) => fill(); rbFilm.Checked += (s, e) => fill(); rbTv.Checked += (s, e) => fill();
            var when = DateTime.Now.AddHours(1); when = new DateTime(when.Year, when.Month, when.Day, when.Hour, 0, 0);
            var date = new DatePicker { SelectedDate = when.Date, Width = 140, Margin = new Thickness(0, 0, 8, 0) };
            var time = new TextBox { Text = when.ToString("HH:mm"), Width = 70, Padding = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };
            var at = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 6) };
            at.Children.Add(new TextBlock { Text = L.T("Kiedy:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }); at.Children.Add(date); at.Children.Add(time);
            var ok = SmallButton(L.T("⏰ Zaplanuj"), () =>
            {
                if (!((list.SelectedItem as ListBoxItem)?.Tag is ValueTuple<string, string, List<string>> x)) { InfoBox(w, L.T("Wybierz film albo kanał z listy.")); return; }
                if (!TimeSpan.TryParse(time.Text.Trim(), out var t) || date.SelectedDate == null) { InfoBox(w, L.T("Podaj godzinę w formacie GG:MM, np. 20:15.")); return; }
                var start = date.SelectedDate.Value.Date + t;
                if (start < DateTime.Now) { InfoBox(w, L.T("Ta godzina już minęła.")); return; }
                AddJob(new IptvJob { Kind = "switch", Film = rbFilm.IsChecked == true, Url = x.Item2, Channel = x.Item1, Opts = x.Item3 ?? new List<string>(), Start = UnixMs(start) });
                ShowToast("⏰ " + start.ToString("dd.MM HH:mm") + "  " + (rbFilm.IsChecked == true ? "🎬 " : "📺 ") + x.Item1, null);
                w.Close();
            });
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
            bar.Children.Add(SmallButton(L.T("📅 Zaplanowane…"), () => ShowJobs(w))); bar.Children.Add(ok);
            var dock = new DockPanel();
            foreach (var el in new UIElement[] { kind, search }) { DockPanel.SetDock(el, Dock.Top); dock.Children.Add(el); }
            DockPanel.SetDock(bar, Dock.Bottom); dock.Children.Add(bar);
            DockPanel.SetDock(at, Dock.Bottom); dock.Children.Add(at);
            dock.Children.Add(list);
            w.Content = dock;
            list.MouseDoubleClick += (s, e) => ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            fill(); w.Show();
        }

        void SwitchNow(IptvJob j)
        {
            if (j.Film) { PlayVideos(new[] { j.Url }, 0); ShowToast("🎬 " + j.Channel, null); return; }   // zaplanowany film
            var ch = new IptvChannel { Name = j.Channel, Url = j.Url, Opts = j.Opts ?? new List<string>() };
            _iptvPlaying[ch.Url] = ch;
            PlayVideos(new[] { ch.Url }, 0);   // film zapisuje miejsce, w ktorym przerwano
            ShowToast("📺 " + j.Channel + (string.IsNullOrEmpty(j.Title) ? "" : " – " + j.Title), null);
        }

        readonly HashSet<string> _recNotified = new HashSet<string>();
        DateTime _connWarned = DateTime.MinValue;

        // ogladanie kanalu TV i nagrywanie naraz = dwa polaczenia z dostawca - wielu pozwala tylko na jedno
        void WarnOneConnection()
        {
            var playing = _vlcWin != null && _playlist.Count > _playIndex && _iptvPlaying.ContainsKey(_playlist[_playIndex]);
            if (!playing && _recording.Count == 0) return;
            if (DateTime.Now - _connWarned < TimeSpan.FromMinutes(30)) return;   // nie czesciej niz co pol godziny
            _connWarned = DateTime.Now;
            ShowToast(L.T("⚠ Oglądanie i nagrywanie naraz to dwa połączenia z dostawcą IPTV. Jeśli pozwala na jedno, jedno z nich może się przerwać."), null);
        }

        readonly Dictionary<string, string> _recFile = new Dictionary<string, string>();   // id nagrania w toku -> plik

        // plik nagrywanego teraz kanalu (do ogladania z nagrania); null = nie nagrywa sie
        string RecFileFor(string url)
        {
            var j = IptvSt.Jobs.FirstOrDefault(x => x.Kind == "rec" && x.Url == url && _recording.ContainsKey(x.Id) && _recFile.ContainsKey(x.Id));
            return j != null && File.Exists(_recFile[j.Id]) && new FileInfo(_recFile[j.Id]).Length > 512 * 1024 ? _recFile[j.Id] : null;
        }

        bool IsRecording(string url) { return IptvSt.Jobs.Any(j => j.Kind == "rec" && j.Url == url && _recording.ContainsKey(j.Id)); }

        void StartRecording(IptvJob j)
        {
            var cts = new CancellationTokenSource();
            _recording[j.Id] = cts;
            var dir = string.IsNullOrWhiteSpace(j.Folder) ? DefaultRecDir : j.Folder;
            var file = Path.Combine(dir, RecordingName(j.Channel, j.Title, DateTime.Now));
            _recFile[j.Id] = file;
            if (_vlcWin != null && _playlist.Count > _playIndex && _iptvPlaying.ContainsKey(_playlist[_playIndex])) WarnOneConnection();
            ShowToast(L.T("⏺ Nagrywam: ") + j.Channel + (string.IsNullOrEmpty(j.Title) ? "" : " – " + j.Title), null);
            _iptvJobsChanged?.Invoke();
            Func<DateTime> until = () => LocalOf(j.Stop);   // "Przedluz" zmienia j.Stop w trakcie nagrywania
            Task.Run(() => RecordStream(j.Url, j.Opts, file, until, cts.Token)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                _recording.Remove(j.Id); _recFile.Remove(j.Id);
                IptvSt.Jobs.RemoveAll(x => x.Id == j.Id); SaveIptv(IptvSt);
                _iptvJobsChanged?.Invoke();
                var err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                if (File.Exists(file)) { var l = LoadLibrary(); if (AddToLibrary(l, new[] { file }, false) > 0) { SaveLibrary(l); _libRefresh?.Invoke(); } }   // biblioteka "Nagrane z TV"
                if (err != null && !File.Exists(file)) ShowToast(L.T("⚠ Nagrywanie nie udało się: ") + j.Channel + " – " + err, null);
                else ShowToast(L.T("✅ Nagranie zapisane: ") + Path.GetFileName(file) + (err != null ? " (" + err + ")" : ""), file);
            })));
        }

        void StopRecording(string url)
        {
            foreach (var j in IptvSt.Jobs.Where(x => x.Kind == "rec" && x.Url == url).ToList())
                if (_recording.TryGetValue(j.Id, out var c)) c.Cancel();
        }

        void StopAllRecordings() { foreach (var c in _recording.Values) c.Cancel(); }

        // ---------- zapis strumienia ----------

        static HttpClient StreamClient(List<string> opts)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var ua = opts?.FirstOrDefault(o => o.StartsWith(":http-user-agent="))?.Substring(17);
            var re = opts?.FirstOrDefault(o => o.StartsWith(":http-referrer="))?.Substring(15);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", string.IsNullOrEmpty(ua) ? "VLC/3.0.24 LibVLC/3.0.24" : ua);
            if (!string.IsNullOrEmpty(re)) http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", re);
            return http;
        }

        // null = w porzadku; tekst = powod przerwania (plik moze byc czesciowy)
        internal static Task<string> RecordStream(string url, List<string> opts, string file, DateTime until, CancellationToken ct) { return RecordStream(url, opts, file, () => until, ct); }

        internal static async Task<string> RecordStream(string url, List<string> opts, string file, Func<DateTime> until, CancellationToken ct)
        {
            if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                return L.T("nagrywanie działa dla kanałów http/https");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            using (var http = StreamClient(opts))
            using (var outFs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                string lastErr = null; int fails = 0;
                while (DateTime.Now < until() && !ct.IsCancellationRequested)
                {
                    try
                    {
                        bool hls = url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!hls)
                        {
                            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                            {
                                resp.EnsureSuccessStatusCode();
                                var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
                                if (ctype.Contains("mpegurl")) hls = true;
                                else
                                {
                                    using (var s = await resp.Content.ReadAsStreamAsync())
                                        await CopyUntil(s, outFs, until, ct);
                                }
                            }
                        }
                        if (hls) { var e = await RecordHls(http, url, outFs, until, ct); if (e != null) return e; }
                        fails = 0;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { lastErr = ex.Message; if (++fails >= 20) return lastErr; }
                    if (DateTime.Now < until() && !ct.IsCancellationRequested) { try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; } }   // zerwane polaczenie - ponownie
                }
                return outFs.Length == 0 ? (lastErr ?? L.T("brak danych")) : null;
            }
        }

        static async Task CopyUntil(Stream s, Stream outFs, Func<DateTime> until, CancellationToken ct)
        {
            var buf = new byte[65536]; int n;
            while (DateTime.Now < until() && !ct.IsCancellationRequested && (n = await s.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                await outFs.WriteAsync(buf, 0, n, ct);
        }

        // HLS: lista segmentow odswiezana co chwile, nowe segmenty dopisywane po kolei (lista glowna - najlepsza jakosc)
        static async Task<string> RecordHls(HttpClient http, string url, Stream outFs, Func<DateTime> until, CancellationToken ct)
        {
            var seen = new HashSet<string>();
            var baseUri = new Uri(url);
            while (DateTime.Now < until() && !ct.IsCancellationRequested)
            {
                var text = await http.GetStringAsync(baseUri, ct);
                var pl = ParseHls(text, baseUri);
                if (pl.Encrypted) return L.T("kanał jest zaszyfrowany (HLS z kluczem) – nie da się go nagrać");
                if (pl.Variant != null) { baseUri = pl.Variant; continue; }
                foreach (var seg in pl.Segments)
                {
                    if (DateTime.Now >= until() || ct.IsCancellationRequested) break;
                    if (!seen.Add(seg.AbsoluteUri)) continue;
                    using (var resp = await http.GetAsync(seg, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        if (!resp.IsSuccessStatusCode) continue;
                        using (var s = await resp.Content.ReadAsStreamAsync()) await CopyUntil(s, outFs, () => DateTime.MaxValue, ct);
                    }
                }
                if (pl.Ended) break;   // nagranie (VOD) - koniec listy
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, pl.Target / 2)), ct);
            }
            return null;
        }

        internal sealed class HlsList { public Uri Variant; public List<Uri> Segments = new List<Uri>(); public bool Encrypted, Ended; public double Target = 6; }

        internal static HlsList ParseHls(string text, Uri baseUri)
        {
            var r = new HlsList();
            long bestBw = -1; bool nextIsVariant = false; long bw = 0;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                {
                    nextIsVariant = true; bw = 0;
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"BANDWIDTH=(\d+)");
                    if (m.Success) long.TryParse(m.Groups[1].Value, out bw);
                }
                else if (line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase) && line.IndexOf("METHOD=NONE", StringComparison.OrdinalIgnoreCase) < 0) r.Encrypted = true;
                else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase)) r.Ended = true;
                else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase)) double.TryParse(line.Substring(22), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r.Target);
                else if (!line.StartsWith("#"))
                {
                    var u = new Uri(baseUri, line);
                    if (nextIsVariant) { if (bw > bestBw) { bestBw = bw; r.Variant = u; } nextIsVariant = false; }
                    else r.Segments.Add(u);
                }
            }
            if (r.Segments.Count > 0) r.Variant = null;
            return r;
        }

        // ---------- dodawanie zadan ----------

        void AddJob(IptvJob j)
        {
            j.Id = Guid.NewGuid().ToString("N").Substring(0, 12);
            IptvSt.Jobs.Add(j); SaveIptv(IptvSt);
            _iptvJobsChanged?.Invoke();
            IptvTick();
        }

        void RecordNow(IptvChannel ch, Window owner)
        {
            AddJob(new IptvJob { Kind = "rec", Url = ch.Url, Channel = ch.Name, Opts = ch.Opts, Start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Stop = UnixMs(DateTime.Now.AddHours(4)), Folder = IptvSt.RecDir });
        }

        void RecordProgramme(IptvChannel ch, EpgItem p)
        {
            AddJob(new IptvJob { Kind = "rec", Url = ch.Url, Channel = ch.Name, Opts = ch.Opts, Title = p.Title, Start = UnixMs(p.Start.AddMinutes(-2)), Stop = UnixMs(p.Stop.AddMinutes(5)), Folder = IptvSt.RecDir });   // zapas na przesuniecia w programie
            ShowToast(L.T("⏺ Zaplanowano nagranie: ") + p.Start.ToString("dd.MM HH:mm") + " " + p.Title, null);
        }

        void SwitchAt(IptvChannel ch, EpgItem p)
        {
            AddJob(new IptvJob { Kind = "switch", Url = ch.Url, Channel = ch.Name, Opts = ch.Opts, Title = p.Title, Start = UnixMs(p.Start) });
            ShowToast(L.T("⏰ Przełączę na: ") + ch.Name + " – " + p.Start.ToString("HH:mm") + " " + p.Title, null);
        }

        // wlasne nagrywanie: kanal, od - do, folder
        void ScheduleDialog(IptvChannel ch, Window owner)
        {
            var d = new Window { Title = L.T("⏺ Zaplanuj nagrywanie – ") + ch.Name, Width = 460, SizeToContent = SizeToContent.Height, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var p = new StackPanel { Margin = new Thickness(12) };
            Func<string, string, TextBox> field = (label, value) => { p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) }); var t = new TextBox { Text = value, Padding = new Thickness(4) }; p.Children.Add(t); return t; };
            var start = DateTime.Now.AddMinutes(1);
            var title = field(L.T("Opis (do nazwy pliku):"), "");
            var from = field(L.T("Początek (dd.MM.rrrr gg:mm):"), start.ToString("dd.MM.yyyy HH:mm"));
            var to = field(L.T("Koniec (dd.MM.rrrr gg:mm):"), start.AddHours(1).ToString("dd.MM.yyyy HH:mm"));
            var folder = field(L.T("Folder nagrań:"), string.IsNullOrWhiteSpace(IptvSt.RecDir) ? DefaultRecDir : IptvSt.RecDir);
            var pick = SmallButton(L.T("📁 Wybierz folder…"), () => { var f = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(folder.Text) ? folder.Text : null }; if (f.ShowDialog(d) == true) folder.Text = f.FolderName; });
            pick.HorizontalAlignment = HorizontalAlignment.Left; pick.Margin = new Thickness(0, 4, 0, 0);
            p.Children.Add(pick);
            var ok = new Button { Content = "OK", Width = 90, Height = 28, IsDefault = true, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            ok.Click += (s, a) => d.DialogResult = true;
            p.Children.Add(ok);
            d.Content = p;
            if (d.ShowDialog() != true) return;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (!DateTime.TryParseExact(from.Text.Trim(), "dd.MM.yyyy HH:mm", ci, System.Globalization.DateTimeStyles.None, out var a1) ||
                !DateTime.TryParseExact(to.Text.Trim(), "dd.MM.yyyy HH:mm", ci, System.Globalization.DateTimeStyles.None, out var a2) || a2 <= a1 || a2 <= DateTime.Now)
            { InfoBox(owner, L.T("Sprawdź czas początku i końca (koniec musi być później niż początek i w przyszłości).")); return; }
            IptvSt.RecDir = folder.Text.Trim();
            AddJob(new IptvJob { Kind = "rec", Url = ch.Url, Channel = ch.Name, Opts = ch.Opts, Title = title.Text.Trim(), Start = UnixMs(a1), Stop = UnixMs(a2), Folder = IptvSt.RecDir });
            ShowToast(L.T("⏺ Zaplanowano nagranie: ") + a1.ToString("dd.MM HH:mm") + "–" + a2.ToString("HH:mm") + " " + ch.Name, null);
        }

        // lista zaplanowanych: nagrania i przelaczenia, usuwanie / zatrzymanie
        void ShowJobs(Window owner)
        {
            var w = new Window { Title = L.T("📅 Zaplanowane"), Width = 640, Height = 360, Owner = owner, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            Independent(w);   // zwykle okno Windows - nie przykrywa innych na stale
            var grid = new GridView();
            foreach (var c in new[] { new[] { "What", "Co", "70" }, new[] { "When", "Kiedy", "170" }, new[] { "Channel", "Kanał", "160" }, new[] { "Title", "Program", "220" } })
                grid.Columns.Add(new GridViewColumn { Header = L.T(c[1]), Width = double.Parse(c[2]), DisplayMemberBinding = new System.Windows.Data.Binding(c[0]) });
            var list = new ListView { View = grid, Margin = new Thickness(8) };
            Action sortJobs = null;
            Action fill = () =>
            {
                list.ItemsSource = IptvSt.Jobs.OrderBy(j => j.Start).Select(j => new
                {
                    Job = j, Channel = j.Channel, Title = j.Title, WhenSort = j.Start,
                    What = j.Kind == "switch" ? "⏰" : _recording.ContainsKey(j.Id) ? L.T("⏺ teraz") : "⏺",
                    When = LocalOf(j.Start).ToString("dd.MM HH:mm") + (j.Kind == "rec" ? "–" + LocalOf(j.Stop).ToString("HH:mm") : ""),
                }).ToList();
                sortJobs?.Invoke();
            };
            sortJobs = SortableList(list, "zaplanowane");
            fill();
            Func<IptvJob> selJob = () => { var it = list.SelectedItem; return it == null ? null : (IptvJob)it.GetType().GetProperty("Job").GetValue(it); };
            // przedluzenie nagrania (takze w trakcie) - np. mecz z dogrywka
            Action<int> extend = min => { var j = selJob(); if (j == null || j.Kind != "rec") return; j.Stop += min * 60000L; SaveIptv(IptvSt); fill(); ShowToast(L.T("⏺ Nagranie do ") + LocalOf(j.Stop).ToString("HH:mm") + " – " + j.Channel, null); };
            Action remove = () =>
            {
                var it = list.SelectedItem; if (it == null) return;
                var j = (IptvJob)it.GetType().GetProperty("Job").GetValue(it);
                if (_recording.TryGetValue(j.Id, out var c)) c.Cancel();   // nagrywanie w toku - zatrzymane, plik zostaje
                else { IptvSt.Jobs.Remove(j); SaveIptv(IptvSt); }
                fill();
            };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            bar.Children.Add(SmallButton(L.T("❌ Odwołaj / zatrzymaj"), remove));
            list.PreviewKeyDown += (s, a) => { if (a.Key == System.Windows.Input.Key.Delete) { remove(); a.Handled = true; } };
            var cm = new ContextMenu();
            cm.Opened += (s, a) =>
            {
                cm.Items.Clear(); var j = selJob();
                Action<string, Action> add = (t, act) => { var mi = new MenuItem { Header = t, IsEnabled = j != null }; mi.Click += (q, e) => act(); cm.Items.Add(mi); };
                add(L.T("❌ Odwołaj (nagrywanie w toku – zatrzymaj)"), remove);
                if (j == null || j.Kind == "rec") { add(L.T("⏩ Przedłuż nagranie o 15 min"), () => extend(15)); add(L.T("⏩ Przedłuż nagranie o 30 min"), () => extend(30)); add(L.T("⏩ Przedłuż nagranie o 1 godz."), () => extend(60)); }
            };
            list.ContextMenu = cm;
            bar.Children.Add(SmallButton(L.T("📁 Folder nagrań"), OpenRecDir));
            var body = new DockPanel();
            DockPanel.SetDock(bar, Dock.Bottom); body.Children.Add(bar);
            body.Children.Add(new TextBlock { Text = L.T("Velivo musi być włączone o czasie nagrania lub przełączenia."), Foreground = Brushes.Gray, Margin = new Thickness(10, 8, 10, 0) });
            DockPanel.SetDock((UIElement)body.Children[1], Dock.Top);
            body.Children.Add(list);
            w.Content = body;
            Action onJobs = () => { if (w.IsLoaded) fill(); };
            _iptvJobsChanged += onJobs;
            w.Closed += (s, a) => _iptvJobsChanged -= onJobs;
            w.Show();
        }
    
        // wybor folderu nagran (nowe nagrania; stare zostaja tam, gdzie sa - i dalej sa w bibliotece "Nagrane z TV")
        void ChooseRecDir(Window owner)
        {
            var cur = string.IsNullOrWhiteSpace(IptvSt.RecDir) ? DefaultRecDir : IptvSt.RecDir;
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Folder nagrań telewizji") };
            if (Directory.Exists(cur)) dlg.InitialDirectory = cur;
            if (dlg.ShowDialog(owner) != true) return;
            var old = IptvSt.RecDir;
            IptvSt.RecDir = string.Equals(dlg.FolderName.TrimEnd('\\'), DefaultRecDir, StringComparison.OrdinalIgnoreCase) ? null : dlg.FolderName;
            // zaplanowane nagrania do starego folderu - do nowego
            foreach (var j in IptvSt.Jobs.Where(j => j.Kind == "rec" && string.Equals(j.Folder ?? "", old ?? "", StringComparison.OrdinalIgnoreCase) && !_recording.ContainsKey(j.Id))) j.Folder = IptvSt.RecDir;
            SaveIptv(IptvSt);
            ShowToast(L.T("📁 Nowe nagrania w: ") + (IptvSt.RecDir ?? DefaultRecDir), null);
        }

        void OpenRecDir()
        {
            var dir = string.IsNullOrWhiteSpace(IptvSt.RecDir) ? DefaultRecDir : IptvSt.RecDir;
            try { Directory.CreateDirectory(dir); System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\""); } catch (Exception ex) { App.LogError(ex); }
        }

        // nagrania z folderu nagran: odtworz, usun (z dysku i z biblioteki filmow), otworz folder
        void ShowRecordings(Window owner)
        {
            var w = new Window { Title = L.T("🎞 Nagrania TV"), Width = 760, Height = 420, Owner = owner, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            Independent(w);   // zwykle okno Windows - nie przykrywa innych na stale
            var grid = new GridView();
            foreach (var c in new[] { new[] { "Name", "Nagranie", "460" }, new[] { "Date", "Data", "130" }, new[] { "Size", "Rozmiar", "90" } })
                grid.Columns.Add(new GridViewColumn { Header = L.T(c[1]), Width = double.Parse(c[2]), DisplayMemberBinding = new System.Windows.Data.Binding(c[0]) });
            var list = new ListView { View = grid, Margin = new Thickness(8), SelectionMode = SelectionMode.Extended };
            RememberColumns(w, "nagrania", grid);
            var count = new TextBlock { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Action sortRec = null;
            Action fill = () =>
            {
                var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.IsNullOrWhiteSpace(IptvSt.RecDir) ? DefaultRecDir : IptvSt.RecDir };
                foreach (var j in IptvSt.Jobs) if (!string.IsNullOrWhiteSpace(j.Folder)) dirs.Add(j.Folder);
                var files = dirs.Where(Directory.Exists).SelectMany(d => { try { return Directory.GetFiles(d, "*.ts"); } catch (Exception) { return new string[0]; } })
                    .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).ToList();
                list.ItemsSource = files.Select(f => new { Path = f.FullName, Name = Path.GetFileNameWithoutExtension(f.Name), Date = f.LastWriteTime.ToString("dd.MM.yyyy HH:mm"), DateSort = f.LastWriteTime, Size = (f.Length / 1048576.0).ToString("0") + " MB", SizeSort = f.Length }).ToList();
                sortRec?.Invoke();
                count.Text = files.Count + L.T(" nagrań");
            };
            sortRec = SortableList(list, "nagrania");
            Func<List<string>> sel = () => list.SelectedItems.Cast<object>().Select(o => (string)o.GetType().GetProperty("Path").GetValue(o)).ToList();
            Action play = () => { var p = sel(); if (p.Count > 0) PlayVideos(p.ToArray(), 0); };
            Action del = () =>
            {
                var p = sel(); if (p.Count == 0) return;
                if (!AskYes(w, string.Format(L.T("Usunąć {0} nagrań z dysku (do Kosza) i z biblioteki filmów?"), p.Count))) return;
                var errs = DeleteVideoFiles(p);
                fill();
                if (errs.Count > 0) InfoBox(w, L.T("Nie udało się usunąć (nagrywane albo odtwarzane):\n") + string.Join("\n", errs.Take(10)));
            };
            list.MouseDoubleClick += (s, a) => play();
            list.PreviewKeyDown += (s, a) => { if (a.Key == System.Windows.Input.Key.Enter) { play(); a.Handled = true; } else if (a.Key == System.Windows.Input.Key.Delete) { del(); a.Handled = true; } };
            var menu = new ContextMenu();
            foreach (var it in new[] { Tuple.Create(L.T("▶ Odtwórz"), play), Tuple.Create(L.T("🗑 Usuń z dysku i z biblioteki…"), del), Tuple.Create(L.T("📁 Otwórz folder nagrań"), (Action)OpenRecDir) })
            { var mi = new MenuItem { Header = it.Item1 }; var act = it.Item2; mi.Click += (s, a) => act(); menu.Items.Add(mi); }
            list.ContextMenu = menu;
            var bar = new DockPanel { Margin = new Thickness(6), LastChildFill = false };
            DockPanel.SetDock(count, Dock.Left); bar.Children.Add(count);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right); bar.Children.Add(buttons);
            buttons.Children.Add(SmallButton(L.T("▶ Odtwórz"), play));
            buttons.Children.Add(SmallButton(L.T("🗑 Usuń z dysku"), del));
            buttons.Children.Add(SmallButton(L.T("📁 Folder nagrań"), OpenRecDir));
            var body = new DockPanel();
            DockPanel.SetDock(bar, Dock.Bottom); body.Children.Add(bar);
            body.Children.Add(list);
            w.Content = body;
            fill();
            w.Show();
        }

        // zaplanowane dla tej audycji: przelaczenie albo nagranie (null = brak)
        IptvJob JobFor(IptvChannel ch, EpgItem x, string kind)
        {
            if (ch == null || x == null) return null;
            return IptvSt.Jobs.FirstOrDefault(j => j.Url == ch.Url && j.Kind == kind &&
                (kind == "switch" ? j.Start == UnixMs(x.Start) :
                 j.Start < UnixMs(x.Stop) && j.Stop >= UnixMs(x.Stop) && (j.Stop <= UnixMs(x.Stop.AddMinutes(10)) || j.Start <= UnixMs(x.Start))));   // takze nagranie "od teraz do konca" trwajacej audycji
        }

        // odwolanie: nagrywanie w toku - zatrzymane (plik zostaje), zaplanowane - usuniete
        void CancelJob(IptvJob j)
        {
            if (j == null) return;
            if (_recording.TryGetValue(j.Id, out var c)) { c.Cancel(); return; }
            if (_asked.TryGetValue(j.Id, out var closeAsk)) { closeAsk(); _asked.Remove(j.Id); }
            IptvSt.Jobs.Remove(j); SaveIptv(IptvSt); _iptvJobsChanged?.Invoke();
            ShowToast(j.Kind == "switch" ? L.T("⏰ Przełączenie odwołane: ") + j.Channel : L.T("⏺ Nagranie odwołane: ") + j.Channel, null);
        }

        // "⏰ 2 przełączenia · ⏺ 1 nagranie" (pusty, gdy nic nie zaplanowano)
        internal string JobsSummary()
        {
            int sw = IptvSt.Jobs.Count(j => j.Kind == "switch"), rec = IptvSt.Jobs.Count(j => j.Kind == "rec");
            var parts = new List<string>();
            if (sw > 0) parts.Add("⏰ " + L.T("zaplanowane przełączenia: ") + sw);
            if (rec > 0) parts.Add("⏺ " + L.T("nagrania: ") + rec + (_recording.Count > 0 ? L.T(" (teraz: ") + _recording.Count + ")" : ""));
            return string.Join("  ·  ", parts);
        }

        // przycisk "📅 ..." z liczba zaplanowanych - klik otwiera liste (odwolywanie); znika, gdy nic nie ma
        Button JobsBadge(Window owner)
        {
            var b = SmallButton("", () => ShowJobs(owner));
            b.Margin = new Thickness(10, 8, 0, 4);
            b.ToolTip = L.T("Zaplanowane przełączenia i nagrania – kliknij, aby zobaczyć lub odwołać");
            Action upd = () => { var t = JobsSummary(); b.Content = "📅 " + t; b.Visibility = t.Length == 0 ? Visibility.Collapsed : Visibility.Visible; };
            upd();
            _iptvJobsChanged += upd;
            owner.Closed += (s, a) => _iptvJobsChanged -= upd;
            return b;
        }
}
}
