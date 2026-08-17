using System.Globalization;
using System.Text;
using AIEverything.Content.Errors;

namespace AIEverything.Content.Text;

public static class ContentTokenizer
{
    private static readonly char[] EdgePunctuation = ['.', '-', '_'];

    public static string TokenizeForIndex(string text) =>
        string.Join(' ', Tokenize(text));

    public static IReadOnlyList<string> GetQueryTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw TooBroad();
        }

        var normalized = query.Normalize(NormalizationForm.FormKC).Trim();
        var runes = normalized.EnumerateRunes().ToArray();
        if (runes.Length == 1 && IsCjk(runes[0]))
        {
            throw TooBroad();
        }

        var terms = Tokenize(normalized)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (terms.Length == 0)
        {
            throw TooBroad();
        }

        return terms;
    }

    public static string BuildMatchQuery(string query) =>
        string.Join(
            " AND ",
            GetQueryTerms(query).Select(term => $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));

    private static IReadOnlyList<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var tokens = new List<string>();
        var identifier = new StringBuilder();
        var cjkRun = new List<Rune>();

        void FlushIdentifier()
        {
            if (identifier.Length == 0)
            {
                return;
            }

            var token = identifier.ToString().Trim(EdgePunctuation);
            if (token.Any(char.IsLetterOrDigit))
            {
                tokens.Add(token);
            }

            identifier.Clear();
        }

        void FlushCjk()
        {
            if (cjkRun.Count == 1)
            {
                tokens.Add(cjkRun[0].ToString());
            }
            else
            {
                for (var index = 0; index < cjkRun.Count - 1; index++)
                {
                    tokens.Add(cjkRun[index].ToString() + cjkRun[index + 1]);
                }
            }

            cjkRun.Clear();
        }

        foreach (var rune in text.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (IsCjk(rune))
            {
                FlushIdentifier();
                cjkRun.Add(rune);
                continue;
            }

            FlushCjk();
            if (IsIdentifierRune(rune))
            {
                identifier.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else
            {
                FlushIdentifier();
            }
        }

        FlushIdentifier();
        FlushCjk();
        return tokens;
    }

    private static bool IsIdentifierRune(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.UppercaseLetter or
               UnicodeCategory.LowercaseLetter or
               UnicodeCategory.TitlecaseLetter or
               UnicodeCategory.ModifierLetter or
               UnicodeCategory.OtherLetter or
               UnicodeCategory.DecimalDigitNumber or
               UnicodeCategory.LetterNumber or
               UnicodeCategory.OtherNumber or
               UnicodeCategory.NonSpacingMark or
               UnicodeCategory.SpacingCombiningMark ||
               rune.Value is '_' or '-' or '.' or '+' or '#';
    }

    private static bool IsCjk(Rune rune) => rune.Value is
        >= 0x3400 and <= 0x4DBF or
        >= 0x4E00 and <= 0x9FFF or
        >= 0xF900 and <= 0xFAFF or
        >= 0x3040 and <= 0x30FF or
        >= 0xAC00 and <= 0xD7AF;

    private static ContentIndexException TooBroad() => new(
        ContentErrorCodes.QueryTooBroad,
        "Content query is empty or too broad.",
        "Provide an English term, a number, or at least two CJK characters.");
}
