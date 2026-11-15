using System;
using System.IO.MemoryMappedFiles;

namespace Velivo
{
    // 🔗 wspólny „widok” Audio Balance for Windows → Velivo: wskaźniki i widmo programu samodzielnego w pamięci współdzielonej (bez plików, bez sieci).
    //    Program samodzielny zapisuje (wątek dźwięku, kilka kB na porcję), Velivo czyta i pokazuje na swoich wykresach jego pracę. Ten sam plik w obu programach.
    static unsafe class AbWidok
    {
        public const string Name = @"Local\VelivoAudioBalance.Widok";
        public const int NV = 20, Ring = 65536;
        const int OffV = 64, OffS = 1024, Size = OffS + Ring * 4 * 2;
        // wartości: 0 rawM 1 rawS 2 rawG10 3 outM 4 outS 5 outG10 6 outI 7 gain 8 limit 9 peak 10 eff 11 target 12 vuL 13 vuR 14 vuInL 15 vuInR 16 preGR 17 profile(1/0) 18 bypass 19 delayMs
        static MemoryMappedFile mmf; static MemoryMappedViewAccessor acc; static byte* p;

        // --- program samodzielny ---
        static int wPos;
        public static void Write(AbProcessor pr, double profileOn, double delayMs)
        {
            try
            {
                if (p == null) { mmf = MemoryMappedFile.CreateOrOpen(Name, Size); acc = mmf.CreateViewAccessor(); acc.SafeMemoryMappedViewHandle.AcquirePointer(ref p); }
                var v = (double*)(p + OffV);
                v[0] = pr.Raw.Momentary; v[1] = pr.Raw.ShortTerm; v[2] = pr.Raw.Gated10; v[3] = pr.Out.Momentary; v[4] = pr.Out.ShortTerm; v[5] = pr.Out.Gated10; v[6] = pr.Out.Integrated;
                v[7] = pr.GainDb; v[8] = pr.LimitDb; v[9] = pr.PeakOutDb; v[10] = pr.EffTarget; v[11] = pr.Target; v[12] = pr.VuL; v[13] = pr.VuR; v[14] = pr.VuInL; v[15] = pr.VuInR;
                v[16] = pr.Pre.Params.IsEnabled ? pr.Pre.CurrentGainReductionDb : 0; v[17] = profileOn; v[18] = pr.Bypass ? 1 : 0; v[19] = delayMs;
                int end = pr.ScopePos; var s = (float*)(p + OffS); var so = s + Ring;
                for (int i = wPos; i != end; i = (i + 1) & (Ring - 1)) { s[i] = pr.Scope[i]; so[i] = pr.ScopeOut[i]; }
                wPos = end; *(int*)(p + 8) = end; *(long*)p = DateTime.UtcNow.Ticks;   // znak życia na końcu
            }
            catch (Exception) { }
        }

#if !AB_SAMODZIELNY
        // --- Velivo ---
        static DateTime rTry;
        /// <summary>Świeże dane (do 1 s) – kopiuje do pr (tylko do wyświetlania). false = program samodzielny nie gra.</summary>
        public static bool Read(AbProcessor pr, out double profileOn, out double bypass)
        {
            profileOn = bypass = 0;
            try
            {
                if (p == null)
                {
                    if ((DateTime.UtcNow - rTry).TotalSeconds < 2) return false; rTry = DateTime.UtcNow;
                    mmf = MemoryMappedFile.OpenExisting(Name, MemoryMappedFileRights.Read); acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read); acc.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                }
                if ((DateTime.UtcNow.Ticks - *(long*)p) > TimeSpan.TicksPerSecond) return false;
                var v = (double*)(p + OffV); var a = new double[NV]; for (int i = 0; i < NV; i++) a[i] = v[i];
                int end = *(int*)(p + 8); var s = (float*)(p + OffS); var so = s + Ring;
                for (int k = 0; k < 4096; k++) { int i = (end - 4096 + k) & (Ring - 1); pr.Scope[i] = s[i]; pr.ScopeOut[i] = so[i]; }
                pr.Mirror(a, end); profileOn = a[17]; bypass = a[18];
                return true;
            }
            catch (Exception) { p = null; return false; }
        }
#endif
    }
}
