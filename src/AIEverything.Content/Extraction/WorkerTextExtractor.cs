using System.Diagnostics;
using System.Text.Json;
using AIEverything.Content.Errors;

namespace AIEverything.Content.Extraction;

public sealed class WorkerTextExtractor : ITextExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _workerPath;
    private readonly TimeSpan _timeout;

    public WorkerTextExtractor(string workerPath, TimeSpan timeout)
    {
        _workerPath = Path.GetFullPath(workerPath);
        _timeout = timeout > TimeSpan.Zero
            ? timeout
            : throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_workerPath))
        {
            throw new ContentIndexException(
                ContentErrorCodes.ExtractionFailed,
                $"Extractor worker is missing: {_workerPath}",
                "Rebuild or reinstall AIEverything.");
        }

        using var process = new Process { StartInfo = CreateStartInfo(request) };
        if (!process.Start())
        {
            throw new ContentIndexException(
                ContentErrorCodes.ExtractionFailed,
                "Extractor worker did not start.",
                "Rebuild or reinstall AIEverything.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new ContentIndexException(
                ContentErrorCodes.ExtractionTimeout,
                $"Document extraction exceeded {_timeout.TotalSeconds:0.###} seconds: {request.Path}",
                "Exclude the file or retry after repairing it.");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        WorkerExtractionResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<WorkerExtractionResponse>(stdout, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw InvalidResponse(stderr, exception);
        }

        if (response?.Success == true && response.Result is not null)
        {
            return response.Result;
        }

        if (response?.Error is { } error)
        {
            throw new ContentIndexException(
                error.Code,
                error.Message,
                error.CorrectiveAction);
        }

        throw InvalidResponse(stderr);
    }

    private ProcessStartInfo CreateStartInfo(ExtractionRequest request)
    {
        var isDll = Path.GetExtension(_workerPath).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo(isDll ? "dotnet" : _workerPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (isDll)
        {
            startInfo.ArgumentList.Add(_workerPath);
        }

        startInfo.ArgumentList.Add("extract");
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(request.Path);
        startInfo.ArgumentList.Add("--max-bytes");
        startInfo.ArgumentList.Add(request.MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--max-chars");
        startInfo.ArgumentList.Add(request.MaxChars.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return startInfo;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static ContentIndexException InvalidResponse(
        string stderr,
        Exception? innerException = null) => new(
            ContentErrorCodes.ExtractionFailed,
            $"Extractor worker returned an invalid response. {stderr}".Trim(),
            "Rebuild AIEverything and retry.",
            innerException);
}
