using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Ai.Features.Correlation;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Alerting;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>Autonomy as if the catalog allowed <paramref name="level"/> (tests only: production reads AiFeatures.All).</summary>
public sealed class FixedCorrelationAutonomy(IOptions<AiCorrelationOptions> options, AiAutonomy level) : CorrelationAutonomy(options)
{
    public override AiAutonomy CatalogMax => level;

    public static void Register(IServiceCollection s, AiAutonomy level)
    {
        s.RemoveAll<CorrelationAutonomy>();
        s.AddSingleton<CorrelationAutonomy>(sp => new FixedCorrelationAutonomy(sp.GetRequiredService<IOptions<AiCorrelationOptions>>(), level));
    }
}

/// <summary>AI-4 at A1: proposals from the L1 rules, the LLM for candidates and explanation, accept / reject, gating, isolation.</summary>
public class CorrelationTests
{
    private const string Timeout = "Timeout after 10s";

    private static Task<int> AddIncidentAsync(AiTestHost host, int monitorId, string cause, double minutesAgo, double? resolvedMinutesAgo = null) =>
        host.WithDbAsync(async db =>
        {
            var i = new Incident { MonitorId = monitorId, StartedAtUtc = host.Now.AddMinutes(-minutesAgo), Cause = cause,
                ResolvedAtUtc = resolvedMinutesAgo is double r ? host.Now.AddMinutes(-r) : null };
            db.Incidents.Add(i);
            await db.SaveChangesAsync();
            return i.Id;
        });

    /// <summary>A monitor on <paramref name="target"/> with an incident <paramref name="minutesAgo"/> ago.</summary>
    private static async Task<int> DownAsync(AiTestHost host, int customerId, string target, string cause, double minutesAgo)
    {
        var m = await host.AddMonitorAsync(customerId, x => { x.Target = target; x.Name = "Secret name " + target; x.Status = MonitorStatus.Down; });
        return await AddIncidentAsync(host, m.Id, cause, minutesAgo);
    }

    private static Task<CorrelationScanner.Result> ScanAsync(AiTestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<CorrelationScanner>().ScanAsync());

    private static Task<T> GroupsAsync<T>(AiTestHost host, Func<IncidentCorrelationService, Task<T>> f) =>
        host.InScopeAsync(sp => f(sp.GetRequiredService<IncidentCorrelationService>()));

    private static Task<List<IncidentGroup>> AllGroupsAsync(AiTestHost host) =>
        host.WithDbAsync(db => db.IncidentGroups.AsNoTracking().Include(g => g.Members).OrderBy(g => g.Id).ToListAsync());

    private static AiJob GroupJob(int customerId, int groupId) =>
        new(AiFeatures.CorrelationGrouping, customerId, IncidentCorrelationService.SubjectType, groupId.ToString(CultureInfo.InvariantCulture));

    [Fact]
    public async Task Same_host_outage_is_proposed_as_one_group_and_explained_with_masked_data()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var a = await DownAsync(host, c.Id, "https://api.example.test/orders?token=abc", Timeout, 3);
        var b = await DownAsync(host, c.Id, "https://api.example.test/users", Timeout, 2.5);
        var d = await DownAsync(host, c.Id, "https://api.example.test/pay", "Connection refused", 2);
        var other = await DownAsync(host, c.Id, "https://unrelated.test/", "Unexpected status code 404 (expected 200-299)", 2);
        var before = await host.WithDbAsync(db => db.Incidents.AsNoTracking().OrderBy(i => i.Id).ToListAsync());

        var result = await ScanAsync(host);

