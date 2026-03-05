package com.archimedes.app

import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import androidx.core.app.NotificationCompat
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.util.concurrent.TimeUnit

/**
 * WorkManager background worker — polls the Net service every 15 minutes.
 * Shows local notifications for:
 *   - Pending approvals
 *   - New notifications from Core
 */
class PollingWorker(context: Context, params: WorkerParameters) :
    CoroutineWorker(context, params) {

    private val api = ArchimedesApi(applicationContext)

    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        try {
            if (!api.isReachable()) return@withContext Result.retry()

            checkApprovals()
            checkNotifications()

            Result.success()
        } catch (_: Exception) {
            Result.retry()
        }
    }

    private fun checkApprovals() {
        val approvals = api.getApprovals()
        if (approvals.isNotEmpty()) {
            val first = approvals.first()
            showNotification(
                id      = NOTIF_ID_APPROVAL,
                channel = ArchimedesApp.CHANNEL_ALERTS,
                title   = "ארכימדס מחכה לאישור",
                body    = first.message.take(120),
                intent  = Intent(applicationContext, ApprovalActivity::class.java)
            )
        }
    }

    private fun checkNotifications() {
        val notifications = api.getPendingNotifications()
        if (notifications.isNotEmpty()) {
            val notif = notifications.first()
            showNotification(
                id      = NOTIF_ID_UPDATE,
                channel = ArchimedesApp.CHANNEL_UPDATES,
                title   = notif.title,
                body    = notif.body.take(120),
                intent  = Intent(applicationContext, MainActivity::class.java)
            )
            api.markAllNotificationsRead()
        }
    }

    private fun showNotification(
        id: Int, channel: String, title: String, body: String, intent: Intent
    ) {
        val pi = PendingIntent.getActivity(
            applicationContext, id, intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val notif = NotificationCompat.Builder(applicationContext, channel)
            .setSmallIcon(android.R.drawable.ic_dialog_info)
            .setContentTitle(title)
            .setContentText(body)
            .setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setContentIntent(pi)
            .setAutoCancel(true)
            .build()

        val manager = applicationContext.getSystemService(Context.NOTIFICATION_SERVICE)
            as NotificationManager
        manager.notify(id, notif)
    }

    companion object {
        private const val WORK_NAME     = "archimedes_polling"
        private const val NOTIF_ID_APPROVAL = 1001
        private const val NOTIF_ID_UPDATE   = 1002

        /** Enqueue periodic polling — safe to call multiple times (idempotent). */
        fun schedule(context: Context) {
            val request = PeriodicWorkRequestBuilder<PollingWorker>(15, TimeUnit.MINUTES)
                .build()
            WorkManager.getInstance(context).enqueueUniquePeriodicWork(
                WORK_NAME,
                ExistingPeriodicWorkPolicy.KEEP,
                request
            )
        }

        /** Cancel background polling. */
        fun cancel(context: Context) {
            WorkManager.getInstance(context).cancelUniqueWork(WORK_NAME)
        }
    }
}
