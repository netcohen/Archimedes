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
    public string Status { get; set; } = "running"; // running, completed, failed
}
