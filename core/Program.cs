using System.Text.Json;
using Archimedes.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var appStartTime = DateTime.UtcNow;

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
var llmAdapter = new LLMAdapter();

// Phase 21: Procedure Memory
var procedureStore = new ProcedureStore();

// Phase 24: Failure Dialogue
var failureDialogueStore = new FailureDialogueStore();

// Phase 25: Availability Engine
var availabilityStore  = new AvailabilityStore();
var availabilityEngine = new AvailabilityEngine(availabilityStore);

var planner = new Planner(llmAdapter, policyEngine, procedureStore);

// Phase 27: Autonomous Tool Acquisition
var toolStore          = new ToolStore();
var sourceStore        = new SourceStore();
var sourceIntelligence = new SourceIntelligence(sourceStore);
var acquisitionHttp    = httpClientFactory.CreateClient();
var searchOrchestrator = new SearchOrchestrator(acquisitionHttp, llmAdapter, sourceIntelligence);
var toolEvaluator      = new ToolEvaluator(llmAdapter, acquisitionHttp);
var legalityChecker    = new LegalityChecker(llmAdapter, toolStore);
var toolInstaller      = new ToolInstaller(toolStore, procedureStore, llmAdapter);
var toolGapDetector    = new ToolGapDetector(toolStore, procedureStore);
var toolAcquisitionEngine = new ToolAcquisitionEngine(
    toolGapDetector, searchOrchestrator, toolEvaluator,
    legalityChecker, toolInstaller, toolStore, sourceIntelligence);

// Phase 26: Goal Layer + Adaptive Planner
var goalStore  = new GoalStore();
var goalEngine = new GoalEngine(goalStore, taskService, llmAdapter, availabilityEngine, failureDialogueStore);
var goalRunner = new GoalRunner(goalStore, goalEngine);
goalRunner.Start();
var smartScheduler = new SmartScheduler(taskService, planner);
smartScheduler.Start();

// Phase 28: Machine Migration (Octopus)
var migrationDataRoot  = Environment.GetEnvironmentVariable("ARCHIMEDES_DATA_PATH")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Archimedes");
var migrationHttp      = httpClientFactory.CreateClient();
var migrationPackager  = new MigrationStatePackager(taskService, goalStore, encryptedStore, deviceKeyManager, migrationDataRoot);
var migrationDiskChk   = new MigrationDiskChecker(migrationHttp);
var taskSuspender      = new TaskSuspender(taskService);
var migrationDeployer  = new MigrationDeployer(migrationHttp);
var migrationEngine    = new MigrationEngine(migrationPackager, migrationDiskChk, taskSuspender, migrationDeployer);
var migrationResumer   = new MigrationResumeEngine(migrationDataRoot, encryptedStore, deviceKeyManager, taskService, goalStore);
// Detect and apply migration continuation log from previous machine (runs once, idempotent)
migrationResumer.TryResume();

var storageConfig = new StorageConfig
{
    RootInternal = Environment.GetEnvironmentVariable("ARCHIMEDES_STORAGE_INTERNAL")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Archimedes"),
    RootExternal = Environment.GetEnvironmentVariable("ARCHIMEDES_STORAGE_EXTERNAL"),
    LogsRetentionDays = int.TryParse(Environment.GetEnvironmentVariable("ARCHIMEDES_LOGS_RETENTION_DAYS"), out var lrd) ? lrd : 7,
    ArtifactsMaxGB = int.TryParse(Environment.GetEnvironmentVariable("ARCHIMEDES_ARTIFACTS_MAX_GB"), out var amg) ? amg : 10,
    MinFreeSpaceMB = int.TryParse(Environment.GetEnvironmentVariable("ARCHIMEDES_MIN_FREE_MB"), out var mfm) ? mfm : 500
};
var storageManager = new StorageManager(storageConfig);
// Phase 19: Observability
var traceService = new TraceService();

// Repo root detection (used by Phase 29/34 CodePatcher and Phase 15/16 SandboxRunner)
var repoRoot = Environment.GetEnvironmentVariable("ARCHIMEDES_REPO_ROOT");
if (string.IsNullOrEmpty(repoRoot))
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "scripts", "phase14-ready-gate.ps1")))
        d = d.Parent;
    repoRoot = d?.FullName ?? Path.GetDirectoryName(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
}

// Phase 29: Autonomous Self-Improvement Engine (24/7, lowest priority)
var selfImprovementStore  = new SelfImprovementStore();
var resourceGuard         = new ResourceGuard();
var selfAnalyzer          = new SelfAnalyzer(procedureStore, toolStore, traceService);
var selfGitManager        = new SelfGitManager();
// Phase 34: Code Patcher — reuses searchOrchestrator (already instantiated) + selfGitManager + repoRoot
var codePatcher = new CodePatcher(llmAdapter, selfGitManager, repoRoot);
var selfImprovementEngine = new SelfImprovementEngine(
    selfAnalyzer, selfImprovementStore, resourceGuard,
    procedureStore, toolStore, traceService, llmAdapter, selfGitManager,
    searchOrchestrator,   // Phase 34: real web research (DuckDuckGo + Tor)
    codePatcher);         // Phase 34: real code patching (LLM → build → test → commit)
selfImprovementEngine.Start();

// Phase 30: Ubuntu OS Autonomy Engine
var hardwareMonitor = new HardwareMonitor();
var aptManager      = new AptManager();
var osManager       = new OsManager(hardwareMonitor, aptManager, storageManager);
osManager.Start();

// Phase 31: Android Bridge — polls Net service for commands from Android app
var androidBridgeHttp = httpClientFactory.CreateClient();
var androidBridge     = new AndroidBridge(androidBridgeHttp, taskService, goalEngine, llmAdapter);
androidBridge.Start();

// Phase 32: Android App OTA Updater (ADB WiFi)
var appUpdater = new AppUpdater(httpClientFactory.CreateClient(), androidBridge);
// Phase 32+: Wire Android app into self-improvement — Archimedes monitors its own mobile component
selfImprovementEngine.SetAppUpdater(appUpdater);

// Episodic memory — every chat interaction stored and recalled for long-term learning
var eventMemory = new EventMemory(storageManager.RootInternal);
eventMemory.Initialize();
selfImprovementEngine.SetEventMemory(eventMemory); // long-term learning from chat

// Phase 20: Success Criteria Engine
var criteriaEngine = new SuccessCriteriaEngine();

var taskRunner = new TaskRunner(taskService, planner, httpClientFactory.CreateClient(),
    storageManager, traceService, criteriaEngine, procedureStore, failureDialogueStore);
taskRunner.Start();

var selfUpdateAudit = new SelfUpdateAudit(storageManager.RootInternal);
var sandboxRoot = Environment.GetEnvironmentVariable("ARCHIMEDES_SANDBOX_ROOT");
if (string.IsNullOrWhiteSpace(sandboxRoot))
    sandboxRoot = Path.Combine(Path.GetTempPath(), "ArchimedesSandbox");
sandboxRoot = Path.GetFullPath(sandboxRoot);
var releasesRoot = Path.Combine(storageManager.RootInternal, "releases");
// repoRoot already computed above (Phase 29/34 section)
var sandboxRunner = new SandboxRunner(sandboxRoot, repoRoot, selfUpdateAudit);
var promotionManager = new PromotionManager(releasesRoot, selfUpdateAudit);

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

// ── Phase 19: Observability middleware ────────────────────────────────────────
// Every request gets a CorrelationId, a trace is opened and closed automatically.
app.Use(async (ctx, next) =>
{
    var corrId = ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault()
        ?? Guid.NewGuid().ToString("N")[..16];

    ctx.Items["CorrelationId"] = corrId;
    ctx.Response.Headers["X-Correlation-Id"] = corrId;

    traceService.Begin(corrId, ctx.Request.Path, ctx.Request.Method);

    try
    {
        await next();
        var success = ctx.Response.StatusCode < 400;
        traceService.Complete(corrId, success, ctx.Response.StatusCode);
    }
    catch (Exception ex)
    {
        traceService.Complete(corrId, false, 500, FailureCode.STEP_EXECUTION_FAILED, ex.Message);
        throw;
    }
});

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

// ── Conversation history (short-term memory) ──────────────────────────────────
// Keeps the last MAX_HISTORY exchanges so Archimedes remembers context within
// a session. Each entry is (role, content) where role = "user" | "assistant".
var chatHistory    = new List<(string Role, string Content)>();
const int MAX_HISTORY_TURNS = 4;  // 4 user + 4 assistant = 8 messages — keeps context lean

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
    var corrId = req.HttpContext.Items["CorrelationId"]?.ToString() ?? "unknown";

    using var r = new StreamReader(req.Body);
    var prompt = await r.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(prompt))
        return Results.BadRequest("Prompt required");

    traceService.BeginStep(corrId, "LLM.Interpret");
    try
    {
        var result = await llmAdapter.Interpret(prompt);
        var code = result.IsHeuristicFallback ? FailureCode.INTENT_AMBIGUOUS : FailureCode.None;
        traceService.CompleteStep(corrId, "LLM.Interpret", true, code,
            $"intent={result.Intent} confidence={result.Confidence:F2} heuristic={result.IsHeuristicFallback}");
        return Results.Json(result);
    }
    catch (Exception ex)
    {
        traceService.CompleteStep(corrId, "LLM.Interpret", false, FailureCode.LLM_INFERENCE_ERROR, ex.Message);
        throw;
    }
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
    var corrId = req.HttpContext.Items["CorrelationId"]?.ToString() ?? "unknown";

    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var request = JsonSerializer.Deserialize<PlannerRequest>(body);
    if (request == null || string.IsNullOrWhiteSpace(request.UserPrompt))
        return Results.BadRequest("userPrompt required");

    var taskId = request.TaskId ?? Guid.NewGuid().ToString("N").Substring(0, 12);

    traceService.BeginStep(corrId, "Planner.Plan");
    try
    {
        var result = await planner.PlanTask(taskId, request.UserPrompt);
        var code = result.Success ? FailureCode.None : FailureCode.PLAN_GENERATION_FAILED;
        traceService.CompleteStep(corrId, "Planner.Plan", result.Success, code,
            $"intent={result.Intent} steps={result.Plan?.Steps.Count ?? 0}");
        return Results.Json(result);
    }
    catch (Exception ex)
    {
        traceService.CompleteStep(corrId, "Planner.Plan", false, FailureCode.PLAN_GENERATION_FAILED, ex.Message);
        throw;
    }
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

