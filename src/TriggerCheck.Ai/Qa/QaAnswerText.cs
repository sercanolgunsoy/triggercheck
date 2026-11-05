using System.Globalization;
using System.Text.RegularExpressions;

namespace TriggerCheck.Ai.Qa;

public enum QaSegmentKind
{
    Text,
    Monitor,
    Incident
}

/// <summary>A piece of an answer: plain text, or a reference to a monitor / incident (<see cref="Id"/>).</summary>
public sealed record QaSegment(QaSegmentKind Kind, string Text, int? Id = null);

/// <summary>
/// Reference tokens of the Q&amp;A answers. The model never sees monitor names; it cites records as
/// <c>[[monitor:12]]</c> / <c>[[incident:34]]</c> (the tools hand it these tokens as "ref"). The page turns a token into
/// a link with the record's name only after loading the record for the signed-in customer, so a token with another
/// tenant's id renders as plain "unknown record" text. Everything else in the answer is plain, HTML-encoded text.
/// </summary>
public static partial class QaAnswerText
{
    public static string MonitorRef(int id) => "[[monitor:" + id.ToString(CultureInfo.InvariantCulture) + "]]";
    public static string IncidentRef(int id) => "[[incident:" + id.ToString(CultureInfo.InvariantCulture) + "]]";

    [GeneratedRegex(@"\[\[\s*(monitor|incident)\s*:\s*#?(\d{1,9})\s*\]\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reference();

    public static List<QaSegment> Parse(string? text)
    {
        var result = new List<QaSegment>();
        if (string.IsNullOrEmpty(text)) return result;
        var pos = 0;
        foreach (Match m in Reference().Matches(text))
        {
            if (m.Index > pos) result.Add(new QaSegment(QaSegmentKind.Text, text[pos..m.Index]));
            var kind = m.Groups[1].Value.Equals("monitor", StringComparison.OrdinalIgnoreCase) ? QaSegmentKind.Monitor : QaSegmentKind.Incident;
            result.Add(new QaSegment(kind, m.Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) result.Add(new QaSegment(QaSegmentKind.Text, text[pos..]));
        return result;
    }

    /// <summary>The answer without reference tokens (their ids are not numbers of the answer).</summary>
    public static string StripReferences(string? text) => string.IsNullOrEmpty(text) ? "" : Reference().Replace(text, " ");
}

/// <summary>What an answer may draw its numbers from (AI-7): the tool results of this question, the questions of the conversation and the earlier answers.</summary>
/// <param name="ToolResults">JSON contents of the tool results.</param>
/// <param name="Questions">The current and earlier questions (as sent).</param>
/// <param name="EarlierAnswers">Earlier answers of the conversation (as sent), so a follow-up may repeat their figures.</param>
/// <param name="Language">"en" / "tr": how "1,234" / "1.234" are read.</param>
public sealed record QaGroundingEvidence(IReadOnlyList<string> ToolResults, IReadOnlyList<string> Questions, IReadOnlyList<string> EarlierAnswers,
    string Language = "en");

/// <summary>
/// Hook for checking that the numbers of an answer come from the data the model was given (AI-7 acceptance: "numbers in
/// answers come from tool results"). Warn-only: the answer is shown with the unverified numbers listed, nothing is
/// stripped. Default: the shared <see cref="Grounding.NumberGrounding"/> engine (<see cref="QaNumberGrounding"/>).
/// </summary>
public interface IQaAnswerGrounding
{
    /// <summary>The numbers / dates of <paramref name="answer"/> (reference tokens already removed) the evidence does not back.</summary>
    IReadOnlyList<string> Ungrounded(string answer, QaGroundingEvidence evidence);
}

/// <summary>
/// AI-7 on the shared engine. Facts come from the tool results' JSON by property (the key decides the unit:
/// "avg_response_ms" is milliseconds, "availability_percent" a percentage, "interval_seconds" seconds; ids, refs, ports
/// and untrusted text add nothing, so an id or a port never backs a count, a duration or a percentage), plus the
/// numbers written in the questions and earlier answers of the conversation with the unit they were written with.
/// Rounding (within 5 %) and time unit conversion are accepted; number words are not checked (warn-only feature).
/// </summary>
public sealed partial class QaNumberGrounding : IQaAnswerGrounding
{
    public static readonly Grounding.GroundingPolicy Policy = new(AllowRounding: true, ConvertTimeUnits: true, RejectNumberWords: false);

    public IReadOnlyList<string> Ungrounded(string answer, QaGroundingEvidence evidence) =>
        Grounding.NumberGrounding.Ungrounded(answer, Facts(evidence), Policy)
            .Select(x => NumberPart().Match(x) is { Success: true } m ? m.Value.TrimEnd('.', ',') : x)
            .Distinct().ToList();

    public static Grounding.GroundingFacts Facts(QaGroundingEvidence evidence)
    {
        var facts = new Grounding.GroundingFacts { Language = evidence.Language };
        foreach (var json in evidence.ToolResults)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                Walk(facts, "", doc.RootElement);
            }
            catch (System.Text.Json.JsonException)
            {
                // not JSON: adds nothing
            }
        }
        foreach (var t in evidence.Questions.Concat(evidence.EarlierAnswers)) Grounding.NumberGrounding.AddFactsFromText(facts, QaAnswerText.StripReferences(t));
        return facts;
    }

    private static bool Ignored(string key)
    {
        var k = key.ToLowerInvariant();
        return k is "id" or "ref" or "port" or "limit" || k.EndsWith("_id", StringComparison.Ordinal) || k.EndsWith("_ref", StringComparison.Ordinal)
               || k.EndsWith("_untrusted", StringComparison.Ordinal) || k.Contains("cause", StringComparison.Ordinal);
    }

    private static void Walk(Grounding.GroundingFacts facts, string key, System.Text.Json.JsonElement e)
    {
        switch (e.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    if (!Ignored(p.Name)) Walk(facts, p.Name, p.Value);
                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) Walk(facts, key, x);
                break;
            case System.Text.Json.JsonValueKind.Number when e.TryGetDecimal(out var d):
                facts.AddValue(key, d, null, null);
                break;
            case System.Text.Json.JsonValueKind.String:
                // Only dates / times and status codes from strings; other text (names, messages) grounds nothing.
                var s = e.GetString() ?? "";
                if (key.Contains("status", StringComparison.OrdinalIgnoreCase) || DateLike().IsMatch(s)) facts.AddValue(key, s, null, null);
                break;
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")]
    private static partial Regex DateLike();

    [GeneratedRegex(@"[0-9][0-9.,]*")]
    private static partial Regex NumberPart();
}
