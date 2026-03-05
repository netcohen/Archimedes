#!/usr/bin/env bash
# install.sh — Archimedes full-stack installer for Ubuntu 24.04
#
# Installs and starts:
#   - Archimedes Core  (.NET 8) on port 5051
#   - Archimedes Net   (Node.js) on port 5052
#
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
CORE_SERVICE=/etc/systemd/system/archimedes-core.service
NET_SERVICE=/etc/systemd/system/archimedes-net.service
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

# ─── [4] Chromium (for browser automation) ───────────────────────────────────
info "[4] Checking Chromium..."
if ! command -v chromium-browser &>/dev/null && ! command -v chromium &>/dev/null; then
    info "    Installing Chromium..."
    apt-get install -y --no-install-recommends chromium-browser
else
    info "    Chromium already installed"
fi

# ─── [5] ADB (for Android OTA updates via WiFi — Phase 32) ──────────────────
info "[5] Checking ADB (Android Debug Bridge)..."
if ! command -v adb &>/dev/null; then
    info "    Installing android-tools-adb..."
    apt-get install -y --no-install-recommends android-tools-adb
else
    info "    ADB already installed: $(adb --version | head -1)"
fi

# ─── [6] Service user ────────────────────────────────────────────────────────
info "[6] Ensuring service user '${SERVICE_USER}'..."
if ! id "${SERVICE_USER}" &>/dev/null; then
    useradd --system --no-create-home --shell /usr/sbin/nologin "${SERVICE_USER}"
    mkdir -p "/home/${SERVICE_USER}/.local/share/Archimedes"
    mkdir -p "/home/${SERVICE_USER}/.archimedes/models"
    chown -R "${SERVICE_USER}:${SERVICE_USER}" "/home/${SERVICE_USER}"
    usermod -d "/home/${SERVICE_USER}" "${SERVICE_USER}"
    info "    Created user '${SERVICE_USER}'"
else
    info "    User '${SERVICE_USER}' already exists"
fi
# Ensure data dirs exist regardless
mkdir -p "/home/${SERVICE_USER}/.local/share/Archimedes"
mkdir -p "/home/${SERVICE_USER}/.archimedes/models"
chown -R "${SERVICE_USER}:${SERVICE_USER}" "/home/${SERVICE_USER}"

# ─── [7] Build & publish Core ────────────────────────────────────────────────
info "[7] Building Archimedes Core..."
CORE_DIR="${REPO_ROOT}/core"
CORE_PUBLISH="${INSTALL_DIR}/core"
mkdir -p "${CORE_PUBLISH}"
dotnet publish "${CORE_DIR}/Archimedes.Core.csproj" \
    -c Release \
    -o "${CORE_PUBLISH}" \
    --nologo -v quiet
chown -R "${SERVICE_USER}:${SERVICE_USER}" "${CORE_PUBLISH}"
info "    Published to ${CORE_PUBLISH}"

# ─── [8] Build & install Net ─────────────────────────────────────────────────
info "[8] Building Archimedes Net..."
NET_DIR="${REPO_ROOT}/net"
NET_PUBLISH="${INSTALL_DIR}/net"

# Install npm dependencies and build TypeScript
cd "${NET_DIR}"
npm ci --prefer-offline --silent || npm install --silent
npm run build

# Copy compiled output to install dir
mkdir -p "${NET_PUBLISH}"
cp -r "${NET_DIR}/dist" "${NET_PUBLISH}/dist"
cp "${NET_DIR}/package.json" "${NET_PUBLISH}/"
cp "${NET_DIR}/package-lock.json" "${NET_PUBLISH}/" 2>/dev/null || true

# Install production dependencies in publish dir
cd "${NET_PUBLISH}"
npm ci --omit=dev --prefer-offline --silent 2>/dev/null || \
    npm install --omit=dev --silent

chown -R "${SERVICE_USER}:${SERVICE_USER}" "${NET_PUBLISH}"
info "    Net service installed at ${NET_PUBLISH}"
cd "${REPO_ROOT}"

# ─── [9] Copy scripts ────────────────────────────────────────────────────────
info "[9] Installing scripts..."
SCRIPTS_PUBLISH="${INSTALL_DIR}/scripts"
mkdir -p "${SCRIPTS_PUBLISH}"
cp "${REPO_ROOT}/scripts/update-android.sh" "${SCRIPTS_PUBLISH}/" 2>/dev/null || true
cp "${REPO_ROOT}/scripts/setup-model.ps1"   "${SCRIPTS_PUBLISH}/" 2>/dev/null || true
cp "${REPO_ROOT}/scripts/env.template"      "${SCRIPTS_PUBLISH}/" 2>/dev/null || true
chmod +x "${SCRIPTS_PUBLISH}/update-android.sh" 2>/dev/null || true
chown -R "${SERVICE_USER}:${SERVICE_USER}" "${SCRIPTS_PUBLISH}"

# ─── [10] Environment config dir ──────────────────────────────────────────────
info "[10] Setting up /etc/archimedes config dir..."
mkdir -p /etc/archimedes
if [ ! -f /etc/archimedes/core.env ]; then
    cat > /etc/archimedes/core.env <<EOF
