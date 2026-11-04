namespace TriggerCheck.Ai.Features;

/// <summary>
/// Static, feature-specific instructions and action allow-lists (AI-2, AI-3). Appended to the shared system prompt
/// (<see cref="Execution.AiPrompts"/>) and kept byte-stable so the prefix is cached; per-call data goes in the user
/// turn. Any change here is a prompt change: run the eval (evals/README.md) before merging.
/// </summary>
public static class AiFeaturePrompts
{
    public const string IncidentInstructions = """
        Explain one incident (a period in which a monitor was DOWN) to the customer's operations team.

        Input: the monitor's configuration, the incident, the newest probe results ("check #<id>", newest first, with
        status code, total time and the phase timings DNS / TCP connect / TLS / TTFB in ms), the TLS certificate, other
        monitors on the same host, slowdown signals from the statistical baseline and maintenance periods.

        How to work:
        - Decide which layer failed first: DNS resolution, TCP connection (refused / unreachable), TLS (expired,
          untrusted or wrong-name certificate, handshake failure), timeout, HTTP status (4xx / 5xx) or response content
          (expected keyword missing). Name that layer explicitly in the cause of the first hypothesis.
        - The first hypothesis is the most likely primary cause. Cite the checks or metrics that show it in its
          evidence, for example "check #1234: status 503" or "check #1240: DNS 2400 ms, 12 ms before the incident".
        - Other monitors on the same host that are also down point to a host-wide outage; healthy ones point to a
          problem specific to this endpoint.
        - A slowdown signal shortly before the incident suggests overload or degradation; mention it only when the
          data shows it.
        - Checks marked "maintenance=yes" ran in a planned maintenance window. Mention it when the incident starts
          right after or ends right before a maintenance period.
        - event "incident_opened": the incident is ongoing; describe what is failing now. event "incident_resolved":
          also say how long it lasted and what the recovering check shows.
        - Keep the summary short and factual. Suggested actions are optional, only from the allowed list, and
          described concretely for this incident.
        """;

    public const string SlowdownInstructions = """
        Explain a slowdown detected by the statistical baseline (a response time anomaly) on a monitor that has not
        failed.

        Input: the monitor, the anomaly signal (start time, score, the newest checks compared with the baseline
        median) and, per phase (DNS, TCP connect, TLS, TTFB, total), the median in the 24 hours before the slowdown
        and the median since it started, with their ratio.

        How to work:
        - Say which phase degraded the most, by how much (use the given milliseconds and ratios; do not compute new
          numbers) and since when. Name that phase explicitly in the cause of the first hypothesis.
        - Unless the input shows an open incident, state clearly in the summary that there is no outage yet.
        - Growth in DNS, TCP connect or TLS points to the network, the name servers or the TLS termination; growth in
          TTFB with stable connection phases points to the server or the application.
        - When phase data is missing (for example because connections were reused), list it under unknowns.
        - Suggested actions are optional, only from the allowed list.
        """;

    /// <summary>A0 (information only): actions are suggestions to a human; nothing is ever executed.</summary>
    public static readonly IReadOnlyList<string> IncidentActions =
    [
        "check_dns_configuration",
        "renew_tls_certificate",
        "check_tls_configuration",
        "check_server_health",
        "check_firewall_and_ports",
        "check_recent_deployment",
        "verify_expected_content",
        "review_timeout_setting",
        "review_failure_threshold",
        "contact_hosting_provider",
        "schedule_maintenance_window",
    ];

    public static readonly IReadOnlyList<string> SlowdownActions =
    [
        "check_dns_configuration",
        "check_tls_configuration",
        "check_server_health",
        "check_network_path",
        "check_recent_deployment",
        "review_timeout_setting",
    ];
}
