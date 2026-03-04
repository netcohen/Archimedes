using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 27 – Persists Source Intelligence records.
/// Archimedes learns which sources reliably provide useful tool candidates.
/// Storage: %LOCALAPPDATA%\Archimedes\source_intelligence.json
/// </summary>
public class SourceStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, SourceRecord> _sources = new();

    private static readonly JsonSerializerOptions _json = new()
        { WriteIndented = true, PropertyNameCaseInsensitive = true };

    // Pre-seeded sources – Archimedes starts with these known sources (score 0.5)
    private static readonly List<(string id, string url, ToolSourceType type)> _seeds = new()
    {
        ("github.com",             "https://github.com",                         ToolSourceType.SURFACE),
        ("nuget.org",              "https://www.nuget.org",                       ToolSourceType.SURFACE),
        ("npmjs.com",              "https://www.npmjs.com",                       ToolSourceType.SURFACE),
        ("pypi.org",               "https://pypi.org",                            ToolSourceType.SURFACE),
        ("stackoverflow.com",      "https://stackoverflow.com",                   ToolSourceType.SURFACE),
        ("reddit.com",             "https://www.reddit.com",                      ToolSourceType.DEEP),
        ("hackernews",             "https://news.ycombinator.com",                ToolSourceType.DEEP),
        ("gitlab.com",             "https://gitlab.com",                          ToolSourceType.DEEP),
        ("hub.docker.com",         "https://hub.docker.com",                      ToolSourceType.DEEP),
        ("tor_toolrepo",           "http://toolrepo.onion",                       ToolSourceType.DARK),
    };

    public SourceStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Archimedes");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "source_intelligence.json");
        Load();
        Seed();
    }

    // ── Read ───────────────────────────────────────────────────────────────

    public List<SourceRecord> GetAll()
    {
        lock (_lock) { return _sources.Values.ToList(); }
    }

    public SourceRecord? Get(string sourceId)
    {
        lock (_lock)
        {
            _sources.TryGetValue(sourceId, out var r);
            return r;
        }
    }

    /// <summary>
    /// Returns sources ranked by relevance for a given capability category.
    /// High-score sources come first.
    /// </summary>
    public List<SourceRecord> GetRanked(string capability, ToolSourceType? filterType = null)
    {
        lock (_lock)
        {
            var list = _sources.Values.AsEnumerable();
            if (filterType.HasValue) list = list.Where(s => s.SourceType == filterType.Value);
            return list
                .OrderByDescending(s => s.RelevanceScore(capability) * 0.6 +
                                        s.ReliabilityScore * 0.4)
                .ToList();
        }
    }

    // ── Write ──────────────────────────────────────────────────────────────

    public void RecordSearch(string sourceId, string capability, bool found)
    {
        lock (_lock)
        {
            if (!_sources.TryGetValue(sourceId, out var rec)) return;
            rec.TotalSearches++;
            if (found) rec.SuccessfulFinds++;
            rec.LastUsed = DateTime.UtcNow;

            // Update category relevance with exponential moving average
            var prev = rec.CategoryRelevance.GetValueOrDefault(capability, 0.5);
            rec.CategoryRelevance[capability] = prev * 0.8 + (found ? 1.0 : 0.0) * 0.2;

            Save();
        }
    }

    public void RecordInstall(string sourceId, bool success)
    {
        lock (_lock)
        {
            if (!_sources.TryGetValue(sourceId, out var rec)) return;
            if (success) rec.SuccessfulInstalls++;
            else         rec.Failures++;
            rec.LastUsed = DateTime.UtcNow;
            Save();
        }
    }

    public void EnsureSource(string sourceId, string baseUrl, ToolSourceType type)
    {
        lock (_lock)
        {
            if (!_sources.ContainsKey(sourceId))
            {
                _sources[sourceId] = new SourceRecord
                {
                    SourceId   = sourceId,
                    BaseUrl    = baseUrl,
                    SourceType = type
                };
                Save();
            }
        }
    }

    // ── Persistence ────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var list = JsonSerializer.Deserialize<List<SourceRecord>>(
                    File.ReadAllText(_path), _json) ?? new();
                _sources = list.ToDictionary(s => s.SourceId);
            }
        }
        catch { _sources = new(); }
    }

    private void Save()
        => File.WriteAllText(_path,
            JsonSerializer.Serialize(_sources.Values.ToList(), _json));

    private void Seed()
    {
        lock (_lock)
        {
            var changed = false;
            foreach (var (id, url, type) in _seeds)
            {
                if (!_sources.ContainsKey(id))
                {
                    _sources[id] = new SourceRecord
                    {
                        SourceId   = id,
                        BaseUrl    = url,
                        SourceType = type
                    };
                    changed = true;
                }
            }
            if (changed) Save();
        }
    }
}
