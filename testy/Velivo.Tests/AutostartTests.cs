using System.Collections.Generic;
using Velivo;
using Xunit;

// Autostart z Windows z opoznieniem: wpis w Run = "Velivo.exe" --autostart N; przy starcie Velivo czeka N s.
public class AutostartTests
{
    [Fact]
    public void PolecenieZawieraOpoznienie()
    {
        Assert.Equal("\"C:\\Program Files\\Velivo\\Velivo.exe\" --autostart 30", MainWindow.AutostartCommand(@"C:\Program Files\Velivo\Velivo.exe", 30));
    }

    [Fact]
    public void OdczytOpoznieniaZArgumentow()
    {
        var a = new List<string> { "--autostart", "60" };
        Assert.Equal(60, MainWindow.TakeAutostartArg(a));
        Assert.Empty(a);   // argumenty nie trafiaja do kart jako adresy
        Assert.Equal(-1, MainWindow.TakeAutostartArg(new List<string> { "https://example.com" }));
        Assert.Equal(0, MainWindow.TakeAutostartArg(new List<string> { "--autostart" }));
        Assert.Equal(600, MainWindow.TakeAutostartArg(new List<string> { "--autostart", "99999" }));   // nie dluzej niz 10 min
    }
}
