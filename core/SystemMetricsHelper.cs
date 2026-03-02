using System.Diagnostics;

namespace Archimedes.Core;

/// <summary>
/// Phase 22 – Chat UI: System metrics helper.
/// Provides process-level CPU, RAM, and uptime for the /system/metrics endpoint.
/// </summary>
public static class SystemMetricsHelper
{
    private static readonly DateTime StartupTime = DateTime.UtcNow;

    // CPU sampling state
    private static DateTime _cpuLastSample = DateTime.MinValue;
    private static TimeSpan _cpuLastTotal  = TimeSpan.Zero;
    private static double   _cpuLastPct    = 0.0;
    private static readonly object _cpuLock = new();

    /// <summary>
    /// Returns the process CPU usage % since last call (sampled, not instantaneous).
    /// Returns 0 on the first call (no prior sample to compare against).
    /// </summary>
    public static double GetCpuPercent()
    {
        lock (_cpuLock)
        {
            var proc = Process.GetCurrentProcess();
            proc.Refresh();

            var now   = DateTime.UtcNow;
            var total = proc.TotalProcessorTime;

            if (_cpuLastSample == DateTime.MinValue)
            {
                _cpuLastSample = now;
                _cpuLastTotal  = total;
                return 0.0;
            }

            var elapsed = (now - _cpuLastSample).TotalSeconds;
            if (elapsed < 0.5) return _cpuLastPct;   // too soon — return cached value

            var cpuDelta = (total - _cpuLastTotal).TotalSeconds;
            _cpuLastPct    = Math.Round(cpuDelta / elapsed / Environment.ProcessorCount * 100.0, 1);
            _cpuLastSample = now;
            _cpuLastTotal  = total;
            return _cpuLastPct;
        }
    }

    /// <summary>Process working set memory in MB.</summary>
    public static long GetRamUsedMb()
    {
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        return proc.WorkingSet64 / (1024 * 1024);
    }

    /// <summary>Total physical system memory in MB (via GC.GetGCMemoryInfo).</summary>
    public static long GetRamTotalMb()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            return info.TotalAvailableMemoryBytes / (1024 * 1024);
        }
        catch { return 0; }
    }

    /// <summary>Seconds since the process started.</summary>
    public static long GetUptimeSeconds()
        => (long)(DateTime.UtcNow - StartupTime).TotalSeconds;
}
