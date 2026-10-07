using Przegladarka;
using Xunit;

// "Gdzie ja to czytalem?" - prawdziwy kod Velivo (MainWindow.MemoryAllowed): co trafia do pamieci stron.
public class PageMemoryTests
{
    [Theory]
    [InlineData("https://www.google.com/search?q=iga+swiatek", false)]   // listy wynikow wyszukiwarek - nie
    [InlineData("https://www.startpage.com/sp/search?query=iga", false)]
    [InlineData("https://duckduckgo.com/?q=iga", false)]
    [InlineData("https://www.bing.com/search?q=iga", false)]
    [InlineData("https://www.ipko.pl/", false)]                            // bankowosc - nigdy
    [InlineData("https://sport.interia.pl/tenis/news-a-jednak-swiatek", true)]   // artykul - tak
    [InlineData("https://maps.google.com/maps/place/Pekin", true)]         // inna strona Google niz wyniki - tak
    [InlineData("about:blank", false)]
    public void Co_trafia_do_pamieci_stron(string url, bool tak)
    {
        Assert.Equal(tak, MainWindow.MemoryAllowed(url));
    }
}
