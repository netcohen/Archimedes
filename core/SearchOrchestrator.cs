using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace Archimedes.Core;

/// <summary>
/// Phase 27 – Multi-tier Search Orchestrator.
/// Searches for tool candidates across Surface, Deep, and Dark web sources.
///
/// Tier 1: High-scoring sources for the requested capability
/// Tier 2: Medium-scoring sources
/// Tier 3: Full sweep (all sources including dark web via Tor)
///
/// Each tier stops early if enough candidates are found.
/// Source Intelligence is updated after every search.
/// </summary>
public class SearchOrchestrator
{
    private readonly HttpClient        _http;
    private readonly HttpClient?       _torHttp;   // null if Tor unavailable
    private readonly LLMAdapter        _llm;
    private readonly SourceIntelligence _intel;

    private const int MinCandidatesBeforeStop = 3;
    private const int TorSocksPort = 9050;

    public SearchOrchestrator(HttpClient http, LLMAdapter llm, SourceIntelligence intel)
    {
        _http  = http;
        _llm   = llm;
        _intel = intel;
        _torHttp = TryBuildTorClient();

        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (compatible; ArchimedesResearch/1.0)");
    }

    // ── Public entry point ─────────────────────────────────────────────────

    /// <summary>
    /// Main search method. Runs tier 1 → 2 → 3 progressively.
    /// Returns ranked candidates, best first.
    /// </summary>
    public async Task<List<ToolCandidate>> SearchAsync(
        string capability, string context, CancellationToken ct = default)
    {
        ArchLogger.LogInfo($"[Search] Starting search for capability: {capability}");
        var all = new List<ToolCandidate>();

        // Generate search queries via LLM
        var queries = await GenerateQueriesAsync(capability, context);
        ArchLogger.LogInfo($"[Search] Generated {queries.Count} queries");

        // Tier 1: high-confidence sources
        var tier1 = _intel.GetTier1Sources(capability);
        ArchLogger.LogInfo($"[Search] Tier1 sources: {tier1.Count}");
        all.AddRange(await SearchSources(tier1, queries, capability, ct));
        if (all.Count >= MinCandidatesBeforeStop)
            return Rank(all, capability);

        // Tier 2: moderate sources
        var tier2 = _intel.GetTier2Sources(capability);
        ArchLogger.LogInfo($"[Search] Tier2 sources: {tier2.Count}");
        all.AddRange(await SearchSources(tier2, queries, capability, ct));
        if (all.Count >= MinCandidatesBeforeStop)
            return Rank(all, capability);

        // Tier 3: full sweep (including dark web if Tor available)
        var tier3 = _intel.GetTier3Sources(capability);
        ArchLogger.LogInfo($"[Search] Tier3 full sweep: {tier3.Count} sources");
        all.AddRange(await SearchSources(tier3, queries, capability, ct));

        return Rank(all, capability);
    }

    // ── Query generation ───────────────────────────────────────────────────

    private async Task<List<string>> GenerateQueriesAsync(string capability, string context)
    {
        var prompt = $@"Generate 4 search queries to find a software integration or library for:
Capability: {capability}
Context: {context}

Return JSON array of strings only. Example: [""whatsapp api library csharp"", ""send whatsapp message dotnet""]
Queries:";

        try
        {
            var raw = await _llm.AskAsync(
                "You are a software research assistant. Output only valid JSON arrays.",
                prompt, 256);
            var json = ExtractJsonArray(raw);
            if (json != null)
            {
                var queries = JsonSerializer.Deserialize<List<string>>(json);
                if (queries is { Count: > 0 }) return queries;
            }
        }
        catch { /* fall through to defaults */ }

        // Heuristic fallback
        var cap = capability.ToLowerInvariant().Replace('_', ' ');
        return new List<string>
        {
            $"{cap} api library csharp dotnet",
            $"{cap} integration nuget package",
            $"{cap} open source github",
            $"{cap} unofficial api wrapper"
        };
    }

    // ── Per-source search dispatch ─────────────────────────────────────────

