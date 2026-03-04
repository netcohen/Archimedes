using System.IO.Compression;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 28 — Packages all Archimedes state into a portable zip file.
///
/// Package structure:
///   continuation_log.json          — resume manifest + raw key material (SENSITIVE)
///   data/archimedes.db             — encrypted SQLite (tasks, runs, outbox)
///   data/goals.json
///   data/acquired_tools.json
///   data/tool_gaps.json
///   data/legal_approvals.json
///   data/source_intelligence.json
///   data/procedures/*.json
///
/// The zip is written to %LOCALAPPDATA%\Archimedes\migrations\.
/// </summary>
public class MigrationStatePackager
{
    private readonly TaskService      _taskService;
    private readonly GoalStore        _goalStore;
    private readonly EncryptedStore   _encryptedStore;
    private readonly DeviceKeyManager _deviceKeyManager;
    private readonly string           _dataRoot;
    private readonly string           _packageDir;

    public MigrationStatePackager(
        TaskService      taskService,
        GoalStore        goalStore,
        EncryptedStore   encryptedStore,
        DeviceKeyManager deviceKeyManager,
        string?          dataRoot = null)
    {
        _taskService      = taskService;
        _goalStore        = goalStore;
        _encryptedStore   = encryptedStore;
        _deviceKeyManager = deviceKeyManager;
        _dataRoot         = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Archimedes");
        _packageDir = Path.Combine(_dataRoot, "migrations");
        Directory.CreateDirectory(_packageDir);
    }

    // ── Main entry ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the migration zip.  Returns its full path.
    /// </summary>
    public Task<string> CreatePackageAsync(
        MigrationPlan plan, CancellationToken ct = default)
    {
        ArchLogger.LogInfo("[Packager] Building migration package...");

        var stamp   = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var zipName = $"archimedes_migration_{plan.MigrationId}_{stamp}.zip";
        var zipPath = Path.Combine(_packageDir, zipName);

        var log = BuildContinuationLog(plan);

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            // 1. Continuation log (resume manifest + key material)
            AddJsonEntry(zip, "continuation_log.json", log);

            // 2. SQLite database
            AddFileEntry(zip,
                Path.Combine(_dataRoot, "archimedes.db"),
                "data/archimedes.db");

            // 3. Flat JSON stores
            foreach (var file in new[]
            {
                "goals.json", "acquired_tools.json", "tool_gaps.json",
                "legal_approvals.json", "source_intelligence.json"
            })
                AddFileEntry(zip,
                    Path.Combine(_dataRoot, file),
                    $"data/{file}");

            // 4. Procedures directory
            var procDir = Path.Combine(_dataRoot, "procedures");
            if (Directory.Exists(procDir))
                foreach (var f in Directory.GetFiles(procDir, "*.json"))
                    AddFileEntry(zip, f, $"data/procedures/{Path.GetFileName(f)}");
        }

        plan.PackagePath = zipPath;

        var sizeKb = new FileInfo(zipPath).Length / 1024;
        ArchLogger.LogInfo(
            $"[Packager] Package ready: {zipPath} ({sizeKb} KB) — " +
            $"tasks={log.ResumableTasks.Count} goals={log.ActiveGoalIds.Count}");

        return Task.FromResult(zipPath);
    }

    // ── Continuation log ───────────────────────────────────────────────────

    private MigrationContinuationLog BuildContinuationLog(MigrationPlan plan)
    {
        var log = new MigrationContinuationLog
        {
            MigrationId   = plan.MigrationId,
            ResumableTasks = plan.TaskDecisions
                .Where(d => d.Action == TaskMigrationAction.SUSPEND)
                .ToList(),
            ActiveGoalIds = _goalStore.GetActive()
                                      .Select(g => g.GoalId)
                                      .ToList()
        };

        // Embed raw key material for cross-machine re-protection
        try
        {
            var rawDb = _encryptedStore.GetRawKeyForMigration();
            if (rawDb.Length > 0)
                log.RawDbKeyBase64 = Convert.ToBase64String(rawDb);
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[Packager] Could not read DB key: {ex.Message}");
        }

        try
        {
            var rawDevice = _deviceKeyManager.GetRawKeysForMigration();
            if (rawDevice.Length > 0)
                log.RawDeviceKeysBase64 = Convert.ToBase64String(rawDevice);
        }
        catch (Exception ex)
        {
            ArchLogger.LogWarn($"[Packager] Could not read device keys: {ex.Message}");
        }

        return log;
    }

    // ── Size estimate ──────────────────────────────────────────────────────

    /// <summary>
    /// Rough estimate of the compressed package size:
    /// 80% of current data-dir size + 10 MB safety margin.
    /// </summary>
    public long EstimatePackageSizeMB()
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(
                _dataRoot, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return Math.Max(10, (long)(total * 0.80 / 1024 / 1024) + 10);
    }

    // ── Zip helpers ────────────────────────────────────────────────────────

    private static void AddJsonEntry(ZipArchive zip, string entryName, object obj)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(obj,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AddFileEntry(ZipArchive zip, string filePath, string entryName)
    {
        if (!File.Exists(filePath)) return;
        zip.CreateEntryFromFile(filePath, entryName, CompressionLevel.SmallestSize);
    }
}
