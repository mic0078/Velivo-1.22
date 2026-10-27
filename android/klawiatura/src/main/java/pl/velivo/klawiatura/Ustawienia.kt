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
    companion object { const val ACTION_ALL = "pl.velivo.klawiatura.WSZYSTKIE"; const val ACTION_THEME = "pl.velivo.klawiatura.MOTYW"; const val ACTION_HEIGHT = "pl.velivo.klawiatura.WYSOKOSC"; const val ACTION_GIF = "pl.velivo.klawiatura.GIF" }
    private val REQ_MIC = 1; private val REQ_GIF = 2
    private fun askGif() = requestPermissions(arrayOf(if (android.os.Build.VERSION.SDK_INT >= 33) android.Manifest.permission.READ_MEDIA_IMAGES else android.Manifest.permission.READ_EXTERNAL_STORAGE), REQ_GIF)
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
        // 📖 słownik i nauka - jedno miejsce (zamiast wielu przycisków)
        button(R.string.kb_dict_learn, R.string.kb_dict_learn_tip) {
            list(R.string.kb_dict_learn, listOf(getString(R.string.kb_words) to { showWords() }, getString(R.string.kb_phrases) to { showPhrases() }, getString(R.string.kb_privacy) to { privacy() },
                getString(R.string.kb_en_variant) to { enVariant() }, getString(R.string.kb_clear_learned) to { clearLearned() }))
        }
        // ♿ dostępność - jedno miejsce: Senior, powiększona litera, brajl, Morse, dźwięki zamiast słów
        button(R.string.kb_m_access, R.string.kb_access_tip) { access() }
        button(R.string.kb_mic_allow, R.string.kb_mic) { requestPermissions(arrayOf(android.Manifest.permission.RECORD_AUDIO), REQ_MIC) }
        button(R.string.kb_theme, R.string.kb_theme_tip) { chooseTheme() }
        button(R.string.kb_height, R.string.kb_height_tip) { chooseHeight() }
        button(R.string.kb_gif_allow, R.string.kb_gif_tip) { askGif() }
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
            ACTION_GIF -> askGif()
            ACTION_ALL -> all()
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
        if (requestCode == REQ_GIF && grantResults.any { it == android.content.pm.PackageManager.PERMISSION_GRANTED }) Toast.makeText(this, R.string.kb_gif_ok, Toast.LENGTH_LONG).show()
        done()
    }

    // motyw jak w Velivo: ciemny (domyślnie), jasny, czarno-biały, jak w telefonie
    private fun chooseTheme() {
        val pr = KlawiaturaVelivo.prefs(this)
        val names = arrayOf(getString(R.string.kb_theme_system), getString(R.string.kb_theme_light), getString(R.string.kb_theme_dark), getString(R.string.kb_theme_contrast))
        AlertDialog.Builder(this).setTitle(R.string.kb_theme).setSingleChoiceItems(names, pr.getInt("motyw", 2)) { d, i -> pr.edit().putInt("motyw", i).apply(); d.dismiss() }.setOnDismissListener { done() }.show().also { x(it) }
    }

    // wysokość klawiszy (procent) - na grubsze palce wyższa
    private fun chooseHeight() {
        val pr = KlawiaturaVelivo.prefs(this)
        val sizes = intArrayOf(90, 100, 110, 120, 135, 150, 170)
        val names = sizes.map { "$it %" }.toTypedArray()
        AlertDialog.Builder(this).setTitle(R.string.kb_height).setSingleChoiceItems(names, sizes.indexOf(pr.getInt("wysokosc", 120)).coerceAtLeast(0)) { d, i ->
            pr.edit().putInt("wysokosc", sizes[i]).apply(); d.dismiss()
        }.setOnDismissListener { done() }.show().also { x(it) }
    }

    // 💬 Moje frazy: wyuczone (napisane co najmniej 2 razy) i własne - przegląd, edycja, usuwanie; „skrót = fraza”
    private fun showPhrases() {
        KlawiaturaVelivo.load(this) {
            val box = EditText(this).apply { setText(KlawiaturaVelivo.phrases().joinToString("\n")); minLines = 8; gravity = Gravity.TOP
                inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS }
            AlertDialog.Builder(this).setTitle(R.string.kb_phrases).setMessage(R.string.kb_phrases_help)
                .setView(ScrollView(this).apply { addView(box) })
                .setPositiveButton(android.R.string.ok) { _, _ -> KlawiaturaVelivo.setPhrases(this, box.text.split('\n')) }
                .setNegativeButton(android.R.string.cancel, null).setOnDismissListener { done() }.show().also { x(it) }
        }
    }

    // 🔒 co klawiatura zapamiętuje (wszystko tylko w telefonie, zaszyfrowane): nauka, frazy, dopasowanie do tempa, nauka tylko w Velivo; profil pisania od nowa
    private fun privacy() {
        val pr = KlawiaturaVelivo.prefs(this)
        val keys = arrayOf("klaw-nauka", "klaw-frazy", "klaw-rytm", "klaw-nauka-velivo"); val defs = booleanArrayOf(true, true, true, false)
        val on = BooleanArray(keys.size) { pr.getBoolean(keys[it], defs[it]) }
        AlertDialog.Builder(this).setTitle(R.string.kb_privacy)
            .setMultiChoiceItems(arrayOf(getString(R.string.kb_p_learn), getString(R.string.kb_p_phrases), getString(R.string.kb_p_rhythm), getString(R.string.kb_p_velivo)), on) { _, i, v -> on[i] = v }
            .setPositiveButton(android.R.string.ok) { _, _ -> val e = pr.edit(); keys.forEachIndexed { i, k -> e.putBoolean(k, on[i]) }; e.apply(); if (!on[2]) KlawiaturaVelivo.resetRhythm(this) }
            .setNeutralButton(R.string.kb_p_reset) { _, _ -> KlawiaturaVelivo.resetRhythm(this); Toast.makeText(this, R.string.kb_p_reset_done, Toast.LENGTH_LONG).show() }
            .setNegativeButton(android.R.string.cancel, null).show().also { x(it) }
    }

    // 🗣 dźwięki zamiast słów: włączenie i nauka własnego „aaaa” (zatwierdź) i „uuuu” (usuń słowo) - 3 s każde
    private fun sounds() {
        val pr = KlawiaturaVelivo.prefs(this)
        if (checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) != android.content.pm.PackageManager.PERMISSION_GRANTED) { requestPermissions(arrayOf(android.Manifest.permission.RECORD_AUDIO), REQ_MIC); return }
        val on = pr.getBoolean("klaw-dzwiek", false)
        fun teach(key: String, say: String) {
            Toast.makeText(this, getString(R.string.kb_sound_say, say), Toast.LENGTH_LONG).show()
            Dzwiek.measure { t ->
                val other = if (key == "dzwiek-a") pr.getFloat("dzwiek-u", Dzwiek.U.toFloat()) else pr.getFloat("dzwiek-a", Dzwiek.A.toFloat())
                val msg = when {
                    t == null -> R.string.kb_sound_quiet
                    if (key == "dzwiek-a") t - other < 150 else other - t < 150 -> R.string.kb_sound_same
                    else -> { pr.edit().putFloat(key, t.toFloat()).apply(); R.string.kb_sound_ok }
                }
                Toast.makeText(this, msg, Toast.LENGTH_LONG).show()
            }
        }
        AlertDialog.Builder(this).setTitle(R.string.kb_sound).setMessage(R.string.kb_sound_tip)
            .setPositiveButton(if (on) R.string.kb_sound_off else R.string.kb_sound_on) { _, _ -> pr.edit().putBoolean("klaw-dzwiek", !on).apply() }
            .setNeutralButton(R.string.kb_sound_teach_a) { _, _ -> teach("dzwiek-a", "aaaa") }
            .setNegativeButton(R.string.kb_sound_teach_u) { _, _ -> teach("dzwiek-u", "uuuu") }.show().also { x(it) }
    }

    // ⚙ z klawiatury: wszystkie ustawienia w jednym okienku nad pisaniem; po wyborze powrót prosto do pisania
    private fun all() = list(R.string.kb_m_settings, listOf(getString(R.string.kb_dict_learn) to { list(R.string.kb_dict_learn, listOf(getString(R.string.kb_words) to { showWords() }, getString(R.string.kb_phrases) to { showPhrases() },
            getString(R.string.kb_privacy) to { privacy() }, getString(R.string.kb_en_variant) to { enVariant() }, getString(R.string.kb_clear_learned) to { clearLearned() })) },
        getString(R.string.kb_m_access) to { access() }, getString(R.string.kb_theme) to { chooseTheme() }, getString(R.string.kb_height) to { chooseHeight() },
        getString(R.string.kb_mic_allow) to { requestPermissions(arrayOf(android.Manifest.permission.RECORD_AUDIO), REQ_MIC) }, getString(R.string.kb_gif_allow) to { askGif() }))
    // okienko nad pisaniem: gdy zamknie się ostatnie okienko (nic już nie jest otwarte) - od razu z powrotem do klawiatury
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (quick && hasFocus) window.decorView.postDelayed({ if (hasWindowFocus() && !isFinishing) finish() }, 600)
    }

    // ✕ w prawym górnym rogu okienka (jak w Windows)
    private fun x(d: AlertDialog) { val dp = resources.displayMetrics.density
        (d.window?.decorView as? android.widget.FrameLayout)?.addView(TextView(this).apply { text = "✕"; textSize = 22f; setTextColor(Color.WHITE); gravity = Gravity.CENTER
            contentDescription = getString(android.R.string.cancel); setOnClickListener { d.dismiss() } }, android.widget.FrameLayout.LayoutParams((40 * dp).toInt(), (40 * dp).toInt(), Gravity.TOP or Gravity.END).apply { topMargin = (8 * dp).toInt(); rightMargin = (8 * dp).toInt() }) }
    // lista do wyboru: duże, czytelne pozycje
    private fun list(title: Int, items: List<Pair<String, () -> Unit>>) =
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert).setTitle(title).setItems(items.map { it.first }.toTypedArray()) { _, i -> items[i].second() }.setNegativeButton(android.R.string.cancel, null).show().also { x(it) }
    private fun onOff(label: Int, key: String, def: Boolean) = getString(label) + ": " + getString(if (KlawiaturaVelivo.prefs(this).getBoolean(key, def)) R.string.kb_on else R.string.kb_off)
    private fun access() {
        val pr = KlawiaturaVelivo.prefs(this)
        list(R.string.kb_m_access, listOf(
            onOff(R.string.kb_senior, "klaw-senior", false) to { KlawiaturaVelivo.senior(this, !pr.getBoolean("klaw-senior", false)); access() },
            onOff(R.string.kb_preview, "klaw-podglad", true) to { pr.edit().putBoolean("klaw-podglad", !pr.getBoolean("klaw-podglad", true)).apply(); access() },
            getString(R.string.kb_braille_learn) to { startActivity(Intent(this, NaukaBrajla::class.java)) },
            getString(R.string.kb_fingers_learn) to { startActivity(Intent(this, NaukaPalcow::class.java)) },
            getString(R.string.kb_braille_settings) to { brailleSettings() },
            getString(R.string.kb_sound) to { sounds() }))
    }
    private fun brailleSettings() {
        val pr = KlawiaturaVelivo.prefs(this)
        val keys = arrayOf("brajl-mowa", "brajl-wibracje", "brajl-podpowiedzi", "brajl-odwrocone", "brajl-postepy"); val defs = booleanArrayOf(true, true, true, false, true)
        val on = BooleanArray(keys.size) { pr.getBoolean(keys[it], defs[it]) }
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert).setTitle(R.string.kb_braille_settings)
            .setMultiChoiceItems(arrayOf(getString(R.string.kb_braille_opt_speech), getString(R.string.kb_braille_opt_vibe), getString(R.string.kb_braille_opt_hints), getString(R.string.kb_braille_opt_mirror), getString(R.string.kb_braille_opt_progress)), on) { _, i, v -> on[i] = v }
            .setPositiveButton(android.R.string.ok) { _, _ -> val e = pr.edit(); keys.forEachIndexed { i, k -> e.putBoolean(k, on[i]) }; if (!on[4]) e.remove("brajl-nauka"); e.apply() }
            .setNegativeButton(android.R.string.cancel, null).show().also { x(it) }
    }
    private fun enVariant() {
        val vals = arrayOf("", "uk", "us"); val pr = KlawiaturaVelivo.prefs(this)
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert).setTitle(R.string.kb_en_variant).setSingleChoiceItems(arrayOf(getString(R.string.kb_en_any), "🇬🇧 UK (colour, realise)", "🇺🇸 US (color, realize)"), vals.indexOf(pr.getString("klaw-en", "")).coerceAtLeast(0)) { d, i -> pr.edit().putString("klaw-en", vals[i]).apply(); d.dismiss() }.show().also { x(it) }
    }
    // 🧹 wyuczone dane (częstość, pary słów, poprawki) - za zgodą; Moje słowa zostają
    private fun clearLearned() {
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert).setTitle(R.string.kb_clear_learned).setMessage(R.string.kb_clear_learned_tip)
            .setPositiveButton(R.string.kb_clear_yes) { _, _ -> KlawiaturaVelivo.load(this) { KlawiaturaVelivo.clearLearned(this); Toast.makeText(this, R.string.kb_clear_done, Toast.LENGTH_LONG).show() } }
            .setNegativeButton(android.R.string.cancel, null).show().also { x(it) }
    }

    // Mój słownik: jedno słowo w linii (dopisywanie hurtem, usuwanie przez skasowanie linii)
    private fun showWords() {
        KlawiaturaVelivo.load(this) {
            val box = EditText(this).apply { setText(KlawiaturaVelivo.ownWords().joinToString("\n")); minLines = 8; gravity = Gravity.TOP
                inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS }
            AlertDialog.Builder(this).setTitle("📖 " + getString(R.string.kb_words)).setMessage(R.string.kb_words_help)
                .setView(ScrollView(this).apply { addView(box) })
                .setPositiveButton(android.R.string.ok) { _, _ -> KlawiaturaVelivo.setOwnWords(this, box.text.split('\n')) }
                .setNegativeButton(android.R.string.cancel, null).setOnDismissListener { done() }.show().also { x(it) }
        }
    }
}

// to samo okienko co w ustawieniach, ale przezroczyste - otwierane z klawiatury, zamyka się po wyborze
class SzybkieUstawienia : Ustawienia() { override val quick = true }
