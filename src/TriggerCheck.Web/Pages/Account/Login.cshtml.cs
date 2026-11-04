using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Pages.Account;

public class LoginModel(SignInManager<ApplicationUser> signIn, Text<SharedResource> L) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public string? Message { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "E-mail is required."), EmailAddress(ErrorMessage = "Enter a valid e-mail address."), Display(Name = "E-mail")] public string Email { get; set; } = "";
        [Required(ErrorMessage = "Password is required."), DataType(DataType.Password), Display(Name = "Password")] public string Password { get; set; } = "";
        [Display(Name = "Remember me")] public bool RememberMe { get; set; }
    }

    public void OnGet(string? reason)
    {
        if (reason == "inactive") Message = L["Your account is inactive. Please contact your administrator."];
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        if (!ModelState.IsValid) return Page();

        var user = await signIn.UserManager.Users.Include(u => u.Customer)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == Input.Email.ToUpperInvariant());
        if (user is null || !user.IsActive || (user.Customer is not null && !user.Customer.IsActive))
        {
            // Same message for unknown and inactive accounts to avoid account enumeration.
            ModelState.AddModelError("", L["Invalid login attempt."]);
            return Page();
        }

        var result = await signIn.PasswordSignInAsync(user, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.IsLockedOut) { ModelState.AddModelError("", L["The account is temporarily locked. Try again in a few minutes."]); return Page(); }
        if (!result.Succeeded) { ModelState.AddModelError("", L["Invalid login attempt."]); return Page(); }

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToPage("/Index");
    }
}
