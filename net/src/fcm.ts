/**
 * FCM push ping structure (placeholder for Phase 5).
 * Real implementation requires Firebase project + FCM server key.
 */
export interface PushPing {
  envelopeId: string;
  timestamp: number;
}

export function createPushPing(envelopeId: string): PushPing {
  return { envelopeId, timestamp: Date.now() };
}
