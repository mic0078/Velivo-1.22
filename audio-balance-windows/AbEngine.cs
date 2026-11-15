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
        public readonly AbProcessor Proc = new AbProcessor();
        WasapiCapture cap; WasapiOut outp; BufferedWaveProvider buf;
        public volatile bool Running; public string OutputName, Error;
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
                timeBeginPeriod(1); cap = new WasapiCapture(cable, false, 100);   // bufor kabla 100 ms: przy 30 ms jedno spóźnione odpytanie (zegar Windows ~16 ms) gubiło próbki = pyknięcie   // odpytywanie, nie zdarzenia - tryb zdarzeń kończył się po cichu po ~1,5 s (VB-Cable / Realtek, dziennik właściciela)
                buf = new BufferedWaveProvider(cap.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(1000), ReadFully = false };
                var cf = cap.WaveFormat;
                cap.DataAvailable += (s, e) =>
                {
                    RtThread(); Diag.Cap(e.BytesRecorded / cf.BlockAlign, cf.SampleRate);
                    buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
                };
                cap.RecordingStopped += (s, e) => { if (Running) { App.LogInfo("Zatrzymany: odbiór z CABLE Output" + (e.Exception != null ? " – " + e.Exception.Message : " (bez błędu Windows)")); if (e.Exception != null) App.LogError(e.Exception); Error = L.T("odbiór z CABLE Output przerwany") + (e.Exception != null ? ": " + e.Exception.Message : ""); Running = false; Stopped?.Invoke(); } };
                ISampleProvider sp = buf.ToSampleProvider();
                if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
                else if (sp.WaveFormat.Channels > 2) sp = new Downmix(sp);
                if (sp.WaveFormat.SampleRate != AbProcessor.Fs) sp = new WdlResamplingSampleProvider(sp, AbProcessor.Fs);
                sp = new Elastic(sp, buf);   // elastyczny bufor: zapas stale ok. 100 ms, bez przerw i trzasków (zegary kabla i głośników się rozjeżdżają)
                outp = new WasapiOut(dev, AudioClientShareMode.Shared, false, 100);   // jw. - odpytywanie, 100 ms zapasu
                outp.Init(new Processing(sp, Proc));
                outp.PlaybackStopped += (s, e) => { if (Running) { App.LogInfo("Zatrzymany: odtwarzanie na " + OutputName + (e.Exception != null ? " – " + e.Exception.Message : " (bez błędu Windows)")); if (e.Exception != null) App.LogError(e.Exception); Error = L.T("odtwarzanie na głośnikach przerwane") + (e.Exception != null ? ": " + e.Exception.Message : ""); Running = false; Stopped?.Invoke(); } };
                Proc.NewMedia(); Running = true;
                cap.StartRecording(); outp.Play();
                App.LogInfo("Audio Balance (program z " + System.IO.File.GetLastWriteTime(Environment.ProcessPath).ToString("yyyy-MM-dd HH:mm") + ", pomiar co 30 s): CABLE Output (" + cf + ") → " + OutputName);
                return true;
            }
            catch (Exception ex) { App.LogError(ex); Error = ex.Message; Stop(); return false; }
        }

        public void Stop()
        {
            Running = false;
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
            public Elastic(ISampleProvider s, BufferedWaveProvider buf) { src = s; b = buf; bytesPerMs = buf.WaveFormat.AverageBytesPerSecond / 1000.0; }
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
                if (filling) { if (lvl < 150) { Silence(buffer, offset, 0, count); return count; } filling = false; fadeIn = 240; }
                // regulator PI: część P - bieżący zapas, część I - uczy się stałej różnicy zegarów kart (zapas wraca do 100 ms); razem najwyżej ±0,5 % (niesłyszalne)
                double err = (lvl - 150) / 150; drift = Math.Max(-0.004, Math.Min(0.004, drift + err * 0.00005));
                double want = 1 + Math.Max(-0.005, Math.Min(0.005, err * 0.003 + drift));
                rate += (want - rate) * 0.05;
                int frames = count / 2;
                for (int f = 0; f < frames; f++)
                {
                    while ((int)pos + 1 >= inLen) if (!Pull()) { Silence(buffer, offset, f * 2, count); filling = true; Gaps++; if ((DateTime.UtcNow - gapLog).TotalSeconds > 10) { gapLog = DateTime.UtcNow; App.LogInfo("Przerwa w dźwięku: zapas pusty (z kabla przyszło za mało), przerw od startu: " + Gaps); } return count; }   // kabel milczy - cisza i ponowne zebranie zapasu
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
                    App.LogInfo($"Pomiar 30 s: z kabla {pct:0.00}% próbek (100% = bez strat), najdłuższa przerwa odbioru {capMax:0} ms ({capLate}× >60 ms), odtwarzania {outMax:0} ms ({outLate}× >120 ms), przetwarzanie najdłużej {procMax:0.0} ms");
                    start = now; samples = 0; capMax = outMax = procMax = 0; capLate = outLate = 0;
                }
            }
            public static void Out() { long now = System.Diagnostics.Stopwatch.GetTimestamp(); if (outLast != 0) { double g = (now - outLast) * tick; if (g > outMax) outMax = g; if (g > 120) outLate++; } outLast = now; }
            public static void Proc(long t) { double ms = t * tick; if (ms > procMax) procMax = ms; }
        }

        // przetwarzanie w porcjach od głośników: dźwięk → Audio Balance (w miejscu)
        sealed class Processing : ISampleProvider
        {
            readonly ISampleProvider src; readonly AbProcessor proc; float[] tmp = new float[9600];
            public Processing(ISampleProvider s, AbProcessor p) { src = s; proc = p; }
            public WaveFormat WaveFormat => src.WaveFormat;
            public int Read(float[] buffer, int offset, int count)
            { RtThread();
                int n = src.Read(buffer, offset, count); if (n < 2) return n;
                n -= n & 1;
                if (tmp.Length < n) tmp = new float[n];
                // kopiowanie próbka po próbce - NAudio podaje tu tablicę bajtów „udającą” float[] (WaveBuffer); Array.Copy jej odmawia („Source array type cannot be assigned…”)
                for (int i = 0; i < n; i++) tmp[i] = buffer[offset + i];
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); Diag.Out();
                try { proc.Process(tmp, n / 2); } catch (Exception ex) { App.LogError(ex); return n; }   // błąd przetwarzania - dźwięk bez zmian, nie cisza
                for (int i = 0; i < n; i++) buffer[offset + i] = tmp[i];
                Diag.Proc(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                return n;
            }
        }

        // 5.1 / 7.1 → stereo (centrum i tylne -3 dB, LFE pominięty)
        sealed class Downmix : ISampleProvider
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
