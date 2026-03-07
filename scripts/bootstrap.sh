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
OLLAMA_MODEL="qwen2.5:7b"           # tool-use optimised model — better than llama3.1:8b for agent tasks
OLLAMA_URL="http://localhost:11434"  # Ollama runs as a local service
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

# Do NOT restart systemd-logind here — it kills the active GNOME session.
# The logind.conf changes take effect at next reboot (or next logind restart).
sudo systemctl daemon-reload 2>/dev/null || true
ok "System sleep/suspend permanently disabled (takes full effect at next reboot)"

# =============================================================================
#  STEP 0.6 — sudo without password (Archimedes needs autonomous sudo access)
# =============================================================================

section "Step 0.6 — Passwordless sudo for Archimedes"

SUDOERS_FILE="/etc/sudoers.d/archimedes-nopasswd"
echo "$USER ALL=(ALL) NOPASSWD: ALL" | sudo tee "$SUDOERS_FILE" > /dev/null
sudo chmod 440 "$SUDOERS_FILE"
# Validate — visudo -c will exit non-zero if the file is broken
sudo visudo -c -f "$SUDOERS_FILE" 2>/dev/null && ok "Passwordless sudo configured for: $USER" \
    || warn "sudoers validation warning — check $SUDOERS_FILE manually"

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
#  STEP 6 — Ollama LLM runtime + model
# =============================================================================

section "Step 6 — Ollama LLM runtime (replaces LlamaSharp)"

# Install Ollama if not present
if command -v ollama &>/dev/null; then
    ok "Ollama already installed: $(ollama --version 2>/dev/null | head -1)"
else
    info "Installing Ollama..."
    curl -fsSL https://ollama.ai/install.sh | sh
    ok "Ollama installed"
fi

# Enable + start Ollama service
sudo systemctl enable ollama 2>/dev/null || true
sudo systemctl start  ollama 2>/dev/null || true
sleep 3
systemctl is-active --quiet ollama && ok "Ollama service: running" \
    || { warn "Ollama service not running — trying to start..."; sudo systemctl restart ollama; sleep 5; }

# Pull model (skipped automatically if already present)
info "Pulling model: $OLLAMA_MODEL (may take 10-20 minutes on first run)..."
ollama pull "$OLLAMA_MODEL" 2>&1 | tail -5
ok "Model ready: $OLLAMA_MODEL"

