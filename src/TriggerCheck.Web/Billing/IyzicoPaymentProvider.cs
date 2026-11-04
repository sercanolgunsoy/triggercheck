using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace TriggerCheck.Web.Billing;

/// <summary>
/// iyzico subscription API over plain HttpClient (the official iyzipay-dotnet package targets .NET Framework-era APIs
/// and is not used). Implemented from docs.iyzico.com (subscription transactions, IYZWSv2 HMAC-SHA256 auth and the
/// X-IYZ-SIGNATURE-V3 webhook signature). Points marked TODO-verify could not be confirmed without a sandbox account.
/// </summary>
public sealed class IyzicoPaymentProvider(HttpClient http, IOptionsMonitor<IyzicoOptions> options, TimeProvider time, ILogger<IyzicoPaymentProvider> logger)
    : IPaymentProvider
{
    public const string HttpClientName = "iyzico";
    public const string SignatureHeader = "X-IYZ-SIGNATURE-V3";

    public string Name => "iyzico";
    public bool IsConfigured => options.CurrentValue.IsConfigured;

    public async Task<CheckoutSession?> StartCheckoutAsync(CheckoutRequest r, CancellationToken ct)
    {
        var c = r.Contact;
        var address = new JsonObject
        {
            ["contactName"] = $"{c.Name} {c.Surname}", ["city"] = c.City, ["country"] = c.Country, ["address"] = c.Address, ["zipCode"] = c.ZipCode
        };
        var body = new JsonObject
        {
            ["locale"] = r.Locale,
            ["conversationId"] = r.ConversationId,
            ["callbackUrl"] = r.CallbackUrl,
            ["pricingPlanReferenceCode"] = r.PricingPlanReferenceCode,
            ["subscriptionInitialStatus"] = "ACTIVE",
            ["customer"] = new JsonObject
            {
                ["name"] = c.Name, ["surname"] = c.Surname, ["email"] = c.Email, ["gsmNumber"] = c.Phone, ["identityNumber"] = c.IdentityNumber,
                ["billingAddress"] = address, ["shippingAddress"] = address.DeepClone()
            }
        };
        var (response, _) = await SendAsync(HttpMethod.Post, "/v2/subscription/checkoutform/initialize", body, ct);
        if (response is null) return null;
        var token = response["token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token)) { logger.LogWarning("iyzico checkout initialize returned no token."); return null; }

        // The subscription checkout form returns an embeddable script (checkoutFormContent). Embedding it would need
        // third-party scripts in our CSP, so the customer is sent to iyzico's hosted page instead.
        // TODO-verify: hosted page URL for subscription tokens (paymentPageUrl is used when iyzico returns it).
        var pageUrl = response["paymentPageUrl"]?.GetValue<string>();
        if (string.IsNullOrEmpty(pageUrl))
            pageUrl = $"{options.CurrentValue.CheckoutPageUrl.TrimEnd('/')}/?token={Uri.EscapeDataString(token)}&lang={r.Locale}";
        return new CheckoutSession(token, pageUrl);
    }

    public async Task<CheckoutResult> GetCheckoutResultAsync(string token, CancellationToken ct)
    {
        var (response, rejected) = await SendAsync(HttpMethod.Get, "/v2/subscription/checkoutform/" + Uri.EscapeDataString(token), null, ct);
        // Only an explicit answer from iyzico is definitive; network errors, 5xx and unreadable bodies are retried later.
        if (rejected) return new CheckoutResult(ProviderSubscriptionState.Rejected);
        if (response?["data"] is not JsonObject data) return new CheckoutResult(ProviderSubscriptionState.Unknown);
        return new CheckoutResult(ParseState(Str(data, "subscriptionStatus")), Str(data, "referenceCode"),
            Str(data, "customerReferenceCode"), Str(data, "pricingPlanReferenceCode"));
    }

    public async Task<ProviderResult> ChangePlanAsync(string subscriptionReference, string newPricingPlanReferenceCode, bool immediately, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["newPricingPlanReferenceCode"] = newPricingPlanReferenceCode, ["upgradePeriod"] = immediately ? "NOW" : "NEXT_PERIOD",
            ["useTrial"] = false, ["resetRecurrenceCount"] = true
        };
        var (response, _) = await SendAsync(HttpMethod.Post, $"/v2/subscription/subscriptions/{Uri.EscapeDataString(subscriptionReference)}/upgrade", body, ct);
        if (response is null) return new ProviderResult(false, "plan change failed");
        var data = response["data"] as JsonObject;
        // An immediate change must leave an ACTIVE subscription (TODO-verify the state iyzico reports for NEXT_PERIOD).
        var status = data is null ? null : Str(data, "subscriptionStatus");
        if (immediately && status is not null && ParseState(status) != ProviderSubscriptionState.Active)
        {
            logger.LogWarning("iyzico plan change returned subscription status {Status}.", status);
            return new ProviderResult(false, "unexpected subscription status");
        }
        // The upgrade may answer with a new subscription reference (the old one becomes its parent).
        return new ProviderResult(true, null, data is null ? null : Str(data, "referenceCode"));
    }

    public async Task<ProviderResult> CancelAsync(string subscriptionReference, CancellationToken ct)
    {
        var body = new JsonObject { ["subscriptionReferenceCode"] = subscriptionReference };
        var (response, _) = await SendAsync(HttpMethod.Post, $"/v2/subscription/subscriptions/{Uri.EscapeDataString(subscriptionReference)}/cancel", body, ct);
        return response is null ? new ProviderResult(false, "cancel failed") : ProviderResult.Ok;
    }

    public WebhookEvent? ParseWebhook(string body, IHeaderDictionary headers)
    {
        var o = options.CurrentValue;
        if (!o.IsConfigured || string.IsNullOrEmpty(o.MerchantId))
        {
            logger.LogWarning("iyzico webhook rejected: Iyzico:SecretKey / Iyzico:MerchantId are not configured.");
            return null;
        }
        JsonObject? json;
        try { json = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return null; }
        if (json is null) return null;

        string? S(string name) => json[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : json[name]?.ToString();
        var eventType = S("iyziEventType") ?? "";
        var subscriptionRef = S("subscriptionReferenceCode") ?? "";
        var orderRef = S("orderReferenceCode") ?? "";
        var customerRef = S("customerReferenceCode") ?? "";
        if (eventType.Length == 0 || subscriptionRef.Length == 0) return null;

        var expected = WebhookSignature(o.MerchantId, o.SecretKey, eventType, subscriptionRef, orderRef, customerRef);
        var given = headers[SignatureHeader].ToString().Trim().ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(given)))
            return null;

        // iyziReferenceCode and iyziEventTime are not covered by the signature: the event id uses signed fields only.
        var eventId = $"{eventType}:{subscriptionRef}:{orderRef}";
        var occurred = time.GetUtcNow().UtcDateTime;
        if (long.TryParse(S("iyziEventTime"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t > 0)
            occurred = (t > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(t) : DateTimeOffset.FromUnixTimeSeconds(t)).UtcDateTime;
        var kind = eventType switch
        {
            "subscription.order.success" => WebhookEventKind.PaymentSucceeded,
            "subscription.order.failure" => WebhookEventKind.PaymentFailed,
            _ => WebhookEventKind.Other
        };
        return new WebhookEvent(eventId, eventType, kind, subscriptionRef, orderRef.Length > 0 ? orderRef : null,
            customerRef.Length > 0 ? customerRef : null, occurred);
    }

    /// <summary>X-IYZ-SIGNATURE-V3 for subscription webhooks: hex HMAC-SHA256(secretKey, merchantId + secretKey + eventType + subscriptionRef + orderRef + customerRef).</summary>
    public static string WebhookSignature(string merchantId, string secretKey, string eventType, string subscriptionRef, string orderRef, string customerRef) =>
        HmacHex(secretKey, merchantId + secretKey + eventType + subscriptionRef + orderRef + customerRef);

    /// <summary>
    /// IYZWSv2 header value: "IYZWSv2 " + base64("apiKey:{key}&amp;randomKey:{rnd}&amp;signature:{hex HMAC-SHA256(secret, rnd + path + body)}").
    /// <paramref name="path"/> is the URI path without query string (TODO-verify for query strings; none are used here).
    /// </summary>
    public static string Authorization(string apiKey, string secretKey, string randomKey, string path, string? body)
    {
        var signature = HmacHex(secretKey, randomKey + path + (body ?? ""));
        var auth = $"apiKey:{apiKey}&randomKey:{randomKey}&signature:{signature}";
        return "IYZWSv2 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(auth));
    }

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string HmacHex(string key, string data) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data)));

    private static ProviderSubscriptionState ParseState(string? s) => s?.ToUpperInvariant() switch
    {
        "ACTIVE" => ProviderSubscriptionState.Active,
        "PENDING" => ProviderSubscriptionState.Pending,
        "UNPAID" => ProviderSubscriptionState.Unpaid,
        "CANCELED" or "CANCELLED" => ProviderSubscriptionState.Cancelled,
        "EXPIRED" => ProviderSubscriptionState.Expired,
        "UPGRADED" => ProviderSubscriptionState.Upgraded,
        _ => ProviderSubscriptionState.Unknown
    };

    /// <summary>
    /// Signed request. Returns the JSON response when iyzico answers status "success"; otherwise logs and returns null,
    /// with <c>Rejected</c> = true when iyzico itself answered "failure" with a 4xx or 200 status (a definitive no), as
    /// opposed to network errors, timeouts, 5xx and unreadable bodies.
    /// </summary>
    private async Task<(JsonObject? Response, bool Rejected)> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.IsConfigured) { logger.LogWarning("iyzico is not configured (Iyzico:ApiKey / Iyzico:SecretKey)."); return (null, false); }

        // The signature covers the exact body bytes, so the JSON is serialized once and sent as is.
        var json = body?.ToJsonString();
        var randomKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + RandomNumberGenerator.GetInt32(100_000_000, 999_999_999);
        using var request = new HttpRequestMessage(method, o.BaseUrl.TrimEnd('/') + path);
        request.Headers.TryAddWithoutValidation("Authorization", Authorization(o.ApiKey, o.SecretKey, randomKey, path, json));
        request.Headers.TryAddWithoutValidation("x-iyzi-rnd", randomKey);
        request.Headers.Accept.ParseAdd("application/json");
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));
        try
        {
            using var response = await http.SendAsync(request, cts.Token);
            var text = await response.Content.ReadAsStringAsync(cts.Token);
            var result = JsonNode.Parse(text) as JsonObject;
            var status = result is null ? null : Str(result, "status");
            if (response.IsSuccessStatusCode && status == "success") return (result, false);
            logger.LogWarning("iyzico {Method} {Path} failed: HTTP {Status}, error {Code} {Message}", method, path, (int)response.StatusCode,
                result?["errorCode"]?.ToString(), result?["errorMessage"]?.ToString());
            return (null, status == "failure" && (int)response.StatusCode < 500);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "iyzico {Method} {Path} failed.", method, path);
            return (null, false);
        }
    }
}
