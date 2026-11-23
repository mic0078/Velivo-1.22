using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VelivoProcesor
{
    // 🤖 okno „Jak myśli AI” (makieta dokumentacja/makieta-procesor-ai.html): co decyduje teraz, głosy z trafnością, przewidywanie i wzorzec dnia, nauka programów.
    // Odświeżane co sekundę tylko, gdy okno jest otwarte; tabela programów tylko przy zmianie.
    sealed partial class Okno
    {
        Window aiWin; Action aiOdswiez;
        static Brush KolorT(int t) => t == 2 ? Czerwony : t == 1 ? Zolty : Zielony;
        static readonly string[] TrybN = { "Cichy", "Zrównoważony", "Turbo" };

        void OknoAi()
        {
            if (aiWin != null) { aiWin.Activate(); return; }
            aiWin = new Window { Title = "Velivo Procesor – jak myśli AI", Width = 860, Height = 720, Background = Bg, Owner = win.IsVisible ? win : null, WindowStartupLocation = WindowStartupLocation.CenterScreen, Icon = win.Icon };
            string sig = null; var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(T("🤖 Jak myśli AI", 18, Fg, FontWeights.SemiBold));
            root.Children.Add(T("Auto wybiera tryb głosowaniem kilku „głosów”. Każdy głos ma trafność – jak często miał rację (sprawdzane chwilę później: przycięcie = trzeba było Turbo, spokój = wystarczał Cichy). Zasady bezpieczeństwa (temperatura, duże obciążenie, „zawsze ten tryb”) mają pierwszeństwo.", 12, Dim));
            var teraz = T("", 13.5, B(0xCF, 0xE3, 0xFF)); root.Children.Add(new Border { Background = B(0x0E, 0x21, 0x40), BorderBrush = B(0x1E, 0x3A, 0x66), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 10, 0, 6), Child = teraz });
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition()); root.Children.Add(g);
            // głosy
            var glosy = new StackPanel(); var gb = new StackPanel(); gb.Children.Add(T("🗳 Głosy teraz", 13, Fg, FontWeights.SemiBold)); gb.Children.Add(glosy);
            gb.Children.Add(T("pasek = trafność głosu, sama się dostraja", 11, Dim)); var bxg = Box(gb); Grid.SetColumn(bxg, 0); g.Children.Add(bxg);
            // przewidywanie + wzorzec dnia
            var prz = T("", 13); var dzien = new Canvas { Height = 46, Margin = new Thickness(0, 8, 0, 2) };
            var pb2 = new StackPanel(); pb2.Children.Add(T("🔮 Przewidywanie", 13, Fg, FontWeights.SemiBold)); pb2.Children.Add(prz); pb2.Children.Add(dzien);
            pb2.Children.Add(T("dzisiejszy dzień tygodnia: zwykłe obciążenie w każdej godzinie (0–23, wyżej = więcej pracy)", 11, Dim)); var bxp = Box(pb2); Grid.SetColumn(bxp, 1); g.Children.Add(bxp);
            // programy
            var lv = new ListView { Background = Card, Foreground = Fg, BorderBrush = Line, Height = 260 };
            var gv = new GridView(); lv.View = gv;
            foreach (var (h, p, w) in new[] { ("Program", "Prog", 220.0), ("Cichy", "C", 80.0), ("Zrównoważony", "Z", 100.0), ("Turbo", "Tu", 80.0), ("Wybiera", "W", 120.0), ("Próbek", "N", 80.0) })
                gv.Columns.Add(new GridViewColumn { Header = h, DisplayMemberBinding = new System.Windows.Data.Binding(p), Width = w });
            var pp = new StackPanel(); pp.Children.Add(T("🎓 Nauka skutków – programy", 13, Fg, FontWeights.SemiBold)); pp.Children.Add(lv);
            pp.Children.Add(T("wyżej = lepszy skutek (cicho bez przycięć); Twój ręczny wybór przy programie liczy się mocno; „Wybiera” po 2 min pracy programu", 11, Dim));
            var row = new WrapPanel();
            row.Children.Add(Btn("🗑 Zapomnij naukę zaznaczonego programu", () => { if (lv.SelectedItem is AiRow r) { s.ZapomnijOcene(r.Prog); sig = null; aiOdswiez(); } }, "Program uczy się od nowa"));
            row.Children.Add(Btn("↺ Wyzeruj trafność głosów", () => { s.ZerujTrafnosc(); aiOdswiez(); }, "Głosy zaczynają z równą trafnością"));
            pp.Children.Add(row); root.Children.Add(Box(pp, new Thickness(0, 6, 0, 0)));
            aiWin.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            aiOdswiez = () =>
            {
                var a = s.StanAi();
                teraz.Text = "💡 " + s.Dlaczego;
                glosy.Children.Clear();
                for (int i = 0; i < a.Nazwy.Length; i++)
                {
                    var gr = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                    foreach (var w in new[] { 125.0, 100.0, 1.0, 44.0 }) gr.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
                    var n = T(a.Nazwy[i], 12.5); gr.Children.Add(n);
                    var m = T(a.Glos[i] >= 0 ? TrybN[a.Glos[i]] : "brak zdania", 12, a.Glos[i] >= 0 ? KolorT(a.Glos[i]) : Dim); Grid.SetColumn(m, 1); gr.Children.Add(m);
                    var bar = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }; bar.Children.Add(new Border { Background = Sep, CornerRadius = new CornerRadius(4) });
                    bar.Children.Add(new Border { Background = Vi, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(2, a.Traf[i] * 150) }); Grid.SetColumn(bar, 2); gr.Children.Add(bar);
                    var pc = T((a.Traf[i] * 100).ToString("0") + "%", 12, Dim); Grid.SetColumn(pc, 3); gr.Children.Add(pc);
                    glosy.Children.Add(gr);
                }
                prz.Text = "Teraz: " + a.Teraz.ToString("0") + " % · za 5 s: " + a.Przew.ToString("0") + " %" + (a.Pora >= 0 ? "\nO tej porze (" + DateTime.Now.ToString("ddd HH") + ":00) zwykle: " + a.Pora.ToString("0") + " %" : "\nWzorzec tej pory – jeszcze się uczy");
                dzien.Children.Clear(); int dt = (int)DateTime.Now.DayOfWeek; double szer = Math.Max(240, dzien.ActualWidth); double kol = szer / 24;
                for (int h = 0; h < 24; h++)
                {
                    double v = a.Tydz[dt, h]; var r = new Rectangle { Width = Math.Max(2, kol - 2), Height = Math.Max(2, Math.Min(100, v) / 100 * 40), Fill = h == DateTime.Now.Hour ? Tq : Vi, RadiusX = 2, RadiusY = 2 };
                    Canvas.SetLeft(r, h * kol); Canvas.SetTop(r, 42 - r.Height); dzien.Children.Add(r);
                }
                var ns = string.Join("|", a.Programy.Select(x => x.Prog + x.N / 30));
                if (ns != sig)
                {
                    sig = ns; var zazn = (lv.SelectedItem as AiRow)?.Prog;
                    lv.ItemsSource = a.Programy.Select(x => { int b = 0; for (int i = 1; i < 3; i++) if (x.Q[i] > x.Q[b] + 0.2) b = i;
                        return new AiRow { Prog = x.Prog, C = x.Q[0].ToString("+0.00;−0.00"), Z = x.Q[1].ToString("+0.00;−0.00"), Tu = x.Q[2].ToString("+0.00;−0.00"), W = x.N >= 60 ? TrybN[b] : "uczy się", N = x.N.ToString("N0") }; }).ToList();
                    if (zazn != null) lv.SelectedItem = lv.Items.Cast<AiRow>().FirstOrDefault(r => r.Prog == zazn);
                }
            };
            aiWin.Closed += (o, e) => { aiWin = null; aiOdswiez = null; };
            aiWin.Loaded += (o, e) => aiOdswiez();
            aiWin.Show();
        }
        public sealed class AiRow { public string Prog { get; set; } public string C { get; set; } public string Z { get; set; } public string Tu { get; set; } public string W { get; set; } public string N { get; set; } }
    }
}
