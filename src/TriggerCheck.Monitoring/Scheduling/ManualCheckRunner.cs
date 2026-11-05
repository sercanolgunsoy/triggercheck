using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Cluster;

namespace TriggerCheck.Monitoring.Scheduling;

/// <summary>
/// OPS-3: "Check now" under the monitor lease (<see cref="MonitorLease"/>), so it never overlaps with a scheduled run or
/// another "check now" on any instance. Overlapping runs could otherwise both start a confirmation (one skipping its
/// delay), lose a failure count or send a certificate warning twice: the Version token covers only status transitions.
/// While another run holds the lease this waits up to the probe timeout + 5 s (at most <see cref="MaxWaitSeconds"/>),
/// which is how long that run can take; then it gives up (<c>null</c>). The schedule is unchanged: the runner computes
/// NextCheckAtUtc exactly as without a lease.
/// </summary>
public sealed class ManualCheckRunner(
    IMonitorCheckRunner runner,
    IDbContextFactory<AppDbContext> dbFactory,
    InstanceIdentity identity,
    TimeProvider time,
    IOptionsMonitor<MonitoringOptions> options,
    ILogger<ManualCheckRunner> logger)
{
    public const int MaxWaitSeconds = 30;
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Runs the check; null when another run kept the monitor leased for the whole wait.
    /// Throws <see cref="KeyNotFoundException"/> for a deleted monitor, and whatever <see cref="IMonitorCheckRunner.RunAsync"/> throws.
    /// </summary>
    public async Task<CheckResult?> RunAsync(int monitorId, CancellationToken ct)
    {
        int timeout;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            timeout = await db.Monitors.Where(m => m.Id == monitorId).Select(m => (int?)m.TimeoutSeconds).FirstOrDefaultAsync(ct)
                      ?? throw new KeyNotFoundException($"Monitor {monitorId} not found.");
        var length = MonitorLease.Length(timeout, options.CurrentValue.SchedulerLeaseMarginSeconds);
        var deadline = time.GetUtcNow().AddSeconds(Math.Min(MaxWaitSeconds, Math.Clamp(timeout <= 0 ? 10 : timeout, 1, 120) + 5));
        while (true)
        {
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                if (await MonitorLease.TryClaimUnscheduledAsync(db, monitorId, identity.Id, time.GetUtcNow().UtcDateTime, length, ct)) break;
                if (!await db.Monitors.AnyAsync(m => m.Id == monitorId, ct)) throw new KeyNotFoundException($"Monitor {monitorId} not found.");
            }
            if (time.GetUtcNow() >= deadline) return null;
            await Task.Delay(PollInterval, time, ct);
        }

        try
        {
            return await runner.RunAsync(monitorId, ct);
        }
        finally
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                if (!await MonitorLease.ReleaseAsync(db, monitorId, identity.Id, CancellationToken.None))
                    logger.LogDebug("Lease of monitor {MonitorId} was gone after its manual check", monitorId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not release the lease of monitor {MonitorId} after its manual check; it expires on its own", monitorId);
            }
        }
    }
}
