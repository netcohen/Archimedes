using System.Collections.Concurrent;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 21 - Procedure Memory.
///
/// After a task completes successfully, its plan is stored as a
/// reusable procedure. When the same intent arrives again the Planner
/// retrieves the cached plan instead of rebuilding it - and skips the
/// deterministic build step entirely (foundation for future LLM-plan
/// caching in later phases).
///
/// Scoring: intent match (base 0.70) + keyword overlap (up to +0.20)
/// - staleness penalty - low-success-rate penalty.
/// </summary>

// ── Procedure record ──────────────────────────────────────────────────────────

public class ProcedureRecord
{
    public string   Id            { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string   Intent        { get; set; } = "";
    public string   PromptExample { get; set; } = "";
    public List<string> Keywords  { get; set; } = new();
    public TaskPlan Plan          { get; set; } = new();

    public int      SuccessCount  { get; set; }
    public int      FailureCount  { get; set; }

    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAt    { get; set; } = DateTime.UtcNow;
    public DateTime? LastSuccessAt{ get; set; }

    // Derived
    public double SuccessRate =>
        (SuccessCount + FailureCount) == 0
            ? 1.0
            : (double)SuccessCount / (SuccessCount + FailureCount);

    public int TotalUses => SuccessCount + FailureCount;
}

// ── Store ─────────────────────────────────────────────────────────────────────

public class ProcedureStore
{
    private readonly ConcurrentDictionary<string, ProcedureRecord> _records = new();
    private readonly string   _storeDir;
    private readonly object   _ioLock = new();

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { WriteIndented = true };

    public ProcedureStore()
    {
        _storeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Archimedes", "procedures");

        try { Directory.CreateDirectory(_storeDir); } catch { }
        LoadFromDisk();
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Find the best matching procedure for the given intent + prompt.
    /// Returns null if no procedure scores >= 0.60.
    /// </summary>
    public ProcedureRecord? FindBest(string intent, string userPrompt)
    {
        var candidates = _records.Values
            .Where(r => r.Intent == intent)
            .ToList();

        if (candidates.Count == 0) return null;

        return candidates
            .Select(r => (record: r, score: Score(r, userPrompt)))
            .Where(x => x.score >= 0.60)
            .OrderByDescending(x => x.score)
            .Select(x => x.record)
            .FirstOrDefault();
    }

    /// <summary>Save a new (or updated) procedure to memory + disk.</summary>
    public string Save(ProcedureRecord record)
    {
        _records[record.Id] = record;
        PersistAsync(record);
        ArchLogger.LogInfo(
            $"[ProcedureStore] Saved {record.Id} intent={record.Intent} " +
            $"successRate={record.SuccessRate:P0}");
        return record.Id;
    }

    /// <summary>Record a success or failure for an existing procedure.</summary>
    public void RecordOutcome(string procedureId, bool success)
    {
        if (!_records.TryGetValue(procedureId, out var record)) return;

        if (success)
        {
            record.SuccessCount++;
            record.LastSuccessAt = DateTime.UtcNow;
        }
        else
        {
            record.FailureCount++;
        }

        record.LastUsedAt = DateTime.UtcNow;
        PersistAsync(record);

        ArchLogger.LogInfo(
            $"[ProcedureStore] Outcome id={procedureId} success={success} " +
            $"rate={record.SuccessRate:P0} total={record.TotalUses}");
    }

    public List<ProcedureRecord> GetAll() =>
        _records.Values
                .OrderByDescending(r => r.LastUsedAt)
                .ToList();

    public ProcedureRecord? GetById(string id) =>
        _records.TryGetValue(id, out var r) ? r : null;

    public bool Delete(string id)
    {
        if (!_records.TryRemove(id, out _)) return false;
        try
        {
            var path = FilePath(id);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
        return true;
    }

    public int Count => _records.Count;

    // ── Scoring ───────────────────────────────────────────────────────────

    private static double Score(ProcedureRecord r, string userPrompt)
    {
        double score = 0.70; // base: intent already matches

        // Keyword overlap bonus (+0 to +0.20)
        var promptKw = ExtractKeywords(userPrompt);
        if (r.Keywords.Count > 0 && promptKw.Count > 0)
        {
            int overlap = r.Keywords.Intersect(promptKw, StringComparer.OrdinalIgnoreCase).Count();
            int total   = r.Keywords.Union  (promptKw, StringComparer.OrdinalIgnoreCase).Count();
            score += 0.20 * ((double)overlap / total);
        }

        // Low success rate penalty (only after 3+ uses)
        if (r.TotalUses >= 3 && r.SuccessRate < 0.50)
            score -= 0.30;

        // Staleness penalty (not used in 30+ days)
        if ((DateTime.UtcNow - r.LastUsedAt).TotalDays > 30)
            score -= 0.10;

        return score;
    }

    /// <summary>Extract meaningful keywords from a natural-language prompt.</summary>
    public static List<string> ExtractKeywords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();

        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a","an","the","to","from","and","or","in","on","at","for","of",
            "with","is","it","me","my","i","want","need","please","can",
            "could","would","get","this","that","be","do","have","has"
        };

        return text.ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', '!', '?', '\n', '\r', '-', '_', '/' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !stopWords.Contains(w))
            .Distinct()
            .Take(20)
            .ToList();
    }

    // ── Disk I/O ──────────────────────────────────────────────────────────

    private string FilePath(string id) =>
        Path.Combine(_storeDir, $"{id}.json");

    private void PersistAsync(ProcedureRecord record)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!IsValidId(record.Id)) return;
                lock (_ioLock)
                {
                    File.WriteAllText(
                        FilePath(record.Id),
                        JsonSerializer.Serialize(record, _jsonOpts));
                }
            }
            catch { /* best-effort */ }
        });
    }

    private void LoadFromDisk()
    {
        try
        {
            int loaded = 0;
            foreach (var file in Directory.GetFiles(_storeDir, "*.json"))
            {
                try
                {
                    var json   = File.ReadAllText(file);
                    var record = JsonSerializer.Deserialize<ProcedureRecord>(json);
                    if (record != null)
                    {
                        _records[record.Id] = record;
                        loaded++;
                    }
                }
                catch { }
            }
            if (loaded > 0)
                ArchLogger.LogInfo($"[ProcedureStore] Loaded {loaded} procedures from disk");
        }
        catch { }
    }

    private static bool IsValidId(string id) =>
        System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9\-_]+$");
}
