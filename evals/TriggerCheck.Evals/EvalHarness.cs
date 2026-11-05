using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Ai.Features;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure;
using TriggerCheck.Infrastructure.Data;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Evals;

public sealed record EvalOptions
{
    /// <summary>True: the real Claude API (needs a key; costs money). False: FakeAiClient answering with each case's canned output.</summary>
    public bool Real { get; init; }
    public string? ApiKey { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    /// <summary>Only cases of this feature (null = all).</summary>
    public string? Feature { get; init; }
    /// <summary>Only cases whose id contains this text (null = all).</summary>
    public string? Filter { get; init; }
    public double Threshold { get; init; } = EvalGrader.DefaultThreshold;
}

/// <summary>
/// Runs eval cases through the production path: scenario → scratch SQLite database → the feature's registered
/// <see cref="IAiContextProvider"/> → <see cref="IAiInterpreter"/> (prompt, schema, budget, validation) → client. The client
/// is wrapped so the grader sees the exact request (masking check) and the raw model output (before validation).
/// </summary>
public static class EvalHarness
{
    /// <summary>Fixed clock: scenario times are relative to it, so prompts are byte-identical between runs.</summary>
    public static readonly DateTime Now = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    public static async Task<EvalReport> RunAsync(IReadOnlyList<EvalScenario> all, EvalOptions options, CancellationToken ct = default)
    {
        var cases = all.Where(s => (options.Feature is null || s.Feature == options.Feature)
                                   && (options.Filter is null || s.Id.Contains(options.Filter, StringComparison.OrdinalIgnoreCase))).ToList();

        var dbPath = Path.Combine(Path.GetTempPath(), $"triggercheck-evals-{Guid.NewGuid():N}.db");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={dbPath};Pooling=False",
            ["Ai:Enabled"] = "true",
            ["Ai:RetryCooldownMinutes"] = "0",
            ["Ai:Qa:PerUserPerMinute"] = "1000",
            ["Ai:ApiKey"] = options.Real ? options.ApiKey : "",
        };
        if (options.Model is not null) settings["Ai:Model"] = options.Model;
        if (options.Effort is not null) settings["Ai:Effort"] = options.Effort;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var fake = new FakeAiClient();
        EvalScenario? current = null;
        fake.Responder = _ => current?.Canned is null
            ? AiCallResult.Fail("no_canned_output")
            : FakeAiClient.Success(current.Canned.ToJsonString());

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddInfrastructure(config);
        services.AddAi(config);
        services.RemoveAll<IAiClient>();
        services.AddSingleton<RecordingAiClient>(sp => new RecordingAiClient(options.Real
            ? ActivatorUtilities.CreateInstance<AnthropicAiClient>(sp)
            : fake));
        services.AddSingleton<IAiClient>(sp => sp.GetRequiredService<RecordingAiClient>());
        // AI-7: the tool-use client, scripted by the case's cannedTurns in fake mode.
        var fakeTools = new FakeAiToolClient();
        services.RemoveAll<IAiToolClient>();
        services.AddSingleton<RecordingAiToolClient>(sp => new RecordingAiToolClient(options.Real
            ? new AnthropicAiToolClient(sp.GetRequiredService<IOptions<AiOptions>>(), sp.GetRequiredService<ILogger<AnthropicAiToolClient>>())
            : fakeTools));
        services.AddSingleton<IAiToolClient>(sp => sp.GetRequiredService<RecordingAiToolClient>());
        // The production providers, wrapped so the grader sees their AiPrompt (evidence keys, ref rule). Registered last: they win.
        var prompts = new PromptRecorder();
        services.AddScoped<IAiContextProvider>(sp => new RecordingContextProvider(ActivatorUtilities.CreateInstance<IncidentInterpretationProvider>(sp), prompts));
        services.AddScoped<IAiContextProvider>(sp => new RecordingContextProvider(ActivatorUtilities.CreateInstance<SlowdownExplanationProvider>(sp), prompts));
        services.AddScoped<IAiContextProvider>(sp => new RecordingContextProvider(ActivatorUtilities.CreateInstance<SettingsSuggestionProvider>(sp), prompts));

