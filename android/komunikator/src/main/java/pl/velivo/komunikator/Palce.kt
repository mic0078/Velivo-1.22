package pl.velivo.komunikator

import android.content.Context
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.view.View
import java.io.File

// 🤟 alfabet palcowy (międzynarodowy, jednoręczny) - uproszczone, wyraźne rysunki dłoni (widok od strony patrzącego) i duża litera obok.
// Własne zdjęcie litery albo całego słowa (znaki/<klucz>.jpg w aplikacji) zastępuje rysunek - tak powstaje słowniczek znaków.
object Palce {
    // palce wskazujący, środkowy, serdeczny, mały: E - wyprostowany, C - zgięty (pięść), H - haczyk, T - kółko z kciukiem;
    // kciuk: up - przy palcu, out - odchylony, across - na dłoni, between - między palcami, under2/under3 - pod palcami, circle - kółko
    private class S(val f: String, val th: String, val ang: IntArray = IntArray(4), val rot: Float = 0f, val mot: Char = ' ', val shape: Char = ' ')
    private val signs = mapOf(
        'a' to S("CCCC", "up"), 'b' to S("EEEE", "across"), 'c' to S("", "", shape = 'C'), 'd' to S("ECCC", "across"), 'e' to S("HHHH", "across"),
        'f' to S("TEEE", "circle"), 'g' to S("ECCC", "out", rot = 90f), 'h' to S("EECC", "across", rot = 90f), 'i' to S("CCCE", "across"),
        'j' to S("CCCE", "across", mot = 'J'), 'k' to S("EECC", "between", intArrayOf(-8, 25, 0, 0)), 'l' to S("ECCC", "out"), 'm' to S("CCCC", "under3"),
        'n' to S("CCCC", "under2"), 'o' to S("", "", shape = 'O'), 'p' to S("EECC", "between", intArrayOf(-8, 25, 0, 0), rot = 160f), 'q' to S("ECCC", "out", rot = 180f),
        'r' to S("EECC", "across", intArrayOf(9, -9, 0, 0)), 's' to S("CCCC", "across"), 't' to S("CCCC", "between"), 'u' to S("EECC", "across"),
        'v' to S("EECC", "across", intArrayOf(-13, 13, 0, 0)), 'w' to S("EEEC", "across", intArrayOf(-16, 0, 16, 0)), 'x' to S("HCCC", "across"),
        'y' to S("CCCE", "out"), 'z' to S("ECCC", "across", mot = 'Z'))
    // polskie litery - litera podstawowa (alfabet międzynarodowy nie ma ą, ć…)
    fun base(c: Char): Char { val i = "ąćęłńóśźż".indexOf(c.lowercaseChar()); return if (i >= 0) "acelnoszz"[i] else c.lowercaseChar() }
    fun has(c: Char) = base(c) in signs
    // własne zdjęcia: litera (a.jpg) albo słowo (dom.jpg)
    fun photo(ctx: Context, key: String) = File(File(ctx.filesDir, "znaki"), key.lowercase().filter { it.isLetterOrDigit() } + ".jpg").takeIf { it.exists() }
    fun savePhoto(ctx: Context, key: String) = File(File(ctx.filesDir, "znaki").apply { mkdirs() }, key.lowercase().filter { it.isLetterOrDigit() } + ".jpg")

