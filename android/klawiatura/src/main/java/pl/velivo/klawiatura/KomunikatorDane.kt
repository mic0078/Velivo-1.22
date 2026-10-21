package pl.velivo.klawiatura

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle

// Telefon z klawiaturą i przeglądarką Velivo: przeglądarka przejmuje kontakty, rozmowy i identyfikator komunikatora z klawiatury
// (jeden komunikator, bez dublowania). Dostęp tylko dla aplikacji Velivo podpisanych tym samym kluczem (uprawnienie "signature").
class KomunikatorDane : ContentProvider() {
    override fun onCreate() = true

    override fun call(method: String, arg: String?, extras: Bundle?): Bundle? {
        val ctx = context ?: return null
        if (ctx.checkCallingPermission(PERM) != PackageManager.PERMISSION_GRANTED) return null
        Rozmowy.syncIcon(ctx)   // przeglądarka Velivo się odezwała - ikona komunikatora klawiatury znika od razu (bez dwóch takich samych)
        val k = Komunikator.get(ctx) { (Build.MANUFACTURER + " " + Build.MODEL).trim() }
        return when (method) {
            "oddaj" -> if (k.contacts.isEmpty()) null else Bundle().apply { putString("json", k.export()) }
            "slownik" -> Bundle().apply { putString("json", KlawiaturaVelivo.syncExport(ctx)) }   // 🔄 nauka klawiatury dla komputera
            "slownik-wgraj" -> { arg?.let { KlawiaturaVelivo.syncImport(ctx, it) }; null }
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
