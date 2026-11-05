using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Insights;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure;
using TriggerCheck.Infrastructure.Data;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Evals;

/// <summary>AI-12 case data: the platform's customers (each on its own plan) with their monitors and usage.</summary>
public sealed class EvalInsights
{
    public List<EvalInsightCustomer> Customers { get; set; } = [];
}

public sealed class EvalInsightCustomer
{
    public string Name { get; set; } = "";
    public string ContactEmail { get; set; } = "ops@example.test";
    public string Plan { get; set; } = "Standard";
    public int MaxMonitors { get; set; } = 50;
    public int MinIntervalSeconds { get; set; } = 60;
    public int? MaxStatusPages { get; set; } = 3;
    public int MaxRegions { get; set; } = 1;
    public bool AllowsSmsVoice { get; set; }
    public int AiCallLimit { get; set; } = 100;
    public bool IsActive { get; set; } = true;
    public int StatusPages { get; set; }
    /// <summary>SMS / voice deliveries on the busiest day of the window.</summary>
    public int SmsPeak { get; set; }
    /// <summary>AI calls this month (reached the model).</summary>
    public int AiCalls { get; set; }
    /// <summary>Incidents of this customer acknowledged this many days ago (one each).</summary>
    public List<double> AcknowledgedDaysAgo { get; set; } = [];
    public List<EvalInsightMonitor> Monitors { get; set; } = [];
}

public sealed class EvalInsightMonitor
{
    public string Name { get; set; } = "Website";
    public string Target { get; set; } = "https://www.example.test/";
    public MonitorType Type { get; set; } = MonitorType.Http;
    public MonitorStatus Status { get; set; } = MonitorStatus.Up;
    /// <summary>Days since the status last changed (DOWN monitors: how long they have been down).</summary>
    public double StatusDays { get; set; } = 1;
    public int IntervalSeconds { get; set; } = 300;
    public bool Enabled { get; set; } = true;
    /// <summary>Remote regions besides the central one.</summary>
    public int RemoteRegions { get; set; }
    /// <summary>Incidents in the last days lasting an hour each, and flaps (resolved after two minutes).</summary>
    public int Incidents { get; set; }
    public int Flaps { get; set; }
    public int Alerts { get; set; }
    /// <summary>Copies of this monitor (name gets a suffix).</summary>
    public int Repeat { get; set; } = 1;
}

/// <summary>
/// AI-12 cases (SuperAdmin platform insights). Each case gets its own scratch database (the signals are platform-wide),
/// the production <see cref="PlatformInsights"/> rules compute the signals and the production
/// <see cref="InsightSummaryService"/> makes the call (prompt, labels, contract, platform budget). Graded, code only:
/// <list type="bullet">
/// <item><b>primary cause</b> (reported as such) = focus: every <c>expect.focusCustomers</c> entry is referenced by its
/// [[label]] in the validated summary;</item>
/// <item><b>evidence</b> = grounded: the raw model output passes the production contract with nothing removed, and an
/// independent grader check (not the production code) finds no digit outside {{placeholders}} / [[labels]], no
/// placeholder that was not offered and no label of a customer or monitor that was not sent (no invented
/// customers or numbers);</item>
/// <item><b>masking</b>: no customer or monitor name, contact e-mail or target host reaches the model;</item>
/// <item><b>language</b>: the request asks for the case's language and the summary is written in it.</item>
/// </list>
/// </summary>
public static partial class InsightsEval
{
    public const string Label = "insights_summary";

