using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Velivo
{
    // 🔊 tor dźwięku: Windows gra na „CABLE Input” (VB-Audio Virtual Cable) → odbiór z „CABLE Output” → Audio Balance → wybrane głośniki
    sealed class AbEngine : IDisposable
    {
        public readonly AbProcessor Proc = MkProc();
        static AbProcessor MkProc() { var p = new AbProcessor(); p.Pre.KeepLevel = true; return p; }
        WasapiCapture cap; WasapiOut outp; BufferedWaveProvider buf;
        public int Latency = 1;   // ⏱ opóźnienie: 0 niskie (~0,1 s), 1 normalne (~0,2 s), 2 bezpieczne (~0,35 s, bez pyknięć)
        int lc, lt, lo;
        public volatile AbRecorder Rec;   // ⏺ nagrywanie wyjścia (WAV)
        // ⏱ ile dźwięk spóźnia się przez ten program (kabel, zapas, karta, wtyczki VST3) - Velivo przesuwa o tyle dźwięk filmu, żeby obraz był równo z głosem
        public int DelayMs => Running ? lc / 2 + lt + lo + 5 + Vst3.Latency * 1000 / AbProcessor.Fs : 0;
        public volatile bool Running; public string OutputName, Error;
        // 👁 podgląd bez przetwarzania: gdy Windows gra prosto na głośniki (nie przez kabel), wskaźniki pokazują ten dźwięk (podsłuch wyjścia Windows, tylko pomiar -
        // nic nie jest zmieniane ani odtwarzane drugi raz). Osobny procesor pomiarowy: dźwięk programów skierowanych wprost na kabel dalej idzie przez Proc.
        public readonly AbProcessor MonProc = new AbProcessor { Bypass = true };
        WasapiLoopbackCapture mon; string monId; System.Threading.Timer monTm; long monLast; readonly object monLk = new object(); float[] monTmp = new float[9600];
        public AbProcessor Shown => mon != null && !(Proc.Raw.ShortTerm > -69) ? MonProc : Proc;
        public void Monitor(string renderId)
        {
            if (renderId == monId) return;
            var old = mon; mon = null; monId = renderId; var t = monTm; monTm = null;
            try { t?.Dispose(); } catch (Exception) { }
            if (old != null) System.Threading.Tasks.Task.Run(() => { try { old.StopRecording(); } catch (Exception) { } try { old.Dispose(); } catch (Exception) { } });
            if (renderId == null) return;
            try
            {
                var dev = new MMDeviceEnumerator().GetDevice(renderId); var c = new WasapiLoopbackCapture(dev);
                var b = new BufferedWaveProvider(c.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(500), ReadFully = false };
                ISampleProvider sp = b.ToSampleProvider();
                if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp); else if (sp.WaveFormat.Channels > 2) sp = new Downmix(sp);
                if (sp.WaveFormat.SampleRate != AbProcessor.Fs) sp = new WdlResamplingSampleProvider(sp, AbProcessor.Fs);
                c.DataAvailable += (s, e) =>
                {
                    if (!ReferenceEquals(s, mon)) return;
                    lock (monLk)
                    {
                        b.AddSamples(e.Buffer, 0, e.BytesRecorded); monLast = Environment.TickCount64; int n;
                        try { while ((n = sp.Read(monTmp, 0, monTmp.Length)) >= 2) MonProc.Process(monTmp, n / 2); } catch (Exception) { }
                    }
                };
                mon = c; c.StartRecording();
                // nic nie gra = Windows nie daje próbek: wskaźniki opadają do ciszy zamiast stać na ostatniej wartości
                monTm = new System.Threading.Timer(_ => { if (mon == null || Environment.TickCount64 - monLast < 250) return; lock (monLk) { Array.Clear(monTmp, 0, monTmp.Length); try { MonProc.Process(monTmp, monTmp.Length / 2); } catch (Exception) { } } }, null, 100, 100);
            }
            catch (Exception ex) { App.LogError(ex); mon = null; }   // bez podglądu - reszta działa jak dotąd (monId zostaje: nie ponawia co chwilę)
        }
        // 🎚 głośność Windows jak tłumik w studiu - ZA przetwarzaniem: suwak głośności „CABLE Input” (klawisze głośności, gdy kabel jest domyślny) ścisza dźwięk
        // PRZED programem - kompresor i wyrównanie dostawały wtedy np. -50 dB (próg nigdy nieosiągany = „kompresor nie działa”, wyrównanie na limicie = „cicho”).
        // Teraz: przed przetwarzaniem poziom wraca do pełnego (o tyle, o ile ściszył Windows), a po przetwarzaniu jest ściszany o tyle samo.
        // Razem zawsze 1:1 - głośność z klawiszy działa jak dotąd, a przetwarzanie nie zależy od suwaka.
        volatile float preT = 1, postT = 1, plVol = 1; internal float PostT => postT; System.Threading.Timer volTm; volatile bool volDirty; int volN;   // plVol - suwak głośności odtwarzacza Velivo (film przychodzi w pełnej głośności)
        MMDevice volIn, volCap; string volInId, volCapId, volOutId; bool volLogged; double volInLog, volCapLog; long volLogAt;
        void VolStart(string cableOutId, string outId)
        {
            volCapId = cableOutId; volOutId = outId; volInId = null; volLogged = false; preT = postT = 1; volDirty = true; volN = 0;
            volTm = new System.Threading.Timer(_ => VolRead(), null, 0, 100);
        }
        void VolStop()
        {
            var t = volTm; volTm = null; try { t?.Dispose(); } catch (Exception) { }
            preT = postT = 1;
        }
        // 🗣 łącze z lektorem Velivo: (1) znak „lektor czyta” (zdarzenie Windows ustawiane przez Velivo) → zamrożenie pomiaru programu, przytrzymane 0,8 s po kwestii;
        // (2) dla Velivo: ile ten program teraz podgłaśnia i jaki ma cel - lektor gra o tyle ciszej / głośniej, żeby po przetwarzaniu każdy głos (jest ich wiele)
        // wychodził tak samo głośno względem celu, niezależnie od filmu
        System.Threading.EventWaitHandle lekEv; long lekAt, lekSince; System.IO.MemoryMappedFiles.MemoryMappedFile lacze; System.IO.MemoryMappedFiles.MemoryMappedViewAccessor laczeV, glosV, wynikV;
        void LekPoll()
        {
            try
            {
                lekEv ??= new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, @"Local\Velivo.Lektor.Czyta");
                // znak ustawiony bez przerwy ponad 40 s = Velivo zamknięte w trakcie kwestii (żadna kwestia tyle nie trwa) - kasujemy go, pomiar wraca
                if (lekEv.WaitOne(0)) { if (lekSince == 0) lekSince = Environment.TickCount64; if (Environment.TickCount64 - lekSince > 40000) { lekEv.Reset(); lekSince = 0; } else lekAt = Environment.TickCount64; } else lekSince = 0;
                Proc.Pre.Hold = Proc.LekHold = Environment.TickCount64 - lekAt < 800;   // lektor czyta (+0,8 s): kompresor, pomiar poziomu i wyrównanie stoją - głos lektora nie jest liczony jako dźwięk filmu
                try { glosV ??= System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen(@"Local\Velivo.Odtwarzacz.Glosnosc", 16).CreateViewAccessor(); float gv = glosV.ReadSingle(0); long ga = glosV.ReadInt64(8); plVol = ga != 0 && Environment.TickCount64 - ga < 3000 && gv >= 0 && gv <= 1.25f ? gv : 1; } catch (Exception) { plVol = 1; }   // 🔊 suwak odtwarzacza Velivo - po wyrównaniu (świeży do 3 s, potem 1)
                if (laczeV == null) { lacze = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen(@"Local\Velivo.AudioBalance.Lacze", 16); laczeV = lacze.CreateViewAccessor(); }
                laczeV.Write(0, Proc.Bypass ? 0f : Proc.Pre.MakeupNowDb * (Proc.Pre.Params.IsEnabled ? 1 : 0) + (float)Proc.GainDb); laczeV.Write(4, (float)Proc.EffTarget); laczeV.Write(8, Environment.TickCount64);
                wynikV ??= System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen(@"Local\Velivo.AudioBalance.Wynik", 16).CreateViewAccessor();   // 🗣 dla lektora Velivo: jak głośno gra teraz film po wyrównaniu (3 s, pełny poziom)
                wynikV.Write(0, (float)Proc.Out.ShortTerm); wynikV.Write(8, Environment.TickCount64);
            }
            catch (Exception) { }
        }
        void VolRead()
        {
            if (volTm == null || !System.Threading.Monitor.TryEnter(this)) return;
            try
            {
                LekPoll();
                if (!volDirty && ++volN % 10 != 0) return;   // od razu po zmianie (powiadomienie Windows), zapasowo co sekundę
                volDirty = false;
                if (volIn == null || volInId == null)
                {
                    volInId = CableInputId(); if (volInId == null) return;
                    var en = new MMDeviceEnumerator(); volIn = en.GetDevice(volInId); volCap = en.GetDevice(volCapId);
                    volIn.AudioEndpointVolume.OnVolumeNotification += d => volDirty = true;
                    volCap.AudioEndpointVolume.OnVolumeNotification += d => volDirty = true;
                }
                double inDb = volIn.AudioEndpointVolume.MasterVolumeLevel, capDb = volCap.AudioEndpointVolume.MasterVolumeLevel;
                double post = Math.Max(0, Math.Min(60, -inDb)), pre = Math.Max(0, Math.Min(60, post + Math.Max(0, -capDb)));   // suwak nagrywania „CABLE Output” też ścisza - wyrównany tylko przed przetwarzaniem
                preT = (float)Math.Pow(10, pre / 20); postT = (float)Math.Pow(10, -post / 20);
                if (volLogged && (Math.Abs(inDb - volInLog) > 0.5 || Math.Abs(capDb - volCapLog) > 0.5) && Environment.TickCount64 - volLogAt > 2000)   // 🔬 każda zmiana głośności Windows w dzienniku (szukanie „ścisza się samo”)
                { volLogAt = Environment.TickCount64; App.LogInfo($"Głośność Windows zmieniona: CABLE Input {volInLog:0.0} → {inDb:0.0} dB, odbiór CABLE Output {volCapLog:0.0} → {capDb:0.0} dB"); volInLog = inDb; volCapLog = capDb; }
                if (!volLogged) { volInLog = inDb; volCapLog = capDb; }
                if (!volLogged)
                {
                    volLogged = true; string spk = "?";
                    try { using var o = new MMDeviceEnumerator().GetDevice(volOutId); spk = Math.Round(o.AudioEndpointVolume.MasterVolumeLevelScalar * 100) + "%" + (o.AudioEndpointVolume.Mute ? " (wyciszone)" : ""); } catch (Exception) { }
                    App.LogInfo($"Głośność Windows: CABLE Input {Math.Round(volIn.AudioEndpointVolume.MasterVolumeLevelScalar * 100)}% ({inDb:0.0} dB), odbiór CABLE Output {Math.Round(volCap.AudioEndpointVolume.MasterVolumeLevelScalar * 100)}% ({capDb:0.0} dB), głośniki {spk} – przetwarzanie dostaje pełny poziom (+{pre:0.0} dB), głośność po przetwarzaniu ({-post:0.0} dB)");
                }
            }
            catch (Exception ex) { if (!volLogged) { volLogged = true; App.LogError(ex); } volIn = null; volInId = null; preT = postT = 1; }   // bez odczytu - jak dotąd (1:1)
            finally { System.Threading.Monitor.Exit(this); }
        }
        public event Action Stopped;   // urządzenie zniknęło / błąd - okno spróbuje ponownie

        // VB-Cable w każdej wersji: „CABLE Input/Output (VB-Audio Virtual Cable)”, nowsze „Głośniki (VB-Audio Virtual Cable)”, „CABLE In 16 Ch”
        static bool IsCable(string name) => name != null && (name.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0);
        public static bool CableInstalled() { try { return FindCable() != null; } catch (Exception) { return false; } }
        static MMDevice FindCable()
        {
            var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) if (d.FriendlyName.IndexOf("CABLE Output", StringComparison.OrdinalIgnoreCase) >= 0 || (IsCable(d.FriendlyName) && d.FriendlyName.IndexOf("16", StringComparison.Ordinal) < 0)) return d;   // odbiór: „CABLE Output (VB-Audio Virtual Cable)”
            return null;
        }
        // głośniki / słuchawki (bez wirtualnego kabla - inaczej dźwięk krążyłby w kółko)
        public static List<(string id, string name)> Outputs()
        {
            var l = new List<(string, string)>();
            try { var en = new MMDeviceEnumerator(); foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) if (!IsCable(d.FriendlyName)) l.Add((d.ID, d.FriendlyName)); } catch (Exception ex) { App.LogError(ex); }
            return l;
        }
        public static (string id, string name) DefaultOutput()
        {
            try { var d = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); return (d.ID, d.FriendlyName); } catch (Exception) { return (null, null); }
        }
        // 🎮 która aplikacja gra teraz przez kabel (najgłośniejsza sesja dźwięku Windows na „CABLE Input”) - do osobnych ustawień kompresora dla programu
        static readonly string[] NotApps = { "VelivoAudioBalance", "SnippingTool", "ScreenClippingHost", "ScreenSketch", "GameBar", "obs64", "ShareX", "audiodg", "SystemSounds" };
        public static string PlayingApp(string cableId)
        {
            try
            {
                using var dev = new MMDeviceEnumerator().GetDevice(cableId); var ss = dev.AudioSessionManager.Sessions; string best = null; float bp = 0.003f;
                for (int i = 0; i < ss.Count; i++)
                {
                    var x = ss[i]; float pk = x.AudioMeterInformation.MasterPeakValue; if (pk <= bp) continue;
                    string n = null; try { n = System.Diagnostics.Process.GetProcessById((int)x.GetProcessID).ProcessName; } catch (Exception) { }
                    if (string.IsNullOrEmpty(n) || Array.Exists(NotApps, x => x.Equals(n, StringComparison.OrdinalIgnoreCase))) continue;   // nagrywanie ekranu i dźwięki systemu to nie „program, który gra”
                    best = n; bp = pk;
                }
                return best;
            }
            catch (Exception) { return null; }
        }
        public static string CableInputId()
        {
            // wejście kabla (tu Windows ma grać): „CABLE Input”, a w nowszym VB-Cable „Głośniki (VB-Audio Virtual Cable)”; wersja 16-kanałowa tylko w zapasie
            try
            {
                string any = null; var en = new MMDeviceEnumerator();
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    var n = d.FriendlyName; if (!IsCable(n)) continue;
                    if (n.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("16", StringComparison.Ordinal) < 0) return d.ID;
                    any ??= d.ID;
                }
                return any;
            }
            catch (Exception) { }
            return null;
        }

        public bool Start(string outId)
        {
            Stop(); Error = null;
            try
            {
                var cable = FindCable(); if (cable == null) { Error = "cable"; return false; }
                var en = new MMDeviceEnumerator(); MMDevice dev = null;
                if (!string.IsNullOrEmpty(outId)) try { dev = en.GetDevice(outId); if (dev.State != DeviceState.Active || IsCable(dev.FriendlyName)) dev = null; } catch (Exception) { dev = null; }
                if (dev == null) foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) if (!IsCable(d.FriendlyName)) { dev = d; break; }
                if (dev == null) { Error = "output"; return false; }
                OutputName = dev.FriendlyName;
                timeBeginPeriod(1); (lc, lt, lo) = Latency == 0 ? (40, 40, 40) : Latency == 2 ? (100, 150, 100) : (60, 80, 60); cap = new WasapiCapture(cable, false, lc);   // bufor kabla 100 ms: przy 30 ms jedno spóźnione odpytanie (zegar Windows ~16 ms) gubiło próbki = pyknięcie   // odpytywanie, nie zdarzenia - tryb zdarzeń kończył się po cichu po ~1,5 s (VB-Cable / Realtek, dziennik właściciela)
                buf = new BufferedWaveProvider(cap.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(1000), ReadFully = false };
                var cf = cap.WaveFormat;
                var myBuf = buf; Diag.Reset();
                cap.DataAvailable += (s, e) =>
                {
                    RtThread(); Diag.Cap(e.BytesRecorded / cf.BlockAlign, cf.SampleRate);
                    myBuf.AddSamples(e.Buffer, 0, e.BytesRecorded);   // własny bufor tej sesji - stary odbiór nie dokłada do nowego
                };
                cap.RecordingStopped += (s, e) => { if (Running && ReferenceEquals(s, cap)) { App.LogInfo("Zatrzymany: odbiór z CABLE Output" + (e.Exception != null ? " – " + e.Exception.Message : " (bez błędu Windows)")); if (e.Exception != null) App.LogError(e.Exception); Error = L.T("odbiór z CABLE Output przerwany") + (e.Exception != null ? ": " + e.Exception.Message : ""); Running = false; Stopped?.Invoke(); } };
                ISampleProvider sp = buf.ToSampleProvider();
                if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
                else if (sp.WaveFormat.Channels > 2) sp = new Downmix(sp);
                if (sp.WaveFormat.SampleRate != AbProcessor.Fs) sp = new WdlResamplingSampleProvider(sp, AbProcessor.Fs);
                sp = new Elastic(sp, buf, lt);   // elastyczny bufor: zapas stale ok. 100 ms, bez przerw i trzasków (zegary kabla i głośników się rozjeżdżają)
                outp = new WasapiOut(dev, AudioClientShareMode.Shared, false, lo);   // jw. - odpytywanie, 100 ms zapasu
                outp.Init(new Processing(sp, Proc) { Owner = this });
                outp.PlaybackStopped += (s, e) => { if (Running && ReferenceEquals(s, outp)) { App.LogInfo("Zatrzymany: odtwarzanie na " + OutputName + (e.Exception != null ? " – " + e.Exception.Message : " (bez błędu Windows)")); if (e.Exception != null) App.LogError(e.Exception); Error = L.T("odtwarzanie na głośnikach przerwane") + (e.Exception != null ? ": " + e.Exception.Message : ""); Running = false; Stopped?.Invoke(); } };
                Proc.NewMedia(); Running = true;
                VolStart(cable.ID, dev.ID);
                cap.StartRecording(); outp.Play();
                App.LogInfo("Audio Balance (program z " + System.IO.File.GetLastWriteTime(Environment.ProcessPath).ToString("yyyy-MM-dd HH:mm") + ", pomiar co 30 s): CABLE Output (" + cf + ") → " + OutputName);
                return true;
            }
            catch (Exception ex) { App.LogError(ex); Error = ex.Message; Stop(); return false; }
        }

        public void Stop()
        {
            Running = false; VolStop(); Monitor(null);
            try { cap?.StopRecording(); } catch (Exception) { }
            try { outp?.Stop(); } catch (Exception) { }
            try { cap?.Dispose(); } catch (Exception) { }
            try { outp?.Dispose(); } catch (Exception) { }
            cap = null; outp = null; buf = null;
        }
        public void Dispose() => Stop();

        // 🎚 elastyczny bufor: kabel i głośniki mają osobne zegary (zawsze trochę różne) - odtwarzanie minimalnie szybciej / wolniej (najwyżej ±0,3 %,
        // niesłyszalne), żeby zapas stał przy ok. 100 ms: bez przerw (brak dźwięku) i bez przycinania (nadmiar). Start po zebraniu zapasu.
        // wątki dźwięku jak w programach audio: harmonogram „Pro Audio” Windows (MMCSS) i najwyższy priorytet - obciążenie okna / komputera nie przerywa dźwięku (trzaski)
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);   // dokładny zegar (1 ms) dla odpytywania WASAPI
        [ThreadStatic] static bool rtDone;
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)] static extern IntPtr AvSetMmThreadCharacteristics(string task, ref int index);
        internal static void RtThread()
        {
            if (rtDone) return; rtDone = true;
            try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest; int i = 0; AvSetMmThreadCharacteristics("Pro Audio", ref i); } catch (Exception) { }
        }

        sealed class Elastic : ISampleProvider
        {
            readonly ISampleProvider src; readonly BufferedWaveProvider b; readonly double bytesPerMs;
            float[] inb = new float[48000 * 2]; int inLen; double pos, rate = 1, drift; bool filling = true; float lastL, lastR; int fadeIn; public static int Gaps; static DateTime gapLog;
            readonly float[] pull = new float[960];
            readonly double tgt;
            public Elastic(ISampleProvider s, BufferedWaveProvider buf, double target) { tgt = target; src = s; b = buf; bytesPerMs = buf.WaveFormat.AverageBytesPerSecond / 1000.0; }
            public WaveFormat WaveFormat => src.WaveFormat;
            double LevelMs => b.BufferedBytes / bytesPerMs + (inLen - pos) / (WaveFormat.SampleRate / 1000.0);
            bool Pull()
            {   // dobranie 10 ms z kabla do bufora wewnętrznego (zachowując niezużyte)
                int n = src.Read(pull, 0, pull.Length) / 2; if (n <= 0) return false;
                int keep = (int)pos; if (keep > 0) { Array.Copy(inb, keep * 2, inb, 0, (inLen - keep) * 2); inLen -= keep; pos -= keep; }
                if ((inLen + n) * 2 > inb.Length) Array.Resize(ref inb, (inLen + n) * 4);
                Array.Copy(pull, 0, inb, inLen * 2, n * 2); inLen += n; return true;
            }
            public int Read(float[] buffer, int offset, int count)
            {
                double lvl = LevelMs;
                if (filling) { if (lvl < tgt) { Silence(buffer, offset, 0, count); return count; } filling = false; fadeIn = 240; }
                // regulator PI: część P - bieżący zapas, część I - uczy się stałej różnicy zegarów kart (zapas wraca do 100 ms); razem najwyżej ±0,5 % (niesłyszalne)
                double err = (lvl - tgt) / tgt; drift = Math.Max(-0.004, Math.Min(0.004, drift + err * 0.00005));
                double want = 1 + Math.Max(-0.005, Math.Min(0.005, err * 0.003 + drift));
                rate += (want - rate) * 0.05;
                int frames = count / 2;
                for (int f = 0; f < frames; f++)
                {
                    while ((int)pos + 1 >= inLen) if (!Pull()) { Silence(buffer, offset, f * 2, count); filling = true; Gaps++; if ((DateTime.UtcNow - gapLog).TotalSeconds > 10) { gapLog = DateTime.UtcNow; var gn = Gaps; System.Threading.Tasks.Task.Run(() => App.LogInfo("Przerwa w dźwięku: zapas pusty (z kabla przyszło za mało), przerw od startu: " + gn)); } return count; }   // kabel milczy - cisza i ponowne zebranie zapasu
                    int k = (int)pos; float fr = (float)(pos - k);
                    buffer[offset + 2 * f] = inb[2 * k] + (inb[2 * k + 2] - inb[2 * k]) * fr;
                    buffer[offset + 2 * f + 1] = inb[2 * k + 1] + (inb[2 * k + 3] - inb[2 * k + 1]) * fr;
                    if (fadeIn > 0) { float g = 1 - fadeIn / 240f; buffer[offset + 2 * f] *= g; buffer[offset + 2 * f + 1] *= g; fadeIn--; }
                    lastL = buffer[offset + 2 * f]; lastR = buffer[offset + 2 * f + 1];
                    pos += rate;
                }
                return count;
            }
            // przerwa: zamiast nagłego zera (pyknięcie) wyciszenie w 5 ms od ostatniej próbki
            void Silence(float[] buffer, int offset, int from, int count)
            {
                for (int i = from, n = 0; i < count; i += 2, n++) { float g = n < 240 ? 1 - n / 240f : 0; buffer[offset + i] = lastL * g; if (i + 1 < count) buffer[offset + i + 1] = lastR * g; }
                lastL = lastR = 0;
            }
        }

        // 🔬 pomiar co 30 s do bledy.log: przerwy w odbiorze z kabla i w odtwarzaniu, ile próbek przyszło względem zegara, czas przetwarzania - szukanie pyknięć
        static class Diag
        {
            static readonly double tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            static long capLast, outLast, start, samples; static double capMax, outMax, procMax; static int capLate, outLate; static int rate;
            public static void Cap(int frames, int sr)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp(); rate = sr;
                if (capLast != 0) { double g = (now - capLast) * tick; if (g > capMax) capMax = g; if (g > 60) capLate++; } else start = now;
                capLast = now; samples += frames;
                if ((now - start) * tick >= 30000)
                {
                    double sec = (now - start) * tick / 1000, pct = samples / (sec * sr) * 100;
                    var line = $"Pomiar 30 s: z kabla {pct:0.00}% próbek (100% = bez strat), najdłuższa przerwa odbioru {capMax:0} ms ({capLate}× >60 ms), odtwarzania {outMax:0} ms ({outLate}× >120 ms), przetwarzanie najdłużej {procMax:0.0} ms"; System.Threading.Tasks.Task.Run(() => App.LogInfo(line));   // zapis na dysk poza wątkiem dźwięku
                    start = now; samples = 0; capMax = outMax = procMax = 0; capLate = outLate = 0;
                }
            }
            public static void Reset() { capLast = outLast = 0; samples = 0; capMax = outMax = procMax = 0; capLate = outLate = 0; }   // nowa sesja - przerwa na ponowny start nie liczy się jako zacięcie
            public static void Out() { long now = System.Diagnostics.Stopwatch.GetTimestamp(); if (outLast != 0) { double g = (now - outLast) * tick; if (g > outMax) outMax = g; if (g > 120) outLate++; } outLast = now; }
            public static void Proc(long t) { double ms = t * tick; if (ms > procMax) procMax = ms; }
        }

        // przetwarzanie w porcjach od głośników: dźwięk → Audio Balance (w miejscu)
        sealed class Processing : ISampleProvider
        {
            readonly ISampleProvider src; readonly AbProcessor proc; float[] tmp = new float[9600]; public AbEngine Owner; float pre = 1, post = 1; int quietN; public volatile bool Idle;   // Idle - cisza ponad 3 s, przetwarzanie stoi
            public Processing(ISampleProvider s, AbProcessor p) { src = s; proc = p; }
            public WaveFormat WaveFormat => src.WaveFormat;
            public int Read(float[] buffer, int offset, int count)
            { RtThread();
                int n = src.Read(buffer, offset, count); if (n < 2) return n;
                n -= n & 1;
                // 💤 cisza (nic nie gra) dłużej niż 3 s: bez przetwarzania i wtyczek VST - procesor odpoczywa; pierwszy dźwięk wraca do pełnej obróbki od razu
                float pk = 0; for (int i = 0; i < n; i++) { float a = buffer[offset + i]; if (a > pk) pk = a; else if (-a > pk) pk = -a; }
                if (pk < 1e-6f) { if (quietN < int.MaxValue - n) quietN += n / 2; } else quietN = 0;
                if (quietN > 3 * 48000) { for (int i = 0; i < n; i++) buffer[offset + i] = 0; Idle = true; return n; }
                Idle = false;
                if (tmp.Length < n) tmp = new float[n];
                // kopiowanie próbka po próbce - NAudio podaje tu tablicę bajtów „udającą” float[] (WaveBuffer); Array.Copy jej odmawia („Source array type cannot be assigned…”)
                // 🎚 pełny poziom przed przetwarzaniem (płynnie, 20 ms - bez trzasku przy zmianie głośności)
                float pv = Owner?.plVol ?? 1, pT = (Owner?.preT ?? 1) / Math.Max(0.05f, pv), qT = (Owner?.postT ?? 1) * pv;   // 🔊 suwak odtwarzacza Velivo: przed pomiarem odwrócony (pełny poziom filmu), po wyrównaniu zastosowany - jak głośność Windows
                for (int i = 0; i < n; i += 2) { pre += (pT - pre) * 0.001f; tmp[i] = buffer[offset + i] * pre; tmp[i + 1] = buffer[offset + i + 1] * pre; }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); Diag.Out();
                try { proc.Process(tmp, n / 2); } catch (Exception ex) { App.LogError(ex); return n; }   // błąd przetwarzania - dźwięk bez zmian, nie cisza
                for (int i = 0; i < n; i++) buffer[offset + i] = tmp[i];
                Diag.Proc(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                Owner?.Rec?.Add(buffer, offset, n);   // ⏺ nagrywanie dźwięku po poprawkach (przed tłumikiem głośności - pełny poziom, jak w studiu)
                for (int i = 0; i < n; i += 2) { post += (qT - post) * 0.001f; buffer[offset + i] *= post; buffer[offset + i + 1] *= post; }   // 🎚 głośność Windows po przetwarzaniu
                AbWidok.Write(proc, proc.Profile != null ? 1 : 0, Owner?.DelayMs ?? 0);   // 🔗 wskaźniki dla Velivo (pamięć współdzielona)
                return n;
            }
        }

        // 5.1 / 7.1 → stereo (centrum i tylne -3 dB, LFE pominięty)
        internal sealed class Downmix : ISampleProvider
        {
            readonly ISampleProvider src; readonly int ch; float[] b = new float[0];
            public Downmix(ISampleProvider s) { src = s; ch = s.WaveFormat.Channels; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(s.WaveFormat.SampleRate, 2); }
            public WaveFormat WaveFormat { get; }
            public int Read(float[] buffer, int offset, int count)
            {
                int frames = count / 2, need = frames * ch; if (b.Length < need) b = new float[need];
                int got = src.Read(b, 0, need) / ch;
                for (int f = 0; f < got; f++)
                {
                    int i = f * ch; float l = b[i], r = b[i + 1], c = ch > 2 ? b[i + 2] * 0.707f : 0, sl = ch > 4 ? b[i + 4] * 0.707f : 0, sr = ch > 5 ? b[i + 5] * 0.707f : 0;
                    buffer[offset + 2 * f] = (l + c + sl) * 0.6f; buffer[offset + 2 * f + 1] = (r + c + sr) * 0.6f;
                }
                return got * 2;
            }
        }
    }

    // domyślne urządzenie Windows (IPolicyConfig - ten sam mechanizm co okno Dźwięk w Windows)
    static class DefaultDevice
    {
        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(); [PreserveSig] int GetDeviceFormat(); [PreserveSig] int ResetDeviceFormat(); [PreserveSig] int SetDeviceFormat();
            [PreserveSig] int GetProcessingPeriod(); [PreserveSig] int SetProcessingPeriod(); [PreserveSig] int GetShareMode(); [PreserveSig] int SetShareMode();
            [PreserveSig] int GetPropertyValue(); [PreserveSig] int SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility();
        }
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }
        public static bool Set(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            try { var pc = (IPolicyConfig)new PolicyConfigClient(); bool ok = true; for (int role = 0; role < 3; role++) ok &= pc.SetDefaultEndpoint(id, role) == 0; return ok; }
            catch (Exception ex) { App.LogError(ex); return false; }
        }
    }
}
