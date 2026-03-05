#!/usr/bin/env bash
# update-android.sh — Archimedes Android App OTA Updater (ADB WiFi)
#
# USAGE:
#   ./scripts/update-android.sh [phone-ip]
#
# REQUIRES (Ubuntu):
#   sudo apt install android-tools-adb
#   Android Studio / SDK (for Gradle builds)
#   ADB WiFi enabled on phone:
#     Android 11+: Settings → Developer options → Wireless debugging
#     Android <11: connect USB once, run: adb tcpip 5555, unplug
#
# CALLED BY:
#   AppUpdater.cs → POST /android/update
#   Or manually: ./scripts/update-android.sh 192.168.1.50
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ANDROID_DIR="$PROJECT_DIR/android"
APK_PATH="$ANDROID_DIR/app/build/outputs/apk/debug/app-debug.apk"
IP_CACHE="$HOME/.archimedes/phone-ip"

# ── Resolve phone IP ──────────────────────────────────────────────────────────
PHONE_IP="${1:-}"

if [ -z "$PHONE_IP" ] && [ -f "$IP_CACHE" ]; then
    PHONE_IP="$(cat "$IP_CACHE")"
    echo "[Updater] Using cached phone IP: $PHONE_IP"
fi

# ── Build APK ─────────────────────────────────────────────────────────────────
echo "[Updater] ▶ Building Android APK (assembleDebug)..."
cd "$ANDROID_DIR"

if [ ! -f "gradlew" ]; then
    echo "[Updater] ERROR: gradlew not found in $ANDROID_DIR" >&2
    exit 1
fi

chmod +x gradlew
./gradlew assembleDebug --quiet

if [ ! -f "$APK_PATH" ]; then
    echo "[Updater] ERROR: APK not found at $APK_PATH" >&2
    exit 1
fi

echo "[Updater] ✓ APK built: $APK_PATH"

# ── ADB WiFi connect ──────────────────────────────────────────────────────────
if [ -n "$PHONE_IP" ]; then
    echo "[Updater] ▶ Connecting to phone at $PHONE_IP:5555..."
    if adb connect "$PHONE_IP:5555" 2>&1 | grep -q "connected\|already connected"; then
        echo "[Updater] ✓ ADB connected"
        # Save IP for next time
        mkdir -p "$(dirname "$IP_CACHE")"
        echo "$PHONE_IP" > "$IP_CACHE"
    else
        echo "[Updater] ⚠  ADB connect failed — will try install anyway (USB fallback)"
    fi
else
    echo "[Updater] ⚠  No phone IP provided — trying connected ADB devices"
fi

# ── Check device reachable ────────────────────────────────────────────────────
DEVICE_COUNT=$(adb devices | grep -c "device$" || echo "0")
if [ "$DEVICE_COUNT" -eq 0 ]; then
    echo "[Updater] ERROR: No ADB devices found" >&2
    echo "[Updater] To enable ADB WiFi (Android 11+):" >&2
    echo "  Settings → Developer options → Wireless debugging → Pair device with QR code" >&2
    echo "  Then: adb pair <ip>:<port> <code>" >&2
    exit 1
fi

# ── Install APK ───────────────────────────────────────────────────────────────
echo "[Updater] ▶ Installing APK..."
adb install -r "$APK_PATH"

echo "[Updater] ✓ Installation complete — please restart the Archimedes app"
