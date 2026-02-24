using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Archimedes.Core;

/// <summary>
/// Smart scheduler with priority lanes, browser concurrency limit, and resource governor.
/// </summary>
public class SmartScheduler
{
    private readonly TaskService _taskService;
    private readonly Planner _planner;
    private readonly object _lock = new();
    
    // Priority queues
    private readonly ConcurrentQueue<string> _immediateQueue = new();
    private readonly ConcurrentQueue<string> _scheduledQueue = new();
    private readonly ConcurrentQueue<string> _backgroundQueue = new();
    
    // Active tasks tracking
    private readonly ConcurrentDictionary<string, ActiveTask> _activeTasks = new();
    
    // Resource limits
    private int _maxBrowserConcurrency = 1;
    private int _activeBrowserTasks = 0;
    private int _maxCpuPercent = 80;
    private int _maxMemoryMB = 1024;
    
    // Monitoring tasks
    private readonly ConcurrentDictionary<string, MonitoringTask> _monitoringTasks = new();
    
    // Watchdog settings
    private int _watchdogTimeoutSeconds = 300; // 5 minutes default
    
    private bool _running = false;
    private CancellationTokenSource? _cts;
    
    public SmartScheduler(TaskService taskService, Planner planner)
    {
        _taskService = taskService;
        _planner = planner;
    }
    
    /// <summary>
    /// Start the scheduler background worker.
    /// </summary>
    public void Start()
    {
        if (_running) return;
        
        _running = true;
        _cts = new CancellationTokenSource();
        
        Task.Run(async () => await SchedulerLoop(_cts.Token));
        Task.Run(async () => await MonitoringLoop(_cts.Token));
        Task.Run(async () => await WatchdogLoop(_cts.Token));
        
        ArchLogger.LogInfo("[Scheduler] Started");
    }
    
    /// <summary>
    /// Stop the scheduler.
    /// </summary>
    public void Stop()
    {
        if (!_running) return;
        
        _running = false;
        _cts?.Cancel();
        
        ArchLogger.LogInfo("[Scheduler] Stopped");
    }
    
    /// <summary>
    /// Enqueue a task for execution.
    /// </summary>
    public void Enqueue(string taskId, TaskPriority priority = TaskPriority.IMMEDIATE)
    {
        switch (priority)
        {
            case TaskPriority.IMMEDIATE:
                _immediateQueue.Enqueue(taskId);
                break;
            case TaskPriority.SCHEDULED:
                _scheduledQueue.Enqueue(taskId);
                break;
            case TaskPriority.BACKGROUND:
                _backgroundQueue.Enqueue(taskId);
                break;
        }
        
        ArchLogger.LogInfo($"[Scheduler] Enqueued task {taskId} with priority {priority}");
    }
    
    /// <summary>
    /// Register a monitoring task with interval and backoff.
    /// </summary>
    public void RegisterMonitoring(string taskId, int intervalMs, int maxJitterMs = 5000, double backoffMultiplier = 1.5)
    {
        var monitoring = new MonitoringTask
        {
            TaskId = taskId,
            IntervalMs = intervalMs,
            CurrentIntervalMs = intervalMs,
            MaxJitterMs = maxJitterMs,
            BackoffMultiplier = backoffMultiplier,
            NextRunAt = DateTime.UtcNow.AddMilliseconds(intervalMs)
        };
        
        _monitoringTasks[taskId] = monitoring;
        ArchLogger.LogInfo($"[Scheduler] Registered monitoring task {taskId} with interval {intervalMs}ms");
    }
    
    /// <summary>
    /// Unregister a monitoring task.
    /// </summary>
    public void UnregisterMonitoring(string taskId)
    {
        _monitoringTasks.TryRemove(taskId, out _);
        ArchLogger.LogInfo($"[Scheduler] Unregistered monitoring task {taskId}");
    }
    
