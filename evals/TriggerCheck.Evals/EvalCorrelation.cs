using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Ai.Features.Correlation;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Evals;

/// <summary>AI-4 case data: the incidents of one customer around an outage (the production rules form the group).</summary>
public sealed class EvalGroup
{
    public List<EvalGroupIncident> Incidents { get; set; } = [];
}

public sealed class EvalGroupIncident
{
    /// <summary>Monitor name; used by expect.keep / expect.leaveOut and checked never to reach the model.</summary>
    public string Name { get; set; } = "";
    public MonitorType Type { get; set; } = MonitorType.Http;
    public string Target { get; set; } = "";
    public int? Port { get; set; }
    public double StartedMinutesAgo { get; set; }
    public double? ResolvedMinutesAgo { get; set; }
    public string? Cause { get; set; }
}

/// <summary>What the harness seeded for an AI-4 case: the job, the group and the incident id of every monitor name.</summary>
public sealed record EvalCorrelationSeed(AiJob Job, IncidentGroup? Group, IReadOnlyDictionary<string, int> IncidentIds);

/// <summary>
/// AI-4 (CorrelationGrouping) eval: the case's incidents are stored for a new customer, the production
/// <see cref="IncidentCorrelationService.ProposeAsync"/> forms the group with the L1 rules (primary, rule members,
/// ambiguous candidates), and the production provider + interpreter ask the model. Graded in code:
/// <list type="bullet">
/// <item><b>primary cause</b> (here: candidate verdicts): the candidates the validated output recommends
/// (<see cref="CorrelationPrompts.Recommended"/>, the rule the UI uses to pre-select them) are exactly
/// <c>expect.keep</c>; nothing in <c>expect.leaveOut</c> is recommended;</item>
/// <item><b>evidence</b>: every raw hypothesis cites an incident key that was sent (<c>incident C</c>, <c>error C</c>);</item>
/// <item><b>masking</b>: no monitor name, URL path / query, planted secret or e-mail address in the request;</item>
/// <item><b>language</b>: as for AI-2.</item>
/// </list>
/// </summary>
public static partial class EvalCorrelation
{
    /// <summary>Label of AI-4 cases (expect.primaryCause); the verdicts are graded from expect.keep / expect.leaveOut.</summary>
    public const string Label = "candidate_verdicts";

    public static async Task<EvalCorrelationSeed> SeedAsync(IServiceProvider sp, EvalScenario s, CancellationToken ct = default)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        DateTime Ago(double minutes) => EvalHarness.Now.AddMinutes(-minutes);
        var plan = new Plan
        {
            Name = "Eval-" + s.Id, MinIntervalSeconds = 15, MaxMonitors = 100,
            AiFeatures = AiFeatures.Format(AiFeatures.All.Select(f => f.Name)), AiMonthlyCallLimit = 1_000_000
        };
        var customer = new Customer { Name = "Eval " + s.Id, ContactEmail = "ops@example.test", Plan = plan, AiEnabled = true, AiLanguage = s.Language };
        db.Customers.Add(customer);
        var incidents = new Dictionary<string, Incident>();
        foreach (var x in s.Group!.Incidents)
        {
            var m = new MonitorEntity
            {
                Customer = customer, Name = x.Name, Type = x.Type, Target = x.Target, Port = x.Port, IntervalSeconds = 60, FailureThreshold = 2,
                Status = x.ResolvedMinutesAgo is null ? MonitorStatus.Down : MonitorStatus.Up, LastError = x.Cause, CreatedAtUtc = EvalHarness.Now.AddDays(-30),
            };
            db.Monitors.Add(m);
            var i = new Incident { Monitor = m, StartedAtUtc = Ago(x.StartedMinutesAgo), ResolvedAtUtc = x.ResolvedMinutesAgo is double r ? Ago(r) : null, Cause = x.Cause };
            db.Incidents.Add(i);
            incidents[x.Name] = i;
        }
        await db.SaveChangesAsync(ct);

