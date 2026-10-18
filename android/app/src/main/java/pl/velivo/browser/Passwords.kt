package pl.velivo.browser

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

// Hasła zaszyfrowane kluczem AES z Android Keystore (klucz nie opuszcza telefonu, jak DPAPI na Windows).
// Zapis: jeden zaszyfrowany blok JSON [{host, user, pass}] w pliku prywatnym aplikacji.
class Passwords(ctx: Context) {
    data class Entry(val host: String, val user: String, val pass: String)

    private val prefs = ctx.getSharedPreferences("hasla", Context.MODE_PRIVATE)

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build()
        )
        return gen.generateKey()
    }

    fun all(): List<Entry> {
        val data = prefs.getString("dane", null) ?: return emptyList()
        return runCatching {
            val raw = Base64.decode(data, Base64.NO_WRAP)
            val c = Cipher.getInstance("AES/GCM/NoPadding")
            c.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, raw, 0, 12))
            val arr = JSONArray(String(c.doFinal(raw, 12, raw.size - 12), Charsets.UTF_8))
            (0 until arr.length()).map { arr.getJSONObject(it).let { o -> Entry(o.getString("h"), o.getString("u"), o.getString("p")) } }
        }.getOrDefault(emptyList())
    }

    private fun write(list: List<Entry>) {
        val arr = JSONArray()
        list.forEach { arr.put(JSONObject().put("h", it.host).put("u", it.user).put("p", it.pass)) }
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, key())
        val enc = c.iv + c.doFinal(arr.toString().toByteArray(Charsets.UTF_8))
        prefs.edit().putString("dane", Base64.encodeToString(enc, Base64.NO_WRAP)).apply()
    }

    fun forHost(host: String) = all().filter { it.host == host }

    fun isSaved(host: String, user: String, pass: String) = all().any { it.host == host && it.user == user && it.pass == pass }

    fun save(host: String, user: String, pass: String) =
        write(all().filter { !(it.host == host && it.user == user) } + Entry(host, user, pass))

    fun remove(e: Entry) = write(all().filter { !(it.host == e.host && it.user == e.user) })

    // Strony, dla których użytkownik wybrał "Nigdy".
    fun never(host: String) = prefs.getStringSet("nigdy", emptySet())!!.contains(host)
    fun addNever(host: String) = prefs.edit().putStringSet("nigdy", prefs.getStringSet("nigdy", emptySet())!! + host).apply()

    private companion object { const val ALIAS = "velivo-hasla" }
}
