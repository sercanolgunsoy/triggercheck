using System.Text.Json;
using TriggerCheck.Ai.Narrative;

namespace TriggerCheck.Tests.Ai.Narrative;

/// <summary>
/// AI-6 structural "no numbers" contract: the model's text may contain no number at all; facts appear only as
/// placeholders the server renders. Includes every S1 / S2 probe of the AI-6 re-review (all must be rejected) and the
/// placeholder forms of the same attacks.
/// </summary>
public class NarrativeContractTests
{
    private static NarrativeValue P(double x) => new(NarrativeValueKind.Percent, x);
    private static NarrativeValue D(double x) => new(NarrativeValueKind.Duration, x);
    private static NarrativeValue C(double x) => new(NarrativeValueKind.Count, x);

    /// <summary>Scenario 2 of the review: A missed (99.86 vs 99.9, certificate expired 5 days before the end), B missed, C met.</summary>
    private static readonly Dictionary<string, NarrativeValue> Values = new()
    {
        ["period.name"] = new(NarrativeValueKind.Period, Text: "2026-03"),
        ["period.previous_name"] = new(NarrativeValueKind.Period, Text: "2026-02"),
        ["total.uptime"] = P(99.71), ["total.downtime"] = D(390.5), ["total.incidents"] = C(7),
        ["prev.uptime"] = P(99.95), ["prev.incidents"] = C(3),
        ["change.incidents"] = new(NarrativeValueKind.ChangeCount, 4), ["change.downtime"] = new(NarrativeValueKind.ChangeDuration, 326.5),
        ["change.uptime"] = new(NarrativeValueKind.ChangePercentagePoints, -0.24),
        ["A.uptime"] = P(99.86), ["A.sla_target"] = P(99.9), ["A.sla_status"] = new(NarrativeValueKind.SlaStatus, Text: "missed"),
        ["A.downtime"] = D(62.5), ["A.incidents"] = C(2), ["A.prev.uptime"] = P(100), ["A.cert_expiry"] = new(NarrativeValueKind.CertificateExpiry, -5),
        ["B.uptime"] = P(99.3), ["B.sla_target"] = P(99.5), ["B.sla_status"] = new(NarrativeValueKind.SlaStatus, Text: "missed"),
        ["B.downtime"] = D(312), ["B.incidents"] = C(4),
        ["C.uptime"] = P(100), ["C.sla_target"] = P(99), ["C.sla_status"] = new(NarrativeValueKind.SlaStatus, Text: "met"),
    };

    private static readonly Dictionary<string, int> Labels = new() { ["monitor A"] = 1, ["monitor B"] = 2, ["monitor C"] = 3 };

    private static NarrativeOutputContract Contract(string language = "en") => new(Values, Labels, language);

    /// <summary>Every attack line of the review probe (Program.cs, S1 and S2): all contain numbers, number words, SLA verdicts or links.</summary>
    public static TheoryData<string> ReviewProbeAttacks => new()
    {
        "monitor A had 100% uptime in February and met its 99.9% SLA.",
        "monitor A had one hundred percent uptime in February.",
        "monitor A had zero minutes of downtime.",
        "monitor A had seven incidents lasting a full day.",
        "Downtime fell by two days.",
        "monitor A had 0 incidents in February.",
        "monitor A met its SLA target; nothing was missed.",
        "monitor A kept its SLA in February.",
        "monitor A stayed within its 99.9% target.",
        "monitor A hit its SLA target.",
        "Monitor A's uptime was perfect.",
        "MONITOR A met its SLA target.",
        "monitor-A met its SLA target.",
        "Monitors A met the SLA target.",
        "monitor A had 5 incidents.",
        "monitor A was down for 1 day.",
        "monitor A had 1,000 incidents.",
        "Downtime improved by 75 minutes.",
        "Incidents were cut by 1.",
        "Uptime rose 0.186 points.",
        "Uptime was 100% in January and stayed the same in February.",
        "monitor A had -75 minutes of downtime.",
        "monitor A was the 3rd slowest.",
        "monitor A had half of the incidents.",
        "monitor A was twice as slow as in January.",
        "monitor A was x75 slower.",
        "monitor A returned http/500 errors for 75 minutes.",
        "monitor A uptime: %99,8'lik.",
        "monitor A ½ of the time.",
        "monitor A was 99.9% available, right at its target.",
        "monitor A SLA hedefini karşıladı.",
        "monitor A SLA'yı sağladı.",
        "A monitörü SLA hedefini karşıladı.",
        "Monitör A hedefi tutturdu.",
        "Visit hxxp://evil[.]com for details.",
        "Details at example[.]com.",
        "Details at example．com.",
        "Details at example。com.",
        "Details at xn--80ak6aa92e.com.",
        "Details at пример.рф.",
        "Call +90 212 555 12 12.",
        "Mail ops at evil dot com.",
        "Details at bit.ly/x",
        "Details at ex​ample.com",
        "monitor A and monitor B both met their SLA targets.",
        "All monitors had 100% uptime.",
        "No monitor had downtime: 0 minutes in total.",
        "monitor A's certificate expires in 5 days.",
        "monitor A had 100 percent uptime.",
        "monitor A uptime: yüzde 100.",
    };

