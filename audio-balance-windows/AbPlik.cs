using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Velivo
{
    // 🎵 poprawianie pliku (WAV, MP3, FLAC, M4A…): te same ustawienia co dźwięk Windows (wyrównanie, kompresor, korektor, dialogi), bez wtyczek VST3;
    //    wynik: „nazwa (Audio Balance).wav” obok oryginału, 32-bit float 48 kHz stereo (bez strat); liczone szybciej niż czas trwania
    static class AbPlik
    {
        public static Task<string> Process(string src, string outDir, Action<AbProcessor> configure, Action<double> progress) => Task.Run(() =>
        {
            using var rd = new MediaFoundationReader(src);   // dekodery Windows: WAV, MP3, AAC/M4A, FLAC, WMA
            ISampleProvider sp = rd.ToSampleProvider();
            if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
            else if (sp.WaveFormat.Channels > 2) sp = new AbEngine.Downmix(sp);
            if (sp.WaveFormat.SampleRate != AbProcessor.Fs) sp = new WdlResamplingSampleProvider(sp, AbProcessor.Fs);
            var pr = new AbProcessor { NoVst = true }; configure(pr); pr.NewMedia();
            var dst = Path.Combine(string.IsNullOrEmpty(outDir) || !Directory.Exists(outDir) ? Path.GetDirectoryName(src)! : outDir, Path.GetFileNameWithoutExtension(src) + " (Audio Balance).wav");
            using var w = new WaveFileWriter(dst, WaveFormat.CreateIeeeFloatWaveFormat(AbProcessor.Fs, 2));
            var buf = new float[AbProcessor.Fs / 10 * 2]; long total = Math.Max(1, rd.Length), last = 0;
            int n;
            while ((n = sp.Read(buf, 0, buf.Length)) > 0)
            {
                n -= n & 1; pr.Process(buf, n / 2); w.WriteSamples(buf, 0, n);
                if (rd.Position - last > total / 100) { last = rd.Position; progress(Math.Min(1, rd.Position / (double)total)); }
            }
            return dst;
        });
    }
}
