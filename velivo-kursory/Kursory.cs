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
            if (args.Length > 0 && args[0] == "--zastosuj") { Kursory.Zastosuj(Kursory.Rozmiar()); return; }
            Application.EnableVisualStyles();
            Application.Run(new Okno());
        }
    }

    static class Kursory
    {
        internal static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velivo", "Kursory");
        static string Ust => Path.Combine(Dir, "rozmiar.txt");
        static string Kopia => Path.Combine(Dir, "poprzednie.txt");
        internal static int Rozmiar() { try { return int.Parse(File.ReadAllText(Ust).Trim()) == 48 ? 48 : 32; } catch (Exception) { return 32; } }

        // rodzaj kursora: nazwa w rejestrze Windows (Panel sterowania\Kursory), podpis, punkt aktywny (w skali 32), liczba klatek
        internal sealed class Rodzaj { public string Rej, Nazwa; public float Hx, Hy; public int Klatki = 1; public string Plik; }
        internal static readonly Rodzaj[] Wszystkie =
        {
            new Rodzaj { Rej = "Arrow", Nazwa = "Strzałka", Hx = 4, Hy = 2, Plik = "strzalka" },
            new Rodzaj { Rej = "Hand", Nazwa = "Łącze", Hx = 13, Hy = 3, Klatki = 8, Plik = "lacze" },
            new Rodzaj { Rej = "IBeam", Nazwa = "Tekst", Hx = 16, Hy = 16, Plik = "tekst" },
            new Rodzaj { Rej = "Wait", Nazwa = "Czekanie", Hx = 16, Hy = 16, Klatki = 12, Plik = "czekanie" },
            new Rodzaj { Rej = "AppStarting", Nazwa = "Praca w tle", Hx = 3, Hy = 2, Klatki = 12, Plik = "praca" },
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
        internal static Bitmap Klatka(Rodzaj r, int S, int f)
        {
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.Clear(Color.Transparent);
                float k = S / 32f; g.ScaleTransform(k, k);
                double t = r.Klatki > 1 ? (double)f / r.Klatki : 0;
                switch (r.Plik)
                {
                    case "strzalka": Ksztalt(g, Strzalka(0, 0)); break;
                    case "pomoc": Ksztalt(g, Strzalka(0, 0)); Znak(g, "?", 22, 18); break;
                    case "lacze": using (var p = Dlon()) { var m = new Matrix(); m.Translate(0, (float)(Math.Sin(t * 2 * Math.PI) * 1.4)); p.Transform(m); Ksztalt(g, p); } break;
                    case "tekst":
                        using (var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 4.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) Belka(g, pen);
                        using (var pen = new Pen(Fiolet, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) Belka(g, pen); break;
                    case "czekanie": Kolo(g, 16, 16, 11, t, 4f); break;
                    case "praca":
                        Ksztalt(g, Strzalka(-1, 0));
                        using (var ring = new Pen(Color.FromArgb((int)(90 + 110 * (0.5 + 0.5 * Math.Sin(t * 2 * Math.PI))), Turkus), 1.3f)) g.DrawEllipse(ring, 16, 16, 15, 15);   // pulsujący pierścień (styl C)
                        Kolo(g, 23.5f, 23.5f, 5.5f, t, 2.6f); break;
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
        static void Ksztalt(Graphics g, GraphicsPath p)
        {
            using (p)
            {
                using (var glow = new Pen(Color.FromArgb(70, Fiolet), 4.5f) { LineJoin = LineJoin.Round }) g.DrawPath(glow, p);
                using (var br = new LinearGradientBrush(new PointF(0, 0), new PointF(32, 32), Fiolet, Turkus)) g.FillPath(br, p);
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
        static void Belka(Graphics g, Pen pen) { g.DrawLine(pen, 12, 5, 20, 5); g.DrawLine(pen, 16, 5, 16, 27); g.DrawLine(pen, 12, 27, 20, 27); }
        static void Kolo(Graphics g, float cx, float cy, float r, double t, float w)
        {
            using (var tlo = new Pen(Color.FromArgb(150, 40, 44, 60), w + 1.6f)) g.DrawEllipse(tlo, cx - r, cy - r, 2 * r, 2 * r);
            using (var br = new LinearGradientBrush(new PointF(cx - r, cy - r), new PointF(cx + r, cy + r), Fiolet, Turkus))
            using (var pen = new Pen(br, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, (float)(t * 360) - 90, 110);
        }
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
        static byte[] Ani(Rodzaj r, int S)
        {
            var klatki = Enumerable.Range(0, r.Klatki).Select(f => { using (var b = Klatka(r, S, f)) return Cur(b, (int)(r.Hx * S / 32), (int)(r.Hy * S / 32)); }).ToList();
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
        static byte[] CurPlik(Rodzaj r, int S) { using (var b = Klatka(r, S, 0)) return Cur(b, (int)(r.Hx * S / 32), (int)(r.Hy * S / 32)); }
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
            Controls.Add(new Label { Text = "Kursory myszy w stylu Velivo – z animacją czekania, pracy w tle i łącza. Windows je pamięta: program nie musi działać w tle.", ForeColor = Dim, Location = new Point(20, 50), Size = new Size(600, 40) });
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
            var maly = new RadioButton { Text = "Normalny (32 px)", AutoSize = true, Location = new Point(22, 352), Checked = Kursory.Rozmiar() == 32, ForeColor = Fg };
            var duzy = new RadioButton { Text = "Duży (48 px) – łatwiej zobaczyć", AutoSize = true, Location = new Point(180, 352), Checked = Kursory.Rozmiar() == 48, ForeColor = Fg };
            Controls.Add(maly); Controls.Add(duzy);
            Button B(string t, int x, Color c) { var b = new Button { Text = t, Location = new Point(x, 388), Size = new Size(t.Length * 8 + 40, 36), FlatStyle = FlatStyle.Flat, BackColor = c, ForeColor = Fg, Cursor = Cursors.Hand }; b.FlatAppearance.BorderSize = 0; Controls.Add(b); return b; }
            var tak = B("✔ Zastosuj kursory Velivo", 20, Akcent);
            var nie = B("↺ Przywróć kursory Windows", 270, Karta);
            stan = new Label { ForeColor = Dim, Location = new Point(20, 434), Size = new Size(600, 24) };
            Controls.Add(stan); Stan();
            tak.Click += (s, e) => { try { Kursory.Zastosuj(duzy.Checked ? 48 : 32); Stan(); } catch (Exception ex) { stan.Text = "Nie udało się: " + ex.Message; } };
            nie.Click += (s, e) => { try { Kursory.Przywroc(); Stan(); } catch (Exception ex) { stan.Text = "Nie udało się: " + ex.Message; } };
            var tm = new Timer { Interval = 90 };
            tm.Tick += (s, e) => { klatka++; foreach (var p in podglad) if (p.Kl.Length > 1) p.Pb.Image = p.Kl[klatka % p.Kl.Length]; };
            tm.Start(); FormClosed += (s, e) => tm.Stop();
        }
        void Stan() => stan.Text = Kursory.Wlaczone() ? "✔ Kursory Velivo są włączone (" + Kursory.Rozmiar() + " px)." : "Teraz: kursory Windows.";
    }
}
