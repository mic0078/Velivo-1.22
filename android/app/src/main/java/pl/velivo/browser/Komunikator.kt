package pl.velivo.browser

// Ten sam plik jest w przeglądarce Velivo (pl.velivo.browser) i w klawiaturze Velivo (pl.velivo.klawiatura) - zmieniaj oba.
// Komunikator Velivo i pliki telefon <-> telefon w sieci domowej, bez internetu i bez serwera (port 41919, jak LanSync).
//  * szukanie: UDP "p2p-who" -> "p2p-here" (tylko nazwa urządzenia i identyfikator czatu),
//  * kontakt: jednorazowo przez TCP "p2p-pair" - klucze ECDH P-256, ten sam 6-cyfrowy kod na obu ekranach, zgoda odbiorcy,
//  * wiadomości: UDP "p2p-msg" szyfrowane AES-GCM kluczem kontaktu, potwierdzenie "p2p-ack" (HMAC),
//  * pliki: TCP "p2p-file" - jednorazowe klucze ECDH, kod do porównania, zgoda odbiorcy, kawałki 1 MB (AES-GCM), suma SHA-256.
// Kontakty i rozmowy zapisane w telefonie zaszyfrowane kluczem z Android Keystore.

import android.app.Activity
import android.app.AlertDialog
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.os.IBinder
import android.content.Context
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.text.InputType
import android.util.Base64
import android.view.Gravity
import android.view.View
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.FrameLayout
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import org.json.JSONArray
import org.json.JSONObject
import java.io.DataInputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import java.util.Locale
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.KeyGenerator
import javax.crypto.Mac
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

class Komunikator private constructor(private val ctx: Context, private val deviceName: () -> String) {
    class Peer(val id: String, val name: String, val addr: InetAddress)
    // name - nazwa wyświetlana (własna albo urządzenia); device - prawdziwa nazwa urządzenia (aktualizowana z sieci); own - nazwa nadana przez użytkownika
    // relay - adres pośrednika tego kontaktu (No-IP:port, np. NUCBOX) - rozmowy poza domem
    class Contact(val id: String, var name: String, val key: ByteArray, var addr: String, var device: String = name, var own: Boolean = false, var relay: String? = null)
    // read: moja - odbiorca przeczytał (✓✓); odebrana - wysłaliśmy „przeczytane”
    // file: wpis o pliku (nie wysyłany ponownie jako tekst)
    class Msg(val mine: Boolean, var text: String, val ts: Long, var ok: Boolean, val mid: String, var read: Boolean = false, var file: Boolean = false, var path: String? = null)

    private val prefs = ctx.getSharedPreferences("komunikator", Context.MODE_PRIVATE)
    private val random = SecureRandom()
    private val main = Handler(Looper.getMainLooper())
    var myId: String = prefs.getString("cid", null) ?: UUID.randomUUID().toString().replace("-", "").also { prefs.edit().putString("cid", it).apply() }
        private set

    // nadawanie UDP: z gniazda przeglądarki (LanSync) albo własnego (klawiatura, listen())
    var udpOut: ((ByteArray, InetAddress) -> Unit)? = null
    // odbiór (okna ustawia UI): prośba o kontakt, plik, nowa wiadomość
    // prośba o kontakt bez otwartego okna (Velivo w tle): czeka na okno (powiadomienie otwiera komunikator)
    @Volatile var pendingPair: Triple<String, String, (Boolean) -> Unit>? = null
    val noUiPair: (String, String, (Boolean) -> Unit) -> Unit = { d, c, a -> pendingPair = Triple(d, c, a) }
    var askPair: (device: String, code: String, accept: (Boolean) -> Unit) -> Unit = noUiPair
    var askFile: (device: String, name: String, size: Long, code: String, accept: (Boolean) -> Unit) -> Unit = { _, _, _, _, a -> a(false) }
    var saveFile: (name: String, tmp: java.io.File, device: String) -> Unit = { _, t, _ -> t.delete() }
    var onMessage: (contact: Contact, msg: Msg) -> Unit = { _, _ -> }
    // 🎬 Filmy i TV od kontaktu (plik Velivo-Filmy-TV.json z Velivo na Windows); viaNet - przyszło przez internet (wtedy z pytaniem)
    var onMedia: ((device: String, json: JSONObject, viaNet: Boolean) -> Unit)? = null
    var onChange: () -> Unit = {}

    // --- zapis (Keystore) ---
    private fun keystore(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey("velivo-komunikator", null) as? SecretKey)?.let { return it }
        val g = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        g.init(KeyGenParameterSpec.Builder("velivo-komunikator", KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build())
        return g.generateKey()
    }
    private fun seal(s: String): String = runCatching {
        val c = Cipher.getInstance("AES/GCM/NoPadding"); c.init(Cipher.ENCRYPT_MODE, keystore())
        b64(c.iv + c.doFinal(s.toByteArray(Charsets.UTF_8)))
    }.getOrDefault("")
    private fun open(s: String?): String? = runCatching {
        val r = unb64(s ?: return null); if (r.size < 29) return null
        val c = Cipher.getInstance("AES/GCM/NoPadding"); c.init(Cipher.DECRYPT_MODE, keystore(), GCMParameterSpec(128, r, 0, 12))
        String(c.doFinal(r, 12, r.size - 12), Charsets.UTF_8)
    }.getOrNull()

    // zapisane kontakty, których nie dało się odczytać - zostają jako kopia, nie nadpisujemy ich pustą listą
    private val contactsUnreadable = prefs.getString("kontakty", null).let { raw -> raw != null && open(raw) == null }.also { bad ->
        if (bad) prefs.edit().putString("kontakty-kopia-" + System.currentTimeMillis(), prefs.getString("kontakty", null)).apply()
    }
    val contacts: MutableList<Contact> = runCatching {
        val a = JSONArray(open(prefs.getString("kontakty", null)) ?: "[]")
        (0 until a.length()).mapNotNull { i -> runCatching { a.getJSONObject(i) }.getOrNull() }.mapNotNull { runCatching { Contact(it.getString("id"), it.optString("name"), unb64(it.getString("key")), it.optString("addr"), it.optString("dev", it.optString("name")), it.optBoolean("own"), it.optString("relay").ifEmpty { null }) }.getOrNull() }
            .filter { it.key.size == 32 }.toMutableList()
    }.getOrDefault(mutableListOf())

    @Synchronized private fun saveContacts() {
        val a = JSONArray()
        contacts.forEach { a.put(JSONObject().put("id", it.id).put("name", it.name).put("key", b64(it.key)).put("addr", it.addr).put("dev", it.device).put("own", it.own).put("relay", it.relay ?: "")) }
        prefs.edit().putString("kontakty", seal(a.toString())).apply()
    }

    private val chats = HashMap<String, MutableList<Msg>>()
    @Synchronized fun chat(id: String): MutableList<Msg> = chats.getOrPut(id) {
        runCatching {
            val a = JSONArray(open(prefs.getString("rozmowa-$id", null)) ?: "[]")
            (0 until a.length()).map { a.getJSONObject(it) }.map { Msg(it.optBoolean("me"), it.optString("t"), it.optLong("ts"), it.optBoolean("ok", true), it.optString("mid"), it.optBoolean("rd"), it.optBoolean("f"), it.optString("p").ifEmpty { null }) }.toMutableList()
        }.getOrDefault(mutableListOf())
    }
    @Synchronized private fun saveChat(id: String) {
        val l = chat(id); while (l.size > 500) l.removeAt(0)
        val a = JSONArray(); l.forEach { a.put(JSONObject().put("me", it.mine).put("t", it.text).put("ts", it.ts).put("ok", it.ok).put("mid", it.mid).put("rd", it.read).put("f", it.file).put("p", it.path ?: "")) }
        prefs.edit().putString("rozmowa-$id", seal(a.toString())).apply()
    }
    // --- przeniesienie klawiatura -> przeglądarka (telefon z obiema aplikacjami: jeden komunikator, bez dublowania) ---
    @Synchronized fun export(): String = JSONObject().put("cid", myId)
        .put("contacts", JSONArray().also { a -> contacts.forEach { a.put(JSONObject().put("id", it.id).put("name", it.name).put("key", b64(it.key)).put("addr", it.addr).put("dev", it.device).put("own", it.own).put("relay", it.relay ?: "")) } })
        .put("chats", JSONObject().also { o -> contacts.forEach { c -> o.put(c.id, JSONArray().also { a -> chat(c.id).forEach { m -> a.put(JSONObject().put("me", m.mine).put("t", m.text).put("ts", m.ts).put("ok", m.ok).put("mid", m.mid).put("rd", m.read).put("f", m.file).put("p", m.path ?: "")) } }) } })
        .put("unread", JSONObject().also { o -> contacts.forEach { o.put(it.id, unread(it.id)) } }).toString()

    // dane z klawiatury: bez własnych kontaktów przejmujemy też jej identyfikator (inne urządzenia dalej nas znają);
    // rozmowy łączone po identyfikatorze wiadomości - nic się nie powtarza
    fun import(json: String) {
        val o = JSONObject(json)
        synchronized(this) {
            val cid = o.optString("cid")
            if (contacts.isEmpty() && cid.length == 32) { myId = cid; prefs.edit().putString("cid", cid).apply() }
            val a = o.optJSONArray("contacts") ?: JSONArray()
            for (i in 0 until a.length()) {
                val j = a.getJSONObject(i); val key = unb64(j.optString("key")); val id = j.optString("id")
                if (key.size != 32 || id.length != 32) continue
                if (contact(id) == null) contacts.add(Contact(id, j.optString("name"), key, j.optString("addr"), j.optString("dev", j.optString("name")), j.optBoolean("own"), j.optString("relay").ifEmpty { null }))
                val list = chat(id); val have = list.map { it.mid }.toHashSet()
                val ms = o.optJSONObject("chats")?.optJSONArray(id) ?: JSONArray()
                for (k in 0 until ms.length()) ms.getJSONObject(k).let { m ->
                    if (have.add(m.optString("mid"))) list.add(Msg(m.optBoolean("me"), m.optString("t"), m.optLong("ts"), m.optBoolean("ok", true), m.optString("mid"), m.optBoolean("rd"), m.optBoolean("f"), m.optString("p").ifEmpty { null }))
                }
                list.sortBy { it.ts }
                val un = o.optJSONObject("unread")?.optInt(id) ?: 0
                if (un > 0) prefs.edit().putInt("nowe-$id", unread(id) + un).apply()
            }
        }
        saveContacts(); synchronized(this) { contacts.toList() }.forEach { saveChat(it.id) }
        main.post { onChange() }
    }

    // po przeniesieniu do przeglądarki: klawiatura nic nie trzyma i nie nasłuchuje
    fun wipe() {
        close()
        synchronized(this) { contacts.clear(); chats.clear() }
        prefs.edit().clear().putBoolean("przeniesione", true).apply()
        myId = UUID.randomUUID().toString().replace("-", "")
        prefs.edit().putString("cid", myId).putBoolean("przeniesione", true).apply()
    }

    fun unread(id: String) = prefs.getInt("nowe-$id", 0)
    fun markRead(id: String) { prefs.edit().putInt("nowe-$id", 0).apply() }
    fun remove(c: Contact) {
        synchronized(this) { contacts.removeAll { it.id == c.id }; chats.remove(c.id) }
        saveContacts(); prefs.edit().remove("rozmowa-${c.id}").remove("nowe-${c.id}").apply(); onChange()
    }
    // własna nazwa kontaktu; pusta albo taka jak urządzenia - znów nazwa urządzenia (aktualizowana z sieci)
    fun rename(c: Contact, name: String) {
        val n = name.trim().take(64)
        if (n.isEmpty() || n == c.device) { c.own = false; c.name = c.device } else { c.own = true; c.name = n }
        saveContacts(); onChange()
    }

    // --- UDP ---
    private fun packet(t: String) = JSONObject().put("t", t).put("id", myId).put("device", deviceName()).put("ts", System.currentTimeMillis()).put("kind", "phone")
    private fun sendUdp(p: JSONObject, to: InetAddress?) {
        up?.let { u -> upSend(u, p); upMore.values.forEach { m -> upSend(m, p) } }   // 🌍 poza domem - przez każdego pośrednika, a sieć domowa i tak dalej (w domu nic nie znika)
        val d = p.toString().toByteArray(Charsets.UTF_8)
        val own = udp
        val out: (ByteArray, InetAddress) -> Unit = if (own != null) { b, to -> own.send(DatagramPacket(b, b.size, to, PORT)) } else udpOut ?: return
        if (to != null) runCatching { out(d, to) } else for (t in broadcastTargets()) runCatching { out(d, t) }
    }

    private val found = java.util.concurrent.ConcurrentHashMap<String, Peer>()
    private val acks = java.util.concurrent.ConcurrentHashMap<String, java.util.concurrent.CountDownLatch>()
    private val seen = java.util.Collections.synchronizedSet(LinkedHashSet<String>())

    // pakiet "p2p-*" z sieci (z LanSync albo z listen()); true - obsłużony
    // viaRelay - pakiet przyszedł przez pośrednika (poza domem), nie z sieci domowej
    fun handleUdp(p: JSONObject, sender: InetAddress?, viaRelay: Boolean = false): Boolean {
        val t = p.optString("t"); if (!t.startsWith("p2p-")) return false
        val from = p.optString("id")
        if (from == myId || from.length != 32 || sender == null) return true
        if (viaRelay) viaNet[from] = true else { lanSeen[from] = System.currentTimeMillis(); viaNet.remove(from) }
        if (Math.abs(p.optLong("ts") - System.currentTimeMillis()) > 600000) return true
        if (t == "p2p-bye") { contact(from)?.let { c -> if (lastSeen.remove(c.id) != null) main.post { onChange() } }; return true }   // zamknął Velivo - od razu poza siecią
        if (!viaRelay && (t == "p2p-here" || t == "p2p-who") && contact(from) == null) notifyNewDevice(from, clean(p.optString("device")))
        contact(from)?.let { c ->
            val was = online(c.id); lastSeen[c.id] = System.currentTimeMillis(); seenAt[c.id] = System.currentTimeMillis()
            if (t == "p2p-here" && !viaRelay && c.addr != sender.hostAddress) { c.addr = sender.hostAddress ?: c.addr; saveContacts() }   // aktualny adres
            // prawdziwa nazwa urządzenia (zmieniona na tamtym telefonie / komputerze); własna nazwa kontaktu zostaje
            val real = clean(p.optString("device"))
            if (real.isNotEmpty() && real != c.device) { c.device = real; if (!c.own) c.name = real; saveContacts(); main.post { onChange() } }
            // 🌍 adres pośrednika kontaktu (np. NUCBOX z No-IP) - poza domem rozmowy idą przez niego
            val rl = p.optString("relay")
            if (t == "p2p-here" && rl.isNotEmpty() && rl.length < 200 && ':' in rl && rl != c.relay) { c.relay = rl; saveContacts() }
            if (!was) main.post { onChange() }
        }
        if (t != "p2p-ack" && t != "p2p-msg") contact(from)?.let { c -> Thread { resend(c) }.start() }
        when (t) {
            "p2p-who" -> sendUdp(packet("p2p-here"), sender)
            "p2p-here" -> found[from] = Peer(from, clean(p.optString("device")), sender)
            "p2p-msg" -> message(p, sender)
            "p2p-ack" -> {
                val c = contact(from) ?: return true; val mid = p.optString("mid")
                if (!MessageDigest.isEqual(unb64(p.optString("mac")), hmac(c.key, "ack\n$mid".toByteArray()))) return true
                if (!sender.isLoopbackAddress && c.addr != sender.hostAddress) { c.addr = sender.hostAddress ?: c.addr; saveContacts() }
                acks[mid]?.countDown()
            }
            // ✓✓ odbiorca przeczytał nasze wiadomości
            "p2p-read" -> {
                val c = contact(from) ?: return true; val mids = p.optString("mids")
                if (p.optString("to") != myId || !MessageDigest.isEqual(unb64(p.optString("mac")), hmac(c.key, "read\n$mids".toByteArray()))) return true
                val set = mids.split(',').toHashSet(); var any = false
                synchronized(this) { chat(c.id).forEach { if (it.mine && !it.read && it.mid in set) { it.read = true; it.ok = true; any = true } } }
                if (any) { saveChat(c.id); main.post { onChange() } }
            }
        }
        return true
    }

    // rozmowa na ekranie: „przeczytane” do nadawcy (najwyżej 60 naraz)
    fun sendRead(c: Contact) {
        val mids = synchronized(this) { chat(c.id).filter { !it.mine && !it.read }.onEach { it.read = true }.map { it.mid }.filter { it.length == 32 } }
        if (mids.isEmpty()) return
        saveChat(c.id)
        Thread {
            for (part in mids.chunked(60)) {
                val j = part.joinToString(",")
                val p = packet("p2p-read").put("to", c.id).put("mids", j).put("mac", b64(hmac(c.key, "read\n$j".toByteArray())))
                val to = runCatching { InetAddress.getByName(c.addr.ifEmpty { null } ?: throw Exception()) }.getOrNull()
                sendUdp(p, to)
            }
        }.start()
    }

