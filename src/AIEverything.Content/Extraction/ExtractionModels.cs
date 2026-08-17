namespace AIEverything.Content.Extraction;

public sealed record ExtractionRequest(
    string Path,
    long MaxBytes = 20 * 1024 * 1024,
    int MaxChars = 2_000_000);

public sealed record ExtractionResult(
    string Text,
    bool Truncated,
    int Characters);

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
}
