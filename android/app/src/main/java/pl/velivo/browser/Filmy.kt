package pl.velivo.browser

import android.content.Context
import android.util.Xml
import org.json.JSONArray
import org.json.JSONObject
import org.xmlpull.v1.XmlPullParser
import java.io.File
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.zip.GZIPInputStream

// Telewizja (IPTV) i Jellyfin na telefonie - jak w Velivo na Windows (Iptv.cs, JellyfinMeta.cs).
// Prywatność: adresy list (często z tokenem) i klucz Jellyfin tylko zaszyfrowane (Android Keystore),
// lista kanałów zapisana zaszyfrowana, nigdy w logach ani w komunikatach o błędach.
object Tv {
    class Source(var id: String, var name: String, var url: String, var epg: String)
    class Channel(val source: String, val name: String, val logo: String, val group: String, val url: String, val tvgId: String) {
        val key get() = "$source|$name"   // ulubione i własne listy po nazwie, nie po adresie (adres może mieć token)
    }
    class Show(val start: Long, val stop: Long, val title: String, val desc: String)

    fun sources(pw: Passwords): MutableList<Source> = runCatching {
        val a = JSONArray(pw.secret("tv-zrodla") ?: "[]")
        (0 until a.length()).map { a.getJSONObject(it) }.map { Source(it.optString("id"), it.optString("name"), it.optString("url"), it.optString("epg")) }.toMutableList()
    }.getOrDefault(mutableListOf())

    fun saveSources(pw: Passwords, l: List<Source>) {
        val a = JSONArray()
        l.forEach { a.put(JSONObject().put("id", it.id).put("name", it.name).put("url", it.url).put("epg", it.epg)) }
        pw.setSecret("tv-zrodla", a.toString())
    }

    private fun open(url: String): InputStream {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 20000; c.readTimeout = 60000; c.instanceFollowRedirects = true
        c.setRequestProperty("User-Agent", "Velivo")
        val s = c.inputStream.buffered()
        s.mark(2)
        val gz = s.read() == 0x1f && s.read() == 0x8b
        s.reset()
        return if (gz || url.endsWith(".gz")) GZIPInputStream(s) else s
    }

