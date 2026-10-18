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

    private fun decrypt(name: String): String? {
        val data = prefs.getString(name, null) ?: return null
        return runCatching {
            val raw = Base64.decode(data, Base64.NO_WRAP)
            val c = Cipher.getInstance("AES/GCM/NoPadding")
            c.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, raw, 0, 12))
            String(c.doFinal(raw, 12, raw.size - 12), Charsets.UTF_8)
        }.getOrNull()
    }

    private fun encrypt(name: String, text: String) {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, key())
        val enc = c.iv + c.doFinal(text.toByteArray(Charsets.UTF_8))
        prefs.edit().putString(name, Base64.encodeToString(enc, Base64.NO_WRAP)).apply()
    }

    fun all(): List<Entry> = runCatching {
        val arr = JSONArray(decrypt("dane") ?: return emptyList())
        (0 until arr.length()).map { arr.getJSONObject(it).let { o -> Entry(o.getString("h"), o.getString("u"), o.getString("p")) } }
    }.getOrDefault(emptyList())

    private fun write(list: List<Entry>) {
        val arr = JSONArray()
        list.forEach { arr.put(JSONObject().put("h", it.host).put("u", it.user).put("p", it.pass)) }
        encrypt("dane", arr.toString())
    }

    // Klucz synchronizacji z Velivo na Windows - zaszyfrowany tak samo jak hasła.
    var lanKey: String?
        get() = decrypt("lan")
        set(v) { if (v == null) prefs.edit().remove("lan").apply() else encrypt("lan", v) }

    // Wiele haseł naraz (z komputera) - jeden zapis zamiast jednego na każde hasło.
    fun saveAll(items: List<Triple<String, String, String>>): Int {
        val list = all().toMutableList()
        var changed = 0
        for ((h, u, p) in items) {
            val i = list.indexOfFirst { it.host == h && it.user == u }
            if (i >= 0 && list[i].pass == p) continue
            if (i >= 0) list[i] = Entry(h, u, p) else list.add(Entry(h, u, p))
            changed++
        }
        if (changed > 0) write(list)
        return changed
    }

    // Dopasowanie jak w Velivo na Windows (PasswordEntryMatchesHost): bez "www.", ta sama domena albo poddomena
    // w dowolną stronę. Nigdy po podobnej nazwie - podróbka "facebook.xyz" nie dostanie hasła do Facebooka.
    fun forHost(host: String): List<Entry> {
        val target = norm(host)
        return all().filter { val h = norm(it.host); h.isNotEmpty() && (h == target || target.endsWith(".$h") || h.endsWith(".$target")) }
    }

    private fun norm(h: String) = h.trim().lowercase().removePrefix("www.")

    fun isSaved(host: String, user: String, pass: String) = all().any { it.host == host && it.user == user && it.pass == pass }

    fun save(host: String, user: String, pass: String) =
        write(all().filter { !(it.host == host && it.user == user) } + Entry(host, user, pass))

    fun remove(e: Entry) = write(all().filter { !(it.host == e.host && it.user == e.user) })

    // Strony, dla których użytkownik wybrał "Nigdy".
    fun never(host: String) = prefs.getStringSet("nigdy", emptySet())!!.contains(host)
    fun addNever(host: String) = prefs.edit().putStringSet("nigdy", prefs.getStringSet("nigdy", emptySet())!! + host).apply()

    private companion object { const val ALIAS = "velivo-hasla" }
}
