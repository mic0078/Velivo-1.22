using Przegladarka;
using Xunit;

// Straznik przelewu w trybie bankowym - prawdziwy kod Velivo (MainWindow.CanonicalAccount / AccountChecksumOk / SameAccount).
public class TransferGuardTests
{
    [Theory]
    [InlineData("61 1090 1014 0000 0712 1981 2874", "PL61109010140000071219812874")]   // polski NRB ze spacjami = IBAN PL
    [InlineData("PL61-1090-1014-0000-0712-1981-2874", "PL61109010140000071219812874")]
    [InlineData("gb82 west 1234 5698 7654 32", "GB82WEST12345698765432")]
    [InlineData("1234 5678", "12345678")]                                                // brytyjski numer konta
    [InlineData("07700 900123", null)]                                                   // telefon - nie rachunek
    [InlineData("PL61 1090 1014 0000 0712 1981 2874 / tytul", null)]                    // inne znaki - nie zgadujemy
    [InlineData("", null)]
    public void Numer_rachunku_w_jednej_postaci(string wpisany, string oczekiwany)
    {
        Assert.Equal(oczekiwany, MainWindow.CanonicalAccount(wpisany));
    }

    [Theory]
    [InlineData("PL61109010140000071219812874", true)]
    [InlineData("GB82WEST12345698765432", true)]
    [InlineData("DE89370400440532013000", true)]
    [InlineData("PL61109010140000071219812875", false)]   // jedna cyfra inna (literowka / podmiana)
    [InlineData("PL16109010140000071219812874", false)]   // zamienione cyfry kontrolne
    [InlineData("GB82WEST12345698765423", false)]         // zamienione dwie ostatnie cyfry
    public void Suma_kontrolna_wylapuje_literowki(string iban, bool poprawny)
    {
        Assert.Equal(poprawny, MainWindow.AccountChecksumOk(iban));
    }

    [Fact]
    public void Brytyjski_numer_konta_bez_sumy_kontrolnej()
    {
        Assert.Null(MainWindow.AccountChecksumOk("12345678"));
    }

    [Fact]
    public void Ten_sam_rachunek_takze_8_cyfr_wobec_IBAN_GB()
    {
        Assert.True(MainWindow.SameAccount("PL61109010140000071219812874", MainWindow.CanonicalAccount("61 1090 1014 0000 0712 1981 2874")));
        Assert.True(MainWindow.SameAccount("98765432", "GB82WEST12345698765432"));
        Assert.True(MainWindow.SameAccount("GB82WEST12345698765432", "98765432"));
        Assert.False(MainWindow.SameAccount("98765433", "GB82WEST12345698765432"));
        Assert.False(MainWindow.SameAccount("98765432", "DE89370400440532013000"));   // koncowka liczy sie tylko dla IBAN GB
        Assert.False(MainWindow.SameAccount(null, "12345678"));
    }
}
