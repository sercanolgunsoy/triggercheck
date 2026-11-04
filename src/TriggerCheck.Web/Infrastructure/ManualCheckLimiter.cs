namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Rate limit for "Check now" (in-memory, single instance):
/// <list type="bullet">
/// <item>per monitor: no manual check within max(plan minimum interval, 30s) of the monitor's last check
/// (scheduled or manual);</item>
/// <item>per customer: at most <see cref="PerCustomerPerMinute"/> manual checks in any rolling minute.</item>
/// </list>
/// </summary>
public sealed class ManualCheckLimiter(TimeProvider time)
{
    public const int MinCooldownSeconds = 30;
    public const int PerCustomerPerMinute = 10;
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

    public static TimeSpan CooldownFor(int planMinIntervalSeconds) =>
        TimeSpan.FromSeconds(Math.Max(planMinIntervalSeconds, MinCooldownSeconds));

    /// <summary>
    /// Records a manual check when allowed. <paramref name="lastCheckedAtUtc"/> is the monitor's persisted last check;
    /// an in-memory reservation also covers concurrent clicks before that value is saved.
    /// <paramref name="bypassCustomerQuota"/> is for platform operators.
    /// </summary>
    public Decision TryAcquire(int customerId, int monitorId, DateTime? lastCheckedAtUtc, int planMinIntervalSeconds, bool bypassCustomerQuota = false)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cooldown = CooldownFor(planMinIntervalSeconds);

        lock (_lock)
        {
            var last = lastCheckedAtUtc;
            if (_lastManualByMonitor.TryGetValue(monitorId, out var manual) && (last is null || manual > last)) last = manual;
            if (last is DateTime l && now - l < cooldown)
                return new Decision(Denial.MonitorCooldown, l + cooldown - now);

            if (!_recentByCustomer.TryGetValue(customerId, out var recent))
                _recentByCustomer[customerId] = recent = new Queue<DateTime>();
            while (recent.Count > 0 && now - recent.Peek() >= Window) recent.Dequeue();
            if (!bypassCustomerQuota && recent.Count >= PerCustomerPerMinute)
                return new Decision(Denial.CustomerQuota, recent.Peek() + Window - now);

            recent.Enqueue(now);
            _lastManualByMonitor[monitorId] = now;
            PruneMonitors(now);
            return new Decision(Denial.None, TimeSpan.Zero);
        }
    }

    /// <summary>Keeps the reservation map small: entries older than the longest supported cooldown are useless.</summary>
    private void PruneMonitors(DateTime now)
    {
        if (_lastManualByMonitor.Count < 1000) return;
        foreach (var id in _lastManualByMonitor.Where(kv => now - kv.Value > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList())
            _lastManualByMonitor.Remove(id);
    }
}
