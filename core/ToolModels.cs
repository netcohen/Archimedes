using System.Text.Json.Serialization;

namespace Archimedes.Core;

// ---------------------------------------------------------------------------
// Phase 27 – Autonomous Tool Acquisition
// All data models for the tool discovery, evaluation, and acquisition pipeline.
// ---------------------------------------------------------------------------

public enum ToolExecutionStrategy
{
    HTTP_API,           // Call a REST/HTTP API
    BROWSER_PROCEDURE,  // Use Playwright via procedure steps
    LOCAL_SCRIPT,       // Run a local PowerShell/Python script
    NUGET_PACKAGE       // Use a .NET NuGet package
}

public enum ToolRiskLevel
{
    SAFE,        // No known risks
    MANAGEABLE,  // Known issues Archimedes can handle autonomously
    DANGEROUS    // Risk of system damage – requires user approval or rejection
}

public enum ToolSourceType
{
    SURFACE,  // Regular web (Google, GitHub, NuGet, npm)
    DEEP,     // Specialised registries, forums, private repositories
    DARK      // Tor/.onion – legitimate tool repos, security research
}

public enum LegalStatus
{
    LEGAL,            // No legal concerns
    NEEDS_APPROVAL    // Potentially problematic – user must decide
}

// ---------------------------------------------------------------------------
// Tool Candidate – discovered but not yet acquired
// ---------------------------------------------------------------------------
public class ToolCandidate
{
    public string CandidateId  { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Capability   { get; set; } = "";   // e.g. "WHATSAPP_SEND"
    public string Name         { get; set; } = "";
    public string Description  { get; set; } = "";
    public string SourceUrl    { get; set; } = "";
    public string SourceDomain { get; set; } = "";
    public ToolSourceType SourceType   { get; set; } = ToolSourceType.SURFACE;
    public ToolExecutionStrategy Strategy { get; set; } = ToolExecutionStrategy.HTTP_API;

    // Strategy-specific details
    public string? ApiEndpoint    { get; set; }
    public string? PackageName    { get; set; }
    public string? PackageVersion { get; set; }
    public string? ProcedureHint  { get; set; } // Browser steps hint

    // Evaluation
    public ToolRiskLevel Risk       { get; set; } = ToolRiskLevel.SAFE;
    public string?       RiskReason { get; set; }
    public LegalStatus   Legal      { get; set; } = LegalStatus.LEGAL;
    public string?       LegalIssue { get; set; }
    public string?       LegalBasis { get; set; } // Which law / section

    public double Score { get; set; }  // Ranking score (0–1)
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
}

// ---------------------------------------------------------------------------
// Acquired Tool – installed and available for use
// ---------------------------------------------------------------------------
public class AcquiredTool
{
    public string ToolId      { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Capability  { get; set; } = "";  // Maps to ActionType / intent
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";

    public ToolExecutionStrategy Strategy { get; set; } = ToolExecutionStrategy.HTTP_API;
    public ToolRiskLevel         Risk     { get; set; } = ToolRiskLevel.SAFE;

    // Runtime config
    public string? ApiEndpoint { get; set; }
    public string? PackageName { get; set; }
    public string? ScriptPath  { get; set; }
    public string? ProcedureId { get; set; }  // Link to ProcedureStore entry
    public Dictionary<string, string> Config { get; set; } = new();

    // Provenance
    public string SourceUrl    { get; set; } = "";
    public ToolSourceType SourceType { get; set; } = ToolSourceType.SURFACE;
    public bool UserApproved   { get; set; }  // true if legal approval was granted

    // Usage stats
    public int    UsageCount   { get; set; }
    public int    SuccessCount { get; set; }
    public int    FailureCount { get; set; }
    public double ReliabilityScore => TotalUses == 0 ? 1.0 :
        (double)SuccessCount / TotalUses;

    [JsonIgnore]
    public int TotalUses => SuccessCount + FailureCount;

    public DateTime  AcquiredAt  { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt  { get; set; }
    public DateTime? LastSuccess { get; set; }
}

// ---------------------------------------------------------------------------
// Source Record – intelligence about a search source
// ---------------------------------------------------------------------------
public class SourceRecord
{
    public string         SourceId   { get; set; } = "";  // Domain/registry identifier
    public string         BaseUrl    { get; set; } = "";
    public ToolSourceType SourceType { get; set; } = ToolSourceType.SURFACE;

    public int TotalSearches     { get; set; }
    public int SuccessfulFinds   { get; set; }   // Search returned relevant candidate
    public int SuccessfulInstalls { get; set; }  // Candidate from here actually worked
    public int Failures          { get; set; }   // Candidate from here failed

    // Relevance by capability category (e.g. "messaging" → 0.91)
    public Dictionary<string, double> CategoryRelevance { get; set; } = new();

    // Derived scores
    public double ReliabilityScore => (SuccessfulInstalls + Failures) == 0 ? 0.5 :
        (double)SuccessfulInstalls / (SuccessfulInstalls + Failures);

    public double RelevanceScore(string category)
    {
        if (CategoryRelevance.TryGetValue(category, out var s)) return s;
        return TotalSearches == 0 ? 0.5 : (double)SuccessfulFinds / TotalSearches;
    }

    public DateTime? LastUsed    { get; set; }
    public DateTime  CreatedAt   { get; set; } = DateTime.UtcNow;
}

// ---------------------------------------------------------------------------
// Tool Gap Event – triggered when a needed capability is missing
// ---------------------------------------------------------------------------
public class ToolGapEvent
{
    public string    GapId      { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string    Capability { get; set; } = "";
    public string    Context    { get; set; } = "";  // User intent / task description
    public string?   GoalId     { get; set; }
    public string?   TaskId     { get; set; }
    public GapStatus Status     { get; set; } = GapStatus.SEARCHING;
    public string?   ResolvedToolId       { get; set; }
    public string?   PendingApprovalId    { get; set; }
    public string?   UserMessage          { get; set; }
    public DateTime  DetectedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt  { get; set; }
}

public enum GapStatus
{
    SEARCHING,        // Active search underway
    AWAITING_LEGAL,   // Waiting for user legal approval
    RESOLVED,         // Tool acquired successfully
    FAILED,           // Could not find or install any tool
    USER_REJECTED     // User explicitly rejected the legal request
}

// ---------------------------------------------------------------------------
// Legal Approval Request – shown to user in Chat
// ---------------------------------------------------------------------------
public class LegalApprovalRequest
{
    public string  ApprovalId   { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string  GapId        { get; set; } = "";
    public string  Capability   { get; set; } = "";
    public string  UserMessage  { get; set; } = "";   // Full explanation for user
    public string  LegalIssue   { get; set; } = "";
    public string? LegalBasis   { get; set; }
    public string? AlternativeSuggestion { get; set; }
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.PENDING;
    public string? UserNote     { get; set; }
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt  { get; set; }
}

public enum ApprovalDecision { PENDING, APPROVED, REJECTED, WAITING_RESEARCH }

// ---------------------------------------------------------------------------
// Legal Check Result – internal result from LegalityChecker
// ---------------------------------------------------------------------------
public class LegalCheckResult
{
    public LegalStatus Status     { get; set; } = LegalStatus.LEGAL;
    public string?     Issue      { get; set; }
    public string?     LegalBasis { get; set; }
    public string?     UserMessage { get; set; }
}
