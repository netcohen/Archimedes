package com.archimedes.app

import android.graphics.BitmapFactory
import android.os.Bundle
import android.util.Base64
import android.widget.Button
import android.widget.EditText
import android.widget.ImageView
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

class CaptchaActivity : AppCompatActivity() {
    private val netUrl get() = ServerConfig.getNetUrl(this)
    private var approvalId: String = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_captcha)
        
        approvalId = intent.getStringExtra("approvalId") ?: ""
        val encryptedImageJson = intent.getStringExtra("captchaImageEncrypted")
        
        if (encryptedImageJson != null) {
            loadCaptchaImage(encryptedImageJson)
        } else {
            findViewById<TextView>(R.id.tvCaptchaStatus).text = "No captcha image"
        }
        
        findViewById<Button>(R.id.btnSubmitCaptcha).setOnClickListener {
            val solution = findViewById<EditText>(R.id.etCaptchaSolution).text.toString()
            if (solution.isNotEmpty()) {
                sendCaptchaResponse(solution)
            } else {
                Toast.makeText(this, "Please enter the captcha code", Toast.LENGTH_SHORT).show()
            }
        }
        
        findViewById<Button>(R.id.btnCancelCaptcha).setOnClickListener {
            sendCancelResponse()
        }
    }

    private fun loadCaptchaImage(encryptedJson: String) {
        try {
            val imageBase64 = decryptCaptchaImage(encryptedJson)
            if (imageBase64 != null) {
                val imageBytes = Base64.decode(imageBase64, Base64.DEFAULT)
                val bitmap = BitmapFactory.decodeByteArray(imageBytes, 0, imageBytes.size)
                findViewById<ImageView>(R.id.ivCaptcha).setImageBitmap(bitmap)
                findViewById<TextView>(R.id.tvCaptchaStatus).text = "Enter the code shown above"
            } else {
                findViewById<TextView>(R.id.tvCaptchaStatus).text = "Failed to decrypt captcha"
            }
        } catch (e: Exception) {
            findViewById<TextView>(R.id.tvCaptchaStatus).text = "Error: ${e.message}"
        }
    }

    private fun decryptCaptchaImage(encryptedJson: String): String? {
        // TODO: Implement E2E decryption with device private key
        // For now, this is a placeholder that would need the Android Keystore integration
        // In production, this would decrypt using X25519+ChaCha20-Poly1305
        return null
    }

    private fun sendCaptchaResponse(solution: String) {
        Thread {
            try {
                val body = JSONObject().apply {
                    put("approved", true)
                    put("captchaSolution", solution)
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
                        Toast.makeText(this, "Captcha submitted", Toast.LENGTH_SHORT).show()
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
