package pl.velivo.klawiatura

import android.app.Activity
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.os.Bundle
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.TextView
import pl.velivo.komunikator.Palce

// 🤟 nauka alfabetu palcowego (międzynarodowego): Poznawanie - znak i litera; Ćwiczenie - znak i 4 litery do wyboru; Słowo - całe słowo w rzędzie dłoni
// i odtwarzanie litera po literze. Postępy tylko w telefonie. Rysunki te same co w klawiaturze 🤟 i komunikatorze.
class NaukaPalcow : Activity() {
    private val pr by lazy { KlawiaturaVelivo.prefs(this) }
    private val abc = ('a'..'z').toList()
    private var mode = 0; private var i = 0; private var want = 'a'; private var word = ""; private var wi = 0
    private lateinit var hand: Palce.Dlon; private lateinit var info: TextView; private lateinit var choices: LinearLayout
    private lateinit var row: Rzad; private lateinit var wordBox: LinearLayout; private lateinit var nav: LinearLayout

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val dp = resources.displayMetrics.density
        fun px(v: Int) = (v * dp).toInt()
        val pl = KlawiaturaVelivo.lang(this) == "pl"
        fun t(p: String, e: String) = if (pl) p else e
        fun btn(l: String, act: () -> Unit) = Button(this).apply { text = l; isAllCaps = false; textSize = 18f; setOnClickListener { act() } }
        hand = Palce.Dlon(this)
        info = TextView(this).apply { textSize = 18f; setTextColor(Color.WHITE); gravity = Gravity.CENTER; setPadding(px(8), px(6), px(8), px(6)) }
        choices = LinearLayout(this)
        nav = LinearLayout(this).apply {
            addView(btn("◀") { step(-1) }, LinearLayout.LayoutParams(0, -2, 1f)); addView(btn("▶") { step(1) }, LinearLayout.LayoutParams(0, -2, 1f)) }
        row = Rzad(this)
        val input = EditText(this).apply { hint = t("Wpisz słowo, np. imię", "Type a word, e.g. a name"); setTextColor(Color.WHITE); setHintTextColor(Color.GRAY); textSize = 20f
            inputType = InputType.TYPE_CLASS_TEXT; setSingleLine(true) }
        wordBox = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL
            addView(LinearLayout(this@NaukaPalcow).apply { addView(input, LinearLayout.LayoutParams(0, -2, 1f))
                addView(btn("🤟 " + t("Pokaż", "Show")) { word = input.text.toString().filter { Palce.has(it) }; wi = 0; show() }) })
            addView(HorizontalScrollView(this@NaukaPalcow).apply { addView(row, LinearLayout.LayoutParams(-2, px(150))) }, LinearLayout.LayoutParams(-1, px(150)))
            addView(btn("▶ " + t("Litera po literze", "Letter by letter")) { if (word.isNotEmpty()) { wi = 0; play() } }) }
        val tabs = LinearLayout(this).apply {
            listOf(t("Poznawanie", "Explore"), t("Ćwiczenie", "Practice"), t("Słowo", "Word")).forEachIndexed { k, l -> addView(btn(l) { mode = k; if (k == 1) pick(); show() }, LinearLayout.LayoutParams(0, -2, 1f)) } }
        setContentView(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL; setBackgroundColor(Color.rgb(0x1E, 0x18, 0x38)); setPadding(px(8), px(8), px(8), px(8))
            addView(TextView(context).apply { text = t("🤟 Nauka alfabetu palcowego", "🤟 Learn fingerspelling"); textSize = 22f; setTextColor(Color.WHITE) })
            addView(tabs); addView(hand, LinearLayout.LayoutParams(-1, 0, 1f)); addView(info); addView(choices); addView(nav); addView(wordBox)
        })
        show()
    }

    private fun known(): MutableMap<Char, Int> = pr.getString("palce-nauka", "").orEmpty().split(',').mapNotNull { e -> e.split(':').takeIf { it.size == 2 && it[0].length == 1 }?.let { it[0][0] to (it[1].toIntOrNull() ?: 0) } }.toMap().toMutableMap()
    private fun step(d: Int) { i = (i + d + abc.size) % abc.size; show() }
    private fun pick() { val k = known(); want = abc.minByOrNull { (k[it] ?: 0) * 10 + (Math.random() * 10).toInt() } ?: 'a' }
    private fun play() { if (wi < word.length && mode == 2) { hand.set(word[wi], null, word[wi].uppercase()); row.mark = wi; row.invalidate(); wi++; hand.postDelayed({ play() }, 1200) } }

    private fun show() {
        val pl = KlawiaturaVelivo.lang(this) == "pl"
        nav.visibility = if (mode == 0) View.VISIBLE else View.GONE; choices.visibility = if (mode == 1) View.VISIBLE else View.GONE; wordBox.visibility = if (mode == 2) View.VISIBLE else View.GONE
        when (mode) {
            0 -> { hand.set(abc[i], null, abc[i].uppercase()); info.text = abc[i].uppercase() }
            1 -> {
                hand.set(want, null, "?"); val k = known()
                info.text = (if (pl) "Jaka to litera? Opanowane: " else "Which letter? Learned: ") + abc.count { (k[it] ?: 0) >= 3 } + " / " + abc.size
                choices.removeAllViews()
                (abc.filter { it != want }.shuffled().take(3) + want).shuffled().forEach { c -> choices.addView(Button(this).apply { text = c.uppercase(); textSize = 22f
                    setOnClickListener {
                        val kk = known(); val ok = c == want; kk[want] = if (ok) (kk[want] ?: 0) + 1 else maxOf(0, (kk[want] ?: 0) - 1)
                        pr.edit().putString("palce-nauka", kk.entries.joinToString(",") { "${it.key}:${it.value}" }).apply()
                        info.text = if (ok) (if (pl) "Dobrze! " else "Correct! ") + want.uppercase() else (if (pl) "To było: " else "It was: ") + want.uppercase()
                        hand.set(want, null, want.uppercase()); hand.postDelayed({ pick(); show() }, if (ok) 900L else 1800L)
                    } }, LinearLayout.LayoutParams(0, -2, 1f)) }
            }
            else -> { row.word = word; row.mark = -1; row.requestLayout(); row.invalidate(); if (word.isNotEmpty()) hand.set(word[0], null, word[0].uppercase()) else hand.set('a', null, ""); info.text = word.uppercase() }
        }
    }

    // całe słowo: dłonie obok siebie, litery pod spodem; podświetlona litera przy odtwarzaniu
    class Rzad(ctx: Context) : View(ctx) {
        var word = ""; var mark = -1
        private val dp = resources.displayMetrics.density
        private val tp = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE; textAlign = Paint.Align.CENTER; textSize = 18 * dp }
        private val hi = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.argb(0x60, 0x22, 0xD3, 0xEE) }
        override fun onMeasure(w: Int, h: Int) = setMeasuredDimension(maxOf(MeasureSpec.getSize(w), (word.length * 110 * dp).toInt()), (150 * dp).toInt())
        override fun onDraw(c: Canvas) {
            val cw = 110 * dp
            word.forEachIndexed { k, ch -> val cx = k * cw + cw / 2
                if (k == mark) c.drawRoundRect(k * cw + 4 * dp, 0f, (k + 1) * cw - 4 * dp, height.toFloat(), 12 * dp, 12 * dp, hi)
                Palce.draw(c, ch, cx, height * 0.42f, cw / 11f); c.drawText(ch.uppercase(), cx, height - 8 * dp, tp) }
        }
    }
}
