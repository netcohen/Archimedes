using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Archimedes.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PolicyDecision
{
    AUTO_ALLOW,
    REQUIRE_APPROVAL,
    REQUIRE_SECRET,
    REQUIRE_CAPTCHA,
    DENY
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EntityScope
{
    SELF,
    CHILD
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionKind
{
    READ_ONLY,
    WRITE,
    MONEY,
    IDENTITY
}

public class TimeWindow
{
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public List<DayOfWeek>? DaysOfWeek { get; set; }
    
    public bool IsActive()
    {
        var now = DateTime.Now;
        
        if (DaysOfWeek != null && DaysOfWeek.Count > 0 && !DaysOfWeek.Contains(now.DayOfWeek))
            return false;
        
        var timeNow = TimeOnly.FromDateTime(now);
        if (StartTime.HasValue && timeNow < StartTime.Value)
            return false;
        if (EndTime.HasValue && timeNow > EndTime.Value)
            return false;
        
        return true;
    }
}

public class PolicyRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string? Description { get; set; }
    
    public string? DomainPattern { get; set; }
    public List<string>? DomainAllowlist { get; set; }
    public List<string>? DomainDenylist { get; set; }
    
    public EntityScope? EntityScope { get; set; }
    public ActionKind? ActionKind { get; set; }
    
    public TimeWindow? TimeWindow { get; set; }
    
    public PolicyDecision Decision { get; set; } = PolicyDecision.REQUIRE_APPROVAL;
    public int Priority { get; set; } = 100;
    
    public bool Enabled { get; set; } = true;
}

public class PolicyEvaluationRequest
{
    public string? Domain { get; set; }
    public string? Action { get; set; }
    public string? ActionKind { get; set; }
    public string? EntityScope { get; set; }
    public Dictionary<string, object>? Context { get; set; }
}

public class PolicyEvaluationResult
{
    public PolicyDecision Decision { get; set; }
    public string? MatchedRuleId { get; set; }
    public string? Reason { get; set; }
    public List<string> EvaluatedRules { get; set; } = new();
}

public class PolicyEngine
{
    private readonly List<PolicyRule> _rules = new();
    private readonly object _lock = new();
    
    public PolicyEngine()
    {
        InitializeDefaultRules();
    }
    
    private void InitializeDefaultRules()
    {
        _rules.Add(new PolicyRule
        {
            Id = "default-deny-money",
            Description = "All money-related actions require approval",
            ActionKind = Core.ActionKind.MONEY,
            Decision = PolicyDecision.REQUIRE_APPROVAL,
            Priority = 10
        });
        
        _rules.Add(new PolicyRule
        {
            Id = "default-deny-identity",
            Description = "Identity actions require approval",
            ActionKind = Core.ActionKind.IDENTITY,
            Decision = PolicyDecision.REQUIRE_APPROVAL,
            Priority = 10
        });
        
        _rules.Add(new PolicyRule
        {
            Id = "testsite-allow",
            Description = "Allow testsite operations",
            DomainAllowlist = new List<string> { "localhost:5052", "127.0.0.1:5052" },
            Decision = PolicyDecision.AUTO_ALLOW,
            Priority = 50
        });
        
        _rules.Add(new PolicyRule
        {
            Id = "default-readonly-allow",
            Description = "Read-only operations are allowed",
            ActionKind = Core.ActionKind.READ_ONLY,
            Decision = PolicyDecision.AUTO_ALLOW,
            Priority = 90
        });
        
        _rules.Add(new PolicyRule
        {
            Id = "default-write-approval",
            Description = "Write operations require approval by default",
            ActionKind = Core.ActionKind.WRITE,
            Decision = PolicyDecision.REQUIRE_APPROVAL,
            Priority = 100
        });
        
        _rules.Add(new PolicyRule
        {
            Id = "default-fallback",
            Description = "Default: require approval for unknown actions",
            Decision = PolicyDecision.REQUIRE_APPROVAL,
            Priority = 1000
        });
    }
    
    public List<PolicyRule> GetRules()
    {
        lock (_lock)
        {
            return _rules.ToList();
        }
    }
    
    public PolicyRule? GetRule(string id)
    {
        lock (_lock)
        {
            return _rules.FirstOrDefault(r => r.Id == id);
        }
    }
    
    public void AddRule(PolicyRule rule)
    {
        lock (_lock)
        {
            var existing = _rules.FirstOrDefault(r => r.Id == rule.Id);
            if (existing != null)
            {
                _rules.Remove(existing);
            }
            _rules.Add(rule);
            _rules.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }
        ArchLogger.LogInfo($"Policy rule added/updated: {rule.Id} -> {rule.Decision}");
    }
    
    public bool RemoveRule(string id)
    {
        lock (_lock)
        {
            var rule = _rules.FirstOrDefault(r => r.Id == id);
            if (rule != null)
            {
                _rules.Remove(rule);
                ArchLogger.LogInfo($"Policy rule removed: {id}");
                return true;
            }
            return false;
        }
    }
    
    public PolicyEvaluationResult Evaluate(PolicyEvaluationRequest request)
    {
        var result = new PolicyEvaluationResult();
        ActionKind? requestActionKind = null;
        EntityScope? requestEntityScope = null;
        
        if (!string.IsNullOrEmpty(request.ActionKind) && 
            Enum.TryParse<ActionKind>(request.ActionKind, true, out var ak))
        {
            requestActionKind = ak;
        }
        
        if (!string.IsNullOrEmpty(request.EntityScope) && 
            Enum.TryParse<EntityScope>(request.EntityScope, true, out var es))
        {
            requestEntityScope = es;
        }
        
        lock (_lock)
        {
            foreach (var rule in _rules.Where(r => r.Enabled).OrderBy(r => r.Priority))
            {
                result.EvaluatedRules.Add(rule.Id);
                
                if (Matches(rule, request, requestActionKind, requestEntityScope))
                {
                    result.Decision = rule.Decision;
                    result.MatchedRuleId = rule.Id;
                    result.Reason = rule.Description ?? $"Matched rule {rule.Id}";
                    
                    ArchLogger.LogInfo($"Policy evaluation: domain={Redactor.Redact(request.Domain)} action={request.Action} -> {rule.Decision} (rule: {rule.Id})");
                    return result;
                }
            }
        }
        
        result.Decision = PolicyDecision.REQUIRE_APPROVAL;
        result.Reason = "No matching rule, defaulting to approval required";
        return result;
    }
    
    private bool Matches(PolicyRule rule, PolicyEvaluationRequest request, 
        ActionKind? requestActionKind, EntityScope? requestEntityScope)
    {
        if (rule.TimeWindow != null && !rule.TimeWindow.IsActive())
            return false;
        
        if (rule.DomainDenylist != null && !string.IsNullOrEmpty(request.Domain))
        {
            foreach (var denied in rule.DomainDenylist)
            {
                if (MatchesDomain(request.Domain, denied))
                    return false;
            }
        }
        
        if (rule.DomainAllowlist != null && rule.DomainAllowlist.Count > 0)
        {
            if (string.IsNullOrEmpty(request.Domain))
                return false;
            
            var matched = false;
            foreach (var allowed in rule.DomainAllowlist)
            {
                if (MatchesDomain(request.Domain, allowed))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched) return false;
        }
        
        if (!string.IsNullOrEmpty(rule.DomainPattern) && !string.IsNullOrEmpty(request.Domain))
        {
            try
            {
                if (!Regex.IsMatch(request.Domain, rule.DomainPattern, RegexOptions.IgnoreCase))
                    return false;
            }
            catch
            {
                return false;
            }
        }
        
        if (rule.ActionKind.HasValue)
        {
            if (!requestActionKind.HasValue || requestActionKind.Value != rule.ActionKind.Value)
                return false;
        }
        
        if (rule.EntityScope.HasValue)
        {
            if (!requestEntityScope.HasValue || requestEntityScope.Value != rule.EntityScope.Value)
                return false;
        }
        
        return true;
    }
    
    private bool MatchesDomain(string domain, string pattern)
    {
        if (pattern.StartsWith("*."))
        {
            var suffix = pattern[1..];
            return domain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                   domain.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase);
        }
        
        return domain.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
