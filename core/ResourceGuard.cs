namespace Archimedes.Core;

/// <summary>
/// Phase 29 – Resource Guard.
///
/// Monitors CPU and RAM usage and signals the self-improvement engine to
/// throttle or pause, protecting hardware from sustained high load.
///
/// Thresholds:
///   CPU avg > 70% over 30 s  → Throttle  (slow down inter-task delay)
///   CPU instant > 90%         → Pause     (stop until CPU drops)
///   After throttle/pause, CPU avg < 50% → Resume
///
/// Uses SystemMetricsHelper which already exists (Phase 22).
/// </summary>
public sealed class ResourceGuard : IDisposable
{
    // ── Thresholds ────────────────────────────────────────────────────────
    private const double CpuThrottlePercent = 70.0;
    private const double CpuPausePercent    = 90.0;
    private const double CpuResumePercent   = 50.0;
    private const int    SampleWindowCount  = 6;       // 6 × 5 s = 30 s window
    private const int    SampleIntervalMs   = 5_000;

    // ── State ─────────────────────────────────────────────────────────────
    private readonly Queue<double> _cpuSamples = new();
    private readonly object        _lock        = new();
    private Timer?                 _timer;
    private bool                   _disposed;

    public bool   ShouldThrottle  { get; private set; }
    public bool   ShouldPause     { get; private set; }
    public string ThrottleReason  { get; private set; } = "";
    public double LastCpuPercent  { get; private set; }
    public double LastRamUsedMb   { get; private set; }

    // ── Events ────────────────────────────────────────────────────────────
    /// <summary>Fired when average CPU crosses the throttle threshold.</summary>
    public event Action? OnThrottle;
    /// <summary>Fired when instantaneous CPU crosses the pause threshold.</summary>
    public event Action? OnPause;
    /// <summary>Fired when CPU drops back to safe levels.</summary>
    public event Action? OnResume;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public void Start()
    {
        _timer = new Timer(Sample, null, 0, SampleIntervalMs);
        ArchLogger.LogInfo("[ResourceGuard] Started (throttle=70% pause=90% resume=50%)");
    }

    private void Sample(object? _)
    {
        var cpu = SystemMetricsHelper.GetCpuPercent();
        var ram = SystemMetricsHelper.GetRamUsedMb();
        double avg;

        lock (_lock)
        {
            _cpuSamples.Enqueue(cpu);
            while (_cpuSamples.Count > SampleWindowCount) _cpuSamples.Dequeue();
            avg = _cpuSamples.Average();
        }

        LastCpuPercent = cpu;
        LastRamUsedMb  = ram;

        var wasThrottled = ShouldThrottle;
        var wasPaused    = ShouldPause;

        if (cpu >= CpuPausePercent)
        {
            ShouldPause    = true;
            ShouldThrottle = true;
            ThrottleReason = $"CPU {cpu:F0}% ≥ {CpuPausePercent}% — עצר מיד";
            if (!wasPaused) OnPause?.Invoke();
        }
        else if (avg >= CpuThrottlePercent)
        {
            ShouldPause    = false;
            ShouldThrottle = true;
            ThrottleReason = $"Avg CPU {avg:F0}% ≥ {CpuThrottlePercent}% — מאט";
            if (!wasThrottled || wasPaused) OnThrottle?.Invoke();
        }
        else if (avg <= CpuResumePercent && (wasThrottled || wasPaused))
        {
            ShouldThrottle = false;
            ShouldPause    = false;
            ThrottleReason = "";
            OnResume?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
    }
}
