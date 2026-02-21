# Archimedes – Development Progress

## Phase 0 – Repo Scaffold ✅

**Status:** Complete

**Done:**
- Created monorepo structure
- `core` – C# .NET 8 WebApplication
- `net` – Node.js TypeScript project
- `android` – Kotlin Android app (minSdk 26, targetSdk 34)
- `docs/` folder with progress, architecture, setup
- Root README and .gitignore

**Phase 0 Re-validation (PASS):**
- **A) Core:** `cd core` → `dotnet restore` → `dotnet build` → `dotnet run` → "Archimedes Core - OK"
- **B) Net:** `cd net` → `npm ci` → `npm run build` → `node dist/index.js` → "Archimedes Net - OK"
- **C) Android:** `cd android` → `gradlew.bat assembleDebug` → BUILD SUCCESSFUL

---

## Phase 1 – Core + Net Communication ✅

**Status:** Complete

**Done:**
- Core HTTP server on http://localhost:5051
- Net HTTP server on http://localhost:5052
- `GET /health` on both returns "OK"
- Core `GET /ping-net` calls Net `/health` and returns result (proves Core→Net cross-call)
- `scripts/phase1-test.ps1` for verification

**Commands executed:**
```
# Terminal 1: start Net
cd net
node dist/index.js

# Terminal 2: start Core
cd core
dotnet run

# Terminal 3: test
Invoke-WebRequest -Uri http://localhost:5052/health -UseBasicParsing  # → OK
Invoke-WebRequest -Uri http://localhost:5051/health -UseBasicParsing  # → OK
Invoke-WebRequest -Uri http://localhost:5051/ping-net -UseBasicParsing  # → OK (cross-call)
```

**Self-test results:** PASS

---

## Phase 2 – Mock Messaging (local queue) ✅

**Status:** Complete

**Done:**
- Core: in-memory envelope queue, POST /envelope (receive), GET /envelope (pop)
- Net: in-memory envelope queue, POST /envelope (receive), GET /envelope (pop)
- Core POST /send-envelope forwards payload to Net /envelope
- Net prints `[Net] Received envelope: {payload}` on receive

**Commands executed:**
```
# Start Net, then Core (same as Phase 1)
cd net && node dist/index.js   # Terminal 1
cd core && dotnet run          # Terminal 2

# Send envelope (Core -> Net)
Invoke-WebRequest -Uri http://localhost:5051/send-envelope -Method POST -Body "Hello envelope" -ContentType "text/plain"

# Receive from Net queue
Invoke-WebRequest -Uri http://localhost:5052/envelope  # Returns "Hello envelope"
```

**Output:** Net console prints `[Net] Received envelope: Hello envelope`

**Self-test results:** PASS (Send -> Receive -> Print)

---

## Phase 3 – Encrypted Envelopes ✅

**Status:** Complete

**Done:** E2E encryption (RSA 2048, OAEP-SHA256), `core/Crypto.cs`, `POST /crypto-test`

**Self-test:** `Invoke-WebRequest -Uri http://localhost:5051/crypto-test -Method POST -Body "secret message"` → ok:true, decrypted matches. PASS

---

## Phase 4 – Android Skeleton ✅

**Status:** Complete

**Done:** PairingActivity (placeholder), InboxActivity, MainActivity shows hardcoded "Test message from PC"

**Self-test:** `gradlew.bat assembleDebug` → BUILD SUCCESSFUL. App shows message.

---

## Phase 4.1 – Firebase Android Integration ✅

**Status:** Complete

**Done:**
- Added Google Services plugin to `android/build.gradle.kts` (version 4.4.0)
- Applied `com.google.gms.google-services` plugin in `android/app/build.gradle.kts`
- Added Firebase BOM 32.7.0 with firebase-messaging-ktx and firebase-firestore-ktx
- Verified `google-services.json` exists at `android/app/google-services.json`

**Commands:**
```powershell
cd android
.\gradlew.bat assembleDebug
```

