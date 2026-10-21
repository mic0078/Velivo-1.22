package pl.velivo.klawiatura

import kotlin.math.ln
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sqrt

// Słownik klawiatury Velivo - przeniesiony 1:1 z Windows (SwipeTyping.cs): autokorekta, propozycje, dokończenia,
// pisanie przeciągnięciem (ślad palca porównany z idealną ścieżką przez środki klawiszy), nauka słów, par, zdań i kształtów.
object Slownik {
    class P(val x: Double, val y: Double)

    // polskie litery przeciąga się przez literę podstawową - także "ź" przez "z"
    fun baseKey(c: Char): Char { val i = "ąćęłńóśżź".indexOf(c); return if (i >= 0) "acelnoszz"[i] else c }

    // wolny ruch ręki z drżeniem: wygładzenie i pominięcie drobnych drgań
    fun steady(pts: List<P>, keyW: Double): List<P> {
        val sm = ArrayList<P>()
        for (i in pts.indices) {
            var x = 0.0; var y = 0.0; var n = 0
            for (j in max(0, i - 3)..min(pts.size - 1, i + 3)) if (dist(pts[j], pts[i]) <= keyW * 0.35) { x += pts[j].x; y += pts[j].y; n++ }
            sm.add(P(x / n, y / n))
        }
        val res = arrayListOf(sm[0])
        for (p in sm) if (dist(p, res.last()) >= keyW * 0.45) res.add(p)
        if (dist(res.last(), sm.last()) > 0.5) res.add(sm.last())
        res[0] = pts[0]; res[res.size - 1] = pts.last()
        return res
    }

    private class Entry(val word: String, val keys: String, val rank: Int)
    @Volatile private var words: MutableList<Entry>? = null
    private var byEnds: MutableMap<String, MutableList<Entry>> = HashMap()

    // słownik: jedno słowo w wierszu, od najczęstszego
    @Synchronized fun load(list: List<String>) {
        val l = ArrayList<Entry>(); val ends = HashMap<String, MutableList<Entry>>()
        var rank = 0
        for (w0 in list) {
            val w = w0.trim().lowercase(); if (w.isEmpty()) continue
            val keys = w.map(::baseKey).joinToString("")
            if (!keys.all { it in 'a'..'z' }) continue
            val e = Entry(w, collapse(keys), rank++)
            l.add(e)
            ends.getOrPut("" + e.keys[0] + e.keys.last()) { ArrayList() }.add(e)
        }
        words = l; byEnds = ends; fold = null; main = null; byLen = null
        synchronized(ownFold) { ownFold.clear() }
        correct("xx")
    }

    val loaded get() = words != null

    fun inMain(word: String): Boolean { correct("xx"); val m = main; return m != null && word.isNotEmpty() && m.contains(word.lowercase()) }
    fun isKnown(word: String): Boolean { val w = word.lowercase(); return words?.any { it.word == w } == true }

    // własny słownik: słowo wpisane litera po literze od razu da się pisać przeciągnięciem
    @Synchronized fun addUserWord(word: String) {
        val w = word.trim().lowercase(); val ws = words ?: return
        if (w.length < 2 || isKnown(w)) return
        val keys = w.map(::baseKey).joinToString("")
        if (!keys.all { it in 'a'..'z' }) return
        val e = Entry(w, collapse(keys), 3000)
        ws.add(e)
        synchronized(ownFold) { ownFold[fold(w)] = w }
        byEnds.getOrPut("" + e.keys[0] + e.keys.last()) { ArrayList() }.add(e)
    }

    @Synchronized fun removeUserWord(word: String) {
        val w = word.lowercase(); val ws = words ?: return
        ws.removeAll { it.word == w && it.rank == 3000 }
        synchronized(ownFold) { val f = fold(w); if (ownFold[f] == w) ownFold.remove(f) }
        byEnds.values.forEach { l -> l.removeAll { it.word == w && it.rank == 3000 } }
    }

    fun ownWords(): List<String> = synchronized(ownFold) { ownFold.values.sorted() }

    private var fold: HashMap<String, Entry>? = null
    private var main: HashSet<String>? = null
    private var foldAll: HashMap<String, MutableList<Entry>>? = null
    private var sortedFold: Array<String>? = null; private var sortedEntry: Array<Entry>? = null
    private val ownFold = HashMap<String, String>()
    // nauka wyborów: ile razy użytkownik napisał / wybrał słowo - częściej używane wyżej
    var useCount: ((String) -> Int)? = null
    private fun score(e: Entry): Double { val n = runCatching { useCount?.invoke(e.word) ?: 0 }.getOrDefault(0); return e.rank / (1.0 + 3.0 * min(n, 50)) }
    private fun fold(w: String) = w.lowercase().map(::baseKey).joinToString("")
    // sąsiednie klawisze (QWERTY) - typowa pomyłka palca
    private val rows = listOf("qwertyuiop", "asdfghjkl", "zxcvbnm")
    private fun near(c: Char): List<Char> = rows.flatMap { r -> val i = r.indexOf(c); if (i < 0) emptyList() else listOfNotNull(r.getOrNull(i - 1), r.getOrNull(i + 1)) }
    private fun cap(typed: String, r: String) = if (typed[0].isUpperCase()) r[0].uppercaseChar() + r.substring(1) else r

