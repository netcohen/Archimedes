using System.Collections.Concurrent;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 24 — Failure Dialogue.
///
/// When a task step fails, instead of silently dying, a FailureDialogue is created.
/// The Chat UI polls GET /recovery-dialogues and surfaces it to the user with
/// three action buttons: נסה שוב (retry) | ספק מידע (provide info) | בטל (dismiss).
/// </summary>

// ── Enums ──────────────────────────────────────────────────────────────────────

public enum DialogueStatus
{
    PENDING,     // Awaiting user response
    ANSWERED,    // User responded (retry or info provided)
    DISMISSED    // User chose to dismiss
}

// ── Model ──────────────────────────────────────────────────────────────────────

public class FailureDialogue
{
    public string DialogueId       { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string TaskId           { get; set; } = "";
    public string TaskTitle        { get; set; } = "";
    public string FailedStep       { get; set; } = "";
    public string FailedAction     { get; set; } = "";
    public string ErrorMessage     { get; set; } = "";
    public string RecoveryQuestion { get; set; } = "";
    public DialogueStatus Status   { get; set; } = DialogueStatus.PENDING;
    public DateTime CreatedAtUtc   { get; set; } = DateTime.UtcNow;
    public DateTime? AnsweredAtUtc { get; set; }
    public string? UserResponse    { get; set; }
    public string? RecoveryAction  { get; set; }  // "retry" | "info" | "dismiss"
}

// ── Store ──────────────────────────────────────────────────────────────────────

public class FailureDialogueStore
{
    private readonly ConcurrentDictionary<string, FailureDialogue> _dialogues = new();
    private readonly string _storeDir;
    private readonly object _ioLock = new();

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { WriteIndented = true };

    public FailureDialogueStore()
    {
        _storeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Archimedes", "dialogues");

        try { Directory.CreateDirectory(_storeDir); } catch { }
        LoadFromDisk();
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Create and persist a new failure dialogue for a failed task step.</summary>
    public FailureDialogue Create(
        string taskId, string taskTitle,
        string failedStep, string failedAction, string errorMessage)
    {
        var question = FailureAnalyzer.Analyze(failedStep, failedAction, errorMessage);

        var dialogue = new FailureDialogue
        {
            TaskId           = taskId,
            TaskTitle        = taskTitle,
            FailedStep       = failedStep,
            FailedAction     = failedAction,
            ErrorMessage     = errorMessage,
            RecoveryQuestion = question
        };

        _dialogues[dialogue.DialogueId] = dialogue;
        PersistAsync(dialogue);

        ArchLogger.LogInfo(
            $"[FailureDialogue] Created {dialogue.DialogueId} task={taskId}: {question}");

        return dialogue;
    }

    public FailureDialogue? Get(string id) =>
        _dialogues.TryGetValue(id, out var d) ? d : null;

    /// <summary>All PENDING dialogues, newest first.</summary>
    public List<FailureDialogue> GetPending() =>
        _dialogues.Values
            .Where(d => d.Status == DialogueStatus.PENDING)
            .OrderByDescending(d => d.CreatedAtUtc)
            .ToList();

    /// <summary>All dialogues (any status), newest first.</summary>
    public List<FailureDialogue> GetAll() =>
        _dialogues.Values
            .OrderByDescending(d => d.CreatedAtUtc)
            .ToList();

    /// <summary>
    /// Record a user response to a pending dialogue.
    /// Returns false if the dialogue doesn't exist or is not PENDING.
    /// </summary>
    public bool Respond(string id, string action, string? userResponse = null)
    {
        if (!_dialogues.TryGetValue(id, out var d)) return false;
        if (d.Status != DialogueStatus.PENDING) return false;

        d.Status         = action == "dismiss" ? DialogueStatus.DISMISSED : DialogueStatus.ANSWERED;
        d.RecoveryAction = action;
        d.UserResponse   = userResponse;
        d.AnsweredAtUtc  = DateTime.UtcNow;

        PersistAsync(d);
        ArchLogger.LogInfo(
            $"[FailureDialogue] Answered {id} action={action}");

        return true;
    }

    public int PendingCount =>
        _dialogues.Values.Count(d => d.Status == DialogueStatus.PENDING);

    // ── Disk persistence (same pattern as ProcedureStore) ─────────────────────

    private string FilePath(string id) =>
        Path.Combine(_storeDir, $"{id}.json");

    private void PersistAsync(FailureDialogue d)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!IsValidId(d.DialogueId)) return;
                lock (_ioLock)
                {
                    File.WriteAllText(
                        FilePath(d.DialogueId),
                        JsonSerializer.Serialize(d, _jsonOpts));
                }
            }
            catch { /* best-effort */ }
        });
    }

    private void LoadFromDisk()
    {
        try
        {
            int loaded = 0;
            foreach (var file in Directory.GetFiles(_storeDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var d    = JsonSerializer.Deserialize<FailureDialogue>(json);
                    if (d != null)
                    {
                        _dialogues[d.DialogueId] = d;
                        loaded++;
                    }
                }
                catch { }
            }
            if (loaded > 0)
                ArchLogger.LogInfo($"[FailureDialogue] Loaded {loaded} dialogues from disk");
        }
        catch { }
    }

    private static bool IsValidId(string id) =>
        System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9\-_]+$");
}
