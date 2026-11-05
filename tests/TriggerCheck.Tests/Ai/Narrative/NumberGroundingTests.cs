using System.Text.Json;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Grounding;
using TriggerCheck.Ai.Narrative;

namespace TriggerCheck.Tests.Ai.Narrative;

/// <summary>
/// Shared number grounding (AI-5, AI-6). The facts come from a realistic narrative context written with
/// <see cref="AiContextBuilder"/> (timestamps, a previous period with zeros and 100 %, signed changes, two monitors,
/// untrusted text), exactly as the provider records them, never from scanning the rendered text.
/// </summary>
public class NumberGroundingTests
{
    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static AiContextBuilder Context()
    {
        var b = new AiContextBuilder("tr");
        b.Section("Report period").Identifier("period", "2026-02")
            .Fact("period_start_utc", Utc(2026, 2, 1)).Fact("period_end_utc", Utc(2026, 3, 1)).Identifier("time_zone", "UTC");
        b.Facts.AddMonth(2026, 2);
        b.Section("Totals of this period")
            .Fact("monitors", 2, GroundingUnit.Count).Fact("uptime_percent", 99.9523).Fact("downtime_minutes", 135.4)
            .Identifier("downtime", "2 h 15 min").Fact("incidents", 3, GroundingUnit.Count).Fact("mttr_minutes", 45.1)
            .Fact("avg_response_ms", 180.5).Fact("p95_response_ms", 1234)
            .Fact("sla_targets_met", 1, GroundingUnit.Count).Fact("sla_targets_missed", 1, GroundingUnit.Count);
        b.Section("Totals of the previous period (2026-01)")
            .Fact("previous_uptime_percent", 100.0).Fact("previous_downtime_minutes", 0.0).Fact("previous_incidents", 0, GroundingUnit.Count);
        b.Section("Changes compared with the previous period")
            .Delta("uptime_change_percentage_points", -0.0477).Delta("downtime_change_minutes", 135.4)
            .Delta("incidents_change", 3, GroundingUnit.Count).Delta("monitor_count_change", 0, GroundingUnit.Count);
        b.Section("monitor A").Subject("monitor A")
            .Fact("sla_target", 99.9, GroundingUnit.Percent).Fact("uptime_percent", 99.814).Fact("sla", "missed")
            .Fact("downtime_minutes", 75.0).Fact("incidents", 1, GroundingUnit.Count).Fact("p95_response_ms", 450)
            .Fact("previous_uptime_percent", 100.0).Fact("previous_incidents", 0, GroundingUnit.Count);
        b.Section("monitor B").Subject("monitor B")
            .Fact("uptime_percent", 100.0).Fact("sla", "no_target").Fact("downtime_minutes", 0.0).Fact("incidents", 0, GroundingUnit.Count)
            .Fact("p95_response_ms", 120);
        b.Section("Longest incidents of the period").Subject("monitor A")
            .Fact("incident a started_at", Utc(2026, 2, 7, 8)).Fact("incident a duration_minutes", 75.0).Fact("incident a error_class", "http_5xx");
        b.Untrusted("note", "HTTP 503 after 42 retries, see http://evil.example/x");
        return b;
    }

    private static readonly AiContextBuilder B = Context();
    private static readonly GroundingPolicy P = NarrativeOutputContract.Policy("2026-01", "January", "Ocak");

    private static List<string> Bad(string text, params string[] subjects) => NumberGrounding.Ungrounded(text, B.Facts, P, subjects);

