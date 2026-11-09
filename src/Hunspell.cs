using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Velivo
{
    // 📖 Pełny słownik polski (Hunspell pl_PL z sjp.pl, licencja Apache 2.0 / MPL / LGPL): słowo bazowe + reguły odmiany = każda forma.
    // Klawiatura: poprawne słowo spoza listy częstych słów zostaje, a literówki w rzadszych słowach (wstwaialo -> wstawiało) mają poprawkę.
    // Ten sam algorytm co w telefonie (Hunspell.kt). Format: SFX/PFX z jednoliterowymi flagami, bez słów złożonych.
    internal sealed class Hunspell
    {
        sealed class Rule { public char Flag; public bool Cross; public string Strip, Add; public Regex Cond; }
        // słownik zwarty (jak w telefonie): posortowane słowa w jednej tablicy znaków + przesunięcia, flagi tak samo - ok. 3× mniej pamięci
        char[] wc = Array.Empty<char>(), fc = Array.Empty<char>(); int[] wo = { 0 }, fo = { 0 }; int n;
        readonly Dictionary<string, List<Rule>> sfxByAdd = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);
        readonly List<Rule> pfx = new List<Rule>();
        int maxAdd;

        public static Hunspell Load(TextReader aff, TextReader dicReader)
        {
            var h = new Hunspell(); string line; var cross = new Dictionary<char, bool>();
            while ((line = aff.ReadLine()) != null)
            {
                var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4 || p[0] != "SFX" && p[0] != "PFX" || p[1].Length != 1) continue;
                char f = p[1][0];
                if (p.Length == 4 && (p[2] == "Y" || p[2] == "N")) { cross[f] = p[2] == "Y"; continue; }   // nagłówek grupy
                if (p.Length < 5) continue;
                string strip = p[2] == "0" ? "" : p[2], add = p[3] == "0" ? "" : p[3].Split('/')[0], cond = p[4];
                var r = new Rule { Flag = f, Cross = cross.TryGetValue(f, out var c) && c, Strip = strip, Add = add,
                    Cond = cond == "." ? null : new Regex(p[0] == "SFX" ? "(" + cond + ")$" : "^(" + cond + ")", RegexOptions.CultureInvariant) };
                if (p[0] == "PFX") h.pfx.Add(r);
                else { if (!h.sfxByAdd.TryGetValue(add, out var l)) h.sfxByAdd[add] = l = new List<Rule>(); l.Add(r); h.maxAdd = Math.Max(h.maxAdd, add.Length); }
            }
            dicReader.ReadLine();   // liczba słów
            var lines = new List<(string W, string F)>(360000);
            while ((line = dicReader.ReadLine()) != null) { if (line.Length == 0) continue; int s = line.IndexOf('/'); lines.Add((s < 0 ? line : line.Substring(0, s), s < 0 ? "" : line.Substring(s + 1))); }
            lines.Sort((a, b) => string.CompareOrdinal(a.W, b.W));
            var wsb = new System.Text.StringBuilder(); var fsb = new System.Text.StringBuilder(); var wo = new int[lines.Count + 1]; var fo = new int[lines.Count + 1]; int k = 0; string prev = null;
            foreach (var (w, fl) in lines)
            {
                if (w == prev) { fsb.Append(fl); fo[k] = fsb.Length; continue; }   // to samo słowo dwa razy - flagi razem
                wsb.Append(w); fsb.Append(fl); k++; wo[k] = wsb.Length; fo[k] = fsb.Length; prev = w;
            }
            h.wc = wsb.ToString().ToCharArray(); h.fc = fsb.ToString().ToCharArray(); h.wo = wo[..(k + 1)]; h.fo = fo[..(k + 1)]; h.n = k;
            return h;
        }

        // flagi słowa (null - brak w słowniku): wyszukiwanie binarne w zwartej tablicy
        string Flags(string w)
        {
            int lo = 0, hi = n - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1, a = wo[mid], b = wo[mid + 1], c = 0, i = 0;
                while (c == 0 && i < w.Length && a + i < b) { c = wc[a + i].CompareTo(w[i]); i++; }
                if (c == 0) c = (b - a).CompareTo(w.Length);
                if (c == 0) return new string(fc, fo[mid], fo[mid + 1] - fo[mid]);
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            return null;
        }

        // czy to poprawna polska forma (wielkość liter: tak jak w słowniku albo małymi)
        public bool Known(string w)
        {
            if (string.IsNullOrEmpty(w)) return false;
            if (Check(w)) return true;
            var lo = w.ToLowerInvariant(); if (lo != w && Check(lo)) return true;
            var cap = char.ToUpperInvariant(lo[0]) + lo.Substring(1); return cap != w && Check(cap);   // imiona i nazwy (Kraków) wpisane małą literą
        }

        bool Check(string w)
        {
            if (Flags(w) != null) return true;
            if (Sfx(w, null)) return true;
            foreach (var r in pfx)   // przedrostek (nie-), także razem z końcówką
            {
                if (!w.StartsWith(r.Add, StringComparison.Ordinal)) continue;
                var stem = r.Strip + w.Substring(r.Add.Length);
                if (r.Cond != null && !r.Cond.IsMatch(stem)) continue;
                var f = Flags(stem); if (f != null && f.IndexOf(r.Flag) >= 0) return true;
                if (r.Cross && Sfx(stem, r)) return true;
            }
            return false;
        }

        bool Sfx(string w, Rule pre)
        {
            for (int k = 0; k <= Math.Min(maxAdd, w.Length - 1); k++)
            {
                if (!sfxByAdd.TryGetValue(w.Substring(w.Length - k), out var rules)) continue;
                var root = w.Substring(0, w.Length - k);
                foreach (var r in rules)
                {
                    var stem = root + r.Strip;
                    if (r.Cond != null && !r.Cond.IsMatch(stem)) continue;
                    var f = Flags(stem); if (f == null || f.IndexOf(r.Flag) < 0) continue;
                    if (pre == null || r.Cross && f.IndexOf(pre.Flag) >= 0) return true;
                }
            }
            return false;
        }

        // poprawka spoza listy częstych słów: polskie znaki i jedna pomyłka (brak, nadmiar, zamiana, przestawienie), potem polskie znaki
        const string Abc = "aąbcćdeęfghijklłmnńoóprsśtuwyzźż";
        static readonly Dictionary<char, string> Pl = new Dictionary<char, string> { ['a'] = "ą", ['c'] = "ć", ['e'] = "ę", ['l'] = "ł", ['n'] = "ń", ['o'] = "ó", ['s'] = "ś", ['z'] = "żź" };
        public string Suggest(string w, Func<string, int> rank)
        {
            string best = null; int br = int.MaxValue, seen = 0; var t0 = System.Diagnostics.Stopwatch.StartNew();
            void Try(string v, int cost) { if (seen++ > 200000 || (seen & 63) == 0 && t0.ElapsedMilliseconds > 120) { seen = 300000; return; } int r = rank(v) + cost; if (r < br && Check(v)) { br = r; best = v; } }   // spacja nie czeka: najwyżej 120 ms; małe litery (klawiatura podaje małe)
            foreach (var v in Diacritics(w)) if (v != w) Try(v, 0);
            if (best != null) return best;
            var edits = new HashSet<string>();
            for (int i = 0; i <= w.Length; i++)
            {
                if (i < w.Length) { edits.Add(w.Remove(i, 1)); if (i + 1 < w.Length) edits.Add(w.Substring(0, i) + w[i + 1] + w[i] + w.Substring(i + 2)); }
                foreach (var c in Abc) { edits.Add(w.Insert(i, c.ToString())); if (i < w.Length && c != w[i]) edits.Add(w.Substring(0, i) + c + w.Substring(i + 1)); }
            }
            foreach (var e in edits) Try(e, 1000000);
            if (best != null) return best;
            // pomyłka palca (sąsiedni klawisz, przestawienie, brak / podwójna litera) + bez polskich znaków (finkcjonalosci -> funkcjonalności)
            if (w.Length >= 5) foreach (var e in Near(w)) foreach (var v in Diacritics(e, 2)) if (v != e) Try(v, 2000000);
            return best;
        }

        // klawiatura QWERTY: sąsiednie klawisze (rząd, rząd wyżej i niżej) - palec trafia obok
        static readonly string[] Rows = { "qwertyuiop", "asdfghjkl", "zxcvbnm" };
        static string Nb(char c)
        {
            for (int r = 0; r < 3; r++) { int i = Rows[r].IndexOf(c); if (i < 0) continue; var s = "";
                for (int dr = -1; dr <= 1; dr++) { int rr = r + dr; if (rr < 0 || rr > 2) continue; for (int di = -1; di <= 1; di++) { int j = i + di + (dr == 0 ? 0 : dr < 0 ? (r == 1 ? 0 : 1) : (r == 0 ? -1 : 0)); if (j >= 0 && j < Rows[rr].Length && Rows[rr][j] != c) s += Rows[rr][j]; } }
                return s; }
            return "";
        }
        static HashSet<string> Near(string w)
        {
            var e = new HashSet<string>();
            for (int i = 0; i < w.Length; i++)
            {
                e.Add(w.Remove(i, 1));
                if (i + 1 < w.Length) e.Add(w.Substring(0, i) + w[i + 1] + w[i] + w.Substring(i + 2));
                foreach (var c in Nb(w[i])) { e.Add(w.Substring(0, i) + c + w.Substring(i + 1)); e.Add(w.Insert(i, c.ToString())); }
                e.Add(w.Insert(i, w[i].ToString()));
            }
            return e;
        }

        // warianty z polskimi znakami: najwyżej 3 litery naraz (źródło, właściwie), w każdym miejscu słowa - nie tylko na początku
        static IEnumerable<string> Diacritics(string w, int maxChanged = 3)
        {
            var pos = new List<int>(); for (int i = 0; i < w.Length; i++) if (Pl.ContainsKey(w[i])) pos.Add(i);
            var res = new List<string> { w }; var cur = new List<string> { w };
            for (int depth = 0; depth < maxChanged; depth++)
            {
                var next = new List<string>();
                foreach (var x in cur) foreach (var i in pos) if (x[i] == w[i]) foreach (var a in Pl[w[i]]) { var y = x.Substring(0, i) + a + x.Substring(i + 1); if (string.CompareOrdinal(y, x) > 0) next.Add(y); }
                if (next.Count == 0 || res.Count + next.Count > 3000) break;
                res.AddRange(next); cur = next;
            }
            return res;
        }
    }
}
