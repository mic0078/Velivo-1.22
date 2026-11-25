using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VelivoProcesor
{
    // 🤖 okno „Jak myśli AI” (makieta dokumentacja/makieta-procesor-ai-2.html): co decyduje teraz, głosy z opisem i trafnością, przewidywanie i wzorzec dnia,
    // tabela nauki programów - wypełnia okno, przewija się, sortowanie (zapamiętane), szukanie, prawy przycisk jak na liście programów.
    // Odświeżane co sekundę tylko, gdy okno jest otwarte; tabela tylko przy zmianie.
    sealed partial class Okno
    {
        Window aiWin; Action aiOdswiez;
        static Brush KolorT(int t) => t == 2 ? Czerwony : t == 1 ? Zolty : Zielony;
        static readonly string[] TrybN = { "Cichy", "Zrównoważony", "Turbo" };
        static readonly string[] GlosOpis =
        {
            "ocena trybów dla programu ze skutków: cisza bez przycięć +, przycięcie −, ciepło −; głosuje po 1 min pracy programu",
            "zwykłe i szczytowe obciążenie programu (ok. 10 min) → tryb; głosuje po 30 s",
            "obciążenie za 5 s z ostatnich 30 s (średnia + trend), próg z przerwą 4 pkt",
            "zwykłe obciążenie w tej godzinie i dniu tygodnia; głosuje po 10 min danych",
        };
        const int Prog = 60;   // próbki (sekundy) do własnego zdania nauki skutków

        void OknoAi()
        {
            if (aiWin != null) { aiWin.Activate(); return; }
            aiWin = new Window { Title = "Velivo Procesor – jak myśli AI", Width = 880, Height = 780, MinWidth = 640, MinHeight = 520, Background = Bg, WindowStartupLocation = WindowStartupLocation.CenterScreen, Icon = win.Icon };
            var root = new Grid { Margin = new Thickness(14) };
            for (int i = 0; i < 4; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            void Wiersz(UIElement e, int r) { Grid.SetRow(e, r); root.Children.Add(e); }
            var gora = new StackPanel(); gora.Children.Add(T("🤖 Jak myśli AI", 18, Fg, FontWeights.SemiBold));
            gora.Children.Add(T("Auto wybiera tryb głosowaniem. Każdy głos ma trafność – jak często miał rację (sprawdzane chwilę później: przycięcie = trzeba było Turbo, spokój = wystarczał Cichy). Zasady bezpieczeństwa (temperatura, duże obciążenie, „zawsze ten tryb”) mają pierwszeństwo.", 12, Dim));
            Wiersz(gora, 0);
            var teraz = T("", 13.5, B(0xCF, 0xE3, 0xFF));
            Wiersz(new Border { Background = B(0x0E, 0x21, 0x40), BorderBrush = B(0x1E, 0x3A, 0x66), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 10, 0, 6), Child = teraz }, 1);
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) }); g.ColumnDefinitions.Add(new ColumnDefinition()); Wiersz(g, 2);
            // głosy z opisem, jak decydują
            var glosy = new StackPanel(); var gb = new StackPanel(); gb.Children.Add(T("🗳 Głosy – co który mówi i jak decyduje", 13, Fg, FontWeights.SemiBold)); gb.Children.Add(glosy);
            gb.Children.Add(T("pasek = trafność (jak często głos miał rację); wygrywa suma trafności, remis → cichszy, Turbo – min. 2 głosy", 11, Dim));
            var bxg = Box(gb, new Thickness(0, 0, 4, 0)); Grid.SetColumn(bxg, 0); g.Children.Add(bxg);
            var wGlos = new (TextBlock M, Border Bar, TextBlock Pc)[GlosOpis.Length];
            for (int i = 0; i < GlosOpis.Length; i++)
            {
                var gr = new Grid { Margin = new Thickness(0, 5, 0, 0) };
                foreach (var w in new[] { 118.0, 100.0, 1.0, 40.0 }) gr.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
                var n = T("", 12.5); gr.Children.Add(n); n.Tag = i;
                var m = T("", 12); Grid.SetColumn(m, 1); gr.Children.Add(m);
                var bar = new Grid { Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }; bar.Children.Add(new Border { Background = Sep, CornerRadius = new CornerRadius(4) });
                var fill = new Border { Background = Vi, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left }; bar.Children.Add(fill); Grid.SetColumn(bar, 2); gr.Children.Add(bar);
                var pc = T("", 12, Dim); Grid.SetColumn(pc, 3); gr.Children.Add(pc);
                glosy.Children.Add(gr); var op = T(GlosOpis[i], 11, Dim); op.Margin = new Thickness(2, 0, 0, 0); glosy.Children.Add(op);
                wGlos[i] = (m, fill, pc); n.Text = new[] { "nauka skutków", "profil programu", "przewidywanie", "pora dnia" }[i];
            }
            // przewidywanie + wzorzec dnia
            var prz = T("", 13); var dzien = new Canvas { Height = 46, Margin = new Thickness(0, 8, 0, 2) };
            var pb2 = new StackPanel(); pb2.Children.Add(T("🔮 Przewidywanie", 13, Fg, FontWeights.SemiBold)); pb2.Children.Add(prz); pb2.Children.Add(dzien);
            pb2.Children.Add(T("dziś: zwykłe obciążenie w każdej godzinie (0–23, wyżej = więcej pracy, turkus = teraz)", 11, Dim)); var bxp = Box(pb2, new Thickness(4, 0, 0, 0)); Grid.SetColumn(bxp, 1); g.Children.Add(bxp);
            // programy: szukanie, przyciski, tabela na całą resztę okna
            var pg = new Grid(); for (int i = 0; i < 4; i++) pg.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var tyt = T("🎓 Nauka skutków – programy", 13, Fg, FontWeights.SemiBold); pg.Children.Add(tyt);
            var narz = new DockPanel { Margin = new Thickness(0, 6, 0, 6) }; Grid.SetRow(narz, 1); pg.Children.Add(narz);
            var lv = new ListView { Background = Card, Foreground = Fg, BorderBrush = Line, ItemContainerStyle = WierszStyl(), SelectionMode = SelectionMode.Extended }; Grid.SetRow(lv, 2); pg.Children.Add(lv);
            string sig = null;
            var b1 = Btn("🗑 Zapomnij zaznaczone", () => { var zz = lv.SelectedItems.OfType<AiRow>().Select(r => r.Prog).ToList(); if (zz.Count == 0) return; foreach (var g in zz) s.ZapomnijOcene(g); sig = null; aiOdswiez(); }, "Zaznaczone programy uczą się od nowa (Ctrl / Shift – kilka naraz)"); b1.Margin = new Thickness(6, 0, 0, 0);
            var b2 = Btn("↺ Wyzeruj trafność", () => { s.ZerujTrafnosc(); aiOdswiez(); }, "Głosy zaczynają z równą trafnością"); b2.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(b2, Dock.Right); DockPanel.SetDock(b1, Dock.Right); narz.Children.Add(b2); narz.Children.Add(b1);
            var szukaj = new TextBox { Background = B(0x0F, 0x13, 0x1B), Foreground = Fg, CaretBrush = Fg, BorderBrush = Line, Padding = new Thickness(6, 3, 6, 3), ToolTip = "🔍 Szukaj programu (nazwa)" };
            narz.Children.Add(szukaj);
            pg.Children.Add(new Func<UIElement>(() => { var d = T("klik w nagłówek – sortowanie (drugi klik odwrotnie, zapamiętane) · wyżej = lepszy skutek · prawy przycisk – Zawsze ten tryb, Zapomnij…", 11, Dim); d.Margin = new Thickness(0, 4, 0, 0); Grid.SetRow(d, 3); return d; })());
            var pcard = Box(pg, new Thickness(0, 8, 0, 0)); Wiersz(pcard, 3);
            // kolumny z sortowaniem
            var gv = new GridView { ColumnHeaderContainerStyle = NaglowekStyl() }; lv.View = gv;
            var kol = new[] { ("Program", "Prog", "prog", 200.0), ("Cichy", "C", "c", 70.0), ("Zrównoważony", "Z", "z", 100.0), ("Turbo", "Tu", "t", 70.0), ("Wybiera", "W", "w", 140.0), ("Próbek", "N", "n", 70.0), ("Ostatnio", "O", "o", 120.0) };
            var sp = (s.Get("ai-sort") ?? "n,desc").Split(','); string sortK = sp[0]; bool malej = sp.Length < 2 || sp[1] == "desc";
            void Naglowki() { foreach (var c in gv.Columns) { var h = (GridViewColumnHeader)c.Header; var nm = ((string)h.Content).TrimEnd(' ', '▲', '▼'); h.Content = nm + ((string)h.Tag == sortK ? (malej ? " ▼" : " ▲") : ""); } }
            foreach (var (h, p, k, w) in kol)
            {
                var hd = new GridViewColumnHeader { Content = h, Tag = k };
                hd.Click += (o, e) => { var kk = (string)hd.Tag; if (kk == sortK) malej = !malej; else { sortK = kk; malej = kk != "prog" && kk != "w"; } s.Put("ai-sort", sortK + "," + (malej ? "desc" : "asc")); Naglowki(); sig = null; aiOdswiez(); };
                gv.Columns.Add(new GridViewColumn { Header = hd, DisplayMemberBinding = new System.Windows.Data.Binding(p), Width = w });
            }
            Naglowki();
            szukaj.TextChanged += (o, e) => { sig = null; aiOdswiez(); };
            lv.ContextMenuOpening += (o, e) => { var it = ItemsControl.ContainerFromElement(lv, e.OriginalSource as DependencyObject) as ListViewItem; if (it?.DataContext is AiRow r) { it.IsSelected = true; lv.ContextMenu = MenuProgramu(r.Prog); } else e.Handled = true; };
            aiWin.Content = root;
            aiOdswiez = () =>
            {
                var a = s.StanAi();
                teraz.Text = "💡 " + s.Dlaczego;
                for (int i = 0; i < wGlos.Length && i < a.Glos.Length; i++)
                {
                    wGlos[i].M.Text = a.Glos[i] >= 0 ? TrybN[a.Glos[i]] : "brak zdania"; wGlos[i].M.Foreground = a.Glos[i] >= 0 ? KolorT(a.Glos[i]) : Dim;
                    wGlos[i].Bar.Width = Math.Max(2, a.Traf[i] * 160); wGlos[i].Pc.Text = (a.Traf[i] * 100).ToString("0") + "%";
                }
                prz.Text = "Teraz: " + a.Teraz.ToString("0") + " % · za 5 s: " + a.Przew.ToString("0") + " %" + (a.Pora >= 0 ? "\nO tej porze (" + DateTime.Now.ToString("ddd HH") + ":00) zwykle: " + a.Pora.ToString("0") + " %" : "\nWzorzec tej pory – jeszcze się uczy");
                dzien.Children.Clear(); int dt = (int)DateTime.Now.DayOfWeek; double kolW = Math.Max(240, dzien.ActualWidth) / 24;
                for (int h = 0; h < 24; h++)
                {
                    var r = new Rectangle { Width = Math.Max(2, kolW - 2), Height = Math.Max(2, Math.Min(100, a.Tydz[dt, h]) / 100 * 40), Fill = h == DateTime.Now.Hour ? Tq : Vi, RadiusX = 2, RadiusY = 2 };
                    Canvas.SetLeft(r, h * kolW); Canvas.SetTop(r, 42 - r.Height); dzien.Children.Add(r);
                }
                var q = szukaj.Text.Trim();
                var ns = q + "|" + string.Join("|", a.Programy.Select(x => x.Prog + x.N / 30));
                if (ns == sig) return;
                sig = ns; var zazn = new HashSet<string>(lv.SelectedItems.OfType<AiRow>().Select(r => r.Prog));
                var rows = a.Programy.Where(x => q.Length == 0 || x.Prog.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Select(x =>
                {
                    int b = 0; for (int i = 1; i < 3; i++) if (x.Q[i] > x.Q[b] + 0.2) b = i;
                    return new AiRow { Prog = x.Prog, Qs = x.Q, Nn = x.N, Ost = x.Ost, Wyb = x.N >= Prog ? b : -1,
                        C = x.Q[0].ToString("+0.00;−0.00"), Z = x.Q[1].ToString("+0.00;−0.00"), Tu = x.Q[2].ToString("+0.00;−0.00"),
                        W = x.N >= Prog ? TrybN[b] : "uczy się (" + x.N + "/" + Prog + ")", N = x.N.ToString("N0"), O = Kiedy(x.Ost) };
                });
                Func<AiRow, IComparable> key = sortK == "prog" ? r => r.Prog.ToLowerInvariant() : sortK == "c" ? r => r.Qs[0] : sortK == "z" ? r => r.Qs[1] : sortK == "t" ? r => r.Qs[2] : sortK == "w" ? r => r.Wyb : sortK == "o" ? r => r.Ost : (Func<AiRow, IComparable>)(r => r.Nn);
                var lst = (malej ? rows.OrderByDescending(key) : rows.OrderBy(key)).ToList();
                lv.ItemsSource = lst;
                tyt.Text = "🎓 Nauka skutków – programy (" + lst.Count + (q.Length > 0 ? " z " + a.Programy.Count : "") + ")";
                foreach (var r0 in lst) if (zazn.Contains(r0.Prog)) lv.SelectedItems.Add(r0);
            };
            aiWin.Closed += (o, e) => { aiWin = null; aiOdswiez = null; };
            aiWin.Loaded += (o, e) => aiOdswiez();
            aiWin.Show();
        }
        static string Kiedy(DateTime t)
        {
            if (t == DateTime.MinValue) return "–";
            var d = DateTime.Now - t;
            return d.TotalMinutes < 2 ? "przed chwilą" : d.TotalMinutes < 60 ? (int)d.TotalMinutes + " min temu" : t.Date == DateTime.Today ? "dziś " + t.ToString("HH:mm") : t.Date == DateTime.Today.AddDays(-1) ? "wczoraj" : t.ToString("d.MM");
        }
        public sealed class AiRow
        {
            public string Prog { get; set; } public string C { get; set; } public string Z { get; set; } public string Tu { get; set; } public string W { get; set; } public string N { get; set; } public string O { get; set; }
            public double[] Qs; public int Nn, Wyb; public DateTime Ost;
        }
    }
}
