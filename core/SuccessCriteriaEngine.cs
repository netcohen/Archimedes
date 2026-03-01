using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 20 – Success Criteria Engine.
///
/// Moves Archimedes from binary success/failure (HTTP 200 = win) to
/// evidence-based outcome verification. After each step execution,
/// the engine inspects the actual result against per-action criteria
/// and returns a typed OutcomeResult with supporting evidence.
/// </summary>

// ── Outcome types ─────────────────────────────────────────────────────────────

public enum OutcomeResult
{
    VERIFIED,        // Success confirmed by concrete evidence
    UNVERIFIED,      // Ran successfully but evidence inconclusive
    PARTIAL,         // Some criteria met, but not all
    FAILED_VERIFY,   // Step ran but outcome verification failed
    NOT_APPLICABLE   // No criteria defined for this action
}

// ── Verification result ───────────────────────────────────────────────────────

public class VerificationResult
{
    public OutcomeResult Outcome          { get; set; } = OutcomeResult.NOT_APPLICABLE;
    public string?       Evidence         { get; set; }
    public string?       ExpectedCriteria { get; set; }
    public string?       FailureReason    { get; set; }
}

// ── Engine ────────────────────────────────────────────────────────────────────

public class SuccessCriteriaEngine
{
    /// <summary>
    /// Verify the outcome of a completed step.
    /// Called AFTER ExecuteStep() regardless of success/failure.
    /// </summary>
    public VerificationResult Verify(PlanStep step, StepExecutionResult result)
    {
        // If step itself failed, mark as FAILED_VERIFY with the step error
        if (!result.Success)
        {
            return new VerificationResult
            {
                Outcome       = OutcomeResult.FAILED_VERIFY,
                FailureReason = result.Error ?? "Step returned failure"
            };
        }

        return step.Action switch
        {
            "http.login"       => VerifyLogin(result),
            "http.fetchData"   => VerifyFetchData(result),
            "http.downloadCsv" => VerifyDownloadCsv(result),
            "http.get"         => VerifyHttpGet(result),
            "http.post"        => VerifyHttpPost(result),

            // Browser steps: trust Net's result
            var a when a.StartsWith("browser.") => VerifyBrowserStep(step, result),

            // Approval steps: if we reach here, approval was granted
            var a when a.StartsWith("approval.") => new VerificationResult
            {
                Outcome  = OutcomeResult.VERIFIED,
                Evidence = "Approval granted by user",
                ExpectedCriteria = "User approval received"
            },

            // Scheduler: fire-and-forget
            "scheduler.reschedule" => new VerificationResult
            {
                Outcome  = OutcomeResult.VERIFIED,
                Evidence = "Reschedule enqueued"
            },

            _ => new VerificationResult { Outcome = OutcomeResult.NOT_APPLICABLE }
        };
    }

    // ── Per-action verifiers ─────────────────────────────────────────────────

    private VerificationResult VerifyLogin(StepExecutionResult result)
    {
        const string criteria = "Response must contain token or success=true";

        if (string.IsNullOrWhiteSpace(result.Data))
            return new VerificationResult
            {
                Outcome          = OutcomeResult.UNVERIFIED,
                ExpectedCriteria = criteria,
                FailureReason    = "Empty login response body"
            };

        try
        {
            using var doc  = JsonDocument.Parse(result.Data);
            var root = doc.RootElement;

            bool hasToken   = root.TryGetProperty("token", out _)
                           || root.TryGetProperty("access_token", out _)
                           || root.TryGetProperty("authToken", out _);

            bool hasSuccess = root.TryGetProperty("success", out var sp)
                           && sp.ValueKind == JsonValueKind.True;

            if (hasToken)
                return new VerificationResult
                {
                    Outcome          = OutcomeResult.VERIFIED,
                    Evidence         = "Login response contains authentication token",
                    ExpectedCriteria = criteria
                };

            if (hasSuccess)
                return new VerificationResult
                {
                    Outcome          = OutcomeResult.VERIFIED,
                    Evidence         = "Login response has success=true",
                    ExpectedCriteria = criteria
                };

            return new VerificationResult
            {
                Outcome          = OutcomeResult.UNVERIFIED,
                Evidence         = "HTTP 200 received but no token/success field found",
                ExpectedCriteria = criteria
            };
        }
        catch
        {
            // Non-JSON response — could still be a valid session cookie login
            return new VerificationResult
            {
                Outcome          = OutcomeResult.UNVERIFIED,
                Evidence         = $"Non-JSON response (length={result.Data.Length}), assuming session-based auth",
                ExpectedCriteria = criteria
            };
        }
    }

