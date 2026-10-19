package pl.velivo.klawiatura

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RadialGradient
import android.graphics.RectF
import android.graphics.Shader
import android.graphics.Typeface
import android.inputmethodservice.InputMethodService
import android.media.AudioManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import android.speech.tts.TextToSpeech
import android.text.InputType
import android.view.MotionEvent
import android.view.View
import android.view.inputmethod.EditorInfo
import android.widget.Toast
import java.io.File
import java.util.Locale
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.min

// Klawiatura Velivo na telefonie - ta sama co na Windows: wygląd w kolorach logo, autokorekta, propozycje, pisanie
// przeciągnięciem (flow), nauka słów i zdań, Mój słownik, dyktowanie, czytanie na głos, PL / EN. Bez pada i skrótów Windows.
class KlawiaturaVelivo : InputMethodService() {
    companion object {
        const val ACTION_MIC = "pl.velivo.browser.KLAWIATURA_MIKROFON"
        const val ACTION_WORDS = "pl.velivo.browser.KLAWIATURA_SLOWNIK"
        private var loadedLang: String? = null
        private val learned = HashMap<String, Int>()
        private val own = HashSet<String>()

        fun lang(ctx: Context): String = prefs(ctx).getString("klaw-jezyk", null) ?: if (Locale.getDefault().language == "pl") "pl" else "en"
        fun prefs(ctx: Context) = ctx.getSharedPreferences("klawiatura", Context.MODE_PRIVATE)
        private fun file(ctx: Context) = File(ctx.filesDir, "klawiatura.bin")

        // słownik wbudowany + Moje słowa i nauka (zaszyfrowane kluczem z Android Keystore) - w tle
        fun load(ctx: Context, done: (() -> Unit)? = null) {
            val l = lang(ctx)
            if (Slownik.loaded && loadedLang == l) { done?.invoke(); return }
            loadedLang = l
            Thread {
                runCatching {
                    val words = ctx.assets.open("slowa-$l.txt").bufferedReader().readLines().toMutableList()
                    runCatching { ctx.assets.open("slowa-$l-dodatek.txt").bufferedReader().readLines() }.getOrNull()?.let { extra ->
                        val have = words.toHashSet(); val add = extra.map { it.trim() }.filter { it.isNotEmpty() && have.add(it) }
                        words.addAll(min(5000, words.size), add)
                    }
                    Slownik.load(words)
                    synchronized(learned) {
                        learned.clear(); own.clear(); Slownik.clearLearned()
                        val f = file(ctx)
                        if (f.exists()) Szyfr.open(f.readBytes())?.let { Slownik.loadText(String(it, Charsets.UTF_8), learned, own) }
                        own.forEach { Slownik.addUserWord(it) }
                    }
                    Slownik.useCount = { w -> synchronized(learned) { learned[w] ?: 0 } }
                }
                Handler(Looper.getMainLooper()).post { done?.invoke() }
            }.start()
        }

        fun save(ctx: Context) {
            val text = synchronized(learned) { Slownik.saveText(learned, own) }
            runCatching { file(ctx).writeBytes(Szyfr.seal(text.toByteArray(Charsets.UTF_8))) }
        }

        fun ownWords(): List<String> = synchronized(learned) { own.sorted() }
        // Mój słownik: jedno słowo w linii (dopisywanie hurtem, usuwanie przez skasowanie linii)
        fun setOwnWords(ctx: Context, list: List<String>) {
            synchronized(learned) {
                val now = list.map { it.trim() }.filter { it.length >= 2 && !it.contains(' ') }.toSet()
                own.filter { it !in now }.forEach { own.remove(it); Slownik.removeUserWord(it) }
                now.filter { it !in own }.forEach { own.add(it); Slownik.addUserWord(it) }
            }
            save(ctx)
        }
    }

    private lateinit var view: Klawisze
    private var tts: TextToSpeech? = null
    private var rec: SpeechRecognizer? = null
    private val ui = Handler(Looper.getMainLooper())

    // stan pisania
    private var secret = false          // pole hasła: nic do nauki ani propozycji
    private var cur = ""                // bieżące słowo z klawiszy
    private var prevWord: String? = null; private var prevWord2: String? = null
    private var fixFrom: String? = null; private var fixTo: String? = null; private var fixSep = ""
    private val keep = HashSet<String>()   // cofnięte poprawki - bez poprawiania do końca sesji
    private var addAsk: String? = null    // „Dodać do słownika?”
    private var sentence = StringBuilder()
    private var lastSwipe: String? = null; private var lastTrace: List<Slownik.P>? = null; private var lastKeyW = 1.0
    private var shift = false; private var caps = false; private var alt = false; private var shiftAuto = false
    private var listening = false

    override fun onCreate() {
        super.onCreate()
        load(this) { if (::view.isInitialized) view.refresh() }
    }

    override fun onCreateInputView(): View { view = Klawisze(this); return view }

