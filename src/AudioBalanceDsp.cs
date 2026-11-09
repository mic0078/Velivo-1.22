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
        // mediana głośności chwilowej z 10 s (bez ciszy poniżej MedianGate) - poziom „dialogu”, odporny na krótkie wybuchy
        readonly double[] mom = new double[100]; readonly double[] sortBuf = new double[100]; int momN, momI;
        public double MedianGate = -60, Median = double.NegativeInfinity;
        public AbMeter()
        {   // współczynniki K-weighting dla 48 kHz wprost z normy (etap 1: półka, etap 2: górnoprzepustowy RLB)
            shelf.Set(1.53512485958697, -2.69169618940638, 1.19839281085285, 1, -1.69065929318241, 0.73248077421585);
            hp.Set(1.0, -2.0, 1.0, 1, -1.99004745483398, 0.99007225036621);
        }
        public static double Lufs(double meanSq) => meanSq <= 1e-20 ? double.NegativeInfinity : -0.691 + 10 * Math.Log10(meanSq);
        public void ResetWindows() { shelf.Reset(); hp.Reset(); Array.Clear(blocks, 0, 30); nBlocks = 0; bi = 0; acc = 0; accN = 0; momN = 0; momI = 0; Momentary = ShortTerm = Median = double.NegativeInfinity; }
        public void ResetIntegrated() { Array.Clear(hCnt, 0, hCnt.Length); Array.Clear(hSum, 0, hSum.Length); }
        // jedna ramka stereo; zwraca chwilową moc K (do detektora kompresora)
        public double Push(double l, double r)
        {
            double kl = hp.L(shelf.L(l)), kr = hp.R(shelf.R(r));
            double p = kl * kl + kr * kr;   // wagi kanałów L/R = 1
            acc += p; LastPower = p;
            if (++accN == Block)
            {
                blocks[bi] = acc / Block; bi = (bi + 1) % 30; if (nBlocks < 30) nBlocks++; acc = 0; accN = 0;
                double m = 0, s = 0; int nm = Math.Min(4, nBlocks);
                for (int k = 0; k < nBlocks; k++) { double v = blocks[(bi - 1 - k + 60) % 30]; s += v; if (k < nm) m += v; }
                Momentary = nBlocks >= 4 ? Lufs(m / 4) : double.NegativeInfinity;
                ShortTerm = nBlocks >= 30 ? Lufs(s / 30) : Lufs(s / nBlocks);
                if (nBlocks >= 4)
                {
                    mom[momI] = Momentary; momI = (momI + 1) % 100; if (momN < 100) momN++;
                    int c = 0; for (int k = 0; k < momN; k++) if (mom[k] > MedianGate) sortBuf[c++] = mom[k];
                    if (c >= 20) { Array.Sort(sortBuf, 0, c); Median = sortBuf[c / 2]; }
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

    public sealed class AbProfile
    {
        public string Name; public double MaxBoost, MaxCut, UpDbS, DownDbS, Gate, ThrOff, Ratio, AttackMs, ReleaseMs;
        public static readonly AbProfile Light = new AbProfile { Name = "light", MaxBoost = 6, MaxCut = 8, UpDbS = 0.5, DownDbS = 1.0, Gate = -25, ThrOff = 8, Ratio = 2, AttackMs = 25, ReleaseMs = 400 };
        public static readonly AbProfile Balanced = new AbProfile { Name = "balanced", MaxBoost = 12, MaxCut = 12, UpDbS = 1.5, DownDbS = 2.0, Gate = -30, ThrOff = 4, Ratio = 3, AttackMs = 12, ReleaseMs = 300 };
        public static readonly AbProfile Night = new AbProfile { Name = "night", MaxBoost = 18, MaxCut = 18, UpDbS = 2.5, DownDbS = 3.0, Gate = -35, ThrOff = 0, Ratio = 6, AttackMs = 6, ReleaseMs = 200 };
        public static AbProfile ByName(string n) => n == "light" ? Light : n == "balanced" ? Balanced : n == "night" ? Night : null;
    }

    public sealed class AbProcessor
    {
        public const int Fs = AbMeter.Fs;
        public static readonly double[] EqFreq = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        public volatile AbProfile Profile;      // null = bez wyrównywania (sam korektor / przejście)
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
        readonly AbBiquad[] eq = new AbBiquad[10]; readonly double[] eqDb = new double[10]; volatile bool eqDirty = true;
        // próbki wyjścia (mono) dla widżetu - widmo (FFT) liczy wątek okna widżetu, nie wątek dźwięku
        public readonly float[] Scope = new float[8192]; public volatile int ScopePos; int pkN;
        public volatile bool Active; public long Frames;   // ostatnio przetworzony dźwięk (do wskaźnika „pracuje”)
        public readonly AbMeter In = new AbMeter(), Out = new AbMeter();
        readonly AbTruePeak tpIn = new AbTruePeak(), tpOut = new AbTruePeak();
        double levDb, detP, grDb;
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
            In.ResetWindows(); Out.ResetWindows(); tpIn.Reset(); tpOut.Reset();
            Array.Clear(dL, 0, Dl); Array.Clear(dR, 0, Dl); for (int i = 0; i < La; i++) mins[i] = 1;
            minRunSum = La; limG = 1; dPos = 0; mPos = 0; dqH = dqT = 0; n = 0; detP = 0; grDb = 0; dHp.Reset(); dLp.Reset(); dS = dM = dSide = dG = 0;
            foreach (var b in eq) b?.Reset();
        }
        volatile bool newReq;
        public void NewMedia() => newReq = true;   // z wątku okna - wykonane w wątku dźwięku przed kolejnym kawałkiem

        // przetwarza ramki stereo float w miejscu (bufor VLC → wyjście)
        public void Process(float[] buf, int frames)
        {
            if (newReq) { newReq = false; Reset(); In.ResetIntegrated(); Out.ResetIntegrated(); levDb = 0; }
            if (double.IsNaN(levDb) || double.IsNaN(grDb) || double.IsNaN(limG) || double.IsNaN(detP)) { Reset(); levDb = 0; }   // samonaprawa
            if (eqDirty) { lock (eqDb) for (int i = 0; i < 10; i++) eq[i] = AbBiquad.Peak(Fs, EqFreq[i], 1.41, eqDb[i]); eqDirty = false; }
            var pr = Profile; bool eqOn = EqOn; double ceil = Math.Pow(10, CeilingDb / 20), vol = Volume;
            if (double.IsNaN(vol) || vol < 0) vol = 1; else if (vol > 2) vol = 2;
            double upS = pr == null ? 0 : pr.UpDbS / Fs, dnS = pr == null ? 0 : pr.DownDbS / Fs;
            double aC = pr == null ? 0 : Math.Exp(-1000.0 / (pr.AttackMs * Fs)), rC = pr == null ? 0 : Math.Exp(-1000.0 / (pr.ReleaseMs * Fs)), detC = Math.Exp(-1000.0 / (10.0 * Fs));
            double relLim = Math.Exp(-1000.0 / (80.0 * Fs));
            int dlg = Dialog; double dMax = dlg == 2 ? 8 : dlg == 1 ? 4 : 0;
            double eC = Math.Exp(-1000.0 / (50.0 * Fs)), dAtt = Math.Exp(-1000.0 / (80.0 * Fs)), dRel = Math.Exp(-1000.0 / (400.0 * Fs));
            if (Bypass || (pr == null && !eqOn && dlg == 0))
            {   // wyłączone w trakcie odtwarzania: dźwięk bez zmian (tylko głośność odtwarzacza), pomiar i widmo dalej działają
                for (int f = 0; f < frames; f++)
                {
                    double l = buf[2 * f] * vol, r = buf[2 * f + 1] * vol;
                    buf[2 * f] = (float)l; buf[2 * f + 1] = (float)r;
                    In.Push(l, r); Out.Push(l, r);
                    double tpo = tpOut.Push(l, r); if (tpo > peakHold) peakHold = tpo;
                    int sp = ScopePos; Scope[sp] = (float)((l + r) * 0.5); ScopePos = (sp + 1) & 8191;
                    if (++pkN == 2400) { pkN = 0; if (++peakN >= 20) { PeakOutDb = 20 * Math.Log10(peakHold + 1e-12); peakHold = 0; peakN = 0; } }
                }
                GainDb = 0; LimitDb = 0; Frames += frames; limG = 1; return;
            }
            for (int f = 0; f < frames; f++)
            {
                double l = buf[2 * f], r = buf[2 * f + 1];
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
                    // 1) wyrównywacz: powoli (dB/s) do celu wg mediany głośności z 10 s (dialog; krótkie wybuchy obsługuje kompresor),
                    //    w ciszy wstrzymany (nie podnosi szumu)
                    In.MedianGate = Target + pr.Gate;
                    if (In.Median > double.NegativeInfinity && In.Momentary > Target + pr.Gate)
                    {
                        double want = Math.Max(-pr.MaxCut, Math.Min(pr.MaxBoost, Target - In.Median));
                        levDb += Math.Max(-dnS, Math.Min(upS, want - levDb));
                    }
                    // 2) kompresor: szybki detektor mocy K (10 ms) po wyrównaniu, miękkie kolano 6 dB
                    double pl = p * Math.Pow(10, levDb / 10);
                    detP = pl + detC * (detP - pl);
                    double lev = AbMeter.Lufs(detP), thr = Target + pr.ThrOff, over = lev - thr, want2 = 0;
                    if (over > 3) want2 = -over * (1 - 1 / pr.Ratio);
                    else if (over > -3) want2 = -(1 - 1 / pr.Ratio) * (over + 3) * (over + 3) / 12;
                    if (over > 8) want2 -= (over - 8) * (1 / pr.Ratio - 0.05);   // powyżej progu + 8 dB prawie twarda granica (wybuchy nie przeskakują poziomu)
                    grDb = want2 < grDb ? want2 + aC * (grDb - want2) : want2 + rC * (grDb - want2);
                    g = levDb + grDb;
                }
                double lin = Math.Pow(10, g / 20) * vol;
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
                Out.Push(ol, or);
                double tpo = tpOut.Push(ol, or); if (tpo > peakHold) peakHold = tpo;
                int sp = ScopePos; Scope[sp] = (float)((ol + or) * 0.5); ScopePos = (sp + 1) & 8191;
                if (++pkN == 2400) { pkN = 0; if (++peakN >= 20) { PeakOutDb = 20 * Math.Log10(peakHold + 1e-12); peakHold = 0; peakN = 0; } }   // szczyt True Peak z ostatniej sekundy
                GainDb = g; LimitDb = 20 * Math.Log10(limG);
            }
            Frames += frames;
        }
    }
}
