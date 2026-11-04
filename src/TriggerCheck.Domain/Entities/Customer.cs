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

    // ---- AI ----
    /// <summary>
    /// Customer opt-in for AI features (only masked technical data is sent). Existing customers default to off (opt-in);
    /// every creation path for new customers sets it to true.
    /// </summary>
    public bool AiEnabled { get; set; }

    public ICollection<Monitor> Monitors { get; set; } = new List<Monitor>();
    public ICollection<AlertChannel> AlertChannels { get; set; } = new List<AlertChannel>();
}
