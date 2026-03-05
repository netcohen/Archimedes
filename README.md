# Archimedes

An autonomous AI agent that lives on a dedicated machine, runs 24/7, and improves itself continuously.

---

## What Archimedes Does

- **Executes tasks** — receives natural-language instructions, plans and runs them autonomously (browser automation, HTTP, file operations)
- **Learns from success** — stores successful execution patterns and reuses them (Procedure Memory)
- **Pursues goals** — persistent goals that survive reboots, with adaptive replanning on failure
- **Improves itself** — 24/7 background self-improvement loop: LLM benchmarking, procedure analysis, prompt experiments, dataset collection
- **Acquires tools** — autonomously searches for, evaluates, and installs new capabilities
- **Manages its OS** — schedules Ubuntu updates, reboots in maintenance windows, monitors hardware
- **Migrates between machines** — packages its full state and redeploys on a new machine
- **Communicates with the user** — Hebrew chat UI on screen + Android companion app (Phase 31)

---

## Architecture

```
Archimedes/
├── core/       C# .NET 8 — main agent (tasks, LLM, planning, self-improvement, OS management)
├── net/        Node.js/TypeScript — browser automation (Playwright) + Firebase relay
├── android/    Kotlin — mobile companion app (approvals, status, commands)
└── docs/       Documentation and progress log
```

**Communication:**
- User → Chat UI (`localhost:5051/chat`) or Android app via Firebase
- Core ↔ Net: HTTP (browser steps forwarded to Playwright)
- Core ↔ Android: Firebase Firestore relay (no direct connection needed)

**LLM:** Local, on-device via LLamaSharp — no cloud dependency

---

## Key Capabilities by Phase

| Phase | Capability | Status |
|-------|-----------|--------|
| 0–13  | Encrypted messaging, approval flow, crash recovery, outbox | ✅ |
| 14    | Task execution engine, browser automation, policy engine, LLM | ✅ |
| 15–16 | Self-update framework, storage manager, promote/rollback | ✅ |
| 17    | Real browser automation via Playwright | ✅ |
| 18    | On-device LLM (LLamaSharp, llama3.2-3b) | ✅ |
| 19–20 | Observability/tracing, success criteria engine | ✅ |
| 21    | Procedure Memory — learns and reuses what worked | ✅ |
| 22    | Hebrew Chat UI with live status | ✅ |
| 23    | Linux/Ubuntu port | ✅ |
| 24    | Failure Dialogue — failures become recovery conversations | ✅ |
| 25    | Availability Engine — Shabbat/sleep awareness | ✅ |
| 26    | Goal Layer + adaptive replanning | ✅ |
| 27    | Autonomous Tool Acquisition | ✅ |
| 28    | Machine Migration (Octopus) | ✅ |
| 29    | 24/7 Self-Improvement Engine | ✅ |
| 30    | Ubuntu OS Autonomy (apt, reboot, firewall, hardware) | ✅ |
| 31    | Firebase bidirectional + Android App | 🔄 In progress |

---

## Quick Start (Ubuntu)

```bash
# One-time setup
sudo bash scripts/install-service.sh   # installs systemd service
.\scripts\setup-model.ps1              # downloads LLM model (~2GB)

# Start
systemctl start archimedes

# Open chat
xdg-open http://localhost:5051/chat
```

**Health check:**
```bash
curl http://localhost:5051/health   # → OK
curl http://localhost:5051/os/status  # → OS health, hardware, apt status
```

---

## Development

```bash
cd core && dotnet build       # build
dotnet run                    # run (port 5051)

cd net && npm ci && npm run build
node dist/index.js            # run Net (port 5052)
```

**Run a gate:**
```powershell
.\scripts\phase30-ready-gate.ps1   # 106/106 PASS
```

See [docs/progress.md](docs/progress.md) for full development history.
