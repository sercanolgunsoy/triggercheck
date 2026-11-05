using TriggerCheck.Ai.Features.Correlation;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Correlation;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>AI-4 L1 rules: error types, links, clustering, and the reading of the model's verdict. Pure functions.</summary>
public class CorrelationRulesTests
{
    private static readonly DateTime T0 = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan W = TimeSpan.FromMinutes(5);

    private static CorrelationIncident I(int id, string target, string cause, double secondsAfter = 0, MonitorType type = MonitorType.Http) =>
        CorrelationRules.Describe(id, 100 + id, type, target, cause, T0.AddSeconds(secondsAfter));

    [Theory]
    [InlineData("DNS resolution failed: No such host is known", CorrelationRules.Dns)]
    [InlineData("Timeout after 10s", CorrelationRules.Timeout)]
    [InlineData("Connection timed out", CorrelationRules.Timeout)]
    [InlineData("Connection refused", CorrelationRules.Connection)]
    [InlineData("Connection reset by peer", CorrelationRules.Connection)]
    [InlineData("Host unreachable", CorrelationRules.Unreachable)]
    [InlineData("TLS/SSL error: The remote certificate is invalid", CorrelationRules.Tls)]
    [InlineData("Unexpected status code 503 (expected 200-299)", CorrelationRules.Http5xx)]
    [InlineData("Unexpected status code 404 (expected 200-299)", CorrelationRules.Http4xx)]
    [InlineData("Keyword 'Checkout' not found in response", CorrelationRules.Content)]
    [InlineData("Something odd", CorrelationRules.Other)]
    [InlineData(null, CorrelationRules.Other)]
    public void Error_type_is_derived_from_the_probe_error(string? cause, string expected) =>
        Assert.Equal(expected, CorrelationRules.ErrorTypeOf(MonitorType.Http, cause));

    [Fact]
    public void Heartbeats_have_no_host_and_their_own_error_type()
    {
        var hb = I(1, "", "No heartbeat received yet", type: MonitorType.Heartbeat);
        Assert.Null(hb.Host);
        Assert.Equal(CorrelationRules.Heartbeat, hb.ErrorType);
        Assert.Equal(LinkStrength.None, CorrelationRules.Link(hb, I(2, "https://a.example.test/", "Timeout after 10s"), W).Strength);
    }

    [Fact]
    public void Same_host_and_same_error_type_is_a_rule_link()
    {
        var link = CorrelationRules.Link(I(1, "https://api.example.test/a", "Timeout after 10s"), I(2, "https://api.example.test/b?x=1", "Timeout after 10s", 40), W);
        Assert.Equal(LinkStrength.Rule, link.Strength);
        Assert.Equal([CorrelationRules.SameHost, CorrelationRules.SameErrorType, CorrelationRules.SameWindow], link.Signals);
    }

    [Fact]
    public void Same_host_with_errors_of_the_same_family_is_a_rule_link()
    {
        // A dying host: one probe times out, another is refused.
        var link = CorrelationRules.Link(I(1, "https://api.example.test/", "Timeout after 10s"), I(2, "https://api.example.test/x", "Connection refused", 30), W);
        Assert.Equal(LinkStrength.Rule, link.Strength);
        Assert.DoesNotContain(CorrelationRules.SameErrorType, link.Signals);
    }

    [Fact]
    public void Same_ip_across_monitor_types_is_a_rule_link()
    {
        var tcp = I(1, "10.0.0.5", "Connection refused", type: MonitorType.Tcp);
        var http = I(2, "http://10.0.0.5:8080/health", "Connection refused", 20);
        var link = CorrelationRules.Link(tcp, http, W);
        Assert.Equal(LinkStrength.Rule, link.Strength);
        Assert.Contains(CorrelationRules.SameIp, link.Signals);
    }

