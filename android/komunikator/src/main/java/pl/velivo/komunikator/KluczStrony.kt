package pl.velivo.komunikator

import android.app.Activity
import android.text.InputType
import android.widget.EditText
import android.widget.Toast
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.SecretKeySpec

// 🔑 logowanie kluczem sprzętowym na stronach (WebAuthn) bez usług Google: strona woła navigator.credentials, skrypt Velivo przekazuje
// prośbę do aplikacji, aplikacja rozmawia z kluczem (FIDO2 przez USB albo NFC, z PIN-em klucza). Adres strony (origin) podaje WebView,
// nie strona - fałszywa strona nie dostanie podpisu dla google.com. Tylko główna ramka i https.
object KluczStrony {
    private fun t(p: String, e: String) = if (java.util.Locale.getDefault().language == "pl") p else e

    // skrypt na początku każdej strony: navigator.credentials.get/create z publicKey -> Velivo; „📱 Telefon” - zwykła obsługa WebView
    const val JS = """(function(){
if(location.protocol!=='https:'||!window.VelivoKlucz||!navigator.credentials)return;
var C=navigator.credentials,oG=C.get.bind(C),oC=C.create.bind(C),seq=0,wait={};
function b64(v){var u=new Uint8Array(v instanceof ArrayBuffer?v:v.buffer.slice(v.byteOffset,v.byteOffset+v.byteLength)),s='';for(var i=0;i<u.length;i++)s+=String.fromCharCode(u[i]);return btoa(s).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');}
function ab(s){if(s==null)return null;s=s.replace(/-/g,'+').replace(/_/g,'/');while(s.length%4)s+='=';var b=atob(s),u=new Uint8Array(b.length);for(var i=0;i<b.length;i++)u[i]=b.charCodeAt(i);return u.buffer;}
function ids(l){return (l||[]).map(function(c){return {id:b64(c.id),transports:c.transports||[]};});}
VelivoKlucz.onmessage=function(e){var m=JSON.parse(e.data),w=wait[m.id];if(w){delete wait[m.id];w(m);}};
if(!window.PublicKeyCredential){window.PublicKeyCredential=function(){};PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable=function(){return Promise.resolve(false);};PublicKeyCredential.isConditionalMediationAvailable=function(){return Promise.resolve(false);};}
function cred(op,m){var r={clientDataJSON:ab(m.clientDataJSON)};
if(op==='get'){r.authenticatorData=ab(m.authenticatorData);r.signature=ab(m.signature);r.userHandle=ab(m.userHandle);}
else{r.attestationObject=ab(m.attestationObject);r.getTransports=function(){return ['usb','nfc'];};r.getAuthenticatorData=function(){return ab(m.authenticatorData);};r.getPublicKey=function(){return ab(m.publicKey);};r.getPublicKeyAlgorithm=function(){return m.alg;};}
var c={id:m.rawId,rawId:ab(m.rawId),type:'public-key',authenticatorAttachment:'cross-platform',response:r,getClientExtensionResults:function(){return m.ext||{};}};
c.toJSON=function(){var o={id:m.rawId,rawId:m.rawId,type:'public-key',authenticatorAttachment:'cross-platform',clientExtensionResults:m.ext||{},response:{clientDataJSON:m.clientDataJSON}};
if(op==='get'){o.response.authenticatorData=m.authenticatorData;o.response.signature=m.signature;o.response.userHandle=m.userHandle;}else{o.response.attestationObject=m.attestationObject;o.response.authenticatorData=m.authenticatorData;o.response.publicKey=m.publicKey;o.response.publicKeyAlgorithm=m.alg;o.response.transports=['usb','nfc'];}return o;};
try{Object.setPrototypeOf(c,PublicKeyCredential.prototype);}catch(e){}return c;}
function ask(op,o){var p=o.publicKey,j={challenge:b64(p.challenge),timeout:p.timeout,userVerification:p.userVerification||(p.authenticatorSelection||{}).userVerification||'preferred',ext:p.extensions?{appid:p.extensions.appid}:{}};
if(op==='get'){j.rpId=p.rpId;j.allow=ids(p.allowCredentials);}
else{j.rp=p.rp;j.user={id:b64(p.user.id),name:p.user.name,displayName:p.user.displayName};j.alg=(p.pubKeyCredParams||[]).map(function(x){return x.alg;});j.exclude=ids(p.excludeCredentials);j.rk=(p.authenticatorSelection||{}).residentKey||((p.authenticatorSelection||{}).requireResidentKey?'required':'discouraged');j.att=p.attestation||'none';}
return new Promise(function(res,rej){var id=++seq;wait[id]=function(m){if(m.fallback)return res(op==='get'?oG(o):oC(o));if(m.error)return rej(new DOMException(m.error,m.name||'NotAllowedError'));res(cred(op,m));};
VelivoKlucz.postMessage(JSON.stringify({id:id,op:op,pk:j}));});}
C.get=function(o){return o&&o.publicKey&&o.mediation!=='conditional'?ask('get',o):oG(o);};
C.create=function(o){return o&&o.publicKey?ask('create',o):oC(o);};
})();"""

