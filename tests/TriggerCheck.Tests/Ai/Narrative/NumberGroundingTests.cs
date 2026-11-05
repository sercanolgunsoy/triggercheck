using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Grounding;

namespace TriggerCheck.Tests.Ai.Narrative;

/// <summary>
/// The shared number grounding engine (AI-5 strict; AI-7). Facts are written with <see cref="AiContextBuilder"/>, as
/// the providers do. Includes the AI-5 review probes and the engine items of the AI-6 re-review (H3, H4, M1-M4, M6, L2-L4).
/// </summary>
public class NumberGroundingTests
{
    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    /// <summary>The AI-5 "raise timeout" context of the review probe.</summary>
    private static GroundingFacts Ai5(string language = "en")
    {
        var b = new AiContextBuilder(language).Fact("event", "settings_suggestion");
        b.Section("Monitor").Fact("type", "Http").Fact("interval_seconds", 60).Fact("timeout_seconds", 10).Fact("failure_threshold", 1)
            .Fact("monitor_created_at", Utc(2026, 1, 5, 9));
        b.Section("Suggestion").Fact("suggestion", "raise_timeout").Fact("setting", "timeout_seconds").Fact("current_value", "10").Fact("suggested_value", "15");
        b.Section("Evidence").Fact("window_days", "14").Fact("successful_checks", "20160").Fact("near_timeout_checks", "312").Fact("near_timeout_from_ms", "8000")
            .Fact("timed_out_checks", "41").Fact("p95_ms", "8450").Fact("p99_ms", "9800").Fact("max_ms", "10000").Fact("timeout_seconds", "10")
            .Fact("interval_seconds", "60").Fact("suggested_timeout_seconds", "15");
        return b.Facts;
    }

    private static List<string> Strict(string text, GroundingFacts f) => NumberGrounding.Ungrounded(text, f, GroundingPolicy.Strict);

    [Theory]
    [InlineData("In the last 14 days, 312 of 20,160 successful checks took longer than 8,000 ms and 41 checks timed out.")]
    [InlineData("p95 is 8450 ms and p99 is 9800 ms, close to the 10-second timeout.")]
    [InlineData("p95 is 8,450 ms, p99 9,800 ms; the max was 10,000 ms.")]
    [InlineData("Raising the timeout from 10 s to 15 s lets slow responses count as up.")]
    [InlineData("Raising the timeout from 10 to 15 seconds would avoid 41 timeouts.")]
    [InlineData("Checks run every 60 seconds.")]
    [InlineData("The monitor has been watched since 5 January 2026.")]
    [InlineData("p95 went up to 8450 ms.")]
    [InlineData("Timeouts were up 41 in the window.")]
    [InlineData("In the last 14 days, 312 successful checks took 8000 ms or more.")] // a bound, not a change
    public void Ai5_probe_legitimate_lines_pass(string text) => Assert.Empty(Strict(text, Ai5()));

    [Theory]
    [InlineData("Son 14 günde 41 kontrol zaman aşımına uğradı; p95 8.450 ms.")]
    [InlineData("Zaman aşımını 10 sn'den 15 sn'ye çıkarmak öneriliyor.")]
    [InlineData("Son 14 günde 312 / 20.160 başarılı kontrol 8.000 ms'yi aştı.")]
    public void Ai5_turkish_lines_pass(string text) => Assert.Empty(Strict(text, Ai5("tr")));

    [Theory]
    [InlineData("41 incidents happened in 14 days.", "41")]          // wrong metric (timed-out checks)
    [InlineData("15 checks timed out.", "15")]                       // the suggested timeout as a count
    [InlineData("The p95 is 10 seconds.", "10 seconds")]             // the timeout as p95
    [InlineData("The p95 is 8.45 s.", "8.45 s")]                     // unit conversion = a new number (strict)
    [InlineData("Over sixty checks timed out.", "sixty")]
    [InlineData("312,000 checks were near the timeout.", "312,000")]
    [InlineData("In the last 14 days, 312 of 20.160 successful checks were slow.", "20.160")] // English reading: 20.16
    [InlineData("The timeout should go up 5 seconds.", "5 seconds")]
    public void Ai5_probe_attacks_are_rejected(string text, string expected) => Assert.Contains(expected, Strict(text, Ai5()));