**Output:** BUILD SUCCESSFUL in 45s (34 tasks executed, processDebugGoogleServices ran)

**Self-test:** PASS

---

## Phase 5 – Firebase Integration ✅

**Status:** Complete

**Done:** Envelope store (memory + Firestore when FIRESTORE_EMULATOR_HOST set), envelopes collection, FCM push ping structure (fcm.ts)

**Self-test:** `POST http://localhost:5052/firestore-test` with body "test envelope" → ok:true. PASS

---

## Phase 6 – Pairing Flow ✅

**Status:** Complete

**Done:** Core GET /pairing-data (QR content), POST /pairing-complete; Android ZXing scan + simulate pairing, key exchange

**Self-test:** Core pairing-data + pairing-complete → ok:true. Android build OK. PASS

---

## Phase 7 – End-to-End Messaging ✅

**Status:** Complete

**Done:** Net POST /from-android (Android→Net→Core), InboxActivity fetch + reply, full roundtrip

**Self-test:** Core→Net→fetch, Android→Net→Core roundtrip. PASS

---

## Phase 8 – Approval Flow ✅

**Status:** Complete

**Done:** WAITING_FOR_USER state, Core /task/run-with-approval, /approvals, /approval-response; Android ApprovalActivity; Net proxy

**Self-test:** Pause → approve → resume. PASS

---

## Phase 9 – Basic Scheduler ✅

**Status:** Complete

**Done:** Jobs + Runs, POST /job, POST /job/{id}/run, GET /run/{id}

**Self-test:** Create job, run, verify completed. PASS

---

## Phase 10 – Monitoring Task ✅

**Status:** Complete

**Done:** Background monitor task (3s tick), GET /monitor/ticks

**Self-test:** Runs multiple times, doesn't block. PASS

---

## Phase 11 – Logging System ✅

**Status:** Complete

**Done:** ArchLogger.HumanSummary, MachineTrace; /log-test/fail

**Self-test:** Failure produces both logs. PASS

---

## Phase 12 – Recovery System ✅

**Status:** Complete

**Done:** SavedState, /state/save, /state/load, /state/clear, file persistence

**Self-test:** Save → restart Core → load → Resume. PASS

---

---

## Phase 12.5 – Project Hygiene & Secrets Management ✅

**Status:** Complete

**Done:**
- `docs/security.md` – Rules for secrets, allowed logging, storage locations
- `net/.env.example` – Template with placeholders
- `.gitignore` – Extended with secret file patterns
- `.gitattributes` – LF line endings for code files
- `scripts/check-no-secrets.ps1` – Scans repo for forbidden patterns
- `docs/setup.md` – Updated with secrets setup guide

**Self-test:**
- `check-no-secrets.ps1` → PASS
- Core build → BUILD SUCCEEDED
- Net build → BUILD SUCCEEDED
- Android build → BUILD SUCCESSFUL

---

## Phase 5.1 – Firestore Mode Detection Fix ✅

**Status:** Complete

**Problem:** Firestore was using in-memory mode (showing `mem_*` ids) even when Firebase credentials existed.

**Done:**
- Updated `net/.env.example` with new env vars:
  - `FIREBASE_PROJECT_ID=archimedes-c76c3`
  - `FIRESTORE_MODE=real|emulator|memory`
- Rewrote `net/src/firestore.ts` with proper mode detection:
  1. `FIRESTORE_MODE=memory` → use memory
  2. `FIRESTORE_MODE=emulator` OR `FIRESTORE_EMULATOR_HOST` set → use emulator
  3. `GOOGLE_APPLICATION_CREDENTIALS` set → use REAL Firestore (Admin SDK)
  4. Fallback → memory with warning log
- Updated `/firestore-test` to return `mode: "real|emulator|memory"`
- ID prefix: `fs_` for real/emulator, `mem_` only for memory
- Added `/v1/firebase/health` endpoint (GET/POST):
  - Writes + reads a test doc
  - Returns `{ok: true, mode: "real", docPath: "envelopes/..."}`

