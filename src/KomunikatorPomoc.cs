using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // 🖱 Pomoc zdalna w rozmowie wideo: rozmówca widzi udostępniony ekran i - tylko po zgodzie (pytanie na stronie rozmowy) -
    // steruje myszą i klawiaturą. Ruchy przychodzą kanałem danych WebRTC prosto od rozmówcy; przez cały czas na wierzchu
    // czerwony pasek „Zakończ sterowanie”. Koniec rozmowy = koniec sterowania.
    public partial class MainWindow
    {
        System.Drawing.Rectangle? _komCtlScreen; Window _komCtlBar;
        readonly HashSet<ushort> _komCtlKeys = new HashSet<ushort>(); readonly HashSet<uint> _komCtlButtons = new HashSet<uint>();   // wciśnięte przez rozmówcę - puszczane na końcu sterowania

        [StructLayout(LayoutKind.Sequential)] struct KomMouseIn { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct KomKeyIn { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] struct KomInUnion { [FieldOffset(0)] public KomMouseIn m; [FieldOffset(0)] public KomKeyIn k; }
        [StructLayout(LayoutKind.Sequential)] struct KomInput { public int type; public KomInUnion u; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, KomInput[] inputs, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
        static DateTime _komCtlBlockedAt;
        // Windows blokuje ruchy, gdy na wierzchu jest okno administratora (UIPI) - pomagający dostaje komunikat, dziennik nazwę okna
        static void KomSend(KomInput i)
        {
            if (SendInput(1, new[] { i }, Marshal.SizeOf<KomInput>()) != 0 || DateTime.UtcNow - _komCtlBlockedAt < TimeSpan.FromSeconds(5)) return;
            _komCtlBlockedAt = DateTime.UtcNow; int err = Marshal.GetLastWin32Error();
            var t = new System.Text.StringBuilder(200); try { GetWindowText(GetForegroundWindow(), t, 200); } catch (Exception) { }
            App.LogInfo("Pomoc zdalna: Windows zablokował ruch myszy/klawiatury (błąd " + err + "), okno na wierzchu: " + t);
            _komCtlBlockedPost?.Invoke("{\"k\":\"ctl-blocked\"}");
        }
        static Action<string> _komCtlBlockedPost;
        static void KomMouse(uint flags, int dx = 0, int dy = 0, int data = 0) =>
            KomSend(new KomInput { type = 0, u = new KomInUnion { m = new KomMouseIn { dx = dx, dy = dy, flags = flags, data = unchecked((uint)data) } } });
        static void KomKey(ushort vk, bool up)
        {
            bool ext = (vk >= 0x21 && vk <= 0x2E) || vk == 0x5B || vk == 0x5C || vk == 0x6F || vk == 0x90;   // strzałki, Home/End, Ins/Del, Win...
            KomSend(new KomInput { type = 1, u = new KomInUnion { k = new KomKeyIn { vk = vk, flags = (up ? 2u : 0u) | (ext ? 1u : 0u) } } });
        }

        // pokazywany ekran = monitor o tym rozmiarze (najpierw główny)
        void KomCtlOn(KomContact c, int w, int h, Action<string> post)
        {
            System.Drawing.Rectangle? pick = null;
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
                if (s.Bounds.Width == w && s.Bounds.Height == h && (pick == null || s.Primary)) pick = s.Bounds;
            _komCtlScreen = pick ?? System.Windows.Forms.Screen.PrimaryScreen.Bounds; _komCtlBlockedPost = post;
            if (_komCtlBar != null) return;
            var bar = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
                SizeToContent = SizeToContent.WidthAndHeight, Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), WindowStartupLocation = WindowStartupLocation.Manual, Top = 0 };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 6, 8, 6) };
            row.Children.Add(new TextBlock { Text = "🖱 " + c.Name + " " + L.T("steruje Twoim komputerem"), Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var stop = new Button { Content = "⏹ " + L.T("Zakończ sterowanie"), ToolTip = L.T("Rozmówca przestaje sterować; rozmowa trwa dalej"), Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3),
                Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(0x7F, 0x1D, 0x1D)), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            stop.Click += (s, e) => { post("{\"k\":\"ctl-stop\"}"); KomCtlOff(); };
            row.Children.Add(stop); bar.Content = row;
            bar.Loaded += (s, e) => { var wa = SystemParameters.WorkArea; bar.Left = wa.Left + (wa.Width - bar.ActualWidth) / 2; bar.Top = wa.Top; };
            _komCtlBar = bar; bar.Show();
        }

        // ruchy rozmówcy w osobnym wątku, nie w wątku okien Velivo: kliknięcie „minimalizuj / zamknij / przesuń” na oknie Velivo uruchamia w wątku tego okna pętlę Windows,
        // która czeka na puszczenie przycisku - a puszczenie stało w kolejce tego samego wątku (okna Velivo zacinały się przy pomocy zdalnej)
        readonly System.Collections.Concurrent.BlockingCollection<Action> _komCtlQ = new System.Collections.Concurrent.BlockingCollection<Action>();
        System.Threading.Thread _komCtlThread;
        void KomCtlDo(Action a)
        {
            if (_komCtlThread == null)
            {
                _komCtlThread = new System.Threading.Thread(() => { foreach (var x in _komCtlQ.GetConsumingEnumerable()) try { x(); } catch (Exception) { } }) { IsBackground = true, Name = "Velivo pomoc zdalna" };
                _komCtlThread.Start();
            }
            _komCtlQ.Add(a);
        }

        void KomCtlOff()
        {
            if (_komCtlScreen == null && _komCtlBar == null) return;
            _komCtlScreen = null;
            KomCtlDo(KomCtlRelease);
            try { _komCtlBar?.Close(); } catch (Exception) { }
            _komCtlBar = null;
        }

        // nic nie zostaje wciśnięte (koniec sterowania, okno pomagającego zminimalizowane, Alt+Tab u niego)
        void KomCtlRelease()
        {
            foreach (var f in _komCtlButtons) KomMouse(f << 1); _komCtlButtons.Clear();
            foreach (var vk in _komCtlKeys) KomKey(vk, true); _komCtlKeys.Clear();
            foreach (ushort vk in new ushort[] { 0x10, 0x11, 0x12, 0x5B }) KomKey(vk, true);
        }

        // ruch od rozmówcy: x, y - ułamek pokazywanego ekranu
        void KomCtlInput(JsonObject o)
        {
            if (_komCtlScreen is not System.Drawing.Rectangle sc) return;
            try
            {
                var t = (string)o["t"];
                if (t == "k") { var vk = (ushort)(int)o["v"]; bool up = (bool?)o["up"] == true; if (up) _komCtlKeys.Remove(vk); else _komCtlKeys.Add(vk); KomKey(vk, up); return; }
                if (t == "rel") { KomCtlRelease(); return; }
                if (t == "w") { KomMouse(0x0800, data: (int)o["d"]); return; }
                var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
                double x = Math.Clamp((double)o["x"], 0, 1), y = Math.Clamp((double)o["y"], 0, 1);
                int ax = (int)((sc.Left + x * (sc.Width - 1) - vs.Left) * 65535 / Math.Max(1, vs.Width - 1));
                int ay = (int)((sc.Top + y * (sc.Height - 1) - vs.Top) * 65535 / Math.Max(1, vs.Height - 1));
                KomMouse(0x0001 | 0x8000 | 0x4000, ax, ay);   // ruch, absolutnie, cały pulpit
                if (t == "d" || t == "u")
                {
                    int b = (int?)o["b"] ?? 0;
                    uint f = b == 2 ? 0x0008u : b == 1 ? 0x0020u : 0x0002u;
                    if (t == "u") _komCtlButtons.Remove(f); else _komCtlButtons.Add(f);
                    KomMouse(t == "u" ? f << 1 : f);
                }
            }
            catch (Exception) { }
        }
    }
}
