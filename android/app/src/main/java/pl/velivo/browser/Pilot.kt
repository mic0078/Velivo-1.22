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
    private lateinit var plansTxt: TextView
    private var plansAt = 0L
    private var pcEpgv: Int? = null   // wersja programu TV na komputerze (z „plans” co 15 s)
    private var onEpgv: ((Int) -> Unit)? = null
    private val pcLibv = HashMap<String, Int>()   // wersja listy na komputerze: "tv" (program TV) i "film" (biblioteka)

    private val bg = Color.parseColor("#15161A")
    private val card = Color.parseColor("#24262D")
    private val accent = Color.parseColor("#6D3FDB")
    private val dim = Color.parseColor("#A0A4AE")

    // logo kanałów i plakaty z Jellyfin: w tle, w pamięci (jak listy Filmy i TV w aplikacji)
    private val imgCache = (act as? MainActivity)?.imgCache ?: android.util.LruCache<String, android.graphics.Bitmap>(150)   // ta sama pamięć co Telewizja i Biblioteka w Velivo
    private val imgPool by lazy { java.util.concurrent.Executors.newFixedThreadPool(3) }
    private var jfKeys = listOf<Pair<String, String>>()
    // dane, które telefon już ma: logo z listy TV telefonu, plakaty i opisy z biblioteki Jellyfin telefonu
    private var tvLogo = mapOf<String, String>()
    private var jfConf: Jelly.Conf? = null
    private var jfById = mapOf<String, Jelly.Item>(); private var jfByName = mapOf<String, Jelly.Item>()
    private fun chKey(t: String) = t.lowercase().filter { it.isLetterOrDigit() }   // „TVP 1 HD” = „tvp1hd” - nazwy z listy telefonu i komputera porównywane bez spacji i znaków
    private fun norm(t: String) = t.lowercase().replace(Regex("\\s*\\(\\d{4}\\)\\s*$"), "").trim()
    private fun phoneData(done: () -> Unit) = Thread {
        runCatching { tvLogo = Tv.sources(pw).flatMap { src -> runCatching { Tv.channels(act, pw, src, false) }.getOrDefault(emptyList()) }.filter { it.logo.isNotEmpty() }.associate { chKey(it.name) to it.logo } }
        runCatching {
            jfConf = Jelly.conf(pw)
            val lib = Jelly.cached(act, pw)?.first ?: emptyList()
            jfById = lib.associateBy { it.id }; jfByName = lib.associateBy { norm(it.name) + "|" + it.year }
        }
        act.runOnUiThread(done)
    }.start()
    // pobieranie obrazków: kolejka tylko dla OTWARTEGO ekranu - po zmianie ekranu stare (np. 350 logo listy TV) są pomijane, nie blokują nowych
    @Volatile private var imgGen = 0
    private val pcWait = HashMap<String, MutableList<(android.graphics.Bitmap?) -> Unit>>()
    // pierwszy wybór: co już jest w pamięci Velivo w telefonie; potem z dysku komputera (pc:…); dopiero na końcu internet
    private fun pick(phone: String?, pc: String) = when { !phone.isNullOrEmpty() && imgCache.get(phone) != null -> phone; pc.startsWith("pc:") -> pc; else -> phone ?: pc }
    fun gotPcImg(o: JSONObject) {
        val k = o.optString("key"); val b64 = o.optString("b")
        val bmp = if (b64.isEmpty()) null else runCatching { val by = android.util.Base64.decode(b64, 0); android.graphics.BitmapFactory.decodeByteArray(by, 0, by.size) }.getOrNull()
        if (bmp != null) imgCache.put(k, bmp)
        pcWait.remove(k)?.forEach { it(bmp) }
    }
    private fun loadBmp(url: String, key0: String?, done: (android.graphics.Bitmap?) -> Unit) {
        if (url.isEmpty()) { done(null); return }
        imgCache.get(url)?.let { done(it); return }
        if (url.startsWith("pc:")) {   // obrazek z dysku komputera (logo / plakat już tam pobrane)
            val l = pcWait.getOrPut(url) { mutableListOf() }; l.add(done)
            if (l.size == 1) { val c = cur; val p = c?.let { pcs[it] }; if (c == null || !lan.sendPilot(c, "img", target = p?.addr, q = url.removePrefix("pc:"))) pcWait.remove(url)?.forEach { it(null) } }
            return
        }
        val key = key0 ?: jfKeys.firstOrNull { url.startsWith(it.first) }?.second
        val gen = imgGen
        imgPool.execute {
            if (gen != imgGen) return@execute   // ekran już zamknięty
            val bmp = runCatching {
                val c = java.net.URL(url).openConnection() as java.net.HttpURLConnection
                c.connectTimeout = 5000; c.readTimeout = 8000
                if (key != null) { c.setRequestProperty("X-Emby-Token", key); c.setRequestProperty("Authorization", "MediaBrowser Token=\"$key\"") }
                c.inputStream.use { android.graphics.BitmapFactory.decodeStream(it) }
            }.getOrNull()
            val small = bmp?.let { if (it.height > 300) android.graphics.Bitmap.createScaledBitmap(it, it.width * 300 / it.height, 300, true) else it }
            if (small != null) imgCache.put(url, small)
            act.runOnUiThread { done(small) }
        }
    }
    // obrazek do ImageView; gdy pierwszy adres nie zadziała - zapasowy (np. plakat z komputera)
    private fun loadImage(iv: ImageView, url: String, key0: String? = null, alt: String = "") {
        iv.tag = url
        if (url.isEmpty()) { if (alt.isNotEmpty()) loadImage(iv, alt); return }
        loadBmp(url, key0) { b -> if (iv.tag == url) { if (b != null) iv.setImageBitmap(b) else if (alt.isNotEmpty() && alt != url) loadImage(iv, alt) } }
    }

    // 🔊 przyciski głośności telefonu sterują głośnością odtwarzacza na komputerze (krok 5%, przytrzymanie powtarza)
    private fun volKey(k: Int, e: android.view.KeyEvent): Boolean {
        if (k != android.view.KeyEvent.KEYCODE_VOLUME_UP && k != android.view.KeyEvent.KEYCODE_VOLUME_DOWN) return false
        val st = pcs[cur]?.st ?: return false   // nic nie gra - zwykła głośność telefonu
        if (e.action == android.view.KeyEvent.ACTION_DOWN) {
            val v = ((if (volDragging) vol.progress else st.optInt("vol")) + if (k == android.view.KeyEvent.KEYCODE_VOLUME_UP) 5 else -5).coerceIn(0, vol.max)
            st.put("vol", v); vol.progress = v; volTxt.text = "🔊 " + s(R.string.pilot_volume) + " $v%"
            send("vol", n = v.toDouble())
        }
        return true
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
        // 📅 Program TV i 🎲 Co dziś obejrzeć - dwa duże kafelki pod Sterowaniem, pod nimi stan planów (makieta-pilot-epg-losowanie.html)
        fun tile(icon: String, t: String, sub: String, f: () -> Unit) = TextView(act).apply {
            text = android.text.SpannableStringBuilder("$icon  $t").apply { setSpan(android.text.style.StyleSpan(Typeface.BOLD), 0, length, 0); val a = length; append("\n" + sub)
                setSpan(android.text.style.ForegroundColorSpan(dim), a, length, 0); setSpan(android.text.style.RelativeSizeSpan(0.8f), a, length, 0) }
            textSize = 17f; setTextColor(Color.WHITE); gravity = Gravity.CENTER; setPadding(dp(8), dp(18), dp(8), dp(18)); isClickable = true
            background = GradientDrawable().apply { setColor(card); cornerRadius = dp(18).toFloat() }; setOnClickListener { f() }
        }
        root.addView(LinearLayout(act).apply {
            addView(tile("📅", s(R.string.pilot_guide), s(R.string.pilot_guide_sub)) { guide() }, LinearLayout.LayoutParams(0, -1, 1f).apply { marginEnd = dp(5) })
            addView(tile("🎲", s(R.string.pilot_rand), s(R.string.pilot_rand_sub)) { randomPick() }, LinearLayout.LayoutParams(0, -1, 1f).apply { marginStart = dp(5) })
        }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(10) })
        plansTxt = TextView(act).apply { textSize = 14f; setTextColor(Color.WHITE); gravity = Gravity.CENTER; setPadding(dp(8), dp(12), dp(8), dp(12)); visibility = View.GONE; isClickable = true
            background = GradientDrawable().apply { setColor(card); cornerRadius = dp(16).toFloat() }; setOnClickListener { plans() } }
        root.addView(plansTxt, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(10) })

        dlg = Dialog(act, android.R.style.Theme_Black_NoTitleBar_Fullscreen).apply {
            setContentView(ScrollView(act).apply { setBackgroundColor(bg); isFillViewport = true; addView(root) })
            setOnDismissListener { lan.onPilot = null; lan.onPilotList = null; h.removeCallbacksAndMessages(null) }
            setOnKeyListener { _, k, e -> volKey(k, e) }   // przyciski głośności telefonu = głośność na komputerze
        }
        lan.onPilot = { id, dev, st, from -> act.runOnUiThread { got(id, dev, st, from) } }
        lan.onPilotList = { id, o -> act.runOnUiThread {
            if (id != cur) return@runOnUiThread
            pcs[id]?.seen = System.currentTimeMillis()   // każda odpowiedź komputera (lista, program, obrazek) = jest dostępny
            if (o.has("epgv")) { val v = o.optInt("epgv"); if (pcEpgv != v) { pcEpgv = v; onEpgv?.invoke(v) } }   // komputer ma nowy program / plany
            if (o.optString("kind") == "plans") { if (o.has("libtv")) pcLibv["tv"] = o.optInt("libtv"); if (o.has("libfilm")) pcLibv["film"] = o.optInt("libfilm") }
            if (o.optString("kind") == "plans") o.optJSONArray("items")?.let { a ->   // ⏰ stan planów pod kafelkami
                val rec = (0 until a.length()).count { a.getJSONArray(it).optString(1) == "rec" }; val sw = a.length() - rec
                plansTxt.text = s(R.string.pilot_plans_count, sw, rec); plansTxt.visibility = if (a.length() > 0) View.VISIBLE else View.GONE
            }
            if (o.optString("kind") == "img") { gotPcImg(o); return@runOnUiThread }
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
        if (cur != null && online(pcs[cur]) && System.currentTimeMillis() - plansAt > 15000) { plansAt = System.currentTimeMillis(); ask("plans") }   // stan planów co 15 s
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

    private fun online(p: Pc?) = p != null && System.currentTimeMillis() - p.seen < 20000   // stan co 1 s; komputer zajęty listą/programem TV potrafi milczeć kilka sekund - 20 s, nie 9
    private fun reachable(p: Pc?) = p != null && p.addr != null && System.currentTimeMillis() - p.seen < 120_000   // polecenie wysyłamy nawet przy chwilowej ciszy
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
        if (!reachable(p) || p?.st == null) { android.widget.Toast.makeText(act, if (!reachable(p)) s(R.string.pilot_unavailable, curName) else s(R.string.pilot_closed), android.widget.Toast.LENGTH_SHORT).show(); return }
        lan.sendPilot(c, op, v, n, p.addr)
    }

    private fun choosePc() {
        val list = pcs.values.sortedBy { it.name.lowercase() }.toMutableList()
        if (cur != null && pcs[cur] == null) list.add(0, Pc(cur!!, curName, null, 0, null))   // zapamiętany, choć teraz wyłączony
        if (list.isEmpty()) { AlertDialog.Builder(act).setTitle(R.string.pilot_choose_pc).setMessage(R.string.pilot_no_pc).setPositiveButton(android.R.string.ok, null).show(); return }
        // czytelna karta na komputer: nazwa (pogrubiona), stan krótko, co gra (kanał / film) i co teraz leci - osobne wiersze, duże odstępy
        val d = Dialog(act, android.R.style.Theme_Black_NoTitleBar_Fullscreen)
        val box = LinearLayout(act).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(16), dp(16), dp(16), dp(16)); setBackgroundColor(bg) }
        box.addView(TextView(act).apply { text = s(R.string.pilot_choose_pc); textSize = 22f; setTextColor(Color.WHITE); setTypeface(typeface, Typeface.BOLD); setPadding(dp(4), 0, 0, dp(12)) })
        for (p in list) {
            val st = if (online(p)) p.st else null
            val card0 = LinearLayout(act).apply {
                orientation = LinearLayout.VERTICAL; setPadding(dp(18), dp(14), dp(18), dp(14)); isClickable = true
                background = GradientDrawable().apply { setColor(card); cornerRadius = dp(16).toFloat(); if (p.id == cur) setStroke(dp(3), accent) }
                setOnClickListener {
                    cur = p.id; curName = p.name
                    prefs.edit().putString("pilot-pc", p.id).putString("pilot-pc-nazwa", p.name).apply()   // zapamiętany także po ponownym uruchomieniu telefonu
                    lan.sendPilot("*", "ping", target = p.addr)
                    d.dismiss(); refresh()
                }
            }
            card0.addView(TextView(act).apply { text = (if (p.id == cur) "✔ " else "") + "💻 " + p.name; textSize = 21f; setTextColor(Color.WHITE); setTypeface(typeface, Typeface.BOLD) })
            val playing = st?.optString("st") == "play" || st?.optString("st") == "pause"
            card0.addView(TextView(act).apply {
                text = if (!online(p)) s(R.string.pilot_offline) else if (st == null) s(R.string.pilot_online) + " · " + s(R.string.pilot_pc_idle) else stText(p)
                textSize = 15f; setTextColor(if (playing) Color.parseColor("#7CE0A3") else dim); setPadding(0, dp(2), 0, 0)
            })
            val nm = st?.optString("name")?.ifBlank { null } ?: st?.optString("title")?.ifBlank { null }
            if (nm != null) card0.addView(TextView(act).apply { text = (if (st?.optString("kind") == "tv") "📺 " else "🎬 ") + nm; textSize = 18f; setTextColor(Color.WHITE); setPadding(0, dp(8), 0, 0) })
            st?.optString("now")?.ifBlank { null }?.let { n -> card0.addView(TextView(act).apply { text = "▶ $n"; textSize = 15f; setTextColor(dim); setPadding(0, dp(2), 0, 0) }) }
            box.addView(card0, LinearLayout.LayoutParams(-1, -2).apply { setMargins(0, dp(6), 0, dp(6)) })
        }
        box.addView(Button(act).apply { text = act.getString(android.R.string.cancel); isAllCaps = false; textSize = 17f; setTextColor(Color.WHITE)
            background = GradientDrawable().apply { setColor(card); cornerRadius = dp(14).toFloat() }; setOnClickListener { d.dismiss() } },
            LinearLayout.LayoutParams(-1, dp(56)).apply { topMargin = dp(10) })
        d.setContentView(ScrollView(act).apply { setBackgroundColor(bg); isFillViewport = true; addView(box) })
        d.show()
    }

    private var onList: ((JSONObject) -> Unit)? = null

    // prośba do wybranego komputera, gdy odtwarzacz nie musi być otwarty (listy, włączenie kanału / filmu)
    private fun ask(op: String, v: String? = null, n: Double = 0.0, q: String? = null, g: String? = null, sort: String? = null): Boolean {
        val c = cur ?: run { choosePc(); return false }
        val p = pcs[c]
        if (!reachable(p)) { android.widget.Toast.makeText(act, s(R.string.pilot_unavailable, curName), android.widget.Toast.LENGTH_LONG).show(); return false }
        return lan.sendPilot(c, op, v, n, p?.addr, q, g, sort)
    }

    // 📺 Telewizja / 🎬 Filmy z komputera: zakładki, grupa / gatunek, szukanie, stuknięcie = włącz na komputerze
    private fun browse(kind: String) {
        if (cur == null) { choosePc(); return }
        imgGen++
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
        // 📦 lista trzymana w telefonie (bez szukania): po otwarciu od razu, z komputera tylko gdy zmieniła się tam biblioteka / program TV (wersja z „plans”)
        fun cacheFile() = java.io.File(act.filesDir, "pilot-lista-" + (kind + "|" + mode + "|" + group + "|" + sort + "|" + cur).hashCode() + ".json")
        var pages = org.json.JSONArray(); var replay = false; var fresh0 = false; var asked = 0L; var busy = false
        fun askPage() { asked = System.currentTimeMillis(); busy = true; if (!ask("list", mode, page.toDouble(), query, group, sort)) { busy = false; info.text = s(R.string.pilot_unavailable, curName) } }
        load = { fresh ->
            if (fresh) {
                page = 0; list.removeAllViews(); imgGen++; pages = org.json.JSONArray(); fresh0 = true
                if (query.isEmpty()) runCatching {
                    val c0 = JSONObject(cacheFile().readText()); val a0 = c0.getJSONArray("pages")
                    replay = true; for (i in 0 until a0.length()) { val p = a0.getJSONObject(i); page = p.optInt("page"); onList?.invoke(p) }; replay = false; page = 0
                    if (a0.length() > 0 && pcLibv[kind] == c0.optInt("v")) fresh0 = false   // ta sama wersja co na komputerze - bez pytania
                }
            }
            if (fresh0 || !fresh) { info.text = s(R.string.pilot_loading); askPage() } else busy = false
        }
        fun watchdog() { h.postDelayed({ if (d.isShowing) { if (busy && System.currentTimeMillis() - asked > 4000) askPage(); watchdog() } }, 2000) }   // zgubiony pakiet - zapytaj ponownie
        watchdog()
        onList = { o ->
            if (!replay && o.optString("kind") == mode && o.optInt("page") == page && fresh0 && page == 0) { fresh0 = false; list.removeAllViews() }   // świeże z komputera zastępuje zapisane
            if (o.optString("kind") == mode && o.optInt("page") == page) {
                if (!replay) { busy = false; pages.put(o); o.optInt("libv").let { if (o.has("libv")) pcLibv[kind] = it } }
                val g = o.optJSONArray("groups"); if (g != null && g.length() > 0) { groups = (0 until g.length()).map { g.optString(it) }; drawTabs() }
                val items = o.optJSONArray("items"); val total = o.optInt("total")
                o.optJSONArray("keys")?.let { ka -> jfKeys = (0 until ka.length()).mapNotNull { ka.optJSONArray(it)?.let { p -> p.optString(0) to p.optString(1) } }.filter { it.first.isNotEmpty() } }
                list.findViewWithTag<View>("more")?.let { list.removeView(it) }
                for (k in 0 until (items?.length() ?: 0)) {
                    val it2 = items!!.getJSONArray(k); val idx = it2.optInt(0); val name = it2.optString(1); val sub = it2.optString(2)
                    var fav = it2.optBoolean(3); var img = it2.optString(4); val pcImg = img; var key: String? = null; var overview = ""
                    if (kind == "tv") img = pick(tvLogo[chKey(name)], pcImg)   // logo: pamięć telefonu, inaczej dysk komputera
                    else { val jc = jfConf; val ji = jfById[it2.optString(5)] ?: jfByName[norm(name) + "|" + it2.optInt(6)]
                        if (ji != null) overview = ji.overview
                        if (jc != null && ji != null) { val ph = Jelly.image(jc, ji.id); img = pick(ph, pcImg); if (img == ph) key = jc.key } }   // plakat z biblioteki telefonu (ten sam adres i klucz co Biblioteka)
                    val resumeAt = it2.optLong(7)
                    fun go(how: String) { if (ask(if (kind == "tv") "play-tv" else "play-film", name, idx.toDouble(), sort = how)) { android.widget.Toast.makeText(act, s(R.string.pilot_starting, name), android.widget.Toast.LENGTH_SHORT).show(); d.dismiss() } }
                    val play = {   // film przerwany: pytanie na telefonie (komputer nie czeka na kliknięcie)
                        if (kind == "film" && resumeAt > 0) AlertDialog.Builder(act).setTitle(name).setMessage(s(R.string.pilot_resume_q, fmt(resumeAt)))
                            .setPositiveButton("▶ " + s(R.string.pilot_resume, fmt(resumeAt))) { _, _ -> go("wznow") }
                            .setNegativeButton("⏮ " + s(R.string.pilot_from_start)) { _, _ -> go("poczatek") }.show()
                        else go("")
                    }
                    if (kind == "film" && !grid) {   // 🎬 lista: plakat po lewej, tytuł, rok/ocena, opis (jak Biblioteka)
                        val line = LinearLayout(act).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL; isClickable = true
                            setPadding(dp(6), dp(6), 0, dp(6)); background = GradientDrawable().apply { setColor(card); cornerRadius = dp(12).toFloat() }
                            setOnClickListener { play() }; setOnLongClickListener { ask("info", "film", idx.toDouble()); true } }
                        line.addView(ImageView(act).apply { scaleType = ImageView.ScaleType.CENTER_CROP; setBackgroundColor(Color.parseColor("#33363F")); loadImage(this, img, key, pcImg) }, LinearLayout.LayoutParams(dp(64), dp(96)))
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
                        ph.addView(ImageView(act).apply { scaleType = ImageView.ScaleType.CENTER_CROP; setBackgroundColor(Color.parseColor("#33363F")); loadImage(this, img, key, pcImg) }, android.widget.FrameLayout.LayoutParams(-1, -1))
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
                    line.addView(ImageView(act).apply { scaleType = ImageView.ScaleType.FIT_CENTER; setPadding(dp(4), dp(4), dp(4), dp(4)); loadImage(this, img, key, pcImg) }, LinearLayout.LayoutParams(dp(64), dp(48)).apply { marginEnd = dp(6) })   // 📺 logo kanału
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
                if (!replay) {
                    if (shown < total) { page++; info.text = s(R.string.pilot_loading) + " " + shown + " / " + total; askPage() }   // następna strona sama
                    else if (query.isEmpty()) { val save = JSONObject().put("v", o.optInt("libv")).put("pages", pages); val cf = cacheFile(); Thread { runCatching { cf.writeText(save.toString()) } }.start() }
                }
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
        d.setOnKeyListener { _, k, e -> volKey(k, e) }   // głośność komputera także na liście
        d.show()
        phoneData { load(true) }   // najpierw logo i plakaty z telefonu, potem lista
    }

    private fun hm(ms: Long) = java.text.SimpleDateFormat("HH:mm", java.util.Locale.getDefault()).format(java.util.Date(ms))
    private fun pillV(t: String, on: Boolean, f: () -> Unit) = TextView(act).apply {
        text = t; textSize = 15f; setTextColor(Color.WHITE); isSingleLine = true; gravity = Gravity.CENTER; isClickable = true
        setPadding(dp(14), dp(9), dp(14), dp(9)); background = GradientDrawable().apply { setColor(if (on) accent else card); cornerRadius = dp(20).toFloat() }
        setOnClickListener { f() }
    }
    private fun screen(title: String): Triple<Dialog, LinearLayout, LinearLayout> {
        imgGen++   // nowy ekran - stara kolejka obrazków pomijana
        val d = Dialog(act, android.R.style.Theme_Black_NoTitleBar_Fullscreen)
        val box = LinearLayout(act).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(12), dp(12), dp(12), dp(12)); setBackgroundColor(bg) }
        val head = LinearLayout(act).apply { gravity = Gravity.CENTER_VERTICAL }
        head.addView(Button(act).apply { text = "✕"; textSize = 20f; setTextColor(Color.WHITE); background = null; setOnClickListener { d.dismiss() } }, LinearLayout.LayoutParams(dp(56), dp(56)))
        head.addView(TextView(act).apply { text = title; textSize = 19f; setTextColor(Color.WHITE); setTypeface(typeface, Typeface.BOLD) })
        box.addView(head)
        val tabs = LinearLayout(act)
        box.addView(android.widget.HorizontalScrollView(act).apply { isHorizontalScrollBarEnabled = false; addView(tabs) }, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(8) })
        d.setContentView(box); d.setOnKeyListener { _, k, e -> volKey(k, e) }
        d.setOnDismissListener { onList = null }
        return Triple(d, box, tabs)
    }
    private fun tabLp() = LinearLayout.LayoutParams(-2, -2).apply { marginEnd = dp(8) }

    // 📅 Program TV: siatka rysowana wprost (jeden widok) - logo z lewej (stałe), godziny u góry, przesuwanie palcem w bok, zielona linia „teraz”
    private class EpgRow(val ci: Int, val name: String, val logo: String, val progs: List<LongArray>, val titles: List<String>, val flags: List<String>) { var bmp: android.graphics.Bitmap? = null }
    private inner class EpgView : View(act) {
        val rows = ArrayList<EpgRow>(); var from = 0L; var to = 0L
        val perMin = dp(2).toFloat(); val logoW = dp(80).toFloat(); val rowH = dp(60).toFloat(); val axisH = dp(24).toFloat(); val gap = dp(3).toFloat()
        var scrollXf = 0f; var onProg: ((EpgRow, Long) -> Unit)? = null; var onLogo: ((EpgRow) -> Unit)? = null
        private val p = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG)
        private val tp = android.text.TextPaint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE; textSize = dp(14).toFloat(); typeface = Typeface.DEFAULT_BOLD }
        private val sp = android.text.TextPaint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply { color = dim; textSize = dp(12).toFloat() }
        private val r = android.graphics.RectF()
        fun maxScroll() = maxOf(0f, (to - from) / 60000f * perMin - (width - logoW))
        override fun onMeasure(w: Int, h: Int) = setMeasuredDimension(MeasureSpec.getSize(w), (axisH + rows.size * rowH).toInt())
        override fun onDraw(c: android.graphics.Canvas) {
            val now = System.currentTimeMillis()
            c.save(); c.clipRect(logoW, 0f, width.toFloat(), height.toFloat()); c.translate(logoW - scrollXf, 0f)
            var t = (from / 3_600_000L + 1) * 3_600_000L; t -= java.util.TimeZone.getDefault().getOffset(t) % 3_600_000L; if (t - 3_600_000L >= from) t -= 3_600_000L
            while (t < to) { c.drawText(hm(t), (t - from) / 60000f * perMin + dp(2), axisH - dp(8), sp); t += 3_600_000L }
            rows.forEachIndexed { i, row ->
                val y = axisH + i * rowH
                row.progs.forEachIndexed { j, pr ->
                    val x0 = (maxOf(pr[0], from) - from) / 60000f * perMin; val x1 = (minOf(pr[1], to) - from) / 60000f * perMin
                    if (x1 - x0 < dp(4) || x1 < scrollXf - 10 || x0 > scrollXf + width) return@forEachIndexed
                    r.set(x0, y + gap, x1 - gap, y + rowH - gap)
                    p.style = android.graphics.Paint.Style.FILL; p.color = if (now in pr[0] until pr[1]) Color.parseColor("#3A2C6E") else card
                    c.drawRoundRect(r, dp(8).toFloat(), dp(8).toFloat(), p)
                    val fl = row.flags[j]
                    if (fl == "rec" || fl == "plan") { p.style = android.graphics.Paint.Style.STROKE; p.strokeWidth = dp(2).toFloat(); p.color = Color.parseColor(if (fl == "rec") "#E5484D" else "#F5C518"); c.drawRoundRect(r, dp(8).toFloat(), dp(8).toFloat(), p) }
                    val tx = maxOf(x0, scrollXf) + dp(7); val avail = x1 - gap - tx - dp(4)
                    if (avail > dp(14)) {
                        c.drawText(android.text.TextUtils.ellipsize((if (fl == "rec") "⏺ " else if (fl == "plan") "⏰ " else "") + row.titles[j], tp, avail, TextUtils.TruncateAt.END).toString(), tx, y + gap + dp(21), tp)
                        c.drawText(android.text.TextUtils.ellipsize(hm(pr[0]) + "–" + hm(pr[1]), sp, avail, TextUtils.TruncateAt.END).toString(), tx, y + gap + dp(40), sp)
                    }
                }
            }
            if (now in from until to) { p.style = android.graphics.Paint.Style.FILL; p.color = Color.parseColor("#7CE0A3"); val x = (now - from) / 60000f * perMin; c.drawRect(x - dp(1), axisH - dp(4), x + dp(1), height.toFloat(), p) }
            c.restore()
            rows.forEachIndexed { i, row ->   // logo (stała kolumna)
                val y = axisH + i * rowH
                r.set(0f, y + gap, logoW - dp(6), y + rowH - gap); p.style = android.graphics.Paint.Style.FILL; p.color = Color.WHITE
                c.drawRoundRect(r, dp(8).toFloat(), dp(8).toFloat(), p)
                val b = row.bmp
                if (b != null) {   // logo u góry, pod nim mała nazwa kanału (FHD / HD / SD się różnią)
                    val np = android.text.TextPaint(tp).apply { color = Color.BLACK; textSize = dp(10).toFloat(); textAlign = android.graphics.Paint.Align.CENTER }
                    val nameH = dp(13).toFloat(); val box = android.graphics.RectF(r.left + dp(4), r.top + dp(3), r.right - dp(4), r.bottom - nameH - dp(2))
                    val s0 = minOf(box.width() / b.width, box.height() / b.height); val w = b.width * s0; val h = b.height * s0
                    c.drawBitmap(b, null, android.graphics.RectF(box.centerX() - w / 2, box.centerY() - h / 2, box.centerX() + w / 2, box.centerY() + h / 2), p)
                    c.drawText(android.text.TextUtils.ellipsize(row.name, np, r.width() - dp(4), TextUtils.TruncateAt.END).toString(), r.centerX(), r.bottom - dp(5), np) }
                else { val bp = android.text.TextPaint(tp).apply { color = Color.BLACK; textSize = dp(11).toFloat() }
                    val lay = android.text.StaticLayout.Builder.obtain(row.name, 0, row.name.length, bp, (r.width() - dp(6)).toInt()).setMaxLines(3).setEllipsize(TextUtils.TruncateAt.END).setAlignment(android.text.Layout.Alignment.ALIGN_CENTER).build()
                    c.save(); c.translate(r.left + dp(3), r.centerY() - lay.height / 2f); lay.draw(c); c.restore() }
            }
        }
        private var downX = 0f; private var downY = 0f; private var startScroll = 0f; private var dragging = false
        override fun onTouchEvent(e: android.view.MotionEvent): Boolean {
            when (e.actionMasked) {
                android.view.MotionEvent.ACTION_DOWN -> { downX = e.x; downY = e.y; startScroll = scrollXf; dragging = false }
                android.view.MotionEvent.ACTION_MOVE -> {
                    val dx = e.x - downX
                    if (!dragging && kotlin.math.abs(dx) > dp(10) && kotlin.math.abs(dx) > kotlin.math.abs(e.y - downY)) { dragging = true; parent?.requestDisallowInterceptTouchEvent(true) }
                    if (dragging) { scrollXf = (startScroll - dx).coerceIn(0f, maxScroll()); invalidate() }
                }
                android.view.MotionEvent.ACTION_UP -> if (!dragging && kotlin.math.abs(e.x - downX) < dp(10) && kotlin.math.abs(e.y - downY) < dp(10)) {
                    val i = ((e.y - axisH) / rowH).toInt(); val row = rows.getOrNull(i)
                    if (row != null && e.y >= axisH) {
                        if (e.x < logoW) onLogo?.invoke(row)
                        else { val t = from + ((e.x - logoW + scrollXf) / perMin * 60000).toLong(); row.progs.firstOrNull { t >= it[0] && t < it[1] }?.let { onProg?.invoke(row, it[0]) } }
                    }
                }
            }
            return true
        }
    }

    private fun guide() {
        if (cur == null) { choosePc(); return }
        val (d, box, tabs) = screen("📅 " + s(R.string.pilot_guide) + " · " + curName)
        var mode = prefs.getString("pilot-epg-tryb", "fav") ?: "fav"; var group = prefs.getString("pilot-epg-grupa", "") ?: ""; var day = 0; var page = 0
        var groups = listOf<String>()
        var sort = prefs.getString("pilot-epg-sort", "") ?: ""   // zapamiętane sortowanie kanałów
        val sorts = listOf("" to s(R.string.pilot_sort_list), "name" to s(R.string.pilot_sort_az), "name-desc" to s(R.string.pilot_sort_za))
        val info = TextView(act).apply { textSize = 14f; setTextColor(dim); setPadding(dp(4), 0, dp(4), dp(6)) }
        box.addView(info)
        val grid = EpgView()
        box.addView(ScrollView(act).apply { addView(grid, android.widget.FrameLayout.LayoutParams(-1, -2)) }, LinearLayout.LayoutParams(-1, 0, 1f))
        box.addView(TextView(act).apply { textSize = 12f; setTextColor(dim); setPadding(dp(4), dp(6), dp(4), 0)
            text = android.text.SpannableStringBuilder(s(R.string.pilot_epg_hint) + "\n").apply {
                fun leg(c: Int, t: String) { val a = length; append("■"); setSpan(android.text.style.ForegroundColorSpan(c), a, length, 0); append(" $t   ") }
                leg(Color.parseColor("#3A2C6E"), s(R.string.pilot_leg_now)); leg(Color.parseColor("#F5C518"), s(R.string.pilot_leg_plan)); leg(Color.parseColor("#E5484D"), s(R.string.pilot_leg_rec)); leg(Color.parseColor("#7CE0A3"), s(R.string.pilot_leg_now) + " (—)") } })
        lateinit var load: (Boolean) -> Unit
        fun drawTabs() {
            tabs.removeAllViews()
            tabs.addView(pillV("⭐ " + s(R.string.pilot_fav), mode == "fav") { mode = "fav"; prefs.edit().putString("pilot-epg-tryb", mode).apply(); drawTabs(); load(true) }, tabLp())
            tabs.addView(pillV(s(R.string.pilot_group) + ": " + group.ifBlank { s(R.string.pilot_any) } + " ▾", mode == "tv") {
                val names = listOf(s(R.string.pilot_any)) + groups
                AlertDialog.Builder(act).setItems(names.toTypedArray()) { _, i -> mode = "tv"; group = if (i == 0) "" else groups[i - 1]
                    prefs.edit().putString("pilot-epg-tryb", mode).putString("pilot-epg-grupa", group).apply(); drawTabs(); load(true) }.show()
            }, tabLp())
            val days = (0..6).map { i -> when (i) { 0 -> s(R.string.pilot_today); 1 -> s(R.string.pilot_tomorrow); else -> java.text.SimpleDateFormat("EEE d.MM", java.util.Locale.getDefault()).format(java.util.Date(System.currentTimeMillis() + i * 86_400_000L)) } }
            tabs.addView(pillV(days[day] + " ▾", false) { AlertDialog.Builder(act).setItems(days.toTypedArray()) { _, i -> day = i; drawTabs(); load(true) }.show() }, tabLp())
            tabs.addView(pillV("⇅ " + (sorts.firstOrNull { it.first == sort }?.second ?: sorts[0].second), false) {
                AlertDialog.Builder(act).setTitle(R.string.pilot_sort).setItems(sorts.map { it.second }.toTypedArray()) { _, i -> sort = sorts[i].first; prefs.edit().putString("pilot-epg-sort", sort).apply(); drawTabs(); load(true) }.show()
            }, tabLp())
            tabs.addView(pillV("⏰ " + s(R.string.pilot_plans), false) { plans() }, tabLp())
        }
        var keepScroll = false
        // 📦 program TV trzymany w telefonie (plik na wybór: ulubione/grupa/dzień/sortowanie) - po otwarciu od razu, a z komputera dociąga się sam, wszystkie strony po kolei
        fun cacheFile() = java.io.File(act.filesDir, "pilot-epg-" + (mode + "|" + group + "|" + day + "|" + sort + "|" + cur).hashCode() + ".json")
        var fresh0 = false; var pages = org.json.JSONArray(); var asked = 0L; var force = false
        fun render(o: JSONObject) {
            o.optJSONArray("groups")?.let { g -> if (g.length() > 0) { groups = (0 until g.length()).map { g.optString(it) }; drawTabs() } }
            grid.from = o.optLong("t0"); grid.to = o.optLong("t1")
            val chans = o.optJSONArray("chans")
            for (k in 0 until (chans?.length() ?: 0)) {
                val c = chans!!.getJSONArray(k); val ps = c.optJSONArray(3)
                val pr = ArrayList<LongArray>(); val ti = ArrayList<String>(); val fl = ArrayList<String>()
                for (j in 0 until (ps?.length() ?: 0)) { val p = ps!!.getJSONArray(j); pr.add(longArrayOf(p.optLong(0), p.optLong(1))); ti.add(p.optString(2)); fl.add(p.optString(3)) }
                val name = c.optString(1)
                val row = EpgRow(c.optInt(0), name, pick(tvLogo[chKey(name)], c.optString(2)), pr, ti, fl)
                grid.rows.add(row)
                loadBmp(row.logo, null) { b -> if (b != null) { row.bmp = b; grid.invalidate() } }
            }
            if (!keepScroll) { keepScroll = true; grid.post { grid.scrollXf = ((System.currentTimeMillis() - 30 * 60000 - grid.from) / 60000f * grid.perMin).coerceIn(0f, grid.maxScroll()); grid.invalidate() } }   // od pół godziny przed „teraz”
            grid.requestLayout(); grid.invalidate()
        }
        fun askPage() { asked = System.currentTimeMillis()
            if (!ask("epg", mode, page.toDouble(), day.toString(), if (mode == "tv") group else "", sort)) info.text = s(R.string.pilot_unavailable, curName) }
        load = { fresh ->
            if (fresh) {
                page = 0; grid.rows.clear(); grid.scrollXf = 0f; keepScroll = false; imgGen++; fresh0 = true; pages = org.json.JSONArray()
                var cv: Int? = null
                runCatching { val c0 = JSONObject(cacheFile().readText()); cv = c0.optInt("v"); val a0 = c0.getJSONArray("pages"); for (i in 0 until a0.length()) render(a0.getJSONObject(i)) }   // zapisany program - od razu
                grid.requestLayout(); grid.invalidate()
                val same = pcEpgv?.let { it == cv } ?: (System.currentTimeMillis() - cacheFile().lastModified() < 3 * 3600_000L)   // ta sama wersja co na komputerze (nie wiadomo - do 3 h)
                if (!force && grid.rows.isNotEmpty() && same) { fresh0 = false; info.visibility = View.GONE }   // aktualny - bez pytania komputera
                force = false
            }
            if (fresh0 || page > 0 || info.visibility != View.GONE) { info.text = s(R.string.pilot_loading); info.visibility = View.VISIBLE; askPage() }
        }
        fun watchdog() { h.postDelayed({ if (d.isShowing) { if (System.currentTimeMillis() - asked > 4000 && (fresh0 || pages.length() > 0) && info.visibility == View.VISIBLE) askPage(); watchdog() } }, 2000) }   // zgubiony pakiet - zapytaj ponownie
        grid.onProg = { row, st -> ask("epg-act", "info", row.ci.toDouble(), st.toString(), row.name) }
        grid.onLogo = { row -> if (ask("epg-act", "play", row.ci.toDouble(), g = row.name)) android.widget.Toast.makeText(act, s(R.string.pilot_starting, row.name), android.widget.Toast.LENGTH_SHORT).show() }
        onList = { o ->
            when (o.optString("kind")) {
                "epg" -> if (o.optBoolean("wait")) { info.text = s(R.string.pilot_epg_wait); asked = System.currentTimeMillis() }
                    else if (o.optInt("page") == page) {
                    if (fresh0) { fresh0 = false; grid.rows.clear() }   // świeże dane z komputera zastępują zapisane
                    render(o); pages.put(o)
                    val total = o.optInt("total")
                    if ((page + 1) * 12 < total) { page++; info.text = s(R.string.pilot_loading) + " " + minOf(total, page * 12) + " / " + total; askPage() }   // następna strona sama
                    else {
                        info.text = if (total == 0) s(R.string.pilot_epg_empty) else ""; info.visibility = if (total == 0) View.VISIBLE else View.GONE
                        val save = JSONObject().put("v", o.optInt("epgv")).put("pages", pages); val cf = cacheFile(); Thread { runCatching { cf.writeText(save.toString()) } }.start()
                    }
                }
                "epg-info" -> progSheet(o) { force = true; load(true) }   // po planie / nagraniu - od nowa z komputera (znaczniki ⏰ ⏺)
            }
        }
        watchdog()
        onEpgv = { _ -> if (d.isShowing && info.visibility == View.GONE) { force = true; load(true) } }   // komputer wczytał nowy program wg swojego harmonogramu - telefon dociąga sam
        d.setOnDismissListener { onList = null; onEpgv = null }
        drawTabs(); d.show()
        phoneData { load(true) }
    }

    // stuknięcie w program: opis i czynności (komputer: włącz, przełącz o godzinie, nagraj, archiwum)
    private fun progSheet(o: JSONObject, reload: () -> Unit) {
        val ch = o.optInt("ch"); val st = o.optLong("start"); val sp = o.optLong("stop"); val now = System.currentTimeMillis()
        val d = Dialog(act, android.R.style.Theme_Black_NoTitleBar)
        val box = LinearLayout(act).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(18), dp(18), dp(18), dp(22))
            background = GradientDrawable().apply { setColor(Color.parseColor("#1D1F25")); cornerRadii = floatArrayOf(dp(22).toFloat(), dp(22).toFloat(), dp(22).toFloat(), dp(22).toFloat(), 0f, 0f, 0f, 0f) } }
        box.addView(TextView(act).apply { text = o.optString("title"); textSize = 20f; setTextColor(Color.WHITE); setTypeface(typeface, Typeface.BOLD) })
        val dayTxt = if (java.text.SimpleDateFormat("yyyyMMdd", java.util.Locale.ROOT).let { f -> f.format(java.util.Date(st)) == f.format(java.util.Date(now)) }) s(R.string.pilot_today).lowercase()
            else java.text.SimpleDateFormat("EEE d.MM", java.util.Locale.getDefault()).format(java.util.Date(st))
        box.addView(TextView(act).apply { text = o.optString("chName") + " · " + dayTxt + " " + hm(st) + "–" + hm(sp); textSize = 14f; setTextColor(dim); setPadding(0, dp(2), 0, dp(4)) })
        o.optString("desc").ifBlank { null }?.let { box.addView(ScrollView(act).apply { addView(TextView(act).apply { text = it; textSize = 14f; setTextColor(dim) }) }, LinearLayout.LayoutParams(-1, -2).apply { height = -2 }) }
        fun act0(t: String, prim: Boolean, f: () -> Unit) = box.addView(TextView(act).apply {
            text = t; textSize = 16f; setTextColor(Color.WHITE); setPadding(dp(16), dp(14), dp(16), dp(14)); isClickable = true
            background = GradientDrawable().apply { setColor(if (prim) accent else card); cornerRadius = dp(14).toFloat() }
            setOnClickListener { f(); d.dismiss() }
        }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(8) })
        fun done(op: String) { if (ask("epg-act", op, ch.toDouble(), st.toString(), o.optString("chName"))) { android.widget.Toast.makeText(act, s(R.string.pilot_sent), android.widget.Toast.LENGTH_SHORT).show(); h.postDelayed({ reload() }, 700) } }
        act0("▶  " + s(R.string.pilot_play_now), true) { if (ask("epg-act", "play", ch.toDouble(), g = o.optString("chName"))) android.widget.Toast.makeText(act, s(R.string.pilot_starting, o.optString("chName")), android.widget.Toast.LENGTH_SHORT).show() }
        val job = o.optString("job"); val jobId = o.optString("jobId")
        if (job == "switch") act0("✕  " + s(R.string.pilot_cancel_plan), false) { ask("plan-del", q = jobId); h.postDelayed({ reload() }, 700) }
        else if (st > now) act0("⏰  " + s(R.string.pilot_switch_at, hm(st)), false) { done("switch") }
        if (job == "rec") act0("✕  " + s(R.string.pilot_cancel_rec), false) { ask("plan-del", q = jobId); h.postDelayed({ reload() }, 700) }
        else if (sp > now) act0("⏺  " + s(R.string.pilot_rec, hm(st) + "–" + hm(sp)), false) { done("rec") }
        if (o.optBoolean("arch")) act0("⏪  " + s(R.string.pilot_archive), false) { done("arch") }
        d.setContentView(box)
        d.window?.apply { setGravity(Gravity.BOTTOM); setLayout(-1, -2); setBackgroundDrawable(android.graphics.drawable.ColorDrawable(Color.TRANSPARENT)) }
        d.setOnKeyListener { _, k, e -> volKey(k, e) }
        d.show()
    }

    // ⏰ zaplanowane przełączenia i nagrania na komputerze, ✕ usuwa
    private fun plans() {
        if (cur == null) { choosePc(); return }
        val (d, box, _) = screen("⏰ " + s(R.string.pilot_plans) + " · " + curName)
        val list = LinearLayout(act).apply { orientation = LinearLayout.VERTICAL }
        box.addView(ScrollView(act).apply { addView(list) }, LinearLayout.LayoutParams(-1, 0, 1f))
        val prev = onList
        onList = { o -> if (o.optString("kind") == "plans") {
            list.removeAllViews(); val a = o.optJSONArray("items")
            if (a == null || a.length() == 0) list.addView(TextView(act).apply { text = s(R.string.pilot_plans_empty); textSize = 16f; setTextColor(dim); setPadding(dp(8), dp(12), 0, 0) })
            for (i in 0 until (a?.length() ?: 0)) {
                val it2 = a!!.getJSONArray(i); val rec = it2.optString(1) == "rec"; val id = it2.optString(0)
                val line = LinearLayout(act).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(14), dp(10), dp(4), dp(10))
                    background = GradientDrawable().apply { setColor(card); cornerRadius = dp(14).toFloat(); setStroke(dp(2), Color.parseColor(if (rec) "#E5484D" else "#F5C518")) } }
                line.addView(TextView(act).apply { textSize = 15f; setTextColor(Color.WHITE)
                    text = (if (rec) "⏺ " + hm(it2.optLong(4)) + "–" + hm(it2.optLong(5)) else "⏰ " + hm(it2.optLong(4))) + "  ·  " + it2.optString(2) + "\n" + it2.optString(3) +
                        (if (it2.optBoolean(6)) "  🔴 " + s(R.string.pilot_recording_now) else "") }, LinearLayout.LayoutParams(0, -2, 1f))
                line.addView(Button(act).apply { text = "✕"; textSize = 20f; setTextColor(Color.WHITE); background = null
                    contentDescription = s(if (rec) R.string.pilot_cancel_rec else R.string.pilot_cancel_plan); setOnClickListener { ask("plan-del", q = id) } }, LinearLayout.LayoutParams(dp(52), dp(52)))
                list.addView(line, LinearLayout.LayoutParams(-1, -2).apply { setMargins(0, dp(4), 0, dp(4)) })
            }
        } else prev?.invoke(o) }
        d.setOnDismissListener { onList = prev }
        d.show(); ask("plans")
    }

    // 🎲 Co dziś obejrzeć: od razu 3 propozycje z biblioteki komputera (te same zasady co losowanie w Kanapie) - plakat, opis, ▶ i ☆ przy każdej
    private fun randomPick() {
        if (cur == null) { choosePc(); return }
        val (d, box, tabs) = screen("🎲 " + s(R.string.pilot_rand))
        var genre = prefs.getString("pilot-los-gatunek", "") ?: ""; var len = prefs.getString("pilot-los-dlugosc", "") ?: ""; var unseen = prefs.getBoolean("pilot-los-nowe", true)
        var genres = listOf<String>()
        val lens = listOf("" to s(R.string.pilot_len_any), "krotki" to s(R.string.pilot_len_short), "sredni" to s(R.string.pilot_len_mid), "dlugi" to s(R.string.pilot_len_long))
        val list = LinearLayout(act).apply { orientation = LinearLayout.VERTICAL }
        box.addView(ScrollView(act).apply { addView(list) }, LinearLayout.LayoutParams(-1, 0, 1f))
        lateinit var roll: () -> Unit
        box.addView(TextView(act).apply { text = "🎲  " + s(R.string.pilot_roll); textSize = 17f; setTextColor(Color.WHITE); gravity = Gravity.CENTER; setPadding(0, dp(16), 0, dp(16)); isClickable = true
            background = GradientDrawable().apply { setColor(accent); cornerRadius = dp(16).toFloat() }; setOnClickListener { roll() } }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(8) })
        fun drawTabs() {
            tabs.removeAllViews()
            tabs.addView(pillV(s(R.string.pilot_genre) + ": " + genre.ifBlank { s(R.string.pilot_genre_any) } + " ▾", false) {
                AlertDialog.Builder(act).setItems((listOf(s(R.string.pilot_genre_any)) + genres).toTypedArray()) { _, i -> genre = if (i == 0) "" else genres[i - 1]; prefs.edit().putString("pilot-los-gatunek", genre).apply(); drawTabs(); roll() }.show() }, tabLp())
            tabs.addView(pillV(s(R.string.pilot_len) + (if (len.isEmpty()) "" else ": " + lens.first { it.first == len }.second) + " ▾", false) {
                AlertDialog.Builder(act).setItems(lens.map { it.second }.toTypedArray()) { _, i -> len = lens[i].first; prefs.edit().putString("pilot-los-dlugosc", len).apply(); drawTabs(); roll() }.show() }, tabLp())
            tabs.addView(pillV(s(R.string.pilot_unseen), unseen) { unseen = !unseen; prefs.edit().putBoolean("pilot-los-nowe", unseen).apply(); drawTabs(); roll() }, tabLp())
        }
        fun card(it2: org.json.JSONArray): View {
            val idx = it2.optInt(0); val name = it2.optString(1); var fav = it2.optBoolean(3); val resumeAt = it2.optLong(7)
            val jc = jfConf; val ji = jfById[it2.optString(5)] ?: jfByName[norm(name) + "|" + it2.optInt(6)]
            val v = LinearLayout(act).apply { setPadding(dp(10), dp(10), dp(10), dp(10)); background = GradientDrawable().apply { setColor(card); cornerRadius = dp(16).toFloat() } }
            val poster = ImageView(act).apply { scaleType = ImageView.ScaleType.CENTER_CROP; setBackgroundColor(Color.parseColor("#33363F")) }
            val ph = if (jc != null && ji != null) Jelly.image(jc, ji.id) else null; val first = pick(ph, it2.optString(4))
            loadImage(poster, first, if (first == ph) jc?.key else null, if (first == ph) it2.optString(4) else ph ?: "")
            v.addView(poster, LinearLayout.LayoutParams(dp(110), dp(165)))
            val col = LinearLayout(act).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(12), 0, 0, 0) }
            col.addView(TextView(act).apply { text = name; textSize = 18f; setTextColor(Color.WHITE); setTypeface(typeface, Typeface.BOLD); maxLines = 2; ellipsize = TextUtils.TruncateAt.END })
            col.addView(TextView(act).apply { text = it2.optString(2); textSize = 13f; setTextColor(Color.WHITE); setPadding(0, dp(2), 0, dp(2)) })
            col.addView(TextView(act).apply { text = ji?.overview?.ifBlank { null } ?: it2.optString(8); textSize = 13f; setTextColor(dim); maxLines = 4; ellipsize = TextUtils.TruncateAt.END }, LinearLayout.LayoutParams(-1, 0, 1f))
            val btns = LinearLayout(act)
            fun go(how: String) { if (ask("play-film", name, idx.toDouble(), sort = how)) { android.widget.Toast.makeText(act, s(R.string.pilot_starting, name), android.widget.Toast.LENGTH_SHORT).show(); d.dismiss() } }
            btns.addView(TextView(act).apply { text = "▶ " + s(R.string.pilot_watch_pc); textSize = 14f; setTextColor(Color.WHITE); gravity = Gravity.CENTER; setPadding(dp(6), dp(10), dp(6), dp(10)); isClickable = true
                background = GradientDrawable().apply { setColor(accent); cornerRadius = dp(12).toFloat() }
                setOnClickListener {
                    if (resumeAt > 0) AlertDialog.Builder(act).setTitle(name).setMessage(s(R.string.pilot_resume_q, fmt(resumeAt)))
                        .setPositiveButton("▶ " + s(R.string.pilot_resume, fmt(resumeAt))) { _, _ -> go("wznow") }
                        .setNegativeButton("⏮ " + s(R.string.pilot_from_start)) { _, _ -> go("poczatek") }.show()
                    else go("")
                } }, LinearLayout.LayoutParams(0, -2, 1f))
            btns.addView(TextView(act).apply { text = if (fav) "★" else "☆"; textSize = 24f; setTextColor(Color.parseColor("#F5C518")); gravity = Gravity.CENTER; isClickable = true; contentDescription = s(R.string.pilot_fav)
                setOnClickListener { if (ask("fav-film", name, idx.toDouble())) { fav = !fav; text = if (fav) "★" else "☆"
                    android.widget.Toast.makeText(act, s(if (fav) R.string.pilot_fav_added else R.string.pilot_fav_removed, name), android.widget.Toast.LENGTH_SHORT).show() } } }, LinearLayout.LayoutParams(dp(48), dp(44)))
            col.addView(btns, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(6) })
            v.addView(col, LinearLayout.LayoutParams(0, -1, 1f))
            v.setOnLongClickListener { ask("info", "film", idx.toDouble()); true }
            return v
        }
        roll = { list.removeAllViews(); imgGen++
            list.addView(TextView(act).apply { text = s(R.string.pilot_loading); textSize = 15f; setTextColor(dim); setPadding(dp(6), dp(12), 0, 0) })
            ask("rand", "", 0.0, if (unseen) "1" else "", genre, len) }
        onList = { o -> if (o.optString("kind") == "rand") {
            o.optJSONArray("keys")?.let { ka -> jfKeys = (0 until ka.length()).mapNotNull { ka.optJSONArray(it)?.let { p -> p.optString(0) to p.optString(1) } }.filter { it.first.isNotEmpty() } }
            o.optJSONArray("genres")?.let { g -> genres = (0 until g.length()).map { g.optString(it) } }
            drawTabs(); list.removeAllViews()
            val a = o.optJSONArray("items")
            if (a == null || a.length() == 0) list.addView(TextView(act).apply { text = s(R.string.pilot_rand_empty); textSize = 16f; setTextColor(dim); setPadding(dp(6), dp(12), 0, 0) })
            for (i in 0 until (a?.length() ?: 0)) list.addView(card(a!!.getJSONArray(i)), LinearLayout.LayoutParams(-1, -2).apply { setMargins(0, dp(5), 0, dp(5)) })
        } }
        drawTabs(); d.show()
        phoneData { roll() }
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
