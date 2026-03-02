#!/usr/bin/env bash
# install.sh — Archimedes Core installer for Ubuntu 24.04
# Usage: sudo bash scripts/install.sh
# Idempotent: safe to run more than once.

set -euo pipefail

# ─── Colour helpers ──────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
info()  { echo -e "${GREEN}[INFO]${NC}  $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
error() { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }

# ─── Root check ──────────────────────────────────────────────────────────────
[[ $EUID -eq 0 ]] || error "Run as root: sudo bash $0"

# ─── Config ──────────────────────────────────────────────────────────────────
INSTALL_DIR=/opt/archimedes
SERVICE_USER=archimedes
SERVICE_FILE=/etc/systemd/system/archimedes-core.service
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

info "Repo root: ${REPO_ROOT}"
info "Install dir: ${INSTALL_DIR}"

# ─── [1] .NET 8 SDK ──────────────────────────────────────────────────────────
info "[1] Checking .NET 8 SDK..."
if ! command -v dotnet &>/dev/null || ! dotnet --list-sdks | grep -q '^8\.'; then
    info "    Installing .NET 8 SDK..."
    apt-get update -qq
    apt-get install -y --no-install-recommends dotnet-sdk-8.0
else
    info "    .NET 8 already installed: $(dotnet --version)"
fi

# ─── [2] PowerShell (pwsh) ───────────────────────────────────────────────────
info "[2] Checking PowerShell (pwsh)..."
if ! command -v pwsh &>/dev/null; then
    info "    Installing PowerShell..."
    apt-get update -qq
    apt-get install -y --no-install-recommends \
        apt-transport-https software-properties-common wget gnupg
    # Microsoft repo for PS 7
    wget -q "https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb" \
         -O /tmp/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb
    apt-get update -qq
    apt-get install -y --no-install-recommends powershell
    rm -f /tmp/packages-microsoft-prod.deb
else
    info "    pwsh already installed: $(pwsh --version)"
fi

# ─── [3] Node.js 20 (for Net layer) ─────────────────────────────────────────
info "[3] Checking Node.js..."
if ! command -v node &>/dev/null || ! node --version | grep -q '^v2[0-9]\.'; then
    info "    Installing Node.js 20 LTS..."
    curl -fsSL https://deb.nodesource.com/setup_20.x | bash -
    apt-get install -y --no-install-recommends nodejs
else
    info "    Node.js already installed: $(node --version)"
fi

# ─── [4] Chromium (for Net/browser layer) ───────────────────────────────────
info "[4] Checking Chromium..."
if ! command -v chromium-browser &>/dev/null && ! command -v chromium &>/dev/null; then
    info "    Installing Chromium..."
    apt-get install -y --no-install-recommends chromium-browser
else
    info "    Chromium already installed"
fi

# ─── [5] Service user ────────────────────────────────────────────────────────
info "[5] Ensuring service user '${SERVICE_USER}'..."
if ! id "${SERVICE_USER}" &>/dev/null; then
    useradd --system --no-create-home --shell /usr/sbin/nologin "${SERVICE_USER}"
    # Create home dir for data storage (LocalApplicationData)
    mkdir -p "/home/${SERVICE_USER}/.local/share/Archimedes"
    chown -R "${SERVICE_USER}:${SERVICE_USER}" "/home/${SERVICE_USER}"
    usermod -d "/home/${SERVICE_USER}" "${SERVICE_USER}"
    info "    Created user '${SERVICE_USER}'"
else
    info "    User '${SERVICE_USER}' already exists"
fi
# Ensure data dir exists regardless
mkdir -p "/home/${SERVICE_USER}/.local/share/Archimedes"
chown -R "${SERVICE_USER}:${SERVICE_USER}" "/home/${SERVICE_USER}"

# ─── [6] Build Core ──────────────────────────────────────────────────────────
info "[6] Building Archimedes.Core..."
CORE_DIR="${REPO_ROOT}/core"
PUBLISH_DIR="${INSTALL_DIR}/core"
mkdir -p "${PUBLISH_DIR}"
dotnet publish "${CORE_DIR}/Archimedes.Core.csproj" \
    -c Release \
    -o "${PUBLISH_DIR}" \
    --nologo -v quiet
info "    Published to ${PUBLISH_DIR}"

# ─── [7] Install systemd service ────────────────────────────────────────────
info "[7] Installing systemd service..."
cp "${REPO_ROOT}/systemd/archimedes-core.service" "${SERVICE_FILE}"
# Patch paths in service file to match actual home dir
sed -i "s|/home/archimedes|/home/${SERVICE_USER}|g" "${SERVICE_FILE}"
systemctl daemon-reload
info "    Service file installed at ${SERVICE_FILE}"

# ─── [8] Enable & start ──────────────────────────────────────────────────────
info "[8] Enabling and starting archimedes-core..."
systemctl enable archimedes-core
systemctl restart archimedes-core
sleep 2

if systemctl is-active --quiet archimedes-core; then
    info "    Service is RUNNING"
else
    warn "    Service did not start — check: journalctl -u archimedes-core -n 50"
fi

# ─── Summary ─────────────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}=====================================${NC}"
echo -e "${GREEN}  Archimedes Core installation done  ${NC}"
echo -e "${GREEN}=====================================${NC}"
echo ""
echo "  Endpoint : http://localhost:5051/health"
echo "  Logs     : journalctl -u archimedes-core -f"
echo "  Status   : systemctl status archimedes-core"
echo "  Stop     : systemctl stop archimedes-core"
echo ""
