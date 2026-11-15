using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Velivo
{
    // 🎚 znak Velivo Audio Balance (inny niż „V” przeglądarki Velivo i Komunikator): słupki poziomu + biała linia celu;
    // w zasobniku słupki rosną z głośnością wyniku (9 klatek liczonych raz - bez wycieku uchwytów)
    static class AbIcon
    {
        static Icon[] frames;
        static Icon Draw(double fill)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                using (var bg = Rounded(new RectangleF(1, 1, 30, 30), 7)) { using var b = new SolidBrush(Color.FromArgb(0x14, 0x16, 0x1F)); g.FillPath(b, bg); using var p = new Pen(Color.FromArgb(0x2E, 0xD3, 0xC6), 1.4f); g.DrawPath(p, bg); }
                double[] hs = { 0.38, 0.66, 1.0, 0.66, 0.38 }; float x0 = 5.2f, w = 21.6f / 5, baseY = 26.2f;
                for (int i = 0; i < 5; i++)
                {
                    double t = i / 4.0; var c = Color.FromArgb((int)(0x2E + (0x7C - 0x2E) * t), (int)(0xD3 + (0x5C - 0xD3) * t), (int)(0xC6 + (0xFF - 0xC6) * t));
                    float h = (float)(Math.Max(0.06, hs[i] * fill) * 20.5); using var b = new SolidBrush(c);
                    g.FillRectangle(b, x0 + i * w + w * 0.15f, baseY - h, w * 0.7f, h);
                }
                float y = baseY - 0.62f * 20.5f; using (var lp = new Pen(Color.FromArgb(240, 255, 255, 255), 1.4f)) g.DrawLine(lp, 3.5f, y, 28.5f, y);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }
        // klatka dla głośności wyniku (LUFS chwilowy); cisza / wyłączone - słupki przy dnie
        internal static Icon Frame(double momentaryLufs)
        {
            if (frames == null) { frames = new Icon[9]; for (int i = 0; i < 9; i++) frames[i] = Draw(i / 8.0); }
            int fr = double.IsNegativeInfinity(momentaryLufs) || double.IsNaN(momentaryLufs) ? 0 : 1 + (int)Math.Round(Math.Max(0, Math.Min(1, (momentaryLufs + 45) / 40)) * 7);
            return frames[fr];
        }
    }
}
