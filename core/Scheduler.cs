namespace Archimedes.Core;

public class Job
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "one-shot";
    public string Payload { get; set; } = "";
}

public class Run
{
    public string Id { get; set; } = "";
    public string JobId { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string Status { get; set; } = "running";
    public int Step { get; set; } = 0;
    public string? Checkpoint { get; set; }
    public string? Error { get; set; }
}

public static class RunStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Paused = "paused";
    public const string Recovering = "recovering";
}
