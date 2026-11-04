using System.Text.Json;
using System.Text.Json.Nodes;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Ai.Features;
using TriggerCheck.Evals;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>
/// AI-11 in CI: the eval harness runs every labelled case through the production context providers and interpreter
/// against FakeAiClient with the cases' canned answers (no key, no network). This keeps the harness, the fixtures and
/// the grader working; it does not measure model quality (that is the manual --real run, see evals/README.md).
/// </summary>
public class EvalHarnessTests
{
    private static string CasesDir => EvalScenario.FindCasesDir(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("evals/cases not found above " + AppContext.BaseDirectory);

    [Fact]
    public void Case_set_covers_the_backlog_scenarios()
    {
        var cases = EvalScenario.LoadAll(CasesDir);
        var incidents = cases.Where(c => c.Feature == AiFeatures.IncidentInterpretation).ToList();
        Assert.True(incidents.Count >= 20, $"AI-2 needs at least 20 labelled scenarios, found {incidents.Count}");
        foreach (var label in new[] { "dns_failure", "tls_expired", "tls_name_mismatch", "http_5xx", "timeout", "keyword_missing", "connection_refused", "host_outage", "maintenance_aftermath" })
            Assert.Contains(incidents, c => c.Expect.PrimaryCause == label);
        Assert.Contains(incidents, c => c.Language == "tr");
        Assert.Contains(cases, c => c.Feature == AiFeatures.SlowdownExplanation);
        Assert.All(cases, c => Assert.NotNull(c.Canned));
    }

    [Fact]
    public async Task Harness_with_fake_client_passes_the_gate_and_writes_a_report()
    {
        var report = await EvalHarness.RunAsync(EvalScenario.LoadAll(CasesDir), new EvalOptions());

        Assert.Equal("fake", report.Mode);
        Assert.All(report.Cases, c => Assert.True(c.Passed, $"{c.Id}: {string.Join("; ", c.Notes)}"));
        Assert.All(report.Cases, c => Assert.Equal(nameof(AiDecisionStatus.Succeeded), c.Status));
        Assert.True(report.GatePassed);
        Assert.Equal(2, report.Features.Count);
        Assert.All(report.Features, f => Assert.Equal(1.0, f.PrimaryCauseAccuracy));
        Assert.All(report.Cases, c => Assert.True(c.InputTokens > 0 && c.CostUsd > 0));

        var dir = Path.Combine(Path.GetTempPath(), "triggercheck-eval-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            report.Write(dir);
            var md = File.ReadAllText(Path.Combine(dir, "last-report.md"));
            Assert.Contains("Gate: **PASSED**", md);
            Assert.Contains("| dns-failure |", md);
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "last-report.json")));
            Assert.True(json.RootElement.GetProperty("gatePassed").GetBoolean());
            // The prompts actually sent are kept next to the report, masked.
            var prompt = File.ReadAllText(Path.Combine(dir, "prompts", "http-401-secrets.txt"));
            Assert.Contains("[secret]", prompt);
            Assert.DoesNotContain("hunter2", prompt);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Wrong_answers_fail_the_gate()
    {
        var cases = EvalScenario.LoadAll(CasesDir).Where(c => c.Feature == AiFeatures.IncidentInterpretation).ToList();
        // Every case answers "DNS": only the DNS cases stay correct.
        var wrong = JsonNode.Parse(Canned("Name resolution failed.", "The DNS lookup failed.", ["error E1"], category: "dns", refs: ["error E1"]))!;
        foreach (var c in cases) c.Canned = wrong.DeepClone();

        var report = await EvalHarness.RunAsync(cases, new EvalOptions());

        var f = Assert.Single(report.Features);
        Assert.True(f.PrimaryCauseAccuracy < 0.3);
        Assert.False(report.GatePassed);
        Assert.Contains(report.Cases, c => c.Notes.Any(n => n.Contains("expected one of", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Feature_and_case_filters_select_cases()
    {
        var report = await EvalHarness.RunAsync(EvalScenario.LoadAll(CasesDir), new EvalOptions { Feature = AiFeatures.SlowdownExplanation, Filter = "ttfb" });
        Assert.Equal(["slow-ttfb", "slow-ttfb-tr"], report.Cases.Select(c => c.Id).Order().ToList());
    }

    private static string Canned(string summary, string cause, string[] evidence, string category = "http_5xx", string[]? refs = null, string confidence = "high") =>
        JsonSerializer.Serialize(new
        {
            summary,
            hypotheses = new[] { new { cause, confidence, evidence, category, evidence_refs = refs ?? ["check #1"] } },
            suggested_actions = Array.Empty<object>(),
            unknowns = Array.Empty<string>(),
        });

    private static EvalScenario Scenario(string label = "http_5xx", string language = "en", params string[] secrets) => new()
    {
        Id = "unit", Language = language, Expect = new EvalExpectation { PrimaryCause = label }, Secrets = secrets.ToList(),
        Monitor = new EvalMonitor { Target = "https://shop.example.test/private/path?x=1" }, Incident = new EvalIncident(),
    };

    private static AiCallRequest Request(string user, string language = "English") =>
        new(AiFeatures.IncidentInterpretation, "claude-opus-5-5", "low", 1000, "system", $"response_language: {language}\n{user}",
            AiInterpretationSchema.Build([], AiFeaturePrompts.IncidentCategories));

    /// <summary>The decision production would store: the contract and the L3 policy applied to the raw output.</summary>
    private static AiDecision Decision(string json, string userContent)
    {
        var raw = AiInterpretation.TryParse(json)!;
        var contract = AiInterpretationValidator.ApplyContract(raw, AiFeaturePrompts.IncidentCategories, EvalGrader.EvidenceKeys(userContent));
        var valid = AiInterpretationValidator.Validate(contract, [], AiAutonomy.A0);
        return new AiDecision { Status = valid is null ? AiDecisionStatus.Failed : AiDecisionStatus.Succeeded, OutputJson = valid?.Interpretation.ToJson() };
    }

    private static EvalCaseResult Grade(EvalScenario s, string user, string json, string language = "English")
    {
        var request = Request(user, language);
        return EvalGrader.Grade(s, request, FakeAiClient.Success(json), Decision(json, request.UserContent));
    }

    [Theory]
    // Reviewer probes: the cause text no longer decides; only the category does. Negations, "resolved", hedges and
    // check ids in the text are irrelevant.
    [InlineData("This is not a DNS problem; the server returned HTTP 503.", "http_5xx", "http_5xx", true)]
    [InlineData("This is not a DNS problem; the server returned HTTP 503.", "http_5xx", "dns_failure", false)]
    [InlineData("DNS sorunu değil; sunucu 503 döndürüyor.", "http_5xx", "dns_failure", false)]
    [InlineData("After DNS resolution succeeded the server returned 500.", "http_5xx", "dns_failure", false)]
    [InlineData("Unclear cause; DNS looked fine.", "unknown", "dns_failure", false)]
    [InlineData("No timeout occurred; the TLS certificate expired.", "tls_expired", "timeout", false)]
    [InlineData("No timeout occurred; the TLS certificate expired.", "tls_expired", "tls_expired", true)]
    [InlineData("The certificate has not expired; the server returned 500 error", "http_5xx", "tls_expired", false)]
    [InlineData("DNS failure and a timeout.", "timeout", "dns_failure", false)]
    [InlineData("The request timed out after 1500 ms.", "timeout", "http_5xx", false)]
    [InlineData("Check #5004 failed.", "unknown", "http_5xx", false)]
    [InlineData("The certificate chain is fine; the server returned 404", "http_4xx", "tls_untrusted", false)]
    [InlineData("Sunucuya ulaşılamıyor.", "network_unreachable", "network_unreachable", true)]
    public void Primary_cause_is_graded_by_category_only(string cause, string category, string label, bool expected)
    {
        var result = Grade(Scenario(label), "check #1: 2026-03-10T11:59:00Z DOWN status=503",
            Canned("Summary of the incident.", cause, ["check #1: status 503"], category));
        Assert.Equal(expected, result.PrimaryCauseOk);
        Assert.True(result.EvidenceOk, string.Join("; ", result.Notes));
    }

    [Fact]
    public void Highest_confidence_wins_and_ties_go_to_the_first()
    {
        var output = AiInterpretation.TryParse("""
            {"summary":"s","suggested_actions":[],"unknowns":[],"hypotheses":[
              {"cause":"a","confidence":"medium","evidence":["e"],"category":"dns","evidence_refs":["check #1"]},
              {"cause":"b","confidence":"high","evidence":["e"],"category":"timeout","evidence_refs":["check #1"]},
              {"cause":"c","confidence":"high","evidence":["e"],"category":"http_5xx","evidence_refs":["check #1"]}]}
            """);
        Assert.Equal("timeout", EvalGrader.Primary(output)!.Category);
        Assert.True(EvalGrader.PrimaryMatches(output, "timeout"));
        Assert.False(EvalGrader.PrimaryMatches(output, "http_5xx"));
    }

    [Fact]
    public void Evidence_refs_must_resolve_to_items_that_were_sent()
    {
        const string user = "check #42: 2026-03-10T11:59:00Z DOWN status=503\nvalidation_error: none\n<untrusted_data source=\"error E1\">\nx\n</untrusted_data>\nevent: incident_opened";
        var keys = EvalGrader.EvidenceKeys("response_language: English\n" + user);
        Assert.Contains("check #42", keys);
        Assert.Contains("error E1", keys);
        Assert.DoesNotContain("event", keys); // describes the subject, not evidence
        Assert.Equal("check #42", AiInterpretationValidator.ResolveRef("#42", keys));
        Assert.Equal("check #42", AiInterpretationValidator.ResolveRef("42", keys));
        Assert.Null(AiInterpretationValidator.ResolveRef("check #7", keys));
        Assert.Null(AiInterpretationValidator.ResolveRef("event", keys));

        // A hypothesis citing a check that was never sent fails the evidence check and is dropped in production.
        var bogus = Grade(Scenario(), user, Canned("HTTP 503.", "HTTP 503", ["check #7: 503"], refs: ["check #7"]));
        Assert.False(bogus.EvidenceOk);
        Assert.False(bogus.PrimaryCauseOk); // nothing survives validation
        var good = Grade(Scenario(), user, Canned("HTTP 503.", "HTTP 503", ["503 on check #42"], refs: ["#42", "error E1"]));
        Assert.True(good.EvidenceOk && good.PrimaryCauseOk, string.Join("; ", good.Notes));

        // The interpreter's contract step: unknown category or no resolvable ref → dropped; refs normalised.
        var raw = AiInterpretation.TryParse(Canned("s", "c", ["e"], refs: ["42", "check #9"]))!;
        var kept = Assert.Single(AiInterpretationValidator.ApplyContract(raw, AiFeaturePrompts.IncidentCategories, keys).Hypotheses);
        Assert.Equal(["check #42"], kept.EvidenceRefs);
        Assert.Empty(AiInterpretationValidator.ApplyContract(AiInterpretation.TryParse(Canned("s", "c", ["e"], category: "aliens", refs: ["42"]))!,
            AiFeaturePrompts.IncidentCategories, keys).Hypotheses);
    }

    [Fact]
    public void Text_mentions_are_a_diagnostic_only()
    {
        Assert.Equal(["dns"], EvalGrader.Classify("DNS resolution of the host fails", slowdown: false));
        Assert.Equal(["tls_expired"], EvalGrader.Classify("Sertifikanın süresi dolmuş", slowdown: false));
        Assert.Equal(["slow_ttfb"], EvalGrader.Classify("TTFB rose from 150 ms to 1200 ms", slowdown: true));
        // A cause text that mentions DNS but is categorised as http_5xx still passes an http_5xx case.
        var r = Grade(Scenario(), "check #1: x", Canned("s", "Not DNS: HTTP 503", ["check #1"]));
        Assert.True(r.PrimaryCauseOk);
        Assert.Contains("dns", r.Categories);
    }

    [Fact]
    public void Grader_flags_leaks_and_wrong_language()
    {
        var json = Canned("The server returns HTTP 503.", "HTTP 503 from the backend", ["check #1: status 503"]);
        var good = Grade(Scenario(), "check #1: status=503", json);
        Assert.True(good.Passed, string.Join("; ", good.Notes));

        // A planted secret, the URL path or an e-mail address in the request fails masking.
        var leak = Grade(Scenario(secrets: "hunter2"), "check #1: password hunter2 at /private/path?x=1 by ops@corp.test", json);
        Assert.False(leak.MaskingOk);
        Assert.Contains(leak.Notes, n => n.Contains("hunter2") && n.Contains("/private/path?x=1") && n.Contains("ops@corp.test"));

        Assert.False(Grade(Scenario(language: "tr"), "check #1: x", json, "Turkish").LanguageOk);
        Assert.True(Grade(Scenario(language: "tr"), "check #1: x",
            Canned("Sunucu HTTP 503 döndürüyor ve hizmet kullanılamıyor.", "HTTP 503", ["kontrol #1: 503"]), "Turkish").LanguageOk);
    }
}
