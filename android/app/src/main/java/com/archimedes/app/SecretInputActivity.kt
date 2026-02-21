package com.archimedes.app

import android.os.Bundle
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

class SecretInputActivity : AppCompatActivity() {
    private val netUrl = "http://10.0.2.2:5052"
    private var approvalId: String = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_secret_input)
        
        approvalId = intent.getStringExtra("approvalId") ?: ""
        val prompt = intent.getStringExtra("prompt") ?: "Enter secret value"
        
        findViewById<TextView>(R.id.tvSecretPrompt).text = prompt
        
        findViewById<Button>(R.id.btnSubmitSecret).setOnClickListener {
            val secretValue = findViewById<EditText>(R.id.etSecretInput).text.toString()
            if (secretValue.isNotEmpty()) {
                sendSecretResponse(secretValue)
            } else {
                Toast.makeText(this, "Please enter a value", Toast.LENGTH_SHORT).show()
            }
        }
        
        findViewById<Button>(R.id.btnCancelSecret).setOnClickListener {
            sendCancelResponse()
        }
    }

    private fun sendSecretResponse(secretValue: String) {
        Thread {
            try {
                val body = JSONObject().apply {
                    put("approved", true)
                    put("secretValue", secretValue)
                }.toString()
                
                val conn = URL("$netUrl/v2/approval/$approvalId/respond").openConnection() as HttpURLConnection
                conn.requestMethod = "POST"
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", "application/json")
                conn.outputStream.use { it.write(body.toByteArray()) }
                val code = conn.responseCode
                conn.disconnect()
                
                runOnUiThread {
                    if (code == 200) {
                        Toast.makeText(this, "Secret submitted", Toast.LENGTH_SHORT).show()
                        finish()
                    } else {
                        Toast.makeText(this, "Failed: $code", Toast.LENGTH_SHORT).show()
                    }
                }
            } catch (e: Exception) {
                runOnUiThread {
                    Toast.makeText(this, "Error: ${e.message}", Toast.LENGTH_LONG).show()
                }
            }
        }.start()
    }

    private fun sendCancelResponse() {
        Thread {
            try {
                val body = JSONObject().apply {
                    put("approved", false)
                }.toString()
                
                val conn = URL("$netUrl/v2/approval/$approvalId/respond").openConnection() as HttpURLConnection
                conn.requestMethod = "POST"
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", "application/json")
                conn.outputStream.use { it.write(body.toByteArray()) }
                conn.disconnect()
                
                runOnUiThread { finish() }
            } catch (e: Exception) {
                runOnUiThread { finish() }
            }
        }.start()
    }
}
