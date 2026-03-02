using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Archimedes.Core;

public class EncryptedStore : IDisposable
{
    private readonly string _dbPath;
    private readonly string _keyPath;
    private SqliteConnection? _connection;
    private bool _disposed;

    public EncryptedStore(string? basePath = null)
    {
        var dir = basePath
            ?? Environment.GetEnvironmentVariable("ARCHIMEDES_DATA_PATH")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Archimedes"
            );
        Directory.CreateDirectory(dir);
        
        _dbPath = Path.Combine(dir, "archimedes.db");
        _keyPath = Path.Combine(dir, "archimedes.key");
    }

    public void Initialize()
    {
        SQLitePCL.Batteries_V2.Init();
        
        var dbKey = GetOrCreateKey();
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = dbKey
        }.ToString();
        
        _connection = new SqliteConnection(connStr);
        _connection.Open();
        
        CreateTables();
        ArchLogger.LogInfo($"EncryptedStore initialized at {_dbPath}");
    }

    private string GetOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var protectedKey = File.ReadAllBytes(_keyPath);
            var key = OsUnprotect(protectedKey);
            return Convert.ToBase64String(key);
        }

        var newKey = new byte[32];
        RandomNumberGenerator.Fill(newKey);
        var protected_ = OsProtect(newKey);
        File.WriteAllBytes(_keyPath, protected_);
        OsRestrictFilePermissions(_keyPath);

        var method = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "DPAPI" : "file permissions (chmod 600)";
        ArchLogger.LogInfo($"Generated new database encryption key ({method} protected)");
        return Convert.ToBase64String(newKey);
    }

    // ── Phase 23: cross-platform key protection ───────────────────────────────
    // Windows: DPAPI (DataProtectionScope.CurrentUser) — OS-managed, user-scoped
    // Linux:   raw bytes stored with chmod 600 — file permission is the security layer
    private static byte[] OsProtect(byte[] data) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)
            : data;

    private static byte[] OsUnprotect(byte[] data) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser)
            : data;

    private static void OsRestrictFilePermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            // chmod 600: owner read+write only
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName  = "chmod",
                Arguments = $"600 \"{path}\"",
                UseShellExecute = false
            })?.WaitForExit();
        }
        catch { /* best-effort — key is still functional without it */ }
    }

    private void CreateTables()
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                type TEXT NOT NULL,
                payload TEXT,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS runs (
                id TEXT PRIMARY KEY,
                job_id TEXT NOT NULL,
                status TEXT NOT NULL,
                step INTEGER DEFAULT 0,
                checkpoint TEXT,
                error TEXT,
                start_time TEXT,
                end_time TEXT,
                FOREIGN KEY (job_id) REFERENCES jobs(id)
            );

            CREATE TABLE IF NOT EXISTS outbox (
                id TEXT PRIMARY KEY,
                operation_id TEXT UNIQUE NOT NULL,
                payload TEXT NOT NULL,
                destination TEXT NOT NULL,
                status TEXT NOT NULL,
                attempts INTEGER DEFAULT 0,
                next_retry TEXT,
                error TEXT,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                last_attempt_at TEXT
            );

            CREATE TABLE IF NOT EXISTS approvals (
                task_id TEXT PRIMARY KEY,
                message TEXT,
                status TEXT NOT NULL,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                responded_at TEXT,
                approved INTEGER
            );

            CREATE TABLE IF NOT EXISTS dedup (
                operation_id TEXT PRIMARY KEY,
                result_id TEXT,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS tasks (
                task_id TEXT PRIMARY KEY,
                created_at TEXT NOT NULL,
                updated_at TEXT,
                title TEXT NOT NULL,
                user_prompt_encrypted TEXT,
                user_prompt_hash TEXT,
                user_prompt_length INTEGER DEFAULT 0,
                type TEXT NOT NULL,
                priority TEXT NOT NULL,
                schedule_json TEXT,
                state TEXT NOT NULL,
                current_step INTEGER DEFAULT 0,
                error TEXT,
                plan_json_encrypted TEXT,
                plan_hash TEXT,
                plan_version INTEGER DEFAULT 0,
                artifacts_encrypted TEXT,
                result_summary TEXT,
                waiting_for_approval_id TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_runs_status ON runs(status);
            CREATE INDEX IF NOT EXISTS idx_outbox_status ON outbox(status, next_retry);
            CREATE INDEX IF NOT EXISTS idx_outbox_operation ON outbox(operation_id);
            CREATE INDEX IF NOT EXISTS idx_tasks_state ON tasks(state);
            CREATE INDEX IF NOT EXISTS idx_tasks_priority ON tasks(priority);
        ";
        cmd.ExecuteNonQuery();
    }

    public void SaveJob(Job job)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO jobs (id, type, payload)
            VALUES (@id, @type, @payload)
        ";
        cmd.Parameters.AddWithValue("@id", job.Id);
        cmd.Parameters.AddWithValue("@type", job.Type);
        cmd.Parameters.AddWithValue("@payload", job.Payload);
        cmd.ExecuteNonQuery();
    }

    public Job? GetJob(string id)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT id, type, payload FROM jobs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new Job
            {
                Id = reader.GetString(0),
                Type = reader.GetString(1),
                Payload = reader.IsDBNull(2) ? "" : reader.GetString(2)
            };
        }
        return null;
    }

    public List<Job> GetAllJobs()
    {
        var jobs = new List<Job>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT id, type, payload FROM jobs";
        
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            jobs.Add(new Job
            {
                Id = reader.GetString(0),
                Type = reader.GetString(1),
                Payload = reader.IsDBNull(2) ? "" : reader.GetString(2)
            });
        }
        return jobs;
    }

    public void SaveRun(Run run)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO runs (id, job_id, status, step, checkpoint, error, start_time, end_time)
            VALUES (@id, @jobId, @status, @step, @checkpoint, @error, @startTime, @endTime)
        ";
        cmd.Parameters.AddWithValue("@id", run.Id);
        cmd.Parameters.AddWithValue("@jobId", run.JobId);
        cmd.Parameters.AddWithValue("@status", run.Status);
        cmd.Parameters.AddWithValue("@step", run.Step);
        cmd.Parameters.AddWithValue("@checkpoint", (object?)run.Checkpoint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@error", (object?)run.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@startTime", run.StartTime.ToString("O"));
        cmd.Parameters.AddWithValue("@endTime", run.EndTime?.ToString("O") ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public Run? GetRun(string id)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT id, job_id, status, step, checkpoint, error, start_time, end_time FROM runs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return ReadRun(reader);
        }
        return null;
    }

    public List<Run> GetRecoverableRuns()
    {
        var runs = new List<Run>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT id, job_id, status, step, checkpoint, error, start_time, end_time FROM runs WHERE status IN ('running', 'recovering')";
        
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            runs.Add(ReadRun(reader));
        }
        return runs;
    }

    public List<Run> GetAllRuns()
    {
        var runs = new List<Run>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT id, job_id, status, step, checkpoint, error, start_time, end_time FROM runs ORDER BY start_time DESC";
        
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            runs.Add(ReadRun(reader));
        }
        return runs;
    }

    private static Run ReadRun(SqliteDataReader reader)
    {
        return new Run
        {
            Id = reader.GetString(0),
            JobId = reader.GetString(1),
            Status = reader.GetString(2),
            Step = reader.GetInt32(3),
            Checkpoint = reader.IsDBNull(4) ? null : reader.GetString(4),
            Error = reader.IsDBNull(5) ? null : reader.GetString(5),
            StartTime = DateTime.Parse(reader.GetString(6)),
            EndTime = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7))
        };
    }

    public void SaveOutboxEntry(OutboxEntry entry)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO outbox (id, operation_id, payload, destination, status, attempts, next_retry, error, last_attempt_at)
            VALUES (@id, @operationId, @payload, @destination, @status, @attempts, @nextRetry, @error, @lastAttemptAt)
        ";
        cmd.Parameters.AddWithValue("@id", entry.Id);
        cmd.Parameters.AddWithValue("@operationId", entry.OperationId);
        cmd.Parameters.AddWithValue("@payload", entry.Payload);
        cmd.Parameters.AddWithValue("@destination", entry.Destination);
        cmd.Parameters.AddWithValue("@status", entry.Status.ToString());
        cmd.Parameters.AddWithValue("@attempts", entry.Attempts);
        cmd.Parameters.AddWithValue("@nextRetry", entry.NextRetry?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@error", (object?)entry.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastAttemptAt", entry.LastAttemptAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public string? CheckDuplicateOperation(string operationId)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT id FROM outbox WHERE operation_id = @operationId";
        cmd.Parameters.AddWithValue("@operationId", operationId);
        
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return reader.GetString(0);
        }
        return null;
    }

    public List<OutboxEntry> GetPendingOutboxEntries()
    {
        var entries = new List<OutboxEntry>();
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            SELECT id, operation_id, payload, destination, status, attempts, next_retry, error, last_attempt_at
            FROM outbox 
            WHERE status = 'PENDING' AND (next_retry IS NULL OR next_retry <= @now)
            ORDER BY created_at
        ";
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadOutboxEntry(reader));
        }
        return entries;
    }

    private static OutboxEntry ReadOutboxEntry(SqliteDataReader reader)
    {
        return new OutboxEntry
        {
            Id = reader.GetString(0),
            OperationId = reader.GetString(1),
            Payload = reader.GetString(2),
            Destination = reader.GetString(3),
            Status = Enum.Parse<OutboxStatus>(reader.GetString(4)),
            Attempts = reader.GetInt32(5),
            NextRetry = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)),
            Error = reader.IsDBNull(7) ? null : reader.GetString(7),
            LastAttemptAt = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8))
        };
    }

    public void SaveTask(AgentTask task)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO tasks (
                task_id, created_at, updated_at, title, 
                user_prompt_encrypted, user_prompt_hash, user_prompt_length,
                type, priority, schedule_json, state, current_step, error,
                plan_json_encrypted, plan_hash, plan_version,
                artifacts_encrypted, result_summary, waiting_for_approval_id
            ) VALUES (
                @taskId, @createdAt, @updatedAt, @title,
                @userPromptEncrypted, @userPromptHash, @userPromptLength,
                @type, @priority, @scheduleJson, @state, @currentStep, @error,
                @planJsonEncrypted, @planHash, @planVersion,
                @artifactsEncrypted, @resultSummary, @waitingForApprovalId
            )
        ";
        cmd.Parameters.AddWithValue("@taskId", task.TaskId);
        cmd.Parameters.AddWithValue("@createdAt", task.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@updatedAt", task.UpdatedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@title", task.Title);
        cmd.Parameters.AddWithValue("@userPromptEncrypted", (object?)task.UserPromptEncrypted ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@userPromptHash", (object?)task.UserPromptHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@userPromptLength", task.UserPromptLength);
        cmd.Parameters.AddWithValue("@type", task.Type.ToString());
        cmd.Parameters.AddWithValue("@priority", task.Priority.ToString());
        cmd.Parameters.AddWithValue("@scheduleJson", task.Schedule != null ? 
            System.Text.Json.JsonSerializer.Serialize(task.Schedule) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@state", task.State.ToString());
        cmd.Parameters.AddWithValue("@currentStep", task.CurrentStep);
        cmd.Parameters.AddWithValue("@error", (object?)task.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@planJsonEncrypted", (object?)task.PlanJsonEncrypted ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@planHash", (object?)task.PlanHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@planVersion", task.PlanVersion);
        cmd.Parameters.AddWithValue("@artifactsEncrypted", (object?)task.ArtifactsEncrypted ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@resultSummary", (object?)task.ResultSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@waitingForApprovalId", (object?)task.WaitingForApprovalId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public AgentTask? GetTask(string taskId)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            SELECT task_id, created_at, updated_at, title,
                   user_prompt_encrypted, user_prompt_hash, user_prompt_length,
                   type, priority, schedule_json, state, current_step, error,
                   plan_json_encrypted, plan_hash, plan_version,
                   artifacts_encrypted, result_summary, waiting_for_approval_id
            FROM tasks WHERE task_id = @taskId
        ";
        cmd.Parameters.AddWithValue("@taskId", taskId);
        
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return ReadTask(reader);
        }
        return null;
    }

    public List<AgentTask> GetTasks(TaskState? stateFilter = null)
    {
        var tasks = new List<AgentTask>();
        using var cmd = _connection!.CreateCommand();
        
        if (stateFilter.HasValue)
        {
            cmd.CommandText = @"
                SELECT task_id, created_at, updated_at, title,
                       user_prompt_encrypted, user_prompt_hash, user_prompt_length,
                       type, priority, schedule_json, state, current_step, error,
                       plan_json_encrypted, plan_hash, plan_version,
                       artifacts_encrypted, result_summary, waiting_for_approval_id
                FROM tasks WHERE state = @state ORDER BY created_at DESC
            ";
            cmd.Parameters.AddWithValue("@state", stateFilter.Value.ToString());
        }
        else
        {
            cmd.CommandText = @"
                SELECT task_id, created_at, updated_at, title,
                       user_prompt_encrypted, user_prompt_hash, user_prompt_length,
                       type, priority, schedule_json, state, current_step, error,
                       plan_json_encrypted, plan_hash, plan_version,
                       artifacts_encrypted, result_summary, waiting_for_approval_id
                FROM tasks ORDER BY created_at DESC
            ";
        }
        
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tasks.Add(ReadTask(reader));
        }
        return tasks;
    }

    private static AgentTask ReadTask(SqliteDataReader reader)
    {
        var task = new AgentTask
        {
            TaskId = reader.GetString(0),
            CreatedAtUtc = DateTime.Parse(reader.GetString(1)),
            UpdatedAtUtc = reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2)),
            Title = reader.GetString(3),
            UserPromptEncrypted = reader.IsDBNull(4) ? null : reader.GetString(4),
            UserPromptHash = reader.IsDBNull(5) ? null : reader.GetString(5),
            UserPromptLength = reader.GetInt32(6),
            CurrentStep = reader.GetInt32(11),
            Error = reader.IsDBNull(12) ? null : reader.GetString(12),
            PlanJsonEncrypted = reader.IsDBNull(13) ? null : reader.GetString(13),
            PlanHash = reader.IsDBNull(14) ? null : reader.GetString(14),
            PlanVersion = reader.GetInt32(15),
            ArtifactsEncrypted = reader.IsDBNull(16) ? null : reader.GetString(16),
            ResultSummary = reader.IsDBNull(17) ? null : reader.GetString(17),
            WaitingForApprovalId = reader.IsDBNull(18) ? null : reader.GetString(18)
        };
        
        if (Enum.TryParse<TaskType>(reader.GetString(7), out var type))
            task.Type = type;
        if (Enum.TryParse<TaskPriority>(reader.GetString(8), out var priority))
            task.Priority = priority;
        if (!reader.IsDBNull(9))
            task.Schedule = System.Text.Json.JsonSerializer.Deserialize<TaskSchedule>(reader.GetString(9));
        if (Enum.TryParse<TaskState>(reader.GetString(10), out var state))
            task.State = state;
        
        return task;
    }

    public int GetTaskCount(TaskState? stateFilter = null)
    {
        using var cmd = _connection!.CreateCommand();
        if (stateFilter.HasValue)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM tasks WHERE state = @state";
            cmd.Parameters.AddWithValue("@state", stateFilter.Value.ToString());
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM tasks";
        }
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public bool IsEncrypted()
    {
        try
        {
            var testBytes = File.ReadAllBytes(_dbPath);
            var headerStr = System.Text.Encoding.ASCII.GetString(testBytes.Take(16).ToArray());
            return !headerStr.StartsWith("SQLite format 3");
        }
        catch
        {
            return true;
        }
    }

    public StoreStats GetStats()
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                (SELECT COUNT(*) FROM jobs) as jobs,
                (SELECT COUNT(*) FROM runs) as runs,
                (SELECT COUNT(*) FROM outbox) as outbox
        ";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        
        return new StoreStats
        {
            Jobs = reader.GetInt32(0),
            Runs = reader.GetInt32(1),
            Outbox = reader.GetInt32(2),
            IsEncrypted = IsEncrypted()
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _connection?.Close();
        _connection?.Dispose();
        _disposed = true;
    }
}

public class StoreStats
{
    public int Jobs { get; set; }
    public int Runs { get; set; }
    public int Outbox { get; set; }
    public bool IsEncrypted { get; set; }
}
