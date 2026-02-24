using System.Text.Json;

namespace Archimedes.Core;

public class SavedState
{
    public string JobId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string Status { get; set; } = "paused";
    public DateTime SavedAt { get; set; }
}

public class PersistentRun
{
    public string Id { get; set; } = "";
    public string JobId { get; set; } = "";
    public string Status { get; set; } = "";
    public int Step { get; set; } = 0;
    public string? Checkpoint { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
}

public class RecoveryState
{
    public List<PersistentRun> Runs { get; set; } = new();
    public DateTime LastSaved { get; set; }
}

public class RecoveryManager
{
    private readonly string _statePath;
    private RecoveryState _state;

    public RecoveryManager(string? basePath = null)
    {
        var dir = basePath ?? Path.Combine(Path.GetTempPath(), "archimedes");
        Directory.CreateDirectory(dir);
        _statePath = Path.Combine(dir, "recovery_state.json");
        _state = Load();
    }

    private RecoveryState Load()
    {
        if (!File.Exists(_statePath))
            return new RecoveryState();

        try
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<RecoveryState>(json) ?? new RecoveryState();
        }
        catch
        {
            return new RecoveryState();
        }
    }

    private void Save()
    {
        _state.LastSaved = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_statePath, json);
    }

    public void TrackRun(Run run)
    {
        var existing = _state.Runs.FirstOrDefault(r => r.Id == run.Id);
        if (existing != null)
        {
            existing.Status = run.Status;
            existing.Step = run.Step;
            existing.Checkpoint = run.Checkpoint;
            existing.EndTime = run.EndTime;
        }
        else
        {
            _state.Runs.Add(new PersistentRun
            {
                Id = run.Id,
                JobId = run.JobId,
                Status = run.Status,
                Step = run.Step,
                Checkpoint = run.Checkpoint,
                StartTime = run.StartTime,
                EndTime = run.EndTime
            });
        }
        Save();
    }

    public void UpdateRunStatus(string runId, string status, int? step = null, string? checkpoint = null)
    {
        var run = _state.Runs.FirstOrDefault(r => r.Id == runId);
        if (run != null)
        {
            run.Status = status;
            if (step.HasValue) run.Step = step.Value;
            if (checkpoint != null) run.Checkpoint = checkpoint;
            if (status == RunStatus.Completed || status == RunStatus.Failed)
                run.EndTime = DateTime.UtcNow;
            Save();
        }
    }

    public List<PersistentRun> GetRecoverableRuns()
    {
        return _state.Runs
            .Where(r => r.Status == RunStatus.Running || r.Status == RunStatus.Recovering)
            .ToList();
    }

    public void MarkRecovering(string runId)
    {
        var run = _state.Runs.FirstOrDefault(r => r.Id == runId);
        if (run != null)
        {
            run.Status = RunStatus.Recovering;
            Save();
            ArchLogger.LogInfo($"Run {runId} marked as RECOVERING (was {run.Status})");
        }
    }

    public void ClearRun(string runId)
    {
        _state.Runs.RemoveAll(r => r.Id == runId);
        Save();
    }

    public void ClearAll()
    {
        _state = new RecoveryState();
        Save();
    }

    public RecoveryState GetState() => _state;
}
