using System.Collections.Generic;
using System.IO;
using Przegladarka;
using Xunit;

// Biblioteka filmow (filmy.json) i wznawianie - logika bez biblioteki VLC.
public class VideoLibraryTests
{
    [Fact]
    public void DodawaniePlikowIFolderuBezDuplikatow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "velivo-filmy-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sezon 1"));
        foreach (var f in new[] { "a.avi", "b.mkv", "notatka.txt", Path.Combine("sezon 1", "c.wmv") }) File.WriteAllText(Path.Combine(dir, f), "x");
        try
        {
            var lib = new List<MainWindow.LibraryVideo>();
            Assert.Equal(3, MainWindow.AddToLibrary(lib, new[] { dir }));             // filmy z podfolderami, bez .txt
            Assert.Equal(0, MainWindow.AddToLibrary(lib, new[] { Path.Combine(dir, "a.avi") }));   // juz jest
            Assert.Contains(lib, v => v.Title == "c");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Wznawianie()
    {
        Assert.Equal(0, MainWindow.ResumeAt(5000, 600000));        // zaledwie zaczety - od poczatku
        Assert.Equal(120000, MainWindow.ResumeAt(120000, 600000));
        Assert.Equal(0, MainWindow.ResumeAt(595000, 600000));      // prawie koniec - od poczatku
        Assert.Equal("1:02:03", MainWindow.FormatTime(3723000));
        Assert.Equal("4:05", MainWindow.FormatTime(245000));
    }

    [Fact]
    public void ZepsutyPlikBiblioteki()
    {
        Assert.Empty(MainWindow.ParseLibrary("{"));
        Assert.Single(MainWindow.ParseLibrary("[{\"Path\":\"C:\\\\f.avi\",\"Title\":\"f\"},{\"Title\":\"bez sciezki\"}]"));
    }

    [Fact]
    public void SortowanieIOstatnio()
    {
        var lib = new List<MainWindow.LibraryVideo>
        {
            new MainWindow.LibraryVideo { Path = @"C:\b\Zorro.avi", Title = "Zorro", Len = 100, Last = 300 },
            new MainWindow.LibraryVideo { Path = @"C:\a\Alfa.mkv", Title = "alfa", Len = 900, Last = 0 },
            new MainWindow.LibraryVideo { Path = @"C:\c\Beta.avi", Title = "Beta", Len = 500, Last = 700 },
        };
        string Order(string key, bool desc) => string.Join(",", System.Linq.Enumerable.Select(MainWindow.SortLibrary(lib, key, desc), v => v.Title));
        Assert.Equal("alfa,Beta,Zorro", Order("title", false));      // bez znaczenia wielkosc liter
        Assert.Equal("Zorro,Beta,alfa", Order("title", true));
        Assert.Equal("alfa,Beta,Zorro", Order("len", true));          // najdluzszy pierwszy
        Assert.Equal("Beta,Zorro,alfa", Order("last", true));         // ostatnio odtwarzany pierwszy, nieogladany na koncu
        var now = new System.DateTime(2026, 10, 12, 20, 0, 0);
        Assert.Equal("", MainWindow.LastPlayedText(0, now));
        Assert.Equal("03.05.2025", MainWindow.LastPlayedText(new System.DateTimeOffset(new System.DateTime(2025, 5, 3, 12, 0, 0)).ToUnixTimeMilliseconds(), now));
        Assert.EndsWith("18:30", MainWindow.LastPlayedText(new System.DateTimeOffset(new System.DateTime(2026, 10, 12, 18, 30, 0)).ToUnixTimeMilliseconds(), now));
    }
}
