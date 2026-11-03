using Microsoft.AspNetCore.Identity;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>Turkish messages for ASP.NET Core Identity validation errors.</summary>
public class TurkishIdentityErrorDescriber : IdentityErrorDescriber
{
    private static IdentityError E(string code, string msg) => new() { Code = code, Description = msg };

    public override IdentityError DefaultError() => E(nameof(DefaultError), "Bilinmeyen bir hata oluştu.");
    public override IdentityError ConcurrencyFailure() => E(nameof(ConcurrencyFailure), "Kayıt başka biri tarafından değiştirildi, lütfen tekrar deneyin.");
    public override IdentityError PasswordMismatch() => E(nameof(PasswordMismatch), "Şifre hatalı.");
    public override IdentityError InvalidToken() => E(nameof(InvalidToken), "Geçersiz token.");
    public override IdentityError LoginAlreadyAssociated() => E(nameof(LoginAlreadyAssociated), "Bu giriş zaten başka bir kullanıcıya bağlı.");
    public override IdentityError InvalidUserName(string? userName) => E(nameof(InvalidUserName), $"'{userName}' geçersiz bir kullanıcı adı.");
    public override IdentityError InvalidEmail(string? email) => E(nameof(InvalidEmail), $"'{email}' geçersiz bir e-posta adresi.");
    public override IdentityError DuplicateUserName(string userName) => E(nameof(DuplicateUserName), $"'{userName}' zaten kullanılıyor.");
    public override IdentityError DuplicateEmail(string email) => E(nameof(DuplicateEmail), $"'{email}' e-posta adresi zaten kullanılıyor.");
    public override IdentityError InvalidRoleName(string? role) => E(nameof(InvalidRoleName), $"'{role}' geçersiz bir rol adı.");
    public override IdentityError DuplicateRoleName(string role) => E(nameof(DuplicateRoleName), $"'{role}' rolü zaten mevcut.");
    public override IdentityError UserAlreadyHasPassword() => E(nameof(UserAlreadyHasPassword), "Kullanıcının zaten bir şifresi var.");
    public override IdentityError UserLockoutNotEnabled() => E(nameof(UserLockoutNotEnabled), "Bu kullanıcı için hesap kilitleme etkin değil.");
    public override IdentityError UserAlreadyInRole(string role) => E(nameof(UserAlreadyInRole), $"Kullanıcı zaten '{role}' rolünde.");
    public override IdentityError UserNotInRole(string role) => E(nameof(UserNotInRole), $"Kullanıcı '{role}' rolünde değil.");
    public override IdentityError PasswordTooShort(int length) => E(nameof(PasswordTooShort), $"Şifre en az {length} karakter olmalıdır.");
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => E(nameof(PasswordRequiresUniqueChars), $"Şifre en az {uniqueChars} farklı karakter içermelidir.");
    public override IdentityError PasswordRequiresNonAlphanumeric() => E(nameof(PasswordRequiresNonAlphanumeric), "Şifre en az bir özel karakter içermelidir.");
    public override IdentityError PasswordRequiresDigit() => E(nameof(PasswordRequiresDigit), "Şifre en az bir rakam ('0'-'9') içermelidir.");
    public override IdentityError PasswordRequiresLower() => E(nameof(PasswordRequiresLower), "Şifre en az bir küçük harf ('a'-'z') içermelidir.");
    public override IdentityError PasswordRequiresUpper() => E(nameof(PasswordRequiresUpper), "Şifre en az bir büyük harf ('A'-'Z') içermelidir.");
}
