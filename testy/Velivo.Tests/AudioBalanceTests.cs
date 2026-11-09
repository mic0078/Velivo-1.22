using System.Collections.Generic;
using System;
using Velivo;
using Xunit;

// 🎚 Audio Balance: pomiar wg ITU-R BS.1770-4 i limiter True Peak (czysty DSP, bez VLC)
public class AudioBalanceTests
{
    const int Fs = 48000;

    [Fact]
    public void SinusMinus20DbfsStereoDajeMinus20Lufs()
    {
        var m = new AbMeter(); double a = Math.Pow(10, -20 / 20.0);
        for (int i = 0; i < Fs * 10; i++) { double v = a * Math.Sin(2 * Math.PI * 997 * i / Fs); m.Push(v, v); }
        Assert.InRange(m.Momentary, -20.2, -19.8);
        Assert.InRange(m.ShortTerm, -20.2, -19.8);
        Assert.InRange(m.Integrated, -20.2, -19.8);
    }

    [Fact]
    public void TruePeakWidziSzczytMiedzyProbkami()
    {
        var tp = new AbTruePeak(); double mx = 0;   // sinus fs/4 z fazą 45°: próbki -3 dBFS, prawdziwy szczyt 0 dBTP
        for (int i = 0; i < 4800; i++) { double v = Math.Sin(2 * Math.PI * 12000 * i / Fs + Math.PI / 4); mx = Math.Max(mx, tp.Push(v, v)); }
        Assert.InRange(20 * Math.Log10(mx), -0.5, 0.5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("light")]
    [InlineData("balanced")]
    [InlineData("night")]
    public void LimiterTrzymaMinus1Dbtp(string profil)
    {
        var p = new AbProcessor { Profile = AbProfile.ByName(profil), Target = -14 };
        if (profil == null) p.EqOn = true;   // bez profilu i korektora procesor nie zmienia dźwięku
        var buf = new float[2 * 480]; var tp = new AbTruePeak(); double mx = 0; var rnd = new Random(1);
        for (int blk = 0; blk < 300; blk++)
        {
            for (int k = 0; k < 480; k++) { double v = blk % 50 < 25 ? Math.Sin(2 * Math.PI * 12000 * (blk * 480 + k) / Fs + Math.PI / 4) : rnd.NextDouble() * 2 - 1; buf[2 * k] = (float)v; buf[2 * k + 1] = (float)v; }
            p.Process(buf, 480);
            for (int k = 0; k < 480; k++) mx = Math.Max(mx, tp.Push(buf[2 * k], buf[2 * k + 1]));
        }
        Assert.True(20 * Math.Log10(mx) <= -0.9, "True Peak wyjścia: " + 20 * Math.Log10(mx));
    }

    [Fact]
    public void NightPodnosiCichyDialogIObnizaWybuch()
    {
        var p = new AbProcessor { Profile = AbProfile.Night, Target = -18 }; var buf = new float[2 * 480]; var rnd = new Random(1);
        double dial = 0, boom = 0; int nd = 0, nb = 0;
        for (int blk = 0; blk < Fs * 90 / 480; blk++)
        {
            double t = blk * 480.0 / Fs; bool b = (t % 20) > 15 && (t % 20) < 17; double amp = b ? Math.Pow(10, -3 / 20.0) : Math.Pow(10, -38 / 20.0);
            for (int k = 0; k < 480; k++) { double v = amp * (b ? rnd.NextDouble() * 2 - 1 : Math.Sin(2 * Math.PI * 300 * (blk * 480 + k) / Fs)); buf[2 * k] = (float)v; buf[2 * k + 1] = (float)v; }
            p.Process(buf, 480);
            if (t > 40) { if (b && (t % 20) > 15.5) { boom += p.Out.Momentary; nb++; } else if ((t % 20) > 5 && (t % 20) < 14) { dial += p.Out.Momentary; nd++; } }
        }
        Assert.True(dial / nd > -25, "dialog: " + dial / nd);              // wejście ok. -38
        Assert.True(boom / nb - dial / nd < 12, "różnica: " + (boom / nb - dial / nd));   // wejście: ok. 35 dB; zapas nad dialogiem celowy (sylaby nie są ścinane)
    }

    [Fact]
    public void ReklamaSciszonaOdRazuAProgramWracaDoCelu()
    {
        var p = new AbProcessor { Profile = AbProfile.Balanced, Target = -18 }; var buf = new float[960]; var rnd = new Random(1);
        double hit = -1, after = 0; int na = 0;
        for (int b = 0; b < 5000; b++)
        {
            double t = b * 0.01, amp = t < 30 || t > 40 ? 0.1 : 0.32;   // reklama +10 dB przez 10 s
            for (int k = 0; k < 480; k++) { double v = amp * (0.5 * Math.Sin(2 * Math.PI * 440 * (b * 480 + k) / 48000.0) + 0.5 * (rnd.NextDouble() * 2 - 1)); buf[2 * k] = buf[2 * k + 1] = (float)v; }
            p.Process(buf, 480);
            if (t > 30.05 && t < 40 && hit < 0 && p.Out.Fast < -16) hit = t - 30;
            if (t > 45 && t < 47) { after += p.Out.Momentary; na++; }
        }
        Assert.InRange(hit, 0, 0.3);              // ściszone w ułamku sekundy
        Assert.InRange(after / na, -24, -15);     // kilka sekund po reklamie program znowu w strefie celu (strefa komfortu ±3 dB)
    }

    [Theory]
    [InlineData("light")]
    [InlineData("normal")]
    [InlineData("strong")]
    public void LimitOnlyNigdyNiePodnosi(string sila)
    {
        // Limit Only: bardzo cichy dialog (-46 LUFS), potem głośna muzyka i znów cichy - wzmocnienie nigdy powyżej 0 dB (także z nauką i poprawką celu)
        var p = new AbProcessor { Profile = AbProfile.ByName("limit", sila), Target = 0, SeedLevDb = 8, TargetAdj = 3 }; var buf = new float[960]; var rnd = new Random(4); double maxG = double.NegativeInfinity;
        p.NewMedia();
        for (int b = 0; b < 6000; b++)
        {
            double amp = b < 2000 || b >= 4000 ? 0.01 : 0.4;
            for (int k = 0; k < 480; k++) { double x = amp * (0.5 * Math.Sin(2 * Math.PI * 300 * (b * 480 + k) / 48000.0) + 0.3 * (rnd.NextDouble() * 2 - 1)); buf[2 * k] = buf[2 * k + 1] = (float)x; }
            p.Process(buf, 480); maxG = Math.Max(maxG, p.GainDb);
        }
        Assert.True(maxG <= 0.01, $"Limit Only podniósł o {maxG:0.00} dB");
        // to samo przy stałym celu -14 (głośno) i bardzo cichym źródle: dalej żadnego podgłośnienia
        var q = new AbProcessor { Profile = AbProfile.ByName("limit", sila), Target = -14, SeedLevDb = 20, TargetAdj = 6 }; q.NewMedia(); double mq = double.NegativeInfinity;
        for (int b = 0; b < 3000; b++) { for (int k = 0; k < 480; k++) { double x = 0.005 * (rnd.NextDouble() * 2 - 1); buf[2 * k] = buf[2 * k + 1] = (float)x; } q.Process(buf, 480); mq = Math.Max(mq, q.GainDb); }
        Assert.True(mq <= 0.01, $"Limit Only (cel -14) podniósł o {mq:0.00} dB");
    }

    [Fact]
    public void NaukaKolejneSeanseUtrwalajaSredniaANietypowyNieDominuje()
    {
        // film: 5 seansów po 40 min na ok. -24 LUFS - średnia blisko -24; potem nietypowy seans (-10, np. sama głośna scena) prawie nic nie zmienia
        double avg = double.NaN, w = 0;
        foreach (var m in new[] { -24.5, -23.6, -24.2, -23.9, -24.1 }) (avg, w) = AbLearnMath.Merge(avg, w, m, 40, false);
        Assert.InRange(avg, -24.3, -23.8);
        var (a2, _) = AbLearnMath.Merge(avg, w, -10, 40, false);
        Assert.InRange(a2 - avg, 0, 1.0);
        // krótki seans (20 s) nie liczy się wcale
        Assert.True(AbLearnMath.Merge(avg, w, -10, 0.3, false).avg == avg);
        // waga rośnie z każdym seansem (jeden seans najwyżej 30 min), film do 240 min
        Assert.True(w == 5 * AbLearnMath.MaxSessionW, $"waga {w}");   // każdy seans 40 min liczy się jako 30 (limit jednego seansu)
    }

    [Fact]
    public void NaukaKanalTvNadazaZaZmianaProgramu()
    {
        // kanał TV: długo -20 LUFS, potem program na -26 przez 3 seanse po 30 min - pamięć przesuwa się wyraźnie (historia TV do 60 min)
        double avg = double.NaN, w = 0;
        for (int i = 0; i < 6; i++) (avg, w) = AbLearnMath.Merge(avg, w, -20, 30, true);
        Assert.True(w == AbLearnMath.CapTv);
        for (int i = 0; i < 3; i++) (avg, w) = AbLearnMath.Merge(avg, w, -26, 30, true);
        Assert.InRange(avg, -25.5, -23.5);   // nadąża (film z tą samą historią zostałby przy -20)
    }

    [Fact]
    public void NaukaKorektaGlosnosciBezPodwojnegoNaliczania()
    {
        // baza 50%; na kanale X podgłaśniasz do 71% (+3 dB) - upodobanie X = +3 dB
        double adj = 0, aw = 0, eff = 0;
        double pref = AbLearnMath.SessionPref(eff, AbLearnMath.VolDb(71, 50)); (adj, aw) = AbLearnMath.MergeW(adj, aw, pref, 30, AbLearnMath.CapTv);
        Assert.InRange(adj, 2.9, 3.1);
        // następny raz: suwak dalej na 71% (już +3 dB) - automatyczna korekta 0 (nie dolicza drugi raz)
        Assert.InRange(AbLearnMath.EffAdj(adj, AbLearnMath.VolDb(71, 50)), -0.1, 0.1);
        // suwak wrócił do bazy 50% - korekta daje całe +3 dB
        Assert.InRange(AbLearnMath.EffAdj(adj, AbLearnMath.VolDb(50, 50)), 2.9, 3.1);
        // 10 seansów bez ruszania suwaka (korekta zastosowana, suwak na bazie) - upodobanie stoi w miejscu (nie narasta)
        for (int i = 0; i < 10; i++) { double e = AbLearnMath.EffAdj(adj, 0); (adj, aw) = AbLearnMath.MergeW(adj, aw, AbLearnMath.SessionPref(e, 0), 30, AbLearnMath.CapTv); }
        Assert.InRange(adj, 2.9, 3.1);
        // inny film: suwak podniesiony dla X nie jest jego korektą - film Y bez wpisu zaczyna od 0
        Assert.True(AbLearnMath.EffAdj(0, 0) == 0);
        // ściszanie: do 35% (-3,1 dB) na kanale Z
        Assert.InRange(AbLearnMath.SessionPref(0, AbLearnMath.VolDb(35, 50)), -3.2, -3.0);
    }

    [Fact]
    public void NaukaZapisIOdczytZgodnyZeStarymiDanymi()
    {
        // zapis ze starszej wersji (pole Lev, bez W/AdjW) wczytuje się; nowy zapis → odczyt bez strat
        var old = "{\"Kanał A\":{\"Lev\":7.2,\"Ad\":-8,\"Adj\":1.5,\"Avg\":-17.2,\"N\":40,\"T\":\"2026-11-09T05:00:00\"}}";
        var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, AbLearnEntry>>(old);
        Assert.True(d["Kanał A"].Avg == -17.2 && d["Kanał A"].Ad == -8 && d["Kanał A"].W == 0);
        d["Kanał A"].W = 60; d["Kanał A"].AdjW = 30;
        var back = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, AbLearnEntry>>(System.Text.Json.JsonSerializer.Serialize(d));
        Assert.True(back["Kanał A"].W == 60 && back["Kanał A"].AdjW == 30 && back["Kanał A"].Adj == 1.5 && !System.Text.Json.JsonSerializer.Serialize(d).Contains("Lev"));
        // ∞ / NaN (np. pomiar ciszy) - zapis nie rzuca błędem (było: okno „nieoczekiwany błąd”), wartości oczyszczone
        var bad = new AbLearnEntry { Ad = double.NegativeInfinity, Avg = double.NegativeInfinity, W = 30, Adj = double.NaN };
        bad.Sanitize();
        var js = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, AbLearnEntry> { ["x"] = bad }, AbLearnEntry.Json);
        Assert.True(bad.Ad == 0 && bad.Adj == 0 && bad.W == 0 && !js.Contains("Infinity") && !js.Contains("NaN"));
    }