    private fun index(): Boolean {
        val ws = words ?: return false
        if (fold != null && main != null) return true
        synchronized(this) {
            if (fold != null && main != null) return true
            val base = ws.filter { it.rank != 3000 }
            val m = HashSet<String>(base.size * 2); base.forEach { m.add(it.word) }
            val f = HashMap<String, Entry>(base.size * 2)
            val all = HashMap<String, MutableList<Entry>>(base.size * 2)
            for (e in base) { val k = fold(e.word); val b = f[k]; if (b == null || e.rank < b.rank) f[k] = e; all.getOrPut(k) { ArrayList(1) }.add(e) }
            val sorted = base.map { fold(it.word) to it }.sortedBy { it.first }
            foldAll = all; sortedFold = Array(sorted.size) { sorted[it].first }; sortedEntry = Array(sorted.size) { sorted[it].second }
            main = m; fold = f
        }
        return true
    }

    // forma rozkazująca znanego czasownika (słownik jej nie ma): naprawiaj(cie) <- naprawiać, kupuj(cie) <- kupować
    private fun imperative(w: String, m: Set<String>): Boolean {
        val b = w.removeSuffix("cie")
        return b.length >= 4 && (b.endsWith("aj") && b.dropLast(1) + "ć" in m || b.endsWith("uj") && b.dropLast(2) + "ować" in m)
    }

    @Volatile private var byLen: HashMap<Int, ArrayList<Pair<String, Entry>>>? = null
    // odległość (zamiana, brak, nadmiar, przestawienie liter); od "max" wzwyż - wynik = max
    private fun dist(a: String, b: String, max: Int): Int {
        if (Math.abs(a.length - b.length) >= max) return max
        var p2 = IntArray(b.length + 1); var p = IntArray(b.length + 1) { it }; var c = IntArray(b.length + 1)
        for (i in 1..a.length) {
            c[0] = i; var low = i
            for (j in 1..b.length) {
                var v = minOf(p[j] + 1, c[j - 1] + 1, p[j - 1] + if (a[i - 1] == b[j - 1]) 0 else 1)
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = minOf(v, p2[j - 2] + 1)
                c[j] = v; if (v < low) low = v
            }
            if (low >= max) return max
            val t = p2; p2 = p; p = c; c = t
        }
        return minOf(p[b.length], max)
    }

    private fun variants(fw: String, cand: (String) -> Unit) {
        val abc = "abcdefghijklmnopqrstuvwxyz"
        for (i in fw.indices) {
            cand(fw.removeRange(i, i + 1))
            if (i + 1 < fw.length) cand(fw.substring(0, i) + fw[i + 1] + fw[i] + fw.substring(i + 2))
            for (c in abc) { if (c != fw[i]) cand(fw.substring(0, i) + c + fw.substring(i + 1)); cand(fw.substring(0, i) + c + fw.substring(i)) }
        }
        for (c in abc) cand(fw + c)
    }

