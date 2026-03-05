import * as http from "http";
import { getEnvelopeStore, getCurrentMode, healthCheck } from "./firestore";
import { safeLogPayload, safeLog } from "./redactor";
import { handleTestsite } from "./testsite";
import { runBrowserSteps, getRunStatus, getAllRuns, isBrowserAvailable, BrowserStep } from "./browser";
import {
  registerToken, sendToDevice, sendToAll, getPendingNotifications,
  markRead, markAllRead, getStatus as getFcmStatus, FcmPayload
} from "./fcm";
import {
  createCommand, getPendingCommands, updateCommand, getCommand,
  getAllCommands, purgeOldCommands, CommandType
} from "./commands";
import {
  startFirestorePoller, updateFirestoreResult, isFirestoreRelayActive
} from "./firestorePoller";

const PORT = parseInt(process.env.PORT || "5052", 10);
const CORE_URL = "http://localhost:5051";

const envelopeQueue: string[] = [];

// Phase 32: Device registry — stores per-device info (IP, FCM token) for OTA updates
interface DeviceInfo { deviceId: string; fcmToken?: string; ip?: string; updatedAt: string; }
const _deviceRegistry = new Map<string, DeviceInfo>();
function registerDevice(deviceId: string, patch: Partial<Omit<DeviceInfo, 'deviceId' | 'updatedAt'>>): DeviceInfo {
  const existing = _deviceRegistry.get(deviceId) ?? { deviceId, updatedAt: new Date().toISOString() };
  const updated  = { ...existing, ...patch, deviceId, updatedAt: new Date().toISOString() };
  _deviceRegistry.set(deviceId, updated);
  return updated;
}

