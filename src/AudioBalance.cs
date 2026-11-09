using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Velivo
{
    // 🎚 Audio Balance (próba): VLC oddaje PCM (amem: SetAudioFormat + SetAudioCallbacks) → AbProcessor (pomiar BS.1770-4,
    // wyrównywanie, kompresor, limiter True Peak, korektor) → wyjście Windows (waveOut). Bez drugiego dekodowania i drugiego odtwarzacza.
    // Synchronizacja: VLC podaje dla każdego kawałka czas odtworzenia (pts w zegarze libVLC); kawałek trafia do karty dźwiękowej tak,
    // żeby zabrzmiał w tej chwili (kolejka w karcie + wyprzedzenie limitera), spóźnione są pomijane, a wcześniejsze czekają.
    sealed class AbOutput : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct WaveFormat { public ushort Tag, Channels; public uint Rate, BytesPerSec; public ushort Align, Bits, Size; }
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, uint dev, ref WaveFormat f, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
        const int HdrSize = 48, FlagsOff = 24, WhdrDone = 1;   // WAVEHDR x64

        sealed class Chunk { public float[] Data; public int Frames; public long Pts; }
        sealed class Buf { public IntPtr Hdr, Mem; public int Cap, Frames; public bool Busy; }

        public readonly AbProcessor Proc;
        IntPtr _h; readonly List<Buf> _bufs = new List<Buf>();
        readonly Queue<Chunk> _q = new Queue<Chunk>(); readonly object _lk = new object();
        Thread _th; volatile bool _run, _paused; volatile int _flushReq;
        public volatile string Source = "";
        public long LateDrops, EarlyWaits;

        readonly LibVLCSharp.Shared.LibVLC _vlc;
        public AbOutput(AbProcessor p, LibVLCSharp.Shared.LibVLC vlc) { Proc = p; _vlc = vlc; }

        public bool Open()
        {
            var f = new WaveFormat { Tag = 1, Channels = 2, Rate = AbProcessor.Fs, Bits = 16, Align = 4, BytesPerSec = AbProcessor.Fs * 4 };
            if (waveOutOpen(out _h, 0xFFFFFFFF, ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0) { _h = IntPtr.Zero; return false; }
            _run = true; _th = new Thread(Loop) { IsBackground = true, Name = "Velivo Audio Balance", Priority = ThreadPriority.AboveNormal }; _th.Start();
            return true;
        }

        // wywolania VLC (watek dzwieku VLC) - tylko kopia, bez obliczen
        public void Play(IntPtr samples, uint count, long pts)
        {
            var c = new Chunk { Frames = (int)count, Pts = pts, Data = new float[count * 2] };
            Marshal.Copy(samples, c.Data, 0, (int)count * 2);
            lock (_lk) { _q.Enqueue(c); while (_q.Count > 400) _q.Dequeue(); Monitor.Pulse(_lk); }
        }
        public void Pause() { _paused = true; if (_h != IntPtr.Zero) waveOutPause(_h); }
        public void Resume() { _paused = false; if (_h != IntPtr.Zero) waveOutRestart(_h); lock (_lk) Monitor.Pulse(_lk); }
        public void Flush() { lock (_lk) { _q.Clear(); } Interlocked.Increment(ref _flushReq); lock (_lk) Monitor.Pulse(_lk); }

        long Now => _vlc.Clock;   // zegar libVLC (µs) - ten sam co pts

        int QueuedFrames() { int n = 0; foreach (var b in _bufs) if (b.Busy) { if ((Marshal.ReadInt32(b.Hdr, FlagsOff) & WhdrDone) != 0) { waveOutUnprepareHeader(_h, b.Hdr, HdrSize); b.Busy = false; } else n += b.Frames; } return n; }

        void Loop()
        {
            var pcm = new short[0];
            int flushSeen = 0;
            while (_run)
            {
                try
                {
                    if (_flushReq != flushSeen)
                    {   // przewiniecie / zmiana kanalu: karta od nowa, procesor bez starych okien (zintegrowany zostaje dla filmu)
                        flushSeen = _flushReq; waveOutReset(_h); QueuedFrames(); Proc.Reset();
                        if (_paused) waveOutPause(_h);
                    }
                    Chunk c = null;
                    lock (_lk) { if (_q.Count == 0 || _paused) { Monitor.Wait(_lk, 20); continue; } c = _q.Peek(); }
                    long queuedUs = QueuedFrames() * 1_000_000L / AbProcessor.Fs;
                    long latUs = queuedUs + 5000 + AbTruePeak.Delay * 1_000_000L / AbProcessor.Fs;   // + wyprzedzenie limitera
                    long dueIn = c.Pts - Now;   // za ile ma zabrzmiec
                    long durUs = c.Frames * 1_000_000L / AbProcessor.Fs;
                    if (dueIn > latUs + 8000) { Interlocked.Increment(ref EarlyWaits); Thread.Sleep((int)Math.Min(20, Math.Max(1, (dueIn - latUs) / 1000 - 4))); continue; }   // za wczesnie - czekamy
                    lock (_lk) { if (_q.Count > 0 && _q.Peek() == c) _q.Dequeue(); else continue; }
                    if (dueIn + durUs < latUs - 60000) { Interlocked.Increment(ref LateDrops); continue; }   // spozniony o ponad 60 ms - pomijamy (obraz jest wazniejszy)
                    Proc.Process(c.Data, c.Frames);
                    if (pcm.Length < c.Frames * 2) pcm = new short[c.Frames * 2];
                    for (int i = 0; i < c.Frames * 2; i++) { float v = c.Data[i] * 32767f; pcm[i] = (short)(v > 32767 ? 32767 : v < -32768 ? -32768 : v); }
                    var b = _bufs.FirstOrDefault(x => !x.Busy && x.Cap >= c.Frames);
                    if (b == null)
                    {
                        if (_bufs.Count > 200) { var old = _bufs.FirstOrDefault(x => !x.Busy); if (old != null) { Marshal.FreeHGlobal(old.Mem); Marshal.FreeHGlobal(old.Hdr); _bufs.Remove(old); } }
                        b = new Buf { Cap = Math.Max(c.Frames, 2048), Hdr = Marshal.AllocHGlobal(HdrSize) }; b.Mem = Marshal.AllocHGlobal(b.Cap * 4); _bufs.Add(b);
                    }
                    Marshal.Copy(pcm, 0, b.Mem, c.Frames * 2);
                    for (int o = 0; o < HdrSize; o += 8) Marshal.WriteInt64(b.Hdr, o, 0);
                    Marshal.WriteIntPtr(b.Hdr, 0, b.Mem); Marshal.WriteInt32(b.Hdr, 8, c.Frames * 4);
                    b.Frames = c.Frames; b.Busy = true;
                    waveOutPrepareHeader(_h, b.Hdr, HdrSize); waveOutWrite(_h, b.Hdr, HdrSize);
                    Proc.Active = true;
                }
                catch (Exception ex) { App.LogError(ex); Thread.Sleep(50); }
            }
        }

        public void Dispose()
        {
            _run = false; lock (_lk) Monitor.Pulse(_lk);
            try { _th?.Join(500); } catch (Exception) { }
            if (_h != IntPtr.Zero) { waveOutReset(_h); QueuedFrames(); foreach (var b in _bufs) { if (!b.Busy) { Marshal.FreeHGlobal(b.Mem); Marshal.FreeHGlobal(b.Hdr); } } waveOutClose(_h); _h = IntPtr.Zero; }
            _bufs.Clear(); Proc.Active = false;
        }
    }

    public partial class MainWindow
    {
        // jeden procesor na cale Velivo - widget i ustawienia steruja nim, nie tworza wlasnego
        internal static readonly AbProcessor AbProc = new AbProcessor();
        AbOutput _abOut;
        LibVLCSharp.Shared.MediaPlayer.LibVLCAudioPlayCb _abPlay; LibVLCSharp.Shared.MediaPlayer.LibVLCAudioPauseCb _abPause; LibVLCSharp.Shared.MediaPlayer.LibVLCAudioResumeCb _abResume;
        LibVLCSharp.Shared.MediaPlayer.LibVLCAudioFlushCb _abFlush; LibVLCSharp.Shared.MediaPlayer.LibVLCAudioDrainCb _abDrain; LibVLCSharp.Shared.MediaPlayer.LibVLCVolumeCb _abVol;

        static string AbProfileName => GetPref("ab-profil") ?? "off";
        static bool AbEqOn => GetPref("ab-eq") == "1";
        internal static bool AbWanted => AbProfileName != "off" || AbEqOn;

        static void AbLoadSettings()
        {
            AbProc.Profile = AbProfile.ByName(AbProfileName);
            AbProc.Target = float.TryParse(GetPref("ab-cel"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : -18f;
            AbProc.EqOn = AbEqOn;
            var g = (GetPref("ab-eq-db") ?? "").Split(',').Select(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0).ToList();
            while (g.Count < 10) g.Add(0);
            AbProc.SetEq(g.Take(10).ToArray());
        }

        // przed Play: odtwarzacz oddaje dzwiek Velivo (tylko gdy Audio Balance albo korektor wlaczone); false = zwykle wyjscie VLC
        bool AbAttach(LibVLCSharp.Shared.MediaPlayer mp)
        {
            AbLoadSettings();
            if (!AbWanted) return false;
            AbDetach();
            var o = new AbOutput(AbProc, _libVlc);
            if (!o.Open()) { App.LogError(new Exception("Audio Balance: wyjście dźwięku Windows (waveOut) nie otworzyło się – zwykły dźwięk VLC")); return false; }
            _abOut = o;
            _abPlay = (d, s, c, p) => _abOut?.Play(s, c, p);
            _abPause = (d, p) => _abOut?.Pause();
            _abResume = (d, p) => _abOut?.Resume();
            _abFlush = (d, p) => _abOut?.Flush();
            _abDrain = d => { };
            _abVol = (d, v, m) => AbProc.Volume = m ? 0 : v;
            mp.SetAudioFormat("FL32", AbProcessor.Fs, 2);   // VLC dekoduje i miksuje (5.1/AC3/E-AC3/AAC) do stereo 48 kHz
            mp.SetAudioCallbacks(_abPlay, _abPause, _abResume, _abFlush, _abDrain);
            mp.SetVolumeCallback(_abVol);
            AbProc.NewMedia();
            return true;
        }

        void AbDetach() { var o = _abOut; _abOut = null; o?.Dispose(); }
        internal void AbNewSource(string name) { if (_abOut != null) { _abOut.Source = name ?? ""; AbProc.NewMedia(); } }

        // ---------- 🎚 Audio Monitor: osobne okienko + wlasna ikonka w zasobniku ----------
        Window _abWin; System.Windows.Forms.NotifyIcon _abTray; DispatcherTimer _abTimer;
        static readonly string[] AbProfiles = { "off", "light", "balanced", "night" };
        static string AbProfileLabel(string p) => p == "light" ? "Light" : p == "balanced" ? "Balanced" : p == "night" ? "Night" : "Off";

        internal void ShowAudioMonitor()
        {
            if (_abWin != null) { _abWin.Show(); if (_abWin.WindowState == WindowState.Minimized) _abWin.WindowState = WindowState.Normal; _abWin.Activate(); _abTimer?.Start(); return; }
            AbLoadSettings();
            var bg = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1F)); var fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF2)); var dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA6));
            var acc = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF)); var acc2 = new SolidColorBrush(Color.FromRgb(0x2E, 0xD3, 0xC6));
            var win = new Window { Title = "Velivo – Audio Monitor", Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.CanMinimize, Background = bg, Foreground = fg, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, Icon = Icon };
            var root = new StackPanel { Margin = new Thickness(12) };
            TextBlock T(string s, double size = 12, Brush c = null, FontWeight? w = null) => new TextBlock { Text = s, FontSize = size, Foreground = c ?? fg, FontWeight = w ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var state = T("", 12, acc2, FontWeights.SemiBold); DockPanel.SetDock(state, Dock.Right); head.Children.Add(state);
            head.Children.Add(T(L.T("🎚 Audio Monitor"), 15, fg, FontWeights.SemiBold));
            root.Children.Add(head);
            var src = T("", 11, dim); root.Children.Add(src);
            // widmo (FFT z prawdziwych probek wyjscia)
            const int NB = 32;
            var spec = new Canvas { Height = 110, Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0D, 0x14)), Margin = new Thickness(0, 8, 0, 4), ClipToBounds = true };
            var bars = new Rectangle[NB]; var peaks = new Rectangle[NB]; var hold = new double[NB];
            var grad = new LinearGradientBrush(Color.FromRgb(0x2E, 0xD3, 0xC6), Color.FromRgb(0x7C, 0x5C, 0xFF), 90);
            for (int i = 0; i < NB; i++) { bars[i] = new Rectangle { Fill = grad, RadiusX = 1.5, RadiusY = 1.5 }; peaks[i] = new Rectangle { Fill = fg, Height = 2, Opacity = 0.8 }; spec.Children.Add(bars[i]); spec.Children.Add(peaks[i]); }
            root.Children.Add(spec);
            var axis = new Grid(); foreach (var (t, al) in new[] { ("50 Hz", HorizontalAlignment.Left), ("1 kHz", HorizontalAlignment.Center), ("16 kHz", HorizontalAlignment.Right) }) { var a = T(t, 10, dim); a.HorizontalAlignment = al; axis.Children.Add(a); }
            root.Children.Add(axis);
            // LOW / MID / HIGH
            var lmh = new Grid { Margin = new Thickness(0, 8, 0, 0) }; var lmhBars = new ProgressBar[3]; var lmhTxt = new[] { "LOW", "MID", "HIGH" };
            for (int i = 0; i < 3; i++)
            {
                lmh.ColumnDefinitions.Add(new ColumnDefinition());
                var sp = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 4, 0, i == 2 ? 0 : 4, 0) };
                sp.Children.Add(T(lmhTxt[i], 10, dim)); lmhBars[i] = new ProgressBar { Height = 8, Minimum = -60, Maximum = 0, Foreground = i == 0 ? acc : i == 1 ? acc2 : Brushes.Gold, Background = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x33)), BorderThickness = new Thickness(0) };
                sp.Children.Add(lmhBars[i]); Grid.SetColumn(sp, i); lmh.Children.Add(sp);
            }
            root.Children.Add(lmh);
            // poziom glosnosci (momentary, skala -40..0 LUFS) i liczby
            var lvl = new ProgressBar { Height = 10, Minimum = -40, Maximum = 0, Margin = new Thickness(0, 10, 0, 2), Foreground = acc2, Background = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x33)), BorderThickness = new Thickness(0), ToolTip = L.T("Głośność chwilowa (400 ms) dźwięku, który słyszysz") };
            root.Children.Add(lvl);
            var nums = new Grid { Margin = new Thickness(0, 4, 0, 0) }; var vals = new TextBlock[4];
            var names = new[] { L.T("Chwilowa"), L.T("3 s"), L.T("Całość"), "True Peak" };
            var tips = new[] { L.T("Momentary LUFS – ostatnie 400 ms"), L.T("Short-term LUFS – ostatnie 3 s"), L.T("Integrated LUFS – od początku filmu/kanału (z bramkami BS.1770)"), L.T("Najwyższy szczyt między próbkami z ostatniej sekundy (dBTP)") };
            for (int i = 0; i < 4; i++)
            {
                nums.ColumnDefinitions.Add(new ColumnDefinition());
                var sp = new StackPanel { ToolTip = tips[i] }; sp.Children.Add(T(names[i], 10, dim)); vals[i] = T("–", 15, fg, FontWeights.SemiBold); sp.Children.Add(vals[i]);
                Grid.SetColumn(sp, i); nums.Children.Add(sp);
            }
            root.Children.Add(nums);
            // wykres glosnosci: ORIGINAL (wejscie) i AUDIOBALANCE (wyjscie), ostatnie 15 s, linia poziomu docelowego
            var leg = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 2) };
            var red = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x53)); var green = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x5A));
            leg.Children.Add(T("● ORIGINAL   ", 10, red, FontWeights.SemiBold)); leg.Children.Add(T("● AUDIOBALANCE", 10, green, FontWeights.SemiBold));
            root.Children.Add(leg);
            var hist = new Canvas { Height = 90, Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0D, 0x14)), ClipToBounds = true, ToolTip = L.T("Głośność chwilowa z ostatnich 15 s: czerwona – oryginał, zielona – po Audio Balance. Przerywana linia – poziom docelowy.") };
            var plIn = new Polyline { Stroke = red, StrokeThickness = 1.5 }; var plOut = new Polyline { Stroke = green, StrokeThickness = 2 };
            var tl = new Line { Stroke = green, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, Opacity = 0.6 }; var tlTxt = T("", 9, green);
            hist.Children.Add(tl); hist.Children.Add(tlTxt); hist.Children.Add(plIn); hist.Children.Add(plOut);
            root.Children.Add(hist);
            const int HN = 150; var hIn = new double[HN]; var hOut = new double[HN]; for (int i = 0; i < HN; i++) hIn[i] = hOut[i] = -70; var hLast = DateTime.MinValue;
            var gainTxt = T("", 11, dim); gainTxt.Margin = new Thickness(0, 6, 0, 0); root.Children.Add(gainTxt);
            // profil i cel - zmiana od razu w glownym procesorze
            var prow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var pbtn = new Dictionary<string, Button>();
            Button Mk(string text, Action a) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 3, 10, 3), Foreground = fg, Background = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x33)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x52)) }; b.Click += (s, e) => a(); return b; }
            void MarkProfile() { foreach (var kv in pbtn) kv.Value.Background = kv.Key == AbProfileName ? acc : new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x33)); }
            foreach (var p in AbProfiles) { var pp = p; var b = Mk(AbProfileLabel(p), () => { AbSetProfile(pp); MarkProfile(); }); b.ToolTip = AbProfileTip(p); pbtn[p] = b; prow.Children.Add(b); }
            MarkProfile();
            root.Children.Add(prow);
            var tgt = new ComboBox { Width = 210, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = L.T("Poziom docelowy – do jakiej głośności Velivo wyrównuje dźwięk. Wybierz sam: głośniej (-14) albo ciszej (-23).") };
            foreach (var v in AbTargets) tgt.Items.Add(new ComboBoxItem { Content = AbTargetLabel(v), Tag = v });
            tgt.SelectedItem = tgt.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Math.Abs((float)i.Tag - AbProc.Target) < 0.1) ?? tgt.Items[2];
            tgt.SelectionChanged += (s, e) => { if (tgt.SelectedItem is ComboBoxItem it) { AbProc.Target = (float)it.Tag; SetPref("ab-cel", ((float)it.Tag).ToString(System.Globalization.CultureInfo.InvariantCulture)); } };
            root.Children.Add(tgt);
            // korektor (EQ) - zwiniety
            var eqExp = new Expander { Header = L.T("Korektor (EQ)"), Foreground = fg, Margin = new Thickness(0, 8, 0, 0) };
            var eqPanel = new StackPanel();
            var eqOn = new CheckBox { Content = L.T("Korektor włączony"), Foreground = fg, IsChecked = AbEqOn, Margin = new Thickness(0, 4, 0, 4) };
            eqOn.Click += (s, e) => { SetPref("ab-eq", eqOn.IsChecked == true ? "1" : "0"); AbProc.EqOn = eqOn.IsChecked == true; AbNeedsRestartHint(); };
            eqPanel.Children.Add(eqOn);
            var eqGrid = new Grid(); var cur = (GetPref("ab-eq-db") ?? "").Split(',');
            var sl = new Slider[10];
            for (int i = 0; i < 10; i++)
            {
                eqGrid.ColumnDefinitions.Add(new ColumnDefinition());
                double v0 = i < cur.Length && double.TryParse(cur[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vv) ? vv : 0;
                var sp = new StackPanel();
                sl[i] = new Slider { Orientation = Orientation.Vertical, Minimum = -12, Maximum = 12, Value = v0, Height = 90, HorizontalAlignment = HorizontalAlignment.Center, TickFrequency = 1, IsSnapToTickEnabled = true, ToolTip = AbProcessor.EqFreq[i] + " Hz" };
                double fq = AbProcessor.EqFreq[i];
                sp.Children.Add(sl[i]); var lab = T(fq >= 1000 ? (fq / 1000) + "k" : fq.ToString(), 9, dim); lab.HorizontalAlignment = HorizontalAlignment.Center; sp.Children.Add(lab);
                Grid.SetColumn(sp, i); eqGrid.Children.Add(sp);
                sl[i].ValueChanged += (s, e) => { var g = sl.Select(x => x.Value).ToArray(); AbProc.SetEq(g); SetPref("ab-eq-db", string.Join(",", g.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)))); };
            }
            eqPanel.Children.Add(eqGrid);
            var presets = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var (nm, g) in new[] { (L.T("Płasko"), new double[10]), (L.T("Dialogi"), new double[] { -4, -3, -1, 0, 2, 4, 4, 2, 0, -1 }), (L.T("Bas"), new double[] { 6, 5, 3, 1, 0, 0, 0, 0, 0, 0 }), (L.T("Muzyka"), new double[] { 3, 2, 0, -1, -1, 0, 1, 2, 3, 3 }) })
            { var gg = g; presets.Children.Add(Mk(nm, () => { for (int i = 0; i < 10; i++) sl[i].Value = gg[i]; })); }
            eqPanel.Children.Add(presets);
            eqExp.Content = eqPanel; root.Children.Add(eqExp);
            // przyciski okna
            var brow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            brow.Children.Add(Mk(L.T("⤵ Do zasobnika"), () => AbHideMonitor()));
            brow.Children.Add(Mk(L.T("✕ Zamknij widżet"), () => win.Close()));
            root.Children.Add(brow);
            root.Children.Add(T(L.T("Ukrycie lub zamknięcie widżetu nie wyłącza Audio Balance – dźwięk dalej jest wyrównywany."), 10, dim));
            win.Content = root;
            _abWin = win;

            // odswiezanie ~30 kl./s tylko, gdy widzet widoczny (FFT w watku okna, nie dzwieku)
            var fftRe = new double[2048]; var fftIm = new double[2048]; var hann = Enumerable.Range(0, 2048).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 2047)).ToArray();
            var edges = Enumerable.Range(0, NB + 1).Select(i => 50 * Math.Pow(16000.0 / 50, i / (double)NB)).ToArray();
            var smooth = new double[NB];
            _abTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _abTimer.Tick += (s, e) =>
            {
                if (!win.IsVisible || win.WindowState == WindowState.Minimized) return;
                bool live = _abOut != null && AbProc.Active;
                state.Text = !AbWanted ? L.T("● wyłączony") : live ? "● " + AbProfileLabel(AbProfileName) + (AbProc.EqOn ? " + EQ" : "") : L.T("● czeka na odtwarzanie");
                state.Foreground = live ? acc2 : dim;
                src.Text = live ? L.T("Źródło: ") + (string.IsNullOrEmpty(_abOut.Source) ? L.T("odtwarzacz Velivo") : _abOut.Source) : AbWanted ? L.T("Włącz film, muzykę albo kanał TV w odtwarzaczu Velivo.") : L.T("Audio Balance i korektor są wyłączone – wybierz profil poniżej.");
                // FFT 2048 z ostatnich probek
                int pos = AbProc.ScopePos;
                for (int i = 0; i < 2048; i++) { fftRe[i] = live ? AbProc.Scope[(pos - 2048 + i + 8192) & 8191] * hann[i] : 0; fftIm[i] = 0; }
                AbFft(fftRe, fftIm);
                double w = spec.ActualWidth, h = spec.ActualHeight, bw = w / NB;
                double lowE = 0, midE = 0, hiE = 0;
                for (int b = 0; b < NB; b++)
                {
                    int k0 = Math.Max(1, (int)(edges[b] * 2048 / AbProcessor.Fs)), k1 = Math.Max(k0 + 1, (int)(edges[b + 1] * 2048 / AbProcessor.Fs));
                    double sum = 0; for (int k = k0; k < k1 && k < 1024; k++) sum += fftRe[k] * fftRe[k] + fftIm[k] * fftIm[k];
                    double db = 10 * Math.Log10(sum / 1024.0 + 1e-12) + 3;
                    double f = (edges[b] + edges[b + 1]) / 2; if (f < 250) lowE += sum; else if (f < 4000) midE += sum; else hiE += sum;
                    double v = Math.Max(0, Math.Min(1, (db + 70) / 70));
                    smooth[b] = v > smooth[b] ? v : smooth[b] * 0.85 + v * 0.15;
                    hold[b] = Math.Max(smooth[b], hold[b] - 0.01);
                    double bh = smooth[b] * h;
                    bars[b].Width = Math.Max(1, bw - 2); bars[b].Height = bh; Canvas.SetLeft(bars[b], b * bw + 1); Canvas.SetTop(bars[b], h - bh);
                    peaks[b].Width = Math.Max(1, bw - 2); Canvas.SetLeft(peaks[b], b * bw + 1); Canvas.SetTop(peaks[b], h - hold[b] * h - 2);
                }
                lmhBars[0].Value = Math.Max(-60, 10 * Math.Log10(lowE / 1024 + 1e-12)); lmhBars[1].Value = Math.Max(-60, 10 * Math.Log10(midE / 1024 + 1e-12)); lmhBars[2].Value = Math.Max(-60, 10 * Math.Log10(hiE / 1024 + 1e-12));
                string F(double x) => double.IsNegativeInfinity(x) || x < -69 || !live ? "–" : x.ToString("0.0");
                lvl.Value = live && AbProc.Out.Momentary > -40 ? AbProc.Out.Momentary : -40;
                vals[0].Text = F(AbProc.Out.Momentary); vals[1].Text = F(AbProc.Out.ShortTerm); vals[2].Text = F(AbProc.Out.Integrated); vals[3].Text = F(AbProc.PeakOutDb);
                vals[3].Foreground = live && AbProc.PeakOutDb > -1.05 ? Brushes.Gold : fg;
                if ((DateTime.UtcNow - hLast).TotalMilliseconds >= 100)
                {   // nowy punkt co 100 ms (blok pomiaru BS.1770)
                    hLast = DateTime.UtcNow;
                    Array.Copy(hIn, 1, hIn, 0, HN - 1); Array.Copy(hOut, 1, hOut, 0, HN - 1);
                    hIn[HN - 1] = live ? Math.Max(-70, AbProc.In.Momentary) : -70; hOut[HN - 1] = live ? Math.Max(-70, AbProc.Out.Momentary) : -70;
                    double hw = hist.ActualWidth, hh = hist.ActualHeight;
                    double Y(double v) => hh - 4 - (Math.Max(-60, Math.Min(0, v)) + 60) / 60 * (hh - 8);
                    var a1 = new PointCollection(HN); var a2 = new PointCollection(HN);
                    for (int i = 0; i < HN; i++) { double x = i * hw / (HN - 1); a1.Add(new Point(x, Y(hIn[i]))); a2.Add(new Point(x, Y(hOut[i]))); }
                    plIn.Points = a1; plOut.Points = a2;
                    double ty = Y(AbProc.Target); tl.X1 = 0; tl.X2 = hw; tl.Y1 = tl.Y2 = ty; tlTxt.Text = AbProc.Target.ToString("0") + " LUFS"; Canvas.SetLeft(tlTxt, 4); Canvas.SetTop(tlTxt, ty - 14);
                }
                gainTxt.Text = !live ? "" : (AbProc.Profile != null ? L.T("Audio Balance: wzmocnienie ") + AbProc.GainDb.ToString("+0.0;-0.0") + " dB" : L.T("Audio Balance: wyłączony (sam korektor)")) +
                    L.T(" · limiter ") + AbProc.LimitDb.ToString("0.0") + " dB" + L.T(" · wejście (3 s) ") + F(AbProc.In.ShortTerm) + " LUFS";
            };
            win.StateChanged += (s, e) => { if (win.WindowState == WindowState.Minimized) AbHideMonitor(); };
            win.IsVisibleChanged += (s, e) => { if (win.IsVisible) _abTimer.Start(); else _abTimer.Stop(); };
            win.Closed += (s, e) => { _abTimer.Stop(); _abTimer = null; _abWin = null; AbTrayRemove(); };
            win.Show(); _abTimer.Start();
        }

        static readonly float[] AbTargets = { -14, -16, -18, -20, -23, -24 };
        static string AbTargetLabel(float v) => v.ToString("0") + " LUFS" + (v == -14 ? L.T(" – głośno (serwisy muzyczne)") : v == -16 ? L.T(" – głośniej") : v == -18 ? L.T(" – zalecane do filmów") : v == -23 ? L.T(" – EBU R128 (telewizja)") : v == -24 ? L.T(" – ATSC A/85 (cicho)") : "");
        static string AbProfileTip(string p) => p == "light" ? L.T("Light – lekkie wyrównanie, zachowuje naturalną dynamikę filmu.") : p == "balanced" ? L.T("Balanced – wygodne oglądanie: cichsze dialogi wyżej, głośne sceny niżej.") : p == "night" ? L.T("Night – mocne ograniczenie dynamiki do cichego słuchania wieczorem.") : L.T("Off – bez wyrównywania (sam korektor, gdy włączony).");

        void AbSetProfile(string p)
        {
            SetPref("ab-profil", p); AbProc.Profile = AbProfile.ByName(p);
            AbNeedsRestartHint();
        }

        // wlaczanie i wylaczanie w trakcie odtwarzania: gdy dzwiek juz idzie przez Velivo - od razu (Off = bez zmian);
        // gdy jeszcze nie - przejecie dzwieku z VLC i ten sam film/kanal od tego samego miejsca (chwila ciszy, ok. 0,5 s)
        void AbNeedsRestartHint()
        {
            var mp = _playerMp;
            if (!AbWanted || _abOut != null || mp == null) return;
            bool live = !mp.IsSeekable; long t = mp.Time;
            if (!AbAttach(mp)) return;
            ShowToast(L.T("🎚 Audio Balance włączony"), null);
            System.Threading.Tasks.Task.Run(() =>
            {
                try { mp.Stop(); mp.Play(); } catch (Exception ex) { App.LogError(ex); }
                if (!live && t > 0) { Thread.Sleep(700); try { mp.Time = t; } catch (Exception) { } }
            });
        }

        void AbHideMonitor()
        {
            if (_abWin == null) return;
            _abWin.Hide(); _abTimer?.Stop();
            if (_abTray == null)
            {
                try
                {
                    _abTray = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath), Text = "Velivo – Audio Monitor", Visible = true };
                    var m = new System.Windows.Forms.ContextMenuStrip();
                    m.Items.Add(L.T("🎚 Otwórz Audio Monitor"), null, (s, e) => Dispatcher.Invoke(ShowAudioMonitor));
                    m.Items.Add(L.T("Ukryj widżet"), null, (s, e) => Dispatcher.Invoke(AbHideMonitor));
                    var prof = new System.Windows.Forms.ToolStripMenuItem(L.T("Profil Audio Balance"));
                    foreach (var p in AbProfiles) { var pp = p; prof.DropDownItems.Add(AbProfileLabel(p), null, (s, e) => Dispatcher.Invoke(() => AbSetProfile(pp))); }
                    prof.DropDownOpening += (s, e) => { foreach (System.Windows.Forms.ToolStripMenuItem it in prof.DropDownItems) it.Checked = it.Text == AbProfileLabel(AbProfileName); };
                    m.Items.Add(prof);
                    m.Items.Add(L.T("✕ Zamknij widżet"), null, (s, e) => Dispatcher.Invoke(() => _abWin?.Close()));
                    _abTray.ContextMenuStrip = m;
                    _abTray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(ShowAudioMonitor); };
                    _abTray.ShowBalloonTip(2500, "Audio Monitor", L.T("Widżet jest w zasobniku – kliknij ikonę, aby go przywrócić. Audio Balance działa dalej."), System.Windows.Forms.ToolTipIcon.Info);
                }
                catch (Exception ex) { App.LogError(ex); }
            }
        }

        void AbTrayRemove() { try { if (_abTray != null) { _abTray.Visible = false; _abTray.Dispose(); _abTray = null; } } catch (Exception) { } }

        // FFT radix-2 w miejscu (n = potega 2)
        static void AbFft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++) { int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); } }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                        double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                    }
                }
            }
        }
    }
}
