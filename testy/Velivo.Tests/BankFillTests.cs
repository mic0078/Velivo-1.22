using Przegladarka;
using Xunit;

// Wypelnianie w trybie bankowym - prawdziwy kod Velivo (MainWindow.DistinctLogins): konta z bazy trybu do jednej strony.
public class BankFillTests
{
    [Fact]
    public void Kilka_kont_do_jednej_strony_kazde_osobno_bez_powtorzen_i_pustych()
    {
        var l = MainWindow.DistinctLogins(new[]
        {
            ("Konto prywatne", "jan", "a1"),
            ("mBank", " JAN ", "inne"),       // ten sam login (wielkosc liter, spacje) - zostaje pierwszy (Moje loginy i hasla)
            ("Firma", "firma01", "b2"),
            ("", "", ""),                     // pusty wpis - pomijany
            ("firma02", "firma02", "c3"),     // nazwa = login - etykieta to sam login
        });
        Assert.Equal(3, l.Count);
        Assert.Equal(("Konto prywatne (jan)", "jan", "a1"), l[0]);
        Assert.Equal(("Firma (firma01)", "firma01", "b2"), l[1]);
        Assert.Equal(("firma02", "firma02", "c3"), l[2]);
    }

    [Fact]
    public void Samo_haslo_bez_loginu_tez_jest_kontem()
    {
        // np. strona banku pytajaca tylko o haslo / passcode
        var l = MainWindow.DistinctLogins(new[] { ("Bank", "", "tajne") });
        Assert.Single(l);
        Assert.Equal(("Bank", "", "tajne"), l[0]);
    }
}
