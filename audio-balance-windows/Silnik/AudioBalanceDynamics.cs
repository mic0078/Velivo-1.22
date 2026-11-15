// ✂ WŁASNA KOPIA silnika dla programu Velivo Audio Balance for Windows (samodzielny program) - zmiany w Velivo (src\AudioBalanceDynamics.cs) jej nie dotykają; skopiowano 2026-11-15
using System;

namespace Velivo
{
    public enum DynDetection { Peak, Rms }

    // ustawienia kompresora wstępnego - obiekt niezmienny; okno podmienia całość jednym przypisaniem (wątek dźwięku czyta referencję raz na blok)
    public sealed class DynParams
    {
        public bool IsEnabled;
        public DynDetection DetectionMode = DynDetection.Rms;
        public float ThresholdDb = -32f, Ratio = 4f, AttackMs = 25f, ReleaseMs = 250f, KneeDb = 10f, MakeupGainDb = 6f, MixPercent = 100f;
        public bool AutoMakeup;
        public float ExpanderThresholdDb = -52f, ExpanderRatio = 1.5f;
        public bool AutoThreshold; public float AutoOffsetDb = 6f;   // próg automatyczny: typowy poziom filmu + AutoOffsetDb (jak „Learn” w Ozone)

        public DynParams Clone() => (DynParams)MemberwiseClone();
        // zakresy ze specyfikacji - wartości spoza nich (np. z ręcznie zmienionego pliku) przycinane
        public DynParams Clamped()
        {
            var p = Clone();
            p.ThresholdDb = C(p.ThresholdDb, -60, 0); p.Ratio = C(p.Ratio, 1, 20); p.AttackMs = C(p.AttackMs, 0.5f, 200); p.ReleaseMs = C(p.ReleaseMs, 10, 2000);
            p.KneeDb = C(p.KneeDb, 0, 12); p.MakeupGainDb = C(p.MakeupGainDb, -12, 24); p.MixPercent = C(p.MixPercent, 0, 100);
            p.ExpanderThresholdDb = C(p.ExpanderThresholdDb, -80, -30); p.ExpanderRatio = C(p.ExpanderRatio, 1, 4); p.AutoOffsetDb = C(p.AutoOffsetDb, 0, 20);
            return p;
        }
        static float C(float v, float lo, float hi) => float.IsNaN(v) ? lo : v < lo ? lo : v > hi ? hi : v;

        // gotowe ustawienia (presety)
        public static DynParams Film() => new DynParams { IsEnabled = true, AutoThreshold = true, AutoOffsetDb = 6, DetectionMode = DynDetection.Rms, ThresholdDb = -28, Ratio = 3.5f, AttackMs = 30, ReleaseMs = 300, KneeDb = 10, MakeupGainDb = 4, MixPercent = 100 };
        // 🔈 bardzo cicho nagrane filmy (dialog ledwo słyszalny): mocne podbicie cichego, ekspander niżej (szum nie rośnie); dalej wyrównanie LUFS dociąga do celu
        public static DynParams VeryQuiet() => new DynParams { IsEnabled = true, DetectionMode = DynDetection.Rms, ThresholdDb = -52, Ratio = 4, AttackMs = 15, ReleaseMs = 300, KneeDb = 12, MakeupGainDb = 22, ExpanderThresholdDb = -68, ExpanderRatio = 1.5f, MixPercent = 100 };
        public static DynParams Night() => new DynParams { IsEnabled = true, DetectionMode = DynDetection.Rms, ThresholdDb = -42, Ratio = 6, AttackMs = 20, ReleaseMs = 250, KneeDb = 10, MakeupGainDb = 12, ExpanderThresholdDb = -50, MixPercent = 100 };
        public static DynParams Parallel() => new DynParams { IsEnabled = true, DetectionMode = DynDetection.Peak, ThresholdDb = -20, Ratio = 4, AttackMs = 10, ReleaseMs = 150, KneeDb = 6, MakeupGainDb = 2, MixPercent = 60 };

