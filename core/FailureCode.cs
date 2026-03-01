namespace Archimedes.Core;

/// <summary>
/// Typed failure codes for Observability (Phase 19).
/// Each code maps to a specific failure domain — no more generic "something went wrong".
/// </summary>
public enum FailureCode
{
    None = 0,

    // ── LLM failures ──────────────────────────────────────────────────────────
    LLM_TIMEOUT           = 100,
    LLM_INFERENCE_ERROR   = 101,
    LLM_MODEL_NOT_LOADED  = 102,

    // ── Intent / Planning failures ────────────────────────────────────────────
    INTENT_AMBIGUOUS      = 200,
    INTENT_UNKNOWN        = 201,
    PLAN_INVALID          = 202,
    PLAN_MISSING_PROMPT   = 203,
    PLAN_GENERATION_FAILED = 204,

    // ── Step execution failures ───────────────────────────────────────────────
    STEP_EXECUTION_FAILED = 300,
    BROWSER_NOT_FOUND     = 301,
    BROWSER_STEP_FAILED   = 302,
    HTTP_STEP_FAILED      = 303,

    // ── Policy failures ───────────────────────────────────────────────────────
    POLICY_DENIED         = 400,

    // ── Approval failures ─────────────────────────────────────────────────────
    APPROVAL_TIMEOUT      = 500,
    APPROVAL_REJECTED     = 501,

    // ── Infrastructure failures ───────────────────────────────────────────────
    TASK_WATCHDOG_TIMEOUT = 600,
    PERSISTENCE_ERROR     = 601,
    NET_UNAVAILABLE       = 602,
    MISSING_PROMPT        = 603,
}
