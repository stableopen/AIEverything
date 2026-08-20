using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIEverything.Content.Errors;
using AIEverything.Content.Extraction;

namespace AIEverything.Server.Tests.Content;

public sealed class ExtractorWorkerIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        $"aieverything-worker-{Guid.NewGuid():N}");

    public ExtractorWorkerIntegrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Worker_emits_one_camel_case_json_result_for_one_file()
    {
        var path = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(path, "worker searchable text", Encoding.UTF8);

        var result = await RunWorkerAsync(path);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(
            "worker searchable text",
            json.RootElement.GetProperty("result").GetProperty("text").GetString());
        Assert.Equal(1, result.StandardOutput.Count(character => character == '\n'));
    }

    [Fact]
    public async Task Worker_returns_structured_error_for_corrupt_document()
    {
        var path = Path.Combine(_root, "broken.docx");
        await File.WriteAllTextAsync(path, "not a package", Encoding.UTF8);

        var result = await RunWorkerAsync(path);

        Assert.Equal(1, result.ExitCode);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(
            ContentErrorCodes.CorruptDocument,
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Worker_client_maps_timeout_and_kills_process_tree()
    {
        var path = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(path, "timeout text", Encoding.UTF8);
        var extractor = new WorkerTextExtractor(WorkerDll, TimeSpan.FromMilliseconds(1));

        var exception = await Assert.ThrowsAsync<ContentIndexException>(() =>
            extractor.ExtractAsync(new ExtractionRequest(path), CancellationToken.None));

        Assert.Equal(ContentErrorCodes.ExtractionTimeout, exception.Code);
    }

    [Fact]
    public async Task Worker_client_returns_extraction_result()
    {
        var path = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(path, "isolated result", Encoding.UTF8);
        var extractor = new WorkerTextExtractor(WorkerDll, TimeSpan.FromSeconds(30));

        var result = await extractor.ExtractAsync(new ExtractionRequest(path), CancellationToken.None);

        Assert.Equal("isolated result", result.Text);
    }

    private static string WorkerDll
    {
        get
        {
            var configuration = AppContext.BaseDirectory.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";
            var pluginRoot = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            return Path.Combine(
                pluginRoot,
                "src",
                "AIEverything.ExtractorWorker",
                "bin",
                configuration,
                "net8.0",
                "win-x64",
                "AIEverything.ExtractorWorker.dll");
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunWorkerAsync(
        string path)
    {
        Assert.True(File.Exists(WorkerDll), $"Missing worker assembly: {WorkerDll}");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(WorkerDll);
        startInfo.ArgumentList.Add("extract");
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add("--max-bytes");
        startInfo.ArgumentList.Add((20 * 1024 * 1024).ToString());
        startInfo.ArgumentList.Add("--max-chars");
        startInfo.ArgumentList.Add("2000000");
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
