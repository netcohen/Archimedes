using System.Collections.Concurrent;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Audit log for self-update events. Redacted, no secrets.
/// Phase 16: persists to JSONL file so audit survives restarts.
/// </summary>
public class SelfUpdateAudit
{
    private readonly ConcurrentQueue<AuditEvent> _events = new();
    private const int MaxEvents = 1000;
    private readonly string? _auditPath;
    private readonly object _fileLock = new();

    /// <param name="dataPath">Directory where selfupdate_audit.jsonl is stored. Null = memory-only.</param>
    public SelfUpdateAudit(string? dataPath = null)
    {
        if (dataPath != null)
        {
            Directory.CreateDirectory(dataPath);
            _auditPath = Path.Combine(dataPath, "selfupdate_audit.jsonl");
            LoadFromDisk();
        }
    }

    public void Log(string action, string? candidateId, string? details, bool success)
    {
        var evt = new AuditEvent
        {
            Timestamp   = DateTime.UtcNow,
            Action      = action,
            CandidateId = candidateId,
            Details     = Redactor.Redact(details ?? ""),
            Success     = success
        };

        _events.Enqueue(evt);
        while (_events.Count > MaxEvents)
            _events.TryDequeue(out _);

        PersistToDisk(evt);
    }

    public List<AuditEvent> GetEvents(int skip = 0, int take = 50)
    {
        return _events.Skip(skip).Take(take).ToList();
    }

    // ── Disk persistence ─────────────────────────────────────────────────────

    private void PersistToDisk(AuditEvent evt)
    {
        if (_auditPath == null) return;
        try
        {
            var line = JsonSerializer.Serialize(evt) + "\n";
            lock (_fileLock)
            {
                File.AppendAllText(_auditPath, line);
            }
        }
        catch { /* never let audit failure crash the system */ }
    }

    private void LoadFromDisk()
    {
        if (_auditPath == null || !File.Exists(_auditPath)) return;
        try
        {
            string[] lines;
            lock (_fileLock)
            {
                lines = File.ReadAllLines(_auditPath);
            }

            var loaded = new List<AuditEvent>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<AuditEvent>(line);
                    if (evt != null) loaded.Add(evt);
                }
                catch { /* skip malformed lines */ }
            }

            // Keep only the most recent MaxEvents
            foreach (var evt in loaded.TakeLast(MaxEvents))
                _events.Enqueue(evt);
        }
        catch { }
    }
}

public class AuditEvent
{
    public DateTime Timestamp   { get; set; }
    public string   Action      { get; set; } = "";
    public string?  CandidateId { get; set; }
    public string   Details     { get; set; } = "";
    public bool     Success     { get; set; }
}
