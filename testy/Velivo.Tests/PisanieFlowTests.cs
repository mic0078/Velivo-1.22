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

    // niedbaly ruch: start i koniec obok klawisza, przestrzelenie na zakretach
    [Fact]
    public void NiedbalyRuchZPrzestrzeleniem()
    {
        SwipeTyping.Load(new[] { "nie", "jak", "domu", "filmu", "kiedy", "kurwy", "jest", "mamy", "nagły", "sobie", "spokojnie" });
        var k = Klawisze();
        foreach (var w in new[] { "domu", "kiedy", "mamy", "sobie" })
        {
            var keys = w.Select(c => k[SwipeTyping.BaseKey(c)]).ToList();
            var pts = new List<Point> { new Point(keys[0].X + 20, keys[0].Y - 15) };
            for (int i = 1; i < keys.Count; i++)
            {
                var a = keys[i - 1]; var b = keys[i];
                var over = i < keys.Count - 1 ? new Point(b.X + (b.X - a.X) * 0.25, b.Y + (b.Y - a.Y) * 0.25) : new Point(b.X - 18, b.Y + 12);
                for (int s = 1; s <= 20; s++) pts.Add(new Point(a.X + (over.X - a.X) * s / 20, a.Y + (over.Y - a.Y) * s / 20));
            }
            Assert.Equal(w, SwipeTyping.Recognize(pts, k, 52)[0]);
        }
    }

    // nauka ruchow myszy: nietypowy slad uzytkownika (np. omija klawisz) po nauczeniu daje jego slowo
    [Fact]
    public void UczySieRuchowMyszy()
    {
        SwipeTyping.Load(new[] { "dom", "dym", "dam", "kot" });
        var k = Klawisze();
        // "dom" pisany zawsze tak samo krzywo: zamiast przez "o" reka idzie nad "u"
        var mojDom = new List<Point> { k['d'], new Point(k['u'].X, k['u'].Y - 8), k['m'] };
        SwipeTyping.LearnShape("dom", mojDom, 52);
        Assert.Equal("dom", SwipeTyping.Recognize(mojDom, k, 52)[0]);
        SwipeTyping.ForgetShape("dom");
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
        var words = rd.ReadToEnd().Split('\n').Select(x => x.Trim()).ToArray();   // na Windows linie koncza sie \r\n
        Assert.Contains("tak", words);
        Assert.DoesNotContain("taak", words);
        Assert.DoesNotContain("taaak", words);
    }

    [Fact]
    public void AutokorektaPolskieZnakiILiterowka()
    {
        SwipeTyping.Load(new[] { "nie", "jest", "żółw", "który", "dziękuję", "komputer", "dom", "pisze", "piszę" });
        Assert.Equal("żółw", SwipeTyping.Correct("zolw"));
        Assert.Equal("Który", SwipeTyping.Correct("Ktory"));
        Assert.Equal("dziękuję", SwipeTyping.Correct("dziekuje"));
        Assert.Equal("komputer", SwipeTyping.Correct("komputr"));   // brakujaca litera
        Assert.Null(SwipeTyping.Correct("pisze"));                   // znane slowo - bez zmian
        Assert.Null(SwipeTyping.Correct("dom"));
        Assert.Null(SwipeTyping.Correct("xyzq"));                    // nic podobnego - bez zmian
        Assert.Null(SwipeTyping.Correct("dm"));                      // krotkich nie zgadujemy
        Assert.Equal("żółw", SwipeTyping.Correct("zolw", null, true));  // rozpoznanie literowki (dawne literowki usuwane z Mojego slownika przy otwarciu)
        SwipeTyping.AddUserWord("zolw");                             // slowo dodane do Mojego slownika - zostaje jak jest
        Assert.Null(SwipeTyping.Correct("zolw"));
        Assert.Null(SwipeTyping.Correct("zolw", new System.Collections.Generic.HashSet<string> { "zolw" }));   // poprawka cofnieta - zostaje
    }
}
