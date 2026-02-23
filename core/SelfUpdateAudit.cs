using System.Collections.Concurrent;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Audit log for self-update events. Redacted, no secrets.
/// </summary>
public class SelfUpdateAudit
{
    private readonly ConcurrentQueue<AuditEvent> _events = new();
    private const int MaxEvents = 1000;

    public void Log(string action, string? candidateId, string? details, bool success)
    {
        var evt = new AuditEvent
        {
            Timestamp = DateTime.UtcNow,
            Action = action,
            CandidateId = candidateId,
            Details = Redactor.Redact(details ?? ""),
            Success = success
        };
        _events.Enqueue(evt);
        while (_events.Count > MaxEvents)
            _events.TryDequeue(out _);
    }

    public List<AuditEvent> GetEvents(int skip = 0, int take = 50)
    {
        return _events.Skip(skip).Take(take).ToList();
    }
}

public class AuditEvent
{
    public DateTime Timestamp { get; set; }
    public string Action { get; set; } = "";
    public string? CandidateId { get; set; }
    public string Details { get; set; } = "";
    public bool Success { get; set; }
}
