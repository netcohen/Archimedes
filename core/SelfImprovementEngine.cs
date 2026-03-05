using System.Diagnostics;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 29 – Self-Improvement Engine.
///
/// The core of Archimedes' autonomy. Runs 24/7 in the background.
/// There is always work to do — no idle state.
///
/// Archimedes treats ALL its components as part of itself:
///   - Core (.NET service)    — procedures, LLM, research, code patching
///   - Net service (Node.js)  — FCM, Firestore relay, device registry
///   - Android app (Kotlin)   — the "eyes and hands" on mobile (Phase 32+)
///
/// Priority model:
///   User tasks  → always take precedence
///   Engine      → pauses when user tasks are active or CPU is high
///   Resume      → continues from checkpoint when resources are available
///
/// Work types: research, LLM benchmarking, procedure analysis, tool analysis,
///             resource analysis, prompt experimentation, dataset collection,
///             self-testing, Core code patching, Android app analysis (Phase 32+).
///
/// Git: when PATCH_CORE_CODE is applied, commits and pushes the Core .cs
///      changes for user visibility and audit. Acquired feature scripts
///      are NOT committed.
/// </summary>
public sealed class SelfImprovementEngine
{
    // ── Dependencies ──────────────────────────────────────────────────────
    private readonly SelfAnalyzer        _analyzer;
    private readonly SelfImprovementStore _store;
    private readonly ResourceGuard       _guard;
    private readonly ProcedureStore      _procedures;
    private readonly ToolStore           _tools;
    private readonly TraceService        _traces;
    private readonly LLMAdapter          _llm;
    private readonly SelfGitManager      _git;
    // Phase 34: Eyes (web research) + Hands (code patching)
    private readonly SearchOrchestrator  _searchOrchestrator;
    private readonly CodePatcher         _codePatcher;
    // Phase 32+: Android app is part of Archimedes — wired after construction
    private AppUpdater?                  _appUpdater;

    // ── State ─────────────────────────────────────────────────────────────
    private readonly Queue<SelfWorkItem> _queue      = new();
    private readonly object              _queueLock  = new();
    private bool                         _running;
    private CancellationTokenSource?     _cts;

    private SelfEngineState  _state               = SelfEngineState.STOPPED;
    private SelfWorkItem?    _currentItem;
    private string           _currentStep         = "";
    private DateTime?        _lastCompletedAt;
    private string?          _userFocusTopic;

    // User task signal — incremented when a user task starts, decremented on completion
    private int _activeUserTasks;

    // ── Timing ────────────────────────────────────────────────────────────
    private static readonly TimeSpan NormalDelay    = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ThrottledDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PausedDelay    = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MaxItemRuntime = TimeSpan.FromMinutes(5);

    // ── HTTP for self-test ────────────────────────────────────────────────
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly string     _selfUrl =
        $"http://localhost:{Environment.GetEnvironmentVariable("ARCHIMEDES_PORT") ?? "5051"}";

    // ── LLM benchmark test cases ──────────────────────────────────────────
    private static readonly (string prompt, string expected)[] BenchmarkCases =
    [
        ("export data from testsite",            "TESTSITE_EXPORT"),
        ("monitor testsite dashboard",           "TESTSITE_MONITOR"),
        ("download the report file",             "FILE_DOWNLOAD"),
        ("login to the portal",                  "LOGIN_FLOW"),
        ("schedule a monitoring task for today", "TESTSITE_MONITOR"),
    ];

    // ── Constructor ───────────────────────────────────────────────────────

