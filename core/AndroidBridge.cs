using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Archimedes.Core;

/// <summary>
/// Phase 31 — Android Bridge.
/// Polls the Net service for commands sent from Android, executes them via Core engines,
/// and posts results back. Also provides a notification channel to push alerts to Android.
/// </summary>
public sealed class AndroidBridge : IDisposable
{
    private readonly HttpClient           _http;
    private readonly TaskService          _taskService;
    private readonly GoalEngine           _goalEngine;
    private readonly LLMAdapter           _llmAdapter;
    private readonly string               _netUrl;
    private readonly CancellationTokenSource _cts = new();
    private Task?                         _pollTask;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
    };

    public AndroidBridge(HttpClient http, TaskService taskService,
                         GoalEngine goalEngine, LLMAdapter llmAdapter)
    {
        _http         = http;
        _taskService  = taskService;
        _goalEngine   = goalEngine;
        _llmAdapter   = llmAdapter;
        _netUrl       = Environment.GetEnvironmentVariable("ARCHIMEDES_NET_URL")
                        ?? "http://localhost:5052";
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────

    public void Start()
    {
        _pollTask = Task.Run(() => PollLoop(_cts.Token));
        ArchLogger.LogInfo("[AndroidBridge] Started — polling every 5 s");
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _pollTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
        ArchLogger.LogInfo("[AndroidBridge] Stopped");
    }

    // ── Poll loop ──────────────────────────────────────────────────────────

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try   { await PollOnce(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { ArchLogger.LogWarn($"[AndroidBridge] poll error: {ex.Message}"); }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnce(CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.GetAsync($"{_netUrl}/v1/android/commands/pending", ct); }
        catch { return; } // Net service not running yet

        if (!resp.IsSuccessStatusCode) return;

        var json     = await resp.Content.ReadAsStringAsync(ct);
        var commands = JsonSerializer.Deserialize<AndroidCommandDto[]>(json, JsonOpts);
        if (commands == null || commands.Length == 0) return;

        foreach (var cmd in commands)
            _ = Task.Run(() => ExecuteCommand(cmd, ct), ct);
    }

    // ── Command execution ──────────────────────────────────────────────────

    private async Task ExecuteCommand(AndroidCommandDto cmd, CancellationToken ct)
    {
        ArchLogger.LogInfo($"[AndroidBridge] Executing cmd={cmd.Id} type={cmd.Type}");

        await PostResultAsync(cmd.Id, "RUNNING", new { }, ct);

        try
        {
            object result = cmd.Type?.ToUpperInvariant() switch
            {
                "STATUS" => GetStatusResult(),
                "TASK"   => ExecuteTaskCommand(cmd),
                "GOAL"   => await ExecuteGoalCommandAsync(cmd, ct),
                "CHAT"   => await ExecuteChatCommandAsync(cmd, ct),
                "APPROVE" or "DENY" => await HandleApprovalAsync(cmd, ct),
                _        => new { error = $"Unknown command type: {cmd.Type}" },
            };

            await PostResultAsync(cmd.Id, "DONE", result, ct);
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[AndroidBridge] Cmd {cmd.Id} failed: {ex.Message}");
            await PostResultAsync(cmd.Id, "FAILED", new { error = ex.Message }, ct);
        }
    }

    private static object GetStatusResult() => new
    {
        isRunning  = true,
        timestamp  = DateTime.UtcNow.ToString("o"),
        source     = "AndroidBridge",
    };

    private object ExecuteTaskCommand(AndroidCommandDto cmd)
    {
        var text = GetPayloadString(cmd, "text");
        if (string.IsNullOrWhiteSpace(text))
            return new { error = "No task text provided" };

        var task = _taskService.CreateTask(new CreateTaskRequest
        {
            Title      = text.Length > 60 ? text[..60] : text,
            UserPrompt = text,
        });
        return new { taskId = task.TaskId, title = task.Title, status = task.State.ToString() };
    }

    private async Task<object> ExecuteGoalCommandAsync(AndroidCommandDto cmd, CancellationToken ct)
    {
        var text = GetPayloadString(cmd, "text");
        if (string.IsNullOrWhiteSpace(text))
            return new { error = "No goal text provided" };

        var goal = await _goalEngine.CreateAsync(new CreateGoalRequest
        {
            Title      = text.Length > 80 ? text[..80] : text,
            UserPrompt = text,
        });
        return new { goalId = goal.GoalId, title = goal.Title };
    }

    private async Task<object> ExecuteChatCommandAsync(AndroidCommandDto cmd, CancellationToken ct)
    {
        var text = GetPayloadString(cmd, "text");
        if (string.IsNullOrWhiteSpace(text))
            return new { error = "No chat text provided" };

        var reply = await _llmAdapter.AskAsync(
            "You are Archimedes, a helpful AI agent. Reply concisely in the same language as the user.",
            text, 256);
        return new { reply };
    }

    private async Task<object> HandleApprovalAsync(AndroidCommandDto cmd, CancellationToken ct)
    {
        var approvalId = GetPayloadString(cmd, "approvalId");
        var approved   = cmd.Type?.ToUpperInvariant() == "APPROVE";

        if (string.IsNullOrWhiteSpace(approvalId))
            return new { error = "No approvalId provided" };

        // Forward to Core approval endpoint
        var body = JsonSerializer.Serialize(new { taskId = approvalId, approved });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            var r = await _http.PostAsync("http://localhost:5051/approval-response", content, ct);
            return new { approved, approvalId, status = r.IsSuccessStatusCode ? "ok" : "error" };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    // ── Notification ───────────────────────────────────────────────────────

    /// <summary>Send a push notification via Net service to all Android devices.</summary>
    public async Task NotifyAsync(string title, string body,
                                  Dictionary<string, string>? data = null)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                title,
                body,
                data = data ?? new Dictionary<string, string>(),
            });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            await _http.PostAsync($"{_netUrl}/v1/android/notify", content);
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[AndroidBridge] Notify failed: {ex.Message}");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string GetPayloadString(AndroidCommandDto cmd, string key)
    {
        if (cmd.Payload != null && cmd.Payload.TryGetValue(key, out var val))
            return val?.ToString() ?? "";
        return "";
    }

    private async Task PostResultAsync(string cmdId, string status, object result,
                                       CancellationToken ct = default)
    {
        try
        {
            var json    = JsonSerializer.Serialize(new { status, result }, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _http.PostAsync($"{_netUrl}/v1/android/commands/{cmdId}/result", content, ct);
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[AndroidBridge] PostResult failed for {cmdId}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    // ── DTO ────────────────────────────────────────────────────────────────

    private sealed class AndroidCommandDto
    {
        [JsonPropertyName("id")]      public string Id      { get; set; } = "";
        [JsonPropertyName("type")]    public string? Type   { get; set; }
        [JsonPropertyName("payload")] public Dictionary<string, object?>? Payload { get; set; }
        [JsonPropertyName("deviceId")]public string DeviceId{ get; set; } = "";
    }
}
