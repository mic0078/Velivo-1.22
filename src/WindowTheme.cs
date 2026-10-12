using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace Przegladarka
{
    // Motyw okien Velivo (biblioteka, telewizja, program TV, notatki, ustawienia...): jasny / ciemny / jak Windows.
    // Ciemny: ciemny pasek tytulu (DWM), tlo i tekst okna, list, pol, naglowkow i przyciskow; okno glowne ma swoje motywy.
    public partial class MainWindow
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        internal static bool WindowsAppsDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch (Exception) { return false; }
        }

        static string _winTheme = "light";
        internal static bool DarkWindows { get { return _winTheme == "dark" || _winTheme == "auto" && WindowsAppsDark(); } }

        static readonly Brush DarkBg = Frozen("#1F2328"), DarkPanel = Frozen("#2D333B"), DarkBorder = Frozen("#444C56"), DarkFg = Frozen("#E6EDF3"), DarkHeader = Frozen("#22272E");
        static Brush Frozen(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

        // wywolywane przy otwarciu kazdego okna (App) i po zmianie ustawienia (otwarte okna)
        internal static void ApplyWindowTheme(Window w)
        {
            if (w is MainWindow || w.AllowsTransparency || w.WindowStyle == WindowStyle.None) return;   // glowne okno ma motywy przegladarki, warstwy na filmie - bez zmian
            if (w.Background is SolidColorBrush sb && sb.Color == Colors.Black) return;   // odtwarzacz - i tak ciemny
            bool dark = DarkWindows;
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h != IntPtr.Zero) { int on = dark ? 1 : 0; DwmSetWindowAttribute(h, 20, ref on, 4); }   // DWMWA_USE_IMMERSIVE_DARK_MODE - ciemny pasek tytulu
            }
            catch (Exception) { }
            for (int i = w.Resources.MergedDictionaries.Count - 1; i >= 0; i--) if (w.Resources.MergedDictionaries[i].Contains("velivo-dark")) w.Resources.MergedDictionaries.RemoveAt(i);
            if (!dark) { w.ClearValue(Window.BackgroundProperty); w.ClearValue(Window.ForegroundProperty); w.Resources["SmallBtnBg"] = Frozen("#F3F4F6"); return; }
            w.Background = DarkBg; w.Foreground = DarkFg;
            w.Resources["SmallBtnBg"] = DarkPanel;
            var d = new ResourceDictionary { { "velivo-dark", true } };
            Action<Type, Action<Style>> style = (t, f) => { var s = new Style(t); f(s); d[t] = s; };
            Func<DependencyProperty, object, Setter> set = (p, v) => new Setter(p, v);
            style(typeof(TextBox), s => { s.Setters.Add(set(Control.BackgroundProperty, DarkHeader)); s.Setters.Add(set(Control.ForegroundProperty, DarkFg)); s.Setters.Add(set(Control.BorderBrushProperty, DarkBorder)); s.Setters.Add(set(TextBoxBase.CaretBrushProperty, DarkFg)); });
            style(typeof(PasswordBox), s => { s.Setters.Add(set(Control.BackgroundProperty, DarkHeader)); s.Setters.Add(set(Control.ForegroundProperty, DarkFg)); s.Setters.Add(set(Control.BorderBrushProperty, DarkBorder)); s.Setters.Add(set(PasswordBox.CaretBrushProperty, DarkFg)); });
            foreach (var t in new[] { typeof(ListBox), typeof(ListView), typeof(TreeView), typeof(DataGrid) })
                style(t, s => { s.Setters.Add(set(Control.BackgroundProperty, DarkHeader)); s.Setters.Add(set(Control.ForegroundProperty, DarkFg)); s.Setters.Add(set(Control.BorderBrushProperty, DarkBorder)); });
            style(typeof(GridViewColumnHeader), s => { s.Setters.Add(set(Control.BackgroundProperty, DarkPanel)); s.Setters.Add(set(Control.ForegroundProperty, DarkFg)); s.Setters.Add(set(Control.BorderBrushProperty, DarkBorder)); });
            style(typeof(Button), s => { s.Setters.Add(set(Control.BackgroundProperty, DarkPanel)); s.Setters.Add(set(Control.ForegroundProperty, DarkFg)); s.Setters.Add(set(Control.BorderBrushProperty, DarkBorder)); });
            foreach (var t in new[] { typeof(CheckBox), typeof(RadioButton), typeof(Label), typeof(GroupBox), typeof(Expander), typeof(TabItem) })
                style(t, s => s.Setters.Add(set(Control.ForegroundProperty, DarkFg)));
            style(typeof(ComboBox), s => s.Setters.Add(set(Control.ForegroundProperty, Brushes.Black)));   // pole listy rozwijanej zostaje jasne (szablon Windows) - tekst czytelny
            style(typeof(ContextMenu), s => { s.Setters.Add(set(Control.BackgroundProperty, DarkPanel)); s.Setters.Add(set(Control.ForegroundProperty, DarkFg)); s.Setters.Add(set(Control.BorderBrushProperty, DarkBorder)); });
            style(typeof(ToolTip), s => { s.Setters.Add(set(Control.BackgroundProperty, DarkPanel)); s.Setters.Add(set(Control.ForegroundProperty, DarkFg)); });
            w.Resources.MergedDictionaries.Insert(0, d);
        }

        void SetWindowTheme(string theme)
        {
            _winTheme = theme == "dark" || theme == "auto" ? theme : "light";
            foreach (Window w in Application.Current.Windows) ApplyWindowTheme(w);
        }
    }
}