    [Theory]
    [MemberData(nameof(ReviewProbeAttacks))]
    public void Review_probe_attacks_are_rejected(string text) =>
        Assert.NotEmpty(Contract(text.Contains('ı') || text.Contains("monitörü") || text.Contains("hedef") ? "tr" : "en").Problems(text));

    [Theory]
    [InlineData("monitor A had {{B.incidents}} incidents.")]                                    // B's figure for A
    [InlineData("monitor A had {{A.uptime}} uptime and monitor B {{A.uptime}}.")]               // swapped
    [InlineData("{{A.uptime}} was the uptime of monitor A.")]                                   // not bound
    [InlineData("monitor A had {{total.incidents}} incidents.")]                                // customer-wide total for a monitor
    [InlineData("monitor A had {{A.prev.uptime}} uptime.")]                                     // previous value as current
    [InlineData("Incidents rose by {{change.incidents}}.")]                                     // direction is rendered
    [InlineData("monitor A had {{A.nonexistent}} incidents.")]
    [InlineData("monitor A had {{A.uptime}.")]                                                  // malformed
    [InlineData("monitor A kept its {{A.sla_target}} SLA target.")]
    [InlineData("monitor A stayed within its {{A.sla_target}} target.")]
    [InlineData("monitor A and monitor B both had {{A.uptime}}.")]
    [InlineData("MONITOR A had {{B.uptime}} uptime.")]
    [InlineData("Monitors A and B had {{C.uptime}} uptime.")]
    [InlineData("monitor A had one incident.")]
    [InlineData("monitor A had {{A.incidents}} incidents, twice as many as monitor B.")]
    [InlineData("monitor Q had {{A.uptime}} uptime.")]                                           // unknown label
    [InlineData("monitor A had no incidents.")]
    public void Placeholder_attacks_are_rejected(string text) => Assert.NotEmpty(Contract().Problems(text));

    [Theory]
    [InlineData("A monitörü {{B.incidents}} olay yaşadı.")]
    [InlineData("monitor A iki olay yaşadı.")]
    [InlineData("monitor A için SLA hedefi {{A.sla_target}} idi ve hedef tutturuldu.")]
    [InlineData("monitor A hiç kesinti yaşamadı.")]
    public void Turkish_attacks_are_rejected(string text) => Assert.NotEmpty(Contract("tr").Problems(text));

    [Theory]
    [InlineData("SLA result of monitor A ({{A.sla_target}} target): {{A.sla_status}}.")]
    [InlineData("monitor A was down for {{A.downtime}} in {{A.incidents}} incidents, and monitor B for {{B.downtime}}.")]
    [InlineData("Monitors A and B: downtime {{A.downtime}} and {{B.downtime}}.")]
    [InlineData("Overall incidents compared with {{period.previous_name}}: {{change.incidents}}.")]
    [InlineData("In the previous period monitor A had {{A.prev.uptime}} uptime.")]
    [InlineData("monitor A's certificate {{A.cert_expiry}}.")]
    [InlineData("For monitor A, overall {{total.incidents}} incidents is the context.")]
    [InlineData("Keep an eye on monitor A in {{period.name}}.")]
    public void Legitimate_placeholder_texts_pass(string text) => Assert.Empty(Contract().Problems(text));

