using System.Text.Json;
using System.Text.Json.Nodes;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
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
        var wrong = JsonNode.Parse(Canned("Name resolution failed.", "The DNS lookup failed.", ["check #1: no DNS time"]))!;
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

    private static string Canned(string summary, string cause, string[] evidence, string confidence = "high") => JsonSerializer.Serialize(new
    {
        summary,
        hypotheses = new[] { new { cause, confidence, evidence } },
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
            AiInterpretationSchema.Build([]));

    [Theory]
    // Reviewer probes: substrings, "resolved", hedges, check ids and negated phrases must not pass.
    [InlineData("The request timed out after 1500 ms.", "http_5xx")]
    [InlineData("Timeouts until the incident was resolved.", "dns_failure")]
    [InlineData("Something is wrong, maybe DNS or a timeout.", "dns_failure")]
    [InlineData("Something is wrong, maybe DNS or a timeout.", "timeout")]
    [InlineData("Check #5004 failed.", "http_5xx")]
    [InlineData("The certificate chain is fine; the server returned 404", "tls_untrusted")]
    [InlineData("Port 5432 is closed.", "http_5xx")]
    public void Grader_rejects_false_positive_causes(string cause, string label) =>
        Assert.False(EvalGrader.CauseMatches(cause, label, slowdown: false), $"'{cause}' must not pass {label}");

    [Theory]
    [InlineData("The request timed out after 1500 ms.", "timeout")]
    [InlineData("The certificate chain is fine; the server returned 404", "http_4xx")]
    [InlineData("Host-wide outage: all monitors on the same host fail with HTTP 502.", "host_outage")]
    [InlineData("The DNS name could not be resolved.", "dns_failure")]
    [InlineData("Sunucu HTTP 503 döndürüyor.", "http_5xx")]
    public void Grader_accepts_clear_causes(string cause, string label) =>
        Assert.True(EvalGrader.CauseMatches(cause, label, slowdown: false), $"'{cause}' should pass {label}");

    [Fact]
    public void Evidence_must_be_grounded_in_the_input()
    {
        const string input = "check #42: 2026-03-10T11:59:00Z DOWN status=503 total_ms=1234\n<untrusted_data source=\"e\">\nName or service not known\n</untrusted_data>\nvalidation_error: RemoteCertificateNameMismatch";
        Assert.True(EvalGrader.Grounded("check #42 returned 503", input));
        Assert.True(EvalGrader.Grounded("errors say 'Name or service not known'", input));
        Assert.True(EvalGrader.Grounded("total time 1234 ms", input));
        Assert.True(EvalGrader.Grounded("RemoteCertificateNameMismatch", input));
        Assert.False(EvalGrader.Grounded("check #7 failed", input));
        Assert.False(EvalGrader.Grounded("response took 9999 ms", input));
        Assert.False(EvalGrader.Grounded("the server seems overloaded", input));
    }

    [Fact]
    public void Grader_classifies_causes_in_English_and_Turkish()
    {
        Assert.Equal(["dns"], EvalGrader.Classify("DNS resolution of the host fails", slowdown: false));
        Assert.Equal(["tls_expired"], EvalGrader.Classify("Sertifikanın süresi dolmuş", slowdown: false));
        Assert.Equal(["timeout"], EvalGrader.Classify("İstekler zaman aşımına uğruyor", slowdown: false));
        Assert.Equal(["slow_ttfb"], EvalGrader.Classify("TTFB rose from 150 ms to 1200 ms", slowdown: true));
        Assert.Empty(EvalGrader.Classify("Something went wrong", slowdown: false));
    }

    [Fact]
    public void Grader_flags_leaks_vague_causes_missing_evidence_and_wrong_language()
    {
        var ok = FakeAiClient.Success(Canned("The server returns HTTP 503.", "HTTP 503 from the backend", ["check #1: status 503"]));
        var good = EvalGrader.Grade(Scenario(), Request("check #1: status=503"), ok, null);
        Assert.True(good.Passed, string.Join("; ", good.Notes));

        // A planted secret, the URL path or an e-mail address in the request fails masking.
        var leak = EvalGrader.Grade(Scenario(secrets: "hunter2"), Request("error: password hunter2 at /private/path?x=1 by ops@corp.test"), ok, null);
        Assert.False(leak.MaskingOk);
        Assert.Contains(leak.Notes, n => n.Contains("hunter2") && n.Contains("/private/path?x=1") && n.Contains("ops@corp.test"));

        // "DNS, TLS or a timeout" names three categories: not a primary cause.
        var vague = EvalGrader.Grade(Scenario("dns_failure"), Request("x"),
            FakeAiClient.Success(Canned("Unclear.", "DNS, TLS certificate expired or a timeout", ["check #1"])), null);
        Assert.False(vague.PrimaryCauseOk);

        var noEvidence = EvalGrader.Grade(Scenario(), Request("x"), FakeAiClient.Success(Canned("HTTP 503.", "HTTP 503", [])), null);
        Assert.False(noEvidence.EvidenceOk);

        var english = EvalGrader.Grade(Scenario(language: "tr"), Request("x", "Turkish"), ok, null);
        Assert.False(english.LanguageOk);
        var turkish = EvalGrader.Grade(Scenario(language: "tr"), Request("x", "Turkish"),
            FakeAiClient.Success(Canned("Sunucu HTTP 503 döndürüyor ve hizmet kullanılamıyor.", "HTTP 503", ["kontrol #1: 503"])), null);
        Assert.True(turkish.LanguageOk);
    }
}
