using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.SuperAdmin.Services;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Plans;

/// <summary>Form model for creating / editing a plan (no direct entity binding).</summary>
public class PlanInput
{
    [Required(ErrorMessage = "Plan adı zorunludur"), StringLength(100, ErrorMessage = "En fazla 100 karakter")]
    [Display(Name = "Plan adı")] public string Name { get; set; } = "";

    [Required(ErrorMessage = "Minimum aralık zorunludur")]
    [Display(Name = "Minimum kontrol aralığı")] public int? MinIntervalSeconds { get; set; }

    [Required(ErrorMessage = "Maks. monitör zorunludur"), Range(1, 1_000_000, ErrorMessage = "Maks. monitör en az 1 olmalıdır")]
    [Display(Name = "Maks. monitör")] public int? MaxMonitors { get; set; }

    [Required(ErrorMessage = "Saklama süresi zorunludur"), Range(1, 3650, ErrorMessage = "Saklama süresi 1 ile 3650 gün arasında olmalıdır")]
    [Display(Name = "Veri saklama (gün)")] public int? RetentionDays { get; set; }

    [Required(ErrorMessage = "Aylık ücret zorunludur"), Range(typeof(decimal), "0", "1000000", ErrorMessage = "Aylık ücret 0 veya daha büyük olmalıdır",
        ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    [ModelBinder(typeof(FlexibleDecimalModelBinder))]
    [Display(Name = "Aylık ücret ($)")] public decimal? MonthlyPrice { get; set; }

    [Display(Name = "Aktif (yeni müşterilere sunulur)")] public bool IsActive { get; set; } = true;

    public static PlanInput From(Plan p) => new()
    {
        Name = p.Name, MinIntervalSeconds = p.MinIntervalSeconds, MaxMonitors = p.MaxMonitors,
        RetentionDays = p.RetentionDays, MonthlyPrice = p.MonthlyPrice, IsActive = p.IsActive
    };

    public void ApplyTo(Plan p)
    {
        p.Name = Name.Trim();
        p.MinIntervalSeconds = MinIntervalSeconds!.Value;
        p.MaxMonitors = MaxMonitors!.Value;
        p.RetentionDays = RetentionDays!.Value;
        p.MonthlyPrice = MonthlyPrice!.Value;
        p.IsActive = IsActive;
    }

    /// <summary>Rules that DataAnnotations can't express: supported interval and unique name (case-insensitive).</summary>
    public async Task ValidateAsync(AppDbContext db, ModelStateDictionary ms, int? currentId, string prefix = "Input")
    {
        if (MinIntervalSeconds is int i && !PlanRules.SupportedIntervals.Contains(i))
            ms.AddModelError($"{prefix}.{nameof(MinIntervalSeconds)}",
                $"Desteklenmeyen aralık. Geçerli değerler: {string.Join(", ", PlanRules.SupportedIntervals.Select(s => s + " sn"))}.");

        if (!string.IsNullOrWhiteSpace(Name))
        {
            var name = Name.Trim().ToUpperInvariant();
            var names = await db.Plans.Where(p => p.Id != currentId).Select(p => p.Name).ToListAsync();
            if (names.Any(n => n.ToUpperInvariant() == name))
                ms.AddModelError($"{prefix}.{nameof(Name)}", "Bu isimde bir plan zaten var.");
        }
    }
}
