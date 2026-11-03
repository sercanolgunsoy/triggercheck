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
}
