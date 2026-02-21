using System.Collections.Concurrent;

namespace Archimedes.Core;

public enum OutboxStatus
{
    PENDING,
    SENDING,
    SENT,
    FAILED
}

public class OutboxEntry
{
    public string Id { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string Payload { get; set; } = "";
    public string Destination { get; set; } = "";
    public OutboxStatus Status { get; set; } = OutboxStatus.PENDING;
    public int Attempts { get; set; } = 0;
    public DateTime? NextRetry { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptAt { get; set; }
    public string? Error { get; set; }
}

public class OutboxResult
{
    public bool Success { get; set; }
    public string? EntryId { get; set; }
    public bool IsDuplicate { get; set; }
    public string? Error { get; set; }
}

public class OutboxService
{
    private readonly ConcurrentDictionary<string, OutboxEntry> _entries = new();
    private readonly ConcurrentDictionary<string, string> _operationIdToEntryId = new();
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;

    private static readonly int[] BackoffMinutes = { 1, 5, 15, 60 };

    public OutboxService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public OutboxResult Enqueue(string operationId, string payload, string destination)
    {
        if (_operationIdToEntryId.TryGetValue(operationId, out var existingId))
        {
            if (_entries.TryGetValue(existingId, out var existing))
            {
                return new OutboxResult
                {
                    Success = existing.Status == OutboxStatus.SENT,
                    EntryId = existingId,
                    IsDuplicate = true
                };
            }
        }

        var entry = new OutboxEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            OperationId = operationId,
            Payload = payload,
            Destination = destination,
            Status = OutboxStatus.PENDING,
            NextRetry = DateTime.UtcNow
        };

        if (!_operationIdToEntryId.TryAdd(operationId, entry.Id))
        {
            return Enqueue(operationId, payload, destination);
        }

        _entries[entry.Id] = entry;
        ArchLogger.LogInfo($"Outbox: enqueued {entry.Id} for operation {Redactor.RedactPayload(operationId)}");

        return new OutboxResult { Success = true, EntryId = entry.Id, IsDuplicate = false };
    }

    public IEnumerable<OutboxEntry> GetPendingEntries()
    {
        var now = DateTime.UtcNow;
        return _entries.Values
            .Where(e => e.Status == OutboxStatus.PENDING && (e.NextRetry == null || e.NextRetry <= now))
            .OrderBy(e => e.CreatedAt);
    }

    public IEnumerable<OutboxEntry> GetAllEntries() => _entries.Values;

    public OutboxEntry? GetEntry(string id) =>
        _entries.TryGetValue(id, out var entry) ? entry : null;

    public async Task<bool> ProcessEntryAsync(OutboxEntry entry)
    {
        if (entry.Status != OutboxStatus.PENDING)
            return false;

        entry.Status = OutboxStatus.SENDING;
        entry.Attempts++;
        entry.LastAttemptAt = DateTime.UtcNow;

        try
        {
            var content = new StringContent(entry.Payload, System.Text.Encoding.UTF8, "application/json");
            content.Headers.Add("X-Operation-Id", entry.OperationId);

            var response = await _httpClient.PostAsync(entry.Destination, content);

            if (response.IsSuccessStatusCode)
            {
                entry.Status = OutboxStatus.SENT;
                entry.Error = null;
                ArchLogger.LogInfo($"Outbox: sent {entry.Id} successfully");
                return true;
            }

            throw new Exception($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;

            if (entry.Attempts >= BackoffMinutes.Length)
            {
                entry.Status = OutboxStatus.FAILED;
                ArchLogger.LogError($"Outbox: {entry.Id} failed permanently after {entry.Attempts} attempts");
            }
            else
            {
                entry.Status = OutboxStatus.PENDING;
                var backoffIndex = Math.Min(entry.Attempts - 1, BackoffMinutes.Length - 1);
                entry.NextRetry = DateTime.UtcNow.AddMinutes(BackoffMinutes[backoffIndex]);
                ArchLogger.LogWarn($"Outbox: {entry.Id} retry scheduled for {entry.NextRetry:O}");
            }

            return false;
        }
    }

    public void StartWorker()
    {
        if (_workerTask != null && !_workerTask.IsCompleted)
            return;

        _workerCts = new CancellationTokenSource();
        _workerTask = Task.Run(async () =>
        {
            while (!_workerCts.Token.IsCancellationRequested)
            {
                try
                {
                    var pending = GetPendingEntries().ToList();
                    foreach (var entry in pending)
                    {
                        if (_workerCts.Token.IsCancellationRequested) break;
                        await ProcessEntryAsync(entry);
                    }
                }
                catch (Exception ex)
                {
                    ArchLogger.LogError("Outbox worker error", ex);
                }

                await Task.Delay(5000, _workerCts.Token).ConfigureAwait(false);
            }
        }, _workerCts.Token);
    }

    public void StopWorker()
    {
        _workerCts?.Cancel();
    }

    public async Task<int> DrainAsync()
    {
        var pending = GetPendingEntries().ToList();
        var sent = 0;

        foreach (var entry in pending)
        {
            if (await ProcessEntryAsync(entry))
                sent++;
        }

        return sent;
    }

    public OutboxStats GetStats()
    {
        var entries = _entries.Values.ToList();
        return new OutboxStats
        {
            Total = entries.Count,
            Pending = entries.Count(e => e.Status == OutboxStatus.PENDING),
            Sending = entries.Count(e => e.Status == OutboxStatus.SENDING),
            Sent = entries.Count(e => e.Status == OutboxStatus.SENT),
            Failed = entries.Count(e => e.Status == OutboxStatus.FAILED)
        };
    }
}

public class OutboxStats
{
    public int Total { get; set; }
    public int Pending { get; set; }
    public int Sending { get; set; }
    public int Sent { get; set; }
    public int Failed { get; set; }
}
