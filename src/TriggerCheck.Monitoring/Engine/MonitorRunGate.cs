using System.Collections.Concurrent;

namespace TriggerCheck.Monitoring.Engine;

/// <summary>
/// Per-monitor async lock (singleton). Serializes runs of the same monitor in this process so that a UI
/// "check now" and a scheduled check cannot interleave their read-modify-write of the monitor state
/// (which could otherwise open two incidents or send duplicate alerts).
/// </summary>
public sealed class MonitorRunGate
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(int monitorId, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(monitorId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) sem.Release();
        }
    }
}
