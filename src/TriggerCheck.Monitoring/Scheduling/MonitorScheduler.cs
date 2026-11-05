using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Cluster;
using TriggerCheck.Monitoring.Diagnostics;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Scheduling;

public static class DueMonitorQuery
{
    /// <summary>Monitors the scheduler must check at <paramref name="nowUtc"/>: enabled, not paused, customer active, NextCheckAtUtc null or past.</summary>
    public static IQueryable<MonitorEntity> WhereDue(this IQueryable<MonitorEntity> monitors, DateTime nowUtc) =>
        monitors.Where(m => m.IsEnabled
                            && m.Status != MonitorStatus.Paused
                            && m.Customer.IsActive
                            && (m.NextCheckAtUtc == null || m.NextCheckAtUtc <= nowUtc));

    /// <summary>A due monitor the scheduler may try to claim, with the timeout its lease length is based on.</summary>
    public sealed record Candidate(int Id, int TimeoutSeconds);

    /// <summary>Due monitors not currently leased by a scheduler instance (OPS-3, see <see cref="MonitorLease"/>).</summary>
    public static Task<List<Candidate>> SelectUnleasedDueAsync(AppDbContext db, DateTime nowUtc, int limit, CancellationToken ct) =>
        db.Monitors.AsNoTracking()
            .WhereDue(nowUtc)
            .Where(m => m.LeasedUntilUtc == null || m.LeasedUntilUtc <= nowUtc)
            .OrderBy(m => m.NextCheckAtUtc) // NULLs (never checked) first in SQLite
            .Select(m => new Candidate(m.Id, m.TimeoutSeconds))
            .Take(limit)
            .ToListAsync(ct);
}

/// <summary>
/// Polls for due monitors every <see cref="MonitoringOptions.SchedulerTickMilliseconds"/> and runs them through
/// <see cref="IMonitorCheckRunner"/> concurrently (bounded by <see cref="MonitoringOptions.MaxConcurrency"/>),
/// each in its own DI scope / DbContext. A monitor that is still in flight is never dispatched twice.
/// Does nothing when <see cref="MonitoringOptions.Enabled"/> is false.
/// <para>
/// Multiple instances (OPS-3): several schedulers may share one database. A dispatched check first waits for a
/// concurrency slot and only then claims the monitor's lease (<see cref="MonitorLease.TryClaimAsync"/>), so a check queued
/// on a busy instance holds no lease and an idle instance can take the slot. Only the instance whose claim succeeds
/// runs the check; the lease is released when the run ends (also on failure or shutdown).
/// "Check now" takes the same lease (<see cref="ManualCheckRunner"/>), so no two runs of a monitor overlap on any instance.
/// </para>
/// </summary>
public sealed class MonitorScheduler : BackgroundService
{
    private const int MaxDuePerTick = 2000;

    private readonly IServiceScopeFactory _scopes;
    private readonly MonitoringOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MonitorScheduler> _logger;
    // Not disposed (no wait handle is ever created): checks still finishing after a stop must be able to release it.
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    private readonly MonitoringMetrics _metrics;
    private readonly string _instanceId;
    private readonly bool _instanceIdConfigured;

