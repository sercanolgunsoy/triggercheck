using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Features.MonitorSetup;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Web.Areas.App.Support;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>AI-9 monitor setup assistant: masking, parsing, clamping, gating, rate limit, audit and "never saves".</summary>
public class MonitorSetupTests
{
    private static string Answer(string type = "http", string? target = "[U1]", object? interval = null, string extra = "") => $$"""
        {"outcome":"form","clarification":null,"explanation":"An HTTP monitor.","type":{{Json(type)}},"name":null,"target":{{Json(target)}},
         "port":null,"keyword":null,"http_method":null,"expected_status_codes":null,"interval_seconds":{{JsonSerializer.Serialize(interval)}},
         "timeout_seconds":null,"failure_threshold":null,"dns_record_type":null,"json_path":null,"assertion_operator":null,
         "expected_value":null,"grace_period_seconds":null{{extra}}}
        """;

    private static string Json(string? s) => JsonSerializer.Serialize(s);

    private static Task<MonitorSetupResult> SuggestAsync(AiTestHost host, int customerId, string request, string userId = "user-1") =>
        host.InScopeAsync(sp => sp.GetRequiredService<MonitorSetupAssistant>().SuggestAsync(customerId, userId, request));

    private static Plan Plan(int minInterval = 60) => new() { Name = "Standard", MinIntervalSeconds = minInterval, MaxMonitors = 10 };

    private static MonitorSetupProposal Build(string json, string request = "check https://shop.example.test/a", int minInterval = 60) =>
        MonitorSetupForm.Build(MonitorSetupDraft.TryParse(json)!, Plan(minInterval), new MonitorSetupRequest(request, 1000));

    // ---------------------------------------------------------------- masking (K5)

    [Fact]
    public async Task Secrets_links_and_quoted_text_never_reach_the_model()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        host.Client.Responder = _ => FakeAiClient.Success(Answer("keyword", extra: ""));

        await SuggestAsync(host, c.Id,
            "Watch https://admin:hunter2@shop.example.test/checkout/pay?session=s3cr3tSession and alert if 'Pay now' is missing. " +
            "Token: ghp_abcdefghijklmnopqrstuvwxyz0123456789, Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc123def456, mail ops@corp.test or call +90 532 123 45 67");

        var sent = Assert.Single(host.Client.Requests);
        var all = sent.SystemPrompt + sent.UserContent;
        foreach (var secret in new[] { "hunter2", "admin", "/checkout", "s3cr3tSession", "Pay now", "ghp_abc", "eyJhbGci", "ops@corp.test", "532 123" })
            Assert.DoesNotContain(secret, all, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[U1]", sent.UserContent);
        Assert.Contains("U1: https://shop.example.test/…", sent.UserContent);
        Assert.Contains("\"[T1]\"", sent.UserContent);
        Assert.Contains("[secret]", sent.UserContent);
        Assert.Contains("[email]", sent.UserContent);
        Assert.Contains("[phone]", sent.UserContent);
        // The request is delimited data; the system prompt is the fixed one (the request cannot change it).
        Assert.Contains("<untrusted_data source=\"request\">", sent.UserContent);
        Assert.Equal(MonitorSetupPrompts.System, sent.SystemPrompt);
        Assert.Equal(AiFeatures.MonitorSetupAssistant, sent.Feature);
    }

    [Fact]
    public void Placeholders_are_restored_locally_and_invented_ones_are_dropped()
    {
        var r = new MonitorSetupRequest("check example.com's page shop.example.test/odeme for 'Ödeme' and “Pay now”, or [U7]", 1000);
        Assert.Equal("shop.example.test/odeme", r.Urls["U1"]);
        Assert.Equal("Ödeme", r.Quotes["T1"]);
        Assert.Equal("Pay now", r.Quotes["T2"]);
        Assert.DoesNotContain("[U7]", r.PlaceholderText); // a typed look-alike cannot point at a link
        Assert.True(r.TryRestore("[T1] and [U1]", out var restored));
        Assert.Equal("Ödeme and shop.example.test/odeme", restored);
        Assert.False(r.TryRestore("[T9]", out _));

        var p = Build(Answer(target: "[U9]"), "check https://shop.example.test/a");
        Assert.Equal("", p.Fields!.Target);
        Assert.Contains(p.Notes, n => n.Kind == MonitorSetupNoteKind.UnknownReference);
    }