# Archimedes Core environment — edit and uncomment as needed
# See ${SCRIPTS_PUBLISH}/env.template for full reference
DOTNET_ENVIRONMENT=Production
ASPNETCORE_URLS=http://localhost:5051
ARCHIMEDES_NET_URL=http://localhost:5052
ARCHIMEDES_MODEL_PATH=/home/${SERVICE_USER}/.archimedes/models/llama3.2-3b.gguf
LLM_GPU_LAYERS=0
# GOOGLE_APPLICATION_CREDENTIALS=/etc/archimedes/firebase-credentials.json
EOF
    chmod 640 /etc/archimedes/core.env
    chown root:${SERVICE_USER} /etc/archimedes/core.env
    info "    Created /etc/archimedes/core.env (review and edit as needed)"
else
    info "    /etc/archimedes/core.env already exists — skipping"
fi

if [ ! -f /etc/archimedes/net.env ]; then
    cat > /etc/archimedes/net.env <<EOF
# Archimedes Net environment — edit and uncomment as needed
PORT=5052
CORE_URL=http://localhost:5051
NODE_ENV=production
LOG_LEVEL=info
# GOOGLE_APPLICATION_CREDENTIALS=/etc/archimedes/firebase-credentials.json
# FIREBASE_PROJECT_ID=archimedes-c76c3
EOF
    chmod 640 /etc/archimedes/net.env
    chown root:${SERVICE_USER} /etc/archimedes/net.env
    info "    Created /etc/archimedes/net.env (review and edit as needed)"
else
    info "    /etc/archimedes/net.env already exists — skipping"
fi

# ─── [11] Install systemd services ───────────────────────────────────────────
info "[11] Installing systemd services..."

# Core service
cp "${REPO_ROOT}/systemd/archimedes-core.service" "${CORE_SERVICE}"
sed -i "s|/home/archimedes|/home/${SERVICE_USER}|g" "${CORE_SERVICE}"
# Activate EnvironmentFile
sed -i 's|^# EnvironmentFile=/etc/archimedes/core.env|EnvironmentFile=/etc/archimedes/core.env|g' "${CORE_SERVICE}"
info "    Core service installed at ${CORE_SERVICE}"

# Net service
cp "${REPO_ROOT}/systemd/archimedes-net.service" "${NET_SERVICE}"
sed -i "s|/home/archimedes|/home/${SERVICE_USER}|g" "${NET_SERVICE}"
# Activate EnvironmentFile
sed -i 's|^# EnvironmentFile=/etc/archimedes/net.env|EnvironmentFile=/etc/archimedes/net.env|g' "${NET_SERVICE}"
info "    Net service installed at ${NET_SERVICE}"

systemctl daemon-reload

# ─── [12] Enable & start services ────────────────────────────────────────────
info "[12] Enabling and starting services..."

# Start Net first (Core depends on it)
systemctl enable archimedes-net
systemctl restart archimedes-net
sleep 2

if systemctl is-active --quiet archimedes-net; then
    info "    archimedes-net is RUNNING"
else
    warn "    archimedes-net did not start — check: journalctl -u archimedes-net -n 50"
fi

# Start Core
systemctl enable archimedes-core
systemctl restart archimedes-core
sleep 3

if systemctl is-active --quiet archimedes-core; then
    info "    archimedes-core is RUNNING"
else
    warn "    archimedes-core did not start — check: journalctl -u archimedes-core -n 50"
fi

# ─── [13] LLM model check ────────────────────────────────────────────────────
info "[13] Checking LLM model..."
MODEL_PATH="/home/${SERVICE_USER}/.archimedes/models/llama3.2-3b.gguf"
if [ -f "${MODEL_PATH}" ]; then
    MODEL_MB=$(du -m "${MODEL_PATH}" | cut -f1)
    info "    Model found: ${MODEL_PATH} (${MODEL_MB}MB)"
else
    warn "    LLM model NOT found at ${MODEL_PATH}"
    warn "    To download (~2GB), run:"
    warn "      sudo -u ${SERVICE_USER} pwsh ${SCRIPTS_PUBLISH}/setup-model.ps1"
    warn "    LLM features (interpret, summarize) will use heuristic fallback until model is present."
fi

# ─── Summary ─────────────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}================================================${NC}"
echo -e "${GREEN}  Archimedes installation complete              ${NC}"
echo -e "${GREEN}================================================${NC}"
echo ""
echo "  Core    : http://localhost:5051/health"
echo "  Net     : http://localhost:5052/health"
echo "  Logs    : journalctl -u archimedes-core -f"
echo "            journalctl -u archimedes-net -f"
echo "  Status  : systemctl status archimedes-core archimedes-net"
echo "  Config  : /etc/archimedes/core.env  /etc/archimedes/net.env"
echo ""
echo -e "${YELLOW}  Next steps:${NC}"
echo "  1. Review /etc/archimedes/core.env and net.env"
if [ ! -f "${MODEL_PATH}" ]; then
echo "  2. Download LLM model:"
echo "       sudo -u ${SERVICE_USER} pwsh ${SCRIPTS_PUBLISH}/setup-model.ps1"
fi
echo "  3. (Optional) Place Firebase credentials:"
echo "       /etc/archimedes/firebase-credentials.json"
echo "       Then uncomment GOOGLE_APPLICATION_CREDENTIALS in net.env"
echo "  4. ADB WiFi (for Android OTA): pair phone once via Developer Options"
echo ""
