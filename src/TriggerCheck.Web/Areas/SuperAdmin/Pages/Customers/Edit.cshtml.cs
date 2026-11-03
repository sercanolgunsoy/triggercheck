using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.SuperAdmin.Services;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Customers;

[Authorize(Policy = Policies.SuperAdmin)]
public class EditModel(AppDbContext db) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public List<Plan> Plans { get; private set; } = [];
    public Customer Customer { get; private set; } = null!;
    public int MonitorCount { get; private set; }
    public Dictionary<int, int> FastMonitorsPerPlan { get; private set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "Müşteri adı zorunludur"), StringLength(200, ErrorMessage = "En fazla 200 karakter")]
        [Display(Name = "Müşteri adı")] public string Name { get; set; } = "";

        [Required(ErrorMessage = "İletişim e-postası zorunludur"), EmailAddress(ErrorMessage = "Geçerli bir e-posta girin"), StringLength(256)]
        [Display(Name = "İletişim e-postası")] public string ContactEmail { get; set; } = "";

        [Required(ErrorMessage = "Plan seçin"), Display(Name = "Plan")] public int? PlanId { get; set; }
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
            ModelState.AddModelError("Input.PlanId", "Geçerli ve aktif bir plan seçin.");
        if (!ModelState.IsValid) return Page();

        Customer.Name = Input.Name.Trim();
        Customer.ContactEmail = Input.ContactEmail.Trim();

        var messages = new List<string> { "Müşteri bilgileri kaydedildi." };
        string? warning = null;
        if (newPlan!.Id != Customer.PlanId)
        {
            var oldPlanName = Customer.Plan.Name;
            var planEntity = await db.Plans.FirstAsync(p => p.Id == newPlan.Id);
            var result = await SuperAdminOps.ChangePlanAsync(db, Customer, planEntity);
            messages.Add($"Plan {oldPlanName} → {planEntity.Name} olarak değiştirildi.");
            if (result.AdjustedMonitors > 0)
                messages.Add($"{result.AdjustedMonitors} monitörün kontrol aralığı plan minimumuna ({PlanRules.FormatInterval(planEntity.MinIntervalSeconds)}) yükseltildi.");
            if (result.OverMonitorLimit)
                warning = $"Uyarı: müşterinin {result.MonitorCount} monitörü var, {planEntity.Name} planı en fazla {planEntity.MaxMonitors} monitöre izin veriyor. Monitörler silinmedi; müşteri yeni monitör ekleyemez.";
        }

        await db.SaveChangesAsync();
        TempData["Success"] = string.Join(" ", messages);
        if (warning is not null) TempData["Error"] = warning;
        return RedirectToPage("Details", new { id });
    }
}
