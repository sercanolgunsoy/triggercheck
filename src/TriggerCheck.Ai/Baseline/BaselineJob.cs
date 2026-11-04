using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Ai.Baseline;

/// <summary>
/// One pass of the L1 baseline job (AI-1) over every enabled monitor of an active customer, in batches of
/// <see cref="AiBaselineOptions.BatchSize"/> monitors:
/// <list type="number">
/// <item>Baselines: <see cref="MonitorBaseline"/> rows (24 hour buckets + global) are rebuilt from the successful checks
/// of the last <see cref="AiBaselineOptions.WindowDays"/> days when missing, or when older than
/// <see cref="AiBaselineOptions.RefreshMinutes"/> and it is the monitor's turn (monitor id modulo the ticks per refresh
/// period, so rebuilds are spread instead of all happening in one tick; anything older than twice the period is rebuilt
/// regardless). One query per rebuilt monitor.</item>
/// <item>Anomalies: the newest N successful checks are read with one query per batch and check interval (at most 4),
/// scored, and an <see cref="AnomalySignal"/> ("ResponseTimeMs") is opened when K of N are anomalous and closed when at
/// most <see cref="AiBaselineOptions.CloseAtMost"/> are (hysteresis).</item>
/// <item>Open signals of monitors that left the evaluated set (deleted, disabled, customer suspended) are closed.</item>
/// </list>
/// Reads CheckResults only; never touches monitor status, incidents or the runner. Re-running a pass on the same data
/// changes nothing.
/// </summary>
public sealed class BaselineJob(AppDbContext db, IOptions<AiOptions> options, TimeProvider time, ILogger<BaselineJob> logger)
{
    public const string ResponseTimeMetric = "ResponseTimeMs";

    public sealed record RunSummary(int Monitors, int BaselinesRebuilt, int SignalsOpened, int SignalsClosed);

    public async Task<RunSummary> RunAsync(CancellationToken ct = default)
    {
        var o = options.Value.Baseline;
        var now = time.GetUtcNow().UtcDateTime;
        var tickSeconds = Math.Max(1, o.IntervalSeconds);
        var slots = Math.Max(1, o.RefreshMinutes * 60 / tickSeconds);
        var tick = (long)(now - DateTime.UnixEpoch).TotalSeconds / tickSeconds;
        int monitors = 0, rebuilt = 0, opened = 0, closed = 0;
        var lastId = 0;
        while (true)
        {
            var batch = await db.Monitors.AsNoTracking()
                .Where(m => m.Id > lastId && m.IsEnabled && m.Customer.IsActive)
                .OrderBy(m => m.Id)
                .Select(m => new { m.Id, m.CustomerId, m.IntervalSeconds })
                .Take(Math.Max(1, o.BatchSize))
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            var ids = batch.Select(m => m.Id).ToList();
            var baselines = (await db.MonitorBaselines.Where(b => ids.Contains(b.MonitorId)).ToListAsync(ct))
                .GroupBy(b => b.MonitorId).ToDictionary(g => g.Key, g => g.ToList());
            var openSignals = (await db.AnomalySignals
                    .Where(s => ids.Contains(s.MonitorId) && s.Metric == ResponseTimeMetric && s.EndedAtUtc == null).ToListAsync(ct))
                .GroupBy(s => s.MonitorId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.StartedAtUtc).First());

            // Newest successful checks: one query per check interval in the batch. Only recent checks count, so a
            // monitor that stopped reporting does not keep an anomaly open.
            var recentByMonitor = new Dictionary<int, List<BaselineMath.Sample>>();
            foreach (var group in batch.GroupBy(m => Math.Max(1, m.IntervalSeconds)))
            {
                var groupIds = group.Select(m => m.Id).ToList();
                var since = now.AddSeconds(-Math.Max(15 * 60, 3 * o.N * group.Key));
                var rows = await db.CheckResults.AsNoTracking()
                    .Where(r => groupIds.Contains(r.MonitorId) && r.IsUp && r.CheckedAtUtc >= since)
                    .Select(r => new { r.MonitorId, r.CheckedAtUtc, r.ResponseTimeMs })
                    .ToListAsync(ct);
                foreach (var g in rows.GroupBy(r => r.MonitorId))
                    recentByMonitor[g.Key] = g.OrderByDescending(r => r.CheckedAtUtc).Take(o.N)
                        .Select(r => new BaselineMath.Sample(r.CheckedAtUtc, r.ResponseTimeMs)).ToList();
            }

            foreach (var m in batch)
            {
                monitors++;
                var rows = baselines.GetValueOrDefault(m.Id) ?? [];
                var global = rows.FirstOrDefault(r => r.Bucket == MonitorBaseline.GlobalBucket);
                var age = global is null ? TimeSpan.MaxValue : now - global.UpdatedAtUtc;
                var refresh = TimeSpan.FromMinutes(Math.Max(1, o.RefreshMinutes));
                if (global is null || age >= 2 * refresh || (age >= refresh && m.Id % slots == tick % slots))
                {
                    rows = await RebuildAsync(m.Id, m.CustomerId, rows, now, o, ct);
                    rebuilt++;
                }
                var stats = rows.ToDictionary(r => r.Bucket, r => new BaselineMath.Stats(r.MedianMs, r.MadMs, r.SampleCount));
                var recent = recentByMonitor.GetValueOrDefault(m.Id) ?? [];

                var eval = BaselineMath.Evaluate(stats, recent, o.K, o.N, o.ZThreshold, o.MinRatio, o.MinBucketSamples, o.MinGlobalSamples);
                var open = openSignals.GetValueOrDefault(m.Id);
                if (open is null && eval.IsAnomalous)
                {
                    db.AnomalySignals.Add(new AnomalySignal
                    {
                        MonitorId = m.Id, CustomerId = m.CustomerId, Metric = ResponseTimeMetric,
                        StartedAtUtc = eval.FirstAnomalousAtUtc ?? now, Score = eval.PeakScore,
                        DetailsJson = Details(eval), UpdatedAtUtc = now
                    });
                    opened++;
                }
                else if (open is not null && eval.AnomalousCount <= o.CloseAtMost)
                {
                    open.EndedAtUtc = recent.Count > 0 ? recent.Max(r => r.AtUtc) : now;
                    open.UpdatedAtUtc = now;
                    closed++;
                }
                else if (open is not null && eval.AnomalousCount > 0)
                {
                    open.Score = Math.Max(open.Score, eval.PeakScore);
                    open.DetailsJson = Details(eval);
                    open.UpdatedAtUtc = now;
                }
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        closed += await db.AnomalySignals
            .Where(s => s.EndedAtUtc == null && !db.Monitors.Any(m => m.Id == s.MonitorId && m.IsEnabled && m.Customer.IsActive))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.EndedAtUtc, now).SetProperty(s => s.UpdatedAtUtc, now), ct);

