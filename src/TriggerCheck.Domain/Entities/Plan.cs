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
    /// <summary>Customers on this plan may use SMS and voice call alert channels (Twilio).</summary>
    public bool AllowsSmsVoice { get; set; }

    // ---- AI (AI-10): monthly budget and the AI features included in the plan ----
    /// <summary>Comma separated <see cref="TriggerCheck.Domain.AiFeatures"/> names included in the plan (empty = no AI).</summary>
    public string AiFeatures { get; set; } = "";
    /// <summary>Maximum AI calls per customer per calendar month (UTC). 0 = no AI calls.</summary>
    public int AiMonthlyCallLimit { get; set; }
    /// <summary>Maximum AI tokens (input + output) per customer per calendar month (UTC). 0 = no limit besides the call limit.</summary>
    public long AiMonthlyTokenLimit { get; set; }

    public ICollection<Customer> Customers { get; set; } = new List<Customer>();
}
