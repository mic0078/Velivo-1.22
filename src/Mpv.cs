using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Velivo
{
    // 🎞 PRÓBA (gałąź plynny-ruch-mpv): film z biblioteki w drugim silniku - mpv - z płynnym ruchem (makieta dokumentacja/makieta-plynny-ruch-mpv.html).
    // mpv pobierany raz z GitHub (shinchiro/mpv-winbuild-cmake, archiwum .7z rozpakowane narzędziem 7zr.exe z 7-zip.org) do narzedzia\mpv.
    // Tryby: 1 wygładzanie (mpv interpolation - bez szarpania 24 kl./s na ekranie 60 Hz, lekkie), 2 jak w telewizorze (dorabianie klatek filtrem FFmpeg minterpolate - ciężkie, procesor).
    // Miejsce oglądania: mpv zapisuje przy wyjściu (watch_later), Velivo przenosi je do biblioteki. TV, Audio Balance, lektor, Pilot - dalej VLC.
    public partial class MainWindow
    {
        static string MpvDir => Path.Combine(ToolsDir, "mpv");
        static string MpvExe => Path.Combine(MpvDir, "mpv.exe");

        async Task<bool> MpvEnsureAsync()
        {
            if (File.Exists(MpvExe)) return true;
            if (MessageBox.Show(this, L.T("Płynny ruch używa darmowego odtwarzacza mpv (ok. 35 MB, z serwisu GitHub).\n\nPobrać go teraz? To jednorazowe."), "🎞", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            ShowToast(L.T("⬇ Pobieram mpv…"), null);
            try
            {
                Directory.CreateDirectory(MpvDir);
                var seven = Path.Combine(ToolsDir, "7zr.exe");
                if (!File.Exists(seven)) await MpvGet("https://www.7-zip.org/a/7zr.exe", seven);
                // najnowsze wydanie: archiwum dla zwykłych procesorów 64-bit (bez „v3” i „dev”)
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest");
                req.Headers.UserAgent.ParseAdd("Velivo");
                using var resp = await ToolHttp.SendAsync(req); resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var url = doc.RootElement.GetProperty("assets").EnumerateArray().Select(a => (n: a.GetProperty("name").GetString(), u: a.GetProperty("browser_download_url").GetString()))
                    .FirstOrDefault(a => a.n != null && a.n.StartsWith("mpv-x86_64-2") && a.n.EndsWith(".7z")).u;
                if (url == null) throw new IOException(L.T("brak archiwum mpv w najnowszym wydaniu"));
                var arc = Path.Combine(ToolsDir, "mpv.7z");
                await MpvGet(url, arc);
                using (var p = Process.Start(new ProcessStartInfo(seven, "x -y -o\"" + MpvDir + "\" \"" + arc + "\"") { UseShellExecute = false, CreateNoWindow = true }))
                    await Task.Run(() => p.WaitForExit(180000));
                try { File.Delete(arc); } catch (Exception) { }
                if (!File.Exists(MpvExe)) throw new IOException("mpv.exe");
                return true;
            }
            catch (Exception ex) { App.LogError(ex); MessageBox.Show(this, L.T("Nie udało się pobrać mpv:\n") + ex.Message, "🎞"); return false; }
        }
        static async Task MpvGet(string url, string target)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url); req.Headers.UserAgent.ParseAdd("Velivo");
            using var resp = await ToolHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead); resp.EnsureSuccessStatusCode();
            using (var fs = File.Create(target + ".tmp")) await resp.Content.CopyToAsync(fs);
            File.Move(target + ".tmp", target, true);
        }

        async void MpvPlay(string path, int tryb)
        {
            if (!await MpvEnsureAsync()) return;
            var src = File.Exists(path) ? path : JellyfinStreamUrl(path) ?? path;   // film tylko w Jellyfin - prosto z serwera
            var lib = LoadLibrary(); var e = FindEntry(lib, path, null);
            long start = e != null && !e.Done ? e.Pos : 0;
            var wl = Path.Combine(MpvDir, "pozycje"); try { if (Directory.Exists(wl)) Directory.Delete(wl, true); } catch (Exception) { }
            var tytul = e?.MyTitle ?? e?.Title ?? Path.GetFileNameWithoutExtension(path);
            string opis = tryb == 2 ? L.T("płynny ruch: jak w telewizorze (próba)") : L.T("płynny ruch: wygładzanie");
            var a = "--force-window=yes --keep-open=no --save-position-on-quit --watch-later-dir=\"" + wl + "\" --title=\"" + tytul.Replace("\"", "'") + "\""
                + " --video-sync=display-resample --interpolation --tscale=oversample"   // wygładzanie (oba tryby)
                + (tryb == 2 ? " --hwdec=no --vf=\"lavfi=[minterpolate=fps=60:mi_mode=mci:mc_mode=obmc:me_mode=bidir:vsbmc=1:scd=fdiff]\"" : " --hwdec=auto-safe")
                + " --osd-playing-msg=\"🎞 " + opis + "\" --osd-duration=4000"
                + (start > 5000 ? " --start=" + (start / 1000) : "") + " \"" + src + "\"";
            try
            {
                using var p = Process.Start(new ProcessStartInfo(MpvExe, a) { UseShellExecute = false, WorkingDirectory = MpvDir });
                await Task.Run(() => p.WaitForExit());
            }
            catch (Exception ex) { App.LogError(ex); MessageBox.Show(this, L.T("Nie udało się uruchomić mpv:\n") + ex.Message, "🎞"); return; }
            // miejsce oglądania z pliku watch_later („start=…” w sekundach) - jak przy zwykłym odtwarzaniu
            try
            {
                var f = Directory.Exists(wl) ? Directory.GetFiles(wl).FirstOrDefault() : null;
                var line = f == null ? null : File.ReadAllLines(f).FirstOrDefault(l => l.StartsWith("start="));
                lib = LoadLibrary(); e = FindEntry(lib, path, null);
                if (e != null)
                {
                    e.Last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (line != null && double.TryParse(line.Substring(6), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec)) { e.Pos = (long)(sec * 1000); e.Done = false; }
                    else if (f == null) { e.Pos = 0; e.Done = true; }   // obejrzany do końca (mpv nie zapisuje miejsca)
                    SaveLibrary(lib);
                }
            }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
