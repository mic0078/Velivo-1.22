using Przegladarka;
using Xunit;

public class SleepTabsTests
{
    [Fact]
    public void Usypiana_tylko_nieuzywana_karta_bez_dzwieku()
    {
        Assert.True(MainWindow.ShouldSleepTab(30, 31, false, false, false));
        Assert.False(MainWindow.ShouldSleepTab(0, 500, false, false, false));    // wylaczone (domyslnie) - nic nie zasypia
        Assert.False(MainWindow.ShouldSleepTab(30, 29, false, false, false));    // jeszcze nie minal czas
        Assert.False(MainWindow.ShouldSleepTab(30, 99, true, false, false));     // ogladana karta
        Assert.False(MainWindow.ShouldSleepTab(30, 99, false, true, false));     // przypieta (poczta, komunikator) - zawsze gotowa
        Assert.False(MainWindow.ShouldSleepTab(30, 99, false, false, true));     // gra muzyka / film / czytanie na glos
    }
}