    [Fact]
    public void NaukaMedianaSeansuOdpornaNaKrotkiHuk()
    {
        // seans 10 min co sekundę ~-24, w środku 20 s huku -8 - mediana zostaje przy dialogu
        var v = new List<float>(); for (int i = 0; i < 600; i++) v.Add(i >= 300 && i < 320 ? -8f : -24f + (i % 5) * 0.2f);
        Assert.InRange(AbLearnMath.Median(v), -24.1, -23.0);
    }

    [Fact]
    public void AutoWyrownujeANiePodglasnia()
    {
        // Auto: film na zmianę cichy i głośny (różnica ~12 dB, co 4 s) - różnica maleje, a średnia zostaje przy średniej filmu (bez podgłaśniania całości)
        var p = new AbProcessor { Profile = AbProfile.Balanced, Target = 0 }; var buf = new float[960]; var rnd = new Random(1);
        p.NewMedia(); double q = 0, g = 0; int nq = 0, ng = 0;
        for (int b = 0; b < 12000; b++)
        {
            bool loud = (b / 400) % 2 == 1; double amp = loud ? 0.2 : 0.05;
            for (int k = 0; k < 480; k++) { double x = amp * (0.5 * Math.Sin(2 * Math.PI * 300 * (b * 480 + k) / 48000.0) + 0.4 * (rnd.NextDouble() * 2 - 1)); buf[2 * k] = buf[2 * k + 1] = (float)x; }
            p.Process(buf, 480);
            if (b > 6000 && b % 400 == 350) { if (loud) { g += p.Out.Momentary; ng++; } else { q += p.Out.Momentary; nq++; } }
        }
        double diff = g / ng - q / nq;
        Assert.True(diff < 12, $"różnica {diff:0.0} dB");   // 12 dB co 4 s - regulator celowo nie goni tak szybkich zmian (to byłoby pompowanie); głośne części łapie szybka warstwa
        Assert.InRange(p.Out.Integrated - p.In.Integrated, -3, 3);
    }

