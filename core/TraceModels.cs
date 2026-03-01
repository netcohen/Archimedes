namespace Archimedes.Core;

/// <summary>
/// A single step within an execution trace.
/// </summary>
public class TraceStep
{
    public int    Index          { get; set; }
    public string Name           { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long   DurationMs     { get; set; }
    public bool   Success        { get; set; }
    public FailureCode FailureCode { get; set; } = FailureCode.None;
    public string? Details       { get; set; }
}

/// <summary>
/// Full execution trace for one HTTP request (keyed by CorrelationId).
/// Persisted to disk; queryable via GET /traces/{correlationId}.
/// </summary>
public class ExecutionTrace
{
    public string  CorrelationId   { get; set; } = "";
    public string? TaskId          { get; set; }
    public string  Endpoint        { get; set; } = "";
    public string  Method          { get; set; } = "";
    public DateTime StartedAtUtc   { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long    TotalDurationMs { get; set; }
    public bool    Success         { get; set; }
    public int     HttpStatusCode  { get; set; }
    public FailureCode FailureCode { get; set; } = FailureCode.None;
    public string? FailureMessage  { get; set; }
    public List<TraceStep> Steps   { get; set; } = new();
}