    /// <summary>
    /// Check resource availability for executing tasks.
    /// </summary>
    public ResourceAvailability CheckAvailability()
    {
        var browserAvailable = _activeBrowserTasks < _maxBrowserConcurrency;
        var activeTaskCount = _activeTasks.Count;
        
        // Simple CPU/memory check (placeholder - real implementation would use Performance Counters)
        var cpuOk = true; // Process.GetCurrentProcess().TotalProcessorTime would be more accurate
        var memoryOk = GC.GetTotalMemory(false) / 1024 / 1024 < _maxMemoryMB;
        
        return new ResourceAvailability
        {
            BrowserSlotsAvailable = _maxBrowserConcurrency - _activeBrowserTasks,
            BrowserSlotsTotal = _maxBrowserConcurrency,
            ActiveTaskCount = activeTaskCount,
            ImmediateQueueSize = _immediateQueue.Count,
            ScheduledQueueSize = _scheduledQueue.Count,
            BackgroundQueueSize = _backgroundQueue.Count,
            CpuAvailable = cpuOk,
            MemoryAvailable = memoryOk,
            CanAcceptTasks = browserAvailable && cpuOk && memoryOk
        };
    }
    
    /// <summary>
    /// Get scheduler statistics.
    /// </summary>
    public SchedulerStats GetStats()
    {
        return new SchedulerStats
        {
            Running = _running,
            ActiveTasks = _activeTasks.Count,
            ImmediateQueueSize = _immediateQueue.Count,
            ScheduledQueueSize = _scheduledQueue.Count,
            BackgroundQueueSize = _backgroundQueue.Count,
            MonitoringTasks = _monitoringTasks.Count,
            BrowserConcurrency = _activeBrowserTasks,
            MaxBrowserConcurrency = _maxBrowserConcurrency
        };
    }
    
    /// <summary>
    /// Configure resource limits.
    /// </summary>
    public void ConfigureLimits(int? maxBrowserConcurrency = null, int? maxCpuPercent = null, int? maxMemoryMB = null, int? watchdogTimeoutSeconds = null)
    {
        if (maxBrowserConcurrency.HasValue)
            _maxBrowserConcurrency = maxBrowserConcurrency.Value;
        if (maxCpuPercent.HasValue)
            _maxCpuPercent = maxCpuPercent.Value;
        if (maxMemoryMB.HasValue)
            _maxMemoryMB = maxMemoryMB.Value;
        if (watchdogTimeoutSeconds.HasValue)
            _watchdogTimeoutSeconds = watchdogTimeoutSeconds.Value;
        
        ArchLogger.LogInfo($"[Scheduler] Configured limits: browser={_maxBrowserConcurrency}, cpu={_maxCpuPercent}%, memory={_maxMemoryMB}MB, watchdog={_watchdogTimeoutSeconds}s");
    }
    
    private async Task SchedulerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Check resource availability
                var availability = CheckAvailability();
                
                if (availability.CanAcceptTasks)
                {
                    // Priority order: IMMEDIATE > SCHEDULED > BACKGROUND
                    string? taskId = null;
                    
                    if (_immediateQueue.TryDequeue(out taskId))
                    {
                        await ExecuteTask(taskId, "IMMEDIATE");
                    }
                    else if (_scheduledQueue.TryDequeue(out taskId))
                    {
                        await ExecuteTask(taskId, "SCHEDULED");
                    }
                    else if (_backgroundQueue.TryDequeue(out taskId))
                    {
                        await ExecuteTask(taskId, "BACKGROUND");
                    }
                }
                
