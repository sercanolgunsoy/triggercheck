namespace TriggerCheck.Monitoring.Alerting;

/// <summary>Alert delivery (<c>Alerting</c> config section). See <see cref="AlertSenderService"/>.</summary>
public class AlertingOptions
{
    public const string Section = "Alerting";

    /// <summary>Alerts queued or in delivery (incl. retry waits). When reached, new alerts are dropped and logged as errors.</summary>
    public int QueueCapacity { get; set; } = 1000;
    /// <summary>Same as <see cref="QueueCapacity"/> but per customer, so one tenant cannot fill the queue for everybody.</summary>
    public int QueueCapacityPerCustomer { get; set; } = 100;
    /// <summary>Delivery attempts running at the same time, over all customers. Retry waits do not hold a slot.</summary>
    public int MaxConcurrency { get; set; } = 10;
    /// <summary>Delivery attempts of one customer running at the same time (keeps one tenant's broken endpoint from taking every slot).</summary>
    public int MaxConcurrencyPerCustomer { get; set; } = 3;
    /// <summary>SMS + voice call deliveries per customer per UTC day; beyond it the alert is recorded as failed and not sent.</summary>
    public int SmsVoiceDailyLimitPerCustomer { get; set; } = 100;
    /// <summary>Upper bound for one delivery attempt on any channel (SMTP has its own <see cref="SmtpOptions.TimeoutSeconds"/>).</summary>
    public int SendTimeoutSeconds { get; set; } = 30;
    /// <summary>On shutdown, queued alerts get one attempt each (no retries) for at most this long.</summary>
    public int ShutdownDrainSeconds { get; set; } = 10;

    /// <summary>Waits before the 2nd, 3rd and 4th attempt of a transient failure (so at most 4 attempts).</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
    public static int MaxAttempts => RetryDelays.Length + 1;
}

/// <summary>Telegram Bot API (<c>Telegram</c> config section). The bot token itself is stored per channel.</summary>
public class TelegramOptions
{
    public const string Section = "Telegram";
    /// <summary>Overridable for tests only.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";
}

/// <summary>
/// Operator notes: restrict Twilio "Geo Permissions" (Messaging and Voice) to the countries you actually serve, so a
/// customer cannot run up costs by alerting premium / far-away numbers; the per-customer daily limit
/// (<see cref="AlertingOptions.SmsVoiceDailyLimitPerCustomer"/>) and the test-button limit are a second line of defence.
/// Twilio account for SMS and voice call channels (<c>Twilio</c> config section). Operator-level: set it via user secrets
/// or environment variables (Twilio__AccountSid, Twilio__AuthToken, Twilio__FromNumber), never in committed config.
/// When it is not configured the SMS / voice channel types are hidden in the UI.
/// </summary>
public class TwilioOptions
{
    public const string Section = "Twilio";
    public string AccountSid { get; set; } = "";
    public string AuthToken { get; set; } = "";
    /// <summary>Twilio phone number in E.164 format (+15551234567) used as sender / caller id.</summary>
    public string FromNumber { get; set; } = "";
    /// <summary>Overridable for tests only.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.twilio.com";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountSid) && !string.IsNullOrWhiteSpace(AuthToken) && !string.IsNullOrWhiteSpace(FromNumber);
}
