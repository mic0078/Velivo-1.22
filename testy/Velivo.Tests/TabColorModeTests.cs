using System.Text.Json;
using Przegladarka;
using Xunit;

public class TabColorModeTests
{
    [Fact]
    public void Tryb_strony_ustawiany_w_silniku_karty()
    {
        var dark = MainWindow.TabColorCommands(true);
        var light = MainWindow.TabColorCommands(false);
        Assert.Equal("Emulation.setEmulatedMedia", dark[0][0]);
        Assert.Equal("dark", JsonDocument.Parse(dark[0][1]).RootElement.GetProperty("features")[0].GetProperty("value").GetString());
        Assert.Equal("light", JsonDocument.Parse(light[0][1]).RootElement.GetProperty("features")[0].GetProperty("value").GetString());   // jasny = jasny, nawet przy ciemnym Windows
        Assert.Equal("Emulation.setAutoDarkModeOverride", dark[1][0]);
        Assert.True(JsonDocument.Parse(dark[1][1]).RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(JsonDocument.Parse(light[1][1]).RootElement.GetProperty("enabled").GetBoolean());
    }
}