    private val skin = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.rgb(0xFF, 0xD9, 0xB8) }
    private val line = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.rgb(0x3B, 0x2A, 0x1A); style = Paint.Style.STROKE; strokeCap = Paint.Cap.ROUND; strokeJoin = Paint.Join.ROUND }
    private val arrow = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.rgb(0x22, 0xD3, 0xEE); style = Paint.Style.STROKE; strokeCap = Paint.Cap.ROUND }
    // rysunek znaku litery: środek (cx, cy), u - jednostka (ok. 1/10 wysokości rysunku); także w klawiszach klawiatury (🤟 tryb alfabetu palcowego)
    fun draw(cv: Canvas, ch: Char, cx: Float, cy: Float, u: Float) {
        val sp = signs[base(ch)] ?: return
        line.strokeWidth = u * 0.16f; arrow.strokeWidth = u * 0.22f
        cv.save(); cv.rotate(sp.rot, cx, cy)
        when (sp.shape) {
            'C' -> { skin.style = Paint.Style.STROKE; skin.strokeWidth = u * 1.5f; val r = RectF(cx - 3 * u, cy - 3.2f * u, cx + 3 * u, cy + 3.2f * u)
                cv.drawArc(r, 40f, 280f, false, skin); line.strokeWidth = u * 0.12f; cv.drawArc(RectF(r.left - .75f * u, r.top - .75f * u, r.right + .75f * u, r.bottom + .75f * u), 40f, 280f, false, line)
                cv.drawArc(RectF(r.left + .75f * u, r.top + .75f * u, r.right - .75f * u, r.bottom - .75f * u), 40f, 280f, false, line); skin.style = Paint.Style.FILL }
            'O' -> { skin.style = Paint.Style.STROKE; skin.strokeWidth = u * 1.5f; cv.drawCircle(cx, cy, 2.6f * u, skin); line.strokeWidth = u * 0.12f
                cv.drawCircle(cx, cy, 3.35f * u, line); cv.drawCircle(cx, cy, 1.85f * u, line); skin.style = Paint.Style.FILL }
            else -> hand(cv, sp, cx, cy, u)
        }
        cv.restore()
    }

    class Dlon(ctx: Context) : View(ctx) {
        private var ch = 'a'; private var pic: android.graphics.Bitmap? = null; private var caption = ""
        private val big = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE; textAlign = Paint.Align.CENTER; isFakeBoldText = true }
        fun set(c: Char, photoFile: File?, cap: String) {
            ch = c; caption = cap; pic = photoFile?.let { runCatching { BitmapFactory.decodeFile(it.path) }.getOrNull() }
            contentDescription = cap; invalidate()
        }
        override fun onDraw(cv: Canvas) {
            cv.drawColor(Color.rgb(0x13, 0x10, 0x26))
            val handW = width * 0.66f; val u = minOf(handW, height.toFloat()) / 10f
            big.textSize = minOf(width * 0.3f, height * 0.5f)
            cv.drawText(caption.ifEmpty { ch.uppercase() }, handW + (width - handW) / 2, height / 2f + big.textSize / 3, big)
            pic?.let { b -> val s = minOf(handW / b.width, height.toFloat() / b.height); val w = b.width * s; val h = b.height * s
                cv.drawBitmap(b, null, RectF((handW - w) / 2, (height - h) / 2, (handW + w) / 2, (height + h) / 2), null); return }
            Palce.draw(cv, ch, handW / 2, height / 2f, u)
        }
    }
    private fun capsule(cv: Canvas, x: Float, y: Float, len: Float, w: Float, deg: Float) {
            cv.save(); cv.rotate(deg, x, y); val r = RectF(x - w / 2, y - len, x + w / 2, y + w / 3)
            cv.drawRoundRect(r, w / 2, w / 2, skin); cv.drawRoundRect(r, w / 2, w / 2, line); cv.restore()
        }
        private fun hand(cv: Canvas, sp: S, cx: Float, cy: Float, u: Float) {
            val top = cy - 0.2f * u; val palm = RectF(cx - 2.2f * u, top, cx + 2.2f * u, top + 4.2f * u)
            val xs = floatArrayOf(-1.55f, -0.52f, 0.52f, 1.55f).map { cx + it * u }; val lens = floatArrayOf(3.3f, 3.7f, 3.4f, 2.7f); val fw = 0.95f * u
            val thBase = Pair(palm.left + 0.2f * u, palm.top + 2.6f * u)
            // kciuk z tyłu (pod palcami): pod, między
            fun thumbTip(x: Float, y: Float) { cv.drawCircle(x, y, 0.55f * u, skin); cv.drawCircle(x, y, 0.55f * u, line) }
            cv.drawRoundRect(palm, 1.1f * u, 1.1f * u, skin); cv.drawRoundRect(palm, 1.1f * u, 1.1f * u, line)
            when (sp.th) { "under3" -> thumbTip(xs[2] + 0.5f * u, palm.top + 0.9f * u); "under2" -> thumbTip(xs[1] + 0.5f * u, palm.top + 0.9f * u) }
            for (i in 0 until 4) when (sp.f[i]) {
                'E' -> capsule(cv, xs[i], palm.top + 0.3f * u, lens[i] * u, fw, sp.ang[i].toFloat())
                'H' -> { capsule(cv, xs[i], palm.top + 0.3f * u, 1.5f * u, fw, 0f); capsule(cv, xs[i], palm.top - 1.1f * u, 1.2f * u, fw, 150f) }
                'T' -> { skin.style = Paint.Style.STROKE; skin.strokeWidth = fw; cv.drawCircle(xs[i] - 0.4f * u, palm.top - 0.9f * u, 1.0f * u, skin); skin.style = Paint.Style.FILL
                    cv.drawCircle(xs[i] - 0.4f * u, palm.top - 0.9f * u, 1.0f * u + fw / 2, line); cv.drawCircle(xs[i] - 0.4f * u, palm.top - 0.9f * u, 1.0f * u - fw / 2, line) }
                else -> { val r = RectF(xs[i] - fw / 2, palm.top - 0.55f * u, xs[i] + fw / 2, palm.top + 1.4f * u)   // zgięty: kostka nad dłonią i palec zawinięty na dłoń
                    cv.drawRoundRect(r, fw / 2, fw / 2, skin); cv.drawRoundRect(r, fw / 2, fw / 2, line) }
            }
            when (sp.th) {
                "up" -> capsule(cv, palm.left + 0.1f * u, palm.top + 2.2f * u, 2.6f * u, fw * 1.1f, -6f)
                "out" -> capsule(cv, thBase.first, thBase.second, 2.8f * u, fw * 1.1f, -70f)
                "across" -> { val r = RectF(palm.left + 0.2f * u, palm.top + 1.5f * u, cx + 1.0f * u, palm.top + 1.5f * u + fw * 1.1f); cv.drawRoundRect(r, fw / 2, fw / 2, skin); cv.drawRoundRect(r, fw / 2, fw / 2, line) }
                "between" -> { capsule(cv, thBase.first, thBase.second, 2.6f * u, fw * 1.1f, 30f) }
                "circle" -> capsule(cv, thBase.first, thBase.second, 2.6f * u, fw * 1.1f, 15f)
            }
            // ruch: J (małym palcem) i Z (wskazującym) - strzałka
            if (sp.mot != ' ') {
                val p = Path()
                if (sp.mot == 'J') { val x = xs[3]; val y = palm.top - 2.9f * u; p.moveTo(x + 0.8f * u, y); p.lineTo(x + 0.8f * u, y + 2.2f * u); p.quadTo(x + 0.8f * u, y + 3.4f * u, x - 0.6f * u, y + 3.0f * u) }
                else { val x = xs[0]; val y = palm.top - 4.0f * u; p.moveTo(x - 1.2f * u, y); p.lineTo(x + 1.2f * u, y); p.lineTo(x - 1.2f * u, y + 1.4f * u); p.lineTo(x + 1.2f * u, y + 1.4f * u) }
                // tor ruchu: przerywana linia i grot strzałki na końcu (wcześniej ciągły zygzak wyglądał jak druga litera „Z”)
                arrow.pathEffect = android.graphics.DashPathEffect(floatArrayOf(u * 0.5f, u * 0.4f), 0f); cv.drawPath(p, arrow); arrow.pathEffect = null
                val pm = android.graphics.PathMeasure(p, false); val end = FloatArray(2); val tan = FloatArray(2); pm.getPosTan(pm.length, end, tan)
                val ang = Math.atan2(tan[1].toDouble(), tan[0].toDouble()); fun tip(da: Double) = Pair(end[0] - (u * 0.9f * Math.cos(ang + da)).toFloat(), end[1] - (u * 0.9f * Math.sin(ang + da)).toFloat())
                val (x1, y1) = tip(0.5); val (x2, y2) = tip(-0.5); cv.drawLine(end[0], end[1], x1, y1, arrow); cv.drawLine(end[0], end[1], x2, y2, arrow)
            }
        }
}
