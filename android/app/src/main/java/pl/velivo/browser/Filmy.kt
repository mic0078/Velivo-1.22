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
    // catchup: archiwum (oglądanie audycji, które już były) - z listy M3U: catchup, catchup-source, catchup-days / tvg-rec
    class Channel(val source: String, val name: String, val logo: String, val group: String, val url: String, val tvgId: String,
                  val catchup: String = "", val catchupSrc: String = "", val catchupDays: Int = 0) {
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
        var u = url
        var c: HttpURLConnection
        var hops = 0
        while (true) {   // przekierowania także http <-> https (HttpURLConnection sam ich nie robi)
            c = URL(u).openConnection() as HttpURLConnection
            c.connectTimeout = 20000; c.readTimeout = 60000; c.instanceFollowRedirects = false
            c.setRequestProperty("User-Agent", "VLC/3.0.20 LibVLC/3.0.20")   // część serwerów IPTV odrzuca nieznane programy
            val code = c.responseCode
            if (code in 300..399 && hops++ < 6) { val loc = c.getHeaderField("Location") ?: break; c.disconnect(); u = URL(URL(u), loc).toString(); continue }
            if (code >= 400) { c.disconnect(); throw java.io.IOException("HTTP $code") }   // bez adresu - może zawierać token
            break
        }
        return unpack(c.inputStream)
    }

    private fun unpack(input: InputStream): InputStream {
        val s = input.buffered()
        s.mark(2)
        val gz = s.read() == 0x1f && s.read() == 0x8b
        s.reset()
        return if (gz) GZIPInputStream(s).buffered() else s
    }

    fun readText(ctx: Context, url: String): String {
        val bytes = (if (url.startsWith("content://")) unpack(ctx.contentResolver.openInputStream(android.net.Uri.parse(url)) ?: throw java.io.IOException("brak pliku"))
            else open(url)).use { it.readBytes() }
        return String(bytes, Charsets.UTF_8).removePrefix("\uFEFF")
    }

    // M3U: #EXTINF:-1 tvg-id="…" tvg-logo="…" group-title="…",Nazwa  /  adres w następnej linii
    fun parseM3u(source: String, text: String): Pair<List<Channel>, String> {
        val out = ArrayList<Channel>()
        var epg = ""
        var name = ""; var logo = ""; var group = ""; var tvg = ""; var cu = ""; var cuSrc = ""; var cuDays = 0
        var dCu = ""; var dCuSrc = ""; var dCuDays = 0   // domyślne dla całej listy (z #EXTM3U)
        fun attr(l: String, k: String) = Regex("""$k="([^"]*)"""").find(l)?.groupValues?.get(1) ?: ""
        for (raw in text.lineSequence()) {
            val l = raw.trim()
            when {
                l.startsWith("#EXTM3U") -> { epg = attr(l, "url-tvg").ifEmpty { attr(l, "x-tvg-url") }.substringBefore(','); dCu = attr(l, "catchup"); dCuSrc = attr(l, "catchup-source"); dCuDays = attr(l, "catchup-days").toIntOrNull() ?: 0 }
                l.startsWith("#EXTINF") -> { name = l.substringAfterLast(',').trim(); logo = attr(l, "tvg-logo"); group = attr(l, "group-title"); tvg = attr(l, "tvg-id")
                    cu = attr(l, "catchup").ifEmpty { attr(l, "catchup-type") }.ifEmpty { dCu }.lowercase(); cuSrc = attr(l, "catchup-source").ifEmpty { dCuSrc }
                    cuDays = listOf(attr(l, "catchup-days"), attr(l, "timeshift"), attr(l, "tvg-rec")).mapNotNull { it.toIntOrNull() }.maxOrNull() ?: dCuDays }
                l.startsWith("#EXTGRP:") -> group = l.substringAfter(':').trim()
                l.isNotEmpty() && !l.startsWith("#") -> {
                    // jak na Windows: dni bez rodzaju - rodzaj po adresie; rodzaj bez dni - typowe 3 dni
                    if (cuDays > 0 && cu.isEmpty()) cu = if (cuSrc.isNotEmpty()) "default" else if (xcLive.matches(l)) "xc" else "flussonic"
                    else if (cu.isNotEmpty() && cuDays == 0 && cu != "disabled") cuDays = 3
                    if (name.isNotEmpty()) out.add(Channel(source, name, logo, group, l, tvg, cu, cuSrc, cuDays))
                    name = ""; logo = ""; group = ""; tvg = ""; cu = ""; cuSrc = ""; cuDays = 0
                }
            }
        }
        return out to epg
    }

    private val memChans = java.util.concurrent.ConcurrentHashMap<String, List<Channel>>()
    private val memEpg = java.util.concurrent.ConcurrentHashMap<String, Map<String, List<Show>>>()

    private fun cacheFile(ctx: Context, id: String, ext: String) = File(File(ctx.filesDir, "tv").apply { mkdirs() }, id + ext)

    // lista z pliku na komputerze (przyszła przez LAN): treść tylko w zaszyfrowanym zapisie
    fun importText(ctx: Context, pw: Passwords, src: Source, text: String) { memChans.remove(src.id + src.url); cacheFile(ctx, src.id, ".bin").writeBytes(pw.sealBytes(text.toByteArray(Charsets.UTF_8))) }

    // lista kanałów: z internetu (i zapis zaszyfrowany), a bez internetu - z zapisu
    fun channels(ctx: Context, pw: Passwords, src: Source, refresh: Boolean): List<Channel> {
        if (!refresh) memChans[src.id + src.url]?.let { return it }   // ponowne otwarcie Telewizji - od razu, bez czytania pliku
        val f = cacheFile(ctx, src.id, ".bin")
        if ((!refresh || src.url.startsWith("velivo-pc:")) && f.exists()) pw.openBytes(f.readBytes())?.let { return parseM3u(src.id, String(it, Charsets.UTF_8)).first.also { l -> memChans[src.id + src.url] = l } }
        if (src.url.startsWith("velivo-pc:")) throw java.io.IOException("lista z komputera - wyślij ją ponownie z Velivo na Windows")
        val text = readText(ctx, src.url)
        if (!text.contains("#EXTINF")) throw java.io.IOException("to nie jest lista M3U")
        f.writeBytes(pw.sealBytes(text.toByteArray(Charsets.UTF_8)))
        val (list, epg) = parseM3u(src.id, text)
        if (src.epg.isEmpty() && epg.isNotEmpty()) { src.epg = epg; saveSources(pw, sources(pw).map { if (it.id == src.id) src else it }) }
        memChans[src.id + src.url] = list
        return list
    }

    // Program TV (XMLTV): tylko kanały z listy i tylko od 6 godzin wstecz do 2 dni naprzód - mało pamięci, szybko
    fun epg(ctx: Context, src: Source, chans: List<Channel>, refresh: Boolean): Map<String, List<Show>> {
        // zapis programu związany z adresem EPG: nowy / zmieniony adres pobiera się od razu (dawniej stary, pusty zapis
        // był „świeży” jeszcze 12 godzin i własnego EPG nie było widać); pusty zapis nigdy nie jest świeży
        val key = src.id + "-" + Integer.toHexString(src.epg.hashCode())
        val f = cacheFile(ctx, key, ".epg7.json")
        val fresh = f.exists() && f.length() > 2 && System.currentTimeMillis() - f.lastModified() < 12 * 3600_000L   // program odświeżany co 12 godzin
        if (!refresh && fresh) { memEpg[key]?.let { return it }; readEpg(f)?.let { memEpg[key] = it; memEpg[src.id] = it; return it } }
        if (src.epg.isEmpty()) return emptyMap()
        val wanted = HashSet<String>()
        chans.forEach { keys(it).forEach { k -> wanted.add(k) } }
        // dawny program (starszy niż 6 godzin) tylko dla kanałów z archiwum ⏪ - pozostałe go nie potrzebują
        // (lista z tysiącami kanałów i tvg-rec=7: tydzień wstecz dla wszystkich zapełniał całą pamięć telefonu)
        val archKeys = HashSet<String>().also { a -> chans.filter { hasCatchup(it) }.forEach { keys(it).forEach { k -> a.add(k) } } }
        val recent = System.currentTimeMillis() - 6 * 3600_000L
        val back = chans.filter { hasCatchup(it) }.maxOfOrNull { it.catchupDays }?.coerceIn(0, 7) ?: 0
        val from = System.currentTimeMillis() - (if (back > 0) back * 24 else 6) * 3600_000L   // wstecz tyle dni, ile archiwum kanałów (⏪), najwyżej 7
        val to = System.currentTimeMillis() + 72 * 3600_000L   // program na 3 dni
        // opisy tylko blisko "teraz" (od 6 godzin wstecz do jutra) - przy tysiącach kanałów i archiwum opisy zajmowały setki MB
        val descFrom = System.currentTimeMillis() - 6 * 3600_000L; val descTo = System.currentTimeMillis() + 30 * 3600_000L
        val names = HashMap<String, MutableList<String>>()   // id kanału w programie -> nazwy
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
                        "dn" -> curId?.let { names.getOrPut(it) { ArrayList() }.add(p.text.trim()) }
                        "t" -> title += p.text
                        "d" -> desc += p.text
                    }
                    XmlPullParser.END_TAG -> {
                        if (p.name == "programme") {
                            val id = curId ?: ""
                            if (stop > from && start < to) {
                                val cands = (listOf(id) + (names[id] ?: emptyList())).flatMap { listOf(it.lowercase(), epgKey(it)) }.filter { it.isNotEmpty() && it in wanted && (stop > recent || it in archKeys) }.distinct()
                                if (cands.isNotEmpty()) { val sh = Show(start, stop, title.trim(), if (stop > descFrom && start < descTo) desc.trim().take(300) else ""); cands.forEach { k -> out.getOrPut(k) { ArrayList() }.add(sh) } }
                            }
                        }
                        field = ""
                    }
                }
                ev = p.next()
            }
        }
        out.values.forEach { it.sortBy { s -> s.start } }
        // zapis strumieniem prosto do pliku - bez budowania całego tekstu w pamięci (przy 8000 kanałów: brak pamięci, 150 MB naraz)
        android.util.JsonWriter(java.io.OutputStreamWriter(java.io.BufferedOutputStream(java.io.FileOutputStream(f)), Charsets.UTF_8)).use { w ->
            w.beginObject()
            out.forEach { (k, l) -> w.name(k); w.beginArray(); l.forEach { w.beginArray(); w.value(it.start); w.value(it.stop); w.value(it.title); w.value(it.desc); w.endArray() }; w.endArray() }
            w.endObject()
        }
        cacheFile(ctx, src.id, ".epg6.json").delete()   // stary zapis (bez adresu w nazwie)
        memEpg[key] = out; memEpg[src.id] = out
        return out
    }

    // odczyt strumieniem (plik programu bywa duży); ten sam program kilku kanałów - jeden obiekt
    private fun readEpg(f: File): Map<String, List<Show>>? = runCatching {
        val out = HashMap<String, List<Show>>()
        val same = HashMap<String, Show>()
        android.util.JsonReader(java.io.InputStreamReader(java.io.BufferedInputStream(java.io.FileInputStream(f)), Charsets.UTF_8)).use { r ->
            r.beginObject()
            while (r.hasNext()) {
                val k = r.nextName(); val l = ArrayList<Show>()
                r.beginArray()
                while (r.hasNext()) { r.beginArray(); val a = r.nextLong(); val b = r.nextLong(); val t = r.nextString(); val d = r.nextString(); r.endArray()
                    l.add(same.getOrPut("$a|$b|$t") { Show(a, b, t, d) }) }
                r.endArray(); out[k] = l
            }
            r.endObject()
        }
        out
    }.getOrNull()

    // 20261019183000 +0200
    // czas XMLTV: rrrrmmddggmm[ss] [+hhmm] - sekundy i strefa bywają pominięte (bez strefy - czas telefonu)
    fun xmltvTime(s: String?): Long = runCatching {
        val t = s!!.trim()
        val d = t.takeWhile { it.isDigit() }
        val dt = java.time.LocalDateTime.of(d.substring(0, 4).toInt(), d.substring(4, 6).toInt(), d.substring(6, 8).toInt(), d.substring(8, 10).toInt(), d.substring(10, 12).toInt(), if (d.length >= 14) d.substring(12, 14).toInt() else 0)
        val off = t.substring(d.length).trim().replace(":", "")
        val zone: java.time.ZoneId = if (off.length >= 5 && (off[0] == '+' || off[0] == '-')) java.time.ZoneOffset.of(off.substring(0, 3) + ":" + off.substring(3, 5)) else java.time.ZoneId.systemDefault()
        dt.atZone(zone).toInstant().toEpochMilli()
    }.getOrDefault(0L)

    // ---------- archiwum (catch-up) - jak w Velivo na Windows (Iptv.cs) ----------
    private val xcLive = Regex("""^(https?://[^/]+)/(?:live/)?([^/]+)/([^/]+)/(\d+)(\.\w+)?$""", RegexOption.IGNORE_CASE)

    fun hasCatchup(c: Channel) = c.catchupDays > 0 && c.catchup.isNotEmpty() && c.catchup != "disabled"

    // audycja jest jeszcze w archiwum: zaczęła się i nie jest starsza niż liczba dni archiwum
    fun canCatchup(c: Channel, s: Show) = hasCatchup(c) && s.start < System.currentTimeMillis() && s.start >= System.currentTimeMillis() - c.catchupDays * 86_400_000L

    // adres audycji z archiwum - formaty jak w popularnych odtwarzaczach IPTV (Kodi IPTV Simple, TiviMate):
    // flussonic: .../index.m3u8 -> .../index-{start}-{dlugosc}.m3u8, .../mpegts -> .../timeshift_abs-{start}.ts; shift: ?utc=&lutc=;
    // xc: /live/u/h/id.ts -> /timeshift/u/h/{minuty}/{rrrr-mm-dd:gg-mm}/id.ts; default / append: wzór z catchup-source
    // variant: kolejne odmiany adresu u tego samego rodzaju (serwery się różnią) - null, gdy więcej nie ma
    fun catchupUrl(c: Channel, start: Long, end: Long, variant: Int = 0): String? {
        if (!hasCatchup(c)) return null
        val s = start / 1000; val e = end / 1000; val n = System.currentTimeMillis() / 1000
        val dur = maxOf(60L, e - s)
        var url = c.url; var q = ""
        val qi = url.indexOf('?'); if (qi >= 0) { q = url.substring(qi); url = url.substring(0, qi) }
        fun fill(t0: String): String {
            var t = t0
            for ((k, v) in listOf("utc" to s, "start" to s, "timestamp" to s, "utcend" to e, "end" to e, "lutc" to n, "now" to n, "duration" to dur, "offset" to maxOf(0L, n - s)))
                t = t.replace("\${" + k + "}", v.toString()).replace("{" + k + "}", v.toString())
            t = Regex("""\{duration:(\d+)\}""").replace(t) { m -> (dur / maxOf(1L, m.groupValues[1].toLong())).toString() }
            val u = java.util.Calendar.getInstance(java.util.TimeZone.getTimeZone("UTC")).apply { timeInMillis = start }
            fun two(v: Int) = String.format(java.util.Locale.US, "%02d", v)
            return t.replace("{Y}", u.get(java.util.Calendar.YEAR).toString()).replace("{m}", two(u.get(java.util.Calendar.MONTH) + 1)).replace("{d}", two(u.get(java.util.Calendar.DAY_OF_MONTH)))
                .replace("{H}", two(u.get(java.util.Calendar.HOUR_OF_DAY))).replace("{M}", two(u.get(java.util.Calendar.MINUTE))).replace("{S}", two(u.get(java.util.Calendar.SECOND)))
        }
        fun shift() = c.url + (if ('?' in c.url) "&" else "?") + "utc=$s&lutc=$n"
        fun flussonic(): String? {
            val sl = url.lastIndexOf('/'); if (sl < 0) return null
            val dir = url.substring(0, sl + 1); val last = url.substring(sl + 1)
            if (last.endsWith(".m3u8", true)) {
                val b = last.dropLast(5)
                return when (variant) {   // odmiany Flussonic dla HLS (różne serwery / pośrednicy)
                    0 -> "$dir$b-$s-$dur.m3u8$q"; 1 -> "${dir}archive-$s-$dur.m3u8$q"; 2 -> "${dir}timeshift_abs-$s.m3u8$q"
                    3 -> "$dir$b-$s-now.m3u8$q"; 4 -> "${dir}timeshift_abs-$s.ts$q"; 5 -> shift(); else -> null
                }
            }
            return when (variant) { 0 -> "${dir}timeshift_abs-$s.ts$q"; 1 -> "${dir}archive-$s-$dur.ts$q"; 2 -> shift(); else -> null }   // strumień mpegts
        }
        return when (c.catchup) {
            "append" -> if (variant == 0) c.url + fill(c.catchupSrc) else null
            "shift" -> if (variant == 0) shift() else null
            "xc" -> { val m = xcLive.find(c.url); if (m == null || variant > 0) null else
                m.groupValues[1] + "/timeshift/" + m.groupValues[2] + "/" + m.groupValues[3] + "/" + (dur / 60) + "/" + java.text.SimpleDateFormat("yyyy-MM-dd:HH-mm", java.util.Locale.US).format(java.util.Date(start)) + "/" + m.groupValues[4] + ".ts" }
            "default" -> if (c.catchupSrc.isNotEmpty()) (if (variant == 0) fill(c.catchupSrc) else null) else flussonic()
            "flussonic", "fs", "flussonic-hls", "flussonic-ts" -> flussonic()
            else -> if (variant == 0 && c.catchupSrc.isNotEmpty()) fill(c.catchupSrc) else null
        }
    }

    // program kanału z pamięci (odtwarzacz: następna audycja z archiwum, "od początku")
    fun cachedShows(ch: Channel): List<Show> = memEpg[ch.source]?.let { showsFor(it, ch) } ?: emptyList()

    // działająca odmiana adresu archiwum zapamiętana dla serwera (następnym razem od razu)
    fun archiveHost(url: String) = runCatching { java.net.URI(url).authority.lowercase() }.getOrDefault("")

    fun showsFor(epg: Map<String, List<Show>>, ch: Channel): List<Show> = keys(ch).firstNotNullOfOrNull { epg[it] } ?: emptyList()

    // jak EpgKey na Windows: bez dopisków jakości (HD/FHD/SD…), nawiasów, prefiksu "|PL|" i znaków innych niż litery/cyfry
    private val qualityTag = Regex("""\b(uhd|fhd|hd|sd|4k|hevc|h265|raw)\b|\(.*?\)|\[.*?\]|^\|?[a-z]{2}\|\s*|[^a-z0-9]""")
    fun epgKey(name: String): String { val k = qualityTag.replace(name.lowercase().replace(".pl", ""), ""); return if (k.isEmpty()) "" else "@$k" }
    private fun keys(ch: Channel) = listOf(ch.tvgId.lowercase(), ch.name.lowercase(), epgKey(ch.tvgId), epgKey(ch.name)).filter { it.isNotEmpty() }.distinct()

    fun nowNext(epg: Map<String, List<Show>>, ch: Channel, t: Long = System.currentTimeMillis()): Pair<Show?, Show?> {
        val l = showsFor(epg, ch)
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

    // --- napisy z Jellyfin (do lektora i do pobrania) ---
    class Sub(val label: String, val path: String, val lang: String)
    fun bytes(c: Conf, path: String): ByteArray {
        val h = URL(c.url + path).openConnection() as HttpURLConnection
        h.connectTimeout = 15000; h.readTimeout = 60000
        h.setRequestProperty("X-Emby-Token", c.key)
        return h.inputStream.use { it.readBytes() }
    }
    // napisy tekstowe filmu (zewnętrzne pliki i wbudowane w film) - Jellyfin podaje je jako SRT; polskie najpierw
    fun subtitles(c: Conf, id: String): List<Sub> {
        val item = if (c.user.isNotEmpty()) JSONObject(get(c, "/Users/${c.user}/Items/$id"))
            else JSONObject(get(c, "/Items?Ids=$id&Fields=MediaSources,MediaStreams")).optJSONArray("Items")?.optJSONObject(0) ?: return emptyList()
        val ms = item.optJSONArray("MediaSources")?.optJSONObject(0) ?: return emptyList()
        val msId = ms.optString("Id", id)
        val st = ms.optJSONArray("MediaStreams") ?: return emptyList()
        return (0 until st.length()).map { st.getJSONObject(it) }.filter { it.optString("Type") == "Subtitle" && it.optBoolean("IsTextSubtitleStream", true) }
            .map { Sub(it.optString("DisplayTitle").ifEmpty { it.optString("Language").ifEmpty { "?" } }, "/Videos/$id/$msId/Subtitles/${it.optInt("Index")}/Stream.srt", it.optString("Language")) }
            .sortedBy { if (it.lang.lowercase() in setOf("pol", "pl", "polish")) 0 else 1 }
    }
    // wyszukiwanie napisów w sieci przez serwer (wtyczka napisów w Jellyfin, np. OpenSubtitles) i pobranie do filmu
    fun searchSubs(c: Conf, id: String, lang: String): List<Pair<String, String>> {
        val a = JSONArray(get(c, "/Items/$id/RemoteSearch/Subtitles/$lang"))
        return (0 until a.length()).map { a.getJSONObject(it) }.map { it.getString("Id") to (it.optString("Name") + " · " + it.optString("ProviderName") + (if (it.optInt("DownloadCount") > 0) " · ⬇" + it.optInt("DownloadCount") else "")) }
    }
    fun downloadSub(c: Conf, id: String, subId: String): Int {
        val h = URL(c.url + "/Items/$id/RemoteSearch/Subtitles/" + java.net.URLEncoder.encode(subId, "UTF-8")).openConnection() as HttpURLConnection
        h.requestMethod = "POST"; h.connectTimeout = 15000; h.readTimeout = 60000
        h.setRequestProperty("X-Emby-Token", c.key); h.doOutput = true
        h.outputStream.use { }
        return h.responseCode
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

    // Video: filmy z bibliotek "mieszanych" / domowych i nierozpoznane przez Jellyfin (jak w Velivo na Windows) - bez tego było ich tylko kilka
    private const val LIB = "/Items?IncludeItemTypes=Movie,Series,Video&Recursive=true&SortBy=SortName&Fields=Overview,ProductionYear,DateCreated&EnableImageTypes=Primary"
    private const val RES = "/Items/Resume?MediaTypes=Video&Limit=30&Fields=Overview"
    private fun cacheFile(ctx: Context) = File(File(ctx.filesDir, "tv").apply { mkdirs() }, "jellyfin.bin")

    // biblioteka z serwera i zapis (zaszyfrowany) - przy następnym otwarciu od razu na ekranie, bez czekania
    fun refresh(ctx: Context, pw: Passwords, c: Conf): Pair<List<Item>, List<Item>> {
        val lib = get(c, "/Users/${c.user}$LIB"); val res = get(c, "/Users/${c.user}$RES")
        cacheFile(ctx).writeBytes(pw.sealBytes(JSONObject().put("lib", lib).put("res", res).toString().toByteArray(Charsets.UTF_8)))
        return items(lib) to items(res)
    }

    fun cached(ctx: Context, pw: Passwords): Pair<List<Item>, List<Item>>? = runCatching {
        val j = JSONObject(String(pw.openBytes(cacheFile(ctx).readBytes())!!, Charsets.UTF_8))
        items(j.getString("lib")) to items(j.getString("res"))
    }.getOrNull()
    fun episodes(c: Conf, seriesId: String) = items(get(c, "/Shows/$seriesId/Episodes?UserId=${c.user}&Fields=Overview"))
    fun image(c: Conf, id: String) = c.url + "/Items/$id/Images/Primary?maxHeight=360&quality=80"
    fun stream(c: Conf, id: String) = c.url + "/Videos/$id/stream?static=true"

    // miejsce w filmie wraca do Jellyfin (Oglądane dalej także na komputerze i telewizorze)
    fun reportStopped(c: Conf, id: String, posMs: Long) =
        post(c, "/Sessions/Playing/Stopped", JSONObject().put("ItemId", id).put("PositionTicks", posMs * 10_000))
}