    // prośba ze strony; reply - odpowiedź do skryptu (w wątku okna)
    fun request(a: Activity, data: String, origin: String, mainFrame: Boolean, reply: (String) -> Unit) {
        val m = runCatching { JSONObject(data) }.getOrNull() ?: return
        val id = m.optInt("id"); val op = m.optString("op"); val pk = m.optJSONObject("pk") ?: return
        fun answer(o: JSONObject) = a.runOnUiThread { runCatching { reply(o.put("id", id).toString()) } }
        val fallback = { answer(JSONObject().put("fallback", true)) }
        val o = android.net.Uri.parse(origin); val host = o.host.orEmpty()
        if (!mainFrame || o.scheme != "https" || host.isEmpty() || op !in setOf("get", "create")) { fallback(); return }
        val org = "https://" + host + if (o.port > 0 && o.port != 443) ":" + o.port else ""
        val rpId = (if (op == "get") pk.optString("rpId") else pk.optJSONObject("rp")?.optString("id").orEmpty()).ifEmpty { host }
        if (!(host == rpId || host.endsWith(".$rpId")) || !rpId.contains('.')) { answer(JSONObject().put("error", "rpId").put("name", "SecurityError")); return }   // strona nie może prosić o cudzą domenę
        a.runOnUiThread {
            val pin = EditText(a).apply { hint = t("PIN klucza (jeśli ustawiony)", "Key PIN (if set)"); setTextColor(android.graphics.Color.WHITE); setHintTextColor(android.graphics.Color.GRAY) }
            pin.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD   // PIN klucza może mieć litery
            var chosen = false
            android.app.AlertDialog.Builder(a, android.R.style.Theme_Material_Dialog_Alert)
                .setTitle("🔑 " + host)
                .setMessage(if (op == "get") t("Strona prosi o zalogowanie kluczem sprzętowym (YubiKey, Titan).", "The site asks to sign in with a security key (YubiKey, Titan).")
                    else t("Strona chce zapisać klucz dostępu na Twoim kluczu sprzętowym.", "The site wants to save a passkey on your security key."))
                .setView(pin)
                .setPositiveButton(t("🔑 Klucz USB / NFC", "🔑 USB / NFC key")) { _, _ -> chosen = true
                    Klucz.run(a, "🔑 $host", { tx -> runCatching { if (op == "get") get(tx, pk, org, rpId, pin.text.toString()) else create(tx, pk, org, rpId, pin.text.toString()) }.getOrElse { e -> JSONObject().put("error", (e as? Fido)?.msg ?: t("Klucz nie odpowiedział", "The key did not respond")) } }) { r ->
                        val res = r ?: JSONObject().put("error", t("Anulowano", "Cancelled"))
                        if (res.has("error")) Toast.makeText(a, "🔑 " + res.optString("error"), Toast.LENGTH_LONG).show()
                        answer(res) } }
                .setNeutralButton(t("📱 Telefon", "📱 Phone")) { _, _ -> chosen = true; fallback() }
                .setNegativeButton(android.R.string.cancel) { _, _ -> chosen = true; answer(JSONObject().put("error", t("Anulowano", "Cancelled"))) }
                .setOnDismissListener { if (!chosen) answer(JSONObject().put("error", t("Anulowano", "Cancelled"))) }
                .show()
        }
    }