    // M3U: #EXTINF:-1 tvg-id="…" tvg-logo="…" group-title="…",Nazwa  /  adres w następnej linii
    fun parseM3u(source: String, text: String): Pair<List<Channel>, String> {
        val out = ArrayList<Channel>()
        var epg = ""
        var name = ""; var logo = ""; var group = ""; var tvg = ""
        fun attr(l: String, k: String) = Regex("""$k="([^"]*)"""").find(l)?.groupValues?.get(1) ?: ""
        for (raw in text.lineSequence()) {
            val l = raw.trim()
            when {
                l.startsWith("#EXTM3U") -> epg = attr(l, "url-tvg").ifEmpty { attr(l, "x-tvg-url") }.substringBefore(',')
                l.startsWith("#EXTINF") -> { name = l.substringAfterLast(',').trim(); logo = attr(l, "tvg-logo"); group = attr(l, "group-title"); tvg = attr(l, "tvg-id") }
                l.startsWith("#EXTGRP:") -> group = l.substringAfter(':').trim()
                l.isNotEmpty() && !l.startsWith("#") -> {
                    if (name.isNotEmpty()) out.add(Channel(source, name, logo, group, l, tvg))
                    name = ""; logo = ""; group = ""; tvg = ""
                }
            }
        }
        return out to epg
    }

    private fun cacheFile(ctx: Context, id: String, ext: String) = File(File(ctx.filesDir, "tv").apply { mkdirs() }, id + ext)

    // lista kanałów: z internetu (i zapis zaszyfrowany), a bez internetu - z zapisu
    fun channels(ctx: Context, pw: Passwords, src: Source, refresh: Boolean): List<Channel> {
        val f = cacheFile(ctx, src.id, ".bin")
        if (!refresh && f.exists()) pw.openBytes(f.readBytes())?.let { return parseM3u(src.id, String(it, Charsets.UTF_8)).first }
        val text = if (src.url.startsWith("content://")) ctx.contentResolver.openInputStream(android.net.Uri.parse(src.url))!!.use { String(it.readBytes(), Charsets.UTF_8) }
            else open(src.url).use { String(it.readBytes(), Charsets.UTF_8) }
        f.writeBytes(pw.sealBytes(text.toByteArray(Charsets.UTF_8)))
        val (list, epg) = parseM3u(src.id, text)
        if (src.epg.isEmpty() && epg.isNotEmpty()) { src.epg = epg; saveSources(pw, sources(pw).map { if (it.id == src.id) src else it }) }
        return list
    }

    // Program TV (XMLTV): tylko kanały z listy i tylko od 6 godzin wstecz do 2 dni naprzód - mało pamięci, szybko
    fun epg(ctx: Context, src: Source, chans: List<Channel>, refresh: Boolean): Map<String, List<Show>> {
        val f = cacheFile(ctx, src.id, ".epg.json")
        if (!refresh && f.exists() && System.currentTimeMillis() - f.lastModified() < 12 * 3600_000L) readEpg(f)?.let { return it }
        if (src.epg.isEmpty()) return emptyMap()
        val wanted = HashSet<String>()
        chans.forEach { if (it.tvgId.isNotEmpty()) wanted.add(it.tvgId.lowercase()); wanted.add(it.name.lowercase()) }
        val from = System.currentTimeMillis() - 6 * 3600_000L
        val to = System.currentTimeMillis() + 48 * 3600_000L
        val names = HashMap<String, String>()   // id kanału w programie -> nazwa
        val out = HashMap<String, MutableList<Show>>()
        open(src.epg).use { input ->
            val p = Xml.newPullParser()
            p.setInput(input, null)
            var ev = p.eventType
            var curId: String? = null; var start = 0L; var stop = 0L; var title = ""; var desc = ""; var field = ""
            while (ev != XmlPullParser.END_DOCUMENT) {
                when (ev) {
                    XmlPullParser.START_TAG -> when (p.name) {
                        "channel" -> { curId = p.getAttributeValue(null, "id"); field = "" }
                        "display-name" -> field = "dn"
                        "programme" -> {
                            curId = p.getAttributeValue(null, "channel"); start = xmltvTime(p.getAttributeValue(null, "start")); stop = xmltvTime(p.getAttributeValue(null, "stop"))
                            title = ""; desc = ""; field = ""
                        }
                        "title" -> field = "t"
                        "desc" -> field = "d"
                        else -> field = ""
                    }
                    XmlPullParser.TEXT -> when (field) {
                        "dn" -> curId?.let { if (!names.containsKey(it)) names[it] = p.text.trim() }
                        "t" -> title += p.text
                        "d" -> desc += p.text
                    }
                    XmlPullParser.END_TAG -> {
                        if (p.name == "programme") {
                            val id = curId ?: ""
                            val key = if (id.lowercase() in wanted) id.lowercase() else names[id]?.lowercase()?.takeIf { it in wanted }
                            if (key != null && stop > from && start < to) out.getOrPut(key) { ArrayList() }.add(Show(start, stop, title.trim(), desc.trim().take(400)))
                        }
                        field = ""
                    }
                }
                ev = p.next()
            }
        }
        out.values.forEach { it.sortBy { s -> s.start } }
        val j = JSONObject()
        out.forEach { (k, l) -> j.put(k, JSONArray().apply { l.forEach { put(JSONArray().put(it.start).put(it.stop).put(it.title).put(it.desc)) } }) }
        f.writeText(j.toString())
        return out
    }

    private fun readEpg(f: File): Map<String, List<Show>>? = runCatching {
        val j = JSONObject(f.readText())
        j.keys().asSequence().associateWith { k -> val a = j.getJSONArray(k); (0 until a.length()).map { val x = a.getJSONArray(it); Show(x.getLong(0), x.getLong(1), x.getString(2), x.getString(3)) } }
    }.getOrNull()

    // 20261019183000 +0200
    fun xmltvTime(s: String?): Long = runCatching {
        val t = s!!.trim()
        val dt = java.time.LocalDateTime.of(t.substring(0, 4).toInt(), t.substring(4, 6).toInt(), t.substring(6, 8).toInt(), t.substring(8, 10).toInt(), t.substring(10, 12).toInt(), t.substring(12, 14).toInt())
        val off = t.substring(14).trim()
        val zone = if (off.length >= 5) java.time.ZoneOffset.of(off.substring(0, 3) + ":" + off.substring(3, 5)) else java.time.ZoneOffset.UTC
        dt.toInstant(zone).toEpochMilli()
    }.getOrDefault(0L)

    fun showsFor(epg: Map<String, List<Show>>, ch: Channel): List<Show> = epg[ch.tvgId.lowercase()] ?: epg[ch.name.lowercase()] ?: emptyList()

    fun nowNext(epg: Map<String, List<Show>>, ch: Channel): Pair<Show?, Show?> {
        val l = showsFor(epg, ch); val t = System.currentTimeMillis()
        val i = l.indexOfFirst { it.start <= t && it.stop > t }
        return if (i < 0) null to l.firstOrNull { it.start > t } else l[i] to l.getOrNull(i + 1)
    }
}

// Jellyfin: adres i klucz API wpisane przez Ciebie (albo przesłane z Velivo na komputerze) - zaszyfrowane w telefonie.
// Klucz idzie w nagłówku X-Emby-Token, nigdy w adresie (nie trafia do logów ani historii).
object Jelly {
    class Conf(val url: String, val key: String, val user: String)
    class Item(val id: String, val name: String, val type: String, val year: Int, val overview: String, val series: String, val ticks: Long, val posTicks: Long, val added: String)

