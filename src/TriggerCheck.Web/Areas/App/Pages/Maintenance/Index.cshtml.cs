using System.Globalization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Maintenance;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Maintenance;

/// <summary>FEAT-3: maintenance windows of the customer (active, upcoming, ended). Read-only for viewers.</summary>
public class IndexModel(AppDbContext db, ICurrentCustomer current, TimeProvider time, Text<AppResource> L) : PageModel
{
    public enum WindowState { Active, Upcoming, Ended }

    public record Row(int Id, string Name, string Schedule, string Scope, WindowState State, MaintenanceOccurrence? Occurrence);

    public List<Row> Windows { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var now = time.GetUtcNow().UtcDateTime;
        var windows = await db.MaintenanceWindows.AsNoTracking()
            .Where(w => w.CustomerId == cid)
            .Select(w => new { Window = w, Count = w.Monitors.Count(m => m.CustomerId == cid) })
            .ToListAsync(ct);

        Windows = windows.Select(x =>
            {
                var occurrence = MaintenanceSchedule.CurrentOrNext(x.Window, now);
                var state = occurrence is null ? WindowState.Ended : occurrence.Value.Contains(now) ? WindowState.Active : WindowState.Upcoming;
                var scope = x.Window.AppliesToAllMonitors ? L["All monitors"].Value : L["{0} monitor(s)", x.Count].Value;
                return new Row(x.Window.Id, x.Window.Name, Describe(x.Window, L), scope, state, occurrence);
            })
            .OrderBy(r => r.State)
            .ThenBy(r => r.Occurrence?.StartUtc ?? DateTime.MaxValue)
            .ThenBy(r => r.Name)
            .ToList();
    }

    /// <summary>Human-readable schedule in the window's own time zone, e.g. "Mon, Wed at 02:00 for 2 h (Europe/Berlin)".</summary>
    public static string Describe(MaintenanceWindow w, IStringLocalizer L)
    {
        var tz = MaintenanceSchedule.FindTimeZone(w.TimeZoneId);
        if (w.IsRecurring)
        {
            var names = Fmt.Culture.DateTimeFormat.AbbreviatedDayNames;
            // Monday first.
            var days = Enum.GetValues<DayOfWeek>().OrderBy(d => ((int)d + 6) % 7)
                .Where(d => MaintenanceSchedule.IsOn(w.RecurrenceDays, d)).Select(d => names[(int)d]);
            var at = TimeSpan.FromMinutes(w.RecurrenceStartMinutes).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            return L["Weekly on {0} at {1} for {2} ({3})", string.Join(", ", days), at, Fmt.Duration(TimeSpan.FromMinutes(w.DurationMinutes)), w.TimeZoneId].Value;
        }
        string Local(DateTime? utc) => utc is { } u && tz is not null
            ? MaintenanceSchedule.UtcToLocal(u, tz).ToString(Fmt.IsTurkish ? "dd.MM.yyyy HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "—";
        return L["{0} to {1} ({2})", Local(w.StartsAtUtc), Local(w.EndsAtUtc), w.TimeZoneId].Value;
    }
}
