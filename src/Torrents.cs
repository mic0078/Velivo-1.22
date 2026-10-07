using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Przegladarka
{
    // Torrenty (wlaczane w Ustawieniach, domyslnie wylaczone): linki magnet: i pliki .torrent pobiera darmowy aria2 -
    // doinstalowany dopiero przy pierwszym uzyciu (za zgoda), z przypieta suma SHA-256. Osobna strefa: wlasny folder
    // (domyslnie Pobrane\Velivo-Torrenty), pliki oznaczone jako z internetu, nic nie jest otwierane samo. Limity pobierania,
    // wysylania i udostepniania - w sekcji Torrenty w Ustawieniach. Zamkniecie Velivo zatrzymuje torrenty.
    public partial class MainWindow
    {
        const string Aria2ZipUrl = "https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip";
        const string Aria2ZipSha256 = "67d015301eef0b612191212d564c5bb0a14b5b9c4796b76454276a4d28d9b288";   // przypieta wersja 1.37.0 - inny plik nie zostanie uruchomiony
        static string Aria2Path { get { return Path.Combine(ToolsDir, "aria2c.exe"); } }
        readonly List<Process> _torrentProcs = new List<Process>();

        internal static bool IsMagnet(string s) { return (s ?? "").Trim().StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase); }

        // nazwa z magnet (dn=) albo skrot sumy (xt=) - do listy pobran
        internal static string MagnetName(string magnet)
        {
            var m = Regex.Match(magnet ?? "", @"[?&]dn=([^&]+)");
            if (m.Success) { try { return Uri.UnescapeDataString(m.Groups[1].Value.Replace('+', ' ')); } catch (Exception) { } }
            var h = Regex.Match(magnet ?? "", @"btih:([0-9a-zA-Z]+)");
            return h.Success ? "magnet " + h.Groups[1].Value.Substring(0, Math.Min(12, h.Groups[1].Value.Length)) : "torrent";
        }

        async Task<bool> EnsureAria2Async()
        {
            if (File.Exists(Aria2Path)) return true;
            if (MessageBox.Show(this, L.T("Do torrentów Velivo używa darmowego programu aria2 (ok. 2,5 MB, z serwisu GitHub).\n\nWażne: w torrentach Twój adres IP widzą inni uczestnicy wymiany. Jeśli chcesz pozostać anonimowy, użyj VPN.\n\nPobrać aria2 teraz? To jednorazowe."),
                L.T("Torrenty"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            var zip = Path.Combine(ToolsDir, "aria2.zip");
            try
            {
                Directory.CreateDirectory(ToolsDir);
                ShowToast(L.T("⬇ Pobieram narzędzie ") + "aria2…", null);
                using (var resp = await ToolHttp.GetAsync(Aria2ZipUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var fs = File.Create(zip)) await resp.Content.CopyToAsync(fs);
                }
                if (!Integrity.Matches(zip, Aria2ZipSha256)) throw new InvalidDataException(L.T("Suma kontrolna pobranego pliku się nie zgadza – plik nie zostanie uruchomiony."));
                await Task.Run(() =>
                {
                    using (var a = ZipFile.OpenRead(zip))
                    {
                        var entry = a.Entries.FirstOrDefault(x => x.Name.Equals("aria2c.exe", StringComparison.OrdinalIgnoreCase));
                        if (entry == null) throw new IOException("aria2c.exe not found");
                        entry.ExtractToFile(Aria2Path, true);
                    }
                });
                return true;
            }
            catch (Exception ex) { App.LogError(ex); MessageBox.Show(this, L.T("Nie udało się pobrać aria2:\n") + ex.Message, "Velivo"); return false; }
            finally { try { File.Delete(zip); } catch (Exception) { } }
        }

        // source = link magnet: albo pobrany plik .torrent
        async Task StartTorrentAsync(string source)
        {
            if (!_settings.Torrents || string.IsNullOrWhiteSpace(source)) return;
            if (!await EnsureAria2Async()) return;
            var dir = TorrentZone();
            if (_settings.AskDownload)   // jak przy zwyklym pobieraniu: wybor folderu (start w strefie torrentow)
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Gdzie pobrać torrent?") };
                try { Directory.CreateDirectory(dir); dlg.InitialDirectory = dir; } catch (Exception) { }
                if (dlg.ShowDialog(this) != true) return;
                dir = dlg.FolderName;
            }
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { MessageBox.Show(this, L.T("Nie można użyć folderu strefy torrentów:\n") + ex.Message, L.T("Torrenty")); return; }
            var psi = new ProcessStartInfo(Aria2Path)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = dir,
            };
            foreach (var a in Aria2Args(dir, _settings.TorrentDownKb, _settings.TorrentUpKb, _settings.TorrentRatio, _settings.TorrentSeedMin, _settings.TorrentPeers))
                psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(source.Trim());

            var title = IsMagnet(source) ? MagnetName(source) : Path.GetFileNameWithoutExtension(source);
            var name = new TextBlock { Text = "🧲 " + title, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            var bar = new ProgressBar { Height = 10, Margin = new Thickness(0, 4, 0, 4), IsIndeterminate = true, Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)) };
            var status = new TextBlock { FontSize = 12, Foreground = Brushes.DimGray, Text = L.T("Szukam uczestników wymiany…") };
            Process proc = null; bool stopped = false;
            var stopBtn = SmallButton(L.T("Zatrzymaj"), () => { stopped = true; try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch (Exception) { } });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            buttons.Children.Add(stopBtn);
            var text = new StackPanel { Margin = new Thickness(10, 8, 8, 8) };
            text.Children.Add(name); text.Children.Add(bar); text.Children.Add(status);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 1), Background = Brushes.White };
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons); row.Children.Add(text);
            AddRowToPanel(row);
            Downloads_Click(null, null);

            string done = null, lastError = null; bool seeding = false;
            try
            {
                proc = Process.Start(psi);
                lock (_torrentProcs) _torrentProcs.Add(proc);
                proc.OutputDataReceived += (s, e) =>
                {
                    var line = e.Data; if (line == null) return;
                    var fin = TorrentDoneFile(line); if (fin != null) { done = fin; return; }
                    var p = TorrentProgress(line); if (p == null) return;
                    seeding = p.Item4;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (p.Item1 >= 0) { bar.IsIndeterminate = false; bar.Value = p.Item1; }
                        status.Text = p.Item4 ? L.T("✔ Pobrane – udostępniam (limit w Ustawieniach → Torrenty)")
                            : p.Item1 + "%" + (p.Item2 != null ? "  ·  " + p.Item2 + "/s" : "") + (p.Item3 != null ? (L.En ? "  ·  left " : "  ·  zostało ") + p.Item3 : "");
                    }));
                };
                proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastError = e.Data.Trim(); };
                proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
                await Task.Run(() => proc.WaitForExit());
            }
            catch (Exception ex) { lastError = ex.Message; }
            finally { if (proc != null) lock (_torrentProcs) _torrentProcs.Remove(proc); }

            buttons.Children.Clear();
            bar.IsIndeterminate = false;
            bool ok = proc != null && proc.HasExited && proc.ExitCode == 0;
            if (ok || (stopped && seeding))
            {
                bar.Value = 100;
                status.Text = L.T("✔ Pobrano") + (done != null ? ": " + done : "");
                var target = done != null && (File.Exists(done) || Directory.Exists(done)) ? done : dir;
                MarkTorrentFiles(target);   // strefa: kazdy plik oznaczony jako z internetu (Windows sprawdzi go przed uruchomieniem)
                buttons.Children.Add(SmallButton(L.T("Pokaż w folderze"), () => { try { Process.Start("explorer.exe", File.Exists(target) ? "/select,\"" + target + "\"" : "\"" + target + "\""); } catch (Exception) { } }));
                ShowToast(L.T("🧲 Pobrano torrent: ") + title, null);
            }
            else
            {
                status.Text = stopped ? L.T("Zatrzymano") : L.T("Nie udało się pobrać: ") + (lastError ?? "?");
                status.Foreground = stopped ? Brushes.Gray : Brushes.Firebrick;
            }
            buttons.Children.Add(SmallButton(L.T("Usuń z listy"), () => { DlPanel.Children.Remove(row); UpdateDownloadsButton(); }));
            UpdateDownloadsButton();
        }

        string TorrentZone()
        {
            return string.IsNullOrWhiteSpace(_settings.TorrentDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Velivo-Torrenty")
                : _settings.TorrentDir.Trim();
        }

        // Opcje aria2 z ustawien: limity (0 = bez limitu), udostepnianie (wspolczynnik 0 = wcale), najwiecej uczestnikow.
        // Po opcjach "--": dalej tylko zrodlo (link / plik), nigdy kolejna opcja.
        internal static List<string> Aria2Args(string dir, int downKb, int upKb, double ratio, int seedMin, int peers)
        {
            var a = new List<string> { "--dir=" + dir };
            if (downKb > 0) a.Add("--max-overall-download-limit=" + downKb + "K");
            if (upKb > 0) a.Add("--max-upload-limit=" + upKb + "K");
            if (ratio <= 0) a.Add("--seed-time=0");
            else { a.Add("--seed-ratio=" + ratio.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)); a.Add("--seed-time=" + Math.Max(1, seedMin)); }
            a.Add("--bt-max-peers=" + Math.Max(5, peers));
            a.AddRange(new[] { "--summary-interval=1", "--console-log-level=warn", "--enable-color=false", "--file-allocation=falloc",
                               "--bt-save-metadata=false", "--auto-file-renaming=true", "--allow-overwrite=false", "--" });
            return a;
        }

        // wynik aria2 na koncu: "803aab|OK  |   657MiB/s|C:\Pobrane\plik.iso" - sciezka pobranego pliku (bez metadanych magnet)
        internal static string TorrentDoneFile(string line)
        {
            var m = Regex.Match(line ?? "", @"^[0-9a-f]{6}\|OK\s*\|[^|]*\|(.+)$");
            if (!m.Success) return null;
            var path = m.Groups[1].Value.Trim();
            return path.Length == 0 || path.Contains("[METADATA]") || path.Contains("[MEMORY]") ? null : path;
        }

        // linia postepu aria2: "[#2089b0 400KiB/33MiB(1%) CN:44 DL:115KiB ETA:4m53s]" albo "[#2089b0 SEED(1.0) CN:3 UL:20KiB]"
        // -> (procent, predkosc, zostalo, udostepnianie); null = to nie linia postepu
        internal static Tuple<int, string, string, bool> TorrentProgress(string line)
        {
            if (line == null || !line.Contains("[#")) return null;
            if (Regex.IsMatch(line, @"\bSEED\(")) return Tuple.Create(100, (string)null, (string)null, true);
            var pct = Regex.Match(line, @"\((\d{1,3})%\)");
            var dl = Regex.Match(line, @"\bDL:([0-9.]+[KMG]?i?B)");
            var eta = Regex.Match(line, @"\bETA:([0-9hms]+)");
            if (!pct.Success && !dl.Success) return null;
            return Tuple.Create(pct.Success ? int.Parse(pct.Groups[1].Value) : -1, dl.Success ? dl.Groups[1].Value : null, eta.Success ? eta.Groups[1].Value : null, false);
        }

        static void MarkTorrentFiles(string path)
        {
            try
            {
                if (File.Exists(path)) { Integrity.MarkFromInternet(path); return; }
                if (Directory.Exists(path)) foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) Integrity.MarkFromInternet(f);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // zamkniecie Velivo - torrenty nie dzialaja dalej po cichu w tle
        void StopTorrents()
        {
            lock (_torrentProcs)
                foreach (var p in _torrentProcs.ToList()) { try { if (!p.HasExited) p.Kill(true); } catch (Exception) { } }
        }

        // pobrany plik .torrent - start pobierania (tylko gdy torrenty wlaczone)
        void OnFileDownloaded(string file)
        {
            if (_settings.Torrents && file != null && file.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                _ = StartTorrentAsync(file);
        }
    }
}
