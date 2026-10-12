using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Przegladarka;
using Xunit;

public class IptvTests
{
    const string M3u = "#EXTM3U url-tvg=\"http://epg.example/guide.xml.gz\"\n" +
        "#EXTINF:-1 tvg-id=\"tvp1.pl\" tvg-logo=\"http://l/1.png\" group-title=\"Polska, ogólne\",TVP 1 HD\n" +
        "#EXTVLCOPT:http-user-agent=Mozilla/5.0\n" +
        "#EXTVLCOPT:network-caching=1000\n" +
        "http://s.example/tvp1.m3u8\n" +
        "#EXTINF:-1,Bez atrybutów\n" +
        "#EXTGRP:Sport\n" +
        "udp://@239.0.0.1:1234\n" +
        "#EXTINF:-1,Zly wpis\n" +
        "nie-adres\n";

    [Fact]
    public void ListaM3u()
    {
        var pl = MainWindow.ParseM3u(M3u);
        Assert.Equal("http://epg.example/guide.xml.gz", pl.EpgUrl);
        Assert.Equal(2, pl.Channels.Count);   // wpis bez adresu pominiety
        var a = pl.Channels[0];
        Assert.Equal("TVP 1 HD", a.Name); Assert.Equal("Polska, ogólne", a.Group); Assert.Equal("tvp1.pl", a.TvgId); Assert.Equal("http://l/1.png", a.Logo);
        Assert.Equal(new[] { ":http-user-agent=Mozilla/5.0" }, a.Opts);   // tylko naglowki - inne opcje VLC z obcej listy pomijane
        Assert.Equal("Sport", pl.Channels[1].Group);
        Assert.Equal("udp://@239.0.0.1:1234", pl.Channels[1].Url);
    }

    [Fact]
    public void CzasXmltv()
    {
        var t = MainWindow.ParseXmltvTime("20261012083000 +0200");
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 6, 30, 0, TimeSpan.Zero).LocalDateTime, t);
        Assert.Null(MainWindow.ParseXmltvTime("bzdura"));
    }

    [Fact]
    public void ProgramTvTakzeGz()
    {
        var now = DateTime.UtcNow;
        string T(DateTime d) => d.ToString("yyyyMMddHHmmss") + " +0000";
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE tv SYSTEM \"xmltv.dtd\"><tv>" +
            "<channel id=\"tvp1.pl\"><display-name>TVP 1 HD</display-name></channel>" +
            $"<programme start=\"{T(now.AddHours(-1))}\" stop=\"{T(now.AddHours(1))}\" channel=\"tvp1.pl\"><title>Wiadomości</title><desc>Opis</desc></programme>" +
            $"<programme start=\"{T(now.AddHours(1))}\" stop=\"{T(now.AddHours(2))}\" channel=\"tvp1.pl\"><title>Film</title></programme>" +
            $"<programme start=\"{T(now.AddDays(-5))}\" stop=\"{T(now.AddDays(-5).AddHours(1))}\" channel=\"tvp1.pl\"><title>Stary</title></programme>" +
            "</tv>";
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) { var b = Encoding.UTF8.GetBytes(xml); gz.Write(b, 0, b.Length); }
        ms.Position = 0;
        var epg = MainWindow.ParseXmltv(ms, DateTime.Now.AddHours(-6), DateTime.Now.AddHours(30));
        var ch = new MainWindow.IptvChannel { Name = "TVP 1 HD" };   // bez tvg-id - po nazwie
        var l = MainWindow.EpgFor(epg, ch);
        Assert.Equal(2, l.Count);   // stary program poza oknem czasu
        Assert.Equal("Wiadomości", MainWindow.EpgAt(l, DateTime.Now).Title);
        var plain = MainWindow.ParseXmltv(new MemoryStream(Encoding.UTF8.GetBytes(xml)), DateTime.Now.AddHours(-6), DateTime.Now.AddHours(30));
        Assert.Equal(2, plain["tvp1.pl"].Count);
    }
}
