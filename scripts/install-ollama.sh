#!/bin/bash
# =============================================================================
#  install-ollama.sh — Install Ollama + pull LLM model
#
#  Run on the Ubuntu machine (first time or after engine switch):
#    cd ~/archimedes && git pull && bash scripts/install-ollama.sh
#
#  What this does:
#    1. Installs Ollama (if not already installed)
#    2. Enables + starts the ollama systemd service
#    3. Pulls the configured model (default: llama3.1:8b)
#    4. Updates /etc/archimedes/environment with ARCHIMEDES_OLLAMA_MODEL
#    5. Restarts the Archimedes service
# =============================================================================

set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
ok()   { echo -e "${GREEN}[OK]${NC} $*"; }
info() { echo -e "${CYAN}[+]${NC} $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()  { echo -e "${RED}[ERR]${NC} $*" >&2; }

OLLAMA_MODEL="${ARCHIMEDES_OLLAMA_MODEL:-llama3.1:8b}"
ENV_FILE="/etc/archimedes/environment"

echo ""
echo -e "${CYAN}=== Archimedes — Install Ollama + ${OLLAMA_MODEL} ===${NC}"
echo ""

# ── 1. Install Ollama ─────────────────────────────────────────────────────
if command -v ollama &>/dev/null; then
    ok "Ollama already installed: $(ollama --version 2>/dev/null | head -1)"
else
    info "Installing Ollama..."
    curl -fsSL https://ollama.ai/install.sh | sh
    ok "Ollama installed"
fi

# ── 2. Enable + start Ollama service ─────────────────────────────────────
info "Enabling Ollama service..."
sudo systemctl enable ollama 2>/dev/null || true
sudo systemctl start  ollama 2>/dev/null || true
sleep 3

if systemctl is-active --quiet ollama; then
    ok "Ollama service: running"
else
    err "Ollama service failed to start — check: journalctl -u ollama -n 30"
    exit 1
fi

# ── 3. Pull model ─────────────────────────────────────────────────────────
info "Pulling model: ${OLLAMA_MODEL} (this may take 10-20 minutes on first run)..."
ollama pull "${OLLAMA_MODEL}"
ok "Model ready: ${OLLAMA_MODEL}"

# ── 4. Test model ─────────────────────────────────────────────────────────
info "Testing model..."
RESP=$(ollama run "${OLLAMA_MODEL}" "reply with exactly: ok" 2>/dev/null || echo "")
if [ -n "$RESP" ]; then
    ok "Model test passed: ${RESP:0:40}"
else
    warn "Model test returned empty — may still be initializing"
fi

# ── 5. Update environment file ────────────────────────────────────────────
if [ -f "$ENV_FILE" ]; then
    info "Updating /etc/archimedes/environment..."

    # Remove old LlamaSharp model path (no longer needed)
    sudo sed -i '/^ARCHIMEDES_MODEL_PATH=/d' "$ENV_FILE"

    # Add/update Ollama settings
    if grep -q "^ARCHIMEDES_OLLAMA_MODEL=" "$ENV_FILE"; then
        sudo sed -i "s|^ARCHIMEDES_OLLAMA_MODEL=.*|ARCHIMEDES_OLLAMA_MODEL=${OLLAMA_MODEL}|" "$ENV_FILE"
    else
        echo "ARCHIMEDES_OLLAMA_MODEL=${OLLAMA_MODEL}" | sudo tee -a "$ENV_FILE" > /dev/null
    fi

    if grep -q "^ARCHIMEDES_OLLAMA_URL=" "$ENV_FILE"; then
        sudo sed -i "s|^ARCHIMEDES_OLLAMA_URL=.*|ARCHIMEDES_OLLAMA_URL=http://localhost:11434|" "$ENV_FILE"
    else
        echo "ARCHIMEDES_OLLAMA_URL=http://localhost:11434" | sudo tee -a "$ENV_FILE" > /dev/null
    fi

    ok "Environment updated"
fi

# ── 6. Remove old .gguf model files (free disk space) ────────────────────
MODEL_DIR="$HOME/.local/share/Archimedes/models"
if ls "$MODEL_DIR"/*.gguf 2>/dev/null | grep -q .; then
    info "Removing old .gguf model files to free disk space..."
    rm -f "$MODEL_DIR"/*.gguf
    ok "Old model files removed"
fi

# ── 7. Restart Archimedes service ─────────────────────────────────────────
info "Restarting Archimedes service..."
sudo systemctl restart archimedes
sleep 5

if systemctl is-active --quiet archimedes; then
    ok "Archimedes service restarted"
else
    err "Service failed — check: journalctl -u archimedes -n 50"
    exit 1
fi

# ── 8. Health check ───────────────────────────────────────────────────────
info "Checking LLM health..."
sleep 3
HEALTH=$(curl -sf http://localhost:5051/llm/health 2>/dev/null || echo "{}")
if echo "$HEALTH" | grep -q '"Available":true'; then
    ok "LLM health: Available=true"
else
    warn "LLM not yet available — may take a few more seconds to connect to Ollama"
    warn "Check: curl http://localhost:5051/llm/health"
fi

echo ""
echo -e "${GREEN}=== Ollama installation complete ===${NC}"
echo -e "  Model:   ${CYAN}${OLLAMA_MODEL}${NC}"
echo -e "  Ollama:  ${CYAN}http://localhost:11434${NC}"
echo -e "  Chat:    ${CYAN}http://localhost:5051/dashboard${NC}"
echo ""
echo -e "  Manage models: ${CYAN}ollama list${NC}"
echo -e "  Test directly: ${CYAN}ollama run ${OLLAMA_MODEL}${NC}"
echo ""