// Scheduler config - GET returns current config, POST updates
app.MapGet("/scheduler/config", () =>
{
    var runnerConfig = taskRunner.GetConfig();
    return Results.Json(new
    {
        runnerIntervalMs = runnerConfig.RunnerIntervalMs,
        watchdogSeconds = runnerConfig.WatchdogSeconds,
        maxTasksPerTick = runnerConfig.MaxTasksPerTick,
        tickBudgetMs = runnerConfig.TickBudgetMs,
        schedulerStats = smartScheduler.GetStats()
    });
});

app.MapPost("/scheduler/config", async (HttpRequest req) =>
{
    try
    {
        using var r = new StreamReader(req.Body);
        var body = await r.ReadToEndAsync();
        
        if (string.IsNullOrWhiteSpace(body) || body == "{}")
        {
            return Results.Json(new
            {
                ok = true,
                message = "No changes applied",
                config = taskRunner.GetConfig()
            });
        }
        
        var request = JsonSerializer.Deserialize<RunnerConfig>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (request == null)
        {
            return Results.BadRequest(new { error = "Invalid JSON format" });
        }
        
        // Validate inputs
        var errors = new List<string>();
        if (request.RunnerIntervalMs.HasValue && request.RunnerIntervalMs.Value < 100)
            errors.Add("runnerIntervalMs must be >= 100");
        if (request.WatchdogSeconds.HasValue && request.WatchdogSeconds.Value < 30)
            errors.Add("watchdogSeconds must be >= 30");
        if (request.MaxTasksPerTick.HasValue && request.MaxTasksPerTick.Value < 1)
            errors.Add("maxTasksPerTick must be >= 1");
        if (request.TickBudgetMs.HasValue && request.TickBudgetMs.Value < 100)
            errors.Add("tickBudgetMs must be >= 100");
        
        if (errors.Any())
        {
            return Results.BadRequest(new { error = "Validation failed", details = errors });
        }
        
        taskRunner.Configure(request);
        
        return Results.Json(new { ok = true, config = taskRunner.GetConfig() });
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = "Invalid JSON", details = ex.Message });
    }
    catch (Exception ex)
    {
        ArchLogger.LogError($"[API] /scheduler/config error: {ex.Message}");
        return Results.BadRequest(new { error = "Configuration error", details = ex.Message });
    }
});

// Debug endpoints
app.MapGet("/task/{id}/trace", (string id) =>
{
    var trace = taskRunner.GetTaskTrace(id);
    if (trace == null)
        return Results.NotFound(new { error = "Task not found" });
    return Results.Json(trace);
});

// ── Phase 19: Observability — Trace API ──────────────────────────────────────
app.MapGet("/traces", (int? limit) =>
{
    var count = Math.Min(limit ?? 20, 100);
    var recent = traceService.GetRecent(count);
    return Results.Json(new
    {
        count        = recent.Count,
        activeCount  = traceService.ActiveCount,
        bufferCount  = traceService.CompletedCount,
        traces       = recent.Select(t => new
        {
            t.CorrelationId,
            t.Endpoint,
            t.Method,
            t.StartedAtUtc,
            t.TotalDurationMs,
            t.Success,
            t.HttpStatusCode,
            failureCode  = t.FailureCode.ToString(),
            stepCount    = t.Steps.Count,
            t.TaskId
        })
    });
});

app.MapGet("/traces/{correlationId}", (string correlationId) =>
{
    var trace = traceService.Get(correlationId);
    if (trace == null)
        return Results.NotFound(new { error = $"Trace '{correlationId}' not found" });

    return Results.Json(new
    {
        trace.CorrelationId,
        trace.TaskId,
        trace.Endpoint,
        trace.Method,
        trace.StartedAtUtc,
        trace.CompletedAtUtc,
        trace.TotalDurationMs,
        trace.Success,
        trace.HttpStatusCode,
        failureCode    = trace.FailureCode.ToString(),
        trace.FailureMessage,
        steps = trace.Steps.Select(s => new
        {
            s.Index,
            s.Name,
            s.StartedAtUtc,
            s.CompletedAtUtc,
            s.DurationMs,
            s.Success,
            failureCode = s.FailureCode.ToString(),
            s.Details,
            s.Outcome,    // Phase 20
            s.Evidence    // Phase 20
        })
    });
});

// ── Phase 20: Success Criteria Engine ────────────────────────────────────────

// Standalone verification — test a step result against criteria
app.MapPost("/criteria/verify", async (HttpRequest req) =>
{
    using var r    = new StreamReader(req.Body);
    var body       = await r.ReadToEndAsync();
    var request    = JsonSerializer.Deserialize<CriteriaVerifyRequest>(body);
    if (request == null || string.IsNullOrWhiteSpace(request.Action))
        return Results.BadRequest(new { error = "action required" });

    var step   = new PlanStep { Action = request.Action, Index = 0 };
    var result = new StepExecutionResult { Success = request.StepSuccess, Data = request.Data };
    var vr     = criteriaEngine.Verify(step, result);

    return Results.Json(new
    {
        action           = request.Action,
        outcome          = vr.Outcome.ToString(),
        vr.Evidence,
        vr.ExpectedCriteria,
        vr.FailureReason
    });
});

// Get the full outcome for a completed task (reads from task trace)
app.MapGet("/task/{id}/outcome", (string id) =>
{
    var task = taskService.GetTask(id);
    if (task == null)
        return Results.NotFound(new { error = "Task not found" });

    var trace = traceService.Get($"task_{id}");
    if (trace == null)
        return Results.Json(new
        {
            taskId         = id,
            state          = task.State.ToString(),
            overallOutcome = "NO_TRACE",
            stepCount      = 0,
            steps          = Array.Empty<object>()
        });

    var stepOutcomes = trace.Steps
        .Where(s => s.Name.StartsWith("Step"))
        .Select(s => new
        {
            s.Name,
            s.Success,
            outcome  = s.Outcome ?? "NOT_APPLICABLE",
            evidence = s.Evidence,
            s.DurationMs
        })
        .ToList();

    // Overall outcome: VERIFIED if all steps verified/unverified, FAILED_VERIFY if any failed
    var overallOutcome = stepOutcomes.Any(s => s.outcome == "FAILED_VERIFY")
        ? "FAILED_VERIFY"
        : stepOutcomes.Any(s => s.outcome == "PARTIAL")
        ? "PARTIAL"
        : stepOutcomes.Any(s => s.outcome == "VERIFIED")
        ? "VERIFIED"
        : "UNVERIFIED";

    return Results.Json(new
    {
        taskId         = id,
        state          = task.State.ToString(),
        overallOutcome,
        stepCount      = stepOutcomes.Count,
        steps          = stepOutcomes
    });
});

// ── Phase 22: Chat UI ─────────────────────────────────────────────────────────

// GET /chat — serve the self-contained chat interface
app.MapGet("/chat", () =>
    Results.Content(ChatHtml.Page, "text/html; charset=utf-8"));

// GET /system/metrics — CPU, RAM, uptime for the top bar
app.MapGet("/system/metrics", () =>
    Results.Json(new
    {
        cpuPercent    = SystemMetricsHelper.GetCpuPercent(),
        ramUsedMb     = SystemMetricsHelper.GetRamUsedMb(),
        ramTotalMb    = SystemMetricsHelper.GetRamTotalMb(),
        uptimeSeconds = SystemMetricsHelper.GetUptimeSeconds()
    }));

// GET /status/current — what Archimedes is doing right now (task + self-improvement)
app.MapGet("/status/current", () =>
{
    var activity    = traceService.GetLatestActivity();
    var selfDevDesc = selfImprovementEngine.GetCurrentActivityDescription();

    if (activity == null)
    {
        return Results.Json(new
        {
            active      = selfDevDesc != null,
            endpoint    = (string?)null,
            step        = (string?)null,
            description = (string?)null,
            selfDev     = selfDevDesc,
            osHealth    = new {
                isLinux        = OperatingSystem.IsLinux(),
                rebootRequired = OsManager.IsRebootRequired(),
                state          = osManager.GetStatus().State.ToString()
            },
            androidBridge = new { polling = true }
        });
    }

    var (endpoint, stepName) = activity.Value;
    var description = stepName switch
    {
        "LLM.Interpret" => "מנתח intent...",
        "Planner.Plan"  => "בונה תוכנית ביצוע...",
        "Task.Execute"  => "מבצע משימה...",
        _               => !string.IsNullOrEmpty(stepName) ? stepName : "עובד..."
    };

    return Results.Json(new
    {
        active      = true,
        endpoint,
        step        = stepName,
        description,
        selfDev     = selfDevDesc,
        osHealth    = new {
            isLinux        = OperatingSystem.IsLinux(),
            rebootRequired = OsManager.IsRebootRequired(),
            state          = osManager.GetStatus().State.ToString()
        },
        androidBridge = new { polling = true }
    });
});

// Phase 31: Core → Android notification relay
app.MapPost("/android/notify", async (HttpRequest req) =>
{
    using var sr  = new StreamReader(req.Body);
    var body      = await sr.ReadToEndAsync();
    var json      = JsonDocument.Parse(body);
    var title     = json.RootElement.TryGetProperty("title",    out var t) ? t.GetString() ?? "" : "";
    var notifBody = json.RootElement.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "";

    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(notifBody))
        return Results.BadRequest(new { error = "title and body are required" });

    Dictionary<string, string>? data = null;
    if (json.RootElement.TryGetProperty("data", out var dataEl))
    {
        data = new Dictionary<string, string>();
        foreach (var prop in dataEl.EnumerateObject())
            data[prop.Name] = prop.Value.GetString() ?? "";
    }

    await androidBridge.NotifyAsync(title, notifBody, data);
    return Results.Json(new { ok = true, title });
});

// Phase 32: POST /android/update — trigger OTA update via ADB WiFi
app.MapPost("/android/update", async (HttpRequest req) =>
{
    using var sr = new StreamReader(req.Body);
    var body     = await sr.ReadToEndAsync();
    string? deviceId = null;
    string? phoneIp  = null;
    if (!string.IsNullOrWhiteSpace(body))
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("deviceId", out var d)) deviceId = d.GetString();
            if (doc.RootElement.TryGetProperty("phoneIp",  out var p)) phoneIp  = p.GetString();
        }
        catch { /* optional body */ }
    }
    var result = await appUpdater.StartUpdateAsync(deviceId, phoneIp);
    return Results.Json(result);
});

