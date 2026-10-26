package pl.velivo.komunikator

// Jeden plik komunikatora dla przeglądarki Velivo i klawiatury Velivo (wspólne źródło w android/komunikator) - poprawka w jednym miejscu.
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
    class Contact(val id: String, var name: String, val key: ByteArray, var addr: String, var device: String = name, var own: Boolean = false, var relay: String? = null) {
        val isGroup get() = key.isEmpty()   // 👥 grupa: bez klucza, członkowie w addr („grupa:id,id”)
    }
    // read: moja - odbiorca przeczytał (✓✓); odebrana - wysłaliśmy „przeczytane”
    // file: wpis o pliku (nie wysyłany ponownie jako tekst)
    class Msg(val mine: Boolean, var text: String, val ts: Long, var ok: Boolean, val mid: String, var read: Boolean = false, var file: Boolean = false, var path: String? = null, var re: String = "")   // re: reakcje „id:emoji,…”

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
            (0 until a.length()).map { a.getJSONObject(it) }.map { Msg(it.optBoolean("me"), it.optString("t"), it.optLong("ts"), it.optBoolean("ok", true), it.optString("mid"), it.optBoolean("rd"), it.optBoolean("f"), it.optString("p").ifEmpty { null }, it.optString("re")) }.toMutableList()
        }.getOrDefault(mutableListOf())
    }
    @Synchronized private fun saveChat(id: String) {
        val l = chat(id); while (l.size > 500) l.removeAt(0)
        ttl(id).takeIf { it > 0 }?.let { h ->   // ⏳ znikające wiadomości: starsze niż ustawiony czas - usunięte (z kopiami plików w Velivo)
            val old = System.currentTimeMillis() - h * 3600000L
            l.removeAll { m -> (m.ts < old).also { gone -> if (gone) m.path?.let { p -> if (p.startsWith(mediaDir.path)) java.io.File(p).delete() } } }
        }
        val a = JSONArray(); l.forEach { a.put(JSONObject().put("me", it.mine).put("t", it.text).put("ts", it.ts).put("ok", it.ok).put("mid", it.mid).put("rd", it.read).put("f", it.file).put("p", it.path ?: "").put("re", it.re)) }
        prefs.edit().putString("rozmowa-$id", seal(a.toString())).apply()
    }
    // --- przeniesienie klawiatura -> przeglądarka (telefon z obiema aplikacjami: jeden komunikator, bez dublowania) ---
    @Synchronized fun export(): String = JSONObject().put("cid", myId)
        .put("contacts", JSONArray().also { a -> contacts.forEach { a.put(JSONObject().put("id", it.id).put("name", it.name).put("key", b64(it.key)).put("addr", it.addr).put("dev", it.device).put("own", it.own).put("relay", it.relay ?: "")) } })
        .put("chats", JSONObject().also { o -> contacts.forEach { c -> o.put(c.id, JSONArray().also { a -> chat(c.id).forEach { m -> a.put(JSONObject().put("me", m.mine).put("t", m.text).put("ts", m.ts).put("ok", m.ok).put("mid", m.mid).put("rd", m.read).put("f", m.file).put("p", m.path ?: "").put("re", m.re)) } }) } })
        .put("unread", JSONObject().also { o -> contacts.forEach { o.put(it.id, unread(it.id)) } }).put("opt", optExport()).toString()

    // ustawienia rozmów i profilu (przypięte, wyciszone, znikające, szkice, motyw, blokada, „Mój adres”, zdjęcie) - przy przenosinach i w kopii
    private val optKeys = listOf("motyw", "tlo", "blokada", "autoimg", "pin-", "cisza-", "znikaj-", "szkic-", "moj-adres", "glos-x")
    private fun optExport() = JSONObject().also { o ->
        prefs.all.forEach { (n, v) -> if (v != null && optKeys.any { n.startsWith(it) }) o.put(n, v) }
        photo(myId)?.let { o.put("foto", b64(it.readBytes())) }
    }
    private fun optImport(o: JSONObject?) {
        o ?: return; val e = prefs.edit()
        o.keys().forEach { n -> runCatching { when {
            n.startsWith("cisza-") -> e.putLong(n, o.getLong(n)); n.startsWith("znikaj-") -> e.putInt(n, o.getInt(n)); n == "glos-x" -> e.putFloat(n, o.getDouble(n).toFloat())
            o.get(n) is Boolean -> e.putBoolean(n, o.getBoolean(n)); n != "foto" -> e.putString(n, o.getString(n))
        } } }
        unb64(o.optString("foto")).takeIf { it.isNotEmpty() }?.let { java.io.File(mediaDir, "profil-$myId.jpg").writeBytes(it); e.putLong("profil-v", System.currentTimeMillis()) }
        e.apply()
    }

    // 💾 kopia zapasowa (jak w WhatsApp, ale u Ciebie): kontakty z kluczami, rozmowy, identyfikator i ustawienia - zaszyfrowane hasłem
    // (PBKDF2 + AES-GCM); bez plików z rozmów. Przywrócenie na nowym telefonie - kontakty dalej nas znają (ten sam identyfikator).
    private fun passKey(p: String, salt: ByteArray) = javax.crypto.SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(javax.crypto.spec.PBEKeySpec(p.toCharArray(), salt, 200000, 256)).encoded
    fun backup(pass: String): ByteArray {
        val salt = ByteArray(16).also { random.nextBytes(it) }; val n = ByteArray(12).also { random.nextBytes(it) }
        val ci = Cipher.getInstance("AES/GCM/NoPadding"); ci.init(Cipher.ENCRYPT_MODE, SecretKeySpec(passKey(pass, salt), "AES"), GCMParameterSpec(128, n))
        return ("VELIVO-KOPIA1\n" + b64(salt + n + ci.doFinal(export().toByteArray(Charsets.UTF_8)))).toByteArray(Charsets.UTF_8)
    }
    fun restore(data: ByteArray, pass: String): Boolean = runCatching {
        val txt = String(data, Charsets.UTF_8); require(txt.startsWith("VELIVO-KOPIA1\n"))
        val b = unb64(txt.substringAfter('\n').trim()); require(b.size > 44)
        val ci = Cipher.getInstance("AES/GCM/NoPadding"); ci.init(Cipher.DECRYPT_MODE, SecretKeySpec(passKey(pass, b.copyOfRange(0, 16)), "AES"), GCMParameterSpec(128, b, 16, 12))
        val json = String(ci.doFinal(b, 28, b.size - 28), Charsets.UTF_8); require(JSONObject(json).optString("cid").length == 32)   // kopia z telefonu
        wipe(); import(json); listen(); announce(); true
    }.getOrDefault(false)

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
                    if (have.add(m.optString("mid"))) list.add(Msg(m.optBoolean("me"), m.optString("t"), m.optLong("ts"), m.optBoolean("ok", true), m.optString("mid"), m.optBoolean("rd"), m.optBoolean("f"), m.optString("p").ifEmpty { null }, m.optString("re")))
                }
                list.sortBy { it.ts }
                val un = o.optJSONObject("unread")?.optInt(id) ?: 0
                if (un > 0) prefs.edit().putInt("nowe-$id", unread(id) + un).apply()
            }
        }
        optImport(o.optJSONObject("opt"))
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
        val out: (ByteArray, InetAddress) -> Unit = if (own != null) { b, to -> own.send(DatagramPacket(b, b.size, to, PORT)); own.send(DatagramPacket(b, b.size, to, OLD_PORT)) } else udpOut ?: return   // nowe i starsze Velivo
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
        // przez pośrednika: od niego wprost albo przekazany przez niego do domowego Wi-Fi („fwd”) - kontakt poza domem (🌍)
        val net = viaRelay || p.optBoolean("fwd")
        if (net) viaNet[from] = true else { lanSeen[from] = System.currentTimeMillis(); viaNet.remove(from) }
        // stara wiadomość (czekała u nadawcy albo na pośredniku) - przyjęta, bez zmiany obecności; inne stare pakiety - pominięte
        if (Math.abs(p.optLong("ts") - System.currentTimeMillis()) > 600000) { if (t == "p2p-msg") message(p, sender); return true }
        if (t == "p2p-bye") { contact(from)?.let { c -> if (lastSeen.remove(c.id) != null) main.post { onChange() } }; return true }   // zamknął Velivo - od razu poza siecią
        if (!net && (t == "p2p-here" || t == "p2p-who") && contact(from) == null) notifyNewDevice(from, clean(p.optString("device")))
        contact(from)?.let { c ->
            val was = online(c.id); lastSeen[c.id] = System.currentTimeMillis(); markSeen(c.id)
            if (t == "p2p-here" && !viaRelay && c.addr != sender.hostAddress) { c.addr = sender.hostAddress ?: c.addr; saveContacts() }   // aktualny adres
            // prawdziwa nazwa urządzenia (zmieniona na tamtym telefonie / komputerze); własna nazwa kontaktu zostaje
            val real = clean(p.optString("device"))
            if (real.isNotEmpty() && real != c.device) { c.device = real; if (!c.own) c.name = real; saveContacts(); main.post { onChange() } }
            // 🌍 adres pośrednika kontaktu (np. NUCBOX z No-IP) - poza domem rozmowy idą przez niego
            val rl = p.optString("relay")
            if (t == "p2p-here" && rl.isNotEmpty() && rl.length < 200 && ':' in rl && rl != c.relay) { c.relay = rl; saveContacts() }
            if (!was) main.post { onChange() }
        }
        if (t != "p2p-ack" && t != "p2p-msg" && t != "p2p-typing") contact(from)?.let { c -> Thread { resend(c); sendPhoto(c) }.start() }   // 🖼 zdjęcie profilowe - gdy tylko kontakt się pojawi
        when (t) {
            "p2p-who" -> sendUdp(packet("p2p-here"), sender)
            "p2p-typing" -> if (p.optString("to") == myId && contact(from) != null) { typingAt[from] = System.currentTimeMillis(); main.post { onTyping(from) } }   // ✍ „pisze…”
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

    // 🚫 usuń u wszystkich (jak w WhatsApp): u mnie i u odbiorców zostaje „Wiadomość usunięta”
    fun deleteForAll(c: Contact, m: Msg) {
        if (!m.mine) return
        m.text = "🚫 " + (if (pl) "Usunięto tę wiadomość" else "You deleted this message"); m.path?.let { p -> if (p.startsWith(mediaDir.path)) java.io.File(p).delete() }; m.path = null; m.ok = true
        saveChat(c.id); main.post { onChange() }
        val body = JSONObject().put("m", m.mid).apply { if (c.isGroup) put("g", c.id) }.toString()
        val to = if (c.isGroup) members(c).mapNotNull { contact(it)?.takeIf { x -> !x.isGroup } } else listOf(c)
        to.forEach { hidden(it, DEL + body) }
    }
    private fun delIn(from: Contact, body: String) {
        val j = runCatching { JSONObject(body) }.getOrNull() ?: return
        val chatId = j.optString("g").takeIf { it.length == 32 && contact(it)?.isGroup == true } ?: from.id
        val m = synchronized(this) { chat(chatId).firstOrNull { it.mid == j.optString("m") && !it.mine } } ?: return
        m.text = (if (chatId != from.id) from.name + ": " else "") + "🚫 " + (if (pl) "Wiadomość usunięta" else "This message was deleted")
        m.path?.let { p -> if (p.startsWith(mediaDir.path)) java.io.File(p).delete() }; m.path = null
        saveChat(chatId); main.post { onChange() }
    }

    // ✏ poprawka, 😀 reakcja - jak usuwanie: do rozmówcy albo do każdego w grupie (wiadomość wskazana jej id)
    private fun tagOut(c: Contact, tag: String, body: JSONObject) {
        if (c.isGroup) body.put("g", c.id)
        (if (c.isGroup) members(c).mapNotNull { contact(it)?.takeIf { x -> !x.isGroup } } else listOf(c)).forEach { hidden(it, tag + body) }
    }
    private fun chatOf(from: Contact, j: JSONObject) = j.optString("g").takeIf { it.length == 32 && contact(it)?.isGroup == true } ?: from.id
    fun edit(c: Contact, m: Msg, text: String) {
        if (!m.mine || m.file) return
        m.text = text.take(MAX_TEXT) + " ✏"; saveChat(c.id); main.post { onChange() }
        tagOut(c, EDIT, JSONObject().put("m", m.mid).put("t", text.take(MAX_TEXT)))
    }
    private fun editIn(from: Contact, body: String) {
        val j = runCatching { JSONObject(body) }.getOrNull() ?: return; val id = chatOf(from, j)
        val m = synchronized(this) { chat(id).firstOrNull { it.mid == j.optString("m") && !it.mine && !it.file } } ?: return
        m.text = ((if (id != from.id) from.name + ": " else "") + j.optString("t")).take(MAX_TEXT) + " ✏"
        saveChat(id); main.post { onChange() }
    }
    fun react(c: Contact, m: Msg, e: String) {
        val e2 = if (m.re.split(',').contains("$myId:$e")) "" else e   // ta sama reakcja ponownie - zdjęta
        m.re = reSet(m.re, myId, e2); saveChat(c.id); main.post { onChange() }
        tagOut(c, REACT, JSONObject().put("m", m.mid).put("e", e2))
    }
    private fun reactIn(from: Contact, body: String) {
        val j = runCatching { JSONObject(body) }.getOrNull() ?: return; val id = chatOf(from, j)
        val m = synchronized(this) { chat(id).firstOrNull { it.mid == j.optString("m") } } ?: return
        m.re = reSet(m.re, from.id, j.optString("e").take(8)); saveChat(id); main.post { onChange() }
    }
    private fun reSet(re: String, id: String, e: String) = (re.split(',').filter { it.length > 33 && !it.startsWith("$id:") } + listOfNotNull(e.ifEmpty { null }?.let { "$id:$it" })).joinToString(",")
    fun reactions(m: Msg) = m.re.split(',').filter { it.length > 33 }.joinToString("") { it.substring(33) }
    // ↩ odpowiedź: pierwszy wiersz cytowanej wiadomości (bez jej cytatu) - czytelne także w starszym Velivo
    fun quote(m: Msg) = "› " + (m.text.lines().firstOrNull { !it.startsWith("› ") } ?: "").removeSuffix(" ✏").take(70) + "\n"

    // 🖼 moje zdjęcie profilowe (małe) - u kontaktów zamiast kropki; nowe dostaje każdy, kto się odezwie
    fun photo(id: String) = java.io.File(mediaDir, "profil-$id.jpg").takeIf { it.exists() }
    fun setPhoto(jpg: ByteArray?) {
        val f = java.io.File(mediaDir, "profil-$myId.jpg"); if (jpg == null) f.delete() else f.writeBytes(jpg)
        prefs.edit().putLong("profil-v", System.currentTimeMillis()).apply()
        contacts.filter { !it.isGroup }.forEach { sendPhoto(it) }
    }
    private fun sendPhoto(c: Contact) {
        val v = prefs.getLong("profil-v", 0); if (v == 0L || prefs.getLong("profil-${c.id}", 0) == v) return
        prefs.edit().putLong("profil-${c.id}", v).apply()
        hidden(c, PHOTO + JSONObject().put("p", photo(myId)?.readBytes()?.let { b64(it) } ?: ""))
    }
    private fun photoIn(from: Contact, body: String) {
        val p = unb64(runCatching { JSONObject(body).optString("p") }.getOrDefault("")); val f = java.io.File(mediaDir, "profil-${from.id}.jpg")
        if (p.isEmpty()) f.delete() else if (p.size < 60000) f.writeBytes(p)
        main.post { onChange() }
    }

    // 📌 przypięte, 🔕 wyciszone (do kiedy), ✏ szkic - ustawienia rozmowy w tym urządzeniu
    fun pinned(id: String) = prefs.getBoolean("pin-$id", false)
    fun setPinned(id: String, on: Boolean) { prefs.edit().putBoolean("pin-$id", on).apply(); main.post { onChange() } }
    fun muted(id: String) = prefs.getLong("cisza-$id", 0) > System.currentTimeMillis()
    fun mute(id: String, ms: Long) { prefs.edit().putLong("cisza-$id", if (ms <= 0) 0 else if (ms == Long.MAX_VALUE) ms else System.currentTimeMillis() + ms).apply(); main.post { onChange() } }
    fun draft(id: String) = prefs.getString("szkic-$id", "") ?: ""
    fun setDraft(id: String, s: String) { if (s != draft(id)) prefs.edit().putString("szkic-$id", s).apply() }
    fun tidy(id: String) = saveChat(id)
    // ⏳ znikające wiadomości (jak w WhatsApp): czas ustawiony w rozmowie dotyczy obu stron
    fun ttl(id: String) = prefs.getInt("znikaj-$id", 0)
    fun setTtl(c: Contact, h: Int) { applyTtl(c.id, h, null); tagOut(c, TIMER, JSONObject().put("h", h)) }
    private fun applyTtl(id: String, h: Int, who: String?) {
        prefs.edit().putInt("znikaj-$id", h).apply()
        val what = if (h <= 0) (if (pl) "wyłączone" else "off") else if (h % 24 == 0) "${h / 24} " + (if (pl) "d." else "d") else "$h h"
        synchronized(this) { chat(id).add(Msg(who == null, "⏳ " + (who?.let { "$it: " } ?: "") + (if (pl) "znikające wiadomości – " else "disappearing messages – ") + what, System.currentTimeMillis(), true, UUID.randomUUID().toString().replace("-", ""))) }
        saveChat(id); main.post { onChange() }
    }
    private fun timerIn(from: Contact, body: String) {
        val j = runCatching { JSONObject(body) }.getOrNull() ?: return
        applyTtl(chatOf(from, j), j.optInt("h").coerceIn(0, 24 * 365), from.name)
    }
    // 🛡 kod bezpieczeństwa: ten sam na obu urządzeniach (z klucza rozmowy) - zgodny = nikt się nie podszywa
    fun safety(c: Contact): String {
        val h = MessageDigest.getInstance("SHA-256").digest("velivo-kod\n".toByteArray(Charsets.UTF_8) + c.key)
        return (0 until 6).joinToString(" ") { i -> "%05d".format(Locale.ROOT, ((h[i * 4].toLong() and 0xff shl 24) or (h[i * 4 + 1].toLong() and 0xff shl 16) or (h[i * 4 + 2].toLong() and 0xff shl 8) or (h[i * 4 + 3].toLong() and 0xff)) % 100000) }
    }

    fun deleteLocal(c: Contact, m: Msg) {
        synchronized(this) { chat(c.id).remove(m) }; saveChat(c.id)
        m.path?.let { p -> if (p.startsWith(mediaDir.path)) java.io.File(p).delete() }   // kopia pliku w Velivo (nie ta z Pobranych / Galerii)
        main.post { onChange() }
    }

    // „jestem w sieci” - po powrocie do domowego Wi-Fi kontakty od razu dosyłają zaległe wiadomości
    fun announce() = Thread { repeat(3) { sendUdp(packet("p2p-here"), null); Thread.sleep(1500) }; contacts.toList().forEach { resend(it) } }.start()

    // „kto jest w sieci?” - kontakty odpowiadają, status 🟢 od razu prawdziwy
    // 🔋 pytanie każdego adresu (/24, ~500 pakietów) najwyżej co 2 min i tylko, gdy ktoś z kontaktów jest poza siecią - Wi-Fi nie budzi się co 30 s
    @Volatile private var sweptAt = 0L
    fun ping() = Thread {
        sendUdp(packet("p2p-here"), null)
        if (System.currentTimeMillis() - sweptAt > 120000L && runCatching { contacts.toList().any { !it.isGroup && !online(it.id) } }.getOrDefault(true)) { sweptAt = System.currentTimeMillis(); sweep(packet("p2p-who")) }
        repeat(2) { sendUdp(packet("p2p-who"), null); Thread.sleep(700) }
    }.start()
    // 🔎 pytanie do każdego adresu domowej sieci (/24) osobno - działa też tam, gdzie telefon lub router gubi rozgłoszenia (Oppo / ColorOS)
    private fun sweep(p: JSONObject) {
        val own = udp ?: return
        val d = p.toString().toByteArray(Charsets.UTF_8)
        runCatching {
            for (ni in NetworkInterface.getNetworkInterfaces()) {
                if (!ni.isUp || ni.isLoopback) continue
                for (ia in ni.interfaceAddresses) {
                    val a = ia.address as? java.net.Inet4Address ?: continue
                    if (!a.isSiteLocalAddress) continue   // tylko sieć domowa (nie dane komórkowe)
                    val b = a.address
                    for (i in 1..254) if (i != (b[3].toInt() and 0xff)) runCatching {
                        val t = InetAddress.getByAddress(byteArrayOf(b[0], b[1], b[2], i.toByte()))
                        own.send(DatagramPacket(d, d.size, t, PORT)); own.send(DatagramPacket(d, d.size, t, OLD_PORT))
                    }
                }
            }
        }
    }

    @Synchronized fun contact(id: String) = contacts.firstOrNull { it.id == id }

    // urządzenia z Velivo (telefony i komputery) w tej sieci - ok. 2,5 s
    fun find(): List<Peer> {
        found.clear()
        sweep(packet("p2p-who"))
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
        if (!seen.add(mid) || synchronized(this) { chat(c.id).any { it.mid == mid } }) return   // powtórzenie (także po ponownym uruchomieniu) (np. potwierdzenie nie doszło) - tylko ponowne ack
        if (seen.size > 2000) synchronized(seen) { seen.iterator().let { it.next(); it.remove() } }
        if (!sender.isLoopbackAddress && c.addr != sender.hostAddress) { c.addr = sender.hostAddress ?: c.addr; saveContacts() }
        if (text.startsWith(CALL)) { val o = runCatching { JSONObject(text.substring(CALL.length)) }.getOrNull() ?: return; onCallSignal(c, o); return }
        typingAt.remove(c.id)
        if (text.startsWith(GROUP)) { groupIn(c, text.substring(GROUP.length), p.optLong("ts"), mid); return }   // 👥 wiadomość w grupie
        if (text.startsWith(CARD)) { cardIn(c, text.substring(CARD.length)); return }   // 👤 kontakt przedstawiony przez znajomego
        if (text.startsWith(DEL)) { delIn(c, text.substring(DEL.length)); return }   // 🚫 nadawca usunął wiadomość u wszystkich
        if (text.startsWith(EDIT)) { editIn(c, text.substring(EDIT.length)); return }   // ✏ nadawca poprawił wiadomość
        if (text.startsWith(REACT)) { reactIn(c, text.substring(REACT.length)); return }   // 😀 reakcja na wiadomość
        if (text.startsWith(PHOTO)) { photoIn(c, text.substring(PHOTO.length)); return }   // 🖼 zdjęcie profilowe kontaktu
        if (text.startsWith(TIMER)) { timerIn(c, text.substring(TIMER.length)); return }   // ⏳ znikające wiadomości
        if (text.startsWith(ASK)) { askIn(c, text.substring(ASK.length)); return }   // 🔗 prośba z „Mojego adresu” (przez pośrednika)
        if (text.startsWith(ADDR) || text.startsWith(ASKOK)) return   // dla pośrednika (Windows)
        val m = Msg(false, text.take(MAX_TEXT), p.optLong("ts"), true, mid)
        synchronized(this) { chat(c.id).add(m) }
        saveChat(c.id)
        prefs.edit().putInt("nowe-${c.id}", unread(c.id) + 1).apply()
        notifyMessage(c)
        main.post { onMessage(c, m); onChange() }
        Thread { resend(c); sendPhoto(c) }.start()   // odezwał się - może czekają nasze niedoręczone
    }

    // wysłanie (w tle): najpierw na znany adres, potem do całej sieci; 3 próby
    fun send(c: Contact, text: String): Msg {
        val mid = UUID.randomUUID().toString().replace("-", "")
        val m = Msg(true, text.take(MAX_TEXT), System.currentTimeMillis(), false, mid)
        synchronized(this) { chat(c.id).add(m) }
        if (c.isGroup) { groupOut(c, m.text, m.mid); m.ok = true }   // 👥 do każdego z grupy osobno (szyfrowane jego kluczem)
        saveChat(c.id)
        if (!c.isGroup) deliver(c, m)
        return m
    }

    // 👥 grupy (jak w Signal: bez serwera grup) - wiadomość idzie do każdego członka osobno, z nazwą i składem grupy;
    // u odbiorcy grupa tworzy się sama; członkowie, których ktoś nie ma w kontaktach, dostają ją od innych
    fun members(g: Contact) = g.addr.removePrefix("grupa:").split(',').filter { it.length == 32 && it != myId }
    fun newGroup(name: String, ids: List<String>): Contact {
        val g = Contact(UUID.randomUUID().toString().replace("-", ""), name, ByteArray(0), "grupa:" + ids.joinToString(","), "👥", own = true)
        synchronized(this) { contacts.add(g) }; saveContacts(); main.post { onChange() }
        return g
    }
    // 👤 przedstawienie: dwie osoby z moich kontaktów (znane z sieci domowej, teraz choćby poza domem) dostają siebie nawzajem
    // jako kontakty - ze wspólnym, nowym kluczem; wizytówki idą zaszyfrowane kluczem każdej z nich
    fun introduce(ids: List<String>) {
        // + moi pośrednicy (np. NUCBOX) - żeby nowi znajomi mogli się łączyć także przez internet
        val cs = (ids.mapNotNull { contact(it) } + synchronized(this) { contacts.filter { it.relay != null } }).filter { !it.isGroup }.distinctBy { it.id }
        for (i in cs.indices) for (j in i + 1 until cs.size) {
            val key = ByteArray(32).also { random.nextBytes(it) }
            fun card(c: Contact) = JSONObject().put("id", c.id).put("name", c.name).put("dev", c.device).put("key", b64(key)).put("relay", c.relay ?: "").toString()
            deliver(cs[i], Msg(true, CARD + card(cs[j]), System.currentTimeMillis(), false, UUID.randomUUID().toString().replace("-", "")))
            deliver(cs[j], Msg(true, CARD + card(cs[i]), System.currentTimeMillis(), false, UUID.randomUUID().toString().replace("-", "")))
        }
    }
    // prośba o kontakt od znajomego: czeka na zgodę (także gdy Velivo w tle / telefon poza domem) - okno przy otwarciu komunikatora
    private fun cardIn(from: Contact, body: String) {
        val j = runCatching { JSONObject(body) }.getOrNull() ?: return
        val id = j.optString("id"); val key = unb64(j.optString("key"))
        if (id.length != 32 || id == myId || key.size != 32 || contact(id) != null) return
        val list = cardList(); if ((0 until list.length()).any { JSONObject(list.getString(it)).optJSONObject("c")?.optString("id") == id }) return
        list.put(JSONObject().put("f", from.id).put("c", j).toString()); prefs.edit().putString("prosby-kontakt", list.toString()).apply()
        notifyPair(clean(j.optString("name")) + (if (pl) " (poleca: ${from.name})" else " (from: ${from.name})"))
        main.post { onChange(); onCards() }
    }
    var onCards: () -> Unit = {}

    // 🔗 „Mój adres Velivo” (jak numer w WhatsApp): link, który można dać każdemu; prośba idzie przez mojego pośrednika (NUCBOX),
    // ja decyduję Dodaj / Odrzuć. Nowa osoba dostaje tylko komunikator (sieć domowa, filmy, synchronizacja - nie).
    private fun hidden(c: Contact, text: String) = deliver(c, Msg(true, text, System.currentTimeMillis(), false, UUID.randomUUID().toString().replace("-", "")))
    private fun myRelay() = synchronized(this) { contacts.firstOrNull { it.relay != null && !it.isGroup } }
    fun myAddress(reset: Boolean = false): String? {
        val r = myRelay() ?: return null
        var sk = prefs.getString("moj-adres", null)
        if (sk == null || reset) { sk = b64(ByteArray(24).also { random.nextBytes(it) }); prefs.edit().putString("moj-adres", sk).apply() }
        hidden(r, ADDR + sk)   // pośrednik zna sekret - wpuszcza tylko prośby z tym linkiem
        val j = JSONObject().put("a", r.relay).put("o", myId).put("s", sk).put("n", deviceName())
        return "VELIVO-KONTAKT:" + Base64.encodeToString(j.toString().toByteArray(Charsets.UTF_8), Base64.NO_WRAP or Base64.URL_SAFE)
    }
    // prośba od kogoś z moim linkiem - czeka na zgodę (okno przy otwarciu komunikatora)
    private fun askIn(relay: Contact, body: String) {
        val j = runCatching { JSONObject(body) }.getOrNull() ?: return
        val id = j.optString("id"); if (id.length != 32 || contact(id) != null) return
        val l = askList(); if ((0 until l.length()).any { JSONObject(l.getString(it)).optString("id") == id }) return
        l.put(j.put("r", relay.id).toString()); prefs.edit().putString("prosby-adres", l.toString()).apply()
        notifyPair(clean(j.optString("name")))
        main.post { onChange(); onCards() }
    }
    private fun askList() = runCatching { JSONArray(prefs.getString("prosby-adres", "[]")) }.getOrDefault(JSONArray())
    fun nextAsk(): String? { val l = askList(); return if (l.length() == 0) null else clean(JSONObject(l.getString(0)).optString("name")).ifEmpty { "Velivo" } }
    fun answerAsk(yes: Boolean) {
        val l = askList(); if (l.length() == 0) return
        val j = JSONObject(l.getString(0)); l.remove(0); prefs.edit().putString("prosby-adres", l.toString()).apply()
        val relay = contact(j.optString("r")) ?: return; val id = j.optString("id")
        if (!yes) { hidden(relay, ASKOK + JSONObject().put("id", id).put("ok", false)); return }
        val pub = unb64(j.optString("pub")); if (pub.isEmpty() || contact(id) != null) return
        val pair = ecPair(); val myPub = pair.public.encoded
        val key = runCatching { agree(pair, pub, "velivo-prosba", pub, myPub) }.getOrNull() ?: return
        val name = clean(j.optString("name")).ifEmpty { "Velivo" }
        synchronized(this) { contacts.add(Contact(id, name, key, "", name, false, relay.relay)) }
        saveContacts()
        hidden(relay, ASKOK + JSONObject().put("id", id).put("ok", true).put("pub", b64(myPub)).put("name", deviceName()))
        main.post { onChange() }
    }
    // ja proszę: link od kogoś - prośba przez jego pośrednika; odpowiedź odbieram co minutę (on może być poza siecią)
    fun requestByLink(link: String): Boolean {
        val j = runCatching { JSONObject(String(Base64.decode(link.trim().substringAfter("VELIVO-KONTAKT:").trim(), Base64.URL_SAFE), Charsets.UTF_8)) }.getOrNull() ?: return false
        if (j.optString("o").length != 32 || j.optString("o") == myId || !j.optString("a").contains(':') || j.optString("s").isEmpty()) return false
        val pair = ecPair()
        j.put("priv", b64(pair.private.encoded)).put("pub", b64(pair.public.encoded))
        val l = myAsks(); l.put(j.toString()); prefs.edit().putString("moje-prosby", l.toString()).apply()
        Thread { pollAsks() }.start()
        return true
    }
    private fun myAsks() = runCatching { JSONArray(prefs.getString("moje-prosby", "[]")) }.getOrDefault(JSONArray())
    @Synchronized fun pollAsks() {
        val l = myAsks(); if (l.length() == 0) return
        val keep = JSONArray()
        for (i in 0 until l.length()) {
            val j = JSONObject(l.getString(i)); var done = false
            runCatching {
                val hp = j.optString("a"); val sk = unb64(j.optString("s")); val ts = System.currentTimeMillis()
                java.net.Socket().use { so ->
                    so.connect(InetSocketAddress(hp.substringBeforeLast(':'), hp.substringAfterLast(':').toInt()), 12000); so.soTimeout = 15000
                    val out = so.getOutputStream(); val inp = DataInputStream(so.getInputStream())
                    val hello = JSONObject().put("Type", "relay-ask").put("Id", myId).put("To", j.optString("o")).put("Pub", j.optString("pub")).put("Name", deviceName()).put("Ts", ts)
                    writeBlock(out, hello.toString().toByteArray(Charsets.UTF_8))
                    writeBlock(out, hmac(sk, "velivo-ask\n$myId\n${j.optString("o")}\n${j.optString("pub")}\n$ts".toByteArray(Charsets.UTF_8)))
                    val rb = readBlock(inp, 8192) ?: return@use; val mac = readBlock(inp, 64) ?: return@use
                    if (!MessageDigest.isEqual(mac, hmac(sk, String(rb, Charsets.UTF_8).toByteArray(Charsets.UTF_8)))) return@use
                    val r = JSONObject(String(rb, Charsets.UTF_8))
                    if (r.optBoolean("wait")) return@use
                    done = true
                    if (!r.optBoolean("ok")) { main.post { onAskResult?.invoke(clean(j.optString("n")), false) }; return@use }
                    val kp = KeyPair(KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(unb64(j.optString("pub")))), KeyFactory.getInstance("EC").generatePrivate(java.security.spec.PKCS8EncodedKeySpec(unb64(j.optString("priv")))))
                    val myPub = unb64(j.optString("pub"))
                    val oPub = unb64(r.optString("ownerPub")); val rPub = unb64(r.optString("relayPub"))
                    val owner = Contact(r.optString("owner"), clean(r.optString("ownerName")).ifEmpty { clean(j.optString("n")) }, agree(kp, oPub, "velivo-prosba", myPub, oPub), "", clean(r.optString("ownerName")), false, hp)
                    val relay = Contact(r.optString("relay"), clean(r.optString("relayName")).ifEmpty { "Velivo" }, agree(kp, rPub, "velivo-prosba", myPub, rPub), "", clean(r.optString("relayName")), false, r.optString("relayAddr").takeIf { ':' in it } ?: hp)
                    synchronized(this) { listOf(owner, relay).forEach { c -> if (c.id.length == 32 && contact(c.id) == null) contacts.add(c) } }
                    saveContacts(); announce()
                    main.post { onChange(); onAskResult?.invoke(owner.name, true) }
                }
            }
            if (!done) keep.put(j.toString())
        }
        prefs.edit().putString("moje-prosby", keep.toString()).apply()
    }
    var onAskResult: ((String, Boolean) -> Unit)? = null
    private fun cardList() = runCatching { JSONArray(prefs.getString("prosby-kontakt", "[]")) }.getOrDefault(JSONArray())
    // pierwsza czekająca prośba: (kto poleca, kogo) albo null
    fun nextCard(): Pair<String, String>? {
        val l = cardList(); if (l.length() == 0) return null
        val o = JSONObject(l.getString(0))
        return (contact(o.optString("f"))?.name ?: "?") to clean(o.optJSONObject("c")?.optString("name") ?: "")
    }
    fun answerCard(yes: Boolean) {
        val l = cardList(); if (l.length() == 0) return
        val o = JSONObject(l.getString(0)); l.remove(0); prefs.edit().putString("prosby-kontakt", l.toString()).apply()
        if (!yes) return
        val j = o.optJSONObject("c") ?: return; val from = contact(o.optString("f"))
        val id = j.optString("id"); val key = unb64(j.optString("key"))
        if (id.length != 32 || key.size != 32 || contact(id) != null) return
        val name = clean(j.optString("name")).ifEmpty { "Velivo" }
        synchronized(this) { contacts.add(Contact(id, name, key, "", clean(j.optString("dev")).ifEmpty { name }, false, j.optString("relay").ifEmpty { null })) }
        saveContacts()
        if (from != null) { synchronized(this) { chat(from.id).add(Msg(false, "👤 " + (if (pl) "Nowy kontakt: $name" else "New contact: $name"), System.currentTimeMillis(), true, UUID.randomUUID().toString().replace("-", ""))) }; saveChat(from.id) }
        announce(); main.post { onChange() }
    }

    // 👥 plik / zdjęcie / głosówka / wideo w grupie: jeden wpis w rozmowie grupy, do każdego członka osobno (nieobecni - 📬 skrzynka)
    fun sendGroupFile(g: Contact, name: String, size: Long, open: () -> java.io.InputStream): String {
        val m = Msg(true, fileLabel(name, size, isImage(name)), System.currentTimeMillis(), true, UUID.randomUUID().toString().replace("-", ""), file = true)
        synchronized(this) { chat(g.id).add(m) }; saveChat(g.id); main.post { onChange() }
        val res = members(g).mapNotNull { contact(it)?.takeIf { c -> !c.isGroup } }.map { c ->
            val t = runCatching { InetAddress.getByName(c.addr.ifEmpty { null } ?: throw Exception()) }.getOrNull()?.takeIf { online(c.id) } ?: InetAddress.getLoopbackAddress()
            sendFile(t, name, size, open, {}, c, group = g)
        }
        return if (res.all { it == "ok" }) "ok" else if (res.all { it == "ok" || it == "skrzynka" || it == "czeka" }) "skrzynka" else res.firstOrNull { it != "ok" } ?: "ok"
    }

    private fun groupOut(g: Contact, text: String, gm: String) {
        val head = JSONObject().put("g", g.id).put("n", g.name).put("m", JSONArray(members(g) + myId)).put("gm", gm).toString()   // gm - ten sam id u wszystkich (usuwanie u wszystkich)
        for (id in members(g)) contact(id)?.takeIf { !it.isGroup }?.let { deliver(it, Msg(true, GROUP + head + "\n" + text, System.currentTimeMillis(), false, UUID.randomUUID().toString().replace("-", ""))) }
    }
    // grupa z nagłówka (g - id, n - nazwa, m - członkowie): tworzy się sama, skład się uzupełnia
    private fun groupOf(from: Contact, head: JSONObject): Contact? {
        val gid = head.optString("g").takeIf { it.length == 32 } ?: return null
        val ids = (0 until (head.optJSONArray("m")?.length() ?: 0)).map { head.getJSONArray("m").optString(it) }.filter { it.length == 32 && it != myId }.toSet() + from.id
        val g = synchronized(this) {
            contact(gid)?.also { it.addr = "grupa:" + (members(it) + ids).distinct().joinToString(",") }
                ?: Contact(gid, clean(head.optString("n")).ifEmpty { "👥" }, ByteArray(0), "grupa:" + ids.joinToString(","), "👥", own = true).also { contacts.add(it) }
        }
        saveContacts()
        return g.takeIf { it.isGroup }
    }
    private fun groupIn(from: Contact, body: String, ts: Long, mid: String) {
        val head = runCatching { JSONObject(body.substringBefore('\n')) }.getOrNull() ?: return
        val g = groupOf(from, head) ?: return
        val gm = head.optString("gm").takeIf { it.length == 32 } ?: mid
        if (synchronized(this) { chat(g.id).any { it.mid == gm } }) return
        val m = Msg(false, (from.name + ": " + body.substringAfter('\n')).take(MAX_TEXT), ts, true, gm)
        synchronized(this) { chat(g.id).add(m) }
        saveChat(g.id)
        prefs.edit().putInt("nowe-${g.id}", unread(g.id) + 1).apply()
        notifyMessage(g)
        main.post { onMessage(g, m); onChange() }
    }

    // 📹 rozmowa wideo: sygnały (dzwonię, odbieram, opis połączenia) jako zaszyfrowane wiadomości - poza czatem
    var onCall: ((Contact, JSONObject) -> Unit)? = null
    var ringing: Pair<String, Long>? = null   // kto dzwoni (z czasu, gdy Velivo było w tle)
    var ringAudio = false   // 📞 rozmowa głosowa (bez kamery)
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
            "ring" -> { ringing = c.id to System.currentTimeMillis(); ringAudio = o.optBoolean("a"); if (nm != null) notifyCall(nm, c) }
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
            // jasno dla każdego: kto dzwoni i duże przyciski Odbierz (zielony) / Odrzuć (czerwony)
            val rej = PendingIntent.getBroadcast(ctx, "odrzuc".hashCode(), Intent(ctx, KomunikatorAkcja::class.java).setAction(KomunikatorAkcja.REJECT).putExtra("id", c.id).setPackage(ctx.packageName),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            val b = Notification.Builder(ctx, "rozmowy").setSmallIcon(android.R.drawable.stat_notify_chat).brand()
                .setContentTitle("📹 " + c.name + (if (pl) " dzwoni do Ciebie" else " is calling you"))
                .setContentText(if (pl) "Stuknij zielony przycisk „Odbierz”" else "Tap the green “Answer” button")
            if (Build.VERSION.SDK_INT >= 31) b.setStyle(Notification.CallStyle.forIncomingCall(android.app.Person.Builder().setName(c.name).setImportant(true).build(), rej, pi))
            else b.addAction(Notification.Action.Builder(null, if (pl) "✕ Odrzuć" else "✕ Decline", rej).build()).addAction(Notification.Action.Builder(null, if (pl) "📞 Odbierz" else "📞 Answer", pi).build())
            val n = b
                .setCategory(Notification.CATEGORY_CALL).setVisibility(Notification.VISIBILITY_PUBLIC).setContentIntent(pi).setFullScreenIntent(pi, true).setAutoCancel(true).setTimeoutAfter(45000).build()
            n.flags = n.flags or Notification.FLAG_INSISTENT
            nm.notify("rozmowa".hashCode(), n)
        }
    }

    private val inFlight = java.util.Collections.synchronizedSet(HashSet<String>())
    private fun deliver(c: Contact, m: Msg) {
        if (c.isGroup || !inFlight.add(m.mid)) return   // grupa nie ma klucza - jej wiadomości idą przez groupOut
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
        "p2p-file", "p2p-box" -> { runCatching { receiveFile(h, c) }; true }
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
        val s = lanSocket(peer.addr)
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
        val box = h.optString("Type") == "p2p-box"   // 📬 plik ze skrzynki pośrednika - klucz z klucza kontaktu (nadawcy mogło już nie być)
        val pubA = unb64(h.optString("Pub")); val base = unb64(h.optString("Nonce"))
        if ((!box && (pubA.isEmpty() || pubA.size > 200)) || base.size != 12) return
        val name = h.optString("Name").replace('\\', '/').substringAfterLast('/').filter { !it.isISOControl() && it !in "<>:\"|?*" }
            .trim().trimStart('.').ifEmpty { "plik" }.takeLast(120)
        val dev = clean(h.optString("Device"))
        val out = cn.out
        val key = if (box) hmac((contact(h.optString("Id")) ?: return).key, ("velivo-box\n" + h.optString("Nonce")).toByteArray(Charsets.UTF_8))
        else { val mine = ecPair(); val pubB = mine.public.encoded; writeBlock(out, pubB); agree(mine, pubA, "velivo-p2p", pubA, pubB) }   // nadawca liczy ten sam kod i pokazuje go u siebie
        val ad = fileAd(h)
        // od kontaktu (podpis kluczem kontaktu) - bez kodu, jak załącznik w rozmowie; zgoda odbiorcy zawsze
        val from = contact(h.optString("Id"))?.takeIf { c -> MessageDigest.isEqual(unb64(h.optString("Mac")), hmac(c.key, ad)) }
        val media = from != null && onMedia != null && name.startsWith("Velivo-Filmy-TV") && name.endsWith(".json") && size < 64L * 1024 * 1024
        val auto = media || (from != null && autoImages && size <= 2L * 1024 * 1024 * 1024)   // zdjęcie / plik od kontaktu - od razu do rozmowy (jak w innych komunikatorach)
        val ok = (!box || from != null) && (auto || ask { a -> askFile(from?.name ?: dev, name, size, if (from != null) "" else code(key), a) })
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
            if (from != null) {   // od kontaktu: w Velivo (w rozmowie, stuknięcie otwiera); do Galerii / Pobranych - przytrzymanie
                var f = java.io.File(mediaDir, name); var i = 2
                while (f.exists()) f = java.io.File(mediaDir, name.substringBeforeLast('.') + " ($i)." + name.substringAfterLast('.')).also { i++ }
                if (tmp.renameTo(f) || runCatching { tmp.copyTo(f); tmp.delete() }.isSuccess) saved = f.path
            } else saveFile(name, tmp, dev)
            out.write(hmac(key, ("file-ack\n" + h.optString("Hash")).toByteArray(Charsets.UTF_8))); out.flush()
            if (from != null) {
                val grp = h.optJSONObject("Group")?.let { groupOf(from, it) }   // 👥 plik w grupie - do rozmowy grupy
                if (grp != null) { note(grp, false, from.name + ": " + fileLabel(name, size, isImage(name)), h.optString("Mid"), saved); if (chatOpen != grp.id) prefs.edit().putInt("nowe-${grp.id}", unread(grp.id) + 1).apply(); notifyMessage(grp) }
                else note(from, false, if (name.endsWith(".apk", true)) "📲 " + (if (pl) "Nowa wersja aplikacji – stuknij, aby zainstalować: " else "New app version – tap to install: ") + name else fileLabel(name, size, isImage(name)), h.optString("Mid"), saved)   // 📲 aktualizacja od właściciela
                // plik / zdjęcie jak wiadomość: nieprzeczytane i powiadomienie (✓ u nadawcy, ✓✓ po otwarciu rozmowy)
                if (grp == null) { if (chatOpen != from.id) prefs.edit().putInt("nowe-${from.id}", unread(from.id) + 1).apply(); notifyMessage(from) }
            }
        } finally { if (!good) tmp.delete() }
    }

    // plik na inny telefon: open() - strumień od początku (dwa razy: suma, potem wysyłka)
    // retry - plik, który czekał na odbiorcę (kopia w Velivo), wysyłany ponownie z tym samym wpisem w rozmowie
    // box - 📬 do skrzynki pośrednika (odbiorcy nie ma): klucz z klucza kontaktu, bez wymiany kluczy z odbiorcą
    fun sendFile(target: InetAddress, name: String, size: Long, open: () -> java.io.InputStream, showCode: (String) -> Unit, to: Contact? = null, retry: Msg? = null, box: Boolean = false, group: Contact? = null): String {
        val md = MessageDigest.getInstance("SHA-256")
        open().use { s -> val b = ByteArray(1 shl 16); while (true) { val r = s.read(b); if (r < 0) break; md.update(b, 0, r) } }
        val hash = hex(md.digest())
        val mine = ecPair(); val pubA = mine.public.encoded
        val base = ByteArray(12).also { random.nextBytes(it) }
        val h = JSONObject().put("Type", if (box) "p2p-box" else "p2p-file").put("Id", myId).put("Device", deviceName()).put("Hash", hash).put("Size", size)
            .put("Name", name).put("Pub", if (box) "" else b64(pubA)).put("Nonce", b64(base))
        val ad = fileAd(h)
        val mid = retry?.mid ?: UUID.randomUUID().toString().replace("-", ""); h.put("Mid", mid)   // poza podpisem - tylko do ✓✓ w rozmowie
        if (to != null) h.put("Mac", b64(hmac(to.key, ad)))   // kontakt rozpozna nadawcę bez kodu
        if (to != null) h.put("To", to.id)   // pośrednik wie, komu przekazać (poza podpisem)
        if (group != null) h.put("Group", JSONObject().put("g", group.id).put("n", group.name).put("m", JSONArray(members(group) + myId)))   // 👥 plik w grupie - u odbiorcy do rozmowy grupy
        // wpis w rozmowie od razu („…”): „przeczytane” od odbiorcy może przyjść, zanim tu skończymy
        // wysłane zdjęcie: mała kopia w Velivo (miniatura w rozmowie)
        val thumb = if (to != null && retry == null && name.endsWith(".gif", true) && size in 1..20L * 1024 * 1024) runCatching {   // 🎞 GIF - oryginał (ruchomy), nie nieruchomy JPG
            java.io.File(mediaDir, "wyslane-$mid.gif").also { f -> open().use { i -> f.outputStream().use { i.copyTo(it) } } }.path
        }.getOrNull() else if (to != null && retry == null && isImage(name)) runCatching {
            val o = android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds = true }
            open().use { android.graphics.BitmapFactory.decodeStream(it, null, o) }
            var smp = 1; while (maxOf(o.outWidth, o.outHeight) / smp > 1280) smp *= 2
            val bmp = upright(open().use { android.graphics.BitmapFactory.decodeStream(it, null, android.graphics.BitmapFactory.Options().apply { inSampleSize = smp }) }!!,
                runCatching { open().use { android.media.ExifInterface(it) } }.getOrNull())
            val f = java.io.File(mediaDir, "wyslane-$mid.jpg"); f.outputStream().use { bmp.compress(android.graphics.Bitmap.CompressFormat.JPEG, 85, it) }; f.path
        }.getOrNull() else null
        // 🎤 własna głosówka / 🎬 wideo - kopia w Velivo, żeby można ją było odsłuchać u siebie (jak w WhatsApp)
        val voice = if (to != null && retry == null && (name.startsWith("Glosowa-") || name.substringAfterLast('.').lowercase() in setOf("mp4", "3gp", "webm", "mkv", "mov"))) runCatching { java.io.File(mediaDir, "moja-$mid-$name").also { f -> open().use { i -> f.outputStream().use { i.copyTo(it) } } }.path }.getOrNull() else null
        val pend = retry ?: to?.let { c -> Msg(true, fileLabel(name, size, thumb != null), System.currentTimeMillis(), false, mid, file = true, path = thumb ?: voice).also { m -> if (group == null) { synchronized(this) { chat(c.id).add(m) }; saveChat(c.id); main.post { onChange() } } } }   // w grupie wpis jest w rozmowie grupy (sendGroupFile)
        return runCatching {
            connect(target, to?.id).use { s ->   // poza domem - tunel przez pośrednika
                s.timeout(15000)
                val out = java.io.BufferedOutputStream(s.out, 1 shl 16)
                writeBlock(out, h.toString().toByteArray(Charsets.UTF_8))
                val inp = s.inp
                val key = if (box) hmac(to!!.key, ("velivo-box\n" + h.optString("Nonce")).toByteArray(Charsets.UTF_8))
                else { val pubB = readBlock(inp, 200) ?: return@use "błąd"; agree(mine, pubB, "velivo-p2p", pubA, pubB).also { showCode(code(it)) } }
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
                if (box || MessageDigest.isEqual(ack, hmac(key, "file-ack\n$hash".toByteArray(Charsets.UTF_8)))) "ok" else "błąd potwierdzenia"   // skrzynka: potwierdza pośrednik
            }
        }.getOrElse { it.message ?: it.javaClass.simpleName }.let { r ->
            // wynik dla okna: "ok", "odrzucono", "skrzynka" (📬 czeka na serwerze), "czeka" (u nas, pójdzie sama) albo błąd
            var res = r
            if (to != null && pend != null) {
                if (r == "ok") { pend.ok = true; pend.text += if (box) (if (pl) " – 📬 czeka na serwerze" else " – 📬 waiting on the server") else if (pl) " – odebrano" else " – received" }
                else if (r == "odrzucono") { synchronized(this) { chat(to.id).remove(pend) }; pend.path?.let { java.io.File(it).delete() } }   // odrzucony - bez wpisu
                else if (retry == null) {
                    if (sendBox(to, name, size, open, pend, group) == "ok") res = "skrzynka"   // odbiorcy nie ma: 📬 do skrzynki pośrednika
                    else runCatching {   // bez pośrednika - kopia w Velivo („…”), wyślę sama, gdy się pojawi
                        val keep = java.io.File(mediaDir, "wyslane-$mid-$name"); open().use { i -> keep.outputStream().use { i.copyTo(it) } }
                        pend.path?.let { java.io.File(it).delete() }; pend.path = keep.path; res = "czeka"
                    }.onFailure { synchronized(this) { chat(to.id).remove(pend) } }
                }
                saveChat(to.id); main.post { onChange() }
            }
            res
        }
    }

    // plik w historii rozmowy (jak załącznik w innych komunikatorach)
    // plik w rozmowie: ten sam identyfikator u nadawcy i odbiorcy (Mid z nagłówka) - „przeczytane” (✓✓) jak przy wiadomościach
    // 📬 odbiorcy nie ma - plik do skrzynki pośrednika (poza domem tunelem, w domu do pośrednika w Wi-Fi); oddany, gdy odbiorca się pojawi
    private fun sendBox(to: Contact, name: String, size: Long, open: () -> java.io.InputStream, pend: Msg, group: Contact? = null): String {
        val now = System.currentTimeMillis()
        val home = synchronized(this) { contacts.firstOrNull { it.relay != null && now - (lanSeen[it.id] ?: 0L) < HOME_MS } }
        // w domu - do pośrednika w Wi-Fi; poza domem - tunelem przez pośrednika (także gdy połączenie jeszcze nie wstało)
        val target = home?.takeIf { up == null }?.addr?.let { a -> runCatching { InetAddress.getByName(a) }.getOrNull() } ?: InetAddress.getLoopbackAddress()
        return sendFile(target, name, size, open, {}, to, pend, box = true, group = group)
    }

    // 🎤 wiadomość głosowa (plik „Glosowa-….m4a”) - opis w rozmowie zamiast nazwy pliku
    private fun fileLabel(name: String, size: Long, image: Boolean) =
        if (name.startsWith("Glosowa-")) "🎤 " + (if (pl) "Wiadomość głosowa" else "Voice message") + " · " + sizeText(size)
        else if (name.substringAfterLast('.').lowercase() in setOf("mp4", "3gp", "webm", "mkv", "mov")) "🎬 " + (if (pl) "Wiadomość wideo" else "Video message") + " · " + sizeText(size)
        else (if (image) "🖼 " else "📎 ") + "$name · " + sizeText(size)

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
    // ✍ „pisze…” jak w WhatsApp: sygnał co najwyżej co 3 s, ważny 6 s; wiadomość od osoby kasuje go od razu
    private val typingAt = java.util.concurrent.ConcurrentHashMap<String, Long>()
    private val typingSent = java.util.concurrent.ConcurrentHashMap<String, Long>()
    var onTyping: (String) -> Unit = {}
    fun typing(id: String) = System.currentTimeMillis() - (typingAt[id] ?: 0L) < 6000
    fun sendTyping(c: Contact) {
        if (c.isGroup || System.currentTimeMillis() - (typingSent[c.id] ?: 0L) < 3000) return
        typingSent[c.id] = System.currentTimeMillis()
        Thread { runCatching { sendUdp(packet("p2p-typing").put("to", c.id), runCatching { InetAddress.getByName(c.addr.ifEmpty { null } ?: throw Exception()) }.getOrNull()) } }.start()
    }
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
        init { runCatching { sock.tcpNoDelay = true } }   // małe pakiety (wiadomości, obecność) od razu, bez czekania Nagle'a
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
            rawOut.write(int4(12 + e.size) + n + e); rawOut.flush()   // jedna ramka = jeden zapis
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
                if (t == "relay-pong") { val was = online(rl.id); lastSeen[rl.id] = System.currentTimeMillis(); markSeen(rl.id); viaNet[rl.id] = true; if (!was) main.post { onChange() }; continue }
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

    // TCP w domu: nowe Velivo - port komunikatora, starsze - 41919 (3 s, bo zapora Windows może milczeć)
    private fun lanSocket(a: InetAddress): java.net.Socket =
        runCatching { java.net.Socket().apply { connect(InetSocketAddress(a, PORT), 3000) } }.getOrNull()
            ?: java.net.Socket().apply { connect(InetSocketAddress(a, OLD_PORT), 5000) }

    // połączenie do urządzenia: poza domem - tunel przez pośrednika; w domu - TCP w sieci domowej
    private fun connect(target: InetAddress, toId: String?): Conn {
        // poza domem - tunel przez pośrednika; bez gotowego połączenia (adres 127.0.0.1 = „nie wiem gdzie”) - pierwszy pośrednik z kontaktów
        val relay = upRelay ?: myRelay().takeIf { target.isLoopbackAddress }
        if ((up != null || target.isLoopbackAddress) && relay != null && toId != null) relayConnect(relay, "tunnel", to = toId)?.let { f -> return Conn(DataInputStream(f.input), f.output, null, f.sock) }
        val s = lanSocket(target)
        return Conn(DataInputStream(s.getInputStream()), s.getOutputStream(), target, s)
    }

    // co 20 s: pośrednik nie widoczny w sieci domowej - połączenie przez internet; widoczny - z powrotem w domu
    @Volatile private var lastIps = ""
    @Volatile private var lastPing = 0L
    @Volatile private var whoAt = 0L
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
            // w domu: pośrednik długo milczy w Wi-Fi - pytamy go, zanim uznamy, że jesteśmy poza domem (bez przeskakiwania dom <-> internet co 3 min)
            if (up == null && now - whoAt > 30000 && relays.any { now - (lanSeen[it.id] ?: 0L) in 40000L..HOME_MS }) { whoAt = now; runCatching { sendUdp(packet("p2p-who"), null) } }
            val home = relay == null || (now - started < 30000 && !upForce) || relays.any { now - (lanSeen[it.id] ?: 0L) < HOME_MS }   // w domu = widać pośrednika (np. NUCBOX); inny telefon w tej samej sieci (hotspot) to nie dom
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
        udp = serve(PORT, true) ?: return
        // część telefonów (Oppo / Realme - ColorOS, niektóre Samsungi) bez tej blokady odrzuca rozgłoszenia Wi-Fi - nie widać nikogo w domu
        runCatching { (wifiLock ?: (ctx.applicationContext.getSystemService(Context.WIFI_SERVICE) as android.net.wifi.WifiManager).createMulticastLock("velivo-komunikator").apply { setReferenceCounted(false) }.also { wifiLock = it }).acquire() }
        // starsze Velivo (np. komputer przed aktualizacją) odpowiada tylko na 41919 - słuchamy też tam, gdy w telefonie nie ma przeglądarki Velivo
        // (ona trzyma 41919 dla synchronizacji z komputerem i sama przekazuje pakiety komunikatora)
        if (ctx.packageName != KomunikatorUsluga.BROWSER && runCatching { ctx.packageManager.getPackageInfo(KomunikatorUsluga.BROWSER, 0) }.isFailure) serve(OLD_PORT, false)
    }
    private val socks = java.util.concurrent.CopyOnWriteArrayList<java.io.Closeable>()
    private fun serve(port: Int, main: Boolean): DatagramSocket? {
        val u = runCatching { DatagramSocket(null).apply { reuseAddress = true; broadcast = true; bind(InetSocketAddress(port)) } }
            .getOrElse { if (main) listenError = it.message; return null }
        socks.add(u)
        Thread {
            val buf = ByteArray(65536)
            while (!u.isClosed) runCatching {
                val p = DatagramPacket(buf, buf.size); u.receive(p)
                handleUdp(JSONObject(String(p.data, 0, p.length, Charsets.UTF_8)), p.address)
            }
        }.start()
        Thread {
            val s = runCatching { ServerSocket().apply { reuseAddress = true; bind(InetSocketAddress(port)) } }.getOrElse { if (main) listenError = it.message; return@Thread }
            socks.add(s); if (u.isClosed) { runCatching { s.close() }; return@Thread }   // zamknięte w międzyczasie
            if (main) tcp = s
            while (!s.isClosed) {
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
        return u
    }

    val idle get() = udp == null && udpOut == null

    private var wifiLock: android.net.wifi.WifiManager.MulticastLock? = null
    fun close() {
        runCatching { wifiLock?.release() }
        socks.forEach { runCatching { it.close() } }; socks.clear()
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
    var voiceSpeed: Float   // ⏩ szybkość odtwarzania głosówek (1 / 1,5 / 2)
        get() = prefs.getFloat("glos-x", 1f)
        set(v) { prefs.edit().putFloat("glos-x", v).apply() }
    var lock: Boolean   // 🔒 komunikator otwierany odciskiem palca / PIN-em telefonu
        get() = prefs.getBoolean("blokada", false)
        set(v) { prefs.edit().putBoolean("blokada", v).apply() }
    var background: Boolean
        get() = prefs.getBoolean("tlo", true)
        set(v) { prefs.edit().putBoolean("tlo", v).apply() }

    // 💬 powiadomienie wyraźnie z komunikatora: ikona Komunikatora Velivo, podpis „Komunikator Velivo”, fioletowy kolor
    private fun Notification.Builder.brand(): Notification.Builder = apply {
        val ic = ctx.resources.getIdentifier("ic_komunikator", "mipmap", ctx.packageName)
        if (ic != 0) setLargeIcon(android.graphics.drawable.Icon.createWithResource(ctx, ic))
        setSubText(if (pl) "Komunikator Velivo" else "Velivo Messenger"); setColor(Color.rgb(0x8B, 0x5C, 0xF6))
    }
    fun channelsReady() { ctx.getSystemService(NotificationManager::class.java)?.let { channels(it) } }
    private fun channels(nm: NotificationManager) {
        val pl = Locale.getDefault().language == "pl"
        // jak w WhatsApp: wiadomość wyskakuje na górze ekranu (bez przeciągania w dół), dźwięk i wibracja; nowy kanał - stary mógł zostać wyciszony
        if (nm.getNotificationChannel(MSG_CH) == null) {
            val old = nm.getNotificationChannel("wiadomosci")
            nm.createNotificationChannel(NotificationChannel(MSG_CH, if (pl) "Wiadomości" else "Messages", NotificationManager.IMPORTANCE_HIGH).apply {
                enableVibration(true); vibrationPattern = longArrayOf(0, 250, 150, 250); enableLights(true); lightColor = Color.rgb(0x8B, 0x5C, 0xF6)
                setShowBadge(true); lockscreenVisibility = Notification.VISIBILITY_PUBLIC
                old?.sound?.let { setSound(it, old.audioAttributes) }   // dźwięk wybrany wcześniej zostaje
            })
            if (old != null) nm.deleteNotificationChannel("wiadomosci")
        }
        // 🔢 licznik na ikonie tylko z wiadomości - nie ze stałego powiadomienia w tle ani informacji
        nm.createNotificationChannel(NotificationChannel("informacje", if (pl) "Informacje" else "Information", NotificationManager.IMPORTANCE_LOW).apply { setShowBadge(false) })
        nm.createNotificationChannel(NotificationChannel("nasluch", if (pl) "Komunikator w tle" else "Messenger in background", NotificationManager.IMPORTANCE_MIN).apply { setShowBadge(false) })
    }

    fun notifyMessage(c: Contact, quiet: Boolean = false) {
        if (chatOpen == c.id) { if (!quiet) chatSound(); sendRead(c); return }
        if (muted(c.id)) return   // 🔕 wyciszona rozmowa - tylko licznik w rozmowie
        val nm = ctx.getSystemService(NotificationManager::class.java) ?: return
        channels(nm)
        // jak w innych komunikatorach: ostatnie wiadomości w powiadomieniu, odpowiedź i „przeczytane” bez otwierania Velivo
        val pl = Locale.getDefault().language == "pl"
        val last = synchronized(this) { chat(c.id).takeLast(6) }
        val me = android.app.Person.Builder().setName(if (pl) "Ty" else "You").build()
        val them = android.app.Person.Builder().setName(c.name).setKey(c.id).build()
        // jasno: „💬 Nowa wiadomość od …” nad treścią
        val st = Notification.MessagingStyle(me).setConversationTitle("💬 " + c.name + (if (pl) " pisze:" else " writes:")).setGroupConversation(true)
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
            nm.notify(c.id.hashCode(), Notification.Builder(ctx, MSG_CH).setSmallIcon(android.R.drawable.stat_notify_chat).brand()
                .setContentTitle("💬 " + c.name + (if (pl) " pisze:" else " writes:")).setContentText(last.lastOrNull()?.text ?: "")
                .setStyle(st).setNumber(n).setCategory(Notification.CATEGORY_MESSAGE).setVisibility(Notification.VISIBILITY_PUBLIC).setContentIntent(pi).setAutoCancel(true)
                .setOnlyAlertOnce(quiet).addAction(Notification.Action.Builder(null, if (pl) "📖 Otwórz" else "📖 Open", pi).build()).addAction(reply).addAction(read).setShowWhen(true).build())
        }
    }
    // rozmowa na ekranie: bez powiadomienia, tylko krótki dźwięk (jak w innych komunikatorach); wyciszony telefon - cisza
    private fun chatSound() = runCatching {
        val am = ctx.getSystemService(android.media.AudioManager::class.java)
        if (am?.ringerMode != android.media.AudioManager.RINGER_MODE_NORMAL) return@runCatching
        // 🔔 dźwięk wybrany dla wiadomości Velivo (ustawienia powiadomień), inaczej domyślny
        val snd = ctx.getSystemService(NotificationManager::class.java)?.getNotificationChannel(MSG_CH)?.sound ?: android.media.RingtoneManager.getDefaultUri(android.media.RingtoneManager.TYPE_NOTIFICATION)
        android.media.RingtoneManager.getRingtone(ctx, snd)?.play()
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
            nm.notify("para".hashCode(), Notification.Builder(ctx, MSG_CH).setSmallIcon(android.R.drawable.stat_notify_chat)
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
            .setContentText(if (pl) "Czeka na wiadomości" else "Waiting for messages")
            .setContentIntent(pi).setOngoing(true).build()
    }

    // 🟢 w sieci: kontakt odezwał się w ostatnich 95 s (każde Velivo z komunikatorem ogłasza się co minutę, okno komunikatora pyta co 30 s)
    private val lastSeen = java.util.concurrent.ConcurrentHashMap<String, Long>()
    fun online(id: String) = System.currentTimeMillis() - (lastSeen[id] ?: 0L) < 95000
    // „ostatnio w sieci” (jak w innych komunikatorach) - zostaje także po pożegnaniu
    private val seenAt = java.util.concurrent.ConcurrentHashMap<String, Long>()
    fun lastSeenAt(id: String): Long? = seenAt[id] ?: prefs.getLong("seen-$id", 0L).takeIf { it > 0L }   // zapisane - zostaje po ponownym uruchomieniu
    private fun markSeen(id: String) { val now = System.currentTimeMillis(); val old = seenAt.put(id, now); if (old == null || now - old > 60000L) prefs.edit().putLong("seen-$id", now).apply() }

    // wychodzimy z sieci (Velivo zamknięte bez odbioru w tle) - kontakty od razu widzą „poza siecią”
    fun bye() = Thread { repeat(2) { sendUdp(packet("p2p-bye"), null); Thread.sleep(150) } }.start()
    private var shownOnline = emptySet<String>()

    // niedoręczone (…) - ponownie, gdy kontakt odezwie się w sieci, i co minutę
    private fun resend(c: Contact) {
        val pending = synchronized(this) { chat(c.id).filter { it.mine && !it.ok && !it.file && System.currentTimeMillis() - it.ts < 7L * 86400000 } }
        pending.forEach { deliver(c, it) }
        // 📎 pliki, które czekały na odbiorcę - gdy jest w sieci
        if (online(c.id)) synchronized(this) { chat(c.id).filter { it.mine && !it.ok && it.file && it.path?.let { p -> java.io.File(p).name.startsWith("wyslane-${it.mid}-") } == true } }.forEach { m ->
            if (!inFlight.add(m.mid)) return@forEach
            Thread {
                runCatching {
                    val f = java.io.File(m.path!!); val target = runCatching { InetAddress.getByName(c.addr.ifEmpty { null } ?: throw Exception()) }.getOrNull() ?: InetAddress.getLoopbackAddress()
                    sendFile(target, f.name.removePrefix("wyslane-${m.mid}-"), f.length(), { f.inputStream() }, {}, c, m)
                }
                inFlight.remove(m.mid)
            }.start()
        }
    }
    private val resender = Thread {
        while (true) runCatching {
            Thread.sleep(60000)
            val list = synchronized(this) { contacts.toList() }
            if ((udpOut != null || udp != null) && list.isNotEmpty()) { sendUdp(packet("p2p-here"), null); list.forEach { resend(it) } }
            pollAsks()   // 🔗 moje prośby z linku - czy ktoś już odpowiedział
            val now = list.filter { online(it.id) }.map { it.id }.toSet()
            if (now != shownOnline) { shownOnline = now; main.post { onChange() } }
        }
    }.apply { isDaemon = true }

    companion object {
        const val MSG_CH = "wiadomosci2"   // kanał wiadomości (wyskakujące, z dźwiękiem i wibracją)
        @Volatile private var one: Komunikator? = null
        // jeden w aplikacji - okna i usługa w tle widzą te same rozmowy
        fun get(ctx: Context, deviceName: () -> String): Komunikator = one ?: synchronized(this) {
            one ?: Komunikator(ctx.applicationContext, deviceName).also { catchCrashes(ctx.applicationContext); one = it; it.resender.start(); it.upLoop.start() }
        }
        // 🐞 awaria w dowolnym wątku (okno, usługa w tle, sieć) - zapis do blad.txt; raport przy następnym otwarciu
        private fun catchCrashes(ctx: Context) {
            val prev = Thread.getDefaultUncaughtExceptionHandler()
            Thread.setDefaultUncaughtExceptionHandler { t, e ->
                runCatching { java.io.File(ctx.filesDir, "blad.txt").writeText(crashHeader(ctx) + "awaria (" + t.name + ")\n" + android.util.Log.getStackTraceString(e).lines().take(40).joinToString("\n")) }
                prev?.uncaughtException(t, e)
            }
        }
        fun crashHeader(ctx: Context) = ctx.packageName + " " + runCatching { ctx.packageManager.getPackageInfo(ctx.packageName, 0).versionName }.getOrDefault("?") + ", Android " + Build.VERSION.RELEASE + ", " + Build.MANUFACTURER + " " + Build.MODEL + "\n"
        // 💬 własny port komunikatora w sieci domowej (osobna aplikacja Komunikator); 41919 - dom (synchronizacja) i starsze Velivo
        const val PORT = 41918; const val OLD_PORT = 41919
        const val APP = "pl.velivo.komunikator"
        const val REQ_PHOTO = 4733   // 📷 zdjęcie z aparatu - wynik: okno.cameraResult
        const val REQ_SCREEN = 4735   // 🖥 zgoda na pokazanie ekranu w rozmowie - wynik: okno.screenResult
        const val REQ_BACKUP = 4734   // 💾 miejsce na kopię (telefon, Google Drive…) - wynik: okno.backupResult
        // jeden komunikator w telefonie: aplikacja Komunikator Velivo, bez niej przeglądarka Velivo, bez obu - klawiatura
        fun owner(ctx: Context): String = listOf(APP, KomunikatorUsluga.BROWSER).firstOrNull { p -> p == ctx.packageName || runCatching { ctx.packageManager.getPackageInfo(p, 0) }.isSuccess } ?: ctx.packageName
        const val CHUNK = 1 shl 20
        const val MAX_TEXT = 4000
        const val CALL = "\u0001velivo-rozmowa\n"
        // w domu: pośrednik widziany w Wi-Fi w ostatnich 10 min (wyjście z domu wykrywa zmiana adresu sieci - od razu);
        // krócej - telefon w domu przeskakiwał co kilka minut na internet i z powrotem (dziennik pośrednika)
        const val HOME_MS = 600000L
        const val GROUP = "\u0001velivo-grupa\n"
        const val CARD = "\u0001velivo-kontakt\n"
        const val DEL = "\u0001velivo-usun\n"
        const val EDIT = "\u0001velivo-edycja\n"; const val REACT = "\u0001velivo-reakcja\n"; const val PHOTO = "\u0001velivo-zdjecie\n"; const val TIMER = "\u0001velivo-znikanie\n"
        const val ADDR = "\u0001velivo-adres\n"; const val ASK = "\u0001velivo-prosba\n"; const val ASKOK = "\u0001velivo-prosba-ok\n"
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
    private var cardOpen = false
    private fun cards() {
        if (cardOpen || a.isFinishing) return
        k.nextAsk()?.let { name ->   // 🔗 prośba z mojego adresu
            cardOpen = true
            AlertDialog.Builder(a).setTitle("👤 " + t("Prośba o kontakt", "Contact request"))
                .setMessage(t("$name chce się z Tobą kontaktować przez Velivo (z Twojego adresu).\n\nDodać? Dostanie tylko komunikator – bez Twojej sieci domowej.", "$name wants to contact you via Velivo (from your address).\n\nAdd? They get the messenger only – not your home network."))
                .setPositiveButton(t("Dodaj", "Add")) { _, _ -> Thread { k.answerAsk(true) }.start() }.setNegativeButton(t("Odrzuć", "Decline")) { _, _ -> Thread { k.answerAsk(false) }.start() }
                .setOnDismissListener { cardOpen = false; a.window.decorView.postDelayed({ cards() }, 300) }.show()
            return
        }
        val (who, name) = k.nextCard() ?: return
        cardOpen = true
        AlertDialog.Builder(a).setTitle("👤 " + t("Prośba o kontakt", "Contact request"))
            .setMessage(t("$who poleca Ci: $name.\n\nDodać do komunikatora? Będziecie mogli pisać i dzwonić także przez internet.", "$who recommends: $name.\n\nAdd to the messenger? You will be able to message and call also over the internet."))
            .setPositiveButton(t("Dodaj", "Add")) { _, _ -> k.answerCard(true) }.setNegativeButton(t("Odrzuć", "Decline")) { _, _ -> k.answerCard(false) }
            .setOnDismissListener { cardOpen = false; a.window.decorView.post { cards() } }.show()
    }

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
        k.onCards = { cards() }; a.window.decorView.post { cards() }
        k.onAskResult = { name, ok -> Toast.makeText(a, if (ok) "👤 " + t("$name przyjął(a) Twoją prośbę – jest w kontaktach", "$name accepted your request – now in contacts") else t("$name odrzucił(a) prośbę", "$name declined the request"), Toast.LENGTH_LONG).show() }   // 👤 prośby o kontakt od znajomych (także z czasu, gdy Velivo było w tle)
        k.askFile = { dev, name, size, code, accept ->
            AlertDialog.Builder(a).setTitle("📥 " + t("Plik z: ", "File from: ") + dev)
                .setMessage("$name (${sizeText(size)})" + if (code.isEmpty()) "" else "\n\n" + t("Kod: $code\n\nPrzyjmij tylko, gdy telefon nadawcy pokazuje ten sam kod.",
                    "Code: $code\n\nAccept only if the sender's phone shows the same code."))
                .setPositiveButton(t("Przyjmij", "Accept")) { _, _ -> accept(true) }.setNegativeButton(t("Odrzuć", "Decline")) { _, _ -> accept(false) }
                .setOnCancelListener { accept(false) }.show()
        }
        // rozmowa „na ekranie” tylko, gdy aplikacja jest na wierzchu - w tle / przy wygaszonym ekranie zwykłe powiadomienia
        a.application.registerActivityLifecycleCallbacks(object : android.app.Application.ActivityLifecycleCallbacks {
            override fun onActivityResumed(x: Activity) { if (x === a) { k.chatOpen = chatWith; onTop = true; if (stale) { stale = false; refresh() } } }
            override fun onActivityPaused(x: Activity) { if (x === a) { k.chatOpen = null; onTop = false } }
            override fun onActivityDestroyed(x: Activity) { if (x === a) { k.chatOpen = null; k.askPair = k.noUiPair; a.application.unregisterActivityLifecycleCallbacks(this) } }
            override fun onActivityCreated(x: Activity, b: android.os.Bundle?) {}
            override fun onActivityStarted(x: Activity) {}
            override fun onActivityStopped(x: Activity) {}
            override fun onActivitySaveInstanceState(x: Activity, b: android.os.Bundle) {}
        })
        k.onChange = { refresh() }
    }
    // 🔋 odświeżanie okna: seria pakietów (obecność, potwierdzenia) = jedno odświeżenie na 0,4 s; w tle / przy wygaszonym ekranie - wcale (raz po powrocie)
    private var onTop = true; private var stale = false; private var refreshQueued = false
    private val ui = Handler(Looper.getMainLooper())
    private fun refresh() {
        if (!onTop) { stale = true; return }
        if (refreshQueued) return
        refreshQueued = true
        ui.postDelayed({ refreshQueued = false; if (onTop) (chatRefresh ?: listRefresh)?.invoke() else stale = true }, 400)
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
        val tick = object : Runnable { override fun run() { if (onTop) { k.ping(); h.postDelayed({ refresh() }, 2000) }; h.postDelayed(this, 30000) } }   // 🔋 w tle bez pytania „kto w sieci” co 30 s (usługa i tak odbiera)
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
            }.setNegativeButton("🔔 " + t("Dźwięk", "Sound")) { _, _ ->
                // wybór dźwięku wiadomości - ustawienia powiadomień Androida dla kanału „Wiadomości”
                k.channelsReady()
                runCatching { a.startActivity(android.content.Intent(android.provider.Settings.ACTION_CHANNEL_NOTIFICATION_SETTINGS)
                    .putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, a.packageName).putExtra(android.provider.Settings.EXTRA_CHANNEL_ID, Komunikator.MSG_CH)) }
            }.show()
    }

    // mały okrągły przycisk-ikona w nagłówku (zwykły Button ma 88 dp szerokości i łamał tytuł na kilka linii)
    // 📹 rozmowa wideo: strona rozmowy (rozmowa.html, WebRTC) na cały ekran; sygnały przez komunikator
    private var callView: android.webkit.WebView? = null
    private var callWith: String? = null
    private var ringDlg: android.app.Dialog? = null
    private var ringTone: android.media.Ringtone? = null
    private fun onCall(c: Komunikator.Contact, o: JSONObject) {
        val kind = o.optString("k")
        if (kind == "ring") { if (callView != null) k.sendCall(c, JSONObject().put("k", "busy")) else incoming(c, o.optBoolean("a")); return }
        if (kind == "end" && callView == null) { ringDlg?.dismiss(); return }
        if (callWith == c.id) callView?.evaluateJavascript("onSig(" + JSONObject.quote(o.toString()) + ")", null)
    }
    // ktoś dzwonił, gdy Velivo było w tle - po otwarciu od razu „Odbierz / Odrzuć”
    private fun ringPending(c: Komunikator.Contact) {
        val r = k.ringing ?: return
        if (r.first == c.id && System.currentTimeMillis() - r.second < 45000) incoming(c, k.ringAudio)
    }
    private fun incoming(c: Komunikator.Contact, audio: Boolean) {
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
        val name = TextView(a).apply { text = (if (audio) "📞 " else "📹 ") + c.name + "\n" + t("dzwoni…", "is calling…"); textSize = 26f; gravity = Gravity.CENTER; setTextColor(ink) }
        val row = LinearLayout(a).apply { gravity = Gravity.CENTER; setPadding(0, px(48), 0, 0)
            addView(btn("✕", t("Odrzuć", "Decline"), Color.rgb(229, 57, 53)) { k.sendCall(c, JSONObject().put("k", "reject")); d.dismiss() })
            addView(btn("📞", t("Odbierz", "Answer"), green) { d.dismiss(); openCall(c, false, audio) }) }
        d.setContentView(LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER; setBackgroundColor(pal().bg1); addView(name); addView(row) })
        d.setOnCancelListener { k.sendCall(c, JSONObject().put("k", "reject")) }
        d.setOnDismissListener { ringTone?.stop(); ringTone = null; ringDlg = null; a.getSystemService(NotificationManager::class.java)?.cancel("rozmowa".hashCode())
            if (callView == null) { a.setShowWhenLocked(false); a.setTurnScreenOn(false) } }
        a.setShowWhenLocked(true); a.setTurnScreenOn(true)   // jak w WhatsApp: także na zablokowanym ekranie
        ringDlg = d; if (runCatching { d.show() }.isFailure) { ringDlg = null; return }
        a.window.decorView.postDelayed({ if (ringDlg === d) d.dismiss() }, 45000)
        ringTone = runCatching { android.media.RingtoneManager.getRingtone(a, android.media.RingtoneManager.getDefaultUri(android.media.RingtoneManager.TYPE_RINGTONE)) }.getOrNull()?.also { runCatching { it.play() } }
    }
    fun openCall(c: Komunikator.Contact, caller: Boolean, audio: Boolean = false) {
        if (callView != null) return
        val perms = if (audio) arrayOf(android.Manifest.permission.RECORD_AUDIO) else arrayOf(android.Manifest.permission.CAMERA, android.Manifest.permission.RECORD_AUDIO)
        fun granted() = perms.all { a.checkSelfPermission(it) == android.content.pm.PackageManager.PERMISSION_GRANTED }
        if (!granted()) a.requestPermissions(perms, 4711)
        val d = android.app.Dialog(a, android.R.style.Theme_Black_NoTitleBar_Fullscreen)
        val w = android.webkit.WebView(a)
        var ended = false
        // dźwięk: słuchawki (przewodowe / Bluetooth) - w nich; bez słuchawek - głośnik; 🔊 przełącza głośnik / słuchawkę przy uchu
        val am = a.getSystemService(android.media.AudioManager::class.java)
        val oldMode = am?.mode; val oldSpeaker = am?.isSpeakerphoneOn
        val heads = intArrayOf(android.media.AudioDeviceInfo.TYPE_BLUETOOTH_SCO, android.media.AudioDeviceInfo.TYPE_WIRED_HEADSET, android.media.AudioDeviceInfo.TYPE_WIRED_HEADPHONES, android.media.AudioDeviceInfo.TYPE_USB_HEADSET, 26 /* TYPE_BLE_HEADSET */)
        var spk = !audio && am?.getDevices(android.media.AudioManager.GET_DEVICES_OUTPUTS)?.none { it.type in heads } ?: true   // 📞 głosowa - przy uchu, jak telefon
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
        w.setBackgroundColor(Color.rgb(18, 15, 36)); w.setRendererPriorityPolicy(android.webkit.WebView.RENDERER_PRIORITY_IMPORTANT, false)   // rozmowa działa też w tle (🖥 pokazywanie ekranu)
        w.addJavascriptInterface(object {
            @android.webkit.JavascriptInterface fun send(s: String) {
                val o = runCatching { JSONObject(s) }.getOrNull() ?: return
                if (o.optString("k") == "close") { a.runOnUiThread { d.dismiss() }; return }
                if (o.optString("k") == "end") ended = true
                k.sendCall(c, o)
            }
            @android.webkit.JavascriptInterface fun speaker(): Boolean { spk = !spk; a.runOnUiThread { route() }; return spk }
            @android.webkit.JavascriptInterface fun speakerOn(): Boolean = spk
            // 🖥 pokazanie ekranu telefonu rozmówcy; 👆 jego wskazówka nad ekranem
            @android.webkit.JavascriptInterface fun screen(on: Boolean) { a.runOnUiThread { if (on) askScreen() else a.stopService(Intent(a, EkranUsluga::class.java)) } }
            @android.webkit.JavascriptInterface fun point(x: Double, y: Double) { EkranUsluga.point(a.applicationContext, x, y) }
            // 🖱 sterowanie telefonem: usługa ułatwień dostępu Velivo; działa tylko, gdy osoba zgodziła się w rozmowie
            @android.webkit.JavascriptInterface fun ctlReady(): Boolean = SterowanieUsluga.on != null
            @android.webkit.JavascriptInterface fun ctlSetup() { a.runOnUiThread {
                AlertDialog.Builder(a).setTitle("🖱 " + t("Pomoc zdalna", "Remote help"))
                    .setMessage(t("Aby rozmówca mógł stukać na Twoim telefonie, włącz raz „Velivo – pomoc zdalna” w Ustawieniach → Ułatwienia dostępu (Pobrane aplikacje).\n\n" +
                        "Gdy Android pisze „Ustawienia z ograniczonym dostępem” (Velivo nie jest ze Sklepu Play): 1) „Informacje o aplikacji” → ⋮ w prawym górnym rogu → „Zezwól na ustawienia z ograniczeniami”, 2) wróć i włącz w Ułatwieniach dostępu.\n\nVelivo steruje tylko po Twojej zgodzie w rozmowie i nie czyta ekranu.",
                        "To let the other person tap on your phone, turn on “Velivo – remote help” once in Settings → Accessibility (Downloaded apps).\n\n" +
                        "If Android says “Restricted setting” (Velivo is not from the Play Store): 1) “App info” → ⋮ in the top right → “Allow restricted settings”, 2) go back and turn it on in Accessibility.\n\nVelivo only acts after you agree in the call and does not read the screen."))
                    .setPositiveButton(t("Ułatwienia dostępu", "Accessibility")) { _, _ -> runCatching { a.startActivity(Intent(android.provider.Settings.ACTION_ACCESSIBILITY_SETTINGS)) } }
                    .setNeutralButton(t("Informacje o aplikacji", "App info")) { _, _ -> runCatching { a.startActivity(Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + a.packageName))) } }
                    .setNegativeButton(android.R.string.cancel, null).show() } }
            @android.webkit.JavascriptInterface fun ctl(on: Boolean) { SterowanieUsluga.allowed = on }
            @android.webkit.JavascriptInterface fun ask(q: String) { EkranUsluga.ask(a, "🖱 " + c.name + "\n" + q, t("Pozwól", "Allow"), t("Nie", "No")) { yes -> w.post { w.evaluateJavascript("ctlAnswer($yes)", null) } } }
            @android.webkit.JavascriptInterface fun input(s: String) { runCatching { SterowanieUsluga.input(JSONObject(s)) } }
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
            a.stopService(Intent(a, EkranUsluga::class.java)); EkranUsluga.onFrame = null; EkranUsluga.onStop = null; SterowanieUsluga.allowed = false
            runCatching { w.destroy() }
            runCatching {
                if (Build.VERSION.SDK_INT >= 31) am?.clearCommunicationDevice() else { @Suppress("DEPRECATION") am?.stopBluetoothSco() }
                oldMode?.let { am?.mode = it }; @Suppress("DEPRECATION") oldSpeaker?.let { am?.isSpeakerphoneOn = it }
            }
            a.setShowWhenLocked(false); a.setTurnScreenOn(false)
        }
        callView = w; callWith = c.id
        val html = runCatching { a.assets.open("rozmowa.html").bufferedReader().use { it.readText() } }.getOrNull() ?: return
        val q = "?rola=" + (if (caller) "dzwoni" else "odbiera") + "&ice=" + Uri.encode(k.turnIce()) + "&pl=" + (if (pl) "1" else "0") + (if (audio) "&audio=1" else "")
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

    // 🖼 zdjęcie profilowe kontaktu (z zieloną obwódką, gdy w sieci); bez zdjęcia - kropka jak dotąd
    private fun avatar(c: Komunikator.Contact, on: Boolean): View {
        val bmp = k.photo(c.id)?.let { decode(it.path, px(44)) }
        // bez zdjęcia - kółko z pierwszą literą w stałym kolorze kontaktu (jak w WhatsApp / Signal); obwódka zielona = w sieci
        if (bmp == null) return TextView(a).apply {
            text = if (c.isGroup) "👥" else c.name.trim().take(1).uppercase().ifEmpty { "?" }; textSize = 18f; gravity = Gravity.CENTER; setTextColor(Color.WHITE); setTypeface(typeface, android.graphics.Typeface.BOLD)
            val hue = (c.id.hashCode() and 0x7fffffff) % 360f
            background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(Color.HSVToColor(floatArrayOf(hue, 0.55f, 0.75f))); setStroke(px(2), if (on) green else Color.TRANSPARENT) }
            layoutParams = LinearLayout.LayoutParams(px(44), px(44)).apply { rightMargin = px(12) }
        }
        return android.widget.ImageView(a).apply {
            setImageBitmap(bmp); scaleType = android.widget.ImageView.ScaleType.CENTER_CROP; clipToOutline = true; setPadding(px(2), px(2), px(2), px(2))
            background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(if (on) green else Color.GRAY) }
            outlineProvider = android.view.ViewOutlineProvider.BACKGROUND; layoutParams = LinearLayout.LayoutParams(px(44), px(44)).apply { rightMargin = px(12) }
        }
    }
    // 👤 mój profil: zdjęcie (wybór z plików) i blokada komunikatora
    var pickingPhoto = false
    // 📷 zdjęcie z aparatu: zrobione - wysyłka oryginału; anulowane - pusty wpis w Galerii usunięty
    private var photoUri: Uri? = null
    // 🖥 zgoda Androida na nagrywanie ekranu; nad innymi aplikacjami - żeby było widać wskazówkę 👆 rozmówcy
    private fun askScreen() {
        if (!android.provider.Settings.canDrawOverlays(a)) Toast.makeText(a, t("Aby widzieć wskazówki 👆 rozmówcy, zezwól Velivo na wyświetlanie nad innymi aplikacjami (Ustawienia → Aplikacje).",
            "To see the other person's 👆 hints, allow Velivo to display over other apps (Settings → Apps)."), Toast.LENGTH_LONG).show()
        val mpm = a.getSystemService(android.media.projection.MediaProjectionManager::class.java) ?: return
        runCatching { a.startActivityForResult(mpm.createScreenCaptureIntent(), Komunikator.REQ_SCREEN) }
    }
    fun screenResult(code: Int, data: Intent?) {
        val w = callView ?: return
        if (code != Activity.RESULT_OK || data == null) { w.evaluateJavascript("scrOff()", null); return }
        EkranUsluga.onFrame = { b -> w.post { if (callView === w) w.evaluateJavascript("scrFrame('" + b + "')", null) } }
        EkranUsluga.onStop = { w.post { if (callView === w) w.evaluateJavascript("scrOff()", null) } }
        runCatching { a.startForegroundService(Intent(a, EkranUsluga::class.java).putExtra("code", code).putExtra("data", data)) }
            .onSuccess { w.evaluateJavascript("scrOn()", null) }.onFailure { w.evaluateJavascript("scrOff()", null) }
        if (!android.provider.Settings.canDrawOverlays(a)) runCatching { a.startActivity(Intent(android.provider.Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:" + a.packageName))) }
    }
    fun cameraResult(ok: Boolean) {
        val u = photoUri ?: return; photoUri = null
        if (ok) sendFiles(listOf(u)) else { attachTo = null; runCatching { a.contentResolver.delete(u, null, null) } }
    }
    private fun profile() {
        val items = arrayOf("🖼 " + t("Moje zdjęcie profilowe…", "My profile photo…"), "🗑 " + t("Usuń moje zdjęcie", "Remove my photo"),
            "🔒 " + t("Blokada odciskiem palca / PIN-em: ", "Fingerprint / PIN lock: ") + if (k.lock) t("wł.", "on") else t("wył.", "off"),
            "💾 " + t("Kopia zapasowa (telefon, Google Drive, NUCBOX)…", "Backup (phone, Google Drive, NUCBOX)…"), "📥 " + t("Przywróć z kopii (nowy telefon)…", "Restore from backup (new phone)…"),
            "🔗 " + t("Mój adres Velivo (dla każdego)", "My Velivo address (for anyone)"))
        AlertDialog.Builder(a).setTitle("👤 " + t("Mój profil", "My profile")).setItems(items) { _, i ->
            when (i) {
                0 -> { pickingPhoto = true; pickFiles() }
                1 -> Thread { k.setPhoto(null) }.start()
                2 -> { k.lock = !k.lock; Toast.makeText(a, items[2].substringBefore(':') + ": " + if (k.lock) t("wł.", "on") else t("wył.", "off"), Toast.LENGTH_SHORT).show() }
                3 -> password(true) { p -> backupPass = p
                    runCatching { a.startActivityForResult(android.content.Intent(android.content.Intent.ACTION_CREATE_DOCUMENT).addCategory(android.content.Intent.CATEGORY_OPENABLE).setType("application/octet-stream")
                        .putExtra(android.content.Intent.EXTRA_TITLE, "Velivo-kopia-" + java.text.SimpleDateFormat("yyyy-MM-dd", Locale.ROOT).format(java.util.Date()) + ".velivo"), Komunikator.REQ_BACKUP) } }
                4 -> AlertDialog.Builder(a).setMessage(t("Kopia zastąpi kontakty i rozmowy w tym telefonie (telefon dostanie identyfikator z kopii – kontakty dalej Cię znają). Dalej?",
                        "The backup replaces contacts and chats on this phone (it gets the identity from the backup – contacts still know you). Continue?"))
                    .setPositiveButton(t("Wybierz plik", "Choose file")) { _, _ -> pickingBackup = true; pickFiles() }.setNegativeButton(android.R.string.cancel, null).show()
                else -> myAddress()
            }
        }.show()
    }
    // 💾 hasło kopii (min. 6 znaków); bez niego kopii nie da się odczytać - także na Google Drive
    private var backupPass: String? = null
    var pickingBackup = false
    // nowa kopia: hasło dwa razy (literówka = kopia nie do odczytania) i 👁 podgląd
    private fun password(create: Boolean, then: (String) -> Unit) {
        val hidden = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD; val shown = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD
        val e = EditText(a).apply { inputType = hidden; hint = t("Hasło kopii", "Backup password") }
        val e2 = EditText(a).apply { inputType = hidden; hint = t("Powtórz hasło", "Repeat password"); visibility = if (create) View.VISIBLE else View.GONE }
        val eye = android.widget.CheckBox(a).apply { text = "👁 " + t("Pokaż hasło", "Show password"); setTextColor(ink)
            setOnCheckedChangeListener { _, on -> for (x in listOf(e, e2)) { x.inputType = if (on) shown else hidden; x.setSelection(x.text.length) } } }
        val box = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(px(20), px(4), px(20), 0); addView(e); addView(e2); addView(eye) }
        AlertDialog.Builder(a).setTitle("🔑 " + t("Hasło kopii", "Backup password"))
            .setMessage(if (create) t("Zapamiętaj je – bez hasła kopii nie da się przywrócić (min. 6 znaków).", "Remember it – the backup cannot be restored without it (min. 6 characters).") else null).setView(box)
            .setPositiveButton(android.R.string.ok) { _, _ ->
                val p = e.text.toString()
                when {
                    p.length < 6 -> Toast.makeText(a, t("Za krótkie hasło", "Password too short"), Toast.LENGTH_SHORT).show()
                    create && p != e2.text.toString() -> Toast.makeText(a, t("Hasła się różnią – wpisz jeszcze raz", "Passwords differ – enter them again"), Toast.LENGTH_LONG).show()
                    else -> then(p)
                }
            }.setNegativeButton(android.R.string.cancel, null).show()
    }
    fun backupResult(u: Uri?) {
        val p = backupPass; backupPass = null
        if (u == null || p == null) return
        Toast.makeText(a, "💾 " + t("Zapisuję kopię…", "Saving backup…"), Toast.LENGTH_SHORT).show()
        Thread {
            val ok = runCatching { a.contentResolver.openOutputStream(u, "wt")!!.use { it.write(k.backup(p)) } }.isSuccess
            a.runOnUiThread { Toast.makeText(a, if (ok) "💾 " + t("Kopia zapisana", "Backup saved") else t("Nie udało się zapisać kopii", "Could not save the backup"), Toast.LENGTH_LONG).show() }
        }.start()
    }
    private fun restoreFrom(u: Uri) = password(false) { p ->
        Toast.makeText(a, "📥 " + t("Przywracam…", "Restoring…"), Toast.LENGTH_SHORT).show()
        Thread {
            val ok = runCatching { a.contentResolver.openInputStream(u)!!.use { it.readBytes() } }.getOrNull()?.let { k.restore(it, p) } ?: false
            a.runOnUiThread { Toast.makeText(a, if (ok) "📥 " + t("Przywrócono kontakty i rozmowy", "Contacts and chats restored") else t("Złe hasło albo to nie jest kopia Velivo z telefonu", "Wrong password or not a Velivo phone backup"), Toast.LENGTH_LONG).show(); if (ok) show() }
        }.start()
    }
    private fun setPhotoFrom(u: Uri) = Thread {
        val jpg = runCatching {
            val b = upright(a.contentResolver.openInputStream(u)!!.use { android.graphics.BitmapFactory.decodeStream(it) }!!, runCatching { a.contentResolver.openInputStream(u)!!.use { android.media.ExifInterface(it) } }.getOrNull())
            val n = minOf(b.width, b.height); val sq = android.graphics.Bitmap.createBitmap(b, (b.width - n) / 2, (b.height - n) / 2, n, n)
            java.io.ByteArrayOutputStream().also { android.graphics.Bitmap.createScaledBitmap(sq, 128, 128, true).compress(android.graphics.Bitmap.CompressFormat.JPEG, 80, it) }.toByteArray()
        }.getOrNull()
        a.runOnUiThread { Toast.makeText(a, if (jpg != null) "🖼 " + t("Zdjęcie wysłane do kontaktów", "Photo sent to contacts") else t("To nie jest zdjęcie", "This is not a photo"), Toast.LENGTH_SHORT).show() }
        if (jpg != null) k.setPhoto(jpg)
    }.start()

    private fun when_(ts: Long): String {
        val d = java.util.Calendar.getInstance().apply { timeInMillis = ts }; val now = java.util.Calendar.getInstance()
        return if (d.get(java.util.Calendar.YEAR) == now.get(java.util.Calendar.YEAR) && d.get(java.util.Calendar.DAY_OF_YEAR) == now.get(java.util.Calendar.DAY_OF_YEAR))
            java.text.SimpleDateFormat("HH:mm", Locale.getDefault()).format(java.util.Date(ts))
        else java.text.SimpleDateFormat("d MMM", Locale.getDefault()).format(java.util.Date(ts))
    }

    // 🔒 blokada: przy otwarciu komunikatora odcisk palca / twarz / PIN telefonu (jak w WhatsApp)
    private var pass = false
    private fun locked(then: () -> Unit): Boolean {
        if (pass || screen != null || !k.lock || a.getSystemService(android.app.KeyguardManager::class.java)?.isDeviceSecure != true) return false
        val b = android.hardware.biometrics.BiometricPrompt.Builder(a).setTitle("🔒 " + t("Komunikator Velivo", "Velivo messenger"))
        if (Build.VERSION.SDK_INT >= 30) b.setAllowedAuthenticators(android.hardware.biometrics.BiometricManager.Authenticators.BIOMETRIC_WEAK or android.hardware.biometrics.BiometricManager.Authenticators.DEVICE_CREDENTIAL)
        else @Suppress("DEPRECATION") b.setDeviceCredentialAllowed(true)
        runCatching { b.build().authenticate(android.os.CancellationSignal(), a.mainExecutor, object : android.hardware.biometrics.BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(r: android.hardware.biometrics.BiometricPrompt.AuthenticationResult?) { pass = true; then(); pass = false }
        }) }.onFailure { return false }
        return true
    }

    // lista rozmów
    fun show() {
        if (locked { show() }) return
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
            for (c in k.contacts.sortedWith(compareByDescending<Komunikator.Contact> { k.pinned(it.id) }.thenByDescending { k.chat(it.id).lastOrNull()?.ts ?: 0L })) {   // 0L - ten sam typ co czas (Int z Long = awaria sortowania)   // 📌 przypięte na górze
                val n = k.unread(c.id); val last = k.chat(c.id).lastOrNull(); val on = k.online(c.id)
                val dot = avatar(c, on)
                val name = TextView(a).apply {
                    text = android.text.SpannableStringBuilder((if (k.pinned(c.id)) "📌 " else "") + c.name + if (k.muted(c.id)) " 🔕" else "").apply {
                        if (c.own && c.device.isNotEmpty() && c.device != c.name) { val st = length; append("  " + c.device); setSpan(android.text.style.ForegroundColorSpan(dim), st, length, 0); setSpan(android.text.style.RelativeSizeSpan(0.75f), st, length, 0) }   // prawdziwe urządzenie
                    }; textSize = 17f; setTextColor(ink); if (n > 0) setTypeface(typeface, android.graphics.Typeface.BOLD); maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END; layoutParams = LinearLayout.LayoutParams(0, -2, 1f) }
                val time = TextView(a).apply { text = last?.let { when_(it.ts) } ?: ""; textSize = 12f; setTextColor(if (n > 0) green else dim) }
                val top = LinearLayout(a).apply { gravity = Gravity.CENTER_VERTICAL; addView(name); addView(time) }
                val snippet = TextView(a).apply {
                    val dr = k.draft(c.id)   // ✏ niedokończona wiadomość - jak w WhatsApp
                    text = if (dr.isNotEmpty()) "✏ " + t("Szkic: ", "Draft: ") + dr.replace('\n', ' ') else last?.let { (if (it.mine) (if (it.read) "✓✓ " else if (it.ok) "✓ " else "… ") else "") + it.text.replace('\n', ' ') } ?: (if (on) t("w sieci", "online") else t("poza siecią", "offline"))
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
        val me = icon("👤", t("Mój profil: zdjęcie i blokada", "My profile: photo and lock")) { profile() }
        val head = LinearLayout(a).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(0, px(8), px(8), px(8)); addView(title); addView(me); addView(palette); addView(bell); addView(close) }
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

    // 🔗 mój adres: link dla każdego (prośba → Dodaj / Odrzuć u mnie); „Zmień” - stary link przestaje działać
    private fun myAddress(reset: Boolean = false) {
        val link = k.myAddress(reset) ?: run { Toast.makeText(a, t("Potrzebny pośrednik (np. NUCBOX) w kontaktach – połącz się z nim raz w domu", "A relay (e.g. NUCBOX) is needed in contacts – connect to it once at home"), Toast.LENGTH_LONG).show(); return }
        val tv = TextView(a).apply { text = link; setTextIsSelectable(true); textSize = 13f; setPadding(px(20), px(10), px(20), px(4)); setTextColor(ink) }
        AlertDialog.Builder(a).setTitle("🔗 " + t("Mój adres Velivo", "My Velivo address"))
            .setMessage(t("Daj go każdemu, kto ma Velivo – wyśle Ci prośbę, a Ty zdecydujesz: Dodaj / Odrzuć. Dostanie tylko komunikator (bez Twojej sieci domowej).",
                "Give it to anyone with Velivo – they send you a request and you decide: Add / Decline. They get the messenger only (not your home network)."))
            .setView(tv)
            .setPositiveButton("📤 " + t("Udostępnij", "Share")) { _, _ -> runCatching { a.startActivity(android.content.Intent.createChooser(android.content.Intent(android.content.Intent.ACTION_SEND).setType("text/plain").putExtra(android.content.Intent.EXTRA_TEXT, t("Dodaj mnie w Velivo (Dodaj kontakt → 🔗 Mam czyjś adres): ", "Add me in Velivo (Add contact → 🔗 I have someone's address): ") + link), null)) } }
            .setNeutralButton("🔄 " + t("Zmień", "Change")) { _, _ -> myAddress(true) }
            .setNegativeButton(android.R.string.cancel, null).show()
    }
    private fun haveAddress() {
        val e = EditText(a).apply { hint = "VELIVO-KONTAKT:…"; minLines = 2 }
        AlertDialog.Builder(a).setTitle("🔗 " + t("Wklej adres Velivo", "Paste a Velivo address")).setView(e)
            .setPositiveButton(t("Wyślij prośbę", "Send request")) { _, _ ->
                val ok = k.requestByLink(e.text.toString().substringAfter("VELIVO-KONTAKT:", "").let { if (it.isEmpty()) e.text.toString() else "VELIVO-KONTAKT:$it" }.trim())
                Toast.makeText(a, if (ok) "👤 " + t("Prośba wysłana – kontakt pojawi się, gdy ta osoba ją przyjmie", "Request sent – the contact appears when they accept") else t("To nie jest adres Velivo", "This is not a Velivo address"), Toast.LENGTH_LONG).show()
            }.setNegativeButton(android.R.string.cancel, null).show()
    }

    // 👤 połączenie znajomych: np. mama i GPD znają się z domu, ale nie dodały się - teraz zostaną kontaktami także poza domem
    private fun introduce() {
        val cs = k.contacts.filter { !it.isGroup }
        if (cs.size < 2) { Toast.makeText(a, t("Potrzeba co najmniej 2 kontaktów", "At least 2 contacts are needed"), Toast.LENGTH_LONG).show(); return }
        val pick = BooleanArray(cs.size)
        AlertDialog.Builder(a).setTitle("👤 " + t("Kogo połączyć ze sobą?", "Who should be connected?"))
            .setMultiChoiceItems(cs.map { it.name }.toTypedArray(), pick) { _, i, on -> pick[i] = on }
            .setPositiveButton(t("Połącz", "Connect")) { _, _ ->
                val ids = cs.filterIndexed { i, _ -> pick[i] }.map { it.id }
                if (ids.size < 2) return@setPositiveButton
                k.introduce(ids)
                Toast.makeText(a, "👤 " + t("Wysłano – dodadzą się, gdy będą w sieci (także przez skrzynkę 📬)", "Sent – they will be added when online (also via the 📬 mailbox)"), Toast.LENGTH_LONG).show()
            }.setNegativeButton(android.R.string.cancel, null).show()
    }

    // 👥 nowa grupa: nazwa i osoby z kontaktów; pierwsza wiadomość tworzy grupę u wszystkich
    private fun newGroup() {
        val cs = k.contacts.filter { !it.isGroup }
        if (cs.size < 2) { Toast.makeText(a, t("Do grupy potrzeba co najmniej 2 kontaktów", "A group needs at least 2 contacts"), Toast.LENGTH_LONG).show(); return }
        val pick = BooleanArray(cs.size)
        AlertDialog.Builder(a).setTitle("👥 " + t("Kto w grupie?", "Who is in the group?"))
            .setMultiChoiceItems(cs.map { it.name }.toTypedArray(), pick) { _, i, on -> pick[i] = on }
            .setPositiveButton(t("Dalej", "Next")) { _, _ ->
                val ids = cs.filterIndexed { i, _ -> pick[i] }.map { it.id }
                if (ids.isEmpty()) return@setPositiveButton
                val e = EditText(a).apply { setSingleLine(); hint = t("np. Rodzina", "e.g. Family") }
                AlertDialog.Builder(a).setTitle("👥 " + t("Nazwa grupy", "Group name")).setView(e)
                    .setPositiveButton(t("Utwórz", "Create")) { _, _ -> openChat(k.newGroup(e.text.toString().trim().ifEmpty { t("Grupa", "Group") }.take(64), ids)) }
                    .setNegativeButton(android.R.string.cancel, null).show()
            }.setNegativeButton(android.R.string.cancel, null).show()
    }

    private fun addChoice() {
        AlertDialog.Builder(a).setTitle("➕ " + t("Dodaj kontakt", "Add contact"))
            .setItems(arrayOf("🔗 " + t("Mój adres Velivo (dla każdego)", "My Velivo address (for anyone)"), "🔗 " + t("Mam czyjś adres Velivo…", "I have someone's Velivo address…"), "👤 " + t("Połącz moich znajomych ze sobą…", "Connect my contacts with each other…"), "👥 " + t("Nowa grupa…", "New group…"), "📶 " + t("W tej sieci (z kodem)", "On this network (with a code)"), "✉ " + t("Zaproś przez internet…", "Invite via internet…"), "🔑 " + t("Mam zaproszenie…", "I have an invitation…"),
                "📶 " + (if (k.newDeviceNotifications) t("Powiadomienia o nowych urządzeniach: wł.", "New device notifications: on") else t("Powiadomienia o nowych urządzeniach: wył.", "New device notifications: off")))) { _, i ->
                when (i) { 0 -> myAddress(); 1 -> haveAddress(); 2 -> introduce(); 3 -> newGroup(); 4 -> addContact(); 5 -> invite(); 6 -> joinDialog(); else -> { k.newDeviceNotifications = !k.newDeviceNotifications; addChoice() } }
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
        val items = arrayOf("✏ " + t("Zmień nazwę", "Rename"), "🗑 " + t("Usuń kontakt i rozmowę", "Delete contact and chat"),
            if (k.pinned(c.id)) "📌 " + t("Odepnij", "Unpin") else "📌 " + t("Przypnij na górze", "Pin to top"),
            if (k.muted(c.id)) "🔔 " + t("Wyłącz wyciszenie", "Unmute") else "🔕 " + t("Wycisz…", "Mute…"),
            "⏳ " + t("Znikające wiadomości…", "Disappearing messages…"), "💾 " + t("Eksport rozmowy (TXT)", "Export chat (TXT)")) + if (c.isGroup) emptyArray() else arrayOf("🛡 " + t("Kod bezpieczeństwa", "Safety code"))
        AlertDialog.Builder(a).setTitle(c.name).setItems(items) { _, i ->
            if (i == 2) { k.setPinned(c.id, !k.pinned(c.id)); return@setItems }
            if (i == 3) {
                if (k.muted(c.id)) k.mute(c.id, 0)
                else { val ms = longArrayOf(8 * 3600000L, 7 * 86400000L, Long.MAX_VALUE)
                    AlertDialog.Builder(a).setTitle("🔕 " + c.name).setItems(arrayOf(t("8 godzin", "8 hours"), t("Tydzień", "A week"), t("Zawsze", "Always"))) { _, j -> k.mute(c.id, ms[j]) }.show() }
                return@setItems
            }
            if (i == 4) {
                val hs = intArrayOf(0, 24, 7 * 24, 90 * 24)
                AlertDialog.Builder(a).setTitle("⏳ " + t("Znikające wiadomości – u obu stron", "Disappearing messages – for both sides"))
                    .setSingleChoiceItems(arrayOf(t("Wyłączone", "Off"), "24 h", t("7 dni", "7 days"), t("90 dni", "90 days")), hs.indexOf(k.ttl(c.id)).coerceAtLeast(0)) { d, j -> d.dismiss(); Thread { k.setTtl(c, hs[j]) }.start() }.show()
                return@setItems
            }
            if (i == 5) { exportChat(c); return@setItems }
            if (i == 6) {
                AlertDialog.Builder(a).setTitle("🛡 " + t("Kod bezpieczeństwa", "Safety code") + " – " + c.name)
                    .setMessage(k.safety(c) + "\n\n" + t("Porównajcie ten kod (u ${c.name}: menu rozmowy z Tobą). Ten sam = rozmowa szyfrowana tylko między Wami, nikt się nie podszywa.",
                        "Compare this code (on ${c.name}'s device: your chat menu). The same = the chat is encrypted only between you, no one is impersonating."))
                    .setPositiveButton(android.R.string.ok, null).show()
                return@setItems
            }
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

    fun sendFiles(uris: List<Uri>, confirmed: Boolean = false) {
        if (uris.isEmpty()) return
        if (pickingPhoto) { pickingPhoto = false; setPhotoFrom(uris[0]); return }
        if (pickingBackup) { pickingBackup = false; restoreFrom(uris[0]); return }
        val c = attachTo; attachTo = null
        if (c != null && !confirmed) { preview(c, uris); return }   // 👁 najpierw podgląd i „Wyślij” (jak w WhatsApp)
        if (c != null && c.isGroup) {   // 👥 do każdego w grupie osobno
            Thread {
                for (u in uris) {
                    val name = runCatching { a.contentResolver.query(u, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)?.use { q -> if (q.moveToFirst()) q.getString(0) else null } }.getOrNull() ?: u.lastPathSegment ?: "plik"
                    val size = runCatching { a.contentResolver.openAssetFileDescriptor(u, "r")?.use { it.length } ?: -1L }.getOrDefault(-1L)
                    if (size < 0) continue
                    a.runOnUiThread { Toast.makeText(a, t("Wysyłam do grupy: ", "Sending to the group: ") + name, Toast.LENGTH_SHORT).show() }
                    val r = k.sendGroupFile(c, name, size) { a.contentResolver.openInputStream(u)!! }
                    a.runOnUiThread { Toast.makeText(a, name + ": " + if (r == "ok") t("wysłano", "sent") else "📬 " + t("dla nieobecnych czeka na serwerze", "waits on the server for those away"), Toast.LENGTH_LONG).show() }
                }
            }.start()
            return
        }
        if (c != null) {
            Thread {
                val known = runCatching { InetAddress.getByName(c.addr.ifEmpty { null } ?: throw Exception()) }.getOrNull()?.takeIf { !it.isLoopbackAddress }
                // 🟢 w sieci i znany adres - od razu, bez szukania
                val addr = (if (k.online(c.id)) known else null) ?: k.find().firstOrNull { it.id == c.id }?.addr ?: known ?: if (k.viaInternet(c.id)) InetAddress.getLoopbackAddress() else null   // poza domem: tunel przez pośrednika
                a.runOnUiThread {
                    // nie ma w sieci - i tak wysyłka: 📬 do skrzynki pośrednika albo czeka u nas i idzie sama
                    sendFilesTo(uris, Komunikator.Peer(c.id, c.name, addr ?: InetAddress.getLoopbackAddress()), c)
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

    // 👁 przed wysłaniem: zdjęcie / ▶ odsłuchaj głosówkę / obejrzyj film; Wyślij albo Anuluj (nagranie z Velivo - usuwane)
    private fun preview(c: Komunikator.Contact, uris: List<Uri>) {
        fun name(u: Uri) = runCatching { a.contentResolver.query(u, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)?.use { q -> if (q.moveToFirst()) q.getString(0) else null } }.getOrNull() ?: u.lastPathSegment ?: "plik"
        val names = uris.map { name(it) }
        val box = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(px(18), px(8), px(18), 0) }
        if (uris.size == 1 && k.isImage(names[0])) runCatching {
            val bmp = upright(a.contentResolver.openInputStream(uris[0])!!.use { android.graphics.BitmapFactory.decodeStream(it, null, android.graphics.BitmapFactory.Options().apply { inSampleSize = 4 }) }!!,
                runCatching { a.contentResolver.openInputStream(uris[0])!!.use { android.media.ExifInterface(it) } }.getOrNull())
            box.addView(android.widget.ImageView(a).apply { setImageBitmap(bmp); adjustViewBounds = true; maxHeight = px(360) })
        }
        box.addView(TextView(a).apply { text = names.joinToString("\n") { (if (k.isImage(it)) "🖼 " else if (it.startsWith("Glosowa-")) "🎤 " else "📎 ") + it }; setTextColor(ink); textSize = 15f; setPadding(0, px(8), 0, 0) })
        fun look(u: Uri, n: String) {
            if (u.scheme == "file") { if (n.startsWith("Glosowa-")) play(u.path!!) else openFile(u.path!!); return }
            runCatching { a.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(u, a.contentResolver.getType(u) ?: "*/*").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)) }
                .onFailure { Toast.makeText(a, t("Brak programu do podglądu", "No app to preview"), Toast.LENGTH_SHORT).show() }
        }
        val d = AlertDialog.Builder(a).setTitle("📤 " + c.name).setView(ScrollView(a).apply { addView(box) })
            .setPositiveButton("➤ " + t("Wyślij", "Send")) { _, _ -> attachTo = c; sendFiles(uris, true) }
            .setNegativeButton(t("Anuluj", "Cancel")) { _, _ -> uris.filter { it.scheme == "file" && it.path?.startsWith(a.cacheDir.path) == true }.forEach { java.io.File(it.path!!).delete() } }
        if (uris.size == 1) d.setNeutralButton(if (names[0].startsWith("Glosowa-")) "▶ " + t("Odsłuchaj", "Listen") else "👁 " + t("Podgląd", "Preview"), null)
        val dlg = d.show()
        // podgląd nie zamyka okienka - po odsłuchaniu dalej Wyślij / Anuluj
        dlg.getButton(AlertDialog.BUTTON_NEUTRAL)?.setOnClickListener { look(uris[0], names[0]) }
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
                        Toast.makeText(a, name + ": " + when (r) { "ok" -> t("wysłano", "sent"); "odrzucono" -> t("odrzucono", "declined")
                            "skrzynka" -> "📬 " + t("czeka na serwerze – dojdzie, gdy odbiorca się pojawi", "waiting on the server – arrives when the recipient appears")
                            "czeka" -> "⏳ " + t("wyślę sama, gdy odbiorca będzie w sieci", "will send when the recipient is online")
                            else -> t("błąd - ", "error - ") + r }, Toast.LENGTH_LONG).show()
                    }
                }
            }.start()
    }

    // rozmowa
    fun openChat(c: Komunikator.Contact) {
        if (locked { openChat(c) }) return
        val root = page()
        listRefresh = null
        k.markRead(c.id); chatWith = c.id; k.chatOpen = c.id; k.clearNotification(c.id); k.tidy(c.id)   // ⏳ stare znikające - usunięte
        var query = ""   // 🔍 szukanie w rozmowie
        k.sendRead(c)
        val msgs = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; setPadding(px(8), px(4), px(8), px(4)) }
        val scroll = ScrollView(a).apply { addView(msgs); layoutParams = LinearLayout.LayoutParams(-1, 0, 1f) }
        val fmt = java.text.SimpleDateFormat("HH:mm", Locale.getDefault())
        val dayFmt = java.text.SimpleDateFormat("EEEE, d MMMM", Locale.getDefault())
        fun fill() {
            msgs.removeAllViews()
            var lastDay = ""
            for (m in k.chat(c.id).takeLast(300).filter { query.isEmpty() || it.text.contains(query, true) }) {
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
                    setImageBitmap(img); m.path?.let { gif(this, it) }   // 🎞 GIF rusza się w rozmowie
                    adjustViewBounds = true; maxWidth = px(240); maxHeight = px(280); scaleType = android.widget.ImageView.ScaleType.FIT_CENTER
                    clipToOutline = true; outlineProvider = object : android.view.ViewOutlineProvider() { override fun getOutline(v: View, o: android.graphics.Outline) { o.setRoundRect(0, 0, v.width, v.height, px(14).toFloat()) } }
                    layoutParams = LinearLayout.LayoutParams(-2, -2).apply { gravity = if (m.mine) Gravity.END else Gravity.START; topMargin = px(6) }
                    contentDescription = t("Zdjęcie: ", "Photo: ") + m.text
                    setOnClickListener { showPhoto(m.path!!, photos(c)) }
                    setOnLongClickListener { msgMenu(c, m); true }
                })
                msgs.addView(TextView(a).apply {
                    val tick = if (m.mine) (if (m.read || m.ok) "  ✓✓" else "  ✓") else ""   // jak WhatsApp: ✓ wysłana, ✓✓ szare doręczona, ✓✓ niebieskie przeczytana
                    text = android.text.SpannableStringBuilder(m.text).apply {
                        if (m.text.startsWith("› ")) { val e = m.text.indexOf('\n').let { if (it < 0) length else it }; setSpan(android.text.style.QuoteSpan(violet), 0, e, 0); setSpan(android.text.style.StyleSpan(android.graphics.Typeface.ITALIC), 0, e, 0) }   // ↩ cytat
                        voiceLength(m.path)?.let { append(" · $it") }   // 🎤 długość głosówki
                        append("\n"); val st = length; append(k.reactions(m).let { if (it.isEmpty()) "" else "$it  " } + fmt.format(java.util.Date(m.ts)) + tick)
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
                    // 🔗 adresy w wiadomości - stuknięcie otwiera przeglądarkę
                    if (m.path == null && android.text.util.Linkify.addLinks(this, android.text.util.Linkify.WEB_URLS)) (text as? android.text.Spannable)?.let { sp ->
                        sp.getSpans(0, sp.length, android.text.style.URLSpan::class.java).forEach { u ->
                            val st = sp.getSpanStart(u); val en = sp.getSpanEnd(u); sp.removeSpan(u)
                            sp.setSpan(object : android.text.style.ClickableSpan() { override fun onClick(v: View) { openLink(u.url) } }, st, en, 0)
                        }
                    }
                    if (img != null) setOnClickListener { showPhoto(m.path!!, photos(c)) }
                    else if (m.path != null && java.io.File(m.path!!).exists() && java.io.File(m.path!!).name.contains("Glosowa-")) setOnClickListener { play(m.path!!) }   // 🎤 głosowa: odtwarzanie w rozmowie
                    else if (m.path != null && java.io.File(m.path!!).exists()) setOnClickListener { openFile(m.path!!) }   // plik: stuknięcie otwiera
                    else if (!m.file && (text as? android.text.Spanned)?.getSpans(0, text.length, android.text.style.ClickableSpan::class.java).isNullOrEmpty()) setOnClickListener { speak(m.text) }   // 🔊 tekst: stuknięcie czyta na głos (ponownie - stop)
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
        // 🎞 GIF-y i naklejki z klawiatury (Gboard i inne) - wysyłane jak plik
        if (Build.VERSION.SDK_INT >= 31) input.setOnReceiveContentListener(arrayOf("image/*", "video/*")) { _, p ->
            val clip = p.clip; val uris = (0 until clip.itemCount).mapNotNull { clip.getItemAt(it).uri }
            if (uris.isEmpty()) p else { attachTo = c; sendFiles(uris); null }
        }
        fun round(text: String, tip: String, color: Int, act: () -> Unit) = Button(a).apply {
            this.text = text; textSize = 18f; setTextColor(if (color == violet) pal().mineFg else ink); contentDescription = tip; tooltipText = tip; setPadding(0, 0, 0, 0)
            background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(color) }
            layoutParams = LinearLayout.LayoutParams(px(46), px(46)).apply { leftMargin = px(6); rightMargin = px(6) }
            setOnClickListener { act() }
        }
        // ↩ odpowiedź na wiadomość: cytat nad polem, idzie przed tekstem
        var replyTo: Komunikator.Msg? = null
        val replyBar = TextView(a).apply { textSize = 13f; setTextColor(dim); maxLines = 2; ellipsize = android.text.TextUtils.TruncateAt.END; visibility = View.GONE; setPadding(px(16), px(6), px(16), px(2))
            contentDescription = t("Anuluj odpowiedź", "Cancel reply"); setOnClickListener { replyTo = null; visibility = View.GONE } }
        onReply = { m -> replyTo = m; replyBar.text = "↩ " + k.quote(m).trim() + "   ✕"; replyBar.visibility = View.VISIBLE; input.requestFocus() }
        fun send() { val s = input.text.toString().trim(); if (s.isEmpty()) return; input.setText(""); k.send(c, (replyTo?.let { k.quote(it) } ?: "") + s); replyTo = null; replyBar.visibility = View.GONE; fill() }
        // 📎 plik albo 🎬 wiadomość wideo (aparat telefonu; nagranie idzie jak plik - także do skrzynki 📬)
        val clip = round("📎", t("Załącz plik albo nagraj wiadomość wideo", "Attach a file or record a video message"), pal().button) {
            AlertDialog.Builder(a).setItems(arrayOf("📎 " + t("Plik / zdjęcie", "File / photo"), "🎬 " + t("Nagraj wiadomość wideo", "Record a video message"), "📍 " + t("Moja lokalizacja", "My location"))) { _, i ->
                if (i == 2) { sendLocation(c) { fill() }; return@setItems }
                attachTo = c
                if (i == 0) pickFiles()
                else if (a.checkSelfPermission(android.Manifest.permission.CAMERA) != android.content.pm.PackageManager.PERMISSION_GRANTED) { attachTo = null; a.requestPermissions(arrayOf(android.Manifest.permission.CAMERA, android.Manifest.permission.RECORD_AUDIO), 4713) }
                else runCatching { a.startActivityForResult(android.content.Intent(android.provider.MediaStore.ACTION_VIDEO_CAPTURE).putExtra(android.provider.MediaStore.EXTRA_DURATION_LIMIT, 180), 32) }   // wynik: MainActivity (REQ_P2PFILE) -> sendFiles
                    .onFailure { attachTo = null; Toast.makeText(a, t("Brak aplikacji aparatu", "No camera app"), Toast.LENGTH_SHORT).show() }
            }.show()
        }
        // 📷 zdjęcie aparatem - jak w WhatsApp, ale w pełnej jakości (oryginał z aparatu, bez zmniejszania); kopia w Galerii (Obrazy/Velivo)
        val cam = round("📷", t("Stuknij – zdjęcie (pełna jakość), przytrzymaj – film", "Tap – photo (full quality), hold – video"), pal().button) {
            if (a.checkSelfPermission(android.Manifest.permission.CAMERA) != android.content.pm.PackageManager.PERMISSION_GRANTED) { a.requestPermissions(arrayOf(android.Manifest.permission.CAMERA), 4714); return@round }
            val u = runCatching { a.contentResolver.insert(android.provider.MediaStore.Images.Media.EXTERNAL_CONTENT_URI, android.content.ContentValues().apply {
                put(android.provider.MediaStore.Images.Media.DISPLAY_NAME, "Velivo-" + java.text.SimpleDateFormat("yyyy-MM-dd-HHmmss", Locale.ROOT).format(java.util.Date()) + ".jpg")
                put(android.provider.MediaStore.Images.Media.RELATIVE_PATH, android.os.Environment.DIRECTORY_PICTURES + "/Velivo") }) }.getOrNull()
            if (u == null || runCatching { a.startActivityForResult(android.content.Intent(android.provider.MediaStore.ACTION_IMAGE_CAPTURE).putExtra(android.provider.MediaStore.EXTRA_OUTPUT, u)
                    .addFlags(android.content.Intent.FLAG_GRANT_WRITE_URI_PERMISSION), Komunikator.REQ_PHOTO) }.isFailure) {
                u?.let { runCatching { a.contentResolver.delete(it, null, null) } }; Toast.makeText(a, t("Brak aplikacji aparatu", "No camera app"), Toast.LENGTH_SHORT).show()
            } else { attachTo = c; photoUri = u }
        }
        // 🎬 przytrzymanie 📷 - nagranie filmu (jak w WhatsApp); potem okienko podglądu i „Wyślij”
        cam.setOnLongClickListener {
            if (a.checkSelfPermission(android.Manifest.permission.CAMERA) != android.content.pm.PackageManager.PERMISSION_GRANTED || a.checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) != android.content.pm.PackageManager.PERMISSION_GRANTED) {
                a.requestPermissions(arrayOf(android.Manifest.permission.CAMERA, android.Manifest.permission.RECORD_AUDIO), 4713); return@setOnLongClickListener true }
            attachTo = c
            runCatching { a.startActivityForResult(android.content.Intent(android.provider.MediaStore.ACTION_VIDEO_CAPTURE).putExtra(android.provider.MediaStore.EXTRA_DURATION_LIMIT, 180), 32) }
                .onFailure { attachTo = null; Toast.makeText(a, t("Brak aplikacji aparatu", "No camera app"), Toast.LENGTH_SHORT).show() }
            true
        }
        // 🎤 wiadomość głosowa: stuknij - nagrywanie, stuknij ponownie - wysyłka (jak plik: w domu, przez internet, do skrzynki)
        var rec: android.media.MediaRecorder? = null; var recFile: java.io.File? = null; var recAt = 0L
        val mic = round("🎤", t("Wiadomość głosowa: stuknij, mów, stuknij ponownie, by wysłać", "Voice message: tap, speak, tap again to send"), pal().button) {}
        mic.setOnClickListener {
            val r = rec
            if (r == null) {
                if (a.checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) != android.content.pm.PackageManager.PERMISSION_GRANTED) { a.requestPermissions(arrayOf(android.Manifest.permission.RECORD_AUDIO), 4712); return@setOnClickListener }
                val f = java.io.File(a.cacheDir, "Glosowa-" + java.text.SimpleDateFormat("yyyy-MM-dd-HHmmss", Locale.ROOT).format(java.util.Date()) + ".m4a")
                rec = runCatching {
                    @Suppress("DEPRECATION") (if (Build.VERSION.SDK_INT >= 31) android.media.MediaRecorder(a) else android.media.MediaRecorder()).apply {
                        setAudioSource(android.media.MediaRecorder.AudioSource.MIC); setOutputFormat(android.media.MediaRecorder.OutputFormat.MPEG_4)
                        setAudioEncoder(android.media.MediaRecorder.AudioEncoder.AAC); setAudioEncodingBitRate(64000); setAudioSamplingRate(44100)
                        setOutputFile(f.path); prepare(); start()
                    }
                }.getOrNull()
                if (rec != null) { recFile = f; recAt = System.currentTimeMillis(); mic.text = "⏹"; (mic.background as? GradientDrawable)?.setColor(Color.rgb(229, 57, 53))
                    Toast.makeText(a, "🎤 " + t("Nagrywam… stuknij ⏹, by wysłać", "Recording… tap ⏹ to send"), Toast.LENGTH_SHORT).show() }
                else Toast.makeText(a, t("Nie udało się włączyć mikrofonu", "Could not start the microphone"), Toast.LENGTH_SHORT).show()
            } else {
                rec = null; val ok = runCatching { r.stop() }.isSuccess; r.release()
                mic.text = "🎤"; (mic.background as? GradientDrawable)?.setColor(pal().button)
                val f = recFile; recFile = null
                if (ok && f != null && System.currentTimeMillis() - recAt > 700) { attachTo = c; sendFiles(listOf(Uri.fromFile(f))) } else f?.delete()
            }
        }
        val btn = round("➤", t("Wyślij", "Send"), violet) {   // klawiatura Velivo najpierw poprawia ostatnie słowo, potem wysyłka
            (a.getSystemService(Context.INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager).sendAppPrivateCommand(input, "pl.velivo.slowo", null); input.postDelayed({ send() }, 150) }
        input.setOnEditorActionListener { _, id, _ -> if (id == android.view.inputmethod.EditorInfo.IME_ACTION_SEND) { send(); true } else false }
        val row = LinearLayout(a).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(px(4), px(6), px(4), px(8)); addView(clip); addView(cam); addView(mic); addView(input); addView(btn) }
        val back = icon("←", t("Rozmowy", "Chats"), 22f) { show() }.apply { (layoutParams as LinearLayout.LayoutParams).apply { leftMargin = px(6); rightMargin = px(6) } }
        val title = TextView(a).apply { textSize = 18f; maxLines = 3; ellipsize = android.text.TextUtils.TruncateAt.END; layoutParams = LinearLayout.LayoutParams(0, -2, 1f) }
        fun status() {
            if (c.isGroup) {   // 👥 grupa: skład zamiast „w sieci”
                val names = k.members(c).map { id -> k.contact(id)?.name ?: "…" }
                title.text = android.text.SpannableStringBuilder("👥 " + c.name).apply { append("\n"); val st = length; append(names.joinToString(", ")); setSpan(android.text.style.RelativeSizeSpan(0.7f), st, length, 0); setSpan(android.text.style.ForegroundColorSpan(Color.GRAY), st, length, 0) }
                title.setTextColor(ink); return
            }
            val on = k.online(c.id)
            if (k.typing(c.id)) {   // ✍ „pisze…”
                title.text = android.text.SpannableStringBuilder(c.name).apply { append("\n"); val st = length; append("✍ " + t("pisze…", "typing…")); setSpan(android.text.style.RelativeSizeSpan(0.7f), st, length, 0); setSpan(android.text.style.ForegroundColorSpan(green), st, length, 0) }
                title.setTextColor(ink); return
            }
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
        val head = LinearLayout(a).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(0, px(4), px(8), px(6)); background = ColorDrawableCompat(Color.argb(50, 0, 0, 0)); addView(back)
            addView(avatar(c, !c.isGroup && k.online(c.id)))   // 🖼 zdjęcie profilowe rozmówcy (bez zdjęcia - litera)
            addView(title)
            addView(icon("🔍", t("Szukaj w rozmowie", "Search in chat")) {
                if (query.isNotEmpty()) { query = ""; fill(); return@icon }   // ponownie - wszystkie wiadomości
                val e = EditText(a).apply { setSingleLine(); hint = t("Słowo…", "Word…") }
                AlertDialog.Builder(a).setTitle("🔍 " + t("Szukaj w rozmowie", "Search in chat")).setView(e).setPositiveButton(android.R.string.ok) { _, _ ->
                    query = e.text.toString().trim(); fill()
                    Toast.makeText(a, t("Znaleziono: ", "Found: ") + k.chat(c.id).count { query.isNotEmpty() && it.text.contains(query, true) } + t(" (🔍 ponownie – wszystkie)", " (🔍 again – all)"), Toast.LENGTH_LONG).show()
                }.setNegativeButton(android.R.string.cancel, null).show()
            })
            addView(icon("🖼", t("Zdjęcia, filmy i pliki z rozmowy", "Photos, videos and files in this chat")) { gallery(c) })
            if (!c.isGroup) { addView(icon("📞", t("Rozmowa głosowa", "Voice call")) { openCall(c, true, true) }); addView(icon("📹", t("Rozmowa wideo", "Video call")) { openCall(c, true) }) } }
        input.setText(k.draft(c.id)); input.setSelection(input.text.length)
        fill(); status(); ringPending(c)
        input.addTextChangedListener(object : android.text.TextWatcher {
            override fun beforeTextChanged(x: CharSequence?, st: Int, n: Int, af: Int) {}
            override fun onTextChanged(x: CharSequence?, st: Int, b: Int, n: Int) { if (n > 0) k.sendTyping(c) }   // ✍ druga osoba widzi „pisze…”
            override fun afterTextChanged(x: android.text.Editable?) { cam.visibility = if (x.isNullOrEmpty()) View.VISIBLE else View.GONE; k.setDraft(c.id, x?.toString() ?: "") }   // ✏ szkic zostaje po wyjściu   // pisanie - więcej miejsca na tekst (jak w WhatsApp)
        })
        k.onTyping = { id -> if (id == c.id && chatWith == c.id) { status(); input.postDelayed({ if (chatWith == c.id) status() }, 6500) } }
        chatRefresh = { k.markRead(c.id); k.sendRead(c); fill(); status() }
        root.removeAllViews()
        root.addView(LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; addView(head); addView(scroll); addView(replyBar); addView(row) })
    }

    // przytrzymanie wiadomości: kopiuj, (zdjęcie) zapisz w Galerii, usuń u mnie
    private fun msgMenu(c: Komunikator.Contact, m: Komunikator.Msg) {
        val photo = m.path?.takeIf { k.isImage(it) && java.io.File(it).exists() }
        val file = m.path?.takeIf { !k.isImage(it) && java.io.File(it).exists() && !it.substringAfterLast('/').startsWith("wyslane-") }
        val items = mutableListOf("↩ " + t("Odpowiedz", "Reply"), "😀 " + t("Reakcja", "React"), "↪ " + t("Przekaż…", "Forward…"), "📋 " + t("Kopiuj", "Copy"))
        if (m.mine && !m.file && !m.text.startsWith("🚫") && System.currentTimeMillis() - m.ts < 15 * 60000) items.add("✏ " + t("Edytuj", "Edit"))   // jak w WhatsApp: do 15 min
        if (m.path?.contains("Glosowa-") == true) items.add("⏩ " + t("Szybkość głosówek: ", "Voice speed: ") + speedText(k.voiceSpeed) + " → " + speedText(nextSpeed()))
        if (photo != null) items.add("💾 " + t("Zapisz w Galerii", "Save to Gallery"))
        if (file != null) items.add("💾 " + t("Zapisz w Pobranych", "Save to Downloads"))
        if (m.mine && !m.text.startsWith("🚫")) items.add("🚫 " + t("Usuń u wszystkich", "Delete for everyone"))
        items.add("🗑 " + t("Usuń u mnie", "Delete for me"))
        items.add("☑ " + t("Zaznacz kilka…", "Select several…"))
        AlertDialog.Builder(a).setItems(items.toTypedArray()) { _, i ->
            if (items[i].startsWith("⏩")) { k.voiceSpeed = nextSpeed(); Toast.makeText(a, "⏩ " + speedText(k.voiceSpeed), Toast.LENGTH_SHORT).show(); return@setItems }
            when (items[i].substringAfter(' ')) {
                t("Odpowiedz", "Reply") -> onReply?.invoke(m)
                t("Reakcja", "React") -> { val e = arrayOf("👍", "❤️", "😂", "😮", "😢", "🙏"); AlertDialog.Builder(a).setItems(e) { _, j -> Thread { k.react(c, m, e[j]) }.start() }.show() }
                t("Przekaż…", "Forward…") -> forward(c, m)
                t("Zaznacz kilka…", "Select several…") -> select(c, m)
                t("Edytuj", "Edit") -> { val e = EditText(a).apply { setText(m.text.removeSuffix(" ✏")); setSelection(text.length) }
                    AlertDialog.Builder(a).setTitle("✏ " + t("Edytuj wiadomość", "Edit message")).setView(e)
                        .setPositiveButton(t("Zapisz", "Save")) { _, _ -> val s2 = e.text.toString().trim(); if (s2.isNotEmpty()) Thread { k.edit(c, m, s2) }.start() }.setNegativeButton(android.R.string.cancel, null).show() }
                t("Kopiuj", "Copy") -> { (a.getSystemService(Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager).setPrimaryClip(android.content.ClipData.newPlainText("Velivo", m.text)); Toast.makeText(a, t("Skopiowano", "Copied"), Toast.LENGTH_SHORT).show() }
                t("Zapisz w Galerii", "Save to Gallery") -> saveToGallery(photo!!)
                t("Zapisz w Pobranych", "Save to Downloads") -> Toast.makeText(a, if (toDownloads(file!!) != null) t("Zapisano w Pobranych", "Saved to Downloads") else t("Nie udało się zapisać", "Could not save"), Toast.LENGTH_SHORT).show()
                t("Usuń u wszystkich", "Delete for everyone") -> AlertDialog.Builder(a).setMessage(t("Usunąć tę wiadomość u wszystkich?", "Delete this message for everyone?"))
                    .setPositiveButton(t("Usuń", "Delete")) { _, _ -> Thread { k.deleteForAll(c, m) }.start() }.setNegativeButton(android.R.string.cancel, null).show()
                else -> k.deleteLocal(c, m)
            }
        }.show()
    }

    // ☑ kilka wiadomości naraz: przekaż, kopiuj, usuń u mnie
    private fun select(c: Komunikator.Contact, first: Komunikator.Msg) {
        val ms = k.chat(c.id).takeLast(100).reversed(); val pick = BooleanArray(ms.size) { ms[it] === first }
        val fmt = java.text.SimpleDateFormat("d.MM HH:mm", Locale.getDefault())
        AlertDialog.Builder(a).setTitle("☑ " + c.name).setMultiChoiceItems(ms.map { (if (it.mine) "› " else "") + it.text.take(80) + " · " + fmt.format(java.util.Date(it.ts)) }.toTypedArray(), pick) { _, i, on -> pick[i] = on }
            .setPositiveButton("↪ " + t("Przekaż", "Forward")) { _, _ -> ms.filterIndexed { i, _ -> pick[i] }.reversed().let { sel -> forwardAll(c, sel) } }
            .setNeutralButton("📋 " + t("Kopiuj", "Copy")) { _, _ ->
                val txt = ms.filterIndexed { i, _ -> pick[i] }.reversed().joinToString("\n") { "[" + fmt.format(java.util.Date(it.ts)) + "] " + (if (it.mine) t("Ja", "Me") else c.name) + ": " + it.text }
                (a.getSystemService(Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager).setPrimaryClip(android.content.ClipData.newPlainText("Velivo", txt)); Toast.makeText(a, t("Skopiowano", "Copied"), Toast.LENGTH_SHORT).show() }
            .setNegativeButton("🗑 " + t("Usuń u mnie", "Delete for me")) { _, _ -> val sel = ms.filterIndexed { i, _ -> pick[i] }
                if (sel.isNotEmpty()) AlertDialog.Builder(a).setMessage(t("Usunąć zaznaczone u mnie: ", "Delete selected for me: ") + sel.size + "?")
                    .setPositiveButton(t("Usuń", "Delete")) { _, _ -> sel.forEach { k.deleteLocal(c, it) } }.setNegativeButton(android.R.string.cancel, null).show() }
            .show()
    }

    private var onReply: ((Komunikator.Msg) -> Unit)? = null
    // ↪ przekazanie: tekst albo plik do wybranych osób / grup
    private fun forward(from: Komunikator.Contact, m: Komunikator.Msg) = forwardAll(from, listOf(m))
    private fun forwardAll(from: Komunikator.Contact, ms: List<Komunikator.Msg>) {
        val cs = k.contacts.filter { it.id != from.id }; if (cs.isEmpty() || ms.isEmpty()) return
        val pick = BooleanArray(cs.size)
        AlertDialog.Builder(a).setTitle("↪ " + t("Przekaż do…", "Forward to…")).setMultiChoiceItems(cs.map { (if (it.isGroup) "👥 " else "") + it.name }.toTypedArray(), pick) { _, i, on -> pick[i] = on }
            .setPositiveButton(t("Wyślij", "Send")) { _, _ ->
                for (to in cs.filterIndexed { i, _ -> pick[i] }) {
                    val fs = ms.mapNotNull { m -> m.path?.let { java.io.File(it) }?.takeIf { m.file && it.exists() } }
                    if (fs.isNotEmpty()) { attachTo = to; sendFiles(fs.map { Uri.fromFile(it) }, true) }   // przekazanie - bez drugiego pytania
                    val txt = ms.filter { m -> !(m.file && m.path?.let { java.io.File(it).exists() } == true) }.map { it.text }
                    if (txt.isNotEmpty()) Thread { txt.forEach { k.send(to, "↪ " + it) } }.start()
                }
            }.setNegativeButton(android.R.string.cancel, null).show()
    }
    // 🖼 zdjęcia, filmy, głosówki i pliki z rozmowy w jednym miejscu (najnowsze u góry)
    private fun gallery(c: Komunikator.Contact) {
        val ms = k.chat(c.id).filter { m -> m.path?.let { java.io.File(it).exists() } == true }.reversed()
        if (ms.isEmpty()) { Toast.makeText(a, t("Brak plików w tej rozmowie", "No files in this chat"), Toast.LENGTH_SHORT).show(); return }
        AlertDialog.Builder(a).setTitle("🖼 " + c.name).setItems(ms.map { (if (k.isImage(it.path!!)) "🖼 " else "") + it.text + " · " + when_(it.ts) }.toTypedArray()) { _, i ->
            val p = ms[i].path!!
            if (k.isImage(p)) showPhoto(p, photos(c)) else if (p.contains("Glosowa-")) play(p) else openFile(p)
        }.show()
    }

    // 💾 rozmowa jako tekst w Pobranych
    private fun exportChat(c: Komunikator.Contact) {
        val fmt = java.text.SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.getDefault())
        val f = java.io.File(a.cacheDir, "Velivo " + c.name.filter { it.isLetterOrDigit() || it == ' ' }.trim() + " " + java.text.SimpleDateFormat("yyyy-MM-dd HH-mm", Locale.getDefault()).format(java.util.Date()) + ".txt")
        f.writeText(k.chat(c.id).joinToString("\n") { "[" + fmt.format(java.util.Date(it.ts)) + "] " + (if (it.mine) t("Ja", "Me") else c.name) + ": " + it.text })
        Toast.makeText(a, if (toDownloads(f.path) != null) t("Zapisano w Pobranych: ", "Saved to Downloads: ") + f.name else t("Nie udało się zapisać", "Could not save"), Toast.LENGTH_LONG).show()
        f.delete()
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

    // 🔊 czytanie wiadomości na głos (syntezator mowy telefonu)
    private var tts: android.speech.tts.TextToSpeech? = null
    private fun speak(text: String) {
        val say = { t0: android.speech.tts.TextToSpeech -> if (t0.isSpeaking) t0.stop() else t0.speak(text, android.speech.tts.TextToSpeech.QUEUE_FLUSH, null, "kom") }
        tts?.let { say(it); return }
        tts = android.speech.tts.TextToSpeech(a) { st -> if (st == android.speech.tts.TextToSpeech.SUCCESS) tts?.let { it.language = Locale.getDefault(); say(it) } }
    }

    // 🎤 odtwarzanie wiadomości głosowej w rozmowie (stuknięcie ponownie - stop)
    private var player: android.media.MediaPlayer? = null; private var playing: String? = null
    private fun nextSpeed() = when (k.voiceSpeed) { 1f -> 1.5f; 1.5f -> 2f; else -> 1f }
    private fun speedText(x: Float) = if (x == 1.5f) t("1,5×", "1.5×") else "${x.toInt()}×"
    // 🎤 długość głosówki (raz na plik)
    private val lengths = HashMap<String, String>()
    private fun voiceLength(path: String?): String? {
        if (path == null || !path.contains("Glosowa-") || !java.io.File(path).exists()) return null
        return lengths.getOrPut(path) {
            val ms = runCatching { android.media.MediaMetadataRetriever().run { setDataSource(path); (extractMetadata(android.media.MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLong() ?: 0L).also { release() } } }.getOrDefault(0L)
            if (ms <= 0) "" else "%d:%02d".format(Locale.ROOT, ms / 60000, ms / 1000 % 60)
        }.ifEmpty { null }
    }
    // 📍 lokalizacja jednorazowo: link do mapy (stuknięcie u odbiorcy otwiera Mapy); bez śledzenia
    @android.annotation.SuppressLint("MissingPermission")
    private fun sendLocation(c: Komunikator.Contact, done: () -> Unit) {
        val perms = arrayOf(android.Manifest.permission.ACCESS_FINE_LOCATION, android.Manifest.permission.ACCESS_COARSE_LOCATION)
        if (perms.none { a.checkSelfPermission(it) == android.content.pm.PackageManager.PERMISSION_GRANTED }) {
            a.requestPermissions(perms, 4715); Toast.makeText(a, t("Zezwól na lokalizację i wybierz 📍 jeszcze raz", "Allow location and choose 📍 again"), Toast.LENGTH_LONG).show(); return }
        val lm = a.getSystemService(android.location.LocationManager::class.java) ?: return
        // jak Mapy: słuchamy naraz wszystkich dozwolonych źródeł (lokalizacja telefonu, sieć, GPS) i bierzemy pierwszy wynik;
        // po 40 s - ostatnie znane położenie z dowolnego źródła (w budynku GPS często milczy, sieć bywa wyłączona w ustawieniach)
        val provs = runCatching { lm.getProviders(true) }.getOrDefault(emptyList())
        val live = listOf("fused", android.location.LocationManager.NETWORK_PROVIDER, android.location.LocationManager.GPS_PROVIDER).filter { it in provs }
        if (live.isEmpty()) { Toast.makeText(a, t("Włącz lokalizację w telefonie", "Turn on location on the phone"), Toast.LENGTH_LONG).show(); return }
        Toast.makeText(a, "📍 " + t("Ustalam położenie…", "Finding location…"), Toast.LENGTH_LONG).show()
        val h = Handler(Looper.getMainLooper()); var sent = false
        lateinit var listener: android.location.LocationListener
        fun send(l: android.location.Location?) {
            if (sent) return
            sent = true; h.removeCallbacksAndMessages(null); runCatching { lm.removeUpdates(listener) }
            if (l == null) {   // co telefon ma - przyczyna widoczna od razu
                val fine = a.checkSelfPermission(android.Manifest.permission.ACCESS_FINE_LOCATION) == android.content.pm.PackageManager.PERMISSION_GRANTED
                Toast.makeText(a, t("Nie udało się ustalić położenia – podejdź do okna albo włącz Wi-Fi", "Could not get the location – move near a window or turn on Wi-Fi") +
                    " (" + live.joinToString(", ") + "; " + t("dokładna: ", "precise: ") + (if (fine) t("tak", "yes") else t("nie", "no")) + ")", Toast.LENGTH_LONG).show(); return }
            k.send(c, "📍 " + t("Moja lokalizacja", "My location") + "\nhttps://maps.google.com/?q=" + String.format(Locale.ROOT, "%.6f,%.6f", l.latitude, l.longitude)); done()
        }
        listener = object : android.location.LocationListener {   // wszystkie metody - Android 10 nie ma domyślnych
            override fun onLocationChanged(l: android.location.Location) = send(l)
            override fun onProviderEnabled(p: String) {}
            override fun onProviderDisabled(p: String) {}
            @Deprecated("Android 10") override fun onStatusChanged(p: String?, st: Int, e: android.os.Bundle?) {}
        }
        // świeże ostatnie położenie (do 2 min) - od razu, bez czekania
        provs.mapNotNull { p -> runCatching { lm.getLastKnownLocation(p) }.getOrNull() }.maxByOrNull { it.time }
            ?.takeIf { System.currentTimeMillis() - it.time < 120000 }?.let { send(it); return }
        live.forEach { p -> runCatching { lm.requestLocationUpdates(p, 0L, 0f, listener, Looper.getMainLooper()) } }
        h.postDelayed({ send(provs.mapNotNull { p -> runCatching { lm.getLastKnownLocation(p) }.getOrNull() }.maxByOrNull { it.time }) }, 40000)
    }

    private fun play(path: String) {
        val was = playing; player?.let { runCatching { it.stop() }; it.release() }; player = null; playing = null
        if (was == path) return
        player = runCatching { android.media.MediaPlayer().apply { setDataSource(path); setOnCompletionListener { it.release(); if (player === it) { player = null; playing = null } }; prepare(); start()
            if (k.voiceSpeed != 1f) playbackParams = playbackParams.setSpeed(k.voiceSpeed) } }.getOrNull()   // ⏩ 1,5× / 2×
        if (player != null) { playing = path; Toast.makeText(a, "▶ " + t("Wiadomość głosowa (stuknij, by zatrzymać)", "Voice message (tap to stop)"), Toast.LENGTH_SHORT).show() }
        else Toast.makeText(a, t("Nie udało się odtworzyć", "Could not play"), Toast.LENGTH_SHORT).show()
    }

    // link: najpierw aplikacja przypisana do takich linków (np. YouTube), inaczej domyślna przeglądarka
    private fun openLink(url: String) {
        val i = android.content.Intent(android.content.Intent.ACTION_VIEW, Uri.parse(url)).addCategory(android.content.Intent.CATEGORY_BROWSABLE)
        if (Build.VERSION.SDK_INT >= 30 && runCatching { a.startActivity(android.content.Intent(i).addFlags(android.content.Intent.FLAG_ACTIVITY_REQUIRE_NON_BROWSER)) }.isSuccess) return
        // przeglądarką jest samo Velivo - komunikator się zamyka, żeby strona była widoczna (inaczej otwierała się pod nim)
        if (a.packageManager.resolveActivity(i, android.content.pm.PackageManager.MATCH_DEFAULT_ONLY)?.activityInfo?.packageName == a.packageName) screen?.dismiss()
        runCatching { a.startActivity(i) }.onFailure { Toast.makeText(a, t("Nie udało się otworzyć", "Could not open"), Toast.LENGTH_SHORT).show() }
    }

    private fun openFile(path: String) {
        // 📦 aplikacja (.apk): instalator Androida; za pierwszym razem zgoda „Instaluj nieznane aplikacje” dla Velivo
        if (path.endsWith(".apk", true) && !a.packageManager.canRequestPackageInstalls()) {
            Toast.makeText(a, t("Pozwól Velivo instalować aplikacje, wróć i stuknij plik jeszcze raz", "Allow Velivo to install apps, then come back and tap the file again"), Toast.LENGTH_LONG).show()
            runCatching { a.startActivity(android.content.Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + a.packageName))) }
            return
        }
        val uri = toDownloads(path) ?: run { Toast.makeText(a, t("Nie udało się otworzyć", "Could not open"), Toast.LENGTH_SHORT).show(); return }
        val mime = android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(path.substringAfterLast('.', "").lowercase()) ?: if (path.endsWith(".apk", true)) "application/vnd.android.package-archive" else "*/*"
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
        upright(android.graphics.BitmapFactory.decodeFile(path, android.graphics.BitmapFactory.Options().apply { inSampleSize = smp }), runCatching { android.media.ExifInterface(path) }.getOrNull())
    }.getOrNull()

    // zdjęcie na cały ekran (stuknięcie zamyka)
    // 🎞 ruchomy GIF (Android 9+: AnimatedImageDrawable); inne obrazy - bez zmian
    private val gifs = android.util.LruCache<String, android.graphics.drawable.AnimatedImageDrawable>(12)   // ruchome GIF-y - dekodowane raz, nie przy każdym odświeżeniu
    private fun gif(v: android.widget.ImageView, path: String) {
        if (!path.endsWith(".gif", true)) return
        runCatching {
            val d = gifs.get(path) ?: (android.graphics.ImageDecoder.decodeDrawable(android.graphics.ImageDecoder.createSource(java.io.File(path))) as? android.graphics.drawable.AnimatedImageDrawable)?.also { gifs.put(path, it) } ?: return
            v.setImageDrawable(d); d.start()
        }
    }
    private fun photos(c: Komunikator.Contact) = k.chat(c.id).mapNotNull { m -> m.path?.takeIf { k.isImage(it) && java.io.File(it).exists() } }
    // przesunięcie palcem ←/→ - poprzednie/następne zdjęcie z rozmowy
    private fun showPhoto(path: String, all: List<String> = listOf(path)) {
        var i = all.indexOf(path).coerceAtLeast(0); val list = if (all.contains(path)) all else listOf(path)
        val max = maxOf(a.resources.displayMetrics.widthPixels, a.resources.displayMetrics.heightPixels)
        val d = android.app.Dialog(a, android.R.style.Theme_Black_NoTitleBar_Fullscreen)
        val v = android.widget.ImageView(a).apply { scaleType = android.widget.ImageView.ScaleType.FIT_CENTER; setBackgroundColor(Color.BLACK)
            contentDescription = t("Zdjęcie – stuknij, aby zamknąć; przesuń, aby zobaczyć następne", "Photo – tap to close; swipe for the next one") }
        fun show() { v.setImageBitmap(decode(list[i], max)); gif(v, list[i]) }
        val g = android.view.GestureDetector(a, object : android.view.GestureDetector.SimpleOnGestureListener() {
            override fun onDown(e: android.view.MotionEvent) = true
            override fun onSingleTapUp(e: android.view.MotionEvent): Boolean { d.dismiss(); return true }
            override fun onLongPress(e: android.view.MotionEvent) { saveToGallery(list[i]) }
            override fun onFling(e1: android.view.MotionEvent?, e2: android.view.MotionEvent, vx: Float, vy: Float): Boolean {
                if (Math.abs(vx) < Math.abs(vy)) return false
                val n = if (vx < 0) i + 1 else i - 1; if (n in list.indices) { i = n; show() }; return true
            }
        })
        v.setOnTouchListener { _, e -> g.onTouchEvent(e) }
        show(); d.setContentView(v); d.show()
    }

    private fun ColorDrawableCompat(c: Int) = android.graphics.drawable.ColorDrawable(c)
}

// 🖥 ekran telefonu w rozmowie (pomoc zdalna): Android pyta o zgodę, pasek powiadomień pokazuje, że ekran jest pokazywany.
// Klatki JPEG (ok. 3 na sekundę, szerokość do 720) idą do strony rozmowy, stamtąd kanałem danych WebRTC prosto do rozmówcy.
class EkranUsluga : Service() {
    private var mp: android.media.projection.MediaProjection? = null
    private var vd: android.hardware.display.VirtualDisplay? = null
    private var reader: android.media.ImageReader? = null
    private var th: android.os.HandlerThread? = null
    override fun onBind(i: Intent?): IBinder? = null
    override fun onStartCommand(i: Intent?, flags: Int, id: Int): Int {
        val pl = Locale.getDefault().language == "pl"
        val nm = getSystemService(NotificationManager::class.java)
        nm?.createNotificationChannel(NotificationChannel("ekran", if (pl) "Pokazywanie ekranu" else "Screen sharing", NotificationManager.IMPORTANCE_LOW))
        val n = Notification.Builder(this, "ekran").setSmallIcon(android.R.drawable.ic_menu_view).setOngoing(true)
            .setContentTitle(if (pl) "Velivo pokazuje ekran rozmówcy" else "Velivo is sharing your screen")
            .setContentText(if (pl) "Zakończ w rozmowie (🖥) albo kończąc rozmowę" else "Stop in the call (🖥) or by ending the call").build()
        runCatching {
            // 🎤 z mikrofonem - rozmowę słychać dalej, gdy ktoś przejdzie do innej aplikacji (Android wycisza mikrofon w tle)
            val mic = if (Build.VERSION.SDK_INT >= 30 && checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) == android.content.pm.PackageManager.PERMISSION_GRANTED) android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE else 0
            startForeground(41921, n, android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION or mic)
            val data = if (Build.VERSION.SDK_INT >= 33) i?.getParcelableExtra("data", Intent::class.java) else @Suppress("DEPRECATION") i?.getParcelableExtra("data")
            val p = getSystemService(android.media.projection.MediaProjectionManager::class.java).getMediaProjection(i!!.getIntExtra("code", 0), data!!)!!
            mp = p
            val t = android.os.HandlerThread("velivo-ekran").also { it.start() }; th = t; val h = Handler(t.looper)
            p.registerCallback(object : android.media.projection.MediaProjection.Callback() { override fun onStop() { stopSelf() } }, h)
            val r = newReader(h)
            vd = p.createVirtualDisplay("velivo-ekran", r.width, r.height, resources.displayMetrics.densityDpi, android.hardware.display.DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, r.surface, null, h)
            hd = h
        }.onFailure { stopSelf() }
        return START_NOT_STICKY
    }
    private var hd: Handler? = null
    private var last = 0L
    @Volatile private var busy = false
    // obraz w rozmiarze ekranu teraz (pion albo poziom), szerokość/wysokość do 720
    private fun newReader(h: Handler): android.media.ImageReader {
        val sz = size(this); val sc = minOf(1f, 720f / minOf(sz.x, sz.y))
        val w = (sz.x * sc).toInt() and 1.inv(); val hh = (sz.y * sc).toInt() and 1.inv()
        val r = android.media.ImageReader.newInstance(w, hh, android.graphics.PixelFormat.RGBA_8888, 2); reader = r
        // klatka najwyżej co 330 ms; po przerwie bierzemy najnowszą (nieruchomy ekran też dojdzie)
        val grab = Runnable {
            busy = false
            runCatching {
                val img = r.acquireLatestImage() ?: return@runCatching
                img.use {
                    val cb = onFrame; if (r !== reader || cb == null) return@use
                    last = System.currentTimeMillis()
                    val pl0 = it.planes[0]; val rw = pl0.rowStride / pl0.pixelStride
                    val src = pl0.buffer; val need = pl0.rowStride * hh
                    val buf = if (src.remaining() >= need) src else java.nio.ByteBuffer.allocate(need).apply { put(src); rewind() }   // ostatni wiersz bywa krótszy
                    val full = android.graphics.Bitmap.createBitmap(rw, hh, android.graphics.Bitmap.Config.ARGB_8888); full.copyPixelsFromBuffer(buf)
                    val bmp = if (rw == w) full else android.graphics.Bitmap.createBitmap(full, 0, 0, w, hh)
                    val o = java.io.ByteArrayOutputStream(); bmp.compress(android.graphics.Bitmap.CompressFormat.JPEG, 55, o)
                    if (bmp !== full) bmp.recycle(); full.recycle()
                    cb(Base64.encodeToString(o.toByteArray(), Base64.NO_WRAP))
                }
            }
        }
        r.setOnImageAvailableListener({ _ ->
            if (!busy) { busy = true; h.postDelayed(grab, maxOf(0L, 330 - (System.currentTimeMillis() - last))) }
        }, h)
        return r
    }
    // 🔄 autoobracanie: telefon obrócony - obraz dalej w dobrych proporcjach
    override fun onConfigurationChanged(c: android.content.res.Configuration) {
        super.onConfigurationChanged(c)
        val h = hd ?: return
        h.post { runCatching {
            val v = vd ?: return@runCatching; val old = reader; val r = newReader(h)
            if (old != null && old.width == r.width && old.height == r.height) { reader = old; r.close(); return@runCatching }
            v.resize(r.width, r.height, resources.displayMetrics.densityDpi); v.surface = r.surface; old?.close()
        } }
    }
    override fun onDestroy() {
        runCatching { vd?.release() }; runCatching { mp?.stop() }; runCatching { reader?.close() }; th?.quitSafely()
        onStop?.invoke()
        super.onDestroy()
    }
    companion object {
        @Volatile var onFrame: ((String) -> Unit)? = null
        @Volatile var onStop: (() -> Unit)? = null
        private var hint: View? = null
        // ❓ pytanie o zgodę widoczne także nad inną aplikacją (osoba pokazuje np. Ustawienia); bez zgody „nad aplikacjami” - zwykłe okno Velivo
        fun ask(act: Activity, text: String, yes: String, no: String, cb: (Boolean) -> Unit) = Handler(Looper.getMainLooper()).post {
            val ctx = act.applicationContext
            if (!android.provider.Settings.canDrawOverlays(ctx)) {
                AlertDialog.Builder(act).setMessage(text).setPositiveButton(yes) { _, _ -> cb(true) }.setNegativeButton(no) { _, _ -> cb(false) }.setOnCancelListener { cb(false) }.show(); return@post
            }
            val wm = ctx.getSystemService(android.view.WindowManager::class.java) ?: return@post
            val m = ctx.resources.displayMetrics; fun px(v: Int) = (v * m.density).toInt()
            lateinit var box: LinearLayout
            fun btn(t: String, c: Int, ok: Boolean) = Button(ctx).apply { this.text = t; isAllCaps = false; setTextColor(Color.WHITE); textSize = 16f
                background = GradientDrawable().apply { setColor(c); cornerRadius = px(22).toFloat() }
                layoutParams = LinearLayout.LayoutParams(0, px(48), 1f).apply { leftMargin = px(6); rightMargin = px(6) }
                setOnClickListener { runCatching { wm.removeView(box) }; cb(ok) } }
            box = LinearLayout(ctx).apply { orientation = LinearLayout.VERTICAL; setPadding(px(18), px(16), px(18), px(14))
                background = GradientDrawable().apply { setColor(Color.rgb(31, 41, 55)); cornerRadius = px(18).toFloat() }
                addView(TextView(ctx).apply { this.text = text; textSize = 17f; setTextColor(Color.WHITE); setPadding(0, 0, 0, px(14)) })
                addView(LinearLayout(ctx).apply { addView(btn(no, Color.rgb(107, 114, 128), false)); addView(btn(yes, Color.rgb(34, 197, 94), true)) }) }
            val lp = android.view.WindowManager.LayoutParams((m.widthPixels * 0.9).toInt(), -2, android.view.WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                android.view.WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE, android.graphics.PixelFormat.TRANSLUCENT).apply { gravity = Gravity.CENTER }
            if (runCatching { wm.addView(box, lp) }.isFailure) cb(false)
        }
        // cały ekran (z paskami systemu) - tak jak widzi go nagrywanie
        fun size(ctx: Context): android.graphics.Point = if (Build.VERSION.SDK_INT >= 30) ctx.getSystemService(android.view.WindowManager::class.java).maximumWindowMetrics.bounds.let { android.graphics.Point(it.width(), it.height()) }
            else ctx.resources.displayMetrics.let { android.graphics.Point(it.widthPixels, it.heightPixels) }
        // 👆 wskazówka rozmówcy: czerwone kółko nad wszystkimi aplikacjami na 3 s (gdy jest zgoda „nad innymi aplikacjami”)
        fun point(ctx: Context, fx: Double, fy: Double) = Handler(Looper.getMainLooper()).post {
            if (!android.provider.Settings.canDrawOverlays(ctx)) return@post
            val wm = ctx.getSystemService(android.view.WindowManager::class.java) ?: return@post
            hint?.let { runCatching { wm.removeView(it) } }
            val m = ctx.resources.displayMetrics; val d = (48 * m.density).toInt()
            val v = TextView(ctx).apply { text = "👆"; textSize = 22f; gravity = Gravity.CENTER
                background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(Color.argb(110, 229, 57, 53)); setStroke((3 * m.density).toInt(), Color.rgb(229, 57, 53)) } }
            val lp = android.view.WindowManager.LayoutParams(d, d, android.view.WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                android.view.WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or android.view.WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or android.view.WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or android.view.WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
                android.graphics.PixelFormat.TRANSLUCENT).apply { gravity = Gravity.TOP or Gravity.START
                    val sz = size(ctx); x = (fx.coerceIn(0.0, 1.0) * sz.x).toInt() - d / 2; y = (fy.coerceIn(0.0, 1.0) * sz.y).toInt() - d / 2
                    if (Build.VERSION.SDK_INT >= 28) layoutInDisplayCutoutMode = android.view.WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES }
            if (runCatching { wm.addView(v, lp) }.isFailure) return@post
            hint = v
            v.postDelayed({ if (hint === v) { runCatching { wm.removeView(v) }; hint = null } }, 3000)
        }
    }
}

