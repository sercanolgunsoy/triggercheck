namespace TriggerCheck.Domain.Entities;

/// <summary>Subscription package. Determines the minimum check interval and quotas.</summary>
public class Plan
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Fastest allowed check interval for monitors of customers on this plan (300, 60, 30, 15...).</summary>
    public int MinIntervalSeconds { get; set; }
    public int MaxMonitors { get; set; }
    /// <summary>How many days of CheckResult history are retained.</summary>
    public int RetentionDays { get; set; } = 30;
    public decimal MonthlyPrice { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<Customer> Customers { get; set; } = new List<Customer>();
}
