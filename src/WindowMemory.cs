using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

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

        // ---------- przezroczystosc okna: Alt + kolko myszy (zapamietywana) ----------

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        const int GWL_EXSTYLE = -20; const long WS_EX_LAYERED = 0x80000; const uint LWA_ALPHA = 2;
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> _alpha = new System.Runtime.CompilerServices.ConditionalWeakTable<Window, object>();

        static string AlphaKey(Window w) { return "przezroczystosc:" + (w is MainWindow ? "Velivo" : WindowKey(w.Title)); }

        // krok o 5%, najwyzej do 20% widocznosci (okno nie znika calkiem)
        internal static double NextAlpha(double a, int delta) { return Math.Round(Math.Max(0.2, Math.Min(1.0, a + (delta > 0 ? 0.05 : -0.05))), 2); }

        static void ApplyAlpha(Window w, double a)
        {
            _alpha.AddOrUpdate(w, a);
            if (w.AllowsTransparency) { w.Opacity = a; return; }
            var h = new WindowInteropHelper(w).Handle; if (h == IntPtr.Zero) return;
            var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
            if (a >= 0.999) { if ((ex & WS_EX_LAYERED) != 0) SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_LAYERED)); return; }
            if ((ex & WS_EX_LAYERED) == 0) SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
            SetLayeredWindowAttributes(h, 0, (byte)Math.Round(a * 255), LWA_ALPHA);
        }

        // przy otwarciu okna - ostatnia przezroczystosc
        internal static void RestoreAlpha(Window w)
        {
            if (w.AllowsTransparency && w.Owner != null) return;   // warstwa na filmie - przezroczystosc ma okno odtwarzacza
            if (WinBounds().TryGetValue(AlphaKey(w), out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && a < 1) ApplyAlpha(w, Math.Max(0.2, a));
        }

        internal static void AlphaWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Alt || !(sender is Window w)) return;
            if (w.AllowsTransparency && w.Owner != null) w = w.Owner;   // kolko nad filmem (warstwa) - cale okno odtwarzacza
            var a = NextAlpha(_alpha.TryGetValue(w, out var cur) ? (double)cur : 1.0, e.Delta);
            ApplyAlpha(w, a);
            e.Handled = true;
            WinBounds()[AlphaKey(w)] = a.ToString(CultureInfo.InvariantCulture);
            try { Directory.CreateDirectory(DataDir); File.WriteAllLines(WindowsFile, WinBounds().Select(kv => kv.Key + "\t" + kv.Value)); }
            catch (Exception ex) { App.LogError(ex); }
        }

        // drobne ustawienia list (np. sortowanie) - w tym samym pliku co okna
        internal static string GetPref(string key) { return WinBounds().TryGetValue("ust:" + key, out var v) ? v : null; }

        internal static void SetPref(string key, string value)
        {
            WinBounds()["ust:" + key] = value;
            try { Directory.CreateDirectory(DataDir); File.WriteAllLines(WindowsFile, WinBounds().Select(kv => kv.Key + "\t" + kv.Value)); }
            catch (Exception ex) { App.LogError(ex); }
        }
}
}
