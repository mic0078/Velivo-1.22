package pl.velivo.klawiatura

import pl.velivo.komunikator.*
import android.app.Activity
import android.content.ContentValues
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.provider.MediaStore
import android.widget.Toast

// 💬 komunikator i 📲 pliki na inny telefon - gdy w telefonie jest sama klawiatura Velivo (bez przeglądarki).
// Własne gniazda na porcie 41919 tylko na czas otwartego okna.
class Rozmowy : Activity() {
    private lateinit var k: Komunikator
    private lateinit var okno: KomunikatorOkno

    override fun onCreate(b: Bundle?) {
        super.onCreate(b)
        syncIcon(this)
        // z ikony, a w telefonie jest Komunikator Velivo albo przeglądarka Velivo - ich komunikator (jeden w telefonie)
        if (hasBrowser(this)) { open(this, intent?.action == ACTION_FILES); finish(); return }
        k = Komunikator.get(this) { (Build.MANUFACTURER + " " + Build.MODEL).trim() }
        k.saveFile = { name, tmp, dev -> save(name, tmp, dev) }
        k.openIntent = { id -> Intent(applicationContext, Rozmowy::class.java).putExtra("czat", id).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK) }
        okno = KomunikatorOkno(this, k) { pick() }
        okno.onClose = { if (!picking) finish() }
        KomunikatorUsluga.stop(this, k)   // okno otwarte - gniazda tutaj, nie w usłudze
        k.listen()
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 2)
        window.decorView.post {
            k.listenError?.let { Toast.makeText(this, getString(R.string.kb_p2p_port, it), Toast.LENGTH_LONG).show() }
            val id = intent?.getStringExtra("czat")
            if (intent?.action == ACTION_FILES) pick() else id?.let { k.contact(it) }?.let { okno.openChat(it) } ?: okno.show()
        }
    }

    private var picking = false
    private fun pick() = runCatching {
        picking = true
        startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*").putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true), 1)
    }

    @Deprecated("Activity API")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        picking = false
        if (resultCode != RESULT_OK || data == null) { okno.show(); return }
        val l = ArrayList<Uri>(); data.clipData?.let { c -> for (i in 0 until c.itemCount) l.add(c.getItemAt(i).uri) } ?: data.data?.let { l.add(it) }
        okno.sendFiles(l)
    }

    // odebrany plik do Pobranych (MediaStore - bez uprawnień)
    private fun save(name: String, tmp: java.io.File, dev: String) {
        val ok = runCatching {
            val v = ContentValues().apply { put(MediaStore.Downloads.DISPLAY_NAME, name); put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS) }
            val uri = contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, v)!!
            contentResolver.openOutputStream(uri)!!.use { o -> tmp.inputStream().use { it.copyTo(o, 1 shl 16) } }
        }.isSuccess
        tmp.delete()
        runOnUiThread { Toast.makeText(this, getString(if (ok) R.string.kb_p2p_saved else R.string.kb_p2p_failed, name, dev), Toast.LENGTH_LONG).show() }
    }

    // okno zamknięte - dalej czeka usługa w tle (powiadomienia o wiadomościach)
    override fun onDestroy() { k.close(); KomunikatorUsluga.start(this, k); super.onDestroy() }

    companion object {
        const val ACTION_FILES = "pl.velivo.klawiatura.PLIKI"
        // z aplikacją Komunikator Velivo albo przeglądarką Velivo w telefonie - tam komunikator; bez nich - własne okno klawiatury
        fun hasBrowser(ctx: android.content.Context) = Komunikator.owner(ctx) != ctx.packageName

        // ikona „Komunikator Velivo” klawiatury tylko bez przeglądarki (z przeglądarką ikona jest w niej - bez dwóch takich samych)
        fun syncIcon(ctx: android.content.Context) = runCatching {
            val cn = android.content.ComponentName(ctx, "pl.velivo.klawiatura.KomunikatorStart")
            val want = if (hasBrowser(ctx)) android.content.pm.PackageManager.COMPONENT_ENABLED_STATE_DISABLED else android.content.pm.PackageManager.COMPONENT_ENABLED_STATE_DEFAULT
            if (ctx.packageManager.getComponentEnabledSetting(cn) != want) ctx.packageManager.setComponentEnabledSetting(cn, want, android.content.pm.PackageManager.DONT_KILL_APP)
        }

        fun open(ctx: android.content.Context, files: Boolean) {
            if (Komunikator.owner(ctx) == Komunikator.APP && runCatching {
                    ctx.startActivity(Intent(if (files) "pl.velivo.komunikator.PLIKI" else "pl.velivo.komunikator.OTWORZ").setPackage(Komunikator.APP).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }.isSuccess) return
            val browser = Intent(if (files) "pl.velivo.browser.WYSLIJ_NA_TELEFON" else "pl.velivo.browser.KOMUNIKATOR")
                .setClassName("pl.velivo.browser", "pl.velivo.browser.MainActivity").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            runCatching { ctx.startActivity(browser) }.onFailure {
                ctx.startActivity(Intent(ctx, Rozmowy::class.java).setAction(if (files) ACTION_FILES else Intent.ACTION_MAIN).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
        }
    }
}
