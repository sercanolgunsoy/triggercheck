using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.App.Pages.Incidents;

public class IndexModel(AppDbContext db, ICurrentCustomer current) : PageModel
{
    public const int PageSize = 50;

    public record Row(int Id, int MonitorId, string MonitorName, DateTime StartedAtUtc, DateTime? ResolvedAtUtc, string? Cause);

    /// <summary>"open", "resolved" or empty (all).</summary>
    [BindProperty(SupportsGet = true)] public string? Filter { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public List<Row> Items { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int OpenCount { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var q = db.Incidents.AsNoTracking().Where(i => i.Monitor.CustomerId == cid);
        OpenCount = await q.CountAsync(i => i.ResolvedAtUtc == null, ct);

        q = Filter switch
        {
            "open" => q.Where(i => i.ResolvedAtUtc == null),
            "resolved" => q.Where(i => i.ResolvedAtUtc != null),
            _ => q
        };
        if (Filter is not ("open" or "resolved")) Filter = null;

        TotalCount = await q.CountAsync(ct);
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);

        Items = await q
            .OrderBy(i => i.ResolvedAtUtc != null).ThenByDescending(i => i.StartedAtUtc)
            .Skip((PageNumber - 1) * PageSize).Take(PageSize)
            .Select(i => new Row(i.Id, i.MonitorId, i.Monitor.Name, i.StartedAtUtc, i.ResolvedAtUtc, i.Cause))
            .ToListAsync(ct);
    }
}
