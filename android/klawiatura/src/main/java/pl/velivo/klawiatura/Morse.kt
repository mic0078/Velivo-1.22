package pl.velivo.klawiatura

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.os.SystemClock
import android.os.VibrationEffect
import android.os.Vibrator
import android.view.MotionEvent
import android.view.View
import kotlin.math.abs
import kotlin.math.min

// ·− alfabet Morse'a: międzynarodowy (litery, cyfry, interpunkcja) i polskie litery ą ć ę ł ń ó ś ź ż
object Morse {
    private val base = mapOf(".-" to "a", "-..." to "b", "-.-." to "c", "-.." to "d", "." to "e", "..-." to "f", "--." to "g", "...." to "h", ".." to "i", ".---" to "j",
        "-.-" to "k", ".-.." to "l", "--" to "m", "-." to "n", "---" to "o", ".--." to "p", "--.-" to "q", ".-." to "r", "..." to "s", "-" to "t", "..-" to "u",
        "...-" to "v", ".--" to "w", "-..-" to "x", "-.--" to "y", "--.." to "z",
        ".----" to "1", "..---" to "2", "...--" to "3", "....-" to "4", "....." to "5", "-...." to "6", "--..." to "7", "---.." to "8", "----." to "9", "-----" to "0",
        ".-.-.-" to ".", "--..--" to ",", "..--.." to "?", "-.-.--" to "!", ".----." to "'", "-..-." to "/", "-.--." to "(", "-.--.-" to ")", "---..." to ":",
        "-.-.-." to ";", "-...-" to "=", ".-.-." to "+", "-....-" to "-", ".-..-." to "\"", ".--.-." to "@")
    private val polish = mapOf(".-.-" to "ą", "-.-.." to "ć", "..-.." to "ę", ".-..-" to "ł", "--.--" to "ń", "---." to "ó", "...-..." to "ś", "--..-." to "ź", "--..-" to "ż")
    fun decode(code: String, pl: Boolean): String? = base[code] ?: if (pl) polish[code] else null
    fun show(code: String) = code.replace('.', '•').replace('-', '—')
}

// pole Morse'a: lewa połowa • (kropka), prawa — (kreska); litera po chwili przerwy. Przesunięcia jak w brajlu:
// jednym palcem w prawo spacja, w lewo usuń, w dół nowa linia, w górę przeczytaj; dwoma palcami w dół - zwykła klawiatura
@SuppressLint("ViewConstructor")
class MorsePole(ctx: Context, private val onLetter: (String) -> Unit, private val onGesture: (String) -> Unit) : View(ctx) {
    var hints = true; var vibrate = true; var pl = true; var gap = 1000L
    private val dp = resources.displayMetrics.density
    private var code = ""; private var lit = 0; private var maxPtr = 0; private var t0 = 0L
    private var sx = 0f; private var sy = 0f
    private val p = Paint(Paint.ANTI_ALIAS_FLAG); private val tp = Paint(Paint.ANTI_ALIAS_FLAG).apply { textAlign = Paint.Align.CENTER }
    private val left = RectF(); private val right = RectF()
    private val done = Runnable { if (code.isNotEmpty()) { val c = code; code = ""; invalidate(); onLetter(c) } }

    init { contentDescription = context.getString(R.string.kb_morse_field) }

    override fun onMeasure(ws: Int, hs: Int) {
        val h = MeasureSpec.getSize(hs)
        setMeasuredDimension(MeasureSpec.getSize(ws), if (MeasureSpec.getMode(hs) == MeasureSpec.UNSPECIFIED || h == 0) (300 * dp).toInt() else min(h, (300 * dp).toInt()).coerceAtLeast((240 * dp).toInt()))
    }
    override fun onSizeChanged(w: Int, h: Int, ow: Int, oh: Int) {
        val top = 92 * dp
        left.set(6 * dp, top, w / 2f - 4 * dp, h - 6 * dp); right.set(w / 2f + 4 * dp, top, w - 6 * dp, h - 6 * dp)
    }

    override fun onDraw(c: Canvas) {
        c.drawColor(Color.rgb(0x13, 0x10, 0x26))
        tp.color = Color.argb(0xD0, 255, 255, 255); tp.textSize = 13 * dp
        if (hints) c.drawText(context.getString(R.string.kb_morse_hint), width / 2f, 20 * dp, tp)
        // bieżący znak: kropki i kreski oraz litera, jeśli już taka jest
        tp.color = Color.WHITE; tp.textSize = 34 * dp
        c.drawText(Morse.show(code) + (Morse.decode(code, pl)?.let { "  $it" } ?: ""), width / 2f, 70 * dp, tp)
        for ((i, z) in listOf(left, right).withIndex()) {
            val on = lit == i + 1
            p.color = if (on) Color.rgb(0x22, 0xC5, 0x5E) else Color.rgb(0x2E, 0x25, 0x60)
            c.drawRoundRect(z, 18 * dp, 18 * dp, p)
            tp.color = if (on) Color.rgb(0x13, 0x10, 0x26) else Color.WHITE; tp.textSize = 64 * dp
            c.drawText(if (i == 0) "•" else "—", z.centerX(), z.centerY() + 22 * dp, tp)
        }
    }

    private fun buzz(ms: Long) {
        if (!vibrate) return
        runCatching { context.getSystemService(Vibrator::class.java)?.vibrate(VibrationEffect.createOneShot(ms, VibrationEffect.DEFAULT_AMPLITUDE)) }
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(e: MotionEvent): Boolean {
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> { removeCallbacks(done); sx = e.x; sy = e.y; maxPtr = 1; t0 = SystemClock.uptimeMillis(); lit = if (e.x < width / 2f) 1 else 2; invalidate() }
            MotionEvent.ACTION_POINTER_DOWN -> { maxPtr = maxOf(maxPtr, e.pointerCount); lit = 0; invalidate() }
            MotionEvent.ACTION_UP -> {
                lit = 0
                val dx = e.x - sx; val dy = e.y - sy; val far = 70 * dp
                val g = when {
                    SystemClock.uptimeMillis() - t0 > 900 || abs(dx) < far && abs(dy) < far -> null
                    maxPtr >= 2 -> if (dy > far && abs(dy) > abs(dx)) "exit" else null
                    abs(dx) > abs(dy) -> if (dx > 0) "space" else "bs"
                    else -> if (dy > 0) "enter" else "read"
                }
                when {
                    g != null -> { if (code.isNotEmpty() && g != "bs") done.run(); if (g == "bs" && code.isNotEmpty()) code = "" else onGesture(g) }
                    maxPtr == 1 && code.length < 7 -> { val dot = sx < width / 2f; code += if (dot) "." else "-"; buzz(if (dot) 40L else 160L); postDelayed(done, gap) }
                }
                invalidate()
            }
            MotionEvent.ACTION_CANCEL -> { lit = 0; invalidate() }
        }
        return true
    }
}
