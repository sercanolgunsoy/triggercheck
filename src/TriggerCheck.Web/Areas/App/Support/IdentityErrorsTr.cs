using Microsoft.AspNetCore.Identity;

namespace TriggerCheck.Web.Areas.App.Support;

/// <summary>Turkish messages for the most common ASP.NET Core Identity errors.</summary>
public static class IdentityErrorsTr
{
    public static string Describe(IdentityError e) => e.Code switch
    {
        nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName) => "Bu e-posta adresiyle kayıtlı bir kullanıcı zaten var.",
        nameof(IdentityErrorDescriber.InvalidEmail) or nameof(IdentityErrorDescriber.InvalidUserName) => "Geçersiz e-posta adresi.",
        nameof(IdentityErrorDescriber.PasswordTooShort) => "Şifre en az 8 karakter olmalıdır.",
        nameof(IdentityErrorDescriber.PasswordRequiresDigit) => "Şifre en az bir rakam içermelidir.",
        nameof(IdentityErrorDescriber.PasswordRequiresLower) => "Şifre en az bir küçük harf içermelidir.",
        nameof(IdentityErrorDescriber.PasswordRequiresUpper) => "Şifre en az bir büyük harf içermelidir.",
        nameof(IdentityErrorDescriber.PasswordRequiresNonAlphanumeric) => "Şifre en az bir özel karakter içermelidir.",
        nameof(IdentityErrorDescriber.PasswordRequiresUniqueChars) => "Şifre daha fazla farklı karakter içermelidir.",
        nameof(IdentityErrorDescriber.PasswordMismatch) => "Mevcut şifre hatalı.",
        _ => e.Description
    };
}
