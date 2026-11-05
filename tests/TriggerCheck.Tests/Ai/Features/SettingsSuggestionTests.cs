using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Ai.Features;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using static TriggerCheck.Ai.Features.SettingsSuggestionRules;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>AI-5: the L1 rules (positive and negative, incl. normal noise), the job, gating, dismissal and the explanation.</summary>
public class SettingsSuggestionTests
{
    private static readonly DateTime Now = AiTestHost.Start.UtcDateTime;

    private static MonitorSettings Settings(MonitorType type = MonitorType.Http, int interval = 60, int timeout = 10, int threshold = 1,
        string? keyword = null, MonitorStatus status = MonitorStatus.Up, double createdDaysAgo = 100) =>
        new(type, interval, timeout, threshold, keyword, status, Now.AddDays(-createdDaysAgo));

    private static List<Check> Ups(int count, int ms = 200, int everyMinutes = 5, double startMinutesAgo = 1) =>
        Enumerable.Range(0, count).Select(i => new Check(Now.AddMinutes(-startMinutesAgo - i * everyMinutes), true, 200, ms, null)).ToList();

    private static List<Check> Sorted(IEnumerable<Check> c) => c.OrderBy(x => x.AtUtc).ToList();

    private static List<IncidentRow> Incidents(int count, TimeSpan duration, double everyHours = 10, double startHoursAgo = 1) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var start = Now.AddHours(-startHoursAgo - i * everyHours);
            return new IncidentRow(start, start + duration, false);
        }).ToList();

    // ---------------- rules ----------------

    [Fact]
    public void Flapping_incidents_that_recover_at_the_next_check_suggest_a_higher_threshold()
    {
        var d = Flapping(Settings(threshold: 1), Incidents(14, TimeSpan.FromSeconds(65)).Concat(Incidents(1, TimeSpan.FromHours(2), startHoursAgo: 150)).ToList(), 7);

        Assert.NotNull(d);
        Assert.Equal(SettingsSuggestionKind.RaiseFailureThreshold, d!.Kind);
        Assert.Equal(("1", "2"), (d.CurrentValue, d.SuggestedValue));
        Assert.Equal("14", SuggestionFact.Get(d.Evidence, "brief_incidents"));
        Assert.Equal("15", SuggestionFact.Get(d.Evidence, "incidents"));
        Assert.Equal("65", SuggestionFact.Get(d.Evidence, "longest_brief_incident_seconds"));
    }

    [Fact]
    public void Few_brief_incidents_real_outages_maintenance_or_max_threshold_suggest_nothing()
    {
        // Normal noise: two blips a week.
        Assert.Null(Flapping(Settings(), Incidents(2, TimeSpan.FromSeconds(60)), 7));
        // Brief incidents are not the majority: real outages.
        Assert.Null(Flapping(Settings(), Incidents(6, TimeSpan.FromSeconds(60)).Concat(Incidents(4, TimeSpan.FromHours(1), startHoursAgo: 3)).ToList(), 7));
        // Long incidents only.
        Assert.Null(Flapping(Settings(), Incidents(10, TimeSpan.FromMinutes(30)), 7));
        // Resolved during maintenance does not count.
        Assert.Null(Flapping(Settings(), Incidents(10, TimeSpan.FromSeconds(60)).Select(i => i with { ResolvedDuringMaintenance = true }).ToList(), 7));
        // Already at the maximum.
        Assert.Null(Flapping(Settings(threshold: 5), Incidents(10, TimeSpan.FromSeconds(60)), 7));
    }

    [Fact]
    public void Responses_close_to_the_timeout_suggest_a_longer_timeout_with_db_numbers()
    {
        var checks = Sorted(Ups(1000)
            .Concat(Ups(40, ms: 9000, everyMinutes: 60, startMinutesAgo: 2.5))
            .Concat(Enumerable.Range(0, 5).Select(i => new Check(Now.AddMinutes(-7 - i * 600), false, null, 10000, "Timeout after 10s"))));
        var d = NearTimeout(Settings(timeout: 10), checks, 7);

        Assert.NotNull(d);
        Assert.Equal(("10", "14"), (d!.CurrentValue, d.SuggestedValue)); // ceil(1.5 x p99 9000 ms)
        Assert.Equal("40", SuggestionFact.Get(d.Evidence, "near_timeout_checks"));
        Assert.Equal("5", SuggestionFact.Get(d.Evidence, "timed_out_checks"));
        Assert.Equal("9000", SuggestionFact.Get(d.Evidence, "p99_ms"));
        Assert.Equal("1040", SuggestionFact.Get(d.Evidence, "successful_checks"));
    }

    [Fact]
    public void Occasional_slow_checks_dead_targets_and_short_intervals_suggest_no_timeout_change()
    {
        // Normal noise: three spikes among 1000 fast checks.
        Assert.Null(NearTimeout(Settings(), Sorted(Ups(1000).Concat(Ups(3, ms: 9500, everyMinutes: 300, startMinutesAgo: 2))), 7));
        // A target that mostly times out is an outage, not a tight timeout.
        Assert.Null(NearTimeout(Settings(), Sorted(Ups(60, ms: 9000).Concat(Enumerable.Range(0, 100).Select(i => new Check(Now.AddMinutes(-3 - i), false, null, 10000, "Timeout after 10s")))), 7));
        // Too few checks.
        Assert.Null(NearTimeout(Settings(), Ups(20, ms: 9000), 7));
        // The interval leaves no room (timeout must stay below it).
        Assert.Null(NearTimeout(Settings(interval: 11, timeout: 10), Sorted(Ups(100, ms: 9000)), 7));
    }

    [Fact]
    public void A_monitor_that_never_succeeded_since_creation_may_have_a_wrong_target()
    {
        var h = new NeverUpHistory(Now.AddHours(-30), 120, "DNS resolution failed: No such host is known.", 120, 0);
        var d = NeverUp(Settings(status: MonitorStatus.Down, createdDaysAgo: 1.3), h, Now);

        Assert.NotNull(d);
        Assert.Equal(SettingsSuggestionKind.TargetMayBeWrong, d!.Kind);
        Assert.Null(d.SuggestedValue);
        Assert.Equal("30", SuggestionFact.Get(d.Evidence, "hours_without_success"));
        Assert.Equal("120", SuggestionFact.Get(d.Evidence, "failed_checks"));
        Assert.Equal("DNS resolution failed: No such host is known.", SuggestionFact.Get(d.Evidence, TopErrorKey));
    }

    [Fact]
    public void Never_up_needs_time_history_since_creation_a_down_status_and_not_keyword_misses()
    {
        var h = new NeverUpHistory(Now.AddHours(-30), 120, "x", 120, 0);
        Assert.Null(NeverUp(Settings(status: MonitorStatus.Down), null, Now));                                    // has succeeded
        Assert.Null(NeverUp(Settings(status: MonitorStatus.Down, createdDaysAgo: 1), h with { FirstCheckAtUtc = Now.AddHours(-5) }, Now)); // too new
        Assert.Null(NeverUp(Settings(status: MonitorStatus.Down, createdDaysAgo: 1.3), h with { Checks = 5 }, Now));
        Assert.Null(NeverUp(Settings(status: MonitorStatus.Down, createdDaysAgo: 90), h, Now));                   // older history purged
        Assert.Null(NeverUp(Settings(status: MonitorStatus.Up, createdDaysAgo: 1.3), h, Now));
        Assert.Null(NeverUp(Settings(MonitorType.Keyword, status: MonitorStatus.Down, createdDaysAgo: 1.3), h with { KeywordMisses = 100 }, Now));
    }

    [Fact]
    public void Keyword_missing_while_the_page_answers_suggests_a_review_and_trims_stray_whitespace()
    {
        var checks = Sorted(Ups(500, startMinutesAgo: 200)
            .Concat(Enumerable.Range(0, 30).Select(i => new Check(Now.AddMinutes(-1 - i * 5), false, 200, 300, "Keyword ' Sale  now ' not found in response"))));
        var d = Keyword(Settings(MonitorType.Keyword, keyword: " Sale  now "), checks, 7);

        Assert.NotNull(d);
        Assert.Equal(" Sale  now ", d!.CurrentValue);
        Assert.Equal("Sale now", d.SuggestedValue);
        Assert.Equal("30", SuggestionFact.Get(d.Evidence, "keyword_misses"));
        Assert.Equal("30", SuggestionFact.Get(d.Evidence, "misses_in_a_row"));
        Assert.Equal("200", SuggestionFact.Get(d.Evidence, "status_code_of_misses"));

        // A clean keyword gets the review without a concrete value (bodies are not stored: no invented keyword).
        var clean = checks.Select(x => x.IsUp ? x : x with { Error = "Keyword 'Sale' not found in response" }).ToList();
        Assert.Null(Keyword(Settings(MonitorType.Keyword, keyword: "Sale"), clean, 7)!.SuggestedValue);
    }

    [Fact]
    public void Rare_keyword_misses_other_errors_and_other_types_suggest_no_keyword_review()
    {
        var noise = Sorted(Ups(1000).Concat(Enumerable.Range(0, 5).Select(i => new Check(Now.AddMinutes(-2 - i * 100), false, 200, 300, "Keyword 'x' not found in response"))));
        Assert.Null(Keyword(Settings(MonitorType.Keyword, keyword: "x"), noise, 7));
        var http5xx = Sorted(Ups(100).Concat(Enumerable.Range(0, 50).Select(i => new Check(Now.AddMinutes(-2 - i), false, 503, 300, "Unexpected status code 503 (expected 200-299)"))));
        Assert.Null(Keyword(Settings(MonitorType.Keyword, keyword: "x"), http5xx, 7));
        Assert.Null(Keyword(Settings(MonitorType.Http), noise, 7));
    }

    [Fact]
    public void Heartbeat_monitors_get_no_suggestions()
    {
        var all = Evaluate(Settings(MonitorType.Heartbeat), Ups(100, ms: 9500), Incidents(14, TimeSpan.FromSeconds(30)), null, Now, 7);
        Assert.Empty(all);
    }

    // ---------------- number grounding ----------------

    [Fact]
    public void Numbers_not_in_the_input_are_rejected()
    {
        // Shared engine (TriggerCheck.Ai.Grounding), strict policy; facts recorded by the context builder.
        var b = new TriggerCheck.Ai.Context.AiContextBuilder("en")
            .Fact("brief_incidents", "14").Fact("incidents", "15").Fact("p99_ms", "4500").Fact("share", "9.1").Fact("last_match_at", "2026-03-09T19:20:00Z");
        var facts = b.Facts;
        var strict = TriggerCheck.Ai.Grounding.GroundingPolicy.Strict;
        Assert.True(TriggerCheck.Ai.Grounding.NumberGrounding.IsGrounded("14 of 15 incidents; p99 4500 ms (4,500 ms), 9.1 %, at 2026-03-09 19:20", facts, strict));
        Assert.Equal(["16"], TriggerCheck.Ai.Grounding.NumberGrounding.Ungrounded("16 of 15 incidents", facts, strict));
        Assert.NotEmpty(TriggerCheck.Ai.Grounding.NumberGrounding.Ungrounded("about 4.5 seconds", facts, strict)); // unit conversion = a new number
        Assert.NotEmpty(TriggerCheck.Ai.Grounding.NumberGrounding.Ungrounded("since 2026-03-08", facts, strict));

        var output = new AiInterpretation("14 of 15 incidents were brief.",
            [new AiHypothesis("Brief blips.", AiConfidence.High, ["14 brief", "20 brief"]), new AiHypothesis("Invented 99 cases.", AiConfidence.Low, ["x"])],
            [new AiSuggestedAction("raise_failure_threshold", "Set it to 3.", AiAutonomy.A1, true)], ["Maybe 7 more."]);
        var grounded = TriggerCheck.Ai.Grounding.NumberGrounding.Apply(output, facts, strict)!;
        Assert.Equal(["14 brief"], Assert.Single(grounded.Hypotheses).Evidence);
        Assert.Empty(grounded.SuggestedActions);
        Assert.Empty(grounded.Unknowns);
        Assert.Null(TriggerCheck.Ai.Grounding.NumberGrounding.Apply(output with { Summary = "17 incidents." }, facts, strict));
    }

    // ---------------- job + explanation ----------------

    private static async Task<MonitorEntity> FlappingMonitorAsync(AiTestHost host, int customerId, int briefIncidents = 14) =>
        await host.WithDbAsync(async db =>
        {
            var m = new MonitorEntity
            {
                CustomerId = customerId, Name = "Flappy", Type = MonitorType.Http, Target = "https://flap.example.test/x?key=secret",
                IntervalSeconds = 60, TimeoutSeconds = 10, FailureThreshold = 1, Status = MonitorStatus.Up, CreatedAtUtc = host.Now.AddDays(-60)
            };
            db.Monitors.Add(m);
            for (var i = 0; i < briefIncidents; i++)
            {
                var start = host.Now.AddHours(-1 - i * 10);
                db.Incidents.Add(new Incident { Monitor = m, StartedAtUtc = start, ResolvedAtUtc = start.AddSeconds(62), Cause = "Timeout after 10s" });
            }
            for (var i = 0; i < 100; i++)
                db.CheckResults.Add(new CheckResult { Monitor = m, CheckedAtUtc = host.Now.AddMinutes(-2 - i * 60), IsUp = true, StatusCode = 200, ResponseTimeMs = 150 });
            await db.SaveChangesAsync();
            return m;
        });

    private static Task<SettingsSuggestionJob.RunSummary> RunJobAsync(AiTestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<SettingsSuggestionJob>().RunAsync());

    private static Task<List<SettingsSuggestion>> SuggestionsAsync(AiTestHost host, int monitorId) =>
        host.WithDbAsync(db => db.SettingsSuggestions.AsNoTracking().Where(s => s.MonitorId == monitorId).OrderBy(s => s.Id).ToListAsync());

    [Fact]
    public async Task Job_creates_one_open_suggestion_and_is_idempotent()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var m = await FlappingMonitorAsync(host, c.Id);
        var quiet = await host.AddMonitorAsync(c.Id, x => x.Status = MonitorStatus.Up);

        var first = await RunJobAsync(host);
        var second = await RunJobAsync(host);

        var s = Assert.Single(await SuggestionsAsync(host, m.Id));
        Assert.Equal((SettingsSuggestionKind.RaiseFailureThreshold, SettingsSuggestionStatus.Open, "1", "2"), (s.Kind, s.Status, s.CurrentValue, s.SuggestedValue));
        Assert.Equal(c.Id, s.CustomerId);
        Assert.Equal(1, first.Created);
        Assert.Equal(0, second.Created);
        Assert.Empty(await SuggestionsAsync(host, quiet.Id));
        Assert.Equal(1, first.Queued); // AI is available (fake client): the explanation is queued once
        Assert.Equal(0, second.Queued); // still waiting in the queue (coalesced)
    }

    private static Task AddBriefIncidentsAsync(AiTestHost host, int monitorId, int count, DateTime newest) => host.WithDbAsync(async db =>
    {
        for (var i = 0; i < count; i++)
        {
            var start = newest.AddHours(-i * 2);
            db.Incidents.Add(new Incident { MonitorId = monitorId, StartedAtUtc = start, ResolvedAtUtc = start.AddSeconds(62), Cause = "Timeout after 10s" });
        }
        await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Changing_the_setting_restarts_the_evaluation_instead_of_ratcheting()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var m = await FlappingMonitorAsync(host, c.Id);
        await RunJobAsync(host);

        // The threshold was raised (edit / apply set SettingsChangedAtUtc): the old incidents happened under threshold 1
        // and say nothing about threshold 2.
        await host.WithDbAsync(db => db.Monitors.Where(x => x.Id == m.Id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.FailureThreshold, 2).SetProperty(x => x.SettingsChangedAtUtc, host.Now)));
        await RunJobAsync(host);
        var list = Assert.Single(await SuggestionsAsync(host, m.Id));
        Assert.Equal(SettingsSuggestionStatus.Obsolete, list.Status);

        // Still flapping under the new threshold, with fresh evidence: then 2 -> 3 is suggested.
        host.Time.Advance(TimeSpan.FromDays(2));
        await AddBriefIncidentsAsync(host, m.Id, 6, host.Now.AddMinutes(-30));
        await RunJobAsync(host);
        var fresh = (await SuggestionsAsync(host, m.Id))[1];
        Assert.Equal(("2", "3", SettingsSuggestionStatus.Open), (fresh.CurrentValue, fresh.SuggestedValue, fresh.Status));
        Assert.Equal("6", SuggestionFact.Get(SuggestionFact.Parse(fresh.EvidenceJson), "incidents"));
        Assert.Equal("2", SuggestionFact.Get(SuggestionFact.Parse(fresh.EvidenceJson), "window_days")); // only the days since the change

        await host.WithDbAsync(db => db.Incidents.Where(i => i.MonitorId == m.Id).ExecuteDeleteAsync());
        await RunJobAsync(host);
        Assert.All(await SuggestionsAsync(host, m.Id), s => Assert.Equal(SettingsSuggestionStatus.Obsolete, s.Status));
    }

    [Fact]
    public async Task Applied_kind_is_not_suggested_again_within_the_window()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var m = await FlappingMonitorAsync(host, c.Id);
        await RunJobAsync(host);
        // Fallback when no change time is known (e.g. a row applied before SettingsChangedAtUtc existed).
        await host.WithDbAsync(db => db.Monitors.Where(x => x.Id == m.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.FailureThreshold, 2)));
        await host.WithDbAsync(db => db.SettingsSuggestions.ExecuteUpdateAsync(u => u
            .SetProperty(s => s.Status, SettingsSuggestionStatus.Applied).SetProperty(s => s.ClosedAtUtc, host.Now)));

        await RunJobAsync(host);
        Assert.Single(await SuggestionsAsync(host, m.Id));
    }

    [Fact]
    public void Timeouts_and_keyword_misses_recorded_under_other_settings_do_not_count()
    {
        // Timeouts at the old 5 s timeout say nothing about the current 10 s one.
        var oldTimeouts = Sorted(Ups(1000).Concat(Ups(4, ms: 9000, everyMinutes: 300, startMinutesAgo: 2))
            .Concat(Enumerable.Range(0, 30).Select(i => new Check(Now.AddMinutes(-3 - i * 60), false, null, 5000, "Timeout after 5s"))));
        Assert.Null(NearTimeout(Settings(timeout: 10), oldTimeouts, 7));
        Assert.NotNull(NearTimeout(Settings(timeout: 5, interval: 60), Sorted(oldTimeouts.Where(x => !x.IsUp).Concat(Ups(1000))), 7));
        Assert.True(IsTimeoutAt("Timeout after 7.5s", 7) == false && IsTimeoutAt("Timeout after 10s", 10));

        // Misses of the previous keyword do not keep a review of the new one alive.
        var oldMisses = Sorted(Ups(200).Concat(Enumerable.Range(0, 40).Select(i => new Check(Now.AddMinutes(-2 - i * 5), false, 200, 300, "Keyword 'Old' not found in response"))));
        Assert.Null(Keyword(Settings(MonitorType.Keyword, keyword: "New"), oldMisses, 7));
        Assert.NotNull(Keyword(Settings(MonitorType.Keyword, keyword: "Old"), oldMisses, 7));
    }

    [Fact]
    public async Task Confirmation_probes_are_not_counted()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id, x => { x.Type = MonitorType.Keyword; x.Keyword = "Hello"; x.Status = MonitorStatus.Up; x.CreatedAtUtc = host.Now.AddDays(-30); });
        await host.WithDbAsync(async db =>
        {
            for (var i = 0; i < 100; i++)
                db.CheckResults.Add(new CheckResult { MonitorId = m.Id, CheckedAtUtc = host.Now.AddMinutes(-10 - i * 5), IsUp = true, StatusCode = 200, ResponseTimeMs = 100 });
            // Only confirmation probes miss the keyword (each failure is followed by its confirmation, counted once).
            for (var i = 0; i < 20; i++)
                db.CheckResults.Add(new CheckResult
                {
                    MonitorId = m.Id, CheckedAtUtc = host.Now.AddMinutes(-12 - i * 5), IsUp = false, StatusCode = 200,
                    ResponseTimeMs = 100, Error = "Keyword 'Hello' not found in response", IsConfirmation = true
                });
            await db.SaveChangesAsync();
        });
        await RunJobAsync(host);
        Assert.Empty(await SuggestionsAsync(host, m.Id));
    }

    [Fact]
    public async Task Timeout_suggestion_is_kept_while_the_proposal_stays_within_the_band_and_its_evidence_is_refreshed()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id, x => { x.TimeoutSeconds = 10; x.IntervalSeconds = 60; x.Status = MonitorStatus.Up; x.CreatedAtUtc = host.Now.AddDays(-60); });
        async Task AddChecksAsync(int slowMs, DateTime now) => await host.WithDbAsync(async db =>
        {
            for (var i = 0; i < 300; i++)
                db.CheckResults.Add(new CheckResult { MonitorId = m.Id, CheckedAtUtc = now.AddMinutes(-1 - i * 5), IsUp = true, StatusCode = 200, ResponseTimeMs = 200 });
            for (var i = 0; i < 20; i++)
                db.CheckResults.Add(new CheckResult { MonitorId = m.Id, CheckedAtUtc = now.AddMinutes(-3 - i * 60), IsUp = true, StatusCode = 200, ResponseTimeMs = slowMs });
            await db.SaveChangesAsync();
        });
        await AddChecksAsync(9000, host.Now);
        await RunJobAsync(host);
        var first = Assert.Single(await SuggestionsAsync(host, m.Id));
        Assert.Equal("14", first.SuggestedValue);

        // Slightly slower a week later: ceil(1.5 x 9300) = 14 -> 14; 9400 -> 15 (within 2 s): same row, evidence refreshed.
        host.Time.Advance(TimeSpan.FromDays(8));
        await AddChecksAsync(9400, host.Now);
        await RunJobAsync(host);
        var kept = Assert.Single(await SuggestionsAsync(host, m.Id));
        Assert.Equal((first.Id, SettingsSuggestionStatus.Open, "14"), (kept.Id, kept.Status, kept.SuggestedValue));
        Assert.Equal(host.Now, kept.EvidenceUpdatedAtUtc);
        Assert.Equal("9400", SuggestionFact.Get(SuggestionFact.Parse(kept.EvidenceJson), "p99_ms"));

        // A much larger proposal replaces it.
        host.Time.Advance(TimeSpan.FromDays(8));
        await AddChecksAsync(20000, host.Now);
        await RunJobAsync(host);
        var list = await SuggestionsAsync(host, m.Id);
        Assert.Equal(SettingsSuggestionStatus.Obsolete, list[0].Status);
        Assert.Equal(("30", SettingsSuggestionStatus.Open), (list[1].SuggestedValue, list[1].Status));
    }

    [Fact]
    public async Task Job_never_overwrites_a_dismissal_and_closes_suggestions_of_ineligible_customers()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var m = await FlappingMonitorAsync(host, c.Id);
        await RunJobAsync(host);
        // Dismissed by a user while the condition went away: stays Dismissed (conditional close).
        await host.WithDbAsync(db => db.SettingsSuggestions.ExecuteUpdateAsync(u => u
            .SetProperty(s => s.Status, SettingsSuggestionStatus.Dismissed).SetProperty(s => s.ClosedAtUtc, host.Now)));
        await host.WithDbAsync(db => db.Incidents.Where(i => i.MonitorId == m.Id).ExecuteDeleteAsync());
        await RunJobAsync(host);
        Assert.Equal(SettingsSuggestionStatus.Dismissed, Assert.Single(await SuggestionsAsync(host, m.Id)).Status);

        // Opt-out closes what is open.
        var c2 = await host.AddCustomerAsync();
        var m2 = await FlappingMonitorAsync(host, c2.Id);
        await RunJobAsync(host);
        await host.WithDbAsync(db => db.Customers.Where(x => x.Id == c2.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.AiEnabled, false)));
        await RunJobAsync(host);
        Assert.Equal(SettingsSuggestionStatus.Obsolete, Assert.Single(await SuggestionsAsync(host, m2.Id)).Status);
    }

    [Fact]
    public async Task Failed_skipped_or_outdated_explanations_are_queued_again_after_the_cooldown()
    {
        // A queue that accepts every job (a worker would take each one right away).
        await using var host = await AiTestHost.CreateAsync(new() { ["Ai:RetryCooldownMinutes"] = "60" },
            configure: services => services.AddSingleton<IAiJobQueue>(new AcceptingQueue()));
        var c = await host.AddCustomerAsync();
        var m = await FlappingMonitorAsync(host, c.Id);
        Assert.Equal(1, (await RunJobAsync(host)).Queued);
        var s = Assert.Single(await SuggestionsAsync(host, m.Id));
        Task<SettingsSuggestionJob.RunSummary> Drained() => RunJobAsync(host);

        async Task DecisionAsync(AiDecisionStatus status) => await host.WithDbAsync(async db =>
        {
            db.AiDecisions.Add(new AiDecision
            {
                CustomerId = c.Id, Feature = AiFeatures.SettingsSuggestions, SubjectType = SettingsSuggestionProvider.SubjectType,
                SubjectId = s.Id.ToString(CultureInfo.InvariantCulture), Model = "m", Effort = "low", Status = status, CreatedAtUtc = host.Now
            });
            await db.SaveChangesAsync();
        });

        await DecisionAsync(AiDecisionStatus.Failed);
        Assert.Equal(0, (await Drained()).Queued);   // within the cool-down
        host.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(1, (await Drained()).Queued);   // retried

        await DecisionAsync(AiDecisionStatus.SkippedBudget);
        host.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(1, (await Drained()).Queued);

        await DecisionAsync(AiDecisionStatus.Succeeded);
        host.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(0, (await Drained()).Queued);   // done
    }

    [Fact]
    public async Task Keywords_never_reach_the_model_through_error_texts()
    {
        Assert.Equal(TriggerCheck.Ai.Context.AiMasker.KeywordMissText, TriggerCheck.Ai.Context.AiMasker.MaskText("Keyword 'Open Sesame' not found in response"));
        Assert.DoesNotContain("0.99", TriggerCheck.Ai.Context.AiMasker.MaskText("JSON value at $.price is 12 (expected 0.99)"));

        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id, x => { x.Type = MonitorType.Keyword; x.Keyword = "Open Sesame"; x.Status = MonitorStatus.Down; });
        var (suggestionId, incidentId) = await host.WithDbAsync(async db =>
        {
            var sug = new SettingsSuggestion
            {
                CustomerId = c.Id, MonitorId = m.Id, Kind = SettingsSuggestionKind.TargetMayBeWrong, CreatedAtUtc = host.Now,
                EvidenceJson = SuggestionFact.Serialize([new("failed_checks", "40"), new(TopErrorKey, "Keyword 'Open Sesame' not found in response")])
            };
            db.SettingsSuggestions.Add(sug);
            db.CheckResults.Add(new CheckResult { MonitorId = m.Id, CheckedAtUtc = host.Now.AddMinutes(-1), StatusCode = 200, Error = "Keyword 'Open Sesame' not found in response" });
            var inc = new Incident { MonitorId = m.Id, StartedAtUtc = host.Now.AddMinutes(-2), Cause = "Keyword 'Open Sesame' not found in response" };
            db.Incidents.Add(inc);
            await db.SaveChangesAsync();
            return (sug.Id, inc.Id);
        });

        var prompts = await host.InScopeAsync(async sp => new[]
        {
            await ActivatorUtilities.CreateInstance<SettingsSuggestionProvider>(sp).BuildAsync(JobFor(c.Id, suggestionId), default),
            await ActivatorUtilities.CreateInstance<IncidentInterpretationProvider>(sp)
                .BuildAsync(new AiJob(AiFeatures.IncidentInterpretation, c.Id, AiSubjects.Incident, incidentId.ToString(CultureInfo.InvariantCulture)), default),
        });
        Assert.All(prompts, p =>
        {
            Assert.DoesNotContain("Sesame", p!.UserContent);
            Assert.Contains("Keyword not found in response", p.UserContent);
        });
    }

    [Fact]
    public async Task Closed_suggestions_are_kept_for_the_dismissal_period_then_follow_retention()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id);
        var planId = await host.WithDbAsync(db => db.Customers.Where(x => x.Id == c.Id).Select(x => x.PlanId).FirstAsync());
        await host.WithDbAsync(db => db.Plans.Where(p => p.Id == planId).ExecuteUpdateAsync(u => u.SetProperty(p => p.RetentionDays, 7)));
        await host.WithDbAsync(async db =>
        {
            foreach (var days in new[] { 20, 40 })
                db.SettingsSuggestions.Add(new SettingsSuggestion
                {
                    CustomerId = c.Id, MonitorId = m.Id, Status = SettingsSuggestionStatus.Dismissed,
                    CreatedAtUtc = host.Now.AddDays(-days), ClosedAtUtc = host.Now.AddDays(-days)
                });
            await db.SaveChangesAsync();
        });
        Task CleanAsync(Microsoft.Extensions.Configuration.IConfiguration? config) => host.InScopeAsync(sp => new TriggerCheck.Monitoring.Retention.RetentionCleaner(
            sp.GetRequiredService<IDbContextFactory<TriggerCheck.Infrastructure.Data.AppDbContext>>(), host.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TriggerCheck.Monitoring.Retention.RetentionCleaner>.Instance, config).CleanupAsync());

        await CleanAsync(null); // default 30 dismissal days: the 20-day-old dismissal stays despite 7 days of retention
        Assert.Single(await SuggestionsAsync(host, m.Id));
        await CleanAsync(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:Suggestions:DismissDays"] = "10" }).Build());
        Assert.Empty(await SuggestionsAsync(host, m.Id));
    }

    [Fact]
    public async Task Dismissed_kind_is_not_suggested_again_for_30_days()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var m = await FlappingMonitorAsync(host, c.Id);
        await RunJobAsync(host);
        await host.WithDbAsync(db => db.SettingsSuggestions.ExecuteUpdateAsync(u => u
            .SetProperty(s => s.Status, SettingsSuggestionStatus.Dismissed).SetProperty(s => s.ClosedAtUtc, host.Now)));

        host.Time.Advance(TimeSpan.FromDays(29));
        await host.WithDbAsync(db => db.Incidents.Where(i => i.MonitorId == m.Id).ExecuteUpdateAsync(u => u
            .SetProperty(i => i.StartedAtUtc, i => i.StartedAtUtc.AddDays(29)).SetProperty(i => i.ResolvedAtUtc, i => i.ResolvedAtUtc!.Value.AddDays(29))));
        await RunJobAsync(host);
        Assert.Single(await SuggestionsAsync(host, m.Id)); // still only the dismissed one

        host.Time.Advance(TimeSpan.FromDays(2)); // 31 days after the dismissal
        await host.WithDbAsync(db => db.Incidents.Where(i => i.MonitorId == m.Id).ExecuteUpdateAsync(u => u
            .SetProperty(i => i.StartedAtUtc, i => i.StartedAtUtc.AddDays(2)).SetProperty(i => i.ResolvedAtUtc, i => i.ResolvedAtUtc!.Value.AddDays(2))));
        await RunJobAsync(host);
        var list = await SuggestionsAsync(host, m.Id);
        Assert.Equal(2, list.Count);
        Assert.Equal(SettingsSuggestionStatus.Open, list[1].Status);
    }

    [Theory]
    [InlineData("optout")]
    [InlineData("plan")]
    [InlineData("nocalls")]
    public async Task Customers_without_the_feature_get_no_suggestions(string why)
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = why switch
        {
            "optout" => await host.AddCustomerAsync(aiEnabled: false),
            "plan" => await host.AddCustomerAsync(features: AiFeatures.Format([AiFeatures.IncidentInterpretation, AiFeatures.SlowdownExplanation])),
            _ => await host.AddCustomerAsync(callLimit: 0),
        };
        var m = await FlappingMonitorAsync(host, c.Id);
        await RunJobAsync(host);
        Assert.Empty(await SuggestionsAsync(host, m.Id));
    }

    [Fact]
    public async Task Detection_runs_without_AI_but_no_explanation_is_queued()
    {
        await using var host = await AiTestHost.CreateAsync(new() { ["Ai:Enabled"] = "false" });
        var c = await host.AddCustomerAsync();
        var m = await FlappingMonitorAsync(host, c.Id);
        var r = await RunJobAsync(host);
        Assert.Single(await SuggestionsAsync(host, m.Id));
        Assert.Equal(0, r.Queued);
    }

    private static AiJob JobFor(int customerId, long suggestionId, string language = "en") =>
        new(AiFeatures.SettingsSuggestions, customerId, SettingsSuggestionProvider.SubjectType, suggestionId.ToString(CultureInfo.InvariantCulture), language);

    private const string GoodAnswer = """
        {"summary":"In the last 7 days 14 of 14 incidents recovered at the next check. Raising the threshold from 1 to 2 avoids these alerts.",
         "hypotheses":[{"cause":"Short blips end before the next check.","confidence":"high","evidence":["14 brief incidents"],"category":"raise_failure_threshold","evidence_refs":["brief_incidents"]},
                       {"cause":"Unrelated.","confidence":"low","evidence":["x"],"category":"raise_timeout","evidence_refs":["p99_ms"]}],
         "suggested_actions":[{"action":"raise_failure_threshold","description":"Set the threshold to 2.","autonomy":"A3","reversible":true}],
         "unknowns":[]}
        """;

    [Fact]
    public async Task Explanation_is_recorded_masked_grounded_and_clamped_to_A1()
    {
        await using var host = await AiTestHost.CreateAsync();
        host.Client.Responder = _ => FakeAiClient.Success(GoodAnswer);
        var c = await host.AddCustomerAsync();
        var m = await FlappingMonitorAsync(host, c.Id);
        await RunJobAsync(host);
        var s = Assert.Single(await SuggestionsAsync(host, m.Id));

        var d = await host.RunAsync(JobFor(c.Id, s.Id));

        Assert.Equal(AiDecisionStatus.Succeeded, d!.Status);
        Assert.Equal(AiFeatures.SettingsSuggestions, d.Feature);
        Assert.Equal(AiAutonomy.A1, d.AutonomyLevel); // A3 from the model is clamped to the feature's A1
        var output = AiInterpretation.TryParse(d.OutputJson)!;
        Assert.Equal("raise_failure_threshold", Assert.Single(output.Hypotheses).Category); // wrong-category ref dropped
        var request = Assert.Single(host.Client.Requests);
        Assert.Contains("brief_incidents: 14", request.UserContent);
        Assert.DoesNotContain("secret", request.UserContent);
        Assert.DoesNotContain("Flappy", request.UserContent);
    }

    [Fact]
    public async Task Explanation_with_an_invented_number_is_rejected()
    {
        await using var host = await AiTestHost.CreateAsync();
        host.Client.Responder = _ => FakeAiClient.Success(GoodAnswer.Replace("14 of 14", "21 of 25"));
        var c = await host.AddCustomerAsync();
        var m = await FlappingMonitorAsync(host, c.Id);
        await RunJobAsync(host);
        var s = Assert.Single(await SuggestionsAsync(host, m.Id));

        var d = await host.RunAsync(JobFor(c.Id, s.Id));
        Assert.Equal((AiDecisionStatus.Failed, "invalid_output"), (d!.Status, d.ErrorCode));
        Assert.Null(d.OutputJson);
    }

    [Fact]
    public async Task Explanation_is_tenant_scoped_and_respects_plan_and_budget()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var m = await FlappingMonitorAsync(host, c.Id);
        await RunJobAsync(host);
        var s = Assert.Single(await SuggestionsAsync(host, m.Id));

        // Another tenant asking for this suggestion: the provider finds nothing, nothing is sent.
        var other = await host.AddCustomerAsync();
        Assert.Null(await host.RunAsync(JobFor(other.Id, s.Id)));
        Assert.Empty(host.Client.Requests);

        // Plan without AI-5.
        var basic = await host.AddCustomerAsync(features: AiFeatures.IncidentInterpretation);
        var mb = await FlappingMonitorAsync(host, basic.Id);
        var sb = await host.WithDbAsync(async db =>
        {
            var x = new SettingsSuggestion { CustomerId = basic.Id, MonitorId = mb.Id, Kind = SettingsSuggestionKind.RaiseFailureThreshold, CurrentValue = "1", SuggestedValue = "2", CreatedAtUtc = host.Now };
            db.SettingsSuggestions.Add(x);
            await db.SaveChangesAsync();
            return x;
        });
        Assert.Equal(AiDecisionStatus.SkippedPlan, (await host.RunAsync(JobFor(basic.Id, sb.Id)))!.Status);

        // Budget used up (AI-10): skipped, the rule-based suggestion itself stays.
        var poor = await host.AddCustomerAsync(callLimit: 1);
        var mp = await FlappingMonitorAsync(host, poor.Id);
        await host.WithDbAsync(async db =>
        {
            db.AiDecisions.Add(new AiDecision
            {
                CustomerId = poor.Id, Feature = AiFeatures.IncidentInterpretation, SubjectType = "Incident", SubjectId = "1",
                Model = "m", Effort = "low", Status = AiDecisionStatus.Succeeded, InputTokens = 10, OutputTokens = 10, CreatedAtUtc = host.Now
            });
            await db.SaveChangesAsync();
        });
        await RunJobAsync(host);
        var sp = Assert.Single(await SuggestionsAsync(host, mp.Id));
        Assert.Equal(AiDecisionStatus.SkippedBudget, (await host.RunAsync(JobFor(poor.Id, sp.Id)))!.Status);
        Assert.Equal(SettingsSuggestionStatus.Open, (await SuggestionsAsync(host, mp.Id))[0].Status);
    }
}

internal sealed class AcceptingQueue : IAiJobQueue
{
    public bool TryEnqueue(AiJob job) => true;
}