    override fun onStartInputView(info: EditorInfo, restarting: Boolean) {
        super.onStartInputView(info, restarting)
        load(this) { view.refresh() }
        val cls = info.inputType and InputType.TYPE_MASK_CLASS
        val v = info.inputType and InputType.TYPE_MASK_VARIATION
        secret = cls == InputType.TYPE_CLASS_TEXT && (v == InputType.TYPE_TEXT_VARIATION_PASSWORD || v == InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD || v == InputType.TYPE_TEXT_VARIATION_WEB_PASSWORD) ||
            cls == InputType.TYPE_CLASS_NUMBER && v == InputType.TYPE_NUMBER_VARIATION_PASSWORD ||
            info.imeOptions and EditorInfo.IME_FLAG_NO_PERSONALIZED_LEARNING != 0
        cur = ""; fixFrom = null; addAsk = null; lastSwipe = null; alt = false; caps = false
        prevWord = null; prevWord2 = null; sentence.clear()
        autoShift()
        view.theme(); view.refresh()
    }

    override fun onFinishInputView(finishingInput: Boolean) { super.onFinishInputView(finishingInput); stopListening() }
    override fun onDestroy() { tts?.shutdown(); rec?.destroy(); super.onDestroy() }

    private fun pref(k: String, def: Boolean = true) = prefs(this).getBoolean(k, def)
    private fun before(n: Int = 64): String = currentInputConnection?.getTextBeforeCursor(n, 0)?.toString() ?: ""

    // wielka litera na początku pola i po ". ", "! ", "? "
    private fun autoShift() {
        if (caps) return
        val b = before(3)
        val info = currentInputEditorInfo
        val wants = info != null && (info.inputType and InputType.TYPE_MASK_CLASS) == InputType.TYPE_CLASS_TEXT &&
            (info.inputType and InputType.TYPE_TEXT_FLAG_CAP_SENTENCES != 0 || info.inputType and InputType.TYPE_MASK_VARIATION == 0)
        shiftAuto = true   // sama wielka litera na początku zdania - cyfry zostają cyframi
        shift = wants && (b.isEmpty() || b.trimEnd().let { it.isEmpty() || it.last() in ".!?" } && b.endsWith(" ") || b.endsWith("\n"))
    }

    private fun click() {
        if (pref("klaw-klik")) (getSystemService(AUDIO_SERVICE) as AudioManager).playSoundEffect(AudioManager.FX_KEYPRESS_STANDARD, 0.4f)
    }

    // --- pisanie ---
    private fun type(c: Char) {
        val ic = currentInputConnection ?: return
        fixFrom = null; addAsk = null; lastSwipe = null
        var ch = c
        if (alt) { val i = "acelnoszx".indexOf(ch.lowercaseChar()); if (i >= 0) ch = "ąćęłńóśżź"[i]; if (!caps) alt = false }
        val up = shift xor caps
        if (ch.isLetter()) ch = if (up) ch.uppercaseChar() else ch
        else if (shift && !shiftAuto) { val i = "1234567890-=[];',./".indexOf(ch); if (i >= 0) ch = "!@#$%^&*()_+{}:\"<>?"[i] }
        if (ch.isLetter()) { ic.commitText(ch.toString(), 1); cur += ch }
        else finishWord(ch.toString())
        if (shift && !caps) shift = false
        if (!ch.isLetter()) autoShift()
        view.refresh()
    }

    // koniec słowa (spacja, znak, Enter): autokorekta, przecinek, „że”, sklejanie, nauka
    private fun finishWord(sep: String) {
        val ic = currentInputConnection ?: return
        val w = cur; cur = ""
        var out = w
        if (w.isNotEmpty() && !secret) {
            val fix = if (pref("klaw-korekta")) Slownik.complete(w, keep) else null
            Slownik.zeNow(fix ?: w, prevWord)?.let { out = it } ?: run { if (fix != null) out = fix }
            if (out != w) {
                ic.beginBatchEdit(); ic.deleteSurroundingText(w.length, 0); ic.commitText(out, 1); ic.endBatchEdit()
                fixFrom = w; fixTo = out; fixSep = sep
            }
            afterWord(out)
        }
        ic.commitText(sep, 1)
        if (sep == "\n" || sep in listOf(".", "!", "?")) { if (!secret) Slownik.learnSentence(sentence.toString() + sep.trim()); sentence.clear(); prevWord = null; prevWord2 = null }
    }

    // poprawki zależne od poprzedniego słowa (ze -> że, przecinek, na prawde -> naprawdę) i nauka
    private fun afterWord(word: String) {
        val ic = currentInputConnection ?: return
        val p = prevWord; val p2 = prevWord2
        val b = before(word.length + 40)
        if (p != null) {
            // „ze” przed tym słowem to „że”: ze to -> że to
            Slownik.zeAsZe("ze", word, p2).takeIf { p == "ze" && b.endsWith("ze $word", true) }?.let { r ->
                val tail = "ze $word"; ic.beginBatchEdit(); ic.deleteSurroundingText(tail.length + if (r.startsWith(",") && b.dropLast(tail.length).endsWith(" ")) 1 else 0, 0)
                ic.commitText((if (r.startsWith(",")) r else r) + " " + word, 1); ic.endBatchEdit(); prevWord = "że"
            }
            Slownik.joinPair(p, word)?.takeIf { b.endsWith("$p $word", true) }?.let { j ->
                ic.beginBatchEdit(); ic.deleteSurroundingText(p.length + 1 + word.length, 0); ic.commitText(j, 1); ic.endBatchEdit()
                learn(j); return
            }
            if (Slownik.needsComma(word, p) && b.endsWith("$p $word")) {
                ic.beginBatchEdit(); ic.deleteSurroundingText(word.length + 1, 0); ic.commitText(", $word", 1); ic.endBatchEdit()
            }
        }
        learn(word)
        if (pref("klaw-czytaj", false)) speak(word)
    }

