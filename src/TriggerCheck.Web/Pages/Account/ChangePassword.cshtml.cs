using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Pages.Account;

/// <summary>Lets the signed-in user set a new password; mandatory while ApplicationUser.MustChangePassword is set.</summary>
[Authorize]
public class ChangePasswordModel(SignInManager<ApplicationUser> signIn, ILogger<ChangePasswordModel> logger, Text<SharedResource> L) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public bool Required { get; private set; }

    public class InputModel
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

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await signIn.UserManager.GetUserAsync(User);
        if (user is null) return Challenge();
        Required = user.MustChangePassword;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        var user = await signIn.UserManager.GetUserAsync(User);
        if (user is null) return Challenge();
        Required = user.MustChangePassword;
        if (ModelState.IsValid && Input.NewPassword == Input.CurrentPassword)
            ModelState.AddModelError("Input.NewPassword", L["The new password must be different from the current one."]);
        if (!ModelState.IsValid) return Page();

        var (outcome, result) = await PasswordChangeLockout.ChangeAsync(signIn, user, Input.CurrentPassword, Input.NewPassword);
        switch (outcome)
        {
            case PasswordChangeOutcome.LockedNow:
                return RedirectToPage("/Account/Login", new { reason = "locked" });
            case PasswordChangeOutcome.AlreadyLocked:
                ModelState.AddModelError("", L["Too many failed attempts. Try again in a few minutes."]);
                return Page();
            case PasswordChangeOutcome.Failed:
                foreach (var e in result!.Errors.Select(e => IdentityErrorMessages.Describe(e, L)).Distinct()) ModelState.AddModelError("", e);
                return Page();
        }
        if (user.MustChangePassword)
        {
            user.MustChangePassword = false;
            var cleared = await signIn.UserManager.UpdateAsync(user);
            if (!cleared.Succeeded)
                logger.LogError("Password of user {UserId} was changed but clearing MustChangePassword failed: {Errors}",
                    user.Id, string.Join("; ", cleared.Errors.Select(e => e.Code)));
        }

        // Re-issue the cookie with the new security stamp and without the must_change_password claim.
        await signIn.RefreshSignInAsync(user);
        TempData["Success"] = L["Your password was changed."].Value;
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToPage("/Index");
    }
}
