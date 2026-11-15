using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Velivo.Tests
{
    // 🧩 Velivo, Velivo Odtwarzacz i Velivo Przeglądarka na jednym folderze danych: zapis jednego nie kasuje zmian drugiego
    public class WspolneDaneTests
    {
        static KeyValuePair<string, string> P(string k, string v) => new KeyValuePair<string, string>(k, v);

        [Fact]
        public void DwaProgramyZmieniajaRozneUstawienia_ObieZmianyZostaja()
        {
            var dir = Path.Combine(Path.GetTempPath(), "velivo-wspolne-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, "ustawienia.txt");
            File.WriteAllLines(f, new[] { "home=a", "lektor=0" });
            var start = new Dictionary<string, string> { ["home"] = "a", ["lektor"] = "0" };
            // Velivo zmienia stronę startową
            var poVelivo = VelivoData.MergeWrite(f, '=', new[] { P("home", "b"), P("lektor", "0") }, start);
            // Odtwarzacz (stara kopia w pamięci: home=a) zmienia lektora
            var poOdtw = VelivoData.MergeWrite(f, '=', new[] { P("home", "a"), P("lektor", "1") }, start);
            Assert.Equal("b", poOdtw["home"]);    // zmiana Velivo nie zginęła
            Assert.Equal("1", poOdtw["lektor"]);  // zmiana Odtwarzacza zapisana
            Assert.Contains("home=b", File.ReadAllLines(f));
            Assert.Contains("lektor=1", File.ReadAllLines(f));
            Directory.Delete(dir, true);
        }

        [Fact]
        public void UsunietyKlucz_ZnikaTylkoGdyNiktGoNieZmienil()
        {
            var dir = Path.Combine(Path.GetTempPath(), "velivo-wspolne-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, "okna.txt");
            File.WriteAllLines(f, new[] { "x\t1", "y\t2" });
            var start = new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" };
            var r = VelivoData.MergeWrite(f, '\t', new[] { P("x", "1") }, start);   // ten program usunął y
            Assert.False(r.ContainsKey("y"));
            File.WriteAllLines(f, new[] { "x\t1", "y\t5" });   // inny program w międzyczasie zmienił y
            r = VelivoData.MergeWrite(f, '\t', new[] { P("x", "1") }, start);
            Assert.Equal("5", r["y"]);
            Directory.Delete(dir, true);
        }
    }
}

namespace Velivo.Tests
{
    public class WspolnaBibliotekaTests
    {
        static MainWindow.LibraryVideo F(string p, long pos = 0) => new MainWindow.LibraryVideo { Path = p, Title = p, Pos = pos, Shared = false };

        [Fact]
        public void PostepZInnegoProgramu_NieGinie_NowyFilmZostaje_UsunietyZnika()
        {
            var baza = new List<MainWindow.LibraryVideo> { F(@"C:\a.mkv"), F(@"C:\b.mkv"), F(@"C:\c.mkv") };
            // ten program (stara lista): zmienił postęp a, usunął c
            var moje = new List<MainWindow.LibraryVideo> { F(@"C:\a.mkv", 100), F(@"C:\b.mkv") };
            // inny program w międzyczasie: postęp b, nowy film d
            var dysk = new List<MainWindow.LibraryVideo> { F(@"C:\a.mkv"), F(@"C:\b.mkv", 200), F(@"C:\c.mkv"), F(@"C:\d.mkv") };
            var w = MainWindow.MergeLibrary(moje, baza, dysk);
            Assert.Equal(100, w.Find(v => v.Path == @"C:\a.mkv").Pos);   // nasza zmiana
            Assert.Equal(200, w.Find(v => v.Path == @"C:\b.mkv").Pos);   // zmiana innego programu
            Assert.Null(w.Find(v => v.Path == @"C:\c.mkv"));              // usunięty przez nas
            Assert.NotNull(w.Find(v => v.Path == @"C:\d.mkv"));           // dodany przez inny program
        }
    }
}