    // autokorekta: słowo spoza słownika -> najczęstsze znane słowo różniące się polskimi znakami albo jedną literą; null = zostaw
    fun correct(typed: String, keep: Collection<String>? = null, anyRank: Boolean = false): String? {
        if (words == null || typed.length < 2 || !typed.all { it.isLetter() }) return null
        if (!index()) return null
        val w = typed.lowercase()
        if (keep != null && w in keep) return null
        val f = fold!!; val m = main!!
        synchronized(myFix) { myFix[w] }?.let { return cap(typed, it) }
        if (w == "ze") return null
        if (w in polite) return if (typed[0] == typed[0].uppercaseChar()) null else polite(w)
        phraseFix[fold(w)]?.let { if (it != w) return cap(typed, it) }
        if (w.length >= 5 && w.startsWith("nie")) {
            val rest = w.substring(3)
            val restOk = foldAll?.get(fold(rest))?.filter { it.rank < 4000 }?.minByOrNull { if (it.word == rest) -1.0 else score(it) }?.word
            if (restOk != null && (isVerbish(restOk) || foldAll?.containsKey(fold(w)) != true)) return (if (typed[0].isUpperCase()) "Nie " else "nie ") + restOk
        }
        if (w in m || imperative(w, m)) return null
        synchronized(ownFold) { if (ownFold.containsValue(w)) return null }
        val fw = w.map(::baseKey).joinToString("")
        var best: Entry? = foldAll?.get(fw)?.minByOrNull(::score) ?: f[fw]
        val ownSame = synchronized(ownFold) { ownFold[fw] }
        if (best == null && ownSame != null && ownSame != w) best = Entry(ownSame, "", 3000)
        else if (best == null && w.length >= 4) {
            variants(fw) { v -> val e = f[v]; if (e != null && (anyRank || e.rank < 15000) && (best == null || score(e) < score(best!!))) best = e }
        }
        if (best == null && w.length >= 6 && !anyRank) {
            // dłuższe słowo: najbliższe znane słowo (także rzadsze): jedna pomyłka (windos -> windows) albo dwie, gdy zgadza się początek
            // (literokem -> literówek; dobre słowo spoza słownika, np. kompiluje, zostaje)
            val idx = byLen ?: HashMap<Int, ArrayList<Pair<String, Entry>>>().also { m -> f.forEach { (k, e) -> m.getOrPut(k.length) { ArrayList() }.add(k to e) }; byLen = m }
            var bd = 3; var bl = 0
            for (len in fw.length - 1..fw.length + 1) for ((k, e) in idx[len] ?: continue) {
                if (k[0] != fw[0] || e.rank >= 120000) continue
                val d = dist(fw, k, minOf(bd + 1, 3))
                if (d == 2 && fw.commonPrefixWith(k).length * 2 < fw.length) continue
                // remis: ta sama długość (zamiana litery) przed brakiem / nadmiarem, potem częstsze
                if (d < 3 && (d < bd || (d == bd && (k.length == fw.length) > (bl == fw.length) || d == bd && (k.length == fw.length) == (bl == fw.length) && score(e) < score(best!!)))) { bd = d; best = e; bl = k.length }
            }
        }
        if (best == null && w.length >= 4 && !anyRank) {
            // dwie pomyłki palca: litera sąsiada na klawiaturze + jedna inna (eicej -> więcej) - tylko bardzo częste słowa
            val abc = "abcdefghijklmnopqrstuvwxyz"
            fun cand(v: String) { val e = f[v]; if (e != null && e.rank < (if (w.length >= 6) 15000 else 3000) && (best == null || score(e) < score(best!!))) best = e }
            for (i in fw.indices) for (nb in near(fw[i])) {
                val x = fw.substring(0, i) + nb + fw.substring(i + 1)
                for (j in 0..x.length) {
                    if (j < x.length) { cand(x.removeRange(j, j + 1)); if (j != i) for (c in abc) if (c != x[j]) cand(x.substring(0, j) + c + x.substring(j + 1)) }
                    for (c in abc) cand(x.substring(0, j) + c + x.substring(j))
                }
            }
        }
        val b = best ?: return null
        if (b.word == w) return null
        val r = polite(b.word)
        if (typed.length > 1 && typed.all { it.isUpperCase() }) return r.uppercase()
        return cap(typed, r)
    }

    // spacja: poprawka, a gdy jej nie ma - dokończenie widoczne w pasku propozycji (klawi -> klawiatura)
    fun complete(typed: String, keep: Collection<String>? = null): String? {
        val fix = correct(typed, keep)
        if (fix != null && (fix.contains(' ') || fold(fix) == fold(typed))) return fix
        val m = main ?: return fix
        if (typed.length < 3 || !typed.all { it.isLetter() }) return fix
        val w = typed.lowercase()
        if (keep != null && w in keep) return fix
        if (imperative(w, m)) return fix   // naprawiaj - dobre słowo, bez dokańczania do „naprawiają”
        synchronized(ownFold) { if (ownFold.containsValue(w)) return fix }
        val p = predict(typed, false, 3).firstOrNull { it.length > typed.length && fold(it).startsWith(fold(w)) } ?: return fix
        val all = foldAll ?: return fix
        val ownP = synchronized(ownFold) { ownFold.containsValue(p.lowercase()) }
        val pl = all[fold(p)] ?: return if (ownP) p else fix
        val pe = pl.minByOrNull(::score)!!; if (pe.rank >= 60000 && !ownP) return fix
        // brakująca litera częstego słowa wygrywa z dokończeniem rzadszego: zarz -> zaraz (nie zarzuty); komp -> komputer zostaje
        if (fix != null && fix.length == typed.length + 1) all[fold(fix)]?.minByOrNull(::score)?.let { if (score(it) <= score(pe)) return fix }
        if (w in m) {
            val we = all[fold(w)]?.firstOrNull { it.word == w }
            if (we == null || score(we) < 5000 || score(pe) * 4 > score(we)) return fix
        }
        return p
    }

