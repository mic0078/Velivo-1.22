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

        // okno postępu instalacji XTTS - widoczne do samego końca, można je zminimalizować (instalacja trwa dalej)
        Window _xtWin; System.Windows.Controls.TextBlock _xtStep, _xtInfo, _xtLine; System.Windows.Controls.Button _xtClose;
        static readonly string[] XtSteps = { "Python", "pip", "PyTorch", "coqui-tts", "model" };

        void XtShow()
        {
            if (_xtWin != null) { if (_xtWin.WindowState == WindowState.Minimized) _xtWin.WindowState = WindowState.Normal; _xtWin.Activate(); return; }
            var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new System.Windows.Controls.TextBlock { Text = L.T("Instalacja głosu XTTS-v2 (kilka GB). Okno możesz zminimalizować – instalacja trwa dalej. Nie zamykaj Velivo. Do końca czytanie idzie głosem systemowym."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            sp.Children.Add(_xtStep = new System.Windows.Controls.TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(_xtInfo = new System.Windows.Controls.TextBlock { Margin = new Thickness(0, 6, 0, 0) });
            sp.Children.Add(_xtLine = new System.Windows.Controls.TextBlock { Margin = new Thickness(0, 6, 0, 0), Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = L.T("Ostatni komunikat instalatora") });
            sp.Children.Add(_xtClose = new System.Windows.Controls.Button { Content = L.T("Zamknij"), Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed });
            _xtWin = new Window { Title = "Velivo – XTTS-v2", Content = sp, Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.CanMinimize, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true };
            _xtClose.Click += (a, b) => _xtWin?.Close();
            _xtWin.Closed += (a, b) => _xtWin = null;
            ApplyWindowTheme(_xtWin);
            _xtWin.Show();
        }

        void XtStatus(int step, string text, bool end = false)
        {
            if (_xtWin == null && !end) XtShow();
            if (_xtWin == null) return;
            _xtStep.Text = step > 0 ? L.T("Krok ") + step + "/5: " + text : text;
            if (end) { _xtInfo.Text = ""; _xtClose.Visibility = Visibility.Visible; if (_xtWin.WindowState == WindowState.Minimized) _xtWin.WindowState = WindowState.Normal; }
        }

        async Task<bool> RunXttsStep(string exe, string args, string what)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = XttsRoot };
            psi.Environment["TTS_HOME"] = Path.Combine(XttsRoot, "model");
            psi.Environment["PIP_CACHE_DIR"] = Path.Combine(XttsRoot, "pip-cache");   // pobierane pakiety w folderze XTTS - widać postęp, po instalacji kasowane
            psi.Environment["TMP"] = psi.Environment["TEMP"] = Path.Combine(XttsRoot, "tmp");
            Directory.CreateDirectory(Path.Combine(XttsRoot, "tmp"));
            long start = DirSize(XttsRoot); var sw = Stopwatch.StartNew(); string last = null;
            using (var tick = new Timer(_ =>
            {
                long mb = Math.Max(0, DirSize(XttsRoot) - start) / 1048576; var t = sw.Elapsed; var ln = last;
                Dispatcher.BeginInvoke(new Action(() => { if (_xtInfo == null) return; _xtInfo.Text = mb + L.T(" MB w tym kroku · czas ") + (int)t.TotalMinutes + ":" + t.Seconds.ToString("00"); _xtLine.Text = ln ?? ""; }));
            }, null, 2000, 2000))
            using (var p = Process.Start(psi))
            {
                var err = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) last = e.Data.Trim(); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) { if (e.Data.Trim().Length > 0) last = e.Data.Trim(); err.AppendLine(e.Data); if (err.Length > 4000) err.Remove(0, 2000); } };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                await Task.Run(() => p.WaitForExit());
                if (p.ExitCode == 0) return true;
                App.LogError(new Exception("XTTS " + what + ": " + err));
                var msg = L.T("❌ XTTS – nie udało się: ") + what + "\n" + err.ToString().Trim().Split('\n').LastOrDefault();
                ShowToast(msg, null); XtStatus(0, msg + "\n" + L.T("Pełny opis: Błędy Velivo. Wybierz głos ponownie – gotowe części się nie pobiorą drugi raz."), true);
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
            XtShow();
            try
            {
                Directory.CreateDirectory(XttsRoot);
                var pyDir = Path.Combine(XttsRoot, "python");
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Velivo");
                    if (!File.Exists(XttsPy))
                    {
                        XtStatus(1, L.T("XTTS: pobieram Pythona…"));
                        var zip = Path.Combine(XttsRoot, "python.zip");
                        await DownloadFile(http, XttsPyZip, zip, (d, t) => Dispatcher.BeginInvoke(new Action(() => { if (_xtInfo != null) _xtInfo.Text = d / 1048576 + " MB"; })));
                        if (Directory.Exists(pyDir)) Directory.Delete(pyDir, true);
                        ZipFile.ExtractToDirectory(zip, pyDir); File.Delete(zip);
                        var pth = Directory.GetFiles(pyDir, "python3*._pth").FirstOrDefault();
                        if (pth != null) File.WriteAllText(pth, File.ReadAllText(pth).Replace("#import site", "import site"));   // pip i pakiety w site-packages
                    }
                    var gp = Path.Combine(XttsRoot, "get-pip.py");
                    if (!File.Exists(Path.Combine(pyDir, "Scripts", "pip.exe")))
                    {
                        XtStatus(2, "pip");
                        await DownloadFile(http, XttsGetPip, gp, null);
                        if (!await RunXttsStep(XttsPy, "\"" + gp + "\" --no-warn-script-location", "pip")) return false;
                    }
                }
                XtStatus(3, L.T("XTTS: instaluję PyTorch (kilka GB, to potrwa)…"));
                string idx = nv ? " --index-url https://download.pytorch.org/whl/cu124" : " --index-url https://download.pytorch.org/whl/cpu";
                if (!await RunXttsStep(XttsPy, "-m pip install --no-warn-script-location torch==2.5.1 torchaudio==2.5.1" + idx, "PyTorch")) return false;
                XtStatus(4, L.T("XTTS: instaluję coqui-tts…"));
                if (!await RunXttsStep(XttsPy, "-m pip install --no-warn-script-location coqui-tts==0.25.3", "coqui-tts")) return false;
                File.WriteAllText(Path.Combine(XttsRoot, "velivo_xtts.py"), XttsScript);
                XtStatus(5, L.T("XTTS: pobieram model (ok. 2 GB)…"));
                if (!await RunXttsStep(XttsPy, "-c \"import os;os.environ['COQUI_TOS_AGREED']='1';from TTS.utils.manage import ModelManager;ModelManager().download_model('tts_models/multilingual/multi-dataset/xtts_v2')\"", L.T("model"))) return false;
                File.WriteAllText(XttsReady, DateTime.Now.ToString("s"));
                foreach (var tmp in new[] { "pip-cache", "tmp" }) try { Directory.Delete(Path.Combine(XttsRoot, tmp), true); } catch (Exception) { }   // kilka GB pobranych paczek - już zainstalowane
                ShowToast(L.T("✅ Głos ") + "XTTS-v2" + L.T(" gotowy – działa bez internetu."), null);
                XtStatus(0, L.T("✅ Głos ") + "XTTS-v2" + L.T(" gotowy – działa bez internetu.") + "\n" + L.T("Wybierz czytanie jeszcze raz – teraz głosem XTTS."), true);
                return true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                ShowToast(L.T("❌ Nie udało się pobrać XTTS: ") + ex.Message, null);
                XtStatus(0, L.T("❌ Nie udało się pobrać XTTS: ") + ex.Message, true);
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
