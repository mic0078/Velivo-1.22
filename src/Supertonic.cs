using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Velivo.SupertonicTts;

namespace Velivo
{
    // 🗣 Supertonic 3 - trzeci silnik czytania (obok Pipera i głosów Windows): lokalnie, bez chmury i kont.
    // Model (~400 MB) pobiera się raz, przy pierwszym wyborze głosu, do Dane\Supertonic; synteza w tle (nie blokuje okna),
    // karta graficzna przez DirectML, zapasowo procesor. Wynik - plik WAV jak u Pipera (to samo odtwarzanie i lektor).
    public partial class MainWindow
    {
        // archiwum modelu (Supertone zakończyło działalność - wersja przypięta do zatwierdzenia, nie zmieni się)
        const string SupertonicBase = "https://huggingface.co/supertone-oss-archive/supertonic-3/resolve/aafc6e32416a594460b32413efc49d7fe4ce6d46/";
        static readonly string[] SupertonicModelFiles = { "onnx/duration_predictor.onnx", "onnx/text_encoder.onnx", "onnx/vector_estimator.onnx", "onnx/vocoder.onnx", "onnx/tts.json", "onnx/unicode_indexer.json" };

        static string SupertonicRoot => Path.Combine(VelivoData.Root, "Supertonic");
        static string SupertonicPath(string rel) => Path.Combine(SupertonicRoot, rel.Replace('/', Path.DirectorySeparatorChar));

        static bool SupertonicInstalled(PiperVoice v) =>
            SupertonicModelFiles.All(f => File.Exists(SupertonicPath(f))) && File.Exists(SupertonicPath("voice_styles/" + v.St + ".json"));

        bool _stDownloading;

        async Task<bool> EnsureSupertonicAsync(PiperVoice v)
        {
            if (SupertonicInstalled(v)) return true;
            if (_stDownloading) { ShowToast(L.T("🔊 Trwa pobieranie głosu – chwilę…"), null); return false; }
            _stDownloading = true;
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Velivo");
                    DateTime lastToast = DateTime.MinValue;
                    foreach (var rel in SupertonicModelFiles.Concat(new[] { "voice_styles/" + v.St + ".json" }))
                    {
                        var target = SupertonicPath(rel);
                        if (File.Exists(target)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        await DownloadFile(http, SupertonicBase + rel + "?download=true", target, (d, t) =>
                        {
                            if (DateTime.UtcNow - lastToast < TimeSpan.FromSeconds(2)) return;
                            lastToast = DateTime.UtcNow;
                            string pct = t > 0 ? (d * 100 / t) + "%" : (d / 1048576) + " MB";
                            Dispatcher.BeginInvoke(new Action(() => ShowToast("⬇ " + L.T("Pobieram głos Supertonic: ") + Path.GetFileName(rel) + " " + pct, null)));
                        });
                    }
                }
                ShowToast(L.T("✅ Głos ") + "Supertonic " + v.St + L.T(" gotowy – działa bez internetu."), null);
                return true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                ShowToast(L.T("❌ Nie udało się pobrać głosu Supertonic: ") + ex.Message + L.T("\nCzytam głosem systemowym."), null);
                return false;
            }
            finally { _stDownloading = false; }
        }

        TextToSpeech _stTts;
        bool _stGpu;
        readonly Dictionary<string, Style> _stStyles = new Dictionary<string, Style>();
        readonly SemaphoreSlim _stLock = new SemaphoreSlim(1, 1);

        // synteza w tle; jedna naraz (model trzymany w pamięci po pierwszym użyciu)
        async Task<string> SupertonicSynthesizeAsync(PiperVoice v, string text, double rate, int steps)
        {
            var line = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (line.Length == 0 || !SupertonicInstalled(v)) return null;
            await _stLock.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    if (_stTts == null)
                    {
                        try { _stTts = Helper.LoadTextToSpeech(SupertonicPath("onnx"), true); _stGpu = true; }
                        catch (Exception ex) { App.LogError(ex); _stTts = Helper.LoadTextToSpeech(SupertonicPath("onnx"), false); _stGpu = false; }   // brak DirectML - procesor
                    }
                    if (!_stStyles.TryGetValue(v.St, out var style))
                        _stStyles[v.St] = style = Helper.LoadVoiceStyle(new List<string> { SupertonicPath("voice_styles/" + v.St + ".json") });
                    float speed = (float)Math.Max(0.7, Math.Min(2.0, 1.05 * rate));
                    var (wav, _) = _stTts.Call(line, v.Lang, style, steps > 0 ? steps : _stGpu ? 8 : 5, speed, 0.2f);   // kroki (Jakość w panelu lektora); samo: karta graficzna 8, procesor 5
                    Directory.CreateDirectory(PiperOutDir);
                    var path = Path.Combine(PiperOutDir, "st-" + Guid.NewGuid().ToString("N") + ".wav");
                    var padded = new float[wav.Length + _stTts.SampleRate / 8]; Array.Copy(wav, padded, wav.Length);   // 0,12 s ciszy na koncu - ostatnie slowo nie ginie w buforze dzwieku
                    Helper.WriteWavFile(path, padded, _stTts.SampleRate);
                    return path;
                });
            }
            catch (Exception ex) { App.LogError(ex); _stTts = null; return null; }
            finally { _stLock.Release(); }
        }
    }
}
