package pl.velivo.komunikator

import android.app.Activity
import android.content.ContentValues
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.provider.MediaStore
import android.widget.Toast

// 💬 Komunikator Velivo - osobna aplikacja. Gdy jest w telefonie, to ona jest jedynym komunikatorem: przeglądarka i klawiatura
// Velivo oddają jej kontakty, rozmowy i identyfikator (te same kontakty dalej nas znają) i otwierają ją przyciskiem 💬.
// W sieci domowej własny port (41918), poza domem - przez domowego pośrednika, jak dotąd.
class Start : Activity() {
    private lateinit var k: Komunikator
    private lateinit var okno: KomunikatorOkno

    override fun onCreate(b: Bundle?) {
        super.onCreate(b)
        // 🐞 po awarii: najpierw raport, zanim wystartuje komunikator i jego sieć (gdyby to one padały - raport i tak dojdzie przez Udostępnij)
        val f = java.io.File(filesDir, "blad.txt")
        val crash = runCatching { f.readText() }.getOrDefault(""); f.delete()
        if (crash.isNotBlank()) report(crash, false) { begin() } else begin()
    }
    private fun begin() {
        k = Komunikator.get(this) { (Build.MANUFACTURER + " " + Build.MODEL).trim() }
        k.saveFile = { name, tmp, dev -> save(name, tmp, dev) }
        k.openIntent = { id -> Intent(applicationContext, Start::class.java).putExtra("czat", id).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP) }
        okno = KomunikatorOkno(this, k) { pick() }
        okno.onClose = { if (!picking) moveTaskToBack(true) }
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 2)
        Thread { takeOver(); runOnUiThread { handle(intent); lastAnr() } }.start()
        network()
    }

    // 🐞 zawieszenie („nie odpowiada”) zapisuje Android (od 11): stos głównego wątku - raport przy następnym otwarciu
    private fun lastAnr() {
        if (Build.VERSION.SDK_INT < 30) return
        runCatching {
            val p = getSharedPreferences("komunikator", MODE_PRIVATE); val seen = p.getLong("raport-ts", 0)
            val x = getSystemService(android.app.ActivityManager::class.java)?.getHistoricalProcessExitReasons(null, 0, 1)?.firstOrNull() ?: return
            if (x.timestamp <= seen) return
            p.edit().putLong("raport-ts", x.timestamp).apply()
            if (x.reason != android.app.ApplicationExitInfo.REASON_ANR) return
            val tr = x.traceInputStream?.bufferedReader()?.use { it.readText() } ?: ""
            report(Komunikator.crashHeader(this) + "zawieszenie " + java.text.SimpleDateFormat("dd.MM HH:mm", java.util.Locale.ROOT).format(java.util.Date(x.timestamp)) + ": " + (x.description ?: "") +
                "\n\"main\"" + tr.substringAfter("\"main\"", "").lines().take(30).joinToString("\n"), true) {}
        }
    }
    // raport: Udostępnij (WhatsApp, SMS - bez sieci Velivo) albo, gdy komunikator działa, wiadomością do kontaktu
    private fun report(text: String, running: Boolean, then: () -> Unit) {
        val pl = java.util.Locale.getDefault().language == "pl"
        val d = android.app.AlertDialog.Builder(this).setTitle("🐞 " + if (pl) "Komunikator zamknął się nieoczekiwanie" else "The messenger closed unexpectedly")
            .setMessage((if (pl) "Wyślij raport synowi / opiekunowi – pomoże znaleźć przyczynę.\n\n" else "Send the report – it helps find the cause.\n\n") + text.lines().take(6).joinToString("\n"))
            .setNegativeButton(android.R.string.cancel, null)
            .setNeutralButton("📤 " + if (pl) "Udostępnij" else "Share") { _, _ -> runCatching { startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, "🐞 " + text), null)) } }
            .setOnDismissListener { then() }
        val cs = if (running) k.contacts.filter { !it.isGroup } else emptyList()
        if (cs.isNotEmpty()) d.setPositiveButton("💬 " + if (pl) "Wyślij do…" else "Send to…") { _, _ ->
            android.app.AlertDialog.Builder(this).setItems(cs.map { it.name }.toTypedArray()) { _, i -> k.send(cs[i], "🐞 " + text.take(3900)) }.show()
        }
        d.show()
    }
    private val ready get() = ::okno.isInitialized
    private fun network() { if (!ready) return; KomunikatorUsluga.stop(this, k); k.listen(); k.wake() }

    // kontakty i rozmowy z przeglądarki i klawiatury Velivo (po rozdzieleniu - raz; potem one już nic nie mają)
    private fun takeOver() {
        for (a in listOf("pl.velivo.browser.komunikator", "pl.velivo.klawiatura.komunikator")) runCatching {
            val u = Uri.parse("content://$a")
            val json = contentResolver.call(u, "oddaj", null, null)?.getString("json") ?: return@runCatching
            k.import(json)
            contentResolver.call(u, "przekazano", null, null)
        }
    }

    override fun onNewIntent(i: Intent) { super.onNewIntent(i); setIntent(i); if (ready) handle(i) }

    private fun handle(i: Intent?) {
        k.listenError?.let { Toast.makeText(this, getString(R.string.port_busy, it), Toast.LENGTH_LONG).show() }
        val shared = when (i?.action) {   // Udostępnij → Komunikator Velivo
            Intent.ACTION_SEND -> listOfNotNull(if (Build.VERSION.SDK_INT >= 33) i.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java) else @Suppress("DEPRECATION") i.getParcelableExtra(Intent.EXTRA_STREAM))
            Intent.ACTION_SEND_MULTIPLE -> (if (Build.VERSION.SDK_INT >= 33) i.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java) else @Suppress("DEPRECATION") i.getParcelableArrayListExtra(Intent.EXTRA_STREAM)) ?: emptyList()
            else -> emptyList()
        }
        val id = i?.getStringExtra("czat")
        when {
            shared.isNotEmpty() -> okno.sendFiles(shared)
            i?.action == ACTION_FILES -> pick()
            else -> id?.let { k.contact(it) }?.let { okno.openChat(it) } ?: okno.show()
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
        if (!ready) return
        if (requestCode == Komunikator.REQ_SCREEN) { okno.screenResult(resultCode, data); return }   // 🖥 ekran w rozmowie
        if (requestCode == Komunikator.REQ_PHOTO) { okno.cameraResult(resultCode == RESULT_OK); return }
        if (requestCode == Komunikator.REQ_BACKUP) { okno.backupResult(data?.data.takeIf { resultCode == RESULT_OK }); return }   // 💾 kopia
        if (resultCode != RESULT_OK || data == null) { okno.attachTo = null; okno.pickingPhoto = false; okno.pickingBackup = false; okno.show(); return }
        val l = ArrayList<Uri>(); data.clipData?.let { c -> for (n in 0 until c.itemCount) l.add(c.getItemAt(n).uri) } ?: data.data?.let { l.add(it) }
        okno.sendFiles(l)   // pliki, 🎬 nagranie z aparatu albo 👤 zdjęcie profilowe
    }

    // odebrany plik do Pobranych (MediaStore - bez uprawnień)
    private fun save(name: String, tmp: java.io.File, dev: String) {
        val ok = runCatching {
            val v = ContentValues().apply { put(MediaStore.Downloads.DISPLAY_NAME, name); put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS) }
            val uri = contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, v)!!
            contentResolver.openOutputStream(uri)!!.use { o -> tmp.inputStream().use { it.copyTo(o, 1 shl 16) } }
        }.isSuccess
        tmp.delete()
        runOnUiThread { Toast.makeText(this, getString(if (ok) R.string.saved else R.string.save_failed, name, dev), Toast.LENGTH_LONG).show() }
    }

    // na ekranie - gniazda tutaj; schowana (także aparat, wybór pliku) - czeka usługa w tle (powiadomienia o wiadomościach)
    override fun onStart() { super.onStart(); network() }
    override fun onStop() { if (ready && !isChangingConfigurations) { k.close(); KomunikatorUsluga.start(this, k) }; super.onStop() }

    companion object { const val ACTION_FILES = "pl.velivo.komunikator.PLIKI" }
}
