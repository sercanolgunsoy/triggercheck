using Microsoft.Extensions.Localization;
using TriggerCheck.Ai.Features.MonitorSetup;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Support;

/// <summary>What the "Describe what to monitor" box on the create-monitor page shows (partial "_MonitorAssistant").</summary>
/// <param name="Request">The text the user typed (kept in the box after a submit).</param>
/// <param name="Explanation">AI-generated explanation of the choices (model text, HTML-encoded by Razor).</param>
/// <param name="Messages">Status and server-side adjustments, in the UI language.</param>
/// <param name="Filled">The form below was pre-filled by the assistant.</param>
public sealed record MonitorAssistantView(string? Request, string? Explanation, IReadOnlyList<string> Messages, bool Filled, bool IsError)
{
    public static MonitorAssistantView Empty { get; } = new(null, null, [], false, false);
}

/// <summary>Maps an AI-9 proposal onto the create form and words its notes (A1: fills the form, never saves).</summary>
public static class MonitorSetupAssist
{
    /// <summary>Copies every proposed value onto the form model. Nothing is saved; the normal create post does that.</summary>
    public static void ApplyTo(MonitorSetupFields f, MonitorInput input)
    {
        input.Type = f.Type;
        input.Name = f.Name;
        input.Target = f.Target;
        input.Port = f.Port;
        input.Keyword = f.Keyword;
        input.HttpMethod = f.HttpMethod;
        input.ExpectedStatusCodes = f.ExpectedStatusCodes;
        input.IntervalSeconds = f.IntervalSeconds;
        input.TimeoutSeconds = f.TimeoutSeconds;
        input.FailureThreshold = f.FailureThreshold;
        input.DnsRecordType = f.DnsRecordType;
        input.JsonPath = f.JsonPath;
        input.AssertionOperator = f.AssertionOperator;
        input.ExpectedValue = f.ExpectedValue;
        input.GracePeriodSeconds = f.GracePeriodSeconds;
    }

    public static string Describe(MonitorSetupNote n, Plan plan, IStringLocalizer L) => n.Kind switch
    {
        MonitorSetupNoteKind.IntervalRaisedToPlanMinimum =>
            L["You asked for a check every {0}. Your plan ({1}) allows {2} at the fastest, so {2} was set.", Fmt.Interval(n.From ?? 0), plan.Name, Fmt.Interval(n.To ?? 0)],
        MonitorSetupNoteKind.IntervalRounded => L["A check every {0} is not offered; {1} was set.", Fmt.Interval(n.From ?? 0), Fmt.Interval(n.To ?? 0)],
        MonitorSetupNoteKind.TimeoutAdjusted =>
            L["The timeout was changed from {0} s to {1} s (it must be 1-60 s and shorter than the check interval).", n.From ?? 0, n.To ?? 0],
        MonitorSetupNoteKind.FailureThresholdAdjusted => L["The failure threshold was changed from {0} to {1} (allowed: 1-5).", n.From ?? 0, n.To ?? 0],
        MonitorSetupNoteKind.GracePeriodAdjusted =>
            L["The grace period was changed from {0} s to {1} s (allowed: {2}-{3} s).", n.From ?? 0, n.To ?? 0, MonitorSetupForm.MinGracePeriodSeconds, MonitorSetupForm.MaxGracePeriodSeconds],
        MonitorSetupNoteKind.PortInvalid => L["The suggested port is not valid; enter a port between 1 and 65535."],
        MonitorSetupNoteKind.CredentialsRemoved => L["The user name and password were removed from the address; credentials in the URL are not supported."],
        MonitorSetupNoteKind.MethodChanged => L["GET is used instead of HEAD because keyword and JSON monitors need the response body."],
        MonitorSetupNoteKind.StatusCodesReset => L["The suggested status codes were not valid; the default 200-299 is used."],
        MonitorSetupNoteKind.UnknownReference => L["The assistant referred to text that is not in your request; that value was left empty."],
        MonitorSetupNoteKind.RequestTruncated => L["Your description was too long and was shortened."],
        _ => n.Kind.ToString()
    };

    /// <summary>The message for a result without a proposal (null for Ready).</summary>
    public static string? StatusMessage(MonitorSetupStatus status, IStringLocalizer L) => status switch
    {
        MonitorSetupStatus.Empty => L["Describe the monitor you need first."].Value,
        MonitorSetupStatus.OverBudget => L["The AI budget of your plan is used up this month. Please fill in the form yourself."].Value,
        MonitorSetupStatus.RateLimited => L["You have used the assistant often in the last hour. Please try again later or fill in the form yourself."].Value,
        MonitorSetupStatus.Failed => L["The assistant could not fill in the form this time. Please try again or fill it in yourself."].Value,
        MonitorSetupStatus.OptOut or MonitorSetupStatus.NotInPlan or MonitorSetupStatus.Off => L["The assistant is not available for your account."].Value,
        _ => null
    };
}