    [Fact]
    public void Ai5_review_cases_ip_dates_times_and_decimals()
    {
        var b = new AiContextBuilder("tr");
        b.Fact("target_host", "10.1.2.3").Fact("last_match_at", "2026-03-10T11:58:00Z").Fact("incidents", "15");
        var f = b.Facts;
        Assert.Empty(Strict("10.1.2.3 adresi bağlantıyı reddetti.", f));
        Assert.NotEmpty(Strict("10.1.2.4 adresi bağlantıyı reddetti.", f));
        Assert.Empty(Strict("Son eşleşme 10.03.2026 tarihinde, 11:58'de oldu.", f));
        Assert.Empty(Strict("Son eşleşme 10 Mart'ta oldu.", f));
        Assert.NotEmpty(Strict("Son eşleşme 1:58'de oldu.", f));
        Assert.NotEmpty(Strict("Son eşleşme 11.03.2026 tarihinde oldu.", f));
        Assert.NotEmpty(Strict("1,5 olay", f)); // Turkish 1.5, never 15
        var en = new AiContextBuilder("en").Fact("incidents", "15").Facts;
        Assert.NotEmpty(Strict("1.5 incidents", en));
    }

    // ---------------- re-review engine items ----------------

    private static GroundingPolicy Loose => new(AllowRounding: true, ConvertTimeUnits: true, PreviousPeriodCues: ["January"]);

    /// <summary>A monitor statement context: subject A, customer-wide totals, previous period, signed changes, codes.</summary>
    private static GroundingFacts Report()
    {
        var b = new AiContextBuilder("en");
        b.Section("Period").Fact("period_start_utc", Utc(2026, 2, 1)).Fact("period_end_utc", Utc(2026, 3, 1));
        b.Facts.AddMonth(2026, 2);
        b.Section("Totals").Fact("incidents", 7, GroundingUnit.Count).Fact("uptime_percent", 99.71).Fact("downtime_minutes", 390.5);
        b.Section("Previous").Fact("previous_uptime_percent", 100.0).Fact("previous_incidents", 3, GroundingUnit.Count);
        b.Section("Changes").Delta("incidents_change", 4, GroundingUnit.Count).Delta("downtime_change_minutes", 75);
        b.Section("monitor A").Subject("monitor A")
            .Fact("sla_target", 99.9, GroundingUnit.Percent).Fact("uptime_percent", 99.86).Fact("downtime_minutes", 75.0)
            .Fact("incidents", 1, GroundingUnit.Count).Fact("certificate_days_left_at_period_end", -5, GroundingUnit.Days)
            .Fact("error_class", "http_5xx").Fact("status_code", 503);
        return b.Facts;
    }

    private static List<string> Bad(string text, params string[] subjects) => NumberGrounding.Ungrounded(text, Report(), Loose, subjects);

