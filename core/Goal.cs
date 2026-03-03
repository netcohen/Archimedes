namespace Archimedes.Core;

// ── Enums ──────────────────────────────────────────────────────────────────────

public enum GoalState
{
    ACTIVE,      // Actively pursuing — has a running task or will spawn one next tick
    MONITORING,  // Periodically checking conditions (between task runs, waiting for next window)
    IDLE,        // Paused by user
    COMPLETED,   // Success criteria met
    FAILED       // All retries and alternatives exhausted
}

public enum GoalType
{
    PERSISTENT,  // Runs continuously until manually stopped (e.g. "monitor price")
    CONDITION,   // Runs until a condition is met (e.g. "wait for file to appear")
    ONE_TIME     // Runs once with adaptive retry (e.g. "export that report")
}

// ── Goal Memory ────────────────────────────────────────────────────────────────

public class GoalMemoryEntry
{
    public DateTime TimestampUtc   { get; set; } = DateTime.UtcNow;
    public string   TaskId         { get; set; } = "";
    public bool     Success        { get; set; }
    public string?  Summary        { get; set; }
    public string?  ObservedValue  { get; set; }  // e.g. price, status, count
}

/// <summary>
/// Phase 26 — accumulated context across all task runs for a goal.
/// This is what makes a Goal smarter than a plain recurring Task:
/// it remembers what it observed and uses that to enrich future prompts.
/// </summary>
public class GoalMemory
{
    public List<GoalMemoryEntry> History          { get; set; } = new();
    public string?               LastObservedValue { get; set; }
    public Dictionary<string, string> Context     { get; set; } = new();

    public void Record(string taskId, bool success, string? summary, string? observedValue = null)
    {
        History.Add(new GoalMemoryEntry
        {
            TaskId        = taskId,
            Success       = success,
            Summary       = summary,
            ObservedValue = observedValue
        });
        if (History.Count > 100)
            History = History.TakeLast(100).ToList();
        if (observedValue != null)
            LastObservedValue = observedValue;
    }

    public int ConsecutiveFailures =>
        History.Count == 0 ? 0 :
        History.AsEnumerable().Reverse().TakeWhile(e => !e.Success).Count();

    public int TotalRuns    => History.Count;
    public int SuccessCount => History.Count(e => e.Success);
}

// ── Goal Checkpoint ────────────────────────────────────────────────────────────

/// <summary>
/// Snapshot of goal state for Phase 28 (Machine Migration).
/// Allows resuming a goal on a new machine from the exact stopping point.
/// </summary>
public class GoalCheckpoint
{
    public DateTime CreatedAtUtc  { get; set; } = DateTime.UtcNow;
    public string   StateSnapshot { get; set; } = "";   // JSON of key fields
    public string?  LastTaskId    { get; set; }
    public string?  ResumeHint    { get; set; }         // human-readable hint
}

// ── Goal Model ─────────────────────────────────────────────────────────────────

public class Goal
{
    public string    GoalId      { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string    Title       { get; set; } = "";
    public string    Description { get; set; } = "";
    public GoalState State       { get; set; } = GoalState.ACTIVE;
    public GoalType  Type        { get; set; } = GoalType.PERSISTENT;

    /// <summary>LLM intent mapped from the user prompt (e.g. TESTSITE_MONITOR).</summary>
    public string Intent { get; set; } = "";

    /// <summary>Runtime parameters: url, threshold, schedule interval, etc.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>Natural-language success condition (null = run until manually stopped).</summary>
    public string? SuccessCondition { get; set; }

    // ── Task linkage ──────────────────────────────────────────────────────────
    public List<string> TaskIds       { get; set; } = new();
    public string?      CurrentTaskId { get; set; }

    // ── Retry / adaptive replanning ──────────────────────────────────────────
    public int RetryCount          { get; set; }
    public int MaxRetries          { get; set; } = 3;
    public int AlternativeAttempts { get; set; }   // resets each retry cycle

    // ── Scheduling (for MONITORING goals) ────────────────────────────────────
    public int       CheckIntervalMinutes { get; set; } = 30;
    public DateTime? NextCheckUtc         { get; set; }

    // ── Timestamps ───────────────────────────────────────────────────────────
    public DateTime  CreatedAtUtc  { get; set; } = DateTime.UtcNow;
    public DateTime  UpdatedAtUtc  { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    public string? FailureReason { get; set; }

    // ── Phase 26: Goal memory ─────────────────────────────────────────────────
    public GoalMemory Memory { get; set; } = new();

    // ── Phase 28: Checkpoint support ─────────────────────────────────────────
    public GoalCheckpoint? LastCheckpoint { get; set; }

    /// <summary>0.0–1.0 progress estimate based on success ratio.</summary>
    public double Progress =>
        Memory.TotalRuns == 0 ? 0.0 :
        Math.Min(1.0, (double)Memory.SuccessCount / Math.Max(1, Memory.TotalRuns));
}

// ── API request model ──────────────────────────────────────────────────────────

public class CreateGoalRequest
{
    public string  Title                { get; set; } = "";
    public string? Description          { get; set; }
    public string? Type                 { get; set; }  // PERSISTENT | CONDITION | ONE_TIME
    public string? SuccessCondition     { get; set; }
    public int?    MaxRetries           { get; set; }
    public int?    CheckIntervalMinutes { get; set; }
    public string  UserPrompt           { get; set; } = "";
}
