package pl.velivo.browser

import android.content.Context
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

// Lekki filtr jak w Velivo na Windows: tylko reguły domenowe "||domena^" z list EasyList, EasyPrivacy i KAD.
// Sprawdzanie: host i wszystkie domeny nadrzędne w HashSet.
class AdBlocker(private val blocked: Set<String>, private val allowed: Set<String>) {

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

        private fun dir(ctx: Context) = File(ctx.filesDir, "filtry").apply { mkdirs() }

        fun build(ctx: Context): AdBlocker {
            val blocked = HashSet<String>()
            val allowed = HashSet<String>()
            ctx.assets.open("blokowane.txt").bufferedReader().forEachLine { l ->
                val t = l.trim()
                if (t.isNotEmpty() && !t.startsWith("#")) blocked.add(t)
            }
            for ((name, _) in sources) {
                val f = File(dir(ctx), name)
                if (f.exists()) f.forEachLine { parse(it, blocked, allowed) }
            }
            return AdBlocker(blocked, allowed)
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