    private class Fido(val code: Int, val msg: String) : Exception(msg)
    private fun err(code: Int) = Fido(code, when (code) {
        0x31 -> t("Zły PIN klucza", "Wrong key PIN"); 0x32, 0x34 -> t("PIN klucza zablokowany – za dużo prób", "Key PIN blocked – too many attempts")
        0x36 -> t("Podaj PIN klucza", "Enter the key PIN"); 0x35 -> t("Ustaw PIN klucza (np. aplikacją Yubico Authenticator)", "Set a key PIN (e.g. with Yubico Authenticator)")
        0x2E -> t("Na tym kluczu nie ma klucza dostępu dla tej strony", "This key has no passkey for this site"); 0x19 -> t("Ten klucz jest już dodany na tej stronie", "This key is already registered on this site")
        0x27, 0x2F -> t("Anulowano na kluczu", "Cancelled on the key"); 0x28 -> t("Brak miejsca na kluczu", "The key is full")
        else -> t("Błąd klucza ", "Key error ") + "0x" + Integer.toHexString(code) })

    private fun b64u(b: ByteArray?) = if (b == null) null else android.util.Base64.encodeToString(b, android.util.Base64.NO_WRAP or android.util.Base64.NO_PADDING or android.util.Base64.URL_SAFE)
    private fun unb64u(s: String?) = runCatching { android.util.Base64.decode(s ?: "", android.util.Base64.URL_SAFE) }.getOrDefault(ByteArray(0))
    private fun sha(b: ByteArray) = MessageDigest.getInstance("SHA-256").digest(b)
    private fun clientData(type: String, pk: JSONObject, origin: String) =
        JSONObject().put("type", type).put("challenge", pk.optString("challenge")).put("origin", origin).put("crossOrigin", false).toString().replace("\\/", "/").toByteArray()

    // polecenie FIDO2: status 0 = dobrze; odpowiedź - mapa CBOR
    private fun ctap(tx: Klucz.Tx, cmd: Int, params: Map<*, *>?): Map<*, *> {
        val r = tx.cbor(byteArrayOf(cmd.toByte()) + (params?.let { Cbor.enc(it) } ?: ByteArray(0)))
        if (r.isEmpty()) throw Fido(-1, t("Klucz nie odpowiedział", "The key did not respond"))
        if (r[0].toInt() != 0) throw err(r[0].toInt() and 0xff)
        return if (r.size > 1) Cbor.dec(r) as? Map<*, *> ?: emptyMap<Any, Any>() else emptyMap<Any, Any>()
    }