// Phase 32: GET /android/update/status — check OTA update progress
app.MapGet("/android/update/status", () =>
    Results.Json(appUpdater.GetStatus()));

// POST /chat/message — routes a user message through LLM → optionally creates a task
app.MapPost("/chat/message", async (HttpRequest req) =>
{
    using var sr   = new StreamReader(req.Body);
    var body       = await sr.ReadToEndAsync();
    var chatReq    = JsonSerializer.Deserialize<ChatMessageRequest>(
        body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    if (chatReq == null || string.IsNullOrWhiteSpace(chatReq.Message))
        return Results.BadRequest("message required");

    // Phase 25: record user interaction so availability engine can learn patterns
    availabilityEngine.RecordInteraction("chat");

    var corrId = Guid.NewGuid().ToString("N")[..12];
    traceService.Begin(corrId, "/chat/message", "POST");
    traceService.BeginStep(corrId, "LLM.Interpret");

    try
    {
        var result = await llmAdapter.Interpret(chatReq.Message);
        var fc     = result.IsHeuristicFallback ? FailureCode.INTENT_AMBIGUOUS : FailureCode.None;
        traceService.CompleteStep(corrId, "LLM.Interpret", true, fc,
            $"intent={result.Intent} confidence={result.Confidence:F2}");

        var intent     = result.Intent ?? "UNKNOWN";
        var confidence = result.Confidence;

        string   reply;
        string?  taskId  = null;
        string?  goalId  = null;

        var supported = new[] { "TESTSITE_EXPORT", "TESTSITE_MONITOR", "LOGIN_FLOW", "FILE_DOWNLOAD" };

        // Phase 26: detect goal-creation requests by keywords
        var msg = chatReq.Message;
        bool isGoalRequest =
            msg.Contains("מטרה", StringComparison.OrdinalIgnoreCase)  ||
            msg.Contains("תמיד", StringComparison.OrdinalIgnoreCase)  ||
            msg.Contains("כל שעה", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("goal", StringComparison.OrdinalIgnoreCase)  ||
            msg.Contains("monitor", StringComparison.OrdinalIgnoreCase) && msg.Contains("continuously", StringComparison.OrdinalIgnoreCase);

        if (isGoalRequest)
        {
            var goal = await goalEngine.CreateAsync(new CreateGoalRequest { UserPrompt = msg });
            goalId = goal.GoalId;
            reply  = $"יצרתי מטרה חדשה: \"{goal.Title}\"\nזיהיתי: {goal.Intent} | מזהה: {goal.GoalId}";
        }
        else if (supported.Contains(intent))
        {
            var task = taskService.CreateTask(new CreateTaskRequest
            {
                Title      = chatReq.Message.Length > 50
                    ? chatReq.Message[..50] + "…"
                    : chatReq.Message,
                UserPrompt = chatReq.Message
            });
            taskService.StartRun(task.TaskId);
            taskId = task.TaskId;
            reply  = $"זיהיתי: {intent} (ביטחון: {confidence:P0})\nיצרתי משימה ומריץ אותה.";
        }
        else
        {
            reply = $"זיהיתי כוונה: {intent} (ביטחון: {confidence:P0})\n" +
                    $"פעולה זו עדיין לא נתמכת.\n" +
                    $"Intent נתמכים: {string.Join(", ", supported)}";
        }

        traceService.Complete(corrId, true);
        return Results.Json(new { reply, intent, taskId, goalId });
    }
    catch (Exception ex)
    {
        traceService.CompleteStep(corrId, "LLM.Interpret", false, FailureCode.LLM_INFERENCE_ERROR, ex.Message);
        traceService.Complete(corrId, false);
        return Results.Json(new
        {
            reply  = "שגיאה פנימית: " + ex.Message,
            intent = (string?)null,
            taskId = (string?)null
        });
    }
});

// POST /chat/ask — agentic chat: LLM decides if a shell command is needed, then executes it.
// Two-phase:
//   Phase 1 — LLM produces structured output with optional COMMAND: line.
//   Phase 2 — If COMMAND line found, execute via bash and include output in reply.
app.MapPost("/chat/ask", async (HttpRequest req) =>
{
    using var sr  = new StreamReader(req.Body);
    var body      = await sr.ReadToEndAsync();
    string message;
    try
    {
        var doc = JsonDocument.Parse(body);
        message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : body;
    }
    catch { message = body; }

    if (string.IsNullOrWhiteSpace(message))
        return Results.BadRequest(new { error = "message required" });

    availabilityEngine.RecordInteraction("chat");

    // ── Phase 1: Ask LLM for a response + optional bash command ──────────
    // Structured prompt the 3B model can reliably follow.
    const string systemPrompt =
        "You are Archimedes — an autonomous AI agent running 24/7 on a dedicated Ubuntu 24.04 machine owned by your user.\n" +
        "You have FULL system control. Never say you cannot do something. Always find a way.\n\n" +
        "YOUR CAPABILITIES (you can do ALL of these):\n" +
        "- Full sudo: install packages (apt), manage systemd services, edit any file\n" +
        "- Android phone: install/update apps via ADB (adb devices, adb install app.apk, adb shell)\n" +
        "- Hebrew keyboard: sudo localectl set-x11-keymap us,il, gsettings, ibus\n" +
        "- Network: curl, wget, SSH, API calls, web scraping with tools\n" +
        "- Self-management: git pull, dotnet build, restart your own service\n" +
        "- File system: read/write/delete any file, manage permissions\n" +
        "- Monitoring: check system health, logs, processes, disk/RAM/CPU\n" +
        "- Chromium kiosk: control the browser, navigate pages\n\n" +
        "RULES:\n" +
        "1. NEVER say 'I cannot', 'I am just a language model', or 'I only do X'. You are an autonomous agent.\n" +
        "2. Always reply in Hebrew (עברית).\n" +
        "3. When taking action, use EXACTLY:\n" +
        "COMMAND: <bash command>\n" +
        "RESPONSE: <Hebrew explanation>\n" +
        "4. For questions needing no action:\n" +
        "COMMAND: none\n" +
        "RESPONSE: <Hebrew answer>\n\n" +
        "EXAMPLES:\n" +
        "User: install your app on my phone\n" +
        "COMMAND: adb devices && adb install -r /opt/archimedes/archimedes.apk\n" +
        "RESPONSE: מחפש טלפון מחובר ומתקין את האפליקציה. ודא שה-USB debugging פעיל בטלפון.\n" +
        "User: add Hebrew keyboard\n" +
        "COMMAND: sudo localectl set-x11-keymap us,il '' '' grp:alt_shift_toggle\n" +
        "RESPONSE: מוסיף עברית למקלדת — Alt+Shift להחלפה.\n" +
        "User: install vim\n" +
        "COMMAND: sudo apt-get install -y vim\n" +
        "RESPONSE: מתקין vim.";

    var llmRaw = await llmAdapter.AskAsync(systemPrompt, message, maxTokens: 300);

    if (string.IsNullOrWhiteSpace(llmRaw))
        return Results.Json(new { reply = "המודל עדיין נטען — נסה שוב בעוד כמה שניות.", command = (string?)null, output = (string?)null });

    // ── Parse COMMAND: / RESPONSE: from LLM output ───────────────────────
    string? bashCommand = null;
    string  hebrewReply = llmRaw.Trim();

    var lines = llmRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    foreach (var line in lines)
    {
        if (line.StartsWith("COMMAND:", StringComparison.OrdinalIgnoreCase))
        {
            var cmd = line["COMMAND:".Length..].Trim();
            if (!string.IsNullOrEmpty(cmd) && !cmd.Equals("none", StringComparison.OrdinalIgnoreCase))
                bashCommand = cmd;
        }
        else if (line.StartsWith("RESPONSE:", StringComparison.OrdinalIgnoreCase))
        {
            hebrewReply = line["RESPONSE:".Length..].Trim();
        }
    }

    // ── Phase 2: Execute command if present ──────────────────────────────
    string? cmdOutput = null;
    if (!string.IsNullOrEmpty(bashCommand))
    {
        ArchLogger.LogInfo($"[Chat] Executing: {bashCommand}");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("bash", new[] { "-c", bashCommand })
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var sb = new System.Text.StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await proc.WaitForExitAsync(cts.Token);
            cmdOutput = sb.ToString().Trim();
            var exitOk = proc.ExitCode == 0;
            if (!exitOk) hebrewReply += $"\n⚠ הפקודה הסתיימה עם שגיאה (קוד {proc.ExitCode}).";
            ArchLogger.LogInfo($"[Chat] Command exit={proc.ExitCode} output={cmdOutput?[..Math.Min(200, cmdOutput?.Length ?? 0)]}");
        }
        catch (Exception ex)
        {
            cmdOutput = "שגיאה: " + ex.Message;
            hebrewReply += "\n⚠ לא הצלחתי להריץ את הפקודה: " + ex.Message;
        }
    }

    return Results.Json(new { reply = hebrewReply, command = bashCommand, output = cmdOutput });
});

// POST /chat/stream — streaming chat via Server-Sent Events (SSE)
// Tokens appear in the dashboard one-by-one as the LLM generates them.
// Flow: LLM streams COMMAND:/RESPONSE: format → at done, execute command → send final event.
app.MapPost("/chat/stream", async (HttpRequest req, HttpResponse res) =>
{
    using var sr = new StreamReader(req.Body);
    var body     = await sr.ReadToEndAsync();
    string message;
    try
    {
        var doc = JsonDocument.Parse(body);
        message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : body;
    }
    catch { message = body; }

    if (string.IsNullOrWhiteSpace(message))
    { res.StatusCode = 400; return; }

    availabilityEngine.RecordInteraction("chat");

    // SSE headers
    res.ContentType = "text/event-stream; charset=utf-8";
    res.Headers["Cache-Control"]      = "no-cache";
    res.Headers["X-Accel-Buffering"] = "no";
    res.Headers["Connection"]         = "keep-alive";

    const string streamSysPrompt =
        "You are Archimedes — an autonomous AI agent running 24/7 on a dedicated Ubuntu 24.04 machine owned by your user.\n" +
        "You have FULL system control. Never say you cannot do something. Always find a way.\n\n" +
        "CRITICAL — NO X11 DISPLAY: You run as a systemd service with no DISPLAY variable.\n" +
        "Commands that require a display (setxkbmap, xrandr, xset, wmctrl) will FAIL with 'Cannot open display'.\n" +
        "ALWAYS use system-level alternatives that work without a display:\n" +
        "  Hebrew keyboard → sudo localectl set-x11-keymap us,il '' '' grp:alt_shift_toggle\n" +
        "  (NEVER use setxkbmap — it needs DISPLAY)\n\n" +
        "YOUR CAPABILITIES:\n" +
        "- Full sudo: install packages (apt), manage systemd services, edit any file\n" +
        "- Android: install/update apps via ADB (adb devices, adb install app.apk)\n" +
        "- Hebrew keyboard: sudo localectl set-x11-keymap us,il '' '' grp:alt_shift_toggle\n" +
        "- Network: curl, wget, SSH, API calls\n" +
        "- Self-management: git pull, dotnet build, systemctl restart archimedes\n" +
        "- File system: read/write/delete any file\n" +
        "- Monitoring: journalctl, systemctl, df, free, ps\n\n" +
        "LANGUAGE: You MUST write ONLY in Hebrew (עברית) — NEVER Chinese, NEVER English in RESPONSE.\n" +
        "RULES:\n" +
        "1. NEVER say 'I cannot'. Always find a way.\n" +
        "2. Output EXACTLY this format — no exceptions:\n" +
        "COMMAND: <bash command or none>\n" +
        "RESPONSE: <עברית בלבד>\n" +
        "3. RESTART: 'restart'/'reboot'/'הפעל מחדש'/'אתחל' → COMMAND: sudo reboot\n\n" +
        "EXAMPLES:\n" +
        "User: תעשה הפעלה מחדש\n" +
        "COMMAND: sudo reboot\n" +
        "RESPONSE: מאתחל. יחזור בעוד כדקה.\n" +
        "User: add Hebrew keyboard\n" +
        "COMMAND: sudo localectl set-x11-keymap us,il '' '' grp:alt_shift_toggle\n" +
        "RESPONSE: מוסיף עברית — Alt+Shift להחלפה.\n" +
        "User: install vim\n" +
        "COMMAND: sudo apt-get install -y vim\n" +
        "RESPONSE: מתקין vim.";

    // Recall relevant past events → inject into system prompt.
    // Limit to 2 events to keep context lean (each event adds ~50 tokens).
    var pastEvents   = eventMemory.Recall(message, limit: 2);
    var memoryBlock  = EventMemory.FormatForPrompt(pastEvents);
    var fullSysPrompt = string.IsNullOrEmpty(memoryBlock)
        ? streamSysPrompt
        : streamSysPrompt + "\n\n" + memoryBlock;

    // Phase 1: stream tokens (pass conversation history for context)
    List<(string Role, string Content)> historySnapshot;
    lock (chatHistory) { historySnapshot = chatHistory.ToList(); }

    var sb  = new System.Text.StringBuilder();
    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120)); // 2-minute ceiling
    try
    {
        await foreach (var token in llmAdapter.StreamAsync(
            fullSysPrompt, message, 200, cts.Token, historySnapshot))
        {
            sb.Append(token);
            var tokenJson = JsonSerializer.Serialize(new { type = "token", token });
            await res.WriteAsync($"data: {tokenJson}\n\n");
            await res.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException)
    {
        await res.WriteAsync("data: {\"type\":\"error\",\"msg\":\"timeout — ה-LLM לא הגיב בזמן\"}\n\n");
        await res.Body.FlushAsync();
        return;
    }
    catch (Exception ex)
    {
        var errJson = JsonSerializer.Serialize(new { type = "error", msg = ex.Message });
        await res.WriteAsync($"data: {errJson}\n\n");
        await res.Body.FlushAsync();
        return;
    }
    finally { cts.Dispose(); }

    // Phase 2: parse COMMAND:/RESPONSE:
    var fullText    = sb.ToString().Trim();
    string? bashCmd = null;
    string reply    = fullText;

    foreach (var line in fullText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        if (line.StartsWith("COMMAND:", StringComparison.OrdinalIgnoreCase))
        {
            var cmd = line["COMMAND:".Length..].Trim();
            if (!string.IsNullOrEmpty(cmd) && !cmd.Equals("none", StringComparison.OrdinalIgnoreCase))
                bashCmd = cmd;
        }
        else if (line.StartsWith("RESPONSE:", StringComparison.OrdinalIgnoreCase))
        {
            reply = line["RESPONSE:".Length..].Trim();
        }
    }

    // Phase 3: execute command — with automatic retry loop (up to 2 attempts).
    // On failure: stream a "retry" SSE event → ask LLM for alternative → execute.
    // This is how Archimedes learns to recover from errors instead of just reporting them.
    string? cmdOut = null;
    bool    cmdOk  = true;

    // Detect commands that kill the server (reboot/shutdown/restart archimedes).
    // These must send "done" FIRST — the server dies before it can respond otherwise.
    static bool IsDisruptive(string? cmd) =>
        cmd != null && System.Text.RegularExpressions.Regex.IsMatch(cmd,
            @"\breboot\b|\bshutdown\b|\bpoweroff\b|\bhalt\b|systemctl\s+restart\s+archimedes");

    if (!string.IsNullOrEmpty(bashCmd) && IsDisruptive(bashCmd))
    {
        // Send done BEFORE the command — client gets its response while server is still up
        var earlyOut  = "מבצע... החיבור יינתק לרגע";
        var earlyDone = JsonSerializer.Serialize(new
            { type = "done", reply, command = bashCmd, output = earlyOut });
        await res.WriteAsync($"data: {earlyDone}\n\n");
        await res.Body.FlushAsync();

        // Delay 800ms so the browser receives and renders the event, then execute
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(
                    "bash", new[] { "-c", bashCmd })
                { UseShellExecute = false };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                ArchLogger.LogWarn($"[Chat/Stream] Disruptive cmd failed: {ex.Message}");
            }
        });

        _ = Task.Run(() => eventMemory.Save(new MemoryEvent
        {
            UserMessage = message,
            Command     = bashCmd,
            Reply       = reply,
            Output      = earlyOut,
            Success     = true
        }));
        return;
    }

    if (!string.IsNullOrEmpty(bashCmd))
    {
        const int MAX_CMD_RETRIES = 2;
        string    currentCmd      = bashCmd;

        for (int attempt = 1; attempt <= MAX_CMD_RETRIES; attempt++)
        {
            ArchLogger.LogInfo($"[Chat/Stream] Executing (attempt {attempt}/{MAX_CMD_RETRIES}): {currentCmd}");
            int exitCode = -1;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("bash", new[] { "-c", currentCmd })
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                var outSb = new System.Text.StringBuilder();
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
                proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                using var cmdCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await proc.WaitForExitAsync(cmdCts.Token);
                cmdOut   = outSb.ToString().Trim();
                exitCode = proc.ExitCode;
            }
            catch (Exception ex)
            {
                cmdOut   = "שגיאה: " + ex.Message;
                exitCode = -1;
                cmdOk    = false;
                ArchLogger.LogWarn($"[Chat/Stream] Execute exception: {ex.Message}");
                break;
            }

            if (exitCode == 0)
            {
                cmdOk   = true;
                bashCmd = currentCmd;   // report the command that actually succeeded
                ArchLogger.LogInfo($"[Chat/Stream] Command succeeded on attempt {attempt}");
                break;
            }

            // ── Command failed ─────────────────────────────────────────────────
            cmdOk = false;
            ArchLogger.LogWarn($"[Chat/Stream] Cmd failed exit={exitCode} attempt={attempt}: {currentCmd}");

            if (attempt == MAX_CMD_RETRIES)
            {
                reply += $"\n⚠ הפקודה נכשלה לאחר {MAX_CMD_RETRIES} ניסיונות (קוד {exitCode}).";
                break;
            }

            // ── Notify client we are retrying ──────────────────────────────────
            var retryEvt = JsonSerializer.Serialize(new
            {
                type    = "retry",
                attempt,
                msg     = $"שגיאה בניסיון {attempt} — מנסה גישה אחרת..."
            });
            await res.WriteAsync($"data: {retryEvt}\n\n");
            await res.Body.FlushAsync();

            // ── Ask LLM for an alternative approach ───────────────────────────
            // Error context is in Hebrew only — prevents qwen2.5:7b from
            // switching to Chinese when it sees mixed Hebrew/English input.
            var errorSummary = cmdOut?[..Math.Min(200, cmdOut?.Length ?? 0)] ?? "";
            var errorCtx =
                $"הפקודה נכשלה:\n" +
                $"פקודה: {currentCmd}\n" +
                $"שגיאה: {errorSummary}\n" +
                $"בקשה מקורית: {message}\n\n" +
                "הצע פקודה חלופית שתפתור את הבעיה. פורמט חובה:\n" +
                "COMMAND: <פקודת bash>\n" +
                "RESPONSE: <הסבר בעברית>";

            var altSb       = new System.Text.StringBuilder();
            var altCts      = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            bool llmRetryOk = true;
            try
            {
                await foreach (var tok in llmAdapter.StreamAsync(
                    streamSysPrompt, errorCtx, 200, altCts.Token))
                {
                    altSb.Append(tok);
                    // Stream retry tokens so the user sees progress in the chat box.
                    // Wrap each write — client may disconnect (BodyStreamBuffer abort).
                    try
                    {
                        var rtJson = JsonSerializer.Serialize(
                            new { type = "retry_token", token = tok });
                        await res.WriteAsync($"data: {rtJson}\n\n");
                        await res.Body.FlushAsync();
                    }
                    catch (Exception writeEx)
                    {
                        // Client disconnected mid-stream — stop streaming but keep
                        // collecting the LLM output so we can still execute the command
                        ArchLogger.LogWarn(
                            $"[Chat/Stream] Client disconnected during retry: {writeEx.Message}");
                        break;
                    }
                }
            }
            catch (Exception ex2)
            {
                ArchLogger.LogWarn($"[Chat/Stream] Retry LLM call failed: {ex2.Message}");
                reply     += "\n⚠ הפקודה נכשלה ולא הצלחתי לקבל גישה חלופית מה-LLM.";
                llmRetryOk = false;
            }
            finally { altCts.Dispose(); }

            if (!llmRetryOk) break;

            // ── Parse alternative command from LLM ────────────────────────────
            string? altCmd   = null;
            string  altReply = reply;
            foreach (var ln in altSb.ToString().Trim()
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (ln.StartsWith("COMMAND:", StringComparison.OrdinalIgnoreCase))
                {
                    var c = ln["COMMAND:".Length..].Trim();
                    if (!string.IsNullOrEmpty(c) &&
                        !c.Equals("none", StringComparison.OrdinalIgnoreCase))
                        altCmd = c;
                }
                else if (ln.StartsWith("RESPONSE:", StringComparison.OrdinalIgnoreCase))
                    altReply = ln["RESPONSE:".Length..].Trim();
            }

            if (string.IsNullOrEmpty(altCmd))
            {
                reply += "\n⚠ הפקודה נכשלה ולא נמצאה גישה חלופית.";
                break;
            }

            // ── Prepare next iteration ────────────────────────────────────────
            currentCmd = altCmd;
            bashCmd    = altCmd;    // update so Phase 4 reports the final command
            reply      = altReply;
            ArchLogger.LogInfo($"[Chat/Stream] Retrying with alternative: {altCmd}");
        }
    }

    // Phase 4: send done event with parsed data
    var doneJson = JsonSerializer.Serialize(new
    {
        type    = "done",
        reply,
        command = bashCmd,
        output  = cmdOut
    });
    await res.WriteAsync($"data: {doneJson}\n\n");
    await res.Body.FlushAsync();

    // Save this exchange to conversation history (short-term memory).
    // Keep OUTPUT short in history — long outputs inflate context and slow the model.
    var assistantContent = string.IsNullOrEmpty(bashCmd)
        ? $"COMMAND: none\nRESPONSE: {reply}"
        : $"COMMAND: {bashCmd}\nRESPONSE: {reply}"
          + (string.IsNullOrEmpty(cmdOut) ? "" : $"\nOUTPUT: {cmdOut[..Math.Min(80, cmdOut.Length)]}");

    lock (chatHistory)
    {
        chatHistory.Add(("user",      message));
        chatHistory.Add(("assistant", assistantContent));
        var maxMessages = MAX_HISTORY_TURNS * 2;
        while (chatHistory.Count > maxMessages)
            chatHistory.RemoveAt(0);
    }

    // Save to episodic memory (long-term learning)
    // cmdOk = true when no command ran OR the command exited 0 (possibly after retries)
    _ = Task.Run(() => eventMemory.Save(new MemoryEvent
    {
        UserMessage = message,
        Command     = bashCmd ?? "none",
        Reply       = reply,
        Output      = cmdOut ?? "",
        Success     = bashCmd == null || cmdOk
    }));
});

