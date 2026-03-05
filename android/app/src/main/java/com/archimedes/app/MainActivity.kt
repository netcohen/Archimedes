package com.archimedes.app

import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.widget.Button
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.cardview.widget.CardView

/**
 * Main activity — Archimedes status dashboard.
 * Polls server every 5 seconds while visible.
 */
class MainActivity : AppCompatActivity() {

    private val handler = Handler(Looper.getMainLooper())

    private val pollRunnable = object : Runnable {
        override fun run() {
            refreshStatus()
            handler.postDelayed(this, 5_000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        // Server URL display
        findViewById<TextView>(R.id.tvServerUrl).text = ServerConfig.getNetUrl(this)

        // Navigation buttons
        findViewById<Button>(R.id.btnApprovals).setOnClickListener {
            startActivity(Intent(this, ApprovalActivity::class.java))
        }
        findViewById<Button>(R.id.btnSendTask).setOnClickListener {
            startActivity(Intent(this, TaskActivity::class.java))
        }
        findViewById<Button>(R.id.btnInbox).setOnClickListener {
            startActivity(Intent(this, InboxActivity::class.java))
        }
        findViewById<Button>(R.id.btnSettings).setOnClickListener {
            startActivity(Intent(this, SettingsActivity::class.java))
        }

        // Tap approval card → open ApprovalActivity
        findViewById<CardView>(R.id.cardApprovals).setOnClickListener {
            startActivity(Intent(this, ApprovalActivity::class.java))
        }

        // Start background polling
        PollingWorker.schedule(this)
    }

    override fun onResume() {
        super.onResume()
        handler.post(pollRunnable)
    }

    override fun onPause() {
        super.onPause()
        handler.removeCallbacks(pollRunnable)
    }

    private fun refreshStatus() {
        Thread {
            val api       = ArchimedesApi(this)
            val reachable = api.isReachable()
            val status    = if (reachable) try { api.getStatus() }   catch (_: Exception) { null } else null
            val approvals = if (reachable) try { api.getApprovals() } catch (_: Exception) { emptyList() } else emptyList<ArchimedesApi.Approval>()

            runOnUiThread {
                val tvStatus       = findViewById<TextView>(R.id.tvStatus)
                val tvDescription  = findViewById<TextView>(R.id.tvDescription)
                val tvApprovalBadge = findViewById<TextView>(R.id.tvApprovalBadge)
                val cardApprovals  = findViewById<CardView>(R.id.cardApprovals)

                if (!reachable) {
                    tvStatus.text      = "⚠️ לא מחובר"
                    tvDescription.text = "בדוק שהשרת פועל ושכתובת השרת נכונה"
                    cardApprovals.visibility = View.GONE
                    return@runOnUiThread
                }

                if (status != null) {
                    tvStatus.text = if (status.active) "⚙️ ארכימדס פעיל" else "✅ ארכימדס ממתין"
                    tvDescription.text = buildString {
                        status.description?.let { append(it) }
                        status.selfDev?.let {
                            if (isNotEmpty()) append(" | ")
                            append(it)
                        }
                        status.osState?.let {
                            if (isNotEmpty()) append("\n")
                            append("OS: $it")
                        }
                    }.ifEmpty { if (status.active) "מבצע משימה..." else "ממתין לפקודות" }
                }

                // Approval badge
                if (approvals.isNotEmpty()) {
                    cardApprovals.visibility = View.VISIBLE
                    tvApprovalBadge.text = "⚠️ ${approvals.size} אישור${if (approvals.size > 1) "ים" else ""} ממתין${if (approvals.size > 1) "ים" else ""}"
                } else {
                    cardApprovals.visibility = View.GONE
                }
            }
        }.start()
    }
}