    private VerificationResult VerifyFetchData(StepExecutionResult result)
    {
        const string criteria = "Response must contain non-empty rows/data array";

        if (string.IsNullOrWhiteSpace(result.Data))
            return new VerificationResult
            {
                Outcome          = OutcomeResult.FAILED_VERIFY,
                ExpectedCriteria = criteria,
                FailureReason    = "Empty data response"
            };

        try
        {
            using var doc  = JsonDocument.Parse(result.Data);
            var root = doc.RootElement;

            // Direct array
            if (root.ValueKind == JsonValueKind.Array)
            {
                var count = root.GetArrayLength();
                return count > 0
                    ? new VerificationResult { Outcome = OutcomeResult.VERIFIED,  Evidence = $"Array with {count} items", ExpectedCriteria = criteria }
                    : new VerificationResult { Outcome = OutcomeResult.PARTIAL,   Evidence = "Empty array returned",      ExpectedCriteria = criteria };
            }

            // Object with rows / data property
            foreach (var key in new[] { "rows", "data", "items", "records" })
            {
                if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var count = arr.GetArrayLength();
                    return count > 0
                        ? new VerificationResult { Outcome = OutcomeResult.VERIFIED, Evidence = $"{key}={count} rows",   ExpectedCriteria = criteria }
                        : new VerificationResult { Outcome = OutcomeResult.PARTIAL,  Evidence = $"{key} array is empty", ExpectedCriteria = criteria };
                }
            }

            return new VerificationResult
            {
                Outcome          = OutcomeResult.UNVERIFIED,
                Evidence         = "JSON received but no recognizable data array",
                ExpectedCriteria = criteria
            };
        }
        catch
        {
            return new VerificationResult
            {
                Outcome          = OutcomeResult.UNVERIFIED,
                Evidence         = $"Non-JSON response, length={result.Data.Length}",
                ExpectedCriteria = criteria
            };
        }
    }

    private VerificationResult VerifyDownloadCsv(StepExecutionResult result)
    {
        const string criteria = "Response must be non-empty CSV (commas + multiple lines)";

        if (string.IsNullOrWhiteSpace(result.Data))
            return new VerificationResult
            {
                Outcome          = OutcomeResult.FAILED_VERIFY,
                ExpectedCriteria = criteria,
                FailureReason    = "Empty CSV response"
            };

        bool hasCommas   = result.Data.Contains(',');
        var  lines       = result.Data.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int  lineCount   = lines.Length;
        bool hasHeader   = lineCount > 0 && lines[0].Contains(',');

        if (hasCommas && lineCount > 1 && hasHeader)
            return new VerificationResult
            {
                Outcome          = OutcomeResult.VERIFIED,
                Evidence         = $"CSV with {lineCount} lines (including header)",
                ExpectedCriteria = criteria
            };

        if (result.Data.Length > 0)
            return new VerificationResult
            {
                Outcome          = OutcomeResult.PARTIAL,
                Evidence         = $"Data received but CSV format unclear: {lineCount} lines, hasCommas={hasCommas}",
                ExpectedCriteria = criteria
            };

        return new VerificationResult
        {
            Outcome          = OutcomeResult.FAILED_VERIFY,
            ExpectedCriteria = criteria,
            FailureReason    = "No CSV content returned"
        };
    }

    private VerificationResult VerifyHttpGet(StepExecutionResult result)
    {
        // Generic GET — success=true is sufficient
        return new VerificationResult
        {
            Outcome          = OutcomeResult.VERIFIED,
            Evidence         = "HTTP GET returned success status",
            ExpectedCriteria = "HTTP 2xx response"
        };
    }

    private VerificationResult VerifyHttpPost(StepExecutionResult result)
    {
        return new VerificationResult
        {
            Outcome          = OutcomeResult.VERIFIED,
            Evidence         = "HTTP POST returned success status",
            ExpectedCriteria = "HTTP 2xx response"
        };
    }

    private VerificationResult VerifyBrowserStep(PlanStep step, StepExecutionResult result)
    {
        // Net (Playwright) does its own verification — trust the result
        return new VerificationResult
        {
            Outcome          = OutcomeResult.VERIFIED,
            Evidence         = $"Browser action '{step.Action}' completed via Net/Playwright",
            ExpectedCriteria = "Browser step execution success"
        };
    }
}

// ── Standalone verify request/response (for POST /criteria/verify) ───────────

public class CriteriaVerifyRequest
{
    public string  Action { get; set; } = "";
    public string? Data   { get; set; }
    public bool    StepSuccess { get; set; } = true;
}
