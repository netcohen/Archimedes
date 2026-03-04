namespace Archimedes.Core;

/// <summary>
/// Phase 27 – Tool Gap Detector.
/// Detects when Archimedes needs a capability it doesn't have yet.
///
/// Integration points:
///  - Called from GoalEngine when spawning a task for an unrecognised intent
///  - Called from TaskRunner when a step action type is unrecognised
///  - Can be called explicitly from /chat/message for forward-looking intent
/// </summary>
public class ToolGapDetector
{
    private readonly ToolStore      _toolStore;
    private readonly ProcedureStore _procedureStore;

    // Known built-in capabilities (always available, no acquisition needed)
    private static readonly HashSet<string> BuiltinCapabilities = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "WEB_NAVIGATE", "WEB_CLICK", "WEB_TYPE", "WEB_SCREENSHOT",
        "WEB_EXTRACT", "FILE_DOWNLOAD", "FILE_READ", "FILE_WRITE",
        "TASK_CREATE", "TASK_CANCEL", "GOAL_CREATE",
        "CHAT_REPLY", "CHAT_NOTIFY",
        "TESTSITE_EXPORT", "TESTSITE_MONITOR",
        "WEB_LOGIN", "DATA_EXTRACT", "UNKNOWN"
    };

    public ToolGapDetector(ToolStore toolStore, ProcedureStore procedureStore)
    {
        _toolStore      = toolStore;
        _procedureStore = procedureStore;
    }

    // ── Gap detection ──────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether a capability is available (built-in or acquired).
    /// Returns null if available, or a ToolGapEvent if not.
    /// </summary>
    public ToolGapEvent? Detect(
        string capability, string context,
        string? goalId = null, string? taskId = null)
    {
        // 1. Built-in?
        if (BuiltinCapabilities.Contains(capability))
            return null;

        // 2. Already acquired?
        if (_toolStore.HasCapability(capability))
            return null;

        // 3. Covered by a procedure?
        var proc = _procedureStore.FindBest(capability, context);
        if (proc != null)
            return null;

        // 4. Already searching for this gap?
        var active = _toolStore.GetActiveGaps()
            .FirstOrDefault(g =>
                g.Capability.Equals(capability, StringComparison.OrdinalIgnoreCase));
        if (active != null)
        {
            ArchLogger.LogInfo(
                $"[GapDetector] Gap for {capability} already active (gapId={active.GapId})");
            return active;
        }

        // 5. New gap
        var gap = new ToolGapEvent
        {
            Capability = capability,
            Context    = context,
            GoalId     = goalId,
            TaskId     = taskId,
            Status     = GapStatus.SEARCHING
        };

        _toolStore.AddGap(gap);

        ArchLogger.LogInfo(
            $"[GapDetector] New gap detected: {capability} (gapId={gap.GapId})");
        return gap;
    }

    /// <summary>
    /// Checks if a capability is currently available.
    /// </summary>
    public bool IsAvailable(string capability)
        => BuiltinCapabilities.Contains(capability) ||
           _toolStore.HasCapability(capability);

    /// <summary>
    /// Returns the AcquiredTool for a capability, if any.
    /// </summary>
    public AcquiredTool? GetTool(string capability)
        => _toolStore.GetToolByCapability(capability);

    /// <summary>
    /// Marks a gap as resolved.
    /// </summary>
    public void MarkResolved(string gapId, string toolId)
    {
        var gap = _toolStore.GetGap(gapId);
        if (gap == null) return;

        gap.Status        = GapStatus.RESOLVED;
        gap.ResolvedToolId = toolId;
        gap.ResolvedAt    = DateTime.UtcNow;
        _toolStore.UpdateGap(gap);
    }

    /// <summary>
    /// Marks a gap as awaiting legal approval.
    /// </summary>
    public void MarkAwaitingLegal(string gapId, string approvalId)
    {
        var gap = _toolStore.GetGap(gapId);
        if (gap == null) return;

        gap.Status            = GapStatus.AWAITING_LEGAL;
        gap.PendingApprovalId = approvalId;
        _toolStore.UpdateGap(gap);
    }

    /// <summary>
    /// Marks a gap as failed.
    /// </summary>
    public void MarkFailed(string gapId, string reason)
    {
        var gap = _toolStore.GetGap(gapId);
        if (gap == null) return;

        gap.Status      = GapStatus.FAILED;
        gap.UserMessage = reason;
        gap.ResolvedAt  = DateTime.UtcNow;
        _toolStore.UpdateGap(gap);
    }

    /// <summary>
    /// Marks a gap as user-rejected.
    /// </summary>
    public void MarkRejected(string gapId)
    {
        var gap = _toolStore.GetGap(gapId);
        if (gap == null) return;

        gap.Status     = GapStatus.USER_REJECTED;
        gap.ResolvedAt = DateTime.UtcNow;
        _toolStore.UpdateGap(gap);
    }
}
