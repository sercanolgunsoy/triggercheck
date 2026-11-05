using System.Text.RegularExpressions;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Ai.Features.MonitorSetup;

/// <summary>Pre-filled values of the create-monitor form (the web layer copies them into its MonitorInput).</summary>
public sealed record MonitorSetupFields
{
    public MonitorType Type { get; init; }
    public string Name { get; init; } = "";
    public string Target { get; init; } = "";
    public int? Port { get; init; }
    public string? Keyword { get; init; }
    public string HttpMethod { get; init; } = "GET";
    public string ExpectedStatusCodes { get; init; } = MonitorSetupForm.DefaultStatusCodes;
    public int IntervalSeconds { get; init; }
    public int TimeoutSeconds { get; init; } = MonitorSetupForm.DefaultTimeoutSeconds;
    public int FailureThreshold { get; init; } = MonitorSetupForm.DefaultFailureThreshold;
    public DnsRecordType? DnsRecordType { get; init; }
    public string? JsonPath { get; init; }
    public AssertionOperator? AssertionOperator { get; init; }
    public string? ExpectedValue { get; init; }
    public int GracePeriodSeconds { get; init; } = MonitorSetupForm.DefaultGracePeriodSeconds;
}

/// <summary>Server-side adjustments the customer is told about (the web layer words them in the UI language).</summary>
public enum MonitorSetupNoteKind
{
    /// <summary>The requested interval is below the plan minimum: raised to the fastest allowed (From → To).</summary>
    IntervalRaisedToPlanMinimum,
    /// <summary>The requested interval is not offered: the next allowed one was chosen (From → To).</summary>
    IntervalRounded,
    /// <summary>Timeout outside 1-60 s or not shorter than the interval (From → To).</summary>
    TimeoutAdjusted,
    /// <summary>Failure threshold outside 1-5 (From → To).</summary>
    FailureThresholdAdjusted,
    /// <summary>Grace period of a heartbeat outside the allowed range (From → To).</summary>
    GracePeriodAdjusted,
    /// <summary>Port outside 1-65535: left empty.</summary>
    PortInvalid,
    /// <summary>User name / password removed from the link; credentials in the URL are not supported.</summary>
    CredentialsRemoved,
    /// <summary>HEAD has no body: GET is used for keyword and JSON monitors.</summary>
    MethodChanged,
    /// <summary>Expected status codes were not in the "200-299,301" form: the default is used.</summary>
    StatusCodesReset,
    /// <summary>A value referred to text that is not in the request (an invented placeholder): left empty.</summary>
    UnknownReference,
    /// <summary>The request was cut to the configured length before it was sent.</summary>
    RequestTruncated,
}

public sealed record MonitorSetupNote(MonitorSetupNoteKind Kind, int? From = null, int? To = null);

public enum MonitorSetupOutcome
{
    /// <summary>The form is pre-filled.</summary>
    Form,
    /// <summary>The request is ambiguous: a question for the customer, nothing pre-filled.</summary>
    Clarify,
    /// <summary>The model chose no or an unknown monitor type: nothing pre-filled.</summary>
    UnsupportedType,
}

/// <param name="Explanation">Model text (placeholders restored), shown as AI-generated.</param>
public sealed record MonitorSetupProposal(MonitorSetupOutcome Outcome, MonitorSetupFields? Fields, string? Explanation, string? Clarification,
    IReadOnlyList<MonitorSetupNote> Notes);

/// <summary>
/// Turns a model draft into form values: unknown types are rejected; placeholders are replaced by the user's own text
/// (invented ones are dropped); targets are normalized per type (scheme added, credentials and paths removed where they
/// do not belong); every number is clamped to the product and plan rules (the interval to the plan's fastest allowed
/// interval, with a note); fields that do not apply to the type are cleared. The result still goes through the normal
/// form validation (plan rules, SSRF pre-check) before the customer sees it, and is saved only by the normal create path.
/// </summary>
public static partial class MonitorSetupForm
{
    public const string DefaultStatusCodes = "200-299";
    public const int DefaultTimeoutSeconds = 10;
    public const int DefaultFailureThreshold = 2;
    public const int DefaultGracePeriodSeconds = 60;
    public const int MinGracePeriodSeconds = 10;
    public const int MaxGracePeriodSeconds = 86400;
    public const int MaxNameChars = 200;

    [GeneratedRegex(@"^\s*\d{3}(\s*-\s*\d{3})?(\s*,\s*\d{3}(\s*-\s*\d{3})?)*\s*$")]
    private static partial Regex StatusCodes();

