using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Velivo
{
    // 🎧 odsłuch utworu przez Audio Balance: plik gra na „CABLE Input”, więc słychać go dokładnie z bieżącymi poprawkami (kompresor, LUFS, korektor, VST3)
    sealed class AbOdsluch : IDisposable
    {
        public readonly string File; readonly MediaFoundationReader rd; readonly WasapiOut o;
        public AbOdsluch(string file)
        {
            File = file; rd = new MediaFoundationReader(file);
            MMDevice dev = null; var id = AbEngine.CableInputId();
            try { if (id != null) dev = new MMDeviceEnumerator().GetDevice(id); } catch (Exception) { }
            o = dev != null ? new WasapiOut(dev, AudioClientShareMode.Shared, false, 200) : new WasapiOut(AudioClientShareMode.Shared, false, 200);   // odpytywanie i 200 ms zapasu - duże pliki i filmy (dekodowanie) bez przycięć
            o.Init(rd);
        }
        public bool Playing => o.PlaybackState == PlaybackState.Playing;
        public TimeSpan Pos { get => rd.CurrentTime; set { try { rd.CurrentTime = value; } catch (Exception) { } } }
        public TimeSpan Len => rd.TotalTime;
        public void Toggle() { if (Playing) o.Pause(); else o.Play(); }
        public void Stop() { o.Stop(); Pos = TimeSpan.Zero; }
        public event Action Ended; bool hooked;
        public void HookEnd() { if (hooked) return; hooked = true; o.PlaybackStopped += (s, e) => Ended?.Invoke(); }
        public void Dispose() { try { o.Dispose(); } catch (Exception) { } try { rd.Dispose(); } catch (Exception) { } }
    }
}
