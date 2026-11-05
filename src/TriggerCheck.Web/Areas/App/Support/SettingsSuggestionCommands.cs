using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Features;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Areas.App.Support;

/// <summary>One open AI-5 suggestion as the monitor details page shows it.</summary>
/// <param name="Ai">The AI explanation block (null when there is nothing to show: AI off, no decision yet and not pending).</param>
/// <param name="OneClick">Apply changes the setting directly (a concrete value exists); otherwise the admin edits the monitor.</param>
public sealed record SettingsSuggestionView(SettingsSuggestion Suggestion, IReadOnlyList<SuggestionFact> Facts, AiPanelModel? Ai, bool OneClick)
{
    public string Fact(string key) => SuggestionFact.Get(Facts, key) ?? "—";

    public DateTime? FactTime(string key) =>
        DateTime.TryParse(SuggestionFact.Get(Facts, key), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

/// <summary>The suggestions block of the monitor details page.</summary>
/// <param name="CanManage">CustomerAdmin: may apply / dismiss (viewers only see the suggestions).</param>
/// <param name="DismissDays">Ai:Suggestions:DismissDays: how long a dismissed kind stays hidden.</param>
public sealed record SettingsSuggestionsModel(int MonitorId, IReadOnlyList<SettingsSuggestionView> Items, bool CanManage, int DismissDays);

public enum SuggestionOutcome
{
    Applied,
    Dismissed,
    NotFound,
    /// <summary>Not open any more, or the setting changed since the suggestion was made (it is closed as Obsolete).</summary>
    Stale,
    /// <summary>No concrete value: the admin edits the monitor instead.</summary>
    NotApplicable,
    /// <summary>The monitor with the suggested value fails the normal monitor validation (plan rules, SSRF, field rules).</summary>
    Invalid,
    /// <summary>The monitor kept changing concurrently.</summary>
    Conflict
}

public sealed record SuggestionResult(SuggestionOutcome Outcome, int MonitorId = 0, IReadOnlyList<string>? Errors = null);

/// <summary>
/// Apply / dismiss of AI-5 suggestions (autonomy A1). Apply never writes the monitor directly: it rebuilds the edit form
/// model from the stored monitor (<see cref="MonitorInput.From"/>), changes the one field, runs the same validation as the
/// edit page (data annotations including the SSRF pre-check, then <see cref="MonitorInput.Validate(Plan, ModelStateDictionary, Microsoft.Extensions.Localization.IStringLocalizer, string)"/>
/// with the plan rules) and saves through <see cref="MonitorCommands"/> (Version bump, probe-settings reset). Each
/// attempt re-reads the monitor, so a concurrent change is re-validated, never overwritten; the suggestion is marked
/// Applied in the same SaveChanges. Every query is restricted to the customer passed in (RequiredCustomerId).
/// </summary>
public sealed class SettingsSuggestionCommands(AppDbContext db, MonitorCommands commands, AiPanels ai, IServiceProvider services, Text<AppResource> L, TimeProvider time,
    IOptions<AiOptions> options)
{
    public const int MaxAttempts = 3;

    /// <summary>Ai:Suggestions:DismissDays.</summary>
    public int DismissDays => Math.Max(0, options.Value.Suggestions.DismissDays);

    /// <summary>The customer can see suggestions: opted in to AI and the plan includes AI-5 (AI-10).</summary>
    public static bool Allowed(Customer customer) => customer.AiEnabled && AiFeatures.PlanAllows(customer.Plan, AiFeatures.SettingsSuggestions);

    /// <param name="customer">The current customer with its Plan.</param>
    public async Task<SettingsSuggestionsModel> LoadAsync(Customer customer, int monitorId, string returnUrl, string? userId, bool canManage, CancellationToken ct)
    {
        if (!Allowed(customer)) return new(monitorId, [], canManage, DismissDays);
        var rows = await db.SettingsSuggestions.AsNoTracking()
            .Where(s => s.CustomerId == customer.Id && s.MonitorId == monitorId && s.Status == SettingsSuggestionStatus.Open)
            .OrderBy(s => s.Kind)
            .ToListAsync(ct);
        var items = new List<SettingsSuggestionView>();
        foreach (var s in rows)
        {
            var panel = await ai.LoadAsync(customer, AiFeatures.SettingsSuggestions, SettingsSuggestionProvider.SubjectType,
                s.Id.ToString(CultureInfo.InvariantCulture), s.EvidenceUpdatedAtUtc, returnUrl, userId, canManage, ct);
            items.Add(new(s, SuggestionFact.Parse(s.EvidenceJson), panel.State is AiPanelState.Decision or AiPanelState.Pending ? panel : null,
                s.SuggestedValue is not null));
        }
        return new(monitorId, items, canManage, DismissDays);
    }

    /// <summary>The monitor's current value of the setting a suggestion is about (invariant text).</summary>
    public static string? CurrentValue(MonitorEntity m, SettingsSuggestionKind kind) => kind switch
    {
        SettingsSuggestionKind.RaiseFailureThreshold => m.FailureThreshold.ToString(CultureInfo.InvariantCulture),
        SettingsSuggestionKind.RaiseTimeout => m.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        SettingsSuggestionKind.ReviewKeyword => m.Keyword,
        _ => null,
    };

    public async Task<SuggestionResult> ApplyAsync(int customerId, Plan plan, long suggestionId, string? userId, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            db.ChangeTracker.Clear();
            var s = await db.SettingsSuggestions.FirstOrDefaultAsync(x => x.Id == suggestionId && x.CustomerId == customerId, ct);
            if (s is null) return new(SuggestionOutcome.NotFound);
            if (s.Status != SettingsSuggestionStatus.Open) return new(SuggestionOutcome.Stale, s.MonitorId);
            if (s.SuggestedValue is null || s.Kind == SettingsSuggestionKind.TargetMayBeWrong) return new(SuggestionOutcome.NotApplicable, s.MonitorId);
            var m = await commands.FindTrackedAsync(customerId, s.MonitorId, ct);
            if (m is null) return new(SuggestionOutcome.NotFound);

            var now = time.GetUtcNow().UtcDateTime;
            if (CurrentValue(m, s.Kind) != s.CurrentValue || m.Type == MonitorType.Heartbeat)
            {
                // Someone changed the setting since the suggestion was made: it no longer describes the monitor.
                s.Status = SettingsSuggestionStatus.Obsolete;
                s.ClosedAtUtc = now;
                await db.SaveChangesAsync(ct);
                return new(SuggestionOutcome.Stale, s.MonitorId);
            }

            var input = MonitorInput.From(m);
            switch (s.Kind)
            {
                case SettingsSuggestionKind.RaiseFailureThreshold:
                    input.FailureThreshold = int.Parse(s.SuggestedValue, CultureInfo.InvariantCulture);
                    break;
                case SettingsSuggestionKind.RaiseTimeout:
                    input.TimeoutSeconds = int.Parse(s.SuggestedValue, CultureInfo.InvariantCulture);
                    break;
                case SettingsSuggestionKind.ReviewKeyword:
                    input.Keyword = s.SuggestedValue;
                    break;
            }
            var errors = Validate(input, plan);
            if (errors.Count > 0) return new(SuggestionOutcome.Invalid, s.MonitorId, errors);

            s.Status = SettingsSuggestionStatus.Applied;
            s.ClosedAtUtc = now;
            s.ClosedByUserId = userId;
            var r = await commands.TryUpdateOnceAsync(m, input, ct); // saves the monitor and the suggestion together
            if (r.Error == MonitorCommandError.None) return new(SuggestionOutcome.Applied, s.MonitorId);
        }
        return new(SuggestionOutcome.Conflict);
    }

    /// <summary>The edit page's validation: data annotations (incl. the SSRF pre-check) and the business / plan rules.</summary>
    private List<string> Validate(MonitorInput input, Plan plan)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(input, new ValidationContext(input, services, null), results, validateAllProperties: true);
        var errors = results.Select(r => r.ErrorMessage).OfType<string>().Select(msg => L[msg].Value).ToList();
        var ms = new ModelStateDictionary();
        input.Validate(plan, ms, L);
        errors.AddRange(ms.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
        return errors.Distinct().ToList();
    }

    public async Task<SuggestionResult> DismissAsync(int customerId, long suggestionId, string? userId, CancellationToken ct)
    {
        var s = await db.SettingsSuggestions.FirstOrDefaultAsync(x => x.Id == suggestionId && x.CustomerId == customerId, ct);
        if (s is null) return new(SuggestionOutcome.NotFound);
        if (s.Status != SettingsSuggestionStatus.Open) return new(SuggestionOutcome.Stale, s.MonitorId);
        s.Status = SettingsSuggestionStatus.Dismissed;
        s.ClosedAtUtc = time.GetUtcNow().UtcDateTime;
        s.ClosedByUserId = userId;
        await db.SaveChangesAsync(ct);
        return new(SuggestionOutcome.Dismissed, s.MonitorId);
    }
}
