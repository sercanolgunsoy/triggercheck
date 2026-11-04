namespace TriggerCheck.Domain.Entities;

/// <summary>
/// SMS + voice call deliveries of a customer on one UTC day, for the daily cost cap. Kept separately from Notifications
/// so deleting monitors or channels does not reset it. Key: (CustomerId, DayUtc).
/// </summary>
public class SmsVoiceUsage
{
    public int CustomerId { get; set; }
    /// <summary>UTC date (time part 00:00).</summary>
    public DateTime DayUtc { get; set; }
    public int Count { get; set; }
}
