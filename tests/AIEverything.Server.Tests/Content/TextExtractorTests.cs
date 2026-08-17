using System.Text;
using AIEverything.Content.Errors;
using AIEverything.Content.Extraction;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AIEverything.Server.Tests.Content;

public sealed class TextExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        $"aieverything-extract-{Guid.NewGuid():N}");
    private readonly ITextExtractor _extractor = CompositeTextExtractor.CreateDefault();

    public TextExtractorTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("utf8.txt", "hello 中文", "utf8")]
    [InlineData("utf8bom.md", "bom text", "utf8bom")]
    [InlineData("utf16le.txt", "little endian", "utf16le")]
    [InlineData("utf16be.txt", "big endian", "utf16be")]
    public async Task Extracts_supported_text_encodings(string name, string expected, string encoding)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, Encode(expected, encoding));

        var result = await _extractor.ExtractAsync(new ExtractionRequest(path), CancellationToken.None);

        Assert.Equal(expected, result.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Rejects_invalid_utf8_without_guessing_a_local_code_page()
    {
        var path = Path.Combine(_root, "invalid.txt");
        await File.WriteAllBytesAsync(path, [0xC3, 0x28, 0xFF]);

        var exception = await Assert.ThrowsAsync<ContentIndexException>(() =>
            _extractor.ExtractAsync(new ExtractionRequest(path), CancellationToken.None));

        Assert.Equal(ContentErrorCodes.UnsupportedEncoding, exception.Code);
    }

    [Fact]
    public async Task Applies_file_size_and_character_caps()
    {
        var path = Path.Combine(_root, "large.txt");
        await File.WriteAllTextAsync(path, "1234567890", Encoding.UTF8);

        var tooLarge = await Assert.ThrowsAsync<ContentIndexException>(() =>
            _extractor.ExtractAsync(new ExtractionRequest(path, MaxBytes: 4), CancellationToken.None));
        var truncated = await _extractor.ExtractAsync(
            new ExtractionRequest(path, MaxChars: 5), CancellationToken.None);

        Assert.Equal(ContentErrorCodes.FileTooLarge, tooLarge.Code);
        Assert.Equal("12345", truncated.Text);
        Assert.True(truncated.Truncated);
    }

    [Fact]
    public async Task Extracts_docx_xlsx_and_pptx_text()
    {
        var docx = CreateDocx("Word正文");
        var xlsx = CreateXlsx("Excel内容");
        var pptx = CreatePptx("PPT标题");

        var word = await _extractor.ExtractAsync(new ExtractionRequest(docx), CancellationToken.None);
        var excel = await _extractor.ExtractAsync(new ExtractionRequest(xlsx), CancellationToken.None);
        var powerPoint = await _extractor.ExtractAsync(new ExtractionRequest(pptx), CancellationToken.None);

        Assert.Contains("Word正文", word.Text, StringComparison.Ordinal);
        Assert.Contains("Excel内容", excel.Text, StringComparison.Ordinal);
        Assert.Contains("PPT标题", powerPoint.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extracts_text_pdf_and_marks_empty_pdf_for_ocr()
    {
        var textPdf = CreatePdf("Hello PDF");
        var emptyPdf = CreatePdf(string.Empty, "empty.pdf");

        var result = await _extractor.ExtractAsync(new ExtractionRequest(textPdf), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<ContentIndexException>(() =>
            _extractor.ExtractAsync(new ExtractionRequest(emptyPdf), CancellationToken.None));

        Assert.Contains("Hello PDF", result.Text, StringComparison.Ordinal);
        Assert.Equal(ContentErrorCodes.OcrRequired, exception.Code);
    }

    [Fact]
    public async Task Extracts_positioned_pdf_words_with_readable_spaces()
    {
        var pdf = CreatePositionedWordsPdf();

        var result = await _extractor.ExtractAsync(
            new ExtractionRequest(pdf), CancellationToken.None);

        Assert.Contains("Efficient Memory Management", result.Text, StringComparison.Ordinal);
        Assert.Contains("Paged Attention", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("EfficientMemoryManagement", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PagedAttention", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Maps_corrupt_supported_documents_and_unknown_extensions()
    {
        var corrupt = Path.Combine(_root, "corrupt.docx");
        var unknown = Path.Combine(_root, "archive.zip");
        await File.WriteAllTextAsync(corrupt, "not a package");
        await File.WriteAllTextAsync(unknown, "not supported");

        var corruptException = await Assert.ThrowsAsync<ContentIndexException>(() =>
            _extractor.ExtractAsync(new ExtractionRequest(corrupt), CancellationToken.None));
        var unknownException = await Assert.ThrowsAsync<ContentIndexException>(() =>
            _extractor.ExtractAsync(new ExtractionRequest(unknown), CancellationToken.None));

        Assert.Equal(ContentErrorCodes.ExtractionFailed, corruptException.Code);
        Assert.Equal(ContentErrorCodes.UnsupportedFileType, unknownException.Code);
    }

    private string CreateDocx(string text)
    {
        var path = Path.Combine(_root, "sample.docx");
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text(text)))));
        main.Document.Save();
        return path;
    }

    private string CreateXlsx(string text)
    {
        var path = Path.Combine(_root, "sample.xlsx");
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData(
            new Row(new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(new S.Text(text)) })));
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Sheet1"
        });
        workbookPart.Workbook.Save();
        return path;
    }

    private string CreatePptx(string text)
    {
        var path = Path.Combine(_root, "sample.pptx");
        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentationPart = document.AddPresentationPart();
        presentationPart.Presentation = new Presentation(new SlideIdList());
        var slidePart = presentationPart.AddNewPart<SlidePart>();
        slidePart.Slide = new Slide(new CommonSlideData(new ShapeTree(
            new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()),
            new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties { Id = 2, Name = "Title" },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                new ShapeProperties(),
                new TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text(text))))))));
        slidePart.Slide.Save();
        var slideIds = presentationPart.Presentation.SlideIdList!;
        slideIds.Append(new SlideId { Id = 256, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
        presentationPart.Presentation.Save();
        return path;
    }

    private string CreatePdf(string text, string name = "sample.pdf") =>
        CreatePdfFromContentStream(
            $"BT /F1 12 Tf 72 720 Td ({text}) Tj ET",
            name);

    private string CreatePositionedWordsPdf() =>
        CreatePdfFromContentStream(
            "BT /F1 12 Tf " +
            "1 0 0 1 72 720 Tm (Efficient) Tj " +
            "1 0 0 1 128 720 Tm (Memory) Tj " +
            "1 0 0 1 185 720 Tm (Management) Tj " +
            "1 0 0 1 270 720 Tm (PagedAttention) Tj ET",
            "positioned-words.pdf");

    private string CreatePdfFromContentStream(string contentStream, string name)
    {
        var path = Path.Combine(_root, name);
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(contentStream) + 1} >>\nstream\n{contentStream}\nendstream"
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }
        builder.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        File.WriteAllText(path, builder.ToString(), Encoding.ASCII);
        return path;
    }

    private static byte[] Encode(string text, string encoding) => encoding switch
    {
        "utf8" => new UTF8Encoding(false).GetBytes(text),
        "utf8bom" => [.. Encoding.UTF8.Preamble, .. new UTF8Encoding(false).GetBytes(text)],
        "utf16le" => [.. Encoding.Unicode.Preamble, .. Encoding.Unicode.GetBytes(text)],
        "utf16be" => [.. Encoding.BigEndianUnicode.Preamble, .. Encoding.BigEndianUnicode.GetBytes(text)],
        _ => throw new ArgumentOutOfRangeException(nameof(encoding))
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