        Assert.Equal(1, result.Proposed);
        Assert.Equal(1, result.JobsQueued);
        var g = Assert.Single(await AllGroupsAsync(host));
        Assert.Equal(IncidentGroupStatus.Proposed, g.Status);
        Assert.Equal(AiAutonomy.A1, g.Mode);
        Assert.Equal(a, g.PrimaryIncidentId);
        Assert.Equal([a, b, d], g.Members.OrderBy(m => m.IncidentId).Select(m => m.IncidentId));
        Assert.DoesNotContain(g.Members, m => m.IncidentId == other);
        Assert.Equal("api.example.test", g.Host);
        // Proposals never touch incidents.
        var after = await host.WithDbAsync(db => db.Incidents.AsNoTracking().OrderBy(i => i.Id).ToListAsync());
        Assert.Equal(before.Select(i => (i.Id, i.ResolvedAtUtc, i.Cause, i.AcknowledgedAtUtc)), after.Select(i => (i.Id, i.ResolvedAtUtc, i.Cause, i.AcknowledgedAtUtc)));

        // A second scan proposes nothing new.
        Assert.Equal(0, (await ScanAsync(host)).Proposed);

        // The explanation goes through the interpreter: recorded, masked, with the AI-4 schema.
        var decision = await host.RunAsync(GroupJob(c.Id, g.Id));
        Assert.Equal(AiDecisionStatus.Succeeded, decision!.Status);
        var request = Assert.Single(host.Client.Requests);
        Assert.Equal(AiFeatures.CorrelationGrouping, request.Feature);
        Assert.Contains("incident A", request.UserContent);
        Assert.Contains("host=api.example.test", request.UserContent);
        Assert.DoesNotContain("Secret name", request.UserContent);
        Assert.DoesNotContain("/orders", request.UserContent);
        Assert.DoesNotContain("token=abc", request.UserContent);
        Assert.Contains(CorrelationPrompts.Instructions.Trim()[..40], request.SystemPrompt);
        Assert.Contains("shared_cause", request.OutputSchema["properties"].ToString());
        Assert.Equal(1, await host.WithDbAsync(db => db.AiDecisions.CountAsync(x => x.Feature == AiFeatures.CorrelationGrouping && x.SubjectId == g.Id.ToString())));
    }

    [Fact]
    public async Task Ambiguous_candidates_are_judged_by_the_model_and_the_admin_decides()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var a = await DownAsync(host, c.Id, "https://api.example.test/", Timeout, 4);
        var b = await DownAsync(host, c.Id, "https://api.example.test/b", Timeout, 3.5);
        var www = await DownAsync(host, c.Id, "https://www.example.test/", Timeout, 3);       // same domain + error: candidate
        var far = await DownAsync(host, c.Id, "https://elsewhere.test/", Timeout, 2.5);       // same error only: candidate
        await ScanAsync(host);
        var g = Assert.Single(await AllGroupsAsync(host));
        Assert.Equal(IncidentGroupRole.Ambiguous, g.Members.Single(m => m.IncidentId == www).Role);
        Assert.False(g.Members.Single(m => m.IncidentId == www).Included); // never added by the rules alone
        Assert.Equal(IncidentGroupRole.Ambiguous, g.Members.Single(m => m.IncidentId == far).Role);

        // The model keeps www (C) and leaves the unrelated host (D) out.
        host.Client.Responder = _ => FakeAiClient.Success("""
            { "summary": "Two monitors on api.example.test and www.example.test timed out together.",
              "hypotheses": [
                { "cause": "Same frontend outage", "confidence": "high", "evidence": ["both time out"], "category": "shared_cause", "evidence_refs": ["incident C", "incident A"] },
                { "cause": "Different provider", "confidence": "medium", "evidence": ["other domain"], "category": "independent", "evidence_refs": ["incident D"] },
                { "cause": "made up", "confidence": "high", "evidence": ["x"], "category": "shared_cause", "evidence_refs": ["rule_signals"] }
              ], "suggested_actions": [], "unknowns": [] }
            """);
        var decision = await host.RunAsync(GroupJob(c.Id, g.Id));
        var output = AiInterpretation.TryParse(decision!.OutputJson);
        Assert.Equal(2, output!.Hypotheses.Count); // the ungrounded one was dropped
        var full = await GroupsAsync(host, s => s.GetAsync(c.Id, g.Id));
        Assert.Equal([www], CorrelationPrompts.Recommended(output, CorrelationGroupingProvider.CandidateKeys(full!)));

        // A1: the admin accepts with the recommended selection.
        Assert.Equal(IncidentCorrelationService.DecisionResult.Done, await GroupsAsync(host, s => s.AcceptAsync(c.Id, g.Id, [b, www], "admin-1")));
        g = Assert.Single(await AllGroupsAsync(host));
        Assert.Equal(IncidentGroupStatus.Accepted, g.Status);
        Assert.Equal("admin-1", g.DecidedByUserId);
        Assert.True(g.Members.Single(m => m.IncidentId == www).Included);
        Assert.False(g.Members.Single(m => m.IncidentId == far).Included);
        Assert.True(g.Members.Single(m => m.IncidentId == a).Included);
        Assert.Equal(IncidentCorrelationService.DecisionResult.AlreadyDecided, await GroupsAsync(host, s => s.RejectAsync(c.Id, g.Id, "admin-2")));
    }

    [Fact]
    public async Task Acceptance_rate_counts_decided_proposals_of_the_window()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var groups = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            await DownAsync(host, c.Id, $"https://h{i}.example{i}.test/a", Timeout, 30 - i * 6);
            await DownAsync(host, c.Id, $"https://h{i}.example{i}.test/b", Timeout, 29.5 - i * 6);
        }
        await ScanAsync(host);
        var ids = (await AllGroupsAsync(host)).Select(g => g.Id).ToList();
        Assert.Equal(4, ids.Count);
        var first = await GroupsAsync(host, s => s.GetAsync(c.Id, ids[0]));
        await GroupsAsync(host, s => s.AcceptAsync(c.Id, ids[0], first!.Members.Select(m => m.IncidentId).ToList(), "u"));
        var second = await GroupsAsync(host, s => s.GetAsync(c.Id, ids[1]));
        await GroupsAsync(host, s => s.AcceptAsync(c.Id, ids[1], first.Members.Select(m => m.IncidentId).ToList(), "u")); // keeps none of its own: a rejection
        await GroupsAsync(host, s => s.RejectAsync(c.Id, ids[2], "u"));
        // ids[3] stays undecided.

        var stats = await GroupsAsync(host, s => s.AcceptanceAsync(c.Id));
        Assert.Equal(1, stats.Accepted);
        Assert.Equal(2, stats.Rejected);
        Assert.Equal(1.0 / 3, stats.Rate!.Value, 3);
        Assert.Equal(1, stats.RuleMembersKept);
        Assert.Equal(1, stats.RuleMembersTotal);
        Assert.Equal(IncidentGroupStatus.Rejected, (await AllGroupsAsync(host)).Single(g => g.Id == second!.Id).Status);

        // Undecided proposals expire after a day and never count.
        host.Time.Advance(TimeSpan.FromHours(25));
        await ScanAsync(host);
        Assert.Equal(IncidentGroupStatus.Expired, (await AllGroupsAsync(host)).Single(g => g.Id == ids[3]).Status);
        Assert.Equal(3, (await GroupsAsync(host, s => s.AcceptanceAsync(c.Id))).Decided);
        Assert.Equal(3, (await GroupsAsync(host, s => s.AcceptanceAsync(null))).Decided); // platform view
    }

    [Fact]
    public async Task A_late_incident_joins_the_open_proposal()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var a = await DownAsync(host, c.Id, "https://api.example.test/a", Timeout, 3);
        await DownAsync(host, c.Id, "https://api.example.test/b", Timeout, 2.5);
        await ScanAsync(host);
        var late = await DownAsync(host, c.Id, "https://api.example.test/c", "Connection refused", 0);
        var result = await ScanAsync(host);
        Assert.Equal(1, result.Proposed); // the extended proposal (explained again)
        var g = Assert.Single(await AllGroupsAsync(host));
        Assert.Equal(a, g.PrimaryIncidentId);
        Assert.Contains(g.Members, m => m.IncidentId == late && m.Role == IncidentGroupRole.Rule && m.Included);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("opt-out")]
    [InlineData("switched-off")]
    [InlineData("ai-off")]
    [InlineData("autonomy-a0")]
    public async Task Nothing_is_proposed_when_the_feature_is_not_available(string reason)
    {
        var settings = new Dictionary<string, string?>();
        if (reason == "ai-off") settings["Ai:Enabled"] = "false";
        if (reason == "autonomy-a0") settings["Ai:Correlation:MaxAutonomy"] = "A0";
        await using var host = await AiTestHost.CreateAsync(settings);
        var c = await host.AddCustomerAsync(aiEnabled: reason != "opt-out",
            features: reason == "plan" ? AiFeatures.Format([AiFeatures.IncidentInterpretation]) : null);
        if (reason == "switched-off")
            await host.WithDbAsync(db => db.Customers.Where(x => x.Id == c.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.AlertGroupingEnabled, false)));
        await DownAsync(host, c.Id, "https://api.example.test/a", Timeout, 3);
        await DownAsync(host, c.Id, "https://api.example.test/b", Timeout, 2);

        var result = await ScanAsync(host);

        Assert.Equal(0, result.Proposed);
        Assert.Empty(await AllGroupsAsync(host));
        Assert.Empty(host.Client.Requests);
        var customer = await GroupsAsync(host, s => s.CustomerAsync(c.Id));
        Assert.Equal(CorrelationMode.Off, await GroupsAsync(host, s => s.ModeAsync(customer!)));
    }

    [Fact]
    public async Task Used_up_budget_keeps_rule_proposals_but_skips_the_model_and_automatic_grouping()
    {
        await using var host = await AiTestHost.CreateAsync(configure: s => FixedCorrelationAutonomy.Register(s, AiAutonomy.A3));
        var c = await host.AddCustomerAsync(callLimit: 1);
        await host.WithDbAsync(async db =>
        {
            db.AiDecisions.Add(new AiDecision { CustomerId = c.Id, Feature = AiFeatures.IncidentInterpretation, SubjectType = "Incident", SubjectId = "1",
                Status = AiDecisionStatus.Succeeded, InputTokens = 10, OutputTokens = 10, CreatedAtUtc = host.Now });
            await db.SaveChangesAsync();
        });
        await DownAsync(host, c.Id, "https://api.example.test/a", Timeout, 3);
        await DownAsync(host, c.Id, "https://api.example.test/b", Timeout, 2);
        var customer = await GroupsAsync(host, s => s.CustomerAsync(c.Id));
        Assert.Equal(CorrelationMode.Suggest, await GroupsAsync(host, s => s.ModeAsync(customer!))); // not Automatic

        await ScanAsync(host);
        var g = Assert.Single(await AllGroupsAsync(host));
        var decision = await host.RunAsync(GroupJob(c.Id, g.Id));
        Assert.Equal(AiDecisionStatus.SkippedBudget, decision!.Status);
        Assert.Empty(host.Client.Requests);
    }

    [Fact]
    public async Task Tenants_are_never_grouped_together_or_visible_to_each_other()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c1 = await host.AddCustomerAsync();
        var c2 = await host.AddCustomerAsync();
        // The same host fails for both tenants at the same time.
        var a1 = await DownAsync(host, c1.Id, "https://shared.example.test/a", Timeout, 3);
        var a2 = await DownAsync(host, c2.Id, "https://shared.example.test/a", Timeout, 2.9);
        var b1 = await DownAsync(host, c1.Id, "https://shared.example.test/b", Timeout, 2.8);
        var b2 = await DownAsync(host, c2.Id, "https://shared.example.test/b", Timeout, 2.7);

        await ScanAsync(host);
        var groups = await AllGroupsAsync(host);
        Assert.Equal(2, groups.Count);
        var g1 = groups.Single(g => g.CustomerId == c1.Id);
        var g2 = groups.Single(g => g.CustomerId == c2.Id);
        Assert.Equal([a1, b1], g1.Members.Select(m => m.IncidentId).Order());
        Assert.Equal([a2, b2], g2.Members.Select(m => m.IncidentId).Order());
        Assert.All(g1.Members, m => Assert.Equal(c1.Id, m.CustomerId));

        Assert.Null(await GroupsAsync(host, s => s.GetAsync(c2.Id, g1.Id)));
        Assert.Equal(IncidentCorrelationService.DecisionResult.NotFound, await GroupsAsync(host, s => s.AcceptAsync(c2.Id, g1.Id, [b1], "x")));
        Assert.Equal(IncidentCorrelationService.DecisionResult.NotFound, await GroupsAsync(host, s => s.RejectAsync(c2.Id, g1.Id, "x")));
        Assert.Equal(0, await GroupsAsync(host, s => s.DissolveAsync(c2.Id, g1.Id, "x")));
        Assert.Equal(IncidentGroupStatus.Proposed, (await AllGroupsAsync(host)).Single(g => g.Id == g1.Id).Status);

        // A job naming another tenant's group builds nothing (no call, nothing recorded).
        Assert.Null(await host.RunAsync(GroupJob(c2.Id, g1.Id)));
        Assert.Empty(host.Client.Requests);
    }

    [Fact]
    public async Task Switching_grouping_off_expires_open_proposals()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        await DownAsync(host, c.Id, "https://api.example.test/a", Timeout, 3);
        await DownAsync(host, c.Id, "https://api.example.test/b", Timeout, 2);
        await ScanAsync(host);

        await GroupsAsync(host, async s => { await s.SetEnabledAsync(c.Id, false, "u"); return 0; });

        Assert.Equal(IncidentGroupStatus.Expired, Assert.Single(await AllGroupsAsync(host)).Status);
        Assert.False((await GroupsAsync(host, s => s.CustomerAsync(c.Id)))!.AlertGroupingEnabled);
        await DownAsync(host, c.Id, "https://api.example.test/c", Timeout, 1);
        await DownAsync(host, c.Id, "https://api.example.test/d", Timeout, 0.5);
        Assert.Equal(0, (await ScanAsync(host)).Proposed);
    }

    [Fact]
    public async Task Automatic_mode_makes_no_proposals_and_the_alert_hook_is_inert_at_A1()
    {
        // Default catalog (A1): the A3 hook never folds and never touches the database for DOWN alerts.
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id);
        await AddIncidentAsync(host, m.Id, Timeout, 1);
        var hook = await host.InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<IAlertGrouping>()));
        Assert.IsType<CorrelationAlertGrouping>(hook);
        var msg = new AlertMessage(m.Id, c.Id, "M", m.Target, AlertKind.Down, Timeout, host.Now);
        Assert.Null(await host.InScopeAsync(sp => sp.GetRequiredService<IAlertGrouping>().TryFoldAsync(msg, default)));

        await using var auto = await AiTestHost.CreateAsync(configure: s => FixedCorrelationAutonomy.Register(s, AiAutonomy.A3));
        var c2 = await auto.AddCustomerAsync();
        await DownAsync(auto, c2.Id, "https://api.example.test/a", Timeout, 3);
        await DownAsync(auto, c2.Id, "https://api.example.test/b", Timeout, 2);
        var customer = await GroupsAsync(auto, s => s.CustomerAsync(c2.Id));
        Assert.Equal(CorrelationMode.Automatic, await GroupsAsync(auto, s => s.ModeAsync(customer!)));
        Assert.Equal(0, (await ScanAsync(auto)).Proposed); // grouping happens on the alert path in A3
    }

    [Fact]
    public void Catalog_keeps_AI4_at_A1_until_the_acceptance_rate_is_measured()
    {
        Assert.Equal(AiAutonomy.A1, AiFeatures.Find(AiFeatures.CorrelationGrouping)!.MaxAutonomy);
        var autonomy = new CorrelationAutonomy(Options.Create(new AiCorrelationOptions()));
        Assert.True(autonomy.Enabled);
        Assert.False(autonomy.Automatic);
        // The operator cap can only lower it.
        Assert.False(new CorrelationAutonomy(Options.Create(new AiCorrelationOptions { MaxAutonomy = AiAutonomy.A0 })).Enabled);
    }
}