    // usunięcie wiadomości tylko u siebie
    private val pl get() = Locale.getDefault().language == "pl"
    private fun sizeText(n: Long) = when { n >= 1L shl 30 -> String.format(Locale.ROOT, "%.1f GB", n / 1073741824.0); n >= 1L shl 20 -> String.format(Locale.ROOT, "%.1f MB", n / 1048576.0); else -> "${maxOf(1, n / 1024)} KB" }

    fun deleteLocal(c: Contact, m: Msg) {
        synchronized(this) { chat(c.id).remove(m) }; saveChat(c.id)
        m.path?.let { p -> if (p.startsWith(mediaDir.path)) java.io.File(p).delete() }   // kopia pliku w Velivo (nie ta z Pobranych / Galerii)
        main.post { onChange() }
    }

    // „jestem w sieci” - po powrocie do domowego Wi-Fi kontakty od razu dosyłają zaległe wiadomości
    fun announce() = Thread { repeat(3) { sendUdp(packet("p2p-here"), null); Thread.sleep(1500) }; contacts.toList().forEach { resend(it) } }.start()

    // „kto jest w sieci?” - kontakty odpowiadają, status 🟢 od razu prawdziwy
    fun ping() = Thread { sendUdp(packet("p2p-here"), null); repeat(2) { sendUdp(packet("p2p-who"), null); Thread.sleep(700) } }.start()

    @Synchronized fun contact(id: String) = contacts.firstOrNull { it.id == id }

    // urządzenia z Velivo (telefony i komputery) w tej sieci - ok. 2,5 s
    fun find(): List<Peer> {
        found.clear()
        repeat(3) { sendUdp(packet("p2p-who"), null); Thread.sleep(800) }
        return found.values.sortedBy { it.name.lowercase() }
    }

    private fun msgAd(from: String, to: String, mid: String, ts: Long) = "p2p-msg\n$from\n$to\n$mid\n$ts".toByteArray(Charsets.UTF_8)

    private fun message(p: JSONObject, sender: InetAddress) {
        if (p.optString("to") != myId) return
        val c = contact(p.optString("id")) ?: return
        val mid = p.optString("mid"); if (mid.length != 32) return
        val n = unb64(p.optString("n")); if (n.size != 12) return
        val text = runCatching {
            val ci = Cipher.getInstance("AES/GCM/NoPadding")
            ci.init(Cipher.DECRYPT_MODE, SecretKeySpec(c.key, "AES"), GCMParameterSpec(128, n))
            ci.updateAAD(msgAd(c.id, myId, mid, p.optLong("ts")))
            String(ci.doFinal(unb64(p.optString("c"))), Charsets.UTF_8)
        }.getOrNull() ?: return
        sendUdp(packet("p2p-ack").put("to", c.id).put("mid", mid).put("mac", b64(hmac(c.key, "ack\n$mid".toByteArray()))), sender)
        if (!seen.add(mid)) return   // powtórzenie (np. potwierdzenie nie doszło) - tylko ponowne ack
        if (seen.size > 2000) synchronized(seen) { seen.iterator().let { it.next(); it.remove() } }
        if (!sender.isLoopbackAddress && c.addr != sender.hostAddress) { c.addr = sender.hostAddress ?: c.addr; saveContacts() }
        if (text.startsWith(CALL)) { val o = runCatching { JSONObject(text.substring(CALL.length)) }.getOrNull() ?: return; onCallSignal(c, o); return }
        val m = Msg(false, text.take(MAX_TEXT), p.optLong("ts"), true, mid)
        synchronized(this) { chat(c.id).add(m) }
        saveChat(c.id)
        prefs.edit().putInt("nowe-${c.id}", unread(c.id) + 1).apply()
        notifyMessage(c)
        main.post { onMessage(c, m); onChange() }
        Thread { resend(c) }.start()   // odezwał się - może czekają nasze niedoręczone
    }

    // wysłanie (w tle): najpierw na znany adres, potem do całej sieci; 3 próby
    fun send(c: Contact, text: String): Msg {
        val mid = UUID.randomUUID().toString().replace("-", "")
        val m = Msg(true, text.take(MAX_TEXT), System.currentTimeMillis(), false, mid)
        synchronized(this) { chat(c.id).add(m) }
        saveChat(c.id)
        deliver(c, m)
        return m
    }

    // 📹 rozmowa wideo: sygnały (dzwonię, odbieram, opis połączenia) jako zaszyfrowane wiadomości - poza czatem
    var onCall: ((Contact, JSONObject) -> Unit)? = null
    var ringing: Pair<String, Long>? = null   // kto dzwoni (z czasu, gdy Velivo było w tle)
    // poza domem: przekaźnik TURN na domowym komputerze (pośredniku) - prywatnie, bez serwerów firm trzecich
    fun turnIce(): String {
        val r = upRelay; val hp = r?.relay
        if (up == null || r == null || hp == null) return "[]"
        return JSONArray().put(JSONObject().put("urls", "turn:$hp?transport=tcp").put("username", myId).put("credential", b64(hmac(r.key, "velivo-turn".toByteArray(Charsets.UTF_8))))).toString()
    }
    fun sendCall(c: Contact, o: JSONObject) = deliver(c, Msg(true, CALL + o, System.currentTimeMillis(), false, UUID.randomUUID().toString().replace("-", "")))
    private fun onCallSignal(c: Contact, o: JSONObject) {
        val nm = ctx.getSystemService(NotificationManager::class.java)
        when (o.optString("k")) {
            "ring" -> { ringing = c.id to System.currentTimeMillis(); if (nm != null) notifyCall(nm, c) }
            "end" -> { if (ringing?.first == c.id) ringing = null; nm?.cancel("rozmowa".hashCode()) }
        }
        main.post { onCall?.invoke(c, o) }
    }
    private fun notifyCall(nm: NotificationManager, c: Contact) {
        channels(nm)
        val pl = Locale.getDefault().language == "pl"
        val pi = PendingIntent.getActivity(ctx, "rozmowa".hashCode(), openIntent(c.id), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        runCatching {
            // jak w WhatsApp: dzwonek w kółko i od razu okno „Odbierz / Odrzuć” na cały ekran (także na zablokowanym telefonie)
            nm.createNotificationChannel(NotificationChannel("rozmowy", if (pl) "Rozmowy wideo" else "Video calls", NotificationManager.IMPORTANCE_HIGH).apply {
                setSound(android.media.RingtoneManager.getDefaultUri(android.media.RingtoneManager.TYPE_RINGTONE),
                    android.media.AudioAttributes.Builder().setUsage(android.media.AudioAttributes.USAGE_NOTIFICATION_RINGTONE).build())
            })
            val n = Notification.Builder(ctx, "rozmowy").setSmallIcon(android.R.drawable.stat_notify_chat)
                .setContentTitle("📹 " + c.name).setContentText(if (pl) "Dzwoni – stuknij, by odebrać" else "Calling – tap to answer")
                .setCategory(Notification.CATEGORY_CALL).setContentIntent(pi).setFullScreenIntent(pi, true).setAutoCancel(true).setTimeoutAfter(45000).build()
            n.flags = n.flags or Notification.FLAG_INSISTENT
            nm.notify("rozmowa".hashCode(), n)
        }
    }

    private val inFlight = java.util.Collections.synchronizedSet(HashSet<String>())
    private fun deliver(c: Contact, m: Msg) {
        if (!inFlight.add(m.mid)) return
        val mid = m.mid
        Thread {
            val n = ByteArray(12).also { random.nextBytes(it) }
            val p = packet("p2p-msg"); p.put("ts", m.ts).put("to", c.id).put("mid", mid).put("n", b64(n))
            val ci = Cipher.getInstance("AES/GCM/NoPadding")
            ci.init(Cipher.ENCRYPT_MODE, SecretKeySpec(c.key, "AES"), GCMParameterSpec(128, n))
            ci.updateAAD(msgAd(myId, c.id, mid, m.ts))
            p.put("c", b64(ci.doFinal(m.text.toByteArray(Charsets.UTF_8))))
            val latch = java.util.concurrent.CountDownLatch(1); acks[mid] = latch
            val known = runCatching { InetAddress.getByName(c.addr.ifEmpty { null } ?: throw Exception()) }.getOrNull()?.takeIf { !it.isLoopbackAddress }
            for (i in 0 until 4) {
                sendUdp(p, if (i % 2 == 0 && known != null) known else null)
                if (latch.await(1500, java.util.concurrent.TimeUnit.MILLISECONDS)) { m.ok = true; break }
            }
            acks.remove(mid); inFlight.remove(mid); saveChat(c.id)
            main.post { onChange() }
        }.start()
    }

    // --- TCP (kontakt i pliki) ---
    // połączenie: TCP w sieci domowej albo szyfrowany tunel przez pośrednika
    class Conn(val inp: DataInputStream, val out: java.io.OutputStream, val addr: InetAddress?, private val sock: java.net.Socket) : java.io.Closeable {
        fun timeout(ms: Int) { runCatching { sock.soTimeout = ms } }
        override fun close() { runCatching { out.flush() }; runCatching { sock.close() } }
    }

    fun handleTcp(h: JSONObject, inp: DataInputStream, sock: java.net.Socket): Boolean = handleTcp(h, Conn(inp, sock.getOutputStream(), sock.inetAddress, sock))
    fun handleTcp(h: JSONObject, c: Conn): Boolean = when (h.optString("Type")) {
        "p2p-pair" -> { runCatching { receivePair(h, c) }; true }
        "p2p-file" -> { runCatching { receiveFile(h, c) }; true }
        else -> false
    }

    private fun ecPair() = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    private fun agree(priv: KeyPair, other: ByteArray, label: String, pubA: ByteArray, pubB: ByteArray): ByteArray {
        val ka = KeyAgreement.getInstance("ECDH"); ka.init(priv.private)
        ka.doPhase(KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(other)), true)
        return MessageDigest.getInstance("SHA-256").digest(ka.generateSecret() + label.toByteArray() + pubA + pubB)
    }
    private fun ask(block: ((Boolean) -> Unit) -> Unit): Boolean {
        val latch = java.util.concurrent.CountDownLatch(1); var ok = false
        main.post { block { yes -> ok = yes; latch.countDown() } }
        return latch.await(3, java.util.concurrent.TimeUnit.MINUTES) && ok
    }
    private fun writeBlock(out: java.io.OutputStream, b: ByteArray) { out.write(int4(b.size)); out.write(b); out.flush() }
    private fun readBlock(inp: DataInputStream, max: Int): ByteArray? { val l = readInt4(inp); if (l <= 0 || l > max) return null; return ByteArray(l).also { inp.readFully(it) } }

    // auto - nowe urządzenie z zaproszeniem dołącza do pośrednika (tylko Windows jest pośrednikiem; tu zawsze z pytaniem)
    private fun receivePair(h: JSONObject, cn: Conn, auto: Boolean = false) {
        val inp = cn.inp
        val other = h.optString("Id"); if (other.length != 32 || other == myId) return
        val pubA = unb64(h.optString("Pub")); if (pubA.isEmpty() || pubA.size > 200) return
        val dev = clean(h.optString("Device"))
        val mine = ecPair(); val pubB = mine.public.encoded
        val out = cn.out
        writeBlock(out, pubB)
        val key = agree(mine, pubA, "velivo-czat", pubA, pubB)
        val invited = h.optBoolean("Invited")
        val label = if (invited) dev + (if (Locale.getDefault().language == "pl") " (zaproszenie przez internet)" else " (internet invitation)") else dev
        if (!auto) notifyPair(label)   // prośba od razu widoczna i słyszalna, także gdy Velivo w tle
        val ok = auto || ask { a -> askPair(label, code(key), { yes -> clearNotification("para"); a(yes) }) }
        out.write(if (ok) 1 else 0); out.flush()
        if (!ok) return
        writeBlock(out, JSONObject().put("Id", myId).put("Device", deviceName()).toString().toByteArray(Charsets.UTF_8))
        addContact(Contact(other, dev, key, cn.addr?.hostAddress ?: ""))
    }

    private fun addContact(c: Contact) {
        synchronized(this) {
            contact(c.id)?.takeIf { it.own }?.let { old -> c.own = true; c.name = old.name }   // ponowne dodanie - własna nazwa zostaje
            contacts.removeAll { it.id == c.id }; contacts.add(c)
        }
        saveContacts(); main.post { onChange() }
    }

    // dodanie kontaktu: showCode - kod do porównania z drugim ekranem; wynik "ok", "odrzucono" albo opis błędu
    fun pair(peer: Peer, showCode: (String) -> Unit): String = runCatching {
        val s = java.net.Socket(); s.connect(InetSocketAddress(peer.addr, PORT), 5000)
        pairOn(Conn(DataInputStream(s.getInputStream()), s.getOutputStream(), peer.addr, s), peer.id, peer.name, null, false, showCode)
    }.getOrElse { it.message ?: it.javaClass.simpleName }

    // dodanie kontaktu na gotowym połączeniu (sieć domowa albo tunel przez pośrednika z zaproszeniem)
    private fun pairOn(cn: Conn, id: String, name: String, relay: String?, invited: Boolean, showCode: (String) -> Unit): String = cn.use { s ->
        s.timeout(15000)
        val mine = ecPair(); val pubA = mine.public.encoded
        val hj = JSONObject().put("Type", "p2p-pair").put("Id", myId).put("Device", deviceName()).put("Pub", b64(pubA)); if (invited) hj.put("Invited", true)
        writeBlock(s.out, hj.toString().toByteArray(Charsets.UTF_8))
        val pubB = readBlock(s.inp, 200) ?: return@use "błąd"
        val key = agree(mine, pubB, "velivo-czat", pubA, pubB)
        showCode(code(key))
        s.timeout(180000)
        if (s.inp.read() != 1) return@use "odrzucono"
        val info = JSONObject(String(readBlock(s.inp, 4096) ?: return@use "błąd", Charsets.UTF_8))
        if (info.optString("Id") != id) return@use "błąd"
        addContact(Contact(id, clean(info.optString("Device")).ifEmpty { name }, key, s.addr?.hostAddress ?: "", relay = relay))
        "ok"
    }

    // --- ✉ zaproszenia przez internet (ten sam kod VELIVO1 co Velivo na Windows) ---
    // Pośrednikiem jest komputer z Velivo; telefon zgłasza mu zaproszenie (klucz zaszyfrowany kluczem kontaktu z pośrednikiem).
    fun createInvite(): String? {
        val relay = synchronized(this) { contacts.firstOrNull { it.relay != null } } ?: return null
        val token = ByteArray(16).also { random.nextBytes(it) }; val k = MessageDigest.getInstance("SHA-256").digest(token)
        val exp = System.currentTimeMillis() + 7L * 86400000
        val n = ByteArray(12).also { random.nextBytes(it) }
        val c = Cipher.getInstance("AES/GCM/NoPadding"); c.init(Cipher.ENCRYPT_MODE, SecretKeySpec(relay.key, "AES"), GCMParameterSpec(128, n))
        c.updateAAD("velivo-invite\n$exp".toByteArray(Charsets.US_ASCII))
        val p = packet("p2p-invite").put("to", relay.id).put("n", b64(n)).put("ek", b64(c.doFinal(k))).put("exp", exp)
        val known = runCatching { InetAddress.getByName(relay.addr.ifEmpty { null } ?: throw Exception()) }.getOrNull()
        Thread { repeat(3) { i -> sendUdp(p, if (i == 0) known else null); Thread.sleep(300) } }.start()
        val code = JSONObject().put("h", relay.relay).put("r", relay.id).put("t", b64(token)).put("i", myId).put("n", deviceName()).toString()
        return "VELIVO1:" + Base64.encodeToString(code.toByteArray(Charsets.UTF_8), Base64.NO_WRAP or Base64.NO_PADDING or Base64.URL_SAFE)
    }

    // dołączenie z kodem: do pośrednika (bez pytania - zaproszenie), potem prośba o kontakt do zapraszającego; wynik "ok" albo opis
    fun join(code: String, showCode: (String) -> Unit): String {
        val pl = Locale.getDefault().language == "pl"
        val bad = if (pl) "To nie jest kod zaproszenia Velivo" else "This is not a Velivo invitation code"
        val j = runCatching {
            val at = code.indexOf("VELIVO1:", ignoreCase = true); if (at < 0) return bad
            val b = code.substring(at + 8).trim().split(' ', '\n', '\r')[0]
            JSONObject(String(Base64.decode(b, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP), Charsets.UTF_8))
        }.getOrElse { return bad }
        val host = j.optString("h"); val relayId = j.optString("r"); val inviter = j.optString("i"); val name = clean(j.optString("n"))
        val token = unb64(j.optString("t"))
        if (host.isEmpty() || relayId.length != 32 || inviter.length != 32 || token.size != 16) return bad
        if (inviter == myId) return if (pl) "To Twoje własne zaproszenie" else "This is your own invitation"
        val k = MessageDigest.getInstance("SHA-256").digest(token)
        val invId = k.copyOf(8).joinToString("") { "%02X".format(it) }
        // 1) pośrednik - nowy kontakt bez pytania (zaproszenie); z nim rozmowy poza domem
        val f = relayConnect(Contact(relayId, "Velivo", k, "", relay = host), "invite", tunnel = invId)
            ?: return if (pl) "Nie można połączyć z pośrednikiem – zaproszenie wygasło albo dom jest niedostępny" else "Cannot connect to the relay – the invitation expired or the home is unreachable"
        val r1 = pairOn(Conn(DataInputStream(f.input), f.output, null, f.sock), relayId, "Velivo", host, true) { }
        if (r1 != "ok") return r1
        if (inviter == relayId) return "ok"
        // 2) zapraszający - prośba z kodem do porównania, zgoda po jego stronie
        val relay = contact(relayId) ?: return "błąd"
        val f2 = relayConnect(relay, "tunnel", to = inviter) ?: return if (pl) "Nie można połączyć z pośrednikiem" else "Cannot connect to the relay"
        val r2 = pairOn(Conn(DataInputStream(f2.input), f2.output, null, f2.sock), inviter, name, null, true, showCode)
        return if (r2 == "ok" || r2 == "odrzucono") r2 else (if (pl) "Zapraszający jest teraz niedostępny – spróbuj, gdy będzie w sieci ($r2)" else "The inviter is unavailable now – try when they are online ($r2)")
    }

