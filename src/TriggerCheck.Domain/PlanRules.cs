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

    /// <summary>
    /// Heartbeat (push) monitors: expected ping intervals. Cron jobs run hourly or daily, so these go beyond
    /// <see cref="SupportedIntervals"/>; the plan minimum still applies and nothing below 60 s is offered.
    /// </summary>
    public static readonly int[] HeartbeatIntervals = [60, 300, 900, 1800, 3600, 21600, 43200, 86400];

    public const int MinHeartbeatIntervalSeconds = 60;

    public static IReadOnlyList<int> AllowedHeartbeatIntervals(Plan plan) =>
        HeartbeatIntervals.Where(i => i >= Math.Max(MinHeartbeatIntervalSeconds, plan.MinIntervalSeconds)).ToList();

    public static bool IsHeartbeatIntervalAllowed(Plan plan, int intervalSeconds) =>
        AllowedHeartbeatIntervals(plan).Contains(intervalSeconds);

    public static bool CanAddMonitor(Plan plan, int currentMonitorCount) => currentMonitorCount < plan.MaxMonitors;

    /// <summary>When a customer is moved to a slower plan, monitors faster than the plan allows are slowed down to the plan minimum.</summary>
    public static int ClampInterval(Plan plan, int intervalSeconds) => Math.Max(intervalSeconds, plan.MinIntervalSeconds);

    /// <summary>Neutral English form for logs; the UI uses the localized TriggerCheck.Web.Localization.Fmt.Interval.</summary>
    public static string FormatInterval(int seconds) => seconds >= 60 && seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} sec";
}

/// <summary>
/// FEAT-7: the Twilio alert channels a plan includes (<see cref="Plan.AllowsSms"/>, <see cref="Plan.AllowsVoiceCall"/>).
/// Every other channel type is included in every plan.
/// </summary>
public readonly record struct SmsVoiceAccess(bool Sms, bool Voice)
{
    public static SmsVoiceAccess Of(Plan plan) => new(plan.AllowsSms, plan.AllowsVoiceCall);

    public bool Any => Sms || Voice;

    public bool Allows(AlertChannelType type) => type switch
    {
        AlertChannelType.Sms => Sms,
        AlertChannelType.VoiceCall => Voice,
        _ => true,
    };
}
