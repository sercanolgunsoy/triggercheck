using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Regions;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

/// <summary>FEAT-6: model of the region breakdown on the monitor details page (_MonitorRegions).</summary>
public sealed record RegionPanel(List<RegionRow> Rows, bool CanManage, int MonitorId);

/// <summary>
/// FEAT-6: the remote regions a monitor is checked from, in addition to the central one (CustomerAdmin only, own
/// customer's monitors only). The plan limits how many (<see cref="RegionRules.MaxRemoteRegions"/>); heartbeat monitors
/// have no regions. With two or more voting regions the monitor's status is decided by majority.
/// </summary>
public class RegionsModel(AppDbContext db, ICurrentCustomer current, TimeProvider time, IOptionsMonitor<RegionOptions> regionOptions, Text<AppResource> L) : PageModel
{
    public record Option(int Id, string Code, string Name, bool IsEnabled, bool IsStale, bool Selected);

    public MonitorEntity Monitor { get; private set; } = null!;
    public List<Option> Options { get; private set; } = [];
    public int MaxRemote { get; private set; }
    public string PlanName { get; private set; } = "";

    [BindProperty] public List<int> RegionIds { get; set; } = [];

    /// <summary>The customer's plan allows remote regions and at least one region is enabled.</summary>
    public static async Task<bool> PlanAllowsRegionsAsync(AppDbContext db, int customerId, CancellationToken ct) =>
        await db.Customers.AnyAsync(c => c.Id == customerId && c.Plan.MaxRegions != 1, ct) && await db.Regions.AnyAsync(r => r.IsEnabled, ct);

    private bool IsAdmin => User.IsInRole(Roles.CustomerAdmin);

    private async Task<bool> LoadAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var m = await db.Monitors.AsNoTracking().Include(x => x.Customer).ThenInclude(c => c.Plan)
            .FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (m is null) return false;
        Monitor = m;
        PlanName = m.Customer.Plan.Name;
        var selected = await db.MonitorRegionStates.Where(s => s.MonitorId == id).Select(s => s.RegionId).ToListAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var regions = await db.Regions.AsNoTracking().Where(r => r.IsEnabled || selected.Contains(r.Id)).OrderBy(r => r.Code).ToListAsync(ct);
        Options = regions.Select(r => new Option(r.Id, r.Code, r.Name, r.IsEnabled, RegionQuorum.IsAgentStale(r, now, regionOptions.CurrentValue),
            selected.Contains(r.Id))).ToList();
        MaxRemote = RegionRules.MaxRemoteRegions(m.Customer.Plan, Options.Count);
        return true;
    }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        return await LoadAsync(id, ct) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        if (!await LoadAsync(id, ct)) return NotFound();
        if (!RegionRules.SupportsRegions(Monitor.Type))
        {
            TempData["Error"] = L["Heartbeat monitors receive pings and are not checked from regions."].Value;
            return RedirectToPage("Details", new { id });
        }

        var wanted = RegionIds.Distinct().ToList();
        // Only listed regions (enabled ones, or ones already assigned) can be chosen.
        if (wanted.Any(r => Options.All(o => o.Id != r)))
            ModelState.AddModelError("", L["Choose one of the listed regions."]);
        else if (wanted.Count > MaxRemote)
            ModelState.AddModelError("", MaxRemote == 0
                ? L["Your plan ({0}) checks monitors from the main region only.", PlanName]
                : L["Your plan ({0}) allows at most {1} additional region(s) per monitor.", PlanName, MaxRemote]);
        if (!ModelState.IsValid)
        {
            Options = Options.Select(o => o with { Selected = wanted.Contains(o.Id) }).ToList();
            return Page();
        }

        var existing = await db.MonitorRegionStates.Where(s => s.MonitorId == id).ToListAsync(ct);
        db.MonitorRegionStates.RemoveRange(existing.Where(s => !wanted.Contains(s.RegionId)));
        foreach (var regionId in wanted.Where(r => existing.All(s => s.RegionId != r)))
            db.MonitorRegionStates.Add(new MonitorRegionState { MonitorId = id, RegionId = regionId }); // due right away
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A parallel save added the same region, or the monitor was deleted meanwhile.
            TempData["Error"] = L["The regions were changed in the meantime. Review them and try again."].Value;
            return RedirectToPage("Regions", new { id });
        }
        TempData["Success"] = wanted.Count == 0
            ? L["\"{0}\" is checked from the main region only.", Monitor.Name].Value
            : L["\"{0}\" is now checked from {1} region(s); its status is decided by majority.", Monitor.Name, wanted.Count + 1].Value;
        return RedirectToPage("Details", new { id });
    }
}
