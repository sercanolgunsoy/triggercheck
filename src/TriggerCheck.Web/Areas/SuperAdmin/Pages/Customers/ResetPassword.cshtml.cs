using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Customers;

[Authorize(Policy = Policies.SuperAdmin)]
public class ResetPasswordModel(UserManager<ApplicationUser> users, Text<AdminResource> L) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "New password is required."), DataType(DataType.Password), StringLength(100, MinimumLength = 8, ErrorMessage = "The password must be at least {2} characters long.")]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = "";

    public ApplicationUser TargetUser { get; private set; } = null!;
    public int CustomerId { get; private set; }

    private async Task<bool> LoadAsync(int customerId, string userId)
    {
        var u = await users.Users.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == userId && x.CustomerId == customerId);
        if (u is null) return false;
        TargetUser = u;
        CustomerId = customerId;
        return true;
    }

    public async Task<IActionResult> OnGetAsync(int customerId, string userId) =>
        await LoadAsync(customerId, userId) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync(int customerId, string userId)
    {
        if (!await LoadAsync(customerId, userId)) return NotFound();
        if (!ModelState.IsValid) return Page();

        var token = await users.GeneratePasswordResetTokenAsync(TargetUser);
        var r = await users.ResetPasswordAsync(TargetUser, token, NewPassword); // also rotates the security stamp
        if (!r.Succeeded)
        {
            foreach (var e in r.Errors) ModelState.AddModelError("", e.Description);
            return Page();
        }
        await users.SetLockoutEndDateAsync(TargetUser, null);
        await users.ResetAccessFailedCountAsync(TargetUser);

        TempData["Success"] = L["The password of {0} was reset; its open sessions were ended.", TargetUser.Email ?? ""].Value;
        return RedirectToPage("Details", new { id = customerId });
    }
}