    [Theory]
    // H3: one separator reading per language
    [InlineData("monitor A had 1,000 incidents.", "monitor A")]
    [InlineData("monitor A had 75,000 minutes of downtime.", "monitor A")]
    // H4: codes are no quantities; units; deltas, previous values and totals need their cue
    [InlineData("monitor A was down for 503 minutes.", "monitor A")]
    [InlineData("monitor A had 5 incidents.", "monitor A")]
    [InlineData("monitor A had 4 incidents.", "monitor A")]          // the change, without a change word
    [InlineData("monitor A had 3 incidents.", "monitor A")]          // the previous total, without a previous word
    [InlineData("monitor A had 7 incidents.", "monitor A")]          // the customer-wide total for a monitor
    [InlineData("monitor A had 99.71% uptime.", "monitor A")]
    [InlineData("Uptime was 100%.")]                                  // previous period only
    // M1: a previous-period word in another sentence does not count
    [InlineData("January was fine. Uptime was 100%.")]
    // M2: direction words
    [InlineData("Incidents fell by 4.")]
    [InlineData("Incidents were cut by 4.")]
    [InlineData("Downtime was reduced by 75 minutes.")]
    [InlineData("Downtime improved by 75 minutes.")]                  // downtime rose
    // M3: rounding limits
    [InlineData("monitor A was down for 1 hour.", "monitor A")]       // 75 min: 20 % off
    [InlineData("monitor A was 99.9% available, right at its target.", "monitor A")] // 99.86 never rounds onto the target
    // M4: signs
    [InlineData("The certificate of monitor A expires in 5 days.", "monitor A")]
    [InlineData("monitor A had -75 minutes of downtime.", "monitor A")]
    // L2 / L3
    [InlineData("monitor A returned http/500 errors.", "monitor A")]
    [InlineData("monitor A was x75 slower.", "monitor A")]
    [InlineData("monitor A was twice as slow.", "monitor A")]
    [InlineData("monitor A had seven incidents.", "monitor A")]
    public void Wrong_metric_monitor_period_sign_and_rounding_are_rejected(string text, params string[] subjects) =>
        Assert.NotEmpty(Bad(text, subjects));

    [Theory]
    [InlineData("monitor A had 1 incident and 99.86% uptime.", "monitor A")]
    [InlineData("monitor A had 99.9% as SLA target.", "monitor A")]
    [InlineData("monitor A was down for 75 minutes (1 h 15 min).", "monitor A")]
    [InlineData("monitor A was down 75 minutes.", "monitor A")]       // M6: "down" is no change here
    [InlineData("Overall there were 7 incidents; monitor A had 1.", "monitor A")]
    [InlineData("In the previous period uptime was 100% with 3 incidents.")]
    [InlineData("Uptime dropped from 100% to 99.71%.")]                // M6: "from" marks the earlier value
    [InlineData("Incidents rose by 4 compared with January.")]
    [InlineData("There were 4 more incidents than in January.")]
    [InlineData("Downtime rose by 75 minutes.")]
    [InlineData("Downtime worsened by 75 minutes.")]
    [InlineData("The certificate of monitor A expired 5 days ago.", "monitor A")]
    [InlineData("monitor A returned HTTP 503.", "monitor A")]
    [InlineData("In 2026 monitor A had 1 incident.", "monitor A")]     // M6: bare year of a known date
    [InlineData("The outage was in February 2026.")]
    public void Legitimate_statements_pass(string text, params string[] subjects) => Assert.Empty(Bad(text, subjects));

    [Fact]
    public void Codes_and_non_finite_values_add_no_facts()
    {
        var f = Report();
        Assert.DoesNotContain(f.Numbers, n => n.Key.Contains("error_class") || n.Key.Contains("status_code"));
        Assert.DoesNotContain(f.Numbers, n => n.Value == 503);
        Assert.Contains(503, f.StatusCodes);
        var b = new AiContextBuilder("en").Fact("ratio", double.NaN).Fact("other", double.PositiveInfinity);
        Assert.Empty(b.Facts.Numbers);
        Assert.Contains(f.Numbers, n => n is { Key: "incidents_change", Sign: 1, Value: 4 });
        Assert.Contains(f.Numbers, n => n is { Key: "certificate_days_left_at_period_end", Negative: true, Value: 5 });
    }

    [Theory]
    [InlineData("Uptime was ٩٩٫٩٥%.")]
    [InlineData("Uptime was ９９.９５%.")]
    [InlineData("p95 was 1234567890123456789012345678901234567890 ms.")]
    [InlineData("Version 1.2.3 is affected.")]
    [InlineData("monitor A ½ of the time.")]
    public void Unparseable_numbers_are_ungrounded_and_never_throw(string text) => Assert.NotEmpty(Bad(text));
}
