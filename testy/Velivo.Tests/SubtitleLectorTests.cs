using System.IO;
using System.Linq;
using System.Text;
using Przegladarka;
using Xunit;

// Lektor w odtwarzaczu filmow: odczyt napisow (SRT, VTT, ASS, polskie znaki w Windows-1250), kwestia w danej chwili,
// jezyk napisow, napisy obok filmu i pamietanie wybranej sciezki napisow / dzwieku.
public class SubtitleLectorTests
{
    const string Srt = "1\r\n00:00:01,500 --> 00:00:03,000\r\n<i>Cześć,</i> jak się masz?\r\n\r\n2\r\n00:00:04,000 --> 00:00:06,250\r\n- Dobrze.\r\n- A ty?\r\n\r\n";

    [Fact]
    public void OdczytSrt()
    {
        var c = MainWindow.ParseSubtitles(Srt);
        Assert.Equal(2, c.Count);
        Assert.Equal(1500, c[0].Start); Assert.Equal(3000, c[0].End);
        Assert.Equal("Cześć, jak się masz?", c[0].Text);      // bez znacznikow <i>
        Assert.Equal("Dobrze. A ty?", c[1].Text);              // bez myslnikow dialogu
    }

    [Fact]
    public void VttIAss()
    {
        var v = MainWindow.ParseSubtitles("WEBVTT\n\n00:05.000 --> 00:07.500\nHello there\n\n01:00:00.000 --> 01:00:02.000\nBye\n");
        Assert.Equal(new long[] { 5000, 3600000 }, v.Select(x => x.Start));
        var a = MainWindow.ParseSubtitles("[Events]\nDialogue: 0,0:00:02.50,0:00:04.00,Default,,0,0,0,,{\\i1}Tak,\\Nale nie teraz\n");
        Assert.Single(a);
        Assert.Equal(2500, a[0].Start); Assert.Equal("Tak, ale nie teraz", a[0].Text);
    }

    [Fact]
    public void PolskieZnakiWindows1250()
    {
        var f = Path.GetTempFileName();
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            File.WriteAllBytes(f, Encoding.GetEncoding(1250).GetBytes(Srt));
            Assert.Contains("Cześć, jak się masz?", MainWindow.ReadSubtitleFile(f));
            File.WriteAllText(f, Srt, new UTF8Encoding(true));
            Assert.StartsWith("1", MainWindow.ReadSubtitleFile(f));   // UTF-8 z BOM tez
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void KwestiaWDanejChwiliIJezyk()
    {
        var c = MainWindow.ParseSubtitles(Srt);
        Assert.Equal(-1, MainWindow.CueAt(c, 500));
        Assert.Equal(0, MainWindow.CueAt(c, 2000));
        Assert.Equal(-1, MainWindow.CueAt(c, 3500));   // przerwa miedzy kwestiami
        Assert.Equal(1, MainWindow.CueAt(c, 6000));
        Assert.Equal("pl", MainWindow.SubtitleLanguage(c));
        Assert.Equal("en", MainWindow.SubtitleLanguage(MainWindow.ParseSubtitles("1\n00:00:01,000 --> 00:00:02,000\nWhat is that? You know it is the end.\n")));
    }

    [Fact]
    public void NapisyObokFilmuNajpierwPolskie()
    {
        var d = Path.Combine(Path.GetTempPath(), "velivo-napisy-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        try
        {
            foreach (var n in new[] { "Film.mkv", "Film.en.srt", "Film.pl.srt", "Inny.srt" }) File.WriteAllText(Path.Combine(d, n), "x");
            Assert.Equal("Film.pl.srt", Path.GetFileName(MainWindow.FindSubtitleFor(Path.Combine(d, "Film.mkv"))));
            Assert.Null(MainWindow.FindSubtitleFor(Path.Combine(d, "Brak.mkv")));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void PamietanieSciezki()
    {
        var tracks = new[] { (-1, "Wyłącz"), (2, "Ścieżka 1 - [English]"), (3, "Ścieżka 2 - [Polski]") };
        Assert.Equal(3, MainWindow.PickTrack(tracks, "Ścieżka 2 - [Polski]"));        // ta sama nazwa
        Assert.Equal(3, MainWindow.PickTrack(tracks, "Napisy 5 - [Polski]"));         // inny film, ten sam jezyk
        Assert.Equal(-1, MainWindow.PickTrack(tracks, "-"));                          // wylaczone
        Assert.Equal(int.MinValue, MainWindow.PickTrack(tracks, "Track 1 - [Deutsch]")); // brak - zostaje domyslna
        Assert.Equal(int.MinValue, MainWindow.PickTrack(tracks, ""));
    }
}
