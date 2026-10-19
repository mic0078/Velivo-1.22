package pl.velivo.browser

import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context

// Odświeżanie w tle (zwykle w nocy: telefon ładuje się i leży nieużywany, Wi-Fi): listy kanałów, program TV
// i biblioteka Jellyfin pobierane z zapisanych adresów - rano wszystko gotowe, otwiera się od razu.
class Odswiezanie : JobService() {
    @Volatile private var stop = false

    override fun onStartJob(params: JobParameters): Boolean {
        stop = false
        Thread {
            val pw = Passwords(this)
            for (s in Tv.sources(pw)) {
                if (stop) break
                val ch = runCatching { Tv.channels(this, pw, s, true) }.getOrNull() ?: continue
                runCatching { Tv.epg(this, s, ch, true) }
            }
            if (!stop) Jelly.conf(pw)?.let { c -> runCatching { Jelly.refresh(this, pw, c) } }
            jobFinished(params, false)
        }.start()
        return true
    }

    override fun onStopJob(params: JobParameters): Boolean { stop = true; return true }   // przerwane (telefon w użyciu) - spróbuje później

    companion object {
        private const val ID = 7001

        // raz na dobę, tylko przy ładowaniu, bezczynności i Wi-Fi - nie zużywa baterii ani danych komórkowych
        fun plan(ctx: Context) {
            val js = ctx.getSystemService(JobScheduler::class.java) ?: return
            if (js.getPendingJob(ID) != null) return
            js.schedule(JobInfo.Builder(ID, ComponentName(ctx, Odswiezanie::class.java))
                .setPeriodic(24 * 3600_000L)
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_UNMETERED)
                .setRequiresCharging(true)
                .setRequiresDeviceIdle(true)
                .setPersisted(true)
                .build())
        }
    }
}
