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
/// handles single-volume and multi-volume split packages.
///   - Single volume → extract + TryResume() immediately
///   - Multi-volume  → collect all volumes in temp; restore only when all arrive
/// </summary>
public class MigrationResumeEngine
{
    private readonly string           _dataRoot;
    private readonly EncryptedStore   _encryptedStore;
    private readonly DeviceKeyManager _deviceKeyManager;
    private readonly TaskService      _taskService;
    private readonly GoalStore        _goalStore;

    private const string LogFileName      = "continuation_log.json";
    private const string ManifestFileName = "volume_manifest.json";

    // ── Multi-volume tracking ──────────────────────────────────────────────
    // Key: migrationId  Value: (receivedCount, totalExpected, mergedTempDir)
    private readonly Dictionary<string, VolumeTracker> _pending = new();
    private readonly object _pendingLock = new();

    private sealed class VolumeTracker
    {
        public int    Total      { get; set; }
        public int    Received   { get; set; }
        public string MergedDir  { get; }
        public VolumeTracker(int total, string mergedDir) { Total = total; MergedDir = mergedDir; }
    }

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
    /// Receives one migration zip volume from the source machine (HTTP POST).
    /// For single-volume packages: restores immediately.
    /// For multi-volume packages: buffers until all volumes arrive, then restores.
    /// Returns true when a complete restore was applied.
    /// </summary>
    public bool ReceivePackage(Stream zipStream)
    {
        // Extract the incoming zip to a temporary directory
        var tempDir = Path.Combine(
            Path.GetTempPath(), $"arch_recv_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);

            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                zip.ExtractToDirectory(tempDir, overwriteFiles: true);
            }

            ArchLogger.LogInfo($"[MigrationResume] Volume extracted to {tempDir}");

            // Read volume manifest (present only for split packages)
            var manifest = ReadManifest(tempDir);

            if (manifest == null || manifest.TotalVolumes <= 1)
            {
                // ── Single-volume package ─────────────────────────────────
                return RestoreFromDir(tempDir);
            }
            else
            {
                // ── Multi-volume package ──────────────────────────────────
                return AccumulateVolume(manifest, tempDir);
            }
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn(
                $"[MigrationResume] ReceivePackage failed: {ex.Message}");
            return false;
        }
        // Note: tempDir cleanup happens inside RestoreFromDir / AccumulateVolume
        // to avoid deleting the shared merge dir prematurely.
    }

    // ── Multi-volume accumulation ──────────────────────────────────────────

    private bool AccumulateVolume(MigrationVolumeManifest manifest, string tempDir)
    {
        string mergedDir;
        bool   allReceived;

        lock (_pendingLock)
        {
            if (!_pending.TryGetValue(manifest.MigrationId, out var tracker))
            {
                mergedDir = Path.Combine(
                    Path.GetTempPath(), $"arch_merge_{manifest.MigrationId}");
                Directory.CreateDirectory(mergedDir);
                tracker = new VolumeTracker(manifest.TotalVolumes, mergedDir);
                _pending[manifest.MigrationId] = tracker;
            }

            mergedDir = tracker.MergedDir;

            // Merge this volume's contents into the shared merge dir
            MergeDirectory(tempDir, mergedDir);
            try { Directory.Delete(tempDir, recursive: true); } catch { }

            tracker.Received++;
            allReceived = tracker.Received >= tracker.Total;

            ArchLogger.LogInfo(
                $"[MigrationResume] Volume {manifest.VolumeIndex}/{manifest.TotalVolumes} " +
                $"received for migration {manifest.MigrationId} " +
                $"({tracker.Received}/{tracker.Total})");
        }

        if (!allReceived) return false;

        // All volumes received — restore and clean up
        lock (_pendingLock) { _pending.Remove(manifest.MigrationId); }

        return RestoreFromDir(mergedDir);
    }

    // ── Restore from extracted directory ───────────────────────────────────

    private bool RestoreFromDir(string sourceDir)
    {
        try
        {
            // Restore data files
            var dataDir = Path.Combine(sourceDir, "data");
            if (Directory.Exists(dataDir))
            {
                CopyDirectory(dataDir, _dataRoot);
                ArchLogger.LogInfo(
                    $"[MigrationResume] Data files restored to {_dataRoot}");
            }

            // Place continuation log for TryResume()
            var logSrc = Path.Combine(sourceDir, LogFileName);
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
                $"[MigrationResume] RestoreFromDir failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { }
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

    private static MigrationVolumeManifest? ReadManifest(string dir)
    {
        var path = Path.Combine(dir, ManifestFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MigrationVolumeManifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    /// <summary>Merges all files from source into destination (overwrites).</summary>
    private static void MergeDirectory(string source, string destination)
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
            MergeDirectory(dir,
                Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static void CopyDirectory(string source, string destination)
        => MergeDirectory(source, destination);
}
