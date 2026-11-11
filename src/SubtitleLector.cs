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
            public double DynBd = double.NaN;
            public bool AbVoice;   // kwestia gra ścieżką Velivo (AbProc.LekPlay)   // lektor w paśmie mowy (dBFS przy głośności 1) - do dopasowania do filmu
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
            public Dictionary<int, string> Ready = new Dictionary<int, string>();   // gotowe nagrania kwestii (caly film liczony w tle, zapis przy filmie)
            public int RenderGen; public bool Rendering; public string RenderKey; public Action<string> Osd; public bool BuildNow;   // RenderGen++ = zatrzymaj liczenie (nowe napisy, inny glos, zamkniecie)
        }

        // syntezuje kwestie: Piper (jezyk zgodny z napisami, jesli jest taki glos), inaczej glos Windows
        readonly HashSet<string> _lectorFetchTried = new HashSet<string>();   // pobieranie głosu lektora - raz na uruchomienie (bez powtórek po błędzie)

        async Task<string> LectorSynthesizeAsync(string text, string lang, double rate)
        {
            // glos wybrany w panelu lektora; "automatycznie" = Supertonic M1 (meski) w jezyku napisow
            var tag = _settings.VlcLectorVoice ?? "";
            var piper = tag.StartsWith("piper:") ? SelectedPiperVoice(tag) : tag.StartsWith("win:") ? null : SelectedPiperVoice("piper:st3-M1-pl") ?? PiperVoices.FirstOrDefault(v => v.Lang == lang);   // bez Supertonic (Velivo bezpłatne) - Piper w języku napisów
            if (piper?.St != null && piper.Lang != lang) piper = PiperVoices.FirstOrDefault(v => v.St == piper.St && v.Lang == lang) ?? new PiperVoice { Id = piper.Id, Label = piper.Label, St = piper.St, Lang = lang };   // Supertonic: ten sam głos w języku napisów
            if (piper?.Xt != null && piper.Lang != lang) piper = new PiperVoice { Id = piper.Id, Label = piper.Label, Xt = piper.Xt, Lang = lang };   // XTTS: ten sam mówca w języku napisów
            if (piper != null && !PiperVoiceInstalled(piper) && !_piperDownloading && !_stDownloading && !_xtDownloading && _lectorFetchTried.Add(piper.Id)) _ = EnsurePiperAsync(piper);   // domyślny głos lektora pobiera się sam; do tego czasu głos Windows
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
        // kwestia do zagrania teraz / następna - z pierwszeństwem przed czytaniem stron (budowa całego filmu w tle - bez)
        async Task<string> LectorSynthLive(Lector lc, SubCue cue) { _synthLive.Value = true; return await LectorSynth(lc, cue); }

        Task<string> LectorSynth(Lector lc, SubCue cue)
        {
            double secs = Math.Max(0.6, (cue.End - cue.Start) / 1000.0);
            double need = cue.Text.Length / 15.0 / secs;
            double rate = Math.Round(Math.Min(Math.Max(2.0, _settings.VlcLectorRate), Math.Max(_settings.VlcLectorRate, need)) * 10) / 10;   // stopnie co 0,1 - kazde inne tempo to ponowne uruchomienie Pipera
            return LectorSynthesizeAsync(cue.Text, lc.Lang, rate);
        }

        string LectorKeyParams(Lector lc) => (_settings.VlcLectorVoice ?? "") + ((_settings.VlcLectorVoice ?? "").StartsWith("piper:xtts-own") && File.Exists(XttsOwnWav) ? "@" + File.GetLastWriteTimeUtc(XttsOwnWav).Ticks : "") + "|" + ((_settings.VlcLectorVoice ?? "").StartsWith("piper:xtts") ? "1" : _settings.VlcLectorRate.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "|" + ((SelectedPiperVoice(string.IsNullOrEmpty(_settings.VlcLectorVoice) ? "piper:st3-M1-pl" : _settings.VlcLectorVoice)?.St != null) ? _settings.VlcLectorSteps.ToString() : "-") + "|" + lc.Lang + ((_settings.VlcLectorVoice ?? "").StartsWith("piper:xtts") ? "|xs3" : "") + "|" + (_settings.VlcLectorDir ?? "").Trim();   // xs3: XTTS zawsze 1:1 (bez przyspieszania) - nagrania z dawnym tempem budowane od nowa   // jakosc (kroki) liczy sie tylko dla Supertonic - przy innym glosie suwak nie buduje od nowa

        static void LectorStopRender(Lector lc) { lc.RenderGen++; lc.Rendering = false; lc.Ready = new Dictionary<int, string>(); lc.RenderKey = null; lc.BuildNow = false; }

        // lektor dla calego filmu: od biezacego miejsca do przodu, potem reszta; nagrania w Dane\lektor\<skrot> - przy drugim ogladaniu gotowe od razu
        async void LectorRender(Lector lc, bool loadOnly = false)   // loadOnly: tylko gotowe nagrania tego filmu (bez liczenia nowych)
        {
            int gen = lc.RenderGen; lc.Rendering = true;
            try
            {
                string key = LectorKeyParams(lc);
                var sb = new System.Text.StringBuilder();
                foreach (var c in lc.Cues) sb.Append('\n').Append(c.Start).Append('|').Append(c.End).Append('|').Append(c.Text);
                string film, set;
                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    film = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").Substring(0, 16);
                    set = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 8);
                }
                var root = string.IsNullOrWhiteSpace(_settings.VlcLectorDir) ? Path.Combine(DataDir, "lektor") : Path.Combine(_settings.VlcLectorDir.Trim(), "velivo-lektor"); PiperOutOverride = string.IsNullOrWhiteSpace(_settings.VlcLectorDir) ? null : _settings.VlcLectorDir.Trim(); var dir = Path.Combine(root, film + "-" + set);   // jeden folder na film (inny glos / tempo / jakosc kasuje poprzedni folder tego filmu); wlasny folder - zawsze podfolder velivo-lektor, porzadki nie ruszaja innych plikow
                var found = await Task.Run(() =>
                {
                    if (loadOnly && !Directory.Exists(dir)) return new Dictionary<int, string>();
                    Directory.CreateDirectory(dir); Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
                    try
                    {
                        foreach (var old in new DirectoryInfo(root).GetDirectories())
                            if (old.FullName != new DirectoryInfo(dir).FullName && (old.Name.StartsWith(film + "-") || !old.Name.Contains('-'))) old.Delete(true);   // stare ustawienia tego filmu i foldery starego formatu
                        foreach (var old in new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Skip(3)) old.Delete(true);   // najwyzej 3 filmy na dysku (film ~ kilkaset MB nagran)
                    }
                    catch (Exception) { }
                    var m = new Dictionary<int, string>();
                    foreach (var p in Directory.EnumerateFiles(dir, "*.wav")) { int n; if (int.TryParse(Path.GetFileNameWithoutExtension(p), out n) && new FileInfo(p).Length > 44) m[n] = p; }
                    return m;
                });
                if (gen != lc.RenderGen) return;
                lc.Ready = found; lc.RenderKey = key;
                if (loadOnly) { if (found.Count > 0) lc.Osd?.Invoke(L.T("🗣 Lektor: używam gotowych nagrań tego filmu – ") + found.Count + " / " + lc.Cues.Count); return; }
                lc.Osd?.Invoke(L.T("🗣 Lektor: przygotowuję cały film w tle – gotowe ") + found.Count + " / " + lc.Cues.Count);
                int fails = 0; var lastOsd = DateTime.UtcNow;   // postep co 10% albo co minute (wolny glos - XTTS)
                while (gen == lc.RenderGen)
                {
                    int from = Math.Max(0, lc.LastSpoken + 1), idx = -1;
                    for (int k = from; k < lc.Cues.Count; k++) if (!lc.Ready.ContainsKey(k) && k != lc.PreIdx) { idx = k; break; }   // bez kwestii liczonej właśnie na żywo - ta sama praca dwa razy dławiła silnik (opóźnienie, pomijane kwestie)
                    if (idx < 0) for (int k = 0; k < from && k < lc.Cues.Count; k++) if (!lc.Ready.ContainsKey(k) && k != lc.PreIdx) { idx = k; break; }
                    if (idx < 0 && lc.PreIdx >= 0 && !lc.Ready.ContainsKey(lc.PreIdx)) { await Task.Delay(200); continue; }   // została tylko kwestia liczona na żywo - poczekaj, potem dokończ
                    if (idx < 0) { lc.Osd?.Invoke(L.T("🗣 Lektor gotowy dla całego filmu")); break; }   // caly film gotowy
                    var preSw = System.Diagnostics.Stopwatch.StartNew(); while (gen == lc.RenderGen && lc.PreWav != null && !lc.PreWav.IsCompleted && preSw.ElapsedMilliseconds < 120000) await Task.Delay(100);   // kwestia liczona na biezaco ma pierwszenstwo (XTTS/Supertonic licza jedna naraz); samo granie lektora budowy nie wstrzymuje
                    if (gen != lc.RenderGen) return;
                    var tmp = await LectorSynth(lc, lc.Cues[idx]);
                    if (gen != lc.RenderGen) { if (tmp != null) try { System.IO.File.Delete(tmp); } catch (Exception) { } return; }
                    if (tmp == null)
                    {
                        if (++fails == 3) { App.LogError(new Exception("Lektor: budowa – głos nie zwrócił nagrania (kwestia " + idx + ", głos " + (_settings.VlcLectorVoice ?? "auto") + ")")); lc.Osd?.Invoke(L.T("🗣 Lektor: głos nie odpowiada przy budowie – szczegóły w Błędy Velivo")); }
                        await Task.Delay(2000); continue;
                    }
                    fails = 0;
                    var dst = Path.Combine(dir, idx + ".wav");
                    try { System.IO.File.Move(tmp, dst, true); lc.Ready[idx] = dst; int n = lc.Ready.Count, all = lc.Cues.Count; if (all > 0 && (n * 10 / all != (n - 1) * 10 / all || (DateTime.UtcNow - lastOsd).TotalSeconds > 60)) lastOsd = DateTime.UtcNow; else continue; lc.Osd?.Invoke(L.T("🗣 Lektor: przygotowuję cały film w tle – gotowe ") + n + " / " + all); }   // postęp na ekranie co 10%
                    catch (Exception ex)
                    {
                        if (File.Exists(dst) && new FileInfo(dst).Length > 44) { lc.Ready[idx] = dst; try { File.Delete(tmp); } catch (Exception) { } }   // nagranie juz jest (wlasnie grane) - zostaje
                        else { App.LogError(ex); await Task.Delay(2000); }
                    }
                }
            }
            catch (Exception ex) { App.LogError(ex); lc.RenderKey = LectorKeyParams(lc); lc.Osd?.Invoke(L.T("🗣 Lektor: nie udało się przygotować filmu – ") + ex.Message); }   // bez ponawiania co 120 ms
            finally { if (gen == lc.RenderGen) lc.Rendering = false; }
        }

        // nagranie kwestii -> mono 48 kHz (tempo sr: szybciej = krócej), pasmo mowy i szczyt - dla ścieżki Velivo
        static (float[] s, double band, double full, double peak) LectorWavPcm48(string wav, double sr)
        {
            try
            {
                var b = System.IO.File.ReadAllBytes(wav); if (b.Length < 1000 || BitConverter.ToInt16(b, 34) != 16) return (null, 0, 0, 0);
                int rate = BitConverter.ToInt32(b, 24), ch = Math.Max(1, (int)BitConverter.ToInt16(b, 22)), n = (b.Length - 44) / (2 * ch);
                if (rate < 8000 || n < 10) return (null, 0, 0, 0);
                double full = LectorWavDb(wav, out var band); if (double.IsNaN(full) || double.IsNaN(band) || double.IsInfinity(band)) return (null, 0, 0, 0);
                double step = rate * Math.Max(0.5, sr) / 48000.0; int outN = (int)((n - 1) / step);
                int st = 0; for (; st < n - 1; st++) if (Math.Abs(BitConverter.ToInt16(b, 44 + st * ch * 2)) > 100) break;   // cisza na początku nagrania (głos syntetyczny) - lektor startuje równo z napisem
                st = Math.Max(0, st - rate / 100); n -= st; outN = (int)((n - 1) / step); if (outN < 10) return (null, 0, 0, 0);
                var o = new float[outN]; double pk = 0;
                Func<int, double> at = i => { double v = 0; for (int c = 0; c < ch; c++) v += BitConverter.ToInt16(b, 44 + ((i + st) * ch + c) * 2); return v / (32768.0 * ch); };
                for (int k = 0; k < outN; k++) { double x = k * step; int i = (int)x; double fr = x - i, v = at(i) + (at(i + 1) - at(i)) * fr; o[k] = (float)v; if (Math.Abs(v) > pk) pk = Math.Abs(v); }
                return (o, band, full, pk);
            }
            catch (Exception) { return (null, 0, 0, 0); }
        }

        static long LectorWavMs(string wav)
        {
            try { using (var fs = System.IO.File.OpenRead(wav)) { var h = new byte[44]; if (fs.Read(h, 0, 44) < 44) return 0; int bps = BitConverter.ToInt32(h, 28); return bps > 0 ? (fs.Length - 44) * 1000 / bps : 0; } }
            catch (Exception) { return 0; }
        }

        // głośność nagrania lektora (dBFS, średnia mowy bez ciszy - bloki 100 ms powyżej -50 dBFS); NaN = nie wiadomo
        static double LectorWavDb(string wav) => LectorWavDb(wav, out _);
        // + pasmo zrozumiałości mowy 1–4 kHz (dla doradcy lektora): ten sam filtr co w pomiarze filmu, przy częstotliwości pliku
        static double LectorWavDb(string wav, out double bandDb)
        {
            bandDb = double.NaN;
            try
            {
                var b = System.IO.File.ReadAllBytes(wav); if (b.Length < 1000 || BitConverter.ToInt16(b, 34) != 16) return double.NaN;
                int sr = BitConverter.ToInt32(b, 24), ch = Math.Max(1, (int)BitConverter.ToInt16(b, 22)), blk = Math.Max(1, sr / 10); double sum = 0, bsum = 0; int nb = 0;
                var hp = AbBiquad.Hp(sr, 1000); var lp = AbBiquad.Lp(sr, 4000);
                for (int o = 44; o + blk * ch * 2 <= b.Length; o += blk * ch * 2)
                {
                    double e = 0, eb = 0;
                    for (int k = 0; k < blk; k++)
                    {
                        double v = 0; for (int c = 0; c < ch; c++) v += BitConverter.ToInt16(b, o + (k * ch + c) * 2) / 32768.0; v /= ch;
                        double f = lp.L(hp.L(v)); e += v * v; eb += f * f;
                    }
                    e /= blk; eb /= blk; if (e > 1e-5) { sum += e; bsum += eb; nb++; }   // tylko bloki z mową (bez ciszy)
                }
                if (nb == 0) return double.NaN;
                bandDb = 10 * Math.Log10(bsum / nb + 1e-12);
                return 10 * Math.Log10(sum / nb);
            }
            catch (Exception) { return double.NaN; }
        }

        static void LectorDropPre(Lector lc)
        {
            var p = lc.PreWav; lc.PreWav = null; lc.PreIdx = -1;
            p?.ContinueWith(t => { try { if (t.Status == TaskStatus.RanToCompletion && t.Result != null) System.IO.File.Delete(t.Result); } catch (Exception) { } });
        }

        // odtwarzacz Windows (WPF MediaPlayer): przy 1,0 gra CISZEJ niż przy 0,99 (sprawdził właściciel - lektor i czytnik) - górna granica 0,99
        // 🗣 inteligentny lektor (domyślnie wł.); wył. = dawne stałe ściszanie na czas kwestii
        internal static bool LectorSmart { get => GetPref("lektor-inteligentny") != "0"; set => SetPref("lektor-inteligentny", value ? "1" : "0"); }
        internal static double WpfVol(double v) => Math.Max(0, Math.Min(0.99, v));

        // 🗣 dynamika lektora: 10 dB nad filmem w paśmie mowy (12 przy dialogu w filmie), suwak „Głośność lektora” = Twoja poprawka (99% = 0 dB); film bez zmian
        double LectorDynVol(Lector lc)
        {
            double user = Math.Max(0.1, _settings.VlcLectorVolume);
            double k = AbProc.KDb;
            if (double.IsNaN(lc.DynBd) || float.IsNaN((float)k) || !(LectorSmart && _abOut != null && AbProc.LekPath)) return WpfVol(user);
            double want = Math.Max(k, -50) + 10 + (AbProc.InDialogShare > 0.5 ? 2 : 0) + 20 * Math.Log10(user / 0.99);
            return WpfVol(Math.Max(0.05, Math.Pow(10, (want - lc.DynBd) / 20)));
        }

        void LectorStopVoice(Lector lc, LibVLCSharp.Shared.MediaPlayer mp)
        {
            if (lc.Voice != null) { try { lc.Voice.Stop(); lc.Voice.Close(); } catch (Exception) { } lc.Voice = null; }
            lc.VoiceDone?.TrySetResult(false);
            if (lc.AbVoice) { AbProc.LekStop(); lc.AbVoice = false; }
            if (lc.Ducked >= 0) { try { mp.Volume = lc.Ducked; } catch (Exception) { } lc.Ducked = -1; }
            AbProc.LekOn = false;   // inteligentny lektor: film wraca płynnie (przytrzymanie 0,8 s - między kwestiami bez pompowania)
        }

        // co ~120 ms: nowa kwestia na ekranie -> czytamy ja (wolny lektor = pomijamy zalegle, czytamy biezaca)
        async void LectorTick(Lector lc, LibVLCSharp.Shared.MediaPlayer mp, Func<int> volume)
        {
            if (lc.Cues.Count == 0) return;
            if (lc.AbVoice) AbProc.LekUserDb = (float)(20 * Math.Log10(Math.Max(0.1, _settings.VlcLectorVolume) / 0.99));
            if (lc.Voice != null && !double.IsNaN(lc.DynBd)) { double cv = lc.Voice.Volume, tv = LectorDynVol(lc); lc.Voice.Volume = cv + (tv - cv) * (tv > cv ? 0.5 : 0.25); }   // dynamika w trakcie kwestii (płynnie, co ~120 ms)
            // budowa lektora takze w pauzie: zmiana glosu / „Zbuduj lektora teraz” przy zatrzymanym filmie startuje od razu
            // (wczesniej sprawdzane dopiero po pauzie - budowa ruszala dopiero po wznowieniu filmu)
            if (lc.RenderKey != null && lc.RenderKey != LectorKeyParams(lc)) LectorStopRender(lc);   // inny glos / tempo / jakosc - liczymy od nowa
            long tNow = 0; try { tNow = mp.Time; } catch (Exception) { }
            if (!lc.Rendering && lc.RenderKey == null && (tNow > 2000 || lc.BuildNow)) LectorRender(lc, !(_settings.VlcLectorAutoBuild || lc.BuildNow));   // budowa dopiero, gdy film już gra (2 s) - głos nie zajmuje karty graficznej w chwili otwierania filmu (film się nie włączał)   // samo albo po przycisku „Zbuduj lektora teraz”
            bool queue = _settings.VlcLectorQueue;   // kolejka: czyta wszystko po kolei, nawet z opóźnieniem
            bool ended = false; try { ended = mp.State == LibVLCSharp.Shared.VLCState.Ended; } catch (Exception) { }
            long t = ended ? long.MaxValue / 2 : mp.Time - mp.SpuDelay / 1000;   // jeden zegar: czas filmu VLC z przesunieciem napisow (to samo co napisy); koniec filmu = kolejka do konca
            long now = Environment.TickCount64;
            LibVLCSharp.Shared.VLCState st = LibVLCSharp.Shared.VLCState.NothingSpecial; try { st = mp.State; } catch (Exception) { }
            if (!mp.IsPlaying && (st == LibVLCSharp.Shared.VLCState.Buffering || st == LibVLCSharp.Shared.VLCState.Opening)) return;   // chwilowe buforowanie filmu to nie pauza - lektor czyta dalej
            if (!mp.IsPlaying && !(queue && ended))
            {
                if (lc.Voice != null || lc.AbVoice || lc.Busy)
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
            if (i < 0 && lc.PreWav == null && !lc.Busy && tNow > 2000)
            {   // przerwa miedzy napisami: najblizsza kwestia liczona juz teraz (po starcie i po przewinieciu)
                int j = lc.Cues.FindIndex(c => c.Start > t);
                if (j >= 0 && lc.Cues[j].Start - t < 15000 && !lc.Ready.ContainsKey(j)) { lc.PreIdx = j; lc.PreWav = LectorSynthLive(lc, lc.Cues[j]); }
            }
            if (i < 0 || i <= lc.LastSpoken || lc.Busy) return;
            lc.LastSpoken = i;
            var cue = lc.Cues[i];
            int gen = lc.Gen;
            lc.Busy = true; lc.CurIdx = i;
            try
            {
                // tempo: ustawione w panelu lektora; dluga kwestia w krotkim czasie - szybciej (do 2x)
                string readyWav; bool cached = lc.Ready.TryGetValue(i, out readyWav) && System.IO.File.Exists(readyWav);
                var wavTask = cached ? Task.FromResult(readyWav) : lc.PreIdx == i ? lc.PreWav : null;
                if (cached) { if (lc.PreIdx == i) LectorDropPre(lc); }
                else if (wavTask == null) { LectorDropPre(lc); wavTask = LectorSynthLive(lc, cue); } else { lc.PreIdx = -1; lc.PreWav = null; }
                if (i + 1 < lc.Cues.Count && !lc.Ready.ContainsKey(i + 1)) { lc.PreIdx = i + 1; lc.PreWav = LectorSynthLive(lc, lc.Cues[i + 1]); }   // nastepna kwestia w tle, gdy ta gra - bez czekania na synteze
                if (await Task.WhenAny(wavTask, Task.Delay(120000)) != wavTask) { App.LogError(new Exception("Lektor: głos nie oddał nagrania kwestii " + i + " w 2 min (" + (_settings.VlcLectorVoice ?? "auto") + ") - pomijam ją")); if (lc.PreIdx == i) LectorDropPre(lc); return; }   // zawieszona synteza nie zatrzymuje lektora na stałe
                var wav = wavTask.Result;
                if (wav == null || gen != lc.Gen || !mp.IsPlaying && !(queue && ended)) { if (wav != null && !cached) try { System.IO.File.Delete(wav); } catch (Exception) { } return; }   // przewinieto / pauza w trakcie syntezy
                long tn = mp.Time - mp.SpuDelay / 1000;
                if (!queue && (tn < cue.Start - 300 || tn > cue.End + 1500)) { if (!cached) try { System.IO.File.Delete(wav); } catch (Exception) { } return; }   // kwestia juz nie na ekranie
                LectorStopVoice(lc, mp);
                double duck = 1;   // właściciel: film nigdy nie ściszany pod lektora - lektor dopasowuje się do filmu
                bool smart = LectorSmart && _abOut != null && AbProc.LekPath;   // 🗣 dynamika: głośność lektora na bieżąco wg filmu w paśmie mowy
                lc.DynBd = double.NaN;
                if (smart) { var bdT = Task.Run(() => { LectorWavDb(wav, out var bd); return bd; }); if (await Task.WhenAny(bdT, Task.Delay(1500)) == bdT && !double.IsNaN(bdT.Result) && !double.IsInfinity(bdT.Result)) lc.DynBd = bdT.Result; }
                // 🗣 doradca: słyszalność tej kwestii w paśmie mowy 1–4 kHz (lektor vs film po wyrównaniu i ściszeniu, dialogi w filmie) - tylko ocena i propozycja
                if (AbProc.Active && !AbBypass)
                {   // pomiar lektora w tle (odczyt i filtr całego WAV - w wątku okna przycinał słupki Audio Monitora), wynik do doradcy w wątku okna; lektor nie czeka
                    double lv = WpfVol(Math.Max(0.1, _settings.VlcLectorVolume)), dk = duck;
                    _ = Task.Run(() => { LectorWavDb(wav, out var bd); return bd; }).ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion && !double.IsNaN(t.Result)) Dispatcher.BeginInvoke(new Action(() => AbLektorCue(t.Result, lv, dk))); });
                }
                if (_abOut != null && mp.IsPlaying)
                {   // 🗣 ścieżka Velivo: lektor wmieszany w dźwięk filmu - głośniej niż pozwala odtwarzacz Windows, dynamika na bieżąco, film bez zmian
                    double sr0 = 1; if (queue) { long late = tn - cue.Start; if (late > 500) sr0 = Math.Min(1.5, 1 + (late - 500) / 6000.0); }
                    var pcm = await Task.Run(() => LectorWavPcm48(wav, sr0));
                    if (pcm.s != null && gen == lc.Gen && mp.IsPlaying)
                    {
                        AbProc.LekUserDb = (float)(20 * Math.Log10(Math.Max(0.1, _settings.VlcLectorVolume) / 0.99));
                        AbProc.LekFloorDb = (float)(20 * Math.Log10(Math.Max(10, Math.Min(100, _settings.VlcLectorDuck)) / 100.0));   // suwak: najgłębsze ustąpienie filmu (100% = film nigdy)
                        lc.AbVoice = true; AbProc.LekPlay(pcm.s, pcm.band, pcm.full, pcm.peak);
                        var sw = System.Diagnostics.Stopwatch.StartNew(); long maxMs = pcm.s.Length / 48 + 20000;
                        while (lc.AbVoice && AbProc.LekPlaying && gen == lc.Gen && sw.ElapsedMilliseconds < maxMs) await Task.Delay(40);
                        if (lc.AbVoice && gen == lc.Gen) { lc.AbVoice = false; AbProc.LekStop(); }
                        if (!cached) try { System.IO.File.Delete(wav); } catch (Exception) { }
                        return;
                    }
                }
                var done = new TaskCompletionSource<bool>();
                var voice = new System.Windows.Media.MediaPlayer { Volume = LectorDynVol(lc) };
                lc.Voice = voice; lc.VoiceDone = done;
                voice.MediaEnded += (s, e) => done.TrySetResult(true);
                voice.MediaFailed += (s, e) => done.TrySetResult(false);
                double sr = 1; if (queue) { long late = tn - cue.Start; if (late > 500) sr = Math.Min(1.5, 1 + (late - 500) / 6000.0); }   // nadrabianie jak lektor: spoznienie ponad 0,5 s = szybciej (do 1,5x), po dogonieniu obrazu zwykle tempo
                voice.Open(new Uri(wav)); voice.SpeedRatio = sr; voice.Play();
                var played = System.Diagnostics.Stopwatch.StartNew(); long durMs = (long)(LectorWavMs(wav) / sr);
                bool natural = await Task.WhenAny(done.Task, Task.Delay(30000)) == done.Task && done.Task.Result;
                if (natural && lc.Voice == voice) { long left = durMs - played.ElapsedMilliseconds; if (left > 0) await Task.Delay((int)Math.Min(left, 3000)); }   // koniec pliku != koniec dzwieku - w buforze glosnika zostaje koncowka, Stop od razu ucinal ostatnie slowo
                if (lc.Voice == voice) LectorStopVoice(lc, mp);   // przewiniecie zatrzymuje glos przez LectorStopVoice w ticku
                voice.Close();   // zwalnia plik z nagraniem kwestii
                if (!cached) try { System.IO.File.Delete(wav); } catch (Exception) { }
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { lc.Busy = false; if (gen == lc.Gen) lc.CurIdx = -1; }
        }
    }
}
