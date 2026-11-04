using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Monitoring.Security;
using TriggerCheck.Web.Localization;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Areas.App.Support;

/// <summary>Form model for monitor create/edit. Never bind the entity directly (over-posting).</summary>
public class MonitorInput : IValidatableObject
{
    public static readonly string[] HttpMethods = ["GET", "HEAD", "POST"];

    /// <summary>Option text for an HTTP method in the form's dropdown.</summary>
    public static string HttpMethodName(string method, IStringLocalizer L) => method switch
    {
        "GET" => L["GET"],
        "HEAD" => L["HEAD"],
        "POST" => L["POST"],
        _ => method
    };

    [Required(ErrorMessage = "Name is required."), StringLength(200, ErrorMessage = "Name can be at most 200 characters.")]
    [Display(Name = "Name")]
    public string Name { get; set; } = "";

    [Display(Name = "Type")]
    public MonitorType Type { get; set; } = MonitorType.Http;

    [Required(ErrorMessage = "Target is required."), StringLength(2048, ErrorMessage = "Target can be at most 2048 characters.")]
    [Display(Name = "Target")]
    public string Target { get; set; } = "";

    [Display(Name = "Port")]
    public int? Port { get; set; }

    [StringLength(500, ErrorMessage = "Keyword can be at most 500 characters.")]
    [Display(Name = "Keyword")]
    public string? Keyword { get; set; }

    [Display(Name = "HTTP method")]
    public string HttpMethod { get; set; } = "GET";

    [StringLength(100, ErrorMessage = "Expected status codes can be at most 100 characters.")]
    [Display(Name = "Expected status codes")]
    public string? ExpectedStatusCodes { get; set; } = "200-299";

    [Display(Name = "Check interval")]
    public int IntervalSeconds { get; set; } = 300;

    [Range(1, 60, ErrorMessage = "Timeout must be between 1 and 60 seconds.")]
    [Display(Name = "Timeout (sec)")]
    public int TimeoutSeconds { get; set; } = 10;

    [Range(1, 5, ErrorMessage = "Failure threshold must be between 1 and 5.")]
    [Display(Name = "Failure threshold")]
    public int FailureThreshold { get; set; } = 2;

    [Display(Name = "Certificate expiry warning")]
    public int CertWarnDays { get; set; } = 14;

    private static readonly Regex StatusCodesRx = new(@"^\s*\d{3}(\s*-\s*\d{3})?(\s*,\s*\d{3}(\s*-\s*\d{3})?)*\s*$", RegexOptions.Compiled);
    private static readonly Regex HostRx = new(@"^(?=.{1,253}$)([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$", RegexOptions.Compiled);

    public static MonitorInput From(MonitorEntity m) => new()
    {
        Name = m.Name, Type = m.Type, Target = m.Target, Port = m.Port, Keyword = m.Keyword,
        HttpMethod = m.HttpMethod, ExpectedStatusCodes = m.ExpectedStatusCodes,
        IntervalSeconds = m.IntervalSeconds, TimeoutSeconds = m.TimeoutSeconds, FailureThreshold = m.FailureThreshold,
        CertWarnDays = m.CertWarnDays
    };

