using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Account;

/// <summary>Customer/plan overview (read-only for everyone) + change own password (any customer member).</summary>
public class IndexModel(AppDbContext db, ICurrentCustomer current, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, Text<AppResource> L) : PageModel
{
    public Customer Customer { get; private set; } = null!;
    public int MonitorCount { get; private set; }
    public IReadOnlyList<int> AllowedIntervals { get; private set; } = [];
    public string? Email { get; private set; }
    public string? RoleName { get; private set; }

    [BindProperty] public PasswordInput Password { get; set; } = new();

    public class PasswordInput
    {
        [Required(ErrorMessage = "Current password is required."), DataType(DataType.Password), Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = "";

        [Required(ErrorMessage = "New password is required."), StringLength(100, MinimumLength = 8, ErrorMessage = "The password must be at least 8 characters long.")]
        [DataType(DataType.Password), Display(Name = "New password")]
        public string NewPassword { get; set; } = "";

        [Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
        [DataType(DataType.Password), Display(Name = "Confirm new password")]
        public string ConfirmPassword { get; set; } = "";
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Customer = await current.GetCustomerAsync(ct);
        MonitorCount = await db.Monitors.CountAsync(m => m.CustomerId == Customer.Id, ct);
        AllowedIntervals = PlanRules.AllowedIntervals(Customer.Plan);
        Email = User.Identity?.Name;
        RoleName = Fmt.RoleName(User.IsInRole(Roles.CustomerAdmin) ? Roles.CustomerAdmin : Roles.CustomerViewer);
    }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostChangePasswordAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (!ModelState.IsValid) return Page();

        var user = await users.GetUserAsync(User);
        if (user is null || user.CustomerId != current.RequiredCustomerId) return Forbid();

        var (outcome, r) = await PasswordChangeLockout.ChangeAsync(signIn, user, Password.CurrentPassword, Password.NewPassword);
        if (outcome == PasswordChangeOutcome.LockedNow)
            return RedirectToPage("/Account/Login", new { area = "", reason = "locked" });
        if (outcome == PasswordChangeOutcome.AlreadyLocked)
        {
            ModelState.AddModelError("", L["Too many failed attempts. Try again in a few minutes."]);
            return Page();
        }
        if (outcome == PasswordChangeOutcome.Failed)
        {
            foreach (var e in r!.Errors.Select(e => IdentityErrorMessages.Describe(e, L)).Distinct())
                ModelState.AddModelError("", e);
            return Page();
        }
        // ChangePassword rotates the security stamp; re-issue this session's cookie so the user stays signed in.
        await signIn.RefreshSignInAsync(user);
        TempData["Success"] = L["Your password was changed."].Value;
        return RedirectToPage("Index");
    }
}
