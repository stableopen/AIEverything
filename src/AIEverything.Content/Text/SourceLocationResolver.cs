using System.Text;
using System.Text.Json;
using AIEverything.Content.Extraction;

namespace AIEverything.Content.Text;

public sealed record SourceLocationHit(
    int StartLine,
    int EndLine,
    string Snippet,
    string? HeadingPath = null,
    string? JsonPath = null,
    string? LocationLabel = null);

public static class SourceLocationResolver
{
    public static IReadOnlyList<SourceLocationHit> Resolve(
        string content,
        string extension,
        IReadOnlyList<string> queryTerms,
        int maxHits = 3) => Resolve(content, extension, queryTerms, null, maxHits);

    public static IReadOnlyList<SourceLocationHit> Resolve(
        string content,
        string extension,
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<ExtractedTextBlock>? blocks,
        int maxHits = 3)
    {
        if (string.Equals(extension, "docx", StringComparison.OrdinalIgnoreCase) &&
            blocks is { Count: > 0 })
        {
            return ResolveBlocks(blocks, queryTerms, maxHits);
        }

        if (string.Equals(extension, "json", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveJson(content, queryTerms, maxHits);
        }

        var lines = SplitLines(content);
        var headings = extension.Equals("md", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals("markdown", StringComparison.OrdinalIgnoreCase)
            ? BuildHeadingPaths(lines)
            : null;
        return FindTextHits(lines, queryTerms, maxHits, headings);
    }

    private static IReadOnlyList<SourceLocationHit> ResolveBlocks(
        IReadOnlyList<ExtractedTextBlock> blocks,
        IReadOnlyList<string> queryTerms,
        int maxHits)
    {
        if (queryTerms.Count == 0 || maxHits < 1)
        {
            return [];
        }

        if (queryTerms.Count > 1)
        {
            (int Start, int End)? best = null;
            for (var start = 0; start < blocks.Count; start++)
            {
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var end = start; end < blocks.Count; end++)
                {
                    foreach (var term in queryTerms)
                    {
                        if (blocks[end].Text.Contains(term, StringComparison.OrdinalIgnoreCase))
                        {
                            found.Add(term);
                        }
                    }

                    if (found.Count != queryTerms.Count)
                    {
                        continue;
                    }

                    if (best is null || end - start < best.Value.End - best.Value.Start)
                    {
                        best = (start, end);
                    }
                    break;
                }
            }

            if (best is { } window)
            {
                var representative = blocks[window.End];
                return [new SourceLocationHit(
                    representative.Ordinal,
                    representative.Ordinal,
                    string.Join(Environment.NewLine, blocks
                        .Skip(window.Start)
                        .Take(window.End - window.Start + 1)
                        .Select(block => block.Text)),
                    representative.HeadingPath,
                    LocationLabel: representative.LocationLabel)];
            }
        }

        return blocks
            .Where(block => queryTerms.Any(term =>
                block.Text.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Take(maxHits)
            .Select(block => new SourceLocationHit(
                block.Ordinal,
                block.Ordinal,
                block.Text,
                block.HeadingPath,
                LocationLabel: block.LocationLabel))
            .ToArray();
    }

    private static IReadOnlyList<SourceLocationHit> ResolveJson(
        string content,
        IReadOnlyList<string> queryTerms,
        int maxHits)
    {
        try
        {
            var utf8 = Encoding.UTF8.GetBytes(content);
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var stack = new Stack<JsonFrame>();
            string? pendingProperty = null;
            var hits = new List<SourceLocationHit>();
            while (reader.Read() && hits.Count < maxHits)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        stack.Push(new JsonFrame(ConsumeValuePath(stack, pendingProperty), false));
                        pendingProperty = null;
                        break;
                    case JsonTokenType.StartArray:
                        stack.Push(new JsonFrame(ConsumeValuePath(stack, pendingProperty), true));
                        pendingProperty = null;
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (stack.Count > 0) stack.Pop();
                        pendingProperty = null;
                        break;
                    case JsonTokenType.PropertyName:
                        pendingProperty = reader.GetString();
                        if (Matches(pendingProperty, queryTerms))
                        {
                            AddJsonHit(content, utf8, reader.TokenStartIndex,
                                BuildChildPath(stack, pendingProperty), queryTerms, hits);
                        }
                        break;
                    default:
                        var value = GetJsonValue(ref reader);
                        var path = BuildChildPath(stack, pendingProperty);
                        if (Matches(value, queryTerms))
                        {
                            AddJsonHit(content, utf8, reader.TokenStartIndex, path, queryTerms, hits);
                        }
                        pendingProperty = null;
                        AdvanceArray(stack);
                        break;
                }
            }
            return hits.Count > 0 ? hits : FindTextHits(SplitLines(content), queryTerms, maxHits, null);
        }
        catch (JsonException)
        {
            return FindTextHits(
                SplitLines(content), queryTerms, maxHits, null,
                "Invalid JSON · text fallback");
        }
    }

    private static void AddJsonHit(
        string content,
        byte[] utf8,
        long byteOffset,
        string path,
        IReadOnlyList<string> queryTerms,
        List<SourceLocationHit> hits)
    {
        var charOffset = Encoding.UTF8.GetCharCount(utf8, 0, checked((int)byteOffset));
        var line = 1 + content.AsSpan(0, charOffset).Count('\n');
        if (hits.Any(hit => hit.StartLine == line && hit.JsonPath == path)) return;
        var lines = SplitLines(content);
        hits.Add(new SourceLocationHit(
            line, line, BuildSnippet(lines, line - 1, queryTerms),
            JsonPath: path,
            LocationLabel: $"{path} · line {line}"));
    }

    private static string GetJsonValue(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString() ?? string.Empty,
        JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        JsonTokenType.Null => "null",
        _ => string.Empty
    };

    private static string BuildChildPath(Stack<JsonFrame> stack, string? property)
    {
        var parent = stack.TryPeek(out var frame) ? frame.Path : "$";
        if (!string.IsNullOrEmpty(property))
        {
            return IsSimpleProperty(property)
                ? $"{parent}.{property}"
                : $"{parent}['{property.Replace("'", "\\'", StringComparison.Ordinal)}']";
        }
        return stack.TryPeek(out frame) && frame.IsArray ? $"{parent}[{frame.Index}]" : parent;
    }

    private static string ConsumeValuePath(Stack<JsonFrame> stack, string? property)
    {
        var path = BuildChildPath(stack, property);
        if (property is null) AdvanceArray(stack);
        return path;
    }

    private static void AdvanceArray(Stack<JsonFrame> stack)
    {
        if (stack.TryPeek(out var frame) && frame.IsArray) frame.Index++;
    }

    private static bool IsSimpleProperty(string value) =>
        value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');

    private static IReadOnlyList<SourceLocationHit> FindTextHits(
        string[] lines,
        IReadOnlyList<string> queryTerms,
        int maxHits,
        string?[]? headingPaths,
        string? fixedLabel = null)
    {
        var hits = new List<SourceLocationHit>();
        if (queryTerms.Count > 1)
        {
            var compact = FindCompactAllTermWindow(lines, queryTerms);
            if (compact is { } window)
            {
                var heading = headingPaths?[window.Start];
                var location = fixedLabel ?? (heading is null
                    ? $"lines {window.Start + 1}-{window.End + 1}"
                    : $"{heading} · lines {window.Start + 1}-{window.End + 1}");
                hits.Add(new SourceLocationHit(
                    window.Start + 1,
                    window.End + 1,
                    BuildWindowSnippet(lines, window.Start, window.End),
                    heading,
                    LocationLabel: location));
                return hits;
            }
        }
        for (var index = 0; index < lines.Length && hits.Count < maxHits; index++)
        {
            if (!Matches(lines[index], queryTerms)) continue;
            var startLine = index + 1;
            var endLine = startLine;
            var heading = headingPaths?[index];
            var location = fixedLabel ?? (heading is null
                ? $"lines {startLine}-{endLine}"
                : $"{heading} · lines {startLine}-{endLine}");
            hits.Add(new SourceLocationHit(
                startLine, endLine, BuildSnippet(lines, index, queryTerms), heading,
                LocationLabel: location));
        }
        return hits;
    }

    private static (int Start, int End)? FindCompactAllTermWindow(
        string[] lines,
        IReadOnlyList<string> queryTerms)
    {
        (int Start, int End)? best = null;
        for (var start = 0; start < lines.Length; start++)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var end = start; end < lines.Length; end++)
            {
                foreach (var term in queryTerms)
                {
                    if (lines[end].Contains(term, StringComparison.OrdinalIgnoreCase)) found.Add(term);
                }
                if (found.Count != queryTerms.Count) continue;
                if (best is null || end - start < best.Value.End - best.Value.Start)
                {
                    best = (start, end);
                }
                break;
            }
        }
        return best;
    }

    private static string BuildWindowSnippet(string[] lines, int start, int end)
    {
        var first = Math.Max(0, start - 1);
        var last = Math.Min(lines.Length - 1, end + 1);
        return string.Join(Environment.NewLine, lines[first..(last + 1)]).Trim();
    }

    private static string BuildSnippet(string[] lines, int hitLine, IReadOnlyList<string> queryTerms)
    {
        var first = Math.Max(0, hitLine - 1);
        var last = Math.Min(lines.Length - 1, hitLine + 1);
        return string.Join(Environment.NewLine, lines[first..(last + 1)]).Trim();
    }

    private static string?[] BuildHeadingPaths(string[] lines)
    {
        var levels = new string?[6];
        var result = new string?[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimStart();
            var hashes = line.TakeWhile(character => character == '#').Count();
            if (hashes is >= 1 and <= 6 && line.Length > hashes && char.IsWhiteSpace(line[hashes]))
            {
                levels[hashes - 1] = line[(hashes + 1)..].Trim();
                for (var deeper = hashes; deeper < levels.Length; deeper++) levels[deeper] = null;
            }
            result[index] = string.Join(" > ", levels.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (result[index]?.Length == 0) result[index] = null;
        }
        return result;
    }

    private static bool Matches(string? value, IReadOnlyList<string> terms) =>
        !string.IsNullOrEmpty(value) && terms.Any(term =>
            value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string[] SplitLines(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private sealed class JsonFrame(string path, bool isArray)
    {
        internal string Path { get; } = path;
        internal bool IsArray { get; } = isArray;
        internal int Index { get; set; }
    }
}
