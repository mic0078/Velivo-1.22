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

    [Fact]
    public void ProcentObejrzanegoPoStopie()
    {
        var v = new MainWindow.LibraryVideo { Path = @"C:\f.avi", Title = "f" };
        MainWindow.RecordProgress(v, 1800000, 6000000, 1);      // przerwany w 30% (Stop)
        Assert.Equal("30%", MainWindow.SeenText(v));
        Assert.False(v.Done);
        MainWindow.RecordProgress(v, 3000, 6000000, 2);         // zaledwie zaczety od nowa - nie "obejrzany"
        Assert.Equal("", MainWindow.SeenText(v));
        MainWindow.RecordProgress(v, 5990000, 6000000, 3);      // ostatnie sekundy = obejrzany
        Assert.Equal("✓", MainWindow.SeenText(v));
        MainWindow.RecordProgress(v, 2000, 6000000, 4);         // ponowne ogladanie od poczatku - dalej obejrzany
        Assert.Equal("✓", MainWindow.SeenText(v));
        MainWindow.RecordProgress(v, 1200000, 6000000, 5);      // ponowne ogladanie przerwane w 20%
        Assert.Equal("20%", MainWindow.SeenText(v));
    }

    [Fact]
    public void UstawieniaFilmuZapisaneOsobno()
    {
        var json = "[{\"Path\":\"C:\\\\a.mkv\",\"Title\":\"a\",\"Vol\":60,\"LVol\":0.8,\"LRate\":1.5,\"LDuck\":100,\"LVoice\":\"piper:pl_PL-gosia-medium\"},{\"Path\":\"C:\\\\b.mkv\",\"Title\":\"b\"}]";
        var l = MainWindow.ParseLibrary(json);
        Assert.Equal(60, l[0].Vol); Assert.Equal(1.5, l[0].LRate); Assert.Equal(100, l[0].LDuck); Assert.Equal("piper:pl_PL-gosia-medium", l[0].LVoice);
        Assert.Null(l[1].Vol);   // film bez wlasnych ustawien - odtwarzacz bierze ostatnio uzywane
        Assert.Null(l[1].LVoice);
    }

    [Fact]
    public void DwieBiblioteki()
    {
        var dir = Path.Combine(Path.GetTempPath(), "velivo-libs-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.mkv"), "x"); File.WriteAllText(Path.Combine(dir, "b.mkv"), "x");
        var lib = new System.Collections.Generic.List<MainWindow.LibraryVideo>();
        MainWindow.AddToLibrary(lib, new[] { Path.Combine(dir, "a.mkv") });         // dysk tego komputera - prywatna
        MainWindow.AddToLibrary(lib, new[] { Path.Combine(dir, "b.mkv") }, true);   // dodany we wspolnej
        Assert.False(MainWindow.IsShared(lib[0]));
        Assert.True(MainWindow.IsShared(lib[1]));
        Assert.True(MainWindow.IsShared(new MainWindow.LibraryVideo { Path = @"\\nas\Filmy\c.mkv", Title = "c" }));   // NAS - wspolna
        Assert.False(MainWindow.IsShared(new MainWindow.LibraryVideo { Path = @"\\nas\Filmy\d.mkv", Title = "d", Shared = false }));   // przeniesiony do prywatnej
        var back = MainWindow.ParseLibrary(System.Text.Json.JsonSerializer.Serialize(lib));
        Assert.Null(back[0].Shared); Assert.True(back[1].Shared);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void KopiaWDrugiejBibliotece()
    {
        var lib = new System.Collections.Generic.List<MainWindow.LibraryVideo>
        {
            new MainWindow.LibraryVideo { Path = @"C:\f.mkv", Title = "f", Pos = 5000, Shared = true },
            new MainWindow.LibraryVideo { Path = @"C:\f.mkv", Title = "f", Pos = 0, Shared = false },
        };
        Assert.Equal(5000, MainWindow.FindEntry(lib, @"c:\F.mkv", true).Pos);   // kazda biblioteka - wlasny licznik
        Assert.Equal(0, MainWindow.FindEntry(lib, @"C:\f.mkv", false).Pos);
    }

    [Fact]
    public void WznowienieZNajnowszegoWpisu()
    {
        // film w obu bibliotekach: ogladany z kolejki (wpis wspolny), potem wlaczony z biblioteki prywatnej - ma zapytac o wznowienie
        var lib = new System.Collections.Generic.List<MainWindow.LibraryVideo>
        {
            new MainWindow.LibraryVideo { Path = @"C:.mkv", Title = "f", Pos = 0, Last = 100, Shared = false },
            new MainWindow.LibraryVideo { Path = @"C:.mkv", Title = "f", Pos = 1800000, Len = 6000000, Last = 200, Shared = true },
        };
        var cur = MainWindow.FindEntry(lib, @"C:.mkv", false);
        MainWindow.TakeNewestProgress(lib, cur);
        Assert.Equal(1800000, MainWindow.ResumeAt(cur.Pos, cur.Len));
        Assert.False(cur.Shared ?? true);   // licznik nadal w swojej bibliotece
    }

    [Fact]
    public void OknaKaskadowo()
    {
        var work = new System.Windows.Rect(0, 0, 1920, 1040);
        var a = new System.Windows.Rect(100, 100, 400, 300);
        var p = Przegladarka.App.CascadeSpot(a, new[] { a, new System.Windows.Rect(130, 130, 400, 300) }, work);
        Assert.Equal(new System.Windows.Point(160, 160), p);   // trzecie okno - po skosie dalej
        Assert.Equal(new System.Windows.Point(100, 100), Przegladarka.App.CascadeSpot(a, new[] { new System.Windows.Rect(100, 100, 800, 600) }, work));   // inny rozmiar - bez zmian
    }
}
