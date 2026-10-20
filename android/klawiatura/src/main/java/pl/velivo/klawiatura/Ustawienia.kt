package pl.velivo.klawiatura

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.provider.Settings
import android.text.InputType
import android.view.Gravity
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast

// Ustawienia klawiatury Velivo: włączenie, wybór, mikrofon do dyktowania, motyw, Mój słownik.
open class Ustawienia : Activity() {
    companion object { const val ACTION_THEME = "pl.velivo.klawiatura.MOTYW"; const val ACTION_HEIGHT = "pl.velivo.klawiatura.WYSOKOSC" }
    private val REQ_MIC = 1
    // z klawiatury (motyw, wysokość, słownik, mikrofon): samo okienko nad pisaniem, bez ekranu ustawień pod spodem; po wyborze powrót do pisania
    protected open val quick = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Rozmowy.syncIcon(this)
        if (quick) { if (!runAction(intent)) finish(); return }
        val dp = resources.displayMetrics.density
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL; setPadding((20 * dp).toInt(), (28 * dp).toInt(), (20 * dp).toInt(), (20 * dp).toInt())
            background = GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(Color.rgb(0x1E, 0x18, 0x38), Color.rgb(0x19, 0x1B, 0x33), Color.rgb(0x13, 0x2A, 0x35)))
        }
        box.addView(TextView(this).apply { text = getString(R.string.kb_name); textSize = 26f; setTextColor(Color.WHITE); setPadding(0, 0, 0, (8 * dp).toInt()) })
        box.addView(TextView(this).apply { text = getString(R.string.kb_help); textSize = 15f; setTextColor(Color.argb(0xD0, 255, 255, 255)); setPadding(0, 0, 0, (16 * dp).toInt()) })
        fun button(label: Int, tip: Int, act: () -> Unit) = box.addView(Button(this).apply {
            text = getString(label); isAllCaps = false; textSize = 17f; setTextColor(Color.WHITE)
            background = GradientDrawable(GradientDrawable.Orientation.LEFT_RIGHT, intArrayOf(Color.rgb(0x7C, 0x3A, 0xED), Color.rgb(0x06, 0xB6, 0xD4))).apply { cornerRadius = 12 * dp }
            contentDescription = getString(tip); tooltipText = getString(tip)
            setOnClickListener { act() }
        }, LinearLayout.LayoutParams(-1, (52 * dp).toInt()).apply { bottomMargin = (10 * dp).toInt() })
        button(R.string.kb_enable, R.string.kb_enable_tip) { runCatching { startActivity(Intent(Settings.ACTION_INPUT_METHOD_SETTINGS)) } }
        button(R.string.kb_choose, R.string.kb_choose_tip) { (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).showInputMethodPicker() }
        button(R.string.kb_mic_allow, R.string.kb_mic) { requestPermissions(arrayOf(android.Manifest.permission.RECORD_AUDIO), REQ_MIC) }
        button(R.string.kb_theme, R.string.kb_theme_tip) { chooseTheme() }
        button(R.string.kb_height, R.string.kb_height_tip) { chooseHeight() }
        button(R.string.kb_words, R.string.kb_words) { showWords() }
        // 📤 pliki na komputer - przez przeglądarkę Velivo (parowanie i szyfrowanie są tam)
        button(R.string.kb_m_send, R.string.kb_m_send_tip) { runCatching { startActivity(Intent("pl.velivo.browser.WYSLIJ_NA_KOMPUTER").setClassName("pl.velivo.browser", "pl.velivo.browser.MainActivity").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
            .onFailure { Toast.makeText(this, R.string.kb_m_no_velivo, Toast.LENGTH_LONG).show() } }
        button(R.string.kb_m_chat, R.string.kb_m_chat_tip) { Rozmowy.open(this, false) }
        button(R.string.kb_m_phone, R.string.kb_m_phone_tip) { Rozmowy.open(this, true) }
        setContentView(ScrollView(this).apply { isFillViewport = true; addView(box) })
        runAction(intent)
    }

    private fun runAction(i: Intent?): Boolean {
        when (i?.action) {
            KlawiaturaVelivo.ACTION_MIC -> requestPermissions(arrayOf(android.Manifest.permission.RECORD_AUDIO), REQ_MIC)
            KlawiaturaVelivo.ACTION_WORDS -> showWords()
            ACTION_THEME -> chooseTheme()
            ACTION_HEIGHT -> chooseHeight()
            else -> return false
        }
        return true
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        runAction(intent)
    }

    private fun done() { if (quick) finish() }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQ_MIC && grantResults.any { it == android.content.pm.PackageManager.PERMISSION_GRANTED }) Toast.makeText(this, R.string.kb_mic_ok, Toast.LENGTH_LONG).show()
        done()
    }

    // motyw jak w Velivo: ciemny (domyślnie), jasny, czarno-biały, jak w telefonie
    private fun chooseTheme() {
        val pr = KlawiaturaVelivo.prefs(this)
        val names = arrayOf(getString(R.string.kb_theme_system), getString(R.string.kb_theme_light), getString(R.string.kb_theme_dark), getString(R.string.kb_theme_contrast))
        AlertDialog.Builder(this).setTitle(R.string.kb_theme).setSingleChoiceItems(names, pr.getInt("motyw", 2)) { d, i -> pr.edit().putInt("motyw", i).apply(); d.dismiss() }.setOnDismissListener { done() }.show()
    }

    // wysokość klawiszy (procent) - na grubsze palce wyższa
    private fun chooseHeight() {
        val pr = KlawiaturaVelivo.prefs(this)
        val sizes = intArrayOf(90, 100, 110, 120, 135, 150, 170)
        val names = sizes.map { "$it %" }.toTypedArray()
        AlertDialog.Builder(this).setTitle(R.string.kb_height).setSingleChoiceItems(names, sizes.indexOf(pr.getInt("wysokosc", 120)).coerceAtLeast(0)) { d, i ->
            pr.edit().putInt("wysokosc", sizes[i]).apply(); d.dismiss()
        }.setOnDismissListener { done() }.show()
    }

    // Mój słownik: jedno słowo w linii (dopisywanie hurtem, usuwanie przez skasowanie linii)
    private fun showWords() {
        KlawiaturaVelivo.load(this) {
            val box = EditText(this).apply { setText(KlawiaturaVelivo.ownWords().joinToString("\n")); minLines = 8; gravity = Gravity.TOP
                inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS }
            AlertDialog.Builder(this).setTitle("📖 " + getString(R.string.kb_words)).setMessage(R.string.kb_words_help)
                .setView(ScrollView(this).apply { addView(box) })
                .setPositiveButton(android.R.string.ok) { _, _ -> KlawiaturaVelivo.setOwnWords(this, box.text.split('\n')) }
                .setNegativeButton(android.R.string.cancel, null).setOnDismissListener { done() }.show()
        }
    }
}

// to samo okienko co w ustawieniach, ale przezroczyste - otwierane z klawiatury, zamyka się po wyborze
class SzybkieUstawienia : Ustawienia() { override val quick = true }