    private fun learn(wd: String) {
        if (wd.contains(' ')) { wd.split(' ').forEach { learn(it) }; return }
        if (wd.length < 2 || secret || !Slownik.loaded) { prevWord2 = prevWord; prevWord = wd.lowercase(); return }
        val lw = wd.lowercase()
        Slownik.learnPair(prevWord, lw); Slownik.learnTriple(prevWord2, prevWord, lw); prevWord2 = prevWord; prevWord = lw
        if (sentence.isNotEmpty()) sentence.append(' '); sentence.append(wd)
        synchronized(learned) { learned[lw] = (learned[lw] ?: 0) + 1 }
        if (lw != "ze" && !Slownik.isKnown(lw) && !Slownik.isTypo(lw) && lw !in own) addAsk = wd
        save(this)
    }

    private fun backspace() {
        val ic = currentInputConnection ?: return
        // ⌫ zaraz po poprawce przywraca to, co wpisano
        val f = fixFrom; val t = fixTo
        if (f != null && t != null && before(t.length + fixSep.length) == t + fixSep) {
            ic.beginBatchEdit(); ic.deleteSurroundingText(t.length + fixSep.length, 0); ic.commitText(f + fixSep, 1); ic.endBatchEdit()
            keep.add(f.lowercase()); fixFrom = null; addAsk = f; view.refresh(); return
        }
        fixFrom = null
        // ⌫ po przeciągnięciu - całe słowo i zapomnienie złego śladu
        lastSwipe?.let { s -> if (before(s.length + 1).let { it == "$s " || it.endsWith(s) }) {
            val n = if (before(s.length + 1) == "$s ") s.length + 1 else s.length
            ic.deleteSurroundingText(n, 0); Slownik.forgetShape(s); lastSwipe = null; cur = ""; autoShift(); view.refresh(); return } }
        val sel = ic.getSelectedText(0)
        if (!sel.isNullOrEmpty()) ic.commitText("", 1)
        else {
            val b = before(2)
            // emoji / znak z dwóch części - cały
            ic.deleteSurroundingText(if (b.length == 2 && Character.isSurrogatePair(b[0], b[1])) 2 else 1, 0)
        }
        cur = if (cur.isNotEmpty()) cur.dropLast(1) else before(30).takeLastWhile { it.isLetter() }
        autoShift(); view.refresh()
    }

    // ⌫ słowo
    private fun deleteWord() {
        val ic = currentInputConnection ?: return
        val b = before(60)
        val trimmed = b.trimEnd(); val n = b.length - trimmed.length + trimmed.takeLastWhile { !it.isWhitespace() }.length
        if (n > 0) ic.deleteSurroundingText(n, 0)
        cur = ""; fixFrom = null; autoShift(); view.refresh()
    }

    private fun enter() {
        val ic = currentInputConnection ?: return
        val info = currentInputEditorInfo
        val action = (info?.imeOptions ?: 0) and EditorInfo.IME_MASK_ACTION
        if (cur.isNotEmpty() && !secret) { val w = cur; cur = ""; afterWord(w) }
        if (!secret && sentence.isNotEmpty()) { Slownik.learnSentence(sentence.toString()); sentence.clear() }
        prevWord = null; prevWord2 = null
        if (info != null && action != EditorInfo.IME_ACTION_NONE && action != EditorInfo.IME_ACTION_UNSPECIFIED && info.imeOptions and EditorInfo.IME_FLAG_NO_ENTER_ACTION == 0) ic.performEditorAction(action)
        else ic.commitText("\n", 1)
        autoShift(); view.refresh()
    }

    // propozycja z paska: podmienia bieżące (albo przeciągnięte) słowo
    private fun pick(s: String) {
        val ic = currentInputConnection ?: return
        val swipe = lastSwipe
        if (swipe != null && before(swipe.length + 1) == "$swipe ") {
            ic.beginBatchEdit(); ic.deleteSurroundingText(swipe.length + 1, 0); ic.commitText("$s ", 1); ic.endBatchEdit()
            Slownik.forgetShape(swipe); Slownik.learnShape(s, lastTrace, lastKeyW); lastSwipe = s
        } else if (cur.isNotEmpty()) {
            ic.beginBatchEdit(); ic.deleteSurroundingText(cur.length, 0); ic.commitText("$s ", 1); ic.endBatchEdit(); cur = ""
        } else {
            val b = before(1)
            ic.commitText((if (b.isNotEmpty() && !b.endsWith(" ") && !b.endsWith("\n")) " " else "") + s + " ", 1)
        }
        learn(s); autoShift(); view.refresh()
    }

