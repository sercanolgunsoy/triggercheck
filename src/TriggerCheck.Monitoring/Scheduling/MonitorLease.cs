using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Monitoring.Scheduling;

/// <summary>
/// OPS-3: the scheduler's per-monitor lease (<see cref="Domain.Entities.Monitor.LeasedUntilUtc"/> /
/// <see cref="Domain.Entities.Monitor.LeaseOwner"/>), so instances sharing one database never check the same slot twice.
/// <para>
/// Claiming is a portable compare-and-set: one conditional UPDATE that succeeds only while the monitor is still due
/// (NextCheckAtUtc null or past) and unleased (null or expired). Exactly one instance gets "1 row affected": on SQLite
/// writers are serialized, on Postgres (READ COMMITTED) the losing UPDATE re-evaluates its WHERE on the committed row.
/// The winner's run advances NextCheckAtUtc before it releases, so an instance working from an older due list cannot
/// claim the same slot afterwards. Postgres could later select candidates with <c>FOR UPDATE SKIP LOCKED</c> to avoid
/// wasted claim attempts; the CAS stays the correctness guarantee.
/// </para>
/// <para>
/// The lease is written with ExecuteUpdate outside <see cref="Domain.Entities.Monitor.Version"/>: it never conflicts with
/// user edits or the runner's own save (which only writes the columns it changed). A crashed instance's lease is
/// reclaimed once it expires. A run that outlives its lease (e.g. a database stall) may overlap with the reclaiming
/// instance; the Version token still prevents duplicate incidents / alerts in that case.
/// </para>
/// </summary>
public static class MonitorLease
{
    /// <summary>
    /// Worst-case run time + margin: a run may wait behind one in-process "check now" of the same monitor
    /// (MonitorRunGate) before its own probe, so two probe timeouts (clamped like the probes, 1-120 s).
    /// </summary>
    public static TimeSpan Length(int timeoutSeconds, int marginSeconds) =>
        TimeSpan.FromSeconds(2 * Math.Clamp(timeoutSeconds <= 0 ? 10 : timeoutSeconds, 1, 120) + Math.Max(5, marginSeconds));

    /// <summary>Claims the monitor's current slot for <paramref name="owner"/>; false when another instance has it or it is no longer due.</summary>
    public static async Task<bool> TryClaimAsync(AppDbContext db, int monitorId, string owner, DateTime nowUtc, TimeSpan length, CancellationToken ct)
    {
        var until = nowUtc + length;
        var claimed = await db.Monitors
            .Where(m => m.Id == monitorId
                        && (m.NextCheckAtUtc == null || m.NextCheckAtUtc <= nowUtc)
                        && (m.LeasedUntilUtc == null || m.LeasedUntilUtc <= nowUtc))
            .ExecuteUpdateAsync(u => u
                .SetProperty(m => m.LeasedUntilUtc, until)
                .SetProperty(m => m.LeaseOwner, owner), ct);
        return claimed == 1;
    }

    /// <summary>Releases the lease if <paramref name="owner"/> still holds it; false when it was lost (expired and reclaimed) or the monitor is gone.</summary>
    public static async Task<bool> ReleaseAsync(AppDbContext db, int monitorId, string owner, CancellationToken ct) =>
        await db.Monitors
            .Where(m => m.Id == monitorId && m.LeaseOwner == owner)
            .ExecuteUpdateAsync(u => u
                .SetProperty(m => m.LeasedUntilUtc, (DateTime?)null)
                .SetProperty(m => m.LeaseOwner, (string?)null), ct) == 1;
}
