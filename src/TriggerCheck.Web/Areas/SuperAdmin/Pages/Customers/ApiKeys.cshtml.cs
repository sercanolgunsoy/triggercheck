using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Customers;

/// <summary>A customer's REST API keys (never a secret) with "revoke all", e.g. after a credential leak.</summary>
[Authorize(Policy = Policies.SuperAdmin)]
public class ApiKeysModel(AppDbContext db, TimeProvider time, Text<AdminResource> L) : PageModel
{
    public Customer Customer { get; private set; } = null!;
    public List<ApiKey> Keys { get; private set; } = [];
    public DateTime Now { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        Customer = c;
        Now = time.GetUtcNow().UtcDateTime;
        Keys = await db.ApiKeys.AsNoTracking().Where(k => k.CustomerId == id)
            .OrderBy(k => k.RevokedAtUtc != null).ThenByDescending(k => k.CreatedAtUtc).ToListAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAllAsync(int id, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(x => x.Id == id, ct)) return NotFound();
        var now = time.GetUtcNow().UtcDateTime;
        var n = await db.ApiKeys.Where(k => k.CustomerId == id && k.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAtUtc, now), ct);
        TempData["Success"] = L["{0} API key(s) were revoked.", n].Value;
        return RedirectToPage(new { id });
    }
}
