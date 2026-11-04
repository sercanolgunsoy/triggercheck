using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Billing;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Pages.Account;

/// <summary>
/// BIZ-1 self-service sign-up: creates a customer on a free trial of the chosen plan (no trial for a free plan) and
/// its first CustomerAdmin, who must confirm the e-mail address before signing in. Every outcome - including an
/// address that already has an account and a filled honeypot - shows the same "check your inbox" page.
/// </summary>
[EnableRateLimiting(BillingSetup.RegisterPolicy)]
public class RegisterModel(AppDbContext db, UserManager<ApplicationUser> users, BillingMailer mailer, BillingService billing, TimeProvider time,
    IOptionsMonitor<BillingOptions> options, Text<SharedResource> L, ILogger<RegisterModel> logger) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public List<Plan> Plans { get; private set; } = [];
    public int TrialDays => options.CurrentValue.TrialDays;

    public class InputModel
    {
        [Required(ErrorMessage = "Company name is required."), StringLength(200, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "Company name")] public string CompanyName { get; set; } = "";

        [Required(ErrorMessage = "Name is required."), StringLength(100, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "Your name")] public string FullName { get; set; } = "";

        [Required(ErrorMessage = "E-mail is required."), EmailAddress(ErrorMessage = "Enter a valid e-mail address."), StringLength(256, ErrorMessage = "At most {1} characters.")]
        [Display(Name = "E-mail")] public string Email { get; set; } = "";

        [Required(ErrorMessage = "Password is required."), StringLength(100, MinimumLength = 8, ErrorMessage = "The password must be at least 8 characters long.")]
        [DataType(DataType.Password), Display(Name = "Password")] public string Password { get; set; } = "";

        [Display(Name = "Plan")] public int PlanId { get; set; }

        [Display(Name = "I accept the terms of service and the privacy policy")] public bool AcceptTerms { get; set; }

        /// <summary>Honeypot: hidden from people, filled in by naive bots.</summary>
        public string? Website { get; set; }
    }

    /// <summary>Plans offered for sign-up: active and self-service (not sales-led ones like Enterprise).</summary>
    private async Task LoadPlansAsync(CancellationToken ct)
    {
        var plans = await db.Plans.AsNoTracking().Where(p => p.IsActive && p.AllowsSelfSignup).ToListAsync(ct);
        Plans = plans.OrderBy(p => p.MonthlyPrice).ThenByDescending(p => p.MinIntervalSeconds).ToList();
    }

    public async Task<IActionResult> OnGetAsync(int? plan, CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated == true) return Redirect("/");
        await LoadPlansAsync(ct);
        Input.PlanId = Plans.FirstOrDefault(p => p.Id == plan)?.Id ?? Plans.FirstOrDefault(p => p.MonthlyPrice > 0)?.Id ?? Plans.FirstOrDefault()?.Id ?? 0;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadPlansAsync(ct);
        if (!string.IsNullOrEmpty(Input.Website))
        {
            logger.LogInformation("Sign-up rejected by the honeypot field.");
            return RedirectToPage("RegisterConfirmation");
        }
        var plan = Plans.FirstOrDefault(p => p.Id == Input.PlanId);
        if (plan is null) ModelState.AddModelError("Input.PlanId", L["Choose a plan."]);
        if (!Input.AcceptTerms) ModelState.AddModelError("Input.AcceptTerms", L["You must accept the terms to continue."]);

        // All validation (including the password policy) runs before the address is looked up, so the response never
        // depends on whether an account exists (no enumeration through validation errors).
        var email = Input.Email.Trim();
        var passwordErrors = new List<IdentityError>();
        foreach (var v in users.PasswordValidators)
        {
            var r = await v.ValidateAsync(users, new ApplicationUser { UserName = email, Email = email }, Input.Password);
            if (!r.Succeeded) passwordErrors.AddRange(r.Errors);
        }
        foreach (var e in passwordErrors) ModelState.AddModelError("Input.Password", e.Description);
        if (!ModelState.IsValid) return Page();

        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            if (!existing.EmailConfirmed && existing.CustomerId is int cid && !await db.Users.AnyAsync(u => u.CustomerId == cid && u.EmailConfirmed, ct))
            {
                // Never confirmed: whoever signed up first did not prove the address. The account takes over this
                // submission (password, names, plan); earlier confirmation links stop working (new security stamp).
                await ResetUnconfirmedAsync(existing, cid, plan!, ct);
                await SendConfirmationAsync(existing, ct);
            }
            else
            {
                // Same result page (and comparable work: one password hash); the owner of the address gets an e-mail.
                users.PasswordHasher.HashPassword(existing, Input.Password);
                await mailer.SendAlreadyRegisteredAsync(email, Link("/Account/Login"), ct);
            }
            return RedirectToPage("RegisterConfirmation");
        }

        var now = time.GetUtcNow().UtcDateTime;
        ApplicationUser user;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var customer = new Customer { Name = Input.CompanyName.Trim(), ContactEmail = email, CreatedAtUtc = now };
            await ApplyPlanAsync(customer, plan!, email, now, ct);
            db.Customers.Add(customer);
            await db.SaveChangesAsync(ct);

            user = new ApplicationUser
            {
                UserName = email, Email = email, EmailConfirmed = false, FullName = Input.FullName.Trim(), CustomerId = customer.Id, CreatedAtUtc = now
            };
            var created = await users.CreateAsync(user, Input.Password);
            if (!created.Succeeded)
            {
                // E.g. a concurrent sign-up with the same address: treat like a duplicate (nothing is kept).
                logger.LogWarning("Sign-up failed: {Errors}", string.Join("; ", created.Errors.Select(e => e.Code)));
                return RedirectToPage("RegisterConfirmation");
            }
            await users.AddToRoleAsync(user, Roles.CustomerAdmin);
            await tx.CommitAsync(ct);
            logger.LogInformation("New self-service customer on plan {Plan} ({Status}).", customer.PlanId, customer.BillingStatus);
        }
        await SendConfirmationAsync(user, ct);
        return RedirectToPage("RegisterConfirmation");
    }

    /// <summary>
    /// Plan and trial of a new (or reset) sign-up: a paid plan starts a trial when the address / domain is still eligible
    /// (otherwise the customer starts on the fallback plan); a free plan has no trial.
    /// </summary>
    private async Task ApplyPlanAsync(Customer customer, Plan plan, string email, DateTime now, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var key = TrialEligibility.Normalize(email);
        customer.TrialReminderSentAtUtc = null;
        if (plan.MonthlyPrice > 0 && await TrialEligibility.IsEligibleAsync(db, key, o, customer.Id == 0 ? null : customer.Id, ct))
        {
            customer.PlanId = plan.Id;
            customer.BillingStatus = BillingStatus.Trial;
            customer.TrialEndsAtUtc = now.AddDays(o.TrialDays);
            customer.TrialKey = key;
            return;
        }
        if (plan.MonthlyPrice > 0) logger.LogInformation("Sign-up without trial: the address or domain already had its free trials.");
        customer.PlanId = plan.MonthlyPrice > 0 ? ((await billing.FallbackPlanAsync(ct))?.Id ?? plan.Id) : plan.Id;
        customer.BillingStatus = BillingStatus.None;
        customer.TrialEndsAtUtc = null;
        customer.TrialKey = null;
    }

    private async Task ResetUnconfirmedAsync(ApplicationUser user, int customerId, Plan plan, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var customer = await db.Customers.FirstAsync(c => c.Id == customerId, ct);
        customer.Name = Input.CompanyName.Trim();
        customer.CreatedAtUtc = now;
        await ApplyPlanAsync(customer, plan, user.Email!, now, ct);
        await db.SaveChangesAsync(ct);

        user.FullName = Input.FullName.Trim();
        await users.UpdateAsync(user);
        await users.RemovePasswordAsync(user);
        await users.AddPasswordAsync(user, Input.Password);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await users.UpdateSecurityStampAsync(user);
        logger.LogInformation("Unconfirmed sign-up was submitted again; the account was reset to the new submission.");
    }

    private async Task SendConfirmationAsync(ApplicationUser user, CancellationToken ct)
    {
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = Link($"/Account/ConfirmEmail?userId={Uri.EscapeDataString(user.Id)}&code={code}");
        await mailer.SendConfirmationAsync(user.Email!, user.FullName, link, ct);
    }

    private string Link(string path) => BillingSetup.PublicUrl(Request, options.CurrentValue, path);
}
