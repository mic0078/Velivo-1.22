using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Przegladarka
{
    // Lektor w odtwarzaczu filmow: czyta napisy na glos w chwili ich pojawienia sie (jak lektor w TV), film na ten czas
    // sciszony. Napisy z pliku obok filmu (film.srt, film.pl.srt, .ass/.ssa/.vtt) albo wczytane recznie. Glos: Piper (wybrany
    // w czytaniu na glos), inaczej glos Windows w jezyku napisow. Przy szybkich dialogach przyspiesza, zalegle kwestie pomija.
    public partial class MainWindow
    {
        internal sealed class SubCue { public long Start, End; public string Text; }

        static readonly Regex SrtTime = new Regex(@"(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})\s*-->\s*(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})");
        static readonly Regex VttShortTime = new Regex(@"^(\d{2}):(\d{2})[.](\d{3})\s*-->\s*(\d{2}):(\d{2})[.](\d{3})");
        static readonly Regex SubTags = new Regex(@"<[^>]+>|\{[^}]*\}");

        static long Ms(string h, string m, string s, string frac)
        {
            var f = frac.PadRight(3, '0').Substring(0, 3);
            return ((long.Parse(h) * 60 + long.Parse(m)) * 60 + long.Parse(s)) * 1000 + long.Parse(f);
        }

        static string CleanSub(string t)
        {
            t = SubTags.Replace(t ?? "", "").Replace("\\N", " ").Replace("\\n", " ").Replace("\\h", " ");
            t = Regex.Replace(t, @"^\s*-\s*", "", RegexOptions.Multiline);   // myslnik dialogu na poczatku wiersza
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        // SRT / VTT / ASS-SSA -> lista kwestii posortowana po czasie
        internal static List<SubCue> ParseSubtitles(string content)
        {
            var cues = new List<SubCue>();
            var lines = (content ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lines.Any(l => l.TrimStart().StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)))
            {
                // ASS: Dialogue: warstwa,start,koniec,styl,nazwa,ml,mr,mv,efekt,tekst (tekst moze zawierac przecinki)
                foreach (var l in lines)
                {
                    var t = l.TrimStart();
                    if (!t.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
                    var p = t.Substring(9).Split(new[] { ',' }, 10);
                    if (p.Length < 10) continue;
                    var a = Regex.Match(p[1].Trim(), @"^(\d+):(\d{2}):(\d{2})[.](\d{1,3})$");
                    var b = Regex.Match(p[2].Trim(), @"^(\d+):(\d{2}):(\d{2})[.](\d{1,3})$");
                    if (!a.Success || !b.Success) continue;
                    long fa = long.Parse(a.Groups[4].Value) * (a.Groups[4].Value.Length == 2 ? 10 : 1), fb = long.Parse(b.Groups[4].Value) * (b.Groups[4].Value.Length == 2 ? 10 : 1);
                    var text = CleanSub(p[9]);
                    if (text.Length > 0) cues.Add(new SubCue { Start = Ms(a.Groups[1].Value, a.Groups[2].Value, a.Groups[3].Value, "0") + fa, End = Ms(b.Groups[1].Value, b.Groups[2].Value, b.Groups[3].Value, "0") + fb, Text = text });
                }
            }
            else
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    long start, end;
                    var m = SrtTime.Match(lines[i]);
                    if (m.Success) { start = Ms(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value); end = Ms(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value); }
                    else
                    {
                        var v = VttShortTime.Match(lines[i]);   // VTT bez godzin: mm:ss.mmm
                        if (!v.Success) continue;
                        start = Ms("0", v.Groups[1].Value, v.Groups[2].Value, v.Groups[3].Value); end = Ms("0", v.Groups[4].Value, v.Groups[5].Value, v.Groups[6].Value);
                    }
                    var sb = new StringBuilder();
                    for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++) sb.Append(lines[i]).Append('\n');
                    var text = CleanSub(sb.ToString());
                    if (text.Length > 0) cues.Add(new SubCue { Start = start, End = end, Text = text });
                }
            }
            return cues.OrderBy(c => c.Start).ToList();
        }

        // polskie napisy czesto sa w Windows-1250 (stare SRT) - UTF-8 tylko, gdy plik jest poprawnym UTF-8
        internal static string ReadSubtitleFile(string path)
        {
            var bytes = File.ReadAllBytes(path);
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1250).GetString(bytes);
            }
        }

        // napisy obok filmu: film.srt, film.pl.srt, film.PL.ass ... (najpierw polskie)
        internal static string FindSubtitleFor(string video)
        {
            try
            {
                var dir = Path.GetDirectoryName(video); var name = Path.GetFileNameWithoutExtension(video);
                var all = Directory.EnumerateFiles(dir, name + "*").Where(f => SubtitleExts.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
                return all.OrderBy(f => Regex.IsMatch(Path.GetFileName(f), @"[._-](pl|pol|polish)[._-]", RegexOptions.IgnoreCase) ? 0 : 1).ThenBy(f => f.Length).FirstOrDefault();
            }
            catch (Exception) { return null; }
        }

        internal static string SubtitleLanguage(IEnumerable<SubCue> cues)
        {
            var sample = string.Join(" ", cues.Take(80).Select(c => c.Text)).ToLowerInvariant();
            int pl = sample.Count(ch => "ąćęłńóśźż".IndexOf(ch) >= 0) + Regex.Matches(sample, @"\b(nie|jest|się|to|że|tak|co|jak)\b").Count;
            int en = Regex.Matches(sample, @"\b(the|you|and|is|what|it|that|to)\b").Count;
            return pl >= en ? "pl" : "en";
        }

        // ktora kwestia powinna byc czytana w chwili t (ms); -1 = zadna
        internal static int CueAt(List<SubCue> cues, long t)
        {
            int lo = 0, hi = cues.Count - 1, found = -1;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (cues[mid].Start <= t) { found = mid; lo = mid + 1; } else hi = mid - 1; }
            return found >= 0 && t < cues[found].End ? found : -1;
        }

        // ---------- silnik lektora (jeden na okno odtwarzacza) ----------

        sealed class Lector
        {
            public List<SubCue> Cues = new List<SubCue>();
            public string Lang = "pl", File;
            public int LastSpoken = -1;
            public long LastTime;
            public bool Busy;
            public System.Windows.Media.MediaPlayer Voice;
            public int Ducked = -1;   // glosnosc filmu przed sciszeniem
        }

        // syntezuje kwestie: Piper (jezyk zgodny z napisami, jesli jest taki glos), inaczej glos Windows
        async Task<string> LectorSynthesizeAsync(string text, string lang, double rate)
        {
            var piper = SelectedPiperVoice(_settings.ReadVoice);
            if (piper != null && piper.Lang != lang) piper = PiperVoices.FirstOrDefault(v => v.Lang == lang && PiperVoiceInstalled(v)) ?? piper;
            if (piper != null && PiperVoiceInstalled(piper)) return await PiperSynthesizeAsync(piper, text, rate);
            try
            {
                var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
                var voice = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase));
                if (voice != null) synth.Voice = voice;
                synth.Options.SpeakingRate = Math.Max(0.5, Math.Min(3.0, rate));
                using (var stream = await synth.SynthesizeTextToStreamAsync(text))
                {
                    Directory.CreateDirectory(PiperOutDir);
                    var path = Path.Combine(PiperOutDir, "lektor-" + Guid.NewGuid().ToString("N") + ".wav");
                    using (var fs = System.IO.File.Create(path)) await stream.AsStreamForRead().CopyToAsync(fs);
                    return path;
                }
            }
            catch (Exception ex) { App.LogError(ex); return null; }
        }

        void LectorStopVoice(Lector lc, LibVLCSharp.Shared.MediaPlayer mp)
        {
            if (lc.Voice != null) { try { lc.Voice.Stop(); lc.Voice.Close(); } catch (Exception) { } lc.Voice = null; }
            if (lc.Ducked >= 0) { try { mp.Volume = lc.Ducked; } catch (Exception) { } lc.Ducked = -1; }
        }

        // co ~120 ms: nowa kwestia na ekranie -> czytamy ja (wolny lektor = pomijamy zalegle, czytamy biezaca)
        async void LectorTick(Lector lc, LibVLCSharp.Shared.MediaPlayer mp, Func<int> volume)
        {
            if (lc.Cues.Count == 0) return;
            if (!mp.IsPlaying) { if (lc.Voice != null) LectorStopVoice(lc, mp); return; }
            long t = mp.Time - mp.SpuDelay / 1000;   // przesuniecie napisow (menu) dotyczy tez lektora
            if (Math.Abs(t - lc.LastTime) > 3000) { LectorStopVoice(lc, mp); lc.LastSpoken = CueAt(lc.Cues, t) - 1; if (lc.LastSpoken < -1) lc.LastSpoken = -1; }   // przewiniecie
            lc.LastTime = t;
            int i = CueAt(lc.Cues, t);
            if (i < 0 || i <= lc.LastSpoken || lc.Busy) return;
            lc.LastSpoken = i;
            var cue = lc.Cues[i];
            lc.Busy = true;
            try
            {
                // tempo: zwykle jak w czytaniu na glos; dluga kwestia w krotkim czasie - szybciej (do 1,8x)
                double secs = Math.Max(0.6, (cue.End - t) / 1000.0);
                double need = cue.Text.Length / 15.0 / secs;
                double rate = Math.Round(Math.Min(1.8, Math.Max(_settings.ReadRate, need)) * 4) / 4;   // stopnie co 0,25 - kazde inne tempo to ponowne uruchomienie Pipera
                var wav = await LectorSynthesizeAsync(cue.Text, lc.Lang, rate);
                if (wav == null || !mp.IsPlaying || mp.Time - mp.SpuDelay / 1000 > cue.End + 1500) return;   // za pozno - pomijamy
                LectorStopVoice(lc, mp);
                lc.Ducked = volume(); mp.Volume = (int)(lc.Ducked * 0.35);   // film ciszej, jak lektor w TV
                var done = new TaskCompletionSource<bool>();
                var voice = new System.Windows.Media.MediaPlayer { Volume = Math.Max(0.2, _settings.ReadVolume) };
                lc.Voice = voice;
                voice.MediaEnded += (s, e) => done.TrySetResult(true);
                voice.MediaFailed += (s, e) => done.TrySetResult(false);
                voice.Open(new Uri(wav)); voice.Play();
                await Task.WhenAny(done.Task, Task.Delay(30000));
                if (lc.Voice == voice) LectorStopVoice(lc, mp);
                try { System.IO.File.Delete(wav); } catch (Exception) { }
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { lc.Busy = false; }
        }
    }
}