    // PIN (protokół 1): wspólny sekret ECDH z kluczem, PIN zaszyfrowany, w zamian token; pinAuth = HMAC(token, skrót danych strony)
    // wynik: pinUvAuthParam (16 bajtów) albo null - bez PIN-u
    private fun pinAuth(tx: Klucz.Tx, pin: String, uv: String, cdh: ByteArray): ByteArray? {
        val opts = ctap(tx, 0x04, null)[4L] as? Map<*, *>
        val hasPin = opts?.get("clientPin") == true
        if (!hasPin) { if (uv == "required") throw err(0x35); return null }
        if (pin.isEmpty()) { if (uv == "required") throw err(0x36); return null }
        val ka = ctap(tx, 0x06, linkedMapOf<Any, Any>(1L to 1L, 2L to 2L))[1L] as? Map<*, *> ?: throw err(0x01)
        val ap = java.security.AlgorithmParameters.getInstance("EC"); ap.init(ECGenParameterSpec("secp256r1"))
        val spec = ap.getParameterSpec(java.security.spec.ECParameterSpec::class.java)
        val their = KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(ECPoint(BigInteger(1, ka[-2L] as ByteArray), BigInteger(1, ka[-3L] as ByteArray)), spec))
        val kp = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        val shared = sha(KeyAgreement.getInstance("ECDH").apply { init(kp.private); doPhase(their, true) }.generateSecret())
        fun p32(b: BigInteger) = b.toByteArray().let { if (it.size > 32) it.copyOfRange(it.size - 32, it.size) else ByteArray(32 - it.size) + it }
        val w = (kp.public as ECPublicKey).w
        val cose = linkedMapOf<Any, Any>(1L to 2L, 3L to -25L, -1L to 1L, -2L to p32(w.affineX), -3L to p32(w.affineY))
        fun aes(mode: Int, b: ByteArray) = Cipher.getInstance("AES/CBC/NoPadding").apply { init(mode, SecretKeySpec(shared, "AES"), IvParameterSpec(ByteArray(16))) }.doFinal(b)
        val enc = aes(Cipher.ENCRYPT_MODE, sha(pin.toByteArray()).copyOf(16))
        val tok = aes(Cipher.DECRYPT_MODE, ctap(tx, 0x06, linkedMapOf<Any, Any>(1L to 1L, 2L to 5L, 3L to cose, 6L to enc))[2L] as? ByteArray ?: throw err(0x31))
        return Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(tok, "HmacSHA256")) }.doFinal(cdh).copyOf(16)
    }

    private fun allow(l: JSONArray?) = (0 until (l?.length() ?: 0)).map { linkedMapOf<Any, Any>("id" to unb64u(l!!.getJSONObject(it).optString("id")), "type" to "public-key") }

    // logowanie: getAssertion; stare klucze U2F Google - rozszerzenie appid (tylko dla tej samej strony albo gstatic dla google.com)
    private fun get(tx: Klucz.Tx, pk: JSONObject, origin: String, rpId: String, pin: String): JSONObject {
        val cd = clientData("webauthn.get", pk, origin); val cdh = sha(cd)
        val al = allow(pk.optJSONArray("allow"))
        val auth = pinAuth(tx, pin, pk.optString("userVerification"), cdh)
        fun once(rp: String): Map<*, *> {
            val p = linkedMapOf<Any, Any>(1L to rp, 2L to cdh); if (al.isNotEmpty()) p[3L] = al
            p[5L] = linkedMapOf<Any, Any>("up" to true); if (auth != null) { p[6L] = auth; p[7L] = 1L }
            return ctap(tx, 0x02, p)
        }
        val appid = pk.optJSONObject("ext")?.optString("appid").orEmpty()
        val appOk = appid.isNotEmpty() && al.isNotEmpty() && (android.net.Uri.parse(appid).host == rpId || (appid == "https://www.gstatic.com/securitykey/origins.json" && (rpId == "google.com" || rpId.endsWith(".google.com"))))
        var usedAppid = false
        val r = try { once(rpId) } catch (e: Fido) { if (!appOk || e.code != 0x2E) throw e; usedAppid = true; once(appid) }
        val cid = (r[1L] as? Map<*, *>)?.get("id") as? ByteArray ?: (al.singleOrNull()?.get("id") as? ByteArray) ?: throw err(0x2E)
        val user = (r[4L] as? Map<*, *>)?.get("id") as? ByteArray
        return JSONObject().put("rawId", b64u(cid)).put("clientDataJSON", b64u(cd)).put("authenticatorData", b64u(r[2L] as ByteArray)).put("signature", b64u(r[3L] as ByteArray))
            .put("userHandle", b64u(user) ?: JSONObject.NULL).put("ext", if (appid.isNotEmpty()) JSONObject().put("appid", usedAppid) else JSONObject())
    }

    // zapisanie klucza dostępu: makeCredential (ES256 / EdDSA); attestation "none" - bez danych o modelu klucza
    private fun create(tx: Klucz.Tx, pk: JSONObject, origin: String, rpId: String, pin: String): JSONObject {
        val cd = clientData("webauthn.create", pk, origin); val cdh = sha(cd)
        val algs = pk.optJSONArray("alg")?.let { a -> (0 until a.length()).map { a.optLong(it) } }.orEmpty().filter { it == -7L || it == -8L }.ifEmpty { listOf(-7L) }
        val u = pk.optJSONObject("user") ?: JSONObject()
        val rk = pk.optString("rk") in setOf("required", "preferred")
        val auth = pinAuth(tx, pin, if (rk && pk.optString("userVerification") != "discouraged") "required" else pk.optString("userVerification"), cdh)
        val p = linkedMapOf<Any, Any>(1L to cdh, 2L to linkedMapOf<Any, Any>("id" to rpId, "name" to (pk.optJSONObject("rp")?.optString("name")?.ifEmpty { null } ?: rpId)),
            3L to linkedMapOf<Any, Any>("id" to unb64u(u.optString("id")), "name" to u.optString("name"), "displayName" to u.optString("displayName").ifEmpty { u.optString("name") }),
            4L to algs.map { linkedMapOf<Any, Any>("alg" to it, "type" to "public-key") })
        allow(pk.optJSONArray("exclude")).takeIf { it.isNotEmpty() }?.let { p[5L] = it }
        if (rk) p[7L] = linkedMapOf<Any, Any>("rk" to true)
        if (auth != null) { p[8L] = auth; p[9L] = 1L }   // makeCredential: PIN w polach 8 i 9 (getAssertion: 6 i 7)
        val r = ctap(tx, 0x01, p)
        val ad = r[2L] as ByteArray
        val none = pk.optString("att") == "none"
        val att = Cbor.enc(linkedMapOf<Any, Any>("fmt" to (if (none) "none" else r[1L] as String), "attStmt" to (if (none) linkedMapOf<Any, Any>() else r[3L] as Map<*, *>), "authData" to ad))
        // dane klucza z authData: AAGUID(16) | długość id(2) | id | klucz publiczny COSE
        val n = ((ad[53].toInt() and 0xff) shl 8) or (ad[54].toInt() and 0xff)
        val cid = ad.copyOfRange(55, 55 + n)
        val cose = Cbor.dec(ad, 55 + n) as? Map<*, *>
        val alg = cose?.get(3L) as? Long ?: -7L
        val spki = when (alg) {
            -7L -> hex("3059301306072a8648ce3d020106082a8648ce3d030107034200") + byteArrayOf(4) + (cose!![-2L] as ByteArray) + (cose[-3L] as ByteArray)
            -8L -> hex("302a300506032b6570032100") + (cose!![-2L] as ByteArray)
            else -> null
        }
        return JSONObject().put("rawId", b64u(cid)).put("clientDataJSON", b64u(cd)).put("attestationObject", b64u(att)).put("authenticatorData", b64u(ad))
            .put("publicKey", b64u(spki) ?: JSONObject.NULL).put("alg", alg)
    }
    private fun hex(s: String) = ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
}

