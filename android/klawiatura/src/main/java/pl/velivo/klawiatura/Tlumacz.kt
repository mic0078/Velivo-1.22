package pl.velivo.klawiatura

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.widget.Toast

// 🌍 tłumaczenie bez serwerów Velivo: tekst idzie do aplikacji tłumacza w telefonie, którą wybierzesz (np. Tłumacz Google -
// z pobranymi językami działa bez internetu); przetłumaczony wraca do pola. Velivo nic nie zapisuje.
class Tlumacz : Activity() {
    companion object { var done: ((String) -> Unit)? = null }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (savedInstanceState != null) return
        val text = intent.getStringExtra("t").orEmpty()
        val base = Intent(Intent.ACTION_PROCESS_TEXT).setType("text/plain")
        val apps = packageManager.queryIntentActivities(base, 0).filter { it.activityInfo.packageName != packageName }
        if (text.isBlank() || apps.isEmpty()) { Toast.makeText(this, R.string.kb_tr_none, Toast.LENGTH_LONG).show(); finish(); return }
        // zgoda: wiadomo, do której aplikacji trafi tekst
        AlertDialog.Builder(this).setTitle(R.string.kb_tr_title).setMessage(getString(R.string.kb_tr_where))
            .setItems(apps.map { it.loadLabel(packageManager).toString() + " – " + it.activityInfo.applicationInfo.loadLabel(packageManager) }.toTypedArray()) { _, i ->
                val a = apps[i].activityInfo
                runCatching { startActivityForResult(Intent(base).setClassName(a.packageName, a.name).putExtra(Intent.EXTRA_PROCESS_TEXT, text).putExtra(Intent.EXTRA_PROCESS_TEXT_READONLY, false), 1) }
                    .onFailure { finish() }
            }.setOnCancelListener { finish() }.show()
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        val t = data?.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString()
        val cb = done; done = null
        finish()
        if (resultCode == RESULT_OK && !t.isNullOrEmpty() && cb != null) Handler(Looper.getMainLooper()).postDelayed({ cb(t) }, 400)   // po powrocie pola do klawiatury
    }
}
