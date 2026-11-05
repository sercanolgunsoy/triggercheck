using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Ai.Budget;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Insights;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Tests.Ai.Insights;

/// <summary>AI-12 summary: the no-numbers contract, rendering, and the on-demand service (fake client, platform budget, audit).</summary>
public class InsightSummaryTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>One noisy customer (C1, noisiest monitor M1) and one abandoned customer (C2, longest-down monitor M2).</summary>
    private static InsightReport SampleReport()
    {
        var input = new InsightInput(
            [new(1, "Acme Corp", "Basic", 100, 60, null, 1, false), new(2, "Globex", "Standard", 100, 60, null, 1, false)],
            [new(10, 1, "Checkout", MonitorType.Http, true, MonitorStatus.Up, Now.AddHours(-1), 300),
             new(20, 2, "Legacy site", MonitorType.Http, true, MonitorStatus.Down, Now.AddDays(-30), 300)],
            Enumerable.Range(0, 12).Select(i => new InsightIncident(10, Now.AddHours(-1 - i), Now.AddHours(-1 - i).AddMinutes(3), null)).ToList(),
            new Dictionary<int, int> { [10] = 24 }, new Dictionary<int, int>(), new Dictionary<int, int>(), 100, new Dictionary<int, InsightAiUsage>());
        return InsightRules.Compute(input, new AiInsightOptions(), Now);
    }

    private static InsightSummaryInput SampleInput(string language = "en") => InsightSummaryPrompt.Build(SampleReport(), 10, language);

    private static string Json(string headline, params string[] points) =>
        System.Text.Json.JsonSerializer.Serialize(new { headline, points });

    [Fact]
    public void Model_input_uses_labels_and_never_names()
    {
        var input = SampleInput();
        Assert.Equal(new InsightLabel(InsightLabel.Customer, 1), input.Labels["C1"]);
        Assert.Equal(new InsightLabel(InsightLabel.Customer, 2), input.Labels["C2"]);
        Assert.Equal(new InsightLabel(InsightLabel.Monitor, 10), input.Labels["M1"]);
        Assert.Equal(new InsightLabel(InsightLabel.Monitor, 20), input.Labels["M2"]);
        Assert.Contains("C1.noise.incidents: 12", input.UserContent);
        Assert.Contains("C2.abandoned.longest_down_days: 30 days", input.UserContent);
        Assert.Contains("C2.abandoned.all_down", input.UserContent);
        Assert.Contains("response_language: English", input.UserContent);
        foreach (var name in new[] { "Acme", "Globex", "Checkout", "Legacy site" }) Assert.DoesNotContain(name, input.UserContent);
        // The signal hash does not depend on the language; the content does.
        var tr = SampleInput("tr");
        Assert.Equal(input.SignalsHash, tr.SignalsHash);
        Assert.Contains("C2.abandoned.longest_down_days: 30 gün", tr.UserContent);
    }

    [Fact]
    public void A_grounded_summary_passes_and_renders_server_values_and_references()
    {
        var input = SampleInput();
        var check = new InsightSummaryContract(input).Check(Json(
            "[[C2]] looks abandoned and [[C1]] is the noisiest customer.",
            "[[C1]] had {{C1.noise.incidents}} incidents and {{C1.noise.alerts}} alerts; [[M1]] alone had {{M1.incidents}}.",
            "For [[C2]], {{C2.abandoned.all_down}} for {{C2.abandoned.longest_down_days}}."));
        Assert.NotNull(check.Json);
        var output = InsightSummaryOutput.TryParse(check.Json)!;
        Assert.Equal(2, output.Points.Count);
        Assert.Equal(0, output.RemovedPoints);
        Assert.Equal(input.SignalsHash, output.SignalsHash);

        var segments = output.Render(output.Points[0]);
        Assert.Equal(new InsightLabel(InsightLabel.Customer, 1), segments[0].Ref);
        var text = string.Concat(segments.Select(s => s.Text ?? "<" + s.Ref!.Kind + s.Ref.Id + ">"));
        Assert.Equal("<customer1> had 12 incidents and 24 alerts; <monitor10> alone had 12.", text);
        Assert.Equal("For <customer2>, every enabled monitor is DOWN for 30 days.",
            string.Concat(output.Render(output.Points[1]).Select(s => s.Text ?? "<" + s.Ref!.Kind + s.Ref.Id + ">")));
    }

    [Theory]
    [InlineData("[[C1]] had 12 incidents.", "digits")]
    [InlineData("[[C1]] had twelve incidents.", "number word")]
    [InlineData("[[C1]] had {{C1.noise.revenue}} incidents.", "unknown placeholder")]
    [InlineData("[[C9]] had {{C1.noise.incidents}} incidents.", "unknown label")]
    [InlineData("[[C2]] had {{C1.noise.incidents}} incidents.", "not bound")]
    [InlineData("{{C1.noise.incidents}} incidents for [[C1]].", "not bound")]
    [InlineData("[[C1]] and [[C2]] had {{C1.noise.incidents}} incidents.", "not bound")]
    [InlineData("All customers had {{total.noisy_customers}} signals.", "quantity word")]
    [InlineData("Contact [[C1]] at ops@example.test about {{C1.noise.incidents}} incidents.", "link or contact")]
    [InlineData("See acme.example.test for [[C1]].", "link or contact")]
    [InlineData("[[C1]] had {{C1.noise.incidents incidents.", "malformed")]
    [InlineData("Customer C1 is noisy.", "digits")]
    public void Ungrounded_texts_are_rejected(string text, string problem)
    {
        var contract = new InsightSummaryContract(SampleInput());
        Assert.Contains(contract.Problems(text), p => p.Contains(problem, StringComparison.Ordinal));
    }

    [Fact]
    public void Turkish_number_words_are_rejected_in_Turkish_summaries()
    {
        var contract = new InsightSummaryContract(SampleInput("tr"));
        Assert.Contains(contract.Problems("[[C1]] iki olay yaşadı."), p => p.StartsWith("number word", StringComparison.Ordinal));
        Assert.Empty(contract.Problems("[[C1]] için {{C1.noise.incidents}} olay var; bir müşteri öne çıkıyor."));
    }

    [Fact]
    public void A_bad_headline_or_mostly_bad_points_reject_the_summary()
    {
        var contract = new InsightSummaryContract(SampleInput());
        Assert.Equal("ungrounded_numbers", contract.Check(Json("Twelve incidents at [[C1]].", "[[C1]] is noisy.")).ErrorCode);
        Assert.Equal("ungrounded_numbers", contract.Check(Json("Platform overview.", "[[C1]] had 12 incidents.", "[[C2]] has 30 days down.", "[[C1]] is noisy.")).ErrorCode);
        var partly = contract.Check(Json("Platform overview.", "[[C1]] had 12 incidents.", "[[C1]] is noisy.", "[[C2]] may be abandoned."));
        Assert.Equal(1, InsightSummaryOutput.TryParse(partly.Json)!.RemovedPoints);
        Assert.Equal("invalid_output", contract.Check("not json").ErrorCode);
        Assert.Equal("invalid_output", contract.Check(Json("", "x")).ErrorCode);
    }

    [Fact]
    public void Unknown_labels_render_as_plain_text()
    {
        var segments = InsightRenderer.Segments("[[C1]] and [[C7]]", new Dictionary<string, InsightLabel> { ["C1"] = new(InsightLabel.Customer, 5) });
        Assert.Equal(5, segments[0].Ref!.Id);
        Assert.Equal(" and [[C7]]", segments[1].Text);
    }

    // ---- service (DB, fake client) ----

    private const string GoodAnswer = """
        {"headline":"[[C1]] is the noisiest customer.","points":["[[C1]] had {{C1.noise.incidents}} incidents in {{rule.window_days}}."]}
        """;

    private static Task<AiTestHost> HostAsync(Dictionary<string, string?>? settings = null) => AiTestHost.CreateAsync(settings);

    /// <summary>A noisy customer: twelve short incidents on one monitor in the last day.</summary>
    private static async Task<Customer> SeedNoisyAsync(AiTestHost h, string name = "Acme Corp")
    {
        var c = await h.AddCustomerAsync();
        await h.WithDbAsync(async db =>
        {
            await db.Customers.Where(x => x.Id == c.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, name));
            var m = new TriggerCheck.Domain.Entities.Monitor { CustomerId = c.Id, Name = "Checkout API", Target = "https://shop.example.test/pay", IntervalSeconds = 60, Status = MonitorStatus.Up };
            db.Monitors.Add(m);
            await db.SaveChangesAsync();
            for (var i = 0; i < 12; i++)
                db.Incidents.Add(new Incident { MonitorId = m.Id, StartedAtUtc = h.Now.AddHours(-1 - i), ResolvedAtUtc = h.Now.AddHours(-1 - i).AddMinutes(2) });
            await db.SaveChangesAsync();
        });
        return c;
    }

    private static Task<InsightSummaryResult> GenerateAsync(AiTestHost h, string language = "en") =>
        h.InScopeAsync(sp => sp.GetRequiredService<InsightSummaryService>().GenerateAsync(language));

    private static Task<List<AiDecision>> PlatformDecisionsAsync(AiTestHost h) =>
        h.WithDbAsync(db => db.AiDecisions.AsNoTracking().Where(d => d.Feature == AiFeatures.PlatformInsights).OrderBy(d => d.Id).ToListAsync());

    [Fact]
    public async Task Generates_a_platform_decision_without_customer_and_without_touching_customer_budgets()
    {
        await using var h = await HostAsync();
        var c = await SeedNoisyAsync(h);
        h.Client.Responder = _ => FakeAiClient.Success(GoodAnswer);

        var result = await GenerateAsync(h);

        Assert.Equal(InsightSummaryStatus.Generated, result.Status);
        var d = Assert.Single(await PlatformDecisionsAsync(h));
        Assert.Null(d.CustomerId);
        Assert.Equal(AiDecisionStatus.Succeeded, d.Status);
        Assert.Equal((InsightSummaryService.SubjectType, InsightSummaryService.SubjectId), (d.SubjectType, d.SubjectId));
        Assert.Equal(AiAutonomy.A1, d.AutonomyLevel);
        Assert.True(d.InputTokens > 0 && d.CostEstimate > 0);

        // What reached the model: labels and values, no customer / monitor names, no URL.
        var request = Assert.Single(h.Client.Requests);
        Assert.Equal(AiFeatures.PlatformInsights, request.Feature);
        foreach (var leak in new[] { "Acme", "Checkout", "shop.example.test", "ops@example.test" }) Assert.DoesNotContain(leak, request.SystemPrompt + request.UserContent);

        // Billed to the platform budget only.
        var usage = await h.InScopeAsync(sp => sp.GetRequiredService<IAiUsage>().GetMonthlyUsageAsync(c.Id));
        Assert.Equal(0, usage.Calls);
        var platform = await h.InScopeAsync(sp => sp.GetRequiredService<InsightSummaryService>().GetUsageAsync());
        Assert.Equal(1, platform.Calls);

        var state = await h.InScopeAsync(sp => sp.GetRequiredService<InsightSummaryService>().GetStateAsync());
        Assert.Equal(d.Id, state.Latest!.Id);
        Assert.Contains(" had 12 incidents in 7 days.", string.Concat(state.Summary!.Render(state.Summary.Points[0]).Select(s => s.Text)));
    }

    [Fact]
    public async Task Unchanged_signals_reuse_the_summary_and_new_calls_are_paced()
    {
        await using var h = await HostAsync();
        await SeedNoisyAsync(h);
        h.Client.Responder = _ => FakeAiClient.Success(GoodAnswer);
        Assert.Equal(InsightSummaryStatus.Generated, (await GenerateAsync(h)).Status);
        Assert.Equal(InsightSummaryStatus.Reused, (await GenerateAsync(h)).Status);
        Assert.Single(h.Client.Requests);

        // New signal (another noisy customer) within the pacing window: no call.
        await SeedNoisyAsync(h, "Initech");
        Assert.Equal(InsightSummaryStatus.TooSoon, (await GenerateAsync(h)).Status);
        Assert.Single(h.Client.Requests);
        h.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(InsightSummaryStatus.Generated, (await GenerateAsync(h)).Status);
        Assert.Equal(2, h.Client.Requests.Count);
    }

    [Fact]
    public async Task Ai_off_stores_nothing_and_no_signals_makes_no_call()
    {
        await using (var off = await HostAsync(new() { ["Ai:Enabled"] = "false" }))
        {
            await SeedNoisyAsync(off);
            Assert.Equal(InsightSummaryStatus.Disabled, (await GenerateAsync(off)).Status);
            Assert.Empty(await PlatformDecisionsAsync(off));
            Assert.Empty(off.Client.Requests);
        }
        await using (var disabled = await HostAsync(new() { ["Ai:Insights:SummaryEnabled"] = "false" }))
        {
            await SeedNoisyAsync(disabled);
            Assert.Equal(InsightSummaryStatus.Disabled, (await GenerateAsync(disabled)).Status);
            Assert.Empty(disabled.Client.Requests);
        }
        await using var quiet = await HostAsync();
        await quiet.AddCustomerAsync();
        Assert.Equal(InsightSummaryStatus.NoSignals, (await GenerateAsync(quiet)).Status);
        Assert.Empty(quiet.Client.Requests);
        Assert.Empty(await PlatformDecisionsAsync(quiet));
    }

    [Fact]
    public async Task Platform_budget_used_up_records_a_skip_without_calling()
    {
        await using var h = await HostAsync(new() { ["Ai:Insights:MonthlyCallLimit"] = "1", ["Ai:Insights:MinMinutesBetweenSummaries"] = "0" });
        await SeedNoisyAsync(h);
        h.Client.Responder = _ => FakeAiClient.Success(GoodAnswer);
        Assert.Equal(InsightSummaryStatus.Generated, (await GenerateAsync(h)).Status);

        await SeedNoisyAsync(h, "Initech"); // new input: no reuse
        var result = await GenerateAsync(h);
        Assert.Equal(InsightSummaryStatus.Budget, result.Status);
        Assert.Equal(AiDecisionStatus.SkippedBudget, result.Decision!.Status);
        Assert.Single(h.Client.Requests);

        await using var none = await HostAsync(new() { ["Ai:Insights:MonthlyCallLimit"] = "0" });
        await SeedNoisyAsync(none);
        Assert.Equal(InsightSummaryStatus.Budget, (await GenerateAsync(none)).Status);
        Assert.Empty(none.Client.Requests);
    }

    [Fact]
    public async Task Failures_and_invented_numbers_are_recorded_and_keep_the_previous_summary()
    {
        await using var h = await HostAsync(new() { ["Ai:Insights:MinMinutesBetweenSummaries"] = "0" });
        await SeedNoisyAsync(h);
        h.Client.Responder = _ => FakeAiClient.Success(GoodAnswer);
        var first = (await GenerateAsync(h)).Decision!;

        await SeedNoisyAsync(h, "Initech");
        h.Client.Responder = _ => FakeAiClient.Success("""{"headline":"[[C1]] had 12 incidents.","points":[]}""");
        var invented = await GenerateAsync(h);
        Assert.Equal(InsightSummaryStatus.Failed, invented.Status);
        Assert.Equal(("ungrounded_numbers", AiDecisionStatus.Failed), (invented.Decision!.ErrorCode, invented.Decision.Status));
        Assert.Null(invented.Decision.OutputJson);

        h.Time.Advance(TimeSpan.FromMinutes(1));
        await SeedNoisyAsync(h, "Umbrella");
        h.Client.Responder = _ => AiCallResult.Fail("rate_limited");
        var failed = await GenerateAsync(h);
        Assert.Equal(InsightSummaryStatus.Failed, failed.Status);
        Assert.Equal("rate_limited", failed.Decision!.ErrorCode);

        var state = await h.InScopeAsync(sp => sp.GetRequiredService<InsightSummaryService>().GetStateAsync());
        Assert.Equal(first.Id, state.Latest!.Id);
        Assert.Equal(failed.Decision.Id, state.LastAttempt!.Id);
        Assert.Equal(3, (await PlatformDecisionsAsync(h)).Count);
    }

    [Fact]
    public async Task Customer_names_reach_only_the_page_and_suspended_customers_are_ignored()
    {
        await using var h = await HostAsync();
        var c = await SeedNoisyAsync(h, "Suspended Inc");
        await h.WithDbAsync(db => db.Customers.Where(x => x.Id == c.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false)));
        var report = await h.InScopeAsync(sp => sp.GetRequiredService<PlatformInsights>().ComputeAsync());
        Assert.True(report.IsEmpty);
        Assert.Equal(InsightSummaryStatus.NoSignals, (await GenerateAsync(h)).Status);
    }
}
