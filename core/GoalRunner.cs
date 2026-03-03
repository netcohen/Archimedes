namespace Archimedes.Core;

/// <summary>
/// Phase 26 — Goal Runner.
///
/// Background loop that periodically advances all active goals.
/// Follows the same Start() pattern as SmartScheduler (no DI required).
///
/// Runs every 30 seconds. For each ACTIVE or MONITORING goal, calls
/// GoalEngine.AdvanceAsync() which handles task status checks and spawning.
///
/// Parallelism: multiple goals advance concurrently because each goal
/// spawns tasks into TaskRunner (which already handles concurrent execution).
/// </summary>
public class GoalRunner
{
    private readonly GoalStore  _store;
    private readonly GoalEngine _engine;

    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);
    private bool _running;
    private CancellationTokenSource? _cts;

    public GoalRunner(GoalStore store, GoalEngine engine)
    {
        _store  = store;
        _engine = engine;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts     = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
        ArchLogger.LogInfo("[GoalRunner] Started (interval=30s)");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _running = false;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try   { await TickAsync(); }
            catch (Exception ex)
            { ArchLogger.LogWarn($"[GoalRunner] Tick error: {ex.Message}"); }

            try   { await Task.Delay(_interval, ct); }
            catch (TaskCanceledException) { break; }
        }
        ArchLogger.LogInfo("[GoalRunner] Stopped");
    }

    private async Task TickAsync()
    {
        var active = _store.GetActive();
        if (active.Count == 0) return;

        ArchLogger.LogInfo($"[GoalRunner] Tick: {active.Count} active goal(s)");

        // Advance all goals concurrently — each spawns into the shared TaskRunner
        var tasks = active.Select(async goal =>
        {
            try   { await _engine.AdvanceAsync(goal); }
            catch (Exception ex)
            { ArchLogger.LogWarn($"[GoalRunner] Advance error goal={goal.GoalId}: {ex.Message}"); }
        });

        await Task.WhenAll(tasks);
    }

    public bool IsRunning      => _running;
    public int  ActiveGoalCount => _store.ActiveCount;
}
