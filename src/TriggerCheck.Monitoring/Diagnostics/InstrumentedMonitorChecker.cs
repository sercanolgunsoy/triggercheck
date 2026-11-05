using System.Diagnostics;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Diagnostics;

/// <summary>
/// Wraps a checker so that every probe gets a "monitor.check" span and is counted / timed in
/// <see cref="MonitoringMetrics"/>. Applied when the check runner is resolved (see DependencyInjection), so it also
/// covers checkers added later or swapped in by tests, without touching the runner or the checkers.
/// </summary>
public sealed class InstrumentedMonitorChecker(IMonitorChecker inner, MonitoringMetrics metrics, TimeProvider time) : IMonitorChecker
{
    public MonitorType Type => inner.Type;

    public async Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct)
    {
        using var activity = MonitoringMetrics.StartCheckActivity(monitor);
        var started = time.GetTimestamp();
        try
        {
            var outcome = await inner.CheckAsync(monitor, ct);
            metrics.RecordCheck(inner.Type, outcome.IsUp, time.GetElapsedTime(started));
            activity?.SetTag("check.result", MonitoringMetrics.ResultTag(outcome.IsUp));
            if (!outcome.IsUp) activity?.SetStatus(ActivityStatusCode.Error);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown / caller gave up: not a probe result
        }
        catch (Exception)
        {
            metrics.RecordCheck(inner.Type, null, time.GetElapsedTime(started));
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    /// <summary>Wraps every checker (already wrapped checkers are kept as they are).</summary>
    public static IReadOnlyList<IMonitorChecker> Wrap(IEnumerable<IMonitorChecker> checkers, MonitoringMetrics metrics, TimeProvider time) =>
        checkers.Select(c => c is InstrumentedMonitorChecker ? c : new InstrumentedMonitorChecker(c, metrics, time)).ToList();
}
