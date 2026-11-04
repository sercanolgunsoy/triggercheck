namespace TriggerCheck.Domain.Entities;

/// <summary>Tenant. Every monitor/alert channel/user (except SuperAdmins) belongs to exactly one customer.</summary>
public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public int PlanId { get; set; }
    public Plan Plan { get; set; } = null!;
    /// <summary>Suspended customers cannot log in and their monitors are not checked.</summary>
    public bool IsActive { get; set; } = true;
    /// <summary>
    /// What happens to an alert when the customer has no enabled alert channel: true (default) = e-mail it to
    /// <see cref="ContactEmail"/> so outages are never silently missed; false = do not send it.
    /// </summary>
    public bool AlertContactEmailWhenNoChannel { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Self-service billing state (BIZ-1/BIZ-2). <see cref="BillingStatus.None"/> = plan managed by the operator or free plan.</summary>
    public BillingStatus BillingStatus { get; set; } = BillingStatus.None;
    /// <summary>End of the free trial (set at sign-up). When it passes without a subscription the customer moves to the fallback plan.</summary>
    public DateTime? TrialEndsAtUtc { get; set; }
    /// <summary>When the "trial ends soon" e-mail was sent (sent once).</summary>
    public DateTime? TrialReminderSentAtUtc { get; set; }
    /// <summary>Normalized sign-up address (lower case, no +tag) used to limit free trials per address and domain.</summary>
    public string? TrialKey { get; set; }
    /// <summary>
    /// Self-service sign-up still in its initial state: set at sign-up, cleared when the address is confirmed or a
    /// SuperAdmin changes the customer. Only such customers are purged when the address is never confirmed.
    /// </summary>
    public bool SignupPending { get; set; }

    // ---- AI ----
    /// <summary>
    /// Customer opt-in for AI features (only masked technical data is sent). Existing customers default to off (opt-in);
    /// every creation path for new customers sets it to true.
    /// </summary>
    public bool AiEnabled { get; set; }

    // ---- SLA reports (FEAT-10) ----
    /// <summary>Opt-in: e-mail the monthly SLA report to the customer admins on the 1st of the month.</summary>
    public bool MonthlyReportEmail { get; set; }
    /// <summary>IANA time zone the report months are cut in (null = UTC).</summary>
    public string? ReportTimeZoneId { get; set; }

    public ICollection<Monitor> Monitors { get; set; } = new List<Monitor>();
    public ICollection<AlertChannel> AlertChannels { get; set; } = new List<AlertChannel>();
    public ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
}
