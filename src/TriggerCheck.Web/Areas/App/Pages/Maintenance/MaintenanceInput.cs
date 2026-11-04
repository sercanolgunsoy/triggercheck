using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Maintenance;

namespace TriggerCheck.Web.Areas.App.Pages.Maintenance;

/// <summary>Form model for maintenance window create/edit (FEAT-3). Times are entered in the chosen time zone.</summary>
public class MaintenanceInput
{
    public const string DefaultTimeZone = "Europe/Istanbul";

    [Required(ErrorMessage = "Name is required."), StringLength(200, ErrorMessage = "Name can be at most 200 characters.")]
    [Display(Name = "Name")]
    public string Name { get; set; } = "";

    /// <summary>Weekly recurrence instead of a one-off window.</summary>
    [Display(Name = "Repeats weekly")]
    public bool IsRecurring { get; set; }

    [Display(Name = "Time zone")]
    public string TimeZoneId { get; set; } = DefaultTimeZone;

    /// <summary>One-off start, local time in <see cref="TimeZoneId"/> (datetime-local input).</summary>
    [Display(Name = "Starts")]
    public DateTime? StartsLocal { get; set; }

    [Display(Name = "Ends")]
    public DateTime? EndsLocal { get; set; }

    /// <summary>Weekly: days the window starts on.</summary>
    public List<DayOfWeek> Days { get; set; } = [];

    /// <summary>Weekly: local start time "HH:mm".</summary>
    [Display(Name = "Start time")]
    public string? StartTime { get; set; }

    [Display(Name = "Duration (minutes)")]
    public int? DurationMinutes { get; set; }

    [Display(Name = "All monitors")]
    public bool AllMonitors { get; set; } = true;

    public List<int> MonitorIds { get; set; } = [];

    /// <summary>IANA time zones offered in the form (sorted; the host's zones mapped to IANA ids).</summary>
    public static IReadOnlyList<string> TimeZoneIds => Zones.Value;

    private static readonly Lazy<IReadOnlyList<string>> Zones = new(() =>
        TimeZoneInfo.GetSystemTimeZones()
            .Select(z => z.HasIanaId ? z.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana : null)
            .Append(DefaultTimeZone).Append("UTC")
            .Where(id => id is not null && MaintenanceSchedule.FindTimeZone(id) is not null)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList());

