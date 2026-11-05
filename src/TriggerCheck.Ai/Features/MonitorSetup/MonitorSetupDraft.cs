using System.Text.Json;
using System.Text.Json.Serialization;

namespace TriggerCheck.Ai.Features.MonitorSetup;

/// <summary>
/// The model's answer as parsed: only the known fields of <see cref="MonitorSetupPrompts.Schema"/> are read, any other
/// property is ignored, and a value of the wrong JSON type counts as null. Values still contain the placeholders and
/// are not validated yet; <see cref="MonitorSetupForm.Build"/> turns a draft into form values. Stored as the decision's
/// OutputJson (placeholders only, never the user's links or quoted text).
/// </summary>
public sealed record MonitorSetupDraft
{
    public const int MaxTextChars = 600;

    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "clarify";
    [JsonPropertyName("clarification")] public string? Clarification { get; init; }
    [JsonPropertyName("explanation")] public string? Explanation { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("target")] public string? Target { get; init; }
    [JsonPropertyName("port")] public long? Port { get; init; }
    [JsonPropertyName("keyword")] public string? Keyword { get; init; }
    [JsonPropertyName("http_method")] public string? HttpMethod { get; init; }
    [JsonPropertyName("expected_status_codes")] public string? ExpectedStatusCodes { get; init; }
    [JsonPropertyName("interval_seconds")] public long? IntervalSeconds { get; init; }
    [JsonPropertyName("timeout_seconds")] public long? TimeoutSeconds { get; init; }
    [JsonPropertyName("failure_threshold")] public long? FailureThreshold { get; init; }
    [JsonPropertyName("dns_record_type")] public string? DnsRecordType { get; init; }
    [JsonPropertyName("json_path")] public string? JsonPath { get; init; }
    [JsonPropertyName("assertion_operator")] public string? AssertionOperator { get; init; }
    [JsonPropertyName("expected_value")] public string? ExpectedValue { get; init; }
    [JsonPropertyName("grace_period_seconds")] public long? GracePeriodSeconds { get; init; }

    public bool IsClarification => Outcome != "form";

    /// <summary>Null when the text is not a JSON object or has no valid "outcome".</summary>
    public static MonitorSetupDraft? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            var outcome = Str(o, "outcome");
            if (outcome is not ("form" or "clarify")) return null;
            return new MonitorSetupDraft
            {
                Outcome = outcome,
                Clarification = Str(o, "clarification"),
                Explanation = Str(o, "explanation"),
                Type = Str(o, "type"),
                Name = Str(o, "name"),
                Target = Str(o, "target"),
                Port = Int(o, "port"),
                Keyword = Str(o, "keyword"),
                HttpMethod = Str(o, "http_method"),
                ExpectedStatusCodes = Str(o, "expected_status_codes"),
                IntervalSeconds = Int(o, "interval_seconds"),
                TimeoutSeconds = Int(o, "timeout_seconds"),
                FailureThreshold = Int(o, "failure_threshold"),
                DnsRecordType = Str(o, "dns_record_type"),
                JsonPath = Str(o, "json_path"),
                AssertionOperator = Str(o, "assertion_operator"),
                ExpectedValue = Str(o, "expected_value"),
                GracePeriodSeconds = Int(o, "grace_period_seconds"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    private static string? Str(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length > MaxTextChars ? s[..MaxTextChars] : s;
    }

    private static long? Int(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)) return (long)Math.Clamp(Math.Round(d), -1e9, 1e9);
        return null;
    }
}
