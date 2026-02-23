using System.Diagnostics;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Tiered storage with retention and quota policies.
/// Internal: critical data. External: artifacts, logs, models.
/// </summary>
public class StorageManager
{
    private readonly string _rootInternal;
    private readonly string? _rootExternal;
    private readonly StorageConfig _config;
    private DateTime _lastCleanupRun = DateTime.MinValue;

    public StorageManager(StorageConfig config)
    {
        _config = config ?? new StorageConfig();
        _rootInternal = NormalizePath(_config.RootInternal ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Archimedes"
        ));
        _rootExternal = string.IsNullOrWhiteSpace(_config.RootExternal)
            ? null
            : NormalizePath(_config.RootExternal);

        Directory.CreateDirectory(_rootInternal);
        if (_rootExternal != null)
            Directory.CreateDirectory(_rootExternal);
    }

    public string RootInternal => _rootInternal;
    public string? RootExternal => _rootExternal;

    /// <summary>
    /// Returns true if storage is healthy and we can accept more load.
    /// </summary>
    public bool CanAcceptLoad()
    {
        var health = GetHealthReport();
        return health.FreeSpaceMB >= (_config.MinFreeSpaceMB ?? 500) &&
               health.IsUnderQuota;
    }

    /// <summary>
    /// Get storage health report.
    /// </summary>
    public StorageHealthReport GetHealthReport()
    {
        var report = new StorageHealthReport
        {
            RootInternal = _rootInternal,
            RootExternal = _rootExternal,
            Timestamp = DateTime.UtcNow
        };

        try
        {
            var internalDrive = Path.GetPathRoot(_rootInternal);
            if (!string.IsNullOrEmpty(internalDrive))
            {
                var di = new DriveInfo(internalDrive);
                report.FreeSpaceMB = di.AvailableFreeSpace / 1024 / 1024;
                report.TotalSpaceMB = di.TotalSize / 1024 / 1024;
            }
        }
        catch (Exception ex)
        {
            report.Errors.Add($"Drive info: {ex.Message}");
        }

        report.ArtifactsMaxGB = _config.ArtifactsMaxGB ?? 10;
        report.LogsRetentionDays = _config.LogsRetentionDays ?? 7;
        report.MinFreeSpaceMB = _config.MinFreeSpaceMB ?? 500;

        try
        {
            report.InternalDirSizeMB = GetDirectorySizeMB(_rootInternal);
            if (_rootExternal != null)
                report.ExternalDirSizeMB = GetDirectorySizeMB(_rootExternal);
        }
        catch (Exception ex)
        {
            report.Errors.Add($"Dir size: {ex.Message}");
        }

        var artifactsGB = (report.ExternalDirSizeMB + report.InternalDirSizeMB) / 1024.0;
        report.IsUnderQuota = artifactsGB <= report.ArtifactsMaxGB;
        report.LargestDirs = GetLargestSubdirs(_rootInternal, 5);
        if (_rootExternal != null)
            report.LargestDirs.AddRange(GetLargestSubdirs(_rootExternal, 5));

        report.LastCleanupRun = _lastCleanupRun == DateTime.MinValue ? null : _lastCleanupRun;
        report.PolicyActionsTaken = new List<string>();

        return report;
    }

    /// <summary>
    /// Run retention and cleanup policies.
    /// </summary>
    public CleanupResult RunCleanup()
    {
        var result = new CleanupResult { Timestamp = DateTime.UtcNow };
        var actions = new List<string>();

        try
        {
            var logsPath = Path.Combine(_rootInternal, "logs");
            if (Directory.Exists(logsPath) && _config.LogsRetentionDays.HasValue)
            {
                var cutoff = DateTime.UtcNow.AddDays(-_config.LogsRetentionDays.Value);
                var deleted = 0;
                foreach (var f in Directory.GetFiles(logsPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) < cutoff)
                        {
                            File.Delete(f);
                            deleted++;
                        }
                    }
                    catch { }
                }
                if (deleted > 0)
                    actions.Add($"Deleted {deleted} log file(s) older than {_config.LogsRetentionDays} days");
            }

            var tempPath = Path.Combine(_rootInternal, "temp");
            if (Directory.Exists(tempPath))
            {
                var deleted = 0;
                foreach (var f in Directory.GetFiles(tempPath))
                {
                    try
                    {
                        if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalHours > 24)
                        {
                            File.Delete(f);
                            deleted++;
                        }
                    }
                    catch { }
                }
                if (deleted > 0)
                    actions.Add($"Deleted {deleted} temp file(s) older than 24h");
            }

            if (_rootExternal != null && _config.ArtifactsMaxGB.HasValue)
            {
                var sizeMB = GetDirectorySizeMB(_rootExternal);
                var sizeGB = sizeMB / 1024.0;
                if (sizeGB > _config.ArtifactsMaxGB.Value)
                {
                    var dirs = GetLargestSubdirs(_rootExternal, 10);
                    foreach (var d in dirs.OrderByDescending(x => x.SizeMB))
                    {
                        if (sizeGB <= _config.ArtifactsMaxGB.Value * 0.9) break;
                        try
                        {
                            Directory.Delete(d.Path, true);
                            sizeMB = GetDirectorySizeMB(_rootExternal);
                            sizeGB = sizeMB / 1024.0;
                            actions.Add($"Removed {d.Path} to free space");
                        }
                        catch { }
                    }
                }
            }

            _lastCleanupRun = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
        }

        result.Actions = actions;
        result.PolicyActionsTaken = actions;
        return result;
    }

    public string GetInternalPath(string relativePath)
    {
        return Path.Combine(_rootInternal, relativePath.TrimStart('/', '\\'));
    }

    public string? GetExternalPath(string relativePath)
    {
        if (_rootExternal == null) return null;
        return Path.Combine(_rootExternal, relativePath.TrimStart('/', '\\'));
    }

    private static string NormalizePath(string p)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(p));
    }

    private static long GetDirectorySizeMB(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long bytes = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return bytes / 1024 / 1024;
    }

    private static List<DirSizeInfo> GetLargestSubdirs(string path, int topN)
    {
        var list = new List<DirSizeInfo>();
        if (!Directory.Exists(path)) return list;
        try
        {
            foreach (var d in Directory.GetDirectories(path))
            {
                try
                {
                    var sizeMB = GetDirectorySizeMB(d);
                    list.Add(new DirSizeInfo { Path = d, SizeMB = sizeMB });
                }
                catch { }
            }
            return list.OrderByDescending(x => x.SizeMB).Take(topN).ToList();
        }
        catch { }
        return list;
    }
}

