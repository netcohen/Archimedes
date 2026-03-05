package com.archimedes.app

import android.content.Context

/**
 * Persistent server URL configuration.
 * Stored in SharedPreferences — survives app restarts.
 * Default: http://10.0.2.2:5052 (Android emulator localhost)
 * Change in Settings to your server's IP (e.g. http://192.168.1.100:5052)
 */
object ServerConfig {
    private const val PREFS_NAME = "archimedes_prefs"
    private const val KEY_NET_URL = "net_url"
    private const val KEY_DEVICE_ID = "device_id"

    const val DEFAULT_NET_URL = "http://10.0.2.2:5052"

    fun getNetUrl(context: Context): String {
        val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        return prefs.getString(KEY_NET_URL, DEFAULT_NET_URL) ?: DEFAULT_NET_URL
    }

    fun setNetUrl(context: Context, url: String) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit().putString(KEY_NET_URL, url.trimEnd('/')).apply()
    }

    /** Device identifier — generated once and persisted */
    fun getDeviceId(context: Context): String {
        val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        var id = prefs.getString(KEY_DEVICE_ID, null)
        if (id == null) {
            id = "android-${System.currentTimeMillis()}"
            prefs.edit().putString(KEY_DEVICE_ID, id).apply()
        }
        return id
    }
}
