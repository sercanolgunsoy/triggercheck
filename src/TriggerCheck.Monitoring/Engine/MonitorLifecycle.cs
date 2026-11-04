using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Engine;

/// <summary>Outcome of a pause/resume request: the monitor's name and whether anything changed.</summary>
public sealed record MonitorLifecycleResult(int MonitorId, string Name, bool Changed);

/// <summary>
/// The single implementation of pause / resume, used by the customer panel and the SuperAdmin panel.
/// <para>Incident semantics (no incident is ever left open on a monitor that is not being observed):</para>
/// <list type="bullet">
/// <item><b>Pause</b>: Status=Paused, IsEnabled=false. Any open incident is closed at the pause time (a paused
/// monitor is not observed, so the downtime cannot be measured further). If the monitor was Down,
/// <see cref="MonitorEntity.DownWhenPaused"/> is set.</item>
/// <item><b>Resume</b>: Status=Unknown, IsEnabled=true, failure streak reset, next check scheduled immediately.
/// Open incidents left over from older versions are closed defensively (and count as "was down").</item>
/// <item>First check after resume: Up and the monitor was paused while Down -&gt; UP alert (Recovered);
/// Up otherwise -&gt; plain BecameUp; Down after the failure threshold -&gt; new incident + DOWN alert.</item>
/// </list>
/// Every change bumps <see cref="MonitorEntity.Version"/>: an in-flight check re-reads the row and sees the new
/// Status/IsEnabled, so it only records its probe. Conversely, if a check changed the status (incident opened or
/// closed) between our read and our save, the save fails on the token and the change is retried on fresh state.
/// Customer suspension is separate: it does not touch the monitors (Status stays as is); the scheduler skips them
/// and the runner treats them as not actively monitored, so no transition happens while a customer is suspended.
/// </summary>
public sealed class MonitorLifecycle(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, ILogger<MonitorLifecycle> logger)
{
    private const int MaxAttempts = 5;

    public static bool IsPaused(MonitorEntity m) => m.Status == MonitorStatus.Paused || !m.IsEnabled;

    /// <summary>Pauses the monitor. <paramref name="customerId"/> restricts the lookup to one tenant (null = any, SuperAdmin). Returns null if not found.</summary>
    public Task<MonitorLifecycleResult?> PauseAsync(int monitorId, int? customerId, CancellationToken ct = default) =>
        MutateAsync(monitorId, customerId, PauseCoreAsync, ct);

    /// <summary>Resumes the monitor. <paramref name="customerId"/> restricts the lookup to one tenant (null = any, SuperAdmin). Returns null if not found.</summary>
    public Task<MonitorLifecycleResult?> ResumeAsync(int monitorId, int? customerId, CancellationToken ct = default) =>
        MutateAsync(monitorId, customerId, ResumeCoreAsync, ct);

    private static async Task<bool> PauseCoreAsync(AppDbContext db, MonitorEntity m, DateTime now, CancellationToken ct)
    {
        if (m.Status == MonitorStatus.Paused && !m.IsEnabled) return false;
        var closed = await CloseOpenIncidentsAsync(db, m.Id, now, ct);
        m.DownWhenPaused = m.DownWhenPaused || m.Status == MonitorStatus.Down || closed > 0;
        m.Status = MonitorStatus.Paused;
        m.IsEnabled = false;
        m.StatusChangedAtUtc = now;
        return true;
    }

    private static async Task<bool> ResumeCoreAsync(AppDbContext db, MonitorEntity m, DateTime now, CancellationToken ct)
    {
        if (!IsPaused(m)) return false;
        if (await CloseOpenIncidentsAsync(db, m.Id, now, ct) > 0) m.DownWhenPaused = true;
        m.Status = MonitorStatus.Unknown;
        m.IsEnabled = true;
        m.ConsecutiveFailures = 0;
        m.StatusChangedAtUtc = now;
        // A heartbeat gets a full interval + grace for its first ping; pings from before the pause do not count.
        m.NextCheckAtUtc = m.Type == MonitorType.Heartbeat ? Checks.HeartbeatSchedule.FirstDeadline(m, now) : now;
        return true;
    }

    private static async Task<int> CloseOpenIncidentsAsync(AppDbContext db, int monitorId, DateTime now, CancellationToken ct)
    {
        var open = await db.Incidents.Where(i => i.MonitorId == monitorId && i.ResolvedAtUtc == null).ToListAsync(ct);
        foreach (var i in open) i.ResolvedAtUtc = now;
        return open.Count;
    }

    private async Task<MonitorLifecycleResult?> MutateAsync(int monitorId, int? customerId,
        Func<AppDbContext, MonitorEntity, DateTime, CancellationToken, Task<bool>> change, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var q = db.Monitors.Where(m => m.Id == monitorId);
            if (customerId is int cid) q = q.Where(m => m.CustomerId == cid);
            var monitor = await q.FirstOrDefaultAsync(ct);
            if (monitor is null) return null;

            var changed = await change(db, monitor, time.GetUtcNow().UtcDateTime, ct);
            if (!changed) return new(monitor.Id, monitor.Name, false);

            monitor.Version++;
            try
            {
                await db.SaveChangesAsync(ct);
                return new(monitor.Id, monitor.Name, true);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                logger.LogDebug("Monitor {MonitorId} changed concurrently; retrying pause/resume", monitorId);
            }
        }
    }
}
