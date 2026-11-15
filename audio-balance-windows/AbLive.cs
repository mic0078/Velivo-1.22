using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Velivo
{
    // 📊 Podgląd jak Audio Monitor w Velivo: paski ŹRÓDŁO / ZMIANA / WYNIK, widmo (słupki - oryginał, kreski - po poprawie), wykres głośności 30 s, redukcja kompresora
    sealed class AbLive
    {
        static readonly Brush Track = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x3A)), Well = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x1A)),
            Red = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)), Teal = new SolidColorBrush(Color.FromRgb(0x2E, 0xD3, 0xC6)), Violet = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF)),
            Green = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)), Gold = new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)), Grey = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA8));
        readonly AbEngine eng; readonly Brush fg, dim;
        public readonly StackPanel Root = new StackPanel();
        readonly DispatcherTimer tm = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };

        // paski
        (Border fill, TextBlock val, Grid track) srcB, chgB, outB, grB;
        Canvas spec, hist; const int NB = 28; readonly Rectangle[] bars = new Rectangle[NB], peaks = new Rectangle[NB]; readonly double[] sm = new double[NB], smO = new double[NB];
        readonly double[] edges = new double[NB + 1]; readonly double[] re = new double[2048], im = new double[2048], re2 = new double[2048], im2 = new double[2048];
        Polyline plIn, plOut; Line tgt; TextBlock tgtTxt; readonly TextBlock[] scale = new TextBlock[3], times = new TextBlock[4]; readonly Line[] gridH = new Line[3], gridV = new Line[3]; const int HN = 300; readonly double[] hIn = new double[HN], hOut = new double[HN]; DateTime hLast;

        public AbLive(AbEngine e, Brush fg, Brush dim)
        {
            eng = e; this.fg = fg; this.dim = dim;
            for (int i = 0; i <= NB; i++) edges[i] = 40 * Math.Pow(16000.0 / 40, i / (double)NB);
            for (int i = 0; i < HN; i++) hIn[i] = hOut[i] = -70;
            var hd = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var hr = new TextBlock { Text = L.T("widmo: słupki – oryginał, kreski – po poprawie · linie: głośność z 30 s (biała – po poprawie, szara – oryginał), zielona – cel"), Foreground = dim, FontSize = 10, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(hr, Dock.Right);
            hd.Children.Add(hr); hd.Children.Add(new TextBlock { Text = L.T("📊 Podgląd"), Foreground = fg, FontSize = 13, FontWeight = FontWeights.SemiBold });
            Root.Children.Add(hd); Root.Children.Add(tiles);
            srcB = Bar(L.T("ŹRÓDŁO"), Red, L.T("Głośność dźwięku, który gra Windows (LUFS, przed poprawą)."));
            chgB = Bar(L.T("ZMIANA"), Teal, L.T("Ile Audio Balance teraz podgłaśnia (w prawo) albo ścisza (w lewo), dB."));
            outB = Bar(L.T("WYNIK"), Green, L.T("Głośność po poprawie – to słyszysz (LUFS)."));
            grB = Bar(L.T("KOMPRESOR"), Gold, L.T("Ile kompresor wstępny teraz ścisza głośne fragmenty (dB)."));
            spec = new Canvas { Height = 120, Background = Well, ClipToBounds = true, Margin = new Thickness(0, 10, 0, 0) };
            for (int b = 0; b < NB; b++)
            {
                double t = b / (double)(NB - 1); var c = Color.FromRgb((byte)(0x2E + (0x7C - 0x2E) * t), (byte)(0xD3 + (0x5C - 0xD3) * t), (byte)(0xC6 + (0xFF - 0xC6) * t));
                bars[b] = new Rectangle { Fill = new SolidColorBrush(c), RadiusX = 1.5, RadiusY = 1.5 }; peaks[b] = new Rectangle { Fill = Brushes.White, Height = 2 };
                spec.Children.Add(bars[b]); spec.Children.Add(peaks[b]);
            }
            Root.Children.Add(spec);
            // wykres głośności z 30 s rysowany na widmie (makieta): biała – po poprawie, szara – oryginał, żółta – cel
            hist = spec;
            plIn = new Polyline { Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0x8A, 0x90, 0xA8)), StrokeThickness = 1.2 }; plOut = new Polyline { Stroke = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.6 };
            tgt = new Line { Stroke = Green, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 4, 3 } };
            tgtTxt = new TextBlock { Foreground = Green, FontSize = 11, FontWeight = FontWeights.SemiBold, Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x10, 0x12, 0x1A)), Padding = new Thickness(4, 0, 4, 0) };
            var gb = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            for (int k = 0; k < 3; k++) { gridH[k] = new Line { Stroke = gb, StrokeThickness = 1 }; gridV[k] = new Line { Stroke = gb, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 4 } }; spec.Children.Insert(0, gridH[k]); spec.Children.Insert(0, gridV[k]); }
            for (int k = 0; k < 4; k++) { times[k] = new TextBlock { Foreground = dim, FontSize = 9, Text = k == 3 ? L.T("teraz") : "-" + (30 - 10 * k) + " s" }; spec.Children.Add(times[k]); }
            for (int k = 0; k < 3; k++) { scale[k] = new TextBlock { Foreground = dim, FontSize = 9, Text = (-20 * (k + 1)).ToString() + " dB" }; spec.Children.Add(scale[k]); }
            spec.Children.Add(tgt); spec.Children.Add(plIn); spec.Children.Add(plOut); spec.Children.Add(tgtTxt);
            tm.Tick += (s, a) => Tick();
            Root.IsVisibleChanged += (s, a) => { if (Root.IsVisible) tm.Start(); else tm.Stop(); };   // tylko gdy Podgląd na ekranie
        }

        TextBlock Cap(string s) => new TextBlock { Text = s, Foreground = dim, FontSize = 10, Margin = new Thickness(0, 4, 0, 2) };
        readonly System.Windows.Controls.Primitives.UniformGrid tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        // kafelek: nazwa, duża liczba, cienki pasek (makieta)
        (Border, TextBlock, Grid) Bar(string name, Brush color, string tip)
        {
            var p = new StackPanel();
            p.Children.Add(new TextBlock { Text = name, Foreground = dim, FontSize = 10 });
            var val = new TextBlock { Text = "–", Foreground = color, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) }; p.Children.Add(val);
            var track = new Grid { Height = 5, Background = Track };
            var fill = new Border { Background = color, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            track.Children.Add(fill); p.Children.Add(track);
            tiles.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x34)), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(0, 0, tiles.Children.Count < 3 ? 8 : 0, 0), Child = p, ToolTip = tip });
            return (fill, val, track);
        }
        static void Set((Border fill, TextBlock val, Grid track) b, double frac, string txt) { b.fill.Width = Math.Max(0, Math.Min(1, frac)) * Math.Max(0, b.track.ActualWidth); b.val.Text = txt; }

        void Tick()
        {
            var p = eng.Proc; bool live = eng.Running && p.Raw.ShortTerm > -69;   // w ciszy „–” zamiast mylących liczb
            double src = p.Raw.ShortTerm, outp = p.Out.ShortTerm, g = p.GainDb, gr = p.Pre.Params.IsEnabled ? p.Pre.CurrentGainReductionDb : 0;
            Set(srcB, live ? (src + 60) / 60 : 0, live ? src.ToString("0.0") : "–");
            Set(outB, live ? (outp + 60) / 60 : 0, live ? outp.ToString("0.0") : "–");
            // ZMIANA: od środka - w prawo podgłaśnia (turkus), w lewo ścisza (fiolet)
            { double w = Math.Max(0, chgB.track.ActualWidth), half = w / 2, x = live ? Math.Max(-1, Math.Min(1, g / 15)) * half : 0;
              chgB.fill.Background = x >= 0 ? Teal : Violet; chgB.fill.Margin = new Thickness(x >= 0 ? half : half + x, 0, 0, 0); chgB.fill.Width = Math.Abs(x);
              chgB.val.Text = live ? (Math.Abs(g) < 0.05 ? "±0,0" : g.ToString("+0.0;-0.0")) : "–"; chgB.val.Foreground = x >= 0 ? Teal : Violet; }
            Set(grB, live && gr < 0 ? -gr / 24 : 0, live && p.Pre.Params.IsEnabled ? gr.ToString("0.0") : "–");
            // widmo: ostatnie 2048 próbek oryginału i wyniku, okno Hanna, FFT
            int pos = p.ScopePos;
            for (int i = 0; i < 2048; i++) { double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 2047); int k = (pos - 2048 + i) & 65535; re[i] = p.Scope[k] * w; im[i] = 0; re2[i] = p.ScopeOut[k] * w; im2[i] = 0; }
            Fft(re, im); Fft(re2, im2);
            double sw = spec.ActualWidth, sh = spec.ActualHeight, bw = sw / NB;
            for (int b = 0; b < NB; b++)
            {
                int k0 = Math.Max(1, (int)(edges[b] * 2048 / 48000)), k1 = Math.Max(k0 + 1, (int)(edges[b + 1] * 2048 / 48000)); double mx = 0, mo = 0;
                for (int k = k0; k < k1 && k < 1024; k++) { mx = Math.Max(mx, re[k] * re[k] + im[k] * im[k]); mo = Math.Max(mo, re2[k] * re2[k] + im2[k] * im2[k]); }
                double v = eng.Running ? Math.Max(0, Math.Min(1, (20 * Math.Log10(2 * Math.Sqrt(mx) / 1024 + 1e-9) + 72) / 60)) : 0, vo = eng.Running ? Math.Max(0, Math.Min(1, (20 * Math.Log10(2 * Math.Sqrt(mo) / 1024 + 1e-9) + 72) / 60)) : 0;
                sm[b] = v > sm[b] ? v : Math.Max(v, sm[b] - 0.04); smO[b] = vo > smO[b] ? vo : Math.Max(vo, smO[b] - 0.04);
                bars[b].Width = Math.Max(1, bw - 3); bars[b].Height = sm[b] * sh; Canvas.SetLeft(bars[b], b * bw + 1.5); Canvas.SetTop(bars[b], sh - sm[b] * sh);
                peaks[b].Width = Math.Max(1, bw - 3); Canvas.SetLeft(peaks[b], b * bw + 1.5); Canvas.SetTop(peaks[b], sh - smO[b] * sh - 2); peaks[b].Visibility = smO[b] > 0.01 ? Visibility.Visible : Visibility.Hidden;
            }
            // wykres 30 s (punkt co 100 ms)
            if ((DateTime.UtcNow - hLast).TotalMilliseconds >= 100)
            {
                hLast = DateTime.UtcNow;
                Array.Copy(hIn, 1, hIn, 0, HN - 1); Array.Copy(hOut, 1, hOut, 0, HN - 1);
                hIn[HN - 1] = eng.Running ? Math.Max(-70, p.Raw.Momentary) : -70; hOut[HN - 1] = eng.Running ? Math.Max(-70, p.Out.Momentary) : -70;
                double hw = hist.ActualWidth, hh = hist.ActualHeight; double Y(double d) => hh - 3 - (Math.Max(-60, Math.Min(0, d)) + 60) / 60 * (hh - 6);
                var a1 = new PointCollection(HN); var a2 = new PointCollection(HN);
                for (int i = 0; i < HN; i++) { double x = i * hw / (HN - 1); a1.Add(new Point(x, Y(hIn[i]))); a2.Add(new Point(x, Y(hOut[i]))); }
                plIn.Points = a1; plOut.Points = a2; tgt.X1 = 0; tgt.X2 = hw; tgt.Y1 = tgt.Y2 = Y(p.EffTarget);
                // podpis celu (zielony) i skala dB - wiadomo, co pokazuje wykres
                tgtTxt.Text = L.T("cel ") + p.EffTarget.ToString("0.0") + " LUFS"; Canvas.SetRight(tgtTxt, 4); Canvas.SetTop(tgtTxt, Math.Max(0, Y(p.EffTarget) - 16));
                for (int k = 0; k < 3; k++) { double y = Y(-20 * (k + 1)); Canvas.SetLeft(scale[k], 3); Canvas.SetTop(scale[k], y - 12); gridH[k].X1 = 0; gridH[k].X2 = hw; gridH[k].Y1 = gridH[k].Y2 = y;
                    double x = hw * (k + 1) / 3; gridV[k].X1 = gridV[k].X2 = x; gridV[k].Y1 = 0; gridV[k].Y2 = hh; }
                for (int k = 0; k < 4; k++) { double x = hw * k / 3; Canvas.SetLeft(times[k], k == 0 ? 3 : k == 3 ? hw - 32 : x + 3); Canvas.SetTop(times[k], hh - 13); }   // podziałka czasu: -30 s … teraz
            }
        }

        static void Fft(double[] r, double[] i0)
        {
            int n = r.Length;
            for (int i = 1, j = 0; i < n; i++) { int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) { (r[i], r[j]) = (r[j], r[i]); (i0[i], i0[j]) = (i0[j], i0[i]); } }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2; double tr = r[b] * cr - i0[b] * ci, ti = r[b] * ci + i0[b] * cr;
                        r[b] = r[a] - tr; i0[b] = i0[a] - ti; r[a] += tr; i0[a] += ti; double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                    }
                }
            }
        }
    }
}
