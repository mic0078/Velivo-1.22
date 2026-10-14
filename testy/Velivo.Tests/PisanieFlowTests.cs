using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Przegladarka;
using Xunit;

// Pisanie przeciagnieciem po klawiaturze ekranowej: dopasowanie sladu do slow ze slownika i wlasny slownik.
[Collection("SwipeTyping")]
public class PisanieFlowTests
{
    // srodki klawiszy jak na klawiaturze Velivo (klawisz 46 px + 6 px odstepu, rzedy przesuniete)
    static Dictionary<char, Point> Klawisze()
    {
        var d = new Dictionary<char, Point>();
        var rows = new[] { ("qwertyuiop", 30.0, 0.0), ("asdfghjkl", 42.0, 52.0), ("zxcvbnm", 66.0, 104.0) };
        foreach (var (r, x0, y) in rows) for (int i = 0; i < r.Length; i++) d[r[i]] = new Point(x0 + i * 52, y);
        return d;
    }

    // slad przez srodki klawiszy slowa z drobnym drzeniem reki
    static List<Point> Slad(string word, Dictionary<char, Point> k)
    {
        var pts = new List<Point>(); int j = 0;
        foreach (var c in word)
        {
            var p = k[SwipeTyping.BaseKey(c)];
            pts.Add(new Point(p.X + (j % 2 == 0 ? 6 : -5), p.Y + (j % 3 == 0 ? 4 : -3))); j++;
        }
        return pts;
    }

    [Fact]
    public void RozpoznajeSlowoZeSladu()
    {
        SwipeTyping.Load(new[] { "nie", "to", "dom", "dobry", "dzień", "kot", "telewizor", "film" });
        var k = Klawisze();
        Assert.Equal("dom", SwipeTyping.Recognize(Slad("dom", k), k, 52)[0]);
        Assert.Equal("dzień", SwipeTyping.Recognize(Slad("dzien", k), k, 52)[0]);   // polska litera na klawiszu podstawowym
        Assert.Equal("telewizor", SwipeTyping.Recognize(Slad("telewizor", k), k, 52)[0]);
    }

    // wolny ruch z drzeniem reki (np. przy niepelnosprawnosci): duzo punktow, zatrzymania na klawiszach
    [Fact]
    public void WolneDrzacePrzeciagniecie()
    {
        SwipeTyping.Load(new[] { "nie", "to", "dom", "dobry", "dzień", "kot", "telewizor", "film", "źle", "kocham", "mama", "jak" });
        var k = Klawisze(); var rnd = new System.Random(7);
        foreach (var w in new[] { "dom", "dzień", "telewizor", "źle", "kocham", "mama" })
        {
            var pts = new List<Point>(); char prev = ' ';
            foreach (var c in w)
            {
                var b = SwipeTyping.BaseKey(c); if (b == prev) continue; prev = b; var p = k[b];
                if (pts.Count > 0) { var q = pts[pts.Count - 1]; for (int s = 1; s < 30; s++) pts.Add(new Point(q.X + (p.X - q.X) * s / 30 + rnd.Next(-14, 14), q.Y + (p.Y - q.Y) * s / 30 + rnd.Next(-14, 14))); }
                for (int s = 0; s < 12; s++) pts.Add(new Point(p.X + rnd.Next(-16, 16), p.Y + rnd.Next(-14, 14)));
            }
            Assert.Equal(w, SwipeTyping.Recognize(pts, k, 52)[0]);
        }
    }

    [Fact]
    public void WlasneSlowoDajeSiePisacPrzeciagnieciem()
    {
        SwipeTyping.Load(new[] { "nie", "to", "dom" });
        var k = Klawisze();
        Assert.False(SwipeTyping.IsKnown("velivo"));
        SwipeTyping.AddUserWord("Velivo");
        Assert.True(SwipeTyping.IsKnown("velivo"));
        Assert.Equal("velivo", SwipeTyping.Recognize(Slad("velivo", k), k, 52)[0]);
        SwipeTyping.RemoveUserWord("velivo");
        Assert.False(SwipeTyping.IsKnown("velivo"));
    }

    [Fact]
    public void ZapisIOdczytNauki()
    {
        var f = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "velivo-slowa-" + System.Guid.NewGuid() + ".txt");
        SwipeTyping.SaveLearned(f, new Dictionary<string, int> { ["dom"] = 3 }, new HashSet<string> { "velivo" });
        var own = new HashSet<string>();
        var d = SwipeTyping.LoadLearned(f, own);
        Assert.Equal(3, d["dom"]);
        Assert.Contains("velivo", own);
        System.IO.File.Delete(f);
    }

    [Fact]
    public void WbudowanySlownikPolski()
    {
        var name = typeof(MainWindow).Assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("slowa-pl.txt"));
        Assert.NotNull(name);
        // bez wydluzen z napisow ("taak", "taaak") - przy przeciaganiu wygladaja jak "tak"
        using var rd = new System.IO.StreamReader(typeof(MainWindow).Assembly.GetManifestResourceStream(name));
        var words = rd.ReadToEnd().Split('\n');
        Assert.Contains("tak", words);
        Assert.DoesNotContain("taak", words);
        Assert.DoesNotContain("taaak", words);
    }
}