    // wątpliwość: do 3 propozycji - polskie znaki albo jedna litera, także rzadkie słowa
    fun suggest(typed: String, keep: Collection<String>? = null, count: Int = 3): List<String> {
        val res = ArrayList<String>()
        if (words == null || typed.length < 3 || !typed.all { it.isLetter() }) return res
        val w = typed.lowercase()
        if (keep != null && w in keep) return res
        if (!index()) return res
        val f = fold!!; if (w in main!! || imperative(w, main!!)) return res
        val fw = w.map(::baseKey).joinToString("")
        val found = HashMap<String, Entry>()
        f[fw]?.let { found[it.word] = it }
        variants(fw) { v -> f[v]?.let { found[it.word] = it } }
        if (found.isEmpty() && fw.length >= 6) {
            // dużo błędów (interezuskdal): najbliższe słowa tylko jako propozycje - bez zamiany
            if (byLen == null) correct("xxxxxxx")
            val near = ArrayList<Pair<Int, Entry>>()
            for (len in fw.length - 2..fw.length + 2) for ((k, e) in byLen?.get(len) ?: continue) {
                if (k[0] != fw[0] || e.rank >= 120000) continue
                val d = dist(fw, k, 5); if (d < 5) near.add(d to e)
            }
            near.sortedWith(compareBy({ it.first }, { score(it.second) })).take(count).forEach { res.add(cap(typed, it.second.word)) }
            return res
        }
        found.values.sortedBy(::score).take(count).forEach { res.add(cap(typed, it.word)) }
        return res
    }

    // pasek propozycji: inne formy słowa (droga -> drogą) i dokończenia (klaw -> klawiatura)
    fun predict(typed: String, done: Boolean, count: Int = 3): List<String> {
        val res = ArrayList<String>()
        if (words == null || typed.isEmpty() || !typed.all { it.isLetter() }) return res
        if (!index()) return res
        val all = foldAll ?: return res; val sf = sortedFold ?: return res; val se = sortedEntry!!
        val w = typed.lowercase(); val fw = fold(w)
        fun add(x: String) { if (x != w && x !in res && res.size < count) res.add(x) }
        all[fw]?.sortedBy(::score)?.forEach { add(it.word) }
        synchronized(ownFold) { ownFold[fw] }?.let { add(it) }
        if (!done && fw.length >= 2 && res.size < count) {
            var lo = 0; var hi = sf.size
            while (lo < hi) { val mid = (lo + hi) / 2; if (sf[mid] < fw) lo = mid + 1 else hi = mid }
            val comp = ArrayList<Entry>()
            var i = lo
            while (i < sf.size && i < lo + 4000 && sf[i].startsWith(fw)) { if (sf[i].length > fw.length) comp.add(se[i]); i++ }
            comp.sortedBy(::score).forEach { add(it.word) }
            synchronized(ownFold) { ownFold.filter { it.key.startsWith(fw) && it.key.length > fw.length }.values.sorted() }.forEach { add(it) }
        }
        if (typed[0].isUpperCase()) for (i in res.indices) res[i] = res[i][0].uppercaseChar() + res[i].substring(1)
        return res
    }

    fun isTypo(w: String) = w.isNotEmpty() && (isMisspelling(w) || w.none { it in "ąćęłńóśźżĄĆĘŁŃÓŚŹŻ" } && correct(w, null, true) != null)
    fun isMisspelling(w: String): Boolean { val c = if (w.isEmpty()) null else correct(w); return c != null && c.contains(' ') }

    private val verbs = setOf("ma", "jest", "są", "może", "musi", "chce", "wie", "będzie", "było", "trzeba", "wolno", "umie", "zna", "lubi", "widzi", "mówi", "robi", "idzie", "je", "pije", "śpi", "daje", "da", "żyje", "działa", "pasuje", "wiadomo", "warto", "boli", "mam", "wiem", "umiem", "jem", "dam", "znam", "chcę", "mogę", "lubię", "rozumiem", "pamiętam")
    fun isVerbish(w0: String): Boolean {
        if (w0.isEmpty()) return false
        val w = w0.lowercase()
        return w in verbs || listOf("ć", "ł", "ła", "ło", "li", "ły", "my", "cie", "sz", "ę", "em", "am").any { w.endsWith(it) && w.length > it.length + 1 }
    }

