using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;

namespace TriggerCheck.Web.Areas.App.Support;

/// <summary>Customer panel wording for the most common ASP.NET Core Identity errors; others keep the (localized) default.</summary>
public static class IdentityErrorMessages
{
    public static string Describe(IdentityError e, IStringLocalizer L) => e.Code switch
    {
        nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName) => L["A user with this e-mail address already exists."],
        nameof(IdentityErrorDescriber.InvalidEmail) or nameof(IdentityErrorDescriber.InvalidUserName) => L["Invalid e-mail address."],
        nameof(IdentityErrorDescriber.PasswordTooShort) => L["The password must be at least 8 characters long."],
        nameof(IdentityErrorDescriber.PasswordRequiresDigit) => L["The password must contain at least one digit."],
        nameof(IdentityErrorDescriber.PasswordRequiresLower) => L["The password must contain at least one lowercase letter."],
        nameof(IdentityErrorDescriber.PasswordRequiresUpper) => L["The password must contain at least one uppercase letter."],
        nameof(IdentityErrorDescriber.PasswordRequiresNonAlphanumeric) => L["The password must contain at least one special character."],
        nameof(IdentityErrorDescriber.PasswordRequiresUniqueChars) => L["The password must contain more distinct characters."],
        nameof(IdentityErrorDescriber.PasswordMismatch) => L["The current password is incorrect."],
        _ => e.Description
    };
}
