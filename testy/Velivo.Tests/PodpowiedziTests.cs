using Przegladarka;
using Xunit;

// Podpowiedzi z jednego slownika: dokladny napis, napis zmienny (po poczatku), brak dla nieznanych.
public class PodpowiedziTests
{
    [Fact]
    public void PodpowiedzWgNapisu()
    {
        Assert.Contains("kopi", AutoTips.For("♻ Przywróć z kopii…"));
        Assert.Contains("Wznawia", AutoTips.For("▶ Wznów od 12:34"));   // napis z czasem
        Assert.Null(AutoTips.For("Jakiś nieznany przycisk"));
        Assert.Null(AutoTips.For(""));
    }
}
