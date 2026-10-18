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
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern short GetKeyState(int vk);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        const uint KeyUp = 2, KeyUnicode = 4, KeyExt = 1;
        // strzalki, Home/End, PgUp/PgDn, Insert/Delete - klawisze rozszerzone (bez tego z Shift dzialaja jak klawiatura numeryczna: brak zaznaczenia)
        static uint ExtOf(ushort vk) { return vk >= 0x21 && vk <= 0x28 || vk == 0x2D || vk == 0x2E ? KeyExt : 0; }

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
                    // dawniej do Mojego slownika trafialy tez literowki (zolw, zzolw) - pisanie przeciagnieciem je podsuwalo: usuwamy je
                    var mine = new HashSet<string>((GetPref("klawiatura-bez-korekty") ?? "").Split('|').Where(x => x.Length > 0));   // dodane recznie / cofniete poprawki - zawsze zostaja
                    // bledna pisownia (napewno, niewiem) - usuwana zawsze, takze dodana recznie / cofnieta
                    var bad = mine.Where(SwipeTyping.IsMisspelling).ToList();
                    if (bad.Count > 0) { mine.ExceptWith(bad); SetPref("klawiatura-bez-korekty", string.Join("|", mine)); }
                    var typos = own.Where(w => !mine.Contains(w) && SwipeTyping.IsTypo(w)).ToList();
                    foreach (var w in typos) { own.Remove(w); learned.Remove(w); }
                    if (typos.Count > 0) SwipeTyping.SaveLearned(SwipeFile, learned, own);
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
            var ins = vks.Select(v => new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = v, Flags = ExtOf(v) } } })
                .Concat(vks.Reverse().Select(v => new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = v, Flags = KeyUp | ExtOf(v) } } })).ToArray();
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
            uint fl = vk == 0 ? KeyUnicode : ExtOf(vk);
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

        // ikonka klawiatury w zasobniku (obok Velivo): klik - pokaz / schowaj klawiature; prawy - menu
        static System.Windows.Forms.NotifyIcon _kbdTray;
        static void EnsureKbdTray()
        {
            if (_kbdTray != null) return;
            try
            {
                System.Drawing.Icon icon;
                using (var bmp = new System.Drawing.Bitmap(32, 32))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Rectangle(0, 0, 32, 32), System.Drawing.Color.FromArgb(0x8B, 0x5C, 0xF6), System.Drawing.Color.FromArgb(0x06, 0xB6, 0xD4), 45f))
                        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                        { path.AddArc(0, 0, 12, 12, 180, 90); path.AddArc(20, 0, 12, 12, 270, 90); path.AddArc(20, 20, 12, 12, 0, 90); path.AddArc(0, 20, 12, 12, 90, 90); path.CloseFigure(); g.FillPath(bg, path); }
                        using (var f = new System.Drawing.Font("Segoe MDL2 Assets", 15f, System.Drawing.GraphicsUnit.Pixel))
                        using (var sf = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center })
                            g.DrawString("\uE765", f, System.Drawing.Brushes.White, new System.Drawing.RectangleF(0, 1, 32, 32), sf);
                    }
                    icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
                }
                _kbdTray = new System.Windows.Forms.NotifyIcon { Icon = icon, Text = L.T("Klawiatura Velivo – kliknij, aby pokazać / schować"), Visible = true };
                Action toggle = () => { if (_kbd != null && _kbd.IsVisible) _kbd.Hide(); else ShowScreenKeyboard(); };
                _kbdTray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Application.Current?.Dispatcher.BeginInvoke(toggle); };
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add(L.T("⌨ Pokaż / schowaj klawiaturę"), null, (s, e) => Application.Current?.Dispatcher.BeginInvoke(toggle));
                var autoIt = new System.Windows.Forms.ToolStripMenuItem(L.T("Otwieraj samą po kliknięciu w pole tekstowe")) { Checked = _kbdAuto, ToolTipText = L.T("Jak klawiatura dotykowa Windows: klik w pole do pisania w dowolnym programie pokazuje klawiaturę Velivo") };
                autoIt.Click += (s, e) => { _kbdAuto = !_kbdAuto; autoIt.Checked = _kbdAuto; Application.Current?.Dispatcher.BeginInvoke(new Action(() => SetPref("klawiatura-sama", _kbdAuto ? "1" : "0"))); };
                menu.Items.Add(autoIt);
                menu.Items.Add(L.T("✕ Zamknij klawiaturę"), null, (s, e) => Application.Current?.Dispatcher.BeginInvoke(new Action(() => _kbd?.Close())));
                _kbdTray.ContextMenuStrip = menu;
                Application.Current.Exit += (s, e) => { try { _kbdTray.Visible = false; _kbdTray.Dispose(); } catch (Exception) { } };
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        static volatile bool _kbdAuto;   // klawiatura sama po kliknieciu w pole tekstowe (domyslnie wylaczone)
        internal static void StartGlobalKeyboardHook()
        {
            _kbdAuto = GetPref("klawiatura-sama") == "1";
            EnsureKbdTray();
            if (_hookStarted) return;
            _hookStarted = true;
            _mouseProc = (code, wp, lp) =>
            {
                if (code >= 0 && ((int)wp == 0x0201 || (int)wp == 0x0204)) PhysReset();   // klik - kursor tekstu mogl sie przeniesc: nowe slowo dla autokorekty
                if (!KeyboardOn) { }   // wylaczona w ustawieniach - nic nie robimy, tylko przekazujemy dalej
                else if (code >= 0 && (int)wp == 0x0207)   // WM_MBUTTONDOWN
                {
                    var pt = Marshal.PtrToStructure<PointStruct>(lp);
                    _holdAt = pt;
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() => { _holdTimer.Stop(); _holdTimer.Start(); CheckForeignField(pt); }));
                }
                else if (code >= 0 && (int)wp == 0x0202 && _kbdAuto)   // WM_LBUTTONUP: klik w pole tekstowe innego programu - klawiatura sama
                {
                    var pt = Marshal.PtrToStructure<PointStruct>(lp);
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() => { if (_kbd == null || !_kbd.IsVisible) CheckForeignField(pt, true); }));
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

        static void CheckForeignField(PointStruct pt, bool auto = false)
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
                    if (el == null && !auto)
                    {   // do zgloszenia: w jakim programie i na czym kliknieto, gdy pola nie rozpoznano
                        try
                        {
                            var u = System.Windows.Automation.AutomationElement.FromPoint(new Point(pt.X, pt.Y));
                            File.AppendAllText(Path.Combine(DataDir, "klawiatura.log"), DateTime.Now.ToString("s") + "  " + System.Diagnostics.Process.GetProcessById((int)pid).ProcessName + "  " + u.Current.ControlType.ProgrammaticName + "  " + u.Current.ClassName + "  " + u.Current.FrameworkId + "\n");
                        }
                        catch (Exception) { }
                        return;
                    }
                    if (el == null) return;   // klik poza polem do pisania - bez klawiatury
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
                    if ((flags & 0x10) == 0)   // bez znakow wyslanych przez sama klawiature Velivo
                    {
                        var f = _onPhysKey; if (f != null) Application.Current?.Dispatcher.BeginInvoke(new Action(() => f(vk)));
                        bool swallow = false;
                        try { swallow = PhysKey(vk); } catch (Exception) { _pWord = ""; }
                        if (swallow) return (IntPtr)1;   // klawisz zastapiony poprawionym tekstem
                    }
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
        // autokorekta przy pisaniu ZWYKLA klawiatura (gdy klawiatura Velivo otwarta) - w podsluchu, zanim klawisz trafi do programu:
        // spacja / Enter / znak po slowie spoza slownika -> slowo poprawione + ten znak; wielka litera po ". "; ⌫ zaraz potem cofa.
        // Klik myszy, inny klawisz albo przerwa > 4 s = nowe slowo (nie wiemy, gdzie jest kursor). Pole hasla - nic.
        static volatile Func<string, string> _physCorrect;      // null = wylaczona (klawiatura zamknieta / korekta wylaczona)
        static volatile bool _physSecret = true;
        static volatile bool _physPause;   // okienko "Dodaj słowo" - wpisujesz nowe slowo, bez poprawiania                // pole hasla (sprawdzane przez klawiature co 0,3 s); na start - bezpiecznie
        static Action<string, string> _physNotify, _physUndone; static Action<string> _physAsk; static Action<string, bool> _physPred;
        // delikatny klik klawisza: krotki (25 ms), cichy dzwiek wygenerowany w pamieci - bez plikow, bez opoznienia
        static System.Media.SoundPlayer _click;
        static void KeyClick()
        {
            if (GetPref("klawiatura-klik") == "0") return;
            try
            {
                if (_click == null)
                {
                    int rate = 22050, n = rate * 25 / 1000; var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
                    bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + n * 2); bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
                    bw.Write(System.Text.Encoding.ASCII.GetBytes("data")); bw.Write(n * 2);
                    for (int i = 0; i < n; i++) { double t = (double)i / rate, env = Math.Exp(-t * 220); bw.Write((short)(Math.Sin(2 * Math.PI * 1500 * t) * env * 2600)); }
                    ms.Position = 0; _click = new System.Media.SoundPlayer(ms); _click.Load();
                }
                _click.Play();
            }
            catch (Exception) { }
        }
        static long _tapLast;   // ostatnie klikniecie klawisza ekranowego
        static string _pWord = "", _pFixFrom, _pFixTo, _pPrevWord; static bool _pPrevSpace; static long _pLast; static char _pPrev; static bool _pCap;
        static void Inject(IEnumerable<KbdInput> ins) { var a = ins.ToArray(); if (a.Length > 0) SendInput((uint)a.Length, a, Marshal.SizeOf(typeof(KbdInput))); }
        static IEnumerable<KbdInput> KeyIn(ushort vk, char ch)
        {
            uint fl = vk == 0 ? KeyUnicode : ExtOf(vk); ushort sc = vk == 0 ? ch : (ushort)0;
            yield return new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = vk, Scan = sc, Flags = fl } } };
            yield return new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = vk, Scan = sc, Flags = fl | KeyUp } } };
        }
        static IEnumerable<KbdInput> TextIn(string t) { return t.SelectMany(c => c == '\n' ? KeyIn(0x0D, '\0') : KeyIn(0, c)); }
        static void PhysReset() { _pWord = ""; _pFixFrom = _pFixTo = null; _pCap = false; _pPrev = '\0'; _pPrevWord = null; _pPrevSpace = false; }
        static void PhysPred(string word, bool done) { var p = _physPred; if (p != null) Application.Current?.Dispatcher.BeginInvoke(new Action(() => p(word, done))); }   // pasek propozycji (watek okna)
        static bool PhysKey(int vk)
        {
            var corr = _physCorrect; _tapLast = Environment.TickCount64;   // pisanie trwa - sprawdzanie pola hasla aktywne
            if (corr == null || _physSecret || _physPause) { PhysReset(); return false; }
            if (vk == 0x10 || vk == 0x11 || vk == 0x12 || vk >= 0xA0 && vk <= 0xA5 || vk == 0x14) return false;   // same modyfikatory - bez zmian
            long now = Environment.TickCount64; if (now - _pLast > 60000) PhysReset(); _pLast = now;
            bool sh = GetAsyncKeyState(0x10) < 0, ctrl = GetAsyncKeyState(0x11) < 0, alt = GetAsyncKeyState(0x12) < 0, caps = (GetKeyState(0x14) & 1) != 0;
            if (vk == 0x08 && ctrl && !alt)
            {   // Ctrl+Backspace (lewy albo prawy): usun slowo przed kursorem - takze w programach, ktore tego nie umieja
                var del = new List<KbdInput>();
                del.Add(new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = 0x10 } } });
                del.AddRange(KeyIn(0x25, '\0'));   // Ctrl (trzymany) + Shift + ← = zaznacz slowo
                del.Add(new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = 0x10, Flags = KeyUp } } });
                bool lc = GetAsyncKeyState(0xA2) < 0, rc = GetAsyncKeyState(0xA3) < 0;
                if (lc) del.Add(new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = 0xA2, Flags = KeyUp } } });
                if (rc) del.Add(new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = 0xA3, Flags = KeyUp | KeyExt } } });
                del.AddRange(KeyIn(0x2E, '\0'));   // Delete bez Ctrl - usun zaznaczenie
                if (lc) del.Add(new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = 0xA2 } } });
                if (rc) del.Add(new KbdInput { Type = 1, U = new InputUnion { Kb = new KeybdData { Vk = 0xA3, Flags = KeyExt } } });   // Ctrl dalej trzymany - jak przed
                Inject(del); PhysReset(); PhysPred(null, false);
                return true;
            }
            if (vk == 0x08)
            {
                if (_pFixTo != null && !ctrl)
                {   // cofniecie poprawki: znak i poprawione slowo -> to, co wpisano
                    string from = _pFixFrom, to = _pFixTo; _pFixFrom = _pFixTo = null;
                    Inject(Enumerable.Range(0, to.Length + 1).SelectMany(_ => KeyIn(0x08, '\0')).Concat(TextIn(from)));
                    _pWord = from; var u = _physUndone; if (u != null) Application.Current?.Dispatcher.BeginInvoke(new Action(() => u(from, to)));
                    return true;
                }
                if (_pWord.Length > 0) _pWord = _pWord.Substring(0, _pWord.Length - 1);
                _pFixFrom = _pFixTo = null; PhysPred(_pWord, false); return false;
            }
            _pFixFrom = _pFixTo = null;
            if (vk >= 0x41 && vk <= 0x5A && (!ctrl && !alt || ctrl && alt))
            {   // litera (AltGr = Ctrl+Alt: polskie znaki)
                char c = (char)(vk + 32);
                if (ctrl && alt) { var i = "acelnoszx".IndexOf(c); if (i < 0) { PhysReset(); return false; } c = "ąćęłńóśżź"[i]; }
                bool upper = sh ^ caps;
                if (_pCap && !upper) { _pCap = false; c = char.ToUpper(c); _pWord += c; _pPrev = c; Inject(KeyIn(0, c)); PhysPred(_pWord, false); return true; }   // poczatek zdania - wielka litera zamiast malej
                _pCap = false; _pWord += upper ? char.ToUpper(c) : c; _pPrev = c; PhysPred(_pWord, false); return false;
            }
            char sep = vk == 0x20 ? ' ' : vk == 0x0D ? '\n' : vk == 0xBE && !sh ? '.' : vk == 0xBC && !sh ? ',' : vk == 0x31 && sh ? '!' : vk == 0xBF && sh ? '?' : vk == 0xBA ? (sh ? ':' : ';') : '\0';
            if (sep == '\0' || ctrl || alt) { PhysReset(); PhysPred(null, false); return false; }   // inny klawisz (strzalki, skroty) - nie wiemy, gdzie jest kursor
            bool capAfter = sep == ' ' && (_pPrev == '.' || _pPrev == '!' || _pPrev == '?') ;
            string word = _pWord; _pWord = ""; _pPrev = sep; _pCap = capAfter;
            string prevW = _pPrevWord; bool prevSpace = _pPrevSpace; _pPrevWord = sep == ' ' ? word : null; _pPrevSpace = sep == ' ';
            if (word.Length < 2 && !(sep == ' ' && SwipeTyping.NeedsComma(word, prevW))) { PhysPred(null, false); return false; }
            var fix = word.Length >= 2 ? corr(word) : null;
            var fin = fix ?? word; if (sep == ' ') _pPrevWord = fin;
            bool comma = sep == ' ' && prevSpace && SwipeTyping.NeedsComma(fin, prevW);   // wiem że -> wiem, że
            PhysPred(sep == ' ' ? fin : null, true);   // po spacji - inne formy skonczonego slowa
            if (fix == null && !comma) { var a = _physAsk; if (a != null && sep == ' ') Application.Current?.Dispatcher.BeginInvoke(new Action(() => a(word))); return false; }
            Inject(Enumerable.Range(0, word.Length + (comma ? 1 : 0)).SelectMany(_ => KeyIn(0x08, '\0')).Concat(TextIn((comma ? ", " : "") + fin + sep)));   // ten znak zamiast przepuszczonego
            if (fix != null)
            {
                _pFixFrom = word; _pFixTo = fix;
                var n = _physNotify; if (n != null) Application.Current?.Dispatcher.BeginInvoke(new Action(() => n(word, fix)));
            }
            return true;
        }

        static void StopKeyEcho()
        {
            _onPhysKey = null; _physCorrect = null; _physNotify = _physUndone = null; _physAsk = null; _physPred = null;
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
            if (_kbd != null) { if (!_kbd.IsVisible) { var wa = SystemParameters.WorkArea; _kbd.Left = wa.Right - _kbd.ActualWidth - 10; _kbd.Top = wa.Bottom - _kbd.ActualHeight - 10; _kbd.Show(); if (_kbd.Content is Border bd && bd.Child is Viewbox vb && vb.Child is FrameworkElement fe) Sail(_kbd, fe, true, null); } _kbd.Topmost = false; _kbd.Topmost = true; _kbd.Topmost = GetPref("klawiatura-wierzch") == "1"; AvoidField(_kbd); return; }   // schowana - wraca
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
            Action<string> askAdd = aw0 => { }; Action hideAdd = () => { };   // pytanie "Dodać do słownika?" (ustawiane nizej)
            string prevWord = null, prevWord2 = null, prevBeforeSwipe = null;   // poprzednie slowo - do nauki kolejnosci slow
            Action<string> learn = null;
            learn = wd =>
            {
                if (wd != null && wd.Contains(' ')) { foreach (var part in wd.Split(' ')) learn(part); return; }   // poprawka na dwa slowa (nie wiem)
                if (wd.Length < 2 || !SwipeTyping.Loaded || SecretField()) return;   // pole hasla - bez nauki
                var lw = wd.ToLowerInvariant();
                SwipeTyping.LearnPair(prevWord, lw); SwipeTyping.LearnTriple(prevWord2, prevWord, lw); prevWord2 = prevWord; prevWord = lw;   // kolejnosc slow i zwroty (2 poprzednie)
                lock (_swLearned)
                {
                    _swLearned[lw] = (_swLearned.TryGetValue(lw, out var n) ? n : 0) + 1;
                    if (!SwipeTyping.IsKnown(lw) && !SwipeTyping.IsTypo(lw)) askAdd(wd);   // nowe slowo - najpierw pytamy (literowki znanego slowa wcale)
                    SwipeTyping.SaveLearned(SwipeFile, _swLearned, _swOwn);
                }
            };
            // autokorekta (przycisk w naglowku, domyslnie wlaczona): po spacji / Enterze / znaku slowo spoza slownika -> poprawione,
            // ⌫ zaraz potem przywraca to, co wpisano; nigdy w polu hasla
            string fixFrom = null, fixTo = null;
            var noFix = new HashSet<string>((GetPref("klawiatura-bez-korekty") ?? "").Split('|').Where(x => x.Length > 0 && !SwipeTyping.IsMisspelling(x)));   // bez blednej pisowni (napewno) - ta jest zawsze poprawiana
            // slowo swiadomie zostawione (cofnieta poprawka, Dodaj slowo): bez poprawiania, do Mojego slownika (propozycje, przeciaganie), nigdy usuwane jako literowka
            Action<string> keepWord = kw =>
            {
                var lw0 = (kw ?? "").Trim().ToLowerInvariant(); if (lw0.Length < 2 || !lw0.All(char.IsLetter) || SwipeTyping.IsMisspelling(lw0)) return;   // bledna pisownia (napewno) - cofniecie dziala tylko ten raz, nie zapamietujemy
                bool added0; lock (noFix) added0 = noFix.Add(lw0);
                if (added0) SetPref("klawiatura-bez-korekty", string.Join("|", noFix.Skip(Math.Max(0, noFix.Count - 500))));
                lock (_swLearned) { _swOwn.Add(lw0); SwipeTyping.AddUserWord(lw0); _swLearned[lw0] = (_swLearned.TryGetValue(lw0, out var c0) ? c0 : 0) + 1; SwipeTyping.SaveLearned(SwipeFile, _swLearned, _swOwn); }
            };   // cofniete poprawki - tych slow nie poprawiamy
            char lastSent = '\0'; bool capNext = false; string tPrevWord = null; bool tPrevSpace = false;   // poprzednie slowo - do przecinka   // wielka litera po ". " / "! " / "? "
            Action<string, List<string>> askFix = null;
            Action<string, bool> predShow = (wd, dn) => { };   // pasek propozycji (ustawiany nizej)   // watpliwosc: "Poprawić na:" z propozycjami (klik podmienia slowo)
            Action<ushort, char> tap = null;
            tap = (vk, ch) =>
            {
                _tapLast = Environment.TickCount64; KeyClick(); if (vk == 0 && char.IsLetter(ch)) hideAdd();
                if (vk == 0x08 && fixTo != null)
                {   // cofniecie poprawki: znak oddzielajacy i poprawione slowo -> wpisane slowo (zapamietane - wiecej go nie poprawiamy)
                    for (int k = 0; k <= fixTo.Length; k++) SendKey(0x08, '\0');
                    foreach (var c2 in fixFrom) SendKey(0, c2);
                    keepWord(fixFrom);
                    cur = fixFrom; fixFrom = fixTo = null; ask.Visibility = Visibility.Collapsed; title.Visibility = Visibility.Visible;
                    return;
                }
                bool autoOn = GetPref("klawiatura-korekta") != "0" && !_physPause;
                if (vk == 0 && char.IsLetter(ch) && capNext && autoOn && !SecretField()) ch = char.ToUpper(ch);
                if (vk != 0 || ch != ' ') capNext = false;
                bool sep = vk == 0 ? !char.IsLetter(ch) : vk == 0x0D || vk == 0x09;
                string fixedNow = null, origNow = cur;
                if (sep && cur.Length >= 2 && autoOn && !SecretField())
                {
                    string fix; lock (noFix) fix = SwipeTyping.Complete(cur, noFix);
                    if (fix != null) { for (int k = 0; k < cur.Length; k++) SendKey(0x08, '\0'); foreach (var c2 in fix) SendKey(0, c2); cur = fix; fixedNow = fix; }
                }
                fixFrom = fixTo = null;
                if (sep)
                {   // przecinek przed spojnikiem / ktory (wiem że -> wiem, że)
                    var fin = fixedNow ?? origNow;
                    if (vk == 0 && ch == ' ' && autoOn && tPrevSpace && fin.Length > 0 && SwipeTyping.NeedsComma(fin, tPrevWord) && !SecretField())
                    { for (int k = 0; k <= fin.Length; k++) SendKey(0x08, '\0'); foreach (var c2 in ", " + fin) SendKey(0, c2); }
                    tPrevWord = vk == 0 && ch == ' ' && fin.Length > 0 ? fin : null; tPrevSpace = vk == 0 && ch == ' ' && fin.Length > 0;
                }
                SendKey(vk, ch); lastSwipe = null; foreach (var x in sugs) x.Visibility = Visibility.Collapsed; title.Visibility = Visibility.Visible; ask.Visibility = Visibility.Collapsed;
                if (vk == 0 && ch == ' ' && (lastSent == '.' || lastSent == '!' || lastSent == '?')) capNext = true;
                lastSent = vk == 0 ? ch : '\0';
                if (fixedNow != null)
                {
                    fixFrom = origNow; fixTo = fixedNow;
                    ask.Text = L.T("Poprawiono: ") + origNow + " → " + fixedNow + L.T("  (⌫ cofa)"); ask.Visibility = Visibility.Visible; title.Visibility = Visibility.Collapsed;
                }
                else if (vk == 0 && ch == ' ' && autoOn && origNow.Length >= 3 && !SecretField())
                {   // pewnej poprawki brak, ale slowa nie ma w slowniku - pytamy
                    List<string> sg; lock (noFix) sg = SwipeTyping.Suggest(origNow, noFix);
                    if (sg.Count > 0) askFix(origNow, sg);
                }
                if (vk == 0 && char.IsLetter(ch)) cur += ch;
                else if (vk == 0x08) { if (cur.Length > 0) cur = cur.Substring(0, cur.Length - 1); }
                else { learn(cur); cur = ""; }
                if (vk == 0 && char.IsLetter(ch) || vk == 0x08) predShow(cur, false);
                else predShow(vk == 0 && ch == ' ' ? (fixedNow ?? origNow) : null, true);   // po spacji - inne formy skonczonego slowa
            };
            // propozycje po przeciagnieciu - w naglowku zamiast napisu (bez osobnego, zwykle pustego rzedu); klik podmienia ostatnie slowo
            var sugRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sugRow.Children.Add(title);
            sugRow.Children.Add(ask);
            // ➕ nowe slowo spoza slownika: jedno klikniecie dopisuje do Mojego slownika (bez klikniecia - nic nie zapisujemy)
            var addB = key("", double.NaN); addB.Height = 26; addB.MinWidth = 70; addB.Padding = new Thickness(12, 0, 12, 0); addB.FontSize = 14; addB.Visibility = Visibility.Collapsed;
            addB.ToolTip = L.T("Dodaj to słowo do Mojego słownika – autokorekta go nie zmieni, a propozycje je podsuną");
            sugRow.Children.Add(addB);
            string addWord = null;
            addB.Click += (s, e) => { if (addWord != null) keepWord(addWord); addWord = null; addB.Visibility = Visibility.Collapsed; ask.Visibility = Visibility.Collapsed; title.Visibility = Visibility.Visible; };
            hideAdd = () => { addB.Visibility = Visibility.Collapsed; addWord = null; };
            askAdd = aw0 =>
            {
                if (aw0 == null || aw0.Length < 2 || _physSecret) return;
                lock (noFix) if (noFix.Contains(aw0.ToLowerInvariant())) return;
                addWord = aw0; addB.Content = "➕ " + aw0; addB.Visibility = Visibility.Visible;
                ask.Text = L.T("Nowe słowo – dodać do słownika?"); ask.Visibility = Visibility.Visible; title.Visibility = Visibility.Collapsed;
            };
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
            askFix = (word, list) =>
            {
                lastSwipe = word; lastTrace = null;   // klik w propozycje podmienia to slowo (+ spacja); bez nauki ruchu
                for (int i = 0; i < sugs.Count; i++) { bool on = i < list.Count; sugs[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed; if (on) { sugs[i].Content = list[i]; sugs[i].DataContext = list[i]; } }
                ask.Text = L.T("Poprawić na:"); ask.Visibility = Visibility.Visible; title.Visibility = Visibility.Collapsed;
            };
            // rzad skrotow: notatka, karteczki, poczta, glosnosc Windows, Velivo, kalkulator, wycinanie, szukanie, wylaczenie komputera
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
            Func<MainWindow> mainW = () => Application.Current?.MainWindow as MainWindow;
            Action showMain = () =>
            {
                var mw = mainW(); if (mw == null) return;
                if (!mw.IsVisible) mw.ShowFromTray(); else { if (mw.WindowState == WindowState.Minimized) mw.WindowState = WindowState.Normal; mw.Activate(); }
            };
            // 📝 Notes: nowy dokument tekstowy w wybranym folderze (domyslnie Dokumenty\Velivo Notatki), otwarty w programie z Windows; prawy - folder
            Func<string> notesDir = () => { var d = GetPref("klawiatura-notatki-folder"); return string.IsNullOrWhiteSpace(d) ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Velivo Notatki") : d; };
            var notesB = fn(ico("\uE8A5", "Notes"), 96, L.T("Nowa notatka: tworzy i otwiera dokument tekstowy w Twoim folderze notatek (prawy przycisk – zmień folder)"), () =>
            {
                try
                {
                    var dir = notesDir(); System.IO.Directory.CreateDirectory(dir);
                    var f = System.IO.Path.Combine(dir, L.T("Notatka") + " " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".txt");
                    System.IO.File.WriteAllText(f, "");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(f) { UseShellExecute = true });
                }
                catch (Exception ex) { App.LogError(ex); MessageBox.Show(L.T("Nie udało się utworzyć notatki:\n") + ex.Message, "Velivo"); }
            });
            notesB.ContextMenu = new ContextMenu();
            notesB.ContextMenuOpening += (s, e) =>
            {
                var cm = notesB.ContextMenu; cm.Items.Clear();
                cm.Items.Add(new MenuItem { Header = L.T("Folder notatek: ") + notesDir(), IsEnabled = false });
                var open = new MenuItem { Header = L.T("📂 Otwórz folder notatek") }; open.Click += (a, b) => { try { var d = notesDir(); System.IO.Directory.CreateDirectory(d); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(d) { UseShellExecute = true }); } catch (Exception ex) { App.LogError(ex); } };
                var change = new MenuItem { Header = L.T("📁 Zmień folder notatek…") }; change.Click += (a, b) => { var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Folder notatek"), InitialDirectory = notesDir() }; if (dlg.ShowDialog() == true) SetPref("klawiatura-notatki-folder", dlg.FolderName); };
                cm.Items.Add(open); cm.Items.Add(change);
            };
            // 🗒 karteczki na pulpit Velivo - te same rodzaje co w menu strony
            var stickyB = fn(ico("\uE70B", null), 50, L.T("Karteczki na pulpit (nowa karteczka albo wszystkie)"), () => { });
            stickyB.Click += (s, e) =>
            {
                var mw = mainW(); if (mw == null) return;
                var cm = new ContextMenu { PlacementTarget = stickyB, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                foreach (var kk in NoteKinds) { var kind = kk[0]; var it = new MenuItem { Header = kk[1] + "  " + L.T(kk[2]) }; it.Click += (a, b) => mw.NewNote(kind); cm.Items.Add(it); }
                cm.Items.Add(new Separator());
                var all = new MenuItem { Header = L.T("Pokaż wszystkie karteczki") }; all.Click += (a, b) => mw.ShowAllNotes(); cm.Items.Add(all);
                cm.IsOpen = true;
            };
            fn(ico("\uE715", "E-mail"), 110, L.T("Otwiera domyślny program pocztowy – nowa wiadomość"), () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mailto:") { UseShellExecute = true }); } catch (Exception ex) { App.LogError(ex); } });
            fn(ico("\uE74F", null), 50, L.T("Wycisz / włącz dźwięk komputera"), () => SendCombo(0xAD));
            fn(ico("\uE993", null), 50, L.T("Ciszej (głośność Windows)"), () => SendCombo(0xAE));
            fn(ico("\uE995", null), 50, L.T("Głośniej (głośność Windows)"), () => SendCombo(0xAF));
            fn(ico("\uE774", "Velivo"), 110, L.T("Pokazuje okno przeglądarki Velivo"), showMain);
            Action<string> run = cmd => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cmd) { UseShellExecute = true }); } catch (Exception ex) { App.LogError(ex); } };
            fn(ico("\uE8EF", null), 50, L.T("Kalkulator Windows"), () => run("calc.exe"));
            fn(ico("\uE8C6", null), 50, L.T("Wycinanie – zaznacz fragment ekranu (narzędzie Windows)"), () => run("ms-screenclip:"));
            // 🔎 szybkie szukanie: wpisz (tez ta klawiatura) i Enter - Velivo otwiera wyniki w nowej karcie
            fn(ico("\uE721", null), 50, L.T("Szukaj w internecie przez Velivo – wpisz i naciśnij Enter"), () =>
            {
                var mw = mainW(); if (mw == null) return;
                var box = new TextBox { FontSize = 18, Padding = new Thickness(8, 6, 8, 6), MinWidth = 420, BorderThickness = new Thickness(0) };
                var sw = new Window { Title = L.T("Szukaj w internecie"), Content = box, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false };
                if (_kbd != null) { sw.Left = _kbd.Left + 20; sw.Top = Math.Max(SystemParameters.WorkArea.Top, _kbd.Top - 80); } else sw.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                box.KeyDown += (a, b) =>
                {
                    if (b.Key == Key.Escape) sw.Close();
                    if (b.Key != Key.Enter || box.Text.Trim().Length == 0) return;
                    var q = box.Text.Trim(); sw.Close();
                    showMain(); mw.AddTab(mw._settings.SearchUrl(q));
                };
                sw.Loaded += (a, b) => { sw.Activate(); box.Focus(); };
                sw.Show();
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
            // pasek propozycji nad klawiszami (jak w telefonie): inne formy slowa (droga -> drogą) i dokonczenia; klik wstawia slowo
            var predRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 38 };
            var preds = new List<Button>();
            string predWord = null; bool predDone = false, predNext = false;
            for (int i = 0; i < 3; i++)
            {
                var pb = key("", double.NaN); pb.MinWidth = 150; pb.Padding = new Thickness(14, 0, 14, 0); pb.Height = 32; pb.FontSize = 15; pb.Visibility = Visibility.Hidden;   // dluzszy zwrot - szerszy przycisk
                pb.ToolTip = L.T("Propozycja słowa – kliknij, aby wstawić");
                pb.Click += (s, e) =>
                {
                    var choice = ((Button)s).DataContext as string; if (choice == null || predWord == null) return;
                    int back = predNext ? 0 : predWord.Length + (predDone ? 1 : 0);   // nastepne slowo - tylko dopisujemy
                    for (int k = 0; k < back; k++) SendKey(0x08, '\0');
                    foreach (var c2 in choice) SendKey(0, c2); SendKey(0, ' ');
                    foreach (var pw in choice.Split(' ')) learn(pw); cur = "";   // caly zwrot - kazde slowo lastSwipe = null; fixFrom = fixTo = null; lastSent = ' ';
                    predShow(null, false);
                };
                preds.Add(pb); predRow.Children.Add(pb);
            }
            predShow = (word, done) =>
            {
                var list = word == null || word.Length == 0 || _physSecret || GetPref("klawiatura-korekta") == "0" ? new List<string>() : SwipeTyping.Predict(word, done);
                predNext = false;
                if (done && word != null && GetPref("klawiatura-korekta") != "0" && !_physSecret)
                {   // nie dobry -> niedobry: podmiana obu slow ("nie " + slowo)
                    var jn = SwipeTyping.JoinNie(prevWord2, word) ?? SwipeTyping.JoinPair(prevWord2, word);
                    if (jn != null) { list.Insert(0, jn); word = prevWord2 + " " + word; }
                }
                if (done && list.Count == 0 && word != null && !_physSecret && GetPref("klawiatura-korekta") != "0") { list = SwipeTyping.NextWords(word, prevWord2); predNext = list.Count > 0; }   // po spacji: co zwykle piszesz dalej
                predWord = list.Count > 0 ? word : null; predDone = done;
                for (int i = 0; i < preds.Count; i++) { bool on = i < list.Count; preds[i].Visibility = on ? Visibility.Visible : Visibility.Hidden; if (on) { preds[i].Content = list[i]; preds[i].DataContext = list[i]; } }
            };
            root.Children.Add(predRow);
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
            var space = key("", 284); vkKeys[0x20] = space; space.ToolTip = L.T("Spacja. Pisanie przeciągnięciem: wciśnij na pierwszej literze słowa i przeciągnij po kolejnych"); space.Click += (s, e) => tap(0, ' ');
            var dot = key(".com", 66); dot.ToolTip = L.T("Wpisuje „.com”"); dot.FontSize = 15; dot.Click += (s, e) => { foreach (var ch in ".com") tap(0, ch); };
            var lf = key("◀", 46); vkKeys[0x25] = lf; lf.ToolTip = L.T("Kursor w lewo"); lf.Click += (s, e) => tap(0x25, '\0');
            var dn = key("▼", 46); vkKeys[0x28] = dn; dn.ToolTip = L.T("Kursor w dół"); dn.Click += (s, e) => tap(0x28, '\0');
            var rt = key("▶", 46); vkKeys[0x27] = rt; rt.ToolTip = L.T("Kursor w prawo"); rt.Click += (s, e) => tap(0x27, '\0');
            // ⌫ slowo: kazde klikniecie usuwa jedno slowo przed kursorem (Ctrl+Shift+← zaznacza slowo, Delete je usuwa - dziala tez w starszych programach)
            var wordDel = key(ico("\uE750", L.T("słowo")), 96); wordDel.FontSize = 14;
            wordDel.ToolTip = L.T("Usuń ostatnie słowo – każde kliknięcie usuwa kolejne słowo przed kursorem");
            wordDel.Click += (s, e) =>
            {
                SendCombo(0x11, 0x10, 0x25); SendCombo(0x2E);
                cur = ""; lastSwipe = null; foreach (var x in sugs) x.Visibility = Visibility.Collapsed; title.Visibility = Visibility.Visible; ask.Visibility = Visibility.Collapsed;
            };
            foreach (var b in new[] { altB, at, space, wordDel, dot, lf, dn, rt }) last.Children.Add(b);
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
            // 📌 zawsze na wierzchu (wl./wyl., zapamietane) i przezroczystosc − / + co 5% (10-100%)
            var pinB = key(ico("\uE718", null), 34); pinB.Height = 28; pinB.FontSize = 13; DockPanel.SetDock(pinB, Dock.Right);
            Action pinShow = () => { bool on = GetPref("klawiatura-wierzch") == "1"; pinB.Background = on ? accent : Brushes.Transparent; pinB.ToolTip = on ? L.T("Zawsze na wierzchu: włączone (kliknij, aby wyłączyć)") : L.T("Zawsze na wierzchu: wyłączone (kliknij, aby włączyć)"); };
            pinShow();
            var alphaPlus = key(ico("\uE710", null), 30); alphaPlus.Height = 28; alphaPlus.FontSize = 12; alphaPlus.Background = Brushes.Transparent; DockPanel.SetDock(alphaPlus, Dock.Right);
            var alphaText = new TextBlock { Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Width = 38, TextAlignment = TextAlignment.Center, ToolTip = L.T("Przezroczystość klawiatury (widoczność)") }; DockPanel.SetDock(alphaText, Dock.Right);
            var alphaMinus = key(ico("\uE738", null), 30); alphaMinus.Height = 28; alphaMinus.FontSize = 12; alphaMinus.Background = Brushes.Transparent; DockPanel.SetDock(alphaMinus, Dock.Right);
            alphaPlus.ToolTip = L.T("Mniej przezroczysta (+5%)"); alphaMinus.ToolTip = L.T("Bardziej przezroczysta (−5%)");
            // ✓ autokorekta pisowni (wl./wyl., domyslnie wlaczona) - takze polskie znaki: zolw -> żółw
            var fixB = key(ico("\uE8FB", "abc"), 58); fixB.Height = 28; fixB.FontSize = 12; DockPanel.SetDock(fixB, Dock.Right);
            Action fixShow = () => { bool on = GetPref("klawiatura-korekta") != "0"; fixB.Background = on ? accent : Brushes.Transparent; fixB.ToolTip = on ? L.T("Autokorekta pisowni: włączona – słowo spoza słownika poprawiane po spacji (też polskie znaki: zolw → żółw); ⌫ zaraz po poprawce cofa ją. Kliknij, aby wyłączyć") : L.T("Autokorekta pisowni: wyłączona (kliknij, aby włączyć)"); };
            Action physApply = () => _physCorrect = GetPref("klawiatura-korekta") != "0" ? (Func<string, string>)(wd => { lock (noFix) return SwipeTyping.Complete(wd, noFix); }) : null;
            fixShow(); fixB.Click += (s, e) => { SetPref("klawiatura-korekta", GetPref("klawiatura-korekta") != "0" ? "0" : "1"); fixShow(); physApply(); };
            head.Children.Add(fixB);
            // 🔈 klik przy pisaniu (wl./wyl., domyslnie wlaczony)
            var clickB = key(ico("\uE995", null), 34); clickB.Height = 28; clickB.FontSize = 13; DockPanel.SetDock(clickB, Dock.Right);
            Action clickShow = () => { bool on = GetPref("klawiatura-klik") != "0"; clickB.Background = on ? accent : Brushes.Transparent; clickB.Content = ico(on ? "\uE995" : "\uE74F", null); clickB.ToolTip = on ? L.T("Dźwięk klawiszy: włączony (kliknij, aby wyciszyć)") : L.T("Dźwięk klawiszy: wyciszony (kliknij, aby włączyć)"); };
            clickShow(); clickB.Click += (s, e) => { SetPref("klawiatura-klik", GetPref("klawiatura-klik") != "0" ? "0" : "1"); clickShow(); };
            head.Children.Add(clickB);
            head.Children.Add(pinB); head.Children.Add(alphaPlus); head.Children.Add(alphaText); head.Children.Add(alphaMinus);
            // wlasny slownik (jak na Androidzie): podglad i usuwanie wyuczonych slow
            var dictB = key(ico("\uE82D", null), 34); dictB.Height = 28; dictB.FontSize = 13; dictB.Background = Brushes.Transparent; DockPanel.SetDock(dictB, Dock.Right);
            dictB.ToolTip = L.T("Mój słownik – słowa, których Velivo nauczyło się od Ciebie; dodawanie wielu słów naraz i usuwanie");
            // okno Mojego slownika: jedno slowo w linii - dopisywanie hurtem (wklej liste), usuwanie (skasuj linie), wczytanie z pliku .txt.
            // Zapis zostaje zaszyfrowany jak dotad (nie jawny plik na dysku)
            Window dictWin = null;
            dictB.Click += (s, e) =>
            {
                if (dictWin != null) { dictWin.Activate(); return; }
                List<string> own; lock (_swLearned) own = _swOwn.OrderBy(x => x, StringComparer.CurrentCulture).ToList();
                var box = new TextBox { FontSize = 15, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6), Text = string.Join("\r\n", own) };
                var info = new TextBlock { Margin = new Thickness(8, 6, 8, 6), TextWrapping = TextWrapping.Wrap, Text = L.T("Jedno słowo w linii. Wklej listę, aby dodać wiele słów naraz; skasuj linię, aby usunąć słowo.") };
                Func<string, Button> btn = t => new Button { Content = L.T(t), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
                var loadB = btn("📂 Wczytaj z pliku…"); loadB.ToolTip = L.T("Dopisz słowa z pliku tekstowego (.txt)");
                var sortB = btn("A→Z Sortuj"); sortB.ToolTip = L.T("Sortuj alfabetycznie i usuń powtórzenia");
                var saveB = btn("💾 Zapisz"); saveB.ToolTip = L.T("Zapisz Mój słownik (Ctrl+S)"); saveB.IsDefault = false;
                var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                Func<List<string>> words = () => box.Text.Split(new[] { '\r', '\n', ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length >= 2 && x.All(char.IsLetter)).Distinct().OrderBy(x => x, StringComparer.CurrentCulture).ToList();
                Action upd = () => count.Text = L.T("Słów: ") + words().Count;
                box.TextChanged += (a, b) => upd(); upd();
                sortB.Click += (a, b) => box.Text = string.Join("\r\n", words());
                loadB.Click += (a, b) =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Pliki tekstowe") + " (*.txt)|*.txt|*.*|*.*" };
                    if (dlg.ShowDialog(dictWin) != true) return;
                    try { box.Text = box.Text.TrimEnd() + "\r\n" + File.ReadAllText(dlg.FileName); box.Text = string.Join("\r\n", words()); } catch { }
                };
                Action save = () =>
                {
                    var nw = new HashSet<string>(words());
                    lock (_swLearned)
                    {
                        foreach (var r in _swOwn.Where(x => !nw.Contains(x)).ToList()) { _swOwn.Remove(r); _swLearned.Remove(r); SwipeTyping.RemoveUserWord(r); }
                        foreach (var n in nw) if (_swOwn.Add(n)) { SwipeTyping.AddUserWord(n); if (!_swLearned.ContainsKey(n)) _swLearned[n] = 1; }
                        SwipeTyping.SaveLearned(SwipeFile, _swLearned, _swOwn);
                    }
                    lock (noFix) { noFix.RemoveWhere(x => !nw.Contains(x)); foreach (var n in nw) noFix.Add(n); SetPref("klawiatura-bez-korekty", string.Join("|", noFix.Skip(Math.Max(0, noFix.Count - 500)))); }
                    box.Text = string.Join("\r\n", nw.OrderBy(x => x, StringComparer.CurrentCulture));
                };
                saveB.Click += (a, b) => save();
                var bar = new DockPanel { Margin = new Thickness(8, 4, 8, 8), LastChildFill = false };
                bar.Children.Add(count);
                foreach (var bb in new[] { saveB, sortB, loadB }) { DockPanel.SetDock(bb, Dock.Right); bar.Children.Add(bb); }
                var root = new DockPanel();
                DockPanel.SetDock(info, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom);
                root.Children.Add(info); root.Children.Add(bar); root.Children.Add(box);
                dictWin = new Window { Title = L.T("📖 Mój słownik"), Content = root, Width = 420, Height = 520, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                dictWin.PreviewKeyDown += (a, b) => { if (b.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { save(); b.Handled = true; } };
                dictWin.Loaded += (a, b) => { _physPause = true; box.Focus(); box.CaretIndex = box.Text.Length; };
                dictWin.Closing += (a, b) => save();
                dictWin.Closed += (a, b) => { _physPause = false; dictWin = null; };
                dictWin.Show();
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
            sayB.ToolTip = L.T("Czytanie na głos zaznaczonego tekstu w dowolnym programie (w polach haseł nigdy)"); sayB.Background = Brushes.Transparent;
            sayB.Click += (s, e) =>
            {
                var cm = new ContextMenu { PlacementTarget = sayB, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                var now = new MenuItem { Header = L.T("🗣 Przeczytaj zaznaczony tekst (każdy program – OpenOffice, Word, strony)") };
                now.Click += (q, w2) => ReadSelectionAloud();
                var stop = new MenuItem { Header = L.T("⏹ Zatrzymaj czytanie") };
                stop.Click += (q, w2) => (Application.Current?.MainWindow as MainWindow)?.KbStopSpeaking();
                var langM = new MenuItem { Header = L.T("Język czytania") };
                foreach (var (code, name) in new[] { ("pl", "Polski"), ("en", "English") })
                {
                    var li = new MenuItem { Header = name, IsCheckable = true, IsChecked = ReadLang == code };
                    li.Click += (q, w2) => SetPref("klawiatura-czytaj-jezyk", code);
                    langM.Items.Add(li);
                }
                cm.Items.Add(now); cm.Items.Add(stop); cm.Items.Add(new Separator()); cm.Items.Add(langM); cm.IsOpen = true;
            };
            head.Children.Add(sayB);
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
            // klawiatura numeryczna po prawej (wykorzystuje wolne miejsce obok liter): cyfry, dzialania, Enter
            var numPad = new StackPanel { Margin = new Thickness(0, 10, 10, 10), VerticalAlignment = VerticalAlignment.Bottom };
            // nad cyframi: kopiuj, wklej, cofnij, ponow (Ctrl+C / V / Z / Y w oknie, w ktorym piszesz)
            var editRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            foreach (var (g, tip, vk) in new[] { ("\uE8C8", L.T("Kopiuj (Ctrl+C)"), (ushort)0x43), ("\uE77F", L.T("Wklej (Ctrl+V)"), (ushort)0x56), ("\uE7A7", L.T("Cofnij (Ctrl+Z)"), (ushort)0x5A), ("\uE7A6", L.T("Ponów (Ctrl+Y)"), (ushort)0x59) })
            { var kb2 = key(ico(g, null), 46); kb2.ToolTip = tip; var v2 = vk; kb2.Click += (s, e) => SendCombo(0x11, v2); editRow.Children.Add(kb2); }
            numPad.Children.Add(editRow);
            foreach (var nr in new[] { "789/", "456*", "123-", "0.,+" })
            {
                var nrow = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var c0 in nr) { var c = c0; var nb = key(c.ToString(), 46); nb.Click += (s, e) => tap(0, c); nrow.Children.Add(nb); }
                numPad.Children.Add(nrow);
            }
            var numEnter = key("⏎", 196); numEnter.Background = accent; numEnter.Foreground = accentText; numEnter.ToolTip = L.T("Enter – zatwierdź / nowa linia"); numEnter.Click += (s, e) => tap(0x0D, '\0');
            numPad.Children.Add(numEnter);
            numPad.Visibility = GetPref("klawiatura-numeryczna") == "0" ? Visibility.Collapsed : Visibility.Visible;
            var keysRow = new StackPanel { Orientation = Orientation.Horizontal }; keysRow.Children.Add(root); keysRow.Children.Add(numPad);
            var keys = new Grid(); keys.Children.Add(keysRow);
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
            bool padUsed = false; int padR = 2, padC = 5; ushort padPrev = 0; DateTime padNext = DateTime.MinValue; int padDir = 0;
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
                if (!ok) { padSel.Visibility = Visibility.Collapsed; return; }
                ushort bt = st.Buttons;
                if (st.Buttons != 0 || Math.Abs((int)st.LX) > 16000 || Math.Abs((int)st.LY) > 16000) padUsed = true;
                padSel.Visibility = padUsed ? Visibility.Visible : Visibility.Collapsed;   // ramka dopiero po uzyciu pada (Windows czasem zglasza pad, ktorego nikt nie uzywa)
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
                    bool up0 = upper || capNext && GetPref("klawiatura-korekta") != "0" && !SecretField(); capNext = false; lastSent = ' ';   // po ". " wielka litera
                    var found = t.Result.Select(x => up0 ? char.ToUpper(x[0]) + x.Substring(1) : x).ToList();
                    learn(cur); cur = "";
                    foreach (var ch in found[0]) SendKey(0, ch); SendKey(0, ' ');
                    prevBeforeSwipe = prevWord;
                    bool unsure = SwipeTyping.LastUncertain && found.Count > 1;
                    ask.Text = L.T("Poprawić na:"); ask.Visibility = unsure ? Visibility.Visible : Visibility.Collapsed; title.Visibility = unsure ? Visibility.Collapsed : Visibility.Visible;
                    lastTrace = pts; if (!SecretField()) SwipeTyping.LearnShape(found[0], pts, lastKeyW);   // nauka Twojego ruchu (nie w polu hasla)
                    learn(found[0]); lastSwipe = found[0];
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
            bool seeThrough = SavedAlpha(L.T("Klawiatura Velivo")) < 0.999;   // przezroczysta tylko na zyczenie - nieprzezroczysta rysuje slad plynniej
            var w = new Window
            {
                Title = L.T("Klawiatura Velivo"), AllowsTransparency = seeThrough, WindowStyle = WindowStyle.None, Background = seeThrough ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0x19, 0x1B, 0x33)), ResizeMode = ResizeMode.CanResize,   // bez przezroczystosci okna - rysowanie na karcie graficznej, plynny slad
                SizeToContent = SizeToContent.WidthAndHeight, Topmost = GetPref("klawiatura-wierzch") == "1", ShowActivated = false, ShowInTaskbar = false,
                Content = new Border { Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromRgb(0x1E, 0x18, 0x38), 0), new GradientStop(Color.FromRgb(0x19, 0x1B, 0x33), 0.55), new GradientStop(Color.FromRgb(0x13, 0x2A, 0x35), 1) }, new Point(0, 0), new Point(1, 1)),
                    CornerRadius = new CornerRadius(14), BorderBrush = new LinearGradientBrush(Color.FromArgb(0x90, 0x8B, 0x5C, 0xF6), Color.FromArgb(0x90, 0x06, 0xB6, 0xD4), 45), BorderThickness = new Thickness(1), Child = new Viewbox { Child = all, Stretch = Stretch.Fill } }   // klawisze wypelniaja okno - szerokosc i wysokosc ustawiasz niezaleznie
            };
            head.MouseLeftButtonDown += (s, e) => { try { w.DragMove(); } catch (InvalidOperationException) { } };
            close.Click += (s, e) => Sail(w, all, false, w.Close); close.ToolTip = L.T("Zamknij klawiaturę");
            minB.Click += (s, e) => Sail(w, all, false, w.Hide);
            Action alphaShow = () => alphaText.Text = (int)Math.Round(WindowAlpha(w) * 100) + "%";
            alphaPlus.Click += (s, e) => { SetWindowAlpha(w, WindowAlpha(w) + 0.05); alphaShow(); };
            alphaMinus.Click += (s, e) =>
            {
                SetWindowAlpha(w, WindowAlpha(w) - 0.05); alphaShow();
                if (!w.AllowsTransparency) { var t = _kbdTarget; w.Close(); ShowScreenKeyboard(t); }   // pierwsze sciszenie widocznosci: klawiatura otwiera sie od nowa jako przezroczysta (Windows nie zmienia tego w otwartym oknie)
            };
            w.Loaded += (s, e) => alphaShow();
            pinB.Click += (s, e) => { bool on = GetPref("klawiatura-wierzch") != "1"; SetPref("klawiatura-wierzch", on ? "1" : "0"); w.Topmost = on; pinShow(); };
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
                else { w.Width = Math.Min(wa.Width * 0.9, 640); w.Height = w.Width * nh / nw; }   // na start ok. 640 punktow szerokosci
                // zawsze w prawym dolnym rogu, tuz nad paskiem zadan
                double ww = double.IsNaN(w.Width) ? nw : w.Width, wh = double.IsNaN(w.Height) ? nh : w.Height;
                w.Left = wa.Right - ww - 10; w.Top = wa.Bottom - wh - 10;   // prawy dolny rog, nad zegarem
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
            // zwykla klawiatura: autokorekta w podsluchu (PhysKey); pole hasla sprawdzane co 0,3 s - tam nic nie poprawiamy
            _physSecret = true;
            var secretTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            // bez pisania przez 20 s - nie pytamy Windows co 0,3 s (mniej obciazenia, bez przycinek); wtedy bezpiecznie "pole hasla" do pierwszego sprawdzenia
            secretTimer.Tick += (s, e) => { if (Environment.TickCount64 - _pLast > 20000 && Environment.TickCount64 - _tapLast > 20000) { _physSecret = true; return; } try { _physSecret = SecretField(); } catch (Exception) { _physSecret = true; } };
            secretTimer.Start();
            _physNotify = (from, to) => { ask.Text = L.T("Poprawiono: ") + from + " → " + to + L.T("  (⌫ cofa)"); ask.Visibility = Visibility.Visible; title.Visibility = Visibility.Collapsed; };
            _physUndone = (from, to) =>
            {
                keepWord(from);
                ask.Visibility = Visibility.Collapsed; title.Visibility = Visibility.Visible;
            };
            _physAsk = word => { if (GetPref("klawiatura-korekta") == "0" || _physSecret) return; List<string> sg; lock (noFix) sg = SwipeTyping.Suggest(word, noFix); if (sg.Count > 0) askFix(word, sg); };
            _physPred = (wd, dn) => { if (dn && wd != null) learn(wd); predShow(wd, dn); };   // najpierw nauka (poprzednie slowa), potem propozycje   // zwykla klawiatura: skonczone slowo tez uczy (czesto pisane - wyzej w propozycjach)
            SwipeTyping.UseCount = wd => { lock (_swLearned) return _swLearned.TryGetValue(wd, out var n) ? n : 0; };
            physApply();
            StartKeyEcho();
            w.Closed += (s, e) => { _kbd = null; _kbdHwnd = IntPtr.Zero; padTimer.Stop(); secretTimer.Stop(); StopKeyEcho(); };
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
