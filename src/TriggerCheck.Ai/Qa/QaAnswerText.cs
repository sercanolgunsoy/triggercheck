using System.Globalization;
using System.Text.RegularExpressions;
using TriggerCheck.Ai.Interpretation;

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

/// <summary>
/// Hook for checking that the numbers of an answer come from the data the model was given (AI-7 acceptance: "numbers in
/// answers come from tool results"). The default uses <see cref="AiNumberGrounding"/>; a shared grounding engine can
/// replace the registration without touching the Q&amp;A loop.
/// </summary>
public interface IQaAnswerGrounding
{
    /// <summary>The numbers / dates of <paramref name="answer"/> (reference tokens already removed) not found in <paramref name="evidence"/>.</summary>
    IReadOnlyList<string> Ungrounded(string answer, string evidence);
}

public sealed class QaNumberGrounding : IQaAnswerGrounding
{
    public IReadOnlyList<string> Ungrounded(string answer, string evidence) => AiNumberGrounding.Ungrounded(answer, evidence).Distinct().ToList();
}
