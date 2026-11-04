using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TriggerCheck.Monitoring.Alerting;

/// <summary>
/// Drains the <see cref="AlertQueue"/> and delivers each alert through <see cref="IAlertDispatcher"/> (own DI scope,
/// at most <see cref="AlertingOptions.MaxConcurrency"/> alerts at a time; per-attempt timeout and retries with backoff
/// are done by the dispatcher). Runs regardless of <c>Monitoring:Enabled</c>, so alerts produced by "check now" are
/// delivered even when the scheduler is off.
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrency));
        var running = new ConcurrentDictionary<Task, byte>();
        try
        {
            // Not stoppingToken: after StopAsync the queue is completed and the alerts left in it are still delivered.
            await foreach (var message in queue.Reader.ReadAllAsync(_abort.Token))
            {
                try
                {
                    await slots.WaitAsync(_abort.Token);
                }
                catch (OperationCanceledException)
                {
                    queue.MarkDone();
                    logger.LogWarning("Alert '{Subject}' for monitor {MonitorId} dropped at shutdown", message.Subject, message.MonitorId);
                    throw;
                }
                var task = DeliverAsync(message, slots, stoppingToken);
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

    private async Task DeliverAsync(AlertMessage message, SemaphoreSlim slots, CancellationToken stopRetrying)
    {
        try
        {
            await Task.Yield(); // never deliver on the reader loop
            using var scope = scopes.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IAlertDispatcher>();
            await dispatcher.DispatchAsync(message, _abort.Token, stopRetrying);
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            logger.LogWarning("Delivery of alert '{Subject}' for monitor {MonitorId} was cancelled at shutdown", message.Subject, message.MonitorId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Alert delivery failed for monitor {MonitorId}", message.MonitorId);
        }
        finally
        {
            slots.Release();
            queue.MarkDone();
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
