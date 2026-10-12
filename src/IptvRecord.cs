using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
        }

        IptvState _iptvState;
        IptvState IptvSt { get { return _iptvState ?? (_iptvState = LoadIptv()); } }

        static string DefaultRecDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Velivo TV"); } }

        readonly Dictionary<string, CancellationTokenSource> _recording = new Dictionary<string, CancellationTokenSource>();   // id zadania -> nagrywanie w toku
        DispatcherTimer _iptvTimer;
        Action _iptvJobsChanged;

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
                    if (now < j.Start) continue;
                    st.Jobs.Remove(j); changed = true;
                    if (now - j.Start > 10 * 60000) continue;   // Velivo bylo wylaczone - po czasie nie przelaczamy
                    var ch = new IptvChannel { Name = j.Channel, Url = j.Url, Opts = j.Opts ?? new List<string>() };
                    _iptvPlaying[ch.Url] = ch;
                    PlayVideos(new[] { ch.Url }, 0);
                    ShowToast("📺 " + j.Channel + (string.IsNullOrEmpty(j.Title) ? "" : " – " + j.Title), null);
                }
                else if (j.Kind == "rec")
                {
                    if (now >= j.Stop) { if (!_recording.ContainsKey(j.Id)) { st.Jobs.Remove(j); changed = true; } continue; }
                    if (now >= j.Start - 30000 && !_recording.ContainsKey(j.Id)) StartRecording(j);
                }
            }
            if (changed) { SaveIptv(st); _iptvJobsChanged?.Invoke(); }
        }

        bool IsRecording(string url) { return IptvSt.Jobs.Any(j => j.Kind == "rec" && j.Url == url && _recording.ContainsKey(j.Id)); }

        void StartRecording(IptvJob j)
        {
            var cts = new CancellationTokenSource();
            _recording[j.Id] = cts;
            var dir = string.IsNullOrWhiteSpace(j.Folder) ? DefaultRecDir : j.Folder;
            var file = Path.Combine(dir, RecordingName(j.Channel, j.Title, DateTime.Now));
            ShowToast(L.T("⏺ Nagrywam: ") + j.Channel + (string.IsNullOrEmpty(j.Title) ? "" : " – " + j.Title), null);
            _iptvJobsChanged?.Invoke();
            var until = LocalOf(j.Stop);
            Task.Run(() => RecordStream(j.Url, j.Opts, file, until, cts.Token)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                _recording.Remove(j.Id);
                IptvSt.Jobs.RemoveAll(x => x.Id == j.Id); SaveIptv(IptvSt);
                _iptvJobsChanged?.Invoke();
                var err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
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
        internal static async Task<string> RecordStream(string url, List<string> opts, string file, DateTime until, CancellationToken ct)
        {
            if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                return L.T("nagrywanie działa dla kanałów http/https");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            using (var http = StreamClient(opts))
            using (var outFs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                string lastErr = null; int fails = 0;
                while (DateTime.Now < until && !ct.IsCancellationRequested)
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
                    if (DateTime.Now < until && !ct.IsCancellationRequested) { try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; } }   // zerwane polaczenie - ponownie
                }
                return outFs.Length == 0 ? (lastErr ?? L.T("brak danych")) : null;
            }
        }

        static async Task CopyUntil(Stream s, Stream outFs, DateTime until, CancellationToken ct)
        {
            var buf = new byte[65536]; int n;
            while (DateTime.Now < until && !ct.IsCancellationRequested && (n = await s.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                await outFs.WriteAsync(buf, 0, n, ct);
        }

        // HLS: lista segmentow odswiezana co chwile, nowe segmenty dopisywane po kolei (lista glowna - najlepsza jakosc)
        static async Task<string> RecordHls(HttpClient http, string url, Stream outFs, DateTime until, CancellationToken ct)
        {
            var seen = new HashSet<string>();
            var baseUri = new Uri(url);
            while (DateTime.Now < until && !ct.IsCancellationRequested)
            {
                var text = await http.GetStringAsync(baseUri, ct);
                var pl = ParseHls(text, baseUri);
                if (pl.Encrypted) return L.T("kanał jest zaszyfrowany (HLS z kluczem) – nie da się go nagrać");
                if (pl.Variant != null) { baseUri = pl.Variant; continue; }
                foreach (var seg in pl.Segments)
                {
                    if (DateTime.Now >= until || ct.IsCancellationRequested) break;
                    if (!seen.Add(seg.AbsoluteUri)) continue;
                    using (var resp = await http.GetAsync(seg, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        if (!resp.IsSuccessStatusCode) continue;
                        using (var s = await resp.Content.ReadAsStreamAsync()) await CopyUntil(s, outFs, DateTime.MaxValue, ct);
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
            { MessageBox.Show(owner, L.T("Sprawdź czas początku i końca (koniec musi być później niż początek i w przyszłości)."), L.T("Nagrywanie")); return; }
            IptvSt.RecDir = folder.Text.Trim();
            AddJob(new IptvJob { Kind = "rec", Url = ch.Url, Channel = ch.Name, Opts = ch.Opts, Title = title.Text.Trim(), Start = UnixMs(a1), Stop = UnixMs(a2), Folder = IptvSt.RecDir });
            ShowToast(L.T("⏺ Zaplanowano nagranie: ") + a1.ToString("dd.MM HH:mm") + "–" + a2.ToString("HH:mm") + " " + ch.Name, null);
        }

        // lista zaplanowanych: nagrania i przelaczenia, usuwanie / zatrzymanie
        void ShowJobs(Window owner)
        {
            var w = new Window { Title = L.T("📅 Zaplanowane"), Width = 640, Height = 360, Owner = owner, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var grid = new GridView();
            foreach (var c in new[] { new[] { "What", "Co", "70" }, new[] { "When", "Kiedy", "170" }, new[] { "Channel", "Kanał", "160" }, new[] { "Title", "Program", "220" } })
                grid.Columns.Add(new GridViewColumn { Header = L.T(c[1]), Width = double.Parse(c[2]), DisplayMemberBinding = new System.Windows.Data.Binding(c[0]) });
            var list = new ListView { View = grid, Margin = new Thickness(8) };
            Action fill = () => list.ItemsSource = IptvSt.Jobs.OrderBy(j => j.Start).Select(j => new
            {
                Job = j, Channel = j.Channel, Title = j.Title,
                What = j.Kind == "switch" ? "⏰" : _recording.ContainsKey(j.Id) ? L.T("⏺ teraz") : "⏺",
                When = LocalOf(j.Start).ToString("dd.MM HH:mm") + (j.Kind == "rec" ? "–" + LocalOf(j.Stop).ToString("HH:mm") : ""),
            }).ToList();
            fill();
            Action remove = () =>
            {
                var it = list.SelectedItem; if (it == null) return;
                var j = (IptvJob)it.GetType().GetProperty("Job").GetValue(it);
                if (_recording.TryGetValue(j.Id, out var c)) c.Cancel();   // nagrywanie w toku - zatrzymane, plik zostaje
                else { IptvSt.Jobs.Remove(j); SaveIptv(IptvSt); }
                fill();
            };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            bar.Children.Add(SmallButton(L.T("🗑 Usuń / zatrzymaj"), remove));
            bar.Children.Add(SmallButton(L.T("📁 Folder nagrań"), OpenRecDir));
            var body = new DockPanel();
            DockPanel.SetDock(bar, Dock.Bottom); body.Children.Add(bar);
            body.Children.Add(new TextBlock { Text = L.T("Velivo musi być włączone o czasie nagrania lub przełączenia."), Foreground = Brushes.Gray, Margin = new Thickness(10, 8, 10, 0) });
            DockPanel.SetDock((UIElement)body.Children[1], Dock.Top);
            body.Children.Add(list);
            w.Content = body;
            Action prev = _iptvJobsChanged;
            _iptvJobsChanged = () => { prev?.Invoke(); if (w.IsLoaded) fill(); };
            w.Closed += (s, a) => _iptvJobsChanged = prev;
            w.Show();
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
            var grid = new GridView();
            foreach (var c in new[] { new[] { "Name", "Nagranie", "460" }, new[] { "Date", "Data", "130" }, new[] { "Size", "Rozmiar", "90" } })
                grid.Columns.Add(new GridViewColumn { Header = L.T(c[1]), Width = double.Parse(c[2]), DisplayMemberBinding = new System.Windows.Data.Binding(c[0]) });
            var list = new ListView { View = grid, Margin = new Thickness(8), SelectionMode = SelectionMode.Extended };
            RememberColumns(w, "nagrania", grid);
            var count = new TextBlock { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Action fill = () =>
            {
                var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.IsNullOrWhiteSpace(IptvSt.RecDir) ? DefaultRecDir : IptvSt.RecDir };
                foreach (var j in IptvSt.Jobs) if (!string.IsNullOrWhiteSpace(j.Folder)) dirs.Add(j.Folder);
                var files = dirs.Where(Directory.Exists).SelectMany(d => { try { return Directory.GetFiles(d, "*.ts"); } catch (Exception) { return new string[0]; } })
                    .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).ToList();
                list.ItemsSource = files.Select(f => new { Path = f.FullName, Name = Path.GetFileNameWithoutExtension(f.Name), Date = f.LastWriteTime.ToString("dd.MM.yyyy HH:mm"), Size = (f.Length / 1048576.0).ToString("0") + " MB" }).ToList();
                count.Text = files.Count + L.T(" nagrań");
            };
            Func<List<string>> sel = () => list.SelectedItems.Cast<object>().Select(o => (string)o.GetType().GetProperty("Path").GetValue(o)).ToList();
            Action play = () => { var p = sel(); if (p.Count > 0) PlayVideos(p.ToArray(), 0); };
            Action del = () =>
            {
                var p = sel(); if (p.Count == 0) return;
                if (MessageBox.Show(w, string.Format(L.T("Usunąć {0} nagrań z dysku (do Kosza) i z biblioteki filmów?"), p.Count), L.T("Nagrania TV"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                var errs = DeleteVideoFiles(p);
                fill();
                if (errs.Count > 0) MessageBox.Show(w, L.T("Nie udało się usunąć (nagrywane albo odtwarzane):\n") + string.Join("\n", errs.Take(10)), L.T("Nagrania TV"));
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
}
}
