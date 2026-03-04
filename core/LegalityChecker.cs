using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 27 – Legality Checker (Iron Rule).
///
/// Archimedes is bound by Israeli law and international law.
/// When a planned action MAY violate any law, execution stops and the user
/// is asked to decide.  Only LEGAL actions proceed automatically.
///
/// No hardcoded ILLEGAL block – the user decides case-by-case.
/// This branch is only reached when no legal path exists.
/// </summary>
public class LegalityChecker
{
    private readonly LLMAdapter _llm;
    private readonly ToolStore  _store;

    // Past user decisions – learn from them to suggest patterns
    // key = normalized description → decision
    private readonly Dictionary<string, ApprovalDecision> _decisionCache = new();

    public LegalityChecker(LLMAdapter llm, ToolStore store)
    {
        _llm   = llm;
        _store = store;
        LoadDecisionCache();
    }

    // ── Main evaluation ────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates whether using this tool candidate is legally acceptable.
    /// Returns LEGAL if safe, otherwise NEEDS_APPROVAL with full explanation.
    /// </summary>
    public async Task<LegalCheckResult> EvaluateAsync(
        ToolCandidate candidate, string context)
    {
        var prompt = BuildLegalPrompt(candidate, context);

        LegalEvalLLMResponse? llmResp = null;
        try
        {
            var raw = await _llm.AskAsync(
                "You are a legal compliance assistant specialising in Israeli law, " +
                "EU GDPR, US CFAA, and international software law. " +
                "Output only valid JSON.",
                prompt, 512);

            var json = ExtractJson(raw);
            if (json != null)
                llmResp = JsonSerializer.Deserialize<LegalEvalLLMResponse>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { /* use heuristic */ }

        if (llmResp == null)
            llmResp = HeuristicEval(candidate, context);

        if (llmResp.IsLegal)
        {
            return new LegalCheckResult { Status = LegalStatus.LEGAL };
        }

        // Build a rich user message
        var userMsg = BuildUserMessage(candidate, context, llmResp);
        candidate.Legal      = LegalStatus.NEEDS_APPROVAL;
        candidate.LegalIssue = llmResp.Issue;
        candidate.LegalBasis = llmResp.LegalBasis;

        return new LegalCheckResult
        {
            Status      = LegalStatus.NEEDS_APPROVAL,
            Issue       = llmResp.Issue,
            LegalBasis  = llmResp.LegalBasis,
            UserMessage = userMsg
        };
    }

    // ── Create approval request ────────────────────────────────────────────

    public LegalApprovalRequest CreateApprovalRequest(
        string gapId, string capability,
        LegalCheckResult legalResult,
        ToolCandidate    candidate)
    {
        var req = new LegalApprovalRequest
        {
            GapId       = gapId,
            Capability  = capability,
            UserMessage = legalResult.UserMessage ?? "",
            LegalIssue  = legalResult.Issue ?? "",
            LegalBasis  = legalResult.LegalBasis,
            AlternativeSuggestion = candidate.ProcedureHint
        };

        _store.AddApproval(req);
        return req;
    }

    // ── Decision processing ────────────────────────────────────────────────

    public void RecordDecision(string approvalId, ApprovalDecision decision, string? userNote)
    {
        var req = _store.GetApproval(approvalId);
        if (req == null) return;

        req.Decision   = decision;
        req.UserNote   = userNote;
        req.DecidedAt  = DateTime.UtcNow;
        _store.UpdateApproval(req);

        // Cache decision for future pattern suggestions
        var key = NormalizeKey(req.LegalIssue);
        _decisionCache[key] = decision;
    }

    /// <summary>
    /// Returns a previous decision for a similar legal issue, if one exists.
    /// </summary>
    public ApprovalDecision? FindSimilarDecision(string legalIssue)
    {
        var key = NormalizeKey(legalIssue);
        if (_decisionCache.TryGetValue(key, out var d)) return d;

        // Fuzzy match: check if any cached key is contained in the new issue
        foreach (var (k, v) in _decisionCache)
            if (legalIssue.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                k.Contains(legalIssue, StringComparison.OrdinalIgnoreCase))
                return v;

        return null;
    }

    // ── LLM prompt ─────────────────────────────────────────────────────────

    private static string BuildLegalPrompt(ToolCandidate c, string context)
        => $@"Evaluate this software integration for legal compliance:

Name: {c.Name}
Description: {c.Description}
Source URL: {c.SourceUrl}
Usage context: {context}
Execution strategy: {c.Strategy}

Check against:
1. Israeli Computer Law (חוק המחשבים 1995)
2. Israeli Privacy Protection Law (חוק הגנת הפרטיות)
3. EU GDPR (if EU data involved)
4. US CFAA / DMCA (if US services involved)
5. Relevant Terms of Service with legal standing

Return JSON:
{{
  ""isLegal"": true/false,
  ""issue"": ""short description of legal concern or empty string"",
  ""legalBasis"": ""specific law/section or empty string"",
  ""severity"": ""none/minor/moderate/serious""
}}";

    private static string BuildUserMessage(
        ToolCandidate c, string context, LegalEvalLLMResponse llmResp)
        => $"חיפשתי אינטגרציה עבור: {context}\n\n" +
           $"הפתרון הטוב ביותר שמצאתי: {c.Name} ({c.SourceUrl})\n\n" +
           $"הבעיה המשפטית שזוהתה:\n{llmResp.Issue}\n\n" +
           $"בסיס חוקי: {llmResp.LegalBasis}\n\n" +
           "לא מצאתי פתרון חוקי חלופי. האם לבצע?";

    // ── Heuristic fallback ─────────────────────────────────────────────────

    private static LegalEvalLLMResponse HeuristicEval(ToolCandidate c, string context)
    {
        var text = $"{c.Name} {c.Description} {c.SourceUrl}".ToLowerInvariant();

        // Known risky patterns
        if (text.Contains("unofficial") || text.Contains("bypass") ||
            text.Contains("crack") || text.Contains("scrape"))
        {
            return new LegalEvalLLMResponse
            {
                IsLegal    = false,
                Issue      = "Integration uses unofficial/bypass methods that may violate ToS",
                LegalBasis = "Terms of Service / Israeli Computer Law",
                Severity   = "moderate"
            };
        }

        return new LegalEvalLLMResponse { IsLegal = true };
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end   = text.LastIndexOf('}');
        if (start >= 0 && end > start) return text[start..(end + 1)];
        return null;
    }

    private static string NormalizeKey(string issue)
        => issue.ToLowerInvariant().Trim()[..Math.Min(80, issue.Length)];

    private void LoadDecisionCache()
    {
        foreach (var req in _store.GetAllApprovals())
        {
            if (req.Decision is ApprovalDecision.APPROVED or ApprovalDecision.REJECTED)
            {
                var key = NormalizeKey(req.LegalIssue);
                _decisionCache[key] = req.Decision;
            }
        }
    }

    // ── Internal LLM response model ────────────────────────────────────────

    private class LegalEvalLLMResponse
    {
        public bool   IsLegal    { get; set; } = true;
        public string Issue      { get; set; } = "";
        public string LegalBasis { get; set; } = "";
        public string Severity   { get; set; } = "none";
    }
}
