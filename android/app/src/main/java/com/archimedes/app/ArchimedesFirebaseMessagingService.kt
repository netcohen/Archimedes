package com.archimedes.app

import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import androidx.core.app.NotificationCompat
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

/**
 * FCM message handler — receives push notifications from Core via Net service.
 *
 * Push flow:
 *   Core needs approval / task done
 *   → Net service (firebase-admin) → FCM → this service
 *   → showNotification() → user sees it on any network
 *
 * Token refresh flow:
 *   Firebase rotates token → onNewToken() → Firestore /devices/{id}
 *   → Net service syncs → firebase-admin has new token
 *
 * Message types (data["type"]):
 *   "approval" → high-priority alert, opens ApprovalActivity
 *   "status"   → informational, opens MainActivity
 *   (default)  → generic notification
 */
class ArchimedesFirebaseMessagingService : FirebaseMessagingService() {

    override fun onMessageReceived(message: RemoteMessage) {
        val data   = message.data
        val type   = data["type"] ?: "update"
        val title  = message.notification?.title ?: data["title"] ?: "ארכימדס"
        val body   = message.notification?.body  ?: data["body"]  ?: ""

        val (channel, intent, notifId, priority) = when (type) {
            "approval" -> Quad(
                ArchimedesApp.CHANNEL_ALERTS,
                Intent(this, ApprovalActivity::class.java),
                2001,
                NotificationCompat.PRIORITY_HIGH
            )
            else -> Quad(
                ArchimedesApp.CHANNEL_UPDATES,
                Intent(this, MainActivity::class.java),
                2002,
                NotificationCompat.PRIORITY_DEFAULT
            )
        }

        val pi = PendingIntent.getActivity(
            this, notifId,
            intent.apply { flags = Intent.FLAG_ACTIVITY_SINGLE_TOP },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val notif = NotificationCompat.Builder(this, channel)
            .setSmallIcon(android.R.drawable.ic_dialog_info)
            .setContentTitle(title)
            .setContentText(body)
            .setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setContentIntent(pi)
            .setAutoCancel(true)
            .setPriority(priority)
            .build()

        (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager)
            .notify(notifId, notif)
    }

    /**
     * Firebase refreshed the FCM token — update Firestore so Net picks it up.
     * Works regardless of whether the phone is on the home network.
     */
    override fun onNewToken(token: String) {
        // Persist locally
        getSharedPreferences("archimedes_prefs", Context.MODE_PRIVATE)
            .edit().putString("fcm_token", token).apply()

        // Push to Firestore /devices/{deviceId} asynchronously
        val deviceId = ServerConfig.getDeviceId(this)
        CoroutineScope(Dispatchers.IO).launch {
            try {
                FirestoreManager.registerFcmToken(deviceId, token)
            } catch (_: Exception) {
                // Best-effort — will be retried next app launch
            }
        }
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    private data class Quad<A, B, C, D>(val first: A, val second: B, val third: C, val fourth: D)
}
