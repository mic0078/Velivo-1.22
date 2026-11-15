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

        static bool IsCable(string name) => name != null && name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0;
        public static bool CableInstalled() { try { return FindCable() != null; } catch (Exception) { return false; } }
        static MMDevice FindCable()
        {
            var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) if (d.FriendlyName.IndexOf("CABLE Output", StringComparison.OrdinalIgnoreCase) >= 0) return d;
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
            try { var en = new MMDeviceEnumerator(); foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) if (d.FriendlyName.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0) return d.ID; } catch (Exception) { }
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
                cap = new WasapiCapture(cable, true, 20);
                buf = new BufferedWaveProvider(cap.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(600), ReadFully = true };
                var cf = cap.WaveFormat;
                cap.DataAvailable += (s, e) =>
                {
                    buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    // zegary kabla i głośników nie chodzą idealnie razem - nadmiar (ponad 200 ms) odrzucany, opóźnienie nie rośnie
                    if (buf.BufferedDuration.TotalMilliseconds > 200) { int drop = cf.AverageBytesPerSecond / 1000 * 100 / cf.BlockAlign * cf.BlockAlign; var junk = new byte[drop]; buf.Read(junk, 0, drop); }
                };
                cap.RecordingStopped += (s, e) => { if (Running) { if (e.Exception != null) App.LogError(e.Exception); Error = L.T("odbiór z CABLE Output przerwany") + (e.Exception != null ? ": " + e.Exception.Message : ""); Running = false; Stopped?.Invoke(); } };
                ISampleProvider sp = buf.ToSampleProvider();
                if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
                else if (sp.WaveFormat.Channels > 2) sp = new Downmix(sp);
                if (sp.WaveFormat.SampleRate != AbProcessor.Fs) sp = new WdlResamplingSampleProvider(sp, AbProcessor.Fs);
                outp = new WasapiOut(dev, AudioClientShareMode.Shared, true, 40);
                outp.Init(new Processing(sp, Proc));
                outp.PlaybackStopped += (s, e) => { if (Running) { if (e.Exception != null) App.LogError(e.Exception); Error = L.T("odtwarzanie na głośnikach przerwane") + (e.Exception != null ? ": " + e.Exception.Message : ""); Running = false; Stopped?.Invoke(); } };
                Proc.NewMedia(); Running = true;
                cap.StartRecording(); outp.Play();
                App.LogInfo("Audio Balance: CABLE Output (" + cf + ") → " + OutputName);
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

        // przetwarzanie w porcjach od głośników: dźwięk → Audio Balance (w miejscu)
        sealed class Processing : ISampleProvider
        {
            readonly ISampleProvider src; readonly AbProcessor proc; float[] tmp = new float[9600];
            public Processing(ISampleProvider s, AbProcessor p) { src = s; proc = p; }
            public WaveFormat WaveFormat => src.WaveFormat;
            public int Read(float[] buffer, int offset, int count)
            {
                int n = src.Read(buffer, offset, count); if (n < 2) return n;
                n -= n & 1;
                if (tmp.Length < n) tmp = new float[n];
                Array.Copy(buffer, offset, tmp, 0, n);
                try { proc.Process(tmp, n / 2); } catch (Exception ex) { App.LogError(ex); return n; }   // błąd przetwarzania - dźwięk bez zmian, nie cisza
                Array.Copy(tmp, 0, buffer, offset, n);
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
