using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Velivo
{
    // 📌 widżet na pulpicie (jak w Velivo): animowane słupki, wynik w LUFS, źródło i zmiana; przeciąganie, menu (prawy przycisk), dwuklik = okno programu
    sealed class AbWidget
    {
        readonly AbMain m; Window w; readonly Rectangle[] bars = new Rectangle[7]; double lvl;
        public AbWidget(AbMain main) { m = main; }
        public bool Open => w != null;

        public void Toggle() { if (w != null) Close(); else Show(); }
        public void Close() { var x = w; w = null; x?.Close(); m.Put("widzet", "0"); }

        public void Show()
        {
            if (w != null) { w.Show(); w.Activate(); return; }
            m.Put("widzet", "1");
            var fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF2)); var dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA8));
            w = new Window { Title = "Velivo Audio Balance – widżet", Width = 176, Height = 92, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = m.Get("widzet-top") != "0", WindowStartupLocation = WindowStartupLocation.Manual };
            w.Left = double.TryParse(m.Get("widzet-x"), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : SystemParameters.WorkArea.Right - 196;
            w.Top = double.TryParse(m.Get("widzet-y"), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ? y : SystemParameters.WorkArea.Top + 20;
            if (double.TryParse(m.Get("widzet-alfa"), NumberStyles.Float, CultureInfo.InvariantCulture, out var a)) w.Opacity = a;
            var g = new Grid { Margin = new Thickness(10, 6, 10, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            var bc = new Canvas { Width = 52, Height = 64, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 7; i++) { double t = i / 6.0; bars[i] = new Rectangle { Width = 5, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(Color.FromRgb((byte)(0x2E + 78 * t), (byte)(0xD3 - 119 * t), (byte)(0xC6 + 57 * t))) }; Canvas.SetLeft(bars[i], i * 7.5); bc.Children.Add(bars[i]); }
            g.Children.Add(bc);
            var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(txt, 1); g.Children.Add(txt);
            txt.Children.Add(new TextBlock { Text = L.T("WYNIK (LUFS)"), Foreground = dim, FontSize = 9 });
            var outT = new TextBlock { Text = "–", Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)), FontSize = 22, FontWeight = FontWeights.SemiBold }; txt.Children.Add(outT);
            var subT = new TextBlock { Foreground = dim, FontSize = 10 }; txt.Children.Add(subT);
            w.Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x14, 0x16, 0x1F)), CornerRadius = new CornerRadius(12), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x7C, 0x5C, 0xFF)), BorderThickness = new Thickness(1), Child = g };
            w.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) { m.ShowMain(); return; }
                try { w.DragMove(); m.Put("widzet-x", w.Left.ToString(CultureInfo.InvariantCulture)); m.Put("widzet-y", w.Top.ToString(CultureInfo.InvariantCulture)); } catch (Exception) { }
            };
            w.MouseWheel += (s, e) => { w.Opacity = Math.Max(0.3, Math.Min(1, w.Opacity + (e.Delta > 0 ? 0.1 : -0.1))); m.Put("widzet-alfa", w.Opacity.ToString(CultureInfo.InvariantCulture)); };
            var cm = new ContextMenu();
            MenuItem It(string h, Action act) { var mi = new MenuItem { Header = h }; mi.Click += (s, e) => act(); cm.Items.Add(mi); return mi; }
            It(L.T("🎚 Otwórz Audio Balance"), m.ShowMain);
            var byp = It("", () => m.ToggleBypass());
            var top = It("", () => { w.Topmost = !w.Topmost; m.Put("widzet-top", w.Topmost ? "1" : "0"); });
            cm.Items.Add(new Separator());
            It(L.T("✕ Zamknij widżet z pulpitu"), Close);
            cm.Opened += (s, e) => { byp.Header = m.Eng.Proc.Bypass ? L.T("⏻ Włącz przetwarzanie") : L.T("⏻ Bez przetwarzania"); top.Header = (w.Topmost ? "✔ " : "") + L.T("📌 Zawsze na wierzchu"); };
            w.ContextMenu = cm; w.ToolTip = L.T("Przeciągnij, aby przesunąć · dwuklik – okno programu · kółko myszy – przezroczystość · prawy przycisk – menu");
            var tm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            int k = 0;
            tm.Tick += (s, e) =>
            {
                var p = m.Eng.Proc; bool on = m.Eng.Running && p.Raw.ShortTerm > -69;
                double target = on ? Math.Max(0, Math.Min(1, (p.Out.Momentary + 50) / 45)) : 0; lvl += (target - lvl) * (target > lvl ? 0.6 : 0.25); k++;
                for (int i = 0; i < 7; i++) { double h = 6 + 56 * lvl * (0.55 + 0.45 * Math.Abs(Math.Sin(k * 0.23 + i * 1.1))); bars[i].Height = h; Canvas.SetTop(bars[i], 64 - h); }
                if (k % 5 == 0)
                {
                    outT.Text = on ? p.Out.ShortTerm.ToString("0.0") : "–";
                    subT.Text = !m.Eng.Running ? L.T("zatrzymany") : p.Bypass ? L.T("bez przetwarzania") : on ? L.T("źródło ") + p.Raw.ShortTerm.ToString("0.0") + "  " + (p.GainDb >= 0 ? "▲ +" : "▼ ") + p.GainDb.ToString("0.0") : L.T("cisza");
                }
            };
            w.Closed += (s, e) => tm.Stop(); tm.Start(); w.Show();
        }
    }
}