    public static MonitorSetupProposal Build(MonitorSetupDraft draft, Plan plan, MonitorSetupRequest request)
    {
        var notes = new List<MonitorSetupNote>();
        if (request.Truncated) notes.Add(new(MonitorSetupNoteKind.RequestTruncated));
        string? Text(string? value) => Restore(value, false);
        string? Display(string? value) => Restore(value, true);
        string? Restore(string? value, bool linksAsHost)
        {
            if (request.TryRestore(value, out var restored, linksAsHost)) return string.IsNullOrWhiteSpace(restored) ? null : restored.Trim();
            if (!notes.Any(n => n.Kind == MonitorSetupNoteKind.UnknownReference)) notes.Add(new(MonitorSetupNoteKind.UnknownReference));
            return null;
        }
        var explanation = Display(draft.Explanation);
        var clarification = Display(draft.Clarification);

        if (draft.IsClarification)
            return new(MonitorSetupOutcome.Clarify, null, explanation, clarification, notes);
        if (draft.Type is null || !MonitorSetupPrompts.Types.TryGetValue(draft.Type, out var type))
            return new(MonitorSetupOutcome.UnsupportedType, null, explanation, clarification, notes);

        var isHttp = type is MonitorType.Http or MonitorType.Keyword or MonitorType.JsonApi;
        var isHeartbeat = type == MonitorType.Heartbeat;

        // ---- target ----
        int? portFromTarget = null;
        var target = isHeartbeat ? "" : NormalizeTarget(type, ResolveTarget(draft.Target, request, Text), notes, out portFromTarget);

        // ---- interval ----
        var allowed = isHeartbeat ? PlanRules.AllowedHeartbeatIntervals(plan) : PlanRules.AllowedIntervals(plan);
        var fastest = allowed.Count > 0 ? allowed.Min() : Math.Max(plan.MinIntervalSeconds, isHeartbeat ? PlanRules.MinHeartbeatIntervalSeconds : 0);
        var interval = fastest;
        if (draft.IntervalSeconds is { } want)
        {
            var w = (int)Math.Clamp(want, 0, int.MaxValue);
            if (allowed.Contains(w)) interval = w;
            else if (w < fastest)
            {
                interval = fastest;
                notes.Add(new(MonitorSetupNoteKind.IntervalRaisedToPlanMinimum, w, interval));
            }
            else
            {
                interval = allowed.Where(i => i >= w).DefaultIfEmpty(allowed.Count > 0 ? allowed.Max() : fastest).Min();
                notes.Add(new(MonitorSetupNoteKind.IntervalRounded, w, interval));
            }
        }

        // ---- timeout / threshold (probed types only) ----
        var timeout = DefaultTimeoutSeconds;
        var threshold = DefaultFailureThreshold;
        if (!isHeartbeat)
        {
            if (draft.TimeoutSeconds is { } t)
            {
                timeout = (int)Math.Clamp(t, 1, 60);
                if (timeout != t) notes.Add(new(MonitorSetupNoteKind.TimeoutAdjusted, (int)Math.Clamp(t, int.MinValue, int.MaxValue), timeout));
            }
            if (timeout >= interval)
            {
                var from = timeout;
                timeout = Math.Max(1, interval / 2);
                if (draft.TimeoutSeconds is not null) notes.Add(new(MonitorSetupNoteKind.TimeoutAdjusted, from, timeout));
            }
            if (draft.FailureThreshold is { } ft)
            {
                threshold = (int)Math.Clamp(ft, 1, 5);
                if (threshold != ft) notes.Add(new(MonitorSetupNoteKind.FailureThresholdAdjusted, (int)Math.Clamp(ft, int.MinValue, int.MaxValue), threshold));
            }
        }

        // ---- grace period (heartbeat) ----
        var grace = DefaultGracePeriodSeconds;
        if (isHeartbeat && draft.GracePeriodSeconds is { } g)
        {
            grace = (int)Math.Clamp(g, MinGracePeriodSeconds, MaxGracePeriodSeconds);
            if (grace != g) notes.Add(new(MonitorSetupNoteKind.GracePeriodAdjusted, (int)Math.Clamp(g, int.MinValue, int.MaxValue), grace));
        }

        // ---- port (tcp) ----
        int? port = null;
        if (type == MonitorType.Tcp)
        {
            var p = draft.Port ?? portFromTarget;
            if (p is >= 1 and <= 65535) port = (int)p;
            else if (p is not null) notes.Add(new(MonitorSetupNoteKind.PortInvalid));
        }

        // ---- HTTP settings ----
        var method = "GET";
        var codes = DefaultStatusCodes;
        if (isHttp)
        {
            var m = draft.HttpMethod?.Trim().ToUpperInvariant();
            if (m is not null && MonitorSetupPrompts.HttpMethods.Contains(m)) method = m;
            if (method == "HEAD" && type != MonitorType.Http)
            {
                method = "GET";
                notes.Add(new(MonitorSetupNoteKind.MethodChanged));
            }
            if (draft.ExpectedStatusCodes is { } c)
            {
                if (StatusCodes().IsMatch(c)) codes = c.Replace(" ", "");
                else notes.Add(new(MonitorSetupNoteKind.StatusCodesReset));
            }
        }

        // ---- type-specific values ----
        string? keyword = type == MonitorType.Keyword ? Text(draft.Keyword) : null;
        string? jsonPath = type == MonitorType.JsonApi ? Text(draft.JsonPath) : null;
        DnsRecordType? recordType = null;
        if (type == MonitorType.Dns)
            recordType = MonitorSetupPrompts.RecordTypes.FirstOrDefault(r => string.Equals(r.ToString(), draft.DnsRecordType, StringComparison.OrdinalIgnoreCase), DnsRecordType.A);
        AssertionOperator? op = null;
        string? expected = null;
        if (type is MonitorType.JsonApi or MonitorType.Dns)
        {
            if (draft.AssertionOperator is { } o && MonitorSetupPrompts.Operators.TryGetValue(o, out var parsed)
                && (type == MonitorType.JsonApi || parsed is AssertionOperator.Equal or AssertionOperator.Contains))
                op = parsed;
            expected = op == AssertionOperator.Exists ? null : Text(draft.ExpectedValue);
            if (type == MonitorType.Dns && expected is not null) op ??= AssertionOperator.Equal;
        }

        var name = Display(draft.Name);
        if (string.IsNullOrWhiteSpace(name)) name = DefaultName(type, target);
        if (name.Length > MaxNameChars) name = name[..MaxNameChars].TrimEnd();

        var fields = new MonitorSetupFields
        {
            Type = type, Name = name, Target = target, Port = port, Keyword = keyword, HttpMethod = method, ExpectedStatusCodes = codes,
            IntervalSeconds = interval, TimeoutSeconds = timeout, FailureThreshold = threshold, DnsRecordType = recordType,
            JsonPath = jsonPath, AssertionOperator = op, ExpectedValue = expected, GracePeriodSeconds = grace,
        };
        return new(MonitorSetupOutcome.Form, fields, explanation, clarification, notes);
    }

