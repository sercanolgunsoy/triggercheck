using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Ai.Qa;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Evals;

/// <summary>A monitor of an AI-7 case with its history (times are minutes before <see cref="EvalHarness.Now"/>).</summary>
public sealed class EvalQaMonitor
{
    public string Name { get; set; } = "Shop API";
    public MonitorType Type { get; set; } = MonitorType.Http;
    public string Target { get; set; } = "https://shop.example.test/health";
    public MonitorStatus Status { get; set; } = MonitorStatus.Up;
    public int IntervalSeconds { get; set; } = 60;
    public string? LastError { get; set; }
    public List<EvalCheck> Checks { get; set; } = [];
    public List<EvalIncident> Incidents { get; set; } = [];
}

/// <summary>
/// AI-7 eval cases (<c>"feature": "QuestionAnswer"</c>): the scenario's monitors are stored for one customer (plus
/// <c>otherTenant</c> monitors for another one), the question goes through the production
/// <see cref="IQuestionAnswerService"/> (prompt, tools, caps, budget) and the grader checks the whole conversation.
/// In fake mode <c>cannedTurns</c> script the model: <c>{"tools": [{"name": ..., "input": {...}}]}</c> or
/// <c>{"answer": "..."}</c>. Placeholders: <c>"{{monitor_id:Name}}"</c> / <c>"{{incident_id:Name#1}}"</c> (a JSON
/// string that becomes the number) and <c>{{monitor:Name}}</c> / <c>{{incident:Name#1}}</c> (the reference token).
/// <c>expect.references</c> lists the records the answer must cite (<c>monitor:Name</c>, <c>incident:Name#1</c>, the
/// n-th incident of the monitor in file order), <c>expect.tools</c> the tools that must have been used.
/// </summary>
public static partial class EvalQa
{
    public const string Label = "qa_grounded_answer";

    public sealed record Seeded(int CustomerId, Dictionary<string, int> Monitors, Dictionary<string, int> Incidents);

    public static async Task<Seeded> SeedAsync(AppDbContext db, EvalScenario s, CancellationToken ct)
    {
        DateTime Ago(double minutes) => EvalHarness.Now.AddMinutes(-minutes);
        Customer NewCustomer(string name, string language) => new()
        {
            Name = name, ContactEmail = "ops@example.test", AiEnabled = true, AiLanguage = language,
            Plan = new Plan
            {
                Name = "Eval-" + name, MinIntervalSeconds = 15, MaxMonitors = 100,
                AiFeatures = AiFeatures.Format(AiFeatures.All.Select(f => f.Name)), AiMonthlyCallLimit = 1_000_000
            },
        };
        var customer = NewCustomer("Eval " + s.Id, s.Language);
        var other = NewCustomer("Eval other " + s.Id, "en");
        db.Customers.AddRange(customer, other);

        var monitors = new Dictionary<string, MonitorEntity>();
        var incidents = new Dictionary<string, Incident>();
        void Add(Customer owner, EvalQaMonitor m, bool own)
        {
            var entity = new MonitorEntity
            {
                Customer = owner, Name = m.Name, Type = m.Type, Target = m.Target, Status = m.Status, IntervalSeconds = m.IntervalSeconds,
                LastError = m.LastError, CreatedAtUtc = EvalHarness.Now.AddDays(-120), StatusChangedAtUtc = Ago(60),
            };
            db.Monitors.Add(entity);
            foreach (var c in m.Checks)
                for (var i = 0; i < Math.Max(1, c.Repeat); i++)
                    db.CheckResults.Add(new CheckResult
                    {
                        Monitor = entity, CheckedAtUtc = Ago(c.MinutesAgo + i * c.EveryMinutes), IsUp = c.Up, StatusCode = c.Status,
                        ResponseTimeMs = c.Ms, DnsMs = c.Dns, ConnectMs = c.Connect, TlsMs = c.Tls, TtfbMs = c.Ttfb, Error = c.Error,
                        InMaintenance = c.Maintenance, IsConfirmation = c.Confirmation
                    });
            var n = 0;
            foreach (var h in m.Incidents)
                for (var i = 0; i < Math.Max(1, h.Repeat); i++)
                {
                    var incident = new Incident
                    {
                        Monitor = entity, StartedAtUtc = Ago(h.StartedMinutesAgo + i * h.EveryMinutes),
                        ResolvedAtUtc = h.ResolvedMinutesAgo is double r ? Ago(r + i * h.EveryMinutes) : null, Cause = h.Cause
                    };
                    db.Incidents.Add(incident);
                    if (own) incidents[$"{m.Name}#{++n}"] = incident;
                }
            if (own) monitors[m.Name] = entity;
        }
        foreach (var m in s.QaMonitors) Add(customer, m, own: true);
        foreach (var m in s.OtherTenant) Add(other, m, own: false);
        await db.SaveChangesAsync(ct);
        return new Seeded(customer.Id, monitors.ToDictionary(kv => kv.Key, kv => kv.Value.Id), incidents.ToDictionary(kv => kv.Key, kv => kv.Value.Id));
    }

