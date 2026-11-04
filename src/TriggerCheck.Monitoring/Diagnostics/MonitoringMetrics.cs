using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Diagnostics;

/// <summary>
/// Metrics (Meter <see cref="Name"/>) and tracing (ActivitySource <see cref="Name"/>) of the monitoring engine, plus the
/// scheduler heartbeat used by the readiness health check. Registered as a singleton; the meter comes from
/// <see cref="IMeterFactory"/> so tests can observe one container's instruments in isolation.
/// <list type="bullet">
/// <item><c>triggercheck.monitoring.checks</c> (counter): probes executed, tags <c>monitor.type</c>, <c>result</c> (up/down/error).</item>
/// <item><c>triggercheck.monitoring.check.duration</c> (histogram, s): probe wall time, same tags.</item>
/// <item><c>triggercheck.monitoring.scheduler.tick_lag</c> (histogram, s): how much later than its interval a scheduler tick started.</item>
/// <item><c>triggercheck.monitoring.scheduler.due_monitors</c> (gauge): monitors due at the last tick (the backlog).</item>
/// <item><c>triggercheck.monitoring.scheduler.in_flight</c> (gauge): checks running at the end of the last tick.</item>
/// <item><c>triggercheck.monitoring.scheduler.heartbeat</c> (gauge, s): unix time of the last completed scheduler tick.</item>
/// <item><c>triggercheck.monitoring.alerts</c> (counter): alert deliveries, tags <c>channel.type</c>, <c>result</c> (sent/failed).</item>
/// </list>
/// </summary>
public sealed class MonitoringMetrics
{
    public const string Name = "TriggerCheck.Monitoring";

    /// <summary>Source of the per-check spans ("monitor.check").</summary>
    public static readonly ActivitySource ActivitySource = new(Name);

    private readonly TimeProvider _time;
    private readonly TimeSpan _tickInterval;
    private readonly Counter<long> _checks;
    private readonly Histogram<double> _checkDuration;
    private readonly Histogram<double> _tickLag;
    private readonly Counter<long> _alerts;

    private long _lastTickStartTicks;      // TimeProvider timestamp of the previous tick start (0 = none yet)
    private long _lastHeartbeatUnixMs = -1; // -1 = no completed tick yet
    private int _dueMonitors;
    private int _inFlight;

    public MonitoringMetrics(IMeterFactory meterFactory, IOptions<MonitoringOptions> options, TimeProvider time)
    {
        _time = time;
        _tickInterval = TimeSpan.FromMilliseconds(Math.Max(50, options.Value.SchedulerTickMilliseconds));
        StartedAt = time.GetUtcNow();

        var meter = meterFactory.Create(Name);
        _checks = meter.CreateCounter<long>("triggercheck.monitoring.checks", "{check}", "Probes executed");
        _checkDuration = meter.CreateHistogram<double>("triggercheck.monitoring.check.duration", "s", "Probe duration");
        _tickLag = meter.CreateHistogram<double>("triggercheck.monitoring.scheduler.tick_lag", "s",
            "Delay of a scheduler tick beyond its configured interval");
        _alerts = meter.CreateCounter<long>("triggercheck.monitoring.alerts", "{alert}", "Alert deliveries by channel type and result");
        meter.CreateObservableGauge("triggercheck.monitoring.scheduler.due_monitors", () => Volatile.Read(ref _dueMonitors),
            "{monitor}", "Monitors due at the last scheduler tick");
        meter.CreateObservableGauge("triggercheck.monitoring.scheduler.in_flight", () => Volatile.Read(ref _inFlight),
            "{check}", "Checks running at the end of the last scheduler tick");
        meter.CreateObservableGauge("triggercheck.monitoring.scheduler.heartbeat", ObserveHeartbeat,
            "s", "Unix time of the last completed scheduler tick");
    }

    /// <summary>When this instance (i.e. the process' monitoring engine) was created; start of the heartbeat grace period.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>End of the last completed scheduler tick, or null if none completed yet.</summary>
    public DateTimeOffset? LastSchedulerHeartbeat
    {
        get
        {
            var ms = Interlocked.Read(ref _lastHeartbeatUnixMs);
            return ms < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
    }

    /// <summary>Called at the start of every scheduler tick: records the tick lag.</summary>
    public void SchedulerTickStarted()
    {
        var now = _time.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastTickStartTicks, now);
        if (previous == 0) return;
        var lag = _time.GetElapsedTime(previous, now) - _tickInterval;
        _tickLag.Record(Math.Max(0, lag.TotalSeconds));
    }

    /// <summary>Called when a scheduler tick completed: updates the heartbeat and backlog gauges.</summary>
    public void SchedulerTickCompleted(int dueMonitors, int inFlight)
    {
        Volatile.Write(ref _dueMonitors, dueMonitors);
        Volatile.Write(ref _inFlight, inFlight);
        Interlocked.Exchange(ref _lastHeartbeatUnixMs, _time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    /// <summary>Records one probe. <paramref name="isUp"/> null means the checker threw.</summary>
    public void RecordCheck(MonitorType type, bool? isUp, TimeSpan duration)
    {
        var tags = new TagList { { "monitor.type", TypeTag(type) }, { "result", ResultTag(isUp) } };
        _checks.Add(1, tags);
        _checkDuration.Record(duration.TotalSeconds, tags);
    }

    /// <summary>
    /// Hook for the alerting code: call once per delivery attempt (per channel) with its outcome.
    /// Not yet called from AlertDispatcher (being reworked separately); wire it in where each NotifyResult is known.
    /// </summary>
    public void RecordAlert(AlertChannelType channelType, bool success) =>
        _alerts.Add(1, new TagList { { "channel.type", channelType.ToString().ToLowerInvariant() }, { "result", success ? "sent" : "failed" } });

    /// <summary>Starts the span of one probe (null when nobody listens). Never tagged with the target: it may carry credentials.</summary>
    public static Activity? StartCheckActivity(MonitorEntity monitor)
    {
        var activity = ActivitySource.StartActivity("monitor.check");
        if (activity is null) return null;
        activity.SetTag("monitor.id", monitor.Id);
        activity.SetTag("monitor.type", TypeTag(monitor.Type));
        activity.SetTag("customer.id", monitor.CustomerId);
        return activity;
    }

    internal static string TypeTag(MonitorType type) => type.ToString().ToLowerInvariant();
    internal static string ResultTag(bool? isUp) => isUp switch { true => "up", false => "down", null => "error" };

    private IEnumerable<Measurement<long>> ObserveHeartbeat()
    {
        var ms = Interlocked.Read(ref _lastHeartbeatUnixMs);
        if (ms >= 0) yield return new Measurement<long>(ms / 1000);
    }
}
