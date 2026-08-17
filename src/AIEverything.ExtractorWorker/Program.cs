using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AIEverything.Content.Errors;
using AIEverything.Content.Extraction;

Console.OutputEncoding = new UTF8Encoding(false);
TrySetBelowNormalPriority();
return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    try
    {
        var request = Parse(arguments);
        var result = await CompositeTextExtractor.CreateDefault()
            .ExtractAsync(request, CancellationToken.None);
        Write(new WorkerExtractionResponse(true, result, null));
        return 0;
    }
    catch (ArgumentException exception)
    {
        Write(new WorkerExtractionResponse(
            false,
            null,
            new WorkerExtractionError(
                ContentErrorCodes.InvalidArguments,
                exception.Message,
                "Use extract --path <absolute-path> --max-bytes <positive> --max-chars <positive>.")));
        return 2;
    }
    catch (ContentIndexException exception)
    {
        Write(new WorkerExtractionResponse(
            false,
            null,
            new WorkerExtractionError(
                exception.Code,
                exception.Message,
                exception.CorrectiveAction)));
        return 1;
    }
    catch (Exception exception)
    {
        Write(new WorkerExtractionResponse(
            false,
            null,
            new WorkerExtractionError(
                ContentErrorCodes.ExtractionFailed,
                exception.Message,
                "Repair or exclude the document and retry.")));
        return 1;
    }
}

static ExtractionRequest Parse(string[] arguments)
{
    if (arguments.Length != 7 || !arguments[0].Equals("extract", StringComparison.Ordinal))
    {
        throw new ArgumentException("Invalid extractor worker arguments.");
    }

    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index < arguments.Length; index += 2)
    {
        if (!arguments[index].StartsWith("--", StringComparison.Ordinal) ||
            values.ContainsKey(arguments[index]))
        {
            throw new ArgumentException("Extractor options must be unique --name value pairs.");
        }

        values[arguments[index]] = arguments[index + 1];
    }

    if (!values.TryGetValue("--path", out var path) || !Path.IsPathFullyQualified(path) ||
        !values.TryGetValue("--max-bytes", out var maxBytesText) ||
        !long.TryParse(maxBytesText, NumberStyles.None, CultureInfo.InvariantCulture, out var maxBytes) ||
        maxBytes < 1 ||
        !values.TryGetValue("--max-chars", out var maxCharsText) ||
        !int.TryParse(maxCharsText, NumberStyles.None, CultureInfo.InvariantCulture, out var maxChars) ||
        maxChars < 1)
    {
        throw new ArgumentException("Extractor path and limits are invalid.");
    }

    return new ExtractionRequest(path, maxBytes, maxChars);
}

static void Write(WorkerExtractionResponse response) =>
    Console.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    }));

static void TrySetBelowNormalPriority()
{
    try
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
    }
    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
    }
}
