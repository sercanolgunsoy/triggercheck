using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TriggerCheck.Ai.Context;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Ai.Features;

/// <summary>One evidence fact of a settings suggestion: a key and its value (numbers in invariant format, times ISO).</summary>
public sealed record SuggestionFact(string Key, string Value)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(IEnumerable<SuggestionFact> facts) => JsonSerializer.Serialize(facts.ToList(), Json);

    public static IReadOnlyList<SuggestionFact> Parse(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<SuggestionFact>>(json, Json)?.Where(f => f?.Key is not null).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? Get(IReadOnlyList<SuggestionFact> facts, string key) => facts.FirstOrDefault(f => f.Key == key)?.Value;
}

/// <summary>A suggestion found by <see cref="SettingsSuggestionRules"/> (not stored yet).</summary>
public sealed record DetectedSuggestion(SettingsSuggestionKind Kind, string? CurrentValue, string? SuggestedValue, IReadOnlyList<SuggestionFact> Evidence);

/// <summary>
/// The L1 rules of AI-5: pure functions of the monitor's settings and its stored history (no LLM, no I/O). Every number
/// a rule puts into the evidence is computed here from the database rows; the model only explains them.
/// <list type="bullet">
/// <item><b>Flapping</b> → raise FailureThreshold: at least <see cref="MinBriefIncidents"/> incidents in the window
/// recovered at the next check (lasted at most one interval + timeout + slack), and they are at least
/// <see cref="MinBriefShare"/> of all incidents. Suggests threshold + 1 (max 5).</item>
/// <item><b>Near timeout</b> → raise TimeoutSeconds: of at least <see cref="MinSuccessfulChecks"/> successful checks,
/// responses at ≥ <see cref="NearTimeoutRatio"/> of the timeout plus timed-out checks number at least
/// <see cref="MinNearTimeoutEvents"/> and <see cref="MinNearTimeoutShare"/> of the checks (and successes are the
/// majority: a dead target is not a timeout problem). Suggests ceil(1.5 × p99) seconds, below the interval, max 60.</item>
/// <item><b>Never up</b> → the target may be wrong: no successful check at all since the monitor was created (the history
/// must reach back to its creation, i.e. retention removed nothing), failing for at least <see cref="NeverUpMinHours"/>
/// hours and <see cref="NeverUpMinChecks"/> checks, and not mostly keyword misses (that is the keyword rule).</item>
/// <item><b>Keyword</b> → review the keyword: the page answered but the keyword was missing in at least
/// <see cref="MinKeywordMisses"/> checks and <see cref="MinKeywordMissShare"/> of the checks. Only the stored error
/// texts and status codes are used: response bodies are never stored or sent (decision K5), so the only concrete
/// proposal is the keyword without stray whitespace when it has some.</item>
/// </list>
/// Heartbeat (push) monitors get no suggestions: their threshold is fixed and they have no target or timeout.
/// </summary>
public static partial class SettingsSuggestionRules
{
    public const int MinBriefIncidents = 5;
    public const double MinBriefShare = 0.8;
    public const int BriefSlackSeconds = 30;
    public const int MaxFailureThreshold = 5;

    public const int MinSuccessfulChecks = 50;
    public const double NearTimeoutRatio = 0.8;
    public const int MinNearTimeoutEvents = 10;
    public const double MinNearTimeoutShare = 0.02;
    public const int MaxTimeoutSeconds = 60;

    public const int NeverUpMinHours = 24;
    public const int NeverUpMinChecks = 10;
    /// <summary>The oldest stored check may be at most this long after the monitor's creation for "never" to hold.</summary>
    public static readonly TimeSpan NeverUpHistorySlack = TimeSpan.FromDays(1);

    public const string TopErrorKey = "top_error";
    public const int MaxErrorChars = 300;

    public const int MinKeywordMisses = 10;
    public const double MinKeywordMissShare = 0.05;

    /// <summary>One central check outside maintenance (ascending time order).</summary>
    public sealed record Check(DateTime AtUtc, bool IsUp, int? StatusCode, int Ms, string? Error);

    public sealed record IncidentRow(DateTime StartedAtUtc, DateTime? ResolvedAtUtc, bool ResolvedDuringMaintenance);

    /// <summary>The whole stored central history of a monitor without a successful check (input of the never-up rule).</summary>
    /// <param name="TopError">Most common error text (raw; masked before it reaches the model).</param>
    public sealed record NeverUpHistory(DateTime FirstCheckAtUtc, int Checks, string? TopError, int TopErrorChecks, int KeywordMisses);

