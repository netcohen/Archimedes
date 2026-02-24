namespace Archimedes.Core;

public enum ApprovalState { Running, WaitingForUser, Completed }

public class PendingApproval
{
    public string TaskId { get; set; } = "";
    public string Message { get; set; } = "";
}
