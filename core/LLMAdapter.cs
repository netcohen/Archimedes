using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using LLama;
using LLama.Common;
using LLama.Sampling;

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

public class LLMAdapter : IDisposable
{
    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private readonly string _modelPath;
    private readonly int _gpuLayers;
    private readonly object _loadLock = new();
    private bool _modelLoaded;

    public LLMAdapter()
    {
        _modelPath = Environment.GetEnvironmentVariable("ARCHIMEDES_MODEL_PATH")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Archimedes", "models", "llama3.2-3b.gguf");

        _gpuLayers = int.TryParse(
            Environment.GetEnvironmentVariable("LLM_GPU_LAYERS"), out var g) ? g : 0;
    }

    // -----------------------------------------------------------------------
    // Model lifecycle
    // -----------------------------------------------------------------------

    private bool EnsureModel()
    {
        if (_modelLoaded) return _weights != null;

        lock (_loadLock)
        {
            if (_modelLoaded) return _weights != null;
            _modelLoaded = true;

            if (!File.Exists(_modelPath))
            {
                ArchLogger.LogWarn($"[LLM] Model not found: {_modelPath}");
                return false;
            }

            try
            {
                ArchLogger.LogInfo($"[LLM] Loading model: {Path.GetFileName(_modelPath)}");
                _modelParams = new ModelParams(_modelPath)
                {
                    ContextSize = 2048,
                    GpuLayerCount = _gpuLayers
                };
                _weights = LLamaWeights.LoadFromFile(_modelParams);
                ArchLogger.LogInfo("[LLM] Model loaded via LLamaSharp");
                return true;
            }
            catch (Exception ex)
            {
                ArchLogger.LogWarn($"[LLM] Model load failed: {ex.Message}");
                return false;
            }
        }
    }

    public void Dispose()
    {
        _weights?.Dispose();
        _weights = null;
    }

    // -----------------------------------------------------------------------
    // Inference
    // -----------------------------------------------------------------------

