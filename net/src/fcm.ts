/**
 * FCM (Firebase Cloud Messaging) — server-side push notifications.
 *
 * Requires GOOGLE_APPLICATION_CREDENTIALS env var → path to Firebase service account JSON.
 * Falls back gracefully to HTTP-polling queue when not configured.
 *
 * Phase 31: Two-channel delivery:
 *   1. FCM push (real-time, requires credentials) — fires if configured
 *   2. Polling queue (always active) — Android polls GET /v1/android/notifications
 */
import { randomUUID } from 'crypto';

// Lazy import so the module loads even when firebase-admin isn't fully configured
let _admin: typeof import('firebase-admin') | null = null;
let _initialized = false;
let _fcmReady = false;

// deviceId → FCM registration token (sent from Android on first launch)
const _tokens = new Map<string, string>();

// Polling fallback: notifications queued for HTTP delivery
export interface PendingNotification {
  id: string;
  title: string;
  body: string;
  data?: Record<string, string>;
  createdAt: string;
  read: boolean;
}
const _queue: PendingNotification[] = [];

// ── Initialization ─────────────────────────────────────────────────────────

function ensureInit(): boolean {
  if (_initialized) return _fcmReady;
  _initialized = true;

  const credPath = process.env.GOOGLE_APPLICATION_CREDENTIALS;
  if (!credPath) {
    console.log('[FCM] GOOGLE_APPLICATION_CREDENTIALS not set — FCM push disabled, polling active');
    return false;
  }

  try {
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    _admin = require('firebase-admin') as typeof import('firebase-admin');
    if (!_admin.apps.length) {
      _admin.initializeApp({ credential: _admin.credential.applicationDefault() });
    }
    _fcmReady = true;
    console.log('[FCM] Firebase Admin SDK initialized ✓');
    return true;
  } catch (e) {
    console.warn('[FCM] Firebase init failed:', e);
    return false;
  }
}

// ── Token registry ─────────────────────────────────────────────────────────

export function registerToken(deviceId: string, token: string): void {
  _tokens.set(deviceId, token);
  console.log(`[FCM] Registered FCM token for device: ${deviceId}`);
}

export function getStatus(): { ready: boolean; devices: number; pendingCount: number } {
  return {
    ready: _fcmReady,
    devices: _tokens.size,
    pendingCount: _queue.filter(n => !n.read).length,
  };
}

// ── Polling queue ──────────────────────────────────────────────────────────

export function getPendingNotifications(): PendingNotification[] {
  return _queue.filter(n => !n.read);
}

export function markRead(id: string): boolean {
  const n = _queue.find(n => n.id === id);
  if (!n) return false;
  n.read = true;
  return true;
}

export function markAllRead(): number {
  let count = 0;
  for (const n of _queue) {
    if (!n.read) { n.read = true; count++; }
  }
  return count;
}

function enqueue(title: string, body: string, data?: Record<string, string>): PendingNotification {
  const n: PendingNotification = {
    id: randomUUID(),
    title,
    body,
    data,
    createdAt: new Date().toISOString(),
    read: false,
  };
  _queue.push(n);
  if (_queue.length > 200) _queue.splice(0, _queue.length - 200);
  return n;
}

// ── Send API ───────────────────────────────────────────────────────────────

export interface FcmPayload {
  title: string;
  body: string;
  data?: Record<string, string>;
}

export interface SendResult {
  sent: boolean;   // FCM push delivered
  queued: boolean; // Added to polling queue
  error?: string;
}

/** Send to a specific device by deviceId */
export async function sendToDevice(deviceId: string, payload: FcmPayload): Promise<SendResult> {
  enqueue(payload.title, payload.body, payload.data);

  const token = _tokens.get(deviceId);
  if (!token) {
    return { sent: false, queued: true, error: `No FCM token registered for device: ${deviceId}` };
  }

  if (!ensureInit() || !_admin) {
    return { sent: false, queued: true, error: 'FCM not configured (missing GOOGLE_APPLICATION_CREDENTIALS)' };
  }

  try {
    const msgId = await _admin.messaging().send({
      token,
      notification: { title: payload.title, body: payload.body },
      data: payload.data ?? {},
      android: { priority: 'high', notification: { channelId: 'archimedes_alerts' } },
    });
    console.log(`[FCM] Pushed to device ${deviceId}: ${msgId}`);
    return { sent: true, queued: true };
  } catch (e) {
    console.warn(`[FCM] Push failed for device ${deviceId}:`, e);
    return { sent: false, queued: true, error: String(e) };
  }
}

/** Broadcast to all registered devices */
export async function sendToAll(payload: FcmPayload): Promise<{ sent: number; failed: number; queued: boolean }> {
  enqueue(payload.title, payload.body, payload.data);
  if (!ensureInit() || _tokens.size === 0) return { sent: 0, failed: 0, queued: true };

  let sent = 0;
  let failed = 0;
  for (const [deviceId] of _tokens) {
    const r = await sendToDevice(deviceId, payload);
    if (r.sent) sent++; else failed++;
  }
  return { sent, failed, queued: true };
}
