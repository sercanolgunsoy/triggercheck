using System.Text.Json;
using TriggerCheck.Ai.Narrative;

namespace TriggerCheck.Tests.Ai.Narrative;

/// <summary>AI-6 hard rule: every number in the narrative comes from the input facts (en + tr formats).</summary>
public class NarrativeNumbersTests
{
    private const string Facts = """
        response_language: Turkish

        ## Totals of this period
        uptime_percent: 99.9523
        downtime_minutes: 135.4
        downtime: 2 h 15 min
        incidents: 3
        p95_response_ms: 1234
        avg_response_ms: 180.5
        uptime_change_percentage_points: 0.0412 (down)
        period: 2026-02
        monitor A: type=Http sla_target=99.9 uptime_percent=99.5 sla=missed
        <untrusted_data source="incident a cause">
        HTTP 503 after 42 retries
        </untrusted_data>
        """;

    private static readonly IReadOnlySet<decimal> F = NarrativeNumbers.FactsOf(Facts);

    [Theory]
    [InlineData("Uptime was 99.95% in 2026-02.")]
    [InlineData("Erişilebilirlik %99,95 oldu.")]
    [InlineData("Erişilebilirlik %99,9523 idi.")]
    [InlineData("Uptime reached 99.9523 %.")]
    [InlineData("There were 3 incidents and 135.4 minutes of downtime.")]
    [InlineData("Toplam kesinti 135,4 dakika.")]
    [InlineData("Downtime was 2 h 15 min.")]
    [InlineData("Kesinti yaklaşık 2 saat 15 dakika sürdü.")]
    [InlineData("Downtime was about 2 hours.")]
    [InlineData("p95 response time was 1,234 ms.")]
    [InlineData("p95 yanıt süresi 1.234 ms oldu.")]
    [InlineData("Average response was 180.5 ms; 5xx errors appeared over HTTP/2 and TLS 1.3.")]
    [InlineData("Uptime fell by 0.04 percentage points.")]
    [InlineData("monitor A missed its 99.9% target with 99.5%.")]
    [InlineData("No numbers here at all.")]
    public void Grounded_texts_pass(string text) =>
        Assert.Empty(NarrativeNumbers.Ungrounded(text, F));

    [Theory]
    [InlineData("Uptime was 99.97%.", "99.97")]
    [InlineData("Erişilebilirlik %99,97 oldu.", "99,97")]
    [InlineData("There were 4 incidents.", "4")]
    [InlineData("Uptime was effectively 100%.", "100")]                // 99.95 may not be rounded up to 100
    [InlineData("Downtime was 0.05% of the month.", "0.05")]           // derived, not given
    [InlineData("Kesinti 5 saat sürdü.", "5 saat")]                    // duration not in the facts
    [InlineData("Downtime was 3 h 40 min.", "3 h 40 min")]
    [InlineData("The service retried 42 times.", "42")]                // only in untrusted data
    [InlineData("Status 503 was returned.", "503")]                    // only in untrusted data
    [InlineData("p95 was 1,5 s.", "1,5 s")]
    public void Ungrounded_numbers_are_found(string text, string expected) =>
        Assert.Contains(expected, NarrativeNumbers.Ungrounded(text, F));

    [Fact]
    public void Ambiguous_comma_reads_as_decimal_thousands_or_list()
    {
        var facts = new HashSet<decimal> { 3, 4, 1234 };
        Assert.Empty(NarrativeNumbers.Ungrounded("monitors 3,4 recovered", facts));  // list
        Assert.Empty(NarrativeNumbers.Ungrounded("1,234 checks", facts));            // en thousands
        Assert.Empty(NarrativeNumbers.Ungrounded("1.234 kontrol", facts));           // tr thousands
        Assert.NotEmpty(NarrativeNumbers.Ungrounded("3,5 saat", facts));
    }

    [Fact]
    public void Mixed_separators_are_parsed_by_locale()
    {
        var facts = new HashSet<decimal> { 12345.6m };
        Assert.Empty(NarrativeNumbers.Ungrounded("12,345.6 ms", facts));
        Assert.Empty(NarrativeNumbers.Ungrounded("12.345,6 ms", facts));
        Assert.NotEmpty(NarrativeNumbers.Ungrounded("12.345,7 ms", facts));
    }

    [Fact]
    public void Dates_in_turkish_format_split_into_grounded_parts()
    {
        var facts = NarrativeNumbers.FactsOf("started_at: 2026-02-14T10:05:00Z");
        Assert.Empty(NarrativeNumbers.Ungrounded("14.02.2026 tarihinde başladı.", facts));
        Assert.NotEmpty(NarrativeNumbers.Ungrounded("15.02.2026 tarihinde başladı.", facts));
    }

    private static string Output(string headline, params string[] happened) => JsonSerializer.Serialize(new
    {
        headline,
        what_happened = happened.Select(t => new { text = t, monitors = new[] { "monitor A", "monitor Q" } }),
        what_changed = Array.Empty<object>(),
        what_to_watch = new[] { new { text = "Keep an eye on monitor A.", monitors = new[] { "A" } } },
    });

    private static NarrativeOutputContract Contract() => new(F, new Dictionary<string, int> { ["monitor A"] = 7 });

    [Fact]
    public void Contract_strips_items_with_invented_numbers_and_keeps_known_labels()
    {
        var check = Contract().Check(Output("A month with 3 incidents.", "Uptime was 99.95%.", "Uptime was 99.97%.", "Downtime was 135.4 minutes."));
        var output = NarrativeOutput.TryParse(check.Json);
        Assert.NotNull(output);
        Assert.Equal(1, output.RemovedItems);
        Assert.Equal(["Uptime was 99.95%.", "Downtime was 135.4 minutes."], output.WhatHappened.Select(i => i.Text));
        Assert.All(output.WhatHappened, i => Assert.Equal(["monitor A"], i.Monitors)); // "monitor Q" was never sent
        Assert.Equal(["monitor A"], output.WhatToWatch[0].Monitors);
        Assert.Equal(7, output.Labels!["monitor A"]);
    }

    [Fact]
    public void Contract_rejects_an_ungrounded_headline_or_a_mostly_invented_narrative()
    {
        Assert.Equal("ungrounded_numbers", Contract().Check(Output("Uptime was 99.99%.", "Uptime was 99.95%.")).ErrorCode);
        var mostly = Contract().Check(Output("A calm month.", "There were 7 incidents.", "Uptime was 98.1%.", "p95 was 999 ms.", "Uptime was 99.95%."));
        Assert.Null(mostly.Json);
        Assert.Equal("ungrounded_numbers", mostly.ErrorCode);
        Assert.Equal("invalid_output", Contract().Check("{\"headline\":\"\"}").ErrorCode);
        Assert.Equal("invalid_output", Contract().Check("not json").ErrorCode);
    }

    [Fact]
    public void Labels_are_replaced_with_names_for_display()
    {
        var names = new Dictionary<string, string> { ["monitor A"] = "Checkout API" };
        Assert.Equal("Checkout API missed its target; Checkout API'nın hedefi. monitor B stays.",
            NarrativeReports.WithNames("monitor A missed its target; Monitör A'nın hedefi. monitor B stays.", names));
    }
}
