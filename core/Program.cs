using System.Text.Json;
using Archimedes.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();

var app = builder.Build();

var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();
var outboxHttpClient = httpClientFactory.CreateClient();
var outboxService = new OutboxService(outboxHttpClient);
outboxService.StartWorker();

var recoveryManager = new RecoveryManager();

var envelopeQueue = new Queue<string>();
var pairingSessions = new Dictionary<string, (byte[] CorePrivateKey, byte[] DevicePublicKey)>();
var pendingApprovals = new Dictionary<string, PendingApproval>();
var approvalResults = new Dictionary<string, bool>();
var approvalWait = new Dictionary<string, TaskCompletionSource<bool>>();
var jobs = new Dictionary<string, Job>();
var runs = new List<Run>();
var statePath = Path.Combine(Path.GetTempPath(), "archimedes_state.json");

SavedState? LoadStateFromDisk()
{
    if (!File.Exists(statePath)) return null;
    try
    {
        var json = File.ReadAllText(statePath);
        return JsonSerializer.Deserialize<SavedState>(json);
    }
    catch { return null; }
}

void SaveStateToDisk(SavedState s)
{
    File.WriteAllText(statePath, JsonSerializer.Serialize(s));
}

var savedState = LoadStateFromDisk();
if (savedState != null) Console.WriteLine($"[Recovery] Loaded legacy state: job={savedState.JobId} run={savedState.RunId}");

var recoverableRuns = recoveryManager.GetRecoverableRuns();
foreach (var persistedRun in recoverableRuns)
{
    ArchLogger.LogInfo($"[Recovery] Found run {persistedRun.Id} in state {persistedRun.Status}, step={persistedRun.Step}");
    recoveryManager.MarkRecovering(persistedRun.Id);
    
    var run = new Run
    {
        Id = persistedRun.Id,
        JobId = persistedRun.JobId,
        StartTime = persistedRun.StartTime,
        Status = RunStatus.Recovering,
        Step = persistedRun.Step,
        Checkpoint = persistedRun.Checkpoint
    };
    runs.Add(run);
    
    _ = Task.Run(async () =>
    {
        ArchLogger.LogInfo($"[Recovery] Resuming run {run.Id} from step {run.Step}");
        await Task.Delay(500);
        run.Step++;
        recoveryManager.UpdateRunStatus(run.Id, RunStatus.Running, run.Step, $"resumed_step_{run.Step}");
        
        await Task.Delay(100);
        run.Status = RunStatus.Completed;
        run.EndTime = DateTime.UtcNow;
        recoveryManager.UpdateRunStatus(run.Id, RunStatus.Completed);
        ArchLogger.LogInfo($"[Recovery] Run {run.Id} completed after recovery");
    });
}

app.MapGet("/health", () => "OK");

app.MapPost("/state/save", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var j = JsonDocument.Parse(body);
    var jobId = j.RootElement.GetProperty("jobId").GetString() ?? "";
    var runId = j.RootElement.GetProperty("runId").GetString() ?? "";
    savedState = new SavedState { JobId = jobId, RunId = runId, Status = "paused", SavedAt = DateTime.UtcNow };
    SaveStateToDisk(savedState);
    return Results.Json(new { ok = true });
});

app.MapGet("/state/load", () =>
{
    savedState ??= LoadStateFromDisk();
    if (savedState == null) return Results.NotFound();
    return Results.Json(savedState);
});

app.MapPost("/state/clear", () =>
{
    savedState = null;
    if (File.Exists(statePath)) File.Delete(statePath);
    return Results.Json(new { ok = true });
});

app.MapPost("/job", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var payload = await r.ReadToEndAsync();
    var id = Guid.NewGuid().ToString("N");
    jobs[id] = new Job { Id = id, Type = "one-shot", Payload = payload ?? "" };
    return Results.Json(new { jobId = id });
});

app.MapPost("/job/{id}/run", (string id) =>
{
    if (!jobs.TryGetValue(id, out var job))
        return Results.NotFound();
    var runId = Guid.NewGuid().ToString("N");
    var run = new Run { Id = runId, JobId = id, StartTime = DateTime.UtcNow, Status = RunStatus.Running, Step = 0 };
    runs.Add(run);
    recoveryManager.TrackRun(run);
    
    _ = Task.Run(async () =>
    {
        run.Step = 1;
        run.Checkpoint = "step_1_started";
        recoveryManager.UpdateRunStatus(run.Id, RunStatus.Running, run.Step, run.Checkpoint);
        
        await Task.Delay(100);
        
        run.Step = 2;
        run.Checkpoint = "step_2_completed";
        run.EndTime = DateTime.UtcNow;
        run.Status = RunStatus.Completed;
        recoveryManager.UpdateRunStatus(run.Id, RunStatus.Completed, run.Step, run.Checkpoint);
        ArchLogger.LogInfo($"[Scheduler] Run {runId} completed for job {id}");
    });
    return Results.Json(new { runId });
});

