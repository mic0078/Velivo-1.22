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

    [Fact]
    public void PrzezroczystoscKolkiem()
    {
        Assert.Equal(0.95, MainWindow.NextAlpha(1.0, -120));
        Assert.Equal(1.0, MainWindow.NextAlpha(1.0, 120));    // nie wiecej niz 100%
        Assert.Equal(0.2, MainWindow.NextAlpha(0.2, -120));   // okno nie znika calkiem
    }

    [Fact]
    public void JasnoscKoloru()
    {
        Assert.Equal(1.0, MainWindow.Luma(System.Windows.Media.Brushes.White), 3);   // biala karta - do zamiany na ciemna
        Assert.Equal(0.0, MainWindow.Luma(System.Windows.Media.Brushes.Black), 3);
        Assert.True(MainWindow.Luma(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF4, 0xF6))) > 0.8);
        Assert.Equal(-1, MainWindow.Luma(null));
    }
}
