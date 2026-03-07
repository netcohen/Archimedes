using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Archimedes.Core;

// ── SSE event model ─────────────────────────────────────────────────────────

public enum ChatEventType { Token, Status, Done, Error }

public record ChatEvent(
    ChatEventType Type,
    string?       Data,
    string?       Command = null,
    string?       Output  = null)
{
    public static ChatEvent Token(string tok)
        => new(ChatEventType.Token,  tok);
    public static ChatEvent Status(string msg)
        => new(ChatEventType.Status, msg);
    public static ChatEvent Done(string? reply, string? cmd, string? output = null)
        => new(ChatEventType.Done,   reply, cmd, output);
    public static ChatEvent Error(string msg)
        => new(ChatEventType.Error,  msg);
}

// ── ChatOrchestrator ─────────────────────────────────────────────────────────

/// <summary>
/// Phase 36 — Chat brain. LLM is a translator; C# makes decisions.
///
/// Flow per user message:
///   1. ChatInterpret  → intent + slots       (heuristic first, LLM fallback)
///   2. QUESTION path  → StreamAsync (short)  → done
///   3. CommandStore.Lookup → command          (instant if already learned)
///   4. Unknown?       → GenerateCommand()     (one-time LLM call)
///   5. Disruptive?    → send Done first, then run in background
///   6. Execute        → (output, exitCode)
///   7. Fail?          → regenerate command with error context, retry once
///   8. Success + new  → CommandStore.Save()  [LEARNING — this is how Archimedes grows]
///   9. NarrateStream  → Hebrew sentence streamed to UI
///  10. Done + save history + EventMemory
/// </summary>
public class ChatOrchestrator
{
    private readonly LLMAdapter   _llm;
    private readonly CommandStore _commandStore;
    private readonly EventMemory  _eventMemory;

    // Short-term conversation memory (moved here from Program.cs)
    private readonly List<(string Role, string Content)> _history = new();
    private const int MAX_HISTORY = 3; // turns (= 6 messages)

    public ChatOrchestrator(LLMAdapter llm, CommandStore commandStore, EventMemory eventMemory)
    {
        _llm          = llm;
        _commandStore = commandStore;
        _eventMemory  = eventMemory;
    }

    // ── Main entry point ─────────────────────────────────────────────────────

    public async IAsyncEnumerable<ChatEvent> HandleAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArchLogger.LogInfo($"[Chat] HandleAsync: {userMessage[..Math.Min(60, userMessage.Length)]}");

