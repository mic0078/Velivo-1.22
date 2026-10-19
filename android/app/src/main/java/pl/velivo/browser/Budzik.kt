package pl.velivo.browser

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import java.time.LocalDateTime
import java.time.ZoneId

// ⏰ Pobudka z wybranym utworem (YouTube albo inna strona z dźwiękiem): codziennie o ustalonej godzinie
// Velivo włącza się na zablokowanym ekranie i odtwarza utwór z narastającą głośnością.
object Budzik {
    const val ACTION_WAKE = "pl.velivo.browser.POBUDKA"
    private fun prefs(ctx: Context) = ctx.getSharedPreferences("velivo", Context.MODE_PRIVATE)

    fun url(ctx: Context): String? = prefs(ctx).getString("pobudka-url", null)
    fun title(ctx: Context): String = prefs(ctx).getString("pobudka-tytul", "") ?: ""
    fun time(ctx: Context): Int = prefs(ctx).getInt("pobudka-czas", -1)   // minuty od północy; -1 = wyłączona
    // dni tygodnia: bit 0 = poniedziałek ... bit 6 = niedziela; 0 = jednorazowo
    fun days(ctx: Context): Int = prefs(ctx).getInt("pobudka-dni", 127)

    fun set(ctx: Context, url: String, title: String, minutes: Int, days: Int) {
        prefs(ctx).edit().putString("pobudka-url", url).putString("pobudka-tytul", title).putInt("pobudka-czas", minutes).putInt("pobudka-dni", days).apply()
        schedule(ctx)
    }

    fun off(ctx: Context) {
        prefs(ctx).edit().putInt("pobudka-czas", -1).apply()
        ctx.getSystemService(AlarmManager::class.java)?.cancel(pending(ctx))
    }

    private fun pending(ctx: Context) = PendingIntent.getActivity(ctx, 9, Intent(ctx, MainActivity::class.java).setAction(ACTION_WAKE)
        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

    // budzik systemowy (ikona budzika na pasku, działa w trybie oszczędzania); drzemka = za N minut
    fun schedule(ctx: Context, snoozeMin: Int = 0) {
        val m = time(ctx)
        if (m < 0 || url(ctx) == null) return
        val now = LocalDateTime.now()
        val d = days(ctx)
        var at = if (snoozeMin > 0) now.plusMinutes(snoozeMin.toLong()) else now.toLocalDate().atTime(m / 60, m % 60)
        if (snoozeMin == 0) {
            // najbliższy wybrany dzień (jednorazowo - najbliższa ta godzina)
            var i = 0
            while (i++ < 8 && (!at.isAfter(now) || (d != 0 && d and (1 shl (at.dayOfWeek.value - 1)) == 0))) at = at.plusDays(1)
        }
        val ms = at.atZone(ZoneId.systemDefault()).toInstant().toEpochMilli()
        val am = ctx.getSystemService(AlarmManager::class.java) ?: return
        runCatching { am.setAlarmClock(AlarmManager.AlarmClockInfo(ms, pending(ctx)), pending(ctx)) }
            .onFailure { am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, ms, pending(ctx)) }   // bez zgody na dokładny budzik - przybliżony
    }
}
