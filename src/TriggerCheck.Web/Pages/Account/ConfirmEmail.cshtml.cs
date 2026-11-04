using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using TriggerCheck.Infrastructure.Identity;

namespace TriggerCheck.Web.Pages.Account;

/// <summary>Target of the sign-up confirmation link (Identity e-mail confirmation token, Base64Url encoded).</summary>
public class ConfirmEmailModel(UserManager<ApplicationUser> users, ILogger<ConfirmEmailModel> logger) : PageModel
{
    public bool Confirmed { get; private set; }

    public async Task OnGetAsync(string? userId, string? code)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(code) || code.Length > 2000) return;
        var user = await users.FindByIdAsync(userId);
        if (user is null) return;
        if (user.EmailConfirmed) { Confirmed = true; return; }

        string token;
        try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (FormatException) { return; }
        var r = await users.ConfirmEmailAsync(user, token);
        Confirmed = r.Succeeded;
        if (!r.Succeeded) logger.LogInformation("E-mail confirmation failed: {Errors}", string.Join("; ", r.Errors.Select(e => e.Code)));
    }
}
