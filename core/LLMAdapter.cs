using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Archimedes.Core;

public class InterpretResult
{
    public string? Intent { get; set; }
    public Dictionary<string, string> Slots { get; set; } = new();
    public double Confidence { get; set; }
    public List<string> ClarificationQuestions { get; set; } = new();
    public bool IsHeuristicFallback { get; set; }
}

public class SummarizeResult
{
    public string ShortSummary { get; set; } = "";
    public List<string> BulletInsights { get; set; } = new();
    public List<string> Risks { get; set; } = new();
    public List<string> NextQuestions { get; set; } = new();
    public bool IsHeuristicFallback { get; set; }
}

public class LLMHealthResult
{
    public bool Available { get; set; }
    public string? Model { get; set; }
    public string? Runtime { get; set; }
    public string? Error { get; set; }
}

// =============================================================================
//  LLMAdapter — Ollama backend
//  Calls the local Ollama service (http://localhost:11434) via HTTP.
//  Ollama must be running: sudo systemctl status ollama
//  Model must be pulled:   ollama pull llama3.1:8b
// =============================================================================
public class LLMAdapter : IDisposable
{
    private readonly HttpClient _http;
    private readonly string     _ollamaBase;
    private readonly string     _model;

    public LLMAdapter()
    {
        _ollamaBase = Environment.GetEnvironmentVariable("ARCHIMEDES_OLLAMA_URL")
            ?? "http://localhost:11434";
        _model = Environment.GetEnvironmentVariable("ARCHIMEDES_OLLAMA_MODEL")
            ?? "qwen2.5:7b";

        // Timeout.InfiniteTimeSpan — each call uses its own CancellationTokenSource
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        ArchLogger.LogInfo($"[LLM] Ollama adapter: base={_ollamaBase} model={_model}");
    }

    public void Dispose() => _http.Dispose();

    // -----------------------------------------------------------------------
    // Health check
    // -----------------------------------------------------------------------

    public async Task<LLMHealthResult> HealthCheck()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var resp = await _http.GetAsync($"{_ollamaBase}/api/tags", cts.Token);

            if (!resp.IsSuccessStatusCode)
                return new LLMHealthResult
                {
                    Available = false, Model = _model, Runtime = "ollama",
                    Error = $"Ollama returned {resp.StatusCode}"
                };

            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            var doc  = JsonDocument.Parse(json);