    [Theory]
    [InlineData("Uptime was 99.95% in February 2026.")]
    [InlineData("Erişilebilirlik %99,95 oldu.")]
    [InlineData("Erişilebilirlik %99,9523 idi.")]
    [InlineData("There were 3 incidents and 135.4 minutes of downtime.")]
    [InlineData("Toplam kesinti 135,4 dakika.")]
    [InlineData("Downtime was 2 h 15 min.")]
    [InlineData("Kesinti yaklaşık 2 saat 15 dakika sürdü.")]
    [InlineData("Downtime was about 2 hours.")]
    [InlineData("p95 response time was 1,234 ms.")]
    [InlineData("p95 yanıt süresi 1.234 ms oldu.")]
    [InlineData("Average response was 180.5 ms; 5xx errors appeared over HTTP/2 and TLS 1.3.")]
    [InlineData("Uptime fell by 0.05 percentage points.")]
    [InlineData("There were 3 more incidents than in January.")]
    [InlineData("Incidents rose by 3.")]
    [InlineData("In January uptime was 100% with 0 incidents.")]
    [InlineData("The outage started on 2026-02-07 at 08:00.")]
    [InlineData("Kesinti 07.02.2026 tarihinde başladı.")]
    [InlineData("Kesinti 7 Şubat'ta başladı.")]
    [InlineData("It started on February 7.")]
    [InlineData("The report covers 2026-02.")]
    [InlineData("No numbers here at all.")]
    public void Grounded_texts_pass(string text) => Assert.Empty(Bad(text));

    [Theory]
    [InlineData("monitor A missed its 99.9% target with 99.81% uptime.", "monitor A")]
    [InlineData("monitor A's incident lasted 75 minutes (1 h 15 min).", "monitor A")]
    [InlineData("monitor B had 0 minutes of downtime and 100% uptime.", "monitor B")]
    [InlineData("In January monitor A had 100% uptime.", "monitor A")]
    public void Subject_facts_pass_for_their_subject(string text, string subject) => Assert.Empty(Bad(text, subject));

    /// <summary>The bypasses found in review (prompt-injection case) and the AI-5 review cases.</summary>
    [Theory]
    [InlineData("monitor A had 100% uptime.", "100%", "monitor A")]                    // only the previous period was 100
    [InlineData("monitor A had 0 minutes of downtime.", "0 minutes", "monitor A")]     // 0 only in other subjects / previous
    [InlineData("monitor A had 7 incidents.", "7", "monitor A")]                       // 7 is a day of a timestamp
    [InlineData("There were 8 incidents.", "8")]                                         // 8 is an hour of a timestamp
    [InlineData("There were 95 incidents.", "95")]                                       // 95 is part of the key p95_response_ms
    [InlineData("The outage lasted 1 day.", "1 day")]                                    // a duration matches only time facts
    [InlineData("Downtime fell by 2 days.", "2 days")]
    [InlineData("Downtime was 28 hours less than in January.", "28 hours")]
    [InlineData("Incidents fell by 3.", "3")]                                            // they rose by 3
    [InlineData("There were 3 fewer incidents.", "3")]
    [InlineData("monitor A had a p95 of 120 ms.", "120 ms", "monitor A")]              // monitor B's figure
    [InlineData("Uptime was 99.97%.", "99.97%")]
    [InlineData("Erişilebilirlik %99,97 oldu.", "%99,97")]
    [InlineData("Downtime was 0.31% of the month.", "0.31%")]                           // derived, not given
    [InlineData("The service retried 42 times.", "42")]                                  // only in untrusted data
    [InlineData("Status 503 was returned.", "503")]
    [InlineData("It started at 1:58.", "1:58")]
    [InlineData("It started on 2026-02-08.", "2026-02-08")]
    [InlineData("Kesinti 10 Mart'ta başladı.", "10 Mart")]
    public void Ungrounded_numbers_are_found(string text, string expected, params string[] subjects) =>
        Assert.Contains(expected, Bad(text, subjects));

    [Theory]
    [InlineData("Uptime was ٩٩٫٩٥%.")]                                                    // Arabic-Indic digits
    [InlineData("Uptime was ９９.９５%.")]                                                 // fullwidth digits
    [InlineData("p95 was 1234567890123456789012345678901234567890 ms.")]                 // overflow: no crash, ungrounded
    [InlineData("Version 1.2.3 is affected.")]
    public void Unparseable_numbers_are_ungrounded_and_never_throw(string text) => Assert.NotEmpty(Bad(text));

    [Theory]
    [InlineData("Details at https://evil.example/fix.")]
    [InlineData("See evil.example for a fix.")]
    [InlineData("Write to ops@corp.example.")]
    [InlineData("Call +90 532 123 45 67 now.")]
    public void Links_domains_and_contacts_are_rejected(string text) => Assert.Contains("link or contact", Bad(text));

