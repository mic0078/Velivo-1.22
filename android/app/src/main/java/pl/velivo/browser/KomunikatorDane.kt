package pl.velivo.browser

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Bundle
import pl.velivo.komunikator.Komunikator
import pl.velivo.komunikator.KomunikatorUsluga

// 💬 Telefon z aplikacją Komunikator Velivo: ona przejmuje kontakty, rozmowy i identyfikator komunikatora z przeglądarki
// (jeden komunikator, bez dublowania). Dostęp tylko dla aplikacji Velivo podpisanych tym samym kluczem (uprawnienie "signature").
class KomunikatorDane : ContentProvider() {
    override fun onCreate() = true

    override fun call(method: String, arg: String?, extras: Bundle?): Bundle? {
        val ctx = context ?: return null
        if (ctx.checkCallingPermission(PERM) != PackageManager.PERMISSION_GRANTED) return null
        val k = Komunikator.get(ctx) { LanSync.MODEL }
        return when (method) {
            "oddaj" -> if (k.contacts.isEmpty()) null else Bundle().apply { putString("json", k.export()) }
            "przekazano" -> { runCatching { ctx.stopService(Intent(ctx, KomunikatorUsluga::class.java)) }; k.wipe(); null }
            else -> null
        }
    }

    override fun query(u: Uri, p: Array<out String>?, s: String?, a: Array<out String>?, o: String?) = null
    override fun getType(u: Uri): String? = null
    override fun insert(u: Uri, v: ContentValues?): Uri? = null
    override fun delete(u: Uri, s: String?, a: Array<out String>?) = 0
    override fun update(u: Uri, v: ContentValues?, s: String?, a: Array<out String>?) = 0

    companion object { const val PERM = "pl.velivo.permission.KOMUNIKATOR" }
}