    [Fact]
    public void LektorCzesteSciszanieNieZmieniaRegulacjiFilmu()
    {
        // lektor co 5 s przez 3 s ścisza film o 8 dB (Volume 0,4) - wzmocnienie filmu (Audio Balance) takie samo jak bez lektora
        double Run(bool lektor)
        {
            var p = new AbProcessor { Profile = AbProfile.Balanced, Target = 0 }; var buf = new float[960]; var rnd = new Random(1); double sum = 0; int n = 0;
            p.NewMedia();
            for (int b = 0; b < 18000; b++)
            {
                double t = b * 0.01; p.Volume = lektor && t > 20 && (t % 5) < 3 ? 0.4f : 1f;
                for (int k = 0; k < 480; k++) { double tt = (b * 480 + k) / 48000.0; buf[2 * k] = buf[2 * k + 1] = (float)(0.06 * Math.Max(0, Math.Sin(2 * Math.PI * 3 * tt)) * (Math.Sin(2 * Math.PI * 220 * tt) + 0.2 * (rnd.NextDouble() * 2 - 1)) + 0.02 * Math.Sin(2 * Math.PI * 110 * tt)); }
                p.Process(buf, 480); if (b >= 6000) { sum += p.GainDb; n++; }
            }
            return sum / n;
        }
        Assert.InRange(Run(true) - Run(false), -0.3, 0.3);
    }

