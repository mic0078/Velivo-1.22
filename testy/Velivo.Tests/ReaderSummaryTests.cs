using System.Linq;
using Przegladarka;
using Xunit;

// Streszczenie w trybie czytania: tylko pelne, samodzielne zdania artykulu - bez srodtytulow, wyrwanych cytatow i zdan,
// ktore bez poprzedniego zdania nie maja sensu (przypadek z TVN24, ktory pokazal wlasciciel).
public class ReaderSummaryTests
{
    const string Tvn = "Ataki w szkołach. Donald Tusk nakazał patrole policji\n\n" +
        "W tym tygodniu doszło do dwóch aktów przemocy w szkołach. Premier Donald Tusk polecił komendantowi głównemu policji, aby funkcjonariusze codziennie patrolowali okolice szkół w całym kraju.\n\n" +
        "Jak mówił, \"właściwie od pewnego czasu policja stara się być obecna przynajmniej raz dziennie patrolować okolice szkoły\".\n\n" +
        "- Sytuacja się zmieniła i szczególnie teraz, kiedy mamy to poczucie, że ryzyko mogło wzrosnąć (...) bardzo proszę o wydanie decyzji, aby policja, nie czekając na interwencję, tylko w ramach rutynowego patrolu przynajmniej raz dziennie pojawiła się w szkole, wewnątrz - powiedział szef rządu w trakcie posiedzenia, zwracając się do komendanta głównego policji gen.\n\n" +
        "Tusk: cała Polska powinna usłyszeć o ich bohaterstwie\n\n" +
        "Patrole mają się pojawiać w szkołach podstawowych i średnich od przyszłego tygodnia, a ich trasy ustalą lokalne komendy policji razem z dyrektorami szkół.\n\n" +
        "Tusk przekazał wyrazy współczucia dla rodziny zmarłej pracownicy szkoły w miejscowości Leszczydół-Nowiny pod Wyszkowem.";

    [Fact]
    public void Tylko_pelne_samodzielne_zdania()
    {
        var s = MainWindow.LocalSummary(Tvn, 4, "Ataki w szkołach. Donald Tusk nakazał patrole policji - TVN24");
        var lines = s.Split('\n');
        Assert.DoesNotContain(lines, l => l.Contains("bohaterstwie"));        // srodtytul bez kropki
        Assert.DoesNotContain(lines, l => l.StartsWith("• -") || l.Contains("(...)"));   // wyrwany cytat z myslnikiem
        Assert.DoesNotContain(lines, l => l.StartsWith("• Jak mówił"));      // zdanie zalezne od poprzedniego
        Assert.Contains(lines, l => l.Contains("Premier Donald Tusk polecił"));   // sedno artykulu jest
        Assert.True(lines.Length >= 2 && lines.Length <= 4);
    }
}
