using System.Text.RegularExpressions;
using AIEverything.Content.Errors;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AIEverything.Content.Extraction;

public sealed class PdfTextExtractor : ITextExtractor
{
    private static readonly ContentOrderTextExtractor.Options TextOptions = new()
    {
        SeparateParagraphsWithDoubleNewline = true,
        ReplaceWhitespaceWithSpace = true,
        NegativeGapAsWhitespace = true
    };

    private static readonly Regex CamelCaseBoundary = new(
        @"(?<=[\p{Ll}\p{Nd}])(?=\p{Lu})|(?<=\p{Lu})(?=\p{Lu}\p{Ll})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = PdfDocument.Open(request.Path);
        var pages = new List<string>();
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageText = ContentOrderTextExtractor.GetText(page, TextOptions);
            if (!string.IsNullOrWhiteSpace(pageText))
            {
                pages.Add(CamelCaseBoundary.Replace(pageText, " "));
            }
        }

        var text = string.Join(Environment.NewLine, pages).Trim();
        if (text.Length == 0)
        {
            throw new ContentIndexException(
                ContentErrorCodes.OcrRequired,
                $"PDF contains no extractable text: {request.Path}",
                "Use OCR outside AIEverything or add a text-based PDF.");
        }

        return Task.FromResult(ExtractionResultFactory.Create(text, request.MaxChars));
    }
}
