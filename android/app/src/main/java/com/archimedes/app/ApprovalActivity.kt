package com.archimedes.app

import android.content.Intent
import android.os.Bundle
import android.widget.Button
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

class ApprovalActivity : AppCompatActivity() {

    private val netUrl get() = ServerConfig.getNetUrl(this)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_approval)
        loadApprovals()
    }

    private fun loadApprovals() {
        Thread {
            try {
                val json = URL("$netUrl/approvals").readText()
                val arr = JSONArray(json)
                runOnUiThread {
                    if (arr.length() == 0) {
                        findViewById<TextView>(R.id.tvApproval).text = "אין אישורים ממתינים"
                        return@runOnUiThread
                    }
                    val first = arr.getJSONObject(0)
                    val taskId = first.optString("taskId", first.optString("TaskId", ""))
                    val msg = first.optString("Message", first.optString("message", "לאשר?"))
                    val count = arr.length()
                    val countText = if (count > 1) " ($count ממתינים)" else ""
                    findViewById<TextView>(R.id.tvApproval).text = "$msg$countText"
                    findViewById<Button>(R.id.btnApprove).setOnClickListener { sendResponse(taskId, true) }
                    findViewById<Button>(R.id.btnDeny).setOnClickListener { sendResponse(taskId, false) }
                }
            } catch (e: Exception) {
                runOnUiThread {
                    findViewById<TextView>(R.id.tvApproval).text = "שגיאה: ${e.message}"
                }
            }
        }.start()
    }

    private fun sendResponse(taskId: String, approved: Boolean) {
        Thread {
            try {
                val body = """{"taskId":"$taskId","approved":$approved}"""
                val conn = URL("$netUrl/approval-response").openConnection() as HttpURLConnection
                conn.requestMethod = "POST"
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", "application/json")
                conn.outputStream.use { it.write(body.toByteArray()) }
                val code = conn.responseCode
                conn.disconnect()
                runOnUiThread {
                    if (code == 200) {
                        Toast.makeText(this, if (approved) "✅ אושר" else "❌ נדחה", Toast.LENGTH_SHORT).show()
                        finish()
                    } else {
                        Toast.makeText(this, "שגיאה: $code", Toast.LENGTH_SHORT).show()
                    }
                }
            } catch (e: Exception) {
                runOnUiThread { Toast.makeText(this, "שגיאה: ${e.message}", Toast.LENGTH_LONG).show() }
            }
        }.start()
    }
}