    fun conf(pw: Passwords): Conf? = runCatching {
        val j = JSONObject(pw.secret("jellyfin") ?: return null)
        Conf(j.getString("url").trimEnd('/'), j.getString("key"), j.optString("user"))
    }.getOrNull()

    fun save(pw: Passwords, c: Conf?) = pw.setSecret("jellyfin", c?.let { JSONObject().put("url", it.url.trimEnd('/')).put("key", it.key).put("user", it.user).toString() })

    fun get(c: Conf, path: String): String {
        val h = URL(c.url + path).openConnection() as HttpURLConnection
        h.connectTimeout = 15000; h.readTimeout = 30000
        h.setRequestProperty("X-Emby-Token", c.key)
        h.setRequestProperty("Accept", "application/json")
        return h.inputStream.bufferedReader().use { it.readText() }
    }

    fun post(c: Conf, path: String, body: JSONObject) = runCatching {
        val h = URL(c.url + path).openConnection() as HttpURLConnection
        h.requestMethod = "POST"; h.doOutput = true; h.connectTimeout = 10000; h.readTimeout = 10000
        h.setRequestProperty("X-Emby-Token", c.key); h.setRequestProperty("Content-Type", "application/json")
        h.outputStream.use { it.write(body.toString().toByteArray()) }
        h.responseCode
    }

    fun users(c: Conf): List<Pair<String, String>> {
        val a = JSONArray(get(c, "/Users"))
        return (0 until a.length()).map { a.getJSONObject(it).let { o -> o.getString("Id") to o.getString("Name") } }
    }

