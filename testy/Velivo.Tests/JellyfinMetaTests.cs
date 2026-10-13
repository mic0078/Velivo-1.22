using System.Text.Json;
using Przegladarka;
using Xunit;

// Informacje o filmach z Jellyfin: dopasowanie po nazwie pliku (rozne sciezki na komputerach) i odczyt danych filmu.
public class JellyfinMetaTests
{
    [Fact]
    public void DopasowaniePoNazwiePliku()
    {
        Assert.Equal(MainWindow.MetaKey(@"D:\Media\Filmy 1\Alien\Alien.1979.PL.1080p.mkv"), MainWindow.MetaKey(@"Y:\MediaJellyfin\Filmy 1\Alien\alien.1979.pl.1080p.MKV"));
        Assert.Equal("film", MainWindow.MetaKey("/media/filmy/Film.mp4"));   // serwer na Linuksie
    }

    [Fact]
    public void DaneFilmuZJellyfin()
    {
        var json = "{\"Id\":\"abc\",\"Name\":\"Obcy - 8. pasażer Nostromo\",\"OriginalTitle\":\"Alien\",\"ProductionYear\":1979,\"CommunityRating\":8.46,\"OfficialRating\":\"R\"," +
            "\"Overview\":\"Załoga statku…\",\"Genres\":[\"Horror\",\"Sci-Fi\"],\"People\":[{\"Name\":\"Ridley Scott\",\"Type\":\"Director\"},{\"Name\":\"Sigourney Weaver\",\"Type\":\"Actor\"}],\"ImageTags\":{\"Primary\":\"t1\"}}";
        var m = MainWindow.ParseJellyfinItem(JsonDocument.Parse(json).RootElement);
        Assert.Equal("Obcy - 8. pasażer Nostromo", m.Name); Assert.Equal("Alien", m.Original); Assert.Equal(1979, m.Year); Assert.Equal(8.5, m.Rating);
        Assert.Equal("Ridley Scott", m.Director); Assert.Equal(new[] { "Sigourney Weaver" }, m.People); Assert.Equal("t1", m.ImageTag);
        Assert.Equal("Obcy - 8. pasażer Nostromo (1979)", MainWindow.MetaTitle(m));
    }

    [Fact]
    public void CzytelnyTytulZNazwyPliku()
    {
        Assert.Equal("Mad Max 3- Pod Kopułą Gromu (1985)", MainWindow.CleanTitle("[ELECTRO-TORRENT] Mad Max 3- Pod Kopułą Gromu 1985 LP r.d11"));
        Assert.Equal("Alien (1979)", MainWindow.CleanTitle("Alien.1979.PL.1080p.BDRip.DD.2.0.x264-MR"));
        Assert.Equal("2001 A Space Odyssey (1968)", MainWindow.CleanTitle("2001.A.Space.Odyssey.1968.2160p.4K.BluRay.x265.10bit.AAC5.1"));
        Assert.Equal("Distant (2024)", MainWindow.CleanTitle("[devil-torrents.pl] Distant (2024) [XviD.720p] Lektor PL [AC3]"));
        Assert.Equal("28 years later", MainWindow.CleanTitle("28 years later-720p"));
        // nierozpoznany w Jellyfin (bez opisu i oceny) - nie jego "tytul" (nazwa pliku)
        Assert.Null(MainWindow.MetaTitle(new MainWindow.MovieInfo { Name = "Alien.1979.PL.1080p", Year = 1979 }));
    }

    [Fact]
    public void DopasowaniePolskichZnakowIFolderu()
    {
        // "ą" zapisane jako litera albo jako "a" + ogonek (NAS) - ten sam film
        Assert.Equal(MainWindow.MetaKey("D:\\Mad Max pod Kopułą Gromu.mkv"), MainWindow.MetaKey("Y:\\Mad Max pod Kopu\u0142a\u0328 Gromu.mkv"));
        // Jellyfin podal folder filmu - plik w tym folderze dopasowany po nazwie folderu
        var meta = new System.Collections.Generic.Dictionary<string, MainWindow.MovieInfo> { { MainWindow.MetaDirKey(@"\\nas\Filmy 3\Mad Max 1985 r.d11" + "\\x"), new MainWindow.MovieInfo { Name = "Mad Max pod Kopułą Gromu" } } };
        Assert.Equal("Mad Max pod Kopułą Gromu", MainWindow.LookupMeta(meta, @"Y:\MediaJellyfin\Filmy 3\Mad Max 1985 r.d11\film.mkv").Name);
    }
}
