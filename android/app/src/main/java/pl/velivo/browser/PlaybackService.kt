package pl.velivo.browser

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder

// Odtwarzanie w tle: powiadomienie trzyma Velivo przy życiu, gdy film gra przy wygaszonym ekranie
// albo w innej aplikacji. Stuknięcie wraca do Velivo, "Zatrzymaj" pauzuje film.
class PlaybackService : Service() {
    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.background_play), NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getActivity(this, 1, Intent(this, MainActivity::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE)
        val n = Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setContentTitle(intent?.getStringExtra(EXTRA_TITLE) ?: getString(R.string.app_name))
            .setContentText(getString(R.string.background_play))
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, getString(R.string.stop), stop).build())
            // ⏾ do snu: wyłącz za 30 minut (każde stuknięcie dodaje 30)
            .addAction(Notification.Action.Builder(null, getString(R.string.sleep_30), PendingIntent.getActivity(this, 2, Intent(this, MainActivity::class.java).setAction(ACTION_SLEEP), PendingIntent.FLAG_IMMUTABLE)).build())
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 29) startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK)
        else startForeground(1, n)
        return START_NOT_STICKY
    }

    companion object {
        const val CHANNEL = "odtwarzanie"
        const val ACTION_STOP = "pl.velivo.browser.STOP"
        const val ACTION_SLEEP = "pl.velivo.browser.SEN"
        const val EXTRA_TITLE = "tytul"
    }
}
