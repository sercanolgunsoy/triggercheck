using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Maintenance;

[Authorize(Policy = Policies.CustomerAdmin)]
public class CreateModel(AppDbContext db, ICurrentCustomer current, TimeProvider time, Text<AppResource> L) : PageModel
{
    [BindProperty] public MaintenanceInput Input { get; set; } = new();
    public IReadOnlyList<(int Id, string Name)> Monitors { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(db, current.RequiredCustomerId, ct);
        // Sensible default: the next full hour, for one hour, in the default zone.
        var tz = Monitoring.Maintenance.MaintenanceSchedule.FindTimeZone(Input.TimeZoneId);
        if (tz is not null)
        {
            var local = Monitoring.Maintenance.MaintenanceSchedule.UtcToLocal(time.GetUtcNow().UtcDateTime, tz);
            var start = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0).AddHours(1);
            Input.StartsLocal = start;
            Input.EndsLocal = start.AddHours(1);
        }
        Input.StartTime = "02:00";
        Input.DurationMinutes = 60;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var monitors = await Input.ValidateAsync(ModelState, L, db, cid, time.GetUtcNow().UtcDateTime, ct);
        if (!ModelState.IsValid)
        {
            await LoadAsync(db, cid, ct);
            return Page();
        }

        var w = new MaintenanceWindow { CustomerId = cid, CreatedAtUtc = time.GetUtcNow().UtcDateTime };
        Input.ApplyTo(w, monitors);
        db.MaintenanceWindows.Add(w);
        await db.SaveChangesAsync(ct);
        TempData["Success"] = L["Maintenance window \"{0}\" was added.", w.Name].Value;
        return RedirectToPage("Index");
    }

    private async Task LoadAsync(AppDbContext db, int cid, CancellationToken ct) =>
        Monitors = await MonitorChoicesAsync(db, cid, ct);

    /// <summary>The customer's monitors for the scope selection.</summary>
    public static async Task<IReadOnlyList<(int Id, string Name)>> MonitorChoicesAsync(AppDbContext db, int customerId, CancellationToken ct) =>
        (await db.Monitors.AsNoTracking().Where(m => m.CustomerId == customerId).OrderBy(m => m.Name)
            .Select(m => new { m.Id, m.Name }).ToListAsync(ct))
        .Select(m => (m.Id, m.Name)).ToList();
}