    private val phraseFix = mapOf(
        "napewno" to "na pewno", "narazie" to "na razie", "wogole" to "w ogóle", "poprostu" to "po prostu", "naprzyklad" to "na przykład",
        "conajmniej" to "co najmniej", "conajwyzej" to "co najwyżej", "spowrotem" to "z powrotem", "zpowrotem" to "z powrotem", "wkoncu" to "w końcu",
        "napoczatku" to "na początku", "wszczegolnosci" to "w szczególności", "pozatym" to "poza tym", "wrazie" to "w razie",
        "nazewnatrz" to "na zewnątrz", "dowidzenia" to "do widzenia", "naszczescie" to "na szczęście", "przedewszystkim" to "przede wszystkim",
        "ztad" to "stąd", "zkad" to "skąd")
    private val polite = setOf("ty", "ci", "cię", "ciebie", "tobie", "tobą", "twój", "twoja", "twoją", "twoje", "twojego", "twojej", "twojemu", "twoim", "twoich", "twoimi")
    private fun polite(r: String) = if (r in polite) r[0].uppercaseChar() + r.substring(1) else r
    private val pairFix = mapOf(
        "na prawde" to "naprawdę", "na przeciwko" to "naprzeciwko", "na przeciw" to "naprzeciw", "z posrod" to "spośród", "z pod" to "spod",
        "z nad" to "znad", "po mimo" to "pomimo", "z reszta" to "zresztą", "na reszcie" to "nareszcie", "do okola" to "dookoła")
    fun joinPair(prev: String?, word: String?): String? {
        if (prev.isNullOrEmpty() || word.isNullOrEmpty()) return null
        val j = pairFix[fold(prev) + " " + fold(word)] ?: return null
        return cap(prev, j)
    }

    // "nie" + słowo pisane razem (nie dobry -> niedobry) - propozycja
    fun joinNie(prev: String?, word: String?): String? {
        val m = main ?: return null
        if (word.isNullOrEmpty() || !prev.equals("nie", true) || !word.all { it.isLetter() }) return null
        val j = "nie" + word.lowercase()
        return if (j in m && !isVerbish(word)) (if (prev!![0].isUpperCase()) "N" + j.substring(1) else j) else null
    }

    private val commaBefore = setOf("że", "żeby", "aby", "bo", "ale", "lecz", "gdyż", "ponieważ", "jeśli", "jeżeli", "chociaż", "choć", "czyli", "a", "więc", "zatem", "natomiast", "gdy", "kiedy", "gdzie", "dlaczego", "jakby",
        "który", "która", "które", "którego", "której", "któremu", "którym", "którą", "których", "którzy", "którymi")
    private val noCommaAfter = setOf("mimo", "pomimo", "chyba", "zwłaszcza", "tylko", "dlatego", "tak", "podczas", "zanim", "nim", "przy", "po", "w", "z", "na", "do", "o", "od", "za", "przez", "dla", "u", "tam", "wtedy", "zawsze", "nawet", "właśnie", "jak", "jakby", "kiedy", "gdy", "gdzie", "dlaczego", "więc", "zatem", "natomiast",
        "i", "a", "oraz", "lub", "albo", "czy", "ani", "ni", "bądź", "że", "bo", "ale", "aby", "żeby", "lecz", "to", "co", "niż")
    private val zeHeads = setOf("myślę", "myśli", "myślisz", "myślał", "myślała", "myśleliśmy", "wiem", "wiesz", "wie", "wiemy", "wiedział", "wiedziała",
        "mówię", "mówi", "mówisz", "mówił", "mówiła", "mówią", "powiem", "powiedział", "powiedziała", "powiedziałem", "powiedziałam", "sądzę", "sądzi", "uważam", "uważa", "uważasz",
        "czuję", "widzę", "widać", "widzisz", "słyszę", "słyszałem", "słyszałam", "nadzieję", "wydaje", "wydawało", "okazało", "okazuje", "pamiętam", "pamiętaj", "zapomniałem", "zapomniałam",
        "cieszę", "obiecuję", "obiecałem", "twierdzi", "twierdzę", "przyznaję", "wierzę", "wątpię", "rozumiem", "napisał", "napisała", "pisze", "dowiedziałem", "dowiedziałam",
        "prawda", "pewnie", "jasne", "ważne", "możliwe", "szkoda", "dobrze", "fakt", "znaczy", "chodzi", "tak", "chyba", "mimo", "pomimo", "to", "dlatego", "zwłaszcza", "tylko", "się", "sam", "sama")
    fun zeNow(ze: String, prev: String?): String? {
        if (!ze.equals("ze", true) || prev.isNullOrEmpty() || !prev.all { it.isLetter() }) return null
        if (prev.lowercase() !in zeHeads && !isVerbish(prev)) return null
        if (pairCount(prev.lowercase() + " ze") > pairCount(prev.lowercase() + " że")) return null
        return if (ze[0].isUpperCase()) "Że" else "że"
    }

