using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Ai.Insights;

/// <summary>
/// "Ai:Insights": AI-12 SuperAdmin platform insights. The signals are computed by <see cref="InsightRules"/> (no LLM,
/// independent of Ai:Enabled); the optional AI summary only describes them (see <see cref="InsightSummaryService"/>).
/// </summary>
public sealed class AiInsightOptions
{
    // ---- noise ----
    /// <summary>History window of the noise signal (incidents started, alerts sent).</summary>
    public int WindowDays { get; set; } = 7;
    /// <summary>A customer is "noisy" with at least this many incidents in the window ...</summary>
    public int NoiseMinIncidents { get; set; } = 10;
    /// <summary>... or at least this many alert notifications in the window.</summary>
    public int NoiseMinAlerts { get; set; } = 30;
    /// <summary>An incident resolved within this many minutes of its start counts as a flap.</summary>
    public int FlapMaxMinutes { get; set; } = 10;

    // ---- plan-limit pressure ----
    /// <summary>A limit is under pressure when usage is at least this percent of it (monitors, status pages, regions, SMS / voice per day, AI quota).</summary>
    public int LimitPressurePercent { get; set; } = 80;
    /// <summary>Fastest interval: at least this percent of the customer's checked monitors run at the plan's minimum interval ...</summary>
    public int FastestIntervalSharePercent { get; set; } = 50;
    /// <summary>... and at least this many of them (one monitor at the minimum is no signal).</summary>
    public int FastestIntervalMinMonitors { get; set; } = 2;

    // ---- abandoned ----
    /// <summary>A monitor DOWN without interruption for at least this many days is an abandoned-account signal; acknowledgements are counted over the same period.</summary>
    public int AbandonedDownDays { get; set; } = 7;

    /// <summary>Rows per table on the page (the most significant first).</summary>
    public int MaxRows { get; set; } = 25;

    // ---- AI summary (platform budget, not a customer's plan quota) ----
    /// <summary>The AI summary can be generated (also needs Ai:Enabled and a key).</summary>
    public bool SummaryEnabled { get; set; } = true;
    /// <summary>Summaries per calendar month (UTC) that may reach the model, platform-wide. 0 = none.</summary>
    public int MonthlyCallLimit { get; set; } = 60;
    /// <summary>Budget tokens per calendar month (as AI-10 counts them); 0 = only the call limit.</summary>
    public long MonthlyTokenLimit { get; set; } = 2_000_000;
    /// <summary>At most one new model call per this many minutes (an unchanged signal set reuses the last summary for free).</summary>
    public int MinMinutesBetweenSummaries { get; set; } = 15;
    /// <summary>Customers per signal that are sent to the model (the most significant first).</summary>
    public int MaxSummaryCustomers { get; set; } = 10;
}

// ---- rule input (loaded by InsightDataLoader; plain records so the rules are testable without a database) ----

public sealed record InsightCustomer(int Id, string Name, string PlanName, int MaxMonitors, int MinIntervalSeconds, int? MaxStatusPages,
    int MaxRegions, bool AllowsSmsVoice);

/// <param name="Regions">Regions the monitor is checked from, including the central one.</param>
public sealed record InsightMonitor(int Id, int CustomerId, string Name, MonitorType Type, bool IsEnabled, MonitorStatus Status,
    DateTime? StatusChangedAtUtc, int IntervalSeconds, int Regions = 1);

public sealed record InsightIncident(int MonitorId, DateTime StartedAtUtc, DateTime? ResolvedAtUtc, DateTime? AcknowledgedAtUtc);

/// <param name="Calls">AI calls this month (AI-10 counting).</param>
/// <param name="PercentUsed">Highest of call and token usage, 0-100.</param>
public sealed record InsightAiUsage(int Calls, int CallLimit, double PercentUsed);

/// <summary>Everything the rules look at. Only active customers and their monitors are included.</summary>
/// <param name="Alerts">Alert notifications sent in the noise window, by monitor id.</param>
/// <param name="StatusPages">Status pages by customer id.</param>
/// <param name="SmsPeakDay">Highest SMS / voice deliveries on one UTC day in the noise window, by customer id.</param>
/// <param name="SmsDailyLimit">Alerting:SmsVoiceDailyLimitPerCustomer (0 = no limit).</param>
public sealed record InsightInput(
    IReadOnlyList<InsightCustomer> Customers,
    IReadOnlyList<InsightMonitor> Monitors,
    IReadOnlyList<InsightIncident> Incidents,
    IReadOnlyDictionary<int, int> Alerts,
    IReadOnlyDictionary<int, int> StatusPages,
    IReadOnlyDictionary<int, int> SmsPeakDay,
    int SmsDailyLimit,
    IReadOnlyDictionary<int, InsightAiUsage> AiUsage);

