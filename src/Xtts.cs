using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Velivo
{
    // 🗣 XTTS-v2 (Coqui, fork idiap „coqui-tts”) - czwarty silnik czytania, najbardziej ludzki, ale ciężki.
    // Pobierany tylko na życzenie (wybór głosu): Python 3.11 (wersja osadzona) + PyTorch + coqui-tts + model (~2 GB) do Dane\Xtts;
    // razem 3–6 GB (z kartą NVIDIA - wersja CUDA). Licencja modelu CPML - tylko użytek niekomercyjny.
    // Synteza w jednym procesie Pythona trzymanym w tle (model wczytany raz), jedna linia JSON = jeden plik WAV.
    public partial class MainWindow
    {
        const string XttsPyZip = "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip";
        const string XttsGetPip = "https://bootstrap.pypa.io/get-pip.py";

        static string XttsRoot => Path.Combine(VelivoData.Root, "Xtts");
        static string XttsPy => Path.Combine(XttsRoot, "python", "python.exe");
        static string XttsReady => Path.Combine(XttsRoot, "gotowe.txt");
        static bool XttsInstalled() => File.Exists(XttsPy) && File.Exists(XttsReady);
        static bool XttsHasNvidia() => File.Exists(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"));

        const string XttsScript = @"import sys, json, os
os.environ['COQUI_TOS_AGREED'] = '1'
import torch
from TTS.api import TTS
dev = 'cuda' if torch.cuda.is_available() else 'cpu'
tts = TTS('tts_models/multilingual/multi-dataset/xtts_v2').to(dev)
print('READY ' + dev, flush=True)
for line in sys.stdin:
    try:
        r = json.loads(line)
        tts.tts_to_file(text=r['text'], language=r['lang'], speaker=r['speaker'], speed=r['speed'], file_path=r['out'], split_sentences=False)
        print('OK ' + r['out'], flush=True)
    except Exception as e:
        print('ERR ' + str(e).replace('\n', ' '), flush=True)
";

        bool _xtDownloading;

        async Task<bool> RunXttsStep(string exe, string args, string what)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = XttsRoot };
            psi.Environment["TTS_HOME"] = Path.Combine(XttsRoot, "model");
            using (var p = Process.Start(psi))
            {
                var err = new StringBuilder();
                p.OutputDataReceived += (s, e) => { };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) { err.AppendLine(e.Data); if (err.Length > 4000) err.Remove(0, 2000); } };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                await Task.Run(() => p.WaitForExit());
                if (p.ExitCode == 0) return true;
                App.LogError(new Exception("XTTS " + what + ": " + err));
                ShowToast(L.T("❌ XTTS – nie udało się: ") + what + "\n" + err.ToString().Trim().Split('\n').LastOrDefault(), null);
                return false;
            }
        }

        async Task<bool> EnsureXttsAsync(PiperVoice v)
        {
            if (XttsInstalled()) return true;
            if (_xtDownloading) { ShowToast(L.T("🔊 Trwa pobieranie głosu – chwilę…"), null); return false; }
            bool nv = XttsHasNvidia();
            if (MessageBox.Show(L.T("Głos XTTS-v2 jest najbardziej naturalny, ale duży: pobierze się Python, PyTorch i model – razem ok. ") + (nv ? "6" : "3") + L.T(" GB (kilkanaście minut lub dłużej). Bez karty NVIDIA mówi wolno (lektor buduje się z wyprzedzeniem).\nLicencja modelu (CPML): tylko użytek niekomercyjny.\n\nPobrać teraz?"), "XTTS-v2", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            _xtDownloading = true;
            try
            {
                Directory.CreateDirectory(XttsRoot);
                var pyDir = Path.Combine(XttsRoot, "python");
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Velivo");
                    if (!File.Exists(XttsPy))
                    {
                        ShowToast("⬇ " + L.T("XTTS: pobieram Pythona…"), null);
                        var zip = Path.Combine(XttsRoot, "python.zip");
                        await DownloadFile(http, XttsPyZip, zip, null);
                        if (Directory.Exists(pyDir)) Directory.Delete(pyDir, true);
                        ZipFile.ExtractToDirectory(zip, pyDir); File.Delete(zip);
                        var pth = Directory.GetFiles(pyDir, "python3*._pth").FirstOrDefault();
                        if (pth != null) File.WriteAllText(pth, File.ReadAllText(pth).Replace("#import site", "import site"));   // pip i pakiety w site-packages
                    }
                    var gp = Path.Combine(XttsRoot, "get-pip.py");
                    if (!File.Exists(Path.Combine(pyDir, "Scripts", "pip.exe")))
                    {
                        await DownloadFile(http, XttsGetPip, gp, null);
                        if (!await RunXttsStep(XttsPy, "\"" + gp + "\" --no-warn-script-location", "pip")) return false;
                    }
                }
                ShowToast("⬇ " + L.T("XTTS: instaluję PyTorch (kilka GB, to potrwa)…"), null);
                string idx = nv ? " --index-url https://download.pytorch.org/whl/cu124" : " --index-url https://download.pytorch.org/whl/cpu";
                if (!await RunXttsStep(XttsPy, "-m pip install --no-warn-script-location torch==2.5.1 torchaudio==2.5.1" + idx, "PyTorch")) return false;
                ShowToast("⬇ " + L.T("XTTS: instaluję coqui-tts…"), null);
                if (!await RunXttsStep(XttsPy, "-m pip install --no-warn-script-location coqui-tts==0.25.3", "coqui-tts")) return false;
                File.WriteAllText(Path.Combine(XttsRoot, "velivo_xtts.py"), XttsScript);
                ShowToast("⬇ " + L.T("XTTS: pobieram model (ok. 2 GB)…"), null);
                if (!await RunXttsStep(XttsPy, "-c \"import os;os.environ['COQUI_TOS_AGREED']='1';from TTS.utils.manage import ModelManager;ModelManager().download_model('tts_models/multilingual/multi-dataset/xtts_v2')\"", L.T("model"))) return false;
                File.WriteAllText(XttsReady, DateTime.Now.ToString("s"));
                ShowToast(L.T("✅ Głos ") + "XTTS-v2" + L.T(" gotowy – działa bez internetu."), null);
                return true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                ShowToast(L.T("❌ Nie udało się pobrać XTTS: ") + ex.Message, null);
                return false;
            }
            finally { _xtDownloading = false; }
        }

        Process _xtProc;
        readonly SemaphoreSlim _xtLock = new SemaphoreSlim(1, 1);

        void StopXttsProcess()
        {
            try { if (_xtProc != null && !_xtProc.HasExited) _xtProc.Kill(); } catch (Exception) { }
            _xtProc = null;
        }

        async Task<string> XttsSynthesizeAsync(PiperVoice v, string text, double rate)
        {
            var line = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (line.Length == 0 || !XttsInstalled()) return null;
            await _xtLock.WaitAsync();
            try
            {
                if (_xtProc == null || _xtProc.HasExited)
                {
                    var psi = new ProcessStartInfo(XttsPy) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = XttsRoot };
                    psi.ArgumentList.Add("-u"); psi.ArgumentList.Add(Path.Combine(XttsRoot, "velivo_xtts.py"));
                    psi.Environment["TTS_HOME"] = Path.Combine(XttsRoot, "model");
                    psi.Environment["PYTHONIOENCODING"] = "utf-8";
                    _xtProc = Process.Start(psi);
                    _xtProc.ErrorDataReceived += (s, e) => { };
                    _xtProc.BeginErrorReadLine();
                    var rd = _xtProc.StandardOutput.ReadLineAsync();
                    if (await Task.WhenAny(rd, Task.Delay(180000)) != rd || !(rd.Result ?? "").StartsWith("READY")) { StopXttsProcess(); ShowToast(L.T("❌ XTTS nie wystartował – czytam innym głosem."), null); return null; }
                }
                Directory.CreateDirectory(PiperOutDir);
                var path = Path.Combine(PiperOutDir, "xt-" + Guid.NewGuid().ToString("N") + ".wav");
                var req = JsonSerializer.Serialize(new { text = line, lang = v.Lang, speaker = v.Xt, speed = Math.Max(0.7, Math.Min(1.6, rate)), @out = path });
                await _xtProc.StandardInput.WriteLineAsync(req);
                await _xtProc.StandardInput.FlushAsync();
                var r = _xtProc.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(r, Task.Delay(60000)) != r) { StopXttsProcess(); return null; }
                if (!(r.Result ?? "").StartsWith("OK")) { App.LogError(new Exception("XTTS: " + r.Result)); return null; }
                return File.Exists(path) ? path : null;
            }
            catch (Exception ex) { App.LogError(ex); StopXttsProcess(); return null; }
            finally { _xtLock.Release(); }
        }
    
        static long FileSize(string f) => File.Exists(f) ? new FileInfo(f).Length : 0;

        // 🧹 pobrane głosy, których nie używa ani czytanie stron (readTag - wybór z okna Ustawień), ani lektor filmów
        void CleanUnusedVoices(Window owner, string readTag)
        {
            var used = new[] { string.IsNullOrEmpty(readTag) ? ReadVoiceTag() : readTag,
                               string.IsNullOrEmpty(_settings.VlcLectorVoice) ? "piper:st3-M1-pl" : _settings.VlcLectorVoice }
                .Select(SelectedPiperVoice).Where(v => v != null).ToList();
            var items = new System.Collections.Generic.List<(string name, long size, Action del)>();
            foreach (var v in PiperVoices.Where(v => v.St == null && v.Xt == null))
            {
                var m = Path.Combine(PiperRoot, "voices", v.Id + ".onnx");
                if (File.Exists(m) && !used.Any(u => u.Id == v.Id))
                    items.Add(("Piper " + v.Label.Split(' ')[0], FileSize(m) + FileSize(m + ".json"), () => { File.Delete(m); if (File.Exists(m + ".json")) File.Delete(m + ".json"); }));
            }
            if (!used.Any(u => u.St == null && u.Xt == null) && Directory.Exists(Path.Combine(PiperRoot, "piper")))
                items.Add((L.T("Program Piper"), DirSize(Path.Combine(PiperRoot, "piper")), () => Directory.Delete(Path.Combine(PiperRoot, "piper"), true)));
            if (!used.Any(u => u.St != null) && Directory.Exists(SupertonicRoot))
                items.Add((L.T("Supertonic (model i wszystkie głosy)"), DirSize(SupertonicRoot), () => { _stTts = null; _stStyles.Clear(); GC.Collect(); GC.WaitForPendingFinalizers(); Directory.Delete(SupertonicRoot, true); }));
            if (!used.Any(u => u.Xt != null) && Directory.Exists(XttsRoot))
                items.Add((L.T("XTTS-v2 (Python, PyTorch, model)"), DirSize(XttsRoot), () => Directory.Delete(XttsRoot, true)));
            if (items.Count == 0) { MessageBox.Show(owner, L.T("Brak nieużywanych głosów na dysku – wszystkie pobrane są w użyciu."), "Velivo", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            string Mb(long b) => (b / 1048576.0).ToString("0") + " MB";
            var list = string.Join("\n", items.Select(x => "• " + x.name + " – " + Mb(x.size)));
            if (MessageBox.Show(owner, L.T("Nieużywane głosy (w użyciu: czytanie stron i lektor filmów – zostają):") + "\n\n" + list + "\n\n" + L.T("Razem: ") + Mb(items.Sum(x => x.size)) + "\n\n" + L.T("Usunąć je z dysku? W razie potrzeby pobiorą się ponownie."),
                "Velivo", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            StopPiperProcess(); StopXttsProcess();
            var failed = new System.Collections.Generic.List<string>();
            foreach (var x in items) { try { x.del(); } catch (Exception ex) { App.LogError(ex); failed.Add(x.name + ": " + ex.Message); } }
            MessageBox.Show(owner, failed.Count == 0 ? L.T("✅ Usunięto. Zwolniono ") + Mb(items.Sum(x => x.size)) + "."
                : L.T("Część nie dała się usunąć (plik w użyciu – uruchom Velivo ponownie i spróbuj jeszcze raz):") + "\n" + string.Join("\n", failed), "Velivo");
        }
    }
}