    var tmpDir: java.io.File = ctx.cacheDir

    private fun fileAd(h: JSONObject) = listOf(h.optString("Type"), h.optString("Id"), h.optString("Device"), h.optString("Hash"),
        h.optLong("Size").toString(), h.optString("Name"), h.optString("Pub"), h.optString("Nonce")).joinToString("\n").toByteArray(Charsets.UTF_8)
    private fun chunkNonce(base: ByteArray, i: Int) = base.copyOf().also { n -> for (b in 0..3) n[8 + b] = (n[8 + b].toInt() xor (i shr (24 - 8 * b))).toByte() }
    private fun chunkAad(ad: ByteArray, i: Int, last: Boolean) = ad + ("\n$i\n" + (if (last) "1" else "0")).toByteArray(Charsets.UTF_8)
    private fun chunks(size: Long) = maxOf(1L, (size + CHUNK - 1) / CHUNK).toInt()

    private fun receiveFile(h: JSONObject, cn: Conn) {
        val inp = cn.inp
        val size = h.optLong("Size")
        if (size < 0 || size > (1L shl 40) || h.optString("Id").length != 32 || h.optString("Id") == myId) return
        val pubA = unb64(h.optString("Pub")); val base = unb64(h.optString("Nonce"))
        if (pubA.isEmpty() || pubA.size > 200 || base.size != 12) return
        val name = h.optString("Name").replace('\\', '/').substringAfterLast('/').filter { !it.isISOControl() && it !in "<>:\"|?*" }
            .trim().trimStart('.').ifEmpty { "plik" }.takeLast(120)
        val dev = clean(h.optString("Device"))
        val mine = ecPair(); val pubB = mine.public.encoded
        val out = cn.out
        writeBlock(out, pubB)   // nadawca liczy ten sam kod i pokazuje go u siebie
        val key = agree(mine, pubA, "velivo-p2p", pubA, pubB)
        val ad = fileAd(h)
        // od kontaktu (podpis kluczem kontaktu) - bez kodu, jak załącznik w rozmowie; zgoda odbiorcy zawsze
        val from = contact(h.optString("Id"))?.takeIf { c -> MessageDigest.isEqual(unb64(h.optString("Mac")), hmac(c.key, ad)) }
        val media = from != null && onMedia != null && name.startsWith("Velivo-Filmy-TV") && name.endsWith(".json") && size < 64L * 1024 * 1024
        val auto = media || (from != null && autoImages && size <= 2L * 1024 * 1024 * 1024)   // zdjęcie / plik od kontaktu - od razu do rozmowy (jak w innych komunikatorach)
        val ok = auto || ask { a -> askFile(from?.name ?: dev, name, size, if (from != null) "" else code(key), a) }
        out.write(if (ok) 1 else 0); out.flush()
        if (!ok) return
        cn.timeout(60000)
        val tmp = java.io.File(tmpDir, "odbior-" + UUID.randomUUID() + ".part")
        var good = false
        try {
            val md = MessageDigest.getInstance("SHA-256")
            java.io.FileOutputStream(tmp).use { fo ->
                val n = chunks(size); var got = 0L
                for (i in 0 until n) {
                    val l = readInt4(inp); if (l < 16 || l > CHUNK + 16) return
                    val enc = ByteArray(l).also { b -> inp.readFully(b) }
                    val c = Cipher.getInstance("AES/GCM/NoPadding")
                    c.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, chunkNonce(base, i)))
                    c.updateAAD(chunkAad(ad, i, i == n - 1))
                    val plain = c.doFinal(enc)
                    got += plain.size; if (got > size) return
                    md.update(plain); fo.write(plain)
                }
                if (got != size) return
            }
            if (!hex(md.digest()).equals(h.optString("Hash"), ignoreCase = true)) return
            good = true
            var saved: String? = null
            if (media) {   // ustawienia Filmów i TV - do Velivo (w domu od razu, przez internet z pytaniem), nie do Pobranych
                val j = runCatching { JSONObject(tmp.readText(Charsets.UTF_8)) }.getOrNull(); tmp.delete()
                out.write(hmac(key, ("file-ack\n" + h.optString("Hash")).toByteArray(Charsets.UTF_8))); out.flush()
                if (j != null) { val via = cn.addr == null; main.post { onMedia?.invoke(from!!.name, j, via) } }
                note(from!!, false, "🎬 " + (if (Locale.getDefault().language == "pl") "Filmy i TV" else "Films and TV"), h.optString("Mid"))
                return
            }
            if (auto) {   // w Velivo (w rozmowie); do Galerii / Pobranych - przytrzymanie
                var f = java.io.File(mediaDir, name); var i = 2
                while (f.exists()) f = java.io.File(mediaDir, name.substringBeforeLast('.') + " ($i)." + name.substringAfterLast('.')).also { i++ }
                if (tmp.renameTo(f) || runCatching { tmp.copyTo(f); tmp.delete() }.isSuccess) saved = f.path
            } else saveFile(name, tmp, dev)
            out.write(hmac(key, ("file-ack\n" + h.optString("Hash")).toByteArray(Charsets.UTF_8))); out.flush()
            if (from != null) {
                note(from, false, (if (isImage(name)) "🖼 " else "📎 ") + "$name · " + sizeText(size), h.optString("Mid"), saved)
                // plik / zdjęcie jak wiadomość: nieprzeczytane i powiadomienie (✓ u nadawcy, ✓✓ po otwarciu rozmowy)
                if (chatOpen != from.id) prefs.edit().putInt("nowe-${from.id}", unread(from.id) + 1).apply()
                notifyMessage(from)
            }
        } finally { if (!good) tmp.delete() }
    }

    // plik na inny telefon: open() - strumień od początku (dwa razy: suma, potem wysyłka)
    fun sendFile(target: InetAddress, name: String, size: Long, open: () -> java.io.InputStream, showCode: (String) -> Unit, to: Contact? = null): String {
        val md = MessageDigest.getInstance("SHA-256")
        open().use { s -> val b = ByteArray(1 shl 16); while (true) { val r = s.read(b); if (r < 0) break; md.update(b, 0, r) } }
        val hash = hex(md.digest())
        val mine = ecPair(); val pubA = mine.public.encoded
        val base = ByteArray(12).also { random.nextBytes(it) }
        val h = JSONObject().put("Type", "p2p-file").put("Id", myId).put("Device", deviceName()).put("Hash", hash).put("Size", size)
            .put("Name", name).put("Pub", b64(pubA)).put("Nonce", b64(base))
        val ad = fileAd(h)
        val mid = UUID.randomUUID().toString().replace("-", ""); h.put("Mid", mid)   // poza podpisem - tylko do ✓✓ w rozmowie
        if (to != null) h.put("Mac", b64(hmac(to.key, ad)))   // kontakt rozpozna nadawcę bez kodu
        if (to != null) h.put("To", to.id)   // pośrednik wie, komu przekazać (poza podpisem)
        // wpis w rozmowie od razu („…”): „przeczytane” od odbiorcy może przyjść, zanim tu skończymy
        // wysłane zdjęcie: mała kopia w Velivo (miniatura w rozmowie)
        val thumb = if (to != null && isImage(name)) runCatching {
            val o = android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds = true }
            open().use { android.graphics.BitmapFactory.decodeStream(it, null, o) }
            var smp = 1; while (maxOf(o.outWidth, o.outHeight) / smp > 1280) smp *= 2
            val bmp = open().use { android.graphics.BitmapFactory.decodeStream(it, null, android.graphics.BitmapFactory.Options().apply { inSampleSize = smp }) }!!
            val f = java.io.File(mediaDir, "wyslane-$mid.jpg"); f.outputStream().use { bmp.compress(android.graphics.Bitmap.CompressFormat.JPEG, 85, it) }; f.path
        }.getOrNull() else null
        val pend = to?.let { c -> Msg(true, (if (thumb != null) "🖼 " else "📎 ") + "$name · " + sizeText(size), System.currentTimeMillis(), false, mid, file = true, path = thumb).also { m -> synchronized(this) { chat(c.id).add(m) }; saveChat(c.id); main.post { onChange() } } }
        return runCatching {
            connect(target, to?.id).use { s ->   // poza domem - tunel przez pośrednika
                s.timeout(15000)
                val out = java.io.BufferedOutputStream(s.out, 1 shl 16)
                writeBlock(out, h.toString().toByteArray(Charsets.UTF_8))
                val inp = s.inp
                val pubB = readBlock(inp, 200) ?: return@use "błąd"
                val key = agree(mine, pubB, "velivo-p2p", pubA, pubB)
                showCode(code(key))
                s.timeout(180000)
                if (inp.read() != 1) return@use "odrzucono"
                s.timeout(300000)
                val n = chunks(size); var sent = 0L
                open().use { src ->
                    val buf = ByteArray(CHUNK)
                    for (i in 0 until n) {
                        var len = 0
                        while (len < CHUNK) { val r = src.read(buf, len, CHUNK - len); if (r < 0) break; len += r }
                        sent += len
                        val c = Cipher.getInstance("AES/GCM/NoPadding")
                        c.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, chunkNonce(base, i)))
                        c.updateAAD(chunkAad(ad, i, i == n - 1))
                        writeBlock(out, c.doFinal(buf, 0, len))
                    }
                }
                if (sent != size) return@use "plik zmienił się w trakcie"
                val ack = ByteArray(32).also { inp.readFully(it) }
                if (MessageDigest.isEqual(ack, hmac(key, "file-ack\n$hash".toByteArray(Charsets.UTF_8)))) "ok" else "błąd potwierdzenia"
            }
        }.getOrElse { it.message ?: it.javaClass.simpleName }.also { r ->
            if (to != null && pend != null) {
                if (r == "ok") { pend.ok = true; pend.text += if (pl) " – odebrano" else " – received" } else { synchronized(this) { chat(to.id).remove(pend) }; pend.path?.let { java.io.File(it).delete() } }   // odrzucony / błąd - bez wpisu
                saveChat(to.id); main.post { onChange() }
            }
        }
    }

    // plik w historii rozmowy (jak załącznik w innych komunikatorach)
    // plik w rozmowie: ten sam identyfikator u nadawcy i odbiorcy (Mid z nagłówka) - „przeczytane” (✓✓) jak przy wiadomościach
    private fun note(c: Contact, mine: Boolean, text: String, mid: String? = null, path: String? = null) {
        val id = mid?.takeIf { it.length == 32 && synchronized(this) { chat(c.id).none { m -> m.mid == it } } } ?: UUID.randomUUID().toString().replace("-", "")
        val m = Msg(mine, text, System.currentTimeMillis(), true, id, file = true, path = path)
        synchronized(this) { chat(c.id).add(m) }
        saveChat(c.id)
        main.post { onChange() }
    }

    // --- 🌍 poza domem: połączenie z pośrednikiem (komputer z Velivo, zawsze włączony, adres No-IP i własny port) ---
    // Ten sam protokół co Velivo na Windows (KomunikatorPosrednik.cs): podpis kluczem kontaktu, klucz sesji, ramki AES-GCM;
    // pakiety p2p-* jak w sieci domowej, pliki tunelem. W domu (pośrednik widoczny w sieci domowej) - jak dotąd.
    private val lanSeen = java.util.concurrent.ConcurrentHashMap<String, Long>()
    private val viaNet = java.util.concurrent.ConcurrentHashMap<String, Boolean>()
    fun viaInternet(id: String) = up != null || viaNet.containsKey(id)
    val away get() = up != null              // 🌍 ten telefon poza domem (połączony przez pośrednika)
    val awayVia get() = upRelay?.name
    @Volatile private var up: Frames? = null
    @Volatile private var upRelay: Contact? = null
    private val started = System.currentTimeMillis()
    // 🌍 nazwa domu w internecie (np. mojdom.ddns.net z adresu Jellyfin poza domem) - gdy dom dostanie nowy adres IP, pośrednik i tak się znajdzie
    var homeName: String
        get() = prefs.getString("dom-nazwa", "") ?: ""
        set(v) { if (v != homeName) prefs.edit().putString("dom-nazwa", v).apply() }
    @Volatile var upForce = false   // po dołączeniu z zaproszeniem - od razu przez internet
    private val upWriter = java.util.concurrent.Executors.newSingleThreadExecutor()

    // [4 bajty długości][nonce 12][dane][tag 16] - AES-GCM kluczem sesji
    class Frames(val sock: java.net.Socket, key: ByteArray) : java.io.Closeable {
        private val k = SecretKeySpec(key, "AES"); private val rnd = SecureRandom()
        private val rawIn = DataInputStream(java.io.BufferedInputStream(sock.getInputStream(), 1 shl 16)); private val rawOut = sock.getOutputStream()
        fun readFrame(): ByteArray {
            val l = readInt4(rawIn); if (l < 28 || l > (2 shl 20)) throw java.io.IOException("ramka")
            val f = ByteArray(l).also { rawIn.readFully(it) }
            val c = Cipher.getInstance("AES/GCM/NoPadding"); c.init(Cipher.DECRYPT_MODE, k, GCMParameterSpec(128, f, 0, 12))
            return c.doFinal(f, 12, l - 12)
        }
        @Synchronized fun writeFrame(d: ByteArray, off: Int = 0, len: Int = d.size) {
            val n = ByteArray(12).also { rnd.nextBytes(it) }
            val c = Cipher.getInstance("AES/GCM/NoPadding"); c.init(Cipher.ENCRYPT_MODE, k, GCMParameterSpec(128, n))
            val e = c.doFinal(d, off, len)
            rawOut.write(int4(12 + e.size)); rawOut.write(n); rawOut.write(e); rawOut.flush()
        }
        val input = object : java.io.InputStream() {
            private var b = ByteArray(0); private var p = 0
            override fun read(): Int { val one = ByteArray(1); return if (read(one, 0, 1) <= 0) -1 else one[0].toInt() and 0xff }
            override fun read(buf: ByteArray, off: Int, len: Int): Int {
                if (p >= b.size) { b = runCatching { readFrame() }.getOrElse { return -1 }; p = 0; if (b.isEmpty()) return -1 }
                val n = minOf(len, b.size - p); System.arraycopy(b, p, buf, off, n); p += n; return n
            }
        }
        val output = object : java.io.OutputStream() {
            private val bo = java.io.ByteArrayOutputStream()
            override fun write(x: Int) { bo.write(x); if (bo.size() >= 1 shl 20) flush() }
            // duży zapis - po kawałkach 1 MB (ramka nie większa niż 2 MB, które przyjmuje druga strona)
            override fun write(buf: ByteArray, off: Int, len: Int) {
                var o = off; var n = len
                while (n > 0) { val k = minOf(n, (1 shl 20) - bo.size()); bo.write(buf, o, k); o += k; n -= k; if (bo.size() >= 1 shl 20) flush() }
            }
            override fun flush() { if (bo.size() > 0) { val d = bo.toByteArray(); bo.reset(); writeFrame(d) } }
        }
        override fun close() { runCatching { output.flush() }; runCatching { sock.close() } }
    }

    private fun macOf(key: ByteArray, label: String, data: ByteArray) = hmac(key, label.toByteArray(Charsets.US_ASCII) + data)

    // mode: "main" (pakiety), "tunnel" (plik do `to`), "tunnel-in" (plik do nas, `tunnel` = id)
    private fun relayConnect(relay: Contact, mode: String, tunnel: String? = null, to: String? = null): Frames? = runCatching {
        val hp = (relay.relay ?: return null).split(':'); if (hp.size != 2) return null
        val sock = java.net.Socket()
        try {
            sock.connect(InetSocketAddress(hp[0], hp[1].toInt()), 12000)   // nazwa No-IP sprawdzana przy każdym połączeniu - zmiana IP domu nie przeszkadza
            sock.soTimeout = 15000; sock.keepAlive = true
            val out = sock.getOutputStream(); val inp = DataInputStream(sock.getInputStream())
            val nc = ByteArray(16).also { random.nextBytes(it) }
            val hello = JSONObject().put("Type", "relay-hello").put("Id", myId).put("Relay", relay.id).put("Nonce", b64(nc))
                .put("Ts", System.currentTimeMillis()).put("Mode", mode).put("Tunnel", tunnel ?: "").put("To", to ?: "").toString().toByteArray(Charsets.UTF_8)
            writeBlock(out, hello); writeBlock(out, macOf(relay.key, "relay-hello\n", hello))
            val rep = readBlock(inp, 4096) ?: throw java.io.IOException(); val mac = readBlock(inp, 64) ?: throw java.io.IOException()
            if (!MessageDigest.isEqual(mac, macOf(relay.key, "relay-ok\n", hello + rep))) throw java.io.IOException("to nie nasz pośrednik")
            val ns = unb64(JSONObject(String(rep, Charsets.UTF_8)).optString("Nonce")); if (ns.size != 16) throw java.io.IOException()
            sock.soTimeout = 150000
            Frames(sock, MessageDigest.getInstance("SHA-256").digest(relay.key + "velivo-relay".toByteArray(Charsets.US_ASCII) + nc + ns))
        } catch (e: Exception) { runCatching { sock.close() }; throw e }
    }.getOrNull()

    // połączenia z pośrednikami od nowa (stare po zmianie sieci albo uśpieniu telefonu bywają martwe bez błędu)
    private fun dropUp() { up?.let { closeUp(it, "powrót do Velivo, pośrednik milczał", alive = false) }; upMore.values.forEach { closeUp(it, "powrót do Velivo", alive = false) }; moreAt = 0L }
    // 🔎 zamknięcie połączenia z pośrednikiem z podaniem powodu (pośrednik zapisuje go w dzienniku)
    @Volatile private var lastWhy = ""
    private fun closeUp(f: Frames, why: String, alive: Boolean = true) {
        // martwe połączenie: tylko zamknięcie (zapis mógłby zawisnąć i zatrzymać ponowne łączenie)
        if (alive) upWriter.execute { runCatching { f.writeFrame(JSONObject().put("t", "relay-log").put("why", "zamykam: $why").toString().toByteArray(Charsets.UTF_8)) }; runCatching { f.close() } }
        else { lastWhy = why; runCatching { f.sock.close() } }
    }
    // telefon obudzony / Velivo na ekranie: pośrednik milczy ponad 25 s - od razu nowe połączenie (bez czekania 50 s)
    fun wake() { val u = up ?: return; if (System.currentTimeMillis() - (upRx[u] ?: 0L) > 25000) Thread { dropUp() }.start() }

    private fun upSend(u: Frames, p: JSONObject) {
        val d = p.toString().toByteArray(Charsets.UTF_8)
        upWriter.execute { runCatching { u.writeFrame(d) }.onFailure { if (up === u) up = null; upMore.entries.removeIf { it.value === u }; runCatching { u.close() } } }
    }

    // 🌍 pozostali pośrednicy (inne komputery z Velivo w kontaktach) - wiadomości idą przez każdego, który działa
    private val upMore = java.util.concurrent.ConcurrentHashMap<String, Frames>()
    private val upRx = java.util.concurrent.ConcurrentHashMap<Frames, Long>()   // ostatnia ramka od pośrednika (odpowiedź na sygnał)
    @Volatile private var moreAt = 0L
    private fun connectMore(relays: List<Contact>) {
        if (System.currentTimeMillis() - moreAt < 60000) return
        moreAt = System.currentTimeMillis()
        Thread {
            for (c in relays) {
                if (c.id == upRelay?.id || upMore.containsKey(c.id) || up == null) continue
                val f = relayConnect(c, "main") ?: continue
                upMore[c.id] = f; upReader(f, c)
                upSend(f, packet("p2p-here"))
            }
        }.start()
    }
    private fun upReader(ff: Frames, rl: Contact) = Thread {
        upRx[ff] = System.currentTimeMillis()
        if (lastWhy.isNotEmpty()) { val w = lastWhy; lastWhy = ""; upSend(ff, JSONObject().put("t", "relay-log").put("why", "poprzednie zerwanie: $w")) }
        runCatching {
            while (up === ff || upMore[rl.id] === ff) {
                val raw = ff.readFrame(); val p = runCatching { JSONObject(String(raw, Charsets.UTF_8)) }.getOrNull() ?: continue; val t = p.optString("t")
                upRx[ff] = System.currentTimeMillis()
                // odpowiedź pośrednika na sygnał - pośrednik nadal w sieci (inaczej po chwili „poza siecią”, choć połączone)
                if (t == "relay-pong") { val was = online(rl.id); lastSeen[rl.id] = System.currentTimeMillis(); seenAt[rl.id] = System.currentTimeMillis(); viaNet[rl.id] = true; if (!was) main.post { onChange() }; continue }
                if (t == "relay-open") { val tid = p.optString("tid"); Thread { tunnelIn(rl, tid) }.start(); continue }
                // błąd przy jednym pakiecie nie może zrywać połączenia z pośrednikiem (zamykało je po kilku sekundach)
                if (t.startsWith("p2p-")) runCatching { handleUdp(p, InetAddress.getLoopbackAddress(), viaRelay = true) }
            }
            closeUp(ff, "połączenie zastąpione innym")
        }.onFailure { if (lastWhy.isEmpty()) lastWhy = (it.javaClass.simpleName + " " + (it.message ?: "")).take(160) }
        if (up === ff) up = null
        upMore.remove(rl.id, ff); upRx.remove(ff); runCatching { ff.close() }
        main.post { onChange() }
    }.start()

    // połączenie do urządzenia: poza domem - tunel przez pośrednika; w domu - TCP w sieci domowej
    private fun connect(target: InetAddress, toId: String?): Conn {
        val relay = upRelay
        if (up != null && relay != null && toId != null) relayConnect(relay, "tunnel", to = toId)?.let { f -> return Conn(DataInputStream(f.input), f.output, null, f.sock) }
        val s = java.net.Socket(); s.connect(InetSocketAddress(target, PORT), 5000)
        return Conn(DataInputStream(s.getInputStream()), s.getOutputStream(), target, s)
    }

    // co 20 s: pośrednik nie widoczny w sieci domowej - połączenie przez internet; widoczny - z powrotem w domu
    @Volatile private var lastIps = ""
    @Volatile private var lastPing = 0L
    private val upLoop = Thread {
        while (true) runCatching {
            Thread.sleep(5000)
            // zmiana sieci (Wi-Fi <-> dane komórkowe): od razu od nowa - kto jest w domu, bez czekania na stare „widziany w domu”
            val ips = runCatching { NetworkInterface.getNetworkInterfaces().toList().flatMap { it.inetAddresses.toList() }.filter { !it.isLoopbackAddress && it is java.net.Inet4Address }.joinToString { it.hostAddress ?: "" } }.getOrDefault("")
            if (ips != lastIps) {
                val first = lastIps.isEmpty(); lastIps = ips
                // inna sieć (np. jeden internet -> drugi): połączenie z adresu, którego już nie ma, jest martwe - od razu nowe
                if (!first) (listOfNotNull(up) + upMore.values).forEach { x -> val a = (x.sock.localAddress as? java.net.Inet4Address)?.hostAddress; if (a != null && a !in ips.split(", ")) closeUp(x, "zmiana sieci", alive = false) }
                if (!first) { lanSeen.clear(); runCatching { sendUdp(packet("p2p-who"), null) }; Thread.sleep(1500) }   // w domowym Wi-Fi komputery odpowiadają w ~1 s
            }
            // komputery z rozmowami poza domem (każdy z kontaktami może nim być); w domu - którykolwiek widoczny w sieci domowej
            val relays = synchronized(this) { contacts.filter { it.relay != null } }
            var relay = relays.firstOrNull()
            // w domu = w sieci domowej widać którykolwiek kontakt (wtedy wszystko jak dotąd, prosto); poza domem - nikogo
            val now = System.currentTimeMillis()
            val home = relay == null || (now - started < 30000 && !upForce) || relays.any { now - (lanSeen[it.id] ?: 0L) < 100000 }   // w domu = widać pośrednika (np. NUCBOX); inny telefon w tej samej sieci (hotspot) to nie dom
            if (home) {
                up?.let { up = null; closeUp(it, "w domu (widać kontakt w Wi-Fi)"); announce() }
                upMore.values.forEach { closeUp(it, "w domu") }; upMore.clear()
                return@runCatching
            }
            // główny pośrednik padł, a inny działa - przejmuje
            if (up == null) upMore.entries.firstOrNull()?.let { (id, f) -> contact(id)?.let { c -> upRelay = c; up = f; upMore.remove(id); announce() } }
            val u = up
            if (u != null) {
                if (now - lastPing > 20000) { lastPing = now; upSend(u, JSONObject().put("t", "relay-ping")); upMore.values.forEach { upSend(it, JSONObject().put("t", "relay-ping")) } }
                // połączenie „umarło po cichu” (operator zerwał bez powiadomienia) - pośrednik nie odpowiada na sygnał 50 s: od nowa
                (listOf(u) + upMore.values).forEach { x -> if (now - (upRx[x] ?: now) > 50000) closeUp(x, "cisza 50 s", alive = false) }
                connectMore(relays)   // każdy włączony pośrednik przekazuje - łączymy się ze wszystkimi
                return@runCatching
            }
            if (udpOut == null && udp == null) return@runCatching   // komunikator nie działa (aplikacja zamknięta bez odbioru w tle)
            var f: Frames? = null
            for (cand in relays) { f = relayConnect(cand, "main"); if (f != null) { relay = cand; break } }   // pierwszy, który odpowie
            // adres domu się zmienił (nowe IP od operatora) - przez nazwę domu, port zapamiętany i domyślny
            val hn = homeName
            if (f == null && hn.isNotEmpty()) for (cand in relays) {
                val old = cand.relay ?: continue
                for (port in listOf(old.substringAfterLast(':'), "41920").distinct()) {
                    val alt = "$hn:$port"; if (alt == old) continue
                    cand.relay = alt; f = relayConnect(cand, "main")
                    if (f != null) { relay = cand; saveContacts(); break } else cand.relay = old
                }
                if (f != null) break
            }
            val ff = f ?: return@runCatching; val rl = relay!!
            upRelay = rl; up = ff
            announce()   // jestem (przez internet) + zaległe wiadomości
            upReader(ff, rl)
            connectMore(relays)
            main.post { onChange() }
        }
    }.apply { isDaemon = true }

    // plik do nas od urządzenia w domu (przez pośrednika)
    private fun tunnelIn(relay: Contact, tid: String) {
        val f = relayConnect(relay, "tunnel-in", tunnel = tid) ?: return
        f.use { runCatching {
            val inp = DataInputStream(f.input)
            val hb = readBlock(inp, 4096) ?: return
            handleTcp(JSONObject(String(hb, Charsets.UTF_8)), Conn(inp, f.output, null, f.sock))
        } }
    }

    // --- własne gniazda (klawiatura bez przeglądarki; przeglądarka używa gniazd LanSync) ---
    @Volatile private var udp: DatagramSocket? = null
    @Volatile private var tcp: ServerSocket? = null
    var listenError: String? = null

    fun listen() {
        if (udp != null) return
        listenError = null
        val u = runCatching { DatagramSocket(null).apply { reuseAddress = true; broadcast = true; bind(InetSocketAddress(PORT)) } }
            .getOrElse { listenError = it.message; return }
        udp = u
        Thread {
            val buf = ByteArray(65536)
            while (udp === u) runCatching {
                val p = DatagramPacket(buf, buf.size); u.receive(p)
                handleUdp(JSONObject(String(p.data, 0, p.length, Charsets.UTF_8)), p.address)
            }.onFailure { if (udp !== u) return@Thread }
        }.start()
        Thread {
            val s = runCatching { ServerSocket().apply { reuseAddress = true; bind(InetSocketAddress(PORT)) } }.getOrElse { listenError = it.message; return@Thread }
            tcp = s
            while (tcp === s) {
                val c = runCatching { s.accept() }.getOrNull() ?: break
                Thread {
                    runCatching {
                        c.use {
                            it.soTimeout = 15000
                            val inp = DataInputStream(it.getInputStream())
                            val hb = readBlock(inp, 4096) ?: return@use
                            handleTcp(JSONObject(String(hb, Charsets.UTF_8)), inp, it)
                        }
                    }
                }.start()
            }
        }.start()
    }

    val idle get() = udp == null && udpOut == null

    fun close() {
        runCatching { udp?.close() }; runCatching { tcp?.close() }
        udp = null; tcp = null
    }

    private fun broadcastTargets(): List<InetAddress> {
        val list = mutableListOf(InetAddress.getByName("255.255.255.255"))
        runCatching {
            for (ni in NetworkInterface.getNetworkInterfaces()) {
                if (!ni.isUp || ni.isLoopback) continue
                for (ia in ni.interfaceAddresses) ia.broadcast?.let { if (it !in list) list.add(it) }
            }
        }
        return list
    }

    // --- powiadomienia o wiadomościach (jak w innych komunikatorach) ---
    @Volatile var chatOpen: String? = null          // rozmowa widoczna na ekranie - bez powiadomienia
    var openIntent: (contactId: String?) -> Intent = { Intent() }   // co otworzyć po stuknięciu powiadomienia
    // 🖼📎 zdjęcia i pliki od dodanych kontaktów (do 2 GB) od razu w rozmowie (zgoda: dodanie kontaktu kodem); wyłącznik w 🎨
    var autoImages: Boolean
        get() = prefs.getBoolean("autoimg", true)
        set(v) { prefs.edit().putBoolean("autoimg", v).apply() }
    val mediaDir get() = java.io.File(ctx.filesDir, "komunikator").also { it.mkdirs() }
    fun isImage(name: String) = name.substringAfterLast('.', "").lowercase() in setOf("jpg", "jpeg", "png", "gif", "bmp", "webp")

    var theme: String
        get() = prefs.getString("motyw", "auto") ?: "auto"
        set(v) { prefs.edit().putString("motyw", v).apply() }
    var background: Boolean
        get() = prefs.getBoolean("tlo", true)
        set(v) { prefs.edit().putBoolean("tlo", v).apply() }

    private fun channels(nm: NotificationManager) {
        val pl = Locale.getDefault().language == "pl"
        nm.createNotificationChannel(NotificationChannel("wiadomosci", if (pl) "Wiadomości" else "Messages", NotificationManager.IMPORTANCE_HIGH))
        nm.createNotificationChannel(NotificationChannel("informacje", if (pl) "Informacje" else "Information", NotificationManager.IMPORTANCE_LOW))
        nm.createNotificationChannel(NotificationChannel("nasluch", if (pl) "Komunikator w tle" else "Messenger in background", NotificationManager.IMPORTANCE_MIN))
    }

    fun notifyMessage(c: Contact, quiet: Boolean = false) {
        if (chatOpen == c.id) { if (!quiet) chatSound(); sendRead(c); return }
        val nm = ctx.getSystemService(NotificationManager::class.java) ?: return
        channels(nm)
        // jak w innych komunikatorach: ostatnie wiadomości w powiadomieniu, odpowiedź i „przeczytane” bez otwierania Velivo
        val pl = Locale.getDefault().language == "pl"
        val last = synchronized(this) { chat(c.id).takeLast(6) }
        val me = android.app.Person.Builder().setName(if (pl) "Ty" else "You").build()
        val them = android.app.Person.Builder().setName(c.name).setKey(c.id).build()
        val st = Notification.MessagingStyle(me)
        last.forEach { st.addMessage(Notification.MessagingStyle.Message(it.text, it.ts, if (it.mine) null else them)) }
        val n = unread(c.id)
        val pi = PendingIntent.getActivity(ctx, c.id.hashCode(), openIntent(c.id), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        fun act(what: String) = Intent(ctx, KomunikatorAkcja::class.java).setAction(what).putExtra("id", c.id).setPackage(ctx.packageName)
        val replyPi = PendingIntent.getBroadcast(ctx, c.id.hashCode() + 1, act(KomunikatorAkcja.REPLY), PendingIntent.FLAG_MUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val readPi = PendingIntent.getBroadcast(ctx, c.id.hashCode() + 2, act(KomunikatorAkcja.READ), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val reply = Notification.Action.Builder(android.graphics.drawable.Icon.createWithResource(ctx, android.R.drawable.ic_menu_send), if (pl) "Odpowiedz" else "Reply", replyPi)
            .addRemoteInput(android.app.RemoteInput.Builder(KomunikatorAkcja.TEXT).setLabel(if (pl) "Wiadomość" else "Message").build())
            .setSemanticAction(Notification.Action.SEMANTIC_ACTION_REPLY).setAllowGeneratedReplies(false).build()
        val read = Notification.Action.Builder(android.graphics.drawable.Icon.createWithResource(ctx, android.R.drawable.ic_menu_view), if (pl) "Przeczytane" else "Mark as read", readPi)
            .setSemanticAction(Notification.Action.SEMANTIC_ACTION_MARK_AS_READ).build()
        runCatching {
            nm.notify(c.id.hashCode(), Notification.Builder(ctx, "wiadomosci").setSmallIcon(android.R.drawable.stat_notify_chat)
                .setContentTitle(c.name).setContentText(last.lastOrNull()?.text ?: "")
                .setStyle(st).setNumber(n).setCategory(Notification.CATEGORY_MESSAGE).setContentIntent(pi).setAutoCancel(true)
                .setOnlyAlertOnce(quiet).addAction(reply).addAction(read).setShowWhen(true).build())
        }
    }
    // rozmowa na ekranie: bez powiadomienia, tylko krótki dźwięk (jak w innych komunikatorach); wyciszony telefon - cisza
    private fun chatSound() = runCatching {
        val am = ctx.getSystemService(android.media.AudioManager::class.java)
        if (am?.ringerMode != android.media.AudioManager.RINGER_MODE_NORMAL) return@runCatching
        android.media.RingtoneManager.getRingtone(ctx, android.media.RingtoneManager.getDefaultUri(android.media.RingtoneManager.TYPE_NOTIFICATION))?.play()
    }

    // 📶 w domowym Wi-Fi jest urządzenie z Velivo spoza kontaktów - powiadomienie raz na dobę (do wyłączenia)
    private fun notifyNewDevice(id: String, dev: String) {
        if (!prefs.getBoolean("powiadom-nowe", true)) return
        val now = System.currentTimeMillis(); if (now - prefs.getLong("nowe-urz-$id", 0) < 86400000) return
        prefs.edit().putLong("nowe-urz-$id", now).apply()
        val nm = ctx.getSystemService(NotificationManager::class.java) ?: return
        channels(nm)
        val pl = Locale.getDefault().language == "pl"
        val pi = PendingIntent.getActivity(ctx, 78, openIntent(null), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val off = PendingIntent.getBroadcast(ctx, 79, Intent(ctx, KomunikatorAkcja::class.java).setAction(KomunikatorAkcja.NEW_OFF).setPackage(ctx.packageName), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        runCatching {
            nm.notify("nowe-urz".hashCode(), Notification.Builder(ctx, "informacje")   // cichy kanał - bez dźwięku i budzenia ekranu.setSmallIcon(android.R.drawable.stat_notify_chat)
                .setContentTitle(if (pl) "📶 Velivo w tej sieci" else "📶 Velivo on this network")
                .setContentText(if (pl) "„${dev.ifEmpty { "Urządzenie" }}” – stuknij, by dodać do komunikatora" else "“${dev.ifEmpty { "Device" }}” – tap to add to the messenger")
                .setContentIntent(pi).setAutoCancel(true).setOnlyAlertOnce(true)
                .addAction(Notification.Action.Builder(null, if (pl) "Nie pokazuj" else "Don't show", off).build()).build())
        }
    }
    var newDeviceNotifications: Boolean
        get() = prefs.getBoolean("powiadom-nowe", true)
        set(v) { prefs.edit().putBoolean("powiadom-nowe", v).apply() }

    private fun notifyPair(dev: String) {
        val nm = ctx.getSystemService(NotificationManager::class.java) ?: return
        channels(nm)
        val pl = Locale.getDefault().language == "pl"
        val pi = PendingIntent.getActivity(ctx, 77, openIntent(null), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        runCatching {
            nm.notify("para".hashCode(), Notification.Builder(ctx, "wiadomosci").setSmallIcon(android.R.drawable.stat_notify_chat)
                .setContentTitle(if (pl) "💬 Prośba o kontakt" else "💬 Contact request").setContentText(if (pl) "„$dev” chce dodać Cię do komunikatora – stuknij, by przyjąć" else "“$dev” wants to add you – tap to accept")
                .setCategory(Notification.CATEGORY_MESSAGE).setContentIntent(pi).setAutoCancel(true).build())
        }
    }

    fun clearNotification(id: String) { ctx.getSystemService(NotificationManager::class.java)?.cancel(id.hashCode()) }

    fun listenNotification(): Notification {
        val nm = ctx.getSystemService(NotificationManager::class.java); if (nm != null) channels(nm)
        val pl = Locale.getDefault().language == "pl"
        val pi = PendingIntent.getActivity(ctx, 1, openIntent(null), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return Notification.Builder(ctx, "nasluch").setSmallIcon(android.R.drawable.stat_notify_chat)
            .setContentTitle(if (pl) "Komunikator Velivo" else "Velivo messenger")
            .setContentText(if (pl) "Czeka na wiadomości w domowym Wi-Fi" else "Waiting for messages on home Wi-Fi")
            .setContentIntent(pi).setOngoing(true).build()
    }

    // 🟢 w sieci: kontakt odezwał się w ostatnich 95 s (każde Velivo z komunikatorem ogłasza się co minutę, okno komunikatora pyta co 30 s)
    private val lastSeen = java.util.concurrent.ConcurrentHashMap<String, Long>()
    fun online(id: String) = System.currentTimeMillis() - (lastSeen[id] ?: 0L) < 95000
    // „ostatnio w sieci” (jak w innych komunikatorach) - zostaje także po pożegnaniu
    private val seenAt = java.util.concurrent.ConcurrentHashMap<String, Long>()
    fun lastSeenAt(id: String): Long? = seenAt[id]

    // wychodzimy z sieci (Velivo zamknięte bez odbioru w tle) - kontakty od razu widzą „poza siecią”
    fun bye() = Thread { repeat(2) { sendUdp(packet("p2p-bye"), null); Thread.sleep(150) } }.start()
    private var shownOnline = emptySet<String>()

    // niedoręczone (…) - ponownie, gdy kontakt odezwie się w sieci, i co minutę
    private fun resend(c: Contact) {
        val pending = synchronized(this) { chat(c.id).filter { it.mine && !it.ok && !it.file && System.currentTimeMillis() - it.ts < 7L * 86400000 } }
        pending.forEach { deliver(c, it) }
    }
    private val resender = Thread {
        while (true) runCatching {
            Thread.sleep(60000)
            val list = synchronized(this) { contacts.toList() }
            if ((udpOut != null || udp != null) && list.isNotEmpty()) { sendUdp(packet("p2p-here"), null); list.forEach { resend(it) } }
            val now = list.filter { online(it.id) }.map { it.id }.toSet()
            if (now != shownOnline) { shownOnline = now; main.post { onChange() } }
        }
    }.apply { isDaemon = true }

    companion object {
        @Volatile private var one: Komunikator? = null
        // jeden w aplikacji - okna i usługa w tle widzą te same rozmowy
        fun get(ctx: Context, deviceName: () -> String): Komunikator = one ?: synchronized(this) {
            one ?: Komunikator(ctx.applicationContext, deviceName).also { one = it; it.resender.start(); it.upLoop.start() }
        }
        const val PORT = 41919
        const val CHUNK = 1 shl 20
        const val MAX_TEXT = 4000
        const val CALL = "\u0001velivo-rozmowa\n"
        fun b64(b: ByteArray): String = Base64.encodeToString(b, Base64.NO_WRAP)
        fun unb64(s: String): ByteArray = runCatching { Base64.decode(s, Base64.DEFAULT) }.getOrDefault(ByteArray(0))
        fun hex(b: ByteArray) = b.joinToString("") { "%02X".format(it) }
        fun clean(s: String) = s.filter { !it.isISOControl() }.trim().take(64)
        fun hmac(key: ByteArray, data: ByteArray): ByteArray = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }.doFinal(data)
        fun int4(n: Int) = byteArrayOf(n.toByte(), (n shr 8).toByte(), (n shr 16).toByte(), (n shr 24).toByte())
        fun readInt4(inp: DataInputStream): Int { val b = ByteArray(4).also { inp.readFully(it) }; return (b[0].toInt() and 0xff) or ((b[1].toInt() and 0xff) shl 8) or ((b[2].toInt() and 0xff) shl 16) or ((b[3].toInt() and 0xff) shl 24) }
        // 6 cyfr "123 456" z klucza - ten sam na obu urządzeniach tylko, gdy nikt się nie podszył
        fun code(key: ByteArray): String {
            val d = MessageDigest.getInstance("SHA-256").digest(key + "kod".toByteArray())
            val n = ((d[0].toLong() and 0xff) shl 24 or ((d[1].toLong() and 0xff) shl 16) or ((d[2].toLong() and 0xff) shl 8) or (d[3].toLong() and 0xff)) % 1_000_000
            return String.format(Locale.ROOT, "%03d %03d", n / 1000, n % 1000)
        }
    }
}

// Okna komunikatora (wspólne dla przeglądarki i klawiatury): kontakty, rozmowa, dodawanie kontaktu, pliki.
// pickFiles - wybór plików przez aplikację (wynik w onActivityResult); open/size - odczyt wybranego pliku.
class KomunikatorOkno(private val a: Activity, private val k: Komunikator, private val pickFiles: () -> Unit) {
    private val pl = Locale.getDefault().language == "pl"
    private fun t(p: String, e: String) = if (pl) p else e
    private val dp = a.resources.displayMetrics.density
    private fun px(v: Int) = (v * dp).toInt()
    private var chatWith: String? = null
    private var chatRefresh: (() -> Unit)? = null

    init {
        k.onCall = { c, o -> onCall(c, o) }
        k.askPair = { dev, code, accept ->
            AlertDialog.Builder(a).setTitle("💬 " + t("Nowy kontakt", "New contact"))
                .setMessage(t("„$dev” chce dodać Cię do komunikatora Velivo.\n\nKod: $code\n\nPrzyjmij tylko, gdy na drugim urządzeniu jest ten sam kod.",
                    "“$dev” wants to add you to Velivo messenger.\n\nCode: $code\n\nAccept only if the other device shows the same code."))
                .setPositiveButton(t("Przyjmij", "Accept")) { _, _ -> accept(true) }.setNegativeButton(t("Odrzuć", "Decline")) { _, _ -> accept(false) }
                .setOnCancelListener { accept(false) }.show()
        }
        k.pendingPair?.let { (d, c, acc) -> k.pendingPair = null; a.window.decorView.post { k.askPair(d, c, acc) } }   // prośba z czasu, gdy Velivo było w tle
        k.askFile = { dev, name, size, code, accept ->
            AlertDialog.Builder(a).setTitle("📥 " + t("Plik z: ", "File from: ") + dev)
                .setMessage("$name (${sizeText(size)})" + if (code.isEmpty()) "" else "\n\n" + t("Kod: $code\n\nPrzyjmij tylko, gdy telefon nadawcy pokazuje ten sam kod.",
                    "Code: $code\n\nAccept only if the sender's phone shows the same code."))
                .setPositiveButton(t("Przyjmij", "Accept")) { _, _ -> accept(true) }.setNegativeButton(t("Odrzuć", "Decline")) { _, _ -> accept(false) }
                .setOnCancelListener { accept(false) }.show()
        }
        // rozmowa „na ekranie” tylko, gdy aplikacja jest na wierzchu - w tle / przy wygaszonym ekranie zwykłe powiadomienia
        a.application.registerActivityLifecycleCallbacks(object : android.app.Application.ActivityLifecycleCallbacks {
            override fun onActivityResumed(x: Activity) { if (x === a) k.chatOpen = chatWith }
            override fun onActivityPaused(x: Activity) { if (x === a) k.chatOpen = null }
            override fun onActivityDestroyed(x: Activity) { if (x === a) { k.chatOpen = null; k.askPair = k.noUiPair; a.application.unregisterActivityLifecycleCallbacks(this) } }
            override fun onActivityCreated(x: Activity, b: android.os.Bundle?) {}
            override fun onActivityStarted(x: Activity) {}
            override fun onActivityStopped(x: Activity) {}
            override fun onActivitySaveInstanceState(x: Activity, b: android.os.Bundle) {}
        })
        k.onChange = { chatRefresh?.invoke() ?: listRefresh?.invoke() }
    }

    fun sizeText(n: Long) = when { n >= 1L shl 30 -> String.format(Locale.ROOT, "%.1f GB", n / 1073741824.0); n >= 1L shl 20 -> String.format(Locale.ROOT, "%.1f MB", n / 1048576.0); else -> "${maxOf(1, n / 1024)} KB" }

    private fun bg(color: Int, r: Int) = GradientDrawable().apply { setColor(color); cornerRadius = px(r).toFloat() }

    // --- jeden pełny ekran jak w innych komunikatorach: lista rozmów <-> rozmowa (strzałka / Wstecz) ---
    private var screen: android.app.Dialog? = null
    private var page: FrameLayout? = null
    private var listRefresh: (() -> Unit)? = null
    var onClose: (() -> Unit)? = null   // klawiatura: przezroczyste okno znika razem z komunikatorem
    // 🎨 motywy komunikatora - te same zestawy co w Velivo na Windows (Komunikator.cs)
    class Pal(val key: String, val pl: String, val en: String, val dark: Boolean, private val c: List<String>) {
        private fun h(i: Int) = Color.parseColor("#" + c[i])
        val bg1 = h(0); val bg2 = h(1); val panel = h(2); val fg = h(3); val dim = h(4); val mine = h(5); val mineFg = h(6); val theirs = h(7); val input = h(8); val border = h(9); val button = h(10)
    }
    private val palettes = listOf(
        Pal("noc", "🌙 Velivo nocny", "🌙 Velivo night", true, listOf("1E1838", "132A35", "2E2560", "ECE8FF", "A9A3CC", "6D3FDB", "FFFFFF", "3A3360", "2E2768", "4A3F8A", "2E2768")),
        Pal("jasny", "☀ Velivo jasny", "☀ Velivo light", false, listOf("E6E1F5", "DCE6F0", "F1EEFA", "2A2255", "6E6894", "6D3FDB", "FFFFFF", "FBFAFF", "FAF9FE", "B9ACE6", "E4DEF7")),
        Pal("ocean", "🌊 Ocean", "🌊 Ocean", true, listOf("0B2540", "0E4D64", "10314F", "E3F4FF", "9CC3D9", "0284C7", "FFFFFF", "1D4A6B", "0F2F4A", "2F6C93", "15466B")),
        Pal("las", "🌲 Las", "🌲 Forest", true, listOf("14281D", "1F3D2B", "1B3326", "E6F5EA", "A3C4AE", "15803D", "FFFFFF", "2D5240", "183025", "3C6B52", "24473A")),
        Pal("zachod", "🌅 Zachód słońca", "🌅 Sunset", true, listOf("3B1C32", "6A2C3E", "4A2238", "FFEDE6", "D9AFA8", "EA580C", "FFFFFF", "6E3448", "3F1D30", "8C4A5C", "5C2A42")),
        Pal("grafit", "🖤 Grafit", "🖤 Graphite", true, listOf("1C1C1E", "2C2C2E", "242426", "EDEDED", "A0A0A5", "3A82F7", "FFFFFF", "3A3A3C", "2A2A2C", "48484A", "333336")),
        Pal("roza", "🌸 Róża", "🌸 Rose", false, listOf("FCE7F3", "F5E1FF", "FBEFF7", "4A1D3A", "8E5C7C", "DB2777", "FFFFFF", "FFFFFF", "FFF7FB", "F0ABD0", "F7D9EA")),
        Pal("mieta", "🍃 Mięta", "🍃 Mint", false, listOf("E0F7EF", "DDEFF7", "EEFAF5", "17352B", "5E8577", "0F9D74", "FFFFFF", "FFFFFF", "F7FDFB", "9ED9C5", "D4F0E6")),
        Pal("kontrast", "◐ Wysoki kontrast", "◐ High contrast", true, listOf("000000", "000000", "000000", "FFFFFF", "FFFF00", "FFFF00", "000000", "1A1A1A", "000000", "FFFFFF", "000000")),
    )
    // auto - jak motyw telefonu (noc / dzień)
    private fun pal(): Pal = palettes.firstOrNull { it.key == k.theme }
        ?: if ((a.resources.configuration.uiMode and android.content.res.Configuration.UI_MODE_NIGHT_MASK) == android.content.res.Configuration.UI_MODE_NIGHT_NO) palettes[1] else palettes[0]
    private val ink get() = pal().fg
    private val dim get() = pal().dim
    private val green = Color.rgb(34, 197, 94)
    private val violet get() = pal().mine

    private fun page(): FrameLayout {
        page?.let { if (screen?.isShowing == true) return it }
        val d = android.app.Dialog(a, android.R.style.Theme_Material_NoActionBar)
        val root = FrameLayout(a).apply {
            // pasek stanu u góry, klawiatura / pasek nawigacji u dołu - pole wpisywania zawsze nad klawiaturą
            if (Build.VERSION.SDK_INT >= 30) setOnApplyWindowInsetsListener { v, ins ->
                val bars = ins.getInsets(android.view.WindowInsets.Type.systemBars()); val ime = ins.getInsets(android.view.WindowInsets.Type.ime())
                v.setPadding(0, bars.top, 0, maxOf(bars.bottom, ime.bottom)); ins
            }
        }
        d.setContentView(root)
        d.window?.apply {
            setLayout(-1, -1)
            setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
            if (Build.VERSION.SDK_INT >= 30) setDecorFitsSystemWindows(false)
        }
        paint(d, root)
        d.setOnKeyListener { _, code, e -> if (code == android.view.KeyEvent.KEYCODE_BACK && e.action == android.view.KeyEvent.ACTION_UP && chatWith != null) { show(); true } else false }
        // realny stan kontaktów: pytanie od razu i co 30 s, dopóki komunikator otwarty
        val h = Handler(Looper.getMainLooper())
        val tick = object : Runnable { override fun run() { k.ping(); h.postDelayed({ (chatRefresh ?: listRefresh)?.invoke() }, 2000); h.postDelayed(this, 30000) } }
        h.post(tick)
        d.setOnDismissListener { h.removeCallbacksAndMessages(null); onClose?.invoke(); screen = null; page = null; chatWith = null; chatRefresh = null; listRefresh = null; k.chatOpen = null }
        screen = d; page = root
        d.show()
        return root
    }

    // tło i paski systemu w kolorach motywu
    private fun paint(d: android.app.Dialog?, root: View) {
        val p = pal()
        root.background = GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(p.bg1, p.bg2))
        d?.window?.apply {
            statusBarColor = p.bg1; navigationBarColor = p.bg2
            if (Build.VERSION.SDK_INT >= 30) decorView.windowInsetsController?.setSystemBarsAppearance(
                if (p.dark) 0 else android.view.WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS or android.view.WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS,
                android.view.WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS or android.view.WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS)
        }
    }

    private fun chooseTheme() {
        val keys = listOf("auto") + palettes.map { it.key }
        val names = listOf(t("📱 Automatyczny (jak telefon)", "📱 Automatic (like the phone)")) + palettes.map { if (pl) it.pl else it.en }
        AlertDialog.Builder(a).setTitle("🎨 " + t("Motyw komunikatora", "Messenger theme"))
            .setSingleChoiceItems(names.toTypedArray(), keys.indexOf(k.theme).coerceAtLeast(0)) { d, i ->
                k.theme = keys[i]; d.dismiss()
                page?.let { paint(screen, it) }
                val c = chatWith?.let { k.contact(it) }
                if (c != null) openChat(c) else show()
            }.setNeutralButton("🖼 " + t("Pliki od kontaktów od razu: ", "Files from contacts at once: ") + if (k.autoImages) t("wł.", "on") else t("wył.", "off")) { _, _ ->
                k.autoImages = !k.autoImages
                Toast.makeText(a, "🖼 " + t("Zdjęcia i pliki od kontaktów od razu w rozmowie: ", "Photos and files from contacts straight into the chat: ") + if (k.autoImages) t("wł.", "on") else t("wył.", "off"), Toast.LENGTH_LONG).show()
            }.setNegativeButton(android.R.string.cancel, null).show()
    }

    // mały okrągły przycisk-ikona w nagłówku (zwykły Button ma 88 dp szerokości i łamał tytuł na kilka linii)
    // 📹 rozmowa wideo: strona rozmowy (rozmowa.html, WebRTC) na cały ekran; sygnały przez komunikator
    private var callView: android.webkit.WebView? = null
    private var callWith: String? = null
    private var ringDlg: android.app.Dialog? = null
    private var ringTone: android.media.Ringtone? = null
    private fun onCall(c: Komunikator.Contact, o: JSONObject) {
        val kind = o.optString("k")
        if (kind == "ring") { if (callView != null) k.sendCall(c, JSONObject().put("k", "busy")) else incoming(c); return }
        if (kind == "end" && callView == null) { ringDlg?.dismiss(); return }
        if (callWith == c.id) callView?.evaluateJavascript("onSig(" + JSONObject.quote(o.toString()) + ")", null)
    }
    // ktoś dzwonił, gdy Velivo było w tle - po otwarciu od razu „Odbierz / Odrzuć”
    private fun ringPending(c: Komunikator.Contact) {
        val r = k.ringing ?: return
        if (r.first == c.id && System.currentTimeMillis() - r.second < 45000) incoming(c)
    }
    private fun incoming(c: Komunikator.Contact) {
        if (ringDlg?.isShowing == true) return
        k.ringing = null
        a.getSystemService(NotificationManager::class.java)?.cancel("rozmowa".hashCode())   // dzwonek gra okno, nie powiadomienie
        val d = android.app.Dialog(a, android.R.style.Theme_Black_NoTitleBar_Fullscreen)
        fun btn(text: String, tip: String, color: Int, act: () -> Unit) = TextView(a).apply {
            this.text = text; textSize = 30f; gravity = Gravity.CENTER; contentDescription = tip; tooltipText = tip
            background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(color) }
            layoutParams = LinearLayout.LayoutParams(px(76), px(76)).apply { leftMargin = px(28); rightMargin = px(28) }
            setOnClickListener { act() }
        }
        val name = TextView(a).apply { text = "📹 " + c.name + "\n" + t("dzwoni…", "is calling…"); textSize = 26f; gravity = Gravity.CENTER; setTextColor(ink) }
        val row = LinearLayout(a).apply { gravity = Gravity.CENTER; setPadding(0, px(48), 0, 0)
            addView(btn("✕", t("Odrzuć", "Decline"), Color.rgb(229, 57, 53)) { k.sendCall(c, JSONObject().put("k", "reject")); d.dismiss() })
            addView(btn("📞", t("Odbierz", "Answer"), green) { d.dismiss(); openCall(c, false) }) }
        d.setContentView(LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER; setBackgroundColor(pal().bg1); addView(name); addView(row) })
        d.setOnCancelListener { k.sendCall(c, JSONObject().put("k", "reject")) }
        d.setOnDismissListener { ringTone?.stop(); ringTone = null; ringDlg = null; a.getSystemService(NotificationManager::class.java)?.cancel("rozmowa".hashCode())
            if (callView == null) { a.setShowWhenLocked(false); a.setTurnScreenOn(false) } }
        a.setShowWhenLocked(true); a.setTurnScreenOn(true)   // jak w WhatsApp: także na zablokowanym ekranie
        ringDlg = d; if (runCatching { d.show() }.isFailure) { ringDlg = null; return }
        a.window.decorView.postDelayed({ if (ringDlg === d) d.dismiss() }, 45000)
        ringTone = runCatching { android.media.RingtoneManager.getRingtone(a, android.media.RingtoneManager.getDefaultUri(android.media.RingtoneManager.TYPE_RINGTONE)) }.getOrNull()?.also { runCatching { it.play() } }
    }
    fun openCall(c: Komunikator.Contact, caller: Boolean) {
        if (callView != null) return
        val perms = arrayOf(android.Manifest.permission.CAMERA, android.Manifest.permission.RECORD_AUDIO)
        fun granted() = perms.all { a.checkSelfPermission(it) == android.content.pm.PackageManager.PERMISSION_GRANTED }
        if (!granted()) a.requestPermissions(perms, 4711)
        val d = android.app.Dialog(a, android.R.style.Theme_Black_NoTitleBar_Fullscreen)
        val w = android.webkit.WebView(a)
        var ended = false
        // dźwięk: słuchawki (przewodowe / Bluetooth) - w nich; bez słuchawek - głośnik; 🔊 przełącza głośnik / słuchawkę przy uchu
        val am = a.getSystemService(android.media.AudioManager::class.java)
        val oldMode = am?.mode; val oldSpeaker = am?.isSpeakerphoneOn
        val heads = intArrayOf(android.media.AudioDeviceInfo.TYPE_BLUETOOTH_SCO, android.media.AudioDeviceInfo.TYPE_WIRED_HEADSET, android.media.AudioDeviceInfo.TYPE_WIRED_HEADPHONES, android.media.AudioDeviceInfo.TYPE_USB_HEADSET, 26 /* TYPE_BLE_HEADSET */)
        var spk = am?.getDevices(android.media.AudioManager.GET_DEVICES_OUTPUTS)?.none { it.type in heads } ?: true
        fun route() = runCatching {
            val m = am ?: return@runCatching
            if (Build.VERSION.SDK_INT >= 31) {
                val devs = m.availableCommunicationDevices
                val pick = if (spk) devs.firstOrNull { it.type == android.media.AudioDeviceInfo.TYPE_BUILTIN_SPEAKER }
                    else devs.firstOrNull { it.type in heads } ?: devs.firstOrNull { it.type == android.media.AudioDeviceInfo.TYPE_BUILTIN_EARPIECE }
                if (pick != null) m.setCommunicationDevice(pick)
            } else {
                @Suppress("DEPRECATION") m.isSpeakerphoneOn = spk
                @Suppress("DEPRECATION") if (!spk && m.isBluetoothScoAvailableOffCall) m.startBluetoothSco()
            }
        }
        runCatching { am?.mode = android.media.AudioManager.MODE_IN_COMMUNICATION }; route()
        w.settings.javaScriptEnabled = true; w.settings.mediaPlaybackRequiresUserGesture = false
        w.setBackgroundColor(Color.rgb(18, 15, 36))
        w.addJavascriptInterface(object {
            @android.webkit.JavascriptInterface fun send(s: String) {
                val o = runCatching { JSONObject(s) }.getOrNull() ?: return
                if (o.optString("k") == "close") { a.runOnUiThread { d.dismiss() }; return }
                if (o.optString("k") == "end") ended = true
                k.sendCall(c, o)
            }
            @android.webkit.JavascriptInterface fun speaker(): Boolean { spk = !spk; a.runOnUiThread { route() }; return spk }
            @android.webkit.JavascriptInterface fun speakerOn(): Boolean = spk
        }, "velivo")
        w.webChromeClient = object : android.webkit.WebChromeClient() {
            override fun onPermissionRequest(r: android.webkit.PermissionRequest) {
                fun tryGrant(n: Int) { if (granted()) r.grant(r.resources) else if (n < 120) w.postDelayed({ tryGrant(n + 1) }, 500) else r.deny() }
                a.runOnUiThread { tryGrant(0) }
            }
        }
        d.window?.addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        d.setContentView(w)
        d.setOnDismissListener {
            if (!ended) k.sendCall(c, JSONObject().put("k", "end"))
            callView = null; callWith = null
            runCatching { w.destroy() }
            runCatching {
                if (Build.VERSION.SDK_INT >= 31) am?.clearCommunicationDevice() else { @Suppress("DEPRECATION") am?.stopBluetoothSco() }
                oldMode?.let { am?.mode = it }; @Suppress("DEPRECATION") oldSpeaker?.let { am?.isSpeakerphoneOn = it }
            }
            a.setShowWhenLocked(false); a.setTurnScreenOn(false)
        }
        callView = w; callWith = c.id
        val html = runCatching { a.assets.open("rozmowa.html").bufferedReader().use { it.readText() } }.getOrNull() ?: return
        val q = "?rola=" + (if (caller) "dzwoni" else "odbiera") + "&ice=" + Uri.encode(k.turnIce()) + "&pl=" + (if (pl) "1" else "0")
        w.loadDataWithBaseURL("https://velivo.rozmowa/rozmowa.html$q", html, "text/html", "utf-8", null)
        d.show()
    }

    private fun icon(text: String, tip: String, size: Float = 20f, act: () -> Unit) = TextView(a).apply {
        this.text = text; textSize = size; gravity = Gravity.CENTER; setTextColor(ink); contentDescription = tip; tooltipText = tip
        isClickable = true; isFocusable = true
        background = android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(Color.argb(60, 128, 128, 128)), null, GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(Color.WHITE) })
        layoutParams = LinearLayout.LayoutParams(px(44), px(44)).apply { leftMargin = px(2) }
        setOnClickListener { act() }
    }

    private fun pill(text: String, tip: String, act: () -> Unit) = Button(a).apply {
        this.text = text; isAllCaps = false; textSize = 15f; setTextColor(pal().mineFg); contentDescription = tip; tooltipText = tip
        background = GradientDrawable().apply { setColor(pal().mine); cornerRadius = px(22).toFloat() }
        setPadding(px(14), 0, px(14), 0); setOnClickListener { act() }
    }

    private fun when_(ts: Long): String {
        val d = java.util.Calendar.getInstance().apply { timeInMillis = ts }; val now = java.util.Calendar.getInstance()
        return if (d.get(java.util.Calendar.YEAR) == now.get(java.util.Calendar.YEAR) && d.get(java.util.Calendar.DAY_OF_YEAR) == now.get(java.util.Calendar.DAY_OF_YEAR))
            java.text.SimpleDateFormat("HH:mm", Locale.getDefault()).format(java.util.Date(ts))
        else java.text.SimpleDateFormat("d MMM", Locale.getDefault()).format(java.util.Date(ts))
    }

    // lista rozmów
    fun show() {
        val root = page()
        chatWith = null; k.chatOpen = null; chatRefresh = null
        val rows = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(px(10), px(4), px(10), px(8)) }
        // 🏠 / 🌍 jak połączony jest ten telefon - zawsze widoczne u góry
        val netBar = TextView(a).apply { textSize = 14f; setTextColor(Color.WHITE); setPadding(px(14), px(8), px(14), px(8)); layoutParams = LinearLayout.LayoutParams(-1, -2).apply { leftMargin = px(10); rightMargin = px(10); bottomMargin = px(6) } }
        fun fill() {
            netBar.text = if (k.away) t("🌍 Poza domem – przez internet", "🌍 Away from home – via internet") + (k.awayVia?.let { t(" (przez $it)", " (through $it)") } ?: "") else t("🏠 W domu – sieć domowa", "🏠 At home – home network")
            netBar.background = bg(if (k.away) Color.rgb(0x25, 0x63, 0xEB) else Color.rgb(0x16, 0xA3, 0x4A), 10)
            rows.removeAllViews()
            if (k.contacts.isEmpty()) rows.addView(TextView(a).apply {
                text = t("Brak kontaktów.\n\nStuknij „➕ Dodaj”, gdy drugie urządzenie ma otwarty komunikator Velivo (telefon: przeglądarka albo klawiatura Velivo, komputer: Velivo na Windows) w tej samej sieci Wi-Fi.",
                    "No contacts yet.\n\nTap “➕ Add” while the other device has Velivo messenger open (phone: Velivo browser or keyboard, computer: Velivo for Windows) on the same Wi-Fi.")
                setTextColor(dim); textSize = 15f; setPadding(px(10), px(24), px(10), px(8))
            })
            for (c in k.contacts.sortedByDescending { k.chat(it.id).lastOrNull()?.ts ?: 0 }) {
                val n = k.unread(c.id); val last = k.chat(c.id).lastOrNull(); val on = k.online(c.id)
                val dot = View(a).apply { background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(if (on) green else Color.GRAY) }; layoutParams = LinearLayout.LayoutParams(px(12), px(12)).apply { rightMargin = px(12) } }
                val name = TextView(a).apply {
                    text = android.text.SpannableStringBuilder(c.name).apply {
                        if (c.own && c.device.isNotEmpty() && c.device != c.name) { val st = length; append("  " + c.device); setSpan(android.text.style.ForegroundColorSpan(dim), st, length, 0); setSpan(android.text.style.RelativeSizeSpan(0.75f), st, length, 0) }   // prawdziwe urządzenie
                    }; textSize = 17f; setTextColor(ink); if (n > 0) setTypeface(typeface, android.graphics.Typeface.BOLD); maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END; layoutParams = LinearLayout.LayoutParams(0, -2, 1f) }
                val time = TextView(a).apply { text = last?.let { when_(it.ts) } ?: ""; textSize = 12f; setTextColor(if (n > 0) green else dim) }
                val top = LinearLayout(a).apply { gravity = Gravity.CENTER_VERTICAL; addView(name); addView(time) }
                val snippet = TextView(a).apply {
                    text = last?.let { (if (it.mine) (if (it.read) "✓✓ " else if (it.ok) "✓ " else "… ") else "") + it.text.replace('\n', ' ') } ?: (if (on) t("w sieci", "online") else t("poza siecią", "offline"))
                    textSize = 14f; setTextColor(dim); maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END; layoutParams = LinearLayout.LayoutParams(0, -2, 1f)
                }
                val badge = TextView(a).apply { text = n.toString(); textSize = 12f; setTextColor(Color.BLACK); gravity = Gravity.CENTER; minWidth = px(22); setPadding(px(6), px(1), px(6), px(1)); background = bg(green, 11); visibility = if (n > 0) View.VISIBLE else View.GONE }
                val bottom = LinearLayout(a).apply { gravity = Gravity.CENTER_VERTICAL; addView(snippet); addView(badge) }
                val text = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; layoutParams = LinearLayout.LayoutParams(0, -2, 1f); addView(top); addView(bottom) }
                rows.addView(LinearLayout(a).apply {
                    gravity = Gravity.CENTER_VERTICAL; setPadding(px(14), px(12), px(14), px(12)); background = bg(pal().panel, 14)
                    layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = px(6) }
                    addView(dot)
                    if (on) addView(TextView(a).apply { setText(if (k.viaInternet(c.id)) "🌍" else "🏠"); textSize = 15f; setPadding(0, 0, px(8), 0); contentDescription = if (k.viaInternet(c.id)) t("przez internet", "via internet") else t("sieć domowa", "home network") })
                    addView(text)
                    contentDescription = t("Rozmowa z ", "Chat with ") + c.name + " - " + (if (on) t("w sieci", "online") else t("poza siecią", "offline")) + if (n > 0) ", " + t("nowe: ", "new: ") + n else ""
                    setOnClickListener { openChat(c) }
                    setOnLongClickListener { manage(c); true }
                })
            }
        }
        fill()
        listRefresh = { if (chatWith == null) fill() }
        val close = icon("✕", t("Zamknij", "Close")) { screen?.dismiss() }
        val title = TextView(a).apply {
            text = "💬 " + t("Komunikator Velivo", "Velivo messenger"); textSize = 20f; setTextColor(ink); setTypeface(typeface, android.graphics.Typeface.BOLD)
            maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END; layoutParams = LinearLayout.LayoutParams(0, -2, 1f); setPadding(px(16), 0, px(4), 0)
        }
        lateinit var bell: TextView
        fun bellLabel() { bell.text = if (k.background) "🔔" else "🔕"; bell.contentDescription = t("Wiadomości, gdy Velivo zamknięte: ", "Messages while Velivo is closed: ") + (if (k.background) t("wł.", "on") else t("wył.", "off")); bell.tooltipText = bell.contentDescription }
        bell = icon("🔔", "") { k.background = !k.background; bellLabel(); Toast.makeText(a, bell.contentDescription, Toast.LENGTH_SHORT).show() }
        bellLabel()
        val palette = icon("🎨", t("Motyw komunikatora", "Messenger theme")) { chooseTheme() }
        val head = LinearLayout(a).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(0, px(8), px(8), px(8)); addView(title); addView(palette); addView(bell); addView(close) }
        val actions = LinearLayout(a).apply {
            gravity = Gravity.CENTER; setPadding(px(10), px(8), px(10), px(10))
            addView(pill("➕ " + t("Dodaj kontakt", "Add contact"), t("Dodaj kontakt: w tej sieci albo przez internet (zaproszenie)", "Add a contact: on this network or via internet (invitation)")) { addChoice() }, LinearLayout.LayoutParams(0, px(46), 1f).apply { rightMargin = px(8) })
            addView(pill("📤 " + t("Wyślij pliki", "Send files"), t("Pliki do osoby z komunikatora (w domu i poza domem)", "Files to a messenger contact (at home and away)")) { attachTo = null; pickFiles() }, LinearLayout.LayoutParams(0, px(46), 1f))
        }
        root.removeAllViews()
        root.addView(LinearLayout(a).apply {
            orientation = LinearLayout.VERTICAL
            addView(head); addView(netBar); addView(ScrollView(a).apply { addView(rows); layoutParams = LinearLayout.LayoutParams(-1, 0, 1f) }); addView(actions)
        })
    }

    private fun addChoice() {
        AlertDialog.Builder(a).setTitle("➕ " + t("Dodaj kontakt", "Add contact"))
            .setItems(arrayOf("📶 " + t("W tej sieci (z kodem)", "On this network (with a code)"), "✉ " + t("Zaproś przez internet…", "Invite via internet…"), "🔑 " + t("Mam zaproszenie…", "I have an invitation…"),
                "📶 " + (if (k.newDeviceNotifications) t("Powiadomienia o nowych urządzeniach: wł.", "New device notifications: on") else t("Powiadomienia o nowych urządzeniach: wył.", "New device notifications: off")))) { _, i ->
                when (i) { 0 -> addContact(); 1 -> invite(); 2 -> joinDialog(); else -> { k.newDeviceNotifications = !k.newDeviceNotifications; addChoice() } }
            }.setNegativeButton(android.R.string.cancel, null).show()
    }

    // zaproszenie: WhatsApp / SMS (osobę wybierasz z kontaktów w tamtej aplikacji - Velivo nie czyta książki adresowej) albo inna aplikacja
    private fun sendInvite(code: String) {
        val text = t("Zaproszenie do komunikatora Velivo:\n", "Velivo messenger invitation:\n") + code
        val send = android.content.Intent(android.content.Intent.ACTION_SEND).setType("text/plain").putExtra(android.content.Intent.EXTRA_TEXT, text)
        AlertDialog.Builder(a).setTitle("📤 " + t("Wyślij zaproszenie", "Send invitation"))
            .setItems(arrayOf("💬 WhatsApp", "✉ SMS", "📤 " + t("Inna aplikacja…", "Other app…"))) { _, i ->
                runCatching {
                    when (i) {
                        0 -> a.startActivity(android.content.Intent(send).setPackage("com.whatsapp"))
                        1 -> a.startActivity(android.content.Intent(android.content.Intent.ACTION_SENDTO, Uri.parse("smsto:")).putExtra("sms_body", text))
                        else -> a.startActivity(android.content.Intent.createChooser(send, null))
                    }
                }.onFailure { Toast.makeText(a, t("Nie ma tej aplikacji – wybierz inną", "App not installed – choose another"), Toast.LENGTH_LONG).show(); runCatching { a.startActivity(android.content.Intent.createChooser(send, null)) } }
            }.setNegativeButton(android.R.string.cancel, null).show()
    }

    private fun invite() {
        val code = k.createInvite()
        if (code == null) { AlertDialog.Builder(a).setTitle("✉ " + t("Zaproś przez internet", "Invite via internet"))
            .setMessage(t("Najpierw włącz pośrednika (Velivo na komputerze zawsze włączonym: 🎨 → 🌍 Rozmowy poza domem) i otwórz tu komunikator w domu.", "First turn on the relay (Velivo on the always-on computer: 🎨 → 🌍 Chats away from home) and open the messenger here while at home."))
            .setPositiveButton(android.R.string.ok, null).show(); return }
        AlertDialog.Builder(a).setTitle("✉ " + t("Zaproś przez internet", "Invite via internet"))
            .setMessage(t("Wyślij ten kod osobie, która ma Velivo. Ona wkleja go w komunikatorze: ➕ → 🔑 Mam zaproszenie. U Ciebie pojawi się prośba z kodem – przyjmij, gdy kod się zgadza. Działa raz, przez 7 dni.\n\n", "Send this code to a person who has Velivo. They paste it in the messenger: ➕ → 🔑 I have an invitation. You will get a request with a code – accept if it matches. Works once, for 7 days.\n\n") + code)
            .setPositiveButton("📤 " + t("Wyślij…", "Send…")) { _, _ -> sendInvite(code) }
            .setNeutralButton("📋 " + t("Kopiuj", "Copy")) { _, _ -> (a.getSystemService(Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager).setPrimaryClip(android.content.ClipData.newPlainText("Velivo", code)); Toast.makeText(a, t("Skopiowano", "Copied"), Toast.LENGTH_SHORT).show() }
            .setNegativeButton(t("Zamknij", "Close"), null).show()
    }

    private fun joinDialog() {
        val clip = runCatching { (a.getSystemService(Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager).primaryClip?.getItemAt(0)?.text?.toString() }.getOrNull()
        val e = EditText(a).apply { hint = "VELIVO1:…"; minLines = 2; if (clip?.contains("VELIVO1:") == true) setText(clip.trim()) }
        AlertDialog.Builder(a).setTitle("🔑 " + t("Mam zaproszenie", "I have an invitation")).setMessage(t("Wklej kod zaproszenia:", "Paste the invitation code:")).setView(e)
            .setPositiveButton(t("Połącz", "Connect")) { _, _ ->
                val code = e.text.toString()
                val wait = AlertDialog.Builder(a).setTitle("✉ " + t("Zaproszenie", "Invitation")).setMessage(t("Łączę z zapraszającym…", "Connecting to the inviter…")).show()
                Thread {
                    val r = k.join(code) { c -> a.runOnUiThread { wait.setMessage(t("Kod: $c\n\nZapraszający widzi ten sam kod – czekam, aż przyjmie.", "Code: $c\n\nThe inviter sees the same code – waiting for them to accept.")) } }
                    if (r == "ok") { k.upForce = true }
                    a.runOnUiThread {
                        wait.dismiss()
                        Toast.makeText(a, if (r == "ok") t("✉ Dodano kontakt – możecie rozmawiać przez internet", "✉ Contact added – you can chat over the internet") else t("✉ Nie dodano: ", "✉ Not added: ") + r, Toast.LENGTH_LONG).show()
                        if (r == "ok") show()
                    }
                }.start()
            }.setNegativeButton(android.R.string.cancel, null).show()
    }

    private fun manage(c: Komunikator.Contact) {
        AlertDialog.Builder(a).setTitle(c.name).setItems(arrayOf("✏ " + t("Zmień nazwę", "Rename"), "🗑 " + t("Usuń kontakt i rozmowę", "Delete contact and chat"))) { _, i ->
            if (i == 0) {
                val e = EditText(a).apply { setText(c.name); setSingleLine(); hint = c.device }
                AlertDialog.Builder(a).setTitle(t("Nazwa", "Name")).setMessage(t("Urządzenie: ", "Device: ") + c.device + t("\nPuste pole – nazwa urządzenia", "\nEmpty field – device name")).setView(e)
                    .setPositiveButton(android.R.string.ok) { _, _ -> k.rename(c, e.text.toString()) }
                    .setNegativeButton(android.R.string.cancel, null).show()
            } else AlertDialog.Builder(a).setMessage(t("Usunąć „${c.name}” i całą rozmowę?", "Delete “${c.name}” and the whole chat?"))
                .setPositiveButton(t("Usuń", "Delete")) { _, _ -> k.remove(c) }.setNegativeButton(android.R.string.cancel, null).show()
        }.show()
    }

    // szukanie urządzeń w sieci i wybór jednego
    private fun choosePeer(title: String, then: (Komunikator.Peer) -> Unit) {
        val wait = AlertDialog.Builder(a).setTitle(title).setMessage(t("Szukam urządzeń z Velivo w tej sieci…", "Looking for Velivo devices on this network…")).show()
        Thread {
            val peers = k.find()
            a.runOnUiThread {
                wait.dismiss()
                if (peers.isEmpty()) AlertDialog.Builder(a).setTitle(title)
                    .setMessage(t("Nie znaleziono. Na drugim urządzeniu otwórz komunikator Velivo (albo przeglądarkę Velivo) w tej samej sieci Wi-Fi i spróbuj ponownie.",
                        "Nothing found. Open Velivo messenger (or the Velivo browser) on the other device on the same Wi-Fi and try again."))
                    .setPositiveButton(t("Ponów", "Retry")) { _, _ -> choosePeer(title, then) }.setNegativeButton(android.R.string.cancel, null).show()
                else AlertDialog.Builder(a).setTitle(title).setItems(peers.map { (if (k.contact(it.id) != null) "👤 " else "📱 ") + it.name }.toTypedArray()) { _, i -> then(peers[i]) }
                    .setNegativeButton(android.R.string.cancel, null).show()
            }
        }.start()
    }

    private fun codeDialog(title: String, code: String) = AlertDialog.Builder(a).setTitle(title)
        .setMessage(t("Kod: $code\n\nSprawdź, czy drugie urządzenie pokazuje ten sam kod, i przyjmij tam.", "Code: $code\n\nCheck that the other device shows the same code and accept there.")).show()

    private fun addContact() = choosePeer("➕ " + t("Dodaj kontakt", "Add contact")) { p ->
        var d: AlertDialog? = null
        Thread {
            val r = k.pair(p) { code -> a.runOnUiThread { d = codeDialog("➕ " + p.name, code) } }
            a.runOnUiThread {
                d?.dismiss()
                Toast.makeText(a, if (r == "ok") t("Dodano: ", "Added: ") + p.name else t("Nie dodano: ", "Not added: ") + r, Toast.LENGTH_LONG).show()
                if (r == "ok") k.contact(p.id)?.let { openChat(it) }
            }
        }.start()
    }

    // pliki wybrane w aplikacji -> wybór telefonu -> wysyłka po kolei z kodem
    // 📎 w rozmowie: pliki dla tego kontaktu (wybór w aplikacji, potem sendFiles)
    var attachTo: Komunikator.Contact? = null

    fun sendFiles(uris: List<Uri>) {
        if (uris.isEmpty()) return
        val c = attachTo; attachTo = null
        if (c != null) {
            Thread {
                val known = runCatching { InetAddress.getByName(c.addr.ifEmpty { null } ?: throw Exception()) }.getOrNull()?.takeIf { !it.isLoopbackAddress }
                // 🟢 w sieci i znany adres - od razu, bez szukania
                val addr = (if (k.online(c.id)) known else null) ?: k.find().firstOrNull { it.id == c.id }?.addr ?: known ?: if (k.viaInternet(c.id)) InetAddress.getLoopbackAddress() else null   // poza domem: tunel przez pośrednika
                a.runOnUiThread {
                    if (addr == null) Toast.makeText(a, t("${c.name} nie ma teraz w sieci", "${c.name} is not on the network now"), Toast.LENGTH_LONG).show()
                    else sendFilesTo(uris, Komunikator.Peer(c.id, c.name, addr), c)
                }
            }.start()
            return
        }
        // kontakty (w domu i poza domem) - jak 📎 w rozmowie; inne urządzenie w tej sieci - wyszukanie z kodem
        val cs = k.contacts.sortedByDescending { k.online(it.id) }
        if (cs.isEmpty()) { choosePeer("📤 " + t("Wyślij pliki do…", "Send files to…")) { p -> sendFilesTo(uris, p, k.contact(p.id)) }; return }
        AlertDialog.Builder(a).setTitle("📤 " + t("Wyślij pliki do…", "Send files to…"))
            .setItems((cs.map { (if (k.online(it.id)) "🟢 " else "⚪ ") + it.name } + ("📶 " + t("Inne urządzenie w tej sieci…", "Other device on this network…"))).toTypedArray()) { _, i ->
                if (i < cs.size) { attachTo = cs[i]; sendFiles(uris) } else choosePeer("📤 " + t("Wyślij pliki do…", "Send files to…")) { p -> sendFilesTo(uris, p, k.contact(p.id)) }
            }.setNegativeButton(android.R.string.cancel, null).show()
    }

    private fun sendFilesTo(uris: List<Uri>, p: Komunikator.Peer, c: Komunikator.Contact?) {
            Thread {
                for (u in uris) {
                    val name = runCatching { a.contentResolver.query(u, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c -> if (c.moveToFirst()) c.getString(0) else null } }.getOrNull()
                        ?: u.lastPathSegment ?: "plik"
                    val size = runCatching { a.contentResolver.openAssetFileDescriptor(u, "r")?.use { it.length } ?: -1L }.getOrDefault(-1L).let { s ->
                        if (s >= 0) s else runCatching { a.contentResolver.openInputStream(u)!!.use { st -> var n = 0L; val b = ByteArray(1 shl 16); while (true) { val r = st.read(b); if (r < 0) break; n += r }; n } }.getOrDefault(-1L)
                    }
                    if (size < 0) { a.runOnUiThread { Toast.makeText(a, t("Nie można odczytać: ", "Cannot read: ") + name, Toast.LENGTH_LONG).show() }; continue }
                    var d: AlertDialog? = null
                    if (c != null) a.runOnUiThread { Toast.makeText(a, t("Wysyłam: ", "Sending: ") + name, Toast.LENGTH_SHORT).show() }
                    val r = k.sendFile(p.addr, name, size, { a.contentResolver.openInputStream(u)!! }, { code -> if (c == null) a.runOnUiThread { d = codeDialog("📤 $name → ${p.name}", code) } }, c)
                    a.runOnUiThread {
                        d?.dismiss()
                        Toast.makeText(a, name + ": " + when (r) { "ok" -> t("wysłano", "sent"); "odrzucono" -> t("odrzucono", "declined"); else -> t("błąd - ", "error - ") + r }, Toast.LENGTH_LONG).show()
                    }
                }
            }.start()
    }

    // rozmowa
    fun openChat(c: Komunikator.Contact) {
        val root = page()
        listRefresh = null
        k.markRead(c.id); chatWith = c.id; k.chatOpen = c.id; k.clearNotification(c.id)
        k.sendRead(c)
        val msgs = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(px(8), px(4), px(8), px(4)) }
        val scroll = ScrollView(a).apply { addView(msgs); layoutParams = LinearLayout.LayoutParams(-1, 0, 1f) }
        val fmt = java.text.SimpleDateFormat("HH:mm", Locale.getDefault())
        val dayFmt = java.text.SimpleDateFormat("EEEE, d MMMM", Locale.getDefault())
        fun fill() {
            msgs.removeAllViews()
            var lastDay = ""
            for (m in k.chat(c.id).takeLast(300)) {
                val day = dayFmt.format(java.util.Date(m.ts))
                if (day != lastDay) {
                    lastDay = day
                    msgs.addView(TextView(a).apply {
                        text = if (when_(m.ts).contains(':')) t("Dzisiaj", "Today") else day; textSize = 12f; setTextColor(dim)
                        setPadding(px(10), px(3), px(10), px(3)); background = bg(Color.argb(60, 0, 0, 0), 10)
                        layoutParams = LinearLayout.LayoutParams(-2, -2).apply { gravity = Gravity.CENTER_HORIZONTAL; topMargin = px(10); bottomMargin = px(4) }
                    })
                }
                // zdjęcie: miniatura nad dymkiem; stuknięcie - pełny ekran
                val img = m.path?.takeIf { k.isImage(it) }?.let { thumb(it) }
                if (img != null) msgs.addView(android.widget.ImageView(a).apply {
                    setImageBitmap(img); adjustViewBounds = true; maxWidth = px(240); maxHeight = px(280); scaleType = android.widget.ImageView.ScaleType.FIT_CENTER
                    clipToOutline = true; outlineProvider = object : android.view.ViewOutlineProvider() { override fun getOutline(v: View, o: android.graphics.Outline) { o.setRoundRect(0, 0, v.width, v.height, px(14).toFloat()) } }
                    layoutParams = LinearLayout.LayoutParams(-2, -2).apply { gravity = if (m.mine) Gravity.END else Gravity.START; topMargin = px(6) }
                    contentDescription = t("Zdjęcie: ", "Photo: ") + m.text
                    setOnClickListener { showPhoto(m.path!!) }
                    setOnLongClickListener { msgMenu(c, m); true }
                })
                msgs.addView(TextView(a).apply {
                    val tick = if (m.mine) (if (m.read) "  ✓✓" else if (m.ok) "  ✓" else "  …") else ""
                    text = android.text.SpannableStringBuilder(m.text).apply {
                        append("\n"); val st = length; append(fmt.format(java.util.Date(m.ts)) + tick)
                        setSpan(android.text.style.RelativeSizeSpan(0.75f), st, length, 0)
                        val p = pal(); val base = if (m.mine) p.mineFg else p.dim
                        setSpan(android.text.style.ForegroundColorSpan(if (m.mine && m.read) (if (p.key == "kontrast") Color.rgb(0, 0, 0xCC) else Color.rgb(0x7D, 0xF9, 0xFF)) else Color.argb(0xC0, Color.red(base), Color.green(base), Color.blue(base))), st, length, 0)
                    }
                    textSize = 16f; setTextColor(if (m.mine) pal().mineFg else ink)
                    setPadding(px(12), px(7), px(12), px(7))
                    background = bg(if (m.mine) violet else pal().theirs, 16)
                    layoutParams = LinearLayout.LayoutParams(-2, -2).apply { gravity = if (m.mine) Gravity.END else Gravity.START; topMargin = px(3); if (m.mine) leftMargin = px(48) else rightMargin = px(48) }
                    contentDescription = (if (m.mine) t("Ty: ", "You: ") else c.name + ": ") + m.text + ", " + fmt.format(java.util.Date(m.ts)) +
                        if (m.mine) ", " + (if (m.read) t("przeczytane", "read") else if (m.ok) t("doręczone", "delivered") else t("czeka na doręczenie", "waiting")) else ""
                    if (img != null) setOnClickListener { showPhoto(m.path!!) }
                    else if (m.path != null && java.io.File(m.path!!).exists()) setOnClickListener { openFile(m.path!!) }   // plik: stuknięcie otwiera
                    setOnLongClickListener { msgMenu(c, m); true }
                })
            }
            scroll.post { scroll.fullScroll(View.FOCUS_DOWN) }
        }
        val input = EditText(a).apply {
            hint = t("Wiadomość", "Message"); inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_SENTENCES or InputType.TYPE_TEXT_FLAG_AUTO_CORRECT
            imeOptions = android.view.inputmethod.EditorInfo.IME_ACTION_SEND; setHorizontallyScrolling(false); maxLines = 5; layoutParams = LinearLayout.LayoutParams(0, -2, 1f)
            setTextColor(ink); setHintTextColor(dim); textSize = 16f
            background = GradientDrawable().apply { setColor(pal().input); cornerRadius = px(22).toFloat(); setStroke(px(1), pal().border) }; setPadding(px(16), px(11), px(16), px(11))
        }
        fun round(text: String, tip: String, color: Int, act: () -> Unit) = Button(a).apply {
            this.text = text; textSize = 18f; setTextColor(if (color == violet) pal().mineFg else ink); contentDescription = tip; tooltipText = tip; setPadding(0, 0, 0, 0)
            background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(color) }
            layoutParams = LinearLayout.LayoutParams(px(46), px(46)).apply { leftMargin = px(6); rightMargin = px(6) }
            setOnClickListener { act() }
        }
        fun send() { val s = input.text.toString().trim(); if (s.isEmpty()) return; input.setText(""); k.send(c, s); fill() }
        val clip = round("📎", t("Załącz plik", "Attach file"), pal().button) { attachTo = c; pickFiles() }
        val btn = round("➤", t("Wyślij", "Send"), violet) {   // klawiatura Velivo najpierw poprawia ostatnie słowo, potem wysyłka
            (a.getSystemService(Context.INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager).sendAppPrivateCommand(input, "pl.velivo.slowo", null); input.postDelayed({ send() }, 150) }
        input.setOnEditorActionListener { _, id, _ -> if (id == android.view.inputmethod.EditorInfo.IME_ACTION_SEND) { send(); true } else false }
        val row = LinearLayout(a).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(px(4), px(6), px(4), px(8)); addView(clip); addView(input); addView(btn) }
        val back = icon("←", t("Rozmowy", "Chats"), 22f) { show() }.apply { (layoutParams as LinearLayout.LayoutParams).apply { leftMargin = px(6); rightMargin = px(6) } }
        val title = TextView(a).apply { textSize = 18f; maxLines = 3; ellipsize = android.text.TextUtils.TruncateAt.END; layoutParams = LinearLayout.LayoutParams(0, -2, 1f) }
        fun status() {
            val on = k.online(c.id)
            title.text = android.text.SpannableStringBuilder(c.name).apply {
                if (c.own && c.device.isNotEmpty() && c.device != c.name) { val s0 = length; append("  (" + c.device + ")"); setSpan(android.text.style.RelativeSizeSpan(0.7f), s0, length, 0) }
                append("\n"); val st = length
                val seen = k.lastSeenAt(c.id)?.let { " · " + t("ostatnio ", "last seen ") + when_(it) } ?: ""
                append(if (on) "● " + (if (k.viaInternet(c.id)) t("w sieci · 🌍 przez internet", "online · 🌍 via internet") else t("w sieci · 🏠 sieć domowa", "online · 🏠 home network")) else "● " + t("poza siecią", "offline") + seen + "\n" + t("wiadomości dojdą, gdy wróci", "messages arrive when back"))
                setSpan(android.text.style.ForegroundColorSpan(if (on) green else Color.GRAY), st, length, 0)
                setSpan(android.text.style.RelativeSizeSpan(0.7f), st, length, 0)
            }
            title.setTextColor(ink)
        }
        val head = LinearLayout(a).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(0, px(4), px(8), px(6)); background = ColorDrawableCompat(Color.argb(50, 0, 0, 0)); addView(back); addView(title)
            addView(icon("📹", t("Rozmowa wideo", "Video call")) { openCall(c, true) }) }
        fill(); status(); ringPending(c)
        chatRefresh = { k.markRead(c.id); k.sendRead(c); fill(); status() }
        root.removeAllViews()
        root.addView(LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; addView(head); addView(scroll); addView(row) })
    }

    // przytrzymanie wiadomości: kopiuj, (zdjęcie) zapisz w Galerii, usuń u mnie
    private fun msgMenu(c: Komunikator.Contact, m: Komunikator.Msg) {
        val photo = m.path?.takeIf { k.isImage(it) && java.io.File(it).exists() }
        val file = m.path?.takeIf { !k.isImage(it) && java.io.File(it).exists() && !it.substringAfterLast('/').startsWith("wyslane-") }
        val items = mutableListOf("📋 " + t("Kopiuj", "Copy"))
        if (photo != null) items.add("💾 " + t("Zapisz w Galerii", "Save to Gallery"))
        if (file != null) items.add("💾 " + t("Zapisz w Pobranych", "Save to Downloads"))
        items.add("🗑 " + t("Usuń u mnie", "Delete for me"))
        AlertDialog.Builder(a).setItems(items.toTypedArray()) { _, i ->
            when (items[i].substringAfter(' ')) {
                t("Kopiuj", "Copy") -> { (a.getSystemService(Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager).setPrimaryClip(android.content.ClipData.newPlainText("Velivo", m.text)); Toast.makeText(a, t("Skopiowano", "Copied"), Toast.LENGTH_SHORT).show() }
                t("Zapisz w Galerii", "Save to Gallery") -> saveToGallery(photo!!)
                t("Zapisz w Pobranych", "Save to Downloads") -> Toast.makeText(a, if (toDownloads(file!!) != null) t("Zapisano w Pobranych", "Saved to Downloads") else t("Nie udało się zapisać", "Could not save"), Toast.LENGTH_SHORT).show()
                else -> k.deleteLocal(c, m)
            }
        }.show()
    }

    // plik z rozmowy w Pobranych (raz) - stamtąd otwiera go inna aplikacja
    private val downloaded = HashMap<String, Uri>()
    private fun toDownloads(path: String): Uri? = downloaded[path] ?: runCatching {
        val f = java.io.File(path)
        val v = android.content.ContentValues().apply {
            put(android.provider.MediaStore.Downloads.DISPLAY_NAME, f.name)
            put(android.provider.MediaStore.Downloads.RELATIVE_PATH, android.os.Environment.DIRECTORY_DOWNLOADS)
        }
        val uri = a.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI, v)!!
        a.contentResolver.openOutputStream(uri)!!.use { o -> f.inputStream().use { it.copyTo(o) } }
        uri
    }.getOrNull()?.also { downloaded[path] = it }

    private fun openFile(path: String) {
        val uri = toDownloads(path) ?: run { Toast.makeText(a, t("Nie udało się otworzyć", "Could not open"), Toast.LENGTH_SHORT).show(); return }
        val mime = android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(path.substringAfterLast('.', "").lowercase()) ?: "*/*"
        runCatching { a.startActivity(android.content.Intent(android.content.Intent.ACTION_VIEW).setDataAndType(uri, mime).addFlags(android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION)) }
            .onFailure { Toast.makeText(a, t("Zapisano w Pobranych – brak aplikacji do otwarcia", "Saved to Downloads – no app to open it"), Toast.LENGTH_LONG).show() }
    }

    private fun saveToGallery(path: String) {
        val ok = runCatching {
            val f = java.io.File(path)
            val v = android.content.ContentValues().apply {
                put(android.provider.MediaStore.Images.Media.DISPLAY_NAME, f.name.removePrefix("wyslane-"))
                put(android.provider.MediaStore.Images.Media.RELATIVE_PATH, android.os.Environment.DIRECTORY_PICTURES + "/Velivo")
            }
            val uri = a.contentResolver.insert(android.provider.MediaStore.Images.Media.EXTERNAL_CONTENT_URI, v)!!
            a.contentResolver.openOutputStream(uri)!!.use { o -> f.inputStream().use { it.copyTo(o) } }
        }.isSuccess
        Toast.makeText(a, if (ok) t("Zapisano w Galerii (Obrazy/Velivo)", "Saved to Gallery (Pictures/Velivo)") else t("Nie udało się zapisać", "Could not save"), Toast.LENGTH_SHORT).show()
    }

    // miniatury raz na plik (rozmowa odświeża się często)
    private val thumbs = HashMap<String, android.graphics.Bitmap?>()
    private fun thumb(path: String): android.graphics.Bitmap? = thumbs.getOrPut(path) { decode(path, px(240)) }
    private fun decode(path: String, maxSide: Int): android.graphics.Bitmap? = runCatching {
        val o = android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds = true }
        android.graphics.BitmapFactory.decodeFile(path, o)
        if (o.outWidth <= 0) return null
        var smp = 1; while (maxOf(o.outWidth, o.outHeight) / (smp * 2) >= maxSide) smp *= 2
        android.graphics.BitmapFactory.decodeFile(path, android.graphics.BitmapFactory.Options().apply { inSampleSize = smp })
    }.getOrNull()

    // zdjęcie na cały ekran (stuknięcie zamyka)
    private fun showPhoto(path: String) {
        val bmp = decode(path, maxOf(a.resources.displayMetrics.widthPixels, a.resources.displayMetrics.heightPixels)) ?: return
        val d = android.app.Dialog(a, android.R.style.Theme_Black_NoTitleBar_Fullscreen)
        d.setContentView(android.widget.ImageView(a).apply {
            setImageBitmap(bmp); scaleType = android.widget.ImageView.ScaleType.FIT_CENTER; setBackgroundColor(Color.BLACK)
            contentDescription = t("Zdjęcie – stuknij, aby zamknąć", "Photo – tap to close")
            setOnClickListener { d.dismiss() }
            setOnLongClickListener { saveToGallery(path); true }
        })
        d.show()
    }

    private fun ColorDrawableCompat(c: Int) = android.graphics.drawable.ColorDrawable(c)
}

