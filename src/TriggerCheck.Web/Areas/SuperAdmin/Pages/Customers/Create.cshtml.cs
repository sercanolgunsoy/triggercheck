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
using TriggerCheck.Web.Areas.SuperAdmin.Services;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Customers;

[Authorize(Policy = Policies.SuperAdmin)]
public class CreateModel(AppDbContext db, UserManager<ApplicationUser> users, ILogger<CreateModel> logger) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public List<Plan> Plans { get; private set; } = [];

    public class InputModel
    {
        [Required(ErrorMessage = "Müşteri adı zorunludur"), StringLength(200, ErrorMessage = "En fazla 200 karakter")]
        [Display(Name = "Müşteri adı")] public string Name { get; set; } = "";

        [Required(ErrorMessage = "İletişim e-postası zorunludur"), EmailAddress(ErrorMessage = "Geçerli bir e-posta girin"), StringLength(256)]
        [Display(Name = "İletişim e-postası")] public string ContactEmail { get; set; } = "";

        [Required(ErrorMessage = "Plan seçin"), Display(Name = "Plan")] public int? PlanId { get; set; }

        [Required(ErrorMessage = "Ad soyad zorunludur"), StringLength(200)]
        [Display(Name = "Yönetici ad soyad")] public string AdminFullName { get; set; } = "";

        [Required(ErrorMessage = "Yönetici e-postası zorunludur"), EmailAddress(ErrorMessage = "Geçerli bir e-posta girin"), StringLength(256)]
        [Display(Name = "Yönetici e-postası")] public string AdminEmail { get; set; } = "";

        [Required(ErrorMessage = "Şifre zorunludur"), DataType(DataType.Password), StringLength(100, MinimumLength = 8, ErrorMessage = "Şifre en az 8 karakter olmalıdır")]
        [Display(Name = "Yönetici şifresi")] public string AdminPassword { get; set; } = "";
    }

    public async Task OnGetAsync()
    {
        Plans = await SuperAdminOps.SelectablePlansAsync(db);
        Input.PlanId = Plans.FirstOrDefault(p => p.Name == "Standard")?.Id ?? Plans.FirstOrDefault()?.Id;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Plans = await SuperAdminOps.SelectablePlansAsync(db);
        var plan = Plans.FirstOrDefault(p => p.Id == Input.PlanId && p.IsActive);
        if (Input.PlanId is not null && plan is null)
            ModelState.AddModelError("Input.PlanId", "Geçerli ve aktif bir plan seçin.");
        if (ModelState.IsValid && await users.FindByEmailAsync(Input.AdminEmail.Trim()) is not null)
            ModelState.AddModelError("Input.AdminEmail", "Bu e-posta adresi ile kayıtlı bir kullanıcı zaten var.");
        if (!ModelState.IsValid) return Page();

        // Customer + its first admin user are created atomically: UserManager shares this scoped DbContext,
        // so both writes run inside the same database transaction.
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var customer = new Customer
            {
                Name = Input.Name.Trim(),
                ContactEmail = Input.ContactEmail.Trim(),
                PlanId = plan!.Id,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();

            var email = Input.AdminEmail.Trim();
            var user = new ApplicationUser
            {
                UserName = email, Email = email, EmailConfirmed = true,
                FullName = Input.AdminFullName.Trim(), CustomerId = customer.Id, IsActive = true
            };
            var result = await users.CreateAsync(user, Input.AdminPassword);
            if (result.Succeeded) result = await users.AddToRoleAsync(user, Roles.CustomerAdmin);
            if (!result.Succeeded)
            {
                await tx.RollbackAsync();
                db.ChangeTracker.Clear();
                foreach (var e in result.Errors) ModelState.AddModelError("", e.Description);
                return Page();
            }

            await tx.CommitAsync();
            TempData["Success"] = $"\"{customer.Name}\" müşterisi ve yönetici kullanıcısı ({email}) oluşturuldu.";
            return RedirectToPage("Details", new { id = customer.Id });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Customer creation failed");
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            ModelState.AddModelError("", "Müşteri oluşturulamadı: " + ex.Message);
            return Page();
        }
    }
}
