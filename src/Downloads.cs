using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Menedzer pobierania w stylu IDM: Velivo samo pobiera plik (kilka polaczen naraz, wstrzymywanie,
    // wznawianie, ponawianie), niezaleznie od kart i od silnika WebView2.
    // Gdy pliku nie da sie przejac (np. tworzony przez skrypt strony), pobiera go silnik - "awaryjnie".
    public partial class MainWindow
    {
        // ======================================================================
        //  Wspolne okno pobran
        // ======================================================================

        StackPanel _dlPanel;
        Window _downloadWin;
        DispatcherTimer _dlTimer;

        StackPanel DlPanel { get { return _dlPanel ?? (_dlPanel = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)) }); } }   // szare tlo = linie miedzy wierszami

        void AddRowToPanel(UIElement row) { DlPanel.Children.Insert(0, row); UpdateDownloadsButton(); }

        void UpdateDownloadsButton()
        {
            int active = ActiveDownloadCount() + _jobs.Count(j => j.State == JobState.Running);
            DownloadsBtn.Content = active > 0 ? " " + active : "";
        }

        void Downloads_Click(object sender, RoutedEventArgs e)
        {
            if (_downloadWin != null) { _downloadWin.Activate(); return; }
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6) };
            bar.Children.Add(SmallButton(L.T("Otwórz folder Pobrane"), () =>
            {
                string dir = null;
                var last = _jobs.FirstOrDefault();
                if (last != null) dir = Path.GetDirectoryName(last.File);
                if (dir == null && Core != null) dir = Core.Profile.DefaultDownloadFolderPath;
                if (dir != null && Directory.Exists(dir)) Process.Start("explorer.exe", dir);
            }));
            bar.Children.Add(SmallButton(L.T("Media na stronie"), DetectPageMedia));
            bar.Children.Add(SmallButton(L.T("Wyczyść zakończone"), ClearFinishedDownloads));
            var scroll = new ScrollViewer { Content = DlPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _downloadWin = new Window
            {
                Title = L.T("Pobrane pliki – Velivo"), Width = 760, Height = 520, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = Docked(bar, scroll)
            };
            _downloadWin.Closed += (s, a) => { scroll.Content = null; _downloadWin = null; }; // panel zostaje na nastepny raz
            _downloadWin.Show();
        }

        void ClearFinishedDownloads()
        {
            foreach (var j in _jobs.Where(x => x.State == JobState.Done || x.State == JobState.Canceled || x.State == JobState.Failed).ToList())
                RemoveJob(j);
            foreach (var d in _downloads.Where(x => { ReadState(x); return x.Dead || x.State != CoreWebView2DownloadState.InProgress; }).ToList())
            {
                _downloads.Remove(d);
                DlPanel.Children.Remove((UIElement)d.Bar.Tag);
            }
            SaveJobs();
            UpdateDownloadsButton();
        }

        // ======================================================================
        //  Start pobierania: przejmij plik albo oddaj go silnikowi
        // ======================================================================

        void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            e.Handled = true; // wlasna lista pobran zamiast okienka Edge
            if (TryHandleCrxDownload(e)) return;
            var core = sender as CoreWebView2;
            var deferral = e.GetDeferral();
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                bool done = false;
                try
                {
                    if (_settings.AskDownload)
                    {
                        // startujemy w ostatnio wybranym folderze (np. Z:\), jesli nadal istnieje
                        string startDir = Path.GetDirectoryName(e.ResultFilePath);
                        try { if (!string.IsNullOrEmpty(_settings.LastDownloadDir) && Directory.Exists(_settings.LastDownloadDir)) startDir = _settings.LastDownloadDir; } catch (Exception) { }
                        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(e.ResultFilePath), InitialDirectory = startDir };
                        if (dlg.ShowDialog(this) != true) { e.Cancel = true; return; }
                        e.ResultFilePath = dlg.FileName;
                        try { _settings.LastDownloadDir = Path.GetDirectoryName(dlg.FileName); _settings.Save(DataDir); } catch (Exception) { }
                    }
                    var job = await ProbeDownload(e.DownloadOperation.Uri, e.ResultFilePath, e.DownloadOperation.MimeType, core);
                    if (job != null)
                    {
                        e.Cancel = true; // silnik nie pobiera - robi to menedzer Velivo
                        done = true;
                        deferral.Complete();
                        StartJob(job);
                        return;
                    }
                    TrackDownload(e, sender); // awaryjnie: pobiera silnik WebView2
                }
                catch (Exception ex) { App.LogError(ex); try { TrackDownload(e, sender); } catch (Exception) { } }
                finally { if (!done) deferral.Complete(); }
            }));
        }

        // ======================================================================
        //  Menedzer pobierania
        // ======================================================================

        enum JobState { Running, Paused, Done, Failed, Canceled }

        sealed class Segment
        {
            public long Start { get; set; }
            public long End { get; set; }   // wlacznie
            public long Done { get; set; }
            public long Length { get { return End - Start + 1; } }
        }

        sealed class Job
        {
            // zapisywane na dysku (bez ciasteczek - te pobieramy na biezaco z profilu)
            public string Url { get; set; }
            public string FinalUrl { get; set; }
            public string File { get; set; }
            public string Referer { get; set; }
            public string UserAgent { get; set; }
            public long Total { get; set; }
            public bool Ranges { get; set; }
            public long SingleGot { get; set; }
            public List<Segment> Segments { get; set; } = new List<Segment>();
            public JobState State { get; set; }
            public string Error { get; set; }
            public DateTime Added { get; set; }
            public DateTime Finished { get; set; }

            // tylko w pamieci
            public bool Private;
            public CoreWebView2CookieManager Cookies;
            public CancellationTokenSource Cts;
            public Task Worker;
            public double Speed; public long LastBytes; public DateTime LastTick;
            public int Connections;
            public TextBlock Name, Status; public ProgressBar Bar; public Button PauseBtn, CancelBtn, OpenBtn; public DockPanel Row;

            public string PartFile { get { return File + ".velivo-part"; } }
            public long Got { get { return Segments.Count > 0 ? Segments.Sum(s => s.Done) : SingleGot; } }
        }

        readonly List<Job> _jobs = new List<Job>();
        static string JobsFile { get { return Path.Combine(DataDir, "pobrane.json"); } }

        static readonly HttpClient DlHttp = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30), MaxConnectionsPerServer = 64
        }) { Timeout = Timeout.InfiniteTimeSpan };

        // Ciasteczka strony dla danego adresu (menedzer ciasteczek dziala tylko w watku okna).
        // Ciasteczka sa wspolne dla calego profilu, wiec bierzemy je z dowolnej ZYWEJ karty tego samego rodzaju
        // (zwykla / prywatna) - karta, z ktorej wyszlo pobieranie, mogla juz zostac zamknieta, a menedzer
        // zamknietej karty nie odpowiada wcale. Do tego limit czasu, zeby nic nie moglo zawisnac.
        async Task<string> CookieHeader(Job job, string url)
        {
            try
            {
                var fetch = Dispatcher.InvokeAsync(() =>
                {
                    var live = _tabs.FirstOrDefault(t => t.Private == job.Private && t.View.CoreWebView2 != null);
                    var cm = live != null ? live.View.CoreWebView2.CookieManager : null;
                    return cm != null ? cm.GetCookiesAsync(url) : Task.FromResult(new List<CoreWebView2Cookie>());
                }).Task.Unwrap();
                var winner = await Task.WhenAny(fetch, Task.Delay(TimeSpan.FromSeconds(5)));
                if (winner != fetch) return null;
                var list = await fetch;
                return list.Count == 0 ? null : string.Join("; ", list.Select(c => c.Name + "=" + c.Value));
            }
            catch (Exception) { return null; }
        }

        // Zadanie GET z recznym podazaniem za przekierowaniami (ciasteczka liczone osobno dla kazdego hosta).
        async Task<HttpResponseMessage> DlSend(Job job, string url, long? from, long? to, CancellationToken ct)
        {
            for (int i = 0; i < 10; i++)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(job.UserAgent)) req.Headers.TryAddWithoutValidation("User-Agent", job.UserAgent);
                if (!string.IsNullOrEmpty(job.Referer) && job.Referer.StartsWith("http")) req.Headers.TryAddWithoutValidation("Referer", job.Referer);
                var cookie = await CookieHeader(job, url);
                if (cookie != null) req.Headers.TryAddWithoutValidation("Cookie", cookie);
                if (from.HasValue) req.Headers.Range = new RangeHeaderValue(from, to);
                // naglowki jak z przegladarki - bez nich czesc serwerow (CDN, ochrona przed botami) odmawia pliku
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                req.Headers.TryAddWithoutValidation("Accept-Language", "pl-PL,pl;q=0.9,en-US;q=0.8,en;q=0.7");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
                req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
                var resp = await DlHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                int code = (int)resp.StatusCode;
                if (code >= 300 && code < 400 && resp.Headers.Location != null)
                {
                    url = new Uri(new Uri(url), resp.Headers.Location).AbsoluteUri;
                    resp.Dispose();
                    continue;
                }
                return resp;
            }
            throw new IOException(L.T("Za dużo przekierowań."));
        }

        // Sprawdzenie, czy plik da sie pobrac samodzielnie. null = nie (pobierze silnik).
        async Task<Job> ProbeDownload(string uri, string file, string mime, CoreWebView2 core)
        {
            if (uri == null || !(uri.StartsWith("http://") || uri.StartsWith("https://"))) { LogEngineFallback(uri, "nie http (blob/data)"); return null; }
            var tab = _tabs.FirstOrDefault(t => t.View.CoreWebView2 == core);
            var job = new Job
            {
                Url = uri, File = file, Added = DateTime.Now, State = JobState.Paused,
                Referer = core != null ? core.Source : null,
                UserAgent = core != null ? core.Settings.UserAgent : null,
                Private = tab != null && tab.Private,
                Cookies = core != null ? core.CookieManager : null,
            };
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
            {
                try
                {
                    var resp = await Task.Run(() => DlSend(job, uri, 0, null, cts.Token));
                    // niektore serwery odrzucaja zapytanie z zakresem - druga proba bez niego
                    if ((int)resp.StatusCode != 200 && (int)resp.StatusCode != 206) { resp.Dispose(); resp = await Task.Run(() => DlSend(job, uri, null, null, cts.Token)); }
                    using (resp)
                    {
                        int code = (int)resp.StatusCode;
                        if (code != 200 && code != 206) { LogEngineFallback(uri, "HTTP " + code); return null; }
                        var type = resp.Content.Headers.ContentType != null ? resp.Content.Headers.ContentType.MediaType : "";
                        // serwer oddal strone zamiast pliku (np. plik tylko z formularza POST) - niech pobierze silnik
                        if (type == "text/html" && (mime ?? "").IndexOf("html", StringComparison.OrdinalIgnoreCase) < 0) { LogEngineFallback(uri, "text/html"); return null; }
                        job.FinalUrl = resp.RequestMessage.RequestUri.AbsoluteUri;
                        if (code == 206 && resp.Content.Headers.ContentRange != null && resp.Content.Headers.ContentRange.Length.HasValue)
                        {
                            job.Total = resp.Content.Headers.ContentRange.Length.Value;
                            job.Ranges = true;
                        }
                        else
                        {
                            job.Total = resp.Content.Headers.ContentLength ?? 0;
                            job.Ranges = resp.Headers.AcceptRanges.Contains("bytes") && job.Total > 0;
                        }
                    }
                }
                catch (Exception ex) { LogEngineFallback(uri, ex.GetType().Name + ": " + ex.Message); return null; }
            }
            job.File = UniqueFile(job.File);
            return job;
        }

        // Dziennik: dlaczego plik pobral silnik WebView2 zamiast Velivo (bez parametrow adresu - moga zawierac tokeny)
        static void LogEngineFallback(string uri, string why)
        {
            try
            {
                string u = uri ?? "";
                int q = u.IndexOfAny(new[] { '?', '#' }); if (q >= 0) u = u.Substring(0, q);
                if (u.StartsWith("data:")) u = "data:";
                File.AppendAllText(Path.Combine(DataDir, "pobieranie-silnik.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + why + "  " + u + Environment.NewLine);
            }
            catch (Exception) { }
        }

        static string UniqueFile(string path)
        {
            if (!File.Exists(path) && !File.Exists(path + ".velivo-part")) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 1; i < 1000; i++)
            {
                var p = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(p) && !File.Exists(p + ".velivo-part")) return p;
            }
            return path;
        }

        void StartJob(Job job)
        {
            _jobs.Insert(0, job);
            BuildJobRow(job);
            AddRowToPanel(job.Row);
            Downloads_Click(null, null);
            ResumeJob(job);
        }

        void ResumeJob(Job job)
        {
            if (job.State == JobState.Running || job.State == JobState.Done) return;
            if (job.Worker != null && !job.Worker.IsCompleted) return; // poprzedni watek jeszcze sie zamyka
            if (job.Cookies == null && !job.Private && Core != null) job.Cookies = Core.CookieManager;
            job.State = JobState.Running; job.Error = null;
            job.Cts = new CancellationTokenSource();
            job.LastBytes = job.Got; job.LastTick = DateTime.UtcNow; job.Speed = 0;
            int conns = Math.Max(1, Math.Min(16, _settings.Connections));
            var ct = job.Cts.Token;
            job.Worker = Task.Run(() => RunJob(job, conns, ct));
            EnsureDownloadTimer();
            RefreshJob(job);
            SaveJobs();
        }

        void PauseJob(Job job)
        {
            if (job.State != JobState.Running) return;
            job.State = JobState.Paused;
            if (job.Cts != null) job.Cts.Cancel();
            RefreshJob(job); SaveJobs();
        }

        void CancelJob(Job job)
        {
            var was = job.State;
            job.State = JobState.Canceled;
            if (job.Cts != null) job.Cts.Cancel();
            if (was != JobState.Running) DeletePart(job); // gdy dziala, czesc usuwa sam watek po zakonczeniu
            RefreshJob(job); SaveJobs();
        }

        static void DeletePart(Job job)
        {
            try { if (File.Exists(job.PartFile)) File.Delete(job.PartFile); } catch (Exception) { }
        }

        void RemoveJob(Job job)
        {
            if (job.State == JobState.Running) return;
            _jobs.Remove(job);
            DlPanel.Children.Remove(job.Row);
            if (job.State != JobState.Done) DeletePart(job);
        }

        // ---------- wlasciwe pobieranie (watek w tle) ----------

        async Task RunJob(Job job, int conns, CancellationToken ct)
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(job.File));
                    if (job.Ranges && job.Total >= 2 * 1024 * 1024 && (conns > 1 || job.Segments.Count > 0))
                        await RunSegmented(job, conns, ct);
                    else
                        await RunSingle(job, ct);

                    if (job.Total > 0 && job.Got != job.Total) throw new IOException(L.T("Pobrano ") + Size(job.Got) + L.T(" z ") + Size(job.Total) + ".");
                    var final = job.File;
                    if (File.Exists(final)) final = UniqueFile(final);
                    File.Move(job.PartFile, final);
                    job.File = final;
                    job.State = JobState.Done;
                    job.Finished = DateTime.Now;
                    break;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break; // wstrzymano albo anulowano
                    // zerwane polaczenie itp. - ponawiamy od miejsca przerwania
                    if (++attempt <= 5 && (job.Ranges || job.Got == 0))
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); } catch (OperationCanceledException) { break; }
                        continue;
                    }
                    job.State = JobState.Failed;
                    job.Error = ex.Message;
                    App.LogError(ex);
                    break;
                }
            }
            if (job.State == JobState.Canceled) DeletePart(job);
            await Dispatcher.InvokeAsync(() => { RefreshJob(job); SaveJobs(); UpdateDownloadsButton(); });
        }

        async Task RunSingle(Job job, CancellationToken ct)
        {
            job.Connections = 1;
            long from = File.Exists(job.PartFile) ? new FileInfo(job.PartFile).Length : 0;
            if (from > 0 && !job.Ranges) from = 0;
            using (var resp = await DlSend(job, job.FinalUrl ?? job.Url, from > 0 ? from : (long?)null, null, ct))
            {
                int code = (int)resp.StatusCode;
                if (code != 200 && code != 206) throw new IOException(L.T("Serwer odpowiedział: ") + code + " " + resp.ReasonPhrase);
                if (from > 0 && code == 200) from = 0; // serwer zaczal od poczatku
                if (job.Total == 0 && resp.Content.Headers.ContentLength.HasValue) job.Total = from + resp.Content.Headers.ContentLength.Value;
                job.SingleGot = from;
                using (var fs = new FileStream(job.PartFile, from > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, true))
                using (var body = await resp.Content.ReadAsStreamAsync(ct))
                    await CopyWithProgress(body, fs, n => job.SingleGot += n, ct);
            }
            if (job.Total == 0) job.Total = job.SingleGot;
        }

        async Task RunSegmented(Job job, int conns, CancellationToken ct)
        {
            if (job.Segments.Count == 0)
            {
                // podzial na rowne czesci, kazda nie mniejsza niz 1 MB
                int n = (int)Math.Max(1, Math.Min(conns, job.Total / (1024 * 1024)));
                long size = job.Total / n;
                for (int i = 0; i < n; i++)
                    job.Segments.Add(new Segment { Start = i * size, End = i == n - 1 ? job.Total - 1 : (i + 1) * size - 1 });
            }
            using (var fs = new FileStream(job.PartFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                if (fs.Length != job.Total) fs.SetLength(job.Total);
            var left = job.Segments.Where(s => s.Done < s.Length).ToList();
            job.Connections = left.Count;
            await Task.WhenAll(left.Select(s => RunSegment(job, s, ct)));
        }

        async Task RunSegment(Job job, Segment seg, CancellationToken ct)
        {
            int attempt = 0;
            while (seg.Done < seg.Length)
            {
                try
                {
                    long from = seg.Start + seg.Done;
                    using (var resp = await DlSend(job, job.FinalUrl ?? job.Url, from, seg.End, ct))
                    {
                        if ((int)resp.StatusCode != 206) throw new IOException(L.T("Serwer nie obsługuje pobierania w częściach (") + (int)resp.StatusCode + ").");
                        using (var fs = new FileStream(job.PartFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 18, true))
                        using (var body = await resp.Content.ReadAsStreamAsync(ct))
                        {
                            fs.Position = from;
                            long room = seg.Length - seg.Done;
                            await CopyWithProgress(body, fs, n => seg.Done += n, ct, room);
                        }
                    }
                    attempt = 0;
                }
                catch (Exception) when (!ct.IsCancellationRequested && ++attempt <= 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
            }
        }

        // Kopiowanie z kontrola zawieszenia: brak danych przez 60 s = zerwane polaczenie (ponowienie).
        static async Task CopyWithProgress(Stream src, Stream dst, Action<long> progress, CancellationToken ct, long limit = long.MaxValue)
        {
            var buf = new byte[256 * 1024];
            while (limit > 0)
            {
                int n;
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    stall.CancelAfter(TimeSpan.FromSeconds(60));
                    try { n = await src.ReadAsync(buf, 0, (int)Math.Min(buf.Length, limit), stall.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException(L.T("Brak danych z serwera przez 60 s.")); }
                }
                if (n == 0) break;
                await dst.WriteAsync(buf, 0, n, ct);
                progress(n);
                limit -= n;
            }
        }

        // ---------- wyglad wiersza pobierania ----------

        void BuildJobRow(Job job)
        {
            job.Name = new TextBlock { Text = Path.GetFileName(job.File), FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = job.Url };
            job.Bar = new ProgressBar { Height = 10, Margin = new Thickness(0, 4, 0, 4), Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)) };
            job.Status = new TextBlock { FontSize = 12, Foreground = Brushes.DimGray };
            job.PauseBtn = SmallButton(L.T("Wstrzymaj"), () => { if (job.State == JobState.Running) PauseJob(job); else ResumeJob(job); });
            job.CancelBtn = SmallButton(L.T("Anuluj"), () =>
            {
                if (job.State == JobState.Running || job.State == JobState.Paused)
                {
                    if (MessageBox.Show(_downloadWin ?? (Window)this, L.T("Anulować pobieranie „") + Path.GetFileName(job.File) + L.T("”?\nPobrana część zostanie usunięta."), L.T("Pobrane"), MessageBoxButton.YesNo) == MessageBoxResult.Yes) CancelJob(job);
                }
                else { RemoveJob(job); SaveJobs(); UpdateDownloadsButton(); }
            });
            job.OpenBtn = SmallButton(L.T("Otwórz"), () =>
            {
                try { Process.Start(new ProcessStartInfo(job.File) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(_downloadWin ?? (Window)this, ex.Message, L.T("Pobrane")); }
            });
            var folder = SmallButton("📁", () =>
            {
                var f = File.Exists(job.File) ? job.File : job.PartFile;
                if (File.Exists(f)) Process.Start("explorer.exe", "/select,\"" + f + "\"");
                else if (Directory.Exists(Path.GetDirectoryName(job.File))) Process.Start("explorer.exe", Path.GetDirectoryName(job.File));
            });
            folder.ToolTip = L.T("Pokaż w folderze");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var b in new[] { job.OpenBtn, job.PauseBtn, job.CancelBtn, folder }) buttons.Children.Add(b);
            var text = new StackPanel();
            text.Children.Add(job.Name); text.Children.Add(job.Bar); text.Children.Add(job.Status);
            job.Row = new DockPanel { Margin = new Thickness(0, 0, 0, 1), Background = Brushes.White };
            text.Margin = new Thickness(10, 8, 8, 8);
            buttons.Margin = new Thickness(0, 0, 10, 0);
            job.Name.ToolTip = job.File + "\n" + job.Url;
            DockPanel.SetDock(buttons, Dock.Right);
            job.Row.Children.Add(buttons); job.Row.Children.Add(text);
            RefreshJob(job);
        }

        // " · 3 paź 2026, 18:42 · strona.pl" - kiedy i skad pobrano
        static string DownloadInfo(Job job)
        {
            var when = job.Finished != default(DateTime) ? job.Finished : job.Added;
            string text = when != default(DateTime) ? "  ·  " + when.ToString("d MMM yyyy, HH:mm", L.En ? new System.Globalization.CultureInfo("en-GB") : new System.Globalization.CultureInfo("pl-PL")) : "";
            Uri u;
            var src = !string.IsNullOrEmpty(job.Referer) && job.Referer.StartsWith("http") ? job.Referer : job.Url;
            if (Uri.TryCreate(src ?? "", UriKind.Absolute, out u) && u.Host.Length > 0) text += "  ·  " + (u.Host.StartsWith("www.") ? u.Host.Substring(4) : u.Host);
            return text;
        }

        static string Eta(double seconds)
        {
            if (double.IsInfinity(seconds) || double.IsNaN(seconds) || seconds <= 0) return "";
            if (seconds < 60) return L.T("zostało ") + (int)seconds + " s";
            if (seconds < 3600) return L.T("zostało ") + (int)(seconds / 60) + " min";
            return L.T("zostało ") + (int)(seconds / 3600) + " godz. " + (int)(seconds % 3600 / 60) + " min";
        }

        void RefreshJob(Job job)
        {
            if (job.Row == null) return;
            long got = job.Got, total = job.Total;
            job.Name.Text = Path.GetFileName(job.File);
            job.Bar.IsIndeterminate = job.State == JobState.Running && total <= 0;
            job.Bar.Value = total > 0 ? got * 100.0 / total : (job.State == JobState.Done ? 100 : 0);
            string size = Size(got) + (total > 0 ? L.T(" z ") + Size(total) : "");
            switch (job.State)
            {
                case JobState.Running:
                    string speed = job.Speed > 0 ? " · " + Size((long)job.Speed) + "/s" : "";
                    string eta = job.Speed > 0 && total > 0 ? " · " + Eta((total - got) / job.Speed) : "";
                    string conn = job.Connections > 1 ? " · " + job.Connections + L.T(" połączeń") : "";
                    job.Status.Text = size + speed + eta + conn;
                    job.Status.Foreground = Brushes.DimGray;
                    break;
                case JobState.Paused:
                    job.Status.Text = L.T("Wstrzymano – ") + size + (job.Ranges ? "" : L.T(" (ten serwer nie pozwala wznowić – zacznie od nowa)"));
                    job.Status.Foreground = Brushes.DarkGoldenrod;
                    break;
                case JobState.Done:
                    bool exists = File.Exists(job.File);
                    job.Status.Text = (exists ? L.T("Gotowe – ") + Size(total > 0 ? total : got) : L.T("Plik usunięty lub przeniesiony"))
                        + DownloadInfo(job);
                    job.Status.Foreground = exists ? Brushes.SeaGreen : Brushes.Gray;
                    job.Bar.Visibility = Visibility.Collapsed;
                    break;
                case JobState.Failed:
                    job.Status.Text = L.T("Błąd: ") + job.Error + L.T(" – kliknij „Wznów”, aby spróbować ponownie");
                    job.Status.Foreground = Brushes.Firebrick;
                    break;
                case JobState.Canceled:
                    job.Status.Text = L.T("Anulowano");
                    job.Status.Foreground = Brushes.Gray;
                    break;
            }
            job.PauseBtn.Content = job.State == JobState.Running ? L.T("Wstrzymaj") : L.T("Wznów");
            job.PauseBtn.Visibility = job.State == JobState.Done || job.State == JobState.Canceled ? Visibility.Collapsed : Visibility.Visible;
            job.OpenBtn.Visibility = job.State == JobState.Done && File.Exists(job.File) ? Visibility.Visible : Visibility.Collapsed;
            if (job.State != JobState.Done) job.Bar.Visibility = Visibility.Visible;
            job.CancelBtn.Content = job.State == JobState.Running || job.State == JobState.Paused ? L.T("Anuluj") : L.T("Usuń z listy");
        }

        // Odswiezanie postepu i predkosci co pol sekundy (tylko gdy cos sie pobiera).
        void EnsureDownloadTimer()
        {
            if (_dlTimer == null)
            {
                _dlTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _dlTimer.Tick += (s, e) =>
                {
                    var now = DateTime.UtcNow;
                    foreach (var j in _jobs.Where(x => x.State == JobState.Running))
                    {
                        double dt = (now - j.LastTick).TotalSeconds;
                        if (dt > 0)
                        {
                            long got = j.Got;
                            double cur = (got - j.LastBytes) / dt;
                            j.Speed = j.Speed <= 0 ? cur : j.Speed * 0.7 + cur * 0.3; // wygladzenie
                            j.LastBytes = got; j.LastTick = now;
                        }
                        RefreshJob(j);
                    }
                    UpdateDownloadsButton();
                    if (!_jobs.Any(x => x.State == JobState.Running)) { _dlTimer.Stop(); SaveJobs(); }
                };
            }
            if (!_dlTimer.IsEnabled) _dlTimer.Start();
        }

        // ---------- zapis listy (pobieranie mozna wznowic po ponownym uruchomieniu) ----------

        void SaveJobs()
        {
            try
            {
                var keep = _jobs.Where(j => !j.Private && j.State != JobState.Canceled).ToList();
                Directory.CreateDirectory(DataDir);
                var tmp = JobsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(keep, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, JobsFile, true);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void LoadJobs()
        {
            try
            {
                if (!File.Exists(JobsFile)) return;
                var list = JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(JobsFile));
                if (list == null) return;
                foreach (var j in list.OrderBy(x => x.Added))
                {
                    if (j.State == JobState.Running) j.State = JobState.Paused; // przerwane zamknieciem programu
                    _jobs.Insert(0, j);
                    BuildJobRow(j);
                    DlPanel.Children.Insert(0, j.Row);
                }
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // Zamykanie Velivo w trakcie pobierania: pytanie, potem wstrzymanie (do wznowienia pozniej).
        void ConfirmCloseWithDownloads(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (e.Cancel) return;
            var running = _jobs.Where(j => j.State == JobState.Running).ToList();
            int engine = ActiveDownloadCount();
            if (running.Count + engine == 0) return;
            var msg = "Trwa pobieranie (" + (running.Count + engine) + L.T(").\n\nZamknąć Velivo?") +
                      (running.Count > 0 ? L.T("\nPobieranie zostanie wstrzymane – wznowisz je po ponownym uruchomieniu.") : "") +
                      (engine > 0 ? L.T("\nPliki pobierane przez silnik przeglądarki zostaną przerwane.") : "");
            if (MessageBox.Show(this, msg, "Velivo", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
            foreach (var j in running) { j.State = JobState.Paused; if (j.Cts != null) j.Cts.Cancel(); }
            try { Task.WaitAll(running.Where(j => j.Worker != null).Select(j => j.Worker).ToArray(), 3000); } catch (Exception) { }
            SaveJobs();
        }

        // ======================================================================
        //  Awaryjne pobieranie przez silnik WebView2 (gdy menedzer nie moze przejac pliku)
        // ======================================================================

        sealed class DownloadRow
        {
            public CoreWebView2DownloadOperation Op;
            public string File;
            public TextBlock Status;
            public ProgressBar Bar;
            public Button Action;
            public CoreWebView2 Owner;           // widok, z ktorego wyszlo pobieranie
            public CoreWebView2DownloadState State;
            public long Got, Total;
            public bool CanResume, Dead;
            public string Reason;
            public string Url; public DateTime Added; public bool Private; public bool Converted;
        }

        readonly List<DownloadRow> _downloads = new List<DownloadRow>();

        void TrackDownload(CoreWebView2DownloadStartingEventArgs e, object owner)
        {
            var ownerCore = owner as CoreWebView2;
            var ownerTab = _tabs.FirstOrDefault(t => t.View.CoreWebView2 == ownerCore);
            var row = new DownloadRow { Op = e.DownloadOperation, File = e.ResultFilePath, Owner = ownerCore, Url = e.DownloadOperation.Uri, Added = DateTime.Now, Private = ownerTab != null && ownerTab.Private };
            _downloads.Insert(0, row);
            BuildDownloadRow(row);
            AddRowToPanel((UIElement)row.Bar.Tag);
            e.DownloadOperation.BytesReceivedChanged += (s, a) => Dispatcher.BeginInvoke(new Action(() => RefreshDownload(row)));
            e.DownloadOperation.StateChanged += (s, a) => Dispatcher.BeginInvoke(new Action(() => { RefreshDownload(row); ReleaseParkedViews(); }));
            RefreshDownload(row);
            Downloads_Click(null, null);
        }

        // Odczyt stanu. Gdy widok, z ktorego wyszlo pobieranie, juz nie istnieje, WebView2 rzuca wyjatek -
        // zostaje ostatni znany stan i pobieranie oznaczamy jako niedostepne (zamiast wywracac program).
        static void ReadState(DownloadRow row)
        {
            if (row.Dead) return;
            try
            {
                var op = row.Op;
                row.State = op.State;
                row.Got = op.BytesReceived;
                row.Total = op.TotalBytesToReceive.HasValue ? (long)op.TotalBytesToReceive.Value : 0;
                row.CanResume = op.CanResume;
                row.Reason = op.State == CoreWebView2DownloadState.Interrupted ? op.InterruptReason.ToString() : null;
            }
            catch (Exception) { row.Dead = true; }
        }

        bool HasActiveDownloads(CoreWebView2 core)
        {
            if (core == null) return false;
            foreach (var d in _downloads)
            {
                if (d.Owner != core) continue;
                ReadState(d);
                if (!d.Dead && d.State == CoreWebView2DownloadState.InProgress) return true;
            }
            return false;
        }

        void CancelEngineDownloads(CoreWebView2 core)
        {
            if (core == null) return;
            foreach (var download in _downloads.Where(d => d.Owner == core).ToList())
            {
                ReadState(download);
                if (!download.Dead && download.State == CoreWebView2DownloadState.InProgress)
                {
                    try { download.Op.Cancel(); } catch (Exception) { }
                }
            }
        }

        int ActiveDownloadCount()
        {
            int n = 0;
            foreach (var d in _downloads) { ReadState(d); if (!d.Dead && d.State == CoreWebView2DownloadState.InProgress) n++; }
            return n;
        }

        void BuildDownloadRow(DownloadRow row)
        {
            row.Status = new TextBlock { FontSize = 12, Foreground = Brushes.DimGray };
            row.Bar = new ProgressBar { Height = 10, Margin = new Thickness(0, 4, 0, 4) };
            row.Action = SmallButton(L.T("Anuluj"), () => DownloadAction(row));
            var folder = SmallButton("📁", () =>
            {
                if (File.Exists(row.File)) Process.Start("explorer.exe", "/select,\"" + row.File + "\"");
                else if (Directory.Exists(Path.GetDirectoryName(row.File))) Process.Start("explorer.exe", Path.GetDirectoryName(row.File));
            });
            folder.ToolTip = L.T("Pokaż w folderze");
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = Path.GetFileName(row.File), FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = row.File });
            text.Children.Add(row.Bar);
            text.Children.Add(row.Status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(row.Action); buttons.Children.Add(folder);
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 1), Background = Brushes.White };
            text.Margin = new Thickness(10, 8, 8, 8);
            buttons.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(buttons, Dock.Right);
            line.Children.Add(buttons); line.Children.Add(text);
            row.Bar.Tag = line;
        }

        void RefreshDownload(DownloadRow row)
        {
            ReadState(row);
            long got = row.Got, total = row.Total;
            bool done = row.State == CoreWebView2DownloadState.Completed && !row.Dead;
            row.Bar.IsIndeterminate = !row.Dead && total <= 0 && row.State == CoreWebView2DownloadState.InProgress;
            row.Bar.Value = total > 0 ? got * 100.0 / total : (done ? 100 : 0);
            string suffix = L.T(" (pobiera silnik przeglądarki)");
            if (row.Dead && row.State != CoreWebView2DownloadState.Completed)
            {
                row.Status.Text = L.T("Przerwano – ") + Size(got) + (total > 0 ? L.T(" z ") + Size(total) : "");
                row.Action.Content = L.T("Usuń z listy");
            }
            else switch (row.State)
            {
                case CoreWebView2DownloadState.InProgress:
                    row.Status.Text = (row.CanResume ? L.T("Wstrzymano – ") : "") + Size(got) + (total > 0 ? L.T(" z ") + Size(total) : "") + suffix;
                    row.Action.Content = row.CanResume ? L.T("Wznów") : L.T("Anuluj");
                    break;
                case CoreWebView2DownloadState.Completed:
                    row.Status.Text = L.T("Gotowe – ") + Size(got);
                    row.Action.Content = L.T("Otwórz");
                    if (!row.Dead) { Dispatcher.BeginInvoke(new Action(() => KeepEngineDownloadInHistory(row))); }
                    break;
                default:
                    row.Status.Text = L.T("Przerwano (") + (row.Reason ?? "?") + ")";
                    row.Action.Content = row.CanResume ? L.T("Wznów") : L.T("Usuń z listy");
                    break;
            }
            UpdateDownloadsButton();
        }

        void DownloadAction(DownloadRow row)
        {
            ReadState(row);
            try
            {
                if (row.State == CoreWebView2DownloadState.Completed && !row.Dead)
                    Process.Start(new ProcessStartInfo(row.File) { UseShellExecute = true });
                else if (!row.Dead && row.CanResume) row.Op.Resume();
                else if (!row.Dead && row.State == CoreWebView2DownloadState.InProgress) row.Op.Cancel();
                else
                {
                    _downloads.Remove(row);
                    DlPanel.Children.Remove((UIElement)row.Bar.Tag);
                    UpdateDownloadsButton();
                    return;
                }
            }
            catch (Exception ex)
            {
                row.Dead = true;
                MessageBox.Show(_downloadWin ?? (Window)this, L.T("Nie udało się wykonać operacji na pobieraniu:\n") + ex.Message, L.T("Pobrane"));
            }
            RefreshDownload(row);
        }

        // Plik pobrany przez silnik trafia do historii pobranych jak kazdy inny (zostaje po ponownym uruchomieniu).
        void KeepEngineDownloadInHistory(DownloadRow row)
        {
            if (row.Converted) return;
            row.Converted = true;
            var line = (UIElement)row.Bar.Tag;
            int at = Math.Max(0, DlPanel.Children.IndexOf(line));
            var job = new Job
            {
                Url = row.Url, File = row.File, Total = row.Got, SingleGot = row.Got, State = JobState.Done,
                Added = row.Added, Finished = DateTime.Now, Private = row.Private,
                Referer = row.Owner != null ? SafeSource(row.Owner) : null
            };
            _downloads.Remove(row);
            DlPanel.Children.Remove(line);
            _jobs.Insert(0, job);
            BuildJobRow(job);
            DlPanel.Children.Insert(Math.Min(at, DlPanel.Children.Count), job.Row);
            SaveJobs();
            UpdateDownloadsButton();
        }

        static string SafeSource(CoreWebView2 core) { try { return core.Source; } catch (Exception) { return null; } }

        static string Size(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.#") + " MB";
            return (b / 1073741824.0).ToString("0.##") + " GB";
        }
    }
}
