using Przegladarka;
using Xunit;

// Wybrane znaki z Moich notatek: dane z notatki i skojarzenie notatki ze strona banku (propozycja w okienku na stronie banku).
public class BankPartialNotesTests
{
    [Fact]
    public void DaneZNotatki()
    {
        Assert.True(MainWindow.NotePartial("Login: jan\nPasscode: 1234\nHasło: tajne1", out var pin, out var pwd));
        Assert.Equal("1234", pin); Assert.Equal("tajne1", pwd);
        Assert.False(MainWindow.NotePartial("Login: jan\nPIN:", out _, out _));   // pusty PIN to brak danych
    }

    [Fact]
    public void NotatkaSkojarzonaZeStronaBanku()
    {
        Assert.True(MainWindow.NoteMatchesHost("NatWest", "", "www.onlinebanking.natwest.com"));
        Assert.True(MainWindow.NoteMatchesHost("Konto", "bank: santander", "retail.santander.co.uk"));
        Assert.False(MainWindow.NoteMatchesHost("Barclays", "", "www.onlinebanking.natwest.com"));
        Assert.False(MainWindow.NoteMatchesHost("x", "", "localhost"));
    }
}
