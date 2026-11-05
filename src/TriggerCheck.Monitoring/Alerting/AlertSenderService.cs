using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TriggerCheck.Monitoring.Alerting;

/// <summary>
/// Drains the <see cref="AlertQueue"/> and delivers each alert through <see cref="IAlertDispatcher"/> (own DI scope).
/// <list type="bullet">
/// <item>Ordering: every monitor has a lane for its status alerts (DOWN/UP) and a separate one for certificate-expiry
/// warnings, so the two never coalesce or supersede each other (escalations: see below). A lane's alerts are delivered one at a time, in order
/// (DOWN is finished before the following UP starts). Different lanes run in parallel.</item>
/// <item>Coalescing: a lane keeps at most one waiting alert, the newest. Older waiting alerts that never started are
/// recorded as superseded (0 attempts) without being sent.</item>
/// <item>Supersede: when a newer alert for the same monitor arrives while an older one is waiting to retry, the retry
/// is dropped and the older alert is recorded as superseded, so a stale DOWN never arrives after the UP.</item>
/// <item>Escalations (FEAT-5) have a third lane per monitor. A status alert (UP, or a DOWN of a new incident) supersedes
/// the monitor's waiting and retrying escalations, but an escalation never supersedes anything: it does not touch the
/// status lane (the original DOWN is always delivered), and a newer escalation does not cancel an older one's
/// delivery; a still waiting older escalation is merged into the newer one (its channels are added).</item>
/// <item>AI-4 group follow-ups (<see cref="AlertKind.GroupUpdate"/>) have a fourth lane per (primary) monitor that behaves
/// like the escalation lane: nothing supersedes them and a waiting one is merged into the newer one (affected monitors
/// are combined). A <see cref="AlertMessage.Deferred"/> DOWN (held back by grouping, sent later) does not make the
/// monitor's escalations stale.</item>
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
    // Never disposed: StopAsync may run again after Dispose (StopApplication, then disposing the host) and must not
    // throw ObjectDisposedException. It holds no unmanaged resources beyond the CancelAfter timer.
    private readonly CancellationTokenSource _abort = new();
    private readonly object _lock = new();
    private readonly Dictionary<(int MonitorId, LaneKind Lane), Lane> _lanes = new();

    private enum LaneKind { Status, Certificate, Escalation, GroupUpdate }

    private static (int MonitorId, LaneKind Lane) LaneKey(AlertMessage m) => (m.MonitorId, m.Kind switch
    {
        AlertKind.CertificateExpiring => LaneKind.Certificate,
        AlertKind.Escalation => LaneKind.Escalation,
        AlertKind.GroupUpdate => LaneKind.GroupUpdate,
        _ => LaneKind.Status
    });

    /// <summary>Lanes whose alerts never supersede each other; a waiting one is merged into the newer one.</summary>
    private static bool IsMergingLane(LaneKind lane) => lane is LaneKind.Escalation or LaneKind.GroupUpdate;

    private static AlertMessage Merge(AlertMessage older, AlertMessage newer) => newer.Kind == AlertKind.GroupUpdate
        ? newer with { AlsoAffected = (older.AlsoAffected ?? []).Union(newer.AlsoAffected ?? []).ToList() }
        : MergeEscalations(older, newer);

    /// <summary>A waiting escalation merged into a newer one: the newer level, delivered to the channels of both.</summary>
    private static AlertMessage MergeEscalations(AlertMessage older, AlertMessage newer) =>
        newer with { ChannelIds = older.ChannelIds is null || newer.ChannelIds is null ? null : older.ChannelIds.Union(newer.ChannelIds).ToList() };
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    private sealed class Lane
    {
        /// <summary>The newest alert waiting behind the one in delivery (null = none).</summary>
        public AlertMessage? Waiting;
        /// <summary>
        /// Cancelled when a newer alert arrives behind the one in delivery. Never disposed: it has no timer, and the
        /// reader may still cancel it after the delivery finished.
        /// </summary>
        public CancellationTokenSource? Superseded;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Not stoppingToken: after StopAsync the queue is completed and the alerts left in it are still delivered.
            await foreach (var received in queue.Reader.ReadAllAsync(_abort.Token))
            {
                var message = received;
                var key = LaneKey(message);
                Lane? started = null;
                AlertMessage? skipped = null, merged = null, staleEscalation = null;
                CancellationTokenSource? toCancel = null, escalationToCancel = null;
                lock (_lock)
                {
                    // A status alert (recovery, or a DOWN of a new incident) makes the monitor's escalations stale.
                    if (key.Lane == LaneKind.Status && !message.Deferred && _lanes.TryGetValue((message.MonitorId, LaneKind.Escalation), out var escalations))
                    {
                        staleEscalation = escalations.Waiting;
                        escalations.Waiting = null;
                        escalationToCancel = escalations.Superseded;
                    }
                    if (_lanes.TryGetValue(key, out var lane))
                    {
                        if (IsMergingLane(key.Lane))
                        {
                            if (lane.Waiting is { } older) message = Merge(merged = older, message);
                        }
                        else
                        {
                            skipped = lane.Waiting;
                            toCancel = lane.Superseded;
                        }
                        lane.Waiting = message;
                    }
                    else
                    {
                        // The token exists before the lane task runs, so an alert arriving right behind this one
                        // (e.g. UP queued back-to-back with DOWN) always cancels the DOWN's retries.
                        _lanes[key] = started = new Lane { Superseded = new CancellationTokenSource() };
                    }
                }
                toCancel?.Cancel();
                escalationToCancel?.Cancel();
                if (skipped is not null) Track(RecordSkippedAsync(skipped));
                if (staleEscalation is not null) Track(RecordSkippedAsync(staleEscalation));
                if (merged is not null) queue.MarkDone(merged); // delivered as part of the newer escalation
                if (started is not null) Track(RunLaneAsync(message, started, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            // drain time is over
        }

        await Task.WhenAll(_running.Keys);

        var dropped = 0;
        while (queue.Reader.TryRead(out var m))
        {
            dropped++;
            queue.MarkDone(m);
        }
        if (dropped > 0) logger.LogWarning("{Count} queued alert(s) were dropped at shutdown", dropped);
    }

    private void Track(Task task)
    {
        _running.TryAdd(task, 0);
        _ = task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
    }

    private async Task RunLaneAsync(AlertMessage first, Lane lane, CancellationToken stopRetrying)
    {
        await Task.Yield(); // never deliver on the reader loop
        var message = first;
        CancellationTokenSource superseded;
        lock (_lock) superseded = lane.Superseded!;
        while (true)
        {
            try
            {
                await DeliverAsync(message, stopRetrying, superseded.Token);
            }
            finally
            {
                queue.MarkDone(message);
            }

            lock (_lock)
            {
                if (lane.Waiting is null)
                {
                    lane.Superseded = null;
                    _lanes.Remove(LaneKey(message));
                    return;
                }
                message = lane.Waiting;
                lane.Waiting = null;
                // Fresh token for the next delivery, published in the same lock in which Waiting was taken, so the
                // reader cancels it for any alert that arrives from now on.
                lane.Superseded = superseded = new CancellationTokenSource();
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

    private async Task RecordSkippedAsync(AlertMessage message)
    {
        try
        {
            await Task.Yield();
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IAlertDispatcher>().RecordSupersededAsync(message, _abort.Token);
        }
        catch (Exception ex) when (!_abort.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not record superseded alert for monitor {MonitorId}", message.MonitorId);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            queue.MarkDone(message);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        _abort.CancelAfter(TimeSpan.FromSeconds(Math.Max(0, options.Value.ShutdownDrainSeconds)));
        await base.StopAsync(cancellationToken); // signals stoppingToken = stop retrying, then waits for the drain
    }

}