    // ---------------------------------------------------------------- parsing and clamping

    [Fact]
    public void Unknown_or_extra_fields_are_ignored()
    {
        var json = Answer(interval: 60, extra: """, "is_enabled": false, "customer_id": 99, "save": true, "alert_channels": ["x"]""");
        var draft = MonitorSetupDraft.TryParse(json)!;
        var stored = draft.ToJson();
        foreach (var name in new[] { "is_enabled", "customer_id", "save", "alert_channels" }) Assert.DoesNotContain(name, stored);

        // Wrong JSON types count as null (server defaults), a missing outcome rejects the answer.
        var typed = MonitorSetupDraft.TryParse(Answer(interval: "60"))!;
        Assert.Null(typed.IntervalSeconds);
        Assert.Null(MonitorSetupDraft.TryParse("""{"type":"http"}"""));
        Assert.Null(MonitorSetupDraft.TryParse("not json"));

        // Every property of the schema is required; nothing else is allowed.
        var schema = MonitorSetupPrompts.Schema();
        Assert.False(schema["additionalProperties"].GetBoolean());
        Assert.Equal(schema["properties"].EnumerateObject().Count(), schema["required"].GetArrayLength());
    }

    [Theory]
    [InlineData("smtp")]
    [InlineData("HTTP ")]
    [InlineData("monitor")]
    public void Unknown_types_are_rejected(string type)
    {
        var p = Build(Answer(type: type));
        Assert.Equal(MonitorSetupOutcome.UnsupportedType, p.Outcome);
        Assert.Null(p.Fields);
    }

    [Theory]
    [InlineData(15, 60, 60, MonitorSetupNoteKind.IntervalRaisedToPlanMinimum)]
    [InlineData(1, 60, 60, MonitorSetupNoteKind.IntervalRaisedToPlanMinimum)]
    [InlineData(45, 15, 60, MonitorSetupNoteKind.IntervalRounded)]
    [InlineData(7200, 15, 300, MonitorSetupNoteKind.IntervalRounded)]
    [InlineData(30, 15, 30, null)]
    [InlineData(null, 60, 60, null)]
    [InlineData(null, 300, 300, null)]
    public void Interval_is_clamped_to_the_plan_with_a_note(int? requested, int planMin, int expected, MonitorSetupNoteKind? note)
    {
        var p = Build(Answer(interval: requested), minInterval: planMin);
        Assert.Equal(expected, p.Fields!.IntervalSeconds);
        if (note is { } n)
        {
            var single = Assert.Single(p.Notes, x => x.Kind == n);
            Assert.Equal(requested, single.From);
            Assert.Equal(expected, single.To);
        }
        else Assert.DoesNotContain(p.Notes, x => x.Kind is MonitorSetupNoteKind.IntervalRaisedToPlanMinimum or MonitorSetupNoteKind.IntervalRounded);
    }

