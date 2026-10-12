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
}
