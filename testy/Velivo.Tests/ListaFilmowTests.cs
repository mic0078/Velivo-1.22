using System.Collections.Generic;
using System.Linq;
using Przegladarka;
using Xunit;

// Lista filmow w odtwarzaczu: sortowanie (wybor zapamietany) i kolejny film z listy innego komputera.
public class ListaFilmowTests
{
    static readonly Dictionary<string, MainWindow.MovieInfo> meta = new Dictionary<string, MainWindow.MovieInfo>
    {
        ["b"] = new MainWindow.MovieInfo { Name = "Alien", Year = 1979, Rating = 8.5, Overview = "x" },
        ["c"] = new MainWindow.MovieInfo { Name = "Zodiak", Year = 2007, Rating = 7.7, Overview = "x" },
    };
    static List<string> Sort(string how) => MainWindow.SortFilms(new[] { "c", "Matrix.1999", "b" }, x => x, x => meta.TryGetValue(x, out var m) ? m : null, how);

    [Fact]
    public void SortowaniePoNazwieOcenieRoku()
    {
        Assert.Equal(new[] { "b", "Matrix.1999", "c" }, Sort("az"));
        Assert.Equal(new[] { "c", "Matrix.1999", "b" }, Sort("za"));
        Assert.Equal(new[] { "b", "c", "Matrix.1999" }, Sort("ocena"));
        Assert.Equal(new[] { "c", "Matrix.1999", "b" }, Sort("rok"));   // Matrix (1999) z nazwy pliku
    }

    [Fact]
    public void KolejnyFilmZListy()
    {
        var l = new List<string> { "1", "2", "3" };
        Assert.Equal("2", MainWindow.Neighbor(l, "1", 1));
        Assert.Equal("1", MainWindow.Neighbor(l, "2", -1));
        Assert.Null(MainWindow.Neighbor(l, "3", 1));
        Assert.Null(MainWindow.Neighbor(l, "x", 1));
    }
}