    // pisanie przeciągnięciem: rozpoznane słowo + spacja (przed słowem spacja, gdy jej brak)
    private fun swiped(trace: List<Slownik.P>, keys: Map<Char, Slownik.P>, keyW: Double) {
        if (!Slownik.loaded) return
        val r = Slownik.recognize(trace, keys, keyW, synchronized(learned) { HashMap(learned) }, 4, prevWord)
        val w0 = r.firstOrNull() ?: return
        val ic = currentInputConnection ?: return
        if (cur.isNotEmpty()) finishWord(" ")
        var w = w0
        if (shift xor caps) w = w[0].uppercaseChar() + w.substring(1)
        if (caps) w = w.uppercase()
        val b = before(1)
        ic.commitText((if (b.isNotEmpty() && !b.endsWith(" ") && !b.endsWith("\n")) " " else "") + w + " ", 1)
        if (!caps) shift = false
        lastSwipe = w; lastTrace = trace; lastKeyW = keyW; swipeAlts = r.drop(1)
        if (!secret) { Slownik.learnShape(w, trace, keyW); learn(w) }
        autoShift(); view.refresh()
    }
    private var swipeAlts: List<String> = emptyList()

    private fun suggestions(): List<String> {
        if (secret || !Slownik.loaded) return emptyList()
        lastSwipe?.let { if (swipeAlts.isNotEmpty()) return swipeAlts.take(3) }
        if (cur.isNotEmpty()) {
            val p = Slownik.predict(cur, false, 3).toMutableList()
            Slownik.suggest(cur, keep, 3).forEach { if (p.size < 3 && it !in p) p.add(it) }
            Slownik.joinNie(prevWord, cur)?.let { p.add(0, it) }
            return p.take(3)
        }
        return Slownik.nextWords(prevWord, prevWord2, 3)
    }

    // --- mowa: czytanie na głos i dyktowanie ---
    private fun speak(t: String) {
        val loc = if (lang(this) == "pl") Locale("pl", "PL") else Locale.ENGLISH
        val e = tts
        if (e == null) tts = TextToSpeech(this) { ok -> if (ok == TextToSpeech.SUCCESS) { tts?.language = loc; tts?.speak(t, TextToSpeech.QUEUE_ADD, null, "v") } }
        else { e.language = loc; e.speak(t, TextToSpeech.QUEUE_ADD, null, "v") }
    }