    [Fact]
    public void DoradcaLektoraOcenaIPropozycja()
    {
        // lektor w paśmie mowy -20 dBFS (głośność 1), film -24 ściszony do 40% (-8 dB) → przewaga +12 dB = dobrze słyszalny
        var a = new AbLektorAdvisor();
        for (int i = 0; i < 10; i++) a.Add(AbLektorAdvisor.CueMargin(-20, 1, -24, 0.4, 0.1));
        Assert.True(a.Status == 0 && a.SuggestOverUp() == 0, $"status {a.Status}, margines {a.Margin:0.0}");
        // film głośny w paśmie mowy (-18), bez ściszenia, w filmie trwa dialog → zagłuszany i propozycja podniesienia przewagi
        a.Reset();
        for (int i = 0; i < 10; i++) a.Add(AbLektorAdvisor.CueMargin(-20, 1, -18, 1, 0.7));
        Assert.True(a.Status == 2, $"status {a.Status}");
        Assert.InRange(a.SuggestOverUp(), 1, 10);
        // „częściowo zagłuszany” (+5 dB) - sama ocena, bez propozycji
        a.Reset();
        for (int i = 0; i < 10; i++) a.Add(5);
        Assert.True(a.Status == 1 && a.SuggestOverUp() == 0);
        // pojedyncza głośna scena (2 z 10 kwestii) nie zmienia oceny i nie daje propozycji
        a.Reset();
        for (int i = 0; i < 10; i++) a.Add(i < 2 ? -6 : 9);
        Assert.True(a.Status == 0 && a.SuggestOverUp() == 0);
    }

