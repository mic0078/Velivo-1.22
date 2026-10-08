using Przegladarka;
using Xunit;

public class ExtensionIsolationTests
{
    [Fact]
    public void Dodatek_nie_dostaje_karty_bankowej_ani_prywatnej()
    {
        Assert.True(MainWindow.ExtensionMayUseTab(false, false));    // zwykla karta - jak w Chrome
        Assert.False(MainWindow.ExtensionMayUseTab(true, false));    // tryb bankowy calkowicie odizolowany
        Assert.False(MainWindow.ExtensionMayUseTab(false, true));    // karta prywatna - jak w Chrome (incognito bez dodatkow)
    }
}
