package pl.velivo.browser

import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.app.KeyguardManager
import android.app.DownloadManager
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
import android.content.res.Configuration
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.net.wifi.WifiManager
import android.hardware.biometrics.BiometricManager
import android.hardware.biometrics.BiometricPrompt
import android.os.Build
import android.os.Bundle
import android.os.CancellationSignal
import android.os.Environment
import android.os.Handler
import android.os.Looper
import android.os.PersistableBundle
import android.text.Editable
import android.text.TextUtils
import android.text.TextWatcher
import android.view.Gravity
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.webkit.CookieManager
import android.webkit.JavascriptInterface
import android.webkit.URLUtil
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.BaseAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ListView
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.webkit.ProfileStore
import androidx.webkit.WebSettingsCompat
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import org.json.JSONObject
import java.io.ByteArrayInputStream

class MainActivity : Activity() {
    // bank: tryb bankowy - osobny stały profil, bez historii i zapisu sesji, wejście odciskiem palca / PIN-em.
    private class Tab(val web: WebView, val priv: Boolean, val bank: Boolean = false) {
        val quiet get() = priv || bank   // bez historii, haseł i zapisu sesji
        var thumb: Bitmap? = null
        var desktop = false   // tryb pulpitu: strona jak na komputerze
    }

    private val tabs = mutableListOf<Tab>()
    private var cur = -1
    private lateinit var frame: FrameLayout
    private lateinit var address: EditText
    private lateinit var progress: ProgressBar
    private lateinit var tabsBtn: Button
    private val prefs by lazy { getSharedPreferences("velivo", Context.MODE_PRIVATE) }
    @Volatile private var blocker = AdBlocker(emptySet(), emptySet())
    @Volatile private var blockedCount = 0

    private val home = "https://www.startpage.com/"
    private val quickAccess by lazy { QuickAccess(this) }
    private val youtubeJs by lazy { assets.open("youtube.js").bufferedReader().use { it.readText() } }
    // bez zachęt „pobierz aplikację” (Startpage i inne) - ten sam skrypt co w Velivo na Windows
    private val noAppPromoJs by lazy { assets.open("velivo-bez-aplikacji.js").bufferedReader().use { it.readText() } }

