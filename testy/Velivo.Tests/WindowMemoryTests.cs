using System.Windows;
using Przegladarka;
using Xunit;

// Okna pamietaja miejsce i rozmiar: klucz bez zmiennej czesci tytulu, zapis "x,y,szer,wys[,max]".
public class WindowMemoryTests
{
    [Fact]
    public void KluczOkna()
    {
        Assert.Equal("Odtwarzacz filmów", MainWindow.WindowKey("Alien 1979 – Odtwarzacz filmów"));
        Assert.Equal("Odtwarzacz filmów", MainWindow.WindowKey("Odtwarzacz filmów"));
        Assert.Equal("🎬 Biblioteka filmów", MainWindow.WindowKey("🎬 Biblioteka filmów"));
    }

    [Fact]
    public void ZapisIOdczyt()
    {
        var s = MainWindow.FormatBounds(new Rect(-1910, 40.4, 860, 540), true);
        Assert.Equal("-1910,40,860,540,max", s);   // drugi monitor po lewej
        Assert.True(MainWindow.ParseBounds(s, out var r, out var max));
        Assert.Equal(new Rect(-1910, 40, 860, 540), r); Assert.True(max);
        Assert.False(MainWindow.ParseBounds("1,2,3", out _, out _));
        Assert.False(MainWindow.ParseBounds("0,0,20,20", out _, out _));   // za male - zepsuty zapis
    }
}
