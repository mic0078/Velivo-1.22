using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Velivo
{
    // 🗣 lektor prosto na słuchawki / głośniki, z pominięciem kabla Audio Balance for Windows: tamten program widzi sam dźwięk filmu
    // (wcześniej głos lektora wchodził w pomiar i wyrównanie). Urządzenie i głośność Windows podaje Audio Balance (lektor-wyjscie.txt).
    static class LektorWprost
    {
        [StructLayout(LayoutKind.Sequential)] struct WaveFormat { public ushort Tag, Channels; public uint Rate, BytesPerSec; public ushort Align, Bits, Size; }
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, uint dev, ref WaveFormat f, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
        static int HdrSize => IntPtr.Size == 8 ? 48 : 32;
        static int FlagsOff => IntPtr.Size == 8 ? 24 : 16;

        static string Plik => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velivo", "Audio Balance for Windows", "lektor-wyjscie.txt");

        // urządzenie (numer waveOut) i głośność Windows z Audio Balance; false = nie wiadomo (stara wersja programu, brak pliku, urządzenie odłączone)
        internal static bool Cel(out uint dev, out double vol)
        {
            dev = 0; vol = 1;
            try
            {
                if (!System.IO.File.Exists(Plik)) return false;
                var p = System.IO.File.ReadAllText(Plik).Split('\t');
                if (p.Length < 2 || p[0].Length == 0) return false;
                double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out vol);
                vol = Math.Max(0, Math.Min(1, vol));
                var l = AbOutput.Devices();   // waveOut ucina nazwę do 31 znaków
                for (int i = 0; i < l.Count; i++) if (l[i].Length > 0 && p[0].StartsWith(l[i], StringComparison.OrdinalIgnoreCase)) { dev = (uint)i; return true; }
            }
            catch (Exception) { }
            return false;
        }

        // gra mono 48 kHz (z LectorWavPcm48) ze wzmocnieniem; kończy się z końcem dźwięku albo przy anulowaniu (przewinięcie, następna kwestia)
        internal static async Task<bool> Graj(float[] s, double gain, uint dev, CancellationToken ct)
        {
            var f = new WaveFormat { Tag = 1, Channels = 2, Rate = 48000, Bits = 16, Align = 4, BytesPerSec = 48000 * 4 };
            if (waveOutOpen(out var h, dev, ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0) return false;
            int bytes = s.Length * 4; IntPtr mem = Marshal.AllocHGlobal(bytes), hdr = Marshal.AllocHGlobal(HdrSize);
            try
            {
                var pcm = new short[s.Length * 2];
                for (int i = 0; i < s.Length; i++) { double v = s[i] * gain; v = v > 1 ? 1 : v < -1 ? -1 : v; pcm[2 * i] = pcm[2 * i + 1] = (short)(v * 32767); }
                Marshal.Copy(pcm, 0, mem, pcm.Length);
                for (int i = 0; i < HdrSize; i++) Marshal.WriteByte(hdr, i, 0);
                Marshal.WriteIntPtr(hdr, 0, mem); Marshal.WriteInt32(hdr, IntPtr.Size, bytes);
                if (waveOutPrepareHeader(h, hdr, HdrSize) != 0) return false;
                waveOutWrite(h, hdr, HdrSize);
                while ((Marshal.ReadInt32(hdr, FlagsOff) & 1) == 0)   // WHDR_DONE = cały bufor zagrany
                {
                    if (ct.IsCancellationRequested) { waveOutReset(h); break; }
                    await Task.Delay(30);
                }
                waveOutUnprepareHeader(h, hdr, HdrSize);
                return !ct.IsCancellationRequested;
            }
            finally { waveOutReset(h); waveOutClose(h); Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(mem); }
        }
    }
}
