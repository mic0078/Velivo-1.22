package pl.velivo.browser

import android.os.Build
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
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
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import java.util.UUID
import java.util.zip.GZIPInputStream
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec

// Synchronizacja z Velivo na Windows w sieci domowej - ten sam protokół co LanSync.cs / LanPairing.cs:
// UDP 41919, pakiety JSON, AES-GCM kluczem z parowania, duży stan przez TCP na ten sam port.
// Telefon na razie tylko ODBIERA zakładki i hasła (nie wysyła stanu - Windows przyjmuje cały profil z ustawieniami).
class LanSync(
    private val lanKey: () -> String?,
    private val saveKey: (String) -> Unit,
    private val profile: () -> String,
    private val deviceName: () -> String,
    private val saveProfile: (String) -> Unit,
    private val ui: (Runnable) -> Unit,
    private val confirmPair: (device: String, code: String, accept: () -> Unit) -> Unit,
    private val onPaired: (device: String) -> Unit,
    private val onState: (device: String, bookmarks: String?, passwords: String?) -> Unit,
    private val onQuickAccess: (device: String, zip: ByteArray) -> Unit = { _, _ -> },
) {
    // Nowe id przy każdym uruchomieniu: Windows traktuje telefon jak nowe urządzenie i od razu wysyła mu cały stan.
    private val id = UUID.randomUUID().toString().replace("-", "")
    @Volatile private var encKey: ByteArray? = null
    @Volatile private var authKey: ByteArray? = null
    @Volatile private var running = false
    @Volatile var pairingUntil = 0L
    // Diagnostyka dla okna parowania: ile pakietów Velivo przyszło z sieci i od kogo.
    @Volatile var rxCount = 0
    @Volatile var lastFrom = ""
    @Volatile var lastType = ""
    @Volatile var socketError: String? = null
    @Volatile var pairStep = ""   // ostatni krok parowania (do okna oczekiwania)
    private var udp: DatagramSocket? = null
    private var tcp: ServerSocket? = null
    private val pending = HashMap<String, Pair<KeyPair, ByteArray>>()   // pairId -> (moje klucze ECDH, klucz transportu)
    // Windows wysyła prośbę na kilka adresów rozgłoszeniowych - ta sama prośba dochodzi kilka razy.
    // Każda kopia dawałaby nowe okno z innym kodem i innym kluczem (potem "bad decrypt") - pytamy tylko raz.
    private val seenOffers = HashSet<String>()
    private val random = SecureRandom()

    val paired get() = lanKey() != null

    fun start() {
        if (running) return
        running = true
        Thread {
            runCatching { deriveKeys() }
            runCatching {
                socketError = null
                udp = DatagramSocket(null).apply { reuseAddress = true; broadcast = true; bind(InetSocketAddress(PORT)) }
                Thread { tcpLoop() }.start()
                Thread { helloLoop() }.start()
                val buf = ByteArray(65536)
                while (running) {
                    val p = DatagramPacket(buf, buf.size)
                    udp?.receive(p) ?: break
                    val data = p.data.copyOf(p.length)
                    val from = p.address
                    runCatching { handle(data, from) }.onFailure { if (pairStep.isNotEmpty()) pairStep = "błąd: " + (it.message ?: it.javaClass.simpleName) }
                }
            }.onFailure { if (running) socketError = it.message ?: it.javaClass.simpleName }
        }.start()
    }

    fun stop() {
        running = false
        runCatching { udp?.close() }
        runCatching { tcp?.close() }
        udp = null
        tcp = null
    }

    fun syncNow() = Thread { runCatching { hello() } }.start()

    private var derivedFor: String? = null

    // PBKDF2 250 000 razy (jak na Windows) - liczone tylko, gdy klucz się zmienił, nie przy każdym powrocie do aplikacji.
    private fun deriveKeys() {
        val key = lanKey()?.trim() ?: run { encKey = null; authKey = null; derivedFor = null; return }
        if (key == derivedFor && encKey != null) return
        val spec = PBEKeySpec(key.toCharArray(), "Velivo LAN sync v1".toByteArray(), 250000, 512)
        val m = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).encoded
        encKey = m.copyOfRange(0, 32)
        authKey = m.copyOfRange(32, 64)
        derivedFor = key
    }

    private fun helloLoop() {
        while (running) {
            runCatching { hello() }
            Thread.sleep(12000)
        }
    }

    private fun hello() {
        val a = authKey ?: return
        val p = packet("hello")
        p.put("key", b64(hmac(a, ad(p))))
        send(p)
    }

    private fun packet(t: String) = JSONObject()
        .put("t", t).put("id", id).put("device", deviceName()).put("profile", profile())
        .put("ts", System.currentTimeMillis())

    // Dane powiązane z pakietem (jak LanAssociatedData): t, id, device, profile, ts, hash - po jednym w linii.
    private fun ad(p: JSONObject) = listOf(
        p.optString("t"), p.optString("id"), p.optString("device"), p.optString("profile"),
        p.optLong("ts").toString(), if (p.isNull("hash")) "" else p.optString("hash")
    ).joinToString("\n").toByteArray(Charsets.UTF_8)

    private fun send(p: JSONObject) {
        val data = p.toString().toByteArray(Charsets.UTF_8)
        val s = udp ?: return
        for (target in broadcastTargets())
            runCatching { s.send(DatagramPacket(data, data.size, target, PORT)) }
    }

    private fun sendTo(p: JSONObject, target: InetAddress) {
        val data = p.toString().toByteArray(Charsets.UTF_8)
        udp?.send(DatagramPacket(data, data.size, target, PORT))
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

    private fun handle(data: ByteArray, sender: InetAddress? = null) {
        val p = JSONObject(String(data, Charsets.UTF_8))
        val from = p.optString("id")
        if (from == id || from.length != 32) return
        val now = System.currentTimeMillis()
        rxCount++
        lastFrom = p.optString("device").filter { !it.isISOControl() }.take(64)
        lastType = p.optString("t")
        if (Math.abs(p.optLong("ts") - now) > 300000) return
        when (p.optString("t")) {
            "pair-offer" -> pairOffer(p)
            "pair-key" -> pairKey(p, sender)
            "state" -> state(p)
        }
    }

    // --- Parowanie: zaczyna Windows (Ustawienia → Synchronizacja LAN → Sparuj), telefon odpowiada --------
    private fun pairOffer(p: JSONObject) {
        if (System.currentTimeMillis() > pairingUntil) return
        val pairId = p.optString("pairId")
        val pub = p.optString("pub")
        if (pairId.length != 32 || pub.isEmpty() || pub.length > 2048) return
        if (!synchronized(seenOffers) { seenOffers.add(pairId) }) return
        val kpg = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }
        val mine = kpg.generateKeyPair()
        val remote = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(unb64(pub)))
        val ka = KeyAgreement.getInstance("ECDH").apply { init(mine.private); doPhase(remote, true) }
        // .NET DeriveKeyMaterial = SHA-256 ze wspólnego sekretu
        val agreement = MessageDigest.getInstance("SHA-256").digest(ka.generateSecret())
        val transport = hmac(agreement, "Velivo LAN pairing v1\n$pairId".toByteArray())
        val digest = hmac(transport, "Velivo LAN pairing code v1\n$pairId".toByteArray())
        val n = ((digest[0].toLong() and 0xff) or ((digest[1].toLong() and 0xff) shl 8) or
            ((digest[2].toLong() and 0xff) shl 16) or ((digest[3].toLong() and 0xff) shl 24)) % 1000000
        val code = n.toString().padStart(6, '0')
        val dev = p.optString("device").filter { !it.isISOControl() }.take(64).ifBlank { "?" }
        val winProfile = p.optString("profile")
        ui(Runnable {
            confirmPair(dev, code) {
                Thread {
                    pairStep = "kod potwierdzony, czekam na klucz z komputera"
                    synchronized(pending) { if (pending.containsKey(pairId)) return@Thread; pending[pairId] = mine to transport }
                    saveProfile(winProfile)
                    send(packet("pair-response").put("pairId", pairId).put("pub", b64(mine.public.encoded)))
                }.start()
            }
        })
    }

    private fun pairKey(p: JSONObject, sender: InetAddress?) {
        val pairId = p.optString("pairId")
        val (_, transport) = synchronized(pending) { pending.remove(pairId) } ?: return
        pairStep = "odebrano klucz"
        val nonce = unb64(p.optString("nonce"))
        val cipher = unb64(p.optString("data"))
        val tag = unb64(p.optString("tag"))
        if (nonce.size != 12 || tag.size != 16 || cipher.size != 32) return
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, SecretKeySpec(transport, "AES"), GCMParameterSpec(128, nonce))
        c.updateAAD(pairId.toByteArray())
        val key = c.doFinal(cipher + tag)
        saveKey(b64(key))
        val proof = b64(hmac(transport, "ack\n$pairId\n$id\n${p.optString("id")}".toByteArray()))
        val ack = packet("pair-ack").put("pairId", pairId).put("proof", proof)
        // Potwierdzenie prosto do komputera, który przysłał klucz (pewniejsze niż rozgłoszenie w Wi-Fi);
        // rozgłoszenie tylko, gdy adres nieznany. Jeden pakiet - Windows przy każdym potwierdzeniu pyta o ustawienia.
        if (sender != null) sendTo(ack, sender) else send(ack)
        pairStep = "wysłano potwierdzenie do komputera"
        pairingUntil = 0
        deriveKeys()
        hello()
        val dev = p.optString("device").filter { !it.isISOControl() }.take(64)
        ui(Runnable { onPaired(dev) })
    }

    // --- Stan z Windows: zakładki i hasła ------------------------------------
    private fun state(p: JSONObject) {
        val k = encKey ?: return
        val winProfile = p.optString("profile")
        if (winProfile != profile()) saveProfile(winProfile)
        val nonce = unb64(p.optString("nonce"))
        val tag = unb64(p.optString("tag"))
        val cipher = unb64(p.optString("data"))
        if (nonce.size != 12 || tag.size != 16 || cipher.isEmpty() || cipher.size > TCP_LIMIT) return
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, SecretKeySpec(k, "AES"), GCMParameterSpec(128, nonce))
        c.updateAAD(ad(p))
        val plain = runCatching { c.doFinal(cipher + tag) }.getOrNull() ?: return   // zły klucz / podrobiony pakiet
        val json = JSONObject(String(gunzip(plain), Charsets.UTF_8))
        val dev = p.optString("device").filter { !it.isISOControl() }.take(64)
        ui(Runnable { onState(dev, json.optString("bookmarks", null), json.optString("passwords", null)) })
    }

    private fun gunzip(d: ByteArray): ByteArray {
        if (d.size < 2 || d[0] != 0x1f.toByte() || d[1] != 0x8b.toByte()) return d
        val out = ByteArrayOutputStream()
        GZIPInputStream(ByteArrayInputStream(d)).use { z ->
            val buf = ByteArray(8192)
            while (true) {
                val n = z.read(buf)
                if (n <= 0) break
                out.write(buf, 0, n)
                if (out.size() > TCP_LIMIT) throw IllegalStateException("za duży stan")
            }
        }
        return out.toByteArray()
    }

    // Duży stan (np. z trybem bankowym) Windows wysyła TCP: 4 bajty długości nagłówka, nagłówek JSON, pakiet.
    private fun tcpLoop() {
        runCatching {
            tcp = ServerSocket().apply { reuseAddress = true; bind(InetSocketAddress(PORT)) }
            while (running) {
                val s = tcp?.accept() ?: break
                Thread {
                    runCatching {
                        s.use {
                            it.soTimeout = 15000
                            val inp = DataInputStream(it.getInputStream())
                            val lb = ByteArray(4).also { b -> inp.readFully(b) }
                            val hl = (lb[0].toInt() and 0xff) or ((lb[1].toInt() and 0xff) shl 8) or
                                ((lb[2].toInt() and 0xff) shl 16) or ((lb[3].toInt() and 0xff) shl 24)
                            if (hl <= 0 || hl > 4096) return@use
                            val header = JSONObject(String(ByteArray(hl).also { b -> inp.readFully(b) }, Charsets.UTF_8))
                            if (header.optString("Type") == "quick-access") { quickAccess(header, inp, it.getOutputStream()); return@use }
                            val len = header.optInt("Length")
                            if (header.optString("Type") != "lan-state" || len <= 0 || len > TCP_LIMIT) return@use
                            val data = ByteArray(len).also { b -> inp.readFully(b) }
                            handle(data, it.inetAddress)
                        }
                    }
                }.start()
            }
        }
    }

    // Szybki Dostęp z Windows (QuickAccessLanSync.cs): ZIP folderu "Szybki Dostęp", AES-GCM, nagłówek podpisany HMAC.
    // Po odebraniu odsyłamy potwierdzenie - inaczej komputer co minutę zapisuje w logu błąd wysyłki.
    private fun quickAccess(h: JSONObject, inp: DataInputStream, out: java.io.OutputStream) {
        val k = encKey ?: return
        val a = authKey ?: return
        val len = h.optInt("Length")
        if (len <= 0 || len > QA_LIMIT || h.optString("Id").length != 32 || h.optString("Id") == id) return
        if (Math.abs(h.optLong("Timestamp") - System.currentTimeMillis()) > 300000) return
        val ad = listOf(h.optString("Type"), h.optString("Id"), h.optString("Device"), h.optString("Profile"),
            h.optLong("Timestamp").toString(), h.optString("Hash"), len.toString()).joinToString("\n").toByteArray(Charsets.UTF_8)
        if (!MessageDigest.isEqual(unb64(h.optString("Mac")), hmac(a, ad))) return   // nie od sparowanego komputera
        val nonce = unb64(h.optString("Nonce"))
        val tag = unb64(h.optString("Tag"))
        if (nonce.size != 12 || tag.size != 16) return
        val cipher = ByteArray(len).also { inp.readFully(it) }
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, SecretKeySpec(k, "AES"), GCMParameterSpec(128, nonce))
        c.updateAAD(ad)
        val zip = c.doFinal(cipher + tag)
        val hash = MessageDigest.getInstance("SHA-256").digest(zip).joinToString("") { "%02X".format(it) }
        if (hash != h.optString("Hash")) return
        val dev = h.optString("Device").filter { !it.isISOControl() }.take(64)
        onQuickAccess(dev, zip)
        out.write(hmac(a, "ack\n$hash".toByteArray(Charsets.UTF_8)))
        out.flush()
    }

    private fun hmac(key: ByteArray, data: ByteArray) =
        Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }.doFinal(data)

    companion object {
        const val PORT = 41919
        val MODEL = (Build.MANUFACTURER + " " + Build.MODEL).trim()
        const val TCP_LIMIT = 8 * 1024 * 1024
        const val QA_LIMIT = 24 * 1024 * 1024

        fun b64(b: ByteArray): String = Base64.encodeToString(b, Base64.NO_WRAP)
        fun unb64(s: String): ByteArray = runCatching { Base64.decode(s, Base64.DEFAULT) }.getOrDefault(ByteArray(0))

        // Plik odzyskiwania parowania z Velivo na Windows (.vlpair): klucz LAN zaszyfrowany hasłem (PBKDF2 + AES-GCM).
        // Wczytanie nie zmienia klucza - inne komputery zostają sparowane.
        fun readRecovery(text: String, pass: String): String? = runCatching {
            val o = JSONObject(text)
            val it = o.getInt("iterations")
            if (o.getInt("v") != 1 || it < 300000 || it > 1000000) return null
            val salt = unb64(o.getString("salt"))
            val nonce = unb64(o.getString("nonce"))
            val tag = unb64(o.getString("tag"))
            val data = unb64(o.getString("data"))
            if (salt.size != 16 || nonce.size != 12 || tag.size != 16 || data.isEmpty()) return null
            val key = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(PBEKeySpec(pass.toCharArray(), salt, it, 256)).encoded
            val c = Cipher.getInstance("AES/GCM/NoPadding")
            c.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
            val payload = JSONObject(String(c.doFinal(data + tag), Charsets.UTF_8))
            val k = payload.optString("Key")
            if (payload.optInt("Version") != 1 || k.trim().toByteArray().size < 24) null else k
        }.getOrNull()

        // Hasła z Windows: [{Host, Url, Username, Password, ...}]
        fun parsePasswords(json: String): List<Triple<String, String, String>> = runCatching {
            val arr = JSONArray(json)
            (0 until arr.length()).mapNotNull { i ->
                val o = arr.getJSONObject(i)
                var host = o.optString("Host").trim().lowercase()
                if (host.isEmpty()) host = android.net.Uri.parse(o.optString("Url")).host ?: ""
                val pass = o.optString("Password")
                if (host.isEmpty() || pass.isEmpty()) null else Triple(host, o.optString("Username"), pass)
            }
        }.getOrDefault(emptyList())
    }
}
