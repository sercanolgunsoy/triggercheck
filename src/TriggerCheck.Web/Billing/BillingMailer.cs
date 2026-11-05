using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Billing;

/// <summary>Account and billing e-mails. Texts follow the current UI culture (English for background jobs).</summary>
public sealed class BillingMailer(AppDbContext db, IAppEmailSender email, IOptionsMonitor<BillingOptions> options, IConfiguration config,
    Text<SharedResource> L)
{
    /// <param name="noTrialPlan">Set when a paid plan was chosen but no free trial was granted (address or domain already had one).</param>
    public Task SendConfirmationAsync(string to, string name, string link, CancellationToken ct, string? noTrialPlan = null) =>
        email.SendAsync(to, L["Confirm your TriggerCheck account"],
            Lines([L["Hello {0},", name], L["Please confirm your e-mail address to activate your TriggerCheck account:"] + "\n" + link,
                .. noTrialPlan is null ? Array.Empty<string>()
                    : [L["No free trial was started: this address or its company domain already had one. Your workspace starts on the {0} plan; you can subscribe to a paid plan on the Billing page.", noTrialPlan].Value],
                L["If you did not sign up, you can ignore this e-mail."]]), ct);

    /// <summary>Sent instead of a second account when someone signs up with an address that already has one (no enumeration on the page).</summary>
    public Task SendAlreadyRegisteredAsync(string to, string loginLink, CancellationToken ct) =>
        email.SendAsync(to, L["Your TriggerCheck account"],
            Lines(L["Someone (hopefully you) tried to create a TriggerCheck account with this e-mail address, but an account already exists."],
                L["Sign in here: {0}", loginLink], L["If this was not you, you can ignore this e-mail."]), ct);

    public Task SendPasswordResetAsync(string to, string link, CancellationToken ct) =>
        email.SendAsync(to, L["Reset your TriggerCheck password"],
            Lines(L["Open this link to choose a new password (valid for one day):"] + "\n" + link, L["If you did not ask for this, you can ignore this e-mail."]), ct);

    public Task TrialReminderAsync(Customer c, Plan fallback, CancellationToken ct) =>
        ToAdminsAsync(c.Id, L["Your TriggerCheck trial ends soon"],
            L["Your free trial of the {0} plan ends on {1} (UTC). Subscribe on the Billing page to keep your plan; otherwise your workspace moves to the {2} plan.", c.Plan.Name,
                c.TrialEndsAtUtc?.ToString("yyyy-MM-dd HH:mm") ?? "", fallback.Name], ct);

    public Task DowngradedAsync(int customerId, string reason, Plan fallback, CancellationToken ct) =>
        ToAdminsAsync(customerId, L["Your TriggerCheck plan changed to {0}", fallback.Name],
            L["{0} Your workspace is now on the {1} plan: monitors faster than {2} were slowed down and you can have at most {3} monitors. You can subscribe again on the Billing page.",
                reason, fallback.Name, PlanRules.FormatInterval(fallback.MinIntervalSeconds), fallback.MaxMonitors], ct);

    public string TrialEndedReason => L["Your free trial has ended."];
    public string GraceEndedReason => L["Your subscription payment could not be collected."];
    public string PeriodEndedReason => L["Your cancelled subscription has ended."];

    public Task PaymentFailedAsync(int customerId, CancellationToken ct) =>
        ToAdminsAsync(customerId, L["TriggerCheck payment failed"],
            L["We could not collect your subscription payment. Please update your card at iyzico within {0} days; after that your workspace moves to the {1} plan.",
                options.CurrentValue.PastDueGraceDays, options.CurrentValue.FallbackPlanName], ct);

    /// <summary>
    /// A paid subscription we refused (second subscription or unexpected plan) was cancelled at the provider: the
    /// operators (Site:ContactEmail) must refund it, and the customer is told that the money comes back.
    /// </summary>
    public async Task UnwantedPaymentAsync(int customerId, string subscriptionReference, string reason, bool cancelled, CancellationToken ct)
    {
        var name = await db.Customers.Where(c => c.Id == customerId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? customerId.ToString();
        await email.SendAsync(Marketing.ContactEmail(config), $"[TriggerCheck] Refund needed: subscription {subscriptionReference}",
            $"Customer \"{name}\" (id {customerId}) paid for a subscription that was not activated ({reason}).\n" +
            $"iyzico subscription reference: {subscriptionReference}\n" +
            $"Cancelled at iyzico: {(cancelled ? "yes" : "NO - cancel it manually")}\n" +
            "Refund the payment in the iyzico merchant panel.", ct);
        await ToAdminsAsync(customerId, L["Your extra TriggerCheck payment will be refunded"],
            L["We received a payment for a subscription that we could not activate (your workspace already has a subscription, or the plan did not match). It was cancelled and the amount will be refunded to your card. Your current plan is not affected."], ct);
    }

    /// <summary>After a booked downgrade the workspace has more monitors than the plan allows (nothing is deleted).</summary>
    public Task OverPlanLimitAsync(int customerId, Plan plan, IReadOnlyList<string> overLimit, CancellationToken ct) =>
        ToAdminsAsync(customerId, L["Your TriggerCheck plan changed to {0}", plan.Name],
            Lines(L["Your workspace is now on the {0} plan, which allows {1} monitors. These monitors are over the limit; they keep running, but you cannot add new monitors until you delete some or upgrade:", plan.Name, plan.MaxMonitors]
                + "\n- " + string.Join("\n- ", overLimit)), ct);

    private static string Lines(params string[] paragraphs) => string.Join("\n\n", paragraphs);

    /// <summary>Active CustomerAdmin users of the customer, or its contact address when there are none.</summary>
    private async Task ToAdminsAsync(int customerId, string subject, string body, CancellationToken ct)
    {
        var adminRoleId = await db.Roles.Where(r => r.Name == Roles.CustomerAdmin).Select(r => r.Id).FirstOrDefaultAsync(ct);
        var to = await db.Users.Where(u => u.CustomerId == customerId && u.IsActive && u.Email != null
                && db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == adminRoleId))
            .Select(u => u.Email!).ToListAsync(ct);
        if (to.Count == 0 && await db.Customers.Where(c => c.Id == customerId).Select(c => c.ContactEmail).FirstOrDefaultAsync(ct) is { Length: > 0 } contact)
            to.Add(contact);
        foreach (var address in to.Distinct(StringComparer.OrdinalIgnoreCase))
            await email.SendAsync(address, subject, body, ct);
    }
}
