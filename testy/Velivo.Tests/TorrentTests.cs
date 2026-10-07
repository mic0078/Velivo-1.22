using System;
using System.Collections.Generic;
using System.Linq;
using Przegladarka;
using Xunit;

// Torrenty - prawdziwy kod Velivo (MainWindow.Aria2Args / MagnetName / TorrentProgress / IsMagnet).
public class TorrentTests
{
    [Fact]
    public void Opcje_z_ustawien_limity_i_udostepnianie()
    {
        var a = MainWindow.Aria2Args(@"C:\Pobrane\Velivo-Torrenty", 2048, 1024, 1.0, 30, 60);
        Assert.Contains(@"--dir=C:\Pobrane\Velivo-Torrenty", a);
        Assert.Contains("--max-overall-download-limit=2048K", a);
        Assert.Contains("--max-upload-limit=1024K", a);
        Assert.Contains("--seed-ratio=1.0", a);
        Assert.Contains("--seed-time=30", a);
        Assert.Contains("--bt-max-peers=60", a);
        Assert.Equal("--", a.Last());   // dalej tylko link / plik - nigdy kolejna opcja
    }

    [Fact]
    public void Bez_limitow_i_bez_udostepniania()
    {
        var a = MainWindow.Aria2Args("D", 0, 0, 0, 30, 40);
        Assert.DoesNotContain(a, x => x.StartsWith("--max-overall-download-limit") || x.StartsWith("--max-upload-limit") || x.StartsWith("--seed-ratio"));
        Assert.Contains("--seed-time=0", a);   // nie udostepniaj po pobraniu
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:ABCDEF0123456789&dn=Ubuntu+24.04+ISO", "Ubuntu 24.04 ISO")]
    [InlineData("magnet:?xt=urn:btih:ABCDEF0123456789abcdef&tr=udp://x", "magnet ABCDEF012345")]
    public void Nazwa_z_linku_magnet(string magnet, string nazwa)
    {
        Assert.Equal(nazwa, MainWindow.MagnetName(magnet));
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:abc", true)]
    [InlineData("  MAGNET:?xt=urn:btih:abc", true)]
    [InlineData("https://example.com/magnet:?x", false)]
    [InlineData("magnet", false)]
    public void Rozpoznanie_linku_magnet(string s, bool tak)
    {
        Assert.Equal(tak, MainWindow.IsMagnet(s));
    }

    [Fact]
    public void Postep_aria2()
    {
        var p = MainWindow.TorrentProgress("[#2089b0 400KiB/33MiB(12%) CN:44 SD:10 DL:1.5MiB ETA:4m53s]");
        Assert.Equal(12, p.Item1); Assert.Equal("1.5MiB", p.Item2); Assert.Equal("4m53s", p.Item3); Assert.False(p.Item4);
        Assert.True(MainWindow.TorrentProgress("[#2089b0 SEED(1.2) CN:3 UL:20KiB]").Item4);
        Assert.Null(MainWindow.TorrentProgress("10/07 12:00:00 [NOTICE] Download complete: C:\\x"));
    }

    [Fact]
    public void Plik_pobrany_z_wyniku_aria2()
    {
        Assert.Equal(@"C:\Pobrane\Velivo-Torrenty\ubuntu.iso", MainWindow.TorrentDoneFile(@"803aab|OK  |   657MiB/s|C:\Pobrane\Velivo-Torrenty\ubuntu.iso"));
        Assert.Null(MainWindow.TorrentDoneFile("2089b0|OK  |   1.2MiB/s|[MEMORY][METADATA]ubuntu.iso"));   // metadane magnet, nie plik
        Assert.Null(MainWindow.TorrentDoneFile("2089b0|ERR |       0B/s|C:\\x"));
        Assert.Null(MainWindow.TorrentDoneFile("Status Legend:"));
    }

    [Fact]
    public void Skojarzenie_z_Windows_rozpoznaje_torrent()
    {
        Assert.Equal("magnet:?xt=urn:btih:abc", MainWindow.TorrentArg(" magnet:?xt=urn:btih:abc "));
        Assert.Equal(@"C:\Pobrane\ubuntu.torrent", MainWindow.TorrentArg("file:///C:/Pobrane/ubuntu.torrent"));   // tak plik przychodzi po ArgToUrl
        Assert.Null(MainWindow.TorrentArg("https://example.com/ubuntu.torrent"));   // adres w sieci to zwykla karta
        Assert.Null(MainWindow.TorrentArg("file:///C:/Pobrane/strona.html"));
        Assert.Null(MainWindow.TorrentArg("velivo.pl"));
    }

    [Fact]
    public void Przerwany_torrent_usuwa_tylko_swoje_niedokonczone_czesci()
    {
        var dir = @"C:\T";
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\T\stary.iso", @"C:\T\inny", @"C:\T\inny.aria2" };
        var after = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase) { @"C:\T\ubuntu.iso", @"C:\T\ubuntu.iso.aria2", @"C:\T\gotowy.mkv" };
        Assert.Equal(new[] { @"C:\T\ubuntu.iso" }, MainWindow.PartialEntries(dir, before, after));   // bez starych plikow i bez skonczonych
        Assert.Empty(MainWindow.PartialEntries(dir, after, after));
    }

    [Fact]
    public void Sciezka_pobieranego_pliku_z_linii_FILE()
    {
        Assert.Equal(@"D:\Filmy\film.avi", MainWindow.TorrentFileLine(@"FILE: D:\Filmy\film.avi"));
        Assert.Null(MainWindow.TorrentFileLine("FILE: [METADATA]film"));
        Assert.Null(MainWindow.TorrentFileLine("[#5d77cf SEED(0.0) CN:0 SD:0]"));
    }

    [Fact]
    public void Usun_plik_usuwa_tylko_to_co_dodal_torrent()
    {
        Assert.Equal(@"D:\Filmy\film.avi", MainWindow.TorrentTopEntry(@"D:\Filmy", @"D:\Filmy\film.avi"));
        Assert.Equal(@"D:\Filmy\Serial S01", MainWindow.TorrentTopEntry(@"D:\Filmy\", @"D:\Filmy\Serial S01\odc1.mkv"));   // torrent z folderem
        Assert.Null(MainWindow.TorrentTopEntry(@"D:\Filmy", @"D:\Filmy"));   // nigdy caly folder pobierania
        Assert.Null(MainWindow.TorrentTopEntry(@"D:\Filmy", @"D:\Filmy2\x.avi"));
        Assert.Null(MainWindow.TorrentTopEntry(@"D:\Filmy", @"D:\Filmy\..\Windows\x"));
    }
}
