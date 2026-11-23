package pl.velivo.browser

import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.security.MessageDigest
import java.security.cert.X509Certificate
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

// 🎞 Mój serwer filmów (Velivo Serwer filmów na komputerze) - w domu i przez internet, jak Jellyfin: adres + hasło serwera.
// Połączenie szyfrowane (TLS) z certyfikatem serwera zapamiętanym przy logowaniu (podstawiony serwer nie przejdzie); hasło nie idzie siecią,
// telefon dostaje własny klucz i podpisuje nim każde zapytanie. Odtwarzacz (VLC) dostaje adres lokalnego pośrednika 127.0.0.1.
object MojSerwer {
    class Srv(val name: String, val host: String, val port: Int, val id: String, val key: String, val fp: String)
    class Film(val id: String, val title: String, val year: Int, val overview: String, val genres: String, val len: Long, var pos: Long, var done: Boolean, val last: Long, val poster: Boolean)

    fun list(pw: Passwords): MutableList<Srv> = runCatching {
        val a = JSONArray(pw.secret("moj-serwer") ?: "[]")
        (0 until a.length()).map { a.getJSONObject(it).let { o -> Srv(o.getString("name"), o.getString("host"), o.getInt("port"), o.getString("id"), o.getString("key"), o.getString("fp")) } }.toMutableList()
    }.getOrDefault(mutableListOf())
    fun save(pw: Passwords, l: List<Srv>) = pw.setSecret("moj-serwer", JSONArray(l.map { JSONObject().put("name", it.name).put("host", it.host).put("port", it.port).put("id", it.id).put("key", it.key).put("fp", it.fp) }).toString())

    private fun hex(b: ByteArray) = b.joinToString("") { "%02X".format(it) }
    private fun hmac(key: ByteArray, msg: String) = Mac.getInstance("HmacSHA256").run { init(SecretKeySpec(key, "HmacSHA256")); doFinal(msg.toByteArray(Charsets.UTF_8)) }

    // TLS: zapamiętany odcisk certyfikatu (fp) albo - przy logowaniu - przyjęcie i podanie, co widać
    private fun tls(host: String, port: Int, fp: String?, seen: ((String) -> Unit)? = null): SSLSocket {
        val tm = object : X509TrustManager {
            override fun checkClientTrusted(c: Array<X509Certificate>, a: String) {}
            override fun checkServerTrusted(c: Array<X509Certificate>, a: String) {
                val f = hex(MessageDigest.getInstance("SHA-256").digest(c[0].encoded)); seen?.invoke(f)
                if (fp != null && f != fp) throw java.security.cert.CertificateException("inny certyfikat serwera")
            }
            override fun getAcceptedIssuers() = arrayOf<X509Certificate>()
        }
        val ctx = SSLContext.getInstance("TLS").apply { init(null, arrayOf(tm), null) }
        val raw = Socket(); raw.connect(InetSocketAddress(host, port), 10000); raw.soTimeout = 30000
        return (ctx.socketFactory.createSocket(raw, host, port, true) as SSLSocket).apply { startHandshake() }
    }
    private fun head(s: Srv, method: String, path: String): String {
        val ts = (System.currentTimeMillis() / 1000).toString()
        val sig = android.util.Base64.encodeToString(hmac(android.util.Base64.decode(s.key, android.util.Base64.NO_WRAP), "$method $path $ts"), android.util.Base64.NO_WRAP)
        return "$method $path HTTP/1.1\r\nHost: ${s.host}\r\nX-Dev: ${s.id}\r\nX-Ts: $ts\r\nX-Sig: $sig\r\n"
    }
    private fun readHead(inp: InputStream): String {
        val sb = StringBuilder()
        while (sb.length < 16384) { val c = inp.read(); if (c < 0) break; sb.append(c.toChar()); if (sb.endsWith("\r\n\r\n")) break }
        return sb.toString()
    }
    // zwykłe zapytanie (lista, plakat, postęp): treść odpowiedzi albo wyjątek z kodem
    private fun get(host: String, port: Int, fp: String?, req: String, seen: ((String) -> Unit)? = null): ByteArray {
        tls(host, port, fp, seen).use { so ->
            so.outputStream.apply { write((req + "Connection: close\r\n\r\n").toByteArray()); flush() }
            val inp = BufferedInputStream(so.inputStream); val h = readHead(inp)
            val code = h.substringAfter(' ').take(3).toIntOrNull() ?: 0
            val out = ByteArrayOutputStream(); inp.copyTo(out)
            if (code != 200) throw java.io.IOException("HTTP $code")
            return out.toByteArray()
        }
    }
    fun bytes(s: Srv, path: String) = get(s.host, s.port, s.fp, head(s, "GET", path))

