using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // ⌨ podpowiedzi przy kursorze (makieta dokumentacja/makieta-klawiatura-podpowiedzi.html): przy zwykłej klawiaturze, także gdy okno klawiatury
    // Velivo jest zamknięte - mały pasek pod kursorem tekstu w dowolnym programie: następne słowo ze zwrotu (Tab, Tab Tab - kolejne) i reszta
    // często pisanego zdania (Ctrl+Spacja). Bez zabierania kursora, znika po 3 s, Esc albo dalszym pisaniu. Wyłączane w menu klawiatury („klawiatura-podpowiedzi”).
    public partial class MainWindow
    {
        static Window _pod; static StackPanel _podRow; static System.Windows.Threading.DispatcherTimer _podTm;
        static volatile List<string> _podSug; static volatile bool _podDone; static volatile string _podWord;   // to, co pokazuje pasek (czytane w wątku klawiatury)
        static int _podIdx; static long _podTabAt;
        internal static bool PodOn => GetPref("klawiatura-podpowiedzi") != "0";

        [StructLayout(LayoutKind.Sequential)] struct GuiInfo { public int Size, Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public RECT Rc; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GuiInfo gi);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref PT p);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("user32.dll", EntryPoint = "GetCursorPos")] static extern bool PodMouse(out PT p);

        // wątek okna: po słowie (done) - następne słowa; w trakcie - dokończenia; do tego reszta zdania
        static void PodShow(string word, bool done)
        {
            if (!PodOn || _kbd != null || _physSecret || word == null || GetPref("klawiatura-korekta") == "0") { PodHide(); return; }
            var rest = SwipeTyping.SentenceRest(_sent.Text(true) + (done ? "" : word)); _sentRest = rest;
            var sug = done ? SwipeTyping.NextWords(word.ToLowerInvariant(), _pPrevWord2) : word.Length >= 2 ? SwipeTyping.Predict(word, false).Where(x => !x.Equals(word, StringComparison.OrdinalIgnoreCase)).ToList() : new List<string>();
            if (sug.Count == 0 && rest == null) { PodHide(); return; }
            _podSug = sug; _podDone = done; _podWord = word; _podIdx = 0;
            PodEnsure(); _podTm.Interval = TimeSpan.FromSeconds(3);
            _podRow.Children.Clear();
            TextBlock Chip(string t, bool on) => new TextBlock { Text = t, Foreground = Brushes.White, FontSize = 13, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 3, 0),
                Background = on ? new LinearGradientBrush(Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x06, 0xB6, 0xD4), 0) : Brushes.Transparent };
            for (int i = 0; i < Math.Min(3, sug.Count); i++) _podRow.Children.Add(Chip(sug[i], i == 0));
            if (rest != null) _podRow.Children.Add(Chip("… " + (rest.Length > 40 ? rest.Substring(0, 40) + "…" : rest.TrimStart()), sug.Count == 0));
            _podRow.Children.Add(new TextBlock { Text = (sug.Count > 0 ? L.T("Tab – wstaw") : "") + (sug.Count > 0 && rest != null ? " · " : "") + (rest != null ? L.T("Ctrl+Spacja – reszta zdania") : ""), Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA3)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
            PodPlace();
        }
        // krótki komunikat w tym samym pasku (np. Ctrl+Spacja: dodano / już jest w słowniku) - 2,5 s
        internal static void PodMsg(string text)
        {
            if (_kbd != null) return;   // okno klawiatury pokazuje to u siebie
            PodEnsure(); _podSug = null; _podRow.Children.Clear();
            _podRow.Children.Add(new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13, Padding = new Thickness(9, 3, 9, 3) });
            _podTm.Interval = TimeSpan.FromSeconds(2.5); PodPlace();
        }
        static void PodEnsure()
        {
            if (_pod != null) return;
            _podRow = new StackPanel { Orientation = Orientation.Horizontal };
            _pod = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true, ShowActivated = false, Focusable = false, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x14, 0x18, 0x21)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x3F, 0x6B)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(4), Child = _podRow } };
            _pod.SourceInitialized += (s, e) => { var h = new System.Windows.Interop.WindowInteropHelper(_pod).Handle; SetWindowLongPtr(h, -20, (IntPtr)(GetWindowLongPtr(h, -20).ToInt64() | 0x08000000 | 0x80 | 0x20)); };   // bez aktywacji, poza Alt+Tab, przezroczysty dla myszy
            _podTm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; _podTm.Tick += (s, e) => PodHide();
        }
        static void PodPlace()
        {
            var pt = PodWhere();
            var src = PresentationSource.FromVisual(Application.Current.MainWindow ?? _pod);
            var dip = src?.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice.Transform(new Point(pt.X, pt.Y)) : new Point(pt.X, pt.Y);
            _pod.Left = dip.X; _pod.Top = dip.Y + 6;
            if (!_pod.IsVisible) _pod.Show();
            _podTm.Stop(); _podTm.Start();   // znika sam
        }
        static void PodHide() { _podSug = null; try { if (_pod != null && _pod.IsVisible) _pod.Hide(); _podTm?.Stop(); } catch (Exception) { } }

        // miejsce kursora tekstu (pod nim); program bez systemowego kursora - przy myszy
        static PT PodWhere()
        {
            var fg = GetForegroundWindow(); var gi = new GuiInfo { Size = Marshal.SizeOf(typeof(GuiInfo)) };
            if (fg != IntPtr.Zero && GetGUIThreadInfo(GetWindowThreadProcessId(fg, IntPtr.Zero), ref gi) && gi.Caret != IntPtr.Zero && gi.Rc.B > gi.Rc.T)
            { var p = new PT { X = gi.Rc.L, Y = gi.Rc.B }; if (ClientToScreen(gi.Caret, ref p)) return p; }
            PodMouse(out var m); m.Y += 16; return m;
        }

        // wątek klawiatury: Tab przy widocznym pasku = wstaw podpowiedź (dwa szybkie Tab - następną zamiast poprzedniej); Esc - schowaj
        static List<string> _podCycle; static int _podDel;
        static bool PodKey(int vk, bool ctrl, bool alt, bool sh)
        {
            var sug = _podSug;
            if (vk == 0x1B) { if (sug != null) Application.Current?.Dispatcher.BeginInvoke(new Action(PodHide)); return false; }
            if (vk != 0x09 || ctrl || alt || sh) return false;
            long now = Environment.TickCount64; bool again = _podCycle != null && now - _podTabAt < 700;
            if (!again && (sug == null || sug.Count == 0)) return false;   // bez paska Tab działa jak zawsze
            var ins = new List<KbdInput>(); string w;
            if (again)
            {   // Tab Tab: zamiast wstawionej podpowiedzi - następna z tej samej listy
                _podIdx = (_podIdx + 1) % _podCycle.Count; w = _podCycle[_podIdx];
                ins.AddRange(Enumerable.Range(0, _podDel).SelectMany(_ => KeyIn(0x08, '\0'))); ins.AddRange(TextIn(w + " ")); _podDel = w.Length + 1;
            }
            else
            {
                _podCycle = sug; _podIdx = 0; w = sug[0]; var typed = _podWord ?? "";
                if (_podDone) { ins.AddRange(TextIn(w + " ")); _podDel = w.Length + 1; }
                else { if (typed.Length > 0 && char.IsUpper(typed[0]) && w.Length > 0) w = char.ToUpper(w[0]) + w.Substring(1);   // „Wsz” → „Wszystko”
                    ins.AddRange(Enumerable.Range(0, typed.Length).SelectMany(_ => KeyIn(0x08, '\0'))); ins.AddRange(TextIn(w + " ")); _podDel = w.Length + 1; }   // całe słowo od nowa (wielkość liter z podpowiedzi)
            }
            Inject(ins); _podTabAt = now;
            PhysReset(); _pPrev = ' '; _pPrevSpace = true; _pPrevWord = w; _pLast = now;
            var at = now; var ww = w;
            System.Threading.Tasks.Task.Delay(750).ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(new Action(() => { if (_podTabAt == at) { _podCycle = null; PodShow(ww, true); } })));   // po chwili - co dalej
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => _pod?.Hide()));
            return true;
        }
    }
}
