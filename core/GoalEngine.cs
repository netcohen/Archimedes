using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 26 — Goal Engine.
///
/// Manages goal lifecycle: creation, evaluation, task spawning, and adaptive replanning.
///
/// Key rules (from Phase 25 authorization model):
///   - Only THIRD_PARTY_MESSAGE actions require availability check.
///   - All other actions run autonomously.
///   - Critical goals always proceed.
///
/// Adaptive replanning algorithm:
///   Task fails → try 2 alternatives (enriched prompts) → if still failing →
///   wait + retry (up to MaxRetries) → escalate to FailureDialogue → FAILED.
/// </summary>
public class GoalEngine
{
    private readonly GoalStore            _store;
    private readonly TaskService          _taskService;
    private readonly LLMAdapter           _llmAdapter;
    private readonly AvailabilityEngine   _availability;
    private readonly FailureDialogueStore _dialogueStore;

    public GoalEngine(
        GoalStore store, TaskService taskService, LLMAdapter llmAdapter,
        AvailabilityEngine availability, FailureDialogueStore dialogueStore)
    {
        _store         = store;
        _taskService   = taskService;
        _llmAdapter    = llmAdapter;
        _availability  = availability;
        _dialogueStore = dialogueStore;
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Goal> CreateAsync(CreateGoalRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.UserPrompt))
            throw new ArgumentException("UserPrompt required");

        var llm    = await _llmAdapter.Interpret(req.UserPrompt);
        var intent = llm.Intent ?? "UNKNOWN";

        var goalType = req.Type?.ToUpperInvariant() switch
        {
            "CONDITION" => GoalType.CONDITION,
            "ONE_TIME"  => GoalType.ONE_TIME,
            _           => GoalType.PERSISTENT
        };

        var title = !string.IsNullOrEmpty(req.Title)
            ? req.Title
            : (req.UserPrompt.Length > 60 ? req.UserPrompt[..60] + "\u2026" : req.UserPrompt);

        var goal = new Goal
        {
            Title                = title,
            Description          = req.Description ?? req.UserPrompt,
            Type                 = goalType,
            Intent               = intent,
            SuccessCondition     = req.SuccessCondition,
            MaxRetries           = req.MaxRetries ?? 3,
            CheckIntervalMinutes = req.CheckIntervalMinutes ?? 30,
        };
        goal.Parameters["userPrompt"] = req.UserPrompt;

        _store.Add(goal);
        ArchLogger.LogInfo($"[GoalEngine] Created goal={goal.GoalId} intent={intent} type={goalType}");