    public static async Task<EvalCaseResult> RunAsync(EvalScenario s, IAiClient client, EvalOptions options, CancellationToken ct)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"triggercheck-evals-insights-{Guid.NewGuid():N}.db");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={dbPath};Pooling=False",
            ["Ai:Enabled"] = "true",
            ["Ai:ApiKey"] = "",
            ["Ai:Insights:MinMinutesBetweenSummaries"] = "0",
        };
        if (options.Model is not null) settings["Ai:Model"] = options.Model;
        if (options.Effort is not null) settings["Ai:Effort"] = options.Effort;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(EvalHarness.Now));
        services.AddInfrastructure(config);
        services.AddAi(config);
        services.RemoveAll<IAiClient>();
        services.AddSingleton(client);
        try
        {
            await using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
            await SeedAsync(db, s, ct);
            var service = scope.ServiceProvider.GetRequiredService<InsightSummaryService>();
            var result = await service.GenerateAsync(s.Language, ct);
            var report = await scope.ServiceProvider.GetRequiredService<PlatformInsights>().ComputeAsync(ct);
            var input = InsightSummaryPrompt.Build(report, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value.Insights.MaxSummaryCustomers, s.Language);
            var recorder = client as RecordingAiClient;
            var names = await CustomerAndMonitorIdsAsync(db, ct);
            return Grade(s, recorder?.LastRequest, recorder?.LastResult, result, input, names);
        }
        finally
        {
            try { File.Delete(dbPath); } catch (IOException) { /* scratch file */ }
        }
    }

    private static async Task SeedAsync(AppDbContext db, EvalScenario s, CancellationToken ct)
    {
        var now = EvalHarness.Now;
        var planNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in s.Insights!.Customers)
        {
            // Each customer gets its own plan; a repeated plan name gets a suffix (the name is unique).
            var planName = spec.Plan;
            for (var n = 2; !planNames.Add(planName); n++) planName = spec.Plan + " " + (char)('A' + n - 1);
            var plan = new Plan
            {
                Name = planName, MinIntervalSeconds = spec.MinIntervalSeconds, MaxMonitors = spec.MaxMonitors,
                MaxStatusPages = spec.MaxStatusPages, MaxRegions = spec.MaxRegions, AllowsSms = spec.AllowsSmsVoice, AllowsVoiceCall = spec.AllowsSmsVoice,
                AiFeatures = AiFeatures.Format(AiFeatures.All.Select(f => f.Name)), AiMonthlyCallLimit = spec.AiCallLimit,
            };
            var customer = new Customer { Name = spec.Name, ContactEmail = spec.ContactEmail, Plan = plan, IsActive = spec.IsActive, AiEnabled = true };
            db.Customers.Add(customer);
            await db.SaveChangesAsync(ct);
            s.SeededCustomerIds[spec.Name] = customer.Id;

            var regions = new List<Region>();
            for (var i = 0; i < spec.Monitors.Max(m => (int?)m.RemoteRegions).GetValueOrDefault(); i++)
            {
                var r = new Region { Code = $"r{customer.Id}-{i}", Name = "Region", Prefix = $"p{customer.Id}x{i}", SecretHash = "x" };
                db.Regions.Add(r);
                regions.Add(r);
            }
            var first = (MonitorEntity?)null;
            foreach (var m in spec.Monitors)
                for (var k = 0; k < Math.Max(1, m.Repeat); k++)
                {
                    var e = new MonitorEntity
                    {
                        Customer = customer, Name = m.Repeat > 1 ? $"{m.Name} {k + 1}" : m.Name, Target = m.Target, Type = m.Type, Status = m.Status,
                        StatusChangedAtUtc = now.AddDays(-m.StatusDays), IntervalSeconds = m.IntervalSeconds, IsEnabled = m.Enabled, CreatedAtUtc = now.AddDays(-200),
                    };
                    db.Monitors.Add(e);
                    first ??= e;
                    foreach (var r in regions.Take(m.RemoteRegions)) db.MonitorRegionStates.Add(new MonitorRegionState { Monitor = e, Region = r });
                    for (var i = 0; i < m.Incidents; i++)
                        db.Incidents.Add(new Incident { Monitor = e, StartedAtUtc = now.AddHours(-2 - 3 * i), ResolvedAtUtc = now.AddHours(-1 - 3 * i) });
                    for (var i = 0; i < m.Flaps; i++)
                        db.Incidents.Add(new Incident { Monitor = e, StartedAtUtc = now.AddHours(-1.5 - 3 * i), ResolvedAtUtc = now.AddHours(-1.5 - 3 * i).AddMinutes(2) });
                    await db.SaveChangesAsync(ct);
                    for (var i = 0; i < m.Alerts; i++)
                        db.Notifications.Add(new Notification { MonitorId = e.Id, SentAtUtc = now.AddHours(-1 - i), Subject = "DOWN", Success = true });
                }
            if (first is not null)
                foreach (var days in spec.AcknowledgedDaysAgo)
                    db.Incidents.Add(new Incident { Monitor = first, StartedAtUtc = now.AddDays(-days).AddHours(-1), ResolvedAtUtc = now.AddDays(-days), AcknowledgedAtUtc = now.AddDays(-days) });
            for (var i = 0; i < spec.StatusPages; i++)
                db.StatusPages.Add(new StatusPage { Customer = customer, Title = "Status", Slug = $"s-{customer.Id}-{i}" });
            if (spec.SmsPeak > 0) db.SmsVoiceUsage.Add(new SmsVoiceUsage { CustomerId = customer.Id, DayUtc = now.Date.AddDays(-1), Count = spec.SmsPeak });
            for (var i = 0; i < spec.AiCalls; i++)
                db.AiDecisions.Add(new AiDecision
                {
                    CustomerId = customer.Id, Feature = AiFeatures.IncidentInterpretation, SubjectType = "Incident", SubjectId = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Model = AiOptions.DefaultModel, Status = AiDecisionStatus.Succeeded, InputTokens = 100, OutputTokens = 10, CreatedAtUtc = now.AddDays(-1),
                });
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task<(HashSet<int> Customers, HashSet<int> Monitors)> CustomerAndMonitorIdsAsync(AppDbContext db, CancellationToken ct) =>
        ((await db.Customers.Select(c => c.Id).ToListAsync(ct)).ToHashSet(), (await db.Monitors.Select(m => m.Id).ToListAsync(ct)).ToHashSet());

    [GeneratedRegex(@"(?i)[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}")]
    private static partial Regex Email();

    public static EvalCaseResult Grade(EvalScenario s, AiCallRequest? request, AiCallResult? raw, InsightSummaryResult result, InsightSummaryInput input,
        (HashSet<int> Customers, HashSet<int> Monitors) existing)
    {
        var r = new EvalCaseResult
        {
            Id = s.Id, Feature = s.Feature, Language = s.Language, Label = s.Expect.PrimaryCause,
            Status = result.Decision?.Status.ToString() ?? result.Status.ToString(),
            InputTokens = raw?.InputTokens ?? 0, OutputTokens = raw?.OutputTokens ?? 0,
            CacheReadTokens = raw?.CacheReadTokens ?? 0, CacheWriteTokens = raw?.CacheWriteTokens ?? 0,
            UserContent = request?.UserContent,
        };
        if (request is null)
        {
            r.Notes.Add("no request was sent (" + result.Status + ")");
            return r;
        }

        // ---- masking: names, contacts and targets never reach the model ----
        var sent = request.SystemPrompt + "\n" + request.UserContent;
        var leaks = new List<string>();
        foreach (var c in s.Insights!.Customers)
        {
            if (sent.Contains(c.Name, StringComparison.OrdinalIgnoreCase)) leaks.Add(c.Name);
            foreach (var m in c.Monitors)
            {
                if (sent.Contains(m.Name, StringComparison.OrdinalIgnoreCase)) leaks.Add(m.Name);
                if (Uri.TryCreate(m.Target, UriKind.Absolute, out var u) && sent.Contains(u.Host, StringComparison.OrdinalIgnoreCase)) leaks.Add(u.Host);
            }
        }
        leaks.AddRange(s.Secrets.Where(x => x.Length > 0 && sent.Contains(x, StringComparison.OrdinalIgnoreCase)));
        leaks.AddRange(Email().Matches(sent).Select(m => m.Value));
        r.MaskingOk = leaks.Count == 0;
        if (!r.MaskingOk) r.Notes.Add("leaked: " + string.Join(", ", leaks.Distinct()));

        // ---- evidence: production contract (nothing removed) + independent grounding check of the raw output ----
        var rawTexts = RawTexts(raw?.Json);
        var contract = new InsightSummaryContract(input).Check(raw?.Json);
        var output = InsightSummaryOutput.TryParse(contract.Json);
        var wrong = WrongClaims(rawTexts, input, existing);
        r.NumbersOk = wrong.Count == 0;
        r.EvidenceOk = output is { RemovedPoints: 0 } && wrong.Count == 0;
        if (output is null) r.Notes.Add("contract rejected the output (" + (contract.ErrorCode ?? raw?.ErrorCode ?? result.Status.ToString()) + ")");
        else if (output.RemovedPoints > 0) r.Notes.Add($"{output.RemovedPoints} point(s) removed by the contract");
        r.Notes.AddRange(wrong);

        // ---- focus: the expected customers are referenced by their labels ----
        var texts = output is null ? [] : new[] { output.Headline }.Concat(output.Points).ToList();
        r.Summary = string.Join(" | ", texts);
        var referenced = texts.SelectMany(t => InsightRenderer.Reference().Matches(t).Select(InsightRenderer.LabelOf))
            .Where(input.Labels.ContainsKey).Select(l => input.Labels[l]).Where(l => l.Kind == InsightLabel.Customer).Select(l => l.Id).ToHashSet();
        var missing = new List<string>();
        foreach (var name in s.Expect.FocusCustomers)
            if (!s.SeededCustomerIds.TryGetValue(name, out var id) || !referenced.Contains(id)) missing.Add(name);
        r.PrimaryCategory = Label;
        r.PrimaryCauseOk = output is not null && missing.Count == 0;
        if (missing.Count > 0) r.Notes.Add("not referenced: " + string.Join(", ", missing));

        // ---- language ----
        var expectedLanguage = s.Language.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "Turkish" : "English";
        var joined = string.Join(" ", rawTexts);
        r.LanguageOk = request.UserContent.StartsWith("response_language: " + expectedLanguage, StringComparison.Ordinal)
                       && joined.Length > 0 && EvalGrader.LooksTurkish(joined) == (expectedLanguage == "Turkish");
        if (!r.LanguageOk) r.Notes.Add($"summary is not in {expectedLanguage}");
        return r;
    }

    private static List<string> RawTexts(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("headline", out var h) && h.ValueKind == JsonValueKind.String) list.Add(h.GetString()!);
            if (doc.RootElement.TryGetProperty("points", out var p) && p.ValueKind == JsonValueKind.Array)
                list.AddRange(p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"\{\{\s*([^{}\s]+)\s*\}\}")]
    private static partial Regex AnyPlaceholder();

    [GeneratedRegex(@"\[\[\s*([^\[\]\s]+)\s*\]\]")]
    private static partial Regex AnyLabel();

    /// <summary>
    /// Independent of the production contract: digits outside placeholders and labels (invented numbers), placeholders
    /// that were not offered, labels that were not sent or point to records that do not exist (invented customers).
    /// </summary>
    public static List<string> WrongClaims(IReadOnlyList<string> texts, InsightSummaryInput input, (HashSet<int> Customers, HashSet<int> Monitors) existing)
    {
        var wrong = new List<string>();
        foreach (var t in texts)
        {
            foreach (Match p in AnyPlaceholder().Matches(t))
                if (!input.Values.ContainsKey(p.Groups[1].Value)) wrong.Add("invented value " + p.Value);
            foreach (Match l in AnyLabel().Matches(t))
            {
                var label = l.Groups[1].Value.ToUpperInvariant();
                if (!input.Labels.TryGetValue(label, out var target)) wrong.Add("invented record " + l.Value);
                else if (!(target.Kind == InsightLabel.Customer ? existing.Customers : existing.Monitors).Contains(target.Id)) wrong.Add("unknown record " + l.Value);
            }
            var free = AnyLabel().Replace(AnyPlaceholder().Replace(t, " "), " ");
            if (free.Any(char.IsDigit)) wrong.Add("number outside placeholders: " + t);
        }
        return wrong;
    }
}
