using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Archimedes.Core;

/// <summary>
/// Episodic memory — every chat interaction is stored and recalled.
///
/// This is the long-term learning layer:
///   User asks something → Archimedes acts → outcome stored
///   Next time a similar question appears → relevant past events injected into prompt
///   Archimedes learns from experience without retraining the model.
///
/// Stored per event:
///   - userMessage    : what the user asked
///   - command        : bash command that was run (or "none")
///   - reply          : Archimedes' Hebrew reply
///   - output         : command stdout/stderr (first 500 chars)
///   - success        : did the command exit 0?
///   - tags           : extracted keywords for fast relevance lookup
///   - timestamp      : when it happened
///
/// Relevance matching: simple keyword overlap (no vectors needed for 8B model scale).
/// Injected into system prompt as: MEMORY: [past events] so LLM has context.
/// </summary>
public class EventMemory
{
    private readonly string _dbPath;
    private SqliteConnection? _conn;
    private readonly object  _lock = new();

    public EventMemory(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        _dbPath = Path.Combine(dataRoot, "event_memory.db");
    }

    public void Initialize()
    {
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS events (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                ts           TEXT    NOT NULL,
                user_message TEXT    NOT NULL,
                command      TEXT    NOT NULL DEFAULT 'none',
                reply        TEXT    NOT NULL DEFAULT '',
                output       TEXT    NOT NULL DEFAULT '',
                success      INTEGER NOT NULL DEFAULT 1,
                tags         TEXT    NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_events_ts ON events(ts DESC);
        ";
        cmd.ExecuteNonQuery();
        ArchLogger.LogInfo("[EventMemory] Initialized");
    }

    // ── Save ────────────────────────────────────────────────────────────────

    public void Save(MemoryEvent ev)
    {
        if (_conn == null) return;
        lock (_lock)
        {
            try
            {
                var tags = ExtractTags(ev.UserMessage + " " + ev.Command);
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO events (ts, user_message, command, reply, output, success, tags)
                    VALUES (@ts, @um, @cmd, @reply, @out, @ok, @tags)";
                cmd.Parameters.AddWithValue("@ts",    ev.Timestamp.ToString("o"));
                cmd.Parameters.AddWithValue("@um",    ev.UserMessage[..Math.Min(500, ev.UserMessage.Length)]);
                cmd.Parameters.AddWithValue("@cmd",   ev.Command);
                cmd.Parameters.AddWithValue("@reply", ev.Reply[..Math.Min(500, ev.Reply.Length)]);
                cmd.Parameters.AddWithValue("@out",   ev.Output[..Math.Min(500, ev.Output.Length)]);
                cmd.Parameters.AddWithValue("@ok",    ev.Success ? 1 : 0);
                cmd.Parameters.AddWithValue("@tags",  tags);
                cmd.ExecuteNonQuery();

                // Keep DB lean — max 200 events
                PruneOldEvents();
            }
            catch (Exception ex)
            {
                ArchLogger.LogWarn($"[EventMemory] Save failed: {ex.Message}");
            }
        }
    }

    // ── Recall ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns up to <paramref name="limit"/> past events relevant to the query.
    /// Relevance = keyword overlap between query tags and stored event tags.
    /// Falls back to most-recent events if nothing relevant found.
    /// </summary>
    public List<MemoryEvent> Recall(string query, int limit = 4)
    {
        if (_conn == null) return new();
        lock (_lock)
        {
            try
            {
                var queryTags = ExtractTags(query).Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                // Load last 100 events, score them, return top N
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT ts, user_message, command, reply, output, success, tags
                    FROM events ORDER BY id DESC LIMIT 100";

                var rows = new List<(int score, MemoryEvent ev)>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var storedTags = (reader.GetString(6) ?? "").Split(',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var score = queryTags.Count(qt => storedTags.Contains(qt));

                    rows.Add((score, new MemoryEvent
                    {
                        Timestamp   = DateTime.Parse(reader.GetString(0)),
                        UserMessage = reader.GetString(1),
                        Command     = reader.GetString(2),
                        Reply       = reader.GetString(3),
                        Output      = reader.GetString(4),
                        Success     = reader.GetInt64(5) == 1
                    }));
                }

                // Prefer relevant events; fall back to recency
                var relevant = rows
                    .Where(r => r.score > 0)
                    .OrderByDescending(r => r.score)
                    .ThenByDescending(r => r.ev.Timestamp)
                    .Take(limit)
                    .Select(r => r.ev)
                    .ToList();

                if (relevant.Count == 0)
                    relevant = rows.Take(limit).Select(r => r.ev).ToList();

                return relevant;
            }
            catch (Exception ex)
            {
                ArchLogger.LogWarn($"[EventMemory] Recall failed: {ex.Message}");
                return new();
            }
        }
    }

    /// <summary>
    /// Formats recalled events as a compact memory block for injection into the system prompt.
    /// </summary>
    public static string FormatForPrompt(List<MemoryEvent> events)
    {
        if (events.Count == 0) return "";

        var lines = new System.Text.StringBuilder();
        lines.AppendLine("MEMORY (past interactions — use to make better decisions):");
        foreach (var e in events)
        {
            var age    = FormatAge(e.Timestamp);
            var status = e.Success ? "✓" : "✗";
            var cmd    = e.Command == "none" ? "" : $" → `{e.Command}`";
            lines.AppendLine($"  [{age}] {status} \"{e.UserMessage}\"{cmd}");
            if (!e.Success && !string.IsNullOrEmpty(e.Output))
                lines.AppendLine($"    error: {e.Output[..Math.Min(120, e.Output.Length)]}");
        }
        return lines.ToString().TrimEnd();
    }

    // ── Stats (for self-improvement engine) ─────────────────────────────────

    public MemoryStats GetStats()
    {
        if (_conn == null) return new();
        lock (_lock)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(*),
                           SUM(CASE WHEN success=1 THEN 1 ELSE 0 END),
                           SUM(CASE WHEN success=0 THEN 1 ELSE 0 END)
                    FROM events";
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return new();
                return new MemoryStats
                {
                    Total   = (int)r.GetInt64(0),
                    Success = (int)r.GetInt64(1),
                    Failure = (int)r.GetInt64(2)
                };
            }
            catch { return new(); }
        }
    }

    public List<MemoryEvent> GetFailures(int limit = 20)
    {
        if (_conn == null) return new();
        lock (_lock)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT ts, user_message, command, reply, output, success, tags
                    FROM events WHERE success=0 ORDER BY id DESC LIMIT @limit";
                cmd.Parameters.AddWithValue("@limit", limit);
                var result = new List<MemoryEvent>();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add(new MemoryEvent
                    {
                        Timestamp   = DateTime.Parse(r.GetString(0)),
                        UserMessage = r.GetString(1),
                        Command     = r.GetString(2),
                        Reply       = r.GetString(3),
                        Output      = r.GetString(4),
                        Success     = false
                    });
                return result;
            }
            catch { return new(); }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string ExtractTags(string text)
    {
        var lower = text.ToLowerInvariant();
        // Tokenise: keep alphanumeric + Hebrew, 3+ chars, deduplicate
        var tokens = System.Text.RegularExpressions.Regex
            .Matches(lower, @"[\w\u05D0-\u05EA]{3,}")
            .Select(m => m.Value)
            .Distinct()
            .Take(20);
        return string.Join(",", tokens);
    }

    private void PruneOldEvents()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM events WHERE id NOT IN (
                SELECT id FROM events ORDER BY id DESC LIMIT 200
            )";
        cmd.ExecuteNonQuery();
    }

    private static string FormatAge(DateTime ts)
    {
        var diff = DateTime.UtcNow - ts.ToUniversalTime();
        if (diff.TotalMinutes < 60)  return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours   < 24)  return $"{(int)diff.TotalHours}h ago";
        return $"{(int)diff.TotalDays}d ago";
    }
}

public class MemoryEvent
{
    public DateTime Timestamp   { get; set; } = DateTime.UtcNow;
    public string   UserMessage { get; set; } = "";
    public string   Command     { get; set; } = "none";
    public string   Reply       { get; set; } = "";
    public string   Output      { get; set; } = "";
    public bool     Success     { get; set; } = true;
}

public class MemoryStats
{
    public int Total   { get; set; }
    public int Success { get; set; }
    public int Failure { get; set; }
    public double SuccessRate => Total == 0 ? 1.0 : (double)Success / Total;
}
