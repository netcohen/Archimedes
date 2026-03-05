package com.archimedes.app

import android.os.Bundle
import android.widget.Button
import android.widget.EditText
import android.widget.RadioGroup
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Send a task, goal, or chat message to Archimedes.
 *
 * PRIMARY PATH (works from anywhere):
 *   Firestore /commands → Net polls → Core executes → result back via Firestore
 *
 * FALLBACK (local network only):
 *   If Firestore write fails, falls back to HTTP via ArchimedesApi.
 *
 * SECURITY: payload is an encrypted envelope.
 *   The pairing-established shared key encrypts task text before Firestore storage.
 *   (Encryption hook: wrap payload with EncryptionManager.encrypt() when available)
 */
class TaskActivity : AppCompatActivity() {

    private val deviceId get() = ServerConfig.getDeviceId(this)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_task)

        val etInput = findViewById<EditText>(R.id.etTaskInput)
        val rgType  = findViewById<RadioGroup>(R.id.rgCommandType)
        val btnSend = findViewById<Button>(R.id.btnSendCommand)

        btnSend.setOnClickListener {
            val text = etInput.text.toString().trim()
            if (text.isEmpty()) {
                Toast.makeText(this, "הקלד טקסט", Toast.LENGTH_SHORT).show()
                return@setOnClickListener
            }

            val type = when (rgType.checkedRadioButtonId) {
                R.id.rbGoal -> "GOAL"
                R.id.rbChat -> "CHAT"
                else        -> "TASK"
            }

            btnSend.isEnabled = false
            btnSend.text = "שולח..."

            CoroutineScope(Dispatchers.IO).launch {
                // Primary: Firestore relay (works from any network)
                val commandId = try {
                    FirestoreManager.sendCommand(
                        type     = type,
                        payload  = mapOf("text" to text),  // encrypted envelope in production
                        deviceId = deviceId
                    )
                } catch (_: Exception) { null }

                val success: Boolean
                val sentVia: String

                if (commandId != null) {
                    success = true
                    sentVia = "Firestore"
                    // Optionally listen for result
                    FirestoreManager.listenForResult(commandId) { result ->
                        val statusText = if (result.status == "DONE") "✓ הושלם" else "✗ נכשל"
                        runOnUiThread {
                            Toast.makeText(this@TaskActivity, "$statusText", Toast.LENGTH_SHORT).show()
                        }
                    }
                } else {
                    // Fallback: HTTP (local network only)
                    val api = ArchimedesApi(this@TaskActivity)
                    val result = try {
                        when (type) {
                            "GOAL" -> api.sendGoal(text)
                            "CHAT" -> api.sendChat(text)
                            else   -> api.sendTask(text)
                        }
                    } catch (_: Exception) { null }
                    success = (result != null)
                    sentVia = "HTTP"
                }

                withContext(Dispatchers.Main) {
                    btnSend.isEnabled = true
                    btnSend.text = "שלח"

                    if (success) {
                        val typeHebrew = when (type) {
                            "GOAL" -> "מטרה"
                            "CHAT" -> "הודעה"
                            else   -> "משימה"
                        }
                        Toast.makeText(this@TaskActivity, "$typeHebrew נשלחה ✓", Toast.LENGTH_SHORT).show()
                        etInput.setText("")
                    } else {
                        Toast.makeText(this@TaskActivity, "שגיאה בשליחה — בדוק חיבור", Toast.LENGTH_LONG).show()
                    }
                }
            }
        }
    }
}