        // 1. Intent classification
        InterpretResult intent;
        try { intent = await _llm.ChatInterpret(userMessage); }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[Chat] ChatInterpret failed: {ex.Message}");
            intent = new InterpretResult { Intent = "UNKNOWN", Confidence = 0.1 };
        }
        ArchLogger.LogInfo($"[Chat] Intent={intent.Intent} confidence={intent.Confidence:F2}");

        // 2. QUESTION / UNKNOWN path — pure conversation, no command
        bool isConversational = intent.Intent is "QUESTION" or "UNKNOWN"
            || intent.Confidence < 0.5;

        if (isConversational)
        {
            List<(string, string)> histSnap;
            lock (_history) { histSnap = _history.ToList(); }

            const string qSys = "ענה בעברית בקצרה ובבירור. מונחים טכניים — אנגלית מותרת.";
            var sb = new StringBuilder();
            await foreach (var tok in _llm.StreamAsync(qSys, userMessage, 150, ct, histSnap))
            {
                sb.Append(tok);
                yield return ChatEvent.Token(tok);
            }
            yield return ChatEvent.Done(null, null, null);
            SaveHistory(userMessage, sb.ToString(), null, null);
            yield break;
        }

        // 3. Resolve command from CommandStore (instant if known)
        var key     = CommandStore.MakeKey(intent);
        var command = _commandStore.Lookup(key);
        bool isNew  = command == null;

        if (isNew)
        {
            yield return ChatEvent.Status("מחשב פקודה...");
            try   { command = await _llm.GenerateCommand(intent, userMessage); }
            catch { command = null; }
        }

        // 4. Still no command → fallback to conversation
        if (string.IsNullOrEmpty(command))
        {
            var fallback = await _llm.AskAsync("ענה בעברית בקצרה.", userMessage, 80);
            yield return ChatEvent.Done(fallback, null);
            SaveHistory(userMessage, fallback, null, null);
            yield break;
        }

        // 5. Disruptive commands — send Done first, execute in background
        if (IsDisruptive(command))
        {
            const string disruptMsg = "מבצע. החיבור יינתק לרגע.";
            yield return ChatEvent.Done(disruptMsg, command, "");
            var cmd = command; // capture for lambda
            _ = Task.Run(async () =>
            {
                await Task.Delay(800, CancellationToken.None);
                var psi = new ProcessStartInfo("bash", new[] { "-c", cmd })
                    { UseShellExecute = false };
                Process.Start(psi);
            });
            _ = Task.Run(() => _eventMemory.Save(new MemoryEvent
                { UserMessage = userMessage, Command = cmd, Reply = disruptMsg, Success = true }));
            yield break;
        }

        // 6. Execute — with one C# retry on failure
        yield return ChatEvent.Status("מריץ...");
        var (output, exitCode, finalCmd) = await ExecuteWithRetry(
            command, intent, userMessage, isNew, ct);

        bool success = exitCode == 0;

        // 7. Learn on success
        if (success && isNew && finalCmd == command)
        {
            _commandStore.Save(key, command);
            ArchLogger.LogInfo($"[Chat] Learned: {key} → {command}");
        }
        else if (success && isNew) // retry succeeded with different command
        {
            // Save the alternative that worked, mapped to the same intent
            _commandStore.Save(key, finalCmd);
        }

        // 8. Narrate — collect tokens (yield inside try/catch is illegal in C# iterators)
        var narrateSb     = new StringBuilder();
        var narrateTokens = new List<string>();
        try
        {
            await foreach (var tok in _llm.NarrateStream(finalCmd, output, exitCode, userMessage, ct))
            {
                narrateTokens.Add(tok);
                narrateSb.Append(tok);
            }
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[Chat] NarrateStream failed: {ex.Message}");
            var fallback = exitCode == 0 ? "הפעולה הושלמה בהצלחה." : "הפעולה נכשלה.";
            narrateTokens.Clear();
            narrateTokens.Add(fallback);
            narrateSb.Append(fallback);
        }

        // Stream collected tokens to UI (outside try/catch — yield is legal here)
        foreach (var tok in narrateTokens)
            yield return ChatEvent.Token(tok);

        // 9. Done
        yield return ChatEvent.Done(null, finalCmd, output);

        // 10. Save to history + long-term memory
        SaveHistory(userMessage, narrateSb.ToString(), finalCmd, output);
        _ = Task.Run(() => _eventMemory.Save(new MemoryEvent
        {
            UserMessage = userMessage,
            Command     = finalCmd ?? "none",
            Reply       = narrateSb.ToString(),
            Output      = output ?? "",
            Success     = success
        }));
    }

    // ── Execute with one C# retry ────────────────────────────────────────────

    private async Task<(string output, int exitCode, string finalCmd)> ExecuteWithRetry(
        string command, InterpretResult intent, string userMessage, bool isNew,
        CancellationToken ct)
    {
        // Attempt 1
        var (out1, code1) = await RunCommand(command, ct);
        if (code1 == 0) return (out1, 0, command);

        ArchLogger.LogWarn($"[Chat] Command failed (exit {code1}): {command}");

        // Ask LLM for alternative (one retry only)
        string? alt = null;
        try
        {
            var errIntent = new InterpretResult
            {
                Intent = intent.Intent,
                Slots  = intent.Slots,
                Confidence = 0.7
            };
            var errorContext = $"הפקודה נכשלה:\nפקודה: {command}\n" +
                               $"שגיאה: {out1[..Math.Min(150, out1.Length)]}\n" +
                               $"בקשה: {userMessage}";
            alt = await _llm.GenerateCommand(errIntent, errorContext);
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[Chat] Retry GenerateCommand failed: {ex.Message}");
        }

        if (!string.IsNullOrEmpty(alt) && alt != command)
        {
            ArchLogger.LogInfo($"[Chat] Retry with: {alt}");
            var (out2, code2) = await RunCommand(alt, ct);
            if (code2 == 0) return (out2, 0, alt);
            // Both failed — return combined output with original command
            return ($"{out1}\n---\n{out2}", code2, alt);
        }

        return (out1, code1, command);
    }

    // ── Process runner ───────────────────────────────────────────────────────

    private static async Task<(string output, int exitCode)> RunCommand(
        string command, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("bash", new[] { "-c", command })
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false
            };
            using var proc = Process.Start(psi)!;
            var sb = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cmdCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cmdCts.CancelAfter(TimeSpan.FromSeconds(30));
            await proc.WaitForExitAsync(cmdCts.Token);

            return (sb.ToString().Trim(), proc.ExitCode);
        }
        catch (Exception ex)
        {
            return ($"שגיאה: {ex.Message}", -1);
        }
    }

    // ── Disruptive command detection ─────────────────────────────────────────

    private static bool IsDisruptive(string cmd) =>
        Regex.IsMatch(cmd,
            @"\breboot\b|\bshutdown\b|\bpoweroff\b|\bhalt\b|systemctl\s+restart\s+archimedes",
            RegexOptions.IgnoreCase);

    // ── History management ───────────────────────────────────────────────────

    private void SaveHistory(string userMsg, string reply, string? command, string? output)
    {
        var assistantContent = string.IsNullOrEmpty(command)
            ? reply
            : $"COMMAND: {command}\nRESPONSE: {reply}"
              + (string.IsNullOrEmpty(output) ? "" : $"\nOUTPUT: {output[..Math.Min(80, output.Length)]}");

        lock (_history)
        {
            _history.Add(("user",      userMsg));
            _history.Add(("assistant", assistantContent));
            var maxMessages = MAX_HISTORY * 2;
            while (_history.Count > maxMessages)
                _history.RemoveAt(0);
        }
    }

    public void ClearHistory()
    {
        lock (_history) { _history.Clear(); }
    }
}
