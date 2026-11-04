namespace TriggerCheck.Domain.Entities;

/// <summary>Where Down/Up notifications of a customer's monitors are sent.</summary>
public class AlertChannel
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Name { get; set; } = "";
    public AlertChannelType Type { get; set; }
    /// <summary>Email address, webhook URL (Webhook/Slack/Teams), Telegram chat id or E.164 phone number (SMS/voice).</summary>
    public string Target { get; set; } = "";
    /// <summary>Channel credential that is never shown or logged (Telegram bot token). Null for other types.</summary>
    public string? Secret { get; set; }
    public bool IsEnabled { get; set; } = true;
}
