using System.Text.Json;
using System.Text.Json.Nodes;
using TriggerCheck.Domain;
using TriggerCheck.Evals;

namespace TriggerCheck.Tests.Ai.Insights;

/// <summary>AI-12 eval cases in CI (fake client): coverage, and the code grader rejecting invented numbers / customers.</summary>
public class InsightsEvalTests
{
    private static string CasesDir => EvalScenario.FindCasesDir(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("evals/cases not found above " + AppContext.BaseDirectory);

    private static List<EvalScenario> Cases() => EvalScenario.LoadAll(CasesDir).Where(c => c.Feature == AiFeatures.PlatformInsights).ToList();

    [Fact]
    public void Case_set_covers_every_signal_in_both_languages()
    {
        var cases = Cases();
        Assert.True(cases.Count >= 6, $"AI-12 needs at least 6 cases, found {cases.Count}");
        Assert.True(cases.Count(c => c.Language == "tr") >= 2);
        Assert.Contains(cases, c => c.Insights!.Customers.Any(x => x.Monitors.Any(m => m.Flaps > 0)));                       // noise
        Assert.Contains(cases, c => c.Insights!.Customers.Any(x => x.Monitors.Any(m => m.Status == Domain.Entities.MonitorStatus.Down))); // abandoned
        Assert.Contains(cases, c => c.Insights!.Customers.Any(x => x.AiCalls > 0 && x.SmsPeak > 0));                       // AI quota + SMS
        Assert.Contains(cases, c => c.Secrets.Count > 0);
        Assert.All(cases, c => Assert.NotEmpty(c.Expect.FocusCustomers));
    }

    [Fact]
    public async Task Fake_run_passes_the_gate()
    {
        var report = await EvalHarness.RunAsync(Cases(), new EvalOptions());
        var f = Assert.Single(report.Features);
        Assert.Equal(AiFeatures.PlatformInsights, f.Feature);
        Assert.All(report.Cases, c => Assert.True(c.Passed, $"{c.Id}: {string.Join("; ", c.Notes)}"));
        Assert.True(report.GatePassed);
    }

    [Fact]
    public async Task Invented_numbers_or_customers_fail_the_gate()
    {
        var cases = Cases();
        var invented = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            headline = "Platform overview.",
            points = new[] { "[[C9]] is about to churn with 42 open incidents.", "[[C1]] lost {{C1.revenue}}." },
        }))!;
        foreach (var c in cases) c.Canned = invented.DeepClone();

        var report = await EvalHarness.RunAsync(cases, new EvalOptions());

        var f = Assert.Single(report.Features);
        Assert.Equal(0, f.EvidenceRate);
        Assert.Equal(0, f.PrimaryCauseAccuracy);
        Assert.False(report.GatePassed);
        Assert.All(report.Cases, c => Assert.False(c.NumbersOk));
        Assert.Contains(report.Cases, c => c.Notes.Any(n => n.StartsWith("invented record [[C9]]", StringComparison.Ordinal)));
        Assert.Contains(report.Cases, c => c.Notes.Any(n => n.StartsWith("invented value {{C1.revenue}}", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_summary_that_ignores_the_focus_customer_fails()
    {
        var cases = Cases().Where(c => c.Id == "insights-mixed-tr").ToList();
        cases[0].Canned = JsonNode.Parse("""{"headline":"[[C1]] son {{rule.window_days}} içinde {{C1.noise.incidents}} olay üretti.","points":[]}""");

        var report = await EvalHarness.RunAsync(cases, new EvalOptions());

        var c = Assert.Single(report.Cases);
        Assert.True(c.EvidenceOk);
        Assert.False(c.PrimaryCauseOk);
        Assert.Contains(c.Notes, n => n.StartsWith("not referenced:", StringComparison.Ordinal));
    }
}