// ---- signals (every number is evidence computed here) ----

public sealed record InsightMonitorRef(int Id, string Name, int Incidents);

/// <summary>Noisiest customers: incidents, flaps (incidents resolved within FlapMaxMinutes) and alerts over the window.</summary>
public sealed record NoiseSignal(int CustomerId, string CustomerName, string PlanName, int Monitors, int Incidents, int Flaps, int Alerts,
    double IncidentsPerMonitor, double FlapSharePercent, InsightMonitorRef? NoisiestMonitor);

public enum PlanLimitKind
{
    Monitors,
    /// <summary>Share of checked monitors at the plan's minimum interval (a faster plan exists).</summary>
    FastestInterval,
    StatusPages,
    /// <summary>Most regions one monitor uses, including the central one.</summary>
    Regions,
    /// <summary>Busiest UTC day of SMS / voice deliveries in the window against the daily limit.</summary>
    SmsVoice,
    AiQuota,
}

public sealed record PlanLimitUsage(PlanLimitKind Kind, double Used, double Limit, double Percent);

/// <summary>Upgrade signal: limits of the customer's plan that are at or above the pressure threshold.</summary>
public sealed record PressureSignal(int CustomerId, string CustomerName, string PlanName, IReadOnlyList<PlanLimitUsage> Limits)
{
    public double MaxPercent => Limits.Count == 0 ? 0 : Limits.Max(l => l.Percent);
}

public sealed record DownMonitor(int Id, string Name, DateTime DownSinceUtc, int DownDays);

/// <summary>Abandoned-account signal: monitors DOWN for at least AbandonedDownDays, with the acknowledgements in that period.</summary>
/// <param name="AllDown">Every enabled monitor of the customer is DOWN (strongest form of the signal).</param>
public sealed record AbandonedSignal(int CustomerId, string CustomerName, string PlanName, IReadOnlyList<DownMonitor> DownMonitors, int EnabledMonitors,
    int AcknowledgementsInPeriod, bool AllDown)
{
    public int LongestDownDays => DownMonitors.Count == 0 ? 0 : DownMonitors.Max(m => m.DownDays);
}

public sealed record InsightThresholds(int WindowDays, int NoiseMinIncidents, int NoiseMinAlerts, int FlapMaxMinutes, int LimitPressurePercent,
    int FastestIntervalSharePercent, int FastestIntervalMinMonitors, int AbandonedDownDays);

/// <summary>Result of one evaluation; each list is ordered most significant first and capped at MaxRows.</summary>
public sealed record InsightReport(DateTime ComputedAtUtc, InsightThresholds Thresholds, IReadOnlyList<NoiseSignal> Noise,
    IReadOnlyList<PressureSignal> Pressure, IReadOnlyList<AbandonedSignal> Abandoned)
{
    public bool IsEmpty => Noise.Count == 0 && Pressure.Count == 0 && Abandoned.Count == 0;
}