        var report = new EvalReport
        {
            Mode = options.Real ? "real" : "fake",
            Threshold = options.Threshold,
            StartedAtUtc = DateTime.UtcNow,
        };
        await using (var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }))
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            using (var scope = sp.CreateScope())
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync(ct);
            var recorder = sp.GetRequiredService<RecordingAiClient>();

            foreach (var scenario in cases)
            {
                if (scenario.Feature == AiFeatures.QuestionAnswer)
                {
                    var qa = await EvalQa.RunAsync(sp, scenario, fakeTools, sp.GetRequiredService<RecordingAiToolClient>(), ct);
                    qa.Model = o.ModelFor(scenario.Feature);
                    qa.Effort = o.Features.TryGetValue(scenario.Feature, out var qf) && qf.Effort is { } e ? e : o.Qa.Effort;
                    qa.CostUsd = decimal.Round(o.PriceFor(qa.Model).Estimate(qa.InputTokens, qa.OutputTokens, qa.CacheReadTokens, qa.CacheWriteTokens), 6);
                    report.Cases.Add(qa);
                    continue;
                }
                current = scenario;
                recorder.Reset();
                prompts.Last = null;
                if (scenario.Feature == AiFeatures.MonitorSetupAssistant)
                {
                    // AI-9 is interactive (no job / provider): the production assistant answers the case's request.
                    using var setupScope = sp.CreateScope();
                    var (setup, setupDecision) = await MonitorSetupEval.RunAsync(setupScope.ServiceProvider, scenario, ct);
                    var graded = MonitorSetupEval.Grade(scenario, recorder.LastRequest, recorder.LastResult, setup, setupDecision);
                    graded.Model = o.ModelFor(scenario.Feature);
                    graded.Effort = o.EffortFor(scenario.Feature);
                    graded.CostUsd = setupDecision?.CostEstimate ?? 0;
                    report.Cases.Add(graded);
                    continue;
                }
                AiDecision? decision = null;
                using (var scope = sp.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var job = await SeedAsync(db, scenario, ct);
                    if (job is not null) decision = await scope.ServiceProvider.GetRequiredService<IAiInterpreter>().RunAsync(job, ct);
                }
                var result = EvalGrader.Grade(scenario, recorder.LastRequest, recorder.LastResult, decision, prompts.Last);
                result.Model = o.ModelFor(scenario.Feature);
                result.Effort = o.EffortFor(scenario.Feature);
                result.CostUsd = decision?.CostEstimate ?? 0;
                report.Cases.Add(result);
            }
        }
        try { File.Delete(dbPath); } catch (IOException) { /* scratch file */ }
        report.FinishedAtUtc = DateTime.UtcNow;
        return report;
    }

    /// <summary>
    /// Stores the scenario for a new customer and returns the job production would queue for it. AI-5: the production
    /// suggestion job runs the rules on the stored history; null when they find nothing (the case then fails).
    /// </summary>
    public static async Task<AiJob?> SeedAsync(AppDbContext db, EvalScenario s, CancellationToken ct = default)
    {
        DateTime Ago(double minutes) => Now.AddMinutes(-minutes);
        var plan = new Plan
        {
            Name = "Eval-" + s.Id, MinIntervalSeconds = 15, MaxMonitors = 100,
            AiFeatures = AiFeatures.Format(AiFeatures.All.Select(f => f.Name)), AiMonthlyCallLimit = 1_000_000
        };
        var customer = new Customer { Name = "Eval " + s.Id, ContactEmail = "ops@example.test", Plan = plan, AiEnabled = true, AiLanguage = s.Language };
        db.Customers.Add(customer);

        MonitorEntity NewMonitor(EvalMonitor m) => new()
        {
            Customer = customer, Name = m.Name, Type = m.Type, Target = m.Target, Port = m.Port, Keyword = m.Keyword,
            ExpectedStatusCodes = m.ExpectedStatusCodes, IntervalSeconds = m.IntervalSeconds, TimeoutSeconds = m.TimeoutSeconds,
            FailureThreshold = m.FailureThreshold, Status = m.Status, CreatedAtUtc = Now.AddDays(-m.CreatedDaysAgo),
            CertExpiresAtUtc = m.CertExpiresInDays is int days ? Now.AddDays(days) : null,
            CertError = m.CertError,
            CertCheckedAtUtc = m.CertExpiresInDays is not null || m.CertError is not null ? Ago(1) : null,
        };

        var monitor = NewMonitor(s.Monitor);
        monitor.LastError = s.Incident?.Cause;
        monitor.StatusChangedAtUtc = s.Incident is { } inc ? Ago(inc.ResolvedMinutesAgo ?? inc.StartedMinutesAgo) : Ago(s.Monitor.CreatedDaysAgo * 1440);
        db.Monitors.Add(monitor);

        foreach (var c in s.Checks)
            for (var i = 0; i < Math.Max(1, c.Repeat); i++)
                db.CheckResults.Add(new CheckResult
                {
                    Monitor = monitor, CheckedAtUtc = Ago(c.MinutesAgo + i * c.EveryMinutes), IsUp = c.Up, StatusCode = c.Status,
                    ResponseTimeMs = c.Ms, DnsMs = c.Dns, ConnectMs = c.Connect, TlsMs = c.Tls, TtfbMs = c.Ttfb, Error = c.Error,
                    InMaintenance = c.Maintenance, IsConfirmation = c.Confirmation
                });

        Incident? incident = null;
        if (s.Incident is { } si)
        {
            incident = new Incident { Monitor = monitor, StartedAtUtc = Ago(si.StartedMinutesAgo), ResolvedAtUtc = si.ResolvedMinutesAgo is double r ? Ago(r) : null, Cause = si.Cause };
            db.Incidents.Add(incident);
        }

        foreach (var h in s.Incidents)
            for (var i = 0; i < Math.Max(1, h.Repeat); i++)
                db.Incidents.Add(new Incident
                {
                    Monitor = monitor, StartedAtUtc = Ago(h.StartedMinutesAgo + i * h.EveryMinutes),
                    ResolvedAtUtc = h.ResolvedMinutesAgo is double hr ? Ago(hr + i * h.EveryMinutes) : null, Cause = h.Cause
                });

        foreach (var other in s.SameHost)
        {
            var m = NewMonitor(other);
            db.Monitors.Add(m);
            if (other.OpenIncident) db.Incidents.Add(new Incident { Monitor = m, StartedAtUtc = Ago(s.Incident?.StartedMinutesAgo ?? 10), Cause = "Outage" });
        }

        foreach (var p in s.Maintenance)
            db.MaintenanceWindows.Add(new MaintenanceWindow { Customer = customer, Name = "Planned maintenance", StartsAtUtc = Ago(p.StartedMinutesAgo), EndsAtUtc = Ago(p.EndedMinutesAgo), AppliesToAllMonitors = true });

        await db.SaveChangesAsync(ct);

        if (s.BaselineMedianMs is double median)
            db.MonitorBaselines.Add(new MonitorBaseline { MonitorId = monitor.Id, CustomerId = customer.Id, Bucket = MonitorBaseline.GlobalBucket, MedianMs = median, MadMs = median * 0.1, SampleCount = 500, UpdatedAtUtc = Now });

        AnomalySignal? signal = null;
        if (s.Anomaly is { } a)
        {
            signal = new AnomalySignal
            {
                MonitorId = monitor.Id, CustomerId = customer.Id, Metric = "ResponseTimeMs", StartedAtUtc = Ago(a.StartedMinutesAgo),
                EndedAtUtc = a.EndedMinutesAgo is double e ? Ago(e) : null, Score = a.Score, UpdatedAtUtc = a.EndedMinutesAgo is double e2 ? Ago(e2) : Now,
                // Same shape as BaselineJob.Details.
                DetailsJson = JsonSerializer.Serialize(new
                {
                    anomalous = a.Points.Count,
                    points = a.Points.OrderByDescending(p => p.MinutesAgo).Select(p => new
                    {
                        at = Ago(p.MinutesAgo), ms = p.Ms, baselineMedianMs = p.MedianMs,
                        ratio = p.MedianMs > 0 ? Math.Round(p.Ms / p.MedianMs, 2) : 0, score = a.Score, Anomalous = true
                    })
                })
            };
            db.AnomalySignals.Add(signal);
        }
        await db.SaveChangesAsync(ct);

        if (s.Feature == AiFeatures.SettingsSuggestions)
        {
            var job = new SettingsSuggestionJob(db, new NullJobQueue(), new FakeAiClient { IsAvailable = false },
                Options.Create(new AiOptions()), new FixedTimeProvider(Now), NullLogger<SettingsSuggestionJob>.Instance);
            await job.RunAsync(ct);
            var suggestion = await db.SettingsSuggestions
                .Where(x => x.MonitorId == monitor.Id && x.Status == SettingsSuggestionStatus.Open).OrderBy(x => x.Kind)
                .FirstOrDefaultAsync(ct);
            return suggestion is null ? null
                : new AiJob(s.Feature, customer.Id, SettingsSuggestionProvider.SubjectType, suggestion.Id.ToString(CultureInfo.InvariantCulture), s.Language);
        }

        return s.Feature == AiFeatures.SlowdownExplanation
            ? new AiJob(s.Feature, customer.Id, AiSubjects.AnomalySignal, signal!.Id.ToString(CultureInfo.InvariantCulture), s.Language)
            : new AiJob(s.Feature, customer.Id, AiSubjects.Incident, incident!.Id.ToString(CultureInfo.InvariantCulture), s.Language);
    }
}

