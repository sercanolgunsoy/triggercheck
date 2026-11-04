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
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Customers;

[Authorize(Policy = Policies.SuperAdmin)]
public class AddUserModel(AppDbContext db, UserManager<ApplicationUser> users, Text<AdminResource> L) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public Customer Customer { get; private set; } = null!;

    public static readonly string[] AllowedRoles = [Roles.CustomerAdmin, Roles.CustomerViewer];

    public class InputModel
    {
        [Required(ErrorMessage = "E-mail is required."), EmailAddress(ErrorMessage = "Enter a valid e-mail."), StringLength(256, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "E-mail")] public string Email { get; set; } = "";

        [Required(ErrorMessage = "Full name is required."), StringLength(200, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "Full name")] public string FullName { get; set; } = "";

        [Required(ErrorMessage = "Password is required."), DataType(DataType.Password), StringLength(100, MinimumLength = 8, ErrorMessage = "The password must be at least {2} characters long.")]
        [Display(Name = "Password")] public string Password { get; set; } = "";

        [Required(ErrorMessage = "Role is required."), Display(Name = "Role")] public string Role { get; set; } = Roles.CustomerViewer;
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
            ModelState.AddModelError("Input.Role", L["Invalid role."]);
        if (ModelState.IsValid && await users.FindByEmailAsync(Input.Email.Trim()) is not null)
            ModelState.AddModelError("Input.Email", L["A user with this e-mail address already exists."]);
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

        TempData["Success"] = L["User {0} ({1}) was added.", email, Input.Role].Value;
        return RedirectToPage("Details", new { id = customerId });
    }
}
