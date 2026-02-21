# Archimedes – Setup

## Prerequisites

- **Core:** .NET 8 SDK
- **Net:** Node.js 18+ and npm
- **Android:** Android Studio (or SDK 34 + Gradle 8.x)

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
