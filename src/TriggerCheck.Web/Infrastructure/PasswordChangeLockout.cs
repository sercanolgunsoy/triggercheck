using Microsoft.AspNetCore.Identity;
using TriggerCheck.Infrastructure.Identity;

namespace TriggerCheck.Web.Infrastructure;

public enum PasswordChangeOutcome
{
    Changed,
    /// <summary>Validation error or wrong current password; see the IdentityResult.</summary>
    Failed,
    /// <summary>The account was already locked (e.g. by failed logins): nothing was changed, the session is kept.</summary>
    AlreadyLocked,
    /// <summary>This attempt locked the account; the session was ended.</summary>
    LockedNow
}

/// <summary>
/// Failed-attempt limit for the change-password forms: a wrong current password counts as a failed sign-in
/// (Identity lockout, same limit as the login page). Only the transition unlocked -> locked caused by this attempt
/// ends the session, so a lockout triggered by someone else on the login page cannot sign the real user out.
/// </summary>
public static class PasswordChangeLockout
{
    public static async Task<(PasswordChangeOutcome Outcome, IdentityResult? Result)> ChangeAsync(
        SignInManager<ApplicationUser> signIn, ApplicationUser user, string currentPassword, string newPassword)
    {
        var users = signIn.UserManager;
        if (await users.IsLockedOutAsync(user)) return (PasswordChangeOutcome.AlreadyLocked, null);

        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (result.Succeeded)
        {
            await users.ResetAccessFailedCountAsync(user);
            return (PasswordChangeOutcome.Changed, result);
        }
        if (!result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
            return (PasswordChangeOutcome.Failed, result);

        await users.AccessFailedAsync(user);
        if (!await users.IsLockedOutAsync(user)) return (PasswordChangeOutcome.Failed, result);
        await signIn.SignOutAsync();
        return (PasswordChangeOutcome.LockedNow, result);
    }
}
