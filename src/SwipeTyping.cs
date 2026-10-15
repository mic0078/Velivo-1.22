using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace Przegladarka
{
    // Pisanie przeciagnieciem (jak "Flow"): slad palca / myszy po klawiszach porownany z idealna sciezka
    // przez srodki klawiszy kazdego slowa ze slownika; wygrywa najblizszy slad, a przy podobnych - czestsze slowo.
    internal static class SwipeTyping
    {
        // polskie litery przeciaga sie przez litere podstawowa - takze "ź" przez "z" (jak w telefonie)
        internal static char BaseKey(char c)
        {
            var i = "ąćęłńóśżź".IndexOf(c);
            return i >= 0 ? "acelnoszz"[i] : c;
        }

        // wolny ruch reki z drzeniem: wygladzenie (srednia z sasiednich punktow) i pominiecie drobnych drgan
        // (punkt dopiero co ok. pol klawisza) - zostaje sam ksztalt sladu, jak przy szybkim przeciagnieciu
        internal static List<Point> Steady(IList<Point> pts, double keyW)
        {
            var sm = new List<Point>();
            for (int i = 0; i < pts.Count; i++)
            {   // srednia tylko z bliskich punktow (wolny ruch) - szybki slad z rzadkimi punktami zostaje bez zmian, zakrety nie sa scinane
                double x = 0, y = 0; int n = 0;
                for (int j = Math.Max(0, i - 3); j <= Math.Min(pts.Count - 1, i + 3); j++)
                    if (Dist(pts[j], pts[i]) <= keyW * 0.35) { x += pts[j].X; y += pts[j].Y; n++; }
                sm.Add(new Point(x / n, y / n));
            }
            var res = new List<Point> { sm[0] };
            foreach (var p in sm) if (Dist(p, res[res.Count - 1]) >= keyW * 0.45) res.Add(p);
            if (Dist(res[res.Count - 1], sm[sm.Count - 1]) > 0.5) res.Add(sm[sm.Count - 1]);
            res[0] = pts[0]; res[res.Count - 1] = pts[pts.Count - 1];   // poczatek i koniec - tam, gdzie naprawde
            return res;
        }

        static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("Velivo.Keyboard.v1");

        sealed class Entry { public string Word; public string Keys; public int Rank; }
        static List<Entry> _words;
        static Dictionary<string, List<Entry>> _byEnds;   // klucz: pierwszy + ostatni klawisz

        // slownik: jedno slowo w wierszu, od najczestszego
        internal static void Load(IEnumerable<string> words)
        {
            var list = new List<Entry>(); var ends = new Dictionary<string, List<Entry>>();
            int rank = 0;
            foreach (var w0 in words)
            {
                var w = (w0 ?? "").Trim().ToLowerInvariant(); if (w.Length == 0) continue;
                var keys = new string(w.Select(BaseKey).ToArray());
                if (!keys.All(k => k >= 'a' && k <= 'z')) continue;
                var e = new Entry { Word = w, Keys = Collapse(keys), Rank = rank++ };
                list.Add(e);
                var k2 = "" + e.Keys[0] + e.Keys[e.Keys.Length - 1];
                if (!ends.TryGetValue(k2, out var l)) ends[k2] = l = new List<Entry>();
                l.Add(e);
            }
            _words = list; _byEnds = ends; _fold = null; _main = null;
            Correct("xx");   // indeks autokorekty od razu (w tle) - przy pisaniu sprawdzenie slowa trwa ulamek milisekundy
        }

        internal static bool Loaded => _words != null;

        internal static bool IsKnown(string word) { var w = word.ToLowerInvariant(); return _words != null && _words.Any(e => e.Word == w); }

        // wlasny slownik (jak na Androidzie): slowo wpisane litera po literze, ktorego nie ma w slowniku, od razu da sie pisac przeciagnieciem
        internal static void AddUserWord(string word)
        {
            var w = (word ?? "").Trim().ToLowerInvariant();
            if (_words == null || w.Length < 2 || IsKnown(w)) return;
            var keys = new string(w.Select(BaseKey).ToArray());
            if (!keys.All(k => k >= 'a' && k <= 'z')) return;
            var e = new Entry { Word = w, Keys = Collapse(keys), Rank = 3000 };   // jak srednio czeste slowo
            _words.Add(e);
            lock (_ownFold) _ownFold[Fold(w)] = w;
            var k2 = "" + e.Keys[0] + e.Keys[e.Keys.Length - 1];
            if (!_byEnds.TryGetValue(k2, out var l)) _byEnds[k2] = l = new List<Entry>();
            l.Add(e);
        }

        internal static void RemoveUserWord(string word)
        {
            var w = (word ?? "").ToLowerInvariant();
            if (_words == null) return;
            _words.RemoveAll(e => e.Word == w && e.Rank == 3000);
            lock (_ownFold) { var f = Fold(w); if (_ownFold.TryGetValue(f, out var o) && o == w) _ownFold.Remove(f); }
            foreach (var l in _byEnds.Values) l.RemoveAll(e => e.Word == w && e.Rank == 3000);
        }

        // autokorekta pisania litera po literze: slowo spoza slownika -> najczestsze znane slowo, ktore rozni sie tylko polskimi znakami
        // (zolw -> żółw) albo jedna litera (brakujaca, nadmiarowa, inna, zamienione dwie sasiednie); null = zostaw jak jest
        static Dictionary<string, Entry> _fold;   // slowo bez polskich znakow -> najczestsze slowo slownika
        static HashSet<string> _main;               // slowa slownika glownego (bez wyuczonych z pisania)
        static Dictionary<string, List<Entry>> _foldAll;   // bez polskich znakow -> wszystkie slowa (droga: droga, drogą)
        static string[] _sortedFold; static Entry[] _sortedEntry;   // do dopowiadania slowa w trakcie pisania
        static readonly Dictionary<string, string> _ownFold = new Dictionary<string, string>();   // Moj slownik: bez polskich znakow -> slowo
        static string Fold(string w) { return new string(w.ToLowerInvariant().Select(BaseKey).ToArray()); }
        // anyRank: literowka tez do rzadkiego slowa (do rozpoznania literowek w Moim slowniku, nie do poprawiania w trakcie pisania)
        internal static string Correct(string typed, ICollection<string> keep = null, bool anyRank = false)
        {
            if (_words == null || string.IsNullOrEmpty(typed) || typed.Length < 2 || !typed.All(char.IsLetter)) return null;
            var w = typed.ToLowerInvariant();
            if (keep != null && keep.Contains(w)) return null;   // slowo, ktorego poprawke uzytkownik cofnal
            var fold = _fold;
            var main = _main;
            if (fold == null || main == null)
            {
                main = new HashSet<string>(_words.Where(x => x.Rank != 3000).Select(x => x.Word));
                fold = new Dictionary<string, Entry>();
                foreach (var e in _words.Where(x => x.Rank != 3000)) { var f = new string(e.Word.Select(BaseKey).ToArray()); if (!fold.TryGetValue(f, out var b) || e.Rank < b.Rank) fold[f] = e; }
                var all = new Dictionary<string, List<Entry>>();
                foreach (var e in _words.Where(x => x.Rank != 3000)) { var f = Fold(e.Word); if (!all.TryGetValue(f, out var l)) all[f] = l = new List<Entry>(); l.Add(e); }
                var sorted = _words.Where(x => x.Rank != 3000).Select(e => (F: Fold(e.Word), E: e)).OrderBy(x => x.F, StringComparer.Ordinal).ToArray();
                _foldAll = all; _sortedFold = sorted.Select(x => x.F).ToArray(); _sortedEntry = sorted.Select(x => x.E).ToArray();
                _main = main; _fold = fold;
            }
            if (main.Contains(w)) return null;
            lock (_ownFold) if (_ownFold.ContainsValue(w)) return null;   // Moje slowo - bez zmian   // slowo ze slownika - bez zmian (wyuczone z pisania nie blokuje poprawki: dawniej uczyly sie tez literowki, np. zolw)
            var fw = new string(w.Select(BaseKey).ToArray());
            Entry best = null;
            if (fold.TryGetValue(fw, out var same)) best = same;   // tylko polskie znaki
            string ownSame; lock (_ownFold) _ownFold.TryGetValue(fw, out ownSame);
            if (best == null && ownSame != null && ownSame != w) best = new Entry { Word = ownSame, Rank = 3000 };   // Moje slowo bez polskich znakow
            else if (best == null && w.Length >= 4)   // jedna litera rozni (krotkich slow nie zgadujemy)
            {
                const string abc = "abcdefghijklmnopqrstuvwxyz";
                Action<string> cand = v => { if (fold.TryGetValue(v, out var e) && (anyRank || e.Rank < 15000) && (best == null || e.Rank < best.Rank)) best = e; };   // literowka -> tylko czeste slowo (rzadkie poprawne slowo spoza slownika zostaje)
                for (int i = 0; i < fw.Length; i++)
                {
                    cand(fw.Remove(i, 1));
                    if (i + 1 < fw.Length) cand(fw.Substring(0, i) + fw[i + 1] + fw[i] + fw.Substring(i + 2));
                    foreach (var c in abc) { if (c != fw[i]) cand(fw.Substring(0, i) + c + fw.Substring(i + 1)); cand(fw.Insert(i, c.ToString())); }
                }
                foreach (var c in abc) cand(fw + c);
            }
            if (best == null || best.Word == w) return null;
            var r = best.Word;
            if (typed.All(char.IsUpper) && typed.Length > 1) return r.ToUpperInvariant();
            return char.IsUpper(typed[0]) ? char.ToUpperInvariant(r[0]) + r.Substring(1) : r;
        }

        // watpliwosc (slowa nie ma w slowniku, a pewnej poprawki brak): do 3 propozycji - polskie znaki albo jedna litera, takze rzadkie slowa
        internal static List<string> Suggest(string typed, ICollection<string> keep = null, int count = 3)
        {
            var res = new List<string>();
            if (_words == null || string.IsNullOrEmpty(typed) || typed.Length < 3 || !typed.All(char.IsLetter)) return res;
            var w = typed.ToLowerInvariant();
            if (keep != null && keep.Contains(w)) return res;
            Correct("xx"); var fold = _fold; var main = _main; if (fold == null || main == null || main.Contains(w)) return res;
            var fw = new string(w.Select(BaseKey).ToArray());
            var found = new Dictionary<string, Entry>();
            Action<string> cand = v => { if (fold.TryGetValue(v, out var e)) found[e.Word] = e; };
            cand(fw);
            const string abc = "abcdefghijklmnopqrstuvwxyz";
            for (int i = 0; i < fw.Length; i++)
            {
                cand(fw.Remove(i, 1));
                if (i + 1 < fw.Length) cand(fw.Substring(0, i) + fw[i + 1] + fw[i] + fw.Substring(i + 2));
                foreach (var c in abc) { if (c != fw[i]) cand(fw.Substring(0, i) + c + fw.Substring(i + 1)); cand(fw.Insert(i, c.ToString())); }
            }
            foreach (var c in abc) cand(fw + c);
            foreach (var e in found.Values.OrderBy(x => x.Rank).Take(count))
                res.Add(char.IsUpper(typed[0]) ? char.ToUpperInvariant(e.Word[0]) + e.Word.Substring(1) : e.Word);
            return res;
        }

        // pasek propozycji: w trakcie pisania - inne formy tego slowa (droga -> drogą) i dokonczenia (klaw -> klawiatura);
        // po skonczonym slowie (done) - tylko inne formy z polskimi znakami. Najczestsze najpierw, Moje slowa tez.
        internal static List<string> Predict(string typed, bool done, int count = 3)
        {
            var res = new List<string>();
            if (_words == null || string.IsNullOrEmpty(typed) || !typed.All(char.IsLetter)) return res;
            Correct("xx"); var all = _foldAll; var sf = _sortedFold; var se = _sortedEntry; if (all == null || sf == null) return res;
            var w = typed.ToLowerInvariant(); var fw = Fold(w);
            Action<string> add = x => { if (x != w && !res.Contains(x) && res.Count < count) res.Add(x); };
            if (all.TryGetValue(fw, out var same)) foreach (var e in same.OrderBy(x => x.Rank)) add(e.Word);
            lock (_ownFold) if (_ownFold.TryGetValue(fw, out var ow)) add(ow);
            if (!done && fw.Length >= 2 && res.Count < count)
            {
                int lo = 0, hi = sf.Length;
                while (lo < hi) { int mid = (lo + hi) / 2; if (string.CompareOrdinal(sf[mid], fw) < 0) lo = mid + 1; else hi = mid; }
                var comp = new List<Entry>();
                for (int i = lo; i < sf.Length && i < lo + 4000 && sf[i].StartsWith(fw, StringComparison.Ordinal); i++) if (sf[i].Length > fw.Length) comp.Add(se[i]);
                foreach (var e in comp.OrderBy(x => x.Rank)) add(e.Word);
                lock (_ownFold) foreach (var o in _ownFold.Where(kv => kv.Key.StartsWith(fw, StringComparison.Ordinal) && kv.Key.Length > fw.Length).Select(kv => kv.Value).OrderBy(x => x)) add(o);
            }
            if (typed.Length > 0 && char.IsUpper(typed[0])) for (int i = 0; i < res.Count; i++) res[i] = char.ToUpperInvariant(res[i][0]) + res[i].Substring(1);
            return res;
        }

        // podwojne litery to ten sam klawisz - na sladzie jeden punkt
        static string Collapse(string keys)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in keys) if (sb.Length == 0 || sb[sb.Length - 1] != c) sb.Append(c);
            return sb.ToString();
        }

        static double Dist(Point a, Point b) { var dx = a.X - b.X; var dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }

        // n punktow rowno rozlozonych na lamanej
        internal static Point[] Resample(IList<Point> pts, int n)
        {
            var res = new Point[n];
            if (pts.Count == 0) return res;
            double total = 0; for (int i = 1; i < pts.Count; i++) total += Dist(pts[i - 1], pts[i]);
            if (total < 1e-6) { for (int i = 0; i < n; i++) res[i] = pts[0]; return res; }
            double step = total / (n - 1), acc = 0; int seg = 1; var prev = pts[0]; res[0] = pts[0]; int k = 1;
            while (k < n - 1 && seg < pts.Count)
            {
                var d = Dist(prev, pts[seg]);
                if (acc + d >= step && d > 0)
                {
                    var t = (step - acc) / d;
                    var q = new Point(prev.X + t * (pts[seg].X - prev.X), prev.Y + t * (pts[seg].Y - prev.Y));
                    res[k++] = q; prev = q; acc = 0;
                }
                else { acc += d; prev = pts[seg]; seg++; }
            }
            while (k < n) res[k++] = pts[pts.Count - 1];
            return res;
        }

        // najlepsze slowa dla sladu; keys - srodki klawiszy liter a-z, keyW - odstep miedzy klawiszami,
        // learned - ile razy uzytkownik wybral slowo (wyzej na liscie)
        internal static List<string> Recognize(IList<Point> trace, IDictionary<char, Point> keys, double keyW, IDictionary<string, int> learned = null, int count = 3, string prev = null)
        {
            var result = new List<string>();
            if (_words == null || trace.Count < 2 || keys.Count == 0) return result;
            const int N = 32;
            trace = Steady(trace, keyW);
            var g = Resample(trace, N);
            // klawisze w poblizu poczatku i konca sladu
            Func<Point, List<char>> near = p =>
            {
                var l = keys.Where(kv => Dist(kv.Value, p) <= keyW * 0.95).Select(kv => kv.Key).ToList();
                if (l.Count == 0) l.Add(keys.OrderBy(kv => Dist(kv.Value, p)).First().Key);
                return l;
            };
            var starts = near(trace[0]); var ends = near(trace[trace.Count - 1]);
            double gLen = 0; for (int i = 1; i < trace.Count; i++) gLen += Dist(trace[i - 1], trace[i]);
            var scored = new List<(string W, double S)>();
            foreach (var s in starts)
                foreach (var e in ends)
                {
                    if (!_byEnds.TryGetValue("" + s + e, out var list)) continue;
                    foreach (var w in list)
                    {
                        if (w.Keys.Length == 1 && gLen > keyW) continue;   // jedna litera - tylko przy krotkim sladzie
                        var path = w.Keys.Select(c => keys.TryGetValue(c, out var p) ? p : new Point(double.NaN, 0)).ToList();
                        if (path.Any(p => double.IsNaN(p.X))) continue;
                        // nauka: Twoj wlasny ksztalt przeciagniecia tego slowa (zapamietany przy wczesniejszym pisaniu) - sprawdzany przed filtrami,
                        // bo Twoj ruch moze omijac klawisze, ktore "powinny" byc na sladzie
                        double d = double.MaxValue;
                        lock (_mine) if (_mine.TryGetValue(w.Word, out var mine)) foreach (var m in mine) d = Math.Min(d, Distance(g, m, keyW) * 0.8);
                        double tLen = 0; for (int i = 1; i < path.Count; i++) tLen += Dist(path[i - 1], path[i]);
                        bool ok = !(tLen > gLen * 1.8 + keyW || gLen > tLen * 1.8 + keyW);   // zupelnie inna dlugosc
                        // kazda litera slowa musi lezec blisko sladu
                        if (ok) foreach (var p in path) if (g.Min(q => Dist(p, q)) > keyW * 1.1) { ok = false; break; }
                        if (ok) d = Math.Min(d, Distance(g, Resample(path, N), keyW));
                        if (d == double.MaxValue) continue;
                        int used = 0; learned?.TryGetValue(w.Word, out used);
                        // nauka kolejnosci: slowo, ktore zwykle piszesz po poprzednim, wygrywa z podobnym ksztaltem (np. "jestem" -> "michał")
                        int pair = 0; if (prev != null) lock (_pairs) _pairs.TryGetValue(prev + " " + w.Word, out pair);
                        scored.Add((w.Word, d + 0.06 * Math.Log(w.Rank + 2) - Math.Min(0.5, 0.15 * used) - Math.Min(0.4, 0.2 * pair)));
                    }
                }
            // propozycje tylko bliskie najlepszemu - bez przypadkowych, dziwnych slow
            double best = scored.Count > 0 ? scored.Min(x => x.S) : 0;
            // niepewnie: drugie slowo pasuje prawie tak samo - klawiatura zapyta "Poprawic na:"
            var order = scored.Select(x => x.S).OrderBy(x => x).ToList();
            LastUncertain = order.Count > 1 && order[1] - order[0] < 0.08;
            foreach (var x in scored.OrderBy(x => x.S)) { if (x.S > best + 0.25) break; if (!result.Contains(x.W)) result.Add(x.W); if (result.Count >= count) break; }
            return result;
        }

        internal static volatile bool LastUncertain;
        static readonly Dictionary<string, int> _pairs = new Dictionary<string, int>();
        internal static void LearnPair(string prev, string word)
        {
            if (string.IsNullOrEmpty(prev) || string.IsNullOrEmpty(word)) return;
            var k = prev.ToLowerInvariant() + " " + word.ToLowerInvariant();
            lock (_pairs) { _pairs[k] = (_pairs.TryGetValue(k, out var n) ? n : 0) + 1; if (_pairs.Count > 20000) _pairs.Remove(_pairs.OrderBy(x => x.Value).First().Key); }
        }

        static IEnumerable<string> ShapeLines()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            lock (_mine) return _mine.SelectMany(kv => kv.Value.Select(sh => "~" + kv.Key + "\t" + string.Join(" ", sh.Select(p => p.X.ToString("0.#", ci) + "," + p.Y.ToString("0.#", ci))))).ToList();
        }
        static IEnumerable<string> PairLines() { lock (_pairs) return _pairs.Select(kv => "=" + kv.Key + "\t" + kv.Value).ToList(); }

        static void ReadShape(string line)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var t = line.Substring(1).Split('\t'); if (t.Length != 2) return;
            var pts = t[1].Split(' ').Select(q => q.Split(',')).Where(q => q.Length == 2).Select(q => new Point(double.Parse(q[0], ci), double.Parse(q[1], ci))).ToArray();
            if (pts.Length != 32) return;
            lock (_mine) { if (!_mine.TryGetValue(t[0], out var l)) _mine[t[0]] = l = new List<Point[]>(); if (l.Count < 3) l.Add(pts); }
        }

        internal static void SaveLearned(string file, IDictionary<string, int> learned, ISet<string> own)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var text = string.Join("\n", learned.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(5000).Select(kv => kv.Key + "\t" + kv.Value)
                    .Concat(own.Select(w => "+" + w))
                    .Concat(ShapeLines())
                    .Concat(PairLines()));
                // zaszyfrowane kontem Windows - w folderze danych nie ma jawnej listy wpisywanych slow
                File.WriteAllBytes(file, System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(text), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // dopasowanie elastyczne w czasie (DTW): przestrzelenie na zakrecie, zatrzymanie czy nierowne tempo reki nie psuja porownania
        // (punkty sladu i wzorca moga sie "przesunac" o kilka pozycji); wynik w odstepach klawiszy na punkt
        static double Distance(Point[] a, Point[] b, double keyW)
        {
            int n = a.Length, m = b.Length; const int band = 6;
            var D = new double[n + 1, m + 1];
            for (int i = 0; i <= n; i++) for (int j = 0; j <= m; j++) D[i, j] = double.MaxValue;
            D[0, 0] = 0;
            for (int i = 1; i <= n; i++)
                for (int j = Math.Max(1, i - band); j <= Math.Min(m, i + band); j++)
                    D[i, j] = Dist(a[i - 1], b[j - 1]) + Math.Min(D[i - 1, j - 1], Math.Min(D[i - 1, j], D[i, j - 1]));
            return D[n, m] / (n * keyW);
        }

        // nauka ruchu reki: przyjete slowo zapamietuje Twoj slad (do 3 ostatnich na slowo); poprawione - zapomina zly
        static readonly Dictionary<string, List<Point[]>> _mine = new Dictionary<string, List<Point[]>>();
        internal static void LearnShape(string word, IList<Point> trace, double keyW)
        {
            if (string.IsNullOrEmpty(word) || trace == null || trace.Count < 2) return;
            var w = word.ToLowerInvariant();
            lock (_mine)
            {
                if (!_mine.TryGetValue(w, out var l)) _mine[w] = l = new List<Point[]>();
                l.Add(Resample(Steady(trace, keyW), 32)); if (l.Count > 3) l.RemoveAt(0);
            }
        }
        internal static void ForgetShape(string word)
        {
            var w = (word ?? "").ToLowerInvariant();
            lock (_mine) if (_mine.TryGetValue(w, out var l) && l.Count > 0) { l.RemoveAt(l.Count - 1); if (l.Count == 0) _mine.Remove(w); }
        }

        // slowa wybierane przez uzytkownika: "slowo<TAB>ile", a wlasne slowa: "+slowo" w pliku danych
        internal static Dictionary<string, int> LoadLearned(string file, ISet<string> own = null)
        {
            var d = new Dictionary<string, int>();
            try
            {
                if (File.Exists(file))
                    foreach (var line in System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser)).Split('\n'))
                    {
                        if (line.StartsWith("+")) { if (line.Length > 2) own?.Add(line.Substring(1)); continue; }
                        if (line.StartsWith("~")) { ReadShape(line); continue; }
                        if (line.StartsWith("=")) { var q = line.Substring(1).Split('\t'); if (q.Length == 2 && int.TryParse(q[1], out var pc)) lock (_pairs) _pairs[q[0]] = pc; continue; }
                        var p = line.Split('\t');
                        if (p.Length == 2 && int.TryParse(p[1], out var n) && p[0].Length > 0) d[p[0]] = n;
                    }
            }
            catch (Exception) { }
            return d;
        }
    }
}
