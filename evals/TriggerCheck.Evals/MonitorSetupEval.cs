using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Features.MonitorSetup;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Evals;

/// <summary>
/// AI-9 cases (monitor setup assistant): the case's <see cref="EvalScenario.Request"/> goes through the production
/// <see cref="MonitorSetupAssistant"/> (masking, prompt, schema, budget, the server-side clamping in
/// <see cref="MonitorSetupForm"/>), and the grader compares the resulting form with the labelled fields. Graded:
/// <list type="bullet">
/// <item><b>primary cause</b> (reported as such): the outcome ("setup_form" / "setup_clarify") and every expected field
/// and note match the final form, i.e. what the customer would see pre-filled;</item>
/// <item><b>masking</b>: none of the case's secrets (quoted text, URL paths and queries, credentials), no link path and no
/// e-mail address appear in the request actually sent;</item>
/// <item><b>language</b>: the request asks for the case's language and the explanation (or question) is written in it.</item>
/// </list>
/// There are no hypotheses, so evidence is not graded (always ok).
/// </summary>
public static partial class MonitorSetupEval
{
    public const string FormLabel = "setup_form";
    public const string ClarifyLabel = "setup_clarify";

    /// <summary>A new customer on an all-features plan with the case's minimum interval, then one assistant request.</summary>
    public static async Task<(MonitorSetupResult Result, AiDecision? Decision)> RunAsync(IServiceProvider sp, EvalScenario s, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var plan = new Plan
        {
            Name = "Eval-" + s.Id, MinIntervalSeconds = s.PlanMinIntervalSeconds ?? 15, MaxMonitors = 100,
            AiFeatures = AiFeatures.Format(AiFeatures.All.Select(f => f.Name)), AiMonthlyCallLimit = 1_000_000
        };
        var customer = new Customer { Name = "Eval " + s.Id, ContactEmail = "ops@example.test", Plan = plan, AiEnabled = true, AiLanguage = s.Language };
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);

        var result = await sp.GetRequiredService<MonitorSetupAssistant>().SuggestAsync(customer.Id, "eval-user", s.Request, ct);
        var decision = result.DecisionId is long id ? await db.AiDecisions.FindAsync([id], ct) : null;
        return (result, decision);
    }

    [GeneratedRegex(@"(?i)[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}")]
    private static partial Regex Email();

    public static EvalCaseResult Grade(EvalScenario s, AiCallRequest? request, AiCallResult? raw, MonitorSetupResult result, AiDecision? decision)
    {
        var r = new EvalCaseResult
        {
            Id = s.Id, Feature = s.Feature, Language = s.Language, Label = s.Expect.PrimaryCause,
            Status = decision?.Status.ToString() ?? result.Status.ToString(),
            InputTokens = raw?.InputTokens ?? 0, OutputTokens = raw?.OutputTokens ?? 0,
            CacheReadTokens = raw?.CacheReadTokens ?? 0, CacheWriteTokens = raw?.CacheWriteTokens ?? 0,
            EvidenceOk = true, // no hypotheses in this feature
            UserContent = request?.UserContent,
        };
        if (request is null)
        {
            r.Notes.Add("no request was sent (" + result.Status + ")");
            return r;
        }

        // ---- masking ----
        var sent = request.SystemPrompt + "\n" + request.UserContent;
        var leaks = s.Secrets.Where(x => !string.IsNullOrEmpty(x) && sent.Contains(x, StringComparison.OrdinalIgnoreCase)).ToList();
        var userLinks = new MonitorSetupRequest(s.Request, int.MaxValue).Urls.Values;
        foreach (var link in userLinks)
            if (Uri.TryCreate(link.Contains("://") ? link : "https://" + link, UriKind.Absolute, out var u) && u.PathAndQuery.Length > 1
                && sent.Contains(u.PathAndQuery, StringComparison.Ordinal))
                leaks.Add(u.PathAndQuery);
        leaks.AddRange(Email().Matches(sent).Select(m => m.Value));
        r.MaskingOk = leaks.Count == 0;
        if (!r.MaskingOk) r.Notes.Add("leaked: " + string.Join(", ", leaks.Distinct()));

        var p = result.Proposal;
        if (p is null)
        {
            r.Notes.Add("no usable output (" + (raw?.ErrorCode ?? result.Status.ToString()) + ")");
            return r;
        }
        var text = p.Explanation ?? p.Clarification ?? "";
        r.Summary = text;

        // ---- outcome and fields ----
        r.PrimaryCategory = p.Outcome == MonitorSetupOutcome.Form ? FormLabel : p.Outcome == MonitorSetupOutcome.Clarify ? ClarifyLabel : "unsupported_type";
        var mismatches = new List<string>();
        if (r.PrimaryCategory != s.Expect.PrimaryCause) mismatches.Add($"outcome {r.PrimaryCategory}, expected {s.Expect.PrimaryCause}");
        if (p.Fields is { } f && s.Expect.Fields is { } expected)
        {
            var actual = JsonSerializer.SerializeToElement(f, EvalScenario.Json);
            foreach (var (name, want) in expected)
            {
                var got = actual.TryGetProperty(name, out var g) ? g : default;
                if (!SameValue(want, got)) mismatches.Add($"{name}: {(got.ValueKind == JsonValueKind.Undefined ? "missing" : got.GetRawText())}, expected {want.GetRawText()}");
            }
        }
        foreach (var note in s.Expect.Notes)
            if (!p.Notes.Any(n => n.Kind.ToString() == note)) mismatches.Add("note missing: " + note);
        r.PrimaryCauseOk = mismatches.Count == 0;
        r.Notes.AddRange(mismatches);

        // ---- language ----
        var expectedLanguage = s.Language.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "Turkish" : "English";
        r.LanguageOk = request.UserContent.StartsWith("response_language: " + expectedLanguage, StringComparison.Ordinal)
                       && text.Length > 0 && EvalGrader.LooksTurkish(text) == (expectedLanguage == "Turkish");
        if (!r.LanguageOk) r.Notes.Add($"explanation is not in {expectedLanguage}");
        return r;
    }

    private static bool SameValue(JsonElement want, JsonElement got)
    {
        if (got.ValueKind == JsonValueKind.Undefined) return want.ValueKind == JsonValueKind.Null;
        if (want.ValueKind == JsonValueKind.String && got.ValueKind == JsonValueKind.String)
            return string.Equals(want.GetString(), got.GetString(), StringComparison.Ordinal);
        if (want.ValueKind == JsonValueKind.Number && got.ValueKind == JsonValueKind.Number)
            return want.GetDecimal() == got.GetDecimal();
        return want.ValueKind == got.ValueKind && (want.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False);
    }
}
