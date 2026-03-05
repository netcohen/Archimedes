using System.Text.Json.Serialization;

namespace Archimedes.Core;

// ---------------------------------------------------------------------------
// Phase 29 – App Self-Development / Autonomous Self-Improvement
// Data models for the engine that runs 24/7 improving Archimedes.
// ---------------------------------------------------------------------------

/// <summary>
/// Type of self-improvement work Archimedes can perform autonomously.
/// There is always work to do — no idle state.
/// </summary>
public enum SelfWorkType
{
    ANALYZE_TRACES,       // Statistical analysis of execution traces
    ANALYZE_PROCEDURES,   // Find and improve low-success-rate procedures
    BENCHMARK_LLM,        // Measure LLM accuracy / speed with test prompts
    RESEARCH_WEB,         // Research a technical topic via LLM knowledge
    COLLECT_DATASET,      // Extract successful task examples for future training
    SELF_TEST,            // Verify system health via endpoint checks
    ANALYZE_TOOL_USAGE,   // Find unused / underused acquired tools
    ANALYZE_RESOURCES,    // Measure CPU / RAM patterns and efficiency
    EXPERIMENT_PROMPT,    // Try alternative prompt templates for common intents
    PATCH_CORE_CODE,      // Generate + sandbox-test + apply code improvement
    ANALYZE_ANDROID_APP   // Phase 32+: check Android app health, OTA status, FCM registration
}

/// <summary>Status of a single self-improvement work item.</summary>
public enum SelfWorkStatus
{
    PENDING,
    IN_PROGRESS,
    COMPLETED,
    FAILED,
    PAUSED,       // Preempted by user task — checkpoint saved
    CANCELLED
}

/// <summary>Overall state of the self-improvement engine.</summary>
public enum SelfEngineState
{
    STARTING,
    RUNNING,      // Actively executing a work item
    IDLE,         // Between items (very brief — new work generated immediately)
    THROTTLED,    // CPU high — adding extra delay
    PAUSED,       // User task preempted self-improvement
    STOPPED
}

/// <summary>A single unit of self-improvement work.</summary>
public class SelfWorkItem
{
    public string Id          { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public SelfWorkType   Type        { get; set; }
    public string         Description { get; set; } = "";
    public int            Priority    { get; set; } = 5;   // 1 (low) – 10 (high)
    public string?        TargetId    { get; set; }        // procedureId / toolId / etc.
    public Dictionary<string, string> Context { get; set; } = new();
    public DateTime       CreatedAt   { get; set; } = DateTime.UtcNow;
    public SelfWorkStatus Status      { get; set; } = SelfWorkStatus.PENDING;
}

/// <summary>Result of executing one work item.</summary>
public class SelfWorkResult
{
    public string       WorkItemId        { get; set; } = "";
    public SelfWorkType Type              { get; set; }
    public string       WorkDescription   { get; set; } = "";
    public bool         Success           { get; set; }
    public string       Summary           { get; set; } = "";
    public string?      Insight           { get; set; }   // Key finding
    public string?      ErrorMessage      { get; set; }
    public DateTime     StartedAt         { get; set; }
    public DateTime     CompletedAt       { get; set; }
    public bool         CodeChangeApplied { get; set; }

    [JsonIgnore]
    public TimeSpan Duration => CompletedAt - StartedAt;
}

/// <summary>
/// Checkpoint saved when self-improvement is preempted mid-task.
/// Allows resuming from exactly where we left off.
/// </summary>
public class SelfWorkCheckpoint
{
    public SelfWorkItem               WorkItem   { get; set; } = new();
    public string                     StepName   { get; set; } = "";
    public Dictionary<string, string> SavedState { get; set; } = new();
    public DateTime                   SavedAt    { get; set; } = DateTime.UtcNow;
}

/// <summary>Status snapshot exposed via GET /selfimprove/status.</summary>
public class SelfImprovementStatus
{
    public SelfEngineState State                  { get; set; }
    public string?         CurrentWorkDescription { get; set; }
    public string?         CurrentStep            { get; set; }
    public string?         ThrottleReason         { get; set; }
    public double          CpuPercent             { get; set; }
    public double          RamUsedMb              { get; set; }
    public int             TotalCompleted         { get; set; }
    public int             TotalSuccessful        { get; set; }
    public int             QueueLength            { get; set; }
    public DateTime?       LastCompletedAt        { get; set; }
    public string?         LastInsight            { get; set; }
    public string?         UserFocusTopic         { get; set; }  // User-redirected focus
    public int             ActiveUserTasks        { get; set; }
}
