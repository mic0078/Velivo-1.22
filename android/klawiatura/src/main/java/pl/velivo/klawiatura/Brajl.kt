package pl.velivo.klawiatura

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.os.SystemClock
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.min

// ⠿ Brajl (6 punktów): znaki polskiego brajla (litery z ą ć ę ł ń ó ś ź ż, cyfry ze znakiem liczby ⠼, interpunkcja)
// i angielskiego (UEB, stopień 1). Punkty 1-2-3 - lewa kolumna od góry, 4-5-6 - prawa; w masce punkt n = bit n-1.
object Brajl {
    private fun m(vararg d: Int) = d.fold(0) { a, x -> a or (1 shl (x - 1)) }
    private val letters = mapOf(m(1) to 'a', m(1, 2) to 'b', m(1, 4) to 'c', m(1, 4, 5) to 'd', m(1, 5) to 'e', m(1, 2, 4) to 'f', m(1, 2, 4, 5) to 'g',
        m(1, 2, 5) to 'h', m(2, 4) to 'i', m(2, 4, 5) to 'j', m(1, 3) to 'k', m(1, 2, 3) to 'l', m(1, 3, 4) to 'm', m(1, 3, 4, 5) to 'n', m(1, 3, 5) to 'o',
        m(1, 2, 3, 4) to 'p', m(1, 2, 3, 4, 5) to 'q', m(1, 2, 3, 5) to 'r', m(2, 3, 4) to 's', m(2, 3, 4, 5) to 't', m(1, 3, 6) to 'u', m(1, 2, 3, 6) to 'v',
        m(2, 4, 5, 6) to 'w', m(1, 3, 4, 6) to 'x', m(1, 3, 4, 5, 6) to 'y', m(1, 3, 5, 6) to 'z')
    private val polish = mapOf(m(1, 6) to 'ą', m(1, 4, 6) to 'ć', m(1, 5, 6) to 'ę', m(1, 2, 6) to 'ł', m(1, 4, 5, 6) to 'ń', m(3, 4, 6) to 'ó',
        m(2, 4, 6) to 'ś', m(2, 3, 4, 6) to 'ź', m(1, 2, 3, 4, 6) to 'ż')
    private val punctPl = mapOf(m(3) to '.', m(2) to ',', m(2, 6) to '?', m(2, 3, 5) to '!', m(2, 5) to ':', m(2, 3) to ';', m(3, 6) to '-', m(2, 3, 5, 6) to '(', m(2, 3, 6) to '„', m(3, 5, 6) to '”')
    private val punctEn = mapOf(m(2, 5, 6) to '.', m(2) to ',', m(2, 3, 6) to '?', m(2, 3, 5) to '!', m(2, 5) to ':', m(2, 3) to ';', m(3, 6) to '-', m(3) to '\'')
    val NUM = m(3, 4, 5, 6)                       // ⠼ znak liczby: następne a–j to cyfry 1–0
    fun capSign(pl: Boolean) = if (pl) m(4, 6) else m(6)   // ⠨ (PL) / ⠠ (EN) - wielka litera

    // stan pisania: po ⠼ cyfry (do spacji), po znaku wielkiej litery - jedna wielka litera, nawias otwarty / zamknięty (PL: ten sam znak)
    class Stan { var num = false; var cap = false; var paren = false; fun reset() { num = false; cap = false } }

    // null - nieznany znak; "" - znak pomocniczy (liczba / wielka litera); inaczej tekst do wpisania
    fun decode(cell: Int, pl: Boolean, s: Stan): String? {
        if (cell == NUM) { s.num = true; return "" }
        if (cell == capSign(pl)) { s.cap = true; return "" }
        letters[cell]?.takeIf { s.num && it in 'a'..'j' }?.let { return if (it == 'j') "0" else ('1' + (it - 'a')).toString() }
        s.num = false
        val ch = letters[cell] ?: (if (pl) polish[cell] else null) ?: (if (pl) punctPl else punctEn)[cell] ?: return null
        if (ch == '(' && pl) { s.paren = !s.paren; return if (s.paren) "(" else ")" }
        if (s.cap && ch.isLetter()) { s.cap = false; return ch.uppercase() }
        return ch.toString()
    }

