package pl.velivo.klawiatura

import java.io.BufferedReader

// 📖 Pełny słownik polski (Hunspell pl_PL z sjp.pl, licencja Apache 2.0 / MPL / LGPL): słowo bazowe + reguły odmiany = każda forma.
// Ten sam algorytm co w Velivo na Windows (src/Hunspell.cs). Format: SFX/PFX z jednoliterowymi flagami, bez słów złożonych.
class Hunspell private constructor() {
    private class Rule(val flag: Char, val cross: Boolean, val strip: String, val add: String, val cond: Regex?)
    // słownik zwarty (mało pamięci w procesie klawiatury): posortowane słowa w jednej tablicy znaków + przesunięcia, flagi tak samo
    private var wc = CharArray(0); private var wo = IntArray(0); private var fc = CharArray(0); private var fo = IntArray(0); private var n = 0
    private val sfxByAdd = HashMap<String, MutableList<Rule>>()
    private val pfx = ArrayList<Rule>()
    private var maxAdd = 0

    companion object {
        fun load(aff: BufferedReader, dicReader: BufferedReader): Hunspell {
            val h = Hunspell(); val cross = HashMap<Char, Boolean>()
            aff.forEachLine { line ->
                val p = line.trim().split(Regex("\\s+"))
                if (p.size < 4 || (p[0] != "SFX" && p[0] != "PFX") || p[1].length != 1) return@forEachLine
                val f = p[1][0]
                if (p.size == 4 && (p[2] == "Y" || p[2] == "N")) { cross[f] = p[2] == "Y"; return@forEachLine }   // nagłówek grupy
                if (p.size < 5) return@forEachLine
                val strip = if (p[2] == "0") "" else p[2]; val add = if (p[3] == "0") "" else p[3].substringBefore('/'); val c = p[4]
                val r = Rule(f, cross[f] == true, strip, add, if (c == ".") null else Regex(if (p[0] == "SFX") "($c)$" else "^($c)"))
                if (p[0] == "PFX") h.pfx.add(r) else { h.sfxByAdd.getOrPut(add) { ArrayList() }.add(r); h.maxAdd = maxOf(h.maxAdd, add.length) }
            }
            dicReader.readLine()   // liczba słów
            val lines = ArrayList<String>(360000)
            dicReader.forEachLine { if (it.isNotEmpty()) lines.add(it) }
            lines.sortWith(compareBy { it.substringBefore('/') })
            val wsb = StringBuilder(); val fsb = StringBuilder(); val wo = IntArray(lines.size + 1); val fo = IntArray(lines.size + 1); var k = 0; var prev: String? = null
            for (line in lines) {
                val s = line.indexOf('/'); val w = if (s < 0) line else line.substring(0, s); val fl = if (s < 0) "" else line.substring(s + 1)
                if (w == prev) { fsb.append(fl); fo[k] = fsb.length; continue }   // to samo słowo dwa razy - flagi razem
                wsb.append(w); fsb.append(fl); k++; wo[k] = wsb.length; fo[k] = fsb.length; prev = w
            }
            h.wc = CharArray(wsb.length).also { wsb.getChars(0, wsb.length, it, 0) }; h.fc = CharArray(fsb.length).also { fsb.getChars(0, fsb.length, it, 0) }
            h.wo = wo.copyOf(k + 1); h.fo = fo.copyOf(k + 1); h.n = k
            return h
        }
        private const val ABC = "aąbcćdeęfghijklłmnńoóprsśtuwyzźż"
        private val PL = mapOf('a' to "ą", 'c' to "ć", 'e' to "ę", 'l' to "ł", 'n' to "ń", 'o' to "ó", 's' to "ś", 'z' to "żź")
        private val ROWS = arrayOf("qwertyuiop", "asdfghjkl", "zxcvbnm")
        // klawiatura QWERTY: sąsiednie klawisze (rząd, rząd wyżej i niżej) - palec trafia obok
        private fun nb(c: Char): String {
            for (r in 0..2) { val i = ROWS[r].indexOf(c); if (i < 0) continue; val sb = StringBuilder()
                for (dr in -1..1) { val rr = r + dr; if (rr < 0 || rr > 2) continue
                    for (di in -1..1) { val j = i + di + (if (dr == 0) 0 else if (dr < 0) (if (r == 1) 0 else 1) else (if (r == 0) -1 else 0)); if (j >= 0 && j < ROWS[rr].length && ROWS[rr][j] != c) sb.append(ROWS[rr][j]) } }
                return sb.toString() }
            return ""
        }
        private fun near(w: String): Set<String> {
            val e = HashSet<String>()
            for (i in w.indices) {
                e.add(w.removeRange(i, i + 1))
                if (i + 1 < w.length) e.add(w.substring(0, i) + w[i + 1] + w[i] + w.substring(i + 2))
                for (c in nb(w[i])) { e.add(w.substring(0, i) + c + w.substring(i + 1)); e.add(w.substring(0, i) + c + w.substring(i)) }
                e.add(w.substring(0, i) + w[i] + w.substring(i))
            }
            return e
        }
        // warianty z polskimi znakami: najwyżej maxChanged liter naraz, w każdym miejscu słowa
        private fun diacritics(w: String, maxChanged: Int = 3): List<String> {
            val pos = w.indices.filter { PL.containsKey(w[it]) }
            val res = ArrayList<String>(); res.add(w); var cur: List<String> = listOf(w)
            repeat(maxChanged) {
                val next = ArrayList<String>()
                for (x in cur) for (i in pos) if (x[i] == w[i]) for (a in PL[w[i]]!!) { val y = x.substring(0, i) + a + x.substring(i + 1); if (y > x) next.add(y) }
                if (next.isEmpty() || res.size + next.size > 3000) return res
                res.addAll(next); cur = next
            }
            return res
        }
    }

