using System.Text.Json;
using System.Text.RegularExpressions;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Ai.Features.Postmortem;
using TriggerCheck.Ai.Narrative;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Evals;

/// <summary>
/// AI-8 cases (postmortem / status page draft): an AI-2 style resolved incident runs through the production
/// <see cref="PostmortemDraftProvider"/> and <see cref="AiInterpreter"/>. Code-graded, the report columns mean:
/// <list type="bullet">
/// <item><b>primary cause</b>: the validated <c>likely_cause.category</c> is one of the categories accepted for the
/// case's label (<see cref="EvalGrader.Labels"/>, the AI-2 labels);</item>
/// <item><b>evidence</b> = grounded, publishable output: the validated draft kept a customer message, the contract
/// removed no sentence, every placeholder in the raw answer is a key the provider offered, and the rendered customer
/// message contains neither the monitored host nor any of <c>expect.notInAnswer</c> / <c>secrets</c>;</item>
/// <item><b>numbers</b>: an independent check (not the production contract) that no raw text has a digit outside
/// <c>{{placeholders}}</c>; one invented number fails the feature's gate;</item>
/// <item><b>masking</b>: no planted secret, monitor name, URL path / query or e-mail address in the request sent;</item>
/// <item><b>language</b>: the request asks for the case's language and the customer message is written in it.</item>
/// </list>
/// </summary>
public static partial class EvalPostmortem
{
    private sealed record RawCause(string? Category, string? Confidence, string? Text);

    private sealed record Raw(RawCause? LikelyCause, string? CustomerMessage, List<string?>? Unknowns);

    [GeneratedRegex(@"(?i)[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}")]
    private static partial Regex Email();

    public static EvalCaseResult Grade(EvalScenario s, AiCallRequest? request, AiCallResult? result, AiDecision? decision, AiPrompt? prompt)
    {
        var r = new EvalCaseResult
        {
            Id = s.Id, Feature = s.Feature, Language = s.Language, Label = s.Expect.PrimaryCause,
            Status = decision?.Status.ToString() ?? "none",
            InputTokens = result?.InputTokens ?? 0, OutputTokens = result?.OutputTokens ?? 0,
            CacheReadTokens = result?.CacheReadTokens ?? 0, CacheWriteTokens = result?.CacheWriteTokens ?? 0,
            UserContent = request?.UserContent,
        };
        if (request is null)
        {
            r.Notes.Add("no request was sent");
            return r;
        }

        // ---- masking (request actually sent) ----
        var sent = request.SystemPrompt + "\n" + request.UserContent;
        var leaks = s.Secrets.Where(x => !string.IsNullOrEmpty(x) && sent.Contains(x, StringComparison.OrdinalIgnoreCase)).ToList();
        Uri.TryCreate(s.Monitor.Target, UriKind.Absolute, out var target);
        if (target is not null && target.PathAndQuery.Length > 1 && sent.Contains(target.PathAndQuery, StringComparison.Ordinal)) leaks.Add(target.PathAndQuery);
        leaks.AddRange(new[] { s.Monitor.Name }.Concat(s.SameHost.Select(m => m.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n) && sent.Contains(n, StringComparison.Ordinal)));
        leaks.AddRange(Email().Matches(sent).Select(m => m.Value));
        r.MaskingOk = leaks.Count == 0;
        if (!r.MaskingOk) r.Notes.Add("leaked: " + string.Join(", ", leaks.Distinct()));

        Raw? raw = null;
        if (result is { Outcome: AiCallOutcome.Succeeded })
            try { raw = JsonSerializer.Deserialize<Raw>(result.Json ?? "", PostmortemOutput.Json); }
            catch (JsonException) { raw = null; }
        if (raw is null)
        {
            r.Notes.Add("no usable output (" + (result?.ErrorCode ?? result?.Outcome.ToString() ?? "none") + ")");
            return r;
        }
        var texts = new[] { raw.LikelyCause?.Text, raw.CustomerMessage }.Concat(raw.Unknowns ?? []).OfType<string>().ToList();
        r.Summary = raw.CustomerMessage;

        // ---- numbers: independent of the production contract ----
        var digits = texts.Where(t => NarrativeRenderer.Placeholder().Replace(t, "").Any(char.IsDigit)).ToList();
        r.NumbersOk = digits.Count == 0;
        if (digits.Count > 0) r.Notes.Add("digits outside placeholders: " + string.Join(" | ", digits));

        // ---- primary cause: the validated category ----
        var validated = decision?.Status == AiDecisionStatus.Succeeded ? PostmortemOutput.TryParse(decision.OutputJson) : null;
        r.PrimaryCategory = validated?.Category;
        r.TopCause = validated?.LikelyCause;
        var accepted = EvalGrader.Labels.TryGetValue(s.Expect.PrimaryCause, out var a) ? a : [];
        r.PrimaryCauseOk = validated is not null && accepted.Contains(validated.Category);
        if (!r.PrimaryCauseOk) r.Notes.Add($"category {validated?.Category ?? "none"}, expected one of [{string.Join(", ", accepted)}]");

        // ---- evidence: grounded and publishable ----
        var offered = (prompt?.Output as PostmortemOutputContract)?.Values.Keys.ToHashSet() ?? [];
        offered.Add(PostmortemRenderer.ServiceKey);
        var unknownKeys = texts.SelectMany(t => NarrativeRenderer.Placeholder().Matches(t).Select(m => m.Groups[1].Value)).Where(k => !offered.Contains(k)).Distinct().ToList();
        var problems = new List<string>();
        if (validated is null) problems.Add("no validated draft (" + (decision?.ErrorCode ?? decision?.Status.ToString() ?? "none") + ")");
        else
        {
            if (validated.RemovedSentences > 0) problems.Add($"{validated.RemovedSentences} sentence(s) removed by the contract");
            var message = validated.Render(validated.CustomerMessage, "the service");
            if (target is not null && message.Contains(target.Host, StringComparison.OrdinalIgnoreCase)) problems.Add("customer message names the host");
            foreach (var x in s.Expect.NotInAnswer.Concat(s.Secrets).Where(x => !string.IsNullOrEmpty(x) && message.Contains(x, StringComparison.OrdinalIgnoreCase)))
                problems.Add("customer message contains " + x);
        }
        if (unknownKeys.Count > 0) problems.Add("unknown placeholders: " + string.Join(", ", unknownKeys));
        r.EvidenceOk = problems.Count == 0;
        r.Notes.AddRange(problems);

        // ---- language ----
        var expectedLanguage = s.Language.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "Turkish" : "English";
        r.LanguageOk = request.UserContent.StartsWith("response_language: " + expectedLanguage, StringComparison.Ordinal)
                       && !string.IsNullOrWhiteSpace(raw.CustomerMessage)
                       && EvalGrader.LooksTurkish(raw.CustomerMessage) == (expectedLanguage == "Turkish");
        if (!r.LanguageOk) r.Notes.Add($"customer message is not in {expectedLanguage}");
        return r;
    }
}
