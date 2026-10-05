using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Przegladarka
{
    // Pobieranie filmow (jak Internet Download Manager): przycisk "Pobierz" nad filmem i w menu prawego przycisku.
    // Zwykly plik wideo -> menedzer pobran Velivo (do 16 polaczen). YouTube, strumienie (m3u8/blob) i inne serwisy ->
    // darmowe narzedzie yt-dlp (pobierane raz, za zgoda uzytkownika), do wysokiej jakosci i MP3 dodatkowo FFmpeg.
    public partial class MainWindow
    {
        static string ToolsDir { get { return Path.Combine(DataDir, "narzedzia"); } }
        static string YtDlpPath { get { return Path.Combine(ToolsDir, "yt-dlp.exe"); } }
        static string FfmpegPath { get { return Path.Combine(ToolsDir, "ffmpeg.exe"); } }

        const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        const string YtDlpSumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
        const string FfmpegSumsUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256";
        const string FfmpegZipUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

        static readonly HttpClient ToolHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };

        sealed class VideoRequest { public string page { get; set; } public string src { get; set; } public string title { get; set; } }

        void HandleVideoDownloadRequest(BrowserTab tab, string json)
        {
            VideoRequest r;
            try { r = JsonSerializer.Deserialize<VideoRequest>(json); } catch (JsonException) { return; }
            if (r == null || tab == null) return;
            var src = r.src ?? "";
            bool direct = (src.StartsWith("http://") || src.StartsWith("https://")) && !src.Contains(".m3u8") && !src.Contains(".mpd")
                          && !IsYoutubeLikeHost(MediaHost(src)) && !IsYoutubeLikeHost(MediaHost(r.page));
            if (direct) { _ = QueueMediaDownload(src, false); return; }
            ShowVideoDownloadDialog(r.page, r.title);
        }

        // Z menu: najwiekszy film na stronie (albo sama strona, np. YouTube).
        async void DownloadVideoFromPage(BrowserTab tab)
        {
            var core = tab != null ? tab.View.CoreWebView2 : null;
            if (core == null) return;
            try
            {
                var raw = await core.ExecuteScriptAsync("(function(){var v=Array.prototype.slice.call(document.querySelectorAll('video')).sort(function(a,b){return b.clientWidth*b.clientHeight-a.clientWidth*a.clientHeight;})[0];return JSON.stringify({page:location.href,src:v?(v.currentSrc||v.src||''):'',title:document.title||''});})()");
                HandleVideoDownloadRequest(tab, JsonSerializer.Deserialize<string>(raw));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void ShowVideoDownloadDialog(string pageUrl, string title)
        {
            if (string.IsNullOrWhiteSpace(pageUrl) || !(pageUrl.StartsWith("http://") || pageUrl.StartsWith("https://"))) return;
            bool hasFfmpeg = File.Exists(FfmpegPath);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(title) ? pageUrl : title, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            var best = new RadioButton { Content = L.T("Najlepsza jakość wideo (do 1080p, MP4)") + (hasFfmpeg ? "" : L.T("  – dobierze dodatek FFmpeg")), IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
            var simple = new RadioButton { Content = L.T("Szybko: wideo w jednym pliku (zwykle 360p–720p, bez dodatków)"), Margin = new Thickness(0, 4, 0, 4) };
            var m4a = new RadioButton { Content = L.T("Tylko dźwięk (M4A, bez dodatków)"), Margin = new Thickness(0, 4, 0, 4) };
            var mp3 = new RadioButton { Content = L.T("Tylko dźwięk MP3") + (hasFfmpeg ? "" : L.T("  – dobierze dodatek FFmpeg")), Margin = new Thickness(0, 4, 0, 4) };
            foreach (var rb in new[] { best, simple, m4a, mp3 }) { rb.FontSize = 14; panel.Children.Add(rb); }
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Pobieraj tylko materiały, do których masz prawo (np. na własny użytek). Treści zabezpieczone DRM (Netflix, Disney+ itp.) nie są obsługiwane."),
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 10, 0, 10)
            });
            // gdzie zapisac: ostatnio wybrany folder filmow, inaczej folder Pobrane
            string folder = !string.IsNullOrWhiteSpace(_settings.VideoDir) && Directory.Exists(_settings.VideoDir) ? _settings.VideoDir : DefaultVideoDir();
            var folderText = new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 8, 0), ToolTip = folder };
            var change = new Button { Content = L.T("Zmień folder…"), Padding = new Thickness(10, 3, 10, 3) };
            change.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = folder, Title = L.T("Gdzie zapisać film?") };
                if (dlg.ShowDialog(this) == true) { folder = dlg.FolderName; folderText.Text = folder; folderText.ToolTip = folder; }
            };
            var where = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var whereLabel = new TextBlock { Text = L.T("Zapisz w:"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(whereLabel, Dock.Left); DockPanel.SetDock(change, Dock.Right);
            where.Children.Add(whereLabel); where.Children.Add(change); where.Children.Add(folderText);
            panel.Children.Insert(panel.Children.Count - 1, where);
            var ok = new Button { Content = L.T("Pobierz"), IsDefault = true, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = L.T("Anuluj"), IsCancel = true, Padding = new Thickness(16, 5, 16, 5) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            var w = new Window { Title = L.T("Pobierz film"), Width = 560, SizeToContent = SizeToContent.Height, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Content = panel };
            string mode = null;
            ok.Click += (s, e) => { mode = best.IsChecked == true ? "best" : simple.IsChecked == true ? "simple" : m4a.IsChecked == true ? "m4a" : "mp3"; w.Close(); };
            w.ShowDialog();
            if (mode == null) return;
            if (folder != _settings.VideoDir) { _settings.VideoDir = folder; try { _settings.Save(DataDir); } catch (Exception) { } }
            _ = StartYtDlpAsync(pageUrl, mode, title, folder);
        }

        // ---------- narzedzia (pobierane raz) ----------

        async Task<bool> EnsureYtDlpAsync()
        {
            if (File.Exists(YtDlpPath))
            {
                // YouTube czesto sie zmienia - narzedzie samo sie aktualizuje co 2 tygodnie
                if (DateTime.Now - File.GetLastWriteTime(YtDlpPath) > TimeSpan.FromDays(14))
                {
                    try
                    {
                        var p = Process.Start(new ProcessStartInfo(YtDlpPath, "-U") { CreateNoWindow = true, UseShellExecute = false });
                        await Task.Run(() => p.WaitForExit(60000));
                        File.SetLastWriteTime(YtDlpPath, DateTime.Now);
                    }
                    catch (Exception) { }
                }
                return true;
            }
            if (MessageBox.Show(this, L.T("Do pobierania z YouTube i innych serwisów Velivo używa darmowego narzędzia yt-dlp (ok. 18 MB, z serwisu GitHub).\n\nPobrać je teraz? To jednorazowe."), L.T("Pobierz film"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            return await DownloadToolAsync(YtDlpUrl, YtDlpPath, "yt-dlp", YtDlpSumsUrl);
        }

        async Task<bool> EnsureFfmpegAsync()
        {
            if (File.Exists(FfmpegPath)) return true;
            if (MessageBox.Show(this, L.T("Najlepsza jakość i MP3 wymagają darmowego dodatku FFmpeg (ok. 140 MB do pobrania, po rozpakowaniu ok. 130 MB).\n\nPobrać go teraz? To jednorazowe."), L.T("Pobierz film"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            var zip = Path.Combine(ToolsDir, "ffmpeg.zip");
            if (!await DownloadToolAsync(FfmpegZipUrl, zip, "FFmpeg", FfmpegSumsUrl)) return false;
            try
            {
                await Task.Run(() =>
                {
                    using (var a = ZipFile.OpenRead(zip))
                    {
                        var entry = a.Entries.FirstOrDefault(x => x.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                                    ?? a.Entries.FirstOrDefault(x => x.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                        if (entry == null) throw new IOException("ffmpeg.exe not found");
                        entry.ExtractToFile(FfmpegPath, true);
                    }
                });
                return true;
            }
            catch (Exception ex) { App.LogError(ex); MessageBox.Show(this, L.T("Nie udało się rozpakować FFmpeg:\n") + ex.Message, "Velivo"); return false; }
            finally { try { File.Delete(zip); } catch (Exception) { } }
        }

        async Task<bool> DownloadToolAsync(string url, string target, string name, string sumsUrl)
        {
            ShowToast((L.En ? "⬇ Downloading " : "⬇ Pobieram narzędzie ") + name + "…", null);
            try
            {
                Directory.CreateDirectory(ToolsDir);
                var tmp = target + ".tmp";
                using (var resp = await ToolHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var fs = File.Create(tmp)) await resp.Content.CopyToAsync(fs);
                }
                // suma SHA-256 z pliku sum tego samego wydania - niezgodny plik nie zostanie uruchomiony
                try { await Integrity.VerifyAgainstSumsAsync(ToolHttp, sumsUrl, Path.GetFileName(new Uri(url).AbsolutePath), tmp); }
                catch (Exception) { try { File.Delete(tmp); } catch (Exception) { } throw; }
                File.Move(tmp, target, true);
                return true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show(this, (L.En ? "Could not download " : "Nie udało się pobrać ") + name + ":\n" + ex.Message, "Velivo");
                return false;
            }
        }

        // ---------- pobieranie ----------

        string DefaultVideoDir()
        {
            string dir = Core != null ? Core.Profile.DefaultDownloadFolderPath : null;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return dir;
        }

        async Task StartYtDlpAsync(string pageUrl, string mode, string title, string dir)
        {
            if (!await EnsureYtDlpAsync()) return;
            bool needFf = mode == "best" || mode == "mp3";
            if (needFf && !await EnsureFfmpegAsync())
            {
                if (mode == "mp3") return;
                mode = "simple";   // bez FFmpeg - najlepsze, co jest w jednym pliku
                needFf = false;
            }
            if (string.IsNullOrWhiteSpace(dir)) dir = DefaultVideoDir();
            Directory.CreateDirectory(dir);

            var psi = new ProcessStartInfo(YtDlpPath)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = dir,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            foreach (var a in new[] { "--no-playlist", "--newline", "--progress", "--no-colors", "--encoding", "utf-8", "-N", "8",
                                      "-o", Path.Combine(dir, "%(title).150B [%(id)s].%(ext)s"),
                                      "--print", "after_move:VELIVOFILE %(filepath)s",
                                      "--progress-template", "download:VELIVO %(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s" })
                psi.ArgumentList.Add(a);
            switch (mode)
            {
                case "best": foreach (var a in new[] { "-f", "bv*[height<=1080][ext=mp4]+ba[ext=m4a]/bv*[height<=1080]+ba/b", "--merge-output-format", "mp4" }) psi.ArgumentList.Add(a); break;
                case "simple": foreach (var a in new[] { "-f", "b[ext=mp4]/b" }) psi.ArgumentList.Add(a); break;
                case "m4a": foreach (var a in new[] { "-f", "ba[ext=m4a]/ba" }) psi.ArgumentList.Add(a); break;
                case "mp3": foreach (var a in new[] { "-f", "ba", "-x", "--audio-format", "mp3", "--audio-quality", "0" }) psi.ArgumentList.Add(a); break;
            }
            if (needFf) { psi.ArgumentList.Add("--ffmpeg-location"); psi.ArgumentList.Add(FfmpegPath); }
            psi.ArgumentList.Add("--");   // wszystko dalej to adres, nie opcja yt-dlp (np. adres zaczynajacy sie od "-")
            psi.ArgumentList.Add(pageUrl);

            // wiersz na liscie pobranych
            var name = new TextBlock { Text = string.IsNullOrWhiteSpace(title) ? pageUrl : title, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = pageUrl };
            var bar = new ProgressBar { Height = 10, Margin = new Thickness(0, 4, 0, 4), IsIndeterminate = true, Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)) };
            var status = new TextBlock { FontSize = 12, Foreground = Brushes.DimGray, Text = L.T("Przygotowanie…") };
            Process proc = null;
            bool canceled = false;
            var cancelBtn = SmallButton(L.T("Anuluj"), () => { canceled = true; try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch (Exception) { } });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            buttons.Children.Add(cancelBtn);
            var text = new StackPanel { Margin = new Thickness(10, 8, 8, 8) };
            text.Children.Add(name); text.Children.Add(bar); text.Children.Add(status);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 1), Background = Brushes.White };
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons); row.Children.Add(text);
            AddRowToPanel(row);
            Downloads_Click(null, null);

            string finalFile = null, lastError = null;
            try
            {
                proc = Process.Start(psi);
                proc.OutputDataReceived += (s, e) =>
                {
                    var line = e.Data; if (line == null) return;
                    if (line.StartsWith("VELIVOFILE ")) { finalFile = line.Substring(11).Trim(); return; }
                    if (!line.StartsWith("VELIVO ")) return;
                    var p = line.Substring(7).Split('|');
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        double pct;
                        if (double.TryParse(p[0].Trim().TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pct))
                        { bar.IsIndeterminate = false; bar.Value = pct; }
                        status.Text = p[0].Trim() + (p.Length > 1 && p[1].Trim() != "NA" ? "  ·  " + p[1].Trim() : "") + (p.Length > 2 && p[2].Trim() != "NA" ? (L.En ? "  ·  left " : "  ·  zostało ") + p[2].Trim() : "");
                    }));
                };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null && e.Data.StartsWith("ERROR")) lastError = e.Data; };
                proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
                await Task.Run(() => proc.WaitForExit());
            }
            catch (Exception ex) { lastError = ex.Message; }

            buttons.Children.Clear();
            bar.IsIndeterminate = false;
            if (!canceled && finalFile != null && File.Exists(finalFile))
            {
                Integrity.MarkFromInternet(finalFile);
                bar.Value = 100; bar.Visibility = Visibility.Collapsed;
                name.Text = Path.GetFileName(finalFile);
                DlPanel.Children.Remove(row);
                var job = new Job
                {
                    Url = pageUrl, Referer = pageUrl, File = finalFile, State = JobState.Done, Added = DateTime.Now, Finished = DateTime.Now,
                    Total = new FileInfo(finalFile).Length, SingleGot = new FileInfo(finalFile).Length,
                };
                _jobs.Insert(0, job);
                BuildJobRow(job);
                DlPanel.Children.Insert(0, job.Row);
                SaveJobs();
                ShowToast(L.T("🎬 Pobrano: ") + Path.GetFileName(finalFile), finalFile);
            }
            else
            {
                status.Text = canceled ? L.T("Anulowano") : L.T("Nie udało się pobrać: ") + (lastError ?? "?");
                status.Foreground = canceled ? Brushes.Gray : Brushes.Firebrick;
                buttons.Children.Add(SmallButton(L.T("Usuń z listy"), () => { DlPanel.Children.Remove(row); UpdateDownloadsButton(); }));
            }
            UpdateDownloadsButton();
        }
    }
}