// usługa w tle: gdy aplikacja zamknięta, komunikator czeka na wiadomości (stałe ciche powiadomienie, jak w innych komunikatorach)
class KomunikatorUsluga : Service() {
    private var lock: android.net.wifi.WifiManager.MulticastLock? = null
    private var net: android.net.ConnectivityManager.NetworkCallback? = null
    override fun onBind(i: Intent?): IBinder? = null
    override fun onStartCommand(i: Intent?, flags: Int, id: Int): Int {
        val k = Komunikator.get(this) { (android.os.Build.MANUFACTURER + " " + android.os.Build.MODEL).trim() }
        runCatching {
            if (Build.VERSION.SDK_INT >= 34) startForeground(41919, k.listenNotification(), android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
            else startForeground(41919, k.listenNotification())
        }.onFailure { stopSelf(); return START_NOT_STICKY }
        // klawiatura w telefonie z przeglądarką Velivo (np. po restarcie usługi): komunikator jest w przeglądarce - tu nic nie nasłuchuje
        started = true
        if (otherOwner(this) || stopAsked) { stopAsked = false; stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); return START_NOT_STICKY }
        if (lock == null) lock = (applicationContext.getSystemService(WIFI_SERVICE) as android.net.wifi.WifiManager).createMulticastLock("velivo-czat").apply { setReferenceCounted(false); acquire() }
        k.listen()
        if (net == null) net = object : android.net.ConnectivityManager.NetworkCallback() {
            // powrót do domowego Wi-Fi (np. po wyjściu z domu): ogłoszenie - nadawcy dosyłają, co czekało
            override fun onAvailable(n: android.net.Network) { k.listen(); k.announce() }
        }.also { cb -> runCatching { getSystemService(android.net.ConnectivityManager::class.java)?.registerNetworkCallback(
            android.net.NetworkRequest.Builder().addTransportType(android.net.NetworkCapabilities.TRANSPORT_WIFI).build(), cb) } }
        return START_STICKY
    }
    override fun onDestroy() {
        net?.let { cb -> runCatching { getSystemService(android.net.ConnectivityManager::class.java)?.unregisterNetworkCallback(cb) } }
        started = false; runCatching { lock?.release() }; super.onDestroy()
    }