    private async Task<List<ToolCandidate>> SearchSources(
        List<SourceRecord> sources, List<string> queries,
        string capability, CancellationToken ct)
    {
        var results = new List<ToolCandidate>();
        foreach (var src in sources)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var found = await SearchOneSource(src, queries, capability, ct);
                _intel.OnSearchCompleted(src.SourceId, capability, found.Count > 0);
                results.AddRange(found);
            }
            catch (Exception ex)
            {
                ArchLogger.LogWarn($"[Search] Source {src.SourceId} failed: {ex.Message}");
                _intel.OnSearchCompleted(src.SourceId, capability, false);
            }
        }
        return results;
    }

    private async Task<List<ToolCandidate>> SearchOneSource(
        SourceRecord src, List<string> queries, string capability, CancellationToken ct)
    {
        var client = src.SourceType == ToolSourceType.DARK && _torHttp != null
            ? _torHttp
            : _http;

        return src.SourceId switch
        {
            "nuget.org"       => await SearchNuGet(queries, capability, client, ct),
            "npmjs.com"       => await SearchNpm(queries, capability, client, ct),
            "github.com"      => await SearchGitHub(queries, capability, client, ct),
            "gitlab.com"      => await SearchGitLab(queries, capability, client, ct),
            "pypi.org"        => await SearchPyPI(queries, capability, client, ct),
            _                 => await SearchDuckDuckGo(queries, capability, src, client, ct)
        };
    }

    // ── NuGet ──────────────────────────────────────────────────────────────

    private async Task<List<ToolCandidate>> SearchNuGet(
        List<string> queries, string cap, HttpClient client, CancellationToken ct)
    {
        var results = new List<ToolCandidate>();
        var q = Uri.EscapeDataString(queries.FirstOrDefault() ?? cap);
        var url = $"https://azuresearch-usnc.nuget.org/query?q={q}&take=5&prerelease=false";

        var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return results;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return results;

        foreach (var pkg in data.EnumerateArray().Take(5))
        {
            var id   = pkg.GetProperty("id").GetString() ?? "";
            var desc = pkg.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var ver  = pkg.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";

            results.Add(new ToolCandidate
            {
                Capability  = cap,
                Name        = id,
                Description = desc[..Math.Min(200, desc.Length)],
                SourceUrl   = $"https://www.nuget.org/packages/{id}",
                SourceDomain = "nuget.org",
                SourceType  = ToolSourceType.SURFACE,
                Strategy    = ToolExecutionStrategy.NUGET_PACKAGE,
                PackageName = id,
                PackageVersion = ver
            });
        }
        return results;
    }

    // ── npm ────────────────────────────────────────────────────────────────

    private async Task<List<ToolCandidate>> SearchNpm(
        List<string> queries, string cap, HttpClient client, CancellationToken ct)
    {
        var results = new List<ToolCandidate>();
        var q = Uri.EscapeDataString(queries.FirstOrDefault() ?? cap);
        var url = $"https://registry.npmjs.org/-/v1/search?text={q}&size=5";

        var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return results;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("objects", out var objs)) return results;

        foreach (var obj in objs.EnumerateArray().Take(5))
        {
            if (!obj.TryGetProperty("package", out var pkg)) continue;
            var name = pkg.GetProperty("name").GetString() ?? "";
            var desc = pkg.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var ver  = pkg.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";

            results.Add(new ToolCandidate
            {
                Capability   = cap,
                Name         = name,
                Description  = desc[..Math.Min(200, desc.Length)],
                SourceUrl    = $"https://www.npmjs.com/package/{name}",
                SourceDomain = "npmjs.com",
                SourceType   = ToolSourceType.SURFACE,
                Strategy     = ToolExecutionStrategy.LOCAL_SCRIPT,
                PackageName  = name,
                PackageVersion = ver
            });
        }
        return results;
    }

    // ── GitHub ─────────────────────────────────────────────────────────────

    private async Task<List<ToolCandidate>> SearchGitHub(
        List<string> queries, string cap, HttpClient client, CancellationToken ct)
    {
        var results = new List<ToolCandidate>();
        var q = Uri.EscapeDataString(queries.FirstOrDefault() ?? cap);
        var url = $"https://api.github.com/search/repositories?q={q}&sort=stars&per_page=5";

        client.DefaultRequestHeaders.Remove("Accept");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

        var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return results;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items)) return results;

        foreach (var repo in items.EnumerateArray().Take(5))
        {
            var name    = repo.TryGetProperty("full_name", out var n) ? n.GetString() ?? "" : "";
            var desc    = repo.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var htmlUrl = repo.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            results.Add(new ToolCandidate
            {
                Capability   = cap,
                Name         = name,
                Description  = (desc ?? "")[..Math.Min(200, (desc ?? "").Length)],
                SourceUrl    = htmlUrl,
                SourceDomain = "github.com",
                SourceType   = ToolSourceType.SURFACE,
                Strategy     = ToolExecutionStrategy.HTTP_API
            });
        }
        return results;
    }

    // ── GitLab ─────────────────────────────────────────────────────────────

    private async Task<List<ToolCandidate>> SearchGitLab(
        List<string> queries, string cap, HttpClient client, CancellationToken ct)
    {
        var results = new List<ToolCandidate>();
        var q = Uri.EscapeDataString(queries.FirstOrDefault() ?? cap);
        var url = $"https://gitlab.com/api/v4/projects?search={q}&order_by=star_count&per_page=5";

        var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return results;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        foreach (var proj in doc.RootElement.EnumerateArray().Take(5))
        {
            var name    = proj.TryGetProperty("path_with_namespace", out var n) ? n.GetString() ?? "" : "";
            var desc    = proj.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var webUrl  = proj.TryGetProperty("web_url", out var w) ? w.GetString() ?? "" : "";

            results.Add(new ToolCandidate
            {
                Capability   = cap,
                Name         = name,
                Description  = (desc ?? "")[..Math.Min(200, (desc ?? "").Length)],
                SourceUrl    = webUrl,
                SourceDomain = "gitlab.com",
                SourceType   = ToolSourceType.DEEP,
                Strategy     = ToolExecutionStrategy.HTTP_API
            });
        }
        return results;
    }

    // ── PyPI ───────────────────────────────────────────────────────────────

    private async Task<List<ToolCandidate>> SearchPyPI(
        List<string> queries, string cap, HttpClient client, CancellationToken ct)
    {
        var results = new List<ToolCandidate>();
        // PyPI doesn't have a free search API, use DuckDuckGo for site:pypi.org
        var query = $"site:pypi.org {queries.FirstOrDefault() ?? cap}";
        return await SearchDuckDuckGo(
            new List<string> { query }, cap,
            new SourceRecord { SourceId = "pypi.org", BaseUrl = "https://pypi.org", SourceType = ToolSourceType.SURFACE },
            client, ct);
    }

    // ── DuckDuckGo fallback ────────────────────────────────────────────────

    private async Task<List<ToolCandidate>> SearchDuckDuckGo(
        List<string> queries, string cap, SourceRecord src,
        HttpClient client, CancellationToken ct)
    {
        var results = new List<ToolCandidate>();
        var query   = queries.FirstOrDefault() ?? cap;

        // Add site filter for known domains
        if (src.SourceId is "stackoverflow.com" or "reddit.com" or "hackernews")
            query = $"site:{src.BaseUrl.Replace("https://", "")} {query}";

        var encoded = Uri.EscapeDataString(query);
        var url     = $"https://html.duckduckgo.com/html/?q={encoded}";

        try
        {
            var html = await client.GetStringAsync(url, ct);

            // Parse result links and titles from DuckDuckGo HTML
            var links  = Regex.Matches(html, @"<a[^>]+class=""result__a""[^>]+href=""([^""]+)""[^>]*>(.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var snippets = Regex.Matches(html, @"<a[^>]+class=""result__snippet""[^>]*>(.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            for (int i = 0; i < Math.Min(links.Count, 5); i++)
            {
                var href  = HttpUtility.HtmlDecode(links[i].Groups[1].Value.Trim());
                var title = Regex.Replace(links[i].Groups[2].Value, "<.*?>", "").Trim();
                var snip  = i < snippets.Count
                    ? Regex.Replace(snippets[i].Groups[1].Value, "<.*?>", "").Trim()
                    : "";

                if (string.IsNullOrEmpty(href) || href.StartsWith("//")) continue;

                results.Add(new ToolCandidate
                {
                    Capability   = cap,
                    Name         = title[..Math.Min(100, title.Length)],
                    Description  = snip[..Math.Min(200, snip.Length)],
                    SourceUrl    = href,
                    SourceDomain = src.SourceId,
                    SourceType   = src.SourceType,
                    Strategy     = ToolExecutionStrategy.HTTP_API
                });
            }
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[Search] DuckDuckGo search failed for {src.SourceId}: {ex.Message}");
        }

        return results;
    }

    // ── Ranking ────────────────────────────────────────────────────────────

    private static List<ToolCandidate> Rank(List<ToolCandidate> candidates, string capability)
    {
        // Deduplicate by source URL
        var seen = new HashSet<string>();
        var unique = candidates.Where(c =>
        {
            if (string.IsNullOrEmpty(c.SourceUrl)) return true;
            return seen.Add(c.SourceUrl);
        }).ToList();

        // Score: prefer NuGet/npm (structured), then GitHub, then generic
        foreach (var c in unique)
        {
            c.Score = c.Strategy switch
            {
                ToolExecutionStrategy.NUGET_PACKAGE => 0.8,
                ToolExecutionStrategy.LOCAL_SCRIPT  => 0.6,
                ToolExecutionStrategy.HTTP_API      => 0.5,
                _                                   => 0.4
            };
            // Bonus for SURFACE sources
            if (c.SourceType == ToolSourceType.SURFACE) c.Score += 0.1;
        }

        return unique.OrderByDescending(c => c.Score).ToList();
    }

    // ── Tor client ─────────────────────────────────────────────────────────

    private static HttpClient? TryBuildTorClient()
    {
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{TorSocksPort}"),
                UseProxy = true
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            ArchLogger.LogInfo("[Search] Tor SOCKS5 proxy configured on port 9050");
            return client;
        }
        catch
        {
            ArchLogger.LogInfo("[Search] Tor not available – dark web search disabled");
            return null;
        }
    }

    public bool IsTorAvailable => _torHttp != null;

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string? ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        var end   = text.LastIndexOf(']');
        if (start >= 0 && end > start)
            return text[start..(end + 1)];
        return null;
    }
}
