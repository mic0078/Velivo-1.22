using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        const uint KeyUp = 2, KeyUnicode = 4;

        static Window _kbd;
        internal static volatile bool KeyboardOn;   // Ustawienia: klawiatura wlaczona (domyslnie nie - tylko na zyczenie)
        static DateTime _shutdownAt = DateTime.MinValue;   // zaplanowane wylaczenie komputera z klawiatury
        // pisanie przeciagnieciem: wybierane slowa i wlasny slownik
        static readonly Dictionary<string, int> _swLearned = new Dictionary<string, int>();
        static readonly HashSet<string> _swOwn = new HashSet<string>();
        static string SwipeFile => Path.Combine(DataDir, "klawiatura-slowa.bin");   // zaszyfrowany (DPAPI, konto Windows)
        static volatile bool _pageSecret;   // strona: kursor w polu hasla (zglasza skrypt strony)

        // czy pole, do ktorego teraz piszemy, jest polem hasla - wtedy NIC nie trafia do nauki ani Mojego slownika;
        // gdy nie da sie tego sprawdzic - tez traktujemy jak haslo (bezpieczniej nie uczyc)
        static bool SecretField()
        {
            try
            {
                GetWindowThreadProcessId(_kbdTarget, out var pid);
                if (pid == (uint)Environment.ProcessId)
                {
                    var f = Keyboard.FocusedElement;
                    if (f is PasswordBox) return true;
                    if (f is Microsoft.Web.WebView2.Wpf.WebView2 || f is DependencyObject d && FindAncestor<Microsoft.Web.WebView2.Wpf.WebView2>(d) != null) return _pageSecret;
                    return false;
                }
                var el = System.Windows.Automation.AutomationElement.FocusedElement;
                return el == null || el.Current.IsPassword;
            }
            catch (Exception) { return true; }
        }

        static T FindAncestor<T>(DependencyObject d) where T : class
        {
            for (; d != null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)) if (d is T t) return t;
            return null;
        }

        // jezyk klawiatury (slownik przeciagania, czytanie na glos): wybrany przelacznikiem, domyslnie jezyk Velivo
        static string KbLang => GetPref("klawiatura-jezyk") ?? (L.En ? "en" : "pl");
        static string _swLang;
        // jezyk czytania na glos (osobno od jezyka klawiatury), domyslnie jak klawiatura
        static string ReadLang => GetPref("klawiatura-czytaj-jezyk") ?? KbLang;

        // slownik wbudowany (polski albo angielski) + wlasne slowa - w tle, przy otwarciu i zmianie jezyka
        static void LoadSwipeDictionary()
        {
            var lang = KbLang;
            if (SwipeTyping.Loaded && _swLang == lang) return;
            _swLang = lang;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var asm = typeof(MainWindow).Assembly;
                    var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("slowa-" + lang + ".txt", StringComparison.Ordinal));
                    if (name == null) return;
                    var words = new List<string>();
                    using (var st = asm.GetManifestResourceStream(name)) using (var rd = new StreamReader(st, System.Text.Encoding.UTF8)) { string l; while ((l = rd.ReadLine()) != null) words.Add(l); }
                    try { File.Delete(Path.Combine(DataDir, "klawiatura-slowa.txt")); } catch (Exception) { }   // dawny jawny plik - nauka od nowa, juz zaszyfrowana
                    var own = new HashSet<string>(); var learned = SwipeTyping.LoadLearned(SwipeFile, own);
                    SwipeTyping.Load(words);
                    foreach (var w in own) SwipeTyping.AddUserWord(w);
                    lock (_swLearned) { foreach (var kv in learned) _swLearned[kv.Key] = kv.Value; foreach (var w in own) _swOwn.Add(w); }
                }
                catch (Exception ex) { App.LogError(ex); }
            });
        }
        static IntPtr _kbdHwnd, _kbdTarget;   // okno klawiatury i okno, do ktorego piszemy

        // kombinacja klawiszy (np. Win+H - dyktowanie Windows): wcisniecie po kolei, puszczenie w odwrotnej kolejnosci
        static void SendCombo(params ushort[] vks)
        {
            var ins = vks.Select(v => new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = v } } })
                .Concat(vks.Reverse().Select(v => new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = v, Flags = KeyUp } } })).ToArray();
            var fg = GetForegroundWindow();
            if (fg == _kbdHwnd && _kbdTarget != IntPtr.Zero) SetForegroundWindow(_kbdTarget);
            SendInput((uint)ins.Length, ins, Marshal.SizeOf(typeof(KbdInput)));
        }

        // przeczytaj zaznaczony tekst z okna, w ktorym sie pisze: Ctrl+C w tym programie (dziala wszedzie, takze OpenOffice),
        // a gdy nic nie zaznaczono - tekst pola z dostepnosci Windows; schowek wraca do poprzedniej zawartosci; pole hasla - nigdy
        static async void ReadSelectionAloud()
        {
            try
            {
                // menu przy przycisku aktywowalo Velivo - najpierw powrot do okna z zaznaczonym tekstem, inaczej Ctrl+C trafia do Velivo
                if (_kbdTarget != IntPtr.Zero && GetForegroundWindow() != _kbdTarget) { SetForegroundWindow(_kbdTarget); await System.Threading.Tasks.Task.Delay(150); }
                bool pass = false; try { pass = System.Windows.Automation.AutomationElement.FocusedElement?.Current.IsPassword == true; } catch (Exception) { }
                if (pass) return;   // pole hasla - nigdy na glos
                string old = null; try { if (Clipboard.ContainsText()) old = Clipboard.GetText(); } catch (Exception) { }
                try { Clipboard.Clear(); } catch (Exception) { }
                SendCombo(0x11, 0x43);   // Ctrl+C
                await System.Threading.Tasks.Task.Delay(250);
                string text = null; try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch (Exception) { }
                try { if (old != null) Clipboard.SetText(old); else Clipboard.Clear(); } catch (Exception) { }
                if (string.IsNullOrWhiteSpace(text))
                    try
                    {   // bez zaznaczenia: cale pole (gdy program je udostepnia)
                        var el = System.Windows.Automation.AutomationElement.FocusedElement;
                        if (el != null && !el.Current.IsPassword)
                        {
                            if (el.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var tp)) text = ((System.Windows.Automation.TextPattern)tp).DocumentRange.GetText(5000);
                            else if (el.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var vp)) text = ((System.Windows.Automation.ValuePattern)vp).Current.Value;
                        }
                    }
                    catch (Exception) { }
                if (string.IsNullOrWhiteSpace(text)) { (Application.Current?.MainWindow as MainWindow)?.ShowToast(L.T("🗣 Nic do przeczytania – zaznacz tekst myszą i spróbuj jeszcze raz."), null); return; }
                (Application.Current?.MainWindow as MainWindow)?.KbSpeak(text.Length > 3000 ? text.Substring(0, 3000) : text, ReadLang);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // czytanie na glos tego, co sie pisze - glosem lektora Velivo (Piper albo glos Windows), slowo po slowie
        System.Windows.Media.MediaPlayer _kbdVoice;
        void KbStopSpeaking() { try { _kbdVoice?.Stop(); } catch (Exception) { } }

        async void KbSpeak(string text, string lang)
        {
            try
            {
                var wav = await LectorSynthesizeAsync(text, lang, 1.0);
                if (wav == null) { ShowToast(L.T("🗣 Brak głosu lektora – zainstaluj głos w Ustawieniach (Czytanie na głos)."), null); return; }
                if (_kbdVoice == null) _kbdVoice = new System.Windows.Media.MediaPlayer();
                var file = wav; EventHandler done = null;
                done = (s, e) => { _kbdVoice.MediaEnded -= done; try { File.Delete(file); } catch (Exception) { } };
                _kbdVoice.MediaEnded += done;
                _kbdVoice.Open(new Uri(wav)); _kbdVoice.Play();
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        static void SendKey(ushort vk, char ch)
        {
            uint fl = vk == 0 ? KeyUnicode : 0;
            ushort sc = vk == 0 ? ch : (ushort)0;
            var ins = new[] { new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = vk, Scan = sc, Flags = fl } } }, new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = vk, Scan = sc, Flags = fl | KeyUp } } } };
            // klik w klawiature mogl ja aktywowac - znaki maja trafic do okna z polem, nie do klawiatury
            var fg = GetForegroundWindow();
            if (fg == _kbdHwnd && _kbdTarget != IntPtr.Zero) SetForegroundWindow(_kbdTarget);
            else if (fg != _kbdHwnd && fg != IntPtr.Zero) _kbdTarget = fg;
            SendInput(2, ins, Marshal.SizeOf(typeof(KbdInput)));
        }

        // klik rolka w pole do pisania w INNYM programie (gdy Velivo dziala): globalny podsluch myszy, tylko srodkowy przycisk;
        // sprawdzenie pola (UI Automation) poza podsluchem, zeby nie spowalniac myszy w systemie
        delegate IntPtr LowLevelProc(int code, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LowLevelProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wp, IntPtr lp);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PointStruct p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [StructLayout(LayoutKind.Sequential)] struct PointStruct { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MsgStruct { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }
        [DllImport("user32.dll")] static extern int GetMessage(out MsgStruct m, IntPtr h, uint min, uint max);
        // pad (Xbox i zgodne z XInput - wiekszosc popularnych; PlayStation przez Steam / DS4Windows)
        [StructLayout(LayoutKind.Sequential)] struct PadState { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int XInputGetState(int user, out PadState st);
        static LowLevelProc _mouseProc;   // trzymane, zeby GC nie zebral delegata
        static IntPtr _mouseHook;
        static bool _hookStarted;
        static System.Windows.Threading.DispatcherTimer _holdTimer;   // przytrzymanie rolki 1 s = klawiatura wszedzie
        static PointStruct _holdAt;

        internal static void StartGlobalKeyboardHook()
        {
            if (_hookStarted) return;
            _hookStarted = true;
            _mouseProc = (code, wp, lp) =>
            {
                if (!KeyboardOn) { }   // wylaczona w ustawieniach - nic nie robimy, tylko przekazujemy dalej
                else if (code >= 0 && (int)wp == 0x0207)   // WM_MBUTTONDOWN
                {
                    var pt = Marshal.PtrToStructure<PointStruct>(lp);
                    _holdAt = pt;
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() => { _holdTimer.Stop(); _holdTimer.Start(); CheckForeignField(pt); }));
                }
                else if (code >= 0 && (int)wp == 0x0208)   // WM_MBUTTONUP - puszczona przed czasem: bez wymuszania
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() => _holdTimer.Stop()));
                return CallNextHookEx(_mouseHook, code, wp, lp);
            };
            // rolka przytrzymana 1 s: klawiatura zawsze, bez sprawdzania pola (dokumenty, edytory) - pisze tam, gdzie jest kursor tekstu
            _holdTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _holdTimer.Tick += (s, e) =>
            {
                _holdTimer.Stop();
                var hw = WindowFromPoint(_holdAt); var top = hw == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hw, 2);
                GetWindowThreadProcessId(hw, out var pid);
                if (top != IntPtr.Zero && pid != (uint)Environment.ProcessId) SetForegroundWindow(top);
                ShowScreenKeyboard(top);
            };
            // podsluch w osobnym watku z wlasna petla wiadomosci - ruch myszy w calym systemie nie czeka na okno Velivo
            var th = new System.Threading.Thread(() =>
            {
                _mouseHook = SetWindowsHookEx(14, _mouseProc, GetModuleHandle(null), 0);   // WH_MOUSE_LL
                if (_mouseHook == IntPtr.Zero) { App.LogError(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error())); return; }
                while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { }
            }) { IsBackground = true, Name = "Velivo-rolka" };
            th.Start();
        }

        static void CheckForeignField(PointStruct pt)
        {
            var hw = WindowFromPoint(pt);
            if (hw == IntPtr.Zero) return;
            GetWindowThreadProcessId(hw, out var pid);
            if (pid == (uint)Environment.ProcessId) return;   // wlasne okna i strony - obsluzone w Velivo
            var top = GetAncestor(hw, 2);   // GA_ROOT
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // programy na Chromium / WebView2 (WhatsApp, Discord, aplikacja Claude) buduja drzewo dostepnosci dopiero
                    // przy pierwszym pytaniu - jesli za pierwszym razem nie ma pola, pytamy jeszcze raz chwile pozniej
                    var el = FindEditable(System.Windows.Automation.AutomationElement.FromPoint(new Point(pt.X, pt.Y)));
                    if (el == null) { System.Threading.Thread.Sleep(250); el = FindEditable(System.Windows.Automation.AutomationElement.FromPoint(new Point(pt.X, pt.Y))); }
                    if (el == null)
                    {   // do zgloszenia: w jakim programie i na czym kliknieto, gdy pola nie rozpoznano
                        try
                        {
                            var u = System.Windows.Automation.AutomationElement.FromPoint(new Point(pt.X, pt.Y));
                            File.AppendAllText(Path.Combine(DataDir, "klawiatura.log"), DateTime.Now.ToString("s") + "  " + System.Diagnostics.Process.GetProcessById((int)pid).ProcessName + "  " + u.Current.ControlType.ProgrammaticName + "  " + u.Current.ClassName + "  " + u.Current.FrameworkId + "\n");
                        }
                        catch (Exception) { }
                        return;
                    }
                    try { el.SetFocus(); } catch (Exception) { }
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() => { SetForegroundWindow(top); ShowScreenKeyboard(top); }));
                }
                catch (Exception) { }   // program bez dostepnosci - bez klawiatury
            });
        }

        // pole do pisania pod kursorem albo jego rodzic (w polu wiadomosci pod myszka bywa tekst / akapit, a polem jest element wyzej)
        static System.Windows.Automation.AutomationElement FindEditable(System.Windows.Automation.AutomationElement el)
        {
            var walker = System.Windows.Automation.TreeWalker.RawViewWalker;
            for (int i = 0; el != null && i < 8; i++, el = walker.GetParent(el))
            {
                var ct = el.Current.ControlType;
                if (ct == System.Windows.Automation.ControlType.Window || ct == System.Windows.Automation.ControlType.Pane && i > 0) break;   // dalej to juz cale okno
                if (IsEditable(el)) return el;
            }
            return null;
        }

        static bool IsEditable(System.Windows.Automation.AutomationElement el)
        {
            var ct = el.Current.ControlType;
            if (!el.Current.IsEnabled) return false;
            bool writable = el.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var vp) && !((System.Windows.Automation.ValuePattern)vp).Current.IsReadOnly;
            if (ct == System.Windows.Automation.ControlType.Edit || ct == System.Windows.Automation.ControlType.ComboBox)
                return writable || el.Current.IsKeyboardFocusable && !(el.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out _));
            // dokument (cala strona) tylko gdy da sie do niego pisac - inaczej klik rolka w zwykla strone otwieralby klawiature
            return ct == System.Windows.Automation.ControlType.Document && writable;
        }

        internal static void ShowScreenKeyboard() { ShowScreenKeyboard(IntPtr.Zero); }

        [DllImport("user32.dll")] static extern bool GetCursorPos(out PointStruct p);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr wp, IntPtr lp);

        // zwykla klawiatura -> fala na klawiaturze ekranowej; podsluch tylko gdy klawiatura Velivo jest otwarta, nic nie zapisuje
        static Action<int> _onPhysKey;
        static LowLevelProc _keyProc; static IntPtr _keyHook; static uint _keyThread;
        static void StartKeyEcho()
        {
            if (_keyHook != IntPtr.Zero || _keyThread != 0) return;
            _keyProc = (code, wp, lp) =>
            {
                if (code >= 0 && ((int)wp == 0x100 || (int)wp == 0x104))   // WM_KEYDOWN / WM_SYSKEYDOWN
                {
                    int vk = Marshal.ReadInt32(lp), flags = Marshal.ReadInt32(lp, 8);
                    if ((flags & 0x10) == 0) { var f = _onPhysKey; if (f != null) Application.Current?.Dispatcher.BeginInvoke(new Action(() => f(vk))); }   // bez znakow wyslanych przez sama klawiature Velivo
                }
                return CallNextHookEx(_keyHook, code, wp, lp);
            };
            var th = new System.Threading.Thread(() =>
            {
                _keyThread = (uint)GetCurrentThreadId();
                _keyHook = SetWindowsHookEx(13, _keyProc, GetModuleHandle(null), 0);   // WH_KEYBOARD_LL
                while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { }
                if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
                _keyHook = IntPtr.Zero; _keyThread = 0;
            }) { IsBackground = true, Name = "Velivo-klawisze" };
            th.Start();
        }
        static void StopKeyEcho()
        {
            _onPhysKey = null;
            if (_keyThread != 0) PostThreadMessage(_keyThread, 0x12, IntPtr.Zero, IntPtr.Zero);   // WM_QUIT - koniec podsluchu
        }

        // jak zagiel (Velivo): klawiatura wplywa lagodnie od dolu i rozjasnia sie, przy zamknieciu odplywa w dol i gasnie (ok. 0,3 s)
        static void Sail(Window w, UIElement content, bool open, Action done)
        {
            double top = w.Top, ms = open ? 320 : 220;
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = open ? System.Windows.Media.Animation.EasingMode.EaseOut : System.Windows.Media.Animation.EasingMode.EaseIn };
            var move = new System.Windows.Media.Animation.DoubleAnimation(open ? top + 40 : top, open ? top : top + 40, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
            var fade = new System.Windows.Media.Animation.DoubleAnimation(open ? 0 : 1, open ? 1 : 0, TimeSpan.FromMilliseconds(ms));
            move.Completed += (s, e) =>
            {   // po animacji zwykla pozycja (inaczej animacja trzymalaby okno i nie dalo sie go przesunac)
                w.BeginAnimation(Window.TopProperty, null); w.Top = open ? top : top + 40;
                content.BeginAnimation(UIElement.OpacityProperty, null); content.Opacity = open ? 1 : 0;
                done?.Invoke();
            };
            w.BeginAnimation(Window.TopProperty, move);
            content.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // klawiatura nie moze zaslaniac pola, w ktore kliknieto (kursor myszy) - przesuwa sie pod nie, a gdy brak miejsca - nad nie
        static void AvoidField(Window w)
        {
            try
            {
                if (!GetCursorPos(out var cp)) return;
                var src = PresentationSource.FromVisual(w); if (src?.CompositionTarget == null) return;
                var c = src.CompositionTarget.TransformFromDevice.Transform(new Point(cp.X, cp.Y));
                double h = w.ActualHeight > 0 ? w.ActualHeight : w.Height, wd = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
                if (c.X < w.Left - 20 || c.X > w.Left + wd + 20 || c.Y < w.Top - 40 || c.Y > w.Top + h + 40) return;   // pole nie jest zasloniete
                var wa = SystemParameters.WorkArea;
                if (c.Y + 50 + h <= wa.Bottom) w.Top = c.Y + 50;
                else if (c.Y - 50 - h >= wa.Top) w.Top = c.Y - 50 - h;
            }
            catch (Exception) { }
        }

        static void ShowScreenKeyboard(IntPtr target)
        {
            _kbdTarget = target != IntPtr.Zero ? target : GetForegroundWindow();   // okno z polem, w ktore kliknieto rolka
            if (_kbd != null) { if (!_kbd.IsVisible) { var wa = SystemParameters.WorkArea; _kbd.Left = wa.Left + (wa.Width - _kbd.ActualWidth) / 2; _kbd.Top = wa.Bottom - _kbd.ActualHeight - 10; _kbd.Show(); if (_kbd.Content is Border bd && bd.Child is Viewbox vb && vb.Child is FrameworkElement fe) Sail(_kbd, fe, true, null); } _kbd.Topmost = false; _kbd.Topmost = true; AvoidField(_kbd); return; }   // schowana - wraca
            LoadSwipeDictionary();
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
            var allKeys = new System.Collections.Generic.List<Button>();   // do kolorow przechodzacych po klawiszach
            Action<Button> wave = null;
            var vkKeys = new Dictionary<int, Button>();   // klawisz zwyklej klawiatury -> klawisz na ekranie (tylko do pokazania fali)   // fala przy nacisnieciu (ustawiana nizej, gdy jest juz plotno)
            Func<object, double, Button> key = (content, w) =>
            {
                var b = new Button { Content = content, Width = w, Height = 46, Margin = new Thickness(2), Focusable = false, FontSize = 18, Foreground = Brushes.White, Background = keyBg, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
                var tpl = new ControlTemplate(typeof(Button));
                var bd = new FrameworkElementFactory(typeof(Border)); bd.Name = "bd";
                bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                bd.AppendChild(cp); tpl.VisualTree = bd;
                var tr = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; tr.Setters.Add(new Setter(Border.BackgroundProperty, keyHover, "bd")); tpl.Triggers.Add(tr);
                var tp = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true }; tp.Setters.Add(new Setter(Border.BackgroundProperty, accent, "bd")); tp.Setters.Add(new Setter(TextElement.ForegroundProperty, accentText, "bd")); tpl.Triggers.Add(tp);
                b.Template = tpl; allKeys.Add(b); b.Click += (s, e) => wave?.Invoke(b); return b;
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
            // nauka slow (jak na Androidzie): slowo wpisane litera po literze trafia do wlasnego slownika po spacji / Enter / znaku
            var title = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White };
            title.Inlines.Add(new System.Windows.Documents.Run("Velivo") { FontWeight = FontWeights.SemiBold });
            title.Inlines.Add(new System.Windows.Documents.Run(" by Michael") { Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)) });
            List<Point> lastTrace = null; double lastKeyW = 52; string cur = "", lastSwipe = null; var sugs = new List<Button>();   // biezace slowo z klawiszy; ostatnie slowo z przeciagniecia (do podmiany z podpowiedzi)
            var ask = new TextBlock { Text = L.T("Poprawić na:"), FontSize = 13, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Visibility = Visibility.Collapsed };
            string prevWord = null, prevBeforeSwipe = null;   // poprzednie slowo - do nauki kolejnosci slow
            Action<string> learn = wd =>
            {
                if (wd.Length < 2 || !SwipeTyping.Loaded || SecretField()) return;   // pole hasla - bez nauki
                var lw = wd.ToLowerInvariant();
                SwipeTyping.LearnPair(prevWord, lw); prevWord = lw;   // kolejnosc slow
                lock (_swLearned)
                {
                    _swLearned[lw] = (_swLearned.TryGetValue(lw, out var n) ? n : 0) + 1;
                    if (!SwipeTyping.IsKnown(lw)) { _swOwn.Add(lw); SwipeTyping.AddUserWord(lw); }
                    SwipeTyping.SaveLearned(SwipeFile, _swLearned, _swOwn);
                }
            };
            Action<string> say = wd =>
            {   // czytanie na glos skonczonego slowa (nigdy w polu hasla)
                if (wd.Length == 0 || GetPref("klawiatura-czytaj") != "1" || SecretField()) return;
                (Application.Current?.MainWindow as MainWindow)?.KbSpeak(wd, ReadLang);
            };
            Action<ushort, char> tap = (vk, ch) =>
            {
                SendKey(vk, ch); lastSwipe = null; foreach (var x in sugs) x.Visibility = Visibility.Collapsed; title.Visibility = Visibility.Visible; ask.Visibility = Visibility.Collapsed;
                if (vk == 0 && char.IsLetter(ch)) cur += ch;
                else if (vk == 0x08) { if (cur.Length > 0) cur = cur.Substring(0, cur.Length - 1); }
                else { learn(cur); say(cur); cur = ""; }
            };
            // propozycje po przeciagnieciu - w naglowku zamiast napisu (bez osobnego, zwykle pustego rzedu); klik podmienia ostatnie slowo
            var sugRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sugRow.Children.Add(title);
            sugRow.Children.Add(ask);
            for (int i = 0; i < 3; i++)
            {
                var sb = key("", double.NaN); sb.Height = 26; sb.MinWidth = 70; sb.Padding = new Thickness(12, 0, 12, 0); sb.FontSize = 14; sb.Visibility = Visibility.Collapsed;
                sb.Click += (s, e) =>
                {
                    var alt0 = ((Button)s).DataContext as string;
                    if (lastSwipe == null || alt0 == null) return;
                    for (int k = 0; k <= lastSwipe.Length; k++) SendKey(0x08, '\0');   // slowo + spacja
                    var lw = lastSwipe.ToLowerInvariant(); if (_swLearned.TryGetValue(lw, out var n) && n > 0) _swLearned[lw] = n - 1;   // to nie bylo to slowo
                    foreach (var ch in alt0) SendKey(0, ch); SendKey(0, ' ');
                    prevWord = prevBeforeSwipe;   // kolejnosc slow: poprawione slowo nastepuje po tym samym poprzednim
                    SwipeTyping.ForgetShape(lastSwipe); if (lastTrace != null) SwipeTyping.LearnShape(alt0, lastTrace, lastKeyW);   // ten ruch znaczyl to slowo
                    learn(alt0); lastSwipe = alt0;
                    foreach (var x in sugs) x.Visibility = Visibility.Collapsed;
                    ask.Visibility = Visibility.Collapsed;
                    title.Visibility = Visibility.Visible;
                };
                sugs.Add(sb); sugRow.Children.Add(sb);
            }
            // rzad skrotow: poczta, glosnosc Windows, Velivo, wylaczenie komputera
            var fnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Func<object, double, string, Action, Button> fn = (txt, wdt, tip, act) =>
            {
                var b = key(txt, wdt); b.Height = 32; b.FontSize = 14; b.ToolTip = tip; b.Click += (s, e) => act(); fnRow.Children.Add(b); return b;
            };
            Func<string, string, object> ico = (glyph, label) =>
            {   // ikona jak na pasku Velivo (Segoe MDL2) + opcjonalny napis
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
                if (label != null) sp.Children.Add(new TextBlock { Text = label, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                return sp;
            };
            fn(ico("\uE715", "E-mail"), 110, L.T("Otwiera domyślny program pocztowy – nowa wiadomość"), () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mailto:") { UseShellExecute = true }); } catch (Exception ex) { App.LogError(ex); } });
            fn(ico("\uE74F", null), 50, L.T("Wycisz / włącz dźwięk komputera"), () => SendCombo(0xAD));
            fn(ico("\uE993", null), 50, L.T("Ciszej (głośność Windows)"), () => SendCombo(0xAE));
            fn(ico("\uE995", null), 50, L.T("Głośniej (głośność Windows)"), () => SendCombo(0xAF));
            fn(ico("\uE774", "Velivo"), 110, L.T("Pokazuje okno przeglądarki Velivo"), () =>
            {
                var mw = Application.Current?.MainWindow as MainWindow; if (mw == null) return;
                if (!mw.IsVisible) mw.ShowFromTray(); else { if (mw.WindowState == WindowState.Minimized) mw.WindowState = WindowState.Normal; mw.Activate(); }
            });
            fn(ico("\uE7E8", null), 50, L.T("Wyłącz komputer (z potwierdzeniem, 30 s na rozmyślenie)"), () =>
            {
                bool pending = DateTime.Now < _shutdownAt;   // drugi klik w ciagu 30 s - odwolanie
                if (MessageBox.Show(pending ? L.T("Odwołać wyłączanie komputera?") : L.T("Wyłączyć komputer?\n\nZa 30 sekund – zapisz otwarte prace. Kliknij ⏻ jeszcze raz, aby odwołać."),
                    L.T("Wyłączanie komputera"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", pending ? "/a" : "/s /t 30") { CreateNoWindow = true, UseShellExecute = false });
                    _shutdownAt = pending ? DateTime.MinValue : DateTime.Now.AddSeconds(30);
                }
                catch (Exception ex) { App.LogError(ex); }
            });
            root.Children.Add(fnRow);
            Button shiftB = null, capsB = null, altB = null;
            foreach (var r in rows)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                if (r == rows[1]) { var t = key("⇥", 66); vkKeys[0x09] = t; t.ToolTip = L.T("Tab – następne pole"); t.Click += (s, e) => tap(0x09, '\0'); row.Children.Add(t); }
                if (r == rows[2]) { capsB = key("⇪", 78); capsB.ToolTip = L.T("Caps Lock – wszystkie litery wielkie, aż klikniesz znowu"); capsB.Click += (s, e) => { caps = !caps; refresh(); }; row.Children.Add(capsB); }
                if (r == rows[3]) { shiftB = key("⇧", 96); shiftB.ToolTip = L.T("Shift – następna litera wielka (albo znak z góry klawisza)"); shiftB.Click += (s, e) => { shift = !shift; refresh(); }; row.Children.Add(shiftB); }
                foreach (var c0 in r)
                {
                    var c = c0; var b = key(c.ToString(), 46);
                    b.Click += (s, e) => { tap(0, map(c)); if (shift || alt) { shift = false; alt = false; refresh(); } };
                    letters.Add((b, c)); row.Children.Add(b);
                }
                if (r == rows[0]) { var bs = key("⌫", 86); vkKeys[0x08] = bs; bs.ToolTip = L.T("Usuń znak przed kursorem"); bs.Click += (s, e) => tap(0x08, '\0'); row.Children.Add(bs); }
                if (r == rows[2]) { var en = key("⏎", 96); vkKeys[0x0D] = en; en.ToolTip = L.T("Enter – zatwierdź / nowa linia"); en.Background = accent; en.Foreground = accentText; en.Click += (s, e) => tap(0x0D, '\0'); row.Children.Add(en); }
                if (r == rows[3]) { var up = key("▲", 46); vkKeys[0x26] = up; up.ToolTip = L.T("Kursor w górę"); up.Click += (s, e) => tap(0x26, '\0'); row.Children.Add(up); }
                root.Children.Add(row);
            }
            var last = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            altB = key("ą ę ł", 96); altB.FontSize = 15; altB.ToolTip = L.T("Polskie znaki (jak prawy Alt)"); altB.Click += (s, e) => { alt = !alt; refresh(); };
            var at = key("@", 46); at.Click += (s, e) => tap(0, '@');
            var space = key("", 380); vkKeys[0x20] = space; space.ToolTip = L.T("Spacja. Pisanie przeciągnięciem: wciśnij na pierwszej literze słowa i przeciągnij po kolejnych"); space.Click += (s, e) => tap(0, ' ');
            var dot = key(".com", 66); dot.ToolTip = L.T("Wpisuje „.com”"); dot.FontSize = 15; dot.Click += (s, e) => { foreach (var ch in ".com") tap(0, ch); };
            var lf = key("◀", 46); vkKeys[0x25] = lf; lf.ToolTip = L.T("Kursor w lewo"); lf.Click += (s, e) => tap(0x25, '\0');
            var dn = key("▼", 46); vkKeys[0x28] = dn; dn.ToolTip = L.T("Kursor w dół"); dn.Click += (s, e) => tap(0x28, '\0');
            var rt = key("▶", 46); vkKeys[0x27] = rt; rt.ToolTip = L.T("Kursor w prawo"); rt.Click += (s, e) => tap(0x27, '\0');
            foreach (var b in new[] { altB, at, space, dot, lf, dn, rt }) last.Children.Add(b);
            root.Children.Add(last);
            refresh = () =>
            {
                foreach (var (b, c) in letters) b.Content = map(c).ToString();
                foreach (var (b, on) in new[] { (shiftB, shift), (capsB, caps), (altB, alt) }) { b.Background = on ? accent : (b.Tag as Brush ?? keyBg); b.Foreground = on ? accentText : Brushes.White; }
            };
            // naglowek do przeciagania i zamkniecia
            var head = new DockPanel { Margin = new Thickness(14, 8, 8, 0), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            var close = key(ico("\uE711", null), 34); close.Height = 28; close.FontSize = 13; close.Background = Brushes.Transparent; DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            // minimalizacja: klawiatura chowa sie (bez paska zadan), wraca kliknieciem rolka w pole albo przytrzymaniem rolki
            var minB = key(ico("\uE921", null), 34); minB.Height = 28; minB.FontSize = 13; minB.Background = Brushes.Transparent; DockPanel.SetDock(minB, Dock.Right);
            minB.ToolTip = L.T("Schowaj klawiaturę (wraca po kliknięciu rolką w pole albo przytrzymaniu rolki)");
            head.Children.Add(minB);
            // wlasny slownik (jak na Androidzie): podglad i usuwanie wyuczonych slow
            var dictB = key(ico("\uE82D", null), 34); dictB.Height = 28; dictB.FontSize = 13; dictB.Background = Brushes.Transparent; DockPanel.SetDock(dictB, Dock.Right);
            dictB.ToolTip = L.T("Mój słownik – słowa, których Velivo nauczyło się od Ciebie (klik w słowo usuwa je)");
            dictB.Click += (s, e) =>
            {
                var cm = new ContextMenu { PlacementTarget = dictB, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                List<string> own; lock (_swLearned) own = _swOwn.OrderBy(x => x).ToList();
                cm.Items.Add(new MenuItem { Header = own.Count == 0 ? L.T("Mój słownik jest pusty – słowa wpisane literami dopiszą się same") : L.T("Mój słownik (klik usuwa): ") + own.Count, IsEnabled = false });
                foreach (var w0 in own.Take(200))
                {
                    var wd = w0; var mi = new MenuItem { Header = wd };
                    mi.Click += (q, w2) => { lock (_swLearned) { _swOwn.Remove(wd); _swLearned.Remove(wd); SwipeTyping.RemoveUserWord(wd); SwipeTyping.SaveLearned(SwipeFile, _swLearned, _swOwn); } };
                    cm.Items.Add(mi);
                }
                cm.IsOpen = true;
            };
            head.Children.Add(dictB);
            // 🎮 sterowanie padem
            var padB = key(ico("\uE7FC", null), 34); padB.Height = 28; padB.FontSize = 13; padB.Background = Brushes.Transparent; DockPanel.SetDock(padB, Dock.Right);
            padB.ToolTip = L.T("Pad (Xbox i zgodne): krzyżak / lewa gałka – wybór klawisza, A – naciśnij, B – ⌫, X – spacja, Y – ⇧, LB / RB – kursor ◀ ▶, LT – ą ę ł, Start – ⏎, Back – zamknij");
            head.Children.Add(padB);
            // 🎤 dyktowanie (pisanie glosem Windows, Win+H), 🔊 czytanie na glos tego, co piszesz, PL / EN - jezyk
            var micB = key(ico("\uE720", null), 34); micB.Height = 28; micB.FontSize = 13; micB.Background = Brushes.Transparent; DockPanel.SetDock(micB, Dock.Right);
            micB.ToolTip = L.T("Pisz głosem – dyktowanie Windows (Win+H); mówisz, a tekst pojawia się w polu. Potrzebny mikrofon i internet (usługa Microsoft).");
            micB.Click += (s, e) => SendCombo(0x5B, 0x48);
            head.Children.Add(micB);
            var sayB = key(ico("\uE8F2", null), 34); sayB.Height = 28; sayB.FontSize = 13; DockPanel.SetDock(sayB, Dock.Right);
            sayB.ToolTip = L.T("Czytanie na głos: zaznaczony tekst w dowolnym programie albo na bieżąco to, co piszesz (w polach haseł nigdy)");
            Action sayApply = () => sayB.Background = GetPref("klawiatura-czytaj") == "1" ? (Brush)accent : Brushes.Transparent;
            sayB.Click += (s, e) =>
            {
                var cm = new ContextMenu { PlacementTarget = sayB, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                var now = new MenuItem { Header = L.T("🗣 Przeczytaj zaznaczony tekst (każdy program – OpenOffice, Word, strony)") };
                now.Click += (q, w2) => ReadSelectionAloud();
                var live = new MenuItem { Header = L.T("Czytaj na bieżąco to, co piszę"), IsCheckable = true, IsChecked = GetPref("klawiatura-czytaj") == "1" };
                live.Click += (q, w2) => { SetPref("klawiatura-czytaj", live.IsChecked ? "1" : "0"); sayApply(); };
                var stop = new MenuItem { Header = L.T("⏹ Zatrzymaj czytanie") };
                stop.Click += (q, w2) => (Application.Current?.MainWindow as MainWindow)?.KbStopSpeaking();
                var langM = new MenuItem { Header = L.T("Język czytania") };
                foreach (var (code, name) in new[] { ("pl", "Polski"), ("en", "English") })
                {
                    var li = new MenuItem { Header = name, IsCheckable = true, IsChecked = ReadLang == code };
                    li.Click += (q, w2) => SetPref("klawiatura-czytaj-jezyk", code);
                    langM.Items.Add(li);
                }
                cm.Items.Add(now); cm.Items.Add(stop); cm.Items.Add(new Separator()); cm.Items.Add(langM); cm.Items.Add(live); cm.IsOpen = true;
            };
            sayApply(); head.Children.Add(sayB);
            var langB = key(KbLang.ToUpperInvariant(), 40); langB.Height = 28; langB.FontSize = 12.5; langB.FontWeight = FontWeights.SemiBold; langB.Background = Brushes.Transparent; DockPanel.SetDock(langB, Dock.Right);
            langB.ToolTip = L.T("Język klawiatury: polski / angielski (słownik pisania przeciągnięciem i czytanie na głos)");
            langB.Click += (s, e) => { SetPref("klawiatura-jezyk", KbLang == "pl" ? "en" : "pl"); langB.Content = KbLang.ToUpperInvariant(); LoadSwipeDictionary(); };
            head.Children.Add(langB);
            // z przodu logo Velivo: zaokraglony kwadrat w gradiencie, biale "V" i 3 smugi predkosci (coraz bledsze)
            var logo = new Canvas { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            logo.Children.Add(new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6),
                Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(violet, 0), new GradientStop(Color.FromRgb(0x4F, 0x6B, 0xED), 0.5), new GradientStop(cyan, 1) }, new Point(0, 0), new Point(1, 1)) });
            logo.Children.Add(new System.Windows.Shapes.Polyline { Points = new PointCollection { new Point(9.5, 6), new Point(13.8, 18), new Point(18, 6) }, Stroke = Brushes.White, StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
            var fade = new LinearGradientBrush(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), Colors.White, 0);   // smuga zanika ku tylowi
            foreach (var (x, y, w0, h0, op) in new[] { (1.0, 7.6, 6.6, 1.8, 1.0), (2.6, 11.3, 6.2, 1.4, 0.8), (4.2, 15.0, 5.6, 1.0, 0.6) })
            {
                var st = new Border { Width = w0, Height = h0, CornerRadius = new CornerRadius(h0 / 2), Background = fade, Opacity = op };
                Canvas.SetLeft(st, x); Canvas.SetTop(st, y); logo.Children.Add(st);
            }
            DockPanel.SetDock(logo, Dock.Left); head.Children.Add(logo);
            head.Children.Add(sugRow);
            // kolory przechodza po klawiszach jak w logo: lewy gorny fiolet -> niebieski -> prawy dolny turkus (subtelnie)
            root.Loaded += (s, e) =>
            {
                var stops = new[] { violet, Color.FromRgb(0x4F, 0x6B, 0xED), cyan };
                foreach (var b in allKeys)
                {
                    if (!root.IsAncestorOf(b) || b.Background == accent) continue;
                    var c = b.TranslatePoint(new Point(b.ActualWidth / 2, b.ActualHeight / 2), root);
                    double u = Math.Max(0, Math.Min(1, c.X / root.ActualWidth * 0.8 + c.Y / root.ActualHeight * 0.2));
                    int sg = u < 0.5 ? 0 : 1; double f = u < 0.5 ? u * 2 : (u - 0.5) * 2;
                    Func<byte, byte, byte> mix = (x, y) => (byte)(x + (y - x) * f);
                    var br = new SolidColorBrush(Color.FromArgb(0x4D, mix(stops[sg].R, stops[sg + 1].R), mix(stops[sg].G, stops[sg + 1].G), mix(stops[sg].B, stops[sg + 1].B)));
                    b.Tag = br; b.Background = br;
                }
            };
            var keys = new Grid(); keys.Children.Add(root);
            // pisanie przeciagnieciem: slad w gradiencie logo; lekki (punkty co kilka px, bez efektow) - plynny przy ruchu myszy
            var trailC = new Canvas { IsHitTestVisible = false };
            var trail = new System.Windows.Shapes.Polyline { Stroke = new LinearGradientBrush(violet, cyan, 0), StrokeThickness = 7, Opacity = 0.85,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            trailC.Children.Add(trail); keys.Children.Add(trailC);
            // fala jak na wodzie: od nacisnietego klawisza rozchodzi sie kolko w kolorach logo i gasnie (0,4 s, jedno na klik)
            var waveBrush = new LinearGradientBrush(violet, cyan, 45); waveBrush.Freeze();
            wave = b =>
            {
                if (!root.IsAncestorOf(b)) return;
                var c = b.TranslatePoint(new Point(b.ActualWidth / 2, b.ActualHeight / 2), trailC);
                var ring = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Stroke = waveBrush, StrokeThickness = 3, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(1, 1) };
                Canvas.SetLeft(ring, c.X - 5); Canvas.SetTop(ring, c.Y - 5); trailC.Children.Add(ring);
                var t = TimeSpan.FromMilliseconds(400);
                var grow = new System.Windows.Media.Animation.DoubleAnimation(1, 11, t) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(0.7, 0, t);
                fadeOut.Completed += (s, e) => trailC.Children.Remove(ring);
                ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                ring.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };
            // pad: ramka na wybranym klawiszu, odczyt pada 60 razy na sekunde tylko gdy 🎮 wlaczone i klawiatura otwarta
            var padSel = new Border { BorderThickness = new Thickness(2.5), CornerRadius = new CornerRadius(9), BorderBrush = new LinearGradientBrush(violet, cyan, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            trailC.Children.Add(padSel);
            int padR = 2, padC = 5; ushort padPrev = 0; DateTime padNext = DateTime.MinValue; int padDir = 0;
            Func<List<List<Button>>> padRows = () => root.Children.OfType<StackPanel>().Select(rw => rw.Children.OfType<Button>().Where(b => b.IsVisible).ToList()).Where(l => l.Count > 0).ToList();
            Action padShow = () =>
            {
                var rs = padRows(); if (rs.Count == 0) return;
                padR = Math.Max(0, Math.Min(rs.Count - 1, padR)); padC = Math.Max(0, Math.Min(rs[padR].Count - 1, padC));
                var b = rs[padR][padC]; var p0 = b.TranslatePoint(new Point(0, 0), trailC);
                Canvas.SetLeft(padSel, p0.X - 2); Canvas.SetTop(padSel, p0.Y - 2); padSel.Width = b.ActualWidth + 4; padSel.Height = b.ActualHeight + 4;
            };
            Action<int, int> padMove = (dr, dc) =>
            {
                var rs = padRows(); if (rs.Count == 0) return;
                if (dc != 0) padC = (padC + dc + rs[padR].Count) % rs[padR].Count;
                if (dr != 0)
                {   // w nowym rzedzie klawisz najblizej w poziomie
                    var cur = rs[padR][Math.Min(padC, rs[padR].Count - 1)]; double cx = cur.TranslatePoint(new Point(cur.ActualWidth / 2, 0), trailC).X;
                    padR = (padR + dr + rs.Count) % rs.Count;
                    padC = rs[padR].Select((b, i) => (i, d: Math.Abs(b.TranslatePoint(new Point(b.ActualWidth / 2, 0), trailC).X - cx))).OrderBy(x => x.d).First().i;
                }
                padShow();
            };
            Action<Button> press = b => b?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var padTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            padTimer.Tick += (s, e) =>
            {
                PadState st = default; bool ok = false;
                try { for (int i = 0; i < 4 && !ok; i++) ok = XInputGetState(i, out st) == 0; } catch (Exception) { padTimer.Stop(); return; }   // brak XInput w systemie
                padSel.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;   // ramka tylko przy podlaczonym padzie (bez pada nie stoi na "t")
                if (!ok) return;
                ushort bt = st.Buttons;
                // galka jak krzyzak
                if (st.LY > 16000) bt |= 0x1; if (st.LY < -16000) bt |= 0x2; if (st.LX < -16000) bt |= 0x4; if (st.LX > 16000) bt |= 0x8;
                if (st.LT > 120) bt |= 0x0800;   // LT jako przycisk (nieuzywany bit)
                ushort fresh = (ushort)(bt & ~padPrev); padPrev = bt;
                // kierunki z powtarzaniem przy przytrzymaniu
                int dir = (bt & 0x1) != 0 ? 1 : (bt & 0x2) != 0 ? 2 : (bt & 0x4) != 0 ? 3 : (bt & 0x8) != 0 ? 4 : 0;
                if (dir != 0 && (dir != padDir || DateTime.Now >= padNext))
                {
                    padNext = DateTime.Now.AddMilliseconds(dir != padDir ? 380 : 90); padDir = dir;
                    if (dir == 1) padMove(-1, 0); else if (dir == 2) padMove(1, 0); else if (dir == 3) padMove(0, -1); else padMove(0, 1);
                }
                if (dir == 0) padDir = 0;
                if ((fresh & 0x1000) != 0) { var rs = padRows(); if (padR < rs.Count && padC < rs[padR].Count) press(rs[padR][padC]); padShow(); }   // A
                if ((fresh & 0x2000) != 0) tap(0x08, '\0');   // B
                if ((fresh & 0x4000) != 0) tap(0, ' ');   // X
                if ((fresh & 0x8000) != 0) press(shiftB);   // Y
                if ((fresh & 0x0800) != 0) press(altB);   // LT
                if ((fresh & 0x0100) != 0) tap(0x25, '\0');   // LB
                if ((fresh & 0x0200) != 0) tap(0x27, '\0');   // RB
                if ((fresh & 0x0010) != 0) tap(0x0D, '\0');   // Start
                if ((fresh & 0x0020) != 0) _kbd?.Close();   // Back
            };
            Action padApply = () =>
            {
                bool on = GetPref("klawiatura-pad") == "1";
                padB.Background = on ? accent : Brushes.Transparent;
                if (!on) padSel.Visibility = Visibility.Collapsed;
                if (on) { padShow(); padTimer.Start(); } else padTimer.Stop();
            };
            padB.Click += (s, e) => { SetPref("klawiatura-pad", GetPref("klawiatura-pad") == "1" ? "0" : "1"); padApply(); };
            root.Loaded += (s, e) => padApply();
            var trace = new List<Point>(); bool down = false, swiping = false;
            root.PreviewMouseLeftButtonDown += (s, e) =>
            {
                down = true; swiping = false; trace.Clear(); trace.Add(e.GetPosition(trailC));
            };
            root.PreviewMouseMove += (s, e) =>
            {
                if (!down) return;
                if (e.LeftButton != MouseButtonState.Pressed) { down = false; return; }
                var p = e.GetPosition(trailC); var lp = trace[trace.Count - 1];
                if (Math.Abs(p.X - lp.X) + Math.Abs(p.Y - lp.Y) < 4) return;   // co kilka pikseli - mniej rysowania
                trace.Add(p);
                if (!swiping && (Math.Abs(p.X - trace[0].X) > 45 || Math.Abs(p.Y - trace[0].Y) > 45))   // drzaca reka przy kliknieciu to jeszcze nie przeciagniecie (prawie caly klawisz)
                {   // to juz przeciagniecie, nie klikniecie klawisza
                    swiping = true; root.CaptureMouse();
                    trail.BeginAnimation(UIElement.OpacityProperty, null); trail.Opacity = 0.85;
                    trail.Points = new PointCollection(trace);
                }
                else if (swiping) trail.Points.Add(p);
            };
            root.PreviewMouseLeftButtonUp += (s, e) =>
            {
                down = false;
                if (!swiping) return;
                swiping = false; e.Handled = true; root.ReleaseMouseCapture();
                trail.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(350)));
                if (!SwipeTyping.Loaded) return;
                // srodki klawiszy liter wzgledem sladu
                var centers = new Dictionary<char, Point>();
                foreach (var (b, c) in letters) if (c >= 'a' && c <= 'z') centers[c] = b.TranslatePoint(new Point(b.ActualWidth / 2, b.ActualHeight / 2), trailC);
                var pts = trace.ToList(); bool upper = shift ^ caps;
                Dictionary<string, int> learnedCopy; lock (_swLearned) learnedCopy = new Dictionary<string, int>(_swLearned);
                // prawdziwy odstep klawiszy w tym ukladzie (q -> w), nie stala - trafniej przy kazdym rozmiarze
                double kw = centers.TryGetValue('q', out var cq) && centers.TryGetValue('w', out var cw2) ? Math.Max(20, Math.Abs(cw2.X - cq.X)) : 52;
                lastKeyW = kw;
                System.Threading.Tasks.Task.Run(() => SwipeTyping.Recognize(pts, centers, kw, learnedCopy, 4, prevWord)).ContinueWith(t =>
                {
                    if (t.IsFaulted || t.Result.Count == 0) return;
                    var found = t.Result.Select(x => upper ? char.ToUpper(x[0]) + x.Substring(1) : x).ToList();
                    learn(cur); cur = "";
                    foreach (var ch in found[0]) SendKey(0, ch); SendKey(0, ' ');
                    prevBeforeSwipe = prevWord;
                    bool unsure = SwipeTyping.LastUncertain && found.Count > 1;
                    ask.Visibility = unsure ? Visibility.Visible : Visibility.Collapsed; title.Visibility = unsure ? Visibility.Collapsed : Visibility.Visible;
                    lastTrace = pts; if (!SecretField()) SwipeTyping.LearnShape(found[0], pts, lastKeyW);   // nauka Twojego ruchu (nie w polu hasla)
                    learn(found[0]); say(found[0]); lastSwipe = found[0];
                    if (shift) { shift = false; refresh(); }
                    for (int i = 0; i < sugs.Count; i++)
                    {
                        var w0 = i + 1 < found.Count ? found[i + 1] : null;
                        sugs[i].DataContext = w0; sugs[i].Content = w0 ?? ""; sugs[i].Visibility = w0 == null ? Visibility.Collapsed : Visibility.Visible;
                        sugs[i].Background = unsure ? accent : (sugs[i].Tag as Brush ?? keyBg);   // niepewnie - propozycje wyraznie podswietlone
                    }
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
            };
            var all = new DockPanel(); DockPanel.SetDock(head, Dock.Top); all.Children.Add(head); all.Children.Add(keys);
            var w = new Window
            {
                Title = L.T("Klawiatura Velivo"), WindowStyle = WindowStyle.None, Background = new SolidColorBrush(Color.FromRgb(0x19, 0x1B, 0x33)), ResizeMode = ResizeMode.CanResize,   // bez przezroczystosci okna - rysowanie na karcie graficznej, plynny slad
                SizeToContent = SizeToContent.WidthAndHeight, Topmost = true, ShowActivated = false, ShowInTaskbar = false,
                Content = new Border { Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromRgb(0x1E, 0x18, 0x38), 0), new GradientStop(Color.FromRgb(0x19, 0x1B, 0x33), 0.55), new GradientStop(Color.FromRgb(0x13, 0x2A, 0x35), 1) }, new Point(0, 0), new Point(1, 1)),
                    CornerRadius = new CornerRadius(14), BorderBrush = new LinearGradientBrush(Color.FromArgb(0x90, 0x8B, 0x5C, 0xF6), Color.FromArgb(0x90, 0x06, 0xB6, 0xD4), 45), BorderThickness = new Thickness(1), Child = new Viewbox { Child = all, Stretch = Stretch.Fill } }   // klawisze wypelniaja okno - szerokosc i wysokosc ustawiasz niezaleznie
            };
            head.MouseLeftButtonDown += (s, e) => { try { w.DragMove(); } catch (InvalidOperationException) { } };
            close.Click += (s, e) => Sail(w, all, false, w.Close); close.ToolTip = L.T("Zamknij klawiaturę");
            minB.Click += (s, e) => Sail(w, all, false, w.Hide);
            // brzegi i rogi do zmiany rozmiaru (okno bez ramki) - szerokosc i wysokosc dowolnie, mysza
            System.Windows.Shell.WindowChrome.SetWindowChrome(w, new System.Windows.Shell.WindowChrome { ResizeBorderThickness = new Thickness(7), CaptionHeight = 0, GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(14), UseAeroCaptionButtons = false });
            w.SourceInitialized += (s, e) =>
            {   // okno nie zabiera fokusu - pole na stronie / w Velivo zostaje aktywne
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle; _kbdHwnd = h;
                SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x08000000);
                int round = 2; DwmSetWindowAttribute(h, 33, ref round, 4);   // zaokraglone rogi (Windows 11)
                // WPF i tak aktywuje okno przy kliknieciu - na WM_MOUSEACTIVATE odpowiadamy "nie aktywuj"
                System.Windows.Interop.HwndSource.FromHwnd(h).AddHook((IntPtr hw, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (msg == 0x21) { handled = true; return (IntPtr)3; }   // MA_NOACTIVATE
                    return IntPtr.Zero;
                });
            };
            w.Loaded += (s, e) =>
            {   // naturalny rozmiar z zawartosci, potem staly i zmieniany brzegami (zapamietany)
                double nw = w.ActualWidth, nh = w.ActualHeight;
                w.SizeToContent = SizeToContent.Manual; w.MinWidth = nw * 0.25; w.MinHeight = nh * 0.25;
                var wa = SystemParameters.WorkArea;
                // mala na start (ok. 1/3 szerokosci ekranu), potem zapamietany rozmiar ustawiony mysza
                var sz = (GetPref("klawiatura-rozmiar") ?? "").Split(';');
                if (sz.Length == 2 && double.TryParse(sz[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sw) && double.TryParse(sz[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sh))
                { w.Width = Math.Max(w.MinWidth, Math.Min(wa.Width, sw)); w.Height = Math.Max(w.MinHeight, Math.Min(wa.Height, sh)); }
                else { w.Width = Math.Min(wa.Width * 0.9, 870); w.Height = w.Width * nh / nw; }   // na start ok. 870 punktow szerokosci
                // zawsze na srodku ekranu, tuz nad paskiem zadan
                double ww = double.IsNaN(w.Width) ? nw : w.Width, wh = double.IsNaN(w.Height) ? nh : w.Height;
                w.Left = wa.Left + (wa.Width - ww) / 2; w.Top = wa.Bottom - wh - 10;
                AvoidField(w);
                Sail(w, all, true, null);
            };
            // rozmiar zapisywany od razu po zmianie (takze gdy Velivo zamknie sie z otwarta klawiatura)
            w.SizeChanged += (s, e) => { if (w.IsLoaded && w.SizeToContent == SizeToContent.Manual) SetPref("klawiatura-rozmiar", w.ActualWidth.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";" + w.ActualHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)); };
            // pisanie na zwyklej klawiaturze: na ekranie fala na tym samym klawiszu (nic nie jest zapisywane; w polu hasla - nic)
            foreach (var (b, c) in letters) if (char.IsLetterOrDigit(c)) vkKeys[char.ToUpperInvariant(c)] = b;
            vkKeys[0x14] = capsB; vkKeys[0xA0] = vkKeys[0xA1] = vkKeys[0x10] = shiftB;
            DateTime secretAt = DateTime.MinValue; bool secret = false;
            _onPhysKey = vk =>
            {
                if ((DateTime.UtcNow - secretAt).TotalMilliseconds > 800) { secret = SecretField(); secretAt = DateTime.UtcNow; }   // sprawdzane najwyzej co 0,8 s
                if (!secret && vkKeys.TryGetValue(vk, out var kb)) wave?.Invoke(kb);
            };
            StartKeyEcho();
            w.Closed += (s, e) => { _kbd = null; _kbdHwnd = IntPtr.Zero; padTimer.Stop(); StopKeyEcho(); };
            _kbd = w; w.Show();
        }

        // pola tekstowe w oknach Velivo (wyszukiwanie, nazwy, hasla, edytowalne listy)
        internal static void KeyboardOnMiddleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle || !KeyboardOn) return;
            var d = e.OriginalSource as DependencyObject;
            while (d != null && !(d is TextBox) && !(d is PasswordBox) && !(d is ComboBox)) d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            if (d is ComboBox cb && !cb.IsEditable) return;
            if (d is TextBox tb && tb.IsReadOnly) return;
            if (d == null) return;
            ((UIElement)d).Focus(); e.Handled = true;
            ShowScreenKeyboard();
        }

        // strona: klik rolka w pole do pisania - fokus w polu i znacznik do okna (bez tresci); bez autoprzewijania
        const string KeyboardPageScript = "(function(){try{if(!window.chrome||!chrome.webview)return;var pm=chrome.webview.postMessage.bind(chrome.webview);document.addEventListener('mousedown',function(e){if(e.button!==1)return;var t=e.target;if(!t||!t.closest)return;var f=t.closest('input,textarea,[contenteditable=\"\"],[contenteditable=true]');if(!f||f.disabled||f.readOnly)return;if(f.tagName==='INPUT'&&/^(button|submit|reset|checkbox|radio|file|image|range|color|hidden)$/i.test(f.type))return;e.preventDefault();try{f.focus();}catch(x){}pm('velivo:klawiatura');},true);var sec=function(t){return !!(t&&t.tagName==='INPUT'&&(t.type==='password'||/password|one-time-code/i.test(t.autocomplete||'')));};document.addEventListener('focusin',function(e){pm('velivo:kbd-haslo:'+(sec(e.target)?'1':'0'));},true);}catch(x){}})();";
    }
}
