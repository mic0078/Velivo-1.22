package pl.velivo.komunikator

// Ochrona przed oszustwami (offline, jak Ochrona.cs w Velivo na Windows): strony-podróbki znanych firm i banków -
// marka w adresie na obcej domenie, literówki i podmienione znaki, adresy xn-- (znaki udające litery)
// oraz podróbki stron, do których masz zapisane hasła. Wspólna dla przeglądarki i komunikatora (🛡 linki w wiadomościach).
object Ochrona {
    // marka -> prawdziwe domeny (domena rejestrowana)
    private val brands = linkedMapOf(
        "paypal" to listOf("paypal.com", "paypal.me", "paypalobjects.com"),
        "ebay" to listOf("ebay.com", "ebay.co.uk", "ebay.pl", "ebay.de", "ebay.fr", "ebay.it", "ebay.es", "ebayimg.com"),
        "amazon" to listOf("amazon.com", "amazon.co.uk", "amazon.pl", "amazon.de", "amazon.fr", "amazon.it", "amazon.es", "amazonpay.com", "media-amazon.com"),
        "facebook" to listOf("facebook.com", "fb.com", "fbcdn.net", "facebook.net"),
        "instagram" to listOf("instagram.com"),
        "whatsapp" to listOf("whatsapp.com", "whatsapp.net"),
        "google" to listOf("google.com", "google.co.uk", "google.pl", "googleusercontent.com", "googleapis.com", "youtube.com", "gmail.com",
            "googleadservices.com", "googlesyndication.com", "googletagmanager.com", "google-analytics.com", "googlevideo.com", "gstatic.com", "doubleclick.net"),   // własne domeny Google - nie podróbki
        "gmail" to listOf("gmail.com", "google.com"),
        "microsoft" to listOf("microsoft.com", "microsoftonline.com", "live.com", "outlook.com", "office.com", "office365.com", "msn.com", "bing.com", "skype.com", "sharepoint.com", "onedrive.com", "xbox.com"),
        "outlook" to listOf("outlook.com", "live.com", "office.com", "microsoft.com"),
        "apple" to listOf("apple.com", "icloud.com"),
        "icloud" to listOf("icloud.com", "apple.com"),
        "twitter" to listOf("twitter.com", "x.com", "twimg.com"),
        "netflix" to listOf("netflix.com"),
        "spotify" to listOf("spotify.com"),
        "allegro" to listOf("allegro.pl", "allegrolokalnie.pl"),
        "olx" to listOf("olx.pl", "olx.ua", "olx.ro", "olx.pt", "olx.bg"),
        "vinted" to listOf("vinted.pl", "vinted.co.uk", "vinted.com", "vinted.fr", "vinted.de"),
        "inpost" to listOf("inpost.pl", "inpost.co.uk", "inpost.eu"),
        "pkobp" to listOf("pkobp.pl", "ipko.pl"),
        "ipko" to listOf("ipko.pl", "pkobp.pl"),
        "mbank" to listOf("mbank.pl"),
        "santander" to listOf("santander.pl", "santander.co.uk", "santander.com"),
        "pekao" to listOf("pekao.com.pl", "pekao24.pl"),
        "millennium" to listOf("bankmillennium.pl"),
        "revolut" to listOf("revolut.com", "revolut.me"),
        "monzo" to listOf("monzo.com", "monzo.me"),
        "starling" to listOf("starlingbank.com"),
        "barclays" to listOf("barclays.co.uk", "barclays.com", "barclaycard.co.uk"),
        "hsbc" to listOf("hsbc.co.uk", "hsbc.com"),
        "lloyds" to listOf("lloydsbank.com", "lloydsbank.co.uk", "lloydsbankinggroup.com"),
        "natwest" to listOf("natwest.com"),
        "halifax" to listOf("halifax.co.uk", "halifax-online.co.uk"),
        "nationwide" to listOf("nationwide.co.uk"),
        "royalmail" to listOf("royalmail.com"),
        "evri" to listOf("evri.com"),
        "hmrc" to listOf("gov.uk"),
        "dvla" to listOf("gov.uk"),
        "binance" to listOf("binance.com"),
        "coinbase" to listOf("coinbase.com"),
    )
    private val official = brands.values.flatten().toHashSet()
    private val twoLevel = setOf("co.uk", "org.uk", "gov.uk", "ac.uk", "me.uk", "ltd.uk", "plc.uk", "com.pl", "net.pl", "org.pl", "gov.pl", "edu.pl",
        "com.au", "net.au", "co.nz", "co.jp", "com.br", "co.in", "com.tr", "com.ua", "co.za", "com.mx", "com.ar", "com.cn")

