using System.Text;
using AIEverything.Content.Errors;

namespace AIEverything.Content.Extraction;

public sealed class PlainTextExtractor : ITextExtractor
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, true, true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(true, true, true);

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(request.Path, cancellationToken);
        try
        {
            string text;
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            {
                text = StrictUtf8.GetString(bytes.AsSpan(Encoding.UTF8.Preamble.Length));
            }
            else if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
            {
                text = StrictUtf16Le.GetString(bytes.AsSpan(Encoding.Unicode.Preamble.Length));
            }
            else if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
            {
                text = StrictUtf16Be.GetString(bytes.AsSpan(Encoding.BigEndianUnicode.Preamble.Length));
            }
            else
            {
                text = StrictUtf8.GetString(bytes);
            }

            return ExtractionResultFactory.Create(text, request.MaxChars);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ContentIndexException(
                ContentErrorCodes.UnsupportedEncoding,
                $"Text encoding is not supported: {request.Path}",
                "Convert the file to UTF-8 or UTF-16 and retry.",
                exception);
        }
    }
}
