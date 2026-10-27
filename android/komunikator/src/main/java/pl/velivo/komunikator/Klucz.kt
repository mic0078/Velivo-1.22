package pl.velivo.komunikator

import android.app.Activity
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.hardware.usb.UsbConstants
import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbManager
import android.nfc.NfcAdapter
import android.nfc.tech.IsoDep
import org.json.JSONObject
import java.security.AlgorithmParameters
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec

// 🔑 klucz sprzętowy (YubiKey i inne klucze FIDO) bez usług Google: standard U2F przez NFC (przyłożenie) albo USB (włożenie i dotknięcie).
// W telefonie zostaje tylko klucz publiczny i uchwyt klucza; przy otwarciu klucz podpisuje losowe wyzwanie, telefon sprawdza podpis.
object Klucz {
    private val app = MessageDigest.getInstance("SHA-256").digest("velivo-komunikator".toByteArray())
    private class Brak : Exception()   // ten klucz nie zna uchwytu (inny klucz) - 6A80
    private fun t(p: String, e: String) = if (java.util.Locale.getDefault().language == "pl") p else e

    // zapisane klucze (wspólne dla Velivo i komunikatora w tej aplikacji): klucz publiczny i uchwyt - bez tajemnic
    fun keys(p: android.content.SharedPreferences): List<JSONObject> = runCatching { org.json.JSONArray(p.getString("klucze", "[]")).let { a -> (0 until a.length()).map { a.getJSONObject(it) } } }.getOrDefault(emptyList())
    private fun save(p: android.content.SharedPreferences, l: List<JSONObject>) = p.edit().putString("klucze", org.json.JSONArray(l).toString()).apply()