    public sealed record MonitorSettings(MonitorType Type, int IntervalSeconds, int TimeoutSeconds, int FailureThreshold, string? Keyword,
        MonitorStatus Status, DateTime CreatedAtUtc);

    public static MonitorSettings SettingsOf(Domain.Entities.Monitor m) =>
        new(m.Type, m.IntervalSeconds, m.TimeoutSeconds, m.FailureThreshold, m.Keyword, m.Status, m.CreatedAtUtc);

    /// <param name="checks">Central checks outside maintenance in the window, ascending.</param>
    /// <param name="incidents">Incidents that started in the window.</param>
    /// <param name="neverUp">Set only when the monitor has no successful central check at all.</param>
    public static List<DetectedSuggestion> Evaluate(MonitorSettings m, IReadOnlyList<Check> checks, IReadOnlyList<IncidentRow> incidents,
        NeverUpHistory? neverUp, DateTime nowUtc, int windowDays)
    {
        var result = new List<DetectedSuggestion>();
        if (m.Type == MonitorType.Heartbeat) return result;
        if (Flapping(m, incidents, windowDays) is { } f) result.Add(f);
        if (NearTimeout(m, checks, windowDays) is { } t) result.Add(t);
        if (NeverUp(m, neverUp, nowUtc) is { } n) result.Add(n);
        if (Keyword(m, checks, windowDays) is { } k) result.Add(k);
        return result;
    }

    public static DetectedSuggestion? Flapping(MonitorSettings m, IReadOnlyList<IncidentRow> incidents, int windowDays)
    {
        if (m.FailureThreshold >= MaxFailureThreshold) return null;
        var relevant = incidents.Where(i => !i.ResolvedDuringMaintenance).ToList();
        var limit = TimeSpan.FromSeconds(Math.Max(1, m.IntervalSeconds) + Math.Max(1, m.TimeoutSeconds) + BriefSlackSeconds);
        var brief = relevant.Where(i => i.ResolvedAtUtc is DateTime r && r - i.StartedAtUtc <= limit).ToList();
        if (brief.Count < MinBriefIncidents || brief.Count < MinBriefShare * relevant.Count) return null;

        var longest = (long)Math.Ceiling(brief.Max(i => (i.ResolvedAtUtc!.Value - i.StartedAtUtc).TotalSeconds));
        var suggested = m.FailureThreshold + 1;
        return new(SettingsSuggestionKind.RaiseFailureThreshold, Int(m.FailureThreshold), Int(suggested),
        [
            new("window_days", Int(windowDays)),
            new("incidents", Int(relevant.Count)),
            new("brief_incidents", Int(brief.Count)),
            new("longer_incidents", Int(relevant.Count - brief.Count)),
            new("longest_brief_incident_seconds", Int(longest)),
            new("interval_seconds", Int(m.IntervalSeconds)),
            new("failure_threshold", Int(m.FailureThreshold)),
            new("suggested_failure_threshold", Int(suggested)),
        ]);
    }

    /// <summary>Error text of a probe that ran into the monitor's timeout (ProbeErrors: "Timeout after 10s").</summary>
    public static bool IsTimeoutError(string? error) => error is not null && error.StartsWith("Timeout after", StringComparison.Ordinal);

    public static DetectedSuggestion? NearTimeout(MonitorSettings m, IReadOnlyList<Check> checks, int windowDays)
    {
        var timeoutMs = Math.Max(1, m.TimeoutSeconds) * 1000;
        var successes = checks.Where(c => c.IsUp).Select(c => c.Ms).OrderBy(x => x).ToList();
        var timeouts = checks.Count(c => !c.IsUp && IsTimeoutError(c.Error));
        if (successes.Count < MinSuccessfulChecks || timeouts >= successes.Count) return null;
        var nearAt = (int)Math.Ceiling(timeoutMs * NearTimeoutRatio);
        var near = successes.Count(ms => ms >= nearAt);
        var considered = successes.Count + timeouts;
        if (near + timeouts < MinNearTimeoutEvents || near + timeouts < MinNearTimeoutShare * considered) return null;

        var p95 = Percentile(successes, 0.95);
        var p99 = Percentile(successes, 0.99);
        var max = successes[^1];
        var cap = Math.Min(MaxTimeoutSeconds, m.IntervalSeconds - 1);
        var suggested = Math.Min(cap, Math.Max(m.TimeoutSeconds + 1, (int)Math.Ceiling(p99 * 1.5 / 1000.0)));
        if (suggested <= m.TimeoutSeconds) return null; // the interval leaves no room for a longer timeout

        return new(SettingsSuggestionKind.RaiseTimeout, Int(m.TimeoutSeconds), Int(suggested),
        [
            new("window_days", Int(windowDays)),
            new("successful_checks", Int(successes.Count)),
            new("near_timeout_checks", Int(near)),
            new("near_timeout_from_ms", Int(nearAt)),
            new("timed_out_checks", Int(timeouts)),
            new("p95_ms", Int(p95)),
            new("p99_ms", Int(p99)),
            new("max_ms", Int(max)),
            new("timeout_seconds", Int(m.TimeoutSeconds)),
            new("interval_seconds", Int(m.IntervalSeconds)),
            new("suggested_timeout_seconds", Int(suggested)),
        ]);
    }

