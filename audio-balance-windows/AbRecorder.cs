using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace Velivo
{
    // ⏺ nagranie dźwięku po poprawkach Audio Balance: WAV 32-bit float, 48 kHz, stereo - dokładnie to, co idzie do głośników (bez ponownego kodowania, najwyższa jakość)
    //    wątek dźwięku tylko kopiuje porcję do kolejki; zapis na dysk w osobnym wątku (bez zacięć)
    sealed class AbRecorder : IDisposable
    {
        public readonly string Path; public long Frames; public readonly DateTime Start = DateTime.Now;
        readonly BlockingCollection<float[]> q = new BlockingCollection<float[]>(2000); readonly Thread th; WaveFileWriter w;
        public string Error;
        public AbRecorder(string dir)
        {
            Directory.CreateDirectory(dir);
            Path = System.IO.Path.Combine(dir, "Audio Balance " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".wav");
            w = new WaveFileWriter(Path, WaveFormat.CreateIeeeFloatWaveFormat(AbProcessor.Fs, 2));
            th = new Thread(() =>
            {
                try { foreach (var b in q.GetConsumingEnumerable()) w.WriteSamples(b, 0, b.Length); }
                catch (Exception ex) { Error = ex.Message; App.LogError(ex); }
                finally { try { w.Dispose(); } catch (Exception) { } }
            }) { IsBackground = true, Name = "Nagrywanie WAV" };
            th.Start();
        }
        public void Add(float[] buf, int off, int n)
        {
            if (q.IsAddingCompleted) return;
            var c = new float[n]; for (int i = 0; i < n; i++) c[i] = buf[off + i];   // element po elemencie (NAudio może podać WaveBuffer)
            if (q.TryAdd(c)) Interlocked.Add(ref Frames, n / 2);
        }
        public void Dispose() { q.CompleteAdding(); th.Join(5000); }
    }
}