app.MapGet("/run/{id}", (string id) =>
{
    var run = runs.FirstOrDefault(r => r.Id == id);
    if (run == null) return Results.NotFound();
    return Results.Json(run);
});

app.MapGet("/recovery/state", () =>
{
    var state = recoveryManager.GetState();
    return Results.Json(new
    {
        runs = state.Runs.Select(r => new
        {
            r.Id,
            r.JobId,
            r.Status,
            r.Step,
            r.Checkpoint,
            r.StartTime,
            r.EndTime
        }),
        state.LastSaved
    });
});

app.MapPost("/recovery/clear", () =>
{
    recoveryManager.ClearAll();
    return Results.Json(new { ok = true });
});

app.MapPost("/job/{id}/run-slow", (string id) =>
{
    if (!jobs.TryGetValue(id, out var job))
        return Results.NotFound();
    var runId = Guid.NewGuid().ToString("N");
    var run = new Run { Id = runId, JobId = id, StartTime = DateTime.UtcNow, Status = RunStatus.Running, Step = 0 };
    runs.Add(run);
    recoveryManager.TrackRun(run);
    
    _ = Task.Run(async () =>
    {
        for (int step = 1; step <= 5; step++)
        {
            run.Step = step;
            run.Checkpoint = $"processing_step_{step}";
            recoveryManager.UpdateRunStatus(run.Id, RunStatus.Running, run.Step, run.Checkpoint);
            ArchLogger.LogInfo($"[Scheduler] Run {runId} step {step}/5");
            await Task.Delay(2000);
        }
        
        run.EndTime = DateTime.UtcNow;
        run.Status = RunStatus.Completed;
        recoveryManager.UpdateRunStatus(run.Id, RunStatus.Completed);
        ArchLogger.LogInfo($"[Scheduler] Run {runId} completed all steps");
    });
    
    return Results.Json(new { runId, message = "Slow run started (5 steps, 2s each)" });
});

var monitorTickCount = 0;
var monitorCts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    while (!monitorCts.Token.IsCancellationRequested)
    {
        await Task.Delay(3000);
        monitorTickCount++;
        Console.WriteLine($"[Monitor] tick {monitorTickCount}");
    }
}, monitorCts.Token);

app.MapGet("/monitor/ticks", () => Results.Json(new { ticks = monitorTickCount }));

app.MapGet("/log-test/fail", () =>
{
    try { throw new InvalidOperationException("Test failure"); }
    catch (Exception ex)
    {
        var summary = ArchLogger.HumanSummary(ex);
        var trace = ArchLogger.MachineTrace(ex);
        return Results.Json(new { humanSummary = summary, machineTrace = trace });
    }
});

app.MapPost("/task/run-with-approval", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var message = await r.ReadToEndAsync() ?? "Proceed?";
    var taskId = Guid.NewGuid().ToString("N");
    var tcs = new TaskCompletionSource<bool>();
    approvalWait[taskId] = tcs;
    pendingApprovals[taskId] = new PendingApproval { TaskId = taskId, Message = message };
    ArchLogger.LogPayload("Task approval request", message);
    var approved = await tcs.Task;
    pendingApprovals.Remove(taskId);
    approvalWait.Remove(taskId);
    Console.WriteLine($"[Core] Task {taskId} resumed, approved={approved}");
    return Results.Json(new { taskId, approved, result = approved ? "completed" : "denied" });
});

app.MapGet("/approvals", () =>
{
    var list = pendingApprovals.Values.ToList();
    return Results.Json(list);
});

app.MapPost("/approval-response", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var json = System.Text.Json.JsonDocument.Parse(body);
    var taskId = json.RootElement.GetProperty("taskId").GetString();
    var approved = json.RootElement.GetProperty("approved").GetBoolean();
    if (string.IsNullOrEmpty(taskId) || !approvalWait.TryGetValue(taskId, out var tcs))
        return Results.NotFound("Unknown task");
    tcs.TrySetResult(approved);
    return Results.Json(new { ok = true });
});

