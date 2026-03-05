/**
 * Firestore Commands Poller — runs on home server inside the Net service.
 *
 * ARCHITECTURE:
 *   Android (anywhere) → Firestore /commands ← Net polls every 5 s
 *   Net creates in-memory command → AndroidBridge (Core) picks up via HTTP poll
 *   Core executes → posts result to Net → Net updates Firestore doc
 *   Android Firestore listener → sees result (real-time)
 *
 * STATUS PUSH:
 *   Every 10 s, Net fetches Core /status/current → writes to Firestore /status/current
 *   Android Firestore listener → sees live status from anywhere
 *
 * FCM TOKEN SYNC:
 *   Net reads /devices collection → registers tokens in fcm.ts in-memory map
 *   Ensures FCM push works even if Android wasn't on home network at first launch.
 *
 * SECURITY:
 *   Payload passes through Firestore as encrypted envelope.
 *   The pairing-established key encrypts task text. Net forwards as-is to Core.
 */
import * as http from 'http';
import { createCommand, updateCommand, CommandType } from './commands';
import { registerToken } from './fcm';

const CORE_URL          = 'http://localhost:5051';
const COMMANDS_COL      = 'commands';
const STATUS_DOC        = 'status/current';
const DEVICES_COL       = 'devices';
const POLL_INTERVAL_MS  = 5_000;   // command poll
const STATUS_INTERVAL_MS = 10_000; // status push
const TOKEN_INTERVAL_MS  = 60_000; // FCM token sync

// Firestore instance shared across this module
let _db: any = null;
let _adminRef: any = null;
let _cmdInterval: NodeJS.Timeout | null = null;
let _statusInterval: NodeJS.Timeout | null = null;
let _tokenInterval: NodeJS.Timeout | null = null;

// Firestore docId → in-memory command id mapping (for result writes)
const _fsToMemory = new Map<string, string>(); // firestoreDocId → inMemoryId

// ── Firebase Admin init ───────────────────────────────────────────────────────

async function getDb(): Promise<any | null> {
  if (_db) return _db;

  try {
    const admin = await import('firebase-admin');
    _adminRef = admin;

    if (admin.apps.length === 0) {
      const credPath  = process.env.GOOGLE_APPLICATION_CREDENTIALS;
      const projectId = process.env.FIREBASE_PROJECT_ID || 'archimedes-c76c3';

      if (!credPath) {
        // Not configured — Firestore relay disabled, polling-only mode
        return null;
      }

      admin.initializeApp({
        credential: admin.credential.applicationDefault(),
        projectId,
      });
      console.log('[FirestorePoller] firebase-admin initialized — real Firestore mode');
    }

    _db = admin.firestore();
    return _db;
  } catch (e) {
    console.warn('[FirestorePoller] firebase-admin unavailable:', String(e).slice(0, 80));
    return null;
  }
}

// ── Command relay ─────────────────────────────────────────────────────────────

/**
 * Poll Firestore /commands for PENDING documents.
 * For each: mark RUNNING in Firestore, add to Net's in-memory command queue.
 * AndroidBridge in Core polls in-memory queue → executes → posts result to Net.
 * Net's result handler calls updateFirestoreResult() to sync back to Firestore.
 */
async function pollCommands(): Promise<void> {
  const db = await getDb();
  if (!db) return;

  try {
    const snap = await db.collection(COMMANDS_COL)
      .where('status', '==', 'PENDING')
      .orderBy('createdAt', 'asc')
      .limit(5)
      .get();

    for (const doc of snap.docs) {
      const data       = doc.data();
      const firestoreId = doc.id;

      // Skip if already queued in memory (idempotent)
      if (_fsToMemory.has(firestoreId)) continue;

      // Mark RUNNING in Firestore
      await doc.ref.update({ status: 'RUNNING' });

      // Add to in-memory queue — deviceId encodes the Firestore doc ID
      // so result handler can write result back to Firestore
      const payload = typeof data.payload === 'object' ? data.payload : { raw: data.payload };
      const cmd = createCommand(
        data.type as CommandType,
        payload,
        `fs:${firestoreId}`   // sentinel prefix for result handler
      );

      _fsToMemory.set(firestoreId, cmd.id);
      console.log(`[FirestorePoller] Queued Firestore command ${firestoreId} → mem:${cmd.id} type=${data.type}`);
    }
  } catch (e: any) {
    // Silently skip transient errors; log persistent ones
    if (!String(e).includes('No document')) {
      console.warn('[FirestorePoller] pollCommands error:', String(e).slice(0, 100));
    }
  }
}

/**
 * Called by the result handler in index.ts when a command with
 * deviceId.startsWith("fs:") completes. Writes result to Firestore.
 */
