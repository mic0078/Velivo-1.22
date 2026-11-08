using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // 🎮 kontroler w odtwarzaczu filmów i TV: przypisania przycisków (domyślne jak w popularnych odtwarzaczach), własne układy
    public partial class MainWindow
    {
        // przycisk kontrolera -> maska bitowa XInput (spusty LT/RT jako bity 0x10000 / 0x20000)
        internal static readonly (string Id, string Name, int Mask)[] PadButtons =
        {
            ("A", "A", 0x1000), ("B", "B", 0x2000), ("X", "X", 0x4000), ("Y", "Y", 0x8000),
            ("Up", "↑", 0x0001), ("Down", "↓", 0x0002), ("Left", "←", 0x0004), ("Right", "→", 0x0008),
            ("LB", "LB", 0x0100), ("RB", "RB", 0x0200), ("LT", "LT", 0x10000), ("RT", "RT", 0x20000),
            ("Start", "Menu ≡", 0x0010), ("Back", "Widok ⧉", 0x0020), ("LS", "L3", 0x0040), ("RS", "R3", 0x0080),
        };

        internal static readonly (string Id, string Name)[] PadActions =
        {
            ("none", "— nic —"), ("play", "Odtwarzaj / pauza"), ("back", "Wstecz / zamknij menu"),
            ("seek-10", "Przewiń −10 s"), ("seek+10", "Przewiń +10 s"), ("seek-60", "Przewiń −60 s"), ("seek+60", "Przewiń +60 s"),
            ("vol+", "Głośniej"), ("vol-", "Ciszej"), ("mute", "Wycisz"),
            ("prev", "Poprzedni (kanał / film)"), ("next", "Następny (kanał / film)"),
            ("channels", "Lista kanałów"), ("info", "Pasek informacji"), ("subs", "Napisy (menu)"), ("audio", "Ścieżka dźwięku (menu)"),
            ("settings", "Ustawienia odtwarzacza"), ("full", "Pełny ekran"), ("kanapa", "Tryb Kanapa (włącz / wyłącz)"),
        };

        // domyślnie jak w odtwarzaczach na konsole / TV: A pauza, B wstecz, krzyżak przewija i zmienia głośność, LB/RB poprzedni/następny
        internal const string PadDefaultMap = "A=play;B=back;X=subs;Y=info;Left=seek-10;Right=seek+10;Up=vol+;Down=vol-;LB=prev;RB=next;LT=seek-60;RT=seek+60;Start=kanapa;Back=settings;LS=mute;RS=channels";

        internal static Dictionary<string, string> PadMap()
        {
            var d = ParsePadMap(PadDefaultMap);
            foreach (var kv in ParsePadMap(GetPref("pad-mapa") ?? "")) d[kv.Key] = kv.Value;
            return d;
        }

        static Dictionary<string, string> ParsePadMap(string s)
        {
            var d = new Dictionary<string, string>();
            foreach (var p in s.Split(';')) { int i = p.IndexOf('='); if (i > 0 && PadActions.Any(a => a.Id == p.Substring(i + 1))) d[p.Substring(0, i)] = p.Substring(i + 1); }
            return d;
        }

        static string PadMapText(Dictionary<string, string> d) => string.Join(";", PadButtons.Select(b => b.Id + "=" + (d.TryGetValue(b.Id, out var a) ? a : "none")));

        // zapisane układy: "nazwa|mapa" w kolejnych wierszach
        static List<(string Name, string Map)> PadLayouts() => (GetPref("pad-uklady") ?? "").Split('\n').Select(l => l.Split('|')).Where(p => p.Length == 2 && p[0].Length > 0).Select(p => (p[0], p[1])).ToList();
        static void PadLayoutsSave(List<(string Name, string Map)> l) => SetPref("pad-uklady", string.Join("\n", l.Select(x => x.Name.Replace("|", " ").Replace("\n", " ") + "|" + x.Map)));

        string PromptText(Window owner, string title, string label, string value)
        {
            var w = new Window { Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = owner?.Topmost == true };
            var p = new StackPanel { Margin = new Thickness(14) };
            p.Children.Add(new TextBlock { Text = label });
            var tb = new TextBox { Text = value ?? "", Width = 260, Margin = new Thickness(0, 6, 0, 10) };
            var ok = new Button { Content = L.T("OK"), IsDefault = true, Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Right };
            string res = null; ok.Click += (s, e) => { res = tb.Text; w.Close(); };
            p.Children.Add(tb); p.Children.Add(ok); w.Content = p; KomStyle(w);
            w.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            w.ShowDialog(); return res;
        }

        // okno przypisań: dla każdego przycisku lista czynności; Zapisz, Przywróć domyślne, własne układy
        internal void ShowPadMapDialog(Window owner)
        {
            var d = new Window { Title = L.T("Kontroler – przypisania przycisków"), Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = owner?.Topmost == true };
            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(new TextBlock { Text = L.T("Wybierz, co robi każdy przycisk kontrolera (Xbox i zgodne) w odtwarzaczu filmów i TV. W otwartym menu krzyżak przesuwa wybór, A zatwierdza, B zamyka."), TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 0, 0, 10) });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            var boxes = new Dictionary<string, ComboBox>();
            int half = (PadButtons.Length + 1) / 2;
            for (int i = 0; i < half; i++) grid.RowDefinitions.Add(new RowDefinition());
            for (int i = 0; i < PadButtons.Length; i++)
            {
                var b = PadButtons[i]; int col = i < half ? 0 : 3, row = i % half;
                var lbl = new TextBlock { Text = b.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
                var cb = new ComboBox { Margin = new Thickness(0, 3, 0, 3) };
                foreach (var a in PadActions) cb.Items.Add(new ComboBoxItem { Content = L.T(a.Name), Tag = a.Id });
                Grid.SetColumn(lbl, col); Grid.SetRow(lbl, row); Grid.SetColumn(cb, col + 1); Grid.SetRow(cb, row);
                grid.Children.Add(lbl); grid.Children.Add(cb); boxes[b.Id] = cb;
            }
            void Fill(Dictionary<string, string> m)
            {
                foreach (var kv in boxes)
                {
                    var id = m.TryGetValue(kv.Key, out var a) ? a : "none";
                    kv.Value.SelectedItem = kv.Value.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == id);
                }
            }
            Dictionary<string, string> Current() => boxes.ToDictionary(kv => kv.Key, kv => (kv.Value.SelectedItem as ComboBoxItem)?.Tag as string ?? "none");
            Fill(PadMap());
            root.Children.Add(grid);
            // własne układy
            var lay = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            lay.Children.Add(new TextBlock { Text = L.T("Moje układy:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var layBox = new ComboBox { Width = 180 };
            void FillLayouts() { layBox.Items.Clear(); foreach (var l in PadLayouts()) layBox.Items.Add(l.Name); }
            FillLayouts();
            var load = new Button { Content = L.T("Wczytaj"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), ToolTip = L.T("Wczytaj wybrany układ do listy powyżej") };
            load.Click += (s, e) => { var l = PadLayouts().FirstOrDefault(x => x.Name == layBox.SelectedItem as string); if (l.Map != null) Fill(ParsePadMap(l.Map)); };
            var saveAs = new Button { Content = L.T("Zapisz jako…"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), ToolTip = L.T("Zapisz obecne przypisania jako własny układ") };
            saveAs.Click += (s, e) =>
            {
                var name = PromptText(d, L.T("Nazwa układu"), L.T("Nazwa układu:"), layBox.SelectedItem as string ?? L.T("Mój układ"));
                if (string.IsNullOrWhiteSpace(name)) return;
                var l = PadLayouts().Where(x => x.Name != name.Trim()).ToList(); l.Add((name.Trim(), PadMapText(Current()))); PadLayoutsSave(l);
                FillLayouts(); layBox.SelectedItem = name.Trim();
            };
            var del = new Button { Content = L.T("Usuń"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), ToolTip = L.T("Usuń wybrany układ") };
            del.Click += (s, e) => { var n = layBox.SelectedItem as string; if (n == null) return; PadLayoutsSave(PadLayouts().Where(x => x.Name != n).ToList()); FillLayouts(); };
            lay.Children.Add(layBox); lay.Children.Add(load); lay.Children.Add(saveAs); lay.Children.Add(del);
            root.Children.Add(lay);
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var def = new Button { Content = L.T("Przywróć domyślne"), Padding = new Thickness(12, 4, 12, 4), ToolTip = L.T("Przypisania jak w popularnych odtwarzaczach") };
            def.Click += (s, e) => Fill(ParsePadMap(PadDefaultMap));
            var ok = new Button { Content = L.T("Zapisz"), Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
            ok.Click += (s, e) => { SetPref("pad-mapa", PadMapText(Current())); d.Close(); };
            bottom.Children.Add(def); bottom.Children.Add(ok);
            root.Children.Add(bottom);
            d.Content = root;
            KomStyle(d);
            d.ShowDialog();
        }
    }
}