    /// <summary>
    /// The user's link for a placeholder target; a host-only form of a link the user typed ("https://shop.example.com/…")
    /// is mapped back to that link; anything else is restored as text.
    /// </summary>
    private static string? ResolveTarget(string? value, MonitorSetupRequest request, Func<string?, string?> text)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (MonitorSetupRequest.BarePlaceholder(value) is { } key)
            return request.Urls.TryGetValue(key, out var link) ? link
                : request.Quotes.TryGetValue(key, out var quoted) ? quoted
                : text(value); // invented: recorded as an unknown reference
        var trimmed = value.Trim();
        if (trimmed.EndsWith("/…", StringComparison.Ordinal) || trimmed.EndsWith("/...", StringComparison.Ordinal))
        {
            var shared = trimmed.EndsWith("/…", StringComparison.Ordinal) ? trimmed : trimmed[..^4] + "/…";
            var matches = request.Urls.Values.Where(u => MonitorSetupRequest.SharedForm(u) == shared).Distinct().ToList();
            if (matches.Count == 1) return matches[0];
            trimmed = trimmed[..trimmed.LastIndexOf('/')];
        }
        return text(trimmed);
    }

    private static string NormalizeTarget(MonitorType type, string? value, List<MonitorSetupNote> notes, out int? port)
    {
        port = null;
        if (string.IsNullOrWhiteSpace(value)) return "";
        var v = value.Trim();
        var hasScheme = v.Contains("://", StringComparison.Ordinal);
        if (type is MonitorType.Http or MonitorType.Keyword or MonitorType.JsonApi)
        {
            if (!hasScheme) v = "https://" + v;
            if (Uri.TryCreate(v, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
            {
                notes.Add(new(MonitorSetupNoteKind.CredentialsRemoved));
                v = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri;
            }
            return v;
        }

        // Tcp / Ping / Dns: a host name or address only.
        if (Uri.TryCreate(hasScheme ? v : "tcp://" + v, UriKind.Absolute, out var u) && u.Host.Length > 0)
        {
            if (u.UserInfo.Length > 0) notes.Add(new(MonitorSetupNoteKind.CredentialsRemoved));
            if (!u.IsDefaultPort && u.Port > 0) port = u.Port;
            // Punycode for Unicode names: the TCP / ping / DNS validation and checkers expect an ASCII host.
            return u.HostNameType == UriHostNameType.IPv6 ? u.Host.Trim('[', ']') : u.IdnHost;
        }
        return v;
    }

    private static string DefaultName(MonitorType type, string target)
    {
        var host = Uri.TryCreate(target, UriKind.Absolute, out var u) ? u.Host : target;
        return string.IsNullOrWhiteSpace(host) ? MonitorSetupPrompts.TypeName(type) : host;
    }
}
