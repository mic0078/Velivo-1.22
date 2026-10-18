package pl.velivo.browser

import android.content.Context
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

// Lekki filtr jak w Velivo na Windows: reguły domenowe "||domena^" z list EasyList, EasyPrivacy i KAD
// (host i domeny nadrzędne w HashSet) oraz reguły ukrywania "##selektor", żeby po reklamie nie zostawało puste miejsce.
class AdBlocker(
    private val blocked: Set<String>, private val allowed: Set<String>,
    private val generic: List<String> = emptyList(), private val perSite: Map<String, List<String>> = emptyMap(),
) {
    // Ogólne reguły są wspólne dla wszystkich stron - CSS składany raz.
    private val genericCss by lazy { toCss(generic) }

    // Arkusz ukrywania dla strony: ogólne + reguły tej domeny i domen nadrzędnych.
    fun hidingCss(host: String?): String {
        val own = ArrayList<String>()
        var h = host?.lowercase()?.removePrefix("www.") ?: return genericCss
        while (true) {
            perSite[h]?.let { own.addAll(it) }
            val dot = h.indexOf('.')
            if (dot < 0) break
            h = h.substring(dot + 1)
        }
        return genericCss + toCss(own)
    }

    fun matches(host: String?): Boolean {
        var h = host?.lowercase() ?: return false
        while (true) {
            if (h in allowed) return false
            if (h in blocked) return true
            val dot = h.indexOf('.')
            if (dot < 0) return false
            h = h.substring(dot + 1)
        }
    }

    companion object {
        private val sources = listOf(
            "easylist.txt" to "https://easylist.to/easylist/easylist.txt",
            "easyprivacy.txt" to "https://easylist.to/easylist/easyprivacy.txt",
            "polska.txt" to "https://raw.githubusercontent.com/MajkiIT/polish-ads-filter/master/polish-adblock-filters/adblock.txt",
        )
        private const val MAX_AGE = 4L * 24 * 3600 * 1000
        private val unsupported = listOf("domain=", "~", "redirect", "csp", "removeparam", "badfilter", "rewrite", "header=", "permissions", "urltransform", "replace=", "denyallow", "to=", "from=", "method=")

        // Jeden błędny selektor unieważnia całą regułę CSS - dzielimy na małe paczki.
        private fun toCss(sel: List<String>) = sel.chunked(20).joinToString("") { it.joinToString(",") + "{display:none!important}" }

        // Rozszerzenia składni uBlock/ABP, których CSS nie rozumie.
        private val notCss = listOf(":-abp", ":has-text", ":upward", ":xpath", ":style", ":remove", ":matches", ":min-text", ":watch", ":others", ":if", ":contains", "+js", "{")

        private fun parseHiding(line: String, generic: MutableSet<String>, exceptions: MutableSet<String>, perSite: MutableMap<String, MutableList<String>>) {
            val i = line.indexOf("##").takeIf { it >= 0 } ?: line.indexOf("#@#").takeIf { it >= 0 } ?: return
            val except = line.startsWith("#@#", i)
            val sel = line.substring(i + if (except) 3 else 2).trim()
            if (sel.isEmpty() || sel.startsWith("^") || notCss.any { sel.contains(it) }) return
            val domains = line.substring(0, i)
            if (domains.isEmpty()) { if (except) exceptions.add(sel) else generic.add(sel); return }
            if (except) return
            for (d in domains.split(',')) {
                val dd = d.trim().lowercase()
                if (dd.isEmpty() || dd.startsWith("~") || dd.contains('*')) continue
                perSite.getOrPut(dd) { ArrayList() }.add(sel)
            }
        }

        private fun dir(ctx: Context) = File(ctx.filesDir, "filtry").apply { mkdirs() }

        fun build(ctx: Context): AdBlocker {
            val blocked = HashSet<String>()
            val allowed = HashSet<String>()
            ctx.assets.open("blokowane.txt").bufferedReader().forEachLine { l ->
                val t = l.trim()
                if (t.isNotEmpty() && !t.startsWith("#")) blocked.add(t)
            }
            val generic = LinkedHashSet<String>()
            val exceptions = HashSet<String>()
            val perSite = HashMap<String, MutableList<String>>()
            for ((name, _) in sources) {
                // pobrana (świeższa) lista, a do pierwszego pobrania - lista dołączona do aplikacji
                val f = File(dir(ctx), name)
                val reader = if (f.exists()) f.bufferedReader() else runCatching { ctx.assets.open("filtry-$name").bufferedReader() }.getOrNull() ?: continue
                reader.useLines { lines -> lines.forEach {
                    val l = it.trim()
                    if (l.contains("##") || l.contains("#@#")) parseHiding(l, generic, exceptions, perSite) else parse(l, blocked, allowed)
                } }
            }
            return AdBlocker(blocked, allowed, (generic - exceptions).toList(), perSite)
        }

        private fun parse(raw: String, blocked: MutableSet<String>, allowed: MutableSet<String>) {
            var line = raw.trim()
            val allow = line.startsWith("@@")
            if (allow) line = line.substring(2)
            if (!line.startsWith("||")) return
            line = line.substring(2)
            val opt = line.indexOf('$')
            if (opt >= 0) {
                val options = line.substring(opt + 1).lowercase()
                if (unsupported.any { options.contains(it) }) return
                line = line.substring(0, opt)
            }
            line = line.trimEnd('^', '*')
            if (line.isEmpty() || line.indexOf('.') <= 0 || line.any { it == '/' || it == '*' || it == '^' || it == ':' }) return
            (if (allow) allowed else blocked).add(line.lowercase())
        }

        fun stale(ctx: Context) = sources.any { (name, _) ->
            val f = File(dir(ctx), name)
            !f.exists() || System.currentTimeMillis() - f.lastModified() > MAX_AGE
        }

        // Plik podmieniany dopiero po udanym pobraniu - nieudane zostawia starą listę.
        fun download(ctx: Context): Int {
            var ok = 0
            for ((name, url) in sources) {
                runCatching {
                    val c = URL(url).openConnection() as HttpURLConnection
                    c.connectTimeout = 20000
                    c.readTimeout = 60000
                    val text = c.inputStream.bufferedReader().use { it.readText() }
                    c.disconnect()
                    if (text.length < 1000 || !text.contains("||")) return@runCatching
                    val f = File(dir(ctx), name)
                    val tmp = File(dir(ctx), "$name.tmp")
                    tmp.writeText(text)
                    if (tmp.renameTo(f)) ok++
                }
            }
            return ok
        }
    }
}
