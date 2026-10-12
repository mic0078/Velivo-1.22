using System.IO;
using Przegladarka;
using Xunit;

// Napisy z OpenSubtitles: skrot pliku (algorytm OpenSubtitles) i nazwa do wyszukania.
public class OpenSubtitlesTests
{
    [Fact]
    public void NazwaDoWyszukania()
    {
        Assert.Equal("alien 1979", MainWindow.OsQuery("Alien.1979.PL.1080p.BDRip.DD.2.0.x264-MR"));
        Assert.Equal("28 years later the bone temple 2026", MainWindow.OsQuery("28.Years.Later.The.Bone.Temple.2026.PL.480p.WEB-DL.DD5.1.XviD-P2P"));
        Assert.Equal("amulet 2020", MainWindow.OsQuery("Amulet (2020) PL"));
        Assert.Equal("2001 a space odyssey 1968", MainWindow.OsQuery("2001.A.Space.Odyssey.1968.2160p.4K.BluRay"));
    }

    [Fact]
    public void SkrotPliku()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(f, new byte[200000]);   // same zera: skrot = rozmiar
            Assert.Equal(200000L.ToString("x16"), MainWindow.OsHash(f));
            File.WriteAllBytes(f, new byte[1000]);
            Assert.Null(MainWindow.OsHash(f));   // za maly plik
        }
        finally { File.Delete(f); }
    }
}
