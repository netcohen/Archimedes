namespace Archimedes.Core;

public class AvailabilityStatus
{
    public bool      IsAvailable        { get; set; }
    public string    Reason             { get; set; } = "";
    public DateTime? NextWindowUtc      { get; set; }
    public DateTime? LastInteractionUtc { get; set; }
}

/// <summary>
/// Phase 25 — Availability Engine.
/// Learns when the user is available and controls whether time-sensitive
/// third-party messaging actions should be delayed.
///
/// Key rule (agreed with user):
///   Only THIRD_PARTY_MESSAGE actions require availability check.
///   All other actions (login, download, monitor, export…) run autonomously.
///   Critical tasks always proceed regardless of availability.
/// </summary>
public class AvailabilityEngine
{
    private readonly AvailabilityStore _store;

    public AvailabilityEngine(AvailabilityStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Record a user interaction (chat message, task creation, etc.).
    /// Call this any time we know the user is actively present.
    /// </summary>
    public void RecordInteraction(string source = "api")
    {
        _store.RecordInteraction(source);
        ArchLogger.LogInfo($"[Availability] Interaction recorded from={source}");
    }

    /// <summary>
    /// Returns the current availability status with reason and next window.
    /// </summary>
    public AvailabilityStatus GetStatus()
    {
        var pattern = _store.GetPattern();
        var now     = DateTime.Now; // local time for availability logic

        // Manual override always wins
        if (pattern.ManualOverride)
            return new AvailabilityStatus
            {
                IsAvailable        = true,
                Reason             = "manual_override",
                LastInteractionUtc = pattern.LastInteractionUtc
            };

        // Shabbat window: Friday 17:00 – Saturday 21:00 (local)
        if (pattern.ShabbatDetected && IsShabbatTime(now))
            return new AvailabilityStatus
            {
                IsAvailable        = false,
                Reason             = "shabbat",
                NextWindowUtc      = NextShabbatEnd(now).ToUniversalTime(),
                LastInteractionUtc = pattern.LastInteractionUtc
            };

        // Sleep hours
        if (IsSleepTime(now.Hour, pattern.SleepStartHour, pattern.SleepEndHour))
            return new AvailabilityStatus
            {
                IsAvailable        = false,
                Reason             = "sleep_hours",
                NextWindowUtc      = NextWakeTime(now, pattern.SleepEndHour).ToUniversalTime(),
                LastInteractionUtc = pattern.LastInteractionUtc
            };

        return new AvailabilityStatus
        {
            IsAvailable        = true,
            Reason             = "business_hours",
            LastInteractionUtc = pattern.LastInteractionUtc
        };
    }

    /// <summary>
    /// Returns true if this step action should be delayed due to user unavailability.
    /// Only THIRD_PARTY_MESSAGE requires availability — everything else is autonomous.
    /// </summary>
    public bool ShouldDelay(string actionKind, bool isCritical = false)
    {
        if (isCritical)                            return false;
        if (actionKind != "THIRD_PARTY_MESSAGE")   return false;
        return !GetStatus().IsAvailable;
    }

    public AvailabilityPattern GetPattern() => _store.GetPattern();

    public void UpdatePattern(int sleepStart, int sleepEnd, bool shabbat, bool manualOverride)
        => _store.UpdatePattern(sleepStart, sleepEnd, shabbat, manualOverride);

    // ── helpers ────────────────────────────────────────────────────────────

    private static bool IsSleepTime(int hour, int sleepStart, int sleepEnd)
    {
        if (sleepStart > sleepEnd)            // overnight span (e.g., 23–07)
            return hour >= sleepStart || hour < sleepEnd;
        return hour >= sleepStart && hour < sleepEnd;
    }

    private static bool IsShabbatTime(DateTime local)
    {
        var dow  = local.DayOfWeek;
        var hour = local.Hour;
        return (dow == DayOfWeek.Friday   && hour >= 17) ||
               (dow == DayOfWeek.Saturday && hour <  21);
    }

    private static DateTime NextShabbatEnd(DateTime local)
    {
        var d = local.Date;
        // Advance to Saturday
        while (d.DayOfWeek != DayOfWeek.Saturday) d = d.AddDays(1);
        return d.AddHours(21);
    }

    private static DateTime NextWakeTime(DateTime local, int wakeHour)
    {
        var candidate = local.Date.AddHours(wakeHour);
        return candidate > local ? candidate : candidate.AddDays(1);
    }
}
