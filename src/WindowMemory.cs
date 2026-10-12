using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;

namespace Przegladarka
{
    // Kazde okno Velivo (biblioteka, odtwarzacz, notatki, PDF...) pamieta swoje miejsce i rozmiar - jak lista kanalow.
    // Klucz = tytul okna bez zmiennej czesci ("Film – Odtwarzacz filmow" -> "Odtwarzacz filmow"); zapis w okna.txt.
    public partial class MainWindow
    {
        static string WindowsFile { get { return Path.Combine(DataDir, "okna.txt"); } }
        static Dictionary<string, string> _winBounds;

        internal static string WindowKey(string title)
        {
            var t = (title ?? "").Trim();
            int i = t.LastIndexOf(" – ", StringComparison.Ordinal);
            return (i >= 0 ? t.Substring(i + 3) : t).Trim();
        }

        // "x,y,szer,wys[,max]"
        internal static string FormatBounds(Rect r, bool max)
        {
            return string.Join(",", new[] { r.Left, r.Top, r.Width, r.Height }.Select(v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture))) + (max ? ",max" : "");
        }

        internal static bool ParseBounds(string s, out Rect r, out bool max)
        {
            r = Rect.Empty; max = false;
            var p = (s ?? "").Split(',');
            if (p.Length < 4) return false;
            var n = new int[4];
            for (int i = 0; i < 4; i++) if (!int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])) return false;
            if (n[2] < 150 || n[3] < 100) return false;
            r = new Rect(n[0], n[1], n[2], n[3]); max = p.Length > 4 && p[4] == "max";
            return true;
        }

        static Dictionary<string, string> WinBounds()
        {
            if (_winBounds != null) return _winBounds;
            _winBounds = new Dictionary<string, string>();
            try { if (File.Exists(WindowsFile)) foreach (var l in File.ReadAllLines(WindowsFile)) { var k = l.IndexOf('\t'); if (k > 0) _winBounds[l.Substring(0, k)] = l.Substring(k + 1); } }
            catch (Exception ex) { App.LogError(ex); }
            return _winBounds;
        }

        static bool Remembers(Window w)
        {
            return !(w is MainWindow) && !"own-bounds".Equals(w.Tag) && w.SizeToContent == SizeToContent.Manual && w.WindowStyle != WindowStyle.None &&
                (w.ResizeMode == ResizeMode.CanResize || w.ResizeMode == ResizeMode.CanResizeWithGrip) && WindowKey(w.Title).Length > 0;
        }

        // przy otwarciu: ostatnie miejsce i rozmiar (tylko gdy okno bedzie widoczne na ktoryms ekranie); przy zamknieciu: zapis
        internal static void RememberWindow(Window w)
        {
            if (!Remembers(w)) return;
            var key = WindowKey(w.Title);
            if (WinBounds().TryGetValue(key, out var s) && ParseBounds(s, out var r, out var max))
            {
                var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                var vis = Rect.Intersect(screen, r);
                if (!vis.IsEmpty && vis.Width >= 120 && vis.Height >= 60)
                {
                    w.Left = r.Left; w.Top = r.Top; w.Width = r.Width; w.Height = r.Height;
                    if (max) w.WindowState = WindowState.Maximized;
                }
            }
            w.Closing += (o, e) =>
            {
                if (w.WindowStyle == WindowStyle.None) return;   // tryb mini / pelny ekran - nie zapamietujemy
                var b = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight) : w.RestoreBounds;
                if (b.IsEmpty) return;
                WinBounds()[key] = FormatBounds(b, w.WindowState == WindowState.Maximized);
                try { Directory.CreateDirectory(DataDir); File.WriteAllLines(WindowsFile, WinBounds().Select(kv => kv.Key + "\t" + kv.Value)); }
                catch (Exception ex) { App.LogError(ex); }
            };
        }
    
        // szerokosci kolumn listy (biblioteka filmow, kanaly) - zapis przy zamknieciu okna
        internal static void RememberColumns(Window w, string key, System.Windows.Controls.GridView grid)
        {
            key = "kolumny:" + key;
            if (WinBounds().TryGetValue(key, out var s))
            {
                var p = s.Split(',');
                for (int i = 0; i < p.Length && i < grid.Columns.Count; i++)
                    if (double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 20 && v <= 3000) grid.Columns[i].Width = v;
            }
            w.Closing += (o, e) =>
            {
                WinBounds()[key] = string.Join(",", grid.Columns.Select(c => ((int)Math.Round(double.IsNaN(c.Width) ? c.ActualWidth : c.Width)).ToString(CultureInfo.InvariantCulture)));
                try { Directory.CreateDirectory(DataDir); File.WriteAllLines(WindowsFile, WinBounds().Select(kv => kv.Key + "\t" + kv.Value)); }
                catch (Exception ex) { App.LogError(ex); }
            };
        }
}
}