    [Fact]
    public void ProcesorRozpoznajeDialogiIMierzyPasmoMowy()
    {
        // mowa (sylaby, 220–1300 Hz) vs bas muzyki (60 Hz): udział pasma mowy wysoki dla mowy, niski dla basu; pasmo 1–4 kHz mierzone
        double Share(Func<double, double> f, out double band)
        {
            var p = new AbProcessor { Profile = AbProfile.Balanced, Target = 0 }; var buf = new float[960]; p.NewMedia();
            for (int b = 0; b < 500; b++) { for (int k = 0; k < 480; k++) { double t = (b * 480 + k) / 48000.0; buf[2 * k] = buf[2 * k + 1] = (float)f(t); } p.Process(buf, 480); }
            band = p.OutSpeechBandDb; return p.InDialogShare;
        }
        double mowa = Share(t => 0.1 * Math.Max(0, Math.Sin(2 * Math.PI * 3 * t)) * (Math.Sin(2 * Math.PI * 400 * t) + 0.6 * Math.Sin(2 * Math.PI * 1300 * t)), out var bm);
        double bas = Share(t => 0.3 * Math.Sin(2 * Math.PI * 60 * t), out var bb);
        Assert.True(mowa > 0.5 && bas < 0.2, $"mowa {mowa:0.00}, bas {bas:0.00}");
        Assert.True(bm > bb + 10, $"pasmo mowy: mowa {bm:0.0} dB, bas {bb:0.0} dB");
    }

    [Fact]
    public void SciszeniePodLektoraNieJestCofane()
    {
        // 🗣 lektor ścisza film suwakiem odtwarzacza (Volume 0,3): wynik ma zostać ok. 10 dB ciszej, nie wracać do celu
        double Run(float v)
        {
            var p = new AbProcessor { Profile = AbProfile.Balanced, Target = -18, Volume = v }; var buf = new float[960]; var rnd = new Random(1);
            p.NewMedia();
            for (int b = 0; b < 3000; b++)
            {
                for (int k = 0; k < 480; k++) { double x = 0.07 * (0.5 * Math.Sin(2 * Math.PI * 300 * (b * 480 + k) / 48000.0) + 0.4 * (rnd.NextDouble() * 2 - 1)); buf[2 * k] = buf[2 * k + 1] = (float)x; }
                p.Process(buf, 480);
            }
            return p.Out.Momentary;
        }
        Assert.InRange(Run(1f) - Run(0.3f), 9, 12);
    }

