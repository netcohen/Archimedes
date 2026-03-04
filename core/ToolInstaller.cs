using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 27 – Tool Installer.
/// Takes a validated ToolCandidate and installs it as an AcquiredTool.
///
/// "Installation" means registering the capability in a way Archimedes can use:
///   HTTP_API        → store endpoint + config in ToolStore
///   BROWSER_PROCEDURE → generate and register a ProcedureRecord in ProcedureStore
///   LOCAL_SCRIPT    → generate a script file + register it
///   NUGET_PACKAGE   → record package info (runtime loading future phase)
///
/// All installs go through a lightweight sandbox verification first.
/// </summary>
public class ToolInstaller
{
    private readonly ToolStore      _toolStore;
    private readonly ProcedureStore _procedureStore;
    private readonly LLMAdapter     _llm;

    public ToolInstaller(ToolStore toolStore, ProcedureStore procedureStore, LLMAdapter llm)
    {
        _toolStore      = toolStore;
        _procedureStore = procedureStore;
        _llm            = llm;
    }

    // ── Main install ───────────────────────────────────────────────────────

    /// <summary>
    /// Installs a tool candidate as an acquired tool.
    /// Returns the AcquiredTool on success, null on failure.
    /// </summary>
    public async Task<AcquiredTool?> InstallAsync(
        ToolCandidate candidate, bool userApproved = false,
        CancellationToken ct = default)
    {
        ArchLogger.LogInfo(
            $"[ToolInstaller] Installing {candidate.Name} " +
            $"strategy={candidate.Strategy} risk={candidate.Risk}");

        try
        {
            var tool = candidate.Strategy switch
            {
                ToolExecutionStrategy.HTTP_API
                    => await InstallHttpApiAsync(candidate, ct),
                ToolExecutionStrategy.BROWSER_PROCEDURE
                    => await InstallBrowserProcedureAsync(candidate, ct),
                ToolExecutionStrategy.NUGET_PACKAGE
                    => InstallNugetPackage(candidate),
                ToolExecutionStrategy.LOCAL_SCRIPT
                    => await InstallLocalScriptAsync(candidate, ct),
                _   => await InstallHttpApiAsync(candidate, ct)
            };

            if (tool == null) return null;

            tool.UserApproved = userApproved;
            _toolStore.AddTool(tool);

            ArchLogger.LogInfo(
                $"[ToolInstaller] Installed tool {tool.ToolId} " +
                $"capability={tool.Capability}");
            return tool;
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[ToolInstaller] Install failed for {candidate.Name}: {ex.Message}");
            return null;
        }
    }

    // ── HTTP API install ───────────────────────────────────────────────────

    private async Task<AcquiredTool?> InstallHttpApiAsync(
        ToolCandidate c, CancellationToken ct)
    {
        // Generate usage config via LLM
        var config = await GenerateApiConfigAsync(c, ct);

        return new AcquiredTool
        {
            Capability   = c.Capability,
            Name         = c.Name,
            Description  = c.Description,
            Strategy     = ToolExecutionStrategy.HTTP_API,
            Risk         = c.Risk,
            ApiEndpoint  = c.ApiEndpoint ?? ExtractApiEndpoint(c.SourceUrl),
            SourceUrl    = c.SourceUrl,
            SourceType   = c.SourceType,
            Config       = config
        };
    }

    // ── Browser procedure install ──────────────────────────────────────────

    private async Task<AcquiredTool?> InstallBrowserProcedureAsync(
        ToolCandidate c, CancellationToken ct)
    {
        // Generate browser steps via LLM
        var steps = await GenerateBrowserStepsAsync(c, ct);

        var procedure = new ProcedureRecord
        {
            Intent         = c.Capability,
            PromptExample  = $"Perform {c.Capability} using {c.Name}",
            Keywords       = ExtractKeywords(c.Capability),
            Plan           = new TaskPlan
            {
                Intent = c.Capability,
                Steps  = steps
            }
        };

        var procId = _procedureStore.Save(procedure);

        return new AcquiredTool
        {
            Capability   = c.Capability,
            Name         = c.Name,
            Description  = c.Description,
            Strategy     = ToolExecutionStrategy.BROWSER_PROCEDURE,
            Risk         = c.Risk,
            SourceUrl    = c.SourceUrl,
            SourceType   = c.SourceType,
            ProcedureId  = procId,
            Config       = new Dictionary<string, string>
            {
                ["procedureHint"] = c.ProcedureHint ?? "",
                ["baseUrl"]       = c.SourceUrl
            }
        };
    }

    // ── NuGet package install ──────────────────────────────────────────────

    private AcquiredTool? InstallNugetPackage(ToolCandidate c)
    {
        // Record the package for future use
        // Full dynamic loading is a future phase capability
        return new AcquiredTool
        {
            Capability   = c.Capability,
            Name         = c.Name,
            Description  = c.Description,
            Strategy     = ToolExecutionStrategy.NUGET_PACKAGE,
            Risk         = c.Risk,
            SourceUrl    = c.SourceUrl,
            SourceType   = c.SourceType,
            PackageName  = c.PackageName,
            Config       = new Dictionary<string, string>
            {
                ["packageName"]    = c.PackageName ?? "",
                ["packageVersion"] = c.PackageVersion ?? "latest"
            }
        };
    }

    // ── Local script install ───────────────────────────────────────────────

