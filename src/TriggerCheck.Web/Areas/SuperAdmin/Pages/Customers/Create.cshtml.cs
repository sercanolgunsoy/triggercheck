using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
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
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Customers;

[Authorize(Policy = Policies.SuperAdmin)]
public class CreateModel(AppDbContext db, UserManager<ApplicationUser> users, ILogger<CreateModel> logger, Text<AdminResource> L) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public List<Plan> Plans { get; private set; } = [];

    public class InputModel
    {
        [Required(ErrorMessage = "Customer name is required."), StringLength(200, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "Customer name")] public string Name { get; set; } = "";

        [Required(ErrorMessage = "Contact e-mail is required."), EmailAddress(ErrorMessage = "Enter a valid e-mail."), StringLength(256, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "Contact e-mail")] public string ContactEmail { get; set; } = "";

        [Required(ErrorMessage = "Select a plan."), Display(Name = "Plan")] public int? PlanId { get; set; }

        [Required(ErrorMessage = "Full name is required."), StringLength(200, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "Admin full name")] public string AdminFullName { get; set; } = "";

        [Required(ErrorMessage = "Admin e-mail is required."), EmailAddress(ErrorMessage = "Enter a valid e-mail."), StringLength(256, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "Admin e-mail")] public string AdminEmail { get; set; } = "";

        [Required(ErrorMessage = "Password is required."), DataType(DataType.Password), StringLength(100, MinimumLength = 8, ErrorMessage = "The password must be at least {2} characters long.")]
        [Display(Name = "Admin password")] public string AdminPassword { get; set; } = "";
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
            ModelState.AddModelError("Input.PlanId", L["Select a valid, active plan."]);
        if (ModelState.IsValid && await users.FindByEmailAsync(Input.AdminEmail.Trim()) is not null)
            ModelState.AddModelError("Input.AdminEmail", L["A user with this e-mail address already exists."]);
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
            TempData["Success"] = L["Customer \"{0}\" and its admin user ({1}) were created.", customer.Name, email].Value;
            return RedirectToPage("Details", new { id = customer.Id });
        }
        catch (Exception ex)
        {
            // Never show exception text in the UI; log it under a correlation id the user can quote.
            var correlationId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
            logger.LogError(ex, "Customer creation failed (correlation id {CorrelationId})", correlationId);
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            ModelState.AddModelError("", L["The customer could not be created because of an unexpected error. Reference: {0}", correlationId]);
            return Page();
        }
    }
}
