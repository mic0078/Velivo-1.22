using System.IO;
using Przegladarka;
using Xunit;

public class DefaultBrowserAskTests
{
    [Fact]
    public void Pytanie_o_domyslna_przegladarke_tylko_raz()
    {
        var dir = Path.Combine(Path.GetTempPath(), "velivo-ask-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var s = AppSettings.Load(dir);
            Assert.False(s.AskedDefaultBrowser);   // nowa instalacja - Velivo zapyta
            s.AskedDefaultBrowser = true;
            s.Save(dir);
            Assert.True(AppSettings.Load(dir).AskedDefaultBrowser);   // po pytaniu - juz nigdy samo
        }
        finally { Directory.Delete(dir, true); }
    }
}
