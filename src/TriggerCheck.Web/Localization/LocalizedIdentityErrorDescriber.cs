using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;

namespace TriggerCheck.Web.Localization;

/// <summary>ASP.NET Core Identity validation errors in the request's UI language (English keys, Turkish in SharedResource.tr.resx).</summary>
public class LocalizedIdentityErrorDescriber(IStringLocalizer<SharedResource> l) : IdentityErrorDescriber
{
    private IdentityError E(string code, string key, params object[] args) => new() { Code = code, Description = l[key, args] };

    public override IdentityError DefaultError() => E(nameof(DefaultError), "An unknown error occurred.");
    public override IdentityError ConcurrencyFailure() => E(nameof(ConcurrencyFailure), "The record was changed by someone else. Please try again.");
    public override IdentityError PasswordMismatch() => E(nameof(PasswordMismatch), "Incorrect password.");
    public override IdentityError InvalidToken() => E(nameof(InvalidToken), "Invalid token.");
    public override IdentityError LoginAlreadyAssociated() => E(nameof(LoginAlreadyAssociated), "This login is already linked to another user.");
    public override IdentityError InvalidUserName(string? userName) => E(nameof(InvalidUserName), "'{0}' is not a valid user name.", userName ?? "");
    public override IdentityError InvalidEmail(string? email) => E(nameof(InvalidEmail), "'{0}' is not a valid e-mail address.", email ?? "");
    public override IdentityError DuplicateUserName(string userName) => E(nameof(DuplicateUserName), "'{0}' is already taken.", userName);
    public override IdentityError DuplicateEmail(string email) => E(nameof(DuplicateEmail), "The e-mail address '{0}' is already in use.", email);
    public override IdentityError InvalidRoleName(string? role) => E(nameof(InvalidRoleName), "'{0}' is not a valid role name.", role ?? "");
    public override IdentityError DuplicateRoleName(string role) => E(nameof(DuplicateRoleName), "The role '{0}' already exists.", role);
    public override IdentityError UserAlreadyHasPassword() => E(nameof(UserAlreadyHasPassword), "The user already has a password.");
    public override IdentityError UserLockoutNotEnabled() => E(nameof(UserLockoutNotEnabled), "Lockout is not enabled for this user.");
    public override IdentityError UserAlreadyInRole(string role) => E(nameof(UserAlreadyInRole), "The user is already in the '{0}' role.", role);
    public override IdentityError UserNotInRole(string role) => E(nameof(UserNotInRole), "The user is not in the '{0}' role.", role);
    public override IdentityError PasswordTooShort(int length) => E(nameof(PasswordTooShort), "The password must be at least {0} characters long.", length);
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => E(nameof(PasswordRequiresUniqueChars), "The password must contain at least {0} different characters.", uniqueChars);
    public override IdentityError PasswordRequiresNonAlphanumeric() => E(nameof(PasswordRequiresNonAlphanumeric), "The password must contain at least one special character.");
    public override IdentityError PasswordRequiresDigit() => E(nameof(PasswordRequiresDigit), "The password must contain at least one digit ('0'-'9').");
    public override IdentityError PasswordRequiresLower() => E(nameof(PasswordRequiresLower), "The password must contain at least one lowercase letter ('a'-'z').");
    public override IdentityError PasswordRequiresUpper() => E(nameof(PasswordRequiresUpper), "The password must contain at least one uppercase letter ('A'-'Z').");
    public override IdentityError RecoveryCodeRedemptionFailed() => E(nameof(RecoveryCodeRedemptionFailed), "The recovery code could not be redeemed.");
}
