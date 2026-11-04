using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Areas.App.Pages.StatusPages;

/// <summary>FEAT-4: the customer's public status pages. Read-only for viewers.</summary>
public class IndexModel(AppDbContext db, ICurrentCustomer current) : PageModel
{
    public record Row(int Id, string Title, string Slug, StatusPageVisibility Visibility, int Monitors, int Subscribers, string? Domain, bool DomainVerified);

    public List<Row> Pages { get; private set; } = [];
    public Plan Plan { get; private set; } = null!;
    public bool CanAdd { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        Plan = (await current.GetCustomerAsync(ct)).Plan;
        Pages = await db.StatusPages.AsNoTracking().Where(p => p.CustomerId == cid).OrderBy(p => p.Title).ThenBy(p => p.Id)
            .Select(p => new Row(p.Id, p.Title, p.Slug, p.Visibility, p.Monitors.Count,
                db.StatusPageSubscribers.Count(s => s.StatusPageId == p.Id && s.ConfirmedAtUtc != null),
                p.CustomDomain, p.CustomDomainVerifiedAtUtc != null))
            .ToListAsync(ct);
        CanAdd = StatusPageRules.CanAddPage(Plan, Pages.Count);
    }
}
