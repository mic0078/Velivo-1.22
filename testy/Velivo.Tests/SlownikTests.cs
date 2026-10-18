using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Velivo;
using Xunit;

// Slownik PL -> EN (Lang.cs): bez powtorzen i z tlumaczeniem kazdego napisu L.T("...") w programie.
public class SlownikTests
{
    [Fact]
    public void BezPowtorzen()
    {
        Assert.True(L.DuplicateKeys.Count == 0, "Powtorzone wpisy w Lang.cs: " + string.Join(" | ", L.DuplicateKeys));
    }

    [Fact]
    public void KazdyNapisMaTlumaczenie()
    {
        var src = ZnajdzSrc();
        var brak = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(src, "*.cs"))
        {
            if (Path.GetFileName(file) == "Lang.cs") continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\bL\.T\(""((?:[^""\\]|\\.)*)""\)"))
            {
                var pl = Regex.Unescape(m.Groups[1].Value);
                if (!L.Has(pl)) brak.Add(Path.GetFileName(file) + ": " + pl);
            }
        }
        Assert.True(brak.Count == 0, "Brak tlumaczenia EN (" + brak.Count + "):\n" + string.Join("\n", brak.Take(40)));
    }

    static string ZnajdzSrc()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var src = Path.Combine(d.FullName, "src");
            if (File.Exists(Path.Combine(src, "Lang.cs"))) return src;
        }
        throw new DirectoryNotFoundException("Nie znaleziono folderu src z Lang.cs");
    }
}
