package pl.velivo.browser

import android.content.Context
import android.net.Uri
import android.webkit.WebResourceResponse
import android.webkit.WebView
import androidx.webkit.WebMessageCompat
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.io.File
import java.util.Locale

// Szybki Dostęp: ta sama strona co w Velivo na Windows (src/QuickAccessExtension/kod, dołączona do assets),
// podawana z lokalnego adresu https://appassets.androidplatform.net/szybki/ - nic nie idzie do internetu.
class QuickAccess(private val ctx: Context) {
    private val memory = HashMap<String, JSONObject>()   // obszar "session" - tylko do zamknięcia aplikacji

    private fun file(area: String) = File(ctx.filesDir, "szybki-$area.json")

    // Pliki z komputera (dane/kopia.json, dane/ikony, dane/miniatury) - strona wczytuje je jak na Windows i scala.
    private val fromPc = File(ctx.filesDir, "szybki-komputer")
    private val favicons = File(ctx.cacheDir, "ikony-stron")

    // Paczka ZIP Szybkiego Dostępu z Windows: rozpakowujemy tylko dane (folder "dane/"), nie kod strony.
    fun importFromComputer(zip: ByteArray) {
        val tmp = File(ctx.filesDir, "szybki-komputer.tmp").apply { deleteRecursively(); mkdirs() }
        java.util.zip.ZipInputStream(zip.inputStream()).use { z ->
            while (true) {
                val e = z.nextEntry ?: break
                val name = e.name.replace('\\', '/')
                if (e.isDirectory || !name.startsWith("dane/") || !safe(name)) continue
                val f = File(tmp, name)
                f.parentFile?.mkdirs()
                f.outputStream().use { z.copyTo(it) }
            }
        }
        fromPc.deleteRecursively()
        tmp.renameTo(fromPc)
    }

    private fun safe(path: String) = path.isNotEmpty() && !path.startsWith("/") && !path.contains(':') &&
        path.split('/').all { it.isNotEmpty() && it != "." && it != ".." }

    @Synchronized
    private fun load(area: String): JSONObject = memory.getOrPut(area) {
        if (area == "session") JSONObject()
        else runCatching { JSONObject(file(area).readText()) }.getOrDefault(JSONObject())
    }

    @Synchronized
    private fun save(area: String) {
        if (area == "session") return
        val f = file(area)
        val tmp = File(f.path + ".tmp")
        tmp.writeText(load(area).toString())
        tmp.renameTo(f)
    }

