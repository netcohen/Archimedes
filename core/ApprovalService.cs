using System.Text.Json;

namespace Archimedes.Core;

public enum ApprovalType
{
    CONFIRMATION,
    SECRET_INPUT,
    CAPTCHA_DECODE
}

public class ApprovalRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TaskId { get; set; } = "";
    public ApprovalType Type { get; set; }
    public string Message { get; set; } = "";
    public string? CaptchaImageBase64Encrypted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Resolved { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public class ApprovalResponse
{
    public string ApprovalId { get; set; } = "";
    public bool Approved { get; set; }
    public string? SecretValue { get; set; }
    public string? CaptchaSolution { get; set; }
}

public class ApprovalService
{
    private readonly Dictionary<string, ApprovalRequest> _pendingApprovals = new();
    private readonly Dictionary<string, TaskCompletionSource<ApprovalResponse>> _waiters = new();
    private readonly DeviceKeyManager _keyManager;
    private readonly object _lock = new();
    
    private bool _simulatorEnabled;
    private Func<ApprovalRequest, ApprovalResponse>? _simulatorHandler;
    
    public ApprovalService(DeviceKeyManager keyManager)
    {
        _keyManager = keyManager;
    }
    
    public void EnableSimulator(Func<ApprovalRequest, ApprovalResponse> handler)
    {
        _simulatorEnabled = true;
        _simulatorHandler = handler;
        ArchLogger.LogInfo("Approval simulator enabled");
    }
    
    public void DisableSimulator()
    {
        _simulatorEnabled = false;
        _simulatorHandler = null;
        ArchLogger.LogInfo("Approval simulator disabled");
    }
    
    public async Task<ApprovalResponse> RequestConfirmation(string taskId, string message)
    {
        var request = new ApprovalRequest
        {
            TaskId = taskId,
            Type = ApprovalType.CONFIRMATION,
            Message = message
        };
        return await WaitForApproval(request);
    }
    
    public async Task<ApprovalResponse> RequestSecret(string taskId, string prompt)
    {
        var request = new ApprovalRequest
        {
            TaskId = taskId,
            Type = ApprovalType.SECRET_INPUT,
            Message = prompt
        };
        return await WaitForApproval(request);
    }
    
    public async Task<ApprovalResponse> RequestCaptcha(string taskId, byte[] captchaImagePng, string? recipientPublicKeyBase64 = null)
    {
        string encryptedImage;
        
        if (!string.IsNullOrEmpty(recipientPublicKeyBase64))
        {
            var recipientKey = Convert.FromBase64String(recipientPublicKeyBase64);
            var envelope = ModernCrypto.Encrypt(
                Convert.ToBase64String(captchaImagePng),
                recipientKey,
                "core",
                Guid.NewGuid().ToString("N")
            );
            encryptedImage = JsonSerializer.Serialize(envelope);
        }
        else
        {
            var keys = _keyManager.GetOrCreateKeyPair();
            var envelope = ModernCrypto.Encrypt(
                Convert.ToBase64String(captchaImagePng),
                keys.PublicKey,
                "core",
                Guid.NewGuid().ToString("N")
            );
            encryptedImage = JsonSerializer.Serialize(envelope);
        }
        
        var request = new ApprovalRequest
        {
            TaskId = taskId,
            Type = ApprovalType.CAPTCHA_DECODE,
            Message = "Please solve the captcha",
            CaptchaImageBase64Encrypted = encryptedImage
        };
        
        var response = await WaitForApproval(request);
        
        request.CaptchaImageBase64Encrypted = null;
        ArchLogger.LogInfo($"Captcha blob auto-deleted for approval {request.Id}");
        
        return response;
    }
    
    private async Task<ApprovalResponse> WaitForApproval(ApprovalRequest request)
    {
        ArchLogger.LogInfo($"Approval requested: id={request.Id} type={request.Type} task={request.TaskId}");
        
        if (_simulatorEnabled && _simulatorHandler != null)
        {
            ArchLogger.LogInfo($"Simulator handling approval {request.Id}");
            return _simulatorHandler(request);
        }
        
        var tcs = new TaskCompletionSource<ApprovalResponse>();
        
        lock (_lock)
        {
            _pendingApprovals[request.Id] = request;
            _waiters[request.Id] = tcs;
        }
        
        try
        {
            return await tcs.Task;
        }
        finally
        {
            lock (_lock)
            {
                _pendingApprovals.Remove(request.Id);
                _waiters.Remove(request.Id);
            }
        }
    }
    
    public bool Respond(ApprovalResponse response)
    {
        lock (_lock)
        {
            if (!_waiters.TryGetValue(response.ApprovalId, out var tcs))
            {
                ArchLogger.LogWarn($"Unknown approval ID: {response.ApprovalId}");
                return false;
            }
            
            if (_pendingApprovals.TryGetValue(response.ApprovalId, out var request))
            {
                request.Resolved = true;
                request.ResolvedAt = DateTime.UtcNow;
            }
            
            if (response.SecretValue != null)
            {
                ArchLogger.LogInfo($"Approval {response.ApprovalId} resolved with secret (len={response.SecretValue.Length})");
            }
            else
            {
                ArchLogger.LogInfo($"Approval {response.ApprovalId} resolved: approved={response.Approved}");
            }
            
            tcs.TrySetResult(response);
            return true;
        }
    }
    
    public List<ApprovalRequest> GetPendingApprovals()
    {
        lock (_lock)
        {
            return _pendingApprovals.Values.ToList();
        }
    }
    
    public ApprovalRequest? GetApproval(string id)
    {
        lock (_lock)
        {
            return _pendingApprovals.TryGetValue(id, out var req) ? req : null;
        }
    }
}
