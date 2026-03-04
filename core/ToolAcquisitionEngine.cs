namespace Archimedes.Core;

/// <summary>
/// Phase 27 – Tool Acquisition Engine.
/// Main orchestrator for the autonomous tool acquisition pipeline.
///
/// Flow:
///   DetectGap → Search (tier1 → tier2 → tier3)
///   → For each candidate: Evaluate → LegalCheck
///   → LEGAL + SAFE/MANAGEABLE → Install
///   → NEEDS_APPROVAL → Ask user (pause gap)
///   → No candidates → Notify user
///
/// Legal approval responses are processed via ResolveLegalDecisionAsync().
/// </summary>
public class ToolAcquisitionEngine
{
    private readonly ToolGapDetector   _gapDetector;
    private readonly SearchOrchestrator _search;
    private readonly ToolEvaluator     _evaluator;
    private readonly LegalityChecker   _legalChecker;
    private readonly ToolInstaller     _installer;
    private readonly ToolStore         _toolStore;
    private readonly SourceIntelligence _intel;

    // Pending gap → completion sources (for async callers waiting on resolution)
    private readonly Dictionary<string, TaskCompletionSource<AcquiredTool?>>
        _pendingResolutions = new();
    private readonly object _resolutionLock = new();

    public ToolAcquisitionEngine(
        ToolGapDetector    gapDetector,
        SearchOrchestrator search,
        ToolEvaluator      evaluator,
        LegalityChecker    legalChecker,
        ToolInstaller      installer,
        ToolStore          toolStore,
        SourceIntelligence intel)
    {
        _gapDetector  = gapDetector;
        _search       = search;
        _evaluator    = evaluator;
        _legalChecker = legalChecker;
        _installer    = installer;
        _toolStore    = toolStore;
        _intel        = intel;
    }

    // ── Main entry point ───────────────────────────────────────────────────

    /// <summary>
    /// Tries to acquire a tool for the given capability.
    /// Returns the AcquiredTool if successful, null if blocked on legal approval.
    /// Caller should check ToolGapDetector.GetGap() for current status.
    /// </summary>
    public async Task<AcquiredTool?> AcquireAsync(
        string capability, string context,
        string? goalId = null, string? taskId = null,
        CancellationToken ct = default)
    {
        ArchLogger.LogInfo($"[Acquisition] Starting acquisition: capability={capability}");

        // Already acquired?
        var existing = _gapDetector.GetTool(capability);
        if (existing != null)
        {
            ArchLogger.LogInfo($"[Acquisition] Already have {capability}: toolId={existing.ToolId}");
            return existing;
        }

        // Detect / register gap
        var gap = _gapDetector.Detect(capability, context, goalId, taskId);

        // If gap is already awaiting legal, return null (still pending)
        if (gap?.Status == GapStatus.AWAITING_LEGAL) return null;

        // Search for candidates
        var candidates = await _search.SearchAsync(capability, context, ct);
        ArchLogger.LogInfo($"[Acquisition] Found {candidates.Count} candidates for {capability}");

        if (candidates.Count == 0)
        {
            NotifyNoSolution(gap, capability);
            return null;
        }

        // Evaluate and try to install each candidate (best first)
        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested) break;

            // Risk evaluation
            await _evaluator.EvaluateAsync(candidate, ct);
            if (candidate.Risk == ToolRiskLevel.DANGEROUS)
            {
                ArchLogger.LogInfo(
                    $"[Acquisition] Skipping DANGEROUS candidate: {candidate.Name}");
                continue;
            }

            // Legal evaluation
            var legalResult = await _legalChecker.EvaluateAsync(candidate, context);
            if (legalResult.Status == LegalStatus.NEEDS_APPROVAL)
            {
                // Check if user has approved something similar before
                var prior = _legalChecker.FindSimilarDecision(legalResult.Issue ?? "");
                if (prior == ApprovalDecision.APPROVED)
                {
                    ArchLogger.LogInfo(
                        $"[Acquisition] Prior approval found for similar issue – proceeding");
                    var t = await TryInstallAsync(candidate, gap, true, ct);
                    if (t != null) return t;
                    continue;
                }

                if (prior == ApprovalDecision.REJECTED)
                {
                    ArchLogger.LogInfo(
                        $"[Acquisition] Prior rejection found – skipping {candidate.Name}");
                    continue;
                }

                // Create approval request and pause
                ArchLogger.LogInfo(
                    $"[Acquisition] Legal approval needed for {candidate.Name}");
                var approval = _legalChecker.CreateApprovalRequest(
                    gap?.GapId ?? "", capability, legalResult, candidate);

                if (gap != null)
                    _gapDetector.MarkAwaitingLegal(gap.GapId, approval.ApprovalId);

                // Store candidate for when approval comes back
                StorePendingCandidate(approval.ApprovalId, candidate);
                return null;   // Gap is now AWAITING_LEGAL
            }