        var groups = sp.GetRequiredService<IncidentCorrelationService>();
        var ids = await groups.ProposeAsync(customer.Id, ct);
        var group = ids.Count == 0 ? null : await groups.GetAsync(customer.Id, ids[0], ct);
        var subject = (group?.Id ?? 0).ToString(CultureInfo.InvariantCulture);
        return new EvalCorrelationSeed(new AiJob(AiFeatures.CorrelationGrouping, customer.Id, IncidentCorrelationService.SubjectType, subject, s.Language),
            group, incidents.ToDictionary(kv => kv.Key, kv => kv.Value.Id));
    }

    [GeneratedRegex(@"(?i)[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}")]
    private static partial Regex Email();

    public static EvalCaseResult Grade(EvalScenario s, EvalCorrelationSeed seed, AiCallRequest? request, AiCallResult? result, AiDecision? decision, AiPrompt? prompt)
    {
        var r = new EvalCaseResult
        {
            Id = s.Id, Feature = s.Feature, Language = s.Language, Label = s.Expect.PrimaryCause,
            Status = decision?.Status.ToString() ?? "none",
            InputTokens = result?.InputTokens ?? 0, OutputTokens = result?.OutputTokens ?? 0,
            CacheReadTokens = result?.CacheReadTokens ?? 0, CacheWriteTokens = result?.CacheWriteTokens ?? 0,
            UserContent = request?.UserContent,
        };
        if (seed.Group is null)
        {
            r.Notes.Add("the rules formed no group (check the case data)");
            return r;
        }
        if (request is null)
        {
            r.Notes.Add("no request was sent");
            return r;
        }

        // ---- masking ----
        var sent = request.SystemPrompt + "\n" + request.UserContent;
        var leaks = s.Secrets.Where(x => !string.IsNullOrEmpty(x) && sent.Contains(x, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var i in s.Group!.Incidents)
        {
            if (!string.IsNullOrWhiteSpace(i.Name) && sent.Contains(i.Name, StringComparison.Ordinal)) leaks.Add(i.Name);
            if (Uri.TryCreate(i.Target, UriKind.Absolute, out var u) && u.PathAndQuery.Length > 1 && sent.Contains(u.PathAndQuery, StringComparison.Ordinal))
                leaks.Add(u.PathAndQuery);
        }
        leaks.AddRange(Email().Matches(sent).Select(m => m.Value));
        r.MaskingOk = leaks.Count == 0;
        if (!r.MaskingOk) r.Notes.Add("leaked: " + string.Join(", ", leaks.Distinct()));

        var expectedLanguage = s.Language.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "Turkish" : "English";
        var raw = RawOutput.Parse(result);
        if (raw is null)
        {
            r.Notes.Add("no usable output (" + (result?.ErrorCode ?? result?.Outcome.ToString() ?? "none") + ")");
            return r;
        }
        r.Summary = raw.Summary;

        // ---- candidate verdicts (graded as "primary cause") ----
        var validated = decision?.Status == AiDecisionStatus.Succeeded ? AiInterpretation.TryParse(decision.OutputJson) : null;
        var candidates = CorrelationGroupingProvider.CandidateKeys(seed.Group);
        var recommended = CorrelationPrompts.Recommended(validated, candidates);
        var names = seed.IncidentIds.ToDictionary(kv => kv.Value, kv => kv.Key);
        var keep = s.Expect.Keep.Select(n => seed.IncidentIds.GetValueOrDefault(n)).ToHashSet();
        var leaveOut = s.Expect.LeaveOut.Select(n => seed.IncidentIds.GetValueOrDefault(n)).ToHashSet();
        var notCandidates = s.Expect.Keep.Concat(s.Expect.LeaveOut).Where(n => !seed.IncidentIds.TryGetValue(n, out var id) || !candidates.Values.Contains(id)).ToList();
        if (notCandidates.Count > 0) r.Notes.Add("not a candidate of the group: " + string.Join(", ", notCandidates));
        r.PrimaryCauseOk = notCandidates.Count == 0 && recommended.SetEquals(keep) && !recommended.Overlaps(leaveOut);
        r.PrimaryCategory = recommended.Count == 0 ? "keep none" : "keep " + string.Join(", ", recommended.Select(id => names.GetValueOrDefault(id, "?")).Order());
        if (!r.PrimaryCauseOk && notCandidates.Count == 0)
            r.Notes.Add($"recommended [{string.Join(", ", recommended.Select(id => names.GetValueOrDefault(id, "?")).Order())}], expected [{string.Join(", ", s.Expect.Keep.Order())}]");

        // ---- evidence ----
        var hypotheses = (raw.Hypotheses ?? []).Where(h => h is not null).ToList();
        var ungrounded = prompt is null ? hypotheses.Count : hypotheses.Count(h => !EvalGrader.Grounded(h, prompt));
        r.EvidenceOk = ungrounded == 0 && (candidates.Count == 0 || (validated?.Hypotheses.Count ?? 0) > 0);
        if (!r.EvidenceOk) r.Notes.Add($"{ungrounded} of {hypotheses.Count} hypotheses without an incident reference that was sent");

        // ---- language ----
        r.LanguageOk = request.UserContent.StartsWith("response_language: " + expectedLanguage, StringComparison.Ordinal)
                       && EvalGrader.LooksTurkish(raw.Summary) == (expectedLanguage == "Turkish");
        if (!r.LanguageOk) r.Notes.Add($"summary is not in {expectedLanguage}");
        return r;
    }
}
