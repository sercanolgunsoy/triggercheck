using System.Net;
using System.Text.RegularExpressions;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Monitoring.Correlation;

/// <summary>How strongly two incidents are linked by the L1 rules (AI-4).</summary>
public enum LinkStrength
{
    None = 0,
    /// <summary>A weaker shared signal (same error type, related domain, same host with unrelated errors): the LLM or the user decides.</summary>
    Ambiguous = 1,
    /// <summary>Same host / IP with compatible errors: grouped by the rule alone.</summary>
    Rule = 2,
}

/// <param name="Host">Host name or IP of the target (lower case); null for heartbeat monitors and unparsable targets.</param>
/// <param name="ErrorType">Coarse error type from <see cref="CorrelationRules.ErrorTypeOf"/>.</param>
public sealed record CorrelationIncident(int IncidentId, int MonitorId, MonitorType Type, string? Host, string ErrorType, DateTime StartedAtUtc);

public sealed record CorrelationLink(LinkStrength Strength, IReadOnlyList<string> Signals)
{
    public static readonly CorrelationLink None = new(LinkStrength.None, []);
}

/// <summary>
/// A candidate group: the earliest incident (<see cref="Primary"/>), the incidents linked to it by a rule, and the
/// incidents that share only a weaker signal (<see cref="Ambiguous"/>).
/// </summary>
public sealed record CorrelationCluster(
    CorrelationIncident Primary,
    IReadOnlyList<CorrelationIncident> RuleMembers,
    IReadOnlyList<CorrelationIncident> Ambiguous,
    IReadOnlyList<string> Signals,
    string? Host);

/// <summary>
/// AI-4 L1 correlation rules: deterministic, no LLM, cheap. Two incidents of one customer are only ever compared when
/// they started within <c>window</c> of each other; on top of the time window they need a shared signal:
/// <list type="bullet">
/// <item><b>Rule</b>: the same host or IP and errors of the same family (both network-level: DNS, timeout, refused,
/// unreachable, TLS; or both application-level: HTTP 5xx / 4xx, content);</item>
/// <item><b>Ambiguous</b>: the same host but errors of different families; or different hosts with the same specific
/// error type (same parent domain, or a network-level / 5xx error everywhere). Only the LLM (explaining) or the user
/// (A1) adds these to a group, never the rules;</item>
/// <item>anything else (only the time window in common, heartbeats, unknown errors on different hosts): not linked.</item>
/// </list>
/// Callers must pass incidents of a single customer only (tenant isolation is the caller's filter).
/// </summary>
public static partial class CorrelationRules
{
    public const string SameHost = "same_host", SameIp = "same_ip", SameDomain = "same_domain", SameErrorType = "same_error_type", SameWindow = "same_window";

    public const string Dns = "dns", Timeout = "timeout", Connection = "connection_refused", Unreachable = "unreachable", Tls = "tls",
        Http5xx = "http_5xx", Http4xx = "http_4xx", Content = "content", Heartbeat = "heartbeat", Other = "other";

    /// <summary>Ambiguous candidates per cluster at most (bounds the LLM context and the proposal).</summary>
    public const int MaxAmbiguous = 10;

    /// <summary>Error types specific enough to suggest a shared cause across different hosts.</summary>
    private static readonly HashSet<string> CrossHostTypes = [Dns, Timeout, Connection, Unreachable, Tls, Http5xx];

    /// <summary>"network", "application" or null (heartbeat / other: no family).</summary>
    public static string? FamilyOf(string errorType) => errorType switch
    {
        Dns or Timeout or Connection or Unreachable or Tls => "network",
        Http5xx or Http4xx or Content => "application",
        _ => null,
    };

