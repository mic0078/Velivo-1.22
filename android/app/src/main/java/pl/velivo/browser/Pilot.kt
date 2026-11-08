package pl.velivo.browser

import android.app.Activity
import android.app.AlertDialog
import android.app.Dialog
import android.content.SharedPreferences
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Handler
import android.os.Looper
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.TextView
import org.json.JSONObject
import java.net.InetAddress

// 🎮 Velivo Pilot: telefon steruje odtwarzaczem filmów i TV w Velivo na Windows (Pilot.cs).
// Komputery w domu znajdują się same (sparowane synchronizacją LAN – ten sam klucz i ta sama ochrona), ostatni wybrany jest zapamiętany.
// Polecenia zawsze tylko do jednego komputera; stan odtwarzacza przychodzi z komputera co sekundę i po każdej zmianie.
class Pilot(private val act: Activity, private val lan: LanSync, private val prefs: SharedPreferences, private val pw: Passwords) {
    private class Pc(val id: String, var name: String, var addr: InetAddress?, var seen: Long, var st: JSONObject?)

    private val pcs = LinkedHashMap<String, Pc>()
    private var cur: String? = prefs.getString("pilot-pc", null)
    private var curName: String = prefs.getString("pilot-pc-nazwa", null) ?: ""
    private val h = Handler(Looper.getMainLooper())
    private val opened = System.currentTimeMillis()
    private var dragging = false
    private var volDragging = false
    private var askedChoice = false

    private lateinit var dlg: Dialog
    private lateinit var pcBtn: Button
    private lateinit var status: TextView
    private lateinit var title: TextView
    private lateinit var playBtn: Button
    private lateinit var seek: SeekBar
    private lateinit var time: TextView
    private lateinit var vol: SeekBar
    private lateinit var volTxt: TextView
    private lateinit var muteBtn: Button

    private val bg = Color.parseColor("#15161A")
    private val card = Color.parseColor("#24262D")
    private val accent = Color.parseColor("#6D3FDB")
    private val dim = Color.parseColor("#A0A4AE")

    // logo kanałów i plakaty z Jellyfin: w tle, w pamięci (jak listy Filmy i TV w aplikacji)
    private val imgCache = android.util.LruCache<String, android.graphics.Bitmap>(150)
    private val imgPool by lazy { java.util.concurrent.Executors.newFixedThreadPool(3) }
    private var jfKeys = listOf<Pair<String, String>>()
    // dane, które telefon już ma: logo z listy TV telefonu, plakaty i opisy z biblioteki Jellyfin telefonu
    private var tvLogo = mapOf<String, String>()
    private var jfConf: Jelly.Conf? = null
    private var jfById = mapOf<String, Jelly.Item>(); private var jfByName = mapOf<String, Jelly.Item>()
    private fun norm(t: String) = t.lowercase().replace(Regex("\\s*\\(\\d{4}\\)\\s*$"), "").trim()
    private fun phoneData(done: () -> Unit) = Thread {
        runCatching { tvLogo = Tv.sources(pw).flatMap { src -> runCatching { Tv.channels(act, pw, src, false) }.getOrDefault(emptyList()) }.filter { it.logo.isNotEmpty() }.associate { it.name.trim().lowercase() to it.logo } }
        runCatching {
            jfConf = Jelly.conf(pw)
            val lib = Jelly.cached(act, pw)?.first ?: emptyList()
            jfById = lib.associateBy { it.id }; jfByName = lib.associateBy { norm(it.name) + "|" + it.year }
        }
        act.runOnUiThread(done)
    }.start()
    private fun loadImage(iv: ImageView, url: String, key0: String? = null) {
        iv.tag = url
        if (url.isEmpty()) return
        imgCache.get(url)?.let { iv.setImageBitmap(it); return }
        val key = key0 ?: jfKeys.firstOrNull { url.startsWith(it.first) }?.second
        imgPool.execute {
            val bmp = runCatching {
                val c = java.net.URL(url).openConnection() as java.net.HttpURLConnection
                c.connectTimeout = 6000; c.readTimeout = 10000
                if (key != null) { c.setRequestProperty("X-Emby-Token", key); c.setRequestProperty("Authorization", "MediaBrowser Token=\"$key\"") }
                c.inputStream.use { android.graphics.BitmapFactory.decodeStream(it) }
            }.getOrNull() ?: return@execute
            val small = if (bmp.height > 300) android.graphics.Bitmap.createScaledBitmap(bmp, bmp.width * 300 / bmp.height, 300, true) else bmp
            imgCache.put(url, small)
            act.runOnUiThread { if (iv.tag == url) iv.setImageBitmap(small) }
        }
    }

    private fun dp(v: Int) = (v * act.resources.displayMetrics.density).toInt()
    private fun s(id: Int, vararg a: Any) = act.getString(id, *a)

    private fun big(icon: String, label: String, onClick: () -> Unit) = Button(act).apply {
        text = icon + "\n" + label; isAllCaps = false; textSize = 15f; setTextColor(Color.WHITE)
        background = GradientDrawable().apply { setColor(card); cornerRadius = dp(18).toFloat() }
        setOnClickListener { onClick() }; contentDescription = label
    }

