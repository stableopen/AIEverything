using AIEverything.Content.Errors;

namespace AIEverything.Content.Extraction;

public sealed class CompositeTextExtractor : ITextExtractor
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log",
        ".ini", ".config", ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java",
        ".go", ".rs", ".sql", ".ps1", ".sh", ".bat"
    };

    private static readonly HashSet<string> OpenXmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx"
    };

    private readonly ITextExtractor _plainText;
    private readonly ITextExtractor _openXml;
    private readonly ITextExtractor _pdf;

    public CompositeTextExtractor(
        ITextExtractor plainText,
        ITextExtractor openXml,
        ITextExtractor pdf)
    {
        _plainText = plainText;
        _openXml = openXml;
        _pdf = pdf;
    }

    public static IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(
            TextExtensions.Concat(OpenXmlExtensions).Append(".pdf"),
            StringComparer.OrdinalIgnoreCase);

    public static CompositeTextExtractor CreateDefault() =>
        new(new PlainTextExtractor(), new OpenXmlTextExtractor(), new PdfTextExtractor());

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Path.IsPathFullyQualified(request.Path) || !File.Exists(request.Path))
        {
            throw new ContentIndexException(
                ContentErrorCodes.ExtractionFailed,
                $"File does not exist or is not an absolute path: {request.Path}",
                "Choose an existing eligible local file.");
        }

        var info = new FileInfo(request.Path);
        if (info.Length > request.MaxBytes)
        {
            throw new ContentIndexException(
                ContentErrorCodes.FileTooLarge,
                $"File exceeds the {request.MaxBytes}-byte extraction limit: {request.Path}",
                "Raise the explicit limit or choose a smaller file.");
        }

        var extension = info.Extension;
        var extractor = TextExtensions.Contains(extension)
            ? _plainText
            : OpenXmlExtensions.Contains(extension)
                ? _openXml
                : extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? _pdf
                    : throw new ContentIndexException(
                        ContentErrorCodes.UnsupportedFileType,
                        $"File type is not supported: {extension}",
                        "Choose a supported text, PDF, DOCX, XLSX, or PPTX file.");

        try
        {
            return await extractor.ExtractAsync(request, cancellationToken);
        }
        catch (ContentIndexException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ContentIndexException(
                ContentErrorCodes.ExtractionFailed,
                $"Document extraction failed: {request.Path}",
                "Repair the document or exclude it from the content index.",
                exception);
        }
    }
}