    [GeneratedRegex(@"status code (\d)\d\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatusCode();

    /// <summary>
    /// Coarse error type of an incident from its cause (the probe's own error texts, <see cref="Checks.ProbeErrors"/>).
    /// Unknown texts are <see cref="Other"/>, which never links incidents on different hosts.
    /// </summary>
    public static string ErrorTypeOf(MonitorType type, string? cause)
    {
        if (type == MonitorType.Heartbeat) return Heartbeat;
        var c = (cause ?? "").ToLowerInvariant();
        if (c.Length == 0) return Other;
        if (c.Contains("dns resolution failed") || c.Contains("nxdomain") || c.Contains("name resolution")) return Dns;
        if (c.Contains("tls/ssl error") || c.Contains("certificate")) return Tls;
        if (c.Contains("timeout after") || c.Contains("timed out")) return Timeout;
        if (c.Contains("connection refused") || c.Contains("connection reset")) return Connection;
        if (c.Contains("host unreachable") || c.Contains("network unreachable") || c.Contains("ping failed")) return Unreachable;
        if (StatusCode().Match(c) is { Success: true } m) return m.Groups[1].Value switch { "5" => Http5xx, "4" => Http4xx, _ => Other };
        if (c.Contains("keyword") || c.Contains("json")) return Content;
        return Other;
    }

    /// <summary>Host name (or IP) of a monitor target, lower case; null for heartbeats and unparsable targets.</summary>
    public static string? HostOf(MonitorType type, string? target) => type switch
    {
        MonitorType.Http or MonitorType.Keyword or MonitorType.JsonApi => Uri.TryCreate(target, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null,
        MonitorType.Heartbeat => null,
        _ => string.IsNullOrWhiteSpace(target) ? null : target.Trim().TrimEnd('.').ToLowerInvariant(),
    };

    public static bool IsIp(string host) => IPAddress.TryParse(host.Trim('[', ']'), out _);

    /// <summary>The last two labels of a host name ("api.shop.example.test" → "example.test"); null for IPs and single labels.</summary>
    public static string? ParentDomainOf(string? host)
    {
        if (string.IsNullOrEmpty(host) || IsIp(host)) return null;
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length < 2 ? null : labels[^2] + "." + labels[^1];
    }

    public static CorrelationIncident Describe(int incidentId, int monitorId, MonitorType type, string? target, string? cause, DateTime startedAtUtc) =>
        new(incidentId, monitorId, type, HostOf(type, target), ErrorTypeOf(type, cause), startedAtUtc);

    /// <summary>How two incidents of the same customer are linked (symmetric).</summary>
    public static CorrelationLink Link(CorrelationIncident a, CorrelationIncident b, TimeSpan window)
    {
        if (a.IncidentId == b.IncidentId || a.MonitorId == b.MonitorId) return CorrelationLink.None;
        if ((a.StartedAtUtc - b.StartedAtUtc).Duration() > window) return CorrelationLink.None;
        if (a.Host is null || b.Host is null) return CorrelationLink.None;

        var sameType = a.ErrorType == b.ErrorType && a.ErrorType != Other;
        if (string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase))
        {
            var hostSignal = IsIp(a.Host) ? SameIp : SameHost;
            var family = FamilyOf(a.ErrorType);
            if (sameType) return new(LinkStrength.Rule, [hostSignal, SameErrorType, SameWindow]);
            if (family is not null && family == FamilyOf(b.ErrorType)) return new(LinkStrength.Rule, [hostSignal, SameWindow]);
            return new(LinkStrength.Ambiguous, [hostSignal, SameWindow]);
        }
        if (!sameType) return CorrelationLink.None;
        var domain = ParentDomainOf(a.Host);
        if (domain is not null && domain == ParentDomainOf(b.Host)) return new(LinkStrength.Ambiguous, [SameDomain, SameErrorType, SameWindow]);
        return CrossHostTypes.Contains(a.ErrorType) ? new(LinkStrength.Ambiguous, [SameErrorType, SameWindow]) : CorrelationLink.None;
    }

    /// <summary>
    /// Splits incidents of one customer into clusters. Deterministic (ordered by start time, then id): the earliest
    /// ungrouped incident becomes a primary; every incident that started at most <paramref name="window"/> after it and
    /// is rule-linked to the primary or to a rule member joins it; then incidents in the same window with an ambiguous
    /// link to any of them are attached as candidates (at most <see cref="MaxAmbiguous"/>). A cluster needs at least one
    /// rule member: shared weak signals alone are never proposed. Every incident is in at most one cluster.
    /// </summary>
    public static IReadOnlyList<CorrelationCluster> Cluster(IEnumerable<CorrelationIncident> incidents, TimeSpan window)
    {
        var remaining = incidents.DistinctBy(i => i.IncidentId).OrderBy(i => i.StartedAtUtc).ThenBy(i => i.IncidentId).ToList();
        var result = new List<CorrelationCluster>();
        while (remaining.Count > 0)
        {
            var primary = remaining[0];
            remaining.RemoveAt(0);
            var inWindow = remaining.Where(x => x.StartedAtUtc - primary.StartedAtUtc <= window).ToList();
            var core = new List<CorrelationIncident> { primary };
            var signals = new List<string>();
            bool added;
            do
            {
                added = false;
                foreach (var x in inWindow.Where(x => !core.Contains(x)).ToList())
                {
                    var link = core.Select(c => Link(c, x, window)).FirstOrDefault(l => l.Strength == LinkStrength.Rule);
                    if (link is null) continue;
                    core.Add(x);
                    signals.AddRange(link.Signals);
                    added = true;
                }
            } while (added);
            if (core.Count < 2) continue;

            foreach (var c in core) remaining.Remove(c);
            var ambiguous = remaining
                .Where(x => x.StartedAtUtc - primary.StartedAtUtc <= window && core.Any(c => Link(c, x, window).Strength == LinkStrength.Ambiguous))
                .Take(MaxAmbiguous).ToList();
            foreach (var a in ambiguous) remaining.Remove(a);

            var members = core.Skip(1).ToList();
            var host = members.All(m => string.Equals(m.Host, primary.Host, StringComparison.OrdinalIgnoreCase)) ? primary.Host : null;
            result.Add(new CorrelationCluster(primary, members, ambiguous, OrderSignals(signals), host));
        }
        return result;
    }

    private static readonly string[] SignalOrder = [SameHost, SameIp, SameDomain, SameErrorType, SameWindow];

    /// <summary>Distinct signals in a stable order.</summary>
    public static IReadOnlyList<string> OrderSignals(IEnumerable<string> signals)
    {
        var set = signals.ToHashSet();
        return SignalOrder.Where(set.Contains).ToList();
    }
}
