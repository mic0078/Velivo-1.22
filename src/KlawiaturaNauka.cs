using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // 📒 „Czego nauczyła się klawiatura” (makieta dokumentacja/makieta-klawiatura-nauka.html): poprawki, „nie poprawiaj”, zwroty, Moje słowa -
    // każdą pozycję można usunąć osobno; jeden przycisk sprząta zamiany poprawnych słów (błąd starej wersji: taka → taką)
    public partial class MainWindow
    {
        static Window _naukaWin;
        internal void KlawiaturaNaukaOkno()
        {
            if (_naukaWin != null) { _naukaWin.Activate(); return; }
            var w = _naukaWin = new Window { Title = L.T("Czego nauczyła się klawiatura"), Width = 700, Height = 620, Background = SBg, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            w.Closed += (s, e) => _naukaWin = null;
            var root = new DockPanel { Margin = new Thickness(16) }; w.Content = root;
            var gora = new StackPanel(); DockPanel.SetDock(gora, Dock.Top); root.Children.Add(gora);
            gora.Children.Add(STxt(L.T("📒 Czego nauczyła się klawiatura"), 17, null, true));
            var op = STxt(L.T("Wszystko, czego klawiatura nauczyła się z Twojego pisania. Złą rzecz usuń osobno – reszta nauki zostaje."), 12, SDim); op.TextWrapping = TextWrapping.Wrap; gora.Children.Add(op);
            var zak = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) }; gora.Children.Add(zak);
            var szukaj = new TextBox { Background = SField, Foreground = SFg, CaretBrush = SFg, BorderBrush = SLine, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 6), ToolTip = L.T("🔍 Szukaj") }; gora.Children.Add(szukaj);
            var dol = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(dol, Dock.Bottom); root.Children.Add(dol);
            var info = STxt("", 12, SDim); DockPanel.SetDock(info, Dock.Bottom); root.Children.Add(info);
            var lista = new StackPanel(); root.Children.Add(new ScrollViewer { Content = lista, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            string tab = "fix"; var tabs = new Dictionary<string, Button>();
            List<string> NieKor() => (GetPref("klawiatura-bez-korekty") ?? "").Split('|').Where(x => x.Length > 0).ToList();
            void Zapisz() { lock (_swLearned) SwipeTyping.SaveLearned(SwipeFile, _swLearned, _swOwn); }
            Action fill = null;
            void Wiersz(string lewa, string prawa, Brush kol, string opis, Action usun)
            {
                var r = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                if (usun != null) { var x = SBtnMake(L.T("Usuń"), () => { usun(); fill(); }); x.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(x, Dock.Right); r.Children.Add(x); }
                var t = new TextBlock { FontSize = 13, Foreground = SFg, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                t.Inlines.Add(new System.Windows.Documents.Run(lewa));
                if (prawa != null) { t.Inlines.Add(new System.Windows.Documents.Run("  →  ") { Foreground = SDim }); t.Inlines.Add(new System.Windows.Documents.Run(prawa) { Foreground = kol ?? SFg, FontWeight = FontWeights.SemiBold }); }
                if (opis != null) t.Inlines.Add(new System.Windows.Documents.Run("   " + opis) { Foreground = SDim, FontSize = 11.5 });
                r.Children.Add(t); lista.Children.Add(r);
            }
            fill = () =>
            {
                lista.Children.Clear(); var q = szukaj.Text.Trim().ToLowerInvariant(); bool Pas(string x) => q.Length == 0 || x.ToLowerInvariant().Contains(q);
                var popr = SwipeTyping.NaukaPoprawki(); var nk = NieKor(); var zw = SwipeTyping.NaukaZwroty(); List<string> moje; lock (_swLearned) moje = _swOwn.OrderBy(x => x).ToList();
                tabs["fix"].Content = "✏ " + L.T("Poprawki") + " (" + popr.Count + ")"; tabs["no"].Content = "🚫 " + L.T("Nie poprawiaj") + " (" + nk.Count + ")";
                tabs["zw"].Content = "💬 " + L.T("Zwroty") + " (" + zw.Count + ")"; tabs["moje"].Content = "📖 " + L.T("Moje słowa") + " (" + moje.Count + ")";
                foreach (var kv in tabs) kv.Value.Background = kv.Key == tab ? SAccent : SBtn;
                int n = 0;
                if (tab == "fix") foreach (var (t, f) in popr.Where(x => Pas(x.Typo) || Pas(x.Fix))) { bool zla = SwipeTyping.ZamianaPoprawnego(t); var tt = t; Wiersz(t, f, zla ? Brushes.IndianRed : Brushes.LightGreen, zla ? L.T("(poprawne słowo zamieniane – prawdopodobnie błąd)") : L.T("(poprawiłeś sam)"), () => { SwipeTyping.ZapomnijPoprawke(tt); Zapisz(); }); if (++n >= 500) break; }
                if (tab == "no") foreach (var x in nk.Where(Pas)) { var xx = x; Wiersz(x, null, null, L.T("(cofnięta poprawka – klawiatura tego nie zmienia)"), () => SetPref("klawiatura-bez-korekty", string.Join("|", NieKor().Where(y => y != xx)))); if (++n >= 500) break; }
                if (tab == "zw") foreach (var (k, c) in zw.Where(x => Pas(x.Key))) { var i = k.LastIndexOf(' '); var kk = k; Wiersz(k.Substring(0, Math.Max(0, i)), k.Substring(i + 1), Brushes.LightSkyBlue, "(" + c + "×)", () => { SwipeTyping.ZapomnijZwrot(kk); Zapisz(); }); if (++n >= 500) break; }
                if (tab == "moje") foreach (var x in moje.Where(Pas)) { var xx = x; Wiersz(x, null, null, null, () => { lock (_swLearned) { _swOwn.Remove(xx); SwipeTyping.RemoveUserWord(xx); } Zapisz(); }); if (++n >= 500) break; }
                info.Text = n >= 500 ? L.T("Pokazane pierwsze 500 – użyj szukania.") : n == 0 ? L.T("Pusto.") : "";
            };
            foreach (var (k, txt) in new[] { ("fix", ""), ("no", ""), ("zw", ""), ("moje", "") }) { var kk = k; var b = SBtnMake(txt, () => { tab = kk; fill(); }); tabs[k] = b; zak.Children.Add(b); }
            szukaj.TextChanged += (s, e) => fill();
            dol.Children.Add(SBtnMake(L.T("🧹 Usuń wszystkie zamiany poprawnych słów"), () =>
            {
                var zle = SwipeTyping.NaukaPoprawki().Where(x => SwipeTyping.ZamianaPoprawnego(x.Typo)).ToList();
                foreach (var (t, _) in zle) SwipeTyping.ZapomnijPoprawke(t); Zapisz(); info.Text = L.T("Usunięto: ") + zle.Count; fill();
            }, SAccent));
            fill(); w.Show();
        }
    }
}