// Clear conversation history (fresh start)
app.MapPost("/chat/reset", () =>
{
    lock (chatHistory) { chatHistory.Clear(); }
    return Results.Json(new { ok = true, message = "שיחה אופסה" });
});

// Episodic memory endpoints — inspect what Archimedes has learned
app.MapGet("/memory", () =>
{
    var stats    = eventMemory.GetStats();
    var recent   = eventMemory.Recall("", limit: 10);
    var failures = eventMemory.GetFailures(5);
    return Results.Json(new
    {
        stats    = new { stats.Total, stats.Success, stats.Failure, successRate = $"{stats.SuccessRate:P0}" },
        recent   = recent.Select(e => new { e.Timestamp, e.UserMessage, e.Command, e.Success }),
        failures = failures.Select(e => new { e.Timestamp, e.UserMessage, e.Command, e.Output })
    });
});

// ── Phase 21: Procedure Memory ────────────────────────────────────────────────

// List all stored procedures
app.MapGet("/procedures", () =>
{
    var all = procedureStore.GetAll();
    return Results.Json(new
    {
        count      = all.Count,
        procedures = all.Select(p => new
        {
            id            = p.Id,
            intent        = p.Intent,
            promptExample = p.PromptExample,
            keywords      = p.Keywords,
            successCount  = p.SuccessCount,
            failureCount  = p.FailureCount,
            successRate   = p.SuccessRate,
            totalUses     = p.TotalUses,
            createdAt     = p.CreatedAt,
            lastUsedAt    = p.LastUsedAt,
            lastSuccessAt = p.LastSuccessAt,
            stepCount     = p.Plan.Steps.Count
        })
    });
});

