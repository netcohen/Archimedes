package com.archimedes.app

import android.os.Bundle
import android.util.Base64
import android.util.Log
import android.widget.Button
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.security.KeyPairGenerator

class PairingActivity : AppCompatActivity() {
    private val coreUrl = "http://10.0.2.2:5051"

    private val scanLauncher = registerForActivityResult(ScanContract()) { result ->
        result.contents?.let { handleQrContent(it) }
            ?: Toast.makeText(this, "Scan cancelled", Toast.LENGTH_SHORT).show()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_pairing)
        findViewById<Button>(R.id.btnScan).setOnClickListener {
            scanLauncher.launch(ScanOptions().setPrompt("Scan PC pairing QR"))
        }
        findViewById<Button>(R.id.btnSimulate).setOnClickListener { simulatePairing() }
    }

    private fun handleQrContent(contents: String) {
        try {
            val json = JSONObject(contents)
            val sessionId = json.getString("sessionId")
            val corePublicKey = json.getString("corePublicKey")
            completePairing(sessionId, corePublicKey)
        } catch (e: Exception) {
            Log.e("Pairing", "Parse QR failed", e)
            runOnUiThread { Toast.makeText(this, "Invalid QR: ${e.message}", Toast.LENGTH_LONG).show() }
        }
    }

    private fun simulatePairing() {
        Thread {
            try {
                val pairingData = URL("$coreUrl/pairing-data").readText()
                val json = JSONObject(pairingData)
                val sessionId = json.getString("sessionId")
                val corePublicKey = json.getString("corePublicKey")
                completePairing(sessionId, corePublicKey)
            } catch (e: Exception) {
                Log.e("Pairing", "Simulate failed", e)
                runOnUiThread { Toast.makeText(this@PairingActivity, "Failed: ${e.message}", Toast.LENGTH_LONG).show() }
            }
        }.start()
    }

    private fun completePairing(sessionId: String, corePublicKey: String) {
        Thread {
            try {
                val kpg = KeyPairGenerator.getInstance("RSA")
                kpg.initialize(2048)
                val kp = kpg.generateKeyPair()
                val devicePubB64 = Base64.encodeToString(kp.public.encoded, Base64.NO_WRAP)
                val body = """{"sessionId":"$sessionId","devicePublicKey":"$devicePubB64"}"""
                val conn = URL("$coreUrl/pairing-complete").openConnection() as HttpURLConnection
                conn.requestMethod = "POST"
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", "application/json")
                conn.outputStream.use { it.write(body.toByteArray()) }
                val resp = conn.inputStream.bufferedReader().readText()
                conn.disconnect()
                val ok = JSONObject(resp).optBoolean("ok", false)
                runOnUiThread {
                    if (ok) Toast.makeText(this@PairingActivity, "Paired!", Toast.LENGTH_SHORT).show()
                    else Toast.makeText(this@PairingActivity, "Pairing failed", Toast.LENGTH_SHORT).show()
                }
            } catch (e: Exception) {
                Log.e("Pairing", "Complete failed", e)
                runOnUiThread { Toast.makeText(this@PairingActivity, "Failed: ${e.message}", Toast.LENGTH_LONG).show() }
            }
        }.start()
    }
}
