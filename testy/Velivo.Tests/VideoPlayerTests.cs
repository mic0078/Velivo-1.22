using Przegladarka;
using Xunit;

public class VideoPlayerTests
{
    [Fact]
    public void Film_z_dysku_trafia_do_odtwarzacza()
    {
        Assert.Equal(@"D:\Filmy\wakacje.mp4", MainWindow.VideoArg("file:///D:/Filmy/wakacje.mp4"));   // tak przychodzi po ArgToUrl
        Assert.Equal(@"D:\Filmy\Serial S01E01.MKV", MainWindow.VideoArg("file:///D:/Filmy/Serial%20S01E01.MKV"));
        Assert.Null(MainWindow.VideoArg("https://example.com/film.mp4"));   // film z sieci - zwykla karta
        Assert.Null(MainWindow.VideoArg("file:///D:/Dokumenty/umowa.pdf"));
        Assert.Null(MainWindow.VideoArg("velivo.pl"));
    }

    [Fact]
    public void Adres_odtwarzacza_niesie_ustawienia_i_film()
    {
        var player = @"C:\Dane\odtwarzacz.html";
        var url = MainWindow.PlayerUrlFor(player, @"D:\Filmy\a b#1.mp4", true, false, true, "S1");
        Assert.Equal("file:///C:/Dane/odtwarzacz.html#a=1&r=0&l=1&s=S1&v=file%3A%2F%2F%2FD%3A%2FFilmy%2Fa%2520b%25231.mp4", url);
        Assert.True(MainWindow.IsPlayerUrl(player, url));   // "Film na wierzchu" przyjmuje tylko ten odtwarzacz
        Assert.False(MainWindow.IsPlayerUrl(player, "file:///C:/Dane/inny.html#a=1"));
        Assert.False(MainWindow.IsPlayerUrl(player, "file:///C:/Dane/odtwarzacz.html.evil"));
    }
}
