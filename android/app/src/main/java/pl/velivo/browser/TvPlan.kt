package pl.velivo.browser

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import org.json.JSONArray
import org.json.JSONObject

// ⏰ Zaplanowane włączenie audycji: o godzinie z programu TV Velivo samo otwiera odtwarzacz na tym kanale
// (budzik systemowy - działa przy wygaszonym ekranie). Zapamiętana tylko nazwa kanału, nie adres z tokenem.
object TvPlan {
    const val ACTION = "pl.velivo.browser.TV_START"
    class Plan(val id: Int, val src: String, val key: String, val channel: String, val show: String, val at: Long, val offAt: Long = 0)   // offAt: wyłącz po zakończeniu audycji

    private fun prefs(ctx: Context) = ctx.getSharedPreferences("velivo", Context.MODE_PRIVATE)

    fun list(ctx: Context): List<Plan> = runCatching {
        val a = JSONArray(prefs(ctx).getString("tv-plan", "[]"))
        (0 until a.length()).map { a.getJSONObject(it) }.map { Plan(it.getInt("id"), it.getString("src"), it.getString("key"), it.getString("ch"), it.getString("show"), it.getLong("at"), it.optLong("off")) }
    }.getOrDefault(emptyList()).filter { it.at > System.currentTimeMillis() - 3600_000L }.sortedBy { it.at }

    private fun save(ctx: Context, l: List<Plan>) {
        val a = JSONArray()
        l.forEach { a.put(JSONObject().put("id", it.id).put("src", it.src).put("key", it.key).put("ch", it.channel).put("show", it.show).put("at", it.at).put("off", it.offAt)) }
        prefs(ctx).edit().putString("tv-plan", a.toString()).apply()
    }

    fun find(ctx: Context, key: String, at: Long) = list(ctx).firstOrNull { it.key == key && it.at == at }

    private fun pending(ctx: Context, id: Int) = PendingIntent.getActivity(ctx, 1000 + id, Intent(ctx, MainActivity::class.java).setAction(ACTION).putExtra("id", id)
        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

    fun add(ctx: Context, ch: Tv.Channel, show: Tv.Show, offAfter: Boolean = false) {
        val l = list(ctx).toMutableList()
        if (l.any { it.key == ch.key && it.at == show.start }) return
        val id = ((l.maxOfOrNull { it.id } ?: 0) + 1) % 900
        l.add(Plan(id, ch.source, ch.key, ch.name, show.title, show.start, if (offAfter) show.stop else 0L)); save(ctx, l)
        val am = ctx.getSystemService(AlarmManager::class.java) ?: return
        runCatching { am.setAlarmClock(AlarmManager.AlarmClockInfo(show.start, pending(ctx, id)), pending(ctx, id)) }
            .onFailure { am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, show.start, pending(ctx, id)) }   // bez zgody na dokładny budzik - przybliżony
    }

    fun remove(ctx: Context, id: Int) {
        ctx.getSystemService(AlarmManager::class.java)?.cancel(pending(ctx, id))
        save(ctx, list(ctx).filter { it.id != id })
    }

    fun get(ctx: Context, id: Int) = list(ctx).firstOrNull { it.id == id }
}
