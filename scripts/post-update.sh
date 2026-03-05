#!/bin/bash
# =============================================================================
#  post-update.sh — Comprehensive post-git-pull updater
#
#  Called automatically by /admin/pull-update after every successful git pull.
#  Handles ALL types of changes: LLM model, kiosk script, C# code.
#
#  Never needs to be run manually — the dashboard update button triggers it.
# =============================================================================

REPO_DIR="$HOME/archimedes"
ENV_FILE="/etc/archimedes/environment"
MODEL_DIR="$HOME/.local/share/Archimedes/models"
DOTNET="$HOME/.dotnet/dotnet"
PUBLISH_OUT="$REPO_DIR/core/bin/Release/net8.0"
LOG="/tmp/archimedes-post-update.log"

# Append all output to log file
exec >> "$LOG" 2>&1
echo ""
echo "=== [$(date '+%Y-%m-%d %H:%M:%S')] post-update.sh started ==="

# ── Helper: get files changed in the latest pull ──────────────────────────
changed_files() {
    # ORIG_HEAD is set by git pull — diff between before-pull and after-pull
    git -C "$REPO_DIR" diff ORIG_HEAD HEAD --name-only 2>/dev/null \
        || git -C "$REPO_DIR" diff HEAD~1 HEAD --name-only 2>/dev/null \
        || echo ""
}

CHANGED=$(changed_files)
echo "Changed files:"
echo "$CHANGED" | sed 's/^/  /'

# ═══════════════════════════════════════════════════════════════════════════
#  1. MODEL UPGRADE — check if bootstrap.sh now expects a different model
# ═══════════════════════════════════════════════════════════════════════════
EXPECTED_FILENAME=$(grep "^MODEL_PATH=" "$REPO_DIR/scripts/bootstrap.sh" 2>/dev/null \
    | grep -oP '[^/"]+\.gguf' | head -1)
EXPECTED_PATH="$MODEL_DIR/$EXPECTED_FILENAME"
CURRENT_PATH=$(grep "^ARCHIMEDES_MODEL_PATH=" "$ENV_FILE" 2>/dev/null | cut -d= -f2 || echo "")

echo ""
echo "--- Model check ---"
echo "  Expected: $EXPECTED_PATH"
echo "  Current:  $CURRENT_PATH"

if [ -n "$EXPECTED_FILENAME" ] && \
   { [ "$CURRENT_PATH" != "$EXPECTED_PATH" ] || [ ! -f "$EXPECTED_PATH" ]; }; then
    echo "  → Model upgrade needed"
    bash "$REPO_DIR/scripts/upgrade-llm.sh"
    echo "  → Model upgrade complete"
else
    echo "  → Model OK — no upgrade needed"
fi

# ═══════════════════════════════════════════════════════════════════════════
#  2. KIOSK SCRIPT — update /usr/local/bin/archimedes-kiosk if changed
#     (write only — no Chromium restart, takes effect on next kiosk restart)
# ═══════════════════════════════════════════════════════════════════════════
echo ""
echo "--- Kiosk script check ---"

KIOSK_SCRIPT="/usr/local/bin/archimedes-kiosk"
CORE_PORT=$(grep "^ARCHIMEDES_PORT=" "$ENV_FILE" 2>/dev/null | cut -d= -f2 || echo "5051")

# Write the latest version of the kiosk launcher to a temp file
cat > /tmp/archimedes-kiosk-new << 'KIOSKEOF'
#!/bin/bash
# Archimedes Kiosk Launcher v2

xset -dpms   2>/dev/null || true
xset s off   2>/dev/null || true
xset s noblank 2>/dev/null || true

export DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$(id -u)/bus"

for i in $(seq 1 45); do
    curl -sf http://localhost:5051/health >/dev/null 2>&1 && break
    sleep 2
done

KIOSK_PROFILE="/tmp/archimedes-kiosk-profile"
mkdir -p "$KIOSK_PROFILE"

while true; do
    for PREF in \
        "$HOME/snap/chromium/current/.config/chromium/Default/Preferences" \
        "$HOME/.config/chromium/Default/Preferences"; do
        if [ -f "$PREF" ]; then
            sed -i 's/"exited_cleanly":false/"exited_cleanly":true/g'  "$PREF" 2>/dev/null || true
            sed -i 's/"exit_type":"Crashed"/"exit_type":"Normal"/g'     "$PREF" 2>/dev/null || true
        fi
    done

    /snap/bin/chromium \
        --kiosk \
        --user-data-dir="$KIOSK_PROFILE" \
        --noerrdialogs \
        --disable-infobars \
        --disable-session-crashed-bubble \
        --disable-restore-session-state \
        --disable-component-update \
        --check-for-update-interval=31536000 \
        --no-first-run \
        --disable-translate \
        --disable-features=TranslateUI \
        --disable-pinch \
        --overscroll-history-navigation=0 \
        http://localhost:5051/dashboard

    sleep 3
done
KIOSKEOF

# Only update if content differs
if ! diff -q /tmp/archimedes-kiosk-new "$KIOSK_SCRIPT" > /dev/null 2>&1; then
    sudo cp /tmp/archimedes-kiosk-new "$KIOSK_SCRIPT"
    sudo chmod +x "$KIOSK_SCRIPT"
    echo "  → Kiosk script updated (takes effect on next kiosk restart)"
else
    echo "  → Kiosk script unchanged"
fi
rm -f /tmp/archimedes-kiosk-new

# ═══════════════════════════════════════════════════════════════════════════
#  3. C# REBUILD — rebuild and restart service if core code changed
# ═══════════════════════════════════════════════════════════════════════════
echo ""
echo "--- C# build check ---"

if echo "$CHANGED" | grep -qE '\.(cs|csproj)$'; then
    echo "  → C# files changed — rebuilding..."

    "$DOTNET" publish \
        "$REPO_DIR/core/Archimedes.Core.csproj" \
        -c Release \
        -o "$PUBLISH_OUT" \
        --nologo \
        2>&1

    echo "  → Build complete — restarting service..."
    sleep 2
    sudo systemctl restart archimedes
    echo "  → Service restarted"
else
    echo "  → No C# changes — no rebuild needed"
fi

echo ""
echo "=== [$(date '+%Y-%m-%d %H:%M:%S')] post-update.sh complete ==="
