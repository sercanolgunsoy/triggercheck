using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.App.Pages.Alerts;

/// <summary>Listing is visible to all customer members; the toggle handler is CustomerAdmin only.</summary>
public class IndexModel(AppDbContext db, ICurrentCustomer current) : PageModel
{
    public List<AlertChannel> Channels { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        Channels = await db.AlertChannels.AsNoTracking().Where(c => c.CustomerId == cid).OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostToggleAsync(int id, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.CustomerAdmin)) return Forbid();
        var cid = current.RequiredCustomerId;
        var c = await db.AlertChannels.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (c is null) return NotFound();
        c.IsEnabled = !c.IsEnabled;
        await db.SaveChangesAsync(ct);
        TempData["Success"] = $"\"{c.Name}\" kanalı {(c.IsEnabled ? "etkinleştirildi" : "devre dışı bırakıldı")}.";
        return RedirectToPage("Index");
    }
}
