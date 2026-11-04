namespace TriggerCheck.Monitoring.Engine;

/// <summary>
/// Per-monitor async lock (singleton). Serializes runs of the same monitor in this process so that a UI
/// "check now" and a scheduled check cannot interleave their read-modify-write of the monitor state
/// (which could otherwise open two incidents or send duplicate alerts).
/// Entries are reference counted and removed as soon as nobody holds or waits for them, so the gate only keeps
/// semaphores for monitors that are currently being checked (deleted monitors do not accumulate).
/// </summary>
public sealed class MonitorRunGate
{
    private readonly Dictionary<int, Entry> _locks = new();

    /// <summary>Number of monitors that currently have a held or awaited lock.</summary>
    public int Count
    {
        get { lock (_locks) return _locks.Count; }
    }

    public async Task<IDisposable> AcquireAsync(int monitorId, CancellationToken ct)
    {
        Entry entry;
        lock (_locks)
        {
            if (!_locks.TryGetValue(monitorId, out entry!))
                _locks[monitorId] = entry = new Entry();
            entry.RefCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Unref(monitorId, entry);
            throw;
        }
        return new Releaser(this, monitorId, entry);
    }

    private void Unref(int monitorId, Entry entry)
    {
        lock (_locks)
        {
            if (--entry.RefCount == 0)
            {
                _locks.Remove(monitorId);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private sealed class Releaser(MonitorRunGate gate, int monitorId, Entry entry) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            entry.Semaphore.Release();
            gate.Unref(monitorId, entry);
        }
    }
}
