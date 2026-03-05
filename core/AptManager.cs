using System.Diagnostics;
using System.Text;

namespace Archimedes.Core;

/// <summary>
/// Manages apt package operations (update, upgrade, autoremove).
/// On non-Linux systems all operations return a descriptive dry-run message.
/// </summary>
public sealed class AptManager
{
    private AptStatus _status = new() { AutoUpgradeEnabled = true };
    private readonly object _lock = new();

    public AptStatus Status { get { lock (_lock) return _status; } }

    // ── Public API ──────────────────────────────────────────────────────────

    public async Task<AptRunResult> UpdateAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return DryRun("sudo apt-get update -y");

        var (code, output) = await RunAsync("sudo apt-get update -y", ct);
        lock (_lock)
        {
            _status.LastUpdateCheck = DateTime.UtcNow;
            _status.LastOutput      = output;
        }

        if (code == 0)
        {
            // Count upgradable packages
            var (_, upgOut) = await RunAsync(
                "apt list --upgradable 2>/dev/null | grep -c '\\[upgradable' || echo 0", ct);
            if (int.TryParse(upgOut.Trim(), out var n))
                lock (_lock) _status.PendingUpdates = n;
        }

        return new AptRunResult { Success = code == 0, Output = output };
    }

    public async Task<AptRunResult> UpgradeAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return DryRun("sudo DEBIAN_FRONTEND=noninteractive apt-get upgrade -y");

        var (code, output) = await RunAsync(
            "sudo DEBIAN_FRONTEND=noninteractive apt-get upgrade -y", ct);
        lock (_lock)
        {
            _status.LastUpgrade    = DateTime.UtcNow;
            _status.LastOutput     = output;
            _status.PendingUpdates = 0;
        }
        return new AptRunResult { Success = code == 0, Output = output };
    }

    public async Task<AptRunResult> AutoremoveAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return DryRun("sudo apt-get autoremove -y && sudo apt-get autoclean -y");

        var (code, output) = await RunAsync(
            "sudo apt-get autoremove -y && sudo apt-get autoclean -y", ct);
        lock (_lock) _status.LastOutput = output;
        return new AptRunResult { Success = code == 0, Output = output };
    }

    public async Task<int> CountPendingUpdatesAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) return 0;
        var (_, output) = await RunAsync(
            "apt list --upgradable 2>/dev/null | grep -c '\\[upgradable' || echo 0", ct);
        return int.TryParse(output.Trim(), out var n) ? n : 0;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static AptRunResult DryRun(string command) =>
        new() { Success = true, Output = $"[non-Linux dry-run] would execute: {command}" };

    internal static async Task<(int ExitCode, string Output)> RunAsync(
        string command, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo("bash", new[] { "-c", command })
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false
            };
            using var proc = Process.Start(psi)!;
            var sb = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, sb.ToString().Trim());
        }
        catch (Exception ex)
        {
            return (-1, $"Error: {ex.Message}");
        }
    }
}
