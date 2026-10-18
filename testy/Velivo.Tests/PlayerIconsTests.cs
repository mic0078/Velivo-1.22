using System;
using System.Threading;
using Velivo;
using Xunit;

// Odtwarzacz filmow: wszystkie ikony (wektorowe) i styl przyciskow wczytuja sie w WPF - blad w danych ikony wyszedlby dopiero po otwarciu filmu.
public class PlayerIconsTests
{
    [Fact]
    public void IkonyIStylPrzyciskow()
    {
        Exception err = null;
        var t = new Thread(() =>
        {
            try
            {
                foreach (var name in MainWindow.PlayerIcons.Keys)
                {
                    var p = MainWindow.PlayerIcon(name);
                    Assert.False(p.Data.Bounds.IsEmpty, "pusta ikona: " + name);
                }
                Assert.NotNull(MainWindow.PlayerButtonStyle());
            }
            catch (Exception ex) { err = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        Assert.Null(err);
    }
}