**Self-test:**
```powershell
# Set credentials
$env:GOOGLE_APPLICATION_CREDENTIALS = "$env:USERPROFILE\.secrets\archimedes\firebase-dev-sa.json"
$env:FIRESTORE_MODE = "real"
$env:FIREBASE_PROJECT_ID = "archimedes-c76c3"

# Start server
cd net; node dist/index.js
# Output: [Firestore] Initialized REAL mode with project: archimedes-c76c3

# Test health endpoint
curl.exe http://localhost:5052/v1/firebase/health
# Output: {"ok":true,"mode":"real","docPath":"envelopes/Qd8CrJCRyFMxCjFMwRsr"}

# Test firestore-test endpoint
curl.exe -X POST http://localhost:5052/firestore-test -d "test"
# Output: {"id":"fs_7hyRXw8b3YpNYSYvRKJ3","ok":true,"mode":"real","readPayload":"test"}
```

**Result:** PASS – Real Firestore mode working, documents visible in Firebase console.

---

## Phase 13 – Hardening & Spec Compliance

### Phase 13.0 – Baseline Verification ✅

**Status:** Complete

**Done:**
- Core build: `dotnet build` → 0 errors
- Net build: `npm ci && npm run build` → success
- Android build: `gradlew.bat assembleDebug` → BUILD SUCCESSFUL
- Firestore REAL mode test: `mode="real"`, id prefix `fs_`

**Self-test:**
```powershell
# All builds
cd core; dotnet build           # PASS
cd ../net; npm ci; npm run build  # PASS
cd ../android; .\gradlew.bat assembleDebug  # PASS

# Firestore real mode
curl.exe -X POST http://localhost:5052/firestore-test -d "phase13-test"
# {"id":"fs_LyWhMCe8wo7lqzkvf7FM","ok":true,"mode":"real","readPayload":"phase13-test"}
```

**Result:** PASS

---

### Phase 13.E – Log Redaction + Tests ✅

**Status:** Complete

**Done:**
- Created `core/Redactor.cs`:
  - Pattern-based redaction for passwords, tokens, API keys, OTPs, cookies, headers, private keys, JWTs
  - `Redact()` - replaces sensitive values with `[REDACTED len=X hash=Y]`
  - `RedactPayload()` - returns only metadata: `[payload len=X hash=Y]`
  - `RedactJson()` - redacts sensitive keys in JSON strings
  - `SafeMetadata()` - returns dict with length, hash, hasContent
- Updated `core/Logger.cs`:
  - All logging methods now use `Redactor.Redact()`
  - Added `LogInfo`, `LogWarn`, `LogError`, `LogPayload` methods
- Updated `core/Program.cs`:
  - Envelope logging uses `ArchLogger.LogPayload()` (no raw data)
  - Task approval logging uses `ArchLogger.LogPayload()`
- Created `net/src/redactor.ts`:
  - TypeScript port of redaction logic
  - `redact()`, `redactPayload()`, `safeMetadata()`, `safeLog()`, `safeLogPayload()`
- Updated `net/src/index.ts`:
  - Envelope logging uses `safeLogPayload()` (no raw data)
- Created `core.tests/` xUnit project with 13 tests for Redactor

**Self-test:**
```powershell
# Core build
cd core; dotnet build  # PASS

# Net build
cd ../net; npm run build  # PASS

# Core tests (redaction)
cd ../core.tests; dotnet test
# Passed! - Failed: 0, Passed: 13, Skipped: 0

# Android build
cd ../android; .\gradlew.bat assembleDebug  # PASS

# Firestore real mode
curl.exe -X POST http://localhost:5052/firestore-test -d "phase13E-test"
# {"id":"fs_1WyG15lGeMffiGYCyniG","ok":true,"mode":"real",...}
```

**Result:** PASS – No sensitive data in logs, all redaction tests pass.

---

### Phase 13.C – Outbox + Retry + Dedup ✅

