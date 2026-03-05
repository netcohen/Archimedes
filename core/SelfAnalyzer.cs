namespace Archimedes.Core;

/// <summary>
/// Phase 29 – Self-Analyzer.
///
/// Analyzes Archimedes' own data to generate self-improvement work items.
/// Always produces work — there is no empty state. When one batch is consumed
/// the next is generated from the next rotation.
///
/// Work is sourced from:
///   - ProcedureStore: find low-success-rate procedures
///   - ToolStore: find unused / underused tools
///   - Research topic rotation (technical topics relevant to Archimedes)
///   - LLM benchmarking (periodic accuracy / speed measurement)
///   - Dataset collection from successful traces
///   - Resource analysis (CPU / RAM patterns)
///   - Self-testing (endpoint health checks)
///   - Prompt experimentation (alternative prompt templates)
///   - Android app health analysis (Phase 32+)
/// </summary>
public sealed class SelfAnalyzer
{
    private readonly ProcedureStore _procedures;
    private readonly ToolStore      _tools;
    private readonly TraceService   _traces;

    // ── Research Topic Rotation ───────────────────────────────────────────
    private static readonly string[] ResearchTopics =
    [
        "LLM prompt engineering best practices for instruction-following models",
        "C# async/await performance optimization patterns",
        "efficient JSON serialization in .NET 8 with System.Text.Json",
        "browser automation reliability: retry patterns and selector stability",
        "LlamaSharp inference optimization: context size and batch settings",
        "task scheduling algorithms for mixed-priority workloads",
        "memory-efficient data structures in C# for long-running services",
        "AI agent self-improvement and meta-learning techniques",
        "local LLM fine-tuning with GGUF models and llama.cpp",
        "statistical anomaly detection for time-series metrics",
        "software reliability engineering: chaos engineering principles",
        "improving LLM output parsing robustness",
        "agent loop design patterns: planning, execution, reflection",
        "low-latency in-process messaging in .NET",
        "techniques for self-describing autonomous systems",
        // Phase 32+: Android app as part of Archimedes
        "Android Kotlin: background services, WorkManager, and battery optimization",
        "Firebase FCM push notification delivery reliability and fallback strategies",
        "ADB over WiFi: automation, security, and deployment best practices",
        "Android app architecture: MVVM, coroutines, and Firestore real-time sync",
    ];

    // ── Intent Rotation for Prompt Experiments ────────────────────────────
    private static readonly string[] Intents =
    [
        "TESTSITE_EXPORT",
        "TESTSITE_MONITOR",
        "FILE_DOWNLOAD",
        "LOGIN_FLOW",
    ];

    private int _researchIdx;
    private int _promptIdx;
    private int _cycleCount;

