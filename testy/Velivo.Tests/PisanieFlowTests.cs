using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Velivo;
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
    public void PisowniaNieIStalychZwrotow()
    {
        SwipeTyping.Load(new[] { "nie", "wiem", "mam", "mogę", "dobry", "niedobry", "pewno", "razie", "naprawdę" });
        Assert.Equal("nie wiem", SwipeTyping.Correct("niewiem"));
        Assert.Equal("nie mam", SwipeTyping.Correct("niemam"));
        Assert.Equal("Nie mogę", SwipeTyping.Correct("Niemoge"));
        Assert.Null(SwipeTyping.Correct("niedobry"));
        Assert.Equal("niedobry", SwipeTyping.JoinNie("nie", "dobry"));
        Assert.Null(SwipeTyping.JoinNie("nie", "wiem"));
        Assert.Equal("na pewno", SwipeTyping.Correct("napewno"));
        Assert.Equal("Na razie", SwipeTyping.Correct("Narazie"));
        Assert.Equal("w ogóle", SwipeTyping.Correct("wogole"));
        Assert.Equal("naprawdę", SwipeTyping.JoinPair("na", "prawdę"));
        Assert.Null(SwipeTyping.JoinPair("na", "pewno"));
        Assert.Equal("Ci", SwipeTyping.Correct("ci"));                 // zwracajac sie do kogos - wielka litera
        Assert.Null(SwipeTyping.Correct("ze"));                        // ze / że rozstrzyga nastepne slowo
        Assert.True(SwipeTyping.IsTypo("napewno"));                  // bledna pisownia nie trafia do Mojego slownika
        SwipeTyping.AddUserWord("napewno");                          // dawniej wyuczona - i tak poprawiana
        Assert.Equal("na pewno", SwipeTyping.Correct("napewno"));
        Assert.False(SwipeTyping.IsMisspelling("dobry"));
    }

    [Fact]
    public void SpacjaWstawiaDokonczenieZPaska()
    {
        SwipeTyping.Load(new[] { "nie", "dom", "komputer", "przepraszam", "dziękuję" });
        Assert.Equal("komputer", SwipeTyping.Complete("komp"));
        Assert.Equal("Przepraszam", SwipeTyping.Complete("Przepr"));
        Assert.Equal("dziękuję", SwipeTyping.Complete("dziekuje"));   // pewna poprawka przed dokonczeniem
        Assert.Null(SwipeTyping.Complete("dom"));                     // poprawne slowo zostaje
        Assert.Null(SwipeTyping.Complete("komp", new[] { "komp" }));  // cofniete - zostaje
        SwipeTyping.AddUserWord("chwalińska");
        Assert.Equal("Chwalińska", SwipeTyping.Complete("Chwalin"));   // Moje slowo (nazwisko) - spacja tez je wstawia
    }

    [Fact]
    public void UczySiePoprawkiPoSkasowaniuSlowa()
    {
        SwipeTyping.Load(new[] { "nie", "kot", "kit", "komputer" });
        Assert.True(SwipeTyping.LearnFix("kmoputer", "komputer"));     // skasowane i wpisane od nowa
        Assert.Equal("komputer", SwipeTyping.Correct("kmoputer"));
        Assert.False(SwipeTyping.LearnFix("kot", "kit"));              // poprawne slowo zmienione na inne - to nie literowka
        Assert.False(SwipeTyping.LearnFix("abcdef", "komputer"));      // zupelnie inne slowo
    }

    [Fact]
    public void PodpowiadaCzestoPisaneZdanie()
    {
        var b = new SwipeTyping.SentenceBuf();
        b.Word("Mam", false, false); b.Word("nadzieję", false, false); b.Word("że", true, false); b.Word("wszystko", false, false); b.Word("dobrze", false, false);
        var t = b.Text(false) + ".";
        Assert.Equal("Mam nadzieję, że wszystko dobrze.", t);
        SwipeTyping.LearnSentence(t);
        Assert.Null(SwipeTyping.SentenceRest("Mam nad"));               // raz napisane - jeszcze nie podpowiadamy
        SwipeTyping.LearnSentence(t);
        Assert.Equal("zieję, że wszystko dobrze.", SwipeTyping.SentenceRest("Mam nad"));
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

    [Fact]
    public void PrzecinekPrzedSpojnikiemISlowaZPolskimiZnakami()
    {
        Assert.True(SwipeTyping.NeedsComma("że", "wiem"));        // wiem, że
        Assert.True(SwipeTyping.NeedsComma("który", "dom"));      // dom, który
        Assert.True(SwipeTyping.NeedsComma("bo", "idę"));
        Assert.False(SwipeTyping.NeedsComma("że", "mimo"));       // mimo że - bez przecinka miedzy
        Assert.False(SwipeTyping.NeedsComma("że", "i"));          // i że
        Assert.False(SwipeTyping.NeedsComma("że", null));         // poczatek zdania
        Assert.False(SwipeTyping.NeedsComma("dom", "wiem"));
        Assert.True(SwipeTyping.NeedsComma("a", "idę"));          // idę, a ty
        Assert.True(SwipeTyping.NeedsComma("gdzie", "wiem"));     // nie wiem, gdzie
        Assert.False(SwipeTyping.NeedsComma("a", "od"));          // od a do z
        Assert.False(SwipeTyping.NeedsComma("gdzie", "tam"));     // tam gdzie
        Assert.Equal(", że", SwipeTyping.ZeAsZe("ze", "to", "wiem"));   // wiem ze to -> wiem, że to
        Assert.Null(SwipeTyping.ZeAsZe("ze", "mną", "idź"));           // ze mną - przyimek
        Assert.Null(SwipeTyping.ZeAsZe("ze", "szkoły", "wracam"));
        Assert.Equal("Że", SwipeTyping.ZeAsZe("Ze", "się", null));
        Assert.Equal("że", SwipeTyping.ZeNow("ze", "Myślę"));          // myślę ze -> myślę, że (od razu po spacji)
        Assert.Null(SwipeTyping.ZeNow("ze", "dom"));                   // dom ze ... - rozstrzyga nastepne slowo
        Assert.Equal("że", SwipeTyping.ZeNow("ze", "powiedzieć"));     // po czasowniku - od razu "że" (ze mną potem wraca)
        Assert.Equal(", że", SwipeTyping.ZeAsZe("ze", "wpisywanie", "powiedzieć"));
        SwipeTyping.Load(new[] { "klawiatura", "żółw" });
        Assert.True(SwipeTyping.IsTypo("zolw"));                  // literowka - nie do Mojego slownika
        Assert.False(SwipeTyping.IsTypo("klawiaturą"));           // z polskimi znakami - swiadomie wpisane slowo
    }

    [Fact]
    public void UczySieZwrotowUzytkownika()
    {
        SwipeTyping.Load(new[] { "daj", "znać", "jak", "dotrzesz" });
        string p2 = null, p1 = null;
        for (int r = 0; r < 3; r++) foreach (var w in new[] { "daj", "znać", "jak", "dotrzesz" }) { SwipeTyping.LearnPair(p1, w); SwipeTyping.LearnTriple(p2, p1, w); p2 = p1; p1 = w; }
        var po1 = SwipeTyping.NextWords("daj");
        Assert.Equal("znać jak dotrzesz", po1[0]);                 // caly zwrot pisany czesto
        Assert.Contains("znać", po1);
        Assert.Equal("jak dotrzesz", SwipeTyping.NextWords("znać", "daj")[0]);   // po dwoch slowach - dalsza czesc zwrotu
    }
}