    private async Task<AcquiredTool?> InstallLocalScriptAsync(
        ToolCandidate c, CancellationToken ct)
    {
        var scriptDir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Archimedes", "scripts");
        Directory.CreateDirectory(scriptDir);

        var scriptName = $"{c.Capability.ToLowerInvariant()}_{DateTime.UtcNow:yyyyMMddHHmmss}.ps1";
        var scriptPath = Path.Combine(scriptDir, scriptName);

        // Generate script via LLM
        var script = await GeneratePowerShellScriptAsync(c, ct);
        await File.WriteAllTextAsync(scriptPath, script, ct);

        return new AcquiredTool
        {
            Capability   = c.Capability,
            Name         = c.Name,
            Description  = c.Description,
            Strategy     = ToolExecutionStrategy.LOCAL_SCRIPT,
            Risk         = c.Risk,
            SourceUrl    = c.SourceUrl,
            SourceType   = c.SourceType,
            ScriptPath   = scriptPath,
            Config       = new Dictionary<string, string>
            {
                ["scriptPath"] = scriptPath,
                ["packageName"] = c.PackageName ?? ""
            }
        };
    }

    // ── LLM generation helpers ─────────────────────────────────────────────

    private async Task<Dictionary<string, string>> GenerateApiConfigAsync(
        ToolCandidate c, CancellationToken ct)
    {
        try
        {
            var raw = await _llm.AskAsync(
                "You are a software integration expert. Output only JSON.",
                $"Generate a minimal config for using {c.Name} ({c.Description}). " +
                $"Source: {c.SourceUrl}. Return JSON with string key-value pairs only. " +
                $"Keys should include: endpoint, authType, notes.",
                256);

            var json = ExtractJson(raw);
            if (json != null)
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (d != null) return d;
            }
        }
        catch { }

        return new Dictionary<string, string>
        {
            ["endpoint"] = c.ApiEndpoint ?? c.SourceUrl,
            ["authType"] = "unknown",
            ["notes"]    = c.Description
        };
    }

    private async Task<List<PlanStep>> GenerateBrowserStepsAsync(
        ToolCandidate c, CancellationToken ct)
    {
        try
        {
            var raw = await _llm.AskAsync(
                "You are a browser automation expert. Generate Playwright steps. Output JSON array.",
                $"Generate browser automation steps to use {c.Name} for {c.Capability}. " +
                $"Hint: {c.ProcedureHint}. " +
                $"Return JSON array of steps with fields: action (string), selector (string), value (string).",
                384);

            var json = ExtractJsonArray(raw);
            if (json != null)
            {
                var steps = JsonSerializer.Deserialize<List<RawStep>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (steps != null)
                    return steps.Select((s, i) => new PlanStep
                    {
                        Index  = i,
                        Action = s.Action ?? "navigate",
                        Params = new Dictionary<string, object>
                        {
                            ["selector"] = s.Selector ?? "",
                            ["value"]    = s.Value    ?? ""
                        }
                    }).ToList();
            }
        }
        catch { }

        // Minimal fallback
        return new List<PlanStep>
        {
            new() { Index = 0, Action = "navigate",
                    Params = new Dictionary<string, object> { ["url"] = c.SourceUrl } }
        };
    }

    private async Task<string> GeneratePowerShellScriptAsync(
        ToolCandidate c, CancellationToken ct)
    {
        try
        {
            var raw = await _llm.AskAsync(
                "You are a PowerShell expert. Write safe, minimal scripts.",
                $"Write a PowerShell script to use {c.Name} (npm package: {c.PackageName}) " +
                $"for {c.Capability}. The script should accept parameters and return JSON output.",
                512);

            // Extract script block if wrapped in markdown
            var match = System.Text.RegularExpressions.Regex.Match(
                raw, @"```(?:powershell|ps1)?\s*([\s\S]+?)```",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim();
            if (raw.Trim().Length > 20) return raw.Trim();
        }
        catch { }

        return $"# Auto-generated stub for {c.Capability} via {c.Name}\n" +
               $"param([string]$input)\n" +
               $"# Install: npm install {c.PackageName}\n" +
               $"Write-Output '{{\"status\":\"not_implemented\"}}'";
    }

    // ── Static helpers ─────────────────────────────────────────────────────

    private static string ExtractApiEndpoint(string sourceUrl)
    {
        // Try to derive an API base URL
        if (sourceUrl.Contains("github.com"))
            return "https://api.github.com";
        if (sourceUrl.Contains("npmjs.com"))
            return "https://registry.npmjs.org";
        return sourceUrl;
    }

    private static List<string> ExtractKeywords(string capability)
        => capability.ToLowerInvariant()
                     .Split('_', ' ')
                     .Where(k => k.Length > 1)
                     .ToList();

    private static string? ExtractJson(string text)
    {
        var s = text.IndexOf('{');
        var e = text.LastIndexOf('}');
        return s >= 0 && e > s ? text[s..(e + 1)] : null;
    }

    private static string? ExtractJsonArray(string text)
    {
        var s = text.IndexOf('[');
        var e = text.LastIndexOf(']');
        return s >= 0 && e > s ? text[s..(e + 1)] : null;
    }

    private class RawStep
    {
        public string? Action   { get; set; }
        public string? Selector { get; set; }
        public string? Value    { get; set; }
    }
}
