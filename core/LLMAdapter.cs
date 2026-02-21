using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

public class LLMAdapter
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly int _timeoutMs;
    private bool _lastCheckAvailable;
    
    public LLMAdapter(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL") ?? "http://127.0.0.1:11434";
        _model = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "llama3.2:3b";
        _timeoutMs = 8000;
        _httpClient.Timeout = TimeSpan.FromMilliseconds(_timeoutMs);
    }
    
    public async Task<LLMHealthResult> HealthCheck()
    {
        var result = new LLMHealthResult
        {
            Runtime = "ollama",
            Model = _model
        };
        
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/tags");
            if (response.IsSuccessStatusCode)
            {
                result.Available = true;
                _lastCheckAvailable = true;
                ArchLogger.LogInfo($"LLM health check: available (model={_model})");
            }
            else
            {
                result.Available = false;
                result.Error = $"Status: {response.StatusCode}";
                _lastCheckAvailable = false;
            }
        }
        catch (Exception ex)
        {
            result.Available = false;
            result.Error = ex.Message;
            _lastCheckAvailable = false;
            ArchLogger.LogWarn($"LLM health check failed: {ex.Message}");
        }
        
        return result;
    }
    
    public async Task<InterpretResult> Interpret(string userPrompt)
    {
        var promptHash = HashString(userPrompt);
        ArchLogger.LogInfo($"LLM interpret: prompt_hash={promptHash} len={userPrompt.Length}");
        
        var sanitized = SanitizeForLLM(userPrompt);
        
        try
        {
            var systemPrompt = @"You are an intent parser. Given a user request, extract:
- intent: one of TESTSITE_EXPORT, TESTSITE_MONITOR, WEB_LOGIN, DATA_EXTRACT, FILE_DOWNLOAD, UNKNOWN
- slots: key-value pairs like {""url"": ""..."", ""username"": ""...""}
- confidence: 0.0 to 1.0
- clarificationQuestions: array of questions if intent unclear

Respond ONLY with valid JSON matching this schema:
{""intent"": ""string"", ""slots"": {}, ""confidence"": 0.0, ""clarificationQuestions"": []}";

            var response = await CallOllama(systemPrompt, sanitized);
            
            if (response != null)
            {
                var parsed = JsonSerializer.Deserialize<InterpretResult>(response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null && !string.IsNullOrEmpty(parsed.Intent))
                {
                    ArchLogger.LogInfo($"LLM interpret result: intent={parsed.Intent} confidence={parsed.Confidence}");
                    return parsed;
                }
            }
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"LLM interpret failed, using heuristic: {ex.Message}");
        }
        
        return HeuristicInterpret(userPrompt);
    }
    
    public async Task<SummarizeResult> Summarize(string content)
    {
        var contentHash = HashString(content);
        ArchLogger.LogInfo($"LLM summarize: content_hash={contentHash} len={content.Length}");
        
        var sanitized = SanitizeForLLM(content);
        var truncated = sanitized.Length > 2000 ? sanitized[..2000] + "..." : sanitized;
        
        try
        {
            var systemPrompt = @"You are a summarizer. Given content, produce:
- shortSummary: 1-2 sentence summary
- bulletInsights: key points as array
- risks: potential issues or concerns
- nextQuestions: follow-up questions

Respond ONLY with valid JSON:
{""shortSummary"": ""..."", ""bulletInsights"": [], ""risks"": [], ""nextQuestions"": []}";

            var response = await CallOllama(systemPrompt, truncated);
            
            if (response != null)
            {
                var parsed = JsonSerializer.Deserialize<SummarizeResult>(response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null && !string.IsNullOrEmpty(parsed.ShortSummary))
                {
                    ArchLogger.LogInfo($"LLM summarize result: summary_len={parsed.ShortSummary.Length}");
                    return parsed;
                }
            }
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"LLM summarize failed, using heuristic: {ex.Message}");
        }
        
        return HeuristicSummarize(content);
    }
    
    private async Task<string?> CallOllama(string systemPrompt, string userContent)
    {
        var request = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            },
            stream = false,
            format = "json"
        };
        
        using var cts = new CancellationTokenSource(_timeoutMs);
        
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/chat", request, cts.Token);
            
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            
            var responseBody = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(responseBody);
            
            if (json.RootElement.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content))
            {
                return content.GetString();
            }
        }
        catch (OperationCanceledException)
        {
            ArchLogger.LogWarn("LLM request timed out");
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"LLM request failed: {ex.Message}");
        }
        
        return null;
    }
    
    private static InterpretResult HeuristicInterpret(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        var result = new InterpretResult
        {
            IsHeuristicFallback = true,
            Confidence = 0.6
        };
        
        if (lower.Contains("testsite") && (lower.Contains("download") || lower.Contains("csv") || lower.Contains("export")))
        {
            result.Intent = "TESTSITE_EXPORT";
            result.Confidence = 0.8;
        }
        else if (lower.Contains("testsite") && (lower.Contains("monitor") || lower.Contains("watch") || lower.Contains("poll")))
        {
            result.Intent = "TESTSITE_MONITOR";
            result.Confidence = 0.8;
        }
        else if (lower.Contains("login"))
        {
            result.Intent = "WEB_LOGIN";
            result.Slots["action"] = "login";
        }
        else if (lower.Contains("download"))
        {
            result.Intent = "FILE_DOWNLOAD";
        }
        else if (lower.Contains("extract") || lower.Contains("scrape") || lower.Contains("table"))
        {
            result.Intent = "DATA_EXTRACT";
        }
        else
        {
            result.Intent = "UNKNOWN";
            result.Confidence = 0.3;
            result.ClarificationQuestions.Add("What specific action would you like me to perform?");
        }
        
        ArchLogger.LogInfo($"Heuristic interpret: intent={result.Intent}");
        return result;
    }
    
    private static SummarizeResult HeuristicSummarize(string content)
    {
        var result = new SummarizeResult
        {
            IsHeuristicFallback = true
        };
        
        var sentences = content.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        if (sentences.Length > 0)
        {
            result.ShortSummary = sentences[0].Trim() + ".";
        }
        else
        {
            result.ShortSummary = content.Length > 100 ? content[..100] + "..." : content;
        }
        
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        result.BulletInsights = lines.Take(5).Select(l => l.Trim()).Where(l => l.Length > 10).ToList();
        
        result.NextQuestions.Add("Would you like more details?");
        
        ArchLogger.LogInfo($"Heuristic summarize: summary_len={result.ShortSummary.Length}");
        return result;
    }
    
    private static string SanitizeForLLM(string input)
    {
        input = System.Text.RegularExpressions.Regex.Replace(input, @"(password|passwd|pwd|secret|token|bearer|jwt|api[-_]?key|auth[-_]?token|session[-_]?id|cookie)[\s:=]+\S+", "$1=[REDACTED]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        input = System.Text.RegularExpressions.Regex.Replace(input, @"[a-zA-Z0-9+/]{40,}", "[LONG_TOKEN_REDACTED]");
        return input;
    }
    
    private static string HashString(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..12];
    }
}
