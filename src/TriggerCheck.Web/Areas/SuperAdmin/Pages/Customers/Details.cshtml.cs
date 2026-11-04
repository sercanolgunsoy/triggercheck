using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Areas.SuperAdmin.Services;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Customers;

[Authorize(Policy = Policies.SuperAdmin)]
public class DetailsModel(AppDbContext db, UserManager<ApplicationUser> users, Text<AdminResource> L) : PageModel
{
    public Customer Customer { get; private set; } = null!;
    public List<UserRow> Users { get; private set; } = [];
    public List<MonitorEntity> Monitors { get; private set; } = [];
    public List<Incident> OpenIncidents { get; private set; } = [];
    public int AlertChannelCount { get; private set; }

    public record UserRow(string Id, string Email, string FullName, string Role, bool IsActive, DateTime CreatedAtUtc, bool LockedOut);

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var c = await db.Customers.AsNoTracking().Include(x => x.Plan).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        Customer = c;

        var userList = await db.Users.AsNoTracking().Where(u => u.CustomerId == id).OrderBy(u => u.Email).ToListAsync();
        var roleRows = await (from ur in db.UserRoles
                              join r in db.Roles on ur.RoleId equals r.Id
                              join u in db.Users on ur.UserId equals u.Id
                              where u.CustomerId == id
                              select new { ur.UserId, r.Name }).ToListAsync();
        Users = userList.Select(u => new UserRow(u.Id, u.Email ?? "", u.FullName,
            string.Join(", ", roleRows.Where(r => r.UserId == u.Id).Select(r => r.Name)),
            u.IsActive, u.CreatedAtUtc, u.LockoutEnd is DateTimeOffset le && le > DateTimeOffset.UtcNow)).ToList();

        Monitors = await db.Monitors.AsNoTracking().Where(m => m.CustomerId == id).OrderBy(m => m.Name).ToListAsync();
        OpenIncidents = await db.Incidents.AsNoTracking().Include(i => i.Monitor)
            .Where(i => i.Monitor.CustomerId == id && i.ResolvedAtUtc == null)
            .OrderByDescending(i => i.StartedAtUtc).ToListAsync();
        AlertChannelCount = await db.AlertChannels.CountAsync(a => a.CustomerId == id);
        return Page();
    }

    public async Task<IActionResult> OnPostSuspendAsync(int id)
    {
        var c = await db.Customers.FindAsync(id);
        if (c is null) return NotFound();
        if (!c.IsActive) { TempData["Error"] = L["The customer is already suspended."].Value; return RedirectToPage(new { id }); }

        c.IsActive = false;
        await db.SaveChangesAsync();
        // Invalidate existing sessions (security stamp) in addition to SessionValidator's per-request active check.
        await SuperAdminOps.InvalidateCustomerSessionsAsync(users, id);
        TempData["Success"] = L["\"{0}\" was suspended. Its users cannot sign in and its monitors are not checked.", c.Name].Value;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostActivateAsync(int id)
    {
        var c = await db.Customers.FindAsync(id);
        if (c is null) return NotFound();
        if (c.IsActive) { TempData["Error"] = L["The customer is already active."].Value; return RedirectToPage(new { id }); }

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            c.IsActive = true;
            await db.SaveChangesAsync();
            // Resume checking immediately. Set-based and bumping the concurrency token: never conflicts with a running
            // check, and an in-flight check re-reads the row afterwards.
            var now = DateTime.UtcNow;
            await db.Monitors.Where(m => m.CustomerId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextCheckAtUtc, now).SetProperty(m => m.Version, m => m.Version + 1));
            await tx.CommitAsync();
        }
        TempData["Success"] = L["\"{0}\" was activated again.", c.Name].Value;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostToggleUserAsync(int id, string userId)
    {
        var u = await users.Users.FirstOrDefaultAsync(x => x.Id == userId && x.CustomerId == id);
        if (u is null) return NotFound();
        u.IsActive = !u.IsActive;
        var r = await users.UpdateAsync(u);
        if (r.Succeeded) r = await users.UpdateSecurityStampAsync(u);
        if (!r.Succeeded) TempData["Error"] = string.Join(" ", r.Errors.Select(e => e.Description));
        else TempData["Success"] = u.IsActive
            ? L["{0} was activated.", u.Email ?? ""].Value
            : L["{0} was deactivated; its open sessions were ended.", u.Email ?? ""].Value;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteUserAsync(int id, string userId)
    {
        var u = await users.Users.FirstOrDefaultAsync(x => x.Id == userId && x.CustomerId == id);
        if (u is null) return NotFound();
        var r = await users.DeleteAsync(u);
        if (!r.Succeeded) TempData["Error"] = string.Join(" ", r.Errors.Select(e => e.Description));
        else TempData["Success"] = L["{0} was deleted.", u.Email ?? ""].Value;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnlockUserAsync(int id, string userId)
    {
        var u = await users.Users.FirstOrDefaultAsync(x => x.Id == userId && x.CustomerId == id);
        if (u is null) return NotFound();
        await users.SetLockoutEndDateAsync(u, null);
        await users.ResetAccessFailedCountAsync(u);
        TempData["Success"] = L["{0} was unlocked.", u.Email ?? ""].Value;
        return RedirectToPage(new { id });
    }
}
