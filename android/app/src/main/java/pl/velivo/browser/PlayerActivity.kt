package pl.velivo.browser

import android.app.Activity
import android.app.AlertDialog
import android.app.PictureInPictureParams
import android.content.Context
import android.content.res.Configuration
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.media.AudioManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.ParcelFileDescriptor
import android.util.Rational
import android.view.GestureDetector
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.SeekBar
import android.widget.TextView
import android.widget.Toast
import org.videolan.libvlc.LibVLC
import org.videolan.libvlc.Media
import org.videolan.libvlc.MediaPlayer
import org.videolan.libvlc.util.VLCVideoLayout
import java.lang.ref.WeakReference

// Odtwarzacz Velivo na bibliotece VLC (jak VLC na Windows): telewizja (IPTV), Jellyfin i filmy z telefonu - każdy format
// (stare AVI, AC3, MPEG-TS, HLS). Pamięta miejsce w filmie, obraz w obrazie, granie w tle, wyłącznik czasowy,
// gesty: jasność (lewa strona), głośność (prawa), przewijanie w bok; w telewizji w górę / w dół = następny / poprzedni kanał.
class PlayerActivity : Activity() {
    class Item(val title: String, val uri: String, val key: String?, val headers: Map<String, String> = emptyMap(), val jellyId: String? = null, val info: String = "", val desc: String = "", val endsAt: Long = 0,
               val ch: Tv.Channel? = null, val arch: Arch? = null)
    // audycja z archiwum (jak w Velivo na Windows): kanał, czas audycji i odmiana adresu (-1 = zapamiętana dla serwera)
    class Arch(val start: Long, val stop: Long, var variant: Int = -1)

    companion object {
        // kolejka ustawiana przed otwarciem (w pamięci - adresy z tokenem nie idą przez Intent ani do logów)
        var queue: List<Item> = emptyList()
        var index = 0
        var live = false
        var offAt = 0L   // ⏰ zaplanowana audycja z wyłączeniem: koniec odtwarzania o tej godzinie
        var wake = false   // ⏰ zaplanowana audycja - włącz ekran i pokaż na blokadzie
        var current: WeakReference<PlayerActivity>? = null
        fun pauseIfPlaying() { current?.get()?.player?.pause() }
        private const val UA = "VLC/3.0.20 LibVLC/3.0.20"
        // jedna biblioteka VLC na całą aplikację - kolejne otwarcie odtwarzacza bez ponownego ładowania (szybki start)
        private var shared: LibVLC? = null
        private fun lib(c: Context) = shared ?: LibVLC(c.applicationContext, arrayListOf("--http-reconnect", "--http-user-agent=$UA", "--audio-time-stretch", "--no-stats")).also { shared = it }
    }

