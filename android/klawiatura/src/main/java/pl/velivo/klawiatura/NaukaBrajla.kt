package pl.velivo.klawiatura

import android.app.Activity
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.os.Bundle
import android.speech.tts.TextToSpeech
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView
import java.util.Locale

// ⠿ nauka brajla: „Poznawanie” - znak, litera i wymowa (▶ ◀ albo przesunięcie na polu); „Ćwiczenie” - Velivo mówi literę, piszesz ją na polu 6 punktów.
// Postępy tylko w telefonie (można wyłączyć). Te same tablice i to samo pole co przy pisaniu brajlem w klawiaturze.
class NaukaBrajla : Activity() {
    private val pr by lazy { KlawiaturaVelivo.prefs(this) }
    private val pl by lazy { KlawiaturaVelivo.lang(this) == "pl" }
    private val abc by lazy { Brajl.alphabet(pl) }
    private var i = 0; private var test = false; private var want = 'a'
    private var tts: TextToSpeech? = null
    private lateinit var cell: Komorka; private lateinit var info: TextView; private lateinit var score: TextView; private lateinit var mode: Button

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val dp = resources.displayMetrics.density
        fun px(v: Int) = (v * dp).toInt()
        fun btn(t: String, act: () -> Unit) = Button(this).apply { text = t; isAllCaps = false; textSize = 16f; contentDescription = t; setOnClickListener { act() } }
        cell = Komorka(this)
        info = TextView(this).apply { textSize = 20f; setTextColor(Color.WHITE); gravity = Gravity.CENTER; setPadding(px(8), px(4), px(8), px(4)) }
        score = TextView(this).apply { textSize = 14f; setTextColor(Color.argb(0xC0, 255, 255, 255)); gravity = Gravity.CENTER }
        mode = btn("") { test = !test; show() }
        val row = LinearLayout(this).apply {
            addView(btn("◀ " + getString(R.string.kb_bl_prev)) { step(-1) }, LinearLayout.LayoutParams(0, -2, 1f))
            addView(btn("🔊 " + getString(R.string.kb_bl_repeat)) { show() }, LinearLayout.LayoutParams(0, -2, 1f))
            addView(btn(getString(R.string.kb_bl_next) + " ▶") { step(1) }, LinearLayout.LayoutParams(0, -2, 1f))
        }
        val pole = BrajlPole(this, ::onCell) { g -> when (g) { "space" -> step(1); "bs" -> step(-1); "read" -> show(); "enter" -> { test = !test; show() }; "exit" -> exit() } }.apply {
            hints = pr.getBoolean("brajl-podpowiedzi", true); vibrate = pr.getBoolean("brajl-wibracje", true); mirror = pr.getBoolean("brajl-odwrocone", false)
            title = getString(R.string.kb_bl_help)
        }
        setContentView(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL; setBackgroundColor(Color.rgb(0x1E, 0x18, 0x38)); setPadding(px(8), px(8), px(8), px(8))
            addView(TextView(context).apply { text = getString(R.string.kb_braille_learn); textSize = 22f; setTextColor(Color.WHITE) })
            addView(cell, LinearLayout.LayoutParams(-1, px(150))); addView(info); addView(score); addView(row); addView(mode)
            addView(pole, LinearLayout.LayoutParams(-1, 0, 1f))
        })
        tts = TextToSpeech(this) { ok -> if (ok == TextToSpeech.SUCCESS) { tts?.language = if (pl) Locale("pl", "PL") else Locale.ENGLISH; show() } }
        show()
    }
    override fun onDestroy() { tts?.shutdown(); super.onDestroy() }

    // wyjście dwoma palcami w dół - dopiero po powtórzeniu (przy kładzeniu palców na literę łatwo o przypadkowe przesunięcie)
    private var exitAsk = 0L
    private fun exit() { if (android.os.SystemClock.uptimeMillis() - exitAsk < 4000) finish() else { exitAsk = android.os.SystemClock.uptimeMillis(); say(getString(R.string.kb_braille_exit_again)) } }
    private fun say(t: String) { tts?.speak(t, TextToSpeech.QUEUE_FLUSH, null, "nauka") }
    private fun dots(c: Char) = Brajl.dotsOf(c, pl) ?: 0

    // postępy: ile razy dobrze dla każdej litery (opanowana: 3 razy) - tylko w telefonie, gdy zapisywanie włączone
    private fun known(): MutableMap<Char, Int> = pr.getString("brajl-nauka", "").orEmpty().split(',').mapNotNull { e -> e.split(':').takeIf { it.size == 2 && it[0].length == 1 }?.let { it[0][0] to (it[1].toIntOrNull() ?: 0) } }.toMap().toMutableMap()
    private fun saveKnown(k: Map<Char, Int>) { if (pr.getBoolean("brajl-postepy", true)) pr.edit().putString("brajl-nauka", k.entries.joinToString(",") { "${it.key}:${it.value}" }).apply() }

    private fun step(d: Int) { if (test) { pick(); show() } else { i = (i + d + abc.size) % abc.size; show() } }
    private fun pick() { val k = known(); want = abc.minByOrNull { (k[it] ?: 0) * 10 + (Math.random() * 10).toInt() } ?: 'a' }

    private fun show() {
        mode.text = if (test) getString(R.string.kb_bl_mode_learn) else getString(R.string.kb_bl_mode_test)
        val k = known(); score.text = getString(R.string.kb_bl_score, abc.count { (k[it] ?: 0) >= 3 }, abc.size)
        if (test) { cell.set(0, "?"); info.text = getString(R.string.kb_bl_write, want.toString()); say(info.text.toString()); return }
        val c = abc[i]; cell.set(dots(c), c.toString())
        info.text = getString(R.string.kb_bl_letter, c.toString(), Brajl.dotsText(dots(c))); say(info.text.toString())
    }

    private fun onCell(mask: Int) {
        val got = Brajl.decode(mask, pl, Brajl.Stan()).orEmpty()
        if (!test) {   // poznawanie: każdy znak pokazany i powiedziany
            cell.set(mask, got.ifEmpty { "?" }); info.text = (got.ifEmpty { getString(R.string.kb_bl_nothing) }) + " – " + Brajl.dotsText(mask); say(Brajl.spoken(got, pl).ifEmpty { getString(R.string.kb_bl_nothing) } + ", " + Brajl.dotsText(mask)); return
        }
        val k = known()
        if (mask == dots(want)) { k[want] = (k[want] ?: 0) + 1; saveKnown(k); say(getString(R.string.kb_bl_good)); cell.set(mask, want.toString()); cell.postDelayed({ pick(); show() }, 900) }
        else { k[want] = maxOf(0, (k[want] ?: 0) - 1); saveKnown(k); cell.set(dots(want), want.toString())
            info.text = getString(R.string.kb_bl_wrong, got.ifEmpty { getString(R.string.kb_bl_nothing) }, want.toString(), Brajl.dotsText(dots(want))); say(info.text.toString()) }
    }

    // duży znak brajla: 6 punktów (pełne - wypukłe) i litera obok
    class Komorka(ctx: Context) : View(ctx) {
        private var mask = 0; private var text = ""
        private val p = Paint(Paint.ANTI_ALIAS_FLAG); private val tp = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE; textAlign = Paint.Align.CENTER }
        fun set(m: Int, t: String) { mask = m; text = t; contentDescription = t + ", " + Brajl.dotsText(m); invalidate() }
        override fun onDraw(c: Canvas) {
            val r = height / 8f; val cx = width * 0.3f; val cy = height / 2f
            for (d in 0 until 6) {
                val x = cx + (if (d < 3) -1 else 1) * r * 1.6f; val y = cy + (d % 3 - 1) * r * 2.6f
                p.style = if (mask and (1 shl d) != 0) Paint.Style.FILL else Paint.Style.STROKE; p.strokeWidth = r / 4
                p.color = if (mask and (1 shl d) != 0) Color.rgb(0x22, 0xC5, 0x5E) else Color.argb(0x90, 255, 255, 255)
                c.drawCircle(x, y, r, p)
            }
            tp.textSize = height * 0.55f; c.drawText(text, width * 0.72f, cy + tp.textSize / 3, tp)
        }
    }
}
