using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 26 — Persists goals to disk as JSON.
/// Path: %LOCALAPPDATA%\Archimedes\goals.json
/// Thread-safe via lock.
/// </summary>
public class GoalStore
{
    private readonly string    _path;
    private readonly object    _lock = new();
    private          List<Goal> _goals;

    public GoalStore(string? basePath = null)
    {
        var dir = basePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Archimedes");
        Directory.CreateDirectory(dir);
        _path  = Path.Combine(dir, "goals.json");
        _goals = Load();
        ArchLogger.LogInfo($"[GoalStore] Loaded {_goals.Count} goal(s) from disk");
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private List<Goal> Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<Goal>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[GoalStore] Load failed: {ex.Message}");
            return new();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_goals,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public Goal Add(Goal goal)
    {
        lock (_lock) { _goals.Add(goal); Save(); return goal; }
    }

    public Goal? GetById(string id)
    {
        lock (_lock) { return _goals.FirstOrDefault(g => g.GoalId == id); }
    }

    public List<Goal> GetAll()
    {
        lock (_lock) { return _goals.ToList(); }
    }

    /// <summary>Returns goals that need to be advanced (ACTIVE or MONITORING).</summary>
    public List<Goal> GetActive()
    {
        lock (_lock)
            return _goals
                .Where(g => g.State == GoalState.ACTIVE || g.State == GoalState.MONITORING)
                .ToList();
    }

    public void Update(Goal goal)
    {
        lock (_lock)
        {
            goal.UpdatedAtUtc = DateTime.UtcNow;
            var idx = _goals.FindIndex(g => g.GoalId == goal.GoalId);
            if (idx >= 0) _goals[idx] = goal;
            Save();
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var removed = _goals.RemoveAll(g => g.GoalId == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public int Count      => _goals.Count;
    public int ActiveCount => _goals.Count(g => g.State == GoalState.ACTIVE || g.State == GoalState.MONITORING);
}
