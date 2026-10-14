using Przegladarka;
using Xunit;

// kopia bezpieczenstwa: dane uzytkownika tak, smieci (do pobrania / odbudowania) nie
public class KopiaTests
{
    [Theory]
    [InlineData("hasla.vault", true)]
    [InlineData("zakladki.txt", true)]
    [InlineData("iptv/listy.json", true)]
    [InlineData("iptv/abc.m3u", true)]
    [InlineData("filmy-info/reczne.json", true)]
    [InlineData("Profiles/praca/hasla.vault", true)]
    [InlineData("Profil/EBWebView/Default/Network/Cookies", true)]
    [InlineData("Profil/EBWebView/Default/Local Storage/leveldb/000003.log", true)]
    [InlineData("Profil/EBWebView/Default/Cache/Cache_Data/f_000001", false)]
    [InlineData("Profil/EBWebView/GrShaderCache/data_0", false)]
    [InlineData("Piper/pl_PL-gosia.onnx", false)]
    [InlineData("narzedzia/yt-dlp.exe", false)]
    [InlineData("Miniatury stron/a.png", false)]
    [InlineData("iptv/logo/1.img", false)]
    [InlineData("iptv/abc.epg", false)]
    [InlineData("vlc.log", false)]
    [InlineData("dziala.txt", false)]
    public void CoTrafiaDoKopii(string rel, bool want) => Assert.Equal(want, MainWindow.BackupInclude(rel));

    // pelna kopia: wszystko poza pamiecia podreczna stron, logiem VLC i plikami tymczasowymi
    [Theory]
    [InlineData("iptv/abc.epg", true)]
    [InlineData("iptv/logo/1.img", true)]
    [InlineData("Piper/pl_PL-gosia.onnx", true)]
    [InlineData("narzedzia/yt-dlp.exe", true)]
    [InlineData("Profil/EBWebView/Default/IndexedDB/x.ldb", true)]
    [InlineData("hasla.vault", true)]
    [InlineData("Profil/EBWebView/Default/Cache/Cache_Data/f_000001", false)]
    [InlineData("Profil/EBWebView/Default/Code Cache/js/a", false)]
    [InlineData("Profil/EBWebView/GrShaderCache/data_0", false)]
    [InlineData("vlc.log", false)]
    [InlineData("kopia.zip.part", false)]
    public void PelnaKopia(string rel, bool want) => Assert.Equal(want, MainWindow.BackupInclude(rel, true));
}
