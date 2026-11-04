using System.Text.RegularExpressions;

namespace TriggerCheck.Ai.Features;

/// <summary>
/// Which input keys may support a hypothesis of a given category (AI-2 / AI-3). Generic keys such as "started_at",
/// "interval_seconds", "current_status" or "target_host" exist in every context, so they never ground a cause on their
/// own: a DNS cause needs a failing check or its error text, a TLS cause the certificate facts, a host-wide outage the
/// other monitors on the host, a slowdown the matching phase, and so on. "unknown" accepts any key.
/// Used by the interpreter's contract step and by the eval grader (same rule).
/// </summary>
public static partial class AiEvidenceRules
{
    [GeneratedRegex(@"^check #\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex Check();

    [GeneratedRegex(@"^error E\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex Error();

    [GeneratedRegex(@"^monitor [B-Z]$", RegexOptions.IgnoreCase)]
    private static partial Regex OtherMonitor();

    private static bool Any(string key, params string[] names) => names.Any(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase));

    private static bool CheckOrError(string key) => Check().IsMatch(key) || Error().IsMatch(key);

    public static bool IsAllowed(string category, string key) => category switch
    {
        "unknown" => true,
        "dns" => CheckOrError(key) || key.StartsWith("dns_", StringComparison.OrdinalIgnoreCase),
        "tls_expired" or "tls_name_mismatch" or "tls_untrusted" =>
            Error().IsMatch(key) || Any(key, "validation_error", "expires_at", "days_left_at_check", "checked_at"),
        "http_5xx" or "http_4xx" or "connection_refused" or "network_unreachable" => CheckOrError(key),
        "timeout" => CheckOrError(key) || Any(key, "timeout_seconds"),
        "keyword_missing" => CheckOrError(key) || Any(key, "content_check"),
        "host_outage" => OtherMonitor().IsMatch(key),
        "maintenance_related" => Check().IsMatch(key) || Any(key, "period", "resolved_during_maintenance"),
        "slow_response" => Check().IsMatch(key) || Any(key, "median_ms", "signal"),
        "config_change" => Any(key, "monitor_created_at", "expected_status_codes", "timeout_seconds", "failure_threshold",
            "interval_seconds", "content_check", "dns_record_type", "expected_value_configured"),
        "heartbeat_missed" => Check().IsMatch(key) || Any(key, "push_monitor", "interval_seconds"),
        "slow_dns" => Any(key, "dns"),
        "slow_connect" => Any(key, "tcp_connect"),
        "slow_tls" => Any(key, "tls"),
        "slow_ttfb" => Any(key, "ttfb"),
        _ => false,
    };
}
