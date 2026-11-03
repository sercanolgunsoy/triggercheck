using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Customers;

[Authorize(Policy = Policies.SuperAdmin)]
public class AddUserModel(AppDbContext db, UserManager<ApplicationUser> users) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public Customer Customer { get; private set; } = null!;

    public static readonly string[] AllowedRoles = [Roles.CustomerAdmin, Roles.CustomerViewer];

    public class InputModel
    {
        [Required(ErrorMessage = "E-posta zorunludur"), EmailAddress(ErrorMessage = "Geçerli bir e-posta girin"), StringLength(256)]
        [Display(Name = "E-posta")] public string Email { get; set; } = "";

        [Required(ErrorMessage = "Ad soyad zorunludur"), StringLength(200)]
        [Display(Name = "Ad soyad")] public string FullName { get; set; } = "";

        [Required(ErrorMessage = "Şifre zorunludur"), DataType(DataType.Password), StringLength(100, MinimumLength = 8, ErrorMessage = "Şifre en az 8 karakter olmalıdır")]
        [Display(Name = "Şifre")] public string Password { get; set; } = "";

        [Required, Display(Name = "Rol")] public string Role { get; set; } = Roles.CustomerViewer;
    }

    public async Task<IActionResult> OnGetAsync(int customerId)
    {
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == customerId);
        if (c is null) return NotFound();
        Customer = c;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int customerId)
    {
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == customerId);
        if (c is null) return NotFound();
        Customer = c;

        if (!AllowedRoles.Contains(Input.Role))
            ModelState.AddModelError("Input.Role", "Geçersiz rol.");
        if (ModelState.IsValid && await users.FindByEmailAsync(Input.Email.Trim()) is not null)
            ModelState.AddModelError("Input.Email", "Bu e-posta adresi ile kayıtlı bir kullanıcı zaten var.");
        if (!ModelState.IsValid) return Page();

        await using var tx = await db.Database.BeginTransactionAsync();
        var email = Input.Email.Trim();
        var user = new ApplicationUser
        {
            UserName = email, Email = email, EmailConfirmed = true,
            FullName = Input.FullName.Trim(), CustomerId = customerId, IsActive = true
        };
        var r = await users.CreateAsync(user, Input.Password);
        if (r.Succeeded) r = await users.AddToRoleAsync(user, Input.Role);
        if (!r.Succeeded)
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            foreach (var e in r.Errors) ModelState.AddModelError("", e.Description);
            return Page();
        }
        await tx.CommitAsync();

        TempData["Success"] = $"{email} kullanıcısı ({Input.Role}) eklendi.";
        return RedirectToPage("Details", new { id = customerId });
    }
}