    public SelfImprovementEngine(
        SelfAnalyzer         analyzer,
        SelfImprovementStore store,
        ResourceGuard        guard,
        ProcedureStore       procedures,
        ToolStore            tools,
        TraceService         traces,
        LLMAdapter           llm,
        SelfGitManager       git,
        SearchOrchestrator   searchOrchestrator,   // Phase 34: real web research
        CodePatcher          codePatcher)           // Phase 34: real code patching
    {
        _analyzer           = analyzer;
        _store              = store;
        _guard              = guard;
        _procedures         = procedures;
        _tools              = tools;
        _traces             = traces;
        _llm                = llm;
        _git                = git;
        _searchOrchestrator = searchOrchestrator;
        _codePatcher        = codePatcher;

        _guard.OnThrottle += () => ArchLogger.LogInfo("[SelfImprove] Throttling — CPU high");
        _guard.OnPause    += () => ArchLogger.LogInfo("[SelfImprove] Pausing — CPU critical");
        _guard.OnResume   += () => ArchLogger.LogInfo("[SelfImprove] Resuming — CPU normal");
    }

    // ── Android integration (Phase 32+) ───────────────────────────────────

    /// <summary>
    /// Wire the Android OTA updater after construction (avoids circular instantiation order).
    /// Called from Program.cs after both SelfImprovementEngine and AppUpdater are created.
    /// </summary>
    public void SetAppUpdater(AppUpdater appUpdater)
    {
        _appUpdater = appUpdater;
        ArchLogger.LogInfo("[SelfImprove] Android app wired — Archimedes now monitors its mobile component");
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public void Start()
    {
        if (_running) return;
        _running = true;
        _state   = SelfEngineState.STARTING;
        _cts     = new CancellationTokenSource();
        _guard.Start();

        // Resume from checkpoint if one exists
        var cp = _store.LoadCheckpoint();
        if (cp != null)
        {
            ArchLogger.LogInfo($"[SelfImprove] Resuming from checkpoint: {cp.WorkItem.Description}");
            lock (_queueLock) { _queue.Enqueue(cp.WorkItem); }
            _store.ClearCheckpoint();
        }

        _ = Task.Run(() => LoopAsync(_cts.Token));
        ArchLogger.LogInfo("[SelfImprove] Engine started — always-on self-improvement active");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _running = false;
        _state   = SelfEngineState.STOPPED;
        _guard.Dispose();
    }

    // ── User task signaling ───────────────────────────────────────────────

    /// <summary>Called when a user-initiated task starts executing.</summary>
    public void NotifyUserTaskStarted()
    {
        Interlocked.Increment(ref _activeUserTasks);
    }

    /// <summary>Called when a user-initiated task finishes (success, failure, or cancel).</summary>
    public void NotifyUserTaskCompleted()
    {
        var v = Interlocked.Decrement(ref _activeUserTasks);
        if (v < 0) Interlocked.Exchange(ref _activeUserTasks, 0);
    }

    // ── User focus redirect ───────────────────────────────────────────────

    /// <summary>
    /// Redirect self-improvement focus to a specific topic.
    /// Saves current checkpoint, clears queue, and queues the requested topic first.
    /// </summary>
    public void RedirectFocus(string topic)
    {
        _userFocusTopic = topic;

        // Save checkpoint for current item if running
        if (_currentItem != null)
        {
            _store.SaveCheckpoint(new SelfWorkCheckpoint
            {
                WorkItem   = _currentItem,
                StepName   = _currentStep,
                SavedState = new Dictionary<string, string> { ["redirected"] = topic },
            });
        }

        // Clear queue and inject high-priority research on the requested topic
        lock (_queueLock)
        {
            _queue.Clear();
            _queue.Enqueue(new SelfWorkItem
            {
                Type        = SelfWorkType.RESEARCH_WEB,
                Description = $"מחקר ממוקד לפי הוראת משתמש: {topic}",
                Priority    = 10,
                Context     = new Dictionary<string, string> { ["topic"] = topic }
            });
        }

        ArchLogger.LogInfo($"[SelfImprove] Focus redirected by user: {topic}");
    }

    // ── Status ────────────────────────────────────────────────────────────

    public SelfImprovementStatus GetStatus() => new()
    {
        State                  = _state,
        CurrentWorkDescription = _currentItem?.Description,
        CurrentStep            = _currentStep,
        ThrottleReason         = _guard.ThrottleReason,
        CpuPercent             = _guard.LastCpuPercent,
        RamUsedMb              = _guard.LastRamUsedMb,
        TotalCompleted         = _store.TotalCompleted,
        TotalSuccessful        = _store.TotalSuccessful,
        QueueLength            = _queue.Count,
        LastCompletedAt        = _lastCompletedAt,
        LastInsight            = _store.GetLatestInsight(),
        UserFocusTopic         = _userFocusTopic,
        ActiveUserTasks        = _activeUserTasks,
    };

    /// <summary>Short Hebrew description of current activity — for the status bar.</summary>
    public string? GetCurrentActivityDescription()
    {
        if (_state == SelfEngineState.STOPPED) return null;
        if (_state == SelfEngineState.PAUSED || _activeUserTasks > 0)
            return "🔬 פיתוח עצמי: ממתין למשאבים";
        if (_state == SelfEngineState.THROTTLED)
            return "🔬 פיתוח עצמי: מאט (עומס CPU)";
        if (_currentItem != null)
            return $"🔬 {_currentItem.Description}";
        return "🔬 פיתוח עצמי: פעיל";
    }

    // ── Main loop ─────────────────────────────────────────────────────────

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ArchLogger.LogWarn($"[SelfImprove] Loop error: {ex.Message}");
                await SafeDelay(NormalDelay, ct);
            }
        }
        _state = SelfEngineState.STOPPED;
        ArchLogger.LogInfo("[SelfImprove] Engine stopped");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Pause if user tasks are active
        if (_activeUserTasks > 0)
        {
            _state = SelfEngineState.PAUSED;
            await SafeDelay(PausedDelay, ct);
            return;
        }

        // Pause if CPU is critical
        if (_guard.ShouldPause)
        {
            _state = SelfEngineState.PAUSED;
            await SafeDelay(PausedDelay, ct);
            return;
        }

        // Throttle if CPU is elevated
        if (_guard.ShouldThrottle)
        {
            _state = SelfEngineState.THROTTLED;
            await SafeDelay(ThrottledDelay, ct);
            return;
        }

        // Refill queue when it runs low
        lock (_queueLock)
        {
            if (_queue.Count < 3)
            {
                var newItems = _analyzer.GenerateWork(5);
                foreach (var wi in newItems) _queue.Enqueue(wi);
                ArchLogger.LogInfo($"[SelfImprove] Queue refilled — {_queue.Count} items");
            }
        }

        // Dequeue next item
        SelfWorkItem? item;
        lock (_queueLock)
        {
            if (!_queue.TryDequeue(out item)) { _state = SelfEngineState.IDLE; return; }
        }

        // Execute
        _state       = SelfEngineState.RUNNING;
        _currentItem = item;
        _currentStep = "מתחיל...";
        item.Status  = SelfWorkStatus.IN_PROGRESS;

        var result = await ExecuteItemAsync(item, ct);

        _store.AddResult(result);
        if (result.Insight != null) _store.AddInsight(result.Insight);
        _lastCompletedAt = DateTime.UtcNow;
        _currentItem     = null;
        _currentStep     = "";
        _state           = SelfEngineState.IDLE;

        ArchLogger.LogInfo($"[SelfImprove] {item.Type} {(result.Success ? "OK" : "FAIL")}: {result.Summary}");

        // Brief breathing room
        var delay = _guard.ShouldThrottle ? ThrottledDelay : NormalDelay;
        await SafeDelay(delay, ct);
    }

    // ── Item execution ────────────────────────────────────────────────────

    private async Task<SelfWorkResult> ExecuteItemAsync(SelfWorkItem item, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MaxItemRuntime);

        var startedAt = DateTime.UtcNow;
        var result    = new SelfWorkResult
        {
            WorkItemId      = item.Id,
            Type            = item.Type,
            WorkDescription = item.Description,
            StartedAt       = startedAt,
        };

        try
        {
            switch (item.Type)
            {
                case SelfWorkType.ANALYZE_PROCEDURES:
                    await ExecuteAnalyzeProcedures(item, result, timeout.Token);
                    break;

                case SelfWorkType.BENCHMARK_LLM:
                    await ExecuteBenchmarkLlm(result, timeout.Token);
                    break;

                case SelfWorkType.RESEARCH_WEB:
                    await ExecuteResearch(item, result, timeout.Token);
                    break;

                case SelfWorkType.COLLECT_DATASET:
                    await ExecuteCollectDataset(result, timeout.Token);
                    break;

                case SelfWorkType.SELF_TEST:
                    await ExecuteSelfTest(result, timeout.Token);
                    break;

                case SelfWorkType.ANALYZE_RESOURCES:
                    await ExecuteAnalyzeResources(result, timeout.Token);
                    break;

                case SelfWorkType.ANALYZE_TOOL_USAGE:
                    await ExecuteAnalyzeToolUsage(item, result, timeout.Token);
                    break;

                case SelfWorkType.EXPERIMENT_PROMPT:
                    await ExecutePromptExperiment(item, result, timeout.Token);
                    break;

                case SelfWorkType.PATCH_CORE_CODE:
                    // Phase 34: real code patching (LLM → sandbox build/test → git commit)
                    await ExecutePatchCoreCode(result, timeout.Token);
                    break;

                case SelfWorkType.ANALYZE_ANDROID_APP:
                    await ExecuteAnalyzeAndroidApp(result, timeout.Token);
                    break;

                default:
                    result.Summary = $"סוג עבודה לא מוכר: {item.Type}";
                    result.Success = false;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Preempted — save checkpoint
            _store.SaveCheckpoint(new SelfWorkCheckpoint
            {
                WorkItem   = item,
                StepName   = _currentStep,
                SavedState = new Dictionary<string, string>(),
            });
            item.Status    = SelfWorkStatus.PAUSED;
            result.Success = false;
            result.Summary = "הופסק — checkpoint נשמר";
        }
        catch (Exception ex)
        {
            item.Status       = SelfWorkStatus.FAILED;
            result.Success    = false;
            result.ErrorMessage = ex.Message;
            result.Summary    = $"שגיאה: {ex.Message[..Math.Min(80, ex.Message.Length)]}";
        }

        result.CompletedAt = DateTime.UtcNow;
        item.Status        = result.Success ? SelfWorkStatus.COMPLETED : item.Status;
        return result;
    }

    // ── Work type implementations ─────────────────────────────────────────

    private async Task ExecuteAnalyzeProcedures(
        SelfWorkItem item, SelfWorkResult result, CancellationToken ct)
    {
        _currentStep = "טוען פרוצדורות...";
        var all = _procedures.GetAll();

        if (all.Count == 0)
        {
            result.Success = true;
            result.Summary = "אין פרוצדורות לניתוח עדיין";
            return;
        }

        var withData    = all.Where(p => p.TotalUses > 0).ToList();
        var avgRate     = withData.Count > 0 ? withData.Average(p => p.SuccessRate) : 1.0;
        var worstProcs  = withData.Where(p => p.SuccessRate < 0.5).OrderBy(p => p.SuccessRate).Take(3).ToList();
        var totalRuns   = all.Sum(p => p.TotalUses);

        _currentStep = "מנתח דפוסי כישלון...";

        string insight;
        if (worstProcs.Count > 0)
        {
            var worstDesc = string.Join(", ", worstProcs.Select(p =>
                $"'{p.Intent}' {p.SuccessRate:P0}"));
            insight = $"ניתוח פרוצדורות: {all.Count} פרוצדורות, {totalRuns} ריצות כולל. " +
                      $"שיעור הצלחה ממוצע: {avgRate:P0}. " +
                      $"פרוצדורות חלשות: {worstDesc}";
        }
        else
        {
            insight = $"ניתוח פרוצדורות: {all.Count} פרוצדורות, {totalRuns} ריצות. " +
                      $"שיעור הצלחה ממוצע: {avgRate:P0}. כל הפרוצדורות תקינות.";
        }

        // If a specific procedure was targeted, ask LLM for improvement suggestions
        if (item.TargetId != null)
        {
            _currentStep = "מבקש הצעות שיפור מ-LLM...";
            var proc = _procedures.GetById(item.TargetId);
            if (proc != null)
            {
                var suggestion = await _llm.AskAsync(
                    "You are an AI agent optimizer. Analyze why a procedure might fail and suggest improvements.",
                    $"Procedure intent: {proc.Intent}\nSuccess rate: {proc.SuccessRate:P0}\n" +
                    $"Total runs: {proc.TotalUses}\nWhat could cause this low success rate? (2-3 sentences)",
                    256);

                if (!string.IsNullOrWhiteSpace(suggestion))
                    insight += $"\nהצעת LLM ל-'{proc.Intent}': {suggestion}";
            }
        }

        result.Success = true;
        result.Insight = insight;
        result.Summary = $"ניתוח {all.Count} פרוצדורות — ממוצע {avgRate:P0}";

        await Task.Delay(100, ct); // yield
    }

    private async Task ExecuteBenchmarkLlm(SelfWorkResult result, CancellationToken ct)
    {
        _currentStep = "מריץ בנצ'מארק LLM...";
        var times    = new List<long>();
        var correct  = 0;
        var total    = BenchmarkCases.Length;

        foreach (var (prompt, expected) in BenchmarkCases)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var r  = await _llm.Interpret(prompt);
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);
            if (r.Intent == expected) correct++;
            await Task.Delay(200, ct);
        }

        var avgMs    = times.Count > 0 ? (long)times.Average() : 0;
        var accuracy = (double)correct / total;
        var ram      = SystemMetricsHelper.GetRamUsedMb();

        result.Success = true;
        result.Insight = $"בנצ'מארק LLM: {correct}/{total} נכון ({accuracy:P0}), " +
                         $"זמן ממוצע {avgMs}ms, RAM {ram:F0}MB";
        result.Summary = $"LLM: {accuracy:P0} דיוק, avg {avgMs}ms";
    }

    private async Task ExecuteResearch(
        SelfWorkItem item, SelfWorkResult result, CancellationToken ct)
    {
        var topic = item.Context.TryGetValue("topic", out var t) ? t : item.Description;
        _currentStep = $"גולש ברשת: {topic[..Math.Min(40, topic.Length)]}...";

        // Phase 34: real web search via SearchOrchestrator (DuckDuckGo + Tor if available)
        List<string> snippets = [];
        try
        {
            snippets = await _searchOrchestrator.ResearchTopicAsync(topic, ct);
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[SelfImprove] Web search failed, using internal knowledge: {ex.Message}");
        }

        // Build LLM prompt — enriched with web snippets when available
        _currentStep = "מסכם ממצאים עם LLM...";
        string prompt;
        if (snippets.Count > 0)
        {
            var webContent = string.Join("\n---\n", snippets);
            prompt = $"Research topic: {topic}\n\n" +
                     $"Web search results:\n{webContent}\n\n" +
                     "Based on the above web results, synthesize the most actionable insights in 3-4 sentences.";
        }
        else
        {
            prompt = $"Research topic: {topic}\n\n" +
                     "Summarize the most important practical insights in 3-4 sentences.";
        }

        var response = await _llm.AskAsync(
            "You are a technical research assistant for an autonomous AI agent system. " +
            "Provide a concise, actionable summary with key insights the agent can apply.",
            prompt, 384);

        var webFlag = snippets.Count > 0
            ? $"(web: {snippets.Count} sources{(_searchOrchestrator.IsTorAvailable ? " via Tor" : "")})"
            : "(internal knowledge)";

        if (!string.IsNullOrWhiteSpace(response))
        {
            result.Success = true;
            result.Insight = $"מחקר '{topic[..Math.Min(50, topic.Length)]}' {webFlag}: {response}";
            result.Summary = $"מחקר הושלם {webFlag}";
        }
        else
        {
            result.Success = false;
            result.Summary = "LLM לא החזיר תוצאת מחקר";
        }

        await Task.Delay(100, ct);
    }

    private async Task ExecuteCollectDataset(SelfWorkResult result, CancellationToken ct)
    {
        _currentStep = "אוסף דוגמאות מ-traces...";
        var traces   = _traces.GetRecent(30);
        var added    = 0;

        foreach (var trace in traces.Where(tr => tr.Success))
        {
            ct.ThrowIfCancellationRequested();

            var entry = System.Text.Json.JsonSerializer.Serialize(new
            {
                endpoint   = trace.Endpoint,
                steps      = trace.Steps?.Count ?? 0,
                durationMs = trace.TotalDurationMs,
                timestamp  = trace.StartedAtUtc,
            });

            _store.AppendDatasetEntry(entry);
            added++;
        }

        var total  = _store.DatasetEntriesCount();
        result.Success = true;
        result.Insight = $"Dataset: נוספו {added} דוגמאות. סה\"כ {total} דוגמאות בקובץ.";
        result.Summary = $"נאספו {added} דוגמאות הצלחה";

        await Task.Delay(100, ct);
    }

    private async Task ExecuteSelfTest(SelfWorkResult result, CancellationToken ct)
    {
        _currentStep = "בודק /health...";
        var tests    = new List<(string name, bool pass)>();

        // Health check
        try
        {
            var r = await _http.GetAsync($"{_selfUrl}/health", ct);
            tests.Add(("/health", r.IsSuccessStatusCode));
        }
        catch { tests.Add(("/health", false)); }

        // LLM health
        _currentStep = "בודק /llm/health...";
        try
        {
            var r = await _http.GetAsync($"{_selfUrl}/llm/health", ct);
            tests.Add(("/llm/health", r.IsSuccessStatusCode));
        }
        catch { tests.Add(("/llm/health", false)); }

        // Self-improve status
        _currentStep = "בודק /selfimprove/status...";
        try
        {
            var r = await _http.GetAsync($"{_selfUrl}/selfimprove/status", ct);
            tests.Add(("/selfimprove/status", r.IsSuccessStatusCode));
        }
        catch { tests.Add(("/selfimprove/status", false)); }

        // Android app health (Phase 32+)
        _currentStep = "בודק מצב אפליקציה אנדרואיד...";
        try
        {
            var netUrl    = Environment.GetEnvironmentVariable("ARCHIMEDES_NET_URL") ?? "http://localhost:5052";
            var r         = await _http.GetAsync($"{netUrl}/v1/android/devices", ct);
            var hasPhone  = false;
            if (r.IsSuccessStatusCode)
            {
                var body = await r.Content.ReadAsStringAsync(ct);
                using var d = JsonDocument.Parse(body);
                hasPhone = d.RootElement.GetArrayLength() > 0;
            }
            tests.Add(("android/devices", r.IsSuccessStatusCode));
            // Not having a registered phone is OK (expected when app not running) — don't fail test
        }
        catch { tests.Add(("android/devices", false)); }

        var passed = tests.Count(t => t.pass);
        var total  = tests.Count;
        result.Success = passed == total;
        result.Insight = $"בדיקת תקינות: {passed}/{total} תקין — " +
                         string.Join(", ", tests.Select(t => $"{t.name}:{(t.pass ? "✓" : "✗")}"));
        result.Summary = $"Self-test: {passed}/{total} PASS";
    }

    private async Task ExecuteAnalyzeResources(SelfWorkResult result, CancellationToken ct)
    {
        _currentStep = "דוגם CPU/RAM...";
        var cpuSamples = new List<double>();
        var ramSamples = new List<double>();

        for (int i = 0; i < 6; i++)
        {
            ct.ThrowIfCancellationRequested();
            cpuSamples.Add(SystemMetricsHelper.GetCpuPercent());
            ramSamples.Add(SystemMetricsHelper.GetRamUsedMb());
            await Task.Delay(5_000, ct);
        }

        var avgCpu = cpuSamples.Average();
        var maxCpu = cpuSamples.Max();
        var avgRam = ramSamples.Average();
        var maxRam = ramSamples.Max();

        result.Success = true;
        result.Insight = $"ניתוח משאבים (30 שניות): CPU avg {avgCpu:F1}% max {maxCpu:F1}%, " +
                         $"RAM avg {avgRam:F0}MB max {maxRam:F0}MB";
        result.Summary = $"CPU: {avgCpu:F1}% avg | RAM: {avgRam:F0}MB avg";
    }

    private async Task ExecuteAnalyzeToolUsage(
        SelfWorkItem item, SelfWorkResult result, CancellationToken ct)
    {
        _currentStep = "מנתח שימוש בכלים...";
        var tools = _tools.GetAllTools();

        if (tools.Count == 0)
        {
            result.Success = true;
            result.Summary = "אין כלים נרכשים לניתוח";
            return;
        }

        var used   = tools.Where(t => t.UsageCount > 0).OrderByDescending(t => t.UsageCount).Take(3);
        var unused = tools.Where(t => t.UsageCount == 0).ToList();
        var avgRel = tools.Average(t => t.ReliabilityScore);

        var insight = $"ניתוח {tools.Count} כלים: reliability ממוצע {avgRel:P0}. " +
                      $"הנפוצים: {string.Join(", ", used.Select(t => t.Capability))}";

        if (unused.Count > 0)
            insight += $". לא בשימוש: {unused.Count} כלים";

        result.Success = true;
        result.Insight = insight;
        result.Summary = $"{tools.Count} כלים, {unused.Count} לא בשימוש";

        await Task.Delay(100, ct);
    }

    private async Task ExecutePromptExperiment(
        SelfWorkItem item, SelfWorkResult result, CancellationToken ct)
    {
        var intent = item.Context.TryGetValue("intent", out var i) ? i : "TESTSITE_EXPORT";
        _currentStep = $"מנסה prompt חלופי ל-{intent}...";

        // Standard variant
        var standard = $"export data from testsite now";
        var sw1      = Stopwatch.StartNew();
        var r1       = await _llm.Interpret(standard);
        sw1.Stop();
        await Task.Delay(300, ct);

        // Alternative variant
        var alt  = $"I need to download the testsite report";
        var sw2  = Stopwatch.StartNew();
        var r2   = await _llm.Interpret(alt);
        sw2.Stop();

        var stdCorrect = r1.Intent == intent;
        var altCorrect = r2.Intent == intent;

        result.Success = true;
        result.Insight = $"ניסוי prompt '{intent}': " +
                         $"סטנדרטי={r1.Intent}({sw1.ElapsedMilliseconds}ms) " +
                         $"חלופי={r2.Intent}({sw2.ElapsedMilliseconds}ms) " +
                         $"std={stdCorrect} alt={altCorrect}";
        result.Summary = $"Prompt experiment: std={stdCorrect} alt={altCorrect}";
    }

    // ── Code Patching (Phase 34) ──────────────────────────────────────────

    private async Task ExecutePatchCoreCode(SelfWorkResult result, CancellationToken ct)
    {
        _currentStep = "בוחר יעד לשיפור קוד...";
        var insight  = _store.GetLatestInsight();

        _currentStep = "מייצר שיפור קוד עם LLM...";
        var patchResult = await _codePatcher.TryPatchAsync(insight, ct);

        result.Success = patchResult.Success;
        result.Summary = patchResult.Summary;

        if (patchResult.Success)
        {
            result.Insight = $"קוד שופר: {patchResult.TargetMethod} ב-{patchResult.TargetFile} — " +
                             "build+tests עברו, commit נוצר";
            ArchLogger.LogInfo($"[SelfImprove] Code patch committed: {patchResult.TargetMethod}");
        }
        else
        {
            ArchLogger.LogInfo($"[SelfImprove] Code patch skipped/reverted: {patchResult.Summary}");
        }
    }

    // ── Android App Analysis (Phase 32+) ─────────────────────────────────

    /// <summary>
    /// Archimedes checks the health of its own Android app:
    ///   - How many devices are registered (phone reported its IP)?
    ///   - Is FCM token registered for push delivery?
    ///   - Is the OTA update infrastructure ready (ADB, build script)?
    ///   - Has the app been recently updated?
    ///
    /// The Android app is not a separate product — it is Archimedes' mobile component.
    /// </summary>
    private async Task ExecuteAnalyzeAndroidApp(SelfWorkResult result, CancellationToken ct)
    {
        _currentStep = "בודק מצב אפליקציה אנדרואיד...";
        var parts    = new List<string>();
        var problems = new List<string>();
        var netUrl   = Environment.GetEnvironmentVariable("ARCHIMEDES_NET_URL") ?? "http://localhost:5052";

        // 1. Check registered devices from Net service
        _currentStep = "בודק מכשירים רשומים...";
        string? phoneIp    = null;
        int     deviceCount = 0;
        try
        {
            var json = await _http.GetStringAsync($"{netUrl}/v1/android/devices", ct);
            using var doc = JsonDocument.Parse(json);
            deviceCount = doc.RootElement.GetArrayLength();
            if (deviceCount > 0)
            {
                parts.Add($"{deviceCount} מכשיר(ים) רשום(ים) ✓");
                var first = doc.RootElement[0];
                if (first.TryGetProperty("ip", out var ipEl) && ipEl.ValueKind == JsonValueKind.String)
                    phoneIp = ipEl.GetString();
                if (phoneIp != null)
                    parts.Add($"IP: {phoneIp}");
            }
            else
            {
                problems.Add("אין מכשיר אנדרואיד רשום — האפליקציה לא רצה/לא מחוברת לרשת");
            }
        }
        catch { problems.Add("Net service לא נגיש"); }

        // 2. Check OTA updater status from Core
        _currentStep = "בודק מצב מנגנון עדכון OTA...";
        try
        {
            // Use the injected AppUpdater directly when available (faster + no HTTP)
            object otaStatus = _appUpdater != null
                ? _appUpdater.GetStatus()
                : (object)await _http.GetStringAsync($"{_selfUrl}/android/update/status", ct);

            bool scriptExists, adbAvailable, updateRunning;

            if (_appUpdater != null)
            {
                // Use reflection-free dynamic access via serialization
                var json2 = JsonSerializer.Serialize(_appUpdater.GetStatus());
                using var d2 = JsonDocument.Parse(json2);
                scriptExists  = d2.RootElement.GetProperty("scriptExists").GetBoolean();
                adbAvailable  = d2.RootElement.GetProperty("adbAvailable").GetBoolean();
                updateRunning = d2.RootElement.GetProperty("running").GetBoolean();
            }
            else
            {
                var json2 = (string)otaStatus;
                using var d2 = JsonDocument.Parse(json2);
                scriptExists  = d2.RootElement.GetProperty("scriptExists").GetBoolean();
                adbAvailable  = d2.RootElement.GetProperty("adbAvailable").GetBoolean();
                updateRunning = d2.RootElement.GetProperty("running").GetBoolean();
            }

            if (scriptExists)  parts.Add("סקריפט עדכון קיים ✓");
            else               problems.Add("סקריפט update-android.sh חסר");

            if (adbAvailable)  parts.Add("ADB זמין ✓");
            else               problems.Add("ADB לא מותקן — לא ניתן לבצע OTA WiFi");

            if (updateRunning) parts.Add("עדכון בתהליך...");
        }
        catch (Exception ex)
        {
            problems.Add($"שגיאה בבדיקת OTA: {ex.Message[..Math.Min(50, ex.Message.Length)]}");
        }

        // 3. Compose insight
        var healthy = problems.Count == 0;
        var summary = healthy
            ? $"אנדרואיד: {deviceCount} מכשיר(ים) — תקין"
            : $"אנדרואיד: {string.Join("; ", problems)}";

        result.Success = healthy;
        result.Insight = $"מצב אפליקציה אנדרואיד | " +
                         (parts.Count > 0 ? string.Join(", ", parts) : "לא פעיל") +
                         (problems.Count > 0 ? " | בעיות: " + string.Join(", ", problems) : "");
        result.Summary = summary;

        await Task.Delay(100, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (TaskCanceledException) { }
    }

    public bool IsRunning => _running;
}