            bool modelFound = false;
            var modelPrefix = _model.Split(':')[0];
            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var m in models.EnumerateArray())
                {
                    var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.StartsWith(modelPrefix, StringComparison.OrdinalIgnoreCase))
                    { modelFound = true; break; }
                }
            }

            return new LLMHealthResult
            {
                Available = modelFound,
                Model     = _model,
                Runtime   = "ollama",
                Error     = modelFound ? null : $"Model '{_model}' not pulled — run: ollama pull {_model}"
            };
        }
        catch (Exception ex)
        {
            return new LLMHealthResult
            {
                Available = false, Model = _model, Runtime = "ollama",
                Error = $"Ollama not running: {ex.Message}"
            };
        }
    }

    // -----------------------------------------------------------------------
    // Non-streaming inference (for Interpret / Summarize / AskAsync)
    // -----------------------------------------------------------------------

    private async Task<string?> ChatOnce(
        string systemPrompt, string userContent,
        int maxTokens = 512, int timeoutSeconds = 90)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        var payload = JsonSerializer.Serialize(new
        {
            model    = _model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userContent  }
            },
            stream  = false,
            options = new { num_predict = maxTokens, temperature = 0.1 }
        });

        ArchLogger.LogInfo($"[LLM] ChatOnce maxTokens={maxTokens} timeout={timeoutSeconds}s");
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp    = await _http.PostAsync($"{_ollamaBase}/api/chat", content, cts.Token);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        var doc  = JsonDocument.Parse(json);
        var text = doc.RootElement
            .GetProperty("message")
            .GetProperty("content")
            .GetString()?.Trim();

        ArchLogger.LogInfo($"[LLM] ChatOnce done, output len={text?.Length ?? 0}");
        return text;
    }

    // -----------------------------------------------------------------------
    // Streaming inference — yields tokens one by one
    // -----------------------------------------------------------------------

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt, string userContent, int maxTokens = 150,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model    = _model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userContent  }
            },
            stream  = true,
            options = new { num_predict = maxTokens, temperature = 0.1 }
        });

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_ollamaBase}/api/chat")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(line); }
            catch { continue; }

            using (doc)
            {
                if (doc.RootElement.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var c))
                {
                    var token = c.GetString();
                    if (!string.IsNullOrEmpty(token))
                        yield return token;
                }

                if (doc.RootElement.TryGetProperty("done", out var done) && done.GetBoolean())
                    yield break;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Public API — same interface as before
    // -----------------------------------------------------------------------

    public async Task<string> AskAsync(
        string systemPrompt, string userContent, int maxTokens = 512)
    {
        ArchLogger.LogInfo($"[LLM] AskAsync model={_model} maxTokens={maxTokens}");
        try
        {
            return await ChatOnce(systemPrompt, userContent, maxTokens) ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            ArchLogger.LogWarn("[LLM] AskAsync timed out");
            return string.Empty;
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[LLM] AskAsync failed: {ex.Message}");
            return string.Empty;
        }
    }

    public async Task<InterpretResult> Interpret(string userPrompt)
    {
        ArchLogger.LogInfo($"[LLM] Interpret len={userPrompt.Length}");
        var sanitized = SanitizeForLLM(userPrompt);

        const string sys =
            "You are an intent parser. Extract the intent from the user request. " +
            "Intent must be one of: TESTSITE_EXPORT, TESTSITE_MONITOR, WEB_LOGIN, DATA_EXTRACT, FILE_DOWNLOAD, UNKNOWN\n" +
            "Respond ONLY with valid JSON: " +
            "{\"intent\":\"string\",\"slots\":{},\"confidence\":0.0,\"clarificationQuestions\":[]}";

        try
        {
            var raw = await ChatOnce(sys, sanitized, maxTokens: 256);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var json   = ExtractJson(raw);
                var parsed = json == null ? null : JsonSerializer.Deserialize<InterpretResult>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null && !string.IsNullOrEmpty(parsed.Intent))
                    return parsed;
            }
        }
        catch (Exception ex) { ArchLogger.LogWarn($"[LLM] Interpret failed: {ex.Message}"); }

        return HeuristicInterpret(userPrompt);
    }

    public async Task<SummarizeResult> Summarize(string content)
    {
        ArchLogger.LogInfo($"[LLM] Summarize len={content.Length}");
        var sanitized = SanitizeForLLM(content);
        var truncated = sanitized.Length > 2000 ? sanitized[..2000] + "..." : sanitized;

        const string sys =
            "You are a summarizer. Produce a JSON summary:\n" +
            "{\"shortSummary\":\"...\",\"bulletInsights\":[],\"risks\":[],\"nextQuestions\":[]}";

        try
        {
            var raw = await ChatOnce(sys, truncated, maxTokens: 512);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var json   = ExtractJson(raw);
                var parsed = json == null ? null : JsonSerializer.Deserialize<SummarizeResult>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null && !string.IsNullOrEmpty(parsed.ShortSummary))
                    return parsed;
            }
        }
        catch (Exception ex) { ArchLogger.LogWarn($"[LLM] Summarize failed: {ex.Message}"); }

        return HeuristicSummarize(content);
    }

    // -----------------------------------------------------------------------
    // Heuristic fallbacks (no model needed)
    // -----------------------------------------------------------------------

    private static InterpretResult HeuristicInterpret(string prompt)
    {
        var lower  = prompt.ToLowerInvariant();
        var result = new InterpretResult { IsHeuristicFallback = true, Confidence = 0.6 };

        if (lower.Contains("testsite") && (lower.Contains("download") || lower.Contains("csv") || lower.Contains("export")))
            { result.Intent = "TESTSITE_EXPORT"; result.Confidence = 0.8; }
        else if (lower.Contains("testsite") && (lower.Contains("monitor") || lower.Contains("watch")))
            { result.Intent = "TESTSITE_MONITOR"; result.Confidence = 0.8; }
        else if (lower.Contains("login"))
            { result.Intent = "WEB_LOGIN"; result.Slots["action"] = "login"; }
        else if (lower.Contains("download"))
            { result.Intent = "FILE_DOWNLOAD"; }
        else if (lower.Contains("extract") || lower.Contains("scrape") || lower.Contains("table"))
            { result.Intent = "DATA_EXTRACT"; }
        else
        {
            result.Intent = "UNKNOWN"; result.Confidence = 0.3;
            result.ClarificationQuestions.Add("What specific action would you like me to perform?");
        }

        ArchLogger.LogInfo($"[LLM] Heuristic intent={result.Intent}");
        return result;
    }

    private static SummarizeResult HeuristicSummarize(string content)
    {
        var result    = new SummarizeResult { IsHeuristicFallback = true };
        var sentences = content.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        result.ShortSummary = sentences.Length > 0
            ? sentences[0].Trim() + "."
            : (content.Length > 100 ? content[..100] + "..." : content);

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        result.BulletInsights = lines.Take(5).Select(l => l.Trim()).Where(l => l.Length > 10).ToList();
        result.NextQuestions.Add("Would you like more details?");
        return result;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string? ExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end   = raw.LastIndexOf('}');
        if (start < 0 || end < 0 || end <= start) return null;
        return raw[start..(end + 1)];
    }

    private static string SanitizeForLLM(string input)
    {
        input = Regex.Replace(input,
            @"(password|passwd|pwd|secret|token|bearer|jwt|api[-_]?key|auth[-_]?token|session[-_]?id|cookie)[\s:=]+\S+",
            "$1=[REDACTED]", RegexOptions.IgnoreCase);
        input = Regex.Replace(input, @"[a-zA-Z0-9+/]{40,}", "[LONG_TOKEN_REDACTED]");
        return input;
    }
}
