package com.archimedes.app

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build
import com.google.firebase.messaging.FirebaseMessaging
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

/**
 * Application class — creates notification channels and registers FCM token on startup.
 */
class ArchimedesApp : Application() {

    companion object {
        const val CHANNEL_ALERTS  = "archimedes_alerts"
        const val CHANNEL_UPDATES = "archimedes_updates"
    }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannels()
        registerFcmToken()
    }

    /**
     * Get the FCM registration token and persist it to:
     *   1. SharedPreferences (local cache)
     *   2. Firestore /devices/{deviceId} (so Net service can pick it up from anywhere)
     *
     * This runs in the background and is best-effort — non-blocking.
     */
    private fun registerFcmToken() {
        FirebaseMessaging.getInstance().token.addOnSuccessListener { token ->
            val deviceId = ServerConfig.getDeviceId(this)
            // Cache locally
            getSharedPreferences("archimedes_prefs", MODE_PRIVATE)
                .edit().putString("fcm_token", token).apply()
            // Write to Firestore so Net service syncs the token even from remote network
            CoroutineScope(Dispatchers.IO).launch {
                try {
                    FirestoreManager.registerFcmToken(deviceId, token)
                } catch (_: Exception) {
                    // Non-fatal — polling fallback still works
                }
            }
        }
    }

    private fun createNotificationChannels() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = getSystemService(NOTIFICATION_SERVICE) as NotificationManager

        // High-priority: approvals, captcha, secrets
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ALERTS,
                "Archimedes Alerts",
                NotificationManager.IMPORTANCE_HIGH
            ).apply {
                description = "Approval requests and urgent notifications"
            }
        )

        // Default: status updates, task completions
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_UPDATES,
                "Archimedes Updates",
                NotificationManager.IMPORTANCE_DEFAULT
            ).apply {
                description = "Task completions and status updates"
            }
        )
    }
}
