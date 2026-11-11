using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Velivo
{
    // 🧠 EKSPERYMENT (gałąź lektor-ai): sieć neuronowa Silero VAD v5.1.2 (MIT, 2,3 MB) - czy w filmie właśnie ktoś mówi.
    // Wątek dźwięku tylko wkłada próbki (16 kHz) do bufora; sieć liczy osobny wątek (procesor, 1 rdzeń), co 32 ms.
    // Model pobierany raz do Dane\modele (sprawdzany SHA-256); bez modelu - lektor działa jak dotąd.
    internal static class AbVad
    {
        const string Url = "https://raw.githubusercontent.com/snakers4/silero-vad/v5.1.2/src/silero_vad/data/silero_vad.onnx";
        const string Sha = "2623a2953f6ff3d2c1e61740c6cdb7168133479b267dfef114a4a3cc5bdd788f";
        static readonly float[] ring = new float[16384]; static long wr;   // pisze tylko wątek dźwięku
        static float acc; static int accN;
        static volatile bool ready, started;
        public static volatile float Speech;   // 0–1, wygładzone (szybko w górę 0,2 s, powoli w dół 1 s)
        public static bool Ready => ready;

        // z wątku dźwięku: film mono 48 kHz (średnia z 3 próbek -> 16 kHz)
        public static void Push(double m)
        {
            if (!ready) return;
            acc += (float)m; if (++accN < 3) return;
            ring[wr & 16383] = acc / 3; acc = 0; accN = 0; Volatile.Write(ref wr, wr + 1);
        }

        public static void Start(string dataDir)
        {
            if (started) return; started = true;
            var t = new Thread(() => Run(dataDir)) { IsBackground = true, Name = "Velivo VAD", Priority = ThreadPriority.BelowNormal };
            t.Start();
        }

        static void Run(string dataDir)
        {
            try
            {
                var dir = Path.Combine(dataDir, "modele"); Directory.CreateDirectory(dir);
                var f = Path.Combine(dir, "silero_vad.onnx");
                if (!File.Exists(f) || !ShaOk(f))
                {
                    using (var h = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                    { var b = h.GetByteArrayAsync(Url).GetAwaiter().GetResult(); File.WriteAllBytes(f + ".tmp", b); }
                    if (!ShaOk(f + ".tmp")) { File.Delete(f + ".tmp"); App.LogInfo("Lektor AI: pobrany model rozpoznawania mowy ma zły skrót - pomijam (lektor działa jak dotąd)"); return; }
                    File.Move(f + ".tmp", f, true);
                }
                using var so = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
                using var s = new InferenceSession(f, so);
                var state = new DenseTensor<float>(new[] { 2, 1, 128 });
                var x = new DenseTensor<float>(new[] { 1, 576 });
                var sr = new DenseTensor<long>(new long[] { 16000 }, Array.Empty<int>());
                var ctx = new float[64];
                long rd = 0; ready = true; App.LogInfo("Lektor AI: rozpoznawanie mowy w filmie działa (Silero VAD)");
                double sp = 0;
                while (true)
                {
                    long w = Volatile.Read(ref wr);
                    if (w - rd > 16384 - 512) rd = w - 512;   // zaległości (pauza, przeciążenie) - tylko najnowsze
                    if (w - rd < 512) { Thread.Sleep(10); continue; }
                    for (int i = 0; i < 64; i++) x[0, i] = ctx[i];
                    for (int i = 0; i < 512; i++) x[0, 64 + i] = ring[(rd + i) & 16383];
                    for (int i = 0; i < 64; i++) ctx[i] = ring[(rd + 448 + i) & 16383];
                    rd += 512;
                    using var r = s.Run(new[] { NamedOnnxValue.CreateFromTensor("input", x), NamedOnnxValue.CreateFromTensor("state", state), NamedOnnxValue.CreateFromTensor("sr", sr) });
                    double p = r.First(v => v.Name == "output").AsTensor<float>().GetValue(0);
                    var ns = r.First(v => v.Name == "stateN").AsTensor<float>(); for (int i = 0; i < 256; i++) state.SetValue(i, ns.GetValue(i));
                    sp += (p - sp) * (p > sp ? 0.15 : 0.032);   // 32 ms na krok: w górę ~0,2 s, w dół ~1 s - bez pompowania
                    Speech = (float)sp;
                }
            }
            catch (Exception ex) { ready = false; App.LogError(new Exception("Lektor AI: rozpoznawanie mowy wyłączone - lektor działa jak dotąd", ex)); }
        }

        static bool ShaOk(string f)
        {
            try { using var h = System.Security.Cryptography.SHA256.Create(); using var fs = File.OpenRead(f); return string.Concat(h.ComputeHash(fs).Select(b => b.ToString("x2"))) == Sha; }
            catch (Exception) { return false; }
        }
    }
}
