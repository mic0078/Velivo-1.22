using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Velivo;
using Xunit;

// Komunikator: to, co psuło się przy serwerach (pośrednikach), zaporze i kopii - sprawdzane przed każdą budową
public class KomunikatorTests
{
    // telefon i laptop szukają przeniesionego serwera (np. CHUWI 41922 -> 41929) na wszystkich portach 41920–41929, pod adresem domu i serwera
    [Fact]
    public void ZapasoweAdresySerweraObejmujaWszystkiePorty()
    {
        var alts = MainWindow.KomAltAddrs("81.111.16.33:41922", new[] { "81.111.16.33", "mojdom.ddns.net" });
        foreach (var p in Enumerable.Range(41920, 10).Where(p => p != 41922))
            Assert.Contains("81.111.16.33:" + p, alts);
        Assert.Contains("mojdom.ddns.net:41922", alts);           // port zapamiętany pod nazwą domu
        Assert.Contains("mojdom.ddns.net:41929", alts);
        Assert.DoesNotContain("81.111.16.33:41922", alts);        // obecny adres - nie jako zapasowy
        Assert.Equal(alts.Count, alts.Distinct().Count());        // bez powtórek
        Assert.Empty(MainWindow.KomAltAddrs("zly-adres", new[] { "81.111.16.33" }));
        Assert.Empty(MainWindow.KomAltAddrs(null, new string[0]));
    }

    // reguła zapory „41920-41929,443” wpuszcza każdy port serwera i 443, nie wpuszcza portów sieci domowej
    [Theory]
    [InlineData("41920-41929,443", 41921, true)]
    [InlineData("41920-41929,443", 41929, true)]
    [InlineData("41920-41929,443", 443, true)]
    [InlineData("41920-41929,443", 41919, false)]
    [InlineData("41921,443", 41921, true)]
    [InlineData("41921, 443", 443, true)]
    [InlineData("41921", 41923, false)]
    [InlineData("", 41921, false)]
    [InlineData(null, 41921, false)]
    public void RegulaZaporyObejmujePort(string ports, int port, bool want) => Assert.Equal(want, MainWindow.KomPortsCover(ports, port));

    // przedstawianie moich urządzeń: ta sama para zawsze dostaje ten sam klucz (powtórka nie rozjeżdża kluczy), inna para - inny
    [Fact]
    public void KluczPrzedstawieniaStalyDlaPary()
    {
        var sec = RandomNumberGenerator.GetBytes(32);
        string a = new string('a', 32), b = new string('b', 32), c = new string('c', 32);
        Assert.Equal(MainWindow.KomIntroKeyFrom(sec, a, b), MainWindow.KomIntroKeyFrom(sec, b, a));
        Assert.NotEqual(MainWindow.KomIntroKeyFrom(sec, a, b), MainWindow.KomIntroKeyFrom(sec, a, c));
        Assert.NotEqual(MainWindow.KomIntroKeyFrom(sec, a, b), MainWindow.KomIntroKeyFrom(RandomNumberGenerator.GetBytes(32), a, b));
        Assert.Equal(32, Convert.FromBase64String(MainWindow.KomIntroKeyFrom(sec, a, b)).Length);
    }

    // podpis powitania serwera: ten sam klucz - ten sam podpis, inny klucz albo inna treść - inny (serwer odrzuca obcych)
    [Fact]
    public void PodpisPowitaniaSerwera()
    {
        var k = RandomNumberGenerator.GetBytes(32); var hello = Encoding.UTF8.GetBytes("{\"Type\":\"relay-hello\"}");
        Assert.Equal(MainWindow.KomMacBytes(k, "relay-hello\n", hello), MainWindow.KomMacBytes(k, "relay-hello\n", hello));
        Assert.NotEqual(MainWindow.KomMacBytes(k, "relay-hello\n", hello), MainWindow.KomMacBytes(RandomNumberGenerator.GetBytes(32), "relay-hello\n", hello));
        Assert.NotEqual(MainWindow.KomMacBytes(k, "relay-hello\n", hello), MainWindow.KomMacBytes(k, "relay-ok\n", hello));
    }

    // ramki serwera: zaszyfrowane wiadomości, pliki (duże - do 1,5 MB synchronizacji) i kolejność przechodzą przez prawdziwe połączenie TCP
    [Fact]
    public async Task RamkiSerweraPrzechodzaPrzezPolaczenie()
    {
        var key = RandomNumberGenerator.GetBytes(32); byte[] nc = RandomNumberGenerator.GetBytes(16), ns = RandomNumberGenerator.GetBytes(16);
        var session = MainWindow.KomSessionKey(key, nc, ns);
        Assert.Equal(session, MainWindow.KomSessionKey(key, nc, ns));   // obie strony liczą ten sam klucz sesji
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        try
        {
            var accept = l.AcceptTcpClientAsync();
            var cl = new TcpClient(); await cl.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)l.LocalEndpoint).Port);
            using var a = new MainWindow.KomFrameStream(cl, session);
            using var b = new MainWindow.KomFrameStream(await accept, session);
            var small = Encoding.UTF8.GetBytes("{\"t\":\"p2p-msg\",\"txt\":\"Cześć mamo\"}");
            var big = RandomNumberGenerator.GetBytes(1_500_000);
            var read = Task.Run(() => new[] { b.ReadFrame(), b.ReadFrame(), b.ReadFrame() });   // odbiór równolegle z wysyłaniem (jak w programie) - inaczej duża ramka czeka na pełny bufor
            a.WriteFrame(small, 0, small.Length); a.WriteFrame(big, 0, big.Length); a.WriteFrame(small, 0, small.Length);
            var r = await read;
            Assert.Equal(small, r[0]); Assert.Equal(big, r[1]); Assert.Equal(small, r[2]);
        }
        finally { l.Stop(); }
    }

    // kopia zapasowa: filmy i zdjęcia bez ponownej kompresji (kopia na serwerze trwała bez końca), silnik rozmów wideo Komunikatora poza kopią
    [Theory]
    [InlineData("skrzynka/abc/film.mp4", true)]
    [InlineData("zdjecie.JPG", true)]
    [InlineData("glosowa.m4a", true)]
    [InlineData("zakladki.txt", false)]
    [InlineData("hasla.vault", false)]
    public void KopiaBezPonownejKompresji(string f, bool packed) => Assert.Equal(packed, MainWindow.BackupPacked(f));

    [Theory]
    [InlineData("Komunikator-WebView/EBWebView/Default/Network/Cookies")]
    [InlineData("Komunikator-WebView/EBWebView/Default/Preferences")]
    public void KopiaBezSilnikaRozmowKomunikatora(string rel) { Assert.False(MainWindow.BackupInclude(rel)); Assert.False(MainWindow.BackupInclude(rel, true)); }
}
