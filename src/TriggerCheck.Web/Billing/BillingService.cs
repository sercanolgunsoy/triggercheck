using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.SuperAdmin.Services;

namespace TriggerCheck.Web.Billing;

public enum BillingError { None, PlanNotAvailable, TooManyMonitors, ProviderUnavailable, ProviderFailed, InvalidState }

/// <param name="MonitorsToDelete">For <see cref="BillingError.TooManyMonitors"/>: how many monitors must be deleted first.</param>
/// <param name="EffectiveAtUtc">For a downgrade: when the new plan takes effect (end of the paid period).</param>
public sealed record BillingOutcome(BillingError Error, int MonitorsToDelete = 0, int? SubscriptionId = null, int AdjustedMonitors = 0,
    DateTime? EffectiveAtUtc = null)
{
    public bool Succeeded => Error == BillingError.None;
}

/// <summary>
/// <see cref="Pending"/>: the provider has not confirmed the checkout yet; it is checked again later.
/// <see cref="Refused"/>: paid, but not activated (second subscription or unexpected plan); cancelled at the provider,
/// the customer and the operators are told that the payment is refunded.
/// </summary>
public enum CheckoutCompletion { Activated, AlreadyProcessed, Pending, Failed, Refused, NotFound }

public enum WebhookOutcome { Processed, Duplicate, Ignored }