    // nauka: alfabet (PL z polskimi literami) i punkty znaku
    fun alphabet(pl: Boolean): List<Char> = if (pl) "aąbcćdeęfghijklłmnńoóprsśtuwyzźż".toList() else ('a'..'z').toList()
    fun dotsOf(ch: Char, pl: Boolean): Int? = (letters + (if (pl) polish else emptyMap())).entries.firstOrNull { it.value == ch.lowercaseChar() }?.key
    fun dotsText(cell: Int) = (1..6).filter { cell and (1 shl (it - 1)) != 0 }.joinToString(", ")
    fun glyph(cell: Int) = (0x2800 + cell).toChar().toString()   // znak Unicode ⠁…⠿ (bity 0-5 = punkty 1-6)
    // 📖 ściąga: siatka „znak – litera” (brajl i Morse), stuknięcie w 📖 w prawym górnym rogu pola
    fun sheet(c: Canvas, w: Float, h: Float, dp: Float, items: List<Pair<String, String>>) {
        c.drawColor(Color.rgb(0x13, 0x10, 0x26)); val cols = 4; val rows = (items.size + cols - 1) / cols
        val cw = w / cols; val rh = (h - 30 * dp) / rows
        val t = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE; textSize = minOf(rh * 0.55f, 18 * dp) }
        val g = Paint(t).apply { color = Color.rgb(0x22, 0xD3, 0xEE) }
        t.textAlign = Paint.Align.CENTER; c.drawText("📖 ✕", w / 2, 20 * dp, t); t.textAlign = Paint.Align.LEFT
        items.forEachIndexed { i, (l, k) -> val x = (i % cols) * cw + 6 * dp; val y = 30 * dp + (i / cols) * rh + rh * 0.7f
            c.drawText(l.uppercase(), x, y, t); c.drawText(k, x + t.textSize * 1.2f, y, g) }
    }
    // nazwy znaków do czytania na głos (syntezator mowy pomija same znaki interpunkcyjne)
    fun spoken(t: String, pl: Boolean) = when (t) {
        "." -> if (pl) "kropka" else "full stop"; "," -> if (pl) "przecinek" else "comma"; "?" -> if (pl) "pytajnik" else "question mark"
        "!" -> if (pl) "wykrzyknik" else "exclamation mark"; ":" -> if (pl) "dwukropek" else "colon"; ";" -> if (pl) "średnik" else "semicolon"
        "-" -> if (pl) "łącznik" else "hyphen"; "(" -> if (pl) "nawias otwierający" else "open bracket"; ")" -> if (pl) "nawias zamykający" else "close bracket"
        "„", "”" -> if (pl) "cudzysłów" else "quote"; "'" -> if (pl) "apostrof" else "apostrophe"; " " -> if (pl) "spacja" else "space"
        else -> t
    }
}

// pole 6 punktów (pisanie brajlem i nauka): punkty z palców położonych razem - znak po podniesieniu wszystkich palców;
// jednym palcem przesunięcie: w prawo spacja, w lewo usuń, w dół nowa linia; dwoma palcami: w górę przeczytaj, w dół wyjście
@SuppressLint("ViewConstructor")
class BrajlPole(ctx: Context, private val onCell: (Int) -> Unit, private val onGesture: (String) -> Unit) : View(ctx) {
    var hints = true            // podpis punktów i gestów (wyłączany dla sprawnie piszących)
    var vibrate = true
    var mirror = false          // telefon ekranem od siebie: kolumny zamienione (punkty 1-2-3 pod prawą ręką)
    var title = ""
    private val dp = resources.displayMetrics.density
    private val zones = Array(6) { RectF() }
    private var mask = 0; private var lit = 0; private var maxPtr = 0; private var t0 = 0L
    private val sx = HashMap<Int, Float>(); private val sy = HashMap<Int, Float>(); private val ex = HashMap<Int, Float>(); private val ey = HashMap<Int, Float>()
    private val p = Paint(Paint.ANTI_ALIAS_FLAG); private val tp = Paint(Paint.ANTI_ALIAS_FLAG).apply { textAlign = Paint.Align.CENTER }

    init { contentDescription = context.getString(R.string.kb_braille_field) }

    override fun onMeasure(ws: Int, hs: Int) {
        val h = MeasureSpec.getSize(hs)
        setMeasuredDimension(MeasureSpec.getSize(ws), if (MeasureSpec.getMode(hs) == MeasureSpec.UNSPECIFIED || h == 0) (330 * dp).toInt() else min(h, (330 * dp).toInt()).coerceAtLeast((260 * dp).toInt()))
    }
    override fun onSizeChanged(w: Int, h: Int, ow: Int, oh: Int) {
        val top = if (title.isNotEmpty() || hints) 30 * dp else 6 * dp
        val rh = (h - top - 6 * dp) / 3; val cw = w / 2f
        for (i in 0 until 6) {
            val col = if ((i >= 3) != mirror) 1 else 0
            zones[i].set(col * cw + 4 * dp, top + rh * (i % 3) + 3 * dp, (col + 1) * cw - 4 * dp, top + rh * (i % 3 + 1) - 3 * dp)
        }
    }
    private fun zoneAt(x: Float, y: Float) = zones.indices.minByOrNull { val z = zones[it]; if (z.contains(x, y)) -1f else hypot(z.centerX() - x, z.centerY() - y) } ?: -1

