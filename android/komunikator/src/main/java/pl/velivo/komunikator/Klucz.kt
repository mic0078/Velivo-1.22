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