        if (opened + closed > 0)
            logger.LogInformation("Baseline job: {Opened} anomaly signal(s) opened, {Closed} closed ({Monitors} monitors)", opened, closed, monitors);
        return new RunSummary(monitors, rebuilt, opened, closed);
    }

    private async Task<List<MonitorBaseline>> RebuildAsync(int monitorId, int customerId, List<MonitorBaseline> existing, DateTime now,
        AiBaselineOptions o, CancellationToken ct)
    {
        var since = now.AddDays(-Math.Max(1, o.WindowDays));
        var samples = (await db.CheckResults.AsNoTracking()
                .Where(r => r.MonitorId == monitorId && r.IsUp && r.CheckedAtUtc >= since)
                .OrderByDescending(r => r.CheckedAtUtc)
                .Take(Math.Max(1, o.MaxSamples))
                .Select(r => new { r.CheckedAtUtc, r.ResponseTimeMs })
                .ToListAsync(ct))
            .Select(r => new BaselineMath.Sample(r.CheckedAtUtc, r.ResponseTimeMs)).ToList();

        db.MonitorBaselines.RemoveRange(existing);
        var rows = samples.Count == 0
            ? [new MonitorBaseline { MonitorId = monitorId, Bucket = MonitorBaseline.GlobalBucket, CustomerId = customerId, UpdatedAtUtc = now }]
            : BaselineMath.Build(samples).Select(kv => new MonitorBaseline
            {
                MonitorId = monitorId, Bucket = kv.Key, CustomerId = customerId,
                MedianMs = kv.Value.Median, MadMs = kv.Value.Mad, SampleCount = kv.Value.Count, UpdatedAtUtc = now
            }).ToList();
        // Removing and re-adding the same keys in one unit of work: save the removal first.
        await db.SaveChangesAsync(ct);
        db.MonitorBaselines.AddRange(rows);
        return rows;
    }

    private static string Details(BaselineMath.Evaluation e) => JsonSerializer.Serialize(new
    {
        anomalous = e.AnomalousCount,
        points = e.Points.Select(p => new
        {
            at = p.AtUtc,
            ms = p.Ms,
            baselineMedianMs = Math.Round(p.MedianMs, 1),
            ratio = p.MedianMs > 0 ? Math.Round(p.Ms / p.MedianMs, 2) : 0,
            score = Math.Round(p.Score, 2),
            p.Anomalous
        })
    });
}

/// <summary>
/// Runs <see cref="BaselineJob"/> every <see cref="AiBaselineOptions.IntervalSeconds"/>. Off when Ai:Baseline:Enabled
/// or Monitoring:Enabled is false. Independent of Ai:Enabled (no LLM involved).
/// </summary>
public sealed class BaselineHostedService(
    IServiceScopeFactory scopes,
    IOptions<AiOptions> options,
    IConfiguration config,
    TimeProvider time,
    ILogger<BaselineHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value.Baseline;
        if (!o.Enabled || !config.GetValue("Monitoring:Enabled", true)) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, o.IntervalSeconds)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<BaselineJob>().RunAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Baseline job failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
