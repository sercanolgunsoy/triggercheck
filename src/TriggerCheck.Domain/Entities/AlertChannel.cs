namespace TriggerCheck.Domain.Entities;

/// <summary>Where Down/Up notifications of a customer's monitors are sent.</summary>
public class AlertChannel
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Name { get; set; } = "";
    public AlertChannelType Type { get; set; }
    /// <summary>Email address or webhook URL.</summary>
    public string Target { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
}