    // flagi słowa (null - brak w słowniku): wyszukiwanie binarne w zwartej tablicy
    private fun flags(w: String): String? {
        var lo = 0; var hi = n - 1
        while (lo <= hi) {
            val mid = (lo + hi) ushr 1; val a = wo[mid]; val b = wo[mid + 1]; var c = 0; var i = 0
            while (c == 0 && i < w.length && a + i < b) { c = wc[a + i].compareTo(w[i]); i++ }
            if (c == 0) c = (b - a).compareTo(w.length)
            if (c == 0) return String(fc, fo[mid], fo[mid + 1] - fo[mid])
            if (c < 0) lo = mid + 1 else hi = mid - 1
        }
        return null
    }

    // czy to poprawna polska forma (także imię / nazwa wpisane małą literą)
    fun known(w: String): Boolean {
        if (w.isEmpty()) return false
        if (check(w)) return true
        val lo = w.lowercase(); if (lo != w && check(lo)) return true
        val cap = lo[0].uppercaseChar() + lo.substring(1); return cap != w && check(cap)
    }

    private fun check(w: String): Boolean {
        if (flags(w) != null) return true
        if (sfx(w, null)) return true
        for (r in pfx) {   // przedrostek (nie-), także razem z końcówką
            if (!w.startsWith(r.add)) continue
            val stem = r.strip + w.substring(r.add.length)
            if (r.cond != null && !r.cond.containsMatchIn(stem)) continue
            val f = flags(stem)
            if (f != null && f.indexOf(r.flag) >= 0) return true
            if (r.cross && sfx(stem, r)) return true
        }
        return false
    }

    private fun sfx(w: String, pre: Rule?): Boolean {
        for (k in 0..minOf(maxAdd, w.length - 1)) {
            val rules = sfxByAdd[w.substring(w.length - k)] ?: continue
            val root = w.substring(0, w.length - k)
            for (r in rules) {
                val stem = root + r.strip
                if (r.cond != null && !r.cond.containsMatchIn(stem)) continue
                val f = flags(stem) ?: continue
                if (f.indexOf(r.flag) < 0) continue
                if (pre == null || (r.cross && f.indexOf(pre.flag) >= 0)) return true
            }
        }
        return false
    }

    // poprawka spoza listy częstych słów: polskie znaki, jedna pomyłka, potem pomyłka palca + polskie znaki (wstwaialo -> wstawiało)
    fun suggest(w: String, rank: (String) -> Int): String? {
        var best: String? = null; var br = Int.MAX_VALUE; var seen = 0; val t0 = System.nanoTime()
        fun tryV(v: String, cost: Int) { if (seen++ > 200000 || ((seen and 63) == 0 && System.nanoTime() - t0 > 120_000_000L)) { seen = 300000; return }; val r = rank(v) + cost; if (r < br && check(v)) { br = r; best = v } }   // spacja nie czeka: najwyżej 120 ms
        for (v in diacritics(w)) if (v != w) tryV(v, 0)
        if (best != null) return best
        val edits = HashSet<String>()
        for (i in 0..w.length) {
            if (i < w.length) { edits.add(w.removeRange(i, i + 1)); if (i + 1 < w.length) edits.add(w.substring(0, i) + w[i + 1] + w[i] + w.substring(i + 2)) }
            for (c in ABC) { edits.add(w.substring(0, i) + c + w.substring(i)); if (i < w.length && c != w[i]) edits.add(w.substring(0, i) + c + w.substring(i + 1)) }
        }
        for (e in edits) tryV(e, 1000000)
        if (best != null) return best
        if (w.length >= 5) for (e in near(w)) for (v in diacritics(e, 2)) if (v != e) tryV(v, 2000000)
        return best
    }
}
