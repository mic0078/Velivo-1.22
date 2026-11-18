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
        // 🧩 Velivo Monitor pokazuje Audio Balance w swojej karcie i na pasku zadań: stan co sekundę do pliku; gdy Monitor go pokazuje - własny widżet schowany
        static readonly string MonDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velivo", "Monitor");
        int tk;
        public void Tick()
        {
            if (++tk % 10 != 0) return;
            var p = m.Eng.Shown; bool on = m.Eng.Running && p.Raw.ShortTerm > -69;
            string sub = !m.Eng.Running ? L.T("stop") : p.Bypass ? L.T("wył.") : on ? (p.GainDb >= 0 ? "▲+" : "▼") + p.GainDb.ToString("0") : L.T("cisza");
            try { System.IO.Directory.CreateDirectory(MonDir); System.IO.File.WriteAllText(System.IO.Path.Combine(MonDir, "ab-stan.txt"), (on ? p.Out.ShortTerm.ToString("0.0", CultureInfo.InvariantCulture) : "-") + "\t" + sub + "\t" + (on && !p.Bypass && p.Pre.Params.IsEnabled ? p.Pre.CurrentGainReductionDb.ToString("0.0", CultureInfo.InvariantCulture) : "")); } catch (Exception) { }   // 3. pole: praca kompresora (dB, ≤ 0), puste = wyłączony
            bool wMon = false;
            try { var z = System.IO.Path.Combine(MonDir, "ab-w-monitorze.txt"); wMon = System.IO.File.Exists(z) && (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(z)).TotalSeconds < 4; } catch (Exception) { }
            if (w != null) w.Visibility = wMon ? Visibility.Hidden : Visibility.Visible;
        }

        public void Toggle() { if (w != null) Close(); else Show(); }
        public void Close() { var x = w; w = null; x?.Close(); m.Put("widzet", "0"); }

        public void Show()
        {
            if (w != null) { w.Show(); w.Activate(); return; }
            m.Put("widzet", "1");
            var fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF2)); var dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA8));
            // 💊 wariant B (wybór właściciela 2026-11-16): pasek 120 × 28 - słupki, wynik LUFS, zmiana dB w jednej linii; reszta w podpowiedzi
            w = new Window { Title = "Velivo Audio Balance – widżet", Width = 120, Height = 28, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = m.Get("widzet-top") != "0", WindowStartupLocation = WindowStartupLocation.Manual };
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop, vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
            double px = double.TryParse(m.Get("widzet-x"), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : SystemParameters.WorkArea.Right - 140;
            double py = double.TryParse(m.Get("widzet-y"), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ? y : SystemParameters.WorkArea.Top + 20;
            if (px < vl || px > vr - 40 || py < vt || py > vb - 20) { px = SystemParameters.WorkArea.Right - 140; py = SystemParameters.WorkArea.Top + 20; }   // drugi monitor odłączony - widżet wraca na główny ekran, nie ginie poza nim
            w.Left = px; w.Top = py;
            if (double.TryParse(m.Get("widzet-alfa"), NumberStyles.Float, CultureInfo.InvariantCulture, out var a)) w.Opacity = a;
            var g = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(9, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var bc = new Canvas { Width = 23, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            for (int i = 0; i < 5; i++) { double t = i / 4.0; bars[i] = new Rectangle { Width = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = new SolidColorBrush(Color.FromRgb((byte)(0x2E + 78 * t), (byte)(0xD3 - 119 * t), (byte)(0xC6 + 57 * t))) }; Canvas.SetLeft(bars[i], i * 5); bc.Children.Add(bars[i]); }
            g.Children.Add(bc);
            var outT = new TextBlock { Text = "–", Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }; g.Children.Add(outT);
            var subT = new TextBlock { Foreground = dim, FontSize = 9, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; g.Children.Add(subT);
            var info = new TextBlock { Foreground = Brushes.Black };
            w.Content = new Border { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1F)), CornerRadius = new CornerRadius(14), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x7C, 0x5C, 0xFF)), BorderThickness = new Thickness(1), Child = g };
            w.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) { m.ShowMain(); return; }
                try { w.DragMove(); m.Put("widzet-x", w.Left.ToString(CultureInfo.InvariantCulture)); m.Put("widzet-y", w.Top.ToString(CultureInfo.InvariantCulture)); } catch (Exception) { }
            };
            w.MouseWheel += (s, e) => { w.Opacity = Math.Round(Math.Max(0.1, Math.Min(1, w.Opacity + (e.Delta > 0 ? 0.1 : -0.1))), 1); m.Put("widzet-alfa", w.Opacity.ToString(CultureInfo.InvariantCulture)); };
            var cm = new ContextMenu();
            MenuItem It(string h, Action act) { var mi = new MenuItem { Header = h }; mi.Click += (s, e) => act(); cm.Items.Add(mi); return mi; }
            It(L.T("🎚 Otwórz Audio Balance"), m.ShowMain);
            var byp = It("", () => m.ToggleBypass());
            var top = It("", () => { w.Topmost = !w.Topmost; m.Put("widzet-top", w.Topmost ? "1" : "0"); });
            cm.Items.Add(new Separator());
            It(L.T("✕ Zamknij widżet z pulpitu"), Close);
            cm.Opened += (s, e) => { byp.Header = m.Eng.Proc.Bypass ? L.T("⏻ Włącz przetwarzanie") : L.T("⏻ Bez przetwarzania"); top.Header = (w.Topmost ? "✔ " : "") + L.T("📌 Zawsze na wierzchu"); };
            w.ContextMenu = cm; w.ToolTip = info;
            var tm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            int k = 0;
            tm.Tick += (s, e) =>
            {
                var p = m.Eng.Shown; bool on = m.Eng.Running && p.Raw.ShortTerm > -69;
                double target = on ? Math.Max(0, Math.Min(1, (p.Out.Momentary + 50) / 45)) : 0; lvl += (target - lvl) * (target > lvl ? 0.6 : 0.25); k++;
                for (int i = 0; i < 5; i++) { double h = 3 + 13 * lvl * (0.55 + 0.45 * Math.Abs(Math.Sin(k * 0.23 + i * 1.1))); bars[i].Height = h; Canvas.SetTop(bars[i], 16 - h); }
                if (k % 5 == 0)
                {
                    outT.Text = on ? p.Out.ShortTerm.ToString("0.0") : "–";
                    subT.Text = !m.Eng.Running ? L.T("stop") : p.Bypass ? L.T("wył.") : on ? (p.GainDb >= 0 ? "▲+" : "▼") + p.GainDb.ToString("0") : L.T("cisza");
                    info.Text = L.T("WYNIK (LUFS): ") + outT.Text + "\n" + (!m.Eng.Running ? L.T("zatrzymany") : p.Bypass ? L.T("bez przetwarzania") : on ? L.T("źródło ") + p.Raw.ShortTerm.ToString("0.0") + "  " + (p.GainDb >= 0 ? "▲ +" : "▼ ") + p.GainDb.ToString("0.0") + " dB" : L.T("cisza")) + "\n" + L.T("Przeciągnij, aby przesunąć · dwuklik – okno programu · kółko myszy – przezroczystość · prawy przycisk – menu");
                }
            };
            w.Closed += (s, e) => tm.Stop(); tm.Start(); w.Show();
        }
    }
}
