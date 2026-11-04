namespace TriggerCheck.Web.Billing;

/// <summary>Self-service sign-up, trial and subscription rules (<c>Billing</c> config section).</summary>
public class BillingOptions
{
    public const string Section = "Billing";

    public int TrialDays { get; set; } = 14;
    /// <summary>The "trial ends soon" e-mail is sent this many days before the trial ends.</summary>
    public int TrialReminderDaysBefore { get; set; } = 3;
    /// <summary>Days a past-due subscription keeps its plan before the customer is moved to the fallback plan.</summary>
    public int PastDueGraceDays { get; set; } = 7;
    /// <summary>Plan customers fall back to when a trial or subscription ends.</summary>
    public string FallbackPlanName { get; set; } = "Basic";
    /// <summary>How often trial expiry / grace periods are processed. 0 disables the background job.</summary>
    public int MaintenanceIntervalMinutes { get; set; } = 60;
    /// <summary>Sign-up attempts (POSTs) allowed per client IP per hour.</summary>
    public int RegistrationsPerHourPerIp { get; set; } = 5;
    /// <summary>Webhook requests allowed per client IP per minute.</summary>
    public int WebhooksPerMinutePerIp { get; set; } = 120;
    /// <summary>
    /// Public base URL (e.g. https://app.example.com) used in e-mail links and the payment callback. Empty = taken
    /// from the request (then AllowedHosts must be restricted so a forged Host header cannot end up in e-mails).
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";
}

/// <summary>iyzico credentials and endpoints (<c>Iyzico</c> config section). Secrets come from configuration only.</summary>
public class IyzicoOptions
{
    public const string Section = "Iyzico";

    public string ApiKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    /// <summary>Merchant id; part of the webhook signature (it is not in the webhook body).</summary>
    public string MerchantId { get; set; } = "";
    /// <summary>https://sandbox-api.iyzipay.com (sandbox) or https://api.iyzipay.com (production).</summary>
    public string BaseUrl { get; set; } = "https://sandbox-api.iyzipay.com";
    /// <summary>
    /// Hosted checkout page used when the initialize response carries no payment page URL; the token and language are
    /// appended as query parameters. TODO-verify against a live sandbox account (https://cpp.iyzipay.com in production).
    /// </summary>
    public string CheckoutPageUrl { get; set; } = "https://sandbox-cpp.iyzipay.com";
    public int TimeoutSeconds { get; set; } = 20;

    public bool IsConfigured => ApiKey.Length > 0 && SecretKey.Length > 0;
}
