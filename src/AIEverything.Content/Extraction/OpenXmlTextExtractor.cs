using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using D = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AIEverything.Content.Extraction;

public sealed class OpenXmlTextExtractor : ITextExtractor
{
    public Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(request.Path).ToLowerInvariant();
        var text = extension switch
        {
            ".docx" => ExtractWord(request.Path),
            ".xlsx" => ExtractSpreadsheet(request.Path),
            ".pptx" => ExtractPresentation(request.Path),
            _ => throw new InvalidOperationException($"Unsupported Open XML extension: {extension}")
        };
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ExtractionResultFactory.Create(text, request.MaxChars));
    }

    private static string ExtractWord(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var values = document.MainDocumentPart?.Document?
            .Descendants<W.Text>()
            .Select(node => node.Text) ?? [];
        return string.Join(Environment.NewLine, values);
    }

    private static string ExtractSpreadsheet(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var sharedStrings = document.WorkbookPart?.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(item => item.InnerText)
            .ToArray() ?? [];
        var values = new List<string>();
        foreach (var cell in document.WorkbookPart?.WorksheetParts
                     .SelectMany(part => part.Worksheet?.Descendants<Cell>() ?? []) ?? [])
        {
            if (cell.DataType?.Value == CellValues.SharedString &&
                int.TryParse(cell.CellValue?.Text, out var index) &&
                index >= 0 && index < sharedStrings.Length)
            {
                values.Add(sharedStrings[index]);
            }
            else if (cell.InlineString is not null)
            {
                values.Add(cell.InlineString.InnerText);
            }
            else if (cell.CellValue?.Text is { Length: > 0 } value)
            {
                values.Add(value);
            }
        }

        return string.Join(Environment.NewLine, values);
    }

    private static string ExtractPresentation(string path)
    {
        using var document = PresentationDocument.Open(path, false);
        var values = document.PresentationPart?.SlideParts
            .SelectMany(part => part.Slide?.Descendants<D.Text>() ?? [])
            .Select(node => node.Text) ?? [];
        return string.Join(Environment.NewLine, values);
    }
}
