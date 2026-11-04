using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.SuperAdmin.Services;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Plans;

/// <summary>Form model for creating / editing a plan (no direct entity binding).</summary>
public class PlanInput
{
    [Required(ErrorMessage = "Plan name is required."), StringLength(100, ErrorMessage = "At most {1} characters.")]
    [Display(Name = "Plan name")] public string Name { get; set; } = "";

    [Required(ErrorMessage = "Minimum interval is required.")]
    [Display(Name = "Minimum check interval")] public int? MinIntervalSeconds { get; set; }

    [Required(ErrorMessage = "Max monitors is required."), Range(1, 1_000_000, ErrorMessage = "Max monitors must be at least 1.")]
    [Display(Name = "Max monitors")] public int? MaxMonitors { get; set; }

    [Required(ErrorMessage = "Retention is required."), Range(1, 3650, ErrorMessage = "Retention must be between {1} and {2} days.")]
    [Display(Name = "Data retention (days)")] public int? RetentionDays { get; set; }

    [Required(ErrorMessage = "Monthly price is required."), Range(typeof(decimal), "0", "1000000", ErrorMessage = "Monthly price must be 0 or greater.",
        ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    [ModelBinder(typeof(FlexibleDecimalModelBinder))]
    [Display(Name = "Monthly price ($)")] public decimal? MonthlyPrice { get; set; }

    [Display(Name = "Active (offered to new customers)")] public bool IsActive { get; set; } = true;

    [Display(Name = "SMS and voice call alerts (Twilio)")] public bool AllowsSmsVoice { get; set; }

    [Display(Name = "Self-service sign-up and billing")] public bool AllowsSelfSignup { get; set; }

    [StringLength(100, ErrorMessage = "At most {1} characters."), RegularExpression(@"^[A-Za-z0-9\-_]*$", ErrorMessage = "Only letters, digits, - and _ are allowed.")]
    [Display(Name = "iyzico product reference code")] public string? IyzicoProductReferenceCode { get; set; }

    [StringLength(100, ErrorMessage = "At most {1} characters."), RegularExpression(@"^[A-Za-z0-9\-_]*$", ErrorMessage = "Only letters, digits, - and _ are allowed.")]
    [Display(Name = "iyzico pricing plan reference code (monthly)")] public string? IyzicoPricingPlanReferenceCode { get; set; }

    // ---- status pages (FEAT-4) ----
    [Range(0, 100_000, ErrorMessage = "The status page limit must be 0 or greater.")]
    [Display(Name = "Status pages (empty = unlimited)")] public int? MaxStatusPages { get; set; } = 1;

    [Display(Name = "Status pages on custom domains")] public bool AllowsStatusPageCustomDomain { get; set; }

    // ---- AI (AI-10) ----
    [Required(ErrorMessage = "AI call limit is required."), Range(0, 10_000_000, ErrorMessage = "The AI call limit must be 0 or greater.")]
    [Display(Name = "AI calls per month")] public int? AiMonthlyCallLimit { get; set; } = 0;

    [Required(ErrorMessage = "AI token limit is required."), Range(0L, 100_000_000_000L, ErrorMessage = "The AI token limit must be 0 or greater.")]
    [Display(Name = "AI tokens per month (0 = no token limit)")] public long? AiMonthlyTokenLimit { get; set; } = 0;

    /// <summary>Selected <see cref="TriggerCheck.Domain.AiFeatures"/> names.</summary>
    public List<string> AiFeatures { get; set; } = [];

    public static PlanInput From(Plan p) => new()
    {
        Name = p.Name, MinIntervalSeconds = p.MinIntervalSeconds, MaxMonitors = p.MaxMonitors,
        RetentionDays = p.RetentionDays, MonthlyPrice = p.MonthlyPrice, IsActive = p.IsActive,
        AllowsSmsVoice = p.AllowsSmsVoice, AllowsSelfSignup = p.AllowsSelfSignup,
        IyzicoProductReferenceCode = p.IyzicoProductReferenceCode, IyzicoPricingPlanReferenceCode = p.IyzicoPricingPlanReferenceCode,
        AiMonthlyCallLimit = p.AiMonthlyCallLimit, AiMonthlyTokenLimit = p.AiMonthlyTokenLimit,
        MaxStatusPages = p.MaxStatusPages, AllowsStatusPageCustomDomain = p.AllowsStatusPageCustomDomain,
        AiFeatures = Domain.AiFeatures.Parse(p.AiFeatures).ToList()
    };

    public void ApplyTo(Plan p)
    {
        p.Name = Name.Trim();
        p.MinIntervalSeconds = MinIntervalSeconds!.Value;
        p.MaxMonitors = MaxMonitors!.Value;
        p.RetentionDays = RetentionDays!.Value;
        p.MonthlyPrice = MonthlyPrice!.Value;
        p.IsActive = IsActive;
        p.AllowsSmsVoice = AllowsSmsVoice;
        p.AllowsSelfSignup = AllowsSelfSignup;
        p.IyzicoProductReferenceCode = string.IsNullOrWhiteSpace(IyzicoProductReferenceCode) ? null : IyzicoProductReferenceCode.Trim();
        p.IyzicoPricingPlanReferenceCode = string.IsNullOrWhiteSpace(IyzicoPricingPlanReferenceCode) ? null : IyzicoPricingPlanReferenceCode.Trim();
        p.AiMonthlyCallLimit = AiMonthlyCallLimit!.Value;
        p.AiMonthlyTokenLimit = AiMonthlyTokenLimit!.Value;
        p.AiFeatures = Domain.AiFeatures.Format(AiFeatures);
        p.MaxStatusPages = MaxStatusPages;
        p.AllowsStatusPageCustomDomain = AllowsStatusPageCustomDomain;
    }

    /// <summary>Rules that DataAnnotations can't express: supported interval and unique name (case-insensitive).</summary>
    public async Task ValidateAsync(AppDbContext db, ModelStateDictionary ms, Text<AdminResource> L, int? currentId, string prefix = "Input")
    {
        if (MinIntervalSeconds is int i && !PlanRules.SupportedIntervals.Contains(i))
            ms.AddModelError($"{prefix}.{nameof(MinIntervalSeconds)}",
                L["Unsupported interval. Valid values: {0}.", string.Join(", ", PlanRules.SupportedIntervals.Select(Fmt.Interval))]);

        if (!string.IsNullOrWhiteSpace(Name))
        {
            var name = Name.Trim().ToUpperInvariant();
            var names = await db.Plans.Where(p => p.Id != currentId).Select(p => p.Name).ToListAsync();
            if (names.Any(n => n.ToUpperInvariant() == name))
                ms.AddModelError($"{prefix}.{nameof(Name)}", L["A plan with this name already exists."]);
        }
    }
}