    // "ze" zamiast "że" rozpoznane po następnym słowie; przyimek zostaje (ze mną, ze szkoły)
    fun zeAsZe(ze: String, next: String?, before: String?): String? {
        if (!ze.equals("ze", true) || next.isNullOrEmpty() || !next.all { it.isLetter() }) return null
        val n = next.lowercase(); val v = "aąeęioóuy"
        val ze2 = pairCount("ze $n"); val zz = pairCount("że $n")
        val prep = if (ze2 != zz) ze2 > zz else n.startsWith("mn") || n == "sobą" || n == "sobie" ||
            n.startsWith("wszystk") && n != "wszystko" && n != "wszystkie" ||
            n.length > 1 && n[0] in "szśźżwfł" && n[1] !in v && !n.startsWith("wszys") && (n[0] != 'w' || n[1] in "szłrnśżź") &&
            listOf("ł", "łem", "łam", "łeś", "łaś", "ć").none { n.endsWith(it) }
        if (prep) return null
        val r = if (ze[0].isUpperCase()) "Że" else "że"
        return if (needsComma("że", before)) ", $r" else r
    }

    fun needsComma(word: String?, prev: String?): Boolean {
        if (word.isNullOrEmpty() || prev.isNullOrEmpty() || !prev.all { it.isLetter() }) return false
        return word.lowercase() in commaBefore && prev.lowercase() !in noCommaAfter
    }

    private fun collapse(keys: String): String { val sb = StringBuilder(); for (c in keys) if (sb.isEmpty() || sb.last() != c) sb.append(c); return sb.toString() }
    private fun dist(a: P, b: P): Double { val dx = a.x - b.x; val dy = a.y - b.y; return sqrt(dx * dx + dy * dy) }

    fun resample(pts: List<P>, n: Int): Array<P> {
        if (pts.isEmpty()) return Array(n) { P(0.0, 0.0) }
        var total = 0.0; for (i in 1 until pts.size) total += dist(pts[i - 1], pts[i])
        if (total < 1e-6) return Array(n) { pts[0] }
        val res = arrayOfNulls<P>(n)
        val step = total / (n - 1); var acc = 0.0; var seg = 1; var prev = pts[0]; res[0] = pts[0]; var k = 1
        while (k < n - 1 && seg < pts.size) {
            val d = dist(prev, pts[seg])
            if (acc + d >= step && d > 0) { val t = (step - acc) / d; val q = P(prev.x + t * (pts[seg].x - prev.x), prev.y + t * (pts[seg].y - prev.y)); res[k++] = q; prev = q; acc = 0.0 }
            else { acc += d; prev = pts[seg]; seg++ }
        }
        while (k < n) res[k++] = pts.last()
        @Suppress("UNCHECKED_CAST") return res as Array<P>
    }

    @Volatile var lastUncertain = false

    // najlepsze słowa dla śladu; keys - środki klawiszy liter a-z, keyW - odstęp klawiszy; learned - ile razy wybrane
    fun recognize(trace0: List<P>, keys: Map<Char, P>, keyW: Double, learned: Map<String, Int>? = null, count: Int = 3, prev: String? = null): List<String> {
        val result = ArrayList<String>()
        if (words == null || trace0.size < 2 || keys.isEmpty()) return result
        val N = 32
        val trace = steady(trace0, keyW)
        val g = resample(trace, N)
        fun near(p: P): List<Char> { val l = keys.filter { dist(it.value, p) <= keyW * 0.95 }.keys.toMutableList(); if (l.isEmpty()) l.add(keys.minByOrNull { dist(it.value, p) }!!.key); return l }
        val starts = near(trace[0]); val ends = near(trace.last())
        var gLen = 0.0; for (i in 1 until trace.size) gLen += dist(trace[i - 1], trace[i])
        val scored = ArrayList<Pair<String, Double>>()
        val be = synchronized(this) { byEnds }
        for (s in starts) for (e in ends) {
            val list = synchronized(this) { be["" + s + e]?.toList() } ?: continue
            for (w in list) {
                if (w.keys.length == 1 && gLen > keyW) continue
                val path = w.keys.map { keys[it] }
                if (path.any { it == null }) continue
                val pp = path.map { it!! }
                var d = Double.MAX_VALUE
                synchronized(mine) { mine[w.word]?.forEach { m -> d = min(d, distance(g, m, keyW) * 0.8) } }
                var tLen = 0.0; for (i in 1 until pp.size) tLen += dist(pp[i - 1], pp[i])
                var ok = !(tLen > gLen * 1.8 + keyW || gLen > tLen * 1.8 + keyW)
                if (ok) for (p in pp) if (g.minOf { dist(p, it) } > keyW * 1.1) { ok = false; break }
                if (ok) d = min(d, distance(g, resample(pp, N), keyW))
                if (d == Double.MAX_VALUE) continue
                val used = learned?.get(w.word) ?: 0
                val pair = if (prev != null) synchronized(pairs) { pairs[prev + " " + w.word] ?: 0 } else 0
                scored.add(w.word to d + 0.06 * ln(w.rank + 2.0) - min(0.5, 0.15 * used) - min(0.4, 0.2 * pair))
            }
        }
        val best = scored.minOfOrNull { it.second } ?: 0.0
        val order = scored.map { it.second }.sorted()
        lastUncertain = order.size > 1 && order[1] - order[0] < 0.08
        for (x in scored.sortedBy { it.second }) { if (x.second > best + 0.25) break; if (x.first !in result) result.add(x.first); if (result.size >= count) break }
        return result
    }