// 🖱 sterowanie telefonem w pomocy zdalnej: stuknięcia i przesunięcia rozmówcy (dispatchGesture); nie czyta ekranu.
// Włączana raz w Ułatwieniach dostępu; wykonuje ruchy tylko, gdy osoba zgodziła się w rozmowie (allowed), do końca rozmowy.
class SterowanieUsluga : android.accessibilityservice.AccessibilityService() {
    override fun onServiceConnected() { on = this }
    override fun onUnbind(i: Intent?): Boolean { on = null; return super.onUnbind(i) }
    override fun onDestroy() { on = null; super.onDestroy() }
    override fun onAccessibilityEvent(e: android.view.accessibility.AccessibilityEvent?) {}
    override fun onInterrupt() {}
    companion object {
        @Volatile var on: SterowanieUsluga? = null
        @Volatile var allowed = false
        private var downX = 0f; private var downY = 0f; private var downAt = 0L
        private val path = ArrayList<android.graphics.PointF>()
        // x, y - ułamek ekranu; d - palec w dół, m - ruch, u - w górę (wtedy stuknięcie, przytrzymanie albo przesunięcie); k - Esc = Wstecz, Home = ekran główny
        fun input(o: JSONObject) {
            val s = on ?: return; if (!allowed) return
            when (o.optString("t")) {
                "k" -> if (!o.optBoolean("up")) when (o.optInt("v")) { 27, 8 -> s.performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_BACK); 36 -> s.performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_HOME); 9 -> s.performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_RECENTS) }
                "d", "m", "u" -> {
                    val sz = EkranUsluga.size(s); val x = (o.optDouble("x").coerceIn(0.0, 1.0) * (sz.x - 1)).toFloat(); val y = (o.optDouble("y").coerceIn(0.0, 1.0) * (sz.y - 1)).toFloat()
                    when (o.optString("t")) {
                        "d" -> { downX = x; downY = y; downAt = System.currentTimeMillis(); path.clear(); path.add(android.graphics.PointF(x, y)) }
                        "m" -> if (downAt > 0 && path.size < 200) path.add(android.graphics.PointF(x, y))
                        "u" -> {
                            if (downAt == 0L) return
                            val ms = (System.currentTimeMillis() - downAt).coerceIn(40L, 3000L); downAt = 0
                            val p = android.graphics.Path().apply { moveTo(downX, downY) }
                            val far = Math.hypot((x - downX).toDouble(), (y - downY).toDouble()) > 24 * s.resources.displayMetrics.density
                            if (far) { path.drop(1).forEach { p.lineTo(it.x, it.y) }; p.lineTo(x, y) }
                            val st = android.accessibilityservice.GestureDescription.StrokeDescription(p, 0, if (far) ms.coerceAtMost(1500L) else if (ms > 500) 800L else 60L)
                            s.dispatchGesture(android.accessibilityservice.GestureDescription.Builder().addStroke(st).build(), null, null)
                        }
                    }
                }
            }
        }
    }
}

// 📷 zdjęcie z aparatu: piksele „na boku” + znacznik obrotu (EXIF) - BitmapFactory go nie czyta, obracamy sami
internal fun upright(b: android.graphics.Bitmap, e: android.media.ExifInterface?): android.graphics.Bitmap {
    val deg = when (e?.getAttributeInt(android.media.ExifInterface.TAG_ORIENTATION, 1)) { 3 -> 180f; 6 -> 90f; 8 -> 270f; else -> 0f }
    if (deg == 0f) return b
    return android.graphics.Bitmap.createBitmap(b, 0, 0, b.width, b.height, android.graphics.Matrix().apply { postRotate(deg) }, true)
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
        fun otherOwner(ctx: Context) = Komunikator.owner(ctx) != ctx.packageName
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
        if (i.action == REJECT) { k.contact(i.getStringExtra("id") ?: return)?.let { c -> k.sendCall(c, JSONObject().put("k", "reject")) }; ctx.getSystemService(NotificationManager::class.java)?.cancel("rozmowa".hashCode()); return }
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
        const val REJECT = "pl.velivo.komunikator.ODRZUC"
        const val TEXT = "tekst"
    }
}
