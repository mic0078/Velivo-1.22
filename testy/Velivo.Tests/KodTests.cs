using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

// Czystość kodu: instrukcja wstawiona za komentarzem „//” w tej samej linii nie wykonuje się, a kompilator milczy
// (zdarzyło się 6 razy: kafelki Audio Monitora, klawiatura ekranowa, karteczki, miniatury…). Test blokuje budowę.
public class KodTests
{
    // przypisanie albo wywołanie metody na końcu komentarza - wygląda jak kod, nie jak zdanie
    static readonly Regex Instrukcja = new Regex(
        @"(?:^|[;}]\s+|\s)(?:[A-Za-z_]\w*\.)*[A-Za-z_]\w*(?:\[[^\]]*\])?\s*(?:=|\+=|-=)\s*(?<rhs>[^;=]*\S);\s*$" +
        @"|(?:^|\s)(?:[A-Za-z_]\w*\.)+[A-Z]\w*\((?<rhs>[^;]*)\);\s*$");

    internal static bool KodZaKomentarzem(string linia)
    {
        int p = PoczatekKomentarza(linia);
        if (p < 0) return false;
        var t = linia.Substring(p + 2).TrimEnd();
        if (t.Contains("http")) return false;
        var m = Instrukcja.Match(t);
        if (!m.Success) return false;
        var rhs = m.Groups["rhs"].Value; int b = 0;   // zdanie w komentarzu zwykle ma niedomknięty nawias „(… = 0;”
        foreach (var c in rhs) { if (c == '(') b++; else if (c == ')' && --b < 0) return false; }
        return b == 0;
    }

    // „//” poza napisem w cudzysłowie
    static int PoczatekKomentarza(string l)
    {
        bool w = false;
        for (int i = 0; i < l.Length; i++)
        {
            char c = l[i];
            if (w) { if (c == '\\') i++; else if (c == '"') w = false; }
            else if (c == '"') w = true;
            else if (c == '/' && i + 1 < l.Length && l[i + 1] == '/') return i;
        }
        return -1;
    }

    [Theory]
    [InlineData("var a = 1;   // opis bx.Child = sp; Grid.SetColumn(bx, i); sum3.Children.Add(bx);", true)]
    [InlineData("cur = \"\";   // caly zwrot - kazde slowo lastSwipe = null; fixFrom = fixTo = null; lastSent = ' ';", true)]
    [InlineData("x();   // ikona (emoji) rep.Margin = new Thickness(6, 0, 0, 0);", true)]
    [InlineData("x();   // meta tylko gdy CSS = normal);", false)]
    [InlineData("x();   // skala -20..+3 VU (0 VU = -18 dBFS RMS), czerwone pole powyzej 0;", false)]
    [InlineData("var u = \"http://a//b\"; y();", false)]
    [InlineData("y();   // zwykły opis bez kodu", false)]
    public void Wykrywanie(string linia, bool kod) => Assert.Equal(kod, KodZaKomentarzem(linia));

    [Fact]
    public void BezKoduZaKomentarzem()
    {
        var root = ZnajdzRepo();
        var zle = new List<string>();
        foreach (var dir in new[] { "src", "komunikator-windows" })
        {
            var d = Path.Combine(root, dir); if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.GetFiles(d, "*.cs"))
            {
                int n = 0;
                foreach (var l in File.ReadLines(f)) { n++; if (KodZaKomentarzem(l)) zle.Add(Path.GetFileName(f) + ":" + n); }
            }
        }
        Assert.True(zle.Count == 0, "Kod za komentarzem „//” (nie wykona się) – przenieś przed komentarz:\n" + string.Join("\n", zle.Take(40)));
    }

    static string ZnajdzRepo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "Lang.cs"))) return d.FullName;
        throw new DirectoryNotFoundException("Nie znaleziono folderu src z Lang.cs");
    }
}
