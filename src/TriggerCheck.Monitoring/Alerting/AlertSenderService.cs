using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TriggerCheck.Monitoring.Alerting;

/// <summary>
/// Drains the <see cref="AlertQueue"/> and delivers each alert through <see cref="IAlertDispatcher"/> (own DI scope).
/// <list type="bullet">
/// <item>Ordering: every monitor has a lane; its alerts are delivered one at a time, in order (DOWN is finished before
/// the following UP starts). Different monitors run in parallel.</item>
/// <item>Supersede: when a newer alert for the same monitor arrives while an older one is waiting to retry, the retry
/// is dropped and the older alert is recorded as superseded, so a stale DOWN never arrives after the UP.</item>
/// <item>Fairness: delivery attempts are throttled by <see cref="AlertDeliveryThrottle"/> (global and per customer)
/// and retry waits hold no slot, so one tenant's failing endpoint cannot stall other tenants' alerts.</item>
/// </list>
/// Runs regardless of <c>Monitoring:Enabled</c>, so alerts produced by "check now" are delivered even when the
/// scheduler is off.
/// Shutdown: the queue stops accepting alerts, alerts already queued get one attempt each (pending retry waits end
/// at once and the failure is recorded) for at most <see cref="AlertingOptions.ShutdownDrainSeconds"/>; after that,
/// in-flight sends are cancelled and whatever is still queued is logged as dropped.
/// </summary>
public sealed class AlertSenderService(
    AlertQueue queue,
    IServiceScopeFactory scopes,
    IOptions<AlertingOptions> options,
    ILogger<AlertSenderService> logger) : BackgroundService
{
    private readonly CancellationTokenSource _abort = new();
    private readonly object _lock = new();
    private readonly Dictionary<int, Lane> _lanes = new();

    private sealed class Lane
    {
        public readonly Queue<AlertMessage> Items = new();
        /// <summary>Cancelled when a newer alert for the same monitor is waiting behind the current one.</summary>
        public CancellationTokenSource? Superseded;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new ConcurrentDictionary<Task, byte>();
        try
        {
            // Not stoppingToken: after StopAsync the queue is completed and the alerts left in it are still delivered.
            await foreach (var message in queue.Reader.ReadAllAsync(_abort.Token))
            {
                Lane? started = null;
                lock (_lock)
                {
                    if (_lanes.TryGetValue(message.MonitorId, out var lane))
                    {
                        lane.Items.Enqueue(message);
                        lane.Superseded?.Cancel();
                    }
                    else
                    {
                        started = new Lane();
                        started.Items.Enqueue(message);
                        _lanes[message.MonitorId] = started;
                    }
                }
                if (started is null) continue;
                var task = RunLaneAsync(message.MonitorId, started, stoppingToken);
                running.TryAdd(task, 0);
                _ = task.ContinueWith(t => running.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            // drain time is over
        }

        await Task.WhenAll(running.Keys);

        var dropped = 0;
        while (queue.Reader.TryRead(out _))
        {
            dropped++;
            queue.MarkDone();
        }
        if (dropped > 0) logger.LogWarning("{Count} queued alert(s) were dropped at shutdown", dropped);
    }

    private async Task RunLaneAsync(int monitorId, Lane lane, CancellationToken stopRetrying)
    {
        await Task.Yield(); // never deliver on the reader loop
        while (true)
        {
            AlertMessage message;
            CancellationTokenSource superseded;
            lock (_lock)
            {
                if (lane.Items.Count == 0)
                {
                    _lanes.Remove(monitorId);
                    return;
                }
                message = lane.Items.Dequeue();
                lane.Superseded = superseded = new CancellationTokenSource();
                if (lane.Items.Count > 0) superseded.Cancel(); // a newer alert is already waiting: no retries
            }
            try
            {
                await DeliverAsync(message, stopRetrying, superseded.Token);
            }
            finally
            {
                lock (_lock) lane.Superseded = null;
                superseded.Dispose();
                queue.MarkDone();
            }
        }
    }

    private async Task DeliverAsync(AlertMessage message, CancellationToken stopRetrying, CancellationToken superseded)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IAlertDispatcher>();
            await dispatcher.DispatchAsync(message, _abort.Token, stopRetrying, superseded);
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            logger.LogWarning("Delivery of alert '{Subject}' for monitor {MonitorId} was cancelled at shutdown", message.Subject, message.MonitorId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Alert delivery failed for monitor {MonitorId}", message.MonitorId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        _abort.CancelAfter(TimeSpan.FromSeconds(Math.Max(0, options.Value.ShutdownDrainSeconds)));
        await base.StopAsync(cancellationToken); // signals stoppingToken = stop retrying, then waits for the drain
    }

    public override void Dispose()
    {
        _abort.Dispose();
        base.Dispose();
    }
}