/// <summary>
/// AI-12 L1 rules: pure functions of the loaded platform data (no LLM, no I/O). The thresholds are
/// <see cref="AiInsightOptions"/> (Ai:Insights:*):
/// <list type="bullet">
/// <item><b>Noise</b>: a customer with at least <see cref="AiInsightOptions.NoiseMinIncidents"/> incidents started, or
/// <see cref="AiInsightOptions.NoiseMinAlerts"/> alert notifications sent, in the last
/// <see cref="AiInsightOptions.WindowDays"/> days. Flaps are incidents resolved within
/// <see cref="AiInsightOptions.FlapMaxMinutes"/>. Ranked by incidents, then alerts, then flaps.</item>
/// <item><b>Plan-limit pressure</b> (upgrade signal): usage ≥ <see cref="AiInsightOptions.LimitPressurePercent"/> % of a
/// limit: monitors vs MaxMonitors, status pages vs MaxStatusPages (when limited), the most regions one monitor uses vs
/// MaxRegions (when above 1), the busiest SMS / voice day of the window vs the daily limit (plans with SMS), the AI
/// quota (AI-10 percent, calls and tokens). Fastest interval: at least
/// <see cref="AiInsightOptions.FastestIntervalMinMonitors"/> and <see cref="AiInsightOptions.FastestIntervalSharePercent"/> %
/// of the enabled, non-heartbeat monitors run at the plan's minimum interval while a faster interval exists.
/// Ranked by the highest percent.</item>
/// <item><b>Abandoned</b>: enabled monitors whose status has been DOWN without change for at least
/// <see cref="AiInsightOptions.AbandonedDownDays"/> days, grouped by customer, with the number of incidents the
/// customer acknowledged in that period (TriggerCheck keeps no login history, so acknowledgements are the activity
/// evidence). Customers whose every enabled monitor is DOWN come first, then the longest outage.</item>
/// </list>
/// Suspended customers are not loaded (their monitors are not checked).
/// </summary>
public static class InsightRules
{
    public static InsightThresholds Thresholds(AiInsightOptions o) => new(Math.Max(1, o.WindowDays), Math.Max(1, o.NoiseMinIncidents),
        Math.Max(1, o.NoiseMinAlerts), Math.Max(0, o.FlapMaxMinutes), Math.Clamp(o.LimitPressurePercent, 1, 100),
        Math.Clamp(o.FastestIntervalSharePercent, 1, 100), Math.Max(1, o.FastestIntervalMinMonitors), Math.Max(1, o.AbandonedDownDays));

    public static InsightReport Compute(InsightInput input, AiInsightOptions options, DateTime nowUtc)
    {
        var t = Thresholds(options);
        var rows = Math.Max(1, options.MaxRows);
        var monitorsByCustomer = input.Monitors.GroupBy(m => m.CustomerId).ToDictionary(g => g.Key, g => g.ToList());
        var customerOf = input.Monitors.ToDictionary(m => m.Id, m => m.CustomerId);
        var noise = Noise(input, t, nowUtc, monitorsByCustomer).Take(rows).ToList();
        var pressure = Pressure(input, t, monitorsByCustomer).Take(rows).ToList();
        var abandoned = Abandoned(input, t, nowUtc, monitorsByCustomer, customerOf).Take(rows).ToList();
        return new InsightReport(nowUtc, t, noise, pressure, abandoned);
    }

    public static bool IsFlap(InsightIncident i, int flapMaxMinutes) =>
        i.ResolvedAtUtc is DateTime r && r >= i.StartedAtUtc && r - i.StartedAtUtc <= TimeSpan.FromMinutes(flapMaxMinutes);

