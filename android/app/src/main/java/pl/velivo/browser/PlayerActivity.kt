package pl.velivo.browser

import android.app.Activity
import android.app.AlertDialog
import android.app.PictureInPictureParams
import android.content.Context
import android.content.Intent
import android.content.res.Configuration
import android.graphics.Color
import android.media.AudioManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
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
import android.widget.TextView
import android.widget.Toast
import androidx.annotation.OptIn
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.PlayerView
import java.lang.ref.WeakReference

// Odtwarzacz Velivo: telewizja (IPTV), Jellyfin i filmy z telefonu. Pamięta miejsce w filmie, obraz w obrazie,
// granie w tle (powiadomienie), wyłącznik czasowy, gesty: jasność (lewa strona), głośność (prawa), przewijanie w bok;
// w telewizji przesunięcie w górę / w dół = następny / poprzedni kanał.
@OptIn(UnstableApi::class)
class PlayerActivity : Activity() {
    class Item(val title: String, val uri: String, val key: String?, val headers: Map<String, String> = emptyMap(), val jellyId: String? = null, val info: String = "")

    companion object {
        // kolejka ustawiana przed otwarciem (w pamięci - adresy z tokenem nie idą przez Intent ani do logów)
        var queue: List<Item> = emptyList()
        var index = 0
        var live = false
        var current: WeakReference<PlayerActivity>? = null
        fun pauseIfPlaying() { current?.get()?.player?.pause() }
    }

