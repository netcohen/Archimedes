export interface EnvelopeDoc {
  id: string;
  payload: string;
  operationId?: string;
  createdAt?: Date;
}

export interface WriteResult {
  id: string;
  isDuplicate: boolean;
}

export interface EnvelopeStore {
  write(payload: string, operationId?: string): Promise<WriteResult>;
  read(id: string): Promise<EnvelopeDoc | null>;
  getMode(): FirestoreMode;
}

export type FirestoreMode = "real" | "emulator" | "memory";

const memoryStore = new Map<string, EnvelopeDoc>();
const operationIdIndex = new Map<string, string>();

function createMemoryStore(): EnvelopeStore {
  return {
    async write(payload: string, operationId?: string): Promise<WriteResult> {
      if (operationId && operationIdIndex.has(operationId)) {
        const existingId = operationIdIndex.get(operationId)!;
        return { id: existingId, isDuplicate: true };
      }

      const id = "mem_" + Date.now() + "_" + Math.random().toString(36).slice(2);
      memoryStore.set(id, { id, payload, operationId, createdAt: new Date() });
      
      if (operationId) {
        operationIdIndex.set(operationId, id);
      }
      
      return { id, isDuplicate: false };
    },
    async read(id: string): Promise<EnvelopeDoc | null> {
      return memoryStore.get(id) ?? null;
    },
    getMode(): FirestoreMode {
      return "memory";
    },
  };
}

let cachedStore: EnvelopeStore | null = null;
let detectedMode: FirestoreMode | null = null;

function detectFirestoreMode(): FirestoreMode {
  const explicitMode = process.env.FIRESTORE_MODE?.toLowerCase();
  
  if (explicitMode === "memory") {
    return "memory";
  }
  
  if (explicitMode === "emulator" || process.env.FIRESTORE_EMULATOR_HOST) {
    return "emulator";
  }
  
  if (explicitMode === "real" || process.env.GOOGLE_APPLICATION_CREDENTIALS) {
    return "real";
  }
  
  return "memory";
}

async function createFirestoreStore(mode: "real" | "emulator"): Promise<EnvelopeStore | null> {
  try {
    const admin = await import("firebase-admin");
    
    const projectId = process.env.FIREBASE_PROJECT_ID || "archimedes-c76c3";
    
    if (admin.apps.length === 0) {
      if (mode === "real") {
        const credPath = process.env.GOOGLE_APPLICATION_CREDENTIALS;
        if (!credPath) {
          console.warn("[Firestore] REAL mode requires GOOGLE_APPLICATION_CREDENTIALS");
          return null;
        }
        admin.initializeApp({
          credential: admin.credential.applicationDefault(),
          projectId,
        });
        console.log(`[Firestore] Initialized REAL mode with project: ${projectId}`);
      } else {
        const emulatorHost = process.env.FIRESTORE_EMULATOR_HOST || "localhost:8080";
        process.env.FIRESTORE_EMULATOR_HOST = emulatorHost;
        admin.initializeApp({ projectId });
        console.log(`[Firestore] Initialized EMULATOR mode: ${emulatorHost}`);
      }
    }
    
    const db = admin.firestore();
    
    return {
      async write(payload: string, operationId?: string): Promise<WriteResult> {
        if (operationId) {
          const existing = await db.collection("envelopes")
            .where("operationId", "==", operationId)
            .limit(1)
            .get();
          
          if (!existing.empty) {
            return { id: "fs_" + existing.docs[0].id, isDuplicate: true };
          }
        }

        const ref = await db.collection("envelopes").add({
          payload,
          operationId: operationId || null,
          createdAt: admin.firestore.FieldValue.serverTimestamp(),
        });
        return { id: "fs_" + ref.id, isDuplicate: false };
      },
      async read(id: string): Promise<EnvelopeDoc | null> {
        const docId = id.startsWith("fs_") ? id.slice(3) : id;
        const doc = await db.collection("envelopes").doc(docId).get();
        if (!doc.exists) return null;
        const d = doc.data()!;
        return {
          id: "fs_" + doc.id,
          payload: d.payload,
          operationId: d.operationId,
          createdAt: d.createdAt?.toDate?.(),
        };
      },
      getMode(): FirestoreMode {
        return mode;
      },
    };
  } catch (e) {
    console.error("[Firestore] Failed to initialize:", e);
    return null;
  }
}

export async function getEnvelopeStore(): Promise<EnvelopeStore> {
  if (cachedStore) return cachedStore;
  
  const mode = detectFirestoreMode();
  detectedMode = mode;
  
  if (mode === "memory") {
    console.log("[Firestore] Using MEMORY mode");
    cachedStore = createMemoryStore();
    return cachedStore;
  }
  
  const fsStore = await createFirestoreStore(mode);
  if (fsStore) {
    cachedStore = fsStore;
    return cachedStore;
  }
  
  console.warn("[Firestore] Falling back to MEMORY mode");
  detectedMode = "memory";
  cachedStore = createMemoryStore();
  return cachedStore;
}

export function getCurrentMode(): FirestoreMode {
  return detectedMode ?? detectFirestoreMode();
}

export async function healthCheck(): Promise<{
  ok: boolean;
  mode: FirestoreMode;
  docPath?: string;
  error?: string;
}> {
  try {
    const store = await getEnvelopeStore();
    const mode = store.getMode();
    
    const testPayload = `health_check_${Date.now()}`;
    const result = await store.write(testPayload);
    const doc = await store.read(result.id);
    
    if (!doc || doc.payload !== testPayload) {
      return { ok: false, mode, error: "Write/read mismatch" };
    }
    
    const docPath = mode === "memory" 
      ? `memory/${result.id}` 
      : `envelopes/${result.id.replace("fs_", "")}`;
    
    return { ok: true, mode, docPath };
  } catch (e) {
    return { 
      ok: false, 
      mode: detectedMode ?? "memory", 
      error: String(e) 
    };
  }
}
