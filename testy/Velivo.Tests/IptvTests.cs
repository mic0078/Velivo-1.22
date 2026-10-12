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

    [Fact]
    public void DopasowanieBezDopiskowJakosci()
    {
        Assert.Equal(MainWindow.EpgKey("TVP 1 HD"), MainWindow.EpgKey("|PL| TVP 1 SD"));
        Assert.Equal(MainWindow.EpgKey("Polsat News FHD"), MainWindow.EpgKey("POLSAT NEWS"));
        Assert.NotEqual(MainWindow.EpgKey("TV Puls"), MainWindow.EpgKey("TV Puls 2"));
        var map = new System.Collections.Generic.Dictionary<string, string> { { MainWindow.EpgKey("TVP 1 HD"), "logo" } };
        Assert.Equal("logo", MainWindow.ByChannel(map, new MainWindow.IptvChannel { Name = "TVP 1 SD" }));
    }

    [Fact]
    public void ListaHls()
    {
        var b = new Uri("http://s.example/live/kanal.m3u8");
        var master = MainWindow.ParseHls("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=3000000\nhigh.m3u8\n", b);
        Assert.Equal("http://s.example/live/high.m3u8", master.Variant.AbsoluteUri);   // najlepsza jakosc
        var media = MainWindow.ParseHls("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\nseg1.ts\n#EXTINF:4,\nhttp://cdn.example/seg2.ts\n", b);
        Assert.Null(media.Variant); Assert.Equal(2, media.Segments.Count); Assert.Equal(4, media.Target);
        Assert.Equal("http://s.example/live/seg1.ts", media.Segments[0].AbsoluteUri);
        Assert.True(MainWindow.ParseHls("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\"\nseg.ts\n", b).Encrypted);
    }

    [Fact]
    public void NazwaNagrania()
    {
        var n = MainWindow.RecordingName("TVN24 FHD [PL]", "Fakty: wydanie/specjalne?", new DateTime(2026, 10, 12, 19, 0, 0));
        Assert.EndsWith("2026-10-12 19.00.ts", n);
        Assert.DoesNotContain("/", n); Assert.DoesNotContain("?", n);
    }

    [Fact]
    public async System.Threading.Tasks.Task NagrywanieStrumieniaTs()
    {
        // maly serwer HTTP: strumien MPEG-TS (bajty), potem rozlaczenie
        var srv = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); srv.Start();
        int port = ((System.Net.IPEndPoint)srv.LocalEndpoint).Port;
        var payload = new byte[188 * 100]; for (int i = 0; i < payload.Length; i += 188) payload[i] = 0x47;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            using (var c = await srv.AcceptTcpClientAsync())
            using (var ns = c.GetStream())
            {
                var buf = new byte[4096]; await ns.ReadAsync(buf, 0, buf.Length);
                var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                await ns.WriteAsync(head, 0, head.Length); await ns.WriteAsync(payload, 0, payload.Length);
            }
        });
        var file = Path.Combine(Path.GetTempPath(), "velivo-rec-" + Guid.NewGuid().ToString("N") + ".ts");
        var cts = new System.Threading.CancellationTokenSource();
        var t = MainWindow.RecordStream("http://127.0.0.1:" + port + "/kanal.ts", null, file, DateTime.Now.AddSeconds(20), cts.Token);
        for (int i = 0; i < 100 && (!File.Exists(file) || new FileInfo(file).Length < payload.Length); i++) await System.Threading.Tasks.Task.Delay(100);
        cts.Cancel();   // jak "Zatrzymaj nagrywanie"
        await t;
        srv.Stop();
        Assert.Equal(payload.Length, new FileInfo(file).Length);
        File.Delete(file);
    }
}