    // Kanał do strony - tylko dla adresu Szybkiego Dostępu, zwykłe strony go nie widzą.
    fun attach(w: WebView) {
        if (!WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)) return
        WebViewCompat.addWebMessageListener(w, "VelivoSD", setOf(ORIGIN)) { _, msg, _, _, reply ->
            val m = runCatching { JSONObject(msg.data ?: "") }.getOrNull() ?: return@addWebMessageListener
            val result = runCatching { handle(m) }.getOrNull()
            reply.postMessage(JSONObject().put("id", m.optInt("id")).put("wynik", result ?: JSONObject.NULL).toString())
        }
    }

    @Synchronized
    private fun handle(m: JSONObject): Any? {
        if (m.optString("op") == "most") return most(m.optJSONObject("dane") ?: return null)
        val area = m.optString("obszar").takeIf { it in setOf("local", "sync", "session") } ?: return null
        val store = load(area)
        return when (m.optString("op")) {
            "get" -> {
                val keys = m.optJSONArray("dane")
                val out = JSONObject()
                if (keys == null) store.keys().forEach { out.put(it, store.get(it)) }
                else for (i in 0 until keys.length()) keys.optString(i).let { if (store.has(it)) out.put(it, store.get(it)) }
                out
            }
            "set" -> { val o = m.optJSONObject("dane") ?: return null; o.keys().forEach { store.put(it, o.get(it)) }; save(area); true }
            "remove" -> { val a = m.optJSONArray("dane") ?: JSONArray(); for (i in 0 until a.length()) store.remove(a.optString(i)); save(area); true }
            "clear" -> { memory[area] = JSONObject(); save(area); true }
            "size" -> store.toString().length
            else -> null
        }
    }

    // Most do plików strony (jak w Velivo na Windows): tylko folder "dane/" w danych Szybkiego Dostępu z komputera.
    private fun most(z: JSONObject): JSONObject {
        val c = z.optString("c")
        if (c == "ping") return JSONObject().put("ok", true).put("baza", "Velivo Android").put("velivo", true)
        val path = z.optString("p").replace('\\', '/')
        if (!(path == "dane" || path.startsWith("dane/")) || !safe(path)) return JSONObject().put("ok", false)
        val f = File(fromPc, path)
        return runCatching {
            when (c) {
                "read" -> if (f.isFile) JSONObject().put("ok", true).put("b64", android.util.Base64.encodeToString(f.readBytes(), android.util.Base64.NO_WRAP)) else JSONObject().put("ok", false)
                "write" -> {
                    val bytes = android.util.Base64.decode(z.optString("b64"), android.util.Base64.DEFAULT)
                    if (bytes.size > 12 * 1024 * 1024) JSONObject().put("ok", false)
                    else { f.parentFile?.mkdirs(); f.writeBytes(bytes); JSONObject().put("ok", true) }
                }
                "list" -> JSONObject().put("ok", true).put("pliki", JSONArray((f.listFiles()?.filter { it.isFile }?.map { it.name }) ?: emptyList<String>()))
                "del" -> { f.delete(); JSONObject().put("ok", true) }
                else -> JSONObject().put("ok", false)
            }
        }.getOrDefault(JSONObject().put("ok", false))
    }

    // Pliki strony z assets; newtab.html z dołączoną nakładką, język wg telefonu.
    fun serve(u: Uri): WebResourceResponse {
        val name = u.path.orEmpty().removePrefix("/szybki/")
        if (name == "_favicon/" || name == "_favicon") return favicon(u.getQueryParameter("pageUrl"))
        if (name.startsWith("dane/")) {
            if (!safe(name)) return notFound()
            val f = File(fromPc, name)
            return if (f.isFile) WebResourceResponse(mimeOf(name), "utf-8", f.inputStream()) else notFound()
        }
        if (name.isEmpty() || name.contains("..") || name.contains('/')) return notFound()
        val bytes: ByteArray = when (name) {
            "jezyk-wybor.js" -> "var VELIVO_LANG = '${if (Locale.getDefault().language == "pl") "pl" else "en"}';".toByteArray()
            else -> runCatching { ctx.assets.open(name).use { it.readBytes() } }.getOrNull() ?: return notFound()
        }
        val out = when (name) {
            "newtab.html" -> String(bytes)
                .replaceFirst("<head>", "<head><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><script src=\"velivo-chrome.js\"></script>")
                .replaceFirst("</head>", "<link rel=\"stylesheet\" href=\"velivo-telefon.css\"></head>")   // wygląd na telefon
                .toByteArray()
            // tłumaczenie strony działa też poza chrome-extension://
            "jezyk.js" -> String(bytes).replace("location.protocol !== 'chrome-extension:'", "false").toByteArray()
            else -> bytes
        }
        return WebResourceResponse(mimeOf(name), "utf-8", ByteArrayInputStream(out))
    }

    private fun mimeOf(name: String) = when (name.substringAfterLast('.').lowercase()) {
        "html" -> "text/html"; "js" -> "application/javascript"; "css" -> "text/css"; "json" -> "application/json"
        "png" -> "image/png"; "jpg", "jpeg" -> "image/jpeg"; "webp" -> "image/webp"; "ico" -> "image/x-icon"; "svg" -> "image/svg+xml"
        else -> "application/octet-stream"
    }

    // Ikona strony dla kafelka: favicon.ico prosto z tej strony (bez Google i innych pośredników),
    // zapamiętana na telefonie. Brak ikony = 404, strona pokazuje literę.
    private fun favicon(pageUrl: String?): WebResourceResponse {
        val host = runCatching { Uri.parse(pageUrl).host }.getOrNull()?.lowercase() ?: return notFound()
        if (!host.matches(Regex("[a-z0-9.-]{1,253}"))) return notFound()
        favicons.mkdirs()
        val f = File(favicons, host)
        val miss = File(favicons, "$host.brak")
        if (!f.isFile && !(miss.isFile && System.currentTimeMillis() - miss.lastModified() < 7L * 24 * 3600 * 1000)) {
            val data = listOf("https://$host/favicon.ico", "https://$host/apple-touch-icon.png").firstNotNullOfOrNull { download(it) }
            if (data != null) f.writeBytes(data) else miss.writeText("")
        }
        return if (f.isFile) WebResourceResponse(sniff(f), null, f.inputStream()) else notFound()
    }

    private fun download(url: String): ByteArray? = runCatching {
        val c = java.net.URL(url).openConnection() as java.net.HttpURLConnection
        c.connectTimeout = 5000
        c.readTimeout = 5000
        c.instanceFollowRedirects = true
        if (c.responseCode != 200 || (c.contentType ?: "").startsWith("text/")) return null
        c.inputStream.use { s -> s.readBytes().takeIf { it.size in 1..512 * 1024 } }
    }.getOrNull()

    private fun sniff(f: File): String {
        val b = f.inputStream().use { s -> ByteArray(4).also { s.read(it) } }
        return when {
            b[0] == 0x89.toByte() && b[1] == 'P'.code.toByte() -> "image/png"
            b[0] == 0xFF.toByte() && b[1] == 0xD8.toByte() -> "image/jpeg"
            else -> "image/x-icon"
        }
    }

    private fun notFound() = WebResourceResponse("text/plain", "utf-8", 404, "Not Found", emptyMap(), ByteArrayInputStream(ByteArray(0)))

    companion object {
        const val HOST = "appassets.androidplatform.net"
        const val ORIGIN = "https://$HOST"
        const val URL = "$ORIGIN/szybki/newtab.html"
        fun isQuickAccess(url: String?) = url != null && url.startsWith("$ORIGIN/szybki/")
    }
}