    private val pairs = HashMap<String, Int>()
    fun learnPair(prev: String?, word: String?) {
        if (prev.isNullOrEmpty() || word.isNullOrEmpty()) return
        val k = prev.lowercase() + " " + word.lowercase()
        synchronized(pairs) { pairs[k] = (pairs[k] ?: 0) + 1; if (pairs.size > 20000) pairs.remove(pairs.minByOrNull { it.value }!!.key) }
    }
    fun learnTriple(prev2: String?, prev: String?, word: String?) {
        if (prev2.isNullOrEmpty() || prev.isNullOrEmpty() || word.isNullOrEmpty()) return
        learnPair(prev2.lowercase() + " " + prev.lowercase(), word)
    }

    // Twoje poprawki: słowo skasowane i wpisane od nowa (kmoputer -> komputer) - następnym razem poprawiane samo
    private val myFix = LinkedHashMap<String, String>()
    fun learnFix(typo: String, fix: String): Boolean {
        if (typo.length < 3 || fix.length < 2 || !typo.all { it.isLetter() } || !fix.all { it.isLetter() }) return false
        val t = typo.lowercase(); val f = fix.lowercase(); val m = main
        if (t == f || m == null || t in m || edits(t, f) > max(1, min(2, t.length / 3))) return false
        synchronized(myFix) { myFix[t] = f; if (myFix.size > 2000) myFix.remove(myFix.keys.first()) }
        return true
    }
    private fun edits(a: String, b: String): Int {
        val d = Array(a.length + 1) { IntArray(b.length + 1) }
        for (i in 0..a.length) d[i][0] = i
        for (j in 0..b.length) d[0][j] = j
        for (i in 1..a.length) for (j in 1..b.length) {
            d[i][j] = minOf(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + if (a[i - 1] == b[j - 1]) 0 else 1)
            if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i][j] = min(d[i][j], d[i - 2][j - 2] + 1)
        }
        return d[a.length][b.length]
    }

    // całe zdania: często powtarzane (co najmniej 2 razy) podsuwane nad propozycjami
    private val sentences = HashMap<String, Int>()
    fun learnSentence(text0: String?) {
        val text = text0?.trim() ?: return
        if (text.length < 8 || text.length > 200 || text.count { it == ' ' } < 2) return
        synchronized(sentences) { sentences[text] = (sentences[text] ?: 0) + 1; if (sentences.size > 500) sentences.remove(sentences.minByOrNull { it.value }!!.key) }
    }
    fun sentenceRest(prefix0: String?): String? {
        val prefix = prefix0?.trimStart() ?: return null
        if (prefix.length < 4) return null
        return synchronized(sentences) {
            sentences.filter { it.value >= 2 && it.key.length > prefix.length + 2 && it.key.startsWith(prefix, true) }.maxByOrNull { it.value }?.key?.substring(prefix.length)
        }
    }

    private fun pairCount(k: String) = synchronized(pairs) { pairs[k] ?: 0 }
    private fun following(ctx: String): List<Map.Entry<String, Int>> {
        val k = "$ctx "
        return synchronized(pairs) { pairs.entries.filter { it.key.startsWith(k) && it.key.indexOf(' ', k.length) < 0 && it.value >= 2 }.sortedByDescending { it.value }.map { java.util.AbstractMap.SimpleEntry(it.key, it.value) } }
    }

    // następne słowo: najpierw po dwóch poprzednich (zwrot), potem po jednym; plus cały zwrot, gdy pisany często
    fun nextWords(prev: String?, prev2: String? = null, count: Int = 3): List<String> {
        val res = ArrayList<String>()
        if (prev.isNullOrEmpty()) return res
        val p = prev.lowercase(); val p2 = prev2?.lowercase()
        val cands = ArrayList<Pair<String, Int>>()
        if (!p2.isNullOrEmpty()) following("$p2 $p").forEach { cands.add(it.key to it.value + 1000) }
        following(p).forEach { cands.add(it.key to it.value) }
        for ((k, _) in cands) { val w = k.substring(k.lastIndexOf(' ') + 1); if (w !in res) res.add(w) }
        if (res.isNotEmpty()) {
            val phrase = arrayListOf(res[0]); var a = p; var b = res[0]
            for (i in 0 until 4) {
                val nx = following("$a $b").firstOrNull() ?: break
                if (nx.value < 3) break
                val w = nx.key.substring(nx.key.lastIndexOf(' ') + 1); phrase.add(w); a = b; b = w
            }
            if (phrase.size >= 2) res.add(0, phrase.joinToString(" "))
        }
        return res.take(count)
    }

    // dopasowanie elastyczne w czasie (DTW)
    private fun distance(a: Array<P>, b: Array<P>, keyW: Double): Double {
        val n = a.size; val m = b.size; val band = 6
        val d = Array(n + 1) { DoubleArray(m + 1) { Double.MAX_VALUE } }
        d[0][0] = 0.0
        for (i in 1..n) for (j in max(1, i - band)..min(m, i + band))
            d[i][j] = dist(a[i - 1], b[j - 1]) + minOf(d[i - 1][j - 1], d[i - 1][j], d[i][j - 1])
        return d[n][m] / (n * keyW)
    }

    // nauka ruchu ręki: przyjęte słowo zapamiętuje Twój ślad (do 3 na słowo); poprawione - zapomina zły
    private val mine = HashMap<String, MutableList<Array<P>>>()
    fun learnShape(word: String?, trace: List<P>?, keyW: Double) {
        if (word.isNullOrEmpty() || trace == null || trace.size < 2) return
        synchronized(mine) { val l = mine.getOrPut(word.lowercase()) { ArrayList() }; l.add(resample(steady(trace, keyW), 32)); if (l.size > 3) l.removeAt(0) }
    }
    fun forgetShape(word: String?) {
        val w = (word ?: "").lowercase()
        synchronized(mine) { val l = mine[w]; if (l != null && l.isNotEmpty()) { l.removeAt(l.size - 1); if (l.isEmpty()) mine.remove(w) } }
    }

    // zapis nauki (ten sam format co na Windows), szyfrowany kluczem z Android Keystore
    fun saveText(learned: Map<String, Int>, own: Set<String>): String {
        val lines = ArrayList<String>()
        learned.filter { it.value > 0 }.entries.sortedByDescending { it.value }.take(5000).forEach { lines.add(it.key + "\t" + it.value) }
        own.forEach { lines.add("+$it") }
        synchronized(mine) { mine.forEach { (w, l) -> l.forEach { sh -> lines.add("~" + w + "\t" + sh.joinToString(" ") { String.format(java.util.Locale.ROOT, "%.1f,%.1f", it.x, it.y) }) } } }
        synchronized(pairs) { pairs.forEach { lines.add("=" + it.key + "\t" + it.value) } }
        synchronized(myFix) { myFix.forEach { lines.add(">" + it.key + "\t" + it.value) } }
        synchronized(sentences) { sentences.forEach { lines.add("*" + it.value + "\t" + it.key) } }
        return lines.joinToString("\n")
    }

    fun loadText(text: String, learned: MutableMap<String, Int>, own: MutableSet<String>) {
        for (line in text.split('\n')) {
            when {
                line.startsWith("+") -> { if (line.length > 2) own.add(line.substring(1)) }
                line.startsWith("~") -> {
                    val t = line.substring(1).split('\t'); if (t.size != 2) continue
                    val pts = t[1].split(' ').map { it.split(',') }.filter { it.size == 2 }.mapNotNull { q -> val x = q[0].toDoubleOrNull(); val y = q[1].toDoubleOrNull(); if (x != null && y != null) P(x, y) else null }
                    if (pts.size != 32) continue
                    synchronized(mine) { val l = mine.getOrPut(t[0]) { ArrayList() }; if (l.size < 3) l.add(pts.toTypedArray()) }
                }
                line.startsWith("*") -> { val q = line.substring(1).split('\t'); val sc = q.getOrNull(0)?.toIntOrNull(); if (q.size == 2 && sc != null && q[1].isNotEmpty()) synchronized(sentences) { sentences[q[1]] = sc } }
                line.startsWith(">") -> { val q = line.substring(1).split('\t'); if (q.size == 2 && q[0].isNotEmpty() && q[1].isNotEmpty()) synchronized(myFix) { myFix[q[0]] = q[1] } }
                line.startsWith("=") -> { val q = line.substring(1).split('\t'); val pc = q.getOrNull(1)?.toIntOrNull(); if (q.size == 2 && pc != null) synchronized(pairs) { pairs[q[0]] = pc } }
                else -> { val p = line.split('\t'); val n = p.getOrNull(1)?.toIntOrNull(); if (p.size == 2 && n != null && p[0].isNotEmpty()) learned[p[0]] = n }
            }
        }
    }

    // nowy język: nauka zostaje (słowa, pary), słownik główny od nowa
    fun clearLearned() { synchronized(mine) { mine.clear() }; synchronized(pairs) { pairs.clear() }; synchronized(myFix) { myFix.clear() }; synchronized(sentences) { sentences.clear() } }
}
