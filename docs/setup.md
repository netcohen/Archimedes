# Archimedes – Setup

## Prerequisites

- **Core:** .NET 8 SDK
- **Net:** Node.js 18+ and npm
- **Android:** Android Studio (or SDK 34 + Gradle 8.x)

## Secrets Setup

1. Create a local secrets folder (not in repo):
   ```
   mkdir C:\Users\%USERNAME%\.secrets\archimedes
   ```

2. Copy the environment template:
   ```bash
   cp net/.env.example net/.env.local
   ```

3. Fill in real values in `net/.env.local`

4. For Firebase (Firestore + FCM HTTP v1), place your service account JSON in:
   ```
   C:\Users\%USERNAME%\.secrets\archimedes\firebase-dev-sa.json
   ```

5. Set environment variable:
   ```powershell
   $env:GOOGLE_APPLICATION_CREDENTIALS = "C:\Users\$env:USERNAME\.secrets\archimedes\firebase-dev-sa.json"
   ```

> **Note:** FCM uses HTTP v1 API with OAuth2. No legacy server key needed.

See [security.md](security.md) for full secrets management guide.

## Build & Run

### Core (HTTP :5051)
```bash
cd core
dotnet build
dotnet run
```

### Net (HTTP :5052)
```bash
cd net
npm install
npm run build
npm start
```

### Android
```bash
cd android
gradlew.bat assembleDebug   # Windows
./gradlew assembleDebug     # Unix
```
Or open `android/` in Android Studio and run on emulator/device.

## Verification

- Core: `http://localhost:5051/health` → OK
- Net: `http://localhost:5052/health` → OK
- Cross-call: `http://localhost:5051/ping-net` → OK
- Envelope: POST to `http://localhost:5051/send-envelope`, GET `http://localhost:5052/envelope`
- Approval: `scripts/phase8-test.ps1` (Core must be running)

## Testing

### Quick E2E Tests (< 2 minutes)

Run the full E2E test suite (requires Core + Net running):

```powershell
# Start services first
cd core; dotnet run &
cd net; npm start &

# Run E2E tests
.\scripts\e2e.ps1
```

Expected output: `Passed: 21, Failed: 0`

### Unit Tests

```powershell
cd core.tests
dotnet test
```

### Soak Test (12 hours)

For long-running stability testing:

```powershell
# Start services in separate terminals
cd core; dotnet run
cd net; npm start

# Run soak test (default 12 hours)
.\scripts\run-soak.ps1

# Or custom duration
.\scripts\run-soak.ps1 -DurationHours 1
```

Logs are written to `logs/soak/`.

### Security Regression

```powershell
.\scripts\phase14-security.ps1
```

## Pre-Commit Check

Before committing, run the secrets scanner:

```powershell
.\scripts\check-no-secrets.ps1
```

This ensures no secrets are accidentally committed.
