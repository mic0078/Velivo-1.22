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
            _words = list; _byEnds = ends;
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
            var k2 = "" + e.Keys[0] + e.Keys[e.Keys.Length - 1];
            if (!_byEnds.TryGetValue(k2, out var l)) _byEnds[k2] = l = new List<Entry>();
            l.Add(e);
        }

        internal static void RemoveUserWord(string word)
        {
            var w = (word ?? "").ToLowerInvariant();
            if (_words == null) return;
            _words.RemoveAll(e => e.Word == w && e.Rank == 3000);
            foreach (var l in _byEnds.Values) l.RemoveAll(e => e.Word == w && e.Rank == 3000);
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
        internal static List<string> Recognize(IList<Point> trace, IDictionary<char, Point> keys, double keyW, IDictionary<string, int> learned = null, int count = 3)
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
                        scored.Add((w.Word, d + 0.06 * Math.Log(w.Rank + 2) - Math.Min(0.5, 0.15 * used)));
                    }
                }
            // propozycje tylko bliskie najlepszemu - bez przypadkowych, dziwnych slow
            double best = scored.Count > 0 ? scored.Min(x => x.S) : 0;
            foreach (var x in scored.OrderBy(x => x.S)) { if (x.S > best + 0.25) break; if (!result.Contains(x.W)) result.Add(x.W); if (result.Count >= count) break; }
            return result;
        }

        static IEnumerable<string> ShapeLines()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            lock (_mine) return _mine.SelectMany(kv => kv.Value.Select(sh => "~" + kv.Key + "\t" + string.Join(" ", sh.Select(p => p.X.ToString("0.#", ci) + "," + p.Y.ToString("0.#", ci))))).ToList();
        }
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
                    .Concat(ShapeLines()));
                // zaszyfrowane kontem Windows - w folderze danych nie ma jawnej listy wpisywanych slow
                File.WriteAllBytes(file, System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(text), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        static double Distance(Point[] a, Point[] b, double keyW)
        {
            double d = 0; for (int i = 0; i < a.Length; i++) d += Dist(a[i], b[i]);
            return d / (a.Length * keyW);
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
                        var p = line.Split('\t');
                        if (p.Length == 2 && int.TryParse(p[1], out var n) && p[0].Length > 0) d[p[0]] = n;
                    }
            }
            catch (Exception) { }
            return d;
        }
    }
}
