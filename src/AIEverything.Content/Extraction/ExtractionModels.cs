namespace AIEverything.Content.Extraction;

public sealed record ExtractionRequest(
    string Path,
    long MaxBytes = 20 * 1024 * 1024,
    int MaxChars = 2_000_000);

public sealed record ExtractionResult(
    string Text,
    bool Truncated,
    int Characters,
    IReadOnlyList<ExtractedTextBlock>? Blocks = null);

public sealed record ExtractedTextBlock(
    int Ordinal,
    string Text,
    string LocationLabel,
    string? HeadingPath = null);

public sealed record WorkerExtractionError(
    string Code,
    string Message,
    string CorrectiveAction);

public sealed record WorkerExtractionResponse(
    bool Success,
    ExtractionResult? Result,
    WorkerExtractionError? Error);

internal static class ExtractionResultFactory
{
    internal static ExtractionResult Create(string text, int maxChars)
    {
        if (maxChars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars));
        }

        var truncated = text.Length > maxChars;
        var result = truncated ? text[..maxChars] : text;
        return new ExtractionResult(result, truncated, result.Length);
    }

    internal static ExtractionResult Create(
        IReadOnlyList<ExtractedTextBlock> blocks,
        int maxChars)
    {
        if (maxChars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars));
        }

        var accepted = new List<ExtractedTextBlock>(blocks.Count);
        var remaining = maxChars;
        var truncated = false;
        foreach (var block in blocks)
        {
            var separatorLength = accepted.Count == 0 ? 0 : Environment.NewLine.Length;
            if (remaining <= separatorLength)
            {
                truncated = true;
                break;
            }

            remaining -= separatorLength;
            if (block.Text.Length <= remaining)
            {
                accepted.Add(block);
                remaining -= block.Text.Length;
                continue;
            }

            accepted.Add(block with { Text = block.Text[..remaining] });
            truncated = true;
            remaining = 0;
            break;
        }

        var text = string.Join(Environment.NewLine, accepted.Select(block => block.Text));
        return new ExtractionResult(text, truncated, text.Length, accepted);
    }
}
