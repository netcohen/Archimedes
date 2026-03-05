package com.archimedes.app

import android.os.Bundle
import android.widget.Button
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import java.net.HttpURLConnection
import java.net.URL

class InboxActivity : AppCompatActivity() {

    private val netUrl  get() = ServerConfig.getNetUrl(this)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_inbox)
        fetchMessage()
        findViewById<Button>(R.id.btnReply).setOnClickListener { sendReply() }
    }

    private fun fetchMessage() {
        Thread {
            try {
                val msg = URL("$netUrl/envelope").readText()
                runOnUiThread {
                    findViewById<TextView>(R.id.tvMessage).text =
                        if (msg.isEmpty()) "אין הודעות" else msg
                }
            } catch (e: Exception) {
                runOnUiThread {
                    findViewById<TextView>(R.id.tvMessage).text = "שגיאה: ${e.message}"
                }
            }
        }.start()
    }

    private fun sendReply() {
        Thread {
            try {
                val body = "Reply from Android"
                val conn = URL("$netUrl/from-android").openConnection() as HttpURLConnection
                conn.requestMethod = "POST"
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", "text/plain")
                conn.outputStream.use { it.write(body.toByteArray()) }
                val code = conn.responseCode
                conn.disconnect()
                runOnUiThread {
                    if (code == 200) {
                        Toast.makeText(this@InboxActivity, "תשובה נשלחה", Toast.LENGTH_SHORT).show()
                    } else {
                        Toast.makeText(this@InboxActivity, "שגיאה: $code", Toast.LENGTH_SHORT).show()
                    }
                }
            } catch (e: Exception) {
                runOnUiThread {
                    Toast.makeText(this@InboxActivity, "שגיאה: ${e.message}", Toast.LENGTH_LONG).show()
                }
            }
        }.start()
    }
}
