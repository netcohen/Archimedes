using System.Collections.Concurrent;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 19 – Observability: TraceService.
///
/// Responsibilities:
///   1. Maintain an in-memory active-trace map (keyed by CorrelationId).
///   2. Accumulate step-level trace entries per correlation.
///   3. On completion, move trace to a circular completed-buffer (max 200).
///   4. Persist every completed trace to disk as JSON for long-term retrieval.
///   5. Support GET /traces and GET /traces/{id} queries.
/// </summary>
public class TraceService
{
    // ── In-memory stores ──────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, ExecutionTrace> _active    = new();
    private readonly ConcurrentQueue<ExecutionTrace>              _completed  = new();
    private const int MaxCompleted = 200;
    private readonly object _purgeLock = new();

    // ── Disk persistence ──────────────────────────────────────────────────────
    private readonly string _traceDir;
    private static readonly JsonSerializerOptions _jsonOpts =
        new() { WriteIndented = true };

    public TraceService()
    {
        _traceDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Archimedes", "traces");

        try { Directory.CreateDirectory(_traceDir); }
        catch { /* best-effort */ }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Begin tracking a new request.</summary>
    public ExecutionTrace Begin(string correlationId, string endpoint, string method = "GET", string? taskId = null)
    {
        var trace = new ExecutionTrace
        {
            CorrelationId = correlationId,
            TaskId        = taskId,
            Endpoint      = endpoint,
            Method        = method,
            StartedAtUtc  = DateTime.UtcNow,
        };
        _active[correlationId] = trace;
        return trace;
    }

    /// <summary>Start a named step within an active trace.</summary>
    public void BeginStep(string correlationId, string stepName)
    {
        if (!_active.TryGetValue(correlationId, out var trace)) return;

        var step = new TraceStep
        {
            Index        = trace.Steps.Count,
            Name         = stepName,
            StartedAtUtc = DateTime.UtcNow,
        };

        lock (trace.Steps) { trace.Steps.Add(step); }
    }

    /// <summary>Mark a named step as completed.</summary>
    public void CompleteStep(
        string      correlationId,
        string      stepName,
        bool        success,
        FailureCode code     = FailureCode.None,
        string?     details  = null,
        string?     outcome  = null,   // Phase 20: OutcomeResult as string
        string?     evidence = null)   // Phase 20: verification evidence
    {
        if (!_active.TryGetValue(correlationId, out var trace)) return;

        TraceStep? step;
        lock (trace.Steps)
        {
            step = trace.Steps.LastOrDefault(s => s.Name == stepName && s.CompletedAtUtc == null);
        }
        if (step == null) return;

        step.CompletedAtUtc = DateTime.UtcNow;
        step.DurationMs     = (long)(step.CompletedAtUtc.Value - step.StartedAtUtc).TotalMilliseconds;
        step.Success        = success;
        step.FailureCode    = code;
        step.Details        = details;
        step.Outcome        = outcome;
        step.Evidence       = evidence;
    }

    /// <summary>Finalize the trace and persist it to disk.</summary>
    public ExecutionTrace? Complete(
        string     correlationId,
        bool       success,
        int        httpStatus    = 200,
        FailureCode code         = FailureCode.None,
        string?    message       = null)
    {
        if (!_active.TryRemove(correlationId, out var trace)) return null;

        trace.CompletedAtUtc  = DateTime.UtcNow;
        trace.TotalDurationMs = (long)(trace.CompletedAtUtc.Value - trace.StartedAtUtc).TotalMilliseconds;
        trace.Success         = success;
        trace.HttpStatusCode  = httpStatus;
        trace.FailureCode     = code;
        trace.FailureMessage  = message;

        // Add to circular buffer
        _completed.Enqueue(trace);
        lock (_purgeLock)
        {
            while (_completed.Count > MaxCompleted)
                _completed.TryDequeue(out _);
        }

        // Persist asynchronously (best-effort, never throws)
        _ = Task.Run(() => PersistToDisk(trace));

        return trace;
    }

    // ── Query API ─────────────────────────────────────────────────────────────

    /// <summary>Get a trace by CorrelationId (active → buffer → disk).</summary>
    public ExecutionTrace? Get(string correlationId)
    {
        // 1. Active (still in-flight)
        if (_active.TryGetValue(correlationId, out var active))
            return active;

        // 2. Completed buffer
        var buffered = _completed.FirstOrDefault(t => t.CorrelationId == correlationId);
        if (buffered != null) return buffered;

        // 3. Disk
        return LoadFromDisk(correlationId);
    }

    /// <summary>List recently completed traces (newest first).</summary>
    public List<ExecutionTrace> GetRecent(int count = 20)
    {
        return _completed
            .TakeLast(count)
            .OrderByDescending(t => t.StartedAtUtc)
            .ToList();
    }

    /// <summary>Number of currently active (in-flight) traces.</summary>
    public int ActiveCount => _active.Count;

    /// <summary>Total completed traces in the in-memory buffer.</summary>
    public int CompletedCount => _completed.Count;

    /// <summary>
    /// Phase 22 – Chat UI: Returns the most recent in-flight step name and endpoint,
    /// or null if nothing is currently active.
    /// Excludes polling/housekeeping endpoints so the status bar shows only real work.
    /// </summary>
    public (string Endpoint, string StepName)? GetLatestActivity()
    {
        // These endpoints are polled constantly by the UI — exclude them from "activity"
        // Note: "/chat/message" is intentionally NOT excluded — it's real user-driven work
        static bool IsNoise(string ep) =>
            ep == "/status/current"          ||
            ep == "/system/metrics"          ||
            ep == "/health"                  ||
            ep == "/tasks"                   ||
            ep == "/chat"                    ||
            ep.StartsWith("/traces")         ||
            ep.StartsWith("/procedures");

        var active = _active.Values
            .Where(t => !IsNoise(t.Endpoint))
            .OrderByDescending(t => t.StartedAtUtc)
            .FirstOrDefault();

        if (active == null) return null;

        TraceStep? lastStep;
        lock (active.Steps)
        {
            // Prefer the last open (incomplete) step; fall back to the last step overall
            lastStep = active.Steps.LastOrDefault(s => s.CompletedAtUtc == null)
                    ?? active.Steps.LastOrDefault();
        }

        if (lastStep == null) return (active.Endpoint, "");
        return (active.Endpoint, lastStep.Name);
    }

    // ── Disk helpers ─────────────────────────────────────────────────────────

    private void PersistToDisk(ExecutionTrace trace)
    {
        try
        {
            var path = Path.Combine(_traceDir, $"{trace.CorrelationId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(trace, _jsonOpts));
        }
        catch { /* best-effort — observability must never crash the app */ }
    }

    private ExecutionTrace? LoadFromDisk(string correlationId)
    {
        try
        {
            // Sanitize: correlationId must be alphanumeric to prevent path traversal
            if (!System.Text.RegularExpressions.Regex.IsMatch(correlationId, @"^[a-zA-Z0-9\-_]+$"))
                return null;

            var path = Path.Combine(_traceDir, $"{correlationId}.json");
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ExecutionTrace>(json);
        }
        catch { return null; }
    }
}
