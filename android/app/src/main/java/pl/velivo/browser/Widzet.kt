package pl.velivo.browser

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.widget.RemoteViews
import java.time.LocalDate

// Widżet na pulpit: 🔎 szukaj, ▶ YouTube, 🗒 karteczki z najbliższymi terminami. Pokazuje tylko treść karteczek
// zapisaną w telefonie - nic z sieci, odświeżany po zmianie karteczek i co kilka godzin (zmiana dnia).
class Widzet : AppWidgetProvider() {
    override fun onUpdate(ctx: Context, mgr: AppWidgetManager, ids: IntArray) { for (id in ids) mgr.updateAppWidget(id, views(ctx)) }

    companion object {
        const val ACTION_SEARCH = "pl.velivo.browser.WIDZET_SZUKAJ"
        const val ACTION_YT = "pl.velivo.browser.WIDZET_YT"

        private fun open(ctx: Context, code: Int, action: String) = PendingIntent.getActivity(ctx, 500 + code,
            Intent(ctx, MainActivity::class.java).setAction(action).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

        private fun views(ctx: Context) = RemoteViews(ctx.packageName, R.layout.widzet_velivo).apply {
            setOnClickPendingIntent(R.id.w_szukaj, open(ctx, 1, ACTION_SEARCH))
            setOnClickPendingIntent(R.id.w_yt, open(ctx, 2, ACTION_YT))
            setOnClickPendingIntent(R.id.w_notatki, open(ctx, 3, Karteczki.ACTION_NOTES))
            setOnClickPendingIntent(R.id.w_lista, open(ctx, 3, Karteczki.ACTION_NOTES))
            setTextViewText(R.id.w_lista, notes(ctx))
        }

        // najbliższe terminy na górze (dziś, jutro, data), potem pozostałe karteczki - najwyżej 4 linie
        private fun notes(ctx: Context): String {
            val today = LocalDate.now()
            val l = Karteczki.load(ctx).map { n ->
                val first = n.text.lineSequence().map { it.trim().removePrefix("•").trim() }.firstOrNull { it.isNotEmpty() } ?: ""
                val d = Karteczki.due(n.text, today, n.repeat)
                val next = Karteczki.next(n.text, today, n.repeat)
                val tag = when (d) { 2 -> " · " + ctx.getString(R.string.note_due_today); 1 -> " · " + ctx.getString(R.string.note_due_tomorrow)
                    else -> next?.let { " · " + it.dayOfMonth + "." + String.format("%02d", it.monthValue) } ?: "" }
                Triple(Karteczki.icon(n.kind) + " " + first.take(40) + tag, d, next)
            }.filter { it.first.length > 3 }
                .sortedWith(compareBy({ -it.second }, { it.third ?: LocalDate.MAX }))
            return if (l.isEmpty()) ctx.getString(R.string.widget_no_notes) else l.take(4).joinToString("\n") { it.first }
        }

        fun refresh(ctx: Context) {
            val mgr = AppWidgetManager.getInstance(ctx) ?: return
            val ids = mgr.getAppWidgetIds(ComponentName(ctx, Widzet::class.java))
            if (ids.isNotEmpty()) runCatching { for (id in ids) mgr.updateAppWidget(id, views(ctx)) }
        }
    }
}
