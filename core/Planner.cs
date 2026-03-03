using System.Text.Json;
using System.Text.Json.Serialization;

namespace Archimedes.Core;

/// <summary>
/// Deterministic planner that uses LLM only for intent interpretation,
/// then builds deterministic plans for known intents.
/// </summary>
public class Planner
{
    private readonly LLMAdapter      _llmAdapter;
    private readonly PolicyEngine    _policyEngine;
    private readonly ProcedureStore? _procedureStore;

    public Planner(LLMAdapter llmAdapter, PolicyEngine policyEngine,
                   ProcedureStore? procedureStore = null)
    {
        _llmAdapter      = llmAdapter;
        _policyEngine    = policyEngine;
        _procedureStore  = procedureStore;
    }
    
    /// <summary>
    /// Plan a task based on user prompt.
    /// Returns a deterministic plan for known intents, or null if intent unknown.
    /// </summary>
    public async Task<PlanResult> PlanTask(string taskId, string userPrompt)
    {
        ArchLogger.LogInfo($"[Planner] Planning task {taskId}, prompt hash: {HashString(userPrompt)}");
        
        // Step 1: Interpret intent using LLM (or heuristic fallback)
        var interpretation = await _llmAdapter.Interpret(userPrompt);
        ArchLogger.LogInfo($"[Planner] Interpreted intent: {interpretation.Intent}, confidence: {interpretation.Confidence}, fallback: {interpretation.IsHeuristicFallback}");
        
        // Step 2: Check if intent is supported
        var supportedIntents = new[] { "TESTSITE_EXPORT", "TESTSITE_MONITOR", "LOGIN_FLOW", "FILE_DOWNLOAD" };
        if (!supportedIntents.Contains(interpretation.Intent))
        {
            return new PlanResult
            {
                Success = false,
                Intent = interpretation.Intent,
                Confidence = interpretation.Confidence,
                Error = $"Intent '{interpretation.Intent}' is not yet supported. Supported: {string.Join(", ", supportedIntents)}",
                ClarificationQuestions = interpretation.ClarificationQuestions ?? new List<string>()
            };
        }
        
        // Convert slots to Dictionary<string, object>
        var slots = interpretation.Slots?.ToDictionary(
            kvp => kvp.Key,
            kvp => (object)kvp.Value) ?? new Dictionary<string, object>();
        
        // Step 3: Evaluate policy for the domain
        var domain = GetDomainForIntent(interpretation.Intent, slots);
        var policyResult = _policyEngine.Evaluate(new PolicyEvaluationRequest
        {
            Domain = domain,
            ActionKind = GetActionKindForIntent(interpretation.Intent)
        });
        
        ArchLogger.LogInfo($"[Planner] Policy for domain '{domain}': {policyResult.Decision}");
        
        if (policyResult.Decision == PolicyDecision.DENY)
        {
            return new PlanResult
            {
                Success = false,
                Intent = interpretation.Intent,
                Confidence = interpretation.Confidence,
                Error = $"Policy denied: {policyResult.Reason}"
            };
        }
        
        // Step 4a: Check Procedure Memory cache (Phase 21)
        var cachedProc = _procedureStore?.FindBest(interpretation.Intent, userPrompt);
        if (cachedProc != null)
        {
            ArchLogger.LogInfo(
                $"[Planner] Cache HIT for intent={interpretation.Intent} " +
                $"procedureId={cachedProc.Id} successRate={cachedProc.SuccessRate:P0}");

            // Update last-used timestamp
            cachedProc.LastUsedAt = DateTime.UtcNow;
            _procedureStore!.Save(cachedProc);

            // Mark plan as coming from cache
            cachedProc.Plan.ProcedureId       = cachedProc.Id;
            cachedProc.Plan.FromProcedureCache = true;

            return new PlanResult
            {
                Success          = true,
                Intent           = interpretation.Intent,
                Confidence       = interpretation.Confidence,
                Plan             = cachedProc.Plan,
                PolicyDecision   = policyResult.Decision,
                RequiresApproval = policyResult.Decision == PolicyDecision.REQUIRE_APPROVAL,
                RequiresSecret   = policyResult.Decision == PolicyDecision.REQUIRE_SECRET,
                RequiresCaptcha  = policyResult.Decision == PolicyDecision.REQUIRE_CAPTCHA,
                ProcedureId      = cachedProc.Id,
                FromProcedureCache = true
            };
        }

        // Step 4b: Build deterministic plan + save to Procedure Memory
        var plan = BuildPlanForIntent(interpretation.Intent, slots, policyResult);

        if (_procedureStore != null)
        {
            var newRecord = new ProcedureRecord
            {
                Intent        = interpretation.Intent,
                PromptExample = userPrompt,
                Keywords      = ProcedureStore.ExtractKeywords(userPrompt),
                Plan          = plan
            };
            var procId = _procedureStore.Save(newRecord);
            plan.ProcedureId       = procId;
            plan.FromProcedureCache = false;
            ArchLogger.LogInfo(
                $"[Planner] Saved new procedure {procId} for intent={interpretation.Intent}");
        }

        return new PlanResult
        {
            Success          = true,
            Intent           = interpretation.Intent,
            Confidence       = interpretation.Confidence,
            Plan             = plan,
            PolicyDecision   = policyResult.Decision,
            RequiresApproval = policyResult.Decision == PolicyDecision.REQUIRE_APPROVAL,
            RequiresSecret   = policyResult.Decision == PolicyDecision.REQUIRE_SECRET,
            RequiresCaptcha  = policyResult.Decision == PolicyDecision.REQUIRE_CAPTCHA,
            ProcedureId      = plan.ProcedureId,
            FromProcedureCache = false
        };
    }
    
