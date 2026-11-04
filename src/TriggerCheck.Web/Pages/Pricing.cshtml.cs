using Microsoft.AspNetCore.Mvc.RazorPages;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Pages;

/// <summary>Public pricing: plan cards and a feature comparison built from the active plans.</summary>
public class PricingModel(AppDbContext db, IConfiguration config) : PageModel
{
    public List<Plan> Plans { get; private set; } = [];
    public string ContactEmail => Marketing.ContactEmail(config);

    public async Task OnGetAsync() => Plans = await Marketing.ActivePlansAsync(db, HttpContext.RequestAborted);
}
