using System.Text.Json;
using System.Text.Json.Serialization;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Regions;

/// <summary>
/// FEAT-6: wire contract between the main app and probe agents (internal API, not part of the public REST API).
/// Pull based: the agent leases due probes (<see cref="LeasePath"/>), runs them and posts the outcomes
/// (<see cref="ResultsPath"/>), quoting each item's <see cref="LeaseItem.ItemId"/>. A lease carries only what the probe
/// needs - never customer / monitor ids or names, alert channels or other secrets.
/// </summary>
public static class AgentProtocol
{
    public const string BasePath = "/api/agent/v1";
    public const string LeasePath = BasePath + "/lease";
    public const string ResultsPath = BasePath + "/results";
    /// <summary>Region code the agent is configured for; must match the region of its credential.</summary>
    public const string RegionHeader = "X-TriggerCheck-Region";
    /// <summary>Marker of agent credentials: <c>tcr_&lt;prefix&gt;_&lt;secret&gt;</c>.</summary>
    public const string TokenMarker = "tcr";

    public const int MaxLeaseItems = 100;
    public const int MaxResultsPerRequest = 100;
    public const int MaxLeaseBodyBytes = 1024;
    public const int MaxResultsBodyBytes = 256 * 1024;
    public const int MaxItemIdLength = 64;
    public const int MaxErrorLength = 1000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Transient monitor the agent hands to the shared checkers (never persisted).</summary>
    public static MonitorEntity ToMonitor(LeaseItem item) => new()
    {
        Name = item.ItemId,
        Type = item.Type,
        Target = item.Target,
        Port = item.Port,
        Keyword = item.Keyword,
        ExpectedStatusCodes = item.ExpectedStatusCodes ?? "200-299",
        HttpMethod = item.HttpMethod ?? "GET",
        TimeoutSeconds = item.TimeoutSeconds,
        DnsRecordType = item.DnsRecordType,
        JsonPath = item.JsonPath,
        AssertionOperator = item.AssertionOperator,
        ExpectedValue = item.ExpectedValue
    };

    public static LeaseItem ToLeaseItem(MonitorEntity m, string itemId, DateTime expiresAtUtc) => new()
    {
        ItemId = itemId,
        Type = m.Type,
        Target = m.Target,
        Port = m.Port,
        Keyword = m.Keyword,
        ExpectedStatusCodes = m.ExpectedStatusCodes,
        HttpMethod = m.HttpMethod,
        TimeoutSeconds = m.TimeoutSeconds,
        DnsRecordType = m.DnsRecordType,
        JsonPath = m.JsonPath,
        AssertionOperator = m.AssertionOperator,
        ExpectedValue = m.ExpectedValue,
        ExpiresAtUtc = expiresAtUtc
    };

    public static ProbeResult ToResult(string itemId, CheckOutcome outcome, DateTime checkedAtUtc) => new()
    {
        ItemId = itemId,
        CheckedAtUtc = checkedAtUtc,
        IsUp = outcome.IsUp,
        StatusCode = outcome.StatusCode,
        ResponseTimeMs = outcome.ResponseTimeMs,
        Error = outcome.Error,
        DnsMs = outcome.Timings?.DnsMs,
        ConnectMs = outcome.Timings?.ConnectMs,
        TlsMs = outcome.Timings?.TlsMs,
        TtfbMs = outcome.Timings?.TtfbMs,
        Certificate = outcome.Certificate is { } c ? new ProbeCertificate { NotAfterUtc = c.NotAfterUtc, Subject = c.Subject, Issuer = c.Issuer, Error = c.Error } : null
    };
}

public sealed record LeaseRequest
{
    /// <summary>How many probes the agent can start now (1..<see cref="AgentProtocol.MaxLeaseItems"/>).</summary>
    public int MaxItems { get; init; } = 10;
}

public sealed record LeaseResponse
{
    public string LeaseId { get; init; } = "";
    public DateTime ServerTimeUtc { get; init; }
    public IReadOnlyList<LeaseItem> Items { get; init; } = [];
}

/// <summary>One probe to run. The settings mirror the monitor's probe settings; nothing else is sent.</summary>
public sealed record LeaseItem
{
    public string ItemId { get; init; } = "";
    public MonitorType Type { get; init; }
    public string Target { get; init; } = "";
    public int? Port { get; init; }
    public string? Keyword { get; init; }
    public string? ExpectedStatusCodes { get; init; }
    public string? HttpMethod { get; init; }
    public int TimeoutSeconds { get; init; }
    public DnsRecordType? DnsRecordType { get; init; }
    public string? JsonPath { get; init; }
    public AssertionOperator? AssertionOperator { get; init; }
    public string? ExpectedValue { get; init; }
    /// <summary>The result must arrive before this time, otherwise it is rejected as expired.</summary>
    public DateTime ExpiresAtUtc { get; init; }
}

public sealed record ResultsRequest
{
    public IReadOnlyList<ProbeResult> Results { get; init; } = [];
}

public sealed record ProbeResult
{
    public string ItemId { get; init; } = "";
    /// <summary>Probe start on the agent's clock (agents must be NTP synchronized; see docs/DEPLOYMENT.md).</summary>
    public DateTime CheckedAtUtc { get; init; }
    public bool IsUp { get; init; }
    public int? StatusCode { get; init; }
    public int ResponseTimeMs { get; init; }
    public string? Error { get; init; }
    public int? DnsMs { get; init; }
    public int? ConnectMs { get; init; }
    public int? TlsMs { get; init; }
    public int? TtfbMs { get; init; }
    /// <summary>Certificate seen by the agent (informational; certificate tracking and alerts use the central probe).</summary>
    public ProbeCertificate? Certificate { get; init; }
}

public sealed record ProbeCertificate
{
    public DateTime NotAfterUtc { get; init; }
    public string? Subject { get; init; }
    public string? Issuer { get; init; }
    public string? Error { get; init; }
}

public enum ResultStatus
{
    /// <summary>Recorded and applied.</summary>
    Accepted,
    /// <summary>This item's result was already accepted (replay / retry); ignored.</summary>
    Duplicate,
    /// <summary>Unknown item, or an item leased to another region or superseded by a newer lease; ignored.</summary>
    Rejected,
    /// <summary>The lease had expired; ignored (the probe is leased again).</summary>
    Expired,
    /// <summary>The monitor was deleted, paused or its probe settings changed after the lease; ignored.</summary>
    Discarded,
    /// <summary>Malformed result; ignored.</summary>
    Invalid
}

public sealed record ResultsResponse
{
    public IReadOnlyList<ResultAck> Results { get; init; } = [];
}

public sealed record ResultAck(string ItemId, ResultStatus Status);
