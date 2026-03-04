using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 27 – Persists acquired tools (capabilities Archimedes has installed for itself).
/// Storage: %LOCALAPPDATA%\Archimedes\acquired_tools.json
/// </summary>
public class ToolStore
{
    private readonly string _toolsPath;
    private readonly string _gapsPath;
    private readonly string _approvalsPath;
    private readonly object _lock = new();
    private List<AcquiredTool>        _tools     = new();
    private List<ToolGapEvent>        _gaps      = new();
    private List<LegalApprovalRequest> _approvals = new();

    private static readonly JsonSerializerOptions _json = new()
        { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public ToolStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Archimedes");
        Directory.CreateDirectory(dir);
        _toolsPath     = Path.Combine(dir, "acquired_tools.json");
        _gapsPath      = Path.Combine(dir, "tool_gaps.json");
        _approvalsPath = Path.Combine(dir, "legal_approvals.json");
        Load();
    }

    // ── Tools ──────────────────────────────────────────────────────────────

    public void AddTool(AcquiredTool tool)
    {
        lock (_lock) { _tools.Add(tool); SaveTools(); }
    }

    public AcquiredTool? GetToolByCapability(string capability) =>
        _tools.FirstOrDefault(t =>
            t.Capability.Equals(capability, StringComparison.OrdinalIgnoreCase));

    public AcquiredTool? GetToolById(string toolId) =>
        _tools.FirstOrDefault(t => t.ToolId == toolId);

    public List<AcquiredTool> GetAllTools()
    {
        lock (_lock) { return _tools.ToList(); }
    }

    public void UpdateTool(AcquiredTool tool)
    {
        lock (_lock)
        {
            var idx = _tools.FindIndex(t => t.ToolId == tool.ToolId);
            if (idx >= 0) _tools[idx] = tool;
            SaveTools();
        }
    }

    public bool HasCapability(string capability) =>
        _tools.Any(t => t.Capability.Equals(capability, StringComparison.OrdinalIgnoreCase));

    // ── Gaps ───────────────────────────────────────────────────────────────

    public void AddGap(ToolGapEvent gap)
    {
        lock (_lock) { _gaps.Add(gap); SaveGaps(); }
    }

    public ToolGapEvent? GetGap(string gapId) =>
        _gaps.FirstOrDefault(g => g.GapId == gapId);

    public List<ToolGapEvent> GetActiveGaps() =>
        _gaps.Where(g => g.Status == GapStatus.SEARCHING ||
                         g.Status == GapStatus.AWAITING_LEGAL).ToList();

    public List<ToolGapEvent> GetAllGaps()
    {
        lock (_lock) { return _gaps.ToList(); }
    }

    public void UpdateGap(ToolGapEvent gap)
    {
        lock (_lock)
        {
            var idx = _gaps.FindIndex(g => g.GapId == gap.GapId);
            if (idx >= 0) _gaps[idx] = gap;
            else _gaps.Add(gap);
            SaveGaps();
        }
    }

    // ── Legal Approvals ────────────────────────────────────────────────────

    public void AddApproval(LegalApprovalRequest req)
    {
        lock (_lock) { _approvals.Add(req); SaveApprovals(); }
    }

    public LegalApprovalRequest? GetApproval(string approvalId) =>
        _approvals.FirstOrDefault(a => a.ApprovalId == approvalId);

    public List<LegalApprovalRequest> GetPendingApprovals() =>
        _approvals.Where(a => a.Decision == ApprovalDecision.PENDING).ToList();

    public List<LegalApprovalRequest> GetAllApprovals()
    {
        lock (_lock) { return _approvals.ToList(); }
    }

    public void UpdateApproval(LegalApprovalRequest req)
    {
        lock (_lock)
        {
            var idx = _approvals.FindIndex(a => a.ApprovalId == req.ApprovalId);
            if (idx >= 0) _approvals[idx] = req;
            SaveApprovals();
        }
    }

    // ── Persistence ────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (File.Exists(_toolsPath))
                _tools = JsonSerializer.Deserialize<List<AcquiredTool>>(
                    File.ReadAllText(_toolsPath), _json) ?? new();
        }
        catch { _tools = new(); }

        try
        {
            if (File.Exists(_gapsPath))
                _gaps = JsonSerializer.Deserialize<List<ToolGapEvent>>(
                    File.ReadAllText(_gapsPath), _json) ?? new();
        }
        catch { _gaps = new(); }

        try
        {
            if (File.Exists(_approvalsPath))
                _approvals = JsonSerializer.Deserialize<List<LegalApprovalRequest>>(
                    File.ReadAllText(_approvalsPath), _json) ?? new();
        }
        catch { _approvals = new(); }
    }

    private void SaveTools()
        => File.WriteAllText(_toolsPath, JsonSerializer.Serialize(_tools, _json));

    private void SaveGaps()
        => File.WriteAllText(_gapsPath, JsonSerializer.Serialize(_gaps, _json));

    private void SaveApprovals()
        => File.WriteAllText(_approvalsPath, JsonSerializer.Serialize(_approvals, _json));
}