**Status:** Complete

**Done:**
- Created `core/Outbox.cs`:
  - `OutboxEntry` with id, operationId, payload, destination, status, attempts, nextRetry, error
  - `OutboxStatus`: PENDING, SENDING, SENT, FAILED
  - `OutboxService` with enqueue, process, drain, background worker
  - Exponential backoff: 1m → 5m → 15m → 60m
  - Deduplication via operationId
- Added endpoints to Core:
  - `POST /outbox/enqueue` - enqueue with operationId
  - `GET /outbox/entries` - list all entries
  - `GET /outbox/stats` - get counts by status
  - `POST /outbox/drain` - manually drain pending
- Background worker starts automatically, processes pending entries every 5s
- Updated Net firestore.ts:
  - `WriteResult` type with id + isDuplicate
  - Memory store: operationId index for dedup
  - Firestore: query by operationId before write
- Added `POST /v1/envelope/idempotent` endpoint (requires X-Operation-Id header)
- Updated `/firestore-test` to return `duplicate` flag

**Self-test:**
```powershell
# All builds
cd core; dotnet build           # PASS
cd ../net; npm run build        # PASS
cd ../android; .\gradlew.bat assembleDebug  # PASS

# Firestore real mode
curl.exe -X POST http://localhost:5052/firestore-test -d "phase13C-test"
# {"id":"fs_...","ok":true,"mode":"real","duplicate":false,...}

# Test deduplication (same operationId twice)
curl.exe -X POST http://localhost:5052/v1/envelope/idempotent -d "test" -H "X-Operation-Id: dedup-test-1"
# {"id":"fs_...","duplicate":false,"mode":"real"}
curl.exe -X POST http://localhost:5052/v1/envelope/idempotent -d "test" -H "X-Operation-Id: dedup-test-1"
# {"id":"fs_...","duplicate":true,"mode":"real"}

# Test outbox enqueue + drain
curl.exe -X POST http://localhost:5051/outbox/enqueue -d @body.json
# {"ok":true,"entryId":"...","duplicate":false}
curl.exe http://localhost:5051/outbox/stats
# {"total":1,"pending":0,"sent":1,...}  (auto-sent by worker)

# Same operationId returns duplicate
curl.exe -X POST http://localhost:5051/outbox/enqueue -d @body.json
# {"ok":true,"entryId":"...","duplicate":true}
```

**Result:** PASS – Durable outbox, exponential retry, operationId deduplication working.

---

### Phase 13.D – Crash Recovery (AUTO) ✅

**Status:** Complete

**Done:**
- Updated `core/Scheduler.cs`:
  - Added `Step`, `Checkpoint`, `Error` to Run model
  - Added `RunStatus` constants: Running, Completed, Failed, Paused, Recovering
- Updated `core/Recovery.cs`:
  - `PersistentRun` with full state: id, jobId, status, step, checkpoint, times
  - `RecoveryState` with list of runs + lastSaved timestamp
  - `RecoveryManager` class with:
    - File-based persistence to `%TEMP%\archimedes\recovery_state.json`
    - `TrackRun()` - save new run to state
    - `UpdateRunStatus()` - update status/step/checkpoint
    - `GetRecoverableRuns()` - find runs in RUNNING or RECOVERING state
    - `MarkRecovering()` - transition to RECOVERING state
    - `ClearRun()`/`ClearAll()` - cleanup
- Updated Core startup in `Program.cs`:
  - Detect recoverable runs on startup
  - Mark as RECOVERING and resume automatically
  - Outbox worker starts automatically
- Added endpoints:
  - `GET /recovery/state` - view all tracked runs
  - `POST /recovery/clear` - clear recovery state
  - `POST /job/{id}/run-slow` - 5-step slow run (2s per step) for testing

