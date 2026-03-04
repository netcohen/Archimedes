using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 29 – Self-Improvement Store.
///
/// Persists everything the self-improvement engine learns and does:
///   - History of completed work items (results)
///   - Insights and key findings accumulated over time
///   - Checkpoint for resuming a paused work item
///   - Dataset entries for future LLM fine-tuning
///
/// Storage:
///   %LOCALAPPDATA%\Archimedes\selfimprove_results.json
///   %LOCALAPPDATA%\Archimedes\selfimprove_insights.json
///   %LOCALAPPDATA%\Archimedes\selfimprove_checkpoint.json
///   %LOCALAPPDATA%\Archimedes\selfimprove_dataset.jsonl
/// </summary>
public class SelfImprovementStore
{
    private const int MaxResults  = 500;
    private const int MaxInsights = 200;

    private readonly string _resultsPath;
    private readonly string _insightsPath;
    private readonly string _checkpointPath;
    private readonly string _datasetPath;
    private readonly object _lock = new();

    private List<SelfWorkResult> _results  = new();
    private List<string>         _insights = new();

    private static readonly JsonSerializerOptions _json = new()
        { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public SelfImprovementStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Archimedes");
        Directory.CreateDirectory(dir);

        _resultsPath    = Path.Combine(dir, "selfimprove_results.json");
        _insightsPath   = Path.Combine(dir, "selfimprove_insights.json");
        _checkpointPath = Path.Combine(dir, "selfimprove_checkpoint.json");
        _datasetPath    = Path.Combine(dir, "selfimprove_dataset.jsonl");

        Load();
    }

    // ── Results ───────────────────────────────────────────────────────────

    public void AddResult(SelfWorkResult result)
    {
        lock (_lock)
        {
            _results.Insert(0, result);
            while (_results.Count > MaxResults) _results.RemoveAt(_results.Count - 1);
            SaveResults();
        }
    }

    public List<SelfWorkResult> GetHistory(int limit = 50)
    {
        lock (_lock) { return _results.Take(limit).ToList(); }
    }

    public SelfWorkResult? GetLatestResult()
    {
        lock (_lock) { return _results.Count > 0 ? _results[0] : null; }
    }

    public int TotalCompleted  { get; private set; }
    public int TotalSuccessful { get; private set; }

    // ── Insights ──────────────────────────────────────────────────────────

    public void AddInsight(string insight)
    {
        lock (_lock)
        {
            _insights.Insert(0, $"[{DateTime.Now:HH:mm}] {insight}");
            while (_insights.Count > MaxInsights) _insights.RemoveAt(_insights.Count - 1);
            SaveInsights();
        }
    }

    public List<string> GetInsights(int limit = 20)
    {
        lock (_lock) { return _insights.Take(limit).ToList(); }
    }

    public string? GetLatestInsight()
    {
        lock (_lock) { return _insights.Count > 0 ? _insights[0] : null; }
    }

    // ── Checkpoint ────────────────────────────────────────────────────────

    public void SaveCheckpoint(SelfWorkCheckpoint checkpoint)
    {
        try
        {
            lock (_lock)
            {
                File.WriteAllText(_checkpointPath,
                    JsonSerializer.Serialize(checkpoint, _json));
            }
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[SelfImprovementStore] Checkpoint save failed: {ex.Message}");
        }
    }

    public SelfWorkCheckpoint? LoadCheckpoint()
    {
        try
        {
            if (!File.Exists(_checkpointPath)) return null;
            lock (_lock)
            {
                var json = File.ReadAllText(_checkpointPath);
                return JsonSerializer.Deserialize<SelfWorkCheckpoint>(json, _json);
            }
        }
        catch { return null; }
    }

    public void ClearCheckpoint()
    {
        try
        {
            lock (_lock)
            {
                if (File.Exists(_checkpointPath)) File.Delete(_checkpointPath);
            }
        }
        catch { }
    }

    // ── Dataset ───────────────────────────────────────────────────────────

    /// <summary>
    /// Appends one JSON-line entry to the training dataset file.
    /// Used by COLLECT_DATASET work items.
    /// </summary>
    public void AppendDatasetEntry(string jsonLine)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_datasetPath, jsonLine + Environment.NewLine);
            }
        }
        catch { }
    }

    public long DatasetEntriesCount()
    {
        try
        {
            if (!File.Exists(_datasetPath)) return 0;
            return File.ReadLines(_datasetPath).Count(l => !string.IsNullOrWhiteSpace(l));
        }
        catch { return 0; }
    }

    // ── Internal ──────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (File.Exists(_resultsPath))
            {
                var r = JsonSerializer.Deserialize<List<SelfWorkResult>>(
                    File.ReadAllText(_resultsPath), _json);
                if (r != null)
                {
                    _results       = r;
                    TotalCompleted  = r.Count;
                    TotalSuccessful = r.Count(x => x.Success);
                }
            }
        }
        catch { }

        try
        {
            if (File.Exists(_insightsPath))
            {
                var i = JsonSerializer.Deserialize<List<string>>(
                    File.ReadAllText(_insightsPath), _json);
                if (i != null) _insights = i;
            }
        }
        catch { }
    }

    private void SaveResults()
    {
        try
        {
            TotalCompleted  = _results.Count;
            TotalSuccessful = _results.Count(r => r.Success);
            File.WriteAllText(_resultsPath, JsonSerializer.Serialize(_results, _json));
        }
        catch { }
    }

    private void SaveInsights()
    {
        try { File.WriteAllText(_insightsPath, JsonSerializer.Serialize(_insights, _json)); }
        catch { }
    }
}
