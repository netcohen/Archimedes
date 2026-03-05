using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 30 — Ubuntu OS Autonomy Engine.
/// Runs a background heartbeat every hour to:
///   - Collect hardware metrics (CPU temp, RAM, disk)
///   - Run apt update/upgrade/autoremove during the maintenance window
///   - Schedule reboots when kernel updates require them
///   - Clean old log files
///   - Manage ufw firewall rules
/// On non-Linux systems all shell operations are no-ops (dry-run).
/// Follows the GoalRunner/SelfImprovementEngine pattern: manual Start()/Stop().
/// </summary>
public sealed class OsManager : IDisposable
{
    private readonly HardwareMonitor _hardware;
    private readonly AptManager      _apt;
    private readonly string          _dataRoot;

    private Timer?                  _timer;
    private CancellationTokenSource _cts = new();
    private OsStatus                _status = new();
    private MaintenanceWindow       _window = new();
    private RebootSchedule?         _reboot;
    private readonly object         _lock   = new();

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private string RebootFile  => Path.Combine(_dataRoot, "os_reboot_schedule.json");
    private string WindowFile  => Path.Combine(_dataRoot, "os_maintenance_window.json");

    public OsManager(HardwareMonitor hardware, AptManager apt, StorageManager storage)
    {
        _hardware = hardware;
        _apt      = apt;
        _dataRoot = storage.RootInternal;
        Directory.CreateDirectory(_dataRoot);
        LoadPersistedState();
        _status.OsName  = OperatingSystem.IsLinux() ? "Linux" : "Windows";
        _status.IsLinux = OperatingSystem.IsLinux();
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public void Start()
    {
        _cts   = new CancellationTokenSource();
        // First tick after 30 seconds, then every hour
        _timer = new Timer(_ => _ = TickAsync(), null,
            TimeSpan.FromSeconds(30), CheckInterval);
    }

    public void Stop()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _timer = null;
        lock (_lock) _status.State = OsManagerState.STOPPED;
    }

    public void Dispose() { Stop(); _cts.Dispose(); }

    // ── Status ───────────────────────────────────────────────────────────────

    public OsStatus GetStatus()
    {
        lock (_lock)
        {
            _status.RebootRequired  = IsRebootRequired();
            _status.ScheduledReboot = _reboot?.ScheduledAt;
            _status.Hardware        = _hardware.Collect();
            _status.Apt             = _apt.Status;
            _status.Firewall        = GetFirewallStatus();
            return _status;
        }
    }

    // ── Maintenance window ────────────────────────────────────────────────────

    public MaintenanceWindow GetMaintenanceWindow() { lock (_lock) return _window; }

    public void SetMaintenanceWindow(MaintenanceWindow w)
    {
        lock (_lock) _window = w;
        File.WriteAllText(WindowFile, JsonSerializer.Serialize(w));
    }

    // ── Reboot management ─────────────────────────────────────────────────────

    public static bool IsRebootRequired() =>
        OperatingSystem.IsLinux() && File.Exists("/var/run/reboot-required");

    public RebootSchedule ScheduleReboot(string reason = "maintenance")
    {
        lock (_lock)
        {
            var when = _window.NextWindow();
            _reboot  = new RebootSchedule
            {
                ScheduledAt = when,
                Reason      = reason,
                CreatedAt   = DateTime.UtcNow
            };
            File.WriteAllText(RebootFile, JsonSerializer.Serialize(_reboot));
            return _reboot;
        }
    }

    public RebootSchedule? GetScheduledReboot() { lock (_lock) return _reboot; }

    public void CancelScheduledReboot()
    {
        lock (_lock)
        {
            _reboot = null;
            if (File.Exists(RebootFile)) File.Delete(RebootFile);
        }
    }

    public async Task<(bool Success, string Output)> RebootNowAsync()
    {
        if (!OperatingSystem.IsLinux())
            return (false, "[non-Linux] reboot not supported on this OS");

        lock (_lock) _status.State = OsManagerState.REBOOTING;
        CancelScheduledReboot();
        var (code, output) = await RunShellAsync("sudo reboot");
        return (code == 0, output);
    }

    // ── Firewall (ufw) ────────────────────────────────────────────────────────

    public (bool Success, string Output) AddFirewallRule(FirewallRule rule)
    {
        if (!OperatingSystem.IsLinux())
            return (true, $"[non-Linux dry-run] would run: sudo ufw {rule.Action} {rule.Port}/{rule.Protocol}");

        var comment = string.IsNullOrWhiteSpace(rule.Comment) ? "" : $" comment '{rule.Comment}'";
        var cmd = $"sudo ufw {rule.Action} {rule.Port}/{rule.Protocol}{comment}";
        var task = RunShellAsync(cmd);
        task.Wait();
        var (code, output) = task.Result;
        return (code == 0, output);
    }