    companion object {
        const val BROWSER = "pl.velivo.browser"
        // aplikacja schodzi z ekranu: nasłuch przejmuje usługa (tylko z kontaktami i włączonym odbiorem w tle)
        fun otherOwner(ctx: Context) = ctx.packageName != BROWSER && runCatching { ctx.packageManager.getPackageInfo(BROWSER, 0) }.isSuccess
        fun start(ctx: Context, k: Komunikator) {
            if (k.contacts.isEmpty() || !k.background || otherOwner(ctx)) return
            stopAsked = false
            runCatching { ctx.startForegroundService(Intent(ctx, KomunikatorUsluga::class.java)); asked = true }
        }
        // usługa uruchomiona, a jeszcze nie pokazała powiadomienia - zatrzymanie teraz kończy się błędem Androida; zatrzyma się sama zaraz po starcie
        @Volatile private var asked = false
        @Volatile private var started = false
        @Volatile private var stopAsked = false
        // aplikacja na ekranie: gniazda wracają do aplikacji
        fun stop(ctx: Context, k: Komunikator) {
            k.close()
            if (asked && !started) { stopAsked = true; return }
            asked = false; runCatching { ctx.stopService(Intent(ctx, KomunikatorUsluga::class.java)) }
        }
    }
}

// przyciski w powiadomieniu: „Odpowiedz” (pisane w powiadomieniu) i „Przeczytane” - Velivo się nie otwiera
class KomunikatorAkcja : android.content.BroadcastReceiver() {
    override fun onReceive(ctx: Context, i: Intent) {
        val k = Komunikator.get(ctx) { (Build.MANUFACTURER + " " + Build.MODEL).trim() }
        if (i.action == NEW_OFF) { k.newDeviceNotifications = false; ctx.getSystemService(NotificationManager::class.java)?.cancel("nowe-urz".hashCode()); return }
        val c = k.contact(i.getStringExtra("id") ?: return) ?: return
        when (i.action) {
            READ -> { k.markRead(c.id); k.clearNotification(c.id) }
            REPLY -> {
                val text = android.app.RemoteInput.getResultsFromIntent(i)?.getCharSequence(TEXT)?.toString()?.trim().orEmpty()
                if (text.isEmpty()) return
                val done = goAsync()
                // aplikacja zamknięta i usługa nie działa - na chwilę własne gniazda, żeby wiadomość wyszła
                if (k.idle) k.listen()
                k.send(c, text); k.markRead(c.id)
                k.notifyMessage(c, quiet = true)   // odpowiedź widać w powiadomieniu, bez ponownego dźwięku
                android.os.Handler(android.os.Looper.getMainLooper()).postDelayed({ done.finish() }, 8000)
            }
        }
    }
    companion object {
        const val REPLY = "pl.velivo.komunikator.ODPOWIEDZ"
        const val READ = "pl.velivo.komunikator.PRZECZYTANE"
        const val NEW_OFF = "pl.velivo.komunikator.NOWE_WYL"
        const val TEXT = "tekst"
    }
}
