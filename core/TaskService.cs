using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Archimedes.Core;

public class TaskService
{
    private readonly EncryptedStore _store;
    private readonly DeviceKeyManager _keyManager;
    
    public TaskService(EncryptedStore store, DeviceKeyManager keyManager)
    {
        _store = store;
        _keyManager = keyManager;
    }
    
    public AgentTask CreateTask(CreateTaskRequest request)
    {
        var task = new AgentTask
        {
            Title = request.Title,
            UserPromptLength = request.UserPrompt.Length,
            UserPromptHash = AgentTask.HashPrompt(request.UserPrompt),
            Schedule = request.Schedule
        };
        
        if (Enum.TryParse<TaskType>(request.Type, true, out var type))
            task.Type = type;
        if (Enum.TryParse<TaskPriority>(request.Priority, true, out var priority))
            task.Priority = priority;
        
        task.UserPromptEncrypted = EncryptString(request.UserPrompt);
        
        _store.SaveTask(task);
        ArchLogger.LogInfo($"Task created: id={task.TaskId} title={Redactor.Redact(task.Title)}");
        
        return task;
    }
    
    public AgentTask? GetTask(string taskId)
    {
        return _store.GetTask(taskId);
    }
    
    public List<AgentTask> GetTasks(TaskState? stateFilter = null)
    {
        return _store.GetTasks(stateFilter);
    }
    
    public AgentTask? SetPlan(string taskId, TaskPlanRequest request)
    {
        var task = _store.GetTask(taskId);
        if (task == null) return null;
        
        if (task.State != TaskState.QUEUED && task.State != TaskState.PLANNING)
        {
            throw new InvalidOperationException($"Cannot set plan in state {task.State}");
        }
        
        var plan = new TaskPlan
        {
            Intent = request.Intent,
            Steps = request.Steps ?? new List<PlanStep>()
        };
        plan.ComputeHash();
        
        task.State = TaskState.PLANNING;
        task.PlanJsonEncrypted = EncryptString(JsonSerializer.Serialize(plan));
        task.PlanHash = plan.Hash;
        task.PlanVersion = plan.Version;
        task.UpdatedAtUtc = DateTime.UtcNow;
        
        _store.SaveTask(task);
        ArchLogger.LogInfo($"Task plan set: id={taskId} hash={task.PlanHash} steps={plan.Steps.Count}");
        
        return task;
    }
    
    public AgentTask? StartRun(string taskId)
    {
        var task = _store.GetTask(taskId);
        if (task == null) return null;
        
        if (task.State != TaskState.QUEUED && task.State != TaskState.PLANNING && task.State != TaskState.PAUSED)
        {
            throw new InvalidOperationException($"Cannot run in state {task.State}");
        }
        
        task.State = TaskState.RUNNING;
        task.UpdatedAtUtc = DateTime.UtcNow;
        
        _store.SaveTask(task);
        ArchLogger.LogInfo($"Task running: id={taskId}");
        
        return task;
    }
    
    public AgentTask? Pause(string taskId)
    {
        var task = _store.GetTask(taskId);
        if (task == null) return null;
        
        if (task.State != TaskState.RUNNING)
        {
            throw new InvalidOperationException($"Cannot pause in state {task.State}");
        }
        
        task.State = TaskState.PAUSED;
        task.UpdatedAtUtc = DateTime.UtcNow;
        
        _store.SaveTask(task);
        ArchLogger.LogInfo($"Task paused: id={taskId}");
        
        return task;
    }
    
    public AgentTask? Resume(string taskId)
    {
        var task = _store.GetTask(taskId);
        if (task == null) return null;
        
        if (task.State != TaskState.PAUSED && 
            task.State != TaskState.WAITING_APPROVAL &&
            task.State != TaskState.WAITING_SECRET &&
            task.State != TaskState.WAITING_CAPTCHA)
        {
            throw new InvalidOperationException($"Cannot resume in state {task.State}");
        }
        
        task.State = TaskState.RUNNING;
        task.WaitingForApprovalId = null;
        task.UpdatedAtUtc = DateTime.UtcNow;
        
        _store.SaveTask(task);
        ArchLogger.LogInfo($"Task resumed: id={taskId}");
        
        return task;
    }
    
