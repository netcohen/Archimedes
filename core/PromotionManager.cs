using System.Collections.Concurrent;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Manages promotion, canary routing, and rollback. Tracks versions and SLOs.
/// Phase 16: atomic state writes, idempotency (PROMOTE_NOOP), retention cleanup.
/// </summary>
public class PromotionManager
{
    private readonly string _releasesRoot;
    private readonly SelfUpdateAudit _audit;
    private string? _currentVersion;
    private string? _canaryVersion;
    private readonly ConcurrentQueue<RuntimeMetric> _metrics = new();
    private const int MaxMetrics = 500;
    private const int KeepLastCandidates = 5;
    private readonly object _promoteLock = new();

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

    /// <summary>
    /// Promotes a candidate. Returns PromoteResult (Success / Noop / Failed).
    /// Thread-safe. Idempotent: promoting the same candidateId twice returns Noop.
    /// </summary>
    public PromoteResult Promote(string candidateId, string sandboxPath, double? canaryPercent = null)
    {
        lock (_promoteLock)
        {
            // Idempotency: already on this version
            if (_currentVersion == candidateId)
            {
                _audit.Log("promote", candidateId, "PROMOTE_NOOP: already current version", true);
                return PromoteResult.Noop;
            }

            try
            {
                var sandboxCore = Path.Combine(sandboxPath, "core", "bin", "Release", "net8.0");
                if (!Directory.Exists(sandboxCore))
                    sandboxCore = Path.Combine(sandboxPath, "core", "bin", "Debug", "net8.0");
                if (!Directory.Exists(sandboxCore))
                {
                    _audit.Log("promote", candidateId, "Candidate build not found", false);
                    return PromoteResult.Failed;
                }

                var prev = _currentVersion;
                var versionDir = Path.Combine(_releasesRoot, candidateId);
                CopyDirectory(sandboxCore, versionDir);
                AppendToHistory(candidateId);

                _currentVersion = candidateId;
                _canaryVersion = (canaryPercent.HasValue && canaryPercent > 0) ? candidateId : null;

                SaveStateAtomic(previousVersion: prev);
                _audit.Log("promote", candidateId, $"PROMOTE_SUCCESS from {prev ?? "none"}", true);

                CleanupOldCandidates();
                return PromoteResult.Success;
            }
            catch (Exception ex)
            {
                _audit.Log("promote", candidateId, ex.Message, false);
                return PromoteResult.Failed;
            }
        }
    }

    /// <summary>
    /// Rolls back to previous version. Returns RollbackResult.
    /// Idempotent: rolling back when nothing exists returns NothingToRollback.
    /// </summary>
    public RollbackResult Rollback()
    {
        lock (_promoteLock)
        {
            var statePath = Path.Combine(_releasesRoot, "state.json");
            if (!File.Exists(statePath))
            {
                _audit.Log("rollback", null, "No state file — nothing to rollback", false);
                return RollbackResult.NothingToRollback;
            }

            try
            {
                var json = File.ReadAllText(statePath);
                var state = JsonSerializer.Deserialize<PromotionState>(json);
                if (state?.PreviousVersion == null)
                {
                    _audit.Log("rollback", null, "No previous version — nothing to rollback", false);
                    return RollbackResult.NothingToRollback;
                }

                var prevDir = Path.Combine(_releasesRoot, state.PreviousVersion);
                if (!Directory.Exists(prevDir))
                {
                    _audit.Log("rollback", state.PreviousVersion, "Previous build directory missing", false);
                    return RollbackResult.Failed;
                }

                var oldCurrent = _currentVersion;
                _currentVersion = state.PreviousVersion;
                _canaryVersion = null;
                // After rollback: explicitly no further previous — skip history lookup to prevent double-rollback
                SaveStateAtomic(previousVersion: null, skipHistoryLookup: true);
                _audit.Log("rollback", _currentVersion, $"ROLLBACK_SUCCESS from {oldCurrent ?? "none"}", true);
                return RollbackResult.Success;
            }
            catch (Exception ex)
            {
                _audit.Log("rollback", null, ex.Message, false);
                return RollbackResult.Failed;
            }
        }
    }

