using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Evals;

/// <summary>
/// One labelled, synthetic eval case (evals/cases/&lt;feature&gt;/*.json). Times are minutes before
/// <see cref="EvalHarness.Now"/>; the harness stores the scenario in a scratch database and builds the model input with
/// the production context provider, so the eval measures exactly what production would send. No real customer data.
/// </summary>
public sealed class EvalScenario
{
    public string Id { get; set; } = "";
    public string Feature { get; set; } = AiFeatures.IncidentInterpretation;
    public string Language { get; set; } = "en";
    public string Description { get; set; } = "";
    public EvalExpectation Expect { get; set; } = new();
    public EvalMonitor Monitor { get; set; } = new();
    /// <summary>AI-2 subject. Required for IncidentInterpretation.</summary>
    public EvalIncident? Incident { get; set; }
    public List<EvalCheck> Checks { get; set; } = [];
    /// <summary>Incident history of the monitor (AI-5 rule input); each entry may repeat going back in time.</summary>
    public List<EvalIncident> Incidents { get; set; } = [];
    public List<EvalMonitor> SameHost { get; set; } = [];
    /// <summary>AI-1 signal: the AI-3 subject, or context for AI-2.</summary>
    public EvalAnomaly? Anomaly { get; set; }
    public List<EvalPeriod> Maintenance { get; set; } = [];
    public double? BaselineMedianMs { get; set; }
    /// <summary>AI-6: the customer's monitors, incidents and checks of the report month and the month before.</summary>
    public EvalNarrative? Narrative { get; set; }
    /// <summary>Planted strings (credentials, URL paths, e-mail addresses) that must never reach the model.</summary>
    public List<string> Secrets { get; set; } = [];
    /// <summary>Filled by the harness (AI-6): monitor name → id of the seeded monitors.</summary>
    [JsonIgnore]
    public Dictionary<string, int> SeededMonitorIds { get; } = new();
    /// <summary>AI-4 (CorrelationGrouping): the incidents around the outage. Required for that feature.</summary>
    public EvalGroup? Group { get; set; }
    /// <summary>AI-9: the text the customer types into the monitor setup assistant. Required for MonitorSetupAssistant.</summary>
    public string? Request { get; set; }
    /// <summary>AI-9: the plan's minimum check interval (default 15 s), to label the clamping.</summary>
    public int? PlanMinIntervalSeconds { get; set; }
    /// <summary>Model answer used in fake mode (CI): a well-formed, correct interpretation for the case.</summary>
    public JsonNode? Canned { get; set; }
    /// <summary>AI-7 (QuestionAnswer): the question, the customer's monitors, another tenant's monitors and the scripted turns (see <see cref="EvalQa"/>).</summary>
    public string? Question { get; set; }
    public List<EvalQaMonitor> QaMonitors { get; set; } = [];
    public List<EvalQaMonitor> OtherTenant { get; set; } = [];
    public JsonArray? CannedTurns { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Every *.json below <paramref name="casesDir"/> (one folder per feature), ordered by path.</summary>
    public static List<EvalScenario> LoadAll(string casesDir)
    {
        var result = new List<EvalScenario>();
        foreach (var file in Directory.EnumerateFiles(casesDir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var s = JsonSerializer.Deserialize<EvalScenario>(File.ReadAllText(file), Json)
                ?? throw new InvalidDataException($"Empty case file {file}");
            if (string.IsNullOrWhiteSpace(s.Id)) s.Id = Path.GetFileNameWithoutExtension(file);
            if (AiFeatures.Find(s.Feature) is null) throw new InvalidDataException($"{file}: unknown feature '{s.Feature}'");
            if (s.Feature == AiFeatures.IncidentInterpretation && s.Incident is null) throw new InvalidDataException($"{file}: incident missing");
            if (s.Feature == AiFeatures.SlowdownExplanation && s.Anomaly is null) throw new InvalidDataException($"{file}: anomaly missing");
            if (s.Feature == AiFeatures.QuestionAnswer && (string.IsNullOrWhiteSpace(s.Question) || s.QaMonitors.Count == 0)) throw new InvalidDataException($"{file}: question or qaMonitors missing");
            if (s.Feature == AiFeatures.MonitorSetupAssistant && string.IsNullOrWhiteSpace(s.Request)) throw new InvalidDataException($"{file}: request missing");
            if (s.Feature == AiFeatures.CorrelationGrouping && s.Group is null) throw new InvalidDataException($"{file}: group missing");
            if (s.Feature == AiFeatures.NarrativeReport)
            {
                if (s.Narrative is not { Monitors.Count: > 0 }) throw new InvalidDataException($"{file}: narrative monitors missing");
                var names = s.Narrative.Monitors.Select(m => m.Name).ToHashSet();
                if (s.Expect.FocusMonitors.FirstOrDefault(n => !names.Contains(n)) is { } unknown) throw new InvalidDataException($"{file}: unknown focus monitor '{unknown}'");
            }
            else if (!EvalGrader.Labels.ContainsKey(s.Expect.PrimaryCause)) throw new InvalidDataException($"{file}: unknown label '{s.Expect.PrimaryCause}'");
            result.Add(s);
        }
        if (result.Select(s => s.Id).Distinct().Count() != result.Count) throw new InvalidDataException("Duplicate case ids");
        return result;
    }

    /// <summary>Finds evals/cases by walking up from <paramref name="start"/> (the repo root or any folder below it).</summary>
    public static string? FindCasesDir(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "evals", "cases");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}

public sealed class EvalExpectation
{
    /// <summary>Label of the primary cause; mapped to the accepted cause categories by <see cref="EvalGrader.Labels"/>.</summary>
    public string PrimaryCause { get; set; } = "";
    /// <summary>AI-3: the summary must say that there is no outage yet.</summary>
    public bool NoOutageStatement { get; set; }
    /// <summary>AI-6: monitors (by name) that "what happened" or "what to watch" must reference by their label.</summary>
    public List<string> FocusMonitors { get; set; } = [];
    /// <summary>AI-6: the true figures of monitors, for the grader's independent claim check (not the production engine).</summary>
    public List<EvalMonitorFacts> MonitorFacts { get; set; } = [];
    /// <summary>AI-7: records the answer must cite ("monitor:Name", "incident:Name#1") and tools it must have used.</summary>
    public List<string> References { get; set; } = [];
    public List<string> Tools { get; set; } = [];
    /// <summary>AI-7: text the answer must not contain (e.g. a link planted in an error text).</summary>
    public List<string> NotInAnswer { get; set; } = [];
    /// <summary>
    /// AI-9: expected values of the pre-filled form (property names of MonitorSetupFields in camelCase, enums as names,
    /// e.g. "type": "Keyword", "intervalSeconds": 60). Fields not listed are not graded.
    /// </summary>
    public Dictionary<string, JsonElement>? Fields { get; set; }
    /// <summary>AI-9: server-side adjustments that must be reported (MonitorSetupNoteKind names).</summary>
    public List<string> Notes { get; set; } = [];
    /// <summary>AI-4: monitor names of the candidates that belong to the group (the model must recommend exactly these).</summary>
    public List<string> Keep { get; set; } = [];
    /// <summary>AI-4: monitor names of the candidates that must be left out (they keep their own notifications).</summary>
    public List<string> LeaveOut { get; set; } = [];
}

/// <summary>Expected figures of one monitor in the report period (computed by hand from the case data).</summary>
public sealed class EvalMonitorFacts
{
    public string Name { get; set; } = "";
    public double? UptimePercent { get; set; }
    public double? SlaTarget { get; set; }
    public bool? SlaMet { get; set; }
    public int? Incidents { get; set; }
}

/// <summary>
/// AI-6 case data. The report month is the month before <see cref="EvalHarness.Now"/> (February 2026, UTC); "previous"
/// incidents fall into January. Days are 1-based days of the month.
/// </summary>
public sealed class EvalNarrative
{
    public List<EvalNarrativeMonitor> Monitors { get; set; } = [];
}

public sealed class EvalNarrativeMonitor
{
    public string Name { get; set; } = "";
    public MonitorType Type { get; set; } = MonitorType.Http;
    public string Target { get; set; } = "https://shop.example.test/health";
    public double? SlaTarget { get; set; }
    /// <summary>Negative: created during the report month.</summary>
    public double CreatedDaysBeforePeriod { get; set; } = 90;
    public List<EvalNarrativeIncident> Incidents { get; set; } = [];
    /// <summary>Response time of the daily successful check in the report month / the month before (null: no checks).</summary>
    public int? Ms { get; set; } = 200;
    public int? PreviousMs { get; set; } = 200;
    /// <summary>Certificate expiry, days after the end of the report month.</summary>
    public int? CertExpiresDaysAfterPeriod { get; set; }
    /// <summary>Slowdown signals (AI-1) that started in the report month.</summary>
    public int Slowdowns { get; set; }
}

public sealed class EvalNarrativeIncident
{
    public int Day { get; set; } = 1;
    public int Hour { get; set; }
    public double Minutes { get; set; } = 30;
    public string? Cause { get; set; }
    /// <summary>In the month before the report month.</summary>
    public bool Previous { get; set; }
}

public sealed class EvalMonitor
{
    public string Name { get; set; } = "Shop API";
    public MonitorType Type { get; set; } = MonitorType.Http;
    public string Target { get; set; } = "https://shop.example.test/health";
    public int? Port { get; set; }
    public string? Keyword { get; set; }
    public string ExpectedStatusCodes { get; set; } = "200-299";
    public int IntervalSeconds { get; set; } = 60;
    public int TimeoutSeconds { get; set; } = 10;
    public int FailureThreshold { get; set; } = 2;
    public MonitorStatus Status { get; set; } = MonitorStatus.Down;
    public int? CertExpiresInDays { get; set; }
    public string? CertError { get; set; }
    /// <summary>Same-host monitors only: it has an open incident.</summary>
    public bool OpenIncident { get; set; }
    public double CreatedDaysAgo { get; set; } = 120;
}

public sealed class EvalIncident
{
    public double StartedMinutesAgo { get; set; }
    public double? ResolvedMinutesAgo { get; set; }
    public string? Cause { get; set; }
    /// <summary>History entries only (<see cref="EvalScenario.Incidents"/>): this many incidents, <see cref="EveryMinutes"/> apart.</summary>
    public int Repeat { get; set; } = 1;
    public double EveryMinutes { get; set; } = 60;
}

/// <summary>One check, or <see cref="Repeat"/> checks <see cref="EveryMinutes"/> apart going back in time.</summary>
public sealed class EvalCheck
{
    public double MinutesAgo { get; set; }
    public int Repeat { get; set; } = 1;
    public double EveryMinutes { get; set; } = 1;
    public bool Up { get; set; }
    public int? Status { get; set; }
    public int Ms { get; set; }
    public int? Dns { get; set; }
    public int? Connect { get; set; }
    public int? Tls { get; set; }
    public int? Ttfb { get; set; }
    public string? Error { get; set; }
    public bool Maintenance { get; set; }
    public bool Confirmation { get; set; }
}

public sealed class EvalAnomaly
{
    public double StartedMinutesAgo { get; set; }
    public double? EndedMinutesAgo { get; set; }
    public double Score { get; set; } = 6;
    public List<EvalAnomalyPoint> Points { get; set; } = [];
}

public sealed class EvalAnomalyPoint
{
    public double MinutesAgo { get; set; }
    public double Ms { get; set; }
    public double MedianMs { get; set; }
}

public sealed class EvalPeriod
{
    public double StartedMinutesAgo { get; set; }
    public double EndedMinutesAgo { get; set; }
}
