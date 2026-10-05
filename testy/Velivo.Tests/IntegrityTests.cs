using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Przegladarka;
using Xunit;

// Testy wywoluja prawdziwy kod Velivo (klasa Integrity z Velivo.exe), nie kopie.
public class MarkOfTheWebTests
{
    static string TempFile(string content = "test")
    {
        var f = Path.Combine(Path.GetTempPath(), "velivo-motw-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(f, content);
        return f;
    }

    [Fact]
    public void Pobrany_plik_dostaje_strefe_Internet()
    {
        var f = TempFile();
        Assert.True(Integrity.MarkFromInternet(f));
        var zone = File.ReadAllText(f + ":Zone.Identifier");
        Assert.Contains("[ZoneTransfer]", zone);
        Assert.Contains("ZoneId=3", zone);
        Assert.Contains("HostUrl=about:internet", zone);   // bez adresu strony (prywatnosc)
        Assert.Equal("test", File.ReadAllText(f));          // tresc pliku nietknieta
    }

    [Fact]
    public void Istniejacy_znacznik_nie_jest_nadpisywany()
    {
        var f = TempFile();
        File.WriteAllText(f + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/x.exe\r\n");
        Assert.True(Integrity.MarkFromInternet(f));
        Assert.Contains("https://example.com/x.exe", File.ReadAllText(f + ":Zone.Identifier"));
    }

    [Fact]
    public void Brak_pliku_nie_wywraca_programu()
    {
        Assert.False(Integrity.MarkFromInternet(Path.Combine(Path.GetTempPath(), "nie-ma-" + Guid.NewGuid() + ".exe")));
        Assert.False(Integrity.MarkFromInternet(null));
    }
}

public class IntegrityTests
{
    sealed class FakeHandler : HttpMessageHandler
    {
        readonly string _body;
        public FakeHandler(string body) { _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
    }

    static string Tool(byte[] data)
    {
        var f = Path.Combine(Path.GetTempPath(), "velivo-tool-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(f, data);
        return f;
    }

    [Fact]
    public void Odczyt_pliku_sum_w_formacie_sha256sum()
    {
        var sums = "1fa6733c37ea6fb51c99ad8fe785e7b7e5f3246c9b980230329d4fb72ed8d4d6  yt-dlp\n" +
                   "66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a  yt-dlp.exe\n" +
                   "8f57909e8d2259841708965de5c45da09cdfadbc58a77f19de023a0cc8140af1 *ffmpeg.zip\n";
        Assert.Equal("66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a", Integrity.FindInSums(sums, "yt-dlp.exe"));
        Assert.Equal("8f57909e8d2259841708965de5c45da09cdfadbc58a77f19de023a0cc8140af1", Integrity.FindInSums(sums, "ffmpeg.zip"));
        Assert.Null(Integrity.FindInSums(sums, "yt-dlp.ex"));   // nazwa musi pasowac dokladnie
        Assert.Null(Integrity.FindInSums("", "yt-dlp.exe"));
    }

    [Fact]
    public async Task Poprawny_plik_przechodzi_weryfikacje()
    {
        var data = Encoding.UTF8.GetBytes("prawdziwy program");
        var f = Tool(data);
        var sums = Integrity.Sha256File(f) + "  yt-dlp.exe\n";
        await Integrity.VerifyAgainstSumsAsync(new HttpClient(new FakeHandler(sums)), "https://x/SUMS", "yt-dlp.exe", f);
    }

    [Fact]
    public async Task Podmieniony_plik_jest_odrzucany()
    {
        var f = Tool(Encoding.UTF8.GetBytes("prawdziwy program"));
        var sums = Integrity.Sha256File(f) + "  yt-dlp.exe\n";
        File.WriteAllBytes(f, Encoding.UTF8.GetBytes("podmieniony program"));   // atak: inna zawartosc
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Integrity.VerifyAgainstSumsAsync(new HttpClient(new FakeHandler(sums)), "https://x/SUMS", "yt-dlp.exe", f));
    }

    [Fact]
    public async Task Brak_sumy_dla_pliku_jest_odrzucany()
    {
        var f = Tool(Encoding.UTF8.GetBytes("x"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Integrity.VerifyAgainstSumsAsync(new HttpClient(new FakeHandler("abc  inny.exe\n")), "https://x/SUMS", "yt-dlp.exe", f));
    }

    [Fact]
    public void Digest_z_GitHuba_w_formacie_sha256_dwukropek()
    {
        var f = Tool(Encoding.UTF8.GetBytes("ubol"));
        Assert.True(Integrity.Matches(f, "sha256:" + Integrity.Sha256File(f)));
        Assert.False(Integrity.Matches(f, "sha256:" + new string('0', 64)));
        Assert.False(Integrity.Matches(f, null));
        Assert.False(Integrity.Matches(f, "sha1:abc"));
    }

    // Prawdziwe pliki z internetu - tak jak pobiera je Velivo.
    [Fact]
    public async Task Piper_z_GitHuba_zgadza_sie_z_przypieta_suma()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var f = Tool(await http.GetByteArrayAsync("https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip"));
        Assert.True(Integrity.Matches(f, Integrity.PiperZipSha256));
        var b = File.ReadAllBytes(f); b[b.Length / 2] ^= 0xFF; File.WriteAllBytes(f, b);   // jeden zmieniony bajt
        Assert.False(Integrity.Matches(f, Integrity.PiperZipSha256));
    }

    [Fact]
    public async Task YtDlp_z_GitHuba_przechodzi_weryfikacje_z_SHA2_256SUMS()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var f = Tool(await http.GetByteArrayAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"));
        await Integrity.VerifyAgainstSumsAsync(http, "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS", "yt-dlp.exe", f);
    }
}