    public void RecordMetric(RuntimeMetric m)
    {
        _metrics.Enqueue(m);
        while (_metrics.Count > MaxMetrics)
            _metrics.TryDequeue(out _);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

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

    /// <summary>
    /// Atomic state write: write to .tmp then File.Replace — no corrupt state on crash.
    /// skipHistoryLookup=true forces PreviousVersion=null (used after rollback to prevent double-rollback).
    /// </summary>
    private void SaveStateAtomic(string? previousVersion = null, bool skipHistoryLookup = false)
    {
        var statePath = Path.Combine(_releasesRoot, "state.json");
        var tmpPath   = statePath + ".tmp";
        var backupPath = statePath + ".bak";

        var state = new PromotionState
        {
            CurrentVersion  = _currentVersion,
            CanaryVersion   = _canaryVersion,
            PreviousVersion = skipHistoryLookup ? previousVersion : (previousVersion ?? GetPreviousFromHistory())
        };

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmpPath, json);

        if (File.Exists(statePath))
            File.Replace(tmpPath, statePath, backupPath);
        else
            File.Move(tmpPath, statePath);
    }

    /// <summary>
    /// Removes old candidate directories, keeping the most recent KeepLastCandidates.
    /// Never deletes current or previous version directories.
    /// </summary>
    private void CleanupOldCandidates()
    {
        try
        {
            // Reload state to get accurate previousVersion after SaveStateAtomic
            string? previousVersion = null;
            var statePath = Path.Combine(_releasesRoot, "state.json");
            if (File.Exists(statePath))
            {
                var state = JsonSerializer.Deserialize<PromotionState>(File.ReadAllText(statePath));
                previousVersion = state?.PreviousVersion;
            }

            var protected_ = new HashSet<string?> { _currentVersion, previousVersion, _canaryVersion }
                .Where(v => v != null).Cast<string>().ToHashSet();

            var candidateDirs = Directory.GetDirectories(_releasesRoot)
                .Select(d => new DirectoryInfo(d))
                .Where(d => !protected_.Contains(d.Name))
                .OrderBy(d => d.CreationTimeUtc)
                .ToList();

            var allowedUnprotected = Math.Max(0, KeepLastCandidates - protected_.Count);
            var toDelete = candidateDirs.Count - allowedUnprotected;
            if (toDelete <= 0) return;

            foreach (var dir in candidateDirs.Take(toDelete))
            {
                try
                {
                    Directory.Delete(dir.FullName, recursive: true);
                    _audit.Log("cleanup", dir.Name, "CLEANUP: removed old candidate", true);
                }
                catch (Exception ex)
                {
                    _audit.Log("cleanup", dir.Name, $"CLEANUP_FAILED: {ex.Message}", false);
                }
            }
        }
        catch (Exception ex)
        {
            _audit.Log("cleanup", null, $"CLEANUP_FAILED: {ex.Message}", false);
        }
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

// ── Result enums ─────────────────────────────────────────────────────────────

public enum PromoteResult  { Success, Noop, Failed }
public enum RollbackResult { Success, NothingToRollback, Failed }

// ── Data models ──────────────────────────────────────────────────────────────

public class PromotionStatus
{
    public string? CurrentVersion { get; set; }
    public string? CanaryVersion  { get; set; }
    public string  ReleasesRoot   { get; set; } = "";
    public List<RuntimeMetric> RecentMetrics { get; set; } = new();
}

public class PromotionState
{
    public string? CurrentVersion  { get; set; }
    public string? CanaryVersion   { get; set; }
    public string? PreviousVersion { get; set; }
}

public class RuntimeMetric
{
    public DateTime Timestamp  { get; set; }
    public string?  Version    { get; set; }
    public bool     Healthy    { get; set; }
    public double   LatencyMs  { get; set; }
    public int      ErrorCount { get; set; }
}
