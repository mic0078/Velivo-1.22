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

    // Pliki strony z assets; newtab.html z dołączoną nakładką, język wg telefonu.
    fun serve(u: Uri): WebResourceResponse {
        val name = u.path.orEmpty().removePrefix("/szybki/")
        if (name.isEmpty() || name.contains("..") || name.contains('/')) return notFound()
        val bytes: ByteArray = when (name) {
            "jezyk-wybor.js" -> "var VELIVO_LANG = '${if (Locale.getDefault().language == "pl") "pl" else "en"}';".toByteArray()
            else -> runCatching { ctx.assets.open(name).use { it.readBytes() } }.getOrNull() ?: return notFound()
        }
        val out = when (name) {
            "newtab.html" -> String(bytes).replaceFirst("<head>", "<head><script src=\"velivo-chrome.js\"></script>").toByteArray()
            // tłumaczenie strony działa też poza chrome-extension://
            "jezyk.js" -> String(bytes).replace("location.protocol !== 'chrome-extension:'", "false").toByteArray()
            else -> bytes
        }
        val mime = when (name.substringAfterLast('.')) {
            "html" -> "text/html"; "js" -> "application/javascript"; "css" -> "text/css"
            "png" -> "image/png"; "json" -> "application/json"; else -> "application/octet-stream"
        }
        return WebResourceResponse(mime, "utf-8", ByteArrayInputStream(out))
    }

    private fun notFound() = WebResourceResponse("text/plain", "utf-8", 404, "Not Found", emptyMap(), ByteArrayInputStream(ByteArray(0)))

    companion object {
        const val HOST = "appassets.androidplatform.net"
        const val ORIGIN = "https://$HOST"
        const val URL = "$ORIGIN/szybki/newtab.html"
        fun isQuickAccess(url: String?) = url != null && url.startsWith("$ORIGIN/szybki/")
    }
}
