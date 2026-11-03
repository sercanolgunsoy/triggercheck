using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.App.Pages.Account;

/// <summary>Customer/plan overview (read-only for everyone) + change own password (any customer member).</summary>
public class IndexModel(AppDbContext db, ICurrentCustomer current, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) : PageModel
{
    public Customer Customer { get; private set; } = null!;
    public int MonitorCount { get; private set; }
    public IReadOnlyList<int> AllowedIntervals { get; private set; } = [];
    public string? Email { get; private set; }
    public string? RoleName { get; private set; }

    [BindProperty] public PasswordInput Password { get; set; } = new();

    public class PasswordInput
    {
        [Required(ErrorMessage = "Mevcut şifre zorunludur"), DataType(DataType.Password), Display(Name = "Mevcut şifre")]
        public string CurrentPassword { get; set; } = "";

        [Required(ErrorMessage = "Yeni şifre zorunludur"), StringLength(100, MinimumLength = 8, ErrorMessage = "Şifre en az 8 karakter olmalıdır")]
        [DataType(DataType.Password), Display(Name = "Yeni şifre")]
        public string NewPassword { get; set; } = "";

        [Compare(nameof(NewPassword), ErrorMessage = "Şifreler eşleşmiyor")]
        [DataType(DataType.Password), Display(Name = "Yeni şifre (tekrar)")]
        public string ConfirmPassword { get; set; } = "";
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Customer = await current.GetCustomerAsync(ct);
        MonitorCount = await db.Monitors.CountAsync(m => m.CustomerId == Customer.Id, ct);
        AllowedIntervals = PlanRules.AllowedIntervals(Customer.Plan);
        Email = User.Identity?.Name;
        RoleName = AppFormat.RoleName(User.IsInRole(Roles.CustomerAdmin) ? Roles.CustomerAdmin : Roles.CustomerViewer);
    }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostChangePasswordAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (!ModelState.IsValid) return Page();

        var user = await users.GetUserAsync(User);
        if (user is null || user.CustomerId != current.RequiredCustomerId) return Forbid();

        var r = await users.ChangePasswordAsync(user, Password.CurrentPassword, Password.NewPassword);
        if (!r.Succeeded)
        {
            foreach (var e in r.Errors.Select(IdentityErrorsTr.Describe).Distinct())
                ModelState.AddModelError("", e);
            return Page();
        }
        // ChangePassword rotates the security stamp; re-issue this session's cookie so the user stays signed in.
        await signIn.RefreshSignInAsync(user);
        TempData["Success"] = "Şifreniz değiştirildi.";
        return RedirectToPage("Index");
    }
}
