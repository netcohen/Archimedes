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
 *
 * STATUS (primary):  Firestore real-time listener via FirestoreManager.
 *                    Works from anywhere — no home network required.
 *
 * APPROVALS (HTTP):  Poll every 10 s while visible (local network only).
 *                    Approvals also arrive as FCM push notifications.
 *
 * BACKGROUND:        WorkManager PollingWorker runs every 15 min.
 */
class MainActivity : AppCompatActivity() {

    private val handler = Handler(Looper.getMainLooper())

    // HTTP approval poll — shows badge when on local network
    private val approvalPollRunnable = object : Runnable {
        override fun run() {
            refreshApprovals()
            handler.postDelayed(this, 10_000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        // Sub-title: indicate Firestore mode
        findViewById<TextView>(R.id.tvServerUrl).text = "ארכיטקטורת Firebase — זמינות מכל רשת"

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

        // Start WorkManager background worker
        PollingWorker.schedule(this)

        // Primary: Firestore real-time status listener (works from any network)
        startFirestoreStatusListener()
    }

    override fun onResume() {
        super.onResume()
        handler.post(approvalPollRunnable)
    }

    override fun onPause() {
        super.onPause()
        handler.removeCallbacks(approvalPollRunnable)
    }

    override fun onDestroy() {
        super.onDestroy()
        FirestoreManager.stopStatusListener()
    }

    // ── Firestore real-time status (works from anywhere) ──────────────────────

    private fun startFirestoreStatusListener() {
        FirestoreManager.listenForStatus { status ->
            runOnUiThread { updateStatusUi(status) }
        }
    }

    private fun updateStatusUi(status: FirestoreManager.ArchimedesStatus) {
        val tvStatus      = findViewById<TextView>(R.id.tvStatus)
        val tvDescription = findViewById<TextView>(R.id.tvDescription)

        tvStatus.text = if (status.active) "⚙️ ארכימדס פעיל" else "✅ ארכימדס ממתין"
        tvDescription.text = buildString {
            status.description?.let { append(it) }
            status.osState?.let {
                if (isNotEmpty()) append("\n")
                append("OS: $it")
            }
        }.ifEmpty { if (status.active) "מבצע משימה..." else "ממתין לפקודות" }
    }

    // ── HTTP approval badge (local network only — FCM push also covers remote) ─

    private fun refreshApprovals() {
        Thread {
            val api       = ArchimedesApi(this)
            val reachable = api.isReachable()
            val approvals = if (reachable) {
                try { api.getApprovals() } catch (_: Exception) { emptyList() }
            } else {
                emptyList<ArchimedesApi.Approval>()
            }

            runOnUiThread {
                val tvApprovalBadge = findViewById<TextView>(R.id.tvApprovalBadge)
                val cardApprovals   = findViewById<CardView>(R.id.cardApprovals)

                if (approvals.isNotEmpty()) {
                    cardApprovals.visibility = View.VISIBLE
                    tvApprovalBadge.text = buildString {
                        append("⚠️ ${approvals.size} ")
                        append("אישור${if (approvals.size > 1) "ים" else ""} ")
                        append("ממתין${if (approvals.size > 1) "ים" else ""}")
                    }
                } else {
                    cardApprovals.visibility = View.GONE
                }
            }
        }.start()
    }
}