            // Legal & risk OK – install
            var tool = await TryInstallAsync(candidate, gap, false, ct);
            if (tool != null) return tool;
        }

        // All candidates exhausted – none worked
        if (gap != null)
            _gapDetector.MarkFailed(gap.GapId,
                $"No suitable tool found for {capability} after evaluating {candidates.Count} candidates");

        ArchLogger.LogWarn($"[Acquisition] Could not acquire {capability}");
        return null;
    }

    // ── Legal decision resolution ──────────────────────────────────────────

    /// <summary>
    /// Called when the user responds to a legal approval request.
    /// Resumes the acquisition if approved.
    /// </summary>
    public async Task<AcquiredTool?> ResolveLegalDecisionAsync(
        string approvalId, ApprovalDecision decision, string? userNote = null,
        CancellationToken ct = default)
    {
        _legalChecker.RecordDecision(approvalId, decision, userNote);

        var approval = _toolStore.GetApproval(approvalId);
        if (approval == null) return null;

        var gap = _toolStore.GetGap(approval.GapId);

        if (decision == ApprovalDecision.REJECTED ||
            decision == ApprovalDecision.WAITING_RESEARCH)
        {
            if (gap != null)
            {
                if (decision == ApprovalDecision.REJECTED)
                    _gapDetector.MarkRejected(gap.GapId);

                gap.UserMessage = userNote;
                _toolStore.UpdateGap(gap);
            }
            return null;
        }

        if (decision == ApprovalDecision.APPROVED)
        {
            // Retrieve the pending candidate and install
            var candidate = RetrievePendingCandidate(approvalId);
            if (candidate == null)
            {
                ArchLogger.LogWarn(
                    $"[Acquisition] No pending candidate for approvalId={approvalId}");
                return null;
            }

            var tool = await TryInstallAsync(candidate, gap, userApproved: true, ct);
            return tool;
        }

        return null;
    }

    // ── Install helper ─────────────────────────────────────────────────────

    private async Task<AcquiredTool?> TryInstallAsync(
        ToolCandidate candidate, ToolGapEvent? gap,
        bool userApproved, CancellationToken ct)
    {
        var tool = await _installer.InstallAsync(candidate, userApproved, ct);
        if (tool == null)
        {
            _intel.OnInstallFailed(candidate.SourceDomain);
            return null;
        }

        _intel.OnInstallSucceeded(candidate.SourceDomain);
        _intel.EnsureSourceKnown(candidate.SourceDomain, candidate.SourceUrl, candidate.SourceType);

        if (gap != null)
            _gapDetector.MarkResolved(gap.GapId, tool.ToolId);

        ArchLogger.LogInfo(
            $"[Acquisition] Tool acquired: {tool.Name} toolId={tool.ToolId}");
        return tool;
    }

    // ── Notification helpers ───────────────────────────────────────────────

    private void NotifyNoSolution(ToolGapEvent? gap, string capability)
    {
        var msg =
            $"חיפשתי בכל המקורות הזמינים ולא מצאתי אינטגרציה עבור: {capability}.\n" +
            "אני ממשיך לחקור את הנושא. האם יש לך הצעה חלופית?";

        if (gap != null)
        {
            gap.UserMessage = msg;
            _toolStore.UpdateGap(gap);
        }

        ArchLogger.LogInfo($"[Acquisition] No solution found for {capability}");
    }

    // ── Pending candidate storage ──────────────────────────────────────────
    // Stored in memory only – survives app lifetime but not restart.
    // A future phase can persist these to disk.

    private readonly Dictionary<string, ToolCandidate> _pendingCandidates = new();
    private readonly object _candidateLock = new();

    private void StorePendingCandidate(string approvalId, ToolCandidate candidate)
    {
        lock (_candidateLock)
            _pendingCandidates[approvalId] = candidate;
    }

    private ToolCandidate? RetrievePendingCandidate(string approvalId)
    {
        lock (_candidateLock)
        {
            _pendingCandidates.TryGetValue(approvalId, out var c);
            _pendingCandidates.Remove(approvalId);
            return c;
        }
    }

    // ── Status helpers (for endpoints) ─────────────────────────────────────

    public List<AcquiredTool>        GetAllTools()          => _toolStore.GetAllTools();
    public List<ToolGapEvent>        GetActiveGaps()        => _toolStore.GetActiveGaps();
    public List<ToolGapEvent>        GetAllGaps()           => _toolStore.GetAllGaps();
    public List<LegalApprovalRequest> GetPendingApprovals() => _toolStore.GetPendingApprovals();
    public List<SourceRecord>         GetSourceStats()      => _intel.GetAll();
    public bool                       IsTorAvailable        => _search.IsTorAvailable;
}
