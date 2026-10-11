using Przegladarka;
using Xunit;

// Karteczki na pulpit (karteczki.json): odczyt, odhaczanie wierszy listy (• / ✓), wzory z ikonkami.
public class StickyNotesTests
{
    [Fact]
    public void OdczytKarteczek()
    {
        var n = MainWindow.ParseNotes("[{\"Id\":\"a1\",\"Kind\":\"shop\",\"Text\":\"☐ mleko\",\"X\":10,\"Y\":20,\"W\":240,\"H\":220,\"Color\":\"FFF59D\",\"Top\":true},{\"Kind\":\"note\"}]");
        Assert.Single(n);   // wpis bez Id pominiety
        Assert.Equal("shop", n[0].Kind);
        Assert.True(n[0].Top);
        Assert.Equal("• mleko", n[0].Text);   // dawne kratki zamienione na kropki
        Assert.Empty(MainWindow.ParseNotes("zepsuty plik"));
    }

    [Fact]
    public void OdhaczanieWierszaListy()
    {
        var t = "• mleko\n• chleb\nzwykły tekst";
        Assert.Equal("• mleko\n✓ chleb\nzwykły tekst", MainWindow.ToggleCheckLine(t, 1));
        Assert.Equal(t, MainWindow.ToggleCheckLine(MainWindow.ToggleCheckLine(t, 0), 0));
        Assert.Equal(t, MainWindow.ToggleCheckLine(t, 2));   // wiersz bez kratki - bez zmian
        Assert.Equal(t, MainWindow.ToggleCheckLine(t, 9));
    }

    [Fact]
    public void NieznanyRodzajToZwyklaKarteczka()
    {
        Assert.Equal("note", MainWindow.NoteKind("cos")[0]);
        Assert.Equal("🛒", MainWindow.NoteKind("shop")[1]);
    }
}