    [Fact]
    public void Same_host_with_unrelated_errors_is_ambiguous()
    {
        var link = CorrelationRules.Link(I(1, "https://api.example.test/", "Timeout after 10s"), I(2, "https://api.example.test/x", "Unexpected status code 404 (expected 200-299)", 30), W);
        Assert.Equal(LinkStrength.Ambiguous, link.Strength);
    }

    [Fact]
    public void Same_parent_domain_and_error_type_is_ambiguous()
    {
        var link = CorrelationRules.Link(I(1, "https://api.example.test/", "Unexpected status code 502 (expected 200-299)"), I(2, "https://www.example.test/", "Unexpected status code 503 (expected 200-299)", 30), W);
        Assert.Equal(LinkStrength.Ambiguous, link.Strength);
        Assert.Contains(CorrelationRules.SameDomain, link.Signals);
    }

    [Fact]
    public void Different_domains_link_only_by_a_specific_error_type()
    {
        Assert.Equal(LinkStrength.Ambiguous, CorrelationRules.Link(I(1, "https://a.one.test/", "DNS resolution failed: x"), I(2, "https://b.two.test/", "DNS resolution failed: y", 10), W).Strength);
        // 4xx on unrelated sites and unknown errors are not a shared signal.
        Assert.Equal(LinkStrength.None, CorrelationRules.Link(I(1, "https://a.one.test/", "Unexpected status code 404 (expected 200-299)"), I(2, "https://b.two.test/", "Unexpected status code 404 (expected 200-299)", 10), W).Strength);
        Assert.Equal(LinkStrength.None, CorrelationRules.Link(I(1, "https://a.one.test/", "odd"), I(2, "https://b.two.test/", "odd", 10), W).Strength);
        // Only the time window in common: not linked.
        Assert.Equal(LinkStrength.None, CorrelationRules.Link(I(1, "https://a.one.test/", "Timeout after 10s"), I(2, "https://b.two.test/", "Connection refused", 10), W).Strength);
    }

    [Fact]
    public void Outside_the_window_or_same_monitor_is_never_linked()
    {
        Assert.Equal(LinkStrength.None, CorrelationRules.Link(I(1, "https://api.example.test/", "Timeout after 10s"), I(2, "https://api.example.test/", "Timeout after 10s", 301), W).Strength);
        var a = I(1, "https://api.example.test/", "Timeout after 10s");
        Assert.Equal(LinkStrength.None, CorrelationRules.Link(a, a with { IncidentId = 2 }, W).Strength);
    }

    [Fact]
    public void Cluster_groups_a_host_outage_and_leaves_unrelated_incidents_alone()
    {
        var incidents = new[]
        {
            I(1, "https://api.example.test/a", "Timeout after 10s", 0),
            I(2, "https://api.example.test/b", "Timeout after 10s", 30),
            I(3, "https://api.example.test/c", "Connection refused", 60),
            I(4, "https://other.test/", "Unexpected status code 404 (expected 200-299)", 20),   // unrelated
            I(5, "https://www.example.test/", "Timeout after 10s", 90),                        // same domain + error: candidate
            I(6, "https://api.example.test/late", "Timeout after 10s", 400),                   // outside the window
        };
        var clusters = CorrelationRules.Cluster(incidents, W);

        var c = Assert.Single(clusters);
        Assert.Equal(1, c.Primary.IncidentId);
        Assert.Equal([2, 3], c.RuleMembers.Select(m => m.IncidentId));
        Assert.Equal([5], c.Ambiguous.Select(m => m.IncidentId));
        Assert.Equal("api.example.test", c.Host);
        Assert.Contains(CorrelationRules.SameHost, c.Signals);
    }

