package pl.velivo.browser

import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.app.DownloadManager
import android.content.Context
import android.content.Intent
import android.content.res.Configuration
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Bundle
import android.os.Environment
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.view.WindowInsets
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
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.PopupMenu
import android.widget.ProgressBar
import android.widget.Toast
import androidx.webkit.ProfileStore
import androidx.webkit.WebSettingsCompat
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import org.json.JSONObject
import java.io.ByteArrayInputStream

class MainActivity : Activity() {
    private class Tab(val web: WebView, val priv: Boolean)

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

    override fun onCreate(savedInstanceState: Bundle?) {
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
        navBtn(button("＋", R.string.new_tab, BLUE, BLUE_BG) { newTab(false, home) })
        root.addView(bar)
        root.addView(progress, LinearLayout.LayoutParams(-1, 6))
        root.addView(frame, LinearLayout.LayoutParams(-1, 0, 1f))
        root.addView(nav)
        setContentView(root)
        // Android 15+ rysuje pod paskami systemu - zostaw na nie miejsce (i na klawiaturę).
        root.setOnApplyWindowInsetsListener { v, insets ->
            val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.ime())
            v.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }

        startFilters()
        dropPrivateProfile() // resztki po poprzedniej sesji
        val link = intent?.dataString
        if (!restoreTabs() && link == null) newTab(false, home)
        if (link != null) newTab(false, link)
    }

    // Kolory jak w Velivo na Windows; ciemne, gdy telefon ma tryb ciemny.
    private val dark by lazy { (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES }
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

    private companion object {
        const val PRIVATE_PROFILE = "prywatna"

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
        intent.dataString?.let { newTab(false, it) }
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
    private fun newTab(priv: Boolean, url: String) {
        val w = WebView(this)
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
        if (!priv) w.addJavascriptInterface(PasswordBridge(w), "VelivoHasla")
        w.webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(view: WebView, req: WebResourceRequest): WebResourceResponse? {
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
                if (!priv) passwordsOnPage(view)
                if (view == web()) address.setText(url)
                if (!priv) addHistory(view.title ?: url, url)
            }
        }
        w.webChromeClient = object : WebChromeClient() {
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
            if (!priv) CookieManager.getInstance().getCookie(dUrl)?.let { req.addRequestHeader("Cookie", it) }
            (getSystemService(DOWNLOAD_SERVICE) as DownloadManager).enqueue(req)
            toast(getString(R.string.download_started))
        }
        tabs.add(Tab(w, priv))
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
        cur = i
        frame.removeAllViews()
        val t = tabs[i]
        frame.addView(t.web)
        address.setText(t.web.url ?: "")
        address.background = round(if (t.priv) PRIV_BG else FIELD)
        tabsBtn.text = tabs.size.toString()
    }

    private fun closeTab(i: Int) {
        val t = tabs.removeAt(i)
        t.web.destroy()
        if (t.priv && tabs.none { it.priv }) dropPrivateProfile()
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

    private fun showTabs() {
        val names = tabs.mapIndexed { i, t ->
            (if (t.priv) "🕶 " else "") + (t.web.title?.takeIf { it.isNotBlank() } ?: t.web.url ?: "…") + if (i == cur) "  ●" else ""
        }.toTypedArray()
        AlertDialog.Builder(this)
            .setTitle(R.string.tabs)
            .setItems(names) { _, i -> show(i) }
            .setPositiveButton(R.string.close_tab) { _, _ -> closeTab(cur) }
            .setNeutralButton(R.string.new_private_tab) { _, _ -> newTab(true, home) }
            .show()
    }

    private fun showMenu(anchor: View) {
        val m = PopupMenu(this, anchor)
        m.menu.add(0, 1, 0, R.string.new_tab)
        m.menu.add(0, 2, 0, R.string.new_private_tab)
        m.menu.add(0, 3, 0, R.string.add_bookmark)
        m.menu.add(0, 4, 0, R.string.bookmarks)
        m.menu.add(0, 5, 0, R.string.history)
        m.menu.add(0, 6, 0, R.string.clear_history)
        m.menu.add(0, 9, 0, R.string.passwords)
        m.menu.add(0, 7, 0, R.string.adblock).apply { isCheckable = true; isChecked = prefs.getBoolean("adblock", true) }
        m.menu.add(0, 8, 0, getString(R.string.blocked, blockedCount)).isEnabled = false
        m.setOnMenuItemClickListener {
            when (it.itemId) {
                1 -> newTab(false, home)
                2 -> newTab(true, home)
                3 -> web()?.let { w -> w.url?.let { u -> addTo("zakladki", w.title ?: u, u, 500); toast(getString(R.string.added)) } }
                4 -> pick("zakladki", R.string.bookmarks)
                5 -> pick("historia", R.string.history)
                6 -> { prefs.edit().remove("historia").apply(); web()?.clearHistory() }
                9 -> showPasswords()
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

    private fun pick(key: String, title: Int) {
        val l = list(key)
        if (l.isEmpty()) { toast(getString(R.string.empty)); return }
        AlertDialog.Builder(this)
            .setTitle(title)
            .setItems(l.map { it.substringBefore('\t') }.toTypedArray()) { _, i -> web()?.loadUrl(l[i].substringAfter('\t')) }
            .show()
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
        val list = passwords.all().sortedBy { it.host }
        if (list.isEmpty()) { toast(getString(R.string.empty)); return }
        AlertDialog.Builder(this)
            .setTitle(R.string.passwords)
            .setItems(list.map { it.host + "\n" + it.user.ifEmpty { "—" } }.toTypedArray()) { _, i ->
                AlertDialog.Builder(this)
                    .setMessage(getString(R.string.delete_q, list[i].host))
                    .setPositiveButton(R.string.delete) { _, _ -> passwords.remove(list[i]) }
                    .setNegativeButton(android.R.string.cancel, null)
                    .show()
            }
            .show()
    }

    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_SHORT).show()

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
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
        val open = tabs.filter { !it.priv }.mapNotNull { it.web.url }
        val sel = tabs.getOrNull(cur)?.takeIf { !it.priv }?.let { t -> tabs.filter { !it.priv }.indexOf(t) } ?: 0
        prefs.edit().putString("karty", open.joinToString("\n")).putInt("karta", sel).apply()
    }

    private fun restoreTabs(): Boolean {
        val urls = prefs.getString("karty", "")!!.lines().filter { it.startsWith("http") }
        if (urls.isEmpty()) return false
        urls.forEach { newTab(false, it) }
        show(prefs.getInt("karta", 0).coerceIn(0, tabs.size - 1))
        return true
    }

    override fun onPause() {
        saveTabs()
        super.onPause()
    }

    override fun onDestroy() {
        tabs.forEach { it.web.destroy() }
        if (isFinishing) dropPrivateProfile()
        super.onDestroy()
    }
}