                await Task.Delay(500, ct); // Check every 500ms
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ArchLogger.LogError($"[Scheduler] Error in scheduler loop: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }
    
    private async Task MonitoringLoop(CancellationToken ct)
    {
        var random = new Random();
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                
                foreach (var kvp in _monitoringTasks)
                {
                    var monitoring = kvp.Value;
                    
                    if (now >= monitoring.NextRunAt)
                    {
                        // Add jitter
                        var jitter = random.Next(0, monitoring.MaxJitterMs);
                        
                        // Enqueue as SCHEDULED
                        _scheduledQueue.Enqueue(monitoring.TaskId);
                        
                        // Calculate next run time
                        monitoring.NextRunAt = now.AddMilliseconds(monitoring.CurrentIntervalMs + jitter);
                        
                        ArchLogger.LogInfo($"[Scheduler] Triggered monitoring task {monitoring.TaskId}, next run in {monitoring.CurrentIntervalMs + jitter}ms");
                    }
                }
                
                await Task.Delay(1000, ct); // Check every second
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ArchLogger.LogError($"[Scheduler] Error in monitoring loop: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }
    
    private async Task WatchdogLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var stuckTasks = new List<string>();
                
                // Check for stuck active tasks
                foreach (var kvp in _activeTasks)
                {
                    var activeTask = kvp.Value;
                    var elapsed = (now - activeTask.StartedAt).TotalSeconds;
                    
                    if (elapsed > _watchdogTimeoutSeconds)
                    {
                        stuckTasks.Add(kvp.Key);
                    }
                }
                
                // Also check for RUNNING tasks in the store that are not in _activeTasks
                var runningTasks = _taskService.GetTasks(TaskState.RUNNING);
                foreach (var task in runningTasks)
                {
                    // Skip if actively being processed
                    if (_activeTasks.ContainsKey(task.TaskId)) continue;
                    
                    // Skip if waiting for approval
                    if (!string.IsNullOrEmpty(task.WaitingForApprovalId)) continue;
                    
                    var elapsed = (now - (task.UpdatedAtUtc ?? task.CreatedAtUtc)).TotalSeconds;
                    
                    if (elapsed > _watchdogTimeoutSeconds)
                    {
                        stuckTasks.Add(task.TaskId);
                    }
                }
                
                // Fail stuck tasks
                foreach (var taskId in stuckTasks.Distinct())
                {
                    ArchLogger.LogWarn($"[Watchdog] Task {taskId} stuck for >{_watchdogTimeoutSeconds}s, marking as FAILED");
                    _taskService.Fail(taskId, $"WatchdogTimeout: No progress for {_watchdogTimeoutSeconds}s");
                    _activeTasks.TryRemove(taskId, out _);
                }
                
                await Task.Delay(30000, ct); // Check every 30 seconds
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ArchLogger.LogError($"[Watchdog] Error: {ex.Message}");
                await Task.Delay(30000, ct);
            }
        }
    }
    
