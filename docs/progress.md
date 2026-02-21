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

## MVP Complete ✅

All 12 phases + hygiene done. Final goal reached:

User sends task → system runs → requests approval → user approves → system completes → result returned