        // Immediately try to advance (spawn first task)
        await AdvanceAsync(goal);
        return goal;
    }

    // ── Evaluate ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks if the goal is achieved and determines the next action.
    /// Returns (isAchieved, reason, nextAction).
    /// </summary>
    public (bool isAchieved, string reason, string nextAction) Evaluate(Goal goal)
    {
        // No success condition = run forever (PERSISTENT)
        if (string.IsNullOrWhiteSpace(goal.SuccessCondition))
            return (false, "no_condition", NextAction(goal));

        var last = goal.Memory.History.LastOrDefault();

        // ONE_TIME: completed as soon as a task succeeds
        if (goal.Type == GoalType.ONE_TIME && last?.Success == true)
            return (true, "one_time_success", "complete");

        // CONDITION: completed when last task succeeded
        if (goal.Type == GoalType.CONDITION && last?.Success == true)
            return (true, "condition_met", "complete");

        return (false, "in_progress", NextAction(goal));
    }

    private string NextAction(Goal goal) => goal.State switch
    {
        GoalState.ACTIVE     => "advance_task",
        GoalState.MONITORING => goal.NextCheckUtc <= DateTime.UtcNow ? "advance_task" : "wait",
        _                    => "wait"
    };

    // ── Advance ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by GoalRunner every tick for each active goal.
    /// Checks current task status, records outcomes, spawns next task if needed.
    /// </summary>
    public async Task AdvanceAsync(Goal goal)
    {
        // Check if we have a running task
        if (!string.IsNullOrEmpty(goal.CurrentTaskId))
        {
            var current = _taskService.GetTask(goal.CurrentTaskId);
            if (current != null)
            {
                var stillActive = current.State is
                    TaskState.RUNNING or TaskState.QUEUED or TaskState.PLANNING or
                    TaskState.WAITING_APPROVAL or TaskState.WAITING_SECRET or TaskState.WAITING_CAPTCHA;

                if (stillActive) return;  // task still in flight — nothing to do

                // Task finished — record in goal memory
                bool success = current.State == TaskState.DONE;
                goal.Memory.Record(current.TaskId, success,
                    current.ResultSummary ?? current.Error);
                goal.CurrentTaskId = null;

                if (!success)
                {
                    await HandleFailureAsync(goal, current);
                    return;
                }

                // Success: reset retry/alternative counters
                goal.RetryCount          = 0;
                goal.AlternativeAttempts = 0;
            }
            else
            {
                // Task disappeared from store — clear reference
                goal.CurrentTaskId = null;
            }
        }

        // Evaluate: is the goal done?
        var (isAchieved, reason, nextAction) = Evaluate(goal);
        if (isAchieved)
        {
            goal.State          = GoalState.COMPLETED;
            goal.CompletedAtUtc = DateTime.UtcNow;
            SaveCheckpoint(goal, "completed");
            _store.Update(goal);
            ArchLogger.LogInfo($"[GoalEngine] Goal={goal.GoalId} COMPLETED reason={reason}");
            return;
        }

        if (nextAction == "wait") { _store.Update(goal); return; }

        await SpawnTaskAsync(goal);
    }

    // ── Spawn task ────────────────────────────────────────────────────────────

    private async Task SpawnTaskAsync(Goal goal)
    {
        var prompt = goal.Parameters.GetValueOrDefault("userPrompt", goal.Description);

        // Enrich prompt with accumulated memory context
        if (!string.IsNullOrEmpty(goal.Memory.LastObservedValue))
            prompt += $" [last observed: {goal.Memory.LastObservedValue}]";

        var titleRaw  = $"[Goal] {goal.Title}";
        var taskTitle = titleRaw.Length > 80 ? titleRaw[..79] : titleRaw;

        var task = _taskService.CreateTask(new CreateTaskRequest
        {
            Title      = taskTitle,
            UserPrompt = prompt
        });

        goal.TaskIds.Add(task.TaskId);
        goal.CurrentTaskId = task.TaskId;

        // For MONITORING goals: schedule next check window
        if (goal.Type == GoalType.PERSISTENT || goal.State == GoalState.MONITORING)
            goal.NextCheckUtc = DateTime.UtcNow.AddMinutes(goal.CheckIntervalMinutes);

        _taskService.StartRun(task.TaskId);
        _store.Update(goal);

        ArchLogger.LogInfo($"[GoalEngine] Goal={goal.GoalId} spawned task={task.TaskId}");
        await Task.CompletedTask;
    }

    // ── Adaptive replanning ───────────────────────────────────────────────────

    /// <summary>
    /// Called when a task belonging to a goal has failed.
    /// Tries up to 2 alternative prompts before incrementing the retry counter.
    /// After MaxRetries, escalates to FailureDialogue and marks the goal FAILED.
    /// </summary>
    private async Task HandleFailureAsync(Goal goal, AgentTask failedTask)
    {
        goal.AlternativeAttempts++;
        ArchLogger.LogWarn($"[GoalEngine] Goal={goal.GoalId} task={failedTask.TaskId} failed " +
                           $"(alt={goal.AlternativeAttempts} retry={goal.RetryCount}/{goal.MaxRetries})");

        // ── Alternative 1 & 2: enrich prompt with failure context ─────────────
        if (goal.AlternativeAttempts <= 2)
        {
            var basePrompt = goal.Parameters.GetValueOrDefault("userPrompt", goal.Description);
            var errHint    = failedTask.Error?.Split('\n')[0] ?? "error";
            goal.Parameters["userPrompt"] = basePrompt + $" [previous attempt failed: {errHint}]";
            _store.Update(goal);
            await SpawnTaskAsync(goal);
            return;
        }

        // ── All alternatives for this cycle exhausted → count as one retry ────
        goal.RetryCount++;
        goal.AlternativeAttempts = 0;

        if (goal.RetryCount >= goal.MaxRetries)
        {
            // Escalate: create a FailureDialogue so user sees it in chat
            _dialogueStore.Create(
                taskId:       failedTask.TaskId,
                taskTitle:    goal.Title,
                failedStep:   $"goal_retry_{goal.RetryCount}",
                failedAction: goal.Intent,
                errorMessage: $"המטרה נכשלה {goal.RetryCount} פעמים. " +
                              (failedTask.Error?.Split('\n')[0] ?? ""));

            goal.State         = GoalState.FAILED;
            goal.FailureReason = $"exhausted {goal.RetryCount} retries";
            goal.CurrentTaskId = null;
            SaveCheckpoint(goal, "failed");
            _store.Update(goal);
            ArchLogger.LogWarn($"[GoalEngine] Goal={goal.GoalId} FAILED");
            return;
        }

        // ── Still have retries: enter MONITORING with 5-minute back-off ───────
        goal.State        = GoalState.MONITORING;
        goal.NextCheckUtc = DateTime.UtcNow.AddMinutes(5);
        goal.CurrentTaskId = null;
        _store.Update(goal);
        ArchLogger.LogInfo($"[GoalEngine] Goal={goal.GoalId} backing off 5 min before retry {goal.RetryCount + 1}");
    }

    // ── State transitions ────────────────────────────────────────────────────

    public Goal? Pause(string id)
    {
        var g = _store.GetById(id);
        if (g == null || g.State is GoalState.COMPLETED or GoalState.FAILED) return null;
        g.State = GoalState.IDLE;
        _store.Update(g);
        ArchLogger.LogInfo($"[GoalEngine] Goal={id} paused");
        return g;
    }

    public Goal? Resume(string id)
    {
        var g = _store.GetById(id);
        if (g == null || g.State != GoalState.IDLE) return null;
        g.State              = GoalState.ACTIVE;
        g.RetryCount         = 0;
        g.AlternativeAttempts = 0;
        _store.Update(g);
        ArchLogger.LogInfo($"[GoalEngine] Goal={id} resumed");
        return g;
    }

    // ── Checkpoint ───────────────────────────────────────────────────────────

    private void SaveCheckpoint(Goal goal, string hint)
    {
        goal.LastCheckpoint = new GoalCheckpoint
        {
            CreatedAtUtc  = DateTime.UtcNow,
            StateSnapshot = JsonSerializer.Serialize(new
            {
                goal.State,
                goal.RetryCount,
                goal.Memory.TotalRuns,
                goal.Memory.LastObservedValue,
                goal.CurrentTaskId
            }),
            LastTaskId = goal.CurrentTaskId,
            ResumeHint = hint
        };
    }
}
