package com.archimedes.app

import android.util.Log
import com.google.firebase.Timestamp
import com.google.firebase.firestore.ListenerRegistration
import com.google.firebase.firestore.SetOptions
import com.google.firebase.firestore.ktx.firestore
import com.google.firebase.ktx.Firebase
import kotlinx.coroutines.tasks.await

/**
 * Firebase Firestore relay for Archimedes commands and status.
 *
 * ARCHITECTURE (remote access — phone works from anywhere):
 *   Android ──Firestore.add(command)──▶ Firebase Cloud
 *   Net (home, outbound only) ──polls Firestore──▶ finds command
 *   Net ──HTTP──▶ Core (local) ──executes──▶ Net ──Firestore.update(result)──▶ Firebase Cloud
 *   Android Firestore listener ──▶ sees result in real-time
 *
 * SECURITY NOTE:
 *   Payload passes through Firestore as an encrypted envelope.
 *   The encryption key is established during the pairing phase (QR + SecretInputActivity).
 *   Sensitive data (task text, results) is never stored in plaintext.
 *   Collection path: /commands/{id}  |  /status/current  |  /devices/{deviceId}
 */
object FirestoreManager {

    private const val TAG          = "FirestoreManager"
    private const val COL_COMMANDS = "commands"
    private const val DOC_STATUS   = "status/current"
    private const val COL_DEVICES  = "devices"

    private val db get() = Firebase.firestore

    // Active Firestore listeners — must be removed to avoid leaks
    private var statusListener: ListenerRegistration? = null
    private val resultListeners = mutableMapOf<String, ListenerRegistration>()

    // ── Commands ──────────────────────────────────────────────────────────────

    data class CommandResult(
        val id:     String,
        val status: String,                      // "DONE" | "FAILED"
        val result: Map<String, Any>?
    )

    /**
     * Write a command to Firestore /commands.
     * Net service polls this collection and relays to Core via local HTTP.
     *
     * @param type    "TASK" | "GOAL" | "CHAT" | "APPROVE" | "DENY"
     * @param payload encrypted envelope map (text, approval id, etc.)
     * @param deviceId this device's persistent ID from ServerConfig
     * @return Firestore document ID (used to listen for result)
     */
    suspend fun sendCommand(
        type:     String,
        payload:  Map<String, Any>,
        deviceId: String
    ): String {
        val doc = hashMapOf(
            "type"      to type,
            "payload"   to payload,
            "deviceId"  to deviceId,
            "status"    to "PENDING",
            "createdAt" to Timestamp.now()
        )
        val ref = db.collection(COL_COMMANDS).add(doc).await()
        Log.d(TAG, "Command sent to Firestore: ${ref.id} type=$type")
        return ref.id
    }

    /**
     * Real-time listener for the result of a specific command.
     * Fires once when status changes to DONE or FAILED, then auto-removes.
     */
    fun listenForResult(commandId: String, onResult: (CommandResult) -> Unit) {
        val reg = db.collection(COL_COMMANDS).document(commandId)
            .addSnapshotListener { snap, err ->
                if (err != null || snap == null) return@addSnapshotListener
                val status = snap.getString("status") ?: return@addSnapshotListener
                if (status == "DONE" || status == "FAILED") {
                    @Suppress("UNCHECKED_CAST")
                    val result = snap.get("result") as? Map<String, Any>
                    onResult(CommandResult(commandId, status, result))
                    resultListeners.remove(commandId)?.remove()
                }
            }
        resultListeners[commandId] = reg
        Log.d(TAG, "Listening for result on command $commandId")
    }

    // ── Status ────────────────────────────────────────────────────────────────

    data class ArchimedesStatus(
        val active:      Boolean,
        val description: String?,
        val osState:     String?,
        val updatedAt:   Long       // epoch seconds
    )

    /**
     * Real-time listener on /status/current.
     * Net service writes here whenever Core's status changes.
     * Works from anywhere — no home network connection needed.
     */
    fun listenForStatus(onStatus: (ArchimedesStatus) -> Unit) {
        stopStatusListener()
        statusListener = db.document(DOC_STATUS)
            .addSnapshotListener { snap, err ->
                if (err != null || snap == null || !snap.exists()) return@addSnapshotListener
                val s = ArchimedesStatus(
                    active      = snap.getBoolean("active") ?: false,
                    description = snap.getString("description"),
                    osState     = snap.getString("osState"),
                    updatedAt   = snap.getTimestamp("updatedAt")?.seconds ?: 0L
                )
                onStatus(s)
            }
        Log.d(TAG, "Listening for status at $DOC_STATUS")
    }

    fun stopStatusListener() {
        statusListener?.remove()
        statusListener = null
    }

    // ── FCM token registration ─────────────────────────────────────────────────

    /**
     * Write this device's FCM token to Firestore /devices/{deviceId}.
     * Net service syncs from here → passes to firebase-admin for push delivery.
     * This works even when the phone is not on the home network.
     */
    suspend fun registerFcmToken(deviceId: String, token: String) {
        db.document("$COL_DEVICES/$deviceId").set(
            hashMapOf(
                "fcmToken"  to token,
                "updatedAt" to Timestamp.now()
            ),
            SetOptions.merge()
        ).await()
        Log.d(TAG, "FCM token registered in Firestore for device: $deviceId")
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    /** Remove all active listeners. Call from Activity.onDestroy(). */
    fun cleanup() {
        stopStatusListener()
        resultListeners.values.forEach { it.remove() }
        resultListeners.clear()
        Log.d(TAG, "All Firestore listeners cleaned up")
    }
}
