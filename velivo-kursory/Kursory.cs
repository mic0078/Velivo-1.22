using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace VelivoKursory
{
    // 🖱 Velivo Kursory: komplet kursorów Windows w stylu Velivo (gradient fiolet → turkus, poświata, biały obrys – czytelny na każdym tle),
    // animacje w zwykłych plikach .ani (czekanie, praca w tle z pulsującym pierścieniem, łącze, „niedostępne”). Windows sam pamięta kursory –
    // program nie działa w tle. Rysowane w programie (bez dołączonych obrazków), dwa rozmiary: 32 i 48 px.
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--przywroc") { Kursory.Przywroc(); return; }   // odinstalowanie
            if (args.Length > 0 && args[0] == "--zastosuj") { Kursory.Zastosuj(args.Length > 1 && int.TryParse(args[1], out var n) && Kursory.Rozmiary.Contains(n) ? n : Kursory.Rozmiar()); return; }   // także z Velivo Centrum: --zastosuj 64
            Application.EnableVisualStyles();
            // jedna kopia: drugie uruchomienie (skrót, Centrum) otwiera okno pierwszej
            var pokaz = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\Velivo.Kursory.Pokaz", out bool pierwszy);
            if (!pierwszy) { pokaz.Set(); return; }
            Application.Run(new Zasobnik(pokaz, args.Length == 0 || args[0] != "--zasobnik"));
        }
    }

    static class Kursory
    {
        internal static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velivo", "Kursory");
        static string Ust => Path.Combine(Dir, "rozmiar.txt");
        static string Kopia => Path.Combine(Dir, "poprzednie.txt");
        internal static readonly int[] Rozmiary = { 24, 28, 32, 40, 48, 64, 96 };
        internal static readonly string[] Opisy = { "24 px – mała", "28 px – trochę mniejsza", "32 px – normalna (jak Windows)", "40 px – trochę większa", "48 px – duża", "64 px – bardzo duża", "96 px – największa (słaby wzrok)" };   // ten sam wybór w oknie, przy zegarze i w Centrum
        internal static int Rozmiar() { try { int n = int.Parse(File.ReadAllText(Ust).Trim()); return Rozmiary.Contains(n) ? n : 32; } catch (Exception) { return 32; } }

        // rodzaj kursora: nazwa w rejestrze Windows (Panel sterowania\Kursory), podpis, punkt aktywny (w skali 32), liczba klatek
        internal sealed class Rodzaj { public string Rej, Nazwa; public float Hx, Hy; public int Klatki = 1; public string Plik; }
        internal static readonly Rodzaj[] Wszystkie =
        {
            new Rodzaj { Rej = "Arrow", Nazwa = "Strzałka", Hx = 4, Hy = 2, Klatki = 16, Plik = "strzalka" },
            new Rodzaj { Rej = "Hand", Nazwa = "Łącze", Hx = 13, Hy = 3, Klatki = 8, Plik = "lacze" },
            new Rodzaj { Rej = "IBeam", Nazwa = "Tekst", Hx = 16, Hy = 16, Plik = "tekst" },
            new Rodzaj { Rej = "Wait", Nazwa = "Czekanie", Hx = 16, Hy = 16, Klatki = 16, Plik = "czekanie" },
            new Rodzaj { Rej = "AppStarting", Nazwa = "Praca w tle", Hx = 4, Hy = 2, Klatki = 16, Plik = "praca" },
            new Rodzaj { Rej = "SizeAll", Nazwa = "Przesuń", Hx = 16, Hy = 16, Plik = "przesun" },
            new Rodzaj { Rej = "SizeWE", Nazwa = "Rozmiar ↔", Hx = 16, Hy = 16, Plik = "poziomo" },
            new Rodzaj { Rej = "SizeNS", Nazwa = "Rozmiar ↕", Hx = 16, Hy = 16, Plik = "pionowo" },
            new Rodzaj { Rej = "SizeNWSE", Nazwa = "Rozmiar ⤡", Hx = 16, Hy = 16, Plik = "skos1" },
            new Rodzaj { Rej = "SizeNESW", Nazwa = "Rozmiar ⤢", Hx = 16, Hy = 16, Plik = "skos2" },
            new Rodzaj { Rej = "No", Nazwa = "Niedostępne", Hx = 16, Hy = 16, Klatki = 10, Plik = "niedostepne" },
            new Rodzaj { Rej = "Crosshair", Nazwa = "Precyzja", Hx = 16, Hy = 16, Plik = "krzyz" },
            new Rodzaj { Rej = "Help", Nazwa = "Pomoc", Hx = 4, Hy = 2, Plik = "pomoc" },
            new Rodzaj { Rej = "UpArrow", Nazwa = "Wybór", Hx = 16, Hy = 3, Plik = "gora" },
            new Rodzaj { Rej = "NWPen", Nazwa = "Pismo", Hx = 4, Hy = 28, Plik = "pioro" },
            new Rodzaj { Rej = "Pin", Nazwa = "Miejsce", Hx = 13, Hy = 3, Klatki = 8, Plik = "lacze" },
            new Rodzaj { Rej = "Person", Nazwa = "Osoba", Hx = 13, Hy = 3, Klatki = 8, Plik = "lacze" },
        };

        static readonly Color Fiolet = Color.FromArgb(0x7C, 0x3A, 0xED), Turkus = Color.FromArgb(0x22, 0xB8, 0xD8), Czerwony = Color.FromArgb(0xF8, 0x71, 0x71);

        // jedna klatka kursora (S x S, przezroczyste tło)
        internal static Bitmap Klatka(Rodzaj r, int S, int f, int C = 0)
        {
            if (C < S) C = S; var bmp = new Bitmap(C, C, PixelFormat.Format32bppArgb);   // C - pole kursora (Windows rozciąga mniejsze pliki do swojej wielkości), S - wielkość rysunku
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.Clear(Color.Transparent);
                var o = Przesuniecie(r, S, C); g.TranslateTransform(o.X, o.Y); float k = S / 32f; g.ScaleTransform(k, k);
                double t = r.Klatki > 1 ? (double)f / r.Klatki : 0;
                switch (r.Plik)
                {
                    case "strzalka": Ksztalt(g, Strzalka(0, 0), t); break;   // fala wody w środku (wybór właściciela 2)
                    case "pomoc": Ksztalt(g, Strzalka(0, 0)); Znak(g, "?", 22, 18); break;
                    case "lacze": using (var p = Dlon()) { var m = new Matrix(); m.Translate(0, (float)(Math.Sin(t * 2 * Math.PI) * 1.4)); p.Transform(m); Ksztalt(g, p); } break;
                    case "tekst":
                        using (var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 4.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) Belka(g, pen);
                        using (var pen = new Pen(Fiolet, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) Belka(g, pen); break;
                    case "czekanie": Kreski(g, 16, 16, 1f, t); break;   // trzy poziome kreski płyną w prawo (wybór właściciela B)
                    case "praca": Ksztalt(g, Strzalka(0, 0), t); Kreski(g, 24, 25, 0.5f, t); break;
                    case "przesun": Ksztalt(g, Przesun()); break;
                    case "poziomo": Ksztalt(g, Dwustronna(0)); break;
                    case "pionowo": Ksztalt(g, Dwustronna(90)); break;
                    case "skos1": Ksztalt(g, Dwustronna(45)); break;
                    case "skos2": Ksztalt(g, Dwustronna(-45)); break;
                    case "niedostepne":
                        int a = (int)(170 + 85 * (0.5 + 0.5 * Math.Sin(t * 2 * Math.PI)));
                        using (var w = new Pen(Color.White, 6f)) { g.DrawEllipse(w, 6, 6, 20, 20); g.DrawLine(w, 9, 23, 23, 9); }
                        using (var c = new Pen(Color.FromArgb(a, Czerwony), 3.6f)) { g.DrawEllipse(c, 6, 6, 20, 20); g.DrawLine(c, 9, 23, 23, 9); } break;
                    case "krzyz":
                        using (var w = new Pen(Color.White, 4f)) { g.DrawLine(w, 16, 3, 16, 29); g.DrawLine(w, 3, 16, 29, 16); }
                        using (var c = new LinearGradientBrush(new PointF(0, 0), new PointF(32, 32), Fiolet, Turkus)) using (var cp = new Pen(c, 2f)) { g.DrawLine(cp, 16, 3, 16, 29); g.DrawLine(cp, 3, 16, 29, 16); } break;
                    case "gora": using (var p = Dwustronna(90, jednostronna: true)) Ksztalt(g, p); break;
                    case "pioro": Ksztalt(g, Pioro()); break;
                }
            }
            return bmp;
        }

        // wspólny wygląd: poświata, biały obrys (czytelność na jasnym i ciemnym), gradient Velivo
        static void Ksztalt(Graphics g, GraphicsPath p, double fala = -1)
        {
            using (p)
            {
                using (var glow = new Pen(Color.FromArgb(70, Fiolet), 4.5f) { LineJoin = LineJoin.Round }) g.DrawPath(glow, p);
                using (var br = new LinearGradientBrush(new PointF(0, 0), new PointF(32, 32), Fiolet, Turkus)) g.FillPath(br, p);
                if (fala >= 0)
                {   // 🌊 fala wody (wybór właściciela 2): turkusowa fala faluje w środku kształtu, przesuwa się w prawo
                    var st = g.Save(); g.SetClip(p);
                    using (var w = new GraphicsPath())
                    {
                        float x0 = (float)(-16 + fala * 8), y0 = 17f;   // jeden okres fali = 8 jednostek, pętla bez skoku
                        var pts = new List<PointF>();
                        for (float x = x0; x <= 40; x += 0.5f) pts.Add(new PointF(x, y0 + (float)Math.Sin((x - x0) / 8 * 2 * Math.PI) * 1.8f));
                        pts.Add(new PointF(40, 40)); pts.Add(new PointF(x0, 40)); w.AddPolygon(pts.ToArray());
                        using (var br = new LinearGradientBrush(new PointF(0, 14), new PointF(0, 32), Color.FromArgb(230, Turkus), Color.FromArgb(230, 0x14, 0x8C, 0xC8))) g.FillPath(br, w);
                        using (var pianka = new Pen(Color.FromArgb(200, 0xE0, 0xF7, 0xFF), 0.9f)) g.DrawLines(pianka, pts.Take(pts.Count - 2).ToArray());
                    }
                    g.Restore(st);
                }
                using (var w = new Pen(Color.White, 1.6f) { LineJoin = LineJoin.Round }) g.DrawPath(w, p);
            }
        }
        static GraphicsPath Strzalka(float dx, float dy)
        {
            var p = new GraphicsPath();
            p.AddPolygon(new[] { new PointF(4, 2), new PointF(4, 25), new PointF(10, 19.5f), new PointF(14, 28.5f), new PointF(18, 26.8f), new PointF(14, 18), new PointF(22, 18) }.Select(x => new PointF(x.X + dx, x.Y + dy)).ToArray());
            return p;
        }
        static GraphicsPath Dlon()
        {
            var p = new GraphicsPath();
            p.AddPolygon(new[] { new PointF(11, 4), new PointF(13, 2), new PointF(15, 4), new PointF(15, 13), new PointF(17, 11.5f), new PointF(19, 13), new PointF(21, 12), new PointF(23, 13.5f), new PointF(25, 13),
                new PointF(27, 15), new PointF(27, 23), new PointF(24, 29), new PointF(14, 29), new PointF(8, 22), new PointF(5, 17), new PointF(7, 15), new PointF(11, 18) });
            return p;
        }
        // ▬ trzy poziome kreski w kolorach Velivo, płyną w prawo jedna po drugiej (pojawiają się i gasną)
        static readonly Color Srodek = Color.FromArgb(0x4F, 0x7B, 0xE0);
        static void Kreski(Graphics g, float cx, float cy, float sk, double t)
        {
            var kol = new[] { Fiolet, Srodek, Turkus };
            for (int i = 0; i < 3; i++)
            {
                double ph = ((t - i * 0.17) % 1 + 1) % 1; float dx = (float)(-6 + 12 * ph) * sk; int a = (int)(255 * Math.Sin(Math.PI * ph));
                float w = 16 * sk, h = 4 * sk, x = cx - w / 2 + dx, y = cy - 6 * sk + i * 6 * sk - h / 2;
                using (var path = new GraphicsPath())
                {
                    float rr = h; path.AddArc(x, y, rr, rr, 90, 180); path.AddArc(x + w - rr, y, rr, rr, 270, 180); path.CloseFigure();
                    using (var o = new Pen(Color.FromArgb(a * 3 / 4, 255, 255, 255), 1.4f * Math.Max(sk, 0.8f))) g.DrawPath(o, path);
                    using (var b = new SolidBrush(Color.FromArgb(a, kol[i]))) g.FillPath(b, path);
                }
            }
        }
        static void Belka(Graphics g, Pen pen) { g.DrawLine(pen, 12, 5, 20, 5); g.DrawLine(pen, 16, 5, 16, 27); g.DrawLine(pen, 12, 27, 20, 27); }
        static void Znak(Graphics g, string s, float x, float y)
        {
            using (var f = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var path = new GraphicsPath()) { path.AddString(s, f.FontFamily, (int)FontStyle.Bold, 12f, new PointF(x, y), StringFormat.GenericDefault); using (var w = new Pen(Color.White, 2.5f)) g.DrawPath(w, path); using (var b = new SolidBrush(Turkus)) g.FillPath(b, path); }
        }
        static GraphicsPath Dwustronna(float kat, bool jednostronna = false)
        {
            var pts = jednostronna
                ? new[] { new PointF(16, 3), new PointF(23, 10), new PointF(18.5f, 10), new PointF(18.5f, 29), new PointF(13.5f, 29), new PointF(13.5f, 10), new PointF(9, 10) }
                : new[] { new PointF(3, 16), new PointF(10, 9), new PointF(10, 13.5f), new PointF(22, 13.5f), new PointF(22, 9), new PointF(29, 16), new PointF(22, 23), new PointF(22, 18.5f), new PointF(10, 18.5f), new PointF(10, 23) };
            var p = new GraphicsPath(); p.AddPolygon(pts);
            if (!jednostronna && kat != 0) { var m = new Matrix(); m.RotateAt(kat, new PointF(16, 16)); p.Transform(m); }
            return p;
        }
        static GraphicsPath Przesun()
        {
            var p = new GraphicsPath(); p.FillMode = FillMode.Winding;
            p.AddPolygon(new[] { new PointF(16, 2), new PointF(21, 7), new PointF(18, 7), new PointF(18, 14), new PointF(25, 14), new PointF(25, 11), new PointF(30, 16), new PointF(25, 21), new PointF(25, 18), new PointF(18, 18),
                new PointF(18, 25), new PointF(21, 25), new PointF(16, 30), new PointF(11, 25), new PointF(14, 25), new PointF(14, 18), new PointF(7, 18), new PointF(7, 21), new PointF(2, 16), new PointF(7, 11), new PointF(7, 14), new PointF(14, 14), new PointF(14, 7), new PointF(11, 7) });
            return p;
        }
        static GraphicsPath Pioro()
        {
            var p = new GraphicsPath();
            p.AddPolygon(new[] { new PointF(4, 28), new PointF(6, 21), new PointF(22, 5), new PointF(27, 10), new PointF(11, 26) });
            return p;
        }

        // ---------- pliki .cur / .ani (obraz 32-bit z kanałem przezroczystości) ----------
        static byte[] Cur(Bitmap b, int hx, int hy)
        {
            int S = b.Width, maskRow = ((S + 31) / 32) * 4, img = 40 + S * S * 4 + maskRow * S;
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0); w.Write((short)2); w.Write((short)1);   // ICONDIR: typ 2 = kursor
                w.Write((byte)S); w.Write((byte)S); w.Write((byte)0); w.Write((byte)0); w.Write((short)hx); w.Write((short)hy); w.Write(img); w.Write(22);
                w.Write(40); w.Write(S); w.Write(S * 2); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write(S * S * 4); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (int y = S - 1; y >= 0; y--) for (int x = 0; x < S; x++) { var c = b.GetPixel(x, y); w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A); }   // od dołu (BMP)
                w.Write(new byte[maskRow * S]);   // maska AND pusta - przezroczystość z kanału alfa
                return ms.ToArray();
            }
        }
        // mały kursor w polu Windows: strzałka przy lewym górnym rogu (tam jest punkt aktywny), kursory środkowe (czekanie, rozmiar) - na środku pola
        static PointF Przesuniecie(Rodzaj r, int S, int C) => new PointF(r.Hx == 16 ? (C - S) / 2f : 0, r.Hy == 16 ? (C - S) / 2f : 0);
        static (int X, int Y) Punkt(Rodzaj r, int S, int C) { var o = Przesuniecie(r, S, C); return ((int)(r.Hx * S / 32 + o.X), (int)(r.Hy * S / 32 + o.Y)); }
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        static int Pole(int S) { int c = 32; try { c = Math.Max(16, GetSystemMetrics(13)); } catch (Exception) { } return Math.Max(S, c); }   // SM_CXCURSOR - wielkość, w jakiej Windows rysuje kursor
        static byte[] Ani(Rodzaj r, int S)
        {
            int C = Pole(S); var h = Punkt(r, S, C);
            var klatki = Enumerable.Range(0, r.Klatki).Select(f => { using (var b = Klatka(r, S, f, C)) return Cur(b, h.X, h.Y); }).ToList();
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                void Tag(string s) => w.Write(Encoding.ASCII.GetBytes(s));
                int fram = 4 + klatki.Sum(k => 8 + k.Length + (k.Length & 1));
                Tag("RIFF"); w.Write(4 + (8 + 36) + (8 + fram)); Tag("ACON");
                Tag("anih"); w.Write(36); w.Write(36); w.Write(r.Klatki); w.Write(r.Klatki); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(r.Plik == "lacze" ? 6 : 5); w.Write(1);   // tempo w 1/60 s
                Tag("LIST"); w.Write(fram); Tag("fram");
                foreach (var k in klatki) { Tag("icon"); w.Write(k.Length); w.Write(k); if ((k.Length & 1) == 1) w.Write((byte)0); }
                return ms.ToArray();
            }
        }

        // ---------- założenie i przywrócenie kursorów w Windows ----------
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, IntPtr v, uint flags);
        const string Klucz = @"Control Panel\Cursors";
        internal static void Zastosuj(int S)
        {
            var d = Path.Combine(Dir, S.ToString()); Directory.CreateDirectory(d); File.WriteAllText(Ust, S.ToString());
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Klucz))
            {
                if (!File.Exists(Kopia))   // poprzednie kursory użytkownika - do przywrócenia (raz, przed pierwszą zmianą)
                    File.WriteAllLines(Kopia, new[] { "=" + (k.GetValue("") as string ?? "") }.Concat(Wszystkie.Select(r => r.Rej + "=" + (k.GetValue(r.Rej) as string ?? ""))));
                foreach (var r in Wszystkie)
                {
                    var f = Path.Combine(d, r.Plik + (r.Klatki > 1 ? ".ani" : ".cur"));
                    if (!File.Exists(f) || new FileInfo(f).LastWriteTimeUtc < File.GetLastWriteTimeUtc(typeof(Kursory).Assembly.Location))   // nowa wersja programu - pliki od nowa
                        File.WriteAllBytes(f, r.Klatki > 1 ? Ani(r, S) : CurPlik(r, S));
                    k.SetValue(r.Rej, f);
                }
                k.SetValue("", "Velivo");
            }
            SystemParametersInfo(0x57, 0, IntPtr.Zero, 0x03);   // SPI_SETCURSORS - Windows wczytuje kursory od razu
        }
        static byte[] CurPlik(Rodzaj r, int S) { int C = Pole(S); var h = Punkt(r, S, C); using (var b = Klatka(r, S, 0, C)) return Cur(b, h.X, h.Y); }
        internal static void Przywroc()
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Klucz))
            {
                var stare = File.Exists(Kopia) ? File.ReadAllLines(Kopia).Select(l => l.Split(new[] { '=' }, 2)).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1]) : new Dictionary<string, string>();
                foreach (var r in Wszystkie) k.SetValue(r.Rej, stare.TryGetValue(r.Rej, out var v) && !v.Contains(@"\Velivo\Kursory\") ? v : "", Microsoft.Win32.RegistryValueKind.ExpandString);
                k.SetValue("", stare.TryGetValue("", out var sch) && sch != "Velivo" ? sch : "");
            }
            SystemParametersInfo(0x57, 0, IntPtr.Zero, 0x03);
            try { File.Delete(Kopia); } catch (Exception) { }
        }
        internal static bool Wlaczone() { try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Klucz)) return (k?.GetValue("Arrow") as string ?? "").Contains(@"\Velivo\Kursory\"); } catch (Exception) { return false; } }
    }

    // 🖱 ikona przy zegarze: wielkość, włącz / przywróć, ustawienia, start z Windows (Velivo Centrum chowa ją u siebie, prawy przycisk w Centrum = to menu)
    sealed class Zasobnik : ApplicationContext
    {
        readonly NotifyIcon tray; Okno okno; readonly SynchronizationContextHolder sc = new SynchronizationContextHolder();
        static bool Auto() => Velivo.StartZWindows.Wlaczony("VelivoKursory");
        static void SetAuto(bool on) => Velivo.StartZWindows.Ustaw("VelivoKursory", on ? "\"" + Application.ExecutablePath + "\" --zasobnik" : null);
        public Zasobnik(System.Threading.EventWaitHandle pokaz, bool otworz)
        {
            tray = new NotifyIcon { Text = "Velivo Kursory", Visible = true };
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { tray.Icon = SystemIcons.Application; }
            var m = new ContextMenuStrip();
            m.Items.Add("🖱 Ustawienia kursorów…", null, (s, e) => Pokaz());
            var wl = new ToolStripMenuItem("Wielkość kursorów"); m.Items.Add(wl);
            var opis = Kursory.Opisy;
            for (int i = 0; i < Kursory.Rozmiary.Length; i++) { int px = Kursory.Rozmiary[i]; var it = new ToolStripMenuItem(opis[i]); it.Click += (s, e) => { Kursory.Zastosuj(px); okno?.Stan(); }; wl.DropDownItems.Add(it); }
            var wlacz = new ToolStripMenuItem(); wlacz.Click += (s, e) => { if (Kursory.Wlaczone()) Kursory.Przywroc(); else Kursory.Zastosuj(Kursory.Rozmiar()); okno?.Stan(); }; m.Items.Add(wlacz);
            var auto = new ToolStripMenuItem("Uruchamiaj z Windows") { CheckOnClick = true }; auto.Click += (s, e) => SetAuto(auto.Checked); m.Items.Add(auto);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("✕ Zakończ Velivo Kursory (kursory zostają)", null, (s, e) => { tray.Visible = false; ExitThread(); });
            m.Opening += (s, e) => { bool on = Kursory.Wlaczone(); wlacz.Text = on ? "↺ Przywróć kursory Windows" : "✔ Włącz kursory Velivo"; auto.Checked = Auto();
                foreach (ToolStripMenuItem it in wl.DropDownItems) it.Checked = on && it.Text.StartsWith(Kursory.Rozmiar() + " "); };
            tray.ContextMenuStrip = m;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Pokaz(); };
            Velivo.CentrumZasobnik.Podlacz(tray);
            new System.Threading.Thread(() => { while (pokaz.WaitOne()) sc.Post(Pokaz); }) { IsBackground = true }.Start();
            if (otworz) Pokaz();
        }
        void Pokaz()
        {
            if (okno == null || okno.IsDisposed) { okno = new Okno(); okno.FormClosed += (s, e) => okno = null; okno.Show(); }
            if (okno.WindowState == FormWindowState.Minimized) okno.WindowState = FormWindowState.Normal;
            okno.Activate();
        }
        protected override void Dispose(bool d) { if (d) tray.Dispose(); base.Dispose(d); }
    }
    // wątek okna (znak „pokaż” z drugiego uruchomienia przychodzi z innego wątku)
    sealed class SynchronizationContextHolder
    {
        readonly System.Threading.SynchronizationContext ctx = System.Threading.SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        public void Post(Action a) => ctx.Post(_ => a(), null);
    }

    // okno: podgląd na żywo, rozmiar, zastosuj / przywróć
    sealed class Okno : Form
    {
        static readonly Color Bg = Color.FromArgb(0x14, 0x18, 0x21), Karta = Color.FromArgb(0x1B, 0x21, 0x30), Fg = Color.FromArgb(0xE8, 0xEB, 0xF0), Dim = Color.FromArgb(0x9A, 0xA3, 0xB2), Akcent = Color.FromArgb(0x6A, 0x3B, 0xE0);
        readonly List<(PictureBox Pb, Kursory.Rodzaj R, Bitmap[] Kl)> podglad = new List<(PictureBox, Kursory.Rodzaj, Bitmap[])>();
        readonly Label stan;
        int klatka;

        public Okno()
        {
            Text = "Velivo Kursory"; BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 10f); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            ClientSize = new Size(640, 470); StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            Controls.Add(new Label { Text = "Velivo Kursory", Font = new Font("Segoe UI Semibold", 16f), AutoSize = true, Location = new Point(18, 14) });
            Controls.Add(new Label { Text = "Kursory myszy w stylu Velivo – strzałka z falą wody w środku, trzy kreski przy czekaniu, łącze i „niedostępne” z animacją. Windows je pamięta: program nie musi działać w tle.", ForeColor = Dim, Location = new Point(20, 50), Size = new Size(600, 40) });
            var siatka = new FlowLayoutPanel { Location = new Point(18, 92), Size = new Size(606, 250), BackColor = Bg };
            Controls.Add(siatka);
            foreach (var r in Kursory.Wszystkie.Where(x => x.Rej != "Pin" && x.Rej != "Person"))
            {
                var p = new Panel { Size = new Size(110, 76), BackColor = Karta, Margin = new Padding(4) };
                var pb = new PictureBox { Size = new Size(48, 48), Location = new Point(31, 4), SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Karta };
                p.Controls.Add(pb); p.Controls.Add(new Label { Text = r.Nazwa, ForeColor = Dim, Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleCenter, Location = new Point(0, 54), Size = new Size(110, 18) });
                var kl = Enumerable.Range(0, r.Klatki).Select(f => Kursory.Klatka(r, 48, f)).ToArray(); pb.Image = kl[0];
                podglad.Add((pb, r, kl)); siatka.Controls.Add(p);
            }
            Controls.Add(new Label { Text = "Wielkość:", AutoSize = true, Location = new Point(20, 355) });
            var wielkosc = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(96, 351), Width = 260, BackColor = Karta, ForeColor = Fg, FlatStyle = FlatStyle.Flat };
            wielkosc.Items.AddRange(Kursory.Opisy); wielkosc.SelectedIndex = Math.Max(0, Array.IndexOf(Kursory.Rozmiary, Kursory.Rozmiar()));
            Controls.Add(wielkosc);
            Button B(string t, int x, Color c) { var b = new Button { Text = t, Location = new Point(x, 388), Size = new Size(t.Length * 8 + 40, 36), FlatStyle = FlatStyle.Flat, BackColor = c, ForeColor = Fg, Cursor = Cursors.Hand }; b.FlatAppearance.BorderSize = 0; Controls.Add(b); return b; }
            var tak = B("✔ Zastosuj kursory Velivo", 20, Akcent);
            var nie = B("↺ Przywróć kursory Windows", 270, Karta);
            stan = new Label { ForeColor = Dim, Location = new Point(20, 434), Size = new Size(600, 24) };
            Controls.Add(stan); Stan();
            tak.Click += (s, e) => { try { Kursory.Zastosuj(Kursory.Rozmiary[Math.Max(0, wielkosc.SelectedIndex)]); Stan(); } catch (Exception ex) { stan.Text = "Nie udało się: " + ex.Message; } };
            nie.Click += (s, e) => { try { Kursory.Przywroc(); Stan(); } catch (Exception ex) { stan.Text = "Nie udało się: " + ex.Message; } };
            var tm = new Timer { Interval = 90 };
            tm.Tick += (s, e) => { klatka++; foreach (var p in podglad) if (p.Kl.Length > 1) p.Pb.Image = p.Kl[klatka % p.Kl.Length]; };
            tm.Start(); FormClosed += (s, e) => tm.Stop();
        }
        internal void Stan() => stan.Text = Kursory.Wlaczone() ? "✔ Kursory Velivo są włączone (" + Kursory.Rozmiar() + " px)." : "Teraz: kursory Windows.";
    }
}
