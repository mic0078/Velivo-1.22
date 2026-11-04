using System;
using System.Text.Json.Nodes;
using Velivo;
using Xunit;

// 🏛 prywatna infrastruktura serwerów: certyfikat członkostwa, dowód przy połączeniu, odwołanie, scalanie, obcy klaster
public class KlasterTests
{
    [Fact]
    public void Czlonek_MaWaznyCertyfikat_IDowodPolaczenia()
    {
        var cl = KomCluster.Create(); var (sp, spub) = KomCluster.NewServerKey();
        var cert = cl.Issue(spub, "ryzen");
        Assert.True(cl.Valid(cert));
        Assert.Same(cert, cl.CertOf("ryzen"));
        var proof = KomCluster.Proof(sp, cl.Id, "ryzen", "nucbox");
        Assert.True(cl.CheckProof(cert, "ryzen", "nucbox", proof));
        Assert.False(cl.CheckProof(cert, "obcy", "nucbox", proof));   // cudzy identyfikator z tym certyfikatem
        Assert.False(cl.CheckProof(cert, "ryzen", "chuwi", proof));   // dowód dla innego połączenia
    }

    [Fact]
    public void SerwerZnajomego_ZInnegoKlastra_NieJestCzlonkiem()
    {
        var moj = KomCluster.Create(); var obcy = KomCluster.Create(); var (sp, spub) = KomCluster.NewServerKey();
        var certObcy = obcy.Issue(spub, "znajomy");
        Assert.False(moj.Valid(certObcy));
        Assert.False(moj.AddCert(certObcy));
        Assert.Null(moj.CertOf("znajomy"));
        // podrobiony certyfikat (zmieniony identyfikator) - podpis się nie zgadza
        var (_, mpub) = KomCluster.NewServerKey(); var c = moj.Issue(mpub, "nucbox"); var f = (JsonObject)c.DeepClone(); f["d"] = "znajomy";
        Assert.False(moj.Valid(f));
    }

    [Fact]
    public void Odwolanie_WygrywaZCertyfikatem_IDochodziDoInnychCzlonkow()
    {
        var a = KomCluster.Create(); var (_, p1) = KomCluster.NewServerKey();
        var cert = a.Issue(p1, "chuwi");
        var b = KomCluster.FromJson(a.ToJson(true));
        Assert.NotNull(b.CertOf("chuwi"));
        var rev = a.Revoke(KomCluster.ServerId(p1));
        Assert.Null(a.CertOf("chuwi"));
        Assert.True(b.Merge(KomCluster.FromJson(a.ToJson(false))));
        Assert.Null(b.CertOf("chuwi"));
        Assert.False(b.AddCert(cert));   // stary certyfikat nie wraca
        Assert.False(b.AddRevoke(rev));   // powtórka - bez zmian
    }

    [Fact]
    public void Scalanie_BezUtraty_IPoRestarcie()
    {
        var a = KomCluster.Create(); var b = KomCluster.FromJson(a.ToJson(true));
        var (_, p1) = KomCluster.NewServerKey(); var (_, p2) = KomCluster.NewServerKey();
        a.Issue(p1, "nucbox"); b.Issue(p2, "ryzen");   // każdy przyjął innego członka niezależnie
        a.Merge(KomCluster.FromJson(b.ToJson(false))); b.Merge(KomCluster.FromJson(a.ToJson(false)));
        Assert.Equal(2, a.Certs.Count); Assert.Equal(2, b.Certs.Count);
        var po = KomCluster.FromJson(JsonNode.Parse(a.ToJson(true).ToJsonString()).AsObject());   // zapis i odczyt (restart)
        Assert.NotNull(po.CertOf("nucbox")); Assert.NotNull(po.CertOf("ryzen")); Assert.NotNull(po.Priv);
        Assert.False(a.Merge(KomCluster.FromJson(b.ToJson(false))));   // powtórka - bez duplikatów
    }
}
