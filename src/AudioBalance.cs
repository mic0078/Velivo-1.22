using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        [StructLayout(LayoutKind.Sequential)] struct MmTime { public uint Type, Val, Pad; }
        [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr h, ref MmTime t, int size);
        long _written;   // ramki oddane karcie od ostatniego resetu
        uint _lastPos; long _posWraps;
        const int HdrSize = 48, FlagsOff = 24, WhdrDone = 1;   // WAVEHDR x64

        sealed class Chunk { public float[] Data; public int Frames; public long Pts; }
        sealed class Buf { public IntPtr Hdr, Mem; public int Cap, Frames; public bool Busy; }

        public readonly AbProcessor Proc;
        IntPtr _h; readonly List<Buf> _bufs = new List<Buf>();
        readonly Queue<Chunk> _q = new Queue<Chunk>(); readonly object _lk = new object();
        Thread _th; volatile bool _run, _paused; volatile int _flushReq;
        public volatile string Source = "";
        public long LateDrops, EarlyWaits;
        public volatile int AheadFrames;   // ile próbek czeka w karcie (przetworzone, jeszcze nie słychać) - wskaźniki są o tyle opóźniane

        readonly LibVLCSharp.Shared.LibVLC _vlc;
        public AbOutput(AbProcessor p, LibVLCSharp.Shared.LibVLC vlc) { Proc = p; _vlc = vlc; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WaveOutCaps { public ushort Mid, Pid; public uint DriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; public uint Support; }
        [DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveOutGetDevCapsW(UIntPtr id, ref WaveOutCaps caps, int size);
        // urzadzenia wyjscia dzwieku Windows (glosniki, sluchawki, Bluetooth, VB-CABLE…); nazwa ucieta przez Windows do 31 znakow
        public static List<string> Devices()
        {
            var l = new List<string>();
            try { int n = waveOutGetNumDevs(); for (int i = 0; i < n; i++) { var c = new WaveOutCaps(); if (waveOutGetDevCapsW((UIntPtr)i, ref c, Marshal.SizeOf<WaveOutCaps>()) == 0 && !string.IsNullOrEmpty(c.Name)) l.Add(c.Name); } } catch (Exception) { }
            return l;
        }

        public bool Open(string device)
        {
            var f = new WaveFormat { Tag = 1, Channels = 2, Rate = AbProcessor.Fs, Bits = 16, Align = 4, BytesPerSec = AbProcessor.Fs * 4 };
            uint id = 0xFFFFFFFF;   // domyslne urzadzenie Windows
            if (!string.IsNullOrEmpty(device)) { int k = Devices().IndexOf(device); if (k >= 0) id = (uint)k; }   // wybrane - gdy go nie ma (odlaczone), domyslne
            if (waveOutOpen(out _h, id, ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0 && (id == 0xFFFFFFFF || waveOutOpen(out _h, 0xFFFFFFFF, ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0)) { _h = IntPtr.Zero; return false; }
            _run = true; _th = new Thread(Loop) { IsBackground = true, Name = "Velivo Audio Balance", Priority = ThreadPriority.AboveNormal }; _th.Start();
            return true;
        }

        // wywolania VLC (watek dzwieku VLC) - tylko kopia, bez obliczen
        public void Play(IntPtr samples, uint count, long pts)
        {
            // VLC oddaje S16N (16 bit, stereo, przeplot) - 4 bajty na ramke
            var c = new Chunk { Frames = (int)count, Pts = pts, Data = new float[count * 2] };
            var raw = new short[count * 2]; Marshal.Copy(samples, raw, 0, raw.Length);
            for (int i = 0; i < raw.Length; i++) c.Data[i] = raw[i] / 32768f;
            lock (_lk) { _q.Enqueue(c); while (_q.Count > 400) _q.Dequeue(); Monitor.Pulse(_lk); }
        }
        public void Pause() { _paused = true; if (_h != IntPtr.Zero) waveOutPause(_h); }
        public void Resume() { _paused = false; if (_h != IntPtr.Zero) waveOutRestart(_h); lock (_lk) Monitor.Pulse(_lk); }
        public void Flush() { lock (_lk) { _q.Clear(); } Interlocked.Increment(ref _flushReq); lock (_lk) Monitor.Pulse(_lk); }

        long Now => _vlc.Clock;   // zegar libVLC (µs) - ten sam co pts

        // zwalnia odegrane bufory; zwraca dokladnie, ile ramek jeszcze nie zabrzmialo (oddane - pozycja karty). Wczesniej liczone calymi buforami:
        // grany bufor liczyl sie w calosci, a po jego koncu kolejka „spadala” o 20–80 ms - program wstawial wtedy cisze (zacinanie dzwieku)
        int QueuedFrames()
        {
            foreach (var b in _bufs) if (b.Busy && (Marshal.ReadInt32(b.Hdr, FlagsOff) & WhdrDone) != 0) { waveOutUnprepareHeader(_h, b.Hdr, HdrSize); b.Busy = false; }
            var t = new MmTime { Type = 2 };   // TIME_SAMPLES
            if (waveOutGetPosition(_h, ref t, 12) == 0 && t.Type == 2)
            {   // licznik karty ma 32 bity - po ok. 24,8 h odtwarzania bez przewijania przekreca sie; liczymy obroty
                if (t.Val < _lastPos) _posWraps++;
                _lastPos = t.Val;
                return (int)Math.Max(0, Math.Min(int.MaxValue, _written - ((long)_posWraps << 32 | t.Val)));
            }
            int n = 0; foreach (var b in _bufs) if (b.Busy) n += b.Frames; return n;
        }

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
                        flushSeen = _flushReq; waveOutReset(_h); _written = 0; _lastPos = 0; _posWraps = 0; QueuedFrames(); Proc.Reset();
                        if (_paused) waveOutPause(_h);
                    }
                    Chunk c = null;
                    lock (_lk) { if (_q.Count == 0 || _paused) { Monitor.Wait(_lk, 20); continue; } c = _q.Dequeue(); }
                    // kiedy zabrzmi pierwsza probka tego kawalka: po tym, co juz czeka w karcie, i po opoznieniu limitera (ok. 5 ms)
                    long playAt = QueuedFrames() * 1_000_000L / AbProcessor.Fs + AbProcessor.LatencyFrames * 1_000_000L / AbProcessor.Fs;
                    long gap = (c.Pts - Now) - playAt;   // > 0: VLC dal dzwiek wczesniej (zwykle) - cisza do jego chwili; < 0: spozniony
                    if (gap > 40000) { Interlocked.Increment(ref EarlyWaits); WriteFrames(null, (int)Math.Min(AbProcessor.Fs * 2L, gap * AbProcessor.Fs / 1_000_000L), ref pcm); }
                    else if (gap < -80000)
                    {   // spozniony o ponad 60 ms - pomijamy poczatek kawalka (obraz jest wazniejszy)
                        int skip = (int)Math.Min(c.Frames, -gap * AbProcessor.Fs / 1_000_000L); Interlocked.Increment(ref LateDrops);
                        if (skip >= c.Frames) continue;
                        var rest = new float[(c.Frames - skip) * 2]; Array.Copy(c.Data, skip * 2, rest, 0, rest.Length); c.Data = rest; c.Frames -= skip;
                    }
                    Proc.Process(c.Data, c.Frames);
                    WriteFrames(c.Data, c.Frames, ref pcm);
                    AheadFrames = QueuedFrames();
                    Proc.Active = true;
                }
                catch (Exception ex) { App.LogError(ex); Thread.Sleep(50); }
            }
        }

        // jeden bufor karty: probki (albo cisza, gdy data == null); karta gra bufory po kolei bez przerw
        void WriteFrames(float[] data, int frames, ref short[] pcm)
        {
            if (frames <= 0) return;
            if (pcm.Length < frames * 2) pcm = new short[frames * 2];
            if (data == null) Array.Clear(pcm, 0, frames * 2);
            else for (int i = 0; i < frames * 2; i++) { float v = data[i] * 32767f; pcm[i] = (short)(v > 32767 ? 32767 : v < -32768 ? -32768 : v); }
            var b = _bufs.FirstOrDefault(x => !x.Busy && x.Cap >= frames);
            if (b == null)
            {
                if (_bufs.Count > 200) { var old = _bufs.FirstOrDefault(x => !x.Busy); if (old != null) { Marshal.FreeHGlobal(old.Mem); Marshal.FreeHGlobal(old.Hdr); _bufs.Remove(old); } }
                b = new Buf { Cap = Math.Max(frames, 2048), Hdr = Marshal.AllocHGlobal(HdrSize) }; b.Mem = Marshal.AllocHGlobal(b.Cap * 4); _bufs.Add(b);
            }
            Marshal.Copy(pcm, 0, b.Mem, frames * 2);
            for (int o = 0; o < HdrSize; o += 8) Marshal.WriteInt64(b.Hdr, o, 0);
            Marshal.WriteIntPtr(b.Hdr, 0, b.Mem); Marshal.WriteInt32(b.Hdr, 8, frames * 4);
            b.Frames = frames; b.Busy = true;
            waveOutPrepareHeader(_h, b.Hdr, HdrSize); waveOutWrite(_h, b.Hdr, HdrSize); _written += frames;
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
        // dźwięk naprawdę płynie (nowe próbki w ostatnich 2 s) - pauza / koniec / zatrzymanie = wskaźniki na zero, nie ostatnia chwila
        static long _abFramesSeen; static DateTime _abFramesAt;
        internal static bool AbFlowing()
        {
            long f = Interlocked.Read(ref AbProc.Frames);
            if (f != _abFramesSeen) { _abFramesSeen = f; _abFramesAt = DateTime.UtcNow; }
            return (DateTime.UtcNow - _abFramesAt).TotalMilliseconds < 2000;   // VLC wysyła dźwięk paczkami - krótsze przerwy to nie pauza (było 0,5 s: monitor co chwilę gasł)
        }
        AbOutput _abOut;
        LibVLCSharp.Shared.MediaPlayer.LibVLCAudioPlayCb _abPlay; LibVLCSharp.Shared.MediaPlayer.LibVLCAudioPauseCb _abPause; LibVLCSharp.Shared.MediaPlayer.LibVLCAudioResumeCb _abResume;
        LibVLCSharp.Shared.MediaPlayer.LibVLCAudioFlushCb _abFlush; LibVLCSharp.Shared.MediaPlayer.LibVLCAudioDrainCb _abDrain; LibVLCSharp.Shared.MediaPlayer.LibVLCVolumeCb _abVol;

        static string AbProfileName => GetPref("ab-profil") ?? "off";
        static bool AbEqOn => GetPref("ab-eq") == "1";
        static int AbDialog => int.TryParse(GetPref("ab-dialogi"), out var d) ? d : 0;
        static bool AbBypass => GetPref("ab-wylacz") == "1";
        internal static bool AbWanted => !AbBypass && (AbProfileName != "off" || AbEqOn || AbDialog > 0);

        static void AbLoadSettings()
        {
            AbProc.Profile = AbProfile.ByName(AbProfileName, AbSilaName);
            if (GetPref("ab-cel-auto") != "1") { SetPref("ab-cel-auto", "1"); SetPref("ab-cel", "0"); }   // 2026-11-09: domyślnie Auto (właściciel), stałe poziomy do wyboru
            if (GetPref("ab-profil") is string op && op != "off" && op != "auto" && op != "limit")   // stare nazwy profili → tryb + intensywność
            { SetPref("ab-profil", op == "limiter" ? "limit" : "auto"); SetPref("ab-sila", op == "light" ? "light" : op == "night" ? "strong" : "normal"); }
            AbProc.Target = float.TryParse(GetPref("ab-cel"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0f;
            AbProc.EqOn = AbEqOn; AbProc.Dialog = AbDialog; AbProc.Bypass = AbBypass;
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
            if (!o.Open(GetPref("ab-wyjscie"))) { App.LogError(new Exception("Audio Balance: wyjście dźwięku Windows (waveOut) nie otworzyło się – zwykły dźwięk VLC")); return false; }
            _abOut = o;
            // kazdy odtwarzacz rozmawia tylko ze swoim wyjsciem - stary (zamykany w tle) nie wpisze dzwieku do nowego
            _abPlay = (d, s, c, p) => { if (_abOut == o) o.Play(s, c, p); };
            _abPause = (d, p) => { if (_abOut == o) o.Pause(); };
            _abResume = (d, p) => { if (_abOut == o) o.Resume(); };
            _abFlush = (d, p) => { if (_abOut == o) o.Flush(); };
            _abDrain = d => { };
            _abVol = (d, v, m) => AbProc.Volume = m ? 0 : v;
            mp.SetAudioFormat("S16N", AbProcessor.Fs, 2);   // VLC dekoduje i miksuje (5.1/AC3/E-AC3/AAC) do stereo 48 kHz; S16N - format pewny w libVLC 3 ("FL32" nie jest jego nazwą - VLC dawał inne próbki, niż czytaliśmy)
            mp.SetAudioCallbacks(_abPlay, _abPause, _abResume, _abFlush, _abDrain);
            mp.SetVolumeCallback(_abVol);
            AbProc.NewMedia();
            return true;
        }

        void AbDetach() { AbLearnEnd(); var o = _abOut; _abOut = null; o?.Dispose(); }
        internal void AbNewSource(string name, bool tv = false) { if (_abOut != null) { _abOut.Source = name ?? ""; AbLearnBegin(name, tv); AbLektorReset(); AbProc.NewMedia(); } }

        // ---------- 🎚 Audio Monitor: osobne okienko + wlasna ikonka w zasobniku ----------
        Window _abWin; System.Windows.Forms.NotifyIcon _abTray; DispatcherTimer _abTimer;
        static readonly string[] AbProfiles = { "off", "auto", "limit" };   // tryb: Wył. / Auto Level / Limit Only
        static readonly string[] AbSilas = { "light", "normal", "strong" };   // intensywność
        static string AbSilaName => GetPref("ab-sila") is string x && AbSilas.Contains(x) ? x : "normal";
        static string AbSilaLabel(string s) => s == "light" ? "Light" : s == "strong" ? "Strong" : "Normal";
        static string AbSilaTip(string s) => s == "light" ? L.T("Light – delikatna korekta.") : s == "strong" ? L.T("Strong – mocniejsze wyrównywanie, np. nocą.") : L.T("Normal – standardowe wyrównywanie.");
        void AbSetSila(string sila) { SetPref("ab-sila", sila); AbProc.Profile = AbProfile.ByName(AbProfileName, sila); }
        static string AbProfileLabel(string p) => p == "auto" ? "Auto Level" : p == "limit" ? "Limit Only" : L.T("Wył.");
        static string AbProfileShort(string p) => p == "auto" ? "A" + AbSilaLabel(AbSilaName)[0] : p == "limit" ? "Lim" + AbSilaLabel(AbSilaName)[0] : "⏻";

        internal void ShowAudioMonitor()
        {
            if (_abWin != null) { _abWin.Show(); if (_abWin.WindowState == WindowState.Minimized) _abWin.WindowState = WindowState.Normal; _abWin.Activate(); _abTimer?.Start(); return; }
            AbLoadSettings();
            var bg = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1F)); var fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF2)); var dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA6));
            var acc = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF)); var acc2 = new SolidColorBrush(Color.FromRgb(0x2E, 0xD3, 0xC6));
            var win = new Window { Title = "Velivo – Audio Monitor", Width = 1080, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.CanMinimize, Background = bg, Foreground = fg, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, Icon = Icon };
            var root = new StackPanel { Margin = new Thickness(12) };
            TextBlock T(string s, double size = 12, Brush c = null, FontWeight? w = null) => new TextBlock { Text = s, FontSize = size, Foreground = c ?? fg, FontWeight = w ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var state = T("", 12, acc2, FontWeights.SemiBold); DockPanel.SetDock(state, Dock.Right); head.Children.Add(state);
            head.Children.Add(T(L.T("🎚 Audio Monitor"), 15, fg, FontWeights.SemiBold));
            root.Children.Add(head);
            var src = T("", 11, dim); src.TextWrapping = TextWrapping.Wrap; src.TextTrimming = TextTrimming.CharacterEllipsis; src.Height = 30; root.Children.Add(src);   // stała wysokość (2 linie) - zmienny opis źródła nie przesuwa V i kafelków
            // 🗣 doradca lektora: wskaźnik słyszalności + propozycja do zatwierdzenia (sam niczego nie zmienia)
            var lekRow = new StackPanel { Margin = new Thickness(0, 4, 0, 2) };
            var lekTxt = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MinHeight = 34, ToolTip = L.T("Doradca lektora: porównuje lektora i film w paśmie zrozumiałości mowy (1–4 kHz), z uwzględnieniem ściszenia filmu i dialogów w filmie. Ocena z 10 ostatnich kwestii. Sam niczego nie zmienia.") };
            var sugTxt = new TextBlock { FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            var sugBtns = new WrapPanel();
            var sugBox = new Border { Background = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x33)), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed, Child = new StackPanel { Children = { sugTxt, sugBtns } } };
            lekRow.Children.Add(lekTxt); lekRow.Children.Add(sugBox); root.Children.Add(lekRow);
            // widmo (FFT z prawdziwych probek wyjscia)
            const int NB = 32;
            var spec = new Canvas { Height = 56, Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0D, 0x14)), Margin = new Thickness(0, 8, 0, 4), ClipToBounds = true };
            var bars = new Rectangle[NB]; var peaks = new Rectangle[NB]; var hold = new double[NB];
            var grad = new LinearGradientBrush(Color.FromRgb(0x2E, 0xD3, 0xC6), Color.FromRgb(0x7C, 0x5C, 0xFF), 90);
            for (int i = 0; i < NB; i++) { bars[i] = new Rectangle { Fill = grad, RadiusX = 1.5, RadiusY = 1.5 }; peaks[i] = new Rectangle { Fill = fg, Height = 2, Opacity = 0.8 }; spec.Children.Add(bars[i]); spec.Children.Add(peaks[i]); }
            root.Children.Add(spec);
            var axis = new Grid(); foreach (var (t, al) in new[] { ("50 Hz", HorizontalAlignment.Left), ("1 kHz", HorizontalAlignment.Center), ("16 kHz", HorizontalAlignment.Right) }) { var a = T(t, 10, dim); a.HorizontalAlignment = al; axis.Children.Add(a); }
            root.Children.Add(axis);
            var bigVu = AbVuPair(out var bigVuUpd); bigVu.Height = 170; bigVu.HorizontalAlignment = HorizontalAlignment.Center; bigVu.Margin = new Thickness(0, 8, 0, 0);
            bigVu.ToolTip = L.T("Lewe ramię V (jasny turkus) – Velivo podgłośniło, prawe (ciemny fiolet) – ściszyło; długość i liczba – ile dB. Puste V – bez zmiany względem źródła.");
            root.Children.Add(bigVu);
            var vuLeg = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            vuLeg.Children.Add(T(L.T("◀ podgłośnione   "), 10, new SolidColorBrush(Color.FromRgb(0x5F, 0xF2, 0xE4)))); vuLeg.Children.Add(T(L.T("puste V = bez zmiany   "), 10, dim)); vuLeg.Children.Add(T(L.T("ściszone ▶"), 10, new SolidColorBrush(Color.FromRgb(0x8B, 0x6B, 0xFF))));
            root.Children.Add(vuLeg);
            var nums = new Grid { Margin = new Thickness(0, 4, 0, 0) }; var vals = new TextBlock[4];
            var names = new[] { L.T("Chwilowa"), L.T("3 s"), L.T("Całość"), "True Peak" };
            var tips = new[] { L.T("Momentary LUFS – ostatnie 400 ms"), L.T("Short-term LUFS – ostatnie 3 s"), L.T("Integrated LUFS – od początku filmu/kanału (z bramkami BS.1770)"), L.T("Najwyższy szczyt między próbkami z ostatniej sekundy (dBTP)") };
            for (int i = 0; i < 4; i++)
            {
                nums.ColumnDefinitions.Add(new ColumnDefinition());
                var sp = new StackPanel { ToolTip = tips[i] }; sp.Children.Add(T(names[i], 10, dim)); vals[i] = T("–", 15, fg, FontWeights.SemiBold); System.Windows.Documents.Typography.SetNumeralAlignment(vals[i], FontNumeralAlignment.Tabular); sp.Children.Add(vals[i]);
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
            // szczegóły pracy procesora: stała siatka 3×2 (podpis nad wartością), cyfry równej szerokości, odświeżanie 4×/s - nie skacze i nie mruga
            var info = new Grid { Margin = new Thickness(0, 8, 0, 0) }; var infoV = new TextBlock[6]; var infoL = new TextBlock[6];
            var infoNames = new[] { L.T("Wzmocnienie"), L.T("Dialogi"), L.T("Limiter"), L.T("Wejście (3 s)"), L.T("Zgrywanie: cisza"), L.T("Pominięte") };
            for (int i = 0; i < 3; i++) info.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 2; i++) info.RowDefinitions.Add(new RowDefinition());
            for (int i = 0; i < 6; i++)
            {
                var sp = new StackPanel { Margin = new Thickness(0, 0, 6, 4) };
                infoL[i] = T(infoNames[i], 10, dim); infoV[i] = T("–", 12, fg, FontWeights.SemiBold);
                System.Windows.Documents.Typography.SetNumeralAlignment(infoV[i], FontNumeralAlignment.Tabular);
                sp.Children.Add(infoL[i]); sp.Children.Add(infoV[i]); Grid.SetColumn(sp, i % 3); Grid.SetRow(sp, i / 3); info.Children.Add(sp);
            }
            var bypassNote = T(L.T("Bez przetwarzania – dźwięk filmu bez zmian (pomiar działa dalej)"), 11, dim); bypassNote.Margin = new Thickness(0, 8, 0, 0);
            root.Children.Add(info); root.Children.Add(bypassNote);
            var infoLast = DateTime.MinValue; var numsLast = DateTime.MinValue;
            // profil i cel - zmiana od razu w glownym procesorze
            var prow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var pbtn = new Dictionary<string, Button>();
            Button Mk(string text, Action a) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 3, 10, 3), Foreground = fg, Background = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x33)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x52)) }; b.Click += (s, e) => a(); return b; }
            Action MarkSilaRef = null;
            void MarkProfile() { foreach (var kv in pbtn) kv.Value.Background = kv.Key == AbProfileName ? acc : new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x33)); MarkSilaRef?.Invoke(); }
            foreach (var p in AbProfiles) { var pp = p; var b = Mk(AbProfileLabel(p), () => { AbSetProfile(pp); MarkProfile(); }); b.ToolTip = AbProfileTip(p); pbtn[p] = b; prow.Children.Add(b); }
            MarkProfile();
            root.Children.Add(prow);
            var srow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) }; var sbtn = new Dictionary<string, Button>();
            srow.Children.Add(new TextBlock { Text = L.T("Intensywność:"), Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6) });
            void MarkSila() { bool on = AbProfileName != "off"; foreach (var kv in sbtn) { kv.Value.IsEnabled = on; kv.Value.Opacity = on ? 1 : 0.4; kv.Value.Background = on && kv.Key == AbSilaName ? acc : new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x33)); } }   // przy Wył. intensywność nieaktywna
            foreach (var sl0 in AbSilas) { var ss = sl0; var b = Mk(AbSilaLabel(sl0), () => { AbSetSila(ss); MarkSila(); }); b.ToolTip = AbSilaTip(sl0); sbtn[sl0] = b; srow.Children.Add(b); }
            MarkSila(); MarkSilaRef = MarkSila; root.Children.Add(srow);
            // 🗣 wyrazne dialogi (opcja dodatkowa) i ⏻ wylacz wszystko jednym przyciskiem
            var drow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            drow.Children.Add(new TextBlock { Text = L.T("🗣 Wyraźne dialogi:"), Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6) });
            var dbtn = new Button[3]; var dnames = new[] { L.T("Wył."), L.T("Średnio"), L.T("Mocno") };
            void MarkDialog() { for (int i = 0; i < 3; i++) dbtn[i].Background = i == AbDialog ? acc : new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x33)); }
            for (int i = 0; i < 3; i++) { int di = i; dbtn[i] = Mk(dnames[i], () => { SetPref("ab-dialogi", di.ToString()); AbProc.Dialog = di; MarkDialog(); AbNeedsRestartHint(); }); dbtn[i].ToolTip = L.T("Podbija mowę tylko wtedy, gdy przykrywa ją muzyka lub efekty. Reszta dźwięku i jej dynamika zostają bez zmian."); drow.Children.Add(dbtn[i]); }
            MarkDialog(); root.Children.Add(drow);
            var bypassBtn = Mk("", () => { });
            void MarkBypass() { bypassBtn.Content = AbBypass ? L.T("⏻ Przetwarzanie WYŁĄCZONE – kliknij, aby włączyć") : !AbWanted ? L.T("⏻ Nic nie jest włączone – kliknij, aby włączyć (Balanced)") : L.T("⏻ Wyłącz całe przetwarzanie"); bypassBtn.Background = AbBypass ? new SolidColorBrush(Color.FromRgb(0x8A, 0x2B, 0x2B)) : new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x33)); }
            bypassBtn.Click += (s, e) => { if (!AbBypass && !AbWanted) { AbSetProfile("auto"); MarkProfile(); } else AbSetBypass(!AbBypass); MarkBypass(); };
            bypassBtn.ToolTip = L.T("Jednym kliknięciem: dźwięk filmu bez żadnych zmian (Audio Balance, korektor, dialogi). Ustawienia zostają.");
            MarkBypass(); root.Children.Insert(2, bypassBtn);
            // 🧠 nauka: włącz/wyłącz i „zapomnij”
            var lrow = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
            var lOn = new CheckBox { Content = L.T("🧠 Ucz się sam (każdy kanał i film osobno)"), IsChecked = AbLearnOn, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6), ToolTip = L.T("Velivo zapamiętuje dla każdego kanału TV i filmu: poziom wyrównania (start bez czekania), głośne reklamy (ściszanie szybsze) i Twoje poprawki głośności (cel tego źródła). Bez AI – sama pamięć pomiarów.") };
            lOn.Click += (s2, e2) => { SetPref("ab-nauka", lOn.IsChecked == true ? "1" : "0"); if (lOn.IsChecked != true) { AbProc.SeedLevDb = float.NaN; AbProc.FastMul = 1; AbProc.TargetAdj = 0; } };
            lrow.Children.Add(lOn);
            var lForget = Mk(L.T("🧹 Zapomnij, czego się nauczyłeś"), () => { });
            lForget.Click += (s2, e2) => { if (AskYes(win, L.T("Usunąć wszystko, czego Audio Balance nauczył się o kanałach i filmach (") + AbLearnCount + ")?", L.T("Usuń"), L.T("Anuluj"))) { AbLearnForget(); ShowToast(L.T("🧠 Pamięć Audio Balance wyczyszczona"), null); } };
            lForget.ToolTip = L.T("Kasuje plik audio-nauka.json – Velivo zacznie uczyć się od nowa.");
            lrow.Children.Add(lForget); root.Children.Add(lrow);
            // najwazniejsze na gorze: ZRODLO (ile ma oryginal) → ZMIANA (ile podglosnil / sciszyl) → WYNIK (co slychac); glosnosc z 3 s, 4×/s
            var sum3 = new Grid { Margin = new Thickness(0, 8, 0, 4) }; var sumV = new TextBlock[3]; var sumL = new TextBlock[3];
            for (int i = 0; i < 3; i++)
            {
                sum3.ColumnDefinitions.Add(new ColumnDefinition());
                var bx = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x2B)), CornerRadius = new CornerRadius(6), Margin = new Thickness(i == 0 ? 0 : 3, 0, i == 2 ? 0 : 3, 0), Padding = new Thickness(6, 4, 6, 4) };
                var sp = new StackPanel(); sumL[i] = T(new[] { L.T("ŹRÓDŁO"), L.T("ZMIANA"), L.T("WYNIK") }[i], 10, dim); sumV[i] = T("–", 17, fg, FontWeights.Bold);
                System.Windows.Documents.Typography.SetNumeralAlignment(sumV[i], FontNumeralAlignment.Tabular);
                sp.Children.Add(sumL[i]); sp.Children.Add(new Viewbox { Child = sumV[i], StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, Height = 24 });   // długa wartość się zmniejsza zamiast łamać (kafelek nie rośnie - nic pod nim nie skacze) bx.Child = sp; Grid.SetColumn(bx, i); sum3.Children.Add(bx);
            }
            sum3.ToolTip = L.T("Źródło – jak głośny jest oryginał (ostatnie 10 s, bez ciszy – jak pomiar zintegrowany). Zmiana – ile Velivo podgłośniło (▲) albo ściszyło (▼). Wynik – co słyszysz; powinien być przy celu, który ustawiasz niżej.");
            root.Children.Insert(3, sum3);
            var tgt = new ComboBox { Width = 330, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = L.T("Poziom docelowy – do jakiej głośności Velivo wyrównuje dźwięk. Wybierz sam: głośniej (-14) albo ciszej (-23).") };
            foreach (var v in AbTargets) tgt.Items.Add(new ComboBoxItem { Content = AbTargetLabel(v), Tag = v });
            tgt.SelectedItem = tgt.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Math.Abs((float)i.Tag - AbProc.Target) < 0.1) ?? tgt.Items[0];
            tgt.SelectionChanged += (s, e) => { if (tgt.SelectedItem is ComboBoxItem it) { AbProc.Target = (float)it.Tag; SetPref("ab-cel", ((float)it.Tag).ToString(System.Globalization.CultureInfo.InvariantCulture)); } };
            root.Children.Add(tgt);
            var devBox = AbDeviceBox(); root.Children.Add(devBox);
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
            brow.Children.Add(Mk(L.T("📌 Na pulpit"), () => { ShowAbDesk(); AbHideMonitor(); }));
            brow.Children.Add(Mk(L.T("⤵ Do zasobnika"), () => AbHideMonitor()));
            brow.Children.Add(Mk(L.T("✕ Zamknij widżet"), AbCloseMonitor));
            root.Children.Add(brow);
            var foot = T(L.T("Ukrycie lub zamknięcie widżetu nie wyłącza Audio Balance – dźwięk dalej jest wyrównywany."), 10, dim); root.Children.Add(foot);
            // układ „Kokpit” (wybór właściciela, makieta A): 2 kolumny bez pustych miejsc - lewa: stan na żywo (V obok kafelków, lektor, wykres, liczby);
            // prawa: ustawienia, korektor, widmo, przyciski; każdy element root musi trafić do kolumny (Move)
            var cols = new Grid { Margin = new Thickness(12) };
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) }); cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var c1 = new StackPanel { Margin = new Thickness(0, 0, 10, 0) }; var c3 = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            void Move(Panel to, params UIElement[] els) { foreach (var el in els) { root.Children.Remove(el); to.Children.Add(el); } }
            Move(c1, head, src);
            var live = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            live.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); live.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var vCol = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 0) }; var tCol = new StackPanel();
            bigVu.Height = 130; bigVu.Margin = new Thickness(0);
            Move(vCol, bigVu, vuLeg); Move(tCol, sum3, lekRow);
            Grid.SetColumn(tCol, 1); live.Children.Add(vCol); live.Children.Add(tCol); c1.Children.Add(live);
            Move(c1, leg, hist, nums, info, bypassNote);   // paski LOW/MID/HIGH i głośności usunięte (właściciel) - zostaje widmo
            Move(c3, prow, srow, drow, tgt, devBox, lrow, eqExp, spec, axis, bypassBtn, brow, foot);
            Grid.SetColumn(c1, 0); Grid.SetColumn(c3, 1); cols.Children.Add(c1); cols.Children.Add(c3);
            win.Content = new Border { Background = bg, Child = cols };   // ciemne tlo zawsze (motyw okien Velivo moze ustawic jasne tlo okna)
            win.Loaded += (s, e) => win.Background = bg;   // po motywie okien Velivo (ustawia tlo przy wczytaniu)
            _abWin = win;

            // odswiezanie ~30 kl./s tylko, gdy widzet widoczny (FFT w watku okna, nie dzwieku)
            var fftRe = new double[2048]; var fftIm = new double[2048]; var hann = Enumerable.Range(0, 2048).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 2047)).ToArray();
            var edges = Enumerable.Range(0, NB + 1).Select(i => 50 * Math.Pow(16000.0 / 50, i / (double)NB)).ToArray();
            var smooth = new double[NB]; var holdT = new int[NB];
            string uiSig = null;
            _abTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(20) };   // 50 kl./s - słupki reagują płynnie
            _abTimer.Tick += (s, e) =>
            {
                if (!win.IsVisible || win.WindowState == WindowState.Minimized) return;
                bool live = _abOut != null && AbProc.Active && AbFlowing();
                bigVuUpd(live);
                string sig = AbBypass + AbProfileName + AbSilaName + AbDialog + AbEqOn;
                if (sig != uiSig) { uiSig = sig; MarkBypass(); MarkProfile(); MarkSila(); MarkDialog(); }
                state.Text = AbBypass ? L.T("● bez przetwarzania") : !AbWanted ? L.T("● wyłączony") : live ? "● " + AbProfileLabel(AbProfileName) + (AbProfileName != "off" ? " " + AbSilaLabel(AbSilaName) : "") + (AbProc.EqOn ? " + EQ" : "") + (AbProc.Dialog > 0 ? L.T(" + dialogi") : "") : L.T("● czeka na odtwarzanie");
                state.Foreground = live ? acc2 : dim;
                src.Text = live ? L.T("Źródło: ") + (string.IsNullOrEmpty(_abOut.Source) ? L.T("odtwarzacz Velivo") : _abOut.Source) + (AbLearnInfo(_abOut.Source) is string li && li.Length > 0 ? "   " + li : "") : AbWanted ? L.T("Włącz film, muzykę albo kanał TV w odtwarzaczu Velivo.") : L.T("Audio Balance i korektor są wyłączone – wybierz profil poniżej.");
                if (src.ToolTip as string != src.Text) src.ToolTip = src.Text;   // pełny opis po najechaniu (w oknie najwyżej 2 linie)
                { var (lt, lc0) = AbLektorStatus(); if (lekTxt.Text != lt) { lekTxt.Text = lt; lekTxt.Foreground = new SolidColorBrush(lc0); } }
                if ((AbLekSugText ?? "") != (string)(sugBox.Tag ?? ""))
                {   // propozycja doradcy: Ty decydujesz (Zastosuj / Nie teraz)
                    sugBox.Tag = AbLekSugText ?? ""; sugBox.Visibility = AbLekSugText == null ? Visibility.Collapsed : Visibility.Visible;
                    sugTxt.Text = "💡 " + (AbLekSugText ?? ""); sugBtns.Children.Clear();
                    if (AbLekSugText != null)
                    {
                        bool canApply = AbLekSugApply != null;
                        var ok = Mk(canApply ? L.T("✔ Zastosuj") : L.T("OK"), () => AbLektorSugAccept()); ok.ToolTip = canApply ? L.T("Zmienia ustawienie lektora tak, jak proponuje doradca. Zawsze możesz wrócić suwakiem w panelu lektora.") : null;
                        sugBtns.Children.Add(ok);
                        if (canApply) { var no = Mk(L.T("Nie teraz"), AbLektorSugDismiss); no.ToolTip = L.T("Ta propozycja nie wróci do końca tego uruchomienia Velivo."); sugBtns.Children.Add(no); }
                    }
                }
                // FFT 2048 z ostatnich probek
                int ahead = Math.Min(60000, _abOut?.AheadFrames ?? 0), pos = AbProc.ScopePos - ahead;   // fragment, który właśnie słychać (za kolejką karty)
                for (int i = 0; i < 2048; i++) { fftRe[i] = live ? AbProc.Scope[(pos - 2048 + i + 131072) & 65535] * hann[i] : 0; fftIm[i] = 0; }
                AbFft(fftRe, fftIm);
                double w = spec.ActualWidth, h = spec.ActualHeight, bw = w / NB;
                for (int b = 0; b < NB; b++)
                {
                    int k0 = Math.Max(1, (int)(edges[b] * 2048 / AbProcessor.Fs)), k1 = Math.Max(k0 + 1, (int)(edges[b + 1] * 2048 / AbProcessor.Fs));
                    double mx = 0; for (int k = k0; k < k1 && k < 1024; k++) mx = Math.Max(mx, fftRe[k] * fftRe[k] + fftIm[k] * fftIm[k]);   // najsilniejszy ton w paśmie (jak Winamp / foobar2000)
                    double db = 20 * Math.Log10(2 * Math.Sqrt(mx) / 1024.0 + 1e-9);   // dBFS (pełny sinus = 0 dB; okno Hanna)
                    double v = double.IsNaN(db) ? 0 : Math.Max(0, Math.Min(1, (db + 72) / 60));   // stała skala -72…-12 dBFS (sprawdzone na dźwięku z nagrania właściciela) - ten sam dźwięk zawsze wygląda tak samo
                    // jak analizator: w górę od razu, w dół szybko i równo (3 dB na klatkę), szczyt trzymany 0,5 s, potem opada
                    smooth[b] = v > smooth[b] ? v : Math.Max(v, smooth[b] - 0.05);
                    if (smooth[b] >= hold[b]) { hold[b] = smooth[b]; holdT[b] = 25; } else if (holdT[b] > 0) holdT[b]--; else hold[b] = Math.Max(smooth[b], hold[b] - 0.015);
                    double bh = smooth[b] * h;
                    bars[b].Width = Math.Max(1, bw - 2); bars[b].Height = bh; Canvas.SetLeft(bars[b], b * bw + 1); Canvas.SetTop(bars[b], h - bh);
                    peaks[b].Width = Math.Max(1, bw - 2); Canvas.SetLeft(peaks[b], b * bw + 1); Canvas.SetTop(peaks[b], h - hold[b] * h - 2);
                }
                string F(double x) => double.IsNegativeInfinity(x) || x < -69 || !live ? "–" : x.ToString("0.0");
                if ((DateTime.UtcNow - numsLast).TotalMilliseconds >= 250)
                {   // liczby 4×/s - przy 30/s nie da się ich przeczytać
                    numsLast = DateTime.UtcNow;
                    {
                        double si = AbProc.Raw.Gated10 > double.NegativeInfinity ? AbProc.Raw.Gated10 : AbProc.Raw.ShortTerm, so = AbProc.Out.Gated10 > double.NegativeInfinity ? AbProc.Out.Gated10 : AbProc.Out.ShortTerm, dd = so - si; bool okd = live && !double.IsInfinity(dd) && !double.IsNaN(dd) && si > -69;
                        sumV[0].Text = F(si) + " LUFS"; sumV[0].Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x53));
                        sumL[1].Text = !okd || Math.Abs(dd) < 0.25 ? L.T("ZMIANA") : dd > 0 ? L.T("PODGŁOŚNIONO") : L.T("ŚCISZONO");
                        sumV[1].Text = !okd ? "–" : Math.Abs(dd) < 0.25 ? "±0 dB" : (dd > 0 ? "▲ +" : "▼ ") + dd.ToString("0.0") + " dB";
                        sumV[1].Foreground = !okd || Math.Abs(dd) < 0.25 ? fg : dd > 0 ? new SolidColorBrush(Color.FromRgb(0x5F, 0xF2, 0xE4)) : new SolidColorBrush(Color.FromRgb(0x8B, 0x6B, 0xFF));
                        sumV[2].Text = F(so) + " LUFS"; sumV[2].Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x5A));
                    }
                    vals[0].Text = F(AbProc.Out.Momentary); vals[1].Text = F(AbProc.Out.ShortTerm); vals[2].Text = F(AbProc.Out.Integrated); vals[3].Text = F(AbProc.PeakOutDb);
                    vals[3].Foreground = live && AbProc.PeakOutDb > -1.05 ? Brushes.Gold : fg;
                }
                if ((DateTime.UtcNow - hLast).TotalMilliseconds >= 100)
                {   // nowy punkt co 100 ms (blok pomiaru BS.1770)
                    hLast = DateTime.UtcNow;
                    Array.Copy(hIn, 1, hIn, 0, HN - 1); Array.Copy(hOut, 1, hOut, 0, HN - 1);
                    var hs = AbProc.Delayed(ahead); hIn[HN - 1] = live ? Math.Max(-70, hs.RawM) : -70; hOut[HN - 1] = live ? Math.Max(-70, hs.OutM) : -70;   // wykres też za kolejką karty - to, co słychać
                    double hw = hist.ActualWidth, hh = hist.ActualHeight;
                    double Y(double v) => hh - 4 - (Math.Max(-60, Math.Min(0, v)) + 60) / 60 * (hh - 8);
                    var a1 = new PointCollection(HN); var a2 = new PointCollection(HN);
                    for (int i = 0; i < HN; i++) { double x = i * hw / (HN - 1); a1.Add(new Point(x, Y(hIn[i]))); a2.Add(new Point(x, Y(hOut[i]))); }
                    plIn.Points = a1; plOut.Points = a2;
                    double ty = Y(AbProc.EffTarget); tl.X1 = 0; tl.X2 = hw; tl.Y1 = tl.Y2 = ty; tlTxt.Text = (AbProc.Target == 0 ? L.T("Auto ") : "") + AbProc.EffTarget.ToString("0") + " LUFS"; Canvas.SetLeft(tlTxt, 4); Canvas.SetTop(tlTxt, ty - 14);
                }
                bool byp = live && AbBypass;
                info.Visibility = live && !byp ? Visibility.Visible : Visibility.Collapsed; bypassNote.Visibility = byp ? Visibility.Visible : Visibility.Collapsed;
                if (live && !byp && (DateTime.UtcNow - infoLast).TotalMilliseconds >= 250)
                {
                    infoLast = DateTime.UtcNow;
                    infoV[0].Text = AbProc.Profile != null ? (Math.Abs(AbProc.GainDb) < 0.05 ? "±0,0" : AbProc.GainDb.ToString("+0.0;-0.0")) + " dB" : L.T("wył.");
                    infoV[1].Text = AbProc.Dialog > 0 ? (Math.Abs(AbProc.DialogDb) < 0.05 ? "±0,0" : AbProc.DialogDb.ToString("+0.0;-0.0")) + " dB" : L.T("wył.");
                    infoV[2].Text = AbProc.LimitDb.ToString("0.0") + " dB";
                    infoV[3].Text = F(AbProc.Raw.ShortTerm) + " LUFS";
                    infoV[4].Text = Interlocked.Read(ref _abOut.EarlyWaits).ToString();
                    infoV[5].Text = Interlocked.Read(ref _abOut.LateDrops).ToString();
                }
            };
            win.StateChanged += (s, e) => { if (win.WindowState == WindowState.Minimized) AbHideMonitor(); };
            win.IsVisibleChanged += (s, e) => { if (win.IsVisible) _abTimer.Start(); else _abTimer.Stop(); };
            // ✕ na pasku okna = do zasobnika (jak minimalizacja); calkowite zamkniecie tylko przyciskiem „Zamknij widżet” albo z menu zasobnika
            win.Closing += (s, e) => { if (!_abCloseReal && !_mainClosing && !_reallyExit) { e.Cancel = true; AbHideMonitor(); } };
            win.Closed += (s, e) => { _abCloseReal = false; _abTimer.Stop(); _abTimer = null; _abWin = null; AbTrayRemove(); };
            win.Show(); _abTimer.Start();
        }

        static readonly float[] AbTargets = { 0, -14, -16, -18, -20, -23, -24 };   // 0 = Auto (średnia źródła)
        static string AbTargetLabel(float v) => v == 0 ? L.T("Auto – inteligentnie (odniesienie -23 LUFS, zalecane)") : v.ToString("0") + " LUFS" + (v == -14 ? L.T(" – głośno (serwisy muzyczne)") : v == -16 ? L.T(" – głośniej") : v == -18 ? L.T(" – zalecane do filmów") : v == -23 ? L.T(" – EBU R128 (telewizja)") : v == -24 ? L.T(" – ATSC A/85 (cicho)") : "");
        static string AbProfileTip(string p) => p == "auto" ? L.T("Auto Level – wyrównywanie odczuwalnej głośności.") : p == "limit" ? L.T("Limit Only – ograniczanie głośnych szczytów bez podbijania cichych fragmentów.") : L.T("Wył. – oryginalny dźwięk bez ingerencji.");

        internal void AbSetBypass(bool on)
        {
            SetPref("ab-wylacz", on ? "1" : "0"); AbProc.Bypass = on;
            ShowToast(on ? L.T("⏻ Przetwarzanie dźwięku wyłączone – film gra bez zmian") : L.T("🎚 Przetwarzanie dźwięku włączone"), null);
            AbNeedsRestartHint();
        }

        // wybor urzadzenia wyjscia: od razu (przelaczenie w trakcie filmu - chwila ciszy, ten sam film od tego miejsca)
        internal void AbSetDevice(string name)
        {
            SetPref("ab-wyjscie", name ?? "");
            if (_abOut != null && _playerMp != null) { AbDetach(); AbNeedsRestartHint(); }
        }

        ComboBox AbDeviceBox()
        {
            var box = new ComboBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
                ToolTip = L.T("Gdzie gra dźwięk filmu przy Audio Balance / korektorze: głośniki, słuchawki, Bluetooth (także VB-CABLE, gdy jest zainstalowany).") };
            box.Items.Add(new ComboBoxItem { Content = L.T("🔈 Domyślne urządzenie Windows"), Tag = "" });
            var cur = GetPref("ab-wyjscie") ?? "";
            foreach (var d in AbOutput.Devices()) box.Items.Add(new ComboBoxItem { Content = "🔈 " + d, Tag = d });
            if (cur.Length > 0 && !box.Items.Cast<ComboBoxItem>().Any(i => (string)i.Tag == cur)) box.Items.Add(new ComboBoxItem { Content = "🔈 " + cur + L.T(" (odłączone)"), Tag = cur });
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == cur) ?? box.Items[0];
            box.SelectionChanged += (a, b) => { if (box.SelectedItem is ComboBoxItem it) AbSetDevice((string)it.Tag); };
            return box;
        }

        void AbSetProfile(string p)
        {
            SetPref("ab-profil", p); AbProc.Profile = AbProfile.ByName(p, AbSilaName);
            if (p == "off" && (AbEqOn || AbDialog > 0) && !AbBypass) ShowToast(L.T("🎚 Wyrównywanie głośności wyłączone. Korektor / dialogi działają dalej – całość wyłączysz przyciskiem ⏻."), null);   // Off = tylko profil; mylilo sie z wylaczeniem wszystkiego
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

        bool _abCloseReal;
        void AbCloseMonitor() { if (_abWin == null) { AbTrayRemove(); return; } _abCloseReal = true; _abWin.Close(); }

        void AbHideMonitor()
        {
            if (_abWin == null) return;
            _abWin.Hide(); _abTimer?.Stop();
            if (_abTray == null)
            {
                try
                {
                    _abVFrames ??= Enumerable.Range(0, 9).Select(i => AbVIcon(i / 8.0)).ToArray();
                    _abTray = new System.Windows.Forms.NotifyIcon { Icon = _abVFrames[0], Text = "Velivo – Audio Monitor", Visible = true };
                    // animowane V: wypelnienie litery rosnie z glosnoscia tego, co slychac (10 kl./s, tylko gdy ikonka jest w zasobniku)
                    _abTrayAnim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    int lastF = -1;
                    _abTrayAnim.Tick += (s2, e2) =>
                    {
                        if (_abTray == null) { _abTrayAnim.Stop(); return; }
                        double m = _abOut != null && AbProc.Active && AbFlowing() && !AbBypass ? AbProc.Out.Momentary : double.NegativeInfinity;
                        int fr = double.IsNegativeInfinity(m) || double.IsNaN(m) ? 0 : 1 + (int)Math.Round(Math.Max(0, Math.Min(1, (m + 45) / 40)) * 7);
                        if (fr != lastF) { lastF = fr; try { _abTray.Icon = _abVFrames[fr]; } catch (Exception) { } }
                    };
                    _abTrayAnim.Start();
                    var m = new System.Windows.Forms.ContextMenuStrip();
                    m.Items.Add(L.T("🎚 Otwórz Audio Monitor"), null, (s, e) => Dispatcher.Invoke(ShowAudioMonitor));
                    m.Items.Add(L.T("Ukryj widżet"), null, (s, e) => Dispatcher.Invoke(AbHideMonitor));
                    m.Items.Add(L.T("📌 Mały widżet na pulpicie"), null, (s, e) => Dispatcher.Invoke(ShowAbDesk));
                    var prof = new System.Windows.Forms.ToolStripMenuItem(L.T("Tryb Audio Balance"));
                    foreach (var p in AbProfiles) { var pp = p; prof.DropDownItems.Add(AbProfileLabel(p), null, (s, e) => Dispatcher.Invoke(() => { if (AbBypass) AbSetBypass(false); AbSetProfile(pp); })); }
                    prof.DropDownOpening += (s, e) => { foreach (System.Windows.Forms.ToolStripMenuItem it in prof.DropDownItems) it.Checked = it.Text == AbProfileLabel(AbProfileName); };
                    m.Items.Add(prof);
                    var silM = new System.Windows.Forms.ToolStripMenuItem(L.T("Intensywność"));
                    foreach (var sl in AbSilas) { var ss = sl; silM.DropDownItems.Add(AbSilaLabel(sl), null, (s, e) => Dispatcher.Invoke(() => AbSetSila(ss))); }
                    silM.DropDownOpening += (s, e) => { foreach (System.Windows.Forms.ToolStripMenuItem it in silM.DropDownItems) { it.Checked = it.Text == AbSilaLabel(AbSilaName); it.Enabled = AbProfileName != "off"; } };
                    m.Items.Add(silM);
                    var byp = new System.Windows.Forms.ToolStripMenuItem(L.T("⏻ Bez przetwarzania dźwięku"));
                    byp.Click += (s, e) => Dispatcher.Invoke(() => AbSetBypass(!AbBypass));
                    m.Opening += (s, e) => byp.Checked = AbBypass;
                    m.Items.Add(byp);
                    m.Items.Add(L.T("✕ Zamknij widżet"), null, (s, e) => Dispatcher.Invoke(AbCloseMonitor));
                    _abTray.ContextMenuStrip = m;
                    _abTray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(ShowAudioMonitor); };
                    _abTray.ShowBalloonTip(2500, "Audio Monitor", L.T("Widżet jest w zasobniku – kliknij ikonę, aby go przywrócić. Audio Balance działa dalej."), System.Windows.Forms.ToolTipIcon.Info);
                }
                catch (Exception ex) { App.LogError(ex); }
            }
        }

        // 📌 maly widzet na pulpicie: widmo + LUFS + profil, bez ramki, pod innymi oknami (jak gadzet), przeciaganie mysza,
        // dwuklik = pelny Audio Monitor, prawy przycisk = menu; miejsce zapamietane
        Window _abDesk, _abDeskV;
        // onlyV = drugi, najmniejszy widzet: samo „V” (bez napisow) - te same mozliwosci (menu, na wierzchu, przezroczystosc, przeciaganie)
        internal void ShowAbDesk(bool onlyV = false)
        {
            var cur0 = onlyV ? _abDeskV : _abDesk;
            if (cur0 != null) { cur0.Show(); cur0.Activate(); return; }
            // na pulpicie zawsze jeden widżet: pokazanie jednego zamienia drugi (miejsce przechodzi na nowy, jeśli nie ma własnego)
            var other = onlyV ? _abDesk : _abDeskV; double? swapL = other?.Left, swapT = other?.Top; other?.Close();
            string key = onlyV ? "ab-pulpitv" : "ab-pulpit";
            var fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF2)); var dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA6));
            var w = new Window { Title = onlyV ? "Velivo – V (pulpit)" : "Velivo – Audio Monitor (pulpit)", Width = onlyV ? 66 : 156, Height = onlyV ? 62 : 78, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = false };
            double l0, t0;
            if (swapL.HasValue && swapT.HasValue) { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = swapL.Value; w.Top = swapT.Value; SetPref(key + "-x", swapL.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); SetPref(key + "-y", swapT.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }   // zamiana - w tym samym miejscu
            else if (double.TryParse(GetPref(key + "-x"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out l0) && double.TryParse(GetPref(key + "-y"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t0))
            { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = l0; w.Top = t0; }
            else { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = SystemParameters.WorkArea.Right - (onlyV ? 90 : 170); w.Top = SystemParameters.WorkArea.Top + 20; }
            var g = new Grid { Margin = new Thickness(8, 4, 8, 4) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); g.RowDefinitions.Add(new RowDefinition()); g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var top = new DockPanel(); var st = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0xD3, 0xC6)), FontWeight = FontWeights.SemiBold }; DockPanel.SetDock(st, Dock.Right); top.Children.Add(st);
            var lufs = new TextBlock { FontSize = 10, Foreground = fg, FontWeight = FontWeights.SemiBold }; top.Children.Add(lufs); g.Children.Add(top);
            // dwa wskazniki wskazowkowe VU (jak w magnetofonie/wzmacniaczu): LEFT i RIGHT, skala -20..+3 VU (0 VU = -18 dBFS RMS), czerwone pole powyzej 0;
            // na dole: oryginal w LUFS i ile Velivo dodalo (▲ zielone) albo odjelo (▼ pomaranczowe) decybeli
            var green = new SolidColorBrush(Color.FromRgb(0x5F, 0xF2, 0xE4)); var orange = new SolidColorBrush(Color.FromRgb(0x8B, 0x6B, 0xFF));   // kolory Velivo: turkus = glosniej, fiolet = ciszej (napis - jasniejszy fiolet)
            var vuPair = AbVuPair(out var vuUpd, !onlyV); vuPair.Margin = new Thickness(0, 2, 0, 2); Grid.SetRow(vuPair, 1); g.Children.Add(vuPair);
            var bottom = new DockPanel(); Grid.SetRow(bottom, 2); g.Children.Add(bottom);
            var delta = new TextBlock { FontSize = 10, FontWeight = FontWeights.SemiBold }; DockPanel.SetDock(delta, Dock.Right); bottom.Children.Add(delta);
            var orig = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x53)) }; bottom.Children.Add(orig);
            System.Windows.Documents.Typography.SetNumeralAlignment(delta, FontNumeralAlignment.Tabular); System.Windows.Documents.Typography.SetNumeralAlignment(orig, FontNumeralAlignment.Tabular);
            var numsAt = DateTime.MinValue;
            if (onlyV) { top.Visibility = Visibility.Collapsed; bottom.Visibility = Visibility.Collapsed; g.Margin = new Thickness(4); }
            w.Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x16, 0x1F)), CornerRadius = new CornerRadius(10), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x7C, 0x5C, 0xFF)), BorderThickness = new Thickness(1), Child = g,
};
            w.MouseLeftButtonDown += (s, e) => { if (!(e.OriginalSource is Visual ov) || !w.IsAncestorOf(ov)) return;   // klik w menu (prawy przycisk) przychodzi tu z okienka menu - przeciaganie zabieralo mu klikniecie
                if (e.ClickCount == 2) ShowAudioMonitor(); else try { w.DragMove(); SetPref(key + "-x", w.Left.ToString(System.Globalization.CultureInfo.InvariantCulture)); SetPref(key + "-y", w.Top.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch (Exception) { } };
            var cm = new ContextMenu();
            MenuItem Item(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (s, e) => a(); cm.Items.Add(mi); return mi; }
            Item(L.T("🎚 Otwórz Audio Monitor"), ShowAudioMonitor);
            var profItems = new Dictionary<string, MenuItem>();
            foreach (var p in AbProfiles) { var pp = p; profItems[p] = Item(L.T("Tryb: ") + AbProfileLabel(p), () => { if (AbBypass) AbSetBypass(false); AbSetProfile(pp); }); }   // wybor profilu = przetwarzanie wlaczone
            var silItems = new Dictionary<string, MenuItem>();
            foreach (var sl in AbSilas) { var ss = sl; silItems[sl] = Item(L.T("Intensywność: ") + AbSilaLabel(sl), () => AbSetSila(ss)); }
            var bypItem = Item(L.T("⏻ Bez przetwarzania dźwięku (wł./wył.)"), () => AbSetBypass(!AbBypass));
            cm.Items.Add(new Separator());
            // 📌 zawsze na wierzchu i przezroczystosc (takze kolko myszy nad widzetem) - zapamietane
            var topItem = Item(L.T("📌 Zawsze na wierzchu"), () => { w.Topmost = !w.Topmost; SetPref(key + "-top", w.Topmost ? "1" : "0"); });
            var alphaMenu = new MenuItem { Header = L.T("◐ Przezroczystość") };
            foreach (var pc in new[] { 100, 85, 70, 55, 40 }) { var v = pc; var mi = new MenuItem { Header = v + " %" }; mi.Click += (s2, e2) => SetAlpha(v / 100.0); alphaMenu.Items.Add(mi); }
            alphaMenu.Items.Add(new MenuItem { Header = L.T("(albo kółko myszy nad widżetem)"), IsEnabled = false });
            cm.Items.Add(alphaMenu);
            cm.Items.Add(new Separator());
            Item(onlyV ? L.T("🔄 Zamień na widżet z liczbami") : L.T("🔄 Zamień na samo V"), () => ShowAbDesk(!onlyV));
            Item(L.T("✕ Zamknij widżet z pulpitu"), () => w.Close());
            // ten sam mechanizm przezroczystosci co reszta okien Velivo (zapamietany, przywracany przy starcie) - bez drugiego, sprzecznego
            void SetAlpha(double a) => SetWindowAlpha(w, Math.Round(Math.Max(0.25, Math.Min(1, a)), 2));
            w.Topmost = GetPref(key + "-top") == "1";
            w.MouseWheel += (s2, e2) => { if (Keyboard.Modifiers != ModifierKeys.Alt) { SetAlpha(WindowAlpha(w) + (e2.Delta > 0 ? 0.05 : -0.05)); e2.Handled = true; } };   // Alt+kolko obsluguje Velivo samo
            cm.Opened += (s, e) => { topItem.IsChecked = w.Topmost; foreach (MenuItem mi in alphaMenu.Items) if (mi.IsEnabled && int.TryParse(((string)mi.Header).Split(' ')[0], out var pv)) mi.IsChecked = Math.Abs(WindowAlpha(w) * 100 - pv) < 3; bypItem.IsChecked = AbBypass; foreach (var kv in profItems) kv.Value.IsChecked = !AbBypass && kv.Key == AbProfileName; foreach (var kv in silItems) { kv.Value.IsChecked = kv.Key == AbSilaName; kv.Value.IsEnabled = AbProfileName != "off"; } };   // stan widac w menu
            w.ContextMenu = cm;
            var tm = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };   // 30 kl./s - wskazowki reaguja szybko
            tm.Tick += (s, e) =>
            {
                if (!w.IsVisible) return;
                bool live = _abOut != null && AbProc.Active && AbFlowing();
                st.Text = AbBypass || !AbWanted ? "⏻" : AbProfileShort(AbProfileName) + (AbProc.Dialog > 0 ? "+🗣" : "");   // krotko: Lim / L / B / N, ⏻ = bez przetwarzania
                vuUpd(live);
                string F(double x) => double.IsNegativeInfinity(x) || double.IsNaN(x) || x < -69 || !live ? "–" : x.ToString("0.0");
                if ((DateTime.UtcNow - numsAt).TotalMilliseconds >= 250)
                {   // liczby 4×/s - czytelne
                    numsAt = DateTime.UtcNow;
                    lufs.Text = F(AbProc.Out.ShortTerm) + " LUFS";
                    orig.Text = L.T("źródło ") + F(AbProc.Raw.ShortTerm);
                    double d = AbProc.Out.ShortTerm - AbProc.Raw.ShortTerm;
                    bool ok = live && !double.IsNaN(d) && !double.IsInfinity(d);
                    delta.Text = !ok ? "" : Math.Abs(d) < 0.25 ? "±0 dB" : (d > 0 ? "▲ +" : "▼ ") + d.ToString("0.0") + " dB";   // krotko - mieści się w wierszu
                    delta.Foreground = !ok || Math.Abs(d) < 0.25 ? dim : d > 0 ? green : orange;
                }
                if (!w.IsMouseOver) return;   // podpowiedz liczona tylko pod mysza
                w.ToolTip = L.T("Lewe ramię V (jasny turkus) – Velivo podgłośniło, prawe (ciemny fiolet) – ściszyło; długość i liczba – ile dB. Puste V – bez zmiany względem źródła.") + "\n" + "M " + F(AbProc.Out.Momentary) + "  S " + F(AbProc.Out.ShortTerm) + "  I " + F(AbProc.Out.Integrated) + " LUFS  TP " + F(AbProc.PeakOutDb) + " dBTP\n" + L.T("Tryb: ") + (AbBypass ? L.T("bez przetwarzania") : AbProfileLabel(AbProfileName) + (AbProfileName != "off" ? " " + AbSilaLabel(AbSilaName) : "")) + "\n" + L.T("Przeciągnij, aby przenieść; dwuklik – pełny widżet; prawy przycisk – menu.");
            };
            w.Closed += (s, e) => { tm.Stop(); if (onlyV) _abDeskV = null; else _abDesk = null; if (!_mainClosing && !_reallyExit) SetPref(key, "0"); };   // zamkniety recznie - nie wraca; przy zamykaniu Velivo wraca po starcie
            w.IsVisibleChanged += (s, e) => { if (w.IsVisible) tm.Start(); else tm.Stop(); };
            if (onlyV) _abDeskV = w; else _abDesk = w; SetPref(key, "1");
            w.Show(); tm.Start();
        }

        // 🎛 wskaznik „V” Velivo (wybor wlasciciela): jedno V dla obu kanalow. Prawe ramie wypelnia sie jasnym turkusem, gdy Velivo
        // podglosnilo, lewe ciemnym fioletem, gdy sciszylo (od 2026-11-09 odwrotnie: lewe = podglosnione, prawe = sciszone); dlugosc = ile dB (pelne ramie = 15 dB). Puste V = bez zmiany wzgledem zrodla.
        // Wartosc = srednia zmiana L i R (Audio Balance zmienia oba kanaly tak samo). Viewbox - ten sam rysunek na pulpicie i w pelnym widzecie.
        FrameworkElement AbVuPair(out Action<bool> upd, bool number = true)
        {
            var cv = new Canvas { Width = 90, Height = number ? 80 : 64 };
            var G = new SolidColorBrush(Color.FromRgb(0x5F, 0xF2, 0xE4)); var O = new SolidColorBrush(Color.FromRgb(0x5B, 0x3F, 0xD6)); var dimB = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA6));   // kolory Velivo
            var OT = new SolidColorBrush(Color.FromRgb(0x8B, 0x6B, 0xFF));   // napis „ściszone” jaśniejszy - ciemny fiolet na ciemnym tle byłby nieczytelny
            var logoBrush = new LinearGradientBrush(Color.FromRgb(0x7C, 0x5C, 0xFF), Color.FromRgb(0x2E, 0xD3, 0xC6), 90);
            Point bot = new Point(45, 58), tl = new Point(17, 8), tr = new Point(73, 8);
            cv.Children.Add(new Polyline { Points = new PointCollection { tl, bot, tr }, Stroke = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x33)), StrokeThickness = 11, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            var arm = new Line { X1 = bot.X, Y1 = bot.Y, X2 = bot.X, Y2 = bot.Y, StrokeThickness = 11, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Stroke = G };
            cv.Children.Add(arm);
            cv.Children.Add(new Polyline { Points = new PointCollection { tl, bot, tr }, Stroke = logoBrush, StrokeThickness = 1.3, StrokeLineJoin = PenLineJoin.Round, Opacity = 0.9 });
            var tx = new TextBlock { FontSize = 10, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Width = 90 };
            System.Windows.Documents.Typography.SetNumeralAlignment(tx, FontNumeralAlignment.Tabular);
            Canvas.SetLeft(tx, 0); Canvas.SetTop(tx, 65); if (number) cv.Children.Add(tx);   // samo V na pulpicie - bez liczby
            var box = new Viewbox { Child = cv, Stretch = Stretch.Uniform };
            double np = 0; var dAt = DateTime.MinValue;
            upd = live =>
            {
                var sn = AbProc.Delayed(_abOut?.AheadFrames ?? 0);   // to, co słychać teraz (za kolejką karty)
                double dl = sn.VuL - sn.VuInL, dr = sn.VuR - sn.VuInR;
                double d = live && !AbBypass && sn.VuInL > -70 ? (dl + dr) / 2 : 0;
                if (double.IsNaN(d) || double.IsInfinity(d)) d = 0;
                np += (Math.Max(-15, Math.Min(15, d)) - np) * 0.7;   // szybka reakcja, bez skakania
                var tip = np >= 0 ? tl : tr; double k = Math.Abs(np) / 15;   // podglosnione - lewe ramie, sciszone - prawe (wybor wlasciciela 2026-11-09)
                arm.X2 = bot.X + (tip.X - bot.X) * k; arm.Y2 = bot.Y + (tip.Y - bot.Y) * k; arm.Stroke = np >= 0 ? G : O;
                arm.Visibility = k < 0.02 ? Visibility.Hidden : Visibility.Visible;
                if ((DateTime.UtcNow - dAt).TotalMilliseconds >= 250)
                {   // liczba 4×/s - czytelna
                    dAt = DateTime.UtcNow;
                    tx.Text = Math.Abs(np) < 0.25 ? "±0 dB" : (np > 0 ? "+" : "") + np.ToString("0.0") + " dB";
                    tx.Foreground = Math.Abs(np) < 0.25 ? dimB : np > 0 ? G : OT;
                }
            };
            return box;
        }


        void AbTrayRemove() { _abTrayAnim?.Stop(); _abTrayAnim = null; try { if (_abTray != null) { _abTray.Visible = false; _abTray.Dispose(); _abTray = null; } } catch (Exception) { } }
        DispatcherTimer _abTrayAnim; static System.Drawing.Icon[] _abVFrames;

        // ikonka „V” Velivo: kontur litery, wnetrze wypelnione od dolu wg glosnosci (0 = przygaszona - cisza / wylaczone); klatki liczone raz
        static System.Drawing.Icon AbVIcon(double fill)
        {
            using (var bmp = new System.Drawing.Bitmap(32, 32))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.Clear(System.Drawing.Color.Transparent);
                    var v = new[] { new System.Drawing.PointF(2, 4), new System.Drawing.PointF(10, 4), new System.Drawing.PointF(16, 21), new System.Drawing.PointF(22, 4), new System.Drawing.PointF(30, 4), new System.Drawing.PointF(19, 29), new System.Drawing.PointF(13, 29) };
                    using (var path = new System.Drawing.Drawing2D.GraphicsPath()) {
                        path.AddPolygon(v);
                        using (var dimB = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(fill <= 0 ? 110 : 70, 0x7C, 0x5C, 0xFF))) g.FillPath(dimB, path);
                        if (fill > 0)
                        {
                            float top = (float)(29 - 25 * fill);
                            g.SetClip(new System.Drawing.RectangleF(0, top, 32, 32 - top));
                            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.PointF(0, 29), new System.Drawing.PointF(0, 4), System.Drawing.Color.FromArgb(0x7C, 0x5C, 0xFF), System.Drawing.Color.FromArgb(0x2E, 0xD3, 0xC6))) g.FillPath(lg, path);
                            g.ResetClip();
                        }
                        using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(230, 0x2E, 0xD3, 0xC6), 1.5f)) g.DrawPath(pen, path);
                    }
                }
                return System.Drawing.Icon.FromHandle(bmp.GetHicon());   // 9 klatek na cale dzialanie Velivo - bez wycieku uchwytow
            }
        }

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