    [Fact]
    public void NaukaZnanyKanalOdRazuWCeluAPoprawkaPrzesuwaCel()
    {
        // 🧠 znany cichy kanał (-26): z zapamiętanym poziomem już po 1 s blisko celu; poprawka -3 dB przesuwa wynik o ok. 3 dB
        double Run(float seed, float adj, int blocks)
        {
            var p = new AbProcessor { Profile = AbProfile.Night, Target = -18, SeedLevDb = seed, TargetAdj = adj }; var buf = new float[960]; var rnd = new Random(1);
            p.NewMedia();
            for (int b = 0; b < blocks; b++)
            {
                for (int k = 0; k < 480; k++) { double v = 0.07 * (0.5 * Math.Sin(2 * Math.PI * 300 * (b * 480 + k) / 48000.0) + 0.4 * (rnd.NextDouble() * 2 - 1)); buf[2 * k] = buf[2 * k + 1] = (float)v; }
                p.Process(buf, 480);
            }
            return p.Out.Momentary;
        }
        double nowy = Run(float.NaN, 0, 100), znany = Run(8.5f, 0, 100);
        Assert.True(Math.Abs(znany + 18) < Math.Abs(nowy + 18), $"znany {znany:0.0}, nowy {nowy:0.0}");
        Assert.InRange(znany, -20, -16);
        Assert.InRange(Run(8.5f, 0, 1500) - Run(8.5f, -3, 1500), 0.8, 4);   // poprawka w dół - bez wpływu limitera na szczytach
    }

    [Fact]
    public void PrzelaczenieKanaluSzybkoDoCelu()
    {
        // kanały / filmy o różnej głośności: po 6 s od przełączenia wynik blisko celu (cichy -26, głośny -16)
        var p = new AbProcessor { Profile = AbProfile.Balanced, Target = -18 }; var buf = new float[960]; var rnd = new Random(1);
        foreach (var amp in new[] { 0.07, 0.22 })
        {
            p.NewMedia();
            for (int b = 0; b < 600; b++)
            {
                double t = b * 0.01;
                for (int k = 0; k < 480; k++) { double v = amp * (0.5 * Math.Sin(2 * Math.PI * 300 * (b * 480 + k) / 48000.0) * (1 + Math.Sin(2 * Math.PI * 3 * t)) + 0.4 * (rnd.NextDouble() * 2 - 1)); buf[2 * k] = buf[2 * k + 1] = (float)v; }
                p.Process(buf, 480);
            }
            Assert.InRange(p.Out.ShortTerm, -23, -15.5);   // Normal: cichy -26 → ok. -22 (85% różnicy poza strefą komfortu ±3 dB), głośny -16 → w strefie
        }
    }

    [Theory]
    [InlineData("light")]
    [InlineData("balanced")]
    [InlineData("night")]
    public void WynikTrafiaWCel(string profil)
    {
        // „film”: mowa z pauzami, muzyka w tle, co 15 s wybuch; źródło ok. -26 LUFS → wynik (zintegrowany od 60 s) przy celu -18 ±1
        var p = new AbProcessor { Profile = AbProfile.ByName(profil), Target = -18 }; var buf = new float[960]; var rnd = new Random(2); var outM = new AbMeter();
        for (int b = 0; b < 12000; b++)
        {
            double t = b * 0.01, sy = Math.Max(0, Math.Sin(2 * Math.PI * 4 * t)) * ((t % 6) < 4.5 ? 1 : 0); bool boom = (t % 15) > 12 && (t % 15) < 13;
            for (int k = 0; k < 480; k++) { int i = b * 480 + k; double v = 0.06 * sy * Math.Sin(2 * Math.PI * 250 * i / 48000.0) + 0.012 * Math.Sin(2 * Math.PI * 110 * i / 48000.0) + (boom ? 0.12 * (rnd.NextDouble() * 2 - 1) : 0); buf[2 * k] = buf[2 * k + 1] = (float)v; }
            p.Process(buf, 480);
            if (t > 60) for (int k = 0; k < 480; k++) outM.Push(buf[2 * k], buf[2 * k + 1]);
        }
        // intensywność: wynik = źródło + Amount × (cel − źródło) - Strong trafia w cel, Light zostawia połowę naturalnej różnicy
        double src = p.In.Integrated, dv = -18 - src; dv = Math.Sign(dv) * Math.Max(0, Math.Abs(dv) - p.Profile.Dead); double goal = src + Math.Max(-p.Profile.MaxCut, Math.Min(p.Profile.MaxBoost, p.Profile.Amount * dv));
        Assert.InRange(outM.Integrated, Math.Min(goal, src) - 1, -17);   // między źródłem a celem - bliżej celu przy większej intensywności, nigdy ponad cel
    }
}
