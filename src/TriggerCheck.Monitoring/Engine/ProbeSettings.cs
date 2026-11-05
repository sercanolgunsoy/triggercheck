using TriggerCheck.Domain.Entities;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Engine;

/// <summary>
/// The monitor settings that determine what a probe actually checks. If any of them changes, earlier results no
/// longer describe the monitor: the runner discards an in-flight result and the Edit page resets the failure streak.
/// </summary>
public readonly record struct ProbeSettings(
    MonitorType Type, string Target, int? Port, string? Keyword, string ExpectedStatusCodes, string HttpMethod, int TimeoutSeconds,
    DnsRecordType? DnsRecordType, string? JsonPath, AssertionOperator? AssertionOperator, string? ExpectedValue, int GracePeriodSeconds)
{
    public static ProbeSettings Of(MonitorEntity m) =>
        new(m.Type, m.Target, m.Port, m.Keyword, m.ExpectedStatusCodes, m.HttpMethod, m.TimeoutSeconds,
            m.DnsRecordType, m.JsonPath, m.AssertionOperator, m.ExpectedValue, m.GracePeriodSeconds);
}
