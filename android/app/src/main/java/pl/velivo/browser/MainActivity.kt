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

    override fun onCreate(savedInstanceState: Bundle?) {
        setTheme(if (dark) R.style.Velivo_Dark else R.style.Velivo_Light)
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
            setImageResource(R.mipmap.ic_launcher)
            setPadding(dp(5), dp(5), dp(5), dp(5))
            background = round(GRAY_BG)
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
        if (intent?.action == Intent.ACTION_SEND) sendSharedFile(intent)
        val link = intent?.dataString?.takeIf { intent?.action != Intent.ACTION_SEND }
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
    // Kolory Velivo z ikony: fiolet -> turkus. Tła paskow i przyciskow w odcieniach fioletu Velivo (nie szare).
    private val BRAND1 = Color.parseColor("#6D3FDB")
    private val BRAND2 = Color.parseColor("#2BA9D8")
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
            tts?.stop()
            stopService(Intent(this, PlaybackService::class.java))
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

    // gradient Velivo (jak ikona) - przyciski glowne
    private fun brand(radius: Int = 8) = GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(BRAND1, BRAND2)).apply { cornerRadius = dp(radius).toFloat() }

    private fun Button.velivo(): Button { background = brand(); setTextColor(Color.WHITE); return this }

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
            override fun onPageCommitVisible(view: WebView, url: String) { hideAds(view, url) }
            // ramka z reklamą, która się nie wczytała - zapamiętaj adres, żeby ją zwinąć zamiast strony błędu
            override fun onReceivedError(view: WebView, req: WebResourceRequest, err: android.webkit.WebResourceError) {
                if (!req.isForMainFrame) { failedFrames.add(req.url.host ?: ""); collapseFrames(view) }
            }
            override fun onPageFinished(view: WebView, url: String) {
                hideAds(view, url)
                for (ms in longArrayOf(0, 1500, 4000, 9000)) view.postDelayed({ collapseFrames(view) }, ms)
                val qa = QuickAccess.isQuickAccess(url)
                if (!quiet && !qa) passwordsOnPage(view)
                if (view == web()) address.setText(if (qa) "" else url)
                if (!quiet && !qa) addHistory(view.title ?: url, url)
                if (bank) bankUsed = System.currentTimeMillis()
                applyHidden(view, url)
            }
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

    // Ukrywanie reklam (reguły "##" z list) i zwijanie pustych ramek z napisem "REKLAMA" -
    // po zablokowanej reklamie nie zostaje dziura ani strona błędu w ramce.
    private fun hideAds(view: WebView, url: String) {
        if (!prefs.getBoolean("adblock", true) || QuickAccess.isQuickAccess(url)) return
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
if(R.test(n)&&e.offsetHeight>0&&empty(e))__vUp(e);}}
var t=0;window.__vZwin=new MutationObserver(function(){clearTimeout(t);t=setTimeout(zwin,400)});
window.__vZwin.observe(d.documentElement,{childList:true,subtree:true});zwin();})()""", null)
    }

    private val failedFrames = java.util.Collections.synchronizedSet(HashSet<String>())

    // Ramki z zablokowanych lub niedziałających adresów reklam - zwiń je razem z pustym otoczeniem.
    private fun collapseFrames(view: WebView) = runOnUiThread {
        if (!prefs.getBoolean("adblock", true)) return@runOnUiThread
        view.evaluateJavascript("JSON.stringify([].map.call(document.querySelectorAll('iframe'),function(f){return f.src||''}))") { r ->
            val srcs = runCatching { org.json.JSONArray(org.json.JSONTokener(r).nextValue() as String) }.getOrNull() ?: return@evaluateJavascript
            val bad = (0 until srcs.length()).filter { i ->
                val u = srcs.optString(i)
                u.isNotEmpty() && Uri.parse(u).let { it.host in failedFrames || isBlocked(it) }
            }
            if (bad.isNotEmpty()) view.evaluateJavascript("(function(){var f=document.querySelectorAll('iframe');${bad.joinToString(",", "[", "]")}.forEach(function(i){var e=f[i];if(e)(window.__vUp?__vUp(e):e.style.setProperty('display','none','important'))})})()", null)
        }
    }

    private fun isBlocked(u: Uri) = (u.scheme == "http" || u.scheme == "https") && blocker.matches(u.host)

    // Najpierw to, co już jest na telefonie, potem w tle świeże listy (co 4 dni).
    private fun startFilters() = Thread {
        blocker = AdBlocker.build(this)
        reapplyFilters()
        if (AdBlocker.stale(this) && AdBlocker.download(this) > 0) { blocker = AdBlocker.build(this); reapplyFilters() }
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
window.__vPick=function(ev){ev.preventDefault();ev.stopPropagation();ov.style.pointerEvents='none';var t=document.elementFromPoint(ev.clientX,ev.clientY);ov.style.pointerEvents='auto';if(t&&t!=ov){mark(best(t));setTimeout(function(){prompt('$pickToken','')},50);}};
ov.addEventListener('click',__vPick,true);document.documentElement.appendChild(ov);
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
        val js = """(function(){var best=document.body,max=0;
document.querySelectorAll('article,main,[role=main],section,div').forEach(function(e){var n=0;
e.querySelectorAll(':scope > p').forEach(function(p){n+=p.innerText.length});if(n>max){max=n;best=e}});
var root=document.querySelector('article')||best,out=[];
root.querySelectorAll('h1,h2,h3,p,li,blockquote').forEach(function(e){var t=e.innerText.trim();
if(t.length>1&&!e.closest('nav,footer,aside,form'))out.push(t)});
if(!out.length)out=[document.body.innerText];
return JSON.stringify({t:document.title,b:out.join('\n\n')})})()"""
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
                override fun onDone(id: String?) { if (id == "v${parts.size - 1}") stopService(Intent(this@MainActivity, PlaybackService::class.java)) }
                @Deprecated("") override fun onError(id: String?) {}
            })
            parts.forEachIndexed { i, part -> t.speak(part, if (i == 0) android.speech.tts.TextToSpeech.QUEUE_FLUSH else android.speech.tts.TextToSpeech.QUEUE_ADD, null, "v$i") }
            runCatching { startForegroundService(Intent(this, PlaybackService::class.java).putExtra(PlaybackService.EXTRA_TITLE, "🔊 " + (web()?.title ?: ""))) }
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

    private fun stopSpeaking() { tts?.stop(); stopService(Intent(this, PlaybackService::class.java)) }

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
        fun resize(d: Float) { size = (size + d).coerceIn(14f, 30f); text.textSize = size; prefs.edit().putFloat("czytnik_rozmiar", size).apply() }
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
        dlg.setContentView(root)
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
            paint.shader = android.graphics.LinearGradient(dp(10).toFloat(), 0f, dp(10) + w, 0f, BRAND1, BRAND2, android.graphics.Shader.TileMode.CLAMP)
        }, LinearLayout.LayoutParams(0, -2, 1f))
        head.addView(TextView(this).apply {
            text = "🛡 " + getString(R.string.blocked, blockedCount)
            textSize = 13f; setTextColor(GREEN); background = round(GREEN_BG); setPadding(dp(10), dp(5), dp(10), dp(5))
        })
        box.addView(head)

        fun section(title: Int, items: List<Triple<String, Int, Int>>, toggles: Map<Int, Boolean> = emptyMap()) {
            box.addView(TextView(this).apply {
                text = getString(title); textSize = 13f; setTextColor(c("#4F4380", "#A79FD0")); setPadding(dp(2), dp(14), 0, dp(6))
            })
            val grid = android.widget.GridLayout(this).apply { columnCount = 3 }
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
                tile.addView(TextView(this).apply { text = icon; textSize = 24f; gravity = Gravity.CENTER })
                tile.addView(TextView(this).apply {
                    text = getString(label) + (if (on != null) "\n" + getString(if (on) R.string.on else R.string.off) else "")
                    textSize = 12f; setTextColor(fg); gravity = Gravity.CENTER; maxLines = 3
                })
                grid.addView(tile, android.widget.GridLayout.LayoutParams(
                    android.widget.GridLayout.spec(android.widget.GridLayout.UNDEFINED), android.widget.GridLayout.spec(android.widget.GridLayout.UNDEFINED, 1f)
                ).apply { width = 0; setMargins(dp(4), dp(4), dp(4), dp(4)) })
            }
            box.addView(grid, LinearLayout.LayoutParams(-1, -2))
        }
        section(R.string.menu_tabs, listOf(
            Triple("＋", R.string.new_tab, 1), Triple("🕶", R.string.new_private_tab, 2), Triple("⭐", R.string.quick_access, 14),
            Triple("🏦", R.string.bank_mode, 10), Triple("🔎", R.string.find_in_page, 16), Triple("🖥", R.string.desktop_mode, 17),
            Triple("📖", R.string.reader, 18), Triple("🎯", R.string.hide_element, 20), Triple("💻", R.string.send_tab, 21), Triple(if (tts?.isSpeaking == true) "⏹" else "🔊", if (tts?.isSpeaking == true) R.string.stop_reading else R.string.read_aloud, 19)),
            mapOf(17 to (tabs.getOrNull(cur)?.desktop == true)))
        section(R.string.menu_mine, listOf(
            Triple("🔖", R.string.bookmarks, 4), Triple("➕", R.string.add_bookmark, 3), Triple("🕘", R.string.history, 5),
            Triple("🔑", R.string.passwords, 9), Triple("✍", R.string.fill_password, 15)))
        section(R.string.menu_settings, listOf(
            Triple("🛡", R.string.adblock, 7), Triple("🎵", R.string.background_play, 12), Triple("🌓", R.string.theme, 11),
            Triple("💻", R.string.lan_sync, 13), Triple("🧹", R.string.clear_history, 6)),
            mapOf(7 to prefs.getBoolean("adblock", true), 12 to prefs.getBoolean("tlo", true)))
        dlg.setContentView(ScrollView(this).apply { addView(box) })
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
    // --- Karta między telefonem a komputerem (z miejscem w filmie, jak 📺 Wyślij do… na Windows) ---
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
            saveFile = { name, data, dev -> saveToDownloads(name, data, dev) },
            onTab = { dev, url, title, time -> receiveTab(dev, url, title, time) },
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
