# LLM Integration (Ollama)

Archimedes uses a local LLM for intent interpretation and summarization. The system is designed to work **without an LLM** using heuristic fallbacks.

## Important Notes

- **NO AUTO-INSTALL**: Archimedes does NOT automatically install Ollama or download models
- **OPTIONAL**: The LLM is optional; all features work with heuristic fallbacks
- **LOCAL ONLY**: All inference runs locally; no data is sent to external services
- **PRIVACY**: Prompts are sanitized before sending to LLM (passwords, tokens, etc. are redacted)

## Manual Installation

### 1. Install Ollama

Download and install from: https://ollama.ai/download

For Windows, run the installer and follow the prompts.

### 2. Pull a Model

Open a terminal and run:

```bash
ollama pull llama3.2:3b
```

Recommended models (choose based on your hardware):
- `llama3.2:3b` - Good balance of speed and quality (default)
- `llama3.2:1b` - Faster, lower quality
- `llama3.1:8b` - Higher quality, slower, more memory

### 3. Verify Installation

```bash
ollama list
```

Should show your downloaded model.

### 4. Start Ollama (if not running)

```bash
ollama serve
```

By default, Ollama runs at http://127.0.0.1:11434

## Configuration

Set these environment variables to customize LLM behavior:

| Variable | Default | Description |
|----------|---------|-------------|
| `LLM_BASE_URL` | `http://127.0.0.1:11434` | Ollama API URL |
| `LLM_MODEL` | `llama3.2:3b` | Model to use |

Example:
```powershell
$env:LLM_MODEL = "llama3.1:8b"
cd core
dotnet run
```

## API Endpoints

### GET /llm/health

Check if LLM is available.

Response:
```json
{
  "available": true,
  "model": "llama3.2:3b",
  "runtime": "ollama"
}
```

### POST /llm/interpret

Parse user intent from natural language.

Request body: Raw text prompt

Response:
```json
{
  "intent": "TESTSITE_EXPORT",
  "slots": {"url": "http://example.com"},
  "confidence": 0.85,
  "clarificationQuestions": [],
  "isHeuristicFallback": false
}
```

### POST /llm/summarize

Summarize content.

Request body: Raw text content

Response:
```json
{
  "shortSummary": "Summary here.",
  "bulletInsights": ["Point 1", "Point 2"],
  "risks": ["Potential issue"],
  "nextQuestions": ["What else?"],
  "isHeuristicFallback": false
}
```

## Fallback Behavior

When LLM is unavailable or times out (8s), the system falls back to heuristic parsing:

- **Intent detection**: Keyword matching (e.g., "download" → FILE_DOWNLOAD)
- **Summarization**: First sentence extraction

All responses include `isHeuristicFallback: true` when using fallback.

## Security

The LLM adapter:
- Never logs raw prompts (only hashes)
- Sanitizes input (removes passwords, tokens, API keys)
- Never sends sensitive data to external services
- Runs entirely local

## Troubleshooting

**LLM not available?**
1. Check if Ollama is running: `curl http://localhost:11434/api/tags`
2. Check if model is downloaded: `ollama list`
3. Check firewall settings

**Slow responses?**
- Use a smaller model: `llama3.2:1b`
- Reduce input length
- Consider upgrading hardware (GPU recommended for larger models)