/// <summary>
/// Subscription lifecycle (BIZ-2): checkout, server-side verification, plan changes, cancellation, webhook events and
/// the downgrade to the fallback plan. Provider calls go through <see cref="IPaymentProvider"/>; every plan change
/// clamps monitor intervals in the same transaction as the customer update (SuperAdminOps.ChangePlanAsync).
/// A paid subscription is never left behind: checkouts are only closed on a definitive provider answer, duplicates
/// and mismatches are cancelled at the provider, and webhooks for unknown subscriptions trigger a re-check.
/// </summary>
public sealed class BillingService(AppDbContext db, IPaymentProvider provider, TimeProvider time, IOptionsMonitor<BillingOptions> options,
    BillingMailer mailer, ILogger<BillingService> logger)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static readonly SubscriptionStatus[] Current = [SubscriptionStatus.Active, SubscriptionStatus.PastDue, SubscriptionStatus.Cancelled];
    private static readonly SubscriptionStatus[] Open = [SubscriptionStatus.Pending, SubscriptionStatus.Superseded];

    /// <summary>Paid checkout and plan changes are possible (feature switched on and provider configured).</summary>
    public bool PaidCheckoutAvailable => options.CurrentValue.PaidCheckoutEnabled && provider.IsConfigured;

    /// <summary>The subscription that currently defines the customer's billing (active, past due or cancelled-at-period-end).</summary>
    public Task<Subscription?> CurrentSubscriptionAsync(int customerId, CancellationToken ct = default) =>
        db.Subscriptions.Include(s => s.Plan)
            .Where(s => s.CustomerId == customerId && Current.Contains(s.Status))
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

    /// <summary>
    /// Starts a hosted checkout for <paramref name="planId"/>; the customer completes it on the provider's page.
    /// Older open checkouts of the customer are superseded (still verified, in case one was paid in another tab).
    /// </summary>
    public async Task<BillingOutcome> StartCheckoutAsync(int customerId, int planId, BillingContact contact, string callbackUrl, string locale, CancellationToken ct)
    {
        if (!PaidCheckoutAvailable) return new(BillingError.ProviderUnavailable);
        var plan = (await PurchasablePlansAsync(ct)).FirstOrDefault(p => p.Id == planId);
        if (plan is null) return new(BillingError.PlanNotAvailable);
        var current = await CurrentSubscriptionAsync(customerId, ct);
        // An existing subscription is changed in place (ChangePlanAsync); only a cancelled one can be replaced by a new checkout.
        if (current is not null && current.Status != SubscriptionStatus.Cancelled) return new(BillingError.InvalidState);
        if (await MonitorsOverLimitAsync(customerId, plan, ct) is > 0 and var over) return new(BillingError.TooManyMonitors, over);

        var session = await provider.StartCheckoutAsync(new CheckoutRequest(Guid.NewGuid().ToString("N"), plan.IyzicoPricingPlanReferenceCode!,
            callbackUrl, locale, contact), ct);
        if (session is null) return new(BillingError.ProviderFailed);

        await db.Subscriptions.Where(s => s.CustomerId == customerId && s.Status == SubscriptionStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SubscriptionStatus.Superseded), ct);
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
    /// Return/callback of the hosted checkout (also used by the maintenance re-check and webhook reconciliation). The
    /// token only identifies the checkout: the result is always retrieved from the provider. Idempotent.
    /// <list type="bullet">
    /// <item>Not definitive (network error, provider still pending): the checkout stays open and is checked again later.</item>
    /// <item>Paid but for another pricing plan, or a second subscription of a customer that already has one: cancelled
    /// at the provider so nobody is charged for a plan they do not get.</item>
    /// <item>Paid and matching: activated; other open checkouts of the customer are superseded.</item>
    /// </list>
    /// </summary>
    public async Task<(CheckoutCompletion Result, int? CustomerId)> CompleteCheckoutAsync(string token, CancellationToken ct, bool finalCheck = false)
    {
        var sub = await db.Subscriptions.Include(s => s.Plan).Include(s => s.Customer).ThenInclude(c => c.Plan)
            .FirstOrDefaultAsync(s => s.CheckoutToken == token, ct);
        if (sub is null) return (CheckoutCompletion.NotFound, null);
        // A failed checkout is only looked at again when it never reached the provider's "paid" state (a paid one that we
        // refused was cancelled at the provider and must stay closed).
        var wasFailed = sub.Status == SubscriptionStatus.Failed;
        if (wasFailed && sub.ProviderReference is not null) return (CheckoutCompletion.Failed, sub.CustomerId);
        if (!Open.Contains(sub.Status) && !wasFailed) return (CheckoutCompletion.AlreadyProcessed, sub.CustomerId);

        var result = await provider.GetCheckoutResultAsync(token, ct);
        // Never close on "no answer" without asking once more.
        if (finalCheck && result.State == ProviderSubscriptionState.Unknown) result = await provider.GetCheckoutResultAsync(token, ct);
        var now = Now;
        sub.LastCheckedAtUtc = now;
        if (!result.Succeeded)
        {
            if (wasFailed || (!result.IsDefinitive && !finalCheck))
            {
                await db.SaveChangesAsync(ct);
                return (wasFailed ? CheckoutCompletion.Failed : CheckoutCompletion.Pending, sub.CustomerId);
            }
            logger.LogWarning("Checkout {SubscriptionId} of customer {CustomerId} was not completed (state {State}).", sub.Id, sub.CustomerId, result.State);
            await CloseAsFailedAsync(sub, "checkout.failure", ct);
            return (CheckoutCompletion.Failed, sub.CustomerId);
        }

        sub.ProviderReference = result.SubscriptionReference;
        sub.ProviderCustomerReference = result.CustomerReference;
        // A missing or different pricing plan code means we cannot tell what was bought: refuse and refund by cancelling.
        if (result.PricingPlanReferenceCode is null || result.PricingPlanReferenceCode != sub.Plan.IyzicoPricingPlanReferenceCode)
        {
            logger.LogWarning("Checkout {SubscriptionId}: provider reports pricing plan {Code}, expected {Expected}; cancelling it at the provider.",
                sub.Id, result.PricingPlanReferenceCode, sub.Plan.IyzicoPricingPlanReferenceCode);
            await CancelUnwantedAsync(sub, "checkout.mismatch", ct);
            return (CheckoutCompletion.Refused, sub.CustomerId);
        }

        var customer = sub.Customer;
        var duplicate = false;
        try
        {
            // The "already subscribed" check runs inside the (write-locking) transaction, so two concurrent activations
            // of the same customer cannot both pass it.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var existing = await db.Subscriptions.Where(s => s.CustomerId == customer.Id && s.Id != sub.Id && Current.Contains(s.Status)).ToListAsync(ct);
            if (existing.Any(s => s.Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue))
            {
                duplicate = true;
                await tx.RollbackAsync(ct);
            }
            else
            {
                // A cancelled-at-period-end subscription is replaced by the new one.
                foreach (var p in existing) { p.Status = SubscriptionStatus.Expired; p.EndedAtUtc = now; }
                await db.Subscriptions.Where(s => s.CustomerId == customer.Id && s.Id != sub.Id && s.Status == SubscriptionStatus.Pending)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SubscriptionStatus.Superseded), ct);
                sub.Status = SubscriptionStatus.Active;
                sub.ActivatedAtUtc = now;
                sub.EndedAtUtc = null;
                sub.CurrentPeriodEndUtc = now.AddMonths(1);
                await SuperAdminOps.ChangePlanAsync(db, customer, sub.Plan);
                customer.BillingStatus = BillingStatus.Active;
                customer.TrialEndsAtUtc = null;
                AddEvent(sub, $"checkout:{token}", "checkout.success", true, null, now);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent callback / re-check activated it first.
            db.ChangeTracker.Clear();
            return (CheckoutCompletion.AlreadyProcessed, sub.CustomerId);
        }
        if (duplicate)
        {
            await CancelUnwantedAsync(sub, "checkout.duplicate", ct);
            return (CheckoutCompletion.Refused, sub.CustomerId);
        }
        logger.LogInformation("Subscription {SubscriptionId} of customer {CustomerId} activated on plan {Plan}.", sub.Id, customer.Id, sub.Plan.Name);
        return (CheckoutCompletion.Activated, customer.Id);
    }

    private async Task CloseAsFailedAsync(Subscription sub, string eventType, CancellationToken ct)
    {
        sub.Status = SubscriptionStatus.Failed;
        sub.EndedAtUtc = Now;
        AddEvent(sub, $"{eventType}:{sub.CheckoutToken}", eventType, false, null, Now);
        await SaveIgnoringDuplicateEventAsync(ct);
    }

    /// <summary>
    /// A paid checkout we do not activate (second subscription or unexpected plan): stop it at the provider, keep a
    /// record and tell the operators (refund) and the customer. Refunds are done manually in the iyzico panel until the
    /// subscription refund API is verified (TODO-verify, see the sandbox checklist).
    /// </summary>
    private async Task CancelUnwantedAsync(Subscription sub, string eventType, CancellationToken ct)
    {
        var cancelled = await provider.CancelAsync(sub.ProviderReference!, ct);
        logger.LogError("Paid subscription {Reference} of customer {CustomerId} was refused ({Reason}); cancel at provider {Cancelled}. Refund it in the iyzico panel.",
            sub.ProviderReference, sub.CustomerId, eventType, cancelled.Succeeded ? "succeeded" : "FAILED - cancel it manually");
        await CloseAsFailedAsync(sub, eventType, ct);
        await mailer.UnwantedPaymentAsync(sub.CustomerId, sub.ProviderReference!, eventType, cancelled.Succeeded, ct);
    }

    /// <summary>
    /// Upgrade (immediately, at the provider's terms) or downgrade (booked at the provider for the next period and
    /// applied when the period rolls over) of an active subscription.
    /// </summary>
    public async Task<BillingOutcome> ChangePlanAsync(int customerId, int planId, CancellationToken ct)
    {
        if (!PaidCheckoutAvailable) return new(BillingError.ProviderUnavailable);
        var sub = await CurrentSubscriptionAsync(customerId, ct);
        if (sub is null || sub.Status != SubscriptionStatus.Active || sub.ProviderReference is null) return new(BillingError.InvalidState);
        var plan = (await PurchasablePlansAsync(ct)).FirstOrDefault(p => p.Id == planId);
        if (plan is null) return new(BillingError.PlanNotAvailable);
        if (plan.Id == sub.PlanId)
        {
            // Choosing the current plan again cancels a booked downgrade.
            if (sub.ScheduledPlanId is null) return new(BillingError.PlanNotAvailable);
            var undo = await provider.ChangePlanAsync(sub.ProviderReference, plan.IyzicoPricingPlanReferenceCode!, immediately: false, ct);
            if (!undo.Succeeded) return new(BillingError.ProviderFailed);
            sub.ScheduledPlanId = null;
            await db.SaveChangesAsync(ct);
            return new(BillingError.None);
        }
        if (await MonitorsOverLimitAsync(customerId, plan, ct) is > 0 and var over) return new(BillingError.TooManyMonitors, over);

        var upgrade = plan.MonthlyPrice > sub.Plan.MonthlyPrice;
        var r = await provider.ChangePlanAsync(sub.ProviderReference, plan.IyzicoPricingPlanReferenceCode!, immediately: upgrade, ct);
        if (!r.Succeeded) return new(BillingError.ProviderFailed);
        if (!string.IsNullOrEmpty(r.NewSubscriptionReference)) sub.ProviderReference = r.NewSubscriptionReference;

        if (!upgrade)
        {
            sub.ScheduledPlanId = plan.Id;
            await db.SaveChangesAsync(ct);
            return new(BillingError.None, EffectiveAtUtc: sub.CurrentPeriodEndUtc);
        }

        var customer = await db.Customers.Include(c => c.Plan).FirstAsync(c => c.Id == customerId, ct);
        var tracked = await db.Plans.FirstAsync(p => p.Id == plan.Id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var change = await SuperAdminOps.ChangePlanAsync(db, customer, tracked);
        sub.PlanId = tracked.Id;
        sub.Plan = tracked;
        sub.ScheduledPlanId = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(BillingError.None, AdjustedMonitors: change.AdjustedMonitors);
    }

    /// <summary>
    /// Applies a booked downgrade (period rolled over). Intervals are clamped in the same transaction; when the customer
    /// has more monitors than the new plan allows, the admins are told which ones (nothing is deleted).
    /// </summary>
    public async Task ApplyScheduledPlanAsync(Subscription sub, CancellationToken ct)
    {
        if (sub.ScheduledPlanId is not int planId) return;
        var plan = await db.Plans.FirstAsync(p => p.Id == planId, ct);
        var customer = await db.Customers.Include(c => c.Plan).FirstAsync(c => c.Id == sub.CustomerId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await SuperAdminOps.ChangePlanAsync(db, customer, plan);
        sub.PlanId = plan.Id;
        sub.Plan = plan;
        sub.ScheduledPlanId = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Scheduled plan change of customer {CustomerId} to {Plan} applied.", customer.Id, plan.Name);

        // Checked again now: monitors may have been added since the downgrade was booked. Nothing is deleted (intervals
        // were clamped above); the admins learn which monitors are over the limit.
        var monitors = await db.Monitors.Where(m => m.CustomerId == customer.Id).OrderBy(m => m.Id).Select(m => m.Name).ToListAsync(ct);
        if (monitors.Count > plan.MaxMonitors)
        {
            logger.LogWarning("Customer {CustomerId} has {Count} monitors after the downgrade to {Plan} (max {Max}).", customer.Id, monitors.Count, plan.Name, plan.MaxMonitors);
            await mailer.OverPlanLimitAsync(customer.Id, plan, monitors.Skip(plan.MaxMonitors).ToList(), ct);
        }
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
        sub.ScheduledPlanId = null;
        sub.CurrentPeriodEndUtc ??= Now;
        await db.Customers.Where(c => c.Id == customerId).ExecuteUpdateAsync(s => s.SetProperty(c => c.BillingStatus, BillingStatus.Cancelled), ct);
        await db.SaveChangesAsync(ct);
        return new(BillingError.None);
    }

    public async Task<bool> CancelAtProviderAsync(string subscriptionReference, CancellationToken ct) =>
        (await provider.CancelAsync(subscriptionReference, ct)).Succeeded;

    /// <summary>Unmatched events and failed checkouts are re-checked by the job for this long.</summary>
    public static readonly TimeSpan ReconcileWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// Applies a signature-verified webhook. Each event (signed type + subscription + order reference) is processed
    /// once; events outside the accepted time window or older than the last applied one are recorded but not applied.
    /// An event for an unknown subscription is recorded as ignored right away (no provider calls inside the webhook
    /// request); the billing job re-checks the open checkouts for it and applies it later (<see cref="ReconcileIgnoredEventsAsync"/>).
    /// </summary>
    public async Task<WebhookOutcome> ApplyWebhookAsync(WebhookEvent e, CancellationToken ct)
    {
        if (await db.PaymentEvents.AnyAsync(x => x.ProviderEventId == e.EventId, ct)) return WebhookOutcome.Duplicate;
        var now = Now;
        if (e.OccurredAtUtc < now.AddHours(-options.CurrentValue.WebhookMaxAgeHours) || e.OccurredAtUtc > now.AddMinutes(10))
        {
            logger.LogWarning("Webhook {EventType} for {Reference} outside the accepted time window ({Time:o}); recorded, not applied.", e.EventType, e.SubscriptionReference, e.OccurredAtUtc);
            return await RecordIgnoredAsync(e, null, IgnoredTimeWindow, ct);
        }

        var sub = await FindSubscriptionAsync(e.SubscriptionReference, ct);
        if (sub is null)
        {
            logger.LogWarning("Webhook {EventType} for unknown subscription {Reference}; recorded, the billing job reconciles it.", e.EventType, e.SubscriptionReference);
            return await RecordIgnoredAsync(e, null, IgnoredUnknownSubscription, ct);
        }
        if (sub.LastEventAtUtc is { } last && e.OccurredAtUtc < last)
        {
            logger.LogInformation("Webhook {EventType} for subscription {SubscriptionId} is older than the last applied event; recorded, not applied.", e.EventType, sub.Id);
            return await RecordIgnoredAsync(e, sub, IgnoredOutOfOrder, ct);
        }
        return await ApplyToSubscriptionAsync(sub, e, null, ct) ? WebhookOutcome.Processed : WebhookOutcome.Duplicate;
    }

    public const string IgnoredUnknownSubscription = "unknown-subscription";
    public const string IgnoredOutOfOrder = "out-of-order";
    public const string IgnoredTimeWindow = "time-window";

    /// <summary>State change of one event; <paramref name="row"/> is the stored (previously ignored) event when reconciling.</summary>
    private async Task<bool> ApplyToSubscriptionAsync(Subscription sub, WebhookEvent e, PaymentEvent? row, CancellationToken ct)
    {
        var notifyFailure = false;
        switch (e.Kind)
        {
            case WebhookEventKind.PaymentSucceeded when sub.Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue:
                // The first order of a new subscription belongs to the period started at checkout.
                var renewal = sub.ActivatedAtUtc is not { } activated || e.OccurredAtUtc - activated > TimeSpan.FromDays(1);
                if (renewal)
                {
                    if (sub.ScheduledPlanId is not null) await ApplyScheduledPlanAsync(sub, ct);
                    var from = sub.CurrentPeriodEndUtc is { } end && end > e.OccurredAtUtc ? end : e.OccurredAtUtc;
                    sub.CurrentPeriodEndUtc = from.AddMonths(1);
                }
                sub.Status = SubscriptionStatus.Active;
                sub.PastDueSinceUtc = null;
                sub.Customer.BillingStatus = BillingStatus.Active;
                break;
            case WebhookEventKind.PaymentFailed when sub.Status is SubscriptionStatus.Active:
                sub.Status = SubscriptionStatus.PastDue;
                sub.PastDueSinceUtc = e.OccurredAtUtc;
                sub.Customer.BillingStatus = BillingStatus.PastDue;
                notifyFailure = true;
                break;
        }
        sub.LastEventAtUtc = e.OccurredAtUtc;
        if (row is null) AddEvent(sub, e.EventId, e.EventType, e.Kind == WebhookEventKind.PaymentSucceeded, e.OrderReference, e.OccurredAtUtc, e);
        else
        {
            row.Ignored = false;
            row.IgnoredReason = null;
            row.CustomerId = sub.CustomerId;
            row.SubscriptionId = sub.Id;
            row.PlanName = sub.Plan.Name;
        }
        if (!await SaveIgnoringDuplicateEventAsync(ct)) return false;
        if (notifyFailure) await mailer.PaymentFailedAsync(sub.CustomerId, ct);
        return true;
    }

    private Task<Subscription?> FindSubscriptionAsync(string reference, CancellationToken ct) =>
        db.Subscriptions.Include(s => s.Plan).Include(s => s.Customer)
            .Where(s => s.ProviderReference == reference).OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct);

    private static WebhookEventKind KindOf(string eventType) => eventType switch
    {
        "subscription.order.success" => WebhookEventKind.PaymentSucceeded,
        "subscription.order.failure" => WebhookEventKind.PaymentFailed,
        _ => WebhookEventKind.Other
    };

    /// <summary>
    /// Job step: events recorded for an unknown subscription (last 30 days, each at most hourly, <paramref name="max"/>
    /// per run) re-check the open and failed checkouts; when one turns out to be the subscription, the event is applied.
    /// </summary>
    public async Task<int> ReconcileIgnoredEventsAsync(int max, CancellationToken ct)
    {
        var now = Now;
        var since = now - ReconcileWindow;
        var recheckBefore = now.AddHours(-1);
        var ids = await db.PaymentEvents
            .Where(x => x.Ignored && x.IgnoredReason == IgnoredUnknownSubscription && x.OccurredAtUtc >= since && (x.LastCheckedAtUtc == null || x.LastCheckedAtUtc < recheckBefore))
            .OrderBy(x => x.LastCheckedAtUtc ?? DateTime.MinValue).Select(x => x.Id).Take(max).ToListAsync(ct);
        var applied = 0;
        foreach (var id in ids)
        {
            var row = await db.PaymentEvents.FirstAsync(x => x.Id == id, ct);
            row.LastCheckedAtUtc = now;
            await db.SaveChangesAsync(ct);
            if (row.SubscriptionReference is not { } reference) continue;
            var e = new WebhookEvent(row.ProviderEventId, row.EventType, KindOf(row.EventType), reference, row.OrderReference, row.CustomerReference, row.OccurredAtUtc);
            var sub = await FindSubscriptionAsync(reference, ct) ?? await ReconcileAsync(e, ct);
            db.ChangeTracker.Clear();
            sub = sub is null ? null : await FindSubscriptionAsync(reference, ct);
            if (sub is null || sub.Status == SubscriptionStatus.Failed) continue;
            row = await db.PaymentEvents.FirstAsync(x => x.Id == id, ct);
            if (sub.LastEventAtUtc is { } last && e.OccurredAtUtc < last)
            {
                row.IgnoredReason = IgnoredOutOfOrder;
                row.CustomerId = sub.CustomerId;
                row.SubscriptionId = sub.Id;
                await db.SaveChangesAsync(ct);
                continue;
            }
            if (await ApplyToSubscriptionAsync(sub, e, row, ct)) applied++;
            db.ChangeTracker.Clear();
        }
        return applied;
    }

    /// <summary>
    /// Job step: failed checkouts that never reached the provider's "paid" state are asked again for 30 days (each at
    /// most every 6 hours, <paramref name="max"/> per run), so a payment confirmed late by the provider is not lost.
    /// </summary>
    public async Task<int> RescanFailedCheckoutsAsync(int max, CancellationToken ct)
    {
        var now = Now;
        var since = now - ReconcileWindow;
        var recheckBefore = now.AddHours(-6);
        var tokens = await db.Subscriptions
            .Where(x => x.Status == SubscriptionStatus.Failed && x.ProviderReference == null && x.CheckoutToken != null && x.CreatedAtUtc >= since
                        && (x.LastCheckedAtUtc == null || x.LastCheckedAtUtc < recheckBefore))
            .OrderBy(x => x.LastCheckedAtUtc ?? DateTime.MinValue).Select(x => x.CheckoutToken!).Take(max).ToListAsync(ct);
        var activated = 0;
        foreach (var token in tokens)
        {
            if ((await CompleteCheckoutAsync(token, ct)).Result == CheckoutCompletion.Activated) activated++;
            db.ChangeTracker.Clear();
        }
        return activated;
    }

    /// <summary>
    /// An event for a subscription we do not know: re-check the open and recently failed checkouts - first those of the
    /// customer with the same provider customer reference, then the most recent ones (capped) - until one is it.
    /// </summary>
    private async Task<Subscription?> ReconcileAsync(WebhookEvent e, CancellationToken ct)
    {
        var since = Now - ReconcileWindow;
        var candidates = db.Subscriptions.Where(s => s.CheckoutToken != null && s.CreatedAtUtc >= since
            && (Open.Contains(s.Status) || (s.Status == SubscriptionStatus.Failed && s.ProviderReference == null)));
        List<string> tokens = [];
        if (e.CustomerReference is { } customerRef)
        {
            var customerIds = db.Subscriptions.Where(s => s.ProviderCustomerReference == customerRef).Select(s => s.CustomerId);
            tokens = await candidates.Where(s => customerIds.Contains(s.CustomerId)).OrderByDescending(s => s.Id).Select(s => s.CheckoutToken!).Take(10).ToListAsync(ct);
        }
        var recent = await candidates.OrderByDescending(s => s.Id).Select(s => s.CheckoutToken!).Take(20).ToListAsync(ct);
        tokens.AddRange(recent.Where(t => !tokens.Contains(t)));

        foreach (var token in tokens)
        {
            await CompleteCheckoutAsync(token, ct);
            db.ChangeTracker.Clear();
            if (await FindSubscriptionAsync(e.SubscriptionReference, ct) is { } found && found.Status != SubscriptionStatus.Failed) return found;
        }
        return null;
    }

    private async Task<WebhookOutcome> RecordIgnoredAsync(WebhookEvent e, Subscription? sub, string reason, CancellationToken ct)
    {
        int? customerId = sub?.CustomerId;
        if (customerId is null && e.CustomerReference is { } customerRef)
            customerId = await db.Subscriptions.Where(s => s.ProviderCustomerReference == customerRef).Select(s => (int?)s.CustomerId).FirstOrDefaultAsync(ct);
        db.PaymentEvents.Add(new PaymentEvent
        {
            CustomerId = customerId, SubscriptionId = sub?.Id, ProviderEventId = e.EventId, EventType = e.EventType, Ignored = true, IgnoredReason = reason,
            Succeeded = e.Kind == WebhookEventKind.PaymentSucceeded, SubscriptionReference = e.SubscriptionReference, CustomerReference = e.CustomerReference,
            OrderReference = e.OrderReference, PlanName = sub?.Plan.Name, OccurredAtUtc = e.OccurredAtUtc, ReceivedAtUtc = Now
        });
        return await SaveIgnoringDuplicateEventAsync(ct) ? WebhookOutcome.Ignored : WebhookOutcome.Duplicate;
    }

    /// <summary>
    /// Moves the customer to the fallback plan (intervals clamped in the same transaction), ends its subscription and
    /// e-mails the admins. The state change is conditional (<paramref name="expectedStatus"/>): when a webhook or
    /// another worker changed the customer in the meantime nothing happens and false is returned.
    /// </summary>
    public async Task<bool> DowngradeToFallbackAsync(Customer customer, Subscription? sub, BillingStatus expectedStatus, string reason, CancellationToken ct)
    {
        var fallback = await FallbackPlanAsync(ct);
        if (fallback is null) { logger.LogError("No fallback plan ({Plan}) exists; customer {CustomerId} was not downgraded.", options.CurrentValue.FallbackPlanName, customer.Id); return false; }

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var claimed = await db.Customers.Where(c => c.Id == customer.Id && c.BillingStatus == expectedStatus)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.BillingStatus, BillingStatus.None), ct);
            if (claimed == 0 || (sub is not null && await db.Subscriptions.Where(s => s.Id == sub.Id && s.Status == sub.Status)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SubscriptionStatus.Expired).SetProperty(s => s.EndedAtUtc, Now), ct) == 0))
            {
                await tx.RollbackAsync(ct);
                await db.Entry(customer).ReloadAsync(ct);
                return false;
            }
            await SuperAdminOps.ChangePlanAsync(db, customer, fallback);
            customer.BillingStatus = BillingStatus.None;
            if (sub is not null) { sub.Status = SubscriptionStatus.Expired; sub.EndedAtUtc = Now; sub.ScheduledPlanId = null; }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        logger.LogInformation("Customer {CustomerId} moved to the fallback plan {Plan}.", customer.Id, fallback.Name);
        await mailer.DowngradedAsync(customer.Id, reason, fallback, ct);
        return true;
    }

    private void AddEvent(Subscription sub, string eventId, string type, bool succeeded, string? orderRef, DateTime occurred, WebhookEvent? e = null) =>
        db.PaymentEvents.Add(new PaymentEvent
        {
            CustomerId = sub.CustomerId, SubscriptionId = sub.Id, ProviderEventId = eventId, EventType = type, Succeeded = succeeded,
            SubscriptionReference = sub.ProviderReference, CustomerReference = e?.CustomerReference ?? sub.ProviderCustomerReference,
            OrderReference = orderRef, PlanName = sub.Plan.Name, OccurredAtUtc = occurred, ReceivedAtUtc = Now
        });

    /// <summary>Saves; a concurrent copy of the same provider event (unique index) is treated as already processed.</summary>
    private async Task<bool> SaveIgnoringDuplicateEventAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true;
}