**Self-test:**
```powershell
# Create job and start slow run
$jobId = (curl.exe -s -X POST http://localhost:5051/job -d '{}' | ConvertFrom-Json).jobId
curl.exe -s -X POST "http://localhost:5051/job/$jobId/run-slow"
# {"runId":"...","message":"Slow run started (5 steps, 2s each)"}

# Check state after 2 seconds
Start-Sleep -Seconds 2
curl.exe -s http://localhost:5051/recovery/state
# {"runs":[{"id":"...","status":"running","step":2,...}],...}

# Kill process (simulate crash)
taskkill /F /IM dotnet.exe

# Restart Core
dotnet run
# Output:
# [INFO] [Recovery] Found run ... in state running, step=2
# [INFO] Run ... marked as RECOVERING
# [INFO] [Recovery] Resuming run ... from step 2
# [INFO] [Recovery] Run ... completed after recovery

# Verify final state
curl.exe -s http://localhost:5051/recovery/state
# {"runs":[{"id":"...","status":"completed","step":3,"checkpoint":"resumed_step_3",...}],...}

# Firestore real mode
curl.exe -X POST http://localhost:5052/firestore-test -d "phase13D-test"
# {"id":"fs_...","ok":true,"mode":"real",...}
```

**Result:** PASS – Automatic crash recovery working, no manual /state/load needed.

---

### Phase 13.B – Encrypted DB (SQLCipher + DPAPI) ✅

**Status:** Complete

**Done:**
- Added NuGet packages:
  - `Microsoft.Data.Sqlite.Core` - SQLite base
  - `SQLitePCLRaw.bundle_e_sqlcipher` - SQLCipher encryption
  - `System.Security.Cryptography.ProtectedData` - DPAPI
- Created `core/EncryptedStore.cs`:
  - SQLCipher encrypted database at `%LOCALAPPDATA%\Archimedes\archimedes.db`
  - Encryption key generated randomly (32 bytes)
  - Key protected with DPAPI (`DataProtectionScope.CurrentUser`)
  - Key stored at `%LOCALAPPDATA%\Archimedes\archimedes.key`
  - Tables: jobs, runs, outbox, approvals, dedup
  - Full CRUD operations for all entity types
  - `IsEncrypted()` method to verify DB is not plaintext
- Added endpoints:
  - `GET /store/stats` - returns counts + isEncrypted flag
  - `POST /store/test` - write/read roundtrip test

**Self-test:**
```powershell
# All builds
cd core; dotnet build           # PASS (with DPAPI warnings - Windows only)
cd ../net; npm run build        # PASS
cd ../android; .\gradlew.bat assembleDebug  # PASS

# Start Core - first run generates key
dotnet run
# [INFO] Generated new database encryption key (DPAPI protected)
# [INFO] EncryptedStore initialized at C:\Users\...\archimedes.db

# Test store
curl.exe -s http://localhost:5051/store/stats
# {"jobs":0,"runs":0,"outbox":0,"isEncrypted":true}

curl.exe -s -X POST http://localhost:5051/store/test -d "test payload"
# {"ok":true,"jobId":"...","isEncrypted":true,"stats":{"jobs":1,...}}

# Kill and restart - verify persistence
taskkill /F /IM dotnet.exe
# Check DB header is NOT "SQLite format 3"
$bytes = [IO.File]::ReadAllBytes("$env:LOCALAPPDATA\Archimedes\archimedes.db")
[Text.Encoding]::ASCII.GetString($bytes[0..15])
# Output: garbled characters (encrypted)

# Restart Core
dotnet run
# [INFO] EncryptedStore initialized at ... (no "Generated new key")
curl.exe -s http://localhost:5051/store/stats
# {"jobs":1,...} - data persisted!

# Firestore real mode
curl.exe -X POST http://localhost:5052/firestore-test -d "phase13B-test"
# {"id":"fs_...","ok":true,"mode":"real",...}
```

**Result:** PASS – Encrypted SQLCipher DB with DPAPI key protection, data persists across restarts.

---

## MVP Complete ✅

All 12 phases + hygiene done. Final goal reached:

User sends task → system runs → requests approval → user approves → system completes → result returned
