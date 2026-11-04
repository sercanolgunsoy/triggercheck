using Microsoft.AspNetCore.Identity;
using TriggerCheck.Infrastructure.Identity;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Failed-attempt limit for the change-password forms: a wrong current password counts as a failed sign-in
/// (Identity lockout, same limit as the login page). When the account becomes locked the session is ended.
/// </summary>
public static class PasswordChangeLockout
{
    /// <summary>Records the outcome of ChangePasswordAsync. Returns true when the account is now locked and signed out.</summary>
    public static async Task<bool> RecordAsync(SignInManager<ApplicationUser> signIn, ApplicationUser user, IdentityResult result)
    {
        var users = signIn.UserManager;
        if (result.Succeeded)
        {
            await users.ResetAccessFailedCountAsync(user);
            return false;
        }
        if (!result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch))) return false;

        await users.AccessFailedAsync(user);
        if (!await users.IsLockedOutAsync(user)) return false;
        await signIn.SignOutAsync();
        return true;
    }
}
