using System.IO;
using Przegladarka;
using Xunit;

public class TrackingSettingTests
{
    [Fact]
    public void Ochrona_przed_sledzeniem_trzy_poziomy_domyslnie_zrownowazona()
    {
        var dir = Path.Combine(Path.GetTempPath(), "velivo-track-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal("balanced", AppSettings.Load(dir).Tracking);   // domyslnie jak bylo
            foreach (var m in new[] { "strict", "none", "balanced" })
            {
                var s = AppSettings.Load(dir); s.Tracking = m; s.Save(dir);
                Assert.Equal(m, AppSettings.Load(dir).Tracking);
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
