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
        var merged = MainWindow.MergeSyncedBank(local, incoming, 0, out bool replaced);
        Assert.False(replaced);                                        // dane te same - bez blokady trybu
        Assert.Equal(2, Parse(merged).GetProperty("Log").GetArrayLength());
    }

    [Fact]
    public void Nowsze_dane_tutaj_nie_sa_nadpisywane_starszymi_z_drugiego_komputera()
    {
        var local = Bank(200, "NOWA-KARTA", (3, "PC-A"));
        var incoming = Bank(100, "STARE", (4, "PC-B"));
        var merged = MainWindow.MergeSyncedBank(local, incoming, 0, out bool replaced);
        Assert.False(replaced);
        Assert.Equal("NOWA-KARTA", Parse(merged).GetProperty("Cards").GetString());
        Assert.Equal(2, Parse(merged).GetProperty("Log").GetArrayLength());   // wpis z PC-B nie ginie
    }

    [Fact]
    public void Nowsze_dane_z_drugiego_komputera_sa_przyjmowane()
    {
        var local = Bank(100, "STARE", (1, "PC-A"));
        var incoming = Bank(300, "NOWE", (5, "PC-B"));
        var merged = MainWindow.MergeSyncedBank(local, incoming, 0, out bool replaced);
        Assert.True(replaced);
        Assert.Equal("NOWE", Parse(merged).GetProperty("Cards").GetString());
        Assert.Equal(2, Parse(merged).GetProperty("Log").GetArrayLength());   // tutejszy wpis tez zostaje
    }

    [Fact]
    public void Uszkodzone_dane_z_sieci_sa_odrzucane()
    {
        Assert.Null(MainWindow.MergeSyncedBank(Bank(1, "X"), "{to nie json", 0, out _));
        Assert.Null(MainWindow.MergeSyncedBank(Bank(1, "X"), "{\"Owner\":\"bez hasla\"}", 0, out _));
    }

    [Fact]
    public void Usuniety_tu_profil_nie_wraca_ze_starszymi_danymi_z_drugiego_komputera()
    {
        // "Zapomnialem hasla - wyczysc" tutaj o czasie 500; drugi komputer ma dane z czasu 300
        Assert.Null(MainWindow.MergeSyncedBank("", Bank(300, "STARE"), 500, out _));
    }

    [Fact]
    public void Nowy_profil_z_drugiego_komputera_po_usunieciu_jest_przyjmowany()
    {
        var merged = MainWindow.MergeSyncedBank("", Bank(700, "NOWY"), 500, out bool replaced);
        Assert.True(replaced);
        Assert.Equal("NOWY", Parse(merged).GetProperty("Cards").GetString());
    }

    [Fact]
    public void Remis_znacznikow_rozstrzygany_tak_samo_na_obu_komputerach()
    {
        var a = Bank(0, "DANE-A");   // pliki sprzed tej wersji: Changed = 0 na obu komputerach
        var b = Bank(0, "DANE-B");
        var naA = Parse(MainWindow.MergeSyncedBank(a, b, 0, out _)).GetProperty("Cards").GetString();
        var naB = Parse(MainWindow.MergeSyncedBank(b, a, 0, out _)).GetProperty("Cards").GetString();
        Assert.Equal(naA, naB);   // oba komputery koncza z tymi samymi danymi (bez zamiany miejscami)
    }

    // ---------- zapis profilu (MainWindow.DecideBankSave): nie nadpisuje tego, co w miedzyczasie przyszlo z drugiego komputera ----------

    [Fact]
    public void Nowy_profil_nie_nadpisuje_profilu_ktory_przyszedl_z_drugiego_komputera()
    {
        // zakladanie profilu trwa (okno z haslem), w tym czasie synchronizacja zapisala profil z PC-B
        Assert.Null(MainWindow.DecideBankSave(Bank(100, "Z-PC-B"), 0, Bank(0, "NOWY"), null, 1000, out bool ok));
        Assert.False(ok);
    }

    [Fact]
    public void Nowy_profil_po_usunieciu_jest_nowszy_niz_usuniecie_nawet_gdy_zegar_drugiego_komputera_sie_spieszy()
    {
        // usuniecie przyszlo z PC-B o czasie 5000 wg jego zegara, a tutejszy zegar wskazuje dopiero 1000
        var json = MainWindow.DecideBankSave("", 5000, Bank(0, "NOWY"), null, 1000, out bool ok);
        Assert.True(ok);
        Assert.True(Parse(json).GetProperty("Changed").GetInt64() > 5000);
        // PC-B (ze znacznikiem usuniecia 5000) przyjmie nowy profil, zamiast go odrzucic jako "starszy niz usuniecie"
        Assert.NotNull(MainWindow.MergeSyncedBank("", json, 5000, out bool replaced));
        Assert.True(replaced);
    }

    [Fact]
    public void Otwarcie_bez_zmiany_danych_zachowuje_znacznik_i_dziennik_z_drugiego_komputera()
    {
        var loaded = Bank(100, "KARTY", (1, "PC-A"));
        var disk = Bank(100, "KARTY", (1, "PC-A"), (2, "PC-B"));    // w miedzyczasie przyszedl wpis dziennika z PC-B
        var c = Bank(100, "KARTY", (1, "PC-A"), (3, "PC-A"));       // tu: nowe otwarcie trybu
        var json = MainWindow.DecideBankSave(disk, 0, c, MainWindow.BankLoadedKey(loaded), 9000, out bool ok);
        Assert.True(ok);
        Assert.Equal(100L, Parse(json).GetProperty("Changed").GetInt64());   // samo otwarcie to nie zmiana danych
        Assert.Equal(3, Parse(json).GetProperty("Log").GetArrayLength());   // wpis z PC-B nie ginie
    }

    [Fact]
    public void Dane_zmienione_na_drugim_komputerze_od_wczytania_nie_sa_nadpisywane()
    {
        var loaded = Bank(100, "KARTY");
        var disk = Bank(200, "KARTY-Z-PC-B");
        Assert.Null(MainWindow.DecideBankSave(disk, 0, Bank(100, "MOJA-ZMIANA"), MainWindow.BankLoadedKey(loaded), 9000, out bool ok));
        Assert.False(ok);
    }

    [Fact]
    public void Profil_usuniety_od_wczytania_nie_jest_przywracany_zapisem()
    {
        var loaded = Bank(100, "KARTY");
        Assert.Null(MainWindow.DecideBankSave("", 500, Bank(100, "KARTY"), MainWindow.BankLoadedKey(loaded), 9000, out _));
        Assert.Null(MainWindow.DecideBankSave("{uszkodzony", 0, Bank(100, "KARTY"), MainWindow.BankLoadedKey(loaded), 9000, out _));
    }

    [Fact]
    public void Zmiana_danych_ma_znacznik_nowszy_niz_dane_na_dysku()
    {
        var loaded = Bank(5000, "KARTY");
        var spozniony = MainWindow.DecideBankSave(loaded, 0, Bank(5000, "NOWA-KARTA"), MainWindow.BankLoadedKey(loaded), 1000, out bool ok);
        Assert.True(ok);
        Assert.Equal(5001L, Parse(spozniony).GetProperty("Changed").GetInt64());   // tutejszy zegar sie spoznia - i tak nowsze
        Assert.Equal("NOWA-KARTA", Parse(spozniony).GetProperty("Cards").GetString());
        var teraz = MainWindow.DecideBankSave(loaded, 0, Bank(5000, "NOWA-KARTA"), MainWindow.BankLoadedKey(loaded), 9000, out _);
        Assert.Equal(9000L, Parse(teraz).GetProperty("Changed").GetInt64());
    }
}