app.MapGet("/pairing-data", () =>
{
    var sessionId = Guid.NewGuid().ToString("N");
    var (pub, priv) = Crypto.GenerateKeyPair();
    pairingSessions[sessionId] = (priv, Array.Empty<byte>());
    var qrContent = System.Text.Json.JsonSerializer.Serialize(new { sessionId, corePublicKey = Convert.ToBase64String(pub) });
    return Results.Json(new { sessionId, corePublicKey = Convert.ToBase64String(pub), qrContent });
});

app.MapPost("/pairing-complete", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var json = System.Text.Json.JsonDocument.Parse(body);
    var sessionId = json.RootElement.GetProperty("sessionId").GetString();
    var devicePublicKeyB64 = json.RootElement.GetProperty("devicePublicKey").GetString();
    if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(devicePublicKeyB64))
        return Results.BadRequest("Missing sessionId or devicePublicKey");
    if (!pairingSessions.TryGetValue(sessionId, out var sess))
        return Results.NotFound("Session expired");
    var devicePub = Convert.FromBase64String(devicePublicKeyB64);
    pairingSessions[sessionId] = (sess.CorePrivateKey, devicePub);
    Console.WriteLine($"[Core] Paired session {sessionId}");
    return Results.Json(new { ok = true });
});

app.MapPost("/envelope", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    envelopeQueue.Enqueue(string.IsNullOrEmpty(body) ? "{}" : body);
    ArchLogger.LogPayload("Envelope received", body);
    return Results.Text("OK", "text/plain");
});

app.MapGet("/envelope", () =>
{
    if (envelopeQueue.TryDequeue(out var msg))
        return Results.Text(msg, "text/plain");
    return Results.Text("", "text/plain");
});

app.MapGet("/ping-net", async (IHttpClientFactory cf) =>
{
    using var client = cf.CreateClient();
    try
    {
        var resp = await client.GetAsync("http://localhost:5052/health");
        var body = await resp.Content.ReadAsStringAsync();
        return Results.Text(body, "text/plain");
    }
    catch (Exception ex)
    {
        return Results.Text($"ERROR: {ex.Message}", statusCode: 500);
    }
});

app.MapPost("/send-envelope", async (HttpRequest req, IHttpClientFactory cf) =>
{
    using var r = new StreamReader(req.Body);
    var payload = await r.ReadToEndAsync();
    using var client = cf.CreateClient();
    try
    {
        var content = new StringContent(payload, System.Text.Encoding.UTF8, "text/plain");
        var resp = await client.PostAsync("http://localhost:5052/envelope", content);
        return Results.Text(await resp.Content.ReadAsStringAsync(), "text/plain");
    }
    catch (Exception ex)
    {
        return Results.Text($"ERROR: {ex.Message}", statusCode: 500);
    }
});

app.MapPost("/crypto-test", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var message = await r.ReadToEndAsync();
    if (string.IsNullOrEmpty(message)) message = "test";
    var (pub, priv) = Crypto.GenerateKeyPair();
    var enc = Crypto.EncryptBase64(message, pub);
    var dec = Crypto.DecryptBase64(enc, priv);
    return Results.Json(new { encrypted = enc, decrypted = dec, ok = dec == message });
});

app.MapPost("/outbox/enqueue", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var json = JsonDocument.Parse(body);
    var operationId = json.RootElement.GetProperty("operationId").GetString() ?? Guid.NewGuid().ToString("N");
    var payload = json.RootElement.GetProperty("payload").GetString() ?? "";
    var destination = json.RootElement.GetProperty("destination").GetString() ?? "http://localhost:5052/envelope";

    var result = outboxService.Enqueue(operationId, payload, destination);
    return Results.Json(new
    {
        ok = result.Success,
        entryId = result.EntryId,
        duplicate = result.IsDuplicate,
        error = result.Error
    });
});

app.MapGet("/outbox/entries", () =>
{
    var entries = outboxService.GetAllEntries().Select(e => new
    {
        e.Id,
        e.OperationId,
        status = e.Status.ToString(),
        e.Attempts,
        e.NextRetry,
        e.Error
    });
    return Results.Json(entries);
});

app.MapGet("/outbox/stats", () =>
{
    var stats = outboxService.GetStats();
    return Results.Json(stats);
});

app.MapPost("/outbox/drain", async () =>
{
    var sent = await outboxService.DrainAsync();
    return Results.Json(new { drained = sent });
});

var port = 5051;
app.Urls.Add($"http://localhost:{port}");
Console.WriteLine($"Archimedes Core listening on http://localhost:{port}");
app.Run();
