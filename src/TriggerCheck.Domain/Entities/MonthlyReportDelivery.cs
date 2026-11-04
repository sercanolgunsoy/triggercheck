namespace TriggerCheck.Domain.Entities;

/// <summary>Monthly SLA report e-mail already sent for a customer and month (FEAT-10, idempotency).</summary>
public class MonthlyReportDelivery
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    /// <summary>"yyyy-MM" of the reported month.</summary>
    public string Month { get; set; } = "";
    public DateTime SentAtUtc { get; set; }
}
