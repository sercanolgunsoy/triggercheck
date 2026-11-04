using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Billing;

namespace TriggerCheck.Web.Pages.Account;

/// <summary>
/// Password reset request. Always shows the same result (no account enumeration); an active account gets a reset
/// link (Identity token, one day). Rate limited per client IP.
/// </summary>
[EnableRateLimiting(BillingSetup.PasswordResetPolicy)]
public class ForgotPasswordModel(UserManager<ApplicationUser> users, BillingMailer mailer, IOptionsMonitor<BillingOptions> options,
    ILogger<ForgotPasswordModel> logger) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public bool Sent { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "E-mail is required."), EmailAddress(ErrorMessage = "Enter a valid e-mail address."), Display(Name = "E-mail")]
        public string Email { get; set; } = "";
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();
        var user = await users.FindByEmailAsync(Input.Email.Trim());
        if (user is { IsActive: true, Email: not null })
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var link = BillingSetup.PublicUrl(Request, options.CurrentValue, $"/Account/ResetPassword?userId={Uri.EscapeDataString(user.Id)}&code={code}");
            await mailer.SendPasswordResetAsync(user.Email, link, ct);
        }
        else logger.LogInformation("Password reset requested for an unknown or inactive account.");
        Sent = true;
        return Page();
    }
}