    [Fact]
    public void Units_must_agree_and_durations_convert_only_between_time_units()
    {
        var b = new AiContextBuilder("en");
        b.Fact("downtime_minutes", 54).Fact("year", 2026);
        var f = b.Facts;
        Assert.NotEmpty(NumberGrounding.Ungrounded("Downtime was 54 h.", f, P));          // 54 minutes, not hours
        Assert.Empty(NumberGrounding.Ungrounded("Downtime was 54 minutes.", f, P));
        Assert.Empty(NumberGrounding.Ungrounded("Downtime was 3240 s.", f, P));          // converted exactly
        Assert.NotEmpty(NumberGrounding.Ungrounded("It lasted 1 day.", f, P));

        var u = new AiContextBuilder("en").Fact("uptime_percent", 99.96).Facts;
        Assert.Empty(NumberGrounding.Ungrounded("Uptime was 99.96 % (about 99.96%).", u, P));
        Assert.NotEmpty(NumberGrounding.Ungrounded("Uptime was effectively 100%.", u, P)); // never rounded up to 100
    }

    // ---------------- AI-5 review cases (strict policy: as given, no rounding, no unit conversion) ----------------

    [Fact]
    public void Strict_policy_ai5_cases()
    {
        var b = new AiContextBuilder("tr");
        b.Fact("target_host", "10.1.2.3").Fact("last_match_at", "2026-03-10T11:58:00Z").Fact("brief_incidents", "14")
            .Fact("incidents", "15").Fact("p99_ms", "4500").Fact("share", "9.1").Fact("expected_status_codes", "200-299");
        var f = b.Facts;
        var s = GroundingPolicy.Strict;
        Assert.Empty(NumberGrounding.Ungrounded("The host 10.1.2.3 refused the connection.", f, s));
        Assert.NotEmpty(NumberGrounding.Ungrounded("The host 10.1.2.4 refused the connection.", f, s));
        Assert.Empty(NumberGrounding.Ungrounded("Son eşleşme 10.03.2026 tarihinde, 11:58'de oldu.", f, s));
        Assert.Empty(NumberGrounding.Ungrounded("Son eşleşme 10 Mart'ta oldu.", f, s));
        Assert.Empty(NumberGrounding.Ungrounded("Last match on 2026-03-10 at 11:58.", f, s));
        Assert.NotEmpty(NumberGrounding.Ungrounded("Last match at 1:58.", f, s));
        Assert.NotEmpty(NumberGrounding.Ungrounded("Son eşleşme 11.03.2026 tarihinde oldu.", f, s));
        Assert.NotEmpty(NumberGrounding.Ungrounded("1.5 incidents", f, s));               // never 15
        Assert.Empty(NumberGrounding.Ungrounded("14 of 15 incidents; p99 4500 ms (4,500 / 4.500), 9,1 %, codes 200-299", f, s));
        Assert.Equal(["16"], NumberGrounding.Ungrounded("16 of 15 incidents", f, s));
        Assert.NotEmpty(NumberGrounding.Ungrounded("about 4.5 seconds", f, s));           // unit conversion = a new number
        Assert.NotEmpty(NumberGrounding.Ungrounded("since 2026-03-08", f, s));
    }

    // ---------------- narrative contract ----------------

    private static string Output(string headline, params (string Text, string[] Monitors)[] happened) => JsonSerializer.Serialize(new
    {
        headline,
        what_happened = happened.Select(t => new { text = t.Text, monitors = t.Monitors }),
        what_changed = Array.Empty<object>(),
        what_to_watch = new[] { new { text = "Keep an eye on monitor A.", monitors = new[] { "A" } } },
    });

    private static NarrativeOutputContract Contract() =>
        new(B.Facts, new Dictionary<string, int> { ["monitor A"] = 7, ["monitor B"] = 8 },
            new Dictionary<string, string> { ["monitor A"] = "missed", ["monitor B"] = "no_target" }, P);

