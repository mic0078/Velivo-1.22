using Velivo;
using Xunit;

// Filmy w sieci lokalnej: zakresy bajtow (przewijanie strumienia), identyfikator filmu i przekazywanie ustawien filmu.
public class LanVideoTests
{
    [Fact]
    public void ZakresyBajtowDoPrzewijania()
    {
        Assert.Equal((100L, 199L), MainWindow.ParseRange("bytes=100-199", 1000));
        Assert.Equal((500L, 999L), MainWindow.ParseRange("bytes=500-", 1000));       // do konca
        Assert.Equal((900L, 999L), MainWindow.ParseRange("bytes=-100", 1000));       // ostatnie 100 bajtow
        Assert.Equal((0L, 999L), MainWindow.ParseRange("bytes=0-5000", 1000));       // koniec za plikiem - przyciety
        Assert.Null(MainWindow.ParseRange("bytes=2000-", 1000));                     // poza plikiem
        Assert.Null(MainWindow.ParseRange(null, 1000));
        Assert.Null(MainWindow.ParseRange("items=1-2", 1000));
    }

    [Fact]
    public void IdFilmuStaleIBezRozroznianiaWielkosciLiter()
    {
        Assert.Equal(MainWindow.FilmId(@"C:\Filmy\A.mkv"), MainWindow.FilmId(@"c:\filmy\a.mkv"));
        Assert.NotEqual(MainWindow.FilmId(@"C:\Filmy\A.mkv"), MainWindow.FilmId(@"C:\Filmy\B.mkv"));
        Assert.Equal(16, MainWindow.FilmId("x").Length);
    }

    [Fact]
    public void UstawieniaFilmuPrzechodzaNaDrugiKomputerIWracaja()
    {
        var owner = new MainWindow.LibraryVideo { Path = @"C:\f.mkv", Title = "f", Vol = 70, LVoice = "piper:pl_PL-gosia-medium", Spu = "Napisy - [Polski]" };
        var sent = MainWindow.PrefsOf(owner);
        Assert.Null(sent.Path);                                  // sciezka nie wychodzi z komputera
        Assert.Equal(70, sent.Vol); Assert.Equal("Napisy - [Polski]", sent.Spu);
        var back = new MainWindow.LibraryVideo { Vol = 40, LRate = 1.5 };   // zmienione na drugim komputerze
        MainWindow.ApplyPrefs(owner, back);
        Assert.Equal(40, owner.Vol); Assert.Equal(1.5, owner.LRate);
        Assert.Equal("piper:pl_PL-gosia-medium", owner.LVoice);  // nieustawione tam - zostaje
    }

    [Fact]
    public void BibliotekaWKilkuPaczkach()
    {
        // 582 filmy z dlugimi nazwami (jak na dyskach USB) - kiedys przychodzila tylko pierwsza paczka (232)
        var shared = new System.Collections.Generic.List<MainWindow.LibraryVideo>();
        for (int i = 0; i < 582; i++) shared.Add(new MainWindow.LibraryVideo { Path = @"E:\Filmy\[ELECTRO-TORRENT.d11] Bardzo dlugi tytul filmu " + i + @" 1080p BRRip H264 Lektor PL\film" + i + ".mkv", Title = "[ELECTRO-TORRENT.d11] Bardzo dlugi tytul filmu " + i });
        int off = 0, pages = 0; bool more;
        var got = new System.Collections.Generic.HashSet<string>();
        do
        {
            var page = MainWindow.LibraryPage(shared, off, out more);
            Assert.NotEmpty(page);
            foreach (var v in page) Assert.True(got.Add(v.Path));   // bez powtorzen
            off += page.Count; pages++;
        } while (more && pages < 60);
        Assert.Equal(582, got.Count);   // cala biblioteka
        Assert.True(pages > 1);
    }
}
