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

var encryptedStore = new EncryptedStore();
encryptedStore.Initialize();

var envelopeQueue = new Queue<string>();
var pairingSessions = new Dictionary<string, (byte[] CorePrivateKey, byte[] DevicePublicKey)>();
var pendingApprovals = new Dictionary<string, PendingApproval>();
var approvalResults = new Dictionary<string, bool>();
var approvalWait = new Dictionary<string, TaskCompletionSource<bool>>();
var jobs = new Dictionary<string, Job>();
var runs = new List<Run>();
var statePath = Path.Combine(Path.GetTempPath(), "archimedes_state.json");

var deviceKeyManager = new DeviceKeyManager();
var taskService = new TaskService(encryptedStore, deviceKeyManager);
var policyEngine = new PolicyEngine();
var approvalService = new ApprovalService(deviceKeyManager);
var llmAdapter = new LLMAdapter(httpClientFactory.CreateClient());
var planner = new Planner(llmAdapter, policyEngine);
var smartScheduler = new SmartScheduler(taskService, planner);
smartScheduler.Start();

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

app.MapGet("/v2/approvals", () =>
{
    var pending = approvalService.GetPendingApprovals();
    return Results.Json(pending.Select(a => new
    {
        a.Id,
        a.TaskId,
        type = a.Type.ToString(),
        a.Message,
        hasCaptcha = a.CaptchaImageBase64Encrypted != null,
        a.CreatedAt
    }));
});

app.MapGet("/v2/approval/{id}", (string id) =>
{
    var approval = approvalService.GetApproval(id);
    if (approval == null)
        return Results.NotFound();
    return Results.Json(new
    {
        approval.Id,
        approval.TaskId,
        type = approval.Type.ToString(),
        approval.Message,
        approval.CaptchaImageBase64Encrypted,
        approval.CreatedAt
    });
});

app.MapPost("/v2/approval/{id}/respond", async (string id, HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var json = JsonDocument.Parse(body);
    
    var response = new ApprovalResponse { ApprovalId = id };
    
    if (json.RootElement.TryGetProperty("approved", out var approvedProp))
        response.Approved = approvedProp.GetBoolean();
    if (json.RootElement.TryGetProperty("secretValue", out var secretProp))
        response.SecretValue = secretProp.GetString();
    if (json.RootElement.TryGetProperty("captchaSolution", out var captchaProp))
        response.CaptchaSolution = captchaProp.GetString();
    
    var ok = approvalService.Respond(response);
    return Results.Json(new { ok });
});

app.MapPost("/v2/approval/request/confirmation", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var json = JsonDocument.Parse(body);
    var taskId = json.RootElement.GetProperty("taskId").GetString() ?? "";
    var message = json.RootElement.GetProperty("message").GetString() ?? "";
    
    var response = await approvalService.RequestConfirmation(taskId, message);
    return Results.Json(new { response.Approved });
});

app.MapPost("/v2/approval/request/secret", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var json = JsonDocument.Parse(body);
    var taskId = json.RootElement.GetProperty("taskId").GetString() ?? "";
    var prompt = json.RootElement.GetProperty("prompt").GetString() ?? "";
    
    var response = await approvalService.RequestSecret(taskId, prompt);
    return Results.Json(new { response.Approved, hasSecret = response.SecretValue != null });
});

app.MapPost("/v2/approval/request/captcha", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var json = JsonDocument.Parse(body);
    var taskId = json.RootElement.GetProperty("taskId").GetString() ?? "";
    var imageBase64 = json.RootElement.GetProperty("imageBase64").GetString() ?? "";
    
    var imageBytes = Convert.FromBase64String(imageBase64);
    var response = await approvalService.RequestCaptcha(taskId, imageBytes);
    return Results.Json(new { response.Approved, response.CaptchaSolution });
});

app.MapPost("/v2/approval/simulator/enable", () =>
{
    approvalService.EnableSimulator(request =>
    {
        switch (request.Type)
        {
            case ApprovalType.CONFIRMATION:
                return new ApprovalResponse { ApprovalId = request.Id, Approved = true };
            case ApprovalType.SECRET_INPUT:
                return new ApprovalResponse { ApprovalId = request.Id, Approved = true, SecretValue = "simulated-secret" };
            case ApprovalType.CAPTCHA_DECODE:
                return new ApprovalResponse { ApprovalId = request.Id, Approved = true, CaptchaSolution = "SIMULATED" };
            default:
                return new ApprovalResponse { ApprovalId = request.Id, Approved = false };
        }
    });
    return Results.Json(new { ok = true, mode = "simulator" });
});

app.MapPost("/v2/approval/simulator/disable", () =>
{
    approvalService.DisableSimulator();
    return Results.Json(new { ok = true, mode = "real" });
});

app.MapGet("/llm/health", async () =>
{
    var result = await llmAdapter.HealthCheck();
    return Results.Json(result);
});

app.MapPost("/llm/interpret", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var prompt = await r.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(prompt))
        return Results.BadRequest("Prompt required");
    
    var result = await llmAdapter.Interpret(prompt);
    return Results.Json(result);
});

