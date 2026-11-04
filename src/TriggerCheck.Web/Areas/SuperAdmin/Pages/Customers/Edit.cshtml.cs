using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.SuperAdmin.Services;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Customers;

[Authorize(Policy = Policies.SuperAdmin)]
public class EditModel(AppDbContext db, Text<AdminResource> L) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public List<Plan> Plans { get; private set; } = [];
    public Customer Customer { get; private set; } = null!;
    public int MonitorCount { get; private set; }
    public Dictionary<int, int> FastMonitorsPerPlan { get; private set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "Customer name is required."), StringLength(200, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "Customer name")] public string Name { get; set; } = "";

        [Required(ErrorMessage = "Contact e-mail is required."), EmailAddress(ErrorMessage = "Enter a valid e-mail."), StringLength(256, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "Contact e-mail")] public string ContactEmail { get; set; } = "";

        [Required(ErrorMessage = "Select a plan."), Display(Name = "Plan")] public int? PlanId { get; set; }
    }

    private async Task<bool> LoadAsync(int id)
    {
        var c = await db.Customers.Include(x => x.Plan).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return false;
        Customer = c;
        Plans = await SuperAdminOps.SelectablePlansAsync(db, c.PlanId);
        var intervals = await db.Monitors.Where(m => m.CustomerId == id).Select(m => m.IntervalSeconds).ToListAsync();
        MonitorCount = intervals.Count;
        FastMonitorsPerPlan = Plans.ToDictionary(p => p.Id, p => intervals.Count(i => i < p.MinIntervalSeconds));
        return true;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        Input = new InputModel { Name = Customer.Name, ContactEmail = Customer.ContactEmail, PlanId = Customer.PlanId };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        // Only active plans, or the plan the customer already has, may be selected.
        var newPlan = Plans.FirstOrDefault(p => p.Id == Input.PlanId);
        if (Input.PlanId is not null && newPlan is null)
            ModelState.AddModelError("Input.PlanId", L["Select a valid, active plan."]);
        if (!ModelState.IsValid) return Page();

        Customer.Name = Input.Name.Trim();
        Customer.ContactEmail = Input.ContactEmail.Trim();

        var messages = new List<string> { L["Customer details were saved."] };
        string? warning = null;
        if (newPlan!.Id != Customer.PlanId)
        {
            var oldPlanName = Customer.Plan.Name;
            var planEntity = await db.Plans.FirstAsync(p => p.Id == newPlan.Id);
            var result = await SuperAdminOps.ChangePlanAsync(db, Customer, planEntity);
            messages.Add(L["The plan was changed from {0} to {1}.", oldPlanName, planEntity.Name]);
            if (result.AdjustedMonitors > 0)
                messages.Add(L["Check interval raised to the plan minimum ({1}) for {0} monitor(s).", result.AdjustedMonitors, Fmt.Interval(planEntity.MinIntervalSeconds)]);
            if (result.OverMonitorLimit)
                warning = L["Warning: the customer has {0} monitors, but the {1} plan allows at most {2}. The monitors were not deleted; the customer cannot add new monitors.", result.MonitorCount, planEntity.Name, planEntity.MaxMonitors];
        }

        await db.SaveChangesAsync();
        TempData["Success"] = string.Join(" ", messages);
        if (warning is not null) TempData["Error"] = warning;
        return RedirectToPage("Details", new { id });
    }
}
