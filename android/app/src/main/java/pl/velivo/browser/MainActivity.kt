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
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.BaseAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ListView
import android.widget.PopupMenu
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

    override fun onCreate(savedInstanceState: Bundle?) {
        setTheme(if (dark) android.R.style.Theme_Material_NoActionBar else android.R.style.Theme_Material_Light_NoActionBar)
        super.onCreate(savedInstanceState)
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
            setHintTextColor(c("#6B7280", "#9CA3AF"))
            setPadding(dp(14), dp(8), dp(14), dp(8))
            background = round(FIELD)
            setOnEditorActionListener { _, _, _ -> go(text.toString()); true }
        }
        bar.addView(address, LinearLayout.LayoutParams(0, -2, 1f))
        tabsBtn = button("1", R.string.tabs, BLUE, BLUE_BG) { showTabs() }
        bar.addView(button("🕶", R.string.new_private_tab, PURPLE, PRIV_BG) { newTab(true, home) })
        bar.addView(tabsBtn)
        bar.addView(button("⋮", R.string.menu, TEXT, GRAY_BG) { showMenu(it) })
        progress = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply { max = 100 }
        frame = FrameLayout(this)
        val nav = LinearLayout(this).apply { gravity = Gravity.CENTER; setPadding(dp(4), dp(4), dp(4), dp(4)) }
        fun navBtn(b: Button) = nav.addView(b, LinearLayout.LayoutParams(0, dp(44), 1f))
        navBtn(button("◀", R.string.back, BLUE, BLUE_BG) { web()?.let { if (it.canGoBack()) it.goBack() } })
        navBtn(button("▶", R.string.forward, BLUE, BLUE_BG) { web()?.let { if (it.canGoForward()) it.goForward() } })
        navBtn(button("⟳", R.string.reload, GREEN, GREEN_BG) { web()?.reload() })
        navBtn(button("⌂", R.string.home, ORANGE, ORANGE_BG) { web()?.loadUrl(home) })
        navBtn(button("＋", R.string.new_tab, BLUE, BLUE_BG) { newTab(false, QuickAccess.URL) })
        root.addView(bar)
        root.addView(progress, LinearLayout.LayoutParams(-1, 6))
        root.addView(frame, LinearLayout.LayoutParams(-1, 0, 1f))
        root.addView(nav)
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
        // Powiadomienie odtwarzania w tle wymaga zgody (Android 13+).
        if (Build.VERSION.SDK_INT >= 33 && prefs.getBoolean("tlo", true) && !prefs.getBoolean("zapytano-powiadomienia", false)) {
            prefs.edit().putBoolean("zapytano-powiadomienia", true).apply()
            requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 1)
        }
        dropPrivateProfile() // resztki po poprzedniej sesji
        val link = intent?.dataString
        if (!restoreTabs() && link == null) newTab(false, home)
        if (link != null) newTab(false, link)
    }

    // Kolory jak w Velivo na Windows; motyw: 0 = jak telefon, 1 = jasny, 2 = ciemny.
    private val dark by lazy {
        when (prefs.getInt("motyw", 0)) {
            1 -> false
            2 -> true
            else -> (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES
        }
    }
    private fun c(light: String, night: String) = Color.parseColor(if (dark) night else light)
    private val BAR by lazy { c("#E5E7EB", "#1F2937") }
    private val TEXT by lazy { c("#1F2937", "#E5E7EB") }
    private val FIELD by lazy { c("#FFFFFF", "#111827") }
    private val GRAY_BG by lazy { c("#F3F4F6", "#374151") }
    private val BLUE by lazy { c("#1D4ED8", "#93C5FD") }
    private val BLUE_BG by lazy { c("#DBEAFE", "#1E3A8A") }
    private val GREEN by lazy { c("#15803D", "#86EFAC") }
    private val GREEN_BG by lazy { c("#DCFCE7", "#14532D") }
    private val ORANGE by lazy { c("#C2410C", "#FDBA74") }
    private val ORANGE_BG by lazy { c("#FFEDD5", "#7C2D12") }
    private val PRIV_BG by lazy { c("#EDE9FE", "#4C1D95") }
    private val PURPLE by lazy { c("#5B21B6", "#DDD6FE") }
    private val BANK_BG by lazy { c("#CCFBF1", "#134E4A") }

    private companion object {
        const val PRIVATE_PROFILE = "prywatna"
        const val BANK_PROFILE = "bank"
        const val REQ_VLPAIR = 7
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
        if (intent.action == PlaybackService.ACTION_STOP) {
            tabs.forEach { it.web.evaluateJavascript("document.querySelectorAll('video,audio').forEach(function(m){m.pause()})", null) }
            stopService(Intent(this, PlaybackService::class.java))
        startLan()
            return
        }
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
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR
    }

    private fun exitFullscreen() {
        val v = fullView ?: return
        (window.decorView as FrameLayout).removeView(v)
        fullView = null
        window.insetsController?.show(WindowInsets.Type.systemBars())
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        fullCallback?.onCustomViewHidden()
        fullCallback = null
    }

    // --- Odtwarzanie w tle -----------------------------------------------------
    // Po wyjściu z Velivo: jeśli gra film lub muzyka, powiadomienie trzyma odtwarzanie przy życiu.
    private fun backgroundPlayback() {
        if (!prefs.getBoolean("tlo", true)) return
        val w = web() ?: return
        if (tabs.getOrNull(cur)?.bank == true) return
        w.evaluateJavascript("[].some.call(document.querySelectorAll('video,audio'),function(m){return !m.paused&&!m.ended})") { playing ->
            if (playing != "true") return@evaluateJavascript
            runCatching {
                startForegroundService(Intent(this, PlaybackService::class.java).putExtra(PlaybackService.EXTRA_TITLE, w.title))
            }
        }
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    private fun round(color: Int) = GradientDrawable().apply { setColor(color); cornerRadius = dp(8).toFloat() }

    private fun button(label: String, tip: Int, fg: Int, bg: Int, click: (View) -> Unit) = Button(this, null, android.R.attr.borderlessButtonStyle).apply {
        text = label
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
        val w = WebView(this)
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
        if (prefs.getBoolean("adblock", true) && WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT))
            WebViewCompat.addDocumentStartJavaScript(w, youtubeJs, YOUTUBE_ORIGINS)
        val quiet = priv || bank
        if (!quiet) w.addJavascriptInterface(PasswordBridge(w), "VelivoHasla")
        if (!quiet) quickAccess.attach(w)
        w.webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(view: WebView, req: WebResourceRequest): WebResourceResponse? {
                if (req.url.host == QuickAccess.HOST) return if (quiet) null else quickAccess.serve(req.url)
                if (prefs.getBoolean("adblock", true) && isBlocked(req.url)) {
                    blockedCount++
                    return WebResourceResponse("text/plain", "utf-8", ByteArrayInputStream(ByteArray(0)))
                }
                return null
            }
            override fun shouldOverrideUrlLoading(view: WebView, req: WebResourceRequest): Boolean {
                val s = req.url.scheme ?: return false
                if (s == "http" || s == "https") return false
                runCatching { startActivity(Intent(Intent.ACTION_VIEW, req.url)) }
                return true
            }
            override fun onPageFinished(view: WebView, url: String) {
                val qa = QuickAccess.isQuickAccess(url)
                if (!quiet && !qa) passwordsOnPage(view)
                if (view == web()) address.setText(if (qa) "" else url)
                if (!quiet && !qa) addHistory(view.title ?: url, url)
                if (bank) bankUsed = System.currentTimeMillis()
            }
        }
        w.webChromeClient = object : WebChromeClient() {
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
            val req = DownloadManager.Request(Uri.parse(dUrl))
                .setMimeType(mime)
                .addRequestHeader("User-Agent", ua)
                .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
                .setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, name)
            if (!quiet) CookieManager.getInstance().getCookie(dUrl)?.let { req.addRequestHeader("Cookie", it) }
            (getSystemService(DOWNLOAD_SERVICE) as DownloadManager).enqueue(req)
            toast(getString(R.string.download_started))
        }
        tabs.add(Tab(w, priv, bank))
        show(tabs.size - 1)
        w.loadUrl(url)
    }

    private fun isBlocked(u: Uri) = (u.scheme == "http" || u.scheme == "https") && blocker.matches(u.host)

    // Najpierw to, co już jest na telefonie, potem w tle świeże listy (co 4 dni).
    private fun startFilters() = Thread {
        blocker = AdBlocker.build(this)
        if (AdBlocker.stale(this) && AdBlocker.download(this) > 0) blocker = AdBlocker.build(this)
    }.start()

    private fun show(i: Int) {
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
    private fun showTabs() {
        tabs.getOrNull(cur)?.let { snap(it) }
        val list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(8), dp(8), dp(8), dp(8)) }
        val dlg = AlertDialog.Builder(this)
            .setTitle(R.string.tabs)
            .setView(ScrollView(this).apply { addView(list) })
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

    private fun showMenu(anchor: View) {
        val m = PopupMenu(this, anchor)
        m.menu.add(0, 1, 0, R.string.new_tab)
        m.menu.add(0, 2, 0, R.string.new_private_tab)
        m.menu.add(0, 14, 0, R.string.quick_access)
        m.menu.add(0, 3, 0, R.string.add_bookmark)
        m.menu.add(0, 4, 0, R.string.bookmarks)
        m.menu.add(0, 5, 0, R.string.history)
        m.menu.add(0, 6, 0, R.string.clear_history)
        m.menu.add(0, 9, 0, R.string.passwords)
        m.menu.add(0, 10, 0, R.string.bank_mode)
        m.menu.add(0, 11, 0, R.string.theme)
        m.menu.add(0, 13, 0, R.string.lan_sync)
        m.menu.add(0, 12, 0, R.string.background_play).apply { isCheckable = true; isChecked = prefs.getBoolean("tlo", true) }
        m.menu.add(0, 7, 0, R.string.adblock).apply { isCheckable = true; isChecked = prefs.getBoolean("adblock", true) }
        m.menu.add(0, 8, 0, getString(R.string.blocked, blockedCount)).isEnabled = false
        m.setOnMenuItemClickListener {
            when (it.itemId) {
                1 -> newTab(false, QuickAccess.URL)
                2 -> newTab(true, home)
                3 -> web()?.let { w -> w.url?.let { u -> addTo("zakladki", w.title ?: u, u, 500); toast(getString(R.string.added)) } }
                4 -> pick("zakladki", R.string.bookmarks)
                5 -> pick("historia", R.string.history)
                6 -> { prefs.edit().remove("historia").apply(); web()?.clearHistory() }
                9 -> unlock { showPasswords() }
                10 -> openBank()
                11 -> chooseTheme()
                13 -> unlock { showLanSync() }
                14 -> web()?.loadUrl(QuickAccess.URL)
                12 -> {
                    val on = !prefs.getBoolean("tlo", true)
                    prefs.edit().putBoolean("tlo", on).apply()
                    if (on && Build.VERSION.SDK_INT >= 33) requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 1)
                }
                7 -> prefs.edit().putBoolean("adblock", !prefs.getBoolean("adblock", true)).apply()
            }
            true
        }
        m.show()
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
            setHintTextColor(c("#6B7280", "#9CA3AF"))
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
                    text = row.sub; textSize = 12f; setTextColor(c("#6B7280", "#9CA3AF")); maxLines = 1; ellipsize = TextUtils.TruncateAt.END
                })
                v.addView(texts, LinearLayout.LayoutParams(0, -2, 1f))
                v.addView(button("✕", R.string.delete, TEXT, GRAY_BG) {
                    onDelete(row); source.remove(row); refreshList(); if (source.isEmpty()) dlg.dismiss()
                }.apply { isFocusable = false })   // inaczej ListView nie przyjmuje stuknięć w wiersz
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
        val b = AlertDialog.Builder(this).setTitle(getString(title) + " (" + all.size + ")").setView(box)
            .setNegativeButton(R.string.close_tab_short, null)
        extra?.let { (label, act) -> b.setNeutralButton(label) { _, _ -> act() } }
        dlg = b.create()
        adapter.refreshList()
        dlg.show()
    }

    // --- Hasła ---------------------------------------------------------------
    private val passwords by lazy { Passwords(this) }
    private var lastAsked = ""

    private fun secureHost(w: WebView): String? {
        val u = Uri.parse(w.url ?: return null)
        return if (u.scheme == "https") u.host else null
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
        val saved = passwords.forHost(host)
        if (saved.isEmpty()) return
        w.evaluateJavascript("document.querySelector('input[type=password]')!=null") { has ->
            if (has != "true" || w != web()) return@evaluateJavascript
            if (saved.size == 1) fill(w, saved[0])
            else AlertDialog.Builder(this)
                .setTitle(R.string.passwords)
                .setItems(saved.map { it.user.ifEmpty { "—" } }.toTypedArray()) { _, i -> fill(w, saved[i]) }
                .show()
        }
    }

    private fun fill(w: WebView, e: Passwords.Entry) {
        if (secureHost(w) != e.host) return
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
        )
    }
    private var multicastLock: WifiManager.MulticastLock? = null

    private fun startLan() {
        if (!prefs.getBoolean("lan", false)) return
        if (multicastLock == null) multicastLock = (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager)
            .createMulticastLock("velivo-lan").apply { setReferenceCounted(false) }
        multicastLock?.acquire()
        lan.start()
    }

    private fun stopLan() {
        lan.stop()
        multicastLock?.release()
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
        val pw = pwds?.let { passwords.saveAll(LanSync.parsePasswords(it)) } ?: 0
        prefs.edit().putString("lan-komputer", dev).putLong("lan-czas", System.currentTimeMillis()).apply()
        if (added + pw > 0) toast(getString(R.string.lan_received, dev, added, pw))
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
        val items = arrayOf(
            getString(if (on) R.string.lan_turn_off else R.string.lan_turn_on),
            getString(R.string.lan_pair),
            getString(R.string.lan_recovery),
            getString(R.string.lan_sync_now),
            getString(R.string.lan_forget),
            getString(R.string.lan_name) + ": " + (prefs.getString("lan-nazwa", null)?.ifBlank { null } ?: LanSync.MODEL),
        )
        AlertDialog.Builder(this)
            .setTitle(getString(R.string.lan_sync) + "\n" + status)
            .setItems(items) { _, i ->
                when (i) {
                    0 -> { prefs.edit().putBoolean("lan", !on).apply(); if (on) stopLan() else startLan() }
                    1 -> {
                        prefs.edit().putBoolean("lan", true).apply()
                        startLan()
                        lan.pairingUntil = System.currentTimeMillis() + 3 * 60 * 1000
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
        val names = arrayOf(getString(R.string.theme_system), getString(R.string.theme_light), getString(R.string.theme_dark))
        AlertDialog.Builder(this)
            .setTitle(R.string.theme)
            .setSingleChoiceItems(names, prefs.getInt("motyw", 0)) { d, i ->
                d.dismiss()
                if (i != prefs.getInt("motyw", 0)) { prefs.edit().putInt("motyw", i).apply(); saveTabs(); recreate() }
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
        stopService(Intent(this, PlaybackService::class.java))
        lockBankIfIdle()
        idle.postDelayed(idleCheck, 30_000)
    }

    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_SHORT).show()

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK && fullView != null) { exitFullscreen(); return true }
        val w = web()
        if (keyCode == KeyEvent.KEYCODE_BACK && w != null && w.canGoBack()) { w.goBack(); return true }
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
        stopLan()
        idle.removeCallbacks(idleCheck)
        backgroundPlayback()
        saveTabs()
        super.onPause()
    }

    override fun onDestroy() {
        tabs.forEach { it.web.destroy() }
        if (isFinishing) dropPrivateProfile()
        super.onDestroy()
    }
}
