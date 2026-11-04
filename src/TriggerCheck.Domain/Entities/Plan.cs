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
    /// <summary>Offered on the self-service sign-up and billing pages (false = sales-led, e.g. Enterprise).</summary>
    public bool AllowsSelfSignup { get; set; }
    /// <summary>iyzico subscription product reference code (informational; the pricing plan code is what checkout uses).</summary>
    public string? IyzicoProductReferenceCode { get; set; }
    /// <summary>iyzico monthly pricing plan reference code. Paid self-service checkout is only possible when it is set.</summary>
    public string? IyzicoPricingPlanReferenceCode { get; set; }

    public ICollection<Customer> Customers { get; set; } = new List<Customer>();
}
