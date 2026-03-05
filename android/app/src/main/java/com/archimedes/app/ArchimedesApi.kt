package com.archimedes.app

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL

/**
 * HTTP client for the Archimedes Net service.
 * All calls are synchronous — run them on background threads (Thread / Coroutine IO).
 */
class ArchimedesApi(private val context: Context) {

    private val baseUrl get() = ServerConfig.getNetUrl(context)
    private val deviceId get() = ServerConfig.getDeviceId(context)

    // ── Status ────────────────────────────────────────────────────────────

    data class Status(
        val active: Boolean,
        val description: String?,
        val selfDev: String?,
        val osState: String?,
        val isLinux: Boolean
    )

    fun getStatus(): Status {
        val json = getJson("$baseUrl/v1/android/status")
        return Status(
            active      = json.optBoolean("active", false),
            description = json.optString("description").ifEmpty { null },
            selfDev     = json.optString("selfDev").ifEmpty { null },
            osState     = json.optJSONObject("osHealth")?.optString("state"),
            isLinux     = json.optJSONObject("osHealth")?.optBoolean("isLinux", false) ?: false
        )
    }

    // ── Approvals ─────────────────────────────────────────────────────────

    data class Approval(val taskId: String, val message: String)

    fun getApprovals(): List<Approval> {
        val arr = getJsonArray("$baseUrl/approvals")
        return (0 until arr.length()).map { i ->
            val obj = arr.getJSONObject(i)
            Approval(
                taskId  = obj.optString("taskId", obj.optString("TaskId", "")),
                message = obj.optString("Message", obj.optString("message", "Approve?"))
            )
        }
    }

    fun sendApprovalResponse(taskId: String, approved: Boolean): Boolean {
        val body = """{"taskId":"$taskId","approved":$approved}"""
        return postJson("$baseUrl/approval-response", body) != null
    }

    // ── Commands (send task/goal/chat from Android to Core) ───────────────

    data class CommandResult(val id: String, val status: String)

    fun sendCommand(type: String, payload: JSONObject): CommandResult? {
        val body = JSONObject().apply {
            put("type",     type)
            put("payload",  payload)
            put("deviceId", deviceId)
        }.toString()
        val resp = postJson("$baseUrl/v1/android/command", body) ?: return null
        return CommandResult(
            id     = resp.optString("id"),
            status = resp.optString("status")
        )
    }

    fun sendTask(text: String) = sendCommand("TASK", JSONObject().put("text", text))
    fun sendGoal(text: String) = sendCommand("GOAL", JSONObject().put("text", text))
    fun sendChat(text: String) = sendCommand("CHAT", JSONObject().put("text", text))

    fun getCommandStatus(id: String): JSONObject? = try {
        getJson("$baseUrl/v1/android/commands")
            .let { null } // commands endpoint returns array — use getAllCommands instead
    } catch (_: Exception) { null }

    // ── Notifications polling ─────────────────────────────────────────────

    data class Notification(val id: String, val title: String, val body: String)

    fun getPendingNotifications(): List<Notification> {
        val arr = getJsonArray("$baseUrl/v1/android/notifications")
        return (0 until arr.length()).map { i ->
            val obj = arr.getJSONObject(i)
            Notification(
                id    = obj.optString("id"),
                title = obj.optString("title"),
                body  = obj.optString("body")
            )
        }
    }

    fun markAllNotificationsRead(): Boolean {
        return postJson("$baseUrl/v1/android/notifications/read-all", "{}") != null
    }

    // ── Health ────────────────────────────────────────────────────────────

    fun isReachable(): Boolean = try {
        val url = URL("$baseUrl/health")
        val conn = (url.openConnection() as HttpURLConnection).apply {
            connectTimeout = 3000
            readTimeout    = 3000
            requestMethod  = "GET"
        }
        val code = conn.responseCode
        conn.disconnect()
        code == 200
    } catch (_: Exception) { false }

    // ── Internal helpers ──────────────────────────────────────────────────

    private fun getJson(url: String): JSONObject = try {
        val conn = (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 8000
            readTimeout    = 8000
            requestMethod  = "GET"
        }
        val text = conn.inputStream.bufferedReader().readText()
        conn.disconnect()
        JSONObject(text)
    } catch (_: Exception) { JSONObject() }

    private fun getJsonArray(url: String): JSONArray = try {
        val conn = (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 8000
            readTimeout    = 8000
            requestMethod  = "GET"
        }
        val text = conn.inputStream.bufferedReader().readText()
        conn.disconnect()
        JSONArray(text)
    } catch (_: Exception) { JSONArray() }

    private fun postJson(url: String, body: String): JSONObject? = try {
        val bytes = body.toByteArray(Charsets.UTF_8)
        val conn = (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 8000
            readTimeout    = 8000
            requestMethod  = "POST"
            doOutput       = true
            setRequestProperty("Content-Type", "application/json")
            setRequestProperty("Content-Length", bytes.size.toString())
        }
        conn.outputStream.use { it.write(bytes) }
        val code = conn.responseCode
        val text = try {
            conn.inputStream.bufferedReader().readText()
        } catch (_: Exception) {
            conn.errorStream?.bufferedReader()?.readText() ?: "{}"
        }
        conn.disconnect()
        if (code in 200..299) JSONObject(text) else null
    } catch (_: Exception) { null }
}
