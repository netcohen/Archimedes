package com.archimedes.app

import android.os.Bundle
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity

/**
 * Settings screen — configure server URL and view device ID.
 */
class SettingsActivity : AppCompatActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)

        val etUrl       = findViewById<EditText>(R.id.etServerUrl)
        val tvDeviceId  = findViewById<TextView>(R.id.tvDeviceId)
        val btnSave     = findViewById<Button>(R.id.btnSaveSettings)
        val btnTest     = findViewById<Button>(R.id.btnTestConnection)

        // Populate current values
        etUrl.setText(ServerConfig.getNetUrl(this))
        tvDeviceId.text = "Device ID: ${ServerConfig.getDeviceId(this)}"

        btnSave.setOnClickListener {
            val url = etUrl.text.toString().trim()
            if (url.isEmpty() || (!url.startsWith("http://") && !url.startsWith("https://"))) {
                Toast.makeText(this, "כתובת לא תקינה (חייבת להתחיל ב-http://)", Toast.LENGTH_LONG).show()
                return@setOnClickListener
            }
            ServerConfig.setNetUrl(this, url)
            Toast.makeText(this, "כתובת השרת עודכנה ✓", Toast.LENGTH_SHORT).show()
        }

        btnTest.setOnClickListener {
            btnTest.isEnabled = false
            btnTest.text = "בודק..."
            Thread {
                val api = ArchimedesApi(this)
                val ok = api.isReachable()
                runOnUiThread {
                    btnTest.isEnabled = true
                    btnTest.text = "בדוק חיבור"
                    if (ok) {
                        Toast.makeText(this, "✅ השרת מגיב!", Toast.LENGTH_SHORT).show()
                    } else {
                        Toast.makeText(this, "❌ השרת לא מגיב — בדוק כתובת ו-IP", Toast.LENGTH_LONG).show()
                    }
                }
            }.start()
        }
    }
}