// CBOR (RFC 8949) w zakresie FIDO2: liczby, bajty, tekst, listy, mapy (kolejność kluczy podana przez wołającego - kanoniczna), prawda/fałsz
object Cbor {
    fun enc(v: Any?): ByteArray = ByteArrayOutputStream().also { w(it, v) }.toByteArray()
    private fun head(o: ByteArrayOutputStream, major: Int, n: Long) {
        val m = major shl 5
        when {
            n < 24 -> o.write(m or n.toInt())
            n < 0x100 -> { o.write(m or 24); o.write(n.toInt()) }
            n < 0x10000 -> { o.write(m or 25); o.write((n shr 8).toInt()); o.write(n.toInt() and 0xff) }
            n < 0x100000000L -> { o.write(m or 26); for (i in 3 downTo 0) o.write(((n shr (8 * i)) and 0xff).toInt()) }
            else -> { o.write(m or 27); for (i in 7 downTo 0) o.write(((n shr (8 * i)) and 0xff).toInt()) }
        }
    }
    private fun w(o: ByteArrayOutputStream, v: Any?) {
        when (v) {
            null -> o.write(0xf6)
            is Boolean -> o.write(if (v) 0xf5 else 0xf4)
            is Int -> w(o, v.toLong())
            is Long -> if (v >= 0) head(o, 0, v) else head(o, 1, -1 - v)
            is ByteArray -> { head(o, 2, v.size.toLong()); o.write(v) }
            is String -> { val b = v.toByteArray(); head(o, 3, b.size.toLong()); o.write(b) }
            is List<*> -> { head(o, 4, v.size.toLong()); v.forEach { w(o, it) } }
            is Map<*, *> -> { head(o, 5, v.size.toLong()); v.forEach { (k, x) -> w(o, k); w(o, x) } }
            else -> throw IllegalArgumentException()
        }
    }
    // od pozycji at (domyślnie 1 - za bajtem statusu FIDO2)
    fun dec(b: ByteArray, at: Int = 1): Any? = R(b, at).v()
    private class R(val b: ByteArray, var i: Int) {
        fun u(n: Int): Long { var x = 0L; repeat(n) { x = (x shl 8) or (b[i++].toLong() and 0xff) }; return x }
        fun v(): Any? {
            val ib = b[i++].toInt() and 0xff; val major = ib shr 5; val ai = ib and 31
            if (major == 7) return when (ai) { 20 -> false; 21 -> true; 22, 23 -> null; 25 -> { i += 2; 0.0 }; 26 -> { i += 4; 0.0 }; 27 -> { i += 8; 0.0 }; else -> null }
            val n = when (ai) { in 0..23 -> ai.toLong(); 24 -> u(1); 25 -> u(2); 26 -> u(4); 27 -> u(8); else -> throw IllegalArgumentException() }
            return when (major) {
                0 -> n; 1 -> -1 - n
                2 -> b.copyOfRange(i, i + n.toInt()).also { i += n.toInt() }
                3 -> String(b, i, n.toInt()).also { i += n.toInt() }
                4 -> List(n.toInt()) { v() }
                5 -> LinkedHashMap<Any?, Any?>().also { m -> repeat(n.toInt()) { val k = v(); m[k] = v() } }
                6 -> v()   // znacznik - pomijany
                else -> throw IllegalArgumentException()
            }
        }
    }
}
