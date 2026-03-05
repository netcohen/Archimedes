#!/bin/bash
# =============================================================================
#  Archimedes Bootstrap — One-Click Ubuntu Deployment
#  Usage (run as the user who will own the service, NOT as root):
#
#    bash <(curl -fsSL https://raw.githubusercontent.com/netcohen/Archimedes/main/scripts/bootstrap.sh)
#
#  Or from a local clone:
#    bash scripts/bootstrap.sh
#
#  What this script does (fully automatic):
#    1. Verifies Ubuntu 24.04 LTS
#    2. Installs .NET 8 SDK + runtime
#    3. Installs git, tor, curl, wget
#    4. Installs ADB (optional — for Android OTA updates)
#    5. Configures git user identity for Archimedes commits
#    6. Clones or updates the Archimedes repository
#    7. Builds the Core binary (dotnet publish -c Release)
#    8. Downloads the LLM model (~2 GB) from HuggingFace
#    9. Writes /etc/archimedes/environment with all required env vars
#   10. Writes the 24-hour CodePatcher hold marker (new-machine safety)
#   11. Installs Archimedes as a systemd service (via install-service.sh)
#   12. Verifies health endpoint responds
#
#  The only thing YOU need to do:
#    - Review the CONFIG section below (repo URL, data path, etc.)
#    - Run this script
#    - Wait ~10 minutes (mostly model download time)
# =============================================================================

set -euo pipefail

# =============================================================================
#  CONFIG — edit these if needed
# =============================================================================

REPO_URL="https://github.com/netcohen/Archimedes.git"
REPO_DIR="$HOME/archimedes"                       # where to clone the repo
DATA_DIR="$HOME/.local/share/Archimedes"          # runtime data (db, procedures, etc.)
MODEL_DIR="$DATA_DIR/models"
MODEL_PATH="$MODEL_DIR/llama3.2-3b.gguf"
MODEL_URL="https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf"
MODEL_MIN_GB=1.5
CORE_PORT=5051
DOTNET_CHANNEL="8.0"
GIT_USER_NAME="Archimedes"
GIT_USER_EMAIL="archimedes@localhost"

# =============================================================================
#  HELPERS
# =============================================================================

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
CYAN='\033[0;36m'; GRAY='\033[0;37m'; BOLD='\033[1m'; NC='\033[0m'

