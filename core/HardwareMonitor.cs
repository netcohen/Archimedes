namespace Archimedes.Core;

/// <summary>
/// Reads hardware metrics: CPU temperature, RAM, disk usage.
/// Linux: reads from /sys and /proc. Windows: graceful fallback.
/// </summary>
public sealed class HardwareMonitor
{
    public double GetCpuTemperature()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        try
        {
            // Enumerate all thermal zones and return the maximum temperature
            var zoneDir = "/sys/class/thermal";
            if (!Directory.Exists(zoneDir)) return 0;

            var temps = Directory.GetDirectories(zoneDir, "thermal_zone*")
                .Select(zone =>
                {
                    var tempFile = Path.Combine(zone, "temp");
                    if (!File.Exists(tempFile)) return 0.0;
                    var raw = File.ReadAllText(tempFile).Trim();
                    return int.TryParse(raw, out var milliC) ? milliC / 1000.0 : 0.0;
                })
                .Where(t => t > 0)
                .ToList();

            return temps.Count > 0 ? temps.Max() : 0;
        }
        catch { return 0; }
    }

    public HardwareMetrics Collect()
    {
        var m = new HardwareMetrics
        {
            CpuTempCelsius = GetCpuTemperature(),
            SampledAt      = DateTime.UtcNow
        };
        CollectRam(m);
        CollectDisks(m);
        return m;
    }

    private static void CollectRam(HardwareMetrics m)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                long totalKb = 0, availableKb = 0;
                foreach (var line in File.ReadAllLines("/proc/meminfo"))
                {
                    var parts = line.Split(':', StringSplitOptions.TrimEntries);
                    if (parts.Length < 2) continue;
                    var valueKb = parts[1].Replace("kB", "").Trim();
                    if (parts[0] == "MemTotal"     && long.TryParse(valueKb, out var t)) totalKb     = t;
                    if (parts[0] == "MemAvailable" && long.TryParse(valueKb, out var a)) availableKb = a;
                }
                m.RamTotalMb     = totalKb / 1024.0;
                m.RamUsedMb      = (totalKb - availableKb) / 1024.0;
                m.RamUsedPercent = totalKb > 0 ? (totalKb - availableKb) * 100.0 / totalKb : 0;
            }
            catch { /* fallback below */ }
        }
        else
        {
            // Windows fallback via GC info
            var gc = GC.GetGCMemoryInfo();
            m.RamTotalMb     = gc.TotalAvailableMemoryBytes / 1024.0 / 1024.0;
            m.RamUsedMb      = Environment.WorkingSet          / 1024.0 / 1024.0;
            m.RamUsedPercent = m.RamTotalMb > 0 ? m.RamUsedMb * 100.0 / m.RamTotalMb : 0;
        }
    }

    private static void CollectDisks(HardwareMetrics m)
    {
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                m.Disks.Add(new DiskMetric
                {
                    MountPoint  = drive.RootDirectory.FullName,
                    TotalBytes  = drive.TotalSize,
                    UsedBytes   = drive.TotalSize - drive.AvailableFreeSpace,
                    UsedPercent = drive.TotalSize > 0
                        ? (drive.TotalSize - drive.AvailableFreeSpace) * 100.0 / drive.TotalSize
                        : 0,
                    Filesystem  = drive.DriveFormat
                });
            }
        }
        catch { /* ignore inaccessible drives */ }
    }
}
