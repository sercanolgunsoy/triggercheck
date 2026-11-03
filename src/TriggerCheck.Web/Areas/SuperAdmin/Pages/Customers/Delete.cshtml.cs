using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Customers;

[Authorize(Policy = Policies.SuperAdmin)]
public class DeleteModel(AppDbContext db, ILogger<DeleteModel> logger, Text<AdminResource> L) : PageModel
{
    [BindProperty, Display(Name = "Customer name")] public string? ConfirmName { get; set; }
    public Customer Customer { get; private set; } = null!;
    public int MonitorCount { get; private set; }
    public int UserCount { get; private set; }
    public int CheckResultCount { get; private set; }

    private async Task<bool> LoadAsync(int id)
    {
        var c = await db.Customers.AsNoTracking().Include(x => x.Plan).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return false;
        Customer = c;
        MonitorCount = await db.Monitors.CountAsync(m => m.CustomerId == id);
        UserCount = await db.Users.CountAsync(u => u.CustomerId == id);
        CheckResultCount = await db.CheckResults.CountAsync(r => r.Monitor.CustomerId == id);
        return true;
    }

    public async Task<IActionResult> OnGetAsync(int id) => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!string.Equals(ConfirmName?.Trim(), Customer.Name, StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(ConfirmName), L["To confirm, type the customer name exactly as shown."]);
            return Page();
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        var monitorIds = db.Monitors.Where(m => m.CustomerId == id).Select(m => m.Id);
        // Notifications have no FK to monitors, remove them explicitly. Everything else
        // (monitors -> check results / incidents, alert channels, users -> roles/claims) cascades in the database.
        await db.Notifications.Where(n => monitorIds.Contains(n.MonitorId)).ExecuteDeleteAsync();
        await db.Customers.Where(c => c.Id == id).ExecuteDeleteAsync();
        await tx.CommitAsync();

        logger.LogWarning("Customer {CustomerId} ({Name}) deleted by {User}", id, Customer.Name, User.Identity?.Name);
        TempData["Success"] = L["Customer \"{0}\" and all its data ({1} monitors, {2} users) were deleted.", Customer.Name, MonitorCount, UserCount].Value;
        return RedirectToPage("Index");
    }
}