// Get a specific procedure (full plan included)
app.MapGet("/procedures/{id}", (string id) =>
{
    var proc = procedureStore.GetById(id);
    if (proc == null) return Results.NotFound(new { error = "Procedure not found" });

    return Results.Json(new
    {
        id            = proc.Id,
        intent        = proc.Intent,
        promptExample = proc.PromptExample,
        keywords      = proc.Keywords,
        successCount  = proc.SuccessCount,
        failureCount  = proc.FailureCount,
        successRate   = proc.SuccessRate,
        totalUses     = proc.TotalUses,
        createdAt     = proc.CreatedAt,
        lastUsedAt    = proc.LastUsedAt,
        lastSuccessAt = proc.LastSuccessAt,
        plan          = proc.Plan
    });
});

// Delete a procedure (e.g. if it became stale or incorrect)
app.MapDelete("/procedures/{id}", (string id) =>
{
    var deleted = procedureStore.Delete(id);
    if (!deleted) return Results.NotFound(new { error = "Procedure not found" });
    return Results.Ok(new { deleted = true, id });
});

// ── Phase 24: Failure Dialogue ────────────────────────────────────────────────

// ── Phase 25: Availability Engine endpoints ──────────────────────────────────

// GET /availability/status — current user availability
app.MapGet("/availability/status", () =>
{
    var status = availabilityEngine.GetStatus();
    return Results.Json(new
    {
        isAvailable        = status.IsAvailable,
        reason             = status.Reason,
        nextWindowUtc      = status.NextWindowUtc,
        lastInteractionUtc = status.LastInteractionUtc
    });
});

// GET /availability/patterns — learned patterns
app.MapGet("/availability/patterns", () =>
{
    var p = availabilityEngine.GetPattern();
    return Results.Json(new
    {
        sleepStartHour     = p.SleepStartHour,
        sleepEndHour       = p.SleepEndHour,
        shabbatDetected    = p.ShabbatDetected,
        manualOverride     = p.ManualOverride,
        lastInteractionUtc = p.LastInteractionUtc,
        interactionCount   = p.RecentInteractions.Count,
        updatedAt          = p.UpdatedAt
    });
});

// POST /availability/interaction — record a user interaction
app.MapPost("/availability/interaction", (HttpRequest req) =>
{
    var source = req.Query.TryGetValue("source", out var s) ? s.ToString() : "api";
    availabilityEngine.RecordInteraction(source);
    return Results.Json(new { recorded = true, source });
});

