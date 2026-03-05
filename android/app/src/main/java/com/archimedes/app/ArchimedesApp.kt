package com.archimedes.app

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build
import com.google.firebase.messaging.FirebaseMessaging
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.net.NetworkInterface

/**
 * Application class — creates notification channels, registers FCM token,
 * and reports local WiFi IP to Net service on startup.
 *
 * The local IP is used by AppUpdater (Core) for ADB WiFi OTA updates:
 *   Net stores IP → Core fetches IP → runs scripts/update-android.sh <ip> → ADB installs APK
 */
class ArchimedesApp : Application() {

    companion object {
        const val CHANNEL_ALERTS  = "archimedes_alerts"
        const val CHANNEL_UPDATES = "archimedes_updates"

        /**
         * Get this device's local WiFi IP address.
         * Returns null if no WiFi connection (cellular only, no loopback).
         * Used by AppUpdater for ADB WiFi OTA installation.
         */
        fun getLocalIpAddress(): String? = try {
            NetworkInterface.getNetworkInterfaces()
                ?.toList()
                ?.flatMap { it.inetAddresses.toList() }
                ?.firstOrNull { addr ->
                    !addr.isLoopbackAddress && addr.hostAddress?.contains(':') == false
                }
                ?.hostAddress
        } catch (_: Exception) { null }
    }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannels()
        registerFcmTokenAndIp()
    }

    /**
     * Register FCM token + local IP to both:
     *   1. Firestore /devices/{deviceId}  (FCM token — works from any network)
     *   2. Net service via HTTP           (IP — for ADB WiFi OTA when on home WiFi)
     *
     * Both are best-effort and non-blocking.
     */
    private fun registerFcmTokenAndIp() {
        val deviceId  = ServerConfig.getDeviceId(this)
        val localIp   = getLocalIpAddress()

        FirebaseMessaging.getInstance().token.addOnSuccessListener { token ->
            // Cache locally
            getSharedPreferences("archimedes_prefs", MODE_PRIVATE)
                .edit()
                .putString("fcm_token", token)
                .apply()

            CoroutineScope(Dispatchers.IO).launch {
                // 1. Firestore: FCM token (for remote push, works anywhere)
                try {
                    FirestoreManager.registerFcmToken(deviceId, token)
                } catch (_: Exception) { /* non-fatal */ }

                // 2. Net service HTTP: FCM token + local IP (for ADB WiFi OTA)
                //    Only succeeds when on home WiFi — best-effort
                try {
                    val netUrl  = ServerConfig.getNetUrl(this@ArchimedesApp)
                    val payload = buildString {
                        append("{\"deviceId\":\"$deviceId\"")
                        append(",\"token\":\"$token\"")
                        if (localIp != null) append(",\"ip\":\"$localIp\"")
                        append("}")
                    }
                    val url  = java.net.URL("$netUrl/fcm/register-token")
                    val conn = (url.openConnection() as java.net.HttpURLConnection).apply {
                        requestMethod = "POST"
                        setRequestProperty("Content-Type", "application/json")
                        doOutput = true
                        connectTimeout = 5_000
                        readTimeout    = 5_000
                    }
                    conn.outputStream.use { it.write(payload.toByteArray()) }
                    conn.responseCode   // trigger the request
                    conn.disconnect()
                } catch (_: Exception) { /* non-fatal — phone may not be on home network */ }
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