    // --- Bez kluczy API: Velivo samo znajduje serwer w sieci domowej i logujesz się jak w aplikacji Jellyfin ---
    // Jellyfin odpowiada na rozgłoszenie "who is JellyfinServer?" (UDP 7359) swoim adresem i nazwą.
    fun discover(timeoutMs: Int = 2500): List<Pair<String, String>> {
        val out = LinkedHashMap<String, String>()
        runCatching {
            java.net.DatagramSocket().use { s ->
                s.broadcast = true; s.soTimeout = timeoutMs
                val msg = "who is JellyfinServer?".toByteArray()
                s.send(java.net.DatagramPacket(msg, msg.size, java.net.InetAddress.getByName("255.255.255.255"), 7359))
                val buf = ByteArray(4096)
                val end = System.currentTimeMillis() + timeoutMs
                while (System.currentTimeMillis() < end) {
                    val p = java.net.DatagramPacket(buf, buf.size)
                    runCatching { s.receive(p) }.getOrNull() ?: break
                    runCatching { val j = JSONObject(String(p.data, 0, p.length)); out[j.getString("Address").trimEnd('/')] = j.optString("Name", "Jellyfin") }
                }
            }
        }
        return out.map { it.key to it.value }
    }

    private fun authHeader(device: String) = "MediaBrowser Client=\"Velivo\", Device=\"$device\", DeviceId=\"velivo-$device\", Version=\"1.22\""

    // użytkownicy widoczni na ekranie logowania serwera (bez hasła i bez klucza)
    fun publicUsers(url: String): List<Pair<String, String>> {
        val h = URL(url.trimEnd('/') + "/Users/Public").openConnection() as HttpURLConnection
        h.connectTimeout = 8000; h.readTimeout = 10000
        val a = JSONArray(h.inputStream.bufferedReader().use { it.readText() })
        return (0 until a.length()).map { a.getJSONObject(it).let { o -> o.getString("Id") to o.getString("Name") } }
    }

    // logowanie nazwą i hasłem: serwer zwraca token tylko dla tego telefonu (widać go w Jellyfin → Urządzenia, można odwołać)
    fun login(url: String, user: String, pass: String, device: String): Conf {
        val h = URL(url.trimEnd('/') + "/Users/AuthenticateByName").openConnection() as HttpURLConnection
        h.requestMethod = "POST"; h.doOutput = true; h.connectTimeout = 10000; h.readTimeout = 15000
        h.setRequestProperty("Content-Type", "application/json")
        h.setRequestProperty("X-Emby-Authorization", authHeader(device))
        h.outputStream.use { it.write(JSONObject().put("Username", user).put("Pw", pass).toString().toByteArray()) }
        val j = JSONObject(h.inputStream.bufferedReader().use { it.readText() })
        return Conf(url.trimEnd('/'), j.getString("AccessToken"), j.getJSONObject("User").getString("Id"))
    }

    private fun items(json: String): List<Item> {
        val a = JSONObject(json).optJSONArray("Items") ?: JSONArray(json)
        return (0 until a.length()).map { a.getJSONObject(it) }.map {
            Item(it.getString("Id"), it.optString("Name"), it.optString("Type"), it.optInt("ProductionYear"), it.optString("Overview"),
                it.optString("SeriesName"), it.optLong("RunTimeTicks"), it.optJSONObject("UserData")?.optLong("PlaybackPositionTicks") ?: 0L, it.optString("DateCreated"))
        }
    }

    fun library(c: Conf) = items(get(c, "/Users/${c.user}/Items?IncludeItemTypes=Movie,Series&Recursive=true&SortBy=SortName&Fields=Overview,ProductionYear,DateCreated&EnableImageTypes=Primary"))
    fun resume(c: Conf) = items(get(c, "/Users/${c.user}/Items/Resume?MediaTypes=Video&Limit=30&Fields=Overview"))
    fun episodes(c: Conf, seriesId: String) = items(get(c, "/Shows/$seriesId/Episodes?UserId=${c.user}&Fields=Overview"))
    fun image(c: Conf, id: String) = c.url + "/Items/$id/Images/Primary?maxHeight=360&quality=80"
    fun stream(c: Conf, id: String) = c.url + "/Videos/$id/stream?static=true"

    // miejsce w filmie wraca do Jellyfin (Oglądane dalej także na komputerze i telewizorze)
    fun reportStopped(c: Conf, id: String, posMs: Long) =
        post(c, "/Sessions/Playing/Stopped", JSONObject().put("ItemId", id).put("PositionTicks", posMs * 10_000))
}
