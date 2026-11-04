using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Customers;

[Authorize(Policy = Policies.SuperAdmin)]
public class IndexModel(AppDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    /// <summary>"active", "suspended" or empty (all).</summary>
    [BindProperty(SupportsGet = true)] public string? State { get; set; }
    [BindProperty(SupportsGet = true)] public int? PlanId { get; set; }

    public List<Plan> Plans { get; private set; } = [];
    public List<Row> Rows { get; private set; } = [];

    public record Row(int Id, string Name, string ContactEmail, string PlanName, bool IsActive, int Monitors, int MaxMonitors, int Users, DateTime CreatedAtUtc,
        BillingStatus BillingStatus = BillingStatus.None, DateTime? TrialEndsAtUtc = null);

    public async Task OnGetAsync()
    {
        Plans = await db.Plans.AsNoTracking().OrderByDescending(p => p.MinIntervalSeconds).ThenBy(p => p.Name).ToListAsync();

        var q = db.Customers.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var like = $"%{Q.Trim()}%";
            q = q.Where(c => EF.Functions.Like(c.Name, like) || EF.Functions.Like(c.ContactEmail, like)
                || db.Users.Any(u => u.CustomerId == c.Id && EF.Functions.Like(u.Email!, like)));
        }
        if (State == "active") q = q.Where(c => c.IsActive);
        else if (State == "suspended") q = q.Where(c => !c.IsActive);
        if (PlanId is int pid) q = q.Where(c => c.PlanId == pid);

        Rows = await q.OrderBy(c => c.Name)
            .Select(c => new Row(c.Id, c.Name, c.ContactEmail, c.Plan.Name, c.IsActive,
                c.Monitors.Count, c.Plan.MaxMonitors,
                db.Users.Count(u => u.CustomerId == c.Id), c.CreatedAtUtc, c.BillingStatus, c.TrialEndsAtUtc))
            .ToListAsync();
    }
}
