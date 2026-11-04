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
    public int MaintenanceIntervalMinutes { get; set; } = 15;
    /// <summary>Sign-up attempts (POSTs) allowed per client IP per hour.</summary>
    public int RegistrationsPerHourPerIp { get; set; } = 5;
    /// <summary>Webhook requests allowed per client IP per minute.</summary>
    public int WebhooksPerMinutePerIp { get; set; } = 120;
    /// <summary>Deprecated: use <c>Site:PublicBaseUrl</c>. Still read as a fallback (see <see cref="Infrastructure.PublicUrls"/>).</summary>
    public string PublicBaseUrl { get; set; } = "";
    /// <summary>
    /// Online paid checkout and plan changes through the payment provider. Off by default until the iyzico integration
    /// has been verified against a sandbox account (docs/DEPLOYMENT.md); sign-up and trials work either way.
    /// </summary>
    public bool PaidCheckoutEnabled { get; set; }
    /// <summary>A pending checkout that the provider has not confirmed within this time is closed as failed.</summary>
    public int CheckoutTimeoutMinutes { get; set; } = 60;
    /// <summary>Open (pending or superseded) checkouts a customer may have at once.</summary>
    public int MaxOpenCheckouts { get; set; } = 3;
    /// <summary>
    /// Checkout POSTs per customer per hour. The rate limiter runs before validation, so form errors count too: the limit
    /// is generous; <see cref="MaxOpenCheckouts"/> bounds the checkouts actually started.
    /// </summary>
    public int CheckoutsPerHourPerCustomer { get; set; } = 10;
    /// <summary>Open checkouts re-checked per job run.</summary>
    public int MaxCheckoutRechecksPerRun { get; set; } = 50;
    /// <summary>Time budget of each provider re-check step of the job (checkouts, unmatched events, failed checkouts).</summary>
    public int ReconcileBudgetSeconds { get; set; } = 60;
    /// <summary>Pending checkouts are re-checked with the provider once they are this old (covers a closed browser tab).</summary>
    public int CheckoutRecheckAfterMinutes { get; set; } = 10;
    /// <summary>Webhook events older than this (by their event time) are recorded but not applied.</summary>
    public int WebhookMaxAgeHours { get; set; } = 72;
    /// <summary>Unconfirmed sign-ups are deleted after this many days (the confirmation token lifetime is one day).</summary>
    public int UnconfirmedAccountDays { get; set; } = 2;
    /// <summary>Free trials per company e-mail domain (public mail providers excepted). 0 = unlimited.</summary>
    public int MaxTrialsPerDomain { get; set; } = 3;
    /// <summary>Mail providers whose domain is shared by unrelated people: only the per-address trial limit applies.</summary>
    public string[] PublicEmailDomains { get; set; } =
    [
        "gmail.com", "googlemail.com", "outlook.com", "outlook.com.tr", "hotmail.com", "hotmail.com.tr", "hotmail.co.uk", "live.com",
        "live.co.uk", "windowslive.com", "msn.com", "yahoo.com", "yahoo.com.tr", "yahoo.co.uk", "ymail.com", "icloud.com", "me.com",
        "mac.com", "yandex.com", "yandex.com.tr", "yandex.ru", "proton.me", "protonmail.com", "pm.me", "gmx.com", "gmx.net", "gmx.de",
        "web.de", "mail.com", "aol.com", "mynet.com", "mail.ru", "zoho.com", "tutanota.com", "fastmail.com", "hey.com", "qq.com"
    ];
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
    /// <summary>
    /// iyzico error codes of a checkout-result retrieval that mean "not paid / token not found" and close the checkout as
    /// failed. Empty by default: the codes are not documented for the subscription checkout form (TODO-verify in the
    /// sandbox); until then an unpaid checkout is closed only at <see cref="BillingOptions.CheckoutTimeoutMinutes"/> and
    /// still re-checked for 30 days.
    /// </summary>
    public string[] DefinitiveCheckoutErrorCodes { get; set; } = [];

    public bool IsConfigured => ApiKey.Length > 0 && SecretKey.Length > 0;
}