    var pl = true; private var sheet = false; private var sheetTap = false
    override fun onDraw(c: Canvas) {
        if (sheet) { Brajl.sheet(c, width.toFloat(), height.toFloat(), dp, Brajl.alphabet(pl).map { it.toString() to Brajl.glyph(Brajl.dotsOf(it, pl) ?: 0) }); return }
        c.drawColor(Color.rgb(0x13, 0x10, 0x26))
        tp.textSize = 18 * dp; tp.color = Color.WHITE; c.drawText("📖", width - 22 * dp, 22 * dp, tp)
        tp.color = Color.argb(0xD0, 255, 255, 255); tp.textSize = 13 * dp
        if (title.isNotEmpty() || hints) c.drawText(title.ifEmpty { context.getString(R.string.kb_braille_hint) }, width / 2f, 20 * dp, tp)
        for (i in 0 until 6) {
            val z = zones[i]; val on = lit and (1 shl i) != 0
            p.color = if (on) Color.rgb(0x22, 0xC5, 0x5E) else Color.rgb(0x2E, 0x25, 0x60)
            c.drawRoundRect(z, 18 * dp, 18 * dp, p)
            p.color = if (on) Color.WHITE else Color.rgb(0x8B, 0x5C, 0xF6)
            c.drawCircle(z.centerX(), z.centerY(), min(z.width(), z.height()) * 0.28f, p)
            if (hints) { tp.color = if (on) Color.rgb(0x13, 0x10, 0x26) else Color.WHITE; tp.textSize = 22 * dp; c.drawText((i + 1).toString(), z.centerX(), z.centerY() + 8 * dp, tp) }
        }
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(e: MotionEvent): Boolean {
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> {
                if (e.actionMasked == MotionEvent.ACTION_DOWN && (sheet || e.y < 34 * dp && e.x > width - 50 * dp)) { sheet = !sheet; sheetTap = true; invalidate(); return true }   // 📖 ściąga
                if (sheetTap) return true
                if (e.actionMasked == MotionEvent.ACTION_DOWN) { mask = 0; maxPtr = 0; t0 = SystemClock.uptimeMillis(); sx.clear(); sy.clear(); ex.clear(); ey.clear() }
                val i = e.actionIndex; val id = e.getPointerId(i)
                sx[id] = e.getX(i); sy[id] = e.getY(i); ex[id] = e.getX(i); ey[id] = e.getY(i)
                val z = zoneAt(e.getX(i), e.getY(i)); if (z >= 0) mask = mask or (1 shl z)
                maxPtr = maxOf(maxPtr, e.pointerCount); lit = mask
                if (vibrate) performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP, HapticFeedbackConstants.FLAG_IGNORE_GLOBAL_SETTING)
                invalidate()
            }
            MotionEvent.ACTION_MOVE -> for (i in 0 until e.pointerCount) { val id = e.getPointerId(i); ex[id] = e.getX(i); ey[id] = e.getY(i) }
            MotionEvent.ACTION_POINTER_UP -> { val i = e.actionIndex; val id = e.getPointerId(i); ex[id] = e.getX(i); ey[id] = e.getY(i) }
            MotionEvent.ACTION_UP -> {
                if (sheetTap) { sheetTap = false; return true }
                val i = e.actionIndex; val id = e.getPointerId(i); ex[id] = e.getX(i); ey[id] = e.getY(i)
                val g = swipe()
                lit = 0; invalidate()
                if (g != null) onGesture(g) else if (mask != 0) onCell(mask)
                mask = 0
            }
            MotionEvent.ACTION_CANCEL -> { mask = 0; lit = 0; invalidate() }
        }
        return true
    }

    // przesunięcie (palce przejechały ponad 70 dp w tę samą stronę, szybko) - inaczej położenie palców = punkty
    private fun swipe(): String? {
        if (SystemClock.uptimeMillis() - t0 > 900 || maxPtr > 2 || sx.isEmpty()) return null
        val dx = sx.keys.map { (ex[it] ?: 0f) - (sx[it] ?: 0f) }; val dy = sx.keys.map { (ey[it] ?: 0f) - (sy[it] ?: 0f) }
        val far = 70 * dp
        val dir = when {
            dx.indices.all { dx[it] > far && abs(dx[it]) > abs(dy[it]) } -> "right"
            dx.indices.all { dx[it] < -far && abs(dx[it]) > abs(dy[it]) } -> "left"
            dy.all { it > far } -> "down"
            dy.all { it < -far } -> "up"
            else -> return null
        }
        return if (maxPtr == 1) when (dir) { "right" -> "space"; "left" -> "bs"; "down" -> "enter"; else -> "read" }
        else when (dir) { "down" -> "exit"; "up" -> "read"; "left" -> "bsword"; else -> "space" }
    }
}
