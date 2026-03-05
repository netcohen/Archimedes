using System.IO.Compression;
using System.Text.Json;

namespace Archimedes.Core;

/// <summary>
/// Phase 28 — Packages all Archimedes state into one or more portable zip files.
///
/// Package structure (each volume):
///   continuation_log.json          — resume manifest + raw key material (SENSITIVE)
///   volume_manifest.json           — volume index / total (omitted for single-zip)
///   data/archimedes.db             — encrypted SQLite (tasks, runs, outbox)
///   data/goals.json
///   data/acquired_tools.json
///   data/tool_gaps.json
///   data/legal_approvals.json
///   data/source_intelligence.json
///   data/procedures/*.json
///
/// Single-zip (default): one zip per migration.
/// Split mode (MaxVolumeSizeMB > 0): multiple zips, each ≤ MaxVolumeSizeMB,
///   for use with limited-capacity transfer media (USB drives, etc.).
///
/// Zips are written to %LOCALAPPDATA%\Archimedes\migrations\.
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
    /// Creates one or more migration zip volumes.
    /// Sets plan.PackagePath (first/only zip), plan.PackagePaths (all), plan.VolumeCount.
    /// Returns the list of created zip paths.
    /// </summary>
    public Task<List<string>> CreatePackageAsync(
        MigrationPlan plan, CancellationToken ct = default)
    {
        ArchLogger.LogInfo("[Packager] Building migration package...");

        var log   = BuildContinuationLog(plan);
        var files = CollectFiles().ToList();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

        List<string> paths;

        if (plan.MaxVolumeSizeMB <= 0)
        {
            // ── Single zip (original behaviour) ──────────────────────────────
            var zipName = $"archimedes_migration_{plan.MigrationId}_{stamp}.zip";
            var zipPath = Path.Combine(_packageDir, zipName);

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddJsonEntry(zip, "continuation_log.json", log);
                foreach (var f in files)
                    AddFileEntry(zip, f.SourcePath, f.EntryName);
            }

            var sizeKb = new FileInfo(zipPath).Length / 1024;
            ArchLogger.LogInfo(
                $"[Packager] Package ready: {zipPath} ({sizeKb} KB) — " +
                $"tasks={log.ResumableTasks.Count} goals={log.ActiveGoalIds.Count}");

            paths = new List<string> { zipPath };
        }
        else
        {
            // ── Multi-volume split ────────────────────────────────────────────
            paths = CreateSplitZips(plan, log, files, stamp);
        }

        plan.PackagePaths = paths;
        plan.PackagePath  = paths.FirstOrDefault();
        plan.VolumeCount  = paths.Count;

        return Task.FromResult(paths);
    }

    // ── Split-zip creation ─────────────────────────────────────────────────

    private List<string> CreateSplitZips(
        MigrationPlan            plan,
        MigrationContinuationLog log,
        List<FileEntry>          files,
        string                   stamp)
    {
        long maxBytes     = plan.MaxVolumeSizeMB * 1024L * 1024L;
        var  volumeGroups = BinPack(files, maxBytes);
        int  total        = volumeGroups.Count;
        var  paths        = new List<string>(total);

        ArchLogger.LogInfo(
            $"[Packager] Split mode: {total} volume(s) × " +
            $"≤ {plan.MaxVolumeSizeMB} MB each");

        for (int i = 0; i < total; i++)
        {
            int volIndex = i + 1;
            var zipName  = $"archimedes_migration_{plan.MigrationId}_{stamp}_vol{volIndex:D3}.zip";
            var zipPath  = Path.Combine(_packageDir, zipName);

            var manifest = new MigrationVolumeManifest
            {
                MigrationId      = plan.MigrationId,
                TotalVolumes     = total,
                VolumeIndex      = volIndex,
                TotalEstimatedMB = plan.RequiredDiskMB
            };

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                // Continuation log + volume manifest go in EVERY volume
                // so any single volume can bootstrap the restore process.
                AddJsonEntry(zip, "continuation_log.json", log);
                AddJsonEntry(zip, "volume_manifest.json", manifest);

                foreach (var f in volumeGroups[i])
                    AddFileEntry(zip, f.SourcePath, f.EntryName);
            }

            var sizeKb = new FileInfo(zipPath).Length / 1024;
            ArchLogger.LogInfo(
                $"[Packager] Volume {volIndex}/{total}: {zipPath} ({sizeKb} KB)");

            paths.Add(zipPath);
        }

        return paths;
    }

    // ── File collection ────────────────────────────────────────────────────

    private record FileEntry(string SourcePath, string EntryName, long EstimatedBytes);

    /// <summary>
    /// Enumerates all data files to include in the package,
    /// together with their estimated compressed size (75 % of raw).
    /// </summary>
    private IEnumerable<FileEntry> CollectFiles()
    {
        const double R = 0.75;   // compression ratio: compressed ≈ 75 % of raw size

        yield return MakeEntry(Path.Combine(_dataRoot, "archimedes.db"), "data/archimedes.db", R);

        foreach (var name in new[]
                 { "goals.json", "acquired_tools.json", "tool_gaps.json",
                   "legal_approvals.json", "source_intelligence.json" })
            yield return MakeEntry(Path.Combine(_dataRoot, name), $"data/{name}", R);

        var procDir = Path.Combine(_dataRoot, "procedures");
        if (Directory.Exists(procDir))
            foreach (var f in Directory.GetFiles(procDir, "*.json"))
                yield return MakeEntry(f, $"data/procedures/{Path.GetFileName(f)}", R);
    }

    private static FileEntry MakeEntry(string path, string entry, double ratio)
    {
        long sz = 0;
        try { if (File.Exists(path)) sz = new FileInfo(path).Length; } catch { }
        return new FileEntry(path, entry, (long)(sz * ratio));
    }

    // ── Bin-packing ────────────────────────────────────────────────────────

    /// <summary>
    /// Greedy first-fit decreasing bin-packing.
    /// Files are sorted largest-first for better packing efficiency.
    /// A file larger than one volume gets its own group (best-effort — estimated sizes).
    /// </summary>
    private static List<List<FileEntry>> BinPack(List<FileEntry> files, long maxBytesPerVolume)
    {
        var groups    = new List<List<FileEntry>>();
        var groupSums = new List<long>();

        foreach (var file in files
                     .Where(f => File.Exists(f.SourcePath))
                     .OrderByDescending(f => f.EstimatedBytes))
        {
            bool placed = false;
            for (int v = 0; v < groups.Count; v++)
            {
                if (groupSums[v] + file.EstimatedBytes <= maxBytesPerVolume)
                {
                    groups[v].Add(file);
                    groupSums[v] += file.EstimatedBytes;
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                groups.Add(new List<FileEntry> { file });
                groupSums.Add(file.EstimatedBytes);
            }
        }

        if (groups.Count == 0)
            groups.Add(new List<FileEntry>());

        return groups;
    }

    // ── Pre-flight estimate (GET /migration/estimate) ──────────────────────

    /// <summary>
    /// Returns a detailed size estimate with per-component breakdown.
    /// Call this before starting migration so the user knows which drive to prepare.
    /// </summary>
    public MigrationSizeEstimate GetDetailedEstimate()
    {
        long dbBytes    = FileSize(Path.Combine(_dataRoot, "archimedes.db"));
        long procBytes  = DirSize(Path.Combine(_dataRoot, "procedures"));
        long toolBytes  = FileSize(Path.Combine(_dataRoot, "acquired_tools.json"))
                        + FileSize(Path.Combine(_dataRoot, "tool_gaps.json"));
        long goalBytes  = FileSize(Path.Combine(_dataRoot, "goals.json"));
        long otherBytes = FileSize(Path.Combine(_dataRoot, "legal_approvals.json"))
                        + FileSize(Path.Combine(_dataRoot, "source_intelligence.json"));

        long rawTotal      = dbBytes + procBytes + toolBytes + goalBytes + otherBytes;
        long estimatedMB   = (long)(rawTotal * 0.80 / 1024 / 1024) + 10;
        long recommendedMB = (long)(estimatedMB * 1.20);  // +20 % safety margin

        return new MigrationSizeEstimate
        {
            RawDataMB          = rawTotal / 1024 / 1024,
            EstimatedPackageMB = Math.Max(10, estimatedMB),
            RecommendedDriveMB = Math.Max(15, recommendedMB),
            Breakdown          = new MigrationSizeBreakdown
            {
                DatabaseMB   = ToMB(dbBytes),
                ProceduresMB = ToMB(procBytes),
                ToolStoreMB  = ToMB(toolBytes),
                GoalsMB      = ToMB(goalBytes),
                OtherMB      = ToMB(otherBytes)
            }
        };
    }

    // ── Rough estimate (used by MigrationEngine for disk check) ───────────

    /// <summary>
    /// Quick estimate: 80 % of data-dir size + 10 MB margin.
    /// </summary>
    public long EstimatePackageSizeMB()
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(
                _dataRoot, "*", SearchOption.AllDirectories))
            {
                // Skip previously created migration zips from the estimate
                if (f.StartsWith(_packageDir, StringComparison.OrdinalIgnoreCase)) continue;
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return Math.Max(10, (long)(total * 0.80 / 1024 / 1024) + 10);
    }

    // ── Continuation log ───────────────────────────────────────────────────

    private MigrationContinuationLog BuildContinuationLog(MigrationPlan plan)
    {
        var log = new MigrationContinuationLog
        {
            MigrationId        = plan.MigrationId,
            NewMachineBootstrap = true,   // Phase 35: always signal 24h CodePatcher hold on target
            ResumableTasks     = plan.TaskDecisions
                .Where(d => d.Action == TaskMigrationAction.SUSPEND)
                .ToList(),
            ActiveGoalIds      = _goalStore.GetActive()
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

    // ── File-size helpers ──────────────────────────────────────────────────

    private static long FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
    }

    private static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { total += new FileInfo(f).Length; } catch { }
        }
        catch { }
        return total;
    }

    private static long ToMB(long bytes) => Math.Max(0, bytes / 1024 / 1024);
}
