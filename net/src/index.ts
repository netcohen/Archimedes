import * as http from "http";
import { getEnvelopeStore } from "./firestore";

const PORT = 5052;
const CORE_URL = "http://localhost:5051";

const envelopeQueue: string[] = [];

const server = http.createServer((req, res) => {
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
      console.log("[Net] Received envelope:", body || "(empty)");
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
        const payload = body || "test envelope";
        const id = await store.write(payload);
        const doc = await store.read(id);
        const ok = doc !== null && doc.payload === payload;
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ id, ok, readPayload: doc?.payload }));
      } catch (e) {
        res.writeHead(500);
        res.end(JSON.stringify({ error: String(e) }));
      }
    });
    return;
  }
  res.writeHead(404);
  res.end();
});

server.listen(PORT, () => {
  console.log(`Archimedes Net listening on http://localhost:${PORT}`);
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