const server = http.createServer((req, res) => {
  if (handleTestsite(req, res)) {
    return;
  }
  if (req.method === "GET" && req.url === "/health") {
    res.writeHead(200, { "Content-Type": "text/plain" });
    res.end("OK");
    return;
  }
  if (req.method === "POST" && req.url === "/envelope") {
    let body = "";
    req.on("data", (chunk) => (body += chunk.toString()));
    req.on("end", () => {
      envelopeQueue.push(body || "{}");
      safeLogPayload("Net", body || "");
      res.writeHead(200, { "Content-Type": "text/plain" });
      res.end("OK");
    });
    return;
  }
  if (req.method === "GET" && req.url === "/envelope") {
    const msg = envelopeQueue.shift();
    res.writeHead(200, { "Content-Type": "text/plain" });
    res.end(msg ?? "");
    return;
  }
  if (req.method === "POST" && req.url === "/from-android") {
    let body = "";
    req.on("data", (chunk) => (body += chunk.toString()));
    req.on("end", () => {
      const payload = body || "{}";
      http.request(
        {
          hostname: "localhost",
          port: 5051,
          path: "/envelope",
          method: "POST",
          headers: { "Content-Type": "text/plain", "Content-Length": Buffer.byteLength(payload) },
        },
        (up) => {
          up.on("data", () => {});
          up.on("end", () => {
            res.writeHead(200, { "Content-Type": "text/plain" });
            res.end("OK");
          });
        }
      )
        .on("error", (e) => {
          res.writeHead(502);
          res.end(JSON.stringify({ error: String(e) }));
        })
        .end(payload);
    });
    return;
  }
  if (req.method === "GET" && req.url === "/approvals") {
    http.get(CORE_URL + "/approvals", (up) => {
      let d = "";
      up.on("data", (c) => (d += c));
      up.on("end", () => {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(d);
      });
    }).on("error", () => {
      res.writeHead(502);
      res.end("[]");
    });
    return;
  }
  if (req.method === "POST" && req.url === "/approval-response") {
    let body = "";
    req.on("data", (chunk) => (body += chunk.toString()));
    req.on("end", () => {
      const r = http.request(
        {
          hostname: "localhost",
          port: 5051,
          path: "/approval-response",
          method: "POST",
          headers: { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) },
        },
        (up) => {
          let d = "";
          up.on("data", (c) => (d += c));
          up.on("end", () => {
            res.writeHead(up.statusCode ?? 200);
            res.end(d);
          });
        }
      );
      r.on("error", () => { res.writeHead(502); res.end(); });
      r.end(body);
    });
    return;
  }
  if (req.method === "POST" && req.url === "/firestore-test") {
    let body = "";
    req.on("data", (chunk) => (body += chunk.toString()));
    req.on("end", async () => {
      try {
        const store = await getEnvelopeStore();
        const mode = store.getMode();
        const payload = body || "test envelope";
        const operationId = req.headers["x-operation-id"] as string | undefined;
        const result = await store.write(payload, operationId);
        const doc = await store.read(result.id);
        const ok = doc !== null && doc.payload === payload;
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ 
          id: result.id, 
          ok, 
          mode, 
          duplicate: result.isDuplicate,
          readPayload: doc?.payload 
        }));
      } catch (e) {
        res.writeHead(500);
        res.end(JSON.stringify({ error: String(e) }));
      }
    });
    return;
  }
  if (req.method === "POST" && req.url === "/v1/envelope/idempotent") {
    let body = "";
    req.on("data", (chunk) => (body += chunk.toString()));
    req.on("end", async () => {
      try {
        const store = await getEnvelopeStore();
        const operationId = req.headers["x-operation-id"] as string | undefined;
        if (!operationId) {
          res.writeHead(400, { "Content-Type": "application/json" });
          res.end(JSON.stringify({ error: "X-Operation-Id header required" }));
          return;
        }
        const result = await store.write(body, operationId);
        res.writeHead(result.isDuplicate ? 200 : 201, { "Content-Type": "application/json" });
        res.end(JSON.stringify({
          id: result.id,
          duplicate: result.isDuplicate,
          mode: store.getMode()
        }));
      } catch (e) {
        res.writeHead(500, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: String(e) }));
      }
    });
    return;
  }
  if (req.method === "GET" && req.url === "/v1/firebase/health") {
    (async () => {
      try {
        const result = await healthCheck();
        res.writeHead(result.ok ? 200 : 500, { "Content-Type": "application/json" });
        res.end(JSON.stringify(result));
      } catch (e) {
        res.writeHead(500, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ ok: false, mode: getCurrentMode(), error: String(e) }));
      }
    })();
    return;
  }
  if (req.method === "POST" && req.url === "/v1/firebase/health") {
    (async () => {
      try {
        const result = await healthCheck();
        res.writeHead(result.ok ? 200 : 500, { "Content-Type": "application/json" });
        res.end(JSON.stringify(result));
      } catch (e) {
        res.writeHead(500, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ ok: false, mode: getCurrentMode(), error: String(e) }));
      }
    })();
    return;
  }

  if (req.method === "GET" && req.url === "/tool/browser/health") {
    (async () => {
      try {
        const available = await isBrowserAvailable();
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ available }));
      } catch (e) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ available: false, error: String(e) }));
      }
    })();
    return;
  }

  if (req.method === "POST" && req.url === "/tool/browser/runStep") {
    let body = "";
    req.on("data", (chunk) => (body += chunk.toString()));
    req.on("end", async () => {
      try {
        const { steps, runId } = JSON.parse(body) as { steps: BrowserStep[]; runId?: string };
        if (!steps || !Array.isArray(steps)) {
          res.writeHead(400, { "Content-Type": "application/json" });
          res.end(JSON.stringify({ error: "steps array required" }));
          return;
        }
        safeLog("Browser", `Running ${steps.length} steps`);
        const result = await runBrowserSteps(steps, runId);
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify(result));
      } catch (e) {
        res.writeHead(500, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: String(e) }));
      }
    });
    return;
  }

  if (req.method === "GET" && req.url?.startsWith("/tool/browser/status/")) {
    const runId = req.url.split("/").pop() || "";
    const status = getRunStatus(runId);
    if (!status) {
      res.writeHead(404, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: "Run not found" }));
      return;
    }
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify(status));
    return;
  }

  if (req.method === "GET" && req.url === "/tool/browser/runs") {
    const runs = getAllRuns();
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify(runs));
    return;
  }

  // ── Phase 31/32: FCM Token Registration + Device Registry ───────────────
  if (req.method === "POST" && req.url === "/fcm/register-token") {
    let body = "";
    req.on("data", (chunk) => (body += chunk.toString()));
    req.on("end", () => {
      try {
        // ip is optional — sent by Android app on startup (local WiFi IP for ADB OTA)
        const { deviceId, token, ip } = JSON.parse(body);
        if (!deviceId || !token) {
          res.writeHead(400, { "Content-Type": "application/json" });
          res.end(JSON.stringify({ error: "deviceId and token required" }));
          return;
        }
        registerToken(deviceId, token);
        // Phase 32: store in device registry for OTA IP resolution
        registerDevice(deviceId, { fcmToken: token, ip: ip ?? undefined });
        if (ip) console.log(`[DeviceRegistry] Device ${deviceId} IP: ${ip}`);
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ ok: true, deviceId }));
      } catch (e) {
        res.writeHead(400, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: String(e) }));
      }
    });
    return;
  }

  // ── Phase 32: Device info (IP + FCM token) for OTA updater ───────────────
  if (req.method === "GET" && req.url?.match(/^\/v1\/android\/device\/[^/]+$/)) {
    const deviceId = req.url.split("/").pop() || "";
    const info = _deviceRegistry.get(deviceId);
    if (!info) {
      res.writeHead(404, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: "Device not found", deviceId }));
      return;
    }
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify(info));
    return;
  }

  // Phase 32: List all registered devices
  if (req.method === "GET" && req.url === "/v1/android/devices") {
    const devices = Array.from(_deviceRegistry.values());
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify(devices));
    return;
  }

  // ── Phase 31: Android → Core commands ────────────────────────────────────
  if (req.method === "POST" && req.url === "/v1/android/command") {
    let body = "";
    req.on("data", (chunk) => (body += chunk.toString()));
    req.on("end", () => {
      try {
        const { type, payload, deviceId } = JSON.parse(body);
        if (!type) {
          res.writeHead(400, { "Content-Type": "application/json" });
          res.end(JSON.stringify({ error: "type required" }));
          return;
        }
        const cmd = createCommand(type as CommandType, payload ?? {}, deviceId ?? "unknown");
        res.writeHead(201, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ id: cmd.id, status: cmd.status }));
      } catch (e) {
        res.writeHead(400, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: String(e) }));
      }
    });
    return;
  }

  if (req.method === "GET" && req.url === "/v1/android/commands/pending") {
    purgeOldCommands();
    const pending = getPendingCommands();
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify(pending));
    return;
  }

  if (req.method === "GET" && req.url === "/v1/android/commands") {
    const all = getAllCommands();
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify(all));
    return;
  }

  if (req.method === "POST" && req.url?.match(/^\/v1\/android\/commands\/[^/]+\/result$/)) {
    const cmdId = req.url.split("/")[4];
    let body = "";
    req.on("data", (chunk) => (body += chunk.toString()));
    req.on("end", () => {
      try {
        const { status, result } = JSON.parse(body);
        const cmd = updateCommand(cmdId, status, result);
        if (!cmd) {
          res.writeHead(404, { "Content-Type": "application/json" });
          res.end(JSON.stringify({ error: "Command not found" }));
          return;
        }
        // Sync result back to Firestore if this command originated from Firestore relay
        // deviceId format: "fs:{firestoreDocId}" — set by firestorePoller.ts
        if (cmd.deviceId.startsWith("fs:")) {
          const firestoreDocId = cmd.deviceId.slice(3);
          const fsStatus = (status === "DONE" || status === "FAILED")
            ? (status as "DONE" | "FAILED")
            : "DONE";
          updateFirestoreResult(firestoreDocId, fsStatus, result).catch(() => {});
        }
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ id: cmd.id, status: cmd.status }));
      } catch (e) {
        res.writeHead(400, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: String(e) }));
      }
    });
    return;
  }

  // ── Phase 31: Push notifications (Core → Android) ─────────────────────────
  if (req.method === "POST" && req.url === "/v1/android/notify") {
    let body = "";
    req.on("data", (chunk) => (body += chunk.toString()));
    req.on("end", async () => {
      try {
        const { deviceId, title, body: notifBody, data } = JSON.parse(body);
        if (!title || !notifBody) {
          res.writeHead(400, { "Content-Type": "application/json" });
          res.end(JSON.stringify({ error: "title and body required" }));
          return;
        }
        const payload: FcmPayload = { title, body: notifBody, data };
        let result;
        if (deviceId) {
          result = await sendToDevice(deviceId, payload);
        } else {
          result = await sendToAll(payload);
        }
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ ok: true, ...result }));
      } catch (e) {
        res.writeHead(400, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: String(e) }));
      }
    });
    return;
  }

  // ── Phase 31: Android polls for notifications ─────────────────────────────
  if (req.method === "GET" && req.url === "/v1/android/notifications") {
    const pending = getPendingNotifications();
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify(pending));
    return;
  }

  if (req.method === "POST" && req.url === "/v1/android/notifications/read-all") {
    const count = markAllRead();
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ ok: true, markedRead: count }));
    return;
  }

  if (req.method === "POST" && req.url?.match(/^\/v1\/android\/notifications\/[^/]+\/read$/)) {
    const notifId = req.url.split("/")[4];
    const ok = markRead(notifId);
    res.writeHead(ok ? 200 : 404, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ ok }));
    return;
  }

  // ── Phase 31: FCM status ──────────────────────────────────────────────────
  if (req.method === "GET" && req.url === "/v1/android/fcm/status") {
    const status = getFcmStatus();
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify(status));
    return;
  }

  // ── Phase 31: Firestore relay status ─────────────────────────────────────
  if (req.method === "GET" && req.url === "/v1/android/firestore/status") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({
      relayActive: isFirestoreRelayActive(),
      mode: isFirestoreRelayActive() ? "firestore" : "memory-only",
    }));
    return;
  }

  // ── Phase 31: Android status proxy ───────────────────────────────────────
  if (req.method === "GET" && req.url === "/v1/android/status") {
    http.get(CORE_URL + "/status/current", (up) => {
      let d = "";
      up.on("data", (c) => (d += c));
      up.on("end", () => {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(d);
      });
    }).on("error", (e) => {
      res.writeHead(502, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: String(e) }));
    });
    return;
  }

  res.writeHead(404);
  res.end();
});

server.listen(PORT, () => {
  console.log(`Archimedes Net listening on http://localhost:${PORT}`);
  // Start Firestore commands poller + status push (requires GOOGLE_APPLICATION_CREDENTIALS)
  startFirestorePoller().catch((e) => {
    console.warn('[Net] Firestore poller failed to start:', String(e).slice(0, 80));
  });
});

export async function sendEnvelopeToCore(payload: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const data = Buffer.from(payload, "utf8");
    const r = http.request(
      {
        hostname: "localhost",
        port: 5051,
        path: "/envelope",
        method: "POST",
        headers: { "Content-Length": data.length },
      },
      (res) => {
        res.on("data", () => {});
        res.on("end", () => resolve());
      }
    );
    r.on("error", reject);
    r.write(data);
    r.end();
  });
}
