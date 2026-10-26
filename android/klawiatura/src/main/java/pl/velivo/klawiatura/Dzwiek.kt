package pl.velivo.klawiatura

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.Handler
import android.os.Looper
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.max
import kotlin.math.sqrt

// 🗣 dźwięki zamiast słów (dla osób, które nie mówią wyraźnie, np. niesłyszących): długie „aaaa” - zatwierdź, długie „uuuu” - usuń słowo.
// Mikrofon tylko w pamięci, ramka po ramce: głośność, czy dźwięk jest dźwięczny i ciągły, oraz barwa (środek ciężkości widma 150–2400 Hz:
// „a” ok. 700–1100 Hz, „u” ok. 300–600 Hz). Nic nie jest nagrywane ani wysyłane. Kalibracja: własne „aaaa” i „uuuu” - granica pośrodku.
object Dzwiek {
    private const val RATE = 16000; private const val N = 512   // ramka 32 ms
    private val bins = (150..2400 step 50).toList()
    private val coef = bins.map { 2 * cos(2 * PI * it / RATE) }

    // barwa ramki: środek ciężkości widma (Hz) - „a” wysoko, „u” nisko; niezależna od wysokości głosu
    fun tone(x: ShortArray, n: Int): Double {
        var num = 0.0; var den = 1e-9
        for ((i, f) in bins.withIndex()) {
            var s1 = 0.0; var s2 = 0.0; val c = coef[i]
            for (k in 0 until n) { val s0 = x[k] + c * s1 - s2; s2 = s1; s1 = s0 }
            val e = s1 * s1 + s2 * s2 - c * s1 * s2
            num += f * e; den += e
        }
        return num / den
    }
    private fun rms(x: ShortArray, n: Int): Double { var s = 0.0; for (k in 0 until n) s += x[k].toDouble() * x[k]; return sqrt(s / max(1, n)) }
    private fun zcr(x: ShortArray, n: Int): Double { var z = 0; for (k in 1 until n) if ((x[k] >= 0) != (x[k - 1] >= 0)) z++; return z.toDouble() / n }

    const val A = 900.0; const val U = 450.0   // barwa bez kalibracji
    // rozpoznanie z ramek (także w testach): dźwięk ciągły ≥ minMs, dźwięczny (nie szum, nie „sss”), wyraźnie głośniejszy od tła.
    // Decyzja po końcu dźwięku z jego drugiej połowy („eeuuuu” - to, czym się kończy); niepewne - nic (bez przypadkowego wysłania)
    class Wykrywacz(private val minMs: Int = 1000, var ta: Double = A, var tu: Double = U) {
        private var floor = 300.0; private var gaps = 0; private val tones = ArrayList<Double>()
        fun frame(x: ShortArray, n: Int): Char? {
            val r = rms(x, n)
            val voiced = r > max(floor * 3, 400.0) && zcr(x, n) < 0.3
            if (!voiced) floor = floor * 0.97 + r * 0.03
            if (voiced) { if (tones.size < 2000) tones.add(tone(x, n)); gaps = 0; return null }
            if (tones.isEmpty() || ++gaps <= 3) return null
            val long = tones.size * N * 1000 / RATE >= minMs
            val half = tones.subList(tones.size / 2, tones.size).sorted(); val t = half[half.size / 2]
            val steady = half.count { abs(it - t) < 150 } >= half.size * 0.75   // przeciągnięty dźwięk ma stałą barwę, zwykła mowa - zmienną
            tones.clear(); gaps = 0
            if (!long || !steady) return null
            val mid = (ta + tu) / 2; val margin = (ta - tu) / 6
            return if (t > mid + margin) 'a' else if (t < mid - margin) 'u' else null
        }
    }

    private var rec: AudioRecord? = null; @Volatile private var on = false
    @SuppressLint("MissingPermission")
    fun start(ta: Double, tu: Double, onSound: (Char) -> Unit) {
        if (on) return
        val size = max(AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT), N * 4)
        val r = runCatching { AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, size) }.getOrNull() ?: return
        if (r.state != AudioRecord.STATE_INITIALIZED) { r.release(); return }
        rec = r; on = true
        val main = Handler(Looper.getMainLooper())
        Thread({
            val w = Wykrywacz(ta = ta, tu = tu); val buf = ShortArray(N)
            runCatching { r.startRecording() }
            while (on) {
                val n = r.read(buf, 0, N); if (n <= 0) { if (n < 0) break else continue }
                w.frame(buf, n)?.let { k -> main.post { if (on) onSound(k) } }
            }
            runCatching { r.stop() }; r.release()
        }, "Velivo dzwiek").start()
    }
    fun stop() { on = false; rec = null }

    // kalibracja: średnia barwa długiego dźwięku (ok. 2 s, tylko ramki dźwięczne) - null, gdy za cicho
    @SuppressLint("MissingPermission")
    fun measure(done: (Double?) -> Unit) {
        val main = Handler(Looper.getMainLooper())
        Thread({
            val size = max(AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT), N * 4)
            val r = runCatching { AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, size) }.getOrNull()
            var sum = 0.0; var cnt = 0
            if (r != null && r.state == AudioRecord.STATE_INITIALIZED) {
                val buf = ShortArray(N); r.startRecording()
                repeat(RATE * 3 / N) { val n = r.read(buf, 0, N); if (n > 0 && rms(buf, n) > 600 && zcr(buf, n) < 0.3) { sum += tone(buf, n); cnt++ } }
                runCatching { r.stop() }
            }
            r?.release()
            main.post { done(if (cnt >= 15) sum / cnt else null) }
        }, "Velivo kalibracja").start()
    }
}
