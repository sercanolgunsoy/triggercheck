using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Users;

[Authorize(Policy = Policies.CustomerAdmin)]
public class CreateModel(UserManager<ApplicationUser> users, ICurrentCustomer current, Text<AppResource> L) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

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

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Input.Role is not (Roles.CustomerAdmin or Roles.CustomerViewer))
            ModelState.AddModelError("Input.Role", L["Invalid role."]);
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
            foreach (var e in result.Errors.Select(e => IdentityErrorMessages.Describe(e, L)).Distinct())
                ModelState.AddModelError("", e);
            return Page();
        }
        await users.AddToRoleAsync(user, Input.Role);

        TempData["Success"] = L["User {0} was created ({1}).", email, Fmt.RoleName(Input.Role)].Value;
        return RedirectToPage("Index");
    }
}