// POST /availability/patterns — manually update patterns
app.MapPost("/availability/patterns", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    try
    {
        var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        var sleepStart     = root.TryGetProperty("sleepStartHour",  out var ss)  ? ss.GetInt32()   : availabilityEngine.GetPattern().SleepStartHour;
        var sleepEnd       = root.TryGetProperty("sleepEndHour",    out var se)  ? se.GetInt32()   : availabilityEngine.GetPattern().SleepEndHour;
        var shabbat        = root.TryGetProperty("shabbatDetected", out var sh)  ? sh.GetBoolean() : availabilityEngine.GetPattern().ShabbatDetected;
        var manualOverride = root.TryGetProperty("manualOverride",  out var mo)  ? mo.GetBoolean() : availabilityEngine.GetPattern().ManualOverride;
        availabilityEngine.UpdatePattern(sleepStart, sleepEnd, shabbat, manualOverride);
        return Results.Json(new { updated = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// POST /availability/should-delay?action=X&critical=false — check if action should be delayed
app.MapPost("/availability/should-delay", (HttpRequest req) =>
{
    var action   = req.Query.TryGetValue("action",   out var a) ? a.ToString() : "";
    var critical = req.Query.TryGetValue("critical",  out var c) && c == "true";
    var delay    = availabilityEngine.ShouldDelay(action, critical);
    var status   = availabilityEngine.GetStatus();
    return Results.Json(new { shouldDelay = delay, reason = status.Reason, isAvailable = status.IsAvailable });
});

// GET /recovery-dialogues — list all pending recovery dialogues (polled by Chat UI)
app.MapGet("/recovery-dialogues", () =>
{
    var pending = failureDialogueStore.GetPending();
    return Results.Json(new
    {
        count     = pending.Count,
        dialogues = pending.Select(d => new
        {
            dialogueId       = d.DialogueId,
            taskId           = d.TaskId,
            taskTitle        = d.TaskTitle,
            failedStep       = d.FailedStep,
            recoveryQuestion = d.RecoveryQuestion,
            createdAt        = d.CreatedAtUtc
        })
    });
});

// POST /recovery-dialogues/{id}/respond — user responds with action + optional info
app.MapPost("/recovery-dialogues/{id}/respond", async (string id, HttpRequest req) =>
{
    RecoveryRespondRequest? body;
    try
    {
        body = await req.ReadFromJsonAsync<RecoveryRespondRequest>();
    }
    catch
    {
        return Results.BadRequest(new { error = "Invalid JSON body" });
    }

    if (body == null || string.IsNullOrWhiteSpace(body.Action))
        return Results.BadRequest(new { error = "action is required (retry | info | dismiss)" });

    var action = body.Action.ToLowerInvariant();
    if (action != "retry" && action != "info" && action != "dismiss")
        return Results.BadRequest(new { error = "action must be retry, info, or dismiss" });

    var dialogue = failureDialogueStore.Get(id);
    if (dialogue == null)
        return Results.NotFound(new { error = "Dialogue not found" });

    var ok = failureDialogueStore.Respond(id, action, body.Info);
    if (!ok)
        return Results.BadRequest(new { error = "Dialogue already answered or dismissed" });

    // On retry: reset the task back to QUEUED and start it running again
    if (action == "retry")
    {
        var reset = taskService.ResetForRetry(dialogue.TaskId);
        if (reset != null)
        {
            taskService.StartRun(dialogue.TaskId);
            return Results.Ok(new
            {
                ok       = true,
                action   = "retry",
                taskId   = dialogue.TaskId,
                message  = "המשימה אופסה ותתחיל לרוץ מחדש"
            });
        }
        return Results.Ok(new
        {
            ok      = true,
            action  = "retry",
            message = "הדיאלוג נענה אך המשימה לא נמצאה לאיפוס"
        });
    }

    return Results.Ok(new { ok = true, action, dialogueId = id });
});

app.MapGet("/tasks/running", () =>
{
    var runningTasks = taskRunner.GetRunningTasks();
    return Results.Json(new
    {
        count = runningTasks.Count,
        tasks = runningTasks,
        runnerStats = taskRunner.GetStats()
    });
});

app.MapGet("/health/deep", async () =>
{
    var runnerStats = taskRunner.GetStats();
    var runningTasks = taskRunner.GetRunningTasks();
    
    // Check Net health
    bool netHealthy = false;
    try
    {
        var netClient = httpClientFactory.CreateClient();
        netClient.Timeout = TimeSpan.FromSeconds(5);
        var netResponse = await netClient.GetAsync("http://localhost:5052/health");
        netHealthy = netResponse.IsSuccessStatusCode;
    }
    catch { }
    
    // Get monitor ticks
    int? monitorTicks = null;
    try
    {
        var monitorClient = httpClientFactory.CreateClient();
        monitorClient.Timeout = TimeSpan.FromSeconds(2);
        var ticksResponse = await monitorClient.GetStringAsync("http://localhost:5051/monitor/ticks");
        if (int.TryParse(ticksResponse.Trim(), out var ticks))
            monitorTicks = ticks;
    }
    catch { }
    
    return Results.Json(new
    {
        status = "ok",
        runner = new
        {
            running = runnerStats.Running,
            lastHeartbeat = runnerStats.LastHeartbeat,
            heartbeatAgeSeconds = (DateTime.UtcNow - runnerStats.LastHeartbeat).TotalSeconds,
            watchdogEnabled = runnerStats.WatchdogEnabled,
            totalTicksProcessed = runnerStats.TotalTicksProcessed,
            totalStepsExecuted = runnerStats.TotalStepsExecuted
        },
        tasks = new
        {
            runningCount = runningTasks.Count,
            oldestAgeSeconds = runningTasks.Any() ? runningTasks.Max(t => t.AgeSeconds) : 0
        },
        net = new
        {
            healthy = netHealthy
        },
        monitor = new
        {
            ticks = monitorTicks
        },
        config = runnerStats.Config
    });
});

app.MapGet("/storage/health", () =>
{
    var report = storageManager.GetHealthReport();
    return Results.Json(report);
});

app.MapPost("/storage/cleanup", () =>
{
    var result = storageManager.RunCleanup();
    return Results.Json(result);
});

app.MapGet("/selfupdate/status", () =>
{
    var status = promotionManager.GetStatus();
    return Results.Json(new
    {
        currentVersion = status.CurrentVersion,
        canaryVersion = status.CanaryVersion,
        releasesRoot = status.ReleasesRoot,
        sandboxRoot = sandboxRoot,
        recentMetricsCount = status.RecentMetrics.Count
    });
});

app.MapPost("/selfupdate/sandbox-run", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    var commit = "HEAD";
    var soakHours = 0;
    var dryRun = false;
    if (!string.IsNullOrWhiteSpace(body))
    {
        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("commit", out var c)) commit = c.GetString() ?? "HEAD";
            if (doc.RootElement.TryGetProperty("soakHours", out var s)) soakHours = s.GetInt32();
            if (doc.RootElement.TryGetProperty("dryRun", out var d)) dryRun = d.GetBoolean();
        }
        catch { }
    }
    var result = await Task.Run(() => sandboxRunner.Run(commit, soakHours, dryRun));
    return Results.Json(result);
});

app.MapPost("/selfupdate/promote", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    string? candidateId = null;
    string? sandboxPath = null;
    double? canaryPercent = null;
    if (!string.IsNullOrWhiteSpace(body))
    {
        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("candidateId", out var c)) candidateId = c.GetString();
            if (doc.RootElement.TryGetProperty("sandboxPath", out var s)) sandboxPath = s.GetString();
            if (doc.RootElement.TryGetProperty("canaryPercent", out var p)) canaryPercent = p.GetDouble();
        }
        catch { }
    }
    if (string.IsNullOrEmpty(candidateId) || string.IsNullOrEmpty(sandboxPath))
        return Results.BadRequest("candidateId and sandboxPath required");
    var sandboxCore = Path.Combine(sandboxPath, "core", "bin", "Release", "net8.0");
    if (!Directory.Exists(sandboxCore))
        sandboxCore = Path.Combine(sandboxPath, "core", "bin", "Debug", "net8.0");
    if (!Directory.Exists(sandboxCore))
        return Results.NotFound("Candidate build not found");
    var promoteResult = promotionManager.Promote(candidateId, sandboxPath, canaryPercent);
    return promoteResult switch
    {
        PromoteResult.Success => Results.Json(new { ok = true,  noop = false }),
        PromoteResult.Noop    => Results.Json(new { ok = true,  noop = true  }),
        _                     => Results.Json(new { ok = false, noop = false })
    };
});

app.MapPost("/selfupdate/rollback", () =>
{
    var rollbackResult = promotionManager.Rollback();
    return rollbackResult switch
    {
        RollbackResult.Success           => Results.Json(new { ok = true }),
        RollbackResult.NothingToRollback => Results.Conflict(new { ok = false, error = "nothing to rollback" }),
        _                                => Results.Json(new { ok = false, error = "rollback failed" })
    };
});