/// <summary>Passes calls through and keeps the last request and raw result for the grader.</summary>
public sealed class RecordingAiClient(IAiClient inner) : IAiClient
{
    public AiCallRequest? LastRequest { get; private set; }
    public AiCallResult? LastResult { get; private set; }
    public bool IsAvailable => inner.IsAvailable;

    public void Reset()
    {
        LastRequest = null;
        LastResult = null;
    }

    public async Task<AiCallResult> CompleteAsync(AiCallRequest request, CancellationToken ct)
    {
        LastRequest = request;
        LastResult = await inner.CompleteAsync(request, ct);
        return LastResult;
    }
}

/// <summary>The harness calls the interpreter itself; the suggestion job's own queueing is not needed.</summary>
public sealed class NullJobQueue : IAiJobQueue
{
    public bool TryEnqueue(AiJob job) => false;
}

public sealed class PromptRecorder
{
    public AiPrompt? Last { get; set; }
}

/// <summary>Delegates to a production context provider and keeps the prompt it built.</summary>
public sealed class RecordingContextProvider(IAiContextProvider inner, PromptRecorder recorder) : IAiContextProvider
{
    public string Feature => inner.Feature;

    public async Task<AiPrompt?> BuildAsync(AiJob job, CancellationToken ct)
    {
        var prompt = await inner.BuildAsync(job, ct);
        recorder.Last = prompt;
        return prompt;
    }
}

public sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
}

/// <summary>Raw model JSON as the grader sees it (before the L3 validation drops anything).</summary>
public static class RawOutput
{
    public static AiInterpretation? Parse(AiCallResult? result) =>
        result is { Outcome: AiCallOutcome.Succeeded } ? AiInterpretation.TryParse(result.Json) : null;
}
