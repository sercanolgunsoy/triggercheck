using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Billing;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Pages.Account;

/// <summary>
/// Target of the reset link. A successful reset also confirms the e-mail address (the link proves access to the
/// mailbox), lifts a lockout and ends other sessions (new security stamp). Invalid user and invalid token look alike.
/// </summary>
[EnableRateLimiting(BillingSetup.PasswordResetPolicy)]
public class ResetPasswordModel(UserManager<ApplicationUser> users, Text<SharedResource> L) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public bool Done { get; private set; }

    public class InputModel
    {
        public string UserId { get; set; } = "";
        public string Code { get; set; } = "";

        [Required(ErrorMessage = "New password is required."), StringLength(100, MinimumLength = 8, ErrorMessage = "The password must be at least 8 characters long.")]
        [DataType(DataType.Password), Display(Name = "New password")]
        public string Password { get; set; } = "";

        [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        [DataType(DataType.Password), Display(Name = "Confirm new password")]
        public string ConfirmPassword { get; set; } = "";
    }

    public void OnGet(string? userId, string? code) => Input = new InputModel { UserId = userId ?? "", Code = code ?? "" };

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var user = Input.UserId.Length is > 0 and <= 100 ? await users.FindByIdAsync(Input.UserId) : null;
        string? token = null;
        try { if (Input.Code.Length is > 0 and <= 2000) token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(Input.Code)); }
        catch (FormatException) { }
        if (user is null || token is null)
        {
            ModelState.AddModelError("", L["This link is invalid or has expired. Request a new one."]);
            return Page();
        }

        var r = await users.ResetPasswordAsync(user, token, Input.Password);
        if (!r.Succeeded)
        {
            if (r.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)))
                ModelState.AddModelError("", L["This link is invalid or has expired. Request a new one."]);
            else
                foreach (var e in r.Errors) ModelState.AddModelError("Input.Password", e.Description);
            return Page();
        }
        if (!user.EmailConfirmed || user.MustChangePassword)
        {
            user.EmailConfirmed = true;
            user.MustChangePassword = false;
            await users.UpdateAsync(user);
        }
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        Done = true;
        return Page();
    }
}