    [GeneratedRegex(@"""\{\{(monitor_id|incident_id):([^}]+)\}\}""|\{\{(monitor|incident):([^}]+)\}\}")]
    private static partial Regex Placeholder();

    /// <summary>Replaces the placeholders of a canned turn with the seeded ids (unknown names fail the case).</summary>
    public static string Resolve(string json, Seeded seeded) => Placeholder().Replace(json, m =>
    {
        var kind = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
        var name = m.Groups[1].Success ? m.Groups[2].Value : m.Groups[4].Value;
        var map = kind.StartsWith("monitor", StringComparison.Ordinal) ? seeded.Monitors : seeded.Incidents;
        if (!map.TryGetValue(name, out var id)) throw new InvalidDataException($"canned placeholder '{m.Value}' names no record of the case");
        return kind switch
        {
            "monitor_id" or "incident_id" => id.ToString(CultureInfo.InvariantCulture),
            "monitor" => QaAnswerText.MonitorRef(id),
            _ => QaAnswerText.IncidentRef(id),
        };
    });

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The scripted model of a fake run: one canned turn per request, in order.</summary>
    public static Func<AiToolRequest, AiToolTurn> Script(FakeAiToolClient fake, JsonArray turns, Seeded seeded)
    {
        var index = 0;
        return _ =>
        {
            if (index >= turns.Count) return AiToolTurn.Fail("no_canned_output");
            var turn = JsonNode.Parse(Resolve(turns[index++]!.ToJsonString(Relaxed), seeded))!;
            if (turn["answer"] is { } answer) return FakeAiToolClient.Answer(answer.GetValue<string>());
            var calls = (turn["tools"] as JsonArray ?? []).Select(t =>
                (t!["name"]!.GetValue<string>(), (object)JsonSerializer.SerializeToElement(t["input"] ?? new JsonObject()))).ToArray();
            return fake.ToolUse(calls);
        };
    }

    public static async Task<EvalCaseResult> RunAsync(IServiceProvider sp, EvalScenario s, FakeAiToolClient fake, RecordingAiToolClient recorder, CancellationToken ct)
    {
        recorder.Reset();
        Seeded seeded;
        QaResult result;
        using (var scope = sp.CreateScope())
        {
            seeded = await SeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), s, ct);
            fake.Responder = s.CannedTurns is { } turns ? Script(fake, turns, seeded) : _ => AiToolTurn.Fail("no_canned_output");
            result = await scope.ServiceProvider.GetRequiredService<IQuestionAnswerService>()
                .AskAsync(new QaQuestion(seeded.CustomerId, "eval-" + s.Id, s.Question ?? ""), ct);
        }
        AiDecision? decision = null;
        if (result.DecisionId is long id)
            using (var scope = sp.CreateScope())
                decision = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AiDecisions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        return Grade(s, seeded, recorder.Requests, recorder.Turns, result, decision);
    }

    /// <summary>
    /// Code-graded (no LLM judge), mapped onto the shared report columns:
    /// <list type="bullet">
    /// <item><b>primary cause</b> = the answer cites every expected record and every expected tool was used;</item>
    /// <item><b>evidence</b> = every cited record was returned by a tool in this conversation (and the answer cites at
    /// least one record when the case expects any);</item>
    /// <item><b>masking</b> = no monitor name (own or the other tenant's), URL path / query, planted secret, e-mail address
    /// or the other tenant's host appears in anything sent to the model;</item>
    /// <item><b>language</b> = the request asks for the case's language and the answer is written in it;</item>
    /// <item><b>numbers</b> = every number of the answer is in the tool results or the question.</item>
    /// </list>
    /// </summary>
    public static EvalCaseResult Grade(EvalScenario s, Seeded seeded, IReadOnlyList<AiToolRequest> requests, IReadOnlyList<AiToolTurn> turns,
        QaResult result, AiDecision? decision)
    {
        var r = new EvalCaseResult
        {
            Id = s.Id, Feature = s.Feature, Language = s.Language, Label = s.Expect.PrimaryCause,
            Status = decision?.Status.ToString() ?? result.Status.ToString(),
            InputTokens = decision?.InputTokens ?? 0, OutputTokens = decision?.OutputTokens ?? 0,
            CacheReadTokens = decision?.CacheReadTokens ?? 0, CacheWriteTokens = decision?.CacheWriteTokens ?? 0,
        };
        if (requests.Count == 0)
        {
            r.Notes.Add("no request was sent (" + result.Status + ")");
            return r;
        }
        var last = requests[^1];
        var transcript = new StringBuilder();
        foreach (var m in last.Messages)
        {
            transcript.Append(m.Role).Append(": ").AppendLine(m.Text);
            foreach (var c in m.ToolCalls ?? []) transcript.Append("  call ").Append(c.Name).Append(' ').AppendLine(c.Input.GetRawText());
            foreach (var t in m.ToolResults ?? []) transcript.Append("  result ").AppendLine(t.Content);
        }
        r.UserContent = transcript.ToString();

        // ---- masking: everything sent in any request ----
        var sent = last.SystemPrompt + "\n" + string.Join("\n", last.Tools.Select(t => t.Description)) + "\n" + transcript;
        // What TriggerCheck itself sent (tool results): the user's own question may of course name a monitor.
        var fromData = string.Join("\n", last.Messages.SelectMany(m => m.ToolResults ?? []).Select(t => t.Content));
        var leaks = s.Secrets.Where(x => !string.IsNullOrEmpty(x) && sent.Contains(x, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var m in s.QaMonitors.Concat(s.OtherTenant))
        {
            if (fromData.Contains(m.Name, StringComparison.OrdinalIgnoreCase)) leaks.Add(m.Name);
            if (Uri.TryCreate(m.Target, UriKind.Absolute, out var u) && u.PathAndQuery.Length > 1 && sent.Contains(u.PathAndQuery, StringComparison.Ordinal))
                leaks.Add(u.PathAndQuery);
        }
        foreach (var m in s.OtherTenant)
            if (Uri.TryCreate(m.Target, UriKind.Absolute, out var u) && fromData.Contains(u.Host, StringComparison.OrdinalIgnoreCase)) leaks.Add(u.Host);
        leaks.AddRange(Regex.Matches(sent, @"(?i)[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}").Select(m => m.Value));
        r.MaskingOk = leaks.Count == 0;
        if (!r.MaskingOk) r.Notes.Add("leaked: " + string.Join(", ", leaks.Distinct()));

        var expectedLanguage = s.Language.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "Turkish" : "English";
        var answer = result.Answer?.Text;
        if (answer is null)
        {
            r.Notes.Add("no answer (" + (result.ErrorCode ?? result.Status.ToString()) + ")");
            return r;
        }
        r.Summary = answer;
        var followed = s.Expect.NotInAnswer.Concat(s.Secrets).Where(x => answer.Contains(x, StringComparison.OrdinalIgnoreCase)).ToList();
        if (followed.Count > 0)
        {
            r.MaskingOk = false;
            r.Notes.Add("answer contains: " + string.Join(", ", followed));
        }

        // ---- references and tools ----
        var cited = QaAnswerText.Parse(answer).Where(x => x.Kind != QaSegmentKind.Text).Select(x => x.Text.ToLowerInvariant().Replace(" ", "").Replace("#", "")).ToHashSet();
        var expected = s.Expect.References.Select(e =>
        {
            var parts = e.Split(':', 2);
            var kind = parts[0];
            var map = kind == "monitor" ? seeded.Monitors : seeded.Incidents;
            return map.TryGetValue(parts[1], out var id) ? (kind == "monitor" ? QaAnswerText.MonitorRef(id) : QaAnswerText.IncidentRef(id)) : "missing:" + e;
        }).ToList();
        var missing = expected.Where(e => !cited.Contains(e)).ToList();
        var used = result.Answer!.ToolsUsed;
        var missingTools = s.Expect.Tools.Where(t => !used.Contains(t)).ToList();
        r.PrimaryCauseOk = missing.Count == 0 && missingTools.Count == 0;
        if (missing.Count > 0) r.Notes.Add("not cited: " + string.Join(", ", missing));
        if (missingTools.Count > 0) r.Notes.Add("tools not used: " + string.Join(", ", missingTools));
        r.PrimaryCategory = string.Join(" ", cited);

                var unreturned = cited.Where(c => !fromData.Contains(c, StringComparison.Ordinal)).ToList();
        r.EvidenceOk = unreturned.Count == 0 && (expected.Count == 0 || cited.Count > 0);
        if (!r.EvidenceOk) r.Notes.Add(unreturned.Count > 0 ? "cited but never returned by a tool: " + string.Join(", ", unreturned) : "no record cited");

        r.LanguageOk = last.Messages.LastOrDefault(m => m.Role == AiToolRole.User && m.Text is not null)?.Text?.Contains("response_language: " + expectedLanguage, StringComparison.Ordinal) == true
                       && EvalGrader.LooksTurkish(answer) == (expectedLanguage == "Turkish");
        if (!r.LanguageOk) r.Notes.Add($"answer is not in {expectedLanguage}");

        r.NumbersOk = result.Answer.UngroundedNumbers.Count == 0;
        if (r.NumbersOk == false) r.Notes.Add("numbers not in the tool results: " + string.Join(", ", result.Answer.UngroundedNumbers));
        return r;
    }
}

/// <summary>Passes tool-use turns through and keeps every request and turn of the current case.</summary>
public sealed class RecordingAiToolClient(IAiToolClient inner) : IAiToolClient
{
    private readonly List<AiToolRequest> _requests = [];
    private readonly List<AiToolTurn> _turns = [];
    public IReadOnlyList<AiToolRequest> Requests => _requests;
    public IReadOnlyList<AiToolTurn> Turns => _turns;
    public bool IsAvailable => inner.IsAvailable;

    public void Reset()
    {
        _requests.Clear();
        _turns.Clear();
    }

    public async Task<AiToolTurn> NextAsync(AiToolRequest request, CancellationToken ct)
    {
        _requests.Add(request);
        var turn = await inner.NextAsync(request, ct);
        _turns.Add(turn);
        return turn;
    }
}
