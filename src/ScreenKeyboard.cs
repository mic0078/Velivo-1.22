using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Przegladarka
{
    // Klawiatura ekranowa Velivo: klik rolka (srodkowym przyciskiem) w pole do pisania - na stronie i w oknach Velivo.
    // Wlasne okno w stylu Velivo, ktore nie zabiera fokusu - znaki ida do pola, w ktorym sie pisze (SendInput).
    public partial class MainWindow
    {
        [StructLayout(LayoutKind.Sequential)] struct KeybdData { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct MouseData { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }   // tylko na rozmiar unii
        [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public KeybdData Kb; [FieldOffset(0)] public MouseData Ms; }
        [StructLayout(LayoutKind.Sequential)] struct KbdInput { public int Type; public InputUnion U; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, KbdInput[] inputs, int size);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int val);
        const uint KeyUp = 2, KeyUnicode = 4;

        static Window _kbd;

        static void SendKey(ushort vk, char ch)
        {
            uint fl = vk == 0 ? KeyUnicode : 0;
            ushort sc = vk == 0 ? ch : (ushort)0;
            var ins = new[] { new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = vk, Scan = sc, Flags = fl } } }, new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = vk, Scan = sc, Flags = fl | KeyUp } } } };
            SendInput(2, ins, Marshal.SizeOf(typeof(KbdInput)));
        }

        internal static void ShowScreenKeyboard()
        {
            if (_kbd != null) { _kbd.Topmost = false; _kbd.Topmost = true; return; }
            bool shift = false, caps = false, alt = false;
            // subtelnie w kolorach logo Velivo: ciemne tlo z lekkim fioletowo-turkusowym odcieniem, klawisze ze szkla,
            // wlaczone / wcisniete - gradient logo (fiolet -> turkus)
            var violet = Color.FromRgb(0x8B, 0x5C, 0xF6); var cyan = Color.FromRgb(0x06, 0xB6, 0xD4);
            var keyBg = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)); var keyHover = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
            var accent = new LinearGradientBrush(Color.FromRgb(0x7C, 0x3A, 0xED), cyan, 0); var accentText = Brushes.White;
            var rows = new[] { "1234567890-=", "qwertyuiop[]", "asdfghjkl;'", "zxcvbnm,./" };
            const string shifted = "!@#$%^&*()_+{}:\"<>?";
            const string shiftSrc = "1234567890-=[];',./";
            var letters = new System.Collections.Generic.List<(Button B, char C)>();
            var root = new StackPanel { Margin = new Thickness(10) };
            Func<object, double, Button> key = (content, w) =>
            {
                var b = new Button { Content = content, Width = w, Height = 46, Margin = new Thickness(3), Focusable = false, FontSize = 18, Foreground = Brushes.White, Background = keyBg, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
                var tpl = new ControlTemplate(typeof(Button));
                var bd = new FrameworkElementFactory(typeof(Border)); bd.Name = "bd";
                bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                bd.AppendChild(cp); tpl.VisualTree = bd;
                var tr = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; tr.Setters.Add(new Setter(Border.BackgroundProperty, keyHover, "bd")); tpl.Triggers.Add(tr);
                var tp = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true }; tp.Setters.Add(new Setter(Border.BackgroundProperty, accent, "bd")); tp.Setters.Add(new Setter(TextElement.ForegroundProperty, accentText, "bd")); tpl.Triggers.Add(tp);
                b.Template = tpl; return b;
            };
            Action refresh = null;
            Func<char, char> map = c =>
            {
                if (alt) { var i = "acelnoszx".IndexOf(char.ToLower(c)); if (i >= 0) c = "ąćęłńóśżź"[i]; }
                bool up = shift ^ caps;
                if (char.IsLetter(c)) return up ? char.ToUpper(c) : c;
                if (shift) { var i = shiftSrc.IndexOf(c); if (i >= 0) return shifted[i]; }
                return c;
            };
            Button shiftB = null, capsB = null, altB = null;
            foreach (var r in rows)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                if (r == rows[1]) { var t = key("⇥", 66); t.Click += (s, e) => SendKey(0x09, '\0'); row.Children.Add(t); }
                if (r == rows[2]) { capsB = key("⇪", 78); capsB.Click += (s, e) => { caps = !caps; refresh(); }; row.Children.Add(capsB); }
                if (r == rows[3]) { shiftB = key("⇧", 96); shiftB.Click += (s, e) => { shift = !shift; refresh(); }; row.Children.Add(shiftB); }
                foreach (var c0 in r)
                {
                    var c = c0; var b = key(c.ToString(), 46);
                    b.Click += (s, e) => { SendKey(0, map(c)); if (shift || alt) { shift = false; alt = false; refresh(); } };
                    letters.Add((b, c)); row.Children.Add(b);
                }
                if (r == rows[0]) { var bs = key("⌫", 86); bs.Click += (s, e) => SendKey(0x08, '\0'); row.Children.Add(bs); }
                if (r == rows[2]) { var en = key("⏎", 96); en.Background = accent; en.Foreground = accentText; en.Click += (s, e) => SendKey(0x0D, '\0'); row.Children.Add(en); }
                if (r == rows[3]) { var up = key("▲", 46); up.Click += (s, e) => SendKey(0x26, '\0'); row.Children.Add(up); }
                root.Children.Add(row);
            }
            var last = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            altB = key("ą ę ł", 96); altB.FontSize = 15; altB.ToolTip = L.T("Polskie znaki (jak prawy Alt)"); altB.Click += (s, e) => { alt = !alt; refresh(); };
            var at = key("@", 46); at.Click += (s, e) => SendKey(0, '@');
            var space = key("", 380); space.Click += (s, e) => SendKey(0, ' ');
            var dot = key(".com", 66); dot.FontSize = 15; dot.Click += (s, e) => { foreach (var ch in ".com") SendKey(0, ch); };
            var lf = key("◀", 46); lf.Click += (s, e) => SendKey(0x25, '\0');
            var dn = key("▼", 46); dn.Click += (s, e) => SendKey(0x28, '\0');
            var rt = key("▶", 46); rt.Click += (s, e) => SendKey(0x27, '\0');
            foreach (var b in new[] { altB, at, space, dot, lf, dn, rt }) last.Children.Add(b);
            root.Children.Add(last);
            refresh = () =>
            {
                foreach (var (b, c) in letters) b.Content = map(c).ToString();
                foreach (var (b, on) in new[] { (shiftB, shift), (capsB, caps), (altB, alt) }) { b.Background = on ? accent : keyBg; b.Foreground = on ? accentText : Brushes.White; }
            };
            // naglowek do przeciagania i zamkniecia
            var head = new DockPanel { Margin = new Thickness(14, 8, 8, 0), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            var close = key("✕", 34); close.Height = 28; close.FontSize = 13; close.Background = Brushes.Transparent; DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            var title = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White };
            title.Inlines.Add(new System.Windows.Documents.Run(L.T("Klawiatura") + " "));
            title.Inlines.Add(new System.Windows.Documents.Run("Velivo") { FontWeight = FontWeights.SemiBold });
            // z przodu 3 smugi jak w logo: coraz krotsze i bledsze, wyrownane do prawej
            var waves = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var (len, op) in new[] { (18.0, 1.0), (14.0, 0.7), (10.0, 0.45) })
                waves.Children.Add(new Border { Width = len, Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(18 - len, 1.5, 0, 1.5), Opacity = op, Background = new LinearGradientBrush(violet, cyan, 0) });
            DockPanel.SetDock(waves, Dock.Left); head.Children.Add(waves);
            head.Children.Add(title);
            var all = new DockPanel(); DockPanel.SetDock(head, Dock.Top); all.Children.Add(head); all.Children.Add(root);
            var w = new Window
            {
                Title = L.T("Klawiatura Velivo"), WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight, Topmost = true, ShowActivated = false, ShowInTaskbar = false,
                Content = new Border { Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromRgb(0x1E, 0x18, 0x38), 0), new GradientStop(Color.FromRgb(0x19, 0x1B, 0x33), 0.55), new GradientStop(Color.FromRgb(0x13, 0x2A, 0x35), 1) }, new Point(0, 0), new Point(1, 1)),
                    CornerRadius = new CornerRadius(14), BorderBrush = new LinearGradientBrush(Color.FromArgb(0x90, 0x8B, 0x5C, 0xF6), Color.FromArgb(0x90, 0x06, 0xB6, 0xD4), 45), BorderThickness = new Thickness(1), Child = all,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 0, Opacity = 0.55, Color = violet } }
            };
            head.MouseLeftButtonDown += (s, e) => { try { w.DragMove(); } catch (InvalidOperationException) { } };
            close.Click += (s, e) => w.Close();
            w.SourceInitialized += (s, e) =>
            {   // okno nie zabiera fokusu - pole na stronie / w Velivo zostaje aktywne
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x08000000);
            };
            w.Loaded += (s, e) =>
            {   // zapamietane miejsce (gdy na ekranie), a pierwszy raz: na dole ekranu, na srodku
                var wa = SystemParameters.WorkArea; var p = (GetPref("klawiatura-poz") ?? "").Split(';');
                if (p.Length == 2 && double.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) && double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
                    && x >= SystemParameters.VirtualScreenLeft && y >= SystemParameters.VirtualScreenTop && x + 120 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && y + 60 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight) { w.Left = x; w.Top = y; }
                else { w.Left = wa.Left + (wa.Width - w.ActualWidth) / 2; w.Top = wa.Bottom - w.ActualHeight - 10; }
            };
            w.Closing += (s, e) => SetPref("klawiatura-poz", w.Left.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";" + w.Top.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.Closed += (s, e) => _kbd = null;
            _kbd = w; w.Show();
        }

        // pola tekstowe w oknach Velivo (wyszukiwanie, nazwy, hasla, edytowalne listy)
        internal static void KeyboardOnMiddleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            var d = e.OriginalSource as DependencyObject;
            while (d != null && !(d is TextBox) && !(d is PasswordBox) && !(d is ComboBox)) d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            if (d is ComboBox cb && !cb.IsEditable) return;
            if (d is TextBox tb && tb.IsReadOnly) return;
            if (d == null) return;
            ((UIElement)d).Focus(); e.Handled = true;
            ShowScreenKeyboard();
        }

        // strona: klik rolka w pole do pisania - fokus w polu i znacznik do okna (bez tresci); bez autoprzewijania
        const string KeyboardPageScript = "(function(){try{if(!window.chrome||!chrome.webview)return;var pm=chrome.webview.postMessage.bind(chrome.webview);document.addEventListener('mousedown',function(e){if(e.button!==1)return;var t=e.target;if(!t||!t.closest)return;var f=t.closest('input,textarea,[contenteditable=\"\"],[contenteditable=true]');if(!f||f.disabled||f.readOnly)return;if(f.tagName==='INPUT'&&/^(button|submit|reset|checkbox|radio|file|image|range|color|hidden)$/i.test(f.type))return;e.preventDefault();try{f.focus();}catch(x){}pm('velivo:klawiatura');},true);}catch(x){}})();";
    }
}
