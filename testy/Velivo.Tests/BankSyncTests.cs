using System.Text.Json;
using Przegladarka;
using Xunit;

// Synchronizacja trybu bankowego przez LAN - prawdziwy kod Velivo (MainWindow.MergeSyncedBank).
public class BankSyncTests
{
    static string Bank(long changed, string cards, params (long t, string dev)[] log)
    {
        var entries = new System.Collections.Generic.List<object>();
        foreach (var e in log) entries.Add(new { T = e.t, Device = e.dev, How = "password", Ok = true });
        return JsonSerializer.Serialize(new { Owner = "test", Salt = "c29s", Hash = "aGFzaA==", Iter = 600000, Cards = cards, Changed = changed, Log = entries });
    }

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Otwarcie_banku_na_drugim_komputerze_nie_zamyka_tu_trybu_i_laczy_dziennik()
    {
        var local = Bank(100, "KARTY", (1, "PC-A"));
        var incoming = Bank(100, "KARTY", (1, "PC-A"), (2, "PC-B"));   // na PC-B tylko otwarto tryb
        var merged = MainWindow.MergeSyncedBank(local, incoming, out bool replaced);
        Assert.False(replaced);                                        // dane te same - bez blokady trybu
        Assert.Equal(2, Parse(merged).GetProperty("Log").GetArrayLength());
    }

    [Fact]
    public void Nowsze_dane_tutaj_nie_sa_nadpisywane_starszymi_z_drugiego_komputera()
    {
        var local = Bank(200, "NOWA-KARTA", (3, "PC-A"));
        var incoming = Bank(100, "STARE", (4, "PC-B"));
        var merged = MainWindow.MergeSyncedBank(local, incoming, out bool replaced);
        Assert.False(replaced);
        Assert.Equal("NOWA-KARTA", Parse(merged).GetProperty("Cards").GetString());
        Assert.Equal(2, Parse(merged).GetProperty("Log").GetArrayLength());   // wpis z PC-B nie ginie
    }

    [Fact]
    public void Nowsze_dane_z_drugiego_komputera_sa_przyjmowane()
    {
        var local = Bank(100, "STARE", (1, "PC-A"));
        var incoming = Bank(300, "NOWE", (5, "PC-B"));
        var merged = MainWindow.MergeSyncedBank(local, incoming, out bool replaced);
        Assert.True(replaced);
        Assert.Equal("NOWE", Parse(merged).GetProperty("Cards").GetString());
        Assert.Equal(2, Parse(merged).GetProperty("Log").GetArrayLength());   // tutejszy wpis tez zostaje
    }

    [Fact]
    public void Uszkodzone_dane_z_sieci_sa_odrzucane()
    {
        Assert.Null(MainWindow.MergeSyncedBank(Bank(1, "X"), "{to nie json", out _));
        Assert.Null(MainWindow.MergeSyncedBank(Bank(1, "X"), "{\"Owner\":\"bez hasla\"}", out _));
    }
}
