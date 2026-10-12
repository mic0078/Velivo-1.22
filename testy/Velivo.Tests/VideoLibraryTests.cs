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
}
