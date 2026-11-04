namespace TriggerCheck.Domain.Entities;

/// <summary>Days of the week a recurring maintenance window starts on (one bit per <see cref="DayOfWeek"/>).</summary>
[Flags]
public enum WeekDays
{
    None = 0,
    Sunday = 1 << DayOfWeek.Sunday,
    Monday = 1 << DayOfWeek.Monday,
    Tuesday = 1 << DayOfWeek.Tuesday,
    Wednesday = 1 << DayOfWeek.Wednesday,
    Thursday = 1 << DayOfWeek.Thursday,
    Friday = 1 << DayOfWeek.Friday,
    Saturday = 1 << DayOfWeek.Saturday
}

/// <summary>
/// Planned maintenance of a customer (FEAT-3). While a window is active, the monitors it covers are still checked and
/// their results recorded (flagged <see cref="CheckResult.InMaintenance"/>), but no incident is opened and no alert is
/// sent, and those checks are left out of uptime and chart statistics. Either one-off (<see cref="StartsAtUtc"/> -
/// <see cref="EndsAtUtc"/>) or weekly (<see cref="IsRecurring"/>: on <see cref="RecurrenceDays"/> at
/// <see cref="RecurrenceStartMinutes"/> local time in <see cref="TimeZoneId"/>, for <see cref="DurationMinutes"/>).
/// See TriggerCheck.Monitoring.Maintenance.MaintenanceSchedule.
/// </summary>
public class MaintenanceWindow
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Name { get; set; } = "";

    /// <summary>IANA time zone the window was entered in (recurrence start times are local to it).</summary>
    public string TimeZoneId { get; set; } = "Europe/Istanbul";

    // ---- one-off ----
    public DateTime? StartsAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }

    // ---- weekly ----
    public bool IsRecurring { get; set; }
    public WeekDays RecurrenceDays { get; set; }
    /// <summary>Local start time of each occurrence, minutes after midnight in <see cref="TimeZoneId"/>.</summary>
    public int RecurrenceStartMinutes { get; set; }
    /// <summary>Length of each occurrence (elapsed time).</summary>
    public int DurationMinutes { get; set; }

    /// <summary>True: covers every monitor of the customer (also ones added later); false: only <see cref="Monitors"/>.</summary>
    public bool AppliesToAllMonitors { get; set; } = true;
    public ICollection<Monitor> Monitors { get; set; } = new List<Monitor>();

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
