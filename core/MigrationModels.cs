namespace Archimedes.Core;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum MigrationStatus
{
    IDLE,
    CHECKING_DISK,
    SUSPENDING_TASKS,
    PACKAGING,
    DEPLOYING,
    COMPLETED,
    FAILED
}

public enum MigrationTargetType
{
    LOCAL_PATH,   // local directory or UNC share (\\server\share)
    HTTP_URL      // target Archimedes instance   (http://host:5051)
}

public enum TaskMigrationAction
{
    SUSPEND,      // paused — will resume on target machine
    ABANDON       // was unrecoverable; will not resume
}

// ── Request model ─────────────────────────────────────────────────────────────

public class StartMigrationRequest
{
    public string  TargetPath      { get; set; } = "";
    public string? TargetType      { get; set; }   // "LOCAL_PATH" (default) or "HTTP_URL"
    public bool    DryRun          { get; set; }   // estimate only, do not package

    /// <summary>
    /// Maximum zip size per volume in MB.  0 (default) = single zip, no split.
    /// Use when the transfer medium (USB drive, etc.) has limited capacity.
    /// Example: 4_000_000 = 4 TB per volume.
    /// </summary>
    public long MaxVolumeSizeMB { get; set; }
}

// ── Migration plan ────────────────────────────────────────────────────────────

/// <summary>
/// Live state of one migration run.
/// Stored in-memory by MigrationEngine; polled via GET /migration/status/{id}.
/// </summary>
public class MigrationPlan
{
    public string              MigrationId    { get; set; } = Guid.NewGuid().ToString("N")[..16];
    public MigrationStatus     Status         { get; set; } = MigrationStatus.IDLE;
    public MigrationTargetType TargetType     { get; set; } = MigrationTargetType.LOCAL_PATH;
    public string              TargetPath     { get; set; } = "";
    public bool                DryRun         { get; set; }

    public long RequiredDiskMB  { get; set; }
    public long AvailableDiskMB { get; set; }

    /// <summary>Full path to the created zip file (set after PACKAGING step).</summary>
    public string? PackagePath  { get; set; }
    public string? Error        { get; set; }

    public List<TaskMigrationDecision> TaskDecisions { get; set; } = new();

    /// <summary>Max volume size in MB (0 = single zip, no split).</summary>
    public long         MaxVolumeSizeMB { get; set; }
    /// <summary>Number of zip volumes created (1 for single zip).</summary>
    public int          VolumeCount     { get; set; } = 1;
    /// <summary>Full paths of all volume zips (set after PACKAGING step).</summary>
    public List<string> PackagePaths    { get; set; } = new();

    public DateTime  StartedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

// ── Per-task migration decision ───────────────────────────────────────────────

public class TaskMigrationDecision
{
    public string              TaskId      { get; set; } = "";
    public string              Title       { get; set; } = "";
    public TaskState           StateBefore { get; set; }
    public int                 StepBefore  { get; set; }
    public TaskPriority        Priority    { get; set; }
    public TaskMigrationAction Action      { get; set; } = TaskMigrationAction.SUSPEND;
}

// ── Continuation log (embedded in the zip) ────────────────────────────────────

/// <summary>
/// Serialised inside every migration package as "continuation_log.json".
/// Contains everything needed to resume on the target machine:
///   - which tasks were paused and at what step
///   - which goals were active
///   - raw (unprotected) key material for cross-machine key re-protection
///
/// SECURITY: RawDbKeyBase64 and RawDeviceKeysBase64 contain unprotected
///           cryptographic key material.  Protect the zip accordingly
///           during transfer (e.g. encrypt, use a secure channel).
/// </summary>
public class MigrationContinuationLog
{
    public string   MigrationId       { get; set; } = "";
    public string   SourceMachine     { get; set; } = Environment.MachineName;
    public string   ArchimedesVersion { get; set; } = "0.28.0";
    public DateTime PackagedAt        { get; set; } = DateTime.UtcNow;

    /// <summary>Tasks that were in-flight and should resume on the target.</summary>
    public List<TaskMigrationDecision> ResumableTasks { get; set; } = new();

    /// <summary>Goal IDs that were ACTIVE / MONITORING at time of packaging.</summary>
    public List<string> ActiveGoalIds { get; set; } = new();

    // ── SENSITIVE: key material ──────────────────────────────────────────────

    /// <summary>Raw (unprotected) SQLite database password — Base64-encoded.</summary>
    public string? RawDbKeyBase64      { get; set; }

    /// <summary>Raw (unprotected) X25519 device key pair (64 bytes) — Base64-encoded.</summary>
    public string? RawDeviceKeysBase64 { get; set; }

    /// <summary>
    /// Phase 35: Always true for machine migrations.
    /// Signals the resume engine to write a 24-hour CodePatcher hold marker on the
    /// target machine — prevents autonomous code patching until the environment
    /// has been verified stable on the new host.
    /// </summary>
    public bool NewMachineBootstrap { get; set; } = true;
}

// ── Disk check result ─────────────────────────────────────────────────────────

public class MigrationDiskCheckResult
{
    public bool    IsEnough    { get; set; }
    public long    AvailableMB { get; set; }
    public long    RequiredMB  { get; set; }
    public string? Error       { get; set; }
}

// ── Pre-flight size estimate (GET /migration/estimate) ────────────────────────

/// <summary>
/// Returned by GET /migration/estimate.
/// Tells the user how much storage to prepare before starting migration.
/// </summary>
public class MigrationSizeEstimate
{
    /// <summary>Total raw (uncompressed) size of packaged files in MB.</summary>
    public long RawDataMB          { get; set; }
    /// <summary>Estimated compressed package size in MB (≈ 80 % of raw + 10 MB).</summary>
    public long EstimatedPackageMB { get; set; }
    /// <summary>Recommended minimum drive size (EstimatedPackageMB × 1.20 safety).</summary>
    public long RecommendedDriveMB { get; set; }
    /// <summary>Per-component breakdown for informational display.</summary>
    public MigrationSizeBreakdown Breakdown { get; set; } = new();
}

public class MigrationSizeBreakdown
{
    public long DatabaseMB   { get; set; }   // archimedes.db
    public long ProceduresMB { get; set; }   // procedures/*.json
    public long ToolStoreMB  { get; set; }   // acquired_tools.json + tool_gaps.json
    public long GoalsMB      { get; set; }   // goals.json
    public long OtherMB      { get; set; }   // legal_approvals.json, source_intelligence.json
}

// ── Multi-volume manifest (embedded in every split-volume zip) ────────────────

/// <summary>
/// Written as "volume_manifest.json" inside each volume zip when split mode is used.
/// The resume engine reads this to know how many volumes belong to this migration.
/// </summary>
public class MigrationVolumeManifest
{
    public string MigrationId      { get; set; } = "";
    public int    TotalVolumes     { get; set; }
    public int    VolumeIndex      { get; set; }   // 1-based
    public long   TotalEstimatedMB { get; set; }
}
