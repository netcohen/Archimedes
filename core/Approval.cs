namespace Archimedes.Core;

public enum TaskState { Running, WaitingForUser, Completed }

public class PendingApproval
{
    public string TaskId { get; set; } = "";
    public string Message { get; set; } = "";
}
