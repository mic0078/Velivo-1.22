using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Velivo
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
        internal static string ReadSubtitleFile(string path) { return SubtitleText(File.ReadAllBytes(path)); }

        // napisy obok filmu: film.srt, film.pl.srt, film.PL.ass ... (najpierw polskie)
        // wszystkie pliki napisow obok filmu (film.srt, film.pl.srt, film.en.srt...)
        internal static List<string> SubtitleFilesFor(string video)
        {
            try
            {
                var dir = Path.GetDirectoryName(video); var name = Path.GetFileNameWithoutExtension(video);
                return Directory.EnumerateFiles(dir, name + "*").Where(f => SubtitleExts.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f).ToList();
            }
            catch (Exception) { return new List<string>(); }
        }

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
        // po przewinięciu: ostatnia „już wypowiedziana” kwestia - bieżąca na ekranie będzie czytana, starsze nie
        internal static int LectorSyncIdx(List<SubCue> cues, long t)
        {
            int on = CueAt(cues, t);
            if (on >= 0) return on - 1;
            int lo = 0, hi = cues.Count - 1, found = -1;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (cues[mid].Start <= t) { found = mid; lo = mid + 1; } else hi = mid - 1; }
            return found;
        }

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
            public long LastTime = long.MinValue;
            public long LastWall;   // Environment.TickCount64 przy LastTime
            public int PreIdx = -1; public Task<string> PreWav;   // nastepna kwestia liczona z wyprzedzeniem (lektor gotowy, gdy napis sie pojawi)
            public int CurIdx = -1;   // czytana teraz kwestia (kolejka: przerwana pauzą wraca do kolejki)
            public long PauseT = long.MinValue;   // czas filmu przy pauzie (kolejka: wznowienie w tym samym miejscu nie gubi zaległych)
            public int Gen;   // zmienia sie przy przewinieciu / pauzie - spozniona synteza jest wyrzucana
            public bool Busy;
            public System.Windows.Media.MediaPlayer Voice;
            public TaskCompletionSource<bool> VoiceDone;   // zatrzymanie glosu konczy czekanie (inaczej Busy do 30 s po przewinieciu)
            public int Ducked = -1;   // glosnosc filmu przed sciszeniem
        }

        // syntezuje kwestie: Piper (jezyk zgodny z napisami, jesli jest taki glos), inaczej glos Windows
        readonly HashSet<string> _lectorFetchTried = new HashSet<string>();   // pobieranie głosu lektora - raz na uruchomienie (bez powtórek po błędzie)

        async Task<string> LectorSynthesizeAsync(string text, string lang, double rate)
        {
            // glos wybrany w panelu lektora; "automatycznie" = Supertonic M1 (meski) w jezyku napisow
            var tag = _settings.VlcLectorVoice ?? "";
            var piper = tag.StartsWith("piper:") ? SelectedPiperVoice(tag) : tag.StartsWith("win:") ? null : SelectedPiperVoice("piper:st3-M1-pl");
            if (piper?.St != null && piper.Lang != lang) piper = PiperVoices.FirstOrDefault(v => v.St == piper.St && v.Lang == lang) ?? new PiperVoice { Id = piper.Id, Label = piper.Label, St = piper.St, Lang = lang };   // Supertonic: ten sam głos w języku napisów
            if (piper != null && !PiperVoiceInstalled(piper) && !_piperDownloading && !_stDownloading && _lectorFetchTried.Add(piper.Id)) _ = EnsurePiperAsync(piper);   // domyślny głos lektora pobiera się sam; do tego czasu głos Windows
            if (piper != null && PiperVoiceInstalled(piper)) return await PiperSynthesizeAsync(piper, text, rate, _settings.VlcLectorSteps);
            try
            {
                var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
                var voice = (tag.StartsWith("win:") ? Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices.FirstOrDefault(v => "win:" + v.DisplayName == tag) : null)
                    ?? Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase));
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

        // tempo z dlugosci kwestii na ekranie (nie z chwili startu) - ta sama synteza nadaje sie do liczenia z wyprzedzeniem
        Task<string> LectorSynth(Lector lc, SubCue cue)
        {
            double secs = Math.Max(0.6, (cue.End - cue.Start) / 1000.0);
            double need = cue.Text.Length / 15.0 / secs;
            double rate = Math.Round(Math.Min(Math.Max(2.0, _settings.VlcLectorRate), Math.Max(_settings.VlcLectorRate, need)) * 10) / 10;   // stopnie co 0,1 - kazde inne tempo to ponowne uruchomienie Pipera
            return LectorSynthesizeAsync(cue.Text, lc.Lang, rate);
        }

        static long LectorWavMs(string wav)
        {
            try { using (var fs = System.IO.File.OpenRead(wav)) { var h = new byte[44]; if (fs.Read(h, 0, 44) < 44) return 0; int bps = BitConverter.ToInt32(h, 28); return bps > 0 ? (fs.Length - 44) * 1000 / bps : 0; } }
            catch (Exception) { return 0; }
        }

        static void LectorDropPre(Lector lc)
        {
            var p = lc.PreWav; lc.PreWav = null; lc.PreIdx = -1;
            p?.ContinueWith(t => { try { if (t.Status == TaskStatus.RanToCompletion && t.Result != null) System.IO.File.Delete(t.Result); } catch (Exception) { } });
        }

        // odtwarzacz Windows (WPF MediaPlayer): przy 1,0 gra CISZEJ niż przy 0,99 (sprawdził właściciel - lektor i czytnik) - górna granica 0,99
        internal static double WpfVol(double v) => Math.Max(0, Math.Min(0.99, v));

        void LectorStopVoice(Lector lc, LibVLCSharp.Shared.MediaPlayer mp)
        {
            if (lc.Voice != null) { try { lc.Voice.Stop(); lc.Voice.Close(); } catch (Exception) { } lc.Voice = null; }
            lc.VoiceDone?.TrySetResult(false);
            if (lc.Ducked >= 0) { try { mp.Volume = lc.Ducked; } catch (Exception) { } lc.Ducked = -1; }
        }

        // co ~120 ms: nowa kwestia na ekranie -> czytamy ja (wolny lektor = pomijamy zalegle, czytamy biezaca)
        async void LectorTick(Lector lc, LibVLCSharp.Shared.MediaPlayer mp, Func<int> volume)
        {
            if (lc.Cues.Count == 0) return;
            bool queue = _settings.VlcLectorQueue;   // kolejka: czyta wszystko po kolei, nawet z opóźnieniem
            bool ended = false; try { ended = mp.State == LibVLCSharp.Shared.VLCState.Ended; } catch (Exception) { }
            long t = ended ? long.MaxValue / 2 : mp.Time - mp.SpuDelay / 1000;   // jeden zegar: czas filmu VLC z przesunieciem napisow (to samo co napisy); koniec filmu = kolejka do konca
            long now = Environment.TickCount64;
            LibVLCSharp.Shared.VLCState st = LibVLCSharp.Shared.VLCState.NothingSpecial; try { st = mp.State; } catch (Exception) { }
            if (!mp.IsPlaying && (st == LibVLCSharp.Shared.VLCState.Buffering || st == LibVLCSharp.Shared.VLCState.Opening)) return;   // chwilowe buforowanie filmu to nie pauza - lektor czyta dalej
            if (!mp.IsPlaying && !(queue && ended))
            {
                if (lc.Voice != null || lc.Busy)
                {
                    LectorStopVoice(lc, mp); lc.Gen++; LectorDropPre(lc);
                    if (queue && lc.CurIdx >= 0) lc.LastSpoken = Math.Min(lc.LastSpoken, lc.CurIdx - 1);   // przerwana kwestia wraca do kolejki
                }
                if (lc.LastTime != long.MinValue)
                {
                    lc.LastTime = long.MinValue; lc.PauseT = t;
                    if (!queue) lc.LastSpoken = LectorSyncIdx(lc.Cues, t);   // pauza: po wznowieniu biezaca kwestia od nowa
                }
                return;
            }
            float speed = 1f; try { speed = mp.Rate; } catch (Exception) { }
            long expected = lc.LastTime == long.MinValue ? t : lc.LastTime + (long)((now - lc.LastWall) * speed);
            bool resumed = lc.LastTime == long.MinValue && queue && lc.PauseT != long.MinValue && Math.Abs(t - lc.PauseT) <= 700;   // wznowienie w tym samym miejscu - zalegla kolejka zostaje
            if (!ended && !resumed && (lc.LastTime == long.MinValue || Math.Abs(t - expected) > (queue ? 3000 : 700)))   // przewiniecie w przod/tyl (takze male i suwakiem) albo start - kolejka od biezacego miejsca
            {
                if (lc.LastTime != long.MinValue) { LectorStopVoice(lc, mp); lc.Gen++; LectorDropPre(lc); }
                lc.LastSpoken = LectorSyncIdx(lc.Cues, t);
            }
            lc.PauseT = long.MinValue;
            lc.LastTime = t; lc.LastWall = now;
            int i = queue ? (lc.LastSpoken + 1 < lc.Cues.Count && lc.Cues[lc.LastSpoken + 1].Start <= t ? lc.LastSpoken + 1 : -1) : CueAt(lc.Cues, t);   // kolejka: następna niewypowiedziana, która już się pojawiła
            if (i < 0 && lc.PreWav == null && !lc.Busy)
            {   // przerwa miedzy napisami: najblizsza kwestia liczona juz teraz (po starcie i po przewinieciu)
                int j = lc.Cues.FindIndex(c => c.Start > t);
                if (j >= 0 && lc.Cues[j].Start - t < 15000) { lc.PreIdx = j; lc.PreWav = LectorSynth(lc, lc.Cues[j]); }
            }
            if (i < 0 || i <= lc.LastSpoken || lc.Busy) return;
            lc.LastSpoken = i;
            var cue = lc.Cues[i];
            int gen = lc.Gen;
            lc.Busy = true; lc.CurIdx = i;
            try
            {
                // tempo: ustawione w panelu lektora; dluga kwestia w krotkim czasie - szybciej (do 2x)
                var wavTask = lc.PreIdx == i ? lc.PreWav : null;
                if (wavTask == null) { LectorDropPre(lc); wavTask = LectorSynth(lc, cue); } else { lc.PreIdx = -1; lc.PreWav = null; }
                if (i + 1 < lc.Cues.Count) { lc.PreIdx = i + 1; lc.PreWav = LectorSynth(lc, lc.Cues[i + 1]); }   // nastepna kwestia w tle, gdy ta gra - bez czekania na synteze
                var wav = await wavTask;
                if (wav == null || gen != lc.Gen || !mp.IsPlaying && !(queue && ended)) { if (wav != null) try { System.IO.File.Delete(wav); } catch (Exception) { } return; }   // przewinieto / pauza w trakcie syntezy
                long tn = mp.Time - mp.SpuDelay / 1000;
                if (!queue && (tn < cue.Start - 300 || tn > cue.End + 1500)) { try { System.IO.File.Delete(wav); } catch (Exception) { } return; }   // kwestia juz nie na ekranie
                LectorStopVoice(lc, mp);
                lc.Ducked = volume(); mp.Volume = (int)(lc.Ducked * Math.Max(0, Math.Min(100, _settings.VlcLectorDuck)) / 100.0);   // film ciszej, jak lektor w TV
                var done = new TaskCompletionSource<bool>();
                var voice = new System.Windows.Media.MediaPlayer { Volume = WpfVol(Math.Max(0.1, _settings.VlcLectorVolume)) };
                lc.Voice = voice; lc.VoiceDone = done;
                voice.MediaEnded += (s, e) => done.TrySetResult(true);
                voice.MediaFailed += (s, e) => done.TrySetResult(false);
                voice.Open(new Uri(wav)); voice.Play();
                var played = System.Diagnostics.Stopwatch.StartNew(); long durMs = LectorWavMs(wav);
                bool natural = await Task.WhenAny(done.Task, Task.Delay(30000)) == done.Task && done.Task.Result;
                if (natural && lc.Voice == voice) { long left = durMs - played.ElapsedMilliseconds + 250; if (left > 0) await Task.Delay((int)Math.Min(left, 3000)); }   // koniec pliku != koniec dzwieku - w buforze glosnika zostaje koncowka, Stop od razu ucinal ostatnie slowo
                if (lc.Voice == voice) LectorStopVoice(lc, mp);   // przewiniecie zatrzymuje glos przez LectorStopVoice w ticku
                try { System.IO.File.Delete(wav); } catch (Exception) { }
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { lc.Busy = false; if (gen == lc.Gen) lc.CurIdx = -1; }
        }
    }
}
