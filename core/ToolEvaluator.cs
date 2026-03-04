using System.Text.Json;
using System.Text.RegularExpressions;

namespace Archimedes.Core;

/// <summary>
/// Phase 27 – Tool Evaluator.
/// Assesses risk level of a tool candidate before installation.
/// Uses LLM for contextual evaluation + heuristic patterns for fast assessment.
/// </summary>
public class ToolEvaluator
{
    private readonly LLMAdapter _llm;
    private readonly HttpClient _http;

    public ToolEvaluator(LLMAdapter llm, HttpClient http)
    {
        _llm  = llm;
        _http = http;
    }

    // ── Main evaluation ────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates a candidate and updates its Risk field in place.
    /// Returns the updated candidate.
    /// </summary>
    public async Task<ToolCandidate> EvaluateAsync(
        ToolCandidate candidate, CancellationToken ct = default)
    {
        // Fast heuristic pass first
        var heuristic = HeuristicRisk(candidate);
        if (heuristic == ToolRiskLevel.DANGEROUS)
        {
            candidate.Risk       = ToolRiskLevel.DANGEROUS;
            candidate.RiskReason = "Heuristic: known dangerous pattern detected";
            return candidate;
        }

        // LLM detailed evaluation
        try
        {
            var prompt = BuildEvalPrompt(candidate);
            var raw    = await _llm.AskAsync(
                "You are a software security analyst. Evaluate tools for risk. Output JSON only.",
                prompt, 384);

            var json = ExtractJson(raw);
            if (json != null)
            {
                var resp = JsonSerializer.Deserialize<RiskEvalResponse>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (resp != null)
                {
                    candidate.Risk       = ParseRisk(resp.Risk);
                    candidate.RiskReason = resp.Reason;
                    return candidate;
                }
            }
        }
        catch { /* fall through to heuristic result */ }

        candidate.Risk       = heuristic;
        candidate.RiskReason = "Heuristic evaluation (LLM unavailable)";
        return candidate;
    }

    // ── Heuristic risk assessment ──────────────────────────────────────────

    private static ToolRiskLevel HeuristicRisk(ToolCandidate c)
    {
        var text = $"{c.Name} {c.Description} {c.SourceUrl}".ToLowerInvariant();

        // Dangerous patterns
        var dangerousPatterns = new[]
        {
            "rootkit", "keylogger", "ransomware", "malware", "trojan",
            "exploit", "payload", "shell injection", "rm -rf", "format c:",
            "delete system", "wipe disk", "kernel module", "driver install"
        };
        if (dangerousPatterns.Any(p => text.Contains(p)))
            return ToolRiskLevel.DANGEROUS;

        // Dark web source = elevated risk by default
        if (c.SourceType == ToolSourceType.DARK)
            return ToolRiskLevel.MANAGEABLE;

        // Unofficial/bypass patterns = manageable
        var manageablePatterns = new[]
        {
            "unofficial", "reverse engineer", "undocumented",
            "no official api", "workaround", "hack", "bypass rate"
        };
        if (manageablePatterns.Any(p => text.Contains(p)))
            return ToolRiskLevel.MANAGEABLE;

        // Official/well-known sources = safe
        if (c.Strategy == ToolExecutionStrategy.NUGET_PACKAGE &&
            c.SourceDomain == "nuget.org")
            return ToolRiskLevel.SAFE;

        if (c.Strategy == ToolExecutionStrategy.HTTP_API &&
            (c.SourceUrl.Contains("googleapis.com") ||
             c.SourceUrl.Contains("microsoft.com") ||
             c.SourceUrl.Contains("twilio.com")))
            return ToolRiskLevel.SAFE;

        return ToolRiskLevel.MANAGEABLE; // Default: manageable, not outright safe
    }

    // ── LLM prompt ─────────────────────────────────────────────────────────

    private static string BuildEvalPrompt(ToolCandidate c)
        => $@"Evaluate this software integration for safety risk:

Name: {c.Name}
Description: {c.Description}
Source: {c.SourceUrl}
Strategy: {c.Strategy}
Source type: {c.SourceType}

Return JSON:
{{
  ""risk"": ""SAFE"" or ""MANAGEABLE"" or ""DANGEROUS"",
  ""reason"": ""brief explanation""
}}

SAFE = well-maintained, official, no known risks
MANAGEABLE = some risks Archimedes can handle autonomously (retry, fallback, rate limit)
DANGEROUS = could damage the system, expose credentials, or cause irreversible damage";

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ToolRiskLevel ParseRisk(string? s) => s?.ToUpperInvariant() switch
    {
        "SAFE"       => ToolRiskLevel.SAFE,
        "MANAGEABLE" => ToolRiskLevel.MANAGEABLE,
        "DANGEROUS"  => ToolRiskLevel.DANGEROUS,
        _            => ToolRiskLevel.MANAGEABLE
    };

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end   = text.LastIndexOf('}');
        if (start >= 0 && end > start) return text[start..(end + 1)];
        return null;
    }

    private class RiskEvalResponse
    {
        public string Risk   { get; set; } = "MANAGEABLE";
        public string Reason { get; set; } = "";
    }
}
