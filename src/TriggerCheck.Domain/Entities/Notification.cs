namespace TriggerCheck.Domain.Entities;

/// <summary>Log of every alert that was sent (or attempted).</summary>
public class Notification
{
    public long Id { get; set; }
    public int MonitorId { get; set; }
    public int? AlertChannelId { get; set; }
    public DateTime SentAtUtc { get; set; }
    public string Subject { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
    /// <summary>
    /// Delivery attempts made (1 = delivered or failed on the first try; transient failures are retried up to 4 times;
    /// 0 = not attempted, e.g. an SMS channel on a plan without SMS).
    /// </summary>
    public int Attempts { get; set; } = 1;
}
