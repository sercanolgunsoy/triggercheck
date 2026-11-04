using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.SuperAdmin.Services;

namespace TriggerCheck.Web.Billing;

public enum BillingError { None, PlanNotAvailable, TooManyMonitors, ProviderUnavailable, ProviderFailed, InvalidState }

/// <param name="MonitorsToDelete">For <see cref="BillingError.TooManyMonitors"/>: how many monitors must be deleted first.</param>
public sealed record BillingOutcome(BillingError Error, int MonitorsToDelete = 0, int? SubscriptionId = null, int AdjustedMonitors = 0)
{
    public bool Succeeded => Error == BillingError.None;
}

public enum CheckoutCompletion { Activated, AlreadyProcessed, Failed, NotFound }

public enum WebhookOutcome { Processed, Duplicate, Ignored }

/// <summary>
/// Subscription lifecycle (BIZ-2): checkout, server-side verification, plan changes, cancellation, webhook events and
/// the downgrade to the fallback plan. Provider calls go through <see cref="IPaymentProvider"/>; every plan change
/// clamps monitor intervals in the same transaction as the customer update (SuperAdminOps.ChangePlanAsync).
/// </summary>
public sealed class BillingService(AppDbContext db, IPaymentProvider provider, TimeProvider time, IOptionsMonitor<BillingOptions> options,
    BillingMailer mailer, ILogger<BillingService> logger)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>The subscription that currently defines the customer's billing (active, past due or cancelled-at-period-end).</summary>
    public Task<Subscription?> CurrentSubscriptionAsync(int customerId, CancellationToken ct = default) =>
        db.Subscriptions.Include(s => s.Plan)
            .Where(s => s.CustomerId == customerId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.PastDue || s.Status == SubscriptionStatus.Cancelled))
            .OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct);

    /// <summary>Plans a customer can buy: active, self-service, paid and linked to a provider pricing plan.</summary>
    public async Task<List<Plan>> PurchasablePlansAsync(CancellationToken ct = default)
    {
        var plans = await db.Plans.AsNoTracking()
            .Where(p => p.IsActive && p.AllowsSelfSignup && p.IyzicoPricingPlanReferenceCode != null && p.IyzicoPricingPlanReferenceCode != "")
            .ToListAsync(ct);
        return plans.Where(p => p.MonthlyPrice > 0).OrderBy(p => p.MonthlyPrice).ToList();
    }

    public async Task<Plan?> FallbackPlanAsync(CancellationToken ct = default)
    {
        var name = options.CurrentValue.FallbackPlanName;
        var plans = await db.Plans.ToListAsync(ct);
        return plans.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? plans.Where(p => p.IsActive).OrderBy(p => p.MonthlyPrice).ThenByDescending(p => p.MinIntervalSeconds).FirstOrDefault();
    }

    /// <summary>Monitors that must be deleted before the customer fits <paramref name="plan"/> (0 = fits).</summary>
    public async Task<int> MonitorsOverLimitAsync(int customerId, Plan plan, CancellationToken ct = default) =>
        Math.Max(0, await db.Monitors.CountAsync(m => m.CustomerId == customerId, ct) - plan.MaxMonitors);

    /// <summary>Starts a hosted checkout for <paramref name="planId"/>; the customer completes it on the provider's page.</summary>
    public async Task<BillingOutcome> StartCheckoutAsync(int customerId, int planId, BillingContact contact, string callbackUrl, string locale, CancellationToken ct)
    {
        if (!provider.IsConfigured) return new(BillingError.ProviderUnavailable);
        var plan = (await PurchasablePlansAsync(ct)).FirstOrDefault(p => p.Id == planId);
        if (plan is null) return new(BillingError.PlanNotAvailable);
        var current = await CurrentSubscriptionAsync(customerId, ct);
        // An existing subscription is changed in place (ChangePlanAsync); only a cancelled one can be replaced by a new checkout.
        if (current is not null && current.Status != SubscriptionStatus.Cancelled) return new(BillingError.InvalidState);
        if (await MonitorsOverLimitAsync(customerId, plan, ct) is > 0 and var over) return new(BillingError.TooManyMonitors, over);

        var session = await provider.StartCheckoutAsync(new CheckoutRequest(Guid.NewGuid().ToString("N"), plan.IyzicoPricingPlanReferenceCode!,
            callbackUrl, locale, contact), ct);
        if (session is null) return new(BillingError.ProviderFailed);

        var sub = new Subscription
        {
            CustomerId = customerId, PlanId = plan.Id, Status = SubscriptionStatus.Pending, Provider = provider.Name,
            CheckoutToken = session.Token, CheckoutUrl = session.PaymentPageUrl, CreatedAtUtc = Now
        };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync(ct);
        return new(BillingError.None, SubscriptionId: sub.Id);
    }

    /// <summary>
    /// Return/callback of the hosted checkout. The token only identifies the pending checkout: the result is always
    /// retrieved from the provider before anything is activated. Idempotent (a repeated callback is a no-op).
    /// </summary>
    public async Task<(CheckoutCompletion Result, int? CustomerId)> CompleteCheckoutAsync(string token, CancellationToken ct)
    {
        var sub = await db.Subscriptions.Include(s => s.Plan).Include(s => s.Customer).ThenInclude(c => c.Plan)
            .FirstOrDefaultAsync(s => s.CheckoutToken == token, ct);
        if (sub is null) return (CheckoutCompletion.NotFound, null);
        if (sub.Status != SubscriptionStatus.Pending)
            return (sub.Status == SubscriptionStatus.Failed ? CheckoutCompletion.Failed : CheckoutCompletion.AlreadyProcessed, sub.CustomerId);

        var result = await provider.GetCheckoutResultAsync(token, ct);
        var now = Now;
        var eventId = $"checkout:{token}";
        if (!result.Succeeded || result.PricingPlanReferenceCode is { } code && code != sub.Plan.IyzicoPricingPlanReferenceCode)
        {
            logger.LogWarning("Checkout of customer {CustomerId} was not completed (state {State}).", sub.CustomerId, result.State);
            sub.Status = SubscriptionStatus.Failed;
            sub.EndedAtUtc = now;
            AddEvent(sub, eventId, "checkout.failure", false, null, now);
            await SaveIgnoringDuplicateEventAsync(ct);
            return (CheckoutCompletion.Failed, sub.CustomerId);
        }

        var customer = sub.Customer;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // A cancelled-at-period-end subscription is replaced by the new one.
        var previous = await db.Subscriptions.Where(s => s.CustomerId == customer.Id && s.Id != sub.Id
            && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.PastDue || s.Status == SubscriptionStatus.Cancelled)).ToListAsync(ct);
        foreach (var p in previous) { p.Status = SubscriptionStatus.Expired; p.EndedAtUtc = now; }

        sub.Status = SubscriptionStatus.Active;
        sub.ProviderReference = result.SubscriptionReference;
        sub.ProviderCustomerReference = result.CustomerReference;
        sub.ActivatedAtUtc = now;
        sub.CurrentPeriodEndUtc = now.AddMonths(1);
        await SuperAdminOps.ChangePlanAsync(db, customer, sub.Plan);
        customer.BillingStatus = BillingStatus.Active;
        AddEvent(sub, eventId, "checkout.success", true, null, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Subscription {SubscriptionId} of customer {CustomerId} activated on plan {Plan}.", sub.Id, customer.Id, sub.Plan.Name);
        return (CheckoutCompletion.Activated, customer.Id);
    }

    /// <summary>Upgrade or downgrade of an active subscription (immediately, prorated by the provider).</summary>
    public async Task<BillingOutcome> ChangePlanAsync(int customerId, int planId, CancellationToken ct)
    {
        var sub = await CurrentSubscriptionAsync(customerId, ct);
        if (sub is null || sub.Status != SubscriptionStatus.Active || sub.ProviderReference is null) return new(BillingError.InvalidState);
        var plan = (await PurchasablePlansAsync(ct)).FirstOrDefault(p => p.Id == planId);
        if (plan is null || plan.Id == sub.PlanId) return new(BillingError.PlanNotAvailable);
        if (await MonitorsOverLimitAsync(customerId, plan, ct) is > 0 and var over) return new(BillingError.TooManyMonitors, over);

        var r = await provider.ChangePlanAsync(sub.ProviderReference, plan.IyzicoPricingPlanReferenceCode!, ct);
        if (!r.Succeeded) return new(BillingError.ProviderFailed);

        var customer = await db.Customers.Include(c => c.Plan).FirstAsync(c => c.Id == customerId, ct);
        var tracked = await db.Plans.FirstAsync(p => p.Id == plan.Id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var change = await SuperAdminOps.ChangePlanAsync(db, customer, tracked);
        sub.PlanId = tracked.Id;
        sub.Plan = tracked;
        if (!string.IsNullOrEmpty(r.NewSubscriptionReference)) sub.ProviderReference = r.NewSubscriptionReference;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(BillingError.None, AdjustedMonitors: change.AdjustedMonitors);
    }

    /// <summary>Stops renewals at the provider; the plan stays until the paid period ends (then the fallback plan).</summary>
    public async Task<BillingOutcome> CancelAsync(int customerId, CancellationToken ct)
    {
        var sub = await CurrentSubscriptionAsync(customerId, ct);
        if (sub is null || sub.Status == SubscriptionStatus.Cancelled || sub.ProviderReference is null) return new(BillingError.InvalidState);
        var r = await provider.CancelAsync(sub.ProviderReference, ct);
        if (!r.Succeeded) return new(BillingError.ProviderFailed);

        sub.Status = SubscriptionStatus.Cancelled;
        sub.CancelAtPeriodEnd = true;
        sub.CurrentPeriodEndUtc ??= Now;
        await db.Customers.Where(c => c.Id == customerId).ExecuteUpdateAsync(s => s.SetProperty(c => c.BillingStatus, BillingStatus.Cancelled), ct);
        await db.SaveChangesAsync(ct);
        return new(BillingError.None);
    }

    public async Task<bool> CancelAtProviderAsync(string subscriptionReference, CancellationToken ct) =>
        (await provider.CancelAsync(subscriptionReference, ct)).Succeeded;

    /// <summary>Applies a signature-verified webhook. Each provider event is processed once (unique event id).</summary>
    public async Task<WebhookOutcome> ApplyWebhookAsync(WebhookEvent e, CancellationToken ct)
    {
        if (await db.PaymentEvents.AnyAsync(x => x.ProviderEventId == e.EventId, ct)) return WebhookOutcome.Duplicate;
        var sub = await db.Subscriptions.Include(s => s.Plan).Include(s => s.Customer)
            .Where(s => s.ProviderReference == e.SubscriptionReference).OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        if (sub is null)
        {
            logger.LogWarning("Webhook {EventType} for unknown subscription {Reference} ignored.", e.EventType, e.SubscriptionReference);
            return WebhookOutcome.Ignored;
        }

        var notifyFailure = false;
        switch (e.Kind)
        {
            case WebhookEventKind.PaymentSucceeded when sub.Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue:
                sub.Status = SubscriptionStatus.Active;
                sub.PastDueSinceUtc = null;
                var from = sub.CurrentPeriodEndUtc is { } end && end > e.OccurredAtUtc ? end : e.OccurredAtUtc;
                // The first order of a new subscription belongs to the period started at checkout.
                if (sub.ActivatedAtUtc is not { } activated || e.OccurredAtUtc - activated > TimeSpan.FromDays(1)) sub.CurrentPeriodEndUtc = from.AddMonths(1);
                sub.Customer.BillingStatus = BillingStatus.Active;
                break;
            case WebhookEventKind.PaymentFailed when sub.Status is SubscriptionStatus.Active:
                sub.Status = SubscriptionStatus.PastDue;
                sub.PastDueSinceUtc = e.OccurredAtUtc;
                sub.Customer.BillingStatus = BillingStatus.PastDue;
                notifyFailure = true;
                break;
        }
        AddEvent(sub, e.EventId, e.EventType, e.Kind == WebhookEventKind.PaymentSucceeded, e.OrderReference, e.OccurredAtUtc);
        if (!await SaveIgnoringDuplicateEventAsync(ct)) return WebhookOutcome.Duplicate;
        if (notifyFailure) await mailer.PaymentFailedAsync(sub.CustomerId, ct);
        return WebhookOutcome.Processed;
    }

    /// <summary>
    /// Moves the customer to the fallback plan (intervals clamped in the same transaction), ends its subscription and
    /// e-mails the admins. Returns false when no fallback plan exists.
    /// </summary>
    public async Task<bool> DowngradeToFallbackAsync(Customer customer, Subscription? sub, string reason, CancellationToken ct)
    {
        var fallback = await FallbackPlanAsync(ct);
        if (fallback is null) { logger.LogError("No fallback plan ({Plan}) exists; customer {CustomerId} was not downgraded.", options.CurrentValue.FallbackPlanName, customer.Id); return false; }

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await SuperAdminOps.ChangePlanAsync(db, customer, fallback);
            customer.BillingStatus = BillingStatus.None;
            if (sub is not null) { sub.Status = SubscriptionStatus.Expired; sub.EndedAtUtc = Now; }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        logger.LogInformation("Customer {CustomerId} moved to the fallback plan {Plan}.", customer.Id, fallback.Name);
        await mailer.DowngradedAsync(customer.Id, reason, fallback, ct);
        return true;
    }

    private void AddEvent(Subscription sub, string eventId, string type, bool succeeded, string? orderRef, DateTime occurred) =>
        db.PaymentEvents.Add(new PaymentEvent
        {
            CustomerId = sub.CustomerId, SubscriptionId = sub.Id, ProviderEventId = eventId, EventType = type, Succeeded = succeeded,
            OrderReference = orderRef, PlanName = sub.Plan.Name, Amount = sub.Plan.MonthlyPrice, OccurredAtUtc = occurred, ReceivedAtUtc = Now
        });

    /// <summary>Saves; a concurrent copy of the same provider event (unique index) is treated as already processed.</summary>
    private async Task<bool> SaveIgnoringDuplicateEventAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