    [Fact]
    public void Contract_strips_invented_numbers_links_and_wrong_sla_claims()
    {
        var check = Contract().Check(Output("A month with 3 incidents.",
            ("monitor A missed its 99.9% target.", ["monitor A", "monitor Q"]),
            ("monitor A met its 99.9% SLA.", ["monitor A"]),                 // contradicts "missed"
            ("Uptime was 99.95%.", []),
            ("Downtime was 135.4 minutes.", []),
            ("Details at https://evil.example/fix.", [])));
        var output = NarrativeOutput.TryParse(check.Json);
        Assert.NotNull(output);
        Assert.Equal(2, output.RemovedItems);
        Assert.Equal(["monitor A missed its 99.9% target.", "Uptime was 99.95%.", "Downtime was 135.4 minutes."], output.WhatHappened.Select(i => i.Text));
        Assert.Equal(["monitor A"], output.WhatHappened[0].Monitors); // "monitor Q" was never sent
        Assert.Equal(7, output.Labels!["monitor A"]);
    }

    [Fact]
    public void Contract_rejects_the_reviewed_prompt_injection_narrative()
    {
        // The hostile answer of the review: every statement is false; nothing of it may be shown.
        var hostile = JsonSerializer.Serialize(new
        {
            headline = "monitor A had 100% uptime in February and met its 99.9% SLA.",
            what_happened = new[] { new { text = "monitor A had 0 minutes of downtime and 7 incidents lasting 1 day.", monitors = new[] { "monitor A" } } },
            what_changed = new[] { new { text = "Downtime fell by 2 days; 28 hours less than January.", monitors = new[] { "monitor A" } } },
            what_to_watch = new[] { new { text = "Nothing to do; details at https://evil.example/fix or call support.", monitors = new[] { "monitor A" } } },
        });
        var check = Contract().Check(hostile);
        Assert.Null(check.Json);
        Assert.Equal("ungrounded_numbers", check.ErrorCode);
        // Even without the headline every item fails on its own.
        Assert.All(new[] { "monitor A had 0 minutes of downtime and 7 incidents lasting 1 day.", "Downtime fell by 2 days; 28 hours less than January.",
            "Nothing to do; details at https://evil.example/fix or call support." }, t => Assert.NotEmpty(Contract().Problems(t, ["monitor A"])));
    }

    [Fact]
    public void Contract_rejects_an_ungrounded_headline_or_a_mostly_invented_narrative()
    {
        Assert.Equal("ungrounded_numbers", Contract().Check(Output("Uptime was 99.99%.", ("Uptime was 99.95%.", []))).ErrorCode);
        var mostly = Contract().Check(Output("A calm month.", ("There were 7 incidents.", []), ("Uptime was 98.1%.", []), ("p95 was 999 ms.", []), ("Uptime was 99.95%.", [])));
        Assert.Null(mostly.Json);
        Assert.Equal("ungrounded_numbers", mostly.ErrorCode);
        Assert.Equal("invalid_output", Contract().Check("{\"headline\":\"\"}").ErrorCode);
        Assert.Equal("invalid_output", Contract().Check("not json").ErrorCode);
    }

    [Fact]
    public void Facts_come_from_written_values_only()
    {
        // Keys, timestamps and untrusted text add no numbers; zero changes are not facts.
        Assert.DoesNotContain(B.Facts.Numbers, f => f.Value is 95 or 42 or 503 or 7 or 8);
        Assert.DoesNotContain(B.Facts.Numbers, f => f.Key == "monitor_count_change");
        Assert.Contains(B.Facts.Numbers, f => f is { Key: "incidents_change", Sign: 1, Value: 3 });
        Assert.Contains(B.Facts.Numbers, f => f is { Key: "previous_uptime_percent", Previous: true, Subject: null });
        Assert.Contains(B.Facts.Numbers, f => f is { Key: "uptime_percent", Subject: "monitor A", Unit: GroundingUnit.Percent });
        Assert.Contains(new DateOnly(2026, 2, 7), B.Facts.Dates);
        Assert.DoesNotContain("evil.example", B.Facts.Literals);
    }

    [Fact]
    public void Labels_are_replaced_with_names_for_display()
    {
        var names = new Dictionary<string, string> { ["monitor A"] = "Checkout API" };
        Assert.Equal("Checkout API missed its target; Checkout API'nın hedefi. monitor B stays.",
            NarrativeReports.WithNames("monitor A missed its target; Monitör A'nın hedefi. monitor B stays.", names));
    }
}