info()    { echo -e "${CYAN}[+]${NC} $*"; }
ok()      { echo -e "${GREEN}[OK]${NC} $*"; }
warn()    { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()     { echo -e "${RED}[ERR]${NC} $*" >&2; }
section() { echo -e "\n${BOLD}${CYAN}=== $* ===${NC}"; }

check_root() {
    if [ "$EUID" -eq 0 ]; then
        err "Do NOT run this script as root. Run as your normal user."
        err "The script will use sudo internally where needed."
        exit 1
    fi
}

# =============================================================================
#  BANNER
# =============================================================================

echo ""
echo -e "${BOLD}${CYAN}"
echo "  ╔══════════════════════════════════════════════════╗"
echo "  ║       Archimedes Bootstrap — Ubuntu 24.04       ║"
echo "  ║       One-click autonomous AI agent deploy      ║"
echo "  ╚══════════════════════════════════════════════════╝"
echo -e "${NC}"
echo -e "  Repo:  ${GRAY}$REPO_URL${NC}"
echo -e "  Local: ${GRAY}$REPO_DIR${NC}"
echo -e "  Data:  ${GRAY}$DATA_DIR${NC}"
echo -e "  Port:  ${GRAY}$CORE_PORT${NC}"
echo ""

# =============================================================================
#  STEP 0 — Preflight
# =============================================================================

section "Step 0 — Preflight checks"

check_root

# Ubuntu version
if ! grep -q "Ubuntu 24.04" /etc/os-release 2>/dev/null; then
    warn "This script is designed for Ubuntu 24.04 LTS."
    warn "Detected: $(grep PRETTY_NAME /etc/os-release | cut -d= -f2 | tr -d '\"')"
    read -rp "  Continue anyway? [y/N] " yn
    [[ "$yn" =~ ^[Yy]$ ]] || exit 1
fi
ok "Ubuntu 24.04 LTS confirmed"

# Internet connectivity
if ! curl -fsSL --max-time 5 https://github.com -o /dev/null 2>/dev/null; then
    err "No internet connectivity. Check your network connection."
    exit 1
fi
ok "Internet connectivity confirmed"

# =============================================================================
#  STEP 0.5 — Disable system sleep / suspend (Phase 37)
# =============================================================================

section "Step 0.5 — Disabling system sleep / suspend"

# Mask all sleep-related systemd targets — survives reboots
sudo systemctl mask sleep.target suspend.target hibernate.target hybrid-sleep.target 2>/dev/null || true

# Drop-in logind config: no lid-close suspend, no idle suspend
sudo mkdir -p /etc/systemd/logind.conf.d
sudo tee /etc/systemd/logind.conf.d/archimedes-no-sleep.conf > /dev/null << 'LOGINDCONF'
[Login]
HandleLidSwitch=ignore
HandleLidSwitchDocked=ignore
HandleLidSwitchExternalPower=ignore
HandleSuspendKey=ignore
HandleHibernateKey=ignore
IdleAction=ignore
IdleActionSec=0
LOGINDCONF

sudo systemctl restart systemd-logind 2>/dev/null || true
ok "System sleep/suspend permanently disabled"

# =============================================================================
#  STEP 1 — System packages
# =============================================================================

section "Step 1 — Installing system packages"

sudo apt-get update -qq
sudo apt-get install -y -qq \
    git \
    curl \
    wget \
    tor \
    apt-transport-https \
    ca-certificates

ok "git, curl, wget, tor installed"

# ADB (optional — for Android OTA)
if ! command -v adb &>/dev/null; then
    info "Installing ADB (Android Debug Bridge) for phone OTA updates..."
    sudo apt-get install -y -qq android-tools-adb 2>/dev/null || warn "ADB not available — Android OTA will be skipped"
fi
command -v adb &>/dev/null && ok "ADB installed: $(adb version | head -1)" || true

# =============================================================================
#  STEP 2 — .NET 8
# =============================================================================

section "Step 2 — .NET 8 SDK"

if command -v dotnet &>/dev/null && dotnet --version 2>/dev/null | grep -q "^8\."; then
    ok ".NET 8 already installed: $(dotnet --version)"
else
    info "Installing .NET 8 SDK (this may take a minute)..."
    wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    /tmp/dotnet-install.sh --channel "$DOTNET_CHANNEL" --install-dir "$HOME/.dotnet" --no-path 2>&1 | tail -3
    rm /tmp/dotnet-install.sh

    # Add to PATH for this session and permanently
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$PATH:$HOME/.dotnet:$HOME/.dotnet/tools"

    # Persist in .bashrc / .profile
    for rcfile in "$HOME/.bashrc" "$HOME/.profile"; do
        if [ -f "$rcfile" ] && ! grep -q "DOTNET_ROOT" "$rcfile"; then
            cat >> "$rcfile" << 'DOTNETRC'

# .NET 8 (added by Archimedes bootstrap)
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools"
DOTNETRC
        fi
    done
    ok ".NET 8 installed: $(dotnet --version)"
fi

# Make sure dotnet is in PATH for the rest of this script
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools"

# =============================================================================
#  STEP 3 — Clone / update repo
# =============================================================================

section "Step 3 — Repository"

if [ -d "$REPO_DIR/.git" ]; then
    info "Repo already exists — pulling latest from main..."
    git -C "$REPO_DIR" fetch origin
    git -C "$REPO_DIR" checkout main 2>/dev/null || true
    git -C "$REPO_DIR" reset --hard origin/main
    ok "Repo updated to origin/main: $REPO_DIR"
else
    info "Cloning Archimedes repo (branch: main)..."
    git clone -b main "$REPO_URL" "$REPO_DIR"
    ok "Repo cloned: $REPO_DIR"
fi

# =============================================================================
#  STEP 4 — Git identity (needed for CodePatcher self-commits)
# =============================================================================

section "Step 4 — Git identity"

CURRENT_GIT_NAME=$(git -C "$REPO_DIR" config user.name 2>/dev/null || true)
CURRENT_GIT_EMAIL=$(git -C "$REPO_DIR" config user.email 2>/dev/null || true)

if [ -z "$CURRENT_GIT_NAME" ]; then
    git -C "$REPO_DIR" config user.name "$GIT_USER_NAME"
    ok "git user.name set to: $GIT_USER_NAME"
else
    ok "git user.name already set: $CURRENT_GIT_NAME"
fi

if [ -z "$CURRENT_GIT_EMAIL" ]; then
    git -C "$REPO_DIR" config user.email "$GIT_USER_EMAIL"
    ok "git user.email set to: $GIT_USER_EMAIL"
else
    ok "git user.email already set: $CURRENT_GIT_EMAIL"
fi

# Verify origin is reachable
if git -C "$REPO_DIR" ls-remote origin HEAD &>/dev/null; then
    ok "Git remote origin is reachable"
else
    warn "Git remote origin not reachable — CodePatcher commits will be local only"
fi

# =============================================================================
#  STEP 5 — Build Core binary
# =============================================================================

section "Step 5 — Building Archimedes Core"

# Use explicit .csproj path + full dotnet binary path to avoid MSB1009
CSPROJ="$REPO_DIR/core/Archimedes.Core.csproj"
DOTNET_BIN="$DOTNET_ROOT/dotnet"

if [ ! -f "$CSPROJ" ]; then
    err "Project file not found: $CSPROJ"
    err "Check that the repo cloned correctly: ls $REPO_DIR/core/"
    exit 1
fi

info "Running dotnet publish (Release)..."
"$DOTNET_BIN" publish "$CSPROJ" \
    -c Release \
    -o "$REPO_DIR/core/bin/Release/net8.0" \
    --nologo 2>&1

if [ ! -f "$REPO_DIR/core/bin/Release/net8.0/Archimedes.Core.dll" ]; then
    err "Build failed — Archimedes.Core.dll not found"
    exit 1
fi
ok "Core binary built successfully"

# =============================================================================
#  STEP 6 — LLM model download
# =============================================================================

section "Step 6 — LLM model (Llama-3.2-3B-Instruct, ~2 GB)"

mkdir -p "$MODEL_DIR"

if [ -f "$MODEL_PATH" ]; then
    MODEL_SIZE_GB=$(du -BG "$MODEL_PATH" | cut -f1 | tr -d 'G')
    if [ "${MODEL_SIZE_GB:-0}" -ge "${MODEL_MIN_GB%.*}" ] 2>/dev/null; then
        ok "Model already exists ($MODEL_SIZE_GB GB) — skipping download"
    else
        warn "Existing model file is too small (${MODEL_SIZE_GB}GB) — re-downloading"
        rm -f "$MODEL_PATH"
    fi
fi

if [ ! -f "$MODEL_PATH" ]; then
    info "Downloading LLM model... (this takes 5-20 minutes depending on connection)"
    info "  URL: $MODEL_URL"
    echo ""
    wget --progress=bar:force:noscroll \
         --header="User-Agent: Archimedes/1.0" \
         -O "$MODEL_PATH.tmp" \
         "$MODEL_URL" 2>&1 || {
        err "Model download failed"
        rm -f "$MODEL_PATH.tmp"
        exit 1
    }
    mv "$MODEL_PATH.tmp" "$MODEL_PATH"

    MODEL_SIZE_GB=$(du -BG "$MODEL_PATH" | cut -f1 | tr -d 'G')
    ok "Model downloaded: ${MODEL_SIZE_GB}GB at $MODEL_PATH"
fi

# =============================================================================
#  STEP 7 — Environment configuration
# =============================================================================

section "Step 7 — Environment configuration"

sudo mkdir -p /etc/archimedes

# Write environment file (idempotent — only if not already configured)
if [ ! -f /etc/archimedes/environment ]; then
    sudo tee /etc/archimedes/environment > /dev/null << EOF
# Archimedes environment — written by bootstrap.sh
ARCHIMEDES_REPO_ROOT=$REPO_DIR
ARCHIMEDES_DATA_PATH=$DATA_DIR
ARCHIMEDES_STORAGE_INTERNAL=$DATA_DIR
ARCHIMEDES_MODEL_PATH=$MODEL_PATH
ARCHIMEDES_PORT=$CORE_PORT
ARCHIMEDES_LOGS_RETENTION_DAYS=7
ARCHIMEDES_ARTIFACTS_MAX_GB=20
# LLM_GPU_LAYERS=0   # set to -1 to use GPU (requires CUDA)
EOF
    ok "Environment file written: /etc/archimedes/environment"
else
    ok "Environment file already exists — not overwriting"
    info "  To reconfigure: sudo nano /etc/archimedes/environment"
fi

# =============================================================================
#  STEP 8 — CodePatcher 24h hold marker (new-machine safety)
# =============================================================================

section "Step 8 — CodePatcher 24h hold (new-machine safety)"

mkdir -p "$DATA_DIR"
HOLD_UNTIL=$(date -u -d "+24 hours" --iso-8601=seconds)
echo "$HOLD_UNTIL" > "$DATA_DIR/new_machine_codepatcher_hold_until.txt"
ok "CodePatcher suppressed until $HOLD_UNTIL"
info "  Archimedes will research, benchmark, and self-test for 24h"
info "  before starting autonomous code patches."
info "  After 24h the hold auto-expires — no action needed."

# =============================================================================
#  STEP 9 — Tor service
# =============================================================================

section "Step 9 — Tor (web research privacy)"

if systemctl is-active tor &>/dev/null; then
    ok "Tor already running"
else
    sudo systemctl enable tor
    sudo systemctl start tor
    sleep 2
    systemctl is-active tor &>/dev/null && ok "Tor started" || warn "Tor failed to start — Archimedes will use direct HTTP for research"
fi

# =============================================================================
#  STEP 9.5 — Create 'live' branch on remote (Phase 36)
# =============================================================================

section "Step 9.5 — Creating 'live' branch on remote"

# 'live' = where Archimedes pushes all autonomous self-patch commits.
# 'main' = human-authored only. Compare main..live on GitHub to audit AI changes.
if git -C "$REPO_DIR" ls-remote --heads origin live | grep -q live; then
    ok "'live' branch already exists on remote"
else
    if git -C "$REPO_DIR" push origin HEAD:refs/heads/live 2>/dev/null; then
        ok "'live' branch created on remote from current main"
    else
        warn "Could not create 'live' branch — will be created on first AI self-patch"
    fi
fi

# =============================================================================
#  STEP 10 — Install systemd service
# =============================================================================

section "Step 10 — Installing Archimedes systemd service"

# Update service file WorkingDirectory and ExecStart to use the built binary
ARCH_SERVICE="$REPO_DIR/scripts/archimedes.service"
ARCH_BIN_DIR="$REPO_DIR/core/bin/Release/net8.0"

# Generate a service file patched for this machine's paths
sudo tee /etc/systemd/system/archimedes.service > /dev/null << EOF
[Unit]
Description=Archimedes AI Agent
Documentation=$REPO_URL
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$USER
Group=$USER
WorkingDirectory=$ARCH_BIN_DIR
ExecStart=$(command -v dotnet) $ARCH_BIN_DIR/Archimedes.Core.dll
Restart=always
RestartSec=10
StartLimitIntervalSec=120
StartLimitBurst=5
StandardOutput=journal
StandardError=journal
SyslogIdentifier=archimedes

# Environment
Environment=ASPNETCORE_URLS=http://localhost:$CORE_PORT
Environment=DOTNET_ROOT=$DOTNET_ROOT
Environment=PATH=$PATH
Environment=DOTNET_ENVIRONMENT=Production
EnvironmentFile=-/etc/archimedes/environment

# Security hardening
NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=strict
ReadWritePaths=$REPO_DIR $DATA_DIR /tmp /var/log

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable archimedes
sudo systemctl restart archimedes

info "Waiting for Core to start..."
sleep 4

# =============================================================================
#  STEP 11 — Health check
# =============================================================================

section "Step 11 — Health verification"

HEALTH_OK=false
for i in $(seq 1 10); do
    HTTP_STATUS=$(curl -s -o /dev/null -w "%{http_code}" "http://localhost:$CORE_PORT/health" 2>/dev/null || true)
    if [ "$HTTP_STATUS" = "200" ]; then
        HEALTH_OK=true
        break
    fi
    info "  Waiting for Core... (attempt $i/10)"
    sleep 3
done

if $HEALTH_OK; then
    ok "Health check PASS: http://localhost:$CORE_PORT/health"
else
    warn "Health check did not respond yet. Check logs:"
    warn "  journalctl -u archimedes -n 50 --no-pager"
fi

# LLM health
LLM_STATUS=$(curl -s "http://localhost:$CORE_PORT/llm/health" 2>/dev/null || echo "{}")
echo -e "  LLM status: ${GRAY}$LLM_STATUS${NC}"

# Self-improvement status
SI_STATUS=$(curl -s "http://localhost:$CORE_PORT/selfimprove/status" 2>/dev/null || echo "{}")
echo -e "  Self-improve: ${GRAY}$SI_STATUS${NC}"

# =============================================================================
#  DONE
# =============================================================================

# =============================================================================
#  STEP 12 — Kiosk display: Chromium fullscreen (Phase 37)
#  Auto-detects Ubuntu Desktop (GDM/GNOME) vs Ubuntu Server (TTY1/startx)
# =============================================================================

section "Step 12 — Kiosk display (Chromium fullscreen)"

# ── Detect display environment ──────────────────────────────────────────
HAS_GNOME=false
HAS_GDM=false
if systemctl is-active --quiet gdm 2>/dev/null || systemctl is-active --quiet gdm3 2>/dev/null \
   || [ -f /etc/gdm3/custom.conf ] || command -v gnome-session &>/dev/null; then
    HAS_GNOME=true
fi
command -v gdm3 &>/dev/null && HAS_GDM=true

if $HAS_GNOME; then
    info "Detected: Ubuntu Desktop (GNOME) — using GDM auto-login + GNOME autostart"
else
    info "Detected: Ubuntu Server / no desktop — using TTY1 + startx"
fi

# ── Install Chromium ────────────────────────────────────────────────────
if ! command -v chromium-browser &>/dev/null && ! command -v chromium &>/dev/null; then
    info "Installing Chromium…"
    sudo apt-get install -y -qq chromium-browser 2>/dev/null \
    || sudo snap install chromium 2>/dev/null \
    || warn "Chromium not installed — kiosk browser unavailable"
fi
CHROMIUM_BIN=$(command -v chromium-browser 2>/dev/null \
             || command -v chromium 2>/dev/null \
             || snap run chromium --version &>/dev/null && echo "snap run chromium" \
             || echo "")
[ -n "$CHROMIUM_BIN" ] && ok "Browser: $CHROMIUM_BIN" || warn "Browser binary not found — kiosk may not start"

# ── Shared: kiosk launch script ─────────────────────────────────────────
KIOSK_SCRIPT="$HOME/.local/bin/archimedes-kiosk.sh"
mkdir -p "$HOME/.local/bin"
cat > "$KIOSK_SCRIPT" << KIOSKSH
#!/bin/bash
# Archimedes kiosk launcher — waits for Core then opens Chromium fullscreen

# Disable X11 screen blanking/DPMS (if X is running)
xset s off 2>/dev/null || true
xset s noblank 2>/dev/null || true
xset -dpms 2>/dev/null || true

# Wait up to 90 s for Core to be ready
for i in \$(seq 1 45); do
    curl -sf http://localhost:$CORE_PORT/health >/dev/null 2>&1 && break
    sleep 2
done

CHROMIUM=\$(command -v chromium-browser 2>/dev/null || command -v chromium 2>/dev/null || echo "snap run chromium")

exec \$CHROMIUM \\
    --kiosk \\
    --no-first-run \\
    --disable-infobars \\
    --disable-session-crashed-bubble \\
    --disable-restore-session-state \\
    --noerrdialogs \\
    --disable-translate \\
    --check-for-update-interval=31536000 \\
    --window-position=0,0 \\
    http://localhost:$CORE_PORT/dashboard
KIOSKSH
chmod +x "$KIOSK_SCRIPT"
ok "Kiosk launcher: $KIOSK_SCRIPT"

# ══════════════════════════════════════════════════════════════════════════
#  PATH A — Ubuntu Desktop (GNOME + GDM)
# ══════════════════════════════════════════════════════════════════════════
if $HAS_GNOME; then

    # 1. GDM auto-login
    if [ -f /etc/gdm3/custom.conf ]; then
        sudo sed -i 's/^#\?AutomaticLoginEnable=.*/AutomaticLoginEnable=True/' /etc/gdm3/custom.conf
        sudo sed -i "s/^#\?AutomaticLogin=.*/AutomaticLogin=$USER/"              /etc/gdm3/custom.conf
        # Insert if not already present
        grep -q "AutomaticLoginEnable" /etc/gdm3/custom.conf || \
            sudo sed -i '/\[daemon\]/a AutomaticLoginEnable=True\nAutomaticLogin='"$USER" /etc/gdm3/custom.conf
        ok "GDM auto-login configured for: $USER"
    else
        warn "/etc/gdm3/custom.conf not found — auto-login may need manual setup"
    fi

    # 2. GNOME autostart entry → launches kiosk after login
    AUTOSTART_DIR="$HOME/.config/autostart"
    mkdir -p "$AUTOSTART_DIR"
    cat > "$AUTOSTART_DIR/archimedes-kiosk.desktop" << DESKTOP
[Desktop Entry]
Type=Application
Name=Archimedes Kiosk
Comment=Launch Archimedes dashboard in kiosk mode
Exec=$KIOSK_SCRIPT
Hidden=false
NoDisplay=false
X-GNOME-Autostart-enabled=true
X-GNOME-Autostart-Delay=3
DESKTOP
    ok "GNOME autostart configured: $AUTOSTART_DIR/archimedes-kiosk.desktop"

    # 3. Disable GNOME screen lock / idle suspend / notifications
    #    (gsettings only works inside an active user session — use dbus-launch)
    if command -v gsettings &>/dev/null; then
        # Screen lock
        gsettings set org.gnome.desktop.screensaver lock-enabled false 2>/dev/null || true
        gsettings set org.gnome.desktop.screensaver idle-activation-enabled false 2>/dev/null || true
        # Session idle (AC)
        gsettings set org.gnome.settings-daemon.plugins.power sleep-inactive-ac-timeout 0 2>/dev/null || true
        gsettings set org.gnome.settings-daemon.plugins.power sleep-inactive-battery-timeout 0 2>/dev/null || true
        gsettings set org.gnome.settings-daemon.plugins.power idle-dim false 2>/dev/null || true
        # Notifications off
        gsettings set org.gnome.desktop.notifications show-banners false 2>/dev/null || true
        ok "GNOME: screen lock + idle suspend disabled"
    else
        warn "gsettings not found — GNOME idle settings not changed"
    fi

    info "  On next reboot: GDM will auto-login → Chromium opens Archimedes dashboard"
    info "  To test now (without reboot): bash $KIOSK_SCRIPT &"

# ══════════════════════════════════════════════════════════════════════════
#  PATH B — Ubuntu Server (no desktop, TTY1 + startx)
# ══════════════════════════════════════════════════════════════════════════
else

    # Install minimal X11 + window manager
    info "Installing X11 + Openbox + unclutter…"
    sudo apt-get install -y -qq xorg openbox unclutter 2>/dev/null || warn "Some X11 packages failed"

    # Auto-login on TTY1 via getty override
    sudo mkdir -p /etc/systemd/system/getty@tty1.service.d
    sudo tee /etc/systemd/system/getty@tty1.service.d/autologin.conf > /dev/null << AUTOLOGIN
[Service]
ExecStart=
ExecStart=-/sbin/agetty --autologin $USER --noclear %I \$TERM
AUTOLOGIN
    sudo systemctl daemon-reload
    ok "Auto-login on TTY1 configured for: $USER"

    # .bash_profile → exec startx on TTY1
    BASH_PROF="$HOME/.bash_profile"
    if ! grep -q "ARCHIMEDES_KIOSK" "$BASH_PROF" 2>/dev/null; then
        cat >> "$BASH_PROF" << 'XSTART'

# ARCHIMEDES_KIOSK: auto-start X kiosk on TTY1
if [ -z "$DISPLAY" ] && [ "$(tty)" = "/dev/tty1" ]; then
    exec startx -- -nocursor 2>/tmp/archimedes-xstart.log
fi
XSTART
        ok ".bash_profile: auto-start X on TTY1"
    else
        ok ".bash_profile already configured"
    fi

    # .xinitrc → openbox + kiosk launcher
    cat > "$HOME/.xinitrc" << XINITRC
#!/bin/bash
# Archimedes kiosk (TTY1 / startx mode)
xset s off; xset s noblank; xset -dpms
unclutter -idle 1 -root &
openbox &
exec $KIOSK_SCRIPT
XINITRC
    chmod +x "$HOME/.xinitrc"
    ok "Kiosk: TTY1 → startx → openbox → Chromium"

    info "  Kiosk starts automatically on next reboot."
    info "  To start now:  startx"

fi

echo ""
echo -e "${BOLD}${GREEN}"
echo "  ╔══════════════════════════════════════════════════╗"
echo "  ║         Archimedes is alive.                    ║"
echo "  ╚══════════════════════════════════════════════════╝"
echo -e "${NC}"
echo -e "  Health:      ${CYAN}http://localhost:$CORE_PORT/health${NC}"
echo -e "  Self-improve:${CYAN}http://localhost:$CORE_PORT/selfimprove/status${NC}"
echo -e "  Logs:        ${GRAY}journalctl -u archimedes -f${NC}"
echo -e "  Repo:        ${GRAY}$REPO_DIR${NC}"
echo -e "  Data:        ${GRAY}$DATA_DIR${NC}"
echo -e "  Model:       ${GRAY}$MODEL_PATH${NC}"
echo ""
echo -e "  ${YELLOW}CodePatcher hold active for 24h — research starts immediately.${NC}"
echo ""
echo -e "  To send Archimedes its first task:"
echo -e "  ${GRAY}curl -X POST http://localhost:$CORE_PORT/tasks \\${NC}"
echo -e "  ${GRAY}  -H 'Content-Type: application/json' \\${NC}"
echo -e "  ${GRAY}  -d '{\"intent\":\"TESTSITE_EXPORT\",\"url\":\"https://example.com\"}'${NC}"
echo ""
