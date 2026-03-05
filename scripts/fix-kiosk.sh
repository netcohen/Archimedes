#!/bin/bash
# =============================================================================
#  fix-kiosk.sh — Repairs Archimedes kiosk mode immediately
#
#  Run this on the Ubuntu machine when kiosk is broken (running as window):
#    cd ~/archimedes && git pull && bash scripts/fix-kiosk.sh
# =============================================================================

set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
ok()   { echo -e "${GREEN}[OK]${NC} $*"; }
info() { echo -e "${CYAN}[+]${NC} $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }

echo -e "\n${CYAN}=== Archimedes Kiosk Repair ===${NC}\n"

# ── 1. Kill any existing Chromium ─────────────────────────────────────────
info "Stopping existing Chromium..."
pkill -f chromium 2>/dev/null || true
sleep 2
ok "Chromium stopped"

# ── 2. Clear kiosk profile (removes all crash state) ──────────────────────
info "Clearing kiosk profile..."
rm -rf /tmp/archimedes-kiosk-profile
mkdir -p /tmp/archimedes-kiosk-profile
ok "Kiosk profile cleared"

# ── 3. Write updated kiosk launcher script ────────────────────────────────
info "Writing updated kiosk launcher..."
sudo tee /usr/local/bin/archimedes-kiosk > /dev/null << 'KIOSKEOF'
#!/bin/bash
# Archimedes Kiosk Launcher v2

# Disable X11 DPMS / blanking
xset -dpms   2>/dev/null || true
xset s off   2>/dev/null || true
xset s noblank 2>/dev/null || true

# Provide DBUS address for snap confinement
export DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$(id -u)/bus"

# Wait up to 90 s for Archimedes Core to be ready
for i in $(seq 1 45); do
    curl -sf http://localhost:5051/health >/dev/null 2>&1 && break
    sleep 2
done

# Use a dedicated profile dir — prevents session-restore dialogs
KIOSK_PROFILE="/tmp/archimedes-kiosk-profile"
mkdir -p "$KIOSK_PROFILE"

# Restart loop — Chromium restarts automatically if it crashes or is killed
while true; do
    # Clear crash-recovery banner — check BOTH regular and Snap Chromium paths
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

sudo chmod +x /usr/local/bin/archimedes-kiosk
ok "Kiosk launcher updated"

# ── 4. Verify autostart file exists ───────────────────────────────────────
AUTOSTART="$HOME/.config/autostart/archimedes-kiosk.desktop"
if [ ! -f "$AUTOSTART" ]; then
    warn "Autostart file missing — recreating..."
    mkdir -p "$HOME/.config/autostart"
    cat > "$AUTOSTART" << 'DESKTOPEOF'
[Desktop Entry]
Type=Application
Name=Archimedes Kiosk
Comment=Archimedes AI Agent fullscreen dashboard
Exec=/usr/local/bin/archimedes-kiosk
X-GNOME-Autostart-enabled=true
X-GNOME-Autostart-Delay=4
Hidden=false
NoDisplay=false
DESKTOPEOF
    ok "Autostart file recreated"
else
    ok "Autostart file exists: $AUTOSTART"
fi

# ── 5. Verify GDM Wayland is disabled ─────────────────────────────────────
if [ -f /etc/gdm3/custom.conf ]; then
    if grep -q "^WaylandEnable=false" /etc/gdm3/custom.conf; then
        ok "GDM: WaylandEnable=false confirmed"
    else
        warn "WaylandEnable=false missing from GDM config — fixing..."
        sudo sed -i '/\[daemon\]/a WaylandEnable=false' /etc/gdm3/custom.conf
        ok "WaylandEnable=false added"
    fi
fi

# ── 6. Verify Archimedes service is running ────────────────────────────────
if systemctl is-active --quiet archimedes; then
    ok "Archimedes service: running"
else
    warn "Archimedes service not running — starting..."
    sudo systemctl start archimedes
    sleep 3
    systemctl is-active --quiet archimedes && ok "Service started" || warn "Service still not running — check: journalctl -u archimedes -n 50"
fi

# ── 7. Launch kiosk now (in background) ───────────────────────────────────
info "Launching kiosk..."
nohup /usr/local/bin/archimedes-kiosk > /tmp/kiosk.log 2>&1 &
KIOSK_PID=$!
sleep 3

if kill -0 "$KIOSK_PID" 2>/dev/null; then
    ok "Kiosk started (PID $KIOSK_PID)"
else
    warn "Kiosk may have exited — check log: cat /tmp/kiosk.log"
fi

echo ""
echo -e "${GREEN}=== Kiosk repair complete ===${NC}"
echo -e "  Log: ${CYAN}tail -f /tmp/kiosk.log${NC}"
echo -e "  To restart manually: ${CYAN}/usr/local/bin/archimedes-kiosk${NC}"
echo ""