app.MapPost("/llm/summarize", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var content = await r.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(content))
        return Results.BadRequest("Content required");
    
    var result = await llmAdapter.Summarize(content);
    return Results.Json(result);
});

app.MapPost("/planner/plan", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var request = JsonSerializer.Deserialize<PlannerRequest>(body);
    if (request == null || string.IsNullOrWhiteSpace(request.UserPrompt))
        return Results.BadRequest("userPrompt required");
    
    var taskId = request.TaskId ?? Guid.NewGuid().ToString("N").Substring(0, 12);
    var result = await planner.PlanTask(taskId, request.UserPrompt);
    return Results.Json(result);
});

app.MapPost("/planner/plan-task/{id}", async (string id) =>
{
    var task = taskService.GetTask(id);
    if (task == null)
        return Results.NotFound(new { error = "Task not found" });
    
    var prompt = taskService.GetUserPrompt(id);
    if (string.IsNullOrEmpty(prompt))
        return Results.BadRequest(new { error = "Task has no prompt" });
    
    var result = await planner.PlanTask(id, prompt);
    
    if (result.Success && result.Plan != null)
    {
        taskService.SetPlan(id, new TaskPlanRequest
        {
            Intent = result.Intent,
            Steps = result.Plan.Steps
        });
    }
    
    return Results.Json(result);
});

app.MapGet("/scheduler/stats", () => Results.Json(smartScheduler.GetStats()));

app.MapGet("/availability", () => Results.Json(smartScheduler.CheckAvailability()));

app.MapPost("/scheduler/enqueue/{taskId}", (string taskId, string? priority) =>
{
    var p = priority?.ToUpper() switch
    {
        "SCHEDULED" => TaskPriority.SCHEDULED,
        "BACKGROUND" => TaskPriority.BACKGROUND,
        _ => TaskPriority.IMMEDIATE
    };
    smartScheduler.Enqueue(taskId, p);
    return Results.Json(new { ok = true, taskId, priority = p.ToString() });
});

app.MapPost("/scheduler/monitoring", (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = r.ReadToEnd();
    var request = JsonSerializer.Deserialize<MonitoringRequest>(body);
    if (request == null || string.IsNullOrWhiteSpace(request.TaskId))
        return Results.BadRequest("taskId required");
    
    smartScheduler.RegisterMonitoring(
        request.TaskId,
        request.IntervalMs,
        request.MaxJitterMs ?? 5000,
        request.BackoffMultiplier ?? 1.5);
    
    return Results.Json(new { ok = true, taskId = request.TaskId, intervalMs = request.IntervalMs });
});

app.MapDelete("/scheduler/monitoring/{taskId}", (string taskId) =>
{
    smartScheduler.UnregisterMonitoring(taskId);
    return Results.Json(new { ok = true, taskId });
});

app.MapPost("/scheduler/config", (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = r.ReadToEnd();
    var request = JsonSerializer.Deserialize<SchedulerConfigRequest>(body);
    if (request == null)
        return Results.BadRequest("Invalid config");
    
    smartScheduler.ConfigureLimits(
        request.MaxBrowserConcurrency,
        request.MaxCpuPercent,
        request.MaxMemoryMB,
        request.WatchdogTimeoutSeconds);
    
    return Results.Json(new { ok = true, stats = smartScheduler.GetStats() });
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
    return Results.Json(new { version = 1, algorithm = "RSA-OAEP-SHA256", encrypted = enc, decrypted = dec, ok = dec == message });
});

