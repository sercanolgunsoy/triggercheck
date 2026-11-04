using TriggerCheck.Domain.Entities;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Checks;

/// <summary>Result of a single probe.</summary>
public sealed record CheckOutcome(bool IsUp, int? StatusCode, int ResponseTimeMs, string? Error)
{
    /// <summary>HTTP/Keyword only: phase breakdown of the probe.</summary>
    public ProbeTimings? Timings { get; init; }
    /// <summary>Https only: server certificate seen on a new connection (null when a pooled connection was reused).</summary>
    public CertificateInfo? Certificate { get; init; }

    public static CheckOutcome Up(int responseTimeMs, int? statusCode = null) => new(true, statusCode, responseTimeMs, null);
    public static CheckOutcome Down(string error, int responseTimeMs = 0, int? statusCode = null) =>
        new(false, statusCode, responseTimeMs, ProbeErrors.Truncate(error));
}

/// <summary>
/// Executes one probe for one <see cref="MonitorType"/>. Implementations must never throw for target-side problems
/// (DNS, timeout, TLS, refused, bad config...) - those become <c>IsUp=false</c> with a short readable error.
/// Only cancellation of the caller's token is allowed to propagate (as <see cref="OperationCanceledException"/>).
/// </summary>
public interface IMonitorChecker
{
    MonitorType Type { get; }
    Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct);
}
