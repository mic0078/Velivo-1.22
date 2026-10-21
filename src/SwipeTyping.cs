using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace Velivo
{
    // Pisanie przeciagnieciem (jak "Flow"): slad palca / myszy po klawiszach porownany z idealna sciezka
    // przez srodki klawiszy kazdego slowa ze slownika; wygrywa najblizszy slad, a przy podobnych - czestsze slowo.
    internal static class SwipeTyping
    {
        // polskie litery przeciaga sie przez litere podstawowa - takze "ź" przez "z" (jak w telefonie)
        // odmiana znanego czasownika (slownik jej nie ma): naprawiaj(cie) <- naprawiać, kupuj(cie) / instaluje / instalujesz <- -ować
        static bool Imperative(string w, ICollection<string> m)
        {
            if (w.Length >= 4 && w.EndsWith("ąc") && m.Contains(w.Substring(0, w.Length - 1))) return true;   // psując <- psują
            var b = w.EndsWith("cie") ? w.Substring(0, w.Length - 3) : w;
            // czasownik na -ować takze z przedrostkiem (instaluje <- zainstalować)
            bool Owac(string stem) => new[] { "", "za", "po", "prze", "wy", "do", "od", "na", "z", "s", "u", "roz", "przy", "w" }.Any(p => m.Contains(p + stem + "ować"));
            if (b.Length >= 4 && (b.EndsWith("aj") && m.Contains(b.Substring(0, b.Length - 1) + "ć") || b.EndsWith("uj") && Owac(b.Substring(0, b.Length - 2)))) return true;
            foreach (var e in new[] { "uje", "ujesz", "ujemy", "ujecie", "ują", "ujący", "ująca", "ujące" })   // instaluje, instalujesz <- instalować
                if (w.Length > e.Length + 2 && w.EndsWith(e) && Owac(w.Substring(0, w.Length - e.Length))) return true;
            return false;
        }

        static Dictionary<int, List<KeyValuePair<string, Entry>>> _byLen;
        // sasiednie klawisze (takze w rzedzie wyzej / nizej) - palec na ekranie trafia obok: b/n, u/i, s/d
        static readonly Dictionary<char, (double R, double C)> _keyPos = new[] { "qwertyuiop", "asdfghjkl", "zxcvbnm" }.SelectMany((row, r) => row.Select((c, i) => (c, r, i)))
            .ToDictionary(x => x.c, x => ((double)x.r, x.i + new[] { 0.0, 0.25, 0.75 }[x.r]));
        static bool Adj(char x, char y) => _keyPos.TryGetValue(x, out var a) && _keyPos.TryGetValue(y, out var b) && Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.C - b.C) <= 1.3;
        // odleglosc w polowkach: litera obok na klawiaturze = 1, inna litera / brak / nadmiar / przestawienie = 2; od "max" wzwyz - wynik = max
        static int Dist(string a, string b, int max, bool soft = true)
        {
            if (Math.Abs(a.Length - b.Length) * 2 >= max) return max;
            var p2 = new int[b.Length + 1]; var p = new int[b.Length + 1]; var c = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) p[j] = j * 2;
            for (int i = 1; i <= a.Length; i++)
            {
                c[0] = i * 2; int low = c[0];
                for (int j = 1; j <= b.Length; j++)
                {
                    int sub = a[i - 1] == b[j - 1] ? 0 : soft && Adj(a[i - 1], b[j - 1]) ? 1 : 2;
                    int v = Math.Min(Math.Min(p[j] + 2, c[j - 1] + 2), p[j - 1] + sub);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, p2[j - 2] + 2);
                    c[j] = v; if (v < low) low = v;
                }
                if (low >= max) return max;
                var t = p2; p2 = p; p = c; c = t;
            }
            return Math.Min(p[b.Length], max);
        }

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
            _words = list; _byEnds = ends; _fold = null; _main = null; _byLen = null;
            lock (_ownFold) _ownFold.Clear();   // Moje slowa dodawane po wczytaniu od nowa (inny jezyk - bez slow z poprzedniego)
            Correct("xx");   // indeks autokorekty od razu (w tle) - przy pisaniu sprawdzenie slowa trwa ulamek milisekundy
        }

        internal static bool Loaded => _words != null;

        // slowo z wbudowanego slownika (bez Moich slow) - w Moim slowniku zbedne
        internal static bool InMain(string word) { Correct("xx"); var m = _main; return m != null && !string.IsNullOrEmpty(word) && m.Contains(word.ToLowerInvariant()); }

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
        // nauka wyborow: ile razy uzytkownik napisal / wybral slowo (ustawia klawiatura) - czesciej uzywane slowa wyzej
        internal static Func<string, int> UseCount;
        static double Score(Entry e) { int n = 0; try { n = UseCount?.Invoke(e.Word) ?? 0; } catch (Exception) { } return e.Rank / (1.0 + 3.0 * Math.Min(n, 50)); }
        static string Fold(string w) { return new string(w.ToLowerInvariant().Select(BaseKey).ToArray()); }
        // sasiednie klawisze (QWERTY) - typowa pomylka palca
        static readonly string[] _rows = { "qwertyuiop", "asdfghjkl", "zxcvbnm" };
        static IEnumerable<char> Near(char c)
        {
            for (int r = 0; r < 3; r++)
            {
                int i = _rows[r].IndexOf(c); if (i < 0) continue;
                if (i > 0) yield return _rows[r][i - 1];
                if (i + 1 < _rows[r].Length) yield return _rows[r][i + 1];
            }
        }
        // anyRank: literowka tez do rzadkiego slowa (do rozpoznania literowek w Moim slowniku, nie do poprawiania w trakcie pisania)
        internal static string Correct(string typed, ICollection<string> keep = null, bool anyRank = false, bool split = true)
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
            lock (_myFix) if (_myFix.TryGetValue(w, out var mf)) return char.IsUpper(typed[0]) ? char.ToUpperInvariant(mf[0]) + mf.Substring(1) : mf;   // Twoja poprawka (skasowane i wpisane od nowa)
            if (w == "ze") return null;
            if (_polite.Contains(w)) return typed[0] == char.ToUpperInvariant(typed[0]) ? null : Polite(w);   // ty -> Ty   // ze / że - rozstrzyga nastepne slowo (ZeAsZe): ze mną, że to
            // stale zwroty pisane blednie razem albo bez polskich znakow: napewno -> na pewno, wogole -> w ogóle
            if (_phraseFix.TryGetValue(Fold(w), out var ph) && ph != w)
                return char.IsUpper(typed[0]) ? char.ToUpperInvariant(ph[0]) + ph.Substring(1) : ph;
            // "nie" z czasownikiem osobno: niewiem -> nie wiem (sklejonego nie ma w slowniku, reszta to czeste slowo); tez bez polskich znakow (niemoge -> nie mogę)
            if (w.Length >= 5 && w.StartsWith("nie", StringComparison.Ordinal))
            {
                var rest = w.Substring(3); string restOk = null;
                if (_foldAll != null && _foldAll.TryGetValue(Fold(rest), out var rl))   // tylko bardzo czeste slowo (wiem, mam, mogę) - rzadkie "niezaplanowany" zostaje razem
                    restOk = rl.Where(e => e.Rank < 4000).OrderBy(e => e.Word == rest ? -1 : Score(e)).Select(e => e.Word).FirstOrDefault();
                if (restOk != null && (IsVerbish(restOk) || !_foldAll.ContainsKey(Fold(w))))   // czasownik - osobno, nawet gdy blad "niewiem" jest w slowniku z tekstow
                    return (char.IsUpper(typed[0]) ? "Nie " : "nie ") + restOk;
            }
            if (main.Contains(w) || Imperative(w, main)) return null;
            // imieslow bez polskich znakow: psujac -> psując, robiac -> robiąc (gdy jest "psują")
            if (w.Length >= 5 && w.EndsWith("ac") && main.Contains(w.Substring(0, w.Length - 2) + "ą"))
            {
                var pc = w.Substring(0, w.Length - 2) + "ąc";
                return char.IsUpper(typed[0]) ? char.ToUpperInvariant(pc[0]) + pc.Substring(1) : pc;
            }
            if (split && w.Length >= 4 && !anyRank && _foldAll != null && !_foldAll.ContainsKey(Fold(w)))
            {   // sklejone dwa znane slowa (otym -> o tym, nastole -> na stole, sietak -> się tak) - przed poprawianiem liter
                string sp = null; int sk = int.MaxValue;
                for (int i = 1; i <= Math.Min(4, w.Length - 2); i++)   // najczestsza para: jakto -> jak to (nie "ja kto")
                {
                    if (!_foldAll.TryGetValue(Fold(w.Substring(0, i)), out var ll) || !_foldAll.TryGetValue(Fold(w.Substring(i)), out var rl)) continue;
                    var le = ll.Where(e => e.Rank < 300).OrderBy(e => e.Rank).FirstOrDefault(); var re = rl.Where(e => e.Rank < (w.Length - i < 3 ? 300 : 120000)).OrderBy(Score).FirstOrDefault();   // robiac - nie "robi ac"
                    if (le == null || re == null) continue;
                    if (le.Rank + re.Rank < sk) { sk = le.Rank + re.Rank; sp = le.Word + " " + re.Word; }
                }
                if (sp != null) return char.IsUpper(typed[0]) ? char.ToUpperInvariant(sp[0]) + sp.Substring(1) : sp;
            }
            lock (_ownFold) if (_ownFold.ContainsValue(w)) return null;   // Moje slowo - bez zmian   // slowo ze slownika - bez zmian (wyuczone z pisania nie blokuje poprawki: dawniej uczyly sie tez literowki, np. zolw)
            var fw = new string(w.Select(BaseKey).ToArray());
            Entry best = null;
            if (_foldAll != null && _foldAll.TryGetValue(fw, out var sameAll)) best = sameAll.OrderBy(Score).First();   // tylko polskie znaki (najczesciej wybierane przez Ciebie najpierw)
            else if (fold.TryGetValue(fw, out var same)) best = same;
            string ownSame; lock (_ownFold) _ownFold.TryGetValue(fw, out ownSame);
            if (best == null && ownSame != null && ownSame != w) best = new Entry { Word = ownSame, Rank = 3000 };   // Moje slowo bez polskich znakow
            else if (best == null && w.Length >= 4)   // jedna litera rozni (krotkich slow nie zgadujemy)
            {
                const string abc = "abcdefghijklmnopqrstuvwxyz";
                double bk = double.MaxValue;   // ostatnia litera zwykle wpisana dobrze: dzialc -> działać (nie działce)
                Action<string> cand = v => { if (fold.TryGetValue(v, out var e) && (anyRank || e.Rank < 15000)) { var key = Score(e) * (v.Length > 0 && v[v.Length - 1] == fw[fw.Length - 1] ? 1.0 : 50.0); if (key < bk) { bk = key; best = e; } } };   // literowka -> tylko czeste slowo (rzadkie poprawne slowo spoza slownika zostaje)
                for (int i = 0; i < fw.Length; i++)
                {
                    cand(fw.Remove(i, 1));
                    if (i + 1 < fw.Length) cand(fw.Substring(0, i) + fw[i + 1] + fw[i] + fw.Substring(i + 2));
                    foreach (var c in abc) { if (c != fw[i]) cand(fw.Substring(0, i) + c + fw.Substring(i + 1)); cand(fw.Insert(i, c.ToString())); }
                }
                foreach (var c in abc) cand(fw + c);
            }
            if (best == null && w.Length >= 6 && !anyRank)
            {   // dluzsze slowo: najblizsze znane slowo (takze rzadsze): jedna pomylka (windos -> windows) albo dwie, gdy zgadza sie poczatek
                // (literokem -> literówek; dobre slowo spoza slownika, np. kompiluje, zostaje)
                var idx = _byLen;
                if (idx == null) { idx = new Dictionary<int, List<KeyValuePair<string, Entry>>>(); foreach (var kv in fold) { if (!idx.TryGetValue(kv.Key.Length, out var l)) idx[kv.Key.Length] = l = new List<KeyValuePair<string, Entry>>(); l.Add(kv); } _byLen = idx; }
                int bd = 5, bl = 0;   // w polowkach: do 2 pomylek (4), pomylka o sasiedni klawisz = pol
                for (int len = fw.Length - 1; len <= fw.Length + 1; len++)
                    if (idx.TryGetValue(len, out var group))
                        foreach (var kv in group)
                        {
                            if (kv.Key[0] != fw[0] && !Adj(kv.Key[0], fw[0]) || kv.Value.Rank >= 120000) continue;
                            int d = Dist(fw, kv.Key, Math.Min(bd + 1, 5));
                            // dwie pomylki "nie obok" (kompiluje / kombinuje) - tylko przy zgodnym poczatku albo koncowce (idelelnie -> idealnie)
                            if (d > 2 && d - Dist(fw, kv.Key, 9, false) / 2 >= 2) { int p = 0, q = 0; while (p < fw.Length && p < kv.Key.Length && fw[p] == kv.Key[p]) p++; while (q < fw.Length && q < kv.Key.Length && fw[fw.Length - 1 - q] == kv.Key[kv.Key.Length - 1 - q]) q++; if (p * 2 < fw.Length && q < 4) continue; }   // idelelnie -> idealnie, kompiluje zostaje
                            // remis: ta sama dlugosc (zamiana litery) przed brakiem / nadmiarem, potem czestsze
                            bool same = kv.Key.Length == fw.Length, bsame = bl == fw.Length;
                            if (d < 5 && (d < bd || d == bd && (same && !bsame || same == bsame && Score(kv.Value) < Score(best)))) { bd = d; best = kv.Value; bl = kv.Key.Length; }
                        }
            }
            if (best == null && w.Length >= 4 && !anyRank)
            {   // dwie pomylki palca: litera sasiada na klawiaturze + jedna inna (eicej -> więcej) - tylko bardzo czeste slowa
                const string abc = "abcdefghijklmnopqrstuvwxyz";
                Action<string> cand = v => { if (fold.TryGetValue(v, out var e) && e.Rank < (w.Length >= 6 ? 15000 : 3000) && (best == null || Score(e) < Score(best))) best = e; };
                for (int i = 0; i < fw.Length; i++)
                    foreach (var nb in Near(fw[i]))
                    {
                        var x = fw.Substring(0, i) + nb + fw.Substring(i + 1);
                        for (int j = 0; j <= x.Length; j++)
                        {
                            if (j < x.Length) { cand(x.Remove(j, 1)); if (j != i) foreach (var c in abc) if (c != x[j]) cand(x.Substring(0, j) + c + x.Substring(j + 1)); }
                            foreach (var c in abc) cand(x.Insert(j, c.ToString()));
                        }
                    }
            }
            if (best == null && split && w.Length >= 5 && !anyRank)
            {   // sklejone slowa (michodziloe -> mi chodziło): czeste krotkie slowo + znane albo poprawione slowo
                for (int i = 2; i <= Math.Min(4, w.Length - 3); i++)   // z poprawka drugiej czesci tylko po dluzszym slowie (mi chodziło; nie "o danielowi")
                {
                    var l = w.Substring(0, i);
                    if (_foldAll == null || !_foldAll.TryGetValue(Fold(l), out var le) || !le.Any(e => e.Word == l && e.Rank < 300)) continue;
                    var rest = w.Substring(i);
                    var rw = main.Contains(rest) ? rest : Correct(rest, keep, false, false)?.ToLowerInvariant();
                    if (rw == null || rw.Contains(' ')) continue;
                    var sp = l + " " + rw;
                    return char.IsUpper(typed[0]) ? char.ToUpperInvariant(sp[0]) + sp.Substring(1) : sp;
                }
            }
            if (best == null || best.Word == w) return null;
            var r = Polite(best.Word);
            if (typed.All(char.IsUpper) && typed.Length > 1) return r.ToUpperInvariant();
            return char.IsUpper(typed[0]) ? char.ToUpperInvariant(r[0]) + r.Substring(1) : r;
        }

        // spacja: poprawka, a gdy jej nie ma - dokonczenie widoczne w pasku propozycji (klawi -> klawiatura).
        // Poprawne slowo (dom, nie) i Moje slowa zostaja jak sa; dokonczenie tylko do czestego slowa
        internal static string Complete(string typed, ICollection<string> keep = null)
        {
            var fix = Correct(typed, keep);
            if (fix != null && (fix.Contains(' ') || Fold(fix) == Fold(typed))) return fix;   // polskie znaki, napewno, niewiem - pewna poprawka
            if (_main == null || typed.Length < 3 || !typed.All(char.IsLetter)) return fix;
            var w = typed.ToLowerInvariant();
            if (keep != null && keep.Contains(w)) return fix;
            if (Imperative(w, _main)) return fix;   // naprawiaj - dobre slowo, bez dokanczania do "naprawiają"
            lock (_ownFold) if (_ownFold.ContainsValue(w)) return fix;
            var p = Predict(typed, false, 3).FirstOrDefault(x => x.Length > typed.Length && Fold(x).StartsWith(Fold(w), StringComparison.Ordinal));   // pierwsze dokonczenie w pasku propozycji
            if (p == null || _foldAll == null) return fix;
            bool ownP; lock (_ownFold) ownP = _ownFold.ContainsValue(p.ToLowerInvariant());
            if (!_foldAll.TryGetValue(Fold(p), out var pl)) return ownP ? p : fix;   // Moje slowo (imie, nazwisko) - tez wstawiamy
            var pe = pl.OrderBy(Score).First(); if (pe.Rank >= 60000 && !ownP) return fix;   // bardzo rzadkie slowo - nie zgadujemy
            // brakujaca litera czestego slowa wygrywa z dokonczeniem rzadszego: zarz -> zaraz (nie zarzuty); komp -> komputer zostaje
            if (fix != null && fix.Length == typed.Length + 1 && _foldAll.TryGetValue(Fold(fix), out var fl) && Score(fl.OrderBy(Score).First()) <= Score(pe)) return fix;
            if (_main.Contains(w))
            {   // wpisane to tez slowo: zostaje, chyba ze rzadkie, a dokonczenie duzo czestsze (pra -> prawda; dom, nie, kot zostaja)
                var we = _foldAll.TryGetValue(Fold(w), out var wl) ? wl.FirstOrDefault(e => e.Word == w) : null;
                if (we == null || Score(we) < 5000 || Score(pe) * 4 > Score(we)) return fix;
            }
            return p;
        }

        // watpliwosc (slowa nie ma w slowniku, a pewnej poprawki brak): do 3 propozycji - polskie znaki albo jedna litera, takze rzadkie slowa
        internal static List<string> Suggest(string typed, ICollection<string> keep = null, int count = 3)
        {
            var res = new List<string>();
            if (_words == null || string.IsNullOrEmpty(typed) || typed.Length < 3 || !typed.All(char.IsLetter)) return res;
            var w = typed.ToLowerInvariant();
            if (keep != null && keep.Contains(w)) return res;
            Correct("xx"); var fold = _fold; var main = _main; if (fold == null || main == null || main.Contains(w) || Imperative(w, main)) return res;
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
            if (found.Count == 0 && fw.Length >= 6)
            {   // duzo bledow (interezuskdal): najblizsze slowa tylko jako propozycje - bez zamiany
                if (_byLen == null) Correct("xxxxxxx");
                var near = new List<(int D, Entry E)>();
                for (int len = fw.Length - 2; len <= fw.Length + 2; len++)
                    if (_byLen != null && _byLen.TryGetValue(len, out var group))
                        foreach (var kv in group) { if (kv.Key[0] != fw[0] && !Adj(kv.Key[0], fw[0]) || kv.Value.Rank >= 120000) continue; int d = Dist(fw, kv.Key, 9); if (d < 9) near.Add((d, kv.Value)); }
                foreach (var e in near.OrderBy(x => x.D).ThenBy(x => Score(x.E)).Take(count).Select(x => x.E))
                    res.Add(char.IsUpper(typed[0]) ? char.ToUpperInvariant(e.Word[0]) + e.Word.Substring(1) : e.Word);
                return res;
            }
            foreach (var e in found.Values.OrderBy(Score).Take(count))
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
            if (all.TryGetValue(fw, out var same)) foreach (var e in same.OrderBy(Score)) add(e.Word);
            lock (_ownFold) if (_ownFold.TryGetValue(fw, out var ow)) add(ow);
            if (!done && fw.Length >= 2 && res.Count < count)
            {
                int lo = 0, hi = sf.Length;
                while (lo < hi) { int mid = (lo + hi) / 2; if (string.CompareOrdinal(sf[mid], fw) < 0) lo = mid + 1; else hi = mid; }
                var comp = new List<Entry>();
                for (int i = lo; i < sf.Length && i < lo + 4000 && sf[i].StartsWith(fw, StringComparison.Ordinal); i++) if (sf[i].Length > fw.Length) comp.Add(se[i]);
                foreach (var e in comp.OrderBy(Score)) add(e.Word);
                lock (_ownFold) foreach (var o in _ownFold.Where(kv => kv.Key.StartsWith(fw, StringComparison.Ordinal) && kv.Key.Length > fw.Length).Select(kv => kv.Value).OrderBy(x => x)) add(o);
            }
            if (typed.Length > 0 && char.IsUpper(typed[0])) for (int i = 0; i < res.Count; i++) res[i] = char.ToUpperInvariant(res[i][0]) + res[i].Substring(1);
            return res;
        }

        // literowka znanego slowa (zolw, zzolw) - nie do Mojego slownika; slowo z polskimi znakami (klawiaturą) piszesz swiadomie - to nie literowka
        internal static bool IsTypo(string w) { return !string.IsNullOrEmpty(w) && (IsMisspelling(w) || w.IndexOfAny("ąćęłńóśźżĄĆĘŁŃÓŚŹŻ".ToCharArray()) < 0 && Correct(w, null, true) != null); }
        // bledna pisownia (napewno, niewiem) - nigdy do Mojego slownika ani do slow bez poprawiania
        internal static bool IsMisspelling(string w) { var c = string.IsNullOrEmpty(w) ? null : Correct(w); return c != null && c.Contains(' '); }

        // czasownik (po koncowce albo jedna z najczestszych form) - z "nie" zawsze osobno (slownik z tekstow ma tez bledy: niewiem, niemam)
        static readonly HashSet<string> _verbs = new HashSet<string> { "ma", "jest", "są", "może", "musi", "chce", "wie", "będzie", "było", "trzeba", "wolno", "umie", "zna", "lubi", "widzi", "mówi", "robi", "idzie", "je", "pije", "śpi", "daje", "da", "żyje", "działa", "pasuje", "wiadomo", "warto", "boli", "mam", "wiem", "umiem", "jem", "dam", "znam", "chcę", "mogę", "lubię", "rozumiem", "pamiętam" };
        internal static bool IsVerbish(string w)
        {
            if (string.IsNullOrEmpty(w)) return false;
            w = w.ToLowerInvariant();
            return _verbs.Contains(w) || new[] { "ć", "ł", "ła", "ło", "li", "ły", "my", "cie", "sz", "ę", "em", "am" }.Any(x => w.EndsWith(x, StringComparison.Ordinal) && w.Length > x.Length + 1);
        }

        // czeste bledy w pisowni stalych zwrotow (klucz bez polskich znakow)
        static readonly Dictionary<string, string> _phraseFix = new Dictionary<string, string> {
            ["napewno"] = "na pewno", ["narazie"] = "na razie", ["wogole"] = "w ogóle", ["poprostu"] = "po prostu", ["naprzyklad"] = "na przykład",
            ["conajmniej"] = "co najmniej", ["conajwyzej"] = "co najwyżej", ["spowrotem"] = "z powrotem", ["zpowrotem"] = "z powrotem", ["wkoncu"] = "w końcu",
            ["napoczatku"] = "na początku", ["wszczegolnosci"] = "w szczególności", ["pozatym"] = "poza tym", ["wrazie"] = "w razie",
            ["nazewnatrz"] = "na zewnątrz", ["dowidzenia"] = "do widzenia", ["naszczescie"] = "na szczęście", ["przedewszystkim"] = "przede wszystkim",
            ["ztad"] = "stąd", ["zkad"] = "skąd" };
        // zwracajac sie do kogos - wielka litera (grzecznosc w listach i wiadomosciach): ty -> Ty, cie -> Cię, twoja -> Twoja
        static readonly HashSet<string> _polite = new HashSet<string> { "ty", "ci", "cię", "ciebie", "tobie", "tobą", "twój", "twoja", "twoją", "twoje", "twojego", "twojej", "twojemu", "twoim", "twoich", "twoimi" };
        static string Polite(string r) { return r != null && _polite.Contains(r) ? char.ToUpperInvariant(r[0]) + r.Substring(1) : r; }
        // dwa slowa, ktore piszemy razem: na prawdę -> naprawdę (klucz bez polskich znakow)
        static readonly Dictionary<string, string> _pairFix = new Dictionary<string, string> {
            ["na prawde"] = "naprawdę", ["na przeciwko"] = "naprzeciwko", ["na przeciw"] = "naprzeciw", ["z posrod"] = "spośród", ["z pod"] = "spod",
            ["z nad"] = "znad", ["po mimo"] = "pomimo", ["z reszta"] = "zresztą", ["na reszcie"] = "nareszcie", ["do okola"] = "dookoła" };
        internal static string JoinPair(string prev, string word)
        {
            if (string.IsNullOrEmpty(prev) || string.IsNullOrEmpty(word)) return null;
            if (!_pairFix.TryGetValue(Fold(prev.ToLowerInvariant()) + " " + Fold(word.ToLowerInvariant()), out var j)) return null;
            return char.IsUpper(prev[0]) ? char.ToUpperInvariant(j[0]) + j.Substring(1) : j;
        }

        // "nie" + slowo, ktore piszemy razem (przymiotnik, przyslowek, rzeczownik): nie dobry -> niedobry - propozycja, nie automat (bywa "nie ma" / "niema")
        internal static string JoinNie(string prev, string word)
        {
            if (_main == null || string.IsNullOrEmpty(word) || !string.Equals(prev, "nie", StringComparison.OrdinalIgnoreCase) || !word.All(char.IsLetter)) return null;
            var j = "nie" + word.ToLowerInvariant();
            return _main.Contains(j) && !IsVerbish(word) ? (char.IsUpper(prev[0]) ? "N" + j.Substring(1) : j) : null;
        }

        // interpunkcja: przecinek przed spojnikami i zaimkami wzglednymi (wiem, że...; dom, który...; idę, bo...).
        // Bez przecinka, gdy slowo zaczyna zdanie albo poprzednie slowo to czesc spojnika (mimo że, chyba że, zwłaszcza że)
        // albo spojnik laczacy (i że, a że, oraz że, lub że, czy że, albo że) - tam przecinka nie stawiamy.
        static readonly HashSet<string> _commaBefore = new HashSet<string> { "że", "żeby", "aby", "bo", "ale", "lecz", "gdyż", "ponieważ", "jeśli", "jeżeli", "chociaż", "choć", "czyli", "a", "więc", "zatem", "natomiast", "gdy", "kiedy", "gdzie", "dlaczego", "jakby",
            "który", "która", "które", "którego", "której", "któremu", "którym", "którą", "których", "którzy", "którymi" };
        static readonly HashSet<string> _noCommaAfter = new HashSet<string> { "mimo", "pomimo", "chyba", "zwłaszcza", "tylko", "dlatego", "tak", "podczas", "zanim", "nim", "przy", "po", "w", "z", "na", "do", "o", "od", "za", "przez", "dla", "u", "tam", "wtedy", "zawsze", "nawet", "właśnie", "jak", "jakby", "kiedy", "gdy", "gdzie", "dlaczego", "więc", "zatem", "natomiast",
            "i", "a", "oraz", "lub", "albo", "czy", "ani", "ni", "bądź", "że", "bo", "ale", "aby", "żeby", "lecz", "to", "co", "jak", "niż" };
        // po tych slowach "ze" to prawie zawsze "że" - poprawiamy od razu po spacji (myślę ze -> myślę, że), bez czekania na nastepne slowo
        static readonly HashSet<string> _zeHeads = new HashSet<string> { "myślę", "myśli", "myślisz", "myślał", "myślała", "myśleliśmy", "wiem", "wiesz", "wie", "wiemy", "wiedział", "wiedziała",
            "mówię", "mówi", "mówisz", "mówił", "mówiła", "mówią", "powiem", "powiedział", "powiedziała", "powiedziałem", "powiedziałam", "sądzę", "sądzi", "uważam", "uważa", "uważasz",
            "czuję", "widzę", "widać", "widzisz", "słyszę", "słyszałem", "słyszałam", "nadzieję", "wydaje", "wydawało", "okazało", "okazuje", "pamiętam", "pamiętaj", "zapomniałem", "zapomniałam",
            "cieszę", "obiecuję", "obiecałem", "twierdzi", "twierdzę", "przyznaję", "wierzę", "wątpię", "rozumiem", "napisał", "napisała", "pisze", "dowiedziałem", "dowiedziałam",
            "prawda", "pewnie", "jasne", "ważne", "możliwe", "szkoda", "dobrze", "fakt", "znaczy", "chodzi", "tak", "chyba", "mimo", "pomimo", "to", "dlatego", "zwłaszcza", "tylko", "się", "sam", "sama" };
        internal static string ZeNow(string ze, string prev)
        {
            if (!string.Equals(ze, "ze", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(prev) || !prev.All(char.IsLetter)) return null;
            if (!_zeHeads.Contains(prev.ToLowerInvariant()) && !IsVerbish(prev)) return null;   // po czasowniku (powiedzieć ze, wiedział ze) - tez "że"; "ze mną" dalej wraca do "ze"
            if (PairCount(prev.ToLowerInvariant() + " ze") > PairCount(prev.ToLowerInvariant() + " że")) return null;   // u Ciebie po tym slowie zwykle "ze" (wyuczone)
            return char.IsUpper(ze[0]) ? "Że" : "że";
        }

        // "ze" zamiast "że" (bez AltGr): rozpoznane po nastepnym slowie. Przyimek zostaje: ze mną, ze szkoły, ze względu, ze wszystkimi;
        // "że" przed zwyklym slowem albo czasownikiem: ze to -> że to, ze zrobił -> że zrobił. Zwraca zamiennik "ze" (z przecinkiem) albo null
        internal static string ZeAsZe(string ze, string next, string before)
        {
            if (!string.Equals(ze, "ze", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(next) || !next.All(char.IsLetter)) return null;
            var n = next.ToLowerInvariant(); const string v = "aąeęioóuy";
            int ze2 = PairCount("ze " + n), zz = PairCount("że " + n);   // kontekst wyuczony z Twojego pisania ma pierwszenstwo
            bool prep = ze2 != zz ? ze2 > zz : n.StartsWith("mn", StringComparison.Ordinal) || n == "sobą" || n == "sobie"
                || n.StartsWith("wszystk", StringComparison.Ordinal) && n != "wszystko" && n != "wszystkie"
                || n.Length > 1 && "szśźżwfł".IndexOf(n[0]) >= 0 && v.IndexOf(n[1]) < 0 && !n.StartsWith("wszys", StringComparison.Ordinal)
                    && (n[0] != 'w' || "szłrnśżź".IndexOf(n[1]) >= 0)   // ze wsi, ze względu, ze włosów; ale że wpisywanie, że wcale
                    && !new[] { "ł", "łem", "łam", "łeś", "łaś", "ć" }.Any(x => n.EndsWith(x, StringComparison.Ordinal));   // ze zrobił -> że zrobił
            if (prep) return null;
            var r = char.IsUpper(ze[0]) ? "Że" : "że";
            return NeedsComma("że", before) ? ", " + r : r;
        }

        internal static bool NeedsComma(string word, string prev)
        {
            if (string.IsNullOrEmpty(word) || string.IsNullOrEmpty(prev) || !prev.All(char.IsLetter)) return false;
            return _commaBefore.Contains(word.ToLowerInvariant()) && !_noCommaAfter.Contains(prev.ToLowerInvariant());
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

        // trojki slow (zwroty): "daj znać" -> "jak"; ten sam zapis co pary (klucz z dwiema spacjami)
        // Twoje poprawki: slowo skasowane i wpisane od nowa (kmoputer -> komputer) - nastepnym razem poprawiane samo
        static readonly Dictionary<string, string> _myFix = new Dictionary<string, string>();
        internal static bool LearnFix(string typo, string fix)
        {
            if (string.IsNullOrEmpty(typo) || string.IsNullOrEmpty(fix) || typo.Length < 3 || fix.Length < 2 || !typo.All(char.IsLetter) || !fix.All(char.IsLetter)) return false;
            string t = typo.ToLowerInvariant(), f = fix.ToLowerInvariant();
            if (t == f || _main == null || _main.Contains(t) || Edits(t, f) > Math.Max(1, Math.Min(2, t.Length / 3))) return false;   // poprawne slowo zmienione na inne albo zupelnie inne slowo - to nie literowka
            lock (_myFix) { _myFix[t] = f; if (_myFix.Count > 2000) _myFix.Remove(_myFix.Keys.First()); }
            return true;
        }
        // odleglosc edycyjna z zamiana sasiednich liter (kmoputer / komputer = 1)
        static int Edits(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            return d[a.Length, b.Length];
        }
        static IEnumerable<string> FixLines() { lock (_myFix) return _myFix.Select(kv => ">" + kv.Key + "\t" + kv.Value).ToList(); }

        // cale zdania: czesto powtarzane (co najmniej 2 razy) podsuwane na pasku nad propozycjami, Ctrl+Spacja wstawia reszte
        static readonly Dictionary<string, int> _sentences = new Dictionary<string, int>();
        internal static void LearnSentence(string text)
        {
            text = text?.Trim(); if (string.IsNullOrEmpty(text) || text.Length < 8 || text.Length > 200 || text.Count(c => c == ' ') < 2) return;
            lock (_sentences) { _sentences[text] = (_sentences.TryGetValue(text, out var n) ? n : 0) + 1; if (_sentences.Count > 500) _sentences.Remove(_sentences.OrderBy(x => x.Value).First().Key); }
        }
        internal static string SentenceRest(string prefix)
        {
            prefix = prefix?.TrimStart(); if (string.IsNullOrEmpty(prefix) || prefix.Length < 4) return null;
            lock (_sentences)
            {
                var best = _sentences.Where(kv => kv.Value >= 2 && kv.Key.Length > prefix.Length + 2 && kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderByDescending(kv => kv.Value).FirstOrDefault();
                return best.Key?.Substring(prefix.Length);
            }
        }
        static IEnumerable<string> SentenceLines() { lock (_sentences) return _sentences.Select(kv => "*" + kv.Value + "\t" + kv.Key).ToList(); }
        // biezace zdanie (slowa juz w tekscie, z przecinkami) - do nauki i podpowiedzi zdan
        internal sealed class SentenceBuf
        {
            readonly List<string> _w = new List<string>(); readonly List<bool> _comma = new List<bool>();
            internal void Word(string w, bool commaBefore, bool commaAfter)
            {
                lock (_w) { if (commaBefore && _comma.Count > 0) _comma[_comma.Count - 1] = true; _w.Add(w); _comma.Add(commaAfter); if (_w.Count > 40) Clear(); }
            }
            internal void Replace(string last, bool commaBefore) { lock (_w) if (_w.Count > 0) { _w[_w.Count - 1] = last; if (commaBefore && _w.Count > 1) _comma[_w.Count - 2] = true; } }
            internal void RemoveLast() { lock (_w) if (_w.Count > 0) { _w.RemoveAt(_w.Count - 1); _comma.RemoveAt(_comma.Count - 1); } }
            internal void Clear() { lock (_w) { _w.Clear(); _comma.Clear(); } }
            internal string Text(bool trailing) { lock (_w) return string.Concat(_w.Select((x, i) => x + (i < _w.Count - 1 || trailing ? (_comma[i] ? ", " : " ") : ""))); }
        }

        static int PairCount(string k) { lock (_pairs) return _pairs.TryGetValue(k, out var c) ? c : 0; }

        // kontekst z Twojego pisania: po tym slowie zwykle piszesz inne, bardzo podobne (to pewni -> to pewnie) - po co najmniej 3 razach
        internal static string ContextFix(string prev, string word, ICollection<string> keep = null)
        {
            if (string.IsNullOrEmpty(prev) || word == null || word.Length < 3 || !word.All(char.IsLetter)) return null;
            var p = prev.ToLowerInvariant(); var w = word.ToLowerInvariant();
            if (keep != null && keep.Contains(w) || PairCount(p + " " + w) > 0) return null;
            string best = null; int bn = 2;
            lock (_pairs)
                foreach (var kv in _pairs)
                {
                    if (kv.Value <= bn || !kv.Key.StartsWith(p + " ", StringComparison.Ordinal)) continue;
                    var c = kv.Key.Substring(p.Length + 1); if (c == w || c.Contains(' ')) continue;
                    if (Dist(Fold(w), Fold(c), 3) <= 2) { best = c; bn = kv.Value; }
                }
            return best == null ? null : char.IsUpper(word[0]) ? char.ToUpperInvariant(best[0]) + best.Substring(1) : best;
        }
        internal static void LearnTriple(string prev2, string prev, string word)
        {
            if (string.IsNullOrEmpty(prev2) || string.IsNullOrEmpty(prev) || string.IsNullOrEmpty(word)) return;
            LearnPair(prev2.ToLowerInvariant() + " " + prev.ToLowerInvariant(), word);
        }

        static List<KeyValuePair<string, int>> Following(string ctx)
        {
            var k = ctx + " ";
            lock (_pairs) return _pairs.Where(kv => kv.Key.StartsWith(k, StringComparison.Ordinal) && kv.Key.IndexOf(' ', k.Length) < 0 && kv.Value >= 2).OrderByDescending(kv => kv.Value).ToList();
        }

        // nastepne slowo: najpierw po dwoch poprzednich (zwrot), potem po jednym; plus caly zwrot, gdy pisany czesto (>= 3 razy)
        internal static List<string> NextWords(string prev, string prev2 = null, int count = 3)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(prev)) return res;
            string p = prev.ToLowerInvariant(), p2 = prev2?.ToLowerInvariant();
            var cands = new List<KeyValuePair<string, int>>();
            if (!string.IsNullOrEmpty(p2)) cands.AddRange(Following(p2 + " " + p).Select(kv => new KeyValuePair<string, int>(kv.Key, kv.Value + 1000)));   // zwrot ma pierwszenstwo
            cands.AddRange(Following(p));
            foreach (var kv in cands) { var w = kv.Key.Substring(kv.Key.LastIndexOf(' ') + 1); if (!res.Contains(w)) res.Add(w); }
            // caly zwrot: od najpewniejszego slowa dalej, poki kolejne slowo jest czeste (najwyzej 5 slow)
            if (res.Count > 0)
            {
                var phrase = new List<string> { res[0] }; string a = p, b = res[0];
                for (int i = 0; i < 4; i++)
                {
                    var nx = Following(a + " " + b).FirstOrDefault();
                    if (nx.Key == null || nx.Value < 3) break;
                    var w = nx.Key.Substring(nx.Key.LastIndexOf(' ') + 1); phrase.Add(w); a = b; b = w;
                }
                if (phrase.Count >= 2) res.Insert(0, string.Join(" ", phrase));
            }
            return res.Take(count).ToList();
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
                    .Concat(PairLines())
                    .Concat(FixLines())
                    .Concat(SentenceLines()));
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
                        if (line.StartsWith("*")) { var q = line.Substring(1).Split('\t'); if (q.Length == 2 && int.TryParse(q[0], out var sc) && q[1].Length > 0) lock (_sentences) _sentences[q[1]] = sc; continue; }
                        if (line.StartsWith(">")) { var q = line.Substring(1).Split('\t'); if (q.Length == 2 && q[0].Length > 0 && q[1].Length > 0) lock (_myFix) _myFix[q[0]] = q[1]; continue; }
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
