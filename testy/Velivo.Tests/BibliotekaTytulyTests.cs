using System.Collections.Generic;
using System.Linq;
using Przegladarka;
using Xunit;

// Biblioteka: sortowanie po tytule takim, jaki widac (nie po surowej nazwie pliku z [dopiskami]),
// czytelny tytul nierozpoznanego filmu z Jellyfin i dopasowanie po tytule i roku.
public class BibliotekaTytulyTests
{
    [Fact]
    public void SortowaniePoWidocznymTytule()
    {
        var l = new List<MainWindow.LibraryVideo>
        {
            new MainWindow.LibraryVideo { Path = @"Y:\a\x.mkv", Title = "[ ELECTRO-TORRENT.d11] Mad Max -Pod Kopula Gromu 1985 1080p BRRip H264 AC3-d11 LP r.d11" },
            new MainWindow.LibraryVideo { Path = @"Y:\a\y.mkv", Title = "Alien.1979.PL.1080p.BDRip" },
        };
        var s = MainWindow.SortLibrary(l, "title", false).Select(v => v.Path).ToList();
        Assert.Equal(new[] { @"Y:\a\y.mkv", @"Y:\a\x.mkv" }, s);
    }

    [Fact]
    public void NierozpoznanyWJellyfinCzytelnie()
    {
        var m = new MainWindow.MovieInfo { Name = "[ELECTRO-TORRENT.d11] Projekt Almanach-Witajcie we wczoraj 2014 1080p BRRip H264 AC3-d11 LP r.d11", Year = 2014, Overview = "x" };
        Assert.Equal("Projekt Almanach-Witajcie we wczoraj (2014)", MainWindow.MetaTitle(m));
        Assert.Equal("Gwiezdne wrota- Arka prawdy (2008)", MainWindow.CleanTitle("[ ELECTROTORRENT.d11] Gwiezdne wrotaArka prawdy 2008 1080p".Replace("wrotaArka", "wrota- Arka")));
        Assert.Equal("Gwiezdne wrota- Arka prawdy (2008)", MainWindow.CleanTitle("Gwiezdne wrota- Arka prawdy2008"));
    }

    [Fact]
    public void DopasowaniePoTytuleIRoku()
    {
        var meta = new Dictionary<string, MainWindow.MovieInfo>(System.StringComparer.OrdinalIgnoreCase);
        var alien = new MainWindow.MovieInfo { Name = "Obcy - 8. pasażer Nostromo", Original = "Alien", Year = 1979, Rating = 8.5 };
        MainWindow.AddTitleKeys(meta, alien);
        Assert.Same(alien, MainWindow.LookupMeta(meta, @"X:\Filmy 0\Obcy Kolekcja\Alien.1979.Directors.Cut.mkv"));
        Assert.Null(MainWindow.LookupMeta(meta, @"X:\Filmy 0\Alien.1986.mkv"));
        Assert.Same(alien, MainWindow.LookupMeta(meta, @"X:\Obcy.8.pasazer.Nostromo.1979.mkv"));   // bez polskich znakow
        var later = new MainWindow.MovieInfo { Name = "28 lat później", Original = "28 Years Later", Year = 2025, Overview = "x" };
        MainWindow.AddTitleKeys(meta, later);
        Assert.Same(later, MainWindow.LookupMeta(meta, @"Y:\28\28 years later.mkv"));   // bez roku w nazwie pliku
        MainWindow.AddTitleKeys(meta, new MainWindow.MovieInfo { Name = "Alien", Year = 2030, Overview = "x" });
        Assert.Null(MainWindow.LookupMeta(meta, @"X:\Alien.mkv"));   // dwa filmy "Alien" - bez roku nie wiadomo ktory
    }

    [Fact]
    public void NoweFilmyZJellyfin()
    {
        var jf = new List<string> { @"U:\Media\Nowy.Film.2025.mkv", @"Y:\Media\Nowy.Film.2025.mkv", @"Y:\Media\Stary.mkv", @"Y:\Media\Brak.mkv", @"Y:\Media\opis.nfo" };
        var lib = new[] { @"X:\Filmy\Stary.mkv" };
        var add = MainWindow.NewJellyfinFiles(jf, lib, p => !p.Contains("Brak"));
        Assert.Equal(new[] { @"U:\Media\Nowy.Film.2025.mkv" }, add);   // ten sam plik z dwoch bibliotek - raz; znany, niedostepny i nie-film - pomijane
    }
}
