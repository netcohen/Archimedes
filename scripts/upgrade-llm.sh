#!/bin/bash
# =============================================================================
#  upgrade-llm.sh — Upgrade LLM from Llama 3.2 3B → Llama 3.1 8B
#
#  Run on the Ubuntu machine:
#    cd ~/archimedes && git pull && bash scripts/upgrade-llm.sh
#
#  Requirements: ~5 GB free disk space, ~10-20 min download time
# =============================================================================

set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
ok()   { echo -e "${GREEN}[OK]${NC} $*"; }
info() { echo -e "${CYAN}[+]${NC} $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()  { echo -e "${RED}[ERR]${NC} $*" >&2; }

MODEL_DIR="$HOME/.local/share/Archimedes/models"
MODEL_PATH="$MODEL_DIR/llama3.1-8b.gguf"
MODEL_URL="https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF/resolve/main/Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf"
ENV_FILE="/etc/archimedes/environment"

echo ""
echo -e "${CYAN}=== Archimedes LLM Upgrade: 3B → 8B ===${NC}"
echo -e "  Model: Meta-Llama-3.1-8B-Instruct Q4_K_M"
echo -e "  Size:  ~4.9 GB"
echo ""

# ── 1. Check disk space ────────────────────────────────────────────────────
AVAIL_GB=$(df -BG "$HOME" | awk 'NR==2 {print $4}' | tr -d 'G')
if [ "${AVAIL_GB:-0}" -lt 6 ]; then
    err "Not enough disk space. Need at least 6 GB free, have ${AVAIL_GB} GB."
    exit 1
fi
ok "Disk space: ${AVAIL_GB} GB available"

# ── 2. Check RAM ───────────────────────────────────────────────────────────
TOTAL_RAM_KB=$(grep MemTotal /proc/meminfo | awk '{print $2}')
TOTAL_RAM_GB=$((TOTAL_RAM_KB / 1024 / 1024))
if [ "$TOTAL_RAM_GB" -lt 8 ]; then
    warn "RAM: ${TOTAL_RAM_GB} GB — 8B model needs ~5 GB. May be tight but will try."
else
    ok "RAM: ${TOTAL_RAM_GB} GB — sufficient for 8B model"
fi

# ── 3. Download model ──────────────────────────────────────────────────────
mkdir -p "$MODEL_DIR"

if [ -f "$MODEL_PATH" ]; then
    SIZE_GB=$(du -BG "$MODEL_PATH" | cut -f1 | tr -d 'G')
    if [ "${SIZE_GB:-0}" -ge 4 ]; then
        ok "Model already downloaded (${SIZE_GB} GB) — skipping download"
    else
        warn "Existing file too small (${SIZE_GB} GB) — re-downloading"
        rm -f "$MODEL_PATH"
    fi
fi

if [ ! -f "$MODEL_PATH" ]; then
    info "Downloading Llama 3.1 8B (~4.9 GB)..."
    info "This will take 10-20 minutes depending on connection speed."
    echo ""

    wget --progress=bar:force:noscroll \
         --header="User-Agent: Archimedes/1.0" \
         -O "$MODEL_PATH.tmp" \
         "$MODEL_URL" 2>&1 || {
        err "Download failed. Check internet connection and retry."
        rm -f "$MODEL_PATH.tmp"
        exit 1
    }

    mv "$MODEL_PATH.tmp" "$MODEL_PATH"
    SIZE_GB=$(du -BG "$MODEL_PATH" | cut -f1 | tr -d 'G')
    ok "Model downloaded: ${SIZE_GB} GB → $MODEL_PATH"
fi

# ── 4. Update /etc/archimedes/environment ─────────────────────────────────
info "Updating environment config..."

if [ ! -f "$ENV_FILE" ]; then
    err "Environment file not found: $ENV_FILE"
    err "Run bootstrap.sh first."
    exit 1
fi

# Replace ARCHIMEDES_MODEL_PATH line
sudo sed -i "s|^ARCHIMEDES_MODEL_PATH=.*|ARCHIMEDES_MODEL_PATH=$MODEL_PATH|" "$ENV_FILE"

# Verify
if grep -q "llama3.1-8b.gguf" "$ENV_FILE"; then
    ok "Environment updated: ARCHIMEDES_MODEL_PATH → llama3.1-8b.gguf"
else
    err "Failed to update environment file"
    exit 1
fi

# ── 5. Delete old 3B model to free space ──────────────────────────────────
OLD_MODEL="$MODEL_DIR/llama3.2-3b.gguf"
if [ -f "$OLD_MODEL" ]; then
    info "Removing old 3B model to free disk space..."
    rm -f "$OLD_MODEL"
    ok "Old model removed: $OLD_MODEL"
fi

# ── 6. Restart Archimedes service ─────────────────────────────────────────
info "Restarting Archimedes service..."
sudo systemctl restart archimedes
sleep 5

if systemctl is-active --quiet archimedes; then
    ok "Archimedes service restarted"
else
    err "Service failed to start — check: journalctl -u archimedes -n 50"
    exit 1
fi

# ── 7. Wait for LLM to load ────────────────────────────────────────────────
info "Waiting for LLM to load (8B takes ~30 seconds to initialize)..."
for i in $(seq 1 30); do
    RESPONSE=$(curl -sf http://localhost:5051/health 2>/dev/null || echo "{}")
    if echo "$RESPONSE" | grep -q '"Available":true'; then
        ok "LLM loaded and ready!"
        break
    fi
    echo -n "."
    sleep 3
done
echo ""

# ── 8. Final status ────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}=== Upgrade complete ===${NC}"
echo -e "  Model:  ${CYAN}Llama 3.1 8B Instruct Q4_K_M${NC}"
echo -e "  Path:   ${CYAN}$MODEL_PATH${NC}"
echo ""
echo -e "  Check:  ${CYAN}curl -s http://localhost:5051/health | python3 -m json.tool${NC}"
echo -e "  Logs:   ${CYAN}journalctl -u archimedes -f${NC}"
echo ""