app.MapGet("/selfupdate/audit", (int? skip, int? take) =>
{
    var skipVal = skip ?? 0;
    var takeVal = take ?? 50;
    var events = selfUpdateAudit.GetEvents(skipVal, takeVal);
    return Results.Json(new { events, total = events.Count });
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
    CreateTaskRequest? request;
    try
    {
        request = JsonSerializer.Deserialize<CreateTaskRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException)
    {
        return Results.BadRequest("Invalid JSON");
    }
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

// Returns only active tasks (non-terminal states).
// If nothing is active, returns a synthetic "idle" row so the UI is never empty.
app.MapGet("/tasks/active", () =>
{
    var allTasks = taskService.GetTasks();
    var active = allTasks
        .Where(t => t.State != TaskState.DONE && t.State != TaskState.FAILED)
        .Select(TaskResponse.FromTask)
        .ToList();

    if (active.Count == 0)
    {
        active.Add(new TaskResponse
        {
            TaskId       = "system-idle",
            Title        = "מנטר מצב המערכת",
            State        = "RUNNING",
            Type         = "SYSTEM",
            Priority     = "LOW",
            CreatedAtUtc = appStartTime,
        });
    }

    return Results.Json(active);
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

// ── Phase 26: Goal Layer ────────────────────────────────────────────────────────

// POST /goals — create a new goal
app.MapPost("/goals", async (HttpRequest req) =>
{
    using var sr  = new StreamReader(req.Body);
    var body      = await sr.ReadToEndAsync();
    var createReq = JsonSerializer.Deserialize<CreateGoalRequest>(
        body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    if (createReq == null || string.IsNullOrWhiteSpace(createReq.UserPrompt))
        return Results.BadRequest("userPrompt required");

    try
    {
        var goal = await goalEngine.CreateAsync(createReq);
        return Results.Json(new
        {
            goalId   = goal.GoalId,
            title    = goal.Title,
            state    = goal.State.ToString(),
            type     = goal.Type.ToString(),
            intent   = goal.Intent,
            taskIds  = goal.TaskIds,
            createdAt = goal.CreatedAtUtc
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// GET /goals — list all goals
app.MapGet("/goals", () =>
{
    var all = goalStore.GetAll();
    return Results.Json(new
    {
        count = all.Count,
        goals = all.Select(g => new
        {
            goalId    = g.GoalId,
            title     = g.Title,
            state     = g.State.ToString(),
            type      = g.Type.ToString(),
            intent    = g.Intent,
            progress  = Math.Round(g.Progress, 2),
            taskCount = g.TaskIds.Count,
            retryCount = g.RetryCount,
            createdAt  = g.CreatedAtUtc,
            updatedAt  = g.UpdatedAtUtc,
            nextCheckUtc = g.NextCheckUtc
        })
    });
});

// GET /goals/{id} — goal detail
app.MapGet("/goals/{id}", (string id) =>
{
    var g = goalStore.GetById(id);
    if (g == null) return Results.NotFound(new { error = "goal not found" });
    return Results.Json(new
    {
        goalId           = g.GoalId,
        title            = g.Title,
        description      = g.Description,
        state            = g.State.ToString(),
        type             = g.Type.ToString(),
        intent           = g.Intent,
        successCondition = g.SuccessCondition,
        progress         = Math.Round(g.Progress, 2),
        taskIds          = g.TaskIds,
        currentTaskId    = g.CurrentTaskId,
        retryCount       = g.RetryCount,
        maxRetries       = g.MaxRetries,
        interactionCount = g.Memory.TotalRuns,
        successCount     = g.Memory.SuccessCount,
        lastObservedValue = g.Memory.LastObservedValue,
        failureReason    = g.FailureReason,
        createdAt        = g.CreatedAtUtc,
        updatedAt        = g.UpdatedAtUtc,
        completedAt      = g.CompletedAtUtc,
        nextCheckUtc     = g.NextCheckUtc,
        hasCheckpoint    = g.LastCheckpoint != null
    });
});

// POST /goals/{id}/pause — ACTIVE/MONITORING -> IDLE
app.MapPost("/goals/{id}/pause", (string id) =>
{
    var g = goalEngine.Pause(id);
    if (g == null) return Results.NotFound(new { error = "goal not found or not pausable" });
    return Results.Json(new { goalId = g.GoalId, state = g.State.ToString() });
});

// POST /goals/{id}/resume — IDLE -> ACTIVE
app.MapPost("/goals/{id}/resume", (string id) =>
{
    var g = goalEngine.Resume(id);
    if (g == null) return Results.NotFound(new { error = "goal not found or not idle" });
    return Results.Json(new { goalId = g.GoalId, state = g.State.ToString() });
});

// POST /goals/{id}/evaluate — force evaluation (no task spawn, just check criteria)
app.MapPost("/goals/{id}/evaluate", (string id) =>
{
    var g = goalStore.GetById(id);
    if (g == null) return Results.NotFound(new { error = "goal not found" });
    var (isAchieved, reason, nextAction) = goalEngine.Evaluate(g);
    return Results.Json(new
    {
        goalId     = g.GoalId,
        isAchieved,
        reason,
        nextAction,
        state      = g.State.ToString(),
        progress   = Math.Round(g.Progress, 2)
    });
});

// GET /goals/{id}/tasks — all tasks spawned by this goal
app.MapGet("/goals/{id}/tasks", (string id) =>
{
    var g = goalStore.GetById(id);
    if (g == null) return Results.NotFound(new { error = "goal not found" });

    var tasks = g.TaskIds
        .Select(tid => taskService.GetTask(tid))
        .Where(t => t != null)
        .Select(t => new
        {
            taskId  = t!.TaskId,
            title   = t.Title,
            state   = t.State.ToString(),
            created = t.CreatedAtUtc
        })
        .ToList();

    return Results.Json(new { goalId = id, count = tasks.Count, tasks });
});

// DELETE /goals/{id} — cancel and remove goal
app.MapDelete("/goals/{id}", (string id) =>
{
    var g = goalStore.GetById(id);
    if (g == null) return Results.NotFound(new { error = "goal not found" });

    // If has active task, try to cancel it
    if (!string.IsNullOrEmpty(g.CurrentTaskId))
    {
        try { taskService.Cancel(g.CurrentTaskId); } catch { }
    }

    goalStore.Delete(id);
    return Results.Json(new { ok = true, goalId = id });
});

// ── Phase 27: Autonomous Tool Acquisition ──────────────────────────────────────

// GET /tools — list all acquired tools
app.MapGet("/tools", () =>
{
    var tools = toolAcquisitionEngine.GetAllTools();
    return Results.Json(new
    {
        count = tools.Count,
        tools = tools.Select(t => new
        {
            toolId      = t.ToolId,
            capability  = t.Capability,
            name        = t.Name,
            strategy    = t.Strategy.ToString(),
            risk        = t.Risk.ToString(),
            sourceType  = t.SourceType.ToString(),
            usageCount  = t.UsageCount,
            reliability = t.ReliabilityScore,
            acquiredAt  = t.AcquiredAt
        })
    });
});

// GET /tools/gaps — list active capability gaps
app.MapGet("/tools/gaps", () =>
{
    var gaps = toolAcquisitionEngine.GetAllGaps();
    return Results.Json(new
    {
        count = gaps.Count,
        gaps  = gaps.Select(g => new
        {
            gapId      = g.GapId,
            capability = g.Capability,
            status     = g.Status.ToString(),
            detectedAt = g.DetectedAt,
            resolvedAt = g.ResolvedAt,
            message    = g.UserMessage
        })
    });
});

// GET /tools/legal/pending — pending legal approvals
app.MapGet("/tools/legal/pending", () =>
{
    var pending = toolAcquisitionEngine.GetPendingApprovals();
    return Results.Json(new
    {
        count    = pending.Count,
        approvals = pending.Select(a => new
        {
            approvalId  = a.ApprovalId,
            gapId       = a.GapId,
            capability  = a.Capability,
            userMessage = a.UserMessage,
            legalIssue  = a.LegalIssue,
            legalBasis  = a.LegalBasis,
            createdAt   = a.CreatedAt
        })
    });
});

// POST /tools/legal/{approvalId}/decide — user responds to legal approval
app.MapPost("/tools/legal/{approvalId}/decide", async (string approvalId, HttpRequest req) =>
{
    using var sr  = new StreamReader(req.Body);
    var body      = await sr.ReadToEndAsync();
    var doc       = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);

    var decisionStr = doc.RootElement.TryGetProperty("decision", out var d)
        ? d.GetString() ?? "" : "";
    var userNote    = doc.RootElement.TryGetProperty("note", out var n)
        ? n.GetString() : null;

    var decision = decisionStr.ToUpperInvariant() switch
    {
        "APPROVED"          => ApprovalDecision.APPROVED,
        "REJECTED"          => ApprovalDecision.REJECTED,
        "WAITING_RESEARCH"  => ApprovalDecision.WAITING_RESEARCH,
        _                   => (ApprovalDecision?)null
    };

    if (decision == null)
        return Results.BadRequest("decision must be APPROVED | REJECTED | WAITING_RESEARCH");

    var tool = await toolAcquisitionEngine.ResolveLegalDecisionAsync(
        approvalId, decision.Value, userNote);

    return Results.Json(new
    {
        ok         = true,
        decision   = decision.ToString(),
        toolId     = tool?.ToolId,
        toolName   = tool?.Name,
        acquired   = tool != null
    });
});

// POST /tools/acquire — trigger acquisition for a capability (non-blocking).
// Returns immediately with gapId; acquisition runs in background.
// Poll GET /tools/gaps to track progress.
app.MapPost("/tools/acquire", async (HttpRequest req) =>
{
    using var sr  = new StreamReader(req.Body);
    var body      = await sr.ReadToEndAsync();
    var doc       = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);

    var capability = doc.RootElement.TryGetProperty("capability", out var c)
        ? c.GetString() ?? "" : "";
    var context    = doc.RootElement.TryGetProperty("context", out var ctx)
        ? ctx.GetString() ?? "" : "";

    if (string.IsNullOrWhiteSpace(capability))
        return Results.BadRequest("capability required");

    // Already acquired? Return immediately.
    var existing = toolGapDetector.GetTool(capability);
    if (existing != null)
    {
        return Results.Json(new
        {
            ok        = true,
            toolId    = existing.ToolId,
            name      = existing.Name,
            strategy  = existing.Strategy.ToString(),
            capability,
            gapId     = (string?)null,
            status    = "ALREADY_ACQUIRED"
        });
    }

    // Register gap immediately (synchronous) so it appears in /tools/gaps now.
    var gap = toolGapDetector.Detect(capability, context);

    // Fire-and-forget search in background.
    _ = Task.Run(async () =>
    {
        try { await toolAcquisitionEngine.AcquireAsync(capability, context); }
        catch (Exception ex) { ArchLogger.LogWarn($"[Acquire BG] {capability}: {ex.Message}"); }
    });

    return Results.Json(new
    {
        ok        = gap != null,
        toolId    = (string?)null,
        name      = (string?)null,
        strategy  = (string?)null,
        capability,
        gapId     = gap?.GapId,
        status    = "SEARCHING"
    });
});

// GET /tools/sources — source intelligence stats
app.MapGet("/tools/sources", () =>
{
    var sources = toolAcquisitionEngine.GetSourceStats();
    return Results.Json(new
    {
        count       = sources.Count,
        torAvailable = toolAcquisitionEngine.IsTorAvailable,
        sources     = sources.Select(s => new
        {
            sourceId    = s.SourceId,
            sourceType  = s.SourceType.ToString(),
            totalSearches = s.TotalSearches,
            reliability   = Math.Round(s.ReliabilityScore, 3),
            lastUsed      = s.LastUsed
        })
    });
});

// ── Phase 28: Machine Migration (Octopus) ────────────────────────────────────

// GET /migration/estimate — pre-flight size estimate (no side effects)
app.MapGet("/migration/estimate", () =>
{
    var est = migrationPackager.GetDetailedEstimate();
    return Results.Json(new
    {
        rawDataMB          = est.RawDataMB,
        estimatedPackageMB = est.EstimatedPackageMB,
        recommendedDriveMB = est.RecommendedDriveMB,
        breakdown          = new
        {
            databaseMB   = est.Breakdown.DatabaseMB,
            proceduresMB = est.Breakdown.ProceduresMB,
            toolStoreMB  = est.Breakdown.ToolStoreMB,
            goalsMB      = est.Breakdown.GoalsMB,
            otherMB      = est.Breakdown.OtherMB
        }
    });
});

// POST /migration/start — initiates migration (non-blocking, returns plan immediately)
app.MapPost("/migration/start", async (HttpRequest req) =>
{
    using var sr  = new StreamReader(req.Body);
    var body      = await sr.ReadToEndAsync();
    StartMigrationRequest? request;
    try
    {
        request = JsonSerializer.Deserialize<StartMigrationRequest>(
            body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch { return Results.BadRequest("Invalid JSON"); }

    if (request == null || string.IsNullOrWhiteSpace(request.TargetPath))
        return Results.BadRequest("targetPath required");

    var plan = migrationEngine.Start(request);
    return Results.Json(new
    {
        migrationId     = plan.MigrationId,
        status          = plan.Status.ToString(),
        targetPath      = plan.TargetPath,
        targetType      = plan.TargetType.ToString(),
        dryRun          = plan.DryRun,
        maxVolumeSizeMB = plan.MaxVolumeSizeMB,
        startedAt       = plan.StartedAt
    });
});

// GET /migration/status — list all migration plans
app.MapGet("/migration/status", () =>
{
    var plans = migrationEngine.GetAllPlans();
    return Results.Json(new
    {
        count = plans.Count,
        migrations = plans.Select(p => new
        {
            migrationId    = p.MigrationId,
            status         = p.Status.ToString(),
            targetPath     = p.TargetPath,
            targetType     = p.TargetType.ToString(),
            dryRun         = p.DryRun,
            requiredDiskMB = p.RequiredDiskMB,
            availableMB    = p.AvailableDiskMB,
            taskCount      = p.TaskDecisions.Count,
            packagePath    = p.PackagePath,
            error          = p.Error,
            startedAt      = p.StartedAt,
            completedAt    = p.CompletedAt
        })
    });
});

// GET /migration/status/{id} — single migration plan
app.MapGet("/migration/status/{id}", (string id) =>
{
    var plan = migrationEngine.GetPlan(id);
    if (plan == null) return Results.NotFound(new { error = "migration not found" });
    return Results.Json(new
    {
        migrationId    = plan.MigrationId,
        status         = plan.Status.ToString(),
        targetPath     = plan.TargetPath,
        targetType     = plan.TargetType.ToString(),
        dryRun         = plan.DryRun,
        requiredDiskMB = plan.RequiredDiskMB,
        availableMB    = plan.AvailableDiskMB,
        taskDecisions  = plan.TaskDecisions.Select(d => new
        {
            taskId     = d.TaskId,
            title      = d.Title,
            action     = d.Action.ToString(),
            stepBefore = d.StepBefore,
            stateBefore = d.StateBefore.ToString()
        }),
        maxVolumeSizeMB = plan.MaxVolumeSizeMB,
        volumeCount     = plan.VolumeCount,
        packagePaths    = plan.PackagePaths,
        packagePath     = plan.PackagePath,
        error           = plan.Error,
        startedAt       = plan.StartedAt,
        completedAt     = plan.CompletedAt
    });
});

// POST /migration/receive — target endpoint: receives zip and triggers resume
app.MapPost("/migration/receive", async (HttpRequest req) =>
{
    // Expect multipart/form-data with a "package" file field
    if (!req.HasFormContentType)
        return Results.BadRequest("multipart/form-data expected");

    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("package");
    if (file == null)
        return Results.BadRequest("package file required");

    using var stream = file.OpenReadStream();
    var ok = migrationResumer.ReceivePackage(stream);
    return Results.Json(new { ok, message = ok ? "Migration package received and applied" : "Package received but resume failed" });
});

// ── Phase 29: Autonomous Self-Improvement ──────────────────────────────────────

// GET /selfimprove/status — current engine state, CPU, insights
app.MapGet("/selfimprove/status", () =>
    Results.Json(selfImprovementEngine.GetStatus()));

// GET /selfimprove/history — history of completed work items
app.MapGet("/selfimprove/history", (int? limit) =>
    Results.Json(selfImprovementStore.GetHistory(limit ?? 50)));

// GET /selfimprove/insights — accumulated insights / findings
app.MapGet("/selfimprove/insights", (int? limit) =>
    Results.Json(selfImprovementStore.GetInsights(limit ?? 20)));

// GET /selfimprove/git-log — list of self-patch commits
app.MapGet("/selfimprove/git-log", () =>
    Results.Json(new { commits = selfGitManager.GetSelfPatchHistory() }));

// POST /selfimprove/redirect — user redirects self-improvement focus
app.MapPost("/selfimprove/redirect", async (HttpRequest req) =>
{
    using var sr = new StreamReader(req.Body);
    var topic    = (await sr.ReadToEndAsync()).Trim();
    if (string.IsNullOrWhiteSpace(topic))
        return Results.BadRequest("topic required");
    selfImprovementEngine.RedirectFocus(topic);
    return Results.Json(new { ok = true, topic });
});

// POST /selfimprove/pause — manual pause
app.MapPost("/selfimprove/pause", () =>
{
    selfImprovementEngine.NotifyUserTaskStarted(); // borrow the signal
    return Results.Json(new { ok = true, status = "paused" });
});

// POST /selfimprove/resume — manual resume
app.MapPost("/selfimprove/resume", () =>
{
    selfImprovementEngine.NotifyUserTaskCompleted();
    return Results.Json(new { ok = true, status = "resumed" });
});

// ── Phase 30: OS Management endpoints ─────────────────────────────────────

// GET /os/status — full OS health snapshot
app.MapGet("/os/status", () => Results.Json(osManager.GetStatus()));

// GET /os/hardware — hardware metrics only (CPU temp, RAM, disks)
app.MapGet("/os/hardware", () => Results.Json(hardwareMonitor.Collect()));

// GET /os/reboot/required — is a reboot needed?
app.MapGet("/os/reboot/required", () =>
    Results.Json(new { required = OsManager.IsRebootRequired() }));

// GET /os/reboot/scheduled — current scheduled reboot (or null)
app.MapGet("/os/reboot/scheduled", () =>
    Results.Json(osManager.GetScheduledReboot() ?? (object)new { scheduled = false }));

// POST /os/reboot/schedule — schedule a reboot in the next maintenance window
app.MapPost("/os/reboot/schedule", async (HttpRequest req) =>
{
    string? reason = null;
    try { var body = await req.ReadFromJsonAsync<Dictionary<string, string>>(); reason = body?.GetValueOrDefault("reason"); } catch { }
    var sched = osManager.ScheduleReboot(reason ?? "user requested");
    return Results.Json(new { ok = true, scheduledAt = sched.ScheduledAt, reason = sched.Reason });
});

// DELETE /os/reboot/schedule — cancel scheduled reboot
app.MapDelete("/os/reboot/schedule", () =>
{
    osManager.CancelScheduledReboot();
    return Results.Json(new { ok = true, cancelled = true });
});

// POST /os/reboot/now — reboot immediately (Linux only)
app.MapPost("/os/reboot/now", async () =>
{
    var (success, output) = await osManager.RebootNowAsync();
    return Results.Json(new { ok = success, output });
});

// GET /os/maintenance-window — current maintenance window config
app.MapGet("/os/maintenance-window", () =>
    Results.Json(osManager.GetMaintenanceWindow()));

// POST /os/maintenance-window — update maintenance window
app.MapPost("/os/maintenance-window", async (HttpRequest req) =>
{
    try
    {
        var w = await req.ReadFromJsonAsync<MaintenanceWindow>();
        if (w == null) return Results.BadRequest("Invalid window config");
        osManager.SetMaintenanceWindow(w);
        return Results.Json(new { ok = true, window = w });
    }
    catch (Exception ex) { return Results.BadRequest(ex.Message); }
});

// GET /os/firewall/rules — ufw status + rules
app.MapGet("/os/firewall/rules", () =>
    Results.Json(osManager.GetFirewallStatus()));

// POST /os/firewall/rule — add a ufw rule
app.MapPost("/os/firewall/rule", async (HttpRequest req) =>
{
    try
    {
        var rule = await req.ReadFromJsonAsync<FirewallRule>();
        if (rule == null || string.IsNullOrWhiteSpace(rule.Port))
            return Results.BadRequest("port is required");
        var (success, output) = osManager.AddFirewallRule(rule);
        return Results.Json(new { ok = success, output });
    }
    catch (Exception ex) { return Results.BadRequest(ex.Message); }
});

// POST /os/apt/update — run apt-get update
app.MapPost("/os/apt/update", async () =>
{
    var result = await aptManager.UpdateAsync();
    return Results.Json(result);
});

// POST /os/apt/upgrade — run apt-get upgrade
app.MapPost("/os/apt/upgrade", async () =>
{
    var result = await aptManager.UpgradeAsync();
    return Results.Json(result);
});

// POST /os/apt/autoremove — run apt-get autoremove + autoclean
app.MapPost("/os/apt/autoremove", async () =>
{
    var result = await aptManager.AutoremoveAsync();
    return Results.Json(result);
});

// POST /os/logs/cleanup — delete log files older than N days (default 30)
app.MapPost("/os/logs/cleanup", async (HttpRequest req) =>
{
    int keepDays = 30;
    try { var body = await req.ReadFromJsonAsync<Dictionary<string, int>>(); keepDays = body?.GetValueOrDefault("keepDays", 30) ?? 30; } catch { }
    var result = osManager.CleanLogs(keepDays);
    return Results.Json(result);
});

// Phase 37: Kiosk dashboard — serve web/dashboard.html at GET /dashboard
// The file lives at $ARCHIMEDES_REPO_ROOT/web/dashboard.html (same repo, no extra server needed).
app.MapGet("/dashboard", () =>
{
    var htmlFile = Path.Combine(repoRoot, "web", "dashboard.html");
    if (!File.Exists(htmlFile))
    {
        var fallback = "<html><body style='background:#080c10;color:#00d4aa;font-family:monospace;padding:40px'>" +
                       "<h1>&#x2B21; ARCHIMEDES</h1><p>Dashboard not found.</p>" +
                       $"<p style='color:#3d5068'>Expected: {htmlFile}</p>" +
                       "<p><a style='color:#00d4aa' href='/health'>/health</a> &nbsp; " +
                       "<a style='color:#00d4aa' href='/selfimprove/status'>/selfimprove/status</a></p></body></html>";
        return Results.Content(fallback, "text/html");
    }
    var html = File.ReadAllText(htmlFile);
    return Results.Content(html, "text/html; charset=utf-8");
});

// POST /admin/pull-update — git pull from main + run post-update.sh (handles everything)
// post-update.sh covers: LLM model upgrade, kiosk script, C# rebuild + service restart
app.MapPost("/admin/pull-update", async () =>
{
    static async Task<(string output, int exitCode)> RunCmd(string cmd, int timeoutSec = 60)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("bash", new[] { "-c", cmd })
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var sb = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        await proc.WaitForExitAsync(cts.Token);
        return (sb.ToString().Trim(), proc.ExitCode);
    }

    // 1. git pull
    var (pullOut, pullCode) = await RunCmd($"git -C \"{repoRoot}\" pull origin main 2>&1");
    if (pullCode != 0)
        return Results.Json(new { ok = false, message = $"git pull נכשל: {pullOut}" });

    bool alreadyUpToDate = pullOut.Contains("Already up to date") || pullOut.Contains("up-to-date");
    if (alreadyUpToDate)
        return Results.Json(new { ok = true, message = "כבר מעודכן — אין שינויים חדשים", updating = false });

    // 2. Detect what changed (for user-facing message only — post-update.sh re-detects internally)
    bool csChanged    = pullOut.Contains(".cs") || pullOut.Contains(".csproj");
    bool modelChanged = pullOut.Contains("upgrade-llm") || pullOut.Contains("bootstrap.sh");
    bool kioskChanged = pullOut.Contains("fix-kiosk") || pullOut.Contains("bootstrap.sh");
    bool scriptChange = pullOut.Contains("scripts/");

    var what = new List<string>();
    if (csChanged)    what.Add("קוד C#");
    if (modelChanged) what.Add("מודל LLM");
    if (kioskChanged) what.Add("קיוסק");
    if (!csChanged && !modelChanged && !kioskChanged) what.Add("קבצים");

    // 3. Run post-update.sh in background — handles model download, kiosk update, C# rebuild
    var postUpdateScript = Path.Combine(repoRoot, "scripts", "post-update.sh");
    _ = Task.Run(async () =>
    {
        await RunCmd($"bash \"{postUpdateScript}\"", timeoutSec: 1800); // up to 30min (model download)
    });

    var whatStr = string.Join(", ", what);
    var firstLine = pullOut.Split('\n')[0];
    return Results.Json(new
    {
        ok       = true,
        message  = $"מעדכן {whatStr} ברקע — {firstLine}",
        updating = true,
        csChanged,
        modelChanged,
        log      = "/tmp/archimedes-post-update.log"
    });
});

var port = int.TryParse(Environment.GetEnvironmentVariable("ARCHIMEDES_PORT"), out var p) ? p : 5051;
app.Urls.Add($"http://localhost:{port}");
Console.WriteLine($"Archimedes Core listening on http://localhost:{port}");
app.Lifetime.ApplicationStopping.Register(() =>
{
    llmAdapter.Dispose();
    goalRunner.Stop();
    selfImprovementEngine.Stop();
    osManager.Stop();
    androidBridge.Stop();
});
app.Run();

// ── Phase 22: Chat request model ───────────────────────────────────────────
public class ChatMessageRequest
{
    public string Message { get; set; } = "";
}

// ── Phase 24: Recovery respond request model ────────────────────────────────
public class RecoveryRespondRequest
{
    public string  Action { get; set; } = "";   // "retry" | "info" | "dismiss"
    public string? Info   { get; set; }         // optional user-provided context
}