    public AgentTask? Cancel(string taskId)
    {
        var task = _store.GetTask(taskId);
        if (task == null) return null;
        
        if (task.State == TaskState.DONE || task.State == TaskState.FAILED)
        {
            throw new InvalidOperationException($"Cannot cancel in state {task.State}");
        }
        
        task.State = TaskState.FAILED;
        task.Error = "Cancelled by user";
        task.UpdatedAtUtc = DateTime.UtcNow;
        
        _store.SaveTask(task);
        ArchLogger.LogInfo($"Task cancelled: id={taskId}");
        
        return task;
    }
    
    public AgentTask? Complete(string taskId, string? summary = null, string? structuredResult = null)
    {
        var task = _store.GetTask(taskId);
        if (task == null) return null;
        
        task.State = TaskState.DONE;
        task.ResultSummary = summary;
        task.UpdatedAtUtc = DateTime.UtcNow;
        
        if (structuredResult != null)
        {
            task.ArtifactsEncrypted = EncryptString(structuredResult);
        }
        
        _store.SaveTask(task);
        ArchLogger.LogInfo($"Task completed: id={taskId}");
        
        return task;
    }
    
    public AgentTask? Fail(string taskId, string error)
    {
        var task = _store.GetTask(taskId);
        if (task == null) return null;
        
        task.State = TaskState.FAILED;
        task.Error = error;
        task.UpdatedAtUtc = DateTime.UtcNow;
        
        _store.SaveTask(task);
        ArchLogger.LogInfo($"Task failed: id={taskId} error={Redactor.Redact(error)}");
        
        return task;
    }
    
    public AgentTask? SetWaitingApproval(string taskId, string approvalId)
    {
        var task = _store.GetTask(taskId);
        if (task == null) return null;
        
        task.State = TaskState.WAITING_APPROVAL;
        task.WaitingForApprovalId = approvalId;
        task.UpdatedAtUtc = DateTime.UtcNow;
        
        _store.SaveTask(task);
        return task;
    }
    
    public AgentTask? SetWaitingSecret(string taskId, string approvalId)
    {
        var task = _store.GetTask(taskId);
        if (task == null) return null;
        
        task.State = TaskState.WAITING_SECRET;
        task.WaitingForApprovalId = approvalId;
        task.UpdatedAtUtc = DateTime.UtcNow;
        
        _store.SaveTask(task);
        return task;
    }
    
    public AgentTask? SetWaitingCaptcha(string taskId, string approvalId)
    {
        var task = _store.GetTask(taskId);
        if (task == null) return null;
        
        task.State = TaskState.WAITING_CAPTCHA;
        task.WaitingForApprovalId = approvalId;
        task.UpdatedAtUtc = DateTime.UtcNow;
        
        _store.SaveTask(task);
        return task;
    }
    
    public AgentTask? UpdateStep(string taskId, int step)
    {
        var task = _store.GetTask(taskId);
        if (task == null) return null;
        
        task.CurrentStep = step;
        task.UpdatedAtUtc = DateTime.UtcNow;
        
        _store.SaveTask(task);
        return task;
    }
    
    public TaskPlan? GetPlan(string taskId)
    {
        var task = _store.GetTask(taskId);
        if (task?.PlanJsonEncrypted == null) return null;
        
        var planJson = DecryptString(task.PlanJsonEncrypted);
        return JsonSerializer.Deserialize<TaskPlan>(planJson);
    }
    
    public string? GetUserPrompt(string taskId)
    {
        var task = _store.GetTask(taskId);
        if (task?.UserPromptEncrypted == null) return null;
        
        return DecryptString(task.UserPromptEncrypted);
    }
    
    public List<AgentTask> GetRecoverableTasks()
    {
        return _store.GetTasks(null)
            .Where(t => t.State == TaskState.RUNNING || 
                       t.State == TaskState.PLANNING ||
                       t.State == TaskState.WAITING_APPROVAL ||
                       t.State == TaskState.WAITING_SECRET ||
                       t.State == TaskState.WAITING_CAPTCHA)
            .ToList();
    }
    
    private string EncryptString(string plaintext)
    {
        var keys = _keyManager.GetOrCreateKeyPair();
        return ModernCrypto.EncryptToJson(
            plaintext,
            keys.PublicKey,
            "local",
            Guid.NewGuid().ToString("N")
        );
    }
    
    private string DecryptString(string envelopeJson)
    {
        var keys = _keyManager.GetOrCreateKeyPair();
        return ModernCrypto.DecryptFromJson(envelopeJson, keys.PrivateKey);
    }
}