    private async Task ExecuteTask(string taskId, string lane)
    {
        var task = _taskService.GetTask(taskId);
        if (task == null)
        {
            ArchLogger.LogWarn($"[Scheduler] Task {taskId} not found");
            return;
        }
        
        // Track active task
        var activeTask = new ActiveTask
        {
            TaskId = taskId,
            StartedAt = DateTime.UtcNow,
            Lane = lane,
            UsesBrowser = HasBrowserSteps(task)
        };
        
        if (activeTask.UsesBrowser)
        {
            lock (_lock)
            {
                if (_activeBrowserTasks >= _maxBrowserConcurrency)
                {
                    // Re-queue task
                    Enqueue(taskId, task.Priority);
                    ArchLogger.LogInfo($"[Scheduler] Browser busy, re-queued task {taskId}");
                    return;
                }
                _activeBrowserTasks++;
            }
        }
        
        _activeTasks[taskId] = activeTask;
        
        try
        {
            ArchLogger.LogInfo($"[Scheduler] Executing task {taskId} in {lane} lane");
            
            // Start the task
            _taskService.StartRun(taskId);
            
            // Execute plan steps (simplified - real implementation would call browser worker)
            var plan = _taskService.GetPlan(taskId);
            if (plan != null)
            {
                for (int i = 0; i < plan.Steps.Count; i++)
                {
                    var step = plan.Steps[i];
                    ArchLogger.LogInfo($"[Scheduler] Task {taskId} step {i + 1}/{plan.Steps.Count}: {step.Action}");
                    
                    // Simulate step execution (real implementation would dispatch to browser worker)
                    await Task.Delay(100); // Placeholder
                    
                    _taskService.UpdateStep(taskId, i + 1);
                }
            }
            
            _taskService.Complete(taskId, "Task completed successfully");
            ArchLogger.LogInfo($"[Scheduler] Task {taskId} completed");
            
            // Handle monitoring backoff on success
            if (_monitoringTasks.TryGetValue(taskId, out var monitoring))
            {
                // Reset to base interval on success
                monitoring.CurrentIntervalMs = monitoring.IntervalMs;
            }
        }
        catch (Exception ex)
        {
            ArchLogger.LogError($"[Scheduler] Task {taskId} failed: {ex.Message}");
            _taskService.Fail(taskId, ex.Message);
            
            // Handle monitoring backoff on failure
            if (_monitoringTasks.TryGetValue(taskId, out var monitoring))
            {
                monitoring.CurrentIntervalMs = (int)(monitoring.CurrentIntervalMs * monitoring.BackoffMultiplier);
                monitoring.FailureCount++;
                ArchLogger.LogInfo($"[Scheduler] Monitoring task {taskId} backed off to {monitoring.CurrentIntervalMs}ms");
            }
        }
        finally
        {
            _activeTasks.TryRemove(taskId, out _);
            
            if (activeTask.UsesBrowser)
            {
                lock (_lock)
                {
                    _activeBrowserTasks--;
                }
            }
        }
    }
    
    private bool HasBrowserSteps(AgentTask task)
    {
        var plan = _taskService.GetPlan(task.TaskId);
        if (plan == null) return false;
        
        return plan.Steps.Any(s => s.Action.StartsWith("browser."));
    }
}

public class ActiveTask
{
    public string TaskId { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public string Lane { get; set; } = "";
    public bool UsesBrowser { get; set; }
}

public class MonitoringTask
{
    public string TaskId { get; set; } = "";
    public int IntervalMs { get; set; }
    public int CurrentIntervalMs { get; set; }
    public int MaxJitterMs { get; set; }
    public double BackoffMultiplier { get; set; }
    public DateTime NextRunAt { get; set; }
    public int FailureCount { get; set; }
}

public class ResourceAvailability
{
    public int BrowserSlotsAvailable { get; set; }
    public int BrowserSlotsTotal { get; set; }
    public int ActiveTaskCount { get; set; }
    public int ImmediateQueueSize { get; set; }
    public int ScheduledQueueSize { get; set; }
    public int BackgroundQueueSize { get; set; }
    public bool CpuAvailable { get; set; }
    public bool MemoryAvailable { get; set; }
    public bool CanAcceptTasks { get; set; }
}

public class SchedulerStats
{
    public bool Running { get; set; }
    public int ActiveTasks { get; set; }
    public int ImmediateQueueSize { get; set; }
    public int ScheduledQueueSize { get; set; }
    public int BackgroundQueueSize { get; set; }
    public int MonitoringTasks { get; set; }
    public int BrowserConcurrency { get; set; }
    public int MaxBrowserConcurrency { get; set; }
}

public class SchedulerConfigRequest
{
    public int? MaxBrowserConcurrency { get; set; }
    public int? MaxCpuPercent { get; set; }
    public int? MaxMemoryMB { get; set; }
    public int? WatchdogTimeoutSeconds { get; set; }
}

public class MonitoringRequest
{
    public string TaskId { get; set; } = "";
    public int IntervalMs { get; set; } = 30000;
    public int? MaxJitterMs { get; set; }
    public double? BackoffMultiplier { get; set; }
}