    private lateinit var vlc: LibVLC
    private lateinit var player: MediaPlayer
    private lateinit var video: VLCVideoLayout
    private lateinit var titleView: TextView
    private lateinit var descView: TextView   // TV: co leci (teraz, potem, opis) - nad paskiem postępu
    private lateinit var controls: View
    private lateinit var playBtn: Button
    private lateinit var seek: SeekBar
    private lateinit var timeView: TextView
    private lateinit var busy: ProgressBar
    private var fd: ParcelFileDescriptor? = null   // film z pamięci telefonu (content://)
    private var seeking = false
    private var resumeAt = 0L   // miejsce w filmie do wznowienia, gdy obraz ruszy
    private val ui = Handler(Looper.getMainLooper())
    // po zamknięciu odtwarzacza (zwolniony VLC) żadne opóźnione zadanie nie może już pytać odtwarzacza - wywracało Velivo
    @Volatile private var gone = false
    private val stuck = Runnable { if (!gone && !player.isPlaying) toast(getString(R.string.player_stuck)) }
    private val hide = Runnable { if (!gone && player.isPlaying) showControls(false) }
    private val prefs by lazy { getSharedPreferences("velivo", Context.MODE_PRIVATE) }
    private val posPrefs by lazy { getSharedPreferences("filmy", Context.MODE_PRIVATE) }
    private val pw by lazy { Passwords(this) }
    private var sleepAt = 0L
    // 🗣 lektor: czyta napisy filmu z Jellyfin głosem Google, film na ten czas ściszony (jak na Windows)
    private var lektor: Lektor? = null
    private val lektorTick = object : Runnable { override fun run() { if (gone) return; lektor?.let { if (player.isPlaying) it.at(player.time - subDelay) }; ui.postDelayed(this, 150) } }
    // ⏱ przesunięcie napisów (ms, + = później): ekran VLC i lektor razem, zapamiętane dla filmu
    private var subDelay = 0L
    private fun subKey() = queue.getOrNull(index)?.key?.let { "napisy-przes-$it" }
    private fun applySubDelay() { runCatching { player.setSpuDelay(subDelay * 1000) }; lektor?.seeked() }
    private fun lektorOn() = prefs.getBoolean("lektor", false)

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()
    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_SHORT).show()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        current = WeakReference(this)
        openedFrom(intent)
        if (wake) { wake = false; setShowWhenLocked(true); setTurnScreenOn(true) }
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        window.decorView.setBackgroundColor(Color.BLACK)
        window.insetsController?.let { it.hide(WindowInsets.Type.systemBars()); it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE }

        vlc = lib(this)
        player = MediaPlayer(vlc)
        player.setEventListener { e -> onVlc(e) }

        video = VLCVideoLayout(this)
        // górny pasek: tytuł, ścieżki (lektor / napisy), wyłącznik, okienko, zamknij
        titleView = TextView(this).apply { setTextColor(Color.WHITE); textSize = 16f; maxLines = 2; setShadowLayer(4f, 0f, 0f, Color.BLACK) }
        val top = LinearLayout(this).apply {
            gravity = Gravity.CENTER_VERTICAL; setPadding(dp(12), dp(10), dp(8), dp(10)); setBackgroundColor(Color.argb(140, 27, 23, 51))
            addView(titleView, LinearLayout.LayoutParams(0, -2, 1f))
            if (queue.getOrNull(index)?.arch != null) addView(btn("⏩", R.string.tv_arch_live) { goLive() })   // z archiwum na żywo
            else if (live && queue.any { q -> q.ch?.let { Tv.hasCatchup(it) } == true }) addView(btn("⏪", R.string.tv_arch_restart) { startOver() })   // od początku audycji (archiwum)
            if (live) addView(btn("☆", R.string.tv_fav) { toggleFav() }.also { favBtn = it })   // ⭐ ulubiony kanał (jak na liście kanałów)
            addView(btn("🗨", R.string.player_tracks) { askTracks() })
            addView(btn("🌙", R.string.sleep_title) { askSleep() })
            addView(btn("⧉", R.string.player_pip) { enterPip() })
            addView(btn("✕", R.string.close_tab_short) { finish() })
        }
        // środek: poprzedni, -10 s, odtwarzaj/pauza, +10 s, następny (w TV: poprzedni / następny kanał)
        playBtn = btn("⏸", R.string.player_playpause, big = true) { if (player.isPlaying) player.pause() else player.play(); poke() }
        val mid = LinearLayout(this).apply {
            gravity = Gravity.CENTER
            addView(btn("⏮", R.string.player_prev, big = true) { next(-1) })
            if (!live) addView(btn("⏪", R.string.player_back) { jump(-10_000) })
            addView(playBtn)
            if (!live) addView(btn("⏩", R.string.player_fwd) { jump(10_000) })
            addView(btn("⏭", R.string.player_next, big = true) { next(1) })
        }
        // dół: opis programu, pasek postępu (filmy), czas
        descView = TextView(this).apply { setTextColor(Color.parseColor("#ECE8FF")); textSize = 14f; maxLines = 6; setPadding(dp(14), dp(10), dp(14), dp(10)) }
        seek = SeekBar(this).apply {
            max = 1000
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(s: SeekBar, p: Int, user: Boolean) { if (user) timeView.text = fmt(player.length * p / 1000) + " · " + fmt(player.length) }
                override fun onStartTrackingTouch(s: SeekBar) { seeking = true; ui.removeCallbacks(hide) }
                override fun onStopTrackingTouch(s: SeekBar) { seeking = false; player.setTime(player.length * s.progress / 1000); lektor?.seeked(); poke() }
            })
        }
        timeView = TextView(this).apply { setTextColor(Color.WHITE); textSize = 14f; setPadding(dp(14), 0, dp(14), dp(12)) }
        val bottom = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL; setBackgroundColor(Color.argb(140, 27, 23, 51))
            addView(descView); if (!live) { addView(seek); addView(timeView) }
        }
        busy = ProgressBar(this).apply { isIndeterminate = true; visibility = View.GONE }
        val box = FrameLayout(this).apply {
            addView(top, FrameLayout.LayoutParams(-1, -2, Gravity.TOP))
            addView(mid, FrameLayout.LayoutParams(-1, -2, Gravity.CENTER))
            addView(bottom, FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM))
        }
        controls = box
        val root = FrameLayout(this)
        root.addView(video, FrameLayout.LayoutParams(-1, -1))
        root.addView(busy, FrameLayout.LayoutParams(dp(56), dp(56), Gravity.CENTER))
        root.addView(box, FrameLayout.LayoutParams(-1, -1))
        setContentView(root)
        gestures(root)
        player.attachViews(video, null, true, false)   // napisy włączone
        load(index)
        ui.postDelayed(saver, 5000)
        ui.post(tick)
        if (offAt > System.currentTimeMillis()) { sleepAt = offAt; ui.postDelayed(sleepRun, offAt - System.currentTimeMillis()) }
        offAt = 0
    }

    private fun isAudio(u: String) = runCatching { contentResolver.getType(Uri.parse(u)) }.getOrNull()?.startsWith("audio/") == true ||
        u.substringBefore('?').substringAfterLast('.').lowercase() in setOf("mp3", "flac", "m4a", "ogg", "opus", "wav", "aac", "wma")

    // film otwarty z innej aplikacji ("Otwórz za pomocą → Velivo") - sam plik, miejsce w filmie pamiętane
    private fun openedFrom(i: android.content.Intent?) {
        val u = i?.data ?: return
        if (i.action != android.content.Intent.ACTION_VIEW) return
        val name = runCatching { contentResolver.query(u, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c -> if (c.moveToFirst()) c.getString(0) else null } }.getOrNull()
            ?: u.lastPathSegment ?: getString(R.string.app_name)
        queue = listOf(Item(name, u.toString(), "plik-" + u.toString().hashCode())); index = 0; live = false
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        if (intent.action == android.content.Intent.ACTION_VIEW && intent.data != null) { openedFrom(intent); recreate() }
    }

    private fun btn(label: String, tip: Int, big: Boolean = false, click: () -> Unit) = Button(this, null, android.R.attr.borderlessButtonStyle).apply {
        text = label; textSize = if (big) 26f else 20f; setTextColor(Color.parseColor("#ECE8FF")); contentDescription = getString(tip); tooltipText = getString(tip)
        background = GradientDrawable().apply { setColor(Color.parseColor("#CC2E2768")); cornerRadius = dp(16).toFloat(); setStroke(dp(1), Color.parseColor("#4A3F8A")) }   // kolory Velivo
        setPadding(0, 0, 0, 0); minWidth = 0; minimumWidth = 0; minHeight = 0; minimumHeight = 0
        setOnClickListener { click() }
        val s = if (big) dp(68) else dp(52)
        layoutParams = LinearLayout.LayoutParams(s, if (big) dp(64) else dp(48)).apply { setMargins(dp(6), 0, dp(6), 0) }
    }

    private fun fmt(ms: Long): String { val s = (ms / 1000).coerceAtLeast(0); return if (s >= 3600) String.format("%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60) else String.format("%02d:%02d", s / 60, s % 60) }

    private fun onVlc(e: MediaPlayer.Event) {
        if (gone) return
        when (e.type) {
            MediaPlayer.Event.Buffering -> busy.visibility = if (e.buffering < 100f) View.VISIBLE else View.GONE
            MediaPlayer.Event.Playing -> { queue.getOrNull(index)?.let { c -> if (c.arch != null && c.ch != null) prefs.edit().putInt("tv-archiwum-" + Tv.archiveHost(c.ch.url), c.arch.variant).apply() }   // ta odmiana działa u dostawcy
                if (resumeAt > 0) { player.setTime(resumeAt); resumeAt = 0 }; busy.visibility = View.GONE; ui.removeCallbacks(stuck); playBtn.text = "⏸"; updatePip(); poke() }
            MediaPlayer.Event.Paused -> { playBtn.text = "▶"; updatePip(); showControls(true) }
            MediaPlayer.Event.EndReached -> { val c = queue.getOrNull(index); if (c?.arch != null) archiveEnded(c) else if (!live) { savePos(true); next(1) } else load(index) }   // kanał się urwał - wznów
            MediaPlayer.Event.EncounteredError -> {
                busy.visibility = View.GONE
                val c = queue.getOrNull(index); val ar = c?.arch; val ach = c?.ch
                if (ar != null && ach != null) {   // archiwum: serwer nie przyjął tej odmiany adresu - kolejna, aż któraś zadziała
                    if (ar.variant + 1 < 10 && Tv.catchupUrl(ach, ar.start, ar.stop, ar.variant + 1) != null) { ar.variant++; toast(getString(R.string.tv_arch_try)); load(index); return }
                    toast(getString(R.string.tv_arch_none)); return
                }
                toast(getString(R.string.player_error))   // bez adresu (może mieć token)
            }
        }
    }

    // czas i pasek postępu co pół sekundy
    private val tick = object : Runnable {
        override fun run() {
            if (gone) return
            if (!live && !seeking) {
                val len = player.length; val t = player.time
                if (len > 0) seek.progress = (t * 1000 / len).toInt()
                timeView.text = fmt(t) + " · " + fmt(len)
            }
            ui.postDelayed(this, 500)
        }
    }

    private fun showControls(on: Boolean) {
        controls.visibility = if (on && !isInPictureInPictureMode) View.VISIBLE else View.GONE
        if (on) poke()
    }

    private fun poke() { ui.removeCallbacks(hide); ui.postDelayed(hide, 4000) }

    private fun jump(d: Long) { player.setTime((player.time + d).coerceAtLeast(0)); lektor?.seeked(); poke() }

    private fun load(i: Int) {
        val it = queue.getOrNull(i) ?: return finish()
        savePos()
        index = i
        titleView.text = it.title + if (it.info.isNotEmpty()) "\n" + it.info.substringBefore('\n') else ""
        descView.text = listOf(it.info, it.desc).filter { d -> d.isNotEmpty() }.joinToString("\n")
        descView.visibility = if (descView.text.isNullOrEmpty()) View.GONE else View.VISIBLE
        busy.visibility = View.VISIBLE
        fd?.close(); fd = null
        val ar = it.arch; val ach = it.ch
        if (ar != null && ach != null && ar.variant < 0) ar.variant = prefs.getInt("tv-archiwum-" + Tv.archiveHost(ach.url), 0)
        val archUrl = if (ar != null && ach != null) (Tv.catchupUrl(ach, ar.start, ar.stop, ar.variant) ?: Tv.catchupUrl(ach, ar.start, ar.stop, 0).also { ar.variant = 0 }) else null
        val m = if (archUrl != null) Media(vlc, Uri.parse(archUrl)) else if (it.uri.startsWith("content://")) {
            fd = contentResolver.openFileDescriptor(Uri.parse(it.uri), "r")
            Media(vlc, fd!!.fileDescriptor)
        } else {
            // Jellyfin: VLC nie wysyła własnych nagłówków - klucz w adresie (tylko w pamięci, nie w logach)
            val token = it.headers["X-Emby-Token"]
            Media(vlc, Uri.parse(if (token != null) it.uri + (if ('?' in it.uri) "&" else "?") + "api_key=" + token else it.uri))
        }
        m.addOption(":http-user-agent=$UA")
        if (isAudio(it.uri)) m.addOption(":audio-visual=goom")   // muzyka: kosmiczna wizualizacja VLC (Goom) zamiast czarnego ekranu
        m.addOption(":network-caching=" + if (live) 1000 else 1500)   // krótki bufor - obraz po chwili, jak w aplikacjach IPTV
        player.media = m
        m.release()
        player.play()
        subDelay = subKey()?.let { k -> prefs.getLong(k, 0L) } ?: 0L
        if (subDelay != 0L) applySubDelay()
        lektor?.stop(); lektor?.load("")
        if (lektorOn() && it.jellyId != null) lektorSubs(it, null)
        val pos = it.key?.let { k -> posPrefs.getFloat(k, 0f) } ?: 0f
        resumeAt = if (!live && pos > 15f) (pos * 1000).toLong() else 0L   // skok po starcie - bez komunikatów po drodze
        ui.removeCallbacks(stuck); ui.postDelayed(stuck, 25000)   // serwer nic nie przysyła - zwykle limit połączeń (TV gra na innym urządzeniu)
        showFav()
        if (live && sleepShowEnd) armShowEnd()
        if (live) prefs.edit().putInt("tv-ostatni", i).putString("tv-ostatni-nazwa", it.title).apply()
        showControls(true)
    }

    private fun next(d: Int) {
        if (queue.isEmpty()) return
        if (!live && index + d !in queue.indices) { finish(); return }   // film: koniec kolejki
        load((index + d + queue.size) % queue.size)
    }

    // miejsce w filmie (telefon; Jellyfin - także na serwerze), co 5 s i przy wyjściu
    private val saver = object : Runnable {
        override fun run() { if (gone) return; savePos(); ui.postDelayed(this, 5000) }
    }

    private fun savePos(ended: Boolean = false) {
        if (live) return
        val it = queue.getOrNull(index) ?: return
        val k = it.key ?: return
        val d = player.length / 1000f; val t = player.time / 1000f
        if (ended) { posPrefs.edit().remove(k).apply(); return }
        if (d < 120) return
        posPrefs.edit().apply { if (t > d - 20 || t < 15) remove(k) else putFloat(k, t) }.apply()
    }

    private fun reportJellyfin() {
        val it = queue.getOrNull(index) ?: return
        val id = it.jellyId ?: return
        val pos = player.time
        val c = Jelly.conf(pw) ?: return
        Thread { runCatching { Jelly.reportStopped(c, id, pos) } }.start()
    }

    // lektor / język i napisy (jak w VLC); film z Jellyfin: także 🗣 lektor czytający napisy i ⬇ pobieranie napisów
    private fun askTracks() {
        val a = player.audioTracks ?: emptyArray(); val s = player.spuTracks ?: emptyArray()
        val it = queue.getOrNull(index)
        val jf = it?.jellyId != null && !live
        val extra = if (jf) listOf((if (lektorOn()) "🗣 " + getString(R.string.lektor_off) else "🗣 " + getString(R.string.lektor_on)), "📄 " + getString(R.string.lektor_pick), "⬇ " + getString(R.string.subs_download), "🎚 " + getString(R.string.lektor_levels)) else emptyList()
        val shift = if (!live && (s.isNotEmpty() || lektor?.loaded == true)) listOf("⏱ " + getString(R.string.subs_shift) + fmtDelay()) else emptyList()
        val labels = a.map { "🔊 " + it.name + if (it.id == player.audioTrack) " ✓" else "" } + s.map { "🗨 " + it.name + if (it.id == player.spuTrack) " ✓" else "" } + shift + extra
        if (labels.isEmpty()) { toast(getString(R.string.player_no_tracks)); return }
        AlertDialog.Builder(this).setTitle(R.string.player_tracks).setItems(labels.toTypedArray()) { _, i ->
            val j = i - shift.size   // pozycje po przesunięciu napisów
            when {
                i < a.size -> player.setAudioTrack(a[i].id)
                i < a.size + s.size -> player.setSpuTrack(s[i - a.size].id)
                shift.isNotEmpty() && i == a.size + s.size -> askSubDelay()
                j == a.size + s.size -> { val on = !lektorOn(); prefs.edit().putBoolean("lektor", on).apply()
                    if (on && it != null) lektorSubs(it, null) else { lektor?.stop(); lektor?.load(""); toast(getString(R.string.lektor_stopped)) } }
                j == a.size + s.size + 1 -> if (it != null) pickSubs(it)
                j == a.size + s.size + 2 -> if (it != null) downloadSubs(it)
                else -> lektorLevels()
            }
        }.show()
    }

    private fun fmtDelay() = if (subDelay == 0L) "" else " (" + (if (subDelay > 0) "+" else "") + String.format(java.util.Locale.ROOT, "%.1f", subDelay / 1000.0) + " s)"

    // ⏱ przesunięcie napisów na żywo: film gra dalej, zmiana widać / słychać od razu (napisy na ekranie i lektor)
    private fun askSubDelay() {
        val label = TextView(this).apply { textSize = 22f; gravity = Gravity.CENTER; setPadding(0, dp(8), 0, dp(8)) }
        fun upd() { label.text = (if (subDelay > 0) "+" else "") + String.format(java.util.Locale.ROOT, "%.1f", subDelay / 1000.0) + " s" }
        fun step(d: Long) { subDelay = if (d == 0L) 0L else subDelay + d; applySubDelay(); subKey()?.let { k -> prefs.edit().putLong(k, subDelay).apply() }; upd() }
        upd()
        val row = LinearLayout(this).apply { gravity = Gravity.CENTER
            listOf("−1 s" to -1000L, "−0.1" to -100L, "+0.1" to 100L, "+1 s" to 1000L).forEach { (t, d) ->
                addView(Button(this@PlayerActivity).apply { text = t; isAllCaps = false; contentDescription = getString(if (d < 0) R.string.subs_earlier else R.string.subs_later); tooltipText = contentDescription; setOnClickListener { step(d) } }) } }
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(16), dp(4), dp(16), dp(4))
            addView(label); addView(row)
            addView(TextView(this@PlayerActivity).apply { text = getString(R.string.subs_shift_help); textSize = 13f; setPadding(0, dp(8), 0, 0) }) }
        AlertDialog.Builder(this).setTitle("⏱ " + getString(R.string.subs_shift)).setView(box)
            .setPositiveButton(android.R.string.ok, null).setNeutralButton(R.string.subs_shift_reset) { _, _ -> step(0) }.show()
    }

    // 🎚 głośność filmu w czasie czytania i głośność lektora - osobno (jak na Windows); od razu słychać zmianę
    private fun lektorLevels() {
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(22), dp(8), dp(22), dp(4)) }
        fun row(label: Int, key: String, def: Int, apply: (Int) -> Unit) {
            val t = TextView(this).apply { textSize = 15f }
            fun upd(v: Int) { t.text = getString(label) + ": " + v + " %" }
            val v0 = prefs.getInt(key, def); upd(v0)
            box.addView(t)
            box.addView(SeekBar(this).apply { max = 100; progress = v0; contentDescription = getString(label)
                setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                    override fun onProgressChanged(s: SeekBar, p: Int, user: Boolean) { if (user) { upd(p); prefs.edit().putInt(key, p).apply(); apply(p) } }
                    override fun onStartTrackingTouch(s: SeekBar) {}
                    override fun onStopTrackingTouch(s: SeekBar) {}
                }) }, LinearLayout.LayoutParams(-1, dp(44)))
        }
        row(R.string.lektor_film_vol, "lektor-film", 35) { }
        row(R.string.lektor_voice_vol, "lektor-glos", 100) { v -> lektor?.volume = v / 100f }
        AlertDialog.Builder(this).setTitle("🎚 " + getString(R.string.lektor_levels)).setView(box).setPositiveButton(android.R.string.ok, null).show()
    }

    // napisy z Jellyfin dla lektora: wybrane albo pierwsze (polskie najpierw)
    private fun lektorSubs(it: Item, path: String?) {
        val c = Jelly.conf(pw) ?: return; val id = it.jellyId ?: return
        Thread {
            val r = runCatching {
                val p = path ?: Jelly.subtitles(c, id).firstOrNull()?.path ?: return@runCatching null
                Lektor.text(Jelly.bytes(c, p))
            }
            ui.post {
                if (gone) return@post
                val txt = r.getOrNull()
                if (txt == null) { toast(getString(if (r.isFailure) R.string.lektor_error else R.string.lektor_no_subs)); return@post }
                // głośność filmu podczas czytania i głośność lektora - osobno, z ustawień (🎚)
                val l = lektor ?: Lektor(this) { d -> ui.post { if (!gone) runCatching { player.volume = if (d) prefs.getInt("lektor-film", 35) else 100 } } }.also { lektor = it; ui.post(lektorTick) }
                l.volume = prefs.getInt("lektor-glos", 100) / 100f
                l.load(txt)
                if (!l.loaded) { toast(getString(R.string.lektor_no_subs)); return@post }
                prefs.edit().putBoolean("lektor", true).apply()
                toast(getString(R.string.lektor_ready))
            }
        }.start()
    }

    private fun pickSubs(it: Item) {
        val c = Jelly.conf(pw) ?: return; val id = it.jellyId ?: return
        Thread {
            val l = runCatching { Jelly.subtitles(c, id) }.getOrDefault(emptyList())
            ui.post {
                if (l.isEmpty()) { toast(getString(R.string.lektor_no_subs)); return@post }
                AlertDialog.Builder(this).setTitle(R.string.lektor_pick).setItems(l.map { s -> s.label }.toTypedArray()) { _, i -> lektorSubs(it, l[i].path) }.show()
            }
        }.start()
    }

    // ⬇ napisy z sieci przez serwer Jellyfin (jego wtyczka napisów): polskie, potem angielskie
    private fun downloadSubs(it: Item) {
        val c = Jelly.conf(pw) ?: return; val id = it.jellyId ?: return
        toast(getString(R.string.subs_searching))
        Thread {
            val r = runCatching { Jelly.searchSubs(c, id, "pol").ifEmpty { Jelly.searchSubs(c, id, "eng") } }
            ui.post {
                val l = r.getOrNull()
                if (l.isNullOrEmpty()) { toast(getString(if (r.isFailure) R.string.subs_no_plugin else R.string.subs_none)); return@post }
                AlertDialog.Builder(this).setTitle(R.string.subs_download).setItems(l.map { x -> x.second }.toTypedArray()) { _, i ->
                    Thread {
                        val code = runCatching { Jelly.downloadSub(c, id, l[i].first) }.getOrDefault(0)
                        ui.post { if (code in 200..299) { toast(getString(R.string.subs_done)); ui.postDelayed({ lektorSubs(it, null) }, 3000) } else toast(getString(R.string.subs_fail)) }
                    }.start()
                }.show()
            }
        }.start()
    }

    // gesty jak w aplikacjach wideo (w pionie i w poziomie): lewa połowa w górę / w dół - jasność, prawa - głośność;
    // w bok - przewijanie filmu; dwuklik - 10 s; stuknięcie - przyciski. Kanały TV: przyciski ⏮ ⏭.
    private fun gestures(root: FrameLayout) {
        val audio = getSystemService(AudioManager::class.java)!!
        val label = TextView(this).apply {
            setTextColor(Color.parseColor("#ECE8FF")); textSize = 22f; setPadding(dp(18), dp(10), dp(18), dp(10)); visibility = View.GONE
            background = GradientDrawable().apply { setColor(Color.parseColor("#CC2E2768")); cornerRadius = dp(16).toFloat() }
        }
        root.addView(label, FrameLayout.LayoutParams(-2, -2, Gravity.CENTER_HORIZONTAL or Gravity.TOP).apply { topMargin = dp(90) })
        val hideLabel = Runnable { label.visibility = View.GONE }
        fun show(t: String) { label.text = t; label.visibility = View.VISIBLE; ui.removeCallbacks(hideLabel); ui.postDelayed(hideLabel, 800) }
        var mode = 0   // 1 jasność, 2 głośność, 3 przewijanie
        var startPos = 0L
        var acc = 0f   // zebrany ruch palca dla głośności (krok = część wysokości ekranu)
        val det = GestureDetector(this, object : GestureDetector.SimpleOnGestureListener() {
            override fun onDown(e: MotionEvent): Boolean { mode = 0; acc = 0f; startPos = player.time; return true }
            override fun onSingleTapConfirmed(e: MotionEvent): Boolean { showControls(controls.visibility != View.VISIBLE); return true }
            override fun onDoubleTap(e: MotionEvent): Boolean { if (!live) jump(if (e.x < root.width / 2f) -10_000 else 10_000); return true }
            override fun onScroll(e1: MotionEvent?, e2: MotionEvent, dx: Float, dy: Float): Boolean {
                if (e1 == null) return false
                val tx = e2.x - e1.x; val ty = e2.y - e1.y
                if (mode == 0) {
                    if (Math.abs(tx) < dp(12) && Math.abs(ty) < dp(12)) return false
                    mode = if (Math.abs(tx) > Math.abs(ty)) (if (live) -1 else 3) else if (e1.x < root.width / 2f) 1 else 2
                }
                when (mode) {
                    1 -> {
                        val lp = window.attributes
                        val cur = if (lp.screenBrightness < 0) runCatching { android.provider.Settings.System.getInt(contentResolver, android.provider.Settings.System.SCREEN_BRIGHTNESS) / 255f }.getOrDefault(0.5f) else lp.screenBrightness
                        lp.screenBrightness = (cur + dy / (root.height * 0.7f)).coerceIn(0.02f, 1f); window.attributes = lp
                        show("☀ " + (lp.screenBrightness * 100).toInt() + "%")
                    }
                    2 -> {
                        val max = audio.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
                        val step = root.height * 0.6f / max
                        acc += dy
                        while (Math.abs(acc) >= step) { audio.adjustStreamVolume(AudioManager.STREAM_MUSIC, if (acc > 0) AudioManager.ADJUST_RAISE else AudioManager.ADJUST_LOWER, 0); acc -= Math.signum(acc) * step }
                        show("🔊 " + audio.getStreamVolume(AudioManager.STREAM_MUSIC) * 100 / max + "%")
                    }
                    3 -> { val t = (startPos + (tx / root.width * 180_000).toLong()).coerceAtLeast(0); player.setTime(t); lektor?.seeked(); show(fmt(t)) }   // cała szerokość = 3 minuty
                }
                return mode > 0
            }
        })
        root.setOnTouchListener { _, e -> det.onTouchEvent(e) }
    }

    // koniec audycji z archiwum: następna z archiwum, a gdy już trwa - na żywo (jak na Windows)
    private fun archiveEnded(c: Item) {
        val ch = c.ch ?: return; val ar = c.arch ?: return
        val nx = Tv.cachedShows(ch).firstOrNull { it.start >= ar.stop }
        if (nx != null && nx.stop <= System.currentTimeMillis()) openArchive(ch, nx) else goLive()
    }

    private fun openArchive(ch: Tv.Channel, s: Tv.Show) {
        queue = listOf(Item(ch.name + " – " + s.title, ch.url, null, info = fmtClock(s.start) + "–" + fmtClock(s.stop), desc = s.desc, ch = ch, arch = Arch(s.start, s.stop))); index = 0; live = false
        recreate()
    }

    private fun fmtClock(ms: Long) = java.text.SimpleDateFormat("EEE HH:mm", java.util.Locale.getDefault()).format(java.util.Date(ms))

    // ⏮ od początku trwającej audycji (archiwum kanału)
    private fun startOver() {
        val ch = queue.getOrNull(index)?.ch
        val s = ch?.let { c -> Tv.cachedShows(c).firstOrNull { it.start <= System.currentTimeMillis() && it.stop > System.currentTimeMillis() } }
        if (ch == null || s == null || !Tv.canCatchup(ch, s)) { toast(getString(R.string.tv_arch_none)); return }
        openArchive(ch, s)
    }

    // ⏩ na żywo: ten kanał bez archiwum
    private fun goLive() {
        val ch = queue.getOrNull(index)?.ch ?: return
        queue = listOf(Item(ch.name, ch.url, ch.key, ch = ch)); index = 0; live = true
        recreate()
    }

    private var favBtn: Button? = null
    private fun favs() = prefs.getStringSet("tv-ulubione", emptySet())!!
    private fun showFav() { favBtn?.text = if (queue.getOrNull(index)?.key in favs()) "⭐" else "☆" }
    private fun toggleFav() {
        val k = queue.getOrNull(index)?.key ?: return
        val f = favs(); prefs.edit().putStringSet("tv-ulubione", if (k in f) f - k else f + k).apply(); showFav()
    }

    private fun askSleep() {
        // na górze: po zakończeniu tego programu (TV, z programu) albo tego filmu
        val endMs = if (live) (queue.getOrNull(index)?.endsAt ?: 0L) - System.currentTimeMillis() else player.length - player.time
        val endMin = if (endMs > 60_000) ((endMs + 59_999) / 60_000).toInt() else 0
        val mins = (if (endMin > 0) intArrayOf(endMin) else intArrayOf()) + intArrayOf(15, 30, 45, 60, 90, 0)
        val labels = mins.mapIndexed { k, it -> if (k == 0 && endMin > 0) getString(if (live) R.string.sleep_show_end else R.string.sleep_film_end, it) else if (it == 0) getString(R.string.sleep_none) else getString(R.string.sleep_min, it) }.toTypedArray()
        AlertDialog.Builder(this).setTitle(R.string.sleep_title).setItems(labels) { _, i ->
            ui.removeCallbacks(sleepRun)
            if (mins[i] == 0) { sleepAt = 0; sleepShowEnd = false; toast(getString(R.string.sleep_off)) }
            else if (i == 0 && endMin > 0 && live) { sleepShowEnd = true; armShowEnd(); toast(labels[0]) }   // dokładnie z końcem audycji - także po zmianie kanału
            else { sleepShowEnd = false; sleepAt = System.currentTimeMillis() + mins[i] * 60_000L; ui.postDelayed(sleepRun, mins[i] * 60_000L); toast(getString(R.string.sleep_set, mins[i])) }
        }.show()
    }

    private var sleepShowEnd = false   // 🌙 po zakończeniu bieżącej audycji

    // wyłącznik na koniec audycji z programu TV oglądanego kanału (po zmianie kanału - jego audycji)
    private fun armShowEnd() {
        ui.removeCallbacks(sleepRun)
        val end = queue.getOrNull(index)?.endsAt ?: 0L
        if (end <= System.currentTimeMillis()) { sleepShowEnd = false; return }
        sleepAt = end; ui.postDelayed(sleepRun, end - System.currentTimeMillis())
    }

    // koniec: odtwarzanie zatrzymane, Velivo schodzi na bok - telefon wraca do zwykłego czuwania (ekran gaśnie jak zawsze)
    private val sleepRun = Runnable {
        if (gone) return@Runnable
        sleepAt = 0; sleepShowEnd = false; player.pause(); PlaybackService.stop(this)
        window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        moveTaskToBack(true); finish()
    }

    // --- obraz w obrazie i granie w tle ---
    private fun pipParams() = PictureInPictureParams.Builder().setAspectRatio(Rational(16, 9)).apply {
        if (Build.VERSION.SDK_INT >= 31) setAutoEnterEnabled(player.isPlaying && prefs.getBoolean("tlo", true))
    }.build()

    private fun updatePip() { runCatching { setPictureInPictureParams(pipParams()) } }
    private fun enterPip() { runCatching { enterPictureInPictureMode(pipParams()) } }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        if (Build.VERSION.SDK_INT < 31 && player.isPlaying && prefs.getBoolean("tlo", true)) enterPip()
    }

    override fun onPictureInPictureModeChanged(pip: Boolean, cfg: Configuration) {
        super.onPictureInPictureModeChanged(pip, cfg)
        showControls(!pip)
        if (pip && !player.isPlaying) player.play()   // w okienku film gra dalej
    }

    override fun onStop() {
        super.onStop()
        savePos()
        // okienko zamknięte (✕ albo przesunięte poza ekran) - koniec odtwarzania
        if (isInPictureInPictureMode) { player.pause(); finish(); return }
        // poza okienkiem: gra dalej w tle (powiadomienie z Zatrzymaj), gdy włączone „Odtwarzanie w tle”
        if (player.isPlaying && prefs.getBoolean("tlo", true)) PlaybackService.start(this, queue.getOrNull(index)?.title ?: "")
        else player.pause()
    }

    override fun onStart() {
        super.onStart()
        PlaybackService.stop(this)
    }

    override fun onDestroy() {
        savePos(); reportJellyfin()
        gone = true
        ui.removeCallbacksAndMessages(null)
        lektor?.release(); lektor = null
        PlaybackService.stop(this)
        player.stop(); player.detachViews(); player.release()   // biblioteka VLC zostaje na następne otwarcie
        fd?.close()
        if (current?.get() == this) current = null
        super.onDestroy()
    }
}
