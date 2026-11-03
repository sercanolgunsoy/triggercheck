using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Identity;

namespace TriggerCheck.Web.Pages.Account;

public class LoginModel(SignInManager<ApplicationUser> signIn) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public string? Message { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "E-posta zorunludur"), EmailAddress] public string Email { get; set; } = "";
        [Required(ErrorMessage = "Şifre zorunludur"), DataType(DataType.Password)] public string Password { get; set; } = "";
        public bool RememberMe { get; set; }
    }

    public void OnGet(string? reason)
    {
        if (reason == "inactive") Message = "Hesabınız pasif durumda. Lütfen yöneticinizle iletişime geçin.";
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        if (!ModelState.IsValid) return Page();

        var user = await signIn.UserManager.Users.Include(u => u.Customer)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == Input.Email.ToUpperInvariant());
        if (user is null || !user.IsActive || (user.Customer is not null && !user.Customer.IsActive))
        {
            // Same message for unknown and inactive accounts to avoid account enumeration.
            ModelState.AddModelError("", "Geçersiz giriş denemesi.");
            return Page();
        }

        var result = await signIn.PasswordSignInAsync(user, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.IsLockedOut) { ModelState.AddModelError("", "Hesap geçici olarak kilitlendi. Birkaç dakika sonra tekrar deneyin."); return Page(); }
        if (!result.Succeeded) { ModelState.AddModelError("", "Geçersiz giriş denemesi."); return Page(); }

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToPage("/Index");
    }
}
