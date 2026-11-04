using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Areas.App.Pages.StatusPages;

/// <summary>Shared parts of the create / edit pages: the form model and the monitor rows (every monitor of the customer).</summary>
public abstract class StatusPageFormModel(AppDbContext db) : PageModel
{
    protected AppDbContext Db { get; } = db;

    [BindProperty] public StatusPageInput Input { get; set; } = new();
    /// <summary>Monitor id → internal monitor name (shown to the admin only, as a hint).</summary>
    public Dictionary<int, string> MonitorNames { get; private set; } = [];

    /// <summary>Expands <see cref="StatusPageInput.Monitors"/> to one row per monitor of the customer (selected ones first, in order).</summary>
    protected async Task LoadMonitorRowsAsync(int customerId, CancellationToken ct)
    {
        var all = await Db.Monitors.AsNoTracking().Where(m => m.CustomerId == customerId).OrderBy(m => m.Name).ThenBy(m => m.Id)
            .Select(m => new { m.Id, m.Name }).ToListAsync(ct);
        MonitorNames = all.ToDictionary(m => m.Id, m => m.Name);
        var chosen = Input.Monitors.Where(m => m.Selected && MonitorNames.ContainsKey(m.MonitorId)).GroupBy(m => m.MonitorId).Select(g => g.First()).ToList();
        var byId = Input.Monitors.GroupBy(m => m.MonitorId).ToDictionary(g => g.Key, g => g.First());
        Input.Monitors = chosen
            .Concat(all.Where(m => !chosen.Any(c => c.MonitorId == m.Id))
                .Select(m => new StatusPageMonitorInput { MonitorId = m.Id, Selected = false, DisplayName = byId.GetValueOrDefault(m.Id)?.DisplayName }))
            .ToList();
    }
}

[Authorize(Policy = Policies.CustomerAdmin)]
public class CreateModel(AppDbContext db, ICurrentCustomer current, TimeProvider time, Text<AppResource> L) : StatusPageFormModel(db)
{
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await LimitReachedAsync(ct)) return RedirectToPage("Index");
        await LoadMonitorRowsAsync(current.RequiredCustomerId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        if (await LimitReachedAsync(ct)) return RedirectToPage("Index");
        var monitors = await Input.ValidateAsync(ModelState, L, Db, cid, null, ct);
        if (!ModelState.IsValid)
        {
            await LoadMonitorRowsAsync(cid, ct);
            return Page();
        }
        var page = new StatusPage { CustomerId = cid, CreatedAtUtc = time.GetUtcNow().UtcDateTime };
        Input.ApplyTo(page, monitors);
        try
        {
            if (!await StatusPageRules.AddWithinLimitAsync(Db, page, (await current.GetCustomerAsync(ct)).Plan, ct))
            {
                await LimitReachedAsync(ct);
                return RedirectToPage("Index");
            }
        }
        catch (DbUpdateException)
        {
            ModelState.AddModelError("Input.Slug", L["This address is already taken."]);
            Db.ChangeTracker.Clear();
            await LoadMonitorRowsAsync(cid, ct);
            return Page();
        }
        await StatusPageSetup.EvictAsync(HttpContext.RequestServices, ct);
        TempData["Success"] = L["Status page \"{0}\" was created.", page.Title].Value;
        return RedirectToPage("Index");
    }

    private async Task<bool> LimitReachedAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var plan = (await current.GetCustomerAsync(ct)).Plan;
        if (StatusPageRules.CanAddPage(plan, await Db.StatusPages.CountAsync(p => p.CustomerId == cid, ct))) return false;
        TempData["Error"] = L["You have reached the status page limit of your plan ({0}). Upgrade your plan to add another one.", StatusPageRules.LimitText(plan)].Value;
        return true;
    }
}
