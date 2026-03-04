namespace Archimedes.Core;

/// <summary>
/// Phase 27 – Source Intelligence.
/// Maintains and queries the learned reliability/relevance of search sources.
/// Wraps SourceStore with ranking logic and outcome feedback.
/// </summary>
public class SourceIntelligence
{
    private readonly SourceStore _store;

    // Thresholds for tiered search strategy
    public const double HighTierThreshold = 0.70;
    public const double MidTierThreshold  = 0.40;

    public SourceIntelligence(SourceStore store)
    {
        _store = store;
    }

    // ── Ranked source lists for progressive search ─────────────────────────

    /// <summary>
    /// Tier 1: High-confidence sources for this capability (score > 0.70).
    /// Search here first – fastest path.
    /// </summary>
    public List<SourceRecord> GetTier1Sources(string capability)
        => GetSources(capability)
           .Where(s => CombinedScore(s, capability) >= HighTierThreshold)
           .ToList();

    /// <summary>
    /// Tier 2: Moderate-confidence sources (0.40 – 0.70).
    /// Search if Tier 1 yields nothing.
    /// </summary>
    public List<SourceRecord> GetTier2Sources(string capability)
        => GetSources(capability)
           .Where(s =>
           {
               var sc = CombinedScore(s, capability);
               return sc >= MidTierThreshold && sc < HighTierThreshold;
           })
           .ToList();

    /// <summary>
    /// Tier 3: All remaining sources – full sweep (surface + deep + dark).
    /// Only reached when tiers 1 and 2 yield nothing.
    /// </summary>
    public List<SourceRecord> GetTier3Sources(string capability)
        => GetSources(capability)
           .Where(s => CombinedScore(s, capability) < MidTierThreshold)
           .ToList();

    /// <summary>
    /// Returns the full ranked list across all tiers.
    /// </summary>
    public List<SourceRecord> GetAllRanked(string capability)
        => GetSources(capability);

    // ── Feedback loop ──────────────────────────────────────────────────────

    public void OnSearchCompleted(string sourceId, string capability, bool foundSomething)
        => _store.RecordSearch(sourceId, capability, foundSomething);

    public void OnInstallSucceeded(string sourceId)
        => _store.RecordInstall(sourceId, success: true);

    public void OnInstallFailed(string sourceId)
        => _store.RecordInstall(sourceId, success: false);

    public void EnsureSourceKnown(string sourceId, string baseUrl, ToolSourceType type)
        => _store.EnsureSource(sourceId, baseUrl, type);

    // ── Stats ──────────────────────────────────────────────────────────────

    public List<SourceRecord> GetAll() => _store.GetAll();

    // ── Private helpers ────────────────────────────────────────────────────

    private List<SourceRecord> GetSources(string capability)
        => _store.GetRanked(capability);

    private static double CombinedScore(SourceRecord s, string capability)
        => s.RelevanceScore(capability) * 0.6 + s.ReliabilityScore * 0.4;
}