    private fun row(vararg views: View) = LinearLayout(act).apply {
        orientation = LinearLayout.HORIZONTAL
        for (v in views) addView(v, LinearLayout.LayoutParams(0, dp(84), 1f).apply { setMargins(dp(5), dp(5), dp(5), dp(5)) })
    }

    fun show() {
        if (!lan.hasKey) {   // bez parowania nie ma bezpiecznego połączenia - prowadzimy do parowania
            AlertDialog.Builder(act).setTitle("🎮 " + s(R.string.pilot)).setMessage(R.string.pilot_not_paired)
                .setPositiveButton(R.string.lan_sync) { _, _ -> (act as? MainActivity)?.openLanSync() }
                .setNegativeButton(android.R.string.cancel, null).show()
            return
        }
        val root = LinearLayout(act).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(14), dp(14), dp(14), dp(14)); setBackgroundColor(bg) }
        // góra: komputer (dotknięcie = wybór innego), stan, co gra
        pcBtn = Button(act).apply {
            isAllCaps = false; textSize = 18f; setTypeface(typeface, Typeface.BOLD); setTextColor(Color.WHITE); gravity = Gravity.START or Gravity.CENTER_VERTICAL
            background = GradientDrawable().apply { setColor(card); cornerRadius = dp(14).toFloat() }
            setPadding(dp(16), 0, dp(16), 0); setOnClickListener { choosePc() }
        }
        root.addView(pcBtn, LinearLayout.LayoutParams(-1, dp(60)))
        status = TextView(act).apply { textSize = 14f; setTextColor(dim); setPadding(dp(4), dp(8), dp(4), 0) }
        root.addView(status)
        title = TextView(act).apply { textSize = 17f; setTextColor(Color.WHITE); maxLines = 4; ellipsize = TextUtils.TruncateAt.END; setPadding(dp(4), dp(6), dp(4), dp(10)) }
        title.setOnClickListener { val k = pcs[cur]?.st?.optString("kind"); browse(if (k == "film") "film" else "tv") }   // stuknięcie = lista: kanały przy TV, filmy przy filmie
        title.setOnLongClickListener { if (pcs[cur]?.st != null) ask("info", "now"); true }   // przytrzymanie tytułu = opis filmu / program kanału
        root.addView(title)
        // 📺🎬 wybór z listy komputera i włączenie wprost z telefonu
        root.addView(row(big("📺", s(R.string.pilot_tv)) { browse("tv") }, big("🎬", s(R.string.pilot_films)) { browse("film") }))
        // środek: duże przyciski
        playBtn = big("⏯", s(R.string.pilot_play)) { send("play") }
        root.addView(row(big("⏮", s(R.string.pilot_prev)) { send("prev") }, playBtn, big("⏭", s(R.string.pilot_next)) { send("next") }, big("⏹", s(R.string.pilot_stop)) { send("stop") }))
        root.addView(row(big("⏪", s(R.string.pilot_back10)) { send("jump", n = -10000.0) }, big("⏩", s(R.string.pilot_fwd10)) { send("jump", n = 10000.0) }))
        // dół: postęp, czas, głośność, wyciszenie
        time = TextView(act).apply { textSize = 15f; setTextColor(Color.WHITE); gravity = Gravity.CENTER; setPadding(0, dp(12), 0, 0) }
        root.addView(time)
        seek = SeekBar(act).apply {
            max = 1000; setPadding(dp(16), dp(10), dp(16), dp(10))
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(b: SeekBar?, p: Int, user: Boolean) { if (user) time.text = fmt(p * len() / 1000) + " / " + fmt(len()) }
                override fun onStartTrackingTouch(b: SeekBar?) { dragging = true }
                override fun onStopTrackingTouch(b: SeekBar?) { dragging = false; if (len() > 0) send("seek", n = progress * len() / 1000.0) }
            })
        }
        root.addView(seek, LinearLayout.LayoutParams(-1, dp(48)))
        val volRow = LinearLayout(act).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        volTxt = TextView(act).apply { textSize = 15f; setTextColor(Color.WHITE); minWidth = dp(110) }
        vol = SeekBar(act).apply {
            max = 125
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(b: SeekBar?, p: Int, user: Boolean) { if (user) volTxt.text = "🔊 " + s(R.string.pilot_volume) + " $p%" }
                override fun onStartTrackingTouch(b: SeekBar?) { volDragging = true }
                override fun onStopTrackingTouch(b: SeekBar?) { volDragging = false; send("vol", n = progress.toDouble()) }
            })
        }
        muteBtn = Button(act).apply {
            isAllCaps = false; textSize = 14f; setTextColor(Color.WHITE)
            background = GradientDrawable().apply { setColor(card); cornerRadius = dp(12).toFloat() }
            text = "🔇 " + s(R.string.pilot_mute); setOnClickListener { send("mute") }
        }
        volRow.addView(volTxt); volRow.addView(vol, LinearLayout.LayoutParams(0, dp(48), 1f)); volRow.addView(muteBtn, LinearLayout.LayoutParams(-2, dp(52)))
        root.addView(volRow)
        root.addView(Button(act).apply {
            isAllCaps = false; textSize = 17f; setTextColor(Color.WHITE); text = "⚙  " + s(R.string.pilot_controls)
            background = GradientDrawable().apply { setColor(accent); cornerRadius = dp(16).toFloat() }
            setOnClickListener { controls() }
        }, LinearLayout.LayoutParams(-1, dp(62)).apply { topMargin = dp(14) })

        dlg = Dialog(act, android.R.style.Theme_Black_NoTitleBar_Fullscreen).apply {
            setContentView(ScrollView(act).apply { setBackgroundColor(bg); isFillViewport = true; addView(root) })
            setOnDismissListener { lan.onPilot = null; lan.onPilotList = null; h.removeCallbacksAndMessages(null) }
        }
        lan.onPilot = { id, dev, st, from -> act.runOnUiThread { got(id, dev, st, from) } }
        lan.onPilotList = { id, o -> act.runOnUiThread {
            if (id != cur) return@runOnUiThread
            if (o.optString("kind") == "info")   // ℹ opis filmu / program kanału (z listy albo z ekranu pilota)
                AlertDialog.Builder(act).setTitle(o.optString("title")).setMessage(o.optString("text")).setPositiveButton(android.R.string.ok, null).show()
            else onList?.invoke(o)
        } }
        dlg.show()
        tick()
        refresh()
    }

    // szukanie komputerów i podtrzymanie: co 3 s „ping” do wszystkich w sieci domowej (komputer bez pingu przez 30 s przestaje wysyłać stan)
    private fun tick() {
        if (!dlg.isShowing) return
        lan.sendPilot("*", "ping")
        cur?.let { c -> pcs[c]?.addr?.let { lan.sendPilot("*", "ping", target = it) } }
        refresh()
        h.postDelayed({ tick() }, 3000)
    }

    private fun got(id: String, dev: String, st: JSONObject?, from: InetAddress?) {
        val p = pcs.getOrPut(id) { Pc(id, dev, from, 0, null) }
        p.name = dev.ifBlank { p.name }; if (from != null) p.addr = from; p.seen = System.currentTimeMillis(); p.st = st
        if (cur == id && curName != p.name) { curName = p.name; prefs.edit().putString("pilot-pc-nazwa", curName).apply() }
        if (cur == null && !askedChoice && System.currentTimeMillis() - opened > 2500) { askedChoice = true; choosePc() }   // pierwszy raz - wybór komputera
        refresh()
    }

    private fun online(p: Pc?) = p != null && System.currentTimeMillis() - p.seen < 9000
    private fun len(): Long = pcs[cur]?.st?.optLong("len") ?: 0L

    private fun stText(p: Pc): String = if (!online(p)) s(R.string.pilot_offline) else {
        val st = p.st
        s(R.string.pilot_online) + " · " + when {
            st == null -> s(R.string.pilot_closed)
            st.optString("st") == "play" -> s(R.string.pilot_playing)
            st.optString("st") == "pause" -> s(R.string.pilot_paused)
            else -> s(R.string.pilot_stopped)
        }
    }

    private fun refresh() {
        if (!::pcBtn.isInitialized) return
        val p = pcs[cur]
        pcBtn.text = "💻  " + (if (cur == null) s(R.string.pilot_choose_pc) else curName.ifBlank { "?" }) + "   ▾"
        val now = System.currentTimeMillis()
        status.text = when {
            cur == null && pcs.isEmpty() -> if (now - opened < 6000) s(R.string.pilot_searching) else s(R.string.pilot_no_pc)
            cur == null -> s(R.string.pilot_choose_pc)
            p == null || !online(p) -> if (now - opened < 6000) s(R.string.pilot_searching) else s(R.string.pilot_unavailable, curName)
            else -> stText(p)
        }
        val st = if (online(p)) p?.st else null
        val nm = st?.optString("name")?.ifBlank { null } ?: st?.optString("title")?.ifBlank { null }   // czysty tytuł (starsze Velivo: tytuł okna)
        title.text = if (nm == null) s(R.string.pilot_nothing) else (if (st?.optString("kind") == "tv") "📺 " else "🎬 ") + nm + (st?.optString("now")?.ifBlank { null }?.let { "\n▶ $it" } ?: "")   // na kanale: co teraz leci
        playBtn.text = (if (st?.optString("st") == "play") "⏸" else "▶") + "\n" + s(R.string.pilot_play)
        val l = st?.optLong("len") ?: 0L; val pos = st?.optLong("pos") ?: 0L
        if (!dragging) { seek.progress = if (l > 0) (pos * 1000 / l).toInt() else 0; time.text = if (st != null && l <= 0) "🔴 " + s(R.string.pilot_live) + " · " + fmt(pos) else fmt(pos) + " / " + fmt(l) }   // TV na żywo bez długości
        seek.isEnabled = st != null && l > 0
        if (!volDragging) { val v = st?.optInt("vol") ?: 0; vol.progress = v; volTxt.text = "🔊 " + s(R.string.pilot_volume) + " $v%" }
        val muted = st?.optBoolean("mute") == true
        muteBtn.text = if (muted) "🔈 " + s(R.string.pilot_unmute) else "🔇 " + s(R.string.pilot_mute)
        for (b in listOf(playBtn, seek, vol, muteBtn)) b.alpha = if (st != null) 1f else 0.45f
    }

    private fun fmt(ms: Long): String {
        val t = (ms / 1000).coerceAtLeast(0); val hh = t / 3600; val mm = t % 3600 / 60; val ss = t % 60
        return if (hh > 0) "%d:%02d:%02d".format(hh, mm, ss) else "%d:%02d".format(mm, ss)
    }

    // polecenie zawsze do jednego, wybranego komputera
    private fun send(op: String, v: String? = null, n: Double = 0.0) {
        val c = cur ?: run { choosePc(); return }
        val p = pcs[c]
        if (!online(p) || p?.st == null) { android.widget.Toast.makeText(act, if (!online(p)) s(R.string.pilot_unavailable, curName) else s(R.string.pilot_closed), android.widget.Toast.LENGTH_SHORT).show(); return }
        lan.sendPilot(c, op, v, n, p.addr)
    }

    private fun choosePc() {
        val list = pcs.values.sortedBy { it.name.lowercase() }.toMutableList()
        if (cur != null && pcs[cur] == null) list.add(0, Pc(cur!!, curName, null, 0, null))   // zapamiętany, choć teraz wyłączony
        if (list.isEmpty()) { AlertDialog.Builder(act).setTitle(R.string.pilot_choose_pc).setMessage(R.string.pilot_no_pc).setPositiveButton(android.R.string.ok, null).show(); return }
        val labels = list.map { (if (it.id == cur) "✔ " else "") + "💻 " + it.name + "\n" + stText(it) + (it.st?.optString("title")?.ifBlank { null }?.let { t -> "\n🎬 $t" } ?: "") }
        AlertDialog.Builder(act).setTitle(R.string.pilot_choose_pc).setItems(labels.toTypedArray()) { _, i ->
            val p = list[i]
            cur = p.id; curName = p.name
            prefs.edit().putString("pilot-pc", p.id).putString("pilot-pc-nazwa", p.name).apply()   // zapamiętany także po ponownym uruchomieniu telefonu
            lan.sendPilot("*", "ping", target = p.addr)
            refresh()
        }.show()
    }

    private var onList: ((JSONObject) -> Unit)? = null

    // prośba do wybranego komputera, gdy odtwarzacz nie musi być otwarty (listy, włączenie kanału / filmu)
    private fun ask(op: String, v: String? = null, n: Double = 0.0, q: String? = null, g: String? = null, sort: String? = null): Boolean {
        val c = cur ?: run { choosePc(); return false }
        val p = pcs[c]
        if (!online(p)) { android.widget.Toast.makeText(act, s(R.string.pilot_unavailable, curName), android.widget.Toast.LENGTH_LONG).show(); return false }
        return lan.sendPilot(c, op, v, n, p?.addr, q, g, sort)
    }

    // 📺 Telewizja / 🎬 Filmy z komputera: zakładki, grupa / gatunek, szukanie, stuknięcie = włącz na komputerze
    private fun browse(kind: String) {
        if (cur == null) { choosePc(); return }
        var mode = prefs.getString("pilot-tryb-$kind", null) ?: if (kind == "tv") "fav" else "cont"   // zapamiętana zakładka (pierwszy raz: ulubione / oglądaj dalej)
        var group = prefs.getString("pilot-grupa-$kind", "") ?: ""; var query = ""; var page = 0   // zapamiętana grupa TV / gatunek
        val sorts = if (kind == "tv") listOf("" to s(R.string.pilot_sort_list), "name" to s(R.string.pilot_sort_az), "name-desc" to s(R.string.pilot_sort_za))
            else listOf("" to s(R.string.pilot_sort_az), "name-desc" to s(R.string.pilot_sort_za), "new" to s(R.string.pilot_sort_new), "year" to s(R.string.pilot_sort_year), "rating" to s(R.string.pilot_sort_rating), "last" to s(R.string.pilot_sort_last))
        var sort = prefs.getString("pilot-sort-$kind", "") ?: ""   // zapamiętane sortowanie
        var grid = prefs.getBoolean("pilot-kanapa", false)   // filmy: lista z plakatami albo Kanapa (siatka plakatów), zapamiętane
        val d = Dialog(act, android.R.style.Theme_Black_NoTitleBar_Fullscreen)
        val box = LinearLayout(act).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(12), dp(12), dp(12), dp(12)); setBackgroundColor(bg) }
        fun pill(t: String, on: Boolean, f: () -> Unit) = TextView(act).apply {   // TextView zamiast Button - przycisk ucinał napis (stała wysokość, wewnętrzne marginesy)
            text = t; textSize = 16f; setTextColor(Color.WHITE); isSingleLine = true; gravity = Gravity.CENTER
            setPadding(dp(16), dp(11), dp(16), dp(11)); isClickable = true; isFocusable = true
            background = GradientDrawable().apply { setColor(if (on) accent else card); cornerRadius = dp(20).toFloat() }
            setOnClickListener { f() }
        }
        val head = LinearLayout(act).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        head.addView(Button(act).apply { text = "✕"; textSize = 20f; setTextColor(Color.WHITE); background = null; setOnClickListener { d.dismiss() }; contentDescription = act.getString(android.R.string.cancel) }, LinearLayout.LayoutParams(dp(56), dp(56)))
        head.addView(TextView(act).apply { text = (if (kind == "tv") "📺 " + s(R.string.pilot_tv) else "🎬 " + s(R.string.pilot_films)) + " · " + curName; textSize = 19f; setTextColor(Color.WHITE); setTypeface(typeface, Typeface.BOLD) })
        box.addView(head)
        val tabs = LinearLayout(act).apply { orientation = LinearLayout.HORIZONTAL }
        box.addView(android.widget.HorizontalScrollView(act).apply { isHorizontalScrollBarEnabled = false; addView(tabs) })   // przesuwany palcem, gdy się nie mieści
        val search = android.widget.EditText(act).apply {
            hint = s(R.string.pilot_search); setTextColor(Color.WHITE); setHintTextColor(dim); textSize = 17f; isSingleLine = true
            imeOptions = android.view.inputmethod.EditorInfo.IME_ACTION_SEARCH
            background = GradientDrawable().apply { setColor(card); cornerRadius = dp(12).toFloat() }; setPadding(dp(14), 0, dp(14), 0)
        }
        box.addView(search, LinearLayout.LayoutParams(-1, dp(52)).apply { topMargin = dp(8) })
        val info = TextView(act).apply { textSize = 14f; setTextColor(dim); setPadding(dp(4), dp(8), dp(4), dp(4)) }
        box.addView(info)
        val list = LinearLayout(act).apply { orientation = LinearLayout.VERTICAL }
        box.addView(ScrollView(act).apply { addView(list) }, LinearLayout.LayoutParams(-1, 0, 1f))
        var groups = listOf<String>()
        lateinit var load: (Boolean) -> Unit
        fun drawTabs() {
            tabs.removeAllViews()
            val a = if (kind == "tv") listOf("fav" to "⭐ " + s(R.string.pilot_fav), "tv" to s(R.string.pilot_all)) else listOf("cont" to "▶ " + s(R.string.pilot_continue), "ffav" to "⭐ " + s(R.string.pilot_fav), "film" to s(R.string.pilot_all))
            for ((m, t) in a) tabs.addView(pill(t, mode == m) { mode = m; group = ""; prefs.edit().putString("pilot-tryb-$kind", m).putString("pilot-grupa-$kind", "").apply(); drawTabs(); load(true) }, LinearLayout.LayoutParams(-2, -2).apply { setMargins(0, dp(6), dp(8), 0) })
            if (mode == "tv" || mode == "film") tabs.addView(pill((if (kind == "tv") s(R.string.pilot_group) else s(R.string.pilot_genre)) + ": " + group.ifBlank { s(R.string.pilot_any) } + " ▾", false) {
                val names = listOf(s(R.string.pilot_any)) + groups
                AlertDialog.Builder(act).setItems(names.toTypedArray()) { _, i -> group = if (i == 0) "" else groups[i - 1]; prefs.edit().putString("pilot-grupa-$kind", group).apply(); drawTabs(); load(true) }.show()
            }, LinearLayout.LayoutParams(-2, -2).apply { setMargins(0, dp(6), dp(8), 0) })
            if (mode != "cont") tabs.addView(pill("⇅ " + (sorts.firstOrNull { it.first == sort }?.second ?: sorts[0].second), false) {
                AlertDialog.Builder(act).setTitle(R.string.pilot_sort).setItems(sorts.map { it.second }.toTypedArray()) { _, i ->
                    sort = sorts[i].first; prefs.edit().putString("pilot-sort-$kind", sort).apply(); drawTabs(); load(true)
                }.show()
            }, LinearLayout.LayoutParams(-2, -2).apply { setMargins(0, dp(6), dp(8), 0) })
            if (kind == "film") tabs.addView(pill(if (grid) "☰ " + s(R.string.pilot_view_list) else "▦ " + s(R.string.pilot_view_kanapa), false) {
                grid = !grid; prefs.edit().putBoolean("pilot-kanapa", grid).apply(); drawTabs(); load(true)
            }, LinearLayout.LayoutParams(-2, -2).apply { setMargins(0, dp(6), 0, 0) })
        }
        load = { fresh ->
            if (fresh) { page = 0; list.removeAllViews() }
            info.text = s(R.string.pilot_loading)
            if (!ask("list", mode, page.toDouble(), query, group, sort)) info.text = s(R.string.pilot_unavailable, curName)
        }
        onList = { o ->
            if (o.optString("kind") == mode && o.optInt("page") == page) {
                val g = o.optJSONArray("groups"); if (g != null && g.length() > 0) { groups = (0 until g.length()).map { g.optString(it) }; drawTabs() }
                val items = o.optJSONArray("items"); val total = o.optInt("total")
                o.optJSONArray("keys")?.let { ka -> jfKeys = (0 until ka.length()).mapNotNull { ka.optJSONArray(it)?.let { p -> p.optString(0) to p.optString(1) } }.filter { it.first.isNotEmpty() } }
                list.findViewWithTag<View>("more")?.let { list.removeView(it) }
                for (k in 0 until (items?.length() ?: 0)) {
                    val it2 = items!!.getJSONArray(k); val idx = it2.optInt(0); val name = it2.optString(1); val sub = it2.optString(2)
                    var fav = it2.optBoolean(3); var img = it2.optString(4); var key: String? = null; var overview = ""
                    if (kind == "tv") tvLogo[name.trim().lowercase()]?.let { img = it }   // logo z listy TV telefonu
                    else { val jc = jfConf; val ji = jfById[it2.optString(5)] ?: jfByName[norm(name) + "|" + it2.optInt(6)]
                        if (ji != null) overview = ji.overview
                        if (jc != null && ji != null) { img = Jelly.image(jc, ji.id); key = jc.key } }   // plakat z biblioteki telefonu (ten sam adres i klucz co Biblioteka)
                    val play = { if (ask(if (kind == "tv") "play-tv" else "play-film", name, idx.toDouble())) { android.widget.Toast.makeText(act, s(R.string.pilot_starting, name), android.widget.Toast.LENGTH_SHORT).show(); d.dismiss() } }
                    if (kind == "film" && !grid) {   // 🎬 lista: plakat po lewej, tytuł, rok/ocena, opis (jak Biblioteka)
                        val line = LinearLayout(act).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL; isClickable = true
                            setPadding(dp(6), dp(6), 0, dp(6)); background = GradientDrawable().apply { setColor(card); cornerRadius = dp(12).toFloat() }
                            setOnClickListener { play() }; setOnLongClickListener { ask("info", "film", idx.toDouble()); true } }
                        line.addView(ImageView(act).apply { scaleType = ImageView.ScaleType.CENTER_CROP; setBackgroundColor(Color.parseColor("#33363F")); loadImage(this, img, key) }, LinearLayout.LayoutParams(dp(64), dp(96)))
                        line.addView(TextView(act).apply { textSize = 15f; setTextColor(Color.WHITE); maxLines = 5; ellipsize = TextUtils.TruncateAt.END; setPadding(dp(12), 0, dp(4), 0)
                            text = android.text.SpannableStringBuilder(name).apply { setSpan(android.text.style.StyleSpan(Typeface.BOLD), 0, length, 0)
                                if (sub.isNotBlank()) append("\n" + sub); if (overview.isNotBlank()) { val st0 = length; append("\n" + overview); setSpan(android.text.style.ForegroundColorSpan(dim), st0, length, 0) } } }, LinearLayout.LayoutParams(0, -2, 1f))
                        line.addView(Button(act).apply { text = if (fav) "★" else "☆"; textSize = 24f; setTextColor(Color.parseColor("#F5C518")); background = null; contentDescription = s(R.string.pilot_fav)
                            setOnClickListener { if (ask("fav-film", name, idx.toDouble())) { fav = !fav; text = if (fav) "★" else "☆"
                                android.widget.Toast.makeText(act, s(if (fav) R.string.pilot_fav_added else R.string.pilot_fav_removed, name), android.widget.Toast.LENGTH_SHORT).show()
                                if (!fav && mode == "ffav") list.removeView(line) } } }, LinearLayout.LayoutParams(dp(52), dp(56)))
                        list.addView(line, LinearLayout.LayoutParams(-1, -2).apply { setMargins(0, dp(3), 0, dp(3)) })
                        continue
                    }
                    if (kind == "film") {   // 🎬 siatka plakatów jak w Kanapie: plakat, tytuł, rok/ocena, ☆; przytrzymanie = opis
                        var gridRow = list.getChildAt(list.childCount - 1) as? LinearLayout
                        if (gridRow?.tag != "grid" || gridRow.childCount >= 3) { gridRow = LinearLayout(act).apply { tag = "grid"; orientation = LinearLayout.HORIZONTAL }; list.addView(gridRow, LinearLayout.LayoutParams(-1, -2)) }
                        val cell = LinearLayout(act).apply {
                            orientation = LinearLayout.VERTICAL; setPadding(dp(4), dp(4), dp(4), dp(8)); isClickable = true
                            background = GradientDrawable().apply { setColor(card); cornerRadius = dp(12).toFloat() }
                            setOnClickListener { play() }
                            setOnLongClickListener { ask("info", "film", idx.toDouble()); true }
                        }
                        val ph = android.widget.FrameLayout(act)
                        ph.addView(ImageView(act).apply { scaleType = ImageView.ScaleType.CENTER_CROP; setBackgroundColor(Color.parseColor("#33363F")); loadImage(this, img, key) }, android.widget.FrameLayout.LayoutParams(-1, -1))
                        if (img.isEmpty()) ph.addView(TextView(act).apply { text = "🎬"; textSize = 32f; gravity = Gravity.CENTER }, android.widget.FrameLayout.LayoutParams(-1, -1))
                        ph.addView(TextView(act).apply {
                            text = if (fav) "★" else "☆"; textSize = 24f; setTextColor(Color.parseColor("#F5C518")); setPadding(dp(8), 0, dp(8), dp(2))
                            background = GradientDrawable().apply { setColor(Color.parseColor("#99000000")); cornerRadius = dp(10).toFloat() }
                            contentDescription = s(R.string.pilot_fav); isClickable = true
                            setOnClickListener { if (ask("fav-film", name, idx.toDouble())) { fav = !fav; text = if (fav) "★" else "☆"
                                android.widget.Toast.makeText(act, s(if (fav) R.string.pilot_fav_added else R.string.pilot_fav_removed, name), android.widget.Toast.LENGTH_SHORT).show() } }
                        }, android.widget.FrameLayout.LayoutParams(-2, -2, Gravity.TOP or Gravity.END))
                        val w = (act.resources.displayMetrics.widthPixels - dp(24)) / 3 - dp(8)
                        cell.addView(ph, LinearLayout.LayoutParams(-1, w * 3 / 2))
                        cell.addView(TextView(act).apply { text = name; textSize = 14f; setTextColor(Color.WHITE); maxLines = 2; ellipsize = TextUtils.TruncateAt.END; setPadding(dp(2), dp(4), dp(2), 0) })
                        if (sub.isNotBlank()) cell.addView(TextView(act).apply { text = sub; textSize = 12f; setTextColor(dim); maxLines = 1; setPadding(dp(2), 0, dp(2), 0) })
                        gridRow.addView(cell, LinearLayout.LayoutParams(0, -2, 1f).apply { setMargins(dp(3), dp(3), dp(3), dp(3)) })
                        continue
                    }
                    val line = LinearLayout(act).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
                    line.addView(ImageView(act).apply { scaleType = ImageView.ScaleType.FIT_CENTER; setPadding(dp(4), dp(4), dp(4), dp(4)); loadImage(this, img, key) }, LinearLayout.LayoutParams(dp(64), dp(48)).apply { marginEnd = dp(6) })   // 📺 logo kanału
                    line.addView(TextView(act).apply {   // TextView - cały tytuł i podpis bez ucinania
                        text = if (sub.isBlank()) name else "$name\n$sub"; textSize = 16f; setTextColor(Color.WHITE); isClickable = true
                        gravity = Gravity.START or Gravity.CENTER_VERTICAL; setPadding(dp(16), dp(12), dp(16), dp(12)); minHeight = dp(56)
                        background = GradientDrawable().apply { setColor(card); cornerRadius = dp(12).toFloat() }
                        setOnLongClickListener { ask("info", if (kind == "tv") "tv" else "film", idx.toDouble()); true }   // przytrzymanie = opis
                        setOnClickListener { play() }
                    }, LinearLayout.LayoutParams(0, -2, 1f))
                    line.addView(Button(act).apply {   // ⭐ dodaj / usuń z ulubionych (te same ulubione co na komputerze)
                        text = if (fav) "★" else "☆"; textSize = 24f; setTextColor(Color.parseColor("#F5C518")); background = null
                        contentDescription = s(R.string.pilot_fav)
                        setOnClickListener {
                            if (ask(if (kind == "tv") "fav-tv" else "fav-film", name, idx.toDouble())) {
                                fav = !fav; text = if (fav) "★" else "☆"
                                android.widget.Toast.makeText(act, s(if (fav) R.string.pilot_fav_added else R.string.pilot_fav_removed, name), android.widget.Toast.LENGTH_SHORT).show()
                                if (!fav && (mode == "fav" || mode == "ffav")) list.removeView(line)
                            }
                        }
                    }, LinearLayout.LayoutParams(dp(56), dp(56)).apply { gravity = Gravity.CENTER_VERTICAL })
                    list.addView(line, LinearLayout.LayoutParams(-1, -2).apply { setMargins(0, dp(3), 0, dp(3)) })
                }
                val shown = (page + 1) * 100
                info.text = if (total == 0) s(if (kind == "tv") R.string.pilot_empty_tv else R.string.pilot_empty_films) else s(R.string.pilot_count, total)
                if (shown < total) list.addView(Button(act).apply {
                    tag = "more"; text = s(R.string.pilot_more); isAllCaps = false; textSize = 16f; setTextColor(Color.WHITE)
                    background = GradientDrawable().apply { setColor(accent); cornerRadius = dp(12).toFloat() }
                    setOnClickListener { page++; load(false) }
                }, LinearLayout.LayoutParams(-1, dp(54)).apply { topMargin = dp(6) })
            }
        }
        search.setOnEditorActionListener { _, _, _ ->
            query = search.text.toString().trim()
            if (query.isNotEmpty() && (mode == "fav" || mode == "cont")) mode = if (kind == "tv") "tv" else "film"   // szukanie zawsze w całej liście
            drawTabs(); load(true); true
        }
        drawTabs()
        d.setContentView(box)
        d.setOnDismissListener { onList = null }
        d.show()
        phoneData { load(true) }   // najpierw logo i plakaty z telefonu, potem lista
    }

    // ⚙ Sterowanie: Obraz, Dźwięk, Napisy, Odtwarzanie - tylko to, co obecny odtwarzacz naprawdę obsługuje
    private fun controls() {
        val st = pcs[cur]?.st ?: run { send("ping"); return }
        val box = LinearLayout(act).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(14), dp(8), dp(14), dp(8)); setBackgroundColor(bg) }
        lateinit var d: AlertDialog
        fun head(t: Int) = box.addView(TextView(act).apply { text = s(t); textSize = 13f; setTextColor(dim); setTypeface(typeface, Typeface.BOLD); setPadding(dp(4), dp(14), 0, dp(4)) })
        fun item(t: String, f: () -> Unit) = box.addView(Button(act).apply {
            text = t; isAllCaps = false; textSize = 16f; setTextColor(Color.WHITE); gravity = Gravity.START or Gravity.CENTER_VERTICAL; setPadding(dp(16), 0, dp(16), 0)
            background = GradientDrawable().apply { setColor(card); cornerRadius = dp(12).toFloat() }
            setOnClickListener { f() }
        }, LinearLayout.LayoutParams(-1, dp(56)).apply { setMargins(0, dp(3), 0, dp(3)) })
        fun pick(titleId: Int, names: List<String>, onPick: (Int) -> Unit) {
            AlertDialog.Builder(act).setTitle(titleId).setItems(names.toTypedArray()) { _, i -> onPick(i); d.dismiss() }.show()
        }
        head(R.string.pilot_picture)
        item("⛶  " + s(R.string.pilot_full) + " · " + s(if (st.optBoolean("full")) R.string.on else R.string.off)) { send("full"); d.dismiss() }
        val asps = listOf("", "16:9", "4:3", "21:9", "1:1", "5:4", "2.35:1")
        item("▭  " + s(R.string.pilot_aspect) + ": " + st.optString("asp").ifBlank { s(R.string.pilot_auto) }) {
            pick(R.string.pilot_aspect, asps.map { it.ifBlank { s(R.string.pilot_auto) } }) { send("aspect", asps[it]) }
        }
        val zooms = listOf(0.0, 1.0, 1.25, 1.5, 2.0)
        item("🔍  " + s(R.string.pilot_zoom) + ": " + st.optDouble("zoom", 0.0).let { if (it <= 0.0) s(R.string.pilot_auto) else "%.2f×".format(it) }) {
            pick(R.string.pilot_zoom, zooms.map { if (it == 0.0) s(R.string.pilot_auto) else "%.2f×".format(it) }) { send("zoom", n = zooms[it]) }
        }
        head(R.string.pilot_sound)
        val auds = st.optJSONArray("auds")
        val audIds = (0 until (auds?.length() ?: 0)).map { auds!!.getJSONArray(it).optInt(0) }
        val audNames = (0 until (auds?.length() ?: 0)).map { auds!!.getJSONArray(it).optString(1) }
        val curAud = audIds.indexOf(st.optInt("aud", -1)).let { if (it >= 0) audNames[it] else s(R.string.pilot_none) }
        if (audIds.isNotEmpty()) item("🎧  " + s(R.string.pilot_audio_track) + ": " + curAud) { pick(R.string.pilot_audio_track, audNames) { send("audio", n = audIds[it].toDouble()) } }
        else box.addView(TextView(act).apply { text = s(R.string.pilot_audio_track) + ": " + s(R.string.pilot_none); setTextColor(dim); setPadding(dp(8), dp(6), 0, dp(6)) })
        box.addView(TextView(act).apply { text = "🔊 " + s(R.string.pilot_volume) + ": " + st.optInt("vol") + "%" + (if (st.optBoolean("mute")) " · " + s(R.string.pilot_mute) else ""); setTextColor(Color.WHITE); setPadding(dp(8), dp(6), 0, dp(6)) })
        head(R.string.pilot_subs)
        val subs = st.optJSONArray("subs")
        val subIds = (0 until (subs?.length() ?: 0)).map { subs!!.getJSONArray(it).optInt(0) }
        val subNames = subIds.mapIndexed { i, sid -> if (sid < 0) s(R.string.pilot_subs_off) else subs!!.getJSONArray(i).optString(1) }
        val curSub = st.optInt("sub", -1).let { cs -> if (cs < 0) s(R.string.pilot_subs_off) else subIds.indexOf(cs).let { if (it >= 0) subNames[it] else "?" } }
        if (subIds.any { it >= 0 }) item("💬  " + s(R.string.pilot_subs_title) + ": " + curSub) { pick(R.string.pilot_subs_title, subNames) { send("sub", n = subIds[it].toDouble()) } }
        else box.addView(TextView(act).apply { text = s(R.string.pilot_subs_title) + ": " + s(R.string.pilot_none); setTextColor(dim); setPadding(dp(8), dp(6), 0, dp(6)) })
        head(R.string.pilot_playback)
        box.addView(row(big("⏪", s(R.string.pilot_back10)) { send("jump", n = -10000.0) }, big(if (st.optString("st") == "play") "⏸" else "▶", s(R.string.pilot_play)) { send("play") }, big("⏩", s(R.string.pilot_fwd10)) { send("jump", n = 10000.0) }))
        box.addView(row(big("⏮", s(R.string.pilot_prev)) { send("prev") }, big("⏹", s(R.string.pilot_stop)) { send("stop") }, big("⏭", s(R.string.pilot_next)) { send("next") }))
        d = AlertDialog.Builder(act).setTitle("⚙ " + s(R.string.pilot_controls)).setView(ScrollView(act).apply { addView(box) })
            .setPositiveButton(android.R.string.ok, null).create()
        d.show()
    }
}