export async function updateFirestoreResult(
  firestoreDocId: string,
  status: 'DONE' | 'FAILED',
  result?: Record<string, unknown>
): Promise<void> {
  const db = await getDb();
  if (!db) return;

  try {
    await db.collection(COMMANDS_COL).doc(firestoreDocId).update({
      status,
      result: result ?? {},
      completedAt: _adminRef?.firestore?.FieldValue?.serverTimestamp?.() ?? new Date(),
    });
    _fsToMemory.delete(firestoreDocId);
    console.log(`[FirestorePoller] Firestore doc ${firestoreDocId} → ${status}`);
  } catch (e) {
    console.warn('[FirestorePoller] updateFirestoreResult error:', String(e).slice(0, 80));
  }
}

// ── Status push ───────────────────────────────────────────────────────────────

/**
 * Fetch Core /status/current and write to Firestore /status/current.
 * Android Firestore listener picks this up in real-time.
 */
async function pushCoreStatus(): Promise<void> {
  const db = await getDb();
  if (!db) return;

  const status = await fetchCoreStatus();
  if (!status) return;

  try {
    await db.doc(STATUS_DOC).set({
      active:      status.active      ?? false,
      description: status.description ?? null,
      osState:     status.osHealth?.state ?? null,
      updatedAt:   _adminRef?.firestore?.FieldValue?.serverTimestamp?.() ?? new Date(),
    }, { merge: true });
  } catch (e) {
    // Non-critical
    console.warn('[FirestorePoller] pushStatus failed:', String(e).slice(0, 60));
  }
}

function fetchCoreStatus(): Promise<any | null> {
  return new Promise((resolve) => {
    const req = http.get(CORE_URL + '/status/current', (res) => {
      let data = '';
      res.on('data', (chunk: string) => (data += chunk));
      res.on('end', () => {
        try { resolve(JSON.parse(data)); }
        catch { resolve(null); }
      });
    });
    req.on('error', () => resolve(null));
    req.setTimeout(5_000, () => { req.destroy(); resolve(null); });
  });
}

// ── FCM token sync ────────────────────────────────────────────────────────────

/**
 * Read /devices collection and register tokens in fcm.ts in-memory map.
 * Ensures Net service knows every device's FCM token even after restart.
 */
async function syncFcmTokens(): Promise<void> {
  const db = await getDb();
  if (!db) return;

  try {
    const snap = await db.collection(DEVICES_COL).get();
    let count = 0;
    for (const doc of snap.docs) {
      const { fcmToken } = doc.data();
      if (fcmToken && typeof fcmToken === 'string') {
        registerToken(doc.id, fcmToken);
        count++;
      }
    }
    if (count > 0) console.log(`[FirestorePoller] Synced ${count} FCM token(s) from Firestore`);
  } catch (e) {
    // Expected if collection doesn't exist yet
  }
}

// ── Public API ────────────────────────────────────────────────────────────────

/** Start all background Firestore pollers. Safe to call multiple times. */
export async function startFirestorePoller(): Promise<void> {
  if (_cmdInterval) return;  // already running

  const db = await getDb();
  if (!db) {
    console.log('[FirestorePoller] GOOGLE_APPLICATION_CREDENTIALS not set — Firestore relay disabled');
    console.log('[FirestorePoller]   Commands: in-memory relay only (local network)');
    console.log('[FirestorePoller]   Status:   HTTP polling only');
    return;
  }

  // Initial sync
  await syncFcmTokens().catch(() => {});
  await pushCoreStatus().catch(() => {});

  // Command poll every 5 s
  _cmdInterval = setInterval(() => {
    pollCommands().catch(() => {});
  }, POLL_INTERVAL_MS);

  // Status push every 10 s
  _statusInterval = setInterval(() => {
    pushCoreStatus().catch(() => {});
  }, STATUS_INTERVAL_MS);

  // FCM token sync every 60 s
  _tokenInterval = setInterval(() => {
    syncFcmTokens().catch(() => {});
  }, TOKEN_INTERVAL_MS);

  console.log('[FirestorePoller] Started — polling commands every 5 s, pushing status every 10 s');
}

/** Stop all pollers. */
export function stopFirestorePoller(): void {
  if (_cmdInterval)    { clearInterval(_cmdInterval);    _cmdInterval = null; }
  if (_statusInterval) { clearInterval(_statusInterval); _statusInterval = null; }
  if (_tokenInterval)  { clearInterval(_tokenInterval);  _tokenInterval = null; }
  _db = null;
  console.log('[FirestorePoller] Stopped');
}

/** True if Firestore relay is active (GOOGLE_APPLICATION_CREDENTIALS configured). */
export function isFirestoreRelayActive(): boolean {
  return _cmdInterval !== null;
}
