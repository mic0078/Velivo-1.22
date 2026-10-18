package pl.velivo.browser

import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.app.DownloadManager
import android.content.Context
import android.content.Intent
import android.graphics.Color
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
    private val blockedHosts by lazy {
        assets.open("blokowane.txt").bufferedReader().readLines()
            .map { it.trim() }.filter { it.isNotEmpty() && !it.startsWith("#") }.toHashSet()
    }
    @Volatile private var blockedCount = 0

    private val home = "https://www.startpage.com/"

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(Color.WHITE) }
        val bar = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(8, 8, 8, 8) }
        address = EditText(this).apply {
            hint = getString(R.string.address_hint)
            setSingleLine()
            imeOptions = EditorInfo.IME_ACTION_GO
            inputType = EditorInfo.TYPE_TEXT_VARIATION_URI
            setSelectAllOnFocus(true)
            setOnEditorActionListener { _, _, _ -> go(text.toString()); true }
        }
        bar.addView(address, LinearLayout.LayoutParams(0, -2, 1f))
        tabsBtn = button("1", R.string.tabs) { showTabs() }
        bar.addView(tabsBtn)
        bar.addView(button("⋮", R.string.menu) { showMenu(it) })
        progress = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply { max = 100 }
        frame = FrameLayout(this)
        val nav = LinearLayout(this).apply { gravity = Gravity.CENTER }
        nav.addView(button("◀", R.string.back) { web()?.let { if (it.canGoBack()) it.goBack() } }, LinearLayout.LayoutParams(0, -2, 1f))
        nav.addView(button("▶", R.string.forward) { web()?.let { if (it.canGoForward()) it.goForward() } }, LinearLayout.LayoutParams(0, -2, 1f))
        nav.addView(button("⟳", R.string.reload) { web()?.reload() }, LinearLayout.LayoutParams(0, -2, 1f))
        nav.addView(button("＋", R.string.new_tab) { newTab(false, home) }, LinearLayout.LayoutParams(0, -2, 1f))
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

        newTab(false, intent?.dataString ?: home)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        intent.dataString?.let { newTab(false, it) }
    }

    private fun button(label: String, tip: Int, click: (View) -> Unit) = Button(this, null, android.R.attr.borderlessButtonStyle).apply {
        text = label
        contentDescription = getString(tip)
        tooltipText = getString(tip)
        minWidth = 0
        setOnClickListener(click)
    }

    private fun web(): WebView? = tabs.getOrNull(cur)?.web

    @SuppressLint("SetJavaScriptEnabled")
    private fun newTab(priv: Boolean, url: String) {
        val w = WebView(this)
        w.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = !priv
            builtInZoomControls = true
            displayZoomControls = false
            setSupportMultipleWindows(false)
            if (priv) cacheMode = android.webkit.WebSettings.LOAD_NO_CACHE
        }
        CookieManager.getInstance().setAcceptThirdPartyCookies(w, false)
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

    private fun isBlocked(u: Uri): Boolean {
        var host = u.host ?: return false
        while (true) {
            if (host in blockedHosts) return true
            val dot = host.indexOf('.')
            if (dot < 0 || host.indexOf('.', dot + 1) < 0) return false
            host = host.substring(dot + 1)
        }
    }

    private fun show(i: Int) {
        cur = i
        frame.removeAllViews()
        val t = tabs[i]
        frame.addView(t.web)
        address.setText(t.web.url ?: "")
        address.setBackgroundColor(if (t.priv) Color.rgb(230, 220, 245) else Color.TRANSPARENT)
        tabsBtn.text = tabs.size.toString()
    }

    private fun closeTab(i: Int) {
        val t = tabs.removeAt(i)
        t.web.destroy()
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

    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_SHORT).show()

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        val w = web()
        if (keyCode == KeyEvent.KEYCODE_BACK && w != null && w.canGoBack()) { w.goBack(); return true }
        return super.onKeyDown(keyCode, event)
    }

    override fun onDestroy() {
        tabs.forEach { it.web.destroy() }
        super.onDestroy()
    }
}
