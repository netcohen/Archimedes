export interface EnvelopeDoc {
  id: string;
  payload: string;
  createdAt?: Date;
}

export interface EnvelopeStore {
  write(payload: string): Promise<string>;
  read(id: string): Promise<EnvelopeDoc | null>;
}

const memoryStore = new Map<string, EnvelopeDoc>();

export const memoryEnvelopeStore: EnvelopeStore = {
  async write(payload: string): Promise<string> {
    const id = "mem_" + Date.now() + "_" + Math.random().toString(36).slice(2);
    memoryStore.set(id, { id, payload, createdAt: new Date() });
    return id;
  },
  async read(id: string): Promise<EnvelopeDoc | null> {
    return memoryStore.get(id) ?? null;
  },
};

let firestoreStore: EnvelopeStore | null = null;

async function initFirestoreStore(): Promise<EnvelopeStore | null> {
  if (firestoreStore) return firestoreStore;
  if (!process.env.FIRESTORE_EMULATOR_HOST) return null;
  try {
    const admin = await import("firebase-admin");
    if (admin.apps.length === 0) {
      admin.initializeApp({ projectId: "archimedes-mvp" });
    }
    const db = admin.firestore();
    firestoreStore = {
      async write(payload: string): Promise<string> {
        const ref = await db.collection("envelopes").add({
          payload,
          createdAt: admin.firestore.FieldValue.serverTimestamp(),
        });
        return ref.id;
      },
      async read(id: string): Promise<EnvelopeDoc | null> {
        const doc = await db.collection("envelopes").doc(id).get();
        if (!doc.exists) return null;
        const d = doc.data()!;
        return {
          id: doc.id,
          payload: d.payload,
          createdAt: d.createdAt?.toDate?.(),
        };
      },
    };
    return firestoreStore;
  } catch {
    return null;
  }
}

export async function getEnvelopeStore(): Promise<EnvelopeStore> {
  const fs = await initFirestoreStore();
  return fs ?? memoryEnvelopeStore;
}
