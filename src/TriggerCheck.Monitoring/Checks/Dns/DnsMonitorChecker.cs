using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Checks.Dns;

/// <summary>
/// Queries <see cref="MonitorEntity.DnsRecordType"/> records of the monitor's host name through the configured
/// resolvers (<see cref="MonitoringOptions.DnsResolvers"/>, tried in order: a timeout, network error, malformed answer,
/// SERVFAIL or REFUSED moves on to the next one; NOERROR and NXDOMAIN are final). Up when at least one record of the
/// type exists and, if <see cref="MonitorEntity.ExpectedValue"/> is set, one record equals / contains it
/// (case-insensitive, trailing dots ignored, IP addresses compared as addresses).
/// </summary>
public sealed class DnsMonitorChecker(IOptionsMonitor<MonitoringOptions> options, ILogger<DnsMonitorChecker> logger) : IMonitorChecker
{
    private const int MaxListedRecords = 5;

    public MonitorType Type => MonitorType.Dns;

    public async Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct)
    {
        var host = CheckTimeouts.NormalizeHost(monitor.Target);
        if (host is null) return CheckOutcome.Down("Host name is not configured");
        try { host = DnsMessage.NormalizeName(host); }
        catch (ArgumentException) { return CheckOutcome.Down($"Invalid host name '{host}'"); }
        if (monitor.DnsRecordType is not { } type || !Enum.IsDefined(type)) return CheckOutcome.Down("DNS record type is not configured");

        var resolvers = ParseResolvers(options.CurrentValue.DnsResolvers, logger);
        if (resolvers.Count == 0) return CheckOutcome.Down("No DNS resolver is configured");

        var timeout = CheckTimeouts.For(monitor);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        // Each resolver gets an equal share, so a dead first resolver still leaves time for the next one.
        var perAttempt = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, timeout.Ticks / resolvers.Count));
        var sw = Stopwatch.StartNew();
        string? lastError = null;
        foreach (var resolver in resolvers)
        {
            if (cts.IsCancellationRequested) break;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            attempt.CancelAfter(perAttempt);
            try
            {
                var answer = await DnsWireClient.QueryAsync(resolver, host, type, attempt.Token);
                if (answer.ResponseCode is 0 or 3)
                    return Evaluate(monitor, host, type, answer, Elapsed(sw));
                lastError = $"DNS query failed: {RcodeName(answer.ResponseCode)}";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"DNS query timed out after {perAttempt.TotalSeconds:0.#}s";
            }
            catch (SocketException ex)
            {
                lastError = "DNS resolver unreachable";
                logger.LogDebug(ex, "DNS resolver {Resolver} failed for monitor {MonitorId}", resolver, monitor.Id);
            }
            catch (DnsFormatException ex)
            {
                lastError = "Malformed DNS response: " + ex.Message;
            }
            catch (IOException ex)
            {
                lastError = "DNS resolver closed the connection";
                logger.LogDebug(ex, "DNS resolver {Resolver} failed for monitor {MonitorId}", resolver, monitor.Id);
            }
        }
        return CheckOutcome.Down(lastError ?? $"Timeout after {timeout.TotalSeconds:0.#}s", Elapsed(sw));
    }

    private static CheckOutcome Evaluate(MonitorEntity monitor, string host, DnsRecordType type, DnsAnswer answer, int ms)
    {
        if (answer.ResponseCode == 3) return CheckOutcome.Down($"DNS name {host} does not exist (NXDOMAIN)", ms);
        if (answer.Records.Count == 0) return CheckOutcome.Down($"No {type} records found for {host}", ms);

        var expected = monitor.ExpectedValue?.Trim();
        if (string.IsNullOrEmpty(expected)) return CheckOutcome.Up(ms);

        var contains = monitor.AssertionOperator == AssertionOperator.Contains;
        if (answer.Records.Any(r => Matches(r.Value, expected, contains))) return CheckOutcome.Up(ms);

        var found = string.Join(", ", answer.Records.Take(MaxListedRecords).Select(r => r.Value))
                    + (answer.Records.Count > MaxListedRecords ? ", …" : "");
        return CheckOutcome.Down($"No {type} record {(contains ? "contains" : "equals")} '{expected}' (found: {found})", ms);
    }

    public static bool Matches(string value, string expected, bool contains)
    {
        if (contains) return value.Contains(expected, StringComparison.OrdinalIgnoreCase);
        if (IPAddress.TryParse(value, out var a) && IPAddress.TryParse(expected, out var b)) return a.Equals(b);
        return string.Equals(value.TrimEnd('.'), expected.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses the configured resolver list (invalid entries are skipped with a warning); defaults when empty.</summary>
    public static IReadOnlyList<IPEndPoint> ParseResolvers(string[]? configured, ILogger? logger = null)
    {
        var entries = configured is { Length: > 0 } ? configured : MonitoringOptions.DefaultDnsResolvers;
        var list = new List<IPEndPoint>();
        foreach (var raw in entries)
        {
            var s = raw?.Trim() ?? "";
            if (IPAddress.TryParse(s, out var ip) && !s.StartsWith('[')) list.Add(new IPEndPoint(ip, 53));
            else if (IPEndPoint.TryParse(s, out var ep)) list.Add(ep.Port == 0 ? new IPEndPoint(ep.Address, 53) : ep);
            else logger?.LogWarning("Ignoring invalid Monitoring:DnsResolvers entry {Entry}", s);
        }
        return list;
    }

    private static string RcodeName(int rcode) => rcode switch
    {
        1 => "FORMERR",
        2 => "SERVFAIL",
        4 => "NOTIMP",
        5 => "REFUSED",
        _ => "RCODE " + rcode
    };

    private static int Elapsed(Stopwatch sw) => (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
}
