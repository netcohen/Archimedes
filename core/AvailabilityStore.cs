using System.Text.Json;

namespace Archimedes.Core;

public class InteractionRecord
{
    public DateTime TimestampUtc { get; set; }
    public string Source { get; set; } = "";
}

public class AvailabilityPattern
{
    public int SleepStartHour { get; set; } = 23;
    public int SleepEndHour   { get; set; } = 7;
    public bool ShabbatDetected { get; set; } = false;
    public bool ManualOverride  { get; set; } = false;
    public DateTime? LastInteractionUtc { get; set; }
    public List<InteractionRecord> RecentInteractions { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persists user availability patterns to disk.
/// </summary>
public class AvailabilityStore
{
    private readonly string   _path;
    private AvailabilityPattern _pattern;
    private readonly object   _lock = new();

    public AvailabilityStore(string? basePath = null)
    {
        var dir = basePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Archimedes");
        Directory.CreateDirectory(dir);
        _path   = Path.Combine(dir, "availability.json");
        _pattern = Load();
    }

    private AvailabilityPattern Load()
    {
        if (!File.Exists(_path)) return new AvailabilityPattern();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AvailabilityPattern>(json) ?? new AvailabilityPattern();
        }
        catch { return new AvailabilityPattern(); }
    }

    private void Save()
    {
        _pattern.UpdatedAt = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(_pattern, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }

    public void RecordInteraction(string source = "api")
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _pattern.LastInteractionUtc = now;
            _pattern.RecentInteractions.Add(new InteractionRecord { TimestampUtc = now, Source = source });
            // Keep last 200 interactions
            if (_pattern.RecentInteractions.Count > 200)
                _pattern.RecentInteractions = _pattern.RecentInteractions.TakeLast(200).ToList();
            RefinePatterns();
            Save();
        }
    }

    /// <summary>
    /// Infer sleep/Shabbat patterns from recorded interactions.
    /// </summary>
    private void RefinePatterns()
    {
        if (_pattern.RecentInteractions.Count < 10) return;

        // Hour distribution (local time)
        var hourCounts = new int[24];
        foreach (var r in _pattern.RecentInteractions)
            hourCounts[r.TimestampUtc.ToLocalTime().Hour]++;

        int total = _pattern.RecentInteractions.Count;

        // Detect sleep: find the largest consecutive silent block in overnight range (20-12)
        // We look for the hour where activity drops below 2% threshold
        var silentHours = Enumerable.Range(0, 24)
            .Where(h => hourCounts[h] < total * 0.02)
            .ToHashSet();

        // Find the block that spans around midnight
        if (silentHours.Count >= 3)
        {
            // Pick latest silent hour before midnight as sleep start
            for (int h = 22; h >= 20; h--)
                if (silentHours.Contains(h)) { _pattern.SleepStartHour = h; break; }
            // Pick earliest silent hour after midnight as end of sleep
            for (int h = 5; h <= 10; h++)
                if (silentHours.Contains(h)) { _pattern.SleepEndHour = h + 1; break; }
        }

        // Shabbat detection: count Saturday interactions vs expected
        int saturdayCount = _pattern.RecentInteractions
            .Count(r => r.TimestampUtc.ToLocalTime().DayOfWeek == DayOfWeek.Saturday);
        int expectedPerDay = total / 7;
        if (expectedPerDay > 3 && saturdayCount < expectedPerDay * 0.1)
            _pattern.ShabbatDetected = true;
    }

    public void UpdatePattern(int sleepStart, int sleepEnd, bool shabbat, bool manualOverride)
    {
        lock (_lock)
        {
            _pattern.SleepStartHour = sleepStart;
            _pattern.SleepEndHour   = sleepEnd;
            _pattern.ShabbatDetected = shabbat;
            _pattern.ManualOverride  = manualOverride;
            Save();
        }
    }

    public AvailabilityPattern GetPattern()
    {
        lock (_lock) { return _pattern; }
    }
}
