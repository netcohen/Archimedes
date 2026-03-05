#!/bin/bash
# Archimedes Service Installer for Ubuntu
# Usage: sudo bash scripts/install-service.sh
# Installs Archimedes as a systemd service with auto-start, self-healing, and apt sudo permissions.

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
INSTALL_DIR="/opt/archimedes"
LOG_DIR="/var/log/archimedes"
CONFIG_DIR="/etc/archimedes"
SERVICE_USER="archimedes"
SERVICE_FILE="$SCRIPT_DIR/archimedes.service"
SUDOERS_FILE="/etc/sudoers.d/archimedes"

# ── Preflight ────────────────────────────────────────────────────────────────

echo "=== Archimedes Service Installer ==="
echo "  Repo root:   $REPO_ROOT"
echo "  Install dir: $INSTALL_DIR"

if [ "$EUID" -ne 0 ]; then
    echo "ERROR: Please run as root (sudo bash $0)"
    exit 1
fi

if ! command -v dotnet &>/dev/null; then
    echo "ERROR: .NET runtime not found. Install with:"
    echo "  wget https://dot.net/v1/dotnet-install.sh && bash dotnet-install.sh --channel 8.0"
    exit 1
fi

# ── System user ──────────────────────────────────────────────────────────────

if ! id -u "$SERVICE_USER" &>/dev/null; then
    useradd --system --no-create-home --shell /bin/false "$SERVICE_USER"
    echo "Created system user: $SERVICE_USER"
else
    echo "System user $SERVICE_USER already exists — skipping"
fi

# ── Directories ──────────────────────────────────────────────────────────────

mkdir -p "$INSTALL_DIR/core"
mkdir -p "$LOG_DIR"
mkdir -p "$CONFIG_DIR"
chown "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR" "$INSTALL_DIR/core" "$LOG_DIR"
chmod 750 "$INSTALL_DIR" "$INSTALL_DIR/core"
echo "Created directories"

# ── Copy binaries ────────────────────────────────────────────────────────────

RELEASE_BIN="$REPO_ROOT/core/bin/Release/net8.0"
DEBUG_BIN="$REPO_ROOT/core/bin/Debug/net8.0"

if [ -d "$RELEASE_BIN" ]; then
    BIN_DIR="$RELEASE_BIN"
elif [ -d "$DEBUG_BIN" ]; then
    BIN_DIR="$DEBUG_BIN"
else
    echo "WARNING: No compiled binary found. Building now..."
    cd "$REPO_ROOT/core" && dotnet publish -c Release -o "$INSTALL_DIR/core" --no-self-contained
    BIN_DIR=""
fi

if [ -n "$BIN_DIR" ]; then
    cp -r "$BIN_DIR/." "$INSTALL_DIR/core/"
    echo "Copied binaries from $BIN_DIR"
fi

chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR/core"

# ── Sudoers for apt + ufw + reboot ───────────────────────────────────────────

cat > "$SUDOERS_FILE" << 'SUDOERS'
# Archimedes AI Agent — allow package management and controlled reboot
archimedes ALL=(ALL) NOPASSWD: /usr/bin/apt-get update *
archimedes ALL=(ALL) NOPASSWD: /usr/bin/apt-get upgrade *
archimedes ALL=(ALL) NOPASSWD: /usr/bin/apt-get autoremove *
archimedes ALL=(ALL) NOPASSWD: /usr/bin/apt-get autoclean *
archimedes ALL=(ALL) NOPASSWD: /usr/sbin/ufw *
archimedes ALL=(ALL) NOPASSWD: /sbin/reboot
archimedes ALL=(ALL) NOPASSWD: /sbin/shutdown *
SUDOERS

chmod 440 "$SUDOERS_FILE"
echo "Configured sudo permissions: $SUDOERS_FILE"

# ── Environment file ─────────────────────────────────────────────────────────

if [ ! -f "$CONFIG_DIR/environment" ]; then
    cat > "$CONFIG_DIR/environment" << 'ENV'
# Archimedes environment overrides
# ARCHIMEDES_PORT=5051
# ARCHIMEDES_DATA_PATH=/opt/archimedes/data
# LLM_GPU_LAYERS=-1
ENV
    echo "Created environment template: $CONFIG_DIR/environment"
fi

# ── systemd service ──────────────────────────────────────────────────────────

cp "$SERVICE_FILE" /etc/systemd/system/archimedes.service
echo "Installed service file: /etc/systemd/system/archimedes.service"

systemctl daemon-reload
systemctl enable archimedes
systemctl restart archimedes

sleep 2
STATUS=$(systemctl is-active archimedes)
echo ""
echo "=== Installation complete ==="
echo "  Service status:  $STATUS"
echo "  Live logs:       journalctl -u archimedes -f"
echo "  Health check:    curl http://localhost:5051/health"
echo "  Stop service:    sudo systemctl stop archimedes"
echo "  Uninstall:       sudo systemctl disable archimedes && sudo rm /etc/systemd/system/archimedes.service"

if [ "$STATUS" != "active" ]; then
    echo ""
    echo "WARNING: Service did not start cleanly. Check logs:"
    echo "  journalctl -u archimedes -n 50 --no-pager"
    exit 1
fi
