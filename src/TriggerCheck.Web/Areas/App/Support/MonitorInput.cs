using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Checks;
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

    /// <summary>Required for every type except Heartbeat (checked in Validate).</summary>
    [StringLength(2048, ErrorMessage = "Target can be at most 2048 characters.")]
    [Display(Name = "Target")]
    [DisplayFormat(ConvertEmptyStringToNull = false)] // no implicit [Required] message: Validate reports it per type
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

    [Display(Name = "Record type")]
    public DnsRecordType? DnsRecordType { get; set; }

    [StringLength(Monitoring.Checks.JsonPath.MaxLength, ErrorMessage = "The JSON path can be at most 500 characters.")]
    [Display(Name = "JSON path")]
    public string? JsonPath { get; set; }

    [Display(Name = "Condition")]
    public AssertionOperator? AssertionOperator { get; set; }

    [StringLength(500, ErrorMessage = "The expected value can be at most 500 characters.")]
    [Display(Name = "Expected value")]
    public string? ExpectedValue { get; set; }

    [Display(Name = "Grace period (sec)")]
    public int GracePeriodSeconds { get; set; } = DefaultGracePeriodSeconds;

    public const int DefaultGracePeriodSeconds = 60;
    public const int MaxGracePeriodSeconds = 86400;
    /// <summary>Below this, normal network jitter of a ping would already count as a missed heartbeat.</summary>
    public const int MinGracePeriodSeconds = 10;

    /// <summary>
    /// Set by <see cref="ApplyTo"/> when it issued a new heartbeat ping token (monitor created as / changed to
    /// Heartbeat). Only the hash is stored, so this is the one chance to show the full ping URL. Never bound.
    /// </summary>
    [BindNever, ValidateNever]
    public string? IssuedHeartbeatToken { get; private set; }

    public static readonly DnsRecordType[] DnsRecordTypes =
        [Domain.Entities.DnsRecordType.A, Domain.Entities.DnsRecordType.AAAA, Domain.Entities.DnsRecordType.CNAME,
         Domain.Entities.DnsRecordType.MX, Domain.Entities.DnsRecordType.NS, Domain.Entities.DnsRecordType.TXT];

    public static readonly AssertionOperator[] JsonOperators =
        [Domain.Entities.AssertionOperator.Equal, Domain.Entities.AssertionOperator.Contains, Domain.Entities.AssertionOperator.Exists,
         Domain.Entities.AssertionOperator.LessThan, Domain.Entities.AssertionOperator.GreaterThan];

    public static readonly AssertionOperator[] DnsOperators =
        [Domain.Entities.AssertionOperator.Equal, Domain.Entities.AssertionOperator.Contains];

    /// <summary>Option text for an assertion operator.</summary>
    public static string OperatorName(AssertionOperator op, IStringLocalizer L) => op switch
    {
        Domain.Entities.AssertionOperator.Equal => L["equals"],
        Domain.Entities.AssertionOperator.Contains => L["contains"],
        Domain.Entities.AssertionOperator.Exists => L["exists"],
        Domain.Entities.AssertionOperator.LessThan => L["is less than"],
        Domain.Entities.AssertionOperator.GreaterThan => L["is greater than"],
        _ => op.ToString()
    };

    public static bool IsHttpType(MonitorType t) => t is MonitorType.Http or MonitorType.Keyword or MonitorType.JsonApi;

    private static readonly Regex StatusCodesRx = new(@"^\s*\d{3}(\s*-\s*\d{3})?(\s*,\s*\d{3}(\s*-\s*\d{3})?)*\s*$", RegexOptions.Compiled);
    private static readonly Regex HostRx = new(@"^(?=.{1,253}$)([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$", RegexOptions.Compiled);

    public static MonitorInput From(MonitorEntity m) => new()
    {
        Name = m.Name, Type = m.Type, Target = m.Type == MonitorType.Heartbeat ? "" : m.Target, Port = m.Port, Keyword = m.Keyword,
        HttpMethod = m.HttpMethod, ExpectedStatusCodes = m.ExpectedStatusCodes,
        IntervalSeconds = m.IntervalSeconds, TimeoutSeconds = m.TimeoutSeconds, FailureThreshold = m.FailureThreshold,
        CertWarnDays = m.CertWarnDays,
        DnsRecordType = m.DnsRecordType, JsonPath = m.JsonPath, AssertionOperator = m.AssertionOperator, ExpectedValue = m.ExpectedValue,
        GracePeriodSeconds = m.Type == MonitorType.Heartbeat ? m.GracePeriodSeconds : DefaultGracePeriodSeconds
    };

    /// <summary>Business validation (type-specific fields + plan rules). Errors are added with the "Input." prefix,
    /// in the language of <paramref name="L"/>.</summary>
    public void Validate(Plan plan, ModelStateDictionary ms, IStringLocalizer L, string prefix = "Input.")
    {
        Name = Name?.Trim() ?? "";
        Target = Target?.Trim() ?? "";
        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword;
        HttpMethod = (HttpMethod ?? "GET").Trim().ToUpperInvariant();

        JsonPath = string.IsNullOrWhiteSpace(JsonPath) ? null : JsonPath.Trim();

        if (!Enum.IsDefined(Type))
            ms.AddModelError(prefix + nameof(Type), L["Invalid monitor type."]);

        if (Type != MonitorType.Heartbeat && Target.Length == 0)
            ms.AddModelError(prefix + nameof(Target), L["Target is required."]);
        else
            ValidateTypeFields(ms, L, prefix);

        if (Type == MonitorType.Heartbeat)
        {
            // Pings are expected at most every minute; the plan minimum applies on top.
            if (!PlanRules.IsHeartbeatIntervalAllowed(plan, IntervalSeconds))
                ms.AddModelError(prefix + nameof(IntervalSeconds),
                    L["This check interval is not available on your plan ({0}). Allowed: {1}.", plan.Name,
                        string.Join(", ", PlanRules.AllowedHeartbeatIntervals(plan).Select(Fmt.Interval))]);
            return; // no probe, so no timeout
        }

        if (!PlanRules.IsIntervalAllowed(plan, IntervalSeconds))
            ms.AddModelError(prefix + nameof(IntervalSeconds),
                L["This check interval is not available on your plan ({0}). Allowed: {1}.", plan.Name,
                    string.Join(", ", PlanRules.AllowedIntervals(plan).Select(Fmt.Interval))]);

        if (TimeoutSeconds >= IntervalSeconds)
            ms.AddModelError(prefix + nameof(TimeoutSeconds), L["The timeout must be shorter than the check interval."]);
    }

    private void ValidateTypeFields(ModelStateDictionary ms, IStringLocalizer L, string prefix)
    {
        switch (Type)
        {
            case MonitorType.Http:
            case MonitorType.Keyword:
            case MonitorType.JsonApi:
                if (!Uri.TryCreate(Target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host))
                    ms.AddModelError(prefix + nameof(Target), L["The target must be a valid URL starting with http:// or https://."]);
                if (!HttpMethods.Contains(HttpMethod))
                    ms.AddModelError(prefix + nameof(HttpMethod), L["The HTTP method must be GET, HEAD or POST."]);
                else if (HttpMethod == "HEAD" && Type != MonitorType.Http)
                    ms.AddModelError(prefix + nameof(HttpMethod), L["A HEAD request has no response body; use GET or POST for keyword and JSON monitors."]);
                if (string.IsNullOrWhiteSpace(ExpectedStatusCodes))
                    ExpectedStatusCodes = "200-299";
                else if (!StatusCodesRx.IsMatch(ExpectedStatusCodes))
                    ms.AddModelError(prefix + nameof(ExpectedStatusCodes), L["Expected status codes must look like \"200-299,301\"."]);
                if (!MonitorStateMachine.CertWarnDayChoices.Contains(CertWarnDays))
                    ms.AddModelError(prefix + nameof(CertWarnDays), L["Invalid certificate expiry warning."]);
                if (Type == MonitorType.Keyword && string.IsNullOrWhiteSpace(Keyword))
                    ms.AddModelError(prefix + nameof(Keyword), L["A keyword monitor needs the text to search for."]);
                if (Type == MonitorType.JsonApi) ValidateJsonAssertion(ms, L, prefix);
                break;
            case MonitorType.Dns:
                if (!IsDnsName(Target))
                    ms.AddModelError(prefix + nameof(Target), L["The target must be a host name without a scheme (e.g. example.com)."]);
                if (DnsRecordType is not { } rt || !DnsRecordTypes.Contains(rt))
                    ms.AddModelError(prefix + nameof(DnsRecordType), L["Choose a DNS record type."]);
                if (!string.IsNullOrWhiteSpace(ExpectedValue) && AssertionOperator is { } dop && !DnsOperators.Contains(dop))
                    ms.AddModelError(prefix + nameof(AssertionOperator), L["A DNS monitor can only check that a record equals or contains the expected value."]);
                break;
            case MonitorType.Heartbeat:
                if (GracePeriodSeconds < MinGracePeriodSeconds || GracePeriodSeconds > MaxGracePeriodSeconds)
                    ms.AddModelError(prefix + nameof(GracePeriodSeconds), L["The grace period must be between {0} and {1} seconds.", MinGracePeriodSeconds, MaxGracePeriodSeconds]);
                break;
            case MonitorType.Tcp:
            case MonitorType.Ping:
                if (!IsHostOrIp(Target))
                    ms.AddModelError(prefix + nameof(Target), L["The target must be a host name or IP address without a scheme (e.g. example.com)."]);
                if (Type == MonitorType.Tcp && (Port is null || Port < 1 || Port > 65535))
                    ms.AddModelError(prefix + nameof(Port), L["A TCP monitor needs a port between 1 and 65535."]);
                break;
        }
    }

    private void ValidateJsonAssertion(ModelStateDictionary ms, IStringLocalizer L, string prefix)
    {
        if (!Monitoring.Checks.JsonPath.TryParse(JsonPath, out _, out _))
            ms.AddModelError(prefix + nameof(JsonPath), L["Enter a JSON path like $.status or $.items[0].name (only .name, ['name'] and [index]; no wildcards or filters)."]);
        if (AssertionOperator is not { } op || !JsonOperators.Contains(op))
        {
            ms.AddModelError(prefix + nameof(AssertionOperator), L["Choose a condition."]);
            return;
        }
        if (op != Domain.Entities.AssertionOperator.Exists && string.IsNullOrEmpty(ExpectedValue))
            ms.AddModelError(prefix + nameof(ExpectedValue), L["Enter the expected value."]);
        else if (op is Domain.Entities.AssertionOperator.LessThan or Domain.Entities.AssertionOperator.GreaterThan && !JsonAssertion.TryParseNumber(ExpectedValue, out _))
            ms.AddModelError(prefix + nameof(ExpectedValue), L["For less than / greater than the expected value must be a number, e.g. 250 or 0.5."]);
    }

    /// <summary>
    /// Friendly SSRF pre-check: rejects literal private/internal IPs and names like "localhost" so the user gets a form
    /// error. The real guard is at connect time (TargetGuard), which also covers DNS names and redirects.
    /// Skipped when Monitoring:AllowPrivateTargets is set.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (context.GetService(typeof(IOptions<MonitoringOptions>)) is IOptions<MonitoringOptions> { Value.AllowPrivateTargets: true }) yield break;

        // Dns only sends the name to the operator's resolvers and Heartbeat makes no connection at all.
        if (Type is MonitorType.Dns or MonitorType.Heartbeat) yield break;
        var target = Target?.Trim();
        var host = IsHttpType(Type)
            ? (Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.Host : null)
            : target;
        if (!TargetGuard.IsObviouslyInternalHost(host)) yield break;

        var L = (Text<AppResource>)context.GetService(typeof(Text<AppResource>))!;
        yield return new ValidationResult(L["This address points to a private or internal network and cannot be monitored."], [nameof(Target)]);
    }

    /// <summary>A DNS name the Dns checker can query (also _service labels and IDN names; no IP literals, URLs or paths).</summary>
    public static bool IsDnsName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("://") || value.Contains('/')) return false;
        try { Monitoring.Checks.Dns.DnsMessage.NormalizeName(value); return true; }
        catch (ArgumentException) { return false; }
    }

    public static bool IsHostOrIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("://") || value.Contains('/')) return false;
        return IPAddress.TryParse(value, out _) || HostRx.IsMatch(value);
    }

    /// <summary>Copies user-editable fields to the entity, clearing fields that don't apply to the type.</summary>
    public void ApplyTo(MonitorEntity m)
    {
        var isHttp = IsHttpType(Type);
        var isHeartbeat = Type == MonitorType.Heartbeat;
        if (m.Target != Target || m.Type != Type)
        {
            // The stored certificate belonged to the old target.
            m.CertExpiresAtUtc = null;
            m.CertSubject = null;
            m.CertIssuer = null;
            m.CertError = null;
            m.CertCheckedAtUtc = null;
            m.CertWarningSentForUtc = null;
            m.CertWarningSentDays = null;
        }
        m.CertWarnDays = isHttp ? CertWarnDays : 0;
        m.Name = Name;
        if (isHeartbeat)
        {
            if (m.Type != MonitorType.Heartbeat || m.HeartbeatTokenHash is null)
            {
                IssuedHeartbeatToken = HeartbeatTokens.Issue(m); // also sets the display Target
                m.LastHeartbeatAtUtc = null;
                m.LastHeartbeatFailed = false;
                m.LastHeartbeatMessage = null;
            }
        }
        else
        {
            m.Target = Target;
            m.HeartbeatTokenHash = null;
            m.HeartbeatTokenPrefix = null;
            m.LastHeartbeatAtUtc = null;
            m.LastHeartbeatFailed = false;
            m.LastHeartbeatMessage = null;
        }
        m.Type = Type;
        m.Port = Type == MonitorType.Tcp ? Port : null;
        m.Keyword = Type == MonitorType.Keyword ? Keyword : null;
        m.HttpMethod = isHttp ? HttpMethod : "GET";
        m.ExpectedStatusCodes = isHttp ? ExpectedStatusCodes ?? "200-299" : "200-299";
        m.IntervalSeconds = IntervalSeconds;
        m.TimeoutSeconds = TimeoutSeconds;
        // Heartbeats: the grace period is the tolerance, one overdue (or failed) ping is enough.
        m.FailureThreshold = isHeartbeat ? 1 : FailureThreshold;
        m.GracePeriodSeconds = isHeartbeat ? GracePeriodSeconds : 0;
        m.DnsRecordType = Type == MonitorType.Dns ? DnsRecordType : null;
        m.JsonPath = Type == MonitorType.JsonApi ? JsonPath : null;
        var dnsExpected = Type == MonitorType.Dns && !string.IsNullOrWhiteSpace(ExpectedValue) ? ExpectedValue.Trim() : null;
        m.AssertionOperator = Type switch
        {
            MonitorType.JsonApi => AssertionOperator,
            MonitorType.Dns when dnsExpected is not null => AssertionOperator ?? Domain.Entities.AssertionOperator.Equal,
            _ => null
        };
        m.ExpectedValue = Type switch
        {
            MonitorType.JsonApi when AssertionOperator != Domain.Entities.AssertionOperator.Exists => ExpectedValue,
            MonitorType.Dns => dnsExpected,
            _ => null
        };
    }
}