    /// <summary>Business validation (type-specific fields + plan rules). Errors are added with the "Input." prefix,
    /// in the language of <paramref name="L"/>.</summary>
    public void Validate(Plan plan, ModelStateDictionary ms, IStringLocalizer L, string prefix = "Input.")
    {
        Name = Name?.Trim() ?? "";
        Target = Target?.Trim() ?? "";
        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword;
        HttpMethod = (HttpMethod ?? "GET").Trim().ToUpperInvariant();

        if (!Enum.IsDefined(Type))
            ms.AddModelError(prefix + nameof(Type), L["Invalid monitor type."]);

        switch (Type)
        {
            case MonitorType.Http:
            case MonitorType.Keyword:
                if (!Uri.TryCreate(Target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host))
                    ms.AddModelError(prefix + nameof(Target), L["The target must be a valid URL starting with http:// or https://."]);
                if (!HttpMethods.Contains(HttpMethod))
                    ms.AddModelError(prefix + nameof(HttpMethod), L["The HTTP method must be GET, HEAD or POST."]);
                if (string.IsNullOrWhiteSpace(ExpectedStatusCodes))
                    ExpectedStatusCodes = "200-299";
                else if (!StatusCodesRx.IsMatch(ExpectedStatusCodes))
                    ms.AddModelError(prefix + nameof(ExpectedStatusCodes), L["Expected status codes must look like \"200-299,301\"."]);
                if (!MonitorStateMachine.CertWarnDayChoices.Contains(CertWarnDays))
                    ms.AddModelError(prefix + nameof(CertWarnDays), L["Invalid certificate expiry warning."]);
                if (Type == MonitorType.Keyword && string.IsNullOrWhiteSpace(Keyword))
                    ms.AddModelError(prefix + nameof(Keyword), L["A keyword monitor needs the text to search for."]);
                break;
            case MonitorType.Tcp:
            case MonitorType.Ping:
                if (!IsHostOrIp(Target))
                    ms.AddModelError(prefix + nameof(Target), L["The target must be a host name or IP address without a scheme (e.g. example.com)."]);
                if (Type == MonitorType.Tcp && (Port is null || Port < 1 || Port > 65535))
                    ms.AddModelError(prefix + nameof(Port), L["A TCP monitor needs a port between 1 and 65535."]);
                break;
        }

        if (!PlanRules.IsIntervalAllowed(plan, IntervalSeconds))
            ms.AddModelError(prefix + nameof(IntervalSeconds),
                L["This check interval is not available on your plan ({0}). Allowed: {1}.", plan.Name,
                    string.Join(", ", PlanRules.AllowedIntervals(plan).Select(Fmt.Interval))]);

        if (TimeoutSeconds >= IntervalSeconds)
            ms.AddModelError(prefix + nameof(TimeoutSeconds), L["The timeout must be shorter than the check interval."]);
    }

    /// <summary>
    /// Friendly SSRF pre-check: rejects literal private/internal IPs and names like "localhost" so the user gets a form
    /// error. The real guard is at connect time (TargetGuard), which also covers DNS names and redirects.
    /// Skipped when Monitoring:AllowPrivateTargets is set.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (context.GetService(typeof(IOptions<MonitoringOptions>)) is IOptions<MonitoringOptions> { Value.AllowPrivateTargets: true }) yield break;

        var target = Target?.Trim();
        var host = Type is MonitorType.Http or MonitorType.Keyword
            ? (Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.Host : null)
            : target;
        if (!TargetGuard.IsObviouslyInternalHost(host)) yield break;

        var L = (Text<AppResource>)context.GetService(typeof(Text<AppResource>))!;
        yield return new ValidationResult(L["This address points to a private or internal network and cannot be monitored."], [nameof(Target)]);
    }

    public static bool IsHostOrIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("://") || value.Contains('/')) return false;
        return IPAddress.TryParse(value, out _) || HostRx.IsMatch(value);
    }

    /// <summary>Copies user-editable fields to the entity, clearing fields that don't apply to the type.</summary>
    public void ApplyTo(MonitorEntity m)
    {
        var isHttp = Type is MonitorType.Http or MonitorType.Keyword;
        if (m.Target != Target || m.Type != Type)
        {
            // The stored certificate belonged to the old target.
            m.CertExpiresAtUtc = null;
            m.CertSubject = null;
            m.CertIssuer = null;
            m.CertError = null;
            m.CertCheckedAtUtc = null;
            m.CertWarningSentForUtc = null;
        }
        m.CertWarnDays = isHttp ? CertWarnDays : 0;
        m.Name = Name;
        m.Type = Type;
        m.Target = Target;
        m.Port = Type == MonitorType.Tcp ? Port : null;
        m.Keyword = Type == MonitorType.Keyword ? Keyword : null;
        m.HttpMethod = isHttp ? HttpMethod : "GET";
        m.ExpectedStatusCodes = isHttp ? ExpectedStatusCodes ?? "200-299" : "200-299";
        m.IntervalSeconds = IntervalSeconds;
        m.TimeoutSeconds = TimeoutSeconds;
        m.FailureThreshold = FailureThreshold;
    }
}
