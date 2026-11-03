using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Users;

[Authorize(Policy = Policies.CustomerAdmin)]
public class IndexModel(AppDbContext db, UserManager<ApplicationUser> users, ICurrentCustomer current, Text<AppResource> L) : PageModel
{
    public record Row(string Id, string Email, string FullName, string? Role, bool IsActive, DateTime CreatedAtUtc, bool IsSelf);

    public List<Row> Users { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var me = users.GetUserId(User);
        var list = await db.Users.AsNoTracking().Where(u => u.CustomerId == cid).OrderBy(u => u.Email).ToListAsync(ct);
        var ids = list.Select(u => u.Id).ToList();
        var roles = await (from ur in db.UserRoles
                           join r in db.Roles on ur.RoleId equals r.Id
                           where ids.Contains(ur.UserId)
                           select new { ur.UserId, r.Name }).ToListAsync(ct);
        Users = list.Select(u => new Row(u.Id, u.Email ?? "", u.FullName,
            roles.Where(r => r.UserId == u.Id).Select(r => r.Name).FirstOrDefault(n => n is Roles.CustomerAdmin or Roles.CustomerViewer),
            u.IsActive, u.CreatedAtUtc, u.Id == me)).ToList();
    }

    /// <summary>Loads a user of THIS customer only.</summary>
    private Task<ApplicationUser?> FindAsync(string id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        return db.Users.FirstOrDefaultAsync(u => u.Id == id && u.CustomerId == cid, ct);
    }

    /// <summary>True if there is an active CustomerAdmin of this customer other than <paramref name="exceptUserId"/>.</summary>
    private async Task<bool> HasOtherActiveAdminAsync(string exceptUserId)
    {
        var cid = current.RequiredCustomerId;
        var admins = await users.GetUsersInRoleAsync(Roles.CustomerAdmin);
        return admins.Any(a => a.CustomerId == cid && a.IsActive && a.Id != exceptUserId);
    }

    private bool IsSelf(ApplicationUser u) => u.Id == users.GetUserId(User);

    private IActionResult Fail(string message)
    {
        TempData["Error"] = message;
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostChangeRoleAsync(string id, string role, CancellationToken ct)
    {
        if (role is not (Roles.CustomerAdmin or Roles.CustomerViewer)) return Fail(L["Invalid role."]);
        var u = await FindAsync(id, ct);
        if (u is null) return NotFound();
        if (IsSelf(u)) return Fail(L["You cannot change your own role."]);

        var isAdmin = await users.IsInRoleAsync(u, Roles.CustomerAdmin);
        if (isAdmin && role == Roles.CustomerViewer && u.IsActive && !await HasOtherActiveAdminAsync(u.Id))
            return Fail(L["The account must keep at least one active admin."]);

        var existing = await users.GetRolesAsync(u);
        var toRemove = existing.Where(r => r is Roles.CustomerAdmin or Roles.CustomerViewer && r != role).ToList();
        if (toRemove.Count > 0) await users.RemoveFromRolesAsync(u, toRemove);
        if (!existing.Contains(role)) await users.AddToRoleAsync(u, role);
        await users.UpdateSecurityStampAsync(u); // forces the user's cookie (role claims) to be refreshed

        TempData["Success"] = L["The role of {0} was changed to \"{1}\".", u.Email ?? "", Fmt.RoleName(role)].Value;
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostToggleActiveAsync(string id, CancellationToken ct)
    {
        var u = await FindAsync(id, ct);
        if (u is null) return NotFound();
        if (IsSelf(u)) return Fail(L["You cannot deactivate your own account."]);

        if (u.IsActive && await users.IsInRoleAsync(u, Roles.CustomerAdmin) && !await HasOtherActiveAdminAsync(u.Id))
            return Fail(L["The account must keep at least one active admin."]);

        u.IsActive = !u.IsActive;
        await users.UpdateAsync(u);
        await users.UpdateSecurityStampAsync(u); // drops existing sessions

        TempData["Success"] = (u.IsActive ? L["{0} was activated.", u.Email ?? ""] : L["{0} was deactivated.", u.Email ?? ""]).Value;
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id, CancellationToken ct)
    {
        var u = await FindAsync(id, ct);
        if (u is null) return NotFound();
        if (IsSelf(u)) return Fail(L["You cannot delete your own account."]);

        if (u.IsActive && await users.IsInRoleAsync(u, Roles.CustomerAdmin) && !await HasOtherActiveAdminAsync(u.Id))
            return Fail(L["The account must keep at least one active admin."]);

        var r = await users.DeleteAsync(u);
        if (!r.Succeeded) return Fail(string.Join(" ", r.Errors.Select(e => IdentityErrorMessages.Describe(e, L))));

        TempData["Success"] = L["{0} was deleted.", u.Email ?? ""].Value;
        return RedirectToPage("Index");
    }
}
