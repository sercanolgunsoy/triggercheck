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
        Message = reason switch
        {
            "inactive" => L["Your account is inactive. Please contact your administrator."].Value,
            "changed" => L["Your account settings changed. Please sign in again."].Value,
            "locked" => L["The account is temporarily locked. Try again in a few minutes."].Value,
            _ => null
        };
    }

    /// <summary>Hash verified for unknown e-mails so they take about as long as known ones (no timing enumeration).</summary>
    private static string? _dummyHash;

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        if (!ModelState.IsValid) return Page();

        var user = await signIn.UserManager.Users.Include(u => u.Customer)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == Input.Email.ToUpperInvariant());
        if (user is null)
        {
            var hasher = signIn.UserManager.PasswordHasher;
            var dummy = new ApplicationUser();
            _dummyHash ??= hasher.HashPassword(dummy, Guid.NewGuid().ToString());
            hasher.VerifyHashedPassword(dummy, _dummyHash, Input.Password);
            ModelState.AddModelError("", L["Invalid login attempt."]);
            return Page();
        }

        Microsoft.AspNetCore.Identity.SignInResult result;
        if (!user.EmailConfirmed)
        {
            // Self-service sign-ups must confirm their address first; told only after a correct password (no enumeration).
            result = await signIn.CheckPasswordSignInAsync(user, Input.Password, lockoutOnFailure: true);
            if (result.Succeeded) { ModelState.AddModelError("", L["Please confirm your e-mail address first. Check your inbox for the link."]); return Page(); }
        }
        else if (!user.IsActive || (user.Customer is not null && !user.Customer.IsActive))
        {
            // Inactive user or suspended customer: never sign in. The specific message is shown only after a correct
            // password, so a wrong password still gets the generic message (no account enumeration).
            result = await signIn.CheckPasswordSignInAsync(user, Input.Password, lockoutOnFailure: true);
            if (result.Succeeded) { ModelState.AddModelError("", L["Your account is suspended. Please contact your administrator."]); return Page(); }
        }
        else
            result = await signIn.PasswordSignInAsync(user, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.IsLockedOut) { ModelState.AddModelError("", L["The account is temporarily locked. Try again in a few minutes."]); return Page(); }
        if (!result.Succeeded) { ModelState.AddModelError("", L["Invalid login attempt."]); return Page(); }

        if (user.MustChangePassword)
            return RedirectToPage("/Account/ChangePassword", new { returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null });

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToPage("/Index");
    }
}