public class StorageConfig
{
    public string? RootInternal { get; set; }
    public string? RootExternal { get; set; }
    public int? LogsRetentionDays { get; set; } = 7;
    public int? ArtifactsMaxGB { get; set; } = 10;
    public int? MinFreeSpaceMB { get; set; } = 500;
    public string? CleanupScheduleCron { get; set; }
}

public class StorageHealthReport
{
    public string RootInternal { get; set; } = "";
    public string? RootExternal { get; set; }
    public DateTime Timestamp { get; set; }
    public long FreeSpaceMB { get; set; }
    public long TotalSpaceMB { get; set; }
    public long InternalDirSizeMB { get; set; }
    public long ExternalDirSizeMB { get; set; }
    public int ArtifactsMaxGB { get; set; }
    public int LogsRetentionDays { get; set; }
    public int MinFreeSpaceMB { get; set; }
    public bool IsUnderQuota { get; set; }
    public List<DirSizeInfo> LargestDirs { get; set; } = new();
    public DateTime? LastCleanupRun { get; set; }
    public List<string> PolicyActionsTaken { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public class DirSizeInfo
{
    public string Path { get; set; } = "";
    public long SizeMB { get; set; }
}

public class CleanupResult
{
    public DateTime Timestamp { get; set; }
    public List<string> Actions { get; set; } = new();
    public List<string> PolicyActionsTaken { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}