# Remove old .gguf files if any (free disk space)
if ls "$MODEL_DIR"/*.gguf 2>/dev/null | grep -q .; then
    info "Removing old .gguf model files..."
    rm -f "$MODEL_DIR"/*.gguf
    ok "Old model files removed"
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
ARCHIMEDES_PORT=$CORE_PORT
ARCHIMEDES_LOGS_RETENTION_DAYS=7
ARCHIMEDES_ARTIFACTS_MAX_GB=20
# Ollama LLM backend
ARCHIMEDES_OLLAMA_URL=$OLLAMA_URL
ARCHIMEDES_OLLAMA_MODEL=$OLLAMA_MODEL
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
# GIT_TERMINAL_PROMPT=0 prevents git from prompting for credentials interactively.
# If the push fails (no auth configured), we just warn and continue.
if GIT_TERMINAL_PROMPT=0 git -C "$REPO_DIR" ls-remote --heads origin live 2>/dev/null | grep -q live; then
    ok "'live' branch already exists on remote"
else
    if GIT_TERMINAL_PROMPT=0 git -C "$REPO_DIR" push origin HEAD:refs/heads/live 2>/dev/null; then
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

# Archimedes needs full system access to act as an autonomous agent.
# Security hardening directives (NoNewPrivileges, ProtectSystem, etc.)
# are intentionally omitted — they would block sudo and filesystem access.

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
#  STEP 12 — Kiosk display: Chromium fullscreen (Phase 37 — research-based)
#
#  Approach (Ubuntu 24.04 Desktop + Server):
#   • Ubuntu Desktop (GDM): custom X11 session via /usr/share/xsessions/
#     - Disables Wayland (avoids GDM3 autologin+Wayland black screen bug)
#     - AccountsService sets session = archimedes-kiosk
#     - /usr/local/bin/archimedes-kiosk runs Chromium in restart loop
#     - dconf system-db disables lock/sleep without needing a live session
#     - Xorg conf disables DPMS at driver level
#   • Ubuntu Server (no GDM): TTY1 getty auto-login + startx + .xinitrc
# =============================================================================

section "Step 12 — Kiosk display (Chromium fullscreen)"

# ── Detect display environment ──────────────────────────────────────────
HAS_GDM=false
if [ -f /etc/gdm3/custom.conf ] || systemctl is-active --quiet gdm 2>/dev/null \
   || systemctl is-active --quiet gdm3 2>/dev/null; then
    HAS_GDM=true
fi

if $HAS_GDM; then
    info "Detected: Ubuntu Desktop (GDM3) — using custom X11 session"
else
    info "Detected: Ubuntu Server / no GDM — using TTY1 + startx"
fi

# ── Install packages ────────────────────────────────────────────────────
info "Installing kiosk packages (unclutter, dconf-cli)…"
sudo apt-get install -y -qq unclutter dconf-cli 2>/dev/null || true

# ── Install Chromium snap (only correct binary on Ubuntu 24.04) ────────
# apt install chromium-browser is a stub — real package is the snap.
if /snap/bin/chromium --version &>/dev/null 2>&1; then
    ok "Chromium already installed: $(/snap/bin/chromium --version | head -1)"
else
    info "Installing Chromium snap (this may take 2-3 minutes)…"
    sudo snap install chromium
    # Hard fail if still missing — nothing else in Step 12 will work without it
    if ! /snap/bin/chromium --version &>/dev/null 2>&1; then
        err "Chromium snap install failed. Check: sudo snap install chromium"
        exit 1
    fi
    ok "Chromium installed: $(/snap/bin/chromium --version | head -1)"
fi
# Freeze snap version — prevents Chromium auto-updating mid-session
sudo snap refresh --hold chromium 2>/dev/null || true

# ── Shared: kiosk launcher script (/usr/local/bin/archimedes-kiosk) ────
sudo tee /usr/local/bin/archimedes-kiosk > /dev/null << KIOSKSH
#!/bin/bash
# Archimedes Kiosk Launcher v2
# Waits for Core → clears Chromium crash flag → opens dashboard in restart loop

# Disable X11 DPMS / blanking
xset -dpms   2>/dev/null || true
xset s off   2>/dev/null || true
xset s noblank 2>/dev/null || true

# Provide DBUS address for snap confinement
export DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/\$(id -u)/bus"

# Wait up to 90 s for Archimedes Core to be ready
for i in \$(seq 1 45); do
    curl -sf http://localhost:$CORE_PORT/health >/dev/null 2>&1 && break
    sleep 2
done

# Use a dedicated temp-ish profile dir — prevents session-restore dialogs on every start
KIOSK_PROFILE="/tmp/archimedes-kiosk-profile"
mkdir -p "\$KIOSK_PROFILE"

# Restart loop — Chromium restarts automatically if it crashes or is killed
while true; do
    # Clear crash-recovery banner — Snap Chromium stores prefs in a different path than regular.
    # Check BOTH locations so the fix works regardless of how Chromium was installed.
    for PREF in \\
        "\$HOME/snap/chromium/current/.config/chromium/Default/Preferences" \\
        "\$HOME/.config/chromium/Default/Preferences"; do
        if [ -f "\$PREF" ]; then
            sed -i 's/"exited_cleanly":false/"exited_cleanly":true/g'  "\$PREF" 2>/dev/null || true
            sed -i 's/"exit_type":"Crashed"/"exit_type":"Normal"/g'     "\$PREF" 2>/dev/null || true
        fi
    done

    /snap/bin/chromium \\
        --kiosk \\
        --user-data-dir="\$KIOSK_PROFILE" \\
        --noerrdialogs \\
        --disable-infobars \\
        --disable-session-crashed-bubble \\
        --disable-restore-session-state \\
        --disable-component-update \\
        --check-for-update-interval=31536000 \\
        --no-first-run \\
        --disable-translate \\
        --disable-features=TranslateUI \\
        --disable-pinch \\
        --overscroll-history-navigation=0 \\
        http://localhost:$CORE_PORT/dashboard

    sleep 3
done
KIOSKSH
sudo chmod +x /usr/local/bin/archimedes-kiosk
ok "Kiosk launcher: /usr/local/bin/archimedes-kiosk"

# ── dconf system-db: disable lock/sleep without needing active session ──
# This is the ONLY reliable way to set GNOME idle/lock from a boot script.
sudo mkdir -p /etc/dconf/profile /etc/dconf/db/local.d
sudo tee /etc/dconf/profile/user > /dev/null << 'DCONFPROFILE'
user-db:user
system-db:local
DCONFPROFILE
sudo tee /etc/dconf/db/local.d/00-archimedes-kiosk > /dev/null << 'DCONFDB'
[org/gnome/desktop/screensaver]
lock-enabled=false
idle-activation-enabled=false

[org/gnome/desktop/session]
idle-delay=uint32 0

[org/gnome/desktop/lockdown]
disable-lock-screen=true

[org/gnome/settings-daemon/plugins/power]
sleep-inactive-ac-type='nothing'
sleep-inactive-battery-type='nothing'
idle-dim=false
power-button-action='nothing'

[org/gnome/desktop/notifications]
show-banners=false

[org/gnome/desktop/input-sources]
sources=[('xkb', 'us'), ('xkb', 'il')]
xkb-options=['grp:alt_shift_toggle']
DCONFDB
sudo dconf update 2>/dev/null && ok "dconf system policy: screen lock + idle disabled" \
    || warn "dconf update failed — may need manual run after reboot"

# ── Xorg conf: disable DPMS at driver level (survives X restarts) ──────
sudo mkdir -p /etc/X11/xorg.conf.d
sudo tee /etc/X11/xorg.conf.d/90-archimedes-nodpms.conf > /dev/null << 'XORGCONF'
Section "ServerFlags"
    Option "StandbyTime" "0"
    Option "SuspendTime" "0"
    Option "OffTime"     "0"
    Option "BlankTime"   "0"
EndSection
XORGCONF
ok "Xorg: DPMS/blanking disabled at driver level"

# ══════════════════════════════════════════════════════════════════════════
#  PATH A — Ubuntu Desktop (GDM3)
#  Strategy: GDM auto-login → standard GNOME session → autostart kiosk
#  (Custom X session abandoned: Chromium snap fails without GNOME env vars)
# ══════════════════════════════════════════════════════════════════════════
if $HAS_GDM; then

    # 1. Clean up previous failed kiosk session attempts
    sudo rm -f /usr/share/xsessions/archimedes-kiosk.desktop
    sudo rm -f /var/lib/AccountsService/users/"$USER"
    ok "Cleaned up previous kiosk session artifacts"

    # 2. GNOME autostart: launch kiosk after GNOME session starts
    #    Created BEFORE GDM config write — so even if something restarts the session,
    #    the autostart file is already in place.
    mkdir -p "$HOME/.config/autostart"
    cat > "$HOME/.config/autostart/archimedes-kiosk.desktop" << 'AUTOSTART'
[Desktop Entry]
Type=Application
Name=Archimedes Kiosk
Comment=Archimedes AI Agent fullscreen dashboard
Exec=/usr/local/bin/archimedes-kiosk
X-GNOME-Autostart-enabled=true
X-GNOME-Autostart-Delay=4
Hidden=false
NoDisplay=false
AUTOSTART
    ok "GNOME autostart: ~/.config/autostart/archimedes-kiosk.desktop"

    # 3. Disable Wayland + enable auto-login  (written LAST in PATH A)
    #    WaylandEnable=false: avoids GDM3 46.2 autologin black-screen bug
    #    Write the entire file (not sed) to guarantee WaylandEnable is uncommented
    sudo bash -c "cat > /etc/gdm3/custom.conf" << GDMCONF
[daemon]
AutomaticLoginEnable=true
AutomaticLogin=$USER
WaylandEnable=false

[security]

[xdmcp]

[chooser]

[debug]
GDMCONF
    ok "GDM: auto-login=$USER, Wayland=disabled"
    # Verify WaylandEnable was written correctly
    grep -q "^WaylandEnable=false" /etc/gdm3/custom.conf \
        && ok "WaylandEnable=false confirmed in gdm3/custom.conf" \
        || warn "WaylandEnable line not found — kiosk may show black screen"

    # 4. Black desktop background (hides GNOME desktop during kiosk startup)
    sudo -u "$USER" dbus-launch gsettings set org.gnome.desktop.background picture-uri '' 2>/dev/null || true
    sudo -u "$USER" dbus-launch gsettings set org.gnome.desktop.background primary-color '#000000' 2>/dev/null || true

    info "  Reboot → GDM auto-login → GNOME → autostart kiosk → Chromium dashboard"

# ══════════════════════════════════════════════════════════════════════════
#  PATH B — Ubuntu Server (no GDM, TTY1 + startx)
# ══════════════════════════════════════════════════════════════════════════
else

    sudo apt-get install -y -qq xorg 2>/dev/null || warn "xorg install failed"

    # TTY1 auto-login via getty override
    sudo mkdir -p /etc/systemd/system/getty@tty1.service.d
    sudo tee /etc/systemd/system/getty@tty1.service.d/autologin.conf > /dev/null << AUTOLOGIN
[Service]
ExecStart=
ExecStart=-/sbin/agetty --autologin $USER --noclear %I \$TERM
AUTOLOGIN
    sudo systemctl daemon-reload
    ok "TTY1: auto-login as $USER"

    # .bash_profile → exec startx on TTY1
    if ! grep -q "ARCHIMEDES_KIOSK" "$HOME/.bash_profile" 2>/dev/null; then
        cat >> "$HOME/.bash_profile" << 'XSTART'

# ARCHIMEDES_KIOSK: auto-start X kiosk on TTY1
if [ -z "$DISPLAY" ] && [ "$(tty)" = "/dev/tty1" ]; then
    exec startx -- -nocursor 2>/tmp/archimedes-xstart.log
fi
XSTART
        ok ".bash_profile: exec startx on TTY1"
    fi

    # .xinitrc → launch kiosk script directly (no window manager needed)
    cat > "$HOME/.xinitrc" << 'XINITRC'
#!/bin/bash
exec /usr/local/bin/archimedes-kiosk
XINITRC
    chmod +x "$HOME/.xinitrc"
    ok "Kiosk: TTY1 → startx → Chromium"

    info "  Kiosk starts automatically on next reboot."

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
