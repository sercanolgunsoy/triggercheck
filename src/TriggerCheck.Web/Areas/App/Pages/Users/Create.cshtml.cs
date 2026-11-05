using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Users;

[Authorize(Policy = Policies.CustomerAdmin)]
public class CreateModel(UserManager<ApplicationUser> users, AppDbContext db, ICurrentCustomer current, Text<AppResource> L) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    /// <summary>The plan's user limit (null = unlimited) and whether the customer has reached it.</summary>
    public int? MaxUsers { get; private set; }
    public bool LimitReached { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Full name is required."), StringLength(200, ErrorMessage = "Full name can be at most 200 characters.")]
        [Display(Name = "Full name")]
        public string FullName { get; set; } = "";

        [Required(ErrorMessage = "E-mail is required."), EmailAddress(ErrorMessage = "Enter a valid e-mail address."), StringLength(256, ErrorMessage = "E-mail can be at most 256 characters.")]
        [Display(Name = "E-mail")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Password is required."), StringLength(100, MinimumLength = 8, ErrorMessage = "The password must be at least 8 characters long.")]
        [DataType(DataType.Password), Display(Name = "Password")]
        public string Password { get; set; } = "";

        [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        [DataType(DataType.Password), Display(Name = "Confirm password")]
        public string ConfirmPassword { get; set; } = "";

        [Display(Name = "Role")]
        public string Role { get; set; } = Roles.CustomerViewer;
    }

    public async Task OnGetAsync(CancellationToken ct) => await LoadLimitAsync(ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Input.Role is not (Roles.CustomerAdmin or Roles.CustomerViewer))
            ModelState.AddModelError("Input.Role", L["Invalid role."]);
        if (!ModelState.IsValid)
        {
            await LoadLimitAsync(ct);
            return Page();
        }

        // The limit check and the insert run in one transaction (serialized like the monitor quota), so parallel posts
        // cannot exceed the plan's user limit.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LoadLimitAsync(ct);
        if (LimitReached)
        {
            ModelState.AddModelError("", LimitMessage);
            return Page();
        }

        var email = Input.Email.Trim();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = Input.FullName.Trim(),
            CustomerId = current.RequiredCustomerId,
            IsActive = true,
            MustChangePassword = true // the password was chosen by the admin
        };
        var result = await users.CreateAsync(user, Input.Password);
        if (result.Succeeded) result = await users.AddToRoleAsync(user, Input.Role);
        if (!result.Succeeded)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            foreach (var e in result.Errors.Select(e => IdentityErrorMessages.Describe(e, L)).Distinct())
                ModelState.AddModelError("", e);
            return Page();
        }
        await tx.CommitAsync(ct);

        TempData["Success"] = L["User {0} was created ({1}).", email, Fmt.RoleName(Input.Role)].Value;
        return RedirectToPage("Index");
    }

    public string LimitMessage => MaxUsers == 1
        ? L["Your plan includes a single user. Upgrade your plan to add team members."].Value
        : L["You have reached the user limit of your plan ({0}). Upgrade your plan to add a new user.", MaxUsers ?? 0].Value;

    private async Task LoadLimitAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var plan = await db.Customers.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Plan).SingleAsync(ct);
        MaxUsers = plan.MaxUsers;
        LimitReached = !PlanRules.CanAddUser(plan, await db.Users.CountAsync(u => u.CustomerId == cid, ct));
    }
}
