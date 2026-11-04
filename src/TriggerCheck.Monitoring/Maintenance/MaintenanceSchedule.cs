using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Monitoring.Maintenance;

/// <summary>One occurrence of a maintenance window, [StartUtc, EndUtc).</summary>
public readonly record struct MaintenanceOccurrence(DateTime StartUtc, DateTime EndUtc)
{
    public bool Contains(DateTime utc) => StartUtc <= utc && utc < EndUtc;
}

/// <summary>
/// Pure schedule logic of <see cref="MaintenanceWindow"/>s (FEAT-3), free of I/O so it can be unit tested.
/// Weekly windows start at a local wall-clock time in the window's IANA time zone, so they follow DST: "Sunday 02:00
/// Europe/Berlin" is 01:00 UTC in winter and 00:00 UTC in summer. Each occurrence lasts DurationMinutes of elapsed
/// time. Local start times that do not exist (inside a spring-forward gap) are shifted forward by the gap (02:30 on
/// the spring-forward day in Berlin becomes 03:30 CEST); ambiguous ones (fall-back) use the first occurrence (the
/// earlier UTC instant).
/// </summary>
public static class MaintenanceSchedule
{
    /// <summary>Longest occurrence of a weekly window (one week).</summary>
    public const int MaxRecurringDurationMinutes = 7 * 24 * 60;
    /// <summary>Longest one-off window.</summary>
    public static readonly TimeSpan MaxOneOffDuration = TimeSpan.FromDays(31);
    /// <summary>Upper bound for the range scanned by <see cref="Occurrences"/> (guards against unbounded loops).</summary>
    private static readonly TimeSpan MaxScanRange = TimeSpan.FromDays(400);

    public const int MaxTimeZoneIdLength = 100;

    /// <summary>Finds an IANA (or, on Windows, system) time zone; null when it is unknown on this host.</summary>
    public static TimeZoneInfo? FindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxTimeZoneIdLength) return null;
        // Any failure means "unknown": on Linux a partial id such as "America" names a directory and throws I/O errors.
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
        catch (Exception) { return null; }
    }

    /// <summary>Occurrences of <paramref name="window"/> that overlap [fromUtc, toUtc), in start order.</summary>
    public static IEnumerable<MaintenanceOccurrence> Occurrences(MaintenanceWindow window, DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc) yield break;
        if (!window.IsRecurring)
        {
            if (window.StartsAtUtc is { } s && window.EndsAtUtc is { } e && e > s)
            {
                var occurrence = new MaintenanceOccurrence(Utc(s), Utc(e));
                if (occurrence.EndUtc > fromUtc && occurrence.StartUtc < toUtc) yield return occurrence;
            }
            yield break;
        }

        if (window.RecurrenceDays == WeekDays.None || window.DurationMinutes <= 0) yield break;
        var tz = FindTimeZone(window.TimeZoneId);
        if (tz is null) yield break;
        if (toUtc - fromUtc > MaxScanRange) toUtc = fromUtc + MaxScanRange;

        var duration = TimeSpan.FromMinutes(Math.Min(window.DurationMinutes, MaxRecurringDurationMinutes));
        var startOfDay = TimeSpan.FromMinutes(Math.Clamp(window.RecurrenceStartMinutes, 0, 24 * 60 - 1));
        // Occurrences starting up to one duration (plus a day of offset slack) before the range can still overlap it.
        var day = LocalDate(fromUtc - duration, tz).AddDays(-1);
        var lastDay = LocalDate(toUtc, tz).AddDays(1);
        for (; day <= lastDay; day = day.AddDays(1))
        {
            if (!IsOn(window.RecurrenceDays, day.DayOfWeek)) continue;
            var start = LocalToUtc(day.ToDateTime(TimeOnly.MinValue) + startOfDay, tz);
            var occurrence = new MaintenanceOccurrence(start, start + duration);
            if (occurrence.EndUtc > fromUtc && occurrence.StartUtc < toUtc) yield return occurrence;
        }
    }

    /// <summary>The occurrence of <paramref name="window"/> that contains <paramref name="atUtc"/>, if any.</summary>
    public static MaintenanceOccurrence? ActiveAt(MaintenanceWindow window, DateTime atUtc)
    {
        foreach (var o in Occurrences(window, atUtc, atUtc.AddTicks(1)))
            if (o.Contains(atUtc)) return o;
        return null;
    }

    /// <summary>The active occurrence at <paramref name="atUtc"/>, else the next one to start (within a year), else null.</summary>
    public static MaintenanceOccurrence? CurrentOrNext(MaintenanceWindow window, DateTime atUtc)
    {
        // Weekly windows start at least once every 8 days; one-off windows are checked over their own span.
        var horizon = window.IsRecurring ? TimeSpan.FromDays(8) : TimeSpan.FromDays(366);
        foreach (var o in Occurrences(window, atUtc, atUtc + horizon))
            return o;
        return null;
    }

    public static bool IsOn(WeekDays days, DayOfWeek day) => (days & (WeekDays)(1 << (int)day)) != 0;

    /// <summary>Converts a wall-clock time of <paramref name="tz"/> to UTC (DST gaps shifted forward, ambiguous times = first occurrence).</summary>
    public static DateTime LocalToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (tz.IsInvalidTime(local))
            offset = tz.GetUtcOffset(local.AddDays(-1)); // the offset before the gap: shifts the time forward by the gap
        else if (tz.IsAmbiguousTime(local))
            offset = tz.GetAmbiguousTimeOffsets(local).Max(); // larger offset = earlier instant
        else
            offset = tz.GetUtcOffset(local);
        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }

    public static DateTime UtcToLocal(DateTime utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTimeFromUtc(Utc(utc), tz);

    private static DateOnly LocalDate(DateTime utc, TimeZoneInfo tz) => DateOnly.FromDateTime(UtcToLocal(utc, tz));

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