    [Fact]
    public void Numbers_and_choices_are_clamped_to_the_form_rules()
    {
        var json = """
            {"outcome":"form","clarification":null,"explanation":"x","type":"keyword","name":null,"target":"[U1]","port":99999,
             "keyword":"[T1]","http_method":"HEAD","expected_status_codes":"200 OK","interval_seconds":60,"timeout_seconds":90,
             "failure_threshold":9,"dns_record_type":"MX","json_path":"$.x","assertion_operator":"less_than","expected_value":"3",
             "grace_period_seconds":5}
            """;
        var p = Build(json, "check https://shop.example.test/a for 'Hello'");
        var f = p.Fields!;
        Assert.Equal(MonitorType.Keyword, f.Type);
        Assert.Equal("https://shop.example.test/a", f.Target);
        Assert.Equal("Hello", f.Keyword);
        Assert.Equal("GET", f.HttpMethod);              // HEAD has no body
        Assert.Equal("200-299", f.ExpectedStatusCodes); // invalid list reset
        Assert.Equal(30, f.TimeoutSeconds);             // 90 → 60 → shorter than the 60 s interval
        Assert.Equal(5, f.FailureThreshold);
        Assert.Null(f.Port);                            // not a TCP monitor
        Assert.Null(f.DnsRecordType);                   // fields of other types are cleared
        Assert.Null(f.JsonPath);
        Assert.Null(f.AssertionOperator);
        Assert.Null(f.ExpectedValue);
        Assert.Equal("shop.example.test", f.Name);      // default name from the host
        var kinds = p.Notes.Select(n => n.Kind).ToList();
        Assert.Contains(MonitorSetupNoteKind.MethodChanged, kinds);
        Assert.Contains(MonitorSetupNoteKind.StatusCodesReset, kinds);
        Assert.Contains(MonitorSetupNoteKind.TimeoutAdjusted, kinds);
        Assert.Contains(MonitorSetupNoteKind.FailureThresholdAdjusted, kinds);
    }

    [Theory]
    [InlineData("tcp", "tcp://db.example.test:5432/x", null, "db.example.test", 5432, false)]
    [InlineData("tcp", "db.example.test", 70000, "db.example.test", null, true)]
    [InlineData("ping", "https://u:p@router.example.test/status", null, "router.example.test", null, false)]
    [InlineData("dns", "example.test", null, "example.test", null, false)]
    [InlineData("http", "www.example.test/health", null, "https://www.example.test/health", null, false)]
    public void Targets_are_normalized_per_type(string type, string target, int? port, string expectedTarget, int? expectedPort, bool portNote)
    {
        var json = Answer(type, target).Replace("\"port\":null", "\"port\":" + (port?.ToString() ?? "null"));
        var p = Build(json, "set it up");
        Assert.Equal(expectedTarget, p.Fields!.Target);
        Assert.Equal(expectedPort, p.Fields.Port);
        Assert.Equal(portNote, p.Notes.Any(n => n.Kind == MonitorSetupNoteKind.PortInvalid));
    }

    [Fact]
    public void Credentials_are_removed_from_the_prefilled_link_and_heartbeats_have_no_target()
    {
        var p = Build(Answer(), "check https://admin:hunter2@intranet.example.test/status");
        Assert.Equal("https://intranet.example.test/status", p.Fields!.Target);
        Assert.Contains(p.Notes, n => n.Kind == MonitorSetupNoteKind.CredentialsRemoved);

        var hb = Build(Answer("heartbeat", "backup.example.test", 120).Replace("\"grace_period_seconds\":null", "\"grace_period_seconds\":5"));
        Assert.Equal("", hb.Fields!.Target);
        Assert.Equal(300, hb.Fields.IntervalSeconds); // heartbeat intervals: 60, 300, ...
        Assert.Equal(MonitorSetupForm.MinGracePeriodSeconds, hb.Fields.GracePeriodSeconds);
        Assert.Contains(hb.Notes, n => n.Kind == MonitorSetupNoteKind.GracePeriodAdjusted);
    }

    [Fact]
    public void A_clarification_fills_nothing()
    {
        var p = Build("""{"outcome":"clarify","clarification":"Which site?","explanation":"No address.","type":"http","target":"x"}""");
        Assert.Equal(MonitorSetupOutcome.Clarify, p.Outcome);
        Assert.Null(p.Fields);
        Assert.Equal("Which site?", p.Clarification);
    }

