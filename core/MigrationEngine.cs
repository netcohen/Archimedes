namespace Archimedes.Core;

/// <summary>
/// Phase 28 — Main orchestrator for machine migration (Octopus).
///
/// Migration pipeline (non-blocking):
///   1. Estimate required disk space
///   2. Check disk on target (LOCAL_PATH DriveInfo / HTTP /storage/health)
///   3. Suspend in-flight tasks (pause RUNNING, record all non-terminal)
///   4. Package state → zip (DB + JSON stores + procedures + continuation log)
///   5. Deploy to target (file copy + restore script  OR  HTTP POST)
///
/// On the target machine at next startup:
///   MigrationResumeEngine.TryResume() detects continuation_log.json,
///   re-protects keys for the new machine, and logs resumable tasks.
///   The TaskRunner then picks up PAUSED tasks normally.
///
/// Usage:
///   POST /migration/start  → returns MigrationPlan (Status=CHECKING_DISK)
///   GET  /migration/status/{id}  → poll for progress
///   POST /migration/receive      → target endpoint (receives zip, triggers resume)
/// </summary>
public class MigrationEngine
{
    private readonly MigrationStatePackager _packager;
    private readonly MigrationDiskChecker   _diskChecker;
    private readonly TaskSuspender          _taskSuspender;
    private readonly MigrationDeployer      _deployer;

    private readonly Dictionary<string, MigrationPlan> _plans = new();
    private readonly object _lock = new();

    public MigrationEngine(
        MigrationStatePackager packager,
        MigrationDiskChecker   diskChecker,
        TaskSuspender          taskSuspender,
        MigrationDeployer      deployer)
    {
        _packager      = packager;
        _diskChecker   = diskChecker;
        _taskSuspender = taskSuspender;
        _deployer      = deployer;
    }

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Starts migration (non-blocking).
    /// Returns the MigrationPlan immediately; pipeline runs in background.
    /// </summary>
    public MigrationPlan Start(StartMigrationRequest request)
    {
        var targetType = request.TargetType?.ToUpperInvariant() == "HTTP_URL"
            ? MigrationTargetType.HTTP_URL
            : MigrationTargetType.LOCAL_PATH;

        var plan = new MigrationPlan
        {
            TargetType = targetType,
            TargetPath = request.TargetPath,
            DryRun     = request.DryRun,
            Status     = MigrationStatus.CHECKING_DISK
        };

        lock (_lock) { _plans[plan.MigrationId] = plan; }

        ArchLogger.LogInfo(
            $"[MigrationEngine] Starting migration {plan.MigrationId} → " +
            $"'{request.TargetPath}' dryRun={request.DryRun}");

        _ = Task.Run(async () => await RunPipelineAsync(plan));

        return plan;
    }

    /// <summary>Returns a plan by ID, or null.</summary>
    public MigrationPlan? GetPlan(string migrationId)
    {
        lock (_lock)
            return _plans.TryGetValue(migrationId, out var p) ? p : null;
    }

    /// <summary>Returns all plans, newest first.</summary>
    public List<MigrationPlan> GetAllPlans()
    {
        lock (_lock)
            return _plans.Values
                         .OrderByDescending(p => p.StartedAt)
                         .ToList();
    }

    // ── Migration pipeline ─────────────────────────────────────────────────

    private async Task RunPipelineAsync(MigrationPlan plan)
    {
        try
        {
            // ── 1. Estimate package size ──────────────────────────────────
            plan.RequiredDiskMB = _packager.EstimatePackageSizeMB();

            // ── 2. Check target disk ──────────────────────────────────────
            plan.Status = MigrationStatus.CHECKING_DISK;
            var disk = await _diskChecker.CheckAsync(
                plan.TargetType, plan.TargetPath, plan.RequiredDiskMB);

            plan.AvailableDiskMB = disk.AvailableMB;

            if (!disk.IsEnough)
            {
                plan.Status = MigrationStatus.FAILED;
                plan.Error  =
                    $"Insufficient disk on target: " +
                    $"need {plan.RequiredDiskMB} MB, " +
                    $"available {disk.AvailableMB} MB" +
                    (disk.Error != null ? $" ({disk.Error})" : "");
                ArchLogger.LogWarn($"[MigrationEngine] {plan.Error}");
                return;
            }

            if (plan.DryRun)
            {
                plan.Status      = MigrationStatus.COMPLETED;
                plan.CompletedAt = DateTime.UtcNow;
                ArchLogger.LogInfo(
                    $"[MigrationEngine] Dry-run complete — " +
                    $"disk OK ({disk.AvailableMB} MB available, " +
                    $"{plan.RequiredDiskMB} MB needed)");
                return;
            }

            // ── 3. Suspend in-flight tasks ────────────────────────────────
            plan.Status        = MigrationStatus.SUSPENDING_TASKS;
            plan.TaskDecisions = await _taskSuspender.SuspendAllAsync();

            // ── 4. Package ────────────────────────────────────────────────
            plan.Status = MigrationStatus.PACKAGING;
            await _packager.CreatePackageAsync(plan);

            // ── 5. Deploy ─────────────────────────────────────────────────
            plan.Status = MigrationStatus.DEPLOYING;
            var deployed = await _deployer.DeployAsync(plan);

            if (!deployed)
            {
                plan.Status = MigrationStatus.FAILED;
                plan.Error  ??= "Deployment to target failed";
                return;
            }

            // ── Done ──────────────────────────────────────────────────────
            plan.Status      = MigrationStatus.COMPLETED;
            plan.CompletedAt = DateTime.UtcNow;
            ArchLogger.LogInfo(
                $"[MigrationEngine] Migration {plan.MigrationId} COMPLETED " +
                $"— package at {plan.PackagePath}");
        }
        catch (Exception ex)
        {
            plan.Status = MigrationStatus.FAILED;
            plan.Error  = ex.Message;
            ArchLogger.LogWarn(
                $"[MigrationEngine] Migration {plan.MigrationId} failed: {ex.Message}");
        }
    }
}
