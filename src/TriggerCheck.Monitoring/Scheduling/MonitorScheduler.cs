using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
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

    public static Task<List<int>> SelectDueIdsAsync(AppDbContext db, DateTime nowUtc, int limit, CancellationToken ct) =>
        db.Monitors.AsNoTracking()
            .WhereDue(nowUtc)
            .OrderBy(m => m.NextCheckAtUtc) // NULLs (never checked) first in SQLite
            .Select(m => m.Id)
            .Take(limit)
            .ToListAsync(ct);
}

/// <summary>
/// Polls for due monitors every <see cref="MonitoringOptions.SchedulerTickMilliseconds"/> and runs them through
/// <see cref="IMonitorCheckRunner"/> concurrently (bounded by <see cref="MonitoringOptions.MaxConcurrency"/>),
/// each in its own DI scope / DbContext. A monitor that is still in flight is never dispatched twice.
/// Does nothing when <see cref="MonitoringOptions.Enabled"/> is false. Assumes a single scheduler instance.
/// </summary>
public sealed class MonitorScheduler : BackgroundService
{
    private const int MaxDuePerTick = 2000;

    private readonly IServiceScopeFactory _scopes;
    private readonly MonitoringOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MonitorScheduler> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    public MonitorScheduler(IServiceScopeFactory scopes, IOptions<MonitoringOptions> options, TimeProvider time, ILogger<MonitorScheduler> logger)
    {
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

    /// <summary>One scheduler tick: selects due monitors and starts a check for each one not already in flight. Returns the dispatched ids.</summary>
    public async Task<IReadOnlyList<int>> DispatchDueAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        List<int> due;
        using (var scope = _scopes.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync(ct);
            due = await DueMonitorQuery.SelectDueIdsAsync(db, now, MaxDuePerTick + _inFlight.Count, ct);
        }

        var dispatched = new List<int>();
        foreach (var id in due)
        {
            if (!_inFlight.TryAdd(id, 0)) continue; // previous check still running
            var task = Task.Run(() => RunOneAsync(id, ct), CancellationToken.None);
            _running.TryAdd(task, 0);
            _ = task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
            dispatched.Add(id);
        }

        if (dispatched.Count > 0)
            _logger.LogDebug("Dispatched {Count} due monitor(s); {InFlight} in flight", dispatched.Count, _inFlight.Count);
        return dispatched;
    }

    private async Task RunOneAsync(int monitorId, CancellationToken ct)
    {
        var acquired = false;
        try
        {
            await _slots.WaitAsync(ct);
            acquired = true;
            using var scope = _scopes.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IMonitorCheckRunner>();
            var result = await runner.RunAsync(monitorId, ct);
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
            if (acquired) _slots.Release();
            _inFlight.TryRemove(monitorId, out _);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _slots.Dispose();
    }
}