    [Fact]
    public void Form_constants_match_the_monitor_form()
    {
        Assert.Equal(MonitorInput.HttpMethods, MonitorSetupPrompts.HttpMethods.ToArray());
        Assert.Equal(MonitorInput.DnsRecordTypes, MonitorSetupPrompts.RecordTypes.ToArray());
        Assert.Equal(MonitorInput.JsonOperators.Order(), MonitorSetupPrompts.Operators.Values.Order());
        Assert.Equal(MonitorInput.MinGracePeriodSeconds, MonitorSetupForm.MinGracePeriodSeconds);
        Assert.Equal(MonitorInput.MaxGracePeriodSeconds, MonitorSetupForm.MaxGracePeriodSeconds);
        Assert.Equal(MonitorInput.DefaultGracePeriodSeconds, MonitorSetupForm.DefaultGracePeriodSeconds);
        var input = new MonitorInput();
        Assert.Equal(MonitorSetupForm.DefaultTimeoutSeconds, input.TimeoutSeconds);
        Assert.Equal(MonitorSetupForm.DefaultFailureThreshold, input.FailureThreshold);
        Assert.Equal(MonitorSetupForm.DefaultStatusCodes, input.ExpectedStatusCodes);
        Assert.Equal(Enum.GetValues<MonitorType>().Order(), MonitorSetupPrompts.Types.Values.Order());
    }

    // ---------------------------------------------------------------- gating, budget, rate limit, audit

    [Fact]
    public async Task Succeeded_call_is_recorded_and_never_creates_a_monitor()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        host.Client.Responder = _ => FakeAiClient.Success(Answer(interval: 60));

        var r = await SuggestAsync(host, c.Id, "check https://shop.example.test/a every minute");

        Assert.Equal(MonitorSetupStatus.Ready, r.Status);
        Assert.Equal("https://shop.example.test/a", r.Proposal!.Fields!.Target);
        Assert.Equal(0, await host.WithDbAsync(db => db.Monitors.CountAsync()));
        var d = await host.WithDbAsync(db => db.AiDecisions.SingleAsync());
        Assert.Equal(AiDecisionStatus.Succeeded, d.Status);
        Assert.Equal(AiFeatures.MonitorSetupAssistant, d.Feature);
        Assert.Equal(MonitorSetupAssistant.SubjectType, d.SubjectType);
        Assert.Equal("user-1", d.SubjectId);
        Assert.Equal(AiAutonomy.A1, d.AutonomyLevel);
        Assert.True(d.InputTokens > 0 && d.CostEstimate > 0);
        Assert.DoesNotContain("shop.example.test/a", d.OutputJson); // the stored draft holds placeholders only

        // The assistant has no way to write monitors: it depends only on the AI layer and the context it records to.
        var deps = typeof(MonitorSetupAssistant).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
        Assert.DoesNotContain(deps, t => t == typeof(MonitorCommands) || t.Namespace?.StartsWith("TriggerCheck.Web", StringComparison.Ordinal) == true);

