namespace AIEverything.Content.Extraction;

public interface ITextExtractor
{
    Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken);
}
