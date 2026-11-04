namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Rate limit for "Check now":
/// <list type="bullet">
/// <item>per monitor: no manual check within max(plan minimum interval, 30s) of the previous MANUAL check, and none
/// within <see cref="ScheduledGuardSeconds"/> of any check. Scheduled checks also update LastCheckedAtUtc, so the long
/// cooldown is deliberately not measured against it (that would refuse almost every click);</item>
/// <item>per customer: at most <see cref="PerCustomerPerMinute"/> manual checks in any rolling minute.</item>
/// </list>
/// </summary>
public sealed class ManualCheckLimiter(TimeProvider time)
{
    // NOTE: state lives in memory and is per instance (the app runs as a single instance).

    public const int MinCooldownSeconds = 30;
    public const int ScheduledGuardSeconds = 10;
    public const int PerCustomerPerMinute = 10;
    private static readonly TimeSpan ScheduledGuard = TimeSpan.FromSeconds(ScheduledGuardSeconds);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public enum Denial { None, MonitorCooldown, CustomerQuota }

    public readonly record struct Decision(Denial Denial, TimeSpan RetryAfter)
    {
        public bool Allowed => Denial == Denial.None;
        /// <summary>Whole seconds to wait, at least 1.</summary>
        public int RetryAfterSeconds => Math.Max(1, (int)Math.Ceiling(RetryAfter.TotalSeconds));
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, DateTime> _lastManualByMonitor = new();
    private readonly Dictionary<int, Queue<DateTime>> _recentByCustomer = new();

    /// <summary>Minimum time between two manual checks of the same monitor.</summary>
    public static TimeSpan CooldownFor(int planMinIntervalSeconds) =>
        TimeSpan.FromSeconds(Math.Max(planMinIntervalSeconds, MinCooldownSeconds));

    /// <summary>
    /// Records a manual check when allowed. <paramref name="lastCheckedAtUtc"/> is the monitor's persisted last check
    /// (scheduled or manual); only the short <see cref="ScheduledGuardSeconds"/> guard applies to it.
    /// </summary>
    public Decision TryAcquire(int customerId, int monitorId, DateTime? lastCheckedAtUtc, int planMinIntervalSeconds)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cooldown = CooldownFor(planMinIntervalSeconds);

        lock (_lock)
        {
            if (_lastManualByMonitor.TryGetValue(monitorId, out var manual) && now - manual < cooldown)
                return new Decision(Denial.MonitorCooldown, manual + cooldown - now);
            if (lastCheckedAtUtc is DateTime last && now - last < ScheduledGuard)
                return new Decision(Denial.MonitorCooldown, last + ScheduledGuard - now);

            if (!_recentByCustomer.TryGetValue(customerId, out var recent))
                _recentByCustomer[customerId] = recent = new Queue<DateTime>();
            while (recent.Count > 0 && now - recent.Peek() >= Window) recent.Dequeue();
            if (recent.Count >= PerCustomerPerMinute)
                return new Decision(Denial.CustomerQuota, recent.Peek() + Window - now);

            recent.Enqueue(now);
            _lastManualByMonitor[monitorId] = now;
            PruneMonitors(now);
            return new Decision(Denial.None, TimeSpan.Zero);
        }
    }

    /// <summary>Keeps the map small: entries older than the longest supported cooldown are useless.</summary>
    private void PruneMonitors(DateTime now)
    {
        if (_lastManualByMonitor.Count < 1000) return;
        foreach (var id in _lastManualByMonitor.Where(kv => now - kv.Value > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList())
            _lastManualByMonitor.Remove(id);
    }
}
