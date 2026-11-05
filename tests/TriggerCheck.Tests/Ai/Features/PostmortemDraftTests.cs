using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Ai.Features;
using TriggerCheck.Ai.Features.Postmortem;
using TriggerCheck.Ai.Narrative;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>
/// AI-8 generation (fake client, no network): deterministic facts, server-rendered numbers, sentence-level grounding,
/// gating (off / opt-out / plan / budget), tenant isolation and that generating never publishes anything.
/// </summary>
public class PostmortemDraftTests
{
    public static string Answer(string message, string cause = "The checks failed with {{incident.error}} ({{incident.status_code}}) for {{incident.duration}}.",
        string category = "http_5xx", string confidence = "high", params string[] unknowns) =>
        JsonSerializer.Serialize(new
        {
            likely_cause = new { category, confidence, text = cause },
            customer_message = message,
            unknowns,
        });

    public const string GoodMessage = "{{service}} was unavailable from {{incident.started_at}} to {{incident.resolved_at}} ({{incident.duration}}). The issue is resolved and we are reviewing what happened.";

    private static void Respond(AiTestHost host, string json) => host.Client.Responder = _ => FakeAiClient.Success(json);

    /// <summary>A resolved 75-minute HTTP 503 incident on a monitor that is shown on a status page as "Checkout".</summary>
    private static async Task<(int Customer, int Monitor, int Incident)> ArrangeAsync(AiTestHost host, bool aiEnabled = true, string? features = null,
        int callLimit = 100, string language = "en", bool onStatusPage = true)
    {
        var c = await host.AddCustomerAsync(aiEnabled, callLimit, features: features);
        await host.WithDbAsync(db => db.Customers.Where(x => x.Id == c.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AiLanguage, language)));
        var m = await host.AddMonitorAsync(c.Id, x =>
        {
            x.Name = "Internal checkout probe";
            x.Target = "https://shop.example.test/checkout/secret-path?token=abc123";
        });
        var start = host.Now.AddHours(-3);
        var incidentId = await host.WithDbAsync(async db =>
        {
            var i = new Incident { MonitorId = m.Id, StartedAtUtc = start, ResolvedAtUtc = start.AddMinutes(75), Cause = "HTTP 503 Service Unavailable. Email ops@example.test" };
            db.Incidents.Add(i);
            db.CheckResults.Add(new CheckResult { MonitorId = m.Id, CheckedAtUtc = start.AddMinutes(-5), IsUp = true, StatusCode = 200, ResponseTimeMs = 120 });
            db.CheckResults.Add(new CheckResult { MonitorId = m.Id, CheckedAtUtc = start.AddMinutes(-1), IsUp = false, StatusCode = 503, ResponseTimeMs = 300, Error = "HTTP 503" });
            for (var k = 0; k < 5; k++)
                db.CheckResults.Add(new CheckResult { MonitorId = m.Id, CheckedAtUtc = start.AddMinutes(10 * k), IsUp = false, StatusCode = 503, ResponseTimeMs = 310, Error = "HTTP 503 Service Unavailable" });
            db.CheckResults.Add(new CheckResult { MonitorId = m.Id, CheckedAtUtc = start.AddMinutes(75), IsUp = true, StatusCode = 200, ResponseTimeMs = 130 });
            if (onStatusPage)
                db.StatusPages.Add(new StatusPage
                {
                    CustomerId = c.Id, Title = "Acme status", Slug = "acme-" + Guid.NewGuid().ToString("N")[..8],
                    Monitors = [new StatusPageMonitor { MonitorId = m.Id, DisplayName = "Checkout" }]
                });
            await db.SaveChangesAsync();
            return i.Id;
        });
        return (c.Id, m.Id, incidentId);
    }

    private static Task<PostmortemGenerateResult> GenerateAsync(AiTestHost host, int customerId, int incidentId) =>
        host.InScopeAsync(sp => sp.GetRequiredService<PostmortemDrafts>().GenerateAsync(customerId, incidentId, "user-1"));

    [Fact]
    public async Task Generation_renders_server_values_and_never_publishes()
    {
        await using var host = await AiTestHost.CreateAsync();
        var (cid, _, incident) = await ArrangeAsync(host);
        Respond(host, Answer(GoodMessage, unknowns: "Whether a deployment happened at {{incident.started_at}}."));

        var result = await GenerateAsync(host, cid, incident);

        Assert.Equal(PostmortemGenerateStatus.Generated, result.Status);
        var draft = await host.WithDbAsync(db => db.PostmortemDrafts.SingleAsync(d => d.IncidentId == incident));
        Assert.Equal(PostmortemDraftStatus.Draft, draft.Status);
        Assert.Equal(cid, draft.CustomerId);
        Assert.Equal("http_5xx", draft.CauseCategory);
        Assert.Equal(result.DecisionId, draft.DecisionId);
        // Numbers come from the database (75 minutes, HTTP 503), the public display name from the status page.
        Assert.StartsWith("Checkout was unavailable from 10 March 2026, 09:00 (UTC) to 10 March 2026, 10:15 (UTC) (1 h 15 min).", draft.CustomerMessage);
        Assert.Contains("server errors (HTTP 5xx) (HTTP 503) for 1 h 15 min", draft.LikelyCause);
        Assert.DoesNotContain("{{", draft.CustomerMessage + draft.LikelyCause);
        Assert.Null(draft.ApprovedAtUtc);

        // Exactly one audited decision at autonomy A2; nothing reached a status page.
        var decision = await host.WithDbAsync(db => db.AiDecisions.SingleAsync(d => d.Feature == AiFeatures.PostmortemDraft));
        Assert.Equal(AiDecisionStatus.Succeeded, decision.Status);
        Assert.Equal(AiAutonomy.A2, decision.AutonomyLevel);
        Assert.Equal(AiSubjects.Incident, decision.SubjectType);
        Assert.Equal(0, await host.WithDbAsync(db => db.StatusPageNotes.CountAsync()));

        // K5: host only, no path / query / monitor names / e-mail; error texts are untrusted data.
        var request = Assert.Single(host.Client.Requests);
        var sent = request.SystemPrompt + request.UserContent;
        Assert.Contains("shop.example.test", request.UserContent);
        Assert.DoesNotContain("secret-path", sent);
        Assert.DoesNotContain("abc123", sent);
        Assert.DoesNotContain("Internal checkout probe", sent);
        Assert.DoesNotContain("Checkout", request.UserContent);
        Assert.DoesNotContain("ops@example.test", sent);
        Assert.Contains("<untrusted_data source=\"incident_cause\">", request.UserContent);
        Assert.Contains("incident.duration: 1 h 15 min", request.UserContent);
    }

    [Fact]
    public async Task Turkish_draft_renders_values_in_Turkish_and_without_a_status_page_uses_a_generic_name()
    {
        await using var host = await AiTestHost.CreateAsync();
        var (cid, _, incident) = await ArrangeAsync(host, language: "tr", onStatusPage: false);
        Respond(host, Answer("{{service}} {{incident.started_at}} ile {{incident.resolved_at}} arasında ({{incident.duration}}) erişilemedi. Sorun çözüldü.",
            "Kontroller {{incident.duration}} boyunca {{incident.error}} ile başarısız oldu."));

        Assert.Equal(PostmortemGenerateStatus.Generated, (await GenerateAsync(host, cid, incident)).Status);

        var draft = await host.WithDbAsync(db => db.PostmortemDrafts.SingleAsync(d => d.IncidentId == incident));
        Assert.Equal("tr", draft.Language);
        Assert.StartsWith("hizmetimiz 10 Mart 2026 09:00 (UTC) ile 10 Mart 2026 10:15 (UTC) arasında (1 sa 15 dk) erişilemedi.", draft.CustomerMessage);
        Assert.Contains("sunucu hataları (HTTP 5xx)", draft.LikelyCause);
        Assert.StartsWith("response_language: Turkish", Assert.Single(host.Client.Requests).UserContent);
    }

    [Fact]
    public async Task Sentences_with_invented_numbers_links_or_unsupported_claims_are_removed()
    {
        await using var host = await AiTestHost.CreateAsync();
        var (cid, _, incident) = await ArrangeAsync(host);
        Respond(host, Answer(
            GoodMessage + " About 4000 customers were affected. No data was lost. Contact support at help.example.test for details.\nWe apologise for the inconvenience.",
            "The upstream returned errors for {{incident.duration}}. It happened twice before."));

        Assert.Equal(PostmortemGenerateStatus.Generated, (await GenerateAsync(host, cid, incident)).Status);

        var draft = await host.WithDbAsync(db => db.PostmortemDrafts.SingleAsync(d => d.IncidentId == incident));
        Assert.DoesNotContain("4000", draft.CustomerMessage);
        Assert.DoesNotContain("data", draft.CustomerMessage);
        Assert.DoesNotContain("example.test", draft.CustomerMessage);
        Assert.DoesNotContain("twice", draft.LikelyCause);
        Assert.Contains("We apologise for the inconvenience.", draft.CustomerMessage);
        var output = PostmortemOutput.TryParse(await host.WithDbAsync(db => db.AiDecisions.Select(d => d.OutputJson).SingleAsync()));
        Assert.Equal(4, output!.RemovedSentences);
    }

    [Fact]
    public async Task Mostly_ungrounded_output_is_rejected_and_no_draft_is_written()
    {
        await using var host = await AiTestHost.CreateAsync();
        var (cid, _, incident) = await ArrangeAsync(host);
        Respond(host, Answer("The outage lasted 75 minutes. It affected 3 regions. {{service}} is back.", "Unknown key {{incident.users}} broke."));

        var result = await GenerateAsync(host, cid, incident);

        Assert.Equal(PostmortemGenerateStatus.Failed, result.Status);
        var decision = await host.WithDbAsync(db => db.AiDecisions.SingleAsync());
        Assert.Equal(AiDecisionStatus.Failed, decision.Status);
        Assert.Equal("ungrounded_numbers", decision.ErrorCode);
        Assert.False(await host.WithDbAsync(db => db.PostmortemDrafts.AnyAsync()));
    }

    [Fact]
    public async Task Unchanged_incident_reuses_the_answer_without_a_new_call()
    {
        await using var host = await AiTestHost.CreateAsync();
        var (cid, _, incident) = await ArrangeAsync(host);
        Respond(host, Answer(GoodMessage));

        Assert.Equal(PostmortemGenerateStatus.Generated, (await GenerateAsync(host, cid, incident)).Status);
        Assert.Equal(PostmortemGenerateStatus.Generated, (await GenerateAsync(host, cid, incident)).Status);

        Assert.Single(host.Client.Requests);
        Assert.Equal(1, await host.WithDbAsync(db => db.AiDecisions.CountAsync()));
        Assert.Equal(1, await host.WithDbAsync(db => db.PostmortemDrafts.CountAsync()));
    }

    [Fact]
    public async Task Disabled_opt_out_plan_and_budget_give_no_draft_and_no_call()
    {
        await using (var off = await AiTestHost.CreateAsync(new() { ["Ai:Enabled"] = "false" }))
        {
            var (cid, _, incident) = await ArrangeAsync(off);
            Assert.Equal(PostmortemGenerateStatus.Off, (await GenerateAsync(off, cid, incident)).Status);
            Assert.False(await off.WithDbAsync(db => db.AiDecisions.AnyAsync())); // switched off: not even a row
        }

        await using var host = await AiTestHost.CreateAsync();
        var optOut = await ArrangeAsync(host, aiEnabled: false);
        Assert.Equal(PostmortemGenerateStatus.OptOut, (await GenerateAsync(host, optOut.Customer, optOut.Incident)).Status);

        var noPlan = await ArrangeAsync(host, features: AiFeatures.IncidentInterpretation);
        Assert.Equal(PostmortemGenerateStatus.NotInPlan, (await GenerateAsync(host, noPlan.Customer, noPlan.Incident)).Status);

        var budget = await ArrangeAsync(host, callLimit: 1);
        await host.WithDbAsync(async db =>
        {
            db.AiDecisions.Add(new AiDecision
            {
                CustomerId = budget.Customer, Feature = AiFeatures.IncidentInterpretation, SubjectType = "Incident", SubjectId = "0", Model = "m",
                Effort = "low", Status = AiDecisionStatus.Succeeded, InputTokens = 10, OutputTokens = 10, CreatedAtUtc = host.Now
            });
            await db.SaveChangesAsync();
        });
        Assert.Equal(PostmortemGenerateStatus.OverBudget, (await GenerateAsync(host, budget.Customer, budget.Incident)).Status);

        Assert.Empty(host.Client.Requests);
        Assert.False(await host.WithDbAsync(db => db.PostmortemDrafts.AnyAsync()));
        // Skips are audited (one row per request); the budget skip too.
        Assert.Equal(3, await host.WithDbAsync(db => db.AiDecisions.CountAsync(d => d.Feature == AiFeatures.PostmortemDraft)));
    }

    [Fact]
    public async Task Other_tenants_and_open_incidents_get_nothing()
    {
        await using var host = await AiTestHost.CreateAsync();
        Respond(host, Answer(GoodMessage));
        var (_, _, incident) = await ArrangeAsync(host);
        var other = await host.AddCustomerAsync();

        Assert.Equal(PostmortemGenerateStatus.NotFound, (await GenerateAsync(host, other.Id, incident)).Status);
        // The provider itself is tenant-scoped: the other customer's job builds no prompt.
        var prompt = await host.InScopeAsync(sp => sp.GetServices<IAiContextProvider>().OfType<PostmortemDraftProvider>().Single()
            .BuildAsync(new AiJob(AiFeatures.PostmortemDraft, other.Id, AiSubjects.Incident, incident.ToString()), CancellationToken.None));
        Assert.Null(prompt);
        Assert.Null(await host.InScopeAsync(sp => sp.GetRequiredService<PostmortemFactsLoader>().LoadAsync(other.Id, incident)));

        var (cid, monitor, _) = await ArrangeAsync(host);
        var open = await host.WithDbAsync(async db =>
        {
            var i = new Incident { MonitorId = monitor, StartedAtUtc = host.Now.AddMinutes(-5), Cause = "HTTP 503" };
            db.Incidents.Add(i);
            await db.SaveChangesAsync();
            return i.Id;
        });
        Assert.Equal(PostmortemGenerateStatus.NotResolved, (await GenerateAsync(host, cid, open)).Status);
        Assert.Empty(host.Client.Requests);
        Assert.False(await host.WithDbAsync(db => db.PostmortemDrafts.AnyAsync()));
    }

    [Fact]
    public async Task Facts_build_the_timeline_and_impact_from_the_database()
    {
        await using var host = await AiTestHost.CreateAsync();
        var (cid, monitor, incident) = await ArrangeAsync(host);
        var start = host.Now.AddHours(-3);
        var sibling = await host.AddMonitorAsync(cid, m => { m.Name = "Shop API"; m.Target = "https://shop.example.test/api"; });
        var elsewhere = await host.AddMonitorAsync(cid, m => { m.Name = "Blog"; m.Target = "https://blog.example.test/"; });
        await host.WithDbAsync(async db =>
        {
            db.Incidents.Add(new Incident { MonitorId = sibling.Id, StartedAtUtc = start.AddMinutes(2), ResolvedAtUtc = start.AddMinutes(60), Cause = "HTTP 503" });
            db.Incidents.Add(new Incident { MonitorId = elsewhere.Id, StartedAtUtc = start.AddMinutes(2), ResolvedAtUtc = start.AddMinutes(60), Cause = "Timeout" });
            await db.Incidents.Where(i => i.Id == incident).ExecuteUpdateAsync(s => s.SetProperty(i => i.AcknowledgedAtUtc, start.AddMinutes(20)));
            await db.SaveChangesAsync();
        });

        var facts = (await host.InScopeAsync(sp => sp.GetRequiredService<PostmortemFactsLoader>().LoadAsync(cid, incident)))!;

        Assert.Equal(75, facts.DurationMinutes);
        Assert.Equal(start.AddMinutes(-1), facts.FirstFailureAtUtc);
        Assert.Equal(6, facts.FailedChecks);
        Assert.Equal(503, facts.StatusCode);
        Assert.Equal("http_5xx", facts.ErrorClass);
        Assert.Equal(20, facts.AcknowledgedAfterMinutes);
        var related = Assert.Single(facts.Related); // same host only, not the blog
        Assert.Equal(sibling.Id, related.MonitorId);
        Assert.Equal("Checkout", Assert.Single(facts.StatusPages).DisplayName);
        Assert.Equal(
            [PostmortemEventKind.FirstFailure, PostmortemEventKind.IncidentOpened, PostmortemEventKind.RelatedIncident, PostmortemEventKind.Acknowledged, PostmortemEventKind.Resolved],
            facts.Timeline.Select(e => e.Kind).ToArray());
        var values = facts.Values();
        Assert.Equal(1, values["related.monitors"].Number);
        Assert.Equal(NarrativeValueKind.Duration, values["incident.acknowledged_after"].Kind);
        Assert.True(monitor > 0);
    }

    [Theory]
    [InlineData("It was down for 75 minutes.", "digits")]
    [InlineData("It was down for seventy-five minutes.", "number word")]
    [InlineData("Twice the usual load hit the server.", "number word")]
    [InlineData("See status.example.test for updates.", "link or contact")]
    [InlineData("Email ops@example.test.", "link or contact")]
    [InlineData("No data was lost.", "unsupported claim")]
    [InlineData("This will not happen again.", "unsupported claim")]
    [InlineData("Customers will receive a refund.", "unsupported claim")]
    [InlineData("{{incident.users}} users were affected.", "unknown placeholder")]
    [InlineData("It lasted {{incident.duration}.", "malformed placeholder")]
    [InlineData("TriggerCheck detected the outage.", "vendor name")]
    public void Contract_rejects_sentences(string sentence, string problem)
    {
        var contract = new PostmortemOutputContract(new Dictionary<string, NarrativeValue> { ["incident.duration"] = new(NarrativeValueKind.Duration, 75) }, "en", "UTC");
        Assert.Contains(contract.Problems(sentence, customerMessage: true), p => p.StartsWith(problem, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{{service}} was unavailable for {{incident.duration}}.")]
    [InlineData("The issue is resolved and we are monitoring the service closely.")]
    [InlineData("Hizmet bir süre erişilemedi.")] // Turkish article "bir" is allowed
    public void Contract_accepts_grounded_sentences(string sentence)
    {
        var contract = new PostmortemOutputContract(new Dictionary<string, NarrativeValue> { ["incident.duration"] = new(NarrativeValueKind.Duration, 75) }, "tr", "UTC");
        Assert.Empty(contract.Problems(sentence, customerMessage: true));
    }
}
