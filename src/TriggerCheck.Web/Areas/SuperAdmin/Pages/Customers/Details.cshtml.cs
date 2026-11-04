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
    /// <summary>Latest subscription that was ever activated (BIZ-2), if any.</summary>
    public Subscription? Subscription { get; private set; }
    public List<PaymentEvent> PaymentEvents { get; private set; } = [];

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
        Subscription = await db.Subscriptions.AsNoTracking().Include(s => s.Plan)
            .Where(s => s.CustomerId == id && s.Status != SubscriptionStatus.Pending && s.Status != SubscriptionStatus.Superseded && s.Status != SubscriptionStatus.Failed)
            .OrderByDescending(s => s.Id).FirstOrDefaultAsync();
        PaymentEvents = await db.PaymentEvents.AsNoTracking().Where(e => e.CustomerId == id)
            .OrderByDescending(e => e.OccurredAtUtc).Take(10).ToListAsync();
        return Page();
    }

    /// <summary>M3: give a trial customer more time (from the current end, or from now when it already passed).</summary>
    public async Task<IActionResult> OnPostExtendTrialAsync(int id, int days, [FromServices] TimeProvider time)
    {
        var c = await db.Customers.FindAsync(id);
        if (c is null) return NotFound();
        if (c.BillingStatus != BillingStatus.Trial || days is < 1 or > 90)
        {
            TempData["Error"] = L["The trial can only be extended for a customer in trial, by 1 to 90 days."].Value;
            return RedirectToPage(new { id });
        }
        var now = time.GetUtcNow().UtcDateTime;
        c.TrialEndsAtUtc = (c.TrialEndsAtUtc is DateTime end && end > now ? end : now).AddDays(days);
        c.TrialReminderSentAtUtc = null;
        c.SignupPending = false;
        await db.SaveChangesAsync();
        TempData["Success"] = L["The trial now ends on {0}.", Fmt.Date(c.TrialEndsAtUtc)].Value;
        return RedirectToPage(new { id });
    }

    /// <summary>M3: end the trial now: the customer moves to the fallback plan (intervals clamped) and is e-mailed.</summary>
    public async Task<IActionResult> OnPostEndTrialAsync(int id, [FromServices] Billing.BillingService billing, [FromServices] Billing.BillingMailer mailer)
    {
        var c = await db.Customers.Include(x => x.Plan).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        c.SignupPending = false;
        if (c.BillingStatus != BillingStatus.Trial || !await billing.DowngradeToFallbackAsync(c, null, BillingStatus.Trial, mailer.TrialEndedReason, HttpContext.RequestAborted))
            TempData["Error"] = L["The customer is not in trial."].Value;
        else
            TempData["Success"] = L["The trial was ended; the customer is now on the {0} plan.", c.Plan.Name].Value;
        return RedirectToPage(new { id });
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
