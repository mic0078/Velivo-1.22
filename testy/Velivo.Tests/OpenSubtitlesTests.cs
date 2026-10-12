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

    [Fact]
    public void NapiprojektKontrolny()
    {
        // wartosci wzorcowe z algorytmu subliminal (Python)
        Assert.Equal("e2308", MainWindow.NapiToken("0123456789abcdef0123456789abcdef"));
        Assert.Equal("8030b", MainWindow.NapiToken("d41d8cd98f00b204e9800998ecf8427e"));
    }

    [Fact]
    public void NapiprojektSkrotIMpl2()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(f, new byte[0]);
            Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", MainWindow.NapiHash(f));   // MD5 pustego pliku
        }
        finally { File.Delete(f); }
        var srt = MainWindow.MplToSrt("[10][35]Pierwsza linia|/druga\n[40][]Bez końca\n");
        Assert.Contains("00:00:01,000 --> 00:00:03,500\nPierwsza linia\ndruga", srt);
        Assert.Contains("00:00:04,000 --> 00:00:07,000\nBez końca", srt);
        Assert.Equal("1\n00:00:01,000 --> 00:00:02,000\nx\n", MainWindow.MplToSrt("1\n00:00:01,000 --> 00:00:02,000\nx\n"));   // SRT bez zmian
    }
}
