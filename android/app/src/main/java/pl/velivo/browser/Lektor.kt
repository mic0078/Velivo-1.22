package pl.velivo.browser

import android.content.Context
import android.speech.tts.TextToSpeech
import android.speech.tts.UtteranceProgressListener
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.util.Locale

// Lektor w odtwarzaczu (jak w Velivo na Windows): czyta napisy na głos w chwili ich pojawienia się, film na ten czas
// ściszony. Głos: silnik Google (gdy jest w telefonie), inaczej domyślny głos Androida, w języku napisów.
// Przy szybkich dialogach przyspiesza, zaległe kwestie pomija.
class Lektor(ctx: Context, private val duck: (Boolean) -> Unit) {
    class Cue(val start: Long, val end: Long, val text: String)

    companion object {
        private val srt = Regex("""(\d+):(\d{2}):(\d{2})[,.](\d{1,3})\s*-->\s*(\d+):(\d{2}):(\d{2})[,.](\d{1,3})""")
        private val vttShort = Regex("""^(\d{2}):(\d{2})[.](\d{3})\s*-->\s*(\d{2}):(\d{2})[.](\d{3})""")
        private val ass = Regex("""^(\d+):(\d{2}):(\d{2})[.](\d{1,3})$""")
        private fun ms(h: String, m: String, s: String, f: String) = ((h.toLong() * 60 + m.toLong()) * 60 + s.toLong()) * 1000 + f.padEnd(3, '0').take(3).toLong()

        // znaczniki <i>, {\an8}, ASS \N - sam tekst do czytania
        private fun clean(t: String) = t.replace(Regex("""\{[^}]*\}"""), "").replace(Regex("<[^>]*>"), "").replace("\\N", " ").replace("\\n", " ")
            .replace('\n', ' ').replace(Regex("""^\s*-\s*"""), "").replace(Regex("""\s+-\s+"""), " ").replace(Regex("""\s+"""), " ").trim()

        // SRT / VTT / ASS-SSA -> kwestie posortowane po czasie
        fun parse(content: String): List<Cue> {
            val cues = ArrayList<Cue>()
            val lines = content.replace("\r\n", "\n").replace('\r', '\n').split('\n')
            if (lines.any { it.trimStart().startsWith("Dialogue:", true) }) {
                for (l in lines) {
                    val t = l.trimStart(); if (!t.startsWith("Dialogue:", true)) continue
                    val p = t.substring(9).split(',', limit = 10); if (p.size < 10) continue
                    val a = ass.find(p[1].trim()) ?: continue; val b = ass.find(p[2].trim()) ?: continue
                    val text = clean(p[9])
                    if (text.isNotEmpty()) cues.add(Cue(ms(a.groupValues[1], a.groupValues[2], a.groupValues[3], a.groupValues[4].padEnd(3, '0')), ms(b.groupValues[1], b.groupValues[2], b.groupValues[3], b.groupValues[4].padEnd(3, '0')), text))
                }
            } else {
                var i = 0
                while (i < lines.size) {
                    val m = srt.find(lines[i]); val v = if (m == null) vttShort.find(lines[i]) else null
                    if (m == null && v == null) { i++; continue }
                    val start = if (m != null) ms(m.groupValues[1], m.groupValues[2], m.groupValues[3], m.groupValues[4]) else ms("0", v!!.groupValues[1], v.groupValues[2], v.groupValues[3])
                    val end = if (m != null) ms(m.groupValues[5], m.groupValues[6], m.groupValues[7], m.groupValues[8]) else ms("0", v!!.groupValues[4], v.groupValues[5], v.groupValues[6])
                    val sb = StringBuilder(); i++
                    while (i < lines.size && lines[i].isNotBlank()) { sb.append(lines[i]).append('\n'); i++ }
                    val text = clean(sb.toString())
                    if (text.isNotEmpty()) cues.add(Cue(start, end, text))
                }
            }
            return cues.sortedBy { it.start }
        }

        // polskie napisy często są w Windows-1250 (stare SRT) - UTF-8 tylko, gdy to poprawny UTF-8
        fun text(bytes: ByteArray): String {
            val utf8 = Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
            return runCatching { utf8.decode(ByteBuffer.wrap(bytes)).toString() }.getOrElse { String(bytes, charset("windows-1250")) }.removePrefix("﻿")
        }

        fun language(cues: List<Cue>): String {
            val s = cues.take(80).joinToString(" ") { it.text }.lowercase()
            val pl = s.count { it in "ąćęłńóśźż" } + Regex("""\b(nie|jest|się|to|że|tak|co|jak)\b""").findAll(s).count()
            val en = Regex("""\b(the|you|and|is|what|it|that|to)\b""").findAll(s).count()
            return if (pl >= en) "pl" else "en"
        }
    }

    private var cues: List<Cue> = emptyList()
    private var last = -1
    private var ready = false
    private var speaking = false
    private val tts: TextToSpeech

    init {
        // silnik Google, gdy jest zainstalowany (najlepsza polszczyzna); inaczej domyślny
        val google = "com.google.android.tts"
        val hasGoogle = runCatching { ctx.packageManager.getPackageInfo(google, 0); true }.getOrDefault(false)
        val ready0 = TextToSpeech.OnInitListener { ok -> ready = ok == TextToSpeech.SUCCESS; setLang() }
        tts = if (hasGoogle) TextToSpeech(ctx, ready0, google) else TextToSpeech(ctx, ready0)
        tts.setOnUtteranceProgressListener(object : UtteranceProgressListener() {
            override fun onStart(id: String?) { speaking = true; duck(true) }
            override fun onDone(id: String?) { speaking = false; duck(false) }
            @Deprecated("") override fun onError(id: String?) { speaking = false; duck(false) }
        })
    }

    private var lang = "pl"
    private fun setLang() { if (ready) tts.language = if (lang == "pl") Locale("pl", "PL") else Locale.ENGLISH }

    val loaded get() = cues.isNotEmpty()

    fun load(content: String) { cues = parse(content); last = -1; lang = language(cues); setLang() }

    // co ~150 ms z odtwarzacza: nowa kwestia na ekranie -> czytamy ją (zaległe pomijamy)
    fun at(t: Long) {
        if (!ready || cues.isEmpty()) return
        var lo = 0; var hi = cues.size - 1; var found = -1
        while (lo <= hi) { val mid = (lo + hi) / 2; if (cues[mid].start <= t) { found = mid; lo = mid + 1 } else hi = mid - 1 }
        if (found < 0 || t >= cues[found].end || found == last) return
        last = found
        val c = cues[found]
        // tempo: długa kwestia w krótkim czasie - szybciej (do 2x), jak na Windows
        val need = c.text.length * 65L   // ok. 65 ms na znak przy zwykłym tempie
        val room = (c.end - c.start).coerceAtLeast(400)
        tts.setSpeechRate((need.toFloat() / room).coerceIn(1f, 2f))
        tts.speak(c.text, TextToSpeech.QUEUE_FLUSH, null, "c$found")   // zaległa kwestia przerwana - czytamy bieżącą
    }

    fun seeked() { last = -1; tts.stop(); if (speaking) { speaking = false; duck(false) } }
    fun stop() { tts.stop(); if (speaking) { speaking = false; duck(false) } }
    fun release() { runCatching { tts.stop(); tts.shutdown() } }
}
