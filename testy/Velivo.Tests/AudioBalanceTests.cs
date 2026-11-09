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
        Assert.True(boom / nb - dial / nd < 10, "różnica: " + (boom / nb - dial / nd));   // wejście: ok. 35 dB
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
        Assert.InRange(after / na, -21, -15);     // kilka sekund po reklamie program znowu przy celu
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
            Assert.InRange(p.Out.ShortTerm, -20, -16);
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
        Assert.InRange(outM.Integrated, -19, -17);
    }
}
