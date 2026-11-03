using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.App.Pages.Users;

[Authorize(Policy = Policies.CustomerAdmin)]
public class CreateModel(UserManager<ApplicationUser> users, ICurrentCustomer current) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "Ad soyad zorunludur"), StringLength(200)]
        [Display(Name = "Ad soyad")]
        public string FullName { get; set; } = "";

        [Required(ErrorMessage = "E-posta zorunludur"), EmailAddress(ErrorMessage = "Geçerli bir e-posta adresi girin"), StringLength(256)]
        [Display(Name = "E-posta")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Şifre zorunludur"), StringLength(100, MinimumLength = 8, ErrorMessage = "Şifre en az 8 karakter olmalıdır")]
        [DataType(DataType.Password), Display(Name = "Şifre")]
        public string Password { get; set; } = "";

        [Compare(nameof(Password), ErrorMessage = "Şifreler eşleşmiyor")]
        [DataType(DataType.Password), Display(Name = "Şifre (tekrar)")]
        public string ConfirmPassword { get; set; } = "";

        [Display(Name = "Rol")]
        public string Role { get; set; } = Roles.CustomerViewer;
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Input.Role is not (Roles.CustomerAdmin or Roles.CustomerViewer))
            ModelState.AddModelError("Input.Role", "Geçersiz rol.");
        if (!ModelState.IsValid) return Page();

        var email = Input.Email.Trim();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = Input.FullName.Trim(),
            CustomerId = current.RequiredCustomerId,
            IsActive = true
        };
        var result = await users.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors.Select(IdentityErrorsTr.Describe).Distinct())
                ModelState.AddModelError("", e);
            return Page();
        }
        await users.AddToRoleAsync(user, Input.Role);

        TempData["Success"] = $"{email} kullanıcısı oluşturuldu ({AppFormat.RoleName(Input.Role)}).";
        return RedirectToPage("Index");
    }
}
