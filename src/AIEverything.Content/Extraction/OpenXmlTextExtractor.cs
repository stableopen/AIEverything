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
        if (extension == ".docx")
        {
            var blocks = ExtractWord(request.Path);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ExtractionResultFactory.Create(blocks, request.MaxChars));
        }

        var text = extension switch
        {
            ".xlsx" => ExtractSpreadsheet(request.Path),
            ".pptx" => ExtractPresentation(request.Path),
            _ => throw new InvalidOperationException($"Unsupported Open XML extension: {extension}")
        };
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ExtractionResultFactory.Create(text, request.MaxChars));
    }

    private static IReadOnlyList<ExtractedTextBlock> ExtractWord(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return [];
        }

        var blocks = new List<ExtractedTextBlock>();
        var headingStack = new string?[9];
        var ordinal = 0;
        var paragraphNumber = 0;
        var tableNumber = 0;
        foreach (var element in body.ChildElements)
        {
            if (element is W.Paragraph paragraph)
            {
                var text = ParagraphText(paragraph);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var headingLevel = ResolveHeadingLevel(document.MainDocumentPart!, paragraph);
                if (headingLevel is int level)
                {
                    headingStack[level - 1] = text;
                    Array.Clear(headingStack, level, headingStack.Length - level);
                    var headingPath = CurrentHeadingPath(headingStack);
                    blocks.Add(new ExtractedTextBlock(++ordinal, text, $"{headingPath} · 标题 {level}", headingPath));
                    continue;
                }

                paragraphNumber++;
                var currentHeading = CurrentHeadingPath(headingStack);
                var label = currentHeading is null
                    ? $"第 {paragraphNumber} 段"
                    : $"{currentHeading} · 第 {paragraphNumber} 段";
                blocks.Add(new ExtractedTextBlock(++ordinal, text, label, currentHeading));
            }
            else if (element is W.Table table)
            {
                tableNumber++;
                var rowNumber = 0;
                foreach (var row in table.Elements<W.TableRow>())
                {
                    rowNumber++;
                    var cellNumber = 0;
                    foreach (var cell in row.Elements<W.TableCell>())
                    {
                        cellNumber++;
                        var text = string.Join(Environment.NewLine, cell.Elements<W.Paragraph>()
                            .Select(ParagraphText)
                            .Where(value => !string.IsNullOrWhiteSpace(value)));
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            continue;
                        }

                        var currentHeading = CurrentHeadingPath(headingStack);
                        var coordinates = $"表格 {tableNumber} · 第 {rowNumber} 行 · 第 {cellNumber} 列";
                        var label = currentHeading is null ? coordinates : $"{currentHeading} · {coordinates}";
                        blocks.Add(new ExtractedTextBlock(++ordinal, text, label, currentHeading));
                    }
                }
            }
        }

        return blocks;
    }

    private static string ParagraphText(W.Paragraph paragraph) =>
        string.Concat(paragraph.Descendants<W.Text>().Select(node => node.Text));

    private static int? ResolveHeadingLevel(
        MainDocumentPart mainPart,
        W.Paragraph paragraph)
    {
        var direct = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
        if (direct is not null && direct.Value < 9)
        {
            return direct.Value + 1;
        }

        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(styleId) && visited.Add(styleId))
        {
            if (styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(styleId["Heading".Length..], out var namedLevel) &&
                namedLevel is >= 1 and <= 9)
            {
                return namedLevel;
            }

            var style = mainPart.StyleDefinitionsPart?.Styles?
                .Elements<W.Style>()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.StyleId?.Value, styleId, StringComparison.OrdinalIgnoreCase));
            var outline = style?.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
            if (outline is not null && outline.Value < 9)
            {
                return outline.Value + 1;
            }

            styleId = style?.BasedOn?.Val?.Value;
        }

        return null;
    }

    private static string? CurrentHeadingPath(IEnumerable<string?> headings)
    {
        var values = headings.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return values.Length == 0 ? null : string.Join(" > ", values);
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
