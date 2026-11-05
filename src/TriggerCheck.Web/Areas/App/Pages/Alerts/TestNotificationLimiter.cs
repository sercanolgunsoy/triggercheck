namespace TriggerCheck.Web.Areas.App.Pages.Alerts;

/// <summary>
/// Light rate limit for "Send test notification": one test per channel every <see cref="ChannelCooldownSeconds"/>
/// and at most <see cref="PerCustomerPerMinute"/> tests per customer in any rolling minute; SMS / voice tests (they cost
/// money) additionally at most <see cref="SmsVoicePerCustomerPerHour"/> per customer in any rolling hour.
/// State lives in memory and is per instance (the app runs as a single instance).
/// </summary>
public sealed class TestNotificationLimiter(TimeProvider time)
{
    public const int ChannelCooldownSeconds = 10;
    public const int PerCustomerPerMinute = 5;
    public const int SmsVoicePerCustomerPerHour = 3;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private readonly Dictionary<int, Queue<DateTime>> _smsVoiceByCustomer = new();
    private static readonly TimeSpan ChannelCooldown = TimeSpan.FromSeconds(ChannelCooldownSeconds);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly Dictionary<int, DateTime> _lastByChannel = new();
    private readonly Dictionary<int, Queue<DateTime>> _recentByCustomer = new();

    /// <summary>Records the test when allowed; otherwise returns the whole seconds to wait (at least 1).</summary>
    public bool TryAcquire(int customerId, int channelId, bool smsOrVoice, out int retryAfterSeconds)
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            retryAfterSeconds = 0;
            if (_lastByChannel.TryGetValue(channelId, out var last) && now - last < ChannelCooldown)
                return Deny(last + ChannelCooldown - now, out retryAfterSeconds);

            if (!_recentByCustomer.TryGetValue(customerId, out var recent))
                _recentByCustomer[customerId] = recent = new Queue<DateTime>();
            while (recent.Count > 0 && now - recent.Peek() >= Window) recent.Dequeue();
            if (recent.Count >= PerCustomerPerMinute)
                return Deny(recent.Peek() + Window - now, out retryAfterSeconds);

            Queue<DateTime>? paid = null;
            if (smsOrVoice)
            {
                if (!_smsVoiceByCustomer.TryGetValue(customerId, out paid))
                    _smsVoiceByCustomer[customerId] = paid = new Queue<DateTime>();
                while (paid.Count > 0 && now - paid.Peek() >= Hour) paid.Dequeue();
                if (paid.Count >= SmsVoicePerCustomerPerHour)
                    return Deny(paid.Peek() + Hour - now, out retryAfterSeconds);
            }

            paid?.Enqueue(now);
            recent.Enqueue(now);
            _lastByChannel[channelId] = now;
            if (_lastByChannel.Count > 1000)
                foreach (var id in _lastByChannel.Where(kv => now - kv.Value >= ChannelCooldown).Select(kv => kv.Key).ToList())
                    _lastByChannel.Remove(id);
            return true;
        }
    }

    private static bool Deny(TimeSpan wait, out int retryAfterSeconds)
    {
        retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        return false;
    }
}
