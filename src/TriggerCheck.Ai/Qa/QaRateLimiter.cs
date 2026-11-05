using Microsoft.Extensions.Options;

namespace TriggerCheck.Ai.Qa;

/// <summary>
/// Per-user limit of Q&amp;A questions (Ai:Qa:PerUserPerMinute / PerUserPerHour, rolling windows) and the per-instance
/// cap of questions answered at the same time (Ai:Qa:MaxConcurrent). State lives in memory and is per instance, like
/// the other light limiters of the app; the monthly AI budget (AI-10) is the cross-instance cost bound.
/// </summary>
public sealed class QaRateLimiter
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1), Hour = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;
    private readonly AiQaOptions _o;
    private readonly SemaphoreSlim _concurrency;
    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<DateTime>> _byUser = new(StringComparer.Ordinal);

    public QaRateLimiter(TimeProvider time, IOptions<AiOptions> options)
    {
        _time = time;
        _o = options.Value.Qa;
        _concurrency = new SemaphoreSlim(Math.Max(1, _o.MaxConcurrent));
    }

    /// <summary>Records the question when allowed; otherwise returns the whole seconds to wait (at least 1).</summary>
    public bool TryAcquire(string userId, out int retryAfterSeconds)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        retryAfterSeconds = 0;
        lock (_lock)
        {
            if (!_byUser.TryGetValue(userId, out var recent)) _byUser[userId] = recent = new Queue<DateTime>();
            while (recent.Count > 0 && now - recent.Peek() >= Hour) recent.Dequeue();
            if (recent.Count >= Math.Max(1, _o.PerUserPerHour))
                return Deny(recent.Peek() + Hour - now, out retryAfterSeconds);
            var lastMinute = recent.Where(t => now - t < Minute).ToList();
            if (lastMinute.Count >= Math.Max(1, _o.PerUserPerMinute))
                return Deny(lastMinute[0] + Minute - now, out retryAfterSeconds);
            recent.Enqueue(now);
            if (_byUser.Count > 10_000)
                foreach (var key in _byUser.Where(kv => kv.Value.All(t => now - t >= Hour)).Select(kv => kv.Key).ToList())
                    _byUser.Remove(key);
            return true;
        }
    }

    /// <summary>A slot for answering one question, or null when Ai:Qa:MaxConcurrent questions are already running.</summary>
    public IDisposable? TryEnter() => _concurrency.Wait(0) ? new Slot(_concurrency) : null;

    private static bool Deny(TimeSpan wait, out int retryAfterSeconds)
    {
        retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        return false;
    }

    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
        }
    }
}