    private async Task<string?> RunInference(string systemPrompt, string userContent, int maxTokens = 512)
    {
        if (_weights == null || _modelParams == null) return null;

        try
        {
            ArchLogger.LogInfo($"[LLM] Starting inference maxTokens={maxTokens}");
            var executor = new StatelessExecutor(_weights, _modelParams);

            // Llama 3.2 Instruct chat format.
            // NOTE: do NOT include <|begin_of_text|> — LLamaSharp adds the BOS token
            // automatically; including it here causes a double-BOS that corrupts inference.
            var prompt =
                "<|start_header_id|>system<|end_header_id|>\n\n" +
                systemPrompt +
                "<|eot_id|><|start_header_id|>user<|end_header_id|>\n\n" +
                userContent +
                "<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n";

            ArchLogger.LogInfo($"[LLM] Prompt len={prompt.Length}, calling InferAsync...");

            // In LLamaSharp 0.17.x Temperature must go through SamplingPipeline;
            // setting it directly on InferenceParams causes a NullReferenceException.
            var inferParams = new InferenceParams
            {
                MaxTokens      = maxTokens,
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.1f },
                AntiPrompts    = new List<string>
                {
                    "<|eot_id|>",
                    "<|end_of_text|>",
                    "<|start_header_id|>"
                }
            };

            var sb = new StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inferParams))
                sb.Append(token);

            var result = sb.ToString().Trim();
            ArchLogger.LogInfo($"[LLM] Inference done, output len={result.Length}");
            return result;
        }
        catch (Exception ex)
        {
            // Log full stack trace so we can diagnose any future failure
            ArchLogger.LogWarn($"[LLM] Inference error:\n{ex}");
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public Task<LLMHealthResult> HealthCheck()
    {
        var available = EnsureModel();
        return Task.FromResult(new LLMHealthResult
        {
            Available = available,
            Model     = Path.GetFileName(_modelPath),
            Runtime   = "llamasharp",
            Error     = available ? null : $"Model not found or failed to load: {_modelPath}"
        });
    }

    public async Task<InterpretResult> Interpret(string userPrompt)
    {
        var promptHash = HashString(userPrompt);
        ArchLogger.LogInfo($"[LLM] Interpret: hash={promptHash} len={userPrompt.Length}");

        var sanitized = SanitizeForLLM(userPrompt);

        if (!EnsureModel())
            return HeuristicInterpret(userPrompt);

        try
        {
            const string systemPrompt =
                "You are an intent parser. Given a user request, extract:\n" +
                "- intent: one of TESTSITE_EXPORT, TESTSITE_MONITOR, WEB_LOGIN, DATA_EXTRACT, FILE_DOWNLOAD, UNKNOWN\n" +
                "- slots: key-value pairs like {\"url\": \"...\", \"username\": \"...\"}\n" +
                "- confidence: 0.0 to 1.0\n" +
                "- clarificationQuestions: array of questions if intent unclear\n\n" +
                "Respond ONLY with valid JSON matching this schema:\n" +
                "{\"intent\": \"string\", \"slots\": {}, \"confidence\": 0.0, \"clarificationQuestions\": []}";

            var raw = await RunInference(systemPrompt, sanitized, maxTokens: 256);

            if (!string.IsNullOrWhiteSpace(raw))
            {
                var json = ExtractJson(raw);
                if (json != null)
                {
                    var parsed = JsonSerializer.Deserialize<InterpretResult>(
                        json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (parsed != null && !string.IsNullOrEmpty(parsed.Intent))
                    {
                        ArchLogger.LogInfo($"[LLM] Intent={parsed.Intent} confidence={parsed.Confidence}");
                        return parsed;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[LLM] Interpret failed, using heuristic: {ex.Message}");
        }

        return HeuristicInterpret(userPrompt);
    }

    public async Task<SummarizeResult> Summarize(string content)
    {
        var contentHash = HashString(content);
        ArchLogger.LogInfo($"[LLM] Summarize: hash={contentHash} len={content.Length}");

        var sanitized = SanitizeForLLM(content);
        var truncated = sanitized.Length > 2000 ? sanitized[..2000] + "..." : sanitized;

        if (!EnsureModel())
            return HeuristicSummarize(content);

        try
        {
            const string systemPrompt =
                "You are a summarizer. Given content, produce:\n" +
                "- shortSummary: 1-2 sentence summary\n" +
                "- bulletInsights: key points as array\n" +
                "- risks: potential issues or concerns\n" +
                "- nextQuestions: follow-up questions\n\n" +
                "Respond ONLY with valid JSON:\n" +
                "{\"shortSummary\": \"...\", \"bulletInsights\": [], \"risks\": [], \"nextQuestions\": []}";

            var raw = await RunInference(systemPrompt, truncated, maxTokens: 512);

            if (!string.IsNullOrWhiteSpace(raw))
            {
                var json = ExtractJson(raw);
                if (json != null)
                {
                    var parsed = JsonSerializer.Deserialize<SummarizeResult>(
                        json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (parsed != null && !string.IsNullOrEmpty(parsed.ShortSummary))
                    {
                        ArchLogger.LogInfo($"[LLM] Summary len={parsed.ShortSummary.Length}");
                        return parsed;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[LLM] Summarize failed, using heuristic: {ex.Message}");
        }

        return HeuristicSummarize(content);
    }

    // -----------------------------------------------------------------------
    // Phase 27: General-purpose LLM query
    // -----------------------------------------------------------------------

    /// <summary>
    /// General-purpose LLM call. Used by Phase 27 components (ToolEvaluator,
    /// LegalityChecker, SearchOrchestrator, ToolInstaller) for ad-hoc queries.
    /// Falls back to empty string if model unavailable.
    /// </summary>
    public async Task<string> AskAsync(
        string systemPrompt, string userContent, int maxTokens = 512)
    {
        ArchLogger.LogInfo($"[LLM] AskAsync: sys={systemPrompt[..Math.Min(60, systemPrompt.Length)]}...");

        if (!EnsureModel())
        {
            ArchLogger.LogWarn("[LLM] AskAsync: model not available, returning empty");
            return string.Empty;
        }

        try
        {
            var sanitized = SanitizeForLLM(userContent);
            var raw       = await RunInference(systemPrompt, sanitized, maxTokens);
            return raw ?? string.Empty;
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[LLM] AskAsync failed: {ex.Message}");
            return string.Empty;
        }
    }

    // -----------------------------------------------------------------------
    // Heuristic fallbacks (always available even without model)
    // -----------------------------------------------------------------------

    private static InterpretResult HeuristicInterpret(string prompt)
    {
        var lower  = prompt.ToLowerInvariant();
        var result = new InterpretResult { IsHeuristicFallback = true, Confidence = 0.6 };

        if (lower.Contains("testsite") && (lower.Contains("download") || lower.Contains("csv") || lower.Contains("export")))
        {
            result.Intent = "TESTSITE_EXPORT"; result.Confidence = 0.8;
        }
        else if (lower.Contains("testsite") && (lower.Contains("monitor") || lower.Contains("watch") || lower.Contains("poll")))
        {
            result.Intent = "TESTSITE_MONITOR"; result.Confidence = 0.8;
        }
        else if (lower.Contains("login"))
        {
            result.Intent = "WEB_LOGIN"; result.Slots["action"] = "login";
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

        ArchLogger.LogInfo($"[LLM] Heuristic summary len={result.ShortSummary.Length}");
        return result;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Extracts JSON object from a string that may contain markdown or extra text.</summary>
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

    private static string HashString(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..12];
    }
}