        // An identical request reuses the answer (no second call), rebuilt with this request's own link.
        var again = await SuggestAsync(host, c.Id, "check https://shop.example.test/b every minute");
        Assert.Equal(MonitorSetupStatus.Ready, again.Status);
        Assert.Equal("https://shop.example.test/b", again.Proposal!.Fields!.Target);
        Assert.Single(host.Client.Requests);
    }

    [Fact]
    public async Task Turkish_customers_get_a_Turkish_request()
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        await host.WithDbAsync(db => db.Customers.Where(x => x.Id == c.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AiLanguage, "tr")));
        await SuggestAsync(host, c.Id, "sitemi izle");
        Assert.StartsWith("response_language: Turkish", Assert.Single(host.Client.Requests).UserContent);
        Assert.Equal("tr", (await host.WithDbAsync(db => db.AiDecisions.SingleAsync())).Language);
    }

    [Theory]
    [InlineData("off", MonitorSetupStatus.Off, null)]
    [InlineData("optout", MonitorSetupStatus.OptOut, AiDecisionStatus.SkippedOptOut)]
    [InlineData("plan", MonitorSetupStatus.NotInPlan, AiDecisionStatus.SkippedPlan)]
    [InlineData("budget", MonitorSetupStatus.OverBudget, AiDecisionStatus.SkippedBudget)]
    public async Task Gating_skips_the_call(string gate, MonitorSetupStatus expected, AiDecisionStatus? recorded)
    {
        await using var host = await AiTestHost.CreateAsync(gate == "off" ? new() { ["Ai:Enabled"] = "false" } : null);
        var c = gate switch
        {
            "optout" => await host.AddCustomerAsync(aiEnabled: false),
            "plan" => await host.AddCustomerAsync(features: AiFeatures.Format([AiFeatures.IncidentInterpretation, AiFeatures.SettingsSuggestions])),
            "budget" => await host.AddCustomerAsync(callLimit: 1),
            _ => await host.AddCustomerAsync(),
        };
        if (gate == "budget")
            await host.WithDbAsync(async db =>
            {
                db.AiDecisions.Add(new AiDecision { CustomerId = c.Id, Feature = AiFeatures.IncidentInterpretation, SubjectType = "Incident", SubjectId = "1",
                    Status = AiDecisionStatus.Succeeded, InputTokens = 10, OutputTokens = 10, CreatedAtUtc = host.Now });
                await db.SaveChangesAsync();
            });

        var r = await SuggestAsync(host, c.Id, "check https://shop.example.test");

        Assert.Equal(expected, r.Status);
        Assert.Null(r.Proposal);
        Assert.Empty(host.Client.Requests);
        var rows = await host.WithDbAsync(db => db.AiDecisions.Where(d => d.Feature == AiFeatures.MonitorSetupAssistant).ToListAsync());
        if (recorded is null) Assert.Empty(rows);
        else Assert.Equal(recorded, Assert.Single(rows).Status);
    }

    [Fact]
    public async Task Requests_are_rate_limited_per_user()
    {
        await using var host = await AiTestHost.CreateAsync(new() { ["Ai:SetupAssistant:RequestsPerUserPerHour"] = "2" });
        var c = await host.AddCustomerAsync();
        host.Client.Responder = _ => FakeAiClient.Success(Answer());

        Assert.Equal(MonitorSetupStatus.Ready, (await SuggestAsync(host, c.Id, "check https://a.example.test")).Status);
        Assert.Equal(MonitorSetupStatus.Ready, (await SuggestAsync(host, c.Id, "check https://b.example.test")).Status);
        Assert.Equal(MonitorSetupStatus.RateLimited, (await SuggestAsync(host, c.Id, "check https://c.example.test")).Status);
        Assert.Equal(2, host.Client.Requests.Count);

        // Another user of the same customer has their own allowance; the window rolls over after an hour.
        Assert.Equal(MonitorSetupStatus.Ready, (await SuggestAsync(host, c.Id, "check https://c.example.test", "user-2")).Status);
        host.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(MonitorSetupStatus.Ready, (await SuggestAsync(host, c.Id, "check https://d.example.test")).Status);
    }

    [Theory]
    [InlineData("{\"type\":\"http\"}", "invalid_output")]
    [InlineData("not json", "invalid_output")]
    public async Task Invalid_output_is_a_recorded_failure(string json, string code)
    {
        await using var host = await AiTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        host.Client.Responder = _ => FakeAiClient.Success(json);

        var r = await SuggestAsync(host, c.Id, "check https://shop.example.test");

        Assert.Equal(MonitorSetupStatus.Failed, r.Status);
        var d = await host.WithDbAsync(db => db.AiDecisions.SingleAsync());
        Assert.Equal(AiDecisionStatus.Failed, d.Status);
        Assert.Equal(code, d.ErrorCode);
    }

    [Fact]
    public async Task Empty_or_overlong_requests()
    {
        await using var host = await AiTestHost.CreateAsync(new() { ["Ai:SetupAssistant:MaxRequestChars"] = "40" });
        var c = await host.AddCustomerAsync();
        host.Client.Responder = _ => FakeAiClient.Success(Answer());
        Assert.Equal(MonitorSetupStatus.Empty, (await SuggestAsync(host, c.Id, "   ")).Status);
        Assert.Empty(host.Client.Requests);

        var r = await SuggestAsync(host, c.Id, "check https://shop.example.test " + new string('x', 200));
        Assert.Contains(r.Proposal!.Notes, n => n.Kind == MonitorSetupNoteKind.RequestTruncated);
        Assert.DoesNotContain("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", Assert.Single(host.Client.Requests).UserContent);
    }
}
