package com.archimedes.app

import android.os.Bundle
import android.widget.Button
import android.widget.EditText
import android.widget.RadioGroup
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity

/**
 * Send a task, goal, or chat message to Archimedes.
 */
class TaskActivity : AppCompatActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_task)

        val etInput   = findViewById<EditText>(R.id.etTaskInput)
        val rgType    = findViewById<RadioGroup>(R.id.rgCommandType)
        val btnSend   = findViewById<Button>(R.id.btnSendCommand)

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

            Thread {
                val api    = ArchimedesApi(this)
                val result = try {
                    when (type) {
                        "GOAL" -> api.sendGoal(text)
                        "CHAT" -> api.sendChat(text)
                        else   -> api.sendTask(text)
                    }
                } catch (_: Exception) { null }

                runOnUiThread {
                    btnSend.isEnabled = true
                    btnSend.text = "שלח"

                    if (result != null) {
                        val typeHebrew = when (type) {
                            "GOAL" -> "מטרה"
                            "CHAT" -> "הודעה"
                            else   -> "משימה"
                        }
                        Toast.makeText(this, "$typeHebrew נשלחה ✓", Toast.LENGTH_SHORT).show()
                        etInput.setText("")
                    } else {
                        Toast.makeText(this, "שגיאה בשליחה — בדוק חיבור", Toast.LENGTH_LONG).show()
                    }
                }
            }.start()
        }
    }
}