    public static MaintenanceInput From(MaintenanceWindow w)
    {
        var tz = MaintenanceSchedule.FindTimeZone(w.TimeZoneId);
        DateTime? Local(DateTime? utc) => utc is { } u && tz is not null ? MaintenanceSchedule.UtcToLocal(u, tz) : utc;
        return new MaintenanceInput
        {
            Name = w.Name,
            IsRecurring = w.IsRecurring,
            TimeZoneId = w.TimeZoneId,
            StartsLocal = Local(w.StartsAtUtc),
            EndsLocal = Local(w.EndsAtUtc),
            Days = Enum.GetValues<DayOfWeek>().Where(d => MaintenanceSchedule.IsOn(w.RecurrenceDays, d)).ToList(),
            StartTime = w.IsRecurring ? TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(w.RecurrenceStartMinutes)).ToString("HH:mm", CultureInfo.InvariantCulture) : null,
            DurationMinutes = w.IsRecurring ? w.DurationMinutes : null,
            AllMonitors = w.AppliesToAllMonitors,
            MonitorIds = w.Monitors.Select(m => m.Id).ToList()
        };
    }

    /// <summary>Validates the input (adds model errors) and returns the monitors it selects (only the customer's own).</summary>
    /// <param name="existing">The window being edited (null when creating): an unchanged one-off period may already be over.</param>
    public async Task<List<Domain.Entities.Monitor>> ValidateAsync(ModelStateDictionary ms, IStringLocalizer L, AppDbContext db, int customerId,
        DateTime nowUtc, CancellationToken ct, MaintenanceWindow? existing = null, string prefix = "Input.")
    {
        Name = Name?.Trim() ?? "";
        TimeZoneId = TimeZoneId?.Trim() ?? "";
        // Only zones offered in the form (IANA ids known on this host).
        var tz = TimeZoneId.Length <= MaintenanceSchedule.MaxTimeZoneIdLength && TimeZoneIds.Contains(TimeZoneId, StringComparer.Ordinal)
            ? MaintenanceSchedule.FindTimeZone(TimeZoneId)
            : null;
        if (tz is null) ms.AddModelError(prefix + nameof(TimeZoneId), L["Choose a valid time zone."]);

        if (IsRecurring)
        {
            if (Days.Count == 0) ms.AddModelError(prefix + nameof(Days), L["Choose at least one day."]);
            if (ParseStartTime() is null) ms.AddModelError(prefix + nameof(StartTime), L["Enter the start time as HH:mm."]);
            if (DurationMinutes is not int d || d < 1 || d > MaintenanceSchedule.MaxRecurringDurationMinutes)
                ms.AddModelError(prefix + nameof(DurationMinutes), L["The duration must be between 1 and {0} minutes.", MaintenanceSchedule.MaxRecurringDurationMinutes]);
        }
        else if (StartsLocal is not { } start || EndsLocal is not { } end)
        {
            ms.AddModelError(prefix + nameof(StartsLocal), L["Enter the start and the end."]);
        }
        else if (tz is not null)
        {
            var startUtc = MaintenanceSchedule.LocalToUtc(start, tz);
            var endUtc = MaintenanceSchedule.LocalToUtc(end, tz);
            if (endUtc <= startUtc) ms.AddModelError(prefix + nameof(EndsLocal), L["The end must be after the start."]);
            else if (endUtc - startUtc > MaintenanceSchedule.MaxOneOffDuration) ms.AddModelError(prefix + nameof(EndsLocal), L["A one-off window can last at most 31 days."]);
            else if (endUtc <= nowUtc && !(existing is { IsRecurring: false } && existing.StartsAtUtc == startUtc && existing.EndsAtUtc == endUtc))
                ms.AddModelError(prefix + nameof(EndsLocal), L["The end must be in the future."]);
        }

        var monitors = new List<Domain.Entities.Monitor>();
        if (!AllMonitors)
        {
            var ids = MonitorIds.Distinct().ToList();
            monitors = await db.Monitors.Where(m => m.CustomerId == customerId && ids.Contains(m.Id)).ToListAsync(ct);
            if (monitors.Count == 0) ms.AddModelError(prefix + nameof(MonitorIds), L["Choose at least one monitor, or apply the window to all monitors."]);
        }
        return monitors;
    }

    /// <summary>Copies the (validated) input onto the entity.</summary>
    public void ApplyTo(MaintenanceWindow w, List<Domain.Entities.Monitor> monitors)
    {
        var tz = MaintenanceSchedule.FindTimeZone(TimeZoneId)!;
        w.Name = Name;
        w.TimeZoneId = TimeZoneId.Trim();
        w.IsRecurring = IsRecurring;
        if (IsRecurring)
        {
            w.StartsAtUtc = w.EndsAtUtc = null;
            w.RecurrenceDays = Days.Aggregate(WeekDays.None, (acc, d) => acc | (WeekDays)(1 << (int)d));
            w.RecurrenceStartMinutes = (int)ParseStartTime()!.Value.ToTimeSpan().TotalMinutes;
            w.DurationMinutes = DurationMinutes!.Value;
        }
        else
        {
            w.StartsAtUtc = MaintenanceSchedule.LocalToUtc(StartsLocal!.Value, tz);
            w.EndsAtUtc = MaintenanceSchedule.LocalToUtc(EndsLocal!.Value, tz);
            w.RecurrenceDays = WeekDays.None;
            w.RecurrenceStartMinutes = 0;
            w.DurationMinutes = 0;
        }
        w.AppliesToAllMonitors = AllMonitors;
        w.Monitors.Clear();
        if (!AllMonitors) foreach (var m in monitors) w.Monitors.Add(m);
    }

    private TimeOnly? ParseStartTime() =>
        TimeOnly.TryParseExact(StartTime?.Trim(), ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}