    public FirewallStatus GetFirewallStatus()
    {
        if (!OperatingSystem.IsLinux())
            return new FirewallStatus { Enabled = false, Rules = [] };
        try
        {
            var task = RunShellAsync("sudo ufw status numbered 2>/dev/null || echo 'inactive'");
            task.Wait();
            return ParseUfwStatus(task.Result.Output);
        }
        catch { return new FirewallStatus { Enabled = false, Rules = [] }; }
    }

    // ── Log cleanup ──────────────────────────────────────────────────────────

    public OsLogCleanupResult CleanLogs(int keepDays = 30)
    {
        var result = new OsLogCleanupResult();
        var cutoff = DateTime.UtcNow.AddDays(-keepDays);

        var searchPaths = new[] { _dataRoot, "/var/log/archimedes" }
            .Where(Directory.Exists);

        foreach (var path in searchPaths)
        {
            foreach (var file in Directory.GetFiles(path, "*.log", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc >= cutoff) continue;
                try
                {
                    var sizeMb = info.Length / 1024.0 / 1024.0;
                    File.Delete(file);
                    result.DeletedFiles++;
                    result.FreedMb += sizeMb;
                    result.Paths.Add(file);
                }
                catch { /* skip locked files */ }
            }
        }
        return result;
    }

    // ── Background tick ──────────────────────────────────────────────────────

    private async Task TickAsync()
    {
        if (_cts.Token.IsCancellationRequested) return;
        try
        {
            lock (_lock) { _status.State = OsManagerState.CHECKING; _status.LastCheckAt = DateTime.UtcNow; }

            // 1. Collect hardware
            var hw = _hardware.Collect();
            lock (_lock) _status.Hardware = hw;

            // 2. Check if a scheduled reboot is due
            RebootSchedule? sched;
            lock (_lock) sched = _reboot;
            if (sched != null && DateTime.UtcNow >= sched.ScheduledAt && _window.IsNow())
            {
                await RebootNowAsync();
                return;
            }

            // 3. Inside maintenance window: update + upgrade + autoremove + log cleanup
            if (_window.IsNow())
            {
                lock (_lock) _status.State = OsManagerState.UPDATING;
                await _apt.UpdateAsync(_cts.Token);
                var pending = await _apt.CountPendingUpdatesAsync(_cts.Token);
                if (pending > 0)
                {
                    await _apt.UpgradeAsync(_cts.Token);
                    await _apt.AutoremoveAsync(_cts.Token);
                    if (IsRebootRequired())
                        ScheduleReboot("apt upgrade requires reboot");
                }
                CleanLogs(30);
            }
            else if (DateTime.Now.Hour % 6 == 0) // Every 6h outside window: update metadata only
            {
                await _apt.UpdateAsync(_cts.Token);
            }

            lock (_lock) { _status.State = OsManagerState.IDLE; _status.LastError = null; }
        }
        catch (Exception ex)
        {
            lock (_lock) { _status.State = OsManagerState.ERROR; _status.LastError = ex.Message; }
        }
    }

    // ── Shell helper ─────────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Output)> RunShellAsync(string command)
    {
        if (!OperatingSystem.IsLinux())
            return (-1, $"[non-Linux] skipped: {command}");
        return await AptManager.RunAsync(command);
    }

    // ── ufw output parser ────────────────────────────────────────────────────

    private static FirewallStatus ParseUfwStatus(string output)
    {
        var status  = new FirewallStatus();
        status.Enabled = output.Contains("active") && !output.Contains("inactive");
        if (!status.Enabled) return status;

        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("ALLOW") && !line.Contains("DENY")) continue;
            var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            status.Rules.Add(new FirewallRule
            {
                Port   = parts.Length > 1 ? parts[1] : "",
                Action = line.Contains("ALLOW") ? "allow" : "deny"
            });
        }
        return status;
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void LoadPersistedState()
    {
        if (File.Exists(RebootFile))
        {
            try { _reboot = JsonSerializer.Deserialize<RebootSchedule>(File.ReadAllText(RebootFile)); }
            catch { /* ignore corrupt file */ }
        }
        if (File.Exists(WindowFile))
        {
            try { _window = JsonSerializer.Deserialize<MaintenanceWindow>(File.ReadAllText(WindowFile)) ?? new(); }
            catch { /* ignore corrupt file */ }
        }
    }
}