    // najpierw hasło / PIN telefonu - nikt przy odblokowanym telefonie nie doda swojego klucza ani nie usunie Twojego
    fun confirm(a: Activity, then: () -> Unit) {
        if (a.getSystemService(android.app.KeyguardManager::class.java)?.isDeviceSecure != true) { then(); return }
        val b = android.hardware.biometrics.BiometricPrompt.Builder(a).setTitle("🔑 " + t("Podaj hasło / PIN telefonu", "Enter the phone password / PIN"))
        if (android.os.Build.VERSION.SDK_INT >= 30) b.setAllowedAuthenticators(android.hardware.biometrics.BiometricManager.Authenticators.DEVICE_CREDENTIAL)
        else @Suppress("DEPRECATION") b.setDeviceCredentialAllowed(true)
        runCatching { b.build().authenticate(android.os.CancellationSignal(), a.mainExecutor, object : android.hardware.biometrics.BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(r: android.hardware.biometrics.BiometricPrompt.AuthenticationResult?) = then()
        }) }
    }

    // ustawienia klucza (opcja, nie przymus): dodaj pierwszy / zapasowy, usuń wszystkie - zawsze po haśle telefonu
    fun setup(a: Activity, p: android.content.SharedPreferences, added: () -> Unit = {}) {
        val toast = { m: String -> android.widget.Toast.makeText(a, m, android.widget.Toast.LENGTH_LONG).show() }
        // nazwa klucza (np. YubiKey, Titan) - przy wymianie widać, który usunąć
        fun add() = confirm(a) { register(a) { j ->
            if (j == null) { toast(t("Nie udało się odczytać klucza", "Could not read the key")); return@register }
            val first = keys(p).isEmpty()
            val name = android.widget.EditText(a).apply { setText(t("Klucz ", "Key ") + (keys(p).size + 1)); setSelectAllOnFocus(true); setTextColor(android.graphics.Color.WHITE) }
            android.app.AlertDialog.Builder(a, android.R.style.Theme_Material_Dialog_Alert).setTitle("🔑 " + t("Nazwa klucza (np. YubiKey, Titan)", "Key name (e.g. YubiKey, Titan)")).setView(name)
                .setPositiveButton(android.R.string.ok) { _, _ -> }.setOnDismissListener {
                    save(p, keys(p) + j.put("n", name.text.toString().trim().take(30).ifEmpty { t("Klucz", "Key") } + " · " + java.text.SimpleDateFormat("yyyy-MM-dd", java.util.Locale.ROOT).format(java.util.Date()))); added()
                    toast(if (first) t("Klucz dodany. Dodaj też zapasowy klucz – gdy jeden się zepsuje albo zginie, drugi działa.", "Key added. Add a backup key too – if one breaks or gets lost, the other works.") else t("Zapasowy klucz dodany", "Backup key added")) }.show() } }
        // wymiana: usunięcie jednego klucza (zepsuty / zgubiony) po haśle telefonu - pozostałe działają dalej
        fun remove() { val l = keys(p)
            android.app.AlertDialog.Builder(a, android.R.style.Theme_Material_Dialog_Alert).setTitle("🗑 " + t("Który klucz usunąć?", "Which key to remove?"))
                .setItems(l.map { it.optString("n", "?") }.toTypedArray()) { _, i -> confirm(a) { save(p, keys(p).filter { it.optString("kh") != l[i].optString("kh") }); toast(t("Klucz usunięty: ", "Key removed: ") + l[i].optString("n")) } }
                .setNegativeButton(android.R.string.cancel, null).show() }
        val l = keys(p)
        android.app.AlertDialog.Builder(a, android.R.style.Theme_Material_Dialog_Alert).setTitle("🔑 " + t("Klucz sprzętowy (YubiKey, Titan)", "Security key (YubiKey, Titan)"))
            .setMessage(t("Opcjonalnie: Velivo i komunikator odblokujesz dotknięciem klucza (USB albo NFC). Odcisk palca / PIN telefonu dalej działa jako zapas.",
                "Optional: unlock Velivo and the messenger by touching the key (USB or NFC). Phone fingerprint / PIN still works as a backup.") +
                if (l.isEmpty()) "" else "\n\n" + l.joinToString("\n") { "✓ " + it.optString("n", "?") } + if (l.size == 1) "\n\n⚠ " + t("Dodaj zapasowy klucz.", "Add a backup key.") else "")
            .setPositiveButton(if (l.isEmpty()) "➕ " + t("Dodaj klucz", "Add key") else "➕ " + t("Dodaj zapasowy", "Add backup")) { _, _ -> add() }
            .apply { if (l.isNotEmpty()) setNeutralButton("🗑 " + t("Usuń klucz…", "Remove key…")) { _, _ -> remove() } }
            .apply { if (l.isNotEmpty()) setNegativeButton("🧪 " + t("Sprawdź", "Check")) { _, _ -> test(a, p) } else setNegativeButton(android.R.string.cancel, null) }.show()   // zamknięcie: ✕ / wstecz
    }

    // odblokowanie zapisanymi kluczami; po udanym - raz na 3 miesiące przypomnienie „sprawdź klucze”
    fun unlock(a: Activity, p: android.content.SharedPreferences, done: (Boolean) -> Unit) = verify(a, keys(p)) { ok -> done(ok); if (ok) due(a, p) }
    private const val TEST_MS = 90L * 86400000
    fun due(a: Activity, p: android.content.SharedPreferences) {
        val now = System.currentTimeMillis(); val last = p.getLong("klucze-test", 0L)
        if (keys(p).isEmpty() || now - last < TEST_MS) return
        if (last == 0L) { p.edit().putLong("klucze-test", now).apply(); return }   // pierwszy raz - liczenie od dziś
        android.app.AlertDialog.Builder(a, android.R.style.Theme_Material_Dialog_Alert).setTitle("🧪 " + t("Sprawdź klucze", "Check your keys"))
            .setMessage(t("Minęły 3 miesiące. Dotknij po kolei każdego klucza (także zapasowego) – awarię wykryjesz teraz, nie w dniu, gdy klucz będzie potrzebny.",
                "3 months have passed. Touch each key in turn (the backup too) – you find a fault now, not on the day you need the key."))
            .setPositiveButton(t("Sprawdź", "Check")) { _, _ -> test(a, p) }
            .setNegativeButton(t("Za tydzień", "In a week")) { _, _ -> p.edit().putLong("klucze-test", now - TEST_MS + 7L * 86400000).apply() }.show()
    }
    // test każdego klucza osobno (po nazwie); wynik ✓ / ✗ i data ostatniego udanego testu przy kluczu
    fun test(a: Activity, p: android.content.SharedPreferences) {
        val l = keys(p); val res = ArrayList<String>()
        fun next(i: Int) {
            if (i >= l.size) {
                p.edit().putLong("klucze-test", System.currentTimeMillis()).apply()
                android.app.AlertDialog.Builder(a, android.R.style.Theme_Material_Dialog_Alert).setTitle("🧪 " + t("Wynik", "Result")).setMessage(res.joinToString("\n") +
                    if (res.any { it.startsWith("✗") }) "\n\n" + t("Nie działający klucz usuń (🗑) i dodaj nowy zapasowy.", "Remove the failing key (🗑) and add a new backup.") else "")
                    .setPositiveButton(android.R.string.ok, null).show(); return }
            android.widget.Toast.makeText(a, t("Teraz: ", "Now: ") + l[i].optString("n", "?"), android.widget.Toast.LENGTH_LONG).show()
            verify(a, listOf(l[i])) { ok ->
                res += (if (ok) "✓ " else "✗ ") + l[i].optString("n", "?")
                if (ok) save(p, keys(p).map { if (it.optString("kh") == l[i].optString("kh")) it.put("t", System.currentTimeMillis()) else it })
                next(i + 1) }
        }
        next(0)
    }

    // rejestracja: {kh, pub} albo null
    fun register(a: Activity, done: (JSONObject?) -> Unit) = run(a, { tx ->
        val ch = ByteArray(32).also { SecureRandom().nextBytes(it) }
        val r = apdu(tx, 0x01, 0x00, ch + app)
        if (r.size < 67 || r[0] != 5.toByte()) null else {
            val khLen = r[66].toInt() and 0xff
            JSONObject().put("pub", b64(r.copyOfRange(1, 66))).put("kh", b64(r.copyOfRange(67, 67 + khLen)))
        }
    }, done)

    // sprawdzenie: true, gdy któryś z zapisanych kluczy podpisał wyzwanie
    fun verify(a: Activity, keys: List<JSONObject>, done: (Boolean) -> Unit) = run(a, { tx ->
        var ok = false
        for (k in keys) {
            val kh = unb64(k.optString("kh")); val ch = ByteArray(32).also { SecureRandom().nextBytes(it) }
            val r = try { apdu(tx, 0x02, 0x03, ch + app + byteArrayOf(kh.size.toByte()) + kh) } catch (e: Brak) { continue }
            if (r.size < 6 || (r[0].toInt() and 1) == 0) continue
            val s = Signature.getInstance("SHA256withECDSA"); s.initVerify(pub(unb64(k.optString("pub"))))
            s.update(app + r.copyOfRange(0, 5) + ch)
            if (s.verify(r.copyOfRange(5, r.size))) { ok = true; break }
        }
        ok
    }, { done(it == true) })

    // U2F: APDU (krótkie przez NFC, rozszerzone przez USB); 6985 = czeka na dotknięcie klucza
    private fun apdu(tx: (Int, Int, ByteArray) -> ByteArray, ins: Int, p1: Int, data: ByteArray): ByteArray {
        val end = System.currentTimeMillis() + 30000
        while (true) {
            val r = tx(ins, p1, data); if (r.size < 2) throw Exception()
            val sw = ((r[r.size - 2].toInt() and 0xff) shl 8) or (r[r.size - 1].toInt() and 0xff)
            when (sw) {
                0x9000 -> return r.copyOf(r.size - 2)
                0x6985 -> { if (System.currentTimeMillis() > end) throw Exception(); Thread.sleep(250) }
                0x6A80 -> throw Brak()
                else -> throw Exception()
            }
        }
    }

    private fun pub(p: ByteArray): ECPublicKey {
        val ap = AlgorithmParameters.getInstance("EC"); ap.init(ECGenParameterSpec("secp256r1"))
        val spec = ECPublicKeySpec(ECPoint(java.math.BigInteger(1, p.copyOfRange(1, 33)), java.math.BigInteger(1, p.copyOfRange(33, 65))), ap.getParameterSpec(ECParameterSpec::class.java))
        return KeyFactory.getInstance("EC").generatePublic(spec) as ECPublicKey
    }
    private fun b64(b: ByteArray) = android.util.Base64.encodeToString(b, android.util.Base64.NO_WRAP)
    private fun unb64(s: String) = runCatching { android.util.Base64.decode(s, android.util.Base64.NO_WRAP) }.getOrDefault(ByteArray(0))

    // okienko „przyłóż / włóż klucz”: NFC (tryb czytnika) i USB naraz, pierwszy klucz wygrywa; najwyżej 60 s
    private fun <T> run(a: Activity, work: ((Int, Int, ByteArray) -> ByteArray) -> T?, done: (T?) -> Unit) {
        val pl = java.util.Locale.getDefault().language == "pl"
        val nfc = NfcAdapter.getDefaultAdapter(a)
        var finished = false
        val dlg = android.app.AlertDialog.Builder(a, android.R.style.Theme_Material_Dialog_Alert).setTitle("🔑 " + if (pl) "Klucz sprzętowy" else "Security key")
            .setMessage(if (pl) "Przyłóż klucz do tyłu telefonu (NFC) albo włóż go do USB i dotknij." else "Hold the key to the back of the phone (NFC) or plug it into USB and touch it.")
            .setNegativeButton(android.R.string.cancel, null).create()
        fun finish(v: T?) = a.runOnUiThread {
            if (finished) return@runOnUiThread; finished = true
            runCatching { nfc?.disableReaderMode(a) }; runCatching { dlg.dismiss() }; done(v)
        }
        dlg.setOnDismissListener { if (!finished) { finished = true; runCatching { nfc?.disableReaderMode(a) }; done(null) } }
        dlg.show()
        runCatching {
            nfc?.enableReaderMode(a, { tag ->
                val d = IsoDep.get(tag) ?: return@enableReaderMode
                val v = runCatching { d.connect(); d.timeout = 5000
                    nfcTx(d, byteArrayOf(0, 0xA4.toByte(), 4, 0, 8, 0xA0.toByte(), 0, 0, 6, 0x47, 0x2F, 0, 1))   // wybór aplikacji FIDO U2F
                    work { ins, p1, data -> nfcTx(d, byteArrayOf(0, ins.toByte(), p1.toByte(), 0, data.size.toByte()) + data + byteArrayOf(0)) }
                }.getOrNull()
                runCatching { d.close() }; finish(v)
            }, NfcAdapter.FLAG_READER_NFC_A or NfcAdapter.FLAG_READER_NFC_B or NfcAdapter.FLAG_READER_SKIP_NDEF_CHECK, null)
        }
        Thread {
            val um = a.getSystemService(UsbManager::class.java); val end = System.currentTimeMillis() + 60000; var asked = false
            while (!finished && System.currentTimeMillis() < end) {
                val dev = um?.deviceList?.values?.firstOrNull { fido(it) != null }
                if (dev != null && !um.hasPermission(dev) && !asked) { asked = true
                    runCatching { um.requestPermission(dev, PendingIntent.getBroadcast(a, 0, Intent("pl.velivo.usb").setPackage(a.packageName), PendingIntent.FLAG_MUTABLE)) } }
                if (dev != null && um.hasPermission(dev)) { finish(runCatching { usb(um, dev, work) }.getOrNull()); return@Thread }
                Thread.sleep(500)
            }
        }.start()
    }

    private fun nfcTx(d: IsoDep, cmd: ByteArray): ByteArray {
        var r = d.transceive(cmd); var out = ByteArray(0)
        while (r.size >= 2 && r[r.size - 2] == 0x61.toByte()) { out += r.copyOf(r.size - 2); r = d.transceive(byteArrayOf(0, 0xC0.toByte(), 0, 0, r[r.size - 1])) }   // dalsza część odpowiedzi
        return out + r
    }

    // USB: interfejs HID z wejściem i wyjściem (FIDO); klawiatura klucza (tylko wejście) pomijana
    private fun fido(d: UsbDevice) = (0 until d.interfaceCount).map { d.getInterface(it) }.firstOrNull { i ->
        i.interfaceClass == UsbConstants.USB_CLASS_HID && (0 until i.endpointCount).map { i.getEndpoint(it) }.let { e -> e.any { it.direction == UsbConstants.USB_DIR_IN } && e.any { it.direction == UsbConstants.USB_DIR_OUT } } }

    // CTAPHID: kanał (INIT), potem MSG z APDU w ramkach po 64 bajty; KEEPALIVE pomijane
    private fun <T> usb(um: UsbManager, dev: UsbDevice, work: ((Int, Int, ByteArray) -> ByteArray) -> T?): T? {
        val itf = fido(dev) ?: return null; val c = um.openDevice(dev) ?: return null
        try {
            c.claimInterface(itf, true)
            val eps = (0 until itf.endpointCount).map { itf.getEndpoint(it) }
            val inp = eps.first { it.direction == UsbConstants.USB_DIR_IN }; val out = eps.first { it.direction == UsbConstants.USB_DIR_OUT }
            fun send(cid: ByteArray, cmd: Int, data: ByteArray) {
                var p = ByteArray(64); cid.copyInto(p); p[4] = (cmd or 0x80).toByte(); p[5] = (data.size shr 8).toByte(); p[6] = data.size.toByte()
                data.copyInto(p, 7, 0, minOf(57, data.size)); c.bulkTransfer(out, p, 64, 3000)
                var off = 57; var seq = 0
                while (off < data.size) { p = ByteArray(64); cid.copyInto(p); p[4] = seq++.toByte(); data.copyInto(p, 5, off, minOf(off + 59, data.size)); off += 59; c.bulkTransfer(out, p, 64, 3000) }
            }
            fun recv(cid: ByteArray): ByteArray {
                val p = ByteArray(64); val end = System.currentTimeMillis() + 35000
                while (true) {
                    if (System.currentTimeMillis() > end) throw Exception()
                    if (c.bulkTransfer(inp, p, 64, 1000) < 7 || !p.copyOf(4).contentEquals(cid)) continue
                    if (p[4] == 0xBB.toByte()) continue   // klucz czeka na dotknięcie
                    val n = ((p[5].toInt() and 0xff) shl 8) or (p[6].toInt() and 0xff); val r = ByteArray(n)
                    p.copyInto(r, 0, 7, 7 + minOf(57, n)); var off = 57
                    while (off < n) { if (c.bulkTransfer(inp, p, 64, 3000) < 5) throw Exception(); p.copyInto(r, off, 5, 5 + minOf(59, n - off)); off += 59 }
                    return r
                }
            }
            val all = byteArrayOf(-1, -1, -1, -1); val nonce = ByteArray(8).also { SecureRandom().nextBytes(it) }
            send(all, 0x06, nonce)
            var r: ByteArray; do { r = recv(all) } while (r.size < 12 || !r.copyOf(8).contentEquals(nonce))
            val cid = r.copyOfRange(8, 12)
            return work { ins, p1, data -> send(cid, 0x03, byteArrayOf(0, ins.toByte(), p1.toByte(), 0, 0, (data.size shr 8).toByte(), data.size.toByte()) + data + byteArrayOf(0, 0)); recv(cid) }
        } finally { runCatching { c.releaseInterface(itf) }; c.close() }
    }
}