    private fun toggleMic() {
        if (listening) { stopListening(); return }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            // zgodę na mikrofon daje się w ustawieniach klawiatury (klawiatura nie może sama zapytać)
            startActivity(Intent(this, Ustawienia::class.java).setAction(ACTION_MIC).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)); return
        }
        if (!SpeechRecognizer.isRecognitionAvailable(this)) { Toast.makeText(this, R.string.kb_mic_none, Toast.LENGTH_LONG).show(); return }
        val r = rec ?: SpeechRecognizer.createSpeechRecognizer(this).also { rec = it }
        r.setRecognitionListener(object : RecognitionListener {
            override fun onResults(b: Bundle) {
                listening = false
                val t = b.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()
                if (!t.isNullOrBlank()) {
                    val ic = currentInputConnection
                    if (cur.isNotEmpty()) finishWord(" ")
                    val before = before(1)
                    var text = t
                    if (shift) text = text[0].uppercaseChar() + text.substring(1)
                    ic?.commitText((if (before.isNotEmpty() && !before.endsWith(" ") && !before.endsWith("\n")) " " else "") + text + " ", 1)
                    if (!secret) text.split(' ').forEach { learn(it) }
                    shift = false; autoShift()
                }
                view.refresh()
            }
            override fun onError(error: Int) { listening = false; view.refresh() }
            override fun onReadyForSpeech(p: Bundle?) {}
            override fun onBeginningOfSpeech() {}
            override fun onRmsChanged(v: Float) {}
            override fun onBufferReceived(b: ByteArray?) {}
            override fun onEndOfSpeech() {}
            override fun onPartialResults(b: Bundle?) {}
            override fun onEvent(t: Int, b: Bundle?) {}
        })
        r.startListening(Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH)
            .putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
            .putExtra(RecognizerIntent.EXTRA_LANGUAGE, if (lang(this) == "pl") "pl-PL" else "en-US"))
        listening = true; view.refresh()
    }
    private fun stopListening() { if (listening) { rec?.stopListening(); listening = false; if (::view.isInitialized) view.refresh() } }

    private fun tip(pl: Int) { Toast.makeText(this, pl, Toast.LENGTH_SHORT).show() }

    // --- widok klawiatury: jedno płótno (szybkie), wygląd jak na Windows ---
    @SuppressLint("ViewConstructor")
    private inner class Klawisze(ctx: Context) : View(ctx) {
        inner class K(val label: String, val code: String, val w: Float) { val r = RectF() }
        private val dp = resources.displayMetrics.density
        private val rows = listOf("1234567890-=", "qwertyuiop[]", "asdfghjkl;'", "zxcvbnm,./")
        private val keys = ArrayList<K>()
        private val head = ArrayList<K>()     // nagłówek: przełączniki
        private val sugR = ArrayList<Pair<RectF, String>>()
        private var sugList: List<String> = emptyList()
        private val p = Paint(Paint.ANTI_ALIAS_FLAG)
        private val tp = Paint(Paint.ANTI_ALIAS_FLAG).apply { textAlign = Paint.Align.CENTER }
        private var pressed: K? = null
        private var downX = 0f; private var downY = 0f; private var downAt = 0L
        private val trace = ArrayList<Slownik.P>(); private var swiping = false
        private var waveX = 0f; private var waveY = 0f; private var waveAt = 0L
        private var longDone = false

        // motywy jak w Velivo: ciemny (logo), jasny, czarno-biały (wybór w ustawieniach klawiatury)
        private var bg1 = 0; private var bg2 = 0; private var bg3 = 0; private var keyBg = 0; private var keyFn = 0; private var text = 0; private var dim = 0; private var border = 0
        private val violet = Color.rgb(0x8B, 0x5C, 0xF6); private val cyan = Color.rgb(0x06, 0xB6, 0xD4); private val mid = Color.rgb(0x4F, 0x6B, 0xED)
        private var contrast = false

        fun theme() {
            val m = prefs(context).getInt("motyw", 2)
            val night = (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES
            contrast = m == 3
            val dark = m == 2 || m == 3 || (m == 0 && night)
            if (contrast) { bg1 = Color.BLACK; bg2 = Color.BLACK; bg3 = Color.BLACK; keyBg = Color.BLACK; keyFn = Color.BLACK; text = Color.WHITE; dim = Color.WHITE; border = Color.WHITE }
            else if (dark) { bg1 = Color.rgb(0x1E, 0x18, 0x38); bg2 = Color.rgb(0x19, 0x1B, 0x33); bg3 = Color.rgb(0x13, 0x2A, 0x35); keyBg = Color.argb(0x14, 255, 255, 255); keyFn = Color.argb(0x22, 255, 255, 255); text = Color.WHITE; dim = Color.argb(0xB0, 255, 255, 255); border = 0 }
            else { bg1 = Color.rgb(0xF1, 0xEC, 0xFF); bg2 = Color.rgb(0xEC, 0xF0, 0xFB); bg3 = Color.rgb(0xE3, 0xF6, 0xF8); keyBg = Color.WHITE; keyFn = Color.rgb(0xDD, 0xD8, 0xF0); text = Color.rgb(0x1E, 0x1B, 0x33); dim = Color.rgb(0x55, 0x50, 0x70); border = 0 }
            invalidate()
        }

        init { theme(); isHapticFeedbackEnabled = true }

        fun refresh() { sugList = suggestions(); layoutKeys(width.toFloat()); invalidate() }

        private val headH get() = 38 * dp
        private val sugH get() = 40 * dp
        private val keyH get() = 50 * dp

        override fun onMeasure(ws: Int, hs: Int) {
            setMeasuredDimension(MeasureSpec.getSize(ws), (headH + sugH + keyH * 5 + 10 * dp).toInt())
        }
        override fun onSizeChanged(w: Int, h: Int, ow: Int, oh: Int) { layoutKeys(w.toFloat()) }

        private fun layoutKeys(width: Float) {
            if (width <= 0) return
            keys.clear(); head.clear()
            val pad = 4 * dp
            // nagłówek: ✓ autokorekta, 🔈 klik, 📖 Mój słownik, 🎤 dyktowanie, 🔊 czytanie, PL/EN, ⌄ schowaj
            val hs = listOf("✓" to "fix", "🔈" to "klik", "📖" to "words", (if (listening) "⏺" else "🎤") to "mic", "🔊" to "read", lang(context).uppercase() to "lang", "⌄" to "hide")
            var x = width - pad
            val bw = min(40 * dp, (width - 44 * dp) / hs.size - 2 * dp)   // przyciski mieszczą się obok logo na każdym telefonie
            for ((l, c) in hs.reversed()) { val k = K(l, c, bw); k.r.set(x - k.w, 4 * dp, x, headH - 2 * dp); head.add(k); x -= k.w + 2 * dp }
            var y = headH + sugH
            // 4 rzędy jak na Windows; ostatni z ⇧ i ⌫
            val lay = listOf(
                rows[0].map { K(it.toString(), it.toString(), 1f) },
                rows[1].map { K(it.toString(), it.toString(), 1f) },
                listOf(K("⇪", "caps", 1f)) + rows[2].map { K(it.toString(), it.toString(), 1f) },
                listOf(K("⇧", "shift", 1.5f)) + rows[3].map { K(it.toString(), it.toString(), 1f) } + listOf(K("⌫", "bs", 1.5f)),
                listOf(K("ą", "alt", 1.3f), K("?", "?", 1f), K(if (lang(context) == "pl") "Polski" else "English", "space", 5.2f), K("⌫ " + getString(R.string.kb_word), "bsw", 2f), K("⏎", "enter", 1.6f)))
            for (row in lay) {
                val units = row.sumOf { it.w.toDouble() }.toFloat()
                val u = (width - pad * 2) / max(units, 10.5f)
                var kx = pad + ((width - pad * 2) - u * units) / 2
                for (k in row) { k.r.set(kx + 2 * dp, y + 3 * dp, kx + u * k.w - 2 * dp, y + keyH - 3 * dp); kx += u * k.w; keys.add(k) }
                y += keyH
            }
            sugR.clear()
            val sw = (width - pad * 2) / 3
            sugList.take(3).forEachIndexed { i, s -> sugR.add(RectF(pad + sw * i, headH, pad + sw * (i + 1), headH + sugH) to s) }
            addAsk?.let { a -> if (sugList.size < 3) sugR.add(RectF(pad + sw * sugList.size.coerceAtMost(2), headH, width - pad, headH + sugH) to "➕$a") }
        }

        private fun label(k: K): String {
            val c = k.code
            if (c.length == 1) {
                var ch = c[0]
                if (alt) { val i = "acelnoszx".indexOf(ch); if (i >= 0) ch = "ąćęłńóśżź"[i] }
                if (ch.isLetter()) return (if (shift xor caps) ch.uppercaseChar() else ch).toString()
                if (shift && !shiftAuto) { val i = "1234567890-=[];',./".indexOf(ch); if (i >= 0) return "!@#$%^&*()_+{}:\"<>?"[i].toString() }
                return ch.toString()
            }
            return k.label
        }

        override fun onDraw(cv: Canvas) {
            val w = width.toFloat(); val h = height.toFloat()
            p.alpha = 255   // farba z gradientem bierze przezroczystość z poprzedniego klawisza - bez tego całość była blada
            p.shader = LinearGradient(0f, 0f, w, h, intArrayOf(bg1, bg2, bg3), floatArrayOf(0f, 0.55f, 1f), Shader.TileMode.CLAMP)
            cv.drawRect(0f, 0f, w, h, p); p.shader = null
            if (!contrast) { p.style = Paint.Style.STROKE; p.strokeWidth = dp; p.shader = LinearGradient(0f, 0f, w, 0f, Color.argb(0x90, 0x8B, 0x5C, 0xF6), Color.argb(0x90, 0x06, 0xB6, 0xD4), Shader.TileMode.CLAMP); cv.drawLine(0f, 0.5f, w, 0.5f, p); p.shader = null; p.style = Paint.Style.FILL }
            // logo: zaokrąglony kwadrat w gradiencie, białe V; „Velivo by Michael”
            val lg = RectF(8 * dp, 8 * dp, 8 * dp + 24 * dp, 8 * dp + 24 * dp)
            p.alpha = 255; p.shader = LinearGradient(lg.left, lg.top, lg.right, lg.bottom, intArrayOf(violet, mid, cyan), null, Shader.TileMode.CLAMP)
            cv.drawRoundRect(lg, 6 * dp, 6 * dp, p); p.shader = null
            tp.color = Color.WHITE; tp.textSize = 16 * dp; tp.typeface = Typeface.DEFAULT_BOLD
            cv.drawText("V", lg.centerX(), lg.centerY() + 6 * dp, tp)
            tp.textAlign = Paint.Align.LEFT; tp.textSize = 13 * dp; tp.color = text
            val room = (head.minOfOrNull { it.r.left } ?: w) - 6 * dp
            val vw = tp.measureText("Velivo")
            if (lg.right + 8 * dp + vw < room) cv.drawText("Velivo", lg.right + 8 * dp, lg.centerY() + 5 * dp, tp)
            tp.typeface = Typeface.DEFAULT; tp.color = dim
            if (lg.right + 8 * dp + vw + tp.measureText(" by Michael") < room) cv.drawText(" by Michael", lg.right + 8 * dp + vw, lg.centerY() + 5 * dp, tp)
            tp.textAlign = Paint.Align.CENTER
            for (k in head) {
                val on = when (k.code) { "fix" -> pref("klaw-korekta"); "klik" -> pref("klaw-klik"); "read" -> pref("klaw-czytaj", false); "mic" -> listening; else -> false }
                key(cv, k.r, on || k === pressed, fn = true)
                tp.textSize = (if (k.code == "lang") 12 else 15) * dp; tp.color = if (on) Color.WHITE else text; tp.typeface = Typeface.DEFAULT_BOLD
                cv.drawText(k.label, k.r.centerX(), k.r.centerY() + 5 * dp, tp)
            }
            // pasek propozycji
            tp.typeface = Typeface.DEFAULT; tp.textSize = 17 * dp
            sugR.forEachIndexed { i, (r, s) ->
                if (i > 0) { p.color = dim; p.alpha = 70; cv.drawRect(r.left, r.top + 10 * dp, r.left + dp, r.bottom - 10 * dp, p); p.alpha = 255 }
                tp.color = text
                val t = if (s.startsWith("➕")) "➕ " + getString(R.string.kb_add, s.substring(1)) else s
                cv.drawText(fit(t, r.width() - 8 * dp), r.centerX(), r.centerY() + 6 * dp, tp)
            }
            // klawisze
            for (k in keys) {
                val on = k === pressed && !swiping || k.code == "shift" && (shift || caps) || k.code == "caps" && caps || k.code == "alt" && alt
                key(cv, k.r, on, fn = k.code.length > 1 || k.code == "?")
                val l = label(k)
                tp.textSize = (if (l.length > 2) 14 else 20) * dp; tp.color = if (on) Color.WHITE else text
                cv.drawText(l, k.r.centerX(), k.r.centerY() + 7 * dp, tp)
                // mała podpowiedź znaku z ⇧ / polskiej litery w rogu
                val hint = if (k.code.length == 1) { val i = "acelnoszx".indexOf(k.code[0]); if (i >= 0 && !alt) "ąćęłńóśżź"[i].toString() else { val j = "1234567890-=[];',./".indexOf(k.code[0]); if (j >= 0 && !(shift && !shiftAuto)) "!@#$%^&*()_+{}:\"<>?"[j].toString() else "" } } else ""
                if (hint.isNotEmpty()) { tp.textSize = 10 * dp; tp.color = dim; cv.drawText(hint, k.r.right - 7 * dp, k.r.top + 12 * dp, tp) }
            }
            // ślad przeciągania w gradiencie logo
            if (swiping && trace.size > 1) {
                val path = Path(); path.moveTo(trace[0].x.toFloat(), trace[0].y.toFloat()); trace.forEach { path.lineTo(it.x.toFloat(), it.y.toFloat()) }
                p.style = Paint.Style.STROKE; p.strokeWidth = 7 * dp; p.strokeCap = Paint.Cap.ROUND; p.strokeJoin = Paint.Join.ROUND
                p.shader = LinearGradient(0f, 0f, w, 0f, violet, cyan, Shader.TileMode.CLAMP); p.alpha = 217
                cv.drawPath(path, p); p.shader = null; p.style = Paint.Style.FILL; p.alpha = 255
            }
            // fala jak na wodzie od naciśniętego klawisza (0,4 s)
            val t = (SystemClock.uptimeMillis() - waveAt) / 400f
            if (t in 0f..1f && !contrast) {
                val rad = 8 * dp + 60 * dp * t
                p.style = Paint.Style.STROKE; p.strokeWidth = 3 * dp
                p.shader = RadialGradient(waveX, waveY, rad, intArrayOf(violet, cyan), null, Shader.TileMode.CLAMP); p.alpha = ((1 - t) * 200).toInt()
                cv.drawCircle(waveX, waveY, rad, p); p.shader = null; p.style = Paint.Style.FILL; p.alpha = 255
                postInvalidateOnAnimation()
            }
        }

        private fun fit(s: String, maxW: Float): String { var t = s; while (t.length > 2 && tp.measureText(t) > maxW) t = t.dropLast(2) + "…"; return t }

        // klawisz ze szkła; włączony / wciśnięty - gradient logo (fiolet -> turkus)
        private fun key(cv: Canvas, r: RectF, on: Boolean, fn: Boolean) {
            if (on) { p.alpha = 255; p.shader = LinearGradient(r.left, 0f, r.right, 0f, Color.rgb(0x7C, 0x3A, 0xED), cyan, Shader.TileMode.CLAMP) }
            else if (!contrast && !fn) {
                // kolory przechodzą po klawiszach jak w logo: lewy górny fiolet -> prawy dolny turkus (subtelnie)
                val f = ((r.centerX() / max(1, width)) + (r.centerY() / max(1, height))) / 2
                val c = if (f < 0.5f) blend(violet, mid, f * 2) else blend(mid, cyan, (f - 0.5f) * 2)
                p.color = if (bg1 == Color.rgb(0x1E, 0x18, 0x38)) Color.argb(0x24, Color.red(c), Color.green(c), Color.blue(c)) else keyBg
            } else p.color = if (fn) keyFn else keyBg
            cv.drawRoundRect(r, 8 * dp, 8 * dp, p); p.shader = null
            if (contrast) { p.style = Paint.Style.STROKE; p.strokeWidth = 1.5f * dp; p.color = border; cv.drawRoundRect(r, 8 * dp, 8 * dp, p); p.style = Paint.Style.FILL }
        }
        private fun blend(a: Int, b: Int, t: Float) = Color.rgb((Color.red(a) + (Color.red(b) - Color.red(a)) * t).toInt(), (Color.green(a) + (Color.green(b) - Color.green(a)) * t).toInt(), (Color.blue(a) + (Color.blue(b) - Color.blue(a)) * t).toInt())

        private fun keyAt(x: Float, y: Float): K? = keys.firstOrNull { it.r.contains(x, y) } ?: keys.minByOrNull { hypot(it.r.centerX() - x, it.r.centerY() - y) }?.takeIf { y > headH + sugH }

        private val repeat = object : Runnable { override fun run() { if (pressed?.code == "bs" && !swiping) { backspace(); longDone = true; postDelayed(this, 60) } } }
        private val longPress = Runnable {
            val k = pressed ?: return@Runnable
            if (swiping) return@Runnable
            when {
                k.code == "bs" -> { longDone = true; post(repeat) }
                // przytrzymanie litery: polska litera (a -> ą), cyfry - znak z ⇧
                k.code.length == 1 && "acelnoszx".indexOf(k.code[0]) >= 0 -> { longDone = true; val was = alt; alt = true; type(k.code[0]); alt = was; performHapticFeedback(HAPTIC_FEEDBACK_ENABLED) }
                k.code.length == 1 && "1234567890-=[];',./".indexOf(k.code[0]) >= 0 -> { longDone = true; val was = shift; val wa = shiftAuto; shift = true; shiftAuto = false; type(k.code[0]); shift = was; shiftAuto = wa }
                k.code == "space" -> { longDone = true; (getSystemService(INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager).showInputMethodPicker() }
            }
        }

        @SuppressLint("ClickableViewAccessibility")
        override fun onTouchEvent(e: MotionEvent): Boolean {
            val x = e.x; val y = e.y
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downX = x; downY = y; downAt = SystemClock.uptimeMillis(); longDone = false; swiping = false; trace.clear()
                    if (y < headH) { pressed = head.firstOrNull { it.r.contains(x, y) }; pressed?.let { k -> postDelayed({ if (pressed === k) { longDone = true; tip(headTip(k.code)) } }, 500) }; invalidate(); return true }
                    if (y < headH + sugH) { pressed = null; return true }
                    pressed = keyAt(x, y); trace.add(Slownik.P(x.toDouble(), y.toDouble()))
                    click(); performHapticFeedback(HAPTIC_FEEDBACK_ENABLED)
                    waveX = pressed?.r?.centerX() ?: x; waveY = pressed?.r?.centerY() ?: y; waveAt = SystemClock.uptimeMillis()
                    postDelayed(longPress, 420)
                    invalidate()
                }
                MotionEvent.ACTION_MOVE -> {
                    val k = pressed ?: return true
                    if (downY < headH + sugH) return true
                    trace.add(Slownik.P(x.toDouble(), y.toDouble()))
                    val kw = (keys.firstOrNull { it.code == "q" }?.r?.width() ?: 40f).toDouble()
                    // przeciąganie od litery: palec odjechał dalej niż pół klawisza
                    if (!swiping && !longDone && k.code.length == 1 && k.code[0] in 'a'..'z' && !secret && hypot(x - downX, y - downY) > kw * 0.6) { swiping = true; removeCallbacks(longPress) }
                    if (swiping) invalidate()
                }
                MotionEvent.ACTION_UP -> {
                    removeCallbacks(longPress); removeCallbacks(repeat)
                    val k = pressed; pressed = null
                    if (downY < headH) { if (k != null && !longDone && k.r.contains(x, y)) headAction(k.code); invalidate(); return true }
                    if (downY < headH + sugH) { sugR.firstOrNull { it.first.contains(x, y) }?.second?.let { s -> if (s.startsWith("➕")) addWord(s.substring(1)) else pick(s) }; return true }
                    if (swiping) {
                        swiping = false
                        val centers = HashMap<Char, Slownik.P>()
                        keys.filter { it.code.length == 1 && it.code[0] in 'a'..'z' }.forEach { centers[it.code[0]] = Slownik.P(it.r.centerX().toDouble(), it.r.centerY().toDouble()) }
                        val kw = (keys.first { it.code == "w" }.r.centerX() - keys.first { it.code == "q" }.r.centerX()).toDouble()
                        swiped(ArrayList(trace), centers, kw); trace.clear(); invalidate(); return true
                    }
                    if (k != null && !longDone) keyAction(k)
                    invalidate()
                }
                MotionEvent.ACTION_CANCEL -> { removeCallbacks(longPress); removeCallbacks(repeat); pressed = null; swiping = false; trace.clear(); invalidate() }
            }
            return true
        }

        private fun headTip(c: String) = when (c) { "fix" -> R.string.kb_fix; "klik" -> R.string.kb_click; "words" -> R.string.kb_words; "mic" -> R.string.kb_mic; "read" -> R.string.kb_read; "lang" -> R.string.kb_lang; else -> R.string.kb_hide }

        private fun headAction(c: String) {
            val pr = prefs(context)
            when (c) {
                "fix" -> pr.edit().putBoolean("klaw-korekta", !pref("klaw-korekta")).apply()
                "klik" -> pr.edit().putBoolean("klaw-klik", !pref("klaw-klik")).apply()
                "read" -> pr.edit().putBoolean("klaw-czytaj", !pref("klaw-czytaj", false)).apply()
                "words" -> startActivity(Intent(context, Ustawienia::class.java).setAction(ACTION_WORDS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                "mic" -> toggleMic()
                "lang" -> { pr.edit().putString("klaw-jezyk", if (lang(context) == "pl") "en" else "pl").apply(); load(context) { refresh() } }
                "hide" -> requestHideSelf(0)
            }
            refresh()
        }

        private fun addWord(w: String) {
            synchronized(learned) { own.add(w.lowercase()) }
            Slownik.addUserWord(w); keep.add(w.lowercase()); addAsk = null; save(context)
            Toast.makeText(context, getString(R.string.kb_added, w), Toast.LENGTH_SHORT).show(); refresh()
        }

        private fun keyAction(k: K) {
            when (k.code) {
                "shift" -> { if (caps) { caps = false; shift = false } else shift = !shift; shiftAuto = false; refresh() }
                "caps" -> { caps = !caps; shift = caps; shiftAuto = false; refresh() }
                "alt" -> { alt = !alt; refresh() }
                "bs" -> backspace()
                "bsw" -> deleteWord()
                "enter" -> enter()
                "space" -> { lastSwipe?.let { if (before(1) == " ") { lastSwipe = null; refresh(); return } }; type(' ') }
                else -> type(k.code[0])
            }
        }
    }
}
