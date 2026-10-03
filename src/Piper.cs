using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Naturalne glosy offline - Piper (https://github.com/rhasspy/piper, licencja MIT).
    // Program i glos pobieraja sie raz, przy pierwszym uzyciu, do %LOCALAPPDATA%\Przegladarka\Piper.
    // Strona (ReaderScript) dzieli tekst na zdania i podswietla je; tutaj powstaje i gra dzwiek.
    public partial class MainWindow
    {
        sealed class PiperVoice
        {
            public string Id, Label, Lang, Path;
        }

        // Tag w ustawieniach: "piper:<Id>"
        static readonly PiperVoice[] PiperVoices =
        {
            new PiperVoice { Id = "pl_PL-gosia-medium",     Label = "Gosia (polski, naturalny offline)",      Lang = "pl", Path = "pl/pl_PL/gosia/medium/" },
            new PiperVoice { Id = "pl_PL-darkman-medium",   Label = "Darkman (polski, naturalny offline)",    Lang = "pl", Path = "pl/pl_PL/darkman/medium/" },
            new PiperVoice { Id = "pl_PL-mc_speech-medium", Label = "MC Speech (polski, naturalny offline)",  Lang = "pl", Path = "pl/pl_PL/mc_speech/medium/" },
            new PiperVoice { Id = "en_US-lessac-medium",    Label = "Lessac (angielski US, naturalny offline)", Lang = "en", Path = "en/en_US/lessac/medium/" },
            new PiperVoice { Id = "en_US-ryan-medium",      Label = "Ryan (angielski US, naturalny offline)",   Lang = "en", Path = "en/en_US/ryan/medium/" },
            new PiperVoice { Id = "en_GB-alba-medium",      Label = "Alba (angielski UK, naturalny offline)",   Lang = "en", Path = "en/en_GB/alba/medium/" },
        };

        const string PiperZipUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
        const string PiperVoicesBase = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/";

        static string PiperRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Przegladarka", "Piper"); } }
        static string PiperExe { get { return Path.Combine(PiperRoot, "piper", "piper.exe"); } }

        static PiperVoice SelectedPiperVoice(string tag)
        {
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith("piper:", StringComparison.Ordinal)) return null;
            var id = tag.Substring(6);
            return PiperVoices.FirstOrDefault(v => v.Id == id);
        }

        static bool PiperVoiceInstalled(PiperVoice v)
        {
            return File.Exists(PiperExe) && File.Exists(Path.Combine(PiperRoot, "voices", v.Id + ".onnx")) && File.Exists(Path.Combine(PiperRoot, "voices", v.Id + ".onnx.json"));
        }

        static async Task DownloadFile(HttpClient http, string url, string target, Action<long, long> progress)
        {
            var tmp = target + ".part";
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = File.Create(tmp))
                {
                    var buf = new byte[81920];
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n);
                        done += n;
                        progress?.Invoke(done, total);
                    }
                }
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
        }

        bool _piperDownloading;

        // Pobiera program Piper i wybrany glos, jesli ich nie ma. Zwraca false przy bledzie.
        async Task<bool> EnsurePiperAsync(PiperVoice v)
        {
            if (PiperVoiceInstalled(v)) return true;
            if (_piperDownloading) { ShowToast("🔊 Trwa pobieranie głosu – chwilę…", null); return false; }
            _piperDownloading = true;
            try
            {
                Directory.CreateDirectory(Path.Combine(PiperRoot, "voices"));
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Velivo");
                    DateTime lastToast = DateTime.MinValue;
                    Action<string, long, long> report = (what, d, t) =>
                    {
                        if (DateTime.UtcNow - lastToast < TimeSpan.FromSeconds(2)) return;
                        lastToast = DateTime.UtcNow;
                        string pct = t > 0 ? (d * 100 / t) + "%" : (d / 1048576) + " MB";
                        Dispatcher.BeginInvoke(new Action(() => ShowToast("⬇ Pobieram " + what + ": " + pct, null)));
                    };
                    if (!File.Exists(PiperExe))
                    {
                        var zip = Path.Combine(PiperRoot, "piper.zip");
                        await DownloadFile(http, PiperZipUrl, zip, (d, t) => report("program Piper", d, t));
                        var dir = Path.Combine(PiperRoot, "piper");
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                        ZipFile.ExtractToDirectory(zip, PiperRoot);   // paczka zawiera folder "piper"
                        File.Delete(zip);
                        if (!File.Exists(PiperExe)) throw new FileNotFoundException("W paczce Piper brak piper.exe.");
                    }
                    var model = Path.Combine(PiperRoot, "voices", v.Id + ".onnx");
                    if (!File.Exists(model + ".json"))
                        await DownloadFile(http, PiperVoicesBase + v.Path + v.Id + ".onnx.json?download=true", model + ".json", null);
                    if (!File.Exists(model))
                        await DownloadFile(http, PiperVoicesBase + v.Path + v.Id + ".onnx?download=true", model, (d, t) => report("głos " + v.Label.Split(' ')[0], d, t));
                }
                ShowToast("✅ Głos " + v.Label.Split(' ')[0] + " gotowy – działa bez internetu.", null);
                return true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                ShowToast("❌ Nie udało się pobrać głosu Piper: " + ex.Message + "\nCzytam głosem systemowym.", null);
                return false;
            }
            finally { _piperDownloading = false; }
        }

        // ---------- proces Piper (jeden, trzymany w tle) ----------
        Process _piperProc;
        string _piperProcKey;
        readonly SemaphoreSlim _piperLock = new SemaphoreSlim(1, 1);
        static string PiperOutDir { get { return Path.Combine(Path.GetTempPath(), "velivo-piper"); } }

        void StopPiperProcess()
        {
            try { if (_piperProc != null && !_piperProc.HasExited) _piperProc.Kill(); } catch (Exception) { }
            _piperProc = null; _piperProcKey = null;
        }

        async Task<string> PiperSynthesizeAsync(PiperVoice v, string text, double rate)
        {
            await _piperLock.WaitAsync();
            try
            {
                double lengthScale = Math.Max(0.4, Math.Min(2.0, 1.0 / Math.Max(0.5, rate)));
                string key = v.Id + "|" + lengthScale.ToString("0.00", CultureInfo.InvariantCulture);
                if (_piperProc == null || _piperProc.HasExited || _piperProcKey != key)
                {
                    StopPiperProcess();
                    Directory.CreateDirectory(PiperOutDir);
                    var psi = new ProcessStartInfo(PiperExe)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                        WorkingDirectory = Path.GetDirectoryName(PiperExe)
                    };
                    psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(Path.Combine(PiperRoot, "voices", v.Id + ".onnx"));
                    psi.ArgumentList.Add("--output_dir"); psi.ArgumentList.Add(PiperOutDir);
                    psi.ArgumentList.Add("--length_scale"); psi.ArgumentList.Add(lengthScale.ToString("0.00", CultureInfo.InvariantCulture));
                    psi.ArgumentList.Add("--sentence_silence"); psi.ArgumentList.Add("0.15");
                    _piperProc = Process.Start(psi);
                    _piperProc.ErrorDataReceived += (s, e) => { };
                    _piperProc.BeginErrorReadLine();
                    _piperProcKey = key;
                }
                // jedna linia = jeden plik WAV; Piper wypisuje jego sciezke
                var line = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (line.Length == 0) return null;
                await _piperProc.StandardInput.WriteLineAsync(line);
                await _piperProc.StandardInput.FlushAsync();
                var readTask = _piperProc.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(readTask, Task.Delay(30000)) != readTask) { StopPiperProcess(); return null; }
                var path = (readTask.Result ?? "").Trim();
                return File.Exists(path) ? path : null;
            }
            catch (Exception ex) { App.LogError(ex); StopPiperProcess(); return null; }
            finally { _piperLock.Release(); }
        }

        // ---------- odtwarzanie sterowane przez strone ----------
        MediaPlayer _piperPlayer;
        readonly HashSet<CoreWebView2> _piperDriven = new HashSet<CoreWebView2>();

        void SetPiperVolume(double v)
        {
            if (_piperPlayer != null) _piperPlayer.Volume = Math.Max(0, Math.Min(1, v));   // od razu, w trakcie zdania
        }

        // Wlacza tryb Piper na stronie (gdy wybrany glos Piper) i uruchamia petle odtwarzania. Zwraca false = czytaj systemowo.
        async Task<bool> PreparePiperReading(CoreWebView2 core)
        {
            var v = SelectedPiperVoice(_settings.ReadVoice);
            if (v == null || core == null) { try { if (core != null) await core.ExecuteScriptAsync("window.__velivoRead && window.__velivoRead.setExternal(false)"); } catch (Exception) { } return false; }
            if (!await EnsurePiperAsync(v)) { await core.ExecuteScriptAsync("window.__velivoRead && window.__velivoRead.setExternal(false)"); return false; }
            await core.ExecuteScriptAsync("window.__velivoRead && window.__velivoRead.setExternal(true)");
            if (!_piperDriven.Contains(core)) { _piperDriven.Add(core); _ = PiperDriveLoop(core, v); }
            return true;
        }

        sealed class PiperPending { public int seq { get; set; } public int i { get; set; } public string text { get; set; } public string next { get; set; } }

        async Task<JsonElement?> PiperState(CoreWebView2 core)
        {
            var raw = await core.ExecuteScriptAsync("window.__velivoRead ? window.__velivoRead.state() : ''");
            var str = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(str)) return null;
            return JsonDocument.Parse(str).RootElement.Clone();
        }

        async Task PiperDriveLoop(CoreWebView2 core, PiperVoice voice)
        {
            string cachedText = null; Task<string> cachedWav = null;
            int idle = 0;
            try
            {
                while (true)
                {
                    await Task.Delay(80);
                    string raw;
                    try { raw = await core.ExecuteScriptAsync("window.__velivoRead ? window.__velivoRead.take() : 'X'"); }
                    catch (Exception) { break; }   // strona/okno zamkniete
                    var str = JsonSerializer.Deserialize<string>(raw);
                    if (str == "X") break;
                    if (string.IsNullOrEmpty(str)) { if (++idle > 750) break; continue; }   // ~1 min bez czytania - konczymy
                    idle = 0;
                    var p = JsonSerializer.Deserialize<PiperPending>(str);
                    var v = SelectedPiperVoice(_settings.ReadVoice) ?? voice;

                    var wavTask = (cachedText == p.text && cachedWav != null) ? cachedWav : PiperSynthesizeAsync(v, p.text, _settings.ReadRate);
                    var wav = await wavTask;
                    // nastepne zdanie liczymy w tle, gdy gra biezace
                    cachedText = p.next; cachedWav = string.IsNullOrEmpty(p.next) ? null : PiperSynthesizeAsync(v, p.next, _settings.ReadRate);

                    var st = await PiperState(core);
                    if (st == null || !st.Value.GetProperty("active").GetBoolean() || st.Value.GetProperty("paused").GetBoolean() || st.Value.GetProperty("seq").GetInt32() != p.seq) continue;
                    if (wav == null) { await core.ExecuteScriptAsync("window.__velivoRead.done(" + p.seq + ")"); continue; }

                    var ended = new TaskCompletionSource<bool>();
                    var player = new MediaPlayer { Volume = _settings.ReadVolume };
                    _piperPlayer = player;
                    player.MediaEnded += (s, e) => ended.TrySetResult(true);
                    player.MediaFailed += (s, e) => ended.TrySetResult(false);
                    player.Open(new Uri(wav));
                    player.Play();
                    bool interrupted = false;
                    while (!ended.Task.IsCompleted)
                    {
                        await Task.WhenAny(ended.Task, Task.Delay(150));
                        if (ended.Task.IsCompleted) break;
                        JsonElement? s2;
                        try { s2 = await PiperState(core); } catch (Exception) { s2 = null; }
                        if (s2 == null || !s2.Value.GetProperty("active").GetBoolean() || s2.Value.GetProperty("paused").GetBoolean() || s2.Value.GetProperty("seq").GetInt32() != p.seq)
                        { interrupted = true; break; }
                    }
                    player.Stop(); player.Close();
                    if (_piperPlayer == player) _piperPlayer = null;
                    try { File.Delete(wav); } catch (Exception) { }
                    if (!interrupted) await core.ExecuteScriptAsync("window.__velivoRead && window.__velivoRead.done(" + p.seq + ")");
                }
            }
            catch (Exception ex) { App.LogError(ex); }
            finally
            {
                _piperDriven.Remove(core);
                if (_piperPlayer != null) { try { _piperPlayer.Stop(); _piperPlayer.Close(); } catch (Exception) { } _piperPlayer = null; }
            }
        }
    }
}
