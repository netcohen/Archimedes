using System.IO.Compression;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 28 — Detects and applies a migration continuation log on startup.
///
/// How it works:
///   1. At startup, check for %LOCALAPPDATA%\Archimedes\continuation_log.json
///   2. If found:
///      a. Re-protect raw key material with this machine's DPAPI / file-permissions
///      b. Log which tasks are resumable (they are already PAUSED in the DB)
///      c. Delete the log so it only runs once
///
/// Additionally exposes ReceivePackage() for the HTTP /migration/receive endpoint —
/// extracts the zip directly and then calls TryResume().
/// </summary>
public class MigrationResumeEngine
{
    private readonly string           _dataRoot;
    private readonly EncryptedStore   _encryptedStore;
    private readonly DeviceKeyManager _deviceKeyManager;
    private readonly TaskService      _taskService;
    private readonly GoalStore        _goalStore;

    private const string LogFileName = "continuation_log.json";

    public MigrationResumeEngine(
        string           dataRoot,
        EncryptedStore   encryptedStore,
        DeviceKeyManager deviceKeyManager,
        TaskService      taskService,
        GoalStore        goalStore)
    {
        _dataRoot         = dataRoot;
        _encryptedStore   = encryptedStore;
        _deviceKeyManager = deviceKeyManager;
        _taskService      = taskService;
        _goalStore        = goalStore;
    }

    // ── Startup check ──────────────────────────────────────────────────────

    /// <summary>
    /// Called at startup.  Returns true if a migration was detected and applied.
    /// </summary>
    public bool TryResume()
    {
        var logPath = Path.Combine(_dataRoot, LogFileName);
        if (!File.Exists(logPath)) return false;

        ArchLogger.LogInfo(
            "[MigrationResume] Continuation log found — applying migration restore...");

        MigrationContinuationLog? log;
        try
        {
            var json = File.ReadAllText(logPath);
            log = JsonSerializer.Deserialize<MigrationContinuationLog>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn(
                $"[MigrationResume] Failed to parse continuation log: {ex.Message}");
            return false;
        }

        if (log == null) return false;

        // 1. Re-protect key material for this machine
        RestoreKeys(log);

        // 2. Log resumable tasks (already PAUSED in the migrated DB)
        LogResumableTasks(log);

        // 3. Remove log — idempotency: only applies once
        try { File.Delete(logPath); }
        catch (Exception ex)
        {
            ArchLogger.LogWarn(
                $"[MigrationResume] Could not delete continuation log: {ex.Message}");
        }

        ArchLogger.LogInfo(
            $"[MigrationResume] Migration {log.MigrationId} from " +
            $"'{log.SourceMachine}' applied — " +
            $"tasks={log.ResumableTasks.Count} goals={log.ActiveGoalIds.Count}");

        return true;
    }

    // ── HTTP receive ───────────────────────────────────────────────────────

    /// <summary>
    /// Receives a migration zip from the source machine (HTTP POST).
    /// Extracts the data files + continuation log, then calls TryResume().
    /// </summary>
    public bool ReceivePackage(Stream zipStream)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(), $"arch_recv_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);

            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                zip.ExtractToDirectory(tempDir, overwriteFiles: true);
            }

            ArchLogger.LogInfo(
                $"[MigrationResume] Package extracted to {tempDir}");

            // Restore data files
            var dataDir = Path.Combine(tempDir, "data");
            if (Directory.Exists(dataDir))
            {
                CopyDirectory(dataDir, _dataRoot);
                ArchLogger.LogInfo(
                    $"[MigrationResume] Data files restored to {_dataRoot}");
            }

            // Place continuation log for TryResume()
            var logSrc = Path.Combine(tempDir, LogFileName);
            if (File.Exists(logSrc))
            {
                File.Copy(logSrc,
                    Path.Combine(_dataRoot, LogFileName),
                    overwrite: true);
            }

            return TryResume();
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn(
                $"[MigrationResume] ReceivePackage failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ── Key restoration ────────────────────────────────────────────────────

    private void RestoreKeys(MigrationContinuationLog log)
    {
        if (!string.IsNullOrEmpty(log.RawDbKeyBase64))
        {
            try
            {
                var raw = Convert.FromBase64String(log.RawDbKeyBase64);
                _encryptedStore.RestoreKeyFromMigration(raw);
                ArchLogger.LogInfo(
                    "[MigrationResume] DB key re-protected for this machine");
            }
            catch (Exception ex)
            {
                ArchLogger.LogWarn(
                    $"[MigrationResume] DB key restore failed: {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(log.RawDeviceKeysBase64))
        {
            try
            {
                var raw = Convert.FromBase64String(log.RawDeviceKeysBase64);
                _deviceKeyManager.RestoreKeysFromMigration(raw);
                ArchLogger.LogInfo(
                    "[MigrationResume] Device keys re-protected for this machine");
            }
            catch (Exception ex)
            {
                ArchLogger.LogWarn(
                    $"[MigrationResume] Device keys restore failed: {ex.Message}");
            }
        }
    }

    // ── Logging ────────────────────────────────────────────────────────────

    private static void LogResumableTasks(MigrationContinuationLog log)
    {
        foreach (var t in log.ResumableTasks)
        {
            ArchLogger.LogInfo(
                $"[MigrationResume] Resumable task: id={t.TaskId} " +
                $"title=\"{t.Title}\" state={t.StateBefore} step={t.StepBefore}");
        }
        foreach (var gId in log.ActiveGoalIds)
        {
            ArchLogger.LogInfo(
                $"[MigrationResume] Active goal at migration time: {gId}");
        }
    }

    // ── Static helpers ─────────────────────────────────────────────────────

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file,
                Path.Combine(destination, Path.GetFileName(file)),
                overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir,
                Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}
