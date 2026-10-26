package pl.velivo.klawiatura

import pl.velivo.komunikator.*
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
import android.view.KeyEvent
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

        // 🔄 nauka dla drugiej klawiatury Velivo (przez przeglądarkę Velivo i jej synchronizację w domowej sieci)
        fun syncExport(ctx: Context): String {
            loadNow(ctx)
            val o = org.json.JSONObject()
            synchronized(learned) { o.put("own", org.json.JSONArray(own.toList())) }
            o.put("nofix", org.json.JSONArray((prefs(ctx).getStringSet("bez-korekty", emptySet()) ?: emptySet()).toList()))
            o.put("pairs", org.json.JSONObject(Slownik.syncPairs() as Map<*, *>)).put("fix", org.json.JSONObject(Slownik.syncFixes() as Map<*, *>))
            return o.toString()
        }
        fun syncImport(ctx: Context, json: String) {
            val o = runCatching { org.json.JSONObject(json) }.getOrNull() ?: return
            loadNow(ctx); if (!Slownik.loaded) return   // najpierw cała dotychczasowa nauka - zapis nigdy jej nie nadpisze pustą
            o.optJSONArray("own")?.let { a -> synchronized(learned) { for (i in 0 until minOf(a.length(), 5000)) { val w = a.optString(i).trim().lowercase(); if (w.length in 2..40 && !w.contains(' ') && own.add(w)) Slownik.addUserWord(w) } } }
            o.optJSONArray("nofix")?.let { a -> val set = HashSet(prefs(ctx).getStringSet("bez-korekty", emptySet()) ?: emptySet()); for (i in 0 until a.length()) a.optString(i).takeIf { it.length in 2..40 }?.let { set.add(it) }; prefs(ctx).edit().putStringSet("bez-korekty", HashSet(set.toList().takeLast(500))).apply() }
            val p = HashMap<String, Int>(); o.optJSONObject("pairs")?.let { j -> j.keys().forEach { k -> p[k] = j.optInt(k) } }
            val fx = HashMap<String, String>(); o.optJSONObject("fix")?.let { j -> j.keys().forEach { k -> fx[k] = j.optString(k) } }
            Slownik.syncMerge(p, fx)
            save(ctx)
        }
        // wczytanie słownika i nauki od razu (wołane spoza klawiatury, np. przy wymianie nauki)
        private fun loadNow(ctx: Context) {
            if (Slownik.loaded && loadedLang == lang(ctx)) return
            val l = java.util.concurrent.CountDownLatch(1); load(ctx) { l.countDown() }; l.await(20, java.util.concurrent.TimeUnit.SECONDS)
        }

        fun lang(ctx: Context): String = prefs(ctx).getString("klaw-jezyk", null) ?: if (Locale.getDefault().language == "pl") "pl" else "en"
        fun prefs(ctx: Context) = ctx.getSharedPreferences("klawiatura", Context.MODE_PRIVATE)
        // 👵 tryb Senior jednym przełącznikiem: wysokie klawisze i duże litery, powiększona litera nad palcem, słowa czytane na głos,
        // bez pisania przeciągnięciem i z dłuższym przytrzymaniem (drżący palec nie robi przypadkowych słów i znaków); wyłączenie - klawisze jak dawniej
        fun senior(ctx: Context, on: Boolean) {
            val e = prefs(ctx).edit().putBoolean("klaw-senior", on)
            if (on) e.putInt("wysokosc", maxOf(prefs(ctx).getInt("wysokosc", 120), 150)).putBoolean("klaw-podglad", true).putBoolean("klaw-czytaj", true)
            else e.putInt("wysokosc", minOf(prefs(ctx).getInt("wysokosc", 120), 120)).putBoolean("klaw-czytaj", false)
            e.apply()
        }
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
            runCatching {
                val text = synchronized(learned) { Slownik.saveText(learned, own) }
                file(ctx).writeBytes(Szyfr.seal(text.toByteArray(Charsets.UTF_8)))
            }
        }
        // ⌨ po słowie: zapis (cały słownik, szyfrowany) w tle i najwyżej raz na 3 s - wcześniej po każdej spacji w wątku klawiatury (przycięcia)
        private val saver = java.util.concurrent.Executors.newSingleThreadExecutor()
        @Volatile private var saveDue = false
        fun saveSoon(ctx: Context) {
            if (saveDue) return
            saveDue = true; val c = ctx.applicationContext
            Handler(Looper.getMainLooper()).postDelayed({ saver.execute { saveDue = false; save(c) } }, 3000)
        }
        fun saveNow(ctx: Context) { if (saveDue) { saveDue = false; val c = ctx.applicationContext; saver.execute { save(c) } } }

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
    private val keep by lazy { HashSet(prefs(this).getStringSet("bez-korekty", emptySet()) ?: emptySet()) }   // cofnięte poprawki (⌫) - zapamiętane na stałe, tych słów już nie poprawiamy
    private var addAsk: String? = null    // „Dodać do słownika?”
    private var sentence = StringBuilder()
    private var lastSwipe: String? = null; private var lastTrace: List<Slownik.P>? = null; private var lastKeyW = 1.0
    private var shift = false; private var caps = false; private var alt = false; private var shiftAuto = false
    private var sym = 0   // 0 - litery, 1 - znaki (?123), 2 - więcej znaków (=\<), 3 - emoji (😀), 4 - 📋 schowek i edycja, 5 - GIF-y z telefonu - jak w Gboard
    private var ecat = -1   // emoji: kategoria (0 - ostatnio używane)
    private var email = false   // pole adresu e-mail - przy spacji @ zamiast 😀
    // 📋 ostatnio skopiowane teksty (jak w Gboard): tylko w pamięci, najwyżej 8 i godzinę; bez tekstów oznaczonych jako poufne (hasła z menedżerów haseł)
    private val clips = ArrayList<Pair<String, Long>>()
    private val clipListener = android.content.ClipboardManager.OnPrimaryClipChangedListener { grabClip() }
    private fun grabClip() {
        val cm = getSystemService(android.content.ClipboardManager::class.java) ?: return
        if (cm.primaryClipDescription?.extras?.getBoolean("android.content.extra.IS_SENSITIVE") == true) return
        val t = runCatching { cm.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this)?.toString() }.getOrNull()?.trim().orEmpty()
        if (t.isEmpty() || t.length > 5000) return
        clips.removeAll { it.first == t }; clips.add(0, t to System.currentTimeMillis()); while (clips.size > 8) clips.removeAt(clips.size - 1)
    }
    private fun recentClips(): List<String> { val old = System.currentTimeMillis() - 3600000L; clips.removeAll { it.second < old }; return clips.map { it.first } }

    // 😀 emoji w kategoriach (jak w Gboard / SwiftKey) i ostatnio używane (zapamiętane)
    private val emojiCats = listOf("😀😃😄😁😆😅🤣😂🙂😉😊😇🥰😍🤩😘😋😜🤪🤗🤔🤭😎🥳", "😐😏😒🙄😔😴😷🤒🤕🤢🤧🥵🥶🤯😕😟😮😳🥺😢😭😱😤😡",
        "👍👎👌✌️🤞🤟🤘👋🤙👏🙌🙏🤝💪👆👇👈👉✋🖐️🤲👶👵👴", "❤️🧡💛💚💙💜🖤🤍💔💕💞💓💗💖💘💝✨⭐🌟💫🔥💯✅❌",
        "🐶🐱🐭🐰🦊🐻🐼🐨🐯🦁🐮🐷🐸🐵🐔🐦🦋🐝🐟🌸🌹🌻🌳☀️", "🍎🍐🍊🍋🍌🍉🍇🍓🍒🍑🥝🍅🥕🌽🍞🧀🍕🍔🍟🍰🎂🍫☕🍷",
        "🚗🚕🚌🚑🚒🚓🚲✈️🚆🚢🏠🏡🏥🏫⛪🏖️⛰️🌍🌙⛅🌧️❄️🌈⏰", "🎉🎁🎈🎄⚽🏀🎾🎮🎵🎶📱💻📷📺📞✉️📅📌✏️📖💊💡🔑💰")
        .map { s -> Regex("\\X").findAll(s).map { it.value }.toList() }
    private fun recentEmoji(): List<String> = prefs(this).getString("emoji-ostatnie", "").orEmpty().split(' ').filter { it.isNotEmpty() }
    private fun useEmoji(e: String) = prefs(this).edit().putString("emoji-ostatnie", (listOf(e) + recentEmoji().filter { it != e }).take(24).joinToString(" ")).apply()

    // 🎞 GIF-y z telefonu (Pobrane, Obrazy) - bez cudzych serwerów (Gboard szuka w Tenor Google); wysyłane do aplikacji, która je przyjmuje (WhatsApp, Wiadomości…)
    private var gifs: List<Pair<android.net.Uri, android.graphics.Bitmap?>> = emptyList()
    private fun gifAllowed() = checkSelfPermission(if (android.os.Build.VERSION.SDK_INT >= 33) Manifest.permission.READ_MEDIA_IMAGES else Manifest.permission.READ_EXTERNAL_STORAGE) == PackageManager.PERMISSION_GRANTED
    private fun loadGifs() {
        if (!gifAllowed()) return
        Thread {
            val out = ArrayList<Pair<android.net.Uri, android.graphics.Bitmap?>>()
            runCatching {
                val base = android.provider.MediaStore.Images.Media.EXTERNAL_CONTENT_URI
                contentResolver.query(base, arrayOf(android.provider.MediaStore.Images.Media._ID), android.provider.MediaStore.Images.Media.MIME_TYPE + "=?", arrayOf("image/gif"),
                    android.provider.MediaStore.Images.Media.DATE_ADDED + " DESC")?.use { c ->
                    while (c.moveToNext() && out.size < 16) {
                        val u = android.content.ContentUris.withAppendedId(base, c.getLong(0))
                        out.add(u to runCatching { contentResolver.loadThumbnail(u, android.util.Size(240, 240), null) }.getOrNull())
                    }
                }
            }
            ui.post { gifs = out; if (::view.isInitialized && sym == 5) view.refresh() }
        }.start()
    }
    private fun sendGif(src: android.net.Uri) {
        val types = currentInputEditorInfo?.contentMimeTypes ?: emptyArray()
        if (types.none { android.content.ClipDescription.compareMimeTypes("image/gif", it) }) { tip(R.string.kb_gif_no); return }
        Thread {
            val dir = File(cacheDir, "gif").apply { mkdirs() }
            dir.listFiles()?.filter { System.currentTimeMillis() - it.lastModified() > 86400000L }?.forEach { it.delete() }   // kopie starsze niż dzień - usuwane
            val f = File(dir, "velivo-" + System.currentTimeMillis() + ".gif")
            if (runCatching { contentResolver.openInputStream(src)!!.use { i -> f.outputStream().use { i.copyTo(it) } } }.isFailure) return@Thread
            ui.post {
                currentInputConnection?.commitContent(android.view.inputmethod.InputContentInfo(android.net.Uri.parse("content://" + GifDane.AUTH + "/" + f.name), android.content.ClipDescription("GIF", arrayOf("image/gif"))),
                    android.view.inputmethod.InputConnection.INPUT_CONTENT_GRANT_READ_URI_PERMISSION, null)
            }
        }.start()
    }
    private var listening = false
    private var afterPunct = false   // po , ; : ! ? następna litera dostaje spację przed sobą (kot,pies -> kot, pies)

    override fun onCreate() {
        super.onCreate()
        getSystemService(android.content.ClipboardManager::class.java)?.addPrimaryClipChangedListener(clipListener)
        Rozmowy.syncIcon(this)   // ikona komunikatora: w klawiaturze tylko bez przeglądarki Velivo
        load(this) { if (::view.isInitialized) view.refresh() }
    }

    override fun onCreateInputView(): View { view = Klawisze(this); return if (pref("brajl", false)) brajlPole() else view }

    // ⠿ pisanie brajlem: osobne pole 6 punktów (własny dotyk - zwykłe klawisze i gesty bez zmian); znaki idą przez zwykłe pisanie
    // (autokorekta, słownik, podpowiedzi, nauka słów, czytanie słów 🔊). Przy TalkBack system przejmuje dotyk - zamiast konfliktu gestów: komunikat
    private var brajlView: BrajlPole? = null
    private val brajlStan = Brajl.Stan()
    private fun brajlPole() = (brajlView ?: BrajlPole(this, ::brailleCell, ::brailleGesture).also { brajlView = it }).apply {
        hints = pref("brajl-podpowiedzi"); vibrate = pref("brajl-wibracje"); mirror = pref("brajl-odwrocone", false)
    }
    fun brailleOn() {
        if ((getSystemService(ACCESSIBILITY_SERVICE) as android.view.accessibility.AccessibilityManager).isTouchExplorationEnabled) {
            val t = getString(R.string.kb_braille_talkback); speak(t, true); Toast.makeText(this, t, Toast.LENGTH_LONG).show(); return
        }
        prefs(this).edit().putBoolean("brajl", true).apply(); brajlStan.reset()
        setInputView(brajlPole()); speak(getString(R.string.kb_braille_on), true)
    }
    private fun brailleOff() { prefs(this).edit().putBoolean("brajl", false).apply(); setInputView(view); view.refresh(); speak(getString(R.string.kb_braille_off), true) }
    private fun brailleCell(cell: Int) {
        val pl = lang(this) == "pl"
        val out = Brajl.decode(cell, pl, brajlStan)
        if (out == null) { if (pref("brajl-mowa")) speak(getString(R.string.kb_braille_unknown, Brajl.dotsText(cell)), true); return }
        if (out.isEmpty()) { if (pref("brajl-mowa")) speak(getString(if (brajlStan.num) R.string.kb_braille_number else R.string.kb_braille_capital), true); return }
        out.forEach { type(it) }
        if (pref("brajl-mowa")) speak(Brajl.spoken(out, pl), true)   // znak na głos (słowa czyta 🔊 jak przy zwykłych klawiszach)
    }
    private fun brailleGesture(g: String) {
        when (g) {
            "space" -> { brajlStan.reset(); type(' '); if (pref("brajl-mowa")) speak(Brajl.spoken(" ", lang(this) == "pl"), true) }
            "bs" -> { backspace(); if (pref("brajl-mowa")) speak(getString(R.string.kb_braille_deleted), true) }
            "bsword" -> deleteWord()
            "enter" -> enter()
            "read" -> speak(before(400).ifBlank { getString(R.string.kb_braille_empty) }, true)
            "exit" -> brailleOff()
        }
    }

    override fun onStartInputView(info: EditorInfo, restarting: Boolean) {
        super.onStartInputView(info, restarting)
        load(this) { view.refresh() }
        val cls = info.inputType and InputType.TYPE_MASK_CLASS
        val v = info.inputType and InputType.TYPE_MASK_VARIATION
        email = cls == InputType.TYPE_CLASS_TEXT && (v == InputType.TYPE_TEXT_VARIATION_EMAIL_ADDRESS || v == InputType.TYPE_TEXT_VARIATION_WEB_EMAIL_ADDRESS)
        secret = cls == InputType.TYPE_CLASS_TEXT && (v == InputType.TYPE_TEXT_VARIATION_PASSWORD || v == InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD || v == InputType.TYPE_TEXT_VARIATION_WEB_PASSWORD) ||
            cls == InputType.TYPE_CLASS_NUMBER && v == InputType.TYPE_NUMBER_VARIATION_PASSWORD ||
            info.imeOptions and EditorInfo.IME_FLAG_NO_PERSONALIZED_LEARNING != 0
        // ponowne uruchomienie tego samego pola (WhatsApp, przeglądarki robią to często w trakcie pisania) - stan pisania zostaje
        if (!restarting) { cur = ""; fixFrom = null; addAsk = null; lastSwipe = null; alt = false; caps = false; afterPunct = false
            prevWord = null; prevWord2 = null; sentence.clear() }
        autoShift()
        view.theme(); view.refresh(); if (pref("brajl", false)) brajlPole().invalidate()   // ⠿ ustawienia brajla z aplikacji klawiatury
        komNew()?.listen()   // telefon tylko z klawiaturą Velivo: widoczny w domowym Wi-Fi do dodania, póki klawiatura na ekranie
    }

    // komunikator klawiatury bez kontaktów (bez przeglądarki Velivo) - nasłuch tylko na czas pisania
    private fun komNew() = if (Rozmowy.hasBrowser(this)) null else Komunikator.get(this) { (android.os.Build.MANUFACTURER + " " + android.os.Build.MODEL).trim() }.takeIf { it.contacts.isEmpty() }

    // ➤ w komunikatorze Velivo: popraw ostatnie słowo przed wysłaniem
    override fun onAppPrivateCommand(action: String?, data: android.os.Bundle?) { if (action == "pl.velivo.slowo" && !secret) finishWord("") }
    override fun onFinishInputView(finishingInput: Boolean) { super.onFinishInputView(finishingInput); stopListening(); komNew()?.close(); saveNow(this) }   // klawiatura chowa się - zaległy zapis od razu
    override fun onDestroy() { getSystemService(android.content.ClipboardManager::class.java)?.removePrimaryClipChangedListener(clipListener); tts?.shutdown(); rec?.destroy(); super.onDestroy() }

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
        else if (shift && !shiftAuto && sym == 0) { val i = "1234567890-=[];',./".indexOf(ch); if (i >= 0) ch = "!@#$%^&*()_+{}:\"<>?"[i] }
        if (ch.isLetter()) {
            if (afterPunct && before(1).let { it.isNotEmpty() && it[0] in ",;:!?" }) ic.commitText(" ", 1)
            afterPunct = false
            ic.commitText(ch.toString(), 1); cur += ch
        }
        else { afterPunct = false; finishWord(ch.toString()) }
        if (shift && !caps) shift = false
        if (!ch.isLetter()) autoShift()
        view.refresh()
    }

    // koniec słowa (spacja, znak, Enter): autokorekta, przecinek, „że”, sklejanie, nauka
    private fun finishWord(sep: String) {
        val ic = currentInputConnection ?: return
        // dwie spacje -> kropka i spacja (jak na Windows): "kot  " -> "kot. "
        if (sep == " " && cur.isEmpty() && !secret) {
            val b = before(2)
            if (b.length == 2 && b[1] == ' ' && b[0].isLetterOrDigit()) {
                ic.beginBatchEdit(); ic.deleteSurroundingText(1, 0); ic.commitText(". ", 1); ic.endBatchEdit()
                if (sentence.isNotEmpty()) { Slownik.learnSentence(sentence.toString() + "."); sentence.clear() }
                prevWord = null; prevWord2 = null; afterPunct = false; autoShift(); return
            }
        }
        // słowo z tekstu przed kursorem (nie tylko z pamięci klawiatury - pole bywa uruchamiane od nowa w trakcie pisania)
        val w = before(48).takeLastWhile { it.isLetter() }.ifEmpty { cur }.takeIf { it.isNotEmpty() && before(it.length) == it } ?: ""; cur = ""
        if (prevWord == null && w.isNotEmpty()) before(120).dropLast(w.length).trimEnd().split(' ', '\n').lastOrNull { it.isNotEmpty() }?.trim(',', '.', '!', '?', ';', ':')?.takeIf { it.all(Char::isLetter) }?.let { prevWord = it.lowercase() }
        var out = w
        if (w.isNotEmpty() && !secret) {
            val fix = if (pref("klaw-korekta")) Slownik.complete(w, keep) ?: Slownik.contextFix(prevWord, w, keep) else null
            Slownik.zeNow(fix ?: w, prevWord)?.let { out = it } ?: run { if (fix != null) out = fix }
            if (out != w) {
                ic.beginBatchEdit(); ic.deleteSurroundingText(w.length, 0); ic.commitText(out, 1); ic.endBatchEdit()
                fixFrom = w; fixTo = out; fixSep = sep
            }
            afterWord(out)
        }
 
        // interpunkcja jak w Velivo na Windows: znak przylega do słowa ("kot ." -> "kot."), po , ; : ! ? spacja przed następnym słowem
        if (sep.length == 1 && sep[0] in ",.!?:;" && w.isEmpty()) {
            val b = before(2)
            if (b.length == 2 && b[1] == ' ' && (b[0].isLetterOrDigit() || b[0] in ")\"'")) ic.deleteSurroundingText(1, 0)
        }
        if (sep.isNotEmpty()) ic.commitText(sep, 1)
        afterPunct = sep.length == 1 && sep[0] in ",;:!?"
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
        saveSoon(this)
    }

    private fun backspace() {
        val ic = currentInputConnection ?: return
        // ⌫ zaraz po poprawce przywraca to, co wpisano
        val f = fixFrom; val t = fixTo
        if (f != null && t != null && before(t.length + fixSep.length) == t + fixSep) {
            ic.beginBatchEdit(); ic.deleteSurroundingText(t.length + fixSep.length, 0); ic.commitText(f + fixSep, 1); ic.endBatchEdit()
            keep.add(f.lowercase()); prefs(this).edit().putStringSet("bez-korekty", HashSet(keep.toList().takeLast(500))).apply()
            fixFrom = null; addAsk = f; view.refresh(); return
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
        if (!secret) finishWord("")   // ostatnie słowo przed Enter też poprawione (zolw⏎ -> żółw)
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
    private fun speak(t: String, flush: Boolean = false) {
        val loc = if (lang(this) == "pl") Locale("pl", "PL") else Locale.ENGLISH
        val e = tts
        val q = if (flush) TextToSpeech.QUEUE_FLUSH else TextToSpeech.QUEUE_ADD   // ⠿ brajl: nowy znak przerywa poprzedni (bez opóźnienia przy szybkim pisaniu)
        if (e == null) tts = TextToSpeech(this) { ok -> if (ok == TextToSpeech.SUCCESS) { tts?.language = loc; tts?.speak(t, q, null, "v") } }
        else { e.language = loc; e.speak(t, q, null, "v") }
    }

    private fun toggleMic() {
        if (listening) { stopListening(); return }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            // zgodę na mikrofon daje się w ustawieniach klawiatury (klawiatura nie może sama zapytać)
            startActivity(Intent(this, SzybkieUstawienia::class.java).setAction(ACTION_MIC).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)); return
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
        // 10 klawiszy w rzędzie (jak zwykłe klawiatury telefonu) - szersze, łatwiej trafić; znaki - przytrzymanie (podpowiedź w rogu)
        private val rows = listOf("1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm")
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
        private var shiftAt = 0L
        // znaki pod przytrzymaniem (bez dodatkowych klawiszy w rzędzie); litery z polskimi znakami (a c e l n o s z x) - polska litera
        private val longSym = mapOf('1' to '!', '2' to '@', '3' to '#', '4' to '$', '5' to '%', '6' to '^', '7' to '&', '8' to '*', '9' to '(', '0' to ')',
            'q' to '%', 'w' to '^', 'r' to '=', 't' to '[', 'y' to ']', 'u' to '{', 'i' to '}', 'p' to '_',
            'd' to '-', 'f' to '+', 'g' to '(', 'h' to ')', 'j' to ';', 'k' to ':',
            'v' to '\'', 'b' to '"', 'm' to '/', ',' to '!', '.' to '…')

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
            requestLayout(); invalidate()
        }

        init { theme(); isHapticFeedbackEnabled = true }

        fun refresh() { sugList = suggestions(); layoutKeys(width.toFloat()); invalidate() }

        private val headH get() = 38 * dp
        private val sugH get() = 40 * dp
        // wysokość klawiszy z ustawień (procent); domyślnie wyższe niż zwykle - wygodniej trafić palcem
        private val keyH get() = 50 * dp * prefs(context).getInt("wysokosc", 120) / 100f

        override fun onMeasure(ws: Int, hs: Int) {
            setMeasuredDimension(MeasureSpec.getSize(ws), (headH + sugH + keyH * 5 + 10 * dp).toInt())
        }
        override fun onSizeChanged(w: Int, h: Int, ow: Int, oh: Int) { layoutKeys(w.toFloat()) }

        private fun layoutKeys(width: Float) {
            if (width <= 0) return
            keys.clear(); head.clear()
            val pad = 4 * dp
            // nagłówek: ✓ autokorekta, 🔈 klik, 📖 Mój słownik, 🎤 dyktowanie, 🔊 czytanie, PL/EN, ⌄ schowaj
            val hs = listOf("📋" to "clip", "✓" to "fix", "🔈" to "klik", "📖" to "words", (if (listening) "⏺" else "🎤") to "mic", "🔊" to "read", lang(context).uppercase() to "lang", "⌄" to "hide")
            var x = width - pad
            val bw = min(40 * dp, (width - 44 * dp) / hs.size - 2 * dp)   // przyciski mieszczą się obok logo na każdym telefonie
            for ((l, c) in hs.reversed()) { val k = K(l, c, bw); k.r.set(x - k.w, 4 * dp, x, headH - 2 * dp); head.add(k); x -= k.w + 2 * dp }
            var y = headH + sugH
            // układ jak w Gboard: litery z cyframi u góry; ?123 - znaki, =\< - więcej znaków
            fun chars(s: String) = s.map { K(it.toString(), it.toString(), 1f) }
            val space = K(if (lang(context) == "pl") "Polski" else "English", "space", 4f)
            val lay = when (sym) {
                1 -> listOf(chars("1234567890"), chars("@#\$_&-+()/"), chars("%^=[]{}<>~"),
                    listOf(K("=\\<", "sym2", 1.5f)) + chars("*\"':;!?") + listOf(K("⌫", "bs", 1.5f)),
                    listOf(K("ABC", "abc", 1.5f), K(",", ",", 1f), K("…", "…", 1f), space, K(".", ".", 1f), K("⏎", "enter", 1.5f)))
                2 -> listOf(chars("1234567890"), chars("~`|•√π÷×§∆"), chars("£€¥°©®™✓¶\\"),
                    listOf(K("?123", "sym", 1.5f)) + chars("«»„”–—·") + listOf(K("⌫", "bs", 1.5f)),
                    listOf(K("ABC", "abc", 1.5f), K(",", ",", 1f), K("…", "…", 1f), space, K(".", ".", 1f), K("⏎", "enter", 1.5f)))
                3 -> {   // 😀 kategorie u góry (🕘 ostatnio używane), 3 rzędy po 8, GIF z telefonu
                    if (ecat < 0) ecat = if (recentEmoji().isNotEmpty()) 0 else 1
                    val list = if (ecat == 0) recentEmoji() else emojiCats[ecat - 1]
                    listOf(listOf(K("🕘", "ecat:0", 1f)) + emojiCats.mapIndexed { i, c -> K(c[0], "ecat:${i + 1}", 1f) } + listOf(K("GIF", "gifs", 1f))) +
                        (0 until 3).map { r -> list.drop(r * 8).take(8).map { K(it, it, 1.25f) } } +
                        listOf(listOf(K("ABC", "abc", 1.5f), K(",", ",", 1f), K("…", "…", 1f), space, K(".", ".", 1f), K("⌫", "bs", 1.5f)))
                }
                4 -> {   // 📋 schowek i edycja (jak w Gboard): ostatnio skopiowane, strzałki, zaznacz, wytnij, kopiuj, wklej, cofnij
                    val cl = recentClips()
                    val clipRows = if (cl.isEmpty()) listOf(listOf(K(getString(R.string.kb_clip_empty), "noop", 10f)), emptyList())
                        else listOf(cl.take(2), cl.drop(2).take(2)).map { r -> r.map { t -> K(t.replace('\n', ' '), "clip:" + cl.indexOf(t), 5f) } }
                    clipRows + listOf(listOf(K("◀", "left", 1.5f), K("▶", "right", 1.5f), K("⇤", "home", 1.5f), K("⇥", "end", 1.5f), K(getString(R.string.kb_selall), "selall", 4f)),
                        listOf(K(getString(R.string.kb_cut), "cut", 2.5f), K(getString(R.string.kb_copy), "copy", 2.5f), K(getString(R.string.kb_paste), "paste", 2.5f), K(getString(R.string.kb_undo), "undo", 2.5f)),
                        listOf(K("ABC", "abc", 1.5f), K(space.label, "space", 5.5f), K("⌫", "bs", 1.5f), K("⏎", "enter", 1.5f)))
                }
                5 -> {   // 🎞 GIF-y z telefonu - 4 rzędy po 4
                    val g = if (!gifAllowed()) listOf(listOf(K(getString(R.string.kb_gif_allow), "gifperm", 10f)))
                        else if (gifs.isEmpty()) listOf(listOf(K(getString(R.string.kb_gif_none), "noop", 10f)))
                        else (0 until 4).map { r -> gifs.indices.drop(r * 4).take(4).map { K("", "gif:$it", 2.5f) } }
                    g + List(4 - g.size) { emptyList<K>() } + listOf(listOf(K("😀", "emoji", 1.5f), K(space.label, "space", 5.5f), K("⌫", "bs", 1.5f), K("ABC", "abc", 1.5f)))
                }
                else -> listOf(chars(rows[0]), chars(rows[1]), chars(rows[2]),
                    listOf(K("⇧", "shift", 1.5f)) + chars(rows[3]) + listOf(K("⌫", "bs", 1.5f)),
                    listOf(K("?123", "sym", 1.5f), K(",", ",", 1f), if (email) K("@", "@", 1f) else K("😀", "emoji", 1f), space, K(".", ".", 1f), K("⏎", "enter", 1.5f)))
            }
            for (row in lay) {
                val units = row.sumOf { it.w.toDouble() }.toFloat()
                val u = (width - pad * 2) / max(units, 10f)
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
                if (shift && !shiftAuto && sym == 0) { val i = "1234567890-=[];',./".indexOf(ch); if (i >= 0) return "!@#$%^&*()_+{}:\"<>?"[i].toString() }
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
                val on = when (k.code) { "fix" -> pref("klaw-korekta"); "klik" -> pref("klaw-klik"); "read" -> pref("klaw-czytaj", false); "mic" -> listening; "clip" -> sym == 4; else -> false }
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
            // menu pod logo V: wszystkie funkcje i opcje w kafelkach (zamiast klawiszy)
            if (menuOpen) {
                layoutMenu()
                for (k in menu) {
                    val on = k === pressed || isOn(k.code)
                    key(cv, k.r, on, fn = true)
                    tp.color = if (on) Color.WHITE else text; tp.textSize = 22 * dp
                    cv.drawText(k.label, k.r.centerX(), k.r.centerY() - 2 * dp, tp)
                    tp.textSize = 11.5f * dp; tp.color = if (on) Color.WHITE else dim
                    cv.drawText(fit(getString(menuName(k.code)), k.r.width() - 6 * dp), k.r.centerX(), k.r.bottom - 8 * dp, tp)
                }
            }
            // klawisze
            if (!menuOpen) for (k in keys) {
                val on = k === pressed && !swiping && !cursorMode || k.code == "shift" && (shift || caps) || k.code == "caps" && caps || k.code == "alt" && alt || k.code == "ecat:$ecat" && sym == 3
                val emo = isEmoji(k.code) && k.code != "emoji"
                key(cv, k.r, on, fn = !emo && (k.code.length > 1 || k.code in setOf("?", ",", ".")))
                if (k.code.startsWith("gif:")) {   // 🎞 miniatura GIF-a
                    gifs.getOrNull(k.code.substring(4).toInt())?.second?.let { b -> val s = min((k.r.width() - 6 * dp) / b.width, (k.r.height() - 6 * dp) / b.height)
                        val dw = b.width * s; val dh = b.height * s; cv.drawBitmap(b, null, RectF(k.r.centerX() - dw / 2, k.r.centerY() - dh / 2, k.r.centerX() + dw / 2, k.r.centerY() + dh / 2), null) }
                    continue
                }
                val l = label(k)
                val hint = if (k.code.length == 1 && sym == 0) { val i = "acelnoszx".indexOf(k.code[0]); if (i >= 0 && !alt) "ąćęłńóśżź"[i].toString() else longSym[k.code[0]]?.toString() ?: "" } else ""
                // jak zwykłe klawiatury telefonu: znak z przytrzymania mały nad literą, litera większa poniżej; wyższe klawisze (ustawienie) - większe litery
                tp.textSize = (if (emo) (if (k.code.startsWith("ecat")) 20 else 26) * ks else if (l.length > 2) 14f else 22 * ks) * dp; tp.color = if (on) Color.WHITE else text
                cv.drawText(if (l.length > 2) fit(l, k.r.width() - 8 * dp) else l, k.r.centerX(), k.r.centerY() + (if (hint.isNotEmpty()) 12 else 8) * dp * (if (emo || l.length > 2) 1f else ks), tp)
                if (hint.isNotEmpty()) { tp.textSize = 12 * ks * dp; tp.color = dim; cv.drawText(hint, k.r.centerX(), k.r.top + 15 * dp, tp) }
            }
            // 🔍 powiększona litera nad palcem (jak w Gboard) - widać, co się naciska; dla słabszego wzroku
            pressed?.takeIf { !swiping && !menuOpen && !cursorMode && downY >= headH + sugH && pref("klaw-podglad") && (it.code.length == 1 && it.code[0].isLetterOrDigit() || isEmoji(it.code) && it.code != "emoji") }?.let { k ->
                val bw = k.r.width() * 1.6f; val bh = keyH * 1.15f
                val left = (k.r.centerX() - bw / 2).coerceIn(2 * dp, w - bw - 2 * dp); val top = max(0f, k.r.top - bh - 2 * dp)
                val b = RectF(left, top, left + bw, top + bh)
                p.color = Color.argb(0x50, 0, 0, 0); cv.drawRoundRect(RectF(b.left + 2 * dp, b.top + 3 * dp, b.right + 2 * dp, b.bottom + 3 * dp), 12 * dp, 12 * dp, p)
                p.color = if (contrast) Color.BLACK else if (text == Color.WHITE) Color.rgb(0x2E, 0x25, 0x60) else Color.WHITE; cv.drawRoundRect(b, 12 * dp, 12 * dp, p)
                if (contrast) { p.style = Paint.Style.STROKE; p.strokeWidth = 2 * dp; p.color = Color.WHITE; cv.drawRoundRect(b, 12 * dp, 12 * dp, p); p.style = Paint.Style.FILL }
                tp.textSize = 38 * ks * dp; tp.color = text; tp.typeface = Typeface.DEFAULT_BOLD
                cv.drawText(label(k), b.centerX(), b.centerY() + 13 * ks * dp, tp); tp.typeface = Typeface.DEFAULT
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

        private val ks get() = (prefs(context).getInt("wysokosc", 120) / 120f).coerceIn(0.85f, 1.35f)   // wielkość liter rośnie z wysokością klawiszy
        private fun isEmoji(c: String) = c.isNotEmpty() && Character.getType(c.codePointAt(0)) == Character.OTHER_SYMBOL.toInt()
        private var cursorMode = false; private var curX = 0f   // ⇔ przesuwanie kursora palcem po spacji (jak w Gboard)
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

        // przytrzymanie ⌫: najpierw litery, po chwili całe słowa (jak w Gboard)
        private var reps = 0
        private val repeat = object : Runnable { override fun run() { if (pressed?.code == "bs" && !swiping) { if (reps++ < 12) backspace() else deleteWord(); longDone = true; postDelayed(this, if (reps > 12) 250L else 60L) } } }
        private val longPress = Runnable {
            val k = pressed ?: return@Runnable
            if (swiping) return@Runnable
            when {
                k.code == "bs" -> { longDone = true; reps = 0; post(repeat) }
                // przytrzymanie @ - emoji
                k.code == "@" && sym == 0 -> { longDone = true; sym = 3; refresh(); performHapticFeedback(HAPTIC_FEEDBACK_ENABLED) }
                // przytrzymanie litery: polska litera (a -> ą), cyfry - znak z ⇧
                k.code.length == 1 && sym == 0 && "acelnoszx".indexOf(k.code[0]) >= 0 -> { longDone = true; val was = alt; alt = true; type(k.code[0]); alt = was; performHapticFeedback(HAPTIC_FEEDBACK_ENABLED) }
                // przytrzymanie cyfry / litery / , . - znak z podpowiedzi w rogu
                k.code.length == 1 && sym == 0 && longSym[k.code[0]] != null -> { longDone = true; val was = shift; val wa = shiftAuto; shift = false; type(longSym[k.code[0]]!!); shift = was && !wa; shiftAuto = wa; performHapticFeedback(HAPTIC_FEEDBACK_ENABLED) }
                k.code == "space" -> { longDone = true; (getSystemService(INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager).showInputMethodPicker() }
            }
        }

        @SuppressLint("ClickableViewAccessibility")
        override fun onTouchEvent(e: MotionEvent): Boolean {
            val x = e.x; val y = e.y
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downX = x; downY = y; downAt = SystemClock.uptimeMillis(); longDone = false; swiping = false; cursorMode = false; trace.clear()
                    if (y < headH && x < (head.minOfOrNull { it.r.left } ?: width.toFloat())) { menuOpen = !menuOpen; performHapticFeedback(HAPTIC_FEEDBACK_ENABLED); invalidate(); return true }   // logo V: menu
                    if (menuOpen && y >= headH) { pressed = menu.firstOrNull { it.r.contains(x, y) }; pressed?.let { k -> postDelayed({ if (pressed === k) { longDone = true; tip(menuName(k.code)) } }, 500) }; invalidate(); return true }
                    if (y < headH) { pressed = head.firstOrNull { it.r.contains(x, y) }; pressed?.let { k -> postDelayed({ if (pressed === k) { longDone = true; tip(headTip(k.code)) } }, 500) }; invalidate(); return true }
                    if (y < headH + sugH) { pressed = null; return true }
                    pressed = keyAt(x, y); trace.add(Slownik.P(x.toDouble(), y.toDouble()))
                    click(); performHapticFeedback(HAPTIC_FEEDBACK_ENABLED)
                    waveX = pressed?.r?.centerX() ?: x; waveY = pressed?.r?.centerY() ?: y; waveAt = SystemClock.uptimeMillis()
                    postDelayed(longPress, if (pref("klaw-senior", false)) 700L else 420L)
                    invalidate()
                }
                MotionEvent.ACTION_MOVE -> {
                    val k = pressed ?: return true
                    if (downY < headH + sugH) return true
                    if (k.code == "space" && !longDone) {   // ⇔ palec po spacji - kursor w lewo / w prawo
                        if (!cursorMode && kotlin.math.abs(x - downX) > 16 * dp) { cursorMode = true; removeCallbacks(longPress); curX = x; invalidate() }
                        if (cursorMode) { val st = 14 * dp
                            while (x - curX > st) { sendDownUpKeyEvents(KeyEvent.KEYCODE_DPAD_RIGHT); curX += st }
                            while (curX - x > st) { sendDownUpKeyEvents(KeyEvent.KEYCODE_DPAD_LEFT); curX -= st } }
                        return true
                    }
                    trace.add(Slownik.P(x.toDouble(), y.toDouble()))
                    val kw = (keys.firstOrNull { it.code == "q" }?.r?.width() ?: 40f).toDouble()
                    // przeciąganie od litery: palec odjechał dalej niż pół klawisza
                    if (!swiping && !longDone && k.code.length == 1 && k.code[0] in 'a'..'z' && !secret && !pref("klaw-senior", false) && hypot(x - downX, y - downY) > kw * 0.6) { swiping = true; removeCallbacks(longPress) }
                    if (swiping) invalidate()
                }
                MotionEvent.ACTION_UP -> {
                    removeCallbacks(longPress); removeCallbacks(repeat)
                    val k = pressed; pressed = null
                    if (cursorMode) { cursorMode = false; cur = ""; fixFrom = null; lastSwipe = null; autoShift(); refresh(); return true }   // kursor przesunięty - bez spacji
                    if (downY < headH) { if (k != null && !longDone && k.r.contains(x, y)) headAction(k.code); invalidate(); return true }
                    if (menuOpen) { if (k != null && !longDone && k.r.contains(x, y)) menuAction(k.code); invalidate(); return true }
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

        // --- menu pod logo V ---
        private var menuOpen = false
        private val menu = ArrayList<K>()
        private val menuItems = listOf("🗒" to "note", "📋" to "notes", "🔍" to "search", "▶" to "yt", "📤" to "sendpc", "💬" to "chat", "📲" to "sendphone",
            "✓" to "fix", "🔈" to "klik", "🔊" to "read", "🎤" to "mic",
            "📖" to "words", "🌐" to "lang", "🎨" to "theme", "↕" to "height", "👵" to "senior", "⠿" to "brajl",
            "⌨" to "switch", "⚙" to "settings", "⌄" to "hide", "⌨︎" to "back")
        private fun layoutMenu() {
            menu.clear()
            val cols = 4; val pad = 4 * dp
            val top = headH; val h = (height - top - 6 * dp) / ((menuItems.size + cols - 1) / cols)
            val w = (width - pad * 2) / cols
            menuItems.forEachIndexed { i, (l, c) ->
                val k = K(if (c == "lang") lang(context).uppercase() else l, c, 1f)
                k.r.set(pad + w * (i % cols) + 2 * dp, top + h * (i / cols) + 3 * dp, pad + w * (i % cols + 1) - 2 * dp, top + h * (i / cols + 1) - 3 * dp)
                menu.add(k)
            }
        }
        private fun isOn(c: String) = when (c) { "fix" -> pref("klaw-korekta"); "klik" -> pref("klaw-klik"); "read" -> pref("klaw-czytaj", false); "mic" -> listening; "senior" -> pref("klaw-senior", false); else -> false }
        private fun menuName(c: String) = when (c) {
            "sendpc" -> R.string.kb_m_send; "chat" -> R.string.kb_m_chat; "sendphone" -> R.string.kb_m_phone; "note" -> R.string.kb_m_note; "notes" -> R.string.kb_m_notes; "search" -> R.string.kb_m_search; "yt" -> R.string.kb_m_yt
            "fix" -> R.string.kb_fix; "klik" -> R.string.kb_click; "read" -> R.string.kb_read; "mic" -> R.string.kb_mic
            "words" -> R.string.kb_words; "lang" -> R.string.kb_lang; "theme" -> R.string.kb_theme; "height" -> R.string.kb_height; "senior" -> R.string.kb_senior; "brajl" -> R.string.kb_braille
            "switch" -> R.string.kb_m_switch; "settings" -> R.string.kb_m_settings; "hide" -> R.string.kb_hide; else -> R.string.kb_m_back
        }
        // funkcje Velivo (karteczki, szukanie, YouTube) - otwierane w przeglądarce Velivo
        private fun velivo(action: String) {
            runCatching { startActivity(Intent(action).setClassName("pl.velivo.browser", "pl.velivo.browser.MainActivity").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
                .onFailure { Toast.makeText(context, R.string.kb_m_no_velivo, Toast.LENGTH_LONG).show() }
        }
        private fun menuAction(c: String) {
            when (c) {
                "note" -> velivo("pl.velivo.browser.KARTECZKA_NOWA")
                "sendpc" -> velivo("pl.velivo.browser.WYSLIJ_NA_KOMPUTER")
                "chat" -> Rozmowy.open(context, false)
                "sendphone" -> Rozmowy.open(context, true)
                "notes" -> velivo("pl.velivo.browser.KARTECZKI")
                "search" -> velivo("pl.velivo.browser.WIDZET_SZUKAJ")
                "yt" -> velivo("pl.velivo.browser.WIDZET_YT")
                "theme" -> startActivity(Intent(context, SzybkieUstawienia::class.java).setAction(Ustawienia.ACTION_THEME).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                "height" -> startActivity(Intent(context, SzybkieUstawienia::class.java).setAction(Ustawienia.ACTION_HEIGHT).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                "settings" -> startActivity(Intent(context, Ustawienia::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                "switch" -> (getSystemService(INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager).showInputMethodPicker()
                "back" -> { menuOpen = false; refresh(); return }
                "brajl" -> { menuOpen = false; brailleOn(); return }
                "senior" -> { senior(context, !pref("klaw-senior", false)); Toast.makeText(context, getString(R.string.kb_senior) + ": " + getString(if (pref("klaw-senior", false)) R.string.kb_on else R.string.kb_off), Toast.LENGTH_SHORT).show(); theme(); refresh(); return }
                else -> { headAction(c); if (c == "lang" || c == "hide") menuOpen = false; refresh(); return }
            }
            menuOpen = false; refresh()
        }

        private fun headTip(c: String) = when (c) { "clip" -> R.string.kb_clip; "fix" -> R.string.kb_fix; "klik" -> R.string.kb_click; "words" -> R.string.kb_words; "mic" -> R.string.kb_mic; "read" -> R.string.kb_read; "lang" -> R.string.kb_lang; else -> R.string.kb_hide }

        private fun headAction(c: String) {
            val pr = prefs(context)
            when (c) {
                "fix" -> pr.edit().putBoolean("klaw-korekta", !pref("klaw-korekta")).apply()
                "klik" -> pr.edit().putBoolean("klaw-klik", !pref("klaw-klik")).apply()
                "read" -> pr.edit().putBoolean("klaw-czytaj", !pref("klaw-czytaj", false)).apply()
                "words" -> startActivity(Intent(context, SzybkieUstawienia::class.java).setAction(ACTION_WORDS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                "mic" -> toggleMic()
                "lang" -> { pr.edit().putString("klaw-jezyk", if (lang(context) == "pl") "en" else "pl").apply(); load(context) { refresh() } }
                "hide" -> requestHideSelf(0)
                "clip" -> { sym = if (sym == 4) 0 else 4; if (sym == 4) grabClip() }   // 📋 schowek i edycja
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
                // ⇧ dwa razy szybko - stałe wielkie litery (zamiast osobnego ⇪)
                "shift" -> { val now = System.currentTimeMillis(); if (caps) { caps = false; shift = false } else if (shift && now - shiftAt < 400) { caps = true; shift = true } else shift = !shift; shiftAt = now; shiftAuto = false; refresh() }
                "caps" -> { caps = !caps; shift = caps; shiftAuto = false; refresh() }
                "alt" -> { alt = !alt; refresh() }
                "bs" -> backspace()
                "sym" -> { sym = 1; shift = false; caps = false; alt = false; refresh() }
                "sym2" -> { sym = 2; refresh() }
                "abc" -> { sym = 0; refresh() }
                "enter" -> enter()
                "space" -> { lastSwipe?.let { if (before(1) == " ") { lastSwipe = null; refresh(); return } }; type(' ') }
                "emoji" -> { sym = 3; shift = false; caps = false; alt = false; refresh() }
                "gifs" -> { sym = 5; loadGifs(); refresh() }
                "gifperm" -> startActivity(Intent(context, SzybkieUstawienia::class.java).setAction(Ustawienia.ACTION_GIF).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                "noop" -> {}
                // ✂ edycja jak w Gboard: strzałki, początek / koniec, zaznacz wszystko, wytnij, kopiuj, wklej, cofnij
                "left", "right", "home", "end" -> { sendDownUpKeyEvents(when (k.code) { "left" -> KeyEvent.KEYCODE_DPAD_LEFT; "right" -> KeyEvent.KEYCODE_DPAD_RIGHT; "home" -> KeyEvent.KEYCODE_MOVE_HOME; else -> KeyEvent.KEYCODE_MOVE_END }); cur = ""; fixFrom = null }
                "selall" -> currentInputConnection?.performContextMenuAction(android.R.id.selectAll)
                "cut", "copy", "paste" -> { currentInputConnection?.performContextMenuAction(when (k.code) { "cut" -> android.R.id.cut; "copy" -> android.R.id.copy; else -> android.R.id.paste }); cur = ""; fixFrom = null; ui.postDelayed({ grabClip(); refresh() }, 300) }
                "undo" -> currentInputConnection?.let { ic -> val t = SystemClock.uptimeMillis(); ic.sendKeyEvent(KeyEvent(t, t, KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_Z, 0, KeyEvent.META_CTRL_ON)); ic.sendKeyEvent(KeyEvent(t, t, KeyEvent.ACTION_UP, KeyEvent.KEYCODE_Z, 0, KeyEvent.META_CTRL_ON)); cur = ""; fixFrom = null }
                else -> when {
                    k.code.startsWith("ecat:") -> { ecat = k.code.substring(5).toInt(); refresh() }
                    k.code.startsWith("clip:") -> { recentClips().getOrNull(k.code.substring(5).toInt())?.let { currentInputConnection?.commitText(it, 1) }; cur = ""; fixFrom = null }
                    k.code.startsWith("gif:") -> gifs.getOrNull(k.code.substring(4).toInt())?.let { sendGif(it.first) }
                    else -> { if (sym == 3 && isEmoji(k.code)) useEmoji(k.code)   // 🕘 ostatnio używane
                        if (k.code.length == 1) type(k.code[0]) else currentInputConnection?.commitText(k.code, 1) }   // emoji (kilka znaków)
                }
            }
        }
    }
}