    public SelfAnalyzer(ProcedureStore procedures, ToolStore tools, TraceService traces)
    {
        _procedures = procedures;
        _tools      = tools;
        _traces     = traces;
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Generate a batch of work items. Never returns empty.
    /// Items are sorted by priority (highest first).
    /// </summary>
    public List<SelfWorkItem> GenerateWork(int count = 5)
    {
        _cycleCount++;
        var items = new List<SelfWorkItem>();

        items.AddRange(FindWeakProcedures());
        items.AddRange(FindUnusedTools());
        items.Add(NextResearchItem());
        items.Add(CreateBenchmarkItem());
        items.Add(CreateDatasetItem());
        items.Add(CreateResourceItem());
        items.Add(CreateSelfTestItem());

        if (_cycleCount % 3 == 0)
            items.Add(NextPromptExperiment());

        // Phase 32+: Android app is part of Archimedes — check its health every 4 cycles
        if (_cycleCount % 4 == 0)
            items.Add(CreateAndroidAppItem());

        // Always return at least `count` items sorted by priority
        return items
            .OrderByDescending(i => i.Priority)
            .Take(Math.Max(count, 3))
            .ToList();
    }

    // ── Work generators ───────────────────────────────────────────────────

    private List<SelfWorkItem> FindWeakProcedures()
    {
        var items = new List<SelfWorkItem>();
        var all   = _procedures.GetAll();

        // Any procedure with ≥3 uses and <40% success rate
        foreach (var p in all.Where(r => r.TotalUses >= 3 && r.SuccessRate < 0.40))
        {
            items.Add(new SelfWorkItem
            {
                Type        = SelfWorkType.ANALYZE_PROCEDURES,
                Description = $"פרוצדורה '{p.Intent}' — {p.SuccessRate:P0} הצלחה ב-{p.TotalUses} ריצות. דורשת שיפור.",
                Priority    = 8,
                TargetId    = p.Id,
                Context     = new Dictionary<string, string>
                {
                    ["intent"]      = p.Intent,
                    ["successRate"] = p.SuccessRate.ToString("F2"),
                    ["totalUses"]   = p.TotalUses.ToString(),
                }
            });
        }

        // General statistical analysis if there are procedures
        if (all.Count > 0)
        {
            var avgSuccess = all.Where(r => r.TotalUses > 0)
                               .Select(r => r.SuccessRate)
                               .DefaultIfEmpty(1.0)
                               .Average();

            items.Add(new SelfWorkItem
            {
                Type        = SelfWorkType.ANALYZE_PROCEDURES,
                Description = $"ניתוח סטטיסטי כולל של {all.Count} פרוצדורות — שיעור הצלחה ממוצע {avgSuccess:P0}",
                Priority    = 5,
                Context     = new Dictionary<string, string>
                {
                    ["procedureCount"] = all.Count.ToString(),
                    ["avgSuccessRate"] = avgSuccess.ToString("F2"),
                }
            });
        }

        return items;
    }

    private List<SelfWorkItem> FindUnusedTools()
    {
        var items  = new List<SelfWorkItem>();
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var tools  = _tools.GetAllTools();

        foreach (var t in tools.Where(x => x.LastUsedAt.HasValue && x.LastUsedAt < cutoff))
        {
            var days = (int)(DateTime.UtcNow - t.LastUsedAt!.Value).TotalDays;
            items.Add(new SelfWorkItem
            {
                Type        = SelfWorkType.ANALYZE_TOOL_USAGE,
                Description = $"כלי '{t.Capability}' — לא בשימוש {days} ימים. בדיקת רלוונטיות.",
                Priority    = 4,
                TargetId    = t.ToolId,
                Context     = new Dictionary<string, string>
                {
                    ["capability"]   = t.Capability,
                    ["daysSinceUse"] = days.ToString(),
                    ["usageCount"]   = t.UsageCount.ToString(),
                }
            });
        }

        // If there are tools but none unused, analyze usage patterns
        if (tools.Count > 0 && items.Count == 0)
        {
            items.Add(new SelfWorkItem
            {
                Type        = SelfWorkType.ANALYZE_TOOL_USAGE,
                Description = $"ניתוח דפוסי שימוש ב-{tools.Count} כלים — זיהוי הנפוצים ביותר",
                Priority    = 3,
            });
        }

        return items;
    }

    private SelfWorkItem NextResearchItem()
    {
        var topic = ResearchTopics[_researchIdx % ResearchTopics.Length];
        _researchIdx++;
        return new SelfWorkItem
        {
            Type        = SelfWorkType.RESEARCH_WEB,
            Description = $"מחקר: {topic}",
            Priority    = 3,
            Context     = new Dictionary<string, string> { ["topic"] = topic }
        };
    }

    private SelfWorkItem CreateBenchmarkItem()
    {
        return new SelfWorkItem
        {
            Type        = SelfWorkType.BENCHMARK_LLM,
            Description = "בנצ'מארק LLM — מדידת דיוק intent, זמן תגובה ושימוש בזיכרון",
            Priority    = 6,
        };
    }

    private SelfWorkItem CreateDatasetItem()
    {
        return new SelfWorkItem
        {
            Type        = SelfWorkType.COLLECT_DATASET,
            Description = "איסוף דוגמאות הצלחה מ-traces לבניית dataset לאימון עתידי",
            Priority    = 4,
        };
    }

    private SelfWorkItem CreateResourceItem()
    {
        return new SelfWorkItem
        {
            Type        = SelfWorkType.ANALYZE_RESOURCES,
            Description = "ניתוח שימוש ב-CPU ו-RAM — זיהוי בזבוז ועומסים חריגים",
            Priority    = 5,
        };
    }

    private SelfWorkItem CreateSelfTestItem()
    {
        return new SelfWorkItem
        {
            Type        = SelfWorkType.SELF_TEST,
            Description = "בדיקת תקינות עצמית — בדיקת endpoints ומצב מערכת",
            Priority    = 7,
        };
    }

    /// <summary>
    /// Phase 32+: Android app health check item.
    /// Archimedes treats its Android app as an integral part of itself —
    /// monitoring device registration, FCM connectivity, and OTA update readiness.
    /// </summary>
    private static SelfWorkItem CreateAndroidAppItem()
    {
        return new SelfWorkItem
        {
            Type        = SelfWorkType.ANALYZE_ANDROID_APP,
            Description = "ניתוח מצב אפליקציה אנדרואיד — בדיקת רישום מכשיר, FCM, ADB ומוכנות לעדכון OTA",
            Priority    = 5,
        };
    }

    private SelfWorkItem NextPromptExperiment()
    {
        var intent = Intents[_promptIdx % Intents.Length];
        _promptIdx++;
        return new SelfWorkItem
        {
            Type        = SelfWorkType.EXPERIMENT_PROMPT,
            Description = $"ניסוי prompt חלופי עבור '{intent}' — מדידת שיפור דיוק",
            Priority    = 6,
            Context     = new Dictionary<string, string> { ["intent"] = intent }
        };
    }
}
