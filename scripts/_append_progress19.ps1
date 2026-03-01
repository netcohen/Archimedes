$progressPath = "C:\Users\netanel\Desktop\Archimedes\docs\progress.md"

$content = @"

---

## Roadmap Revision - Post Phase 18

**Date:** 2026-03-01

### Current State

| Phase | Name | Status | Note |
|-------|------|--------|------|
| 16 | Base Hardening | Complete | PromotionManager, Audit, SmartScheduler |
| 17 | Browser Automation | Complete | Playwright via Net, 7/7 gate |
| 18 | On-Device LLM (LLamaSharp) | Complete | Real inference confirmed, 8/8 gate + master test 17/17 |

**Deviation from original plan:**
Phase 18 in the redesigned roadmap was Observability.
We implemented LLamaSharp instead (needed as prerequisite for Phase 20 Success Criteria Engine).
Observability becomes Phase 19 to close the gap before building higher layers.

**LLM fix note (post-gate):**
Phase 18 gate originally passed 8/8 but with isHeuristicFallback=True on all tests.
Two bugs fixed: double BOS token + Temperature via DefaultSamplingPipeline (not directly on InferenceParams).
After fix: master test 17/17 PASS, all LLM tests heuristic=False, avg 5s inference, +47MB memory delta (stable).

---

## Roadmap - Phase 19 to Final

### Vision
Archimedes: an autonomous agent that takes natural-language tasks, executes them via browser
and integrations, knows when it succeeded, recovers intelligently from failure, remembers what
worked, and can move between machines while continuing to develop itself.

---

### Phase 19 - Observability

**What:** Every step leaves a structured trace. The system knows exactly what it did and why it failed.

- Structured step-level trace with correlation IDs per task run
- Typed failure codes (not generic "failed": timeout / selector-not-found / http-error / parse-error)
- Execution snapshots persisted to disk (survive Core crash)
- Trace queryable per task via /task/{id}/trace API

**Why first:** Success Criteria Engine needs traces to evaluate outcomes.
Failure Dialogue needs execution graphs to explain failures. Without this, both are blind.

---

### Phase 20 - Success Criteria Engine

**What:** Every task defines what success looks like before it starts.

- LLM extracts success criteria from the user prompt
- Internal DSL to represent criteria (e.g. rowCount > 0 / price < threshold / element visible)
- Deterministic Evaluator checks criteria against trace at end of run
- Automatic retry rules based on failure type and criteria

---

### Phase 21 - Procedure Memory

**What:** What worked gets remembered and reused.

- Successful execution graphs stored as graph: nodes=steps, edges=conditions, metadata=success-rate/version/date
- LLM finds semantic match to existing procedures for new tasks
- Detect when a procedure has become stale (site changed, selector broken)
- Partial subgraph reuse across tasks

---

### Phase 22 - Failure Dialogue

**What:** A failure becomes a conversation, not a dead end.

- Reads the execution graph (Phase 19) to reconstruct what was attempted
- Explains in natural language: what was tried and why it failed
- Asks exactly one focused question
- User answer updates the procedure and triggers retry

---

### Phase 23 - Availability Engine

**What:** The system learns when the user is available and acts accordingly.

- Learns availability patterns from non-response (Shabbat, sleep hours)
- Sensitive information waits for an availability window before acting
- Firebase data deleted immediately after read
- Override layer: critical situations always notify regardless of availability

---

### Phase 24 - Goal Layer + Adaptive Planner

**What:** Moves from executing tasks to pursuing goals.

- Goal abstraction above task level ("maintain price below X" not "check price now")
- When a step fails: finds alternative path toward the goal
- ACTIVE / MONITORING / IDLE state management per goal
- Smart resource allocation across concurrent goals

---

### Phase 25 - Integrations (WhatsApp, Sheets, Calendar, Email)

**What:** The hands of the system - real-world integrations.

- WhatsApp Desktop automation
- Google Sheets read/write
- Calendar event management
- Email read and send

Only here - because by Phase 25 the system has judgment, memory, and failure recovery.

---

### Phase 26 - Machine Migration (Octopus)

**What:** The system can move itself between machines safely.

- Check target disk space before starting
- Suspend or finish tasks by priority before migration
- Self-package + continuation log (exact state snapshot)
- Self-deploy on new machine
- Resume from exact stopping point

---

### Phase 27 - App Self-Development

**What:** Archimedes can extend its own Android app.

- Generates APK updates
- Installs via ADB / developer mode
- App UI grows alongside system capabilities
"@

Add-Content -Path $progressPath -Value $content
Write-Host "progress.md updated"
