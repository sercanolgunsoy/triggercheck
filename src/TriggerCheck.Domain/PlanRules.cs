using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Domain;

/// <summary>Business rules tying monitors to the customer's plan. Use these everywhere instead of re-implementing.</summary>
public static class PlanRules
{
    /// <summary>All intervals the product offers, fastest last.</summary>
    public static readonly int[] SupportedIntervals = [300, 60, 30, 15];

    /// <summary>Intervals a customer on <paramref name="plan"/> may choose.</summary>
    public static IReadOnlyList<int> AllowedIntervals(Plan plan) =>
        SupportedIntervals.Where(i => i >= plan.MinIntervalSeconds).ToList();

    public static bool IsIntervalAllowed(Plan plan, int intervalSeconds) =>
        SupportedIntervals.Contains(intervalSeconds) && intervalSeconds >= plan.MinIntervalSeconds;

    public static bool CanAddMonitor(Plan plan, int currentMonitorCount) => currentMonitorCount < plan.MaxMonitors;

    /// <summary>When a customer is moved to a slower plan, monitors faster than the plan allows are slowed down to the plan minimum.</summary>
    public static int ClampInterval(Plan plan, int intervalSeconds) => Math.Max(intervalSeconds, plan.MinIntervalSeconds);

    public static string FormatInterval(int seconds) => seconds >= 60 ? $"{seconds / 60} dk" : $"{seconds} sn";
}
