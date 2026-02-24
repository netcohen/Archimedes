using System.Collections.Concurrent;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Manages promotion, canary routing, and rollback. Tracks versions and SLOs.
/// </summary>
public class PromotionManager
{
    private readonly string _releasesRoot;
    private readonly SelfUpdateAudit _audit;
    private string? _currentVersion;
    private string? _canaryVersion;
    private readonly ConcurrentQueue<RuntimeMetric> _metrics = new();
    private const int MaxMetrics = 500;

    public PromotionManager(string releasesRoot, SelfUpdateAudit audit)
    {
        _releasesRoot = Path.GetFullPath(releasesRoot);
        _audit = audit;
        Directory.CreateDirectory(_releasesRoot);
        LoadState();
    }

    public string? CurrentVersion => _currentVersion;
    public string? CanaryVersion => _canaryVersion;

    public PromotionStatus GetStatus()
    {
        return new PromotionStatus
        {
            CurrentVersion = _currentVersion,
            CanaryVersion = _canaryVersion,
            ReleasesRoot = _releasesRoot,
            RecentMetrics = _metrics.ToArray().TakeLast(20).ToList()
        };
    }

    public bool Promote(string candidateId, string sandboxPath, double? canaryPercent = null)
    {
        try
        {
            var prev = _currentVersion;
            AppendToHistory(candidateId);
            var versionDir = Path.Combine(_releasesRoot, candidateId);
            var sandboxCore = Path.Combine(sandboxPath, "core", "bin", "Release", "net8.0");
            if (!Directory.Exists(sandboxCore))
                sandboxCore = Path.Combine(sandboxPath, "core", "bin", "Debug", "net8.0");
            if (!Directory.Exists(sandboxCore))
            {
                _audit.Log("promote", candidateId, "Candidate build not found", false);
                return false;
            }

            CopyDirectory(sandboxCore, versionDir);
            var previous = _currentVersion;
            _currentVersion = candidateId;
            if (canaryPercent.HasValue && canaryPercent > 0)
                _canaryVersion = candidateId;
            else
                _canaryVersion = null;
            SaveState(previous);
            _audit.Log("promote", candidateId, $"Promoted from {prev ?? "none"}", true);
            return true;
        }
        catch (Exception ex)
        {
            _audit.Log("promote", candidateId, ex.Message, false);
            return false;
        }
    }

    public bool Rollback()
    {
        var statePath = Path.Combine(_releasesRoot, "state.json");
        if (!File.Exists(statePath))
        {
            _audit.Log("rollback", null, "No state to rollback", false);
            return false;
        }
        try
        {
            var json = File.ReadAllText(statePath);
            var state = JsonSerializer.Deserialize<PromotionState>(json);
            if (state?.PreviousVersion == null)
            {
                _audit.Log("rollback", null, "No previous version", false);
                return false;
            }
            var prevDir = Path.Combine(_releasesRoot, state.PreviousVersion);
            if (!Directory.Exists(prevDir))
            {
                _audit.Log("rollback", state.PreviousVersion, "Previous build missing", false);
                return false;
            }
            var oldCurrent = _currentVersion;
            _currentVersion = state.PreviousVersion;
            _canaryVersion = null;
            SaveState(previousVersion: oldCurrent);
            _audit.Log("rollback", _currentVersion, $"Rolled back from {state.CurrentVersion}", true);
            return true;
        }
        catch (Exception ex)
        {
            _audit.Log("rollback", null, ex.Message, false);
            return false;
        }
    }

    public void RecordMetric(RuntimeMetric m)
    {
        _metrics.Enqueue(m);
        while (_metrics.Count > MaxMetrics)
            _metrics.TryDequeue(out _);
    }

    private void LoadState()
    {
        var statePath = Path.Combine(_releasesRoot, "state.json");
        if (!File.Exists(statePath)) return;
        try
        {
            var json = File.ReadAllText(statePath);
            var state = JsonSerializer.Deserialize<PromotionState>(json);
            if (state != null)
            {
                _currentVersion = state.CurrentVersion;
                _canaryVersion = state.CanaryVersion;
            }
        }
        catch { }
    }

    private void SaveState(string? previousVersion = null)
    {
        var statePath = Path.Combine(_releasesRoot, "state.json");
        var state = new PromotionState
        {
            CurrentVersion = _currentVersion,
            CanaryVersion = _canaryVersion,
            PreviousVersion = previousVersion ?? GetPreviousFromHistory()
        };
        File.WriteAllText(statePath, JsonSerializer.Serialize(state));
    }

    private void AppendToHistory(string version)
    {
        try
        {
            var historyPath = Path.Combine(_releasesRoot, "history.txt");
            File.AppendAllText(historyPath, $"{version}\n");
        }
        catch { }
    }

    private string? GetPreviousFromHistory()
    {
        var historyPath = Path.Combine(_releasesRoot, "history.txt");
        if (!File.Exists(historyPath)) return null;
        try
        {
            var lines = File.ReadAllLines(historyPath);
            var versions = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
            var idx = versions.LastIndexOf(_currentVersion ?? "");
            if (idx > 0) return versions[idx - 1];
            return versions.Count >= 2 ? versions[^2] : null;
        }
        catch { }
        return null;
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var item in Directory.GetFileSystemEntries(src))
        {
            var name = Path.GetFileName(item);
            if (name == null) continue;
            var d = Path.Combine(dest, name);
            if (Directory.Exists(item))
                CopyDirectory(item, d);
            else
                File.Copy(item, d, true);
        }
    }
}

public class PromotionStatus
{
    public string? CurrentVersion { get; set; }
    public string? CanaryVersion { get; set; }
    public string ReleasesRoot { get; set; } = "";
    public List<RuntimeMetric> RecentMetrics { get; set; } = new();
}

public class PromotionState
{
    public string? CurrentVersion { get; set; }
    public string? CanaryVersion { get; set; }
    public string? PreviousVersion { get; set; }
}

public class RuntimeMetric
{
    public DateTime Timestamp { get; set; }
    public string? Version { get; set; }
    public bool Healthy { get; set; }
    public double LatencyMs { get; set; }
    public int ErrorCount { get; set; }
}
