using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TriggerCheck.Ai.Context;

namespace TriggerCheck.Ai.Grounding;

/// <summary>
/// Word-level rules shared by the number grounding engine (AI-5, AI-7) and the AI-6 narrative contract: spelled-out
/// numbers and multipliers (EN / TR), and links / contacts (URLs, domains incl. obfuscated forms, e-mail, phone).
/// </summary>
public static partial class TextRules
{
    /// <summary>
    /// Spelled-out numbers, fractions, ordinals and multipliers. English "one" is included (also as a pronoun: some
    /// over-rejection is accepted). Turkish "bir" is not (it is also the indefinite article); the Turkish list is applied
    /// only to Turkish text, since "on" (ten) is an English preposition. Whole words, Turkish also with an apostrophe
    /// suffix ("yedi'si").
    /// </summary>
    [GeneratedRegex(@"(?i)(?<![\p{L}\p{N}])(?:zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|" +
        @"sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundreds?|thousands?|millions?|" +
        @"billions?|dozens?|half|halves|quarters?|twice|thrice|double[ds]?|triple[ds]?|quadruple[ds]?|tenfold|hundredfold|" +
        @"first|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth)(?![\p{L}\p{N}])")]
    private static partial Regex EnglishNumberWord();

    [GeneratedRegex(@"(?i)(?<![\p{L}\p{N}])(?:sıfır|iki|üç|dört|beş|altı|yedi|sekiz|dokuz|on|yirmi|otuz|kırk|elli|altmış|yetmiş|seksen|doksan|" +
        @"yüz|bin|milyon|milyar|yarım|yarısı|çeyrek|birinci|ikinci|üçüncü|dördüncü|beşinci)(?:'\p{L}+)?(?![\p{L}\p{N}])")]
    private static partial Regex TurkishNumberWord();

    /// <summary>"x75", "3x", "2×": multipliers glued to a number.</summary>
    [GeneratedRegex(@"(?i)(?<![\p{L}])[x×]\s?[0-9]|[0-9]\s?[x×](?![\p{L}])")]
    private static partial Regex Multiplier();

    /// <summary>Spelled-out numbers, fractions or multipliers in <paramref name="text"/> (empty: none).</summary>
    /// <param name="language">"tr" also applies the Turkish list.</param>
    public static List<string> NumberWords(string text, string language)
    {
        var s = Normalize(text);
        var words = EnglishNumberWord().Matches(s).Select(m => m.Value);
        if (language.StartsWith("tr", StringComparison.OrdinalIgnoreCase)) words = words.Concat(TurkishNumberWord().Matches(s).Select(m => m.Value));
        return words.Concat(Multiplier().Matches(s).Select(m => m.Value)).ToList();
    }

    /// <summary>Digits of any script, vulgar fractions, superscripts, Roman numeral characters.</summary>
    public static bool HasAnyDigit(string text) =>
        text.Any(c => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.OtherNumber or UnicodeCategory.LetterNumber);

    /// <summary>NFKC (fullwidth forms to ASCII), ideographic / fullwidth full stops to ".", invisible characters removed.</summary>
    public static string Normalize(string text)
    {
        var s = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '。' or '．' or '｡' or '․' or '‧') sb.Append('.');
            else if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue; // zero-width characters
            else sb.Append(c);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"(?i)\b(?:h(?:tt|xx)ps?|ftp)\s*(?::|\[:\])\s*/{2}|\bwww\s*(?:\.|\[\.\]|\(\.\))|(?:\[\.\]|\(\.\)|\{\.\}|\[dot\]|\(dot\)|\s+dot\s+)\s*\p{L}{2,24}\b")]
    private static partial Regex ObfuscatedLink();

    [GeneratedRegex(@"(?<![\p{L}\p{N}@.-])(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,61}[\p{L}\p{N}])?\.)+(?:\p{L}{2,24}|xn--[a-z0-9-]+)(?![\p{L}\p{N}-])(?:/\S*)?")]
    private static partial Regex Domain();

    [GeneratedRegex(@"(?i)\b(?:https?|ftp)://\S+")]
    private static partial Regex Url();

    /// <summary>
    /// URL, domain name (also "example[.]com", "evil dot com", "hxxp://", fullwidth / ideographic dots, IDN and
    /// punycode), e-mail address or phone number in <paramref name="text"/>.
    /// </summary>
    public static bool HasLinkOrContact(string text)
    {
        var s = Normalize(text);
        if (Url().IsMatch(s) || ObfuscatedLink().IsMatch(s) || Domain().IsMatch(s)) return true;
        var masked = AiMasker.MaskText(s, longTokens: false);
        return masked.Contains(AiMasker.Email, StringComparison.Ordinal) || masked.Contains(AiMasker.Phone, StringComparison.Ordinal);
    }
}
