using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace VelivoProcesor
{
    // 💡 ikona przy zegarze (makieta-velivo-procesor-3.html): kolor obciążenia procesora (zielona do 30 %, żółta do 70 %, czerwona powyżej)
    // i dioda dysku w rogu - mruga ok. 8 razy na sekundę, dopóki wybrany dysk czyta / zapisuje (jak LED w obudowie). Ikony rysowane raz (3 kolory × dioda).
    sealed class Dioda
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string src, IntPtr user, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
        [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr q);
        [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr c, uint fmt, IntPtr type, out PdhValue v);
        [StructLayout(LayoutKind.Sequential)] struct PdhValue { public uint Status; public uint Pad; public double Value; }
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        IntPtr q, c; string dysk;   // "*" wszystkie, "C:" jeden, "" wyłączona
        readonly Dictionary<int, Icon> ikony = new Dictionary<int, Icon>();
        bool swieci; int ost = -1;

        public void Dysk(string d)
        {
            if (d == dysk) return; dysk = d;
            if (q != IntPtr.Zero) { PdhCloseQuery(q); q = c = IntPtr.Zero; }
            if (string.IsNullOrEmpty(d) || PdhOpenQuery(null, IntPtr.Zero, out q) != 0) { q = IntPtr.Zero; return; }
            PdhAddEnglishCounter(q, d == "*" ? @"\PhysicalDisk(_Total)\Disk Bytes/sec" : @"\LogicalDisk(" + d + @")\Disk Bytes/sec", IntPtr.Zero, out c);
            PdhCollectQueryData(q);
        }
        // co 60 ms: czy dysk pracuje (od poprzedniego odczytu) - dioda zmienia stan, gdy pracuje, gaśnie od razu, gdy nie
        public Icon Krok(double cpu, int px)
        {
            bool praca = false;
            if (q != IntPtr.Zero && PdhCollectQueryData(q) == 0 && PdhGetFormattedCounterValue(c, 0x200, IntPtr.Zero, out var v) == 0) praca = v.Value > 4096;
            swieci = praca && !swieci;
            int kol = cpu >= 70 ? 2 : cpu >= 30 ? 1 : 0, klucz = kol * 2 + (swieci ? 1 : 0);
            if (klucz == ost) return null; ost = klucz;   // bez zmiany - ikona zostaje
            if (!ikony.TryGetValue(klucz * 1000 + px, out var ic)) ikony[klucz * 1000 + px] = ic = Rysuj(kol, swieci, px);
            return ic;
        }
        static Icon Rysuj(int kol, bool led, int px)
        {
            using (var bmp = new Bitmap(px, px))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var tlo = kol == 2 ? Color.FromArgb(0xDC, 0x26, 0x26) : kol == 1 ? Color.FromArgb(0xCA, 0x8A, 0x04) : Color.FromArgb(0x16, 0xA3, 0x4A);
                float r = px * 0.22f, s = px - 1;
                using (var path = new GraphicsPath())
                {
                    path.AddArc(0, 0, r * 2, r * 2, 180, 90); path.AddArc(s - r * 2, 0, r * 2, r * 2, 270, 90);
                    path.AddArc(s - r * 2, s - r * 2, r * 2, r * 2, 0, 90); path.AddArc(0, s - r * 2, r * 2, r * 2, 90, 90); path.CloseFigure();
                    using (var b = new SolidBrush(tlo)) g.FillPath(b, path);
                }
                using (var pen = new Pen(Color.White, Math.Max(1.6f, px / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[] { new PointF(px * 0.27f, px * 0.25f), new PointF(px * 0.5f, px * 0.75f), new PointF(px * 0.73f, px * 0.25f) });   // V jak w logo
                if (led)
                {
                    float d = px * 0.42f;
                    using (var glow = new SolidBrush(Color.FromArgb(120, 255, 80, 60))) g.FillEllipse(glow, px - d - 0.5f, px - d - 0.5f, d + 0.5f, d + 0.5f);
                    using (var b = new SolidBrush(Color.FromArgb(255, 255, 59, 48))) g.FillEllipse(b, px - d * 0.85f, px - d * 0.85f, d * 0.75f, d * 0.75f);
                }
                var h = bmp.GetHicon(); try { return (Icon)Icon.FromHandle(h).Clone(); } finally { DestroyIcon(h); }
            }
        }
    }
}
