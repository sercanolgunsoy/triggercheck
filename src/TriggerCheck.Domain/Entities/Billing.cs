namespace TriggerCheck.Domain.Entities;

/// <summary>Billing state of a customer as shown in the panels.</summary>
public enum BillingStatus
{
    None = 0,       // no self-service subscription: free plan or plan assigned by the operator
    Trial = 1,      // free trial of the chosen plan (Customer.TrialEndsAtUtc)
    Active = 2,     // paid subscription in good standing
    PastDue = 3,    // last renewal failed; downgraded after the grace period
    Cancelled = 4   // cancelled at period end; the plan stays until the paid period ends
}

/// <summary>State of one provider subscription.</summary>
public enum SubscriptionStatus
{
    Pending = 0,    // checkout started, not (yet) confirmed by the provider
    Active = 1,
    PastDue = 2,
    Cancelled = 3,  // renewal stopped at the provider; still valid until CurrentPeriodEndUtc
    Expired = 4,    // ended (period over, grace period over or replaced)
    Failed = 5,     // checkout definitively not completed (or a duplicate that was cancelled at the provider)
    Superseded = 6  // pending checkout replaced by a newer one; still verified in case it was paid in another tab
}

/// <summary>A (monthly) subscription of a customer at the payment provider. The newest non-pending row is the current one.</summary>
public class Subscription
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public int PlanId { get; set; }
    public Plan Plan { get; set; } = null!;
    public SubscriptionStatus Status { get; set; }
    /// <summary>Provider name ("iyzico", "fake" in tests).</summary>
    public string Provider { get; set; } = "";
    /// <summary>Provider subscription reference (iyzico subscriptionReferenceCode); null until the checkout is confirmed.</summary>
    public string? ProviderReference { get; set; }
    /// <summary>Provider customer reference; used to match webhooks of subscriptions we do not know yet.</summary>
    public string? ProviderCustomerReference { get; set; }
    /// <summary>Checkout token issued by the provider; the return callback is matched (and verified) with it.</summary>
    public string? CheckoutToken { get; set; }
    /// <summary>Hosted payment page of a pending checkout (provider URL, never contains card data).</summary>
    public string? CheckoutUrl { get; set; }
    public DateTime? CurrentPeriodEndUtc { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTime? PastDueSinceUtc { get; set; }
    /// <summary>Downgrade booked at the provider for the next period; applied when the period rolls over.</summary>
    public int? ScheduledPlanId { get; set; }
    /// <summary>Time of the newest webhook event applied; older (out-of-order) events are ignored.</summary>
    public DateTime? LastEventAtUtc { get; set; }
    /// <summary>Last time a failed/open checkout was checked with the provider (re-scan throttle).</summary>
    public DateTime? LastCheckedAtUtc { get; set; }
    /// <summary>Paid but refused (duplicate / plan mismatch) and cancelled: an operator must refund it.</summary>
    public DateTime? RefundNeededAtUtc { get; set; }
    /// <summary>When and by whom the refund was marked as done (SuperAdmin audit).</summary>
    public DateTime? RefundHandledAtUtc { get; set; }
    public string? RefundHandledBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
}

/// <summary>
/// Payment / subscription event received from the provider (webhook or verified checkout). The unique
/// <see cref="ProviderEventId"/> makes processing idempotent; the rows are the customer's payment history. Events that
/// could not be matched or were out of order are kept with <see cref="Ignored"/> for reconciliation.
/// </summary>
public class PaymentEvent
{
    public long Id { get; set; }
    /// <summary>Null for an ignored event that could not be matched to a customer.</summary>
    public int? CustomerId { get; set; }
    public int? SubscriptionId { get; set; }
    public string ProviderEventId { get; set; } = "";
    /// <summary>Provider event type, e.g. "subscription.order.success".</summary>
    public string EventType { get; set; } = "";
    public bool Succeeded { get; set; }
    /// <summary>Not applied (unknown subscription, out of order or outside the accepted time window).</summary>
    public bool Ignored { get; set; }
    /// <summary>Why an event was not applied: <c>unknown-subscription</c> (re-checked by the job), <c>out-of-order</c>, <c>time-window</c>.</summary>
    public string? IgnoredReason { get; set; }
    /// <summary>Last reconciliation attempt of an ignored event (re-scan throttle).</summary>
    public DateTime? LastCheckedAtUtc { get; set; }
    public string? SubscriptionReference { get; set; }
    public string? CustomerReference { get; set; }
    public string? OrderReference { get; set; }
    public string? PlanName { get; set; }
    /// <summary>Amount charged as reported by the provider (null when the provider does not report it).</summary>
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
}