app.MapPost("/task", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var request = JsonSerializer.Deserialize<CreateTaskRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (request == null)
        return Results.BadRequest("Invalid request");
    
    try
    {
        var task = taskService.CreateTask(request);
        return Results.Json(TaskResponse.FromTask(task));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/task/{id}", (string id) =>
{
    var task = taskService.GetTask(id);
    if (task == null)
        return Results.NotFound();
    return Results.Json(TaskResponse.FromTask(task));
});

app.MapGet("/tasks", (HttpRequest req) =>
{
    TaskState? stateFilter = null;
    if (req.Query.TryGetValue("state", out var stateVal) && 
        Enum.TryParse<TaskState>(stateVal.FirstOrDefault(), true, out var parsed))
    {
        stateFilter = parsed;
    }
    
    var tasks = taskService.GetTasks(stateFilter);
    return Results.Json(tasks.Select(TaskResponse.FromTask));
});

app.MapPost("/task/{id}/plan", async (string id, HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var request = JsonSerializer.Deserialize<TaskPlanRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (request == null)
        return Results.BadRequest("Invalid plan request");
    
    try
    {
        var task = taskService.SetPlan(id, request);
        if (task == null)
            return Results.NotFound();
        return Results.Json(TaskResponse.FromTask(task));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPost("/task/{id}/run", (string id) =>
{
    try
    {
        var task = taskService.StartRun(id);
        if (task == null)
            return Results.NotFound();
        return Results.Json(TaskResponse.FromTask(task));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPost("/task/{id}/pause", (string id) =>
{
    try
    {
        var task = taskService.Pause(id);
        if (task == null)
            return Results.NotFound();
        return Results.Json(TaskResponse.FromTask(task));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPost("/task/{id}/resume", (string id) =>
{
    try
    {
        var task = taskService.Resume(id);
        if (task == null)
            return Results.NotFound();
        return Results.Json(TaskResponse.FromTask(task));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPost("/task/{id}/cancel", (string id) =>
{
    try
    {
        var task = taskService.Cancel(id);
        if (task == null)
            return Results.NotFound();
        return Results.Json(TaskResponse.FromTask(task));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapGet("/policy/rules", () =>
{
    var rules = policyEngine.GetRules();
    return Results.Json(rules.Select(r => new
    {
        r.Id,
        r.Description,
        r.DomainPattern,
        r.DomainAllowlist,
        r.DomainDenylist,
        entityScope = r.EntityScope?.ToString(),
        actionKind = r.ActionKind?.ToString(),
        decision = r.Decision.ToString(),
        r.Priority,
        r.Enabled
    }));
});

app.MapPost("/policy/rules", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var rule = JsonSerializer.Deserialize<PolicyRule>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (rule == null)
        return Results.BadRequest("Invalid rule");
    
    policyEngine.AddRule(rule);
    return Results.Json(new { ok = true, ruleId = rule.Id });
});

app.MapDelete("/policy/rules/{id}", (string id) =>
{
    var removed = policyEngine.RemoveRule(id);
    return Results.Json(new { ok = removed });
});

app.MapPost("/policy/evaluate", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var request = JsonSerializer.Deserialize<PolicyEvaluationRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (request == null)
        return Results.BadRequest("Invalid request");
    
    var result = policyEngine.Evaluate(request);
    return Results.Json(new
    {
        decision = result.Decision.ToString(),
        result.MatchedRuleId,
        result.Reason,
        result.EvaluatedRules
    });
});

app.MapPost("/crypto/v2/test", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var message = await r.ReadToEndAsync();
    if (string.IsNullOrEmpty(message)) message = "test secret message";

    var keys = deviceKeyManager.GetOrCreateKeyPair();
    var deviceId = "core-device";
    var operationId = Guid.NewGuid().ToString("N");

    var envelope = ModernCrypto.Encrypt(message, keys.PublicKey, deviceId, operationId);
    var decrypted = ModernCrypto.Decrypt(envelope, keys.PrivateKey);

    return Results.Json(new
    {
        version = 2,
        algorithm = "X25519+ChaCha20-Poly1305",
        envelope = new
        {
            envelope.Version,
            envelope.DeviceId,
            envelope.OperationId,
            envelope.Timestamp,
            envelope.Nonce,
            ciphertextLength = envelope.Ciphertext.Length,
            ephemeralPublicKeyLength = envelope.EphemeralPublicKey.Length
        },
        decrypted,
        ok = decrypted == message,
        plaintextNotInEnvelope = !envelope.Ciphertext.Contains(message)
    });
});

app.MapGet("/crypto/v2/publickey", () =>
{
    var publicKey = deviceKeyManager.GetPublicKeyBase64();
    return Results.Json(new { publicKey, algorithm = "X25519" });
});

app.MapPost("/crypto/v2/encrypt", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var json = JsonDocument.Parse(body);
    
    var message = json.RootElement.GetProperty("message").GetString() ?? "";
    var recipientPublicKeyB64 = json.RootElement.GetProperty("recipientPublicKey").GetString() ?? "";
    var deviceId = json.RootElement.TryGetProperty("deviceId", out var d) ? d.GetString() ?? "unknown" : "unknown";
    var operationId = json.RootElement.TryGetProperty("operationId", out var o) ? o.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");

    var recipientPublicKey = Convert.FromBase64String(recipientPublicKeyB64);
    var envelopeJson = ModernCrypto.EncryptToJson(message, recipientPublicKey, deviceId, operationId);

    return Results.Text(envelopeJson, "application/json");
});

app.MapPost("/crypto/v2/decrypt", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var envelopeJson = await r.ReadToEndAsync();
    
    var keys = deviceKeyManager.GetOrCreateKeyPair();
    var decrypted = ModernCrypto.DecryptFromJson(envelopeJson, keys.PrivateKey);

    return Results.Json(new { decrypted });
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

app.MapGet("/store/stats", () =>
{
    var stats = encryptedStore.GetStats();
    return Results.Json(stats);
});

app.MapPost("/store/test", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    
    var testJobId = Guid.NewGuid().ToString("N");
    var testJob = new Job { Id = testJobId, Type = "test", Payload = body ?? "test" };
    encryptedStore.SaveJob(testJob);
    
    var loaded = encryptedStore.GetJob(testJobId);
    var stats = encryptedStore.GetStats();
    
    return Results.Json(new
    {
        ok = loaded != null && loaded.Payload == testJob.Payload,
        jobId = testJobId,
        isEncrypted = stats.IsEncrypted,
        stats
    });
});

var port = 5051;
app.Urls.Add($"http://localhost:{port}");
Console.WriteLine($"Archimedes Core listening on http://localhost:{port}");
app.Run();