    public static DetectedSuggestion? NeverUp(MonitorSettings m, NeverUpHistory? h, DateTime nowUtc)
    {
        if (h is null || m.Status is not (MonitorStatus.Down or MonitorStatus.Unknown)) return null;
        var hours = (long)Math.Floor((nowUtc - h.FirstCheckAtUtc).TotalHours);
        if (hours < NeverUpMinHours || h.Checks < NeverUpMinChecks) return null;
        if (h.FirstCheckAtUtc > m.CreatedAtUtc + NeverUpHistorySlack) return null; // older history may have been purged
        if (h.KeywordMisses * 2 >= h.Checks) return null; // the target answers; the keyword rule covers it

        var facts = new List<SuggestionFact>
        {
            new("first_check_at", AiContextBuilder.Iso(h.FirstCheckAtUtc)),
            new("hours_without_success", Int(hours)),
            new("failed_checks", Int(h.Checks)),
            new("successful_checks", "0"),
        };
        if (h.TopError is not null)
        {
            facts.Add(new("top_error_checks", Int(h.TopErrorChecks)));
            // Customer-visible probe error (raw here; the explanation provider sends it masked, as untrusted data).
            facts.Add(new(TopErrorKey, h.TopError.Length <= MaxErrorChars ? h.TopError : h.TopError[..MaxErrorChars] + "…"));
        }
        return new(SettingsSuggestionKind.TargetMayBeWrong, null, null, facts);
    }

    /// <summary>KeywordMonitorChecker's failure text: "Keyword '…' not found in response".</summary>
    public static bool IsKeywordMiss(string? error) =>
        error is not null && error.StartsWith("Keyword '", StringComparison.Ordinal) && error.EndsWith("' not found in response", StringComparison.Ordinal);

    public static DetectedSuggestion? Keyword(MonitorSettings m, IReadOnlyList<Check> checks, int windowDays)
    {
        if (m.Type != MonitorType.Keyword || string.IsNullOrEmpty(m.Keyword) || checks.Count == 0) return null;
        var misses = checks.Where(c => !c.IsUp && IsKeywordMiss(c.Error)).ToList();
        if (misses.Count < MinKeywordMisses || misses.Count < MinKeywordMissShare * checks.Count) return null;

        var inARow = 0;
        for (var i = checks.Count - 1; i >= 0 && !checks[i].IsUp && IsKeywordMiss(checks[i].Error); i--) inARow++;
        var lastMatch = checks.LastOrDefault(c => c.IsUp)?.AtUtc;
        var status = misses.Where(c => c.StatusCode is not null).GroupBy(c => c.StatusCode!.Value)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => (int?)g.Key).FirstOrDefault();
        var normalized = Whitespace().Replace(m.Keyword.Trim(), " ");
        var hasStrayWhitespace = normalized != m.Keyword && normalized.Length > 0;

        var facts = new List<SuggestionFact>
        {
            new("window_days", Int(windowDays)),
            new("checks", Int(checks.Count)),
            new("keyword_misses", Int(misses.Count)),
            new("misses_in_a_row", Int(inARow)),
            new("last_match_at", lastMatch is DateTime lm ? AiContextBuilder.Iso(lm) : "none in the window"),
        };
        if (status is int sc) facts.Add(new("status_code_of_misses", Int(sc)));
        facts.Add(new("keyword_has_extra_whitespace", hasStrayWhitespace ? "yes" : "no"));
        return new(SettingsSuggestionKind.ReviewKeyword, m.Keyword, hasStrayWhitespace ? normalized : null, facts);
    }

    /// <summary>Nearest-rank percentile of an ascending list.</summary>
    public static int Percentile(IReadOnlyList<int> ascending, double p)
    {
        if (ascending.Count == 0) return 0;
        var rank = (int)Math.Ceiling(p * ascending.Count);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Count - 1)];
    }

    private static string Int(long v) => v.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
