namespace Archimedes.Core;

/// <summary>
/// Phase 28 — Pauses all in-flight tasks before packaging for migration.
///
/// Strategy:
///   RUNNING  + IMMEDIATE priority → brief grace period (5 s) then Pause
///   RUNNING  + other priority     → Pause immediately
///   QUEUED / PLANNING             → already safe for DB snapshot; record as SUSPEND
///   PAUSED / WAITING_*            → already safe; record as SUSPEND
///   DONE / FAILED                 → skip (nothing to resume)
/// </summary>
public class TaskSuspender
{
    private readonly TaskService _taskService;
    private const int GracePeriodMs = 5_000;

    public TaskSuspender(TaskService taskService) => _taskService = taskService;

    // ── Main entry ─────────────────────────────────────────────────────────

    /// <summary>
    /// Suspends in-flight tasks and returns a migration decision per task.
    /// </summary>
    public async Task<List<TaskMigrationDecision>> SuspendAllAsync(
        CancellationToken ct = default)
    {
        ArchLogger.LogInfo("[TaskSuspender] Suspending tasks for migration...");

        var decisions = new List<TaskMigrationDecision>();

        var candidates = _taskService
            .GetTasks()
            .Where(t => t.State is not (TaskState.DONE or TaskState.FAILED))
            .ToList();

        foreach (var task in candidates)
        {
            if (ct.IsCancellationRequested) break;

            var decision = new TaskMigrationDecision
            {
                TaskId      = task.TaskId,
                Title       = task.Title,
                StateBefore = task.State,
                StepBefore  = task.CurrentStep,
                Priority    = task.Priority
            };

            if (task.State == TaskState.RUNNING)
            {
                // Give IMMEDIATE tasks a short window to reach a checkpoint
                if (task.Priority == TaskPriority.IMMEDIATE)
                {
                    ArchLogger.LogInfo(
                        $"[TaskSuspender] Grace period for IMMEDIATE task {task.TaskId}...");
                    // swallow cancellation — we still want to pause even if ct fires
                    await Task.Delay(GracePeriodMs, ct)
                              .ContinueWith(_ => { });

                    // Re-read: task may have finished during grace period
                    var refreshed = _taskService.GetTask(task.TaskId);
                    if (refreshed?.State is TaskState.DONE or TaskState.FAILED)
                        continue; // Completed on its own — nothing to migrate
                }

                try
                {
                    _taskService.Pause(task.TaskId);
                    decision.Action = TaskMigrationAction.SUSPEND;
                    ArchLogger.LogInfo(
                        $"[TaskSuspender] Paused task {task.TaskId} " +
                        $"title=\"{task.Title}\" step={task.CurrentStep}");
                }
                catch (Exception ex)
                {
                    decision.Action = TaskMigrationAction.ABANDON;
                    ArchLogger.LogWarn(
                        $"[TaskSuspender] Could not pause {task.TaskId}: {ex.Message}");
                }
            }
            else
            {
                // QUEUED / PLANNING / PAUSED / WAITING_* — already at a stable state
                decision.Action = TaskMigrationAction.SUSPEND;
            }

            decisions.Add(decision);
        }

        int suspended = decisions.Count(d => d.Action == TaskMigrationAction.SUSPEND);
        int abandoned = decisions.Count(d => d.Action == TaskMigrationAction.ABANDON);
        ArchLogger.LogInfo(
            $"[TaskSuspender] Suspension done: " +
            $"suspended={suspended} abandoned={abandoned}");

        return decisions;
    }
}
