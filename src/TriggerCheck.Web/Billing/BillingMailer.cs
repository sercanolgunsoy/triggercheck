using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Billing;

/// <summary>Account and billing e-mails. Texts follow the current UI culture (English for background jobs).</summary>
public sealed class BillingMailer(AppDbContext db, IAppEmailSender email, IOptionsMonitor<BillingOptions> options, Text<SharedResource> L)
{
    public Task SendConfirmationAsync(string to, string name, string link, CancellationToken ct) =>
        email.SendAsync(to, L["Confirm your TriggerCheck account"],
            Lines(L["Hello {0},", name], L["Please confirm your e-mail address to activate your TriggerCheck account:"] + "\n" + link,
                L["If you did not sign up, you can ignore this e-mail."]), ct);

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
