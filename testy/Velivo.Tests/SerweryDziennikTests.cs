using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Velivo;
using Xunit;

public class SerweryDziennikTests
{
    static string Tmp() => Path.Combine(Path.GetTempPath(), "velivo-ev-" + Guid.NewGuid().ToString("N") + ".jsonl");

    static void Sync(KomEventLog from, KomEventLog to)
    {
        for (int i = 0; i < 1000; i++) { var m = from.Missing(to.Have(), 8000); if (m.Count == 0) return; foreach (var e in m) to.Merge(e); }
    }

    [Fact]
    public void PowrotSerwera_DostajeTylkoBrakujace_BezDuplikatow()
    {
        var a = new KomEventLog(Tmp(), "A"); var b = new KomEventLog(Tmp(), "B");
        for (int i = 0; i < 300; i++) b.Add("msg", "x", new JsonObject { ["mid"] = "m" + i });
        a.Add("msg", "y", new JsonObject { ["mid"] = "a1" });
        Sync(b, a); Sync(a, b);
        Assert.Equal(301, a.Count); Assert.Equal(301, b.Count);
        Sync(b, a);
        Assert.Equal(301, a.Count);
        Assert.False(a.Behind(b.Have())); Assert.False(b.Behind(a.Have()));
    }

    [Fact]
    public void ZgubionyPakiet_LicznikBezDziur()
    {
        var b = new KomEventLog(Tmp(), "B"); var a = new KomEventLog(Tmp(), "A");
        var e1 = b.Add("msg", "x", null); var e2 = b.Add("msg", "x", null); var e3 = b.Add("msg", "x", null);
        a.Merge(e1); a.Merge(e3);
        Assert.Equal(1, (long)a.Have()["B"]);
        Assert.True(a.Behind(b.Have()));
        Assert.Single(b.Missing(a.Have(), 100000).Where(e => (long)e["n"] == 2));
        a.Merge(e2);
        Assert.Equal(3, (long)a.Have()["B"]);
    }

    [Fact]
    public void PoPonownymUruchomieniu_PamietaStan()
    {
        var f = Tmp(); var a = new KomEventLog(f, "A");
        a.Add("msg", "x", null); a.Add("done", "x", new JsonArray("m1"));
        var a2 = new KomEventLog(f, "A");
        Assert.Equal(2, a2.Count); Assert.Equal(2, (long)a2.Have()["A"]);
        Assert.Equal(3, (long)a2.Add("msg", "x", null)["n"]);
    }

    [Fact]
    public void Porzadki_NieCofajaLicznika()
    {
        var f = Tmp(); var a = new KomEventLog(f, "A");
        a.Add("msg", "x", null, 1); a.Add("msg", "x", null, 2); a.Add("msg", "x", null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(2, a.Prune(1000));
        var a2 = new KomEventLog(f, "A");
        Assert.Equal(1, a2.Count); Assert.Equal(3, (long)a2.Have()["A"]);
        var b = new KomEventLog(Tmp(), "B");
        var old = b.Add("msg", "x", null); var a3 = new KomEventLog(Tmp(), "A");
        Assert.True(a3.Merge(old)); Assert.False(a3.Merge(old));
    }

    [Fact]
    public void Paczka_MaLimitRozmiaru()
    {
        var a = new KomEventLog(Tmp(), "A");
        for (int i = 0; i < 50; i++) a.Add("msg", "x", new JsonObject { ["t"] = new string('z', 1000) });
        var m = a.Missing(new JsonObject(), 8000);
        Assert.InRange(m.Count, 1, 8);
    }
}