    private static IEnumerable<NoiseSignal> Noise(InsightInput input, InsightThresholds t, DateTime now, Dictionary<int, List<InsightMonitor>> monitors)
    {
        var start = now.AddDays(-t.WindowDays);
        var incidents = input.Incidents.Where(i => i.StartedAtUtc >= start && i.StartedAtUtc <= now).GroupBy(i => i.MonitorId)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Flaps: g.Count(i => IsFlap(i, t.FlapMaxMinutes))));
        var result = new List<NoiseSignal>();
        foreach (var c in input.Customers)
        {
            var mine = monitors.GetValueOrDefault(c.Id) ?? [];
            int count = 0, flaps = 0, alerts = 0;
            InsightMonitorRef? top = null;
            foreach (var m in mine)
            {
                var (n, f) = incidents.GetValueOrDefault(m.Id);
                count += n;
                flaps += f;
                alerts += input.Alerts.GetValueOrDefault(m.Id);
                if (n > 0 && (top is null || n > top.Incidents || (n == top.Incidents && m.Id < top.Id))) top = new InsightMonitorRef(m.Id, m.Name, n);
            }
            if (count < t.NoiseMinIncidents && alerts < t.NoiseMinAlerts) continue;
            var enabled = mine.Count(m => m.IsEnabled);
            result.Add(new NoiseSignal(c.Id, c.Name, c.PlanName, enabled, count, flaps, alerts,
                Math.Round((double)count / Math.Max(1, enabled), 1), count == 0 ? 0 : Math.Round(100.0 * flaps / count), top));
        }
        return result.OrderByDescending(s => s.Incidents).ThenByDescending(s => s.Alerts).ThenByDescending(s => s.Flaps).ThenBy(s => s.CustomerId);
    }

    /// <summary>True when <paramref name="used"/> is at least <paramref name="percent"/> % of <paramref name="limit"/> (exact, no rounding).</summary>
    public static bool AtLeast(double used, double limit, int percent) => limit > 0 && used * 100 >= percent * limit;

    private static double Pct(double used, double limit) => limit <= 0 ? 0 : Math.Round(100.0 * used / limit, 1);

    private static IEnumerable<PressureSignal> Pressure(InsightInput input, InsightThresholds t, Dictionary<int, List<InsightMonitor>> monitors)
    {
        var fastestSupported = PlanRules.SupportedIntervals.Min();
        var result = new List<PressureSignal>();
        foreach (var c in input.Customers)
        {
            var mine = monitors.GetValueOrDefault(c.Id) ?? [];
            var limits = new List<PlanLimitUsage>();
            void Check(PlanLimitKind kind, double used, double limit)
            {
                if (AtLeast(used, limit, t.LimitPressurePercent)) limits.Add(new PlanLimitUsage(kind, used, limit, Pct(used, limit)));
            }

            Check(PlanLimitKind.Monitors, mine.Count, c.MaxMonitors);

            var checkedMonitors = mine.Where(m => m.IsEnabled && m.Type != MonitorType.Heartbeat).ToList();
            var atMinimum = checkedMonitors.Count(m => m.IntervalSeconds <= c.MinIntervalSeconds);
            if (c.MinIntervalSeconds > fastestSupported && atMinimum >= t.FastestIntervalMinMonitors
                && AtLeast(atMinimum, checkedMonitors.Count, t.FastestIntervalSharePercent))
                limits.Add(new PlanLimitUsage(PlanLimitKind.FastestInterval, atMinimum, checkedMonitors.Count, Pct(atMinimum, checkedMonitors.Count)));

            if (c.MaxStatusPages is int pages && pages > 0) Check(PlanLimitKind.StatusPages, input.StatusPages.GetValueOrDefault(c.Id), pages);
            if (c.MaxRegions > 1 && mine.Count > 0) Check(PlanLimitKind.Regions, mine.Max(m => Math.Max(1, m.Regions)), c.MaxRegions);
            if (c.AllowsSmsVoice && input.SmsDailyLimit > 0) Check(PlanLimitKind.SmsVoice, input.SmsPeakDay.GetValueOrDefault(c.Id), input.SmsDailyLimit);
            if (input.AiUsage.TryGetValue(c.Id, out var ai) && ai.CallLimit > 0 && ai.PercentUsed >= t.LimitPressurePercent)
                limits.Add(new PlanLimitUsage(PlanLimitKind.AiQuota, ai.Calls, ai.CallLimit, Math.Round(ai.PercentUsed, 1)));

            if (limits.Count > 0) result.Add(new PressureSignal(c.Id, c.Name, c.PlanName, limits));
        }
        return result.OrderByDescending(s => s.MaxPercent).ThenByDescending(s => s.Limits.Count).ThenBy(s => s.CustomerId);
    }

    private static IEnumerable<AbandonedSignal> Abandoned(InsightInput input, InsightThresholds t, DateTime now, Dictionary<int, List<InsightMonitor>> monitors,
        Dictionary<int, int> customerOf)
    {
        var since = now.AddDays(-t.AbandonedDownDays);
        var acks = input.Incidents.Where(i => i.AcknowledgedAtUtc is DateTime a && a >= since && a <= now && customerOf.ContainsKey(i.MonitorId))
            .GroupBy(i => customerOf[i.MonitorId]).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<AbandonedSignal>();
        foreach (var c in input.Customers)
        {
            var mine = monitors.GetValueOrDefault(c.Id) ?? [];
            var down = mine.Where(m => m.IsEnabled && m.Status == MonitorStatus.Down && m.StatusChangedAtUtc is DateTime at && at <= since)
                .Select(m => new DownMonitor(m.Id, m.Name, m.StatusChangedAtUtc!.Value, (int)Math.Floor((now - m.StatusChangedAtUtc!.Value).TotalDays)))
                .OrderByDescending(m => m.DownDays).ThenBy(m => m.Id).ToList();
            if (down.Count == 0) continue;
            var enabled = mine.Count(m => m.IsEnabled);
            result.Add(new AbandonedSignal(c.Id, c.Name, c.PlanName, down, enabled, acks.GetValueOrDefault(c.Id), down.Count == enabled));
        }
        return result.OrderByDescending(s => s.AllDown).ThenByDescending(s => s.LongestDownDays).ThenByDescending(s => s.DownMonitors.Count).ThenBy(s => s.CustomerId);
    }
}
