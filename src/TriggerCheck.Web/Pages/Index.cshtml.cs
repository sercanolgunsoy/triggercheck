using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TriggerCheck.Domain;

namespace TriggerCheck.Web.Pages;

public class IndexModel : PageModel
{
    public IActionResult OnGet()
    {
        if (User.IsInRole(Roles.SuperAdmin)) return RedirectToPage("/Index", new { area = "SuperAdmin" });
        if (User.IsInRole(Roles.CustomerAdmin) || User.IsInRole(Roles.CustomerViewer)) return RedirectToPage("/Index", new { area = "App" });
        return Page();
    }
}
