using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.Wave;

namespace Velivo
{
    // 〰 wykres fali jak w Audacity: obwiednia (min/max) całego utworu, linijka czasu, kursor odtwarzania; klik/przeciąganie = przewinięcie, kółko = powiększenie, Shift+kółko = przesuwanie
    sealed class AbFala : FrameworkElement
    {
        const int Blk = 256; float[] mn = Array.Empty<float>(), mx = Array.Empty<float>(); int sr = 48000; double total;   // sekundy
        double view0, viewLen; public double Play;   // widoczny fragment (s), kursor (s)
        public Action<double> Seek; public string Status = "";
        static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x13)), Wave = new SolidColorBrush(Color.FromRgb(0x2E, 0xD3, 0xC6)), Dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA8)), Grid = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        static readonly Pen Head = new Pen(new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)), 1.5), Mid = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1);
        public AbFala() { Height = 180; ClipToBounds = true; Cursor = Cursors.IBeam; ToolTip = L.T("Klik / przeciąganie – przewiń · kółko – powiększ · Shift + kółko – przesuń"); }

        public async void Load(string file)
        {
            Status = L.T("Wczytuję falę…"); mn = mx = Array.Empty<float>(); InvalidateVisual();
            try
            {
                var (a, b, rate, len) = await Task.Run(() =>
                {
                    using var rd = new MediaFoundationReader(file); var sp = rd.ToSampleProvider(); int ch = sp.WaveFormat.Channels;
                    var la = new List<float>(); var lb = new List<float>(); var buf = new float[Blk * ch * 64]; int n, k = 0; float lo = 1, hi = -1;
                    while ((n = sp.Read(buf, 0, buf.Length)) > 0)
                        for (int i = 0; i + ch <= n; i += ch)
                        {
                            float v = 0; for (int c = 0; c < ch; c++) v += buf[i + c]; v /= ch;
                            if (v < lo) lo = v; if (v > hi) hi = v;
                            if (++k == Blk) { la.Add(lo); lb.Add(hi); k = 0; lo = 1; hi = -1; }
                        }
                    if (k > 0) { la.Add(lo); lb.Add(hi); }
                    return (la.ToArray(), lb.ToArray(), sp.WaveFormat.SampleRate, rd.TotalTime.TotalSeconds);
                });
                mn = a; mx = b; sr = rate; total = len; view0 = 0; viewLen = len; Status = "";
            }
            catch (Exception ex) { Status = "✖ " + ex.Message; App.LogError(ex); }
            InvalidateVisual();
        }

        double X(double t) => (t - view0) / Math.Max(0.001, viewLen) * ActualWidth;
        double T(double x) => Math.Max(0, Math.Min(total, view0 + x / Math.Max(1, ActualWidth) * viewLen));
        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight, top = 16, wh = h - top, mid = top + wh / 2;
            dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
            if (mn.Length == 0 || total <= 0) { dc.DrawText(Txt(Status.Length > 0 ? Status : L.T("Otwórz utwór lub film – tu pojawi się fala"), Dim, 12), new Point(10, mid - 8)); return; }
            // linijka czasu
            double step = new[] { 0.1, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 }[0]; foreach (var s0 in new[] { 0.1, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 }) { step = s0; if (w / (viewLen / s0) >= 70) break; }
            for (double t = Math.Ceiling(view0 / step) * step; t < view0 + viewLen; t += step)
            { double x = X(t); dc.DrawRectangle(Grid, null, new Rect(x, top, 1, wh)); dc.DrawText(Txt(TimeSpan.FromSeconds(t).ToString(step < 1 ? @"m\:ss\.f" : @"m\:ss"), Dim, 9), new Point(x + 2, 1)); }
            dc.DrawLine(Mid, new Point(0, mid), new Point(w, mid));
            // obwiednia: na każdy piksel min i max bloków w jego zakresie
            double blkPerSec = sr / (double)Blk;
            for (int px = 0; px < (int)w; px++)
            {
                int b0 = (int)((view0 + px / w * viewLen) * blkPerSec), b1 = Math.Max(b0 + 1, (int)((view0 + (px + 1) / w * viewLen) * blkPerSec));
                if (b0 >= mn.Length) break; float lo = 1, hi = -1;
                for (int b = Math.Max(0, b0); b < b1 && b < mn.Length; b++) { if (mn[b] < lo) lo = mn[b]; if (mx[b] > hi) hi = mx[b]; }
                double y1 = mid - Math.Min(1, hi) * wh / 2, y2 = mid - Math.Max(-1, lo) * wh / 2;
                dc.DrawRectangle(Wave, null, new Rect(px, y1, 1, Math.Max(1, y2 - y1)));
            }
            double hx = X(Play); if (hx >= 0 && hx <= w) dc.DrawLine(Head, new Point(hx, 0), new Point(hx, h));
        }
        static FormattedText Txt(string s, Brush b, double size) => new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, 1.0);

        public void SetPlay(double t)
        {
            Play = t; if (viewLen < total && (t < view0 || t > view0 + viewLen)) view0 = Math.Max(0, Math.Min(total - viewLen, t - viewLen * 0.1));   // kursor ucieka z widoku - widok idzie za nim
            InvalidateVisual();
        }
        bool down;
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { if (total <= 0) return; down = true; CaptureMouse(); Seek?.Invoke(T(e.GetPosition(this).X)); }
        protected override void OnMouseMove(MouseEventArgs e) { if (down) Seek?.Invoke(T(e.GetPosition(this).X)); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { down = false; ReleaseMouseCapture(); }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (total <= 0) return; e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) view0 = Math.Max(0, Math.Min(total - viewLen, view0 - Math.Sign(e.Delta) * viewLen * 0.15));
            else { double at = T(e.GetPosition(this).X), nl = Math.Max(0.5, Math.Min(total, viewLen * (e.Delta > 0 ? 0.7 : 1.4))); view0 = Math.Max(0, Math.Min(total - nl, at - (at - view0) * nl / viewLen)); viewLen = nl; }
            InvalidateVisual();
        }
    }
}
