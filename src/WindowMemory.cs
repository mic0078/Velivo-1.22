using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        // okno niezalezne (jak zwykle okno Windows): wlasna pozycja na pasku zadan, nie przykrywa na stale innych okien Velivo,
        // przelaczanie Alt+Tab; zamykane razem z Velivo. Pierwsze otwarcie - na srodku okna, z ktorego je otwarto.
        readonly List<Window> _independent = new List<Window>();
        internal void Independent(Window w)
        {
            var o = w.Owner;
            if (o != null && w.WindowStartupLocation == WindowStartupLocation.CenterOwner && o.IsLoaded)
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                double ow = o.WindowState == WindowState.Maximized ? SystemParameters.WorkArea.Width : o.ActualWidth, oh = o.WindowState == WindowState.Maximized ? SystemParameters.WorkArea.Height : o.ActualHeight;
                double ol = o.WindowState == WindowState.Maximized ? SystemParameters.WorkArea.Left : o.Left, ot = o.WindowState == WindowState.Maximized ? SystemParameters.WorkArea.Top : o.Top;
                w.Left = ol + Math.Max(0, (ow - w.Width) / 2); w.Top = ot + Math.Max(0, (oh - w.Height) / 2);
            }
            if (w.WindowStartupLocation == WindowStartupLocation.CenterOwner) w.WindowStartupLocation = WindowStartupLocation.CenterScreen;   // bez okna-wlasciciela
            if (w.Icon == null) w.Icon = Icon;
            w.Owner = null; w.ShowInTaskbar = true;
            _independent.Add(w); w.Closed += (s, e) => _independent.Remove(w);
        }
        // pytanie Tak / Nie w stylu Velivo (ciemne, czytelne okienko jak w odtwarzaczu) - zamiast szarego MessageBox Windows
        internal static bool AskYes(Window owner, string text, string yes = null, string no = null)
        {
            bool res = false;
            var d = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, Owner = owner != null && owner.IsVisible ? owner : null, Topmost = owner?.Topmost ?? false, WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen };
            var p = new StackPanel { Margin = new Thickness(26, 20, 26, 20), MaxWidth = 460 };
            p.Children.Add(new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 15, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 16) });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Func<string, bool, Button> btn = (t, primary) => new Button
            {
                Content = t, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(6, 0, 6, 0), FontSize = 14, Cursor = System.Windows.Input.Cursors.Hand,
                Background = primary ? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)) : Brushes.White, Foreground = primary ? Brushes.White : Brushes.Black,
                BorderBrush = primary ? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)) : Brushes.LightGray, Template = RoundButton()
            };
            var y = btn(yes ?? L.T("Tak"), true); var n = btn(no ?? L.T("Nie"), false);
            y.Click += (s, e) => { res = true; d.Close(); }; n.Click += (s, e) => d.Close();
            y.IsDefault = true; n.IsCancel = true; if (no == "") y.IsCancel = true;
            row.Children.Add(y); if (no != "") row.Children.Add(n); p.Children.Add(row);
            d.Content = new Border { Child = p, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x22, 0x22, 0x22)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), BorderThickness = new Thickness(1) };
            d.Loaded += (s, e) => y.Focus();
            d.ShowDialog();
            return res;
        }

        // informacja z jednym przyciskiem OK - ten sam czytelny styl
        internal static void InfoBox(Window owner, string text) { AskYes(owner, text, "OK", ""); }

        // zaokraglony przycisk (tlo z Background, poswiata pod mysza)
        static ControlTemplate RoundButton()
        {
            var g = new FrameworkElementFactory(typeof(Grid));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1)); bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            var ov = new FrameworkElementFactory(typeof(Border), "ov"); ov.SetValue(Border.CornerRadiusProperty, new CornerRadius(6)); ov.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            g.AppendChild(bd); g.AppendChild(ov); g.AppendChild(cp);
            var t = new ControlTemplate(typeof(Button)) { VisualTree = g };
            var h = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; h.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), "ov")); t.Triggers.Add(h);
            return t;
        }

        void CloseIndependent() { foreach (var w in _independent.ToList()) try { w.Close(); } catch (Exception) { } }

        internal static string GetPref(string key) { return WinBounds().TryGetValue("ust:" + key, out var v) ? v : null; }

        internal static void SetPref(string key, string value)
        {
            WinBounds()["ust:" + key] = value;
            try { Directory.CreateDirectory(DataDir); File.WriteAllLines(WindowsFile, WinBounds().Select(kv => kv.Key + "\t" + kv.Value)); }
            catch (Exception ex) { App.LogError(ex); }
        }

        // sortowanie dowolnej listy (ListView + GridView z DisplayMemberBinding): klik w naglowek, drugi klik - odwrotnie,
        // zapamietane; wlasciwosc "<Kolumna>Sort" (jesli jest) to klucz sortowania (np. data jako DateTime zamiast tekstu)
        internal static Action SortableList(System.Windows.Controls.ListView list, string prefKey)
        {
            var grid = (System.Windows.Controls.GridView)list.View;
            var p = (GetPref("sort-" + prefKey) ?? "").Split(',');
            string key = p[0].Length > 0 ? p[0] : null; bool desc = p.Length > 1 && p[1] == "desc";
            var headers = new List<Tuple<System.Windows.Controls.GridViewColumnHeader, string, string>>();
            foreach (var c in grid.Columns)
            {
                var path = (c.DisplayMemberBinding as System.Windows.Data.Binding)?.Path?.Path; if (path == null) continue;
                var text = c.Header as string ?? (c.Header as System.Windows.Controls.GridViewColumnHeader)?.Content as string ?? path;
                var h = new System.Windows.Controls.GridViewColumnHeader { Content = text, Tag = path, Cursor = Cursors.Hand };
                c.Header = h; headers.Add(Tuple.Create(h, path, text));
            }
            Action apply = () =>
            {
                foreach (var h in headers) h.Item1.Content = h.Item3 + (h.Item2 == key ? (desc ? "  ▼" : "  ▲") : "");
                var src = list.ItemsSource as System.Collections.IEnumerable; if (src == null || key == null) return;
                var items = src.Cast<object>().ToList(); if (items.Count == 0) return;
                var t = items[0].GetType(); var prop = t.GetProperty(key + "Sort") ?? t.GetProperty(key);
                System.Reflection.FieldInfo fld = prop == null ? t.GetField(key + "Sort") ?? t.GetField(key) : null;
                if (prop == null && fld == null) return;
                Func<object, object> get = o => prop != null ? prop.GetValue(o) : fld.GetValue(o);
                var cmp = Comparer<object>.Create((a, b) => a is string sa && b is string sb ? string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase) : Comparer<object>.Default.Compare(a, b));
                var sel = list.SelectedItem;
                list.ItemsSource = (desc ? items.OrderByDescending(get, cmp) : items.OrderBy(get, cmp)).ToList();
                if (sel != null) list.SelectedItem = sel;
            };
            list.AddHandler(System.Windows.Controls.GridViewColumnHeader.ClickEvent, new RoutedEventHandler((s, a) =>
            {
                if (!(a.OriginalSource is System.Windows.Controls.GridViewColumnHeader h) || !(h.Tag is string k)) return;
                if (k == key) desc = !desc; else { key = k; desc = false; }
                SetPref("sort-" + prefKey, key + (desc ? ",desc" : ""));
                apply();
            }));
            apply();
            return apply;   // wywolac po kazdym ustawieniu ItemsSource
        }
}
}
