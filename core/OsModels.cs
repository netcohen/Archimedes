using System.Text.Json.Serialization;

namespace Archimedes.Core;

public enum OsManagerState { IDLE, CHECKING, UPDATING, REBOOTING, STOPPED, ERROR }

public class OsStatus
{
    [JsonPropertyName("osName")]          public string         OsName          { get; set; } = "";
    [JsonPropertyName("isLinux")]         public bool           IsLinux         { get; set; }
    [JsonPropertyName("rebootRequired")]  public bool           RebootRequired  { get; set; }
    [JsonPropertyName("scheduledReboot")] public DateTime?      ScheduledReboot { get; set; }
    [JsonPropertyName("apt")]             public AptStatus      Apt             { get; set; } = new();
    [JsonPropertyName("hardware")]        public HardwareMetrics Hardware        { get; set; } = new();
    [JsonPropertyName("firewall")]        public FirewallStatus Firewall        { get; set; } = new();
    [JsonPropertyName("state")]           public OsManagerState State           { get; set; }
    [JsonPropertyName("lastCheckAt")]     public DateTime       LastCheckAt     { get; set; }
    [JsonPropertyName("lastError")]       public string?        LastError       { get; set; }
}

public class AptStatus
{
    [JsonPropertyName("pendingUpdates")]         public int       PendingUpdates         { get; set; }
    [JsonPropertyName("pendingSecurityUpdates")] public int       PendingSecurityUpdates { get; set; }
    [JsonPropertyName("lastUpdateCheck")]        public DateTime? LastUpdateCheck        { get; set; }
    [JsonPropertyName("lastUpgrade")]            public DateTime? LastUpgrade            { get; set; }
    [JsonPropertyName("autoUpgradeEnabled")]     public bool      AutoUpgradeEnabled     { get; set; }
    [JsonPropertyName("lastOutput")]             public string    LastOutput             { get; set; } = "";
}

public class HardwareMetrics
{
    [JsonPropertyName("cpuTempCelsius")]  public double           CpuTempCelsius { get; set; }
    [JsonPropertyName("ramUsedMb")]       public double           RamUsedMb      { get; set; }
    [JsonPropertyName("ramTotalMb")]      public double           RamTotalMb     { get; set; }
    [JsonPropertyName("ramUsedPercent")]  public double           RamUsedPercent { get; set; }
    [JsonPropertyName("disks")]           public List<DiskMetric> Disks          { get; set; } = new();
    [JsonPropertyName("sampledAt")]       public DateTime         SampledAt      { get; set; }
}

public class DiskMetric
{
    [JsonPropertyName("mountPoint")]  public string MountPoint  { get; set; } = "";
    [JsonPropertyName("usedBytes")]   public long   UsedBytes   { get; set; }
    [JsonPropertyName("totalBytes")]  public long   TotalBytes  { get; set; }
    [JsonPropertyName("usedPercent")] public double UsedPercent { get; set; }
    [JsonPropertyName("filesystem")]  public string Filesystem  { get; set; } = "";
}

public class FirewallStatus
{
    [JsonPropertyName("enabled")] public bool              Enabled { get; set; }
    [JsonPropertyName("rules")]   public List<FirewallRule> Rules  { get; set; } = new();
    [JsonPropertyName("default")] public string            Default { get; set; } = "deny";
}

public class FirewallRule
{
    [JsonPropertyName("port")]     public string Port     { get; set; } = "";
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "tcp";
    [JsonPropertyName("action")]   public string Action   { get; set; } = "allow";
    [JsonPropertyName("comment")]  public string Comment  { get; set; } = "";
}

public class MaintenanceWindow
{
    [JsonPropertyName("startHour")]   public int    StartHour   { get; set; } = 3;
    [JsonPropertyName("startMinute")] public int    StartMinute { get; set; } = 0;
    [JsonPropertyName("endHour")]     public int    EndHour     { get; set; } = 4;
    [JsonPropertyName("endMinute")]   public int    EndMinute   { get; set; } = 0;
    // true = day is active (0=Sun … 6=Sat). Default: Sun-Fri active, Sat=false (Shabbat)
    [JsonPropertyName("activeDays")]  public bool[] ActiveDays  { get; set; } = [true, true, true, true, true, true, false];

    public bool IsNow()
    {
        var now   = DateTime.Now;
        if (!ActiveDays[(int)now.DayOfWeek]) return false;
        var start = new TimeSpan(StartHour, StartMinute, 0);
        var end   = new TimeSpan(EndHour,   EndMinute,   0);
        return now.TimeOfDay >= start && now.TimeOfDay < end;
    }

    public DateTime NextWindow()
    {
        var now   = DateTime.Now;
        var start = new TimeSpan(StartHour, StartMinute, 0);
        for (int i = 0; i < 8; i++)
        {
            var candidate = now.AddDays(i).Date + start;
            if (candidate > now && ActiveDays[(int)candidate.DayOfWeek])
                return candidate;
        }
        return now.AddDays(1).Date + start;
    }
}

public class RebootSchedule
{
    [JsonPropertyName("scheduledAt")] public DateTime ScheduledAt { get; set; }
    [JsonPropertyName("reason")]      public string   Reason      { get; set; } = "";
    [JsonPropertyName("createdAt")]   public DateTime CreatedAt   { get; set; }
}

public class OsLogCleanupResult
{
    [JsonPropertyName("deletedFiles")] public int          DeletedFiles { get; set; }
    [JsonPropertyName("freedMb")]      public double       FreedMb      { get; set; }
    [JsonPropertyName("paths")]        public List<string> Paths        { get; set; } = new();
}

public class AptRunResult
{
    [JsonPropertyName("success")] public bool   Success { get; set; }
    [JsonPropertyName("output")]  public string Output  { get; set; } = "";
}