    // logowanie: adres (np. moj-dom.ddns.net:41940) i hasło; dowód = HMAC(hasło, odcisk widzianego certyfikatu) - jak na komputerze
    fun login(pw: Passwords, address: String, pass: String, device: String): Srv {
        val a = address.trim().removePrefix("https://").removePrefix("http://").trimEnd('/')
        val host = a.substringBeforeLast(':', a); val port = if (':' in a) a.substringAfterLast(':').toIntOrNull() ?: 41940 else 41940
        var fp: String? = null
        get(host, port, null, "GET /ping HTTP/1.1\r\nHost: $host\r\n") { fp = it }
        val f = fp ?: throw java.io.IOException("brak certyfikatu")
        val proof = hex(hmac(pass.trim().toByteArray(Charsets.UTF_8), f))
        val j = JSONObject(String(get(host, port, f, "GET /pair?proof=$proof&name=${java.net.URLEncoder.encode(device, "UTF-8")} HTTP/1.1\r\nHost: $host\r\n"), Charsets.UTF_8))
        val s = Srv(j.getString("name"), host, port, j.getString("id"), j.getString("key"), f)
        save(pw, list(pw).filter { it.name != s.name } + s)
        return s
    }

    fun films(s: Srv): List<Film> {
        val a = JSONArray(String(bytes(s, "/lib"), Charsets.UTF_8))
        return (0 until a.length()).map { a.getJSONObject(it) }.map { o ->
            val m = o.optJSONObject("m")
            val g = m?.optJSONArray("Genres")?.let { ga -> (0 until ga.length()).joinToString(", ") { ga.getString(it) } } ?: ""
            Film(o.getString("i"), m?.optString("Name").takeUnless { it.isNullOrEmpty() } ?: o.optString("t"), m?.optInt("Year") ?: 0, m?.optString("Overview") ?: "", g,
                o.optLong("l"), o.optLong("p"), o.optBoolean("d"), o.optLong("w"), !o.isNull("pu"))
        }
    }
    fun progress(s: Srv, id: String, posMs: Long, lenMs: Long, done: Boolean, unwatch: Boolean = false) =
        Thread { runCatching { bytes(s, "/prog?id=$id&pos=$posMs&len=$lenMs" + (if (done) "&done=1" else "") + (if (unwatch) "&unwatch=1" else "")) } }.start()

    // lokalny pośrednik dla odtwarzacza: http://127.0.0.1:<port>/<nr serwera>/<id filmu> → TLS do serwera, podpisane, z zakresem (przewijanie)
    @Volatile private var proxy: ServerSocket? = null
    @Volatile var servers: List<Srv> = emptyList()
    fun url(idx: Int, id: String): String {
        val p = proxy ?: synchronized(this) {
            proxy ?: ServerSocket(0, 16, java.net.InetAddress.getByName("127.0.0.1")).also { ss ->
                proxy = ss
                Thread { while (true) { val c = runCatching { ss.accept() }.getOrNull() ?: break; Thread { serve(c) }.start() } }.apply { isDaemon = true }.start()
            }
        }
        return "http://127.0.0.1:${p.localPort}/$idx/$id"
    }
    private fun serve(c: Socket) = runCatching {
        c.use {
            val inp = BufferedInputStream(c.getInputStream()); val h = readHead(inp)
            val first = h.substringBefore("\r\n").split(' '); if (first.size < 2) return@use
            val parts = first[1].trim('/').split('/'); val s = servers.getOrNull(parts.getOrNull(0)?.toIntOrNull() ?: -1) ?: return@use
            val range = h.split("\r\n").firstOrNull { it.startsWith("Range:", true) }
            val method = if (first[0] == "HEAD") "HEAD" else "GET"
            tls(s.host, s.port, s.fp).use { so ->
                so.outputStream.apply { write((head(s, method, "/f/" + parts.getOrNull(1)) + (if (range != null) range + "\r\n" else "") + "Connection: close\r\n\r\n").toByteArray()); flush() }
                so.soTimeout = 0
                val out: OutputStream = c.getOutputStream(); so.inputStream.copyTo(out, 1 shl 16); out.flush()
            }
        }
    }
}
