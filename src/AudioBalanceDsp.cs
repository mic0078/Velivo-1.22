using System.Linq;
using System.Collections.Generic;
using System;

namespace Velivo
{
    // 🎚 Audio Balance - czysty DSP (bez okien, bez VLC): pomiar wg ITU-R BS.1770-4 (K-weighting, LUFS chwilowy 400 ms,
    // krótkookresowy 3 s, zintegrowany z bramkami -70 LUFS / -10 LU, True Peak przez 4x nadpróbkowanie),
    // procesor sterowany pomiarem (powolny wyrównywacz poziomu + kompresor dynamiki z miękkim kolanem)
    // i limiter True Peak z wyprzedzeniem. Stereo 48 kHz float (VLC miksuje 5.1 do stereo przed nami).
    // Pomiar NIE jest normalizacją: wynik pomiaru steruje osobnym procesorem dynamiki.
    public sealed class AbBiquad
    {
        double b0, b1, b2, a1, a2, z1L, z2L, z1R, z2R;
        public void Set(double nb0, double nb1, double nb2, double na0, double na1, double na2) { b0 = nb0 / na0; b1 = nb1 / na0; b2 = nb2 / na0; a1 = na1 / na0; a2 = na2 / na0; }
        public void Reset() { z1L = z2L = z1R = z2R = 0; }
        public double L(double x) { double y = b0 * x + z1L; z1L = b1 * x - a1 * y + z2L; z2L = b2 * x - a2 * y; return y; }
        public double R(double x) { double y = b0 * x + z1R; z1R = b1 * x - a1 * y + z2R; z2R = b2 * x - a2 * y; return y; }
        public static AbBiquad Hp(double fs, double f) { double w = 2 * Math.PI * f / fs, al = Math.Sin(w) / Math.Sqrt(2), c = Math.Cos(w); var b = new AbBiquad(); b.Set((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al); return b; }
        public static AbBiquad Lp(double fs, double f) { double w = 2 * Math.PI * f / fs, al = Math.Sin(w) / Math.Sqrt(2), c = Math.Cos(w); var b = new AbBiquad(); b.Set((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al); return b; }
        // RBJ: filtr szczytowy (korektor)
        public static AbBiquad Peak(double fs, double f, double q, double db)
        {
            double A = Math.Pow(10, db / 40), w = 2 * Math.PI * f / fs, al = Math.Sin(w) / (2 * q), c = Math.Cos(w);
            var b = new AbBiquad(); b.Set(1 + al * A, -2 * c, 1 - al * A, 1 + al / A, -2 * c, 1 - al / A); return b;
        }
    }

    // True Peak: 4x nadpróbkowanie filtrem z ITU-R BS.1770-4 załącznik 2 (48 współczynników, 4 fazy po 12)
    public sealed class AbTruePeak
    {
        static readonly double[][] Ph =
        {
            new[] { 0.0017089843750, 0.0109863281250, -0.0196533203125, 0.0332031250000, -0.0594482421875, 0.1373291015625, 0.9721679687500, -0.1022949218750, 0.0476074218750, -0.0266113281250, 0.0148925781250, -0.0083007812500 },
            new[] { -0.0291748046875, 0.0292968750000, -0.0517578125000, 0.0891113281250, -0.1665039062500, 0.4650878906250, 0.7797851562500, -0.2003173828125, 0.1015625000000, -0.0582275390625, 0.0330810546875, -0.0189208984375 },
            new[] { -0.0189208984375, 0.0330810546875, -0.0582275390625, 0.1015625000000, -0.2003173828125, 0.7797851562500, 0.4650878906250, -0.1665039062500, 0.0891113281250, -0.0517578125000, 0.0292968750000, -0.0291748046875 },
            new[] { -0.0083007812500, 0.0148925781250, -0.0266113281250, 0.0476074218750, -0.1022949218750, 0.9721679687500, 0.1373291015625, -0.0594482421875, 0.0332031250000, -0.0196533203125, 0.0109863281250, 0.0017089843750 },
        };
        public const int Delay = 6;   // opóźnienie filtra w próbkach wejścia (połowa 12)
        readonly double[] hL = new double[12], hR = new double[12]; int pos;
        public void Reset() { Array.Clear(hL, 0, 12); Array.Clear(hR, 0, 12); pos = 0; }
        // największa wartość bezwzględna (próbka i 3 punkty między próbkami) z obu kanałów
        public double Push(double l, double r)
        {
            pos = (pos + 11) % 12; hL[pos] = l; hR[pos] = r;
            double m = 0;
            for (int p = 0; p < 4; p++)
            {
                double sl = 0, sr = 0; var c = Ph[p];
                for (int k = 0; k < 12; k++) { int i = (pos + k) % 12; sl += c[k] * hL[i]; sr += c[k] * hR[i]; }
                sl = Math.Abs(sl); sr = Math.Abs(sr); if (sl > m) m = sl; if (sr > m) m = sr;
            }
            return m;
        }
    }

    public sealed class AbMeter
    {
        public const int Fs = 48000, Block = 4800;   // blok 100 ms
        readonly AbBiquad shelf = new AbBiquad(), hp = new AbBiquad();
        readonly double[] blocks = new double[30]; int nBlocks, bi; double acc; int accN;
        readonly long[] hCnt = new long[1000]; readonly double[] hSum = new double[1000];   // histogram bloków 400 ms (-70..+30 LUFS co 0,1)
        public double Momentary = double.NegativeInfinity, ShortTerm = double.NegativeInfinity, LastPower;
        public double Fast = double.NegativeInfinity;   // ostatni blok 100 ms - do szybkiego ściszania nagłych zgłośnień
        // mediana głośności chwilowej z 10 s (bez ciszy poniżej MedianGate) - poziom „dialogu”, odporny na krótkie wybuchy
        readonly double[] mom = new double[100]; readonly double[] sortBuf = new double[100]; int momN, momI;
        public double MedianGate = -60, Median = double.NegativeInfinity;
        public double Gated10 = double.NegativeInfinity;   // jak „zintegrowana” (bramki -70 / -10 LU), ale z ostatnich 10 s - do dociagania wyniku do celu
        public double Median15 = double.NegativeInfinity;  // mediana głośności chwilowej z 1,5 s (bez ciszy) - tylko wykrywanie zmiany sceny (regulator dalej spokojny)
        public AbMeter()
        {   // współczynniki K-weighting dla 48 kHz wprost z normy (etap 1: półka, etap 2: górnoprzepustowy RLB)
            shelf.Set(1.53512485958697, -2.69169618940638, 1.19839281085285, 1, -1.69065929318241, 0.73248077421585);
            hp.Set(1.0, -2.0, 1.0, 1, -1.99004745483398, 0.99007225036621);
        }
        public static double Lufs(double meanSq) => meanSq <= 1e-20 ? double.NegativeInfinity : -0.691 + 10 * Math.Log10(meanSq);
        public void ResetWindows() { shelf.Reset(); hp.Reset(); Array.Clear(blocks, 0, 30); nBlocks = 0; bi = 0; acc = 0; accN = 0; momN = 0; momI = 0; Momentary = ShortTerm = Median = Fast = Gated10 = Median15 = double.NegativeInfinity; }
        public void ResetIntegrated() { Array.Clear(hCnt, 0, hCnt.Length); Array.Clear(hSum, 0, hSum.Length); }
        // jedna ramka stereo; zwraca chwilową moc K (do detektora kompresora)
        public double Push(double l, double r)
        {
            double kl = hp.L(shelf.L(l)), kr = hp.R(shelf.R(r));
            double p = kl * kl + kr * kr;   // wagi kanałów L/R = 1
            acc += p; LastPower = p;
            if (++accN == Block)
            {
                blocks[bi] = acc / Block; Fast = Lufs(blocks[bi]); bi = (bi + 1) % 30; if (nBlocks < 30) nBlocks++; acc = 0; accN = 0;
                double m = 0, s = 0; int nm = Math.Min(4, nBlocks);
                for (int k = 0; k < nBlocks; k++) { double v = blocks[(bi - 1 - k + 60) % 30]; s += v; if (k < nm) m += v; }
                Momentary = nBlocks >= 4 ? Lufs(m / 4) : double.NegativeInfinity;
                ShortTerm = nBlocks >= 30 ? Lufs(s / 30) : Lufs(s / nBlocks);
                if (nBlocks >= 4)
                {
                    mom[momI] = Momentary; momI = (momI + 1) % 100; if (momN < 100) momN++;
                    int c = 0; for (int k = 0; k < momN; k++) if (mom[k] > MedianGate) sortBuf[c++] = mom[k];
                    if (c >= 20) { Array.Sort(sortBuf, 0, c); Median = sortBuf[c / 2]; }
                    double e1 = 0; int n1 = 0; for (int k = 0; k < momN; k++) if (mom[k] > -70) { e1 += Math.Pow(10, (mom[k] + 0.691) / 10); n1++; }
                    if (n1 >= 10)
                    {
                        double gate = Lufs(e1 / n1) - 10, e2 = 0; int n2 = 0;
                        for (int k = 0; k < momN; k++) if (mom[k] > gate) { e2 += Math.Pow(10, (mom[k] + 0.691) / 10); n2++; }
                        Gated10 = n2 > 0 ? Lufs(e2 / n2) : double.NegativeInfinity;
                    }
                    { int c2 = 0; for (int k = 0; k < Math.Min(momN, 15); k++) { double v = mom[(momI - 1 - k + 200) % 100]; if (v > MedianGate) sortBuf[c2++] = v; }
                      if (c2 >= 8) { Array.Sort(sortBuf, 0, c2); Median15 = sortBuf[c2 / 2]; } else Median15 = double.NegativeInfinity; }   // pauza dłuższa niż pół okna - bez wyniku (cisza nie przełącza sceny)
                }
                if (nBlocks >= 4 && Momentary > -70) { int b = (int)Math.Round((Math.Min(Momentary, 29.9) + 70) * 10); if (b >= 0 && b < 1000) { hCnt[b]++; hSum[b] += m / 4; } }   // bloki 400 ms co 100 ms (75% zakładki)
            }
            return p;
        }
        // zintegrowana głośność z bramką bezwzględną -70 LUFS i względną -10 LU
        public double Integrated
        {
            get
            {
                long n = 0; double s = 0;
                for (int b = 0; b < 1000; b++) { n += hCnt[b]; s += hSum[b]; }
                if (n == 0) return double.NegativeInfinity;
                double gate = Lufs(s / n) - 10; n = 0; s = 0;
                for (int b = 0; b < 1000; b++) if (b / 10.0 - 70 >= gate - 0.05) { n += hCnt[b]; s += hSum[b]; }
                return n == 0 ? double.NegativeInfinity : Lufs(s / n);
            }
        }
    }

    // Inteligentna normalizacja (specyfikacja właściciela 2026-11-09, wzór AudioBalance): tryb Auto Level / Limit Only × intensywność Light / Normal / Strong.
    // Amount - jaką część odchylenia od celu korygujemy (Light zachowuje więcej naturalnej dynamiki); LimitOnly - nigdy nie podgłaśnia.
    // 🧠 pamięć długoterminowa źródła (nauka Audio Balance): seans = mediana głośności (krótkoterminowej, co sekundę) z całego oglądania,
    // dopisywana do historii z wagą = długość seansu (najwyżej 30 min - jeden długi film nie kasuje historii); seans nietypowy
    // (ponad 8 dB od dobrze poznanej średniej) liczy się 4× słabiej; historia ograniczona: kanał TV 60 min (nadąża za zmianą programów), film 240 min
    public static class AbLearnMath
    {
        public const double MinSessionMin = 0.5, MaxSessionW = 30, CapTv = 60, CapFilm = 240;
        public static double Median(IList<float> v) { if (v.Count == 0) return double.NaN; var o = v.OrderBy(x => x).ToList(); return o.Count % 2 == 1 ? o[o.Count / 2] : (o[o.Count / 2 - 1] + o[o.Count / 2]) / 2.0; }
        // zwraca nową średnią i wagę historii; seans za krótki albo bez pomiaru - bez zmian
        public static (double avg, double w) Merge(double avg, double w, double sessMedian, double sessMin, bool tv)
        {
            if (double.IsNaN(sessMedian) || sessMin < MinSessionMin) return (avg, w);
            if (w <= 0 || double.IsNaN(avg)) return (sessMedian, Math.Min(Math.Min(sessMin, MaxSessionW), tv ? CapTv : CapFilm));
            double sw = Math.Min(sessMin, MaxSessionW);
            if (w >= 10 && Math.Abs(sessMedian - avg) > 8) sw *= 0.25;
            double na = (avg * w + sessMedian * sw) / (w + sw);
            return (na, Math.Min(w + sw, tv ? CapTv : CapFilm));
        }

        // głośność: bazowa (Twój zwykły poziom, wspólny) ≠ korekta źródła (Adj, dB względem bazowej) ≠ reklamy (FastMul) - osobne wielkości
        public const double MaxAdj = 6, CapBase = 600;
        public static double VolDb(double vol, double baseVol) => vol > 0 && baseVol > 0 ? 20 * Math.Log10(vol / baseVol) : 0;
        // przy starcie źródła stosujemy tylko tę część zapamiętanej korekty, której Twój obecny suwak jeszcze nie daje - bez podwójnego naliczania
        public static double EffAdj(double adj, double curVolDb) => Math.Max(-MaxAdj, Math.Min(MaxAdj, adj - curVolDb));
        // upodobanie w seansie = korekta automatyczna, która była zastosowana + Twoja głośność względem bazowej (średnia z seansu)
        public static double SessionPref(double effApplied, double sessVolDb) => Math.Max(-MaxAdj, Math.Min(MaxAdj, effApplied + sessVolDb));
        public static (double v, double w) MergeW(double v, double w, double x, double sessMin, double cap)
        {
            if (sessMin < MinSessionMin || double.IsNaN(x)) return (v, w);
            double sw = Math.Min(sessMin, MaxSessionW);
            if (w <= 0) return (x, Math.Min(sw, cap));
            return ((v * w + x * sw) / (w + sw), Math.Min(w + sw, cap));
        }
    }

    // 🗣 Doradca lektora (AI bez sieci neuronowych - pomiary i statystyka): ocenia słyszalność lektora względem filmu w paśmie
    // zrozumiałości mowy (1–4 kHz), z uwzględnieniem ściszenia filmu pod lektora i dialogów w filmie; NICZEGO nie zmienia - tylko proponuje
    public sealed class AbLektorAdvisor
    {
        public const double Good = 6, Partial = 0;   // przewaga lektora w paśmie mowy: ≥6 dB dobrze, 0–6 częściowo, <0 zagłuszany
        readonly List<double> last = new List<double>();
        public double Margin = double.NaN; public int Status = -1;   // -1 brak danych, 0 dobrze, 1 częściowo, 2 zagłuszany
        // jedna kwestia: lektor (dBFS całość i pasmo 1–4 kHz, głośność odtwarzacza lektora 0–1), film (pasmo 1–4 kHz po wyrównaniu, ściszenie 0–1, udział dialogów)
        public static double CueMargin(double lekBandDb, double lekVol, double filmBandDb, double duck, double dialogShare)
        {
            double m = lekBandDb + 20 * Math.Log10(Math.Max(1e-3, lekVol)) - (filmBandDb + 20 * Math.Log10(Math.Max(1e-3, duck)));
            if (dialogShare > 0.5) m -= 2;   // w filmie trwa dialog - druga mowa w tym samym paśmie przeszkadza bardziej niż muzyka
            return m;
        }
        public void Add(double margin)
        {
            if (double.IsNaN(margin) || double.IsInfinity(margin)) return;
            last.Add(margin); if (last.Count > 10) last.RemoveAt(0);
            var o = last.OrderBy(x => x).ToList(); Margin = o[o.Count / 2];   // mediana 10 ostatnich kwestii - jedna głośna scena nie zmienia oceny
            Status = Margin >= Good ? 0 : Margin >= Partial ? 1 : 2;
        }
        public void Reset() { last.Clear(); Margin = double.NaN; Status = -1; }
        public int Count => last.Count;
        // propozycja: o ile podnieść „Lektor nad filmem” (0 = nie trzeba); tylko gdy 8 z 10 ostatnich kwestii nie było dobrze słyszalnych
        // propozycja tylko, gdy lektor naprawdę ginie: 8 z 10 ostatnich kwestii poniżej 0 dB (🔴); „częściowo” (🟡) - sama ocena; krok co najmniej 3 dB
        public int SuggestOverUp() => last.Count >= 10 && last.Count(x => x < Partial) >= 8 ? (int)Math.Max(3, Math.Min(10, Math.Ceiling(Good - Margin))) : 0;
    }

    // wpis pamięci nauki jednego źródła (audio-nauka.json); stare pole Lev ze starszych wersji jest przy wczytaniu pomijane
    public sealed class AbLearnEntry
    {
        public double Ad { get; set; } public double Adj { get; set; } public double AdjW { get; set; } public double Avg { get; set; } public double W { get; set; } public int N { get; set; } public DateTime T { get; set; }
        // przed zapisem: ∞ / NaN (np. pomiar ciszy) zastąpione bezpieczną wartością - wpis bez sensownej średniej traci wagę (uczy się od nowa)
        public void Sanitize()
        {
            static double F(double x, double d) => double.IsNaN(x) || double.IsInfinity(x) ? d : x;
            Ad = Math.Max(-60, Math.Min(0, F(Ad, 0))); Adj = Math.Max(-AbLearnMath.MaxAdj, Math.Min(AbLearnMath.MaxAdj, F(Adj, 0))); AdjW = F(AdjW, 0);
            if (double.IsNaN(Avg) || double.IsInfinity(Avg) || Avg < -70 || Avg > 0) { Avg = 0; W = 0; } W = F(W, 0);
        }
        public static readonly System.Text.Json.JsonSerializerOptions Json = new System.Text.Json.JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    }

    public sealed class AbProfile
    {
        public string Name; public double MaxBoost, MaxCut, UpDbS, DownDbS, Gate, ThrOff, Ratio, AttackMs, ReleaseMs, Amount = 1, Dead = 1.5, FastHead = 6; public bool LimitOnly;   // Dead - strefa komfortu ±dB wokół punktu odniesienia: tam bez zmian
        public double FastDownDbS;   // nagły skok w górę (eksplozja, reklama) - ściszanie prawie natychmiast (dB/s); podgłaśnianie zostaje płynne
        static AbProfile Mk(bool lim, string sila)
        {
            int k = sila == "light" ? 0 : sila == "strong" ? 2 : 1;
            var p = new AbProfile { LimitOnly = lim, Name = (lim ? "limit" : "auto") + "-" + new[] { "light", "normal", "strong" }[k],
                Amount = new[] { 0.6, 0.85, 1.0 }[k], Dead = new[] { 4.0, 3, 2 }[k], FastHead = new[] { 12.0, 10, 8 }[k], MaxBoost = lim ? 0 : new[] { 10.0, 15, 20 }[k], MaxCut = new[] { 10.0, 15, 20 }[k],
                UpDbS = new[] { 0.7, 1.0, 1.5 }[k], DownDbS = new[] { 1.0, 1.5, 2.5 }[k], Gate = new[] { -25.0, -30, -35 }[k],
                ThrOff = lim ? new[] { 8.0, 6, 4 }[k] : new[] { 8.0, 6, 5 }[k], Ratio = new[] { 1.5, 2, 2.5 }[k],
                AttackMs = new[] { 25.0, 15, 8 }[k], ReleaseMs = new[] { 400.0, 300, 200 }[k], FastDownDbS = new[] { 30.0, 40, 60 }[k] };
            return p;
        }
        public static readonly AbProfile[] All = { Mk(false, "light"), Mk(false, "normal"), Mk(false, "strong"), Mk(true, "light"), Mk(true, "normal"), Mk(true, "strong") };
        public static AbProfile Light => All[0]; public static AbProfile Balanced => All[1]; public static AbProfile Night => All[2]; public static AbProfile LimiterOnly => All[4];
        // tryb: "auto" / "limit" / "off"; intensywność: "light" / "normal" / "strong" (stare nazwy light/balanced/night/limiter - też)
        public static AbProfile ByName(string mode, string sila = "normal")
        {
            if (mode == "light") return All[0]; if (mode == "balanced") return All[1]; if (mode == "night") return All[2]; if (mode == "limiter") return All[4];
            if (mode != "auto" && mode != "limit") return null;
            int k = sila == "light" ? 0 : sila == "strong" ? 2 : 1;
            return All[(mode == "limit" ? 3 : 0) + k];
        }
    }

    public sealed class AbProcessor
    {
        public const int Fs = AbMeter.Fs;
        public static readonly double[] EqFreq = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        public volatile AbProfile Profile;      // null = bez wyrównywania (sam korektor / przejście)
        public double EffTarget = -18;          // cel w tej chwili (Auto: średnia źródła) - do wyświetlania
        public volatile float Target = -18;     // poziom docelowy LUFS - wybór użytkownika
        public volatile float CeilingDb = -1;   // limiter True Peak (dBTP)
        public volatile float Volume = 1;       // głośność odtwarzacza (VLC) - liniowo
        public volatile bool EqOn;
        public volatile bool Bypass;          // ⏻ jeden przycisk: całe przetwarzanie wyłączone (ustawienia zostają)
        public volatile int Dialog;           // 🗣 wyraźne dialogi: 0 wył., 1 średnio (do +4 dB), 2 mocno (do +8 dB)
        public double DialogDb;               // bieżące podbicie mowy (widżet)
        // dialogi: mowa jest zwykle w środku stereo (L+R) w paśmie 250–4000 Hz; gdy przykrywa ją reszta (muzyka, efekty, szerokie stereo),
        // pasmo mowy ze środka jest równolegle podbijane - reszta dźwięku i jej dynamika zostają bez zmian
        readonly AbBiquad dHp = AbBiquad.Hp(48000, 250), dLp = AbBiquad.Lp(48000, 4000); double dS, dM, dSide, dG;
        // 🗣 doradca lektora: pasmo zrozumiałości mowy 1–4 kHz wyniku filmu (przed głośnością odtwarzacza) i aktywność dialogów w oryginale
        readonly AbBiquad aHp = AbBiquad.Hp(48000, 1000), aLp = AbBiquad.Lp(48000, 4000), sHp = AbBiquad.Hp(48000, 250), sLp = AbBiquad.Lp(48000, 4000);
        double aP, sS, sA;
        public double OutSpeechBandDb => 10 * Math.Log10(aP + 1e-12);        // film w paśmie 1–4 kHz (dBFS, średnio ok. 3 s)
        public double InDialogShare => sA < 1e-9 ? 0 : Math.Min(1, sS / sA); // udział pasma mowy w oryginale (0–1) - dialogi w filmie
        readonly AbBiquad[] eq = new AbBiquad[10]; readonly double[] eqDb = new double[10]; volatile bool eqDirty = true;
        // próbki wyjścia (mono) dla widżetu - widmo (FFT) liczy wątek okna widżetu, nie wątek dźwięku
        public readonly float[] Scope = new float[65536], ScopeOut = new float[65536]; public volatile int ScopePos; int pkN;   // 1,4 s surowego wejścia (widmo) - wskaźniki czytają fragment, który właśnie słychać (za kolejką karty)
        // migawki wskaźników co kawałek dźwięku (numer próbki) - wyświetlanie zsynchronizowane z tym, co słychać
        public struct Snap { public long F; public double VuL, VuR, VuInL, VuInR, Fast, RawM, OutM; }
        readonly Snap[] snaps = new Snap[512]; int snapI;
        public Snap Delayed(long behind) { long target = Frames - behind; int n = snapI; for (int k = n - 1; k >= 0 && k >= n - 512; k--) { var x = snaps[k & 511]; if (x.F <= target) return x; } return n > 0 ? snaps[Math.Max(0, n - 512) & 511] : default; }
        void SnapAdd() { snaps[snapI & 511] = new Snap { F = Frames, VuL = VuL, VuR = VuR, VuInL = VuInL, VuInR = VuInR, Fast = Out.Fast, RawM = Raw.Momentary, OutM = Out.Momentary }; snapI++; }
        public volatile bool Active; public long Frames;
        // wskazniki VU lewego i prawego kanalu wyjscia: moc RMS wygladzona ~80 ms, w dBFS
        public double VuL = double.NegativeInfinity, VuR = double.NegativeInfinity; double vuPL, vuPR; int vuN;
        public double VuInL = double.NegativeInfinity, VuInR = double.NegativeInfinity; double viPL, viPR; int viN;   // to samo dla oryginalu (przed przetwarzaniem)
        void VuIn(double l, double r)
        {
            const double c = 0.99974;
            viPL = l * l + c * (viPL - l * l); viPR = r * r + c * (viPR - r * r);
            if (++viN >= 480) { viN = 0; VuInL = 10 * Math.Log10(viPL * 2 + 1e-12); VuInR = 10 * Math.Log10(viPR * 2 + 1e-12); }
        }
        void Vu(double l, double r)
        {
            const double c = 0.99974;   // ok. 80 ms przy 48 kHz - szybka reakcja wskazowki (wlasciciel: 300 ms za wolno)
            vuPL = l * l + c * (vuPL - l * l); vuPR = r * r + c * (vuPR - r * r);
            if (++vuN >= 480) { vuN = 0; VuL = 10 * Math.Log10(vuPL * 2 + 1e-12); VuR = 10 * Math.Log10(vuPR * 2 + 1e-12); }   // *2: sinus 0 dBFS = 0 dB
        }   // ostatnio przetworzony dźwięk (do wskaźnika „pracuje”)
        public readonly AbMeter In = new AbMeter(), Out = new AbMeter();
        public readonly AbMeter Raw = new AbMeter();   // prawdziwe źródło (przed korektorem i dialogami) - do wyświetlania „ŹRÓDŁO” i ORIGINAL
        readonly AbTruePeak tpIn = new AbTruePeak(), tpOut = new AbTruePeak();
        double levDb, detP, grDb, fastDb, wantS, vS = 1; long sinceStart; int sceneN;   // vS - głośność odtwarzacza wygładzona (ok. 80 ms): ściszanie pod lektora i powrót bez skoku
        // limiter z wyprzedzeniem: opóźnienie sygnału, minimum w oknie i wygładzenie średnią z okna tej samej długości
        const int La = 240, Dl = La + AbTruePeak.Delay;
        public const int LatencyFrames = Dl;   // opóźnienie przetwarzania (ok. 5 ms) - uwzględniane przy zgrywaniu z obrazem   // okno 5 ms; sygnał opóźniony dodatkowo o opóźnienie filtra True Peak
        readonly float[] dL = new float[Dl], dR = new float[Dl]; readonly double[] mins = new double[La]; int mPos;
        int dPos; double minRunSum, limG = 1; readonly int[] dq = new int[La + 1]; readonly double[] dqV = new double[La + 1]; int dqH, dqT; long n;
        public double GainDb, LimitDb, PeakOutDb = double.NegativeInfinity; double peakHold; int peakN;
        public AbProcessor() { Reset(); }
        public void SetEq(double[] db) { lock (eqDb) Array.Copy(db, eqDb, 10); eqDirty = true; }
        public void Reset()
        {
            In.ResetWindows(); Out.ResetWindows(); Raw.ResetWindows(); tpIn.Reset(); tpOut.Reset();
            Array.Clear(dL, 0, Dl); Array.Clear(dR, 0, Dl); for (int i = 0; i < La; i++) mins[i] = 1;
            minRunSum = La; limG = 1; dPos = 0; mPos = 0; dqH = dqT = 0; n = 0; detP = 0; grDb = 0; fastDb = 0; dHp.Reset(); dLp.Reset(); dS = dM = dSide = dG = 0; aHp.Reset(); aLp.Reset(); sHp.Reset(); sLp.Reset(); aP = sS = sA = 0;
            foreach (var b in eq) b?.Reset();
        }
        volatile bool newReq;
        // 🧠 nauka (AbLearn): poziom startowy, mnoznik szybkiego sciszania i przesuniecie celu dla tego zrodla - ustawiane przed NewMedia
        public volatile float SeedLevDb = float.NaN, FastMul = 1, TargetAdj = 0;
        public double FastMinDb;                    // najglebsze chwilowe sciszenie od ostatniego odczytu (reklamy)
        public void NewMedia() => newReq = true;   // z wątku okna - wykonane w wątku dźwięku przed kolejnym kawałkiem

        // przetwarza ramki stereo float w miejscu (bufor VLC → wyjście)
        public void Process(float[] buf, int frames)
        {
            if (newReq) { newReq = false; Reset(); In.ResetIntegrated(); Out.ResetIntegrated(); Raw.ResetIntegrated(); levDb = float.IsNaN(SeedLevDb) ? 0 : Math.Max(-Profile.MaxCut, Math.Min(Profile.MaxBoost, SeedLevDb)); wantS = levDb; sinceStart = 0; }
            if (double.IsNaN(levDb) || double.IsNaN(grDb) || double.IsNaN(limG) || double.IsNaN(detP)) { Reset(); levDb = 0; }   // samonaprawa
            if (eqDirty) { lock (eqDb) for (int i = 0; i < 10; i++) eq[i] = AbBiquad.Peak(Fs, EqFreq[i], 1.41, eqDb[i]); eqDirty = false; }
            double Tg = Target;
            if (Tg >= -0.5) Tg = -23;   // Auto = -23 LUFS (EBU R128): stały punkt odniesienia - ciche w górę, głośne w dół, bez stałego podbijania całości
            Tg += TargetAdj; EffTarget = Tg; var pr = Profile;
            // tło: fragment ponad 22 dB cichszy niż średnia tego filmu (szum, ambient, pauza) nie jest podbijany - regulator czeka (raz na bufor)
            double bgGate = pr == null ? -99 : Tg + pr.Gate, film = In.Integrated; if (pr != null && film > -70) bgGate = Math.Max(bgGate, film - 22); bool eqOn = EqOn; double ceil = Math.Pow(10, CeilingDb / 20), vol = Volume;
            if (double.IsNaN(vol) || vol < 0) vol = 1; else if (vol > 2) vol = 2;
            double kV = 1.0 / (0.08 * Fs);
            double upS = pr == null ? 0 : pr.UpDbS / Fs, dnS = pr == null ? 0 : pr.DownDbS / Fs;
            double aC = pr == null ? 0 : Math.Exp(-1000.0 / (pr.AttackMs * Fs)), rC = pr == null ? 0 : Math.Exp(-1000.0 / (pr.ReleaseMs * Fs)), detC = Math.Exp(-1000.0 / (10.0 * Fs));
            double relLim = Math.Exp(-1000.0 / (80.0 * Fs));
            int dlg = Dialog; double dMax = dlg == 2 ? 8 : dlg == 1 ? 4 : 0;
            double eC = Math.Exp(-1000.0 / (50.0 * Fs)), dAtt = Math.Exp(-1000.0 / (80.0 * Fs)), dRel = Math.Exp(-1000.0 / (400.0 * Fs));
            if (!Bypass && Vst3.Active)
            {   // 🎛 łańcuch VST3 przejmuje przetwarzanie: oryginał → wtyczki → głośność odtwarzacza (bez korektora, dialogów i wyrównywania Velivo - bez podwójnego przetwarzania)
                for (int f = 0; f < frames; f++)
                {
                    double il = buf[2 * f], ir = buf[2 * f + 1];
                    Scope[(ScopePos + f) & 65535] = (float)((il + ir) * 0.5);   // widmo: oryginał (pozycję przesuwa druga pętla - razem z wyjściem)
                    double vv = vS + (vol - vS) * kV; VuIn(il * vv, ir * vv); Raw.Push(il * vv, ir * vv); In.Push(il * vv, ir * vv);
                }
                if (Vst3.Run(buf, frames))
                {
                    for (int f = 0; f < frames; f++)
                    {
                        vS += (vol - vS) * kV;
                        double l = buf[2 * f] * vS, r = buf[2 * f + 1] * vS;
                        if (l > 1) l = 1; else if (l < -1) l = -1; if (r > 1) r = 1; else if (r < -1) r = -1;   // tylko bezpiecznik zakresu (o brzmieniu decydują Twoje wtyczki)
                        buf[2 * f] = (float)l; buf[2 * f + 1] = (float)r;
                        { int sp = ScopePos; ScopeOut[sp] = vS > 1e-3 ? (float)((l + r) * 0.5 / vS) : 0f; ScopePos = (sp + 1) & 65535; }
                        Out.Push(l, r); Vu(l, r);
                        double tpo = tpOut.Push(l, r); if (tpo > peakHold) peakHold = tpo;
                        if (++pkN == 2400) { pkN = 0; if (++peakN >= 20) { PeakOutDb = 20 * Math.Log10(peakHold + 1e-12); peakHold = 0; peakN = 0; } }
                    }
                    GainDb = 0; LimitDb = 0; Frames += frames; limG = 1; SnapAdd(); return;
                }
                In.ResetWindows(); Raw.ResetWindows();   // wszystkie wtyczki wyłączone - zwykła droga (pomiar od nowa, bez podwójnego liczenia tego kawałka)
            }
            if (Bypass || (pr == null && !eqOn && dlg == 0))
            {   // wyłączone w trakcie odtwarzania: dźwięk bez zmian (tylko głośność odtwarzacza), pomiar i widmo dalej działają
                for (int f = 0; f < frames; f++)
                {
                    { int sp = ScopePos; Scope[sp] = ScopeOut[sp] = (float)((buf[2 * f] + buf[2 * f + 1]) * 0.5); ScopePos = (sp + 1) & 65535; }   // widmo: surowy dźwięk filmu (bez przetwarzania wyjście = wejście)
                    vS += (vol - vS) * kV; VuIn(buf[2 * f] * vS, buf[2 * f + 1] * vS); Raw.Push(buf[2 * f] * vS, buf[2 * f + 1] * vS);
                    double l = buf[2 * f] * vS, r = buf[2 * f + 1] * vS;
                    buf[2 * f] = (float)l; buf[2 * f + 1] = (float)r;
                    In.Push(l, r); Out.Push(l, r); Vu(l, r);
                    double tpo = tpOut.Push(l, r); if (tpo > peakHold) peakHold = tpo;
                    if (++pkN == 2400) { pkN = 0; if (++peakN >= 20) { PeakOutDb = 20 * Math.Log10(peakHold + 1e-12); peakHold = 0; peakN = 0; } }
                }
                GainDb = 0; LimitDb = 0; Frames += frames; limG = 1; SnapAdd(); return;
            }
            for (int f = 0; f < frames; f++)
            {
                double l = buf[2 * f], r = buf[2 * f + 1];
                { int sp = ScopePos; Scope[sp] = (float)((l + r) * 0.5); ScopePos = (sp + 1) & 65535; }   // widmo: surowy dźwięk filmu (przed korektorem, wyrównaniem i głośnością) - pracę Velivo pokazują V i paski
                vS += (vol - vS) * kV; VuIn(l * vS, r * vS); Raw.Push(l * vS, r * vS);   // oryginal przy tej samej glosnosci odtwarzacza - porownanie uczciwe
                { double dm0 = (l + r) * 0.5, sb = sLp.L(sHp.L(dm0)); sS += (sb * sb - sS) / Fs; sA += (dm0 * dm0 - sA) / Fs; }   // dialogi w oryginale (1 s)
                if (eqOn) for (int i = 0; i < 10; i++) { l = eq[i].L(l); r = eq[i].R(r); }
                if (dlg > 0)
                {
                    double m = (l + r) * 0.5, sd = (l - r) * 0.5, spc = dLp.L(dHp.L(m));
                    dS = spc * spc + eC * (dS - spc * spc); dM = m * m + eC * (dM - m * m); dSide = sd * sd + eC * (dSide - sd * sd);
                    double all = dM + dSide, share = dS / (all + 1e-15);   // udział mowy (środek, pasmo mowy) w całym dźwięku
                    double want = all < 1e-7 || share < 0.25 ? 0 : dMax * Math.Max(0, Math.Min(1, (0.8 - share) / 0.45));   // brak mowy / mowa wyraźna = 0, mowa przykryta = więcej
                    dG = want > dG ? want + dAtt * (dG - want) : want + dRel * (dG - want);
                    double add = Math.Pow(10, dG / 20) - 1; l += add * spc; r += add * spc; DialogDb = dG;
                }
                else DialogDb = 0;
                double p = In.Push(l, r);
                double g = 0;
                if (pr != null)
                {
                    // 1) wyrównywanie: pomiar krótkoterminowy (3 s, BS.1770) - bez pompowania między słowami; korekta = Amount × odchylenie od celu
                    //    (Light zostawia połowę naturalnej dynamiki), płynnie (wygładzenie 1 s + limit dB/s); Limit Only - tylko w dół; w ciszy wstrzymany
                    In.MedianGate = Tg + pr.Gate;
                    // podstawa: 3 s, ale nie wyżej niż mediana 10 s + 3 dB - krótki huk (eksplozja) nie ciągnie regulatora w dół (robi to szybka warstwa),
                    //    a cichnięcie widać od razu (3 s) - po wybuchu poziom dialogu wraca bez czekania na okno pomiarowe
                    // podstawa: głośność samej treści z 10 s (bramka -10 LU jak w BS.1770 - przerwy między zdaniami pomijane), na starcie 3 s;
                    //    krótki huk nie ciągnie (mediana + 3 dB) - bez falowania między zdaniami (było: okno 3 s z przerwami → „roller coaster”)
                    double basis = In.Gated10 > double.NegativeInfinity && sinceStart > Fs * 6 ? In.Gated10 : In.ShortTerm > double.NegativeInfinity ? In.ShortTerm : In.Momentary;
                    if (In.Median > double.NegativeInfinity) basis = Math.Min(basis, In.Median + 3);
                    // zmiana sceny: mediana z 1,5 s różni się od podstawy o ponad 5 dB dłużej niż 1 s - nowy poziom w ok. 1–2 s zamiast gonienia przez 15 s
                    //    (w obrębie sceny bez zmian: spokojna średnia 10 s - bez pompowania między zdaniami; pauzy i cisza nie przełączają - pomiar pomija ciszę)
                    bool scene = false;
                    double m15 = In.Median15 > double.NegativeInfinity && In.Median > double.NegativeInfinity ? Math.Min(In.Median15, In.Median + 3) : double.NegativeInfinity;   // głośniej - ta sama osłona co podstawa: krótki huk nie jest „sceną”
                    if (sinceStart > Fs * 6 && m15 > double.NegativeInfinity && Math.Abs(m15 - basis) > 5) { if (++sceneN > 2 * Fs) scene = true; } else sceneN = 0;
                    if (scene) basis = m15;
                    if (basis > double.NegativeInfinity && In.Momentary > bgGate)
                    {
                        double dev = Tg - basis; dev = Math.Sign(dev) * Math.Max(0, Math.Abs(dev) - pr.Dead);   // strefa komfortu wokół punktu odniesienia = głośność prawidłowa, bez zmian
                        double want = Math.Max(-pr.MaxCut, Math.Min(pr.MaxBoost, pr.Amount * dev));
                        if (pr.LimitOnly) want = Math.Min(0, want);
                        wantS += (want - wantS) * (1.0 / ((scene ? 0.5 : 2.0) * Fs));
                        double up = sinceStart < Fs * 6 || scene ? upS * 6 : upS, dn = sinceStart < Fs * 6 || scene ? dnS * 6 : dnS;   // start źródła i zmiana sceny - szybko do poziomu, potem spokojnie   // pierwsze 6 s nowego źródła - szybko do poziomu (nowy kanał to nie pompowanie), potem spokojnie
                        levDb += Math.Max(-dn, Math.Min(up, wantS - levDb));
                    }
                    // nagłe zgłośnienie (reklama, głośniejsza scena): gdy głośność z 400 ms po obecnym wzmocnieniu przekracza cel o ponad 2 dB,
                    // ściszamy szybko (30–60 dB/s, czyli kilka dB w ok. 0,1 s) - bez czekania na powolne wyrównanie z 10 s
                    // osobne, chwilowe sciszenie (fastDb): szybko w dol, a gdy zgloszenie minie - wraca w ok. 1–2 s; powolny poziom (levDb)
                    // zostaje nietkniety, wiec po wybuchu dialog od razu jest znowu glosny (nie czeka na powolne podglosnianie)
                    double fastIn = Math.Max(In.Fast, In.Momentary);   // 100 ms - reakcja w ok. 0,1 s
                    double fastWant = pr.FastDownDbS > 0 && fastIn > double.NegativeInfinity ? Math.Min(0, Math.Max(-pr.MaxCut, Tg + pr.FastHead - (fastIn + levDb)))   /* zapas ponad poziom dialogu - huk zostaje hukiem, tylko bez skoku */ : 0;
                    fastDb = fastWant < fastDb ? Math.Max(fastWant, fastDb - pr.FastDownDbS * FastMul / Fs) : Math.Min(fastWant, fastDb + pr.FastDownDbS / 5 / Fs);
                    if (fastDb < FastMinDb) FastMinDb = fastDb;
                    // 2) kompresor: szybki detektor mocy K (10 ms) po wyrównaniu, miękkie kolano 6 dB
                    double pl = p * Math.Pow(10, (levDb + fastDb) / 10);   // kompresor widzi już ściszony huk - warstwy nie ściszają tego samego dwa razy
                    detP = pl + detC * (detP - pl);
                    double lev = AbMeter.Lufs(detP), thr = Tg + pr.ThrOff, over = lev - thr, want2 = 0;
                    if (over > 3) want2 = -over * (1 - 1 / pr.Ratio);
                    else if (over > -3) want2 = -(1 - 1 / pr.Ratio) * (over + 3) * (over + 3) / 12;
                    if (over > 8) want2 -= (over - 8) * (1 / pr.Ratio - 0.05);   // powyżej progu + 8 dB prawie twarda granica (wybuchy nie przeskakują poziomu)
                    grDb = want2 < grDb ? want2 + aC * (grDb - want2) : want2 + rC * (grDb - want2);
                    // (korekta wyniku „corrDb” usunięta 2026-11-09: domykała pętlę na wyjściu i między zdaniami pchała głośność w górę i w dół - falowanie ±3 dB)
                    g = levDb + fastDb + grDb;
                    if (pr.LimitOnly && g > 0) g = 0;   // Limit Only: bezwzględnie żadnego podgłośnienia - niezależnie od nauki, korekty, startu źródła
                }
                double lin = Math.Pow(10, g / 20) * vS;
                // 3) limiter True Peak: wymagane wzmocnienie z punktów między próbkami
                double tp = tpIn.Push(l * lin, r * lin), rq = tp > ceil ? ceil / tp : 1;
                // minimum w oknie La (kolejka monotoniczna)
                while (dqT > dqH && dqV[(dqT - 1) % (La + 1)] >= rq) dqT--;
                dq[dqT % (La + 1)] = (int)(n % int.MaxValue); dqV[dqT % (La + 1)] = rq; dqT++;
                while (dq[dqH % (La + 1)] <= (int)(n % int.MaxValue) - La) dqH++;
                double mn = dqV[dqH % (La + 1)];
                minRunSum += mn - mins[mPos]; mins[mPos] = mn; mPos = (mPos + 1) % La;
                double sm = Math.Min(1, minRunSum / La);
                limG = sm < limG ? sm : sm + relLim * (limG - sm);
                // sygnał opóźniony o La (z tym samym wzmocnieniem, jakie miał w chwili wejścia)
                double ol = dL[dPos] * limG, or = dR[dPos] * limG;
                dL[dPos] = (float)(l * lin); dR[dPos] = (float)(r * lin); dPos = (dPos + 1) % Dl; n++;
                if (ol > ceil) ol = ceil; else if (ol < -ceil) ol = -ceil;   // bezpiecznik
                if (or > ceil) or = ceil; else if (or < -ceil) or = -ceil;
                buf[2 * f] = (float)ol; buf[2 * f + 1] = (float)or;
                ScopeOut[(ScopePos - 1) & 65535] = vS > 1e-3 ? (float)((ol + or) * 0.5 / vS) : 0f;   // widmo „po korekcie” (bez głośności odtwarzacza - porównanie z surowym wejściem)
                Out.Push(ol, or); Vu(ol, or);
                {   // pomiar dla doradcy lektora: wynik w paśmie 1–4 kHz (przed głośnością odtwarzacza), stała czasu 3 s
                    double om = vS > 1e-3 ? (ol + or) * 0.5 / vS : 0, ab = aLp.L(aHp.L(om)); aP += (ab * ab - aP) / (3.0 * Fs);
                }
                double tpo = tpOut.Push(ol, or); if (tpo > peakHold) peakHold = tpo;
                if (++pkN == 2400) { pkN = 0; if (++peakN >= 20) { PeakOutDb = 20 * Math.Log10(peakHold + 1e-12); peakHold = 0; peakN = 0; } }   // szczyt True Peak z ostatniej sekundy
                GainDb = g; LimitDb = 20 * Math.Log10(limG); sinceStart++;
            }
            Frames += frames; SnapAdd();
        }
    }
}