        // zapis w ustawieniach Velivo: jedna linia, kropka dziesiętna
        public string Save()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return string.Join(";", IsEnabled ? "1" : "0", DetectionMode == DynDetection.Peak ? "peak" : "rms", ThresholdDb.ToString(ci), Ratio.ToString(ci), AttackMs.ToString(ci), ReleaseMs.ToString(ci),
                KneeDb.ToString(ci), MakeupGainDb.ToString(ci), AutoMakeup ? "1" : "0", MixPercent.ToString(ci), ExpanderThresholdDb.ToString(ci), ExpanderRatio.ToString(ci), AutoThreshold ? "1" : "0", AutoOffsetDb.ToString(ci));
        }
        public static DynParams Load(string s)
        {
            var p = new DynParams(); if (string.IsNullOrWhiteSpace(s)) return p;
            var a = s.Split(';'); if (a.Length < 12) return p;
            var ci = System.Globalization.CultureInfo.InvariantCulture; const System.Globalization.NumberStyles ns = System.Globalization.NumberStyles.Float;
            float F(int i, float d) => float.TryParse(a[i], ns, ci, out var v) ? v : d;
            p.IsEnabled = a[0] == "1"; p.DetectionMode = a[1] == "peak" ? DynDetection.Peak : DynDetection.Rms;
            p.ThresholdDb = F(2, p.ThresholdDb); p.Ratio = F(3, p.Ratio); p.AttackMs = F(4, p.AttackMs); p.ReleaseMs = F(5, p.ReleaseMs); p.KneeDb = F(6, p.KneeDb);
            p.MakeupGainDb = F(7, p.MakeupGainDb); p.AutoMakeup = a[8] == "1"; p.MixPercent = F(9, p.MixPercent); p.ExpanderThresholdDb = F(10, p.ExpanderThresholdDb); p.ExpanderRatio = F(11, p.ExpanderRatio);
            if (a.Length >= 14) { p.AutoThreshold = a[12] == "1"; p.AutoOffsetDb = F(13, p.AutoOffsetDb); }   // starszy zapis (12 pól) - bez progu automatycznego
            return p.Clamped();
        }
    }

    // 🎚 Kompresor wstępny (wzór: Ozone Dynamics) - przed regulatorem LUFS (Audio Balance): spłaszcza różnice szept - wybuch, zanim regulator je zobaczy,
    // więc regulator nie musi „pompować” całości. Detekcja Peak / RMS (stereo razem - ta sama redukcja L i P), miękkie kolano, ekspander szumu w ciszy,
    // kompresja równoległa (Mix) i podbicie (ręczne + auto). Zero alokacji w pętli; parametry podmieniane atomowo (volatile).
    public sealed class VelivoDynamicsProcessor
    {
        readonly double fs;
        volatile DynParams prm = new DynParams();
        public DynParams Params { get => prm; set => prm = (value ?? new DynParams()).Clamped(); }
        public volatile float CurrentGainReductionDb;   // obecna redukcja (dB, ≤ 0; ujemna też przy ekspanderze) - miernik w oknie
        public volatile float InputDb = -120, OutputDb = -120;   // poziom wykrywania na wejściu i wynik po kompresji (dB) - kropka na wykresie i mierniki
        public volatile float EffExpanderDb = -52;   // próg ciszy w tej chwili (dopasowany do filmu)
        public volatile float EffThresholdDb = -32;   // próg w tej chwili (przy progu automatycznym - liczony z filmu)
        // 🎯 próg automatyczny współpracuje z wyrównaniem LUFS (właściciel): typowy poziom filmu jest doprowadzany do celu LUFS - bardzo cichy film w górę,
        // głośny w dół; regulator LUFS za kompresorem dostaje dźwięk już blisko celu i tylko dopracowuje. AutoTargetDb ustawia Audio Balance (cel LUFS).
        public volatile float AutoTargetDb = float.NaN, AutoLiftDb;
        double liftS, progP = double.NaN;   // progP - typowa MOC dźwięku (ta sama bramka co progDb) - do celu LUFS liczy się moc, nie średnia z dB
        int mtrN, progN; double progDb = double.NaN;   // progDb - typowy poziom filmu (dB, ok. 10 s, bez ciszy) dla progu automatycznego
        double env, grDb, avgGr, mkS = double.NaN, mixS = double.NaN;
        DynParams cp; double ca, cr, cRms, cAvg, cs;   // współczynniki liczone raz na zmianę ustawień (nie co próbkę)

        public VelivoDynamicsProcessor(double sampleRate = 48000) { fs = sampleRate; }
        public void Reset() { cp = null; env = 0; grDb = 0; avgGr = 0; mkS = double.NaN; mixS = double.NaN; CurrentGainReductionDb = 0; InputDb = OutputDb = -120; progDb = double.NaN; progP = double.NaN; progN = 0; liftS = 0; AutoLiftDb = 0; }

        // charakterystyka (statyczna): poziom wejścia (dB) → zmiana wzmocnienia (dB)
        public static double GainComputer(double x, DynParams p) => GainComputer(x, p, p.ThresholdDb, p.ExpanderThresholdDb);
        public static double GainComputer(double x, DynParams p, double thr) => GainComputer(x, p, thr, p.ExpanderThresholdDb);
        public static double GainComputer(double x, DynParams p, double thr, double expThr)
        {
            double t = thr, r = Math.Max(1, p.Ratio), w = Math.Max(0, p.KneeDb), d = x - t, g = 0;
            if (w > 0 && Math.Abs(d) <= w / 2) g = (1 / r - 1) * (d + w / 2) * (d + w / 2) / (2 * w);   // miękkie kolano - płynne wejście kompresji
            else if (d > 0) g = (1 / r - 1) * d;
            if (x < expThr && p.ExpanderRatio > 1) g += Math.Max(-40, (x - expThr) * (p.ExpanderRatio - 1));   // ekspander: szum w ciszy łagodnie w dół, nie w górę
            return g;
        }

        // bufor przeplatany (stereo L/P) albo mono, przetwarzany w miejscu
        public void Process(Span<float> buffer, int channels)
        {
            var p = prm; if (!p.IsEnabled || channels < 1) return;
            int frames = buffer.Length / channels;
            for (int f = 0; f < frames; f++)
            {
                int i = f * channels;
                double l = buffer[i], r = channels > 1 ? buffer[i + 1] : l;
                Frame(ref l, ref r, p);
                buffer[i] = (float)l; if (channels > 1) buffer[i + 1] = (float)r;
            }
        }

        // jedna próbka stereo (wołane z pętli Audio Balance: po pomiarze źródła, przed korektorem i regulatorem LUFS)
        public void Frame(ref double l, ref double r, DynParams p)
        {
            if (double.IsNaN(env) || double.IsNaN(grDb) || double.IsInfinity(grDb)) Reset();   // samonaprawa
            if (!ReferenceEquals(p, cp))
            {
                cp = p; ca = Math.Exp(-1000.0 / (Math.Max(0.5, p.AttackMs) * fs)); cr = Math.Exp(-1000.0 / (Math.Max(10, p.ReleaseMs) * fs));
                cRms = 1 - Math.Exp(-1000.0 / (20.0 * fs)); cAvg = 1 - Math.Exp(-1.0 / (3.0 * fs)); cs = 1 - Math.Exp(-1000.0 / (50.0 * fs));
            }
            // detekcja: wspólna dla obu kanałów (panorama zostaje)
            double lvl;
            if (p.DetectionMode == DynDetection.Peak) lvl = Math.Max(Math.Abs(l), Math.Abs(r));
            else { double e = (l * l + r * r) * 0.5; env += (e - env) * cRms; lvl = Math.Sqrt(env); }   // RMS z ok. 20 ms
            double xDb = lvl > 1e-9 ? 20 * Math.Log10(lvl) : -180;
            if ((mtrN & 63) == 0 && xDb > -70)   // mierzone zawsze (nie tylko z zaznaczonym progiem automatycznym) - po zaznaczeniu próg od razu na właściwym miejscu, bez zależności od chwili kliknięcia
            {   // typowy poziom filmu: średnia w dB z ok. 10 s, cisza (-20 dB poniżej) pominięta, głośne liczą się najwyżej +3 dB - wybuch nie podnosi progu
                double e2 = lvl * lvl;
                if (double.IsNaN(progP)) progP = e2;
                else if (progN + 1 < 3 * fs / 64) progP += (e2 - progP) / (0.5 * fs / 64);
                else if (xDb > progDb - 20) progP += (Math.Min(e2, Math.Pow(10, (progDb + 3) / 10)) - progP) / ((progN < 30 * fs / 64 ? 10.0 : 60.0) * fs / 64);
                if (double.IsNaN(progDb)) progDb = xDb;
                else if (++progN < 3 * fs / 64) progDb += (xDb - progDb) / (0.5 * fs / 64);   // pierwsze 3 s filmu: szybko do jego poziomu (bez ograniczeń)
                else if (xDb > progDb - 20) progDb += (Math.Min(xDb, progDb + 3) - progDb) / ((progN < 30 * fs / 64 ? 10.0 : 60.0) * fs / 64);   // po 30 s średnia z ok. 60 s - próg stoi, nie jeździ za uderzeniami i frazami muzyki (falowanie zgłoszone przez właściciela)
            }
            double thr = p.AutoThreshold && !double.IsNaN(progDb) ? Math.Max(-60, Math.Min(0, progDb + p.AutoOffsetDb)) : p.ThresholdDb;
            // próg ciszy zawsze co najmniej 20 dB poniżej typowego poziomu filmu - w cichym filmie ekspander nie bierze dialogów za szum (nagranie właściciela: The Occupant, dialogi -56 dB)
            double expThr = double.IsNaN(progDb) ? p.ExpanderThresholdDb : Math.Min(p.ExpanderThresholdDb, progDb - 20);
            EffThresholdDb = (float)thr; EffExpanderDb = (float)expThr;
            double want = GainComputer(xDb, p, thr, expThr);
            grDb = want < grDb ? want + ca * (grDb - want) : want + cr * (grDb - want);   // mocniej - czas ataku, słabiej - czas powrotu
            if (xDb > expThr) avgGr += (grDb - avgGr) * cAvg;   // średnia redukcja z ok. 3 s (bez ciszy) - do automatycznego podbicia
            // 🎯 do celu LUFS: typowa moc dźwięku (w LUFS ok. +2,3 dB) → cel, z uwzględnieniem tego, co kompresor ściszył; w górę spokojnie (6 dB/s - cichy
            // początek nie wybucha), w dół szybko (20 dB/s). Przy progu automatycznym suwak „Podbicie” nie jest potrzebny - poziom ustala cel LUFS.
            double tgA = AutoTargetDb; bool toTarget = p.AutoThreshold && !double.IsNaN(progP) && progP > 1e-12 && !double.IsNaN(tgA);
            double liftWant = toTarget ? Math.Max(-20, Math.Min(30, tgA - (10 * Math.Log10(progP) + 2.3) - avgGr)) : 0;
            liftS += Math.Max(-20 / fs, Math.Min(6 / fs, liftWant - liftS));
            double mkWant = toTarget ? liftS : p.MakeupGainDb + (p.AutoMakeup ? -avgGr : 0) + liftS, mixWant = p.MixPercent / 100.0;
            // podbicie i Mix zmieniane płynnie (50 ms) - bez trzasków przy przesuwaniu suwaków
            mkS = double.IsNaN(mkS) ? mkWant : mkS + (mkWant - mkS) * cs; mixS = double.IsNaN(mixS) ? mixWant : mixS + (mixWant - mixS) * cs;
            double gw = Math.Pow(10, (grDb + mkS) / 20);
            l = l * (1 - mixS) + l * gw * mixS; r = r * (1 - mixS) + r * gw * mixS;   // równoległa: suchy (bez zmian) + skompresowany
            CurrentGainReductionDb = (float)grDb;
            if ((++mtrN & 63) == 0) { AutoLiftDb = (float)liftS; InputDb = (float)Math.Max(-120, xDb); OutputDb = (float)Math.Max(-120, xDb + 20 * Math.Log10(Math.Max(1e-6, 1 - mixS + mixS * gw))); }   // mierniki co 64 próbki (bez kosztu w każdej próbce)
        }
    }
}
