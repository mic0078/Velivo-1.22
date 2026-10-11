using System.Linq;
using Przegladarka;
using Xunit;

// Grupy kart (grupy-kart.json): zawsze wracaja po ponownym uruchomieniu, takze gdy przywracanie kart jest wylaczone.
// Wczesniej grupy zapisywaly sie tylko z sesja - przy domyslnych ustawieniach znikaly po restarcie.
public class KeptGroupsTests
{
    [Fact]
    public void OdczytujePoprawnaGrupe()
    {
        var g = MainWindow.ParseKeptGroups("[{\"Name\":\"Praca\",\"Color\":\"2563EB\",\"Collapsed\":true,\"Urls\":[\"https://example.com/\",\"https://wikipedia.org/\"]}]");
        Assert.Single(g);
        Assert.Equal("Praca", g[0].Name);
        Assert.True(g[0].Collapsed);
        Assert.Equal(new[] { "https://example.com/", "https://wikipedia.org/" }, g[0].Urls);
    }

    [Fact]
    public void PomijaZleWpisyIAdresy()
    {
        var g = MainWindow.ParseKeptGroups("[{\"Name\":\"\",\"Urls\":[\"https://a.pl/\"]},{\"Name\":\"X\",\"Urls\":[\"about:blank\"]},{\"Name\":\"Y\",\"Urls\":[\"edge://settings\",\"https://b.pl/\"]}]");
        Assert.Equal(new[] { "Y" }, g.Select(x => x.Name));
        Assert.Equal(new[] { "https://b.pl/" }, g[0].Urls);
    }

    [Fact]
    public void UszkodzonyPlikNieWywracaStartu()
    {
        Assert.Empty(MainWindow.ParseKeptGroups("{to nie json"));
        Assert.Empty(MainWindow.ParseKeptGroups(null));
    }
}