    fun regDomain(host: String): String {
        val h = host.trim().trimEnd('.').lowercase()
        val p = h.split('.')
        if (p.size <= 2) return h
        val last2 = p[p.size - 2] + "." + p[p.size - 1]
        return if (last2 in twoLevel) p[p.size - 3] + "." + last2 else last2
    }

    private fun label(reg: String) = reg.substringBefore('.')

    // 0->o, 1->l, rn->m, vv->w ... - typowe podmiany w podróbkach
    private fun deconfuse(s: String): String {
        val map = mapOf('0' to 'o', '1' to 'l', '3' to 'e', '4' to 'a', '5' to 's', '7' to 't', '8' to 'b', '9' to 'g', 'i' to 'l')
        return s.lowercase().replace("rn", "m").replace("vv", "w").replace("cl", "d").map { map[it] ?: it }.joinToString("")
    }

    private fun editDistance(a: String, b: String): Int {
        if (Math.abs(a.length - b.length) > 2) return 99
        val d = Array(a.length + 1) { IntArray(b.length + 1) }
        for (i in 0..a.length) d[i][0] = i
        for (j in 0..b.length) d[0][j] = j
        for (i in 1..a.length) for (j in 1..b.length) {
            d[i][j] = minOf(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + if (a[i - 1] == b[j - 1]) 0 else 1)
            if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i][j] = minOf(d[i][j], d[i - 2][j - 2] + 1)
        }
        return d[a.length][b.length]
    }

    // null = strona wygląda w porządku; inaczej: domena, którą strona podrabia ("xn" - znaki udające litery)
    fun checkLookalike(rawHost: String?, extra: Collection<String>): String? {
        val host = rawHost?.lowercase()?.trimEnd('.') ?: return null
        if (host.isEmpty() || !host.contains('.') || host.all { it.isDigit() || it == '.' }) return null
        val reg = regDomain(host)
        if (reg in official || reg in extra) return null
        // 1. adres z obcymi znakami udającymi litery
        if (host.split('.').any { it.startsWith("xn--") }) return "xn"
        // 2. marka w adresie, ale domena nie należy do tej firmy (paypal-secure-login.com, mbank.pl.weryfikacja.top)
        val tokens = host.split('.', '-').filter { it.isNotEmpty() }
        for ((brand, domains) in brands)
            if (tokens.any { it == brand || (brand.length >= 6 && it.startsWith(brand)) }) return domains[0]
        // 3. literówka lub podmieniony znak w nazwie znanej strony (paypa1.com, rnbank.pl)
        val l = label(reg)
        if (l.length >= 4) for (d in official + extra) {
            val o = label(d)
            if (o.length < 4 || o == l) continue
            if (deconfuse(l) == deconfuse(o)) return d
            if (o.length >= 6 && l.length >= 5 && editDistance(l, o) == 1) return d
        }
        return null
    }

    // 🛡 wiadomość w komunikatorze: link podrabiający znaną firmę albo prośba o kod BLIK, dane karty, kod z SMS-a
    // (najczęstsze oszustwa na starszych osobach - WhatsApp i Signal tego nie sprawdzają); null - nic podejrzanego
    private val asks = listOf("blik", "kod sms", "kod z sms", "numer karty", "dane karty", "cvv", "dopłat", "hasło do banku", "logowania do banku",
        "card number", "card details", "verification code", "bank details", "sort code")
    fun linkFake(text: String): String? = Regex("(?:https?://|www\\.)([^/\\s:?#]+)", RegexOption.IGNORE_CASE).findAll(text).firstNotNullOfOrNull { checkLookalike(it.groupValues[1], emptyList()) }
    fun messageWarning(text: String, pl: Boolean): String? {
        linkFake(text)?.let { d -> val what = if (d == "xn") (if (pl) "znaną stronę (podmienione litery)" else "a known site (look-alike letters)") else d
            return if (pl) "⚠ Ten link udaje $what – to może być oszustwo. Nie podawaj haseł ani danych karty." else "⚠ This link imitates $what – it may be a scam. Don't enter passwords or card details." }
        val t = text.lowercase()
        if (asks.any { t.contains(it) }) return if (pl) "💡 Prośba o kod, dane karty albo dopłatę? Zanim cokolwiek zrobisz, zadzwoń do tej osoby i upewnij się." else "💡 Asked for a code, card details or a payment? Call the person to make sure before you do anything."
        return null
    }
}
