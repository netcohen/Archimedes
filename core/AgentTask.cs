using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Archimedes.Core;

public enum TaskType
{
    ONE_SHOT,
    MONITORING,
    RECURRING
}

public enum TaskPriority
{
    IMMEDIATE,
    SCHEDULED,
    BACKGROUND
}

public enum TaskState
{
    QUEUED,
    PLANNING,
    WAITING_APPROVAL,
    WAITING_SECRET,
    WAITING_CAPTCHA,
    RUNNING,
    PAUSED,
    DONE,
    FAILED
}

public class TaskSchedule
{
    public string? CronExpression { get; set; }
    public DateTime? NextRunAt { get; set; }
    public TimeSpan? Interval { get; set; }
}

public class TaskPlan
{
    public int Version { get; set; } = 1;
    public string? Intent { get; set; }
    public List<PlanStep> Steps { get; set; } = new();
    public string? Hash { get; set; }
    
    public void ComputeHash()
    {
        var json = JsonSerializer.Serialize(Steps);
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
        Hash = Convert.ToHexString(bytes)[..16];
    }
}

public class PlanStep
{
    public int Index { get; set; }
    public string Action { get; set; } = "";
    public Dictionary<string, object>? Params { get; set; }
    public string? Description { get; set; }
}

public class TaskArtifacts
{
    public string? ResultSummary { get; set; }
    public string? StructuredResultJson { get; set; }
    public List<string> Files { get; set; } = new();
}

public class AgentTask
{
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    
    public string Title { get; set; } = "";
    
    [JsonIgnore]
    public string? UserPromptEncrypted { get; set; }
    
    public string? UserPromptHash { get; set; }
    public int UserPromptLength { get; set; }
    
    public TaskType Type { get; set; } = TaskType.ONE_SHOT;
    public TaskPriority Priority { get; set; } = TaskPriority.IMMEDIATE;
    public TaskSchedule? Schedule { get; set; }
    
    public TaskState State { get; set; } = TaskState.QUEUED;
    public int CurrentStep { get; set; }
    public string? Error { get; set; }
    
    [JsonIgnore]
    public string? PlanJsonEncrypted { get; set; }
    
    public string? PlanHash { get; set; }
    public int PlanVersion { get; set; }
    
    [JsonIgnore]
    public string? ArtifactsEncrypted { get; set; }
    
    public string? ResultSummary { get; set; }
    
    public string? WaitingForApprovalId { get; set; }
    
    public static string HashPrompt(string prompt)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(prompt));
        return Convert.ToHexString(bytes)[..16];
    }
}

public class CreateTaskRequest
{
    public string Title { get; set; } = "";
    public string UserPrompt { get; set; } = "";
    public string? Type { get; set; }
    public string? Priority { get; set; }
    public TaskSchedule? Schedule { get; set; }
}

public class TaskResponse
{
    public string TaskId { get; set; } = "";
    public string Title { get; set; } = "";
    public string State { get; set; } = "";
    public string Type { get; set; } = "";
    public string Priority { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public int CurrentStep { get; set; }
    public string? PlanHash { get; set; }
    public int? PlanVersion { get; set; }
    public string? ResultSummary { get; set; }
    public string? Error { get; set; }
    public string? WaitingForApprovalId { get; set; }
    public string? UserPromptHash { get; set; }
    public int? UserPromptLength { get; set; }
    
    public static TaskResponse FromTask(AgentTask task)
    {
        return new TaskResponse
        {
            TaskId = task.TaskId,
            Title = task.Title,
            State = task.State.ToString(),
            Type = task.Type.ToString(),
            Priority = task.Priority.ToString(),
            CreatedAtUtc = task.CreatedAtUtc,
            UpdatedAtUtc = task.UpdatedAtUtc,
            CurrentStep = task.CurrentStep,
            PlanHash = task.PlanHash,
            PlanVersion = task.PlanVersion > 0 ? task.PlanVersion : null,
            ResultSummary = task.ResultSummary,
            Error = task.Error,
            WaitingForApprovalId = task.WaitingForApprovalId,
            UserPromptHash = task.UserPromptHash,
            UserPromptLength = task.UserPromptLength > 0 ? task.UserPromptLength : null
        };
    }
}

public class TaskPlanRequest
{
    public string? Intent { get; set; }
    public List<PlanStep>? Steps { get; set; }
}