    override fun onCreate(savedInstanceState: Bundle?) {
        setTheme(if (contrast) R.style.Velivo_Contrast else if (dark) R.style.Velivo_Dark else R.style.Velivo_Light)
        super.onCreate(savedInstanceState)
        catchCrashes()
        Thread { runCatching { if (Tv.sources(passwords).isNotEmpty() || Jelly.conf(passwords) != null) Odswiezanie.plan(this) } }.start()   // nocne odświeżanie TV i biblioteki
        window.statusBarColor = BAR
        window.navigationBarColor = BAR
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(BAR) }
        val bar = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(6), dp(6), dp(6), dp(6)) }
        address = EditText(this).apply {
            hint = getString(R.string.address_hint)
            setSingleLine()
            imeOptions = EditorInfo.IME_ACTION_GO
            inputType = EditorInfo.TYPE_TEXT_VARIATION_URI
            setSelectAllOnFocus(true)
            textSize = 15f
            setTextColor(TEXT)
            setHintTextColor(c("#4F4380", "#A79FD0"))
            setPadding(dp(14), dp(8), dp(14), dp(8))
            background = round(FIELD)
            setOnEditorActionListener { _, _, _ -> go(text.toString()); true }
        }
        bar.addView(address, LinearLayout.LayoutParams(0, -2, 1f))
        tabsBtn = button("1", R.string.tabs, BLUE, BLUE_BG) { showTabs() }.velivo()
        bar.addView(button("🕶", R.string.new_private_tab, PURPLE, PRIV_BG) { newTab(true, home) })
        bar.addView(tabsBtn)
        // logo Velivo zamiast ⋮ - otwiera menu Velivo
        bar.addView(ImageView(this).apply {
            // logo na całą wysokość przycisku (samo jest zaokrąglonym kafelkiem - bez dodatkowej ramki)
            setImageResource(R.mipmap.ic_launcher)
            scaleType = ImageView.ScaleType.FIT_CENTER
            adjustViewBounds = true
            contentDescription = getString(R.string.menu)
            tooltipText = getString(R.string.menu)
            isClickable = true
            setOnClickListener { showMenu(it) }
        }, LinearLayout.LayoutParams(dp(40), dp(40)).apply { setMargins(dp(3), 0, dp(3), 0) })
        progress = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 100
            progressTintList = android.content.res.ColorStateList.valueOf(BRAND2)
        }
        frame = FrameLayout(this)
        val nav = LinearLayout(this).apply { gravity = Gravity.CENTER; setPadding(dp(4), dp(4), dp(4), dp(4)) }
        fun navBtn(b: Button) = nav.addView(b, LinearLayout.LayoutParams(0, dp(44), 1f))
        navBtn(button("◀", R.string.back, BLUE, BLUE_BG) { stopSpeaking(); web()?.let { if (it.canGoBack()) it.goBack() } })
        navBtn(button("▶", R.string.forward, BLUE, BLUE_BG) { web()?.let { if (it.canGoForward()) it.goForward() } })
        navBtn(button("⟳", R.string.reload, GREEN, GREEN_BG) { web()?.reload() })
        navBtn(button("⌂", R.string.home, ORANGE, ORANGE_BG) { web()?.loadUrl(home) })
        navBtn(button("＋", R.string.new_tab, BLUE, BLUE_BG) { newTab(false, QuickAccess.URL) }.velivo())
        root.addView(bar)
        root.addView(progress, LinearLayout.LayoutParams(-1, 6))
        root.addView(frame, LinearLayout.LayoutParams(-1, 0, 1f))
        root.addView(buildFindBar())
        root.addView(buildPickBar())
        root.addView(nav)
        // ▶ płaski pasek YouTube na samym dole: karta z YouTube, jeśli już otwarta; inaczej nowa (bez reklam, gra w tle, obraz w obrazie)
        ytBar = TextView(this).apply {
            text = if (BuildConfig.PELNA) "▶  YouTube  ·  " + getString(R.string.youtube_flat) else "▶  YouTube"
            gravity = Gravity.CENTER; textSize = 13f; setTextColor(Color.WHITE); setBackgroundColor(Color.parseColor("#E62117"))
            contentDescription = getString(ytLabel); tooltipText = getString(ytLabel)
            isClickable = true
            foreground = android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(Color.argb(60, 255, 255, 255)), null, null)
            // przytrzymanie: wyłącznik czasowy (muzyka do snu)
            setOnLongClickListener { askSleep(); true }
            setOnClickListener {
                val i = tabs.indexOfFirst { !it.priv && !it.bank && (Uri.parse(it.web.url ?: "").host ?: "").endsWith("youtube.com") }
                if (i >= 0) show(i) else newTab(false, "https://m.youtube.com/")
            }
        }
        root.addView(ytBar, LinearLayout.LayoutParams(-1, dp(30)))
        uiScale = prefs.getFloat("skala", 1f)
        chrome = listOf(bar, nav, ytBar)
        pipBars = listOf(bar, progress, nav, ytBar, findBar, pickBar)
        chrome.forEach { applyScale(it) }
        setContentView(root)
        if (Build.VERSION.SDK_INT >= 30) {
            val light = WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS or WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS
            window.insetsController?.setSystemBarsAppearance(if (dark) 0 else light, light)
        }
        // Android 15+ rysuje pod paskami systemu - zostaw na nie miejsce (i na klawiaturę).
        root.setOnApplyWindowInsetsListener { v, insets ->
            val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.ime())
            v.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }

        startFilters()
        window.decorView.post { showLastCrash() }
        Karteczki.schedule(this)
        // stare zapisy miejsca w filmie (także z podglądów na stronie głównej YouTube) - jednorazowo od nowa
        if (!prefs.getBoolean("filmy-v2", false)) { getSharedPreferences("filmy", Context.MODE_PRIVATE).edit().clear().apply(); prefs.edit().putBoolean("filmy-v2", true).apply() }
        Thread { runCatching { Karteczki.notifyDue(this) } }.start()   // termin dziś / jutro, gdy Velivo otwarte po 9:00
        if (intent?.action == Karteczki.ACTION_NOTES) window.decorView.post { showNotes(intent.getStringExtra("id")) }
        if (intent?.action == Budzik.ACTION_WAKE) window.decorView.post { wakeUp() }
        if (intent?.action == Widzet.ACTION_SEARCH || intent?.action == Widzet.ACTION_YT) { val a = intent.action; window.decorView.post { widgetAction(a) } }
        if (intent?.action == TvPlan.ACTION) window.decorView.post { startPlanned(intent.getIntExtra("id", -1)) }
        Thread { runCatching { vaultDomains = passwords.all().map { Ochrona.regDomain(it.host) }.filter { it.contains('.') }.toHashSet() } }.start()
        // Powiadomienie odtwarzania w tle wymaga zgody (Android 13+).
        if (Build.VERSION.SDK_INT >= 33 && prefs.getBoolean("tlo", true) && !prefs.getBoolean("zapytano-powiadomienia", false)) {
            prefs.edit().putBoolean("zapytano-powiadomienia", true).apply()
            requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 1)
        }
        dropPrivateProfile() // resztki po poprzedniej sesji
        if (intent?.action == Intent.ACTION_SEND) sendSharedFile(intent)
        val link = intent?.dataString?.takeIf { intent?.action != Intent.ACTION_SEND }
        if (!restoreTabs() && link == null) newTab(false, home)
        if (link != null) newTab(false, link)
    }

    // Kolory jak w Velivo na Windows; motyw: 0 = jak telefon, 1 = jasny, 2 = ciemny (domyślny), 3 = czarno-biały (wysoki kontrast).
    private val contrast by lazy { prefs.getInt("motyw", 2) == 3 }
    private val dark by lazy {
        when (prefs.getInt("motyw", 2)) {   // domyślnie ciemny Velivo
            1 -> false
            2, 3 -> true
            else -> (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES
        }
    }
    // czarno-biały: ciemne kolory -> czerń, jasne -> biel (tła czarne, napisy i ramki białe, ikony kolorowe dla czytelności)
    private fun c(light: String, night: String): Int {
        val v = Color.parseColor(if (dark) night else light)
        if (!contrast) return v
        return if (Color.red(v) * 0.299 + Color.green(v) * 0.587 + Color.blue(v) * 0.114 < 128) Color.BLACK else Color.WHITE
    }
    // Kolory Velivo z ikony: fiolet -> turkus. Tła paskow i przyciskow w odcieniach fioletu Velivo (nie szare).
    private val BRAND1 by lazy { if (contrast) Color.BLACK else Color.parseColor("#6D3FDB") }
    private val BRAND2 by lazy { if (contrast) Color.WHITE else Color.parseColor("#2BA9D8") }
    private val BAR by lazy { c("#EEE9FB", "#1B1733") }
    private val TEXT by lazy { c("#2A2150", "#ECE8FF") }
    private val FIELD by lazy { c("#FFFFFF", "#120F24") }
    private val GRAY_BG by lazy { c("#E2DBF7", "#2A2450") }
    private val BLUE by lazy { c("#4A2FB8", "#B3A6FF") }
    private val BLUE_BG by lazy { c("#E4DDFB", "#2E2768") }
    private val GREEN by lazy { c("#15803D", "#86EFAC") }
    private val GREEN_BG by lazy { c("#DCFCE7", "#14532D") }
    private val ORANGE by lazy { c("#C2410C", "#FDBA74") }
    private val ORANGE_BG by lazy { c("#FFEDD5", "#7C2D12") }
    private val PRIV_BG by lazy { c("#E6DEFD", "#4C1D95") }
    private val PURPLE by lazy { c("#5B21B6", "#DDD6FE") }
    private val BANK_BG by lazy { c("#CCFBF1", "#134E4A") }

    private companion object {
        const val PRIVATE_PROFILE = "prywatna"
        const val BANK_PROFILE = "bank"
        const val REQ_VLPAIR = 7
        const val REQ_MEDIA = 8
        const val REQ_ROLE = 9
        const val REQ_VIDEOS = 10
        val YOUTUBE_ORIGINS = setOf("https://www.youtube.com", "https://m.youtube.com", "https://youtube.com", "https://music.youtube.com")
        const val BANK_IDLE_MS = 10 * 60 * 1000L

        // Przy wysłaniu formularza: pierwsze wypełnione pole hasła i pole tekstowe przed nim.
        const val CAPTURE_JS = """(function(){if(window.__vh||!window.VelivoHasla)return;window.__vh=1;
function grab(){var ps=[].slice.call(document.querySelectorAll('input[type=password]')).filter(function(p){return p.value});
if(!ps.length)return;var p=ps[0],ins=[].slice.call(document.querySelectorAll('input')),u='';
for(var j=ins.indexOf(p)-1;j>=0;j--){var t=(ins[j].type||'text').toLowerCase();if((t=='text'||t=='email'||t=='tel')&&ins[j].value){u=ins[j].value;break;}}
VelivoHasla.zapisz(u,p.value);}
document.addEventListener('submit',grab,true);
document.addEventListener('click',function(e){var b=e.target.closest&&e.target.closest('button,input[type=submit],[role=button]');if(b)grab();},true);
document.addEventListener('keydown',function(e){if(e.key=='Enter')grab();},true);})();"""

        // Wpisuje login i hasło tak, jakby pisał użytkownik (zdarzenia input/change dla stron w React itp.).
        const val FILL_JS = """(function(u,p){var ps=document.querySelectorAll('input[type=password]');if(!ps.length)return;
function set(el,v){Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,v);
el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));}
var p0=ps[0],ins=[].slice.call(document.querySelectorAll('input'));
for(var j=ins.indexOf(p0)-1;j>=0;j--){var t=(ins[j].type||'text').toLowerCase();if(t=='text'||t=='email'||t=='tel'){if(u)set(ins[j],u);break;}}
set(p0,p);})"""
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        if (intent.action == Karteczki.ACTION_NOTES) { showNotes(intent.getStringExtra("id")); return }
        if (intent.action == Budzik.ACTION_WAKE) { wakeUp(); return }
        if (intent.action == Widzet.ACTION_SEARCH || intent.action == Widzet.ACTION_YT) { widgetAction(intent.action); return }
        if (intent.action == TvPlan.ACTION) { startPlanned(intent.getIntExtra("id", -1)); return }
        if (intent.action == PlaybackService.ACTION_SLEEP) { setSleep(((sleepAt - System.currentTimeMillis()).coerceAtLeast(0) / 60000).toInt() + 30); moveTaskToBack(true); return }
        if (intent.action == PlaybackService.ACTION_STOP) {
            tabs.forEach { it.web.evaluateJavascript("document.querySelectorAll('video,audio').forEach(function(m){m.pause()})", null) }
            tts?.stop()
            PlayerActivity.pauseIfPlaying()
            PlaybackService.stop(this)
        startLan()
            return
        }
        if (intent.action == Intent.ACTION_SEND) { sendSharedFile(intent); return }
        intent.dataString?.let { newTab(false, it) }
    }

    // --- Pełny ekran filmów: na cały ekran, bez pasków systemu, telefon obraca się wg filmu ------
    private var fullView: View? = null
    private var fullCallback: WebChromeClient.CustomViewCallback? = null

    private fun enterFullscreen(v: View, cb: WebChromeClient.CustomViewCallback) {
        if (fullView != null) { cb.onCustomViewHidden(); return }
        fullView = v
        fullCallback = cb
        (window.decorView as FrameLayout).addView(v, FrameLayout.LayoutParams(-1, -1))
        v.setBackgroundColor(Color.BLACK)
        window.insetsController?.let {
            it.hide(WindowInsets.Type.systemBars())
            it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
        if (!isInPictureInPictureMode) requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR   // zmiana obrotu w okienku - Android zamyka okienko
    }

    // widżet na pulpicie: 🔎 - pasek adresu z klawiaturą, ▶ - YouTube (jak czerwony pasek)
    private fun widgetAction(a: String?) {
        if (a == Widzet.ACTION_YT) { ytBar.performClick(); return }
        address.requestFocus(); address.selectAll()
        address.postDelayed({ getSystemService(android.view.inputmethod.InputMethodManager::class.java)?.showSoftInput(address, 0) }, 200)
    }

    private fun exitFullscreen() {
        val v = fullView ?: return
        (window.decorView as FrameLayout).removeView(v)
        fullView = null
        window.insetsController?.show(WindowInsets.Type.systemBars())
        if (!isInPictureInPictureMode) requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        fullCallback?.onCustomViewHidden()
        fullCallback = null
    }

    // --- Obraz w obrazie (PiP): film gra w okienku po wyjściu z Velivo ---------------
    private lateinit var ytBar: TextView
    private var pipBars = listOf<View>()
    private val pipCheck = Handler(Looper.getMainLooper())
    @Volatile private var videoPlaying = false

    // co 2 s (tylko gdy Velivo na ekranie): czy w bieżącej karcie gra film - Android 12+ wchodzi wtedy w PiP sam
    private val pipTick = object : Runnable {
        override fun run() {
            val t = tabs.getOrNull(cur)
            if (t != null && !t.bank) t.web.evaluateJavascript("(function(){var v=[].slice.call(document.querySelectorAll('video')).filter(function(m){return !m.paused&&!m.ended&&m.readyState>2})[0];" +
                    "return v?JSON.stringify({t:v.currentTime,d:v.duration}):''})()") { r ->
                val j = runCatching { org.json.JSONObject(org.json.JSONTokener(r).nextValue() as String) }.getOrNull()
                val playing = j != null
                if (playing != videoPlaying) { videoPlaying = playing; updatePip() }
                if (j != null && !t.priv) saveVideoPos(t.web.url, j.optDouble("t"), j.optDouble("d"))
            }
            pipCheck.postDelayed(this, 2000)
        }
    }

    // --- Miejsce w filmie: telefon pamięta, gdzie przerwałeś (także bez logowania), i wznawia od tego miejsca ---
    // klucz: film YouTube (v=…) albo adres strony; ostatnie 300 filmów, tylko w telefonie, bez kart prywatnych i bankowych
    private fun videoKey(url: String?): String? {
        val u = Uri.parse(url ?: return null)
        val host = u.host ?: return null
        // YouTube: tylko strona filmu (v=…) - strona główna z podglądami, szorty i transmisje nie są zapamiętywane
        if (host.endsWith("youtube.com")) return u.getQueryParameter("v")?.let { "film-yt-$it" }
        if (host == "youtu.be") return u.lastPathSegment?.let { "film-yt-$it" }
        val path = u.path ?: ""
        if (path.length < 2) return null   // strona główna serwisu - to nie jest konkretny film
        return "film-" + (host + path).hashCode()
    }

    private fun saveVideoPos(url: String?, t: Double, d: Double) {
        val k = videoKey(url) ?: return
        if (d.isNaN() || d.isInfinite() || d < 120 || d > 6 * 3600) return   // krótkie filmy i transmisje na żywo - bez zapamiętywania
        val p = getSharedPreferences("filmy", Context.MODE_PRIVATE)
        val e = p.edit()
        if (t > d - 20 || t < 15) e.remove(k) else e.putFloat(k, t.toFloat())   // obejrzany do końca - następnym razem od początku
        if (p.all.size > 300) p.all.keys.take(50).forEach { e.remove(it) }
        e.apply()
    }

    private val resumedKey = java.util.WeakHashMap<WebView, String>()   // raz na film, nie przy każdym kliknięciu na stronie

    private fun resumeVideo(view: WebView, url: String) {
        if (tabs.firstOrNull { it.web == view }?.priv == true) return
        val k = videoKey(url) ?: return
        if (resumedKey[view] == k) return
        resumedKey[view] = k
        val pos = getSharedPreferences("filmy", Context.MODE_PRIVATE).getFloat(k, 0f)
        if (pos < 15f) return
        val js = "(function(t){var v=document.querySelector('video');if(!v)return false;if(Math.abs(v.currentTime-t)>10)v.currentTime=t;return true})($pos)"
        view.postDelayed({ view.evaluateJavascript(js) { r ->
            if (r == "true") toast(getString(R.string.video_resumed, String.format("%d:%02d", pos.toInt() / 60, pos.toInt() % 60)))
            else view.postDelayed({ view.evaluateJavascript(js, null) }, 3000)   // odtwarzacz jeszcze się ładuje
        } }, 2500)
    }

    private fun pipParams() = android.app.PictureInPictureParams.Builder().setAspectRatio(android.util.Rational(16, 9)).apply {
        if (Build.VERSION.SDK_INT >= 31) setAutoEnterEnabled(videoPlaying && prefs.getBoolean("tlo", true) && !youtubeLimited())
    }.build()

    private fun updatePip() { runCatching { setPictureInPictureParams(pipParams()) } }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        // starsze Androidy (10-11): wejście w PiP ręcznie
        if (Build.VERSION.SDK_INT < 31 && videoPlaying && prefs.getBoolean("tlo", true) && !youtubeLimited()) runCatching { enterPictureInPictureMode(pipParams()) }
    }

    override fun onPictureInPictureModeChanged(pip: Boolean, cfg: Configuration) {
        super.onPictureInPictureModeChanged(pip, cfg)
        pipBars.forEach { it.visibility = if (pip) View.GONE else if (it == findBar || it == pickBar) it.visibility else View.VISIBLE }
        // w okienku tylko film, na całą powierzchnię
        web()?.evaluateJavascript(if (pip) "(function(){var s=document.getElementById('velivo-pip')||document.createElement('style');s.id='velivo-pip';" +
            "s.textContent='video{position:fixed!important;inset:0!important;width:100vw!important;height:100vh!important;z-index:2147483647!important;background:#000!important;object-fit:contain!important}';" +
            "document.documentElement.appendChild(s);var v=document.querySelector('video');if(v&&v.paused)v.play();})()" else "(function(){var s=document.getElementById('velivo-pip');if(s)s.remove();})()", null)
    }

    // okienko zamknięte (✕) - film nie gra dalej w tle bez obrazu
    // Android zatrzymuje Velivo także przy otwartym okienku (onStop) - nie da się odróżnić od zamknięcia okienka,
    // więc bez pauzy: film gra dalej jak w tle, z powiadomieniem „Zatrzymaj”.
    override fun onStop() {
        super.onStop()
        if (isInPictureInPictureMode && prefs.getBoolean("tlo", true)) PlaybackService.start(this, web()?.title ?: "")
    }

    // --- 🌙 Wyłącznik czasowy: muzyka / film / czytanie cichnie po wybranym czasie (do snu) ---
    private var sleepAt = 0L
    private val sleepRun = Runnable {
        sleepAt = 0
        tabs.forEach { it.web.evaluateJavascript("document.querySelectorAll('video,audio').forEach(function(m){m.pause()})", null) }
        tts?.stop()
        PlayerActivity.pauseIfPlaying()
        PlaybackService.stop(this)
    }

    private fun setSleep(min: Int) {
        pipCheck.removeCallbacks(sleepRun)
        if (min <= 0) { sleepAt = 0; toast(getString(R.string.sleep_off)); return }
        sleepAt = System.currentTimeMillis() + min * 60_000L
        pipCheck.postDelayed(sleepRun, min * 60_000L)
        toast(getString(R.string.sleep_set, min))
    }

    private fun askSleep() {
        val mins = intArrayOf(-5, -4, -1, 15, 30, 45, 60, 90, -3, 0, -2)
        val wake = Budzik.time(this)
        val labels = mins.map { when (it) {
            -5 -> "🎵 " + getString(R.string.yt_my_songs); -4 -> "⭐ " + getString(R.string.yt_add)
            -1 -> getString(R.string.sleep_track); 0 -> getString(R.string.sleep_none); -3 -> getString(R.string.sleep_own)
            -2 -> if (wake >= 0) getString(R.string.wake_menu_on, String.format("%d:%02d", wake / 60, wake % 60)) else getString(R.string.wake_menu)
            else -> getString(R.string.sleep_min, it) } }.toTypedArray()
        AlertDialog.Builder(this).setTitle(R.string.sleep_title).setItems(labels) { _, i -> when (mins[i]) { -5 -> showYtSongs(); -4 -> ytAddCurrent(); -1 -> sleepAfterTrack(); -2 -> askWake(); -3 -> askSleepMinutes(); else -> setSleep(mins[i]) } }.show()
    }

    // ⏰ Pobudka: utwór z bieżącej karty (np. YouTube) + godzina; codziennie
    private fun askWake() {
        val w = web()
        val url = w?.url?.takeIf { it.startsWith("http") && !QuickAccess.isQuickAccess(it) }
        val items = mutableListOf<Pair<String, () -> Unit>>()
        // dowolny utwór: z bieżącej karty, z zakładek albo z historii; potem godzina i dni
        if (url != null) items.add(getString(R.string.wake_this, w?.title ?: url) to { wakeWhen(url, w?.title ?: url) })
        for ((key, label) in listOf("zakladki" to R.string.wake_from_bookmarks, "historia" to R.string.wake_from_history))
            if (list(key).isNotEmpty()) items.add(getString(label) to {
                val rows = list(key).map { val u = it.substringAfter('\t'); Row(it.substringBefore('\t').ifBlank { u }, u, u) }
                showList(label, rows, "pobudka-$key", onOpen = { row, d -> d.dismiss(); wakeWhen(row.key as String, row.title) }, onDelete = { row -> removeFrom(key, row.key as String) })
            })
        if (Budzik.time(this) >= 0) {
            items.add(getString(R.string.wake_test) to { wakeUp(test = true) })
            items.add(getString(R.string.wake_off) to { Budzik.off(this); toast(getString(R.string.wake_off_done)) })
        }
        if (items.isEmpty()) { toast(getString(R.string.wake_need_track)); return }
        val cur = Budzik.time(this)
        AlertDialog.Builder(this).setTitle(if (cur >= 0) getString(R.string.wake_title_on, String.format("%d:%02d", cur / 60, cur % 60) + " (" + daysName(Budzik.days(this)) + ")", Budzik.title(this)) else getString(R.string.wake_menu))
            .setItems(items.map { it.first }.toTypedArray()) { _, i -> items[i].second() }.show()
    }

    private fun wakeWhen(url: String, title: String) {
        val m = Budzik.time(this).takeIf { it >= 0 } ?: (7 * 60)
        android.app.TimePickerDialog(this, { _, h, min ->
            val opts = intArrayOf(127, 31, 96, -1, 0)   // codziennie, pon-pt, weekend, wybrane, jednorazowo
            val labels = arrayOf(getString(R.string.days_all), getString(R.string.days_work), getString(R.string.days_weekend), getString(R.string.days_pick), getString(R.string.days_once))
            fun save(days: Int) {
                Budzik.set(this, url, title, h * 60 + min, days)
                // Android 12+: budzik o dokładnej godzinie za zgodą (wersja ze sklepu) - bez zgody budzi z dokładnością kilku minut
                if (Build.VERSION.SDK_INT >= 31 && getSystemService(android.app.AlarmManager::class.java)?.canScheduleExactAlarms() == false)
                    runCatching { startActivity(Intent(android.provider.Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM, Uri.parse("package:$packageName"))); toast(getString(R.string.wake_exact)) }
                toast(getString(R.string.wake_set, String.format("%d:%02d", h, min)) + " · " + daysName(days))
            }
            AlertDialog.Builder(this).setTitle(R.string.days_title).setItems(labels) { _, i ->
                if (opts[i] >= 0) { save(opts[i]); return@setItems }
                val names = java.time.DayOfWeek.values().map { it.getDisplayName(java.time.format.TextStyle.FULL, resources.configuration.locales[0]) }.toTypedArray()
                val checked = BooleanArray(7) { Budzik.days(this) and (1 shl it) != 0 }
                AlertDialog.Builder(this).setTitle(R.string.days_pick).setMultiChoiceItems(names, checked) { _, k, on -> checked[k] = on }
                    .setPositiveButton(android.R.string.ok) { _, _ -> save((0 until 7).filter { checked[it] }.sumOf { 1 shl it }.takeIf { it != 0 } ?: 127) }
                    .setNegativeButton(android.R.string.cancel, null).show()
            }.show()
        }, m / 60, m % 60, true).show()
    }

    private fun daysName(d: Int): String = when (d) {
        127 -> getString(R.string.days_all); 31 -> getString(R.string.days_work); 96 -> getString(R.string.days_weekend); 0 -> getString(R.string.days_once)
        else -> java.time.DayOfWeek.values().filter { d and (1 shl (it.value - 1)) != 0 }.joinToString(", ") { it.getDisplayName(java.time.format.TextStyle.SHORT, resources.configuration.locales[0]) }
    }

    // rano: Velivo na zablokowanym ekranie, utwór w nowej karcie, głośność narasta przez minutę
    private fun wakeUp(test: Boolean = false) {
        val url = Budzik.url(this) ?: return
        setShowWhenLocked(true); setTurnScreenOn(true)
        if (!test) { if (Budzik.days(this) == 0) Budzik.off(this) else Budzik.schedule(this) }   // jednorazowo - koniec; inaczej następny wybrany dzień
        newTab(false, url)
        val w = web() ?: return
        w.settings.mediaPlaybackRequiresUserGesture = false
        val start = Runnable {
            w.evaluateJavascript("(function(){var v=document.querySelector('video,audio');if(!v)return;v.muted=false;v.volume=0.15;var p=v.play();if(p&&p.catch)p.catch(function(){});" +
                "var t=setInterval(function(){v.volume=Math.min(1,v.volume+0.05);if(v.volume>=1)clearInterval(t);},3000);})()", null)
        }
        w.postDelayed(start, 4000); w.postDelayed(start, 9000)   // strona mogła jeszcze nie mieć odtwarzacza
        AlertDialog.Builder(this).setTitle(R.string.wake_now).setMessage(Budzik.title(this)).setCancelable(false)
            .setPositiveButton(R.string.wake_stop) { _, _ -> setShowWhenLocked(false); setTurnScreenOn(false); w.evaluateJavascript("document.querySelectorAll('video,audio').forEach(function(m){m.pause()})", null) }
            .setNegativeButton(R.string.wake_snooze) { _, _ ->
                w.evaluateJavascript("document.querySelectorAll('video,audio').forEach(function(m){m.pause()})", null)
                Budzik.schedule(this, 10); setShowWhenLocked(false); setTurnScreenOn(false)
            }.show()
    }

    private fun askSleepMinutes() {
        val num = EditText(this).apply { setText("120"); inputType = android.text.InputType.TYPE_CLASS_NUMBER; setSelectAllOnFocus(true) }
        AlertDialog.Builder(this).setTitle(R.string.sleep_own).setView(LinearLayout(this).apply { setPadding(dp(20), dp(8), dp(20), 0); addView(num, LinearLayout.LayoutParams(-1, -2)) })
            .setPositiveButton(android.R.string.ok) { _, _ -> num.text.toString().toIntOrNull()?.takeIf { it in 1..1440 }?.let { setSleep(it) } }
            .setNegativeButton(android.R.string.cancel, null).show()
    }

    // 🌙 po zakończeniu utworu, który teraz gra: koniec utworu albo przejście YouTube do następnego = pauza
    private fun sleepAfterTrack() {
        val t = tabs.firstOrNull { (Uri.parse(it.web.url ?: "").host ?: "").endsWith("youtube.com") } ?: tabs.getOrNull(cur) ?: return
        t.web.evaluateJavascript("""(function(){var v=[].slice.call(document.querySelectorAll('video,audio')).filter(function(m){return !m.paused})[0]||document.querySelector('video');
if(!v)return false;var src=v.currentSrc,href=location.href;clearInterval(window.__vSen);
function stop(){clearInterval(window.__vSen);document.querySelectorAll('video,audio').forEach(function(m){m.pause()});}
v.addEventListener('ended',stop,{once:true});
window.__vSen=setInterval(function(){var c=document.querySelector('video');if(location.href!=href||(c&&c.currentSrc!=src)||v.ended)stop();},500);return true;})()""") { r ->
            toast(getString(if (r == "true") R.string.sleep_track_set else R.string.sleep_track_none))
        }
    }

    // --- Odtwarzanie w tle -----------------------------------------------------
    // Po wyjściu z Velivo: jeśli gra film lub muzyka, powiadomienie trzyma odtwarzanie przy życiu.
    // ostrożna wersja ze sklepu ("sklep"): YouTube nie gra w tle ani w okienku
    private val ytLabel get() = if (BuildConfig.PELNA) R.string.youtube else R.string.youtube_plain

    private fun youtubeLimited(): Boolean = !BuildConfig.YOUTUBE && (Uri.parse(web()?.url ?: "").host ?: "").endsWith("youtube.com")

    private fun backgroundPlayback() {
        if (!prefs.getBoolean("tlo", true) || youtubeLimited()) return
        val w = web() ?: return
        if (tabs.getOrNull(cur)?.bank == true) return
        w.evaluateJavascript("[].some.call(document.querySelectorAll('video,audio'),function(m){return !m.paused&&!m.ended})") { playing ->
            if (playing != "true") return@evaluateJavascript
            runCatching {
                PlaybackService.start(this, w.title ?: "")
            }
        }
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    private fun round(color: Int) = GradientDrawable().apply { setColor(color); cornerRadius = dp(8).toFloat(); if (contrast && color != Color.TRANSPARENT) setStroke(dp(2), Color.WHITE) }   // czarno-biały: biała ramka

    // gradient Velivo (jak ikona) - przyciski glowne
    private fun brand(radius: Int = 8) = if (contrast) GradientDrawable().apply { setColor(Color.BLACK); setStroke(dp(2), Color.WHITE); cornerRadius = dp(radius).toFloat() }
        else GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(BRAND1, BRAND2)).apply { cornerRadius = dp(radius).toFloat() }

    private fun Button.velivo(): Button { background = brand(); setTextColor(Color.WHITE); return this }

    // przycisk na całą szerokość okna: wysokość według napisu (dwie linie zamiast uciętego tekstu)
    private fun wide(b: Button) = b.apply { layoutParams = LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(8); bottomMargin = dp(4) }; setPadding(dp(12), dp(10), dp(12), dp(10)) }

    private fun button(label: String, tip: Int, fg: Int, bg: Int, click: (View) -> Unit) = Button(this, null, android.R.attr.borderlessButtonStyle).apply {
        text = label
        isAllCaps = false   // normalne litery - czytelniej, bez ucinania długich napisów
        textSize = 18f
        setTextColor(fg)
        background = round(bg)
        setPadding(dp(12), 0, dp(12), 0)
        minHeight = dp(40)
        layoutParams = LinearLayout.LayoutParams(-2, dp(40)).apply { setMargins(dp(3), 0, dp(3), 0) }
        contentDescription = getString(tip)
        tooltipText = getString(tip)
        minWidth = 0
        minimumWidth = 0
        minimumHeight = 0
        setOnClickListener(click)
    }

    private fun web(): WebView? = tabs.getOrNull(cur)?.web

    @SuppressLint("SetJavaScriptEnabled")
    private fun newTab(priv: Boolean, url: String, bank: Boolean = false) {
        // Gra w tle i w okienku: gdy Android zatrzymuje Velivo (zgaszony ekran, okienko), WebView chowa stronę i wyłącza
        // obraz i dźwięk filmu. Dopóki w tej karcie gra film, a granie w tle jest włączone - strona dalej "widoczna".
        val w = object : WebView(this) {
            private var kept = false
            override fun onWindowVisibilityChanged(visibility: Int) {
                val keep = visibility != View.VISIBLE && videoPlaying && this === web() && tabs.getOrNull(cur)?.bank != true &&
                    prefs.getBoolean("tlo", true) && !youtubeLimited()
                if (keep) { kept = true; super.onWindowVisibilityChanged(View.VISIBLE); return }
                // powrót po "udawanej" widoczności: obraz strony trzeba odbudować (inaczej biały ekran) - na moment ukryj i pokaż
                if (visibility == View.VISIBLE && kept) { kept = false; super.onWindowVisibilityChanged(View.INVISIBLE); super.onWindowVisibilityChanged(View.VISIBLE); return }
                super.onWindowVisibilityChanged(visibility)
            }
        }
        if (bank) WebViewCompat.setProfile(w, BANK_PROFILE)
        // Karta prywatna: osobny profil (ciasteczka, pamięć stron), kasowany po zamknięciu ostatniej prywatnej karty.
        if (priv) {
            if (privateSupported) WebViewCompat.setProfile(w, PRIVATE_PROFILE)
            else toast(getString(R.string.private_limited))
        }
        w.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = !priv || privateSupported
            builtInZoomControls = true
            displayZoomControls = false
            setSupportMultipleWindows(false)
            if (priv) cacheMode = android.webkit.WebSettings.LOAD_NO_CACHE
        }
        // Strony bez własnego ciemnego wyglądu przyciemniane automatycznie w trybie ciemnym.
        if (WebViewFeature.isFeatureSupported(WebViewFeature.ALGORITHMIC_DARKENING))
            WebSettingsCompat.setAlgorithmicDarkeningAllowed(w.settings, true)
        CookieManager.getInstance().setAcceptThirdPartyCookies(w, false)
        // Reklamy na YouTube: skrypt uruchamiany przed stroną (tylko na domenach YouTube).
        // wersje "pelna" i "sklepyt" - w ostrożnej wersji sklepowej bez skryptu YouTube
        if (BuildConfig.YOUTUBE && prefs.getBoolean("adblock", true) && WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT))
            WebViewCompat.addDocumentStartJavaScript(w, youtubeJs, YOUTUBE_ORIGINS)
        if (WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT))
            runCatching { WebViewCompat.addDocumentStartJavaScript(w, noAppPromoJs, setOf("*")) }
        val quiet = priv || bank
        if (!quiet) w.addJavascriptInterface(PasswordBridge(w), "VelivoHasla")
        if (!quiet) quickAccess.attach(w)
        w.webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(view: WebView, req: WebResourceRequest): WebResourceResponse? {
                if (req.url.host == QuickAccess.HOST) return if (quiet) null else quickAccess.serve(req.url)
                if (prefs.getBoolean("adblock", true) && !siteAllowed(pageHost[view]) && isBlocked(req.url)) {
                    blockedCount++
                    req.url.host?.let { h -> pageBlocked[view]?.merge(Ochrona.regDomain(h), 1, Int::plus) }
                    return WebResourceResponse("text/plain", "utf-8", ByteArrayInputStream(ByteArray(0)))
                }
                return null
            }
            // każda nowa strona w karcie: licznik prywatności od zera, ochrona przed podróbkami, najpierw https
            override fun onPageStarted(view: WebView, url: String, favicon: Bitmap?) {
                val u = Uri.parse(url)
                val host = u.host?.lowercase() ?: return
                view.settings.textZoom = zoomKey(url)?.let { prefs.getInt(it, 100) } ?: 100   // wielkość zapamiętana dla tej strony
                if (host == QuickAccess.HOST) return
                pageHost[view] = host
                pageBlocked[view] = java.util.concurrent.ConcurrentHashMap()
                // strona sama wraca z https na http - zostaw ją (inaczej pętla)
                if (u.scheme == "http" && host in httpsTried) { httpsTried.remove(host); httpAllowed.add(host) }
                if (u.scheme == "http" && !isHomeNetwork(host) && host !in httpAllowed) {
                    view.stopLoading(); httpsTried.add(host)
                    view.loadUrl(url.replaceFirst("http://", "https://")); return
                }
                if (u.scheme != "http" && u.scheme != "https") return
                val fake = if (host in protectionAllowed) null else Ochrona.checkLookalike(host, vaultDomains)
                if (fake != null) { view.stopLoading(); warnLookalike(view, url, host, fake) }
            }
            override fun shouldOverrideUrlLoading(view: WebView, req: WebResourceRequest): Boolean {
                val s = req.url.scheme ?: return false
                if (s == "http" || s == "https") return false
                runCatching { startActivity(Intent(Intent.ACTION_VIEW, req.url)) }
                return true
            }
            override fun onPageCommitVisible(view: WebView, url: String) { hideAds(view, url) }
            // YouTube przechodzi do kolejnego filmu bez wczytywania strony - też wznów od zapamiętanego miejsca
            override fun doUpdateVisitedHistory(view: WebView, url: String, isReload: Boolean) {
                if (!isReload && Uri.parse(url).getQueryParameter("v") != null) resumeVideo(view, url)
            }
            // ramka z reklamą, która się nie wczytała - zapamiętaj adres, żeby ją zwinąć zamiast strony błędu
            override fun onReceivedSslError(view: WebView, handler: android.webkit.SslErrorHandler, err: android.net.http.SslError) {
                handler.cancel()   // nigdy nie przepuszczamy błędnego certyfikatu
                val h = Uri.parse(err.url ?: "").host?.lowercase()
                if (h != null && err.url == view.url && httpsTried.remove(h)) askHttp(view, err.url.replaceFirst("https://", "http://"), h)
            }
            override fun onReceivedError(view: WebView, req: WebResourceRequest, err: android.webkit.WebResourceError) {
                // strona nie działa po https - zapytaj, czy otworzyć bez szyfrowania
                val h = req.url.host?.lowercase()
                if (req.isForMainFrame && req.url.scheme == "https" && h != null && httpsTried.remove(h)) { askHttp(view, req.url.toString().replaceFirst("https://", "http://"), h); return }
                if (!req.isForMainFrame) { failedFrames.add(req.url.host ?: ""); collapseFrames(view) }
            }
            override fun onPageFinished(view: WebView, url: String) {
                if (url.startsWith("https://")) Uri.parse(url).host?.let { httpsTried.remove(it.lowercase()) }
                hideAds(view, url)
                for (ms in longArrayOf(0, 1500, 4000, 9000)) view.postDelayed({ collapseFrames(view) }, ms)
                resumeVideo(view, url)
                val qa = QuickAccess.isQuickAccess(url)
                if (!quiet && !qa) passwordsOnPage(view)
                if (view == web()) address.setText(if (qa) "" else url)
                if (!quiet && !qa) addHistory(view.title ?: url, url)
                if (bank) bankUsed = System.currentTimeMillis()
                applyHidden(view, url)
            }
        }
        // Przytrzymanie palca na reklamie / pustym polu (bez tekstu i linku) - Velivo samo proponuje usunięcie.
        // Na tekście i linkach zostaje zwykłe zaznaczanie.
        var touchX = 0f; var touchY = 0f
        // dwa palce na stronie = większy / mniejszy tekst tej strony (50-300%), zapamiętany osobno dla każdej strony
        val zoomDet = android.view.ScaleGestureDetector(this, object : android.view.ScaleGestureDetector.SimpleOnScaleGestureListener() {
            override fun onScale(d: android.view.ScaleGestureDetector): Boolean {
                if (zoomKey(w.url) == null) return true
                w.settings.textZoom = (w.settings.textZoom * d.scaleFactor).toInt().coerceIn(50, 300); return true
            }
            override fun onScaleEnd(d: android.view.ScaleGestureDetector) {
                if (zoomKey(w.url) == null) { w.settings.textZoom = 100; return }
                val z = (Math.round(w.settings.textZoom / 10f) * 10).coerceIn(50, 300)
                w.settings.textZoom = z
                zoomKey(w.url)?.let { k -> if (z == 100) prefs.edit().remove(k).apply() else prefs.edit().putInt(k, z).apply() }
                toast(getString(R.string.page_zoom, z))
            }
        })
        zoomDet.isQuickScaleEnabled = false   // podwójne stuknięcie i przeciągnięcie to zwykłe przewijanie, nie powiększanie
        w.setOnTouchListener { _, e ->
            if (e.action == android.view.MotionEvent.ACTION_DOWN) { touchX = e.x; touchY = e.y }
            zoomDet.onTouchEvent(e)
            // w trybie pulpitu zostaje zwykłe powiększanie całej strony
            e.pointerCount > 1 && tabs.firstOrNull { it.web == w }?.desktop != true
        }
        w.setOnLongClickListener {
            val type = w.hitTestResult.type
            if (type == WebView.HitTestResult.EDIT_TEXT_TYPE || type == WebView.HitTestResult.SRC_ANCHOR_TYPE || type == WebView.HitTestResult.SRC_IMAGE_ANCHOR_TYPE) return@setOnLongClickListener false
            if (w == web() && prefs.getBoolean("adblock", true)) longPressHide(w, touchX, touchY)
            false
        }
        w.webChromeClient = object : WebChromeClient() {
            // 🎯 stuknięty element - zaproponuj usunięcie (sygnał tylko z naszego skryptu, z losowym znacznikiem)
            override fun onJsPrompt(view: WebView, url: String?, message: String?, def: String?, result: android.webkit.JsPromptResult): Boolean {
                if (message != pickToken) return false
                result.confirm("")
                if (view == web()) askHide()
                return true
            }
            override fun onShowCustomView(v: View, cb: CustomViewCallback) = enterFullscreen(v, cb)
            override fun onHideCustomView() = exitFullscreen()
            override fun onProgressChanged(view: WebView, p: Int) {
                if (view != web()) return
                progress.progress = p
                progress.visibility = if (p < 100) View.VISIBLE else View.INVISIBLE
            }
        }
        w.setDownloadListener { dUrl, ua, disp, mime, _ ->
            val name = URLUtil.guessFileName(dUrl, disp, mime)
            // aplikacja spoza Sklepu Play - najczęstszy sposób oszustów na przejęcie telefonu
            if (name.endsWith(".apk", true)) {
                AlertDialog.Builder(this).setTitle(R.string.apk_title).setMessage(getString(R.string.apk_warn, Uri.parse(dUrl).host ?: ""))
                    .setPositiveButton(R.string.apk_cancel, null)
                    .setNegativeButton(R.string.apk_anyway) { _, _ -> startDownload(dUrl, ua, name, mime, quiet) }
                    .show()
                return@setDownloadListener
            }
            startDownload(dUrl, ua, name, mime, quiet)
        }
        tabs.add(Tab(w, priv, bank))
        show(tabs.size - 1)
        w.loadUrl(url)
    }

    private fun startDownload(dUrl: String, ua: String, name: String, mime: String, quiet: Boolean) {
        val req = DownloadManager.Request(Uri.parse(dUrl))
            .setMimeType(mime)
            .addRequestHeader("User-Agent", ua)
            .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
            .setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, name)
        if (!quiet) CookieManager.getInstance().getCookie(dUrl)?.let { req.addRequestHeader("Cookie", it) }
        (getSystemService(DOWNLOAD_SERVICE) as DownloadManager).enqueue(req)
        toast(getString(R.string.download_started))
    }

    // Ukrywanie reklam (reguły "##" z list) i zwijanie pustych ramek z napisem "REKLAMA" -
    // po zablokowanej reklamie nie zostaje dziura ani strona błędu w ramce.
    private fun hideAds(view: WebView, url: String) {
        if (!prefs.getBoolean("adblock", true) || QuickAccess.isQuickAccess(url) || siteAllowed(Uri.parse(url).host)) return
        // YouTube: reklamy usuwa youtube.js; duży arkusz i ciągłe przeglądanie strony (YouTube zmienia ją bez przerwy) tylko by go spowalniały
        if ((Uri.parse(url).host ?: "").endsWith("youtube.com")) return
        val css = org.json.JSONObject.quote(blocker.hidingCss(Uri.parse(url).host))
        view.evaluateJavascript("""(function(){var d=document;if(!d.documentElement)return;
var s=d.getElementById('velivo-reklamy');if(!s){s=d.createElement('style');s.id='velivo-reklamy';(d.head||d.documentElement).appendChild(s);}
s.textContent=$css;
if(window.__vZwin)return;
var R=/(^|[\s_-])(ad|ads|advert|adslot|adbox|adunit|adplaceholder|adcontainer|reklama|reklamy|baner|banner)([\s_-]|$)|AdSlot|AdPlaceholder|CenterAd|adPlaceholder|ad-slot|ad_slot|placeholderAd|-ad-|_ad_/i;
var L=/^(reklama|reklamy|ogłoszenie|advertisement|advert|ads?|sponsored|sponsorowane|materiał sponsorowany)$/i;
function lab(e){var t=(e.textContent||'').trim();if(t)return t.length<40&&L.test((e.innerText||t).trim());
for(var a=0;a<e.attributes.length;a++)if(e.attributes[a].name.indexOf('data-')==0&&L.test(e.attributes[a].value.trim()))return true;
var c=(getComputedStyle(e,'::before').content+' '+getComputedStyle(e,'::after').content).replace(/["']/g,'').trim();return /reklama|advert|ogłoszenie|sponsor/i.test(c);}
// pusta ramka: tylko napis REKLAMA, ramki (iframe), skrypty i puste elementy (innerText pomija <script>)
function empty(e){var t=(e.innerText||'').trim();if(t&&!L.test(t))return false;if(e.querySelector('h1,h2,h3,article'))return false;
var m=e.querySelectorAll('img,video,picture');for(var i=0;i<m.length;i++)if(m[i].offsetWidth>40&&m[i].offsetHeight>40)return false;return true;}
window.__vUp=function(e){var b=e;while(b.parentElement&&b.parentElement!=d.body&&b.parentElement.offsetHeight<900&&empty(b.parentElement))b=b.parentElement;
b.style.setProperty('display','none','important');};
function zwin(){var all=d.querySelectorAll('div,span,p,section,aside,ins');for(var i=0;i<all.length;i++){var e=all[i];
if(e.children.length<=3&&lab(e)){__vUp(e);continue;}
// miejsce na reklamę po nazwie klasy/id (AdSlot, AdPlaceholder, ad-container, reklama...) - tylko gdy puste
var n=(typeof e.className=='string'?e.className:'')+' '+e.id;
if(R.test(n)&&empty(e))__vUp(e);}
// ramki reklam (to samo, co znajduje ręczne usuwanie): pusta / zablokowana ramka albo oznaczona jako reklama w niej lub w 3 poziomach wyżej
var fr=d.querySelectorAll('iframe,ins');for(var j=0;j<fr.length;j++){var f=fr[j];if(f.style.display=='none')continue;
var src=f.getAttribute('src')||'',blank=!src||src.indexOf('about:')==0,ad=f.tagName=='INS'||/google_ads|doubleclick|adserver|\/ads?\//i.test(src+' '+f.id+' '+f.name);
for(var k=0,a=f;!ad&&a&&k<4;k++,a=a.parentElement){var an=(typeof a.className=='string'?a.className:'')+' '+a.id;if(R.test(an)||(blank&&L.test((a.innerText||'').trim())))ad=true;}
if(ad&&!/youtube|vimeo|player|video|embed|recaptcha|captcha/i.test(src+' '+f.id+' '+f.className))__vUp(f);}}
var t=0;window.__vZwin=new MutationObserver(function(){clearTimeout(t);t=setTimeout(zwin,400)});
window.__vZwin.observe(d.documentElement,{childList:true,subtree:true});zwin();})()""", null)
    }

    private val failedFrames = java.util.Collections.synchronizedSet(HashSet<String>())

    // Ramki z zablokowanych lub niedziałających adresów reklam - zwiń je razem z pustym otoczeniem.
    private fun collapseFrames(view: WebView) = runOnUiThread {
        if (!prefs.getBoolean("adblock", true) || siteAllowed(Uri.parse(view.url ?: "").host)) return@runOnUiThread
        view.evaluateJavascript("JSON.stringify([].map.call(document.querySelectorAll('iframe'),function(f){return f.src||''}))") { r ->
            val srcs = runCatching { org.json.JSONArray(org.json.JSONTokener(r).nextValue() as String) }.getOrNull() ?: return@evaluateJavascript
            val bad = (0 until srcs.length()).filter { i ->
                val u = srcs.optString(i)
                u.isNotEmpty() && Uri.parse(u).let { it.host in failedFrames || isBlocked(it) }
            }
            if (bad.isNotEmpty()) view.evaluateJavascript("(function(){var f=document.querySelectorAll('iframe');${bad.joinToString(",", "[", "]")}.forEach(function(i){var e=f[i];if(e)(window.__vUp?__vUp(e):e.style.setProperty('display','none','important'))})})()", null)
        }
    }

    // --- Prywatność i ochrona przed oszustwami (jak Ochrona.cs na Windows) ---
    private val pageHost: MutableMap<WebView, String> = java.util.Collections.synchronizedMap(java.util.WeakHashMap())
    private val pageBlocked: MutableMap<WebView, java.util.concurrent.ConcurrentHashMap<String, Int>> = java.util.Collections.synchronizedMap(java.util.WeakHashMap())
    private val httpAllowed = java.util.Collections.synchronizedSet(HashSet<String>())   // na tę sesję: zgoda na stronę bez szyfrowania
    private val httpsTried = java.util.Collections.synchronizedSet(HashSet<String>())
    @Volatile private var vaultDomains: Set<String> = emptySet()   // domeny z zapisanymi hasłami - ich podróbki też wykrywamy
    private val protectionAllowed get() = prefs.getStringSet("ochrona-dozwolone", emptySet()) ?: emptySet()

    // strony, na których użytkownik wyłączył blokowanie reklam
    private fun siteAllowed(host: String?) = host != null && Ochrona.regDomain(host) in (prefs.getStringSet("adblock-wyjatki", emptySet()) ?: emptySet())

    // klucz wielkości strony: domena (www. bez znaczenia); Szybki Dostęp ma własny
    // Strona startowa (Szybki Dostęp) - zawsze 100%, bez zmiany wielkości (wyjątek)
    private fun zoomKey(url: String?): String? = Uri.parse(url ?: "").host?.lowercase()?.takeIf { it != QuickAccess.HOST }?.removePrefix("www.")?.let { "zoom-$it" }

    private fun warnLookalike(view: WebView, url: String, host: String, fake: String) {
        val msg = if (fake == "xn") getString(R.string.fake_xn, host) else getString(R.string.fake_brand, host, fake)
        AlertDialog.Builder(this).setTitle(R.string.fake_title).setMessage(msg).setCancelable(false)
            .setPositiveButton(R.string.fake_back) { _, _ -> if (view.canGoBack()) view.goBack() else view.loadUrl(home) }
            .setNegativeButton(R.string.fake_trust) { _, _ ->
                prefs.edit().putStringSet("ochrona-dozwolone", protectionAllowed + host).apply()
                view.loadUrl(url)
            }
            .show()
    }

    private fun askHttp(view: WebView, url: String, host: String) {
        AlertDialog.Builder(this).setTitle(R.string.http_title).setMessage(getString(R.string.http_warn, host))
            .setPositiveButton(R.string.fake_back) { _, _ -> if (view.canGoBack()) view.goBack() else view.loadUrl(home) }
            .setNegativeButton(R.string.http_open) { _, _ -> httpAllowed.add(host); view.loadUrl(url) }
            .show()
    }

    private fun showPrivacy() {
        val w = web() ?: return
        val u = Uri.parse(w.url ?: "")
        val host = u.host ?: return
        val blocked = pageBlocked[w]?.entries?.sortedByDescending { it.value } ?: emptyList()
        val secure = u.scheme == "https"
        val off = siteAllowed(host)
        val text = StringBuilder()
            .append(if (secure) getString(R.string.priv_secure) else getString(R.string.priv_insecure)).append("\n\n")
            .append(getString(R.string.priv_blocked_page, blocked.sumOf { it.value }, blocked.size)).append("\n")
        blocked.take(12).forEach { text.append("  • ").append(it.key).append(" (").append(it.value).append(")\n") }
        text.append("\n").append(getString(R.string.blocked, blockedCount))
        if (off) text.append("\n\n").append(getString(R.string.priv_site_off))
        AlertDialog.Builder(this).setTitle("🛡 " + host.removePrefix("www."))
            .setMessage(text.toString())
            .setPositiveButton(android.R.string.ok, null)
            .setNeutralButton(if (off) R.string.priv_enable else R.string.priv_disable) { _, _ ->
                val set = (prefs.getStringSet("adblock-wyjatki", emptySet()) ?: emptySet()).toMutableSet()
                val d = Ochrona.regDomain(host)
                if (off) set.remove(d) else set.add(d)
                prefs.edit().putStringSet("adblock-wyjatki", set).apply()
                w.reload()
            }
            .show()
    }

    private fun isBlocked(u: Uri) = (u.scheme == "http" || u.scheme == "https") && blocker.matches(u.host)

    // Najpierw to, co już jest na telefonie, potem w tle świeże listy (co 4 dni).
    private fun startFilters() = Thread {
        blocker = AdBlocker.build(this).also { it.warm() }   // arkusz ukrywania składany w tle, nie przy pierwszej stronie
        reapplyFilters()
        if (AdBlocker.stale(this) && AdBlocker.download(this) > 0) { blocker = AdBlocker.build(this).also { it.warm() }; reapplyFilters() }
    }.start()

    // Karty przywrócone przy starcie wczytują się, zanim listy są gotowe - po wczytaniu list posprzątaj je jeszcze raz.
    private fun reapplyFilters() = runOnUiThread {
        tabs.forEach { t -> t.web.url?.let { hideAds(t.web, it); collapseFrames(t.web) } }
    }

    private fun show(i: Int) {
        if (::findBar.isInitialized && findBar.visibility == View.VISIBLE) closeFind()   // szukanie dotyczy jednej karty
        if (::pickBar.isInitialized && pickBar.visibility == View.VISIBLE) stopPick()
        tabs.getOrNull(cur)?.let { snap(it) }
        cur = i
        frame.removeAllViews()
        val t = tabs[i]
        frame.addView(t.web)
        address.setText(t.web.url?.takeIf { !QuickAccess.isQuickAccess(it) } ?: "")
        address.background = round(if (t.bank) BANK_BG else if (t.priv) PRIV_BG else FIELD)
        tabsBtn.text = tabs.size.toString()
        // Karta bankowa: bez zrzutów ekranu i podglądu na liście ostatnich aplikacji.
        if (t.bank) window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        else window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE)
    }

    // Miniatura karty (1/4 rozmiaru, tylko w pamięci); karty bankowe bez miniatur.
    private fun snap(t: Tab) {
        val w = t.web
        if (t.bank || w.width < 4 || w.height < 4) return
        runCatching {
            val b = Bitmap.createBitmap(w.width / 4, w.height / 4, Bitmap.Config.RGB_565)
            Canvas(b).apply { scale(0.25f, 0.25f); w.draw(this) }
            t.thumb = b
        }
    }

    private fun closeTab(i: Int) {
        val t = tabs.removeAt(i)
        t.web.destroy()
        if (t.priv && tabs.none { it.priv }) dropPrivateProfile()
        if (t.bank && tabs.none { it.bank }) bankUnlocked = false
        if (tabs.isEmpty()) { cur = -1; newTab(false, home) } else show(minOf(i, tabs.size - 1))
    }

    private fun go(text: String) {
        val s = text.trim()
        if (s.isEmpty()) return
        val url = when {
            s.contains("://") -> s
            !s.contains(' ') && s.contains('.') -> "https://$s"
            else -> home + "do/search?q=" + Uri.encode(s)
        }
        web()?.loadUrl(url)
        web()?.requestFocus()
        (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).hideSoftInputFromWindow(address.windowToken, 0)
    }

    // Lista kart: miniatura, tytuł i adres; stuknięcie przełącza, ✕ lub przesunięcie w bok zamyka.
    // --- Powiększanie dwoma palcami (dla słabszego wzroku): menu, listy, karteczki, pasek i dolne przyciski ---
    // Jedna skala dla całego Velivo (80-220%), zapamiętana; strony powiększa się jak zwykle w samej stronie.
    private var uiScale = 1f
    private var chrome = listOf<View>()
    private val scaleBase = java.util.WeakHashMap<View, FloatArray>()   // oryginalne: tekst, szerokość, wysokość, min. wysokość

    // błąd jednego elementu nie może zamknąć Velivo - zostaje w dzienniku (Logcat: Velivo)
    private fun applyScale(v: View) {
        runCatching { scaleOne(v) }.onFailure { android.util.Log.e("Velivo", "powiekszanie", it) }
    }

    private fun scaleOne(v: View) {
        val s = uiScale
        val lp = v.layoutParams
        val o = scaleBase.getOrPut(v) {
            floatArrayOf(if (v is TextView) v.textSize else 0f, (lp?.width ?: -1).toFloat(), (lp?.height ?: -1).toFloat(), v.minimumHeight.toFloat())
        }
        if (v is TextView && o[0] > 0f) {
            if (v.autoSizeTextType != TextView.AUTO_SIZE_TEXT_TYPE_NONE)
                v.setAutoSizeTextTypeUniformWithConfiguration((9 * s).toInt().coerceAtLeast(6), (12 * s).toInt().coerceAtLeast(7), 1, android.util.TypedValue.COMPLEX_UNIT_SP)
            else v.setTextSize(android.util.TypedValue.COMPLEX_UNIT_PX, o[0] * s)
        }
        if (lp != null) {
            if (o[1] > 0f) lp.width = (o[1] * s).toInt()
            if (o[2] > 0f) lp.height = (o[2] * s).toInt()
            v.layoutParams = lp
        }
        if (v is ViewGroup) for (i in 0 until v.childCount) applyScale(v.getChildAt(i))
    }

    // Opakowanie okna: dwa palce rozsuwane / zsuwane = większe / mniejsze; jeden palec działa jak zwykle.
    // onClose: przeciągnięcie palcem w dół (gdy lista / strona jest na samej górze) zamyka okno i pokazuje stronę pod spodem
    private inner class PinchFrame(private val content: View, private val onClose: (() -> Unit)? = null) : FrameLayout(this@MainActivity) {
        private var pinching = false
        private var downX = 0f; private var downY = 0f; private var dragging = false; private var canDrag = false
        private val slop = android.view.ViewConfiguration.get(this@MainActivity).scaledTouchSlop
        // czy pod palcem jest coś, co jeszcze przewija się w górę (wtedy przeciąganie przewija, nie zamyka)
        private fun scrollsUp(v: View, x: Float, y: Float): Boolean {
            val r = android.graphics.Rect(); if (!v.isShown || !v.getGlobalVisibleRect(r) || !r.contains(x.toInt(), y.toInt())) return false
            if (v.canScrollVertically(-1)) return true
            return v is android.view.ViewGroup && (0 until v.childCount).any { scrollsUp(v.getChildAt(it), x, y) }
        }
        private val det = android.view.ScaleGestureDetector(context, object : android.view.ScaleGestureDetector.SimpleOnScaleGestureListener() {
            override fun onScale(d: android.view.ScaleGestureDetector): Boolean {
                runCatching {
                    uiScale = (uiScale * d.scaleFactor).coerceIn(0.8f, 2.2f)
                    applyScale(content); chrome.forEach { applyScale(it) }
                }.onFailure { android.util.Log.e("Velivo", "powiekszanie", it) }
                return true
            }
            override fun onScaleEnd(d: android.view.ScaleGestureDetector) {
                prefs.edit().putFloat("skala", uiScale).apply()
                toast(getString(R.string.ui_scale, Math.round(uiScale * 100)))
            }
        })
        init { det.isQuickScaleEnabled = false; addView(content); applyScale(content) }
        override fun dispatchTouchEvent(ev: MotionEvent): Boolean {
            det.onTouchEvent(ev)
            if (onClose != null && ev.pointerCount == 1 && !pinching) when (ev.actionMasked) {
                MotionEvent.ACTION_DOWN -> { downX = ev.rawX; downY = ev.rawY; dragging = false; canDrag = !scrollsUp(content, ev.rawX, ev.rawY) }
                MotionEvent.ACTION_MOVE -> {
                    val dy = ev.rawY - downY; val dx = ev.rawX - downX
                    if (!dragging && canDrag && dy > slop * 2 && dy > Math.abs(dx) * 2) {
                        dragging = true
                        val c = MotionEvent.obtain(ev).apply { action = MotionEvent.ACTION_CANCEL }; runCatching { super.dispatchTouchEvent(c) }; c.recycle()
                    }
                    if (dragging) { content.translationY = dy.coerceAtLeast(0f); content.alpha = (1f - dy / (height * 1.5f)).coerceIn(0.3f, 1f); return true }
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> if (dragging) {
                    dragging = false
                    if (ev.rawY - downY > height * 0.22f) content.animate().translationY(height.toFloat()).alpha(0f).setDuration(150).withEndAction { onClose.invoke() }.start()
                    else content.animate().translationY(0f).alpha(1f).setDuration(150).start()
                    return true
                }
            }
            if (ev.pointerCount > 1 && !pinching) {
                pinching = true
                val c = MotionEvent.obtain(ev).apply { action = MotionEvent.ACTION_CANCEL }   // przewijanie / stuknięcie pod palcami - anuluj
                runCatching { super.dispatchTouchEvent(c) }
                c.recycle()
            }
            if (pinching) {
                if (ev.actionMasked == MotionEvent.ACTION_UP || ev.actionMasked == MotionEvent.ACTION_CANCEL) pinching = false
                return true
            }
            return super.dispatchTouchEvent(ev)
        }
    }

    private fun showTabs() {
        tabs.getOrNull(cur)?.let { snap(it) }
        val list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(8), dp(8), dp(8), dp(8)) }
        val dlg = AlertDialog.Builder(this)
            .setTitle(R.string.tabs)
            .setView(PinchFrame(ScrollView(this).apply { addView(list) }))
            .setPositiveButton(R.string.new_tab) { _, _ -> newTab(false, QuickAccess.URL) }
            .setNeutralButton(R.string.new_private_tab) { _, _ -> newTab(true, home) }
            .setNegativeButton(R.string.bank_mode) { _, _ -> openBank() }
            .create()
        fun fill() {
            list.removeAllViews()
            tabs.toList().forEachIndexed { i, t -> list.addView(tabRow(t, i == cur, { show(tabs.indexOf(t)); dlg.dismiss() }) {
                closeTab(tabs.indexOf(t))
                fill()
            }) }
            applyScale(list)
        }
        fill()
        dlg.show()
    }

    @SuppressLint("ClickableViewAccessibility")
    private fun tabRow(t: Tab, active: Boolean, open: () -> Unit, close: () -> Unit): View {
        val row = LinearLayout(this).apply {
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(6), dp(6), dp(6), dp(6))
            background = round(if (t.bank) BANK_BG else if (t.priv) PRIV_BG else if (active) BLUE_BG else GRAY_BG)
            layoutParams = LinearLayout.LayoutParams(-1, -2).apply { setMargins(0, dp(3), 0, dp(3)) }
        }
        val img = ImageView(this).apply {
            scaleType = ImageView.ScaleType.CENTER_CROP
            t.thumb?.let { setImageBitmap(it) } ?: setBackgroundColor(FIELD)
        }
        row.addView(img, LinearLayout.LayoutParams(dp(72), dp(96)))
        val title = (if (t.bank) "🏦 " else if (t.priv) "🕶 " else "") + (t.web.title?.takeIf { it.isNotBlank() } ?: "…")
        row.addView(TextView(this).apply {
            text = title + "\n" + (Uri.parse(t.web.url ?: "").host ?: "")
            setTextColor(TEXT)
            maxLines = 3
            setPadding(dp(10), 0, dp(6), 0)
        }, LinearLayout.LayoutParams(0, -2, 1f))
        row.addView(button("✕", R.string.close_tab, TEXT, GRAY_BG) { close() })
        // Przesunięcie w bok o 1/3 szerokości zamyka kartę; krótkie stuknięcie otwiera.
        var x0 = 0f
        row.setOnTouchListener { v, e ->
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> { x0 = e.rawX; true }
                MotionEvent.ACTION_MOVE -> { v.translationX = e.rawX - x0; v.alpha = 1f - minOf(0.7f, Math.abs(v.translationX) / v.width); true }
                MotionEvent.ACTION_UP -> {
                    val dx = e.rawX - x0
                    if (Math.abs(dx) > v.width / 3f) close()
                    else { v.animate().translationX(0f).alpha(1f).start(); if (Math.abs(dx) < dp(10)) open() }
                    true
                }
                else -> { v.animate().translationX(0f).alpha(1f).start(); true }
            }
        }
        return row
    }

    // --- Szukaj na stronie: pasek nad dolnymi przyciskami, licznik trafień, poprzedni / następny ---
    private lateinit var findBar: LinearLayout
    private lateinit var findText: EditText
    private lateinit var findCount: TextView

    private fun buildFindBar(): View {
        findBar = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(6), dp(4), dp(6), dp(4)); visibility = View.GONE }
        findText = EditText(this).apply {
            hint = getString(R.string.find_in_page); setSingleLine(); textSize = 15f
            setTextColor(TEXT); setHintTextColor(c("#4F4380", "#A79FD0")); background = round(FIELD); setPadding(dp(12), dp(6), dp(12), dp(6))
            imeOptions = EditorInfo.IME_ACTION_SEARCH
            setOnEditorActionListener { _, _, _ -> web()?.findNext(true); true }
            addTextChangedListener(object : TextWatcher {
                override fun afterTextChanged(e: Editable?) { web()?.findAllAsync(e?.toString() ?: "") }
                override fun beforeTextChanged(a: CharSequence?, b: Int, c: Int, d: Int) {}
                override fun onTextChanged(a: CharSequence?, b: Int, c: Int, d: Int) {}
            })
        }
        findCount = TextView(this).apply { setTextColor(TEXT); setPadding(dp(8), 0, dp(4), 0); textSize = 13f }
        findBar.addView(findText, LinearLayout.LayoutParams(0, -2, 1f))
        findBar.addView(findCount)
        findBar.addView(button("▲", R.string.find_prev, BLUE, BLUE_BG) { web()?.findNext(false) })
        findBar.addView(button("▼", R.string.find_next, BLUE, BLUE_BG) { web()?.findNext(true) })
        findBar.addView(button("✕", R.string.close_tab_short, TEXT, GRAY_BG) { closeFind() })
        return findBar
    }

    private fun openFind() {
        val w = web() ?: return
        w.setFindListener { active, total, _ -> findCount.text = if (total == 0) "0" else "${active + 1} / $total" }
        findBar.visibility = View.VISIBLE
        findText.requestFocus()
        (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).showSoftInput(findText, 0)
        if (findText.text.isNotEmpty()) w.findAllAsync(findText.text.toString())
    }

    // --- Usun element palcem (jak w Velivo na Windows): regula CSS zapamietana dla domeny ---
    private lateinit var pickBar: LinearLayout

    private fun buildPickBar(): View {
        pickBar = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(6), dp(4), dp(6), dp(4)); visibility = View.GONE }
        pickBar.addView(TextView(this).apply { text = getString(R.string.hide_hint); setTextColor(TEXT); textSize = 13f; setPadding(dp(6), 0, dp(4), 0) }, LinearLayout.LayoutParams(0, -2, 1f))
        pickBar.addView(button("⬆", R.string.hide_bigger, BLUE, BLUE_BG) { web()?.evaluateJavascript("window.__vBig&&__vBig()", null) })
        pickBar.addView(button("🗑", R.string.hide_element, GREEN, GREEN_BG) { hidePicked() })
        pickBar.addView(button("📋", R.string.hide_copy, BLUE, BLUE_BG) { copyPicked() })
        pickBar.addView(button("↺", R.string.hide_restore, ORANGE, ORANGE_BG) { restoreHidden() })
        pickBar.addView(button("✕", R.string.close_tab_short, TEXT, GRAY_BG) { stopPick() })
        return pickBar
    }

    private fun hostKey(url: String?) = "ukryte_" + (Uri.parse(url ?: "").host ?: "").removePrefix("www.")

    private fun longPressHide(w: WebView, x: Float, y: Float) {
        val js = "(function(x,y){var r=window.devicePixelRatio||1,l=document.elementsFromPoint(x/r,y/r);for(var i=0;i<l.length;i++){var e=l[i];" +
            "if(e==document.body||e==document.documentElement)break;if(e.tagName=='IFRAME'||e.tagName=='INS')return true;" +
            "var n=(typeof e.className=='string'?e.className:'')+' '+e.id;if(/AdSlot|AdPlaceholder|CenterAd|ad-slot|ad_slot|google_ads|reklama/i.test(n))return true;}" +
            "var e=l[0];if(!e||e==document.body)return false;var t=(e.innerText||'').trim();" +
            "return !t||/^(reklama|reklamy|ogłoszenie|advertisement|advert|ads?|sponsored|sponsorowane)$/i.test(t)})($x,$y)"
        w.evaluateJavascript(js) { r ->
            if (r != "true") return@evaluateJavascript
            startPick()
            w.evaluateJavascript("window.__vAt&&__vAt($x,$y)", null)
        }
    }

    private fun startPick() {
        val w = web() ?: return
        pickBar.visibility = View.VISIBLE
        w.evaluateJavascript("""(function(){if(window.__vPick)return;var cur=null;
function mark(e){if(cur)cur.style.outline=cur.__vo||'';cur=e;if(e){e.__vo=e.style.outline;e.style.outline='3px solid #6D3FDB';}}
var ov=document.createElement('div');ov.style.cssText='position:fixed;inset:0;z-index:2147483647;background:rgba(109,63,219,.06);touch-action:pan-y';
// najlepszy kandydat: od stukniętego elementu w górę, dopóki otoczenie jest puste (sama reklama / napis REKLAMA)
function txt(e){return (e.innerText||'').trim();}
function best(e){var L=/^(reklama|reklamy|ogłoszenie|advertisement|advert|ads?|sponsored|sponsorowane)$/i;
while(e.parentElement&&e.parentElement!=document.body&&e.parentElement.offsetHeight<window.innerHeight*0.9){var t=txt(e.parentElement);if(t&&!L.test(t)&&t!=txt(e))break;e=e.parentElement;}return e;}
// reklama bywa schowana pod przezroczystą warstwą strony - przeglądamy wszystkie warstwy pod palcem
var AD=/(^|[\s_-])(ad|ads|advert|adslot|adbox|adunit|reklama|baner|banner)([\s_-]|$)|AdSlot|AdPlaceholder|CenterAd|placeholder|ad-slot|ad_slot|-ad-|_ad_|google_ads/i;
function at(x,y){ov.style.pointerEvents='none';var l=document.elementsFromPoint(x,y).filter(function(e){return e!=ov&&e!=document.body&&e!=document.documentElement;});ov.style.pointerEvents='auto';
for(var i=0;i<l.length;i++){var e=l[i],n=(typeof e.className=='string'?e.className:'')+' '+e.id;if(e.tagName=='IFRAME'||e.tagName=='INS'||AD.test(n))return e;}return l[0];}
function choose(t){if(t){mark(best(t));setTimeout(function(){prompt('$pickToken','')},50);}}
window.__vPick=function(ev){ev.preventDefault();ev.stopPropagation();choose(at(ev.clientX,ev.clientY));};
ov.addEventListener('click',__vPick,true);document.documentElement.appendChild(ov);
window.__vAt=function(x,y){var r=window.devicePixelRatio||1;choose(at(x/r,y/r));};
window.__vBig=function(){if(cur&&cur.parentElement&&cur.parentElement!=document.body)mark(cur.parentElement);};
function sel(e){var p=[];while(e&&e.nodeType==1&&e!=document.body){var s=e.tagName.toLowerCase();
if(e.id&&!/\d{3}/.test(e.id)){p.unshift(s+'#'+CSS.escape(e.id));break;}
var c=[].filter.call(e.classList,function(x){return !/\d{3}|^(active|show|open|hover)$/.test(x)}).slice(0,2);
if(c.length)s+='.'+c.map(CSS.escape).join('.');
var n=1,x=e;while((x=x.previousElementSibling))if(x.tagName==e.tagName)n++;s+=':nth-of-type('+n+')';
p.unshift(s);e=e.parentElement;}return p.join(' > ');}
window.__vInfo=function(){if(!cur)return '';var p=[],e=cur;while(e&&e!=document.body){p.unshift(e.tagName.toLowerCase()+(e.id?'#'+e.id:'')+(typeof e.className=='string'&&e.className?'.'+e.className.trim().split(/\s+/).join('.'):''));e=e.parentElement;}
var h=cur.outerHTML.replace(/<script[\s\S]*?<\/script>/gi,'<script/>');return location.host+'\n'+p.join(' > ')+'\n\n'+h.slice(0,3000);};
window.__vTake=function(){if(!cur)return '';var s=sel(cur);cur.style.setProperty('display','none','important');cur=null;return s;};
window.__vStop=function(){mark(null);ov.remove();window.__vPick=null;};})()""", null)
    }

    // Diagnostyka: kod zaznaczonego pola do schowka - do zgłoszenia reklamy, której Velivo nie ukrywa
    private fun copyPicked() {
        web()?.evaluateJavascript("window.__vInfo?__vInfo():''") { r ->
            val t = runCatching { org.json.JSONTokener(r).nextValue() as String }.getOrDefault("")
            if (t.isEmpty()) { toast(getString(R.string.hide_hint)); return@evaluateJavascript }
            (getSystemService(CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newPlainText("Velivo", t))
            toast(getString(R.string.hide_copied))
        }
    }

    private val pickToken = "velivo-pick-" + java.util.UUID.randomUUID()

    private fun askHide() {
        AlertDialog.Builder(this).setTitle(R.string.hide_element).setMessage(R.string.hide_ask)
            .setPositiveButton(R.string.hide_yes) { _, _ -> hidePicked() }
            .setNeutralButton(R.string.hide_bigger) { _, _ -> web()?.evaluateJavascript("window.__vBig&&__vBig()") { askHide() } }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun stopPick() {
        pickBar.visibility = View.GONE
        web()?.evaluateJavascript("window.__vStop&&__vStop()", null)
    }

    private fun hidePicked() {
        val w = web() ?: return
        w.evaluateJavascript("window.__vTake?__vTake():''") { r ->
            val sel = runCatching { org.json.JSONTokener(r).nextValue() as String }.getOrDefault("")
            if (sel.isEmpty()) { toast(getString(R.string.hide_hint)); return@evaluateJavascript }
            val key = hostKey(w.url)
            val rules = (prefs.getStringSet(key, emptySet()) ?: emptySet()) + sel
            prefs.edit().putStringSet(key, rules).apply()
            toast(getString(R.string.hidden_saved))
        }
    }

    private fun restoreHidden() {
        val w = web() ?: return
        prefs.edit().remove(hostKey(w.url)).apply()
        stopPick(); w.reload(); toast(getString(R.string.hide_restored))
    }

    private fun applyHidden(view: WebView, url: String) {
        val rules = prefs.getStringSet(hostKey(url), null) ?: return
        val css = org.json.JSONObject.quote(rules.joinToString(",") + "{display:none!important}")
        view.evaluateJavascript("(function(){var s=document.getElementById('velivo-ukryte')||document.createElement('style');s.id='velivo-ukryte';s.textContent=$css;(document.head||document.documentElement).appendChild(s);})()", null)
    }

    private fun closeFind() {
        web()?.clearMatches()
        findBar.visibility = View.GONE
        findCount.text = ""
        (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).hideSoftInputFromWindow(findText.windowToken, 0)
    }

    // --- Tryb pulpitu: strona jak na komputerze (bez "Android" i "Mobile" w identyfikatorze przeglądarki) ---
    private fun toggleDesktop() {
        val t = tabs.getOrNull(cur) ?: return
        t.desktop = !t.desktop
        val st = t.web.settings
        st.userAgentString = if (t.desktop) desktopAgent(WebSettings.getDefaultUserAgent(this)) else null
        st.useWideViewPort = t.desktop
        st.loadWithOverviewMode = t.desktop
        t.web.reload()
        toast(getString(if (t.desktop) R.string.desktop_on else R.string.desktop_off))
    }

    private fun desktopAgent(mobile: String): String {
        val chrome = Regex("Chrome/[\\d.]+").find(mobile)?.value ?: "Chrome/130.0.0.0"
        return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) $chrome Safari/537.36"
    }

    // Menu Velivo: wysuwane od dołu, logo i licznik reklam na górze, kolorowe kafelki jak przyciski Velivo na Windows.
    // --- Czytnik i czytanie na glos: tekst artykulu wyciagniety ze strony, bez reklam i menu ---
    private var tts: android.speech.tts.TextToSpeech? = null

    private fun readArticle(done: (String, String) -> Unit) {
        // tytuł raz (z <h1>), bez powtórzeń i bez ramek ze skrótem / podsumowaniem artykułu (to ten sam tekst drugi raz)
        val js = """(function(){var best=document.body,max=0;
document.querySelectorAll('article,main,[role=main],section,div').forEach(function(e){var n=0;
e.querySelectorAll(':scope > p').forEach(function(p){n+=p.innerText.length});if(n>max){max=n;best=e}});
var root=document.querySelector('article')||best,out=[],keys=[];
var h=document.querySelector('h1'),title=((h&&h.innerText)||document.title).replace(/\s+/g,' ').trim();
function key(t){return t.toLowerCase().replace(/[^0-9a-ząćęłńóśźżäöüéèàç]+/g,' ').trim();}
function dup(t){var k=key(t);if(!k)return true;for(var i=0;i<keys.length;i++){var o=keys[i];if(o==k||(k.length<200&&o.indexOf(k)>=0)||(o.length<200&&k.indexOf(o)>=0&&o.length>k.length*0.6))return true;}keys.push(k);return false;}
dup(title);
var SKIP='nav,footer,aside,form,[class*=summary i],[class*=podsumow i],[class*=skrot i],[class*=tldr i],[class*=highlight i],[class*=key-point i],[class*=keypoint i],[class*=lead-points i]';
root.querySelectorAll('h1,h2,h3,p,li,blockquote').forEach(function(e){var t=e.innerText.replace(/\s+/g,' ').trim();
if(t.length>1&&!e.closest(SKIP)&&!dup(t))out.push(t)});
if(!out.length)out=[document.body.innerText];
return JSON.stringify({t:title,b:out.join('\n\n')})})()"""
        val w = web() ?: return
        w.evaluateJavascript(js) { r ->
            try {
                val o = org.json.JSONObject(org.json.JSONTokener(r).nextValue() as String)
                val body = o.optString("b").trim()
                if (body.isEmpty()) toast(getString(R.string.reader_empty)) else done(o.optString("t"), body)
            } catch (_: Exception) { toast(getString(R.string.reader_empty)) }
        }
    }

    private fun speak(text: String) {
        val go = speakGo@{
            val t = tts!!
            val lang = t.setLanguage(resources.configuration.locales[0])
            if (lang == android.speech.tts.TextToSpeech.LANG_MISSING_DATA || lang == android.speech.tts.TextToSpeech.LANG_NOT_SUPPORTED) { offerTts(); return@speakGo }
            // TTS przyjmuje ograniczona dlugosc - dzielimy po akapitach
            val parts = text.split("\n").map { it.trim() }.filter { it.isNotEmpty() }.flatMap { it.chunked(3500) }
            // ostatni fragment przeczytany -> zdejmij powiadomienie, ktore trzymalo czytanie przy zgaszonym ekranie
            t.setOnUtteranceProgressListener(object : android.speech.tts.UtteranceProgressListener() {
                override fun onStart(id: String?) {}
                override fun onDone(id: String?) { if (id == "v${parts.size - 1}") PlaybackService.stop(this@MainActivity) }
                @Deprecated("") override fun onError(id: String?) {}
            })
            parts.forEachIndexed { i, part -> t.speak(part, if (i == 0) android.speech.tts.TextToSpeech.QUEUE_FLUSH else android.speech.tts.TextToSpeech.QUEUE_ADD, null, "v$i") }
            PlaybackService.start(this, "🔊 " + (web()?.title ?: ""))
            toast(getString(R.string.reading_started))
        }
        if (tts != null) go() else tts = android.speech.tts.TextToSpeech(this) { st ->
            if (st == android.speech.tts.TextToSpeech.SUCCESS) go() else { tts = null; offerTts() }
        }
    }

    // brak syntezatora albo glosu w jezyku telefonu - zaproponuj pobranie
    private fun offerTts() {
        AlertDialog.Builder(this).setTitle(R.string.read_aloud).setMessage(R.string.tts_missing)
            .setPositiveButton(R.string.download) { _, _ ->
                runCatching { startActivity(Intent(android.speech.tts.TextToSpeech.Engine.ACTION_INSTALL_TTS_DATA)) }.onFailure {
                    runCatching { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("market://details?id=com.google.android.tts"))) }.onFailure {
                        startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("https://play.google.com/store/apps/details?id=com.google.android.tts")))
                    }
                }
            }
            .setNegativeButton(android.R.string.cancel, null).show()
    }

    private fun stopSpeaking() { tts?.stop(); PlaybackService.stop(this) }

    private fun showReader(title: String, body: String) {
        val dlg = android.app.Dialog(this, android.R.style.Theme_DeviceDefault_NoActionBar)
        val bg = c("#FBF8F1", "#15121F"); val fg = c("#2A2150", "#E6E1F5")
        var size = prefs.getFloat("czytnik_rozmiar", 19f)
        val text = TextView(this).apply {
            this.text = body; textSize = size; setTextColor(fg); typeface = android.graphics.Typeface.SERIF
            setLineSpacing(0f, 1.45f); setTextIsSelectable(true); setPadding(dp(20), dp(8), dp(20), dp(40))
        }
        val bar = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(8), dp(8), dp(8), dp(8)); setBackgroundColor(BAR) }
        bar.addView(TextView(this).apply { this.text = "📖 " + getString(R.string.reader); textSize = 16f; setTextColor(TEXT); setPadding(dp(8), 0, 0, 0) }, LinearLayout.LayoutParams(0, -2, 1f))
        fun resize(d: Float) { size = (size + d).coerceIn(14f, 48f); text.textSize = size; prefs.edit().putFloat("czytnik_rozmiar", size).apply() }
        bar.addView(button("A−", R.string.text_smaller, BLUE, BLUE_BG) { resize(-1f) })
        bar.addView(button("A+", R.string.text_larger, BLUE, BLUE_BG) { resize(1f) })
        bar.addView(button("🔊", R.string.read_aloud, BLUE, BLUE_BG) { if (tts?.isSpeaking == true) stopSpeaking() else speak(title + ".\n" + body) })
        bar.addView(button("✕", R.string.close_tab_short, TEXT, GRAY_BG) { dlg.dismiss() })
        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(bg) }
        col.addView(TextView(this).apply {
            this.text = title; textSize = 24f; setTextColor(fg); typeface = android.graphics.Typeface.create(android.graphics.Typeface.SERIF, android.graphics.Typeface.BOLD)
            setPadding(dp(20), dp(20), dp(20), dp(12))
        })
        col.addView(text)
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(bg) }
        root.addView(bar)
        root.addView(android.widget.ScrollView(this).apply { addView(col) }, LinearLayout.LayoutParams(-1, 0, 1f))
        // zapamietaj, gdzie skonczyles czytac (lokalnie, wg adresu strony)
        val key = "czytnik_poz_" + (web()?.url ?: title).hashCode()
        val scroll = root.getChildAt(1) as android.widget.ScrollView
        dlg.setOnDismissListener { prefs.edit().putInt(key, scroll.scrollY).apply(); stopSpeaking() }
        // dwa palce w czytniku = większy / mniejszy tekst (jak A− / A+)
        val det = android.view.ScaleGestureDetector(this, object : android.view.ScaleGestureDetector.SimpleOnScaleGestureListener() {
            override fun onScale(d: android.view.ScaleGestureDetector): Boolean { resize(size * d.scaleFactor - size); return true }
        })
        det.isQuickScaleEnabled = false
        val frame = object : FrameLayout(this) {
            override fun dispatchTouchEvent(ev: MotionEvent): Boolean { det.onTouchEvent(ev); return if (ev.pointerCount > 1) true else super.dispatchTouchEvent(ev) }
        }
        frame.addView(root)
        dlg.setContentView(frame)
        dlg.show()
        val pos = prefs.getInt(key, 0)
        if (pos > 0) scroll.post { scroll.scrollTo(0, pos); toast(getString(R.string.reader_resumed)) }
    }

    private fun menuAction(id: Int) {
        when (id) {
            16 -> openFind()
            17 -> toggleDesktop()
            20 -> startPick()
            21 -> sendTabToComputer()
            30 -> showTv()
            31 -> showLibrary()
            32 -> showMyFiles()
            33 -> showYtSongs()
            34 -> ytAddCurrent()
            35 -> askSleep()
            36 -> askDefaultBrowser()
            37 -> AlertDialog.Builder(this).setTitle(R.string.default_player).setMessage(R.string.default_player_help)
                .setPositiveButton(R.string.phone_videos) { _, _ -> showPhoneVideos() }.setNegativeButton(android.R.string.ok, null).show()
            38 -> showPhoneVideos()
            23 -> showNotes()
            24 -> newNote()
            25 -> runCatching { startActivity(Intent(DownloadManager.ACTION_VIEW_DOWNLOADS)) }
            22 -> runCatching {
                // język tylko dla Velivo (Android 13+), starsze - ustawienia aplikacji
                startActivity(Intent(if (Build.VERSION.SDK_INT >= 33) android.provider.Settings.ACTION_APP_LOCALE_SETTINGS else android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
            }
            18 -> readArticle { t, b -> showReader(t, b) }
            19 -> if (tts?.isSpeaking == true) stopSpeaking() else readArticle { t, b -> speak(t + ".\n" + b) }
            1 -> newTab(false, QuickAccess.URL)
            2 -> newTab(true, home)
            3 -> web()?.let { w -> w.url?.let { u -> addTo("zakladki", w.title ?: u, u, 500); toast(getString(R.string.added)) } }
            4 -> pick("zakladki", R.string.bookmarks)
            5 -> pick("historia", R.string.history)
            6 -> { prefs.edit().remove("historia").apply(); web()?.clearHistory(); toast(getString(R.string.history_cleared)) }
            9 -> unlock { showPasswords() }
            10 -> openBank()
            11 -> chooseTheme()
            13 -> unlock { showLanSync() }
            14 -> web()?.loadUrl(QuickAccess.URL)
            15 -> fillFromMenu()
            12 -> {
                val on = !prefs.getBoolean("tlo", true)
                prefs.edit().putBoolean("tlo", on).apply()
                if (on && Build.VERSION.SDK_INT >= 33) requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 1)
            }
            7 -> prefs.edit().putBoolean("adblock", !prefs.getBoolean("adblock", true)).apply()
        }
    }

    private fun showMenu(@Suppress("UNUSED_PARAMETER") anchor: View) {
        val dlg = android.app.Dialog(this)
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(14), dp(16), dp(18))
            background = GradientDrawable().apply {
                // jasny: biale tlo, zeby kafelki odcinaly sie wyraznie
                setColor(c("#FFFFFF", "#1B1733"))
                cornerRadii = floatArrayOf(dp(22).toFloat(), dp(22).toFloat(), dp(22).toFloat(), dp(22).toFloat(), 0f, 0f, 0f, 0f)
            }
        }
        // uchwyt do przeciągnięcia (jak w menu Androida)
        box.addView(View(this).apply { background = round(GRAY_BG) }, LinearLayout.LayoutParams(dp(40), dp(5)).apply { gravity = Gravity.CENTER_HORIZONTAL; bottomMargin = dp(12) })
        // nagłówek: logo Velivo, nazwa, licznik zablokowanych reklam
        val head = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
        head.addView(ImageView(this).apply { setImageResource(R.mipmap.ic_launcher) }, LinearLayout.LayoutParams(dp(40), dp(40)))
        head.addView(TextView(this).apply {
            text = "Velivo"; textSize = 24f; setTypeface(typeface, android.graphics.Typeface.BOLD); setPadding(dp(10), 0, 0, 0)
            // napis w gradiencie Velivo (fiolet -> turkus), jak pod ikona
            val w = paint.measureText("Velivo")
            if (contrast) setTextColor(Color.WHITE)
            else paint.shader = android.graphics.LinearGradient(dp(10).toFloat(), 0f, dp(10) + w, 0f, BRAND1, BRAND2, android.graphics.Shader.TileMode.CLAMP)
        }, LinearLayout.LayoutParams(0, -2, 1f))
        head.addView(TextView(this).apply {
            text = "🛡 " + getString(R.string.blocked, blockedCount)
            textSize = 13f; setTextColor(GREEN); background = round(GREEN_BG); setPadding(dp(10), dp(5), dp(10), dp(5))
            // stuknięcie: panel prywatności tej strony
            contentDescription = getString(R.string.privacy_panel); tooltipText = getString(R.string.privacy_panel)
            setOnClickListener { dlg.dismiss(); showPrivacy() }
        })
        box.addView(head)

        fun section(title: Int, items: List<Triple<String, Int, Int>>, toggles: Map<Int, Boolean> = emptyMap()) {
            box.addView(TextView(this).apply {
                text = getString(title); textSize = 13f; setTextColor(c("#4F4380", "#A79FD0")); setPadding(dp(2), dp(14), 0, dp(6))
            })
            // duże powiększenie - kafelki po 2 w rzędzie (liczba kolumn ustalana przy otwarciu menu)
            val grid = android.widget.GridLayout(this).apply { columnCount = if (uiScale >= 1.35f) 2 else 3 }
            for ((icon, label, id) in items) {
                val on = toggles[id]
                val primary = id == 1 || id == 14   // nowa karta, Szybki Dostep - gradient Velivo
                val (fg, bg) = when {
                    primary -> Color.WHITE to BRAND1
                    on == true -> GREEN to GREEN_BG
                    on == false -> c("#4F4380", "#A79FD0") to GRAY_BG
                    id == 2 -> PURPLE to PRIV_BG
                    id == 10 -> c("#0F766E", "#5EEAD4") to BANK_BG
                    id == 6 -> c("#B91C1C", "#FCA5A5") to c("#FEE2E2", "#450A0A")
                    id in setOf(11, 13) -> ORANGE to ORANGE_BG
                    else -> BLUE to BLUE_BG
                }
                val tile = LinearLayout(this).apply {
                    orientation = LinearLayout.VERTICAL
                    gravity = Gravity.CENTER
                    background = if (primary) brand(10) else round(bg)
                    setPadding(dp(4), dp(10), dp(4), dp(10))
                    contentDescription = getString(label)
                    tooltipText = getString(label)
                    isClickable = true
                    foreground = android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(Color.argb(60, 128, 128, 128)), null, round(Color.WHITE))
                    setOnClickListener { dlg.dismiss(); menuAction(id) }
                }
                // jednakowe kafelki: ikona w stałym polu, napis zawsze w 2 liniach (dłuższy - mniejsza czcionka)
                tile.addView(TextView(this).apply { text = icon; textSize = 24f; gravity = Gravity.CENTER }, LinearLayout.LayoutParams(-1, dp(34)))
                tile.addView(TextView(this).apply {
                    text = getString(label) + (if (on != null) " · " + getString(if (on) R.string.on else R.string.off) else "")
                    setTextColor(fg); gravity = Gravity.CENTER; setLines(2); ellipsize = TextUtils.TruncateAt.END
                    setAutoSizeTextTypeUniformWithConfiguration(9, 12, 1, android.util.TypedValue.COMPLEX_UNIT_SP)
                }, LinearLayout.LayoutParams(-1, dp(36)).apply { topMargin = dp(4) })
                grid.addView(tile, android.widget.GridLayout.LayoutParams(
                    android.widget.GridLayout.spec(android.widget.GridLayout.UNDEFINED), android.widget.GridLayout.spec(android.widget.GridLayout.UNDEFINED, 1f)
                ).apply { width = 0; height = dp(96); setMargins(dp(4), dp(4), dp(4), dp(4)) })
            }
            box.addView(grid, LinearLayout.LayoutParams(-1, -2))
        }
        // po 3 w rzędzie, pełne rzędy: karty, ta strona, moje, ustawienia
        section(R.string.menu_tabs, listOf(
            Triple("＋", R.string.new_tab, 1), Triple("🕶", R.string.new_private_tab, 2), Triple("⭐", R.string.quick_access, 14),
            Triple("🏦", R.string.bank_mode, 10), Triple("📤", R.string.send_tab, 21), Triple("🖥", R.string.desktop_mode, 17)),
            mapOf(17 to (tabs.getOrNull(cur)?.desktop == true)))
        section(R.string.menu_page, listOf(
            Triple("🔎", R.string.find_in_page, 16), Triple("📖", R.string.reader, 18),
            Triple(if (tts?.isSpeaking == true) "⏹" else "🔊", if (tts?.isSpeaking == true) R.string.stop_reading else R.string.read_aloud, 19),
            Triple("🎯", R.string.hide_element, 20), Triple("➕", R.string.add_bookmark, 3), Triple("✍", R.string.fill_password, 15)))
        section(R.string.menu_media, listOf(
            Triple("📺", R.string.tv, 30), Triple("🎬", R.string.jf_library, 31), Triple("🎞", R.string.my_films, 32),
            Triple("🎵", R.string.yt_my_songs, 33), Triple("⭐", R.string.yt_add, 34), Triple("🌙", R.string.sleep_title, 35)))
        section(R.string.menu_mine, listOf(
            Triple("🔖", R.string.bookmarks, 4), Triple("🕘", R.string.history, 5), Triple("🔑", R.string.passwords, 9),
            Triple("🗒", R.string.notes, 23), Triple("📝", R.string.note_new, 24), Triple("📥", R.string.downloads, 25)))
        section(R.string.menu_settings, listOf(
            Triple("🛡", R.string.adblock, 7), Triple("🎵", R.string.background_play, 12), Triple("🌓", R.string.theme, 11),
            Triple("💻", R.string.lan_sync, 13), Triple("🌐", R.string.language, 22), Triple("🧹", R.string.clear_history, 6),
            Triple("🧭", R.string.default_browser, 36), Triple("▶", R.string.default_player, 37), Triple("📱", R.string.phone_videos, 38)),
            mapOf(7 to prefs.getBoolean("adblock", true), 12 to prefs.getBoolean("tlo", true)))
        // na samym dole menu też płaski pasek YouTube (przytrzymanie: wyłącznik czasowy i pobudka)
        box.addView(TextView(this).apply {
            text = if (BuildConfig.PELNA) "▶  YouTube  ·  " + getString(R.string.youtube_flat) else "▶  YouTube"
            gravity = Gravity.CENTER; textSize = 14f; setTextColor(Color.WHITE)
            background = GradientDrawable().apply { setColor(Color.parseColor("#E62117")); cornerRadius = dp(10).toFloat() }
            contentDescription = getString(ytLabel); tooltipText = getString(ytLabel)
            setOnClickListener { dlg.dismiss(); ytBar.performClick() }
            setOnLongClickListener { dlg.dismiss(); askSleep(); true }
        }, LinearLayout.LayoutParams(-1, dp(44)).apply { topMargin = dp(14); leftMargin = dp(4); rightMargin = dp(4) })
        dlg.setContentView(PinchFrame(ScrollView(this).apply { addView(box) }) { dlg.dismiss() })
        dlg.window?.apply {
            setBackgroundDrawable(android.graphics.drawable.ColorDrawable(Color.TRANSPARENT))
            setLayout(-1, -2)
            setGravity(Gravity.BOTTOM)
            attributes = attributes.apply { windowAnimations = android.R.style.Animation_InputMethod }
        }
        dlg.show()
    }

    // Zapis listy: każda pozycja to "tytuł\turl", najnowsze na górze.
    private fun list(key: String) = prefs.getString(key, "")!!.lines().filter { it.contains('\t') }

    private fun addTo(key: String, title: String, url: String, max: Int) {
        val line = title.replace('\t', ' ').replace('\n', ' ') + "\t" + url
        val l = (listOf(line) + list(key).filter { !it.endsWith("\t" + url) }).take(max)
        prefs.edit().putString(key, l.joinToString("\n")).apply()
    }

    private fun addHistory(title: String, url: String) = addTo("historia", title, url, 300)

    private fun removeFrom(key: String, url: String) =
        prefs.edit().putString(key, list(key).filter { !it.endsWith("\t" + url) }.joinToString("\n")).apply()

    private fun pick(key: String, title: Int) {
        val rows = list(key).map { val url = it.substringAfter('\t'); Row(it.substringBefore('\t').ifBlank { url }, url, url) }
        showList(title, rows, key, extra = if (key == "historia") R.string.clear_history to {
            prefs.edit().remove("historia").apply(); web()?.clearHistory()
        } else null, onOpen = { row, d -> d.dismiss(); web()?.loadUrl(row.key as String) },
            onDelete = { row -> removeFrom(key, row.key as String) })
    }

    // --- Lista (zakładki, historia, hasła): litera strony, tytuł, adres, szukanie, sortowanie, ✕ ----
    private class Row(val title: String, val sub: String, val key: Any)

    private fun showList(title: Int, all: List<Row>, sortKey: String, extra: Pair<Int, () -> Unit>? = null,
                         onOpen: (Row, AlertDialog) -> Unit, onDelete: (Row) -> Unit) {
        if (all.isEmpty()) { toast(getString(R.string.empty)); return }
        val source = all.toMutableList()
        var shown = listOf<Row>()
        var sortAz = prefs.getBoolean("sort-$sortKey", false)   // false = najnowsze na górze (kolejność zapisu)
        val search = EditText(this).apply {
            hint = getString(R.string.search_hint)
            setSingleLine()
            setTextColor(TEXT)
            setHintTextColor(c("#4F4380", "#A79FD0"))
            background = round(GRAY_BG)
            setPadding(dp(12), dp(8), dp(12), dp(8))
        }
        val sortBtn = button("", R.string.sort, BLUE, BLUE_BG) {}
        val listView = ListView(this).apply { divider = null }
        lateinit var dlg: AlertDialog
        val adapter = object : BaseAdapter() {
            override fun getCount() = shown.size
            override fun getItem(i: Int) = shown[i]
            override fun getItemId(i: Int) = i.toLong()
            override fun getView(i: Int, convert: View?, parent: ViewGroup?): View {
                val row = shown[i]
                val v = LinearLayout(this@MainActivity).apply {
                    gravity = Gravity.CENTER_VERTICAL
                    setPadding(dp(4), dp(8), dp(4), dp(8))
                }
                val host = (Uri.parse(row.sub).host ?: row.title).removePrefix("www.")
                val letter = host.firstOrNull { it.isLetterOrDigit() }?.uppercaseChar()?.toString() ?: "•"
                val hue = (host.hashCode() and 0x7fffffff) % 360
                v.addView(TextView(this@MainActivity).apply {
                    text = letter
                    gravity = Gravity.CENTER
                    textSize = 16f
                    setTextColor(Color.WHITE)
                    background = GradientDrawable().apply {
                        shape = GradientDrawable.OVAL
                        setColor(Color.HSVToColor(floatArrayOf(hue.toFloat(), 0.55f, if (dark) 0.55f else 0.75f)))
                    }
                }, LinearLayout.LayoutParams(dp(36), dp(36)))
                val texts = LinearLayout(this@MainActivity).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(12), 0, dp(6), 0) }
                texts.addView(TextView(this@MainActivity).apply {
                    text = row.title; textSize = 15f; setTextColor(TEXT); maxLines = 1; ellipsize = TextUtils.TruncateAt.END
                })
                texts.addView(TextView(this@MainActivity).apply {
                    text = row.sub; textSize = 12f; setTextColor(c("#4F4380", "#A79FD0")); maxLines = 1; ellipsize = TextUtils.TruncateAt.END
                })
                v.addView(texts, LinearLayout.LayoutParams(0, -2, 1f))
                v.addView(button("✕", R.string.delete, TEXT, GRAY_BG) {
                    onDelete(row); source.remove(row); refreshList(); if (source.isEmpty()) dlg.dismiss()
                }.apply { isFocusable = false })   // inaczej ListView nie przyjmuje stuknięć w wiersz
                applyScale(v)
                return v
            }
            fun refreshList() {
                val q = search.text.toString().trim().lowercase()
                val f = if (q.isEmpty()) source else source.filter { it.title.lowercase().contains(q) || it.sub.lowercase().contains(q) }
                shown = if (sortAz) f.sortedBy { it.title.lowercase() } else f
                sortBtn.text = getString(if (sortAz) R.string.sort_az else R.string.sort_newest)
                notifyDataSetChanged()
            }
        }
        sortBtn.setOnClickListener { sortAz = !sortAz; prefs.edit().putBoolean("sort-$sortKey", sortAz).apply(); adapter.refreshList() }
        search.addTextChangedListener(object : TextWatcher {
            override fun afterTextChanged(e: Editable?) = adapter.refreshList()
            override fun beforeTextChanged(a: CharSequence?, b: Int, c: Int, d: Int) {}
            override fun onTextChanged(a: CharSequence?, b: Int, c: Int, d: Int) {}
        })
        listView.adapter = adapter
        listView.setOnItemClickListener { _, _, i, _ -> onOpen(shown[i], dlg) }
        val top = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(16), dp(4), dp(16), dp(4)) }
        top.addView(search, LinearLayout.LayoutParams(0, -2, 1f))
        top.addView(sortBtn)
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        box.addView(top)
        box.addView(listView, LinearLayout.LayoutParams(-1, (resources.displayMetrics.heightPixels * 0.6).toInt()))
        listView.setPadding(dp(12), 0, dp(12), 0)
        val b = AlertDialog.Builder(this).setTitle(getString(title) + " (" + all.size + ")").setView(PinchFrame(box))
            .setNegativeButton(R.string.close_tab_short, null)
        extra?.let { (label, act) -> b.setNeutralButton(label) { _, _ -> act() } }
        dlg = b.create()
        adapter.refreshList()
        dlg.show()
    }

    // --- Hasła ---------------------------------------------------------------
    private val passwords by lazy { Passwords(this) }
    private var lastAsked = ""

    // Hasła tylko na https - albo na http w sieci domowej (router, NAS), gdzie hasło nie wychodzi poza dom.
    private fun secureHost(w: WebView): String? {
        val u = Uri.parse(w.url ?: return null)
        val host = u.host?.lowercase() ?: return null
        return when (u.scheme) {
            "https" -> host
            "http" -> host.takeIf { isHomeNetwork(it) }
            else -> null
        }
    }

    private fun isHomeNetwork(h: String): Boolean {
        if (h == "localhost" || h.endsWith(".local") || h.endsWith(".lan") || h.endsWith(".home.arpa")) return true
        val p = h.split('.').mapNotNull { it.toIntOrNull() }
        if (p.size != 4 || h.count { it == '.' } != 3) return false
        return p[0] == 10 || (p[0] == 192 && p[1] == 168) || (p[0] == 172 && p[1] in 16..31) || p[0] == 127
    }

    // Strona wysłała formularz z hasłem - wywoływane z JavaScriptu (wątek w tle).
    private inner class PasswordBridge(private val w: WebView) {
        @JavascriptInterface
        fun zapisz(user: String, pass: String) = runOnUiThread { askSave(w, user, pass) }
    }

    private fun askSave(w: WebView, user: String, pass: String) {
        val host = secureHost(w) ?: return
        if (pass.isEmpty() || pass.length > 500 || user.length > 500) return
        val key = "$host\t$user\t${pass.hashCode()}"
        if (key == lastAsked || passwords.never(host) || passwords.isSaved(host, user, pass)) return
        lastAsked = key
        AlertDialog.Builder(this)
            .setTitle(getString(R.string.save_password_q, host))
            .setMessage(user)
            .setPositiveButton(R.string.save) { _, _ -> passwords.save(host, user, pass); toast(getString(R.string.added)) }
            .setNeutralButton(R.string.never) { _, _ -> passwords.addNever(host) }
            .setNegativeButton(R.string.not_now, null)
            .show()
    }

    private fun passwordsOnPage(w: WebView) {
        w.evaluateJavascript(CAPTURE_JS, null)
        val host = secureHost(w) ?: return
        val saved = accountsFor(host)
        if (saved.isEmpty()) return
        w.evaluateJavascript("document.querySelector('input[type=password]')!=null") { has ->
            if (has != "true" || w != web()) return@evaluateJavascript
            if (saved.size == 1) fill(w, saved[0]) else chooseAccount(w, saved)
        }
    }

    // To samo konto zapisane np. dla cda.pl i www.cda.pl - na liście raz.
    private fun accountsFor(host: String) = passwords.forHost(host).distinctBy { it.user to it.pass }

    private fun chooseAccount(w: WebView, saved: List<Passwords.Entry>) {
        AlertDialog.Builder(this)
            .setTitle(R.string.fill_password)
            .setItems(saved.map { it.user.ifEmpty { "—" } + "   (" + it.host + ")" }.toTypedArray()) { _, i -> fill(w, saved[i]) }
            .show()
    }

    // Ręcznie z menu - gdy formularz logowania pojawił się dopiero po załadowaniu strony.
    private fun fillFromMenu() {
        val w = web() ?: return
        if (tabs.getOrNull(cur)?.quiet == true) return
        val host = secureHost(w) ?: run { toast(getString(R.string.no_passwords_here)); return }
        val saved = accountsFor(host)
        when (saved.size) {
            0 -> toast(getString(R.string.no_passwords_here))
            1 -> fill(w, saved[0])
            else -> chooseAccount(w, saved)
        }
    }

    private fun fill(w: WebView, e: Passwords.Entry) {
        val h = secureHost(w) ?: return
        if (e !in passwords.forHost(h)) return   // strona zmieniła się w międzyczasie - hasło nie dla niej
        w.evaluateJavascript("$FILL_JS(${JSONObject.quote(e.user)},${JSONObject.quote(e.pass)})", null)
    }

    private fun showPasswords() {
        val rows = passwords.all().map { Row(it.host, it.user.ifEmpty { "—" }, it) }
        showList(R.string.passwords, rows, "hasla", onOpen = { row, _ ->
            val e = row.key as Passwords.Entry
            AlertDialog.Builder(this)
                .setTitle(e.host)
                .setMessage(e.user.ifEmpty { "—" })
                .setPositiveButton(R.string.copy_password) { _, _ -> copySecret(e.pass) }
                .setNeutralButton(R.string.copy_login) { _, _ -> copySecret(e.user, sensitive = false) }
                .setNegativeButton(R.string.close_tab_short, null)
                .show()
        }, onDelete = { row -> passwords.remove(row.key as Passwords.Entry) })
    }

    // Hasło w schowku oznaczone jako poufne - Android nie pokazuje go w podglądzie schowka.
    private fun copySecret(text: String, sensitive: Boolean = true) {
        val clip = ClipData.newPlainText("Velivo", text)
        if (sensitive) clip.description.extras = PersistableBundle().apply { putBoolean("android.content.extra.IS_SENSITIVE", true) }
        (getSystemService(CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(clip)
        toast(getString(R.string.copied))
    }

    // --- Synchronizacja z Velivo na Windows (sieć domowa) -------------------------
    // ===================== 🎵 Moje utwory: własna lista z YouTube - tylko w telefonie (bez konta Google) =====================
    // Każdy utwór: id filmu, tytuł, kategoria (Pop, Rock… albo własna), własne playlisty. Sortowanie i filtr zapamiętane.
    private fun ytSongs(): MutableList<org.json.JSONObject> = runCatching {
        val a = org.json.JSONArray(prefs.getString("yt-utwory", "[]")); (0 until a.length()).map { a.getJSONObject(it) }.toMutableList()
    }.getOrDefault(mutableListOf())
    private fun ytSave(l: List<org.json.JSONObject>) = prefs.edit().putString("yt-utwory", org.json.JSONArray(l).toString()).apply()
    private fun ytLists(s: org.json.JSONObject): List<String> = s.optJSONArray("l")?.let { a -> (0 until a.length()).map { a.getString(it) } } ?: emptyList()
    private fun ytCategories(): List<String> = (resources.getStringArray(R.array.yt_categories).toList() + ytSongs().map { it.optString("c") }).filter { it.isNotEmpty() }.distinct()
    private fun ytPlaylists(): List<String> = ytSongs().flatMap { ytLists(it) }.distinct().sorted()

    private fun currentYtId(): String? {
        val u = Uri.parse(web()?.url ?: return null)
        val h = u.host ?: return null
        return if (h.endsWith("youtube.com")) u.getQueryParameter("v") else if (h == "youtu.be") u.lastPathSegment else null
    }

    // ⭐ dodaj utwór, który teraz gra (strona filmu YouTube): kategoria, potem opcjonalnie playlista
    private fun ytAddCurrent() {
        val id = currentYtId() ?: return toast(getString(R.string.yt_open_first))
        val title = (web()?.title ?: id).removeSuffix(" - YouTube").trim()
        if (ytSongs().any { it.optString("id") == id }) return toast(getString(R.string.yt_already, title))
        chooseText(getString(R.string.yt_category), ytCategories(), getString(R.string.yt_new_category)) { cat ->
            val s = org.json.JSONObject().put("id", id).put("t", title).put("c", cat).put("a", System.currentTimeMillis()).put("l", org.json.JSONArray())
            ytSave(ytSongs() + s)
            toast(getString(R.string.yt_added, title))
            val pls = ytPlaylists()
            if (pls.isNotEmpty()) chooseText(getString(R.string.yt_to_playlist), listOf(getString(R.string.yt_no_playlist)) + pls, getString(R.string.yt_new_playlist)) { pl ->
                if (pl != getString(R.string.yt_no_playlist)) ytSave(ytSongs().map { if (it.optString("id") == id) it.put("l", org.json.JSONArray(ytLists(it) + pl)) else it })
            }
        }
    }

    // wybór z listy albo nowa nazwa
    private fun chooseText(title: String, options: List<String>, newLabel: String, done: (String) -> Unit) {
        val labels = (options + "＋ $newLabel").toTypedArray()
        AlertDialog.Builder(this).setTitle(title).setItems(labels) { _, i ->
            if (i < options.size) done(options[i])
            else {
                val e = EditText(this).apply { setSingleLine(); hint = newLabel }
                AlertDialog.Builder(this).setTitle(newLabel).setView(LinearLayout(this).apply { setPadding(dp(20), dp(8), dp(20), 0); addView(e, LinearLayout.LayoutParams(-1, -2)) })
                    .setPositiveButton(android.R.string.ok) { _, _ -> e.text.toString().trim().take(40).takeIf { it.isNotEmpty() }?.let(done) }
                    .setNegativeButton(android.R.string.cancel, null).show()
            }
        }.show()
    }

    private fun showYtSongs() {
        var filter = prefs.getString("yt-filtr", "") ?: ""   // "" wszystkie, "C:kategoria", "P:playlista"
        lateinit var scr: MediaScreen
        fun rows() = ytSongs().filter { s ->
            when { filter.startsWith("C:") -> s.optString("c") == filter.substring(2); filter.startsWith("P:") -> filter.substring(2) in ytLists(s); else -> true }
        }.map { s -> MRow(s.optString("t"), listOf(s.optString("c")).plus(ytLists(s).map { "📋 $it" }).filter { it.isNotEmpty() }.joinToString(" · "),
            "https://i.ytimg.com/vi/" + s.optString("id") + "/mqdefault.jpg", tag = s) }
        fun info() = when { filter.startsWith("C:") -> "🎼 " + filter.substring(2); filter.startsWith("P:") -> "📋 " + filter.substring(2); else -> getString(R.string.tv_all) } + " · " + getString(R.string.yt_count, rows().size)
        scr = MediaScreen("🎵 " + getString(R.string.yt_my_songs), "yt", listOf(getString(R.string.sort_az), getString(R.string.yt_sort_cat), getString(R.string.sort_newest)),
            sorter = { l, s -> when (s) { 0 -> l.sortedBy { it.title.lowercase() }
                1 -> l.sortedWith(compareBy({ (it.tag as org.json.JSONObject).optString("c").lowercase() }, { it.title.lowercase() }))
                else -> l.sortedByDescending { (it.tag as org.json.JSONObject).optLong("a") } } },
            onOpen = { r, shown -> dlgClose(); ytPlayQueue(shown.map { (it.tag as org.json.JSONObject).optString("id") }, shown.indexOf(r)) },
            onLong = { r -> ytSongMenu(r.tag as org.json.JSONObject) { scr.setRows(rows(), info()) } })
        ytScreen = scr
        scr.addButton("▶", R.string.yt_play_all) { if (scr.shown.isNotEmpty()) { dlgClose(); ytPlayQueue(scr.shown.map { (it.tag as org.json.JSONObject).optString("id") }, 0) } }
        scr.addButton("🔀", R.string.yt_shuffle) { if (scr.shown.isNotEmpty()) { dlgClose(); ytPlayQueue(scr.shown.map { (it.tag as org.json.JSONObject).optString("id") }.shuffled(), 0) } }
        scr.addButton("☰", R.string.tv_filter) {
            val opts = listOf("" to getString(R.string.tv_all)) + ytCategories().filter { c -> ytSongs().any { it.optString("c") == c } }.map { "C:$it" to "🎼 $it" } + ytPlaylists().map { "P:$it" to "📋 $it" }
            AlertDialog.Builder(this).setTitle(R.string.tv_filter).setItems(opts.map { it.second }.toTypedArray()) { _, i ->
                filter = opts[i].first; prefs.edit().putString("yt-filtr", filter).apply(); scr.setRows(rows(), info())
            }.show()
        }
        scr.show()
        scr.setRows(rows(), if (ytSongs().isEmpty()) getString(R.string.yt_empty) else info())
    }

    private var ytScreen: MediaScreen? = null
    private fun dlgClose() { runCatching { ytScreen?.dlg?.dismiss() } }

    private fun ytSongMenu(s: org.json.JSONObject, changed: () -> Unit) {
        val id = s.optString("id")
        fun upd(f: (org.json.JSONObject) -> Unit) { ytSave(ytSongs().map { if (it.optString("id") == id) it.also(f) else it }); changed() }
        val opts = mutableListOf<Pair<String, () -> Unit>>()
        opts.add(getString(R.string.yt_change_category) to { chooseText(getString(R.string.yt_category), ytCategories(), getString(R.string.yt_new_category)) { c -> upd { it.put("c", c) } } })
        opts.add(getString(R.string.yt_to_playlist) to { chooseText(getString(R.string.yt_to_playlist), ytPlaylists(), getString(R.string.yt_new_playlist)) { p -> upd { it.put("l", org.json.JSONArray((ytLists(it) + p).distinct())) } } })
        for (p in ytLists(s)) opts.add(getString(R.string.tv_list_remove, p) to { upd { it.put("l", org.json.JSONArray(ytLists(it) - p)) } })
        opts.add(getString(R.string.yt_rename) to {
            val e = EditText(this).apply { setSingleLine(); setText(s.optString("t")) }
            AlertDialog.Builder(this).setView(LinearLayout(this).apply { setPadding(dp(20), dp(8), dp(20), 0); addView(e, LinearLayout.LayoutParams(-1, -2)) })
                .setPositiveButton(android.R.string.ok) { _, _ -> e.text.toString().trim().takeIf { it.isNotEmpty() }?.let { t -> upd { it.put("t", t) } } }.setNegativeButton(android.R.string.cancel, null).show()
        })
        opts.add(getString(R.string.delete) to { ytSave(ytSongs().filter { it.optString("id") != id }); changed() })
        AlertDialog.Builder(this).setTitle(s.optString("t")).setItems(opts.map { it.first }.toTypedArray()) { _, i -> opts[i].second() }.show()
    }

    // --- kolejka: utwory po kolei w karcie YouTube (także w tle i przy zgaszonym ekranie); YouTube nie wtrąca swoich propozycji ---
    private var ytQueue = listOf<String>(); private var ytIndex = 0; private var ytLoadedAt = 0L
    private val ytTick = object : Runnable {
        override fun run() {
            if (ytQueue.isEmpty()) return
            val t = tabs.firstOrNull { (Uri.parse(it.web.url ?: "").host ?: "").endsWith("youtube.com") }
            if (t == null) { ytQueue = emptyList(); return }
            t.web.evaluateJavascript("(function(){var v=document.querySelector('video');return JSON.stringify({id:new URLSearchParams(location.search).get('v')||'',e:!!(v&&v.ended)})})()") { r ->
                val j = runCatching { org.json.JSONObject(org.json.JSONTokener(r).nextValue() as String) }.getOrNull() ?: return@evaluateJavascript
                val want = ytQueue.getOrNull(ytIndex)
                val settled = System.currentTimeMillis() - ytLoadedAt > 6000
                if (j.optBoolean("e") || (settled && want != null && j.optString("id").isNotEmpty() && j.optString("id") != want)) ytNext(t.web)
            }
            pipCheck.postDelayed(this, 2000)
        }
    }

    private fun ytPlayQueue(ids: List<String>, at: Int) {
        if (ids.isEmpty()) return
        ytQueue = ids; ytIndex = at.coerceIn(0, ids.size - 1)
        val i = tabs.indexOfFirst { !it.priv && !it.bank && (Uri.parse(it.web.url ?: "").host ?: "").endsWith("youtube.com") }
        if (i >= 0) show(i) else newTab(false, "about:blank")
        ytLoad(web() ?: return)
        pipCheck.removeCallbacks(ytTick); pipCheck.postDelayed(ytTick, 4000)
        toast(getString(R.string.yt_queue, ids.size))
    }

    private fun ytLoad(w: WebView) { ytLoadedAt = System.currentTimeMillis(); w.settings.mediaPlaybackRequiresUserGesture = false; w.loadUrl("https://m.youtube.com/watch?v=" + ytQueue[ytIndex]) }

    private fun ytNext(w: WebView) {
        if (ytIndex + 1 >= ytQueue.size) { ytQueue = emptyList(); return }   // koniec kolejki
        ytIndex++; ytLoad(w)
    }

    // ===================== 🎬 Filmy i TV: telewizja (IPTV + program), Jellyfin, filmy z telefonu =====================
    private class MRow(val title: String, val sub: String, val image: String?, val headers: Map<String, String> = emptyMap(), var star: Boolean? = null, val tag: Any? = null)

    // małe obrazki (logo kanałów, plakaty): w tle, w pamięci podręcznej
    private val imgCache = android.util.LruCache<String, Bitmap>(120)
    private val imgPool by lazy { java.util.concurrent.Executors.newFixedThreadPool(3) }
    private fun loadImage(iv: ImageView, url: String?, headers: Map<String, String>) {
        iv.tag = url; iv.setImageDrawable(null)
        if (url.isNullOrEmpty()) return
        imgCache.get(url)?.let { iv.setImageBitmap(it); return }
        imgPool.execute {
            val bmp = runCatching {
                val c = java.net.URL(url).openConnection() as java.net.HttpURLConnection
                c.connectTimeout = 8000; c.readTimeout = 10000
                headers.forEach { (k, v) -> c.setRequestProperty(k, v) }
                c.inputStream.use { android.graphics.BitmapFactory.decodeStream(it) }
            }.getOrNull() ?: return@execute
            val small = if (bmp.height > 240) Bitmap.createScaledBitmap(bmp, bmp.width * 240 / bmp.height, 240, true) else bmp
            imgCache.put(url, small)
            runOnUiThread { if (iv.tag == url) iv.setImageBitmap(small) }
        }
    }

    // wspólny ekran listy: szukanie, sortowanie (zapamiętane), przyciski u góry, ⭐, przytrzymanie = więcej
    private inner class MediaScreen(title: String, val sortKey: String, val sorts: List<String>,
                                    val sorter: (List<MRow>, Int) -> List<MRow>,
                                    val onOpen: (MRow, List<MRow>) -> Unit,
                                    val onLong: ((MRow) -> Unit)? = null,
                                    val onStar: ((MRow) -> Unit)? = null) {
        val dlg = android.app.Dialog(this@MainActivity, android.R.style.Theme_DeviceDefault_NoActionBar)
        val bar = LinearLayout(this@MainActivity).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(8), dp(8), dp(8), dp(8)); setBackgroundColor(BAR) }
        val status = TextView(this@MainActivity).apply { setTextColor(TEXT); textSize = 13f; setPadding(dp(14), dp(4), dp(14), dp(4)) }
        var all = listOf<MRow>(); var shown = listOf<MRow>()
        var sort = prefs.getInt("sort-$sortKey", 0)
        val search = EditText(this@MainActivity).apply {
            hint = getString(R.string.search_hint); setSingleLine(); setTextColor(TEXT); setHintTextColor(c("#4F4380", "#A79FD0"))
            background = round(GRAY_BG); setPadding(dp(12), dp(8), dp(12), dp(8))
        }
        val sortBtn = button("", R.string.sort, BLUE, BLUE_BG) {}
        val adapter = object : BaseAdapter() {
            override fun getCount() = shown.size
            override fun getItem(i: Int) = shown[i]
            override fun getItemId(i: Int) = i.toLong()
            override fun getView(i: Int, convert: View?, parent: ViewGroup?): View {
                val r = shown[i]
                val v = LinearLayout(this@MainActivity).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(10), dp(6), dp(6), dp(6)) }
                val iv = ImageView(this@MainActivity).apply { scaleType = ImageView.ScaleType.FIT_CENTER; background = round(GRAY_BG) }
                v.addView(iv, LinearLayout.LayoutParams(dp(64), dp(48)))
                loadImage(iv, r.image, r.headers)
                val t = LinearLayout(this@MainActivity).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(12), 0, dp(6), 0) }
                t.addView(TextView(this@MainActivity).apply { text = r.title; textSize = 16f; setTextColor(TEXT); maxLines = 2; ellipsize = TextUtils.TruncateAt.END })
                if (r.sub.isNotEmpty()) t.addView(TextView(this@MainActivity).apply { text = r.sub; textSize = 13f; setTextColor(c("#4F4380", "#A79FD0")); maxLines = 3; ellipsize = TextUtils.TruncateAt.END })
                v.addView(t, LinearLayout.LayoutParams(0, -2, 1f))
                r.star?.let { s ->
                    v.addView(button(if (s) "⭐" else "☆", R.string.tv_fav, ORANGE, Color.TRANSPARENT) { onStar?.invoke(r); refresh() }.apply { isFocusable = false })
                }
                applyScale(v)
                return v
            }
        }
        val listView = ListView(this@MainActivity).apply { divider = null; adapter = this@MediaScreen.adapter }

        init {
            bar.addView(TextView(this@MainActivity).apply { text = title; textSize = 18f; setTextColor(TEXT); setPadding(dp(8), 0, 0, 0) }, LinearLayout.LayoutParams(0, -2, 1f))
            val top = LinearLayout(this@MainActivity).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(10), dp(6), dp(10), dp(2)) }
            top.addView(search, LinearLayout.LayoutParams(0, -2, 1f))
            if (sorts.size > 1) top.addView(sortBtn)
            sortBtn.setOnClickListener {
                AlertDialog.Builder(this@MainActivity).setTitle(R.string.sort).setSingleChoiceItems(sorts.toTypedArray(), sort) { d, i ->
                    sort = i; prefs.edit().putInt("sort-$sortKey", i).apply(); d.dismiss(); refresh()
                }.show()
            }
            search.addTextChangedListener(object : TextWatcher {
                override fun afterTextChanged(e: Editable?) = refresh()
                override fun beforeTextChanged(a: CharSequence?, b: Int, c: Int, d: Int) {}
                override fun onTextChanged(a: CharSequence?, b: Int, c: Int, d: Int) {}
            })
            listView.setOnItemClickListener { _, _, i, _ -> onOpen(shown[i], shown) }
            listView.setOnItemLongClickListener { _, _, i, _ -> onLong?.invoke(shown[i]); onLong != null }
            val root = LinearLayout(this@MainActivity).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(c("#F7F4FF", "#15121F")) }
            root.addView(bar); root.addView(top); root.addView(status)
            root.addView(listView, LinearLayout.LayoutParams(-1, 0, 1f))
            dlg.setContentView(PinchFrame(root) { dlg.dismiss() })
        }

        fun addButton(label: String, tip: Int, act: () -> Unit) { bar.addView(button(label, tip, BLUE, BLUE_BG) { act() }) }
        fun show() { bar.addView(button("✕", R.string.close_tab_short, TEXT, GRAY_BG) { dlg.dismiss() }); dlg.show() }
        fun setRows(rows: List<MRow>, info: String = "") { all = rows; status.text = info; status.visibility = if (info.isEmpty()) View.GONE else View.VISIBLE; refresh() }
        fun refresh() {
            val q = search.text.toString().trim().lowercase()
            val f = if (q.isEmpty()) all else all.filter { it.title.lowercase().contains(q) || it.sub.lowercase().contains(q) }
            shown = sorter(f, sort)
            sortBtn.text = sorts.getOrNull(sort) ?: ""
            adapter.notifyDataSetChanged()
        }
    }

    private fun play(items: List<PlayerActivity.Item>, at: Int, live: Boolean) {
        if (items.isEmpty()) return
        PlayerActivity.queue = items; PlayerActivity.index = at.coerceIn(0, items.size - 1); PlayerActivity.live = live
        startActivity(Intent(this, PlayerActivity::class.java))
    }

    private fun fmtTime(ms: Long) = java.text.SimpleDateFormat("HH:mm", java.util.Locale.getDefault()).format(java.util.Date(ms))

    // ---------- 📺 Telewizja ----------
    private fun tvFavs() = prefs.getStringSet("tv-ulubione", emptySet())!!
    private fun tvLists(): org.json.JSONObject = runCatching { org.json.JSONObject(prefs.getString("tv-listy", "{}")!!) }.getOrDefault(org.json.JSONObject())
    private fun tvListKeys(name: String): Set<String> = tvLists().optJSONArray(name)?.let { a -> (0 until a.length()).map { a.getString(it) }.toSet() } ?: emptySet()
    private fun tvSetList(name: String, keys: Collection<String>) {
        val j = tvLists(); if (keys.isEmpty()) j.remove(name) else j.put(name, org.json.JSONArray(keys.toList())); prefs.edit().putString("tv-listy", j.toString()).apply()
    }

    private fun showTv() {
        val sources = Tv.sources(passwords)
        if (sources.isEmpty()) { editTvSource(null) { showTv() }; return }
        var chans = listOf<Tv.Channel>()
        var epg = mapOf<String, Map<String, List<Tv.Show>>>()
        var filter = prefs.getString("tv-filtr", "") ?: ""   // "" wszystkie, "*" ulubione, "L:nazwa" własna lista, "G:grupa" grupa
        var at = 0L   // 📅 program na wybraną godzinę; 0 = teraz
        lateinit var scr: MediaScreen
        fun rows(): List<MRow> {
            val fav = tvFavs()
            val keys = when { filter == "*" -> fav; filter.startsWith("L:") -> tvListKeys(filter.substring(2)); else -> null }
            return chans.filter { ch -> keys?.contains(ch.key) ?: (!filter.startsWith("G:") || ch.group == filter.substring(2)) }.map { ch ->
                val (now, nxt) = Tv.nowNext(epg[ch.source] ?: emptyMap(), ch, if (at > 0) at else System.currentTimeMillis())
                val sub = listOfNotNull(now?.let { getString(R.string.tv_now, it.title, fmtTime(it.stop)) }, nxt?.let { getString(R.string.tv_next, fmtTime(it.start), it.title) })
                    .joinToString("\n").ifEmpty { if (filter.startsWith("G:")) "" else ch.group }   // grupa tylko przy wszystkich kanałach - przy wybranej grupie to powtórka nagłówka
                MRow(ch.name, sub, ch.logo.ifEmpty { null }, star = ch.key in fav, tag = ch)
            }
        }
        fun info() = getString(R.string.tv_count, chans.size) + (if (at > 0) " · 📅 " + java.text.SimpleDateFormat("EEE HH:mm", java.util.Locale.getDefault()).format(java.util.Date(at)) else "") + " · " + when {
            filter == "*" -> "⭐ " + getString(R.string.tv_fav); filter.startsWith("L:") -> "📋 " + filter.substring(2)
            filter.startsWith("G:") -> filter.substring(2); else -> getString(R.string.tv_all) }
        val watched = prefs.getStringSet("tv-ogladane", emptySet())!!
        scr = MediaScreen("📺 " + getString(R.string.tv), "tv2", listOf(getString(R.string.sort_az), getString(R.string.sort_fav_first), getString(R.string.tv_sort_list)),   // domyślnie alfabetycznie
            sorter = { l, s -> when (s) { 0 -> l.sortedBy { it.title.lowercase() }; 1 -> l.sortedWith(compareBy({ it.star != true }, { it.title.lowercase() })); else -> l } },
            onOpen = { r, shownRows ->
                val future = if (at > System.currentTimeMillis() + 60_000) Tv.nowNext(epg[(r.tag as Tv.Channel).source] ?: emptyMap(), r.tag as Tv.Channel, at).first else null
                if (future != null) { showOpts(r.tag as Tv.Channel, future); return@MediaScreen }   // 📅 audycja później - zaplanuj albo oglądaj
                val items = shownRows.map { val ch = it.tag as Tv.Channel; PlayerActivity.Item(ch.name, ch.url, ch.key, ch = ch, info = it.sub, desc = Tv.nowNext(epg[ch.source] ?: emptyMap(), ch).first?.desc ?: "", endsAt = Tv.nowNext(epg[ch.source] ?: emptyMap(), ch).first?.stop ?: 0L) }
                prefs.edit().putStringSet("tv-ogladane", watched + (r.tag as Tv.Channel).key).apply()
                play(items, shownRows.indexOf(r), live = true)
            },
            onLong = { r -> tvChannelMenu(r.tag as Tv.Channel, epg[(r.tag as Tv.Channel).source] ?: emptyMap()) { scr.setRows(rows(), info()) } },
            onStar = { r -> val k = (r.tag as Tv.Channel).key; val f = tvFavs(); prefs.edit().putStringSet("tv-ulubione", if (k in f) f - k else f + k).apply(); r.star = r.star != true })
        fun reload(refresh: Boolean) {
            if (refresh || chans.isEmpty()) scr.setRows(emptyList(), getString(R.string.tv_loading))
            Thread {
                // najpierw kanały (z pamięci albo zapisu - bez sieci), od razu na ekran; program TV dociąga się potem w tle
                val all = ArrayList<Tv.Channel>(); var err = ""
                val srcs = Tv.sources(passwords)
                val per = srcs.map { s -> s to runCatching { Tv.channels(this, passwords, s, refresh) }.getOrElse { e -> err += "\n" + s.name + ": " + tvErr(e); emptyList() } }
                per.forEach { all.addAll(it.second) }
                val errText = if (err.isNotEmpty()) " · " + getString(R.string.tv_error) + err else ""
                runOnUiThread { chans = all; scr.setRows(rows(), info() + errText) }
                val ep = HashMap<String, Map<String, List<Tv.Show>>>()
                per.forEach { (s, c) -> ep[s.id] = runCatching { Tv.epg(this, s, c, refresh) }.getOrDefault(emptyMap()) }
                runOnUiThread { epg = ep; scr.setRows(rows(), info() + errText) }
            }.start()
        }
        scr.addButton("📅", R.string.tv_guide) {   // pełny program: co leci na wszystkich kanałach o wybranej porze
            fun dayAt(days: Int, h: Int, m: Int) = java.util.Calendar.getInstance().apply { add(java.util.Calendar.DAY_OF_YEAR, days); set(java.util.Calendar.HOUR_OF_DAY, h); set(java.util.Calendar.MINUTE, m); set(java.util.Calendar.SECOND, 0) }.timeInMillis
            val opts = listOf(getString(R.string.tv_at_now), getString(R.string.tv_at_evening), getString(R.string.tv_at_tomorrow), getString(R.string.tv_at_pick), getString(R.string.tv_plans), getString(R.string.tv_all_shows), getString(R.string.tv_grid))
            AlertDialog.Builder(this).setTitle(R.string.tv_guide).setItems(opts.toTypedArray()) { _, i ->
                when (i) {
                    0 -> { at = 0; scr.setRows(rows(), info()) }
                    1 -> { at = dayAt(0, 20, 0); scr.setRows(rows(), info()) }
                    2 -> { at = dayAt(1, 20, 0); scr.setRows(rows(), info()) }
                    4 -> showPlans()
                    5 -> showAllShows(rows().map { it.tag as Tv.Channel }, epg)
                    6 -> showGrid(rows().map { it.tag as Tv.Channel }, epg)
                    3 -> {
                        val days = arrayOf(getString(R.string.tv_today), getString(R.string.tv_tomorrow), getString(R.string.tv_day_after))
                        AlertDialog.Builder(this).setItems(days) { _, d ->
                            android.app.TimePickerDialog(this, { _, h, m -> at = dayAt(d, h, m); scr.setRows(rows(), info()) }, 20, 0, true).show()
                        }.show()
                    }
                }
            }.show()
        }
        scr.addButton("☰", R.string.tv_filter) {
            val groups = chans.map { it.group }.filter { it.isNotEmpty() }.distinct().sorted()
            val lists = tvLists().keys().asSequence().toList().sorted()
            val opts = listOf("" to getString(R.string.tv_all), "*" to "⭐ " + getString(R.string.tv_fav)) + lists.map { "L:$it" to "📋 $it" } + groups.map { "G:$it" to it }
            AlertDialog.Builder(this).setTitle(R.string.tv_filter).setItems(opts.map { it.second }.toTypedArray()) { _, i ->
                filter = opts[i].first; prefs.edit().putString("tv-filtr", filter).apply(); scr.setRows(rows(), info())
            }.show()
        }
        scr.addButton("⟳", R.string.reload) { reload(true) }
        scr.addButton("⚙", R.string.tv_sources) { tvSources { reload(true) } }
        scr.show()
        reload(false)
    }

    // przytrzymanie kanału: program TV, własne listy
    // 📅 pełny program kanału: dni i godziny, ⏰ przy zaplanowanych; stuknięcie - oglądaj albo zaplanuj
    private fun showGuide(ch: Tv.Channel, epg: Map<String, List<Tv.Show>>) {
        val now = System.currentTimeMillis()
        val day = java.text.SimpleDateFormat("EEE d.MM", java.util.Locale.getDefault())
        fun rows() = Tv.showsFor(epg, ch).filter { it.stop > now || Tv.canCatchup(ch, it) }.map { s ->
            val planned = TvPlan.find(this, ch.key, s.start) != null
            MRow((if (planned) "⏰ " else if (s.stop <= now) "⏪ " else if (s.start <= now) "▶ " else "") + fmtTime(s.start) + "  " + s.title, day.format(java.util.Date(s.start)) + " · " + fmtTime(s.start) + "–" + fmtTime(s.stop) + if (s.desc.isNotEmpty()) "\n" + s.desc else "", ch.logo.ifEmpty { null }, tag = s)
        }
        lateinit var scr: MediaScreen
        scr = MediaScreen("📅 " + ch.name, "tv-guide", listOf(getString(R.string.tv_sort_list)), sorter = { l, _ -> l },
            onOpen = { r, _ -> showOpts(ch, r.tag as Tv.Show) { scr.setRows(rows(), "") } })
        scr.show()
        scr.setRows(rows(), if (Tv.showsFor(epg, ch).isEmpty()) getString(R.string.tv_no_guide) else "")
    }

    // audycja: później - ⏰ włącz o tej godzinie (najpierw); trwa - oglądaj; była - z archiwum. Opis pod "ℹ"
    // (AlertDialog nie pokazuje listy razem z tekstem - dlatego czas w tytule, a opis osobno)
    private fun showOpts(ch: Tv.Channel, s: Tv.Show, changed: () -> Unit = {}) {
        val now = System.currentTimeMillis()
        val plan = TvPlan.find(this, ch.key, s.start)
        val opts = mutableListOf<Pair<String, () -> Unit>>()
        if (s.start > now) opts.add(if (plan == null) getString(R.string.tv_plan_at, fmtTime(s.start)) to {
                // po włączeniu: oglądać dalej czy wyłączyć odtwarzanie z końcem audycji
                AlertDialog.Builder(this).setTitle(getString(R.string.tv_plan_at, fmtTime(s.start))).setItems(arrayOf(getString(R.string.tv_plan_keep), getString(R.string.tv_plan_off, fmtTime(s.stop)))) { _, k ->
                    TvPlan.add(this, ch, s, offAfter = k == 1); toast(getString(R.string.tv_plan_set, s.title, fmtTime(s.start))); changed()
                }.show() }
            else getString(R.string.tv_plan_remove) to { TvPlan.remove(this, plan.id); changed() })
        if (Tv.canCatchup(ch, s)) opts.add(getString(R.string.tv_catchup) to {   // ⏪ archiwum: audycja, która była (albo od początku tej, co trwa)
            play(listOf(PlayerActivity.Item(ch.name + " – " + s.title, ch.url, null, info = fmtTime(s.start) + "–" + fmtTime(s.stop), desc = s.desc, ch = ch, arch = PlayerActivity.Arch(s.start, s.stop))), 0, live = false) })
        opts.add(getString(R.string.tv_plan_watch) to { playChannel(ch) })
        if (s.desc.isNotEmpty()) opts.add("ℹ " + getString(R.string.tv_desc) to {
            AlertDialog.Builder(this).setTitle(s.title).setMessage(s.desc).setPositiveButton(android.R.string.ok) { _, _ -> showOpts(ch, s, changed) }.show() })
        val day = java.text.SimpleDateFormat("EEE d.MM", java.util.Locale.getDefault()).format(java.util.Date(s.start))
        AlertDialog.Builder(this).setTitle(s.title + "\n" + ch.name + " · " + day + " " + fmtTime(s.start) + "–" + fmtTime(s.stop))
            .setItems(opts.map { it.first }.toTypedArray()) { _, i -> opts[i].second() }.show()
    }

    // 📋 co i kiedy: kanały z wybranej grupy (ulubionych, listy) alfabetycznie, pod spodem co leci; stuknięcie - program kanału
    private fun showAllShows(chans: List<Tv.Channel>, epg: Map<String, Map<String, List<Tv.Show>>>) {
        fun rows() = chans.sortedBy { it.name.lowercase() }.map { ch ->
            val (now, nxt) = Tv.nowNext(epg[ch.source] ?: emptyMap(), ch)
            MRow(ch.name, listOfNotNull(now?.let { getString(R.string.tv_now, it.title, fmtTime(it.stop)) }, nxt?.let { getString(R.string.tv_next, fmtTime(it.start), it.title) }).joinToString("\n"), ch.logo.ifEmpty { null }, tag = ch)
        }
        val scr = MediaScreen("📋 " + getString(R.string.tv_all_shows), "tv-all", listOf(getString(R.string.sort_az)), sorter = { l, _ -> l.sortedBy { it.title.lowercase() } },
            onOpen = { r, _ -> val ch = r.tag as Tv.Channel; showGuide(ch, epg[ch.source] ?: emptyMap()) })
        scr.show()
        scr.setRows(rows(), "")
    }

    // 🗓 siatka programu (jak w Televizo): kanały w wierszach, czas w poziomie, ⏰ i ⏪ po stuknięciu audycji
    private fun showGrid(chans: List<Tv.Channel>, epg: Map<String, Map<String, List<Tv.Show>>>) {
        val list = chans.sortedBy { it.name.lowercase() }.take(150)   // alfabetycznie, jak wszystkie listy   // tyle, ile da się płynnie przewinąć - wybierz grupę albo ulubione
        val now = System.currentTimeMillis()
        val back = list.maxOfOrNull { if (it.catchup.isNotEmpty()) it.catchupDays.coerceAtLeast(1) else 0 }?.coerceIn(0, 7) ?: 0   // archiwum: tyle dni wstecz
        val from = now - (if (back > 0) back * 24L else 2L) * 3600_000L - now % 1800_000L; val to = now + 48 * 3600_000L
        val pxMin = dp(4).toFloat()   // 4 dp na minutę: 30 min = 120 dp
        val rowH = dp(56); val nameW = dp(120)
        fun x(t: Long) = ((t - from) / 60_000f * pxMin).toInt()
        val dlg = android.app.Dialog(this, android.R.style.Theme_DeviceDefault_NoActionBar)
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(BAR) }
        val bar = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(8), dp(8), dp(8), dp(8)) }
        bar.addView(TextView(this).apply { text = "🗓 " + getString(R.string.tv_grid); textSize = 18f; setTextColor(TEXT); setPadding(dp(8), 0, 0, 0) }, LinearLayout.LayoutParams(0, -2, 1f))
        bar.addView(button("✕", R.string.close_tab_short, TEXT, GRAY_BG) { dlg.dismiss() })
        root.addView(bar)
        val names = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        val grid = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        // nagłówek z godzinami
        names.addView(TextView(this), LinearLayout.LayoutParams(nameW, dp(28)))
        val head = FrameLayout(this)
        var t = from; while (t < to) { head.addView(TextView(this).apply { text = java.text.SimpleDateFormat("EEE HH:mm", java.util.Locale.getDefault()).format(java.util.Date(t)); textSize = 12f; setTextColor(TEXT) },
            FrameLayout.LayoutParams(-2, -2).apply { leftMargin = x(t) + dp(4) }); t += 1800_000L }
        grid.addView(head, LinearLayout.LayoutParams(x(to), dp(28)))
        for (ch in list) {
            names.addView(TextView(this).apply { text = ch.name; textSize = 13f; setTextColor(TEXT); maxLines = 2; gravity = Gravity.CENTER_VERTICAL; setPadding(dp(8), 0, dp(4), 0); setBackgroundColor(c("#EEE9FF", "#241F45"))
                setOnClickListener { playChannel(ch); dlg.dismiss() } }, LinearLayout.LayoutParams(nameW, rowH).apply { bottomMargin = dp(2) })
            val row = FrameLayout(this)
            for (sh in Tv.showsFor(epg[ch.source] ?: emptyMap(), ch).filter { it.stop > from && it.start < to }) {
                val l = x(maxOf(sh.start, from)); val w = (x(minOf(sh.stop, to)) - l - dp(2)).coerceAtLeast(dp(8))
                val nowShow = sh.start <= now && sh.stop > now
                row.addView(TextView(this).apply {
                    text = (if (TvPlan.find(this@MainActivity, ch.key, sh.start) != null) "⏰ " else if (sh.stop <= now && Tv.canCatchup(ch, sh)) "⏪ " else "") + sh.title + "\n" + fmtTime(sh.start)
                    textSize = 12f; maxLines = 2; setTextColor(TEXT); setPadding(dp(6), dp(4), dp(4), dp(4))
                    background = round(if (nowShow) c("#D9CCFF", "#4A3F8A") else if (sh.stop <= now) c("#F2EFFA", "#1B1733") else c("#E6DFFF", "#2E2768"))
                    setOnClickListener { showOpts(ch, sh) }
                }, FrameLayout.LayoutParams(w, rowH).apply { leftMargin = l })
            }
            grid.addView(row, LinearLayout.LayoutParams(x(to), rowH).apply { bottomMargin = dp(2) })
        }
        // linia „teraz”
        val content = FrameLayout(this).apply { addView(grid); addView(View(this@MainActivity).apply { setBackgroundColor(Color.parseColor("#FF5A5A")) }, FrameLayout.LayoutParams(dp(2), -1).apply { leftMargin = x(now) }) }
        val hs = android.widget.HorizontalScrollView(this).apply { addView(content) }
        val body = LinearLayout(this).apply { addView(names); addView(hs) }
        root.addView(ScrollView(this).apply { addView(body) }, LinearLayout.LayoutParams(-1, 0, 1f))
        if (chans.size > list.size) root.addView(TextView(this).apply { text = getString(R.string.tv_grid_more, list.size, chans.size); setTextColor(TEXT); textSize = 12f; setPadding(dp(12), dp(6), dp(12), dp(6)) })
        dlg.setContentView(PinchFrame(root) { dlg.dismiss() }); dlg.show()
        hs.post { hs.scrollTo((x(now) - dp(60)).coerceAtLeast(0), 0) }
    }

    private fun showPlans() {
        val l = TvPlan.list(this)
        if (l.isEmpty()) { toast(getString(R.string.tv_plans_empty)); return }
        val day = java.text.SimpleDateFormat("EEE d.MM HH:mm", java.util.Locale.getDefault())
        AlertDialog.Builder(this).setTitle(R.string.tv_plans).setItems(l.map { "⏰ " + day.format(java.util.Date(it.at)) + "  " + it.show + "\n" + it.channel + if (it.offAt > 0) " · ⏹ " + fmtTime(it.offAt) else "" }.toTypedArray()) { _, i ->
            AlertDialog.Builder(this).setTitle(l[i].show).setItems(arrayOf(getString(R.string.tv_plan_remove))) { _, _ -> TvPlan.remove(this, l[i].id) }.show()
        }.show()
    }

    // kanał z listy (zapis w telefonie) - z kolejką całej listy, by działały ⏮ ⏭
    private fun playChannel(ch: Tv.Channel, wake: Boolean = false, offAt: Long = 0) {
        Thread {
            val src = Tv.sources(passwords).firstOrNull { it.id == ch.source } ?: return@Thread
            val all = runCatching { Tv.channels(this, passwords, src, false) }.getOrNull() ?: return@Thread
            val i = all.indexOfFirst { it.key == ch.key }.coerceAtLeast(0)
            runOnUiThread { PlayerActivity.wake = wake; PlayerActivity.offAt = offAt; play(all.map { PlayerActivity.Item(it.name, it.url, it.key, ch = it) }, i, live = true) }
        }.start()
    }

    // ⏰ zaplanowana audycja: o czasie Velivo samo włącza kanał (także przy zablokowanym ekranie)
    private fun startPlanned(id: Int) {
        val p = TvPlan.get(this, id) ?: return
        TvPlan.remove(this, id)
        playChannel(Tv.Channel(p.src, p.key.substringAfter('|'), "", "", "", ""), wake = true, offAt = p.offAt)
    }

    private fun tvChannelMenu(ch: Tv.Channel, epg: Map<String, List<Tv.Show>>, changed: () -> Unit) {
        val lists = tvLists().keys().asSequence().toList().sorted()
        val opts = mutableListOf<Pair<String, () -> Unit>>()
        opts.add(getString(R.string.tv_guide) to { showGuide(ch, epg) })
        for (l in lists) {
            val inL = ch.key in tvListKeys(l)
            opts.add(getString(if (inL) R.string.tv_list_remove else R.string.tv_list_add, l) to { tvSetList(l, if (inL) tvListKeys(l) - ch.key else tvListKeys(l) + ch.key); changed() })
        }
        opts.add(getString(R.string.tv_list_new) to {
            val e = EditText(this).apply { setSingleLine(); hint = getString(R.string.tv_list_name) }
            AlertDialog.Builder(this).setTitle(R.string.tv_list_new).setView(LinearLayout(this).apply { setPadding(dp(20), dp(8), dp(20), 0); addView(e, LinearLayout.LayoutParams(-1, -2)) })
                .setPositiveButton(android.R.string.ok) { _, _ -> e.text.toString().trim().take(40).takeIf { it.isNotEmpty() }?.let { tvSetList(it, tvListKeys(it) + ch.key); changed() } }
                .setNegativeButton(android.R.string.cancel, null).show()
        })
        AlertDialog.Builder(this).setTitle(ch.name).setItems(opts.map { it.first }.toTypedArray()) { _, i -> opts[i].second() }.show()
    }

    private fun tvSources(changed: () -> Unit) {
        val src = Tv.sources(passwords)
        val labels = (src.map { "📺 " + it.name } + getString(R.string.tv_source_add) + getString(R.string.media_share_ask)).toTypedArray()
        AlertDialog.Builder(this).setTitle(R.string.tv_sources).setItems(labels) { _, i ->
            if (i == src.size + 1) askComputerMedia()
            else if (i == src.size) editTvSource(null, changed)
            else AlertDialog.Builder(this).setTitle(src[i].name).setItems(arrayOf(getString(R.string.tv_source_edit), getString(R.string.tv_source_delete))) { _, k ->
                if (k == 0) editTvSource(src[i], changed)
                else { Tv.saveSources(passwords, src.filterIndexed { j, _ -> j != i }); java.io.File(filesDir, "tv").listFiles()?.filter { it.name.startsWith(src[i].id) }?.forEach { it.delete() }; changed() }
            }.show()
        }.show()
    }

    // 📲 Filmy i TV z komputera bez parowania: komputer pyta właściciela i pokazuje kod; telefon potem pyta o zapis
    private fun askComputerMedia() {
        val temp = !prefs.getBoolean("lan", false)
        startLan(force = true)   // telefon bez synchronizacji (np. mamy) - sieć tylko na czas prośby
        lan.askMedia()
        if (temp) idle.postDelayed({ if (!prefs.getBoolean("lan", false)) stopLan() }, 4 * 60 * 1000L)
        AlertDialog.Builder(this).setTitle(R.string.media_share_ask).setMessage(R.string.media_share_wait).setPositiveButton(android.R.string.ok, null).show()
    }

    // dodanie / zmiana listy: nazwa, adres M3U (albo plik z telefonu), adres programu TV (EPG, opcjonalnie)
    // powód błędu listy TV bez adresu (adres może zawierać token)
    private fun tvErr(e: Throwable): String = when (e) {
        is java.net.UnknownHostException -> "nie znaleziono serwera"
        is java.net.SocketTimeoutException -> "serwer nie odpowiada"
        is java.net.ConnectException -> "brak połączenia z serwerem"
        is javax.net.ssl.SSLException -> "błąd https"
        is SecurityException -> "brak dostępu do pliku – dodaj go ponownie"
        is java.io.FileNotFoundException -> "nie ma pliku"
        else -> (e.message ?: e.javaClass.simpleName).take(80)
    }

    private fun editTvSource(s: Tv.Source?, changed: () -> Unit) {
        val name = EditText(this).apply { setSingleLine(); hint = getString(R.string.tv_source_name); setText(s?.name ?: "") }
        val url = EditText(this).apply { setSingleLine(); hint = getString(R.string.tv_source_url); setText(s?.url ?: ""); inputType = EditorInfo.TYPE_TEXT_VARIATION_URI }
        val epg = EditText(this).apply { setSingleLine(); hint = getString(R.string.tv_source_epg); setText(s?.epg ?: ""); inputType = EditorInfo.TYPE_TEXT_VARIATION_URI }
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20), dp(8), dp(20), 0) }
        box.addView(name); box.addView(url)
        box.addView(wide(button("📁 " + getString(R.string.tv_source_file), R.string.tv_source_file, BLUE, BLUE_BG) {
            pickDocuments(arrayOf("*/*"), false) { uris -> uris.firstOrNull()?.let { u ->
                runCatching { contentResolver.takePersistableUriPermission(u, Intent.FLAG_GRANT_READ_URI_PERMISSION) }   // plik dostępny też po restarcie
                url.setText(u.toString()) } }
        }))
        box.addView(epg)
        if (s == null) box.addView(wide(button(getString(R.string.media_share_ask), R.string.media_share_ask, BLUE, BLUE_BG) { askComputerMedia() }))
        box.addView(TextView(this).apply { text = getString(R.string.tv_source_help); textSize = 12f; setTextColor(TEXT); setPadding(0, dp(8), 0, 0) })
        AlertDialog.Builder(this).setTitle(if (s == null) R.string.tv_source_add else R.string.tv_source_edit).setView(ScrollView(this).apply { addView(box) })
            .setPositiveButton(android.R.string.ok) { _, _ ->
                var u = url.text.toString().trim().replace(" ", "%20")
                if (!u.contains("://") && u.contains('.')) u = "http://$u"   // adres wpisany bez http://
                if (!(u.startsWith("http://") || u.startsWith("https://") || u.startsWith("content://") || u.startsWith("velivo-pc:"))) { toast(getString(R.string.tv_source_bad)); return@setPositiveButton }
                val list = Tv.sources(passwords)
                val n = Tv.Source(s?.id ?: java.util.UUID.randomUUID().toString().replace("-", "").take(12), name.text.toString().trim().ifEmpty { "TV" }, u, epg.text.toString().trim())
                Tv.saveSources(passwords, if (s == null) list + n else list.map { if (it.id == n.id) n else it })
                changed()
            }.setNegativeButton(android.R.string.cancel, null).show()
    }

    // ---------- 🎬 Biblioteka (Jellyfin) ----------
    private fun showLibrary() {
        val c = Jelly.conf(passwords) ?: return setupJellyfin { showLibrary() }
        val hdr = mapOf("X-Emby-Token" to c.key)
        var mode = prefs.getInt("jf-widok", 0)   // 0 oglądane dalej, 1 filmy, 2 seriale
        var lib = listOf<Jelly.Item>(); var resume = listOf<Jelly.Item>()
        lateinit var scr: MediaScreen
        fun dur(t: Long) = if (t > 0) (t / 600_000_000).let { m -> if (m >= 60) "${m / 60} h ${m % 60} min" else "$m min" } else ""
        fun row(it: Jelly.Item) = MRow(if (it.series.isNotEmpty()) it.series + " – " + it.name else it.name,
            listOf(if (it.year > 0) it.year.toString() else "", dur(it.ticks), if (it.posTicks > 0 && it.ticks > 0) "▶ " + (it.posTicks * 100 / it.ticks) + "%" else "").filter { s -> s.isNotEmpty() }.joinToString(" · ") +
                if (it.overview.isNotEmpty()) "\n" + it.overview else "", Jelly.image(c, it.id), hdr, tag = it)
        fun rows() = when (mode) { 0 -> resume; 1 -> lib.filter { it.type == "Movie" }; else -> lib.filter { it.type == "Series" } }.map { row(it) }
        val titles = listOf(getString(R.string.jf_resume), getString(R.string.jf_movies), getString(R.string.jf_series))
        fun item(it: Jelly.Item) = PlayerActivity.Item(if (it.series.isNotEmpty()) it.series + " – " + it.name else it.name, Jelly.stream(c, it.id), "jf-" + it.id, hdr, it.id)
        scr = MediaScreen("🎬 " + getString(R.string.jf_library), "jf", listOf(getString(R.string.sort_az), getString(R.string.sort_newest), getString(R.string.jf_sort_year)),
            sorter = { l, s -> when (s) { 0 -> l.sortedBy { it.title.lowercase() }; 1 -> l.sortedByDescending { (it.tag as Jelly.Item).added }; else -> l.sortedByDescending { (it.tag as Jelly.Item).year } } },
            onOpen = { r, shownRows ->
                val it = r.tag as Jelly.Item
                if (it.type == "Series") showEpisodes(c, it)
                else { val movies = shownRows.map { x -> x.tag as Jelly.Item }.filter { x -> x.type != "Series" }; play(movies.map { x -> item(x) }, movies.indexOf(it), false) }
            })
        fun load() {
            // najpierw zapis z telefonu (od razu), potem świeże dane z serwera w tle
            val cache = Jelly.cached(this, passwords)
            if (cache != null) { lib = cache.first; resume = cache.second; scr.setRows(rows(), titles[mode]) } else scr.setRows(emptyList(), getString(R.string.tv_loading))
            Thread {
                val ok = runCatching { Jelly.refresh(this, passwords, c).let { lib = it.first; resume = it.second } }.isSuccess
                runOnUiThread { scr.setRows(rows(), titles[mode] + if (!ok && cache == null) " · " + getString(R.string.jf_error) else "") }
            }.start()
        }
        scr.addButton("☰", R.string.tv_filter) {
            AlertDialog.Builder(this).setSingleChoiceItems(titles.toTypedArray(), mode) { d, i -> mode = i; prefs.edit().putInt("jf-widok", i).apply(); d.dismiss(); scr.setRows(rows(), titles[mode]) }.show()
        }
        scr.addButton("⟳", R.string.reload) { load() }
        scr.addButton("⚙", R.string.jf_settings) { setupJellyfin { load() } }
        scr.show()
        load()
    }

    private fun showEpisodes(c: Jelly.Conf, series: Jelly.Item) {
        val hdr = mapOf("X-Emby-Token" to c.key)
        lateinit var scr: MediaScreen
        scr = MediaScreen("📺 " + series.name, "jf-odc", listOf(getString(R.string.tv_sort_list), getString(R.string.sort_az)),
            sorter = { l, s -> if (s == 1) l.sortedBy { it.title.lowercase() } else l },
            onOpen = { r, shownRows ->
                val eps = shownRows.map { it.tag as Jelly.Item }
                play(eps.map { PlayerActivity.Item(series.name + " – " + it.name, Jelly.stream(c, it.id), "jf-" + it.id, hdr, it.id) }, shownRows.indexOf(r), false)
            })
        scr.show()
        scr.setRows(emptyList(), getString(R.string.tv_loading))
        Thread {
            val eps = runCatching { Jelly.episodes(c, series.id) }.getOrDefault(emptyList())
            runOnUiThread { scr.setRows(eps.map { MRow(it.name, (if (it.posTicks > 0 && it.ticks > 0) "▶ " + (it.posTicks * 100 / it.ticks) + "% · " else "") + it.overview, Jelly.image(c, it.id), hdr, tag = it) }) }
        }.start()
    }

    // Jellyfin bez kluczy API: Velivo samo szuka serwera w sieci domowej, wybierasz siebie z listy i wpisujesz hasło
    // (jak w aplikacji Jellyfin). Token dostaje tylko ten telefon - zaszyfrowany; widać go w Jellyfin → Urządzenia.
    private fun setupJellyfin(done: () -> Unit) {
        toast(getString(R.string.jf_searching))
        Thread {
            val found = Jelly.discover()
            runOnUiThread {
                val labels = found.map { "🎬 " + it.second + "\n" + it.first } + getString(R.string.jf_manual) +
                    (if (Jelly.conf(passwords) != null) listOf(getString(R.string.jf_forget)) else emptyList())
                AlertDialog.Builder(this).setTitle(if (found.isEmpty()) getString(R.string.jf_none_found) else getString(R.string.jf_settings))
                    .setItems(labels.toTypedArray()) { _, i ->
                        when {
                            i < found.size -> jellyfinUser(found[i].first, done)
                            i == found.size -> {
                                val url = EditText(this).apply { setSingleLine(); hint = "http://192.168.1.10:8096"; inputType = EditorInfo.TYPE_TEXT_VARIATION_URI }
                                AlertDialog.Builder(this).setTitle(R.string.jf_manual).setView(LinearLayout(this).apply { setPadding(dp(20), dp(8), dp(20), 0); addView(url, LinearLayout.LayoutParams(-1, -2)) })
                                    .setPositiveButton(android.R.string.ok) { _, _ ->
                                        var u = url.text.toString().trim().trimEnd('/')
                                        if (u.isNotEmpty() && !u.startsWith("http")) u = "http://$u"
                                        if (!u.contains(Regex(":\\d+$")) && u.count { it == ':' } == 1) u += ":8096"
                                        jellyfinUser(u, done)
                                    }.setNegativeButton(android.R.string.cancel, null).show()
                            }
                            else -> { Jelly.save(passwords, null); toast(getString(R.string.jf_forgotten)) }
                        }
                    }.setNegativeButton(android.R.string.cancel, null).show()
            }
        }.start()
    }

    private fun jellyfinUser(url: String, done: () -> Unit) {
        Thread {
            val users = runCatching { Jelly.publicUsers(url) }.getOrNull()
            runOnUiThread {
                if (users == null) { toast(getString(R.string.jf_error)); return@runOnUiThread }
                fun ask(name: String) {
                    val pass = EditText(this).apply { setSingleLine(); hint = getString(R.string.jf_password); inputType = EditorInfo.TYPE_CLASS_TEXT or EditorInfo.TYPE_TEXT_VARIATION_PASSWORD }
                    AlertDialog.Builder(this).setTitle(name).setView(LinearLayout(this).apply { setPadding(dp(20), dp(8), dp(20), 0); addView(pass, LinearLayout.LayoutParams(-1, -2)) })
                        .setPositiveButton(R.string.jf_login) { _, _ ->
                            val pw = pass.text.toString()
                            Thread {
                                val c = runCatching { Jelly.login(url, name, pw, LanSync.MODEL) }.getOrNull()
                                runOnUiThread {
                                    if (c == null) toast(getString(R.string.jf_bad_login))
                                    else { Jelly.save(passwords, c); toast(getString(R.string.jf_logged, name)); done() }
                                }
                            }.start()
                        }.setNegativeButton(android.R.string.cancel, null).show()
                }
                // użytkownicy ukryci na ekranie logowania - wpisz nazwę
                val labels = users.map { "👤 " + it.second } + getString(R.string.jf_other_user)
                AlertDialog.Builder(this).setTitle(R.string.jf_user).setItems(labels.toTypedArray()) { _, i ->
                    if (i < users.size) ask(users[i].second)
                    else {
                        val n = EditText(this).apply { setSingleLine(); hint = getString(R.string.jf_user) }
                        AlertDialog.Builder(this).setTitle(R.string.jf_other_user).setView(LinearLayout(this).apply { setPadding(dp(20), dp(8), dp(20), 0); addView(n, LinearLayout.LayoutParams(-1, -2)) })
                            .setPositiveButton(android.R.string.ok) { _, _ -> n.text.toString().trim().takeIf { it.isNotEmpty() }?.let { ask(it) } }
                            .setNegativeButton(android.R.string.cancel, null).show()
                    }
                }.show()
            }
        }.start()
    }

    // ---------- 📁 Moje filmy (z telefonu, wybrane przez Ciebie - bez dostępu do całej pamięci) ----------
    private fun myFiles(): org.json.JSONArray = runCatching { org.json.JSONArray(prefs.getString("moje-filmy", "[]")) }.getOrDefault(org.json.JSONArray())

    // 🧭 Velivo jako domyślna przeglądarka - systemowe pytanie Androida (tylko gdy ktoś chce)
    private fun askDefaultBrowser() {
        val rm = getSystemService(android.app.role.RoleManager::class.java)
        if (rm != null && rm.isRoleAvailable(android.app.role.RoleManager.ROLE_BROWSER)) {
            if (rm.isRoleHeld(android.app.role.RoleManager.ROLE_BROWSER)) { toast(getString(R.string.default_browser_done)); return }
            runCatching { startActivityForResult(rm.createRequestRoleIntent(android.app.role.RoleManager.ROLE_BROWSER), REQ_ROLE); return }
        }
        runCatching { startActivity(Intent(android.provider.Settings.ACTION_MANAGE_DEFAULT_APPS_SETTINGS)) }
    }

    // 📱 wszystkie filmy i muzyka z telefonu (aparat, pobrane, WhatsApp…) - A–Z, najnowsze, długość
    private fun showPhoneVideos() {
        val perms = if (Build.VERSION.SDK_INT >= 33) arrayOf(android.Manifest.permission.READ_MEDIA_VIDEO, android.Manifest.permission.READ_MEDIA_AUDIO) else arrayOf(android.Manifest.permission.READ_EXTERNAL_STORAGE)
        if (perms.any { checkSelfPermission(it) != android.content.pm.PackageManager.PERMISSION_GRANTED } && afterVideoPerm == null) { afterVideoPerm = { showPhoneVideos() }; requestPermissions(perms, REQ_VIDEOS); return }
        // widok: 0 wszystko, 1 filmy, 2 moja muzyka, 3 dźwięki wbudowane (dzwonki, powiadomienia, alarmy) - zapamiętany; alfabetycznie
        var kind = prefs.getInt("telefon-rodzaj", 0)
        var all = listOf<MRow>()
        fun type(r: MRow) = if (r.title.startsWith("🎵")) 2 else if (r.title.startsWith("🔔")) 3 else 1
        fun name(r: MRow) = r.title.removePrefix("🎵 ").removePrefix("🔔 ").lowercase()
        fun shown() = all.filter { kind == 0 || type(it) == kind }
        val kinds = listOf(getString(R.string.phone_all), "🎬 " + getString(R.string.phone_only_video), "🎵 " + getString(R.string.phone_only_audio), "🔔 " + getString(R.string.phone_builtin))
        lateinit var scr: MediaScreen
        scr = MediaScreen("📱 " + getString(R.string.phone_videos), "telefon", listOf(getString(R.string.sort_az), getString(R.string.sort_newest)),
            sorter = { l, s -> if (s == 0) l.sortedWith(compareBy({ type(it) }, { name(it) })) else l.sortedByDescending { (it.tag as Pair<*, *>).second as Long } },
            onOpen = { r, shownRows -> play(shownRows.map { val u = (it.tag as Pair<*, *>).first.toString(); PlayerActivity.Item(it.title.removePrefix("🎵 ").removePrefix("🔔 "), u, "plik-" + u.hashCode()) }, shownRows.indexOf(r), false) })
        fun info() = kinds[kind] + " · " + all.count { type(it) == 1 } + " 🎬 · " + all.count { type(it) == 2 } + " 🎵 · " + all.count { type(it) == 3 } + " 🔔"
        scr.addButton("☰", R.string.tv_filter) {
            AlertDialog.Builder(this).setSingleChoiceItems(kinds.toTypedArray(), kind) { d, i -> kind = i; prefs.edit().putInt("telefon-rodzaj", i).apply(); d.dismiss(); scr.setRows(shown(), info()) }.show()
        }
        scr.show()
        scr.setRows(emptyList(), getString(R.string.tv_loading))
        Thread {
            val rows = ArrayList<MRow>()
            // cała pamięć telefonu: filmy, muzyka i dźwięki wbudowane (także systemowe z pamięci wewnętrznej)
            val ext = android.provider.MediaStore.Audio.Media.EXTERNAL_CONTENT_URI; val int = android.provider.MediaStore.Audio.Media.INTERNAL_CONTENT_URI
            for (col in listOf(android.provider.MediaStore.Video.Media.EXTERNAL_CONTENT_URI, ext, int)) runCatching {
                val isAudio = col != android.provider.MediaStore.Video.Media.EXTERNAL_CONTENT_URI
                val cols = arrayOf(android.provider.MediaStore.MediaColumns._ID, android.provider.MediaStore.MediaColumns.DISPLAY_NAME, android.provider.MediaStore.MediaColumns.DURATION,
                    android.provider.MediaStore.MediaColumns.DATE_ADDED, android.provider.MediaStore.MediaColumns.BUCKET_DISPLAY_NAME) +
                    (if (isAudio) arrayOf(android.provider.MediaStore.Audio.AudioColumns.IS_RINGTONE, android.provider.MediaStore.Audio.AudioColumns.IS_NOTIFICATION, android.provider.MediaStore.Audio.AudioColumns.IS_ALARM) else emptyArray())
                contentResolver.query(col, cols, null, null, null)?.use { c ->
                    while (c.moveToNext()) {
                        val u = android.content.ContentUris.withAppendedId(col, c.getLong(0))
                        // wbudowane: z pamięci systemowej albo oznaczone jako dzwonek / powiadomienie / alarm
                        val builtin = isAudio && (col == int || c.getInt(5) != 0 || c.getInt(6) != 0 || c.getInt(7) != 0)
                        val icon = if (!isAudio) "" else if (builtin) "🔔 " else "🎵 "
                        val sec = c.getLong(2) / 1000; val min = sec / 60
                        val len = if (min >= 60) "${min / 60} h ${min % 60} min" else if (min > 0) "$min min" else if (sec > 0) "$sec s" else null
                        rows.add(MRow(icon + (c.getString(1) ?: "?"), listOfNotNull(c.getString(4), len).joinToString(" · "), null, tag = u to c.getLong(3)))
                    }
                }
            }
            runOnUiThread { all = rows; scr.setRows(shown(), if (rows.isEmpty()) getString(R.string.my_films_empty) else info()) }
        }.start()
    }

    private var afterVideoPerm: (() -> Unit)? = null

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQ_VIDEOS && grantResults.any { it == android.content.pm.PackageManager.PERMISSION_GRANTED }) afterVideoPerm?.invoke()   // choć filmy albo choć muzyka
        afterVideoPerm = null
    }

    private fun showMyFiles() {
        lateinit var scr: MediaScreen
        fun rows(): List<MRow> { val a = myFiles(); return (0 until a.length()).map { a.getJSONObject(it) }.map { MRow(it.optString("n"), java.text.DateFormat.getDateInstance().format(java.util.Date(it.optLong("t"))), null, tag = it) } }
        scr = MediaScreen("🎞 " + getString(R.string.my_films), "moje2", listOf(getString(R.string.sort_az), getString(R.string.sort_newest)),   // domyślnie alfabetycznie
            sorter = { l, s -> if (s == 0) l.sortedBy { it.title.lowercase() } else l.sortedByDescending { (it.tag as org.json.JSONObject).optLong("t") } },
            onOpen = { r, shownRows -> val o = shownRows.map { it.tag as org.json.JSONObject }
                play(o.map { PlayerActivity.Item(it.optString("n"), it.optString("u"), "moje-" + it.optString("u").hashCode()) }, shownRows.indexOf(r), false) },
            onLong = { r -> AlertDialog.Builder(this).setMessage(getString(R.string.my_films_remove, r.title)).setPositiveButton(R.string.delete) { _, _ ->
                val a = myFiles(); val n = org.json.JSONArray(); for (i in 0 until a.length()) if (a.getJSONObject(i).optString("u") != (r.tag as org.json.JSONObject).optString("u")) n.put(a.getJSONObject(i))
                prefs.edit().putString("moje-filmy", n.toString()).apply(); scr.setRows(rows()) }.setNegativeButton(android.R.string.cancel, null).show() })
        scr.addButton("＋", R.string.my_films_add) {
            pickDocuments(arrayOf("video/*", "audio/*"), true) { uris ->
                val a = myFiles()
                for (u in uris) {
                    runCatching { contentResolver.takePersistableUriPermission(u, Intent.FLAG_GRANT_READ_URI_PERMISSION) }
                    val name = runCatching { contentResolver.query(u, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)?.use { if (it.moveToFirst()) it.getString(0) else null } }.getOrNull() ?: u.lastPathSegment ?: "?"
                    a.put(org.json.JSONObject().put("u", u.toString()).put("n", name).put("t", System.currentTimeMillis()))
                }
                prefs.edit().putString("moje-filmy", a.toString()).apply(); scr.setRows(rows())
            }
        }
        scr.show()
        scr.setRows(rows(), if (myFiles().length() == 0) getString(R.string.my_films_empty) else "")
    }

    private var pickCallback: ((List<Uri>) -> Unit)? = null
    private fun pickDocuments(types: Array<String>, multi: Boolean, done: (List<Uri>) -> Unit) {
        pickCallback = done
        startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*")
            .putExtra(Intent.EXTRA_MIME_TYPES, types).putExtra(Intent.EXTRA_ALLOW_MULTIPLE, multi), REQ_MEDIA)
    }

    // ustawienia z Velivo na komputerze (Jellyfin, listy TV) - zawsze z pytaniem
    // code: Filmy i TV bez parowania - ten sam kod musi być na komputerze; Jellyfin bez klucza - logowanie swoim użytkownikiem
    private fun receiveMediaConf(dev: String, jf: org.json.JSONObject?, tv: org.json.JSONArray?, code: String? = null) {
        val n = tv?.length() ?: 0
        val text = getString(R.string.media_conf_ask, dev, if (jf != null) "Jellyfin" else "—", n) + if (code != null) "\n\n" + getString(R.string.media_share_code, code) else ""
        AlertDialog.Builder(this).setTitle(R.string.media_conf_title).setMessage(text)
            .setPositiveButton(R.string.file_accept) { _, _ ->
                val jfKey = jf?.optString("key").orEmpty()
                if (jf != null && jfKey.isNotEmpty()) Jelly.save(passwords, Jelly.Conf(jf.optString("url"), jfKey, ""))
                if (tv != null) {
                    val list = Tv.sources(passwords)
                    for (i in 0 until tv.length()) { val o = tv.getJSONObject(i); val u = o.optString("url"); val m3u = o.optString("m3u")
                        if (u.startsWith("velivo-pc:") && m3u.isNotEmpty()) {   // lista z pliku na komputerze - treść zapisana zaszyfrowana
                            val src = list.firstOrNull { it.url == u } ?: Tv.Source(java.util.UUID.randomUUID().toString().replace("-", "").take(12), o.optString("name").ifEmpty { "TV" }, u, o.optString("epg")).also { list.add(it) }
                            Tv.importText(this, passwords, src, m3u)
                        } else if (list.none { it.url == u } && (u.startsWith("http://") || u.startsWith("https://")))
                            list.add(Tv.Source(java.util.UUID.randomUUID().toString().replace("-", "").take(12), o.optString("name").ifEmpty { "TV" }, u, o.optString("epg"))) }
                    Tv.saveSources(passwords, list)
                }
                toast(getString(R.string.media_conf_saved))
                if (jf != null) { if (jfKey.isNotEmpty()) setupJellyfinUser() else jellyfinUser(jf.optString("url").trimEnd('/')) {} }
            }.setNegativeButton(android.R.string.cancel, null).show()
    }

    private fun setupJellyfinUser() {
        val c = Jelly.conf(passwords) ?: return
        Thread {
            val users = runCatching { Jelly.users(c) }.getOrNull()
            if (users.isNullOrEmpty()) { runOnUiThread { toast(getString(R.string.jf_error)) }; return@Thread }
            if (users.size == 1) { Jelly.save(passwords, Jelly.Conf(c.url, c.key, users[0].first)); return@Thread }   // jeden użytkownik - bez pytania
            runOnUiThread { AlertDialog.Builder(this).setTitle(R.string.jf_user).setItems(users.map { it.second }.toTypedArray()) { _, i -> Jelly.save(passwords, Jelly.Conf(c.url, c.key, users[i].first)) }.show() }
        }.start()
    }

    // --- Karta między telefonem a komputerem (z miejscem w filmie, jak 📺 Wyślij do… na Windows) ---
    // --- 🗒 Karteczki (jak na Windows): termin w treści, powtarzanie, przypomnienie dzień wcześniej i w dniu terminu ---
    private fun repeatName(r: String?): String = when {
        r.isNullOrEmpty() -> getString(R.string.rep_once)
        r == "7d" -> getString(R.string.rep_week); r == "14d" -> getString(R.string.rep_2weeks)
        r == "1m" -> getString(R.string.rep_month); r == "1y" -> getString(R.string.rep_year)
        r.endsWith("d") -> getString(R.string.rep_days, r.dropLast(1)); r.endsWith("m") -> getString(R.string.rep_months, r.dropLast(1))
        else -> getString(R.string.rep_years, r.dropLast(1))
    }

    private fun noteInfo(n: Karteczki.Note): String {
        val today = java.time.LocalDate.now()
        val d = Karteczki.due(n.text, today, n.repeat)
        val parts = mutableListOf<String>()
        if (d == 2) parts.add("⏰ " + getString(R.string.note_due_today)) else if (d == 1) parts.add("⏰ " + getString(R.string.note_due_tomorrow))
        else Karteczki.next(n.text, today, n.repeat)?.let { parts.add("📅 " + it.format(java.time.format.DateTimeFormatter.ofPattern("dd.MM.yyyy"))) }
        if (!n.repeat.isNullOrEmpty()) parts.add("🔁 " + repeatName(n.repeat))
        return parts.joinToString(" · ")
    }

    private fun newNote() = newNoteThen {}

    private fun showNotes(openId: String? = null) {
        val dlg = android.app.Dialog(this, android.R.style.Theme_DeviceDefault_NoActionBar)
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(c("#F7F4FF", "#15121F")) }
        val bar = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(8), dp(8), dp(8), dp(8)); setBackgroundColor(BAR) }
        bar.addView(TextView(this).apply { text = "🗒 " + getString(R.string.notes); textSize = 18f; setTextColor(TEXT); setPadding(dp(8), 0, 0, 0) }, LinearLayout.LayoutParams(0, -2, 1f))
        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(10), dp(10), dp(10), dp(24)) }
        fun fill() {
            col.removeAllViews()
            val today = java.time.LocalDate.now()
            // najbliższy termin na górze, karteczki bez terminu na końcu
            val list = Karteczki.load(this).sortedBy { Karteczki.next(it.text, today, it.repeat)?.toEpochDay() ?: Long.MAX_VALUE }
            if (list.isEmpty()) col.addView(TextView(this).apply { text = getString(R.string.notes_empty); setTextColor(TEXT); textSize = 15f; setPadding(dp(8), dp(24), dp(8), 0); gravity = Gravity.CENTER })
            for (n in list) {
                val card = LinearLayout(this).apply {
                    orientation = LinearLayout.VERTICAL; setPadding(dp(14), dp(10), dp(14), dp(12))
                    background = GradientDrawable().apply { setColor(Color.parseColor("#" + n.color)); cornerRadius = dp(14).toFloat() }
                    isClickable = true
                    foreground = android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(Color.argb(50, 0, 0, 0)), null, null)
                }
                card.addView(TextView(this).apply { text = Karteczki.icon(n.kind) + "  " + getString(Karteczki.title(n.kind)); textSize = 13f; setTextColor(Color.parseColor("#333333")); setTypeface(typeface, android.graphics.Typeface.BOLD) })
                card.addView(TextView(this).apply { text = n.text.trim().ifEmpty { "…" }; textSize = 16f; setTextColor(Color.parseColor("#222222")); maxLines = 5; ellipsize = TextUtils.TruncateAt.END; setPadding(0, dp(4), 0, 0) })
                val info = noteInfo(n)
                if (info.isNotEmpty()) card.addView(TextView(this).apply { text = info; textSize = 12f; setTextColor(Color.parseColor(if (info.startsWith("⏰")) "#B91C1C" else "#555555")); setPadding(0, dp(6), 0, 0) })
                card.setOnClickListener { editNote(n) { fill() } }
                col.addView(card, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(10) })
            }
            applyScale(col)
        }
        bar.addView(button("＋", R.string.note_new, Color.WHITE, BRAND1) { newNoteThen { fill() } }.velivo())
        bar.addView(button("✕", R.string.close_tab_short, TEXT, GRAY_BG) { dlg.dismiss() })
        root.addView(bar)
        root.addView(ScrollView(this).apply { addView(col) }, LinearLayout.LayoutParams(-1, 0, 1f))
        fill()
        dlg.setContentView(PinchFrame(root) { dlg.dismiss() })
        dlg.show()
        openId?.let { id -> Karteczki.load(this).firstOrNull { it.id == id }?.let { editNote(it) { fill() } } }
    }

    private fun newNoteThen(done: () -> Unit) {
        val labels = Karteczki.kinds.map { it.second + "  " + getString(it.third) }.toTypedArray()
        AlertDialog.Builder(this).setTitle(R.string.note_new).setItems(labels) { _, i ->
            val k = Karteczki.kinds[i].first
            val list = Karteczki.load(this)
            val n = Karteczki.Note(java.util.UUID.randomUUID().toString().replace("-", ""), k, Karteczki.startText(k), Karteczki.colors[0], "")
            list.add(n); Karteczki.save(this, list)
            editNote(n, done)
        }.show()
    }

    private fun editNote(n: Karteczki.Note, done: () -> Unit) {
        val dlg = android.app.Dialog(this, android.R.style.Theme_DeviceDefault_Light_NoActionBar)
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(Color.parseColor("#" + n.color)) }
        val head = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(6), dp(6), dp(6), dp(6)); setBackgroundColor(Color.argb(28, 0, 0, 0)) }
        val dark = Color.parseColor("#333333"); val clear = Color.TRANSPARENT
        head.addView(TextView(this).apply { text = Karteczki.icon(n.kind) + " " + getString(Karteczki.title(n.kind)); textSize = 16f; setTextColor(dark); setTypeface(typeface, android.graphics.Typeface.BOLD); setPadding(dp(8), 0, 0, 0) }, LinearLayout.LayoutParams(0, -2, 1f))
        val box = EditText(this).apply {
            setText(n.text); textSize = 17f; setTextColor(Color.parseColor("#222222")); gravity = Gravity.TOP
            background = null; setPadding(dp(16), dp(12), dp(16), dp(12))
            inputType = android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE or android.text.InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
        }
        val info = TextView(this).apply { textSize = 13f; setTextColor(Color.parseColor("#555555")); setPadding(dp(16), dp(6), dp(16), dp(12)) }
        fun upd() { n.text = box.text.toString(); info.text = noteInfo(n).ifEmpty { getString(R.string.note_hint) } }
        box.addTextChangedListener(object : TextWatcher {
            override fun afterTextChanged(e: Editable?) = upd()
            override fun beforeTextChanged(a: CharSequence?, b: Int, c: Int, d: Int) {}
            override fun onTextChanged(a: CharSequence?, b: Int, c: Int, d: Int) {}
        })
        fun store() {
            n.text = box.text.toString()
            val list = Karteczki.load(this)
            val i = list.indexOfFirst { it.id == n.id }
            if (i >= 0) list[i] = n else list.add(n)
            Karteczki.save(this, list)
        }
        // 📅 ustal termin: data z kalendarza wstawiana w miejscu kursora (jak na Windows)
        head.addView(button("📅", R.string.note_date, dark, clear) {
            val t = java.time.LocalDate.now()
            android.app.DatePickerDialog(this, { _, y, m, d ->
                val v = String.format("%02d.%02d.%04d", d, m + 1, y)
                val at = box.selectionStart.coerceAtLeast(0)
                box.text.insert(at, if (at > 0 && !box.text[at - 1].isWhitespace()) " $v" else v)
            }, t.year, t.monthValue - 1, t.dayOfMonth).show()
        })
        head.addView(button("🔁", R.string.note_repeat, dark, clear) {
            val opts = listOf("", "7d", "14d", "1m", "1y")
            val labels = (opts.map { repeatName(it) } + getString(R.string.rep_own)).toTypedArray()
            AlertDialog.Builder(this).setTitle(R.string.note_repeat).setSingleChoiceItems(labels, opts.indexOf(n.repeat)) { d, i ->
                d.dismiss()
                if (i < opts.size) { n.repeat = opts[i]; upd(); return@setSingleChoiceItems }
                // własny odstęp: co N dni / tygodni / miesięcy / lat
                val num = EditText(this).apply { setText("10"); inputType = android.text.InputType.TYPE_CLASS_NUMBER; setSelectAllOnFocus(true) }
                val units = arrayOf(getString(R.string.unit_days), getString(R.string.unit_weeks), getString(R.string.unit_months), getString(R.string.unit_years))
                var u = 0
                val lay = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20), dp(8), dp(20), 0); addView(num) }
                AlertDialog.Builder(this).setTitle(R.string.rep_own).setView(lay)
                    .setSingleChoiceItems(units, 0) { _, k -> u = k }
                    .setPositiveButton(android.R.string.ok) { _, _ ->
                        val v = num.text.toString().toIntOrNull()?.takeIf { it in 1..999 } ?: return@setPositiveButton
                        n.repeat = when (u) { 1 -> "${v * 7}d"; 2 -> "${v}m"; 3 -> "${v}y"; else -> "${v}d" }
                        upd()
                    }.setNegativeButton(android.R.string.cancel, null).show()
            }.show()
        })
        head.addView(button("🎨", R.string.note_color, dark, clear) {
            val i = (Karteczki.colors.indexOf(n.color) + 1) % Karteczki.colors.size
            n.color = Karteczki.colors[i]; root.setBackgroundColor(Color.parseColor("#" + n.color))
        })
        head.addView(button("🗑", R.string.note_delete, dark, clear) {
            AlertDialog.Builder(this).setMessage(R.string.note_delete_ask)
                .setPositiveButton(R.string.note_delete) { _, _ ->
                    Karteczki.save(this, Karteczki.load(this).filter { it.id != n.id })
                    dlg.setOnDismissListener { done() }; dlg.dismiss()
                }.setNegativeButton(android.R.string.cancel, null).show()
        })
        head.addView(button("✓", R.string.note_done, dark, clear) { dlg.dismiss() })
        dlg.setOnDismissListener { store(); done() }
        root.addView(head)
        root.addView(ScrollView(this).apply { isFillViewport = true; addView(box) }, LinearLayout.LayoutParams(-1, 0, 1f))
        root.addView(info)
        upd()
        dlg.setContentView(PinchFrame(root))
        dlg.show()
        box.requestFocus()
        box.setSelection(Karteczki.startText(n.kind).indexOf('\n').takeIf { it > 0 && n.text == Karteczki.startText(n.kind) } ?: box.text.length)
    }

    private fun sendTabToComputer() {
        val w = web() ?: return
        val url = w.url ?: return
        if (tabs.getOrNull(cur)?.let { it.priv || it.bank } == true) { toast(getString(R.string.send_tab_private)); return }
        if (!(url.startsWith("http://") || url.startsWith("https://"))) { toast(getString(R.string.send_tab_bad)); return }
        if (!prefs.getBoolean("lan", false) || !lan.paired) { toast(getString(R.string.lan_not_paired)); return }
        w.evaluateJavascript("(function(){var v=[].slice.call(document.querySelectorAll('video')).filter(function(x){return x.currentTime>0}).sort(function(a,b){return b.clientWidth*b.clientHeight-a.clientWidth*a.clientHeight})[0];return v?v.currentTime:0})()") { r ->
            val time = r.toDoubleOrNull() ?: 0.0
            if (lan.sendTab(url, w.title ?: url, time)) toast(getString(R.string.send_tab_done, prefs.getString("lan-komputer", "") ?: ""))
            else toast(getString(R.string.send_tab_wait))
        }
    }

    private fun receiveTab(dev: String, url: String, title: String, time: Double) {
        var u = url
        // YouTube: czas w adresie działa najpewniej
        if (time > 5 && (u.contains("youtube.com/watch") || u.contains("youtu.be/"))) {
            u = u.replace(Regex("([?&])t=[^&]*&?"), "$1").trimEnd('&', '?')
            u += (if (u.contains("?")) "&" else "?") + "t=" + time.toInt() + "s"
        }
        newTab(false, u)
        if (time > 5) web()?.let { w -> w.postDelayed({ w.evaluateJavascript("(function(t){var v=[].slice.call(document.querySelectorAll('video')).sort(function(a,b){return b.clientWidth*b.clientHeight-a.clientWidth*a.clientHeight})[0];if(v&&Math.abs(v.currentTime-t)>5)v.currentTime=t})($time)", null) }, 4000) }
        toast(getString(R.string.tab_received, dev, title.ifBlank { u }))
    }

    private val lan: LanSync by lazy {
        LanSync(
            lanKey = { passwords.lanKey },
            saveKey = { passwords.lanKey = it },
            profile = { prefs.getString("lan-profil", "domyslny")!! },
            deviceName = { prefs.getString("lan-nazwa", null)?.ifBlank { null } ?: LanSync.MODEL },
            saveProfile = { prefs.edit().putString("lan-profil", it.ifBlank { "domyslny" }).apply() },
            ui = { runOnUiThread(it) },
            confirmPair = { dev, code, accept ->
                AlertDialog.Builder(this)
                    .setTitle(R.string.lan_pair_confirm)
                    .setMessage(getString(R.string.lan_pair_code, dev, code))
                    .setPositiveButton(R.string.lan_codes_match) { _, _ -> accept() }
                    .setNegativeButton(android.R.string.cancel, null)
                    .show()
            },
            onPaired = { dev -> lan.pairingUntil = 0; prefs.edit().putString("lan-komputer", dev).apply(); toast(getString(R.string.lan_paired, dev)) },
            onState = { dev, bookmarks, pwds -> applyLanState(dev, bookmarks, pwds) },
            askFile = { dev, name, size, accept ->
                AlertDialog.Builder(this)
                    .setTitle(R.string.file_title)
                    .setMessage(getString(R.string.file_ask, dev, name, android.text.format.Formatter.formatShortFileSize(this, size.toLong())))
                    .setPositiveButton(R.string.file_accept) { _, _ -> accept(true) }
                    .setNegativeButton(android.R.string.cancel) { _, _ -> accept(false) }
                    .setOnCancelListener { accept(false) }
                    .show()
            },
            saveFile = { name, data, dev ->
                if (name.startsWith("Velivo-TV-") && name.endsWith(".m3u")) {   // duża lista TV z 📱 Wyślij Filmy i TV na Windows - prosto do Telewizji
                    val u = "velivo-pc:" + name; val list = Tv.sources(passwords)
                    val src = list.firstOrNull { it.url == u } ?: Tv.Source(java.util.UUID.randomUUID().toString().replace("-", "").take(12), name.removePrefix("Velivo-TV-").removeSuffix(".m3u"), u, "").also { list.add(it) }
                    Tv.importText(this, passwords, src, String(data, Charsets.UTF_8)); Tv.saveSources(passwords, list)
                    runOnUiThread { toast(getString(R.string.media_conf_saved)) }
                } else saveToDownloads(name, data, dev)
            },
            onTab = { dev, url, title, time -> receiveTab(dev, url, title, time) },
            onMedia = { dev, jf, tv -> receiveMediaConf(dev, jf, tv) },
            onMediaShared = { dev, code, jf, tv -> receiveMediaConf(dev, jf, tv, code) },
            onQuickAccess = { dev, zip ->
                runCatching { quickAccess.importFromComputer(zip) }.onSuccess {
                    runOnUiThread {
                        toast(getString(R.string.qa_received, dev))
                        tabs.filter { QuickAccess.isQuickAccess(it.web.url) }.forEach { it.web.reload() }
                    }
                }
            },
        )
    }
    private var multicastLock: WifiManager.MulticastLock? = null

    // force: na chwilę także bez włączonej synchronizacji (prośba o Filmy i TV z telefonu bez parowania)
    private fun startLan(force: Boolean = false) {
        if (!force && !prefs.getBoolean("lan", false)) return
        if (multicastLock == null) multicastLock = (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager)
            .createMulticastLock("velivo-lan").apply { setReferenceCounted(false) }
        multicastLock?.acquire()
        lan.start()
    }

    private fun stopLan() {
        lan.stop()
        multicastLock?.release()
    }

    // Plik z komputera do Pobranych (MediaStore - bez dodatkowych uprawnień).
    private fun saveToDownloads(name: String, data: ByteArray, dev: String) {
        val values = android.content.ContentValues().apply {
            put(android.provider.MediaStore.Downloads.DISPLAY_NAME, name)
            put(android.provider.MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS)
        }
        val ok = runCatching {
            val uri = contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)!!
            contentResolver.openOutputStream(uri)!!.use { it.write(data) }
        }.isSuccess
        runOnUiThread { toast(getString(if (ok) R.string.file_saved else R.string.file_failed, name, dev)) }
    }

    // Udostępnij → Velivo: plik z galerii / menedżera plików idzie do komputera (komputer pyta o zgodę).
    private fun sendSharedFile(intent: Intent) {
        @Suppress("DEPRECATION")
        val uri = (intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)) ?: return
        if (!lan.paired) { toast(getString(R.string.lan_not_paired)); return }
        prefs.edit().putBoolean("lan", true).apply()
        startLan()
        val name = runCatching {
            contentResolver.query(uri, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c -> if (c.moveToFirst()) c.getString(0) else null }
        }.getOrNull() ?: uri.lastPathSegment ?: "plik"
        toast(getString(R.string.file_waiting, name))
        Thread {
            // komputer musi się najpierw odezwać w sieci (adres) - czekamy do 30 s
            var waited = 0
            while (lan.computer == null && waited < 30) { Thread.sleep(1000); waited++ }
            val result = runCatching {
                val size = contentResolver.openAssetFileDescriptor(uri, "r")?.use { it.length } ?: -1L
                if (size > LanSync.FILE_LIMIT) "plik większy niż 100 MB"
                else lan.sendFile(name, contentResolver.openInputStream(uri)!!.use { it.readBytes() })
            }.getOrElse { it.message ?: "błąd" }
            runOnUiThread {
                toast(when (result) {
                    "ok" -> getString(R.string.file_sent, name)
                    "odrzucono" -> getString(R.string.file_declined)
                    else -> getString(R.string.file_send_failed, result)
                })
            }
        }.start()
    }

    // Zakładki (Windows: "adres<TAB>tytuł") i hasła z komputera dopisywane do telefonu; nic nie jest usuwane.
    private fun applyLanState(dev: String, bookmarks: String?, pwds: String?) {
        var added = 0
        bookmarks?.lines()?.reversed()?.forEach { line ->
            val parts = line.split('\t')
            val url = parts[0].trim()
            if (!url.startsWith("http") || list("zakladki").any { it.endsWith("\t" + url) }) return@forEach
            addTo("zakladki", parts.getOrNull(1)?.ifBlank { null } ?: url, url, 2000)
            added++
        }
        val pcPasswords = pwds?.let { LanSync.parsePasswords(it) } ?: emptyList()
        val pw = passwords.saveAll(pcPasswords)
        prefs.edit().putString("lan-komputer", dev).putLong("lan-czas", System.currentTimeMillis()).apply()
        if (added + pw > 0) toast(getString(R.string.lan_received, dev, added, pw))
        // co komputer już ma - tego telefon nie wysyła
        if (bookmarks != null && pwds != null) {
            val pcUrls = bookmarks.lines().map { it.split('\t')[0].trim() }.filter { it.startsWith("http") }.toSet()
            sendNewToComputer(pcUrls, pcPasswords.map { secretKey(it.first, it.second, it.third) }.toSet())
        }
    }

    // Telefon → komputer: tylko nowe zakładki i hasła, każde raz (komputer potwierdza; odrzucone nie wracają).
    private fun secretKey(host: String, user: String, pass: String) =
        java.security.MessageDigest.getInstance("SHA-256").digest("$host\t$user\t$pass".toByteArray()).joinToString("") { "%02x".format(it) }

    private fun sendNewToComputer(pcUrls: Set<String>, pcSecrets: Set<String>) {
        val sent = prefs.getStringSet("wyslane-na-komputer", emptySet())!!
        val newBookmarks = list("zakladki").map { it.substringAfter('\t') to it.substringBefore('\t') }
            .filter { (url, _) -> url.startsWith("http") && url !in pcUrls && "z:$url" !in sent }
        val newPasswords = passwords.all().map { Triple(it.host, it.user, it.pass) }
            .filter { val k = secretKey(it.first, it.second, it.third); k !in pcSecrets && "h:$k" !in sent }
        if (newBookmarks.isEmpty() && newPasswords.isEmpty()) return
        if (!lan.sendPhoneAdd(newBookmarks, newPasswords)) return
        prefs.edit().putStringSet("wyslane-na-komputer", sent + newBookmarks.map { "z:" + it.first } +
            newPasswords.map { "h:" + secretKey(it.first, it.second, it.third) }).apply()
        toast(getString(R.string.lan_sent_to_pc, newBookmarks.size, newPasswords.size))
    }

    private fun showLanSync() {
        val on = prefs.getBoolean("lan", false)
        val pc = prefs.getString("lan-komputer", null)
        val last = prefs.getLong("lan-czas", 0)
        val status = when {
            !lan.paired -> getString(R.string.lan_not_paired)
            last > 0 -> getString(R.string.lan_status, pc ?: "?", java.text.DateFormat.getDateTimeInstance().format(java.util.Date(last)))
            else -> getString(R.string.lan_waiting)
        }
        val qa = listOfNotNull(
            lan.tcpError?.let { getString(R.string.lan_tcp_error, it) },
            lan.qaStep.takeIf { it.isNotEmpty() }?.let { getString(R.string.quick_access) + ": " + it },
        ).joinToString("\n")
        val items = arrayOf(
            getString(if (on) R.string.lan_turn_off else R.string.lan_turn_on),
            getString(R.string.lan_pair),
            getString(R.string.lan_recovery),
            getString(R.string.lan_sync_now),
            getString(R.string.lan_forget),
            getString(R.string.lan_name) + ": " + (prefs.getString("lan-nazwa", null)?.ifBlank { null } ?: LanSync.MODEL),
        )
        AlertDialog.Builder(this)
            .setTitle(getString(R.string.lan_sync) + "\n" + status + (if (qa.isEmpty()) "" else "\n" + qa))
            .setItems(items) { _, i ->
                when (i) {
                    0 -> { prefs.edit().putBoolean("lan", !on).apply(); if (on) stopLan() else startLan() }
                    1 -> {
                        prefs.edit().putBoolean("lan", true).apply()
                        startLan()
                        lan.startPairing()   // telefon wysyła zaproszenie do komputerów w sieci
                        showPairingWait()
                    }
                    2 -> startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*"), REQ_VLPAIR)
                    3 -> { if (!lan.paired) toast(getString(R.string.lan_not_paired)) else { startLan(); lan.syncNow() } }
                    5 -> {
                        val input = EditText(this).apply { setText(prefs.getString("lan-nazwa", "")); hint = LanSync.MODEL; setSingleLine() }
                        AlertDialog.Builder(this).setTitle(R.string.lan_name).setMessage(R.string.lan_name_help).setView(input)
                            .setPositiveButton(android.R.string.ok) { _, _ ->
                                prefs.edit().putString("lan-nazwa", input.text.toString().filter { !it.isISOControl() }.trim().take(40)).apply()
                            }
                            .setNegativeButton(android.R.string.cancel, null).show()
                    }
                    4 -> { passwords.lanKey = null; prefs.edit().remove("lan-komputer").remove("lan-czas").putBoolean("lan", false).apply(); stopLan() }
                }
            }
            .show()
    }

    // Okno oczekiwania na parowanie: trzyma Velivo na ekranie (synchronizacja działa tylko przy otwartej aplikacji)
    // i na żywo pokazuje, co telefon odbiera z sieci - od razu widać, czy pakiety z komputera w ogóle dochodzą.
    private fun showPairingWait() {
        lan.pairStep = ""
        val name = prefs.getString("lan-nazwa", null)?.ifBlank { null } ?: LanSync.MODEL
        val info = TextView(this).apply { setPadding(dp(20), dp(12), dp(20), 0); setTextColor(TEXT) }
        val live = TextView(this).apply { setPadding(dp(20), dp(12), dp(20), dp(8)); setTextColor(BLUE) }
        info.text = getString(R.string.lan_pair_help, name)
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; addView(info); addView(live) }
        val dlg = AlertDialog.Builder(this).setTitle(R.string.lan_pair).setView(ScrollView(this).apply { addView(box) })
            .setNegativeButton(R.string.close_tab_short) { _, _ -> lan.pairingUntil = 0 }
            .setOnDismissListener { window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) }
            .create()
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        val tick = object : Runnable {
            override fun run() {
                if (!dlg.isShowing) return
                val left = ((lan.pairingUntil - System.currentTimeMillis()) / 1000).coerceAtLeast(0)
                live.text = when {
                    lan.socketError != null -> getString(R.string.lan_socket_error, lan.socketError)
                    lan.rxCount == 0 -> getString(R.string.lan_rx_none, left)
                    else -> getString(R.string.lan_rx_some, lan.rxCount, lan.lastFrom, lan.lastType, left)
                }
                if (lan.pairStep.isNotEmpty()) live.append("\n\n" + lan.pairStep)
                if (left == 0L) { live.append("\n" + getString(R.string.lan_pair_timeout)); return }
                idle.postDelayed(this, 1000)
            }
        }
        dlg.show()
        idle.post(tick)
    }

    @Deprecated("Activity API")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == REQ_MEDIA) {
            val cb = pickCallback; pickCallback = null
            if (resultCode != RESULT_OK || data == null) return
            val uris = data.clipData?.let { cd -> (0 until cd.itemCount).map { cd.getItemAt(it).uri } } ?: listOfNotNull(data.data)
            cb?.invoke(uris); return
        }
        if (requestCode != REQ_VLPAIR || resultCode != RESULT_OK) return
        val uri = data?.data ?: return
        val text = runCatching {
            contentResolver.openInputStream(uri)!!.use { s -> String(s.readBytes().take(1024 * 1024).toByteArray(), Charsets.UTF_8) }
        }.getOrNull() ?: return
        val input = EditText(this).apply {
            inputType = EditorInfo.TYPE_CLASS_TEXT or EditorInfo.TYPE_TEXT_VARIATION_PASSWORD
            hint = getString(R.string.lan_recovery_pass)
        }
        AlertDialog.Builder(this)
            .setTitle(R.string.lan_recovery)
            .setView(input)
            .setPositiveButton(android.R.string.ok) { _, _ ->
                val pass = input.text.toString()
                toast(getString(R.string.lan_recovery_wait))
                Thread {
                    val key = LanSync.readRecovery(text, pass)
                    runOnUiThread {
                        if (key == null) { toast(getString(R.string.lan_recovery_bad)); return@runOnUiThread }
                        passwords.lanKey = key
                        prefs.edit().putBoolean("lan", true).apply()
                        stopLan(); startLan()
                        toast(getString(R.string.lan_recovery_ok))
                    }
                }.start()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    // --- Motyw ----------------------------------------------------------------
    private fun chooseTheme() {
        val names = arrayOf(getString(R.string.theme_system), getString(R.string.theme_light), getString(R.string.theme_dark), getString(R.string.theme_contrast))
        AlertDialog.Builder(this)
            .setTitle(R.string.theme)
            .setSingleChoiceItems(names, prefs.getInt("motyw", 2)) { d, i ->
                d.dismiss()
                if (i != prefs.getInt("motyw", 2)) { prefs.edit().putInt("motyw", i).apply(); saveTabs(); recreate() }
            }
            .show()
    }

    // --- Blokada odciskiem palca / PIN-em telefonu -----------------------------
    private fun unlock(onOk: () -> Unit) {
        if (!getSystemService(KeyguardManager::class.java).isDeviceSecure) { toast(getString(R.string.no_lock)); onOk(); return }
        val b = BiometricPrompt.Builder(this).setTitle(getString(R.string.unlock))
        if (Build.VERSION.SDK_INT >= 30) b.setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG or BiometricManager.Authenticators.DEVICE_CREDENTIAL)
        else setCredentialAllowed(b)
        b.build().authenticate(CancellationSignal(), mainExecutor, object : BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) = onOk()
        })
    }

    @Suppress("DEPRECATION")
    private fun setCredentialAllowed(b: BiometricPrompt.Builder) { b.setDeviceCredentialAllowed(true) }

    // --- Tryb bankowy ---------------------------------------------------------
    private var bankUnlocked = false
    private var bankUsed = 0L
    private val idle = Handler(Looper.getMainLooper())
    private val idleCheck = object : Runnable {
        override fun run() { lockBankIfIdle(); idle.postDelayed(this, 30_000) }
    }

    private fun openBank() {
        if (!privateSupported) { toast(getString(R.string.bank_unavailable)); return }
        lockBankIfIdle()
        val go = { bankUnlocked = true; bankUsed = System.currentTimeMillis(); newTab(false, home, bank = true) }
        if (bankUnlocked) go() else unlock(go)
    }

    // Po 10 min bezczynności karty bankowe się zamykają (logowania w profilu bankowym zostają).
    private fun lockBankIfIdle() {
        if (!bankUnlocked || System.currentTimeMillis() - bankUsed < BANK_IDLE_MS) return
        bankUnlocked = false
        if (tabs.none { it.bank }) return
        tabs.filter { it.bank }.forEach { closeTab(tabs.indexOf(it)) }
        toast(getString(R.string.bank_locked))
    }

    override fun dispatchTouchEvent(ev: MotionEvent): Boolean {
        if (tabs.getOrNull(cur)?.bank == true) bankUsed = System.currentTimeMillis()
        return super.dispatchTouchEvent(ev)
    }

    override fun onResume() {
        super.onResume()
        PlaybackService.stop(this)
        lockBankIfIdle()
        idle.postDelayed(idleCheck, 30_000)
        pipCheck.removeCallbacks(pipTick); pipCheck.post(pipTick)
    }

    // Gdy Velivo się zamknie przez błąd: opis zapisany lokalnie (bez adresów i treści stron - sam przebieg błędu),
    // przy następnym uruchomieniu okno z przyciskiem Kopiuj - do zgłoszenia.
    private fun catchCrashes() {
        val prev = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { t, e ->
            runCatching {
                java.io.File(filesDir, "blad.txt").writeText("Velivo ${packageManager.getPackageInfo(packageName, 0).versionName}, Android ${Build.VERSION.RELEASE}, ${Build.MODEL}\n" + android.util.Log.getStackTraceString(e).lines().take(40).joinToString("\n"))
            }
            prev?.uncaughtException(t, e)
        }
    }

    private fun showLastCrash() {
        val f = java.io.File(filesDir, "blad.txt")
        if (!f.exists()) return
        val text = runCatching { f.readText() }.getOrDefault("")
        f.delete()
        if (text.isBlank()) return
        AlertDialog.Builder(this).setTitle(R.string.crash_title)
            .setMessage(getString(R.string.crash_msg) + "\n\n" + text.lines().take(8).joinToString("\n"))
            .setPositiveButton(R.string.crash_copy) { _, _ ->
                (getSystemService(CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newPlainText("Velivo", text))
                toast(getString(R.string.crash_copied))
            }
            .setNegativeButton(R.string.close_tab_short, null).show()
    }

    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_SHORT).show()

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK && fullView != null) { exitFullscreen(); return true }
        val w = web()
        if (keyCode == KeyEvent.KEYCODE_BACK && w != null && w.canGoBack()) { stopSpeaking(); w.goBack(); return true }
        return super.onKeyDown(keyCode, event)
    }

    private val privateSupported by lazy { WebViewFeature.isFeatureSupported(WebViewFeature.MULTI_PROFILE) }

    private fun dropPrivateProfile() {
        if (privateSupported) runCatching { ProfileStore.getInstance().deleteProfile(PRIVATE_PROFILE) }
    }

    // Otwarte karty (bez prywatnych) wracają po ponownym uruchomieniu.
    private fun saveTabs() {
        val keep = tabs.filter { !it.quiet }
        val open = keep.mapNotNull { it.web.url }
        val sel = tabs.getOrNull(cur)?.let { keep.indexOf(it) }?.takeIf { it >= 0 } ?: 0
        prefs.edit().putString("karty", open.joinToString("\n")).putInt("karta", sel).apply()
    }

    private fun restoreTabs(): Boolean {
        val urls = prefs.getString("karty", "")!!.lines().filter { it.startsWith("https://") || it.startsWith("http://") }
        if (urls.isEmpty()) return false
        urls.forEach { newTab(false, it) }
        show(prefs.getInt("karta", 0).coerceIn(0, tabs.size - 1))
        return true
    }

    override fun onPause() {
        pipCheck.removeCallbacks(pipTick)
        stopLan()
        idle.removeCallbacks(idleCheck)
        backgroundPlayback()
        saveTabs()
        super.onPause()
    }

    override fun onDestroy() {
        tts?.shutdown()
        tabs.forEach { it.web.destroy() }
        if (isFinishing) dropPrivateProfile()
        super.onDestroy()
    }
}