    [Theory]
    [InlineData("monitor A için erişilebilirlik {{A.uptime}} oldu; önceki dönemde {{A.prev.uptime}} idi.")]
    [InlineData("{{period.name}} döneminde monitor A için SLA sonucu: {{A.sla_status}}.")]
    [InlineData("A monitörü {{A.incidents}} olay yaşadı ve bir sertifika uyarısı var.")]          // "bir" is the article
    public void Legitimate_turkish_texts_pass(string text) => Assert.Empty(Contract("tr").Problems(text));

    [Fact]
    public void The_server_renders_values_per_language_and_names_locally()
    {
        var names = new Dictionary<string, string> { ["monitor A"] = "Checkout API", ["monitor B"] = "Blog" };
        var en = new NarrativeOutput("h", [], [], [], Labels, 0, Values, "en");
        Assert.Equal("Checkout API: 99.86% uptime, 1 h 3 min down, SLA missed; certificate expired 5 days before the end of the period.",
            en.Render("monitor A: {{A.uptime}} uptime, {{A.downtime}} down, SLA {{A.sla_status}}; certificate {{A.cert_expiry}}.", names));
        Assert.Equal("Overall: 4 more, 5 h 27 min more, 0.24 percentage points lower; March 2026.",
            en.Render("Overall: {{change.incidents}}, {{change.downtime}}, {{change.uptime}}; {{period.name}}.", names));
        var tr = en with { Language = "tr" };
        Assert.Equal("Checkout API: %99,86, 1 sa 3 dk, karşılanmadı; 5 gün önce sona erdi; Mart 2026.",
            tr.Render("monitor A: {{A.uptime}}, {{A.downtime}}, {{A.sla_status}}; {{A.cert_expiry}}; {{period.name}}.", names));
        Assert.Equal("Checkout API and Blog: 2 and 4.", en.Render("Monitors A and B: {{A.incidents}} and {{B.incidents}}.", names));
        Assert.Equal("Checkout API monitörü ve Checkout API.", tr.Render("A monitörü ve MONITOR A.", names));
    }

    private static string Output(string headline, params string[] happened) => JsonSerializer.Serialize(new
    {
        headline,
        what_happened = happened.Select(t => new { text = t, monitors = new[] { "monitor A", "monitor Q" } }),
        what_changed = Array.Empty<object>(),
        what_to_watch = new[] { new { text = "Keep an eye on monitor A.", monitors = new[] { "A" } } },
    });

    [Fact]
    public void Check_strips_bad_items_rejects_bad_headlines_and_mostly_bad_narratives()
    {
        var check = Contract().Check(Output("SLA result of monitor A: {{A.sla_status}}.",
            "monitor A was down for {{A.downtime}}.", "monitor A met its target.", "monitor A had {{A.incidents}} incidents."));
        var output = NarrativeOutput.TryParse(check.Json)!;
        Assert.Equal(1, output.RemovedItems);
        Assert.Equal(["monitor A"], output.WhatHappened[0].Monitors); // "monitor Q" was never sent
        Assert.Equal("en", output.Language);
        Assert.Equal(99.86, output.Values!["A.uptime"].Number);

        Assert.Equal("ungrounded_numbers", Contract().Check(Output("monitor A had 100% uptime.", "monitor A was down for {{A.downtime}}.")).ErrorCode);
        Assert.Equal("ungrounded_numbers", Contract().Check(Output("A calm month.", "There were 7 incidents.", "Two monitors missed.", "Uptime was 99%.", "monitor A was fine.")).ErrorCode);
        Assert.Equal("invalid_output", Contract().Check("{\"headline\":\"\"}").ErrorCode);
        Assert.Equal("invalid_output", Contract().Check("not json").ErrorCode);
    }
}
