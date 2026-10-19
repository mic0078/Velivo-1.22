package pl.velivo.browser

import android.app.AlarmManager
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import org.json.JSONArray
import org.json.JSONObject
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.ZoneId

// Karteczki jak w Velivo na Windows (StickyNotes.cs): termin wpisany w treści (12.05.2026, 12.05, 2026-05-12, 12/05/2026),
// powtarzanie terminu ("7d", "14d", "1m", "1y", własny odstęp), przypomnienie dzień wcześniej i w dniu terminu.
// Ten sam układ pól co karteczki.json na Windows (Id, Kind, Text, Color, Repeat).
object Karteczki {
    class Note(var id: String, var kind: String, var text: String, var color: String, var repeat: String)

    // rodzaj -> ikona, tytuł (zasób), początkowa treść
    val kinds = listOf(
        Triple("note", "📝", R.string.note_note), Triple("todo", "✅", R.string.note_todo), Triple("shop", "🛒", R.string.note_shop),
        Triple("call", "📞", R.string.note_call), Triple("important", "❗", R.string.note_important), Triple("idea", "💡", R.string.note_idea),
        Triple("today", "⭐", R.string.note_today),
    )
    fun startText(kind: String) = if (kind in setOf("todo", "shop", "today")) "• \n• \n• " else ""
    fun icon(kind: String) = kinds.firstOrNull { it.first == kind }?.second ?: "📝"
    fun title(kind: String) = kinds.firstOrNull { it.first == kind }?.third ?: R.string.note_note
    val colors = listOf("FFF59D", "FFCC80", "A5D6A7", "90CAF9", "F48FB1", "E1BEE7")

    private fun prefs(ctx: Context) = ctx.getSharedPreferences("velivo", Context.MODE_PRIVATE)

    fun load(ctx: Context): MutableList<Note> = runCatching {
        val a = JSONArray(prefs(ctx).getString("karteczki", "[]"))
        (0 until a.length()).map { a.getJSONObject(it) }.filter { it.optString("Id").isNotEmpty() }.map {
            Note(it.optString("Id"), it.optString("Kind", "note"), it.optString("Text"), it.optString("Color", colors[0]), it.optString("Repeat"))
        }.toMutableList()
    }.getOrDefault(mutableListOf())

    fun save(ctx: Context, list: List<Note>) {
        val a = JSONArray()
        list.forEach { a.put(JSONObject().put("Id", it.id).put("Kind", it.kind).put("Text", it.text).put("Color", it.color).put("Repeat", it.repeat)) }
        prefs(ctx).edit().putString("karteczki", a.toString()).apply()
        schedule(ctx)
    }

    private val dateRe = Regex("""(?<!\d)(\d{4})-(\d{1,2})-(\d{1,2})(?!\d)|(?<![\d.])(\d{1,2})[./](\d{1,2})(?:[./](\d{2,4}))?(?![\d/])""")

    // termin w treści: 2 - dziś, 1 - jutro, 0 - brak (jak NoteDue na Windows)
    fun due(text: String, today: LocalDate, repeat: String?): Int {
        var every = 0; var unit = 'd'
        if (!repeat.isNullOrEmpty() && repeat.length > 1) repeat.dropLast(1).toIntOrNull()?.takeIf { it > 0 }?.let { every = it; unit = repeat.last() }
        fun next(d: LocalDate) = when (unit) { 'm' -> d.plusMonths(every.toLong()); 'y' -> d.plusYears(every.toLong()); else -> d.plusDays(every.toLong()) }
        var best = 0
        for (m in dateRe.findAll(text)) {
            val g = m.groups
            val y: Int; val mo: Int; val d: Int
            if (g[1] != null) { y = g[1]!!.value.toInt(); mo = g[2]!!.value.toInt(); d = g[3]!!.value.toInt() }
            else { d = g[4]!!.value.toInt(); mo = g[5]!!.value.toInt(); y = g[6]?.value?.toInt()?.let { if (it < 100) it + 2000 else it } ?: today.year }
            var dt = runCatching { LocalDate.of(y, mo, d) }.getOrNull() ?: continue
            var i = 0
            while (every > 0 && dt.isBefore(today) && i++ < 20000) dt = next(dt)   // pierwszy termin od dziś
            if (dt == today) return 2
            if (dt == today.plusDays(1)) best = 1
        }
        return best
    }

    // najbliższy termin (dziś lub później) - do napisu na karteczce
    fun next(text: String, today: LocalDate, repeat: String?): LocalDate? {
        for (d in 0..800) {
            if (due(text, today.plusDays(d.toLong()), repeat) == 2) return today.plusDays(d.toLong())
            if (repeat.isNullOrEmpty() && d > 400) break
        }
        return null
    }

    // codzienne sprawdzenie o 9:00 (niedokładny alarm - bez specjalnych uprawnień, oszczędza baterię)
    fun schedule(ctx: Context) {
        val am = ctx.getSystemService(AlarmManager::class.java) ?: return
        val pi = PendingIntent.getBroadcast(ctx, 7, Intent(ctx, Przypomnienie::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        var at = LocalDate.now().atTime(9, 0)
        if (!at.isAfter(LocalDateTime.now())) at = at.plusDays(1)
        am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at.atZone(ZoneId.systemDefault()).toInstant().toEpochMilli(), pi)
    }

    // powiadomienia: termin jutro / dziś - każda karteczka raz dziennie
    fun notifyDue(ctx: Context) {
        val today = LocalDate.now()
        val p = prefs(ctx)
        val nm = ctx.getSystemService(NotificationManager::class.java) ?: return
        nm.createNotificationChannel(NotificationChannel("terminy", ctx.getString(R.string.notes_channel), NotificationManager.IMPORTANCE_DEFAULT))
        for (n in load(ctx)) {
            val d = due(n.text, today, n.repeat)
            if (d == 0) continue
            val key = "karteczka-pow-" + n.id
            if (p.getString(key, "") == today.toString()) continue
            p.edit().putString(key, today.toString()).apply()
            val open = PendingIntent.getActivity(ctx, n.id.hashCode(), Intent(ctx, MainActivity::class.java).setAction(ACTION_NOTES).putExtra("id", n.id), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            val first = n.text.lines().map { it.trim().trimStart('•').trim() }.firstOrNull { it.isNotEmpty() } ?: ctx.getString(title(n.kind))
            nm.notify(n.id.hashCode(), Notification.Builder(ctx, "terminy")
                .setSmallIcon(android.R.drawable.ic_popup_reminder)
                .setContentTitle(icon(n.kind) + " " + ctx.getString(if (d == 2) R.string.note_due_today else R.string.note_due_tomorrow))
                .setContentText(first)
                .setStyle(Notification.BigTextStyle().bigText(n.text.trim()))
                .setContentIntent(open).setAutoCancel(true).build())
        }
    }

    const val ACTION_NOTES = "pl.velivo.browser.KARTECZKI"

    class Przypomnienie : BroadcastReceiver() {
        override fun onReceive(ctx: Context, intent: Intent) {
            runCatching { notifyDue(ctx) }
            schedule(ctx)   // następny dzień (także po restarcie telefonu)
            if (intent.action == Intent.ACTION_BOOT_COMPLETED) runCatching { Budzik.schedule(ctx) }   // pobudka po włączeniu telefonu
        }
    }
}
