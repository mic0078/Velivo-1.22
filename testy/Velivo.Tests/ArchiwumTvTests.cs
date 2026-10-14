using System;
using Przegladarka;
using Xunit;

// archiwum TV (catch-up): odczyt z listy i adresy audycji z przeszlosci w roznych formatach dostawcow (token zmyslony)
public class ArchiwumTvTests
{
    static readonly DateTime Start = new DateTime(2026, 10, 13, 21, 0, 0, DateTimeKind.Local);
    static readonly DateTime End = Start.AddMinutes(100);
    static long U(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

    [Fact]
    public void FlussonicHlsJakULinetv()
    {
        var pl = MainWindow.ParseM3u("#EXTM3U\n#EXTINF:-1 tvg-id=\"tvp1.pl\" group-title=\"PL\" catchup=\"flussonic\" timeshift=\"3\",TVP 1 HD\nhttp://serwer.test:8080/ch2101/index.m3u8?token=ABC\n");
        var c = pl.Channels[0];
        Assert.True(MainWindow.HasCatchup(c));
        Assert.Equal(3, c.CatchupDays);
        Assert.Equal("http://serwer.test:8080/ch2101/index-" + U(Start) + "-6000.m3u8?token=ABC", MainWindow.CatchupUrl(c, Start, End, End.AddHours(1)));
    }

    [Fact]
    public void FlussonicOdmianyAdresu()
    {
        var c = new MainWindow.IptvChannel { Url = "http://s.test/ch1/index.m3u8?token=T", Catchup = "flussonic", CatchupDays = 3 };
        Assert.Equal("http://s.test/ch1/archive-" + U(Start) + "-6000.m3u8?token=T", MainWindow.CatchupUrl(c, Start, End, End, 1));
        Assert.Equal("http://s.test/ch1/timeshift_abs-" + U(Start) + ".m3u8?token=T", MainWindow.CatchupUrl(c, Start, End, End, 2));
        Assert.Null(MainWindow.CatchupUrl(c, Start, End, End, 99));   // kolejne odmiany sie koncza
    }

    [Fact]
    public void FlussonicMpegts()
    {
        var c = new MainWindow.IptvChannel { Url = "http://s.test/kanal/mpegts?token=X", Catchup = "flussonic", CatchupDays = 2 };
        Assert.Equal("http://s.test/kanal/timeshift_abs-" + U(Start) + ".ts?token=X", MainWindow.CatchupUrl(c, Start, End, End));
    }

    [Fact]
    public void XtreamCodes()
    {
        var pl = MainWindow.ParseM3u("#EXTM3U\n#EXTINF:-1 tvg-rec=\"7\",Kanal\nhttp://s.test:80/live/jan/haslo/1234.ts\n");
        var c = pl.Channels[0];
        Assert.Equal("xc", c.Catchup);
        Assert.Equal("http://s.test:80/timeshift/jan/haslo/100/" + Start.ToString("yyyy-MM-dd:HH-mm") + "/1234.ts", MainWindow.CatchupUrl(c, Start, End, End));
    }

    [Fact]
    public void ShiftIDefaultZeWzorem()
    {
        var sh = new MainWindow.IptvChannel { Url = "http://s.test/a.m3u8?k=1", Catchup = "shift", CatchupDays = 3 };
        Assert.Equal("http://s.test/a.m3u8?k=1&utc=" + U(Start) + "&lutc=" + U(End), MainWindow.CatchupUrl(sh, Start, End, End));
        var pl = MainWindow.ParseM3u("#EXTM3U\n#EXTINF:-1 catchup=\"default\" catchup-days=\"5\" catchup-source=\"http://s.test/arch/{utc}/{duration}/{Y}\",K\nhttp://s.test/live\n");
        Assert.Equal("http://s.test/arch/" + U(Start) + "/6000/" + Start.ToUniversalTime().ToString("yyyy"), MainWindow.CatchupUrl(pl.Channels[0], Start, End, End));
    }

    [Fact]
    public void BezArchiwumIZaStareAudycje()
    {
        var pl = MainWindow.ParseM3u("#EXTM3U\n#EXTINF:-1,Zwykly\nhttp://s.test/x.m3u8\n");
        Assert.False(MainWindow.HasCatchup(pl.Channels[0]));
        Assert.Null(MainWindow.CatchupUrl(pl.Channels[0], Start, End, End));
        var c = new MainWindow.IptvChannel { Url = "http://s.test/i.m3u8", Catchup = "flussonic", CatchupDays = 3 };
        var now = Start.AddDays(5);
        Assert.False(MainWindow.InCatchup(c, new MainWindow.EpgItem { Start = Start, Stop = End }, now));   // starsze niz 3 dni
        Assert.True(MainWindow.InCatchup(c, new MainWindow.EpgItem { Start = now.AddHours(-5), Stop = now.AddHours(-4) }, now));
        Assert.False(MainWindow.InCatchup(c, new MainWindow.EpgItem { Start = now.AddHours(1), Stop = now.AddHours(2) }, now));   // przyszla
    }
}