    [Fact]
    public void Cluster_never_puts_an_incident_in_two_groups_and_needs_a_rule_member()
    {
        var incidents = new[]
        {
            I(1, "https://a.example.test/", "Timeout after 10s", 0),
            I(2, "https://a.example.test/", "Timeout after 10s", 10),
            I(3, "https://b.other.test/", "Connection refused", 20),
            I(4, "https://b.other.test/", "Connection refused", 30),
            I(5, "https://c.third.test/", "Timeout after 10s", 40), // only an ambiguous link: never a group of its own
        };
        var clusters = CorrelationRules.Cluster(incidents, W);
        Assert.Equal(2, clusters.Count);
        var all = clusters.SelectMany(c => new[] { c.Primary }.Concat(c.RuleMembers).Concat(c.Ambiguous)).Select(i => i.IncidentId).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.DoesNotContain(clusters, c => c.RuleMembers.Count == 0);
        Assert.Equal([5], clusters[0].Ambiguous.Select(a => a.IncidentId)); // attached to the first group it is linked to
    }

    [Fact]
    public void Cluster_window_is_anchored_at_the_primary_so_groups_do_not_chain_forever()
    {
        var incidents = Enumerable.Range(1, 10).Select(i => I(i, "https://api.example.test/", "Timeout after 10s", (i - 1) * 120)).ToList();
        var clusters = CorrelationRules.Cluster(incidents, W);
        Assert.All(clusters, c => Assert.All(c.RuleMembers, m => Assert.True(m.StartedAtUtc - c.Primary.StartedAtUtc <= W)));
        // 0-240 s, 360-600 s, 720-960 s; the last one (1080 s) is alone and stays ungrouped.
        Assert.Equal([1, 4, 7], clusters.Select(c => c.Primary.IncidentId));
        Assert.Equal(9, clusters.Sum(c => 1 + c.RuleMembers.Count));
    }

    [Fact]
    public void Ambiguous_candidates_are_capped()
    {
        var incidents = new List<CorrelationIncident> { I(1, "https://api.example.test/", "Timeout after 10s"), I(2, "https://api.example.test/", "Timeout after 10s", 1) };
        incidents.AddRange(Enumerable.Range(10, 20).Select(i => I(i, $"https://h{i}.example.test/", "Timeout after 10s", 5 + i)));
        var c = Assert.Single(CorrelationRules.Cluster(incidents, W));
        Assert.Equal(CorrelationRules.MaxAmbiguous, c.Ambiguous.Count);
    }

    // ---- the model's verdict on candidates --------------------------------------------------------------------

    private static AiHypothesis H(string category, AiConfidence confidence, params string[] refs) =>
        new("x", confidence, ["e"], category, refs);

    private static AiInterpretation Out(params AiHypothesis[] h) => new("s", h, [], []);

    private static readonly Dictionary<string, int> Candidates = new() { ["incident C"] = 30, ["incident D"] = 40 };

    [Fact]
    public void Candidates_are_kept_only_on_a_confident_shared_cause_verdict()
    {
        Assert.Equal([30], CorrelationPrompts.Recommended(Out(H("shared_cause", AiConfidence.High, "incident C", "incident A"), H("independent", AiConfidence.High, "incident D")), Candidates));
        Assert.Empty(CorrelationPrompts.Recommended(Out(H("shared_cause", AiConfidence.Low, "incident C")), Candidates));
        // Contradicting verdicts of equal confidence: left out (keeps its own notification).
        Assert.Empty(CorrelationPrompts.Recommended(Out(H("shared_cause", AiConfidence.Medium, "incident C"), H("unclear", AiConfidence.Medium, "incident C")), Candidates));
        Assert.Empty(CorrelationPrompts.Recommended(null, Candidates));
        Assert.Empty(CorrelationPrompts.Recommended(Out(), Candidates));
    }

    [Fact]
    public void Only_incident_keys_ground_a_verdict()
    {
        Assert.True(CorrelationPrompts.IsAllowed("shared_cause", "incident B"));
        Assert.True(CorrelationPrompts.IsAllowed("independent", "error C"));
        Assert.False(CorrelationPrompts.IsAllowed("shared_cause", "rule_signals"));
        Assert.True(CorrelationPrompts.IsAllowed("unclear", "start_spread_seconds"));
    }
}
