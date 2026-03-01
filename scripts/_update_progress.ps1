$progressPath = "C:\Users\netanel\Desktop\Archimedes\docs\progress.md"

$phase18 = @"

---

## Phase 18 - LLamaSharp: On-Device LLM

**Status:** Complete

**Problem solved:**
LLMAdapter previously called Ollama via HTTP (external service, separate install required).
After Phase 18, the LLM runs directly inside Core process via LLamaSharp - zero external dependencies.

**Changes:**

| File | Change |
|------|--------|
| core/Archimedes.Core.csproj | Added LLamaSharp 0.17.0 + LLamaSharp.Backend.Cpu |
| core/LLMAdapter.cs | Replaced Ollama HTTP with LLamaSharp StatelessExecutor + lazy model loading |
| core/Program.cs | Removed HttpClient from LLMAdapter ctor, added Dispose on shutdown |
| scripts/setup-model.ps1 | NEW - downloads llama3.2-3b.gguf (~2GB) one-time setup |
| scripts/phase18-llm.ps1 | NEW - 8-test gate script (all PASS) |

**Architecture after Phase 18:**

    Core (C#) - single process
    LLMAdapter.Interpret() --> LLamaSharp StatelessExecutor --> llama3.2-3b.gguf
    No Ollama service, no HTTP calls, no external dependencies

**Gate results (8/8 PASS):**
1. GGUF model file exists (1.88 GB)
2. dotnet build - 0 errors
3. /llm/health - available=true, runtime=llamasharp
4. /llm/interpret - returns valid intent JSON
5. 'monitor testsite dashboard' -> TESTSITE_MONITOR
6. 'download file from url' -> FILE_DOWNLOAD
7. /llm/summarize - returns non-empty shortSummary
8. Heuristic fallback - works when model unavailable, no crash

**Portability:**
- Copy Archimedes folder to new machine
- Run scripts\setup-model.ps1 (downloads model once, ~2GB)
- No Ollama installation required
- GPU support: set LLM_GPU_LAYERS=-1 env var

**How to run:**
- One-time: .\scripts\setup-model.ps1
- Gate: .\scripts\phase18-llm.ps1
"@

Add-Content -Path $progressPath -Value $phase18
Write-Host "progress.md updated"
