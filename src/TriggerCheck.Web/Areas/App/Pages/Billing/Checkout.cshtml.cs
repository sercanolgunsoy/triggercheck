using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Billing;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Billing;

/// <summary>
/// Starts a subscription: collects the billing details iyzico requires (never card data), initializes the hosted
/// checkout and links to the provider's payment page. CustomerAdmin only.
/// </summary>
[Authorize(Policy = Policies.CustomerAdmin)]
public class CheckoutModel(AppDbContext db, ICurrentCustomer current, BillingService billing, UserManager<ApplicationUser> users,
    Text<AppResource> L) : PageModel
{
    [BindProperty] public ContactInput Input { get; set; } = new();
    public Plan? Plan { get; private set; }
    /// <summary>Set once the checkout was initialized: the provider's hosted payment page.</summary>
    public string? PaymentPageUrl { get; private set; }

    public class ContactInput
    {
        [Required(ErrorMessage = "First name is required."), StringLength(100, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "First name")] public string Name { get; set; } = "";
        [Required(ErrorMessage = "Last name is required."), StringLength(100, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "Last name")] public string Surname { get; set; } = "";
        [Required(ErrorMessage = "Phone number is required."), RegularExpression(@"^\+?[0-9 ]{7,20}$", ErrorMessage = "Enter a valid phone number.")]
        [Display(Name = "Mobile phone")] public string Phone { get; set; } = "";
        [RegularExpression(@"^[0-9]{11}$", ErrorMessage = "The identity number has 11 digits.")]
        [Display(Name = "Turkish identity or tax number (optional)")] public string? IdentityNumber { get; set; }
        [Required(ErrorMessage = "Address is required."), StringLength(300, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "Billing address")] public string Address { get; set; } = "";
        [Required(ErrorMessage = "City is required."), StringLength(100, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "City")] public string City { get; set; } = "";
        [Required(ErrorMessage = "Country is required."), StringLength(100, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "Country")] public string Country { get; set; } = "Turkey";
        [StringLength(20, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "Postal code")] public string? ZipCode { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int? planId, int? subscriptionId, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        if (subscriptionId is int sid)
        {
            // Tenant-filtered: only the customer's own pending checkout can be shown.
            var sub = await db.Subscriptions.AsNoTracking().Include(s => s.Plan)
                .FirstOrDefaultAsync(s => s.Id == sid && s.CustomerId == cid && s.Status == SubscriptionStatus.Pending, ct);
            if (sub is null) return RedirectToPage("Index");
            Plan = sub.Plan;
            PaymentPageUrl = sub.CheckoutUrl;
            return Page();
        }

        Plan = (await billing.PurchasablePlansAsync(ct)).FirstOrDefault(p => p.Id == planId);
        if (Plan is null) return RedirectToPage("Index");
        var user = await users.GetUserAsync(User);
        var parts = (user?.FullName ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        Input.Name = parts.ElementAtOrDefault(0) ?? "";
        Input.Surname = parts.ElementAtOrDefault(1) ?? "";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int planId, CancellationToken ct)
    {
        Plan = (await billing.PurchasablePlansAsync(ct)).FirstOrDefault(p => p.Id == planId);
        if (Plan is null) return RedirectToPage("Index");
        if (!ModelState.IsValid) return Page();

        var email = (await users.GetUserAsync(User))?.Email ?? "";
        var i = Input;
        // iyzico requires an identity number; customers outside Turkey may use the documented placeholder. TODO-verify with iyzico.
        var contact = new BillingContact(i.Name.Trim(), i.Surname.Trim(), email, i.Phone.Replace(" ", ""),
            string.IsNullOrWhiteSpace(i.IdentityNumber) ? "11111111111" : i.IdentityNumber, i.Address.Trim(), i.City.Trim(), i.Country.Trim(), i.ZipCode?.Trim());
        var callback = BillingSetup.PublicUrl(Request, BillingSetup.CallbackPath);
        var locale = Fmt.IsTurkish ? "tr" : "en";

        var r = await billing.StartCheckoutAsync(current.RequiredCustomerId, planId, contact, callback, locale, ct);
        if (!r.Succeeded)
        {
            TempData["Error"] = r.Error switch
            {
                BillingError.TooManyMonitors => L["The new plan allows fewer monitors than you have. Delete {0} monitor(s) first, then change the plan.", r.MonitorsToDelete].Value,
                BillingError.ProviderUnavailable => L["Online payment is not available at the moment. Please contact us."].Value,
                BillingError.ProviderFailed => L["The payment provider did not accept the request. Please try again later."].Value,
                _ => L["This action is not possible in the current subscription state."].Value
            };
            return RedirectToPage("Index");
        }
        // The provider page is opened by a link: a redirect after this POST would be blocked by the CSP form-action rule.
        return RedirectToPage("Checkout", new { subscriptionId = r.SubscriptionId });
    }
}