    public MonitorScheduler(IServiceScopeFactory scopes, IOptions<MonitoringOptions> options, TimeProvider time, ILogger<MonitorScheduler> logger,
        MonitoringMetrics metrics, InstanceIdentity identity)
    {
        _metrics = metrics;
        _instanceId = identity.Id;
        _instanceIdConfigured = identity.IsConfigured;
        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));
    }

    public int InFlightCount => _inFlight.Count;
    public bool IsInFlight(int monitorId) => _inFlight.ContainsKey(monitorId);

    /// <summary>Completes when every check dispatched so far has finished.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_running.Keys);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_instanceIdConfigured) await ReleaseOwnLeasesAsync(stoppingToken);
        if (!_options.Enabled)
        {
            _logger.LogInformation("Monitoring scheduler disabled (Monitoring:Enabled=false)");
            return;
        }

        var tick = TimeSpan.FromMilliseconds(Math.Max(50, _options.SchedulerTickMilliseconds));
        _logger.LogInformation("Monitoring scheduler started (tick {Tick} ms, max concurrency {Max})", tick.TotalMilliseconds, _options.MaxConcurrency);

        using var timer = new PeriodicTimer(tick, _time);
        try
        {
            do
            {
                try
                {
                    await DispatchDueAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduler tick failed; will retry on next tick");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }

        try { await WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception) { /* checks observe cancellation; don't block shutdown */ }
        _logger.LogInformation("Monitoring scheduler stopped");
    }

    /// <summary>
    /// One scheduler tick: selects due, unleased monitors and starts a check for each one not already in flight.
    /// Returns the dispatched ids (a dispatched check still runs only if it wins the lease claim).
    /// </summary>
    public async Task<IReadOnlyList<int>> DispatchDueAsync(CancellationToken ct)
    {
        _metrics.SchedulerTickStarted();
        var now = _time.GetUtcNow().UtcDateTime;
        List<DueMonitorQuery.Candidate> due;
        using (var scope = _scopes.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync(ct);
            due = await DueMonitorQuery.SelectUnleasedDueAsync(db, now, MaxDuePerTick + _inFlight.Count, ct);
        }

        var dispatched = new List<int>();
        foreach (var candidate in due)
        {
            if (!_inFlight.TryAdd(candidate.Id, 0)) continue; // previous check still running
            var task = Task.Run(() => RunOneAsync(candidate, ct), CancellationToken.None);
            _running.TryAdd(task, 0);
            _ = task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
            dispatched.Add(candidate.Id);
        }

        if (dispatched.Count > 0)
            _logger.LogDebug("Dispatched {Count} due monitor(s); {InFlight} in flight", dispatched.Count, _inFlight.Count);
        _metrics.SchedulerTickCompleted(due.Count, _inFlight.Count); // heartbeat for /health/ready
        return dispatched;
    }

    private async Task RunOneAsync(DueMonitorQuery.Candidate candidate, CancellationToken ct)
    {
        var monitorId = candidate.Id;
        var acquired = false;
        var leased = false;
        var completed = false;
        using var scope = _scopes.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        try
        {
            await _slots.WaitAsync(ct);
            acquired = true;
            // Claimed only now (not at dispatch): a check waiting for a slot holds no lease, so another instance can take it.
            await using (var db = await factory.CreateDbContextAsync(ct))
                leased = await MonitorLease.TryClaimAsync(db, monitorId, _instanceId, _time.GetUtcNow().UtcDateTime,
                    MonitorLease.Length(candidate.TimeoutSeconds, _options.SchedulerLeaseMarginSeconds), ct);
            if (!leased)
            {
                _logger.LogDebug("Monitor {MonitorId} was claimed by another instance or is no longer due", monitorId);
                return;
            }
            var runner = scope.ServiceProvider.GetRequiredService<IMonitorCheckRunner>();
            var result = await runner.RunAsync(monitorId, ct);
            completed = true;
            _logger.LogDebug("Checked monitor {MonitorId}: {State} in {Ms} ms {Error}", monitorId, result.IsUp ? "up" : "down", result.ResponseTimeMs, result.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch (KeyNotFoundException)
        {
            _logger.LogDebug("Monitor {MonitorId} was deleted before it could be checked", monitorId);
        }
        catch (MonitorCheckDiscardedException ex)
        {
            _logger.LogDebug("{Message}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check of monitor {MonitorId} failed", monitorId);
        }
        finally
        {
            // Before leaving _inFlight: this instance must not dispatch the monitor again while it still holds the lease.
            if (leased) await ReleaseLeaseAsync(factory, monitorId, completed);
            if (acquired) _slots.Release();
            _inFlight.TryRemove(monitorId, out _);
        }
    }

    /// <summary>
    /// OPS-3: with a configured (stable) instance id, leases left by a crashed predecessor with the same id are freed at
    /// startup (also when this scheduler is disabled: "check now" and the job leases use the same id).
    /// </summary>
    private async Task ReleaseOwnLeasesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var (monitors, jobs) = await scope.ServiceProvider.GetRequiredService<ClusterLeases>().ReleaseOwnedAsync(ct);
            if (monitors + jobs > 0)
                _logger.LogInformation("Released {Monitors} monitor lease(s) and {Jobs} job lease(s) left by a previous run of instance {InstanceId}", monitors, jobs, _instanceId);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not release the leases of a previous run; they expire on their own");
        }
    }

    /// <summary>Releases the monitor's lease (also on failure / shutdown, so another instance need not wait for expiry).</summary>
    private async Task ReleaseLeaseAsync(IDbContextFactory<AppDbContext> factory, int monitorId, bool completed)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
            if (!await MonitorLease.ReleaseAsync(db, monitorId, _instanceId, CancellationToken.None) && completed)
                _logger.LogWarning("Check of monitor {MonitorId} outlived its scheduler lease; another instance may have checked the same slot", monitorId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not release the scheduler lease of monitor {MonitorId}; it expires on its own", monitorId);
        }
    }
}