    private lateinit var player: ExoPlayer
    private lateinit var view: PlayerView
    private lateinit var titleView: TextView
    private val ui = Handler(Looper.getMainLooper())
    private val prefs by lazy { getSharedPreferences("velivo", Context.MODE_PRIVATE) }
    private val posPrefs by lazy { getSharedPreferences("filmy", Context.MODE_PRIVATE) }
    private val pw by lazy { Passwords(this) }
    private var sleepAt = 0L

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()
    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_SHORT).show()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        current = WeakReference(this)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        window.decorView.setBackgroundColor(Color.BLACK)
        window.insetsController?.let { it.hide(WindowInsets.Type.systemBars()); it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE }

        val http = DefaultHttpDataSource.Factory().setAllowCrossProtocolRedirects(true).setUserAgent("VLC/3.0.20 LibVLC/3.0.20")   // serwery IPTV często wpuszczają tylko znane odtwarzacze
            .setConnectTimeoutMs(15000).setReadTimeoutMs(20000)
        player = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(DefaultDataSource.Factory(this, http)))
            .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MOVIE).build(), true)
            .setHandleAudioBecomingNoisy(true)   // słuchawki wyjęte - pauza
            .build()
        player.addListener(object : Player.Listener {
            override fun onPlayerError(error: PlaybackException) {
                // kanał bez rozszerzenia w adresie: drugi raz jako HLS albo jako zwykły strumień (MPEG-TS)
                if (retryAs < 2) { retryAs++; load(index, keepRetry = true); return }
                toast(getString(R.string.player_error))   // bez adresu (może mieć token)
            }
            override fun onIsPlayingChanged(isPlaying: Boolean) { updatePip() }
            override fun onPlaybackStateChanged(state: Int) { if (state == Player.STATE_ENDED && !live) { savePos(); next(1) } }
        })
        httpFactory = http

        view = PlayerView(this).apply {
            player = this@PlayerActivity.player
            setShowBuffering(PlayerView.SHOW_BUFFERING_ALWAYS)   // kółko ładowania zamiast czarnego ekranu
            setShowSubtitleButton(true)   // napisy i ścieżki dźwięku (lektor / język) w ustawieniach odtwarzacza
            setBackgroundColor(Color.BLACK)
            keepScreenOn = true
        }
        // górny pasek: tytuł, wyłącznik, okienko, zamknij - duże przyciski
        titleView = TextView(this).apply { setTextColor(Color.WHITE); textSize = 16f; maxLines = 2; setShadowLayer(4f, 0f, 0f, Color.BLACK) }
        val top = LinearLayout(this).apply {
            gravity = Gravity.CENTER_VERTICAL; setPadding(dp(12), dp(10), dp(8), dp(10)); setBackgroundColor(Color.argb(110, 0, 0, 0))
            addView(titleView, LinearLayout.LayoutParams(0, -2, 1f))
            addView(btn("🌙", R.string.sleep_title) { askSleep() })
            addView(btn("⧉", R.string.player_pip) { enterPip() })
            addView(btn("✕", R.string.close_tab_short) { finish() })
        }
        view.setControllerVisibilityListener(PlayerView.ControllerVisibilityListener { v -> top.visibility = v })
        val root = FrameLayout(this)
        root.addView(view, FrameLayout.LayoutParams(-1, -1))
        root.addView(top, FrameLayout.LayoutParams(-1, -2, Gravity.TOP))
        setContentView(root)
        gestures()
        load(index)
        ui.postDelayed(saver, 5000)
    }

    private var httpFactory: DefaultHttpDataSource.Factory? = null

    private fun btn(label: String, tip: Int, click: () -> Unit) = Button(this, null, android.R.attr.borderlessButtonStyle).apply {
        text = label; textSize = 20f; setTextColor(Color.parseColor("#ECE8FF")); contentDescription = getString(tip); tooltipText = getString(tip)
        background = android.graphics.drawable.GradientDrawable().apply { setColor(Color.parseColor("#CC2E2768")); cornerRadius = dp(14).toFloat(); setStroke(dp(1), Color.parseColor("#4A3F8A")) }   // kolory Velivo
        setPadding(0, 0, 0, 0); minWidth = 0; minimumWidth = 0; minHeight = 0; minimumHeight = 0
        setOnClickListener { click() }
        layoutParams = LinearLayout.LayoutParams(dp(52), dp(48)).apply { setMargins(dp(4), 0, dp(4), 0) }
    }

    private var retryAs = 0   // 0 jak w adresie, 1 HLS, 2 zwykły strumień

    private fun load(i: Int, keepRetry: Boolean = false) {
        val it = queue.getOrNull(i) ?: return finish()
        if (!keepRetry) retryAs = 0
        index = i
        titleView.text = it.title + if (it.info.isNotEmpty()) "\n" + it.info else ""
        httpFactory?.setDefaultRequestProperties(it.headers)   // np. klucz Jellyfin w nagłówku, nie w adresie
        val mi = MediaItem.Builder().setUri(it.uri)
        when (retryAs) { 1 -> mi.setMimeType(androidx.media3.common.MimeTypes.APPLICATION_M3U8); 2 -> mi.setMimeType(androidx.media3.common.MimeTypes.VIDEO_MP2T) }
        player.setMediaItem(mi.build())
        player.prepare()
        val pos = it.key?.let { k -> posPrefs.getFloat(k, 0f) } ?: 0f
        if (!live && pos > 15f) { player.seekTo((pos * 1000).toLong()); toast(getString(R.string.video_resumed, String.format("%d:%02d", pos.toInt() / 60, pos.toInt() % 60))) }
        player.play()
        if (live) prefs.edit().putInt("tv-ostatni", i).putString("tv-ostatni-nazwa", it.title).apply()
    }

    private fun next(d: Int) {
        if (queue.isEmpty()) return
        val n = (index + d + queue.size) % queue.size
        if (!live && index + d !in queue.indices) { finish(); return }   // film: koniec kolejki
        load(n)
    }

    // miejsce w filmie (telefon; Jellyfin - także na serwerze), co 5 s i przy wyjściu
    private val saver = object : Runnable {
        override fun run() { savePos(); ui.postDelayed(this, 5000) }
    }

    private fun savePos() {
        if (live) return
        val it = queue.getOrNull(index) ?: return
        val k = it.key ?: return
        val t = player.currentPosition / 1000f; val d = player.duration / 1000f
        if (d < 120 || player.duration == C.TIME_UNSET) return
        posPrefs.edit().apply { if (t > d - 20 || t < 15) remove(k) else putFloat(k, t) }.apply()
    }

    private fun reportJellyfin() {
        val it = queue.getOrNull(index) ?: return
        val id = it.jellyId ?: return
        val pos = player.currentPosition
        val c = Jelly.conf(pw) ?: return
        Thread { Jelly.reportStopped(c, id, pos) }.start()
    }

    private fun gestures() {
        val audio = getSystemService(AudioManager::class.java)!!
        var mode = 0   // 1 jasność, 2 głośność, 3 przewijanie
        var startPos = 0L
        val det = GestureDetector(this, object : GestureDetector.SimpleOnGestureListener() {
            override fun onDown(e: MotionEvent): Boolean { mode = 0; startPos = player.currentPosition; return false }
            override fun onFling(e1: MotionEvent?, e2: MotionEvent, vx: Float, vy: Float): Boolean {
                if (!live || e1 == null || Math.abs(vy) < Math.abs(vx)) return false
                next(if (vy < 0) 1 else -1)   // w górę - następny kanał
                return true
            }
            override fun onScroll(e1: MotionEvent?, e2: MotionEvent, dx: Float, dy: Float): Boolean {
                if (e1 == null) return false
                val tx = e2.x - e1.x; val ty = e2.y - e1.y
                if (mode == 0) mode = if (Math.abs(tx) > Math.abs(ty)) (if (live) -1 else 3) else if (live) -1 else if (e1.x < view.width / 2f) 1 else 2
                when (mode) {
                    1 -> {
                        val lp = window.attributes
                        val cur = if (lp.screenBrightness < 0) 0.5f else lp.screenBrightness
                        lp.screenBrightness = (cur + dy / view.height).coerceIn(0.02f, 1f); window.attributes = lp
                    }
                    2 -> {
                        val max = audio.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
                        if (Math.abs(dy) > view.height / (max * 2f)) audio.adjustStreamVolume(AudioManager.STREAM_MUSIC, if (dy > 0) AudioManager.ADJUST_RAISE else AudioManager.ADJUST_LOWER, AudioManager.FLAG_SHOW_UI)
                    }
                    3 -> player.seekTo((startPos + (tx / view.width * 180_000).toLong()).coerceAtLeast(0))   // cała szerokość = 3 minuty
                }
                return mode > 0
            }
        })
        view.setOnTouchListener { _, e -> det.onTouchEvent(e); false }   // stuknięcie dalej pokazuje przyciski odtwarzacza
    }

    private fun askSleep() {
        val mins = intArrayOf(15, 30, 45, 60, 90, 0)
        val labels = mins.map { if (it == 0) getString(R.string.sleep_none) else getString(R.string.sleep_min, it) }.toTypedArray()
        AlertDialog.Builder(this).setTitle(R.string.sleep_title).setItems(labels) { _, i ->
            ui.removeCallbacks(sleepRun)
            if (mins[i] == 0) { sleepAt = 0; toast(getString(R.string.sleep_off)) }
            else { sleepAt = System.currentTimeMillis() + mins[i] * 60_000L; ui.postDelayed(sleepRun, mins[i] * 60_000L); toast(getString(R.string.sleep_set, mins[i])) }
        }.show()
    }

    private val sleepRun = Runnable { sleepAt = 0; player.pause(); PlaybackService.stop(this) }

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
        view.useController = !pip
        if (pip && !player.isPlaying) player.play()   // w okienku film gra dalej
    }

    override fun onStop() {
        super.onStop()
        savePos()
        // okienko zamknięte (✕ albo przesunięte poza ekran) - koniec odtwarzania
        if (isInPictureInPictureMode) { player.pause(); finish(); return }
        // poza okienkiem: gra dalej w tle (powiadomienie z Zatrzymaj), gdy włączone „Odtwarzanie w tle”
        run {
            if (player.isPlaying && prefs.getBoolean("tlo", true))
                PlaybackService.start(this, queue.getOrNull(index)?.title ?: "")
            else player.pause()
        }
    }

    override fun onStart() {
        super.onStart()
        PlaybackService.stop(this)
    }

    override fun onDestroy() {
        savePos(); reportJellyfin()
        ui.removeCallbacksAndMessages(null)
        PlaybackService.stop(this)
        player.release()
        if (current?.get() == this) current = null
        super.onDestroy()
    }
}
