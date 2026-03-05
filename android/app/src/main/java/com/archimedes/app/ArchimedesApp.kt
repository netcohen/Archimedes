package com.archimedes.app

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build

/**
 * Application class — creates notification channels on startup.
 */
class ArchimedesApp : Application() {

    companion object {
        const val CHANNEL_ALERTS  = "archimedes_alerts"
        const val CHANNEL_UPDATES = "archimedes_updates"
    }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannels()
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
