namespace TriggerCheck.Web.Billing;

/// <summary>Billing details the provider requires for a subscription checkout (no card data: the card is entered on the provider's page).</summary>
public sealed record BillingContact(string Name, string Surname, string Email, string Phone, string IdentityNumber,
    string Address, string City, string Country, string? ZipCode);

public sealed record CheckoutRequest(string ConversationId, string PricingPlanReferenceCode, string CallbackUrl, string Locale, BillingContact Contact);

/// <summary>A started hosted checkout: the provider token and the page the customer completes the payment on.</summary>
public sealed record CheckoutSession(string Token, string PaymentPageUrl);

public enum ProviderSubscriptionState { Active, Pending, Unpaid, Cancelled, Expired, Upgraded, Unknown }

/// <summary>Server-side verified outcome of a checkout (retrieved from the provider, never taken from the browser).</summary>
public sealed record CheckoutResult(bool Succeeded, string? SubscriptionReference, string? CustomerReference,
    string? PricingPlanReferenceCode, ProviderSubscriptionState State, string? Error);

public sealed record ProviderResult(bool Succeeded, string? Error, string? NewSubscriptionReference = null)
{
    public static readonly ProviderResult Ok = new(true, null);
}

public enum WebhookEventKind { PaymentSucceeded, PaymentFailed, Other }

/// <summary>A signature-verified webhook notification.</summary>
public sealed record WebhookEvent(string EventId, string EventType, WebhookEventKind Kind, string SubscriptionReference,
    string? OrderReference, string? CustomerReference, DateTime OccurredAtUtc);

/// <summary>
/// Everything payment-provider specific. The production implementation is <see cref="IyzicoPaymentProvider"/>;
/// tests register a fake. Implementations never throw for provider errors: they return a failed result (and log).
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Short provider name stored on subscriptions.</summary>
    string Name { get; }
    /// <summary>False when no credentials are configured: paid checkout is then unavailable.</summary>
    bool IsConfigured { get; }

    Task<CheckoutSession?> StartCheckoutAsync(CheckoutRequest request, CancellationToken ct);
    Task<CheckoutResult> GetCheckoutResultAsync(string token, CancellationToken ct);
    /// <summary>Moves the subscription to another pricing plan immediately (provider prorates).</summary>
    Task<ProviderResult> ChangePlanAsync(string subscriptionReference, string newPricingPlanReferenceCode, CancellationToken ct);
    /// <summary>Stops renewals of the subscription.</summary>
    Task<ProviderResult> CancelAsync(string subscriptionReference, CancellationToken ct);
    /// <summary>Verifies the signature and parses a webhook. Returns null when the signature is invalid or the body is malformed.</summary>
    WebhookEvent? ParseWebhook(string body, IHeaderDictionary headers);
}