    private TaskPlan BuildPlanForIntent(string intent, Dictionary<string, object>? slots, PolicyEvaluationResult policy)
    {
        slots ??= new Dictionary<string, object>();
        
        var plan = new TaskPlan
        {
            Version = 1,
            Intent = intent,
            Steps = new List<PlanStep>()
        };
        
        switch (intent)
        {
            case "TESTSITE_EXPORT":
                plan.Steps = BuildTestsiteExportSteps(slots, policy);
                break;
                
            case "TESTSITE_MONITOR":
                plan.Steps = BuildTestsiteMonitorSteps(slots, policy);
                break;
                
            case "LOGIN_FLOW":
                plan.Steps = BuildLoginFlowSteps(slots, policy);
                break;
                
            case "FILE_DOWNLOAD":
                plan.Steps = BuildFileDownloadSteps(slots, policy);
                break;
        }
        
        // Reindex steps
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            plan.Steps[i].Index = i + 1;
        }
        
        plan.ComputeHash();
        return plan;
    }
    
    private List<PlanStep> BuildTestsiteExportSteps(Dictionary<string, object> slots, PolicyEvaluationResult policy)
    {
        var steps = new List<PlanStep>();
        var baseUrl = ResolveTestsiteUrl(slots);
        
        // If approval required, add approval step at the beginning
        if (policy.Decision == PolicyDecision.REQUIRE_APPROVAL)
        {
            steps.Add(new PlanStep
            {
                Action = "approval.requestConfirmation",
                Params = new Dictionary<string, object> { ["message"] = "Proceed with testsite export?" },
                Description = "Request user approval before export"
            });
        }
        
        // HTTP-based testsite workflow (no browser required)
        steps.Add(new PlanStep
        {
            Action = "http.login",
            Params = new Dictionary<string, object> 
            { 
                ["url"] = $"{baseUrl}/testsite/api/login",
                ["username"] = "test",
                ["password"] = "test"
            },
            Description = "Login to testsite API"
        });
        
        steps.Add(new PlanStep
        {
            Action = "http.fetchData",
            Params = new Dictionary<string, object> { ["url"] = $"{baseUrl}/testsite/api/data" },
            Description = "Fetch table data from API"
        });
        
        steps.Add(new PlanStep
        {
            Action = "http.downloadCsv",
            Params = new Dictionary<string, object> { ["url"] = $"{baseUrl}/testsite/api/csv" },
            Description = "Download CSV export"
        });
        
        return steps;
    }
    
    private List<PlanStep> BuildTestsiteMonitorSteps(Dictionary<string, object> slots, PolicyEvaluationResult policy)
    {
        var steps = new List<PlanStep>();
        var baseUrl = ResolveTestsiteUrl(slots);
        var interval = slots.ContainsKey("intervalMs") ? Convert.ToInt32(slots["intervalMs"]) : 30000;
        
        // Request approval if needed
        if (policy.Decision == PolicyDecision.REQUIRE_APPROVAL)
        {
            steps.Add(new PlanStep
            {
                Action = "approval.requestConfirmation",
                Params = new Dictionary<string, object> { ["message"] = $"Monitor testsite every {interval / 1000}s?" },
                Description = "Request user approval for monitoring"
            });
        }
        
        // Navigate to dashboard
        steps.Add(new PlanStep
        {
            Action = "browser.openUrl",
            Params = new Dictionary<string, object> { ["url"] = $"{baseUrl}/testsite/dashboard" },
            Description = "Navigate to testsite dashboard"
        });
        
        // Wait for table
        steps.Add(new PlanStep
        {
            Action = "browser.waitFor",
            Params = new Dictionary<string, object> { ["selector"] = "#dataTable", ["timeoutMs"] = 5000 },
            Description = "Wait for data table"
        });
        
        // Extract and compare
        steps.Add(new PlanStep
        {
            Action = "browser.extractTable",
            Params = new Dictionary<string, object> { ["selector"] = "#dataTable" },
            Description = "Extract current table state"
        });
        
        // Take screenshot
        steps.Add(new PlanStep
        {
            Action = "browser.screenshotSelector",
            Params = new Dictionary<string, object> { ["selector"] = "#dataTable" },
            Description = "Screenshot of data table"
        });
        
        // Schedule next check (monitoring mode)
        steps.Add(new PlanStep
        {
            Action = "scheduler.reschedule",
            Params = new Dictionary<string, object> { ["intervalMs"] = interval, ["intent"] = "TESTSITE_MONITOR" },
            Description = $"Schedule next check in {interval / 1000}s"
        });
        
        return steps;
    }
    
    private List<PlanStep> BuildLoginFlowSteps(Dictionary<string, object> slots, PolicyEvaluationResult policy)
    {
        var steps = new List<PlanStep>();
        var url = slots.ContainsKey("url") ? slots["url"]?.ToString() ?? "" : "";
        
        // Always require secret for login
        steps.Add(new PlanStep
        {
            Action = "approval.requestSecret",
            Params = new Dictionary<string, object> { ["prompt"] = $"Enter credentials for {url}" },
            Description = "Request login credentials"
        });
        
        if (!string.IsNullOrEmpty(url))
        {
            steps.Add(new PlanStep
            {
                Action = "browser.openUrl",
                Params = new Dictionary<string, object> { ["url"] = url },
                Description = "Navigate to login page"
            });
        }
        
        steps.Add(new PlanStep
        {
            Action = "browser.detectLoginForm",
            Params = new Dictionary<string, object>(),
            Description = "Detect and fill login form"
        });
        
        return steps;
    }
    
    private List<PlanStep> BuildFileDownloadSteps(Dictionary<string, object> slots, PolicyEvaluationResult policy)
    {
        var steps = new List<PlanStep>();
        var url = slots.ContainsKey("url") ? slots["url"]?.ToString() ?? "" : "";
        var filename = slots.ContainsKey("filename") ? slots["filename"]?.ToString() ?? "download" : "download";
        
        if (policy.Decision == PolicyDecision.REQUIRE_APPROVAL)
        {
            steps.Add(new PlanStep
            {
                Action = "approval.requestConfirmation",
                Params = new Dictionary<string, object> { ["message"] = $"Download file from {url}?" },
                Description = "Request approval for download"
            });
        }
        
        if (!string.IsNullOrEmpty(url))
        {
            steps.Add(new PlanStep
            {
                Action = "browser.openUrl",
                Params = new Dictionary<string, object> { ["url"] = url },
                Description = "Navigate to download page"
            });
        }
        
        steps.Add(new PlanStep
        {
            Action = "browser.downloadFile",
            Params = new Dictionary<string, object> { ["filename"] = filename },
            Description = "Download file"
        });
        
        return steps;
    }
    
    // Placeholder domains that the LLM hallucinates when no real URL is in the prompt
    private static readonly HashSet<string> _placeholderHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "example.com", "testsitetest.com", "localhost", "test.com", "sample.com", "placeholder.com"
    };

    private static string ResolveTestsiteUrl(Dictionary<string, object> slots)
    {
        if (slots.TryGetValue("url", out var raw) && raw is string urlStr && !string.IsNullOrWhiteSpace(urlStr))
        {
            try
            {
                var uri = new Uri(urlStr);
                if (!_placeholderHosts.Contains(uri.Host))
                    return urlStr.TrimEnd('/');
            }
            catch { }
        }
        return "http://localhost:5052";
    }

    private string GetDomainForIntent(string intent, Dictionary<string, object>? slots)
    {
        // Extract domain from slots if available
        if (slots != null && slots.ContainsKey("url"))
        {
            try
            {
                var uri = new Uri(slots["url"]?.ToString() ?? "");
                return uri.Host + (uri.Port != 80 && uri.Port != 443 ? $":{uri.Port}" : "");
            }
            catch { }
        }
        
        // Default domains for known intents
        return intent switch
        {
            "TESTSITE_EXPORT" => "localhost:5052",
            "TESTSITE_MONITOR" => "localhost:5052",
            _ => "unknown"
        };
    }
    
    private string GetActionKindForIntent(string intent)
    {
        return intent switch
        {
            "TESTSITE_EXPORT" => "READ_ONLY",
            "TESTSITE_MONITOR" => "READ_ONLY",
            "FILE_DOWNLOAD" => "READ_ONLY",
            "LOGIN_FLOW" => "IDENTITY",
            _ => "READ_ONLY"
        };
    }
    
    private static string HashString(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).Substring(0, 8).ToLower();
    }
}

public class PlanResult
{
    public bool    Success     { get; set; }
    public string  Intent      { get; set; } = "";
    public double  Confidence  { get; set; }
    public string? Error       { get; set; }
    public TaskPlan? Plan      { get; set; }
    public PolicyDecision PolicyDecision { get; set; }
    public bool RequiresApproval { get; set; }
    public bool RequiresSecret   { get; set; }
    public bool RequiresCaptcha  { get; set; }
    public List<string> ClarificationQuestions { get; set; } = new();

    // Phase 21: Procedure Memory
    public string? ProcedureId        { get; set; }
    public bool    FromProcedureCache  { get; set; }
}

public class PlannerRequest
{
    public string? TaskId { get; set; }
    public string UserPrompt { get; set; } = "";
}
